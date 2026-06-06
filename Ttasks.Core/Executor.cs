using System.Diagnostics;
using System.Text;

namespace Ttasks.Core;

public sealed class RetryPolicy
{
    public RetryPolicy(int maxAttempts, double backoffSeconds = 0)
    {
        if (maxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(maxAttempts), "MaxAttempts must be at least 1.");
        if (double.IsNaN(backoffSeconds) || double.IsInfinity(backoffSeconds) || backoffSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(backoffSeconds), "BackoffSeconds must be a finite, non-negative number.");

        MaxAttempts = maxAttempts;
        BackoffSeconds = backoffSeconds;
    }

    public int MaxAttempts { get; }
    public double BackoffSeconds { get; }
}

public sealed record PersistenceError(string Id, Exception Error);

public interface ITaskFailureDetails
{
    string Output { get; }
    string? ErrorOutput { get; }
    int? ReturnCode { get; }
    object? Raw { get; }
    string TerminationReason { get; }
}

public class TaskExecutionException : InvalidOperationException, ITaskFailureDetails
{
    public TaskExecutionException(string message, string terminationReason, string output = "", string? errorOutput = null, int? returnCode = null, object? raw = null)
        : base(message)
    {
        if (string.IsNullOrWhiteSpace(terminationReason))
            throw new ArgumentException("Termination reason must be non-empty.", nameof(terminationReason));

        TerminationReason = terminationReason;
        Output = output;
        ErrorOutput = errorOutput;
        ReturnCode = returnCode;
        Raw = raw;
    }

    public string Output { get; }
    public string? ErrorOutput { get; }
    public int? ReturnCode { get; }
    public object? Raw { get; }
    public string TerminationReason { get; }
}

public sealed class TaskTimeoutException : TaskExecutionException
{
    public TaskTimeoutException(string message, string output = "", string? errorOutput = null, int? returnCode = null, object? raw = null)
        : base(message, "timeout", output, errorOutput, returnCode, raw)
    {
    }
}

internal sealed class TaskCancelledException : OperationCanceledException, ITaskFailureDetails
{
    public TaskCancelledException(string message, string output = "", string? errorOutput = null, int? returnCode = null, object? raw = null)
        : base(message)
    {
        Output = output;
        ErrorOutput = errorOutput;
        ReturnCode = returnCode;
        Raw = raw;
    }

    public string Output { get; }
    public string? ErrorOutput { get; }
    public int? ReturnCode { get; }
    public object? Raw { get; }
    public string TerminationReason => "cancelled";
}

public sealed class TaskExecutor : IDisposable
{
    private readonly object _gate = new();
    private readonly EventBus _events = new();
    private readonly Dictionary<TaskType, Func<TaskContext, object?>> _handlers = new();
    private readonly Dictionary<string, CancellationTokenSource> _runningCancellation = new(StringComparer.Ordinal);
    private readonly List<PersistenceError> _persistenceErrors = [];
    private readonly List<PersistenceError> _graphPersistenceErrors = [];
    private readonly List<System.Threading.Tasks.Task> _submitted = [];
    private bool _isShutdown;

    public TaskExecutor(ITaskStore? store = null)
    {
        Store = store;
    }

    public static TaskExecutor WithBuiltInHandlers(ITaskStore? store = null)
    {
        var executor = new TaskExecutor(store);
        executor.Register(TaskType.Bash, RunBash);
        executor.Register(TaskType.Powershell, RunPowerShell);
        executor.Register(TaskType.Process, RunProcessTask);
        return executor;
    }

    public static bool IsPowerShellAvailable() =>
        FindExecutable("pwsh") is not null || FindExecutable("powershell") is not null;

    public EventBus Events => _events;
    public ITaskStore? Store { get; }
    public bool IsShutdown
    {
        get
        {
            lock (_gate)
                return _isShutdown;
        }
    }

