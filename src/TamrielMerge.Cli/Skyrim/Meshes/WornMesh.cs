using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using nifly;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;
using Vector4 = System.Numerics.Vector4;

namespace TamrielMerge.Skyrim.Meshes;

/// <summary>A skeleton's bind pose: each bone's transform to the actor's space (row-vector matrices, v * M) and its
/// parent bone.</summary>
public sealed class BindSkeleton
{
    public Dictionary<string, Matrix4x4> World { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Parent { get; } = new(StringComparer.OrdinalIgnoreCase);
    public bool Has(string bone) => World.ContainsKey(bone);
    public Vector3 Position(string bone) => World[bone].Translation;

    /// <summary>The skeleton of a Skyrim NIF (every named NiNode, its transform through its parents).</summary>
    public static BindSkeleton FromNif(NifFile nif)
    {
        var sk = new BindSkeleton();
        var hdr = nif.GetHeader();
        for (uint id = 0; id < hdr.GetNumBlocks(); id++)
        {
            if (hdr.GetBlockById(id) is not NiNode node) continue;
            string name = node.name.get() ?? "";
            if (name.Length == 0) continue;
            var m = WornMesh.ToMatrix(node.transform);
            for (var parent = nif.GetParentNode(node); parent is not null; parent = nif.GetParentNode(parent)) m *= WornMesh.ToMatrix(parent.transform);
            sk.World[name] = WornMesh.Orthonormal(m);
            if (nif.GetParentNode(node) is { } up && up.name.get() is { Length: > 0 } upName) sk.Parent[name] = upName;
        }
        return sk;
    }
}

/// <summary>A worn shape in a skeleton's bind space: its geometry, its weights by bone name, and how it draws.</summary>
public sealed class WornShape
{
    public required string Name;
    public required Vector3[] V;
    public required Vector3[] N;
    public required Vector2[] Uv;
    /// <summary>Triangles as index triplets.</summary>
    public required int[] Tris;
    /// <summary>Up to four bones per vertex, by name, weights summing to 1.</summary>
    public required (string Bone, float W)[][] Weights;
    /// <summary>Texture slots (data-relative, textures\...): diffuse, normal, glow, ...; empty slots "".</summary>
    public string[] Textures = [];
    public uint ShaderType;
    /// <summary>Skinned, ZBuffer test, remappable textures, cast and receive shadows (vanilla armour); vertex alpha when used.</summary>
    public uint Flags1 = 0x82400302;
    public uint Flags2 = 0x8001;
    public float Glossiness = 10, SpecularStrength, Alpha = 1, SubsurfaceRolloff, RimlightPower, BacklightPower;
    public Vector3 SpecularColor = Vector3.One, Emissive;
    public float EmissiveMultiple;
    public uint Clamp = 3;
    public ushort? AlphaFlags;
    public byte AlphaThreshold;
    /// <summary>A copy of a vanilla body or hands shape (the wearer's skin, drawn where the armour leaves it bare).</summary>
    public bool IsSkin;

    public WornShape With(Vector3[]? v = null, Vector3[]? n = null) => new()
    {
        Name = Name, V = v ?? V, N = n ?? N, Uv = Uv, Tris = Tris, Weights = Weights, Textures = Textures, ShaderType = ShaderType,
        Flags1 = Flags1, Flags2 = Flags2, Glossiness = Glossiness, SpecularStrength = SpecularStrength, Alpha = Alpha,
        SubsurfaceRolloff = SubsurfaceRolloff, RimlightPower = RimlightPower, BacklightPower = BacklightPower, SpecularColor = SpecularColor,
        Emissive = Emissive, EmissiveMultiple = EmissiveMultiple, Clamp = Clamp, AlphaFlags = AlphaFlags, AlphaThreshold = AlphaThreshold, IsSkin = IsSkin,
    };
}

/// <summary>
/// Worn armour and clothing for Skyrim from games built on the Biped skeleton ("Bip01 ..."): Morrowind's and Oblivion's
/// parts, which are skinned or attached to that skeleton, re-rigged onto Skyrim's ("NPC ... [...]").
/// <list type="bullet">
/// <item>Re-rigging (<see cref="Retarget"/>): every source bone maps to the Skyrim bone of the same name (Morrowind's
/// three-finger hands to Skyrim's five: each finger is shared by the two Skyrim fingers it stands for). Each bone moves
/// its geometry rigidly from its own bind to the Skyrim bone's, turned by the difference of the two bones' frames
/// (the minimal turn between the bones' directions where the frames disagree) and stretched along the bone to the
/// Skyrim bone's length, so limbs keep their shape at the joints and take Skyrim's proportions; vertices blend their
/// bones' moves by weight, as skinning does.</item>
/// <item>Fitting (<see cref="FitOutside"/>): parts worn on the body are pushed out of Skyrim's own body (or hands or
/// feet) wherever they sink into it, to a small margin by layer (underwear, clothes, armour), and the push is smoothed
/// over the mesh so it bends rather than dents. Done against each body weight (_0 thin, _1 heavy).</item>
/// <item>Writing (<see cref="Write"/>): a vanilla-style armour NIF: NiNode root, the bones as nodes, each shape a
/// skinned BSTriShape with BSDismemberSkinInstance (its slot as partition), NiSkinData and NiSkinPartition.</item>
/// </list>
/// </summary>
public static class WornMesh
{
    // ---- matrices (row vectors, v * M, as System.Numerics) ----

    /// <summary>A nifly transform as a row-vector matrix, from where it takes the origin and the axes.</summary>
    public static Matrix4x4 ToMatrix(MatTransform t)
    {
        var o = t.ApplyTransform(new nifly.Vector3(0, 0, 0));
        var x = t.ApplyTransform(new nifly.Vector3(1, 0, 0));
        var y = t.ApplyTransform(new nifly.Vector3(0, 1, 0));
        var z = t.ApplyTransform(new nifly.Vector3(0, 0, 1));
        return new Matrix4x4(x.x - o.x, x.y - o.y, x.z - o.z, 0, y.x - o.x, y.y - o.y, y.z - o.z, 0, z.x - o.x, z.y - o.y, z.z - o.z, 0, o.x, o.y, o.z, 1);
    }

