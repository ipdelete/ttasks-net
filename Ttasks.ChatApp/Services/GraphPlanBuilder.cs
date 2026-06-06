using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;

namespace Ttasks.ChatApp.Services;

public sealed class GraphPlanBuilder
{
    public TaskGraph Build(GraphPlan plan, CapabilitySet? capabilities = null)
    {
        var graph = new TaskGraph(plan.Graph.Title, plan.Graph.Metadata);
        var tasks = plan.Tasks.ToDictionary(task => task.Id, task => CreateTask(task, capabilities), StringComparer.Ordinal);
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

    private static CoreTask CreateTask(GraphPlanTask task, CapabilitySet? capabilities) =>
        task.Type.ToLowerInvariant() switch
        {
            "powershell" => CreateCapabilityTask(task, capabilities),
            "process" => CreateCapabilityTask(task, capabilities),
            "prompt" => CoreTask.Prompt(task.Payload ?? string.Empty, task.Title, task.Description, task.Timeout, task.Metadata),
            _ => throw new ArgumentException($"Unsupported task type '{task.Type}'.")
        };

    private static CoreTask CreateCapabilityTask(GraphPlanTask task, CapabilitySet? capabilities)
    {
        if (string.IsNullOrWhiteSpace(task.CapabilityId))
            throw new ArgumentException($"Task '{task.Id}' capabilityId is required.");
        if (capabilities is null || !capabilities.ById.TryGetValue(task.CapabilityId, out var capability))
            throw new ArgumentException($"Task '{task.Id}' references an unavailable capability.");

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["capabilityId"] = capability.Id,
            ["capabilityDisplayName"] = capability.DisplayName
        };
        if (capability.Metadata.TryGetValue(StoreBackedTaskLibrary.LibraryKeyKey, out var libraryKey))
            metadata["libraryKey"] = libraryKey;
        if (capability.Metadata.TryGetValue("libraryTaskId", out var libraryTaskId))
            metadata["libraryTaskId"] = libraryTaskId;
        if (capability.Metadata.TryGetValue("capabilityKind", out var kind))
            metadata["capabilityKind"] = kind;
        if (capability.Metadata.TryGetValue("capabilityPolicy", out var policy))
            metadata["capabilityPolicy"] = policy;
        if (capability.Metadata.TryGetValue("toolName", out var toolName))
            metadata["toolName"] = toolName;

        foreach (var entry in task.Metadata ?? new Dictionary<string, object?>())
            metadata[entry.Key] = entry.Value;

        return capability.TaskType switch
        {
            TaskType.Powershell => CoreTask.Powershell(
                capability.Payload,
                task.Title ?? capability.DisplayName,
                task.Description ?? capability.Description,
                task.Timeout,
                metadata),
            TaskType.Process => CoreTask.Process(
                ProcessCommand.FromJson(capability.Payload),
                task.Title ?? capability.DisplayName,
                task.Description ?? capability.Description,
                task.Timeout,
                metadata),
            _ => throw new ArgumentException($"Unsupported capability task type '{capability.TaskType}'.")
        };
    }
}
