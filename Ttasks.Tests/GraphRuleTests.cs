using Ttasks.Core;

namespace Ttasks.Tests;

using TaskModel = Ttasks.Core.Task;

public class GraphRuleTests
{
    [Fact]
    public void Add_DeduplicatesDependenciesAndReturnsDirectOrder()
    {
        var graph = new TaskGraph();
        var root = TaskModel.Bash("root");
        var leaf = TaskModel.Bash("leaf");

        graph.Add(root);
        graph.Add(leaf, after: new[] { root, root });

        Assert.Equal(new[] { root }, graph.Dependencies(leaf));
        Assert.Equal(root, graph.Roots().Single());
        Assert.Equal(leaf, graph.Leaves().Single());
    }

    [Fact]
    public void Add_RejectsInvalidRequiredFlag()
    {
        var graph = new TaskGraph();

        Assert.Throws<ArgumentException>(() => graph.Add(TaskModel.Bash("x"), required: false));
    }

    [Fact]
    public void Run_RejectsInvalidInputAndCycles()
    {
        var graph = new TaskGraph();
        var executor = new TaskExecutor();

        Assert.Throws<ArgumentOutOfRangeException>(() => graph.Run(executor, maxWorkers: 0));

        var missing = TaskModel.Bash("missing");
        graph.Add(missing, after: new[] { TaskModel.Bash("ghost") });
        Assert.Throws<InvalidOperationException>(() => graph.Run(executor));

        graph = new TaskGraph();
        var a = TaskModel.Bash("a");
        var b = TaskModel.Bash("b");
        graph.Add(a, after: new[] { b });
        graph.Add(b, after: new[] { a });
        Assert.Throws<InvalidOperationException>(() => graph.Run(executor));

        graph = new TaskGraph();
        a = TaskModel.Bash("a");
        b = TaskModel.Bash("b");
        graph.Add(a);
        graph.Add(b, after: new[] { a });
        a.TransitionTo(Ttasks.Core.TaskStatus.Running);
        Assert.Throws<InvalidOperationException>(() => graph.Run(executor));
    }

    [Fact]
    public void Run_ReturnsGraphAndRunsEmptyGraphCleanly()
    {
        var graph = new TaskGraph();
        var executor = new TaskExecutor();

        Assert.Same(graph, graph.Run(executor));
        Assert.True(graph.Ok);
        Assert.Empty(graph.RequiredTasks);
    }
}
