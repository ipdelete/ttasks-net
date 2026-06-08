using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;

namespace Ttasks.ChatApp.Services;

public sealed class GraphPlanBuilder
{
    public TaskGraph Build(GraphPlan plan)
    {
        var graph = new TaskGraph(plan.Graph.Title, plan.Graph.Metadata);
        var tasks = plan.Tasks.ToDictionary(task => task.Id, CreateTask, StringComparer.Ordinal);
        var dependencies = plan.Edges
            .GroupBy(edge => edge.To, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => tasks[edge.From]).ToList(), StringComparer.Ordinal);

        foreach (var planTask in plan.Tasks)
        {
            dependencies.TryGetValue(planTask.Id, out var after);
            graph.Add(tasks[planTask.Id], after: after ?? []);
        }

        return graph;
    }

    private static CoreTask CreateTask(GraphPlanTask task) =>
        task.Type.ToLowerInvariant() switch
        {
            "prompt" => CreatePromptTask(task),
            "process" => CreateProcessTask(task),
            _ => throw new ArgumentException($"Unsupported task type '{task.Type}'.")
        };

    private static CoreTask CreatePromptTask(GraphPlanTask task)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["planTaskId"] = task.Id
        };
        foreach (var entry in task.Metadata ?? new Dictionary<string, object?>())
            metadata[entry.Key] = entry.Value;

        return CoreTask.Prompt(
            task.Prompt ?? string.Empty,
            task.Title,
            task.Description,
            task.Timeout,
            metadata);
    }

    private static CoreTask CreateProcessTask(GraphPlanTask task)
    {
        if (task.Process is null)
            throw new ArgumentException($"Process task '{task.Id}' must include a process spec.");

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["planTaskId"] = task.Id
        };
        if (!string.IsNullOrWhiteSpace(task.LibraryItemKey))
            metadata["libraryKey"] = task.LibraryItemKey;
        if (task.LibrarySuggestion is { } suggestion)
            metadata["librarySuggestionKey"] = suggestion.Key;
        foreach (var entry in task.Metadata ?? new Dictionary<string, object?>())
            metadata[entry.Key] = entry.Value;

        return CoreTask.Process(
            new ProcessCommand(task.Process.FileName, task.Process.Args.ToList()),
            task.Title,
            task.Description,
            task.Timeout,
            metadata);
    }
}
