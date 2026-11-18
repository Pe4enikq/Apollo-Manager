using Walkman.Contracts.Engines;
using Walkman.Contracts.Messaging;
using Walkman.Contracts.Plugins;

namespace Walkman.L2.Telemetry.Dumper;

/// <summary>
/// Dynamic plugin entry point for L2.Telemetry.Dumper registering ITelemetryDumper and its mediator command handler.
/// </summary>
public sealed class TelemetryDumperPlugin : IWalkmanPlugin
{
    public string ModuleId => WellKnownModules.L2TelemetryDumper;

    public PluginTier Tier => PluginTier.L2Engine;

    public ValueTask InitializeAsync(IPluginRegistrationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var dumper = new SanitizedTelemetryDumper();
        context.RegisterEngineService<ITelemetryDumper>(dumper);
        context.RegisterCommandHandler<GenerateDiagnosticDumpCommand, DiagnosticDumpResult>(
            new GenerateDiagnosticDumpCommandHandler(dumper));
        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken ct = default) =>
        ValueTask.CompletedTask;
}
