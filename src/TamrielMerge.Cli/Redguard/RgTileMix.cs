using System.Numerics;

namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's ground squares as mixes of plain ground. Its land is one 64 x 64 picture a square, and where grass meets
/// sand the square's picture is the meeting itself, drawn across it and turned by the square's two top bits. Laid as a
/// Skyrim land texture such a picture repeats over the square and shows as stripes (user, 2026-11-12, on Stros M'Kai:
/// "why does the terrain look weird and not blended"). Here each picture is read at its four corners: a picture whose
/// corners are alike is plain ground, and every corner of every picture is the plain ground nearest it in colour. A
/// point of the land is then the plain grounds of its square's corners, weighed by how near each corner is.
/// </summary>
public sealed class RgTileMix
{
    /// <summary>How a square's turn (0-3) and the picture's rows map to the world: found by <see cref="Fit"/>.</summary>
    public readonly record struct Laying(int Mode, bool RowsNorth)
    {
        /// <summary>0 turned anticlockwise, 1 clockwise, 2 bit 0 mirrors east-west and bit 1 north-south, 3 the bits the
        /// other way round, 4 and 5 as 0 and 1 on a mirrored picture.</summary>
        public string Name => Mode switch { 0 => "turned anticlockwise", 1 => "turned clockwise", 2 => "mirrored (bit 0 across, bit 1 down)", 3 => "mirrored (bit 0 down, bit 1 across)", 4 => "mirrored, turned anticlockwise", _ => "mirrored, turned clockwise" };
        public const int Modes = 6;
    }

    readonly RgWorld world;
    readonly Dictionary<int, int[]> corners = [];   // tile -> the plain tile at each picture corner (TL, TR, BL, BR)
    readonly Dictionary<int, Vector3> mean = [];    // tile -> its picture's colour
    public Laying Way { get; private set; }
    public int Plain { get; }
    public int Mixed { get; }
    /// <summary>Grid points whose four squares agree on the ground there, and those where they don't (the fit's score).</summary>
    public (int Agree, int Differ) Seams { get; private set; }

    public RgTileMix(RgWorld world, RgTextureBank tiles, RgPalette palette, Func<int, bool> usable)
    {
        this.world = world;
        var used = new HashSet<int>();
        for (int z = 0; z < RgWorld.Size; z++)
            for (int x = 0; x < RgWorld.Size; x++) used.Add(world.Texture[x, z] & 0x3F);
        // Each picture's corner colours: the mean of a quarter-wide patch in each corner.
        var colour = new Dictionary<int, Vector3[]>();
        foreach (int t in used)
        {
            if (!tiles.Images.TryGetValue(t, out var img) || img.Width < 4 || img.Height < 4) continue;
            int pw = Math.Max(1, img.Width / 4), ph = Math.Max(1, img.Height / 4);
            Vector3 Patch(int x0, int y0)
            {
                var sum = Vector3.Zero;
                for (int y = y0; y < y0 + ph; y++)
                    for (int x = x0; x < x0 + pw; x++)
                    {
                        uint c = palette.Colors[img.Pixels[y * img.Width + x]];
                        sum += new Vector3((c >> 16) & 0xFF, (c >> 8) & 0xFF, c & 0xFF);
                    }
                return sum / (pw * ph);
            }
            colour[t] = [Patch(0, 0), Patch(img.Width - pw, 0), Patch(0, img.Height - ph), Patch(img.Width - pw, img.Height - ph)];
        }
        // Plain ground: corners within 30 of each other (of 255, as a distance in colour), and a texture to lay.
        float Spread(Vector3[] c) => c.SelectMany(a => c.Select(b => Vector3.Distance(a, b))).Max();
        var plain = colour.Where(kv => Spread(kv.Value) < 30 && usable(kv.Key)).ToDictionary(kv => kv.Key, kv => (kv.Value[0] + kv.Value[1] + kv.Value[2] + kv.Value[3]) / 4);
        Plain = plain.Count;
        foreach (var (t, c) in colour) mean[t] = (c[0] + c[1] + c[2] + c[3]) / 4;
        Mixed = colour.Count - plain.Count;
        foreach (var (t, c) in colour)
        {
            if (plain.ContainsKey(t) || plain.Count == 0) { corners[t] = [t, t, t, t]; continue; }
            corners[t] = c.Select(cc => plain.MinBy(p => Vector3.Distance(p.Value, cc)).Key).ToArray();
        }
        Fit();
    }

