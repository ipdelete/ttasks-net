using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public sealed class Phase9AgentGraphMetadataConformanceTests
{
    [Fact]
    public void R_AGENTGRAPH_04_05_Task_Metadata_Is_Validated_And_Lifecycle_Neutral()
    {
        var task = CoreTask.Bash("echo ok", metadata: new Dictionary<string, object?>
        {
            ["source"] = "chat-ui",
            ["fanout"] = 3,
            ["nested"] = new Dictionary<string, object?> { ["chatId"] = "48:notes" }
        });
        var executor = new TaskExecutor();
        executor.Register(TaskType.Bash, _ => "ok");

        executor.Execute(task);
        task.SetMetadata("afterSuccess", true);

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.Equal("chat-ui", task.Metadata["source"]);
        Assert.Equal(3L, task.Metadata["fanout"]);
        Assert.True((bool)task.Metadata["afterSuccess"]!);
        Assert.Throws<ArgumentException>(() => task.SetMetadata("", "bad"));
        Assert.Throws<ArgumentException>(() => task.SetMetadata("bad", DateTimeOffset.UtcNow));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<string, object?>)task.Metadata)["bypass"] = "nope");
    }

    [Fact]
    public void R_AGENTGRAPH_04_05_Graph_Metadata_Is_Validated_And_Does_Not_Affect_Scheduling()
    {
        var graph = new TaskGraph("teams summary", metadata: new Dictionary<string, object?>
        {
            ["source"] = "chat-ui",
            ["intent"] = "summarize teams"
        });
        var first = CoreTask.Bash("first");
        var second = CoreTask.Bash("second");
        var executor = new TaskExecutor();
        executor.Register(TaskType.Bash, ctx => ctx.Payload);
        graph.Add(first);
        graph.Add(second, after: new[] { first });

        graph.SetMetadata("planner", "gpt-5.5");
        graph.Run(executor);

        Assert.True(graph.Ok);
        Assert.Equal(new[] { first.Id }, graph.Dependencies(second).Select(task => task.Id));
        Assert.Equal("chat-ui", graph.Metadata["source"]);
        Assert.Equal("gpt-5.5", graph.Metadata["planner"]);
        Assert.Throws<ArgumentException>(() => graph.SetMetadata("bad", new object()));
    }
}
