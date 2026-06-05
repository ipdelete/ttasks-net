using Ttasks.Core;

namespace Ttasks.Tests;

using CoreTask = Ttasks.Core.Task;
using TaskState = Ttasks.Core.TaskStatus;

public class Phase4StoreIntegrationConformanceTests
{
    [Fact]
    public void R_STORE_22_InMemory_Concurrent_Writes_Are_Safe()
    {
        var store = new InMemoryStore();
        var tasks = Enumerable.Range(0, 128).Select(i => CoreTask.Bash($"task-{i}")).ToArray();

        Parallel.ForEach(tasks, task => store.Tasks.Save(task));

        Assert.Equal(tasks.Length, store.Tasks.Count);
        Assert.All(tasks, task => Assert.Same(task, store.Tasks.Get(task.Id)));
    }

    [Fact]
    public void R_STORE_23_User_Facing_Store_Errors_Propagate_Normally()
    {
        var store = new InMemoryStore();
        var task = CoreTask.Bash("x");

        Assert.Throws<ArgumentException>(() => store.Tasks.Set("other", task));
    }

    [Fact]
    public void R_STORE_23_Executor_Persistence_Failures_Emit_Event_And_Do_Not_Derail_Execution()
    {
        var store = new FailingStore(failTasks: true);
        var executor = new TaskExecutor(store);
        var task = CoreTask.Bash("x");
        var events = new List<TaskEvent>();

        executor.Events.Subscribe(events.Add);
        executor.Register(TaskType.Bash, _ => "ok");

        var result = executor.Execute(task);

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.Equal("ok", result.Output);
        Assert.NotEmpty(executor.PersistenceErrors);
        Assert.Contains(events, evt => evt.Type == TaskEventType.PersistenceFailed && evt.TaskId == task.Id);
        Assert.Contains(events, evt => evt.Type == TaskEventType.Succeeded);
    }

    [Fact]
    public void R_STORE_24_Graph_Run_Persists_At_Start_Before_Handlers_Execute()
    {
        var store = new InMemoryStore();
        var executor = new TaskExecutor(store);
        var graph = new TaskGraph("g");
        var task = CoreTask.Bash("x");
        var sawGraphBeforeHandler = false;

        graph.Add(task);
        executor.Register(TaskType.Bash, _ =>
        {
            sawGraphBeforeHandler = store.Graphs.Has(graph.Id);
            Assert.Same(graph, store.Graphs.Get(graph.Id));
            Assert.True(store.Tasks.Has(task.Id));
            Assert.Equal(TaskState.Running, store.Tasks.Get(task.Id).Status);
            return "ok";
        });

        graph.Run(executor);

        Assert.True(sawGraphBeforeHandler);
    }

    [Fact]
    public void R_STORE_24_Graph_Run_Persists_Final_Statuses_At_End()
    {
        var store = new InMemoryStore();
        var executor = new TaskExecutor(store);
        var graph = new TaskGraph("g");
        var task = CoreTask.Bash("x");

        graph.Add(task);
        executor.Register(TaskType.Bash, _ => "ok");

        graph.Run(executor);

        Assert.Same(graph, store.Graphs.Get(graph.Id));
        Assert.Equal(TaskState.Succeeded, store.Tasks.Get(task.Id).Status);
    }

    [Fact]
    public void R_STORE_24_Invalid_Graph_Does_Not_Persist()
    {
        var store = new InMemoryStore();
        var executor = new TaskExecutor(store);
        var graph = new TaskGraph("invalid");
        var task = CoreTask.Bash("x");
        graph.Add(task, after: new[] { CoreTask.Bash("missing") });

        Assert.Throws<InvalidOperationException>(() => graph.Run(executor));

        Assert.False(store.Graphs.Has(graph.Id));
        Assert.False(store.Tasks.Has(task.Id));
    }

    [Fact]
    public void R_STORE_24_Graph_Persistence_Failure_Is_Captured_And_Does_Not_Propagate()
    {
        var store = new FailingStore(failGraphs: true);
        var executor = new TaskExecutor(store);
        var graph = new TaskGraph("g");
        var task = CoreTask.Bash("x");

        graph.Add(task);
        executor.Register(TaskType.Bash, _ => "ok");

        graph.Run(executor);

        Assert.Equal(TaskState.Succeeded, task.Status);
        Assert.NotEmpty(executor.GraphPersistenceErrors);
    }

    private sealed class FailingStore : ITaskStore
    {
        public FailingStore(bool failTasks = false, bool failGraphs = false)
        {
            var backing = new InMemoryStore();
            Tasks = failTasks ? new FailingTaskCollection() : backing.Tasks;
            Graphs = failGraphs ? new FailingGraphCollection() : backing.Graphs;
        }

        public ITaskCollection Tasks { get; }
        public IGraphCollection Graphs { get; }
    }

    private sealed class FailingTaskCollection : ITaskCollection
    {
        public int Count => 0;
        public IEnumerable<string> Keys => Array.Empty<string>();
        public void Save(CoreTask task) => throw new InvalidOperationException("task save failed");
        public CoreTask Get(string id) => throw new KeyNotFoundException(id);
        public void Set(string id, object value) => throw new InvalidOperationException("task save failed");
        public void Delete(string id) => throw new KeyNotFoundException(id);
        public bool Has(object key) => false;
        public object this[string id]
        {
            get => Get(id);
            set => Set(id, value);
        }
    }

    private sealed class FailingGraphCollection : IGraphCollection
    {
        public int Count => 0;
        public IEnumerable<string> Keys => Array.Empty<string>();
        public void Save(TaskGraph graph) => throw new InvalidOperationException("graph save failed");
        public TaskGraph Get(string id) => throw new KeyNotFoundException(id);
        public void Set(string id, object value) => throw new InvalidOperationException("graph save failed");
        public void Delete(string id) => throw new KeyNotFoundException(id);
        public bool Has(object key) => false;
        public object this[string id]
        {
            get => Get(id);
            set => Set(id, value);
        }
    }
}
