using System.Text.Json;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim.Terrain;

/// <summary>
/// Where every Daggerfall location with exterior blocks stands on the overland, at full size (docs/overland-locations.md;
/// user, 2026-10-25: "towns go on the overland like in daggerfall"). An RMB block is 2 x 2 cells here (4096 Daggerfall
/// units x BlockMeshBuilder.Scale), while a location's map pixel is one cell, so at their pixels 7,498 of 13,410 counted
/// locations overlapped another (`locations --overlaps`). Each is placed, biggest first, at its pixel when that is clear,
/// else at the nearest clear spot round it on land; footprints are cell-aligned (a block's corner on a cell's), with a
/// cell of open ground between neighbours. Worked out from the height field before any levelling, so it is the same
/// in every process; saved beside Daggerfall.esm (<see cref="FileName"/>) for the steps that come after.
/// </summary>
public sealed class LocationLayout
{
    /// <summary>A location's place: its footprint's south-west cell (global grid) and size in cells, the level its
    /// ground is made, and how far it was moved from its map pixel (units).</summary>
    public sealed record Site(int Region, int Index, string Name, int CellX, int CellY, int CellsW, int CellsH, float Ground, float Moved);

    public const string FileName = "Daggerfall.layout.json";
    /// <summary>Cells per RMB block (4096 Daggerfall units x 2.0 = 8192 units).</summary>
    public const int CellsPerBlock = 2;
    /// <summary>Open ground kept round every footprint (cells), between it and the next.</summary>
    const int Gap = 1;
    /// <summary>The most open ground kept between two footprints for the difference of their levels (cells).</summary>
    const int MaxSpacing = 8;
    /// <summary>How far round its pixel a location is looked for a clear place (cells).</summary>
    const int Reach = 96;
    /// <summary>The land under a footprint goes no lower than this (units: above the beach).</summary>
    const float MinGround = 96;
    /// <summary>The land eases back from a footprint's level to its own over this (units) at least.</summary>
    public const float Blend = 2 * 4096;
    /// <summary>The steepest that easing may be on average (degrees; the smooth step's steepest is half as steep again): a
    /// footprint far over or under the land along its edge eases back over longer, up to <see cref="MaxBlend"/>, instead of
    /// standing on a mound or in a pit (2026-11-03, measured on the layout, `locations --layout --embank`: 229 towns stood
    /// over 1,000 units above the land at their edge, Charenfort 9,245 and Sentinel 5,126, all eased back over
    /// <see cref="Blend"/>; user: "the towns were on hills").</summary>
    public const float MaxBlendDegrees = 10;
    public const float MaxBlend = 16 * 4096;

    /// <summary>
    /// How far a site's ground eases back to the land (units): from every point of its edge (8 quads apart, the corners
    /// outward on the diagonal) the land is followed straight out to where it is within <see cref="MaxBlendDegrees"/> on
    /// average of the site's level, and the easing reaches the farthest of those, <see cref="Blend"/> at least and
    /// <see cref="MaxBlend"/> at most. Measured from the edge alone (2026-11-03, first try) a town at a mountain's foot,
    /// level with the land at its edge, kept the short easing and had the mountain's rise pressed into it (189 of the 212
    /// towns still with a step over 30 degrees beside them). The sea ends a line: a harbour town keeps its shore rather
    /// than filling the sea before it.
    /// </summary>
    public static float BlendOf(HeightField f, Site s)
    {
        int q = HeightField.QuadsPerCell;
        float unit = HeightField.UnitsPerQuad, slope = MathF.Tan(MaxBlendDegrees * MathF.PI / 180);
        int vx0 = (s.CellX - f.MinCellX) * q, vy0 = (s.CellY - f.MinCellY) * q, vx1 = vx0 + s.CellsW * q, vy1 = vy0 + s.CellsH * q;
        int first = (int)(Blend / unit);
        float most = Blend;
        void Out(int sx, int sy, int dx, int dy)
        {
            float along = unit * MathF.Sqrt(dx * dx + dy * dy);
            int i = (int)MathF.Ceiling(first / (along / unit));
            for (; i * along <= MaxBlend; i += 2)
            {
                int vx = sx + dx * i, vy = sy + dy * i;
                if (vx < 0 || vy < 0 || vx >= f.VertsX || vy >= f.VertsY) return;
                float land = f.At(vx, vy);
                if (land < 0 || MathF.Abs(land - s.Ground) <= i * along * slope) break;
            }
            most = MathF.Max(most, MathF.Min(i * along, MaxBlend));
        }
        for (int x = vx0; x <= vx1; x += 8) { Out(x, vy0, 0, -1); Out(x, vy1, 0, 1); }
        for (int y = vy0; y <= vy1; y += 8) { Out(vx0, y, -1, 0); Out(vx1, y, 1, 0); }
        Out(vx0, vy0, -1, -1); Out(vx1, vy0, 1, -1); Out(vx0, vy1, -1, 1); Out(vx1, vy1, 1, 1);
        return most;
    }
    /// <summary>How far under its ground level the land is made inside a footprint (units), its edge left on it: the
    /// blocks' ground meshes stand at the level and cover the footprint, and land at the same height showed through them
    /// in patches (both drawn at once). More than the land's own height steps (8 units, VHGT) and the ground mesh's
    /// collision (12 thick) under it.</summary>
    public const float Sink = 24;

