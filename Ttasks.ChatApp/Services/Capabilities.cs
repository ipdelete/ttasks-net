using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;

namespace Ttasks.ChatApp.Services;

public sealed record CapabilityRequest(string SessionId, string UserMessage);

public sealed record AllowedTool(
    string Prefix,
    string? Description = null,
    string? HelpCommand = null,
    IReadOnlyList<string>? Traits = null);

public sealed record CapabilitySet(
    IReadOnlyList<AllowedTool> AllowedTools,
    IReadOnlyList<TaskLibraryItem> LibrarySuggestions,
    IReadOnlyList<GraphLibraryItem> GraphLibrarySuggestions,
    string EmptyMessage);

public interface ICapabilityProvider
{
    CapabilitySet GetCapabilities(CapabilityRequest request);
}

public sealed partial class ConfigCapabilityProvider : ICapabilityProvider
{
    private readonly ChatAppOptions _options;
    private readonly ITaskLibrary _library;
    private readonly IGraphLibrary _graphLibrary;

    public ConfigCapabilityProvider(IOptions<ChatAppOptions> options, ITaskLibrary library, IGraphLibrary graphLibrary)
    {
        _options = options.Value;
        _library = library;
        _graphLibrary = graphLibrary;
    }

    public CapabilitySet GetCapabilities(CapabilityRequest request)
    {
        var allowed = _options.AllowedTools
            .Where(tool => !string.IsNullOrWhiteSpace(tool.Prefix))
            .Select(tool => new AllowedTool(
                tool.Prefix.Trim(),
                tool.Description,
                tool.HelpCommand,
                NormalizeTraits(tool.Traits)))
            .ToList();

        var suggestions = _library.All();
        var graphSuggestions = _graphLibrary.All();
        var message = allowed.Count == 0
            ? "No tools are configured for this chat session."
            : "No allowed tool covered this request.";

        return new CapabilitySet(allowed, suggestions, graphSuggestions, message);
    }

    internal static IReadOnlyList<string>? NormalizeTraits(IEnumerable<string>? traits)
    {
        if (traits is null)
            return null;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var normalized = new List<string>();
        foreach (var raw in traits)
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var collapsed = WhitespaceRegex().Replace(raw.Trim(), string.Empty)
                .ToLowerInvariant();
            if (collapsed.Length == 0)
                continue;
            if (seen.Add(collapsed))
                normalized.Add(collapsed);
        }

        return normalized.Count == 0 ? null : normalized;
    }

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
}

public sealed record TemplateParameter(
    string Name,
    string Source,
    string? Format = null,
    object? DefaultValue = null);

