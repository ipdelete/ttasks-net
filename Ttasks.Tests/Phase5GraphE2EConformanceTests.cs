using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase5GraphE2EConformanceTests
{
    [Fact]
    public void S_GRAPH_01_Empty_Graph_Runs_And_Reports_Ok()
    {
        var graph = new TaskGraph();

        graph.Run(new TaskExecutor());

        Assert.True(graph.Ok);
    }

    [Fact]
    public void S_GRAPH_02_Single_Task_End_To_End_With_Store()
    {
        var store = new InMemoryStore();
        var executor = new TaskExecutor(store);
        var graph = new TaskGraph("single");
        var task = CoreTask.Bash("x");

        executor.Register(TaskType.Bash, _ => "ok");
        graph.Add(task);
        graph.Run(executor);

        Assert.True(graph.Ok);
        Assert.Same(graph, store.Graphs.Get(graph.Id));
        Assert.Equal(TaskState.Succeeded, store.Tasks.Get(task.Id).Status);
    }

    [Fact]
    public void S_GRAPH_03_Linear_Chain_A_To_B_To_C()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var a = CoreTask.Bash("a");
        var b = CoreTask.Bash("b");
        var c = CoreTask.Bash("c");

        executor.Register(TaskType.Bash, _ => "ok");
        graph.Add(a);
        graph.Add(b, after: new[] { a });
        graph.Add(c, after: new[] { b });

        graph.Run(executor);

        Assert.All(new[] { a, b, c }, task => Assert.Equal(TaskState.Succeeded, task.Status));
        Assert.True(graph.Ok);
    }

    [Fact]
    public void S_GRAPH_04_Diamond_Converges()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var root = CoreTask.Bash("root");
        var left = CoreTask.Bash("left");
        var right = CoreTask.Bash("right");
        var tail = CoreTask.Bash("tail");

        executor.Register(TaskType.Bash, _ => "ok");
        graph.Add(root);
        graph.Add(left, after: new[] { root });
        graph.Add(right, after: new[] { root });
        graph.Add(tail, after: new[] { left, right });

        graph.Run(executor, maxWorkers: 2);

        Assert.Equal(TaskState.Succeeded, tail.Status);
        Assert.True(graph.Ok);
    }

    [Fact]
    public void S_GRAPH_05_Failure_Cascades_To_Descendants()
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
        Assert.False(graph.Ok);
    }

    [Fact]
    public void S_GRAPH_06_Optional_Finally_Cleanup_Runs_Even_On_Failure()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var root = CoreTask.Bash("root");
        var child = CoreTask.Bash("child");
        var cleanup = CoreTask.Bash("cleanup");

        executor.Register(TaskType.Bash, ctx => ctx.Payload == "root" ? throw new InvalidOperationException("boom") : "ok");
        graph.Add(root);
        graph.Add(child, after: new[] { root });
        graph.Add(cleanup, after: new[] { root, child }, finally_: true, required: false);

        graph.Run(executor);

        Assert.Equal(TaskState.Succeeded, cleanup.Status);
        Assert.Contains(cleanup, graph.Succeeded);
    }

    [Fact]
    public void S_GRAPH_07_ReRun_Skips_Already_Succeeded_Tasks_And_Repairs_Failed_Task()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph();
        var a = CoreTask.Bash("a");
        var b = CoreTask.Bash("bad");
        var calls = new List<string>();

        executor.Register(TaskType.Bash, ctx =>
        {
            calls.Add(ctx.Payload);
            if (ctx.Payload == "bad")
                throw new InvalidOperationException("boom");
            return "ok";
        });
        graph.Add(a);
        graph.Add(b, after: new[] { a });

        graph.Run(executor);
        Assert.False(graph.Ok);

        b.Payload = "fixed";
        graph.Run(executor);

        Assert.Equal(new[] { "a", "bad", "fixed" }, calls);
        Assert.True(graph.Ok);
    }

    [Fact]
    public void S_GRAPH_10_Independent_Branches_Isolated_Under_Failure()
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

        Assert.Equal(TaskState.Blocked, b.Status);
        Assert.Equal(TaskState.Succeeded, x.Status);
        Assert.Equal(TaskState.Succeeded, y.Status);
    }
}