    public List<Site> Sites { get; init; } = [];

    readonly Dictionary<(int, int), Site> byLocation = [];

    public Site? Of(DfLocation loc) => Lookup().GetValueOrDefault((loc.Region, loc.Index));

    Dictionary<(int, int), Site> Lookup()
    {
        if (byLocation.Count == 0) foreach (var s in Sites) byLocation[(s.Region, s.Index)] = s;
        return byLocation;
    }

    /// <summary>The footprint's south-west corner (global units, at its ground level).</summary>
    public static Noggog.P3Float Corner(Site s) => new(s.CellX * 4096f, s.CellY * 4096f, s.Ground);

    /// <summary>The footprint's middle (global units, at its ground level).</summary>
    public static Noggog.P3Float Centre(Site s) => new((s.CellX + s.CellsW / 2f) * 4096f, (s.CellY + s.CellsH / 2f) * 4096f, s.Ground);

    /// <summary>
    /// Lays out <paramref name="locations"/> (those with exterior blocks) on the unlevelled field <paramref name="f"/>.
    /// </summary>
    public static LocationLayout Make(HeightField f, IEnumerable<DfLocation> locations)
    {
        var order = locations.Where(l => l.ExteriorBlocks.Length > 0 && l.BlockWidth > 0 && l.BlockHeight > 0)
            .OrderByDescending(l => l.BlockWidth * l.BlockHeight).ThenBy(l => l.Region).ThenBy(l => l.Index).ToList();
        var taken = new HashSet<(int, int)>();
        var sites = new List<Site>();
        var seams = Seams(new OverlandTiles(f));
        // The sites placed, by 16-cell square, to find those near a spot.
        const int Bucket = 16;
        var buckets = new Dictionary<(int, int), List<Site>>();
        float levelPerCell = 4096f * MathF.Tan(MaxBlendDegrees * MathF.PI / 180);
        // Whether a footprint at (x, y) with ground level g stands far enough from every site placed for the land between
        // to ease from one level to the other no steeper than MaxBlendDegrees on average: a cell of open ground for every
        // levelPerCell of difference, Gap at least and MaxSpacing at most (2026-11-03, measured, `locations --layout
        // --embank`: of 201 towns with a step over 30 degrees beside them once levelled, 175 had a site within 3 cells
        // whose level was over 1,000 units from theirs, a Gap apart).
        bool Spaced(int x, int y, int w, int h, float g)
        {
            for (int by = Math.DivRem(y - MaxSpacing, Bucket, out _) - 1; by <= (y + h + MaxSpacing) / Bucket + 1; by++)
                for (int bx = Math.DivRem(x - MaxSpacing, Bucket, out _) - 1; bx <= (x + w + MaxSpacing) / Bucket + 1; bx++)
                {
                    if (!buckets.TryGetValue((bx, by), out var near)) continue;
                    foreach (var o in near)
                    {
                        float gx = Math.Max(Math.Max(o.CellX - (x + w), x - (o.CellX + o.CellsW)), 0);
                        float gy = Math.Max(Math.Max(o.CellY - (y + h), y - (o.CellY + o.CellsH)), 0);
                        float need = Math.Clamp(MathF.Abs(o.Ground - g) / levelPerCell, Gap, MaxSpacing);
                        if (MathF.Sqrt(gx * gx + gy * gy) < need) return false;
                    }
                }
            return true;
        }
        foreach (var loc in order)
        {
            int w = loc.BlockWidth * CellsPerBlock, h = loc.BlockHeight * CellsPerBlock;
            var at = LocationPlacer.WorldPosition(f, loc.PixelX + 0.5, loc.PixelY + 0.5);
            // The corner putting the footprint's middle on the pixel's.
            int wantX = (int)MathF.Round(at.X / 4096f - w / 2f), wantY = (int)MathF.Round(at.Y / 4096f - h / 2f);
            (int X, int Y, float Ground)? best = null, fallback = null;
            for (int r = 0; r <= Reach && best is null; r++)
            {
                // The ring r cells out, nearest first.
                foreach (var (dx, dy) in Ring(r))
                {
                    int x = wantX + dx, y = wantY + dy;
                    if (!Clear(taken, x, y, w, h) || seams.Crosses(x, y, w, h)) continue;
                    var (ground, land) = GroundOf(f, x, y, w, h);
                    fallback ??= (x, y, ground);
                    if (!land || !Spaced(x, y, w, h, ground)) continue;
                    best = (x, y, ground);
                    break;
                }
            }
            var (cx, cy, g) = best ?? fallback ?? (wantX, wantY, MinGround);
            for (int y = cy - Gap; y < cy + h + Gap; y++)
                for (int x = cx - Gap; x < cx + w + Gap; x++) taken.Add((x, y));
            float moved = MathF.Sqrt(MathF.Pow((cx - wantX) * 4096f, 2) + MathF.Pow((cy - wantY) * 4096f, 2));
            var site = new Site(loc.Region, loc.Index, loc.Name, cx, cy, w, h, g, moved);
            sites.Add(site);
            for (int by = Math.DivRem(cy, Bucket, out _) - 1; by <= (cy + h) / Bucket + 1; by++)
                for (int bx = Math.DivRem(cx, Bucket, out _) - 1; bx <= (cx + w) / Bucket + 1; bx++)
                {
                    if (!buckets.TryGetValue((bx, by), out var list)) buckets[(bx, by)] = list = [];
                    list.Add(site);
                }
        }
        return new LocationLayout { Sites = sites };
    }

