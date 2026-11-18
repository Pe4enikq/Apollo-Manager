using Walkman.Contracts.Plugins;

namespace Walkman.L2.Crypto.OpenMG;

/// <summary>
/// Dynamic plugin entry point for L2.Crypto.OpenMG (ea3/EA3 and DvID XOR engine arrives in M2).
/// </summary>
public sealed class OpenMgCryptoPlugin : IWalkmanPlugin
{
    public string ModuleId => WellKnownModules.L2CryptoOpenMg;

    public PluginTier Tier => PluginTier.L2Engine;

    public ValueTask InitializeAsync(IPluginRegistrationContext context, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask ShutdownAsync(CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
