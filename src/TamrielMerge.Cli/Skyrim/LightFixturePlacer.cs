using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim.Meshes;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Replaces Daggerfall's light sprites (texture archive 210 flats) with Skyrim light fixtures, which carry their own
/// flame and smoke effects. The fixture is chosen from where the sprite sits (on a wall, hanging under a ceiling,
/// standing on a floor, or on a raised surface) and its size from the archive header; no Daggerfall art is used.
/// The light itself comes from Daggerfall's separate light objects, placed elsewhere.
/// </summary>
public sealed class LightFixturePlacer
{
    public const int LightsArchive = 210;
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey WallTorch = Sk(0x90726);        // TorchPermanent01: burning wall torch, flame built in
    static readonly FormKey WallSconce = Sk(0x5D6EA);       // ImpWallSconce02CandleOn01
    static readonly FormKey Chandelier = Sk(0x5F21D);       // ImpChandellierCandle01 (hangs 280 above its origin)
    static readonly FormKey HangingCandles = Sk(0x5AD5B);   // CandleHornChandelier02 (compact, 49 above its origin)
    static readonly FormKey StandingBrazier = Sk(0x93A89);  // WHfirebrazier01 (fire brazier on a stand)
    static readonly FormKey Brazier = Sk(0x5F227);          // ImpBrazier01 (bowl)
    static readonly FormKey BrazierFire = Sk(0xCD824);      // FXfireWithEmbers01_Cheap
    static readonly FormKey TallCandelabra = Sk(0xCE026);   // ImpCandelabraCandle02 (167 tall)
    static readonly FormKey Candelabra = Sk(0x5F222);       // ImpCandelabraCandle01 (111 tall)
    static readonly FormKey TableCandle = Sk(0x1F24A);      // CandleHornTable01
    static readonly FormKey Campfire = Sk(0x35F4C);         // Campfire01Off: stone ring and logs (lit by the fire FX)
    static readonly FormKey CampfireSound = Sk(0x37E3E);    // FXFireCampfireLP
    const int CampfireRecord = 1;                          // 210.1: fire in a stone ring
    /// <summary>101, "temp E3 lights": braziers, chandeliers, lanterns, a campfire (101.5), a skull candle, a torch.</summary>
    public const int E3LightsArchive = 101;
    const int E3CampfireRecord = 5;

    /// <summary>Light sprites (archive 210, and the E3 lights 101 that a few blocks use).</summary>
    public static bool IsLight(DfRdbObject o) => o.Type == DfRdbObjectType.Flat && o.TextureArchive is LightsArchive or E3LightsArchive;

    static readonly Dictionary<string, Dictionary<int, (int Width, int Height)[]>> sizeCache = new();
    readonly Dictionary<int, (int Width, int Height)[]> spriteSizes;
    readonly List<DfPlacedPolygon> floors, ceilings, walls;

    public LightFixturePlacer(string arena2, IEnumerable<DfPlacedPolygon> dungeonPolygons)
    {
        lock (sizeCache)
            if (!sizeCache.TryGetValue(arena2, out spriteSizes!))
                sizeCache[arena2] = spriteSizes = new()
                {
                    [LightsArchive] = ReadSpriteSizes(Path.Combine(arena2, $"TEXTURE.{LightsArchive:000}")),
                    [E3LightsArchive] = ReadSpriteSizes(Path.Combine(arena2, $"TEXTURE.{E3LightsArchive:000}")),
                };
        var polys = dungeonPolygons.ToList();
        floors = polys.Where(p => p.Normal.Y > 0.7f).ToList();
        ceilings = polys.Where(p => p.Normal.Y < -0.7f).ToList();
        walls = polys.Where(p => MathF.Abs(p.Normal.Y) < 0.3f).ToList();
        anyUp = polys.Where(p => p.Normal.Y > 0.3f).ToList();
        anyDown = polys.Where(p => p.Normal.Y < -0.3f).ToList();
    }

    readonly List<DfPlacedPolygon> anyUp = [], anyDown = [];

