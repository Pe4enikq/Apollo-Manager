namespace Walkman.Contracts.Messaging;

/// <summary>
/// Marker interface for strongly-typed domain and lifecycle events published on the L1 event bus.
/// </summary>
public interface IEvent
{
}

/// <summary>
/// Request contract for state-mutating operations routed through the L1 mediator.
/// </summary>
public interface ICommand<TResponse>
{
}

/// <summary>
/// Request contract for read-only queries routed through the L1 mediator.
/// </summary>
public interface IQuery<TResponse>
{
}

/// <summary>
/// Handler for a state-mutating command registered by a core component or dynamically loaded plugin.
/// </summary>
public interface ICommandHandler<in TCommand, TResponse> where TCommand : ICommand<TResponse>
{
    Task<TResponse> HandleAsync(TCommand command, CancellationToken ct = default);
}

/// <summary>
/// Handler for a read-only query registered by a core component or dynamically loaded plugin.
/// </summary>
public interface IQueryHandler<in TQuery, TResponse> where TQuery : IQuery<TResponse>
{
    Task<TResponse> HandleAsync(TQuery query, CancellationToken ct = default);
}

/// <summary>
/// Scope-aware publish/subscribe event bus enabling clean plugin unload without delegate leaks.
/// </summary>
public interface IWalkmanEventBus
{
    void Publish<TEvent>(TEvent evt) where TEvent : class, IEvent;

    IDisposable Subscribe<TEvent>(Action<TEvent> handler, string? scopeId = null) where TEvent : class, IEvent;

    int UnsubscribeScope(string scopeId);
}

/// <summary>
/// Contract-based command and query mediator decoupling L3 presentation modules from L2 engines.
/// </summary>
public interface IWalkmanMediator
{
    Task<TResponse> SendAsync<TResponse>(ICommand<TResponse> command, CancellationToken ct = default);

    Task<TResponse> QueryAsync<TResponse>(IQuery<TResponse> query, CancellationToken ct = default);
}
