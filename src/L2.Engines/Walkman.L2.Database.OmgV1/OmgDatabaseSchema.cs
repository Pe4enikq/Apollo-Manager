using System.Buffers.Binary;
using Walkman.Contracts.Devices;
using Walkman.Contracts.Engines;

namespace Walkman.L2.Database.OmgV1;

internal sealed record OmgTreeTableSpec(
    byte TableId,
    ushort TreeType,
    uint GplbOffset,
    uint GplbAllocatedLength,
    uint TplbOffset,
    ushort GinfElementSize,
    bool IsHierarchical2D);

internal sealed record OmgDatabaseSchema(
    bool IsAdoptMode,
    int GtrlstSlotCount,
    int TreinfElementCount,
    byte[]? Raw00GtrlstBytes,
    IReadOnlyList<OmgTreeTableSpec> Tables,
    bool Has2DTree,
    bool HasReservedDat);

internal static class OmgDatabaseSchemaIntrospector
{
    private const uint DefaultGplbOffset = 0x30;
    private const uint DefaultGplbAlloc = 0x4010;
    private const uint DefaultTplbOffset = 0x4040;

    public static async Task<OmgDatabaseSchema> IntrospectAsync(
        ConnectedDeviceIdentity device,
        ITransportDriver transport,
        CancellationToken ct)
    {
        bool hasGtrlst = await transport.ExistsAsync(device, "OMGAUDIO/00GTRLST.DAT", ct).ConfigureAwait(false);
        bool hasReserved = await transport.ExistsAsync(device, "OMGAUDIO/RESERVED.DAT", ct).ConfigureAwait(false);

        if (!hasGtrlst)
        {
            // Bootstrap Mode: fresh/empty device without OMGAUDIO/00GTRLST.DAT
            bool isE500 = string.Equals(device.Profile.BootstrapSchemaProfile, "OmgV1_E500_45Slot", StringComparison.OrdinalIgnoreCase);
            int slotCount = isE500 ? 0x2D : 0x22;
            var bootstrapTables = new List<OmgTreeTableSpec>
            {
                new(0x01, 0x0001, DefaultGplbOffset, DefaultGplbAlloc, DefaultTplbOffset, 0x0310, false),
                new(0x02, 0x0003, DefaultGplbOffset, DefaultGplbAlloc, DefaultTplbOffset, 0x0090, false),
                new(0x03, 0x0003, DefaultGplbOffset, DefaultGplbAlloc, DefaultTplbOffset, 0x0090, false),
                new(0x04, 0x0003, DefaultGplbOffset, DefaultGplbAlloc, DefaultTplbOffset, 0x0090, false)
            };
            if (isE500)
            {
                bootstrapTables.Add(new(0x2D, 0x0003, DefaultGplbOffset, DefaultGplbAlloc, DefaultTplbOffset, 0x0110, true));
            }

            return new OmgDatabaseSchema(
                IsAdoptMode: false,
                GtrlstSlotCount: slotCount,
                TreinfElementCount: slotCount,
                Raw00GtrlstBytes: null,
                Tables: bootstrapTables,
                Has2DTree: isE500,
                HasReservedDat: isE500 || hasReserved);
        }

        // Adopt Mode: introspect existing 00GTRLST.DAT and .DAT headers
        byte[] rawGtrlst = await transport.ReadFileAsync(device, "OMGAUDIO/00GTRLST.DAT", ct).ConfigureAwait(false);
        var activeIds = ParseActiveTreeIdsFromGtrlst(rawGtrlst, out int gtrlstSlotCount);

        int treinfCount = gtrlstSlotCount;
        if (await transport.ExistsAsync(device, "OMGAUDIO/02TREINF.DAT", ct).ConfigureAwait(false))
        {
            byte[] treinf = await transport.ReadFileAsync(device, "OMGAUDIO/02TREINF.DAT", ct).ConfigureAwait(false);
            if (treinf.Length >= 40)
            {
                treinfCount = Math.Max(treinfCount, BinaryPrimitives.ReadUInt16BigEndian(treinf.AsSpan(36, 2)));
            }
        }

        var tables = new List<OmgTreeTableSpec>();
        bool has2D = false;

        foreach (var (tableId, treeType) in activeIds)
        {
            if (tableId == 0x22)
            {
                continue; // 01TREE22 / 03GINF22 preserved untouched
            }

            if (tableId is not (0x01 or 0x02 or 0x03 or 0x04 or 0x2D))
            {
                continue; // Unknown future table IDs preserved untouched
            }

            bool is2D = tableId == 0x2D;
            if (is2D) has2D = true;

            uint gplbOff = DefaultGplbOffset;
            uint gplbAlloc = DefaultGplbAlloc;
            uint tplbOff = DefaultTplbOffset;
            ushort ginfElemSize = tableId switch
            {
                0x01 => 0x0310,
                0x2D => 0x0110,
                _ => 0x0090
            };

            string treePath = $"OMGAUDIO/01TREE{tableId:X2}.DAT";
            if (await transport.ExistsAsync(device, treePath, ct).ConfigureAwait(false))
            {
                byte[] treeHdr = await transport.ReadFilePrefixAsync(device, treePath, 64, ct).ConfigureAwait(false);
                if (treeHdr.Length >= 48 && treeHdr[0] == (byte)'T' && treeHdr[1] == (byte)'R')
                {
                    gplbOff = BinaryPrimitives.ReadUInt32BigEndian(treeHdr.AsSpan(20, 4));
                    gplbAlloc = BinaryPrimitives.ReadUInt32BigEndian(treeHdr.AsSpan(24, 4));
                    tplbOff = BinaryPrimitives.ReadUInt32BigEndian(treeHdr.AsSpan(36, 4));
                }
            }

            string ginfPath = $"OMGAUDIO/03GINF{tableId:X2}.DAT";
            if (await transport.ExistsAsync(device, ginfPath, ct).ConfigureAwait(false))
            {
                byte[] ginfHdr = await transport.ReadFilePrefixAsync(device, ginfPath, 64, ct).ConfigureAwait(false);
                if (ginfHdr.Length >= 40 && ginfHdr[0] == (byte)'G' && ginfHdr[1] == (byte)'P')
                {
                    ushort parsedElem = BinaryPrimitives.ReadUInt16BigEndian(ginfHdr.AsSpan(38, 2));
                    if (parsedElem >= 0x90)
                    {
                        ginfElemSize = parsedElem;
                    }
                }
            }

            tables.Add(new OmgTreeTableSpec(tableId, treeType, gplbOff, gplbAlloc, tplbOff, ginfElemSize, is2D));
        }

        return new OmgDatabaseSchema(
            IsAdoptMode: true,
            GtrlstSlotCount: gtrlstSlotCount,
            TreinfElementCount: treinfCount,
            Raw00GtrlstBytes: rawGtrlst,
            Tables: tables,
            Has2DTree: has2D,
            HasReservedDat: hasReserved);
    }