    /// <summary>A row-vector matrix (rotation and translation, no scale) as a nifly transform.</summary>
    public static MatTransform ToTransform(Matrix4x4 m) => new()
    {
        translation = new nifly.Vector3(m.M41, m.M42, m.M43),
        // nifly: p' = R p (columns are the turned axes) = the row-vector matrix's rows.
        rotation = new Matrix3(m.M11, m.M21, m.M31, m.M12, m.M22, m.M32, m.M13, m.M23, m.M33),
        scale = 1,
    };

    /// <summary>The matrix with its axes made unit length and square to each other (bind poses carry no scale).</summary>
    public static Matrix4x4 Orthonormal(Matrix4x4 m)
    {
        var x = Vector3.Normalize(new Vector3(m.M11, m.M12, m.M13));
        var y = new Vector3(m.M21, m.M22, m.M23);
        y = Vector3.Normalize(y - x * Vector3.Dot(x, y));
        var z = Vector3.Cross(x, y);
        if (Vector3.Dot(z, new Vector3(m.M31, m.M32, m.M33)) < 0) z = -z;   // keep a mirror a mirror
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, m.M41, m.M42, m.M43, 1);
    }

    // ---- bones ----

    /// <summary>The bone each Biped bone continues into (its length and direction run to it).</summary>
    static string? NextOf(string bip)
    {
        string side = bip.Contains(" L ") ? "L" : bip.Contains(" R ") ? "R" : "";
        string part = bip.Replace("Bip01 ", "").Replace("L ", "").Replace("R ", "").Trim();
        string P(string p) => side.Length > 0 ? $"Bip01 {side} {p}" : $"Bip01 {p}";
        return part switch
        {
            "Pelvis" => "Bip01 Spine", "Spine" => "Bip01 Spine1", "Spine1" => "Bip01 Spine2", "Spine2" => "Bip01 Neck", "Neck" => "Bip01 Head",
            "Clavicle" => P("UpperArm"), "UpperArm" => P("Forearm"), "Forearm" => P("Hand"),
            "Thigh" => P("Calf"), "Calf" => P("Foot"), "Foot" => P("Toe0"),
            // FingerN runs to its next joint FingerN1, FingerNj to FingerN(j+1).
            _ when System.Text.RegularExpressions.Regex.Match(part, @"^Finger(\d)(\d?)$") is { Success: true } f
                => P($"Finger{f.Groups[1].Value}{(f.Groups[2].Value.Length == 0 ? 1 : f.Groups[2].Value[0] - '0' + 1)}"),
            _ => null,
        };
    }

