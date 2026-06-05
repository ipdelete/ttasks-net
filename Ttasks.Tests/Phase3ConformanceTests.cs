using Ttasks.Core;

namespace Ttasks.Tests;

using TaskModel = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase3ConformanceTests
{
    [Fact]
    public void R_EXEC_01_02_Register_And_IsRegistered_Are_Type_Keyed()
    {
        var executor = new TaskExecutor();

        Assert.False(executor.IsRegistered(TaskType.Bash));
        executor.Register(TaskType.Bash, _ => "ok");
        Assert.True(executor.IsRegistered(TaskType.Bash));

        executor.Register(TaskType.Bash, _ => "later");
        Assert.True(executor.IsRegistered(TaskType.Bash));
    }

    [Fact]
    public void R_EXEC_04_05_06_Execute_Canonical_Lifecycle_And_Missing_Handler()
    {
        var executor = new TaskExecutor();
        var task = new TaskModel(TaskType.Bash, "echo hi");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.Register(TaskType.Bash, _ => "ok");

        var result = executor.Execute(task);

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.Same(result, task.Result);
        Assert.Equal("ok", result.Output);
        Assert.Equal(2, events.Count);
        Assert.Equal(TaskEventType.Started, events[0].Type);
        Assert.Equal(TaskEventType.Succeeded, events[1].Type);

        var missing = new TaskModel(TaskType.Prompt, "nope");
        var missingEvents = new List<TaskEvent>();
        var missingExecutor = new TaskExecutor();
        missingExecutor.Events.Subscribe(missingEvents.Add);

        Assert.Throws<InvalidOperationException>(() => missingExecutor.Execute(missing));
        Assert.Equal(TaskState.Failed, missing.Status);
        Assert.Equal("handler", missing.Result?.TerminationReason);
        Assert.Single(missingEvents);
        Assert.Equal(TaskEventType.Failed, missingEvents[0].Type);
    }

    [Fact]
    public void R_EXEC_09_10_11_12_TaskContext_Exposes_ReadOnly_View_And_Progress_Validation()
    {
        var executor = new TaskExecutor();
        var task = new TaskModel(TaskType.Bash, "echo hi");
        var upstream = new Dictionary<string, TaskModel> { ["parent"] = new(TaskType.Prompt, "parent") };

        executor.Register(TaskType.Bash, ctx =>
        {
            Assert.Equal(task.Id, ctx.Id);
            Assert.Equal(task.Title, ctx.Title);
            Assert.Equal(task.Description, ctx.Description);
            Assert.Equal(task.Payload, ctx.Payload);
            Assert.Equal(task.Type, ctx.Type);
            Assert.False(ctx.Cancelled);
            Assert.Equal(task.Status, ctx.Status);
            Assert.Same(task, ctx.Task);
            Assert.Equal(upstream.Keys.Count, ctx.Upstream.Count);
            Assert.Same(upstream["parent"], ctx.Upstream["parent"]);

            Assert.Throws<InvalidOperationException>(() => ctx.EmitProgress());
            Assert.Throws<ArgumentOutOfRangeException>(() => ctx.EmitProgress(percent: 101));
            Assert.Throws<ArgumentOutOfRangeException>(() => ctx.EmitProgress(percent: double.NaN));
            Assert.Throws<ArgumentException>(() => ctx.EmitProgress(message: "   "));

            return "ok";
        });

        var contextTask = new TaskModel(TaskType.Bash, "echo hi");
        var context = new TaskContext(contextTask, executor, upstream);
        Assert.Equal(contextTask.Id, context.Id);

        executor.Execute(task, upstream: upstream);
    }

    [Fact]
    public void R_EXEC_13_14_Cancel_Pending_Task_Attaches_Cancelled_Result()
    {
        var executor = new TaskExecutor();
        var task = new TaskModel(TaskType.Bash, "echo hi");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);

        executor.Cancel(task);

        Assert.Equal(TaskState.Cancelled, task.Status);
        Assert.Equal(TaskState.Cancelled, task.Result?.Status);
        Assert.Equal("cancelled", task.Result?.Error);
        Assert.Equal("cancelled", task.Result?.TerminationReason);
        Assert.Equal(0, task.Result?.DurationSeconds);
        Assert.Single(events);
        Assert.Equal(TaskEventType.Cancelled, events[0].Type);
    }

    [Fact]
    public void R_EXEC_16_17_RetryPolicy_Validation_And_Recovery()
    {
        var executor = new TaskExecutor();
        var task = new TaskModel(TaskType.Bash, "echo hi");
        var attempts = 0;

        executor.Register(TaskType.Bash, _ =>
        {
            attempts++;
            if (attempts < 3) throw new InvalidOperationException("fail");
            return "ok";
        });

        var result = executor.Execute(task, new RetryPolicy(3, 0));

        Assert.Equal(3, attempts);
        Assert.Equal("ok", result.Output);
        Assert.Equal(TaskState.Succeeded, task.Status);
    }
}
