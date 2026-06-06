using GitHub.Copilot;
using System.Text.Json;

namespace Ttasks.Core;

public sealed class LlmTurnRequest
{
    public required string Prompt { get; init; }
    public required string Model { get; init; }
    public bool ToolsEnabled { get; init; }
    public TimeSpan? Timeout { get; init; }
    public string PermissionPolicy { get; init; } = LlmPermissionPolicies.ApproveAll;
}

public sealed class LlmTurnResult
{
    private LlmTurnResult(string? text, bool isText, object? raw)
    {
        AssistantText = text;
        IsText = isText;
        Raw = raw;
    }

    public string? AssistantText { get; }
    public bool IsText { get; }
    public object? Raw { get; }

    public static LlmTurnResult TextResult(string? text) => new(text, true, text);
    public static LlmTurnResult Text(string? text) => TextResult(text);
    public static LlmTurnResult Empty { get; } = new(null, true, null);
    public static LlmTurnResult NonText(object? raw) => new(null, false, raw);
}

public sealed class LlmSessionOptions
{
    public string Model { get; init; } = CopilotHandlers.DefaultAgentModel;
    public string? ReasoningEffort { get; init; }
    public string? WorkingDirectory { get; init; }
    public TimeSpan? Timeout { get; init; }
    public string PermissionPolicy { get; init; } = LlmPermissionPolicies.ApproveAll;
    public IReadOnlyDictionary<string, object?> AdditionalOptions { get; init; } = new Dictionary<string, object?>();
}

public sealed class LlmHandlerOptions
{
    public string? Model { get; init; }
    public TimeSpan? Timeout { get; init; }
    public string PermissionPolicy { get; init; } = LlmPermissionPolicies.ApproveAll;
    public string? ReasoningEffort { get; init; }
    public string? WorkingDirectory { get; init; }
    public bool IncludeUpstreamResults { get; init; }
    public IReadOnlyDictionary<string, object?> AdditionalOptions { get; init; } = new Dictionary<string, object?>();
}

public static class LlmPermissionPolicies
{
    public const string ApproveAll = "approve-all";
}

public interface ILlmProvider
{
    ILlmSession CreateSession(LlmSessionOptions options);
}

public interface ILlmSession : IDisposable, IAsyncDisposable
{
    event Action<object>? Event;
    System.Threading.Tasks.Task<LlmTurnResult> SendAndWaitAsync(LlmTurnRequest request, CancellationToken cancellationToken = default);
    System.Threading.Tasks.Task AbortAsync();
}

public static class CopilotHandlers
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public const string DefaultPromptModel = "gpt-5.4-mini";
    public static readonly TimeSpan DefaultPromptTimeout = TimeSpan.FromSeconds(60);
    public const string DefaultAgentModel = "gpt-5.5";

    public static Func<TaskContext, object?> MakePromptHandler(ILlmProvider provider, LlmHandlerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        options ??= new LlmHandlerOptions();
        var model = ValidateModel(options.Model ?? DefaultPromptModel);
        var timeout = ValidateTimeout(options.Timeout ?? DefaultPromptTimeout);

        return context => ExecuteOneShot(provider, context, model, timeout, toolsEnabled: false, options);
    }

    public static Func<TaskContext, object?> MakeAgentHandler(ILlmProvider provider, LlmHandlerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(provider);
        options ??= new LlmHandlerOptions();
        var model = ValidateModel(options.Model ?? DefaultAgentModel);
        var timeout = ValidateTimeout(options.Timeout);

        return context => ExecuteOneShot(provider, context, model, timeout, toolsEnabled: true, options);
    }

    private static string ExecuteOneShot(
        ILlmProvider provider,
        TaskContext context,
        string model,
        TimeSpan? defaultTimeout,
        bool toolsEnabled,
        LlmHandlerOptions options)
    {
        context.RaiseIfCancelled();
        using var session = provider.CreateSession(new LlmSessionOptions
        {
            Model = model,
            Timeout = defaultTimeout,
            PermissionPolicy = options.PermissionPolicy,
            ReasoningEffort = options.ReasoningEffort,
            WorkingDirectory = options.WorkingDirectory,
            AdditionalOptions = options.AdditionalOptions
        });
        context.RaiseIfCancelled();
        var request = CreateRequest(context, model, defaultTimeout, toolsEnabled, options.PermissionPolicy, options.IncludeUpstreamResults);
        var result = SendBlocking(session, request, context.CancellationToken);
        context.RaiseIfCancelled();
        return result;
    }

    internal static LlmTurnRequest CreateRequest(TaskContext context, string model, TimeSpan? defaultTimeout, bool toolsEnabled, string permissionPolicy, bool includeUpstreamResults = false)
    {
        var timeout = context.Timeout.HasValue ? TimeSpan.FromSeconds(context.Timeout.Value) : defaultTimeout;
        return new LlmTurnRequest
        {
            Prompt = includeUpstreamResults ? ComposePromptWithUpstream(context) : context.Payload,
            Model = model,
            ToolsEnabled = toolsEnabled,
            Timeout = timeout,
            PermissionPolicy = permissionPolicy
        };
    }

    internal static string ComposePromptWithUpstream(TaskContext context) =>
        $"Instruction:\n{context.Payload}\n\nUpstream results:\n{JsonSerializer.Serialize(CreateUpstreamEnvelope(context), JsonOptions)}";

    private static IReadOnlyList<UpstreamEnvelopeEntry> CreateUpstreamEnvelope(TaskContext context) =>
        context.UpstreamTasks.Select(task => new UpstreamEnvelopeEntry(
            task.Id,
            task.Title,
            task.Description,
            task.TypeName,
            task.Status.ToString(),
            task.Result?.Output ?? string.Empty,
            task.Result?.Error,
            task.Result?.ReturnCode,
            task.Result?.TerminationReason,
            task.Metadata)).ToList();

    private sealed record UpstreamEnvelopeEntry(
        string Id,
        string Title,
        string Description,
        string Type,
        string Status,
        string Output,
        string? Error,
        int? ReturnCode,
        string? TerminationReason,
        IReadOnlyDictionary<string, object?> Metadata);

    internal static string SendBlocking(ILlmSession session, LlmTurnRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return NormalizeText(SendWithCancellationAsync(session, request, cancellationToken).GetAwaiter().GetResult());
        }
        catch (OperationCanceledException)
        {
            throw new TaskCancelledException("Task was cancelled.");
        }
    }

    internal static async System.Threading.Tasks.Task<LlmTurnResult> SendWithCancellationAsync(
        ILlmSession session,
        LlmTurnRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await session.SendAndWaitAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await session.AbortAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static string NormalizeText(LlmTurnResult? result) =>
        result is { IsText: true, AssistantText: { } text } ? text : string.Empty;

    internal static string ValidateModel(string model)
    {
        if (string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Model must be non-empty.", nameof(model));
        return model;
    }

    internal static TimeSpan? ValidateTimeout(TimeSpan? timeout)
    {
        if (timeout.HasValue && timeout.Value <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout), "Timeout must be positive when supplied.");
        return timeout;
    }
}

