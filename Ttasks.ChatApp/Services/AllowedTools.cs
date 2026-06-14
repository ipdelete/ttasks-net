using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace Ttasks.ChatApp.Services;

public sealed record AllowedToolDefinition(
    string Id,
    string Prefix,
    string PrefixNormalized,
    string? Description,
    string? HelpCommand,
    IReadOnlyList<string>? Traits,
    bool Enabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record AllowedToolDraft(
    string Prefix,
    string? Description = null,
    string? HelpCommand = null,
    IReadOnlyList<string>? Traits = null,
    bool Enabled = true);

public interface IAllowedToolStore
{
    IReadOnlyList<AllowedToolDefinition> All();
    IReadOnlyList<AllowedToolDefinition> Enabled();
    AllowedToolDefinition? FindById(string id);
    AllowedToolDefinition? FindByNormalizedPrefix(string prefixNormalized);
    AllowedToolDefinition Add(AllowedToolDefinition definition, string actor, string action);
    AllowedToolDefinition Update(AllowedToolDefinition definition, string actor, string action);
    AllowedToolDefinition SetEnabled(string id, bool enabled, string actor, string action);
    bool HasSeenSeedPrefix(string prefixNormalized);
    void MarkSeedPrefixSeen(string prefixNormalized, string actor);
}

public sealed partial class AllowedToolRegistry
{
    private readonly IAllowedToolStore _store;

    public AllowedToolRegistry(IAllowedToolStore store)
    {
        _store = store;
    }

    public IReadOnlyList<AllowedToolDefinition> All() => _store.All();
    public IReadOnlyList<AllowedToolDefinition> Enabled() => _store.Enabled();

    public AllowedToolDefinition GetById(string id) =>
        _store.FindById(id)
        ?? throw new KeyNotFoundException($"Capability '{id}' was not found.");

    public AllowedToolDefinition Create(AllowedToolDraft draft, string actor)
    {
        var normalizedPrefix = NormalizePrefix(draft.Prefix);
        if (string.IsNullOrWhiteSpace(normalizedPrefix))
            throw new ArgumentException("Capability prefix is required.", nameof(draft));

        EnsureUniquePrefix(normalizedPrefix, exceptId: null);
        if (draft.Enabled)
            EnsureNoStrictPrefixOverlap(normalizedPrefix, exceptId: null);

        var now = DateTimeOffset.UtcNow;
        var definition = new AllowedToolDefinition(
            Guid.NewGuid().ToString("N"),
            normalizedPrefix,
            normalizedPrefix,
            string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description.Trim(),
            string.IsNullOrWhiteSpace(draft.HelpCommand) ? null : draft.HelpCommand.Trim(),
            NormalizeTraits(draft.Traits),
            draft.Enabled,
            now,
            now);
        return _store.Add(definition, actor, action: "create");
    }

    public AllowedToolDefinition Update(string id, AllowedToolDraft draft, string actor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var existing = GetById(id);
        var normalizedPrefix = NormalizePrefix(draft.Prefix);
        if (string.IsNullOrWhiteSpace(normalizedPrefix))
            throw new ArgumentException("Capability prefix is required.", nameof(draft));

        EnsureUniquePrefix(normalizedPrefix, exceptId: id);
        if (draft.Enabled)
            EnsureNoStrictPrefixOverlap(normalizedPrefix, exceptId: id);

        var updated = existing with
        {
            Prefix = normalizedPrefix,
            PrefixNormalized = normalizedPrefix,
            Description = string.IsNullOrWhiteSpace(draft.Description) ? null : draft.Description.Trim(),
            HelpCommand = string.IsNullOrWhiteSpace(draft.HelpCommand) ? null : draft.HelpCommand.Trim(),
            Traits = NormalizeTraits(draft.Traits),
            Enabled = draft.Enabled,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        return _store.Update(updated, actor, action: "update");
    }

    public AllowedToolDefinition SetEnabled(string id, bool enabled, string actor)
    {
        var existing = GetById(id);
        if (enabled)
            EnsureNoStrictPrefixOverlap(existing.PrefixNormalized, exceptId: id);
        return _store.SetEnabled(id, enabled, actor, enabled ? "enable" : "disable");
    }

    public void SeedDefaults(IEnumerable<AllowedToolConfig> seeds)
    {
        foreach (var seed in seeds)
        {
            var normalizedPrefix = NormalizePrefix(seed.Prefix);
            if (string.IsNullOrWhiteSpace(normalizedPrefix))
                continue;
            if (_store.HasSeenSeedPrefix(normalizedPrefix))
                continue;

            var existing = _store.FindByNormalizedPrefix(normalizedPrefix);
            if (existing is null)
            {
                Create(new AllowedToolDraft(seed.Prefix, seed.Description, seed.HelpCommand, seed.Traits, Enabled: true), "startup-seed");
            }

            _store.MarkSeedPrefixSeen(normalizedPrefix, "startup-seed");
        }
    }

    internal static string NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            return string.Empty;
        var collapsed = SpaceRegex().Replace(prefix.Trim(), " ");
        return collapsed.ToLowerInvariant();
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

            var collapsed = SpaceRegex().Replace(raw.Trim(), string.Empty)
                .ToLowerInvariant();
            if (collapsed.Length == 0)
                continue;
            if (seen.Add(collapsed))
                normalized.Add(collapsed);
        }

        return normalized.Count == 0 ? null : normalized;
    }

    private void EnsureUniquePrefix(string normalizedPrefix, string? exceptId)
    {
        var existing = _store.FindByNormalizedPrefix(normalizedPrefix);
        if (existing is null)
            return;
        if (exceptId is not null && string.Equals(existing.Id, exceptId, StringComparison.Ordinal))
            return;

        throw new InvalidOperationException($"A capability for prefix '{normalizedPrefix}' already exists.");
    }

    private void EnsureNoStrictPrefixOverlap(string normalizedPrefix, string? exceptId)
    {
        foreach (var tool in _store.Enabled())
        {
            if (exceptId is not null && string.Equals(tool.Id, exceptId, StringComparison.Ordinal))
                continue;

            var other = tool.PrefixNormalized;
            if (HasStrictPrefixOverlap(normalizedPrefix, other))
            {
                throw new InvalidOperationException(
                    $"Capability prefix '{normalizedPrefix}' overlaps with existing enabled prefix '{tool.Prefix}'. " +
                    "Strict prefix overlaps are not allowed.");
            }
        }
    }

    private static bool HasStrictPrefixOverlap(string left, string right) =>
        IsStrictPrefix(left, right) || IsStrictPrefix(right, left);

    private static bool IsStrictPrefix(string prefix, string full)
    {
        if (prefix.Length >= full.Length)
            return false;
        if (!full.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        return full[prefix.Length] == ' ';
    }

    [GeneratedRegex("\\s+")]
    private static partial Regex SpaceRegex();
}

public static class AllowedToolSeeder
{
    public static void Seed(AllowedToolRegistry registry, IEnumerable<AllowedToolConfig> seeds)
    {
        registry.SeedDefaults(seeds);
    }
}

public sealed class InMemoryAllowedToolStore : IAllowedToolStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AllowedToolDefinition> _byId = new(StringComparer.Ordinal);
    private readonly HashSet<string> _seenSeeds = new(StringComparer.Ordinal);

    public IReadOnlyList<AllowedToolDefinition> All()
    {
        lock (_gate)
        {
            return _byId.Values
                .OrderBy(tool => tool.PrefixNormalized, StringComparer.Ordinal)
                .ToList();
        }
    }

    public IReadOnlyList<AllowedToolDefinition> Enabled()
    {
        lock (_gate)
        {
            return _byId.Values
                .Where(tool => tool.Enabled)
                .OrderBy(tool => tool.PrefixNormalized, StringComparer.Ordinal)
                .ToList();
        }
    }

    public AllowedToolDefinition? FindById(string id)
    {
        lock (_gate)
            return _byId.TryGetValue(id, out var tool) ? tool : null;
    }

    public AllowedToolDefinition? FindByNormalizedPrefix(string prefixNormalized)
    {
        lock (_gate)
        {
            return _byId.Values.FirstOrDefault(tool =>
                string.Equals(tool.PrefixNormalized, prefixNormalized, StringComparison.Ordinal));
        }
    }

    public AllowedToolDefinition Add(AllowedToolDefinition definition, string actor, string action)
    {
        lock (_gate)
        {
            if (_byId.ContainsKey(definition.Id))
                throw new InvalidOperationException($"Capability '{definition.Id}' already exists.");
            if (_byId.Values.Any(tool => string.Equals(tool.PrefixNormalized, definition.PrefixNormalized, StringComparison.Ordinal)))
                throw new InvalidOperationException($"A capability for prefix '{definition.PrefixNormalized}' already exists.");
            _byId[definition.Id] = definition;
            return definition;
        }
    }

    public AllowedToolDefinition Update(AllowedToolDefinition definition, string actor, string action)
    {
        lock (_gate)
        {
            if (!_byId.ContainsKey(definition.Id))
                throw new KeyNotFoundException($"Capability '{definition.Id}' was not found.");

            if (_byId.Values.Any(tool =>
                    !string.Equals(tool.Id, definition.Id, StringComparison.Ordinal)
                    && string.Equals(tool.PrefixNormalized, definition.PrefixNormalized, StringComparison.Ordinal)))
            {
                throw new InvalidOperationException($"A capability for prefix '{definition.PrefixNormalized}' already exists.");
            }

            _byId[definition.Id] = definition;
            return definition;
        }
    }

    public AllowedToolDefinition SetEnabled(string id, bool enabled, string actor, string action)
    {
        lock (_gate)
        {
            if (!_byId.TryGetValue(id, out var existing))
                throw new KeyNotFoundException($"Capability '{id}' was not found.");
            var updated = existing with
            {
                Enabled = enabled,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            _byId[id] = updated;
            return updated;
        }
    }

    public bool HasSeenSeedPrefix(string prefixNormalized)
    {
        lock (_gate)
            return _seenSeeds.Contains(prefixNormalized);
    }

    public void MarkSeedPrefixSeen(string prefixNormalized, string actor)
    {
        lock (_gate)
            _seenSeeds.Add(prefixNormalized);
    }
}

public sealed class SqliteAllowedToolStore : IAllowedToolStore
{
    private readonly object _gate = new();
    private readonly string _path;

    public SqliteAllowedToolStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
        EnsureSchema();
    }

    public IReadOnlyList<AllowedToolDefinition> All()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, prefix, prefix_normalized, description, help_command, traits_json, enabled, created_at, updated_at
                FROM allowed_tools
                ORDER BY prefix_normalized, id;
                """;
            using var reader = command.ExecuteReader();
            var items = new List<AllowedToolDefinition>();
            while (reader.Read())
                items.Add(ReadTool(reader));
            return items;
        }
    }

    public IReadOnlyList<AllowedToolDefinition> Enabled()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, prefix, prefix_normalized, description, help_command, traits_json, enabled, created_at, updated_at
                FROM allowed_tools
                WHERE enabled = 1
                ORDER BY prefix_normalized, id;
                """;
            using var reader = command.ExecuteReader();
            var items = new List<AllowedToolDefinition>();
            while (reader.Read())
                items.Add(ReadTool(reader));
            return items;
        }
    }

    public AllowedToolDefinition? FindById(string id)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, prefix, prefix_normalized, description, help_command, traits_json, enabled, created_at, updated_at
                FROM allowed_tools
                WHERE id = $id
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$id", id);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadTool(reader) : null;
        }
    }

    public AllowedToolDefinition? FindByNormalizedPrefix(string prefixNormalized)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT id, prefix, prefix_normalized, description, help_command, traits_json, enabled, created_at, updated_at
                FROM allowed_tools
                WHERE prefix_normalized = $prefix_normalized
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$prefix_normalized", prefixNormalized);
            using var reader = command.ExecuteReader();
            return reader.Read() ? ReadTool(reader) : null;
        }
    }

    public AllowedToolDefinition Add(AllowedToolDefinition definition, string actor, string action)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO allowed_tools(
                        id, prefix, prefix_normalized, description, help_command, traits_json, enabled, created_at, updated_at)
                    VALUES(
                        $id, $prefix, $prefix_normalized, $description, $help_command, $traits_json, $enabled, $created_at, $updated_at);
                    """;
                command.Parameters.AddWithValue("$id", definition.Id);
                command.Parameters.AddWithValue("$prefix", definition.Prefix);
                command.Parameters.AddWithValue("$prefix_normalized", definition.PrefixNormalized);
                command.Parameters.AddWithValue("$description", DbValue(definition.Description));
                command.Parameters.AddWithValue("$help_command", DbValue(definition.HelpCommand));
                command.Parameters.AddWithValue("$traits_json", DbValue(SerializeTraits(definition.Traits)));
                command.Parameters.AddWithValue("$enabled", definition.Enabled ? 1 : 0);
                command.Parameters.AddWithValue("$created_at", Format(definition.CreatedAt));
                command.Parameters.AddWithValue("$updated_at", Format(definition.UpdatedAt));
                command.ExecuteNonQuery();
            }

            WriteAudit(connection, transaction, action, definition.Id, definition.Prefix, actor, null);
            transaction.Commit();
            return definition;
        }
    }

    public AllowedToolDefinition Update(AllowedToolDefinition definition, string actor, string action)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE allowed_tools
                    SET prefix = $prefix,
                        prefix_normalized = $prefix_normalized,
                        description = $description,
                        help_command = $help_command,
                        traits_json = $traits_json,
                        enabled = $enabled,
                        updated_at = $updated_at
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", definition.Id);
                command.Parameters.AddWithValue("$prefix", definition.Prefix);
                command.Parameters.AddWithValue("$prefix_normalized", definition.PrefixNormalized);
                command.Parameters.AddWithValue("$description", DbValue(definition.Description));
                command.Parameters.AddWithValue("$help_command", DbValue(definition.HelpCommand));
                command.Parameters.AddWithValue("$traits_json", DbValue(SerializeTraits(definition.Traits)));
                command.Parameters.AddWithValue("$enabled", definition.Enabled ? 1 : 0);
                command.Parameters.AddWithValue("$updated_at", Format(definition.UpdatedAt));
                if (command.ExecuteNonQuery() == 0)
                    throw new KeyNotFoundException($"Capability '{definition.Id}' was not found.");
            }

            WriteAudit(connection, transaction, action, definition.Id, definition.Prefix, actor, null);
            transaction.Commit();
            return definition;
        }
    }

    public AllowedToolDefinition SetEnabled(string id, bool enabled, string actor, string action)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var updatedAt = DateTimeOffset.UtcNow;

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    UPDATE allowed_tools
                    SET enabled = $enabled,
                        updated_at = $updated_at
                    WHERE id = $id;
                    """;
                command.Parameters.AddWithValue("$id", id);
                command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
                command.Parameters.AddWithValue("$updated_at", Format(updatedAt));
                if (command.ExecuteNonQuery() == 0)
                    throw new KeyNotFoundException($"Capability '{id}' was not found.");
            }

            var definition = FindByIdInTransaction(connection, transaction, id)
                ?? throw new KeyNotFoundException($"Capability '{id}' was not found.");
            WriteAudit(connection, transaction, action, definition.Id, definition.Prefix, actor, null);
            transaction.Commit();
            return definition;
        }
    }

    public bool HasSeenSeedPrefix(string prefixNormalized)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT 1
                FROM allowed_tool_seed_prefixes
                WHERE prefix_normalized = $prefix_normalized
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$prefix_normalized", prefixNormalized);
            return command.ExecuteScalar() is not null;
        }
    }

    public void MarkSeedPrefixSeen(string prefixNormalized, string actor)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO allowed_tool_seed_prefixes(prefix_normalized, first_seen_at)
                    VALUES($prefix_normalized, $first_seen_at)
                    ON CONFLICT(prefix_normalized) DO NOTHING;
                    """;
                command.Parameters.AddWithValue("$prefix_normalized", prefixNormalized);
                command.Parameters.AddWithValue("$first_seen_at", Format(DateTimeOffset.UtcNow));
                command.ExecuteNonQuery();
            }

            WriteAudit(connection, transaction, "seed_prefix_seen", null, prefixNormalized, actor, null);
            transaction.Commit();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        connection.Open();
        using var timeoutCommand = connection.CreateCommand();
        timeoutCommand.CommandText = "PRAGMA busy_timeout = 5000";
        timeoutCommand.ExecuteNonQuery();
        using var walCommand = connection.CreateCommand();
        walCommand.CommandText = "PRAGMA journal_mode = WAL";
        walCommand.ExecuteNonQuery();
        return connection;
    }

    private void EnsureSchema()
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            ExecuteNonQuery(
                connection,
                transaction,
                """
                CREATE TABLE IF NOT EXISTS allowed_tools (
                    id TEXT PRIMARY KEY,
                    prefix TEXT NOT NULL,
                    prefix_normalized TEXT NOT NULL UNIQUE,
                    description TEXT NULL,
                    help_command TEXT NULL,
                    traits_json TEXT NULL,
                    enabled INTEGER NOT NULL,
                    created_at TEXT NOT NULL,
                    updated_at TEXT NOT NULL
                );
                """);
            ExecuteNonQuery(
                connection,
                transaction,
                """
                CREATE TABLE IF NOT EXISTS allowed_tool_seed_prefixes (
                    prefix_normalized TEXT PRIMARY KEY,
                    first_seen_at TEXT NOT NULL
                );
                """);
            ExecuteNonQuery(
                connection,
                transaction,
                """
                CREATE TABLE IF NOT EXISTS allowed_tool_audit (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    action TEXT NOT NULL,
                    tool_id TEXT NULL,
                    prefix TEXT NULL,
                    actor TEXT NOT NULL,
                    details_json TEXT NULL,
                    created_at TEXT NOT NULL
                );
                """);
            transaction.Commit();
        }
    }

    private static void ExecuteNonQuery(SqliteConnection connection, SqliteTransaction transaction, string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static AllowedToolDefinition ReadTool(SqliteDataReader reader)
    {
        var traitsJson = reader.IsDBNull(reader.GetOrdinal("traits_json"))
            ? null
            : reader.GetString(reader.GetOrdinal("traits_json"));

        return new AllowedToolDefinition(
            reader.GetString(reader.GetOrdinal("id")),
            reader.GetString(reader.GetOrdinal("prefix")),
            reader.GetString(reader.GetOrdinal("prefix_normalized")),
            reader.IsDBNull(reader.GetOrdinal("description")) ? null : reader.GetString(reader.GetOrdinal("description")),
            reader.IsDBNull(reader.GetOrdinal("help_command")) ? null : reader.GetString(reader.GetOrdinal("help_command")),
            DeserializeTraits(traitsJson),
            reader.GetInt32(reader.GetOrdinal("enabled")) != 0,
            Parse(reader.GetString(reader.GetOrdinal("created_at"))),
            Parse(reader.GetString(reader.GetOrdinal("updated_at"))));
    }

    private static string? SerializeTraits(IReadOnlyList<string>? traits)
    {
        if (traits is null || traits.Count == 0)
            return null;
        return JsonSerializer.Serialize(traits);
    }

    private static IReadOnlyList<string>? DeserializeTraits(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        var values = JsonSerializer.Deserialize<List<string>>(json);
        return values is { Count: > 0 } ? values : null;
    }

    private static object DbValue(string? value) => value is null ? DBNull.Value : value;
    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);
    private static string Format(DateTimeOffset value) => value.ToString("O");

    private static void WriteAudit(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string action,
        string? toolId,
        string? prefix,
        string actor,
        string? detailsJson)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO allowed_tool_audit(action, tool_id, prefix, actor, details_json, created_at)
            VALUES($action, $tool_id, $prefix, $actor, $details_json, $created_at);
            """;
        command.Parameters.AddWithValue("$action", action);
        command.Parameters.AddWithValue("$tool_id", DbValue(toolId));
        command.Parameters.AddWithValue("$prefix", DbValue(prefix));
        command.Parameters.AddWithValue("$actor", actor);
        command.Parameters.AddWithValue("$details_json", DbValue(detailsJson));
        command.Parameters.AddWithValue("$created_at", Format(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    private static AllowedToolDefinition? FindByIdInTransaction(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string id)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT id, prefix, prefix_normalized, description, help_command, traits_json, enabled, created_at, updated_at
            FROM allowed_tools
            WHERE id = $id
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadTool(reader) : null;
    }
}
