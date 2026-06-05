namespace Ttasks.Core;

public interface ITaskStore
{
    ITaskCollection Tasks { get; }
    IGraphCollection Graphs { get; }
}

public interface ITaskCollection
{
    int Count { get; }
    IEnumerable<string> Keys { get; }
    void Save(Task task);
    Task Get(string id);
    void Set(string id, object value);
    void Delete(string id);
    bool Has(object key);
    object this[string id] { get; set; }
}

public interface IGraphCollection
{
    int Count { get; }
    IEnumerable<string> Keys { get; }
    void Save(TaskGraph graph);
    TaskGraph Get(string id);
    void Set(string id, object value);
    void Delete(string id);
    bool Has(object key);
    object this[string id] { get; set; }
}

public sealed class InMemoryStore : ITaskStore
{
    private readonly InMemoryTaskCollection _tasks = new();

    public InMemoryStore()
    {
        Graphs = new InMemoryGraphCollection(_tasks);
    }

    public InMemoryTaskCollection Tasks => _tasks;
    public InMemoryGraphCollection Graphs { get; }

    ITaskCollection ITaskStore.Tasks => Tasks;
    IGraphCollection ITaskStore.Graphs => Graphs;
}

public sealed class InMemoryTaskCollection : ITaskCollection
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Task> _items = new(StringComparer.Ordinal);

    public int Count
    {
        get
        {
            lock (_gate)
                return _items.Count;
        }
    }

    public IEnumerable<string> Keys
    {
        get
        {
            lock (_gate)
                return _items.Keys.ToList();
        }
    }

    public void Save(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        Set(task.Id, task);
    }

    public Task Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var task))
                throw new KeyNotFoundException($"Task '{id}' was not found.");

            return task;
        }
    }

    public void Set(string id, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (value is not Task task)
            throw new ArgumentException("Only Task instances can be stored in the task collection.", nameof(value));

        if (!string.Equals(id, task.Id, StringComparison.Ordinal))
            throw new ArgumentException("Task id does not match the provided key.", nameof(id));

        lock (_gate)
            _items[id] = task;
    }

    public void Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            if (!_items.Remove(id))
                throw new KeyNotFoundException($"Task '{id}' was not found.");
        }
    }

    public bool Has(object key)
    {
        lock (_gate)
        {
            if (key is Task task)
                return _items.ContainsKey(task.Id);

            if (key is string id)
                return _items.ContainsKey(id);

            return false;
        }
    }

    public void Cancel(string id)
    {
        Get(id).Cancel();
    }

    public object this[string id]
    {
        get => Get(id);
        set => Set(id, value);
    }
}

public sealed class InMemoryGraphCollection : IGraphCollection
{
    private readonly object _gate = new();
    private readonly Dictionary<string, TaskGraph> _items = new(StringComparer.Ordinal);
    private readonly ITaskCollection _tasks;

    public InMemoryGraphCollection(ITaskCollection tasks)
    {
        _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _items.Count;
        }
    }

    public IEnumerable<string> Keys
    {
        get
        {
            lock (_gate)
                return _items.Keys.ToList();
        }
    }

    public void Save(TaskGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);

        foreach (var task in graph.Members)
            _tasks.Save(task);

        Set(graph.Id, graph);
    }

    public TaskGraph Get(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var graph))
                throw new KeyNotFoundException($"Graph '{id}' was not found.");

            return graph;
        }
    }

    public void Set(string id, object value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (value is not TaskGraph graph)
            throw new ArgumentException("Only TaskGraph instances can be stored in the graph collection.", nameof(value));

        if (!string.Equals(id, graph.Id, StringComparison.Ordinal))
            throw new ArgumentException("Graph id does not match the provided key.", nameof(id));

        lock (_gate)
            _items[id] = graph;
    }

    public void Delete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            if (!_items.Remove(id))
                throw new KeyNotFoundException($"Graph '{id}' was not found.");
        }
    }

    public bool Has(object key)
    {
        lock (_gate)
        {
            if (key is TaskGraph graph)
                return _items.ContainsKey(graph.Id);

            if (key is string id)
                return _items.ContainsKey(id);

            return false;
        }
    }

    public object this[string id]
    {
        get => Get(id);
        set => Set(id, value);
    }
}
