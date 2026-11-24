namespace Walkman.L3.AppShell.Core;

/// <summary>
/// Isolated synthetic preview catalog and mock Walkman hardware overlay.
/// Only active when the user enables Demo Mode in Settings (enabled by default for first-run preview).
/// </summary>
public static class DemoModeOverlay
{
    public const string DemoSourceLocationId = "demo-catalog";

    public static IReadOnlyList<WalkmanDeviceSummary> CreateDemoDevices() =>
    [
        new(
            Id: "nw-hd5",
            Model: "NW-HD5",
            Year: 2005,
            Series: "Network Walkman HDD",
            StorageTotalGb: 20.0,
            StorageUsedGb: 11.4,
            BatteryPct: 86,
            SupportedCodecs: ["ATRAC3plus", "ATRAC3", "MP3"],
            ScreenResolution: "128 × 64 Monochrome LCD",
            ScreenTint: ScreenTintMode.Cyan,
            Firmware: "2.01",
            IsLiveHardware: false,
            IsDemoDevice: true,
            TrackCount: 4,
            GroupNames: ["All Tracks", "Electronic", "Soundtracks", "Instrumental"]),
        new(
            Id: "nw-a3000",
            Model: "NW-A3000",
            Year: 2005,
            Series: "Walkman A-Series",
            StorageTotalGb: 20.0,
            StorageUsedGb: 14.8,
            BatteryPct: 72,
            SupportedCodecs: ["ATRAC3plus", "ATRAC3", "MP3", "WMA"],
            ScreenResolution: "128 × 96 Organic EL",
            ScreenTint: ScreenTintMode.Amber,
            Firmware: "3.00",
            IsLiveHardware: false,
            IsDemoDevice: true,
            TrackCount: 2,
            GroupNames: ["All Tracks", "Electronic", "Soundtracks"]),
        new(
            Id: "nw-s706f",
            Model: "NW-S706F",
            Year: 2006,
            Series: "Noise Canceling Stick",
            StorageTotalGb: 4.0,
            StorageUsedGb: 2.7,
            BatteryPct: 94,
            SupportedCodecs: ["ATRAC3plus", "MP3", "AAC", "Linear PCM"],
            ScreenResolution: "128 × 36 Color OLED",
            ScreenTint: ScreenTintMode.Lime,
            Firmware: "1.04",
            IsLiveHardware: false,
            IsDemoDevice: true,
            TrackCount: 1,
            GroupNames: ["All Tracks", "Instrumental"]),
        new(
            Id: "nw-e405",
            Model: "NW-E405",
            Year: 2005,
            Series: "Organic EL Capsule",
            StorageTotalGb: 0.5,
            StorageUsedGb: 0.28,
            BatteryPct: 91,
            SupportedCodecs: ["ATRAC3plus", "ATRAC3", "MP3"],
            ScreenResolution: "128 × 36 Organic EL",
            ScreenTint: ScreenTintMode.Cyan,
            Firmware: "2.00",
            IsLiveHardware: false,
            IsDemoDevice: true,
            TrackCount: 1,
            GroupNames: ["All Tracks", "Electronic"])
    ];

    public static TransferLocationItem CreateDemoSourceLocation() =>
        new(
            Id: DemoSourceLocationId,
            Label: "Demo Sample Catalog",
            Kind: "demo",
            Detail: "Bundled synthetic preview tracks",
            IsDemoLocation: true);

