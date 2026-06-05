using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public sealed class Phase8LlmHandlerConformanceTests
{
    [Fact]
    public void R_COP_01_Prompt_Handler_Sends_One_Toolless_Turn_And_Returns_Text()
    {
        var provider = new RecordingLlmProvider("hello");
        var executor = new TaskExecutor();
        var task = CoreTask.Prompt("greet me");
        executor.Register(TaskType.Prompt, CopilotHandlers.MakePromptHandler(provider));

        var result = executor.Execute(task);

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.Equal("hello", result.Output);
        var request = Assert.Single(provider.Requests);
        Assert.Equal("greet me", request.Prompt);
        Assert.False(request.ToolsEnabled);
    }

    [Fact]
    public void R_COP_02_Agent_Handler_Sends_One_ToolCapable_Turn_And_Uses_Fresh_Sessions()
    {
        var provider = new RecordingLlmProvider("agent-ok");
        var executor = new TaskExecutor();
        executor.Register(TaskType.Agent, CopilotHandlers.MakeAgentHandler(provider));

        executor.Execute(CoreTask.Agent("first"));
        executor.Execute(CoreTask.Agent("second"));

        Assert.Equal(2, provider.CreatedSessions);
        Assert.All(provider.Requests, request => Assert.True(request.ToolsEnabled));
        Assert.Equal(new[] { "first", "second" }, provider.Requests.Select(request => request.Prompt));
    }

    [Fact]
    public void R_COP_03_Empty_And_NonText_Responses_Normalize_To_Empty_String()
    {
        var provider = new RecordingLlmProvider();
        provider.QueueResult(LlmTurnResult.Empty);
        provider.QueueResult(LlmTurnResult.NonText(new { ignored = true }));
        var executor = new TaskExecutor();
        executor.Register(TaskType.Prompt, CopilotHandlers.MakePromptHandler(provider));

        var empty = executor.Execute(CoreTask.Prompt("empty"));
        var nonText = executor.Execute(CoreTask.Prompt("non-text"));

        Assert.Equal("", empty.Output);
        Assert.Equal("", nonText.Output);
    }

    [Fact]
    public void R_COP_04_Provider_Error_Propagates_Through_Executor_Failure()
    {
        var provider = new RecordingLlmProvider();
        provider.QueueException(new InvalidOperationException("sdk failed"));
        var executor = new TaskExecutor();
        var task = CoreTask.Prompt("x");
        executor.Register(TaskType.Prompt, CopilotHandlers.MakePromptHandler(provider));

        var exception = Assert.Throws<InvalidOperationException>(() => executor.Execute(task));

        Assert.Equal("sdk failed", exception.Message);
        Assert.Equal(TaskState.Failed, task.Status);
        Assert.Equal("sdk failed", task.Result?.Error);
    }

    [Fact]
    public void R_COP_05_Handler_Factories_Validate_Model_And_Timeout()
    {
        var provider = new RecordingLlmProvider("ok");

        Assert.Throws<ArgumentException>(() => CopilotHandlers.MakePromptHandler(provider, new LlmHandlerOptions { Model = "" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => CopilotHandlers.MakePromptHandler(provider, new LlmHandlerOptions { Timeout = TimeSpan.Zero }));
        Assert.Throws<ArgumentException>(() => CopilotHandlers.MakeAgentHandler(provider, new LlmHandlerOptions { Model = " " }));
    }

    [Fact]
    public void R_COP_06_08_Defaults_And_Model_Overrides_Are_Passed_To_Provider()
    {
        var provider = new RecordingLlmProvider("ok");
        var executor = new TaskExecutor();
        executor.Register(TaskType.Prompt, CopilotHandlers.MakePromptHandler(provider));
        executor.Register(TaskType.Agent, CopilotHandlers.MakeAgentHandler(provider, new LlmHandlerOptions { Model = "custom-agent" }));

        executor.Execute(CoreTask.Prompt("prompt"));
        executor.Execute(CoreTask.Agent("agent"));

        Assert.Equal(CopilotHandlers.DefaultPromptModel, provider.Sessions[0].Options.Model);
        Assert.Equal(CopilotHandlers.DefaultPromptTimeout, provider.Requests[0].Timeout);
        Assert.Equal("custom-agent", provider.Sessions[1].Options.Model);
    }

    [Fact]
    public void R_COP_07_Task_Timeout_Overrides_Handler_Default()
    {
        var provider = new RecordingLlmProvider("ok");
        var executor = new TaskExecutor();
        executor.Register(TaskType.Prompt, CopilotHandlers.MakePromptHandler(provider, new LlmHandlerOptions { Timeout = TimeSpan.FromSeconds(60) }));

        executor.Execute(CoreTask.Prompt("x", timeout: 2));

        Assert.Equal(TimeSpan.FromSeconds(2), provider.Requests.Single().Timeout);
    }

    [Fact]
    public async System.Threading.Tasks.Task R_COP_09_MidTurn_Cancellation_Aborts_OneShot_Handler()
    {
        var provider = new RecordingLlmProvider();
        provider.BlockUntilCancelled = true;
        var executor = new TaskExecutor();
        var task = CoreTask.Prompt("wait");
        executor.Register(TaskType.Prompt, CopilotHandlers.MakePromptHandler(provider));

        var execution = System.Threading.Tasks.Task.Run(() => executor.Execute(task));
        Assert.True(provider.SendStarted.Wait(TimeSpan.FromSeconds(2)));
        executor.Cancel(task);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(TaskState.Cancelled, task.Status);
        Assert.True(provider.Sessions.Single().AbortCalled);
    }

    [Fact]
    public void R_COP_25_Default_Executor_Remains_Handler_Free_For_Prompt_And_Agent()
    {
        var executor = new TaskExecutor();
        var prompt = CoreTask.Prompt("hi");

        Assert.False(executor.IsRegistered(TaskType.Prompt));
        Assert.False(executor.IsRegistered(TaskType.Agent));
        Assert.Equal(TaskType.Prompt, prompt.Type);
        Assert.Throws<InvalidOperationException>(() => executor.Execute(prompt));
        Assert.Equal("handler", prompt.Result?.TerminationReason);
    }
}

