using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase2EventShapeConformanceTests
{
    [Fact]
    public void R_EVT_01_Defines_Every_Required_Event_Type()
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
    public void R_EVT_11_Event_Fields_Are_Read_Only_After_Construction()
    {
        var mutableProperties = typeof(TaskEvent)
            .GetProperties()
            .Where(property => property.SetMethod?.IsPublic == true)
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(mutableProperties);
    }

    [Fact]
    public void R_EVT_11_Event_Task_Remains_A_Live_Reference()
    {
        var task = CoreTask.Bash("x");
        var evt = new TaskEvent(TaskEventType.Started, task, TaskState.Running, previousStatus: TaskState.Pending);

        task.TransitionTo(TaskState.Running);

        Assert.Same(task, evt.Task);
        Assert.Equal(TaskState.Running, evt.Task.Status);
    }

    [Theory]
    [InlineData(TaskEventType.Started, TaskState.Pending, TaskState.Running)]
    [InlineData(TaskEventType.Succeeded, TaskState.Running, TaskState.Succeeded)]
    [InlineData(TaskEventType.Failed, TaskState.Running, TaskState.Failed)]
    [InlineData(TaskEventType.Cancelled, TaskState.Failed, TaskState.Cancelled)]
    [InlineData(TaskEventType.Blocked, TaskState.Pending, TaskState.Blocked)]
    public void R_EVT_12_Status_Changing_Events_Carry_PreviousStatus_And_Matching_Status(
        TaskEventType type,
        TaskState previous,
        TaskState current)
    {
        var task = CoreTask.Bash("x");
        var evt = new TaskEvent(type, task, current, previousStatus: previous);

        Assert.Equal(previous, evt.PreviousStatus);
        Assert.Equal(current, evt.Status);
    }

    [Theory]
    [InlineData(TaskEventType.Progress)]
    [InlineData(TaskEventType.Output)]
    [InlineData(TaskEventType.PersistenceFailed)]
    public void R_EVT_12_Non_Status_Events_Have_Null_PreviousStatus(TaskEventType type)
    {
        var task = CoreTask.Bash("x");
        var evt = type switch
        {
            TaskEventType.Progress => new TaskEvent(type, task, TaskState.Running, progressPercent: 1),
            TaskEventType.Output => new TaskEvent(type, task, TaskState.Running, outputStream: "stdout", outputChunk: "x"),
            TaskEventType.PersistenceFailed => new TaskEvent(type, task, task.Status, error: "store failed"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        };

        Assert.Null(evt.PreviousStatus);
    }

    [Theory]
    [InlineData(TaskEventType.Progress)]
    [InlineData(TaskEventType.Output)]
    [InlineData(TaskEventType.PersistenceFailed)]
    public void R_EVT_12_Non_Status_Events_Reject_PreviousStatus(TaskEventType type)
    {
        var task = CoreTask.Bash("x");

        Assert.Throws<ArgumentException>(() => type switch
        {
            TaskEventType.Progress => new TaskEvent(type, task, TaskState.Running, previousStatus: TaskState.Pending, progressPercent: 1),
            TaskEventType.Output => new TaskEvent(type, task, TaskState.Running, previousStatus: TaskState.Pending, outputStream: "stdout", outputChunk: "x"),
            TaskEventType.PersistenceFailed => new TaskEvent(type, task, task.Status, previousStatus: TaskState.Pending, error: "store failed"),
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null)
        });
    }

    [Fact]
    public void R_EVT_13_Persistence_Failed_Event_Shape_References_Task_And_Error_Without_PreviousStatus()
    {
        var task = CoreTask.Bash("x");

        var evt = new TaskEvent(TaskEventType.PersistenceFailed, task, task.Status, error: "store failed");

        Assert.Equal(TaskEventType.PersistenceFailed, evt.Type);
        Assert.Same(task, evt.Task);
        Assert.Equal(task.Id, evt.TaskId);
        Assert.Equal("store failed", evt.Error);
        Assert.Null(evt.PreviousStatus);
    }

    [Fact]
    public void R_EVT_14_Output_Carries_Stream_And_Chunk()
    {
        var task = CoreTask.Bash("x");

        var stdout = new TaskEvent(TaskEventType.Output, task, TaskState.Running, outputStream: "stdout", outputChunk: "out");
        var stderr = new TaskEvent(TaskEventType.Output, task, TaskState.Running, outputStream: "stderr", outputChunk: "err");

        Assert.Equal("stdout", stdout.OutputStream);
        Assert.Equal("out", stdout.OutputChunk);
        Assert.Equal("stderr", stderr.OutputStream);
        Assert.Equal("err", stderr.OutputChunk);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stdall")]
    public void R_EVT_14_Output_Rejects_Invalid_Stream_Values(string? outputStream)
    {
        var task = CoreTask.Bash("x");

        Assert.ThrowsAny<ArgumentException>(() => new TaskEvent(TaskEventType.Output, task, TaskState.Running, outputStream: outputStream, outputChunk: "x"));
    }

    [Fact]
    public void R_EVT_14_Output_Rejects_Missing_Chunk()
    {
        var task = CoreTask.Bash("x");

        Assert.Throws<ArgumentNullException>(() => new TaskEvent(TaskEventType.Output, task, TaskState.Running, outputStream: "stdout"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(50)]
    [InlineData(100)]
    public void R_EVT_15_Progress_Accepts_Percent_In_Closed_Range(double percent)
    {
        var task = CoreTask.Bash("x");

        var evt = new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressPercent: percent);

        Assert.Equal(percent, evt.ProgressPercent);
    }

    [Fact]
    public void R_EVT_15_Progress_Accepts_Message_Only()
    {
        var task = CoreTask.Bash("x");

        var evt = new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressMessage: "working");

        Assert.Equal("working", evt.ProgressMessage);
        Assert.Null(evt.ProgressPercent);
    }

    [Fact]
    public void R_EVT_15_Progress_Rejects_Empty_Event()
    {
        var task = CoreTask.Bash("x");

        Assert.Throws<InvalidOperationException>(() => new TaskEvent(TaskEventType.Progress, task, TaskState.Running));
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(100.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void R_EVT_15_Progress_Rejects_Invalid_Percent(double percent)
    {
        var task = CoreTask.Bash("x");

        Assert.Throws<ArgumentOutOfRangeException>(() => new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressPercent: percent));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void R_EVT_15_Progress_Rejects_Empty_Message(string message)
    {
        var task = CoreTask.Bash("x");

        Assert.Throws<ArgumentException>(() => new TaskEvent(TaskEventType.Progress, task, TaskState.Running, progressMessage: message));
    }
}
