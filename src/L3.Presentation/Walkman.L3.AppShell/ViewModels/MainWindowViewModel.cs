using Avalonia.Media.Imaging;
using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;
using Walkman.Contracts.Messaging;
using Walkman.L3.AppShell.Controls;
using Walkman.L3.AppShell.Core;
using Walkman.L3.AppShell.Services;

namespace Walkman.L3.AppShell.ViewModels;

public sealed record ProjectedOutputItem(
    string TitleWithExtension,
    string SourceToTargetLabel,
    string EstimatedSizeText,
    string OriginalSizeText);

public sealed record FeatureCardItem(
    string Id,
    string Title,
    string Subtitle,
    Geometry? IconGeometry,
    bool IsBlocked,
    bool ShowBlockedNote);

public sealed partial class WalkmanDeviceCardViewModel : ObservableObject
{
    public required WalkmanDeviceSummary Profile { get; init; }
    [ObservableProperty] private bool _isConnected;

    public string ButtonLabel => IsConnected ? "Disconnect" : $"Pair {Profile.Model}";
    public string StatusBadge => Profile.IsLiveHardware ? "LIVE USB" : "DEMO PROFILE";
    public SupportStatus SupportStatus => Profile.SupportStatus;
    public bool IsExperimental => Profile.IsExperimentalProfile;
    public string SupportBadge => Profile.SupportStatusBadge;
    public bool ShowSlotPoolInfo => Profile.SlotPoolStatus is { } s && (s.HasMaclist0 || s.BrokenUnsignedSlotCount > 0);
    public string SlotPoolSummary => Profile.SlotPoolStatus is { } s && (s.HasMaclist0 || s.BrokenUnsignedSlotCount > 0)
        ? MainWindowViewModel.FormatSlotPoolSummary(s)
        : string.Empty;

    partial void OnIsConnectedChanged(bool value)
    {
        OnPropertyChanged(nameof(ButtonLabel));
    }
}

public sealed partial class MainWindowViewModel : ObservableObject, IDisposable
{
    private readonly WalkmanBackendService _backend;
    private readonly AudioPlaybackService _player;
    private readonly List<AudioTrackItem> _allTracks = new();
    private readonly List<TransferLocationItem> _customFolderLocations = new();
    private readonly List<WalkmanDeviceSummary> _liveHardwareDevices = new();
    private readonly IDisposable? _progressSub;
    private DispatcherTimer? _demoTransferTimer;
    private CancellationTokenSource? _folderScanCts;
    [ObservableProperty] private bool _isScanningLibraryFolder;

    [ObservableProperty] private StartScreenPhase _startPhase = StartScreenPhase.IdleBlobs;
    [ObservableProperty] private string? _activeModuleId;
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private bool _reducedBlur;
    [ObservableProperty] private WalkmanDeviceSummary? _connectedDevice;
    [ObservableProperty] private bool _isJogDialOpen;

    [ObservableProperty] private string _defaultMusicFolder;
    [ObservableProperty] private string _convertedOutputFolder;
    [ObservableProperty] private string _transcoderBackendStatus = "Native C# ATRAC3+ · LAME MP3 · Bundled";
    [ObservableProperty] private string _usbWatchStatus = "Listening via WM_DEVICECHANGE + VFS Poll";

    // Bottom Left Dock: Transfer / Transcode Job
    [ObservableProperty] private ActiveTransferJob? _transferJob;

    // Bottom Right Dock: Global Audio Player
    [ObservableProperty] private AudioTrackItem? _playerTrack;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private int _elapsedSec;

    // File Manager State
    [ObservableProperty] private TransferLocationItem? _selectedSourceLocation;
    [ObservableProperty] private TransferLocationItem? _selectedDestLocation;
    [ObservableProperty] private string _fileManagerLeftSearch = string.Empty;
    [ObservableProperty] private string _fileManagerRightSearch = string.Empty;
    [ObservableProperty] private UploadMode _fmUploadMode = UploadMode.Auto;
    [ObservableProperty] private CustomCodecSpec _fmCustomCodec = FileManagerStateLogic.CustomCodecSpecs[1];
    [ObservableProperty] private bool _fmNormalizeVolume = true;
    [ObservableProperty] private bool _fmRewriteTables;
    [ObservableProperty] private bool _fmRewriteLock;
    [ObservableProperty] private bool _fmSafeEjectAfter;
    [ObservableProperty] private bool _fmRememberChoice;
    [ObservableProperty] private string? _fileManagerHint;

    // Codec Converter State
    [ObservableProperty] private TransferLocationItem? _converterSourceLocation;
    [ObservableProperty] private TransferLocationItem? _converterOutputLocation;
    [ObservableProperty] private string _converterSearch = string.Empty;
    [ObservableProperty] private TargetCodec _convTargetCodec = TargetCodec.Atrac3Plus;
    [ObservableProperty] private BitrateOption _convBitrate = BitrateOption.K256;
    [ObservableProperty] private SampleRateOption _convSampleRate = SampleRateOption.Hz44100;
    [ObservableProperty] private bool _convNormalizeVolume = true;
    [ObservableProperty] private bool _convPreserveMetadata = true;
    [ObservableProperty] private bool _convKeepSourceFiles = true;
    [ObservableProperty] private bool _convAddToLibrary = true;
    [ObservableProperty] private bool _convRememberChoice;

    // My Library State
    [ObservableProperty] private string _libraryFormatFilter = "All";
    [ObservableProperty] private string _librarySearch = string.Empty;
    [ObservableProperty] private string _libraryFolderDisplay;

    // Customization State
    [ObservableProperty] private string _activeCustomTab = "oled";
    [ObservableProperty] private ScreenTintMode _oledTintMode = ScreenTintMode.Cyan;
    [ObservableProperty] private string _oledAnimationMode = "spectrum";
    [ObservableProperty] private string _oledBannerText = "WALKMAN AERO";
    [ObservableProperty] private string _subtitlesLrcText =
        "[00:14.20] Neon reflections on cool glass\n" +
        "[00:19.85] Midnight horizon over Tokyo Bay\n" +
        "[00:26.10] Silver cassette turning in the dark\n" +
        "[00:33.40] Clear sky drifting into morning light";
    [ObservableProperty] private string _tagEditTitle = "Midnight Horizon (2004 Remaster)";
    [ObservableProperty] private string _tagEditArtist = "Mondo Grosso";
    [ObservableProperty] private string _tagEditAlbum = "Next Wave";
    [ObservableProperty] private string? _customizationFeedback;
    [ObservableProperty] private NwmProjectDto? _loadedNwmProject;
    [ObservableProperty] private string _nwmProjectSummary = "Built-in dot-matrix generator ready (120×36 0xEC01)";
    [ObservableProperty] private bool _autoBackupOmgaudioBeforeWrite;
    [ObservableProperty] private bool _hasCompletedOnboarding;
    [ObservableProperty] private bool _isOnboardingOpen;

    public Func<string, Task<string?>>? PickFolderAsyncHandler { get; set; }
    public Func<Task<string?>>? PickNwmOpenFileAsyncHandler { get; set; }
    public Func<string, Task<string?>>? PickNwmSaveFileAsyncHandler { get; set; }

    public ObservableCollection<TransferLocationItem> AllLocations { get; } = new();
    public ObservableCollection<TransferLocationItem> DestLocations { get; } = new();
    public BulkObservableCollection<AudioTrackItem> FileManagerSourceTracks { get; } = new();
    public BulkObservableCollection<AudioTrackItem> FileManagerDestTracks { get; } = new();
    public BulkObservableCollection<AudioTrackItem> ConverterSourceTracks { get; } = new();
    public ObservableCollection<ProjectedOutputItem> ConverterProjectedOutputs { get; } = new();
    public BulkObservableCollection<AudioTrackItem> FilteredLibraryTracks { get; } = new();
    public ObservableCollection<WalkmanDeviceCardViewModel> DeviceCards { get; } = new();
    public ObservableCollection<FeatureCardItem> FlatFeatures { get; } = new();
    public IReadOnlyList<CustomCodecSpec> CustomCodecChoices => FileManagerStateLogic.CustomCodecSpecs;

    public MainWindowViewModel(
        WalkmanBackendService backend,
        AudioPlaybackService player,
        bool autostart = false,
        bool suppressSettingsSave = false,
        string? settingsDirectoryOverride = null)
    {
        _backend = backend;
        _player = player;
        _suppressSettingsSave = suppressSettingsSave;
        _settingsDirectoryOverride = settingsDirectoryOverride;

        if (autostart)
        {
            _startPhase = StartScreenPhase.MedallionReady;
        }

        string userMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        if (string.IsNullOrWhiteSpace(userMusic))
        {
            userMusic = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Music");
        }

        _defaultMusicFolder = Path.Combine(userMusic, "Walkman Library");
        _convertedOutputFolder = Path.Combine(userMusic, "Converted");
        _libraryFolderDisplay = _defaultMusicFolder;

        LoadSavedSettingsAndInitializeOverlay();
        if (!HasCompletedOnboarding && !autostart && !suppressSettingsSave)
        {
            IsOnboardingOpen = true;
        }
        LoadRealTracksFromFolder(_defaultMusicFolder, "pc-library");

        _player.StateChanged += OnAudioPlayerStateChanged;
        _player.TrackEnded += () => StepTrack(1, fromTrackEnd: true);
        _player.NextTrackRequested += () => StepTrack(1);
        _player.PreviousTrackRequested += () => PlayPreviousTrack();
        _player.OmaStreamResolver = (trk, ct) =>
            _backend.DecodeOmaTrackForPlaybackAsync(trk, ConnectedDevice?.LiveIdentity?.HardwareDvIdKey, ct);
        _progressSub = _backend.EventBus.Subscribe<TransferProgressEvent>(OnBackendTransferProgress);

        RebuildAllLocations();
        RebuildDeviceCards();
        RebuildFlatFeatures();

        bool hasLocalPc = _allTracks.Any(t => !t.IsDemoItem && t.Locations.Contains("pc-library"));
        string defaultSrcId = !hasLocalPc && IsDemoModeEnabled ? DemoModeOverlay.DemoSourceLocationId : "pc-library";
        SelectedSourceLocation = AllLocations.FirstOrDefault(l => l.Id == defaultSrcId) ?? AllLocations.FirstOrDefault();
        SelectedDestLocation = DestLocations.FirstOrDefault(l => l.Id == "nw-hd5") ?? DestLocations.FirstOrDefault();
        ConverterSourceLocation = AllLocations.FirstOrDefault(l => l.Id == defaultSrcId) ?? AllLocations.FirstOrDefault();
        ConverterOutputLocation = AllLocations.FirstOrDefault(l => l.Id == "pc-converted") ?? AllLocations.LastOrDefault();
        CustomSelectedLocation = AllLocations.FirstOrDefault(l => l.Id == defaultSrcId) ?? AllLocations.FirstOrDefault();

        RefreshAllLists();
        RefreshCustomizationTracks();
        RefreshTechnicalInfoAndProbes();
        NotifyAllSummaryProps();
    }

    // Computed Shell Flags
    public bool IsStartHub => ActiveModuleId is null;
    public bool IsWorkspace => ActiveModuleId is not null;
    public bool ShowTopControls => IsWorkspace || StartPhase != StartScreenPhase.IdleBlobs;
    public bool IsDeviceConnected => ConnectedDevice is not null;
    public bool IsJogDialBlurActive => IsJogDialOpen && !ReducedBlur;
    public string TopUsbPillText => ConnectedDevice is not null
        ? $"{ConnectedDevice.Model} {(ConnectedDevice.IsLiveHardware ? "USB" : "Connected")}"
        : "Simulate USB";
    public string ThemePillLabel => IsDark ? "Dark Graphite" : "Light Aero";

    public bool IsFileManagerActive => ActiveModuleId == ModuleIds.FileManager;
    public bool IsCodecConverterActive => ActiveModuleId == ModuleIds.Converter;
    public bool IsLibraryActive => ActiveModuleId == ModuleIds.Library;
    public bool IsCustomizationActive => ActiveModuleId is ModuleIds.Customization or ModuleIds.CustomOled or ModuleIds.CustomSubtitles or ModuleIds.CustomTags;
    public bool IsAllFeaturesActive => ActiveModuleId == ModuleIds.AllFeatures;
    public bool IsSettingsActive => ActiveModuleId == ModuleIds.Settings;

