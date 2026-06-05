using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase2EventOrderingConformanceTests
{
    [Fact]
    public void R_EVT_02_Status_Changing_Events_Fire_After_Transition_Is_Applied()
    {
        var executor = new TaskExecutor();
        var observations = new List<(TaskEventType Type, TaskState EventStatus, TaskState TaskStatus)>();
        var task = CoreTask.Bash("x");

        executor.Register(TaskType.Bash, _ => "ok");
        executor.Events.Subscribe(evt => observations.Add((evt.Type, evt.Status, evt.Task.Status)));

        executor.Execute(task);

        Assert.Collection(
            observations,
            started => Assert.Equal((TaskEventType.Started, TaskState.Running, TaskState.Running), started),
            succeeded => Assert.Equal((TaskEventType.Succeeded, TaskState.Succeeded, TaskState.Succeeded), succeeded));
    }

    [Theory]
    [InlineData(TaskEventType.Succeeded)]
    [InlineData(TaskEventType.Failed)]
    [InlineData(TaskEventType.Cancelled)]
    public void R_EVT_03_Terminal_NonBlocked_Events_See_Attached_Result(TaskEventType terminalType)
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        TaskResult? observed = null;

        executor.Events.Subscribe(evt =>
        {
            if (evt.Type == terminalType)
                observed = evt.Task.Result;
        });

        if (terminalType == TaskEventType.Succeeded)
        {
            executor.Register(TaskType.Bash, _ => "ok");
            executor.Execute(task);
        }
        else if (terminalType == TaskEventType.Failed)
        {
            executor.Register(TaskType.Bash, _ => throw new InvalidOperationException("boom"));
            Assert.Throws<InvalidOperationException>(() => executor.Execute(task));
        }
        else
        {
            executor.Cancel(task);
        }

        Assert.NotNull(observed);
        Assert.Same(task.Result, observed);
    }

    [Fact]
    public void R_EVT_03_Blocked_Event_Does_Not_Carry_Result()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        TaskResult? observed = null;

        executor.Events.Subscribe(evt =>
        {
            if (evt.Type == TaskEventType.Blocked)
                observed = evt.Task.Result;
        });

        executor.MarkBlocked(task, "parent");

        Assert.Null(observed);
        Assert.Null(task.Result);
    }

    [Fact]
    public void R_EVT_04_Success_Attempt_Emits_Started_Progress_Then_Terminal()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEventType>();

        executor.Events.Subscribe(evt => events.Add(evt.Type));
        executor.Register(TaskType.Bash, ctx =>
        {
            ctx.EmitProgress(percent: 50, message: "half");
            return "ok";
        });

        executor.Execute(task);

        Assert.Equal(new[] { TaskEventType.Started, TaskEventType.Progress, TaskEventType.Succeeded }, events);
    }

    [Fact]
    public void R_EVT_04_Failed_Attempt_Emits_Started_Then_Failed()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEventType>();

        executor.Events.Subscribe(evt => events.Add(evt.Type));
        executor.Register(TaskType.Bash, _ => throw new InvalidOperationException("boom"));

        Assert.Throws<InvalidOperationException>(() => executor.Execute(task));

        Assert.Equal(new[] { TaskEventType.Started, TaskEventType.Failed }, events);
    }

    [Fact]
    public void R_EVT_04_Cancelled_Attempt_Emits_Started_Then_Cancelled()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEventType>();

        executor.Events.Subscribe(evt => events.Add(evt.Type));
        executor.Register(TaskType.Bash, _ => throw new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => executor.Execute(task));

        Assert.Equal(new[] { TaskEventType.Started, TaskEventType.Cancelled }, events);
    }

    [Fact]
    public void R_EVT_05_Missing_Handler_Failed_Path_Skips_Started()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEventType>();

        executor.Events.Subscribe(evt => events.Add(evt.Type));

        Assert.Throws<InvalidOperationException>(() => executor.Execute(task));

        Assert.Equal(new[] { TaskEventType.Failed }, events);
    }

    [Fact]
    public void R_EVT_05_Pending_Cancel_Path_Skips_Started()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEventType>();

        executor.Events.Subscribe(evt => events.Add(evt.Type));
        executor.Cancel(task);

        Assert.Equal(new[] { TaskEventType.Cancelled }, events);
    }

    [Fact]
    public void R_EVT_05_Blocked_Path_Skips_Started()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEventType>();

        executor.Events.Subscribe(evt => events.Add(evt.Type));
        executor.MarkBlocked(task, "parent");

        Assert.Equal(new[] { TaskEventType.Blocked }, events);
    }

    [Fact]
    public void R_EVT_06_Retries_Are_Independent_Attempts_With_Previous_Status_From_Retry_State()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var attempts = 0;
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.Register(TaskType.Bash, _ =>
        {
            attempts++;
            if (attempts < 3)
                throw new InvalidOperationException("boom");
            return "ok";
        });

        executor.Execute(task, new RetryPolicy(3, 0));

        Assert.Equal(
            new[]
            {
                TaskEventType.Started,
                TaskEventType.Failed,
                TaskEventType.Started,
                TaskEventType.Failed,
                TaskEventType.Started,
                TaskEventType.Succeeded,
            },
            events.Select(evt => evt.Type).ToArray());
        Assert.Equal(TaskState.Pending, events[0].PreviousStatus);
        Assert.Equal(TaskState.Failed, events[2].PreviousStatus);
        Assert.Equal(TaskState.Failed, events[4].PreviousStatus);
    }

    [Fact]
    public void R_EVT_06_Exhausted_Retries_Emit_Started_And_Failed_For_Each_Attempt()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEventType>();

        executor.Events.Subscribe(evt => events.Add(evt.Type));
        executor.Register(TaskType.Bash, _ => throw new InvalidOperationException("boom"));

        Assert.Throws<InvalidOperationException>(() => executor.Execute(task, new RetryPolicy(3, 0)));

        Assert.Equal(
            new[]
            {
                TaskEventType.Started,
                TaskEventType.Failed,
                TaskEventType.Started,
                TaskEventType.Failed,
                TaskEventType.Started,
                TaskEventType.Failed,
            },
            events);
    }

    [Fact]
    public void R_EVT_17_Triggering_Parent_Terminal_Event_Precedes_Blocked_Child_Event()
    {
        var executor = new TaskExecutor();
        var graph = new TaskGraph("blocked ordering");
        var parent = CoreTask.Bash("parent");
        var child = CoreTask.Bash("child");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.Register(TaskType.Bash, _ => throw new InvalidOperationException("boom"));
        graph.Add(parent);
        graph.Add(child, after: new[] { parent });

        graph.Run(executor);

        var parentFailureIndex = events.FindIndex(evt => evt.TaskId == parent.Id && evt.Type == TaskEventType.Failed);
        var childBlockedIndex = events.FindIndex(evt => evt.TaskId == child.Id && evt.Type == TaskEventType.Blocked);

        Assert.InRange(parentFailureIndex, 0, events.Count - 1);
        Assert.InRange(childBlockedIndex, 0, events.Count - 1);
        Assert.True(parentFailureIndex < childBlockedIndex);
        Assert.Equal(parent.Id, child.BlockedBy);
    }
}
