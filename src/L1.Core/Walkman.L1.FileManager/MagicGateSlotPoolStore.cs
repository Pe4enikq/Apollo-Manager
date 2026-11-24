using System.Security.Cryptography;
using System.Text.Json;
using Walkman.Contracts.Engines;

namespace Walkman.L1.FileManager;

/// <summary>
/// Cross-platform JSON store persisting verified pre-signed OpenMG 0x0001 slot templates
/// indexed by the SHA-256 fingerprint of a device's MACLIST0.DAT (Option 2 Slot-Recycling).
/// Stored strictly locally under <c>%LOCALAPPDATA%/ApolloManager/SlotPools/</c>, never transmitted
/// externally, and bound to the originating device's <c>MACLIST0.DAT</c> fingerprint.
/// </summary>
internal sealed class MagicGateSlotPoolStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _storeDirectory;
    private readonly object _syncLock = new();

    public MagicGateSlotPoolStore(string? storeDirectory = null)
    {
        if (!string.IsNullOrWhiteSpace(storeDirectory))
        {
            _storeDirectory = Path.GetFullPath(storeDirectory);
        }
        else
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string baseDir = string.IsNullOrWhiteSpace(localAppData) ? Path.GetTempPath() : localAppData;
            _storeDirectory = Path.Combine(baseDir, "ApolloManager", "SlotPools");
        }
    }

    public static string ComputeMaclistFingerprint(ReadOnlySpan<byte> maclist0Bytes) =>
        Convert.ToHexString(SHA256.HashData(maclist0Bytes))[..16];

    public Dictionary<int, SignedMagicGateSlotTemplate> LoadTemplates(ReadOnlySpan<byte> maclist0Bytes)
    {
        var result = new Dictionary<int, SignedMagicGateSlotTemplate>();
        if (maclist0Bytes.Length < 8)
        {
            return result;
        }

        string path = GetStoreFilePath(maclist0Bytes);
        lock (_syncLock)
        {
            if (!File.Exists(path))
            {
                return result;
            }

            List<SlotTemplateDto>? entries;
            try
            {
                string json = File.ReadAllText(path);
                entries = JsonSerializer.Deserialize<List<SlotTemplateDto>>(json, JsonOptions);
            }
            catch
            {
                return result;
            }

            if (entries is null)
            {
                return result;
            }

            foreach (var dto in entries)
            {
                if (TryDeserializeVerified(dto, maclist0Bytes, out var template))
                {
                    result[template!.TrackId] = template;
                }
            }
        }

        return result;
    }

    public void UpsertTemplates(ReadOnlySpan<byte> maclist0Bytes, IEnumerable<SignedMagicGateSlotTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        if (maclist0Bytes.Length < 8)
        {
            return;
        }

        var incoming = templates.ToArray();
        if (incoming.Length == 0)
        {
            return;
        }

        string path = GetStoreFilePath(maclist0Bytes);
        lock (_syncLock)
        {
            var merged = new Dictionary<int, SignedMagicGateSlotTemplate>();
            if (File.Exists(path))
            {
                try
                {
                    var existing = JsonSerializer.Deserialize<List<SlotTemplateDto>>(File.ReadAllText(path), JsonOptions);
                    if (existing is not null)
                    {
                        foreach (var dto in existing)
                        {
                            if (TryDeserializeVerified(dto, maclist0Bytes, out var parsed))
                            {
                                merged[parsed!.TrackId] = parsed;
                            }
                        }
                    }
                }
                catch
                {
                    // Rebuild clean cache file if existing JSON was corrupt.
                }
            }

            foreach (var tpl in incoming)
            {
                if (IsSlotConsistentWithMaclist(tpl, maclist0Bytes))
                {
                    merged[tpl.TrackId] = tpl;
                }
            }

            Directory.CreateDirectory(_storeDirectory);
            var serialized = merged.Values
                .OrderBy(t => t.TrackId)
                .Select(ToDto)
                .ToList();

            string tempPath = path + $".{Guid.NewGuid():N}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(serialized, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
    }

    private string GetStoreFilePath(ReadOnlySpan<byte> maclist0Bytes) =>
        Path.Combine(_storeDirectory, $"{ComputeMaclistFingerprint(maclist0Bytes)}.json");

    private static bool TryDeserializeVerified(
        SlotTemplateDto dto,
        ReadOnlySpan<byte> maclist0Bytes,
        out SignedMagicGateSlotTemplate? template)
    {
        template = null;
        try
        {
            byte[] ulinf = Convert.FromBase64String(dto.UlinfBase64);
            byte[]? bklsi = dto.BklsiBase64 is not null ? Convert.FromBase64String(dto.BklsiBase64) : null;
            byte[]? olinf = dto.OlinfBase64 is not null ? Convert.FromBase64String(dto.OlinfBase64) : null;
            byte[] cid = Convert.FromBase64String(dto.ContentIdBase64);
            byte[] iv = Convert.FromBase64String(dto.IvBase64);
            byte[] mac = Convert.FromBase64String(dto.MaclistEntryBase64);

            var candidate = new SignedMagicGateSlotTemplate(dto.TrackId, ulinf, bklsi, olinf, cid, iv, mac);
            if (!IsSlotConsistentWithMaclist(candidate, maclist0Bytes))
            {
                return false;
            }

            template = candidate;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSlotConsistentWithMaclist(SignedMagicGateSlotTemplate tpl, ReadOnlySpan<byte> maclist0Bytes)
    {
        if (tpl.TrackId is < 1 or > 4096 ||
            tpl.Ulinf240 is not { Length: 240 } ||
            tpl.ContentId20 is not { Length: 20 } ||
            tpl.Iv8 is not { Length: 8 } ||
            tpl.MaclistEntry8 is not { Length: 8 })
        {
            return false;
        }

        int offset = (tpl.TrackId - 1) * 8;
        if (offset + 8 > maclist0Bytes.Length)
        {
            return false;
        }

        ReadOnlySpan<byte> diskSlot = maclist0Bytes.Slice(offset, 8);
        return diskSlot.IndexOfAnyExcept((byte)0) >= 0 && diskSlot.SequenceEqual(tpl.MaclistEntry8);
    }

    private static SlotTemplateDto ToDto(SignedMagicGateSlotTemplate tpl) =>
        new()
        {
            TrackId = tpl.TrackId,
            UlinfBase64 = Convert.ToBase64String(tpl.Ulinf240),
            BklsiBase64 = tpl.Bklsi428 is not null ? Convert.ToBase64String(tpl.Bklsi428) : null,
            OlinfBase64 = tpl.Olinf64 is not null ? Convert.ToBase64String(tpl.Olinf64) : null,
            ContentIdBase64 = Convert.ToBase64String(tpl.ContentId20),
            IvBase64 = Convert.ToBase64String(tpl.Iv8),
            MaclistEntryBase64 = Convert.ToBase64String(tpl.MaclistEntry8)
        };

    private sealed class SlotTemplateDto
    {
        public int TrackId { get; set; }
        public string UlinfBase64 { get; set; } = string.Empty;
        public string? BklsiBase64 { get; set; }
        public string? OlinfBase64 { get; set; }
        public string ContentIdBase64 { get; set; } = string.Empty;
        public string IvBase64 { get; set; } = string.Empty;
        public string MaclistEntryBase64 { get; set; } = string.Empty;
    }
}