    /// <summary>The Skyrim bones a Biped bone stands for, with their shares: "Bip01 L UpperArm" is "NPC L UpperArm
    /// [LUar]"; Morrowind's three-finger hands' fingers 1 and 2 are Skyrim's index+middle and ring+little fingers (a
    /// five-finger source, Oblivion's, maps finger to finger); twist bones go to Skyrim's first twist bone; "Bip01"
    /// itself (the root's frame) is the pelvis's.</summary>
    public static (string Bone, float Share)[] SkyrimBones(string bip, BindSkeleton skyrim, bool fiveFingers = false)
    {
        string name = bip.Replace("Bip01", "").Trim();
        if (name.Length == 0) name = "Pelvis";
        string side = name.StartsWith("L ") ? "L" : name.StartsWith("R ") ? "R" : "";
        string part = side.Length > 0 ? name[2..] : name;
        string? Find(string skyrimPart)
        {
            string prefix = side.Length > 0 ? $"NPC {side} {skyrimPart} [" : $"NPC {skyrimPart} [";
            return skyrim.World.Keys.FirstOrDefault(k => k.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        }
        (string, float)[] One(string? b) => b is null ? [] : [(b, 1f)];
        (string, float)[] Two(string a, string b) => Find(a) is { } x && Find(b) is { } y ? [(x, 0.5f), (y, 0.5f)] : One(Find(a) ?? Find(b));
        var m = System.Text.RegularExpressions.Regex.Match(part, @"^Finger(\d)(\d?)$");
        if (m.Success)
        {
            int finger = m.Groups[1].Value[0] - '0';
            int joint = m.Groups[2].Value.Length > 0 ? m.Groups[2].Value[0] - '0' : 0;
            if (fiveFingers) return One(Find($"Finger{finger}{joint}"));
            return finger switch
            {
                0 => One(Find($"Finger0{joint}") ?? Find("Finger00")),
                1 => Two($"Finger1{joint}", $"Finger2{joint}"),
                _ => Two($"Finger3{joint}", $"Finger4{joint}"),
            };
        }
        if (part.Contains("Twist", StringComparison.OrdinalIgnoreCase))
            return One(part.StartsWith("Fore", StringComparison.OrdinalIgnoreCase) ? Find("ForearmTwist1") : Find("UpperarmTwist1") ?? Find("UpperArmTwist1"));
        if (part.Equals("Neck1", StringComparison.OrdinalIgnoreCase)) part = "Neck";
        if (part.Equals("Toe0", StringComparison.OrdinalIgnoreCase)) return One(Find("Toe0"));
        return One(Find(part));
    }

    /// <summary>How one source bone's geometry moves onto Skyrim's skeleton (row-vector affine map), its turn, the
    /// Skyrim bones it becomes, and how much it stretches along the bone.</summary>
    sealed record BoneMove(Matrix4x4 Map, Matrix4x4 Rotation, (string Bone, float Share)[] Targets, float Stretch);

    /// <summary>
    /// A bone's anatomical frame (rows: x along the bone, then two axes square to it): built the same way on both
    /// skeletons, so the turn between them is the turn between the bones whatever each skeleton's own axis convention
    /// (Morrowind's Bip01 frames and Skyrim's NPC frames differ by a quarter turn). The roll reference is the body's
    /// forward (+Y; both games face +Y), or up (+Z) for bones that themselves run forward (feet).
    /// </summary>
    static Matrix4x4 Frame(Vector3 dir, bool up)
    {
        var x = Vector3.Normalize(dir);
        var reference = up ? Vector3.UnitZ : Vector3.UnitY;
        var z = reference - x * Vector3.Dot(reference, x);
        z = z.LengthSquared() > 1e-6f ? Vector3.Normalize(z) : Vector3.Normalize(Vector3.Cross(x, Vector3.UnitX));
        var y = Vector3.Cross(z, x);
        return new Matrix4x4(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, 0, 0, 0, 1);
    }

    /// <summary>Where a bone runs to: its next bone's position (the fingers' middle for a hand); null for an end bone.</summary>
    static (Vector3 From, Vector3 To)? Run(string bone, BindSkeleton from, BindSkeleton to, Func<string, Vector3?> target)
    {
        if (!from.Has(bone) || target(bone) is not { } P) return null;
        var nexts = bone.EndsWith(" Hand", StringComparison.OrdinalIgnoreCase)
            ? new[] { bone.Replace(" Hand", " Finger1"), bone.Replace(" Hand", " Finger2") }
            : NextOf(bone) is { } n ? [n] : [];
        var found = nexts.Where(n => from.Has(n) && target(n) is not null).ToList();
        if (found.Count == 0) return null;
        var c = found.Aggregate(Vector3.Zero, (a, n) => a + from.Position(n)) / found.Count;
        var C = found.Aggregate(Vector3.Zero, (a, n) => a + target(n)!.Value) / found.Count;
        return (c - from.Position(bone), C - P);
    }

    /// <summary>The moves of the source bones onto Skyrim's (see the class summary). A bone that runs to a next bone
    /// turns by the difference of the two bones' anatomical frames and stretches along itself to the Skyrim bone's
    /// length; an end bone (head, toes, finger tips) turns with the bone above it.</summary>
    static Dictionary<string, BoneMove> Moves(BindSkeleton from, BindSkeleton to, IEnumerable<string> bones)
    {
        var result = new Dictionary<string, BoneMove>(StringComparer.OrdinalIgnoreCase);
        var targetsOf = new Dictionary<string, (string Bone, float Share)[]>(StringComparer.OrdinalIgnoreCase);
        bool five = from.World.Keys.Any(k => k.Contains("Finger3", StringComparison.OrdinalIgnoreCase));
        (string Bone, float Share)[] Targets(string b) => targetsOf.TryGetValue(b, out var t) ? t : targetsOf[b] = SkyrimBones(b, to, five);
        Vector3? Target(string b) => Targets(b) is { Length: > 0 } t ? t.Aggregate(Vector3.Zero, (a, x) => a + to.Position(x.Bone) * x.Share) : null;
        var turns = new Dictionary<string, (Matrix4x4 Turn, Vector3 Axis, float Stretch)?>(StringComparer.OrdinalIgnoreCase);
        (Matrix4x4 Turn, Vector3 Axis, float Stretch)? TurnOf(string bone, int depth = 0)
        {
            if (turns.TryGetValue(bone, out var hit)) return hit;
            (Matrix4x4, Vector3, float)? r = null;
            if (Run(bone, from, to, Target) is var (d, D) && d.LengthSquared() > 1e-4f && D.LengthSquared() > 1e-4f)
            {
                bool up = MathF.Abs(Vector3.Dot(Vector3.Normalize(d), Vector3.UnitY)) > 0.7f || MathF.Abs(Vector3.Dot(Vector3.Normalize(D), Vector3.UnitY)) > 0.7f;
                var turn = Matrix4x4.Transpose(Frame(d, up)) * Frame(D, up);
                r = (turn, Vector3.Normalize(d), Math.Clamp(D.Length() / d.Length(), 0.6f, 1.6f));
            }
            else if (depth < 32 && from.Parent.TryGetValue(bone, out var parent) && TurnOf(parent, depth + 1) is { } up)
                r = (up.Turn, Vector3.Zero, 1);
            return turns[bone] = r;
        }
        foreach (var bone in bones.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!from.Has(bone) || Targets(bone) is not { Length: > 0 } targets) continue;
            Vector3 p = from.Position(bone), P = Target(bone)!.Value;
            var (rotation, axis, stretch) = TurnOf(bone) ?? (Matrix4x4.Identity, Vector3.Zero, 1);
            // v' = P + stretched(v - p) * turn: along the bone by the length ratio, across it unchanged.
            var scale = axis == Vector3.Zero ? Matrix4x4.Identity
                : Matrix4x4.Identity + new Matrix4x4(axis.X * axis.X, axis.X * axis.Y, axis.X * axis.Z, 0, axis.Y * axis.X, axis.Y * axis.Y, axis.Y * axis.Z, 0, axis.Z * axis.X, axis.Z * axis.Y, axis.Z * axis.Z, 0, 0, 0, 0, 0) * (stretch - 1);
            var map = Matrix4x4.CreateTranslation(-p) * scale * rotation * Matrix4x4.CreateTranslation(P);
            result[bone] = new BoneMove(map, rotation, targets, stretch);
        }
        return result;
    }
    /// <summary>A report of how each Biped bone lines up with Skyrim's (dev aid): positions, frame error, stretch.</summary>
    public static IEnumerable<string> Report(BindSkeleton from, BindSkeleton to)
    {
        var moves = Moves(from, to, from.World.Keys.Where(k => k.StartsWith("Bip01", StringComparison.OrdinalIgnoreCase)));
        foreach (var (bone, m) in moves.OrderBy(k => k.Key))
        {
            var p = from.Position(bone);
            var mapped = Vector3.Transform(p, m.Map);
            yield return $"{bone,-22} -> {string.Join("+", m.Targets.Select(t => t.Bone)),-46} at {p:F1} -> {mapped:F1} (Skyrim {to.Position(m.Targets[0].Bone):F1}) stretch {m.Stretch:F2}";
        }
    }

