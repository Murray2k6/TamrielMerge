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

    /// <summary>The land's height at a world position (between its height points), or null where there is no land.</summary>
    public static float? GroundAt(MwWorld world, float x, float y, Dictionary<(int, int), float[,]?>? cache = null)
    {
        int gx = (int)MathF.Floor(x / MwCellUnits), gy = (int)MathF.Floor(y / MwCellUnits);
        float[,]? h = null;
        if (cache is null || !cache.TryGetValue((gx, gy), out h))
        {
            h = world.Lands.TryGetValue((gx, gy), out var l) ? Heights(l.Land) : null;
            if (cache is not null) cache[(gx, gy)] = h;
        }
        if (h is null) return null;
        float fx = (x - gx * MwCellUnits) / 128, fy = (y - gy * MwCellUnits) / 128;
        int x0 = Math.Clamp((int)fx, 0, MwVerts - 2), y0 = Math.Clamp((int)fy, 0, MwVerts - 2);
        float tx = fx - x0, ty = fy - y0;
        return (h[x0, y0] * (1 - tx) + h[x0 + 1, y0] * tx) * (1 - ty) + (h[x0, y0 + 1] * (1 - tx) + h[x0 + 1, y0 + 1] * tx) * ty;
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
        string? path = textures.EnsureLand(file);
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
                landscape.Layers.AddRange(Layers(gx, gy, vx0, vy0));
                landscape.IsCompressed = true;
                yield return ((gx * 2 + i, gy * 2 + j), landscape, min);
            }
    }

    /// <summary>Every land of the world by Morrowind cell (set by the builder): a vertex on a cell's edge is weighted by
    /// the neighbouring cell's texture tiles too, and a quadrant's grounds are settled with its neighbours'.</summary>
    public IReadOnlyDictionary<(int X, int Y), (Tes3Record Land, Dictionary<int, string> Textures)>? Lands { get; set; }
    readonly Dictionary<(int, int), ushort[,]?> tileCache = [];
    readonly Dictionary<(int, int), Dictionary<string, float>?> keptCache = [];
    /// <summary>Quadrants that held more than six textures, and vertices given another ground because a quadrant they
    /// lie on could not hold theirs.</summary>
    public int CrowdedQuadrants { get; private set; }
    public int VerticesRestood { get; private set; }
    public int NothingShared { get; private set; }

    static int Floor(int a, int b) => (int)Math.Floor(a / (double)b);

    /// <summary>The texture file of a tile by world tile coordinates (16 a Morrowind cell); a tile of a cell without
    /// land is read as the nearest tile of the cell (<paramref name="hx"/>, <paramref name="hy"/>) asking.</summary>
    string TextureAt(int tx, int ty, int hx, int hy)
    {
        int gx = Floor(tx, 16), gy = Floor(ty, 16);
        if ((gx != hx || gy != hy) && (Lands is null || !Lands.ContainsKey((gx, gy))))
        {
            tx = Math.Clamp(tx, hx * 16, hx * 16 + 15); ty = Math.Clamp(ty, hy * 16, hy * 16 + 15);
            gx = hx; gy = hy;
        }
        if (Lands is null || !Lands.TryGetValue((gx, gy), out var land)) return "_land_default.tga";
        if (!tileCache.TryGetValue((gx, gy), out var tiles)) tileCache[(gx, gy)] = tiles = Tiles(land.Land);
        int v = tiles is null ? 0 : tiles[tx - gx * 16, ty - gy * 16];
        return v > 0 && land.Textures.TryGetValue(v - 1, out var file) ? file : "_land_default.tga";
    }

    /// <summary>A world vertex's grounds (128-unit grid, 64 a Morrowind cell) as shares: the tiles it touches, one
    /// inside a tile, two on an edge, four at a corner.</summary>
    Dictionary<string, float> VertexGrounds(int vx, int vy, int hx, int hy)
    {
        var w = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        int bx = Floor(vx, 4), by = Floor(vy, 4);
        var xs = vx - bx * 4 == 0 ? new[] { bx - 1, bx } : [bx];
        var ys = vy - by * 4 == 0 ? new[] { by - 1, by } : [by];
        foreach (var tx in xs) foreach (var ty in ys)
        {
            string t = TextureAt(tx, ty, hx, hy);
            w[t] = w.GetValueOrDefault(t) + 1f / (xs.Length * ys.Length);
        }
        return w;
    }

    /// <summary>The (up to six) textures a world quadrant lays (quadrants of 16 quads, 4 a Morrowind cell) with how
    /// much of it each covers; null where there is no land.</summary>
    Dictionary<string, float>? Kept(int qx, int qy)
    {
        if (keptCache.TryGetValue((qx, qy), out var hit)) return hit;
        int hx = Floor(qx, 4), hy = Floor(qy, 4);
        if (Lands is null || !Lands.ContainsKey((hx, hy))) return keptCache[(qx, qy)] = null;
        var totals = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        for (int ly = 0; ly < QuadVerts; ly++)
            for (int lx = 0; lx < QuadVerts; lx++)
                foreach (var (t, v) in VertexGrounds(qx * 16 + lx, qy * 16 + ly, hx, hy)) totals[t] = totals.GetValueOrDefault(t) + v;
        var laid = totals.Where(kv => LandTexture(kv.Key) is not null).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).ToList();
        if (laid.Count > 6) CrowdedQuadrants++;
        var kept = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in laid.Take(6)) kept[kv.Key] = kv.Value;
        return keptCache[(qx, qy)] = kept;
    }

    /// <summary>
    /// Per quadrant (17x17 vertices), each vertex weighted by the texture tiles around it; the most-used texture is
    /// the base, up to five more are alpha layers. A quadrant holds six textures, and a vertex on a quadrant's edge is
    /// its neighbours' too: it keeps only the grounds that every quadrant it lies on lays (and where none of its own
    /// is, takes the commonest ground they all lay), so that both sides of an edge draw the same ground there.
    /// Grounds dropped by one quadrant alone showed as a straight seam along the edge (Redguard's land, 2026-11-12).
    /// </summary>
    IEnumerable<BaseLayer> Layers(int gx, int gy, int vx0, int vy0)
    {
        for (int q = 0; q < 4; q++)
        {
            int qx = gx * 4 + vx0 / 16 + (q & 1), qy = gy * 4 + vy0 / 16 + (q >> 1);
            if (Kept(qx, qy) is not { Count: > 0 } kept) continue;
            var order = kept.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => kv.Key).ToList();
            var weights = new List<Dictionary<string, float>>(QuadVerts * QuadVerts);
            for (int ly = 0; ly < QuadVerts; ly++)
                for (int lx = 0; lx < QuadVerts; lx++)
                {
                    var w = VertexGrounds(qx * 16 + lx, qy * 16 + ly, gx, gy);
                    // The quadrants this vertex lies on, and the grounds all of them lay.
                    var sharing = new List<Dictionary<string, float>> { kept };
                    for (int oy = ly == 0 ? -1 : 0; oy <= (ly == 16 ? 1 : 0); oy++)
                        for (int ox = lx == 0 ? -1 : 0; ox <= (lx == 16 ? 1 : 0); ox++)
                            if ((ox != 0 || oy != 0) && Kept(qx + ox, qy + oy) is { Count: > 0 } theirs) sharing.Add(theirs);
                    var allowed = new HashSet<string>(kept.Keys, StringComparer.OrdinalIgnoreCase);
                    foreach (var s in sharing.Skip(1)) allowed.IntersectWith(s.Keys);
                    if (allowed.Count == 0) { allowed.UnionWith(kept.Keys); NothingShared++; }   // nothing in common: this quadrant's own
                    var mine = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                    float sum = 0;
                    foreach (var (t, v) in w) if (allowed.Contains(t)) { mine[t] = v; sum += v; }
                    if (sum > 0) { foreach (var t in mine.Keys.ToList()) mine[t] /= sum; }
                    else
                    {
                        string stand = allowed.OrderByDescending(t => sharing.Sum(s => s.GetValueOrDefault(t))).ThenBy(t => t, StringComparer.OrdinalIgnoreCase).First();
                        mine[stand] = 1;
                        VerticesRestood++;
                    }
                    weights.Add(mine);
                }
            var quadrant = (Quadrant)q;
            yield return new BaseLayer { Header = new LayerHeader { Texture = LandTexture(order[0])!.ToLink<ILandscapeTextureGetter>(), Quadrant = quadrant } };
            ushort number = 0;
            for (int li = 1; li < order.Count; li++)
            {
                var layer = new AlphaLayer { Header = new LayerHeader { Texture = LandTexture(order[li])!.ToLink<ILandscapeTextureGetter>(), Quadrant = quadrant, LayerNumber = number }, AlphaLayerData = [] };
                for (int k = 0; k < weights.Count; k++)
                {
                    // A layer's strength is its ground's plain share of the vertex; the base takes the rest. The game
                    // adds the layers (seen on Redguard's land, 2026-11-12: written as layers laid one over another the
                    // strengths of a vertex came to more than 1 and the land drew too bright, by another amount in
                    // each quadrant, straight bright edges along cell borders).
                    float alpha = weights[k].GetValueOrDefault(order[li]);
                    if (alpha > 0.004f) layer.AlphaLayerData.Add(new AlphaLayerData { Position = (ushort)k, Opacity = Math.Min(alpha, 1f) });
                }
                if (layer.AlphaLayerData.Count > 0) { number++; yield return layer; }
            }
        }
    }
}
