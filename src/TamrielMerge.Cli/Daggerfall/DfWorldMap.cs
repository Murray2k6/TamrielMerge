namespace TamrielMerge.Daggerfall;

/// <summary>Daggerfall climate codes from CLIMATE.PAK (Daggerfall Unity MapsFile.Climates).</summary>
public enum DfClimate : byte
{
    Ocean = 223, Desert = 224, Desert2 = 225, Mountain = 226, Rainforest = 227,
    Swamp = 228, Subtropical = 229, MountainWoods = 230, Woodlands = 231, HauntedWoodlands = 232,
}

/// <summary>
/// The 1000x500 Daggerfall world map: coarse elevation and fine 3x3-per-pixel elevation from WOODS.WLD,
/// climate from CLIMATE.PAK and region index from POLITIC.PAK. Pixel (0,0) is the north-west corner.
/// Format reference: Daggerfall Unity WoodsFile.cs / PakFile.cs.
/// </summary>
public sealed class DfWorldMap
{
    public const int Width = 1000, Height = 500, FineScale = 3;

    /// <summary>Coarse elevation, one byte per map pixel.</summary>
    public required byte[] Elevation { get; init; }
    /// <summary>Fine elevation, 3000x1500: the inner 3x3 of each pixel's 5x5 WOODS.WLD grid.</summary>
    public required byte[] FineElevation { get; init; }
    public required byte[] Climate { get; init; }
    public required byte[] Politic { get; init; }

    public byte ElevationAt(int x, int y) => Elevation[Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)];
    public byte FineAt(int x, int y) => FineElevation[Math.Clamp(y, 0, Height * FineScale - 1) * Width * FineScale + Math.Clamp(x, 0, Width * FineScale - 1)];
    public DfClimate ClimateAt(int x, int y) => (DfClimate)Climate[Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)];

    public static DfWorldMap Load(string arena2)
    {
        var woods = File.ReadAllBytes(Path.Combine(arena2, "WOODS.WLD"));
        using var r = new BinaryReader(new MemoryStream(woods));
        r.ReadUInt32();
        uint width = r.ReadUInt32(), height = r.ReadUInt32();
        if (width != Width || height != Height) throw new InvalidDataException($"Unexpected WOODS.WLD size {width}x{height}");
        r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();
        uint heightMapOffset = r.ReadUInt32();
        r.BaseStream.Position += 28 * 4;
        var pixelOffsets = new uint[Width * Height];
        for (int i = 0; i < pixelOffsets.Length; i++) pixelOffsets[i] = r.ReadUInt32();

        var elevation = woods.AsSpan((int)heightMapOffset, Width * Height).ToArray();

        // Each pixel record holds a 5x5 elevation grid 22 bytes in, rows north to south; the outer ring
        // duplicates neighbours, so keep the inner 3x3 (as Daggerfall Unity does).
        int fineW = Width * FineScale;
        var fine = new byte[fineW * Height * FineScale];
        for (int py = 0; py < Height; py++)
            for (int px = 0; px < Width; px++)
            {
                int src = (int)pixelOffsets[py * Width + px] + 22;
                for (int j = 0; j < FineScale; j++)
                    for (int i = 0; i < FineScale; i++)
                        fine[(py * FineScale + j) * fineW + px * FineScale + i] = woods[src + (j + 1) * 5 + (i + 1)];
            }

        return new DfWorldMap
        {
            Elevation = elevation,
            FineElevation = fine,
            Climate = ReadPak(Path.Combine(arena2, "CLIMATE.PAK")),
            Politic = ReadPak(Path.Combine(arena2, "POLITIC.PAK")),
        };
    }

    /// <summary>PAK files: 500 row offsets, each row run-length encoded as (UInt16 count, byte value) to 1001 values.</summary>
    static byte[] ReadPak(string path)
    {
        const int pakWidth = 1001;
        var data = File.ReadAllBytes(path);
        var result = new byte[Width * Height];
        for (int row = 0; row < Height; row++)
        {
            int pos = BitConverter.ToInt32(data, row * 4);
            int x = 0;
            while (x < pakWidth)
            {
                int count = BitConverter.ToUInt16(data, pos);
                byte value = data[pos + 2];
                pos += 3;
                for (int c = 0; c < count && x < pakWidth; c++, x++)
                    if (x < Width) result[row * Width + x] = value;
            }
        }
        return result;
    }
}
