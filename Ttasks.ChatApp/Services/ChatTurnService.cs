using System.Text.Json;
using Microsoft.Extensions.Options;
using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;

namespace Ttasks.ChatApp.Services;

public sealed class ChatTurnService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string CompleteResultGuidance =
        """
        Complete-result strategy:
        - If the user asks for all results, a total count, or a complete summary, do not infer completeness from one bounded page.
        - Use the tool documentation to find count, --all, paging, cursor, continuation-token, offset, skip, or next-page support.
        - Fetch only the fields needed for discovery/counting first, such as stable ids or keys.
        - Page until the tool returns an empty page, a final short page, or another documented end condition.
        - De-duplicate across pages by stable id/key before counting.
        - Report an exact total only when the graph has proven complete coverage; otherwise say the result is bounded or incomplete.
        """;
    private readonly ILlmProvider _provider;
    private readonly GraphPlanValidator _validator;
    private readonly GraphPlanBuilder _builder;
    private readonly ChatSessionRegistry _sessions;
    private readonly ITaskStore _store;
    private readonly ICapabilityProvider _capabilities;
    private readonly ITaskLibrary _library;
    private readonly TaskLibraryTemplateRenderer _renderer;
    private readonly ChatAppOptions _options;

    private const string CandidateDefinitionMetadataKey = "candidateDefinition";

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
        var attempt = RunGraphAttempts(executor, llmSession, activeSessionId, userMessage, route.PlanIntent ?? userMessage, capabilities);
        return new ChatResponse("graph", attempt.Answer, activeSessionId, attempt.GraphId, attempt.Tasks);
    }

    private GraphAttemptOutcome RunGraphAttempts(TaskExecutor planningExecutor, LlmAgentSession llmSession, string sessionId, string userMessage, string planIntent, CapabilitySet baseCapabilities)
    {
        GraphAttemptOutcome? lastAttempt = null;
        var repairContext = string.Empty;
        var maxAttempts = Math.Max(1, _options.MaxGraphRepairAttempts + 1);

        for (var attemptNumber = 1; attemptNumber <= maxAttempts; attemptNumber++)
        {
            var capabilities = baseCapabilities.Tools.Count > 0
                ? MaterializeToolCapabilities(planningExecutor, baseCapabilities, planIntent, repairContext)
                : baseCapabilities;
            if (capabilities.Capabilities.Count == 0)
                return new GraphAttemptOutcome(capabilities.EmptyMessage, null, []);

            try
            {
                var prompt = string.IsNullOrWhiteSpace(repairContext)
                    ? CreatePlannerPrompt(planIntent, capabilities)
                    : CreateRepairPlannerPrompt(userMessage, planIntent, capabilities, repairContext);
                var planJson = planningExecutor.Execute(CoreTask.Prompt(prompt)).Output;
                var plan = ParseJson<GraphPlan>(planJson);
                _validator.Validate(plan, capabilities);

                var graphExecutor = CreatePromptExecutor(llmSession, includeUpstreamResults: true, _store);
                RegisterExecutableCapabilities(graphExecutor, capabilities);
                var graph = _builder.Build(plan, capabilities);
                graph.Run(graphExecutor, maxWorkers: _options.MaxWorkers);

                lastAttempt = ToOutcome(graph);
                if (graph.Ok)
                {
                    PromoteUsedCandidateCapabilities(plan, capabilities);
                    return lastAttempt;
                }

                repairContext = CreateFailureObservation(attemptNumber, planJson, plan, capabilities, lastAttempt.Tasks);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                var tasks = lastAttempt?.Tasks ?? [];
                repairContext = CreateFailureObservation(attemptNumber, ex, tasks);
                lastAttempt = new GraphAttemptOutcome(
                    $"Attempt {attemptNumber} failed before graph execution: {ex.Message}",
                    null,
                    tasks);
            }
        }

        return lastAttempt is null
            ? new GraphAttemptOutcome("The graph did not produce a final answer.", null, [])
            : lastAttempt with
            {
                Answer = string.IsNullOrWhiteSpace(lastAttempt.Answer)
                    ? DescribeGraphFailure(lastAttempt.Tasks)
                    : lastAttempt.Answer
            };
    }

    private static GraphAttemptOutcome ToOutcome(TaskGraph graph)
    {
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

        return new GraphAttemptOutcome(answer ?? string.Empty, graph.Id, tasks);
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

    private static void RegisterExecutableCapabilities(TaskExecutor executor, CapabilitySet capabilities)
    {
        var allowedPayloads = capabilities.Capabilities
            .Where(capability => capability.TaskType == TaskType.Powershell)
            .Select(capability => capability.Payload)
            .ToHashSet(StringComparer.Ordinal);
        var allowedProcessPayloads = capabilities.Capabilities
            .Where(capability => capability.TaskType == TaskType.Process)
            .Select(capability => capability.Payload)
            .ToHashSet(StringComparer.Ordinal);

        executor.Register(TaskType.Powershell, context =>
        {
            if (!allowedPayloads.Contains(context.Payload))
                throw new InvalidOperationException("Only host-approved capability payloads are allowed in this experiment.");

            return TaskExecutor.WithBuiltInHandlers().Execute(CoreTask.Powershell(context.Payload, timeout: context.Timeout)).Raw;
        });
        executor.Register(TaskType.Process, context =>
        {
            if (!allowedProcessPayloads.Contains(context.Payload))
                throw new InvalidOperationException("Only host-approved capability payloads are allowed in this experiment.");

            return TaskExecutor.WithBuiltInHandlers().Execute(CoreTask.Process(
                ProcessCommand.FromJson(context.Payload),
                context.Title,
                context.Description,
                context.Timeout,
                context.Task.Metadata)).Raw;
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

        {{CompleteResultGuidance}}

        Capability catalog:
        {{FormatCapabilityCatalog(capabilities)}}
        """;

    private CapabilitySet MaterializeToolCapabilities(TaskExecutor executor, CapabilitySet capabilities, string planIntent, string repairContext)
    {
        var proposalJson = executor.Execute(CoreTask.Prompt(CreateToolAuthoringPrompt(planIntent, capabilities.Tools, repairContext))).Output;
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
            TaskType.Process,
            proposal.PayloadTemplate ?? string.Empty,
            proposal.Parameters ?? [],
            metadata,
            proposal.FileName,
            proposal.ArgsTemplate ?? []);
        var item = CreateCandidateItem(definition);
        metadata = new Dictionary<string, object?>(item.Metadata, StringComparer.Ordinal)
        {
            ["candidateTaskLibraryKey"] = item.Key,
            [CandidateDefinitionMetadataKey] = definition
        };

        return new CommandCapability(
            "pending",
            item.DisplayName,
            item.Description,
            item.TaskType,
            item.TaskType == TaskType.Process
                ? _renderer.RenderProcess(item).ToJson()
                : _renderer.Render(item),
            metadata);
    }

    private TaskLibraryItem CreateCandidateItem(TaskLibraryDefinition definition)
    {
        var metadata = new Dictionary<string, object?>(definition.Metadata, StringComparer.Ordinal)
        {
            [StoreBackedTaskLibrary.LibraryKeyKey] = definition.Key,
            [StoreBackedTaskLibrary.TemplateParametersKey] = definition.Parameters.Select(parameter => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["name"] = parameter.Name,
                ["source"] = parameter.Source,
                ["format"] = parameter.Format,
                ["defaultValue"] = parameter.DefaultValue
            }).ToList(),
            [StoreBackedTaskLibrary.ProcessFileNameKey] = definition.ProcessFileName,
            [StoreBackedTaskLibrary.ProcessArgsTemplateKey] = definition.ProcessArgsTemplate?.ToList(),
            ["candidateTaskLibraryItem"] = true
        };

        return new TaskLibraryItem(
            $"candidate-{Guid.NewGuid():N}",
            definition.Key,
            definition.DisplayName,
            definition.Description,
            definition.TaskType,
            definition.PayloadTemplate,
            definition.Parameters,
            metadata,
            DateTimeOffset.UtcNow,
            definition.ProcessFileName,
            definition.ProcessArgsTemplate);
    }

    private static void ValidateToolProposal(ToolTaskProposal proposal, ToolCapability tool)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.DisplayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(proposal.FileName);
        if (proposal.ArgsTemplate is null)
            throw new InvalidOperationException($"Tool task proposal '{proposal.Key}' must include argsTemplate.");
        if (!string.Equals(proposal.Type, "process", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Tool task proposal '{proposal.Key}' must use process.");
        if (!string.Equals(tool.Policy, "full", StringComparison.Ordinal))
            throw new InvalidOperationException($"Tool capability '{tool.Kind}' does not allow LLM-authored templates.");
        if (!string.Equals(proposal.FileName, tool.ToolName, StringComparison.Ordinal))
            throw new InvalidOperationException($"Tool task proposal '{proposal.Key}' must use the '{tool.ToolName}' tool.");
    }

    private string CreateToolAuthoringPrompt(string planIntent, IReadOnlyList<ToolCapability> tools, string repairContext) =>
        $$"""
        Create task-library templates for the user's requested tool work.

        Intent:
        {{planIntent}}

        {{FormatRepairContextForPrompt(repairContext)}}

        Return JSON only. Do not wrap it in Markdown.

        Schema:
        {
          "tasks": [
            {
              "key": "stable.semantic.key",
              "displayName": "short user-facing name",
              "description": "what this task does",
              "type": "process",
              "fileName": "tool executable name",
              "argsTemplate": ["arg1", "arg2 with {token}"],
              "toolCapabilityKind": "matching tool capability kind",
              "parameters": [
                { "name": "today", "source": "clock.now" },
                { "name": "tomorrow", "source": "clock.tomorrow" },
                { "name": "top", "source": "default", "defaultValue": 100 }
              ],
              "metadata": { "purpose": "search" }
            }
          ]
        }

        Rules:
        - Create the minimum task templates needed for this intent.
        - For CLI tools, create process templates with fileName and argsTemplate. Do not create powershell templates for tool calls.
        - fileName must equal the listed toolName for the selected tool capability.
        - argsTemplate must contain one array item per process argument.
        - Use the tool documentation below to choose commands and flags.
        - You may use template tokens such as {today:yyyy-MM-dd}, {yesterday:yyyy-MM-dd}, {tomorrow:yyyy-MM-dd}, and default parameters when useful.
        - Supported parameter sources are clock.now, clock.yesterday, clock.tomorrow, default, and metadata:<key>.
        - For full-tool capabilities, the approved tool is the boundary. Use any documented command, flag, or query shape for that tool that is needed to satisfy the user's request.
        - Use stable keys because accepted templates are saved in the task library for reuse.

        {{CompleteResultGuidance}}

        Tool capabilities:
        {{FormatToolCatalog(tools)}}
        """;

    private string CreateRepairPlannerPrompt(string userMessage, string planIntent, CapabilitySet capabilities, string repairContext) =>
        $$"""
        Revise the ttasks-net graph plan after a failed attempt.

        Original user request:
        {{userMessage}}

        Intent:
        {{planIntent}}

        Failure observations:
        {{repairContext}}

        Return JSON only. Do not wrap it in Markdown.

        Use the same schema and rules as the normal planner:
        - Non-prompt tasks must reference one capabilityId from the catalog below.
        - Do not include payload on non-prompt tasks.
        - Do not invent, substitute, or add capability ids.
        - Prefer changing only what is needed to recover from the failure.
        - Add one final prompt task that summarizes upstream read outputs.
        - The final prompt must depend on all read tasks.

        {{CompleteResultGuidance}}

        Capability catalog:
        {{FormatCapabilityCatalog(capabilities)}}
        """;

    private static string FormatRepairContextForPrompt(string repairContext) =>
        string.IsNullOrWhiteSpace(repairContext)
            ? string.Empty
            : "Previous attempt failed. Use these observations to choose a corrected tool strategy before proposing templates:\n" + repairContext;

    private void PromoteUsedCandidateCapabilities(GraphPlan plan, CapabilitySet capabilities)
    {
        var usedCapabilityIds = plan.Tasks
            .Select(task => task.CapabilityId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var capability in capabilities.Capabilities.Where(capability => usedCapabilityIds.Contains(capability.Id)))
        {
            if (capability.Metadata.TryGetValue(CandidateDefinitionMetadataKey, out var raw) && raw is TaskLibraryDefinition definition)
                _library.GetOrAdd(definition);
        }
    }

    private static string CreateFailureObservation(int attemptNumber, string planJson, GraphPlan plan, CapabilitySet capabilities, IReadOnlyList<ChatTaskSummary> tasks) =>
        JsonSerializer.Serialize(
            new
            {
                attempt = attemptNumber,
                previousPlan = planJson,
                selectedCapabilities = plan.Tasks
                    .Where(task => !string.IsNullOrWhiteSpace(task.CapabilityId))
                    .Select(task => new
                    {
                        task.Id,
                        task.Type,
                        task.CapabilityId,
                        capability = capabilities.ById.TryGetValue(task.CapabilityId!, out var capability)
                            ? new
                            {
                                capability.DisplayName,
                                taskType = capability.TaskType == TaskType.Powershell ? "powershell" : capability.TaskType.ToString().ToLowerInvariant(),
                                payload = capability.Payload,
                                metadata = capability.Metadata
                            }
                            : null
                    }),
                failedTasks = tasks
                    .Where(task => task.Status is "Failed" or "Blocked" or "Cancelled")
                    .Select(task => new
                    {
                        task.Id,
                        task.Type,
                        task.Status,
                        task.Error,
                        task.BlockedBy,
                        Output = Truncate(task.Output, 2000)
                    })
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    private static string CreateFailureObservation(int attemptNumber, Exception exception, IReadOnlyList<ChatTaskSummary> tasks) =>
        JsonSerializer.Serialize(
            new
            {
                attempt = attemptNumber,
                failure = new
                {
                    type = exception.GetType().Name,
                    message = exception.Message
                },
                tasks
            },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "...";

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

    private sealed record GraphAttemptOutcome(
        string Answer,
        string? GraphId,
        IReadOnlyList<ChatTaskSummary> Tasks);
}
