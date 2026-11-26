using System.IO;
using System.Text.Json;

namespace Walkman.L3.AppShell.Core;

/// <summary>
/// Persisted user preferences for Apollo Manager shell appearance, codecs, and playback.
/// </summary>
public sealed record AppUserSettings(
    bool IsDarkTheme = false,
    bool IsDemoModeEnabled = false,
    bool EnableColdStartIntro = true,
    bool UseSidebarRail = false,
    bool RememberCodecChoice = false,
    string PreferredCodec = "ATRAC3+",
    string PreferredBitrate = "256k",
    string PreferredSampleRate = "44.1 kHz",
    bool NormalizeVolume = true,
    bool PreserveTagsAndCover = true,
    bool KeepSourceFiles = true,
    bool AddToLibraryAfterConvert = true,
    double PlayerVolume = 0.85,
    bool IsPlayerMuted = false,
    bool IsShuffleEnabled = false,
    string RepeatMode = "Off",
    string? CustomMusicFolder = null,
    string? CustomConvertedFolder = null,
    bool AutoBackupOmgaudioBeforeWrite = false,
    bool ShowLibColAlbum = true,
    bool ShowLibColRating = true,
    bool ShowLibColCodec = true,
    bool ShowLibColDuration = true,
    bool FmRememberChoice = false,
    string FmUploadMode = "Auto",
    string FmCustomCodecDisplayName = "ATRAC3plus 256k",
    bool FmNormalizeVolume = true,
    bool FmRewriteTables = false,
    bool FmRewriteLock = false,
    bool FmSafeEjectAfter = false,
    bool HasCompletedOnboarding = false);

/// <summary>
/// Reads and writes <see cref="AppUserSettings"/> to JSON in LocalApplicationData.
/// </summary>
public static class AppSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static string GetDefaultDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ApolloManager");

    public static string GetSettingsPath(string? overrideDirectory = null)
    {
        string dir = string.IsNullOrWhiteSpace(overrideDirectory) ? GetDefaultDirectory() : overrideDirectory;
        return Path.Combine(dir, "settings.json");
    }

    public static AppUserSettings Load(string? overrideDirectory = null)
    {
        string path = GetSettingsPath(overrideDirectory);
        if (!File.Exists(path))
        {
            return new AppUserSettings();
        }

        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<AppUserSettings>(json, SerializerOptions) ?? new AppUserSettings();
        }
        catch
        {
            // Fall back to clean defaults if settings JSON is corrupt or partially written.
            return new AppUserSettings();
        }
    }

    public static void Save(AppUserSettings settings, string? overrideDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string path = GetSettingsPath(overrideDirectory);
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(path, json);
        }
        catch
        {
            // Settings persistence failure must not crash audio or device workflows.
        }
    }
}
