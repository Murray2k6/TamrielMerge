using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using nifly;
using TamrielMerge.Skyrim;
using TamrielMerge.Skyrim.Meshes;
using Ob = Mutagen.Bethesda.Oblivion;
using Vector2 = System.Numerics.Vector2;
using Vector3 = System.Numerics.Vector3;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's worn models (armour and clothing biped models, NIF 20.0.0.5 read by nifly) as shapes on Oblivion's
/// Biped skeleton, for <see cref="WornMesh.Retarget"/>. Skinned shapes are taken in their bind pose (each vertex in
/// skin space, each bone where its skin-to-bone transform puts it, carried to the skeleton's space); rigid ones
/// (helmets, shields) ride the bone their model names in its "Prn" extra data, as Oblivion attaches them, placed on
/// Oblivion's character skeleton (meshes\characters\_male\skeleton.nif). Oblivion's own bare-skin pieces (arms in a
/// cuirass, textured from textures\characters\) are left out: Skyrim's body is worn under the armour instead.
/// </summary>
public sealed class ObWornSource(ObData data, ObTextures textures)
{
    BindSkeleton? skeleton;
    T WithNif<T>(byte[] bytes, Func<NifFile, T> use)
    {
        // nifly isn't thread-safe: one lock for every use (the worn writers share it).
        T result = default!;
        SkyrimCharacterAssets.Locked(() =>
        {
            string tmp = Path.Combine(Path.GetTempPath(), $"tm_obworn_{Guid.NewGuid():N}.nif");
            File.WriteAllBytes(tmp, bytes);
            try
            {
                using var nif = new NifFile();
                if (nif.Load(tmp) != 0) throw new InvalidDataException("nifly can't read it");
                result = use(nif);
            }
            finally { File.Delete(tmp); }
        });
        return result;
    }

    /// <summary>Oblivion's character skeleton in its rest pose (the pose its armour is bound in).</summary>
    public BindSkeleton Skeleton()
    {
        if (skeleton is not null) return skeleton;
        const string path = @"meshes\characters\_male\skeleton.nif";
        var bytes = data.Read(path) ?? throw new FileNotFoundException(path);
        return skeleton = WithNif(bytes, BindSkeleton.FromNif);
    }

