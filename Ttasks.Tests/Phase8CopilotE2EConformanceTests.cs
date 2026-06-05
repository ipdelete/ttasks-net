using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public sealed class Phase8CopilotE2EConformanceTests
{
    [Fact]
    public void R_COP_01_02_Handlers_Work_In_Graph_Through_Explicit_Registration()
    {
        var provider = new RecordingLlmProvider();
        provider.QueueResult(LlmTurnResult.Text("prompt output"));
        provider.QueueResult(LlmTurnResult.Text("agent output"));
        var executor = new TaskExecutor();
        executor.Register(TaskType.Prompt, CopilotHandlers.MakePromptHandler(provider));
        executor.Register(TaskType.Agent, CopilotHandlers.MakeAgentHandler(provider));
        var prompt = CoreTask.Prompt("summarize");
        var agent = CoreTask.Agent("act");
        var graph = new TaskGraph("llm graph");
        graph.Add(prompt);
        graph.Add(agent, after: new[] { prompt });

        graph.Run(executor);

        Assert.Equal(TaskState.Succeeded, prompt.Status);
        Assert.Equal(TaskState.Succeeded, agent.Status);
        Assert.Equal("prompt output", prompt.Result?.Output);
        Assert.Equal("agent output", agent.Result?.Output);
        Assert.Equal(new[] { false, true }, provider.Requests.Select(request => request.ToolsEnabled));
    }

    [Fact]
    public void R_COP_17_Shared_Agent_Session_Preserves_State_Across_Graph_Tasks()
    {
        var provider = new RecordingLlmProvider();
        provider.QueueResult(LlmTurnResult.Text("first"));
        provider.QueueResult(LlmTurnResult.Text("second"));
        using var session = new LlmAgentSession(provider, new LlmSessionOptions { Model = "m" }).Enter();
        var executor = new TaskExecutor();
        executor.Register(TaskType.Agent, session.Handler());
        var first = CoreTask.Agent("first turn");
        var second = CoreTask.Agent("second turn");
        var graph = new TaskGraph("shared");
        graph.Add(first);
        graph.Add(second, after: new[] { first });

        graph.Run(executor);

        Assert.Equal(1, provider.CreatedSessions);
        Assert.Equal(new[] { "first turn", "second turn" }, provider.Requests.Select(request => request.Prompt));
    }
}
