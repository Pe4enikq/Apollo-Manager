using System.Reflection;
using System.Text.RegularExpressions;
using ATL;
using Avalonia.Media.Imaging;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Messaging;
using Walkman.Contracts.Plugins;
using Walkman.L1.DevicePoller;
using Walkman.L1.FileManager;
using Walkman.L1.Orchestrator.Manifests;
using Walkman.L1.Orchestrator.Messaging;
using Walkman.L1.Orchestrator.Plugins;
using Walkman.L3.AppShell.Core;

namespace Walkman.L3.AppShell.Services;

public sealed partial class WalkmanBackendService : IAsyncDisposable
{
    private const string OfflineToolsScopeId = "offline-tools";
    private const string TelemetryToolsScopeId = "telemetry-tools";

    private readonly string? _codebaseRoot;
    private readonly string _configuration;
    private readonly WalkmanEventBus _eventBus;
    private readonly WalkmanMediator _mediator;
    private readonly ModuleOrchestrator _orchestrator;
    private readonly DeviceManifestRegistry? _manifestRegistry;
    private readonly DevicePollerCoordinator? _poller;
    private readonly FileManagerCoordinator _fileManager;
    private readonly List<DeviceDetectionProbe> _unrecognizedProbes = new();
    private readonly SemaphoreSlim _deviceScopeLock = new(1, 1);

    private string? _mountedFixtureScopeId;
    private bool _offlineToolsLoaded;
    private bool _telemetryToolsLoaded;

    public WalkmanBackendService()
    {
        _codebaseRoot = FindCodebaseRoot(AppContext.BaseDirectory);
        _configuration = typeof(WalkmanBackendService).Assembly
            .GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration ?? "Debug";

        _eventBus = new WalkmanEventBus();
        _eventBus.Subscribe<UnrecognizedSonyDeviceDetectedEvent>(ev =>
        {
            lock (_unrecognizedProbes)
            {
                if (_unrecognizedProbes.All(p =>
                        p.VendorId != ev.Probe.VendorId ||
                        p.ProductId != ev.Probe.ProductId ||
                        !string.Equals(p.VolumeRootPath, ev.Probe.VolumeRootPath, StringComparison.OrdinalIgnoreCase)))
                {
                    _unrecognizedProbes.Add(ev.Probe);
                }
            }
        });
        _mediator = new WalkmanMediator();
        _orchestrator = new ModuleOrchestrator(
            _eventBus,
            _mediator,
            moduleId => ResolvePluginAssemblyPath(AppContext.BaseDirectory, _codebaseRoot, _configuration, moduleId));

        string localManifestPath = Path.Combine(AppContext.BaseDirectory, "manifests", "devices.json");
        string manifestPath = File.Exists(localManifestPath)
            ? localManifestPath
            : (_codebaseRoot is not null
                ? Path.Combine(_codebaseRoot, "manifests", "devices.json")
                : localManifestPath);

        if (File.Exists(manifestPath))
        {
            _manifestRegistry = DeviceManifestRegistry.LoadFromFile(manifestPath);
            _poller = new DevicePollerCoordinator(_manifestRegistry);
        }

        _fileManager = new FileManagerCoordinator(_orchestrator, _eventBus);
    }

    public IWalkmanEventBus EventBus => _eventBus;
    public bool HasManifestRegistry => _manifestRegistry is not null;

    public IReadOnlyList<DeviceDetectionProbe> LastUnrecognizedSonyProbes
    {
        get
        {
            lock (_unrecognizedProbes)
            {
                return _unrecognizedProbes
                    .Where(p => p.VendorId == 0x054C ||
                                p.PresentRootFolders.Any(f => string.Equals(f.TrimEnd('/', '\\'), "OMGAUDIO", StringComparison.OrdinalIgnoreCase)))
                    .ToArray();
            }
        }
    }

    public IReadOnlyList<DeviceDetectionProbe> LastUnrecognizedProbes
    {
        get
        {
            lock (_unrecognizedProbes)
            {
                return _unrecognizedProbes.ToArray();
            }
        }
    }

    public async Task<DiagnosticDumpResult> ExportDiagnosticDumpAsync(
        DeviceDetectionProbe probe,
        string outputZipPath,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(probe);

        if (!_telemetryToolsLoaded)
        {
            await _orchestrator.LoadModuleAsync(WellKnownModules.L2TelemetryDumper, TelemetryToolsScopeId, ct).ConfigureAwait(false);
            await _orchestrator.LoadModuleAsync(WellKnownModules.L3DumpWizard, TelemetryToolsScopeId, ct).ConfigureAwait(false);
            _telemetryToolsLoaded = true;
        }

        var wizard = _orchestrator.TryResolveEngine<IDumpWizardController>();
        if (wizard is not null)
        {
            return await wizard.ExportDiagnosticArchiveAsync(probe, outputZipPath, ct).ConfigureAwait(false);
        }

        var dumper = _orchestrator.ResolveRequiredEngine<ITelemetryDumper>();
        return await dumper.CreateSanitizedDumpAsync(new DiagnosticDumpRequest(probe, outputZipPath), ct).ConfigureAwait(false);
    }

    public async Task EnsureOfflineToolsLoadedAsync(CancellationToken ct = default)
    {
        if (_offlineToolsLoaded)
        {
            return;
        }

        await _orchestrator.LoadModuleAsync(WellKnownModules.L2CryptoOpenMg, OfflineToolsScopeId, ct).ConfigureAwait(false);
        await _orchestrator.LoadModuleAsync(WellKnownModules.L2CodecPipeline, OfflineToolsScopeId, ct).ConfigureAwait(false);
        await _orchestrator.LoadModuleAsync(WellKnownModules.L2CustomNwmEngine, OfflineToolsScopeId, ct).ConfigureAwait(false);
        _offlineToolsLoaded = true;
    }