    /// <summary>
    /// Shapes rigged to a Biped skeleton (in its bind space, weights by Biped bone) moved onto Skyrim's skeleton and
    /// re-weighted to its bones; a vertex on bones Skyrim lacks follows the nearest ancestor that maps.
    /// </summary>
    public static List<WornShape> Retarget(IEnumerable<WornShape> shapes, BindSkeleton from, BindSkeleton to, List<string> problems)
    {
        var list = shapes.ToList();
        var moves = Moves(from, to, list.SelectMany(s => s.Weights.SelectMany(w => w.Select(x => x.Bone))).Concat(from.World.Keys));
        BoneMove? MoveOf(string bone)
        {
            for (int guard = 0; guard < 32 && bone is not null; guard++)
            {
                if (moves.TryGetValue(bone, out var m)) return m;
                if (!from.Parent.TryGetValue(bone, out var up)) break;
                bone = up;
            }
            return null;
        }
        var result = new List<WornShape>();
        foreach (var s in list)
        {
            var v = new Vector3[s.V.Length];
            var n = new Vector3[s.V.Length];
            var weights = new (string, float)[s.V.Length][];
            int lost = 0;
            for (int i = 0; i < s.V.Length; i++)
            {
                Vector3 pos = Vector3.Zero, nrm = Vector3.Zero;
                var acc = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
                float total = 0;
                foreach (var (bone, w) in s.Weights[i])
                {
                    if (w <= 0 || MoveOf(bone) is not { } m) continue;
                    pos += Vector3.Transform(s.V[i], m.Map) * w;
                    if (s.N.Length == s.V.Length) nrm += Vector3.TransformNormal(s.N[i], m.Rotation) * w;
                    foreach (var (t, share) in m.Targets) acc[t] = acc.GetValueOrDefault(t) + w * share;
                    total += w;
                }
                if (total <= 0) { lost++; pos = s.V[i]; acc[to.World.Keys.First(k => k.StartsWith("NPC Pelvis", StringComparison.OrdinalIgnoreCase))] = 1; total = 1; }
                v[i] = pos / total;
                n[i] = nrm.LengthSquared() > 0 ? Vector3.Normalize(nrm) : Vector3.UnitZ;
                var top = acc.OrderByDescending(kv => kv.Value).Take(4).ToList();
                float sum = top.Sum(kv => kv.Value);
                weights[i] = top.Select(kv => (kv.Key, kv.Value / sum)).ToArray();
            }
            if (lost > 0) problems.Add($"{s.Name}: {lost} vertices on bones Skyrim lacks (kept on the pelvis)");
            var moved = s.With(v, n);
            moved.Weights = weights;
            result.Add(moved);
        }
        return result;
    }

    // ---- fitting ----

    /// <summary>
    /// Pushes the shapes out of the surface (Skyrim's body, hands or feet, in the same bind space) wherever a vertex is
    /// within the surface's reach and less than <paramref name="margin"/> outside it, along the surface's normal there;
    /// the pushes are then smoothed over each shape's own edges (two passes) so the cloth bends rather than dents.
    /// Returns how many vertices moved.
    /// </summary>
    public static int FitOutside(List<WornShape> shapes, WornSurface surface, float margin)
    {
        int moved = 0;
        foreach (var s in shapes)
        {
            var push = new Vector3[s.V.Length];
            for (int i = 0; i < s.V.Length; i++)
            {
                var p = s.V[i];
                if (surface.Nearest(p) is not { } hit) continue;
                var (q, qn) = hit;
                float d = Vector3.Dot(p - q, qn);
                if (d < margin - 0.01f) push[i] = qn * (margin - d);
            }
            // Smooth the pushes along the shape's edges (each vertex takes the most of itself and its neighbours'
            // average, so a push spreads to the vertices around it instead of making a dent).
            var neighbours = new List<int>[s.V.Length];
            for (int i = 0; i < s.V.Length; i++) neighbours[i] = [];
            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
                for (int k = 0; k < 3; k++) { neighbours[s.Tris[t + k]].Add(s.Tris[t + (k + 1) % 3]); neighbours[s.Tris[t + k]].Add(s.Tris[t + (k + 2) % 3]); }
            for (int pass = 0; pass < 2; pass++)
            {
                var next = new Vector3[push.Length];
                for (int i = 0; i < push.Length; i++)
                {
                    var avg = neighbours[i].Count > 0 ? neighbours[i].Aggregate(Vector3.Zero, (a, j) => a + push[j]) / neighbours[i].Count : Vector3.Zero;
                    // Never less than the vertex's own push (it must clear the body), else blended towards its neighbours'.
                    var blended = push[i] * 0.5f + avg * 0.5f;
                    next[i] = blended.LengthSquared() >= push[i].LengthSquared() ? blended : push[i];
                }
                push = next;
            }
            for (int i = 0; i < s.V.Length; i++)
                if (push[i].LengthSquared() > 1e-8f) { s.V[i] += push[i]; if (push[i].LengthSquared() > 0.0025f) moved++; }
        }
        return moved;
    }

