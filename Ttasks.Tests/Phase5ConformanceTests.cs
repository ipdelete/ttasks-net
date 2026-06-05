using Ttasks.Core;

namespace Ttasks.Tests;

using TaskModel = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase5ConformanceTests
{
    [Fact]
    public void R_GRAPH_14_15_16_17_18_Normal_And_Finally_Scheduling_Respects_Blocking_And_Independence()
    {
        var executor = new TaskExecutor();
        var events = new List<TaskEvent>();
        executor.Events.Subscribe(events.Add);

        executor.Register(TaskType.Bash, ctx =>
        {
            if (ctx.Payload == "root")
                throw new InvalidOperationException("boom");
            return "ok";
        });

        var graph = new TaskGraph("demo");
        var root = TaskModel.Bash("root");
        var badBranch = TaskModel.Bash("bad-branch");
        var goodLeaf = TaskModel.Bash("good-leaf");
        var cleanup = TaskModel.Bash("cleanup");

        graph.Add(root);
        graph.Add(badBranch, after: new[] { root });
        graph.Add(goodLeaf, after: new[] { root });
        graph.Add(cleanup, after: new[] { badBranch }, finally_: true, required: false);

        graph.Run(executor);

        Assert.Equal(TaskState.Failed, root.Status);
        Assert.Equal(TaskState.Blocked, badBranch.Status);
        Assert.Equal(TaskState.Blocked, goodLeaf.Status);
        Assert.Equal(TaskState.Succeeded, cleanup.Status);
        Assert.Equal(root.Id, badBranch.BlockedBy);
        Assert.Equal(root.Id, goodLeaf.BlockedBy);
        Assert.Contains(cleanup, graph.FinallyTasks);
        Assert.Contains(cleanup, graph.OptionalTasks);
        Assert.False(graph.Ok);
    }

    [Fact]
    public void R_GRAPH_19_21_22_23_24_25_26_27_29_30_Run_Reports_Progress_And_Upstream_Context_And_Empty_Graph_Behavior()
    {
        var graph = new TaskGraph("pipeline");
        var root = TaskModel.Bash("root");
        var executor = new TaskExecutor();
        var seen = new List<string>();

        executor.Register(TaskType.Bash, ctx =>
        {
            seen.Add(string.Join(",", ctx.Upstream.Keys.OrderBy(k => k)));
            Assert.Same(root, ctx.Upstream[root.Id]);
            return "ok";
        });
        var child = TaskModel.Bash("child");

        var seedExecutor = new TaskExecutor();
        seedExecutor.Register(TaskType.Bash, _ => "ok");
        seedExecutor.Execute(root);

        graph.Add(root);
        graph.Add(child, after: new[] { root });

        graph.Run(executor);

        Assert.Single(seen);
        Assert.Equal(root.Id, seen[0]);
        Assert.Equal(TaskState.Succeeded, child.Status);
        Assert.Same(graph, graph.Run(executor));
        Assert.True(graph.Ok);
        Assert.Empty(graph.Errors);

        var empty = new TaskGraph("empty");
        Assert.Same(empty, empty.Run(new TaskExecutor()));
        Assert.True(empty.Ok);
        Assert.Empty(empty.Failed);
        Assert.Empty(empty.Blocked);
    }
}