    /// <summary>The picture corner (0 TL, 1 TR, 2 BL, 3 BR) at a square's world corner (east 0/1, south 0/1) under a laying.</summary>
    static int CornerAt(int turn, int east, int south, Laying way)
    {
        // The square's corner as picture coordinates before the turn: u east, v down the picture.
        int u = east, v = way.RowsNorth ? 1 - south : south;
        switch (way.Mode)
        {
            case 2: if ((turn & 1) != 0) u = 1 - u; if ((turn & 2) != 0) v = 1 - v; break;
            case 3: if ((turn & 1) != 0) v = 1 - v; if ((turn & 2) != 0) u = 1 - u; break;
            default:
                if (way.Mode >= 4) u = 1 - u;
                for (int k = 0; k < turn; k++) (u, v) = way.Mode % 2 == 1 ? (v, 1 - u) : (1 - v, u);
                break;
        }
        return v * 2 + u;
    }

    int GroundAt(int gx, int gz, int east, int south, Laying way)
    {
        int raw = world.Texture[Math.Clamp(gx, 0, RgWorld.Size - 1), Math.Clamp(gz, 0, RgWorld.Size - 1)];
        int t = raw & 0x3F;
        return corners.TryGetValue(t, out var c) ? c[CornerAt(raw >> 6, east, south, way)] : t;
    }

    /// <summary>The laying under which neighbouring squares agree most often on the ground at the grid point they share.</summary>
    void Fit()
    {
        var best = (Way: new Laying(0, false), Agree: -1, Differ: 0);
        for (int mode = 0; mode < Laying.Modes; mode++)
            foreach (bool north in new[] { false, true })
            {
                var way = new Laying(mode, north);
                int agree = 0, differ = 0;
                for (int z = 1; z < RgWorld.Size; z++)
                    for (int x = 1; x < RgWorld.Size; x++)
                    {
                        // The point shared by squares (x-1, z-1), (x, z-1), (x-1, z), (x, z).
                        int a = GroundAt(x - 1, z - 1, 1, 1, way), b = GroundAt(x, z - 1, 0, 1, way), c = GroundAt(x - 1, z, 1, 0, way), d = GroundAt(x, z, 0, 0, way);
                        if (a == b && b == c && c == d) agree++; else differ++;
                    }
                if (agree > best.Agree) best = (way, agree, differ);
            }
        Way = best.Way;
        Seams = (best.Agree, best.Differ);
    }

    /// <summary>Every laying's score, for the dev report.</summary>
    public IEnumerable<(Laying Way, int Agree, int Differ)> Scores()
    {
        var keep = (Way, Seams);
        for (int mode = 0; mode < Laying.Modes; mode++)
            foreach (bool north in new[] { false, true })
            {
                var way = new Laying(mode, north);
                int agree = 0, differ = 0;
                for (int z = 1; z < RgWorld.Size; z++)
                    for (int x = 1; x < RgWorld.Size; x++)
                    {
                        int a = GroundAt(x - 1, z - 1, 1, 1, way), b = GroundAt(x, z - 1, 0, 1, way), c = GroundAt(x - 1, z, 1, 0, way), d = GroundAt(x, z, 0, 0, way);
                        if (a == b && b == c && c == d) agree++; else differ++;
                    }
                yield return (way, agree, differ);
            }
        (Way, Seams) = keep;
    }

    /// <summary>The ground among these nearest a ground in colour (the first, of a ground with no picture).</summary>
    public int Nearest(int tile, IReadOnlyCollection<int> among)
    {
        if (!mean.TryGetValue(tile, out var c)) return among.First();
        return among.MinBy(t => mean.TryGetValue(t, out var o) ? Vector3.Distance(o, c) : float.MaxValue);
    }

    /// <summary>The plain grounds at a point of the grid (gx east, gz south, in squares) and how much of each.</summary>
    public Dictionary<int, float> At(float gx, float gz)
    {
        // The grid points round it, each the ground its four squares give it (most say), weighed by nearness.
        int x0 = (int)MathF.Floor(gx), z0 = (int)MathF.Floor(gz);
        float fx = gx - x0, fz = gz - z0;
        var mix = new Dictionary<int, float>();
        foreach (var (px, pz, f) in new[] { (x0, z0, (1 - fx) * (1 - fz)), (x0 + 1, z0, fx * (1 - fz)), (x0, z0 + 1, (1 - fx) * fz), (x0 + 1, z0 + 1, fx * fz) })
        {
            if (f <= 0) continue;
            foreach (int g in new[] { GroundAt(px - 1, pz - 1, 1, 1, Way), GroundAt(px, pz - 1, 0, 1, Way), GroundAt(px - 1, pz, 1, 0, Way), GroundAt(px, pz, 0, 0, Way) })
                mix[g] = mix.GetValueOrDefault(g) + f / 4;
        }
        return mix;
    }
}
