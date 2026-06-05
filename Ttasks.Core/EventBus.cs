namespace Ttasks.Core;

public sealed class EventBus
{
    private readonly object _gate = new();
    private readonly List<Action<TaskEvent>> _subscribers = [];
    private readonly List<Exception> _errors = [];

    public IReadOnlyList<Exception> Errors
    {
        get
        {
            lock (_gate)
                return _errors.ToList().AsReadOnly();
        }
    }

    public IDisposable Subscribe(Action<TaskEvent> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_gate)
            _subscribers.Add(handler);
        return new Subscription(this, handler);
    }

    public IDisposable SubscribeScoped(Action<TaskEvent> handler)
    {
        return Subscribe(handler);
    }

    public void Publish(TaskEvent evt)
    {
        ArgumentNullException.ThrowIfNull(evt);

        lock (_gate)
        {
            foreach (var subscriber in _subscribers.ToArray())
            {
                try
                {
                    subscriber(evt);
                }
                catch (Exception ex)
                {
                    _errors.Add(ex);
                }
            }
        }
    }

    internal bool Remove(Action<TaskEvent> handler)
    {
        lock (_gate)
            return _subscribers.Remove(handler);
    }

    private sealed class Subscription(EventBus bus, Action<TaskEvent> handler) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed) return;

            _disposed = true;
            bus.Remove(handler);
        }
    }
}