    public async Task<(IReadOnlyList<WalkmanDeviceSummary> Devices, IReadOnlyList<AudioTrackItem> DeviceTracks)> ScanLiveHardwareAsync(
        CancellationToken ct = default)
    {
        if (_poller is null)
        {
            return (Array.Empty<WalkmanDeviceSummary>(), Array.Empty<AudioTrackItem>());
        }

        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            bool useScsi = OperatingSystem.IsWindows();
            IReadOnlyList<PolledDeviceOutcome> outcomes;
            try
            {
                outcomes = await Task.Run(() => _poller.ScanAndHydrateAsync(_orchestrator, _eventBus, allowScsiPassThrough: useScsi, ct), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                await Task.Delay(350, ct).ConfigureAwait(false);
                outcomes = await Task.Run(() => _poller.ScanAndHydrateAsync(_orchestrator, _eventBus, allowScsiPassThrough: useScsi, ct), ct).ConfigureAwait(false);
            }
            lock (_unrecognizedProbes)
            {
                _unrecognizedProbes.Clear();
                foreach (var o in outcomes)
                {
                    if (!o.Resolution.IsRecognized)
                    {
                        _unrecognizedProbes.Add(o.Probe);
                    }
                }
            }

            return await BuildSummariesFromOutcomesAsync(outcomes, ct).ConfigureAwait(false);
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task<(WalkmanDeviceSummary? Device, IReadOnlyList<AudioTrackItem> DeviceTracks, string? Error)> InspectFixtureVolumeAsync(
        string fixtureRootPath,
        CancellationToken ct = default)
    {
        if (_poller is null)
        {
            return (null, Array.Empty<AudioTrackItem>(), "Device manifest registry is not available.");
        }

        if (string.IsNullOrWhiteSpace(fixtureRootPath) || !Directory.Exists(fixtureRootPath))
        {
            return (null, Array.Empty<AudioTrackItem>(), "Selected directory does not exist.");
        }

        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var outcome = await Task.Run(() => _poller.InspectFixtureVolume(fixtureRootPath), ct).ConfigureAwait(false);
            if (!outcome.Resolution.IsRecognized || outcome.Resolution.Identity is null)
            {
                lock (_unrecognizedProbes)
                {
                    _unrecognizedProbes.Add(outcome.Probe);
                }

                return (null, Array.Empty<AudioTrackItem>(), outcome.Resolution.UnrecognizedReason ?? "Folder is not a recognized Walkman OMGAUDIO volume.");
            }

            var identity = outcome.Resolution.Identity;
            string nextScopeId = $"device:{identity.DeviceInstanceId}";
            if (!string.IsNullOrWhiteSpace(_mountedFixtureScopeId) &&
                !string.Equals(_mountedFixtureScopeId, nextScopeId, StringComparison.OrdinalIgnoreCase))
            {
                await _orchestrator.UnloadScopeAsync(_mountedFixtureScopeId, ct).ConfigureAwait(false);
            }

            _mountedFixtureScopeId = nextScopeId;
            await _orchestrator.HydrateForDeviceAsync(identity, ct).ConfigureAwait(false);
            var (devices, tracks) = await BuildSummariesFromOutcomesAsync([outcome], ct).ConfigureAwait(false);
            return (devices.FirstOrDefault(), tracks, null);
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task SafeEjectDeviceAsync(WalkmanDeviceSummary device, CancellationToken ct = default)
    {
        if (!device.IsLiveHardware || device.LiveIdentity is null)
        {
            return;
        }

        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string scopeId = $"device:{device.LiveIdentity.DeviceInstanceId}";
            if (_poller is not null)
            {
                await _poller.UnloadDeviceSessionAsync(device.LiveIdentity, _orchestrator, _eventBus, ct).ConfigureAwait(false);
            }
            else
            {
                await _orchestrator.UnloadScopeAsync(scopeId, ct).ConfigureAwait(false);
            }

            if (string.Equals(_mountedFixtureScopeId, scopeId, StringComparison.OrdinalIgnoreCase))
            {
                _mountedFixtureScopeId = null;
            }
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task<MagicGateSlotPoolStatus?> RefreshSlotPoolStatusAsync(ConnectedDeviceIdentity identity, CancellationToken ct = default)
    {
        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _fileManager.GetMagicGateSlotPoolStatusAsync(identity, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task<SyncBatchResult> SyncTracksToLiveDeviceAsync(
        ConnectedDeviceIdentity targetDevice,
        IReadOnlyList<SyncTrackRequest> requests,
        CancellationToken ct = default,
        IReadOnlyList<string>? orderedGroupNames = null)
    {
        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _fileManager.SyncTracksToDeviceAsync(
                targetDevice,
                requests,
                ct: ct,
                orderedGroupNames: orderedGroupNames).ConfigureAwait(false);
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task DeleteTracksFromLiveDeviceAsync(
        ConnectedDeviceIdentity device,
        IReadOnlyList<int> trackIds,
        CancellationToken ct = default,
        IProgress<TransferProgressEvent>? progress = null)
    {
        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _fileManager.DeleteTracksAsync(device, trackIds, ct, progress).ConfigureAwait(false);
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task RenameDeviceCollectionAsync(
        ConnectedDeviceIdentity device,
        string oldName,
        string newName,
        CancellationToken ct = default)
    {
        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _fileManager.RenameCollectionAsync(device, oldName, newName, ct).ConfigureAwait(false);
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task DeleteDeviceCollectionAsync(
        ConnectedDeviceIdentity device,
        string collectionName,
        bool deleteTracks = false,
        CancellationToken ct = default,
        IProgress<TransferProgressEvent>? progress = null)
    {
        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _fileManager.DeleteCollectionAsync(device, collectionName, deleteTracks, ct, progress).ConfigureAwait(false);
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task UpdateDeviceTableAsync(
        ConnectedDeviceIdentity device,
        DeviceTableUpdateRequest update,
        CancellationToken ct = default)
    {
        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _fileManager.UpdateDeviceTableAsync(device, update, ct).ConfigureAwait(false);
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task ReorderDeviceCollectionTracksAsync(
        ConnectedDeviceIdentity device,
        string collectionName,
        IReadOnlyList<int> orderedTrackIds,
        CancellationToken ct = default)
    {
        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _fileManager.ReorderCollectionTracksAsync(device, collectionName, orderedTrackIds, ct).ConfigureAwait(false);
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task<Stream?> DecodeOmaTrackForPlaybackAsync(
        AudioTrackItem track,
        uint? hardwareDeviceKey = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track.SourceFilePath) || !File.Exists(track.SourceFilePath))
        {
            return null;
        }

        await EnsureOfflineToolsLoadedAsync(ct).ConfigureAwait(false);
        var decoder = _orchestrator.TryResolveEngine<IAudioStreamDecoder>()
            ?? _orchestrator.TryResolveEngine<ICodecService>() as IAudioStreamDecoder;
        if (decoder is null)
        {
            return null;
        }

        return await decoder.DecodeOmaToPlayableWavStreamAsync(track.SourceFilePath, hardwareDeviceKey, ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> PackScreensaverAsync(NwmProjectDto project, CancellationToken ct = default)
    {
        await EnsureOfflineToolsLoadedAsync(ct).ConfigureAwait(false);
        var packer = _orchestrator.TryResolveEngine<IScreensaverPacker>();
        return packer?.Pack(project);
    }

    public async Task<NwmValidationResult?> ValidateScreensaverAsync(
        NwmProjectDto project,
        ScreenSpecification spec,
        CancellationToken ct = default)
    {
        await EnsureOfflineToolsLoadedAsync(ct).ConfigureAwait(false);
        var packer = _orchestrator.TryResolveEngine<IScreensaverPacker>();
        return packer?.Validate(project, spec);
    }

    public async Task<NwmProjectDto?> UnpackScreensaverAsync(byte[] nwmBytes, CancellationToken ct = default)
    {
        await EnsureOfflineToolsLoadedAsync(ct).ConfigureAwait(false);
        var packer = _orchestrator.TryResolveEngine<IScreensaverPacker>();
        return packer?.Unpack(nwmBytes);
    }

    public async Task<string?> BackupOmgaudioTablesAsync(
        ConnectedDeviceIdentity device,
        string? backupRootDirectory = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(device.VolumeRootPath))
        {
            return null;
        }

        string omgDir = Path.Combine(device.VolumeRootPath, "OMGAUDIO");
        if (!Directory.Exists(omgDir))
        {
            return null;
        }

        string[] datFiles = Directory.GetFiles(omgDir, "*.DAT", SearchOption.TopDirectoryOnly);
        if (datFiles.Length == 0)
        {
            return null;
        }

        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string root = string.IsNullOrWhiteSpace(backupRootDirectory)
                ? Path.Combine(AppSettingsStore.GetDefaultDirectory(), "Backups", $"{SanitizeFileName(device.ResolvedModelCode)}_{DateTime.UtcNow:yyyyMMdd_HHmmss}")
                : backupRootDirectory;
            Directory.CreateDirectory(root);

            foreach (string src in datFiles)
            {
                ct.ThrowIfCancellationRequested();
                string dest = Path.Combine(root, Path.GetFileName(src));
                File.Copy(src, dest, overwrite: true);
            }

            return root;
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task<(int UpdatedCount, string? BackupPath)> CommitDeviceTrackTagsAsync(
        ConnectedDeviceIdentity device,
        IReadOnlyList<TrackMetadataPatch> patches,
        bool backupBeforeWrite = false,
        string? backupRootDirectory = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(patches);

        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? backupPath = backupBeforeWrite
                ? await BackupOmgaudioTablesAsync(device, backupRootDirectory, ct).ConfigureAwait(false)
                : null;
            var db = _orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
            int updated = await db.UpdateTrackMetadataAsync(device, patches, ct).ConfigureAwait(false);
            return (updated, backupPath);
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task<string> CommitDeviceLrcAsync(
        ConnectedDeviceIdentity device,
        int trackId,
        IReadOnlyList<string> lines,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (trackId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(trackId));
        }

        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var transport = _orchestrator.ResolveRequiredEngine<ITransportDriver>();
            string relPath = $"OMGAUDIO/10F{trackId / 256:X2}/1{trackId:X7}.LRC";
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(string.Join(Environment.NewLine, lines));
            await transport.WriteFileAtomicAsync(device, relPath, utf8, ct).ConfigureAwait(false);
            return relPath;
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    public async Task<(bool Success, string? RelativePath, int ByteLength, string? ErrorMessage)> DeployScreensaverToDeviceAsync(
        ConnectedDeviceIdentity device,
        NwmProjectDto project,
        bool backupBeforeWrite = false,
        string? backupRootDirectory = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(project);

        await EnsureOfflineToolsLoadedAsync(ct).ConfigureAwait(false);
        var packer = _orchestrator.TryResolveEngine<IScreensaverPacker>();
        if (packer is null)
        {
            return (false, null, 0, "L2.Custom.NwmEngine plugin unavailable.");
        }

        if (device.Profile.Screen is not null)
        {
            var validation = packer.Validate(project, device.Profile.Screen);
            if (!validation.IsValid)
            {
                return (false, null, 0, string.Join("; ", validation.Errors));
            }
        }

        byte[] nwmBytes = packer.Pack(project);

        await _deviceScopeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (backupBeforeWrite)
            {
                await BackupOmgaudioTablesAsync(device, backupRootDirectory, ct).ConfigureAwait(false);
            }

            var transport = _orchestrator.ResolveRequiredEngine<ITransportDriver>();
            string targetDir = await transport.ExistsAsync(device, "NWWM-SCR", ct).ConfigureAwait(false)
                ? "NWWM-SCR"
                : "SCREEN";
            string fileName = Sanitize8Dot3NwmFileName(project.Name);
            string relPath = $"{targetDir}/{fileName}";
            await transport.WriteFileAtomicAsync(device, relPath, nwmBytes, ct).ConfigureAwait(false);
            return (true, relPath, nwmBytes.Length, null);
        }
        finally
        {
            _deviceScopeLock.Release();
        }
    }

    private static string Sanitize8Dot3NwmFileName(string? name)
    {
        Span<char> buf = stackalloc char[8];
        int len = 0;
        if (!string.IsNullOrWhiteSpace(name))
        {
            foreach (char ch in name.ToUpperInvariant())
            {
                if (len >= 8) break;
                if ((ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch is '_' or '-')
                {
                    buf[len++] = ch;
                }
            }
        }

        string stem = len > 0 ? new string(buf[..len]) : "APOLLO01";
        return $"{stem}.NWM";
    }

    public async Task<TranscodeResult> TranscodeOfflineAudioAsync(
        AudioTrackItem source,
        string outputDirectory,
        TargetCodec targetCodec,
        int bitrateKbps,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(source.SourceFilePath) || !File.Exists(source.SourceFilePath))
        {
            return new TranscodeResult(false, string.Empty, 0, 0, 0, "Sample track has no physical audio file on disk.");
        }

        await EnsureOfflineToolsLoadedAsync(ct).ConfigureAwait(false);
        var codecService = _orchestrator.ResolveRequiredEngine<ICodecService>();
        Directory.CreateDirectory(outputDirectory);

        string safeName = SanitizeFileName(source.Title);
        string ext = ConverterStateLogic.GetOutputExtension(targetCodec);
        string outPath = Path.Combine(outputDirectory, $"{safeName}{ext}");

        if (targetCodec == TargetCodec.Aal)
        {
            return new TranscodeResult(false, string.Empty, 0, 0, 0, "AAL (ATRAC Advanced Lossless) is import/decode-only; no open-source AAL encoder exists.");
        }

        var mappedCodec = targetCodec switch
        {
            TargetCodec.Atrac3Plus => AudioCodecId.Atrac3Plus,
            TargetCodec.Mp3 => AudioCodecId.Mp3,
            TargetCodec.Wav => AudioCodecId.LinearPcm,
            TargetCodec.Flac => AudioCodecId.LinearPcm,
            _ => AudioCodecId.Atrac3Plus
        };

        var tags = new Ea3TagMetadata(
            Title: source.Title,
            Artist: source.Artist,
            Album: source.Album,
            Genre: source.Folder,
            Year: source.Year > 0 ? source.Year : null,
            TrackNumber: null,
            DurationMs: (uint)Math.Max(0, source.DurationSec * 1000));

        var request = new TranscodeRequest(
            SourceFilePath: source.SourceFilePath,
            StagingOutputPath: outPath,
            TargetCodec: mappedCodec,
            TargetBitrateKbps: bitrateKbps,
            EncryptionMode: OmaEncryptionMode.Unprotected_0xFFFF,
            TrackId: 1,
            HardwareDeviceKey: null,
            Tags: tags);

        return await codecService.ProcessToOmaAsync(request, ct).ConfigureAwait(false);
    }

    public sealed record LocalTrackMetadataPatch(
        string Title,
        string Artist,
        string Album,
        int Year,
        int DurationSec,
        string Bitrate,
        string Genre,
        int TrackNumber,
        string AlbumArtist,
        string Composer,
        string Comment,
        int Rating,
        string SortTitle,
        string ArtistLinks,
        IReadOnlyList<string>? SubtitleLines,
        Bitmap? CoverArtwork,
        bool ProtectedLock);

    public async Task<IReadOnlyList<AudioTrackItem>> ScanLocalFolderTracksIncrementalAsync(
        string folderPath,
        string locationId,
        Action<IReadOnlyList<AudioTrackItem>>? onBatchReady = null,
        Action<IReadOnlyList<(AudioTrackItem Track, LocalTrackMetadataPatch Patch)>>? onMetadataBatchReady = null,
        int batchSize = 32,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return Array.Empty<AudioTrackItem>();
        }

        int effectiveBatch = Math.Max(4, batchSize);
        var supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".oma"
        };

        // Phase 1: Fast file discovery (zero ATL.Track or bitmap decoding)
        var discovered = await Task.Run(() =>
        {
            var list = new List<AudioTrackItem>();
            var batch = new List<AudioTrackItem>(effectiveBatch);

            string rootName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrWhiteSpace(rootName))
            {
                rootName = folderPath;
            }

            var dirStack = new Stack<string>();
            dirStack.Push(folderPath);

            while (dirStack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                string currentDir = dirStack.Pop();
                try
                {
                    foreach (string subDir in Directory.EnumerateDirectories(currentDir))
                    {
                        dirStack.Push(subDir);
                    }
                }
                catch
                {
                }

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(currentDir, "*.*", SearchOption.TopDirectoryOnly);
                }
                catch
                {
                    continue;
                }

                string relDir = Path.GetRelativePath(folderPath, currentDir);
                string displayFolder = relDir == "."
                    ? rootName
                    : $"{rootName} / {relDir.Replace(Path.DirectorySeparatorChar, '/').Replace("/", " / ")}";

                foreach (string file in files)
                {
                    ct.ThrowIfCancellationRequested();
                    string ext = Path.GetExtension(file);
                    if (!supportedExts.Contains(ext))
                    {
                        continue;
                    }

                    try
                    {
                        var info = new FileInfo(file);
                        string format = ext.TrimStart('.').ToUpperInvariant();
                        if (format == "OMA")
                        {
                            format = "ATRAC3+";
                        }

                        string title = Path.GetFileNameWithoutExtension(file);
                        var item = new AudioTrackItem
                        {
                            Id = $"local:{file}",
                            Title = title,
                            Artist = "Loading...",
                            Album = displayFolder,
                            Year = 0,
                            Format = format,
                            Bitrate = format,
                            DurationSec = 1,
                            SizeMb = Math.Round(info.Length / 1048576.0, 1),
                            Locations = [locationId, "pc-library"],
                            Folder = displayFolder,
                            CoverGradient = PickGradientForTitle(title),
                            SourceFilePath = file
                        };

                        list.Add(item);
                        batch.Add(item);
                        if (batch.Count >= effectiveBatch)
                        {
                            onBatchReady?.Invoke(batch.ToArray());
                            batch.Clear();
                        }
                    }
                    catch
                    {
                    }
                }
            }

            if (batch.Count > 0)
            {
                onBatchReady?.Invoke(batch.ToArray());
                batch.Clear();
            }

            return list;
        }, ct).ConfigureAwait(false);

        // Phase 2: Lazy background metadata, artwork, LRC, and .OMA lock enrichment
        await Task.Run(() =>
        {
            var folderCoverCache = new Dictionary<string, Bitmap?>(StringComparer.OrdinalIgnoreCase);
            var singleAlbumDirCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            int metaBatchLimit = Math.Min(effectiveBatch, 20);
            var patchBatch = new List<(AudioTrackItem Track, LocalTrackMetadataPatch Patch)>(metaBatchLimit);

            foreach (var item in discovered)
            {
                ct.ThrowIfCancellationRequested();
                string? file = item.SourceFilePath;
                if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
                {
                    continue;
                }

                try
                {
                    var track = new Track(file);
                    string title = !string.IsNullOrWhiteSpace(track.Title)
                        ? track.Title
                        : Path.GetFileNameWithoutExtension(file);
                    string artist = !string.IsNullOrWhiteSpace(track.Artist) ? track.Artist : "Unknown Artist";
                    string album = !string.IsNullOrWhiteSpace(track.Album) ? track.Album : "Local Folder";
                    int year = track.Year ?? 0;
                    int durSec = Math.Max(1, track.Duration);
                    string bitrate = track.Bitrate > 0 ? $"{track.Bitrate} kbps" : item.Format;
                    IReadOnlyList<string>? lrc = TryLoadSidecarLrc(file);
                    Bitmap? coverBitmap = null;

                    var patch = new LocalTrackMetadataPatch(
                        Title: title,
                        Artist: artist,
                        Album: album,
                        Year: year,
                        DurationSec: durSec,
                        Bitrate: bitrate,
                        Genre: track.Genre ?? string.Empty,
                        TrackNumber: track.TrackNumber ?? 0,
                        AlbumArtist: track.AlbumArtist ?? string.Empty,
                        Composer: track.Composer ?? string.Empty,
                        Comment: track.Comment ?? string.Empty,
                        Rating: track.Popularity.HasValue ? Math.Clamp((int)Math.Round(track.Popularity.Value * 5f), 0, 5) : 0,
                        SortTitle: track.AdditionalFields.TryGetValue("TSOT", out string? tsot) ? tsot : string.Empty,
                        ArtistLinks: track.AdditionalFields.TryGetValue("ARTISTLINK", out string? alink) ? alink : string.Empty,
                        SubtitleLines: lrc,
                        CoverArtwork: coverBitmap,
                        ProtectedLock: false);

                    patchBatch.Add((item, patch));
                    if (patchBatch.Count >= metaBatchLimit)
                    {
                        onMetadataBatchReady?.Invoke(patchBatch.ToArray());
                        patchBatch.Clear();
                    }
                }
                catch
                {
                }
            }

            if (patchBatch.Count > 0)
            {
                onMetadataBatchReady?.Invoke(patchBatch.ToArray());
                patchBatch.Clear();
            }
        }, ct).ConfigureAwait(false);

        return discovered;
    }

    public static void ApplyMetadataPatch(AudioTrackItem track, LocalTrackMetadataPatch patch)
    {
        track.Title = patch.Title;
        track.Artist = patch.Artist;
        track.Album = patch.Album;
        track.Year = patch.Year;
        track.DurationSec = patch.DurationSec;
        track.Bitrate = patch.Bitrate;
        track.Genre = patch.Genre;
        track.TrackNumber = patch.TrackNumber;
        track.AlbumArtist = patch.AlbumArtist;
        track.Composer = patch.Composer;
        track.Comment = patch.Comment;
        track.Rating = patch.Rating;
        track.SortTitle = patch.SortTitle;
        track.ArtistLinks = patch.ArtistLinks;
        track.SubtitleLines = patch.SubtitleLines;
        track.CoverArtwork = patch.CoverArtwork;
        track.ProtectedLock = patch.ProtectedLock;
    }

    public IReadOnlyList<AudioTrackItem> ScanLocalFolderTracks(string folderPath, string locationId)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Directory.Exists(folderPath))
        {
            return Array.Empty<AudioTrackItem>();
        }

        var supportedExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".oma"
        };

        var results = new List<AudioTrackItem>();
        var folderCoverCache = new Dictionary<string, Bitmap?>(StringComparer.OrdinalIgnoreCase);
        var singleAlbumDirCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        string rootName = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(rootName))
        {
            rootName = folderPath;
        }

        var dirStack = new Stack<string>();
        dirStack.Push(folderPath);

        while (dirStack.Count > 0)
        {
            string currentDir = dirStack.Pop();
            try
            {
                foreach (string subDir in Directory.EnumerateDirectories(currentDir))
                {
                    dirStack.Push(subDir);
                }
            }
            catch
            {
                // Skip inaccessible subdirectories.
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(currentDir, "*.*", SearchOption.TopDirectoryOnly);
            }
            catch
            {
                continue;
            }

            string relDir = Path.GetRelativePath(folderPath, currentDir);
            string displayFolder = relDir == "."
                ? rootName
                : $"{rootName} / {relDir.Replace(Path.DirectorySeparatorChar, '/').Replace("/", " / ")}";

            foreach (string file in files)
            {
                string ext = Path.GetExtension(file);
                if (!supportedExts.Contains(ext))
                {
                    continue;
                }

                try
                {
                    var info = new FileInfo(file);
                    var track = new Track(file);
                    string format = ext.TrimStart('.').ToUpperInvariant();
                    if (format == "OMA")
                    {
                        format = "ATRAC3+";
                    }

                    string title = !string.IsNullOrWhiteSpace(track.Title)
                        ? track.Title
                        : Path.GetFileNameWithoutExtension(file);
                    string artist = !string.IsNullOrWhiteSpace(track.Artist) ? track.Artist : "Unknown Artist";
                    string album = !string.IsNullOrWhiteSpace(track.Album) ? track.Album : "Local Folder";
                    int year = track.Year ?? 0;
                    int durSec = Math.Max(1, track.Duration);
                    string bitrate = track.Bitrate > 0 ? $"{track.Bitrate} kbps" : format;
                    IReadOnlyList<string>? lrc = TryLoadSidecarLrc(file);
                    Bitmap? coverBitmap = null;

                    results.Add(new AudioTrackItem
                    {
                        Id = $"local:{file}",
                        Title = title,
                        Artist = artist,
                        Album = album,
                        Year = year,
                        Format = format,
                        Bitrate = bitrate,
                        DurationSec = durSec,
                        SizeMb = Math.Round(info.Length / 1048576.0, 1),
                        Locations = [locationId, "pc-library"],
                        Folder = displayFolder,
                        CoverGradient = PickGradientForTitle(title),
                        CoverArtwork = coverBitmap,
                        SubtitleLines = lrc,
                        SourceFilePath = file,
                        Genre = track.Genre ?? string.Empty,
                        TrackNumber = track.TrackNumber ?? 0,
                        AlbumArtist = track.AlbumArtist ?? string.Empty,
                        Composer = track.Composer ?? string.Empty,
                        Comment = track.Comment ?? string.Empty,
                        Rating = track.Popularity.HasValue ? Math.Clamp((int)Math.Round(track.Popularity.Value * 5f), 0, 5) : 0,
                        SortTitle = track.AdditionalFields.TryGetValue("TSOT", out string? tsot) ? tsot : string.Empty,
                        ArtistLinks = track.AdditionalFields.TryGetValue("ARTISTLINK", out string? alink) ? alink : string.Empty
                    });
                }
                catch
                {
                    // Skip unreadable audio files rather than breaking scan.
                }
            }
        }

        return results;
    }

    public static bool TrySaveLocalTrackTags(
        string filePath,
        string? title,
        string? artist,
        string? album,
        int? year = null,
        string? albumArtist = null,
        string? genre = null,
        int? trackNumber = null,
        string? composer = null,
        string? sortTitle = null,
        string? artistLinks = null,
        int? rating = null,
        string? comment = null)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return false;
        }

        try
        {
            var track = new Track(filePath);
            if (title is not null) track.Title = title;
            if (artist is not null) track.Artist = artist;
            if (album is not null) track.Album = album;
            if (year is > 0) track.Year = year.Value;
            if (albumArtist is not null) track.AlbumArtist = albumArtist;
            if (genre is not null) track.Genre = genre;
            if (trackNumber is > 0) track.TrackNumber = trackNumber.Value;
            if (composer is not null) track.Composer = composer;
            if (comment is not null) track.Comment = comment;
            if (rating is >= 0 and <= 5) track.Popularity = rating.Value / 5f;
            if (!string.IsNullOrWhiteSpace(sortTitle)) track.AdditionalFields["TSOT"] = sortTitle;
            if (!string.IsNullOrWhiteSpace(artistLinks)) track.AdditionalFields["ARTISTLINK"] = artistLinks;

            return track.Save();
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySaveLocalLrcFile(string audioFilePath, IReadOnlyList<string> lines)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath))
        {
            return false;
        }

        try
        {
            string lrcPath = Path.ChangeExtension(audioFilePath, ".lrc");
            File.WriteAllLines(lrcPath, lines);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TrySaveLocalTrackCover(string audioFilePath, string imageFilePath, out Bitmap? previewBitmap)
    {
        previewBitmap = null;
        if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath) ||
            string.IsNullOrWhiteSpace(imageFilePath) || !File.Exists(imageFilePath))
        {
            return false;
        }

        try
        {
            byte[] imageBytes = File.ReadAllBytes(imageFilePath);
            if (imageBytes.Length == 0)
            {
                return false;
            }

            var track = new Track(audioFilePath);
            track.EmbeddedPictures.Clear();
            track.EmbeddedPictures.Add(PictureInfo.fromBinaryData(imageBytes, PictureInfo.PIC_TYPE.Front));
            if (!track.Save())
            {
                return false;
            }

            try
            {
                using var ms = new MemoryStream(imageBytes);
                previewBitmap = Bitmap.DecodeToWidth(ms, 96);
            }
            catch
            {
                previewBitmap = null;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool TryRemoveLocalTrackCover(string audioFilePath)
    {
        if (string.IsNullOrWhiteSpace(audioFilePath) || !File.Exists(audioFilePath))
        {
            return false;
        }

        try
        {
            var track = new Track(audioFilePath);
            if (track.EmbeddedPictures.Count == 0)
            {
                return true;
            }

            track.EmbeddedPictures.Clear();
            return track.Save();
        }
        catch
        {
            return false;
        }
    }

    private const int MaxCoverArtCacheEntries = 64;
    private static readonly object CoverArtCacheLock = new();
    private static readonly Dictionary<string, LinkedListNode<(string Path, Bitmap? Artwork)>> CoverArtCacheMap = new(StringComparer.OrdinalIgnoreCase);
    private static readonly LinkedList<(string Path, Bitmap? Artwork)> CoverArtCacheLru = new();

    public static Bitmap? GetOrLoadCoverArt(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return null;
        }

        lock (CoverArtCacheLock)
        {
            if (CoverArtCacheMap.TryGetValue(filePath, out var existing))
            {
                CoverArtCacheLru.Remove(existing);
                CoverArtCacheLru.AddFirst(existing);
                return existing.Value.Artwork;
            }
        }

        Bitmap? decoded = null;
        try
        {
            var track = new Track(filePath);
            if (track.EmbeddedPictures.Count > 0)
            {
                using var ms = new MemoryStream(track.EmbeddedPictures[0].PictureData);
                decoded = Bitmap.DecodeToWidth(ms, 96);
            }
            else
            {
                string? dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    foreach (string name in new[] { "cover.jpg", "cover.png", "folder.jpg", "folder.png", "front.jpg" })
                    {
                        string candidate = Path.Combine(dir, name);
                        if (File.Exists(candidate))
                        {
                            using var fs = File.OpenRead(candidate);
                            decoded = Bitmap.DecodeToWidth(fs, 96);
                            break;
                        }
                    }
                }
            }
        }
        catch
        {
            decoded = null;
        }

        lock (CoverArtCacheLock)
        {
            if (CoverArtCacheMap.TryGetValue(filePath, out var existing))
            {
                CoverArtCacheLru.Remove(existing);
                CoverArtCacheLru.AddFirst(existing);
                return existing.Value.Artwork;
            }

            var node = new LinkedListNode<(string Path, Bitmap? Artwork)>((filePath, decoded));
            CoverArtCacheLru.AddFirst(node);
            CoverArtCacheMap[filePath] = node;

            while (CoverArtCacheLru.Count > MaxCoverArtCacheEntries && CoverArtCacheLru.Last is { } last)
            {
                CoverArtCacheLru.RemoveLast();
                CoverArtCacheMap.Remove(last.Value.Path);
                try { last.Value.Artwork?.Dispose(); } catch { }
            }
        }

        return decoded;
    }

    internal static Bitmap? TryExtractCoverBitmap(
        string filePath,
        Track atlTrack,
        Dictionary<string, Bitmap?> folderCoverCache,
        string? scanRootFolder = null,
        Dictionary<string, bool>? singleAlbumDirCache = null)
    {
        try
        {
            byte[]? picData = atlTrack.EmbeddedPictures?.FirstOrDefault()?.PictureData;
            if (picData is { Length: > 0 })
            {
                using var ms = new MemoryStream(picData);
                return Bitmap.DecodeToWidth(ms, 96);
            }

            string? dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(dir))
            {
                return null;
            }

            // Per-track sidecar (<TrackName>.jpg / .png) is always safe even in mixed folders.
            string stem = Path.GetFileNameWithoutExtension(filePath);
            foreach (string sidecarExt in new[] { ".jpg", ".jpeg", ".png" })
            {
                string sidecar = Path.Combine(dir, stem + sidecarExt);
                if (File.Exists(sidecar))
                {
                    using var fs = File.OpenRead(sidecar);
                    return Bitmap.DecodeToWidth(fs, 96);
                }
            }

            // Never apply folder-level cover.jpg/folder.jpg in the root scan folder or mixed-album directories.
            if (string.IsNullOrWhiteSpace(scanRootFolder) ||
                string.Equals(Path.GetFullPath(dir).TrimEnd('\\', '/'), Path.GetFullPath(scanRootFolder).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                !IsDedicatedSingleAlbumDirectory(dir, singleAlbumDirCache))
            {
                return null;
            }

            if (folderCoverCache.TryGetValue(dir, out Bitmap? cached))
            {
                return cached;
            }

            Bitmap? found = null;
            string[] candidates = ["cover.jpg", "cover.png", "folder.jpg", "folder.png", "front.jpg"];
            foreach (string name in candidates)
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    using var fs = File.OpenRead(candidate);
                    found = Bitmap.DecodeToWidth(fs, 96);
                    break;
                }
            }

            folderCoverCache[dir] = found;
            return found;
        }
        catch
        {
            // Headless unit test / Skia-uninitialized runs skip bitmap decoding safely.
            return null;
        }
    }