    /// <summary>The closest point on a triangle to p, with its barycentric weights for b and c.</summary>
    internal static (Vector3 P, float U, float W) Closest(Vector3 p, Vector3 a, Vector3 b, Vector3 c)
    {
        // Ericson, Real-Time Collision Detection 5.1.5.
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return (a, 0, 0);
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return (b, 1, 0);
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0) { float v = d1 / (d1 - d3); return (a + v * ab, v, 0); }
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return (c, 0, 1);
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0) { float w = d2 / (d2 - d6); return (a + w * ac, 0, w); }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0) { float w = (d4 - d3) / ((d4 - d3) + (d5 - d6)); return (b + w * (c - b), 1 - w, w); }
        float denom = 1 / (va + vb + vc);
        float vv = vb * denom, ww = vc * denom;
        return (a + ab * vv + ac * ww, vv, ww);
    }

    // ---- first person ----

    static bool IsArmBone(string bone) =>
        bone.Contains("Clavicle", StringComparison.OrdinalIgnoreCase) || bone.Contains("UpperArm", StringComparison.OrdinalIgnoreCase)
        || bone.Contains("Upperarm", StringComparison.OrdinalIgnoreCase) || bone.Contains("Forearm", StringComparison.OrdinalIgnoreCase)
        || bone.Contains(" Hand", StringComparison.OrdinalIgnoreCase) || bone.Contains("Finger", StringComparison.OrdinalIgnoreCase)
        || bone.Contains("Thumb", StringComparison.OrdinalIgnoreCase);

    /// <summary>What the first-person view draws of the shapes: the triangles on the arms and hands (each vertex's
    /// heaviest bone an arm, hand or finger bone), unused vertices dropped; shapes with none left out.</summary>
    public static List<WornShape> FirstPerson(IEnumerable<WornShape> shapes)
    {
        var result = new List<WornShape>();
        foreach (var s in shapes)
        {
            bool Arm(int i) => s.Weights[i].Length > 0 && IsArmBone(s.Weights[i].MaxBy(w => w.W).Bone);
            var keep = new List<int>();
            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
                if (Arm(s.Tris[t]) && Arm(s.Tris[t + 1]) && Arm(s.Tris[t + 2])) keep.AddRange([s.Tris[t], s.Tris[t + 1], s.Tris[t + 2]]);
            if (keep.Count == 0) continue;
            var remap = new Dictionary<int, int>();
            foreach (int i in keep) remap.TryAdd(i, remap.Count);
            var order = remap.OrderBy(kv => kv.Value).Select(kv => kv.Key).ToArray();
            var cut = s.With(order.Select(i => s.V[i]).ToArray(), s.N.Length == s.V.Length ? order.Select(i => s.N[i]).ToArray() : []);
            cut.Uv = order.Select(i => i < s.Uv.Length ? s.Uv[i] : Vector2.Zero).ToArray();
            cut.Weights = order.Select(i => s.Weights[i]).ToArray();
            cut.Tris = keep.Select(i => remap[i]).ToArray();
            result.Add(cut);
        }
        return result;
    }

    // ---- reading vanilla meshes ----

    /// <summary>A vanilla skinned NIF's shapes in the skeleton's bind space (each vertex through its bones' skin-to-bone
    /// transforms and the skeleton), with their textures and shader settings.</summary>
    public static List<WornShape> ReadSkinned(NifFile nif, BindSkeleton skeleton)
    {
        var result = new List<WornShape>();
        var hdr = nif.GetHeader();
        using var all = nif.GetShapes();
        // A BlockCache spends the header wrapper it is given (later calls through it crash): always a fresh one.
        using var cache = new niflycpp.BlockCache(nif.GetHeader());
        for (int si = 0; si < all.Count; si++)
        {
            var shape = all[si];
            using var names = new vectorstring();
            nif.GetShapeBoneList(shape, names);
            var bones = names.ToList();
            var tri = cache.EditableBlockById<BSTriShape>(hdr.GetBlockID(shape));
            var verts = nif.GetVertsForShape(shape);
            if (tri is null || bones.Count == 0 || tri.vertData.Count != verts.Count) continue;
            var toWorld = new Matrix4x4[bones.Count];
            for (int b = 0; b < bones.Count; b++)
            {
                var t = new MatTransform();
                nif.GetShapeTransformSkinToBone(shape, (uint)b, t);
                toWorld[b] = skeleton.World.TryGetValue(bones[b], out var w) ? ToMatrix(t) * w : Matrix4x4.Identity;
            }
            var normals = nif.GetNormalsForShape(shape);
            var uvs = nif.GetUvsForShape(shape);
            int n = verts.Count;
            var v = new Vector3[n]; var nr = new Vector3[n]; var uv = new Vector2[n];
            var weights = new (string, float)[n][];
            for (int i = 0; i < n; i++)
            {
                var vd = tri.vertData[i];
                var w = vd.weights.ToArray(); var wb = vd.weightBones.ToArray();
                var list = new List<(string, float)>();
                Vector3 p = new(verts[i].x, verts[i].y, verts[i].z), pos = Vector3.Zero, nn = Vector3.Zero;
                Vector3 nrm = normals is { Count: > 0 } && i < normals.Count ? new Vector3(normals[i].x, normals[i].y, normals[i].z) : Vector3.UnitZ;
                float total = 0;
                for (int k = 0; k < 4; k++)
                    if (w[k] > 0 && wb[k] < bones.Count)
                    {
                        pos += Vector3.Transform(p, toWorld[wb[k]]) * w[k];
                        nn += Vector3.TransformNormal(nrm, toWorld[wb[k]]) * w[k];
                        list.Add((bones[wb[k]], w[k]));
                        total += w[k];
                    }
                v[i] = total > 0 ? pos / total : p;
                nr[i] = nn.LengthSquared() > 0 ? Vector3.Normalize(nn) : nrm;
                uv[i] = uvs is { Count: > 0 } && i < uvs.Count ? new Vector2(uvs[i].u, uvs[i].v) : Vector2.Zero;
                weights[i] = total > 0 ? list.Select(x => (x.Item1, x.Item2 / total)).ToArray() : [(bones[0], 1f)];
            }
            using var t3 = new vectorTriangle();
            shape.GetTriangles(t3);
            var tris = new int[t3.Count * 3];
            for (int k = 0; k < t3.Count; k++) { tris[k * 3] = t3[k].p1; tris[k * 3 + 1] = t3[k].p2; tris[k * 3 + 2] = t3[k].p3; }
            // Normals from the bound geometry itself (nifly hands out none for many SSE shapes): area-weighted face normals
            // of the counter-clockwise front faces, so they point out of the body.
            if (normals is not { Count: > 0 } || nr.All(x => MathF.Abs(x.Z - 1) < 1e-4f))
            {
                nr = new Vector3[n];
                for (int k = 0; k + 2 < tris.Length; k += 3)
                {
                    var f = Vector3.Cross(v[tris[k + 1]] - v[tris[k]], v[tris[k + 2]] - v[tris[k]]);
                    nr[tris[k]] += f; nr[tris[k + 1]] += f; nr[tris[k + 2]] += f;
                }
                // Vertices split at UV seams share one normal, so the seams don't show.
                foreach (var g in Enumerable.Range(0, n).GroupBy(k => (MathF.Round(v[k].X * 100), MathF.Round(v[k].Y * 100), MathF.Round(v[k].Z * 100))))
                    if (g.Count() > 1) { var sum = g.Aggregate(Vector3.Zero, (a, k) => a + nr[k]); foreach (int k in g) nr[k] = sum; }
                for (int k = 0; k < n; k++) nr[k] = nr[k].LengthSquared() > 1e-12f ? Vector3.Normalize(nr[k]) : Vector3.UnitZ;
            }
            var ws = new WornShape { Name = shape.name.get() ?? $"Shape{result.Count}", V = v, N = nr, Uv = uv, Tris = tris, Weights = weights };
            if (nif.GetShader(shape) is { } shader && cache.EditableBlockById<BSLightingShaderProperty>(hdr.GetBlockID(shader)) is { } sp)
            {
                ws.ShaderType = sp.GetShaderType();
                ws.Flags1 = sp.shaderFlags1; ws.Flags2 = sp.shaderFlags2;
                ws.Glossiness = sp.glossiness; ws.SpecularStrength = sp.specularStrength; ws.Alpha = sp.alpha;
                ws.SpecularColor = new Vector3(sp.specularColor.x, sp.specularColor.y, sp.specularColor.z);
                ws.SubsurfaceRolloff = sp.subsurfaceRolloff; ws.RimlightPower = sp.rimlightPower; ws.BacklightPower = sp.backlightPower;
                ws.Textures = Enumerable.Range(0, 9).Select(k => nif.GetTexturePathByIndex(shape, (uint)k) ?? "").ToArray();
                ws.IsSkin = ws.ShaderType == 5;
            }
            if (nif.GetAlphaProperty(shape) is { } alpha) { ws.AlphaFlags = alpha.flags; ws.AlphaThreshold = alpha.threshold; }
            result.Add(ws);
        }
        return result;
    }

    // ---- writing ----

    /// <summary>
    /// Writes a vanilla-style skinned armour NIF of the shapes (in <paramref name="skeleton"/>'s bind space, weights by
    /// Skyrim bone): NiNode root, each used bone a node under it at its bind transform, each shape a BSTriShape skinned
    /// to those bones (skin-to-bone = the bone's bind inverted) with one dismember partition, <paramref name="partition"/>
    /// (the biped slot). nifly isn't thread-safe: callers serialise writes.
    /// </summary>
    public static void Write(string path, IReadOnlyList<WornShape> shapes, BindSkeleton skeleton, int partition)
    {
        using var nif = new NifFile();
        nif.Create(NiVersion.getSSE());
        var hdr = nif.GetHeader();
        var root = nif.GetRootNode();
        root.name = new NiStringRef(Path.GetFileName(path));
        var used = shapes.SelectMany(s => s.Weights.SelectMany(w => w.Select(x => x.Bone))).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(skeleton.Has).OrderBy(b => b, StringComparer.OrdinalIgnoreCase).ToList();
        // Bone nodes by block ID (taken at once: nifly's wrappers of new blocks mustn't be kept).
        var nodes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in used) nodes[b] = hdr.GetBlockID(BlockMeshBuilder.Owned(nif.AddNode(b, ToTransform(skeleton.World[b]), root)));
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in shapes)
        {
            if (s.V.Length == 0 || s.V.Length > 65535 || s.Tris.Length < 3) continue;
            using var vv = new vectorVector3(); using var uu = new vectorVector2(); using var nn = new vectorVector3(); using var tt = new vectorTriangle();
            foreach (var p in s.V) vv.Add(new nifly.Vector3(p.X, p.Y, p.Z));
            for (int i = 0; i < s.V.Length; i++) { var t = i < s.Uv.Length ? s.Uv[i] : Vector2.Zero; uu.Add(new nifly.Vector2(t.X, t.Y)); }
            bool hasNormals = s.N.Length == s.V.Length;
            if (hasNormals) foreach (var p in s.N) nn.Add(new nifly.Vector3(p.X, p.Y, p.Z));
            for (int t = 0; t + 2 < s.Tris.Length; t += 3) tt.Add(new Triangle((ushort)s.Tris[t], (ushort)s.Tris[t + 1], (ushort)s.Tris[t + 2]));
            string name = s.Name;
            for (int k = 2; !names.Add(name); k++) name = $"{s.Name}:{k}";
            // nifly's wrappers of blocks it made may think they own them: never let a finalizer free one.
            var shape = BlockMeshBuilder.Owned(hasNormals ? nif.CreateShapeFromData(name, vv, tt, uu, nn) : nif.CreateShapeFromData(name, vv, tt, uu));
            if (!hasNormals) nif.CalcNormalsForShape(shape);
            for (int k = 0; k < s.Textures.Length; k++) if (s.Textures[k].Length > 0) nif.SetTextureSlot(shape, s.Textures[k], (uint)k);
            nif.CalcTangentsForShape(shape);
            // Shader first (the skinning steps below rebuild blocks the shape points at).
            if (nif.GetShader(shape) is not { } shader) { nif.DeleteShape(shape); continue; }
            BlockMeshBuilder.Owned(shader);
            uint shaderId = hdr.GetBlockID(shader);
            if (s.AlphaFlags is { } af) nif.AssignAlphaProperty(shape, BlockMeshBuilder.Owned(new nifly.NiAlphaProperty { flags = af, threshold = s.AlphaThreshold }));
            BlockMeshBuilder.EditBlock<BSLightingShaderProperty>(nif, shaderId, sp =>
            {
                if (s.ShaderType != 0) sp.SetShaderType(s.ShaderType);
                sp.shaderFlags1 = s.Flags1 | 0x2u;   // always skinned
                sp.shaderFlags2 = s.Flags2;
                sp.glossiness = s.Glossiness;
                sp.specularStrength = s.SpecularStrength;
                sp.specularColor = new nifly.Vector3(s.SpecularColor.X, s.SpecularColor.Y, s.SpecularColor.Z);
                sp.alpha = s.Alpha;
                sp.emissiveColor = new nifly.Vector3(s.Emissive.X, s.Emissive.Y, s.Emissive.Z);
                sp.emissiveMultiple = s.EmissiveMultiple;
                sp.textureClampMode = (TexClampMode)s.Clamp;
                if (s.IsSkin) { sp.subsurfaceRolloff = s.SubsurfaceRolloff; sp.rimlightPower = s.RimlightPower; sp.backlightPower = s.BacklightPower; }
            });

            // Skinning: the bones this shape uses, each bound at its bind pose.
            var shapeBones = s.Weights.SelectMany(w => w.Select(x => x.Bone)).Distinct(StringComparer.OrdinalIgnoreCase).Where(nodes.ContainsKey).ToList();
            if (shapeBones.Count == 0) { nif.DeleteShape(shape); continue; }
            var slot = shapeBones.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i, StringComparer.OrdinalIgnoreCase);
            nif.CreateSkinning(shape);
            using (var ids = new vectorint()) { foreach (var b in shapeBones) ids.Add((int)nodes[b]); nif.SetShapeBoneIDList(shape, ids); }
            nif.SetShapeTransformGlobalToSkin(shape, ToTransform(Matrix4x4.Identity));
            var inverse = new Matrix4x4[shapeBones.Count];
            for (int i = 0; i < shapeBones.Count; i++)
            {
                Matrix4x4.Invert(skeleton.World[shapeBones[i]], out inverse[i]);
                nif.SetShapeTransformSkinToBone(shape, (uint)i, ToTransform(inverse[i]));
            }
            for (int v = 0; v < s.V.Length; v++)
            {
                using var bids = new vectoruchar(); using var ws = new vectorfloat();
                foreach (var (b, w) in s.Weights[v]) if (slot.TryGetValue(b, out int bi) && w > 0) { bids.Add((byte)bi); ws.Add(w); }
                if (bids.Count == 0) { bids.Add(0); ws.Add(1); }
                nif.SetShapeVertWeights(name, (ushort)v, bids, ws);
            }
            // The skin data's per-bone weights and bounds (in the bone's own space).
            uint skinId = shape.SkinInstanceRef().index;
            if (skinId != uint.MaxValue)
            {
                uint dataId;
                using (var cache = new niflycpp.BlockCache(nif.GetHeader())) dataId = cache.EditableBlockById<NiSkinInstance>(skinId)?.dataRef.index ?? uint.MaxValue;
                if (dataId != uint.MaxValue)
                    BlockMeshBuilder.EditBlock<NiSkinData>(nif, dataId, sd =>
                    {
                        sd.hasVertWeights = 1;
                        var boneList = sd.bones;
                        for (int i = 0; i < shapeBones.Count && i < boneList.Count; i++)
                        {
                            var bd = boneList[i];
                            var list = new vectorSkinWeight();
                            var pts = new List<Vector3>();
                            for (int v = 0; v < s.V.Length; v++)
                                foreach (var (b, w) in s.Weights[v])
                                    if (w > 0 && slot.TryGetValue(b, out int bi) && bi == i)
                                    {
                                        list.Add(new SkinWeight { index = (ushort)v, weight = w });
                                        pts.Add(Vector3.Transform(s.V[v], inverse[i]));
                                    }
                            bd.vertexWeights = list;
                            bd.numVertices = (ushort)list.Count;
                            var c = pts.Count > 0 ? pts.Aggregate(Vector3.Zero, (a, p) => a + p) / pts.Count : Vector3.Zero;
                            bd.bounds = new BoundingSphere { center = new nifly.Vector3(c.X, c.Y, c.Z), radius = pts.Count > 0 ? pts.Max(p => Vector3.Distance(p, c)) : 0 };
                            boneList[i] = bd;
                        }
                        sd.bones = boneList;
                    });
            }
            nif.UpdateSkinPartitions(shape);
            using (var parts = new NiVectorBSDismemberSkinInstancePartitionInfo())
            using (var triParts = new vectorint())
            {
                parts.push_back(new BSDismemberSkinInstance.PartitionInfo { partID = (ushort)partition, flags = PartitionFlags.PF_EDITOR_VISIBLE | PartitionFlags.PF_START_NET_BONESET });
                for (int t = 0; t < s.Tris.Length / 3; t++) triParts.Add(0);
                nif.SetShapePartitions(shape, parts, triParts, true);
            }
            nif.UpdateSkinPartitions(shape);

        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (nif.Save(path) != 0) throw new IOException($"nifly failed to save {path}");
    }
}

