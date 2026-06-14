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
    private readonly IGraphLibrary _graphLibrary;
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
        IGraphLibrary graphLibrary,
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
        _graphLibrary = graphLibrary;
        _renderer = renderer;
        _options = options.Value;
    }

    public ChatResponse Handle(string? sessionId, string userMessage)
    {
        var activeSessionId = ChatSessionRegistry.ResolveSessionId(sessionId);
        var turnId = Guid.NewGuid().ToString("N");

        var capabilities = _capabilities.GetCapabilities(new CapabilityRequest(activeSessionId, userMessage));
        var llmSession = _sessions.GetOrCreate(activeSessionId, capabilities.AllowedTools);
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

        if (capabilities.AllowedTools.Count == 0)
            return new ChatResponse("answer", capabilities.EmptyMessage, activeSessionId);

        var turnState = RunTurn(executor, llmSession, activeSessionId, turnId, userMessage, route.PlanIntent ?? userMessage, capabilities);
        return new ChatResponse("graph", turnState.Answer, activeSessionId, turnState.LastGraphId, turnState.Tasks);
    }

    public PlannedTurnResult RunPlannedTurn(string? sessionId, string? turnId, string intent)
    {
        if (string.IsNullOrWhiteSpace(intent))
            throw new ArgumentException("Intent is required.", nameof(intent));

        var activeSessionId = ChatSessionRegistry.ResolveSessionId(sessionId);
        var activeTurnId = string.IsNullOrWhiteSpace(turnId) ? Guid.NewGuid().ToString("N") : turnId!;

        var capabilities = _capabilities.GetCapabilities(new CapabilityRequest(activeSessionId, intent));
        if (capabilities.AllowedTools.Count == 0)
        {
            return new PlannedTurnResult(
                Answer: capabilities.EmptyMessage,
                SessionId: activeSessionId,
                TurnId: activeTurnId,
                GraphId: null,
                Tasks: Array.Empty<ChatTaskSummary>(),
                Succeeded: false);
        }

        var llmSession = _sessions.GetOrCreate(activeSessionId, capabilities.AllowedTools);
        var planningExecutor = CreatePromptExecutor(llmSession, includeUpstreamResults: false, _store);
        var turnState = RunTurn(planningExecutor, llmSession, activeSessionId, activeTurnId, intent, intent, capabilities);
        // Treat the turn as "ran" when at least one graph executed (even if some
        // tasks failed). Planner-only failure leaves LastGraphId null.
        var succeeded = turnState.LastGraphId is not null;
        return new PlannedTurnResult(
            turnState.Answer,
            activeSessionId,
            activeTurnId,
            turnState.LastGraphId,
            turnState.Tasks,
            succeeded);
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
        var authoredPlan = initialPlan;
        var plan = ResolveGraphLibraryReference(initialPlan);
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
                    PromoteLibrarySuggestions(plan, graph, capabilities);
                    PromoteGraphSuggestion(authoredPlan, graph);
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
                authoredPlan = CarryForwardSuggestion(repaired, authoredPlan);
                plan = ResolveGraphLibraryReference(authoredPlan);
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
                    out var repairError2);
                if (repaired is null)
                    return new BatchOutcome(false, $"Repair attempt {attempt + 1} failed to parse: {repairError2}", lastGraphId, allTasks);
                authoredPlan = CarryForwardSuggestion(repaired, authoredPlan);
                plan = ResolveGraphLibraryReference(authoredPlan);
            }
        }

        return new BatchOutcome(false, "Batch exhausted repair attempts without success.", lastGraphId, allTasks);
    }

    private static GraphPlan CarryForwardSuggestion(GraphPlan repaired, GraphPlan previousAuthored)
    {
        if (repaired.GraphSuggestion is not null)
            return repaired;
        if (previousAuthored.GraphSuggestion is null)
            return repaired;
        return repaired with { GraphSuggestion = previousAuthored.GraphSuggestion };
    }

    private GraphPlan ResolveGraphLibraryReference(GraphPlan plan)
    {
        if (!string.IsNullOrWhiteSpace(plan.GraphLibraryKey))
        {
            var item = _graphLibrary.All().FirstOrDefault(candidate => string.Equals(candidate.Key, plan.GraphLibraryKey, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"Plan references unknown graph library item '{plan.GraphLibraryKey}'.");

            var rendered = RenderGraphTemplate(item, plan.GraphParameters);
            return plan with
            {
                Graph = rendered.Graph,
                Tasks = rendered.Tasks,
                Edges = rendered.Edges
            };
        }

        // Authored plan: if the planner supplied graphParameters (typically
        // alongside a graphSuggestion that declares them), substitute those
        // values into the authored plan for THIS execution. The original
        // authoredPlan keeps its placeholder tokens so promotion can save the
        // template with placeholders intact.
        if (plan.GraphParameters is { Count: > 0 } overrides)
        {
            var declared = (plan.GraphSuggestion?.Parameters as IReadOnlyList<TemplateParameter>)
                ?? overrides.Keys.Select(k => new TemplateParameter(k, "user")).ToList();
            var renderedTasks = plan.Tasks.Select(t => RenderPlanTask(t, declared, overrides)).ToList();
            return plan with { Tasks = renderedTasks };
        }

        return plan;
    }

    private GraphPlan RenderGraphTemplate(GraphLibraryItem item, IReadOnlyDictionary<string, object?>? overrides)
    {
        var renderedTasks = item.PlanTemplate.Tasks
            .Select(task => RenderPlanTask(task, item.Parameters, overrides))
            .ToList();
        return item.PlanTemplate with
        {
            Tasks = renderedTasks,
            Edges = item.PlanTemplate.Edges.ToList(),
            GraphLibraryKey = null,
            GraphParameters = null,
            GraphSuggestion = null
        };
    }

    private GraphPlanTask RenderPlanTask(GraphPlanTask task, IReadOnlyList<TemplateParameter> parameters, IReadOnlyDictionary<string, object?>? overrides)
    {
        var rendered = task with
        {
            Title = _renderer.RenderText(task.Title ?? string.Empty, parameters, overrides),
            Description = _renderer.RenderText(task.Description ?? string.Empty, parameters, overrides),
            Prompt = task.Prompt is null ? null : _renderer.RenderText(task.Prompt, parameters, overrides)
        };
        if (string.IsNullOrEmpty(task.Title)) rendered = rendered with { Title = null };
        if (string.IsNullOrEmpty(task.Description)) rendered = rendered with { Description = null };

        if (task.Process is { } process)
        {
            var renderedArgs = process.Args.Select(arg => _renderer.RenderText(arg, parameters, overrides)).ToList();
            rendered = rendered with
            {
                Process = new ProcessSpec(_renderer.RenderText(process.FileName, parameters, overrides), renderedArgs)
            };
        }

        if (task.LibraryParameters is { } libraryParams)
        {
            var renderedLibParams = libraryParams.ToDictionary(
                kvp => kvp.Key,
                kvp => (object?)(kvp.Value is string s ? _renderer.RenderText(s, parameters, overrides) : kvp.Value),
                StringComparer.Ordinal);
            rendered = rendered with { LibraryParameters = renderedLibParams };
        }

        return rendered;
    }

    private void PromoteGraphSuggestion(GraphPlan authoredPlan, TaskGraph graph)
    {
        if (authoredPlan.GraphSuggestion is null)
            return;
        if (!string.IsNullOrWhiteSpace(authoredPlan.GraphLibraryKey))
            return;
        if (authoredPlan.Tasks.Count == 0)
            return;
        if (!graph.Ok)
            return;

        var s = authoredPlan.GraphSuggestion;
        var template = new GraphPlan(
            authoredPlan.Graph,
            authoredPlan.Tasks.ToList(),
            authoredPlan.Edges.ToList());

        _graphLibrary.GetOrAdd(new GraphLibraryDefinition(
            s.Key,
            s.DisplayName,
            s.Description,
            template,
            s.Parameters ?? [],
            s.Metadata));
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

    private TaskExecutor CreatePromptExecutor(LlmAgentSession session, bool includeUpstreamResults, ITaskStore? store = null)
    {
        var executor = new TaskExecutor(store);
        executor.Register(TaskType.Prompt, session.PromptHandler(new LlmHandlerOptions
        {
            IncludeUpstreamResults = includeUpstreamResults,
            Timeout = _options.LlmTimeoutSeconds > 0
                ? TimeSpan.FromSeconds(_options.LlmTimeoutSeconds)
                : null
        }));
        return executor;
    }

    private static void RegisterProcessHandler(TaskExecutor executor)
    {
        executor.Register(TaskType.Process, context =>
        {
            var upstreamOutputs = context.UpstreamTasks
                .Where(task => task.Metadata.TryGetValue("planTaskId", out var raw) && raw is string)
                .ToDictionary(
                    task => (string)task.Metadata["planTaskId"]!,
                    task => task.Result?.Output ?? string.Empty,
                    StringComparer.Ordinal);

            var command = ProcessCommand.FromJson(context.Payload);
            var resolvedArgs = OutputReferenceResolver.ResolveAll(command.Args, upstreamOutputs);
            var resolved = new ProcessCommand(command.FileName, resolvedArgs);

            return TaskExecutor.WithBuiltInHandlers().Execute(CoreTask.Process(
                resolved,
                context.Title,
                context.Description,
                context.Timeout,
                context.Task.Metadata)).Raw;
        });
    }

    private void PromoteLibrarySuggestions(GraphPlan plan, TaskGraph graph, CapabilitySet capabilities)
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
            var metadata = EnrichMetadataWithTraits(suggestion, capabilities);
            _library.GetOrAdd(new TaskLibraryDefinition(
                suggestion.Key,
                suggestion.DisplayName,
                suggestion.Description,
                suggestion.FileName,
                suggestion.ArgsTemplate.ToList(),
                (suggestion.Parameters ?? []).ToList(),
                metadata));
        }
    }

    private static IReadOnlyDictionary<string, object?>? EnrichMetadataWithTraits(
        LibrarySuggestion suggestion,
        CapabilitySet capabilities)
    {
        var existing = suggestion.Metadata;
        var hasAuthored = existing is not null
            && existing.TryGetValue("traits", out var authoredRaw)
            && authoredRaw is not null;

        if (hasAuthored)
        {
            var copy = new Dictionary<string, object?>(existing!, StringComparer.Ordinal);
            copy["traits.source"] = "authored";
            return copy;
        }

        var inherited = InheritTraitsFor(suggestion, capabilities);
        if (inherited.Count == 0)
            return existing;

        var merged = existing is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(existing, StringComparer.Ordinal);
        merged["traits"] = inherited;
        merged["traits.source"] = "inherited";
        return merged;
    }

    private static IReadOnlyList<string> InheritTraitsFor(
        LibrarySuggestion suggestion,
        CapabilitySet capabilities)
    {
        if (capabilities.AllowedTools.Count == 0)
            return [];

        var head = ComputeSuggestionHead(suggestion);
        if (string.IsNullOrEmpty(head))
            return [];

        var matches = capabilities.AllowedTools
            .Where(tool => HeadStartsWithPrefix(head, tool.Prefix))
            .ToList();
        if (matches.Count == 0)
            return [];

        var union = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in matches)
        {
            if (tool.Traits is null)
                continue;
            foreach (var trait in tool.Traits)
            {
                if (seen.Add(trait))
                    union.Add(trait);
            }
        }
        return union;
    }

    private static string ComputeSuggestionHead(LibrarySuggestion suggestion)
    {
        var leadingArgs = suggestion.ArgsTemplate
            .TakeWhile(arg => !arg.StartsWith("-", StringComparison.Ordinal)
                && !arg.StartsWith("{", StringComparison.Ordinal));
        return string.Join(' ', new[] { suggestion.FileName }.Concat(leadingArgs));
    }

    private static bool HeadStartsWithPrefix(string head, string prefix)
    {
        prefix = prefix.Trim();
        if (string.IsNullOrEmpty(prefix))
            return false;
        if (string.Equals(head, prefix, StringComparison.Ordinal))
            return true;
        return head.StartsWith(prefix + " ", StringComparison.Ordinal);
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
