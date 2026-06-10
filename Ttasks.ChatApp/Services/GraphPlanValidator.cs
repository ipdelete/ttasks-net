using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Ttasks.ChatApp.Services;

public sealed partial class GraphPlanValidator
{
    private readonly ChatAppOptions _options;

    public GraphPlanValidator(IOptions<ChatAppOptions> options)
    {
        _options = options.Value;
    }

    public void Validate(GraphPlan plan, CapabilitySet capabilities)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(capabilities);
        if (plan.Graph is null)
            throw new ArgumentException("Plan graph is required.", nameof(plan));
        if (plan.Tasks.Count == 0)
            throw new ArgumentException("Plan must contain at least one task.", nameof(plan));
        if (plan.Tasks.Count > _options.MaxTasks)
            throw new ArgumentException($"Plan exceeds the maximum task count of {_options.MaxTasks}.", nameof(plan));

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in plan.Tasks)
        {
            if (!ids.Add(task.Id) || !TaskIdPattern().IsMatch(task.Id))
                throw new ArgumentException($"Invalid or duplicate task id '{task.Id}'.", nameof(plan));

            ValidateTask(task, capabilities, plan);
            EnsureNoUnresolvedPlaceholders(task);
        }

        foreach (var edge in plan.Edges)
        {
            if (!ids.Contains(edge.From) || !ids.Contains(edge.To))
                throw new ArgumentException("Edges must reference declared tasks.", nameof(plan));
            if (edge.From == edge.To)
                throw new ArgumentException("Self edges are not allowed.", nameof(plan));
        }

        EnsureAcyclic(plan);
        if (!plan.Tasks.Any(task => string.Equals(task.Type, "prompt", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Plan must include a final prompt task.", nameof(plan));

        ValidateGraphSuggestion(plan.GraphSuggestion);
    }

    private static void ValidateGraphSuggestion(GraphLibrarySuggestion? suggestion)
    {
        if (suggestion is null)
            return;
        if (string.IsNullOrWhiteSpace(suggestion.Key))
            throw new ArgumentException("Graph suggestion must include a stable key.", nameof(suggestion));
        if (string.IsNullOrWhiteSpace(suggestion.DisplayName))
            throw new ArgumentException("Graph suggestion must include a display name.", nameof(suggestion));
        if (string.IsNullOrWhiteSpace(suggestion.Description))
            throw new ArgumentException("Graph suggestion must include a description.", nameof(suggestion));
    }

    private void ValidateTask(GraphPlanTask task, CapabilitySet capabilities, GraphPlan plan)
    {
        if (string.Equals(task.Type, "prompt", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(task.Prompt))
                throw new ArgumentException($"Prompt task '{task.Id}' prompt is required.");
            if (task.Process is not null)
                throw new ArgumentException($"Prompt task '{task.Id}' must not include a process spec.");
            if (task.LibrarySuggestion is not null || !string.IsNullOrWhiteSpace(task.LibraryItemKey))
                throw new ArgumentException($"Prompt task '{task.Id}' must not reference a library template.");
            return;
        }

        if (!string.Equals(task.Type, "process", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Task '{task.Id}' type '{task.Type}' is not allowed.");

        if (task.Process is null)
            throw new ArgumentException($"Process task '{task.Id}' must include a process spec.");
        if (string.IsNullOrWhiteSpace(task.Process.FileName))
            throw new ArgumentException($"Process task '{task.Id}' fileName is required.");
        if (!IsAllowed(task.Process, capabilities.AllowedTools))
            throw new ArgumentException(
                $"Process task '{task.Id}' command '{Describe(task.Process)}' does not match any allowed tool prefix.");

        ValidateReferencedTaskIds(task, plan);

        if (task.LibrarySuggestion is { } suggestion)
        {
            if (string.IsNullOrWhiteSpace(suggestion.Key))
                throw new ArgumentException($"Process task '{task.Id}' library suggestion must include a key.");
            if (!string.Equals(suggestion.FileName, task.Process.FileName, StringComparison.Ordinal))
                throw new ArgumentException(
                    $"Process task '{task.Id}' library suggestion fileName must match the process fileName.");
            var probe = new ProcessSpec(suggestion.FileName, suggestion.ArgsTemplate);
            if (!IsAllowed(probe, capabilities.AllowedTools))
                throw new ArgumentException(
                    $"Process task '{task.Id}' library suggestion '{suggestion.Key}' does not match any allowed tool prefix.");
        }
    }

    private static void ValidateReferencedTaskIds(GraphPlanTask task, GraphPlan plan)
    {
        if (task.Process is null)
            return;
        var declaredTaskIds = plan.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var dependencyIds = plan.Edges
            .Where(edge => string.Equals(edge.To, task.Id, StringComparison.Ordinal))
            .Select(edge => edge.From)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var arg in task.Process.Args)
        {
            foreach (var referencedId in OutputReferenceResolver.ReferencedTaskIds(arg))
            {
                if (!declaredTaskIds.Contains(referencedId))
                    throw new ArgumentException(
                        $"Process task '{task.Id}' references unknown task '{referencedId}' via ${{{{ tasks.{referencedId}.output ... }}}}.");
                if (!dependencyIds.Contains(referencedId))
                    throw new ArgumentException(
                        $"Process task '{task.Id}' references task '{referencedId}' but does not declare it as a dependency. Add an edge from '{referencedId}' to '{task.Id}'.");
            }
        }
    }

    internal static bool IsAllowed(ProcessSpec process, IReadOnlyList<AllowedTool> allowed)
    {
        if (allowed.Count == 0)
            return false;
        var head = ComputeHead(process);
        return allowed.Any(tool => HeadStartsWithPrefix(head, tool.Prefix));
    }

    private static string ComputeHead(ProcessSpec process)
    {
        var leadingArgs = process.Args.TakeWhile(arg => !arg.StartsWith("-", StringComparison.Ordinal));
        return string.Join(' ', new[] { process.FileName }.Concat(leadingArgs));
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

    private static string Describe(ProcessSpec process) =>
        string.Join(' ', new[] { process.FileName }.Concat(process.Args));

    private static void EnsureAcyclic(GraphPlan plan)
    {
        var outgoing = plan.Edges
            .GroupBy(edge => edge.From, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(edge => edge.To).ToList(), StringComparer.Ordinal);
        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string id)
        {
            if (visited.Contains(id))
                return;
            if (!visiting.Add(id))
                throw new ArgumentException("Plan graph contains a cycle.", nameof(plan));

            if (outgoing.TryGetValue(id, out var children))
            {
                foreach (var child in children)
                    Visit(child);
            }

            visiting.Remove(id);
            visited.Add(id);
        }

        foreach (var task in plan.Tasks)
            Visit(task.Id);
    }

    [GeneratedRegex("^[A-Za-z0-9_.:-]{1,64}$")]
    private static partial Regex TaskIdPattern();

    // Matches an unresolved placeholder like {name} or {name:format}, but NOT
    // task output references inside ${{ ... }} (their inner '{' is followed by
    // whitespace, and their outer '{' is preceded by '$').
    [GeneratedRegex(@"(?<!\$)\{(?![\{\s])([A-Za-z_][A-Za-z0-9_.\-]*)(?::[^}\n]*)?\}")]
    private static partial Regex UnresolvedPlaceholderRegex();

    private static void EnsureNoUnresolvedPlaceholders(GraphPlanTask task)
    {
        void Scan(string? value, string field)
        {
            if (string.IsNullOrEmpty(value)) return;
            var match = UnresolvedPlaceholderRegex().Match(value);
            if (match.Success)
                throw new ArgumentException(
                    $"Task '{task.Id}' has an unresolved placeholder '{match.Value}' in {field}. " +
                    "Either supply a value for it via libraryParameters/graphParameters, replace the placeholder with a literal value, or reference an upstream output via ${{ tasks.<id>.output }}.");
        }

        Scan(task.Title, "title");
        Scan(task.Description, "description");
        Scan(task.Prompt, "prompt");
        if (task.Process is { } process)
        {
            Scan(process.FileName, "process.fileName");
            for (var i = 0; i < process.Args.Count; i++)
                Scan(process.Args[i], $"process.args[{i}]");
        }
    }
}