/// <summary>A surface to fit worn shapes outside of (<see cref="WornMesh.FitOutside"/>): its triangles with vertex
/// normals, in a grid of cells as large as the reach, for nearest-point queries.</summary>
public sealed class WornSurface
{
    readonly List<(Vector3 A, Vector3 B, Vector3 C, Vector3 Na, Vector3 Nb, Vector3 Nc)> tris = [];
    readonly Dictionary<(int, int, int), List<int>> grid = [];
    readonly float cell;

    public WornSurface(IEnumerable<WornShape> shapes, float reach = 10)
    {
        cell = reach;
        foreach (var s in shapes)
            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
            {
                int a = s.Tris[t], b = s.Tris[t + 1], c = s.Tris[t + 2];
                Vector3 fn = Vector3.Cross(s.V[b] - s.V[a], s.V[c] - s.V[a]);
                if (fn.LengthSquared() < 1e-10f) continue;
                fn = Vector3.Normalize(fn);
                Vector3 N(int i) => s.N.Length == s.V.Length && s.N[i].LengthSquared() > 0 ? s.N[i] : fn;
                tris.Add((s.V[a], s.V[b], s.V[c], N(a), N(b), N(c)));
            }
        for (int i = 0; i < tris.Count; i++)
        {
            var lo = Vector3.Min(tris[i].A, Vector3.Min(tris[i].B, tris[i].C));
            var hi = Vector3.Max(tris[i].A, Vector3.Max(tris[i].B, tris[i].C));
            var (x0, y0, z0) = Key(lo); var (x1, y1, z1) = Key(hi);
            for (int x = x0; x <= x1; x++) for (int y = y0; y <= y1; y++) for (int z = z0; z <= z1; z++)
                    (grid.TryGetValue((x, y, z), out var l) ? l : grid[(x, y, z)] = []).Add(i);
        }
    }

