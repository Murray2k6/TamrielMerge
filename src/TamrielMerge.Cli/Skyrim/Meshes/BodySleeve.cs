using System.Numerics;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>
/// A worn piece made to fit Skyrim's own body where its game had no worn model of it (Morrowind's belts, Daggerfall's
/// greaves and pauldrons): a sleeve of rings round a stretch of a body axis (the waist's, a thigh's, an upper arm's),
/// each ring's points where rays out from the axis meet the naked body, pushed out by a margin; every point takes the
/// body's own skin weights there (its triangle's corners, blended), so it bends with the body as the body does. The
/// texture (the item's own) is wrapped round it. Made from the same rays at both weights, so the meshes' vertices match
/// one to one, as the weight slider needs.
/// </summary>
public static class BodySleeve
{
    /// <summary>Why the last <see cref="Make"/> on this thread found no body (for the log).</summary>
    [ThreadStatic] public static string? LastFailure;

    /// <summary>How many points the last <see cref="Make"/> on this thread put back on their ring's run.</summary>
    [ThreadStatic] public static int Repaired;

    /// <summary>
    /// A sleeve of <paramref name="rings"/> rings of <paramref name="segments"/> points from <paramref name="from"/> to
    /// <paramref name="to"/> (the axis) on <paramref name="body"/> (the naked body's shapes at one weight: its skin and
    /// the underwear over its waist, whose skin under it is open), the
    /// texture repeated <paramref name="repeats"/> times round it. Rays look no further than <paramref name="reach"/> out
    /// (so an arm or the other leg isn't taken for this part); <paramref name="up"/> a direction across the axis that
    /// angle 0 points along. Null when a ray finds no body (the axis isn't inside it).
    /// </summary>
    /// <summary>The part of a texture a sleeve wears: its UV rectangle, and whether the texture runs along it in v (else
    /// in u). A belt's strap uses its own piece of the belt's texture (an atlas holds the strap with other parts).</summary>
    public readonly record struct TextureSpan(float U0, float U1, float V0, float V1, bool AlongV)
    {
        public static readonly TextureSpan Whole = new(0, 1, 0, 1, false);

        /// <summary>The rectangle a piece's UVs cover, along whichever UV changes more along the piece's longest side.</summary>
        public static TextureSpan Of(WornShape piece)
        {
            float u0 = piece.Uv.Min(t => t.X), u1 = piece.Uv.Max(t => t.X), v0 = piece.Uv.Min(t => t.Y), v1 = piece.Uv.Max(t => t.Y);
            var lo = piece.V.Aggregate(new Vector3(float.MaxValue), Vector3.Min); var hi = piece.V.Aggregate(new Vector3(float.MinValue), Vector3.Max);
            bool alongX = hi.X - lo.X >= hi.Y - lo.Y;
            // How much u and v change per unit along that side (a least-squares slope's size).
            float Slope(Func<int, float> uvOf)
            {
                int n = piece.V.Length; if (n < 2) return 0;
                double mx = 0, mt = 0; for (int i = 0; i < n; i++) { mx += alongX ? piece.V[i].X : piece.V[i].Y; mt += uvOf(i); }
                mx /= n; mt /= n; double sxy = 0, sxx = 0;
                for (int i = 0; i < n; i++) { double dx = (alongX ? piece.V[i].X : piece.V[i].Y) - mx; sxy += dx * (uvOf(i) - mt); sxx += dx * dx; }
                return sxx > 0 ? (float)Math.Abs(sxy / sxx) : 0;
            }
            return new TextureSpan(u0, u1, v0, v1, Slope(i => piece.Uv[i].Y) > Slope(i => piece.Uv[i].X));
        }
    }

    public static WornShape? Make(IReadOnlyList<WornShape> body, Vector3 from, Vector3 to, Vector3 up, int rings, int segments, float margin, float reach,
        string name, string[] textures, float repeats) => Make(body, from, to, up, rings, segments, margin, reach, name, textures, repeats, TextureSpan.Whole, out _);