public sealed record TaskLibraryDefinition(
    string Key,
    string DisplayName,
    string Description,
    string FileName,
    IReadOnlyList<string> ArgsTemplate,
    IReadOnlyList<TemplateParameter> Parameters,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record TaskLibraryItem(
    string Id,
    string Key,
    string DisplayName,
    string Description,
    string FileName,
    IReadOnlyList<string> ArgsTemplate,
    IReadOnlyList<TemplateParameter> Parameters,
    IReadOnlyDictionary<string, object?> Metadata,
    DateTimeOffset CreatedAt);

public interface ITaskLibrary
{
    TaskLibraryItem GetOrAdd(TaskLibraryDefinition definition);
    IReadOnlyList<TaskLibraryItem> All();
}

public sealed class StoreBackedTaskLibrary : ITaskLibrary
{
    public const string IsLibraryItemKey = "taskLibraryItem";
    public const string LibraryKeyKey = "taskLibraryKey";
    public const string TemplateParametersKey = "templateParameters";
    public const string ProcessFileNameKey = "processFileName";
    public const string ProcessArgsTemplateKey = "processArgsTemplate";

    private readonly ITaskStore _store;

    public StoreBackedTaskLibrary(ITaskStore store)
    {
        _store = store;
    }

    public TaskLibraryItem GetOrAdd(TaskLibraryDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Key);
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.FileName);

        var existing = All().FirstOrDefault(item => string.Equals(item.Key, definition.Key, StringComparison.Ordinal));
        if (existing is not null)
            return UpdateIfChanged(existing, definition);

        var task = CreateTemplateTask(definition);
        _store.Tasks.Save(task);
        return ToItem(task);
    }

    public IReadOnlyList<TaskLibraryItem> All() =>
        _store.Tasks.Keys
            .Select(id => _store.Tasks.Get(id))
            .Where(IsLibraryTask)
            .Select(ToItem)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToList();

    private TaskLibraryItem UpdateIfChanged(TaskLibraryItem existing, TaskLibraryDefinition definition)
    {
        var same = existing.DisplayName == definition.DisplayName
            && existing.Description == definition.Description
            && existing.FileName == definition.FileName
            && existing.ArgsTemplate.SequenceEqual(definition.ArgsTemplate, StringComparer.Ordinal)
            && SameParameters(existing.Parameters, definition.Parameters);
        if (same)
            return existing;

        var task = _store.Tasks.Get(existing.Id);
        ApplyDefinitionToTask(task, definition);
        _store.Tasks.Save(task);
        return ToItem(task);
    }

    private static bool SameParameters(IReadOnlyList<TemplateParameter> left, IReadOnlyList<TemplateParameter> right)
    {
        if (left.Count != right.Count)
            return false;
        return left.Zip(right).All(pair =>
            pair.First.Name == pair.Second.Name
            && pair.First.Source == pair.Second.Source
            && pair.First.Format == pair.Second.Format
            && Equals(pair.First.DefaultValue, pair.Second.DefaultValue));
    }

    private static CoreTask CreateTemplateTask(TaskLibraryDefinition definition)
    {
        var command = new ProcessCommand(definition.FileName, definition.ArgsTemplate.ToList());
        var task = CoreTask.Process(
            command,
            title: definition.DisplayName,
            description: definition.Description);
        ApplyTemplateMetadata(task, definition);
        return task;
    }

    private static void ApplyDefinitionToTask(CoreTask task, TaskLibraryDefinition definition)
    {
        task.Title = definition.DisplayName;
        task.Description = definition.Description;
        task.Payload = new ProcessCommand(definition.FileName, definition.ArgsTemplate.ToList()).ToJson();
        ApplyTemplateMetadata(task, definition);
    }

    private static void ApplyTemplateMetadata(CoreTask task, TaskLibraryDefinition definition)
    {
        task.SetMetadata(IsLibraryItemKey, true);
        task.SetMetadata(LibraryKeyKey, definition.Key);
        task.SetMetadata(ProcessFileNameKey, definition.FileName);
        task.SetMetadata(ProcessArgsTemplateKey, definition.ArgsTemplate.ToList());
        task.SetMetadata(TemplateParametersKey, definition.Parameters.Select(ToMetadata).ToList());
        foreach (var entry in definition.Metadata ?? new Dictionary<string, object?>())
            task.SetMetadata(entry.Key, entry.Value);
    }

    private static bool IsLibraryTask(CoreTask task) =>
        task.Metadata.TryGetValue(IsLibraryItemKey, out var value) && value is bool boolValue && boolValue;

    private static TaskLibraryItem ToItem(CoreTask task)
    {
        var key = task.Metadata.TryGetValue(LibraryKeyKey, out var value) ? value as string : null;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Task library item '{task.Id}' is missing a library key.");

        var fileName = ReadString(task.Metadata, ProcessFileNameKey)
            ?? throw new InvalidOperationException($"Task library item '{task.Id}' is missing a file name.");
        var args = ReadStringList(task.Metadata, ProcessArgsTemplateKey) ?? [];

        return new TaskLibraryItem(
            task.Id,
            key,
            task.Title,
            task.Description,
            fileName,
            args,
            ReadParameters(task.Metadata),
            task.Metadata,
            task.CreatedAt);
    }

    private static IReadOnlyDictionary<string, object?> ToMetadata(TemplateParameter parameter)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = parameter.Name,
            ["source"] = parameter.Source
        };
        if (!string.IsNullOrWhiteSpace(parameter.Format))
            values["format"] = parameter.Format;
        if (parameter.DefaultValue is not null)
            values["defaultValue"] = parameter.DefaultValue;
        return values;
    }

    private static IReadOnlyList<TemplateParameter> ReadParameters(IReadOnlyDictionary<string, object?> metadata)
    {
        if (!metadata.TryGetValue(TemplateParametersKey, out var raw) || raw is not IEnumerable<object?> items)
            return [];

        return items
            .OfType<IReadOnlyDictionary<string, object?>>()
            .Select(item => new TemplateParameter(
                item.TryGetValue("name", out var name) ? name as string ?? string.Empty : string.Empty,
                item.TryGetValue("source", out var source) ? source as string ?? string.Empty : string.Empty,
                item.TryGetValue("format", out var format) ? format as string : null,
                item.TryGetValue("defaultValue", out var defaultValue) ? defaultValue : null))
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name) && !string.IsNullOrWhiteSpace(parameter.Source))
            .ToList();
    }

    private static string? ReadString(IReadOnlyDictionary<string, object?> metadata, string key) =>
        metadata.TryGetValue(key, out var value) ? value as string : null;

    private static IReadOnlyList<string>? ReadStringList(IReadOnlyDictionary<string, object?> metadata, string key)
    {
        if (!metadata.TryGetValue(key, out var raw))
            return null;
        if (raw is IEnumerable<object?> values)
            return values.OfType<string>().ToList();
        if (raw is IEnumerable<string> strings)
            return strings.ToList();
        return null;
    }
}

