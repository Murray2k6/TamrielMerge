using System.Numerics;
using TamrielMerge.Daggerfall;
using Tex = TamrielMerge.Skyrim.Meshes.BlockMeshBuilder.Tex;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>A Daggerfall town's climate, as its buildings show it.</summary>
public enum TownStyle { Desert, Mountain, Woodland, Swamp }

/// <summary>
/// Daggerfall's own textures for its towns' buildings, inside and out (user, 2026-10-11: "daggerfall needs its building
/// textures and upscaled" - they had been drawn with Skyrim's Solitude, Windhelm, Whiterun and Riften textures by
/// climate). Each surface takes its own texture in the town's climate, as Daggerfall swaps them: a building family's
/// archives come in four, desert (+0), mountain (+100), temperate (+300) and swamp (+400) - the swamp falling back to
/// temperate where it has none. Upscaled with the rest (DfTextureExport). The town's ground keeps Skyrim's land
/// texture; solid colours their palette colour.
/// </summary>
public sealed class TownTextures(string arena2, DfTextureExport export)
{
    static readonly Tex Ground = new(@"textures\landscape\dirt02.dds", @"textures\landscape\dirt02_n.dds", false);

    public static TownStyle StyleOf(DfClimate climate) => climate switch
    {
        DfClimate.Desert or DfClimate.Desert2 or DfClimate.Subtropical or DfClimate.Rainforest => TownStyle.Desert,
        DfClimate.Mountain or DfClimate.MountainWoods => TownStyle.Mountain,
        DfClimate.Swamp => TownStyle.Swamp,
        _ => TownStyle.Woodland,
    };

    static int ClimateBase(TownStyle style) => style switch
    {
        TownStyle.Desert => 0, TownStyle.Mountain => 100, TownStyle.Swamp => 400, _ => 300,
    };

    readonly Dictionary<int, string> names = new();
    string NameOf(int archive)
    {
        if (!names.TryGetValue(archive, out var n)) names[archive] = n = archive is >= 0 and < 512 ? DfTexture.Load(arena2, archive)?.Name ?? "" : "";
        return n;
    }

    static bool Has(string name, params string[] words) => words.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The archive in the town's climate: the same family a hundreds-step away, but only when its name is the same apart
    /// from the climate ("Desert Manor" and "Woodland Manor", "Desert Roofs" and "Woodland Roofs") - Daggerfall reuses
    /// the numbers for unrelated archives (user, 2026-10-12: "daggerfall beuilding textures look wrong": "Desert Store
    /// Int" 71 became 371 "Misc Mantella texs", and walls wore 334 "Daggerfall people"). The swamp's falls back to the
    /// temperate where it has none.
    /// </summary>
    internal int InClimate(int archive, TownStyle style)
    {
        if (archive is < 2 or >= 500) return archive;
        string own = FamilyKey(NameOf(archive));
        if (own.Length == 0) return archive;
        int family = archive % 100;
        foreach (int target in style == TownStyle.Swamp ? [400 + family, 300 + family] : new[] { ClimateBase(style) + family })
            if (FamilyKey(NameOf(target)) == own) return target;
        // Not at the usual number: the archive of that name in the town's climate wherever it is, else its own.
        return ByFamily().TryGetValue((own, StyleWord(style)), out int found) ? found : archive;
    }

    static string StyleWord(TownStyle style) => style switch
    {
        TownStyle.Desert => "desert", TownStyle.Mountain => "mountain", TownStyle.Swamp => "swamp", _ => "woodland",
    };

    Dictionary<(string Family, string Climate), int>? byFamily;

