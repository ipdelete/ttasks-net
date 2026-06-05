using Ttasks.Core;

namespace Ttasks.Tests;

internal sealed class RecordingLlmProvider : ILlmProvider
{
    private readonly Queue<LlmTurnResult> _results = new();
    private readonly Queue<Exception> _exceptions = new();
    private readonly object _gate = new();

    public RecordingLlmProvider(string? defaultText = null)
    {
        if (defaultText is not null)
            _results.Enqueue(LlmTurnResult.Text(defaultText));
    }

    public bool ThrowOnCreate { get; set; }
    public bool BlockUntilCancelled { get; set; }
    public TimeSpan DelayEachSend { get; set; }
    public int CreatedSessions { get; private set; }
    public ManualResetEventSlim SendStarted { get; } = new();
    public List<RecordingLlmSession> Sessions { get; } = [];
    public List<LlmTurnRequest> Requests { get; } = [];
    public List<RecordingLlmSession> RequestOwners { get; } = [];

    public void QueueResult(LlmTurnResult result) => _results.Enqueue(result);

    public void QueueException(Exception exception) => _exceptions.Enqueue(exception);

    public ILlmSession CreateSession(LlmSessionOptions options)
    {
        if (ThrowOnCreate)
        {
            ThrowOnCreate = false;
            throw new InvalidOperationException("create failed");
        }

        var session = new RecordingLlmSession(this, options);
        Sessions.Add(session);
        CreatedSessions++;
        return session;
    }

    internal async System.Threading.Tasks.Task<LlmTurnResult> Send(RecordingLlmSession session, LlmTurnRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            Requests.Add(request);
            RequestOwners.Add(session);
        }

        SendStarted.Set();
        if (DelayEachSend > TimeSpan.Zero)
            await System.Threading.Tasks.Task.Delay(DelayEachSend, cancellationToken);

        if (BlockUntilCancelled)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
        }

        if (_exceptions.Count > 0)
            throw _exceptions.Dequeue();

        return _results.Count > 0 ? _results.Dequeue() : LlmTurnResult.Text("ok");
    }
}

internal sealed class RecordingLlmSession : ILlmSession
{
    private readonly RecordingLlmProvider _provider;

    public RecordingLlmSession(RecordingLlmProvider provider, LlmSessionOptions options)
    {
        _provider = provider;
        Options = options;
    }

    public LlmSessionOptions Options { get; }
    public bool AbortCalled { get; private set; }
    public bool Disposed { get; private set; }

    public event Action<object>? Event;

    public System.Threading.Tasks.Task<LlmTurnResult> SendAndWaitAsync(LlmTurnRequest request, CancellationToken cancellationToken = default) =>
        _provider.Send(this, request, cancellationToken);

    public System.Threading.Tasks.Task AbortAsync()
    {
        AbortCalled = true;
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public void Emit(object evt) => Event?.Invoke(evt);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public void Dispose()
    {
        Disposed = true;
    }
}
