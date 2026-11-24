using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;

namespace Walkman.L2.Crypto.OpenMG;

/// <summary>
/// Dynamic plugin entry point for L2.Crypto.OpenMG registering IOpenMgCryptoService.
/// </summary>
public sealed class OpenMgCryptoPlugin : IWalkmanPlugin
{
    public string ModuleId => WellKnownModules.L2CryptoOpenMg;

    public PluginTier Tier => PluginTier.L2Engine;

    public ValueTask InitializeAsync(IPluginRegistrationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterEngineService<IOpenMgCryptoService>(new OpenMgCryptoService());
        context.RegisterEngineService<IOpenMgIcvBridge>(new WindowsOpenMgIcvBridge());
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
