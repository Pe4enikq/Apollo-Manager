using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;

namespace Walkman.L2.Custom.NwmEngine;

/// <summary>
/// Dynamic plugin entry point for L2.Custom.NwmEngine (.nwm 0xEC01/0xEC02/0xD301 codec).
/// </summary>
public sealed class NwmEnginePlugin : IWalkmanPlugin
{
    public string ModuleId => WellKnownModules.L2CustomNwmEngine;

    public PluginTier Tier => PluginTier.L2Engine;

    public ValueTask InitializeAsync(IPluginRegistrationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.RegisterEngineService<IScreensaverPacker>(new NwmScreensaverPacker());
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
