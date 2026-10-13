using nifly;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Skyrim.Distant;

/// <summary>
/// A model as distant LOD uses it: its visible shapes in the model's own space (through the node chain), each with its
/// diffuse texture and whether it is cut out (alpha-tested leaves, fences), and its bounds. Read with nifly as the
/// flyby's scene exporter reads models (SceneExporter.WriteMesh): hidden, particle, effect-shader and skinned shapes
/// and editor markers are left out.
/// </summary>
public sealed class DistantModel
{
    public sealed class Part
    {
        public string Texture = "";
        public bool Cutout;
        public Vector3[] P = [];
        public Vector3[] N = [];
        public Vector2[] UV = [];
        public int[] T = [];
    }

    public List<Part> Parts { get; } = [];
    public Vector3 Min { get; private set; } = new(float.MaxValue);
    public Vector3 Max { get; private set; } = new(float.MinValue);
    /// <summary>Half the largest extent: how big the model reads from afar.</summary>
    public float Radius => Parts.Count == 0 ? 0 : MathF.Max(Max.X - Min.X, MathF.Max(Max.Y - Min.Y, Max.Z - Min.Z)) / 2;
    public int Triangles => Parts.Sum(p => p.T.Length / 3);

    static readonly object NifLock = new();

    public static DistantModel? Load(byte[] bytes)
    {
        // nifly reads from a file, and isn't safe to drive from several threads at once.
        string tmp = Path.Combine(Path.GetTempPath(), $"tm_lod_{Guid.NewGuid():N}.nif");
        lock (NifLock)
        {
            File.WriteAllBytes(tmp, bytes);
            try
            {
                using var nif = new NifFile();
                if (nif.Load(tmp) != 0) return null;
                var hdr = nif.GetHeader();
                var model = new DistantModel();
                foreach (var shape in nif.GetShapes())
                {
                    if ((shape.flags & 1) != 0 || shape.IsSkinned()) continue;
                    string type = hdr.GetBlockTypeStringById(hdr.GetBlockID(shape));
                    if (type.Contains("Particle")) continue;
                    var chain = new List<MatTransform> { shape.transform };
                    bool hidden = false;
                    for (var parent = nif.GetParentNode(shape); parent is not null; parent = nif.GetParentNode(parent))
                    {
                        chain.Add(parent.transform);
                        string name = parent.name.get() ?? "";
                        hidden |= (parent.flags & 1) != 0 || name.Contains("EditorMarker", StringComparison.OrdinalIgnoreCase);
                    }
                    if (hidden) continue;
                    var shader = nif.GetShader(shape);
                    if (shader is not null && hdr.GetBlockTypeStringById(hdr.GetBlockID(shader)) == "BSEffectShaderProperty") continue;
                    using var tris = new vectorTriangle();
                    shape.GetTriangles(tris);
                    var verts = nif.GetVertsForShape(shape)?.ToList() ?? [];
                    if (tris.Count == 0 || verts.Count == 0) continue;
                    var normals = nif.GetNormalsForShape(shape)?.ToList() ?? [];
                    var uvs = nif.GetUvsForShape(shape)?.ToList() ?? [];
                    var part = new Part
                    {
                        Texture = Normalise(nif.GetTexturePathByIndex(shape, 0) ?? ""),
                        Cutout = nif.GetAlphaProperty(shape) is { } a && (a.flags & 0x200) != 0,
                        P = new Vector3[verts.Count], N = new Vector3[verts.Count], UV = new Vector2[verts.Count],
                        T = new int[tris.Count * 3],
                    };
                    for (int i = 0; i < verts.Count; i++)
                    {
                        var g = verts[i];
                        foreach (var t in chain) g = t.ApplyTransform(g);
                        part.P[i] = new Vector3(g.x, g.y, g.z);
                        var n = new Vector3(0, 0, 1);
                        if (normals.Count == verts.Count)
                        {
                            var d = normals[i];
                            foreach (var t in chain) d = t.ApplyTransformToDir(d);
                            n = new Vector3(d.x, d.y, d.z);
                            n = n.LengthSquared() > 0 ? Vector3.Normalize(n) : new Vector3(0, 0, 1);
                        }
                        part.N[i] = n;
                        part.UV[i] = uvs.Count == verts.Count ? new Vector2(uvs[i].u, uvs[i].v) : Vector2.Zero;
                    }
                    int k = 0;
                    foreach (var t in tris) { part.T[k++] = t.p1; part.T[k++] = t.p2; part.T[k++] = t.p3; }
                    model.Add(part);
                }
                return model.Parts.Count > 0 ? model : null;
            }
            catch { return null; }
            finally { File.Delete(tmp); }
        }
    }

