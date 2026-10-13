using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's exterior terrain as a Skyrim worldspace. A Morrowind cell is 8192 units with 65x65 height points 128
/// units apart, stored as signed byte steps of 8 units (VHGT) — Skyrim's own encoding at twice the cell size — so
/// each Morrowind cell becomes 2x2 Skyrim cells with the same heights, normals (VNML) and vertex colours (VCLR).
/// Its 16x16 grid of texture tiles (VTEX, 512 units each; stored in 4x4 blocks of 4x4) becomes, per Skyrim quadrant,
/// a base layer plus alpha layers weighted by the tiles around each vertex.
/// </summary>
public sealed class MwTerrain(SkyrimMod mod, MwTextures textures)
{
    public const int MwCellUnits = 8192, SkyCellUnits = 4096, MwVerts = 65, SkyVerts = 33, QuadVerts = 17;
    readonly Dictionary<string, LandscapeTexture?> landTextures = new(StringComparer.OrdinalIgnoreCase);
    public List<string> Problems { get; } = [];

    /// <summary>Absolute heights (world units) of a LAND record, [x, y] with x east and y north.</summary>
    public static float[,] Heights(Tes3Record land)
    {
        var v = land.Get("VHGT")!.Value;
        float offset = v.Float(0);
        var h = new float[MwVerts, MwVerts];
        float row = offset;
        for (int y = 0; y < MwVerts; y++)
        {
            row += (sbyte)v.Data[4 + y * MwVerts];
            float col = row;
            h[0, y] = col * 8;
            for (int x = 1; x < MwVerts; x++)
            {
                col += (sbyte)v.Data[4 + y * MwVerts + x];
                h[x, y] = col * 8;
            }
        }
        return h;
    }

    /// <summary>VTEX as [tx, ty] (x east, y north): undoes the 4x4-block storage order.</summary>
    public static ushort[,]? Tiles(Tes3Record land)
    {
        if (land.Get("VTEX") is not { } f) return null;
        var t = new ushort[16, 16];
        int i = 0;
        for (int y1 = 0; y1 < 4; y1++)
            for (int x1 = 0; x1 < 4; x1++)
                for (int y2 = 0; y2 < 4; y2++)
                    for (int x2 = 0; x2 < 4; x2++)
                        t[x1 * 4 + x2, y1 * 4 + y2] = BitConverter.ToUInt16(f.Data, 2 * i++);
        return t;
    }

    /// <summary>The Skyrim landscape texture for a Morrowind texture file (TXST + LTEX, created once).</summary>
    LandscapeTexture? LandTexture(string file)
    {
        if (landTextures.TryGetValue(file, out var hit)) return hit;
        var (path, _) = textures.Ensure(file);
        if (path is null) { Problems.Add($"landscape texture {file} not found"); return landTextures[file] = null; }
        string rel = path.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? path[9..] : path;
        string id = new(Path.GetFileNameWithoutExtension(file).Where(char.IsLetterOrDigit).ToArray());
        var set = new TextureSet(mod)
        {
            EditorID = "MWLandSet" + id,
            ObjectBounds = new ObjectBounds(),
            Diffuse = rel,
            NormalOrGloss = MwTextures.FlatNormal[9..],
        };
        mod.TextureSets.Add(set);
        var lt = new LandscapeTexture(mod)
        {
            EditorID = "MWLand" + id,
            TextureSet = set.ToNullableLink<ITextureSetGetter>(),
            MaterialType = new FormLinkNullable<IMaterialTypeGetter>(),
            HavokFriction = 30,
            HavokRestitution = 30,
            TextureSpecularExponent = 30,
        };
        mod.LandscapeTextures.Add(lt);
        return landTextures[file] = lt;
    }