public sealed class LlmAgentSession : IDisposable, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SemaphoreSlim _turns = new(1, 1);
    private readonly ILlmProvider _provider;
    private readonly List<Action<object>> _subscribers = [];
    private readonly List<Exception> _eventErrors = [];
    private ILlmSession? _session;
    private LlmSessionState _state = LlmSessionState.Closed;
    private Action<object>? _providerEventHandler;

    public LlmAgentSession(ILlmProvider provider, LlmSessionOptions options)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        Options = options ?? throw new ArgumentNullException(nameof(options));
        CopilotHandlers.ValidateModel(Options.Model);
        CopilotHandlers.ValidateTimeout(Options.Timeout);
    }

    public LlmSessionOptions Options { get; }
    public string Model => Options.Model;
    public string? ReasoningEffort => Options.ReasoningEffort;
    public string? WorkingDirectory => Options.WorkingDirectory;
    public TimeSpan? Timeout => Options.Timeout;
    public IReadOnlyList<Exception> EventErrors
    {
        get
        {
            lock (_gate)
                return _eventErrors.ToList().AsReadOnly();
        }
    }

    public LlmAgentSession Enter()
    {
        EnterCore(LlmSessionState.SyncActive);
        return this;
    }

    public async System.Threading.Tasks.Task<LlmAgentSession> EnterAsync()
    {
        EnterCore(LlmSessionState.AsyncActive);
        await System.Threading.Tasks.Task.CompletedTask;
        return this;
    }

    private void EnterCore(LlmSessionState targetState)
    {
        lock (_gate)
        {
            if (_state != LlmSessionState.Closed)
                throw new InvalidOperationException("LLM agent session is already active.");
            try
            {
                _session = _provider.CreateSession(Options);
                _providerEventHandler = DispatchEvent;
                _session.Event += _providerEventHandler;
                _state = targetState;
            }
            catch
            {
                _session?.Dispose();
                _session = null;
                _providerEventHandler = null;
                _state = LlmSessionState.Closed;
                throw;
            }
        }
    }

    public void Exit()
    {
        ILlmSession? session;
        Action<object>? handler;
        lock (_gate)
        {
            session = _session;
            handler = _providerEventHandler;
            _session = null;
            _providerEventHandler = null;
            _state = LlmSessionState.Closed;
        }

        if (session is not null && handler is not null)
            session.Event -= handler;
        session?.Dispose();
    }

    public async System.Threading.Tasks.Task ExitAsync()
    {
        ILlmSession? session;
        Action<object>? handler;
        lock (_gate)
        {
            session = _session;
            handler = _providerEventHandler;
            _session = null;
            _providerEventHandler = null;
            _state = LlmSessionState.Closed;
        }

        if (session is not null && handler is not null)
            session.Event -= handler;
        if (session is not null)
            await session.DisposeAsync().ConfigureAwait(false);
    }

    public string SendAndWait(string prompt, TimeSpan? timeout = null, CancellationToken cancellationToken = default) =>
        SendAndWaitAsync(prompt, timeout, cancellationToken).GetAwaiter().GetResult();

    public async System.Threading.Tasks.Task<string> SendAndWaitAsync(string prompt, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            throw new ArgumentException("Prompt must be non-empty.", nameof(prompt));
        CopilotHandlers.ValidateTimeout(timeout);

        ILlmSession session;
        lock (_gate)
        {
            if (_state == LlmSessionState.Closed || _session is null)
                throw new InvalidOperationException("LLM agent session is not active.");
            session = _session;
        }

        await _turns.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var request = new LlmTurnRequest
            {
                Prompt = prompt,
                Model = Options.Model,
                ToolsEnabled = true,
                Timeout = timeout ?? Options.Timeout,
                PermissionPolicy = Options.PermissionPolicy
            };
            var result = await CopilotHandlers.SendWithCancellationAsync(session, request, cancellationToken).ConfigureAwait(false);
            return CopilotHandlers.NormalizeText(result);
        }
        finally
        {
            _turns.Release();
        }
    }

    public Func<TaskContext, object?> Handler()
    {
        return context =>
        {
            ILlmSessionStateSnapshot snapshot = GetStateSnapshot();
            if (snapshot.State != LlmSessionState.SyncActive)
                throw new InvalidOperationException("LLM agent session handler requires a sync-active session.");

            context.RaiseIfCancelled();
            var timeout = context.Timeout.HasValue ? TimeSpan.FromSeconds(context.Timeout.Value) : Options.Timeout;
            return SendAndWait(context.Payload, timeout, context.CancellationToken);
        };
    }

    public Action On(Action<object> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        lock (_gate)
            _subscribers.Add(handler);

        var removed = 0;
        return () =>
        {
            if (Interlocked.Exchange(ref removed, 1) == 1)
                return;
            lock (_gate)
                _subscribers.Remove(handler);
        };
    }

    private void DispatchEvent(object evt)
    {
        List<Action<object>> subscribers;
        lock (_gate)
            subscribers = _subscribers.ToList();

        foreach (var subscriber in subscribers)
        {
            try
            {
                subscriber(evt);
            }
            catch (Exception ex)
            {
                lock (_gate)
                    _eventErrors.Add(ex);
            }
        }
    }

    private ILlmSessionStateSnapshot GetStateSnapshot()
    {
        lock (_gate)
            return new LlmSessionStateSnapshot(_state);
    }

    public void Dispose() => Exit();

    public ValueTask DisposeAsync()
    {
        return new ValueTask(ExitAsync());
    }

    private enum LlmSessionState
    {
        Closed,
        SyncActive,
        AsyncActive
    }

    private interface ILlmSessionStateSnapshot
    {
        LlmSessionState State { get; }
    }

    private sealed record LlmSessionStateSnapshot(LlmSessionState State) : ILlmSessionStateSnapshot;
}

