using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase3CancellationRetryConformanceTests
{
    [Theory]
    [InlineData(TaskState.Succeeded)]
    [InlineData(TaskState.Cancelled)]
    public void R_EXEC_13_Cancel_Sink_Task_Is_Silent_NoOp(TaskState state)
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEvent>();

        if (state == TaskState.Succeeded)
        {
            executor.Register(TaskType.Bash, _ => "ok");
            executor.Execute(task);
        }
        else
        {
            task.Cancel();
        }

        var originalResult = task.Result;
        executor.Events.Subscribe(events.Add);
        executor.Cancel(task);
        executor.Cancel(task);

        Assert.Equal(state, task.Status);
        Assert.Same(originalResult, task.Result);
        Assert.Empty(events);
    }

    [Theory]
    [InlineData(TaskState.Pending)]
    [InlineData(TaskState.Failed)]
    [InlineData(TaskState.Blocked)]
    public void R_EXEC_13_Cancel_NonRunning_NonSink_Task_Emits_Exactly_One_Cancelled(TaskState state)
    {
        var executor = new TaskExecutor();
        var task = CreateTaskInState(state);
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.Cancel(task);

        Assert.Equal(TaskState.Cancelled, task.Status);
        Assert.Single(events);
        Assert.Equal(TaskEventType.Cancelled, events[0].Type);
        Assert.Equal(state, events[0].PreviousStatus);
    }

    [Fact]
    public async System.Threading.Tasks.Task R_EXEC_13_Cancel_Running_Is_Observed_By_Execute_And_Emits_One_Cancelled()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var started = new ManualResetEventSlim();
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.Register(TaskType.Bash, ctx =>
        {
            started.Set();
            while (!ctx.Cancelled)
                Thread.Sleep(1);
            ctx.RaiseIfCancelled();
            return "ignored";
        });

        var execution = System.Threading.Tasks.Task.Run(() => executor.Execute(task));
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        executor.Cancel(task);

        await Assert.ThrowsAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(TaskState.Cancelled, task.Status);
        Assert.Single(events.Where(evt => evt.Type == TaskEventType.Cancelled));
    }

    [Fact]
    public void R_EXEC_14_Cancelled_Pending_Result_Fields_Are_Canonical()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");

        executor.Cancel(task);

        Assert.Equal(TaskState.Cancelled, task.Result?.Status);
        Assert.Equal("cancelled", task.Result?.Error);
        Assert.Equal("cancelled", task.Result?.TerminationReason);
        Assert.Equal(task.Result?.StartedAt, task.Result?.FinishedAt);
        Assert.Equal(0, task.Result?.DurationSeconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void R_EXEC_16_RetryPolicy_Rejects_Invalid_MaxAttempts(int maxAttempts)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(maxAttempts));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void R_EXEC_16_RetryPolicy_Rejects_Invalid_Backoff(double backoff)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(1, backoff));
    }

    [Fact]
    public void R_EXEC_16_RetryPolicy_Defaults_Backoff_To_Zero()
    {
        var policy = new RetryPolicy(1);

        Assert.Equal(0, policy.BackoffSeconds);
    }

    [Fact]
    public void R_EXEC_17_Retries_Until_Success()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var attempts = 0;

        executor.Register(TaskType.Bash, _ =>
        {
            attempts++;
            if (attempts < 3)
                throw new InvalidOperationException("boom");
            return "ok";
        });

        var result = executor.Execute(task, new RetryPolicy(3));

        Assert.Equal(3, attempts);
        Assert.Equal("ok", result.Output);
        Assert.Equal(TaskState.Succeeded, task.Status);
    }

    [Fact]
    public void R_EXEC_17_Retry_Exhaustion_Rethrows_Final_Error()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var attempts = 0;

        executor.Register(TaskType.Bash, _ =>
        {
            attempts++;
            throw new InvalidOperationException($"boom-{attempts}");
        });

        var exception = Assert.Throws<InvalidOperationException>(() => executor.Execute(task, new RetryPolicy(3)));

        Assert.Equal(3, attempts);
        Assert.Equal("boom-3", exception.Message);
        Assert.Equal(TaskState.Failed, task.Status);
    }

    [Fact]
    public void R_EXEC_18_Cancellation_Is_Never_Retried()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var attempts = 0;

        executor.Register(TaskType.Bash, _ =>
        {
            attempts++;
            throw new OperationCanceledException();
        });

        Assert.Throws<OperationCanceledException>(() => executor.Execute(task, new RetryPolicy(3)));

        Assert.Equal(1, attempts);
        Assert.Equal(TaskState.Cancelled, task.Status);
    }

    [Fact]
    public void R_EXEC_19_Missing_Handler_Does_Not_Retry()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);

        Assert.Throws<InvalidOperationException>(() => executor.Execute(task, new RetryPolicy(3)));

        Assert.Single(events);
        Assert.Equal(TaskEventType.Failed, events[0].Type);
    }

    [Fact]
    public void R_EXEC_20_Backoff_Is_Observed_Between_Attempts()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var attempts = 0;
        var started = DateTimeOffset.UtcNow;

        executor.Register(TaskType.Bash, _ =>
        {
            attempts++;
            if (attempts == 1)
                throw new InvalidOperationException("boom");
            return "ok";
        });

        executor.Execute(task, new RetryPolicy(2, 0.05));

        Assert.True(DateTimeOffset.UtcNow - started >= TimeSpan.FromMilliseconds(40));
    }

    [Fact]
    public async System.Threading.Tasks.Task R_EXEC_21_Cancellation_During_Backoff_Stops_Further_Attempts()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var attempts = 0;
        var failed = new ManualResetEventSlim();

        executor.Events.Subscribe(evt =>
        {
            if (evt.Type == TaskEventType.Failed)
                failed.Set();
        });
        executor.Register(TaskType.Bash, _ =>
        {
            attempts++;
            throw new InvalidOperationException("boom");
        });

        var execution = System.Threading.Tasks.Task.Run(() => executor.Execute(task, new RetryPolicy(3, 1)));
        Assert.True(failed.Wait(TimeSpan.FromSeconds(2)));
        executor.Cancel(task);

        await Assert.ThrowsAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Equal(1, attempts);
        Assert.Equal(TaskState.Cancelled, task.Status);
    }

    private static CoreTask CreateTaskInState(TaskState state)
    {
        var task = CoreTask.Bash("x");
        switch (state)
        {
            case TaskState.Pending:
                return task;
            case TaskState.Failed:
                task.TransitionTo(TaskState.Failed, "boom");
                return task;
            case TaskState.Blocked:
                task.TransitionTo(TaskState.Blocked, blockedBy: "parent");
                return task;
            default:
                throw new ArgumentOutOfRangeException(nameof(state), state, null);
        }
    }
}
