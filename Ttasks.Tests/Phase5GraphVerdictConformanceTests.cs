using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase5GraphVerdictConformanceTests
{
    [Fact]
    public void R_GRAPH_25_Status_Views_Reflect_Graph_Members_Only()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var member = CoreTask.Bash("member");
        var outsider = CoreTask.Bash("outsider");

        executor.Register(TaskType.Bash, _ => "ok");
        executor.Execute(outsider);
        graph.Add(member);
        graph.Run(executor);

        Assert.Equal(new[] { member }, graph.Succeeded);
        Assert.DoesNotContain(outsider, graph.Succeeded);
    }

    [Fact]
    public void R_GRAPH_25_Failed_Cancelled_And_Blocked_Views_Filter_Members()
    {
        var graph = new TaskGraph();
        var failed = CoreTask.Bash("failed");
        var cancelled = CoreTask.Bash("cancelled");
        var blocked = CoreTask.Bash("blocked");
        var outsider = CoreTask.Bash("outsider");

        failed.TransitionTo(TaskState.Failed, "boom");
        cancelled.Cancel();
        blocked.TransitionTo(TaskState.Blocked, blockedBy: "parent");
        outsider.TransitionTo(TaskState.Failed, "boom");
        graph.Add(failed);
        graph.Add(cancelled);
        graph.Add(blocked);

        Assert.Equal(new[] { failed }, graph.Failed);
        Assert.Equal(new[] { cancelled }, graph.Cancelled);
        Assert.Equal(new[] { blocked }, graph.Blocked);
        Assert.DoesNotContain(outsider, graph.Failed);
    }

    [Fact]
    public void R_GRAPH_26_Errors_Record_Executor_Thrown_Errors_Only()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var failed = CoreTask.Bash("failed");
        var blocked = CoreTask.Bash("blocked");

        executor.Register(TaskType.Bash, ctx => ctx.Payload == "failed" ? throw new InvalidOperationException("boom") : "ok");
        graph.Add(failed);
        graph.Add(blocked, after: new[] { failed });

        graph.Run(executor);

        Assert.True(graph.Errors.ContainsKey(failed.Id));
        Assert.False(graph.Errors.ContainsKey(blocked.Id));
    }

    [Fact]
    public void R_GRAPH_27_Unrun_Graph_Reports_Not_Ok()
    {
        Assert.False(new TaskGraph().Ok);
    }

    [Fact]
    public void R_GRAPH_27_Empty_Graph_Reports_Ok_After_Run()
    {
        var graph = new TaskGraph();

        graph.Run(new TaskExecutor());

        Assert.True(graph.Ok);
    }

    [Fact]
    public void R_GRAPH_27_Optional_Finally_Failure_Does_Not_Break_Ok()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var root = CoreTask.Bash("root");
        var cleanup = CoreTask.Bash("cleanup");

        executor.Register(TaskType.Bash, ctx => ctx.Payload == "cleanup" ? throw new InvalidOperationException("cleanup failed") : "ok");
        graph.Add(root);
        graph.Add(cleanup, after: new[] { root }, finally_: true, required: false);

        graph.Run(executor);

        Assert.True(graph.Ok);
        Assert.Equal(new[] { cleanup }, graph.OptionalFailed);
        Assert.Empty(graph.RequiredFailed);
    }

    [Fact]
    public void R_GRAPH_27_Required_Finally_Failure_Breaks_Ok()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var root = CoreTask.Bash("root");
        var cleanup = CoreTask.Bash("cleanup");

        executor.Register(TaskType.Bash, ctx => ctx.Payload == "cleanup" ? throw new InvalidOperationException("cleanup failed") : "ok");
        graph.Add(root);
        graph.Add(cleanup, after: new[] { root }, finally_: true, required: true);

        graph.Run(executor);

        Assert.False(graph.Ok);
        Assert.Equal(new[] { cleanup }, graph.RequiredFailed);
    }

    [Fact]
    public void R_GRAPH_28_Graph_Persistence_Failures_Are_Captured_Not_Propagated()
    {
        var executor = new TaskExecutor(new FailingGraphStore());
        var graph = new TaskGraph();
        var task = CoreTask.Bash("x");

        executor.Register(TaskType.Bash, _ => "ok");
        graph.Add(task);

        graph.Run(executor);

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.NotEmpty(executor.GraphPersistenceErrors);
    }

    [Fact]
    public void R_GRAPH_30_Empty_Graph_Runs_Cleanly_With_Empty_Status_Views()
    {
        var graph = new TaskGraph();

        graph.Run(new TaskExecutor());

        Assert.True(graph.Ok);
        Assert.Empty(graph.Succeeded);
        Assert.Empty(graph.Failed);
        Assert.Empty(graph.Cancelled);
        Assert.Empty(graph.Blocked);
    }

    private sealed class FailingGraphStore : ITaskStore
    {
        private readonly InMemoryStore _backing = new();

        public ITaskCollection Tasks => _backing.Tasks;
        public IGraphCollection Graphs { get; } = new FailingGraphs();

        private sealed class FailingGraphs : IGraphCollection
        {
            public int Count => 0;
            public IEnumerable<string> Keys => Array.Empty<string>();
            public void Save(TaskGraph graph) => throw new InvalidOperationException("graph save failed");
            public TaskGraph Get(string id) => throw new KeyNotFoundException(id);
            public void Set(string id, object value) => throw new InvalidOperationException("graph save failed");
            public void Delete(string id) => throw new KeyNotFoundException(id);
            public bool Has(object key) => false;
            public object this[string id]
            {
                get => Get(id);
                set => Set(id, value);
            }
        }
    }
}
