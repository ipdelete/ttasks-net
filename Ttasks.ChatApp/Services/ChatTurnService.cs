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
    internal const string BatchKey = "batch";
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

        var turnState = RunTurn(executor, llmSession, activeSessionId, turnId, userMessage, route.PlanIntent ?? userMessage, capabilities);
        return new ChatResponse("graph", turnState.Answer, activeSessionId, turnState.LastGraphId, turnState.Tasks);
    }

    private TurnState RunTurn(
        TaskExecutor planningExecutor,
        LlmAgentSession llmSession,
        string sessionId,
        string turnId,
        string userMessage,
        string planIntent,
        CapabilitySet capabilities)
    {
        var batchHistory = new List<BatchSummary>();
        var allTasks = new List<ChatTaskSummary>();
        string? lastGraphId = null;
        string? finalAnswer = null;
        var maxBatches = Math.Max(1, _options.MaxContinuationBatches + 1);

        for (var batchNumber = 1; batchNumber <= maxBatches; batchNumber++)
        {
            GraphPlan? plan;
            if (batchNumber == 1)
            {
                plan = TryGetPlan(
                    planningExecutor,
                    () => Prompts.Planner(planIntent, capabilities, _options.DefaultTimeoutSeconds),
                    title: "Plan graph",
                    turnId,
                    sessionId,
                    "planner",
                    batchNumber,
                    attempt: 1,
                    out var planError);
                if (plan is null)
                {
                    finalAnswer = $"Attempt 1 failed before graph execution: {planError}";
                    break;
                }
            }
            else
            {
                var continuationDecision = AskContinuation(planningExecutor, userMessage, planIntent, capabilities, batchHistory, turnId, sessionId, batchNumber);
                if (continuationDecision.Mode.Equals("answer", StringComparison.OrdinalIgnoreCase))
                {
                    finalAnswer = continuationDecision.Answer ?? string.Empty;
                    break;
                }
                if (!continuationDecision.Mode.Equals("graph", StringComparison.OrdinalIgnoreCase) || continuationDecision.Plan is null)
                {
                    finalAnswer = $"Continuation step {batchNumber} returned an unrecognized response.";
                    break;
                }
                plan = continuationDecision.Plan;
            }

            var batchOutcome = ExecuteBatchWithRepair(planningExecutor, llmSession, plan, capabilities, turnId, sessionId, batchNumber);
            allTasks.AddRange(batchOutcome.Tasks);
            if (batchOutcome.GraphId is not null)
                lastGraphId = batchOutcome.GraphId;
            batchHistory.Add(new BatchSummary(batchNumber, batchOutcome.Tasks, batchOutcome.Ok));

            if (!batchOutcome.Ok)
            {
                finalAnswer = string.IsNullOrWhiteSpace(batchOutcome.Answer)
                    ? DescribeGraphFailure(batchOutcome.Tasks)
                    : batchOutcome.Answer;
                break;
            }

            if (batchNumber == maxBatches)
            {
                finalAnswer = string.IsNullOrWhiteSpace(batchOutcome.Answer)
                    ? "Reached the continuation budget without a final answer."
                    : batchOutcome.Answer;
                break;
            }
        }

        return new TurnState(finalAnswer ?? "The graph did not produce a final answer.", lastGraphId, allTasks);
    }

    private ContinuationDecision AskContinuation(
        TaskExecutor executor,
        string userMessage,
        string planIntent,
        CapabilitySet capabilities,
        IReadOnlyList<BatchSummary> batchHistory,
        string turnId,
        string sessionId,
        int batchNumber)
    {
        var continuationTask = CoreTask.Prompt(
            Prompts.Continuation(userMessage, planIntent, capabilities, FormatBatchHistory(batchHistory)),
            title: $"Continuation decision for batch {batchNumber}",
            metadata: TurnMetadataWithBatch(turnId, sessionId, "continuation", batchNumber, attempt: 1));
        var json = executor.Execute(continuationTask).Output;
        return ParseJson<ContinuationDecision>(json);
    }

    private GraphPlan? TryGetPlan(
        TaskExecutor executor,
        Func<string> promptFactory,
        string title,
        string turnId,
        string sessionId,
        string kind,
        int batchNumber,
        int attempt,
        out string? error)
    {
        try
        {
            var task = CoreTask.Prompt(
                promptFactory(),
                title: title,
                metadata: TurnMetadataWithBatch(turnId, sessionId, kind, batchNumber, attempt));
            var json = executor.Execute(task).Output;
            var plan = ParseJson<GraphPlan>(json);
            error = null;
            return plan;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            error = ex.Message;
            return null;
        }
    }

    private BatchOutcome ExecuteBatchWithRepair(
        TaskExecutor planningExecutor,
        LlmAgentSession llmSession,
        GraphPlan initialPlan,
        CapabilitySet capabilities,
        string turnId,
        string sessionId,
        int batchNumber)
    {
        var allTasks = new List<ChatTaskSummary>();
        string? lastGraphId = null;
        var plan = initialPlan;
        var repairContext = string.Empty;
        var maxAttempts = Math.Max(1, _options.MaxGraphRepairAttempts + 1);

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                ResolveLibraryReferences(plan, capabilities);
                _validator.Validate(plan, capabilities);

                var graphExecutor = CreatePromptExecutor(llmSession, includeUpstreamResults: true, _store);
                RegisterProcessHandler(graphExecutor);
                var graph = _builder.Build(plan);
                TagGraphTasks(graph, turnId, sessionId, batchNumber, attempt);
                graph.Run(graphExecutor, maxWorkers: _options.MaxWorkers);

                var snapshot = ToOutcome(graph);
                allTasks.AddRange(snapshot.Tasks);
                lastGraphId = graph.Id;

                if (graph.Ok)
                {
                    PromoteLibrarySuggestions(plan, graph);
                    return new BatchOutcome(true, snapshot.Answer, lastGraphId, allTasks);
                }

                repairContext = CreateFailureObservation(attempt, plan, snapshot.Tasks);
                if (attempt == maxAttempts)
                    return new BatchOutcome(false, snapshot.Answer, lastGraphId, allTasks);

                var repaired = TryGetPlan(
                    planningExecutor,
                    () => Prompts.Repair(string.Empty, string.Empty, capabilities, repairContext),
                    title: $"Repair plan attempt {attempt + 1}",
                    turnId,
                    sessionId,
                    "repair",
                    batchNumber,
                    attempt + 1,
                    out var repairError);
                if (repaired is null)
                    return new BatchOutcome(false, $"Repair attempt {attempt + 1} failed to parse: {repairError}", lastGraphId, allTasks);
                plan = repaired;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                if (attempt == maxAttempts)
                    return new BatchOutcome(false, $"Attempt {attempt} failed before graph execution: {ex.Message}", lastGraphId, allTasks);

                repairContext = CreateFailureObservation(attempt, ex);
                var repaired = TryGetPlan(
                    planningExecutor,
                    () => Prompts.Repair(string.Empty, string.Empty, capabilities, repairContext),
                    title: $"Repair plan attempt {attempt + 1}",
                    turnId,
                    sessionId,
                    "repair",
                    batchNumber,
                    attempt + 1,
                    out var repairError);
                if (repaired is null)
                    return new BatchOutcome(false, $"Repair attempt {attempt + 1} failed to parse: {repairError}", lastGraphId, allTasks);
                plan = repaired;
            }
        }

        return new BatchOutcome(false, "Batch exhausted repair attempts without success.", lastGraphId, allTasks);
    }

    private static void TagGraphTasks(TaskGraph graph, string turnId, string sessionId, int batchNumber, int attemptNumber)
    {
        foreach (var task in graph.Members)
        {
            task.SetMetadata(TurnIdKey, turnId);
            task.SetMetadata(SessionIdKey, sessionId);
            task.SetMetadata(AttemptKey, attemptNumber);
            task.SetMetadata(BatchKey, batchNumber);
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

    private static IReadOnlyDictionary<string, object?> TurnMetadataWithBatch(string turnId, string sessionId, string kind, int batch, int attempt)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [TurnIdKey] = turnId,
            [SessionIdKey] = sessionId,
            [TaskKindKey] = kind,
            [AttemptKey] = attempt,
            [BatchKey] = batch
        };
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

    private static GraphSnapshot ToOutcome(TaskGraph graph)
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

        return new GraphSnapshot(answer ?? string.Empty, tasks);
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

    private static string FormatBatchHistory(IReadOnlyList<BatchSummary> batches) =>
        JsonSerializer.Serialize(
            batches.Select(batch => new
            {
                batch = batch.BatchNumber,
                succeeded = batch.Succeeded,
                tasks = batch.Tasks.Select(task => new
                {
                    task.Id,
                    task.Type,
                    task.Status,
                    task.Error,
                    task.BlockedBy,
                    Output = Truncate(task.Output, 4000)
                })
            }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });

    private static string CreateFailureObservation(int attemptNumber, GraphPlan plan, IReadOnlyList<ChatTaskSummary> tasks) =>
        JsonSerializer.Serialize(
            new
            {
                attempt = attemptNumber,
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

    private static string CreateFailureObservation(int attemptNumber, Exception exception) =>
        JsonSerializer.Serialize(
            new
            {
                attempt = attemptNumber,
                failure = new
                {
                    type = exception.GetType().Name,
                    message = exception.Message
                }
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

    private sealed record TurnState(string Answer, string? LastGraphId, IReadOnlyList<ChatTaskSummary> Tasks);
    private sealed record GraphSnapshot(string Answer, IReadOnlyList<ChatTaskSummary> Tasks);
    private sealed record BatchOutcome(bool Ok, string Answer, string? GraphId, IReadOnlyList<ChatTaskSummary> Tasks);
    private sealed record BatchSummary(int BatchNumber, IReadOnlyList<ChatTaskSummary> Tasks, bool Succeeded);
}
