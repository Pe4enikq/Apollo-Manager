using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.Services;

namespace Walkman.L3.AppShell.ViewModels;

public sealed record DumpProbeOption(
    string Id,
    string Label,
    string Detail,
    DeviceDetectionProbe Probe)
{
    public string VendorHex => $"0x{Probe.VendorId:X4}";
    public string ProductHex => $"0x{Probe.ProductId:X4}";
    public string RootPathDisplay => Probe.VolumeRootPath ?? "Virtual / SCSI Probe";
}

public sealed record TechInfoRow(
    string Label,
    string Value,
    string Category);

public sealed partial class MainWindowViewModel
{
    private readonly bool _suppressSettingsSave;
    private readonly string? _settingsDirectoryOverride;
    private bool _isInitializing = true;

    [ObservableProperty] private bool _isDemoModeEnabled;
    [ObservableProperty] private bool _enableColdStartIntro = true;
    [ObservableProperty] private bool _useSidebarRail;
    [ObservableProperty] private double _playerVolume = 0.85;
    [ObservableProperty] private bool _isPlayerMuted;
    [ObservableProperty] private ConfirmDialogState? _activeConfirmDialog;

    [ObservableProperty] private string _activeDeviceTab = "pair";
    [ObservableProperty] private string _fmTargetGroupName = "Apollo Transfer";
    [ObservableProperty] private string _customTrackSearch = string.Empty;
    [ObservableProperty] private TransferLocationItem? _customSelectedLocation;
    [ObservableProperty] private AudioTrackItem? _selectedSubtitleTrack;
    [ObservableProperty] private AudioTrackItem? _selectedTagTrack;
    [ObservableProperty] private string _tagEditYear = "2003";
    [ObservableProperty] private bool _autoSyncLyricsStub;
    [ObservableProperty] private bool _autoLookupTagsStub;

    [ObservableProperty] private string _fwPackagePath = "No firmware package selected (.bin / .fw)";
    [ObservableProperty] private bool _fwPatchRegionUnlock;
    [ObservableProperty] private bool _fwPatchVolumeCap;
    [ObservableProperty] private DumpProbeOption? _selectedDumpProbe;
    [ObservableProperty] private string _dumpWizardStatusText = "Select a connected or unrecognized Sony USB device to export telemetry.";
    [ObservableProperty] private int _allocateSlotCount = 10;
    [ObservableProperty] private string _allocateSlotCountText = "10";
    [ObservableProperty] private bool _isOpenMgRuntimePromptOpen;
    [ObservableProperty] private string _openMgRuntimePromptMessage = string.Empty;
    [ObservableProperty] private bool _isAllocatingSlots;
    private int _pendingSlotAllocationCount;
    private bool _syncingAllocateSlotCount;

    partial void OnAllocateSlotCountChanged(int value)
    {
        if (_syncingAllocateSlotCount)
        {
            return;
        }

        _syncingAllocateSlotCount = true;
        try
        {
            int clamped = Math.Clamp(value, 1, 512);
            if (clamped != value)
            {
                AllocateSlotCount = clamped;
            }

            AllocateSlotCountText = clamped.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _syncingAllocateSlotCount = false;
        }
    }

    partial void OnAllocateSlotCountTextChanged(string value)
    {
        if (_syncingAllocateSlotCount)
        {
            return;
        }

        if (int.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            _syncingAllocateSlotCount = true;
            try
            {
                int clamped = Math.Clamp(parsed, 1, 512);
                AllocateSlotCount = clamped;
                if (clamped != parsed)
                {
                    AllocateSlotCountText = clamped.ToString(CultureInfo.InvariantCulture);
                }
            }
            finally
            {
                _syncingAllocateSlotCount = false;
            }
        }
    }

    public string OpenMgSlotAllocationWarningText =>
        "Coming Soon · WIP (v0.2.0b): Direct new 0x0001 ATRAC3+ slot minting is gated in v0.1.1b. Existing pre-signed slots can still be recycled automatically by deleting tracks, or use MP3 (0xFFFE).";

    public bool CanAllocateAtrac3PlusSlots => false;

    [RelayCommand]
    private async Task AllocateAtrac3PlusSlotsAsync()
    {
        ShowToast(
            "Coming Soon · WIP (v0.2.0b)",
            "Direct 0x0001 slot minting is gated in v0.1.1b. Delete an existing ATRAC3+ track to recycle its slot, or use MP3 mode.",
            ToastSeverity.Info);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task LocateOpenMgInstallerAsync()
    {
        if (PickOpenGenericFileAsyncHandler is null)
        {
            ShowToast("Installer Picker Unavailable", "Select and run your Sony OpenMG / SonicStage installer manually, then retry.", ToastSeverity.Info);
            return;
        }

        string? installerPath = await PickOpenGenericFileAsyncHandler(
            "Locate Sony OpenMG / SonicStage Installer",
            "Windows Installer (*.exe;*.msi)",
            "*.exe;*.msi");
        if (string.IsNullOrWhiteSpace(installerPath) || !File.Exists(installerPath))
        {
            return;
        }

        try
        {
            using var proc = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(installerPath)
            {
                UseShellExecute = true
            });
            if (proc is not null)
            {
                await proc.WaitForExitAsync();
            }

            var cap = _backend.ProbeOpenMgIcvBridgeCapability();
            if (cap is { IsHostRuntimePresent: true })
            {
                IsOpenMgRuntimePromptOpen = false;
                ShowToast("OpenMG Runtime Detected", "Host Sony OpenMG Secure Module detected. Resuming slot allocation...", ToastSeverity.Success);
                if (_pendingSlotAllocationCount > 0)
                {
                    AllocateSlotCount = _pendingSlotAllocationCount;
                    _pendingSlotAllocationCount = 0;
                    await AllocateAtrac3PlusSlotsAsync();
                }
            }
            else
            {
                ShowToast("OpenMG Still Not Found", "hdwmcom.dll was not detected after installer exit. You can retry or switch to MP3 Mode.", ToastSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowToast("Installer Launch Failed", ex.Message, ToastSeverity.Error);
        }
    }

    [RelayCommand]
    private void OpenOpenMgHelpGuide()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://archive.org/details/sony-sonicstage-4.3-ultimate")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            // Ignore shell open failure on headless test hosts.
        }

        ShowToast(
            "OpenMG Secure Module Guide",
            "Install Sony OpenMG Secure Module (included with SonicStage 4.3 Ultimate) so Apollo can mint new 0x0001 slots via Windows COM, or switch to MP3 Mode for zero-dependency transfers.",
            ToastSeverity.Info,
            durationSeconds: 8.0);
    }