    /// <summary>
    /// The bands round the overland's tile seams (global grid, start inclusive, end exclusive) no footprint may touch: a
    /// tile shows <see cref="OverlandTiles.Overlap"/> cells of its neighbour's land, but what stands there only in the
    /// tile whose core it is in, so a location in the overlap would be bare levelled ground seen from the other tile and
    /// come into view whole as the player crossed over. Kept a <see cref="Gap"/> clear of it too.
    /// </summary>
    sealed record SeamBands(List<(int Lo, int Hi)> X, List<(int Lo, int Hi)> Y)
    {
        public bool Crosses(int x, int y, int w, int h) =>
            X.Any(b => x < b.Hi && x + w > b.Lo) || Y.Any(b => y < b.Hi && y + h > b.Lo);
    }

    static SeamBands Seams(OverlandTiles tiles)
    {
        const int reach = OverlandTiles.Overlap + Gap;
        var xs = Enumerable.Range(1, tiles.Columns - 1).Select(c => tiles.MinCellX + c * tiles.CoreX).Select(s => (s - reach, s + reach)).ToList();
        var ys = Enumerable.Range(1, tiles.Rows - 1).Select(r => tiles.MinCellY + r * tiles.CoreY).Select(s => (s - reach, s + reach)).ToList();
        return new SeamBands(xs, ys);
    }

    static bool Clear(HashSet<(int, int)> taken, int x0, int y0, int w, int h)
    {
        for (int y = y0; y < y0 + h; y++)
            for (int x = x0; x < x0 + w; x++)
                if (taken.Contains((x, y))) return false;
        return true;
    }

    /// <summary>The offsets r cells out (the square ring), nearest the middle first.</summary>
    static IEnumerable<(int, int)> Ring(int r)
    {
        if (r == 0) { yield return (0, 0); yield break; }
        var ring = new List<(int, int)>();
        for (int i = -r; i <= r; i++) { ring.Add((i, -r)); ring.Add((i, r)); }
        for (int i = -r + 1; i <= r - 1; i++) { ring.Add((-r, i)); ring.Add((r, i)); }
        foreach (var p in ring.OrderBy(p => p.Item1 * p.Item1 + p.Item2 * p.Item2).ThenBy(p => p.Item1).ThenBy(p => p.Item2)) yield return p;
    }

