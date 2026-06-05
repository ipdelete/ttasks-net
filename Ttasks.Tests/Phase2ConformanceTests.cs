using Ttasks.Core;

namespace Ttasks.Tests;

using TaskModel = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase2ConformanceTests
{
    [Fact]
    public void R_EVT_01_EventTypes_Are_Exhaustive()
    {
        Assert.Equal(
            new[]
            {
                TaskEventType.Started,
                TaskEventType.Progress,
                TaskEventType.Output,
                TaskEventType.Succeeded,
                TaskEventType.Failed,
                TaskEventType.Cancelled,
                TaskEventType.Blocked,
                TaskEventType.PersistenceFailed,
            },
            Enum.GetValues<TaskEventType>());
    }

    [Fact]
    public void R_EVT_07_And_R_EVT_16_Subscriber_Errors_Are_Isolated_And_Collected()
    {
        var bus = new EventBus();
        var seen = new List<TaskEvent>();

        bus.Subscribe(_ => throw new InvalidOperationException("boom"));
        bus.Subscribe(seen.Add);

        bus.Publish(new TaskEvent(TaskEventType.Progress, new TaskModel(TaskType.Bash, "echo hi"), TaskState.Running,
            progressPercent: 25, progressMessage: "warming up"));

        Assert.Single(seen);
        Assert.Single(bus.Errors);
        Assert.Equal("boom", Assert.IsType<InvalidOperationException>(bus.Errors[0]).Message);
    }

    [Fact]
    public void R_EVT_08_Unsubscribe_Is_Idempotent_And_Stops_Future_Events()
    {
        var bus = new EventBus();
        var seen = new List<TaskEvent>();
        var subscription = bus.Subscribe(seen.Add);

        bus.Publish(new TaskEvent(TaskEventType.Started, new TaskModel(TaskType.Bash, "echo hi"), TaskState.Running));

        subscription.Dispose();
        subscription.Dispose();

        bus.Publish(new TaskEvent(TaskEventType.Succeeded, new TaskModel(TaskType.Bash, "echo hi"), TaskState.Succeeded));

        Assert.Single(seen);
        Assert.Equal(TaskEventType.Started, seen[0].Type);
    }

    [Fact]
    public void R_EVT_09_SubscribeScoped_Disposes_Automatically()
    {
        var bus = new EventBus();
        var seen = new List<TaskEvent>();

        using (bus.SubscribeScoped(seen.Add))
        {
            bus.Publish(new TaskEvent(TaskEventType.Started, new TaskModel(TaskType.Bash, "echo hi"), TaskState.Running));
        }

        bus.Publish(new TaskEvent(TaskEventType.Succeeded, new TaskModel(TaskType.Bash, "echo hi"), TaskState.Succeeded));

        Assert.Single(seen);
    }

    [Fact]
    public void R_EVT_11_Events_Keep_Their_Emit_Time_Fields()
    {
        var task = new TaskModel(TaskType.Bash, "echo hi");
        var evt = new TaskEvent(TaskEventType.Failed, task, TaskState.Failed,
            previousStatus: TaskState.Running,
            error: "boom");

        task.TransitionTo(TaskState.Running);
        task.TransitionTo(TaskState.Failed, "later");

        Assert.Equal(TaskEventType.Failed, evt.Type);
        Assert.Equal(TaskState.Failed, evt.Status);
        Assert.Equal(TaskState.Running, evt.PreviousStatus);
        Assert.Equal("boom", evt.Error);
        Assert.Null(evt.ProgressPercent);
        Assert.Null(evt.ProgressMessage);
        Assert.Null(evt.OutputStream);
        Assert.Null(evt.OutputChunk);
    }

    [Fact]
    public void R_EVT_12_PreviousStatus_Matches_Transition_Context()
    {
        var task = new TaskModel(TaskType.Bash, "echo hi");
        task.TransitionTo(TaskState.Running);

        var started = new TaskEvent(TaskEventType.Started, task, TaskState.Running, previousStatus: TaskState.Pending);
        var succeeded = new TaskEvent(TaskEventType.Succeeded, task, TaskState.Succeeded, previousStatus: TaskState.Running);

        Assert.Equal(TaskState.Pending, started.PreviousStatus);
        Assert.Equal(TaskState.Running, succeeded.PreviousStatus);
    }

    [Fact]
    public void R_EVT_14_And_R_EVT_15_Output_And_Progress_Fields_Are_Preserved()
    {
        var task = new TaskModel(TaskType.Bash, "echo hi");
        var outputEvent = new TaskEvent(TaskEventType.Output, task, TaskState.Running, outputStream: "stderr", outputChunk: "oops");
        var progressEvent = new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressPercent: 42, progressMessage: "warming up");

        Assert.Equal("stderr", outputEvent.OutputStream);
        Assert.Equal("oops", outputEvent.OutputChunk);
        Assert.Equal(42, progressEvent.ProgressPercent);
        Assert.Equal("warming up", progressEvent.ProgressMessage);
    }
}