    private static bool IsDedicatedSingleAlbumDirectory(string dir, Dictionary<string, bool>? cache)
    {
        if (cache is not null && cache.TryGetValue(dir, out bool cached))
        {
            return cached;
        }

        bool result = false;
        try
        {
            var audioExts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".oma"
            };

            string? sharedAlbum = null;
            int count = 0;
            foreach (string f in Directory.EnumerateFiles(dir, "*.*", SearchOption.TopDirectoryOnly))
            {
                if (!audioExts.Contains(Path.GetExtension(f)))
                {
                    continue;
                }

                count++;
                var t = new Track(f);
                string album = t.Album?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(album))
                {
                    sharedAlbum = null;
                    break;
                }

                if (sharedAlbum is null)
                {
                    sharedAlbum = album;
                }
                else if (!string.Equals(sharedAlbum, album, StringComparison.OrdinalIgnoreCase))
                {
                    sharedAlbum = null;
                    break;
                }
            }

            result = count > 0 && !string.IsNullOrEmpty(sharedAlbum);
        }
        catch
        {
            result = false;
        }

        if (cache is not null)
        {
            cache[dir] = result;
        }

        return result;
    }

    private async Task<(IReadOnlyList<WalkmanDeviceSummary> Devices, IReadOnlyList<AudioTrackItem> DeviceTracks)> BuildSummariesFromOutcomesAsync(
        IReadOnlyList<PolledDeviceOutcome> outcomes,
        CancellationToken ct)
    {
        var devices = new List<WalkmanDeviceSummary>();
        var deviceTracks = new List<AudioTrackItem>();

        foreach (var outcome in outcomes)
        {
            if (!outcome.Resolution.IsRecognized || outcome.Resolution.Identity is null)
            {
                continue;
            }

            var id = outcome.Resolution.Identity;
            var vfs = await _fileManager.MountDeviceVfsSnapshotAsync(id, ct).ConfigureAwait(false);
            var db = _orchestrator.ResolveRequiredEngine<IDatabaseEngine>();
            OmgDatabaseSnapshot snapshot;
            try
            {
                snapshot = await db.ReadDatabaseFastAsync(id, ct).ConfigureAwait(false);
            }
            catch
            {
                // Empty/wiped 0-track players may lack populated 04CNTINF.DAT; mount cleanly with 0 tracks.
                snapshot = new OmgDatabaseSnapshot(Array.Empty<AudioTrackDto>(), Array.Empty<TrackGroupDto>(), 0);
            }

            MagicGateSlotPoolStatus? slotStatus = null;
            try
            {
                slotStatus = await _fileManager.GetMagicGateSlotPoolStatusAsync(
                    id,
                    ct,
                    preloadedSnapshot: snapshot,
                    harvestActiveSlots: false).ConfigureAwait(false);
            }
            catch
            {
                slotStatus = null;
            }

            double totalGb = Math.Round(vfs.SpaceInfo.TotalBytes / 1_073_741_824.0, 2);
            double usedGb = Math.Round((vfs.SpaceInfo.TotalBytes - vfs.SpaceInfo.FreeBytes) / 1_073_741_824.0, 2);
            string screenRes = id.Profile.Screen is not null
                ? $"{id.Profile.Screen.LogicalWidth} × {id.Profile.Screen.LogicalHeight} OLED"
                : "Monochrome LCD";

            string locId = $"live:{id.DeviceInstanceId}";
            string[] groupNames = snapshot.Groups
                .Select(g => g.Title)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Prepend("All Tracks")
                .ToArray();

            devices.Add(new WalkmanDeviceSummary(
                Id: locId,
                Model: id.ResolvedModelCode,
                Year: 2005,
                Series: id.Profile.DisplayName,
                StorageTotalGb: Math.Max(0.01, totalGb),
                StorageUsedGb: Math.Max(0, usedGb),
                BatteryPct: 92,
                SupportedCodecs: id.Profile.Codecs.Select(c => c.Codec.ToString()).ToArray(),
                ScreenResolution: screenRes,
                ScreenTint: id.ResolvedModelCode.StartsWith("NW-A", StringComparison.OrdinalIgnoreCase)
                    ? ScreenTintMode.Amber
                    : ScreenTintMode.Cyan,
                Firmware: "Hardware USB",
                IsLiveHardware: true,
                LiveIdentity: id,
                SlotPoolStatus: slotStatus,
                IsDemoDevice: false,
                TrackCount: snapshot.Tracks.Count,
                GroupNames: groupNames,
                DetectionProbe: outcome.Probe,
                SupportStatus: id.Profile.SupportStatus));

            foreach (var t in snapshot.Tracks)
            {
                string fmt = t.Codec switch
                {
                    AudioCodecId.Atrac3Plus or AudioCodecId.Atrac3PlusLossless => "ATRAC3+",
                    AudioCodecId.Atrac3 or AudioCodecId.Atrac3Lossless => "ATRAC3",
                    AudioCodecId.Mp3 => "MP3",
                    AudioCodecId.Aac => "AAC",
                    AudioCodecId.LinearPcm => "WAV",
                    _ => t.Codec.ToString().ToUpperInvariant()
                };

                string? relPath = id.VolumeRootPath is not null
                    ? Path.Combine(id.VolumeRootPath, "OMGAUDIO", $"10F{t.TrackId / 256:X2}", $"1{t.TrackId:X7}.OMA")
                    : null;

                string? effectiveGroup = !string.IsNullOrWhiteSpace(t.GroupName)
                    ? t.GroupName
                    : (!string.IsNullOrWhiteSpace(t.Album) ? t.Album : null);

                deviceTracks.Add(new AudioTrackItem
                {
                    Id = $"live:{id.DeviceInstanceId}:{t.TrackId}",
                    Title = string.IsNullOrWhiteSpace(t.Title) ? $"Track #{t.TrackId}" : t.Title,
                    Artist = string.IsNullOrWhiteSpace(t.Artist) ? "Unknown Artist" : t.Artist,
                    Album = string.IsNullOrWhiteSpace(t.Album) ? string.Empty : t.Album,
                    Year = t.Year,
                    Format = fmt,
                    Bitrate = $"{t.BitrateKbps} kbps",
                    DurationSec = Math.Max(1, (int)(t.DurationMs / 1000)),
                    SizeMb = Math.Round(t.FileSizeBytes / 1048576.0, 1),
                    Locations = [locId],
                    Folder = effectiveGroup ?? "OMGAUDIO",
                    GroupName = effectiveGroup,
                    Genre = t.Genre,
                    SortTitle = t.SortTitle ?? string.Empty,
                    TrackNumber = t.TrackNumber,
                    SubtitleLines = relPath is not null ? TryLoadSidecarLrc(relPath) : null,
                    CoverGradient = PickGradientForTitle(t.Title),
                    ProtectedLock = t.IsCopyProtected,
                    SourceFilePath = relPath,
                    DeviceTrackId = t.TrackId,
                    DeviceInstanceId = id.DeviceInstanceId
                });
            }
        }

        return (devices, deviceTracks);
    }

    private static IReadOnlyList<string>? TryLoadSidecarLrc(string audioFilePath)
    {
        string lrcPath = Path.ChangeExtension(audioFilePath, ".lrc");
        if (!File.Exists(lrcPath))
        {
            return null;
        }

        var lines = File.ReadAllLines(lrcPath)
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Select(line => line.Trim())
            .ToArray();

        return lines.Length > 0 ? lines : null;
    }

    public static string StripLrcTimestamps(string line) =>
        LrcTimestampRegex().Replace(line, string.Empty).Trim();

    private static (string, string) PickGradientForTitle(string title)
    {
        int hash = Math.Abs(title.GetHashCode(StringComparison.OrdinalIgnoreCase)) % 4;
        return hash switch
        {
            0 => ("#6CC8FF", "#8EE352"),
            1 => ("#7ED4FF", "#D8F3FF"),
            2 => ("#4EB6F7", "#73E2A7"),
            _ => ("#95E858", "#7ACBFF")
        };
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return string.Join("_", name.Split(invalid, StringSplitOptions.RemoveEmptyEntries)).Trim();
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

    internal static string? ResolvePluginAssemblyPath(
        string baseDirectory,
        string? codebaseRoot,
        string configuration,
        string moduleId)
    {
        string assemblyFileName = $"Walkman.{moduleId}.dll";
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

        string candidate = Path.Combine(projectDir, "bin", configuration, "net8.0", assemblyFileName);
        return File.Exists(candidate) ? candidate : null;
    }

    [GeneratedRegex(@"^\[\d{1,2}:\d{2}(?:\.\d{1,3})?\]")]
    private static partial Regex LrcTimestampRegex();

    public async ValueTask DisposeAsync()
    {
        if (_offlineToolsLoaded)
        {
            await _orchestrator.UnloadScopeAsync(OfflineToolsScopeId).ConfigureAwait(false);
        }

        if (_telemetryToolsLoaded)
        {
            await _orchestrator.UnloadScopeAsync(TelemetryToolsScopeId).ConfigureAwait(false);
        }
    }
}