    /// <summary>The level a footprint's ground is made (the median of the land under it, no lower than the beach), and
    /// whether it stands on land: most of it above the sea, and no more of it under water than a harbour's edge.</summary>
    static (float Ground, bool Land) GroundOf(HeightField f, int cellX, int cellY, int w, int h)
    {
        var heights = new List<float>();
        int vx0 = (cellX - f.MinCellX) * HeightField.QuadsPerCell, vy0 = (cellY - f.MinCellY) * HeightField.QuadsPerCell;
        if (vx0 < 0 || vy0 < 0 || vx0 + w * HeightField.QuadsPerCell >= f.VertsX || vy0 + h * HeightField.QuadsPerCell >= f.VertsY) return (MinGround, false);
        for (int y = 0; y <= h * HeightField.QuadsPerCell; y += 8)
            for (int x = 0; x <= w * HeightField.QuadsPerCell; x += 8)
                heights.Add(f.At(vx0 + x, vy0 + y));
        heights.Sort();
        float median = heights[heights.Count / 2];
        int wet = heights.Count(v => v < 0);
        return (MathF.Max(median, MinGround), median > 0 && wet <= heights.Count / 4);
    }

    /// <summary>
    /// The land under every footprint made its ground level (<see cref="Sink"/> under it inside, on it at the edge),
    /// easing back to the land round it over <see cref="BlendOf"/> (smoothly, as Daggerfall Unity blends a location's
    /// flattened ground into its terrain), each site's measured on the land as made, before any levelling.
    /// <para>In two passes: first every site's blend round it (all sites at once), touching no vertex in or on the edge of
    /// any footprint, then every footprint set. Done a site at a time, the blend of a site levelled later reached into the footprint of a bigger
    /// one next to it (sites stand a <see cref="Gap"/> cell apart, blends reach two) and pulled its edge towards the other's
    /// level (2026-11-02, measured on the built land: Sentinel's west edge 1,307 units over its ground, burying buildings,
    /// its east edge 1,821 under, the town floating; 1,170 of 15,251 sites off by over 100 units at their edge).</para>
    /// </summary>
    public void Level(HeightField f)
    {
        if (f.Heights is not { } heights) throw new InvalidOperationException("only a made height field can be levelled");
        int q = HeightField.QuadsPerCell;
        float unit = HeightField.UnitsPerQuad;
        var reach = Sites.Select(s => BlendOf(f, s)).ToArray();
        // Which cells of the field a footprint covers: a vertex is in or on the edge of one when any cell it is a corner
        // of is covered.
        var covered = new bool[f.CellsX * f.CellsY];
        foreach (var s in Sites)
            for (int cy = s.CellY; cy < s.CellY + s.CellsH; cy++)
                for (int cx = s.CellX; cx < s.CellX + s.CellsW; cx++)
                {
                    int lx = cx - f.MinCellX, ly = cy - f.MinCellY;
                    if (lx >= 0 && ly >= 0 && lx < f.CellsX && ly < f.CellsY) covered[lx + ly * f.CellsX] = true;
                }
        bool InFootprint(int vx, int vy)
        {
            int cx = vx / q, cy = vy / q;
            for (int oy = (vy % q == 0 ? -1 : 0); oy <= 0; oy++)
                for (int ox = (vx % q == 0 ? -1 : 0); ox <= 0; ox++)
                {
                    int x = cx + ox, y = cy + oy;
                    if (x >= 0 && y >= 0 && x < f.CellsX && y < f.CellsY && covered[x + y * f.CellsX]) return true;
                }
            return false;
        }
        // Every vertex every site's easing reaches takes all of them at once: how much it is levelled is the most any of
        // them levels it (1 on a footprint's edge, 0 where its easing ends), and the level it goes to is theirs weighted by
        // that and by nearness (1 / distance squared), so beside a footprint its own level holds and between two sites the
        // land runs from one level to the other. One after another, as until 2026-11-03, a site's easing ran up to the edge
        // of the one beside it (a cell apart) and that edge was then set back to its own level, a cliff a step wide
        // (measured, `locations --layout --embank`: 721 towns with a step over 30 degrees beside them, against 35 on the
        // land as made). In bands of rows, so the sums take a few hundred megabytes, not the whole field's.
        var rects = new (int X0, int Y0, int X1, int Y1, int Blend)[Sites.Count];
        for (int k = 0; k < Sites.Count; k++)
        {
            var s = Sites[k];
            int x0 = (s.CellX - f.MinCellX) * q, y0 = (s.CellY - f.MinCellY) * q;
            rects[k] = (x0, y0, x0 + s.CellsW * q, y0 + s.CellsH * q, (int)MathF.Ceiling(reach[k] / unit));
        }
        const int BandRows = 512;
        int width = f.VertsX;
        var sumW = new float[BandRows * width];
        var sumWG = new float[BandRows * width];
        var most = new float[BandRows * width];
        for (int band = 0; band < f.VertsY; band += BandRows)
        {
            int last = Math.Min(f.VertsY - 1, band + BandRows - 1);
            Array.Clear(sumW); Array.Clear(sumWG); Array.Clear(most);
            bool touched = false;
            for (int k = 0; k < Sites.Count; k++)
            {
                var (x0, y0, x1, y1, blend) = rects[k];
                int top = Math.Max(Math.Max(0, y0 - blend), band), bottom = Math.Min(Math.Min(f.VertsY - 1, y1 + blend), last);
                if (top > bottom) continue;
                float ground = Sites[k].Ground, sReach = reach[k];
                for (int vy = top; vy <= bottom; vy++)
                    for (int vx = Math.Max(0, x0 - blend); vx <= Math.Min(f.VertsX - 1, x1 + blend); vx++)
                    {
                        if (InFootprint(vx, vy)) continue;
                        float dx = Math.Max(Math.Max(x0 - vx, vx - x1), 0), dy = Math.Max(Math.Max(y0 - vy, vy - y1), 0);
                        float d = MathF.Sqrt(dx * dx + dy * dy) * unit;
                        if (d >= sReach) continue;
                        float t = d / sReach;
                        float level = 1 - t * t * (3 - 2 * t);   // 1 on the footprint, 0 at the easing's end
                        float near = MathF.Max(d, unit);
                        float w = level / (near * near);
                        int i = (vy - band) * width + vx;
                        sumW[i] += w;
                        sumWG[i] += w * ground;
                        if (level > most[i]) most[i] = level;
                        touched = true;
                    }
            }
            if (!touched) continue;
            for (int vy = band; vy <= last; vy++)
                for (int vx = 0; vx < width; vx++)
                {
                    int i = (vy - band) * width + vx;
                    if (most[i] <= 0) continue;
                    long at = (long)vy * width + vx;
                    float land = heights[at];
                    heights[at] = HeightField.Store(land + most[i] * (sumWG[i] / sumW[i] - land));
                }
        }
        // The footprints: inside under the ground level, the edge on it.
        foreach (var s in Sites)
        {
            int x0 = (s.CellX - f.MinCellX) * q, y0 = (s.CellY - f.MinCellY) * q, x1 = x0 + s.CellsW * q, y1 = y0 + s.CellsH * q;
            for (int vy = Math.Max(0, y0); vy <= Math.Min(f.VertsY - 1, y1); vy++)
                for (int vx = Math.Max(0, x0); vx <= Math.Min(f.VertsX - 1, x1); vx++)
                    heights[(long)vy * f.VertsX + vx] = HeightField.Store(vx > x0 && vx < x1 && vy > y0 && vy < y1 ? s.Ground - Sink : s.Ground);
        }
    }

