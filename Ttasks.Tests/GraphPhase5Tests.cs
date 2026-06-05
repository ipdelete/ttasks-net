using Ttasks.Core;

namespace Ttasks.Tests;

using TaskModel = Ttasks.Core.Task;

public class GraphPhase5Tests
{
    [Fact]
    public void Run_Exposes_Status_Views_And_Finally_Classification()
    {
        var executor = new TaskExecutor();
        executor.Register(TaskType.Bash, _ => "ok");

        var graph = new TaskGraph("demo");
        var root = TaskModel.Bash("root");
        var cleanup = TaskModel.Bash("cleanup");

        graph.Add(root);
        graph.Add(cleanup, after: new[] { root }, finally_: true, required: false);

        graph.Run(executor);

        Assert.Equal(new[] { root }, graph.RequiredTasks);
        Assert.Equal(new[] { cleanup }, graph.FinallyTasks);
        Assert.Equal(new[] { cleanup }, graph.OptionalTasks);
        Assert.Contains(root, graph.Succeeded);
        Assert.Contains(cleanup, graph.Succeeded);
        Assert.True(graph.Ok);
    }

    [Fact]
    public void Run_Records_Executor_Errors_And_Fails_Required_Tasks()
    {
        var executor = new TaskExecutor();
        executor.Register(TaskType.Bash, _ => throw new InvalidOperationException("boom"));

        var graph = new TaskGraph("demo");
        var root = TaskModel.Bash("root");

        graph.Add(root);
        graph.Run(executor);

        Assert.Contains(root.Id, graph.Errors.Keys);
        Assert.Contains(root, graph.Failed);
        Assert.Contains(root, graph.RequiredFailed);
        Assert.False(graph.Ok);
    }
}
