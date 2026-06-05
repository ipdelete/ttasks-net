using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;

public class Phase3TaskContextConformanceTests
{
    [Fact]
    public void R_EXEC_09_Context_Exposes_ReadOnly_Task_View()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("payload", title: "title", description: "desc", timeout: 10);

        executor.Register(TaskType.Bash, ctx =>
        {
            Assert.Equal(task.Id, ctx.Id);
            Assert.Equal("title", ctx.Title);
            Assert.Equal("desc", ctx.Description);
            Assert.Equal("payload", ctx.Payload);
            Assert.Equal(TaskType.Bash, ctx.Type);
            Assert.Equal(10, ctx.Timeout);
            Assert.Equal(Ttasks.Core.TaskStatus.Running, ctx.Status);
            Assert.Same(task, ctx.Task);
            return "ok";
        });

        executor.Execute(task);
    }

    [Fact]
    public void R_EXEC_10_Context_Upstream_Is_Copied_At_Construction()
    {
        var executor = new TaskExecutor();
        var parent = CoreTask.Bash("parent");
        var late = CoreTask.Bash("late");
        var task = CoreTask.Bash("child");
        var upstream = new Dictionary<string, CoreTask> { [parent.Id] = parent };

        executor.Register(TaskType.Bash, ctx =>
        {
            upstream[late.Id] = late;
            Assert.Single(ctx.Upstream);
            Assert.Same(parent, ctx.Upstream[parent.Id]);
            Assert.False(ctx.Upstream.ContainsKey(late.Id));
            Assert.Throws<NotSupportedException>(() => ((IDictionary<string, CoreTask>)ctx.Upstream).Add(late.Id, late));
            return "ok";
        });

        executor.Execute(task, upstream: upstream);
    }

    [Fact]
    public void R_EXEC_10_Execute_Passes_Upstream_Task_Refs_To_Handler()
    {
        var executor = new TaskExecutor();
        var parent = CoreTask.Bash("parent");
        var task = CoreTask.Bash("child");

        executor.Register(TaskType.Bash, ctx =>
        {
            Assert.Same(parent, ctx.Upstream[parent.Id]);
            return "ok";
        });

        executor.Execute(task, upstream: new Dictionary<string, CoreTask> { [parent.Id] = parent });
    }

    [Fact]
    public void R_EXEC_11_EmitProgress_Uses_Executor_Event_Bus()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.Register(TaskType.Bash, ctx =>
        {
            ctx.EmitProgress(percent: 25, message: "working");
            return "ok";
        });

        executor.Execute(task);

        var progress = Assert.Single(events.Where(evt => evt.Type == TaskEventType.Progress));
        Assert.Equal(25, progress.ProgressPercent);
        Assert.Equal("working", progress.ProgressMessage);
        Assert.Same(task, progress.Task);
    }

    [Fact]
    public void R_EXEC_12_EmitProgress_After_Running_Cancellation_Throws_Cancellation()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");

        executor.Register(TaskType.Bash, ctx =>
        {
            executor.Cancel(task);
            Assert.True(ctx.Cancelled);
            Assert.Throws<OperationCanceledException>(() => ctx.EmitProgress(percent: 1));
            return "ignored";
        });

        Assert.Throws<OperationCanceledException>(() => executor.Execute(task));
        Assert.Equal(Ttasks.Core.TaskStatus.Cancelled, task.Status);
    }

    [Fact]
    public void R_EXEC_12_RaiseIfCancelled_Honors_Cancellation_Flag()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");

        executor.Register(TaskType.Bash, ctx =>
        {
            executor.Cancel(task);
            Assert.Throws<OperationCanceledException>(ctx.RaiseIfCancelled);
            return "ignored";
        });

        Assert.Throws<OperationCanceledException>(() => executor.Execute(task));
    }
}