    /// <summary>Whether global cell (<paramref name="gx"/>, <paramref name="gy"/>) is in a footprint.</summary>
    public bool Covers(int gx, int gy) => Cells().Contains((gx, gy));

    /// <summary>Whether a vertex of <paramref name="f"/>'s grid lies in a footprint or on its edge (any cell it is a
    /// corner of is covered).</summary>
    public bool CoversVertex(HeightField f, int vx, int vy)
    {
        int q = HeightField.QuadsPerCell;
        int cx = Math.DivRem(vx, q, out int rx) + f.MinCellX, cy = Math.DivRem(vy, q, out int ry) + f.MinCellY;
        if (Covers(cx, cy)) return true;
        return (rx == 0 && Covers(cx - 1, cy)) || (ry == 0 && Covers(cx, cy - 1)) || (rx == 0 && ry == 0 && Covers(cx - 1, cy - 1));
    }

    HashSet<(int, int)>? cells;
    public HashSet<(int, int)> Cells()
    {
        if (cells is not null) return cells;
        var set = new HashSet<(int, int)>();
        foreach (var s in Sites)
            for (int y = s.CellY; y < s.CellY + s.CellsH; y++)
                for (int x = s.CellX; x < s.CellX + s.CellsW; x++) set.Add((x, y));
        return cells = set;
    }

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this));

    public static LocationLayout Load(string path) => JsonSerializer.Deserialize<LocationLayout>(File.ReadAllText(path))
        ?? throw new InvalidDataException($"{path} is not a location layout");

    /// <summary>The layout beside a Daggerfall.esm (in <paramref name="dir"/>), or null when it has none.</summary>
    public static LocationLayout? In(string dir) => File.Exists(Path.Combine(dir, FileName)) ? Load(Path.Combine(dir, FileName)) : null;
}