    private static List<(byte TableId, ushort TreeType)> ParseActiveTreeIdsFromGtrlst(byte[] rawGtrlst, out int slotCount)
    {
        var list = new List<(byte, ushort)>();
        slotCount = 0x22;
        if (rawGtrlst.Length < 48)
        {
            return list;
        }

        uint gtlbOff = BinaryPrimitives.ReadUInt32BigEndian(rawGtrlst.AsSpan(36, 4));
        if (gtlbOff + 16 > rawGtrlst.Length)
        {
            return list;
        }

        slotCount = BinaryPrimitives.ReadUInt16BigEndian(rawGtrlst.AsSpan((int)gtlbOff + 4, 2));
        ushort elemSize = BinaryPrimitives.ReadUInt16BigEndian(rawGtrlst.AsSpan((int)gtlbOff + 6, 2));
        if (elemSize < 20)
        {
            return list;
        }

        int dataStart = (int)gtlbOff + 16;
        for (int i = 0; i < slotCount; i++)
        {
            int off = dataStart + i * elemSize;
            if (off + 4 > rawGtrlst.Length) break;
            ushort id = BinaryPrimitives.ReadUInt16BigEndian(rawGtrlst.AsSpan(off, 2));
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(rawGtrlst.AsSpan(off + 2, 2));
            if (id is > 0 and <= 0xFF && type != 0)
            {
                list.Add(((byte)id, type));
            }
        }

        return list;
    }
}
