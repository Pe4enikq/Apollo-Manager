using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;

namespace Walkman.L3.AppShell.Core;

public sealed class AudioTrackItem : INotifyPropertyChanged
{
    private string _title = string.Empty;
    private string _artist = string.Empty;
    private string _album = string.Empty;
    private int _year;
    private string _bitrate = string.Empty;
    private int _durationSec;
    private double _sizeMb;
    private string? _groupName;
    private IReadOnlyList<string>? _subtitleLines;
    private Bitmap? _coverArtwork;
    private bool _isSelected;
    private bool _isLibrarySelected;
    private bool _isDestSelected;
    private bool _isTagSelected;
    private bool _protectedLock;
    private bool _isNowPlaying;
    private bool _isActivelyPlaying;
    private int _rating;
    private string _genre = string.Empty;
    private int _trackNumber;
    private string _albumArtist = string.Empty;
    private string _composer = string.Empty;
    private string _sortTitle = string.Empty;
    private string _artistLinks = string.Empty;
    private string _comment = string.Empty;
    private bool _isStagedPreview;

    public required string Id { get; init; }

    public required string Title
    {
        get => _title;
        set
        {
            if (!string.Equals(_title, value, StringComparison.Ordinal))
            {
                _title = value;
                OnPropertyChanged();
            }
        }
    }