public sealed partial class TaskLibraryTemplateRenderer
{
    private static readonly Regex TokenPattern = new(@"(?<!\{)\{(?<name>[A-Za-z_][A-Za-z0-9_]*)(:(?<format>[^}]+))?\}(?!\})", RegexOptions.Compiled);
    private readonly TimeProvider _timeProvider;

    public TaskLibraryTemplateRenderer()
        : this(TimeProvider.System)
    {
    }

    public TaskLibraryTemplateRenderer(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public ProcessCommand Render(TaskLibraryItem item, IReadOnlyDictionary<string, object?>? overrides = null) =>
        new(item.FileName, item.ArgsTemplate.Select(arg => RenderArg(item, arg, overrides)).ToList());

    private string RenderArg(TaskLibraryItem item, string template, IReadOnlyDictionary<string, object?>? overrides) =>
        TokenPattern.Replace(template, match =>
        {
            var name = match.Groups["name"].Value;
            var parameter = item.Parameters.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
                ?? new TemplateParameter(name, "default");

            var format = match.Groups["format"].Success ? match.Groups["format"].Value : parameter.Format;
            return FormatValue(ResolveValue(item, parameter, overrides), format);
        });

    private object? ResolveValue(TaskLibraryItem item, TemplateParameter parameter, IReadOnlyDictionary<string, object?>? overrides)
    {
        if (overrides is not null && overrides.TryGetValue(parameter.Name, out var overrideValue))
            return overrideValue;

        return parameter.Source switch
        {
            "clock.now" => _timeProvider.GetLocalNow(),
            "clock.yesterday" => _timeProvider.GetLocalNow().AddDays(-1),
            "clock.tomorrow" => _timeProvider.GetLocalNow().AddDays(1),
            "default" => parameter.DefaultValue,
            var source when source.StartsWith("metadata:", StringComparison.Ordinal) =>
                item.Metadata.TryGetValue(source["metadata:".Length..], out var value) ? value : parameter.DefaultValue,
            _ => throw new InvalidOperationException($"Unsupported template parameter source '{parameter.Source}'.")
        };
    }

    public string RenderText(string template, IReadOnlyList<TemplateParameter> parameters, IReadOnlyDictionary<string, object?>? overrides = null)
    {
        if (string.IsNullOrEmpty(template))
            return template;
        return TokenPattern.Replace(template, match =>
        {
            var name = match.Groups["name"].Value;
            var parameter = parameters.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal))
                ?? new TemplateParameter(name, "default");
            var format = match.Groups["format"].Success ? match.Groups["format"].Value : parameter.Format;
            return FormatValue(ResolveValueWithoutItem(parameter, overrides), format);
        });
    }

    private object? ResolveValueWithoutItem(TemplateParameter parameter, IReadOnlyDictionary<string, object?>? overrides)
    {
        if (overrides is not null && overrides.TryGetValue(parameter.Name, out var overrideValue))
            return overrideValue;

        return parameter.Source switch
        {
            "clock.now" => _timeProvider.GetLocalNow(),
            "clock.yesterday" => _timeProvider.GetLocalNow().AddDays(-1),
            "clock.tomorrow" => _timeProvider.GetLocalNow().AddDays(1),
            "default" => parameter.DefaultValue,
            "user" => parameter.DefaultValue,
            _ => throw new InvalidOperationException($"Unsupported template parameter source '{parameter.Source}'.")
        };
    }

    private static string FormatValue(object? value, string? format) =>
        value switch
        {
            null => string.Empty,
            DateTimeOffset dateTime => string.IsNullOrWhiteSpace(format) ? dateTime.ToString("O") : dateTime.ToString(format, CultureInfo.InvariantCulture),
            DateTime dateTime => string.IsNullOrWhiteSpace(format) ? dateTime.ToString("O") : dateTime.ToString(format, CultureInfo.InvariantCulture),
            IFormattable formattable => string.IsNullOrWhiteSpace(format) ? formattable.ToString(null, CultureInfo.InvariantCulture) : formattable.ToString(format, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
}

public static class TaskLibrarySeeder
{
    public static void Seed(ITaskLibrary library, IEnumerable<TaskLibrarySeed> seeds)
    {
        foreach (var seed in seeds)
        {
            if (string.IsNullOrWhiteSpace(seed.Key) || string.IsNullOrWhiteSpace(seed.FileName))
                continue;
            library.GetOrAdd(new TaskLibraryDefinition(
                seed.Key,
                seed.DisplayName,
                seed.Description,
                seed.FileName,
                seed.ArgsTemplate.ToList(),
                seed.Parameters.Select(p => new TemplateParameter(p.Name, p.Source, p.Format, p.DefaultValue)).ToList()));
        }
    }
}

public sealed record GraphLibraryDefinition(
    string Key,
    string DisplayName,
    string Description,
    GraphPlan PlanTemplate,
    IReadOnlyList<TemplateParameter> Parameters,
    IReadOnlyDictionary<string, object?>? Metadata = null);

public sealed record GraphLibraryItem(
    string Id,
    string Key,
    string DisplayName,
    string Description,
    GraphPlan PlanTemplate,
    IReadOnlyList<TemplateParameter> Parameters,
    IReadOnlyDictionary<string, object?> Metadata,
    DateTimeOffset CreatedAt);

public interface IGraphLibrary
{
    GraphLibraryItem GetOrAdd(GraphLibraryDefinition definition);
    IReadOnlyList<GraphLibraryItem> All();
    bool Remove(string key);
}

public sealed class StoreBackedGraphLibrary : IGraphLibrary
{
    public const string IsGraphLibraryItemKey = "graphLibraryItem";
    public const string GraphLibraryKeyKey = "graphLibraryKey";
    public const string GraphTemplateParametersKey = "graphTemplateParameters";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ITaskStore _store;

    public StoreBackedGraphLibrary(ITaskStore store)
    {
        _store = store;
    }

    public GraphLibraryItem GetOrAdd(GraphLibraryDefinition definition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(definition.Key);
        ArgumentNullException.ThrowIfNull(definition.PlanTemplate);

        var existing = All().FirstOrDefault(item => string.Equals(item.Key, definition.Key, StringComparison.Ordinal));
        if (existing is not null)
            return UpdateIfChanged(existing, definition);

        var task = CreateTemplateTask(definition);
        _store.Tasks.Save(task);
        return ToItem(task);
    }

    public IReadOnlyList<GraphLibraryItem> All() =>
        _store.Tasks.Keys
            .Select(id => _store.Tasks.Get(id))
            .Where(IsGraphLibraryTask)
            .Select(ToItem)
            .OrderBy(item => item.Key, StringComparer.Ordinal)
            .ToList();

    public bool Remove(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var existing = All().FirstOrDefault(item => string.Equals(item.Key, key, StringComparison.Ordinal));
        if (existing is null)
            return false;
        _store.Tasks.Delete(existing.Id);
        return true;
    }

    private GraphLibraryItem UpdateIfChanged(GraphLibraryItem existing, GraphLibraryDefinition definition)
    {
        var same = existing.DisplayName == definition.DisplayName
            && existing.Description == definition.Description
            && string.Equals(SerializePlan(existing.PlanTemplate), SerializePlan(definition.PlanTemplate), StringComparison.Ordinal)
            && SameParameters(existing.Parameters, definition.Parameters);
        if (same)
            return existing;

        var task = _store.Tasks.Get(existing.Id);
        task.Title = definition.DisplayName;
        task.Description = definition.Description;
        task.Payload = SerializePlan(definition.PlanTemplate);
        ApplyTemplateMetadata(task, definition);
        _store.Tasks.Save(task);
        return ToItem(task);
    }

    private static bool SameParameters(IReadOnlyList<TemplateParameter> left, IReadOnlyList<TemplateParameter> right)
    {
        if (left.Count != right.Count) return false;
        return left.Zip(right).All(pair =>
            pair.First.Name == pair.Second.Name
            && pair.First.Source == pair.Second.Source
            && pair.First.Format == pair.Second.Format
            && Equals(pair.First.DefaultValue, pair.Second.DefaultValue));
    }

    private static CoreTask CreateTemplateTask(GraphLibraryDefinition definition)
    {
        var task = CoreTask.Prompt(
            SerializePlan(definition.PlanTemplate),
            title: definition.DisplayName,
            description: definition.Description);
        ApplyTemplateMetadata(task, definition);
        return task;
    }

    private static void ApplyTemplateMetadata(CoreTask task, GraphLibraryDefinition definition)
    {
        task.SetMetadata(IsGraphLibraryItemKey, true);
        task.SetMetadata(GraphLibraryKeyKey, definition.Key);
        task.SetMetadata(GraphTemplateParametersKey, definition.Parameters.Select(ToMetadata).ToList());
        foreach (var entry in definition.Metadata ?? new Dictionary<string, object?>())
            task.SetMetadata(entry.Key, entry.Value);
    }

    private static bool IsGraphLibraryTask(CoreTask task) =>
        task.Metadata.TryGetValue(IsGraphLibraryItemKey, out var value) && value is bool boolValue && boolValue;

    private static GraphLibraryItem ToItem(CoreTask task)
    {
        var key = task.Metadata.TryGetValue(GraphLibraryKeyKey, out var value) ? value as string : null;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Graph library item '{task.Id}' is missing a key.");

        var plan = DeserializePlan(task.Payload)
            ?? throw new InvalidOperationException($"Graph library item '{task.Id}' payload is not a graph plan.");

        return new GraphLibraryItem(
            task.Id,
            key,
            task.Title,
            task.Description,
            plan,
            ReadParameters(task.Metadata),
            task.Metadata,
            task.CreatedAt);
    }

    private static IReadOnlyDictionary<string, object?> ToMetadata(TemplateParameter parameter)
    {
        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["name"] = parameter.Name,
            ["source"] = parameter.Source
        };
        if (!string.IsNullOrWhiteSpace(parameter.Format))
            values["format"] = parameter.Format;
        if (parameter.DefaultValue is not null)
            values["defaultValue"] = parameter.DefaultValue;
        return values;
    }

    private static IReadOnlyList<TemplateParameter> ReadParameters(IReadOnlyDictionary<string, object?> metadata)
    {
        if (!metadata.TryGetValue(GraphTemplateParametersKey, out var raw) || raw is not IEnumerable<object?> items)
            return [];
        return items
            .OfType<IReadOnlyDictionary<string, object?>>()
            .Select(item => new TemplateParameter(
                item.TryGetValue("name", out var name) ? name as string ?? string.Empty : string.Empty,
                item.TryGetValue("source", out var source) ? source as string ?? string.Empty : string.Empty,
                item.TryGetValue("format", out var format) ? format as string : null,
                item.TryGetValue("defaultValue", out var defaultValue) ? defaultValue : null))
            .Where(parameter => !string.IsNullOrWhiteSpace(parameter.Name) && !string.IsNullOrWhiteSpace(parameter.Source))
            .ToList();
    }

    private static string SerializePlan(GraphPlan plan) => JsonSerializer.Serialize(plan, JsonOptions);
    private static GraphPlan? DeserializePlan(string json) => JsonSerializer.Deserialize<GraphPlan>(json, JsonOptions);
}
