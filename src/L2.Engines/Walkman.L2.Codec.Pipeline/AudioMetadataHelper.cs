using System.Buffers.Binary;
using System.Text;
using ATL;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;

namespace Walkman.L2.Codec.Pipeline;

/// <summary>
/// Permissive MIT metadata extraction and writing backed by ATL.NET (z440.atl.core),
/// with direct Sony ea3/EA3 fallback for .oma/.aa3/.omg files.
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

        string ext = Path.GetExtension(filePath).ToLowerInvariant();
        if (ext is ".oma" or ".aa3" or ".omg")
        {
            var omaMeta = TryReadOmaMetadata(filePath);
            if (omaMeta is not null)
            {
                return omaMeta;
            }
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

    private static Ea3TagMetadata? TryReadOmaMetadata(string filePath)
    {
        try
        {
            using var fs = File.OpenRead(filePath);
            long totalLen = fs.Length;
            if (totalLen < 96)
            {
                return null;
            }

            byte[] prefix = new byte[Math.Min(totalLen, 16384)];
            int read = fs.Read(prefix, 0, prefix.Length);
            if (read < 96)
            {
                return null;
            }

            int ea3Offset = 0;
            string title = string.Empty;
            string artist = string.Empty;
            string album = string.Empty;
            string genre = string.Empty;
            int? year = null;
            int? trackNo = null;
            uint tagDurMs = 0;

            if (prefix[0] == (byte)'e' && prefix[1] == (byte)'a' && prefix[2] == (byte)'3')
            {
                int bodySize = ((prefix[6] & 0x7F) << 21) | ((prefix[7] & 0x7F) << 14) | ((prefix[8] & 0x7F) << 7) | (prefix[9] & 0x7F);
                ea3Offset = 10 + bodySize;
                int pos = 10;
                int limit = Math.Min(ea3Offset, read);
                while (pos + 10 <= limit && prefix[pos] != 0)
                {
                    string fid = Encoding.ASCII.GetString(prefix, pos, 4);
                    int fsz = BinaryPrimitives.ReadInt32BigEndian(prefix.AsSpan(pos + 4, 4));
                    if (fsz <= 0 || pos + 10 + fsz > limit) break;
                    if (fsz > 1)
                    {
                        byte enc = prefix[pos + 10];
                        string val = DecodeId3Text(enc, prefix.AsSpan(pos + 11, fsz - 1));
                        switch (fid)
                        {
                            case "TIT2": title = val; break;
                            case "TPE1": artist = val; break;
                            case "TALB": album = val; break;
                            case "TCON": genre = val; break;
                            case "TYER": if (int.TryParse(val, out int y) && y > 0) year = y; break;
                            case "TLEN": if (uint.TryParse(val, out uint d) && d > 0) tagDurMs = d; break;
                            case "TXXX":
                                int nul = val.IndexOf('\0');
                                if (nul > 0 && val[..nul].Equals("OMG_TRACK", StringComparison.OrdinalIgnoreCase) &&
                                    int.TryParse(val[(nul + 1)..], out int tn) && tn > 0)
                                {
                                    trackNo = tn;
                                }
                                break;
                        }
                    }
                    pos += 10 + fsz;
                }
            }
            else if (prefix[0] != (byte)'E' || prefix[1] != (byte)'A' || prefix[2] != (byte)'3')
            {
                return null;
            }

            if (ea3Offset + 96 > read)
            {
                byte[] ea3Buf = new byte[96];
                fs.Position = ea3Offset;
                if (fs.Read(ea3Buf, 0, 96) < 96) return null;
                prefix = ea3Buf;
                ea3Offset = 0;
            }

            ReadOnlySpan<byte> ea3 = prefix.AsSpan(ea3Offset, 96);
            if (ea3[0] != (byte)'E' || ea3[1] != (byte)'A' || ea3[2] != (byte)'3')
            {
                return null;
            }

            var codecId = (AudioCodecId)ea3[32];
            uint p24 = (uint)((ea3[33] << 16) | (ea3[34] << 8) | ea3[35]);
            uint durMs = BinaryPrimitives.ReadUInt32BigEndian(ea3.Slice(36, 4));
            uint frames = BinaryPrimitives.ReadUInt32BigEndian(ea3.Slice(40, 4));
            if (durMs == 0) durMs = tagDurMs;
            if (durMs == 0)
            {
                long payloadBytes = Math.Max(0, totalLen - (ea3Offset + 96));
                if (codecId == AudioCodecId.Atrac3Plus)
                {
                    int frameBytes = ((int)(p24 & 0x3FF) + 1) * 8;
                    uint f = frames > 0 ? frames : (frameBytes > 0 ? (uint)(payloadBytes / frameBytes) : 0u);
                    durMs = (uint)Math.Round(f * 2048.0 * 1000.0 / 44100.0, MidpointRounding.AwayFromZero);
                }
                else if (codecId == AudioCodecId.Atrac3)
                {
                    int frameBytes = (int)(p24 & 0x3FF) * 8;
                    uint f = frames > 0 ? frames : (frameBytes > 0 ? (uint)(payloadBytes / frameBytes) : 0u);
                    durMs = (uint)Math.Round(f * 1024.0 * 1000.0 / 44100.0, MidpointRounding.AwayFromZero);
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = Path.GetFileNameWithoutExtension(filePath);
            }

            return new Ea3TagMetadata(title.Trim(), artist.Trim(), album.Trim(), genre.Trim(), year, trackNo, durMs);
        }
        catch
        {
            return null;
        }
    }

    private static string DecodeId3Text(byte enc, ReadOnlySpan<byte> body)
    {
        return enc switch
        {
            0x00 => Encoding.Latin1.GetString(body).TrimEnd('\0').Trim(),
            0x01 => Encoding.Unicode.GetString(body.Length >= 2 && body[0] == 0xFF && body[1] == 0xFE ? body[2..] : body).TrimEnd('\0').Trim(),
            0x02 => Encoding.BigEndianUnicode.GetString(body).TrimEnd('\0').Trim(),
            0x03 => Encoding.UTF8.GetString(body).TrimEnd('\0').Trim(),
            _ => Encoding.BigEndianUnicode.GetString(body).TrimEnd('\0').Trim()
        };
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
