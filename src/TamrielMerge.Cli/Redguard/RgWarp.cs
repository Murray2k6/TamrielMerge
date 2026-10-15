using System.Numerics;

namespace TamrielMerge.Redguard;

/// <summary>
/// A Redguard world made bigger (user, 2026-10-09: "make the island bigger since its an old game", "i was afraid i might
/// walk off the island with one step"; chose "stretch the wilds"): Redguard's buildings, doors and furniture already
/// match Skyrim's scale (a door 186 tall against Skyrim's 178, a table 60 against 63, people about 130 against 128), so
/// what stands is kept at true size and only the land between is stretched.
/// Rigid pieces (buildings, walls, docks, bridges, gates, boats: anything but scenery that is over 200 units across or
/// 300 tall) that touch make a settlement; a settlement is the convex shell round its pieces (its streets and yards held
/// too, and a convex shape can't fold the ground round it), shells that touch are one settlement. Each settlement moves
/// as a whole by (Stretch - 1) x (its centre - the island's centre); the open land is spread by Stretch about the
/// island's centre; between, the two are blended over a ring twice the settlement's size (Shepard weights, infinite at
/// a settlement's edge, so each stays exactly rigid where rings overlap). Heights are kept (slopes in the wilds get
/// gentler). Free things (palms, plants, rocks, small props, people, lights, markers) move with the ground under them.
/// The map is piecewise linear over a 64-unit grid in Skyrim's horizontal coordinates (before stretching), so objects
/// (<see cref="Forward"/>) and the land sampled back through it (<see cref="Inverse"/>) agree exactly.
/// </summary>
public sealed class RgWarp
{
    /// <summary>How far the open land is spread, each way: 3 since 2026-10-15 (user: "redguards esm converts really
    /// small"; chose stretch 3x): Stros M'Kai about 1.6 x 1.7 km (it was 2, about 1 x 1.1 km; Redguard's own 620 x 600 m).</summary>
    public const float Stretch = 3f;
    const float Grid = 128, Extent = 32768, Margin = 192, Touch = 256;
    /// <summary>Settlements nearer than this share of their radii together are one block (see Build).</summary>
    public static float Close = float.TryParse(Environment.GetEnvironmentVariable("TAMRIELMERGE_WARP_CLOSE"), System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0.35f;
    const int N = (int)(2 * Extent / Grid) + 1;

    public static readonly RgWarp None = new(null, Vector2.Zero, 1);

    readonly Vector2[,]? map;   // where each grid node goes (null: no change)
    readonly Vector2 centre;
    readonly float s;
    Dictionary<(int, int), List<int>>? buckets;   // target-space buckets of grid triangles, for Inverse
    const float Bucket = 512;

    RgWarp(Vector2[,]? map, Vector2 centre, float s) { this.map = map; this.centre = centre; this.s = s; }

    public bool IsNone => map is null;

    /// <summary>A Skyrim position (from <see cref="RgSpace.Point"/>) in the stretched world; height unchanged.</summary>
    public Vector3 Forward(Vector3 p) { var q = Forward(new Vector2(p.X, p.Y)); return new Vector3(q.X, q.Y, p.Z); }

    public Vector2 Forward(Vector2 p)
    {
        if (map is null) return p;
        float gx = (p.X + Extent) / Grid, gy = (p.Y + Extent) / Grid;
        if (gx < 0 || gy < 0 || gx >= N - 1 || gy >= N - 1) return centre + (p - centre) * s;
        int x = (int)gx, y = (int)gy;
        float fx = gx - x, fy = gy - y;
        // Two triangles per grid square: (x,y) (x+1,y) (x+1,y+1) and (x,y) (x+1,y+1) (x,y+1).
        return fx >= fy
            ? map[x, y] + (map[x + 1, y] - map[x, y]) * fx + (map[x + 1, y + 1] - map[x + 1, y]) * fy
            : map[x, y] + (map[x + 1, y + 1] - map[x, y + 1]) * fx + (map[x, y + 1] - map[x, y]) * fy;
    }

    /// <summary>The unstretched position that <see cref="Forward"/> takes to <paramref name="q"/>.</summary>
    public Vector2 Inverse(Vector2 q)
    {
        if (map is null) return q;
        buckets ??= BuildBuckets();
        if (buckets.TryGetValue(((int)MathF.Floor(q.X / Bucket), (int)MathF.Floor(q.Y / Bucket)), out var tris))
            foreach (int t in tris)
            {
                var (a, b, c, pa, pb, pc) = Triangle(t);
                float det = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
                if (MathF.Abs(det) < 1e-6f) continue;
                float u = ((q.X - a.X) * (c.Y - a.Y) - (q.Y - a.Y) * (c.X - a.X)) / det;
                float v = ((b.X - a.X) * (q.Y - a.Y) - (b.Y - a.Y) * (q.X - a.X)) / det;
                if (u >= -1e-4f && v >= -1e-4f && u + v <= 1 + 1e-4f) return pa + (pb - pa) * u + (pc - pa) * v;
            }
        return centre + (q - centre) / s;   // beyond the grid: the open sea, stretched evenly
    }

    (Vector2 A, Vector2 B, Vector2 C, Vector2 Pa, Vector2 Pb, Vector2 Pc) Triangle(int t)
    {
        int cellIndex = t >> 1, x = cellIndex % (N - 1), y = cellIndex / (N - 1);
        Vector2 P(int i, int j) => new(i * Grid - Extent, j * Grid - Extent);
        return (t & 1) == 0
            ? (map![x, y], map[x + 1, y], map[x + 1, y + 1], P(x, y), P(x + 1, y), P(x + 1, y + 1))
            : (map![x, y], map[x + 1, y + 1], map[x, y + 1], P(x, y), P(x + 1, y + 1), P(x, y + 1));
    }

    Dictionary<(int, int), List<int>> BuildBuckets()
    {
        var b = new Dictionary<(int, int), List<int>>();
        for (int t = 0; t < (N - 1) * (N - 1) * 2; t++)
        {
            var (p0, p1, p2, _, _, _) = Triangle(t);
            int x0 = (int)MathF.Floor(MathF.Min(p0.X, MathF.Min(p1.X, p2.X)) / Bucket), x1 = (int)MathF.Floor(MathF.Max(p0.X, MathF.Max(p1.X, p2.X)) / Bucket);
            int y0 = (int)MathF.Floor(MathF.Min(p0.Y, MathF.Min(p1.Y, p2.Y)) / Bucket), y1 = (int)MathF.Floor(MathF.Max(p0.Y, MathF.Max(p1.Y, p2.Y)) / Bucket);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if (!b.TryGetValue((x, y), out var list)) b[(x, y)] = list = [];
                    list.Add(t);
                }
        }
        return b;
    }

