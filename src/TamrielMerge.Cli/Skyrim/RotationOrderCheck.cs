using System.Numerics;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Which order a game turns a placed reference by its three angles, from the game's own data: tilted references placed
/// by each candidate order (the six orders of the three turns, X and Y each by the negated angle, as Z is, or by the
/// angle itself), and how near their own collision the points the game's makers put around them (Skyrim's navmesh
/// vertices, Oblivion's pathgrid points) then sit. Those points were made on the real placement, so the right order
/// puts them on the collision. `rot-order-vanilla` (Skyrim), `ob-rot-order` (Oblivion).
/// </summary>
public static class RotationOrderCheck
{
    public sealed record Ref(List<Vector3[]> Collision, Vector3 Position, Vector3 Angles, float Scale, List<Vector3> Points);

    public static readonly IReadOnlyList<(string Name, Func<Vector3, Matrix4x4> Matrix)> Candidates = Build();

    static List<(string, Func<Vector3, Matrix4x4>)> Build()
    {
        var list = new List<(string, Func<Vector3, Matrix4x4>)>();
        foreach (var order in new[] { "XYZ", "XZY", "YXZ", "YZX", "ZXY", "ZYX" })
            foreach (int sign in new[] { -1, 1 })
                list.Add(($"{order} first-to-last, X/Y {(sign < 0 ? "negated" : "as is")}", a =>
                {
                    var m = Matrix4x4.Identity;
                    foreach (char axis in order)
                        m *= axis switch
                        {
                            'X' => Matrix4x4.CreateRotationX(sign * a.X),
                            'Y' => Matrix4x4.CreateRotationY(sign * a.Y),
                            _ => Matrix4x4.CreateRotationZ(-a.Z),
                        };
                    return m;
                }));
        return list;
    }

    /// <summary>The candidates' scores. A point is on the collision within <paramref name="on"/> units; the telling
    /// points are those one order puts on it and another <paramref name="off"/> or more away.</summary>
    public static void Report(string what, List<Ref> refs, float on, float off, int[]? pair)
    {
        int total = refs.Sum(r => r.Points.Count);
        Console.WriteLine($"{what}: {refs.Count} tilted references with collision and points around them, {total} points");
        var dists = new float[Candidates.Count][];
        for (int ci = 0; ci < Candidates.Count; ci++) dists[ci] = new float[total];
        Parallel.For(0, Candidates.Count, ci =>
        {
            int at = 0;
            foreach (var r in refs)
            {
                var m = Candidates[ci].Matrix(r.Angles);
                var world = r.Collision.Select(t => t.Select(v => Vector3.Transform(v * r.Scale, m) + r.Position).ToArray()).ToList();
                foreach (var p in r.Points)
                {
                    float best = float.MaxValue;
                    foreach (var t in world) best = MathF.Min(best, Vector3.Distance(p, Navmesh.NavmeshBuilder.ClosestOnTriangle(p, t[0], t[1], t[2])));
                    dists[ci][at++] = best;
                }
            }
        });
        var telling = new List<int>();
        for (int k = 0; k < total; k++)
        {
            float lo = float.MaxValue, hi = 0;
            for (int ci = 0; ci < Candidates.Count; ci++) { lo = MathF.Min(lo, dists[ci][k]); hi = MathF.Max(hi, dists[ci][k]); }
            if (lo <= on && hi >= off) telling.Add(k);
        }
        Console.WriteLine($"  {telling.Count} points the orders disagree on (within {on} of the collision by one, {off}+ off by another); each order puts on it:");
        foreach (var ci in Enumerable.Range(0, Candidates.Count).OrderByDescending(ci => telling.Count(k => dists[ci][k] <= on)))
        {
            int n = telling.Count(k => dists[ci][k] <= on);
            Console.WriteLine($"    [{ci,2}] {Candidates[ci].Name,-36} {n,6} ({n * 100.0 / Math.Max(1, telling.Count):F1}%)");
        }
        if (pair is [int a, int b])
        {
            int onlyA = 0, onlyB = 0;
            for (int k = 0; k < total; k++)
            {
                if (dists[a][k] <= on && dists[b][k] >= off) onlyA++;
                if (dists[b][k] <= on && dists[a][k] >= off) onlyB++;
            }
            Console.WriteLine($"  {Candidates[a].Name} alone: {onlyA}; {Candidates[b].Name} alone: {onlyB}");
        }
        foreach (var ci in Enumerable.Range(0, Candidates.Count).OrderByDescending(ci => dists[ci].Count(d => d <= on)))
        {
            var d = dists[ci];
            Console.WriteLine($"  {Candidates[ci].Name,-36} within {on}: {d.Count(x => x <= on) * 100.0 / Math.Max(1, total),5:F1}%  within {2 * on}: {d.Count(x => x <= 2 * on) * 100.0 / Math.Max(1, total),5:F1}%");
        }
    }
}
