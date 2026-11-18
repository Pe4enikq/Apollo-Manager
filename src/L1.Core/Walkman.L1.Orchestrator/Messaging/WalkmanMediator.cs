using Walkman.Contracts.Messaging;

namespace Walkman.L1.Orchestrator.Messaging;

/// <summary>
/// Thread-safe, zero-dependency command/query mediator supporting dynamic plugin handler registration
/// and deterministic scope cleanup upon AssemblyLoadContext unload.
/// </summary>
public sealed class WalkmanMediator : IWalkmanMediator
{
    private const string GlobalScope = "__global__";

    private sealed record HandlerRegistration(
        string ScopeId,
        Func<object, CancellationToken, Task<object>> Invoker);

    private readonly object _gate = new();
    private readonly Dictionary<Type, HandlerRegistration> _handlers = new();

    /// <summary>
    /// Registers an ICommandHandler for TCommand, tagged with an optional plugin scope.
    /// </summary>
    public void RegisterCommandHandler<TCommand, TResponse>(
        ICommandHandler<TCommand, TResponse> handler,
        string? scopeId = null)
        where TCommand : ICommand<TResponse>
    {
        ArgumentNullException.ThrowIfNull(handler);

        RegisterInternal(
            typeof(TCommand),
            scopeId,
            async (request, ct) =>
            {
                var result = await handler.HandleAsync((TCommand)request, ct).ConfigureAwait(false);
                return result!;
            });
    }

    /// <summary>
    /// Registers an IQueryHandler for TQuery, tagged with an optional plugin scope.
    /// </summary>
    public void RegisterQueryHandler<TQuery, TResponse>(
        IQueryHandler<TQuery, TResponse> handler,
        string? scopeId = null)
        where TQuery : IQuery<TResponse>
    {
        ArgumentNullException.ThrowIfNull(handler);

        RegisterInternal(
            typeof(TQuery),
            scopeId,
            async (request, ct) =>
            {
                var result = await handler.HandleAsync((TQuery)request, ct).ConfigureAwait(false);
                return result!;
            });
    }

    /// <inheritdoc />
    public async Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        return await DispatchAsync<TResponse>(command, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return await DispatchAsync<TResponse>(query, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Removes all command and query handlers owned by the specified scope and returns the count removed.
    /// </summary>
    public int UnregisterScope(string scopeId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scopeId);

        lock (_gate)
        {
            var keysToRemove = _handlers
                .Where(kvp => string.Equals(kvp.Value.ScopeId, scopeId, StringComparison.Ordinal))
                .Select(kvp => kvp.Key)
                .ToList();

            foreach (var key in keysToRemove)
            {
                _handlers.Remove(key);
            }

            return keysToRemove.Count;
        }
    }

    private void RegisterInternal(
        Type requestType,
        string? scopeId,
        Func<object, CancellationToken, Task<object>> invoker)
    {
        var effectiveScope = string.IsNullOrWhiteSpace(scopeId) ? GlobalScope : scopeId;

        lock (_gate)
        {
            if (_handlers.TryGetValue(requestType, out var existing) &&
                !string.Equals(existing.ScopeId, effectiveScope, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Handler for '{requestType.FullName}' is already registered by scope '{existing.ScopeId}'.");
            }

            _handlers[requestType] = new HandlerRegistration(effectiveScope, invoker);
        }
    }

    private async Task<TResponse> DispatchAsync<TResponse>(object request, CancellationToken ct)
    {
        var requestType = request.GetType();
        HandlerRegistration? registration;

        lock (_gate)
        {
            _handlers.TryGetValue(requestType, out registration);
        }

        if (registration is null)
        {
            throw new InvalidOperationException(
                $"No mediator handler is registered for '{requestType.FullName}'. Ensure the owning L2/L3 plugin is loaded.");
        }

        var rawResult = await registration.Invoker(request, ct).ConfigureAwait(false);
        return (TResponse)rawResult;
    }
}
