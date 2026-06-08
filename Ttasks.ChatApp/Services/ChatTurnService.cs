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
        if (capabilities.AllowedTools.Count == 0)
            return new ChatResponse("answer", capabilities.EmptyMessage, activeSessionId);

        var attempt = RunGraphAttempts(executor, llmSession, userMessage, route.PlanIntent ?? userMessage, capabilities);
        return new ChatResponse("graph", attempt.Answer, activeSessionId, attempt.GraphId, attempt.Tasks);
    }

    private GraphAttemptOutcome RunGraphAttempts(
        TaskExecutor planningExecutor,
        LlmAgentSession llmSession,
        string userMessage,
        string planIntent,
        CapabilitySet capabilities)
    {
        GraphAttemptOutcome? lastAttempt = null;
        var repairContext = string.Empty;
        var maxAttempts = Math.Max(1, _options.MaxGraphRepairAttempts + 1);

        for (var attemptNumber = 1; attemptNumber <= maxAttempts; attemptNumber++)
        {
            try
            {
                var prompt = string.IsNullOrWhiteSpace(repairContext)
                    ? CreatePlannerPrompt(planIntent, capabilities)
                    : CreateRepairPlannerPrompt(userMessage, planIntent, capabilities, repairContext);
                var planJson = planningExecutor.Execute(CoreTask.Prompt(prompt)).Output;
                var plan = ParseJson<GraphPlan>(planJson);
                ResolveLibraryReferences(plan, capabilities);
                _validator.Validate(plan, capabilities);

                var graphExecutor = CreatePromptExecutor(llmSession, includeUpstreamResults: true, _store);
                RegisterProcessHandler(graphExecutor);
                var graph = _builder.Build(plan);
                graph.Run(graphExecutor, maxWorkers: _options.MaxWorkers);

                lastAttempt = ToOutcome(graph);
                if (graph.Ok)
                {
                    PromoteLibrarySuggestions(plan, graph);
                    return lastAttempt;
                }

                repairContext = CreateFailureObservation(attemptNumber, planJson, plan, lastAttempt.Tasks);
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

    private void ResolveLibraryReferences(GraphPlan plan, CapabilitySet capabilities)
    {
        var libraryByKey = capabilities.LibrarySuggestions.ToDictionary(item => item.Key, StringComparer.Ordinal);
        for (var i = 0; i < plan.Tasks.Count; i++)
        {
            var task = plan.Tasks[i];
            if (!string.Equals(task.Type, "process", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.IsNullOrWhiteSpace(task.LibraryItemKey))
                continue;
            if (task.Process is not null)
                continue;
            if (!libraryByKey.TryGetValue(task.LibraryItemKey, out var item))
                throw new InvalidOperationException($"Task '{task.Id}' references unknown libraryItemKey '{task.LibraryItemKey}'.");

            var command = _renderer.Render(item, task.LibraryParameters);
            plan.Tasks[i] = task with { Process = new ProcessSpec(command.FileName, command.Args.ToList()) };
        }
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

    private static void RegisterProcessHandler(TaskExecutor executor)
    {
        executor.Register(TaskType.Process, context =>
        {
            var command = ProcessCommand.FromJson(context.Payload);
            return TaskExecutor.WithBuiltInHandlers().Execute(CoreTask.Process(
                command,
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

        If the user can be answered directly without reading external systems, running tool commands, or doing external actions, return:
        {
          "mode": "answer",
          "answer": "your concise answer",
          "planIntent": null
        }

        If the user is asking to use external tools, fan out work, run commands, or summarize external outputs, return:
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
          "graph": { "title": "short title", "metadata": { "source": "chat-ui" } },
          "tasks": [
            {
              "id": "stable-id",
              "type": "process|prompt",
              "process": { "fileName": "tool", "args": ["arg1", "arg2"] },
              "prompt": "prompt text for prompt tasks only",
              "libraryItemKey": "optional key of a library template to render and use as process",
              "libraryParameters": { "optionalToken": "value" },
              "librarySuggestion": {
                "key": "stable.semantic.key",
                "displayName": "short name",
                "description": "what this does",
                "fileName": "tool",
                "argsTemplate": ["arg1", "arg2 with {token}"],
                "parameters": [ { "name": "token", "source": "clock.now|clock.yesterday|clock.tomorrow|default", "format": "yyyy-MM-dd", "defaultValue": null } ]
              },
              "title": "optional",
              "description": "optional",
              "timeout": {{_options.DefaultTimeoutSeconds}},
              "metadata": { "kind": "read" }
            }
          ],
          "edges": [ { "from": "dep-id", "to": "consumer-id" } ]
        }

        Rules:
        - Use only the allowed tools listed below. Each "process" task's fileName plus leading non-flag args must start with one of the allowed prefixes (e.g. "mail", "teams read", "az account list").
        - Prefer reusing a libraryItemKey from the library suggestions when one matches the request. The host will render that template and run it. Use libraryParameters to override values.
        - When authoring a new strategy you expect to reuse later, include librarySuggestion so the host can promote it to the library after the graph succeeds. The fileName must match process.fileName and the args template head must also satisfy the allowed prefixes.
        - For prompt tasks, set prompt text and do not include process/libraryItemKey/librarySuggestion.
        - Independent reads should fan out. Add one final prompt task that summarizes upstream outputs and depends on all reads.
        - Use stable semantic task ids.

        {{CompleteResultGuidance}}

        Allowed tools:
        {{FormatAllowedTools(capabilities.AllowedTools)}}

        Library suggestions for this turn:
        {{FormatLibrarySuggestions(capabilities.LibrarySuggestions)}}
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

        Use the same schema and rules as the normal planner. Prefer changing only what is needed to recover from the failure. Do not repeat a failed command shape without changing it.

        {{CompleteResultGuidance}}

        Allowed tools:
        {{FormatAllowedTools(capabilities.AllowedTools)}}

        Library suggestions for this turn:
        {{FormatLibrarySuggestions(capabilities.LibrarySuggestions)}}
        """;

    private void PromoteLibrarySuggestions(GraphPlan plan, TaskGraph graph)
    {
        var memberByPlanId = graph.Members
            .Where(task => task.Metadata.TryGetValue("planTaskId", out var raw) && raw is string)
            .ToDictionary(
                task => (string)task.Metadata["planTaskId"]!,
                task => task,
                StringComparer.Ordinal);

        foreach (var planTask in plan.Tasks)
        {
            if (planTask.LibrarySuggestion is null)
                continue;
            if (!memberByPlanId.TryGetValue(planTask.Id, out var coreTask))
                continue;
            if (coreTask.Status != Ttasks.Core.TaskStatus.Succeeded)
                continue;

            var suggestion = planTask.LibrarySuggestion;
            _library.GetOrAdd(new TaskLibraryDefinition(
                suggestion.Key,
                suggestion.DisplayName,
                suggestion.Description,
                suggestion.FileName,
                suggestion.ArgsTemplate.ToList(),
                (suggestion.Parameters ?? []).ToList(),
                suggestion.Metadata));
        }
    }

    private static string CreateFailureObservation(int attemptNumber, string planJson, GraphPlan plan, IReadOnlyList<ChatTaskSummary> tasks) =>
        JsonSerializer.Serialize(
            new
            {
                attempt = attemptNumber,
                previousPlan = planJson,
                processTasks = plan.Tasks
                    .Where(task => string.Equals(task.Type, "process", StringComparison.OrdinalIgnoreCase))
                    .Select(task => new
                    {
                        task.Id,
                        process = task.Process,
                        libraryItemKey = task.LibraryItemKey
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

    private static string FormatAllowedTools(IReadOnlyList<AllowedTool> tools) =>
        JsonSerializer.Serialize(
            tools.Select(tool => new { prefix = tool.Prefix, description = tool.Description, helpCommand = tool.HelpCommand }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    private static string FormatLibrarySuggestions(IReadOnlyList<TaskLibraryItem> items) =>
        JsonSerializer.Serialize(
            items.Select(item => new
            {
                key = item.Key,
                displayName = item.DisplayName,
                description = item.Description,
                fileName = item.FileName,
                argsTemplate = item.ArgsTemplate,
                parameters = item.Parameters.Select(p => new
                {
                    name = p.Name,
                    source = p.Source,
                    format = p.Format,
                    defaultValue = p.DefaultValue
                })
            }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    private sealed record GraphAttemptOutcome(
        string Answer,
        string? GraphId,
        IReadOnlyList<ChatTaskSummary> Tasks);
}
