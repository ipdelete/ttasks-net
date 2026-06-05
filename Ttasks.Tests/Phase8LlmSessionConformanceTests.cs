using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public sealed class Phase8LlmSessionConformanceTests
{
    [Fact]
    public void R_COP_10_Session_Constructor_Validates_Model_And_Timeout()
    {
        var provider = new RecordingLlmProvider("ok");

        Assert.Throws<ArgumentException>(() => new LlmAgentSession(provider, new LlmSessionOptions { Model = "" }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LlmAgentSession(provider, new LlmSessionOptions { Timeout = TimeSpan.Zero }));
    }

    [Fact]
    public void R_COP_11_Double_Enter_Is_Rejected_And_Exit_Allows_Reentry()
    {
        var provider = new RecordingLlmProvider("ok");
        var session = new LlmAgentSession(provider, new LlmSessionOptions { Model = "m" });

        session.Enter();
        var exception = Assert.Throws<InvalidOperationException>(() => session.Enter());
        Assert.Contains("active", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ok", session.SendAndWait("still active"));
        session.Exit();

        session.Enter();
        Assert.Equal("ok", session.SendAndWait("again"));
        session.Exit();
    }

    [Fact]
    public void R_COP_13_Failed_Enter_Leaves_Session_Closed()
    {
        var provider = new RecordingLlmProvider("ok") { ThrowOnCreate = true };
        var session = new LlmAgentSession(provider, new LlmSessionOptions { Model = "m" });

        Assert.Throws<InvalidOperationException>(() => session.Enter());

        Assert.Throws<InvalidOperationException>(() => session.SendAndWait("closed"));
        session.Enter();
        session.Exit();
    }

    [Fact]
    public void R_COP_14_Exit_Disposes_Active_Provider_Session()
    {
        var provider = new RecordingLlmProvider("ok");
        var session = new LlmAgentSession(provider, new LlmSessionOptions { Model = "m" });

        session.Enter();
        var providerSession = provider.Sessions.Single();
        session.Exit();

        Assert.True(providerSession.Disposed);
    }

    [Fact]
    public void R_COP_15_SendAndWait_Validates_State_Prompt_And_Timeout()
    {
        var provider = new RecordingLlmProvider("ok");
        var session = new LlmAgentSession(provider, new LlmSessionOptions { Model = "m", Timeout = TimeSpan.FromSeconds(5) });

        Assert.Throws<InvalidOperationException>(() => session.SendAndWait("inactive"));
        session.Enter();
        Assert.Throws<ArgumentException>(() => session.SendAndWait(""));
        Assert.Throws<ArgumentOutOfRangeException>(() => session.SendAndWait("x", TimeSpan.Zero));

        session.SendAndWait("valid", TimeSpan.FromSeconds(2));

        Assert.Equal(TimeSpan.FromSeconds(2), provider.Requests.Single().Timeout);
        session.Exit();
    }

    [Fact]
    public async System.Threading.Tasks.Task R_COP_16_Shared_Session_Serializes_Concurrent_Turns()
    {
        var provider = new RecordingLlmProvider();
        provider.QueueResult(LlmTurnResult.Text("one"));
        provider.QueueResult(LlmTurnResult.Text("two"));
        provider.DelayEachSend = TimeSpan.FromMilliseconds(50);
        var session = new LlmAgentSession(provider, new LlmSessionOptions { Model = "m" });
        session.Enter();

        var first = System.Threading.Tasks.Task.Run(() => session.SendAndWait("first"));
        var second = System.Threading.Tasks.Task.Run(() => session.SendAndWait("second"));
        await System.Threading.Tasks.Task.WhenAll(first, second);

        Assert.Equal(new[] { "first", "second" }, provider.Requests.Select(request => request.Prompt));
        Assert.Equal(new[] { "one", "two" }, new[] { first.Result, second.Result });
        session.Exit();
    }

    [Fact]
    public void R_COP_17_Shared_Session_Reuses_One_Provider_Session_Until_Reopened()
    {
        var provider = new RecordingLlmProvider("ok");
        var session = new LlmAgentSession(provider, new LlmSessionOptions { Model = "m" });

        session.Enter();
        session.SendAndWait("first");
        session.SendAndWait("second");
        session.Exit();
        session.Enter();
        session.SendAndWait("third");

        Assert.Equal(2, provider.CreatedSessions);
        Assert.Equal(provider.Sessions[0], provider.RequestOwners[0]);
        Assert.Equal(provider.Sessions[0], provider.RequestOwners[1]);
        Assert.Equal(provider.Sessions[1], provider.RequestOwners[2]);
        session.Exit();
    }

    [Fact]
    public void R_COP_18_19_Session_Handler_Is_A_Normal_Agent_Handler_Only_When_Sync_Active()
    {
        var provider = new RecordingLlmProvider("handled");
        var session = new LlmAgentSession(provider, new LlmSessionOptions { Model = "m", Timeout = TimeSpan.FromSeconds(5) });
        var executor = new TaskExecutor();
        executor.Register(TaskType.Agent, session.Handler());

        Assert.Throws<InvalidOperationException>(() => executor.Execute(CoreTask.Agent("before enter")));
        session.Enter();
        var task = CoreTask.Agent("run", timeout: 2);
        var result = executor.Execute(task);
        session.Exit();

        Assert.Equal("handled", result.Output);
        Assert.True(provider.Requests.Single().ToolsEnabled);
        Assert.Equal(TimeSpan.FromSeconds(2), provider.Requests.Single().Timeout);
        Assert.Throws<InvalidOperationException>(() => executor.Execute(CoreTask.Agent("after exit")));
    }

    [Fact]
    public async System.Threading.Tasks.Task R_COP_18_Handler_Rejects_Async_Active_Session()
    {
        var provider = new RecordingLlmProvider("ok");
        var session = new LlmAgentSession(provider, new LlmSessionOptions { Model = "m" });
        var executor = new TaskExecutor();
        executor.Register(TaskType.Agent, session.Handler());

        await session.EnterAsync();

        Assert.Throws<InvalidOperationException>(() => executor.Execute(CoreTask.Agent("async-active")));
        await session.ExitAsync();
    }

    [Fact]
    public async System.Threading.Tasks.Task R_COP_20_Cancelling_Session_Handler_Aborts_Active_Turn_And_Session_Recovers()
    {
        var provider = new RecordingLlmProvider();
        provider.BlockUntilCancelled = true;
        var session = new LlmAgentSession(provider, new LlmSessionOptions { Model = "m" });
        var executor = new TaskExecutor();
        var task = CoreTask.Agent("wait");
        session.Enter();
        executor.Register(TaskType.Agent, session.Handler());

        var execution = System.Threading.Tasks.Task.Run(() => executor.Execute(task));
        Assert.True(provider.SendStarted.Wait(TimeSpan.FromSeconds(2)));
        executor.Cancel(task);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(3)));
        provider.BlockUntilCancelled = false;
        provider.QueueResult(LlmTurnResult.Text("recovered"));

        Assert.Equal(TaskState.Cancelled, task.Status);
        Assert.True(provider.Sessions.Single().AbortCalled);
        Assert.Equal("recovered", session.SendAndWait("next"));
        session.Exit();
    }

    [Fact]
    public void R_COP_21_22_Event_Subscriptions_Are_Idempotent_And_Isolate_Errors()
    {
        var provider = new RecordingLlmProvider("ok");
        var session = new LlmAgentSession(provider, new LlmSessionOptions { Model = "m" });
        var goodEvents = new List<object>();
        var unsubscribeBad = session.On(_ => throw new InvalidOperationException("subscriber failed"));
        var unsubscribeGood = session.On(goodEvents.Add);
        Assert.Throws<ArgumentNullException>(() => session.On(null!));

        session.Enter();
        provider.Sessions.Single().Emit("event-1");
        unsubscribeBad();
        unsubscribeBad();
        provider.Sessions.Single().Emit("event-2");
        unsubscribeGood();
        provider.Sessions.Single().Emit("event-3");

        Assert.Equal(new object[] { "event-1", "event-2" }, goodEvents);
        Assert.Single(session.EventErrors);
        session.Exit();
    }
}
