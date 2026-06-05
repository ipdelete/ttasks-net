using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase5GraphRunConformanceTests
{
    [Fact]
    public void R_GRAPH_10_Run_Rejects_NonPositive_MaxWorkers()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskGraph().Run(new TaskExecutor(), maxWorkers: 0));
    }

    [Fact]
    public void R_GRAPH_11_Run_Rejects_Unregistered_Dependencies_Before_Events_Or_Persistence()
    {
        var store = new InMemoryStore();
        var executor = new TaskExecutor(store);
        var events = new List<TaskEvent>();
        var graph = new TaskGraph("invalid");
        var task = CoreTask.Bash("x");

        executor.Events.Subscribe(events.Add);
        graph.Add(task, after: new[] { CoreTask.Bash("missing") });

        Assert.Throws<InvalidOperationException>(() => graph.Run(executor));
        Assert.Empty(events);
        Assert.False(store.Graphs.Has(graph.Id));
    }

    [Fact]
    public void R_GRAPH_12_Run_Rejects_Self_Loop_Before_Scheduling()
    {
        var executor = new TaskExecutor();
        var events = new List<TaskEvent>();
        var graph = new TaskGraph();
        var task = CoreTask.Bash("x");

        executor.Events.Subscribe(events.Add);
        graph.Add(task, after: new[] { task });

        Assert.Throws<InvalidOperationException>(() => graph.Run(executor));
        Assert.Empty(events);
    }

    [Fact]
    public void R_GRAPH_12_Run_Rejects_Two_Node_Cycle()
    {
        var graph = new TaskGraph();
        var a = CoreTask.Bash("a");
        var b = CoreTask.Bash("b");

        graph.Add(a, after: new[] { b });
        graph.Add(b, after: new[] { a });

        Assert.Throws<InvalidOperationException>(() => graph.Run(new TaskExecutor()));
    }

    [Fact]
    public void R_GRAPH_12_Run_Rejects_Larger_Cycle()
    {
        var graph = new TaskGraph();
        var a = CoreTask.Bash("a");
        var b = CoreTask.Bash("b");
        var c = CoreTask.Bash("c");

        graph.Add(a, after: new[] { c });
        graph.Add(b, after: new[] { a });
        graph.Add(c, after: new[] { b });

        Assert.Throws<InvalidOperationException>(() => graph.Run(new TaskExecutor()));
    }

    [Fact]
    public void R_GRAPH_13_Run_Rejects_Stale_Running_Task()
    {
        var graph = new TaskGraph();
        var task = CoreTask.Bash("x");
        task.TransitionTo(TaskState.Running);
        graph.Add(task);

        Assert.Throws<InvalidOperationException>(() => graph.Run(new TaskExecutor()));
    }

    [Fact]
    public void R_GRAPH_14_29_Linear_Chain_Runs_In_Order_And_Returns_Self()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var a = CoreTask.Bash("a");
        var b = CoreTask.Bash("b");
        var c = CoreTask.Bash("c");
        var started = new List<string>();

        executor.Events.Subscribe(evt =>
        {
            if (evt.Type == TaskEventType.Started)
                started.Add(evt.Task.Payload);
        });
        executor.Register(TaskType.Bash, _ => "ok");
        graph.Add(a);
        graph.Add(b, after: new[] { a });
        graph.Add(c, after: new[] { b });

        var returned = graph.Run(executor, maxWorkers: 2);

        Assert.Same(graph, returned);
        Assert.Equal(new[] { "a", "b", "c" }, started);
        Assert.True(graph.Ok);
    }

    [Fact]
    public void R_GRAPH_14_20_Diamond_Runs_Parallel_Branches_Bounded_By_MaxWorkers()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var root = CoreTask.Bash("root");
        var left = CoreTask.Bash("left");
        var right = CoreTask.Bash("right");
        var tail = CoreTask.Bash("tail");
        var current = 0;
        var maxSeen = 0;

        executor.Register(TaskType.Bash, ctx =>
        {
            var now = Interlocked.Increment(ref current);
            maxSeen = Math.Max(maxSeen, now);
            if (ctx.Payload is "left" or "right")
                Thread.Sleep(50);
            Interlocked.Decrement(ref current);
            return "ok";
        });

        graph.Add(root);
        graph.Add(left, after: new[] { root });
        graph.Add(right, after: new[] { root });
        graph.Add(tail, after: new[] { left, right });

        graph.Run(executor, maxWorkers: 2);

        Assert.True(maxSeen >= 2);
        Assert.True(maxSeen <= 2);
        Assert.Equal(TaskState.Succeeded, tail.Status);
    }

    [Fact]
    public void R_GRAPH_16_27_Failure_Blocks_Descendants_And_Ok_Is_False()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var a = CoreTask.Bash("a");
        var b = CoreTask.Bash("b");
        var c = CoreTask.Bash("c");

        executor.Register(TaskType.Bash, ctx => ctx.Payload == "a" ? throw new InvalidOperationException("boom") : "ok");
        graph.Add(a);
        graph.Add(b, after: new[] { a });
        graph.Add(c, after: new[] { b });

        graph.Run(executor);

        Assert.Equal(TaskState.Failed, a.Status);
        Assert.Equal(TaskState.Blocked, b.Status);
        Assert.Equal(TaskState.Blocked, c.Status);
        Assert.Equal(a.Id, b.BlockedBy);
        Assert.Equal(b.Id, c.BlockedBy);
        Assert.False(graph.Ok);
    }

    [Fact]
    public void R_GRAPH_18_Independent_Branches_Are_Unaffected_By_Failure()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var a = CoreTask.Bash("a");
        var b = CoreTask.Bash("b");
        var x = CoreTask.Bash("x");
        var y = CoreTask.Bash("y");

        executor.Register(TaskType.Bash, ctx => ctx.Payload == "a" ? throw new InvalidOperationException("boom") : "ok");
        graph.Add(a);
        graph.Add(b, after: new[] { a });
        graph.Add(x);
        graph.Add(y, after: new[] { x });

        graph.Run(executor, maxWorkers: 2);

        Assert.Equal(TaskState.Failed, a.Status);
        Assert.Equal(TaskState.Blocked, b.Status);
        Assert.Equal(TaskState.Succeeded, x.Status);
        Assert.Equal(TaskState.Succeeded, y.Status);
        Assert.Equal(new[] { a }, graph.Failed);
        Assert.Equal(new[] { b }, graph.Blocked);
    }

    [Fact]
    public void R_GRAPH_15_17_Finally_Runs_After_Failed_And_Blocked_Parents()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var a = CoreTask.Bash("a");
        var b = CoreTask.Bash("b");
        var cleanup = CoreTask.Bash("cleanup");
        var started = new List<string>();

        executor.Events.Subscribe(evt =>
        {
            if (evt.Type == TaskEventType.Started)
                started.Add(evt.Task.Payload);
        });
        executor.Register(TaskType.Bash, ctx => ctx.Payload == "a" ? throw new InvalidOperationException("boom") : "ok");
        graph.Add(a);
        graph.Add(b, after: new[] { a });
        graph.Add(cleanup, after: new[] { a, b }, finally_: true, required: false);

        graph.Run(executor);

        Assert.Equal(TaskState.Succeeded, cleanup.Status);
        Assert.True(started.IndexOf("cleanup") > started.IndexOf("a"));
        Assert.DoesNotContain(cleanup, graph.Blocked);
    }

    [Fact]
    public void R_GRAPH_21_Already_Succeeded_Tasks_Are_Not_Reexecuted()
    {
        var seed = new TaskExecutor();
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var done = CoreTask.Bash("done");
        var child = CoreTask.Bash("child");
        var calls = new List<string>();

        seed.Register(TaskType.Bash, _ => "ok");
        seed.Execute(done);
        executor.Register(TaskType.Bash, ctx =>
        {
            calls.Add(ctx.Payload);
            return "ok";
        });
        graph.Add(done);
        graph.Add(child, after: new[] { done });

        graph.Run(executor);

        Assert.Equal(new[] { "child" }, calls);
        Assert.Equal(TaskState.Succeeded, child.Status);
    }

    [Fact]
    public void R_GRAPH_22_Carryover_Blocked_Task_Recovers_When_Parent_Succeeds()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var root = CoreTask.Bash("root");
        var child = CoreTask.Bash("child");

        child.TransitionTo(TaskState.Blocked, blockedBy: root.Id);
        executor.Register(TaskType.Bash, _ => "ok");
        graph.Add(root);
        graph.Add(child, after: new[] { root });

        graph.Run(executor);

        Assert.Equal(TaskState.Succeeded, root.Status);
        Assert.Equal(TaskState.Succeeded, child.Status);
        Assert.Null(child.BlockedBy);
    }

    [Fact]
    public void R_GRAPH_23_Errors_Reset_At_Start_Of_Run()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var task = CoreTask.Bash("x");
        var attempts = 0;

        executor.Register(TaskType.Bash, _ =>
        {
            attempts++;
            if (attempts == 1)
                throw new InvalidOperationException("boom");
            return "ok";
        });
        graph.Add(task);

        graph.Run(executor);
        Assert.NotEmpty(graph.Errors);

        graph.Run(executor);

        Assert.Empty(graph.Errors);
        Assert.True(graph.Ok);
    }

    [Fact]
    public void R_GRAPH_24_Handler_Sees_Only_Direct_Upstream_Parents()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var root = CoreTask.Bash("root");
        var mid = CoreTask.Bash("mid");
        var leaf = CoreTask.Bash("leaf");
        var sibling = CoreTask.Bash("sibling");
        var cleanup = CoreTask.Bash("cleanup");

        executor.Register(TaskType.Bash, ctx =>
        {
            if (ctx.Payload == "leaf")
            {
                Assert.Equal(new[] { mid.Id }, ctx.Upstream.Keys.ToArray());
                Assert.Same(mid, ctx.Upstream[mid.Id]);
            }

            return "ok";
        });
        graph.Add(root);
        graph.Add(mid, after: new[] { root });
        graph.Add(sibling);
        graph.Add(cleanup, after: new[] { root }, finally_: true, required: false);
        graph.Add(leaf, after: new[] { mid });

        graph.Run(executor, maxWorkers: 2);
    }
}
