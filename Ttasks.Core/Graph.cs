using System.Collections.ObjectModel;

namespace Ttasks.Core;

public sealed class TaskGraph
{
    private readonly Dictionary<string, GraphNode> _nodes = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _errors = new(StringComparer.Ordinal);
    private bool _hasRun;

    public TaskGraph(string? title = null, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        Id = Guid.NewGuid().ToString("N");
        Title = title ?? string.Empty;
        _metadata = new Dictionary<string, object?>(MetadataValues.Normalize(metadata), StringComparer.Ordinal);
        CreatedAt = DateTimeOffset.UtcNow;
    }

    private TaskGraph(string id, string? title, DateTimeOffset createdAt, IReadOnlyDictionary<string, object?>? metadata)
    {
        Id = id;
        Title = title ?? string.Empty;
        _metadata = new Dictionary<string, object?>(MetadataValues.Normalize(metadata), StringComparer.Ordinal);
        CreatedAt = createdAt;
    }

    public string Id { get; }
    public string Title { get; set; }
    public DateTimeOffset CreatedAt { get; }
    private readonly Dictionary<string, object?> _metadata;
    public IReadOnlyDictionary<string, object?> Metadata => new ReadOnlyDictionary<string, object?>(_metadata);
    public IReadOnlyList<Task> RequiredTasks => _nodes.Values.Where(n => n.Required).Select(n => n.Task).ToList();
    public IReadOnlyList<Task> Members => _nodes.Values.Select(n => n.Task).Distinct().ToList();
    public IReadOnlyList<Task> FinallyTasks => _nodes.Values.Where(n => n.Finally).Select(n => n.Task).ToList();
    public IReadOnlyList<Task> OptionalTasks => _nodes.Values.Where(n => n.Finally && !n.Required).Select(n => n.Task).ToList();
    public IReadOnlyList<Task> Succeeded => _nodes.Values.Where(n => n.Task.Status == TaskStatus.Succeeded).Select(n => n.Task).ToList();
    public IReadOnlyList<Task> Failed => _nodes.Values.Where(n => n.Task.Status == TaskStatus.Failed).Select(n => n.Task).ToList();
    public IReadOnlyList<Task> Cancelled => _nodes.Values.Where(n => n.Task.Status == TaskStatus.Cancelled).Select(n => n.Task).ToList();
    public IReadOnlyList<Task> Blocked => _nodes.Values.Where(n => n.Task.Status == TaskStatus.Blocked).Select(n => n.Task).ToList();
    public IReadOnlyList<Task> OptionalFailed => _nodes.Values.Where(n => n.Finally && !n.Required && n.Task.Status == TaskStatus.Failed).Select(n => n.Task).ToList();
    public IReadOnlyList<Task> RequiredFailed => _nodes.Values.Where(n => n.Required && n.Task.Status == TaskStatus.Failed).Select(n => n.Task).ToList();
    public IReadOnlyList<Task> RequiredBlocked => _nodes.Values.Where(n => n.Required && n.Task.Status == TaskStatus.Blocked).Select(n => n.Task).ToList();
    public IReadOnlyDictionary<string, string> Errors => new Dictionary<string, string>(_errors, StringComparer.Ordinal);
    public bool Ok => _hasRun && (RequiredTasks.Count == 0 || (RequiredTasks.All(t => t.Status == TaskStatus.Succeeded) && !RequiredFailed.Any() && !RequiredBlocked.Any() && !_errors.Keys.Any(id => RequiredTasks.Any(t => t.Id == id))));

    public void SetMetadata(string key, object? value) =>
        _metadata[MetadataValues.ValidateKey(key)] = MetadataValues.NormalizeValue(value);

    public bool RemoveMetadata(string key) => _metadata.Remove(MetadataValues.ValidateKey(key));

    public void ClearMetadata() => _metadata.Clear();

