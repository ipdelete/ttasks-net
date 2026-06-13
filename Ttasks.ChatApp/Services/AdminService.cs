using Microsoft.Extensions.Options;
using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;
using CoreTaskStatus = Ttasks.Core.TaskStatus;

namespace Ttasks.ChatApp.Services;

public sealed class AdminService
{
    private readonly ITaskStore _store;
    private readonly ITaskLibrary _library;
    private readonly ChatAppOptions _options;

    public AdminService(ITaskStore store, ITaskLibrary library, IGraphLibrary graphLibrary, IOptions<ChatAppOptions> options)
    {
        _store = store;
        _library = library;
        _graphLibrary = graphLibrary;
        _options = options.Value;
    }

    private readonly IGraphLibrary _graphLibrary;

    public IReadOnlyList<AdminGraphSummary> RecentGraphs(int limit = 50)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));

        return _store.Graphs.Keys
            .Reverse()
            .Take(limit)
            .Select(id => ToSummary(_store.Graphs.Get(id)))
            .ToList();
    }

    public AdminGraphDetail GetGraph(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var graph = _store.Graphs.Get(id);
        var tasks = graph.Members.Select(ToTaskSummary).ToList();
        var edges = graph.Members
            .SelectMany(task => graph.Dependencies(task).Select(dependency => new AdminGraphEdge(dependency.Id, task.Id)))
            .ToList();

        return new AdminGraphDetail(
            graph.Id,
            graph.Title,
            graph.CreatedAt,
            GraphStatus(graph.Members),
            graph.Metadata,
            tasks,
            edges);
    }

    public AdminTaskDetail GetTask(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var task = _store.Tasks.Get(id);
        return new AdminTaskDetail(
            task.Id,
            task.TypeName,
            task.Title,
            task.Description,
            task.Payload,
            task.Timeout,
            task.Status.ToString(),
            task.CreatedAt,
            task.Error,
            task.BlockedBy,
            task.Metadata,
            task.Result is null ? null : ToResult(task.Result));
    }

    public IReadOnlyList<AdminTaskLibraryItem> TaskLibrary() =>
        _library.All()
            .Select(item => new AdminTaskLibraryItem(
                item.Id,
                item.Key,
                item.DisplayName,
                item.Description,
                item.FileName,
                item.ArgsTemplate,
                item.CreatedAt,
                item.Metadata))
            .ToList();

    public IReadOnlyList<AdminGraphLibraryItem> GraphLibrary() =>
        _graphLibrary.All()
            .Select(item => new AdminGraphLibraryItem(
                item.Id,
                item.Key,
                item.DisplayName,
                item.Description,
                item.PlanTemplate,
                item.Parameters,
                item.CreatedAt,
                item.Metadata))
            .ToList();

    public bool RemoveGraphLibraryItem(string key) => _graphLibrary.Remove(key);

    public IReadOnlyList<AdminAllowedTool> AllowedTools() =>
        _options.AllowedTools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Prefix))
            .Select(tool => new AdminAllowedTool(
                tool.Prefix.Trim(),
                tool.Description,
                tool.HelpCommand,
                ConfigCapabilityProvider.NormalizeTraits(tool.Traits)))
            .ToList();

    public IReadOnlyList<AdminTurnSummary> RecentTurns(int limit = 50)
    {
        if (limit < 1)
            throw new ArgumentOutOfRangeException(nameof(limit));

        return EnumerateTurnTasks()
            .GroupBy(task => (string)task.Metadata[ChatTurnService.TurnIdKey]!, StringComparer.Ordinal)
            .Select(group => ToTurnSummary(group.Key, group.ToList()))
            .OrderByDescending(turn => turn.CreatedAt)
            .Take(limit)
            .ToList();
    }

    public AdminTurnDetail GetTurn(string turnId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(turnId);

        var tasks = EnumerateTurnTasks()
            .Where(task => string.Equals(task.Metadata[ChatTurnService.TurnIdKey] as string, turnId, StringComparison.Ordinal))
            .OrderBy(task => task.CreatedAt)
            .ToList();
        if (tasks.Count == 0)
            throw new KeyNotFoundException($"Turn '{turnId}' was not found.");

        var sessionId = tasks
            .Select(task => task.Metadata.TryGetValue(ChatTurnService.SessionIdKey, out var raw) ? raw as string : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return new AdminTurnDetail(
            turnId,
            sessionId,
            tasks[0].CreatedAt,
            RollupStatus(tasks),
            tasks.Select(ToTurnTask).ToList());
    }

    private IEnumerable<CoreTask> EnumerateTurnTasks() =>
        _store.Tasks.Keys
            .Select(id => _store.Tasks.Get(id))
            .Where(task => task.Metadata.ContainsKey(ChatTurnService.TurnIdKey));

    private static AdminTurnSummary ToTurnSummary(string turnId, IReadOnlyList<CoreTask> tasks)
    {
        var ordered = tasks.OrderBy(task => task.CreatedAt).ToList();
        var byKind = ordered
            .GroupBy(task => task.Metadata.TryGetValue(ChatTurnService.TaskKindKey, out var raw) ? raw as string ?? "unknown" : "unknown", StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        var sessionId = ordered
            .Select(task => task.Metadata.TryGetValue(ChatTurnService.SessionIdKey, out var raw) ? raw as string : null)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        return new AdminTurnSummary(
            turnId,
            sessionId,
            ordered[0].CreatedAt,
            ordered.Count,
            byKind.GetValueOrDefault("router", 0),
            byKind.GetValueOrDefault("planner", 0),
            byKind.GetValueOrDefault("repair", 0),
            byKind.GetValueOrDefault("process", 0),
            byKind.GetValueOrDefault("summary", 0),
            ordered.Count(task => task.Status == CoreTaskStatus.Failed),
            ordered.Count(task => task.Status == CoreTaskStatus.Blocked),
            ordered.Count(task => task.Status == CoreTaskStatus.Cancelled),
            ordered.Count(task => task.Status == CoreTaskStatus.Succeeded),
            RollupStatus(ordered));
    }

    private static AdminTurnTask ToTurnTask(CoreTask task) =>
        new(
            task.Id,
            task.Metadata.TryGetValue(ChatTurnService.TaskKindKey, out var kind) ? kind as string ?? "unknown" : "unknown",
            task.TypeName,
            task.Status.ToString(),
            task.CreatedAt,
            task.Title,
            task.Error ?? task.Result?.Error,
            task.BlockedBy,
            task.Metadata.TryGetValue(ChatTurnService.AttemptKey, out var attempt) && attempt is IConvertible convertible
                ? Convert.ToInt32(convertible, System.Globalization.CultureInfo.InvariantCulture)
                : null);

    private static string RollupStatus(IReadOnlyList<CoreTask> tasks)
    {
        if (tasks.Count == 0)
            return "Empty";
        if (tasks.Any(task => task.Status is CoreTaskStatus.Failed or CoreTaskStatus.Cancelled or CoreTaskStatus.Blocked))
            return "Failed";
        if (tasks.All(task => task.Status == CoreTaskStatus.Succeeded))
            return "Succeeded";
        if (tasks.Any(task => task.Status == CoreTaskStatus.Running))
            return "Running";
        return "Pending";
    }

    private static AdminGraphSummary ToSummary(TaskGraph graph)
    {
        var tasks = graph.Members;
        return new AdminGraphSummary(
            graph.Id,
            graph.Title,
            graph.CreatedAt,
            GraphStatus(tasks),
            tasks.Count,
            tasks.Count(task => task.Status == CoreTaskStatus.Succeeded),
            tasks.Count(task => task.Status == CoreTaskStatus.Failed),
            tasks.Count(task => task.Status == CoreTaskStatus.Cancelled),
            tasks.Count(task => task.Status == CoreTaskStatus.Blocked));
    }

    private static AdminTaskSummary ToTaskSummary(CoreTask task) =>
        new(
            task.Id,
            task.TypeName,
            task.Title,
            task.Status.ToString(),
            task.CreatedAt,
            task.Error,
            task.BlockedBy,
            !string.IsNullOrEmpty(task.Result?.Output));

    private static AdminTaskResult ToResult(TaskResult result) =>
        new(
            result.Status.ToString(),
            result.StartedAt,
            result.FinishedAt,
            result.DurationSeconds,
            result.Output,
            result.Error,
            result.ReturnCode,
            result.TerminationReason);

    private static string GraphStatus(IReadOnlyList<CoreTask> tasks)
    {
        if (tasks.Count == 0)
            return "Empty";
        if (tasks.Any(task => task.Status is CoreTaskStatus.Failed or CoreTaskStatus.Cancelled or CoreTaskStatus.Blocked))
            return "Failed";
        if (tasks.All(task => task.Status == CoreTaskStatus.Succeeded))
            return "Succeeded";
        if (tasks.Any(task => task.Status == CoreTaskStatus.Running))
            return "Running";
        return "Pending";
    }
}
