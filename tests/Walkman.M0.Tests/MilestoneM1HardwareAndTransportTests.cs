using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;
using Walkman.L1.DevicePoller;
using Walkman.L1.DevicePoller.Probing;
using Walkman.L1.FileManager;
using Walkman.L1.Orchestrator.Manifests;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;

namespace Walkman.M0.Tests;

/// <summary>
/// Milestone M1 verification suite covering DvID key recovery, device detection latency,
/// MSC 8.3/atomic/serial transport safety, and sanitized diagnostic telemetry export.
/// Strictly read-only toward any physical player or test fixture backup.
/// </summary>
public sealed class MilestoneM1HardwareAndTransportTests
{
    [Fact]
    public void DvIdHardwareKeyResolver_RecoversExactHardwareKeyFromRealNwE403OmaHeaders()
    {
        var backupRoot = FindNwE403BackupRoot();
        var resolver = new DvIdHardwareKeyResolver();

        var tier1 = resolver.TryReadDvIdFile(backupRoot);
        Assert.NotNull(tier1);
        Assert.Equal(0x09278120u, tier1.DeviceKey);
        Assert.Equal((ushort)0x0128, tier1.ModelSegment);
        Assert.Equal("Tier1_DvIdFile", tier1.SourceTier);

        var sw = Stopwatch.StartNew();
        var tier2 = resolver.TryRecoverFromExistingOmaHeaders(backupRoot);
        sw.Stop();

        Assert.NotNull(tier2);
        Assert.Equal(0x09278120u, tier2.DeviceKey);
        Assert.Equal("Tier2_OmaKnownPlaintext", tier2.SourceTier);
        Assert.True(sw.ElapsedMilliseconds < 100, $"Tier 2 key recovery took {sw.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task DevicePoller_IdentifiesBackedUpNwE403Volume_Under100ms_AndMountsVfs()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var backupRoot = FindNwE403BackupRoot();
        var configuration = DetectBuildConfiguration();
        var registry = DeviceManifestRegistry.LoadFromFile(Path.Combine(codebaseRoot, "manifests", "devices.json"));

        var poller = new DevicePollerCoordinator(registry);

        var sw = Stopwatch.StartNew();
        var outcome = poller.InspectFixtureVolume(
            backupRoot,
            vendorId: 0x054C,
            productId: 0x01FB,
            usbProductString: "SONY NWWM MEM AAD2 USB Device",
            overrideTotalBytes: 255_492_096,
            overrideVolumeSerial: 0xA091E093);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 100, $"Resolution took {sw.ElapsedMilliseconds} ms (>100 ms gate).");
        Assert.True(outcome.Resolution.IsRecognized);
        Assert.NotNull(outcome.Resolution.Identity);
        Assert.Equal("NW-E403", outcome.Resolution.Identity.ResolvedModelCode);
        Assert.Equal("NW-E400", outcome.Resolution.Identity.Profile.SeriesId);
        Assert.Equal(DatabaseFormat.OmgV1, outcome.Resolution.Identity.Profile.DatabaseFormat);
        Assert.Equal(0x09278120u, outcome.Resolution.Identity.HardwareDvIdKey);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        var fileManager = new FileManagerCoordinator(orchestrator);

        await orchestrator.HydrateForDeviceAsync(outcome.Resolution.Identity);
        try
        {
            var vfs = await fileManager.MountDeviceVfsSnapshotAsync(outcome.Resolution.Identity);
            Assert.Equal(15, vfs.DatabaseTableFiles.Count);
            Assert.Contains(vfs.DatabaseTableFiles, f => f.EndsWith("04CNTINF.DAT", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(vfs.DatabaseTableFiles, f => f.EndsWith("03GINF22.DAT", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(3, vfs.AudioTrackFiles.Count);
            Assert.Contains(vfs.ScreensaverFiles, f => f.Equals("NWWM-SCR/fading_wind.nwm", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            await orchestrator.UnloadScopeAsync($"device:{outcome.Resolution.Identity.DeviceInstanceId}");
        }
    }

    [Fact]
    public async Task MscTransportDriver_Enforces8Dot3Uppercase_AtomicFlush_AndVolumeSerialBinding()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var configuration = DetectBuildConfiguration();
        var registry = DeviceManifestRegistry.LoadFromFile(Path.Combine(codebaseRoot, "manifests", "devices.json"));

        var sandboxRoot = Path.Combine(Path.GetTempPath(), $"apollo_msc_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(sandboxRoot);

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        try
        {
            await orchestrator.LoadModuleAsync(WellKnownModules.L2TransportMsc, "test-msc");
            var transport = orchestrator.ResolveRequiredEngine<ITransportDriver>();

            var e400Profile = registry.Profiles.First(p => p.SeriesId == "NW-E400");
            var mockDevice = new ConnectedDeviceIdentity(
                DeviceInstanceId: "TEST-MOCK-E403",
                Profile: e400Profile,
                ResolvedModelCode: "NW-E403",
                ColorSuffix: null,
                VolumeRootPath: sandboxRoot,
                VolumeSerialNumber: 0xA091E093,
                HardwareDvIdKey: 0x09278120,
                RawDvIdBytes: null);

            byte[] samplePayload = [0x65, 0x61, 0x33, 0x03, 0xAA, 0xBB, 0xCC, 0xDD];
            await transport.WriteFileAtomicAsync(mockDevice, "omgaudio/10f00/10000001.oma", samplePayload);

            Assert.True(await transport.ExistsAsync(mockDevice, "OMGAUDIO/10F00/10000001.OMA"));
            var listed = await transport.ListFilesAsync(mockDevice, "OMGAUDIO/10F00");
            Assert.Single(listed);
            Assert.Equal("OMGAUDIO/10F00/10000001.OMA", listed[0]);

            // Ensure no staging .TMP file lingered after atomic move.
            Assert.Empty(Directory.GetFiles(sandboxRoot, "*.TMP", SearchOption.AllDirectories));

            byte[] readBack = await transport.ReadFileAsync(mockDevice, "OMGAUDIO/10F00/10000001.OMA");
            Assert.Equal(samplePayload, readBack);

            // Guard 2: Traversal and non-8.3 / non-ASCII paths must fail.
            await Assert.ThrowsAsync<ArgumentException>(() =>
                transport.WriteFileAtomicAsync(mockDevice, "../evil.dat", samplePayload));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                transport.WriteFileAtomicAsync(mockDevice, "OMGAUDIO/verylongfilename123.oma", samplePayload));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                transport.WriteFileAtomicAsync(mockDevice, "OMGAUDIO/BAD.LONGEXT", samplePayload));
            await Assert.ThrowsAsync<ArgumentException>(() =>
                transport.WriteFileAtomicAsync(mockDevice, "OMGAUDIO/ТЕСТ.OMA", samplePayload));

            // Guard 1: Volume serial mismatch aborts writes immediately.
            var mismatchedTransport = (ITransportDriver)Activator.CreateInstance(
                transport.GetType(),
                new Func<string, uint?>(_ => 0xDEADBEEFu))!;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                mismatchedTransport.WriteFileAtomicAsync(mockDevice, "OMGAUDIO/10F00/10000002.OMA", samplePayload));

            Assert.False(await transport.ExistsAsync(mockDevice, "OMGAUDIO/10F00/10000002.OMA"));

            await transport.DeleteFileAsync(mockDevice, "OMGAUDIO/10F00/10000001.OMA");
            Assert.False(await transport.ExistsAsync(mockDevice, "OMGAUDIO/10F00/10000001.OMA"));
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("test-msc");
            if (Directory.Exists(sandboxRoot))
            {
                Directory.Delete(sandboxRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task TelemetryDumper_GeneratesSanitizedZipUnder50Kb_WithZeroFullAudioTracks()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var backupRoot = FindNwE403BackupRoot();
        var configuration = DetectBuildConfiguration();
        var outputZip = Path.GetFullPath(Path.Combine(codebaseRoot, "..", "test folder", "NW-E403_Diagnostic_Dump.zip"));

        var bus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            bus,
            mediator,
            moduleId => ResolvePluginDllPath(codebaseRoot, configuration, moduleId));

        var probe = new DeviceDetectionProbe(
            VendorId: 0x054C,
            ProductId: 0x01FB,
            UsbProductString: "SONY NWWM MEM AAD2 USB Device",
            VolumeRootPath: backupRoot,
            VolumeSerialNumber: 0xA091E093,
            TotalVolumeBytes: 255_492_096,
            HardwareDvIdSegment: 0x0128,
            HardwareDvIdKey: 0x09278120,
            PresentRootFolders: ["OMGAUDIO", "NWWM", "NWWM-SCR"],
            PresentOmgAudioFiles: ["00GTRLST.DAT", "04CNTINF.DAT"]);

        await orchestrator.HydrateForUnrecognizedDeviceAsync(probe);
        try
        {
            var dumpWizard = orchestrator.ResolveRequiredEngine<IDumpWizardController>();
            var dumpResult = await dumpWizard.ExportDiagnosticArchiveAsync(probe, outputZip);

            Assert.True(dumpResult.Success);
            Assert.True(File.Exists(outputZip));
            Assert.True(dumpResult.ZipSizeBytes is > 0 and < 50 * 1024,
                $"Diagnostic zip size {dumpResult.ZipSizeBytes} B exceeded 50 KB budget.");

            using var archive = ZipFile.OpenRead(outputZip);
            var entryNames = archive.Entries.Select(e => e.FullName).ToArray();

            Assert.Contains("probe_manifest.json", entryNames);
            Assert.Contains("directory_listing.txt", entryNames);
            Assert.Contains("tables/OMGAUDIO/04CNTINF.DAT", entryNames);
            Assert.Contains("tables/NWWM/FNOTABLE.DAT.head4k", entryNames);

            var omaEntries = archive.Entries
                .Where(e => e.FullName.StartsWith("oma_headers/", StringComparison.Ordinal))
                .ToArray();

            Assert.Equal(3, omaEntries.Length);
            Assert.All(omaEntries, e => Assert.True(e.Length <= 4160));
        }
        finally
        {
            await orchestrator.UnloadScopeAsync("dump-wizard");
        }
    }

    private static string FindNwE403BackupRoot()
    {
        var codebaseRoot = ArchitectureDependencyTests.FindCodebaseRoot();
        var path = Path.GetFullPath(Path.Combine(codebaseRoot, "..", "test folder", "NW-E403_Live_Backup"));
        Assert.True(Directory.Exists(path), $"Expected NW-E403 backup fixture at '{path}'.");
        return path;
    }

    private static string DetectBuildConfiguration()
    {
        var attr = typeof(MilestoneM1HardwareAndTransportTests)
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

        return Path.Combine(
            codebaseRoot,
            relativeProjectDir,
            "bin",
            configuration,
            "net8.0",
            $"Walkman.{moduleId}.dll");
    }
}
