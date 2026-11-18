using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;

namespace Walkman.L2.Transport.Msc;

/// <summary>
/// Dynamic plugin entry point for L2.Transport.Msc registering the Mass Storage ITransportDriver.
/// </summary>
public sealed class MscTransportPlugin : IWalkmanPlugin
{
    public string ModuleId => WellKnownModules.L2TransportMsc;

    public PluginTier Tier => PluginTier.L2Engine;

    public ValueTask InitializeAsync(IPluginRegistrationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterEngineService<ITransportDriver>(new MscTransportDriver());
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
