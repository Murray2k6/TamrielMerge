using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Navmesh;

/// <summary>
/// An exterior cell's navmesh input as wound (the land, wound up, and the placed objects' collision), for rays: what
/// lies above, beside or under a point and which way it faces. Held flat (nine floats a triangle) and binned by x and
/// y in index arrays: 64-unit columns for rays up and faces under a point, 256-unit bins for level rays. Flat, so a
/// cell's million triangles are a few large arrays, not a million small ones kept through Recast's build (2026-10-19:
/// kept as arrays and lists, Morrowind's 5,810 exterior cells ran the PC out of memory, 11.3 GB private for 3.5 GB live
/// at 645 cells).
/// </summary>
public sealed class WoundInput
{
    const float Bin = 256, Column = 64;
    static readonly Vector3[] Level = [Vector3.UnitX, -Vector3.UnitX, Vector3.UnitY, -Vector3.UnitY];

    readonly float[] p;
    readonly Grid columns, bins;

    /// <summary>For <see cref="Cast"/>: each triangle's last ray (one thread uses an instance: the cell it is for).</summary>
    int[]? seen;
    int stamp;

    /// <summary>Rays cast and triangles tested by every instance (for the build's log).</summary>
    public static long Rays => Interlocked.Read(ref rays);
    public static long Tests => Interlocked.Read(ref tests);
    static long rays, tests;
    long myRays, myTests;

    /// <summary>Adds this instance's counts to the totals.</summary>
    public void Count() { Interlocked.Add(ref rays, myRays); Interlocked.Add(ref tests, myTests); myRays = myTests = 0; }

    /// <summary>The triangles' corners, nine floats a triangle (x, y, z of each corner in turn).</summary>
    public ReadOnlySpan<float> Coordinates => p;

    /// <summary>A grid of cells <paramref name="size"/> units a side over the triangles' extent, each listing the
    /// triangles whose box reaches into it (start and count in Items).</summary>
    sealed class Grid
    {
        public readonly float Size, X0, Y0;
        public readonly int W, H;
        public readonly int[] Start, Items;

        public Grid(float[] p, int n, float size, float x0, float y0, float x1, float y1)
        {
            Size = size; X0 = MathF.Floor(x0 / size) * size; Y0 = MathF.Floor(y0 / size) * size;
            W = Math.Max(1, (int)MathF.Floor((x1 - X0) / size) + 1); H = Math.Max(1, (int)MathF.Floor((y1 - Y0) / size) + 1);
            Start = new int[W * H + 1];
            // Counted, then filled: each triangle into the cells its box covers.
            for (int pass = 0; pass < 2; pass++)
            {
                var at = pass == 1 ? (int[])Start.Clone() : null;
                for (int i = 0; i < n; i++)
                {
                    var (cx0, cy0, cx1, cy1) = Box(p, i);
                    for (int cy = cy0; cy <= cy1; cy++)
                        for (int cx = cx0; cx <= cx1; cx++)
                        {
                            int c = cx + cy * W;
                            if (pass == 0) Start[c + 1]++; else Items![at![c]++] = i;
                        }
                }
                if (pass == 0)
                {
                    for (int c = 0; c < W * H; c++) Start[c + 1] += Start[c];
                    Items = new int[Start[W * H]];
                }
            }
            Items ??= [];
        }

        (int, int, int, int) Box(float[] p, int i)
        {
            int o = i * 9;
            float minX = MathF.Min(p[o], MathF.Min(p[o + 3], p[o + 6])), maxX = MathF.Max(p[o], MathF.Max(p[o + 3], p[o + 6]));
            float minY = MathF.Min(p[o + 1], MathF.Min(p[o + 4], p[o + 7])), maxY = MathF.Max(p[o + 1], MathF.Max(p[o + 4], p[o + 7]));
            return (Math.Clamp((int)MathF.Floor((minX - X0) / Size), 0, W - 1), Math.Clamp((int)MathF.Floor((minY - Y0) / Size), 0, H - 1),
                    Math.Clamp((int)MathF.Floor((maxX - X0) / Size), 0, W - 1), Math.Clamp((int)MathF.Floor((maxY - Y0) / Size), 0, H - 1));
        }