    [RelayCommand]
    private void SwitchToMp3ModeFromOpenMgPrompt()
    {
        IsOpenMgRuntimePromptOpen = false;
        _pendingSlotAllocationCount = 0;
        FmUploadMode = UploadMode.Auto;
        ShowToast("Switched to MP3 Mode", "MP3 (0xFFFE) mode active — transfers work immediately with zero Sony software.", ToastSeverity.Success);
    }

    [RelayCommand]
    private void SetAllocateSlotCount(string countStr)
    {
        if (int.TryParse(countStr, out int count))
        {
            AllocateSlotCount = Math.Clamp(count, 1, 512);
        }
    }

    [RelayCommand]
    private void AdjustAllocateSlotCount(string deltaStr)
    {
        if (int.TryParse(deltaStr, out int delta))
        {
            AllocateSlotCount = Math.Clamp(AllocateSlotCount + delta, 1, 512);
        }
    }

    [RelayCommand]
    private void DismissOpenMgRuntimePrompt()
    {
        IsOpenMgRuntimePromptOpen = false;
        _pendingSlotAllocationCount = 0;
    }

    public Func<string, string, string, Task<string?>>? PickOpenGenericFileAsyncHandler { get; set; }
    public Func<string, string, string, Task<string?>>? PickSaveGenericFileAsyncHandler { get; set; }
    public event Action<string?>? FocusSearchRequested;

    public ObservableCollection<ToastItem> Toasts { get; } = new();
    public ObservableCollection<string> FmAvailableGroups { get; } = new(["Apollo Transfer", "Favorites", "Electronic", "Soundtracks"]);
    public ObservableCollection<AudioTrackItem> CustomizationTracks { get; } = new();
    public ObservableCollection<DumpProbeOption> AvailableDumpProbes { get; } = new();
    public ObservableCollection<TechInfoRow> TechnicalInfoRows { get; } = new();

    public bool HasActiveBottomDock => HasTransferJob || HasPlayerTrack;
    public bool HasConfirmDialog => ActiveConfirmDialog is not null;
    public bool HasToasts => Toasts.Count > 0;
    public Bitmap? PlayerCoverBitmap => PlayerTrack?.CoverArtwork;
    public bool PlayerHasCoverBitmap => PlayerTrack?.CoverArtwork is not null;
    public string DemoModeStatusLabel => IsDemoModeEnabled ? "ON · Synthetic Preview Active" : "OFF · Live Disk & Hardware Only";
    public string NavStyleLabel => UseSidebarRail ? "Compact Sidebar Rail" : "Radial Edge Jog Dial";

    // Device & System Tab Flags
    public bool IsDeviceSystemActive => ActiveModuleId is ModuleIds.DeviceSystem or ModuleIds.PairSync or ModuleIds.CustomFirmware or ModuleIds.DeviceDumpWizard or ModuleIds.DeviceTechInfo;
    public bool IsDevicePairTab => ActiveDeviceTab == "pair";
    public bool IsDeviceFirmwareTab => ActiveDeviceTab == "firmware";
    public bool IsDeviceDumpTab => ActiveDeviceTab == "dump";
    public bool IsDeviceTechTab => ActiveDeviceTab == "tech";
    public bool IsFwReadyDevice => ConnectedDevice is not null;
    public bool IsFwReadyBattery => (ConnectedDevice?.BatteryPct ?? 0) >= 40;
    public string FirmwareDeviceReadinessText => ConnectedDevice is not null ? $"{ConnectedDevice.Model} Linked" : "Waiting USB";
    public string FirmwareBatteryReadinessText => ConnectedDevice is not null ? $"{ConnectedDevice.BatteryPct}% OK" : "—";
    public bool CanStartDump => SelectedDumpProbe is not null;

    public double DestProjectedTotalPercent => Math.Clamp(DestBaseUsedPercent + DestProjectedAdditionalPercent, 0, 100);

    // Projected Storage Bar & Destination Multi-Select Deletion (File Manager)
    public double DestBaseUsedPercent => DestDeviceSummary is { StorageTotalGb: > 0 } dev
        ? Math.Clamp((dev.StorageUsedGb / dev.StorageTotalGb) * 100.0, 0, 100)
        : 0;

    public double DestSelectedTransferGb => GetUnfilteredFileManagerSourceTracks().Where(t => t.IsSelected).Sum(EstimateTransferTrackSizeMb) / 1024.0;

