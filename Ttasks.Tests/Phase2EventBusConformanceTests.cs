using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase2EventBusConformanceTests
{
    [Fact]
    public void R_EVT_07_Subscriber_Errors_Do_Not_Stop_Other_Subscribers()
    {
        var bus = new EventBus();
        var task = CoreTask.Bash("x");
        var seen = new List<TaskEvent>();
        var evt = new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressPercent: 10);

        bus.Subscribe(_ => throw new InvalidOperationException("first"));
        bus.Subscribe(seen.Add);
        bus.Subscribe(_ => throw new ApplicationException("second"));

        bus.Publish(evt);

        Assert.Equal(new[] { evt }, seen);
        Assert.Collection(
            bus.Errors,
            first => Assert.IsType<InvalidOperationException>(first),
            second => Assert.IsType<ApplicationException>(second));
    }

    [Fact]
    public void R_EVT_07_Subscriber_Errors_Do_Not_Change_Task_State_Or_Result()
    {
        var bus = new EventBus();
        var task = CoreTask.Bash("x");
        var evt = new TaskEvent(TaskEventType.Progress, task, TaskState.Pending, progressMessage: "queued");

        bus.Subscribe(_ => throw new InvalidOperationException("boom"));

        bus.Publish(evt);

        Assert.Equal(TaskState.Pending, task.Status);
        Assert.Null(task.Result);
        Assert.Null(task.Error);
    }

    [Fact]
    public void R_EVT_08_Subscribe_Returns_Unsubscribe_That_Prevents_Future_Delivery()
    {
        var bus = new EventBus();
        var task = CoreTask.Bash("x");
        var seen = new List<TaskEvent>();
        var subscription = bus.Subscribe(seen.Add);

        bus.Publish(new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressPercent: 1));
        subscription.Dispose();
        bus.Publish(new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressPercent: 2));

        Assert.Single(seen);
        Assert.Equal(1, seen[0].ProgressPercent);
    }

    [Fact]
    public void R_EVT_08_Unsubscribe_Is_Idempotent()
    {
        var bus = new EventBus();
        var task = CoreTask.Bash("x");
        var seen = new List<TaskEvent>();
        var subscription = bus.Subscribe(seen.Add);

        subscription.Dispose();
        subscription.Dispose();
        bus.Publish(new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressPercent: 1));

        Assert.Empty(seen);
    }

    [Fact]
    public void R_EVT_09_SubscribeScoped_Subscribes_For_Using_Block()
    {
        var bus = new EventBus();
        var task = CoreTask.Bash("x");
        var seen = new List<TaskEvent>();

        using (bus.SubscribeScoped(seen.Add))
        {
            bus.Publish(new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressPercent: 1));
        }

        bus.Publish(new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressPercent: 2));

        Assert.Single(seen);
        Assert.Equal(1, seen[0].ProgressPercent);
    }

    [Fact]
    public void R_EVT_09_SubscribeScoped_Unsubscribes_When_Block_Throws()
    {
        var bus = new EventBus();
        var task = CoreTask.Bash("x");
        var seen = new List<TaskEvent>();

        Assert.Throws<InvalidOperationException>((Action)(() =>
        {
            using (bus.SubscribeScoped(seen.Add))
            {
                bus.Publish(new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressPercent: 1));
                throw new InvalidOperationException("boom");
            }
        }));

        bus.Publish(new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressPercent: 2));

        Assert.Single(seen);
        Assert.Equal(1, seen[0].ProgressPercent);
    }

    [Fact]
    public void R_EVT_10_Subscribe_Rejects_Null_Handler()
    {
        var bus = new EventBus();

        Assert.Throws<ArgumentNullException>(() => bus.Subscribe(null!));
    }

    [Fact]
    public void R_EVT_16_Errors_View_Preserves_Order_And_Cannot_Mutate_Internal_List()
    {
        var bus = new EventBus();
        var task = CoreTask.Bash("x");
        var evt = new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressPercent: 10);

        bus.Subscribe(_ => throw new InvalidOperationException("first"));
        bus.Subscribe(_ => throw new ApplicationException("second"));
        bus.Publish(evt);

        var errors = bus.Errors;
        Assert.Equal("first", errors[0].Message);
        Assert.Equal("second", errors[1].Message);
        Assert.Throws<NotSupportedException>(() => ((IList<Exception>)errors).Add(new Exception("external")));
        Assert.Equal(2, bus.Errors.Count);
    }
}