    /// <param name="span">The texture's part the sleeve wears, repeated <paramref name="repeats"/> times round it, each
    /// repeat a strip of its own (vertices doubled where two meet, so the part repeats without smearing).</param>
    /// <param name="columns">The vertices a ring has (the sleeve's vertex i is ring i / columns, column i % columns).</param>
    /// <param name="arc">The part of the way round the sleeve goes, in degrees either side of angle 0 (<paramref name="up"/>'s
    /// way): 180 all the way round (a closed ring), less for an open plate (a pauldron over the outside of the upper arm:
    /// its inner side lies against the chest in Skyrim's bind pose, with no outer surface there).</param>
    public static WornShape? Make(IReadOnlyList<WornShape> body, Vector3 from, Vector3 to, Vector3 up, int rings, int segments, float margin, float reach,
        string name, string[] textures, float repeats, TextureSpan span, out int columns, float arc = 180)
    {
        bool closed = arc >= 179.9f;
        columns = 0;
        // One surface a shape, so a hit names that shape's vertices (for their weights).
        var surfaces = body.Select(s => new WornSurface([s], 10)).ToList();
        (Vector3 Point, Vector3 Normal, int A, int B, int C, float U, float W, int Shape)? Nearest(Vector3 p)
        {
            (Vector3, Vector3, int, int, int, float, float, int)? best = null;
            float bestGap = float.MaxValue;
            for (int k = 0; k < surfaces.Count; k++)
                if (surfaces[k].NearestTriangle(p) is { } h && Vector3.Distance(h.Point, p) < bestGap)
                {
                    bestGap = Vector3.Distance(h.Point, p);
                    best = (h.Point, h.Normal, h.A, h.B, h.C, h.U, h.W, k);
                }
            return best;
        }
        var axis = Vector3.Normalize(to - from);
        var side = Vector3.Normalize(Vector3.Cross(axis, up));
        var front = Vector3.Cross(side, axis);
        // The columns: per repeat a strip from the part's start to its end, its first column the last's angle again.
        int strips = Math.Max(1, (int)MathF.Round(repeats));
        int perStrip = Math.Max(2, (int)MathF.Round(segments / (float)strips));
        segments = perStrip * strips;
        var layout = new List<(int Step, float Along)>();
        for (int k = 0; k < strips; k++)
            for (int j = 0; j <= perStrip; j++) layout.Add((k * perStrip + j, j / (float)perStrip));
        columns = layout.Count;
        var v = new Vector3[rings * columns];
        var uv = new Vector2[v.Length];
        var weights = new (string Bone, float W)[v.Length][];
        var hits = new Dictionary<(int Ring, int Step), (Vector3 P, (string, float)[] W)>();
        var grazed = new HashSet<(int Ring, int Step)>();
        // Every surface triangle, for rays' exact crossings.
        var triangles = new List<(Vector3 A, Vector3 B, Vector3 C, int Ia, int Ib, int Ic, int Shape)>();
        for (int k = 0; k < body.Count; k++)
            for (int t = 0; t + 2 < body[k].Tris.Length; t += 3)
                triangles.Add((body[k].V[body[k].Tris[t]], body[k].V[body[k].Tris[t + 1]], body[k].V[body[k].Tris[t + 2]], body[k].Tris[t], body[k].Tris[t + 1], body[k].Tris[t + 2], k));
        // The outermost crossing of the ray from reach out, inward to the axis (Möller-Trumbore), with the triangle's
        // corners, the barycentric weights of its second and third, and its face normal.
        (Vector3 Point, Vector3 Normal, int A, int B, int C, float U, float W, int Shape)? Crossing(Vector3 centre, Vector3 dir)
        {
            var origin = centre + dir * reach; var inward = -dir;
            float best = float.MaxValue;
            (Vector3, Vector3, int, int, int, float, float, int)? found = null;
            foreach (var t in triangles)
            {
                Vector3 e1 = t.B - t.A, e2 = t.C - t.A, pv = Vector3.Cross(inward, e2);
                float det = Vector3.Dot(e1, pv);
                if (MathF.Abs(det) < 1e-8f) continue;
                float inv = 1 / det;
                Vector3 tv = origin - t.A;
                float u = Vector3.Dot(tv, pv) * inv;
                if (u < 0 || u > 1) continue;
                Vector3 qv = Vector3.Cross(tv, e1);
                float w = Vector3.Dot(inward, qv) * inv;
                if (w < 0 || u + w > 1) continue;
                float dist = Vector3.Dot(e2, qv) * inv;
                if (dist < 0 || dist > reach || dist >= best) continue;
                best = dist;
                var n = Vector3.Normalize(Vector3.Cross(e1, e2));
                found = (origin + inward * dist, n, t.Ia, t.Ib, t.Ic, u, w, t.Shape);
            }
            return found;
        }
        // A ray out from the axis, from outside in: the first point a surface is within a step of (its outer side). A ray
        // through a gap (between the arm and the side, the cuirass and the body) passes close by without crossing: then
        // the surface point it came nearest, within 2.5 (2026-10-19: rays at the shoulders passed within 0.55-0.9, one at
        // a dwarven cuirass's thigh 2.0), marked as grazing.
        ((Vector3 Point, Vector3 Normal, int A, int B, int C, float U, float W, int Shape)? Hit, float Closest, bool Grazing) Cast(Vector3 centre, Vector3 dir)
        {
            // The ray's own crossing of a triangle, the outermost (distinct rays, distinct points: a point within a step
            // of the ray can lie off it, and near a strap's edge several rays took the same one, 2026-10-19).
            if (Crossing(centre, dir) is { } exact) return (exact, 0, false);
            (Vector3 Point, Vector3 Normal, int A, int B, int C, float U, float W, int Shape)? hit = null, nearestSeen = null;
            float closest = float.MaxValue;
            for (float d = reach; d >= 0 && hit is null; d -= 0.25f)
                if (Nearest(centre + dir * d) is { } h)
                {
                    float gap = Vector3.Distance(h.Point, centre + dir * d);
                    if (gap < closest) { closest = gap; nearestSeen = h; }
                    if (gap < 0.5f) hit = h;
                }
            if (hit is null && closest < 2.5f) return (nearestSeen, closest, true);
            return (hit, closest, false);
        }
        // A point moved by the repairs below, put back on the surface along its own ray from its ring's middle (not on the
        // nearest surface point, which near a cuirass's corner is the same for neighbouring points: 2026-10-19, two
        // vertices of 55 of 144 pauldrons and greaves met there, a triangle of no area).
        (Vector3 P, (string, float)[] W)? OnRay(int r, Vector3 target)
        {
            var c = Vector3.Lerp(from, to, rings == 1 ? 0.5f : r / (float)(rings - 1));
            var d = target - c; d -= axis * Vector3.Dot(d, axis);
            if (d.LengthSquared() < 1e-6f) return null;
            d = Vector3.Normalize(d);
            // Only the ray's exact crossing: a near miss's nearest point is what made neighbouring points meet.
            if (Crossing(c, d) is not { } f) return null;
            var outward = Vector3.Dot(f.Normal, d) > 0 ? f.Normal : d;
            return (f.Point + Vector3.Normalize(outward) * margin, Blend(body[f.Shape], f.A, f.B, f.C, f.U, f.W));
        }
        for (int r = 0; r < rings; r++)
        {
            var centre = Vector3.Lerp(from, to, rings == 1 ? 0.5f : r / (float)(rings - 1));
            for (int s = 0; s < columns; s++)
            {
                var (step, along) = layout[s];
                float across = 1 - r / (float)Math.Max(1, rings - 1);
                // The texture's part: along it round the sleeve, across it over the sleeve's width.
                uv[r * columns + s] = span.AlongV
                    ? new Vector2(span.U0 + across * (span.U1 - span.U0), span.V0 + along * (span.V1 - span.V0))
                    : new Vector2(span.U0 + along * (span.U1 - span.U0), span.V0 + across * (span.V1 - span.V0));
                int key = closed ? step % segments : step;
                if (hits.TryGetValue((r, key), out var known)) { v[r * columns + s] = known.P; weights[r * columns + s] = known.W; continue; }
                float angle = closed ? 2 * MathF.PI * key / segments : (-arc + 2 * arc * key / segments) * MathF.PI / 180;
                var dir = front * MathF.Cos(angle) + side * MathF.Sin(angle);
                var (hit, closest, grazing) = Cast(centre, dir);
                if (grazing) grazed.Add((r, key));
                if (hit is not { } found)
                {
                    LastFailure = $"ring {r} angle {angle * 180 / MathF.PI:F0}: from ({centre.X:F1},{centre.Y:F1},{centre.Z:F1}) the body came no nearer than {(closest == float.MaxValue ? "(none within reach)" : closest.ToString("F2"))}";
                    return null;
                }
                int i = r * columns + s;
                var outward = Vector3.Dot(found.Normal, dir) > 0 ? found.Normal : dir;
                v[i] = found.Point + Vector3.Normalize(outward) * margin;
                weights[i] = Blend(body[found.Shape], found.A, found.B, found.C, found.U, found.W);
                hits[(r, key)] = (v[i], weights[i]);
            }
        }
        // The sleeve as a smooth shell over what its rays crossed (2026-10-19: put back onto the surface, points of a plate
        // over a cuirass's straps and pads met at its corners; 55 of 144 meshes had triangles of no area, and an open
        // corner folded to a point). Each ray keeps its own line out from the axis, so no two rays' points can meet; its
        // distance out is its crossing's (the margin added), a ray through a gap taking its ring's nearest crossings';
        // then, twice, each is raised halfway from its neighbours' mean to their farthest, never below its own, so the
        // shell stays outside what it covers and has no spikes. Each point keeps the weights of the surface its ray
        // crossed (a gap's, its nearer neighbour's). TM_SLEEVE_RAW=1 (dev aid): the crossings as found.
        Repaired = 0;
        int keys = closed ? segments : segments + 1;
        if (Environment.GetEnvironmentVariable("TM_SLEEVE_RAW") != "1")
        {
            var radius = new float[rings, keys];
            var dirs = new Vector3[keys];
            var centres = new Vector3[rings];
            for (int k = 0; k < keys; k++)
            {
                float angle = closed ? 2 * MathF.PI * k / segments : (-arc + 2 * arc * k / segments) * MathF.PI / 180;
                dirs[k] = front * MathF.Cos(angle) + side * MathF.Sin(angle);
            }
            for (int r = 0; r < rings; r++)
            {
                centres[r] = Vector3.Lerp(from, to, rings == 1 ? 0.5f : r / (float)(rings - 1));
                for (int k = 0; k < keys; k++)
                    radius[r, k] = grazed.Contains((r, k)) ? float.NaN : Vector3.Dot(hits[(r, k)].P - centres[r], dirs[k]);
            }
            int Wrap(int k) => closed ? ((k % keys) + keys) % keys : k;
            // Gaps: between the ring's nearest crossings either side, by how far round each is; the nearer one's weights.
            for (int r = 0; r < rings; r++)
                for (int k = 0; k < keys; k++)
                {
                    if (!float.IsNaN(radius[r, k])) continue;
                    (int K, int D)? Near(int step)
                    {
                        for (int n = 1; n < keys; n++)
                        {
                            int at = Wrap(k + step * n);
                            if (at < 0 || at >= keys) return null;
                            if (!grazed.Contains((r, at))) return (at, n);
                        }
                        return null;
                    }
                    var below = Near(-1); var above = Near(1);
                    if (below is null && above is null) continue;
                    var (ka, da) = below ?? above!.Value; var (kb, db) = above ?? below!.Value;
                    radius[r, k] = below is null || above is null ? radius[r, ka] : radius[r, ka] + (radius[r, kb] - radius[r, ka]) * da / (da + db);
                    hits[(r, k)] = (hits[(r, k)].P, hits[(r, da <= db ? ka : kb)].W);
                    Repaired++;
                }
            // A ring all gaps: its nearest ring's distances and weights.
            for (int r = 0; r < rings; r++)
                if (Enumerable.Range(0, keys).All(k => float.IsNaN(radius[r, k])))
                {
                    int source = Enumerable.Range(0, rings).Where(o => !Enumerable.Range(0, keys).All(k => float.IsNaN(radius[o, k])))
                        .OrderBy(o => Math.Abs(o - r)).DefaultIfEmpty(-1).First();
                    if (source < 0) break;
                    for (int k = 0; k < keys; k++) { radius[r, k] = radius[source, k]; hits[(r, k)] = (hits[(r, k)].P, hits[(source, k)].W); Repaired++; }
                }
            for (int pass = 0; pass < 2; pass++)
            {
                var next = (float[,])radius.Clone();
                for (int r = 0; r < rings; r++)
                    for (int k = 0; k < keys; k++)
                    {
                        if (float.IsNaN(radius[r, k])) continue;
                        float sum = 0, most = float.MinValue; int n = 0;
                        for (int dr = -1; dr <= 1; dr++)
                            for (int dk = -1; dk <= 1; dk++)
                            {
                                int rr = r + dr, kk = Wrap(k + dk);
                                if (rr < 0 || rr >= rings || kk < 0 || kk >= keys || float.IsNaN(radius[rr, kk])) continue;
                                sum += radius[rr, kk]; most = MathF.Max(most, radius[rr, kk]); n++;
                            }
                        if (n > 0) next[r, k] = MathF.Max(radius[r, k], (sum / n + most) / 2);
                    }
                radius = next;
            }
            for (int r = 0; r < rings; r++)
                for (int k = 0; k < keys; k++)
                    if (!float.IsNaN(radius[r, k])) hits[(r, k)] = (centres[r] + dirs[k] * radius[r, k], hits[(r, k)].W);
        }
        for (int r = 0; r < rings; r++)
            for (int s = 0; s < columns; s++)
            {
                int key = closed ? layout[s].Step % segments : layout[s].Step;
                (v[r * columns + s], weights[r * columns + s]) = hits[(r, key)];
            }
        var tris = new List<int>();
        for (int r = 0; r + 1 < rings; r++)
            for (int k = 0; k < strips; k++)
                for (int j = 0; j < perStrip; j++)
                {
                    int s = k * (perStrip + 1) + j;
                    int a = r * columns + s, b = a + 1, c = a + columns, d = c + 1;
                    // Wound so the faces look out from the axis (checked below).
                    tris.AddRange([a, c, b, b, c, d]);
                }
        var shape = new WornShape { Name = name, V = v, N = [], Uv = uv, Tris = tris.ToArray(), Weights = weights, Textures = textures };
        // The winding by the first face: its normal must point away from the axis.
        var n0 = Vector3.Cross(v[shape.Tris[1]] - v[shape.Tris[0]], v[shape.Tris[2]] - v[shape.Tris[0]]);
        var mid = (v[shape.Tris[0]] + v[shape.Tris[1]] + v[shape.Tris[2]]) / 3;
        var toAxis = mid - (from + axis * Vector3.Dot(mid - from, axis));
        if (Vector3.Dot(n0, toAxis) < 0)
            for (int t = 0; t + 2 < shape.Tris.Length; t += 3) (shape.Tris[t + 1], shape.Tris[t + 2]) = (shape.Tris[t + 2], shape.Tris[t + 1]);
        return shape;
    }

