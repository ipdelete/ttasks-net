using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase3ExecutorRegistrationConformanceTests
{
    [Fact]
    public void R_EXEC_01_Register_Rejects_Unknown_Task_Type()
    {
        var executor = new TaskExecutor();

        Assert.Throws<ArgumentOutOfRangeException>(() => executor.Register((TaskType)999, _ => "ok"));
    }

    [Fact]
    public void R_EXEC_01_Register_Rejects_Null_Handler()
    {
        var executor = new TaskExecutor();

        Assert.Throws<ArgumentNullException>(() => executor.Register(TaskType.Bash, null!));
    }

    [Fact]
    public void R_EXEC_01_ReRegistering_Replaces_Handler()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");

        executor.Register(TaskType.Bash, _ => "first");
        executor.Register(TaskType.Bash, _ => "second");

        var result = executor.Execute(task);

        Assert.Equal("second", result.Output);
    }

    [Fact]
    public void R_EXEC_02_IsRegistered_Reflects_State()
    {
        var executor = new TaskExecutor();

        Assert.False(executor.IsRegistered(TaskType.Bash));
        executor.Register(TaskType.Bash, _ => "ok");
        Assert.True(executor.IsRegistered(TaskType.Bash));
        Assert.False(executor.IsRegistered(TaskType.Prompt));
    }

    [Fact]
    public void R_EXEC_02_IsRegistered_Rejects_Unknown_Task_Type()
    {
        var executor = new TaskExecutor();

        Assert.Throws<ArgumentOutOfRangeException>(() => executor.IsRegistered((TaskType)999));
    }

    [Fact]
    public void R_EXEC_03_Default_Executor_Is_Handler_Free_For_Embedding()
    {
        var executor = new TaskExecutor();

        Assert.All(Enum.GetValues<TaskType>(), type => Assert.False(executor.IsRegistered(type)));
    }

    [Fact]
    public void R_EXEC_04_Execute_Rejects_Null_Task_Before_Emitting_Events()
    {
        var executor = new TaskExecutor();
        var events = new List<TaskEvent>();
        executor.Events.Subscribe(events.Add);

        Assert.Throws<ArgumentNullException>(() => executor.Execute(null!));

        Assert.Empty(events);
    }

    [Fact]
    public void R_EXEC_05_Success_Drives_Canonical_Lifecycle()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.Register(TaskType.Bash, _ => "ok");

        var result = executor.Execute(task);

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.Same(result, task.Result);
        Assert.Equal("ok", result.Output);
        Assert.Equal(new[] { TaskEventType.Started, TaskEventType.Succeeded }, events.Select(evt => evt.Type));
    }

    [Fact]
    public void R_EXEC_05_Handler_Error_Drives_Failed_Lifecycle()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.Register(TaskType.Bash, _ => throw new InvalidOperationException("boom"));

        Assert.Throws<InvalidOperationException>(() => executor.Execute(task));

        Assert.Equal(TaskState.Failed, task.Status);
        Assert.Equal("boom", task.Error);
        Assert.Equal("handler", task.Result?.TerminationReason);
        Assert.Equal(new[] { TaskEventType.Started, TaskEventType.Failed }, events.Select(evt => evt.Type));
    }

    [Fact]
    public void R_EXEC_05_Handler_Cancellation_Drives_Cancelled_Lifecycle()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.Register(TaskType.Bash, _ => throw new OperationCanceledException());

        Assert.Throws<OperationCanceledException>(() => executor.Execute(task));

        Assert.Equal(TaskState.Cancelled, task.Status);
        Assert.Equal("cancelled", task.Result?.TerminationReason);
        Assert.Equal(new[] { TaskEventType.Started, TaskEventType.Cancelled }, events.Select(evt => evt.Type));
    }

    [Fact]
    public void R_EXEC_06_Missing_Handler_Emits_Only_Failed()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);

        Assert.Throws<InvalidOperationException>(() => executor.Execute(task));

        Assert.Equal(TaskState.Failed, task.Status);
        Assert.Equal("handler", task.Result?.TerminationReason);
        Assert.Equal(new[] { TaskEventType.Failed }, events.Select(evt => evt.Type));
        Assert.Equal(TaskState.Pending, events.Single().PreviousStatus);
    }

    [Fact]
    public void R_EXEC_07_Execute_Refuses_Already_Cancelled_Task_Without_Calling_Handler()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var called = false;
        var events = new List<TaskEvent>();

        task.Cancel();
        executor.Events.Subscribe(events.Add);
        executor.Register(TaskType.Bash, _ =>
        {
            called = true;
            return "ok";
        });

        Assert.Throws<OperationCanceledException>(() => executor.Execute(task));

        Assert.False(called);
        Assert.Empty(events);
    }

    [Fact]
    public void R_EXEC_08_Execute_Refuses_Succeeded_Task_Without_Mutation()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        executor.Register(TaskType.Bash, _ => "ok");
        executor.Execute(task);
        var result = task.Result;

        Assert.Throws<InvalidOperationException>(() => executor.Execute(task));

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.Same(result, task.Result);
    }
}
