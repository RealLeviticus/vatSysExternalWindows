using System;
using System.Collections.Generic;
using System.Text;

namespace VatSysExternalWindows;

/// <summary>
/// Makes the installed Terminus TTF loadable as a web font. Its glyph 0
/// (.notdef) declares far more hinting bytecode than the glyph holds; Windows
/// tolerates that, but the font sanitiser in Chrome and Firefox rejects the
/// whole file, and a device without Terminus installed then draws every label
/// in a fallback font. The repair empties that one glyph, which no text uses.
/// </summary>
internal static class FontRepair
{
    public static byte[] Fix(byte[] font)
    {
        try
        {
            var b = (byte[])font.Clone();
            var tables = new Dictionary<string, (int Record, int Offset, int Length)>();
            int count = U16(b, 4);
            for (var i = 0; i < count; i++)
            {
                var record = 12 + i * 16;
                tables[Encoding.ASCII.GetString(b, record, 4)] = (record, (int)U32(b, record + 8), (int)U32(b, record + 12));
            }

            if (!tables.TryGetValue("loca", out var loca) || !tables.TryGetValue("head", out var head) ||
                !tables.TryGetValue("glyf", out var glyf))
                return font;

            var longOffsets = (short)U16(b, head.Offset + 50) == 1;
            long Loca(int index) => longOffsets ? U32(b, loca.Offset + index * 4) : U16(b, loca.Offset + index * 2) * 2L;

            var start = Loca(0);
            var end = Loca(1);
            if (end - start < 12) return font;

            var glyph = glyf.Offset + (int)start;
            var contours = (short)U16(b, glyph);
            var lengthAt = glyph + 10 + contours * 2;
            if (contours < 0 || lengthAt + 2 > glyf.Offset + end) return font;
            if (U16(b, lengthAt) <= end - start) return font; // the glyph is sound

            // loca[0] = loca[1]: glyph 0 becomes an empty glyph
            if (longOffsets) Put32(b, loca.Offset, (uint)end);
            else Put16(b, loca.Offset, (ushort)(end / 2));

            Put32(b, loca.Record + 4, Checksum(b, loca.Offset, loca.Length));
            Put32(b, head.Offset + 8, 0);
            Put32(b, head.Record + 4, Checksum(b, head.Offset, head.Length));
            Put32(b, head.Offset + 8, unchecked(0xB1B0AFBAu - Checksum(b, 0, b.Length)));
            return b;
        }
        catch
        {
            return font;
        }
    }

    private static int U16(byte[] b, int offset) => (b[offset] << 8) | b[offset + 1];

    private static uint U32(byte[] b, int offset) =>
        ((uint)b[offset] << 24) | ((uint)b[offset + 1] << 16) | ((uint)b[offset + 2] << 8) | b[offset + 3];

    private static void Put16(byte[] b, int offset, ushort value)
    {
        b[offset] = (byte)(value >> 8);
        b[offset + 1] = (byte)value;
    }

    private static void Put32(byte[] b, int offset, uint value)
    {
        b[offset] = (byte)(value >> 24);
        b[offset + 1] = (byte)(value >> 16);
        b[offset + 2] = (byte)(value >> 8);
        b[offset + 3] = (byte)value;
    }

    /// <summary>The sfnt checksum: the sum of the big-endian 32-bit words, zero padded.</summary>
    private static uint Checksum(byte[] b, int offset, int length)
    {
        uint sum = 0;
        for (var i = 0; i < length; i += 4)
        {
            uint word = 0;
            for (var k = 0; k < 4; k++) word = (word << 8) | (i + k < length ? b[offset + i + k] : (byte)0);
            unchecked { sum += word; }
        }
        return sum;
    }
}
