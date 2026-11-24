using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Avalonia;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Plugins;
using Walkman.L1.DevicePoller;
using Walkman.L1.FileManager;
using Walkman.L1.Orchestrator.Manifests;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;

namespace Walkman.L3.AppShell;

/// <summary>
/// L3 Desktop Composition Host &amp; CLI Diagnostics Entry Point.
/// </summary>
internal static partial class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(int dwProcessId);

    [STAThread]
    public static int Main(string[] args)
    {
        bool cliRequested = args.Any(a =>
            a.Equals("--cli", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--verify-standalone", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--m2-live-clone-test", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("--m2-live-delete-track=", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("--live-delete-track=", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("--live-sync-at3p=", StringComparison.OrdinalIgnoreCase));

        bool consoleAttachRequested = cliRequested || args.Any(a =>
            a.Equals("--help", StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith("--capture-screens=", StringComparison.OrdinalIgnoreCase));

        if (consoleAttachRequested && OperatingSystem.IsWindows())
        {
            AttachConsole(AttachParentProcess);
        }

        if (cliRequested)
        {
            return RunCliAsync(args).GetAwaiter().GetResult();
        }

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    private static async Task<int> RunCliAsync(string[] args)
    {
        var codebaseRoot = FindCodebaseRoot(AppContext.BaseDirectory);
        string localManifestPath = Path.Combine(AppContext.BaseDirectory, "manifests", "devices.json");
        var manifestPath = File.Exists(localManifestPath)
            ? localManifestPath
            : (codebaseRoot is not null
                ? Path.Combine(codebaseRoot, "manifests", "devices.json")
                : localManifestPath);

        if (args.Any(a => a.Equals("--verify-standalone", StringComparison.OrdinalIgnoreCase)))
        {
            return await RunStandaloneVerificationAsync(manifestPath, codebaseRoot).ConfigureAwait(false);
        }

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
                $"Apollo Manager L1 Cold Boot OK (Branch A Standalone) | Scan: {sw.Elapsed.TotalMilliseconds:F1} ms | Manifest profiles: {registry.Profiles.Count}");
            return 0;
        }

        bool runLiveCloneTest = args.Contains("--m2-live-clone-test", StringComparer.OrdinalIgnoreCase);
        int? deleteTrackId = ParseIntFlag(args, "--m2-live-delete-track=") ?? ParseIntFlag(args, "--live-delete-track=");
        string? syncAt3pPath = ParseStringFlag(args, "--live-sync-at3p=");
        int syncBitrate = ParseIntFlag(args, "--bitrate=") ?? 352;
        string syncGroup = ParseStringFlag(args, "--group=") ?? "Smtng else";
        int? syncReplaceId = ParseIntFlag(args, "--replace=");

        foreach (var outcome in outcomes)
        {
            if (outcome.Resolution.IsRecognized && outcome.Resolution.Identity is not null)
            {
                var id = outcome.Resolution.Identity;
                var vfs = await fileManager.MountDeviceVfsSnapshotAsync(id);
                var db = orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
                var snapshot = await db.ReadDatabaseAsync(id);
                var slotStatus = await fileManager.GetMagicGateSlotPoolStatusAsync(id);

                var dvKeyText = id.HardwareDvIdKey.HasValue ? $"0x{id.HardwareDvIdKey.Value:X8}" : "None";
                var serialText = id.VolumeSerialNumber.HasValue ? $"0x{id.VolumeSerialNumber.Value:X8}" : "None";
                long usedBytes = vfs.SpaceInfo.TotalBytes - vfs.SpaceInfo.FreeBytes;

                Console.WriteLine($"[Branch B] Detected {id.ResolvedModelCode} ({id.Profile.SeriesId}) in {sw.Elapsed.TotalMilliseconds:F1} ms");
                Console.WriteLine($"  USB VID:PID   : 0x{outcome.Probe.VendorId:X4}:0x{outcome.Probe.ProductId:X4} ({outcome.Probe.UsbProductString ?? "N/A"})");
                Console.WriteLine($"  Storage       : {id.VolumeRootPath} | Serial={serialText} | Total={vfs.SpaceInfo.TotalBytes:N0} B | Used={usedBytes:N0} B | Free={vfs.SpaceInfo.FreeBytes:N0} B");
                Console.WriteLine($"  DvID Key      : {dvKeyText} (Source: {outcome.DvIdResolution.SourceTier})");
                if (slotStatus.HasMaclist0)
                {
                    Console.WriteLine(
                        $"  OpenMG 0x0001 : {slotStatus.TotalSignedSlotsInMaclist0} signed slot(s) ({slotStatus.ActiveSignedTrackCount} active, {slotStatus.RecycledFreeSlotCount} free recycled)");
                }
                if (slotStatus.HardwareIcv is not null)
                {
                    Console.WriteLine(
                        $"  Hardware ICV  : seq=0x{slotStatus.HardwareIcv.SequenceCounter:X}, EKB=0x{slotStatus.HardwareIcv.BoundEkbRevision:X8}, UMAC={Convert.ToHexString(slotStatus.HardwareIcv.Maclist0Umac8).ToLowerInvariant()}");
                }
                if (slotStatus.WindowsIcvBridge is not null)
                {
                    Console.WriteLine($"  Win ICV Bridge: {slotStatus.WindowsIcvBridge.StatusMessage}");
                }
                Console.WriteLine($"  Library       : {snapshot.Tracks.Count} active tracks across {snapshot.Groups.Count} groups | Total Playtime={TimeSpan.FromMilliseconds(snapshot.TotalDurationMs):hh\\:mm\\:ss}");
                Console.WriteLine();
                Console.WriteLine("  ID  | Codec | Kbps |  Time  |   Weight   | Group        | Title — Artist (Album)");
                Console.WriteLine("  ----+-------+------+--------+------------+--------------+----------------------------------------------");

                foreach (var t in snapshot.Tracks)
                {
                    string dur = TimeSpan.FromMilliseconds(t.DurationMs).ToString(@"mm\:ss");
                    string weight = $"{t.FileSizeBytes / 1048576.0:F2} MB";
                    string grp = (t.GroupName ?? "-").PadRight(12);
                    if (grp.Length > 12) grp = grp[..12];
                    Console.WriteLine($"  {t.TrackId,3} | {t.Codec,-5} | {t.BitrateKbps,4} | {dur}  | {weight,10} | {grp} | {t.Title} — {t.Artist} ({t.Album})");
                }

                if (runLiveCloneTest)
                {
                    await ExecuteLiveCloneAndWriteTestAsync(id, snapshot, orchestrator);
                }

                if (deleteTrackId.HasValue)
                {
                    await ExecuteLiveDeleteTestAsync(id, deleteTrackId.Value, fileManager, orchestrator);
                }

                if (syncAt3pPath is not null)
                {
                    Console.WriteLine();
                    Console.WriteLine($"[Live Sync AT3P] Converting and uploading '{syncAt3pPath}' (Bitrate={syncBitrate} kbps, Group='{syncGroup}', ReplaceId={syncReplaceId?.ToString() ?? "auto-recycle"})...");
                    var syncResult = await fileManager.SyncTracksToDeviceAsync(
                        id,
                        [new SyncTrackRequest(syncAt3pPath, AudioCodecId.Atrac3Plus, syncBitrate, syncGroup, ReplaceTrackId: syncReplaceId)]);
                    Console.WriteLine($"[Live Sync AT3P] Succeeded={syncResult.SucceededCount}, Failed={syncResult.FailedCount}");
                    foreach (var added in syncResult.AddedTracks)
                    {
                        Console.WriteLine($"  + Track #{added.TrackId}: {added.Title} ({added.Codec} {added.BitrateKbps} kbps, {added.FileSizeBytes:N0} B)");
                    }
                    foreach (var err in syncResult.Errors)
                    {
                        Console.WriteLine($"  ! Error: {err}");
                    }
                }

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

    private static async Task ExecuteLiveCloneAndWriteTestAsync(
        ConnectedDeviceIdentity device,
        OmgDatabaseSnapshot snapshot,
        IModuleOrchestrator orchestrator)
    {
        var transport = orchestrator.ResolveRequiredEngine<ITransportDriver>();
        var crypto = orchestrator.ResolveRequiredEngine<IOpenMgCryptoService>();
        var db = orchestrator.ResolveRequiredEngine<IDatabaseEngine>();

        if (!device.HardwareDvIdKey.HasValue)
        {
            throw new InvalidOperationException("Cannot write 0xFFFE track without resolved HardwareDvIdKey.");
        }

        var sourceTrack = snapshot.Tracks
            .Where(t => t.Codec == AudioCodecId.Mp3 && t.FileSizeBytes > 0)
            .OrderBy(t => t.FileSizeBytes)
            .First();

        string sourceRelPath = $"OMGAUDIO/10F{sourceTrack.TrackId / 256:X2}/1{sourceTrack.TrackId:X7}.OMA";
        byte[] sourceOmaBytes = await transport.ReadFileAsync(device, sourceRelPath);
        byte[] cleanMp3 = crypto.UnwrapOmaToMp3(sourceOmaBytes, sourceTrack.TrackId, device.HardwareDvIdKey);

        int newTrackId = snapshot.Tracks.Max(t => t.TrackId) + 1;
        if (newTrackId <= 27)
        {
            newTrackId = 28;
        }

        var newTags = new Ea3TagMetadata(
            Title: $"Apollo M2 ({sourceTrack.Title})",
            Artist: sourceTrack.Artist,
            Album: sourceTrack.Album,
            Genre: sourceTrack.Genre,
            Year: sourceTrack.Year > 0 ? sourceTrack.Year : 2026,
            TrackNumber: sourceTrack.TrackNumber > 0 ? sourceTrack.TrackNumber : 1,
            DurationMs: sourceTrack.DurationMs);

        byte[] newOmaBytes = crypto.WrapMp3ToOma(
            cleanMp3,
            newTrackId,
            device.HardwareDvIdKey,
            OmaEncryptionMode.HardwareXor_0xFFFE,
            newTags,
            ea3BlockSize: 1024);

        var parsedNew = crypto.ParseHeader(newOmaBytes);
        string destRelPath = $"OMGAUDIO/10F{newTrackId / 256:X2}/1{newTrackId:X7}.OMA";
        await transport.WriteFileAtomicAsync(device, destRelPath, newOmaBytes);

        var newTrackDto = new AudioTrackDto(
            TrackId: newTrackId,
            Title: newTags.Title,
            Artist: newTags.Artist,
            Album: newTags.Album,
            Genre: newTags.Genre,
            SortTitle: null,
            TrackNumber: newTags.TrackNumber ?? 1,
            Year: newTags.Year ?? 2026,
            DurationMs: parsedNew.DurationMs,
            FrameCount: parsedNew.FrameCount,
            Codec: AudioCodecId.Mp3,
            BitrateKbps: sourceTrack.BitrateKbps,
            EncryptionMode: OmaEncryptionMode.HardwareXor_0xFFFE,
            FilePropertiesBigEndian: ((uint)AudioCodecId.Mp3 << 24) | parsedNew.CodecParams24,
            LsiKeyring48: null,
            FileSizeBytes: newOmaBytes.Length,
            GroupName: sourceTrack.GroupName ?? "Smtng else");

        await db.CommitTransactionAsync(
            device,
            new OmgDatabaseTransaction([newTrackDto], Array.Empty<int>(), Array.Empty<TrackGroupDto>()));

        var verified = await db.ReadDatabaseAsync(device);
        Console.WriteLine();
        Console.WriteLine($"[M2 Live Write OK] Cloned Track #{sourceTrack.TrackId} -> Track #{newTrackId} ({destRelPath}, {newOmaBytes.Length:N0} B)");
        Console.WriteLine($"  Verified active tracks on device: {verified.Tracks.Count} across {verified.Groups.Count} groups.");
    }

    private static async Task ExecuteLiveDeleteTestAsync(
        ConnectedDeviceIdentity device,
        int trackIdToDelete,
        FileManagerCoordinator fileManager,
        IModuleOrchestrator orchestrator)
    {
        var transport = orchestrator.ResolveRequiredEngine<ITransportDriver>();
        var db = orchestrator.ResolveRequiredEngine<IDatabaseEngine>();

        await fileManager.DeleteTracksAsync(device, [trackIdToDelete]);

        var verified = await db.ReadDatabaseAsync(device);
        var space = await transport.GetSpaceInfoAsync(device);
        string relPath = $"OMGAUDIO/10F{trackIdToDelete / 256:X2}/1{trackIdToDelete:X7}.OMA";
        Console.WriteLine();
        Console.WriteLine($"[Live Delete OK] Removed Track #{trackIdToDelete} ({relPath})");
        Console.WriteLine($"  Verified active tracks: {verified.Tracks.Count} across {verified.Groups.Count} groups | Total Playtime={TimeSpan.FromMilliseconds(verified.TotalDurationMs):hh\\:mm\\:ss} | Free={space.FreeBytes:N0} B");
    }

    private static int? ParseIntFlag(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2) is [_, var val] && int.TryParse(val, out int parsed)
            ? parsed
            : null;

    private static string? ParseStringFlag(string[] args, string prefix) =>
        args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            ?.Split('=', 2) is [_, var val] && !string.IsNullOrWhiteSpace(val)
            ? val.Trim('"')
            : null;

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

    private static async Task<int> RunStandaloneVerificationAsync(string manifestPath, string? codebaseRoot)
    {
        int failures = 0;
        string baseDir = AppContext.BaseDirectory;
        Console.WriteLine($"[StandaloneVerify] BaseDir={baseDir}");
        Console.WriteLine($"[StandaloneVerify] CodebaseRoot={(codebaseRoot ?? "<none>")}");

        if (!File.Exists(manifestPath))
        {
            Console.WriteLine($"[FAIL] Missing manifest: {manifestPath}");
            failures++;
        }
        else
        {
            var registry = DeviceManifestRegistry.LoadFromFile(manifestPath);
            Console.WriteLine($"[OK] Manifest loaded: {manifestPath} ({registry.Profiles.Count} profiles)");
        }

        string[] expectedFiles =
        [
            Path.Combine(baseDir, "codecs", "libmp3lame.dll"),
            Path.Combine(baseDir, "codecs", "COPYING.LAME"),
            Path.Combine(baseDir, "tools", "atracdenc", "atracdenc.exe"),
            Path.Combine(baseDir, "tools", "vgmstream", "vgmstream-cli.exe"),
            Path.Combine(baseDir, "tools", "vgmstream", "COPYING.LGPLv2.1"),
            Path.Combine(baseDir, "LICENSE"),
            Path.Combine(baseDir, "THIRD_PARTY_NOTICES.md")
        ];
        foreach (string expected in expectedFiles)
        {
            if (File.Exists(expected))
            {
                Console.WriteLine($"[OK] Asset present: {Path.GetRelativePath(baseDir, expected)}");
            }
            else
            {
                Console.WriteLine($"[FAIL] Missing asset: {expected}");
                failures++;
            }
        }

        var configuration = typeof(Program).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Release";
        var eventBus = new WalkmanEventBus();
        var mediator = new WalkmanMediator();
        var orchestrator = new ModuleOrchestrator(
            eventBus,
            mediator,
            moduleId => ResolvePluginAssemblyPath(baseDir, codebaseRoot, configuration, moduleId));

        string[] allModules =
        [
            WellKnownModules.L2TransportMsc,
            WellKnownModules.L2CryptoOpenMg,
            WellKnownModules.L2DatabaseOmgV1,
            WellKnownModules.L2CodecPipeline,
            WellKnownModules.L2CustomNwmEngine,
            WellKnownModules.L2TelemetryDumper,
            WellKnownModules.L3ScreensaverStudio,
            WellKnownModules.L3DumpWizard
        ];

        const string scopeId = "standalone-verify";
        foreach (string moduleId in allModules)
        {
            try
            {
                await orchestrator.LoadModuleAsync(moduleId, scopeId).ConfigureAwait(false);
                Console.WriteLine($"[OK] Plugin loaded: {moduleId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[FAIL] Plugin {moduleId}: {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
        }

        await orchestrator.UnloadScopeAsync(scopeId).ConfigureAwait(false);
        Console.WriteLine($"[StandaloneVerify] Result={(failures == 0 ? "PASS" : $"FAIL ({failures} errors)")}");
        return failures == 0 ? 0 : 1;
    }

    private static string? ResolvePluginAssemblyPath(
        string baseDirectory,
        string? codebaseRoot,
        string configuration,
        string moduleId)
    {
        var assemblyFileName = $"Walkman.{moduleId}.dll";
        string[] packagedCandidates =
        [
            Path.Combine(baseDirectory, "plugins", $"Walkman.{moduleId}", assemblyFileName),
            Path.Combine(baseDirectory, "plugins", moduleId, assemblyFileName),
            Path.Combine(baseDirectory, "plugins", assemblyFileName),
            Path.Combine(baseDirectory, assemblyFileName)
        ];

        foreach (string packagedPath in packagedCandidates)
        {
            if (File.Exists(packagedPath))
            {
                return packagedPath;
            }
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