    /// <summary>The stretched area's bounds (Skyrim units) for the whole unstretched world.</summary>
    public (Vector2 Min, Vector2 Max) Bounds()
    {
        if (map is null) return (new Vector2(-Extent), new Vector2(Extent));
        Vector2 mn = new(float.MaxValue), mx = new(float.MinValue);
        foreach (var v in map) { mn = Vector2.Min(mn, v); mx = Vector2.Max(mx, v); }
        return (mn, mx);
    }

    sealed class Settlement
    {
        public List<Vector2> Points = [];   // corners of its pieces' cells
        public Vector2[] Hull = [];
        public Vector2 Centre;
        public float Radius, Ring;
        public Vector2 Min, Max;

        public void Shape()
        {
            Hull = ConvexHull(Points);
            Centre = Hull.Aggregate(Vector2.Zero, (a, p) => a + p) / Hull.Length;
            var c = Centre;
            Radius = Hull.Max(p => Vector2.Distance(p, c));
            Ring = Math.Clamp(2 * Radius, 1536f, 16384f);
            Min = Hull.Aggregate(Vector2.Min); Max = Hull.Aggregate(Vector2.Max);
        }

        public bool Contains(Vector2 p)
        {
            for (int i = 0; i < Hull.Length; i++)
            {
                var a = Hull[i]; var b = Hull[(i + 1) % Hull.Length];
                if ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X) < 0) return false;
            }
            return true;
        }

        /// <summary>Distance from the shell (0 inside).</summary>
        public float Distance(Vector2 p)
        {
            if (Contains(p)) return 0;
            float best = float.MaxValue;
            for (int i = 0; i < Hull.Length; i++)
            {
                var a = Hull[i]; var b = Hull[(i + 1) % Hull.Length];
                var ab = b - a;
                float t = ab.LengthSquared() > 0 ? Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
                best = MathF.Min(best, Vector2.Distance(p, a + ab * t));
            }
            return best;
        }
    }

    static Vector2[] ConvexHull(List<Vector2> pts)
    {
        var p = pts.Distinct().OrderBy(v => v.X).ThenBy(v => v.Y).ToList();
        if (p.Count < 3) return p.ToArray();
        static float Cross(Vector2 o, Vector2 a, Vector2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new Vector2[2 * p.Count];
        int k = 0;
        for (int i = 0; i < p.Count; i++) { while (k >= 2 && Cross(hull[k - 2], hull[k - 1], p[i]) <= 0) k--; hull[k++] = p[i]; }
        for (int i = p.Count - 2, t = k + 1; i >= 0; i--) { while (k >= t && Cross(hull[k - 2], hull[k - 1], p[i]) <= 0) k--; hull[k++] = p[i]; }
        return hull[..(k - 1)];   // counter-clockwise
    }

    /// <summary>
    /// The stretch for a world: <paramref name="rigid"/> = footprints (Skyrim horizontal bounds) of what must keep its
    /// shape, <paramref name="land"/> = points of land above water (the island's centre is theirs).
    /// </summary>
    public static RgWarp Build(IReadOnlyList<(Vector2 Min, Vector2 Max)> rigid, IReadOnlyList<Vector2> land, float stretch, Action<string>? log = null, string name = "")
    {
        if (land.Count == 0 || stretch <= 1) return None;
        var centre = land.Aggregate(Vector2.Zero, (a, p) => a + p) / land.Count;
        int cells = N - 1;
        int Cell(float v) => Math.Clamp((int)MathF.Floor((v + Extent) / Grid), 0, cells - 1);

        // Pieces that touch (footprints with a margin, 8-connected) make a settlement.
        var zone = new bool[cells, cells];
        foreach (var (mn, mx) in rigid)
            for (int y = Cell(mn.Y - Margin); y <= Cell(mx.Y + Margin); y++)
                for (int x = Cell(mn.X - Margin); x <= Cell(mx.X + Margin); x++)
                    zone[x, y] = true;
        var seen = new bool[cells, cells];
        var settlements = new List<Settlement>();
        for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
            {
                if (!zone[x, y] || seen[x, y]) continue;
                var st = new Settlement();
                var stack = new Stack<(int, int)>([(x, y)]);
                seen[x, y] = true;
                while (stack.Count > 0)
                {
                    var (cx, cy) = stack.Pop();
                    float x0 = cx * Grid - Extent, y0 = cy * Grid - Extent;
                    st.Points.AddRange([new(x0, y0), new(x0 + Grid, y0), new(x0, y0 + Grid), new(x0 + Grid, y0 + Grid)]);
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = cx + dx, ny = cy + dy;
                            if (nx < 0 || ny < 0 || nx >= cells || ny >= cells || !zone[nx, ny] || seen[nx, ny]) continue;
                            seen[nx, ny] = true;
                            stack.Push((nx, ny));
                        }
                }
                st.Shape();
                settlements.Add(st);
            }
        // Shells that touch are one settlement (repeat: a merged shell is bigger).
        for (bool merged = true; merged;)
        {
            merged = false;
            for (int a = 0; a < settlements.Count && !merged; a++)
                for (int b = a + 1; b < settlements.Count && !merged; b++)
                {
                    var (sa, sb) = (settlements[a], settlements[b]);
                    // Touching, or so close for their size that the ground between (a street, a square) would be
                    // smeared out rather than spread: one block.
                    float near = MathF.Max(Touch, Close * MathF.Min(sa.Radius, sb.Radius));
                    if (sa.Min.X - near > sb.Max.X || sb.Min.X - near > sa.Max.X || sa.Min.Y - near > sb.Max.Y || sb.Min.Y - near > sa.Max.Y) continue;
                    if (!sb.Hull.Any(p => sa.Distance(p) < near) && !sa.Hull.Any(p => sb.Distance(p) < near) && !EdgesCross(sa.Hull, sb.Hull)) continue;
                    sa.Points.AddRange(sb.Hull);
                    sa.Points = sa.Points.Count > 4096 ? ConvexHull(sa.Points).ToList() : sa.Points;
                    sa.Shape();
                    settlements.RemoveAt(b);
                    merged = true;
                }
        }

        // Where each grid node goes: the least-squares sheet. Every grid edge in the open wants to be Stretch times its
        // length (and keep its direction); every settlement is one piece that may only shift (its nodes share one
        // unknown offset); the outer edge of the world (open sea) is pinned at Stretch about the island's centre. The two
        // axes solve apart (conjugate gradients, Jacobi-preconditioned).
        int[,] owner = new int[N, N];   // settlement index + 1 holding the node, 0 = open
        for (int k = 0; k < settlements.Count; k++)
        {
            var st = settlements[k];
            int x0 = Math.Max(0, (int)MathF.Floor((st.Min.X + Extent) / Grid)), x1 = Math.Min(N - 1, (int)MathF.Ceiling((st.Max.X + Extent) / Grid));
            int y0 = Math.Max(0, (int)MathF.Floor((st.Min.Y + Extent) / Grid)), y1 = Math.Min(N - 1, (int)MathF.Ceiling((st.Max.Y + Extent) / Grid));
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    if (owner[x, y] == 0 && st.Contains(new Vector2(x * Grid - Extent, y * Grid - Extent))) owner[x, y] = k + 1;
        }
        int open = 0;
        var index = new int[N, N];
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                index[x, y] = owner[x, y] == 0 ? open++ : -1;
        int unknowns = open + settlements.Count;
        for (int y = 0; y < N; y++)
            for (int x = 0; x < N; x++)
                if (owner[x, y] > 0) index[x, y] = open + owner[x, y] - 1;
        bool Edge(int x, int y) => x == 0 || y == 0 || x == N - 1 || y == N - 1;
        var map = new Vector2[N, N];
        for (int axis = 0; axis < 2; axis++)
        {
            // Node value = base + z[index]: base is the node's own coordinate inside a settlement (z = its offset), 0 in
            // the open (z = the coordinate itself).
            float Coord(int x, int y) => axis == 0 ? x * Grid - Extent : y * Grid - Extent;
            float Base(int x, int y) => owner[x, y] > 0 ? Coord(x, y) : 0;
            float Target(int x, int y) => (axis == 0 ? centre.X : centre.Y) + stretch * (Coord(x, y) - (axis == 0 ? centre.X : centre.Y));
            const float pin = 4;
            // Right-hand side and diagonal.
            var rhs = new double[unknowns];
            var diag = new double[unknowns];
            void EdgeTerm(int xa, int ya, int xb, int yb, bool along)
            {
                int ia = index[xa, ya], ib = index[xb, yb];
                if (ia == ib) return;   // inside one settlement: rigid already
                float want = along ? stretch * Grid : 0;   // b - a wanted along this axis
                float r = want - (Base(xb, yb) - Base(xa, ya));
                rhs[ib] += r; rhs[ia] -= r;
                diag[ia] += 1; diag[ib] += 1;
            }
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    if (x + 1 < N) EdgeTerm(x, y, x + 1, y, axis == 0);
                    if (y + 1 < N) EdgeTerm(x, y, x, y + 1, axis == 1);
                    if (Edge(x, y) && owner[x, y] == 0) { rhs[index[x, y]] += pin * Target(x, y); diag[index[x, y]] += pin; }
                }
            void Apply(double[] z, double[] outv)
            {
                Array.Clear(outv);
                for (int y = 0; y < N; y++)
                    for (int x = 0; x < N; x++)
                    {
                        int i = index[x, y];
                        if (x + 1 < N) { int j = index[x + 1, y]; if (i != j) { double d = z[j] - z[i]; outv[j] += d; outv[i] -= d; } }
                        if (y + 1 < N) { int j = index[x, y + 1]; if (i != j) { double d = z[j] - z[i]; outv[j] += d; outv[i] -= d; } }
                        if (Edge(x, y) && owner[x, y] == 0) outv[i] += pin * z[i];
                    }
            }
            // Start from the plain stretch.
            var z = new double[unknowns];
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                    if (owner[x, y] == 0) z[index[x, y]] = Target(x, y);
            for (int k = 0; k < settlements.Count; k++)
                z[open + k] = (stretch - 1) * ((axis == 0 ? settlements[k].Centre.X : settlements[k].Centre.Y) - (axis == 0 ? centre.X : centre.Y));
            var r0 = new double[unknowns]; var pv = new double[unknowns]; var ap = new double[unknowns]; var zp = new double[unknowns];
            Apply(z, ap);
            for (int i = 0; i < unknowns; i++) r0[i] = rhs[i] - ap[i];
            for (int i = 0; i < unknowns; i++) { zp[i] = r0[i] / Math.Max(1e-9, diag[i]); pv[i] = zp[i]; }
            double rz = 0; for (int i = 0; i < unknowns; i++) rz += r0[i] * zp[i];
            double norm0 = Math.Sqrt(r0.Sum(v => v * v));
            int it = 0;
            for (; it < 6000; it++)
            {
                Apply(pv, ap);
                double pap = 0; for (int i = 0; i < unknowns; i++) pap += pv[i] * ap[i];
                if (pap <= 0) break;
                double alpha = rz / pap;
                double rr = 0;
                for (int i = 0; i < unknowns; i++) { z[i] += alpha * pv[i]; r0[i] -= alpha * ap[i]; rr += r0[i] * r0[i]; }
                if (Math.Sqrt(rr) < 1e-6 * norm0 + 1e-6) break;
                double rzNew = 0;
                for (int i = 0; i < unknowns; i++) { zp[i] = r0[i] / Math.Max(1e-9, diag[i]); rzNew += r0[i] * zp[i]; }
                double beta = rzNew / rz; rz = rzNew;
                for (int i = 0; i < unknowns; i++) pv[i] = zp[i] + beta * pv[i];
            }
            if (axis == 0) log?.Invoke($"sheet solved in {it} steps");
            for (int y = 0; y < N; y++)
                for (int x = 0; x < N; x++)
                {
                    float v = Base(x, y) + (float)z[index[x, y]];
                    map[x, y] = axis == 0 ? new Vector2(v, 0) : new Vector2(map[x, y].X, v);
                }
        }

        var warp = new RgWarp(map, centre, stretch);
        // Folds: every grid triangle must keep its winding.
        int folds = 0;
        float minArea = float.MaxValue, maxArea = 0;
        for (int t = 0; t < cells * cells * 2; t++)
        {
            var (a, b, c, _, _, _) = warp.Triangle(t);
            float area = ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / (Grid * Grid);
            if (area <= 0) folds++;
            minArea = MathF.Min(minArea, area); maxArea = MathF.Max(maxArea, area);
        }
        // How much the land itself is stretched (area), by percentile: the wilds should sit near Stretch^2.
        var landCells = new HashSet<(int, int)>(land.Select(p => (Cell(p.X) / 4, Cell(p.Y) / 4)));
        var landAreas = new List<float>();
        for (int y = 0; y < cells; y++)
            for (int x = 0; x < cells; x++)
            {
                if (!landCells.Contains((x / 4, y / 4))) continue;
                var (ta, tb, tc, _, _, _) = warp.Triangle((y * cells + x) * 2);
                landAreas.Add(((tb.X - ta.X) * (tc.Y - ta.Y) - (tb.Y - ta.Y) * (tc.X - ta.X)) / (Grid * Grid));
            }
        landAreas.Sort();
        string Pct(double q) => landAreas.Count == 0 ? "-" : $"x{landAreas[(int)Math.Min(landAreas.Count - 1, q * landAreas.Count)]:F1}";
        log?.Invoke($"land area stretch: median {Pct(0.5)}, 90% {Pct(0.9)}, 99% {Pct(0.99)}, max {Pct(1)}; {landAreas.Count(a => a > 2 * stretch * stretch) * 100.0 / Math.Max(1, landAreas.Count):F1}% of the land over twice the aim");
        var landMin = land.Aggregate(Vector2.Min); var landMax = land.Aggregate(Vector2.Max);
        var newLand = land.Select(warp.Forward).ToList();
        var nMin = newLand.Aggregate(Vector2.Min); var nMax = newLand.Aggregate(Vector2.Max);
        log?.Invoke($"stretch {stretch}: {settlements.Count} settlements kept whole (largest {string.Join(", ", settlements.OrderByDescending(st => st.Radius).Take(5).Select(st => $"{st.Radius * 2 * 0.01428f:F0} m"))}); " +
            $"land {(landMax.X - landMin.X) * 0.01428f:F0} x {(landMax.Y - landMin.Y) * 0.01428f:F0} m -> {(nMax.X - nMin.X) * 0.01428f:F0} x {(nMax.Y - nMin.Y) * 0.01428f:F0} m; " +
            $"ground area x{minArea:F2}..x{maxArea:F2}, {folds} folds");

        // Dev aid: TAMRIELMERGE_WARP_IMAGE=<file.ppm> draws the stretched world: land grey, settlements coloured.
        if (Environment.GetEnvironmentVariable("TAMRIELMERGE_WARP_IMAGE") is { Length: > 0 } image)
        {
            var (bMin, bMax) = warp.Bounds();
            int size = 1024;
            float scale = MathF.Max(bMax.X - bMin.X, bMax.Y - bMin.Y) / size;
            var landSet = new HashSet<(int, int)>(land.Select(p => (Cell(p.X) / 4, Cell(p.Y) / 4)));
            var img = new byte[size * size * 3];
            for (int py = 0; py < size; py++)
                for (int px = 0; px < size; px++)
                {
                    var q = new Vector2(bMin.X + (px + 0.5f) * scale, bMax.Y - (py + 0.5f) * scale);
                    var p = warp.Inverse(q);
                    int k = settlements.FindIndex(st => st.Contains(p));
                    uint c = k >= 0 ? (uint)((k + 1) * 2654435761u) | 0x404040u : landSet.Contains((Cell(p.X) / 4, Cell(p.Y) / 4)) ? 0xB0B0B0u : 0x203040u;
                    // Grid lines every 4096 units before stretching, to show the stretch.
                    if (MathF.Abs(p.X % 4096) < scale * 0.6f || MathF.Abs(p.Y % 4096) < scale * 0.6f) c = 0x506070u;
                    int o = (py * size + px) * 3;
                    img[o] = (byte)(c >> 16); img[o + 1] = (byte)(c >> 8); img[o + 2] = (byte)c;
                }
            using var f = File.Create(image.Replace(".ppm", $"_{name}.ppm"));
            f.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {size} {size} 255\n"));
            f.Write(img);
        }
        // Dev aid: TAMRIELMERGE_WARP_FOLDS=<file.ppm>: the unstretched world, folded grid squares red.
        if (Environment.GetEnvironmentVariable("TAMRIELMERGE_WARP_FOLDS") is { Length: > 0 } foldImage)
        {
            var landSet = new HashSet<(int, int)>(land.Select(p => (Cell(p.X) / 4, Cell(p.Y) / 4)));
            var img = new byte[cells * cells * 3];
            for (int y = 0; y < cells; y++)
                for (int x = 0; x < cells; x++)
                {
                    var p = new Vector2((x + 0.5f) * Grid - Extent, (y + 0.5f) * Grid - Extent);
                    int k = settlements.FindIndex(st => st.Contains(p));
                    uint c = k >= 0 ? (uint)((k + 1) * 2654435761u) | 0x404040u : landSet.Contains((x / 4, y / 4)) ? 0xB0B0B0u : 0x203040u;
                    for (int h = 0; h < 2; h++)
                    {
                        var (ta, tb, tc, _, _, _) = warp.Triangle((y * cells + x) * 2 + h);
                        if ((tb.X - ta.X) * (tc.Y - ta.Y) - (tb.Y - ta.Y) * (tc.X - ta.X) <= 0) c = 0xFF0000u;
                    }
                    int o = ((cells - 1 - y) * cells + x) * 3;
                    img[o] = (byte)(c >> 16); img[o + 1] = (byte)(c >> 8); img[o + 2] = (byte)c;
                }
            using var f = File.Create(foldImage.Replace(".ppm", $"_{name}.ppm"));
            f.Write(System.Text.Encoding.ASCII.GetBytes($"P6 {cells} {cells} 255\n"));
            f.Write(img);
        }
        if (folds > 0) throw new InvalidOperationException($"the stretched ground folds over itself in {folds} places");
        return warp;
    }

    static bool EdgesCross(Vector2[] a, Vector2[] b)
    {
        static float Cross(Vector2 o, Vector2 p, Vector2 q) => (p.X - o.X) * (q.Y - o.Y) - (p.Y - o.Y) * (q.X - o.X);
        for (int i = 0; i < a.Length; i++)
            for (int j = 0; j < b.Length; j++)
            {
                Vector2 p1 = a[i], p2 = a[(i + 1) % a.Length], q1 = b[j], q2 = b[(j + 1) % b.Length];
                if (Cross(p1, p2, q1) * Cross(p1, p2, q2) < 0 && Cross(q1, q2, p1) * Cross(q1, q2, p2) < 0) return true;
            }
        return false;
    }
}
