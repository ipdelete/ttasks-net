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
    private readonly ChatAppOptions _options;

    public ChatTurnService(
        ILlmProvider provider,
        GraphPlanValidator validator,
        GraphPlanBuilder builder,
        ChatSessionRegistry sessions,
        ITaskStore store,
        ICapabilityProvider capabilities,
        IOptions<ChatAppOptions> options)
    {
        _provider = provider;
        _validator = validator;
        _builder = builder;
        _sessions = sessions;
        _store = store;
        _capabilities = capabilities;
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
}