        /// <summary>The cell holding a point, or -1 outside the grid.</summary>
        public int At(float x, float y)
        {
            int cx = (int)MathF.Floor((x - X0) / Size), cy = (int)MathF.Floor((y - Y0) / Size);
            return cx < 0 || cy < 0 || cx >= W || cy >= H ? -1 : cx + cy * W;
        }
    }

    /// <summary>From the triangles' corners, nine floats a triangle.</summary>
    public WoundInput(float[] coordinates)
    {
        p = coordinates;
        int n = p.Length / 9;
        float x0 = float.MaxValue, y0 = float.MaxValue, x1 = float.MinValue, y1 = float.MinValue;
        for (int k = 0; k < n * 3; k++)
        {
            x0 = MathF.Min(x0, p[k * 3]); x1 = MathF.Max(x1, p[k * 3]);
            y0 = MathF.Min(y0, p[k * 3 + 1]); y1 = MathF.Max(y1, p[k * 3 + 1]);
        }
        if (n == 0) { x0 = y0 = x1 = y1 = 0; }
        columns = new Grid(p, n, Column, x0, y0, x1, y1);
        bins = new Grid(p, n, Bin, x0, y0, x1, y1);
    }

    /// <summary>From triangles (three corners each).</summary>
    public WoundInput(List<Vector3[]> triangles) : this(Flatten(triangles)) { }

    static float[] Flatten(List<Vector3[]> triangles)
    {
        var f = new float[triangles.Count * 9];
        for (int i = 0; i < triangles.Count; i++)
            for (int k = 0; k < 3; k++) { f[i * 9 + k * 3] = triangles[i][k].X; f[i * 9 + k * 3 + 1] = triangles[i][k].Y; f[i * 9 + k * 3 + 2] = triangles[i][k].Z; }
        return f;
    }

    Vector3 Corner(int i, int k) => new(p[i * 9 + k * 3], p[i * 9 + k * 3 + 1], p[i * 9 + k * 3 + 2]);