    /// <summary>
    /// A point no room holds (Daggerfall units, Y up): nothing at all under it, or the nearest thing under it more
    /// than 512 below and nothing over it within 512. Daggerfall has lights and their sprites inside its edge blocks' solid
    /// stone (2026-10-16: 199 candles 1,900 units over any floor in W0000022, 116 candelabras under every floor),
    /// where its player never saw them: they are left out.
    /// </summary>
    public bool InVoid(Vector3 p)
    {
        if (anyUp.Count == 0 && anyDown.Count == 0) return false;   // no room given (the streets' fires)
        if (Surface(anyUp, p, below: true) is not { } under) return true;
        return p.Y - under > 512 && (Surface(anyDown, p, below: false) is not { } over || over - p.Y > 512);
    }

    public sealed record Placement(FormKey Base, Vector3 PositionDf, float HeadingRadians, string Kind);

    /// <summary>
    /// Fixtures for one light sprite at a dungeon-space position (Daggerfall units, Y up). Daggerfall stores a
    /// sprite's centre, so its base is half its height lower.
    /// </summary>
    public IEnumerable<Placement> Fixtures(DfRdbObject flat, Vector3 position)
    {
        if (InVoid(position)) yield break;
        var sizes = spriteSizes.GetValueOrDefault(flat.TextureArchive) ?? spriteSizes[LightsArchive];
        var (w, h) = flat.TextureRecord < sizes.Length ? sizes[flat.TextureRecord] : (16, 48);
        float bottom = position.Y - h / 2f, top = position.Y + h / 2f;
        float floor = HeightBelow(position) ?? bottom;
        float? ceiling = HeightAbove(position);
        float baseAboveFloor = bottom - floor;

        // Hanging: its top close under a ceiling and well clear of the floor.
        if (ceiling is { } c && c - top < 24 && position.Y - floor > 96)
        {
            // The chandelier rises 280 above its origin; under a ceiling lower than that over a head's room (80) it went
            // through the ceiling (2026-10-16: 198 of 336 checked), so the compact candles hang there instead.
            bool roomForChandelier = c - floor >= 280 / BlockMeshBuilder.Scale + 80;
            yield return w < 40 || !roomForChandelier
                ? new(HangingCandles, new Vector3(position.X, c - 49 / BlockMeshBuilder.Scale, position.Z), 0, "hanging candles")
                : new(Chandelier, new Vector3(position.X, c - 280 / BlockMeshBuilder.Scale, position.Z), 0, "chandelier");
            yield break;
        }
        // Wall-mounted: a wall face just behind it and clearly off the floor.
        if (NearestWall(position, 40) is { } wall && baseAboveFloor > 24)
        {
            // Skyrim headings turn clockwise from north. The torch leans out along its local +Y; the Imperial
            // sconce stands out along its local -X.
            var n = wall.Normal;
            bool sconce = w <= 16 && h <= 30;
            float heading = sconce ? MathF.Atan2(n.Z, -n.X) : MathF.Atan2(n.X, n.Z);
            // Mounted at least 20 units under the wall's top: a sprite at the edge put the torch half on the slanted cove
            // above (2026-10-16: 940 torches, eight a dungeon, with no wall behind their upper half).
            var mount = wall.Point with { Y = MathF.Min(wall.Point.Y, wall.Top - 20 / BlockMeshBuilder.Scale) };
            yield return new(sconce ? WallSconce : WallTorch, mount, heading, sconce ? "wall sconce" : "wall torch");
            yield break;
        }
        // Standing on the floor.
        if (baseAboveFloor <= 16)
        {
            var at = new Vector3(position.X, floor, position.Z);
            if (flat.TextureArchive == LightsArchive && flat.TextureRecord == CampfireRecord || flat.TextureArchive == E3LightsArchive && flat.TextureRecord == E3CampfireRecord)
            {
                yield return new(Campfire, at, 0, "campfire");
                yield return new(BrazierFire, at + new Vector3(0, 6 / BlockMeshBuilder.Scale, 0), 0, "campfire flames");
                yield return new(CampfireSound, at + new Vector3(0, 20 / BlockMeshBuilder.Scale, 0), 0, "campfire sound");
                yield break;
            }
            if (w >= 35 && h >= 70) yield return new(StandingBrazier, at, 0, "standing brazier");
            else if (w >= 35)
            {
                yield return new(Brazier, at, 0, "brazier");
                yield return new(BrazierFire, at + new Vector3(0, 30 / BlockMeshBuilder.Scale, 0), 0, "brazier fire");
            }
            else if (h >= 80) yield return new(TallCandelabra, at, 0, "tall candelabra");
            else if (h >= 40) yield return new(Candelabra, at, 0, "candelabra");
            else yield return new(TableCandle, at, 0, "candle");
            yield break;
        }
        // Resting on a raised surface such as a table or ledge: sit it at the sprite's base (DungeonCellBuilder then
        // settles it onto the Skyrim table or shelf standing in for Daggerfall's).
        yield return new(TableCandle, new Vector3(position.X, bottom, position.Z), 0, SurfaceCandle);
    }

