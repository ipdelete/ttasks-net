using Ttasks.Core;

namespace Ttasks.Tests;

using TaskModel = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class EventBusTests
{
    [Fact]
    public void SubscribeAndUnsubscribe_OnlyDeliverEventsWhileRegistered()
    {
        var bus = new EventBus();
        var seen = new List<TaskEvent>();
        var subscription = bus.Subscribe(seen.Add);

        bus.Publish(new TaskEvent(TaskEventType.Started, new TaskModel(TaskType.Bash, "echo hi"), TaskState.Running, previousStatus: TaskState.Pending));

        subscription.Dispose();
        bus.Publish(new TaskEvent(TaskEventType.Succeeded, new TaskModel(TaskType.Bash, "echo hi"), TaskState.Succeeded, previousStatus: TaskState.Running));

        Assert.Single(seen);
        Assert.Equal(TaskEventType.Started, seen[0].Type);

        subscription.Dispose();
    }

    [Fact]
    public void Publish_CapturesSubscriberErrorsAndContinues()
    {
        var bus = new EventBus();
        var seen = new List<TaskEvent>();

        bus.Subscribe(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe(seen.Add);

        bus.Publish(new TaskEvent(TaskEventType.Progress, new TaskModel(TaskType.Bash, "echo hi"), TaskState.Running, progressPercent: 1));

        Assert.Single(seen);
        Assert.Single(bus.Errors);
        Assert.IsType<InvalidOperationException>(bus.Errors[0]);
    }

    [Fact]
    public void TaskEvent_ExposesEventFields()
    {
        var task = new TaskModel(TaskType.Bash, "echo hi");
        var evt = new TaskEvent(TaskEventType.Failed, task, TaskState.Failed, previousStatus: TaskState.Running,
            error: "boom", progressPercent: 25, progressMessage: "warming up", outputStream: "stdout", outputChunk: "x");

        Assert.Equal(TaskEventType.Failed, evt.Type);
        Assert.Equal(task.Id, evt.TaskId);
        Assert.Equal(task, evt.Task);
        Assert.Equal(TaskState.Failed, evt.Status);
        Assert.Equal(TaskState.Running, evt.PreviousStatus);
        Assert.Equal("boom", evt.Error);
        Assert.Equal(25, evt.ProgressPercent);
        Assert.Equal("warming up", evt.ProgressMessage);
        Assert.Equal("stdout", evt.OutputStream);
        Assert.Equal("x", evt.OutputChunk);
    }
}
