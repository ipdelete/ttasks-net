using System.Text.Json;
using Microsoft.Extensions.Options;
using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;

namespace Ttasks.ChatApp.Services;

public sealed class ChatTurnService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    internal const string TurnIdKey = "turnId";
    internal const string TaskKindKey = "kind";
    internal const string AttemptKey = "attempt";
    internal const string SessionIdKey = "sessionId";

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
        var turnId = Guid.NewGuid().ToString("N");

        var executor = CreatePromptExecutor(llmSession, includeUpstreamResults: false, _store);
        var routerTask = CoreTask.Prompt(
            Prompts.Router(userMessage),
            title: "Route chat turn",
            metadata: TurnMetadata(turnId, activeSessionId, "router"));
        var routeJson = executor.Execute(routerTask).Output;
        var route = ParseJson<RouteDecision>(routeJson);

        if (string.Equals(route.Mode, "answer", StringComparison.OrdinalIgnoreCase))
            return new ChatResponse("answer", route.Answer ?? string.Empty, activeSessionId);

        if (!string.Equals(route.Mode, "graph", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Unknown route mode '{route.Mode}'.");

        var capabilities = _capabilities.GetCapabilities(new CapabilityRequest(activeSessionId, userMessage));
        if (capabilities.AllowedTools.Count == 0)
            return new ChatResponse("answer", capabilities.EmptyMessage, activeSessionId);

        var attempt = RunGraphAttempts(executor, llmSession, activeSessionId, turnId, userMessage, route.PlanIntent ?? userMessage, capabilities);
        return new ChatResponse("graph", attempt.Answer, activeSessionId, attempt.GraphId, attempt.Tasks);
    }

    private GraphAttemptOutcome RunGraphAttempts(
        TaskExecutor planningExecutor,
        LlmAgentSession llmSession,
        string sessionId,
        string turnId,
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
                var isRepair = !string.IsNullOrWhiteSpace(repairContext);
                var prompt = isRepair
                    ? Prompts.Repair(userMessage, planIntent, capabilities, repairContext)
                    : Prompts.Planner(planIntent, capabilities, _options.DefaultTimeoutSeconds);
                var plannerTask = CoreTask.Prompt(
                    prompt,
                    title: isRepair ? $"Repair plan attempt {attemptNumber}" : "Plan graph",
                    metadata: TurnMetadata(turnId, sessionId, isRepair ? "repair" : "planner", attemptNumber));
                var planJson = planningExecutor.Execute(plannerTask).Output;
                var plan = ParseJson<GraphPlan>(planJson);
                ResolveLibraryReferences(plan, capabilities);
                _validator.Validate(plan, capabilities);

                var graphExecutor = CreatePromptExecutor(llmSession, includeUpstreamResults: true, _store);
                RegisterProcessHandler(graphExecutor);
                var graph = _builder.Build(plan);
                TagGraphTasks(graph, turnId, sessionId, attemptNumber);
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

    private static void TagGraphTasks(TaskGraph graph, string turnId, string sessionId, int attemptNumber)
    {
        foreach (var task in graph.Members)
        {
            task.SetMetadata(TurnIdKey, turnId);
            task.SetMetadata(SessionIdKey, sessionId);
            task.SetMetadata(AttemptKey, attemptNumber);
            if (!task.Metadata.ContainsKey(TaskKindKey))
                task.SetMetadata(TaskKindKey, task.Type == TaskType.Prompt ? "summary" : "process");
        }
    }

    private static IReadOnlyDictionary<string, object?> TurnMetadata(string turnId, string sessionId, string kind, int? attempt = null)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [TurnIdKey] = turnId,
            [SessionIdKey] = sessionId,
            [TaskKindKey] = kind
        };
        if (attempt is not null)
            values[AttemptKey] = attempt.Value;
        return values;
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

    private sealed record GraphAttemptOutcome(
        string Answer,
        string? GraphId,
        IReadOnlyList<ChatTaskSummary> Tasks);
}