    (int, int, int) Key(Vector3 p) => ((int)MathF.Floor(p.X / cell), (int)MathF.Floor(p.Y / cell), (int)MathF.Floor(p.Z / cell));

    /// <summary>The nearest surface point within reach and the surface's (interpolated) normal there; null if none.</summary>
    public (Vector3 Point, Vector3 Normal)? Nearest(Vector3 p)
    {
        var (kx, ky, kz) = Key(p);
        float best = cell * cell;
        Vector3 q = default, qn = default;
        bool found = false;
        for (int x = kx - 1; x <= kx + 1; x++) for (int y = ky - 1; y <= ky + 1; y++) for (int z = kz - 1; z <= kz + 1; z++)
                if (grid.TryGetValue((x, y, z), out var l))
                    foreach (int ti in l)
                    {
                        var t = tris[ti];
                        var (cp, u, w) = WornMesh.Closest(p, t.A, t.B, t.C);
                        float d2 = Vector3.DistanceSquared(p, cp);
                        if (d2 >= best) continue;
                        best = d2; q = cp; found = true;
                        qn = t.Na * (1 - u - w) + t.Nb * u + t.Nc * w;
                    }
        if (!found || qn.LengthSquared() < 1e-8f) return null;
        return (q, Vector3.Normalize(qn));
    }
}
/// <summary>
/// Skyrim's own character assets for worn meshes, from the game's archives (loose files in Data first, as the game
/// loads them): the male and female skeletons and the naked body, hands and feet at both weights, in bind space.
/// </summary>
public sealed class SkyrimCharacterAssets(string skyrimData)
{
    Dictionary<string, IArchiveFile>? archived;
    readonly Dictionary<string, BindSkeleton> skeletons = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, List<WornShape>> parts = new(StringComparer.OrdinalIgnoreCase);
    static readonly object NiflyLock = new();

