using Walkman.Contracts.Devices;
using Walkman.Contracts.Messaging;

namespace Walkman.Contracts.Plugins;

/// <summary>
/// Architectural tier of a dynamically loadable Apollo Manager plugin.
/// </summary>
public enum PluginTier
{
    L2Engine,
    L3Presentation
}

/// <summary>
/// Canonical module identifiers matching manifests/devices.json and plugin assemblies.
/// </summary>
public static class WellKnownModules
{
    public const string L2TransportMsc = "L2.Transport.Msc";
    public const string L2DatabaseOmgV1 = "L2.Database.OmgV1";
    public const string L2CryptoOpenMg = "L2.Crypto.OpenMG";
    public const string L2CodecPipeline = "L2.Codec.Pipeline";
    public const string L2CustomNwmEngine = "L2.Custom.NwmEngine";
    public const string L2TelemetryDumper = "L2.Telemetry.Dumper";

    public const string L3AppShell = "L3.AppShell";
    public const string L3ScreensaverStudio = "L3.ScreensaverStudio";
    public const string L3DumpWizard = "L3.DumpWizard";
}

/// <summary>
/// Registration API handed to an IWalkmanPlugin during initialization inside a scoped ALC.
/// </summary>
public interface IPluginRegistrationContext
{
    string ScopeId { get; }

    IWalkmanEventBus EventBus { get; }

    IWalkmanMediator Mediator { get; }

    void RegisterEngineService<TService>(TService implementation) where TService : class;

    TService? TryResolveEngine<TService>() where TService : class;

    void RegisterCommandHandler<TCommand, TResponse>(ICommandHandler<TCommand, TResponse> handler)
        where TCommand : ICommand<TResponse>;

    void RegisterQueryHandler<TQuery, TResponse>(IQueryHandler<TQuery, TResponse> handler)
        where TQuery : IQuery<TResponse>;

    IDisposable SubscribeEvent<TEvent>(Action<TEvent> handler) where TEvent : class, IEvent;
}

/// <summary>
/// Entry point implemented by every dynamically loaded L2 engine or L3 presentation module.
/// </summary>
public interface IWalkmanPlugin
{
    string ModuleId { get; }

    PluginTier Tier { get; }

    ValueTask InitializeAsync(IPluginRegistrationContext context, CancellationToken ct = default);

    ValueTask ShutdownAsync(CancellationToken ct = default);
}

/// <summary>
/// L1 runtime orchestrator managing collectible AssemblyLoadContext plugin lifecycles.
/// </summary>
public interface IModuleOrchestrator
{
    IReadOnlyCollection<string> LoadedModuleIds { get; }

    Task<IReadOnlyList<string>> HydrateForDeviceAsync(ConnectedDeviceIdentity device, CancellationToken ct = default);

    Task<IReadOnlyList<string>> HydrateForUnrecognizedDeviceAsync(DeviceDetectionProbe probe, CancellationToken ct = default);

    Task LoadModuleAsync(string moduleId, string scopeId, CancellationToken ct = default);

    Task UnloadScopeAsync(string scopeId, CancellationToken ct = default);

    TService? TryResolveEngine<TService>() where TService : class;

    TService ResolveRequiredEngine<TService>() where TService : class;
}
