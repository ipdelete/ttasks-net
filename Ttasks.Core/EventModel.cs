namespace Ttasks.Core;

public enum TaskEventType
{
    Started,
    Progress,
    Output,
    Succeeded,
    Failed,
    Cancelled,
    Blocked,
    PersistenceFailed
}

public sealed class TaskEvent
{
    public TaskEvent(TaskEventType type, Task task, TaskStatus status, TaskStatus? previousStatus = null, string? error = null,
        double? progressPercent = null, string? progressMessage = null, string? outputStream = null, string? outputChunk = null)
    {
        if (!Enum.IsDefined(typeof(TaskEventType), type))
            throw new ArgumentOutOfRangeException(nameof(type));

        ArgumentNullException.ThrowIfNull(task);

        if (type is (TaskEventType.Progress or TaskEventType.Output or TaskEventType.PersistenceFailed) && previousStatus is not null)
            throw new ArgumentException("Non-status events must not include a previous status.", nameof(previousStatus));

        if (type == TaskEventType.Output)
        {
            if (outputStream is not ("stdout" or "stderr"))
                throw new ArgumentException("Output stream must be either 'stdout' or 'stderr'.", nameof(outputStream));
            if (outputChunk is null)
                throw new ArgumentNullException(nameof(outputChunk));
        }

        if (type == TaskEventType.Progress)
        {
            if (progressMessage is not null && string.IsNullOrWhiteSpace(progressMessage))
                throw new ArgumentException("Progress message must be non-empty.", nameof(progressMessage));
            if (progressPercent is null && string.IsNullOrWhiteSpace(progressMessage))
                throw new InvalidOperationException("Progress events require a percent or message.");
            if (progressPercent is not null && (double.IsNaN(progressPercent.Value) || double.IsInfinity(progressPercent.Value) || progressPercent.Value < 0 || progressPercent.Value > 100))
                throw new ArgumentOutOfRangeException(nameof(progressPercent), "Progress percent must be finite and between 0 and 100.");
        }

        Type = type;
        Task = task;
        TaskId = task.Id;
        Status = status;
        PreviousStatus = previousStatus;
        Error = error;
        ProgressPercent = progressPercent;
        ProgressMessage = progressMessage;
        OutputStream = outputStream;
        OutputChunk = outputChunk;
        Timestamp = DateTimeOffset.UtcNow;
    }

    public TaskEventType Type { get; }
    public string TaskId { get; }
    public Task Task { get; }
    public DateTimeOffset Timestamp { get; }
    public TaskStatus? PreviousStatus { get; }
    public TaskStatus Status { get; }
    public string? Error { get; }
    public double? ProgressPercent { get; }
    public string? ProgressMessage { get; }
    public string? OutputStream { get; }
    public string? OutputChunk { get; }
}
