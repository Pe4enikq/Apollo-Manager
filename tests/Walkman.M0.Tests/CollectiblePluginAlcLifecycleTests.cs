using System.Reflection;
using System.Runtime.CompilerServices;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Messaging;
using Walkman.Contracts.Plugins;
using Walkman.L1.Orchestrator.Manifests;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;

namespace Walkman.M0.Tests;

/// <summary>
/// Verifies dynamic Branch B / Branch C plugin hydration into collectible AssemblyLoadContexts
/// and proves zero reference retention prevents memory leaks when a device scope unloads.
/// </summary>
public sealed class CollectiblePluginAlcLifecycleTests
{
    [Fact]
    public async Task HydrateForNwE403_LoadsRequiredPluginsAndCollectsAllAlcsOnUnload()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();
        var manifestPath = Path.Combine(codebaseRoot, "manifests", "devices.json");
        var registry = DeviceManifestRegistry.LoadFromFile(manifestPath);

        var probe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x01FB,
            UsbProductString: "NW-E403",
            VolumeRootPath: @"E:\",
            VolumeSerialNumber: 0x1234ABCD,
            TotalVolumeBytes: 256_000_000,
            HardwareDvIdSegment: 0x0128,
            HardwareDvIdKey: 0xDEADBEEF,
            PresentRootFolders: ["OMGAUDIO", "SCREEN"],
            PresentOmgAudioFiles: ["04CNTINF.DAT"]);

        var resolution = registry.ResolveDevice(probe);
        Assert.True(resolution.IsRecognized);
        Assert.NotNull(resolution.Identity);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var loadedEvents = new List<ModuleLoadedEvent>();
        var unloadedEvents = new List<ModuleUnloadedEvent>();
        var connectedEvents = new List<DeviceConnectedEvent>();

        using var sub1 = bus.Subscribe<ModuleLoadedEvent>(e => loadedEvents.Add(e));
        using var sub2 = bus.Subscribe<ModuleUnloadedEvent>(e => unloadedEvents.Add(e));
        using var sub3 = bus.Subscribe<DeviceConnectedEvent>(e => connectedEvents.Add(e));

        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        var weakRefs = await ExecuteDeviceHydrationAndUnloadAsync(orchestrator, resolution.Identity!);

        Assert.Equal(6, loadedEvents.Count);
        Assert.Equal(6, unloadedEvents.Count);
        Assert.Single(connectedEvents);
        Assert.Empty(orchestrator.LoadedModuleIds);

        ForceGarbageCollectionUntilUnloaded(weakRefs);
        Assert.All(weakRefs, w => Assert.False(w.IsAlive));
    }

    [Fact]
    public async Task HydrateForUnrecognizedDevice_LoadsTelemetryDumperAndDumpWizardAndUnloadsCleanly()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var unrecognizedEvents = new List<UnrecognizedSonyDeviceDetectedEvent>();
        using var sub = bus.Subscribe<UnrecognizedSonyDeviceDetectedEvent>(e => unrecognizedEvents.Add(e));

        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        var unknownProbe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x0999,
            UsbProductString: "Unknown Sony Walkman",
            VolumeRootPath: @"F:\",
            VolumeSerialNumber: 0xCAFE0001,
            TotalVolumeBytes: 1_000_000_000,
            HardwareDvIdSegment: null,
            HardwareDvIdKey: null,
            PresentRootFolders: ["OMGAUDIO"],
            PresentOmgAudioFiles: ["04CNTINF.DAT"]);

        var weakRefs = await ExecuteUnrecognizedHydrationAndUnloadAsync(orchestrator, unknownProbe);

        Assert.Single(unrecognizedEvents);
        Assert.Empty(orchestrator.LoadedModuleIds);

        ForceGarbageCollectionUntilUnloaded(weakRefs);
        Assert.All(weakRefs, w => Assert.False(w.IsAlive));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> ExecuteDeviceHydrationAndUnloadAsync(
        ModuleOrchestrator orchestrator,
        ConnectedDeviceIdentity identity)
    {
        var loaded = await orchestrator.HydrateForDeviceAsync(identity);

        Assert.Contains(WellKnownModules.L2TransportMsc, loaded);
        Assert.Contains(WellKnownModules.L2DatabaseOmgV1, loaded);
        Assert.Contains(WellKnownModules.L2CryptoOpenMg, loaded);
        Assert.Contains(WellKnownModules.L2CodecPipeline, loaded);
        Assert.Contains(WellKnownModules.L2CustomNwmEngine, loaded);
        Assert.Contains(WellKnownModules.L3ScreensaverStudio, loaded);
        Assert.Equal(6, orchestrator.LoadedModuleIds.Count);

        var scopeId = $"device:{identity.DeviceInstanceId}";
        var weakRefs = orchestrator.GetScopeContextWeakReferences(scopeId).ToArray();
        Assert.Equal(6, weakRefs.Length);

        await orchestrator.UnloadScopeAsync(scopeId);
        return weakRefs;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<WeakReference[]> ExecuteUnrecognizedHydrationAndUnloadAsync(
        ModuleOrchestrator orchestrator,
        DeviceDetectionProbe probe)
    {
        var loaded = await orchestrator.HydrateForUnrecognizedDeviceAsync(probe);

        Assert.Equal(
            [WellKnownModules.L2TelemetryDumper, WellKnownModules.L3DumpWizard],
            loaded);

        var weakRefs = orchestrator.GetScopeContextWeakReferences("dump-wizard").ToArray();
        Assert.Equal(2, weakRefs.Length);

        await orchestrator.UnloadScopeAsync("dump-wizard");
        return weakRefs;
    }

    private static void ForceGarbageCollectionUntilUnloaded(WeakReference[] weakRefs)
    {
        for (int attempt = 0; attempt < 20 && weakRefs.Any(w => w.IsAlive); attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }
    }

    private static string DetectBuildConfiguration()
    {
        var attr = typeof(CollectiblePluginAlcLifecycleTests)
            .Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>();

        return string.IsNullOrWhiteSpace(attr?.Configuration) ? "Debug" : attr.Configuration;
    }

    private static string ResolvePluginDllPath(string codebaseRoot, string configuration, string moduleId)
    {
        string relativeProjectDir = moduleId switch
        {
            WellKnownModules.L2TransportMsc => Path.Combine("src", "L2.Engines", "Walkman.L2.Transport.Msc"),
            WellKnownModules.L2DatabaseOmgV1 => Path.Combine("src", "L2.Engines", "Walkman.L2.Database.OmgV1"),
            WellKnownModules.L2CryptoOpenMg => Path.Combine("src", "L2.Engines", "Walkman.L2.Crypto.OpenMG"),
            WellKnownModules.L2CodecPipeline => Path.Combine("src", "L2.Engines", "Walkman.L2.Codec.Pipeline"),
            WellKnownModules.L2CustomNwmEngine => Path.Combine("src", "L2.Engines", "Walkman.L2.Custom.NwmEngine"),
            WellKnownModules.L2TelemetryDumper => Path.Combine("src", "L2.Engines", "Walkman.L2.Telemetry.Dumper"),
            WellKnownModules.L3ScreensaverStudio => Path.Combine("src", "L3.Presentation", "Walkman.L3.ScreensaverStudio"),
            WellKnownModules.L3DumpWizard => Path.Combine("src", "L3.Presentation", "Walkman.L3.DumpWizard"),
            _ => throw new ArgumentOutOfRangeException(nameof(moduleId), moduleId, "Unknown module id.")
        };

        var dllPath = Path.Combine(
            codebaseRoot,
            relativeProjectDir,
            "bin",
            configuration,
            "net8.0",
            $"Walkman.{moduleId}.dll");

        return dllPath;
    }
}
