using System.Text.Json;
using Microsoft.Extensions.Options;
using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;

namespace Ttasks.ChatApp.Services;

public sealed class ChatTurnService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ILlmProvider _provider;
    private readonly GraphPlanValidator _validator;
    private readonly GraphPlanBuilder _builder;
    private readonly ChatSessionRegistry _sessions;
    private readonly ITaskStore _store;
    private readonly ICapabilityProvider _capabilities;
    private readonly ITaskLibrary _library;
    private readonly TaskLibraryTemplateRenderer _renderer;
    private readonly ChatAppOptions _options;

    public ChatTurnService(
        ILlmProvider provider,
        GraphPlanValidator validator,
        GraphPlanBuilder builder,
        ChatSessionRegistry sessions,
        ITaskStore store,
        ICapabilityProvider capabilities,
        ITaskLibrary library,
        TaskLibraryTemplateRenderer renderer,
        IOptions<ChatAppOptions> options)
    {
        _provider = provider;
        _validator = validator;
        _builder = builder;
        _sessions = sessions;
        _store = store;
        _capabilities = capabilities;
        _library = library;
        _renderer = renderer;
        _options = options.Value;
    }

    public ChatResponse Handle(string? sessionId, string userMessage)
    {
        var (activeSessionId, llmSession) = _sessions.GetOrCreate(sessionId);

        var executor = CreatePromptExecutor(llmSession, includeUpstreamResults: false);
        var routeJson = executor.Execute(CoreTask.Prompt(CreateRouterPrompt(userMessage))).Output;
        var route = ParseJson<RouteDecision>(routeJson);

        if (string.Equals(route.Mode, "answer", StringComparison.OrdinalIgnoreCase))
            return new ChatResponse("answer", route.Answer ?? string.Empty, activeSessionId);

        if (!string.Equals(route.Mode, "graph", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown route mode '{route.Mode}'.");

        var capabilities = _capabilities.GetCapabilities(new CapabilityRequest(activeSessionId, userMessage));
        if (capabilities.Capabilities.Count == 0 && capabilities.Tools.Count == 0)
            return new ChatResponse("answer", capabilities.EmptyMessage, activeSessionId);
        if (capabilities.Tools.Count > 0)
            capabilities = MaterializeToolCapabilities(executor, capabilities, route.PlanIntent ?? userMessage);
        if (capabilities.Capabilities.Count == 0)
            return new ChatResponse("answer", capabilities.EmptyMessage, activeSessionId);

        var planJson = executor.Execute(CoreTask.Prompt(CreatePlannerPrompt(route.PlanIntent ?? userMessage, capabilities))).Output;
        var plan = ParseJson<GraphPlan>(planJson);
        _validator.Validate(plan, capabilities);

        var graphExecutor = CreatePromptExecutor(llmSession, includeUpstreamResults: true, _store);
        RegisterPowerShellCapabilities(graphExecutor, capabilities);
        var graph = _builder.Build(plan, capabilities);
        graph.Run(graphExecutor, maxWorkers: _options.MaxWorkers);

        var final = graph.Leaves().LastOrDefault(task => task.Type == TaskType.Prompt)
            ?? graph.Leaves().Last();
        var tasks = graph.Members
            .Select(task => new ChatTaskSummary(
                task.Id,
                task.TypeName,
                task.Status.ToString(),
                task.Result?.Output ?? string.Empty,
                task.Result?.Error ?? task.Error,
                task.BlockedBy))
            .ToList();

        var answer = final.Result?.Output;
        if (string.IsNullOrWhiteSpace(answer) && !graph.Ok)
            answer = DescribeGraphFailure(tasks);

        return new ChatResponse("graph", answer ?? string.Empty, activeSessionId, graph.Id, tasks);
    }

    private static TaskExecutor CreatePromptExecutor(LlmAgentSession session, bool includeUpstreamResults, ITaskStore? store = null)
    {
        var executor = new TaskExecutor(store);
        executor.Register(TaskType.Prompt, session.PromptHandler(new LlmHandlerOptions
        {
            IncludeUpstreamResults = includeUpstreamResults
        }));
        return executor;
    }

    private static void RegisterPowerShellCapabilities(TaskExecutor executor, CapabilitySet capabilities)
    {
        var allowedPayloads = capabilities.Capabilities
            .Where(capability => capability.TaskType == TaskType.Powershell)
            .Select(capability => capability.Payload)
            .ToHashSet(StringComparer.Ordinal);

        executor.Register(TaskType.Powershell, context =>
        {
            if (!allowedPayloads.Contains(context.Payload))
                throw new InvalidOperationException("Only host-approved capability payloads are allowed in this experiment.");

            return TaskExecutor.WithBuiltInHandlers().Execute(CoreTask.Powershell(context.Payload, timeout: context.Timeout)).Raw;
        });
    }

    private string CreateRouterPrompt(string userMessage) =>
        $$"""
        You are the router for a ttasks-net chat experiment.

        Return JSON only. Do not wrap it in Markdown.

        If the user can be answered directly without reading Teams, running shell commands, or doing external actions, return:
        {
          "mode": "answer",
          "answer": "your concise answer",
          "planIntent": null
        }

        If the user is asking to read Teams chats/channels, mail, calendar, Azure, fan out work, run commands, or summarize external outputs, return:
        {
          "mode": "graph",
          "answer": null,
          "planIntent": "short actionable intent"
        }

        User message:
        {{userMessage}}
        """;

    private string CreatePlannerPrompt(string planIntent, CapabilitySet capabilities) =>
        $$"""
        Create a ttasks-net graph plan for this intent:
        {{planIntent}}

        Return JSON only. Do not wrap it in Markdown.

        Schema:
        {
          "graph": {
            "title": "short title",
            "metadata": { "source": "chat-ui" }
          },
          "tasks": [
            {
              "id": "stable-id",
              "type": "powershell|prompt",
              "capabilityId": "capability id for non-prompt tasks",
              "payload": "prompt text for prompt tasks only",
              "title": "optional title",
              "description": "optional description",
              "timeout": {{_options.DefaultTimeoutSeconds}},
              "metadata": { "kind": "read" }
            }
          ],
          "edges": [
            { "from": "dependency-task-id", "to": "dependent-task-id" }
          ]
        }
        Rules:
        - Non-prompt tasks must reference one capabilityId from the catalog below.
        - Do not include payload on non-prompt tasks.
        - Do not invent, substitute, or add capability ids.
        - Create one read task for each relevant capability so independent reads fan out.
        - Add one final prompt task that summarizes upstream read outputs.
        - The final prompt must depend on all read tasks.
        - Use stable semantic task ids.

        Capability catalog:
        {{FormatCapabilityCatalog(capabilities)}}
        """;

    private CapabilitySet MaterializeToolCapabilities(TaskExecutor executor, CapabilitySet capabilities, string planIntent)
    {
        var proposalJson = executor.Execute(CoreTask.Prompt(CreateToolAuthoringPrompt(planIntent, capabilities.Tools))).Output;
        var proposals = ParseJson<ToolTaskProposalSet>(proposalJson);
        var toolByKind = capabilities.Tools.ToDictionary(tool => tool.Kind, StringComparer.Ordinal);
        var authored = proposals.Tasks
            .Select(proposal => ToCommandCapability(proposal, toolByKind))
            .ToList();
        var combined = capabilities.Capabilities.Concat(authored)
            .Select((capability, index) => Renumber(capability, index))
            .ToList();

        return new CapabilitySet(combined, capabilities.EmptyMessage, capabilities.Tools);
    }

    private CommandCapability ToCommandCapability(ToolTaskProposal proposal, IReadOnlyDictionary<string, ToolCapability> toolByKind)
    {
        var toolKind = string.IsNullOrWhiteSpace(proposal.ToolCapabilityKind)
            ? toolByKind.Keys.SingleOrDefault()
            : proposal.ToolCapabilityKind;
        if (string.IsNullOrWhiteSpace(toolKind) || !toolByKind.TryGetValue(toolKind, out var tool))
            throw new InvalidOperationException($"Tool task proposal '{proposal.Key}' references an unavailable tool capability.");

        ValidateToolProposal(proposal, tool);

        var metadata = new Dictionary<string, object?>(proposal.Metadata ?? new Dictionary<string, object?>(), StringComparer.Ordinal)
        {
            ["capabilityKind"] = tool.Kind,
            ["capabilityPolicy"] = tool.Policy,
            ["toolName"] = tool.ToolName,
            ["authoredFromToolCapability"] = true
        };
        var definition = new TaskLibraryDefinition(
            proposal.Key,
            proposal.DisplayName,
            proposal.Description,
            TaskType.Powershell,
            proposal.PayloadTemplate,
            proposal.Parameters ?? [],
            metadata);
        var item = _library.GetOrAdd(definition);
        metadata = new Dictionary<string, object?>(item.Metadata, StringComparer.Ordinal)
        {
            ["libraryTaskId"] = item.Id
        };

        return new CommandCapability(
            "pending",
            item.DisplayName,
            item.Description,
            item.TaskType,
            _renderer.Render(item),
            metadata);
    }

    private static void ValidateToolProposal(ToolTaskProposal proposal, ToolCapability tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.PayloadTemplate);
        if (!string.Equals(proposal.Type, "powershell", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Tool task proposal '{proposal.Key}' must use powershell.");
        if (!string.Equals(tool.Policy, "full", StringComparison.Ordinal))
            throw new InvalidOperationException($"Tool capability '{tool.Kind}' does not allow LLM-authored templates.");
        if (!IsSingleToolCommand(proposal.PayloadTemplate, tool.ToolName))
            throw new InvalidOperationException($"Tool task proposal '{proposal.Key}' must invoke only the '{tool.ToolName}' tool.");
    }

    private static bool IsSingleToolCommand(string payloadTemplate, string toolName)
    {
        var trimmed = payloadTemplate.Trim();
        if (!trimmed.Equals(toolName, StringComparison.Ordinal) && !trimmed.StartsWith(toolName + " ", StringComparison.Ordinal))
            return false;
        return !trimmed.Contains('\n')
            && !trimmed.Contains('\r')
            && !trimmed.Contains("&&", StringComparison.Ordinal)
            && !trimmed.Contains('|')
            && !trimmed.Contains(';');
    }

    private string CreateToolAuthoringPrompt(string planIntent, IReadOnlyList<ToolCapability> tools) =>
        $$"""
        Create task-library templates for the user's requested tool work.

        Intent:
        {{planIntent}}

        Return JSON only. Do not wrap it in Markdown.

        Schema:
        {
          "tasks": [
            {
              "key": "stable.semantic.key",
              "displayName": "short user-facing name",
              "description": "what this task does",
              "type": "powershell",
              "payloadTemplate": "single CLI command template",
              "toolCapabilityKind": "matching tool capability kind",
              "parameters": [
                { "name": "today", "source": "clock.now" },
                { "name": "tomorrow", "source": "clock.tomorrow" },
                { "name": "top", "source": "default", "defaultValue": 10 }
              ],
              "metadata": { "purpose": "search" }
            }
          ]
        }

        Rules:
        - Create the minimum task templates needed for this intent.
        - Payload templates must invoke exactly one listed tool and must not use pipes, command chaining, or shell metacharacters.
        - Use the tool documentation below to choose commands and flags.
        - You may use template tokens such as {today:yyyy-MM-dd}, {tomorrow:yyyy-MM-dd}, and default parameters when useful.
        - For date-based mail searches, prefer raw OData queries and JSON output.
        - Use stable keys because accepted templates are saved in the task library for reuse.

        Tool capabilities:
        {{FormatToolCatalog(tools)}}
        """;

    private static T ParseJson<T>(string text)
    {
        var json = ExtractJson(text);
        return JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Unable to parse {typeof(T).Name}.");
    }

    private static string ExtractJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstNewline = trimmed.IndexOf('\n');
            var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
            if (firstNewline >= 0 && lastFence > firstNewline)
                return trimmed[(firstNewline + 1)..lastFence].Trim();
        }

        return trimmed;
    }

    private static string DescribeGraphFailure(IReadOnlyList<ChatTaskSummary> tasks)
    {
        var failed = tasks.Where(task => task.Status is "Failed" or "Blocked" or "Cancelled").ToList();
        if (failed.Count == 0)
            return "The graph completed without producing a final answer.";

        var lines = failed.Select(task =>
        {
            var detail = task.Error ?? (task.BlockedBy is null ? null : $"blocked by {task.BlockedBy}");
            return detail is null
                ? $"- {task.Id} ({task.Type}) {task.Status}"
                : $"- {task.Id} ({task.Type}) {task.Status}: {detail}";
        });
        return "The graph did not produce a final answer because one or more tasks failed:\n" + string.Join("\n", lines);
    }

    private static string FormatCapabilityCatalog(CapabilitySet capabilities) =>
        JsonSerializer.Serialize(
            capabilities.Capabilities.Select(capability => new
            {
                id = capability.Id,
                taskType = capability.TaskType == TaskType.Powershell ? "powershell" : capability.TaskType.ToString().ToLowerInvariant(),
                displayName = capability.DisplayName,
                description = capability.Description
            }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    private static string FormatToolCatalog(IReadOnlyList<ToolCapability> tools) =>
        JsonSerializer.Serialize(
            tools.Select(tool => new
            {
                kind = tool.Kind,
                toolName = tool.ToolName,
                displayName = tool.DisplayName,
                description = tool.Description,
                policy = tool.Policy,
                documentation = tool.Documentation
            }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    private static CommandCapability Renumber(CommandCapability capability, int index)
    {
        var id = $"cap-{index + 1}";
        var metadata = new Dictionary<string, object?>(capability.Metadata, StringComparer.Ordinal)
        {
            ["capabilityId"] = id
        };

        return capability with { Id = id, Metadata = metadata };
    }
}
