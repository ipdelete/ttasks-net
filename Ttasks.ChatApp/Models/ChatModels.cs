namespace Ttasks.ChatApp.Services;

public sealed record ChatRequest(string Message, string? SessionId = null);

public sealed record ChatResponse(
    string Mode,
    string Answer,
    string SessionId,
    string? GraphId = null,
    IReadOnlyList<ChatTaskSummary>? Tasks = null);

public sealed record ChatTaskSummary(
    string Id,
    string Type,
    string Status,
    string Output,
    string? Error = null,
    string? BlockedBy = null);

public sealed record PlannedTurnResult(
    string Answer,
    string SessionId,
    string TurnId,
    string? GraphId,
    IReadOnlyList<ChatTaskSummary> Tasks,
    bool Succeeded);

public sealed record RouteDecision(
    string Mode,
    string? Answer,
    string? PlanIntent);

public sealed record ContinuationDecision(
    string Mode,
    string? Answer,
    GraphPlan? Plan);

public sealed record GraphPlan(
    GraphPlanInfo Graph,
    List<GraphPlanTask> Tasks,
    List<GraphPlanEdge> Edges,
    string? GraphLibraryKey = null,
    IReadOnlyDictionary<string, object?>? GraphParameters = null,
    GraphLibrarySuggestion? GraphSuggestion = null);

public sealed record GraphLibrarySuggestion(
    string Key,
    string DisplayName,
    string Description,
    IReadOnlyList<TemplateParameter>? Parameters = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record GraphPlanInfo(
    string Title,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record GraphPlanTask(
    string Id,
    string Type,
    string? Prompt = null,
    ProcessSpec? Process = null,
    LibrarySuggestion? LibrarySuggestion = null,
    string? LibraryItemKey = null,
    IReadOnlyDictionary<string, object?>? LibraryParameters = null,
    string? Title = null,
    string? Description = null,
    int? Timeout = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record ProcessSpec(string FileName, IReadOnlyList<string> Args);

public sealed record LibrarySuggestion(
    string Key,
    string DisplayName,
    string Description,
    string FileName,
    IReadOnlyList<string> ArgsTemplate,
    IReadOnlyList<TemplateParameter>? Parameters = null,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record GraphPlanEdge(string From, string To);

public sealed record AdminTaskLibraryItem(
    string Id,
    string Key,
    string DisplayName,
    string Description,
    string FileName,
    IReadOnlyList<string> ArgsTemplate,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed record AdminAllowedTool(
    string Prefix,
    string? Description,
    string? HelpCommand,
    IReadOnlyList<string>? Traits = null);

public sealed record AdminGraphLibraryItem(
    string Id,
    string Key,
    string DisplayName,
    string Description,
    GraphPlan PlanTemplate,
    IReadOnlyList<TemplateParameter> Parameters,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed record AdminTurnSummary(
    string TurnId,
    string? SessionId,
    DateTimeOffset CreatedAt,
    int TaskCount,
    int RouterCount,
    int PlannerCount,
    int RepairCount,
    int ProcessCount,
    int SummaryCount,
    int Failed,
    int Blocked,
    int Cancelled,
    int Succeeded,
    string Status);

public sealed record AdminTurnDetail(
    string TurnId,
    string? SessionId,
    DateTimeOffset CreatedAt,
    string Status,
    IReadOnlyList<AdminTurnTask> Tasks);

public sealed record AdminTurnTask(
    string Id,
    string Kind,
    string Type,
    string Status,
    DateTimeOffset CreatedAt,
    string Title,
    string? Error,
    string? BlockedBy,
    int? Attempt);

public sealed record AdminGraphSummary(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    string Status,
    int TaskCount,
    int Succeeded,
    int Failed,
    int Cancelled,
    int Blocked);

public sealed record AdminGraphDetail(
    string Id,
    string Title,
    DateTimeOffset CreatedAt,
    string Status,
    IReadOnlyDictionary<string, object?> Metadata,
    IReadOnlyList<AdminTaskSummary> Tasks,
    IReadOnlyList<AdminGraphEdge> Edges);

public sealed record AdminTaskSummary(
    string Id,
    string Type,
    string Title,
    string Status,
    DateTimeOffset CreatedAt,
    string? Error,
    string? BlockedBy,
    bool HasOutput);

public sealed record AdminTaskDetail(
    string Id,
    string Type,
    string Title,
    string Description,
    string Payload,
    int? Timeout,
    string Status,
    DateTimeOffset CreatedAt,
    string? Error,
    string? BlockedBy,
    IReadOnlyDictionary<string, object?> Metadata,
    AdminTaskResult? Result);

public sealed record AdminTaskResult(
    string Status,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    double DurationSeconds,
    string Output,
    string? Error,
    int? ReturnCode,
    string? TerminationReason);

public sealed record AdminGraphEdge(string From, string To);
