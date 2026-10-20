using Noggog;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Navmesh;

/// <summary>
/// TM_NAV_SURFACE=1 (dev aid): what each exterior navmesh stands on, measured against its cell's input as wound (the
/// land and the placed objects' collision, before Recast turns every face up). Sampled every 64 units over each
/// navmesh triangle (a triangle with no such point at its middle), each sample: on the land (within 32 units of it),
/// under it, or above it, and then on what: the nearest input face under it (from 48 below to 32 above: Recast's detail mesh lies within 24 of the floor) facing up as
/// wound, or an underside (facing down: walkable only because exteriors' faces are all turned up), or none. Each
/// sample is also checked for being shut inside something: from 64 units over it, a ray up and four level ones
/// (east, west, north, south) each meet a face from behind (its wound normal along the ray) within 4096 units.
/// </summary>
public static class ExteriorSurface
{
    public static readonly bool On = Environment.GetEnvironmentVariable("TM_NAV_SURFACE") == "1";

    static readonly object Gate = new();
    static long samples, onLand, underLand, noLand, onUp, onUnder, onNothing, shut, shutOnLand, shutAbove;
    static readonly List<(long Count, string Where)> worstUnder = [], worstShut = [], worstNothing = [], worstNoLand = [];

    public static string Summary
    {
        get
        {
            lock (Gate)
            {
                string Pct(long n) => $"{100.0 * n / Math.Max(1, samples):F2}%";
                return $"navmesh surface ({samples:N0} samples, 64 units apart): on the land {Pct(onLand)}, under it {Pct(underLand)}, over no land {Pct(noLand)}; above the land on a face up {Pct(onUp)}, " +
                    $"on an underside {Pct(onUnder)}, on nothing found {Pct(onNothing)}; shut inside something {Pct(shut)} ({Pct(shutOnLand)} on the land, {Pct(shutAbove)} above it)" +
                    (worstUnder.Count > 0 ? $"; most on undersides: {string.Join(", ", worstUnder.Select(w => $"{w.Count} at {w.Where}"))}" : "") +
                    (worstShut.Count > 0 ? $"; most shut in: {string.Join(", ", worstShut.Select(w => $"{w.Count} at {w.Where}"))}" : "") +
                    (worstNothing.Count > 0 ? $"; most on nothing found: {string.Join(", ", worstNothing.Select(w => $"{w.Count} at {w.Where}"))}" : "") +
                    (worstNoLand.Count > 0 ? $"; most over no land: {string.Join(", ", worstNoLand.Select(w => $"{w.Count} at {w.Where}"))}" : "");
            }
        }
    }

    public static void Check(string world, int cx, int cy, float[] input, List<P3Float> verts, List<(int A, int B, int C)> tris, Func<float, float, float?> land)
    {
        var wound = new WoundInput(input);
        Vector3? firstUnderside = null, firstShut = null, firstNothing = null, firstNoLand = null;
        long n = 0, nLand = 0, nUnder = 0, nNoLand = 0, nUp = 0, nUnderside = 0, nNothing = 0, nShut = 0, nShutLand = 0, nShutAbove = 0;
        void Sample(Vector3 p)
        {
            n++;
            bool isLand = false;
            if (land(p.X, p.Y) is not { } g) { nNoLand++; firstNoLand ??= p; }
            else if (MathF.Abs(p.Z - g) <= 32) { nLand++; isLand = true; }
            else if (p.Z < g - 32) nUnder++;
            if (!isLand)
                switch (wound.Under(p, 48, 32))
                {
                    case > 0: nUp++; break;
                    case < 0: nUnderside++; firstUnderside ??= p; break;
                    default: nNothing++; firstNothing ??= p; break;
                }
            if (wound.Shut(p))
            {
                nShut++; firstShut ??= p;
                if (isLand) nShutLand++; else nShutAbove++;
            }
        }
        foreach (var (ia, ib, ic) in tris)
        {
            var A = verts[ia]; var B = verts[ib]; var C = verts[ic];
            float det = (B.Y - C.Y) * (A.X - C.X) + (C.X - B.X) * (A.Y - C.Y);
            if (MathF.Abs(det) < 1e-3f) continue;
            float minX = MathF.Min(A.X, MathF.Min(B.X, C.X)), maxX = MathF.Max(A.X, MathF.Max(B.X, C.X)), minY = MathF.Min(A.Y, MathF.Min(B.Y, C.Y)), maxY = MathF.Max(A.Y, MathF.Max(B.Y, C.Y));
            bool any = false;
            for (float py = MathF.Ceiling(minY / 64) * 64; py <= maxY; py += 64)
                for (float px = MathF.Ceiling(minX / 64) * 64; px <= maxX; px += 64)
                {
                    float l1 = ((B.Y - C.Y) * (px - C.X) + (C.X - B.X) * (py - C.Y)) / det, l2 = ((C.Y - A.Y) * (px - C.X) + (A.X - C.X) * (py - C.Y)) / det, l3 = 1 - l1 - l2;
                    if (l1 < -1e-4f || l2 < -1e-4f || l3 < -1e-4f) continue;
                    Sample(new Vector3(px, py, l1 * A.Z + l2 * B.Z + l3 * C.Z));
                    any = true;
                }
            if (!any) Sample(new Vector3((A.X + B.X + C.X) / 3, (A.Y + B.Y + C.Y) / 3, (A.Z + B.Z + C.Z) / 3));
        }
        lock (Gate)
        {
            samples += n; onLand += nLand; underLand += nUnder; noLand += nNoLand; onUp += nUp; onUnder += nUnderside; onNothing += nNothing;
            shut += nShut; shutOnLand += nShutLand; shutAbove += nShutAbove;
            string Where(Vector3? e) => $"{world} {cx},{cy} of {n}" + (e is { } q ? $" (e.g. {q.X:F0},{q.Y:F0},{q.Z:F0})" : "");
            if (nUnderside > 0) Keep(worstUnder, (nUnderside, Where(firstUnderside)));
            if (nShut > 0) Keep(worstShut, (nShut, Where(firstShut)));
            if (nNothing > 0) Keep(worstNothing, (nNothing, Where(firstNothing)));
            if (nNoLand > 0) Keep(worstNoLand, (nNoLand, Where(firstNoLand)));
        }
        static void Keep(List<(long Count, string Where)> list, (long, string) item)
        {
            list.Add(item);
            list.Sort((a, b) => b.Count.CompareTo(a.Count));
            if (list.Count > 8) list.RemoveAt(8);
        }
    }
}
