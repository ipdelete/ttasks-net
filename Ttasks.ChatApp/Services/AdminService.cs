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

    public AdminService(ITaskStore store, ITaskLibrary library, IOptions<ChatAppOptions> options)
    {
        _store = store;
        _library = library;
        _options = options.Value;
    }

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

    public IReadOnlyList<AdminAllowedTool> AllowedTools() =>
        _options.AllowedTools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Prefix))
            .Select(tool => new AdminAllowedTool(tool.Prefix.Trim(), tool.Description, tool.HelpCommand))
            .ToList();

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
