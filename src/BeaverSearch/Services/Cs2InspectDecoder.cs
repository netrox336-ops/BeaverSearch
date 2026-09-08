using BeaverSearch.Models;

namespace BeaverSearch.Services;

/// <summary>
/// Offline decoder for CS2 masked/hybrid inspect links introduced in 2026.
/// Classic S...A...D(decimal) links intentionally return null because they require Steam GC.
/// </summary>
public static class Cs2InspectDecoder
{
    public static Cs2InspectData? TryDecode(string? inspectLink, string steamId64, string assetId)
    {
        if (string.IsNullOrWhiteSpace(inspectLink)) return null;
        try
        {
            var link = Uri.UnescapeDataString(inspectLink)
                .Replace("%owner_steamid%", steamId64, StringComparison.OrdinalIgnoreCase)
                .Replace("%assetid%", assetId, StringComparison.OrdinalIgnoreCase);
            var marker = "csgo_econ_action_preview ";
            var i = link.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return null;
            var payload = link[(i + marker.Length)..].Trim();

            if (payload.StartsWith('S'))
            {
                var d = payload.IndexOf('D');
                if (d < 0 || d == payload.Length - 1) return null;
                payload = payload[(d + 1)..];
            }

            if (payload.Length < 12 || payload.Length % 2 != 0 || !payload.All(Uri.IsHexDigit)) return null;
            var raw = Convert.FromHexString(payload);
            if (raw.Length < 6) return null;
            var key = raw[0];
            var proto = new byte[raw.Length - 5];
            for (var p = 0; p < proto.Length; p++) proto[p] = (byte)(raw[p + 1] ^ key);
            return ParseItem(proto);
        }
        catch { return null; }
    }

    private static Cs2InspectData ParseItem(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        uint def = 0, paint = 0, seed = 0;
        float wear = 0;
        var stickers = new List<uint>();

        while (pos < data.Length)
        {
            var tag = ReadVarint(data, ref pos);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (field == 0) break;

            if (wire == 0)
            {
                var value = ReadVarint(data, ref pos);
                switch (field)
                {
                    case 3: def = (uint)value; break;
                    case 4: paint = (uint)value; break;
                    case 7: wear = BitConverter.Int32BitsToSingle(unchecked((int)(uint)value)); break;
                    case 8: seed = (uint)value; break;
                }
            }
            else if (wire == 2)
            {
                var len = checked((int)ReadVarint(data, ref pos));
                if (len < 0 || pos + len > data.Length) throw new InvalidDataException("Invalid protobuf length.");
                if (field == 12)
                {
                    var sticker = ParseSticker(data.Slice(pos, len));
                    if (sticker != 0) stickers.Add(sticker);
                }
                pos += len;
            }
            else
            {
                SkipWire(data, ref pos, wire);
            }
        }

        return new Cs2InspectData(def, paint, seed, wear, stickers);
    }

    private static uint ParseSticker(ReadOnlySpan<byte> data)
    {
        var pos = 0;
        uint id = 0;
        while (pos < data.Length)
        {
            var tag = ReadVarint(data, ref pos);
            var field = (int)(tag >> 3);
            var wire = (int)(tag & 7);
            if (wire == 0)
            {
                var value = ReadVarint(data, ref pos);
                if (field == 2) id = (uint)value;
            }
            else SkipWire(data, ref pos, wire);
        }
        return id;
    }

    private static ulong ReadVarint(ReadOnlySpan<byte> data, ref int pos)
    {
        ulong value = 0;
        var shift = 0;
        while (pos < data.Length && shift < 64)
        {
            var b = data[pos++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return value;
            shift += 7;
        }
        throw new InvalidDataException("Invalid protobuf varint.");
    }

    private static void SkipWire(ReadOnlySpan<byte> data, ref int pos, int wire)
    {
        switch (wire)
        {
            case 0: _ = ReadVarint(data, ref pos); break;
            case 1: pos += 8; break;
            case 2:
                var len = checked((int)ReadVarint(data, ref pos));
                pos += len;
                break;
            case 5: pos += 4; break;
            default: throw new InvalidDataException("Unsupported protobuf wire type.");
        }
        if (pos > data.Length) throw new InvalidDataException("Protobuf field exceeds payload.");
    }
}