    /// <summary>Every climate archive below 500 by its family and climate word (temperate counted as woodland).</summary>
    Dictionary<(string Family, string Climate), int> ByFamily()
    {
        if (byFamily is not null) return byFamily;
        byFamily = new();
        for (int a = 2; a < 500; a++)
        {
            var words = NameOf(a).ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string? climate = words.FirstOrDefault(w => ClimateWords.Contains(w));
            if (climate is null) continue;
            if (climate == "temperate") climate = "woodland";
            byFamily.TryAdd((FamilyKey(NameOf(a)), climate), a);
        }
        return byFamily;
    }

    static readonly string[] ClimateWords = ["desert", "mountain", "woodland", "temperate", "swamp"];

    /// <summary>An archive's name without its climate word, lower case, "Int.", "Interior" and "Interiors" alike ("Tavern
    /// Interiors", "Mountain Tavern Int."): the family it belongs to.</summary>
    static string FamilyKey(string name) =>
        string.Join(' ', name.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !ClimateWords.Contains(w))
            .Select(w => w is "int." or "interior" or "interiors" ? "int" : w));

    /// <summary>The terrain tile set of a climate (Daggerfall Unity ClimateSwaps): desert 2, mountain 102, temperate 302,
    /// swamp 402.</summary>
    public int TerrainArchive(TownStyle s)
    {
        int a = s switch { TownStyle.Desert => 2, TownStyle.Mountain => 102, TownStyle.Swamp => 402, _ => 302 };
        return NameOf(a).Length == 0 ? 302 : a;
    }

    /// <summary>
    /// A town block's ground as Daggerfall lays it (user, 2026-10-12: "now for the daggerfall textures for the game
    /// itself and worldspace"): its 16 x 16 tiles (DfTownBlock.GroundTiles: the record, 0x40 turned a quarter, 0x80
    /// flipped), each a square of its climate's terrain tile - grass, dirt, stone, water, paving and the tiles blending
    /// them - with the tile's own turn and flip. In Skyrim units, block-relative.
    /// </summary>
    internal List<(Vector3[] Pts, Vector3 N, Tex Tex)> GroundPolygons(DfTownBlock tb, TownStyle style)
    {
        int terrain = TerrainArchive(style);
        float tile = 256 * BlockMeshBuilder.Scale;
        var polys = new List<(Vector3[], Vector3, Tex)>();
        for (int tz = 0; tz < 16; tz++)
            for (int tx = 0; tx < 16; tx++)
            {
                byte t = tb.GroundTiles[tx, 15 - tz];
                var (path, w, h) = export.Ensure(terrain, t & 0x3F);
                var tex = new Tex(path, DfTextureExport.FlatNormal, false, w, h);
                float x0 = tx * tile, y0 = tz * tile;
                Vector3[] pts = [new(x0, y0, 0), new(x0 + tile, y0, 0), new(x0 + tile, y0 + tile, 0), new(x0, y0 + tile, 0)];
                // Corners south-west, south-east, north-east, north-west; the tile's top row to the north.
                Vector2[] uv = [new(0, h), new(w, h), new(w, 0), new(0, 0)];
                if ((t & 0x40) != 0) uv = [uv[1], uv[2], uv[3], uv[0]];          // a quarter turn
                if ((t & 0x80) != 0) uv = uv.Select(p => new Vector2(w - p.X, h - p.Y)).ToArray();   // flipped
                BlockMeshBuilder.SetTexelUvs(pts, uv);
                polys.Add((pts, Vector3.UnitZ, tex));
            }
        return polys;
    }

    /// <summary>The texture for a Daggerfall surface in a town of this climate.</summary>
    internal Tex Pick(int archive, int record, Vector3 normalUp, TownStyle style, bool interior)
    {
        string n = NameOf(archive);
        if (archive is 0 or 1 || Has(n, "Solid Colo")) return new Tex(export.EnsureSolid(archive, record), DfTextureExport.FlatNormal, false);
        if (Has(n, "Terrain")) return Ground;
        var (path, w, h) = export.Ensure(InClimate(archive, style), record);
        return new Tex(path, DfTextureExport.FlatNormal, false, w, h);
    }
}
