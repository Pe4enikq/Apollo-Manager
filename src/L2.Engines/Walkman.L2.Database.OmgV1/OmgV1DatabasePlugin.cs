using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;

namespace Walkman.L2.Database.OmgV1;

/// <summary>
/// Dynamic plugin entry point for L2.Database.OmgV1 registering IDatabaseEngine.
/// </summary>
public sealed class OmgV1DatabasePlugin : IWalkmanPlugin
{
    public string ModuleId => WellKnownModules.L2DatabaseOmgV1;

    public PluginTier Tier => PluginTier.L2Engine;

    public ValueTask InitializeAsync(IPluginRegistrationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterEngineService<IDatabaseEngine>(
            new OmgV1DatabaseEngine(
                () => context.TryResolveEngine<ITransportDriver>()
                    ?? throw new InvalidOperationException("Required ITransportDriver is not loaded in the active scope."),
                () => context.TryResolveEngine<IOpenMgCryptoService>()));

        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
