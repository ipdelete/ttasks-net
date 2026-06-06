using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ttasks.Core;

namespace Ttasks.ChatApp.Services;

public sealed partial class GraphPlanValidator
{
    private readonly ChatAppOptions _options;

    public GraphPlanValidator(IOptions<ChatAppOptions> options)
    {
        _options = options.Value;
    }

    public void Validate(GraphPlan plan, CapabilitySet? capabilities = null)
    {
        ArgumentNullException.ThrowIfNull(plan);
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

            ValidateTask(task, capabilities);
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
    }

    private void ValidateTask(GraphPlanTask task, CapabilitySet? capabilities)
    {
        if (string.Equals(task.Type, "prompt", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(task.Payload))
                throw new ArgumentException($"Prompt task '{task.Id}' payload is required.");
            if (!string.IsNullOrWhiteSpace(task.CapabilityId))
                throw new ArgumentException($"Prompt task '{task.Id}' must not reference a capability.");
            return;
        }

        if (!string.Equals(task.Type, "powershell", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Task '{task.Id}' type '{task.Type}' is not allowed.");

        if (!string.IsNullOrWhiteSpace(task.Payload))
            throw new ArgumentException($"Task '{task.Id}' must not provide an executable payload; use capabilityId.");

        if (string.IsNullOrWhiteSpace(task.CapabilityId))
            throw new ArgumentException($"Task '{task.Id}' capabilityId is required.");

        if (capabilities is null || !capabilities.ById.TryGetValue(task.CapabilityId, out var capability))
            throw new ArgumentException($"Task '{task.Id}' references an unavailable capability.");

        if (capability.TaskType != TaskType.Powershell)
            throw new ArgumentException($"Task '{task.Id}' type does not match capability '{capability.Id}'.");

        if (capability.Metadata.TryGetValue("capabilityKind", out var rawKind) && rawKind is string kind)
            ValidateCapabilityPayload(capability, kind);
    }

    private static void ValidateCapabilityPayload(CommandCapability capability, string kind)
    {
        var isValid = kind switch
        {
            "teams.read" => TeamsReadPattern().IsMatch(capability.Payload) || TeamsReadChannelPattern().IsMatch(capability.Payload),
            "mail" => MailToolPattern().IsMatch(capability.Payload) && IsSingleCommand(capability.Payload),
            "mail.today" => MailTodayPattern().IsMatch(capability.Payload),
            "calendar.today" => CalendarTodayPattern().IsMatch(capability.Payload),
            "az.account.list" => capability.Payload == "az account list --only-show-errors --output json",
            "az.group.list" => capability.Payload == "az group list --only-show-errors --output json",
            "az.resource.list" => capability.Payload == "az resource list --only-show-errors --output json",
            _ => throw new ArgumentException($"Capability '{capability.Id}' has unsupported kind '{kind}'.")
        };

        if (!isValid)
            throw new ArgumentException($"Capability '{capability.Id}' does not resolve to a valid '{kind}' command.");
    }

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

    [GeneratedRegex("^teams read \\S+ -n \\d+ --json$")]
    private static partial Regex TeamsReadPattern();

    [GeneratedRegex("^teams read-channel \\S+ \\S+ -n \\d+ --json$")]
    private static partial Regex TeamsReadChannelPattern();

    [GeneratedRegex("^mail search --query '\\?\\$filter=receivedDateTime ge \\d{4}-\\d{2}-\\d{2}T00:00:00Z and receivedDateTime lt \\d{4}-\\d{2}-\\d{2}T00:00:00Z&\\$orderby=receivedDateTime desc&\\$top=\\d+' --json$")]
    private static partial Regex MailTodayPattern();

    [GeneratedRegex("^mail(\\s+.+)?$")]
    private static partial Regex MailToolPattern();

    [GeneratedRegex("^calendar list -s \\d{4}-\\d{2}-\\d{2}T00:00:00 -e \\d{4}-\\d{2}-\\d{2}T00:00:00 -n \\d+ --json$")]
    private static partial Regex CalendarTodayPattern();

    private static bool IsSingleCommand(string payload) =>
        !payload.Contains('\n')
        && !payload.Contains('\r')
        && !payload.Contains("&&", StringComparison.Ordinal)
        && !payload.Contains('|')
        && !payload.Contains(';');
}