    void Add(Part p)
    {
        Parts.Add(p);
        foreach (var v in p.P) { Min = Vector3.Min(Min, v); Max = Vector3.Max(Max, v); }
    }

    public static string Normalise(string t)
    {
        if (t.Length == 0) return "";
        t = t.Replace('/', '\\').TrimStart('\\').ToLowerInvariant();
        return t.StartsWith("textures\\") ? t : "textures\\" + t;
    }

    /// <summary>
    /// A coarser copy for the distance (vertex clustering): vertices within one <paramref name="grid"/>-unit cube merge,
    /// triangles that collapse go. Small shapes and cut-out shapes are kept as they are; with
    /// <paramref name="dropCutouts"/> cut-out shapes (thin leaves, railings) go, as at the far levels.
    /// </summary>
    public DistantModel Simplified(float grid, bool dropCutouts)
    {
        var copy = new DistantModel();
        foreach (var p in Parts)
        {
            if (p.Cutout) { if (!dropCutouts) copy.Add(p); continue; }
            copy.Add(p.T.Length / 3 < 24 || grid <= 0 ? p : Cluster(p, grid));
        }
        return copy;
    }

    static Part Cluster(Part p, float g)
    {
        var map = new Dictionary<(int, int, int), int>();
        var sum = new List<Vector3>(); var nsum = new List<Vector3>(); var uv = new List<Vector2>(); var count = new List<int>();
        var remap = new int[p.P.Length];
        for (int i = 0; i < p.P.Length; i++)
        {
            var v = p.P[i];
            var key = ((int)MathF.Floor(v.X / g), (int)MathF.Floor(v.Y / g), (int)MathF.Floor(v.Z / g));
            if (!map.TryGetValue(key, out int c))
            {
                map[key] = c = sum.Count;
                sum.Add(Vector3.Zero); nsum.Add(Vector3.Zero); uv.Add(p.UV[i]); count.Add(0);
            }
            sum[c] += v; nsum[c] += p.N[i]; count[c]++;
            remap[i] = c;
        }
        var tris = new List<int>();
        var seen = new HashSet<(int, int, int)>();
        for (int t = 0; t < p.T.Length; t += 3)
        {
            int a = remap[p.T[t]], b = remap[p.T[t + 1]], c = remap[p.T[t + 2]];
            if (a == b || b == c || a == c) continue;
            int lo = Math.Min(a, Math.Min(b, c)), hi = Math.Max(a, Math.Max(b, c)), mid = a + b + c - lo - hi;
            if (!seen.Add((lo, mid, hi))) continue;
            tris.Add(a); tris.Add(b); tris.Add(c);
        }
        var q = new Part { Texture = p.Texture, Cutout = p.Cutout, P = new Vector3[sum.Count], N = new Vector3[sum.Count], UV = [.. uv], T = [.. tris] };
        for (int i = 0; i < sum.Count; i++)
        {
            q.P[i] = sum[i] / count[i];
            q.N[i] = nsum[i].LengthSquared() > 0 ? Vector3.Normalize(nsum[i]) : new Vector3(0, 0, 1);
        }
        return q;
    }
}