    public static List<AudioTrackItem> CreateDemoSampleTracks() =>
    [
        new()
        {
            Id = "trk-01",
            Title = "Midnight Horizon (2004 Remaster)",
            Artist = "Mondo Grosso",
            Album = "Next Wave",
            Year = 2003,
            Format = "FLAC",
            Bitrate = "942 kbps",
            DurationSec = 274,
            SizeMb = 31.4,
            Locations = [DemoSourceLocationId, "nw-hd5"],
            Folder = "Electronic / Shibuya",
            GroupName = "Electronic",
            CoverGradient = ("#6CC8FF", "#8EE352"),
            IsDemoItem = true,
            SubtitleLines =
            [
                "[00:12.40] Neon reflections on cool glass",
                "[00:24.10] Midnight horizon over Tokyo Bay",
                "[00:38.80] Silver cassette turning in the dark"
            ]
        },
        new()
        {
            Id = "trk-02",
            Title = "Clear Sky Over Odaiba",
            Artist = "Kenji Kawai",
            Album = "Glass Reflections",
            Year = 2005,
            Format = "ATRAC3+",
            Bitrate = "256 kbps",
            DurationSec = 238,
            SizeMb = 7.4,
            Locations = [DemoSourceLocationId, "nw-hd5", "nw-a3000"],
            Folder = "Soundtracks",
            GroupName = "Soundtracks",
            CoverGradient = ("#7ED4FF", "#D8F3FF"),
            IsDemoItem = true,
            SubtitleLines =
            [
                "[00:09.00] Morning light across the bay bridge",
                "[00:21.50] Quiet waves under crystal glass"
            ]
        },
        new()
        {
            Id = "trk-03",
            Title = "Aqua Drift",
            Artist = "Shinichi Osawa",
            Album = "The One",
            Year = 2007,
            Format = "FLAC",
            Bitrate = "1018 kbps",
            DurationSec = 312,
            SizeMb = 38.6,
            Locations = [DemoSourceLocationId],
            Folder = "Electronic / Shibuya",
            GroupName = "Electronic",
            CoverGradient = ("#4EB6F7", "#73E2A7"),
            IsSelected = true,
            IsDemoItem = true,
            SubtitleLines =
            [
                "[00:15.20] Drifting through liquid sky",
                "[00:31.00] Pulse of aluminum and air"
            ]
        },
        new()
        {
            Id = "trk-04",
            Title = "Starlight Express",
            Artist = "Capsule",
            Album = "FRUITS CLiPPER",
            Year = 2006,
            Format = "MP3",
            Bitrate = "320 kbps",
            DurationSec = 259,
            SizeMb = 9.9,
            Locations = [DemoSourceLocationId, "nw-hd5"],
            Folder = "J-Pop / 2000s",
            GroupName = "Electronic",
            CoverGradient = ("#95E858", "#7ACBFF"),
            ProtectedLock = true,
            IsDemoItem = true,
            SubtitleLines =
            [
                "[00:05.00] Boarding the starlight line at 00:05",
                "[00:19.40] Windows glowing baby-blue"
            ]
        },
        new()
        {
            Id = "trk-05",
            Title = "Velvet Morning",
            Artist = "Nujabes",
            Album = "Modal Soul",
            Year = 2005,
            Format = "FLAC",
            Bitrate = "876 kbps",
            DurationSec = 245,
            SizeMb = 26.2,
            Locations = [DemoSourceLocationId],
            Folder = "Instrumental",
            GroupName = "Instrumental",
            CoverGradient = ("#8BD6FF", "#B2EF7E"),
            IsSelected = true,
            IsDemoItem = true
        },
        new()
        {
            Id = "trk-06",
            Title = "Feather",
            Artist = "Nujabes feat. Cise Starr",
            Album = "Modal Soul",
            Year = 2005,
            Format = "ATRAC3+",
            Bitrate = "256 kbps",
            DurationSec = 175,
            SizeMb = 5.5,
            Locations = [DemoSourceLocationId, "nw-hd5", "nw-s706f"],
            Folder = "Instrumental",
            GroupName = "Instrumental",
            CoverGradient = ("#68C1F8", "#9BE858"),
            IsDemoItem = true,
            SubtitleLines =
            [
                "[00:08.15] Light as a feather when I am floating through",
                "[00:22.40] Reading through the daily news"
            ]
        },
        new()
        {
            Id = "trk-07",
            Title = "Silver Rain",
            Artist = "Cornelius",
            Album = "Sensuous",
            Year = 2006,
            Format = "WAV",
            Bitrate = "1411 kbps",
            DurationSec = 290,
            SizeMb = 49.8,
            Locations = [DemoSourceLocationId, "nw-e405"],
            Folder = "Electronic / Shibuya",
            GroupName = "Electronic",
            CoverGradient = ("#B8D6EA", "#7CCBFF"),
            IsDemoItem = true
        },
        new()
        {
            Id = "trk-08",
            Title = "Digital Breeze",
            Artist = "Royksopp",
            Album = "The Understanding",
            Year = 2005,
            Format = "MP3",
            Bitrate = "320 kbps",
            DurationSec = 226,
            SizeMb = 8.6,
            Locations = [DemoSourceLocationId, "nw-a3000"],
            Folder = "Electronic / Europe",
            GroupName = "Electronic",
            CoverGradient = ("#7CCBFF", "#94E552"),
            IsDemoItem = true
        }
    ];
}