    /// <summary>
    /// The four Skyrim LAND records of one Morrowind cell, keyed by Skyrim cell grid. <paramref name="neighbour"/>
    /// supplies heights across cell edges (Morrowind's edge rows repeat the neighbour's, as Skyrim's do).
    /// </summary>
    public IEnumerable<((int X, int Y) Grid, Landscape Land, float Min)> Build(int gx, int gy, Tes3Record land, Dictionary<int, string> ltex)
    {
        var h = Heights(land);
        var tiles = Tiles(land);
        var colors = land.Get("VCLR");
        for (int j = 0; j < 2; j++)
            for (int i = 0; i < 2; i++)
            {
                int vx0 = i * 32, vy0 = j * 32;
                var deltas = new Array2d<sbyte>(new sbyte[SkyVerts, SkyVerts]);
                int origin = (int)Math.Round(h[vx0, vy0] / 8), rowStart = origin;
                float min = float.MaxValue;
                for (int y = 0; y < SkyVerts; y++)
                {
                    int t0 = (int)Math.Round(h[vx0, vy0 + y] / 8);
                    int d0 = y == 0 ? 0 : Math.Clamp(t0 - rowStart, -128, 127);
                    rowStart += d0;
                    deltas[0, y] = (sbyte)d0;
                    int current = rowStart;
                    for (int x = 1; x < SkyVerts; x++)
                    {
                        int t = (int)Math.Round(h[vx0 + x, vy0 + y] / 8);
                        int d = Math.Clamp(t - current, -128, 127);
                        current += d;
                        deltas[x, y] = (sbyte)d;
                    }
                    for (int x = 0; x < SkyVerts; x++) min = Math.Min(min, h[vx0 + x, vy0 + y]);
                }
                var landscape = new Landscape(mod)
                {
                    Flags = (Landscape.Flag)(0x1D | (colors is not null ? 0x2 : 0)),
                    VertexHeightMap = new LandscapeVertexHeightMap { Offset = origin, HeightMap = deltas },
                    VertexNormals = new Array2d<P3UInt8>(new P3UInt8[SkyVerts, SkyVerts]),
                };
                for (int y = 0; y < SkyVerts; y++)
                    for (int x = 0; x < SkyVerts; x++)
                    {
                        int mx = vx0 + x, my = vy0 + y;
                        float dx = (h[Math.Min(mx + 1, 64), my] - h[Math.Max(mx - 1, 0), my]) / ((Math.Min(mx + 1, 64) - Math.Max(mx - 1, 0)) * 128f);
                        float dy = (h[mx, Math.Min(my + 1, 64)] - h[mx, Math.Max(my - 1, 0)]) / ((Math.Min(my + 1, 64) - Math.Max(my - 1, 0)) * 128f);
                        float len = MathF.Sqrt(dx * dx + dy * dy + 1);
                        landscape.VertexNormals[x, y] = new P3UInt8((byte)(sbyte)Math.Round(-dx / len * 127), (byte)(sbyte)Math.Round(-dy / len * 127), (byte)(sbyte)Math.Round(1 / len * 127));
                    }
                if (colors is { } c)
                {
                    landscape.VertexColors = new Array2d<P3UInt8>(new P3UInt8[SkyVerts, SkyVerts]);
                    for (int y = 0; y < SkyVerts; y++)
                        for (int x = 0; x < SkyVerts; x++)
                        {
                            int o = ((vy0 + y) * MwVerts + vx0 + x) * 3;
                            landscape.VertexColors[x, y] = new P3UInt8(c.Data[o], c.Data[o + 1], c.Data[o + 2]);
                        }
                }
                landscape.Layers.AddRange(Layers(tiles, ltex, vx0, vy0));
                landscape.IsCompressed = true;
                yield return ((gx * 2 + i, gy * 2 + j), landscape, min);
            }
    }

    /// <summary>
    /// Per quadrant (17x17 vertices), each vertex weighted by the texture tiles around it (a tile spans 4 quads; a
    /// vertex on a tile edge counts both sides); the most-used texture is the base, up to five more are alpha layers
    /// whose opacity is their weight over the weight of themselves and everything beneath.
    /// </summary>
    IEnumerable<BaseLayer> Layers(ushort[,]? tiles, Dictionary<int, string> ltex, int vx0, int vy0)
    {
        const int maxAlphaLayers = 5;
        string Texture(int tx, int ty)
        {
            int v = tiles is null ? 0 : tiles[Math.Clamp(tx, 0, 15), Math.Clamp(ty, 0, 15)];
            return v > 0 && ltex.TryGetValue(v - 1, out var file) ? file : "_land_default.tga";
        }
        for (int q = 0; q < 4; q++)
        {
            int qx = vx0 + (q & 1) * 16, qy = vy0 + (q >> 1) * 16;
            var weights = new List<Dictionary<string, float>>(QuadVerts * QuadVerts);
            var totals = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            for (int ly = 0; ly < QuadVerts; ly++)
                for (int lx = 0; lx < QuadVerts; lx++)
                {
                    int mx = qx + lx, my = qy + ly;
                    var w = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    // The tiles this vertex touches: one inside a tile, two on an edge, four at a corner.
                    var xs = mx % 4 == 0 ? new[] { mx / 4 - 1, mx / 4 } : [mx / 4];
                    var ys = my % 4 == 0 ? new[] { my / 4 - 1, my / 4 } : [my / 4];
                    foreach (var tx in xs) foreach (var ty in ys)
                    {
                        string t = Texture(tx, ty);
                        w[t] = w.GetValueOrDefault(t) + 1f / (xs.Length * ys.Length);
                    }
                    foreach (var (t, v) in w) totals[t] = totals.GetValueOrDefault(t) + v;
                    weights.Add(w);
                }
            var order = totals.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).Where(t => LandTexture(t) is not null).Take(maxAlphaLayers + 1).ToList();
            if (order.Count == 0) continue;
            var quadrant = (Quadrant)q;
            yield return new BaseLayer { Header = new LayerHeader { Texture = LandTexture(order[0])!.ToLink<ILandscapeTextureGetter>(), Quadrant = quadrant } };
            for (int li = 1; li < order.Count; li++)
            {
                var layer = new AlphaLayer { Header = new LayerHeader { Texture = LandTexture(order[li])!.ToLink<ILandscapeTextureGetter>(), Quadrant = quadrant, LayerNumber = (ushort)(li - 1) }, AlphaLayerData = [] };
                for (int k = 0; k < weights.Count; k++)
                {
                    float below = 0;
                    for (int b = 0; b <= li; b++) below += weights[k].GetValueOrDefault(order[b]);
                    float alpha = below > 0 ? weights[k].GetValueOrDefault(order[li]) / below : 0;
                    if (alpha > 0.004f) layer.AlphaLayerData.Add(new AlphaLayerData { Position = (ushort)k, Opacity = Math.Min(alpha, 1f) });
                }
                if (layer.AlphaLayerData.Count > 0) yield return layer;
            }
        }
    }
}
