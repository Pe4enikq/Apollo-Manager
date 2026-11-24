using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Walkman.Contracts.Devices;
using Walkman.L3.AppShell.Core;

namespace Walkman.L3.AppShell.ViewModels;

public sealed partial class MainWindowViewModel
{
    private readonly HashSet<string> _collapsedGroupKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _autoCollapsedCatalogSignatures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _customCollectionsByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _stagedTrackGroups = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _collectionTrackOrder = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _collectionOrderByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _pendingGroupRenamesByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<Walkman.Contracts.Engines.TrackMetadataPatch>> _pendingTagPatchesByDevice = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _notifiedBpbWarningKeys = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _notifiedSupportTierKeys = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastRewindPressUtc = DateTime.MinValue;

    [ObservableProperty] private bool _hasPendingDeviceTableChanges;
    public bool CanCommitPendingDeviceChanges => HasPendingDeviceTableChanges && !IsTransferRunning && DestDeviceSummary is not null;
    public string UpdateDeviceTableButtonLabel => "Update";

    partial void OnHasPendingDeviceTableChangesChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCommitPendingDeviceChanges));
        OnPropertyChanged(nameof(UpdateDeviceTableButtonLabel));
    }

    // My Library Column Visibility (persisted in AppSettingsStore)
    [ObservableProperty] private bool _showLibColAlbum = true;
    [ObservableProperty] private bool _showLibColRating = true;
    [ObservableProperty] private bool _showLibColCodec = true;
    [ObservableProperty] private bool _showLibColDuration = true;

    public Avalonia.Controls.GridLength LibColAlbumWidth => ShowLibColAlbum ? new Avalonia.Controls.GridLength(1.3, Avalonia.Controls.GridUnitType.Star) : new Avalonia.Controls.GridLength(0);
    public Avalonia.Controls.GridLength LibColRatingWidth => ShowLibColRating ? new Avalonia.Controls.GridLength(0.8, Avalonia.Controls.GridUnitType.Star) : new Avalonia.Controls.GridLength(0);
    public Avalonia.Controls.GridLength LibColCodecWidth => ShowLibColCodec ? new Avalonia.Controls.GridLength(1.1, Avalonia.Controls.GridUnitType.Star) : new Avalonia.Controls.GridLength(0);
    public Avalonia.Controls.GridLength LibColDurationWidth => ShowLibColDuration ? new Avalonia.Controls.GridLength(74) : new Avalonia.Controls.GridLength(0);

    partial void OnShowLibColAlbumChanged(bool value) { OnPropertyChanged(nameof(LibColAlbumWidth)); PersistSettings(); }
    partial void OnShowLibColRatingChanged(bool value) { OnPropertyChanged(nameof(LibColRatingWidth)); PersistSettings(); }
    partial void OnShowLibColCodecChanged(bool value) { OnPropertyChanged(nameof(LibColCodecWidth)); PersistSettings(); }
    partial void OnShowLibColDurationChanged(bool value) { OnPropertyChanged(nameof(LibColDurationWidth)); PersistSettings(); }

    [RelayCommand]
    private void RevealPlayerTrackInLibrary()
    {
        if (PlayerTrack is null)
        {
            return;
        }

        string folder = string.IsNullOrWhiteSpace(PlayerTrack.Folder) ? "Root" : PlayerTrack.Folder;
        _collapsedGroupKeys.Remove($"lib:{folder}");
        if (!string.IsNullOrWhiteSpace(LibrarySearch) &&
            !FilteredLibraryTracks.Any(t => string.Equals(t.Id, PlayerTrack.Id, StringComparison.Ordinal)))
        {
            LibrarySearch = string.Empty;
        }

        OpenModule(ModuleIds.Library);
        RebuildV3ListEntries();
    }

    // Playback Repeat & Shuffle
    [ObservableProperty] private bool _isShuffleEnabled;
    [ObservableProperty] private RepeatMode _repeatMode = RepeatMode.Off;

    public bool IsRepeatActive => RepeatMode != RepeatMode.Off;
    public bool IsRepeatOne => RepeatMode == RepeatMode.One;
    public string RepeatBadgeText => RepeatMode == RepeatMode.One ? "1" : string.Empty;
    public string RepeatToolTip => RepeatMode switch
    {
        RepeatMode.All => "Repeat: All tracks",
        RepeatMode.One => "Repeat: Current track",
        _ => "Repeat: Off"
    };
    public string ShuffleToolTip => IsShuffleEnabled ? "Shuffle: On" : "Shuffle: Off";

    partial void OnIsShuffleEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(ShuffleToolTip));
        PersistSettings();
    }

    partial void OnRepeatModeChanged(RepeatMode value)
    {
        OnPropertyChanged(nameof(IsRepeatActive));
        OnPropertyChanged(nameof(IsRepeatOne));
        OnPropertyChanged(nameof(RepeatBadgeText));
        OnPropertyChanged(nameof(RepeatToolTip));
        PersistSettings();
    }

    [RelayCommand]
    private void ToggleShuffle() => IsShuffleEnabled = !IsShuffleEnabled;

    [RelayCommand]
    private void CycleRepeatMode()
    {
        RepeatMode = RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off
        };
    }

    public bool HandleRewindOrPrevious()
    {
        var now = DateTime.UtcNow;
        bool isDoubleClick = (now - _lastRewindPressUtc).TotalMilliseconds <= 650
                             || (ElapsedSec <= 1 && (now - _lastRewindPressUtc).TotalMilliseconds <= 1400);
        _lastRewindPressUtc = now;
        if (isDoubleClick)
        {
            return true;
        }

        _player.Seek(0);
        return false;
    }

    private static readonly IReadOnlySet<string> EmptyCollapsedSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AudioTrackItem> GetActivePlaybackQueue()
    {
        bool useDest = ActiveModuleId == ModuleIds.FileManager
            ? (PlayerTrack is not null && FileManagerDestTracks.Any(t => t.Id == PlayerTrack.Id))
            : (ActiveModuleId != ModuleIds.Library &&
               ActiveModuleId != ModuleIds.Converter &&
               PlayerTrack is not null &&
               !FilteredLibraryTracks.Any(t => t.Id == PlayerTrack.Id) &&
               !FileManagerSourceTracks.Any(t => t.Id == PlayerTrack.Id) &&
               FileManagerDestTracks.Any(t => t.Id == PlayerTrack.Id));

        if (useDest)
        {
            string destId = SelectedDestLocation?.Id ?? string.Empty;
            var customCols = _customCollectionsByDevice.TryGetValue(destId, out var list) ? list : Enumerable.Empty<string>();
            var deviceCols = DestDeviceSummary?.GroupNames
                ?.Where(g => !string.IsNullOrWhiteSpace(g) && !string.Equals(g, "All Tracks", StringComparison.OrdinalIgnoreCase))
                ?? Enumerable.Empty<string>();

            var orderByGroup = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
            string prefix = $"{destId}::";
            foreach (var kv in _collectionTrackOrder)
            {
                if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    orderByGroup[kv.Key[prefix.Length..]] = kv.Value;
                }
            }

            var destEntries = BuildDestinationCollectionEntries(
                FileManagerDestTracks,
                Array.Empty<AudioTrackItem>(),
                _stagedTrackGroups,
                FmTargetGroupName,
                deviceCols.Concat(customCols),
                FmDestSortField,
                FmDestSortDesc,
                EmptyCollapsedSet,
                orderByGroup);

            return destEntries
                .Where(e => e.IsTrack && e.Track is not null && !e.Track.IsStagedPreview)
                .Select(e => e.Track!)
                .ToList();
        }

        (IEnumerable<AudioTrackItem> tracks, TrackSortField sortField, bool sortDesc, string scope) = ActiveModuleId switch
        {
            ModuleIds.Library => (FilteredLibraryTracks, LibrarySortField, LibrarySortDesc, "lib"),
            ModuleIds.FileManager => (FileManagerSourceTracks, FmSourceSortField, FmSourceSortDesc, "fmsrc"),
            ModuleIds.Converter => (ConverterSourceTracks, ConvSortField, ConvSortDesc, "conv"),
            _ => PlayerTrack is not null && FilteredLibraryTracks.Any(t => t.Id == PlayerTrack.Id)
                ? (FilteredLibraryTracks, LibrarySortField, LibrarySortDesc, "lib")
                : (PlayerTrack is not null && FileManagerSourceTracks.Any(t => t.Id == PlayerTrack.Id)
                    ? (FileManagerSourceTracks, FmSourceSortField, FmSourceSortDesc, "fmsrc")
                    : (PlayerTrack is not null && ConverterSourceTracks.Any(t => t.Id == PlayerTrack.Id)
                        ? (ConverterSourceTracks, ConvSortField, ConvSortDesc, "conv")
                        : (FilteredLibraryTracks, LibrarySortField, LibrarySortDesc, "lib")))
        };

        var expandedEntries = BuildSortedTrackEntries(
            tracks,
            sortField,
            sortDesc,
            groupByFolder: true,
            scope,
            EmptyCollapsedSet,
            PlayerTrack?.Id);

        return expandedEntries
            .Where(e => e.IsTrack && e.Track is not null && !e.Track.IsStagedPreview)
            .Select(e => e.Track!)
            .ToList();
    }

    private void StepTrack(int delta, bool fromTrackEnd = false)
    {
        var queue = GetActivePlaybackQueue();
        if (queue.Count == 0)
        {
            return;
        }

        if (fromTrackEnd && RepeatMode == RepeatMode.One && PlayerTrack is not null)
        {
            _player.Seek(0);
            if (!_player.IsPlaying)
            {
                _player.TogglePlayPause();
            }
            return;
        }

        int currentIdx = -1;
        if (PlayerTrack is not null)
        {
            for (int i = 0; i < queue.Count; i++)
            {
                if (string.Equals(queue[i].Id, PlayerTrack.Id, StringComparison.Ordinal))
                {
                    currentIdx = i;
                    break;
                }
            }
        }

        if (IsShuffleEnabled && queue.Count > 1)
        {
            int nextIdx = Random.Shared.Next(queue.Count);
            if (nextIdx == currentIdx)
            {
                nextIdx = (nextIdx + 1) % queue.Count;
            }
            _player.PlayOrToggle(queue[nextIdx]);
            return;
        }

        int rawNext = currentIdx + delta;
        if (fromTrackEnd && RepeatMode == RepeatMode.Off && rawNext >= queue.Count)
        {
            _player.Stop();
            return;
        }

        int wrapped = ((rawNext % queue.Count) + queue.Count) % queue.Count;
        _player.PlayOrToggle(queue[wrapped]);
    }

    // Input Modal (New / Rename Collection)
    [ObservableProperty] private InputDialogState? _activeInputDialog;
    [ObservableProperty] private string _inputDialogText = string.Empty;
    public bool HasInputDialog => ActiveInputDialog is not null;

    partial void OnActiveInputDialogChanged(InputDialogState? value) => OnPropertyChanged(nameof(HasInputDialog));

    public void RequestInputDialog(string title, string prompt, string initialValue, string confirmLabel, Action<string> onConfirm)
    {
        InputDialogText = initialValue;
        ActiveInputDialog = new InputDialogState(title, prompt, initialValue, confirmLabel, onConfirm);
    }

    [RelayCommand]
    private void ConfirmInputDialog()
    {
        var dlg = ActiveInputDialog;
        string text = InputDialogText.Trim();
        ActiveInputDialog = null;
        if (dlg is not null && !string.IsNullOrWhiteSpace(text))
        {
            dlg.OnConfirm(text);
        }
    }

    [RelayCommand]
    private void CancelInputDialog() => ActiveInputDialog = null;

    // Sort By & Hierarchical Folder / Collection Entries
    public IReadOnlyList<TrackSortField> AvailableSortFields { get; } =
    [
        TrackSortField.Title,
        TrackSortField.Artist,
        TrackSortField.Album,
        TrackSortField.AlbumTrackNumber,
        TrackSortField.Format,
        TrackSortField.Duration,
        TrackSortField.Size,
        TrackSortField.Rating,
        TrackSortField.Folder,
        TrackSortField.Subtitles
    ];

    public IReadOnlyList<TrackSortField> AvailableDestSortFields { get; } =
    [
        TrackSortField.Order,
        TrackSortField.Title,
        TrackSortField.Artist,
        TrackSortField.Album,
        TrackSortField.AlbumTrackNumber,
        TrackSortField.Format,
        TrackSortField.Duration,
        TrackSortField.Size,
        TrackSortField.Rating,
        TrackSortField.Subtitles
    ];

    [ObservableProperty] private TrackSortField _librarySortField = TrackSortField.Title;
    [ObservableProperty] private bool _librarySortDesc;
    [ObservableProperty] private bool _libraryGroupByFolder = true;

    [ObservableProperty] private TrackSortField _fmSourceSortField = TrackSortField.Title;
    [ObservableProperty] private bool _fmSourceSortDesc;
    [ObservableProperty] private bool _fmSourceGroupByFolder = true;

    [ObservableProperty] private TrackSortField _fmDestSortField = TrackSortField.Order;
    [ObservableProperty] private bool _fmDestSortDesc;

    [ObservableProperty] private TrackSortField _convSortField = TrackSortField.Title;
    [ObservableProperty] private bool _convSortDesc;
    [ObservableProperty] private bool _convGroupByFolder = true;

    [ObservableProperty] private TrackSortField _customSortField = TrackSortField.Title;
    [ObservableProperty] private bool _customSortDesc;

    public BulkObservableCollection<TrackListEntry> LibraryEntries { get; } = new();
    public BulkObservableCollection<TrackListEntry> FileManagerSourceEntries { get; } = new();
    public BulkObservableCollection<TrackListEntry> FileManagerDestEntries { get; } = new();
    public BulkObservableCollection<TrackListEntry> ConverterSourceEntries { get; } = new();
    public BulkObservableCollection<TrackListEntry> CustomizationEntries { get; } = new();

    public string FmSelectedQueueSizeText => string.Create(CultureInfo.InvariantCulture, $"{TransferSelectedCount} selected · ~{DestSelectedTransferGb * 1024.0:F1} MB");
    public string ConvSelectedQueueSizeText => string.Create(CultureInfo.InvariantCulture, $"{ConverterSelectedCount} queued · {ConverterSourceTracks.Where(t => t.IsSelected).Sum(t => t.SizeMb):F1} MB");

    partial void OnLibrarySortFieldChanged(TrackSortField value) => RebuildV3ListEntries();
    partial void OnLibrarySortDescChanged(bool value) => RebuildV3ListEntries();
    partial void OnLibraryGroupByFolderChanged(bool value) => RebuildV3ListEntries();
    partial void OnFmSourceSortFieldChanged(TrackSortField value) => RebuildV3ListEntries();
    partial void OnFmSourceSortDescChanged(bool value) => RebuildV3ListEntries();
    partial void OnFmSourceGroupByFolderChanged(bool value) => RebuildV3ListEntries();
    partial void OnFmDestSortFieldChanged(TrackSortField value) => RebuildV3ListEntries();
    partial void OnFmDestSortDescChanged(bool value) => RebuildV3ListEntries();
    partial void OnConvSortFieldChanged(TrackSortField value) => RebuildV3ListEntries();
    partial void OnConvSortDescChanged(bool value) => RebuildV3ListEntries();
    partial void OnConvGroupByFolderChanged(bool value) => RebuildV3ListEntries();
    partial void OnCustomSortFieldChanged(TrackSortField value) => RebuildCustomizationEntries();
    partial void OnCustomSortDescChanged(bool value) => RebuildCustomizationEntries();

    [RelayCommand] private void ToggleLibrarySortDirection() => LibrarySortDesc = !LibrarySortDesc;
    [RelayCommand] private void ToggleLibraryGroupByFolder() => LibraryGroupByFolder = !LibraryGroupByFolder;
    [RelayCommand] private void ToggleFmSourceSortDirection() => FmSourceSortDesc = !FmSourceSortDesc;
    [RelayCommand] private void ToggleFmSourceGroupByFolder() => FmSourceGroupByFolder = !FmSourceGroupByFolder;
    [RelayCommand] private void ToggleFmDestSortDirection() => FmDestSortDesc = !FmDestSortDesc;
    [RelayCommand] private void ToggleConvSortDirection() => ConvSortDesc = !ConvSortDesc;
    [RelayCommand] private void ToggleConvGroupByFolder() => ConvGroupByFolder = !ConvGroupByFolder;
    [RelayCommand] private void ToggleCustomSortDirection() => CustomSortDesc = !CustomSortDesc;

    [RelayCommand]
    private void ToggleFolderExpanded(string? headerKey)
    {
        if (string.IsNullOrWhiteSpace(headerKey))
        {
            return;
        }

        bool isNowCollapsed = _collapsedGroupKeys.Add(headerKey);
        if (!isNowCollapsed)
        {
            _collapsedGroupKeys.Remove(headerKey);
        }

        int colon = headerKey.IndexOf(':');
        string scope = colon > 0 ? headerKey[..colon].ToLowerInvariant() : string.Empty;
        ObservableCollection<TrackListEntry>? targetCollection = scope switch
        {
            "lib" => LibraryEntries,
            "fmsrc" => FileManagerSourceEntries,
            "fmdest" => FileManagerDestEntries,
            "conv" => ConverterSourceEntries,
            "cust" => CustomizationEntries,
            _ => null
        };

        if (targetCollection is null)
        {
            RebuildV3ListEntries();
            return;
        }

        int headerIdx = -1;
        for (int i = 0; i < targetCollection.Count; i++)
        {
            if (targetCollection[i].IsHeader && string.Equals(targetCollection[i].HeaderKey, headerKey, StringComparison.OrdinalIgnoreCase))
            {
                headerIdx = i;
                break;
            }
        }

        if (headerIdx < 0)
        {
            RebuildV3ListEntries();
            return;
        }

        var header = targetCollection[headerIdx];
        if (isNowCollapsed)
        {
            header.IsExpanded = false;
            int removeIdx = headerIdx + 1;
            while (removeIdx < targetCollection.Count && !targetCollection[removeIdx].IsHeader)
            {
                targetCollection.RemoveAt(removeIdx);
            }
            return;
        }

        header.IsExpanded = true;
        IReadOnlyList<TrackListEntry> rebuilt = scope switch
        {
            "lib" => BuildSortedTrackEntries(FilteredLibraryTracks, LibrarySortField, LibrarySortDesc, groupByFolder: true, "lib", _collapsedGroupKeys, PlayerTrack?.Id),
            "fmsrc" => BuildSortedTrackEntries(FileManagerSourceTracks, FmSourceSortField, FmSourceSortDesc, groupByFolder: true, "fmsrc", _collapsedGroupKeys, PlayerTrack?.Id),
            "conv" => BuildSortedTrackEntries(ConverterSourceTracks, ConvSortField, ConvSortDesc, groupByFolder: true, "conv", _collapsedGroupKeys, PlayerTrack?.Id),
            "cust" => BuildSortedTrackEntries(CustomizationTracks, CustomSortField, CustomSortDesc, groupByFolder: true, "cust", _collapsedGroupKeys, PlayerTrack?.Id),
            "fmdest" => BuildDestEntriesSnapshot(),
            _ => Array.Empty<TrackListEntry>()
        };

        int rebuiltHeaderIdx = -1;
        for (int i = 0; i < rebuilt.Count; i++)
        {
            if (rebuilt[i].IsHeader && string.Equals(rebuilt[i].HeaderKey, headerKey, StringComparison.OrdinalIgnoreCase))
            {
                rebuiltHeaderIdx = i;
                break;
            }
        }

        if (rebuiltHeaderIdx < 0)
        {
            RebuildV3ListEntries();
            return;
        }

        int insertAt = headerIdx + 1;
        for (int i = rebuiltHeaderIdx + 1; i < rebuilt.Count && !rebuilt[i].IsHeader; i++)
        {
            targetCollection.Insert(insertAt++, rebuilt[i]);
        }
    }

    public void UpdateFolderPlayStateBadgesInPlace()
    {
        static void UpdateHeaders(
            ObservableCollection<TrackListEntry> entries,
            IEnumerable<AudioTrackItem> tracks,
            bool isDest)
        {
            var playingGroups = new HashSet<string>(
                tracks.Where(t => t.IsNowPlaying)
                      .Select(t => isDest
                          ? (string.IsNullOrWhiteSpace(t.GroupName) ? "Unassigned" : t.GroupName!)
                          : (string.IsNullOrWhiteSpace(t.Folder) ? "Root" : t.Folder)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                if (entry.IsHeader)
                {
                    bool playing = playingGroups.Contains(entry.HeaderTitle);
                    entry.HasPlayingTrack = playing;
                    entry.ContainsPlayingTrack = playing;
                }
            }
        }

        UpdateHeaders(LibraryEntries, FilteredLibraryTracks, isDest: false);
        UpdateHeaders(FileManagerSourceEntries, FileManagerSourceTracks, isDest: false);
        UpdateHeaders(ConverterSourceEntries, ConverterSourceTracks, isDest: false);
        UpdateHeaders(CustomizationEntries, CustomizationTracks, isDest: false);
        UpdateHeaders(FileManagerDestEntries, FileManagerDestTracks, isDest: true);
    }

    public void UpdateFolderSelectionBadgesInPlace()
    {
        static void UpdateHeaders(
            ObservableCollection<TrackListEntry> entries,
            IEnumerable<AudioTrackItem> tracks,
            Func<AudioTrackItem, bool> isSelected)
        {
            var selectedGroups = new HashSet<string>(
                tracks.Where(isSelected)
                      .Select(t => string.IsNullOrWhiteSpace(t.Folder) ? "Root" : t.Folder),
                StringComparer.OrdinalIgnoreCase);

            foreach (var entry in entries)
            {
                if (entry.IsHeader)
                {
                    entry.HasSelectedTracks = selectedGroups.Contains(entry.HeaderTitle);
                }
            }
        }

        UpdateHeaders(FileManagerSourceEntries, FileManagerSourceTracks, t => t.IsSelected);
        UpdateHeaders(ConverterSourceEntries, ConverterSourceTracks, t => t.IsSelected);
        UpdateHeaders(LibraryEntries, FilteredLibraryTracks, t => t.IsLibrarySelected);
    }

    public void UpdateDestFolderSelectionBadgesInPlace()
    {
        var selectedGroups = new HashSet<string>(
            FileManagerDestTracks.Where(t => t.IsDestSelected)
                                 .Select(t => string.IsNullOrWhiteSpace(t.GroupName) ? "Unassigned" : t.GroupName!),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in FileManagerDestEntries)
        {
            if (entry.IsHeader)
            {
                entry.HasSelectedTracks = selectedGroups.Contains(entry.HeaderTitle);
            }
        }
    }

    public void UpdateCustFolderSelectionBadgesInPlace()
    {
        var selectedGroups = new HashSet<string>(
            CustomizationTracks.Where(t => t.IsTagSelected)
                               .Select(t => string.IsNullOrWhiteSpace(t.Folder) ? "Root" : t.Folder),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in CustomizationEntries)
        {
            if (entry.IsHeader)
            {
                entry.HasSelectedTracks = selectedGroups.Contains(entry.HeaderTitle);
            }
        }
    }

    [RelayCommand]
    private void CollapseAllFolders(string? scope)
    {
        IEnumerable<TrackListEntry> entries = scope?.ToLowerInvariant() switch
        {
            "lib" => LibraryEntries,
            "fmsrc" => FileManagerSourceEntries,
            "fmdest" => FileManagerDestEntries,
            "conv" => ConverterSourceEntries,
            "cust" => CustomizationEntries,
            _ => LibraryEntries
        };

        var headerKeys = entries
            .Where(e => e.IsHeader && !string.IsNullOrWhiteSpace(e.HeaderKey))
            .Select(e => e.HeaderKey)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (headerKeys.Count == 0)
        {
            return;
        }

        bool anyExpanded = headerKeys.Any(k => !_collapsedGroupKeys.Contains(k));
        if (anyExpanded)
        {
            foreach (string key in headerKeys)
            {
                _collapsedGroupKeys.Add(key);
            }
        }
        else
        {
            foreach (string key in headerKeys)
            {
                _collapsedGroupKeys.Remove(key);
            }
        }

        RebuildV3ListEntries();
    }

    private void ApplyAutoCollapseIfLargeCatalog(string scopePrefix, string locationKey, IReadOnlyCollection<AudioTrackItem> tracks)
    {
        if (tracks.Count <= 500)
        {
            return;
        }

        var folderKeys = tracks
            .Select(t => $"{scopePrefix}:{(string.IsNullOrWhiteSpace(t.Folder) ? "Root" : t.Folder)}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (folderKeys.Count <= 5)
        {
            return;
        }

        string sig = $"{scopePrefix}|{locationKey}|{tracks.Count}|{folderKeys.Count}";
        if (_autoCollapsedCatalogSignatures.Add(sig))
        {
            foreach (string key in folderKeys)
            {
                _collapsedGroupKeys.Add(key);
            }
        }
    }

    public void RebuildV3ListEntries()
    {
        OnPropertyChanged(nameof(FmSelectedQueueSizeText));
        OnPropertyChanged(nameof(ConvSelectedQueueSizeText));

        ApplyAutoCollapseIfLargeCatalog("lib", LibraryFolderDisplay, FilteredLibraryTracks);
        ApplyAutoCollapseIfLargeCatalog("fmsrc", SelectedSourceLocation?.Id ?? "pc-library", FileManagerSourceTracks);
        ApplyAutoCollapseIfLargeCatalog("conv", ConverterSourceLocation?.Id ?? "pc-library", ConverterSourceTracks);

        SyncEntryCollection(LibraryEntries, BuildSortedTrackEntries(
            FilteredLibraryTracks,
            LibrarySortField,
            LibrarySortDesc,
            groupByFolder: true,
            "lib",
            _collapsedGroupKeys,
            PlayerTrack?.Id));

        SyncEntryCollection(FileManagerSourceEntries, BuildSortedTrackEntries(
            FileManagerSourceTracks,
            FmSourceSortField,
            FmSourceSortDesc,
            groupByFolder: true,
            "fmsrc",
            _collapsedGroupKeys,
            PlayerTrack?.Id));

        SyncEntryCollection(ConverterSourceEntries, BuildSortedTrackEntries(
            ConverterSourceTracks,
            ConvSortField,
            ConvSortDesc,
            groupByFolder: true,
            "conv",
            _collapsedGroupKeys,
            PlayerTrack?.Id));

        RebuildFileManagerDestEntries();
        RebuildCustomizationEntries();
    }

    public void RebuildCustomizationEntries()
    {
        ApplyAutoCollapseIfLargeCatalog("cust", CustomSelectedLocation?.Id ?? "pc-library", CustomizationTracks);
        SyncEntryCollection(CustomizationEntries, BuildSortedTrackEntries(
            CustomizationTracks,
            CustomSortField,
            CustomSortDesc,
            groupByFolder: true,
            "cust",
            _collapsedGroupKeys,
            PlayerTrack?.Id));
    }

    private void RebuildFileManagerDestEntries()
    {
        SyncEntryCollection(FileManagerDestEntries, BuildDestEntriesSnapshot());
    }

    private IReadOnlyList<TrackListEntry> BuildDestEntriesSnapshot()
    {
        string destId = SelectedDestLocation?.Id ?? string.Empty;
        string qRight = FileManagerRightSearch.Trim();
        var stagedSource = GetUnfilteredFileManagerSourceTracks()
            .Where(t => t.IsSelected &&
                        (string.IsNullOrEmpty(qRight) ||
                         SearchTextMatcher.Matches(qRight, t.Title, t.Artist, t.Album, GetStagedCollectionName(t.Id))))
            .ToList();
        var customCols = _customCollectionsByDevice.TryGetValue(destId, out var list) ? list : Enumerable.Empty<string>();
        var deviceCols = DestDeviceSummary?.GroupNames
            ?.Where(g => !string.IsNullOrWhiteSpace(g) && !string.Equals(g, "All Tracks", StringComparison.OrdinalIgnoreCase))
            ?? Enumerable.Empty<string>();

        var orderByGroup = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        string prefix = $"{destId}::";
        foreach (var kv in _collectionTrackOrder)
        {
            if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                orderByGroup[kv.Key[prefix.Length..]] = kv.Value;
            }
        }

        var existingGroups = FileManagerDestTracks
            .Select(t => string.IsNullOrWhiteSpace(t.GroupName) ? "Unassigned" : t.GroupName);
        var stagedGroups = stagedSource.Select(t => GetStagedCollectionName(t.Id));
        var explicitGroupOrder = EnsureDeviceGroupOrder(destId, deviceCols, existingGroups, stagedGroups, customCols);

        return BuildDestinationCollectionEntries(
            FileManagerDestTracks,
            stagedSource,
            _stagedTrackGroups,
            FmTargetGroupName,
            deviceCols.Concat(customCols),
            FmDestSortField,
            FmDestSortDesc,
            _collapsedGroupKeys,
            orderByGroup,
            explicitGroupOrder);
    }

    public static IReadOnlyList<TrackListEntry> BuildSortedTrackEntries(
        IEnumerable<AudioTrackItem> tracks,
        TrackSortField sortField,
        bool descending,
        bool groupByFolder,
        string scopePrefix,
        IReadOnlySet<string> collapsedKeys,
        string? playingTrackId = null)
    {
        var sorted = SortTracks(tracks, sortField, descending).ToList();
        if (!groupByFolder)
        {
            return sorted.Select(t => new TrackListEntry { IsHeader = false, Track = t }).ToList();
        }

        var result = new List<TrackListEntry>(sorted.Count + 8);
        foreach (var grp in sorted.GroupBy(t => string.IsNullOrWhiteSpace(t.Folder) ? "Root" : t.Folder, StringComparer.OrdinalIgnoreCase))
        {
            string key = $"{scopePrefix}:{grp.Key}";
            bool expanded = !collapsedKeys.Contains(key);
            var items = grp.ToList();
            bool hasPlaying = items.Any(t => t.IsNowPlaying || (!string.IsNullOrEmpty(playingTrackId) && string.Equals(t.Id, playingTrackId, StringComparison.Ordinal)));
            bool hasSelected = string.Equals(scopePrefix, "lib", StringComparison.OrdinalIgnoreCase)
                ? items.Any(t => t.IsLibrarySelected)
                : items.Any(t => t.IsSelected);
            result.Add(new TrackListEntry
            {
                IsHeader = true,
                HeaderKey = key,
                HeaderTitle = grp.Key,
                ScopePrefix = scopePrefix,
                ItemCount = items.Count,
                IsExpanded = expanded,
                HasSelectedTracks = hasSelected,
                HasPlayingTrack = hasPlaying,
                ContainsPlayingTrack = hasPlaying
            });

            if (expanded)
            {
                foreach (var item in items)
                {
                    result.Add(new TrackListEntry { IsHeader = false, Track = item });
                }
            }
        }

        return result;
    }

    public static IReadOnlyList<TrackListEntry> BuildDestinationCollectionEntries(
        IEnumerable<AudioTrackItem> destTracks,
        IEnumerable<AudioTrackItem> stagedSourceTracks,
        IReadOnlyDictionary<string, string> stagedGroupOverrides,
        string defaultTargetGroup,
        IEnumerable<string> customCollections,
        TrackSortField sortField,
        bool descending,
        IReadOnlySet<string> collapsedKeys,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? customOrderByGroup = null,
        IReadOnlyList<string>? explicitGroupOrder = null)
    {
        var existingByGroup = new Dictionary<string, List<AudioTrackItem>>(StringComparer.OrdinalIgnoreCase);
        IEnumerable<AudioTrackItem> orderedDest = sortField == TrackSortField.Order
            ? (descending ? destTracks.Reverse() : destTracks)
            : SortTracks(destTracks, sortField, descending);

        foreach (var t in orderedDest)
        {
            string g = string.IsNullOrWhiteSpace(t.GroupName) ? "Unassigned" : t.GroupName;
            if (!existingByGroup.TryGetValue(g, out var list))
            {
                list = [];
                existingByGroup[g] = list;
            }
            list.Add(t);
        }

        var stagedByGroup = new Dictionary<string, List<AudioTrackItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var src in stagedSourceTracks)
        {
            string g = stagedGroupOverrides.TryGetValue(src.Id, out string? custom) && !string.IsNullOrWhiteSpace(custom)
                ? custom
                : (string.IsNullOrWhiteSpace(defaultTargetGroup) ? "Apollo Transfer" : defaultTargetGroup);
            if (!stagedByGroup.TryGetValue(g, out var list))
            {
                list = [];
                stagedByGroup[g] = list;
            }

            list.Add(new AudioTrackItem
            {
                Id = $"staged:{src.Id}",
                Title = src.Title,
                Artist = src.Artist,
                Album = src.Album,
                Year = src.Year,
                Format = src.Format,
                Bitrate = src.Bitrate,
                DurationSec = src.DurationSec,
                SizeMb = src.SizeMb,
                Folder = src.Folder,
                GroupName = g,
                SubtitleLines = src.SubtitleLines,
                IsStagedPreview = true,
                SourceFilePath = src.SourceFilePath
            });
        }

        var discoveredGroups = existingByGroup.Keys
            .Concat(stagedByGroup.Keys)
            .Concat(customCollections)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        List<string> allGroupNames;
        if (explicitGroupOrder is { Count: > 0 })
        {
            var presentSet = new HashSet<string>(discoveredGroups, StringComparer.OrdinalIgnoreCase);
            allGroupNames = explicitGroupOrder
                .Where(presentSet.Contains)
                .Concat(discoveredGroups.Where(g => !explicitGroupOrder.Contains(g, StringComparer.OrdinalIgnoreCase)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            allGroupNames = discoveredGroups;
        }

        var result = new List<TrackListEntry>();
        foreach (string groupName in allGroupNames)
        {
            existingByGroup.TryGetValue(groupName, out var existingItems);
            stagedByGroup.TryGetValue(groupName, out var stagedItems);
            int existingCount = existingItems?.Count ?? 0;
            int stagedCount = stagedItems?.Count ?? 0;
            int count = existingCount + stagedCount;
            bool isStagedOnly = existingCount == 0 && stagedCount > 0;
            string key = $"fmdest:{groupName}";
            bool expanded = !collapsedKeys.Contains(key);

            result.Add(new TrackListEntry
            {
                IsHeader = true,
                HeaderKey = key,
                HeaderTitle = groupName,
                ItemCount = count,
                ExistingCount = existingCount,
                StagedCount = stagedCount,
                IsExpanded = expanded,
                IsStagedHeader = isStagedOnly,
                IsCollectionHeader = true,
                HasSelectedTracks = (existingItems?.Any(t => t.IsDestSelected) ?? false),
                HasPlayingTrack = (existingItems?.Any(t => t.IsNowPlaying) ?? false) || (stagedItems?.Any(t => t.IsNowPlaying) ?? false)
            });

            if (!expanded)
            {
                continue;
            }

            if (sortField == TrackSortField.Order)
            {
                IReadOnlyList<string>? customOrder = null;
                customOrderByGroup?.TryGetValue(groupName, out customOrder);
                var combined = ApplyGroupTrackOrder(
                    existingItems ?? (IReadOnlyList<AudioTrackItem>)Array.Empty<AudioTrackItem>(),
                    stagedItems ?? (IReadOnlyList<AudioTrackItem>)Array.Empty<AudioTrackItem>(),
                    customOrder);
                if (descending && customOrder is null)
                {
                    combined = combined.Reverse().ToList();
                }

                for (int i = 0; i < combined.Count; i++)
                {
                    result.Add(new TrackListEntry
                    {
                        IsHeader = false,
                        Track = combined[i],
                        CollectionPosition = i + 1
                    });
                }
            }
            else
            {
                int pos = 1;
                if (existingItems is not null)
                {
                    foreach (var item in existingItems)
                    {
                        result.Add(new TrackListEntry { IsHeader = false, Track = item, CollectionPosition = pos++ });
                    }
                }

                if (stagedItems is not null)
                {
                    foreach (var item in stagedItems)
                    {
                        result.Add(new TrackListEntry { IsHeader = false, Track = item, CollectionPosition = pos++ });
                    }
                }
            }
        }

        return result;
    }

    public static (string Title, string Message)? BuildBpbWarningNotification(WalkmanDeviceSummary? device)
    {
        var bpb = device?.LiveIdentity?.BpbDiagnostics ?? device?.DetectionProbe?.BpbDiagnostics;
        if (device is null || bpb is null || !bpb.HasWindowsFormatWarning)
        {
            return null;
        }

        string msg = string.IsNullOrWhiteSpace(bpb.WarningMessage)
            ? $"Windows-formatted FAT16 detected (OEM '{bpb.OemName}', ReservedSectors={bpb.ReservedSectors}). Format on the Walkman device first to avoid FORMAT ERROR."
            : bpb.WarningMessage;

        return ($"FAT16 Format Warning ({device.Model})", msg);
    }

    public static IReadOnlyList<AudioTrackItem> ApplyGroupTrackOrder(
        IReadOnlyList<AudioTrackItem> existing,
        IReadOnlyList<AudioTrackItem> staged,
        IReadOnlyList<string>? customOrderIds)
    {
        if (customOrderIds is null || customOrderIds.Count == 0)
        {
            var merged = new List<AudioTrackItem>(existing.Count + staged.Count);
            merged.AddRange(existing);
            merged.AddRange(staged);
            return merged;
        }

        var byId = new Dictionary<string, AudioTrackItem>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in existing)
        {
            byId[item.Id] = item;
        }
        foreach (var item in staged)
        {
            byId[item.Id] = item;
        }

        var result = new List<AudioTrackItem>(byId.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string id in customOrderIds)
        {
            if (byId.TryGetValue(id, out var item) && seen.Add(id))
            {
                result.Add(item);
            }
        }

        foreach (var item in existing)
        {
            if (seen.Add(item.Id))
            {
                result.Add(item);
            }
        }

        foreach (var item in staged)
        {
            if (seen.Add(item.Id))
            {
                result.Add(item);
            }
        }

        return result;
    }

    public static bool MoveTrackInOrder(List<string> orderedIds, string trackId, int delta)
    {
        int idx = orderedIds.FindIndex(id => string.Equals(id, trackId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            return false;
        }

        int targetIdx = Math.Clamp(idx + delta, 0, orderedIds.Count - 1);
        if (targetIdx == idx)
        {
            return false;
        }

        orderedIds.RemoveAt(idx);
        orderedIds.Insert(targetIdx, trackId);
        return true;
    }

    public static bool SetTrackPositionInOrder(List<string> orderedIds, string trackId, int oneBasedPosition)
    {
        int idx = orderedIds.FindIndex(id => string.Equals(id, trackId, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            return false;
        }

        int targetIdx = Math.Clamp(oneBasedPosition - 1, 0, orderedIds.Count - 1);
        if (targetIdx == idx)
        {
            return false;
        }

        orderedIds.RemoveAt(idx);
        orderedIds.Insert(targetIdx, trackId);
        return true;
    }

    public static IEnumerable<AudioTrackItem> SortTracks(IEnumerable<AudioTrackItem> tracks, TrackSortField field, bool descending)
    {
        if (field == TrackSortField.AlbumTrackNumber)
        {
            return descending
                ? tracks.OrderByDescending(t => t.Album, StringComparer.OrdinalIgnoreCase)
                        .ThenByDescending(t => t.TrackNumber > 0 ? t.TrackNumber : -1)
                        .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
                : tracks.OrderBy(t => string.IsNullOrWhiteSpace(t.Album) ? "\uFFFF" : t.Album, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(t => t.TrackNumber > 0 ? t.TrackNumber : int.MaxValue)
                        .ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase);
        }

        IOrderedEnumerable<AudioTrackItem> ordered = field switch
        {
            TrackSortField.Artist => descending ? tracks.OrderByDescending(t => t.Artist, StringComparer.OrdinalIgnoreCase) : tracks.OrderBy(t => t.Artist, StringComparer.OrdinalIgnoreCase),
            TrackSortField.Album => descending ? tracks.OrderByDescending(t => t.Album, StringComparer.OrdinalIgnoreCase) : tracks.OrderBy(t => t.Album, StringComparer.OrdinalIgnoreCase),
            TrackSortField.Format => descending ? tracks.OrderByDescending(t => t.Format, StringComparer.OrdinalIgnoreCase) : tracks.OrderBy(t => t.Format, StringComparer.OrdinalIgnoreCase),
            TrackSortField.Duration => descending ? tracks.OrderByDescending(t => t.DurationSec) : tracks.OrderBy(t => t.DurationSec),
            TrackSortField.Size => descending ? tracks.OrderByDescending(t => t.SizeMb) : tracks.OrderBy(t => t.SizeMb),
            TrackSortField.Rating => descending ? tracks.OrderByDescending(t => t.Rating) : tracks.OrderBy(t => t.Rating),
            TrackSortField.Folder => descending ? tracks.OrderByDescending(t => t.Folder, StringComparer.OrdinalIgnoreCase) : tracks.OrderBy(t => t.Folder, StringComparer.OrdinalIgnoreCase),
            TrackSortField.Subtitles => descending ? tracks.OrderBy(t => t.HasSubtitles) : tracks.OrderByDescending(t => t.HasSubtitles),
            _ => descending ? tracks.OrderByDescending(t => t.Title, StringComparer.OrdinalIgnoreCase) : tracks.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase)
        };

        return ordered.ThenBy(t => t.Title, StringComparer.OrdinalIgnoreCase);
    }

    private static void SyncEntryCollection(BulkObservableCollection<TrackListEntry> target, IReadOnlyList<TrackListEntry> source)
    {
        target.ReplaceAll(source);
    }

    // Player Collections & Staged Drag-and-Drop Preview Commands
    [RelayCommand]
    private void RequestCreateCollection()
    {
        RequestInputDialog(
            "New Player Collection",
            "Create a new OMGAUDIO collection group (baked into player tables on Transfer):",
            "New Collection",
            "Create",
            name =>
            {
                string destId = SelectedDestLocation?.Id ?? string.Empty;
                if (!_customCollectionsByDevice.TryGetValue(destId, out var list))
                {
                    list = [];
                    _customCollectionsByDevice[destId] = list;
                }

                if (!list.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(name);
                }

                if (!FmAvailableGroups.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    FmAvailableGroups.Add(name);
                }

                FmTargetGroupName = name;
                RebuildFileManagerDestEntries();
                ShowToast("Collection Created", $"'{name}' ready for staged tracks.", ToastSeverity.Success);
            });
    }

    [RelayCommand]
    private void RequestRenameCollection(string? currentName)
    {
        if (string.IsNullOrWhiteSpace(currentName))
        {
            return;
        }

        RequestInputDialog(
            "Rename Collection",
            $"Rename '{currentName}' (changes are committed on Transfer):",
            currentName,
            "Rename",
            newName =>
            {
                string destId = SelectedDestLocation?.Id ?? string.Empty;
                if (_customCollectionsByDevice.TryGetValue(destId, out var list))
                {
                    int idx = list.FindIndex(c => string.Equals(c, currentName, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0)
                    {
                        list[idx] = newName;
                    }
                }

                foreach (var key in _stagedTrackGroups.Where(kv => string.Equals(kv.Value, currentName, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
                {
                    _stagedTrackGroups[key] = newName;
                }

                foreach (var trk in FileManagerDestTracks.Where(t => string.Equals(t.GroupName, currentName, StringComparison.OrdinalIgnoreCase)))
                {
                    trk.GroupName = newName;
                }

                if (string.Equals(FmTargetGroupName, currentName, StringComparison.OrdinalIgnoreCase))
                {
                    FmTargetGroupName = newName;
                }

                string oldOrderKey = $"{destId}::{currentName}";
                string newOrderKey = $"{destId}::{newName}";
                if (_collectionTrackOrder.Remove(oldOrderKey, out var existingOrder))
                {
                    _collectionTrackOrder[newOrderKey] = existingOrder;
                }

                if (_collectionOrderByDevice.TryGetValue(destId, out var groupOrder))
                {
                    int gIdx = groupOrder.FindIndex(g => string.Equals(g, currentName, StringComparison.OrdinalIgnoreCase));
                    if (gIdx >= 0)
                    {
                        groupOrder[gIdx] = newName;
                    }
                }

                if (!_pendingGroupRenamesByDevice.TryGetValue(destId, out var renames))
                {
                    renames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    _pendingGroupRenamesByDevice[destId] = renames;
                }
                renames[currentName] = newName;
                HasPendingDeviceTableChanges = true;

                RebuildFileManagerDestEntries();
            });
    }

    public void StageTrackToCollection(AudioTrackItem sourceTrack, string? collectionName)
    {
        sourceTrack.IsSelected = true;
        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            _stagedTrackGroups[sourceTrack.Id] = collectionName;
            if (!FmAvailableGroups.Contains(collectionName, StringComparer.OrdinalIgnoreCase))
            {
                FmAvailableGroups.Add(collectionName);
            }
        }

        NotifySelectionChanged();
    }

    public void UnstagePreviewTrack(AudioTrackItem stagedPreview)
    {
        string rawId = stagedPreview.Id.StartsWith("staged:", StringComparison.Ordinal)
            ? stagedPreview.Id["staged:".Length..]
            : stagedPreview.Id;

        var src = GetUnfilteredFileManagerSourceTracks().FirstOrDefault(t => t.Id == rawId)
            ?? _allTracks.FirstOrDefault(t => t.Id == rawId);
        if (src is not null)
        {
            src.IsSelected = false;
        }

        _stagedTrackGroups.Remove(rawId);
        NotifySelectionChanged();
    }

    // Legacy SonicStage Tag & Lyrics Editor Properties
    [ObservableProperty] private string _tagEditAlbumArtist = string.Empty;
    [ObservableProperty] private string _tagEditGenre = "Electronic";
    [ObservableProperty] private string _tagEditTrackNumber = "1";
    [ObservableProperty] private string _tagEditComposer = string.Empty;
    [ObservableProperty] private string _tagEditSortTitle = string.Empty;
    [ObservableProperty] private string _tagEditArtistLinks = string.Empty;
    [ObservableProperty] private int _tagEditRating = 4;
    [ObservableProperty] private string _tagEditComment = string.Empty;
    [ObservableProperty] private string _lyricsLanguage = "Auto";

    public IReadOnlyList<string> AvailableLyricsLanguages { get; } = ["Auto", "ENG", "JPN", "RUS"];

    [RelayCommand]
    private void SetTagEditRating(string? ratingStr)
    {
        if (int.TryParse(ratingStr, out int rating))
        {
            TagEditRating = Math.Clamp(rating, 0, 5);
        }
    }

    public bool TagRatingAtLeast1 => TagEditRating >= 1;
    public bool TagRatingAtLeast2 => TagEditRating >= 2;
    public bool TagRatingAtLeast3 => TagEditRating >= 3;
    public bool TagRatingAtLeast4 => TagEditRating >= 4;
    public bool TagRatingAtLeast5 => TagEditRating >= 5;

    partial void OnTagEditRatingChanged(int value)
    {
        OnPropertyChanged(nameof(TagRatingAtLeast1));
        OnPropertyChanged(nameof(TagRatingAtLeast2));
        OnPropertyChanged(nameof(TagRatingAtLeast3));
        OnPropertyChanged(nameof(TagRatingAtLeast4));
        OnPropertyChanged(nameof(TagRatingAtLeast5));
    }

    partial void OnTagEditYearChanged(string value)
    {
        string digits = new string(value.Where(char.IsDigit).Take(4).ToArray());
        if (!string.Equals(digits, value, StringComparison.Ordinal))
        {
            TagEditYear = digits;
        }
    }

    partial void OnTagEditTrackNumberChanged(string value)
    {
        string digits = new string(value.Where(char.IsDigit).Take(3).ToArray());
        if (!string.Equals(digits, value, StringComparison.Ordinal))
        {
            TagEditTrackNumber = digits;
        }
    }

    [RelayCommand]
    private void SetTargetCollection(string? collectionName)
    {
        if (string.IsNullOrWhiteSpace(collectionName)) return;
        if (!FmAvailableGroups.Contains(collectionName, StringComparer.OrdinalIgnoreCase))
        {
            FmAvailableGroups.Add(collectionName);
        }
        FmTargetGroupName = collectionName;
        ShowToast("Target Collection", $"New transfers will stage into '{collectionName}'.", ToastSeverity.Info);
    }

    [RelayCommand]
    private void DeleteCollection(string? collectionName)
    {
        if (string.IsNullOrWhiteSpace(collectionName))
        {
            return;
        }

        var destDev = DestDeviceSummary;
        string destId = SelectedDestLocation?.Id ?? string.Empty;
        var existingTracks = GetUnfilteredFileManagerDestTracks()
            .Where(t => string.Equals(string.IsNullOrWhiteSpace(t.GroupName) ? "Unassigned" : t.GroupName, collectionName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (existingTracks.Count == 0 && destDev?.IsLiveHardware != true)
        {
            ExecuteDeleteCollectionImmediate(destId, collectionName);
            ShowToast("Collection Removed", $"Removed collection '{collectionName}'.", ToastSeverity.Info);
            return;
        }

        string destName = destDev?.Model ?? SelectedDestLocation?.Label ?? "destination";
        string msg = existingTracks.Count > 0
            ? $"Permanently delete collection '{collectionName}' and all {existingTracks.Count} track(s) inside it from {destName}? Free MagicGate/storage slots will be reclaimed."
            : $"Permanently delete collection '{collectionName}' from {destName}?";
        string confirmLabel = existingTracks.Count > 0
            ? $"Delete Collection ({existingTracks.Count} tracks)"
            : "Delete Collection";

        RequestConfirmation(
            $"Delete Collection '{collectionName}'?",
            msg,
            confirmLabel,
            isDestructive: true,
            () => _ = ExecuteDeleteCollectionAsync(destDev, destId, collectionName, existingTracks));
    }

    private void ExecuteDeleteCollectionImmediate(string destId, string collectionName)
    {
        if (_customCollectionsByDevice.TryGetValue(destId, out var list))
        {
            list.RemoveAll(c => string.Equals(c, collectionName, StringComparison.OrdinalIgnoreCase));
        }

        if (_collectionOrderByDevice.TryGetValue(destId, out var orderList))
        {
            orderList.RemoveAll(c => string.Equals(c, collectionName, StringComparison.OrdinalIgnoreCase));
        }

        var sourcePool = GetUnfilteredFileManagerSourceTracks();
        foreach (string key in _stagedTrackGroups.Where(kv => string.Equals(kv.Value, collectionName, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Key).ToList())
        {
            _stagedTrackGroups.Remove(key);
            var src = sourcePool.FirstOrDefault(t => t.Id == key) ?? _allTracks.FirstOrDefault(t => t.Id == key);
            if (src is not null)
            {
                src.IsSelected = false;
            }
        }

        _collectionTrackOrder.Remove($"{destId}::{collectionName}");
        RebuildFileManagerDestEntries();
        NotifySelectionChanged();
    }

    private async Task ExecuteDeleteCollectionAsync(
        WalkmanDeviceSummary? destDev,
        string destId,
        string collectionName,
        List<AudioTrackItem> existingTracks)
    {
        try
        {
            int count = Math.Max(1, existingTracks.Count);
            TransferJob = new ActiveTransferJob(
                Guid.NewGuid().ToString("N"),
                $"Removing '{collectionName}'...",
                $"Deleting from {destDev?.Model ?? "destination"}",
                "OMGAUDIO Delete",
                0,
                count,
                5,
                count,
                false,
                IsDeleteJob: true);

            ExecuteDeleteCollectionImmediate(destId, collectionName);

            if (destDev is { IsLiveHardware: true, LiveIdentity: not null })
            {
                var progress = CreateDeleteProgressReporter(destDev.Model, count);
                await _backend.DeleteDeviceCollectionAsync(destDev.LiveIdentity, collectionName, deleteTracks: true, progress: progress);
                await RescanUsbHardwareAsync();
            }
            else
            {
                foreach (var trk in existingTracks)
                {
                    trk.IsDestSelected = false;
                    trk.Locations.Remove(destId);
                    if (trk.Locations.Count == 0)
                    {
                        _allTracks.Remove(trk);
                    }
                }

                RefreshAllLists();
            }

            TransferJob = new ActiveTransferJob(
                Guid.NewGuid().ToString("N"),
                $"Deleted '{collectionName}'",
                $"Removed {existingTracks.Count} track(s) from {destDev?.Model ?? "destination"}",
                "OMGAUDIO Delete",
                count,
                count,
                100,
                0,
                true,
                IsDeleteJob: true);

            ShowToast("Collection Deleted", $"Deleted '{collectionName}' ({existingTracks.Count} track(s)).", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            TransferJob = null;
            ShowToast("Collection Delete Failed", ex.Message, ToastSeverity.Error);
        }
    }

    // COD-05 Handoff Accessors & Session BPB Warning Surfacing
    public string GetStagedCollectionName(string sourceTrackId)
    {
        if (_stagedTrackGroups.TryGetValue(sourceTrackId, out string? custom) && !string.IsNullOrWhiteSpace(custom))
        {
            return custom;
        }

        return string.IsNullOrWhiteSpace(FmTargetGroupName) ? "Apollo Transfer" : FmTargetGroupName.Trim();
    }

    public IReadOnlyList<string> GetEffectiveCollectionOrder(string destId, string collectionName)
    {
        return EnsureCollectionOrderList(destId, collectionName);
    }

    public int? GetStagedTrackTargetIndex(string sourceTrackId, string? collectionName = null)
    {
        string destId = SelectedDestLocation?.Id ?? string.Empty;
        string group = string.IsNullOrWhiteSpace(collectionName) ? GetStagedCollectionName(sourceTrackId) : collectionName;
        var order = EnsureCollectionOrderList(destId, group);
        string stagedId = sourceTrackId.StartsWith("staged:", StringComparison.Ordinal)
            ? sourceTrackId
            : $"staged:{sourceTrackId}";
        int idx = order.FindIndex(id => string.Equals(id, stagedId, StringComparison.OrdinalIgnoreCase));
        return idx >= 0 ? idx : null;
    }

    public List<AudioTrackItem> OrderSelectedTracksForTransfer(IReadOnlyList<AudioTrackItem> selected)
    {
        string destId = SelectedDestLocation?.Id ?? string.Empty;
        var byGroup = selected.GroupBy(t => GetStagedCollectionName(t.Id), StringComparer.OrdinalIgnoreCase);
        var ordered = new List<AudioTrackItem>(selected.Count);
        foreach (var grp in byGroup)
        {
            var orderList = EnsureCollectionOrderList(destId, grp.Key);
            var lookup = grp.ToDictionary(t => $"staged:{t.Id}", StringComparer.OrdinalIgnoreCase);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in orderList)
            {
                if (lookup.TryGetValue(id, out var item) && seen.Add(item.Id))
                {
                    ordered.Add(item);
                }
            }

            foreach (var item in grp)
            {
                if (seen.Add(item.Id))
                {
                    ordered.Add(item);
                }
            }
        }

        return ordered;
    }

    public void CheckAndSurfaceBpbWarnings(IEnumerable<WalkmanDeviceSummary> devices)
    {
        foreach (var dev in devices)
        {
            CheckAndSurfaceBpbWarningForDevice(dev);
        }
    }

    public void CheckAndSurfaceBpbWarningForDevice(WalkmanDeviceSummary? device)
    {
        if (device is null)
        {
            return;
        }

        var notice = BuildBpbWarningNotification(device);
        if (notice is null)
        {
            return;
        }

        var bpb = device.LiveIdentity?.BpbDiagnostics ?? device.DetectionProbe?.BpbDiagnostics;
        string dedupKey = $"{device.Id}|{bpb?.OemName}|{bpb?.ReservedSectors}";
        if (_notifiedBpbWarningKeys.Add(dedupKey))
        {
            ShowToast(notice.Value.Title, notice.Value.Message, ToastSeverity.Warning, durationSeconds: 10.0);
        }
    }

    public void ClearBpbWarningNotification(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        string prefix = $"{deviceId}|";
        _notifiedBpbWarningKeys.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public void ClearAllBpbWarningNotifications() => _notifiedBpbWarningKeys.Clear();

    public static (string Title, string Message)? BuildExperimentalDeviceNotification(WalkmanDeviceSummary? device)
    {
        if (device is null || device.IsDemoDevice || device.SupportStatus == SupportStatus.Verified)
        {
            return null;
        }

        return device.SupportStatus switch
        {
            SupportStatus.Experimental => (
                $"Experimental Profile: {device.Model}",
                $"{device.Model} ({device.Series}) has not been hardware-verified yet. Please back up your OMGAUDIO folder before writing tracks."),
            SupportStatus.Unsupported => (
                $"Unsupported Profile: {device.Model}",
                $"{device.Model} is not currently supported for write operations. Export telemetry via Dump Wizard to help add support."),
            _ => null
        };
    }

    public static (string Title, string Message)? BuildUnrecognizedSonyProbeNotification(DeviceDetectionProbe? probe)
    {
        if (probe is null)
        {
            return null;
        }

        return (
            "Unrecognized Sony Walkman",
            $"Detected Sony USB device (0x{probe.VendorId:X4}:0x{probe.ProductId:X4} at {probe.VolumeRootPath}) not yet in devices.json. Export a diagnostic dump in Device & System → Dump Wizard to contribute its USB IDs.");
    }

    public void CheckAndSurfaceSupportTierWarnings(
        IEnumerable<WalkmanDeviceSummary> devices,
        IEnumerable<DeviceDetectionProbe>? unrecognizedSonyProbes = null)
    {
        foreach (var dev in devices)
        {
            CheckAndSurfaceSupportTierWarningForDevice(dev);
        }

        CheckAndSurfaceUnrecognizedSonyProbes(unrecognizedSonyProbes);
    }

    public void CheckAndSurfaceSupportTierWarningForDevice(WalkmanDeviceSummary? device)
    {
        if (device is null)
        {
            return;
        }

        var notice = BuildExperimentalDeviceNotification(device);
        if (notice is null)
        {
            return;
        }

        string dedupKey = $"exp|{device.Id}|{device.SupportStatus}";
        if (_notifiedSupportTierKeys.Add(dedupKey))
        {
            ShowToast(notice.Value.Title, notice.Value.Message, ToastSeverity.Warning, durationSeconds: 9.0);
        }
    }

    public void CheckAndSurfaceUnrecognizedSonyProbes(IEnumerable<DeviceDetectionProbe>? probes)
    {
        if (probes is null)
        {
            return;
        }

        foreach (var p in probes)
        {
            var notice = BuildUnrecognizedSonyProbeNotification(p);
            if (notice is null)
            {
                continue;
            }

            string dedupKey = $"unrec|0x{p.VendorId:X4}:0x{p.ProductId:X4}|{p.VolumeSerialNumber:X8}|{p.VolumeRootPath}";
            if (_notifiedSupportTierKeys.Add(dedupKey))
            {
                ShowToast(notice.Value.Title, notice.Value.Message, ToastSeverity.Warning, durationSeconds: 10.0);
            }
        }
    }

    public void ClearSupportTierWarningNotification(string? deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
        {
            return;
        }

        string prefix = $"exp|{deviceId}|";
        _notifiedSupportTierKeys.RemoveWhere(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    public void ClearAllSupportTierWarningNotifications() => _notifiedSupportTierKeys.Clear();

    // Destination Reordering & Drag-and-Drop Helpers
    private List<string> EnsureCollectionOrderList(string destId, string groupName)
    {
        string key = $"{destId}::{groupName}";
        var existing = GetUnfilteredFileManagerDestTracks()
            .Where(t => string.Equals(string.IsNullOrWhiteSpace(t.GroupName) ? "Unassigned" : t.GroupName, groupName, StringComparison.OrdinalIgnoreCase))
            .Select(t => t.Id)
            .ToList();
        var staged = GetUnfilteredFileManagerSourceTracks()
            .Where(t => t.IsSelected && string.Equals(GetStagedCollectionName(t.Id), groupName, StringComparison.OrdinalIgnoreCase))
            .Select(t => $"staged:{t.Id}")
            .ToList();

        var validIds = existing.Concat(staged).ToList();
        if (!_collectionTrackOrder.TryGetValue(key, out var list))
        {
            list = new List<string>(validIds);
            _collectionTrackOrder[key] = list;
            return list;
        }

        var validSet = new HashSet<string>(validIds, StringComparer.OrdinalIgnoreCase);
        list.RemoveAll(id => !validSet.Contains(id));
        foreach (string id in validIds)
        {
            if (!list.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(id);
            }
        }

        return list;
    }

    public void StageTrackToCollectionAt(AudioTrackItem sourceTrack, string? collectionName, string? beforeTrackId)
    {
        string group = string.IsNullOrWhiteSpace(collectionName)
            ? (string.IsNullOrWhiteSpace(FmTargetGroupName) ? "Apollo Transfer" : FmTargetGroupName.Trim())
            : collectionName.Trim();

        sourceTrack.IsSelected = true;
        _stagedTrackGroups[sourceTrack.Id] = group;
        if (!FmAvailableGroups.Contains(group, StringComparer.OrdinalIgnoreCase))
        {
            FmAvailableGroups.Add(group);
        }

        string destId = SelectedDestLocation?.Id ?? string.Empty;
        FmDestSortField = TrackSortField.Order;
        var list = EnsureCollectionOrderList(destId, group);
        string stagedId = $"staged:{sourceTrack.Id}";
        list.RemoveAll(id => string.Equals(id, stagedId, StringComparison.OrdinalIgnoreCase));

        int insertIdx = string.IsNullOrWhiteSpace(beforeTrackId)
            ? -1
            : list.FindIndex(id => string.Equals(id, beforeTrackId, StringComparison.OrdinalIgnoreCase));
        if (insertIdx >= 0)
        {
            list.Insert(insertIdx, stagedId);
        }
        else
        {
            list.Add(stagedId);
        }

        NotifySelectionChanged();
    }

    public void ReorderDestTrackBefore(string draggedTrackId, AudioTrackItem targetTrack)
    {
        if (string.IsNullOrWhiteSpace(draggedTrackId) ||
            string.Equals(draggedTrackId, targetTrack.Id, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        string destId = SelectedDestLocation?.Id ?? string.Empty;
        string targetGroup = string.IsNullOrWhiteSpace(targetTrack.GroupName) ? "Unassigned" : targetTrack.GroupName;

        MoveTrackGroupAssignment(destId, draggedTrackId, targetGroup);
        FmDestSortField = TrackSortField.Order;
        var list = EnsureCollectionOrderList(destId, targetGroup);
        list.RemoveAll(id => string.Equals(id, draggedTrackId, StringComparison.OrdinalIgnoreCase));

        int insertIdx = list.FindIndex(id => string.Equals(id, targetTrack.Id, StringComparison.OrdinalIgnoreCase));
        if (insertIdx >= 0)
        {
            list.Insert(insertIdx, draggedTrackId);
        }
        else
        {
            list.Add(draggedTrackId);
        }

        if (!draggedTrackId.StartsWith("staged:", StringComparison.Ordinal))
        {
            HasPendingDeviceTableChanges = true;
        }

        RebuildFileManagerDestEntries();
    }

    public void MoveDestTrackToCollection(string draggedTrackId, string targetCollectionName)
    {
        if (string.IsNullOrWhiteSpace(draggedTrackId) || string.IsNullOrWhiteSpace(targetCollectionName))
        {
            return;
        }

        string destId = SelectedDestLocation?.Id ?? string.Empty;
        MoveTrackGroupAssignment(destId, draggedTrackId, targetCollectionName);
        FmDestSortField = TrackSortField.Order;
        var list = EnsureCollectionOrderList(destId, targetCollectionName);
        list.RemoveAll(id => string.Equals(id, draggedTrackId, StringComparison.OrdinalIgnoreCase));
        list.Add(draggedTrackId);
        if (!draggedTrackId.StartsWith("staged:", StringComparison.Ordinal))
        {
            HasPendingDeviceTableChanges = true;
        }
        RebuildFileManagerDestEntries();
    }

    private void MoveTrackGroupAssignment(string destId, string trackId, string targetGroup)
    {
        if (trackId.StartsWith("staged:", StringComparison.Ordinal))
        {
            string rawId = trackId["staged:".Length..];
            string prevGroup = GetStagedCollectionName(rawId);
            if (!string.Equals(prevGroup, targetGroup, StringComparison.OrdinalIgnoreCase) &&
                _collectionTrackOrder.TryGetValue($"{destId}::{prevGroup}", out var prevList))
            {
                prevList.RemoveAll(id => string.Equals(id, trackId, StringComparison.OrdinalIgnoreCase));
            }
            _stagedTrackGroups[rawId] = targetGroup;
        }
        else
        {
            var destTrack = FileManagerDestTracks.FirstOrDefault(t => string.Equals(t.Id, trackId, StringComparison.OrdinalIgnoreCase));
            if (destTrack is not null)
            {
                string prevGroup = string.IsNullOrWhiteSpace(destTrack.GroupName) ? "Unassigned" : destTrack.GroupName;
                if (!string.Equals(prevGroup, targetGroup, StringComparison.OrdinalIgnoreCase) &&
                    _collectionTrackOrder.TryGetValue($"{destId}::{prevGroup}", out var prevList))
                {
                    prevList.RemoveAll(id => string.Equals(id, trackId, StringComparison.OrdinalIgnoreCase));
                }
                destTrack.GroupName = targetGroup;
            }
        }
    }

    // Track-Level ContextMenu Commands
    [RelayCommand]
    private void PlayTrackFromMenu(AudioTrackItem? track)
    {
        if (track is not null)
        {
            PlayOrToggleTrack(track);
        }
    }

    [RelayCommand]
    private void ToggleSourceTrackSelection(AudioTrackItem? track)
    {
        if (track is not null)
        {
            ToggleTrackSelectionAndRefresh(track);
        }
    }

    [RelayCommand]
    private void StageSourceTrackToTarget(AudioTrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        string group = string.IsNullOrWhiteSpace(FmTargetGroupName) ? "Apollo Transfer" : FmTargetGroupName.Trim();
        StageTrackToCollection(track, group);
        ShowToast("Track Staged", $"'{track.Title}' staged into '{group}'.", ToastSeverity.Info);
    }

    [RelayCommand]
    private void StageLibraryTrackForTransfer(AudioTrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        track.IsSelected = true;
        NotifySelectionChanged();
        if (ActiveModuleId != ModuleIds.FileManager)
        {
            OpenModule(ModuleIds.FileManager);
        }
        ShowToast("Staged for Transfer", $"'{track.Title}' selected in File Manager.", ToastSeverity.Info);
    }

    [RelayCommand]
    private void OpenTrackInCustomization(AudioTrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        var matchingLoc = AllLocations.FirstOrDefault(l => track.Locations.Contains(l.Id));
        if (matchingLoc is not null)
        {
            CustomSelectedLocation = matchingLoc;
        }

        ActiveCustomTab = "tags";
        OpenModule(ModuleIds.Customization);
        SelectedTagTrack = track;
        SelectedSubtitleTrack = track;
    }

    [RelayCommand]
    private void MoveDestTrackUp(AudioTrackItem? track) => MoveDestTrackBy(track, -1);

    [RelayCommand]
    private void MoveDestTrackDown(AudioTrackItem? track) => MoveDestTrackBy(track, 1);

    private void MoveDestTrackBy(AudioTrackItem? track, int delta)
    {
        if (track is null)
        {
            return;
        }

        string destId = SelectedDestLocation?.Id ?? string.Empty;
        string group = string.IsNullOrWhiteSpace(track.GroupName) ? "Unassigned" : track.GroupName;
        FmDestSortField = TrackSortField.Order;
        var list = EnsureCollectionOrderList(destId, group);
        if (MoveTrackInOrder(list, track.Id, delta))
        {
            if (!track.IsStagedPreview)
            {
                HasPendingDeviceTableChanges = true;
            }
            RebuildFileManagerDestEntries();
        }
    }

    [RelayCommand]
    private void RequestSetDestTrackPosition(AudioTrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        string destId = SelectedDestLocation?.Id ?? string.Empty;
        string group = string.IsNullOrWhiteSpace(track.GroupName) ? "Unassigned" : track.GroupName;
        var list = EnsureCollectionOrderList(destId, group);
        int count = Math.Max(1, list.Count);
        int currentPos = Math.Max(1, list.FindIndex(id => string.Equals(id, track.Id, StringComparison.OrdinalIgnoreCase)) + 1);

        RequestInputDialog(
            "Set Track Position",
            $"Enter target position (1–{count}) in '{group}' for '{track.Title}':",
            currentPos.ToString(CultureInfo.InvariantCulture),
            "Move",
            input =>
            {
                if (int.TryParse(input.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int targetPos))
                {
                    FmDestSortField = TrackSortField.Order;
                    var orderList = EnsureCollectionOrderList(destId, group);
                    if (SetTrackPositionInOrder(orderList, track.Id, targetPos))
                    {
                        RebuildFileManagerDestEntries();
                    }
                    int clamped = Math.Clamp(targetPos, 1, Math.Max(1, orderList.Count));
                    ShowToast("Position Updated", $"'{track.Title}' moved to #{clamped} in '{group}'.", ToastSeverity.Info);
                }
            });
    }

    [RelayCommand]
    private void UnstageDestPreviewTrack(AudioTrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        string destId = SelectedDestLocation?.Id ?? string.Empty;
        string group = string.IsNullOrWhiteSpace(track.GroupName) ? "Unassigned" : track.GroupName;
        if (_collectionTrackOrder.TryGetValue($"{destId}::{group}", out var list))
        {
            list.RemoveAll(id => string.Equals(id, track.Id, StringComparison.OrdinalIgnoreCase));
        }

        UnstagePreviewTrack(track);
    }

    [RelayCommand]
    private void RequestDeleteSingleDestTrack(AudioTrackItem? track)
    {
        if (track is null || track.IsStagedPreview)
        {
            return;
        }

        var destDev = DestDeviceSummary;
        if (destDev is null)
        {
            return;
        }

        RequestConfirmation(
            $"Delete '{track.Title}'?",
            $"Permanently remove '{track.Title}' from {destDev.Model}? Free MagicGate/storage slots will be reclaimed.",
            "Delete Track",
            isDestructive: true,
            () => _ = ExecuteDeleteDestTracksAsync(destDev, [track]));
    }

    public void SetTrackRating(AudioTrackItem? track, int stars)
    {
        if (track is null)
        {
            return;
        }

        int next = track.Rating == stars ? 0 : Math.Clamp(stars, 0, 5);
        track.Rating = next;
        if (SelectedTagTrack?.Id == track.Id)
        {
            TagEditRating = next;
        }

        if (LibrarySortField == TrackSortField.Rating
            || FmSourceSortField == TrackSortField.Rating
            || FmDestSortField == TrackSortField.Rating
            || ConvSortField == TrackSortField.Rating)
        {
            RebuildV3ListEntries();
        }
    }

    [RelayCommand]
    private void RemoveTrackFromLibrary(AudioTrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        if (_allTracks.Remove(track))
        {
            RefreshAllLists();
            ShowToast("Removed from Library", $"Removed '{track.Title}' from Apollo library view.", ToastSeverity.Info);
        }
    }

    [RelayCommand]
    private void RequestDeleteTrackFromPc(AudioTrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        bool hasDiskFile = !string.IsNullOrWhiteSpace(track.SourceFilePath) && File.Exists(track.SourceFilePath);
        string prompt = hasDiskFile
            ? $"Permanently delete '{Path.GetFileName(track.SourceFilePath)}' from your PC disk and remove it from Apollo?"
            : $"Remove '{track.Title}' from Apollo library? (No local PC file path is attached.)";

        RequestConfirmation(
            $"Delete '{track.Title}' from PC?",
            prompt,
            "Delete File",
            isDestructive: true,
            () =>
            {
                if (hasDiskFile && !string.IsNullOrWhiteSpace(track.SourceFilePath))
                {
                    try
                    {
                        File.Delete(track.SourceFilePath);
                    }
                    catch (Exception ex)
                    {
                        ShowToast("Delete Failed", ex.Message, ToastSeverity.Error);
                        return;
                    }
                }

                _allTracks.Remove(track);
                RefreshAllLists();
                ShowToast("File Deleted", $"Deleted '{track.Title}'.", ToastSeverity.Info);
            });
    }

    [RelayCommand]
    private void RemoveFolderFromLibrary(string? folderName)
    {
        if (string.IsNullOrWhiteSpace(folderName))
        {
            return;
        }

        int removed = _allTracks.RemoveAll(t =>
            t.DeviceInstanceId is null &&
            string.Equals(string.IsNullOrWhiteSpace(t.Folder) ? "Root" : t.Folder, folderName, StringComparison.OrdinalIgnoreCase));
        if (removed > 0)
        {
            RefreshAllLists();
            ShowToast("Folder Removed", $"Unlinked '{folderName}' ({removed} track(s)) from Apollo library.", ToastSeverity.Info);
        }
    }

    [RelayCommand]
    private void ClearMyLibrary()
    {
        RequestConfirmation(
            "Clear My Library?",
            "Remove all local PC tracks from Apollo's library view? Your audio files on disk will NOT be deleted.",
            "Clear Library",
            isDestructive: false,
            () =>
            {
                _allTracks.RemoveAll(t => t.DeviceInstanceId is null && !t.IsDemoItem);
                _customFolderLocations.Clear();
                RebuildAllLocations();
                RefreshAllLists();
                ShowToast("Library Cleared", "All local PC folders unlinked from Apollo.", ToastSeverity.Info);
            });
    }

    [RelayCommand]
    private void SelectFolderTracks(TrackListEntry? header)
    {
        if (header is null || !header.IsHeader || string.IsNullOrWhiteSpace(header.HeaderKey))
        {
            return;
        }

        SelectFolderByKey(header.HeaderKey);
    }

    [RelayCommand]
    private void SelectFolderByKey(string? headerKey)
    {
        if (string.IsNullOrWhiteSpace(headerKey))
        {
            return;
        }

        int colon = headerKey.IndexOf(':');
        string scope = colon > 0 ? headerKey[..colon] : string.Empty;
        string folderName = colon > 0 ? headerKey[(colon + 1)..] : headerKey;

        if (IsTransferRunning && (scope.Equals("fmsrc", StringComparison.OrdinalIgnoreCase) || scope.Equals("fmdest", StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        IEnumerable<AudioTrackItem> pool = scope.ToLowerInvariant() switch
        {
            "lib" => FilteredLibraryTracks,
            "fmsrc" => FileManagerSourceTracks,
            "conv" => ConverterSourceTracks,
            "cust" => CustomizationTracks,
            "fmdest" => FileManagerDestTracks,
            _ => FileManagerSourceTracks
        };

        var matches = pool
            .Where(t => string.Equals(
                scope.Equals("fmdest", StringComparison.OrdinalIgnoreCase)
                    ? (string.IsNullOrWhiteSpace(t.GroupName) ? "Unassigned" : t.GroupName)
                    : (string.IsNullOrWhiteSpace(t.Folder) ? "Root" : t.Folder),
                folderName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            return;
        }

        if (scope.Equals("fmdest", StringComparison.OrdinalIgnoreCase))
        {
            bool selectAll = matches.Any(t => !t.IsDestSelected);
            foreach (var t in matches) t.IsDestSelected = selectAll;
            NotifyDestSelectionChanged();
        }
        else if (scope.Equals("lib", StringComparison.OrdinalIgnoreCase))
        {
            bool selectAll = matches.Any(t => !t.IsLibrarySelected);
            foreach (var t in matches) t.IsLibrarySelected = selectAll;
            foreach (var entry in LibraryEntries)
            {
                if (entry.IsHeader && string.Equals(entry.HeaderKey, headerKey, StringComparison.OrdinalIgnoreCase))
                {
                    entry.HasSelectedTracks = selectAll;
                }
            }
            OnPropertyChanged(nameof(LibrarySelectedCount));
        }
        else if (scope.Equals("cust", StringComparison.OrdinalIgnoreCase))
        {
            bool selectAll = matches.Any(t => !t.IsTagSelected);
            foreach (var t in matches) t.IsTagSelected = selectAll;
            NotifyTagSelectionChanged();
        }
        else
        {
            bool selectAll = matches.Any(t => !t.IsSelected);
            foreach (var t in matches) t.IsSelected = selectAll;
            NotifySelectionChanged();
        }
    }

    public int LibrarySelectedCount => FilteredLibraryTracks.Count(t => t.IsLibrarySelected);

    public void ToggleLibraryTrackSelection(AudioTrackItem? track)
    {
        if (track is null)
        {
            return;
        }

        track.IsLibrarySelected = !track.IsLibrarySelected;
        string folder = string.IsNullOrWhiteSpace(track.Folder) ? "Root" : track.Folder;
        string key = $"lib:{folder}";
        bool anySelected = FilteredLibraryTracks.Any(t =>
            string.Equals(string.IsNullOrWhiteSpace(t.Folder) ? "Root" : t.Folder, folder, StringComparison.OrdinalIgnoreCase) &&
            t.IsLibrarySelected);
        foreach (var entry in LibraryEntries)
        {
            if (entry.IsHeader && string.Equals(entry.HeaderKey, key, StringComparison.OrdinalIgnoreCase))
            {
                entry.HasSelectedTracks = anySelected;
            }
        }
        OnPropertyChanged(nameof(LibrarySelectedCount));
    }

    [RelayCommand]
    private void StageLibraryFolderForTransfer(string? headerKeyOrFolderName)
    {
        if (string.IsNullOrWhiteSpace(headerKeyOrFolderName))
        {
            return;
        }

        string folderName = headerKeyOrFolderName.StartsWith("lib:", StringComparison.OrdinalIgnoreCase)
            ? headerKeyOrFolderName["lib:".Length..]
            : headerKeyOrFolderName;

        var matches = FilteredLibraryTracks
            .Where(t => string.Equals(
                string.IsNullOrWhiteSpace(t.Folder) ? "Root" : t.Folder,
                folderName,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
        {
            return;
        }

        foreach (var t in matches)
        {
            t.IsSelected = true;
        }

        NotifySelectionChanged();
        ShowToast("Staged for Transfer", $"Staged {matches.Count} track(s) from '{folderName}' for File Manager transfer.", ToastSeverity.Info);
    }

    public void SelectTrackRange(
        IReadOnlyList<TrackListEntry> entries,
        AudioTrackItem anchor,
        AudioTrackItem target,
        bool isDestSelection,
        bool targetState)
    {
        if (IsTransferRunning &&
            (ReferenceEquals(entries, FileManagerSourceEntries) || ReferenceEquals(entries, FileManagerDestEntries)))
        {
            return;
        }

        int anchorIdx = -1;
        int targetIdx = -1;
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            if (!e.IsTrack || e.Track is null || e.Track.IsStagedPreview)
            {
                continue;
            }

            if (ReferenceEquals(e.Track, anchor) || string.Equals(e.Track.Id, anchor.Id, StringComparison.Ordinal))
            {
                anchorIdx = i;
            }
            if (ReferenceEquals(e.Track, target) || string.Equals(e.Track.Id, target.Id, StringComparison.Ordinal))
            {
                targetIdx = i;
            }
        }

        if (anchorIdx < 0 || targetIdx < 0)
        {
            return;
        }

        int start = Math.Min(anchorIdx, targetIdx);
        int end = Math.Max(anchorIdx, targetIdx);
        for (int i = start; i <= end; i++)
        {
            var e = entries[i];
            if (!e.IsTrack || e.Track is null || e.Track.IsStagedPreview)
            {
                continue;
            }

            if (isDestSelection)
            {
                e.Track.IsDestSelected = targetState;
            }
            else
            {
                e.Track.IsSelected = targetState;
            }
        }

        if (isDestSelection)
        {
            NotifyDestSelectionChanged();
        }
        else
        {
            NotifySelectionChanged();
        }
    }

    [RelayCommand]
    private void ClearLibrarySearch() => LibrarySearch = string.Empty;
    [RelayCommand]
    private void ClearFmLeftSearch() => FileManagerLeftSearch = string.Empty;
    [RelayCommand]
    private void ClearFmRightSearch() => FileManagerRightSearch = string.Empty;
    [RelayCommand]
    private void ClearConverterSearch() => ConverterSearch = string.Empty;
    [RelayCommand]
    private void ClearCustomSearch() => CustomTrackSearch = string.Empty;

    [RelayCommand]
    private void ShiftLyricsTiming(string? deltaStr)
    {
        if (!int.TryParse(deltaStr, out int deltaMs) || string.IsNullOrWhiteSpace(SubtitlesLrcText))
        {
            return;
        }

        var lines = SubtitlesLrcText.Split(['\r', '\n']);
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length >= 7 && line[0] == '[' && int.TryParse(line.AsSpan(1, 2), out int min) && line[3] == ':' && int.TryParse(line.AsSpan(4, 2), out int sec))
            {
                int totalMs = Math.Max(0, (min * 60 + sec) * 1000 + deltaMs);
                int newMin = totalMs / 60000;
                int newSec = (totalMs % 60000) / 1000;
                int closeIdx = line.IndexOf(']');
                if (closeIdx > 0)
                {
                    lines[i] = $"[{newMin:D2}:{newSec:D2}.00]{line[(closeIdx + 1)..]}";
                }
            }
        }

        SubtitlesLrcText = string.Join(Environment.NewLine, lines);
    }

    [RelayCommand]
    private void InsertPlayerTime()
    {
        int sec = Math.Max(0, ElapsedSec);
        string stamp = $"[{sec / 60:D2}:{sec % 60:D2}.00] ";
        SubtitlesLrcText = string.IsNullOrWhiteSpace(SubtitlesLrcText)
            ? stamp
            : SubtitlesLrcText.TrimEnd() + Environment.NewLine + stamp;
    }

    public Func<Task<string?>>? PickCoverFileHandler { get; set; }

    public Avalonia.Media.Imaging.Bitmap? TagEditCoverBitmap => SelectedTagTrack?.CoverArtwork;
    public bool HasTagEditCover => SelectedTagTrack?.CoverArtwork is not null;
    public string TagEditCoverStatusText => HasTagEditCover ? "Embedded / linked cover art active" : "No cover art assigned";

    [RelayCommand]
    private void RequestClearMyLibrary()
    {
        RequestConfirmation(
            "Clear My Library?",
            "Remove all local PC library folders and tracks from Apollo's catalog view? Your files on disk will not be deleted.",
            "Clear Library",
            isDestructive: true,
            () =>
            {
                int removed = _allTracks.RemoveAll(t => t.DeviceInstanceId is null && !t.IsDemoItem);
                RefreshAllLists();
                ShowToast("Library Cleared", $"Cleared {removed} local track(s) from My Library.", ToastSeverity.Info);
            });
    }

    [RelayCommand]
    private async Task ChooseTagCoverAsync()
    {
        if (SelectedTagTrack is null && !ConverterSourceTracks.Any())
        {
            ShowToast("No Track Selected", "Select a track in the list first.", ToastSeverity.Warning);
            return;
        }

        if (PickCoverFileHandler is null)
        {
            return;
        }

        string? path = await PickCoverFileHandler();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var bitmap = Avalonia.Media.Imaging.Bitmap.DecodeToWidth(stream, 256);
            var targets = CustomizationTracks.Where(t => t.IsTagSelected).ToList();
            if (targets.Count == 0 && SelectedTagTrack is not null)
            {
                targets.Add(SelectedTagTrack);
            }

            foreach (var t in targets)
            {
                t.CoverArtwork = bitmap;
            }

            OnPropertyChanged(nameof(TagEditCoverBitmap));
            OnPropertyChanged(nameof(HasTagEditCover));
            OnPropertyChanged(nameof(TagEditCoverStatusText));
            OnPropertyChanged(nameof(PlayerCoverBitmap));
            OnPropertyChanged(nameof(PlayerHasCoverBitmap));
            ShowToast("Cover Art Updated", $"Applied '{Path.GetFileName(path)}' to {targets.Count} track(s).", ToastSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowToast("Cover Load Failed", ex.Message, ToastSeverity.Error);
        }
    }

    [RelayCommand]
    private void RemoveTagCover()
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

        foreach (var t in targets)
        {
            t.CoverArtwork = null;
        }

        OnPropertyChanged(nameof(TagEditCoverBitmap));
        OnPropertyChanged(nameof(HasTagEditCover));
        OnPropertyChanged(nameof(TagEditCoverStatusText));
        OnPropertyChanged(nameof(PlayerCoverBitmap));
        OnPropertyChanged(nameof(PlayerHasCoverBitmap));
        ShowToast("Cover Art Removed", $"Cleared cover art on {targets.Count} track(s).", ToastSeverity.Info);
    }

    // COD-18 & COD-19: Stable Folder Order, Real Delete Progress, Post-Transfer Reset & Update Table Commit
    private void SeedDeviceGroupOrder(WalkmanDeviceSummary device)
    {
        if (device.GroupNames is null)
        {
            return;
        }

        var seeded = device.GroupNames
            .Where(g => !string.IsNullOrWhiteSpace(g) && !string.Equals(g, "All Tracks", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        _collectionOrderByDevice[device.Id] = seeded;
    }

    private List<string> EnsureDeviceGroupOrder(
        string destId,
        IEnumerable<string> deviceGroupNames,
        IEnumerable<string> existingTrackGroups,
        IEnumerable<string> stagedGroups,
        IEnumerable<string> customCollections)
    {
        if (!_collectionOrderByDevice.TryGetValue(destId, out var list))
        {
            list = new List<string>();
            foreach (string g in deviceGroupNames)
            {
                if (!string.IsNullOrWhiteSpace(g) && !list.Contains(g, StringComparer.OrdinalIgnoreCase))
                {
                    list.Add(g);
                }
            }
            _collectionOrderByDevice[destId] = list;
        }

        foreach (string g in existingTrackGroups.Concat(stagedGroups).Concat(customCollections))
        {
            if (!string.IsNullOrWhiteSpace(g) && !list.Contains(g, StringComparer.OrdinalIgnoreCase))
            {
                list.Add(g);
            }
        }

        return list;
    }

    public IReadOnlyList<string> GetDeviceCollectionOrder(string? destId = null)
    {
        string id = string.IsNullOrWhiteSpace(destId) ? (SelectedDestLocation?.Id ?? string.Empty) : destId;
        return _collectionOrderByDevice.TryGetValue(id, out var list) ? list.ToArray() : Array.Empty<string>();
    }

    public bool ReorderDestCollectionBefore(string draggedCollectionName, string targetCollectionName)
    {
        if (string.IsNullOrWhiteSpace(draggedCollectionName) ||
            string.IsNullOrWhiteSpace(targetCollectionName) ||
            string.Equals(draggedCollectionName, targetCollectionName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        string destId = SelectedDestLocation?.Id ?? string.Empty;
        var order = EnsureDeviceGroupOrder(destId, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        int fromIdx = order.FindIndex(g => string.Equals(g, draggedCollectionName, StringComparison.OrdinalIgnoreCase));
        int toIdx = order.FindIndex(g => string.Equals(g, targetCollectionName, StringComparison.OrdinalIgnoreCase));
        if (fromIdx < 0 || toIdx < 0 || fromIdx == toIdx)
        {
            return false;
        }

        string item = order[fromIdx];
        order.RemoveAt(fromIdx);
        int adjustedTarget = order.FindIndex(g => string.Equals(g, targetCollectionName, StringComparison.OrdinalIgnoreCase));
        order.Insert(Math.Max(0, adjustedTarget), item);

        HasPendingDeviceTableChanges = true;
        RebuildFileManagerDestEntries();
        return true;
    }

    public bool MoveDestCollectionBy(string collectionName, int delta)
    {
        if (string.IsNullOrWhiteSpace(collectionName) || delta == 0)
        {
            return false;
        }

        string destId = SelectedDestLocation?.Id ?? string.Empty;
        var order = EnsureDeviceGroupOrder(destId, Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());
        int idx = order.FindIndex(g => string.Equals(g, collectionName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0)
        {
            return false;
        }

        int targetIdx = Math.Clamp(idx + delta, 0, order.Count - 1);
        if (targetIdx == idx)
        {
            return false;
        }

        string item = order[idx];
        order.RemoveAt(idx);
        order.Insert(targetIdx, item);

        HasPendingDeviceTableChanges = true;
        RebuildFileManagerDestEntries();
        return true;
    }

    [RelayCommand]
    private void MoveCollectionUp(string? collectionName)
    {
        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            MoveDestCollectionBy(collectionName, -1);
        }
    }

    [RelayCommand]
    private void MoveCollectionDown(string? collectionName)
    {
        if (!string.IsNullOrWhiteSpace(collectionName))
        {
            MoveDestCollectionBy(collectionName, 1);
        }
    }

    [RelayCommand]
    private async Task CommitPendingDeviceChangesAsync()
    {
        var destDev = DestDeviceSummary;
        if (destDev is null || IsTransferRunning)
        {
            return;
        }

        string destId = destDev.Id;
        var destTracks = GetUnfilteredFileManagerDestTracks().ToList();
        var byGroup = destTracks
            .GroupBy(t => string.IsNullOrWhiteSpace(t.GroupName) ? "Unassigned" : t.GroupName!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

        var orderedGroups = EnsureDeviceGroupOrder(
            destId,
            destDev.GroupNames?.Where(g => !string.Equals(g, "All Tracks", StringComparison.OrdinalIgnoreCase)) ?? Array.Empty<string>(),
            byGroup.Keys,
            Array.Empty<string>(),
            _customCollectionsByDevice.TryGetValue(destId, out var c) ? c : Enumerable.Empty<string>()).ToList();

        var updatedGroupDtos = new List<Walkman.Contracts.Engines.TrackGroupDto>();
        int gid = 1;
        foreach (string groupName in orderedGroups)
        {
            if (!byGroup.TryGetValue(groupName, out var members) || members.Count == 0)
            {
                continue;
            }

            var orderList = EnsureCollectionOrderList(destId, groupName);
            var orderedMembers = ApplyGroupTrackOrder(members, Array.Empty<AudioTrackItem>(), orderList);
            int[] trackIds = orderedMembers.Where(t => t.DeviceTrackId.HasValue).Select(t => t.DeviceTrackId!.Value).ToArray();
            if (trackIds.Length == 0)
            {
                continue;
            }

            uint durMs = (uint)orderedMembers.Sum(t => (long)Math.Max(0, t.DurationSec) * 1000L);
            updatedGroupDtos.Add(new Walkman.Contracts.Engines.TrackGroupDto(gid++, groupName, string.Empty, string.Empty, durMs, trackIds));
        }

        if (destDev is { IsLiveHardware: true, LiveIdentity: not null })
        {
            TransferJob = new ActiveTransferJob(
                Guid.NewGuid().ToString("N"),
                "OMGAUDIO Tables",
                "Committing folder/track order & tags...",
                "Table Update",
                0,
                Math.Max(1, updatedGroupDtos.Count),
                25,
                2,
                false);

            _pendingGroupRenamesByDevice.TryGetValue(destId, out var renames);
            _pendingTagPatchesByDevice.TryGetValue(destId, out var tagPatches);

            await _backend.UpdateDeviceTableAsync(
                destDev.LiveIdentity,
                new Walkman.L1.FileManager.DeviceTableUpdateRequest(
                    updatedGroupDtos,
                    orderedGroups,
                    renames,
                    tagPatches));

            _pendingGroupRenamesByDevice.Remove(destId);
            _pendingTagPatchesByDevice.Remove(destId);
            HasPendingDeviceTableChanges = false;

            await RescanUsbHardwareAsync();

            TransferJob = new ActiveTransferJob(
                Guid.NewGuid().ToString("N"),
                "OMGAUDIO Tables",
                "Player tables updated",
                "Table Update",
                Math.Max(1, updatedGroupDtos.Count),
                Math.Max(1, updatedGroupDtos.Count),
                100,
                0,
                true);
        }
        else
        {
            _pendingGroupRenamesByDevice.Remove(destId);
            _pendingTagPatchesByDevice.Remove(destId);
            HasPendingDeviceTableChanges = false;
            RebuildFileManagerDestEntries();
        }

        ShowToast("Player Tables Updated", $"Committed folder & track order to {destDev.Model}.", ToastSeverity.Success);
    }

    private void QueuePendingDeviceTagPatch(string destId, Walkman.Contracts.Engines.TrackMetadataPatch patch)
    {
        if (!_pendingTagPatchesByDevice.TryGetValue(destId, out var list))
        {
            list = new List<Walkman.Contracts.Engines.TrackMetadataPatch>();
            _pendingTagPatchesByDevice[destId] = list;
        }

        list.RemoveAll(p => p.TrackId == patch.TrackId);
        list.Add(patch);
        HasPendingDeviceTableChanges = true;
    }

    private IProgress<Walkman.Contracts.Messaging.TransferProgressEvent> CreateDeleteProgressReporter(string modelName, int totalCount)
    {
        return new Progress<Walkman.Contracts.Messaging.TransferProgressEvent>(ev =>
        {
            void Apply()
            {
                int pct = Math.Clamp((int)Math.Round(ev.PercentComplete), 0, 100);
                TransferJob = new ActiveTransferJob(
                    ev.JobId,
                    ev.CurrentTrackTitle,
                    $"Deleting from {modelName}",
                    "OMGAUDIO Delete",
                    ev.CompletedTracks,
                    Math.Max(1, ev.TotalTracks),
                    pct,
                    Math.Max(0, ev.TotalTracks - ev.CompletedTracks),
                    pct >= 100,
                    IsDeleteJob: true);
            }

            if (Avalonia.Application.Current is null)
            {
                Apply();
            }
            else
            {
                Avalonia.Threading.Dispatcher.UIThread.Post(Apply);
            }
        });
    }

    private void ClearStagedSelectionsAfterTransfer(
        string destId,
        IReadOnlyList<AudioTrackItem> transferredTracks,
        Walkman.Contracts.Engines.SyncBatchResult result)
    {
        var failedNames = new HashSet<string>(
            result.Errors
                .Select(e => e.Split(':', 2)[0].Trim())
                .Where(n => !string.IsNullOrEmpty(n)),
            StringComparer.OrdinalIgnoreCase);

        foreach (var trk in transferredTracks)
        {
            string? fileName = !string.IsNullOrWhiteSpace(trk.SourceFilePath) ? Path.GetFileName(trk.SourceFilePath) : null;
            if (result.FailedCount > 0 && fileName is not null && failedNames.Contains(fileName))
            {
                continue;
            }

            trk.IsSelected = false;
            _stagedTrackGroups.Remove(trk.Id);
            RemoveStagedIdFromAllOrders(destId, trk.Id);
        }
    }

    private void RemoveStagedIdFromAllOrders(string destId, string sourceTrackId)
    {
        string stagedId = $"staged:{sourceTrackId}";
        string prefix = $"{destId}::";
        foreach (var kv in _collectionTrackOrder)
        {
            if (kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                kv.Value.RemoveAll(id => string.Equals(id, stagedId, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
}
