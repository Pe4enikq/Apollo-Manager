using Walkman.Contracts.Plugins;

namespace Walkman.L2.Codec.Pipeline;

/// <summary>
/// Dynamic plugin entry point for L2.Codec.Pipeline (MP3 pass-through, LAME, atracdenc, vgmstream arrive in M3).
/// </summary>
public sealed class CodecPipelinePlugin : IWalkmanPlugin
{
    public string ModuleId => WellKnownModules.L2CodecPipeline;

    public PluginTier Tier => PluginTier.L2Engine;

    public ValueTask InitializeAsync(IPluginRegistrationContext context, CancellationToken ct = default) =>
        ValueTask.CompletedTask;

    public ValueTask ShutdownAsync(CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
