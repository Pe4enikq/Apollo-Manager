using ATL;
using Walkman.Contracts.Engines;

namespace Walkman.L2.Codec.Pipeline;

/// <summary>
/// Permissive MIT metadata extraction and writing backed by ATL.NET (z440.atl.core).
/// </summary>
internal static class AudioMetadataHelper
{
    public static Ea3TagMetadata ReadMetadata(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException($"Audio source file not found: '{filePath}'.", filePath);
        }

        var track = new Track(filePath);
        string title = !string.IsNullOrWhiteSpace(track.Title)
            ? track.Title.Trim()
            : Path.GetFileNameWithoutExtension(filePath);

        string artist = track.Artist?.Trim() ?? string.Empty;
        string album = track.Album?.Trim() ?? string.Empty;
        string genre = track.Genre?.Trim() ?? string.Empty;
        int? year = track.Year is > 0 ? track.Year : null;
        int? trackNo = track.TrackNumber is > 0 ? track.TrackNumber : null;
        uint durationMs = track.DurationMs > 0 ? (uint)Math.Round(track.DurationMs) : 0u;

        return new Ea3TagMetadata(title, artist, album, genre, year, trackNo, durationMs);
    }

    public static void WriteMetadata(string outputFilePath, Ea3TagMetadata tags)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputFilePath);
        ArgumentNullException.ThrowIfNull(tags);

        var track = new Track(outputFilePath)
        {
            Title = tags.Title,
            Artist = tags.Artist,
            Album = tags.Album,
            Genre = tags.Genre
        };

        if (tags.Year is > 0)
        {
            track.Year = tags.Year.Value;
        }

        if (tags.TrackNumber is > 0)
        {
            track.TrackNumber = tags.TrackNumber.Value;
        }

        track.Save();
    }
}
