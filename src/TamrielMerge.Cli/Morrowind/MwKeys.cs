using System.Numerics;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's (NetImmerse's) key interpolation, every kind its creatures use (2026-10-21, `mw-key-types`: of their
/// tracks 969 rotation lists are TBC, 579 translation and 171 scale lists quadratic, 272 and 120 TBC, 236 morph weight
/// lists quadratic; read straight before, they missed the curves):
/// <list type="bullet">
/// <item>linear (1): straight between keys, rotations along the shortest arc;</item>
/// <item>quadratic (2): a cubic Hermite curve through the keys, each key's stored tangents per interval (the first stored
/// leads into the key, the second out of it: measured over 17,754 keys, the first sits 1.0 from the step coming in and
/// 9.1 from the step going out, the second 1.2 and 8.8 the other way);</item>
/// <item>TBC (3): Kochanek-Bartels, each key's tangents from its tension, continuity and bias and its neighbours, scaled
/// for unequal key spacing; a first or last key takes itself as its missing neighbour;</item>
/// <item>TBC rotations: the quaternion spline (squad) whose inner quaternions come from the same tension, continuity and
/// bias in the rotations' logarithms.</item>
/// </list>
/// Before the first key the first value holds, after the last the last.
/// </summary>
public static class MwKeys
{
    static (int Index, float U)? Segment<T>(List<(float Time, T Value)> keys, float t)
    {
        if (keys.Count == 0) return null;
        if (t <= keys[0].Time) return (0, 0);
        for (int i = 1; i < keys.Count; i++)
            if (t <= keys[i].Time)
            {
                float span = keys[i].Time - keys[i - 1].Time;
                return (i - 1, span > 1e-6f ? (t - keys[i - 1].Time) / span : 1);
            }
        return (keys.Count - 1, 0);
    }

    static (float H00, float H10, float H01, float H11) Hermite(float u)
    {
        float u2 = u * u, u3 = u2 * u;
        return (2 * u3 - 3 * u2 + 1, u3 - 2 * u2 + u, -2 * u3 + 3 * u2, u3 - u2);
    }

    /// <summary>A key's (incoming, outgoing) tangents per interval: stored for quadratic keys, made from tension,
    /// continuity and bias for TBC ones.</summary>
    public static (Vector3 In, Vector3 Out) Tangents(List<(float Time, Vector3 Value)> keys, int type, List<float[]> extra, int i)
    {
        if (type == 2 && i < extra.Count)
        {
            var e = extra[i];
            return e.Length >= 6 ? (new Vector3(e[0], e[1], e[2]), new Vector3(e[3], e[4], e[5])) : (new Vector3(e[0], 0, 0), new Vector3(e[1], 0, 0));
        }
        if (type == 3 && i < extra.Count)
        {
            float ten = extra[i][0], con = extra[i][1], bia = extra[i][2];
            int p = Math.Max(0, i - 1), n = Math.Min(keys.Count - 1, i + 1);
            var prev = keys[i].Value - keys[p].Value; var next = keys[n].Value - keys[i].Value;
            float dtPrev = keys[i].Time - keys[p].Time, dtNext = keys[n].Time - keys[i].Time, sum = dtPrev + dtNext;
            var tin = (1 - ten) * (1 - con) * (1 + bia) / 2 * prev + (1 - ten) * (1 + con) * (1 - bia) / 2 * next;
            var tout = (1 - ten) * (1 + con) * (1 + bia) / 2 * prev + (1 - ten) * (1 - con) * (1 - bia) / 2 * next;
            if (sum > 1e-6f) { tin *= 2 * dtPrev / sum; tout *= 2 * dtNext / sum; }
            return (tin, tout);
        }
        return (Vector3.Zero, Vector3.Zero);
    }

    /// <summary>A vector (or, in X, a float) at time <paramref name="t"/>.</summary>
    public static Vector3 Vector(List<(float Time, Vector3 Value)> keys, int type, List<float[]> extra, float t)
    {
        if (Segment(keys, t) is not { } s) return Vector3.Zero;
        var (i, u) = s;
        if (i + 1 >= keys.Count || u <= 0) return keys[i].Value;
        var a = keys[i].Value; var b = keys[i + 1].Value;
        if (type is 2 or 3 && extra.Count == keys.Count)
        {
            var (h00, h10, h01, h11) = Hermite(u);
            return h00 * a + h10 * Tangents(keys, type, extra, i).Out + h01 * b + h11 * Tangents(keys, type, extra, i + 1).In;
        }
        return Vector3.Lerp(a, b, u);
    }

    public static float Float(List<(float Time, float Value)> keys, int type, List<float[]> extra, float t) =>
        Vector(keys.Select(k => (k.Time, new Vector3(k.Value, 0, 0))).ToList(), type, extra, t).X;

    /// <summary>A rotation at time <paramref name="t"/>.</summary>
    public static Quaternion Rotation(List<(float Time, Quaternion Value)> keys, int type, List<float[]> extra, float t)
    {
        if (Segment(keys, t) is not { } s) return Quaternion.Identity;
        var (i, u) = s;
        if (i + 1 >= keys.Count || u <= 0) return keys[i].Value;
        var a = keys[i].Value; var b = Shortest(a, keys[i + 1].Value);
        if (type == 3 && extra.Count == keys.Count)
        {
            // The squad on one side of the double cover: the next key taken on the shorter arc from this one, its inner
            // quaternion made from it as taken, and the blends between them not turning back to a shorter arc of their own
            // (2026-10-21: with each blend taking its own, the Heart of Akulakhan's half turn between keys 90 degrees apart
            // as quaternions ran 191, 180, then 349 degrees over three frames, a frame 20 units off; it is 180 to 360).
            var aOut = Inner(keys, extra, i, outgoing: true, a);
            var bIn = Inner(keys, extra, i + 1, outgoing: false, b);
            return Quaternion.Normalize(Slerp(Slerp(a, b, u), Slerp(aOut, bIn, u, shortest: false), 2 * u * (1 - u), shortest: false));
        }
        return Slerp(a, b, u);
    }

    static Quaternion Shortest(Quaternion from, Quaternion to) => Quaternion.Dot(from, to) < 0 ? -to : to;

    /// <summary>Spherical blend, in doubles (near-equal rotations blend linearly, then made unit): along the shorter arc,
    /// or with <paramref name="shortest"/> false along the arc from <paramref name="a"/> to <paramref name="b"/> as given.</summary>
    public static Quaternion Slerp(Quaternion a, Quaternion b, float u, bool shortest = true)
    {
        double dot = (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W;
        if (shortest && dot < 0) { b = -b; dot = -dot; }
        if (dot < -1 + 1e-9)
        {
            // Opposite as given (one rotation both ways round): no arc between them; held at the first.
            return a;
        }
        double wa, wb;
        if (dot > 1 - 1e-9) { wa = 1 - u; wb = u; }
        else
        {
            double om = Math.Acos(Math.Min(1, dot)), so = Math.Sin(om);
            wa = Math.Sin((1 - u) * om) / so; wb = Math.Sin(u * om) / so;
        }
        return Quaternion.Normalize(new Quaternion((float)(a.X * wa + b.X * wb), (float)(a.Y * wa + b.Y * wb), (float)(a.Z * wa + b.Z * wb), (float)(a.W * wa + b.W * wb)));
    }

    static Vector3 Log(Quaternion q)
    {
        q = Quaternion.Normalize(q);
        if (q.W < 0) q = -q;
        double v = Math.Sqrt((double)q.X * q.X + (double)q.Y * q.Y + (double)q.Z * q.Z);
        if (v < 1e-12) return Vector3.Zero;
        double angle = Math.Atan2(v, q.W);
        double k = angle / v;
        return new Vector3((float)(q.X * k), (float)(q.Y * k), (float)(q.Z * k));
    }

    static Quaternion Exp(Vector3 v)
    {
        double a = v.Length();
        if (a < 1e-12) return Quaternion.Identity;
        double s = Math.Sin(a) / a;
        return new Quaternion((float)(v.X * s), (float)(v.Y * s), (float)(v.Z * s), (float)Math.Cos(a));
    }

    /// <summary>A TBC rotation key's inner quaternion leading out of it (or into it): q·exp((T − log(q⁻¹q_next))/2) out,
    /// q·exp((log(q_prev⁻¹q) − T)/2) in, the tangents T from tension, continuity and bias in log space, scaled for unequal
    /// spacing (zero tension, continuity and bias with even spacing give the plain squad). <paramref name="q"/> is the key
    /// on the side of the double cover the blend takes it (itself or its negative).</summary>
    static Quaternion Inner(List<(float Time, Quaternion Value)> keys, List<float[]> extra, int i, bool outgoing, Quaternion q)
    {
        float ten = extra[i][0], con = extra[i][1], bia = extra[i][2];
        int p = Math.Max(0, i - 1), n = Math.Min(keys.Count - 1, i + 1);
        var qp = Shortest(q, keys[p].Value); var qn = Shortest(q, keys[n].Value);
        var logPrev = Log(Quaternion.Inverse(qp) * q);   // the step coming in
        var logNext = Log(Quaternion.Inverse(q) * qn);   // the step going out
        float dtPrev = keys[i].Time - keys[p].Time, dtNext = keys[n].Time - keys[i].Time, sum = dtPrev + dtNext;
        if (outgoing)
        {
            var tout = (1 - ten) * (1 + con) * (1 + bia) / 2 * logPrev + (1 - ten) * (1 - con) * (1 - bia) / 2 * logNext;
            if (sum > 1e-6f) tout *= 2 * dtNext / sum;
            return q * Exp((tout - logNext) / 2);
        }
        var tin = (1 - ten) * (1 - con) * (1 + bia) / 2 * logPrev + (1 - ten) * (1 + con) * (1 - bia) / 2 * logNext;
        if (sum > 1e-6f) tin *= 2 * dtPrev / sum;
        return q * Exp((logPrev - tin) / 2);
    }

    /// <summary>
    /// Checks the interpolation against what it must be (`mw-keys-selftest`): through every key exactly, each kind's
    /// slope at a key's start its out tangent (quadratic, TBC), TBC with no tension, continuity or bias the Catmull-Rom
    /// curve, rotations unit length and through their keys, a TBC rotation smooth across its keys. Returns failures.
    /// </summary>
    public static List<string> SelfTest()
    {
        var fails = new List<string>();
        var keys = new List<(float, Vector3)> { (0, new(0, 0, 0)), (1, new(10, 2, 0)), (2.5f, new(12, -3, 4)), (3, new(20, 0, 1)) };
        var quad = keys.Select((k, i) => new float[] { 1 + i, -i, 0.5f, 2 - i, i * 0.3f, 1 }).ToList();
        var tbc0 = keys.Select(_ => new float[] { 0, 0, 0 }).ToList();
        var tbc = keys.Select((k, i) => new float[] { 0.3f - i * 0.1f, 0.2f * i - 0.2f, 0.1f * i }).ToList();
        foreach (var (type, extra, what) in new[] { (1, new List<float[]>(), "linear"), (2, quad, "quadratic"), (3, tbc, "TBC") })
            foreach (var (time, v) in keys)
                if (Vector3.Distance(Vector(keys, type, extra, time), v) > 1e-4f) fails.Add($"{what}: not through its key at {time}");
        // Slope at a segment's start, per interval, is the out tangent.
        foreach (var (type, extra, what) in new[] { (2, quad, "quadratic"), (3, tbc, "TBC") })
            for (int i = 0; i + 1 < keys.Count; i++)
            {
                float span = keys[i + 1].Item1 - keys[i].Item1, du = 1e-4f;
                var slope = (Vector(keys, type, extra, keys[i].Item1 + du * span) - keys[i].Item2) / du;
                var want = Tangents(keys, type, extra, i).Out;
                if (Vector3.Distance(slope, want) > 1e-2f * MathF.Max(1, want.Length())) fails.Add($"{what}: slope {slope} at key {i}, out tangent {want}");
            }
        // TBC with nothing set, evenly spaced: Catmull-Rom.
        var even = new List<(float, Vector3)> { (0, new(0, 0, 0)), (1, new(4, 1, 0)), (2, new(5, 5, 2)), (3, new(9, 4, 1)) };
        float uu = 0.37f;
        Vector3 p0 = even[0].Item2, p1 = even[1].Item2, p2 = even[2].Item2, p3 = even[3].Item2;
        var catmull = 0.5f * (2 * p1 + (-p0 + p2) * uu + (2 * p0 - 5 * p1 + 4 * p2 - p3) * uu * uu + (-p0 + 3 * p1 - 3 * p2 + p3) * uu * uu * uu);
        var ours = Vector(even, 3, tbc0, 1 + uu);
        if (Vector3.Distance(ours, catmull) > 1e-3f) fails.Add($"TBC 0/0/0: {ours}, Catmull-Rom {catmull}");
        // Rotations: unit, through their keys, smooth across a TBC key.
        var rk = new List<(float, Quaternion)>
        {
            (0, Quaternion.Identity), (1, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 0.8f)),
            (2, Quaternion.CreateFromAxisAngle(Vector3.Normalize(new Vector3(1, 1, 0)), 1.4f)), (3.5f, Quaternion.CreateFromAxisAngle(Vector3.UnitX, -0.6f)),
        };
        // (continuity 0: a TBC curve with continuity is meant to break its speed at a key)
        var rtbc = rk.Select((_, i) => new float[] { 0.2f - 0.05f * i, 0, 0.1f }).ToList();
        foreach (var (type, extra) in new[] { (1, new List<float[]>()), (3, rtbc) })
        {
            foreach (var (time, q) in rk)
                if (MathF.Abs(MathF.Abs(Quaternion.Dot(Rotation(rk, type, extra, time), q)) - 1) > 1e-5f) fails.Add($"rotation {type}: not through its key at {time}");
            for (float time = 0; time <= 3.5f; time += 0.05f)
                if (MathF.Abs(Rotation(rk, type, extra, time).Length() - 1) > 1e-4f) fails.Add($"rotation {type}: not unit at {time}");
        }
        // Smooth across key 1 (TBC): the angular speed just before and just after match.
        float d = 1e-3f;
        float AngleBetween(Quaternion x, Quaternion y) => 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(x, y)), 0, 1));
        var before = AngleBetween(Rotation(rk, 3, rtbc, 1 - d), Rotation(rk, 3, rtbc, 1)) / d;
        var after = AngleBetween(Rotation(rk, 3, rtbc, 1), Rotation(rk, 3, rtbc, 1 + d)) / d;
        if (MathF.Abs(before - after) > 0.05f * MathF.Max(before, after)) fails.Add($"TBC rotation: speed {before:F3} into key 1, {after:F3} out (continuity 0: must be smooth)");
        // A half turn between keys (90 degrees apart as quaternions, the Heart of Akulakhan's spin: keys at 180 then 360
        // degrees about Z, with a key either side): it turns on steadily, never back, never in a jump.
        var half = new List<(float, Quaternion)>
        {
            (0, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2)), (1, new Quaternion(0, -1.19e-7f, 1, 1.07e-6f)),
            (2, new Quaternion(0, -1.4e-13f, 1.14e-6f, -1)), (3, Quaternion.CreateFromAxisAngle(Vector3.UnitZ, 2.5f * MathF.PI)),
        };
        var htbc = half.Select(_ => new float[] { 0, 0, 0 }).ToList();
        foreach (var (type, extra) in new[] { (1, new List<float[]>()), (3, htbc) })
        {
            float last = float.NaN, worstStep = 0;
            for (float time = 1; time <= 2.0001f; time += 0.01f)
            {
                var q = Rotation(half, type, extra, time);
                float angle = 2 * MathF.Atan2(q.Z, q.W);   // about Z, unwrapped below
                if (!float.IsNaN(last)) { while (angle - last > MathF.PI) angle -= 2 * MathF.PI; while (angle - last < -MathF.PI) angle += 2 * MathF.PI; worstStep = MathF.Max(worstStep, MathF.Abs(angle - last)); if (angle < last - 1e-4f) { fails.Add($"rotation {type}: the half turn goes back at {time:F2}"); break; } }
                last = angle;
            }
            if (worstStep > 0.1f) fails.Add($"rotation {type}: the half turn jumps {worstStep * 180 / MathF.PI:F1} degrees in 0.01 s");
        }
        return fails;
    }
}
