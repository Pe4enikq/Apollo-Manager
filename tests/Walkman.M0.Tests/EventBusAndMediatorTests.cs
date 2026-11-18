using Walkman.Contracts.Messaging;
using Walkman.L1.Orchestrator.Messaging;

namespace Walkman.M0.Tests;

/// <summary>
/// Verifies WalkmanEventBus and WalkmanMediator routing, scoped cleanup, and missing-handler guards.
/// </summary>
public sealed class EventBusAndMediatorTests
{
    private sealed record PingEvent(string Message) : IEvent;

    private sealed record PingCommand(string Input) : ICommand<string>;

    private sealed record StatusQuery(int Code) : IQuery<string>;

    private sealed class PingCommandHandler : ICommandHandler<PingCommand, string>
    {
        public Task<string> HandleAsync(PingCommand command, CancellationToken ct = default) =>
            Task.FromResult($"ACK:{command.Input}");
    }

    private sealed class StatusQueryHandler : IQueryHandler<StatusQuery, string>
    {
        public Task<string> HandleAsync(StatusQuery query, CancellationToken ct = default) =>
            Task.FromResult($"STATUS:{query.Code}");
    }

    [Fact]
    public void EventBus_DeliversToSubscribers_AndRespectsDisposableAndScopeUnsubscribe()
    {
        var bus = new WalkmanEventBus();
        var receivedGlobal = new List<string>();
        var receivedScopeA = new List<string>();
        var receivedScopeB = new List<string>();

        using var globalToken = bus.Subscribe<PingEvent>(e => receivedGlobal.Add(e.Message));
        _ = bus.Subscribe<PingEvent>(e => receivedScopeA.Add(e.Message), scopeId: "scope-a");
        _ = bus.Subscribe<PingEvent>(e => receivedScopeB.Add(e.Message), scopeId: "scope-b");

        bus.Publish(new PingEvent("first"));

        int removedCount = bus.UnsubscribeScope("scope-a");
        Assert.Equal(1, removedCount);

        bus.Publish(new PingEvent("second"));

        globalToken.Dispose();
        bus.Publish(new PingEvent("third"));

        Assert.Equal(["first", "second"], receivedGlobal);
        Assert.Equal(["first"], receivedScopeA);
        Assert.Equal(["first", "second", "third"], receivedScopeB);
    }

    [Fact]
    public async Task Mediator_DispatchesCommandsAndQueries_AndPurgesOnScopeUnregister()
    {
        var mediator = new WalkmanMediator();
        mediator.RegisterCommandHandler(new PingCommandHandler(), scopeId: "scope-a");
        mediator.RegisterQueryHandler(new StatusQueryHandler(), scopeId: "scope-b");

        var cmdResult = await mediator.SendAsync(new PingCommand("E403"));
        var queryResult = await mediator.QueryAsync(new StatusQuery(200));

        Assert.Equal("ACK:E403", cmdResult);
        Assert.Equal("STATUS:200", queryResult);

        // Registering the same command from a conflicting scope must fail loudly.
        Assert.Throws<InvalidOperationException>(() =>
            mediator.RegisterCommandHandler(new PingCommandHandler(), scopeId: "scope-b"));

        int removed = mediator.UnregisterScope("scope-a");
        Assert.Equal(1, removed);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            mediator.SendAsync(new PingCommand("after-unload")));

        // Scope-b query handler must still be intact.
        Assert.Equal("STATUS:200", await mediator.QueryAsync(new StatusQuery(200)));
    }
}