    /// <summary>The nearest face a ray from <paramref name="o"/> along <paramref name="d"/> (straight up, or level along
    /// x or y) crosses within <paramref name="reach"/>: how far, and whether it is met from behind (its wound normal
    /// along the ray).</summary>
    public (float T, bool Back)? Cast(Vector3 o, Vector3 d, float reach)
    {
        (float T, bool Back)? best = null;
        myRays++;
        if (d.Z != 0)
        {
            int c = columns.At(o.X, o.Y);
            if (c >= 0) for (int j = columns.Start[c]; j < columns.Start[c + 1]; j++) Test(columns.Items[j]);
            return best;
        }
        // A triangle met in an earlier bin of this ray is not tested again: stamped with the ray's number.
        seen ??= new int[p.Length / 9];
        if (++stamp == int.MaxValue) { Array.Clear(seen); stamp = 1; }
        int steps = (int)(reach / Bin) + 1;
        for (int s = 0; s <= steps; s++)
        {
            var at = o + d * (s * Bin);
            int c = bins.At(at.X, at.Y);
            if (c >= 0)
                for (int j = bins.Start[c]; j < bins.Start[c + 1]; j++)
                {
                    int i = bins.Items[j];
                    if (seen[i] == stamp) continue;
                    seen[i] = stamp;
                    Test(i);
                }
            // The bins not yet looked in all lie further along than s bins.
            if (best is { } found && found.T <= s * Bin) break;
        }
        return best;

        void Test(int i)
        {
            // Not across the ray's line: its extent on the two axes the ray doesn't run along (2026-10-19: without
            // this, the shut-in test tested every face of a 256-unit column at every height, and Morrowind's
            // exteriors took over half an hour of 16 cores and weren't done).
            int k = i * 9;
            myTests++;
            if (d.Z == 0 && (o.Z < MathF.Min(p[k + 2], MathF.Min(p[k + 5], p[k + 8])) || o.Z > MathF.Max(p[k + 2], MathF.Max(p[k + 5], p[k + 8])))) return;
            if (d.X == 0 && (o.X < MathF.Min(p[k], MathF.Min(p[k + 3], p[k + 6])) || o.X > MathF.Max(p[k], MathF.Max(p[k + 3], p[k + 6])))) return;
            if (d.Y == 0 && (o.Y < MathF.Min(p[k + 1], MathF.Min(p[k + 4], p[k + 7])) || o.Y > MathF.Max(p[k + 1], MathF.Max(p[k + 4], p[k + 7])))) return;
            if (d.Z > 0 && o.Z > MathF.Max(p[k + 2], MathF.Max(p[k + 5], p[k + 8]))) return;
            var t0 = Corner(i, 0);
            var e1 = Corner(i, 1) - t0; var e2 = Corner(i, 2) - t0;
            var q = Vector3.Cross(d, e2);
            float det = Vector3.Dot(e1, q);
            if (MathF.Abs(det) < 1e-6f) return;
            var r = o - t0;
            float u = Vector3.Dot(r, q) / det;
            if (u < 0 || u > 1) return;
            var rr = Vector3.Cross(r, e1);
            float v = Vector3.Dot(d, rr) / det;
            if (v < 0 || u + v > 1) return;
            float dist = Vector3.Dot(e2, rr) / det;
            if (dist <= 0.01f || dist > reach || (best is { } b && dist >= b.T)) return;
            best = (dist, Vector3.Dot(Vector3.Cross(e1, e2), d) > 0);
        }
    }

    /// <summary>Whether a point on a floor is shut inside something: from 64 units over it, the ray up and the four
    /// level ones each meet a face from behind within 4096 units.</summary>
    public bool Shut(Vector3 at)
    {
        var o = at + new Vector3(0, 0, 64);
        if (Cast(o, Vector3.UnitZ, 4096) is not { Back: true }) return false;
        foreach (var d in Level)
            if (Cast(o, d, 4096) is not { Back: true }) return false;
        return true;
    }

    /// <summary>The highest face under a point, from <paramref name="below"/> under it to <paramref name="above"/> over
    /// it: its wound normal's z (null when there is none).</summary>
    public float? Under(Vector3 at, float below, float above)
    {
        int c = columns.At(at.X, at.Y);
        if (c < 0) return null;
        float bestZ = float.MinValue; float? nz = null;
        for (int j = columns.Start[c]; j < columns.Start[c + 1]; j++)
        {
            int i = columns.Items[j];
            var a0 = Corner(i, 0); var a1 = Corner(i, 1); var a2 = Corner(i, 2);
            float det = (a1.Y - a2.Y) * (a0.X - a2.X) + (a2.X - a1.X) * (a0.Y - a2.Y);
            if (MathF.Abs(det) < 1e-6f) continue;
            float a = ((a1.Y - a2.Y) * (at.X - a2.X) + (a2.X - a1.X) * (at.Y - a2.Y)) / det, b = ((a2.Y - a0.Y) * (at.X - a2.X) + (a0.X - a2.X) * (at.Y - a2.Y)) / det;
            if (a < -1e-4f || b < -1e-4f || a + b > 1 + 1e-4f) continue;
            float z = a * a0.Z + b * a1.Z + (1 - a - b) * a2.Z;
            if (z < at.Z - below || z > at.Z + above || z <= bestZ) continue;
            bestZ = z;
            nz = Vector3.Cross(a1 - a0, a2 - a0).Z;
        }
        return nz;
    }
}
