using Walkman.Contracts.Plugins;

namespace Walkman.L3.ScreensaverStudio;

/// <summary>
/// Dynamic presentation plugin entry point for L3.ScreensaverStudio (Avalonia OLED editor arrives in M4).
/// </summary>
public sealed class ScreensaverStudioPlugin : IWalkmanPlugin
{
    public string ModuleId => WellKnownModules.L3ScreensaverStudio;

    public PluginTier Tier => PluginTier.L3Presentation;

    public ValueTask InitializeAsync(IPluginRegistrationContext context, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask ShutdownAsync(CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
