using System.Text.Json;
using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;

public sealed class Phase9UpstreamPromptConformanceTests
{
    [Fact]
    public void R_AGENTGRAPH_10_Prompt_Handler_Does_Not_Include_Upstream_By_Default()
    {
        var provider = new RecordingLlmProvider("ok");
        var parent = SucceededTask("parent", "parent output", raw: new { secret = "raw" });
        var child = CoreTask.Prompt("summarize");
        var executor = new TaskExecutor();
        executor.Register(TaskType.Prompt, CopilotHandlers.MakePromptHandler(provider));

        executor.Execute(child, upstream: new Dictionary<string, CoreTask> { [parent.Id] = parent });

        Assert.Equal("summarize", provider.Requests.Single().Prompt);
    }

    [Fact]
    public void R_AGENTGRAPH_11_12_13_14_Prompt_Handler_Composes_Deterministic_Separated_Upstream_Envelope()
    {
        var provider = new RecordingLlmProvider("summary");
        var first = SucceededTask("read-b", "B output", raw: new { secret = "do-not-send" }, metadata: new Dictionary<string, object?> { ["plannerId"] = "b" });
        var second = SucceededTask("read-a", "A output", metadata: new Dictionary<string, object?> { ["plannerId"] = "a" });
        var missing = CoreTask.Bash("not run", title: "Missing", metadata: new Dictionary<string, object?> { ["plannerId"] = "missing" });
        var child = CoreTask.Prompt("Summarize the upstream Teams reads.");
        var executor = new TaskExecutor();
        executor.Register(TaskType.Prompt, CopilotHandlers.MakePromptHandler(provider, new LlmHandlerOptions { IncludeUpstreamResults = true }));

        executor.Execute(
            child,
            upstream: new Dictionary<string, CoreTask>
            {
                [first.Id] = first,
                [second.Id] = second,
                [missing.Id] = missing
            },
            orderedUpstream: new[] { first, second, missing });

        var prompt = provider.Requests.Single().Prompt;
        Assert.StartsWith("Instruction:\nSummarize the upstream Teams reads.\n\nUpstream results:\n", prompt);
        Assert.DoesNotContain("do-not-send", prompt);

        using var document = JsonDocument.Parse(prompt.Split("Upstream results:\n", StringSplitOptions.None)[1]);
        var entries = document.RootElement.EnumerateArray().ToArray();
        Assert.Equal(new[] { "read-b", "read-a", missing.Id }, entries.Select(entry => entry.GetProperty("id").GetString()));
        Assert.Equal("B output", entries[0].GetProperty("output").GetString());
        Assert.Equal("bash", entries[0].GetProperty("type").GetString());
        Assert.Equal("Succeeded", entries[0].GetProperty("status").GetString());
        Assert.False(entries[0].TryGetProperty("raw", out _));
        Assert.Equal("", entries[2].GetProperty("output").GetString());
        Assert.Equal(JsonValueKind.Null, entries[2].GetProperty("error").ValueKind);
        Assert.Equal("missing", entries[2].GetProperty("metadata").GetProperty("plannerId").GetString());
    }

    [Fact]
    public void R_AGENTGRAPH_11_Manual_Upstream_Map_Uses_Lexical_Task_Id_TieBreaker()
    {
        var provider = new RecordingLlmProvider("summary");
        var readB = SucceededTask("read-b", "B output");
        var readA = SucceededTask("read-a", "A output");
        var child = CoreTask.Prompt("Summarize.");
        var executor = new TaskExecutor();
        executor.Register(TaskType.Prompt, CopilotHandlers.MakePromptHandler(provider, new LlmHandlerOptions { IncludeUpstreamResults = true }));

        executor.Execute(
            child,
            upstream: new Dictionary<string, CoreTask>
            {
                [readB.Id] = readB,
                [readA.Id] = readA
            });

        using var document = JsonDocument.Parse(provider.Requests.Single().Prompt.Split("Upstream results:\n", StringSplitOptions.None)[1]);
        Assert.Equal(new[] { readA.Id, readB.Id }, document.RootElement.EnumerateArray().Select(entry => entry.GetProperty("id").GetString()));
    }

    [Fact]
    public void R_AGENTGRAPH_11_15_Agent_Handler_Uses_Graph_Dependency_Order_For_Upstream_Envelope()
    {
        var provider = new RecordingLlmProvider("summary");
        var executor = new TaskExecutor();
        executor.Register(TaskType.Bash, ctx => ctx.Payload);
        executor.Register(TaskType.Agent, CopilotHandlers.MakeAgentHandler(provider, new LlmHandlerOptions { IncludeUpstreamResults = true }));
        var first = CoreTask.Bash("first output", title: "First");
        var second = CoreTask.Bash("second output", title: "Second");
        var summary = CoreTask.Agent("Summarize in declared dependency order.");
        var graph = new TaskGraph("ordered fan-in");
        graph.Add(first);
        graph.Add(second);
        graph.Add(summary, after: new[] { second, first });

        graph.Run(executor, maxWorkers: 2);

        var prompt = provider.Requests.Single().Prompt;
        using var document = JsonDocument.Parse(prompt.Split("Upstream results:\n", StringSplitOptions.None)[1]);
        Assert.Equal(new[] { second.Id, first.Id }, document.RootElement.EnumerateArray().Select(entry => entry.GetProperty("id").GetString()));
        Assert.True(provider.Requests.Single().ToolsEnabled);
    }

    private static CoreTask SucceededTask(string id, string output, object? raw = null, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        var now = DateTimeOffset.Parse("2026-01-01T00:00:00.0000000+00:00");
        return CoreTask.Restore(
            id,
            TaskType.Bash,
            "payload",
            id,
            "description",
            null,
            Ttasks.Core.TaskStatus.Succeeded,
            null,
            null,
            now,
            new TaskResult
            {
                TaskId = id,
                Status = Ttasks.Core.TaskStatus.Succeeded,
                StartedAt = now,
                FinishedAt = now,
                Output = output,
                Raw = raw
            },
            metadata);
    }
}
