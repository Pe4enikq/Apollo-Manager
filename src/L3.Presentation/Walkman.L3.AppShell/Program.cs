using System.Diagnostics;
using System.Reflection;
using Walkman.Contracts.Plugins;
using Walkman.L1.DevicePoller;
using Walkman.L1.FileManager;
using Walkman.L1.Orchestrator.Manifests;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;

namespace Walkman.L3.AppShell;

/// <summary>
/// M1 read-only host composition root demonstrating live hardware detection, ALC hydration, and VFS mount.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var codebaseRoot = FindCodebaseRoot(AppContext.BaseDirectory);
        var manifestPath = codebaseRoot is not null
            ? Path.Combine(codebaseRoot, "manifests", "devices.json")
            : Path.Combine(AppContext.BaseDirectory, "manifests", "devices.json");

        if (!File.Exists(manifestPath))
        {
            Console.WriteLine($"Apollo Manager L1 Cold Boot OK | Manifest not found at '{manifestPath}'");
            return 0;
        }

        var configuration = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

        var eventBus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            eventBus,
            mediator,
            moduleId => ResolvePluginAssemblyPath(AppContext.BaseDirectory, codebaseRoot, configuration, moduleId));

        var registry = DeviceManifestRegistry.LoadFromFile(manifestPath);
        var poller = new DevicePollerCoordinator(registry);
        var fileManager = new FileManagerCoordinator(orchestrator);

        var sw = Stopwatch.StartNew();
        var outcomes = await poller.ScanAndHydrateAsync(orchestrator, eventBus, allowScsiPassThrough: false);
        sw.Stop();

        if (outcomes.Count == 0)
        {
            Console.WriteLine(
                $"Apollo Manager L1 Cold Boot OK (Branch A Standalone) | Scan: {sw.Elapsed.TotalMilliseconds:F1} ms | Manifest profiles: {registry.Profiles.Count} | Loaded plugins: {fileManager.GetActiveModules().Count}");
            return 0;
        }

        foreach (var outcome in outcomes)
        {
            if (outcome.Resolution.IsRecognized && outcome.Resolution.Identity is not null)
            {
                var id = outcome.Resolution.Identity;
                var vfs = await fileManager.MountDeviceVfsSnapshotAsync(id);
                var dvKeyText = id.HardwareDvIdKey.HasValue ? $"0x{id.HardwareDvIdKey.Value:X8}" : "None";
                var serialText = id.VolumeSerialNumber.HasValue ? $"0x{id.VolumeSerialNumber.Value:X8}" : "None";

                Console.WriteLine($"[Branch B] Detected {id.ResolvedModelCode} ({id.Profile.SeriesId}) in {sw.Elapsed.TotalMilliseconds:F1} ms");
                Console.WriteLine($"  USB VID:PID   : 0x{outcome.Probe.VendorId:X4}:0x{outcome.Probe.ProductId:X4} ({outcome.Probe.UsbProductString ?? "N/A"})");
                Console.WriteLine($"  Volume        : {id.VolumeRootPath} | Serial={serialText} | Total={vfs.SpaceInfo.TotalBytes:N0} B | Free={vfs.SpaceInfo.FreeBytes:N0} B");
                Console.WriteLine($"  DvID Key      : {dvKeyText} (Source: {outcome.DvIdResolution.SourceTier})");
                Console.WriteLine($"  Loaded Plugins: {string.Join(", ", orchestrator.LoadedModuleIds)}");
                Console.WriteLine($"  VFS Mount     : {vfs.DatabaseTableFiles.Count} .DAT tables, {vfs.AudioTrackFiles.Count} .OMA tracks, {vfs.ScreensaverFiles.Count} .nwm ({string.Join(", ", vfs.ScreensaverFiles)})");

                await orchestrator.UnloadScopeAsync($"device:{id.DeviceInstanceId}");
            }
            else
            {
                Console.WriteLine($"[Branch C] Unrecognized volume {outcome.Probe.VolumeRootPath} | Reason: {outcome.Resolution.UnrecognizedReason}");
                await orchestrator.UnloadScopeAsync("dump-wizard");
            }
        }

        return 0;
    }

    private static string? FindCodebaseRoot(string startDirectory)
    {
        var current = new DirectoryInfo(startDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "WalkmanUtility.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        return null;
    }

    private static string? ResolvePluginAssemblyPath(
        string baseDirectory,
        string? codebaseRoot,
        string configuration,
        string moduleId)
    {
        var assemblyFileName = $"Walkman.{moduleId}.dll";
        var directPath = Path.Combine(baseDirectory, assemblyFileName);
        if (File.Exists(directPath))
        {
            return directPath;
        }

        if (codebaseRoot is null)
        {
            return null;
        }

        string? projectDir = moduleId switch
        {
            WellKnownModules.L2TransportMsc => Path.Combine(codebaseRoot, "src", "L2.Engines", "Walkman.L2.Transport.Msc"),
            WellKnownModules.L2DatabaseOmgV1 => Path.Combine(codebaseRoot, "src", "L2.Engines", "Walkman.L2.Database.OmgV1"),
            WellKnownModules.L2CryptoOpenMg => Path.Combine(codebaseRoot, "src", "L2.Engines", "Walkman.L2.Crypto.OpenMG"),
            WellKnownModules.L2CodecPipeline => Path.Combine(codebaseRoot, "src", "L2.Engines", "Walkman.L2.Codec.Pipeline"),
            WellKnownModules.L2CustomNwmEngine => Path.Combine(codebaseRoot, "src", "L2.Engines", "Walkman.L2.Custom.NwmEngine"),
            WellKnownModules.L2TelemetryDumper => Path.Combine(codebaseRoot, "src", "L2.Engines", "Walkman.L2.Telemetry.Dumper"),
            WellKnownModules.L3ScreensaverStudio => Path.Combine(codebaseRoot, "src", "L3.Presentation", "Walkman.L3.ScreensaverStudio"),
            WellKnownModules.L3DumpWizard => Path.Combine(codebaseRoot, "src", "L3.Presentation", "Walkman.L3.DumpWizard"),
            _ => null
        };

        if (projectDir is null)
        {
            return null;
        }

        var candidate = Path.Combine(projectDir, "bin", configuration, "net8.0", assemblyFileName);
        return File.Exists(candidate) ? candidate : null;
    }
}
