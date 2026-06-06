using Microsoft.Data.Sqlite;
using CoreTask = Ttasks.Core.Task;

namespace Ttasks.Core;

public sealed class SqliteStoreOptions
{
    public bool AllowDestructiveMigration { get; init; }
    public Action<string>? WarningSink { get; init; }
}

public sealed class SqliteStoreSchemaException : InvalidOperationException
{
    public SqliteStoreSchemaException(string message)
        : base(message)
    {
    }
}

public sealed class SqliteStore : ITaskStore, IDisposable
{
    private static readonly string[] KnownTables = ["metadata", "tasks", "graphs", "graph_nodes", "graph_edges"];
    private readonly object _gate = new();
    private readonly string _path;

    public const int CurrentSchemaVersion = 2;

    public SqliteStore(string path, SqliteStoreOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        _path = path;
        Tasks = new SqliteTaskCollection(this);
        Graphs = new SqliteGraphCollection(this);
        EnsureSchema(options ?? new SqliteStoreOptions());
    }

    public SqliteTaskCollection Tasks { get; }
    public SqliteGraphCollection Graphs { get; }

    ITaskCollection ITaskStore.Tasks => Tasks;
    IGraphCollection ITaskStore.Graphs => Graphs;

    public void Dispose()
    {
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection($"Data Source={_path};Pooling=False");
        connection.Open();
        ExecuteNonQuery(connection, "PRAGMA foreign_keys = ON");
        ExecuteNonQuery(connection, "PRAGMA busy_timeout = 5000");
        ExecuteNonQuery(connection, "PRAGMA journal_mode = WAL");
        return connection;
    }

    private void EnsureSchema(SqliteStoreOptions options)
    {
        lock (_gate)
        {
            using var connection = OpenConnection();
            var knownTables = GetKnownTables(connection).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (knownTables.Count == 0)
            {
                CreateSchema(connection);
                return;
            }

            var version = TryReadSchemaVersion(connection);
            if (version == CurrentSchemaVersion)
                return;

            if (!options.AllowDestructiveMigration)
            {
                var observed = version is null ? "missing" : version.Value.ToString();
                throw new SqliteStoreSchemaException(
                    $"SQLite store schema version mismatch: expected {CurrentSchemaVersion}, observed {observed}. " +
                    "Set AllowDestructiveMigration to rebuild the store.");
            }

            options.WarningSink?.Invoke("Destructive SQLite store migration requested; existing ttasks tables will be rebuilt.");
            DropKnownTables(connection);
            CreateSchema(connection);
        }
    }

