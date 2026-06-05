using Ttasks.Core;

namespace Ttasks.Tests;

using TaskModel = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class GraphTests
{
    [Fact]
    public void Add_And_Views_Work_Basically()
    {
        var graph = new TaskGraph("demo");
        var first = TaskModel.Bash("one");
        var second = TaskModel.Bash("two");

        graph.Add(first);
        graph.Add(second, after: new[] { first }, finally_: true, required: false);

        Assert.Equal("demo", graph.Title);
        Assert.Single(graph.Dependencies(second));
        Assert.Same(first, graph.Dependencies(second)[0]);
        Assert.Contains(first, graph.Roots());
        Assert.Contains(second, graph.Leaves());
        Assert.True(graph.IsFinally(second));
        Assert.True(graph.IsOptional(second));
    }

    [Fact]
    public void Run_Executes_Dependency_Chain()
    {
        var executor = new TaskExecutor();
        executor.Register(TaskType.Bash, _ => "ok");

        var graph = new TaskGraph("pipeline");
        var build = TaskModel.Bash("build");
        var test = TaskModel.Bash("test");

        graph.Add(build);
        graph.Add(test, after: new[] { build });

        graph.Run(executor);

        Assert.Equal(TaskState.Succeeded, build.Status);
        Assert.Equal(TaskState.Succeeded, test.Status);
        Assert.True(graph.Ok);
    }
}