    // Dock Visibility & Formatting
    public bool HasTransferJob => TransferJob is not null;
    public bool IsTransferRunning => TransferJob is { IsCompleted: false, IsError: false };
    public bool IsFileTransferUnlocked => !IsTransferRunning;
    public bool HasPlayerTrack => PlayerTrack is not null;
    public int PlayerDurationSec => Math.Max(1, PlayerTrack?.DurationSec ?? 1);
    public string PlayerTimeText => $"{AudioTrackItem.FormatDuration(ElapsedSec)} / {AudioTrackItem.FormatDuration(PlayerTrack?.DurationSec ?? 0)}";
    public bool PlayerHasLrc => PlayerTrack?.HasSubtitles == true;
    public string PlayerSubtitleOrMeta
    {
        get
        {
            if (PlayerTrack is null) return string.Empty;
            if (PlayerTrack.SubtitleLines is { Count: > 0 } lines)
            {
                int idx = (ElapsedSec / 6) % lines.Count;
                string cleanLine = WalkmanBackendService.StripLrcTimestamps(lines[idx]);
                return $"♪ {cleanLine}";
            }

            return $"{PlayerTrack.Artist} · {PlayerTrack.Format}";
        }
    }
    public Brush PlayerCoverBrush => PlayerTrack is null
        ? new SolidColorBrush(Color.Parse("#7ACBFF"))
        : new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse(PlayerTrack.CoverGradient.TopHex), 0),
                new GradientStop(Color.Parse(PlayerTrack.CoverGradient.BottomHex), 1)
            }
        };

    // File Manager Computed Properties
    public FileManagerTransferConfig CurrentFmConfig => new(
        SelectedSourceLocation?.Id ?? "pc-library",
        SelectedDestLocation?.Id ?? "nw-hd5",
        FmUploadMode,
        FmCustomCodec.DisplayName,
        FmNormalizeVolume,
        FmRewriteTables,
        FmRewriteLock,
        FmSafeEjectAfter,
        FmRememberChoice);

    public string TransferCodecSubtitle => "Export tracks from PC library or folders to Player OMGAUDIO";
    public string FmModeHint => FileManagerStateLogic.GetModeHint(FmUploadMode);
    public bool IsUploadQuality => FmUploadMode == UploadMode.Quality;
    public bool IsUploadFaster => FmUploadMode == UploadMode.Faster;
    public bool IsUploadAuto => FmUploadMode == UploadMode.Auto;
    public bool IsUploadCustom => FmUploadMode == UploadMode.Custom;
    public bool IsFmModeLocked => FileManagerStateLogic.IsTransferControlLocked(FmRememberChoice, TransferControlKind.UploadMode);
    public bool IsFmNormLocked => FileManagerStateLogic.IsTransferControlLocked(FmRememberChoice, TransferControlKind.NormalizeVolume);
    public bool IsFmTablesLocked => FileManagerStateLogic.IsTransferControlLocked(FmRememberChoice, TransferControlKind.RewriteTables);
    public bool IsFmLockTrackLocked => FileManagerStateLogic.IsTransferControlLocked(FmRememberChoice, TransferControlKind.RewriteLock);
    public bool IsFmEjectLocked => FileManagerStateLogic.IsTransferControlLocked(FmRememberChoice, TransferControlKind.SafeEjectAfter);
    public string RememberChoiceLabel => FmRememberChoice ? "Choice Locked" : "Remember Choice";
    public int TransferSelectedCount => GetUnfilteredFileManagerSourceTracks().Count(t => t.IsSelected);
    public string TransferButtonLabel => IsTransferRunning ? "Transferring..." : $"Transfer ({TransferSelectedCount})";
    public bool CanStartTransfer => TransferSelectedCount > 0 && !IsTransferRunning && SelectedSourceLocation?.Id != SelectedDestLocation?.Id;
    public string FmSourceSearchQuery
    {
        get => FileManagerLeftSearch;
        set => FileManagerLeftSearch = value;
    }
    public string FmDestSearchQuery
    {
        get => FileManagerRightSearch;
        set => FileManagerRightSearch = value;
    }
    [RelayCommand] private void ClearFmSourceSearch() => FmSourceSearchQuery = string.Empty;
    [RelayCommand] private void ClearFmDestSearch() => FmDestSearchQuery = string.Empty;
    public WalkmanDeviceSummary? DestDeviceSummary => _liveHardwareDevices
        .Concat(IsDemoModeEnabled ? DemoModeOverlay.CreateDemoDevices() : [])
        .FirstOrDefault(d => d.Id == SelectedDestLocation?.Id);
    public bool HasDestDeviceSummary => DestDeviceSummary is not null;
    public bool HasDestSlotPool => DestDeviceSummary?.SlotPoolStatus is { } s && (s.HasMaclist0 || s.BrokenUnsignedSlotCount > 0);
    public string DestSlotPoolText => DestDeviceSummary?.SlotPoolStatus is { } s && (s.HasMaclist0 || s.BrokenUnsignedSlotCount > 0)
        ? FormatSlotPoolSummary(s)
        : string.Empty;

    public static string FormatSlotPoolSummary(MagicGateSlotPoolStatus s)
    {
        if (!s.IsHardwareIcvInitialized)
        {
            return $"0x0001 Slots: 0 valid · {s.BrokenUnsignedSlotCount} broken/unsigned (ICV uninitialized)";
        }

        if (s.BrokenUnsignedSlotCount > 0)
        {
            return $"0x0001 Slots: {s.ActiveSignedTrackCount} active · {s.RecycledFreeSlotCount} free · {s.BrokenUnsignedSlotCount} broken";
        }

        return $"0x0001 Slots: {s.ActiveSignedTrackCount} active · {s.RecycledFreeSlotCount} recycled free";
    }
    public bool CanDeleteSelectedDest => DestDeviceSummary is not null;

    // Codec Converter Computed Properties
    public OfflineConverterConfig CurrentConverterConfig => new(
        ConverterSourceLocation?.Id ?? "pc-library",
        ConverterOutputLocation?.Id ?? "pc-converted",
        ConvTargetCodec,
        ConvBitrate,
        ConvSampleRate,
        ConvNormalizeVolume,
        ConvPreserveMetadata,
        ConvKeepSourceFiles,
        ConvAddToLibrary,
        ConvRememberChoice);

    public string ConverterSubtitle => "Standalone offline batch audio encoder";
    public string ConverterCodecHint => ConverterStateLogic.GetCodecHint(ConvTargetCodec);
    public bool IsLosslessTarget => ConverterStateLogic.IsLosslessCodec(ConvTargetCodec);
    public bool ShowLossyBitrates => !IsLosslessTarget;
    public string ConverterBitrateRightText => IsLosslessTarget ? "16-bit PCM" : ConverterStateLogic.GetBitrateLabel(ConvBitrate);
    public string ConverterBitrateHeader => IsLosslessTarget ? "BIT DEPTH / RATE" : "BITRATE & SAMPLE RATE";
    public bool IsTargetAtrac3Plus => ConvTargetCodec == TargetCodec.Atrac3Plus;
    public bool IsTargetAtrac3 => ConvTargetCodec == TargetCodec.Atrac3;
    public bool IsTargetAtrac1 => ConvTargetCodec == TargetCodec.Atrac1;
    public bool IsTargetMp3 => ConvTargetCodec == TargetCodec.Mp3;
    public bool IsTargetAal => ConvTargetCodec == TargetCodec.Aal;
    public bool IsTargetAac => IsTargetAal;
    public bool IsTargetFlac => ConvTargetCodec == TargetCodec.Flac;
    public bool IsTargetWav => ConvTargetCodec == TargetCodec.Wav;
    public bool ShowStandardBitrates => IsTargetAtrac3Plus || IsTargetMp3;
    public bool ShowAtrac3Bitrates => IsTargetAtrac3;
    public bool ShowAtrac1Bitrate => IsTargetAtrac1;
    public bool IsBitrate352 => ConvBitrate == BitrateOption.K352;
    public bool IsBitrate292 => ConvBitrate == BitrateOption.K292;
    public bool IsBitrate256 => ConvBitrate == BitrateOption.K256;
    public bool IsBitrate192 => ConvBitrate == BitrateOption.K192;
    public bool IsBitrate132 => ConvBitrate == BitrateOption.K132;
    public bool IsBitrate128 => ConvBitrate == BitrateOption.K128;
    public bool IsBitrate105 => ConvBitrate == BitrateOption.K105;
    public bool IsBitrate66 => ConvBitrate == BitrateOption.K66;
    public bool IsRate44100 => ConvSampleRate == SampleRateOption.Hz44100;
    public bool IsRate48000 => ConvSampleRate == SampleRateOption.Hz48000;
    public bool IsConvCodecLocked => ConverterStateLogic.IsConverterControlLocked(ConvRememberChoice, ConverterControlKind.TargetCodec);
    public bool IsConvBitrateLocked => ConverterStateLogic.IsConverterControlLocked(ConvRememberChoice, ConverterControlKind.Bitrate);
    public bool IsConvRateLocked => ConverterStateLogic.IsConverterControlLocked(ConvRememberChoice, ConverterControlKind.SampleRate);
    public bool IsConvNormLocked => ConverterStateLogic.IsConverterControlLocked(ConvRememberChoice, ConverterControlKind.NormalizeVolume);
    public bool IsConvMetaLocked => ConverterStateLogic.IsConverterControlLocked(ConvRememberChoice, ConverterControlKind.PreserveMetadata);
    public bool IsConvKeepLocked => ConverterStateLogic.IsConverterControlLocked(ConvRememberChoice, ConverterControlKind.KeepSource);
    public bool IsConvAddLocked => ConverterStateLogic.IsConverterControlLocked(ConvRememberChoice, ConverterControlKind.AddToLibrary);
    public string ConverterRememberLabel => ConvRememberChoice ? "Choice Locked" : "Remember Choice";
    public int ConverterSelectedCount => ConverterSourceTracks.Count(t => t.IsSelected);
    public string ConvertButtonLabel => IsTransferRunning ? "Encoding..." : $"Convert ({ConverterSelectedCount})";
    public bool CanStartConvert => ConverterSelectedCount > 0 && !IsTransferRunning;
    public string ConverterFootprintLeft => $"Estimated Output ({ConverterSelectedCount} files)";
    public string ConverterFootprintRight { get; private set; } = "0.0 MB → 0.0 MB (0%)";
    public int ConverterFootprintPercent { get; private set; }

    // Library Filter Flags
    public bool IsLibFilterAll => LibraryFormatFilter == "All";
    public bool IsLibFilterFlac => LibraryFormatFilter == "FLAC";
    public bool IsLibFilterAtrac => LibraryFormatFilter == "ATRAC3+";
    public bool IsLibFilterMp3 => LibraryFormatFilter == "MP3";
    public bool IsLibFilterWav => LibraryFormatFilter == "WAV";

    // Customization Tab Flags
    public bool IsCustomOledTab => ActiveCustomTab == "oled";
    public bool IsOledPreviewActive => IsCustomizationActive && IsCustomOledTab;
    public bool IsCustomSubtitlesTab => ActiveCustomTab == "subtitles";
    public bool IsCustomTagsTab => ActiveCustomTab == "tags";
    public bool IsAnimSpectrum => OledAnimationMode == "spectrum";
    public bool IsAnimOrbit => OledAnimationMode == "orbit";
    public bool IsAnimWave => OledAnimationMode == "classic";
    public bool IsAnimCustom => OledAnimationMode == "custom";
    public bool IsTintCyan => OledTintMode == ScreenTintMode.Cyan;
    public bool IsTintLime => OledTintMode == ScreenTintMode.Lime;
    public bool IsTintAmber => OledTintMode == ScreenTintMode.Amber;
    public string OledTintLabel => OledTintMode switch
    {
        ScreenTintMode.Lime => "Light-Lime OLED",
        ScreenTintMode.Amber => "NW-A3000 Amber",
        _ => "Baby-Blue EL"
    };

    public string OledScreenResLabel => OledAnimationMode == "custom" && LoadedNwmProject is not null
        ? $"{LoadedNwmProject.Width} × {LoadedNwmProject.Height} ({LoadedNwmProject.Magic})"
        : ConnectedDevice?.ScreenResolution ?? "Multi-Generation Screen R&D (UI-01)";
    public string CustomUploadButtonLabel => "Upload to Player";
    public string CustomUploadSubtitle => ConnectedDevice is not null
        ? $"Screensavers, synchronized lyrics & metadata · target: {ConnectedDevice.Model}"
        : "Edit screensavers, LRC subtitles, and metadata offline · upload requires a connected Walkman.";
    public string FirmwareTargetModel => ConnectedDevice is not null ? $"Sony {ConnectedDevice.Model}" : "Offline Preview";
    public string FirmwareVersionText => ConnectedDevice is not null ? $"v{ConnectedDevice.Firmware}" : "—";
    public bool IsStartHubRingExpanded => IsStartHub && StartPhase == StartScreenPhase.RingExpanded;
    public bool ShowRadialJogDial => IsWorkspace && !UseSidebarRail;
    public bool ShowSidebarRail => IsWorkspace && UseSidebarRail;

    public event Action? IntroReplayedRequested;

    // Commands
    [RelayCommand]
    private void OpenModuleById(string moduleId) => OpenModule(moduleId);
    public void TriggerPointerEnterStart()
    {
        if (ActiveModuleId is null && StartPhase == StartScreenPhase.IdleBlobs)
        {
            StartPhase = ModuleRegistry.TransitionStartState(StartPhase, StartScreenEvent.PointerEnter);
        }
    }

    [RelayCommand]
    private void ReplayIntro()
    {
        ActiveModuleId = null;
        StartPhase = StartScreenPhase.IdleBlobs;
        IntroReplayedRequested?.Invoke();
    }

    [RelayCommand]
    private void ToggleTheme() => IsDark = !IsDark;

    [RelayCommand]
    private void ToggleReducedBlur() => ReducedBlur = !ReducedBlur;

    [RelayCommand]
    private void ToggleQuickConnect()
    {
        if (ConnectedDevice is not null)
        {
            _ = SafeEjectCommand.ExecuteAsync(null);
        }
        else
        {
            var first = DeviceCards.FirstOrDefault()?.Profile
                ?? (IsDemoModeEnabled ? DemoModeOverlay.CreateDemoDevices()[0] : null);
            if (first is not null)
            {
                SelectDevice(first);
                ShowToast("Player Linked", $"Connected to {first.Model}", ToastSeverity.Success);
            }
            else
            {
                ShowToast("No Player Found", "Plug a Player via USB.", ToastSeverity.Warning);
            }
        }
    }

    private bool _isScanningHardware;

    [RelayCommand]
    private async Task RescanUsbHardwareAsync()
    {
        if (_isScanningHardware)
        {
            return;
        }

        _isScanningHardware = true;
        try
        {
            string? prevConnectedId = ConnectedDevice?.Id;
            var (liveDevices, liveTracks) = await _backend.ScanLiveHardwareAsync();
            _liveHardwareDevices.Clear();
            _liveHardwareDevices.AddRange(liveDevices);

            _allTracks.RemoveAll(t => t.DeviceInstanceId is not null);
            _allTracks.AddRange(liveTracks);

            RebuildAllLocations();
            RebuildDeviceCards();

            if (liveDevices.Count > 0)
            {
                var targetDevice = liveDevices.FirstOrDefault(d => string.Equals(d.Id, prevConnectedId, StringComparison.OrdinalIgnoreCase))
                    ?? liveDevices[0];
                if (!HasPendingDeviceTableChanges)
                {
                    SeedDeviceGroupOrder(targetDevice);
                }
                SelectDevice(targetDevice);
                UsbWatchStatus = $"Detected {targetDevice.Model} ({targetDevice.LiveIdentity?.VolumeRootPath})";
                ShowToast("Player Connected", UsbWatchStatus, ToastSeverity.Success);
                CheckAndSurfaceBpbWarnings(liveDevices);
                CheckAndSurfaceSupportTierWarnings(liveDevices, _backend.LastUnrecognizedSonyProbes);
            }
            else if (ConnectedDevice?.IsLiveHardware == true)
            {
                ClearAllBpbWarningNotifications();
                ClearAllSupportTierWarningNotifications();
                ConnectedDevice = null;
                if (ActiveModuleId == ModuleIds.FileManager)
                {
                    ReturnToStartHub();
                }
                UsbWatchStatus = IsDemoModeEnabled
                    ? "Live player disconnected · Demo profiles available"
                    : "No live player USB volume attached";
                ShowToast("Player Disconnected", "Returned cleanly from File Manager.", ToastSeverity.Warning);
                CheckAndSurfaceUnrecognizedSonyProbes(_backend.LastUnrecognizedSonyProbes);
            }
            else
            {
                UsbWatchStatus = IsDemoModeEnabled
                    ? "No live player USB volume attached · Demo profiles available"
                    : "No live player USB volume attached";
                CheckAndSurfaceUnrecognizedSonyProbes(_backend.LastUnrecognizedSonyProbes);
            }

            RefreshAllLists();
            RefreshTechnicalInfoAndProbes();
            NotifyAllSummaryProps();
        }
        catch (Exception ex)
        {
            UsbWatchStatus = $"Scan error: {ex.Message}";
            ShowToast("USB Scan Error", ex.Message, ToastSeverity.Error);
        }
        finally
        {
            _isScanningHardware = false;
        }
    }

    [RelayCommand]
    private async Task MountFolderFixtureAsync()
    {
        if (PickFolderAsyncHandler is null) return;
        string? folder = await PickFolderAsyncHandler("Select Player OMGAUDIO Root Folder");
        if (string.IsNullOrWhiteSpace(folder)) return;

        var (device, tracks, error) = await _backend.InspectFixtureVolumeAsync(folder);
        if (error is not null || device is null)
        {
            UsbWatchStatus = error ?? "Unrecognized fixture folder.";
            return;
        }

        _liveHardwareDevices.RemoveAll(d => d.Id == device.Id);
        _liveHardwareDevices.Insert(0, device);
        _allTracks.RemoveAll(t => t.DeviceInstanceId == device.LiveIdentity?.DeviceInstanceId);
        _allTracks.AddRange(tracks);

        RebuildAllLocations();
        RebuildDeviceCards();
        SelectDevice(device);
        CheckAndSurfaceBpbWarnings([device]);
        CheckAndSurfaceSupportTierWarnings([device], _backend.LastUnrecognizedSonyProbes);
        UsbWatchStatus = $"Mounted fixture {device.Model} from {folder}";
    }

    [RelayCommand]
    private async Task SafeEjectAsync()
    {
        string? ejectedModel = ConnectedDevice?.Model;
        if (ConnectedDevice is { IsLiveHardware: true } live)
        {
            ClearBpbWarningNotification(live.Id);
            ClearSupportTierWarningNotification(live.Id);
            await _backend.SafeEjectDeviceAsync(live);
            _liveHardwareDevices.RemoveAll(d => d.Id == live.Id);
            _allTracks.RemoveAll(t => t.DeviceInstanceId == live.LiveIdentity?.DeviceInstanceId);
        }

        ConnectedDevice = null;
        if (ActiveModuleId == ModuleIds.FileManager)
        {
            ReturnToStartHub();
        }

        RebuildAllLocations();
        RebuildDeviceCards();
        RefreshAllLists();
        RefreshTechnicalInfoAndProbes();
        NotifyAllSummaryProps();
        if (!string.IsNullOrEmpty(ejectedModel))
        {
            ShowToast("Safe Eject Complete", $"{ejectedModel} ejected cleanly.", ToastSeverity.Info);
        }
    }

    public void SelectDevice(WalkmanDeviceSummary? device)
    {
        ConnectedDevice = device;
        if (device is not null)
        {
            var destMatch = AllLocations.FirstOrDefault(l => l.Id == device.Id);
            if (destMatch is not null)
            {
                SelectedDestLocation = destMatch;
            }
            OledTintMode = device.ScreenTint;
        }

        foreach (var card in DeviceCards)
        {
            card.IsConnected = device is not null && card.Profile.Id == device.Id;
        }

        RefreshFmGroupsForDest(DestDeviceSummary);
        RebuildFlatFeatures();
        RefreshTechnicalInfoAndProbes();
        NotifyAllSummaryProps();
        NotifyFmStorageProjection();
        OnPropertyChanged(nameof(HasDestDeviceSummary));
        OnPropertyChanged(nameof(DestDeviceSummary));
        CheckAndSurfaceBpbWarningForDevice(device);
        CheckAndSurfaceSupportTierWarningForDevice(device);
    }

    public void OpenModule(string moduleId)
    {
        if (moduleId is ModuleIds.CustomOled or ModuleIds.CustomSubtitles or ModuleIds.CustomTags)
        {
            ActiveCustomTab = moduleId switch
            {
                ModuleIds.CustomSubtitles => "subtitles",
                ModuleIds.CustomTags => "tags",
                _ => "oled"
            };
        }
        else if (moduleId is ModuleIds.DeviceSystem or ModuleIds.PairSync or ModuleIds.CustomFirmware or ModuleIds.DeviceDumpWizard or ModuleIds.DeviceTechInfo)
        {
            ActiveDeviceTab = moduleId switch
            {
                ModuleIds.CustomFirmware => "firmware",
                ModuleIds.DeviceDumpWizard => "dump",
                ModuleIds.DeviceTechInfo => "tech",
                _ => "pair"
            };
        }

        if (StartPhase == StartScreenPhase.IdleBlobs)
        {
            StartPhase = StartScreenPhase.MedallionReady;
        }

        ActiveModuleId = moduleId;
        IsJogDialOpen = false;
    }

    public void ReturnToStartHub()
    {
        IsJogDialOpen = false;
        ActiveModuleId = null;
        StartPhase = StartScreenPhase.MedallionReady;
    }

    // File Manager Commands
    [RelayCommand]
    private void SwapTransferEndpoints()
    {
        if (SelectedSourceLocation is null ||
            !string.Equals(SelectedSourceLocation.Kind, "device", StringComparison.OrdinalIgnoreCase) ||
            !DestLocations.Any(d => string.Equals(d.Id, SelectedSourceLocation.Id, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast("Player Required", "Import Destination only accepts Walkman players.", ToastSeverity.Warning);
            return;
        }

        var prevSource = SelectedSourceLocation;
        SelectedSourceLocation = SelectedDestLocation;
        SelectedDestLocation = prevSource;
        RefreshFileManagerLists();
    }

    [RelayCommand]
    private void SetUploadMode(UploadMode mode)
    {
        if (!IsFmModeLocked)
        {
            FmUploadMode = mode;
        }
    }

    [RelayCommand]
    private void ToggleFmOption(string optionKey)
    {
        switch (optionKey)
        {
            case "norm" when !IsFmNormLocked: FmNormalizeVolume = !FmNormalizeVolume; break;
            case "tables" when !IsFmTablesLocked: FmRewriteTables = !FmRewriteTables; break;
            case "lock" when !IsFmLockTrackLocked: FmRewriteLock = !FmRewriteLock; break;
            case "eject" when !IsFmEjectLocked: FmSafeEjectAfter = !FmSafeEjectAfter; break;
        }
        PersistSettings();
    }

    [RelayCommand]
    private void ToggleFmRememberChoice() => FmRememberChoice = !FmRememberChoice;

    [RelayCommand]
    private async Task BrowseFmLocationAsync(string side)
    {
        if (!string.Equals(side, "left", StringComparison.OrdinalIgnoreCase))
        {
            await MountFolderFixtureAsync();
            return;
        }

        string? folder = PickFolderAsyncHandler is not null
            ? await PickFolderAsyncHandler("Select Export Source Folder")
            : @"D:\Audio\FLAC Vault";

        if (string.IsNullOrWhiteSpace(folder)) return;

        string id = $"custom:{folder}";
        var existing = AllLocations.FirstOrDefault(l => l.Id == id);
        if (existing is null)
        {
            existing = new TransferLocationItem(id, folder, "folder", "Custom local folder", folder);
            _customFolderLocations.Add(existing);
            LoadRealTracksFromFolder(folder, id);
            RebuildAllLocations();
        }

        if (side == "left")
        {
            SelectedSourceLocation = existing;
        }
        else
        {
            SelectedDestLocation = existing;
        }

        FileManagerHint = $"Selected {folder}";
        RefreshAllLists();
    }

    [RelayCommand]
    private async Task StartTransferAsync()
    {
        var selected = GetUnfilteredFileManagerSourceTracks().Where(t => t.IsSelected).ToList();
        if (selected.Count == 0 || !CanStartTransfer)
        {
            return;
        }

        selected = OrderSelectedTracksForTransfer(selected);
        StopDemoTransferTimer();

        var destDevice = DestDeviceSummary;
        bool hasRealFiles = selected.All(t => !string.IsNullOrWhiteSpace(t.SourceFilePath) && File.Exists(t.SourceFilePath));

        if (destDevice is { IsLiveHardware: true, LiveIdentity: not null } && hasRealFiles)
        {
            if (FmUploadMode == UploadMode.Custom &&
                FmCustomCodec.Codec is AudioCodecId.Atrac3PlusLossless or AudioCodecId.Atrac3Lossless)
            {
                ShowToast("AAL Is Import-Only", "AAL (ATRAC Advanced Lossless) is import/decode-only and cannot be encoded or written to the player.", ToastSeverity.Warning);
                return;
            }

            AudioCodecId targetCodec = FmUploadMode switch
            {
                UploadMode.Quality => AudioCodecId.Atrac3Plus,
                UploadMode.Faster => AudioCodecId.Atrac3Plus,
                UploadMode.Custom => FmCustomCodec.Codec,
                _ => AudioCodecId.Mp3
            };
            int bitrate = FmUploadMode switch
            {
                UploadMode.Quality => 352,
                UploadMode.Faster => 192,
                UploadMode.Custom => FmCustomCodec.BitrateKbps,
                _ => 192
            };

            TransferJob = new ActiveTransferJob(
                Guid.NewGuid().ToString("N"),
                selected[0].Title,
                "Transcoding & writing OMGAUDIO",
                FileManagerStateLogic.DescribeEffectiveCodec(CurrentFmConfig),
                0,
                selected.Count,
                5,
                selected.Count * 4,
                false);

            string groupName = string.IsNullOrWhiteSpace(FmTargetGroupName) ? "Apollo Transfer" : FmTargetGroupName.Trim();
            var orderedSelected = OrderSelectedTracksForTransfer(selected);
            var requests = orderedSelected
                .Select(t =>
                {
                    string grp = GetStagedCollectionName(t.Id);
                    int? targetPos = GetStagedTrackTargetIndex(t.Id, grp);
                    return new SyncTrackRequest(t.SourceFilePath!, targetCodec, bitrate, grp, TargetPositionIndex: targetPos);
                })
                .ToArray();

            var result = await _backend.SyncTracksToLiveDeviceAsync(
                destDevice.LiveIdentity,
                requests,
                orderedGroupNames: GetDeviceCollectionOrder(destDevice.Id));

            ClearStagedSelectionsAfterTransfer(destDevice.Id, orderedSelected, result);
            if (result.FailedCount == 0)
            {
                HasPendingDeviceTableChanges = false;
            }

            await RescanUsbHardwareAsync();
            NotifySelectionChanged();
            NotifyDestSelectionChanged();
            NotifyFmStorageProjection();
            RebuildV3ListEntries();

            string? firstRawError = result.Errors.FirstOrDefault();
            string dockStageLabel = result.FailedCount == 0
                ? $"Committed to OMGAUDIO ({groupName})"
                : SanitizeTransferErrorForDock(firstRawError);

            TransferJob = new ActiveTransferJob(
                Guid.NewGuid().ToString("N"),
                result.FailedCount == 0 ? $"{result.SucceededCount} file(s) transferred" : $"{result.SucceededCount} ok, {result.FailedCount} failed",
                dockStageLabel,
                FileManagerStateLogic.DescribeEffectiveCodec(CurrentFmConfig),
                result.SucceededCount,
                selected.Count,
                100,
                0,
                IsCompleted: result.FailedCount == 0,
                IsError: result.FailedCount > 0,
                ErrorText: result.FailedCount > 0 ? firstRawError : null);

            if (result.FailedCount > 0 &&
                targetCodec is AudioCodecId.Atrac3 or AudioCodecId.Atrac3Plus &&
                result.Errors.Any(e => e.Contains("0x0001", StringComparison.OrdinalIgnoreCase) || e.Contains("MACLIST0", StringComparison.OrdinalIgnoreCase)))
            {
                var bridgeCap = _backend.ProbeOpenMgIcvBridgeCapability();
                if (bridgeCap is { IsWorkerAvailable: true, IsHostRuntimePresent: false })
                {
                    _pendingSlotAllocationCount = Math.Clamp(result.FailedCount, 1, 512);
                    OpenMgRuntimePromptMessage = bridgeCap.StatusMessage;
                    IsOpenMgRuntimePromptOpen = true;
                }
            }

            ShowToast(
                result.FailedCount == 0 ? "Transfer Complete" : "Transfer Partial",
                result.FailedCount == 0
                    ? $"{result.SucceededCount}/{selected.Count} track(s) sent to {destDevice.Model}."
                    : $"{result.SucceededCount}/{selected.Count} track(s) sent to {destDevice.Model}. {dockStageLabel}",
                result.FailedCount == 0 ? ToastSeverity.Success : ToastSeverity.Warning);

            if (FmSafeEjectAfter)
            {
                await SafeEjectAsync();
            }

            return;
        }

        // Smooth interactive progress for sample / demo tracks
        RunSimulatedBatchJob(
            selected,
            FileManagerStateLogic.DescribeEffectiveCodec(CurrentFmConfig).Split(' ')[0],
            FmNormalizeVolume ? "Normalizing & encoding" : "Transcoding",
            FmRewriteTables ? "Writing OMGAUDIO tables" : "Writing to destination",
            onFinish: () =>
            {
                string destId = SelectedDestLocation?.Id ?? "nw-hd5";
                string targetGroup = string.IsNullOrWhiteSpace(FmTargetGroupName) ? "Apollo Transfer" : FmTargetGroupName.Trim();
                foreach (var trk in selected)
                {
                    if (!trk.Locations.Contains(destId)) trk.Locations.Add(destId);
                    trk.GroupName = GetStagedCollectionName(trk.Id);
                    trk.IsSelected = false;
                    _stagedTrackGroups.Remove(trk.Id);
                    RemoveStagedIdFromAllOrders(destId, trk.Id);
                    if (FmRewriteLock) trk.ProtectedLock = true;
                }

                if (FmSafeEjectAfter)
                {
                    ConnectedDevice = null;
                    RebuildDeviceCards();
                    if (ActiveModuleId == ModuleIds.FileManager)
                    {
                        ReturnToStartHub();
                    }
                }

                NotifySelectionChanged();
                RefreshAllLists();
                ShowToast("Transfer Complete", $"Sent {selected.Count} track(s) into group '{targetGroup}'.", ToastSeverity.Success);
                return FmSafeEjectAfter ? "Completed · Device ejected" : "Transfer complete";
            });
    }

    // Codec Converter Commands
    [RelayCommand]
    private void SetConverterCodec(TargetCodec codec)
    {
        if (IsConvCodecLocked) return;
        // ATRAC3 / ATRAC1 target encoding is gated for v0.2.0b; keep v0.1.1b users on verified ATRAC3+.
        if (codec is TargetCodec.Atrac3 or TargetCodec.Atrac1)
        {
            codec = TargetCodec.Atrac3Plus;
        }

        ConvTargetCodec = codec;
        if (ConverterStateLogic.IsLosslessCodec(codec))
        {
            ConvBitrate = BitrateOption.Lossless;
        }
        else if (codec == TargetCodec.Atrac3)
        {
            if (ConvBitrate is not (BitrateOption.K132 or BitrateOption.K105 or BitrateOption.K66))
            {
                ConvBitrate = BitrateOption.K132;
            }
        }
        else if (codec == TargetCodec.Atrac1)
        {
            ConvBitrate = BitrateOption.K292;
        }
        else if (ConvBitrate is BitrateOption.Lossless or BitrateOption.K292 or BitrateOption.K132 or BitrateOption.K105 or BitrateOption.K66)
        {
            ConvBitrate = BitrateOption.K256;
        }

        RefreshConverterProjectedOutputs();
        RebuildV3ListEntries();
    }

    [RelayCommand]
    private void SetConverterBitrate(BitrateOption bitrate)
    {
        if (IsConvBitrateLocked) return;
        ConvBitrate = bitrate;
        RefreshConverterProjectedOutputs();
        RebuildV3ListEntries();
    }

    [RelayCommand]
    private void SetConverterSampleRate(SampleRateOption rate)
    {
        if (!IsConvRateLocked)
        {
            ConvSampleRate = rate;
        }
    }

    [RelayCommand]
    private void ToggleConverterOption(string optionKey)
    {
        switch (optionKey)
        {
            case "norm" when !IsConvNormLocked: ConvNormalizeVolume = !ConvNormalizeVolume; break;
            case "meta" when !IsConvMetaLocked: ConvPreserveMetadata = !ConvPreserveMetadata; break;
            case "keep" when !IsConvKeepLocked: ConvKeepSourceFiles = !ConvKeepSourceFiles; break;
            case "add" when !IsConvAddLocked: ConvAddToLibrary = !ConvAddToLibrary; break;
        }
        PersistSettings();
    }

    [RelayCommand]
    private void ToggleConverterRememberChoice() => ConvRememberChoice = !ConvRememberChoice;

    [RelayCommand]
    private async Task BrowseConverterFolderAsync(string side)
    {
        if (PickFolderAsyncHandler is null) return;
        string? folder = await PickFolderAsyncHandler($"Select {(side == "source" ? "Input Source" : "Output Destination")} Folder");
        if (string.IsNullOrWhiteSpace(folder)) return;

        string id = $"custom:{folder}";
        var existing = AllLocations.FirstOrDefault(l => l.Id == id);
        if (existing is null)
        {
            existing = new TransferLocationItem(id, folder, "folder", "Selected via OS dialog", folder);
            _customFolderLocations.Add(existing);
            if (side == "source") LoadRealTracksFromFolder(folder, id);
            RebuildAllLocations();
        }

        if (side == "source") ConverterSourceLocation = existing;
        else ConverterOutputLocation = existing;
        RefreshConverterLists();
    }

    [RelayCommand]
    private async Task StartConversionAsync()
    {
        var selected = ConverterSourceTracks.Where(t => t.IsSelected).ToList();
        if (selected.Count == 0 || IsTransferRunning)
        {
            return;
        }

        StopDemoTransferTimer();
        string profileLabel = ConverterStateLogic.DescribeConverterProfile(CurrentConverterConfig);

        string outDir = ConverterOutputLocation?.DirectoryPath ?? ConvertedOutputFolder;
        bool allHaveDiskSource = selected.All(t => !string.IsNullOrWhiteSpace(t.SourceFilePath) && File.Exists(t.SourceFilePath));
        if (allHaveDiskSource && ConvTargetCodec == TargetCodec.Aal)
        {
            ShowToast("AAL Is Import-Only", "AAL (ATRAC Advanced Lossless) is import/decode-only; no open-source AAL encoder exists. Select ATRAC3+, MP3, WAV, or FLAC.", ToastSeverity.Warning);
            return;
        }

        if (allHaveDiskSource && ConvTargetCodec is TargetCodec.Atrac3Plus or TargetCodec.Atrac3 or TargetCodec.Atrac1 or TargetCodec.Mp3 or TargetCodec.Wav or TargetCodec.Flac)
        {
            int done = 0;
            foreach (var trk in selected)
            {
                TransferJob = new ActiveTransferJob(
                    "conv",
                    trk.Title,
                    ConvNormalizeVolume ? "Normalizing & encoding" : "Offline encoding",
                    profileLabel,
                    done,
                    selected.Count,
                    Math.Clamp((int)Math.Round(((done + 0.4) / selected.Count) * 100), 5, 95),
                    (selected.Count - done) * 3,
                    false);

                await _backend.TranscodeOfflineAudioAsync(
                    trk,
                    outDir,
                    ConvTargetCodec,
                    ConverterStateLogic.GetBitrateKbps(ConvBitrate));
                done++;
            }

            TransferJob = new ActiveTransferJob(
                "conv",
                $"{selected.Count} file(s) encoded",
                "Conversion complete",
                profileLabel,
                selected.Count,
                selected.Count,
                100,
                0,
                true);
            return;
        }

        RunSimulatedBatchJob(
            selected,
            profileLabel,
            ConvNormalizeVolume ? "Normalizing & encoding" : "Offline encoding",
            ConvPreserveMetadata ? "Embedding ID3/OMA tags" : "Finalizing audio file",
            onFinish: () =>
            {
                string outLocId = ConverterOutputLocation?.Id ?? "pc-converted";
                foreach (var trk in selected)
                {
                    if (!trk.Locations.Contains(outLocId)) trk.Locations.Add(outLocId);
                }

                RefreshAllLists();
                return "Conversion complete";
            });
    }

    // Library Commands
    [RelayCommand]
    private void SetLibraryFilter(string filter)
    {
        LibraryFormatFilter = filter;
        RefreshLibraryList();
    }

    [RelayCommand]
    private async Task ChangeLibraryFolderAsync()
    {
        if (PickFolderAsyncHandler is not null)
        {
            string? picked = await PickFolderAsyncHandler("Select Local Music Library Folder");
            if (!string.IsNullOrWhiteSpace(picked))
            {
                DefaultMusicFolder = picked;
                LibraryFolderDisplay = picked;
                LoadRealTracksFromFolder(picked, "pc-library");
                RebuildAllLocations();
                RefreshAllLists();
                RefreshCustomizationTracks();
                NotifyAllSummaryProps();
                PersistSettings();
                ShowToast("Library Folder Updated", picked, ToastSeverity.Success);
            }
        }
    }

    [RelayCommand]
    private async Task BrowseDefaultLibrarySettingsAsync() => await ChangeLibraryFolderAsync();

    [RelayCommand]
    private async Task BrowseConvertedFolderSettingsAsync()
    {
        if (PickFolderAsyncHandler is null)
        {
            return;
        }

        string? picked = await PickFolderAsyncHandler("Select Default Converted Output Folder");
        if (!string.IsNullOrWhiteSpace(picked))
        {
            ConvertedOutputFolder = picked;
            RebuildAllLocations();
            RefreshAllLists();
            PersistSettings();
            ShowToast("Output Folder Updated", picked, ToastSeverity.Success);
        }
    }

    public Bitmap? SelectedTagTrackCover => SelectedTagTrack?.CoverArtwork;
    public bool HasSelectedTagTrackCover => SelectedTagTrack?.CoverArtwork is not null;

    [RelayCommand]
    private async Task ChooseTrackCoverAsync()
    {
        var targets = CustomizationTracks.Where(t => t.IsTagSelected).ToList();
        if (targets.Count == 0 && SelectedTagTrack is not null)
        {
            targets.Add(SelectedTagTrack);
        }

        if (targets.Count == 0)
        {
            ShowToast("No Track Selected", "Select one or more tracks before choosing cover art.", ToastSeverity.Warning);
            return;
        }

        if (PickOpenGenericFileAsyncHandler is null)
        {
            return;
        }

        string? imagePath = await PickOpenGenericFileAsyncHandler("Select Cover Artwork", "Image Files", "*.jpg;*.jpeg;*.png;*.webp;*.bmp");
        if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
        {
            return;
        }

        int saved = 0;
        foreach (var trk in targets)
        {
            if (!string.IsNullOrWhiteSpace(trk.SourceFilePath) && File.Exists(trk.SourceFilePath) &&
                WalkmanBackendService.TrySaveLocalTrackCover(trk.SourceFilePath, imagePath, out var bmp))
            {
                trk.CoverArtwork = bmp;
                saved++;
            }
        }

        OnPropertyChanged(nameof(SelectedTagTrackCover));
        OnPropertyChanged(nameof(HasSelectedTagTrackCover));
        OnPropertyChanged(nameof(PlayerCoverBitmap));
        OnPropertyChanged(nameof(PlayerHasCoverBitmap));
        CustomizationFeedback = saved > 0
            ? $"Embedded cover art into {saved} track(s)"
            : "Could not embed cover art (no local file path).";
        ShowToast(saved > 0 ? "Cover Art Updated" : "Cover Update Skipped", CustomizationFeedback, saved > 0 ? ToastSeverity.Success : ToastSeverity.Warning);
    }

    [RelayCommand]
    private void RemoveTrackCover()
    {
        var targets = CustomizationTracks.Where(t => t.IsTagSelected).ToList();
        if (targets.Count == 0 && SelectedTagTrack is not null)
        {
            targets.Add(SelectedTagTrack);
        }

        if (targets.Count == 0)
        {
            return;
        }

        int removed = 0;
        foreach (var trk in targets)
        {
            if (!string.IsNullOrWhiteSpace(trk.SourceFilePath) && File.Exists(trk.SourceFilePath))
            {
                WalkmanBackendService.TryRemoveLocalTrackCover(trk.SourceFilePath);
            }

            if (trk.CoverArtwork is not null)
            {
                trk.CoverArtwork = null;
                removed++;
            }
        }

        OnPropertyChanged(nameof(SelectedTagTrackCover));
        OnPropertyChanged(nameof(HasSelectedTagTrackCover));
        OnPropertyChanged(nameof(PlayerCoverBitmap));
        OnPropertyChanged(nameof(PlayerHasCoverBitmap));
        CustomizationFeedback = $"Removed cover art from {Math.Max(removed, targets.Count)} track(s)";
        ShowToast("Cover Art Removed", CustomizationFeedback, ToastSeverity.Info);
    }

    // Playback Commands
    public void PlayOrToggleTrack(AudioTrackItem track)
    {
        _player.PlayOrToggle(track);
    }

    [RelayCommand]
    private void ToggleGlobalPlayPause() => _player.TogglePlayPause();

    [RelayCommand]
    private void PlayPreviousTrack()
    {
        if (HandleRewindOrPrevious())
        {
            StepTrack(-1);
        }
    }

    [RelayCommand]
    private void PlayNextTrack() => StepTrack(1);

    [RelayCommand]
    private void CloseGlobalPlayer() => _player.Stop();

    public void SeekPlayer(double seconds) => _player.Seek(seconds);
    public void AttachWindowHandleForSmtc(IntPtr hwnd) => _player.AttachWindowHandleForSmtc(hwnd);
    public bool TryConsumeExternalMediaKeyGuard() => _player.TryConsumeExternalMediaKeyGuard();

    // Customization Commands
    [RelayCommand]
    private void SetCustomTab(string tab)
    {
        ActiveCustomTab = tab;
        ActiveModuleId = tab switch
        {
            "subtitles" => ModuleIds.CustomSubtitles,
            "tags" => ModuleIds.CustomTags,
            _ => ModuleIds.CustomOled
        };
    }

    [RelayCommand]
    private void SetOledAnimation(string mode) => OledAnimationMode = mode;

    [RelayCommand]
    private void SetOledTint(ScreenTintMode tint) => OledTintMode = tint;

    [RelayCommand]
    private async Task LoadNwmFileAsync()
    {
        if (PickNwmOpenFileAsyncHandler is null) return;
        string? file = await PickNwmOpenFileAsyncHandler();
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file)) return;

        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(file);
            var dto = await _backend.UnpackScreensaverAsync(bytes);
            if (dto is not null)
            {
                LoadedNwmProject = dto;
                OledAnimationMode = "custom";
                NwmProjectSummary = $"Loaded '{dto.Name}' by {dto.Author} ({dto.Timeline.Count} frames, {dto.UniqueBitmaps.Count} bitmaps, {bytes.Length:N0} B)";
                CustomizationFeedback = $"Unpacked {Path.GetFileName(file)}";
            }
        }
        catch (Exception ex)
        {
            CustomizationFeedback = $"Invalid .nwm: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task ExportNwmFileAsync()
    {
        var dto = LoadedNwmProject ?? BuildDemoNwmProject();
        byte[]? nwmBytes = await _backend.PackScreensaverAsync(dto);
        if (nwmBytes is null)
        {
            CustomizationFeedback = "L2.Custom.NwmEngine plugin unavailable.";
            return;
        }

        string defaultName = $"{dto.Name.Replace(' ', '_')}.nwm";
        string? savePath = PickNwmSaveFileAsyncHandler is not null
            ? await PickNwmSaveFileAsyncHandler(defaultName)
            : Path.Combine(ConvertedOutputFolder, defaultName);

        if (string.IsNullOrWhiteSpace(savePath)) return;

        Directory.CreateDirectory(Path.GetDirectoryName(savePath)!);
        await File.WriteAllBytesAsync(savePath, nwmBytes);
        CustomizationFeedback = $"Exported {Path.GetFileName(savePath)} ({nwmBytes.Length:N0} B · {dto.UniqueBitmaps.Count} bitmaps)";
    }

    partial void OnAutoBackupOmgaudioBeforeWriteChanged(bool value) => PersistSettings();

    [RelayCommand]
    private void ToggleAutoBackupOmgaudio() => AutoBackupOmgaudioBeforeWrite = !AutoBackupOmgaudioBeforeWrite;

    [RelayCommand]
    private async Task UploadCustomizationToPlayerAsync()
    {
        if (ConnectedDevice is null)
        {
            ShowToast("Player Required", "Connect or pair a player before uploading.", ToastSeverity.Warning);
            return;
        }

        if (!ConnectedDevice.IsLiveHardware || ConnectedDevice.LiveIdentity is not { } identity)
        {
            CustomizationFeedback = ActiveCustomTab switch
            {
                "oled" => $"Screen pack (.NWM) staged for {ConnectedDevice.Model}",
                "subtitles" => $"Synced LRC subtitles to {ConnectedDevice.Model}",
                "tags" => $"Synced OMGAUDIO ID3/OMA metadata to {ConnectedDevice.Model}",
                _ => $"Uploaded to {ConnectedDevice.Model}"
            };
            ShowToast("Uploaded to Player", CustomizationFeedback, ToastSeverity.Success);
            return;
        }

        try
        {
            switch (ActiveCustomTab)
            {
                case "oled":
                {
                    var dto = LoadedNwmProject ?? BuildDemoNwmProject();
                    var res = await _backend.DeployScreensaverToDeviceAsync(
                        identity,
                        dto,
                        backupBeforeWrite: AutoBackupOmgaudioBeforeWrite);
                    if (!res.Success)
                    {
                        CustomizationFeedback = res.ErrorMessage ?? "Screensaver deployment failed.";
                        ShowToast("Upload Failed", CustomizationFeedback, ToastSeverity.Error);
                        return;
                    }

                    CustomizationFeedback = $"Deployed {res.RelativePath} ({res.ByteLength:N0} B) to {ConnectedDevice.Model}";
                    ShowToast("Screensaver Deployed", CustomizationFeedback, ToastSeverity.Success);
                    break;
                }
                case "subtitles":
                {
                    var trk = SelectedSubtitleTrack;
                    if (trk is null || !trk.DeviceTrackId.HasValue || !string.Equals(trk.DeviceInstanceId, identity.DeviceInstanceId, StringComparison.OrdinalIgnoreCase))
                    {
                        ShowToast("Device Track Required", "Select a track from the connected player to commit .LRC subtitles.", ToastSeverity.Warning);
                        return;
                    }

                    string[] lines = SubtitlesLrcText
                        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                        .Select(l => l.Trim())
                        .Where(l => !string.IsNullOrWhiteSpace(l))
                        .ToArray();
                    string relPath = await _backend.CommitDeviceLrcAsync(identity, trk.DeviceTrackId.Value, lines);
                    trk.SubtitleLines = lines;
                    CustomizationFeedback = $"Committed {lines.Length} LRC lines to {relPath}";
                    ShowToast("Subtitles Committed", CustomizationFeedback, ToastSeverity.Success);
                    break;
                }
                case "tags":
                {
                    var targets = CustomizationTracks
                        .Where(t => t.IsTagSelected && t.DeviceTrackId.HasValue && string.Equals(t.DeviceInstanceId, identity.DeviceInstanceId, StringComparison.OrdinalIgnoreCase))
                        .ToList();
                    if (targets.Count == 0 && SelectedTagTrack is { DeviceTrackId: not null } single &&
                        string.Equals(single.DeviceInstanceId, identity.DeviceInstanceId, StringComparison.OrdinalIgnoreCase))
                    {
                        targets.Add(single);
                    }

                    if (targets.Count == 0)
                    {
                        ShowToast("Device Track Required", "Select one or more tracks on the connected player to commit metadata.", ToastSeverity.Warning);
                        return;
                    }

                    int? parsedYear = int.TryParse(TagEditYear.Trim(), out int yr) && yr > 0 ? yr : null;
                    int? parsedTrackNum = int.TryParse(TagEditTrackNumber.Trim(), out int tn) && tn > 0 ? tn : null;
                    bool multi = targets.Count > 1;
                    var patches = targets.Select(t => new TrackMetadataPatch(
                        TrackId: t.DeviceTrackId!.Value,
                        Title: !multi && !string.IsNullOrWhiteSpace(TagEditTitle) ? TagEditTitle.Trim() : null,
                        Artist: !string.IsNullOrWhiteSpace(TagEditArtist) ? TagEditArtist.Trim() : null,
                        Album: !string.IsNullOrWhiteSpace(TagEditAlbum) ? TagEditAlbum.Trim() : null,
                        Genre: !string.IsNullOrWhiteSpace(TagEditGenre) ? TagEditGenre.Trim() : null,
                        SortTitle: !multi && !string.IsNullOrWhiteSpace(TagEditSortTitle) ? TagEditSortTitle.Trim() : null,
                        Year: parsedYear,
                        TrackNumber: !multi ? parsedTrackNum : null,
                        AlbumArtist: !string.IsNullOrWhiteSpace(TagEditAlbumArtist) ? TagEditAlbumArtist.Trim() : null,
                        Composer: !string.IsNullOrWhiteSpace(TagEditComposer) ? TagEditComposer.Trim() : null,
                        Rating: TagEditRating > 0 ? TagEditRating : null,
                        Comment: !string.IsNullOrWhiteSpace(TagEditComment) ? TagEditComment.Trim() : null,
                        ArtistLinks: !string.IsNullOrWhiteSpace(TagEditArtistLinks) ? TagEditArtistLinks.Trim() : null)).ToArray();

                    var (updated, backupPath) = await _backend.CommitDeviceTrackTagsAsync(
                        identity,
                        patches,
                        backupBeforeWrite: AutoBackupOmgaudioBeforeWrite);
                    await RescanUsbHardwareAsync();
                    CustomizationFeedback = backupPath is not null
                        ? $"Committed {updated} track(s) to {ConnectedDevice.Model} (backup saved)"
                        : $"Committed {updated} track(s) to {ConnectedDevice.Model}";
                    ShowToast("Metadata Committed", CustomizationFeedback, ToastSeverity.Success);
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            CustomizationFeedback = $"Upload failed: {ex.Message}";
            ShowToast("Upload Error", ex.Message, ToastSeverity.Error);
        }
    }

    [RelayCommand]
    private void DismissTransferDock()
    {
        StopDemoTransferTimer();
        TransferJob = null;
    }

    public void ToggleTrackSelectionAndRefresh(AudioTrackItem track)
    {
        track.IsSelected = !track.IsSelected;
        NotifySelectionChanged();
    }

    public void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(TransferSelectedCount));
        OnPropertyChanged(nameof(TransferButtonLabel));
        OnPropertyChanged(nameof(CanStartTransfer));
        OnPropertyChanged(nameof(ConverterSelectedCount));
        OnPropertyChanged(nameof(ConvertButtonLabel));
        OnPropertyChanged(nameof(CanStartConvert));
        OnPropertyChanged(nameof(FmSelectedQueueSizeText));
        OnPropertyChanged(nameof(ConvSelectedQueueSizeText));
        NotifyFmStorageProjection();
        RefreshConverterProjectedOutputs();
        UpdateFolderSelectionBadgesInPlace();
        RebuildFileManagerDestEntries();
    }

    // Internal Sync & Refresh Helpers
    partial void OnStartPhaseChanged(StartScreenPhase value)
    {
        OnPropertyChanged(nameof(ShowTopControls));
        OnPropertyChanged(nameof(IsStartHubRingExpanded));
    }

    partial void OnActiveModuleIdChanged(string? value)
    {
        OnPropertyChanged(nameof(IsStartHub));
        OnPropertyChanged(nameof(IsWorkspace));
        OnPropertyChanged(nameof(IsStartHubRingExpanded));
        OnPropertyChanged(nameof(ShowRadialJogDial));
        OnPropertyChanged(nameof(ShowSidebarRail));
        OnPropertyChanged(nameof(ShowTopControls));
        OnPropertyChanged(nameof(IsFileManagerActive));
        OnPropertyChanged(nameof(IsCodecConverterActive));
        OnPropertyChanged(nameof(IsLibraryActive));
        OnPropertyChanged(nameof(IsCustomizationActive));
        OnPropertyChanged(nameof(IsOledPreviewActive));
        OnPropertyChanged(nameof(IsDeviceSystemActive));
        OnPropertyChanged(nameof(IsAllFeaturesActive));
        OnPropertyChanged(nameof(IsSettingsActive));
    }
    partial void OnIsDarkChanged(bool value)
    {
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;
        }
        OnPropertyChanged(nameof(ThemePillLabel));
        PersistSettings();
    }
    partial void OnIsJogDialOpenChanged(bool value) => OnPropertyChanged(nameof(IsJogDialBlurActive));
    public bool IsFallbackDiscSpinning => IsPlaying && !PlayerHasCoverBitmap && !ReducedBlur;

    partial void OnReducedBlurChanged(bool value)
    {
        OnPropertyChanged(nameof(IsJogDialBlurActive));
        OnPropertyChanged(nameof(IsFallbackDiscSpinning));
    }
    partial void OnConnectedDeviceChanged(WalkmanDeviceSummary? value)
    {
        OnPropertyChanged(nameof(IsDeviceConnected));
        OnPropertyChanged(nameof(TopUsbPillText));
        OnPropertyChanged(nameof(OledScreenResLabel));
        OnPropertyChanged(nameof(CustomUploadSubtitle));
        OnPropertyChanged(nameof(FirmwareTargetModel));
        OnPropertyChanged(nameof(FirmwareVersionText));
        OnPropertyChanged(nameof(IsFwReadyDevice));
        OnPropertyChanged(nameof(IsFwReadyBattery));
        OnPropertyChanged(nameof(FirmwareDeviceReadinessText));
        OnPropertyChanged(nameof(FirmwareBatteryReadinessText));
        OnPropertyChanged(nameof(CanAllocateAtrac3PlusSlots));
    }
    partial void OnTransferJobChanged(ActiveTransferJob? value)
    {
        OnPropertyChanged(nameof(HasTransferJob));
        OnPropertyChanged(nameof(HasActiveBottomDock));
        OnPropertyChanged(nameof(IsTransferRunning));
        OnPropertyChanged(nameof(IsFileTransferUnlocked));
        OnPropertyChanged(nameof(CanCommitPendingDeviceChanges));
        OnPropertyChanged(nameof(UpdateDeviceTableButtonLabel));
        OnPropertyChanged(nameof(CanDeleteSelectedDest));
        OnPropertyChanged(nameof(CanStartTransfer));
        OnPropertyChanged(nameof(CanCommitPendingDeviceChanges));
        OnPropertyChanged(nameof(CanStartConvert));
        OnPropertyChanged(nameof(CanAllocateAtrac3PlusSlots));
        OnPropertyChanged(nameof(TransferButtonLabel));
        OnPropertyChanged(nameof(ConvertButtonLabel));
        OnPropertyChanged(nameof(TransferJobProgressText));
        OnPropertyChanged(nameof(TransferJobEtaText));
        OnPropertyChanged(nameof(TransferJobStageCodec));
        OnPropertyChanged(nameof(TransferJobErrorTooltip));
        OnPropertyChanged(nameof(TransferJobProgressPct));
        OnPropertyChanged(nameof(SummaryEngineTitle));
    }

    private static readonly System.Text.RegularExpressions.Regex WindowsPathRegex =
        new(@"[A-Za-z]:\\(?:[^\\/:*?""<>|\r\n]+\\)*[^\\/:*?""<>|\r\n]*", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex UnixPathRegex =
        new(@"(?<![A-Za-z0-9_])/(?:[^\s/'""]+/)+[^\s'""]+", System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string SanitizeTransferErrorForDock(string? rawError)
    {
        if (string.IsNullOrWhiteSpace(rawError))
        {
            return "Transfer failed";
        }

        string text = rawError.Replace("\r\n", "\n").Replace('\r', '\n');
        int stderrIdx = text.IndexOf("Stderr:", StringComparison.OrdinalIgnoreCase);
        bool hadCliStderr = stderrIdx >= 0;
        if (stderrIdx >= 0)
        {
            text = text[..stderrIdx];
        }

        string firstLine = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault() ?? string.Empty;

        firstLine = WindowsPathRegex.Replace(firstLine, string.Empty);
        firstLine = UnixPathRegex.Replace(firstLine, string.Empty);
        firstLine = System.Text.RegularExpressions.Regex.Replace(firstLine, @"\s{2,}", " ").Trim(' ', ':', ';', '-', '(', ')');

        if (rawError.Contains("0x0001", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("MACLIST0", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("hdwmcom", StringComparison.OrdinalIgnoreCase))
        {
            return "OpenMG 0x0001 slot/runtime error — hover for details";
        }

        if (hadCliStderr ||
            rawError.Contains("ffmpeg", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("atracdenc", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("lame", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("exit code", StringComparison.OrdinalIgnoreCase))
        {
            return "Audio encoder failed — hover for details";
        }

        if (rawError.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase))
        {
            return "Access denied writing to player";
        }

        if (rawError.Contains("disk full", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("not enough space", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("ENOSPC", StringComparison.OrdinalIgnoreCase))
        {
            return "Not enough free space on player";
        }

        if (string.IsNullOrWhiteSpace(firstLine))
        {
            return "Transfer failed — hover for details";
        }

        return firstLine.Length > 76 ? $"{firstLine[..73].TrimEnd()}..." : firstLine;
    }
    partial void OnPlayerTrackChanged(AudioTrackItem? value)
    {
        SyncTrackPlayStates();
        OnPropertyChanged(nameof(HasPlayerTrack));
        OnPropertyChanged(nameof(HasActiveBottomDock));
        OnPropertyChanged(nameof(PlayerDurationSec));
        OnPropertyChanged(nameof(PlayerTimeText));
        OnPropertyChanged(nameof(PlayerHasLrc));
        OnPropertyChanged(nameof(PlayerSubtitleOrMeta));
        OnPropertyChanged(nameof(PlayerCoverBrush));
        OnPropertyChanged(nameof(PlayerCoverBitmap));
        OnPropertyChanged(nameof(PlayerHasCoverBitmap));
        OnPropertyChanged(nameof(IsFallbackDiscSpinning));
    }
    partial void OnIsPlayingChanged(bool value)
    {
        SyncTrackPlayStates();
        OnPropertyChanged(nameof(IsFallbackDiscSpinning));
    }
    private void SyncTrackPlayStates()
    {
        foreach (var t in _allTracks)
        {
            bool current = ReferenceEquals(t, PlayerTrack);
            t.IsNowPlaying = current;
            t.IsActivelyPlaying = current && IsPlaying;
        }
        UpdateFolderPlayStateBadgesInPlace();
    }
    partial void OnElapsedSecChanged(int value)
    {
        OnPropertyChanged(nameof(PlayerTimeText));
        OnPropertyChanged(nameof(PlayerSubtitleOrMeta));
    }

    partial void OnSelectedSourceLocationChanged(TransferLocationItem? value) => RefreshFileManagerLists();
    partial void OnSelectedDestLocationChanged(TransferLocationItem? value)
    {
        if (value is not null && !string.Equals(value.Kind, "device", StringComparison.OrdinalIgnoreCase))
        {
            SelectedDestLocation = DestLocations.FirstOrDefault();
            return;
        }

        RefreshFileManagerLists();
        RefreshFmGroupsForDest(DestDeviceSummary);
        NotifyFmStorageProjection();
        OnPropertyChanged(nameof(DestDeviceSummary));
        OnPropertyChanged(nameof(HasDestDeviceSummary));
        OnPropertyChanged(nameof(HasDestSlotPool));
        OnPropertyChanged(nameof(DestSlotPoolText));
        OnPropertyChanged(nameof(CanDeleteSelectedDest));
        OnPropertyChanged(nameof(CanStartTransfer));
        OnPropertyChanged(nameof(CanCommitPendingDeviceChanges));
    }
    partial void OnFileManagerLeftSearchChanged(string value)
    {
        OnPropertyChanged(nameof(FmSourceSearchQuery));
        RefreshFileManagerSourceList();
    }
    partial void OnFileManagerRightSearchChanged(string value)
    {
        OnPropertyChanged(nameof(FmDestSearchQuery));
        RefreshFileManagerDestList();
    }
    partial void OnFmUploadModeChanged(UploadMode value)
    {
        OnPropertyChanged(nameof(TransferCodecSubtitle));
        OnPropertyChanged(nameof(FmModeHint));
        OnPropertyChanged(nameof(IsUploadQuality));
        OnPropertyChanged(nameof(IsUploadFaster));
        OnPropertyChanged(nameof(IsUploadAuto));
        OnPropertyChanged(nameof(IsUploadCustom));
        NotifyFmStates();
        NotifyFmStorageProjection();
        OnPropertyChanged(nameof(FmSelectedQueueSizeText));
        PersistSettings();
    }
    partial void OnFmCustomCodecChanged(CustomCodecSpec value)
    {
        OnPropertyChanged(nameof(TransferCodecSubtitle));
        NotifyFmStorageProjection();
        OnPropertyChanged(nameof(FmSelectedQueueSizeText));
        PersistSettings();
    }
    partial void OnFmRememberChoiceChanged(bool value)
    {
        OnPropertyChanged(nameof(IsFmModeLocked));
        OnPropertyChanged(nameof(IsFmNormLocked));
        OnPropertyChanged(nameof(IsFmTablesLocked));
        OnPropertyChanged(nameof(IsFmLockTrackLocked));
        OnPropertyChanged(nameof(IsFmEjectLocked));
        OnPropertyChanged(nameof(RememberChoiceLabel));
        NotifyFmStates();
        PersistSettings();
    }

    partial void OnConverterSourceLocationChanged(TransferLocationItem? value) => RefreshConverterLists();
    partial void OnConverterSearchChanged(string value) => RefreshConverterLists();
    partial void OnConvTargetCodecChanged(TargetCodec value)
    {
        OnPropertyChanged(nameof(ConverterSubtitle));
        OnPropertyChanged(nameof(ConverterCodecHint));
        OnPropertyChanged(nameof(IsLosslessTarget));
        OnPropertyChanged(nameof(ShowLossyBitrates));
        OnPropertyChanged(nameof(ConverterBitrateRightText));
        OnPropertyChanged(nameof(ConverterBitrateHeader));
        OnPropertyChanged(nameof(IsTargetAtrac3Plus));
        OnPropertyChanged(nameof(IsTargetAtrac3));
        OnPropertyChanged(nameof(IsTargetAtrac1));
        OnPropertyChanged(nameof(IsTargetMp3));
        OnPropertyChanged(nameof(IsTargetAal));
        OnPropertyChanged(nameof(IsTargetAac));
        OnPropertyChanged(nameof(IsTargetFlac));
        OnPropertyChanged(nameof(IsTargetWav));
        OnPropertyChanged(nameof(ShowStandardBitrates));
        OnPropertyChanged(nameof(ShowAtrac3Bitrates));
        OnPropertyChanged(nameof(ShowAtrac1Bitrate));
        NotifyConvStates();
        PersistSettings();
    }
    partial void OnConvBitrateChanged(BitrateOption value)
    {
        OnPropertyChanged(nameof(ConverterSubtitle));
        OnPropertyChanged(nameof(ConverterBitrateRightText));
        OnPropertyChanged(nameof(IsBitrate352));
        OnPropertyChanged(nameof(IsBitrate292));
        OnPropertyChanged(nameof(IsBitrate256));
        OnPropertyChanged(nameof(IsBitrate192));
        OnPropertyChanged(nameof(IsBitrate132));
        OnPropertyChanged(nameof(IsBitrate128));
        OnPropertyChanged(nameof(IsBitrate105));
        OnPropertyChanged(nameof(IsBitrate66));
        NotifyConvStates();
        PersistSettings();
    }
    partial void OnConvSampleRateChanged(SampleRateOption value)
    {
        OnPropertyChanged(nameof(ConverterSubtitle));
        OnPropertyChanged(nameof(IsRate44100));
        OnPropertyChanged(nameof(IsRate48000));
        NotifyConvStates();
        PersistSettings();
    }
    partial void OnConvRememberChoiceChanged(bool value)
    {
        OnPropertyChanged(nameof(IsConvCodecLocked));
        OnPropertyChanged(nameof(IsConvBitrateLocked));
        OnPropertyChanged(nameof(IsConvRateLocked));
        OnPropertyChanged(nameof(IsConvNormLocked));
        OnPropertyChanged(nameof(IsConvMetaLocked));
        OnPropertyChanged(nameof(IsConvKeepLocked));
        OnPropertyChanged(nameof(IsConvAddLocked));
        OnPropertyChanged(nameof(ConverterRememberLabel));
        NotifyConvStates();
        PersistSettings();
    }

    partial void OnLibraryFormatFilterChanged(string value)
    {
        OnPropertyChanged(nameof(IsLibFilterAll));
        OnPropertyChanged(nameof(IsLibFilterFlac));
        OnPropertyChanged(nameof(IsLibFilterAtrac));
        OnPropertyChanged(nameof(IsLibFilterMp3));
        OnPropertyChanged(nameof(IsLibFilterWav));
        RefreshLibraryList();
    }
    partial void OnLibrarySearchChanged(string value) => RefreshLibraryList();
    partial void OnActiveCustomTabChanged(string value)
    {
        OnPropertyChanged(nameof(IsCustomOledTab));
        OnPropertyChanged(nameof(IsOledPreviewActive));
        OnPropertyChanged(nameof(IsCustomSubtitlesTab));
        OnPropertyChanged(nameof(IsCustomTagsTab));
        OnPropertyChanged(nameof(CustomUploadButtonLabel));
    }
    partial void OnOledAnimationModeChanged(string value)
    {
        OnPropertyChanged(nameof(IsAnimSpectrum));
        OnPropertyChanged(nameof(IsAnimOrbit));
        OnPropertyChanged(nameof(IsAnimWave));
        OnPropertyChanged(nameof(IsAnimCustom));
        OnPropertyChanged(nameof(OledScreenResLabel));
    }
    partial void OnLoadedNwmProjectChanged(NwmProjectDto? value) => OnPropertyChanged(nameof(OledScreenResLabel));
    partial void OnOledTintModeChanged(ScreenTintMode value)
    {
        OnPropertyChanged(nameof(IsTintCyan));
        OnPropertyChanged(nameof(IsTintLime));
        OnPropertyChanged(nameof(IsTintAmber));
        OnPropertyChanged(nameof(OledTintLabel));
    }

    private void RebuildAllLocations()
    {
        bool hasLocalPc = _allTracks.Any(t => !t.IsDemoItem && t.Locations.Contains("pc-library"));
        string defaultSrcId = !hasLocalPc && IsDemoModeEnabled ? DemoModeOverlay.DemoSourceLocationId : "pc-library";

        string prevSrcId = SelectedSourceLocation?.Id ?? defaultSrcId;
        string prevDstId = SelectedDestLocation?.Id ?? "nw-hd5";
        string prevConvSrc = ConverterSourceLocation?.Id ?? defaultSrcId;
        string prevConvOut = ConverterOutputLocation?.Id ?? "pc-converted";
        string prevCustomSrc = CustomSelectedLocation?.Id ?? defaultSrcId;

        if (IsDemoModeEnabled && !hasLocalPc)
        {
            if (prevSrcId == "pc-library") prevSrcId = DemoModeOverlay.DemoSourceLocationId;
            if (prevConvSrc == "pc-library") prevConvSrc = DemoModeOverlay.DemoSourceLocationId;
            if (prevCustomSrc == "pc-library") prevCustomSrc = DemoModeOverlay.DemoSourceLocationId;
        }

        AllLocations.Clear();
        DestLocations.Clear();
        foreach (var pc in PcLocationCatalog.BuildDefaultPcLocations(DefaultMusicFolder, ConvertedOutputFolder))
        {
            AllLocations.Add(pc);
        }

        foreach (var custom in _customFolderLocations)
        {
            AllLocations.Add(custom);
        }

        if (IsDemoModeEnabled)
        {
            AllLocations.Add(DemoModeOverlay.CreateDemoSourceLocation());
        }

        foreach (var live in _liveHardwareDevices)
        {
            var liveItem = new TransferLocationItem(
                live.Id,
                $"Sony {live.Model} (Live USB)",
                "device",
                live.StorageFormatted,
                LiveDeviceId: live.LiveIdentity?.DeviceInstanceId);
            AllLocations.Add(liveItem);
            DestLocations.Add(liveItem);
        }

        if (IsDemoModeEnabled)
        {
            foreach (var demo in DemoModeOverlay.CreateDemoDevices())
            {
                string label = ConnectedDevice?.Id == demo.Id
                    ? $"Sony {demo.Model} (Connected)"
                    : $"Sony {demo.Model}";
                var demoItem = new TransferLocationItem(demo.Id, label, "device", demo.StorageFormatted, IsDemoLocation: true);
                AllLocations.Add(demoItem);
                DestLocations.Add(demoItem);
            }
        }

        SelectedSourceLocation = AllLocations.FirstOrDefault(l => l.Id == prevSrcId) ?? AllLocations.FirstOrDefault();
        if (ConnectedDevice is { IsLiveHardware: true } liveConn)
        {
            SelectedDestLocation = DestLocations.FirstOrDefault(l => l.Id == liveConn.Id)
                ?? DestLocations.FirstOrDefault();
        }
        else
        {
            SelectedDestLocation = DestLocations.FirstOrDefault(l => l.Id == prevDstId)
                ?? DestLocations.FirstOrDefault(l => l.Id.StartsWith("live:", StringComparison.OrdinalIgnoreCase))
                ?? DestLocations.FirstOrDefault();
        }
        ConverterSourceLocation = AllLocations.FirstOrDefault(l => l.Id == prevConvSrc) ?? AllLocations.FirstOrDefault();
        ConverterOutputLocation = AllLocations.FirstOrDefault(l => l.Id == prevConvOut)
            ?? AllLocations.FirstOrDefault(l => l.Id == "pc-converted")
            ?? AllLocations.LastOrDefault();
        CustomSelectedLocation = AllLocations.FirstOrDefault(l => l.Id == prevCustomSrc) ?? AllLocations.FirstOrDefault();
    }

    private void RebuildDeviceCards()
    {
        DeviceCards.Clear();
        foreach (var live in _liveHardwareDevices)
        {
            DeviceCards.Add(new WalkmanDeviceCardViewModel
            {
                Profile = live,
                IsConnected = ConnectedDevice?.Id == live.Id
            });
        }

        if (IsDemoModeEnabled)
        {
            foreach (var demo in DemoModeOverlay.CreateDemoDevices())
            {
                DeviceCards.Add(new WalkmanDeviceCardViewModel
                {
                    Profile = demo,
                    IsConnected = ConnectedDevice?.Id == demo.Id
                });
            }
        }
    }

    private void RebuildFlatFeatures()
    {
        FlatFeatures.Clear();
        foreach (var mod in ModuleRegistry.DefaultModules)
        {
            if (mod.Children is { Count: > 0 } children)
            {
                foreach (var child in children)
                {
                    bool blocked = child.RequiresDevice && ConnectedDevice is null;
                    FlatFeatures.Add(new FeatureCardItem(
                        child.Id,
                        child.Title,
                        $"{mod.Title} · {child.Subtitle}",
                        GetIconForModule(child.Id),
                        blocked,
                        blocked));
                }
            }
            else
            {
                bool blocked = mod.RequiresDevice && ConnectedDevice is null;
                FlatFeatures.Add(new FeatureCardItem(
                    mod.Id,
                    mod.Title,
                    mod.Subtitle,
                    GetIconForModule(mod.Id),
                    blocked,
                    blocked));
            }
        }
    }

    private static Geometry? GetIconForModule(string id)
    {
        if (Application.Current is null)
        {
            return null;
        }

        try
        {
            return Geometry.Parse(id switch
            {
                ModuleIds.FileManager => AeroIcons.Swap,
                ModuleIds.Library => AeroIcons.Music,
                ModuleIds.Converter => AeroIcons.AudioLines,
                ModuleIds.CustomOled => AeroIcons.Monitor,
                ModuleIds.CustomSubtitles => AeroIcons.Captions,
                ModuleIds.CustomTags => AeroIcons.Tags,
                ModuleIds.CustomFirmware => AeroIcons.Cpu,
                ModuleIds.DeviceSystem => AeroIcons.Wrench,
                ModuleIds.DeviceDumpWizard => AeroIcons.DumpArchive,
                ModuleIds.DeviceTechInfo => AeroIcons.TechInfo,
                ModuleIds.PairSync => AeroIcons.Usb,
                _ => AeroIcons.Disc
            });
        }
        catch
        {
            return null;
        }
    }

    private void RefreshAllLists()
    {
        RefreshFileManagerLists();
        RefreshConverterLists();
        RefreshLibraryList();
        RefreshCustomizationTracks();
        NotifyAllSummaryProps();
    }

    private IReadOnlyList<AudioTrackItem> GetUnfilteredFileManagerSourceTracks()
    {
        string srcId = SelectedSourceLocation?.Id ?? "pc-library";
        var result = new List<AudioTrackItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in _allTracks)
        {
            if (t.Locations.Contains(srcId) && seen.Add(t.Id))
            {
                result.Add(t);
            }
        }

        foreach (var t in FileManagerSourceTracks)
        {
            if (seen.Add(t.Id))
            {
                result.Add(t);
            }
        }

        return result;
    }

    private IReadOnlyList<AudioTrackItem> GetUnfilteredFileManagerDestTracks()
    {
        string dstId = SelectedDestLocation?.Id ?? "nw-hd5";
        var result = new List<AudioTrackItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var t in _allTracks)
        {
            if (t.Locations.Contains(dstId) && seen.Add(t.Id))
            {
                result.Add(t);
            }
        }

        foreach (var t in FileManagerDestTracks)
        {
            if (seen.Add(t.Id))
            {
                result.Add(t);
            }
        }

        return result;
    }

    private void RefreshFileManagerLists()
    {
        RefreshFileManagerSourceListCore();
        RefreshFileManagerDestListCore();
        NotifyDestSelectionChanged();
        NotifySelectionChanged();
        RebuildV3ListEntries();
    }

    private void RefreshFileManagerSourceList()
    {
        RefreshFileManagerSourceListCore();
        SyncEntryCollection(FileManagerSourceEntries, BuildSortedTrackEntries(
            FileManagerSourceTracks,
            FmSourceSortField,
            FmSourceSortDesc,
            groupByFolder: true,
            "fmsrc",
            _collapsedGroupKeys,
            PlayerTrack?.Id));
    }

    private void RefreshFileManagerDestList()
    {
        RefreshFileManagerDestListCore();
        NotifyDestSelectionChanged();
        RebuildFileManagerDestEntries();
    }

    private void RefreshFileManagerSourceListCore()
    {
        string srcId = SelectedSourceLocation?.Id ?? "pc-library";
        string qLeft = FileManagerLeftSearch.Trim();

        FileManagerSourceTracks.Clear();
        foreach (var t in _allTracks.Where(t =>
            t.Locations.Contains(srcId) &&
            SearchTextMatcher.Matches(qLeft, t.Title, t.Artist, t.Album, t.Folder)))
        {
            FileManagerSourceTracks.Add(t);
        }
    }

    private void RefreshFileManagerDestListCore()
    {
        string dstId = SelectedDestLocation?.Id ?? "nw-hd5";
        string qRight = FileManagerRightSearch.Trim();

        FileManagerDestTracks.Clear();
        foreach (var t in _allTracks.Where(t =>
            t.Locations.Contains(dstId) &&
            SearchTextMatcher.Matches(qRight, t.Title, t.Artist, t.Album, t.GroupDisplay)))
        {
            FileManagerDestTracks.Add(t);
        }
    }

    private void RefreshConverterLists()
    {
        string srcId = ConverterSourceLocation?.Id ?? "pc-library";
        string q = ConverterSearch.Trim();

        ConverterSourceTracks.Clear();
        foreach (var t in _allTracks.Where(t =>
            (srcId == "pc-library" ? t.DeviceInstanceId is null : t.Locations.Contains(srcId)) &&
            SearchTextMatcher.Matches(q, t.Title, t.Artist, t.Album, t.Format, t.Folder)))
        {
            ConverterSourceTracks.Add(t);
        }

        RefreshConverterProjectedOutputs();
        RebuildV3ListEntries();
    }

    private void RefreshConverterProjectedOutputs()
    {
        ConverterProjectedOutputs.Clear();
        var selected = ConverterSourceTracks.Where(t => t.IsSelected).ToList();
        double totalIn = 0;
        double totalOut = 0;
        string ext = ConverterStateLogic.GetOutputExtension(ConvTargetCodec);
        string rateText = ConverterStateLogic.IsLosslessCodec(ConvTargetCodec)
            ? "Lossless"
            : ConverterStateLogic.GetBitrateLabel(ConvBitrate);
        string codecLabel = ConverterStateLogic.GetCodecLabel(ConvTargetCodec);

        foreach (var trk in selected)
        {
            double est = ConverterStateLogic.EstimateConvertedSizeMb(trk.DurationSec, ConvTargetCodec, ConvBitrate);
            totalIn += trk.SizeMb;
            totalOut += est;
            ConverterProjectedOutputs.Add(new ProjectedOutputItem(
                $"{trk.Title}{ext}",
                $"{trk.Format} → {codecLabel} {rateText}",
                string.Create(CultureInfo.InvariantCulture, $"~{est:F1} MB"),
                string.Create(CultureInfo.InvariantCulture, $"from {trk.SizeMb:F1} MB")));
        }

        int delta = totalIn > 0 ? (int)Math.Round(((totalOut - totalIn) / totalIn) * 100.0) : 0;
        ConverterFootprintPercent = totalIn > 0 ? Math.Clamp((int)Math.Round((totalOut / totalIn) * 100.0), 8, 100) : 0;
        string deltaText = delta > 0 ? $"+{delta}%" : $"{delta}%";
        ConverterFootprintRight = string.Create(CultureInfo.InvariantCulture, $"{totalIn:F1} MB → {totalOut:F1} MB ({deltaText})");

        OnPropertyChanged(nameof(ConverterSelectedCount));
        OnPropertyChanged(nameof(ConvertButtonLabel));
        OnPropertyChanged(nameof(CanStartConvert));
        OnPropertyChanged(nameof(ConverterFootprintLeft));
        OnPropertyChanged(nameof(ConverterFootprintRight));
        OnPropertyChanged(nameof(ConverterFootprintPercent));
    }

    private void RefreshLibraryList()
    {
        string q = LibrarySearch.Trim();
        FilteredLibraryTracks.Clear();
        foreach (var t in _allTracks.Where(t =>
            t.DeviceInstanceId is null &&
            (!t.IsDemoItem || IsDemoModeEnabled) &&
            (LibraryFormatFilter == "All" || string.Equals(t.Format, LibraryFormatFilter, StringComparison.OrdinalIgnoreCase)) &&
            SearchTextMatcher.Matches(q, t.Title, t.Artist, t.Album, t.Folder)))
        {
            FilteredLibraryTracks.Add(t);
        }

        RebuildV3ListEntries();
    }

    private void LoadRealTracksFromFolder(string folder, string locationId)
    {
        if (Application.Current is null)
        {
            var realTracks = _backend.ScanLocalFolderTracks(folder, locationId);
            foreach (var rt in realTracks)
            {
                if (!_allTracks.Any(x => x.Id == rt.Id))
                {
                    _allTracks.Insert(0, rt);
                }
            }

            return;
        }

        _folderScanCts?.Cancel();
        _folderScanCts?.Dispose();
        _folderScanCts = new CancellationTokenSource();
        var ct = _folderScanCts.Token;
        IsScanningLibraryFolder = true;

        _ = Task.Run(async () =>
        {
            try
            {
                await _backend.ScanLocalFolderTracksIncrementalAsync(
                    folder,
                    locationId,
                    onBatchReady: batch =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (ct.IsCancellationRequested)
                            {
                                return;
                            }

                            var knownIds = new HashSet<string>(_allTracks.Select(x => x.Id), StringComparer.OrdinalIgnoreCase);
                            bool addedAny = false;
                            foreach (var rt in batch)
                            {
                                if (knownIds.Add(rt.Id))
                                {
                                    _allTracks.Add(rt);
                                    addedAny = true;
                                }
                            }

                            if (addedAny)
                            {
                                RefreshAllLists();
                            }
                        });
                    },
                    onMetadataBatchReady: patches =>
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            if (ct.IsCancellationRequested)
                            {
                                return;
                            }

                            foreach (var (track, patch) in patches)
                            {
                                WalkmanBackendService.ApplyMetadataPatch(track, patch);
                            }

                            NotifyAllSummaryProps();
                        });
                    },
                    batchSize: 32,
                    ct: ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                Dispatcher.UIThread.Post(() => IsScanningLibraryFolder = false);
            }
        }, ct);
    }

    private void RunSimulatedBatchJob(
        List<AudioTrackItem> queueTracks,
        string codecLabel,
        string stageA,
        string stageB,
        Func<string> onFinish)
    {
        int total = queueTracks.Count;
        int tick = 0;
        int totalTicks = total * 4;

        TransferJob = new ActiveTransferJob(
            Guid.NewGuid().ToString("N"),
            queueTracks[0].Title,
            stageA,
            codecLabel,
            0,
            total,
            10,
            Math.Max(1, (int)Math.Ceiling(totalTicks * 0.45)),
            false);

        _demoTransferTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _demoTransferTimer.Tick += (_, _) =>
        {
            tick++;
            int pct = Math.Min(100, (int)Math.Round((tick / (double)totalTicks) * 100.0));
            int completed = Math.Min(total, (int)Math.Floor((tick / (double)totalTicks) * total));
            int activeIdx = Math.Min(total - 1, completed);
            var current = queueTracks[activeIdx];
            string stage = (tick % 4) < 3 ? stageA : stageB;

            if (tick >= totalTicks)
            {
                StopDemoTransferTimer();
                string finishLabel = onFinish();
                TransferJob = new ActiveTransferJob(
                    Guid.NewGuid().ToString("N"),
                    $"{total} file{(total > 1 ? "s" : "")} processed",
                    finishLabel,
                    codecLabel,
                    total,
                    total,
                    100,
                    0,
                    true);
            }
            else
            {
                TransferJob = new ActiveTransferJob(
                    TransferJob?.Id ?? "job",
                    current.Title,
                    stage,
                    codecLabel,
                    completed,
                    total,
                    pct,
                    Math.Max(1, (int)Math.Ceiling((totalTicks - tick) * 0.42)),
                    false);
            }
        };
        _demoTransferTimer.Start();
    }

    private void StopDemoTransferTimer()
    {
        _demoTransferTimer?.Stop();
        _demoTransferTimer = null;
    }

    private void OnBackendTransferProgress(TransferProgressEvent ev)
    {
        Dispatcher.UIThread.Post(() =>
        {
            int pct = Math.Clamp((int)Math.Round(ev.PercentComplete), 0, 100);
            string stageText = pct switch
            {
                >= 95 => "Committing OMGAUDIO .DAT tables...",
                >= 55 => "Writing .OMA files to USB...",
                _ => "Transcoding & packaging audio..."
            };

            TransferJob = new ActiveTransferJob(
                ev.JobId,
                ev.CurrentTrackTitle,
                stageText,
                "Live USB Sync",
                ev.CompletedTracks,
                ev.TotalTracks,
                pct,
                Math.Max(0, (ev.TotalTracks - ev.CompletedTracks) * 4),
                pct >= 100);
        });
    }

    private void OnAudioPlayerStateChanged()
    {
        PlayerTrack = _player.CurrentTrack;
        IsPlaying = _player.IsPlaying;
        ElapsedSec = _player.ElapsedSeconds;
    }

    private NwmProjectDto BuildDemoNwmProject()
    {
        // Generate an animated 16-frame 120x36 1bpp wave banner using banner text aesthetics
        const int w = 120;
        const int h = 36;
        const int bytesPerFrame = (w * h) / 8;
        var bitmaps = new List<byte[]>();
        var timeline = new List<NwmFrameEntry>();

        for (int f = 0; f < 16; f++)
        {
            byte[] buf = new byte[bytesPerFrame];
            double phase = f / 16.0 * Math.PI * 2.0;
            for (int x = 0; x < w; x++)
            {
                int y1 = (int)Math.Round(18 + Math.Sin(x * 0.14 + phase) * 11);
                int y2 = (int)Math.Round(18 + Math.Cos(x * 0.1 - phase) * 8);
                for (int dy = -1; dy <= 1; dy++)
                {
                    int py = Math.Clamp(y1 + dy, 0, h - 1);
                    int idx = py * w + x;
                    buf[idx >> 3] |= (byte)(0x80 >> (idx & 7));
                }

                if (y2 >= 0 && y2 < h)
                {
                    int idx2 = y2 * w + x;
                    buf[idx2 >> 3] |= (byte)(0x80 >> (idx2 & 7));
                }
            }

            bitmaps.Add(buf);
            timeline.Add(new NwmFrameEntry((byte)f, 0x10));
        }

        string safeName = string.IsNullOrWhiteSpace(OledBannerText) ? "APOLLOAERO" : OledBannerText;
        return new NwmProjectDto(
            ScreensaverMagic.NwE400_0xEC01,
            safeName,
            "ApolloManager",
            30720,
            12288,
            w,
            h,
            1,
            timeline,
            bitmaps);
    }

    public void Dispose()
    {
        _folderScanCts?.Cancel();
        _folderScanCts?.Dispose();
        _folderScanCts = null;
        StopDemoTransferTimer();
        _progressSub?.Dispose();
        _player.Dispose();
    }
}
