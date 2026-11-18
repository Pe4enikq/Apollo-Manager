using Walkman.Contracts.Plugins;

namespace Walkman.L2.Database.OmgV1;

/// <summary>
/// Dynamic plugin entry point for L2.Database.OmgV1 (OMGAUDIO v1 binary parser/writer arrives in M2).
/// </summary>
public sealed class OmgV1DatabasePlugin : IWalkmanPlugin
{
    public string ModuleId => WellKnownModules.L2DatabaseOmgV1;

    public PluginTier Tier => PluginTier.L2Engine;

    public ValueTask InitializeAsync(IPluginRegistrationContext context, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask ShutdownAsync(CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
