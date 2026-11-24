using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;

namespace Walkman.L2.Codec.Pipeline;

/// <summary>
/// Dynamic plugin entry point for L2.Codec.Pipeline registering ICodecService.
/// </summary>
public sealed class CodecPipelinePlugin : IWalkmanPlugin
{
    public string ModuleId => WellKnownModules.L2CodecPipeline;

    public PluginTier Tier => PluginTier.L2Engine;

    public ValueTask InitializeAsync(IPluginRegistrationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var service = new CodecPipelineService(() =>
            context.TryResolveEngine<IOpenMgCryptoService>()
            ?? throw new InvalidOperationException("Required IOpenMgCryptoService is not loaded in the active scope."));
        context.RegisterEngineService<ICodecService>(service);
        context.RegisterEngineService<IAudioStreamDecoder>(service);

        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken ct = default)
    {
        LameNativeEncoder.UnloadLibrary();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                NAudio.MediaFoundation.MediaFoundationApi.Shutdown();
            }
            catch
            {
                // Best-effort MediaFoundation shutdown on plugin unload.
            }
        }

        return ValueTask.CompletedTask;
    }
}