    public required string Artist
    {
        get => _artist;
        set
        {
            if (!string.Equals(_artist, value, StringComparison.Ordinal))
            {
                _artist = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ArtistAndDuration));
            }
        }
    }

    public required string Album
    {
        get => _album;
        set
        {
            if (!string.Equals(_album, value, StringComparison.Ordinal))
            {
                _album = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AlbumAndYear));
            }
        }
    }

    public int Year
    {
        get => _year;
        set
        {
            if (_year != value)
            {
                _year = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AlbumAndYear));
            }
        }
    }

    public required string Format { get; init; }
    public required string Bitrate
    {
        get => _bitrate;
        set
        {
            if (!string.Equals(_bitrate, value, StringComparison.Ordinal))
            {
                _bitrate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CodecAndBitrate));
            }
        }
    }

    public int DurationSec
    {
        get => _durationSec;
        set
        {
            if (_durationSec != value)
            {
                _durationSec = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DurationFormatted));
                OnPropertyChanged(nameof(ArtistAndDuration));
            }
        }
    }

    public double SizeMb
    {
        get => _sizeMb;
        set
        {
            if (Math.Abs(_sizeMb - value) > 0.0001)
            {
                _sizeMb = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SizeFormatted));
            }
        }
    }
    public List<string> Locations { get; init; } = new();
    public string Folder { get; init; } = string.Empty;
    public (string TopHex, string BottomHex) CoverGradient { get; init; } = ("#6CC8FF", "#8EE352");
    public string? SourceFilePath { get; init; }
    public int? DeviceTrackId { get; init; }
    public string? DeviceInstanceId { get; init; }
    public bool IsDemoItem { get; init; }

    public string? GroupName
    {
        get => _groupName;
        set
        {
            if (!string.Equals(_groupName, value, StringComparison.Ordinal))
            {
                _groupName = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(GroupDisplay));
            }
        }
    }

    public IReadOnlyList<string>? SubtitleLines
    {
        get => _subtitleLines;
        set
        {
            _subtitleLines = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSubtitles));
        }
    }

    public Bitmap? CoverArtwork
    {
        get => _coverArtwork;
        set
        {
            if (!ReferenceEquals(_coverArtwork, value))
            {
                _coverArtwork = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasCoverArtwork));
            }
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsLibrarySelected
    {
        get => _isLibrarySelected;
        set
        {
            if (_isLibrarySelected != value)
            {
                _isLibrarySelected = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsDestSelected
    {
        get => _isDestSelected;
        set
        {
            if (_isDestSelected != value)
            {
                _isDestSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsTagSelected
    {
        get => _isTagSelected;
        set
        {
            if (_isTagSelected != value)
            {
                _isTagSelected = value;
                OnPropertyChanged();
            }
        }
    }

    public bool ProtectedLock
    {
        get => _protectedLock;
        set
        {
            if (_protectedLock != value)
            {
                _protectedLock = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsNowPlaying
    {
        get => _isNowPlaying;
        set
        {
            if (_isNowPlaying != value)
            {
                _isNowPlaying = value;
                OnPropertyChanged();
            }
        }
    }

    public bool IsActivelyPlaying
    {
        get => _isActivelyPlaying;
        set
        {
            if (_isActivelyPlaying != value)
            {
                _isActivelyPlaying = value;
                OnPropertyChanged();
            }
        }
    }

    public int Rating
    {
        get => _rating;
        set
        {
            int clamped = Math.Clamp(value, 0, 5);
            if (_rating != clamped)
            {
                _rating = clamped;
                OnPropertyChanged();
                OnPropertyChanged(nameof(RatingStars));
                OnPropertyChanged(nameof(HasRating));
                OnPropertyChanged(nameof(RatingFilledStars));
                OnPropertyChanged(nameof(RatingAtLeast1));
                OnPropertyChanged(nameof(RatingAtLeast2));
                OnPropertyChanged(nameof(RatingAtLeast3));
                OnPropertyChanged(nameof(RatingAtLeast4));
                OnPropertyChanged(nameof(RatingAtLeast5));
            }
        }
    }

    public string RatingStars => _rating <= 0 ? "☆☆☆☆☆" : new string('★', _rating) + new string('☆', 5 - _rating);
    public bool HasRating => _rating > 0;
    public string RatingFilledStars => _rating <= 0 ? string.Empty : new string('★', _rating);
    public bool RatingAtLeast1 => _rating >= 1;
    public bool RatingAtLeast2 => _rating >= 2;
    public bool RatingAtLeast3 => _rating >= 3;
    public bool RatingAtLeast4 => _rating >= 4;
    public bool RatingAtLeast5 => _rating >= 5;

    public string Genre
    {
        get => _genre;
        set { if (!string.Equals(_genre, value, StringComparison.Ordinal)) { _genre = value; OnPropertyChanged(); } }
    }

    public int TrackNumber
    {
        get => _trackNumber;
        set { if (_trackNumber != value) { _trackNumber = value; OnPropertyChanged(); } }
    }

    public string AlbumArtist
    {
        get => _albumArtist;
        set { if (!string.Equals(_albumArtist, value, StringComparison.Ordinal)) { _albumArtist = value; OnPropertyChanged(); } }
    }

    public string Composer
    {
        get => _composer;
        set { if (!string.Equals(_composer, value, StringComparison.Ordinal)) { _composer = value; OnPropertyChanged(); } }
    }

    public string SortTitle
    {
        get => _sortTitle;
        set { if (!string.Equals(_sortTitle, value, StringComparison.Ordinal)) { _sortTitle = value; OnPropertyChanged(); } }
    }

    public string ArtistLinks
    {
        get => _artistLinks;
        set { if (!string.Equals(_artistLinks, value, StringComparison.Ordinal)) { _artistLinks = value; OnPropertyChanged(); } }
    }

    public string Comment
    {
        get => _comment;
        set { if (!string.Equals(_comment, value, StringComparison.Ordinal)) { _comment = value; OnPropertyChanged(); } }
    }

    public bool IsStagedPreview
    {
        get => _isStagedPreview;
        set { if (_isStagedPreview != value) { _isStagedPreview = value; OnPropertyChanged(); } }
    }

    public string DurationFormatted => FormatDuration(DurationSec);
    public string SizeFormatted => string.Create(CultureInfo.InvariantCulture, $"{SizeMb:F1} MB");
    public string ArtistAndDuration => $"{Artist} · {DurationFormatted}";
    public string AlbumAndYear => Year > 0 ? $"{Album} · {Year}" : Album;
    public string CodecAndBitrate => string.IsNullOrWhiteSpace(Bitrate) ? Format : $"{Format} · {Bitrate}";
    public string GroupDisplay => string.IsNullOrWhiteSpace(GroupName) ? "Unassigned" : GroupName;
    public bool HasSubtitles => SubtitleLines is { Count: > 0 };
    public bool HasCoverArtwork => CoverArtwork is not null;

    public static string FormatDuration(int seconds)
    {
        int clamped = Math.Max(0, seconds);
        int m = clamped / 60;
        int s = clamped % 60;
        return $"{m}:{s:D2}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public enum ScreenTintMode
{
    Cyan,
    Lime,
    Amber
}

public enum RepeatMode
{
    Off,
    All,
    One
}

public enum TrackSortField
{
    Title,
    Artist,
    Album,
    AlbumTrackNumber,
    Format,
    Duration,
    Size,
    Rating,
    Folder,
    Order,
    Subtitles
}

public sealed class TrackListEntry : INotifyPropertyChanged
{
    public bool IsHeader { get; init; }
    public string HeaderKey { get; init; } = string.Empty;
    public string HeaderTitle { get; init; } = string.Empty;
    public string ScopePrefix { get; init; } = string.Empty;
    public int ItemCount { get; init; }
    public int ExistingCount { get; init; }
    public int StagedCount { get; init; }
    private bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded != value)
            {
                _isExpanded = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            }
        }
    }

    public bool IsStagedHeader { get; init; }
    public bool IsCollectionHeader { get; init; }

    private bool _hasSelectedTracks;
    public bool HasSelectedTracks
    {
        get => _hasSelectedTracks;
        set
        {
            if (_hasSelectedTracks != value)
            {
                _hasSelectedTracks = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedTracks)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedAndPlaying)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPlayingAndSelected)));
            }
        }
    }

    private bool _containsPlayingTrack;
    public bool ContainsPlayingTrack
    {
        get => _containsPlayingTrack;
        set
        {
            if (_containsPlayingTrack != value)
            {
                _containsPlayingTrack = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContainsPlayingTrack)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasSelectedAndPlaying)));
            }
        }
    }

    public bool HasSelectedAndPlaying => HasSelectedTracks && ContainsPlayingTrack;

    private bool _hasPlayingTrack;
    public bool HasPlayingTrack
    {
        get => _hasPlayingTrack;
        set
        {
            if (_hasPlayingTrack != value)
            {
                _hasPlayingTrack = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPlayingTrack)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasPlayingAndSelected)));
            }
        }
    }

    public bool HasPlayingAndSelected => HasPlayingTrack && HasSelectedTracks;
    public AudioTrackItem? Track { get; init; }
    public int CollectionPosition { get; init; }
    public string PositionLabel => CollectionPosition > 0 ? $"#{CollectionPosition}" : string.Empty;
    public bool IsTrack => !IsHeader && Track is not null;
    public string HeaderCountLabel => StagedCount > 0
        ? $"{ExistingCount} +{StagedCount}"
        : (ItemCount == 1 ? "1 track" : $"{ItemCount} tracks");

    private bool _isDropTargetBefore;
    public bool IsDropTargetBefore
    {
        get => _isDropTargetBefore;
        set
        {
            if (_isDropTargetBefore != value)
            {
                _isDropTargetBefore = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropTargetBefore)));
            }
        }
    }

    private bool _isDropTargetOver;
    public bool IsDropTargetOver
    {
        get => _isDropTargetOver;
        set
        {
            if (_isDropTargetOver != value)
            {
                _isDropTargetOver = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDropTargetOver)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record InputDialogState(
    string Title,
    string Prompt,
    string InitialValue,
    string ConfirmLabel,
    Action<string> OnConfirm);

public sealed record WalkmanDeviceSummary(
    string Id,
    string Model,
    int Year,
    string Series,
    double StorageTotalGb,
    double StorageUsedGb,
    int BatteryPct,
    IReadOnlyList<string> SupportedCodecs,
    string ScreenResolution,
    ScreenTintMode ScreenTint,
    string Firmware,
    bool IsLiveHardware = false,
    ConnectedDeviceIdentity? LiveIdentity = null,
    MagicGateSlotPoolStatus? SlotPoolStatus = null,
    bool IsDemoDevice = false,
    int TrackCount = 0,
    IReadOnlyList<string>? GroupNames = null,
    DeviceDetectionProbe? DetectionProbe = null,
    SupportStatus SupportStatus = SupportStatus.Verified)
{
    public bool IsExperimentalProfile => SupportStatus == SupportStatus.Experimental;
    public string SupportStatusBadge => SupportStatus switch
    {
        SupportStatus.Verified => "VERIFIED",
        SupportStatus.Experimental => "EXPERIMENTAL",
        _ => "UNSUPPORTED"
    };
    public double StorageFreeGb => Math.Max(0, StorageTotalGb - StorageUsedGb);
    public int StorageUsedPercent => StorageTotalGb > 0
        ? Math.Clamp((int)Math.Round((StorageUsedGb / StorageTotalGb) * 100.0), 0, 100)
        : 0;
    public string CodecsFormatted => string.Join(" · ", SupportedCodecs);
    public string SeriesAndYear => Year > 0 ? $"{Series} · {Year}" : Series;
    public string StorageFormatted => string.Create(CultureInfo.InvariantCulture, $"{StorageUsedGb:F1} / {StorageTotalGb:F1} GB");
    public string ModeBadge => IsDemoDevice ? "DEMO" : (IsLiveHardware ? "LIVE USB" : "OFFLINE");
}

public sealed record TransferLocationItem(
    string Id,
    string Label,
    string Kind,
    string Detail,
    string? DirectoryPath = null,
    string? LiveDeviceId = null,
    bool IsDemoLocation = false)
{
    public override string ToString() => IsDemoLocation ? $"{Label} [DEMO]" : Label;
}

public sealed record ActiveTransferJob(
    string Id,
    string CurrentFileName,
    string StageLabel,
    string CodecLabel,
    int CompletedCount,
    int TotalCount,
    int ProgressPct,
    int EtaSeconds,
    bool IsCompleted,
    bool IsError = false,
    string? ErrorText = null,
    bool IsDeleteJob = false);

public enum ToastSeverity
{
    Info,
    Success,
    Warning,
    Error
}

public sealed record ToastItem(
    string Id,
    string Title,
    string Message,
    ToastSeverity Severity)
{
    public bool IsWarning => Severity == ToastSeverity.Warning;
    public bool IsError => Severity == ToastSeverity.Error;
    public bool IsSuccess => Severity == ToastSeverity.Success;
}

public sealed record ConfirmDialogState(
    string Title,
    string Message,
    string ConfirmLabel,
    bool IsDestructive,
    Action OnConfirm);

public static class PcLocationCatalog
{
    public static IReadOnlyList<TransferLocationItem> BuildDefaultPcLocations(string musicFolderPath, string convertedFolderPath) =>
    [
        new("pc-library", "My Music", "library", musicFolderPath, DirectoryPath: musicFolderPath),
        new("pc-converted", "Converted Output", "folder", convertedFolderPath, DirectoryPath: convertedFolderPath)
    ];
}
