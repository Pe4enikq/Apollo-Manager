using System.Text.Json;
using Walkman.L3.AppShell.Controls;

namespace Walkman.L3.AppShell.Core;

/// <summary>
/// Data-driven flash card item for the decoupled Starting Guide overlay.
/// </summary>
public sealed record OnboardingCard(
    int StepNumber,
    string StepBadge,
    string Title,
    string Subtitle,
    string Body,
    string TipLabel,
    string IconGeometryData,
    string? ActionModuleId = null,
    string? ActionLabel = null);

/// <summary>
/// Provides the built-in Starting Guide flash-card deck and JSON serialization helpers.
/// </summary>
public static class OnboardingCardDeck
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static IReadOnlyList<OnboardingCard> DefaultCards { get; } =
    [
        new(
            StepNumber: 1,
            StepBadge: "STEP 1 · HARDWARE LINK",
            Title: "USB Connect & Support Tiers",
            Subtitle: "Automatic Sony Walkman detection with Verified vs. Experimental safeguards.",
            Body: "Plug your Sony Network Walkman in over USB (or preview safely via Demo Mode in Settings). Verified players use hardware-proven layouts, while Experimental profiles show a single-session advisory and verify filesystem markers before writing.",
            TipLabel: "Click the Center Medallion or open Device & System → Pair & Sync to inspect your connected unit.",
            IconGeometryData: AeroIcons.Usb,
            ActionModuleId: ModuleIds.PairSync,
            ActionLabel: "Open Pair & Sync"),
        new(
            StepNumber: 2,
            StepBadge: "STEP 2 · AUDIO CODECS & 0x0001 SLOTS",
            Title: "ATRAC3+ Slots vs. Keyless MP3 Mode",
            Subtitle: "Pure-MSC slot recycling alongside zero-DRM MP3 transfers.",
            Body: "Gen 3 flash Walkmans (such as NW-E400/E500) store 0x0001 ATRAC3plus tracks inside signed MACLIST0 slots paired with an internal hardware ICV register. Apollo automatically reuses freed 0x0001 slots, or lets you switch instantly to keyless 0xFFFE MP3 mode with zero Sony runtime required.",
            TipLabel: "Tip: Use File Manager → Auto or Custom mode to pick ATRAC3plus or MP3 for each Walkman.",
            IconGeometryData: AeroIcons.AudioLines,
            ActionModuleId: ModuleIds.Converter,
            ActionLabel: "Open Codec Converter"),
        new(
            StepNumber: 3,
            StepBadge: "STEP 3 · TRANSFER WORKFLOW",
            Title: "File Manager Staging & \"Update\"",
            Subtitle: "Two independent actions: upload new tracks or commit table changes.",
            Body: "Select tracks in Export Source (or drag them directly into Import Destination collections) and click Transfer to encode and upload audio. When reordering tracks, dragging collections, renaming groups, or editing tags, click Update to flush only your OMGAUDIO database changes.",
            TipLabel: "Tip: Look for the glowing blue Update button whenever pending collection edits are queued.",
            IconGeometryData: AeroIcons.Swap,
            ActionModuleId: ModuleIds.FileManager,
            ActionLabel: "Open File Manager"),
        new(
            StepNumber: 4,
            StepBadge: "STEP 4 · STORAGE SAFETY",
            Title: "FAT16 Safety, Backups & Safe Eject",
            Subtitle: "Protect your Walkman database from mid-write corruption.",
            Body: "Classic Sony Walkmans require strict factory FAT16 geometry and synchronized OMGAUDIO index tables. Always use Apollo's Safe Eject button before unplugging your USB cable, and leave automatic OMGAUDIO backups enabled in Settings before major reorganizations.",
            TipLabel: "Safety: Apollo automatically checks FAT16 BPB geometry and skips unsafe writes if errors are detected.",
            IconGeometryData: AeroIcons.Shield,
            ActionModuleId: ModuleIds.Settings,
            ActionLabel: "Open Settings"),
        new(
            StepNumber: 5,
            StepBadge: "STEP 5 · COMMUNITY TELEMETRY",
            Title: "Help Expand Model Support via Dump Wizard",
            Subtitle: "Sanitized ~50 KB diagnostic archives with zero personal audio files.",
            Body: "If your Sony player appears unrecognized or uses an unfamiliar firmware revision, open Device & System → Dump Wizard and export a diagnostic .ZIP. It collects only hardware USB IDs, crypto keys, and track table headers—zero audio files leave your PC.",
            TipLabel: "Once exported, use the Dump Wizard Tally form link to share the archive with developers.",
            IconGeometryData: AeroIcons.DumpArchive,
            ActionModuleId: ModuleIds.DeviceDumpWizard,
            ActionLabel: "Open Dump Wizard")
    ];

    public static string ToJson(IReadOnlyList<OnboardingCard> cards)
    {
        ArgumentNullException.ThrowIfNull(cards);
        return JsonSerializer.Serialize(cards, JsonOptions);
    }

    public static IReadOnlyList<OnboardingCard> LoadFromJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return DefaultCards;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<List<OnboardingCard>>(json, JsonOptions);
            return parsed is { Count: > 0 } ? parsed : DefaultCards;
        }
        catch
        {
            return DefaultCards;
        }
    }
}
