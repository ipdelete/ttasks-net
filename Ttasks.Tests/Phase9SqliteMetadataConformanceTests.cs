using Microsoft.Data.Sqlite;
using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;

public sealed class Phase9SqliteMetadataConformanceTests
{
    [Fact]
    public void R_AGENTGRAPH_06_Task_Metadata_Roundtrips_Through_Sqlite_As_Detached_Snapshot()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var task = CoreTask.Powershell("teams read 48:notes -n 20 --json", metadata: new Dictionary<string, object?>
        {
            ["chatId"] = "48:notes",
            ["kind"] = "teams-read",
            ["attempt"] = 1
        });
        store.Tasks.Save(task);

        var loaded = store.Tasks.Get(task.Id);
        loaded.SetMetadata("chatId", "changed");

        Assert.NotSame(task, loaded);
        Assert.Equal("48:notes", store.Tasks.Get(task.Id).Metadata["chatId"]);
        Assert.Equal("teams-read", loaded.Metadata["kind"]);
        Assert.Equal(1, Convert.ToInt32(loaded.Metadata["attempt"]));
    }

    [Fact]
    public void R_AGENTGRAPH_06_07_Graph_Metadata_Roundtrips_With_Topology_And_Update_Persists()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var readNotes = CoreTask.Powershell("teams read 48:notes -n 20 --json", metadata: new Dictionary<string, object?>
        {
            ["plannerId"] = "read-notes"
        });
        var summarize = CoreTask.Prompt("Summarize the upstream reads.");
        var graph = new TaskGraph("fan-in", metadata: new Dictionary<string, object?>
        {
            ["source"] = "chat-ui"
        });
        graph.Add(readNotes);
        graph.Add(summarize, after: new[] { readNotes });
        store.Graphs.Save(graph);

        graph.SetMetadata("source", "updated-ui");
        readNotes.SetMetadata("plannerId", "read-notes-updated");
        store.Graphs.Save(graph);
        var loaded = store.Graphs.Get(graph.Id);
        var loadedRead = loaded.Members.Single(task => task.Id == readNotes.Id);

        Assert.NotSame(graph, loaded);
        Assert.Equal("updated-ui", loaded.Metadata["source"]);
        Assert.Equal("read-notes-updated", loadedRead.Metadata["plannerId"]);
        Assert.Equal(new[] { readNotes.Id }, loaded.Dependencies(loaded.Members.Single(task => task.Id == summarize.Id)).Select(task => task.Id));
    }

    [Fact]
    public void R_AGENTGRAPH_11_Durable_Graph_Reload_Preserves_Dependency_Declaration_Order()
    {
        using var database = TempDatabase();
        var store = new SqliteStore(database.Path);
        var first = CoreTask.Bash("first");
        var second = CoreTask.Bash("second");
        var summary = CoreTask.Prompt("summarize");
        var graph = new TaskGraph("ordered");
        graph.Add(first);
        graph.Add(second);
        graph.Add(summary, after: new[] { second, first });
        store.Graphs.Save(graph);

        var loaded = store.Graphs.Get(graph.Id);
        var loadedSummary = loaded.Members.Single(task => task.Id == summary.Id);

        Assert.Equal(new[] { second.Id, first.Id }, loaded.Dependencies(loadedSummary).Select(task => task.Id));
    }

    private static TempDatabaseFile TempDatabase() => new();

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
