using Microsoft.Data.Sqlite;
using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public sealed class Phase7SqliteStoreConformanceTests
{
    [Fact]
    public void R_STORE_04_05_Sqlite_Set_Rejects_Id_Mismatch_And_Wrong_Type()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var task = CoreTask.Bash("x");
        var graph = new TaskGraph("g");
        store.Tasks.Save(task);
        store.Graphs.Save(graph);

        Assert.Throws<ArgumentException>(() => store.Tasks.Set("other", task));
        Assert.Throws<ArgumentException>(() => store.Tasks.Set(task.Id, graph));
        Assert.Throws<ArgumentException>(() => store.Graphs.Set("other", graph));
        Assert.Throws<ArgumentException>(() => store.Graphs.Set(graph.Id, task));
        Assert.Equal(new[] { task.Id }, store.Tasks.Keys);
        Assert.Equal(new[] { graph.Id }, store.Graphs.Keys);
    }

    [Fact]
    public void R_STORE_06_07_Sqlite_Missing_Get_And_Delete_Throw_KeyNotFound()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);

        Assert.Throws<KeyNotFoundException>(() => store.Tasks.Get("missing"));
        Assert.Throws<KeyNotFoundException>(() => store.Tasks.Delete("missing"));
        Assert.Throws<KeyNotFoundException>(() => store.Graphs.Get("missing"));
        Assert.Throws<KeyNotFoundException>(() => store.Graphs.Delete("missing"));
    }

    [Fact]
    public void R_STORE_08_Sqlite_Has_Accepts_Ids_And_Objects_And_Never_Throws_For_Other_Values()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var task = CoreTask.Bash("x");
        var graph = new TaskGraph("g");
        store.Tasks.Save(task);
        store.Graphs.Save(graph);

        Assert.True(store.Tasks.Has(task.Id));
        Assert.True(store.Tasks.Has(task));
        Assert.False(store.Tasks.Has(graph));
        Assert.False(store.Tasks.Has(123));
        Assert.True(store.Graphs.Has(graph.Id));
        Assert.True(store.Graphs.Has(graph));
        Assert.False(store.Graphs.Has(task));
        Assert.False(store.Graphs.Has(new object()));
    }

    [Fact]
    public void R_STORE_10_Sqlite_Keys_Are_Stable_CreatedAt_Then_Id_Ordered_And_Count_Matches()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var sameCreatedAt = DateTimeOffset.Parse("2026-01-01T00:00:00.0000000+00:00");
        var later = sameCreatedAt.AddSeconds(1);
        var taskB = CoreTask.Restore("b", TaskType.Bash, "b", "", "", null, TaskState.Pending, null, null, sameCreatedAt, null);
        var taskA = CoreTask.Restore("a", TaskType.Bash, "a", "", "", null, TaskState.Pending, null, null, sameCreatedAt, null);
        var taskC = CoreTask.Restore("c", TaskType.Bash, "c", "", "", null, TaskState.Pending, null, null, later, null);
        var graphB = TaskGraph.Restore("graph-b", "b", sameCreatedAt, []);
        var graphA = TaskGraph.Restore("graph-a", "a", sameCreatedAt, []);

        store.Tasks.Save(taskB);
        store.Tasks.Save(taskC);
        store.Tasks.Save(taskA);
        store.Graphs.Save(graphB);
        store.Graphs.Save(graphA);

        Assert.Equal(new[] { "a", "b", "c" }, store.Tasks.Keys);
        Assert.Equal(store.Tasks.Count, store.Tasks.Keys.Count());
        Assert.Equal(new[] { "graph-a", "graph-b" }, store.Graphs.Keys);
        Assert.Equal(store.Graphs.Count, store.Graphs.Keys.Count());
    }

    [Fact]
    public void R_STORE_13_Durable_Task_Reads_Return_Detached_Snapshots()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var task = CoreTask.Bash("echo original", title: "before");
        store.Tasks.Save(task);

        var first = store.Tasks.Get(task.Id);
        var second = store.Tasks.Get(task.Id);
        first.Title = "mutated snapshot";

        Assert.NotSame(task, first);
        Assert.NotSame(first, second);
        Assert.Equal("before", second.Title);
        Assert.Equal("before", store.Tasks.Get(task.Id).Title);
    }

    [Fact]
    public void R_STORE_14_Task_Roundtrip_Preserves_Core_Fields_And_Result()
    {
        using var database = TempDatabase();
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("payload", title: "title", description: "description", timeout: 30);
        executor.Register(TaskType.Bash, _ => new { stdout = "out", stderr = "err", returncode = 0 });
        var result = executor.Execute(task);
        var store = new SqliteStore(database.Path);

        store.Tasks.Save(task);
        var loaded = store.Tasks.Get(task.Id);

        Assert.Equal(task.Id, loaded.Id);
        Assert.Equal(TaskType.Bash, loaded.Type);
        Assert.Equal("payload", loaded.Payload);
        Assert.Equal("title", loaded.Title);
        Assert.Equal("description", loaded.Description);
        Assert.Equal(30, loaded.Timeout);
        Assert.Equal(TaskState.Succeeded, loaded.Status);
        Assert.Equal(task.CreatedAt, loaded.CreatedAt);
        Assert.NotNull(loaded.Result);
        Assert.Equal(result.TaskId, loaded.Result!.TaskId);
        Assert.Equal(result.Status, loaded.Result.Status);
        Assert.Equal(result.StartedAt, loaded.Result.StartedAt);
        Assert.Equal(result.FinishedAt, loaded.Result.FinishedAt);
        Assert.Equal("out", loaded.Result.Output);
        Assert.Equal("err", loaded.Result.Error);
        Assert.Equal(0, loaded.Result.ReturnCode);
        Assert.Null(loaded.Result.TerminationReason);
        Assert.Null(loaded.Result.Raw);
    }

    [Fact]
    public void R_STORE_14_Task_Roundtrip_Preserves_Failure_And_Blocked_Metadata()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var failed = CoreTask.Bash("fail");
        var blocked = CoreTask.Bash("blocked");
        var executor = new TaskExecutor(store);
        executor.Register(TaskType.Bash, _ => throw new InvalidOperationException("boom"));

        Assert.Throws<InvalidOperationException>(() => executor.Execute(failed));
        blocked.TransitionTo(TaskState.Blocked, blockedBy: failed.Id);
        store.Tasks.Save(blocked);

        var loadedFailed = store.Tasks.Get(failed.Id);
        var loadedBlocked = store.Tasks.Get(blocked.Id);

        Assert.Equal(TaskState.Failed, loadedFailed.Status);
        Assert.Equal("boom", loadedFailed.Error);
        Assert.Equal("handler", loadedFailed.Result?.TerminationReason);
        Assert.Equal(TaskState.Blocked, loadedBlocked.Status);
        Assert.Equal(failed.Id, loadedBlocked.BlockedBy);
    }

    [Fact]
    public void R_STORE_15_Graph_Roundtrip_Preserves_Topology_And_Finally_Metadata()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var root = CoreTask.Bash("root");
        var a = CoreTask.Bash("a");
        var b = CoreTask.Bash("b");
        var tail = CoreTask.Bash("tail");
        var cleanup = CoreTask.Bash("cleanup");
        var graph = new TaskGraph("diamond");
        graph.Add(root);
        graph.Add(a, after: new[] { root });
        graph.Add(b, after: new[] { root });
        graph.Add(tail, after: new[] { a, b });
        graph.Add(cleanup, after: new[] { a, b }, finally_: true, required: false);

        store.Graphs.Save(graph);
        var loaded = store.Graphs.Get(graph.Id);
        var loadedRoot = loaded.Members.Single(task => task.Id == root.Id);
        var loadedTail = loaded.Members.Single(task => task.Id == tail.Id);
        var loadedCleanup = loaded.Members.Single(task => task.Id == cleanup.Id);

        Assert.NotSame(graph, loaded);
        Assert.Equal(graph.Id, loaded.Id);
        Assert.Equal("diamond", loaded.Title);
        Assert.Equal(graph.CreatedAt, loaded.CreatedAt);
        Assert.Equal(new[] { root.Id, a.Id, b.Id, tail.Id, cleanup.Id }, loaded.Members.Select(task => task.Id));
        Assert.Equal(new[] { root.Id }, loaded.Roots().Select(task => task.Id));
        Assert.Equal(new[] { a.Id, b.Id }, loaded.Dependencies(loadedTail).Select(task => task.Id));
        Assert.True(loaded.IsFinally(loadedCleanup));
        Assert.True(loaded.IsOptional(loadedCleanup));
        Assert.NotSame(root, loadedRoot);
        Assert.Same(loaded.Members.Single(task => task.Id == a.Id), loaded.Dependencies(loadedTail)[0]);
    }

    [Fact]
    public void R_STORE_16_Graph_Save_Persists_Member_Tasks_And_Delete_Keeps_Tasks()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var task = CoreTask.Bash("x");
        var graph = new TaskGraph("g");
        graph.Add(task);

        store.Graphs.Save(graph);

        Assert.True(store.Graphs.Has(graph.Id));
        Assert.True(store.Tasks.Has(task.Id));

        store.Graphs.Delete(graph.Id);

        Assert.False(store.Graphs.Has(graph.Id));
        Assert.True(store.Tasks.Has(task.Id));
        Assert.Throws<KeyNotFoundException>(() => store.Graphs.Get(graph.Id));
    }

    [Fact]
    public void R_STORE_17_Durable_Data_Survives_New_Store_Instance()
    {
        using var database = TempDatabase();
        var task = CoreTask.Bash("persist");
        var graph = new TaskGraph("persisted graph");
        graph.Add(task);
        new SqliteStore(database.Path).Graphs.Save(graph);

        var reopened = new SqliteStore(database.Path);

        Assert.True(reopened.Tasks.Has(task.Id));
        Assert.True(reopened.Graphs.Has(graph.Id));
        Assert.Equal("persist", reopened.Tasks.Get(task.Id).Payload);
        Assert.Equal(new[] { task.Id }, reopened.Graphs.Get(graph.Id).Members.Select(member => member.Id));
    }

    [Fact]
    public void R_STORE_18_19_Fresh_Database_Creates_Versioned_Schema()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);

        Assert.Empty(store.Tasks.Keys);
        Assert.Equal(SqliteStore.CurrentSchemaVersion, ReadSchemaVersion(database.Path));
    }

    [Fact]
    public void R_STORE_20_Schema_Mismatch_Refuses_To_Open_Without_Modifying_Data()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var task = CoreTask.Bash("keep");
        store.Tasks.Save(task);
        WriteSchemaVersion(database.Path, SqliteStore.CurrentSchemaVersion + 100);

        var exception = Assert.Throws<SqliteStoreSchemaException>(() => new SqliteStore(database.Path));

        Assert.Contains("schema", exception.Message, StringComparison.OrdinalIgnoreCase);
        WriteSchemaVersion(database.Path, SqliteStore.CurrentSchemaVersion);
        Assert.Equal("keep", new SqliteStore(database.Path).Tasks.Get(task.Id).Payload);
    }

    [Fact]
    public void R_STORE_20_Populated_Known_Tables_Without_Metadata_Refuses_To_Open()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        store.Tasks.Save(CoreTask.Bash("x"));
        DeleteSchemaVersion(database.Path);

        Assert.Throws<SqliteStoreSchemaException>(() => new SqliteStore(database.Path));
    }

    [Fact]
    public void R_STORE_21_Destructive_Migration_Is_Explicit_And_Noisy()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var task = CoreTask.Bash("deleted");
        store.Tasks.Save(task);
        WriteSchemaVersion(database.Path, SqliteStore.CurrentSchemaVersion + 100);
        var warnings = new List<string>();

        var rebuilt = new SqliteStore(database.Path, new SqliteStoreOptions
        {
            AllowDestructiveMigration = true,
            WarningSink = warnings.Add
        });

        Assert.NotEmpty(warnings);
        Assert.Equal(SqliteStore.CurrentSchemaVersion, ReadSchemaVersion(database.Path));
        Assert.False(rebuilt.Tasks.Has(task.Id));
    }

    [Fact]
    public void R_STORE_22_Concurrent_Graph_Run_Persists_All_Terminal_Task_States()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var executor = new TaskExecutor(store);
        var graph = new TaskGraph("wide");
        var tasks = Enumerable.Range(0, 32).Select(i => CoreTask.Bash($"task-{i}")).ToArray();
        foreach (var task in tasks)
            graph.Add(task);
        executor.Register(TaskType.Bash, ctx => ctx.Payload);

        graph.Run(executor, maxWorkers: 8);

        Assert.Empty(executor.PersistenceErrors);
        Assert.All(tasks, task => Assert.Equal(TaskState.Succeeded, store.Tasks.Get(task.Id).Status));
    }

    [Fact]
    public void R_STORE_24_Graph_Run_Persists_At_Start_And_End()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var executor = new TaskExecutor(store);
        var graph = new TaskGraph("run");
        var task = CoreTask.Bash("x");
        var sawStartPersistence = false;
        graph.Add(task);
        executor.Register(TaskType.Bash, _ =>
        {
            sawStartPersistence = store.Graphs.Has(graph.Id);
            Assert.Equal(new[] { task.Id }, store.Graphs.Get(graph.Id).Members.Select(member => member.Id));
            return "ok";
        });

        graph.Run(executor);

        Assert.True(sawStartPersistence);
        Assert.Equal(TaskState.Succeeded, store.Graphs.Get(graph.Id).Members.Single().Status);
    }

    [Fact]
    public void R_STORE_24_Invalid_Graph_Does_Not_Persist()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var executor = new TaskExecutor(store);
        var graph = new TaskGraph("invalid");
        var task = CoreTask.Bash("x");
        graph.Add(task, after: new[] { CoreTask.Bash("missing") });

        Assert.Throws<InvalidOperationException>(() => graph.Run(executor));

        Assert.False(store.Graphs.Has(graph.Id));
        Assert.False(store.Tasks.Has(task.Id));
    }

    private static TempDatabaseFile TempDatabase() => new();

    private static int ReadSchemaVersion(string path)
    {
        using var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM metadata WHERE key = 'schema_version'";
        return int.Parse((string)command.ExecuteScalar()!);
    }

    private static void WriteSchemaVersion(string path, int version)
    {
        using var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE metadata SET value = $version WHERE key = 'schema_version'";
        command.Parameters.AddWithValue("$version", version.ToString());
        command.ExecuteNonQuery();
    }

    private static void DeleteSchemaVersion(string path)
    {
        using var connection = OpenConnection(path);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM metadata WHERE key = 'schema_version'";
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenConnection(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    private sealed class TempDatabaseFile : IDisposable
    {
        public TempDatabaseFile()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ttasks-{Guid.NewGuid():N}.db");
        }

        public string Path { get; }

        public void Dispose()
        {
            foreach (var path in new[] { Path, $"{Path}-wal", $"{Path}-shm" })
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
        }
    }
}
