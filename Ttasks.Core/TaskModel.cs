namespace Ttasks.Core;

public enum TaskStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    Blocked
}

public enum TaskType
{
    Bash,
    Powershell,
    Prompt,
    Agent
}

public sealed class TaskResult
{
    public string TaskId { get; init; } = string.Empty;
    public TaskStatus Status { get; init; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; init; }
    public double DurationSeconds => (FinishedAt - StartedAt).TotalSeconds;
    public string Output { get; init; } = string.Empty;
    public string? Error { get; init; }
    public int? ReturnCode { get; init; }
    public object? Raw { get; init; }
    public string? TerminationReason { get; init; }
}

public sealed class Task
{
    private static readonly IReadOnlyDictionary<(TaskStatus From, TaskStatus To), bool> AllowedTransitions =
        new Dictionary<(TaskStatus From, TaskStatus To), bool>
        {
            [(TaskStatus.Pending, TaskStatus.Running)] = true,
            [(TaskStatus.Pending, TaskStatus.Failed)] = true,
            [(TaskStatus.Pending, TaskStatus.Cancelled)] = true,
            [(TaskStatus.Pending, TaskStatus.Blocked)] = true,
            [(TaskStatus.Running, TaskStatus.Succeeded)] = true,
            [(TaskStatus.Running, TaskStatus.Failed)] = true,
            [(TaskStatus.Running, TaskStatus.Cancelled)] = true,
            [(TaskStatus.Running, TaskStatus.Blocked)] = true,
            [(TaskStatus.Failed, TaskStatus.Running)] = true,
            [(TaskStatus.Failed, TaskStatus.Cancelled)] = true,
            [(TaskStatus.Blocked, TaskStatus.Running)] = true,
            [(TaskStatus.Blocked, TaskStatus.Cancelled)] = true,
        };

    public Task(TaskType type, string payload, string? title = null, string? description = null, int? timeout = null)
        : this(Guid.NewGuid().ToString("N"), type, payload, title, description, timeout)
    {
    }

    internal Task(string id, TaskType type, string payload, string? title = null, string? description = null, int? timeout = null)
    {
        if (!Enum.IsDefined(typeof(TaskType), type))
            throw new ArgumentOutOfRangeException(nameof(type), "Task type must be one of the built-in task types.");

        if (timeout.HasValue && timeout.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive when supplied.");

        Id = id;
        Type = type;
        _payload = payload ?? string.Empty;
        _title = title ?? string.Empty;
        _description = description ?? string.Empty;
        if (timeout.HasValue && timeout.Value <= 0)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive when supplied.");
        _timeout = timeout;
        Status = TaskStatus.Pending;
        CreatedAt = DateTimeOffset.UtcNow;
    }

    private string _payload;
    private string _title;
    private string _description;
    private int? _timeout;

    public string Id { get; }
    public TaskType Type { get; }
    public string TypeName => Type switch
    {
        TaskType.Bash => "bash",
        TaskType.Powershell => "powershell",
        TaskType.Prompt => "prompt",
        TaskType.Agent => "agent",
        _ => Type.ToString().ToLowerInvariant()
    };
    public string Payload
    {
        get => _payload;
        set
        {
            ThrowIfSucceeded();
            _payload = value ?? string.Empty;
        }
    }
    public string Title
    {
        get => _title;
        set
        {
            ThrowIfSucceeded();
            _title = value ?? string.Empty;
        }
    }
    public string Description
    {
        get => _description;
        set
        {
            ThrowIfSucceeded();
            _description = value ?? string.Empty;
        }
    }
    public int? Timeout
    {
        get => _timeout;
        set
        {
            ThrowIfSucceeded();
            if (value.HasValue && value.Value <= 0)
                throw new ArgumentOutOfRangeException(nameof(value), "Timeout must be positive when supplied.");
            _timeout = value;
        }
    }
    public TaskStatus Status { get; private set; }
    public string? Error { get; private set; }
    public TaskResult? Result { get; private set; }
    public string? BlockedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; }

    public bool IsPending => Status == TaskStatus.Pending;
    public bool IsRunning => Status == TaskStatus.Running;
    public bool IsSucceeded => Status == TaskStatus.Succeeded;
    public bool IsFailed => Status == TaskStatus.Failed;
    public bool IsCancelled => Status == TaskStatus.Cancelled;
    public bool IsBlocked => Status == TaskStatus.Blocked;
    public bool IsActive => Status is TaskStatus.Pending or TaskStatus.Running;
    public bool IsTerminal => Status is TaskStatus.Succeeded or TaskStatus.Cancelled;
    public bool IsSink => IsTerminal;
    public bool IsBad => Status is TaskStatus.Failed or TaskStatus.Cancelled or TaskStatus.Blocked;

    public bool CanTransitionTo(TaskStatus nextStatus) => AllowedTransitions.ContainsKey((Status, nextStatus));

    public void TransitionTo(TaskStatus nextStatus, string? error = null, string? blockedBy = null)
    {
        if (!CanTransitionTo(nextStatus))
            throw new InvalidOperationException($"Task cannot transition from {Status} to {nextStatus}.");

        if (nextStatus == TaskStatus.Running)
        {
            Result = null;
            BlockedBy = null;
            Error = null;
        }

        if (nextStatus == TaskStatus.Succeeded)
        {
            Error = null;
        }

        if (nextStatus == TaskStatus.Failed)
        {
            Error = error;
        }

        if (nextStatus == TaskStatus.Blocked)
        {
            BlockedBy = blockedBy;
        }

        Status = nextStatus;
    }

    public static Task Bash(string payload, string? title = null, string? description = null, int? timeout = null) =>
        new(TaskType.Bash, payload, title, description, timeout);

    public static Task Powershell(string payload, string? title = null, string? description = null, int? timeout = null) =>
        new(TaskType.Powershell, payload, title, description, timeout);

    public static Task Prompt(string payload, string? title = null, string? description = null, int? timeout = null) =>
        new(TaskType.Prompt, payload, title, description, timeout);

    public static Task Agent(string payload, string? title = null, string? description = null, int? timeout = null) =>
        new(TaskType.Agent, payload, title, description, timeout);

    public void Cancel()
    {
        if (IsTerminal)
            return;

        TransitionTo(TaskStatus.Cancelled, Error);
    }

    private void ThrowIfSucceeded()
    {
        if (Status == TaskStatus.Succeeded)
            throw new InvalidOperationException("Cannot modify a succeeded task.");
    }

    internal void AttachResult(TaskResult result) => Result = result;

    internal void AttachBlockedBy(string? blockedBy) => BlockedBy = blockedBy;

    public override bool Equals(object? obj) => obj is Task other && Id == other.Id;

    public override int GetHashCode() => Id.GetHashCode(StringComparison.Ordinal);

    public override string ToString() => $"Task(Id={Id}, Title={Title}, Status={Status})";
}
