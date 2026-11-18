using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Messaging;
using Walkman.Contracts.Plugins;

namespace Walkman.L3.DumpWizard;

/// <summary>
/// L3 presentation controller dispatching privacy-safe diagnostic dump requests through the L1 mediator.
/// </summary>
public sealed class DumpWizardController : IDumpWizardController
{
    private readonly IWalkmanMediator _mediator;

    public DumpWizardController(IWalkmanMediator mediator)
    {
        _mediator = mediator ?? throw new ArgumentNullException(nameof(mediator));
    }

    public Task<DiagnosticDumpResult> ExportDiagnosticArchiveAsync(
        DeviceDetectionProbe probe,
        string outputZipPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputZipPath);

        return _mediator.SendAsync(
            new GenerateDiagnosticDumpCommand(new DiagnosticDumpRequest(probe, outputZipPath)),
            ct);
    }
}

/// <summary>
/// Dynamic presentation plugin entry point for L3.DumpWizard.
/// </summary>
public sealed class DumpWizardPlugin : IWalkmanPlugin
{
    private IDisposable? _unrecognizedSub;

    public string ModuleId => WellKnownModules.L3DumpWizard;

    public PluginTier Tier => PluginTier.L3Presentation;

    public DeviceDetectionProbe? LastUnrecognizedProbe { get; private set; }

    public ValueTask InitializeAsync(IPluginRegistrationContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.RegisterEngineService<IDumpWizardController>(new DumpWizardController(context.Mediator));
        _unrecognizedSub = context.SubscribeEvent<UnrecognizedSonyDeviceDetectedEvent>(evt =>
        {
            LastUnrecognizedProbe = evt.Probe;
        });

        return ValueTask.CompletedTask;
    }

    public ValueTask ShutdownAsync(CancellationToken ct = default)
    {
        _unrecognizedSub?.Dispose();
        _unrecognizedSub = null;
        return ValueTask.CompletedTask;
    }
}
