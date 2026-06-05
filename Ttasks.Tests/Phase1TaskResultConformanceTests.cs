using System.Runtime.CompilerServices;
using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase1TaskResultConformanceTests
{
    [Fact]
    public void R_TASK_12_Succeeded_Event_Sees_Result_Attached_Before_Emit()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        TaskResult? eventResult = null;

        executor.Register(TaskType.Bash, _ => "ok");
        executor.Events.Subscribe(evt =>
        {
            if (evt.Type == TaskEventType.Succeeded)
                eventResult = evt.Task.Result;
        });

        var result = executor.Execute(task);

        Assert.Same(result, eventResult);
        Assert.Same(result, task.Result);
    }

    [Fact]
    public void R_TASK_12_Failed_Event_Sees_Result_Attached_Before_Emit()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        TaskResult? eventResult = null;

        executor.Register(TaskType.Bash, _ => throw new InvalidOperationException("boom"));
        executor.Events.Subscribe(evt =>
        {
            if (evt.Type == TaskEventType.Failed)
                eventResult = evt.Task.Result;
        });

        Assert.Throws<InvalidOperationException>(() => executor.Execute(task));

        Assert.NotNull(eventResult);
        Assert.Same(task.Result, eventResult);
        Assert.Equal(TaskState.Failed, eventResult.Status);
    }

    [Fact]
    public void R_TASK_12_Cancelled_Event_Sees_Result_Attached_Before_Emit()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        TaskResult? eventResult = null;

        executor.Events.Subscribe(evt =>
        {
            if (evt.Type == TaskEventType.Cancelled)
                eventResult = evt.Task.Result;
        });

        executor.Cancel(task);

        Assert.NotNull(eventResult);
        Assert.Same(task.Result, eventResult);
        Assert.Equal(TaskState.Cancelled, eventResult.Status);
    }

    [Fact]
    public void R_TASK_12_Blocked_Task_Does_Not_Get_Result_Attached()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");

        executor.MarkBlocked(task, "parent");

        Assert.Equal(TaskState.Blocked, task.Status);
        Assert.Null(task.Result);
    }

    [Fact]
    public void R_TASK_13_TaskResult_Properties_Are_Init_Only_After_Construction()
    {
        var mutableProperties = typeof(TaskResult)
            .GetProperties()
            .Where(property => property.SetMethod is not null)
            .Where(property => !property.SetMethod!.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)))
            .Select(property => property.Name)
            .ToList();

        Assert.Empty(mutableProperties);
    }

    [Fact]
    public void R_TASK_14_String_Handler_Return_Normalizes_To_Output_And_Raw()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        executor.Register(TaskType.Bash, _ => "hello");

        var result = executor.Execute(task);

        Assert.Equal("hello", result.Output);
        Assert.Null(result.Error);
        Assert.Null(result.ReturnCode);
        Assert.Same("hello", result.Raw);
    }

    [Fact]
    public void R_TASK_14_Process_Like_Return_With_Empty_Stderr_Normalizes_Error_To_Null()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var raw = new { stdout = "done", stderr = "", returncode = 0 };
        executor.Register(TaskType.Bash, _ => raw);

        var result = executor.Execute(task);

        Assert.Equal("done", result.Output);
        Assert.Null(result.Error);
        Assert.Equal(0, result.ReturnCode);
        Assert.Same(raw, result.Raw);
    }

    [Fact]
    public void R_TASK_14_Process_Like_Return_With_Non_Empty_Stderr_Keeps_Error_Message()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var raw = new { stdout = "", stderr = "warning", returncode = 2 };
        executor.Register(TaskType.Bash, _ => raw);

        var result = executor.Execute(task);

        Assert.Equal(string.Empty, result.Output);
        Assert.Equal("warning", result.Error);
        Assert.Equal(2, result.ReturnCode);
        Assert.Same(raw, result.Raw);
    }

    [Fact]
    public void R_TASK_14_Other_Return_Values_Default_Fields_And_Preserve_Raw()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var raw = new object();
        executor.Register(TaskType.Bash, _ => raw);

        var result = executor.Execute(task);

        Assert.Equal(string.Empty, result.Output);
        Assert.Null(result.Error);
        Assert.Null(result.ReturnCode);
        Assert.Same(raw, result.Raw);
    }

    [Fact]
    public void R_TASK_16_Execute_Returns_Same_Result_Reference_Attached_To_Task()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        executor.Register(TaskType.Bash, _ => "ok");

        var result = executor.Execute(task);

        Assert.Same(result, task.Result);
    }

    [Fact]
    public async System.Threading.Tasks.Task R_TASK_16_Submit_Resolves_Same_Result_Reference_Attached_To_Task()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        executor.Register(TaskType.Bash, _ => "ok");

        var result = await executor.Submit(task);

        Assert.Same(result, task.Result);
    }

    [Fact]
    public void R_TASK_16_Subsequent_Attempt_Replaces_Prior_Result_With_New_Object()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var attempts = 0;

        executor.Register(TaskType.Bash, _ =>
        {
            attempts++;
            if (attempts == 1)
                throw new InvalidOperationException("boom");
            return "ok";
        });

        Assert.Throws<InvalidOperationException>(() => executor.Execute(task));
        var failedResult = task.Result;

        var succeededResult = executor.Execute(task);

        Assert.NotNull(failedResult);
        Assert.NotSame(failedResult, succeededResult);
        Assert.Same(succeededResult, task.Result);
    }
}
