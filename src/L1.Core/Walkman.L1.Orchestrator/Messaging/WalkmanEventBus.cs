using Walkman.Contracts.Messaging;

namespace Walkman.L1.Orchestrator.Messaging;

/// <summary>
/// Thread-safe, zero-dependency publish/subscribe event bus supporting scope-bound unsubscription
/// so unloaded collectible plugin scopes leave zero retained event handler delegates.
/// </summary>
public sealed class WalkmanEventBus : IWalkmanEventBus
{
    private const string GlobalScope = "__global__";

    private sealed record SubscriptionEntry(Guid Id, string ScopeId, Action<object> Invoker);

    private readonly object _gate = new();
    private readonly Dictionary<Type, List<SubscriptionEntry>> _subscriptions = new();

    /// <summary>
    /// Synchronously publishes an event instance to all currently registered subscribers for TEvent.
    /// </summary>
    public void Publish<TEvent>(TEvent evt) where TEvent : class, IEvent
    {
        ArgumentNullException.ThrowIfNull(evt);

        SubscriptionEntry[] snapshot;
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(typeof(TEvent), out var entries) || entries.Count == 0)
            {
                return;
            }

            snapshot = entries.ToArray();
        }

        foreach (var entry in snapshot)
        {
            entry.Invoker(evt);
        }
    }

    /// <summary>
    /// Registers a strongly-typed subscriber optionally associated with a plugin scope identifier.
    /// </summary>
    public IDisposable Subscribe<TEvent>(Action<TEvent> handler, string? scopeId = null) where TEvent : class, IEvent
    {
        ArgumentNullException.ThrowIfNull(handler);

        var entryId = Guid.NewGuid();
        var effectiveScope = string.IsNullOrWhiteSpace(scopeId) ? GlobalScope : scopeId;
        var entry = new SubscriptionEntry(entryId, effectiveScope, staticPayload => handler((TEvent)staticPayload));
        var eventType = typeof(TEvent);

        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(eventType, out var entries))
            {
                entries = new List<SubscriptionEntry>();
                _subscriptions[eventType] = entries;
            }

            entries.Add(entry);
        }

        return new SubscriptionToken(this, eventType, entryId);
    }

    /// <summary>
    /// Removes all subscriptions registered under the given plugin scope and returns the removed count.
    /// </summary>
    public int UnsubscribeScope(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        int removedTotal = 0;
        lock (_gate)
        {
            List<Type>? emptyKeys = null;
            foreach (var (eventType, entries) in _subscriptions)
            {
                int removed = entries.RemoveAll(e => string.Equals(e.ScopeId, scopeId, StringComparison.Ordinal));
                removedTotal += removed;
                if (entries.Count == 0)
                {
                    emptyKeys ??= new List<Type>();
                    emptyKeys.Add(eventType);
                }
            }

            if (emptyKeys is not null)
            {
                foreach (var key in emptyKeys)
                {
                    _subscriptions.Remove(key);
                }
            }
        }

        return removedTotal;
    }

    private void RemoveById(Type eventType, Guid entryId)
    {
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(eventType, out var entries))
            {
                return;
            }

            entries.RemoveAll(e => e.Id == entryId);
            if (entries.Count == 0)
            {
                _subscriptions.Remove(eventType);
            }
        }
    }

    private sealed class SubscriptionToken : IDisposable
    {
        private WalkmanEventBus? _bus;
        private readonly Type _eventType;
        private readonly Guid _entryId;

        public SubscriptionToken(WalkmanEventBus bus, Type eventType, Guid entryId)
        {
            _bus = bus;
            _eventType = eventType;
            _entryId = entryId;
        }

        public void Dispose()
        {
            var bus = Interlocked.Exchange(ref _bus, null);
            bus?.RemoveById(_eventType, _entryId);
        }
    }
}
