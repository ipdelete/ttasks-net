using System.Text.RegularExpressions;
using System.Text.Json;
using System.Web;
using System.Globalization;
using Microsoft.Extensions.Options;
using Ttasks.Core;
using CoreTask = Ttasks.Core.Task;

namespace Ttasks.ChatApp.Services;

public sealed record CapabilityRequest(string SessionId, string UserMessage);

public sealed record CommandCapability(
    string Id,
    string DisplayName,
    string Description,
    TaskType TaskType,
    string Payload,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed record ToolCapability(
    string Kind,
    string ToolName,
    string DisplayName,
    string Description,
    string Policy,
    string Documentation,
    IReadOnlyDictionary<string, object?> Metadata);

public sealed record CapabilitySet(
    IReadOnlyList<CommandCapability> Capabilities,
    string EmptyMessage,
    IReadOnlyList<ToolCapability>? ToolCapabilities = null)
{
    public IReadOnlyList<ToolCapability> Tools { get; } = ToolCapabilities ?? [];

    private readonly Lazy<IReadOnlyDictionary<string, CommandCapability>> _byId = new(
        () => Capabilities.ToDictionary(capability => capability.Id, StringComparer.Ordinal));

    public IReadOnlyDictionary<string, CommandCapability> ById => _byId.Value;
}

public interface ICapabilityProvider
{
    CapabilitySet GetCapabilities(CapabilityRequest request);
}

public sealed class CompositeCapabilityProvider : ICapabilityProvider
{
    private readonly IReadOnlyList<ICapabilityProvider> _providers;

    public CompositeCapabilityProvider(IEnumerable<ICapabilityProvider> providers)
    {
        _providers = providers.ToList();
    }

    public CapabilitySet GetCapabilities(CapabilityRequest request)
    {
        var sets = _providers
            .Select(provider => provider.GetCapabilities(request))
            .ToList();
        var capabilities = sets
            .SelectMany(set => set.Capabilities)
            .Select((capability, index) => Renumber(capability, index))
            .ToList();
        var tools = sets
            .SelectMany(set => set.Tools)
            .ToList();

        return new CapabilitySet(
            capabilities,
            "I can plan action graphs only from host-approved capabilities. This turn did not include a supported Teams chat/channel ID/link, mail request, calendar request, or Azure inventory request.",
            tools);
    }

    private static CommandCapability Renumber(CommandCapability capability, int index)
    {
        var id = $"cap-{index + 1}";
        var metadata = new Dictionary<string, object?>(capability.Metadata, StringComparer.Ordinal)
        {
            ["capabilityId"] = id
        };

        return capability with { Id = id, Metadata = metadata };
    }
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
    TaskType TaskType,
    string PayloadTemplate,
    IReadOnlyList<TemplateParameter> Parameters,
    IReadOnlyDictionary<string, object?> Metadata,
    string? ProcessFileName = null,
    IReadOnlyList<string>? ProcessArgsTemplate = null);

public sealed record TaskLibraryItem(
    string Id,
    string Key,
    string DisplayName,
    string Description,
    TaskType TaskType,
    string PayloadTemplate,
    IReadOnlyList<TemplateParameter> Parameters,
    IReadOnlyDictionary<string, object?> Metadata,
    DateTimeOffset CreatedAt,
    string? ProcessFileName = null,
    IReadOnlyList<string>? ProcessArgsTemplate = null);

public interface ITaskLibrary
{
    TaskLibraryItem GetOrAdd(TaskLibraryDefinition definition);
    IReadOnlyList<TaskLibraryItem> All();
}

public interface IToolDocumentationProvider
{
    string GetHelp(string toolName);
}

public sealed class ShellToolDocumentationProvider : IToolDocumentationProvider
{
    public string GetHelp(string toolName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(toolName);
        return TaskExecutor.WithBuiltInHandlers()
            .Execute(CoreTask.Powershell($"{toolName} --help", timeout: 10))
            .Output;
    }
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

        var existing = All().FirstOrDefault(item => string.Equals(item.Key, definition.Key, StringComparison.Ordinal));
        if (existing is not null)
            return UpdateIfChanged(existing, definition);

        var metadata = new Dictionary<string, object?>(definition.Metadata, StringComparer.Ordinal)
        {
            [IsLibraryItemKey] = true,
            [LibraryKeyKey] = definition.Key,
            [TemplateParametersKey] = definition.Parameters.Select(ToMetadata).ToList()
        };
        var task = CreateTemplateTask(definition, metadata);
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
        if (SamePayloadTemplate(existing, definition)
            && existing.DisplayName == definition.DisplayName
            && existing.Description == definition.Description
            && existing.ProcessFileName == definition.ProcessFileName
            && SameStringList(existing.ProcessArgsTemplate, definition.ProcessArgsTemplate)
            && SameParameters(existing.Parameters, definition.Parameters)
            && MetadataContains(existing.Metadata, definition.Metadata))
            return existing;

        var task = _store.Tasks.Get(existing.Id);
        task.Title = definition.DisplayName;
        task.Description = definition.Description;
        task.Payload = CreateTemplateTask(definition, task.Metadata).Payload;
        task.SetMetadata(TemplateParametersKey, definition.Parameters.Select(ToMetadata).ToList());
        SetOptionalMetadata(task, ProcessFileNameKey, definition.ProcessFileName);
        SetOptionalMetadata(task, ProcessArgsTemplateKey, definition.ProcessArgsTemplate);
        foreach (var entry in definition.Metadata)
            task.SetMetadata(entry.Key, entry.Value);
        _store.Tasks.Save(task);
        return ToItem(task);
    }

    private static bool SamePayloadTemplate(TaskLibraryItem existing, TaskLibraryDefinition definition) =>
        definition.TaskType == TaskType.Process
            || existing.PayloadTemplate == definition.PayloadTemplate;

    private static bool SameParameters(IReadOnlyList<TemplateParameter> left, IReadOnlyList<TemplateParameter> right)
    {
        if (left.Count != right.Count)
            return false;

        return left.Zip(right).All(pair =>
            pair.First.Name == pair.Second.Name
            && pair.First.Source == pair.Second.Source
            && pair.First.Format == pair.Second.Format
            && SameValue(pair.First.DefaultValue, pair.Second.DefaultValue));
    }

    private static bool SameStringList(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
    {
        left ??= [];
        right ??= [];
        return left.SequenceEqual(right, StringComparer.Ordinal);
    }

    private static bool SameValue(object? left, object? right) =>
        (left, right) switch
        {
            (null, null) => true,
            (null, _) or (_, null) => false,
            (IConvertible leftValue, IConvertible rightValue)
                when IsNumber(left) && IsNumber(right) => Convert.ToDecimal(leftValue, CultureInfo.InvariantCulture) == Convert.ToDecimal(rightValue, CultureInfo.InvariantCulture),
            _ => Equals(left, right)
        };

    private static bool IsNumber(object value) =>
        value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal;

    private static bool MetadataContains(IReadOnlyDictionary<string, object?> existing, IReadOnlyDictionary<string, object?> expected) =>
        expected.All(entry => existing.TryGetValue(entry.Key, out var value) && SameMetadataValue(value, entry.Value));

    private static bool SameMetadataValue(object? left, object? right)
    {
        if (left is IEnumerable<object?> leftList && right is IEnumerable<object?> rightList)
            return leftList.SequenceEqual(rightList);

        if (left is IEnumerable<string> leftStrings && right is IEnumerable<string> rightStrings)
            return leftStrings.SequenceEqual(rightStrings);

        return SameValue(left, right);
    }

    private static CoreTask CreateTemplateTask(TaskLibraryDefinition definition, IReadOnlyDictionary<string, object?> metadata) =>
        definition.TaskType switch
        {
            TaskType.Powershell => CoreTask.Powershell(definition.PayloadTemplate, definition.DisplayName, definition.Description, metadata: metadata),
            TaskType.Prompt => CoreTask.Prompt(definition.PayloadTemplate, definition.DisplayName, definition.Description, metadata: metadata),
            TaskType.Bash => CoreTask.Bash(definition.PayloadTemplate, definition.DisplayName, definition.Description, metadata: metadata),
            TaskType.Agent => CoreTask.Agent(definition.PayloadTemplate, definition.DisplayName, definition.Description, metadata: metadata),
            TaskType.Process => CoreTask.Process(
                new ProcessCommand(
                    definition.ProcessFileName ?? throw new ArgumentException("Process task library definitions require a process file name."),
                    definition.ProcessArgsTemplate ?? []),
                definition.DisplayName,
                definition.Description,
                metadata: AddProcessMetadata(metadata, definition)),
            _ => throw new ArgumentException($"Unsupported library task type '{definition.TaskType}'.")
        };

    private static IReadOnlyDictionary<string, object?> AddProcessMetadata(IReadOnlyDictionary<string, object?> metadata, TaskLibraryDefinition definition)
    {
        var values = new Dictionary<string, object?>(metadata, StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(definition.ProcessFileName))
            values[ProcessFileNameKey] = definition.ProcessFileName;
        if (definition.ProcessArgsTemplate is not null)
            values[ProcessArgsTemplateKey] = definition.ProcessArgsTemplate.ToList();
        return values;
    }

    private static void SetOptionalMetadata(CoreTask task, string key, object? value)
    {
        if (value is null)
            task.RemoveMetadata(key);
        else
            task.SetMetadata(key, value);
    }

    private static bool IsLibraryTask(CoreTask task) =>
        task.Metadata.TryGetValue(IsLibraryItemKey, out var value) && value is bool boolValue && boolValue;

    private static TaskLibraryItem ToItem(CoreTask task)
    {
        var key = task.Metadata.TryGetValue(LibraryKeyKey, out var value) ? value as string : null;
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Task library item '{task.Id}' is missing a library key.");

        return new TaskLibraryItem(
            task.Id,
            key,
            task.Title,
            task.Description,
            task.Type,
            task.Payload,
            ReadParameters(task.Metadata),
            task.Metadata,
            task.CreatedAt,
            ReadString(task.Metadata, ProcessFileNameKey),
            ReadStringList(task.Metadata, ProcessArgsTemplateKey));
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
    private static readonly Regex TokenPattern = new(@"\{(?<name>[A-Za-z0-9_.-]+)(:(?<format>[^}]+))?\}", RegexOptions.Compiled);
    private readonly TimeProvider _timeProvider;

    public TaskLibraryTemplateRenderer()
        : this(TimeProvider.System)
    {
    }

    public TaskLibraryTemplateRenderer(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public string Render(TaskLibraryItem item) =>
        TokenPattern.Replace(item.PayloadTemplate, match =>
        {
            var name = match.Groups["name"].Value;
            var parameter = item.Parameters.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (parameter is null)
                throw new InvalidOperationException($"Template '{item.Key}' references unknown parameter '{name}'.");

            var format = match.Groups["format"].Success ? match.Groups["format"].Value : parameter.Format;
            return FormatValue(ResolveValue(item, parameter), format);
        });

    public ProcessCommand RenderProcess(TaskLibraryItem item)
    {
        if (item.TaskType != TaskType.Process)
            throw new InvalidOperationException($"Template '{item.Key}' is not a process template.");
        if (string.IsNullOrWhiteSpace(item.ProcessFileName))
            throw new InvalidOperationException($"Process template '{item.Key}' is missing a process file name.");

        return new ProcessCommand(
            item.ProcessFileName,
            (item.ProcessArgsTemplate ?? []).Select(arg => RenderText(item, arg)).ToList());
    }

    private string RenderText(TaskLibraryItem item, string template) =>
        TokenPattern.Replace(template, match =>
        {
            var name = match.Groups["name"].Value;
            var parameter = item.Parameters.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (parameter is null)
                throw new InvalidOperationException($"Template '{item.Key}' references unknown parameter '{name}'.");

            var format = match.Groups["format"].Success ? match.Groups["format"].Value : parameter.Format;
            return FormatValue(ResolveValue(item, parameter), format);
        });

    private object? ResolveValue(TaskLibraryItem item, TemplateParameter parameter) =>
        parameter.Source switch
        {
            "clock.now" => _timeProvider.GetLocalNow(),
            "clock.yesterday" => _timeProvider.GetLocalNow().AddDays(-1),
            "clock.tomorrow" => _timeProvider.GetLocalNow().AddDays(1),
            "default" => parameter.DefaultValue,
            var source when source.StartsWith("metadata:", StringComparison.Ordinal) =>
                item.Metadata.TryGetValue(source["metadata:".Length..], out var value) ? value : parameter.DefaultValue,
            _ => throw new InvalidOperationException($"Unsupported template parameter source '{parameter.Source}'.")
        };

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

public sealed partial class TeamsCapabilityProvider : ICapabilityProvider
{
    private static readonly Regex TeamsChatUrlPattern = new(@"https://teams\.microsoft\.com/l/chat/(?<id>[^/]+)/conversations", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex DirectChatIdPattern = new(@"(?<![A-Za-z0-9_.:-])(?<id>48:[A-Za-z0-9_.:-]+|19:[^\s<>,]+@thread\.(?:v2|tacv2))(?![A-Za-z0-9_.:-])", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ITaskLibrary _library;
    private readonly TaskLibraryTemplateRenderer _renderer;
    private readonly ITeamsChatMetadataResolver _chatMetadata;
    private readonly ChatAppOptions _options;

    public TeamsCapabilityProvider(ITaskLibrary library, TaskLibraryTemplateRenderer renderer, ITeamsChatMetadataResolver chatMetadata, IOptions<ChatAppOptions> options)
    {
        _library = library;
        _renderer = renderer;
        _chatMetadata = chatMetadata;
        _options = options.Value;
    }

    public CapabilitySet GetCapabilities(CapabilityRequest request)
    {
        var explicitTargets = ExtractTargets(request.UserMessage);
        var itemsByKey = explicitTargets
            .Select(target => _library.GetOrAdd(ToDefinition(target)))
            .Concat(FindAliasMatches(request.UserMessage))
            .GroupBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();
        var capabilities = itemsByKey
            .Select(ToCapability)
            .ToList();

        return new CapabilitySet(
            capabilities,
            "I can run Teams read graphs, but I need explicit Teams chat links or chat IDs in the message so the host can expose a safe capability.");
    }

    public IReadOnlyList<string> ExtractTargets(string userMessage)
    {
        var chatIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in TeamsChatUrlPattern.Matches(userMessage))
            chatIds.Add(HttpUtility.UrlDecode(match.Groups["id"].Value));

        foreach (Match match in DirectChatIdPattern.Matches(userMessage))
            chatIds.Add(match.Groups["id"].Value.TrimEnd('.', ')', ']', '}'));

        return chatIds.OrderBy(id => id, StringComparer.Ordinal).ToList();
    }

    private TaskLibraryDefinition ToDefinition(string chatId)
    {
        var chat = _chatMetadata.Resolve(chatId);
        var title = string.IsNullOrWhiteSpace(chat.Topic) ? chatId : chat.Topic;
        var payloadTemplate = "teams read {chatId} -n {maxMessages} --json";
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["capabilityKind"] = "teams.read",
            ["teamsChatId"] = chatId,
            ["teamsChatAliases"] = CreateAliases(title)
        };
        if (!string.IsNullOrWhiteSpace(chat.Topic))
            metadata["teamsChatTopic"] = chat.Topic;
        if (!string.IsNullOrWhiteSpace(chat.Error))
            metadata["teamsChatMetadataError"] = chat.Error;

        return new TaskLibraryDefinition(
            $"teams.chat.read:{chatId}",
            $"Read Teams chat {title}",
            "Read messages from a user-provided Teams chat.",
            TaskType.Powershell,
            payloadTemplate,
            [
                new TemplateParameter("chatId", "metadata:teamsChatId"),
                new TemplateParameter("maxMessages", "default", DefaultValue: _options.MaxTeamsReadMessages)
            ],
            metadata);
    }

    private IEnumerable<TaskLibraryItem> FindAliasMatches(string userMessage)
    {
        var normalizedMessage = NormalizeAlias(userMessage);
        if (string.IsNullOrWhiteSpace(normalizedMessage))
            return [];

        return _library.All()
            .Where(item => item.Metadata.TryGetValue("capabilityKind", out var kind) && string.Equals(kind as string, "teams.read", StringComparison.Ordinal))
            .Where(item => ReadAliases(item.Metadata).Any(alias => ContainsAlias(normalizedMessage, alias)));
    }

    private CommandCapability ToCapability(TaskLibraryItem item)
    {
        var metadata = new Dictionary<string, object?>(item.Metadata, StringComparer.Ordinal)
        {
            ["libraryTaskId"] = item.Id
        };

        return new CommandCapability(
            "pending",
            item.DisplayName,
            item.Description,
            item.TaskType,
            _renderer.Render(item),
            metadata);
    }

    internal static string NormalizeAlias(string value)
    {
        var normalized = AliasTokenPattern().Replace(value.ToLowerInvariant(), " ");
        return WhitespacePattern().Replace(normalized, " ").Trim();
    }

    private static IReadOnlyList<string> CreateAliases(string value)
    {
        var alias = NormalizeAlias(value);
        return string.IsNullOrWhiteSpace(alias) ? [] : [alias];
    }

    private static IReadOnlyList<string> ReadAliases(IReadOnlyDictionary<string, object?> metadata)
    {
        if (!metadata.TryGetValue("teamsChatAliases", out var raw))
            return [];

        if (raw is IEnumerable<object?> values)
            return values.OfType<string>().Where(alias => !string.IsNullOrWhiteSpace(alias)).ToList();

        if (raw is IEnumerable<string> strings)
            return strings.Where(alias => !string.IsNullOrWhiteSpace(alias)).ToList();

        return [];
    }

    private static bool ContainsAlias(string normalizedMessage, string alias) =>
        normalizedMessage.Contains(alias, StringComparison.Ordinal);

    [GeneratedRegex("[^\\p{L}\\p{Nd}]+")]
    private static partial Regex AliasTokenPattern();

    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespacePattern();
}

public sealed record TeamsChatMetadata(string ChatId, string? Topic = null, string? Error = null);

public interface ITeamsChatMetadataResolver
{
    TeamsChatMetadata Resolve(string chatId);
}

public sealed class ShellTeamsChatMetadataResolver : ITeamsChatMetadataResolver
{
    public TeamsChatMetadata Resolve(string chatId)
    {
        try
        {
            var output = TaskExecutor.WithBuiltInHandlers()
                .Execute(CoreTask.Powershell($"teams chat-get {chatId} --json", timeout: 10))
                .Output;
            if (string.IsNullOrWhiteSpace(output))
                return new TeamsChatMetadata(chatId, Error: "teams chat-get returned no output.");

            using var document = JsonDocument.Parse(output);
            var topic = document.RootElement.TryGetProperty("topic", out var property) ? property.GetString() : null;
            return new TeamsChatMetadata(chatId, topic);
        }
        catch (Exception ex) when (ex is InvalidOperationException or TaskExecutionException or JsonException)
        {
            return new TeamsChatMetadata(chatId, Error: ex.Message);
        }
    }
}

public sealed partial class MailToolCapabilityProvider : ICapabilityProvider
{
    private static readonly Regex MailPattern = new(@"\b(mail|email|emails|message|messages|inbox)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly IToolDocumentationProvider _documentation;

    public MailToolCapabilityProvider(IToolDocumentationProvider documentation)
    {
        _documentation = documentation;
    }

    public CapabilitySet GetCapabilities(CapabilityRequest request)
    {
        if (!MailPattern.IsMatch(request.UserMessage))
        {
            return new CapabilitySet(
                [],
                "I can expose the mail tool when the request asks about mail, email, messages, or the inbox.");
        }

        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["capabilityKind"] = "mail",
            ["capabilityPolicy"] = "full",
            ["toolName"] = "mail"
        };

        return new CapabilitySet(
            [],
            "I can expose the mail tool when the request asks about mail, email, messages, or the inbox.",
            [
                new ToolCapability(
                    "mail",
                    "mail",
                    "Use mail CLI",
                    "Use the full mail CLI surface based on mail --help to create or reuse task-library items.",
                    "full",
                    _documentation.GetHelp("mail"),
                    metadata)
            ]);
    }
}

public sealed partial class CalendarTodayCapabilityProvider : ICapabilityProvider
{
    private static readonly Regex TodayCalendarPattern = new(@"\b(today'?s|todays)\s+(calendar|schedule|events|meetings)\b|\b(calendar|schedule|events|meetings)\s+(today|for today)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ITaskLibrary _library;
    private readonly TaskLibraryTemplateRenderer _renderer;

    public CalendarTodayCapabilityProvider(ITaskLibrary library, TaskLibraryTemplateRenderer renderer)
    {
        _library = library;
        _renderer = renderer;
    }

    public CapabilitySet GetCapabilities(CapabilityRequest request)
    {
        if (!TodayCalendarPattern.IsMatch(request.UserMessage))
        {
            return new CapabilitySet(
                [],
                "I can expose today's calendar when the request asks for today's calendar, schedule, events, or meetings.");
        }

        var item = _library.GetOrAdd(ToDefinition());
        var metadata = new Dictionary<string, object?>(item.Metadata, StringComparer.Ordinal)
        {
            ["libraryTaskId"] = item.Id
        };

        return new CapabilitySet(
            [
                new CommandCapability(
                    "pending",
                    item.DisplayName,
                    item.Description,
                    item.TaskType,
                    _renderer.Render(item),
                    metadata)
            ],
            "I can expose today's calendar when the request asks for today's calendar, schedule, events, or meetings.");
    }

    private static TaskLibraryDefinition ToDefinition()
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["capabilityKind"] = "calendar.today"
        };

        return new TaskLibraryDefinition(
            "calendar.today",
            "Read today's calendar",
            "List calendar events for today and return them as JSON.",
            TaskType.Powershell,
            "calendar list -s {today:yyyy-MM-dd}T00:00:00 -e {tomorrow:yyyy-MM-dd}T00:00:00 -n {top} --json",
            [
                new TemplateParameter("today", "clock.now"),
                new TemplateParameter("tomorrow", "clock.tomorrow"),
                new TemplateParameter("top", "default", DefaultValue: 10)
            ],
            metadata);
    }
}

public sealed partial class AzureInventoryCapabilityProvider : ICapabilityProvider
{
    private static readonly Regex AzurePattern = new(@"\b(az|azure)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex SubscriptionPattern = new(@"\b(subscriptions?|accounts?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ResourceGroupPattern = new(@"\bresource\s+groups?\b|\brgs?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ResourcePattern = new(@"\bresources?\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private readonly ITaskLibrary _library;
    private readonly TaskLibraryTemplateRenderer _renderer;

    public AzureInventoryCapabilityProvider(ITaskLibrary library, TaskLibraryTemplateRenderer renderer)
    {
        _library = library;
        _renderer = renderer;
    }

    public CapabilitySet GetCapabilities(CapabilityRequest request)
    {
        if (!AzurePattern.IsMatch(request.UserMessage))
        {
            return new CapabilitySet(
                [],
                "I can expose read-only Azure inventory for subscriptions, resource groups, and resources.");
        }

        var definitions = MatchingDefinitions(request.UserMessage).ToList();
        if (definitions.Count == 0)
        {
            return new CapabilitySet(
                [],
                "I can expose read-only Azure inventory when the request asks for Azure subscriptions, resource groups, or resources.");
        }

        var capabilities = definitions
            .Select(definition => _library.GetOrAdd(definition))
            .Select(ToCapability)
            .ToList();

        return new CapabilitySet(
            capabilities,
            "I can expose read-only Azure inventory when the request asks for Azure subscriptions, resource groups, or resources.");
    }

    private static IEnumerable<TaskLibraryDefinition> MatchingDefinitions(string userMessage)
    {
        if (SubscriptionPattern.IsMatch(userMessage))
            yield return ToDefinition("az.account.list", "List Azure subscriptions", "List accessible Azure subscriptions as JSON.", "az account list --only-show-errors --output json");

        var wantsResourceGroups = ResourceGroupPattern.IsMatch(userMessage);
        if (wantsResourceGroups)
            yield return ToDefinition("az.group.list", "List Azure resource groups", "List Azure resource groups in the active subscription as JSON.", "az group list --only-show-errors --output json");

        if (ResourcePattern.IsMatch(userMessage) && !wantsResourceGroups)
            yield return ToDefinition("az.resource.list", "List Azure resources", "List Azure resources in the active subscription as JSON.", "az resource list --only-show-errors --output json");
    }

    private CommandCapability ToCapability(TaskLibraryItem item)
    {
        var metadata = new Dictionary<string, object?>(item.Metadata, StringComparer.Ordinal)
        {
            ["libraryTaskId"] = item.Id
        };

        return new CommandCapability(
            "pending",
            item.DisplayName,
            item.Description,
            item.TaskType,
            _renderer.Render(item),
            metadata);
    }

    private static TaskLibraryDefinition ToDefinition(string key, string displayName, string description, string payload)
    {
        var metadata = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["capabilityKind"] = key
        };

        return new TaskLibraryDefinition(
            key,
            displayName,
            description,
            TaskType.Powershell,
            payload,
            [],
            metadata);
    }
}
