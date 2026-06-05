using Ttasks.Core;

namespace Ttasks.Tests;

using TaskModel = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class ExecutorTests
{
    [Fact]
    public void Execute_Success_TransitionsAndPublishesLifecycleEvents()
    {
        var executor = new TaskExecutor();
        var task = new TaskModel(TaskType.Bash, "echo hi");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.Register(TaskType.Bash, _ => "ok");

        var result = executor.Execute(task);

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.NotNull(task.Result);
        Assert.Equal("ok", result.Output);
        Assert.Equal(2, events.Count);
        Assert.Equal(TaskEventType.Started, events[0].Type);
        Assert.Equal(TaskEventType.Succeeded, events[1].Type);
    }

    [Fact]
    public void Execute_MissingHandler_EmitsFailedAndThrows()
    {
        var executor = new TaskExecutor();
        var task = new TaskModel(TaskType.Prompt, "hi");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);

        Assert.Throws<InvalidOperationException>(() => executor.Execute(task));

        Assert.Equal(TaskState.Failed, task.Status);
        Assert.Equal("handler", task.Result?.TerminationReason);
        Assert.Single(events);
        Assert.Equal(TaskEventType.Failed, events[0].Type);
    }
}