    /// <summary>The body's skin weights at a point of its triangle (corners a, b, c, barycentric u for b and w for c):
    /// the corners' weights blended, the four heaviest bones kept, summing to 1.</summary>
    static (string Bone, float W)[] Blend(WornShape body, int a, int b, int c, float u, float w)
    {
        var sum = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, share) in new[] { (a, 1 - u - w), (b, u), (c, w) })
            foreach (var (bone, weight) in body.Weights[k]) sum[bone] = sum.GetValueOrDefault(bone) + weight * share;
        var top = sum.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).Take(4).ToList();
        float total = top.Sum(kv => kv.Value);
        return top.Select(kv => (kv.Key, kv.Value / total)).ToArray();
    }

    /// <summary>
    /// A small rigid piece (a belt's buckle) set on the sleeve at its middle ring's point at angle 0 (the axis'
    /// front), facing out: <paramref name="piece"/>'s vertices in its own space (its face looking along +Z), turned so +Z
    /// looks out from the sleeve, and given that point's weights.
    /// </summary>
    public static WornShape Attach(WornShape sleeve, int rings, int columns, WornShape piece, float lift)
    {
        // The middle ring's first vertex is at angle 0 (the front); out from the ring's middle through it.
        int at = rings / 2 * columns;
        var anchor = sleeve.V[at];
        var ringMiddle = Enumerable.Range(at, columns).Aggregate(Vector3.Zero, (s, i) => s + sleeve.V[i]) / columns;
        var outward = Vector3.Normalize(new Vector3(anchor.X - ringMiddle.X, anchor.Y - ringMiddle.Y, 0));
        var upAxis = Vector3.UnitZ;
        var right = Vector3.Cross(upAxis, outward);
        var centre = piece.V.Aggregate(Vector3.Zero, (s, p) => s + p) / piece.V.Length;
        var v = piece.V.Select(p => p - centre).Select(p => anchor + outward * (lift + p.Z) + right * p.X + upAxis * p.Y).ToArray();
        var weights = piece.V.Select(_ => sleeve.Weights[at]).ToArray();
        return new WornShape
        {
            Name = piece.Name, V = v, N = [], Uv = piece.Uv, Tris = piece.Tris, Weights = weights, Textures = piece.Textures,
            Flags1 = piece.Flags1, Flags2 = piece.Flags2, ShaderType = piece.ShaderType, Glossiness = piece.Glossiness,
            SpecularStrength = piece.SpecularStrength, Alpha = piece.Alpha, AlphaFlags = piece.AlphaFlags, AlphaThreshold = piece.AlphaThreshold,
        };
    }

    /// <summary>
    /// How a made piece sits (the check, for the log): its farthest vertex from the naked body, every vertex's weights
    /// summing to 1 and naming bones the skeleton has, and its textures there.
    /// </summary>
    public static string Check(IEnumerable<WornShape> shapes, IReadOnlyList<WornShape> body, BindSkeleton skeleton, Func<string, bool> textureThere)
    {
        var surface = new WornSurface(body, 30);
        float farthest = 0, spike = 0, standOut = 0, thinnest = 0; int badWeights = 0, badBones = 0, vertices = 0, missingTextures = 0;
        foreach (var s in shapes)
        {
            // Spikes: the longest edge against the shape's median edge.
            var edges = new List<float>();
            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
                for (int e = 0; e < 3; e++) edges.Add(Vector3.Distance(s.V[s.Tris[t + e]], s.V[s.Tris[t + (e + 1) % 3]]));
            if (edges.Count > 0) { edges.Sort(); spike = MathF.Max(spike, edges[^1] / MathF.Max(edges[edges.Count / 2], 1e-3f)); }
            // The thinnest triangle: longest edge squared over twice its area (an equilateral one is 1.2, a needle high).
            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
            {
                Vector3 a = s.V[s.Tris[t]], b = s.V[s.Tris[t + 1]], c = s.V[s.Tris[t + 2]];
                float area = Vector3.Cross(b - a, c - a).Length() / 2, longest = MathF.Max(Vector3.Distance(a, b), MathF.Max(Vector3.Distance(b, c), Vector3.Distance(c, a)));
                if (area > 1e-6f) thinnest = MathF.Max(thinnest, longest * longest / (2 * area));
            }
            // Standing out: each vertex from its neighbours' middle (neighbours by shared triangles), against the median edge.
            if (edges.Count > 0)
            {
                float med = MathF.Max(edges[edges.Count / 2], 1e-3f);
                var nbrs = new Dictionary<int, HashSet<int>>();
                for (int t = 0; t + 2 < s.Tris.Length; t += 3)
                    for (int e = 0; e < 3; e++)
                    {
                        int a = s.Tris[t + e], b = s.Tris[t + (e + 1) % 3];
                        (nbrs.TryGetValue(a, out var na) ? na : nbrs[a] = []).Add(b);
                        (nbrs.TryGetValue(b, out var nb) ? nb : nbrs[b] = []).Add(a);
                    }
                foreach (var (i, set) in nbrs)
                {
                    if (set.Count < 2) continue;
                    var middle = set.Aggregate(Vector3.Zero, (sum, j) => sum + s.V[j]) / set.Count;
                    standOut = MathF.Max(standOut, Vector3.Distance(s.V[i], middle) / med);
                }
            }
            foreach (var p in s.V)
            {
                vertices++;
                farthest = Math.Max(farthest, surface.Nearest(p) is { } h ? Vector3.Distance(p, h.Point) : float.MaxValue);
            }
            foreach (var w in s.Weights)
            {
                if (MathF.Abs(w.Sum(x => x.W) - 1) > 1e-3f) badWeights++;
                if (w.Any(x => !skeleton.Has(x.Bone))) badBones++;
            }
            missingTextures += s.Textures.Count(t => t.Length > 0 && !textureThere(t));
        }
        return $"{vertices} vertices, farthest {farthest:F1} units from the body, longest edge {spike:F1}x the median, standing out {standOut:F1}x, thinnest triangle {thinnest:F1}, {badWeights} weights not summing to 1, {badBones} on bones the skeleton lacks, {missingTextures} textures not there";
    }
}
