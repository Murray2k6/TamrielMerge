namespace TamrielMerge.Util;

/// <summary>
/// A small DDS reader: BC1 (DXT1), BC2 (DXT3) and BC3 (DXT5), plain or behind a DX10 header, and uncompressed
/// 24/32-bit RGB(A) by channel masks. Decodes one mip level to 0xAARRGGBB pixels. Other formats (BC4-7, float)
/// give null.
/// </summary>
public static class DdsImage
{
    /// <summary>Decodes the given mip level (clamped to the last one), or null for an unsupported file.</summary>
    public static (uint[] Px, int W, int H)? Decode(byte[] b, int mip = 0)
    {
        if (b.Length < 128 || BitConverter.ToUInt32(b, 0) != 0x20534444) return null;
        int h = (int)BitConverter.ToUInt32(b, 12), w = (int)BitConverter.ToUInt32(b, 16);
        int mips = Math.Max(1, (int)BitConverter.ToUInt32(b, 28));
        uint pfFlags = BitConverter.ToUInt32(b, 80), fourCC = BitConverter.ToUInt32(b, 84), bits = BitConverter.ToUInt32(b, 88);
        uint rMask = BitConverter.ToUInt32(b, 92), gMask = BitConverter.ToUInt32(b, 96), bMask = BitConverter.ToUInt32(b, 100), aMask = BitConverter.ToUInt32(b, 104);
        int offset = 128;
        int bc = 0;   // 1, 2 or 3; 0 = uncompressed
        if ((pfFlags & 0x4) != 0)
        {
            switch (fourCC)
            {
                case 0x31545844: bc = 1; break;   // DXT1
                case 0x33545844: case 0x32545844: bc = 2; break;   // DXT3 / DXT2
                case 0x35545844: case 0x34545844: bc = 3; break;   // DXT5 / DXT4
                case 0x30315844:   // DX10
                    uint dxgi = BitConverter.ToUInt32(b, 128);
                    offset = 148;
                    bc = dxgi switch { 70 or 71 or 72 => 1, 73 or 74 or 75 => 2, 76 or 77 or 78 => 3, _ => -1 };
                    if (bc < 0)
                    {
                        if (dxgi is 28 or 29) { bc = 0; bits = 32; rMask = 0xFF; gMask = 0xFF00; bMask = 0xFF0000; aMask = 0xFF000000; }
                        else if (dxgi is 87 or 91) { bc = 0; bits = 32; rMask = 0xFF0000; gMask = 0xFF00; bMask = 0xFF; aMask = 0xFF000000; }
                        else return null;
                    }
                    break;
                default: return null;
            }
        }
        else if (bits is not (24 or 32)) return null;

        int level = Math.Clamp(mip, 0, mips - 1);
        for (int l = 0; l < level; l++) { offset += LevelSize(Math.Max(1, w >> l), Math.Max(1, h >> l), bc, (int)bits); }
        int lw = Math.Max(1, w >> level), lh = Math.Max(1, h >> level);
        if (offset + LevelSize(lw, lh, bc, (int)bits) > b.Length) return null;
        var px = new uint[lw * lh];
        if (bc == 0)
        {
            int bpp = (int)bits / 8;
            for (int i = 0; i < lw * lh; i++)
            {
                uint v = bpp == 4 ? BitConverter.ToUInt32(b, offset + i * 4) : (uint)(b[offset + i * 3] | b[offset + i * 3 + 1] << 8 | b[offset + i * 3 + 2] << 16);
                px[i] = (aMask == 0 ? 0xFF000000u : Channel(v, aMask) << 24) | Channel(v, rMask) << 16 | Channel(v, gMask) << 8 | Channel(v, bMask);
            }
            return (px, lw, lh);
        }
        int blockBytes = bc == 1 ? 8 : 16;
        var block = new uint[16];
        int bw = (lw + 3) / 4, bh = (lh + 3) / 4;
        for (int by = 0; by < bh; by++)
            for (int bx = 0; bx < bw; bx++)
            {
                int at = offset + (by * bw + bx) * blockBytes;
                DecodeColor(b, bc == 1 ? at : at + 8, block, bc == 1);
                if (bc == 2)
                    for (int i = 0; i < 16; i++) block[i] = block[i] & 0xFFFFFF | (uint)((b[at + i / 2] >> (i % 2 * 4) & 15) * 17) << 24;
                else if (bc == 3)
                {
                    int a0 = b[at], a1 = b[at + 1];
                    ulong idx = 0;
                    for (int k = 0; k < 6; k++) idx |= (ulong)b[at + 2 + k] << (8 * k);
                    for (int i = 0; i < 16; i++)
                    {
                        int code = (int)(idx >> (3 * i) & 7);
                        int a = code switch
                        {
                            0 => a0, 1 => a1,
                            _ when a0 > a1 => ((8 - code) * a0 + (code - 1) * a1) / 7,
                            6 => 0, 7 => 255,
                            _ => ((6 - code) * a0 + (code - 1) * a1) / 5,
                        };
                        block[i] = block[i] & 0xFFFFFF | (uint)a << 24;
                    }
                }
                for (int i = 0; i < 16; i++)
                {
                    int x = bx * 4 + i % 4, y = by * 4 + i / 4;
                    if (x < lw && y < lh) px[y * lw + x] = block[i];
                }
            }
        return (px, lw, lh);
    }