    public IReadOnlyList<PersistenceError> PersistenceErrors
    {
        get
        {
            lock (_gate)
                return _persistenceErrors.ToList().AsReadOnly();
        }
    }

    public IReadOnlyList<PersistenceError> GraphPersistenceErrors
    {
        get
        {
            lock (_gate)
                return _graphPersistenceErrors.ToList().AsReadOnly();
        }
    }

    public bool IsRegistered(TaskType type)
    {
        if (!Enum.IsDefined(typeof(TaskType), type))
            throw new ArgumentOutOfRangeException(nameof(type));

        lock (_gate)
            return _handlers.ContainsKey(type);
    }

    public void Register(TaskType type, Func<TaskContext, object?> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!Enum.IsDefined(typeof(TaskType), type))
            throw new ArgumentOutOfRangeException(nameof(type));

        lock (_gate)
            _handlers[type] = handler;
    }

    public TaskResult Execute(Task task, RetryPolicy? retryPolicy = null, IReadOnlyDictionary<string, Task>? upstream = null, IReadOnlyList<Task>? orderedUpstream = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        retryPolicy ??= new RetryPolicy(1, 0);
        ValidateRetryPolicy(retryPolicy);
        var upstreamSnapshot = SnapshotUpstream(upstream, orderedUpstream);
        var orderedUpstreamSnapshot = SnapshotOrderedUpstream(upstreamSnapshot, orderedUpstream);

        if (task.Status == TaskStatus.Cancelled)
            throw new OperationCanceledException("Task is already cancelled.");

        for (var attempt = 1; attempt <= retryPolicy.MaxAttempts; attempt++)
        {
            var handler = GetHandlerOrNull(task.Type);
            if (handler is null)
            {
                var previousStatus = task.Status;
                var result = new TaskResult
                {
                    TaskId = task.Id,
                    Status = TaskStatus.Failed,
                    Error = "No handler registered for task type.",
                    StartedAt = DateTimeOffset.UtcNow,
                    FinishedAt = DateTimeOffset.UtcNow,
                    TerminationReason = "handler"
                };
                task.AttachResult(result);
                task.TransitionTo(TaskStatus.Failed, result.Error);
                PersistTask(task);
                _events.Publish(new TaskEvent(TaskEventType.Failed, task, TaskStatus.Failed, previousStatus: previousStatus));
                throw new InvalidOperationException(result.Error);
            }

            if (!task.CanTransitionTo(TaskStatus.Running))
                throw new InvalidOperationException($"Task cannot execute from status {task.Status}.");

            var previous = task.Status;
            var cts = new CancellationTokenSource();
            task.TransitionTo(TaskStatus.Running);
            RegisterRunningCancellation(task, cts);
            PersistTask(task);
            _events.Publish(new TaskEvent(TaskEventType.Started, task, TaskStatus.Running, previousStatus: previous));

            try
            {
                var startedAt = DateTimeOffset.UtcNow;
                var context = new TaskContext(task, this, upstreamSnapshot, cts.Token, orderedUpstreamSnapshot);
                var raw = handler(context);
                cts.Token.ThrowIfCancellationRequested();
                var normalized = NormalizeResult(raw);
                var result = new TaskResult
                {
                    TaskId = task.Id,
                    Status = TaskStatus.Succeeded,
                    StartedAt = startedAt,
                    FinishedAt = DateTimeOffset.UtcNow,
                    Output = normalized.Output,
                    Error = normalized.Error,
                    ReturnCode = normalized.ReturnCode,
                    Raw = normalized.Raw,
                    TerminationReason = null
                };
                task.AttachResult(result);
                task.TransitionTo(TaskStatus.Succeeded);
                PersistTask(task);
                _events.Publish(new TaskEvent(TaskEventType.Succeeded, task, TaskStatus.Succeeded, previousStatus: TaskStatus.Running));
                return result;
            }
            catch (TaskExecutionException ex)
            {
                AttachFailedResult(task, ex.Message, ex);

                if (attempt < retryPolicy.MaxAttempts)
                {
                    SleepBackoff(task, retryPolicy.BackoffSeconds);
                    if (task.Status == TaskStatus.Cancelled)
                        throw new OperationCanceledException("Task was cancelled during retry backoff.");
                    continue;
                }

                throw;
            }
            catch (OperationCanceledException ex)
            {
                if (task.Status != TaskStatus.Cancelled)
                {
                    var details = ex as ITaskFailureDetails;
                    var result = new TaskResult
                    {
                        TaskId = task.Id,
                        Status = TaskStatus.Cancelled,
                        Output = details?.Output ?? string.Empty,
                        Error = details?.ErrorOutput ?? "cancelled",
                        ReturnCode = details?.ReturnCode,
                        Raw = details?.Raw,
                        StartedAt = DateTimeOffset.UtcNow,
                        FinishedAt = DateTimeOffset.UtcNow,
                        TerminationReason = "cancelled"
                    };
                    task.AttachResult(result);
                    task.TransitionTo(TaskStatus.Cancelled, "cancelled");
                    PersistTask(task);
                    _events.Publish(new TaskEvent(TaskEventType.Cancelled, task, TaskStatus.Cancelled, previousStatus: TaskStatus.Running));
                }

                throw;
            }
            catch (Exception ex)
            {
                var result = new TaskResult
                {
                    TaskId = task.Id,
                    Status = TaskStatus.Failed,
                    Error = ex.Message,
                    StartedAt = DateTimeOffset.UtcNow,
                    FinishedAt = DateTimeOffset.UtcNow,
                    TerminationReason = "handler"
                };
                task.AttachResult(result);
                task.TransitionTo(TaskStatus.Failed, ex.Message);
                PersistTask(task);
                _events.Publish(new TaskEvent(TaskEventType.Failed, task, TaskStatus.Failed, previousStatus: TaskStatus.Running));

                if (attempt < retryPolicy.MaxAttempts)
                {
                    SleepBackoff(task, retryPolicy.BackoffSeconds);
                    if (task.Status == TaskStatus.Cancelled)
                        throw new OperationCanceledException("Task was cancelled during retry backoff.");
                    continue;
                }

                throw;
            }
            finally
            {
                UnregisterRunningCancellation(task, cts);
            }
        }

        throw new InvalidOperationException("Retry loop exhausted.");
    }

    public SubmittedTask Submit(Task task, RetryPolicy? retryPolicy = null, IReadOnlyDictionary<string, Task>? upstream = null, IReadOnlyList<Task>? orderedUpstream = null)
    {
        ArgumentNullException.ThrowIfNull(task);
        retryPolicy ??= new RetryPolicy(1, 0);
        ValidateRetryPolicy(retryPolicy);

        lock (_gate)
        {
            if (_isShutdown)
                throw new InvalidOperationException("Executor is shut down.");
        }

        var upstreamSnapshot = SnapshotUpstream(upstream, orderedUpstream);
        var submitted = new SubmittedTask(this, task, retryPolicy, upstreamSnapshot, SnapshotOrderedUpstream(upstreamSnapshot, orderedUpstream));
        lock (_gate)
            _submitted.Add(submitted.InnerTask);
        submitted.InnerTask.ContinueWith(
            completed =>
            {
                lock (_gate)
                    _submitted.Remove(completed);
            },
            System.Threading.Tasks.TaskScheduler.Default);
        return submitted;
    }

    public void Cancel(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        if (task.Status is TaskStatus.Succeeded or TaskStatus.Cancelled)
            return;

        CancellationTokenSource? runningCancellation;
        lock (_gate)
            _runningCancellation.TryGetValue(task.Id, out runningCancellation);

        if (task.Status == TaskStatus.Running && runningCancellation is not null)
        {
            runningCancellation.Cancel();
            return;
        }

        var previousStatus = task.Status;
        var now = DateTimeOffset.UtcNow;
        var result = new TaskResult
        {
            TaskId = task.Id,
            Status = TaskStatus.Cancelled,
            Error = "cancelled",
            StartedAt = now,
            FinishedAt = now,
            TerminationReason = "cancelled"
        };
        task.AttachResult(result);
        task.TransitionTo(TaskStatus.Cancelled, "cancelled");
        PersistTask(task);
        _events.Publish(new TaskEvent(TaskEventType.Cancelled, task, TaskStatus.Cancelled, previousStatus: previousStatus));
    }

    public void MarkBlocked(Task task, string parentId)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentException.ThrowIfNullOrWhiteSpace(parentId);

        var previousStatus = task.Status;
        task.TransitionTo(TaskStatus.Blocked, blockedBy: parentId);
        PersistTask(task);
        _events.Publish(new TaskEvent(TaskEventType.Blocked, task, TaskStatus.Blocked, previousStatus: previousStatus));
    }

    public bool IsRunning(Task task)
    {
        ArgumentNullException.ThrowIfNull(task);
        lock (_gate)
            return _runningCancellation.ContainsKey(task.Id);
    }

    public void Shutdown()
    {
        System.Threading.Tasks.Task[] pending;
        lock (_gate)
        {
            _isShutdown = true;
            var currentId = System.Threading.Tasks.Task.CurrentId;
            pending = _submitted
                .Where(task => !task.IsCompleted && (!currentId.HasValue || task.Id != currentId.Value))
                .ToArray();
        }

        try
        {
            System.Threading.Tasks.Task.WaitAll(pending);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is OperationCanceledException))
        {
            throw new OperationCanceledException("Submitted work was cancelled.", ex);
        }
    }

    public void Close() => Shutdown();

    public void Dispose() => Shutdown();

    public void PersistGraph(TaskGraph graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        if (Store is null)
            return;

        try
        {
            Store.Graphs.Save(graph);
        }
        catch (Exception ex)
        {
            lock (_gate)
                _graphPersistenceErrors.Add(new PersistenceError(graph.Id, ex));
        }
    }

    internal bool IsCancellationRequested(Task task)
    {
        lock (_gate)
            return _runningCancellation.TryGetValue(task.Id, out var cts) && cts.IsCancellationRequested;
    }

    private Func<TaskContext, object?>? GetHandlerOrNull(TaskType type)
    {
        lock (_gate)
            return _handlers.TryGetValue(type, out var handler) ? handler : null;
    }

    private void RegisterRunningCancellation(Task task, CancellationTokenSource cts)
    {
        lock (_gate)
            _runningCancellation[task.Id] = cts;
    }

    private void UnregisterRunningCancellation(Task task, CancellationTokenSource cts)
    {
        lock (_gate)
        {
            if (_runningCancellation.TryGetValue(task.Id, out var current) && ReferenceEquals(current, cts))
                _runningCancellation.Remove(task.Id);
        }

        cts.Dispose();
    }

    private void PersistTask(Task task)
    {
        if (Store is null)
            return;

        try
        {
            Store.Tasks.Save(task);
        }
        catch (Exception ex)
        {
            lock (_gate)
                _persistenceErrors.Add(new PersistenceError(task.Id, ex));
            _events.Publish(new TaskEvent(TaskEventType.PersistenceFailed, task, task.Status, error: ex.Message));
        }
    }

    private void AttachFailedResult(Task task, string error, ITaskFailureDetails details)
    {
        var result = new TaskResult
        {
            TaskId = task.Id,
            Status = TaskStatus.Failed,
            Output = details.Output,
            Error = details.ErrorOutput ?? error,
            ReturnCode = details.ReturnCode,
            Raw = details.Raw,
            StartedAt = DateTimeOffset.UtcNow,
            FinishedAt = DateTimeOffset.UtcNow,
            TerminationReason = details.TerminationReason
        };
        task.AttachResult(result);
        task.TransitionTo(TaskStatus.Failed, error);
        PersistTask(task);
        _events.Publish(new TaskEvent(TaskEventType.Failed, task, TaskStatus.Failed, previousStatus: TaskStatus.Running));
    }

    private static object? RunBash(TaskContext context)
    {
        var bash = FindExecutable("bash") ?? throw new InvalidOperationException("Bash executable was not found.");
        return RunProcess(context, new ShellCommand(bash, ["-lc", context.Payload]));
    }

    private static object? RunPowerShell(TaskContext context)
    {
        if (FindExecutable("pwsh") is { } pwsh)
            return RunProcess(context, new ShellCommand(pwsh, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", context.Payload]));

        if (FindExecutable("powershell") is { } powershell)
            return RunProcess(context, new ShellCommand(powershell, ["-NoLogo", "-NoProfile", "-NonInteractive", "-Command", context.Payload]));

        throw new InvalidOperationException("PowerShell executable was not found.");
    }

    private static object? RunProcessTask(TaskContext context)
    {
        var command = context.Task.GetProcessCommand();
        var fileName = Path.IsPathRooted(command.FileName)
            ? command.FileName
            : FindExecutable(command.FileName) ?? throw new InvalidOperationException($"Process executable '{command.FileName}' was not found.");

        return RunProcess(context, new ShellCommand(fileName, command.Args, command.WorkingDirectory, command.Environment));
    }

    private static object? RunProcess(TaskContext context, ShellCommand command)
    {
        var startInfo = new ProcessStartInfo(command.FileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        if (!string.IsNullOrWhiteSpace(command.WorkingDirectory))
            startInfo.WorkingDirectory = command.WorkingDirectory;
        foreach (var entry in command.Environment ?? new Dictionary<string, string>())
            startInfo.Environment[entry.Key] = entry.Value;

        foreach (var argument in command.Arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();

        if (!process.Start())
            throw new InvalidOperationException($"Failed to start process '{command.FileName}'.");

        var stdoutReader = ReadProcessStream(process.StandardOutput.BaseStream, "stdout", context, stdout);
        var stderrReader = ReadProcessStream(process.StandardError.BaseStream, "stderr", context, stderr);
        var deadline = context.Timeout.HasValue ? DateTimeOffset.UtcNow + TimeSpan.FromSeconds(context.Timeout.Value) : (DateTimeOffset?)null;

        while (!process.WaitForExit(25))
        {
            if (context.Cancelled)
            {
                KillProcess(process);
                WaitForReaders(stdoutReader, stderrReader);
                throw new TaskCancelledException("Task was cancelled.", stdout.ToString(), NullIfEmpty(stderr.ToString()), TryGetExitCode(process),
                    CreateRawProcessResult(stdout, stderr, TryGetExitCode(process)));
            }

            if (deadline.HasValue && DateTimeOffset.UtcNow >= deadline.Value)
            {
                KillProcess(process);
                WaitForReaders(stdoutReader, stderrReader);
                throw new TaskTimeoutException("Task timeout.", stdout.ToString(), NullIfEmpty(stderr.ToString()), TryGetExitCode(process),
                    CreateRawProcessResult(stdout, stderr, TryGetExitCode(process)));
            }
        }

        process.WaitForExit();
        WaitForReaders(stdoutReader, stderrReader);

        var output = stdout.ToString();
        var error = NullIfEmpty(stderr.ToString());
        var exitCode = process.ExitCode;
        var raw = CreateRawProcessResult(stdout, stderr, exitCode);
        if (exitCode != 0)
            throw new TaskExecutionException($"Process exited with code {exitCode}.", "exit_code", output, error, exitCode, raw);

        return raw;
    }

    private static async System.Threading.Tasks.Task ReadProcessStream(Stream stream, string outputStream, TaskContext context, StringBuilder buffer)
    {
        var bytes = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(bytes).ConfigureAwait(false);
            if (read == 0)
                break;

            var chunk = Encoding.UTF8.GetString(bytes, 0, read);
            buffer.Append(chunk);
            context.EmitOutput(outputStream, chunk);
        }
    }

    private static void WaitForReaders(params System.Threading.Tasks.Task[] readers)
    {
        try
        {
            System.Threading.Tasks.Task.WaitAll(readers);
        }
        catch (AggregateException ex) when (ex.InnerExceptions.All(inner => inner is IOException or ObjectDisposedException))
        {
        }
    }

    private static void KillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode : null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static object CreateRawProcessResult(StringBuilder stdout, StringBuilder stderr, int? returnCode) =>
        new { stdout = stdout.ToString(), stderr = stderr.ToString(), returncode = returnCode };

    private static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    private static string? FindExecutable(string name)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE;.CMD;.BAT;.COM")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var path in paths)
        {
            var candidate = Path.Combine(path, name);
            if (File.Exists(candidate))
                return candidate;

            foreach (var extension in extensions)
            {
                var executable = candidate.EndsWith(extension, StringComparison.OrdinalIgnoreCase) ? candidate : candidate + extension;
                if (File.Exists(executable))
                    return executable;
            }
        }

        return null;
    }

    private sealed record ShellCommand(
        string FileName,
        IReadOnlyList<string> Arguments,
        string? WorkingDirectory = null,
        IReadOnlyDictionary<string, string>? Environment = null);

    private static IReadOnlyDictionary<string, Task> SnapshotUpstream(IReadOnlyDictionary<string, Task>? upstream, IReadOnlyList<Task>? orderedUpstream)
    {
        var snapshot = upstream?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal) ?? new Dictionary<string, Task>(StringComparer.Ordinal);
        if (orderedUpstream is null)
            return snapshot;

        foreach (var task in orderedUpstream)
            snapshot.TryAdd(task.Id, task);
        return snapshot;
    }

    private static IReadOnlyList<Task> SnapshotOrderedUpstream(IReadOnlyDictionary<string, Task> upstream, IReadOnlyList<Task>? orderedUpstream) =>
        orderedUpstream?.ToList().AsReadOnly()
        ?? upstream.OrderBy(kvp => kvp.Key, StringComparer.Ordinal).Select(kvp => kvp.Value).ToList().AsReadOnly();

    private static void SleepBackoff(Task task, double backoffSeconds)
    {
        if (backoffSeconds <= 0)
            return;

        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(backoffSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (task.Status == TaskStatus.Cancelled)
                throw new OperationCanceledException("Task was cancelled during retry backoff.");

            var remaining = deadline - DateTimeOffset.UtcNow;
            Thread.Sleep(remaining < TimeSpan.FromMilliseconds(10) ? remaining : TimeSpan.FromMilliseconds(10));
        }
    }

    private static void ValidateRetryPolicy(RetryPolicy retryPolicy)
    {
        if (retryPolicy is null)
            throw new ArgumentNullException(nameof(retryPolicy));
        if (retryPolicy.MaxAttempts < 1)
            throw new ArgumentOutOfRangeException(nameof(retryPolicy), "MaxAttempts must be at least 1.");
        if (double.IsNaN(retryPolicy.BackoffSeconds) || double.IsInfinity(retryPolicy.BackoffSeconds) || retryPolicy.BackoffSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(retryPolicy), "BackoffSeconds must be finite and non-negative.");
    }

    private static TaskResult NormalizeResult(object? raw)
    {
        if (raw is string text)
            return new TaskResult { Output = text, Error = null, ReturnCode = null, Raw = text };

        var stdout = TryGetProperty(raw, "stdout") ?? TryGetProperty(raw, "Stdout");
        var stderr = TryGetProperty(raw, "stderr") ?? TryGetProperty(raw, "Stderr");
        var returnCode = TryGetProperty(raw, "returncode") ?? TryGetProperty(raw, "ReturnCode");
        if (stdout is not null || stderr is not null || returnCode is not null)
        {
            var output = stdout?.ToString() ?? string.Empty;
            var error = string.IsNullOrWhiteSpace(stderr?.ToString()) ? null : stderr?.ToString();
            var code = returnCode is int i ? i : returnCode is long l ? (int?)l : null;
            return new TaskResult { Output = output, Error = error, ReturnCode = code, Raw = raw };
        }

        return new TaskResult { Output = string.Empty, Error = null, ReturnCode = null, Raw = raw };
    }

    private static object? TryGetProperty(object? value, string name)
    {
        if (value is null)
            return null;

        var property = value.GetType().GetProperty(name, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
        return property?.GetValue(value);
    }
}

public sealed class SubmittedTask
{
    private int _started;

    internal SubmittedTask(TaskExecutor executor, Task task, RetryPolicy retryPolicy, IReadOnlyDictionary<string, Task> upstream, IReadOnlyList<Task> orderedUpstream)
    {
        Executor = executor;
        Task = task;
        InnerTask = System.Threading.Tasks.Task.Run(() =>
        {
            Interlocked.Exchange(ref _started, 1);
            return executor.Execute(task, retryPolicy, upstream, orderedUpstream);
        });
    }

    public TaskExecutor Executor { get; }
    public Task Task { get; }
    internal System.Threading.Tasks.Task<TaskResult> InnerTask { get; }

    public bool Cancel()
    {
        if (Interlocked.CompareExchange(ref _started, 2, 0) != 0)
            return false;

        Executor.Cancel(Task);
        return true;
    }

    public System.Runtime.CompilerServices.TaskAwaiter<TaskResult> GetAwaiter() => InnerTask.GetAwaiter();

    public System.Threading.Tasks.Task<TaskResult> AsTask() => InnerTask;
}

public sealed class TaskContext
{
    private readonly IReadOnlyDictionary<string, Task> _upstream;
    private readonly IReadOnlyList<Task> _upstreamTasks;
    private readonly CancellationToken _cancellationToken;

    public TaskContext(Task task, TaskExecutor executor, IReadOnlyDictionary<string, Task>? upstream = null, CancellationToken cancellationToken = default, IReadOnlyList<Task>? orderedUpstream = null)
    {
        Task = task ?? throw new ArgumentNullException(nameof(task));
        Executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _upstream = new System.Collections.ObjectModel.ReadOnlyDictionary<string, Task>(
            upstream?.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal) ?? new Dictionary<string, Task>(StringComparer.Ordinal));
        _upstreamTasks = (orderedUpstream?.ToList() ?? _upstream.Values.ToList()).AsReadOnly();
        _cancellationToken = cancellationToken;
    }

    public string Id => Task.Id;
    public string Title => Task.Title;
    public string Description => Task.Description;
    public string Payload => Task.Payload;
    public TaskType Type => Task.Type;
    public int? Timeout => Task.Timeout;
    public TaskStatus Status => Task.Status;
    public bool Cancelled => Task.Status == TaskStatus.Cancelled || _cancellationToken.IsCancellationRequested || Executor.IsCancellationRequested(Task);
    public CancellationToken CancellationToken => _cancellationToken;
    public Task Task { get; }
    public TaskExecutor Executor { get; }
    public IReadOnlyDictionary<string, Task> Upstream => _upstream;
    public IReadOnlyList<Task> UpstreamTasks => _upstreamTasks;

    public void RaiseIfCancelled()
    {
        if (Cancelled)
            throw new OperationCanceledException("Task is cancelled.");
    }

    public void ThrowIfCancellationRequested() => RaiseIfCancelled();

    public void EmitProgress(double? percent = null, string? message = null)
    {
        RaiseIfCancelled();

        Executor.Events.Publish(new TaskEvent(TaskEventType.Progress, Task, Task.Status, progressPercent: percent, progressMessage: message));
    }

    public void EmitOutput(string stream, string chunk)
    {
        Executor.Events.Publish(new TaskEvent(TaskEventType.Output, Task, Task.Status, outputStream: stream, outputChunk: chunk));
    }
}