    /// <summary>The model's shapes in the skeleton's space, weights by Biped bone, with the skeleton they are bound to
    /// (Oblivion's, with each skinned bone at its bind from the model's skin data).</summary>
    /// <param name="keepSkin">Also the shapes textured from characters\ (heads and hair, which are no armour's bare
    /// skin).</param>
    /// <param name="rides">The bone a rigid shape rides when the model names none (hair and heads: the head bone).</param>
    public (List<WornShape> Shapes, BindSkeleton Skeleton) Read(string model, List<string> problems, bool keepSkin = false, string? rides = null)
    {
        string source = model.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var bytes = data.Read(source) ?? throw new FileNotFoundException(source);
        var character = Skeleton();
        var bind = new BindSkeleton();
        foreach (var (k, v) in character.World) bind.World[k] = v;
        foreach (var (k, v) in character.Parent) bind.Parent[k] = v;
        var shapes = WithNif(bytes, nif =>
        {
            var result = new List<WornShape>();
            var hdr = nif.GetHeader();
            // The bone a rigid model hangs from: its "Prn" string extra data.
            string? prn = null;
            for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                if (hdr.GetBlockById(i) is NiStringExtraData sx && sx.name.get() == "Prn") prn = sx.stringData.get();
            using var all = nif.GetShapes();
            for (int si = 0; si < all.Count; si++)
            {
                var shape = all[si];
                string type = hdr.GetBlockTypeStringById(hdr.GetBlockID(shape));
                if (type is not ("NiTriStrips" or "NiTriShape") || (shape.flags & 1) != 0) continue;
                string texture = nif.GetTexturePathByIndex(shape, 0) ?? "";
                // Oblivion's bare skin in armour (arms, legs, hands): Skyrim's body is worn under it instead.
                if (!keepSkin && ObData.Normalize(texture).Contains(@"characters\")) continue;
                var verts = nif.GetVertsForShape(shape);
                if (verts is null || verts.Count == 0) continue;
                var normals = nif.GetNormalsForShape(shape);
                var uvs = nif.GetUvsForShape(shape);
                using var t3 = new vectorTriangle();
                shape.GetTriangles(t3);
                if (t3.Count == 0) continue;
                int n = verts.Count;
                var v = new Vector3[n]; var nr = new Vector3[n];
                var weights = new (string, float)[n][];
                using var names = new vectorstring();
                nif.GetShapeBoneList(shape, names);
                var bones = names.ToList();
                bool mirrored = false;
                if (bones.Count > 0)
                {
                    // Skinned: skin space -> the skeleton's space through the skin's global-to-skin transform, inverted;
                    // each bone bound where its skin-to-bone transform says.
                    var g2s = new MatTransform();
                    var toGlobal = nif.GetShapeTransformGlobalToSkin(shape, g2s) && Matrix4x4.Invert(WornMesh.ToMatrix(g2s), out var inv) ? inv : Matrix4x4.Identity;
                    for (int b = 0; b < bones.Count; b++)
                    {
                        var stb = new MatTransform();
                        nif.GetShapeTransformSkinToBone(shape, (uint)b, stb);
                        if (Matrix4x4.Invert(WornMesh.ToMatrix(stb), out var boneInSkin)) bind.World[bones[b]] = WornMesh.Orthonormal(boneInSkin * toGlobal);
                    }
                    var acc = new List<(string, float)>[n];
                    for (int i = 0; i < n; i++) acc[i] = [];
                    using (var cache = new niflycpp.BlockCache(nif.GetHeader()))
                    {
                        uint skinId = shape.SkinInstanceRef().index;
                        var inst = cache.EditableBlockById<NiSkinInstance>(skinId);
                        var sd = inst is null ? null : cache.EditableBlockById<NiSkinData>(inst.dataRef.index);
                        if (sd is not null)
                            for (int b = 0; b < bones.Count && b < sd.bones.Count; b++)
                                foreach (var sw in sd.bones[b].vertexWeights)
                                    if (sw.index < n && sw.weight > 0) acc[sw.index].Add((bones[b], sw.weight));
                    }
                    for (int i = 0; i < n; i++)
                    {
                        v[i] = Vector3.Transform(new Vector3(verts[i].x, verts[i].y, verts[i].z), toGlobal);
                        nr[i] = normals is { Count: > 0 } && i < normals.Count ? Vector3.Normalize(Vector3.TransformNormal(new Vector3(normals[i].x, normals[i].y, normals[i].z), toGlobal)) : Vector3.UnitZ;
                        var top = acc[i].GroupBy(x => x.Item1).Select(g => (g.Key, g.Sum(x => x.Item2))).OrderByDescending(x => x.Item2).Take(4).ToList();
                        float total = top.Sum(x => x.Item2);
                        weights[i] = total > 0 ? top.Select(x => (x.Key, x.Item2 / total)).ToArray() : [("Bip01 Pelvis", 1f)];
                    }
                }
                else
                {
                    // Rigid: the model's own transforms down to the shape, on the bone named by "Prn" (else the
                    // nearest Bip01 node above it in the model).
                    var local = WornMesh.ToMatrix(shape.transform);
                    string? holder = null;
                    for (var parent = nif.GetParentNode(shape); parent is not null; parent = nif.GetParentNode(parent))
                    {
                        string pn = parent.name.get() ?? "";
                        if (holder is null && pn.StartsWith("Bip01", StringComparison.OrdinalIgnoreCase) && character.Has(pn)) { holder = pn; break; }
                        local *= WornMesh.ToMatrix(parent.transform);
                    }
                    holder ??= prn is not null && character.Has(prn) ? prn : rides is not null && character.Has(rides) ? rides : null;
                    if (holder is null) { problems.Add($"{model}: rigid shape {shape.name.get()} names no bone it rides"); continue; }
                    // What rides the head (helmets, hoods) is modelled upright about the head bone's position, in the
                    // actor's own axes, not in the bone's frame (whose x runs up the neck): 2026-11-13, the Blades, iron
                    // and glass helmets' own vertices run 7.4 to 10.3 either side of x = 0, forward to y = 13 and up to
                    // z = 21, and placed in the bone's frame every helmet lay on its side (worn -17..5 across the head).
                    var place = holder.Equals("Bip01 Head", StringComparison.OrdinalIgnoreCase)
                        ? local * Matrix4x4.CreateTranslation(character.World[holder].Translation)
                        : local * character.World[holder];
                    if (Environment.GetEnvironmentVariable("TM_WORN_DEBUG") == "1")
                    {
                        var raw = Enumerable.Range(0, n).Select(i => new Vector3(verts[i].x, verts[i].y, verts[i].z)).ToList();
                        Console.WriteLine($"DEBUG rigid {model} {shape.name.get()} holder {holder} prn {prn} raw {raw.Aggregate(Vector3.Min):F1}..{raw.Aggregate(Vector3.Max):F1} local x <{local.M11:F2},{local.M12:F2},{local.M13:F2}> y <{local.M21:F2},{local.M22:F2},{local.M23:F2}> z <{local.M31:F2},{local.M32:F2},{local.M33:F2}> t {local.Translation:F1}");
                    }
                    mirrored = place.GetDeterminant() < 0;
                    for (int i = 0; i < n; i++)
                    {
                        v[i] = Vector3.Transform(new Vector3(verts[i].x, verts[i].y, verts[i].z), place);
                        nr[i] = normals is { Count: > 0 } && i < normals.Count ? Vector3.Normalize(Vector3.TransformNormal(new Vector3(normals[i].x, normals[i].y, normals[i].z), place)) : Vector3.UnitZ;
                        weights[i] = [(holder, 1f)];
                    }
                }
                var tris = new int[t3.Count * 3];
                for (int k = 0; k < t3.Count; k++)
                {
                    tris[k * 3] = t3[k].p1;
                    tris[k * 3 + 1] = mirrored ? t3[k].p3 : t3[k].p2;
                    tris[k * 3 + 2] = mirrored ? t3[k].p2 : t3[k].p3;
                }
                string? diffuse = texture.Length > 0 ? textures.Ensure(texture) : null;
                string? glow = texture.Length > 0 ? textures.Glow(texture) : null;
                var ws = new WornShape
                {
                    Name = shape.name.get() is { Length: > 0 } nm ? nm : $"Shape{si}",
                    V = v, N = normals is { Count: > 0 } ? nr : [],
                    Uv = Enumerable.Range(0, n).Select(i => uvs is { Count: > 0 } && i < uvs.Count ? new Vector2(uvs[i].u, uvs[i].v) : Vector2.Zero).ToArray(),
                    Tris = tris, Weights = weights,
                    Textures = glow is not null
                        ? [diffuse ?? textures.BlankDiffuse(), texture.Length > 0 ? textures.Normal(texture) : textures.Flat(), glow]
                        : [diffuse ?? textures.BlankDiffuse(), texture.Length > 0 ? textures.Normal(texture) : textures.Flat()],
                    // As Oblivion's statics are converted: specular from the normal map's alpha.
                    Flags1 = 0x82400303,
                    Flags2 = 0x8001u | (glow is not null ? 0x40u : 0),
                    ShaderType = glow is not null ? 2u : 0u,
                    SpecularStrength = 1,
                    EmissiveMultiple = glow is not null ? 1 : 0,
                    Emissive = glow is not null ? Vector3.One : Vector3.Zero,
                };
                if (nif.GetMaterialProperty(shape) is { } mat) { ws.Glossiness = Math.Max(1, mat.GetGlossiness()); ws.Alpha = mat.GetAlpha(); }
                if (nif.GetAlphaProperty(shape) is { } alpha)
                {
                    ws.AlphaFlags = alpha.flags; ws.AlphaThreshold = alpha.threshold;
                    if ((alpha.flags & 1) != 0 && (alpha.flags & 0x200) == 0) ws.Flags2 &= ~0x1u;   // blended: no depth write
                }
                result.Add(ws);
            }
            return result;
        });
        return (shapes, bind);
    }
}

/// <summary>
/// Oblivion's worn armour and clothing look (the counterpart of Morrowind's MwWorn): each item's male and female
/// biped models re-rigged onto Skyrim's skeleton (<see cref="WornMesh"/>), fitted over Skyrim's body at both weights,
/// and given armor addons in the slots Oblivion's biped flags mean in Skyrim: upper body the body (with Skyrim's body
/// worn under it), lower body an unused slot of its own (53) layered over the legs, both (robes) the two together,
/// hands the hands, feet the feet, head and hair the head and hair, shields the shield. Amulets and rings have no
/// worn model in Oblivion and draw none.
/// </summary>
public sealed partial class ObPluginBuilder
{
    SkyrimCharacterAssets? characterAssets;
    ObWornSource? wornSource;
    public int WornConverted { get; private set; }
    public int WornFallbacks { get; private set; }

    sealed record ObWornLook(BipedObjectFlag Slots, int Partition, string? Fit, float Margin, string? Skin);

    /// <summary>The Skyrim look of an Oblivion biped flag set; null for items with no worn model (rings, amulets).</summary>
    static ObWornLook? LookFor(Ob.BipedFlag f, bool armour)
    {
        bool upper = f.HasFlag(Ob.BipedFlag.UpperBody), lower = f.HasFlag(Ob.BipedFlag.LowerBody);
        if (upper) return new(BipedObjectFlag.Body | (lower ? VanillaActors.Slot(53) : 0), 32, "body", armour ? 1.2f : 0.5f, "body");
        if (lower) return new(VanillaActors.Slot(53), 53, "body", armour ? 1.2f : 0.5f, null);
        if (f.HasFlag(Ob.BipedFlag.Hand)) return new(BipedObjectFlag.Hands, 33, "hands", armour ? 0.6f : 0.4f, null);
        if (f.HasFlag(Ob.BipedFlag.Foot)) return new(BipedObjectFlag.Feet, 37, "feet", armour ? 0.8f : 0.5f, null);
        // Helmets and hoods over Skyrim's head, which isn't Oblivion's (fitted outside it: until 2026-11-14 they were
        // only placed on the head bone, and the head showed through them).
        if (f.HasFlag(Ob.BipedFlag.Head)) return new(BipedObjectFlag.Head | BipedObjectFlag.Hair, 30, "head", armour ? 0.35f : 0.25f, null);
        if (f.HasFlag(Ob.BipedFlag.Hair)) return new(BipedObjectFlag.Hair, 31, "head", armour ? 0.35f : 0.25f, null);
        if (f.HasFlag(Ob.BipedFlag.Shield)) return new(BipedObjectFlag.Shield, 39, null, 0, null);
        return null;
    }

    readonly System.Collections.Concurrent.ConcurrentDictionary<string, Dictionary<(bool Female, bool FirstPerson), string>?> wornMeshes = new(StringComparer.OrdinalIgnoreCase);
    readonly object wornSourceLock = new();

    /// <summary>Items wearing the same models the same way (an enchanted copy of a cuirass) share one conversion.</summary>
    static string WornKey(string? male, string? female, ObWornLook look) => $"{male}|{female}|{look.Slots}|{look.Fit}|{look.Margin}|{look.Skin}".ToLowerInvariant();

    /// <summary>
    /// The worn meshes of a pair of Oblivion biped models (male, female; either standing in for a missing other):
    /// re-rigged, fitted at both weights, first-person versions where they have arms or hands, written under
    /// meshes\tamrielmerge\ob\worn\&lt;model&gt;\. Their paths by (female, first person), or null when they can't be made
    /// (the reason in <paramref name="problems"/>). Safe to run in parallel.
    /// </summary>
    Dictionary<(bool Female, bool FirstPerson), string>? WornMeshes(string? maleModel, string? femaleModel, ObWornLook look, List<string> problems)
    {
        string anyModel = maleModel is { Length: > 0 } ? maleModel : femaleModel!;
        string stem = Sanitize(Path.ChangeExtension(ObData.Normalize(anyModel).Replace(@"meshes\", ""), null).Replace('\\', '_')).ToLowerInvariant();
        string folder = $@"{ObMeshConverter.OutFolder}\worn\{stem}{VanillaActors.StableHash(WornKey(maleModel, femaleModel, look)) % 10000:D4}";
        var paths = new Dictionary<(bool Female, bool FirstPerson), string>();
        try
        {
            foreach (bool female in new[] { false, true })
            {
                string model = (female ? femaleModel : maleModel) is { Length: > 0 } m ? m : anyModel;
                var skyrimSkeleton = characterAssets!.Skeleton(female);
                List<WornShape> source; BindSkeleton skeleton;
                // Reading copies textures (shared state): one model at a time.
                lock (wornSourceLock) (source, skeleton) = wornSource!.Read(model, problems);
                if (source.Count == 0) throw new InvalidDataException($"{model}: no shapes to wear");
                var shapes = WornMesh.Retarget(source, skeleton, skyrimSkeleton, problems);
                if (Environment.GetEnvironmentVariable("TM_WORN_DEBUG") == "1")
                {
                    static string B(IEnumerable<WornShape> l) => string.Join("; ", l.Select(s => $"{s.Name} {s.V.Aggregate(Vector3.Min):F1}..{s.V.Aggregate(Vector3.Max):F1} on {string.Join(",", s.Weights.SelectMany(w => w.Select(x => x.Item1)).Distinct().Take(3))}"));
                    Console.WriteLine($"DEBUG {model} female {female} | source {B(source)} | moved {B(shapes)}");
                    foreach (var bone in new[] { "Bip01 Head", "Bip01 Neck", "Bip01 Spine2", "Bip01 L Hand", "Bip01" })
                        if (skeleton.Has(bone)) { var w = skeleton.World[bone]; Console.WriteLine($"  {bone}: at {w.Translation:F1} x {new Vector3(w.M11, w.M12, w.M13):F2} y {new Vector3(w.M21, w.M22, w.M23):F2} z {new Vector3(w.M31, w.M32, w.M33):F2}"); }
                    foreach (var bone in skyrimSkeleton.World.Keys.Where(k => k.StartsWith("NPC Head") || k.StartsWith("NPC Neck")))
                    { var w = skyrimSkeleton.World[bone]; Console.WriteLine($"  {bone}: at {w.Translation:F1} x {new Vector3(w.M11, w.M12, w.M13):F2} y {new Vector3(w.M21, w.M22, w.M23):F2} z {new Vector3(w.M31, w.M32, w.M33):F2}"); }
                }
                bool any1st = false;
                foreach (int weight in new[] { 0, 1 })
                {
                    var fitted = shapes.Select(s => s.With(s.V.ToArray(), s.N.ToArray())).ToList();
                    if (look.Fit is { } over)
                        for (int round = 0; round < 4 && WornMesh.FitOutside(fitted, characterAssets.Surface(over, female, weight), look.Margin) > 0; round++) { }
                    if (look.Skin is { } skin) fitted.AddRange(characterAssets.Naked(skin, female, weight));
                    string name = $@"{folder}\{(female ? "f" : "m")}_{weight}.nif";
                    SkyrimCharacterAssets.Locked(() => WornMesh.Write(Path.Combine(modOutDir, name), fitted, skyrimSkeleton, look.Partition));
                    var first = WornMesh.FirstPerson(fitted);
                    if (first.Count > 0)
                    {
                        string firstName = $@"{folder}\1st_{(female ? "f" : "m")}_{weight}.nif";
                        SkyrimCharacterAssets.Locked(() => WornMesh.Write(Path.Combine(modOutDir, firstName), first, skyrimSkeleton, look.Partition));
                        any1st = true;
                    }
                }
                paths[(female, false)] = $@"{folder}\{(female ? "f" : "m")}_1.nif";
                if (any1st) paths[(female, true)] = $@"{folder}\1st_{(female ? "f" : "m")}_1.nif";
            }
            return paths;
        }
        catch (Exception e)
        {
            problems.Add($"worn {anyModel}: {e.Message} (template look kept)");
            return null;
        }
    }

    /// <summary>
    /// Converts every armour and clothing item's worn models at once, in parallel (geometry on all cores; reading,
    /// texture copies and NIF writing one at a time), each distinct model pair once. Returns (converted, failed).
    /// </summary>
    public (int Converted, int Failed) PrepareWorn()
    {
        characterAssets ??= new SkyrimCharacterAssets(SkyrimData!);
        wornSource ??= new ObWornSource(data, meshes.Textures);
        foreach (bool female in new[] { false, true })
        {
            characterAssets.Skeleton(female);
            foreach (var part in new[] { "body", "hands", "feet", "head" })
                foreach (int w in new[] { 0, 1 }) characterAssets.Surface(part, female, w);
        }
        lock (wornSourceLock) wornSource.Skeleton();
        var work = new Dictionary<string, (string? Male, string? Female, ObWornLook Look)>(StringComparer.OrdinalIgnoreCase);
        foreach (var rec in records.Values)
        {
            (string? male, string? female, Ob.BipedFlag flags, bool armour) = rec switch
            {
                Ob.IArmorGetter a => (a.MaleBipedModel?.File, a.FemaleBipedModel?.File, a.ClothingFlags?.BipedFlags ?? 0, true),
                Ob.IClothingGetter c => (c.MaleBipedModel?.File, c.FemaleBipedModel?.File, c.ClothingFlags?.BipedFlags ?? 0, false),
                _ => (null, null, (Ob.BipedFlag)0, false),
            };
            if (male is not { Length: > 0 } && female is not { Length: > 0 }) continue;
            if (LookFor(flags, armour) is not { } look) continue;
            work.TryAdd(WornKey(male, female, look), (male, female, look));
        }
        int converted = 0, failed = 0;
        var problems = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, kv =>
        {
            var local = new List<string>();
            var paths = WornMeshes(kv.Value.Male, kv.Value.Female, kv.Value.Look, local);
            wornMeshes[kv.Key] = paths;
            if (paths is null) Interlocked.Increment(ref failed); else Interlocked.Increment(ref converted);
            foreach (var p in local) problems.Add(p);
        });
        Problems.AddRange(problems);
        return (converted, failed);
    }
    /// <summary>
    /// Gives an item (its template's copy) Oblivion's worn look: armor addons copied from the template's (races and
    /// sounds) wearing the converted models. Without a worn look in Oblivion (rings, amulets) it draws nothing; when
    /// the model can't be converted (logged) it keeps the template's look, in <paramref name="fallbackSlot"/> drawing
    /// nothing for parts Skyrim has no slot for.
    /// </summary>
    void ApplyWornLook(Armor a, string? maleModel, string? femaleModel, Ob.BipedFlag flags, bool armour, IArmorGetter template, BipedObjectFlag? fallbackSlot)
    {
        a.BodyTemplate ??= new BodyTemplate();
        var look = LookFor(flags, armour);
        // No worn look in Oblivion (rings, amulets): the vanilla template's (a ring's or amulet's), as every Skyrim armour
        // has worn parts; a look without models: a part drawing nothing (Skyrim.WornNothing).
        if (look is null) return;
        if (maleModel is not { Length: > 0 } && femaleModel is not { Length: > 0 })
        {
            a.Armature.Clear();
            a.Armature.Add(Skyrim.WornNothing.Addon(mod, modOutDir, a.EditorID + "AA", look.Slots, Vanilla.Skyrim.ArmorAddons.First(x => x.EditorID == "IronCuirassAA")).ToLink<IArmorAddonGetter>());
            a.BodyTemplate.FirstPersonFlags = look.Slots;
            return;
        }
        characterAssets ??= new SkyrimCharacterAssets(SkyrimData!);
        wornSource ??= new ObWornSource(data, meshes.Textures);
        string key = WornKey(maleModel, femaleModel, look);
        if (!wornMeshes.TryGetValue(key, out var paths)) wornMeshes[key] = paths = WornMeshes(maleModel, femaleModel, look, Problems);
        if (paths is null)
        {
            WornFallbacks++;
            if (fallbackSlot is { } s)
            {
                a.BodyTemplate.FirstPersonFlags = s;
                a.Armature.Clear();
                a.Armature.Add(Skyrim.WornNothing.Addon(mod, modOutDir, a.EditorID + "AA", s, Vanilla.Skyrim.ArmorAddons.First(x => x.EditorID == "IronCuirassAA")).ToLink<IArmorAddonGetter>());
            }
            return;
        }        string Model(bool female, bool firstPerson) => SkyrimModelPath(paths[(female, firstPerson)]);
        var templates = template.Armature.Select(l => Vanilla.Skyrim.ArmorAddons.FirstOrDefault(x => x.FormKey == l.FormKey)).OfType<IArmorAddonGetter>().ToList();
        if (templates.Count == 0) templates.Add(Vanilla.Skyrim.ArmorAddons.First(x => x.EditorID == "IronCuirassAA"));
        a.Armature.Clear();
        for (int i = 0; i < templates.Count; i++)
        {
            var aa = mod.ArmorAddons.DuplicateInAsNewRecord(templates[i]);
            aa.EditorID = a.EditorID + "AA" + (i == 0 ? "" : i.ToString());
            aa.BodyTemplate = new BodyTemplate { FirstPersonFlags = look.Slots, ArmorType = ArmorType.Clothing, Flags = templates[i].BodyTemplate?.Flags ?? 0 };
            aa.WorldModel = new GenderedItem<Model?>(new Model { File = Model(false, false) }, new Model { File = Model(true, false) });
            aa.FirstPersonModel = paths.ContainsKey((false, true)) || paths.ContainsKey((true, true))
                ? new GenderedItem<Model?>(paths.ContainsKey((false, true)) ? new Model { File = Model(false, true) } : null, paths.ContainsKey((true, true)) ? new Model { File = Model(true, true) } : null)
                : null;
            aa.WeightSliderEnabled = new GenderedItem<bool>(true, true);
            aa.SkinTexture = null;
            aa.TextureSwapList = null;
            a.Armature.Add(aa.ToLink<IArmorAddonGetter>());
        }
        a.BodyTemplate.FirstPersonFlags = look.Slots;
        WornConverted++;
    }

    /// <summary>Dev aid (ob-worn): builds the armour and clothing whose EditorID matches, as the plugin builder does,
    /// into the output folder. Returns a line per item.</summary>
    public IEnumerable<string> ConvertWorn(System.Text.RegularExpressions.Regex match, int take)
    {
        foreach (var (key, rec) in records.Where(r => r.Value is Ob.IArmorGetter or Ob.IClothingGetter && r.Value.EditorID is { } e && match.IsMatch(e)).Take(take).ToList())
        {
            int before = Problems.Count;
            var item = Item(key);
            yield return item is Armor a
                ? $"{rec.EditorID}: {a.EditorID} slots {a.BodyTemplate?.FirstPersonFlags}, {a.Armature.Count} addons: {string.Join(", ", a.Armature.Select(l => mod.ArmorAddons.FirstOrDefault(x => x.FormKey == l.FormKey)?.WorldModel?.Male?.File.DataRelativePath.Path ?? l.FormKey.ToString()).Distinct())}"
                : $"{rec.EditorID}: {item?.GetType().Name ?? "not made"}";
            foreach (var p in Problems.Skip(before)) yield return "   problem: " + p;
        }
    }
}