public sealed class CopilotSdkProvider : ILlmProvider
{
    public ILlmSession CreateSession(LlmSessionOptions options)
    {
        return new CopilotSdkSession(options);
    }
}

internal sealed class CopilotSdkSession : ILlmSession
{
    private readonly LlmSessionOptions _options;
    private CopilotClient? _client;
    private CopilotSession? _session;
    private IDisposable? _subscription;

    public CopilotSdkSession(LlmSessionOptions options)
    {
        _options = options;
    }

    public event Action<object>? Event;

    public async System.Threading.Tasks.Task<LlmTurnResult> SendAndWaitAsync(LlmTurnRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureStarted(request, cancellationToken).ConfigureAwait(false);
        var timeout = request.Timeout;
        var message = await _session!.SendAndWaitAsync(request.Prompt, timeout, cancellationToken).ConfigureAwait(false);
        return message?.Data?.Content is { } content
            ? LlmTurnResult.Text(content)
            : LlmTurnResult.Empty;
    }

    public async System.Threading.Tasks.Task AbortAsync()
    {
        if (_session is not null)
            await _session.AbortAsync().ConfigureAwait(false);
    }

    private async System.Threading.Tasks.Task EnsureStarted(LlmTurnRequest request, CancellationToken cancellationToken)
    {
        if (_session is not null)
            return;

        _client = new CopilotClient(new CopilotClientOptions
        {
            WorkingDirectory = _options.WorkingDirectory
        });
        await _client.StartAsync().ConfigureAwait(false);
        _session = await _client.CreateSessionAsync(new SessionConfig
        {
            Model = _options.Model,
            ReasoningEffort = _options.ReasoningEffort,
            OnPermissionRequest = request.ToolsEnabled && _options.PermissionPolicy == LlmPermissionPolicies.ApproveAll ? PermissionHandler.ApproveAll : null,
            AvailableTools = request.ToolsEnabled ? null : []
        }).ConfigureAwait(false);
        _subscription = _session.On<SessionEvent>(evt => Event?.Invoke(evt));
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public async ValueTask DisposeAsync()
    {
        if (_subscription is not null)
        {
            _subscription.Dispose();
            _subscription = null;
        }

        if (_session is not null)
        {
            await _session.DisposeAsync().ConfigureAwait(false);
            _session = null;
        }

        if (_client is not null)
        {
            await _client.DisposeAsync().ConfigureAwait(false);
            _client = null;
        }
    }
}