    /// <summary>The average colour (R, G, B) of a DDS, from a small mip level; null if it can't be read.</summary>
    public static (byte R, byte G, byte B)? Average(byte[] b)
    {
        if (b.Length < 128) return null;
        int h = (int)BitConverter.ToUInt32(b, 12), w = (int)BitConverter.ToUInt32(b, 16);
        int mips = Math.Max(1, (int)BitConverter.ToUInt32(b, 28));
        // The level near 16 pixels across: small to decode, still a fair average.
        int level = 0;
        while (level + 1 < mips && Math.Max(w >> (level + 1), h >> (level + 1)) >= 16) level++;
        if (Decode(b, level) is not { } img) return null;
        long r = 0, g = 0, bl = 0;
        foreach (var p in img.Px) { r += p >> 16 & 255; g += p >> 8 & 255; bl += p & 255; }
        int n = img.Px.Length;
        return ((byte)(r / n), (byte)(g / n), (byte)(bl / n));
    }

    static int LevelSize(int w, int h, int bc, int bits) =>
        bc == 0 ? w * h * bits / 8 : Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * (bc == 1 ? 8 : 16);

    static uint Channel(uint v, uint mask)
    {
        if (mask == 0) return 0;
        int shift = System.Numerics.BitOperations.TrailingZeroCount(mask);
        uint max = mask >> shift;
        return ((v & mask) >> shift) * 255 / max;
    }

    static void DecodeColor(byte[] b, int at, uint[] block, bool bc1)
    {
        ushort c0 = BitConverter.ToUInt16(b, at), c1 = BitConverter.ToUInt16(b, at + 2);
        uint idx = BitConverter.ToUInt32(b, at + 4);
        Span<uint> pal = stackalloc uint[4];
        static (int R, int G, int B) Rgb(ushort c) => ((c >> 11 & 31) * 255 / 31, (c >> 5 & 63) * 255 / 63, (c & 31) * 255 / 31);
        var (r0, g0, b0) = Rgb(c0); var (r1, g1, b1) = Rgb(c1);
        static uint Pack(int r, int g, int b, int a = 255) => (uint)(a << 24 | r << 16 | g << 8 | b);
        pal[0] = Pack(r0, g0, b0); pal[1] = Pack(r1, g1, b1);
        if (c0 > c1 || !bc1)
        {
            pal[2] = Pack((2 * r0 + r1) / 3, (2 * g0 + g1) / 3, (2 * b0 + b1) / 3);
            pal[3] = Pack((r0 + 2 * r1) / 3, (g0 + 2 * g1) / 3, (b0 + 2 * b1) / 3);
        }
        else
        {
            pal[2] = Pack((r0 + r1) / 2, (g0 + g1) / 2, (b0 + b1) / 2);
            pal[3] = Pack(0, 0, 0, 0);
        }
        for (int i = 0; i < 16; i++) block[i] = pal[(int)(idx >> (2 * i) & 3)];
    }
}
