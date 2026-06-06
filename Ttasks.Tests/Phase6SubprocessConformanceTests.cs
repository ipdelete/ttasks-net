using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase6SubprocessConformanceTests
{
    [Fact]
    public void R_EXEC_28_Bash_Streams_Stdout_And_Retains_Result_Output()
    {
        var executor = TaskExecutor.WithBuiltInHandlers();
        var task = CoreTask.Bash("echo phase6-stdout");
        var events = new List<TaskEvent>();
        executor.Events.Subscribe(events.Add);

        var result = executor.Execute(task);

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.Contains("phase6-stdout", result.Output);
        var output = Assert.Single(events.Where(evt => evt.Type == TaskEventType.Output && evt.OutputStream == "stdout"));
        Assert.Contains("phase6-stdout", output.OutputChunk);
        Assert.True(events.FindIndex(evt => evt.Type == TaskEventType.Output) < events.FindIndex(evt => evt.Type == TaskEventType.Succeeded));
    }

    [Fact]
    public void R_EXEC_28_Bash_Streams_Stderr_Separately_And_Retains_Result_Error()
    {
        var executor = TaskExecutor.WithBuiltInHandlers();
        var task = CoreTask.Bash("echo phase6-stderr >&2");
        var events = new List<TaskEvent>();
        executor.Events.Subscribe(events.Add);

        var result = executor.Execute(task);

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.Contains("phase6-stderr", result.Error);
        Assert.DoesNotContain("phase6-stderr", result.Output);
        var output = Assert.Single(events.Where(evt => evt.Type == TaskEventType.Output && evt.OutputStream == "stderr"));
        Assert.Contains("phase6-stderr", output.OutputChunk);
    }

    [Fact]
    public void R_EXEC_28_Bash_NonZero_Exit_Fails_With_ExitCode_And_Preserved_Output()
    {
        var executor = TaskExecutor.WithBuiltInHandlers();
        var task = CoreTask.Bash("echo before-fail; echo fail-error >&2; exit 7");
        var events = new List<TaskEvent>();
        executor.Events.Subscribe(events.Add);

        var exception = Assert.Throws<TaskExecutionException>(() => executor.Execute(task));

        Assert.Equal(TaskState.Failed, task.Status);
        Assert.Equal("exit_code", task.Result?.TerminationReason);
        Assert.Equal(7, task.Result?.ReturnCode);
        Assert.Contains("before-fail", task.Result?.Output);
        Assert.Contains("fail-error", task.Result?.Error);
        Assert.Equal(7, exception.ReturnCode);
        Assert.True(events.FindIndex(evt => evt.Type == TaskEventType.Output) < events.FindIndex(evt => evt.Type == TaskEventType.Failed));
    }

    [Fact]
    public void R_EXEC_29_Bash_Timeout_Kills_Process_And_Preserves_Partial_Output()
    {
        var executor = TaskExecutor.WithBuiltInHandlers();
        var task = CoreTask.Bash("echo before-timeout; sleep 5", timeout: 2);
        var started = DateTimeOffset.UtcNow;

        var exception = Assert.Throws<TaskTimeoutException>(() => executor.Execute(task));

        Assert.True(DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(4));
        Assert.Equal(TaskState.Failed, task.Status);
        Assert.Equal("timeout", task.Result?.TerminationReason);
        Assert.Contains("before-timeout", task.Result?.Output);
        Assert.Contains("timeout", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void R_EXEC_29_Timed_Out_Bash_Emits_Partial_Output_Before_Failed()
    {
        var executor = TaskExecutor.WithBuiltInHandlers();
        var task = CoreTask.Bash("echo partial-output; sleep 5", timeout: 2);
        var events = new List<TaskEvent>();
        executor.Events.Subscribe(events.Add);

        Assert.Throws<TaskTimeoutException>(() => executor.Execute(task));

        var outputIndex = events.FindIndex(evt => evt.Type == TaskEventType.Output && evt.OutputChunk?.Contains("partial-output") == true);
        var failedIndex = events.FindIndex(evt => evt.Type == TaskEventType.Failed);
        Assert.InRange(outputIndex, 0, events.Count - 1);
        Assert.True(outputIndex < failedIndex);
    }

    [Fact]
    public async System.Threading.Tasks.Task R_EXEC_30_Cancelling_Running_Bash_Terminates_Process_And_Marks_Cancelled()
    {
        var executor = TaskExecutor.WithBuiltInHandlers();
        var task = CoreTask.Bash("echo before-cancel; sleep 10");
        var events = new List<TaskEvent>();
        var sawOutput = new ManualResetEventSlim();
        executor.Events.Subscribe(evt =>
        {
            events.Add(evt);
            if (evt.Type == TaskEventType.Output && evt.OutputChunk?.Contains("before-cancel") == true)
                sawOutput.Set();
        });

        var execution = System.Threading.Tasks.Task.Run(() => executor.Execute(task));
        Assert.True(sawOutput.Wait(TimeSpan.FromSeconds(3)));
        Assert.True(executor.IsRunning(task));
        executor.Cancel(task);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(4)));

        Assert.Equal(TaskState.Cancelled, task.Status);
        Assert.Equal("cancelled", task.Result?.TerminationReason);
        Assert.Contains("before-cancel", task.Result?.Output);
        Assert.Single(events.Where(evt => evt.Type == TaskEventType.Cancelled));
        Assert.False(executor.IsRunning(task));
    }

    [Fact]
    public void R_EXEC_31_Bash_NonUtf8_Output_Does_Not_Crash()
    {
        var executor = TaskExecutor.WithBuiltInHandlers();
        var task = CoreTask.Bash("printf '\\xff\\xfeok\\n'");

        var result = executor.Execute(task);

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.Contains("ok", result.Output);
    }

    [Fact]
    public void R_EXEC_28_Powershell_Handler_Executes_Simple_Command_When_Available()
    {
        if (!TaskExecutor.IsPowerShellAvailable())
            return;

        var executor = TaskExecutor.WithBuiltInHandlers();
        var task = CoreTask.Powershell("Write-Output 'phase6-pwsh'");

        var result = executor.Execute(task);

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.Contains("phase6-pwsh", result.Output);
    }

    [Fact]
    public void R_EXEC_28_Process_Handler_Executes_Argv_Without_Shell_Expansion()
    {
        if (!TaskExecutor.IsPowerShellAvailable())
            return;

        var script = Path.Combine(Path.GetTempPath(), $"ttasks-process-argv-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, "param([string]$x)\nWrite-Output $x\n");
        var executor = TaskExecutor.WithBuiltInHandlers();
        try
        {
            var task = CoreTask.Process(new ProcessCommand(
                "pwsh",
                "-NoProfile",
                "-NonInteractive",
                "-File",
                script,
                "?$filter=isRead eq false&$select=id,subject,from,receivedDateTime&$top=10"));

            var result = executor.Execute(task);

            Assert.Equal(TaskState.Succeeded, task.Status);
            Assert.Contains("?$filter=isRead eq false&$select=id,subject,from,receivedDateTime&$top=10", result.Output);
        }
        finally
        {
            File.Delete(script);
        }
    }

    [Fact]
    public void R_EXEC_34_Output_Events_Are_Not_Persisted()
    {
        var store = new InMemoryStore();
        var executor = TaskExecutor.WithBuiltInHandlers(store);
        var task = CoreTask.Bash("echo output-not-persisted");
        var persistedStatuses = new List<TaskState>();
        executor.Events.Subscribe(evt =>
        {
            if (evt.Type == TaskEventType.Output)
                persistedStatuses.Add(store.Tasks.Get(task.Id).Status);
        });

        executor.Execute(task);

        Assert.Equal(new[] { TaskState.Running }, persistedStatuses);
    }
}