    /// <summary>The kind of a candle on a raised surface (a table, a shelf, a ledge).</summary>
    public const string SurfaceCandle = "candle on a surface";

    /// <summary>Debug summary of the surroundings used to classify a sprite.</summary>
    public string Describe(Vector3 p) =>
        $"floor {p.Y - (HeightBelow(p) ?? float.NaN):F0} below, ceiling {(HeightAbove(p) - p.Y)?.ToString("F0") ?? "none"} above, wall {(NearestWall(p, 200) is { } w ? Vector3.Distance(w.Point, p).ToString("F0") : "none")} away";

    float? HeightBelow(Vector3 p) => Surface(floors, p, below: true);
    float? HeightAbove(Vector3 p) => Surface(ceilings, p, below: false);

    static float? Surface(List<DfPlacedPolygon> polys, Vector3 p, bool below)
    {
        float? best = null;
        foreach (var poly in polys)
            for (int i = 1; i + 1 < poly.Points.Length; i++)
                if (HeightInTriangle(poly.Points[0], poly.Points[i], poly.Points[i + 1], p.X, p.Z) is { } y
                    && (below ? y <= p.Y + 16 && (best is null || y > best) : y >= p.Y - 16 && (best is null || y < best)))
                    best = y;
        return best;
    }

    /// <summary>The closest wall face within range whose extent covers the point; Normal points toward the point, Top
    /// is the face's highest point.</summary>
    (Vector3 Point, Vector3 Normal, float Top)? NearestWall(Vector3 p, float range)
    {
        (Vector3, Vector3, float)? best = null; float bestD = range;
        foreach (var wall in walls)
        {
            var n = Vector3.Normalize(new Vector3(wall.Normal.X, 0, wall.Normal.Z));
            float d = Vector3.Dot(p - wall.Points[0], n);
            if (MathF.Abs(d) >= bestD) continue;
            var onPlane = p - n * d;
            var min = wall.Points.Aggregate(Vector3.Min) - new Vector3(8);
            var max = wall.Points.Aggregate(Vector3.Max) + new Vector3(8);
            if (onPlane.X < min.X || onPlane.X > max.X || onPlane.Y < min.Y || onPlane.Y > max.Y || onPlane.Z < min.Z || onPlane.Z > max.Z) continue;
            bestD = MathF.Abs(d);
            best = (onPlane, d >= 0 ? n : -n, wall.Points.Max(q => q.Y));
        }
        return best;
    }

    static float? HeightInTriangle(Vector3 a, Vector3 b, Vector3 c, float x, float z)
    {
        float den = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
        if (MathF.Abs(den) < 1e-6f) return null;
        float w0 = ((b.Z - c.Z) * (x - c.X) + (c.X - b.X) * (z - c.Z)) / den;
        float w1 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / den;
        float w2 = 1 - w0 - w1;
        return w0 < 0 || w1 < 0 || w2 < 0 ? null : w0 * a.Y + w1 * b.Y + w2 * c.Y;
    }

    /// <summary>Width and height of each record from a Daggerfall TEXTURE file header (no image data is read).</summary>
    static (int, int)[] ReadSpriteSizes(string path)
    {
        var d = File.ReadAllBytes(path);
        int count = BitConverter.ToInt16(d, 0);
        var sizes = new (int, int)[count];
        for (int r = 0; r < count; r++)
        {
            int pos = BitConverter.ToInt32(d, 26 + r * 20 + 2);
            int scale = BitConverter.ToInt16(d, pos + 24);
            float s = 1 + scale / 256f; // negative scale shrinks the sprite
            sizes[r] = ((int)(BitConverter.ToInt16(d, pos + 4) * s), (int)(BitConverter.ToInt16(d, pos + 6) * s));
        }
        return sizes;
    }
}
