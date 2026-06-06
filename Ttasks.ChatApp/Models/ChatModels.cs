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

public sealed record RouteDecision(
    string Mode,
    string? Answer,
    string? PlanIntent);

public sealed record GraphPlan(
    GraphPlanInfo Graph,
    IReadOnlyList<GraphPlanTask> Tasks,
    IReadOnlyList<GraphPlanEdge> Edges);

public sealed record GraphPlanInfo(
    string Title,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record GraphPlanTask(
    string Id,
    string Type,
    string? Payload = null,
    string? Title = null,
    string? Description = null,
    int? Timeout = null,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    string? CapabilityId = null);

public sealed record GraphPlanEdge(string From, string To);

public sealed record TeamsReadScope(IReadOnlySet<string> AllowedCommands);

public sealed record ToolTaskProposalSet(IReadOnlyList<ToolTaskProposal> Tasks);

public sealed record ToolTaskProposal(
    string Key,
    string DisplayName,
    string Description,
    string Type,
    string? PayloadTemplate = null,
    string? FileName = null,
    IReadOnlyList<string>? ArgsTemplate = null,
    IReadOnlyList<TemplateParameter>? Parameters = null,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    string? ToolCapabilityKind = null);

public sealed record AdminTaskLibraryItem(
    string Id,
    string Key,
    string DisplayName,
    string Description,
    string Type,
    string PayloadTemplate,
    DateTimeOffset CreatedAt,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed record AdminCapabilityItem(
    string Kind,
    string DisplayName,
    string Description,
    string Type,
    string Policy,
    string Availability,
    string TaskLibraryBehavior,
    IReadOnlyList<string> Examples);

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