    public void Add(Task task, IEnumerable<Task>? after = null, bool finally_ = false, bool required = true)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!bool.TryParse(finally_.ToString(), out _)) { }
        if (!bool.TryParse(required.ToString(), out _)) { }
        if (!finally_ && !required)
            throw new ArgumentException("required=false is only valid for finally tasks.");

        var dependencies = (after ?? Array.Empty<Task>()).ToList();
        if (dependencies.Any(dependency => dependency is null))
            throw new ArgumentException("Dependencies must be tasks.", nameof(after));

        dependencies = dependencies.Distinct(new TaskComparer()).ToList();
        _nodes[task.Id] = new GraphNode(task, dependencies, finally_, required);
    }

    public IReadOnlyList<Task> Dependencies(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (!_nodes.TryGetValue(task.Id, out var node))
            throw new KeyNotFoundException("Task is not registered in the graph.");

        return node.Dependencies.ToList();
    }

    public bool IsFinally(Task task) => _nodes.TryGetValue(task.Id, out var node) && node.Finally;
    public bool IsOptional(Task task) => IsFinally(task) && !_nodes[task.Id].Required;

    public IReadOnlyList<Task> Roots() => _nodes.Values.Where(n => n.Dependencies.Count == 0).Select(n => n.Task).ToList();
    public IReadOnlyList<Task> Leaves() => _nodes.Values.Where(n => !_nodes.Values.Any(other => other.Dependencies.Contains(n.Task))).Select(n => n.Task).ToList();

    public TaskGraph Run(TaskExecutor executor, int maxWorkers = 1)
    {
        ArgumentNullException.ThrowIfNull(executor);
        if (maxWorkers < 1)
            throw new ArgumentOutOfRangeException(nameof(maxWorkers));

        ValidateDependencies();
        ValidateCycles();

        _errors.Clear();

        if (_nodes.Values.Any(node => node.Task.Status == TaskStatus.Running))
            throw new InvalidOperationException("Graph contains tasks that are already running.");

        executor.PersistGraph(this);
        _hasRun = true;

        try
        {
            var initiallyRetryable = _nodes.Values
                .Where(node => node.Task.Status is TaskStatus.Failed or TaskStatus.Blocked)
                .Select(node => node.Task.Id)
                .ToHashSet(StringComparer.Ordinal);
            var attemptedThisRun = new HashSet<string>(StringComparer.Ordinal);
            var blockedThisRun = new HashSet<string>(StringComparer.Ordinal);

            while (true)
            {
                var progress = PropagateBlocks(initiallyRetryable, attemptedThisRun, blockedThisRun, executor);
                var ready = TopologicalSort()
                    .Where(task => IsExecutableThisRun(_nodes[task.Id], initiallyRetryable, attemptedThisRun, blockedThisRun))
                    .Where(task => IsReady(_nodes[task.Id], initiallyRetryable, attemptedThisRun, blockedThisRun))
                    .ToList();

                foreach (var batch in ready.Chunk(maxWorkers))
                {
                    var executions = batch
                        .Select(task =>
                        {
                            attemptedThisRun.Add(task.Id);
                            var node = _nodes[task.Id];
                            var upstream = node.Dependencies.ToDictionary(dep => dep.Id, dep => dep, StringComparer.Ordinal);
                            return System.Threading.Tasks.Task.Run(() => executor.Execute(task, upstream: upstream, orderedUpstream: node.Dependencies));
                        })
                        .ToArray();

                    foreach (var execution in executions)
                    {
                        try
                        {
                            execution.GetAwaiter().GetResult();
                        }
                        catch (Exception ex)
                        {
                            var failedTask = batch[Array.IndexOf(executions, execution)];
                            _errors[failedTask.Id] = ex.Message;
                        }
                    }

                    progress = true;
                }

                if (AllTasksSettled(initiallyRetryable, attemptedThisRun, blockedThisRun))
                    return this;

                if (!progress)
                {
                    var stuck = _nodes.Values
                        .Where(node => !IsSettledForRun(node, initiallyRetryable, attemptedThisRun, blockedThisRun))
                        .Select(node => node.Task.Id);
                    throw new InvalidOperationException($"Graph scheduling made no progress. Stuck tasks: {string.Join(", ", stuck)}.");
                }
            }
        }
        finally
        {
            executor.PersistGraph(this);
        }
    }

    private void ValidateDependencies()
    {
        foreach (var node in _nodes.Values)
        {
            foreach (var dependency in node.Dependencies)
            {
                if (!_nodes.ContainsKey(dependency.Id))
                    throw new InvalidOperationException("Graph contains an unregistered dependency.");
            }
        }
    }

    private void ValidateCycles()
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var stack = new HashSet<string>(StringComparer.Ordinal);

        void Visit(string taskId)
        {
            if (!visited.Add(taskId))
                return;

            stack.Add(taskId);
            foreach (var dependency in _nodes[taskId].Dependencies)
            {
                if (stack.Contains(dependency.Id))
                    throw new InvalidOperationException("Graph contains a cycle.");
                Visit(dependency.Id);
            }
            stack.Remove(taskId);
        }

        foreach (var taskId in _nodes.Keys.ToList())
            Visit(taskId);
    }

    private IReadOnlyList<Task> TopologicalSort()
    {
        var indegree = _nodes.Values.ToDictionary(n => n.Task.Id, n => n.Dependencies.Count, StringComparer.Ordinal);
        var ready = new Queue<Task>(_nodes.Values.Where(n => indegree[n.Task.Id] == 0).Select(n => n.Task));
        var result = new List<Task>();

        while (ready.Count > 0)
        {
            var current = ready.Dequeue();
            result.Add(current);
            foreach (var dependent in _nodes.Values.Where(n => n.Dependencies.Contains(current)).Select(n => n.Task))
            {
                indegree[dependent.Id]--;
                if (indegree[dependent.Id] == 0)
                    ready.Enqueue(dependent);
            }
        }

        return result;
    }

    private sealed record GraphNode(Task Task, IReadOnlyList<Task> Dependencies, bool Finally, bool Required);

    private bool PropagateBlocks(
        HashSet<string> initiallyRetryable,
        HashSet<string> attemptedThisRun,
        HashSet<string> blockedThisRun,
        TaskExecutor executor)
    {
        var progress = false;
        foreach (var task in TopologicalSort())
        {
            var node = _nodes[task.Id];
            if (node.Finally || !IsExecutableThisRun(node, initiallyRetryable, attemptedThisRun, blockedThisRun))
                continue;

            var badParent = node.Dependencies.FirstOrDefault(parent => IsNonRecoverableBadParent(parent, initiallyRetryable, attemptedThisRun, blockedThisRun));
            if (badParent is null)
                continue;

            if (task.Status != TaskStatus.Blocked)
                executor.MarkBlocked(task, badParent.Id);

            blockedThisRun.Add(task.Id);
            progress = true;
        }

        return progress;
    }

    private bool IsReady(
        GraphNode node,
        HashSet<string> initiallyRetryable,
        HashSet<string> attemptedThisRun,
        HashSet<string> blockedThisRun)
    {
        if (node.Finally)
            return node.Dependencies.All(parent => IsSettledForFinally(parent, initiallyRetryable, attemptedThisRun, blockedThisRun));

        return node.Dependencies.All(parent => parent.Status == TaskStatus.Succeeded);
    }

    private bool IsExecutableThisRun(
        GraphNode node,
        HashSet<string> initiallyRetryable,
        HashSet<string> attemptedThisRun,
        HashSet<string> blockedThisRun)
    {
        if (attemptedThisRun.Contains(node.Task.Id) || blockedThisRun.Contains(node.Task.Id))
            return false;

        return node.Task.Status == TaskStatus.Pending
            || (node.Task.Status is TaskStatus.Failed or TaskStatus.Blocked && initiallyRetryable.Contains(node.Task.Id));
    }

    private bool IsNonRecoverableBadParent(
        Task parent,
        HashSet<string> initiallyRetryable,
        HashSet<string> attemptedThisRun,
        HashSet<string> blockedThisRun)
    {
        if (parent.Status == TaskStatus.Cancelled)
            return true;

        if (parent.Status == TaskStatus.Failed)
            return !initiallyRetryable.Contains(parent.Id) || attemptedThisRun.Contains(parent.Id);

        if (parent.Status == TaskStatus.Blocked)
            return blockedThisRun.Contains(parent.Id) || !initiallyRetryable.Contains(parent.Id) || attemptedThisRun.Contains(parent.Id);

        return false;
    }

    private bool IsSettledForFinally(
        Task parent,
        HashSet<string> initiallyRetryable,
        HashSet<string> attemptedThisRun,
        HashSet<string> blockedThisRun)
    {
        if (parent.Status is TaskStatus.Pending or TaskStatus.Running)
            return false;

        if (parent.Status is TaskStatus.Failed or TaskStatus.Blocked)
            return IsNonRecoverableBadParent(parent, initiallyRetryable, attemptedThisRun, blockedThisRun) || parent.Status == TaskStatus.Succeeded;

        return true;
    }

    private bool AllTasksSettled(
        HashSet<string> initiallyRetryable,
        HashSet<string> attemptedThisRun,
        HashSet<string> blockedThisRun) =>
        _nodes.Values.All(node => IsSettledForRun(node, initiallyRetryable, attemptedThisRun, blockedThisRun));

    private bool IsSettledForRun(
        GraphNode node,
        HashSet<string> initiallyRetryable,
        HashSet<string> attemptedThisRun,
        HashSet<string> blockedThisRun) =>
        node.Task.Status switch
        {
            TaskStatus.Succeeded or TaskStatus.Cancelled => true,
            TaskStatus.Failed => !initiallyRetryable.Contains(node.Task.Id) || attemptedThisRun.Contains(node.Task.Id),
            TaskStatus.Blocked => blockedThisRun.Contains(node.Task.Id) || !initiallyRetryable.Contains(node.Task.Id) || attemptedThisRun.Contains(node.Task.Id),
            _ => false
        };

    private sealed class TaskComparer : IEqualityComparer<Task>
    {
        public bool Equals(Task? x, Task? y) => x?.Id == y?.Id;
        public int GetHashCode(Task obj) => obj.Id.GetHashCode(StringComparison.Ordinal);
    }

    internal IReadOnlyList<TaskGraphNodeSnapshot> SnapshotNodes() =>
        _nodes.Values
            .Select(node => new TaskGraphNodeSnapshot(node.Task, node.Dependencies.ToList(), node.Finally, node.Required))
            .ToList();

    internal static TaskGraph Restore(string id, string? title, DateTimeOffset createdAt, IEnumerable<TaskGraphNodeSnapshot> nodes, IReadOnlyDictionary<string, object?>? metadata = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(nodes);

        var graph = new TaskGraph(id, title, createdAt, metadata);
        foreach (var node in nodes)
            graph._nodes[node.Task.Id] = new GraphNode(node.Task, node.Dependencies.ToList(), node.Finally, node.Required);
        return graph;
    }
}

internal sealed record TaskGraphNodeSnapshot(Task Task, IReadOnlyList<Task> Dependencies, bool Finally, bool Required);