    public double EstimateTransferTrackSizeMb(AudioTrackItem track)
    {
        double dur = Math.Max(1, track.DurationSec);
        return FmUploadMode switch
        {
            UploadMode.Quality => ConverterStateLogic.EstimateConvertedSizeMb(dur, TargetCodec.Atrac3Plus, BitrateOption.K352),
            UploadMode.Faster => ConverterStateLogic.EstimateConvertedSizeMb(dur, TargetCodec.Atrac3Plus, BitrateOption.K192),
            UploadMode.Auto => string.Equals(track.Format, "MP3", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(track.Format, "ATRAC3+", StringComparison.OrdinalIgnoreCase) ||
                               string.Equals(track.Format, "ATRAC3", StringComparison.OrdinalIgnoreCase)
                ? track.SizeMb
                : ConverterStateLogic.EstimateConvertedSizeMb(dur, TargetCodec.Mp3, BitrateOption.K192),
            UploadMode.Custom => FmCustomCodec.Codec switch
            {
                AudioCodecId.LinearPcm => ConverterStateLogic.EstimateConvertedSizeMb(dur, TargetCodec.Wav, BitrateOption.Lossless),
                AudioCodecId.Atrac3PlusLossless or AudioCodecId.Atrac3Lossless => ConverterStateLogic.EstimateConvertedSizeMb(dur, TargetCodec.Aal, BitrateOption.Lossless),
                _ => Math.Round(Math.Max(0.4, (dur * FmCustomCodec.BitrateKbps) / 8.0 / 1024.0), 1)
            },
            _ => track.SizeMb
        };
    }

    public double DestProjectedAdditionalPercent => DestDeviceSummary is { StorageTotalGb: > 0 } dev
        ? Math.Clamp((DestSelectedTransferGb / dev.StorageTotalGb) * 100.0, 0, Math.Max(0, 100.0 - DestBaseUsedPercent))
        : 0;

    public bool IsTransferOverCapacity => DestDeviceSummary is { StorageTotalGb: > 0 } dev
        && (dev.StorageUsedGb + DestSelectedTransferGb) > dev.StorageTotalGb;

    public string DestProjectedStorageText => DestDeviceSummary is null
        ? string.Empty
        : DestSelectedTransferGb > 0.001
            ? string.Create(CultureInfo.InvariantCulture, $"{DestDeviceSummary.StorageUsedGb + DestSelectedTransferGb:F2} / {DestDeviceSummary.StorageTotalGb:F1} GB projected (+{DestSelectedTransferGb * 1024.0:F0} MB)")
            : DestDeviceSummary.StorageFormatted;

    public int SelectedDestTrackCount => FileManagerDestTracks.Count(t => t.IsDestSelected);
    public bool HasSelectedDestTracks => SelectedDestTrackCount > 0;
    public string DeleteDestButtonLabel => HasSelectedDestTracks ? $"Delete ({SelectedDestTrackCount})" : "Delete Selected";

    // Customization Batch Tags & Selection
    public int TagBatchSelectedCount => CustomizationTracks.Count(t => t.IsTagSelected);
    public string TagSaveButtonLabel => TagBatchSelectedCount > 1 ? $"Save {TagBatchSelectedCount} Tagged" : "Save Local Tags";

    // All Features Live Summary Cards
    public string SummaryHardwareTitle => ConnectedDevice is not null
        ? $"{ConnectedDevice.Model} ({ConnectedDevice.ModeBadge})"
        : "No Player Linked";
    public string SummaryHardwareDetail => ConnectedDevice is not null
        ? $"{ConnectedDevice.StorageFormatted} · Battery {ConnectedDevice.BatteryPct}% · {ConnectedDevice.TrackCount} tracks"
        : (IsDemoModeEnabled ? "Pair a demo profile or plug USB player" : "Waiting for USB player storage");
    public string SummaryLibraryTitle => $"{_allTracks.Count(t => !t.IsDemoItem)} Local · {_allTracks.Count(t => t.IsDemoItem)} Demo";
    public string SummaryLibraryDetail => $"Root: {Path.GetFileName(DefaultMusicFolder)} · LRC & ID3/OMA indexed";
    public string SummaryEngineTitle => IsTransferRunning ? (TransferJob?.StageLabel ?? "Processing...") : "L2 Codec & Crypto Ready";
    public string SummaryEngineDetail => "ATRAC3+ · LAME MP3 · AAL (Decode) · FLAC · OpenMG 0x0001";

    public void RequestFocusSearch() => FocusSearchRequested?.Invoke(ActiveModuleId);

    public void ShowToast(string title, string message, ToastSeverity severity = ToastSeverity.Info, double durationSeconds = 4.5)
    {
        var toast = new ToastItem(Guid.NewGuid().ToString("N"), title, message, severity);
        Toasts.Insert(0, toast);
        while (Toasts.Count > 4)
        {
            Toasts.RemoveAt(Toasts.Count - 1);
        }

        OnPropertyChanged(nameof(HasToasts));

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(2.0, durationSeconds)) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DismissToast(toast.Id);
        };
        timer.Start();
    }

    [RelayCommand]
    private void DismissToast(string? id)
    {
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var found = Toasts.FirstOrDefault(t => t.Id == id);
        if (found is not null)
        {
            Toasts.Remove(found);
            OnPropertyChanged(nameof(HasToasts));
        }
    }

    public void RequestConfirmation(string title, string message, string confirmLabel, bool isDestructive, Action onConfirm)
    {
        ActiveConfirmDialog = new ConfirmDialogState(title, message, confirmLabel, isDestructive, onConfirm);
    }

    [RelayCommand]
    private void ConfirmActiveDialog()
    {
        var dlg = ActiveConfirmDialog;
        ActiveConfirmDialog = null;
        dlg?.OnConfirm();
    }

    [RelayCommand]
    private void CancelActiveDialog()
    {
        ActiveConfirmDialog = null;
    }

    [RelayCommand]
    private void ToggleMute() => IsPlayerMuted = !IsPlayerMuted;

    [RelayCommand]
    private void ToggleDemoMode() => IsDemoModeEnabled = !IsDemoModeEnabled;

    [RelayCommand]
    private void ToggleColdStartIntro() => EnableColdStartIntro = !EnableColdStartIntro;

    [RelayCommand]
    private void ToggleNavigationStyle() => UseSidebarRail = !UseSidebarRail;

    [RelayCommand]
    private void ToggleSelectAllFmSource()
    {
        if (FileManagerSourceTracks.Count == 0)
        {
            return;
        }

        bool selectAll = FileManagerSourceTracks.Any(t => !t.IsSelected);
        foreach (var t in FileManagerSourceTracks)
        {
            t.IsSelected = selectAll;
        }

        NotifySelectionChanged();
    }

    [RelayCommand]
    private void ToggleSelectAllFmDest()
    {
        if (FileManagerDestTracks.Count == 0)
        {
            return;
        }

        bool selectAll = FileManagerDestTracks.Any(t => !t.IsDestSelected);
        foreach (var t in FileManagerDestTracks)
        {
            t.IsDestSelected = selectAll;
        }

        NotifyDestSelectionChanged();
    }

    [RelayCommand]
    private void ToggleSelectAllConvSource()
    {
        if (ConverterSourceTracks.Count == 0)
        {
            return;
        }

        bool selectAll = ConverterSourceTracks.Any(t => !t.IsSelected);
        foreach (var t in ConverterSourceTracks)
        {
            t.IsSelected = selectAll;
        }

        NotifySelectionChanged();
    }

    [RelayCommand]
    private void RequestDeleteSelectedDestTracks()
    {
        var selected = FileManagerDestTracks.Where(t => t.IsDestSelected).ToList();
        var destDev = DestDeviceSummary;
        if (selected.Count == 0 || destDev is null)
        {
            ShowToast("Nothing Selected", "Check one or more destination tracks before deleting.", ToastSeverity.Warning);
            return;
        }

        RequestConfirmation(
            $"Delete {selected.Count} Track(s)?",
            $"Permanently remove {selected.Count} selected track(s) from {destDev.Model}? Free MagicGate/storage slots will be reclaimed.",
            $"Delete ({selected.Count})",
            isDestructive: true,
            () => _ = ExecuteDeleteDestTracksAsync(destDev, selected));
    }

    private async Task ExecuteDeleteDestTracksAsync(WalkmanDeviceSummary destDev, List<AudioTrackItem> selected)
    {
        try
        {
            TransferJob = new ActiveTransferJob(
                Guid.NewGuid().ToString("N"),
                $"Removing {selected.Count} track(s)...",
                $"Deleting from {destDev.Model}",
                "OMGAUDIO Delete",
                0,
                selected.Count,
                25,
                Math.Max(1, selected.Count),
                false,
                IsDeleteJob: true);

            if (destDev.IsLiveHardware && destDev.LiveIdentity is not null)
            {
                int[] ids = selected.Where(t => t.DeviceTrackId.HasValue).Select(t => t.DeviceTrackId!.Value).ToArray();
                if (ids.Length > 0)
                {
                    var progress = CreateDeleteProgressReporter(destDev.Model, ids.Length);
                    await _backend.DeleteTracksFromLiveDeviceAsync(destDev.LiveIdentity, ids, progress: progress);
                }

                await RescanUsbHardwareAsync();
            }
            else
            {
                foreach (var trk in selected)
                {
                    trk.IsDestSelected = false;
                    trk.Locations.Remove(destDev.Id);
                    if (trk.Locations.Count == 0)
                    {
                        _allTracks.Remove(trk);
                    }
                }

                RefreshAllLists();
            }

            TransferJob = new ActiveTransferJob(
                Guid.NewGuid().ToString("N"),
                $"Deleted {selected.Count} track(s)",
                $"Removed from {destDev.Model}",
                "OMGAUDIO Delete",
                selected.Count,
                selected.Count,
                100,
                0,
                true,
                IsDeleteJob: true);

            ShowToast("Tracks Removed", $"Removed {selected.Count} track(s) from {destDev.Model}.", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            TransferJob = null;
            ShowToast("Deletion Failed", ex.Message, ToastSeverity.Error);
        }
    }

    public void NotifyDestSelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedDestTrackCount));
        OnPropertyChanged(nameof(HasSelectedDestTracks));
        OnPropertyChanged(nameof(CanDeleteSelectedDest));
        OnPropertyChanged(nameof(DeleteDestButtonLabel));
        UpdateDestFolderSelectionBadgesInPlace();
    }

    public int ImportDroppedFiles(IReadOnlyList<string> paths, bool forConverter)
    {
        if (paths.Count == 0)
        {
            return 0;
        }

        var supportedExts = WalkmanBackendService.SupportedAudioExtensions;

        int imported = 0;
        string? firstFolder = null;

        foreach (string rawPath in paths)
        {
            if (Directory.Exists(rawPath))
            {
                string locId = $"custom:{rawPath}";
                if (_customFolderLocations.All(c => c.Id != locId))
                {
                    _customFolderLocations.Add(new TransferLocationItem(locId, Path.GetFileName(rawPath), "folder", rawPath, DirectoryPath: rawPath));
                }

                firstFolder ??= locId;
                var scanned = _backend.ScanLocalFolderTracks(rawPath, locId);
                foreach (var trk in scanned)
                {
                    if (_allTracks.All(existing => !string.Equals(existing.Id, trk.Id, StringComparison.OrdinalIgnoreCase)))
                    {
                        trk.IsSelected = true;
                        _allTracks.Add(trk);
                        imported++;
                    }
                }
            }
            else if (File.Exists(rawPath) && supportedExts.Contains(Path.GetExtension(rawPath)))
            {
                string? dir = Path.GetDirectoryName(rawPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    string locId = $"custom:{dir}";
                    if (_customFolderLocations.All(c => c.Id != locId))
                    {
                        _customFolderLocations.Add(new TransferLocationItem(locId, Path.GetFileName(dir), "folder", dir, DirectoryPath: dir));
                    }

                    firstFolder ??= locId;
                    foreach (var trk in _backend.ScanLocalFolderTracks(dir, locId)
                                 .Where(t => string.Equals(t.SourceFilePath, rawPath, StringComparison.OrdinalIgnoreCase)))
                    {
                        if (_allTracks.All(existing => !string.Equals(existing.Id, trk.Id, StringComparison.OrdinalIgnoreCase)))
                        {
                            trk.IsSelected = true;
                            _allTracks.Add(trk);
                            imported++;
                        }
                    }
                }
            }
        }

        if (imported > 0)
        {
            RebuildAllLocations();
            if (!string.IsNullOrEmpty(firstFolder))
            {
                var match = AllLocations.FirstOrDefault(l => l.Id == firstFolder);
                if (match is not null)
                {
                    if (forConverter)
                    {
                        ConverterSourceLocation = match;
                    }
                    else
                    {
                        SelectedSourceLocation = match;
                    }
                }
            }

            RefreshAllLists();
            ShowToast("Imported Audio", $"Added {imported} track(s) from Explorer.", ToastSeverity.Success);
        }

        return imported;
    }

    // Customization Workbench Commands
    [RelayCommand]
    private void SaveLocalSubtitles()
    {
        var track = SelectedSubtitleTrack;
        if (track is null)
        {
            ShowToast("No Track Selected", "Choose a track from the left list first.", ToastSeverity.Warning);
            return;
        }

        string[] lines = SubtitlesLrcText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .ToArray();

        track.SubtitleLines = lines;
        if (!string.IsNullOrWhiteSpace(track.SourceFilePath) && File.Exists(track.SourceFilePath))
        {
            bool saved = WalkmanBackendService.TrySaveLocalLrcFile(track.SourceFilePath, lines);
            CustomizationFeedback = saved
                ? $"Saved sidecar {Path.ChangeExtension(Path.GetFileName(track.SourceFilePath), ".lrc")} ({lines.Length} lines)"
                : "Could not write sidecar .lrc file on disk.";
        }
        else
        {
            CustomizationFeedback = $"Updated preview subtitles for '{track.Title}' ({lines.Length} lines).";
        }

        OnPropertyChanged(nameof(PlayerSubtitleOrMeta));
        OnPropertyChanged(nameof(PlayerHasLrc));
        ShowToast("Subtitles Saved", CustomizationFeedback, ToastSeverity.Success);
    }

    [RelayCommand]
    private void ToggleSelectAllTagTracks()
    {
        if (CustomizationTracks.Count == 0)
        {
            return;
        }

        bool selectAll = CustomizationTracks.Any(t => !t.IsTagSelected);
        foreach (var t in CustomizationTracks)
        {
            t.IsTagSelected = selectAll;
        }

        OnPropertyChanged(nameof(TagBatchSelectedCount));
        OnPropertyChanged(nameof(TagSaveButtonLabel));
    }

    public void NotifyTagSelectionChanged()
    {
        OnPropertyChanged(nameof(TagBatchSelectedCount));
        OnPropertyChanged(nameof(TagSaveButtonLabel));
        UpdateCustFolderSelectionBadgesInPlace();
    }

    [RelayCommand]
    private async Task SaveLocalTagsAsync()
    {
        var targets = CustomizationTracks.Where(t => t.IsTagSelected).ToList();
        if (targets.Count == 0 && SelectedTagTrack is not null)
        {
            targets.Add(SelectedTagTrack);
        }

        if (targets.Count == 0)
        {
            ShowToast("No Track Selected", "Select one or more tracks to update ID3/OMA metadata.", ToastSeverity.Warning);
            return;
        }

        int? parsedYear = int.TryParse(TagEditYear.Trim(), out int yr) && yr > 0 ? yr : null;
        int? parsedTrackNum = int.TryParse(TagEditTrackNumber.Trim(), out int tn) && tn > 0 ? tn : null;
        bool multi = targets.Count > 1;

        foreach (var trk in targets)
        {
            if (!multi && !string.IsNullOrWhiteSpace(TagEditTitle)) trk.Title = TagEditTitle.Trim();
            if (!string.IsNullOrWhiteSpace(TagEditArtist)) trk.Artist = TagEditArtist.Trim();
            if (!string.IsNullOrWhiteSpace(TagEditAlbum)) trk.Album = TagEditAlbum.Trim();
            if (parsedYear.HasValue) trk.Year = parsedYear.Value;
            if (!string.IsNullOrWhiteSpace(TagEditAlbumArtist)) trk.AlbumArtist = TagEditAlbumArtist.Trim();
            if (!string.IsNullOrWhiteSpace(TagEditGenre)) trk.Genre = TagEditGenre.Trim();
            if (!multi && parsedTrackNum.HasValue) trk.TrackNumber = parsedTrackNum.Value;
            if (!string.IsNullOrWhiteSpace(TagEditComposer)) trk.Composer = TagEditComposer.Trim();
            if (!multi && !string.IsNullOrWhiteSpace(TagEditSortTitle)) trk.SortTitle = TagEditSortTitle.Trim();
            if (!string.IsNullOrWhiteSpace(TagEditArtistLinks)) trk.ArtistLinks = TagEditArtistLinks.Trim();
            if (TagEditRating > 0) trk.Rating = TagEditRating;
            if (!string.IsNullOrWhiteSpace(TagEditComment)) trk.Comment = TagEditComment.Trim();
        }

        int diskSaved = await Task.Run(() =>
        {
            int count = 0;
            foreach (var trk in targets)
            {
                if (!string.IsNullOrWhiteSpace(trk.SourceFilePath) && File.Exists(trk.SourceFilePath))
                {
                    if (WalkmanBackendService.TrySaveLocalTrackTags(
                            trk.SourceFilePath,
                            multi ? null : trk.Title,
                            trk.Artist,
                            trk.Album,
                            parsedYear,
                            trk.AlbumArtist,
                            trk.Genre,
                            multi ? null : parsedTrackNum,
                            trk.Composer,
                            multi ? null : trk.SortTitle,
                            trk.ArtistLinks,
                            trk.Rating,
                            trk.Comment))
                    {
                        count++;
                    }
                }
            }
            return count;
        });

        string? activeDestId = DestDeviceSummary?.Id;
        foreach (var trk in targets)
        {
            if (trk.DeviceTrackId.HasValue && !string.IsNullOrWhiteSpace(activeDestId))
            {
                QueuePendingDeviceTagPatch(activeDestId, new TrackMetadataPatch(
                    TrackId: trk.DeviceTrackId.Value,
                    Title: !multi ? trk.Title : null,
                    Artist: trk.Artist,
                    Album: trk.Album,
                    Genre: trk.Genre,
                    SortTitle: !multi ? trk.SortTitle : null,
                    Year: parsedYear,
                    TrackNumber: !multi ? parsedTrackNum : null,
                    AlbumArtist: trk.AlbumArtist,
                    Composer: trk.Composer,
                    Rating: trk.Rating,
                    Comment: trk.Comment,
                    ArtistLinks: trk.ArtistLinks));
            }
        }

        CustomizationFeedback = diskSaved > 0
            ? $"Saved tags to {diskSaved} local file(s) via ATL.NET"
            : $"Updated in-memory metadata across {targets.Count} track(s)";
        ShowToast("Tags Updated", CustomizationFeedback, ToastSeverity.Success);
        RefreshAllLists();
        SyncTrackPlayStates();
    }

    // Device & System Workbench Commands
    [RelayCommand]
    private void SetDeviceTab(string tab)
    {
        ActiveDeviceTab = tab;
        ActiveModuleId = tab switch
        {
            "firmware" => ModuleIds.CustomFirmware,
            "dump" => ModuleIds.DeviceDumpWizard,
            "tech" => ModuleIds.DeviceTechInfo,
            _ => ModuleIds.PairSync
        };
    }

    [RelayCommand]
    private async Task BrowseFirmwarePackageAsync()
    {
        if (PickOpenGenericFileAsyncHandler is null)
        {
            return;
        }

        string? picked = await PickOpenGenericFileAsyncHandler("Select Walkman Firmware Package", "Firmware Images", "*.bin;*.fw;*.upg");
        if (!string.IsNullOrWhiteSpace(picked))
        {
            FwPackagePath = picked;
            ShowToast("Firmware Loaded", Path.GetFileName(picked), ToastSeverity.Info);
        }
    }

    [RelayCommand]
    private void UploadFirmwarePostMvp()
    {
        if (ConnectedDevice is null)
        {
            ShowToast("Player Required", "Connect a player before staging firmware updates.", ToastSeverity.Warning);
            return;
        }

        ShowToast("Post-MVP Feature", $"Firmware flashing for Sony {ConnectedDevice.Model} is scheduled post-MVP.", ToastSeverity.Info);
    }

    [RelayCommand]
    private async Task ExportDiagnosticDumpAsync()
    {
        if (SelectedDumpProbe is null)
        {
            ShowToast("No Device Probe", "Connect or select a target probe first.", ToastSeverity.Warning);
            return;
        }

        string defaultName = $"walkman-diag-0x{SelectedDumpProbe.Probe.VendorId:X4}-0x{SelectedDumpProbe.Probe.ProductId:X4}.zip";
        string? outputZip = PickSaveGenericFileAsyncHandler is not null
            ? await PickSaveGenericFileAsyncHandler("Save Sanitized Telemetry Archive", "ZIP Archives", defaultName)
            : Path.Combine(ConvertedOutputFolder, defaultName);

        if (string.IsNullOrWhiteSpace(outputZip))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputZip)!);
            var res = await _backend.ExportDiagnosticDumpAsync(SelectedDumpProbe.Probe, outputZip);
            DumpWizardStatusText = $"Captured {res.CapturedFilesCount} files ({res.ZipSizeBytes / 1024.0:F1} KB) → {Path.GetFileName(res.OutputZipPath)}";
            ShowToast("Diagnostic Archive Ready", DumpWizardStatusText, res.Success ? ToastSeverity.Success : ToastSeverity.Warning);
        }
        catch (Exception ex)
        {
            DumpWizardStatusText = $"Dump failed: {ex.Message}";
            ShowToast("Dump Failed", ex.Message, ToastSeverity.Error);
        }
    }

    public const string DumpSubmissionFormUrl = "https://tally.so/r/ArBeRN";

    [RelayCommand]
    private void ShowPostMvpNotice(string actionName)
    {
        ShowToast("Post-MVP Maintenance", $"'{actionName}' is queued for Post-MVP device R&D.", ToastSeverity.Info);
    }

    [RelayCommand]
    private void OpenDumpSubmissionForm()
    {
        try
        {
            Process.Start(new ProcessStartInfo(DumpSubmissionFormUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowToast("Browser Launch Failed", $"Open {DumpSubmissionFormUrl} manually ({ex.Message}).", ToastSeverity.Warning);
        }
    }

    [RelayCommand]
    private void OpenOnboardingGuide()
    {
        IsOnboardingOpen = true;
    }

    public void DismissOnboardingGuide(bool dontShowAgain)
    {
        IsOnboardingOpen = false;
        if (dontShowAgain && !HasCompletedOnboarding)
        {
            HasCompletedOnboarding = true;
            PersistSettings();
        }
    }

    private void LoadSavedSettingsAndInitializeOverlay()
    {
        if (!_suppressSettingsSave)
        {
            var s = AppSettingsStore.Load(_settingsDirectoryOverride);
            IsDark = s.IsDarkTheme;
            IsDemoModeEnabled = s.IsDemoModeEnabled;
            EnableColdStartIntro = s.EnableColdStartIntro;
            UseSidebarRail = s.UseSidebarRail;
            PlayerVolume = Math.Clamp(s.PlayerVolume, 0.0, 1.0);
            IsPlayerMuted = s.IsPlayerMuted;
            IsShuffleEnabled = s.IsShuffleEnabled;
            if (Enum.TryParse<RepeatMode>(s.RepeatMode, ignoreCase: true, out var parsedRepeat))
            {
                RepeatMode = parsedRepeat;
            }
            ConvRememberChoice = s.RememberCodecChoice;
            ConvNormalizeVolume = s.NormalizeVolume;
            ConvPreserveMetadata = s.PreserveTagsAndCover;
            ConvKeepSourceFiles = s.KeepSourceFiles;
            ConvAddToLibrary = s.AddToLibraryAfterConvert;
            AutoBackupOmgaudioBeforeWrite = s.AutoBackupOmgaudioBeforeWrite;

            if (Enum.TryParse<UploadMode>(s.FmUploadMode, ignoreCase: true, out var parsedFmMode))
            {
                FmUploadMode = parsedFmMode;
            }

            if (!string.IsNullOrWhiteSpace(s.FmCustomCodecDisplayName))
            {
                var matchedSpec = FileManagerStateLogic.CustomCodecSpecs.FirstOrDefault(
                    c => string.Equals(c.DisplayName, s.FmCustomCodecDisplayName, StringComparison.OrdinalIgnoreCase));
                FmCustomCodec = matchedSpec ?? FileManagerStateLogic.CustomCodecSpecs[1];
            }

            FmNormalizeVolume = s.FmNormalizeVolume;
            FmRewriteTables = s.FmRewriteTables;
            FmRewriteLock = s.FmRewriteLock;
            FmSafeEjectAfter = s.FmSafeEjectAfter;
            FmRememberChoice = s.FmRememberChoice;

            if (!string.IsNullOrWhiteSpace(s.CustomMusicFolder) && Directory.Exists(s.CustomMusicFolder))
            {
                DefaultMusicFolder = s.CustomMusicFolder;
                LibraryFolderDisplay = s.CustomMusicFolder;
            }

            if (!string.IsNullOrWhiteSpace(s.CustomConvertedFolder) && Directory.Exists(s.CustomConvertedFolder))
            {
                ConvertedOutputFolder = s.CustomConvertedFolder;
            }

            // ATRAC3 / ATRAC1 are gated for v0.2.0b; coerce any previously saved v0.2.0b-dev preference cleanly to ATRAC3+.
            ConvTargetCodec = s.PreferredCodec switch
            {
                "MP3" => TargetCodec.Mp3,
                "AAL" or "AAC" => TargetCodec.Aal,
                "FLAC" => TargetCodec.Flac,
                "WAV" => TargetCodec.Wav,
                _ => TargetCodec.Atrac3Plus
            };

            ConvBitrate = s.PreferredBitrate switch
            {
                "352k" => BitrateOption.K352,
                "192k" => BitrateOption.K192,
                "128k" => BitrateOption.K128,
                "Lossless" when ConverterStateLogic.IsLosslessCodec(ConvTargetCodec) => BitrateOption.Lossless,
                _ => BitrateOption.K256
            };

            ConvSampleRate = s.PreferredSampleRate == "48 kHz" ? SampleRateOption.Hz48000 : SampleRateOption.Hz44100;

            if (!s.EnableColdStartIntro && StartPhase == StartScreenPhase.IdleBlobs)
            {
                StartPhase = StartScreenPhase.MedallionReady;
            }

            ShowLibColAlbum = s.ShowLibColAlbum;
            ShowLibColRating = s.ShowLibColRating;
            ShowLibColCodec = s.ShowLibColCodec;
            ShowLibColDuration = s.ShowLibColDuration;
            HasCompletedOnboarding = s.HasCompletedOnboarding;
        }

        _player.Volume = PlayerVolume;
        _player.IsMuted = IsPlayerMuted;
        SyncDemoOverlayState();
        _isInitializing = false;
        NotifyFmStates();
        NotifyConvStates();
    }

    private void PersistSettings()
    {
        if (_suppressSettingsSave || _isInitializing)
        {
            return;
        }

        var dto = new AppUserSettings(
            IsDarkTheme: IsDark,
            IsDemoModeEnabled: IsDemoModeEnabled,
            EnableColdStartIntro: EnableColdStartIntro,
            UseSidebarRail: UseSidebarRail,
            RememberCodecChoice: ConvRememberChoice,
            PreferredCodec: ConverterStateLogic.GetCodecLabel(ConvTargetCodec),
            PreferredBitrate: ConverterStateLogic.GetBitrateLabel(ConvBitrate),
            PreferredSampleRate: ConverterStateLogic.GetSampleRateLabel(ConvSampleRate),
            NormalizeVolume: ConvNormalizeVolume,
            PreserveTagsAndCover: ConvPreserveMetadata,
            KeepSourceFiles: ConvKeepSourceFiles,
            AddToLibraryAfterConvert: ConvAddToLibrary,
            PlayerVolume: PlayerVolume,
            IsPlayerMuted: IsPlayerMuted,
            IsShuffleEnabled: IsShuffleEnabled,
            RepeatMode: RepeatMode.ToString(),
            CustomMusicFolder: DefaultMusicFolder,
            CustomConvertedFolder: ConvertedOutputFolder,
            AutoBackupOmgaudioBeforeWrite: AutoBackupOmgaudioBeforeWrite,
            ShowLibColAlbum: ShowLibColAlbum,
            ShowLibColRating: ShowLibColRating,
            ShowLibColCodec: ShowLibColCodec,
            ShowLibColDuration: ShowLibColDuration,
            FmRememberChoice: FmRememberChoice,
            FmUploadMode: FmUploadMode.ToString(),
            FmCustomCodecDisplayName: FmCustomCodec.DisplayName,
            FmNormalizeVolume: FmNormalizeVolume,
            FmRewriteTables: FmRewriteTables,
            FmRewriteLock: FmRewriteLock,
            FmSafeEjectAfter: FmSafeEjectAfter,
            HasCompletedOnboarding: HasCompletedOnboarding);

        AppSettingsStore.Save(dto, _settingsDirectoryOverride);
    }

    private void SyncDemoOverlayState()
    {
        if (IsDemoModeEnabled)
        {
            if (!_allTracks.Any(t => t.IsDemoItem))
            {
                _allTracks.AddRange(DemoModeOverlay.CreateDemoSampleTracks());
            }
        }
        else
        {
            _allTracks.RemoveAll(t => t.IsDemoItem);
            if (ConnectedDevice?.IsDemoDevice == true)
            {
                ConnectedDevice = _liveHardwareDevices.FirstOrDefault();
                if (ConnectedDevice is null && ActiveModuleId == ModuleIds.FileManager)
                {
                    ReturnToStartHub();
                }
            }

            if (PlayerTrack?.IsDemoItem == true)
            {
                _player.Stop();
            }
        }

        RebuildAllLocations();
        RebuildDeviceCards();
        RefreshAllLists();
        RefreshCustomizationTracks();
        RefreshTechnicalInfoAndProbes();
        NotifyAllSummaryProps();
    }

    private void RefreshCustomizationTracks()
    {
        string locId = CustomSelectedLocation?.Id ?? string.Empty;
        string q = CustomTrackSearch.Trim();

        CustomizationTracks.Clear();
        foreach (var t in _allTracks.Where(t =>
            (string.IsNullOrEmpty(locId) || t.Locations.Contains(locId)) &&
            SearchTextMatcher.Matches(q, t.Title, t.Artist, t.Album)))
        {
            CustomizationTracks.Add(t);
        }

        RebuildCustomizationEntries();

        if (SelectedSubtitleTrack is null || !CustomizationTracks.Contains(SelectedSubtitleTrack))
        {
            SelectedSubtitleTrack = CustomizationTracks.FirstOrDefault();
        }

        if (SelectedTagTrack is null || !CustomizationTracks.Contains(SelectedTagTrack))
        {
            SelectedTagTrack = CustomizationTracks.FirstOrDefault();
        }
    }

    private void RefreshFmGroupsForDest(WalkmanDeviceSummary? dest)
    {
        FmAvailableGroups.Clear();
        FmAvailableGroups.Add("Apollo Transfer");
        if (dest?.GroupNames is { Count: > 0 } groups)
        {
            foreach (string g in groups)
            {
                if (!string.Equals(g, "All Tracks", StringComparison.OrdinalIgnoreCase) &&
                    !FmAvailableGroups.Contains(g, StringComparer.OrdinalIgnoreCase))
                {
                    FmAvailableGroups.Add(g);
                }
            }
        }

        if (!FmAvailableGroups.Contains(FmTargetGroupName, StringComparer.OrdinalIgnoreCase))
        {
            FmTargetGroupName = FmAvailableGroups[0];
        }
    }

    private void RefreshTechnicalInfoAndProbes()
    {
        AvailableDumpProbes.Clear();
        foreach (var p in _backend.LastUnrecognizedProbes)
        {
            AvailableDumpProbes.Add(new DumpProbeOption(
                $"unrec:{p.VendorId:X4}:{p.ProductId:X4}",
                $"Unrecognized USB Player (0x{p.VendorId:X4}:0x{p.ProductId:X4})",
                p.VolumeRootPath ?? "USB Mass Storage Probe",
                p));
        }

        if (ConnectedDevice?.DetectionProbe is { } liveProbe)
        {
            AvailableDumpProbes.Add(new DumpProbeOption(
                $"live:{ConnectedDevice.Id}",
                $"{ConnectedDevice.Model} (Live Probe)",
                liveProbe.VolumeRootPath ?? "Mounted OMGAUDIO Volume",
                liveProbe));
        }
        else if (IsDemoModeEnabled && AvailableDumpProbes.Count == 0)
        {
            var demoProbe = new DeviceDetectionProbe(
                VendorId: 0x054C,
                ProductId: 0x027A,
                UsbProductString: "SONY WALKMAN (Demo Profile)",
                VolumeRootPath: null,
                VolumeSerialNumber: 0x2005A300,
                TotalVolumeBytes: 20_000_000_000L,
                HardwareDvIdSegment: 0x0001,
                HardwareDvIdKey: 0x4A91E012,
                PresentRootFolders: ["OMGAUDIO"],
                PresentOmgAudioFiles: ["00GTRLST.DAT", "01GRPLST.DAT", "03GINF01.DAT", "04CNTINF.DAT", "MACLIST0.DAT"]);
            AvailableDumpProbes.Add(new DumpProbeOption(
                "demo:probe",
                "NW-HD5 Simulated Telemetry Probe [DEMO]",
                "Synthetic 0x054C:0x027A Descriptor",
                demoProbe));
        }

        SelectedDumpProbe = AvailableDumpProbes.FirstOrDefault();
        OnPropertyChanged(nameof(CanStartDump));

        TechnicalInfoRows.Clear();
        var dev = ConnectedDevice;
        if (dev is not null)
        {
            TechnicalInfoRows.Add(new TechInfoRow("Device Model", $"{dev.Model} ({dev.ModeBadge})", "Identity"));
            TechnicalInfoRows.Add(new TechInfoRow("Hardware Series", dev.Series, "Identity"));
            TechnicalInfoRows.Add(new TechInfoRow("Display Panel", dev.ScreenResolution, "Hardware"));
            TechnicalInfoRows.Add(new TechInfoRow("Storage Usage", dev.StorageFormatted, "Storage"));
            TechnicalInfoRows.Add(new TechInfoRow("Supported Codecs", dev.CodecsFormatted, "Audio"));
            if (dev.DetectionProbe is { } p)
            {
                TechnicalInfoRows.Add(new TechInfoRow("USB VID / PID", $"0x{p.VendorId:X4} : 0x{p.ProductId:X4}", "USB Bus"));
                TechnicalInfoRows.Add(new TechInfoRow("Volume Path", p.VolumeRootPath ?? "Virtual", "Filesystem"));
                TechnicalInfoRows.Add(new TechInfoRow("Volume Serial", p.VolumeSerialNumber.HasValue ? $"0x{p.VolumeSerialNumber:X8}" : "—", "Filesystem"));
            }

            if (dev.SlotPoolStatus is { } sp)
            {
                TechnicalInfoRows.Add(new TechInfoRow("OpenMG 0x0001 Pool", sp.HasMaclist0 ? $"{sp.ActiveSignedTrackCount} active · {sp.RecycledFreeSlotCount} recycled" : "Unprotected / No MACLIST0", "Crypto"));
            }

            if ((dev.LiveIdentity?.BpbDiagnostics ?? dev.DetectionProbe?.BpbDiagnostics) is { } bpb)
            {
                TechnicalInfoRows.Add(new TechInfoRow(
                    "FAT16 BPB",
                    bpb.HasWindowsFormatWarning
                        ? $"WARNING · OEM '{bpb.OemName}' (Rsvd={bpb.ReservedSectors})"
                        : $"OK · OEM '{bpb.OemName}' (Rsvd={bpb.ReservedSectors})",
                    "Filesystem"));
            }
        }
        else
        {
            TechnicalInfoRows.Add(new TechInfoRow("Hardware Status", "No player attached", "Identity"));
            TechnicalInfoRows.Add(new TechInfoRow("Orchestrator", _backend.HasManifestRegistry ? "Manifest Loaded (L1/L2 ready)" : "Standalone Shell", "Engine"));
        }

        TechnicalInfoRows.Add(new TechInfoRow("Audio Engine", TranscoderBackendStatus, "Engine"));
        TechnicalInfoRows.Add(new TechInfoRow("USB Watcher", UsbWatchStatus, "Transport"));
    }

    private void NotifyAllSummaryProps()
    {
        OnPropertyChanged(nameof(SummaryHardwareTitle));
        OnPropertyChanged(nameof(SummaryHardwareDetail));
        OnPropertyChanged(nameof(SummaryLibraryTitle));
        OnPropertyChanged(nameof(SummaryLibraryDetail));
        OnPropertyChanged(nameof(SummaryEngineTitle));
        OnPropertyChanged(nameof(SummaryEngineDetail));
    }

    private void NotifyFmStorageProjection()
    {
        OnPropertyChanged(nameof(DestBaseUsedPercent));
        OnPropertyChanged(nameof(DestProjectedAdditionalPercent));
        OnPropertyChanged(nameof(DestProjectedTotalPercent));
        OnPropertyChanged(nameof(IsTransferOverCapacity));
        OnPropertyChanged(nameof(DestProjectedStorageText));
    }

    partial void OnIsDemoModeEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(DemoModeStatusLabel));
        SyncDemoOverlayState();
        PersistSettings();
    }

    partial void OnEnableColdStartIntroChanged(bool value) => PersistSettings();

    partial void OnUseSidebarRailChanged(bool value)
    {
        OnPropertyChanged(nameof(NavStyleLabel));
        OnPropertyChanged(nameof(ShowRadialJogDial));
        OnPropertyChanged(nameof(ShowSidebarRail));
        PersistSettings();
    }

    partial void OnPlayerVolumeChanged(double value)
    {
        _player.Volume = value;
        PersistSettings();
    }

    partial void OnIsPlayerMutedChanged(bool value)
    {
        _player.IsMuted = value;
        PersistSettings();
    }

    partial void OnActiveConfirmDialogChanged(ConfirmDialogState? value) => OnPropertyChanged(nameof(HasConfirmDialog));
    partial void OnCustomTrackSearchChanged(string value) => RefreshCustomizationTracks();
    partial void OnCustomSelectedLocationChanged(TransferLocationItem? value) => RefreshCustomizationTracks();
    partial void OnSelectedDumpProbeChanged(DumpProbeOption? value) => OnPropertyChanged(nameof(CanStartDump));

    partial void OnActiveDeviceTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsDevicePairTab));
        OnPropertyChanged(nameof(IsDeviceFirmwareTab));
        OnPropertyChanged(nameof(IsDeviceDumpTab));
        OnPropertyChanged(nameof(IsDeviceTechTab));
    }

    partial void OnSelectedSubtitleTrackChanged(AudioTrackItem? value)
    {
        if (value is null)
        {
            return;
        }

        SubtitlesLrcText = value.SubtitleLines is { Count: > 0 } lines
            ? string.Join('\n', lines)
            : string.Empty;
    }

    partial void OnSelectedTagTrackChanged(AudioTrackItem? value)
    {
        if (value is null)
        {
            return;
        }

        TagEditTitle = value.Title;
        TagEditArtist = value.Artist;
        TagEditAlbum = value.Album;
        TagEditYear = value.Year > 0 ? value.Year.ToString(CultureInfo.InvariantCulture) : string.Empty;
        TagEditAlbumArtist = value.AlbumArtist;
        TagEditGenre = value.Genre;
        TagEditTrackNumber = value.TrackNumber > 0 ? value.TrackNumber.ToString(CultureInfo.InvariantCulture) : string.Empty;
        TagEditComposer = value.Composer;
        TagEditSortTitle = value.SortTitle;
        TagEditArtistLinks = value.ArtistLinks;
        TagEditRating = value.Rating;
        TagEditComment = value.Comment;
        OnPropertyChanged(nameof(TagEditCoverBitmap));
        OnPropertyChanged(nameof(HasTagEditCover));
        OnPropertyChanged(nameof(TagEditCoverStatusText));
        OnPropertyChanged(nameof(SelectedTagTrackCover));
        OnPropertyChanged(nameof(HasSelectedTagTrackCover));
    }

    partial void OnFmTargetGroupNameChanged(string value) => RebuildFileManagerDestEntries();
}
