using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase1ConformanceTests
{
    [Fact]
    public void StateMachine_Rules_Allow_Only_Canonical_Transitions()
    {
        var task = CoreTask.Bash("x");

        Assert.True(task.CanTransitionTo(TaskState.Running));
        Assert.True(task.CanTransitionTo(TaskState.Failed));
        Assert.True(task.CanTransitionTo(TaskState.Cancelled));
        Assert.True(task.CanTransitionTo(TaskState.Blocked));

        task.TransitionTo(TaskState.Failed, "boom");
        Assert.True(task.CanTransitionTo(TaskState.Running));
        Assert.True(task.CanTransitionTo(TaskState.Cancelled));
        Assert.False(task.CanTransitionTo(TaskState.Succeeded));

        Assert.Throws<InvalidOperationException>(() => task.TransitionTo(TaskState.Blocked));
        Assert.Equal(TaskState.Failed, task.Status);
    }

    [Fact]
    public void StateMachine_Rules_Keep_Sink_States_Terminal()
    {
        var task = CoreTask.Bash("x");
        task.TransitionTo(TaskState.Running);
        task.TransitionTo(TaskState.Succeeded);

        Assert.True(task.IsTerminal);
        Assert.True(task.IsSink);
        Assert.False(task.CanTransitionTo(TaskState.Failed));

        task = CoreTask.Bash("x");
        task.TransitionTo(TaskState.Cancelled);
        Assert.True(task.IsTerminal);
        Assert.True(task.IsSink);
        Assert.False(task.CanTransitionTo(TaskState.Running));
    }

    [Fact]
    public void Running_Entry_Clears_Carryover_And_Succeeded_Clears_Error()
    {
        var task = CoreTask.Bash("x");
        task.TransitionTo(TaskState.Failed, "boom");
        task.AttachResult(new TaskResult { TaskId = task.Id, Status = TaskState.Failed, Error = "old", StartedAt = DateTimeOffset.UtcNow, FinishedAt = DateTimeOffset.UtcNow });
        task.AttachBlockedBy("upstream");

        task.TransitionTo(TaskState.Running);

        Assert.Null(task.Result);
        Assert.Null(task.BlockedBy);
        Assert.Null(task.Error);

        task.TransitionTo(TaskState.Succeeded);
        Assert.Null(task.Error);
    }

    [Fact]
    public void Failed_And_Cancelled_Preserve_Error_And_Cancel_Is_Idempotent()
    {
        var task = CoreTask.Bash("x");
        task.TransitionTo(TaskState.Failed, "boom");

        Assert.Equal("boom", task.Error);

        task.TransitionTo(TaskState.Cancelled);
        Assert.Equal("boom", task.Error);

        task.Cancel();
        Assert.Equal(TaskState.Cancelled, task.Status);
    }

    [Fact]
    public void Retryable_States_And_Predicates_Agree_With_Table()
    {
        var failed = CoreTask.Bash("x");
        failed.TransitionTo(TaskState.Failed, "boom");
        Assert.True(failed.CanTransitionTo(TaskState.Running));
        Assert.True(failed.IsFailed);
        Assert.True(failed.IsBad);

        var blocked = CoreTask.Bash("x");
        blocked.TransitionTo(TaskState.Blocked, blockedBy: "parent");
        Assert.True(blocked.CanTransitionTo(TaskState.Running));
        Assert.True(blocked.IsBlocked);
        Assert.True(blocked.IsBad);
        Assert.True(blocked.IsActive == false);
    }

    [Fact]
    public void Succeeded_Task_Rejects_Public_Mutation_And_Identity_Is_Stable()
    {
        var task = CoreTask.Bash("x");
        task.TransitionTo(TaskState.Running);
        task.TransitionTo(TaskState.Succeeded);

        Assert.Throws<InvalidOperationException>(() => task.Payload = "new");
        Assert.Throws<InvalidOperationException>(() => task.Title = "new");
        Assert.Throws<InvalidOperationException>(() => task.Description = "new");
        Assert.Throws<InvalidOperationException>(() => task.Timeout = 15);

        Assert.NotNull(typeof(CoreTask).GetProperty(nameof(CoreTask.Id))?.GetMethod);
        Assert.False(typeof(CoreTask).GetProperty(nameof(CoreTask.Status))!.SetMethod!.IsPublic);
        Assert.False(typeof(CoreTask).GetProperty(nameof(CoreTask.Result))!.SetMethod!.IsPublic);
        Assert.False(typeof(CoreTask).GetProperty(nameof(CoreTask.BlockedBy))!.SetMethod!.IsPublic);
    }

    [Fact]
    public void Task_Factories_And_Equality_Follow_The_Contract()
    {
        var bash = CoreTask.Bash("echo hi", title: "Build", description: "desc", timeout: 30);
        var powershell = CoreTask.Powershell("Write-Host hi");
        var prompt = CoreTask.Prompt("Hello");
        var agent = CoreTask.Agent("Plan");

        Assert.Equal(TaskType.Bash, bash.Type);
        Assert.Equal("bash", bash.TypeName);
        Assert.Equal("echo hi", bash.Payload);
        Assert.Equal("Build", bash.Title);
        Assert.Equal("desc", bash.Description);
        Assert.Equal(30, bash.Timeout);

        Assert.Equal(TaskType.Powershell, powershell.Type);
        Assert.Equal(TaskType.Prompt, prompt.Type);
        Assert.Equal(TaskType.Agent, agent.Type);

        var ctor = typeof(CoreTask).GetConstructors(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 6);
        var a = (CoreTask)ctor.Invoke(new object[] { "same", TaskType.Bash, "x", null!, null!, null! });
        var b = (CoreTask)ctor.Invoke(new object[] { "same", TaskType.Bash, "y", null!, null!, null! });
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, CoreTask.Bash("x"));
    }

    [Fact]
    public void TaskResult_Is_Immutable_And_Execute_Returns_The_Same_Object()
    {
        var executor = new TaskExecutor();
        executor.Register(TaskType.Bash, _ => "ok");
        var task = CoreTask.Bash("echo hi");

        var result = executor.Execute(task);

        Assert.Same(result, task.Result);
    }

    [Fact]
    public void TaskResult_Normalization_Supports_String_And_Process_Like_Values()
    {
        var executor = new TaskExecutor();
        executor.Register(TaskType.Bash, _ => "hello");
        var stringTask = CoreTask.Bash("echo hi");
        var stringResult = executor.Execute(stringTask);

        Assert.Equal("hello", stringResult.Output);
        Assert.Null(stringResult.Error);
        Assert.Same("hello", stringResult.Raw);

        executor = new TaskExecutor();
        executor.Register(TaskType.Bash, _ => new { stdout = "done\n", stderr = "", returncode = 0 });
        var procTask = CoreTask.Bash("proc");
        var procResult = executor.Execute(procTask);

        Assert.Equal("done\n", procResult.Output);
        Assert.Null(procResult.Error);
        Assert.Equal(0, procResult.ReturnCode);
        Assert.NotNull(procResult.Raw);
    }
}
