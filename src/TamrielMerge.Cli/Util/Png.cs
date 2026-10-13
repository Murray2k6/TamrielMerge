using System.IO.Compression;
using System.Text;

namespace TamrielMerge.Util;

/// <summary>Minimal RGB PNG writer for preview images.</summary>
public static class Png
{
    public static void WriteRgb(string path, int width, int height, byte[] rgb)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width); WriteBE(ihdr, 4, (uint)height);
        ihdr[8] = 8; ihdr[9] = 2; // 8-bit RGB
        Chunk(fs, "IHDR", ihdr);

        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(rgb, y * width * 3, width * 3);
            }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", []);
    }

    /// <summary>Reads an 8-bit, non-interlaced RGB or RGBA PNG (as Pillow writes) into ARGB pixels.</summary>
    /// <summary>Any non-interlaced PNG (grey, grey+alpha, RGB, RGBA, palette; 8 or 16 bits a sample) as ARGB.</summary>
    public static (uint[] Argb, int Width, int Height) Read(string path)
    {
        var data = File.ReadAllBytes(path);
        int p = 8, width = 0, height = 0, channels = 0, depth = 8, colorType = 2;
        byte[]? palette = null, paletteAlpha = null;
        using var idat = new MemoryStream();
        while (p + 8 <= data.Length)
        {
            int len = (int)ReadBE(data, p);
            string type = Encoding.ASCII.GetString(data, p + 4, 4);
            if (type == "IHDR")
            {
                width = (int)ReadBE(data, p + 8); height = (int)ReadBE(data, p + 12);
                depth = data[p + 16]; colorType = data[p + 17];
                if (data[p + 20] != 0) throw new NotSupportedException($"{path}: interlaced PNG");
                if (depth != 8 && depth != 16) throw new NotSupportedException($"{path}: {depth}-bit PNG");
                channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => throw new NotSupportedException($"{path}: colour type {colorType}") };
            }
            else if (type == "PLTE") palette = data[(p + 8)..(p + 8 + len)];
            else if (type == "tRNS") paletteAlpha = data[(p + 8)..(p + 8 + len)];
            else if (type == "IDAT") idat.Write(data, p + 8, len);
            else if (type == "IEND") break;
            p += 12 + len;
        }
        idat.Position = 0;
        using var z = new ZLibStream(idat, CompressionMode.Decompress);
        int bytes = depth / 8, bpp = channels * bytes, stride = width * bpp;
        var raw = new byte[height * stride];
        var prev = new byte[stride];
        var line = new byte[stride];
        for (int y = 0; y < height; y++)
        {
            int filter = z.ReadByte();
            z.ReadExactly(line);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? line[i - bpp] : 0, b = prev[i], c = i >= bpp ? prev[i - bpp] : 0;
                int pr = a + b - c, pa = Math.Abs(pr - a), pb = Math.Abs(pr - b), pc = Math.Abs(pr - c);
                line[i] = (byte)(line[i] + filter switch
                {
                    1 => a, 2 => b, 3 => (a + b) / 2,
                    4 => pa <= pb && pa <= pc ? a : pb <= pc ? b : c,
                    _ => 0,
                });
            }
            Buffer.BlockCopy(line, 0, raw, y * stride, stride);
            (prev, line) = (line, prev);
        }
        var argb = new uint[width * height];
        for (int i = 0; i < argb.Length; i++)
        {
            int o = i * bpp;
            uint S(int ch) => raw[o + ch * bytes];   // the high byte of a 16-bit sample
            uint r, g, bl, al = 255;
            switch (colorType)
            {
                case 0: r = g = bl = S(0); break;
                case 4: r = g = bl = S(0); al = S(1); break;
                case 3:
                    int idx = raw[o];
                    r = palette is not null && idx * 3 + 2 < palette.Length ? palette[idx * 3] : 0u;
                    g = palette is not null && idx * 3 + 2 < palette.Length ? palette[idx * 3 + 1] : 0u;
                    bl = palette is not null && idx * 3 + 2 < palette.Length ? palette[idx * 3 + 2] : 0u;
                    al = paletteAlpha is not null && idx < paletteAlpha.Length ? paletteAlpha[idx] : 255u;
                    break;
                case 6: r = S(0); g = S(1); bl = S(2); al = S(3); break;
                default: r = S(0); g = S(1); bl = S(2); break;
            }
            argb[i] = al << 24 | r << 16 | g << 8 | bl;
        }
        return (argb, width, height);
    }

    static uint ReadBE(byte[] b, int o) => (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);

    /// <summary>8-bit RGBA (transparent sprites).</summary>
    public static void WriteRgba(string path, int width, int height, byte[] rgba)
    {
        using var fs = File.Create(path);
        fs.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteBE(ihdr, 0, (uint)width); WriteBE(ihdr, 4, (uint)height);
        ihdr[8] = 8; ihdr[9] = 6; // 8-bit RGBA
        Chunk(fs, "IHDR", ihdr);
        using var raw = new MemoryStream();
        using (var z = new ZLibStream(raw, CompressionLevel.Optimal, leaveOpen: true))
            for (int y = 0; y < height; y++)
            {
                z.WriteByte(0);
                z.Write(rgba, y * width * 4, width * 4);
            }
        Chunk(fs, "IDAT", raw.ToArray());
        Chunk(fs, "IEND", []);
    }

    static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4]; WriteBE(len, 0, (uint)data.Length);
        s.Write(len);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        s.Write(typeBytes); s.Write(data);
        var crc = new byte[4]; WriteBE(crc, 0, Crc(typeBytes, data));
        s.Write(crc);
    }

    static readonly uint[] CrcTable = Enumerable.Range(0, 256).Select(n =>
    {
        uint c = (uint)n;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    static uint Crc(byte[] a, byte[] b)
    {
        uint c = 0xFFFFFFFFu;
        foreach (var x in a) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        foreach (var x in b) c = CrcTable[(c ^ x) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFFu;
    }

    static void WriteBE(byte[] b, int o, uint v) { b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v; }
}
