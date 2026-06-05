using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase3SubmitShutdownPersistenceConformanceTests
{
    [Fact]
    public async System.Threading.Tasks.Task R_EXEC_22_Submit_Returns_Awaitable_That_Completes_With_TaskResult()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        executor.Register(TaskType.Bash, _ => "ok");

        var submitted = executor.Submit(task);
        var result = await submitted;

        Assert.Same(task.Result, result);
        Assert.Equal("ok", result.Output);
    }

    [Fact]
    public void R_EXEC_22_Submit_Rejects_Null_Task_Synchronously()
    {
        var executor = new TaskExecutor();

        Assert.Throws<ArgumentNullException>(() => executor.Submit(null!));
    }

    [Fact]
    public async System.Threading.Tasks.Task R_EXEC_23_Submit_Uses_Same_Lifecycle_As_Execute()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.Register(TaskType.Bash, _ => "ok");

        var result = await executor.Submit(task, new RetryPolicy(1));

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.Same(result, task.Result);
        Assert.Equal(new[] { TaskEventType.Started, TaskEventType.Succeeded }, events.Select(evt => evt.Type));
    }

    [Fact]
    public async System.Threading.Tasks.Task R_EXEC_23_Submit_Honors_RetryPolicy()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var attempts = 0;

        executor.Register(TaskType.Bash, _ =>
        {
            attempts++;
            if (attempts < 2)
                throw new InvalidOperationException("boom");
            return "ok";
        });

        var result = await executor.Submit(task, new RetryPolicy(2));

        Assert.Equal(2, attempts);
        Assert.Equal("ok", result.Output);
    }

    [Fact]
    public async System.Threading.Tasks.Task R_EXEC_24_Submitted_Running_Task_Is_Not_Cancelled_By_Future_Cancel()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var started = new ManualResetEventSlim();
        var release = new ManualResetEventSlim();

        executor.Register(TaskType.Bash, _ =>
        {
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(2)));
            return "ok";
        });

        var submitted = executor.Submit(task);
        Assert.True(started.Wait(TimeSpan.FromSeconds(2)));
        Assert.False(submitted.Cancel());
        release.Set();

        var result = await submitted;
        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.Equal("ok", result.Output);
    }

    [Fact]
    public void R_EXEC_25_Shutdown_Is_Idempotent_And_Rejects_Later_Submit()
    {
        var executor = new TaskExecutor();

        executor.Shutdown();
        executor.Shutdown();

        Assert.True(executor.IsShutdown);
        Assert.Throws<InvalidOperationException>(() => executor.Submit(CoreTask.Bash("x")));
    }

    [Fact]
    public async System.Threading.Tasks.Task R_EXEC_25_Shutdown_Waits_For_Submitted_Work_To_Finish()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var release = new ManualResetEventSlim();

        executor.Register(TaskType.Bash, _ =>
        {
            Assert.True(release.Wait(TimeSpan.FromSeconds(2)));
            return "ok";
        });

        var submitted = executor.Submit(task);
        var shutdown = System.Threading.Tasks.Task.Run(executor.Shutdown);
        Thread.Sleep(20);
        Assert.False(shutdown.IsCompleted);
        release.Set();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(2));
        await submitted;
        Assert.Equal(TaskState.Succeeded, task.Status);
    }

    [Fact]
    public void R_EXEC_25_Close_Aliases_Shutdown()
    {
        var executor = new TaskExecutor();

        executor.Close();

        Assert.True(executor.IsShutdown);
    }

    [Fact]
    public async System.Threading.Tasks.Task R_EXEC_26_Shutdown_From_Worker_Does_Not_Deadlock()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");

        executor.Register(TaskType.Bash, _ =>
        {
            executor.Shutdown();
            return "ok";
        });

        var result = await executor.Submit(task);

        Assert.Equal("ok", result.Output);
        Assert.True(executor.IsShutdown);
    }

    [Fact]
    public void R_EXEC_27_Dispose_Closes_Executor()
    {
        var executor = new TaskExecutor();

        executor.Dispose();

        Assert.True(executor.IsShutdown);
    }

    [Fact]
    public void R_EXEC_32_MarkBlocked_Transitions_And_Emits_Blocked()
    {
        var executor = new TaskExecutor();
        var task = CoreTask.Bash("x");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.MarkBlocked(task, "parent");

        Assert.Equal(TaskState.Blocked, task.Status);
        Assert.Equal("parent", task.BlockedBy);
        var evt = Assert.Single(events);
        Assert.Equal(TaskEventType.Blocked, evt.Type);
        Assert.Equal(TaskState.Pending, evt.PreviousStatus);
    }

    [Fact]
    public void R_EXEC_32_MarkBlocked_Rejects_Empty_ParentId()
    {
        var executor = new TaskExecutor();

        Assert.Throws<ArgumentException>(() => executor.MarkBlocked(CoreTask.Bash("x"), ""));
    }

    [Fact]
    public void R_EXEC_15_Cancel_Persists_Before_Cancelled_Event()
    {
        var store = new InMemoryStore();
        var executor = new TaskExecutor(store);
        var task = CoreTask.Bash("x");
        TaskState? observed = null;

        executor.Events.Subscribe(evt =>
        {
            if (evt.Type == TaskEventType.Cancelled)
                observed = store.Tasks.Get(task.Id).Status;
        });

        executor.Cancel(task);

        Assert.Equal(TaskState.Cancelled, observed);
        Assert.Same(task, store.Tasks.Get(task.Id));
    }

    [Fact]
    public void R_EXEC_33_Persists_Status_Changes_Before_Lifecycle_Events()
    {
        var store = new InMemoryStore();
        var executor = new TaskExecutor(store);
        var task = CoreTask.Bash("x");
        var observed = new List<TaskState>();

        executor.Events.Subscribe(evt =>
        {
            if (evt.Type is TaskEventType.Started or TaskEventType.Succeeded)
                observed.Add(store.Tasks.Get(task.Id).Status);
        });
        executor.Register(TaskType.Bash, _ => "ok");

        executor.Execute(task);

        Assert.Equal(new[] { TaskState.Running, TaskState.Succeeded }, observed);
        Assert.Same(task, store.Tasks.Get(task.Id));
    }

    [Fact]
    public void R_EXEC_33_Graph_Persistence_Hook_Captures_Graph()
    {
        var store = new InMemoryStore();
        var executor = new TaskExecutor(store);
        var graph = new TaskGraph("g");
        var task = CoreTask.Bash("x");

        executor.Register(TaskType.Bash, _ => "ok");
        graph.Add(task);
        graph.Run(executor);

        Assert.Same(graph, store.Graphs.Get(graph.Id));
        Assert.Same(task, store.Tasks.Get(task.Id));
    }
}