    public byte[]? Read(string path)
    {
        path = path.Replace('/', '\\');
        string loose = Path.Combine(skyrimData, path);
        if (File.Exists(loose)) return File.ReadAllBytes(loose);
        if (archived is null)
        {
            archived = new(StringComparer.OrdinalIgnoreCase);
            foreach (var bsa in Directory.GetFiles(skyrimData, "Skyrim - Meshes*.bsa"))
                foreach (var f in Archive.CreateReader(GameRelease.SkyrimSE, bsa).Files) archived.TryAdd(f.Path.Replace('/', '\\'), f);
        }
        return archived.TryGetValue(path, out var file) ? file.GetBytes() : null;
    }

    T WithNif<T>(byte[] bytes, Func<NifFile, T> use)
    {
        lock (NiflyLock)
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"tm_worn_{Guid.NewGuid():N}.nif");
            File.WriteAllBytes(tmp, bytes);
            try
            {
                using var nif = new NifFile();
                if (nif.Load(tmp) != 0) throw new InvalidDataException("nifly can't read it");
                return use(nif);
            }
            finally { File.Delete(tmp); }
        }
    }

    public const string MaleSkeleton = @"meshes\actors\character\character assets\skeleton.nif";
    public const string FemaleSkeleton = @"meshes\actors\character\character assets female\skeleton_female.nif";

    public BindSkeleton Skeleton(bool female)
    {
        string path = female ? FemaleSkeleton : MaleSkeleton;
        if (skeletons.TryGetValue(path, out var hit)) return hit;
        var bytes = Read(path) ?? throw new FileNotFoundException(path);
        return skeletons[path] = WithNif(bytes, BindSkeleton.FromNif);
    }

    /// <summary>The naked body, hands or feet ("body", "hands", "feet") at a weight (0 thin, 1 heavy), in bind space.</summary>
    public IReadOnlyList<WornShape> Naked(string part, bool female, int weight)
    {
        string path = $@"meshes\actors\character\character assets\{(female ? "female" : "male")}{part}_{weight}.nif";
        if (parts.TryGetValue(path, out var hit)) return hit;
        var bytes = Read(path) ?? throw new FileNotFoundException(path);
        var skeleton = Skeleton(female);
        return parts[path] = WithNif(bytes, nif => WornMesh.ReadSkinned(nif, skeleton));
    }

    readonly Dictionary<string, WornSurface> surfaces = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The naked part as a surface to fit worn shapes outside of (built once).</summary>
    public WornSurface Surface(string part, bool female, int weight)
    {
        string key = $"{part}|{female}|{weight}";
        return surfaces.TryGetValue(key, out var s) ? s : surfaces[key] = new WornSurface(Naked(part, female, weight));
    }

    /// <summary>Serialises nifly use (it isn't thread-safe).</summary>
    public static void Locked(Action a) { lock (NiflyLock) a(); }
}