    private static IReadOnlyList<string> GetKnownTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
        using var reader = command.ExecuteReader();
        var tables = new List<string>();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (KnownTables.Contains(name, StringComparer.OrdinalIgnoreCase))
                tables.Add(name);
        }

        return tables;
    }

    private static int? TryReadSchemaVersion(SqliteConnection connection)
    {
        if (!GetKnownTables(connection).Contains("metadata", StringComparer.OrdinalIgnoreCase))
            return null;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version'";
        var value = command.ExecuteScalar();
        return value is null || value == DBNull.Value ? null : int.Parse((string)value);
    }

    private static void DropKnownTables(SqliteConnection connection)
    {
        foreach (var table in KnownTables.Reverse())
            ExecuteNonQuery(connection, $"DROP TABLE IF EXISTS {table}");
    }

    private static void CreateSchema(SqliteConnection connection)
    {
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS metadata (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """);
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS tasks (
                id TEXT PRIMARY KEY,
                type INTEGER NOT NULL,
                payload TEXT NOT NULL,
                title TEXT NOT NULL,
                description TEXT NOT NULL,
                timeout INTEGER NULL,
                metadata_json TEXT NOT NULL DEFAULT '{}',
                status INTEGER NOT NULL,
                error TEXT NULL,
                blocked_by TEXT NULL,
                created_at TEXT NOT NULL,
                result_status INTEGER NULL,
                result_started_at TEXT NULL,
                result_finished_at TEXT NULL,
                result_output TEXT NULL,
                result_error TEXT NULL,
                result_return_code INTEGER NULL,
                result_termination_reason TEXT NULL
            );
            """);
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS graphs (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                metadata_json TEXT NOT NULL DEFAULT '{}',
                created_at TEXT NOT NULL
            );
            """);
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS graph_nodes (
                graph_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                position INTEGER NOT NULL,
                is_finally INTEGER NOT NULL,
                is_required INTEGER NOT NULL,
                PRIMARY KEY (graph_id, task_id),
                FOREIGN KEY (graph_id) REFERENCES graphs(id) ON DELETE CASCADE,
                FOREIGN KEY (task_id) REFERENCES tasks(id)
            );
            """);
        ExecuteNonQuery(
            connection,
            """
            CREATE TABLE IF NOT EXISTS graph_edges (
                graph_id TEXT NOT NULL,
                task_id TEXT NOT NULL,
                dependency_id TEXT NOT NULL,
                position INTEGER NOT NULL,
                PRIMARY KEY (graph_id, task_id, dependency_id),
                FOREIGN KEY (graph_id, task_id) REFERENCES graph_nodes(graph_id, task_id) ON DELETE CASCADE,
                FOREIGN KEY (dependency_id) REFERENCES tasks(id)
            );
            """);
        ExecuteNonQuery(
            connection,
            """
            INSERT INTO metadata(key, value)
            VALUES ('schema_version', $version)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """,
            ("$version", CurrentSchemaVersion.ToString()));
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string Format(DateTimeOffset value) => value.ToString("O");

    private static DateTimeOffset ParseDateTimeOffset(string value) => DateTimeOffset.Parse(value, null, System.Globalization.DateTimeStyles.RoundtripKind);

    private static object DbValue<T>(T? value) => value is null ? DBNull.Value : value;

    private void SaveTask(SqliteConnection connection, SqliteTransaction? transaction, CoreTask task)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            INSERT INTO tasks(
                id, type, payload, title, description, timeout, metadata_json, status, error, blocked_by, created_at,
                result_status, result_started_at, result_finished_at, result_output, result_error, result_return_code, result_termination_reason)
            VALUES (
                $id, $type, $payload, $title, $description, $timeout, $metadata_json, $status, $error, $blocked_by, $created_at,
                $result_status, $result_started_at, $result_finished_at, $result_output, $result_error, $result_return_code, $result_termination_reason)
            ON CONFLICT(id) DO UPDATE SET
                type = excluded.type,
                payload = excluded.payload,
                title = excluded.title,
                description = excluded.description,
                timeout = excluded.timeout,
                metadata_json = excluded.metadata_json,
                status = excluded.status,
                error = excluded.error,
                blocked_by = excluded.blocked_by,
                created_at = excluded.created_at,
                result_status = excluded.result_status,
                result_started_at = excluded.result_started_at,
                result_finished_at = excluded.result_finished_at,
                result_output = excluded.result_output,
                result_error = excluded.result_error,
                result_return_code = excluded.result_return_code,
                result_termination_reason = excluded.result_termination_reason;
            """;
        command.Parameters.AddWithValue("$id", task.Id);
        command.Parameters.AddWithValue("$type", (int)task.Type);
        command.Parameters.AddWithValue("$payload", task.Payload);
        command.Parameters.AddWithValue("$title", task.Title);
        command.Parameters.AddWithValue("$description", task.Description);
        command.Parameters.AddWithValue("$timeout", DbValue(task.Timeout));
        command.Parameters.AddWithValue("$metadata_json", MetadataValues.Serialize(task.Metadata));
        command.Parameters.AddWithValue("$status", (int)task.Status);
        command.Parameters.AddWithValue("$error", DbValue(task.Error));
        command.Parameters.AddWithValue("$blocked_by", DbValue(task.BlockedBy));
        command.Parameters.AddWithValue("$created_at", Format(task.CreatedAt));
        command.Parameters.AddWithValue("$result_status", DbValue(task.Result is null ? null : (int?)task.Result.Status));
        command.Parameters.AddWithValue("$result_started_at", DbValue(task.Result is null ? null : Format(task.Result.StartedAt)));
        command.Parameters.AddWithValue("$result_finished_at", DbValue(task.Result is null ? null : Format(task.Result.FinishedAt)));
        command.Parameters.AddWithValue("$result_output", DbValue(task.Result?.Output));
        command.Parameters.AddWithValue("$result_error", DbValue(task.Result?.Error));
        command.Parameters.AddWithValue("$result_return_code", DbValue(task.Result?.ReturnCode));
        command.Parameters.AddWithValue("$result_termination_reason", DbValue(task.Result?.TerminationReason));
        command.ExecuteNonQuery();
    }

    private CoreTask LoadTask(SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT * FROM tasks WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        using var reader = command.ExecuteReader();
        if (!reader.Read())
            throw new KeyNotFoundException($"Task '{id}' was not found.");

        TaskResult? result = null;
        if (!reader.IsDBNull(reader.GetOrdinal("result_status")))
        {
            result = new TaskResult
            {
                TaskId = reader.GetString(reader.GetOrdinal("id")),
                Status = (TaskStatus)reader.GetInt32(reader.GetOrdinal("result_status")),
                StartedAt = ParseDateTimeOffset(reader.GetString(reader.GetOrdinal("result_started_at"))),
                FinishedAt = ParseDateTimeOffset(reader.GetString(reader.GetOrdinal("result_finished_at"))),
                Output = reader.IsDBNull(reader.GetOrdinal("result_output")) ? string.Empty : reader.GetString(reader.GetOrdinal("result_output")),
                Error = reader.IsDBNull(reader.GetOrdinal("result_error")) ? null : reader.GetString(reader.GetOrdinal("result_error")),
                ReturnCode = reader.IsDBNull(reader.GetOrdinal("result_return_code")) ? null : reader.GetInt32(reader.GetOrdinal("result_return_code")),
                TerminationReason = reader.IsDBNull(reader.GetOrdinal("result_termination_reason")) ? null : reader.GetString(reader.GetOrdinal("result_termination_reason")),
                Raw = null
            };
        }

        return CoreTask.Restore(
            reader.GetString(reader.GetOrdinal("id")),
            (TaskType)reader.GetInt32(reader.GetOrdinal("type")),
            reader.GetString(reader.GetOrdinal("payload")),
            reader.GetString(reader.GetOrdinal("title")),
            reader.GetString(reader.GetOrdinal("description")),
            reader.IsDBNull(reader.GetOrdinal("timeout")) ? null : reader.GetInt32(reader.GetOrdinal("timeout")),
            (TaskStatus)reader.GetInt32(reader.GetOrdinal("status")),
            reader.IsDBNull(reader.GetOrdinal("error")) ? null : reader.GetString(reader.GetOrdinal("error")),
            reader.IsDBNull(reader.GetOrdinal("blocked_by")) ? null : reader.GetString(reader.GetOrdinal("blocked_by")),
            ParseDateTimeOffset(reader.GetString(reader.GetOrdinal("created_at"))),
            result,
            MetadataValues.Deserialize(reader.GetString(reader.GetOrdinal("metadata_json"))));
    }

    private bool TaskExists(SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM tasks WHERE id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    private bool GraphExists(SqliteConnection connection, string id)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM graphs WHERE id = $id LIMIT 1";
        command.Parameters.AddWithValue("$id", id);
        return command.ExecuteScalar() is not null;
    }

    public sealed class SqliteTaskCollection : ITaskCollection
    {
        private readonly SqliteStore _store;

        internal SqliteTaskCollection(SqliteStore store)
        {
            _store = store;
        }

        public int Count
        {
            get
            {
                using var connection = _store.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM tasks";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        public IEnumerable<string> Keys
        {
            get
            {
                using var connection = _store.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id FROM tasks ORDER BY created_at, id";
                using var reader = command.ExecuteReader();
                var keys = new List<string>();
                while (reader.Read())
                    keys.Add(reader.GetString(0));
                return keys;
            }
        }

        public void Save(CoreTask task)
        {
            ArgumentNullException.ThrowIfNull(task);
            Set(task.Id, task);
        }

        public CoreTask Get(string id)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            using var connection = _store.OpenConnection();
            return _store.LoadTask(connection, id);
        }

        public void Set(string id, object value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (value is not CoreTask task)
                throw new ArgumentException("Only Task instances can be stored in the task collection.", nameof(value));
            if (!string.Equals(id, task.Id, StringComparison.Ordinal))
                throw new ArgumentException("Task id does not match the provided key.", nameof(id));

            lock (_store._gate)
            {
                using var connection = _store.OpenConnection();
                using var transaction = connection.BeginTransaction();
                _store.SaveTask(connection, transaction, task);
                transaction.Commit();
            }
        }

        public void Delete(string id)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            lock (_store._gate)
            {
                using var connection = _store.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM tasks WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteNonQuery() == 0)
                    throw new KeyNotFoundException($"Task '{id}' was not found.");
            }
        }

        public bool Has(object key)
        {
            var id = key switch
            {
                string value => value,
                CoreTask task => task.Id,
                _ => null
            };

            if (string.IsNullOrEmpty(id))
                return false;

            using var connection = _store.OpenConnection();
            return _store.TaskExists(connection, id);
        }

        public object this[string id]
        {
            get => Get(id);
            set => Set(id, value);
        }
    }

    public sealed class SqliteGraphCollection : IGraphCollection
    {
        private readonly SqliteStore _store;

        internal SqliteGraphCollection(SqliteStore store)
        {
            _store = store;
        }

        public int Count
        {
            get
            {
                using var connection = _store.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM graphs";
                return Convert.ToInt32(command.ExecuteScalar());
            }
        }

        public IEnumerable<string> Keys
        {
            get
            {
                using var connection = _store.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT id FROM graphs ORDER BY created_at, id";
                using var reader = command.ExecuteReader();
                var keys = new List<string>();
                while (reader.Read())
                    keys.Add(reader.GetString(0));
                return keys;
            }
        }

        public void Save(TaskGraph graph)
        {
            ArgumentNullException.ThrowIfNull(graph);
            Set(graph.Id, graph);
        }

        public TaskGraph Get(string id)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            using var connection = _store.OpenConnection();
            return LoadGraph(connection, id);
        }

        public void Set(string id, object value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            if (value is not TaskGraph graph)
                throw new ArgumentException("Only TaskGraph instances can be stored in the graph collection.", nameof(value));
            if (!string.Equals(id, graph.Id, StringComparison.Ordinal))
                throw new ArgumentException("Graph id does not match the provided key.", nameof(id));

            lock (_store._gate)
            {
                using var connection = _store.OpenConnection();
                using var transaction = connection.BeginTransaction();
                foreach (var task in graph.Members)
                    _store.SaveTask(connection, transaction, task);
                SaveGraph(connection, transaction, graph);
                transaction.Commit();
            }
        }

        public void Delete(string id)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(id);
            lock (_store._gate)
            {
                using var connection = _store.OpenConnection();
                using var command = connection.CreateCommand();
                command.CommandText = "DELETE FROM graphs WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                if (command.ExecuteNonQuery() == 0)
                    throw new KeyNotFoundException($"Graph '{id}' was not found.");
            }
        }

        public bool Has(object key)
        {
            var id = key switch
            {
                string value => value,
                TaskGraph graph => graph.Id,
                _ => null
            };

            if (string.IsNullOrEmpty(id))
                return false;

            using var connection = _store.OpenConnection();
            return _store.GraphExists(connection, id);
        }

        public object this[string id]
        {
            get => Get(id);
            set => Set(id, value);
        }

        private static void SaveGraph(SqliteConnection connection, SqliteTransaction transaction, TaskGraph graph)
        {
            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText =
                    """
                    INSERT INTO graphs(id, title, metadata_json, created_at)
                    VALUES ($id, $title, $metadata_json, $created_at)
                    ON CONFLICT(id) DO UPDATE SET
                        title = excluded.title,
                        metadata_json = excluded.metadata_json,
                        created_at = excluded.created_at;
                    """;
                command.Parameters.AddWithValue("$id", graph.Id);
                command.Parameters.AddWithValue("$title", graph.Title);
                command.Parameters.AddWithValue("$metadata_json", MetadataValues.Serialize(graph.Metadata));
                command.Parameters.AddWithValue("$created_at", Format(graph.CreatedAt));
                command.ExecuteNonQuery();
            }

            ExecuteInTransaction(connection, transaction, "DELETE FROM graph_edges WHERE graph_id = $graph_id", ("$graph_id", graph.Id));
            ExecuteInTransaction(connection, transaction, "DELETE FROM graph_nodes WHERE graph_id = $graph_id", ("$graph_id", graph.Id));

            var nodes = graph.SnapshotNodes();
            for (var nodeIndex = 0; nodeIndex < nodes.Count; nodeIndex++)
            {
                var node = nodes[nodeIndex];
                ExecuteInTransaction(
                    connection,
                    transaction,
                    """
                    INSERT INTO graph_nodes(graph_id, task_id, position, is_finally, is_required)
                    VALUES ($graph_id, $task_id, $position, $is_finally, $is_required)
                    """,
                    ("$graph_id", graph.Id),
                    ("$task_id", node.Task.Id),
                    ("$position", nodeIndex),
                    ("$is_finally", node.Finally ? 1 : 0),
                    ("$is_required", node.Required ? 1 : 0));

                for (var edgeIndex = 0; edgeIndex < node.Dependencies.Count; edgeIndex++)
                {
                    ExecuteInTransaction(
                        connection,
                        transaction,
                        """
                        INSERT INTO graph_edges(graph_id, task_id, dependency_id, position)
                        VALUES ($graph_id, $task_id, $dependency_id, $position)
                        """,
                        ("$graph_id", graph.Id),
                        ("$task_id", node.Task.Id),
                        ("$dependency_id", node.Dependencies[edgeIndex].Id),
                        ("$position", edgeIndex));
                }
            }
        }

        private TaskGraph LoadGraph(SqliteConnection connection, string id)
        {
            string title;
            IReadOnlyDictionary<string, object?> metadata;
            DateTimeOffset createdAt;
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT title, metadata_json, created_at FROM graphs WHERE id = $id";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                    throw new KeyNotFoundException($"Graph '{id}' was not found.");

                title = reader.GetString(0);
                metadata = MetadataValues.Deserialize(reader.GetString(1));
                createdAt = ParseDateTimeOffset(reader.GetString(2));
            }

            var nodeRows = new List<(string TaskId, bool Finally, bool Required)>();
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT task_id, is_finally, is_required FROM graph_nodes WHERE graph_id = $id ORDER BY position, task_id";
                command.Parameters.AddWithValue("$id", id);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    nodeRows.Add((reader.GetString(0), reader.GetInt32(1) != 0, reader.GetInt32(2) != 0));
            }

            var tasksById = nodeRows
                .Select(row => _store.LoadTask(connection, row.TaskId))
                .ToDictionary(task => task.Id, StringComparer.Ordinal);
            var nodes = new List<TaskGraphNodeSnapshot>();
            foreach (var row in nodeRows)
            {
                var dependencies = LoadDependencyIds(connection, id, row.TaskId)
                    .Select(dependencyId => tasksById[dependencyId])
                    .ToList();
                nodes.Add(new TaskGraphNodeSnapshot(tasksById[row.TaskId], dependencies, row.Finally, row.Required));
            }

            return TaskGraph.Restore(id, title, createdAt, nodes, metadata);
        }

        private static IReadOnlyList<string> LoadDependencyIds(SqliteConnection connection, string graphId, string taskId)
        {
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT dependency_id FROM graph_edges WHERE graph_id = $graph_id AND task_id = $task_id ORDER BY position, dependency_id";
            command.Parameters.AddWithValue("$graph_id", graphId);
            command.Parameters.AddWithValue("$task_id", taskId);
            using var reader = command.ExecuteReader();
            var dependencyIds = new List<string>();
            while (reader.Read())
                dependencyIds.Add(reader.GetString(0));
            return dependencyIds;
        }

        private static void ExecuteInTransaction(SqliteConnection connection, SqliteTransaction transaction, string sql, params (string Name, object? Value)[] parameters)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            foreach (var parameter in parameters)
                command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
            command.ExecuteNonQuery();
        }
    }
}
