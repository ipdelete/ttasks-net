using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;
using CoreTaskStatus = Ttasks.Core.TaskStatus;

namespace Ttasks.ChatApp.Services;

public sealed class AdminService
{
    private readonly ITaskStore _store;
    private readonly ITaskLibrary _library;

    public AdminService(ITaskStore store, ITaskLibrary library)
    {
        _store = store;
        _library = library;
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
                item.TaskType == TaskType.Powershell ? "powershell" : item.TaskType.ToString().ToLowerInvariant(),
                item.PayloadTemplate,
                item.CreatedAt,
                item.Metadata))
            .ToList();

    public IReadOnlyList<AdminCapabilityItem> Capabilities() =>
        [
            new AdminCapabilityItem(
                "teams.read",
                "Read Teams chat",
                "Read messages from a Teams chat and make the JSON output available to a graph.",
                "powershell",
                "fixed-template",
                "Available when the user provides an explicit Teams chat URL/ID or references a previously saved chat alias.",
                "Creates or reuses task-library items keyed as teams.chat.read:<chat-id>; saved items include chat metadata and aliases when Teams metadata can be resolved.",
                [
                    "read https://teams.microsoft.com/l/chat/<chat-id>/conversations",
                    "read 19:<chat-id>@thread.v2",
                    "read teams chat aet swe chat"
                ]),
            new AdminCapabilityItem(
                "mail",
                "Use mail CLI",
                "Use the full mail CLI surface from mail --help to create reusable task-library templates.",
                "powershell",
                "full-tool",
                "Available when the user asks about mail, email, messages, or the inbox.",
                "The LLM reads mail --help, proposes task-library templates, and the host stores accepted templates for reuse. The host still requires a single mail command with no shell chaining.",
                [
                    "how many emails did I get today",
                    "find unread mail from Kent",
                    "read messages about deploy"
                ]),
            new AdminCapabilityItem(
                "calendar.today",
                "Read today's calendar",
                "List today's calendar events and make event JSON available to a graph.",
                "powershell",
                "fixed-template",
                "Available when the user asks for today's calendar, schedule, events, or meetings.",
                "Creates or reuses the calendar.today task-library item; the date range is rendered from the host clock at execution planning time.",
                [
                    "read today's calendar",
                    "summarize my schedule today",
                    "what meetings do I have today"
                ]),
            new AdminCapabilityItem(
                "az.account.list",
                "List Azure subscriptions",
                "List accessible Azure subscriptions as JSON.",
                "powershell",
                "limited-tool",
                "Available when the user asks for Azure subscriptions or accounts.",
                "Creates or reuses the az.account.list task-library item with a fixed read-only az account list command.",
                [
                    "list azure subscriptions",
                    "show az accounts"
                ]),
            new AdminCapabilityItem(
                "az.group.list",
                "List Azure resource groups",
                "List Azure resource groups in the active subscription as JSON.",
                "powershell",
                "limited-tool",
                "Available when the user asks for Azure resource groups.",
                "Creates or reuses the az.group.list task-library item with a fixed read-only az group list command.",
                [
                    "list azure resource groups",
                    "show az rgs"
                ]),
            new AdminCapabilityItem(
                "az.resource.list",
                "List Azure resources",
                "List Azure resources in the active subscription as JSON.",
                "powershell",
                "limited-tool",
                "Available when the user asks for Azure resources, excluding resource-group-only requests.",
                "Creates or reuses the az.resource.list task-library item with a fixed read-only az resource list command.",
                [
                    "list azure resources",
                    "summarize az resources"
                ])
        ];

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
