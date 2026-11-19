using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Skyrim.Meshes;
using TamrielMerge.Util;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Each of Oblivion's people with their own face (user, 2026-11-14: "facegen as well"; 2026-11-15: "ureil does not
/// look like uriel from the intro", "baurus does not have is oblivion voice either").
/// <list type="bullet">
/// <item>The face is Oblivion's own: its race's head, ears, eyes, mouth, teeth and tongue models, each moved by the
/// person's FaceGen shape coefficients (ObFaceGenPart), the head's skin made from its texture and the person's
/// texture coefficients (ObFaceTexture). Proven offline on Uriel Septim before any of this was written.</item>
/// <item>In Skyrim it is a face file of the person's own (facegendata\facegeom\Oblivion.esm\00xxxxxx.nif) whose
/// shapes are named after head parts made here (OBFace..., OBEyes..., OBMouth... with the teeth and tongue as its
/// extra parts, and two empty ones that stand in the place of the race's own hair and brows), each with a morph
/// file made from Oblivion's (lip shapes, expressions, blinks). The head and ears are one shape with one texture,
/// so Skyrim's skin tint treats them alike.</item>
/// <item>The head is brought onto Skyrim's as the hair is (skull box to box), takes its bone weights from the
/// nearest of Skyrim's head, and its neck's open ring is drawn onto the ring of Skyrim's head, so it meets the body
/// where Skyrim's does.</item>
/// <item>Skin colour: Skyrim draws skin as its texture under the person's skin tone (the body by the record's tone,
/// the head by a tint texture filled with it: measured 2026-11-15, Calixto's is his tone 130,109,91 throughout).
/// So the person's skin texture is brought to the mean colour of Skyrim's own head texture, features kept, and the
/// tone is Skyrim's median for the race and sex times how far the person's skin lies from Oblivion's median for
/// theirs.</item>
/// <item>With a face of their own a person needs no Skyrim person to borrow one from (the Traits template), and so
/// keeps the voice type of their own record: Skyrim takes the voice from the template with the face (measured in
/// game 2026-11-15: Uriel's record said MaleEvenToned, the game MaleOldKindly, his face donor's).</item>
/// </list>
/// Not carried over: Oblivion's age detail maps (headhuman{m|f}NN.dds: grey maps within 3% of flat; how the game
/// lays them on is not measured), and the hair's own FaceGen shape.
/// </summary>
public sealed partial class ObPluginBuilder
{
    sealed class FacePiece
    {
        public required ObFaceGenPart Gen;
        public required int First;
        public required int Count;
        /// <summary>Offsets in the model's own space as offsets in Skyrim's (row vectors).</summary>
        public required Matrix4x4 Linear;
    }

    sealed class FaceShape
    {
        public required string Name;
        public required WornShape Base;
        public required List<FacePiece> Pieces;
        public bool Skin;
        public HeadPart? Record;
        /// <summary>Vertices that stand at one place (the model's seams), for normals.</summary>
        public int[][] Weld = [];
        /// <summary>The head's own vertices that the neck's fitting moves: the ring vertex each follows and by how much.</summary>
        public (int Vertex, int Ring, float Weight)[] NeckPull = [];
        public int[] Ring = [];
    }

    sealed class HeadSet
    {
        public required string Id;
        public required bool Female;
        public required List<FaceShape> Shapes;
        public required List<HeadPart> Listed;
        public required BindSkeleton Skeleton;
        public ObFaceTexture? HeadModes, EarModes;
        public uint[] HeadBase = []; public int HeadW, HeadH;
        public uint[] EarBase = []; public int EarW, EarH;
        public Vector3 HeadMean, SkyrimSkin, SkyrimTone, ObMedian;
        public string NormalMap = "";
        public Vector3[] SkyrimRing = [];
        public WornShape? Hidden;
        /// <summary>The race's own parts no person's part takes the place of (<see cref="KeptParts"/>), each as a
        /// shape named after it, out of sight inside the skull.</summary>
        public List<WornShape> Kept = [];
    }

    readonly Dictionary<(FormKey Race, bool Female), HeadSet?> headSets = [];
    Dictionary<string, Mutagen.Bethesda.Archives.IArchiveFile>? skyrimTextures;
    /// <summary>People given their own face, those left with a borrowed one (their race's parts couldn't be made:
    /// in problems.txt), and the head sets made (a race and sex each).</summary>
    public int OwnFaces { get; private set; }
    public int FacesBorrowed { get; private set; }
    public int HeadSetsMade => headSets.Values.Count(s => s is not null);

    const string FaceFolder = @"tamrielmerge\ob\faces";
    const string HeadBone = "NPC Head [Head]";

    /// <summary>Gives the person their own face (see the class's notes). False when their race's parts can't be made;
    /// the person then keeps the face borrowed for them.</summary>
    bool OwnFace(Npc npc, Ob.INpcGetter n, bool female, string obRace, IRaceGetter race)
    {
        if (SkyrimData is null || records.GetValueOrDefault(n.Race.FormKey) is not Ob.IRaceGetter obR) return false;
        var key = (n.Race.FormKey, female);
        if (!headSets.TryGetValue(key, out var set))
        {
            try { set = MakeHeadSet(obR, female, HeadKind(obRace), race); }
            catch (Exception e) { Problems.Add($"faces of {obRace} {(female ? "women" : "men")}: {e.Message}"); set = null; }
            headSets[key] = set;
        }
        if (set is null) { FacesBorrowed++; return false; }
        try { WriteFace(npc, n, set); }
        catch (Exception e) { Problems.Add($"face of {n.EditorID}: {e.Message}"); FacesBorrowed++; return false; }
        OwnFaces++;
        return true;
    }

    // ---- a race and sex's parts ----

    HeadSet MakeHeadSet(Ob.IRaceGetter obR, bool female, string kind, IRaceGetter race)
    {
        var problems = new List<string>();
        characterAssets ??= new SkyrimCharacterAssets(SkyrimData!);
        wornSource ??= new ObWornSource(data, meshes.Textures);
        var skeleton = characterAssets.Skeleton(female);
        var skyrimHead = FaceGeomHair.HeadOf(characterAssets.Head(female, kind));
        string id = Sanitize(obR.EditorID ?? obR.FormKey.ID.ToString("X6")) + (female ? "F" : "M");

        // Oblivion's face parts by their place in the race's list: 0 head, 1 and 2 the ears (men's, women's), 3 mouth,
        // 4 and 5 the teeth (lower, upper), 6 tongue, 7 and 8 the eyes (left, right).
        (string Model, string Icon)? Part(int which)
        {
            foreach (var p in obR.FaceData)
                if (p.Index is { } ix && Convert.ToInt32(ix) == which && p.Model?.File is { Length: > 0 } file)
                    return (file.ToString(), p.Icon ?? "");
            return null;
        }
        WornShape Read(string model)
        {
            List<WornShape> source; BindSkeleton obSkeleton;
            lock (wornSourceLock) (source, obSkeleton) = wornSource.Read(model, problems, keepSkin: true, rides: "Bip01 Head");
            if (source.Count == 0) throw new InvalidDataException($"{model} has no shapes");
            return Combine(WornMesh.Retarget(source, obSkeleton, skeleton, problems), Path.GetFileNameWithoutExtension(model));
        }

        var headPart = Part(0) ?? throw new InvalidDataException("the race names no head model");
        var head = Read(headPart.Model);
        var unmapped = new List<WornShape> { head.With(v: (Vector3[])head.V.Clone(), n: (Vector3[])head.N.Clone()) };
        WornShape Mapped(WornShape s) { var list = new List<WornShape> { s }; FaceGeomHair.OntoHead(list, unmapped, skyrimHead); return list[0]; }
        head = Mapped(head);
        var headGen = ObFaceGenPart.Read(data, headPart.Model);
        if (!headGen.HasTri || headGen.V != head.V.Length) throw new InvalidDataException($"{headPart.Model}: its morph file has {headGen.V} vertices, the model {head.V.Length}");
        var headLinear = Fit(headGen.Base, head.V, headGen.V, out float headError);
        if (headError > 0.5f) throw new InvalidDataException($"{headPart.Model}: its morph file's vertices are not the model's (off by {headError:F2} on average)");

        var set = new HeadSet { Id = id, Female = female, Shapes = [], Listed = [], Skeleton = skeleton };
        var validRaces = FaceRaces(race, female);
        HeadPart Record(string name, int type, bool extra, string model, string? morphs)
        {
            var hp = new HeadPart(mod)
            {
                EditorID = name,
                Name = name,
                Model = new Model { File = SkyrimModelPath(model) },
                Flags = (HeadPart.Flag)((female ? 4 : 2) | (extra ? 8 : 0)),
                Type = (HeadPart.TypeEnum)type,
            };
            if (validRaces is { } races) hp.ValidRaces.SetTo(races);
            if (morphs is not null) hp.Parts.Add(new Part { PartType = (Part.PartTypeEnum)1, FileName = SkyrimModelPath(morphs) });
            mod.HeadParts.Add(hp);
            return hp;
        }

        // The face: head and ears as one shape, the head's UVs in the left half of one texture, the ears' beside it.
        var pieces = new List<(WornShape Shape, ObFaceGenPart Gen, Matrix4x4 Linear)> { (head, headGen, headLinear) };
        var earPart = Part(female ? 2 : 1);
        if (earPart is { } ears)
        {
            // (The ears have no morph file to measure their space by: FaceGen's parts of one head share it.)
            pieces.Add(Piece(Read, Mapped, ears.Model, headLinear));
        }
        var face = Join("OBFace" + id, pieces, (i, uv) => i == 0 ? new Vector2(uv.X * 0.5f, uv.Y) : new Vector2(0.5f + uv.X * 0.25f, uv.Y * 0.5f));
        face.Skin = true;
        face.Base.Weights = SkyrimWeights(face.Base.V, skyrimHead);
        NeckOf(face, head.V.Length, skyrimHead, set);
        set.Shapes.Add(face);

        // The eyes as one shape; the mouth, the teeth and the tongue each their own.
        var eyePieces = new List<(WornShape Shape, ObFaceGenPart Gen, Matrix4x4 Linear)>();
        foreach (int which in new[] { 7, 8 })
            if (Part(which) is { } eye) eyePieces.Add(Piece(Read, Mapped, eye.Model, headLinear));
        FaceShape? eyesShape = eyePieces.Count > 0 ? Join("OBEyes" + id, eyePieces, (_, uv) => uv) : null;
        if (eyesShape is not null) set.Shapes.Add(eyesShape);
        var mouthShapes = new List<FaceShape>();
        foreach (var (which, label) in new[] { (3, "OBMouth"), (4, "OBTeethLower"), (5, "OBTeethUpper"), (6, "OBTongue") })
        {
            if (Part(which) is not { } part) continue;
            var shape = Join(label + id, [Piece(Read, Mapped, part.Model, headLinear)], (_, uv) => uv);
            string? texture = part.Icon.Length > 0 ? meshes.Textures.Ensure(part.Icon) : null;
            if (texture is not null) shape.Base.Textures = [texture, meshes.Textures.Flat()];
            mouthShapes.Add(shape);
            set.Shapes.Add(shape);
        }

        // Each part's model and morph file, and its record.
        foreach (var shape in set.Shapes)
        {
            Dress(shape, set);
            string model = $@"meshes\{FaceFolder}\{shape.Name.ToLowerInvariant()}.nif";
            SkyrimCharacterAssets.Locked(() => WornMesh.Write(Path.Combine(modOutDir, model), [shape.Base], skeleton, 230, faceFile: true));
            string? morphs = null;
            var morphList = Morphs(shape);
            if (morphList.Count > 0)
            {
                morphs = Path.ChangeExtension(model, ".tri");
                FaceTri.Write(Path.Combine(modOutDir, morphs), shape.Base.V, shape.Base.Tris, shape.Base.Uv, morphList);
            }
            bool extra = mouthShapes.Count > 1 && mouthShapes.IndexOf(shape) > 0;
            int type = shape == face ? 1 : shape == eyesShape ? 2 : 0;
            shape.Record = Record(shape.Name, type, extra, model, morphs);
        }
        if (mouthShapes.Count > 1)
            foreach (var extra in mouthShapes.Skip(1)) mouthShapes[0].Record!.ExtraParts.Add(extra.Record!.ToLink<IHeadPartGetter>());
        set.Listed.Add(face.Record!);
        if (eyesShape is not null) set.Listed.Add(eyesShape.Record!);
        if (mouthShapes.Count > 0) set.Listed.Add(mouthShapes[0].Record!);

        // In the place of the race's own hair and brows (a person lists a part of each kind, or the race's default is
        // put on them): a shape with nothing to see, inside the skull. Their hair is worn (ObHair); brows are in the skin.
        var at = skeleton.Has(HeadBone) ? skeleton.Position(HeadBone) : Vector3.Zero;
        set.Hidden = new WornShape
        {
            Name = "hidden",
            V = [at, at + new Vector3(0.01f, 0, 0), at + new Vector3(0, 0, 0.01f)],
            N = [Vector3.UnitY, Vector3.UnitY, Vector3.UnitY],
            Uv = [Vector2.Zero, Vector2.Zero, Vector2.Zero],
            Tris = [0, 1, 2],
            Weights = [[(HeadBone, 1f)], [(HeadBone, 1f)], [(HeadBone, 1f)]],
            Textures = [meshes.Textures.Flat(), meshes.Textures.Flat()],
        };
        foreach (var (label, type) in new[] { ("OBNoHair", 3), ("OBNoBrows", 6) })
        {
            string name = label + id;
            string model = $@"meshes\{FaceFolder}\{name.ToLowerInvariant()}.nif";
            var hidden = set.Hidden.With(); hidden.Name = name;
            SkyrimCharacterAssets.Locked(() => WornMesh.Write(Path.Combine(modOutDir, model), [hidden], skeleton, 230, faceFile: true));
            set.Listed.Add(Record(name, type, false, model, null));
        }

        set.Kept = KeptParts(race, female, at, problems);

        // The head's skin: its texture and modes, the ears', the normal map laid out as the skin is, and the colours
        // the skin tone is reckoned from.
        string headTexture = TexturePath(headPart.Icon, headPart.Model);
        var decoded = data.Read(headTexture) is { } bytes ? DdsImage.Decode(bytes) : null;
        if (decoded is not { } headImage) throw new InvalidDataException($"{headTexture}: the head's texture can't be read");
        (set.HeadBase, set.HeadW, set.HeadH) = headImage;
        set.HeadModes = ObFaceTexture.Read(data, headPart.Model);
        set.HeadMean = Mean(set.HeadBase);
        if (earPart is { } earsTexture && data.Read(TexturePath(earsTexture.Icon, earsTexture.Model)) is { } earBytes && DdsImage.Decode(earBytes) is { } earImage)
        {
            (set.EarBase, set.EarW, set.EarH) = earImage;
            set.EarModes = ObFaceTexture.Read(data, earsTexture.Model);
        }
        set.NormalMap = NormalAtlas(id, headTexture);
        string skyrimDiffuse = skyrimHead.SelectMany(s => s.Textures.Take(1)).FirstOrDefault(t => t.Length > 0) ?? "";
        set.SkyrimSkin = SkyrimTexture(skyrimDiffuse) is { } skinBytes && DdsImage.Average(skinBytes) is var (sr, sg, sb) ? new Vector3(sr, sg, sb) : new Vector3(82, 73, 68);
        var tones = Vanilla.Faces(race.FormKey, female).Select(f => f.TextureLighting).OfType<System.Drawing.Color>().ToList();
        set.SkyrimTone = tones.Count > 0 ? new Vector3(Median(tones.Select(c => (float)c.R)), Median(tones.Select(c => (float)c.G)), Median(tones.Select(c => (float)c.B))) : new Vector3(130, 109, 91);
        var own = records.Values.OfType<Ob.INpcGetter>()
            .Where(x => x.Race.FormKey == obR.FormKey && (x.Configuration?.Flags.HasFlag(Ob.Npc.NpcFlag.Female) == true) == female)
            .Select(x => SkinMean(set, ObFaceGenPart.Coefficients(x.FaceGenTextureSymmetric))).ToList();
        set.ObMedian = own.Count > 0 ? new Vector3(Median(own.Select(c => c.X)), Median(own.Select(c => c.Y)), Median(own.Select(c => c.Z))) : set.HeadMean;
        foreach (var p in problems.Distinct()) Problems.Add($"faces {id}: {p}");
        return set;
    }

    /// <summary>
    /// The Skyrim race's own head parts of the kind "Misc" (its mouth, with any extra parts of it), each as its own
    /// model's shape under its own name, shrunk to nothing at a point inside the skull.
    /// <para>A person's head part takes the place of the race's default of the same kind, but not of this kind: Skyrim's
    /// own people who list Misc parts (hairlines, beards' extras: 19 of them in Skyrim.esm) keep their race's mouth. So
    /// the race's mouth is on every converted person too, and the game uses a person's face file only when it holds a
    /// shape for every part the person has. The first own faces had none for the mouth: the game left every one of the
    /// files aside and put the heads together itself from the parts' plain models, with no skin tint (blue faces, the
    /// race's plain shape, and Skyrim's teeth beside Oblivion's: runs r74 to r80, 2026-11-15; a face moved up inside a
    /// person's file stayed where it was, one moved up in the race's part model rose).</para>
    /// <para>The shape is the part's own, vertex for vertex (the game moves it by the part's own morph file), only out
    /// of sight: Oblivion's mouth, teeth and tongue are the ones seen.</para>
    /// </summary>
    List<WornShape> KeptParts(IRaceGetter race, bool female, Vector3 at, List<string> problems)
    {
        var kept = new List<WornShape>();
        var headData = female ? race.HeadData?.Female : race.HeadData?.Male;
        if (headData is null) return kept;
        var all = new List<IHeadPartGetter>();
        foreach (var reference in headData.HeadParts)
            if (reference.Head.FormKeyNullable is { } k && Vanilla.Skyrim.HeadParts.FirstOrDefault(h => h.FormKey == k) is { } part && part.Type is { } type && (int)type == 0)
            {
                all.Add(part);
                foreach (var extra in part.ExtraParts)
                    if (Vanilla.Skyrim.HeadParts.FirstOrDefault(h => h.FormKey == extra.FormKey) is { } x) all.Add(x);
            }
        foreach (var part in all)
        {
            string name = part.EditorID ?? "";
            string file = part.Model?.File.DataRelativePath.Path ?? "";
            if (name.Length == 0 || file.Length == 0) { problems.Add($"the race's part {part.FormKey} has no name or model (its people's face files will not be used)"); continue; }
            if (!file.StartsWith("meshes", StringComparison.OrdinalIgnoreCase)) file = @"meshes\" + file;
            var shapes = characterAssets!.Read(file) is { } bytes ? characterAssets.Skinned(bytes, female) : null;
            if (shapes is not { Count: > 0 }) { problems.Add($"the race's part {name}: {file} can't be read (its people's face files will not be used)"); continue; }
            var source = shapes[0];
            var centre = source.V.Aggregate(Vector3.Zero, (a, p) => a + p) / source.V.Length;
            var shape = source.With(v: [.. source.V.Select(p => at + (p - centre) * 0.001f)]);
            shape.Name = name;
            shape.Weights = [.. shape.V.Select(_ => new[] { (HeadBone, 1f) })];
            kept.Add(shape);
        }
        return kept;
    }

    /// <summary>
    /// One of the race's face models on Skyrim's head with its FaceGen files and the map its offsets go through: its
    /// own (its morph file's vertices against the model's) where it has a morph file that is the model's, else the
    /// head's (FaceGen's parts of one head share a space). A morph file that is not the model's is left out: the part
    /// then keeps the race's plain shape.
    /// </summary>
    (WornShape Shape, ObFaceGenPart Gen, Matrix4x4 Linear) Piece(Func<string, WornShape> read, Func<WornShape, WornShape> mapped, string model, Matrix4x4 headLinear)
    {
        var shape = mapped(read(model));
        var gen = ObFaceGenPart.Read(data, model);
        var linear = headLinear;
        if (gen.HasTri)
        {
            if (gen.V != shape.V.Length) { Problems.Add($"face part {model}: its morph file has {gen.V} vertices, the model {shape.V.Length} (kept in the race's plain shape)"); return (shape, new ObFaceGenPart(), linear); }
            var own = Fit(gen.Base, shape.V, gen.V, out float error);
            if (error <= 0.5f) linear = own;
            else { Problems.Add($"face part {model}: its morph file's vertices are not the model's (off by {error:F2} on average; kept in the race's plain shape)"); return (shape, new ObFaceGenPart(), linear); }
        }
        else if (gen.ModeVertices != shape.V.Length) return (shape, new ObFaceGenPart(), linear);
        return (shape, gen, linear);
    }

    static float Median(IEnumerable<float> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count == 0 ? 0 : sorted[sorted.Count / 2];
    }

    static Vector3 Mean(uint[] px)
    {
        double r = 0, g = 0, b = 0;
        foreach (uint c in px) { r += (c >> 16) & 0xFF; g += (c >> 8) & 0xFF; b += c & 0xFF; }
        return px.Length == 0 ? Vector3.Zero : new Vector3((float)(r / px.Length), (float)(g / px.Length), (float)(b / px.Length));
    }

    /// <summary>A face's mean skin colour: the head's texture's plus what the person's coefficients add.</summary>
    static Vector3 SkinMean(HeadSet set, float[] coefficients) =>
        Vector3.Clamp(set.HeadMean + (set.HeadModes?.MeanShift(coefficients) ?? Vector3.Zero), new Vector3(8), new Vector3(255));

    /// <summary>A part's texture: the race's icon for it, else the texture named as its model is.</summary>
    static string TexturePath(string icon, string model)
    {
        string t = icon.Length > 0 ? ObData.Normalize(icon) : Path.ChangeExtension(ObData.Normalize(model), ".dds");
        if (t.StartsWith(@"meshes\")) t = t[7..];
        return t.StartsWith(@"textures\") ? t : @"textures\" + t;
    }

    /// <summary>The form list of races Skyrim's own face part for the race and sex is valid for.</summary>
    FormKey? FaceRaces(IRaceGetter race, bool female)
    {
        var headData = female ? race.HeadData?.Female : race.HeadData?.Male;
        if (headData is null) return null;
        foreach (var reference in headData.HeadParts)
            if (reference.Head.FormKeyNullable is { } k && Vanilla.Skyrim.HeadParts.FirstOrDefault(h => h.FormKey == k) is { } part && part.Type is { } type && (int)type == 1)
                return part.ValidRaces.FormKeyNullable;
        return null;
    }

    byte[]? SkyrimTexture(string path)
    {
        if (path.Length == 0 || SkyrimData is null) return null;
        path = path.Replace('/', '\\');
        string loose = Path.Combine(SkyrimData, path);
        if (File.Exists(loose)) return File.ReadAllBytes(loose);
        if (skyrimTextures is null)
        {
            skyrimTextures = new(StringComparer.OrdinalIgnoreCase);
            foreach (var bsa in Directory.GetFiles(SkyrimData, "Skyrim - Textures*.bsa"))
                foreach (var f in Mutagen.Bethesda.Archives.Archive.CreateReader(GameRelease.SkyrimSE, bsa).Files) skyrimTextures.TryAdd(f.Path.Replace('/', '\\'), f);
        }
        return skyrimTextures.TryGetValue(path, out var file) ? file.GetBytes() : null;
    }

    /// <summary>Several shapes of one model as one (vertices in the model's order).</summary>
    static WornShape Combine(List<WornShape> shapes, string name)
    {
        if (shapes.Count == 1) return shapes[0];
        var tris = new List<int>(); int offset = 0;
        foreach (var s in shapes) { tris.AddRange(s.Tris.Select(t => t + offset)); offset += s.V.Length; }
        var first = shapes[0].With();
        return new WornShape
        {
            Name = name,
            V = [.. shapes.SelectMany(s => s.V)],
            N = [.. shapes.SelectMany(s => s.N.Length == s.V.Length ? s.N : s.V.Select(_ => Vector3.UnitZ))],
            Uv = [.. shapes.SelectMany(s => s.Uv.Length == s.V.Length ? s.Uv : s.V.Select(_ => Vector2.Zero))],
            Tris = [.. tris],
            Weights = [.. shapes.SelectMany(s => s.Weights)],
            Textures = first.Textures, ShaderType = first.ShaderType, Flags1 = first.Flags1, Flags2 = first.Flags2,
        };
    }

    /// <summary>The linear map that takes points of the model's own space to where the mapped model has them (least
    /// squares over the first <paramref name="count"/>), and how far off it leaves them on average.</summary>
    static Matrix4x4 Fit(Vector3[] from, Vector3[] to, int count, out float error)
    {
        count = Math.Min(count, Math.Min(from.Length, to.Length));
        var fc = Vector3.Zero; var tc = Vector3.Zero;
        for (int i = 0; i < count; i++) { fc += from[i]; tc += to[i]; }
        fc /= count; tc /= count;
        var a = new double[3, 3]; var b = new double[3, 3];
        for (int i = 0; i < count; i++)
        {
            var f = from[i] - fc; var t = to[i] - tc;
            double[] fv = [f.X, f.Y, f.Z], tv = [t.X, t.Y, t.Z];
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++) { a[r, c] += fv[r] * fv[c]; b[r, c] += fv[r] * tv[c]; }
        }
        var am = new Matrix4x4((float)a[0, 0], (float)a[0, 1], (float)a[0, 2], 0, (float)a[1, 0], (float)a[1, 1], (float)a[1, 2], 0, (float)a[2, 0], (float)a[2, 1], (float)a[2, 2], 0, 0, 0, 0, 1);
        var bm = new Matrix4x4((float)b[0, 0], (float)b[0, 1], (float)b[0, 2], 0, (float)b[1, 0], (float)b[1, 1], (float)b[1, 2], 0, (float)b[2, 0], (float)b[2, 1], (float)b[2, 2], 0, 0, 0, 0, 1);
        if (!Matrix4x4.Invert(am, out var inverse)) { error = float.MaxValue; return Matrix4x4.Identity; }
        var linear = inverse * bm;
        double sum = 0;
        for (int i = 0; i < count; i++) sum += Vector3.DistanceSquared(Vector3.TransformNormal(from[i] - fc, linear) + tc, to[i]);
        error = (float)Math.Sqrt(sum / count);
        return linear;
    }

    /// <summary>Parts as one shape named after its head part, their UVs through <paramref name="uv"/> (piece, UV).</summary>
    static FaceShape Join(string name, List<(WornShape Shape, ObFaceGenPart Gen, Matrix4x4 Linear)> parts, Func<int, Vector2, Vector2> uv)
    {
        var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<Vector2>(); var tris = new List<int>();
        var pieces = new List<FacePiece>();
        for (int i = 0; i < parts.Count; i++)
        {
            var (shape, gen, linear) = parts[i];
            int first = v.Count;
            v.AddRange(shape.V);
            n.AddRange(shape.N.Length == shape.V.Length ? shape.N : shape.V.Select(_ => Vector3.UnitY));
            for (int k = 0; k < shape.V.Length; k++) t.Add(uv(i, k < shape.Uv.Length ? shape.Uv[k] : Vector2.Zero));
            tris.AddRange(shape.Tris.Select(x => x + first));
            pieces.Add(new FacePiece { Gen = gen, First = first, Count = shape.V.Length, Linear = linear });
        }
        var joined = parts[0].Shape.With(v: [.. v], n: [.. n]);
        joined.Name = name;
        joined.Uv = [.. t];
        joined.Tris = [.. tris];
        joined.Weights = [.. v.Select(_ => new[] { (HeadBone, 1f) })];
        var groups = new Dictionary<(int, int, int), List<int>>();
        for (int i = 0; i < v.Count; i++)
        {
            var key = ((int)MathF.Round(v[i].X * 500), (int)MathF.Round(v[i].Y * 500), (int)MathF.Round(v[i].Z * 500));
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = [];
            list.Add(i);
        }
        return new FaceShape { Name = name, Base = joined, Pieces = pieces, Weld = [.. groups.Values.Select(g => g.ToArray())] };
    }

    /// <summary>Each vertex's bones as the nearest vertex of Skyrim's head has them.</summary>
    static (string Bone, float W)[][] SkyrimWeights(Vector3[] vertices, IReadOnlyList<WornShape> skyrimHead)
    {
        var points = skyrimHead.SelectMany(s => s.V.Select((p, i) => (P: p, W: s.Weights[i]))).ToArray();
        var weights = new (string, float)[vertices.Length][];
        for (int i = 0; i < vertices.Length; i++)
        {
            int best = 0; float bestD = float.MaxValue;
            for (int k = 0; k < points.Length; k++)
            {
                float d = Vector3.DistanceSquared(vertices[i], points[k].P);
                if (d < bestD) { bestD = d; best = k; }
            }
            weights[i] = points.Length > 0 && points[best].W.Length > 0 ? points[best].W : [(HeadBone, 1f)];
        }
        return weights;
    }

    /// <summary>The open edges of a mesh as loops of vertex positions (vertices at one place counted as one), in order.</summary>
    static List<List<int>> OpenLoops(Vector3[] v, int[] tris, int count)
    {
        var place = new Dictionary<(int, int, int), int>();
        var id = new int[count];
        for (int i = 0; i < count; i++)
        {
            var key = ((int)MathF.Round(v[i].X * 500), (int)MathF.Round(v[i].Y * 500), (int)MathF.Round(v[i].Z * 500));
            if (!place.TryGetValue(key, out int first)) place[key] = first = i;
            id[i] = first;
        }
        var uses = new Dictionary<(int, int), int>();
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            if (tris[t] >= count || tris[t + 1] >= count || tris[t + 2] >= count) continue;
            for (int e = 0; e < 3; e++)
            {
                int a = id[tris[t + e]], b = id[tris[t + (e + 1) % 3]];
                if (a == b) continue;
                var key = (Math.Min(a, b), Math.Max(a, b));
                uses[key] = uses.GetValueOrDefault(key) + 1;
            }
        }
        var next = new Dictionary<int, List<int>>();
        foreach (var ((a, b), used) in uses)
        {
            if (used != 1) continue;
            if (!next.TryGetValue(a, out var la)) next[a] = la = [];
            if (!next.TryGetValue(b, out var lb)) next[b] = lb = [];
            la.Add(b); lb.Add(a);
        }
        var loops = new List<List<int>>();
        var seen = new HashSet<int>();
        foreach (int start in next.Keys)
        {
            if (!seen.Add(start)) continue;
            var loop = new List<int> { start };
            int at = start;
            while (true)
            {
                int step = next[at].FirstOrDefault(x => !seen.Contains(x), -1);
                if (step < 0) break;
                seen.Add(step); loop.Add(step); at = step;
            }
            if (loop.Count >= 6) loops.Add(loop);
        }
        return loops;
    }

    /// <summary>
    /// The head's neck: its lowest open ring, and the ring of Skyrim's head it is to meet. Each vertex within reach of
    /// the ring follows the ring vertex nearest it, wholly at the ring and less the further up the neck.
    /// </summary>
    static void NeckOf(FaceShape face, int headVertices, IReadOnlyList<WornShape> skyrimHead, HeadSet set)
    {
        var v = face.Base.V;
        var ours = OpenLoops(v, face.Base.Tris, headVertices).OrderBy(l => l.Average(i => v[i].Z)).FirstOrDefault();
        var skyrim = Combine([.. skyrimHead], "head");
        var theirs = OpenLoops(skyrim.V, skyrim.Tris, skyrim.V.Length).OrderBy(l => l.Average(i => skyrim.V[i].Z)).FirstOrDefault();
        if (ours is null || theirs is null) return;
        set.SkyrimRing = [.. theirs.Select(i => skyrim.V[i])];
        face.Ring = [.. ours];
        const float reach = 6f;
        var pull = new List<(int, int, float)>();
        for (int i = 0; i < headVertices; i++)
        {
            int best = -1; float bestD = float.MaxValue;
            for (int k = 0; k < ours.Count; k++)
            {
                float d = Vector3.Distance(v[i], v[ours[k]]);
                if (d < bestD) { bestD = d; best = k; }
            }
            if (best < 0 || bestD >= reach) continue;
            float w = 1 - bestD / reach;
            pull.Add((i, best, w * w));
        }
        face.NeckPull = [.. pull];
    }

    static Vector3 NearestOnRing(Vector3 p, Vector3[] ring)
    {
        var best = ring[0]; float bestD = float.MaxValue;
        for (int i = 0; i < ring.Length; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Length];
            var ab = b - a;
            float t = ab.LengthSquared() > 1e-9f ? Math.Clamp(Vector3.Dot(p - a, ab) / ab.LengthSquared(), 0, 1) : 0;
            var q = a + ab * t;
            float d = Vector3.DistanceSquared(p, q);
            if (d < bestD) { bestD = d; best = q; }
        }
        return best;
    }

    /// <summary>How a part draws: the face as Skyrim's own heads (the face tint shader, matte, its normal map the
    /// model's own kind, not Skyrim's model-space one), the rest plainly.</summary>
    void Dress(FaceShape shape, HeadSet set)
    {
        var s = shape.Base;
        s.IsSkin = false; s.HairTint = null; s.AlphaFlags = null; s.Alpha = 1;
        s.Emissive = Vector3.Zero; s.EmissiveMultiple = 0; s.SpecularStrength = 0; s.Glossiness = 30;
        if (shape.Skin)
        {
            s.ShaderType = 4;
            // Skyrim's head: 0x82401703 / 0x02008021. Without its model-space normals (0x1000), specular (0x1: matte)
            // and vertex colours (0x20: these have none).
            s.Flags1 = 0x82400702;
            s.Flags2 = 0x02008001;
            s.Textures = ["", "", "", @"textures\actors\character\male\blankdetailmap.dds"];
        }
        else
        {
            s.ShaderType = 0;
            s.Flags1 = 0x82400302 & ~0x1u;
            s.Flags2 = 0x8001;
            if (s.Textures.Length == 0 || s.Textures[0].Length == 0) s.Textures = [meshes.Textures.Flat(), meshes.Textures.Flat()];
        }
    }

    /// <summary>A part's morphs under Skyrim's names, as offsets in Skyrim's space over its whole shape.</summary>
    static List<(string Name, Vector3[] Delta)> Morphs(FaceShape shape)
    {
        var morphs = new Dictionary<string, Vector3[]>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var piece in shape.Pieces)
            foreach (var (name, delta) in piece.Gen.Morphs)
                foreach (string skyrim in FaceTri.SkyrimNames(name))
                {
                    if (!morphs.TryGetValue(skyrim, out var all)) { morphs[skyrim] = all = new Vector3[shape.Base.V.Length]; order.Add(skyrim); }
                    for (int i = 0; i < piece.Count && i < delta.Length; i++) all[piece.First + i] = Vector3.TransformNormal(delta[i], piece.Linear);
                }
        return [.. order.Select(n => (n, morphs[n]))];
    }

    /// <summary>The head's normal map laid out as the skin is (the head's in the left half), written once a set.</summary>
    string NormalAtlas(string id, string headTexture)
    {
        string rel = $@"textures\{FaceFolder}\{id.ToLowerInvariant()}_n.dds";
        const int w = 512, h = 256;
        var px = new uint[w * h];
        Array.Fill(px, 0xFF8080FFu);
        string source = Path.ChangeExtension(headTexture, null) + "_n.dds";
        if (data.Read(source) is { } bytes && DdsImage.Decode(bytes) is var (normal, nw, nh))
            for (int y = 0; y < h; y++)
                for (int x = 0; x < 256; x++)
                {
                    var (r, g, b) = ObFaceTexture.Sample(normal, nw, nh, (x + 0.5f) / 256, (y + 0.5f) / h);
                    px[y * w + x] = 0xFF000000u | (uint)r << 16 | (uint)g << 8 | (uint)b;
                }
        string full = Path.Combine(modOutDir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        DfTextureExport.WriteDds(full, px, w, h);
        return rel;
    }

    // ---- a person ----

    void WriteFace(Npc npc, Ob.INpcGetter n, HeadSet set)
    {
        float[] symmetric = ObFaceGenPart.Coefficients(n.FaceGenGeometrySymmetric), asymmetric = ObFaceGenPart.Coefficients(n.FaceGenGeometryAsymmetric);
        float[] texture = ObFaceGenPart.Coefficients(n.FaceGenTextureSymmetric);
        string file = $"{npc.FormKey.ID:X8}";

        // The skin: the person's own, brought to the mean colour of Skyrim's head texture; the tone carries its colour.
        const int w = 512, h = 256;
        var mean = SkinMean(set, texture);
        var scale = set.SkyrimSkin / mean;
        var px = new uint[w * h];
        uint fill = Pack(set.SkyrimSkin);
        Array.Fill(px, fill);
        if (set.HeadModes is { } modes)
        {
            var skin = modes.Skin(set.HeadBase, set.HeadW, set.HeadH, texture);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < 256; x++)
                {
                    int sx = Math.Min(modes.Columns - 1, x * modes.Columns / 256), sy = Math.Min(modes.Rows - 1, y * modes.Rows / h);
                    int o = 3 * (sy * modes.Columns + sx);
                    px[y * w + x] = Pack(new Vector3(skin[o], skin[o + 1], skin[o + 2]) * scale);
                }
        }
        else
            for (int y = 0; y < h; y++)
                for (int x = 0; x < 256; x++)
                {
                    var (r, g, b) = ObFaceTexture.Sample(set.HeadBase, set.HeadW, set.HeadH, (x + 0.5f) / 256, (y + 0.5f) / h);
                    px[y * w + x] = Pack(new Vector3(r, g, b) * scale);
                }
        if (set.EarBase.Length > 0)
        {
            float[]? earSkin = set.EarModes?.Skin(set.EarBase, set.EarW, set.EarH, texture);
            for (int y = 0; y < 128; y++)
                for (int x = 0; x < 128; x++)
                {
                    Vector3 c;
                    if (earSkin is not null && set.EarModes is { } em)
                    {
                        int sx = Math.Min(em.Columns - 1, x * em.Columns / 128), sy = Math.Min(em.Rows - 1, y * em.Rows / 128);
                        int o = 3 * (sy * em.Columns + sx);
                        c = new Vector3(earSkin[o], earSkin[o + 1], earSkin[o + 2]);
                    }
                    else { var (r, g, b) = ObFaceTexture.Sample(set.EarBase, set.EarW, set.EarH, (x + 0.5f) / 128, (y + 0.5f) / 128); c = new Vector3(r, g, b); }
                    px[y * w + 256 + x] = Pack(c * scale);
                }
        }
        string skinRel = $@"textures\{FaceFolder}\people\{file.ToLowerInvariant()}.dds";
        Bc1Dds.Write(Path.Combine(modOutDir, skinRel), px, w, h);
        var tone = Vector3.Clamp(set.SkyrimTone * (mean / set.ObMedian), new Vector3(16), new Vector3(250));
        var toneColour = System.Drawing.Color.FromArgb(255, (int)tone.X, (int)tone.Y, (int)tone.Z);
        string tintRel = $@"textures\actors\character\facegendata\facetint\{PluginName}\{file}.dds";
        // The tint at 256 x 256 (Skyrim's own are 512): at 16 x 16 the game drew the face all but black (Uriel's cheek
        // 15,12,10 in run r81; in run r82 the same tone at 256 x 256 lit as skin, at 16 x 16 dark again, 2026-11-15).
        const int TintSize = 256;
        var tint = new uint[TintSize * TintSize];
        Array.Fill(tint, Pack(tone));
        Bc1Dds.Write(Path.Combine(modOutDir, tintRel), tint, TintSize, TintSize);

        // The eyes' texture: the person's own eyes (their record's, else the race's first).
        string? eyeTexture = null;
        // (A record with no eyes of its own, 278 of the 1,187 people of the first build, had no face made at all: the
        // missing list was read as if it were there.)
        IEnumerable<FormKey> eyeKeys = n.Eyes is { Count: > 0 } ownEyes ? ownEyes.Select(e => e.FormKey)
            : (records.GetValueOrDefault(n.Race.FormKey) as Ob.IRaceGetter)?.Eyes?.Select(e => e.FormKey) ?? [];
        if (eyeKeys.Select(k => records.GetValueOrDefault(k)).OfType<Ob.IEyeGetter>().FirstOrDefault()?.Icon is { Length: > 0 } eyeIcon)
            eyeTexture = meshes.Textures.Ensure(eyeIcon);

        // Each part in the person's shape.
        var shapes = new List<WornShape>();
        foreach (var part in set.Shapes)
        {
            var v = (Vector3[])part.Base.V.Clone();
            foreach (var piece in part.Pieces)
            {
                if (piece.Gen.ModeVertices < piece.Count) continue;
                var offsets = piece.Gen.Offsets(symmetric, asymmetric, piece.Count);
                for (int i = 0; i < piece.Count; i++) v[piece.First + i] += Vector3.TransformNormal(offsets[i], piece.Linear);
            }
            if (part.Ring.Length > 0 && set.SkyrimRing.Length > 2)
            {
                var move = new Vector3[part.Ring.Length];
                for (int k = 0; k < part.Ring.Length; k++) move[k] = NearestOnRing(v[part.Ring[k]], set.SkyrimRing) - v[part.Ring[k]];
                // Vertices at the ring's own places first (a seam's twins move as one), then the rest by their pull.
                var moved = new Vector3[v.Length];
                foreach (var (vertex, ring, weight) in part.NeckPull) moved[vertex] = move[ring] * weight;
                for (int i = 0; i < v.Length; i++) v[i] += moved[i];
            }
            var normals = part.Skin ? Normals(v, part.Base.Tris, part.Weld, part.Base.N) : part.Base.N;
            var shape = part.Base.With(v: v, n: normals);
            shape.Name = part.Name;
            if (part.Skin) shape.Textures = [skinRel, set.NormalMap, "", @"textures\actors\character\male\blankdetailmap.dds", "", "", tintRel];
            else if (part.Name.StartsWith("OBEyes", StringComparison.Ordinal) && eyeTexture is not null) shape.Textures = [eyeTexture, meshes.Textures.Flat()];
            shapes.Add(shape);
        }
        foreach (var listed in set.Listed.Where(l => l.EditorID!.StartsWith("OBNo", StringComparison.Ordinal)))
        {
            var hidden = set.Hidden!.With();
            hidden.Name = listed.EditorID!;
            shapes.Add(hidden);
        }
        foreach (var kept in set.Kept)
        {
            var shape = kept.With();
            shape.Name = kept.Name;
            shapes.Add(shape);
        }
        string faceRel = $@"meshes\actors\character\facegendata\facegeom\{PluginName}\{file}.nif";
        SkyrimCharacterAssets.Locked(() => WornMesh.Write(Path.Combine(modOutDir, faceRel), shapes, set.Skeleton, 230, faceFile: true, faceGeom: true));

        // The record: these parts, this tone, and no one else's face.
        npc.HeadParts.Clear();
        foreach (var listed in set.Listed) npc.HeadParts.Add(listed.ToLink<IHeadPartGetter>());
        npc.HeadTexture.Clear();
        npc.HairColor.Clear();
        npc.TextureLighting = toneColour;
        npc.TintLayers.Clear();
        npc.FaceMorph = null;
        npc.FaceParts = null;
        npc.Template.Clear();
        npc.Configuration.TemplateFlags = 0;
    }

    static uint Pack(Vector3 c) =>
        0xFF000000u | (uint)Math.Clamp((int)MathF.Round(c.X), 0, 255) << 16 | (uint)Math.Clamp((int)MathF.Round(c.Y), 0, 255) << 8 | (uint)Math.Clamp((int)MathF.Round(c.Z), 0, 255);

    /// <summary>People whose face's normals, made again from its triangles, had to be turned to face the way the
    /// model's own do, and those that already did.</summary>
    public int FaceNormalsTurned, FaceNormalsKept;

    /// <summary>Smooth normals (<see cref="Normals(Vector3[], int[], int[][])"/>) facing the way the model's own normals
    /// do: which side of a triangle is its front depends on the order of its corners, and Oblivion's models wind theirs
    /// the other way about from the cross product taken here. Turned inwards, every face was lit from inside the head
    /// and came out all but black in game (Uriel's cheek 15,12,10 against a Skyrim face's 120,95,85: run r81,
    /// 2026-11-15; the race's own head part, with the model's normals, had been lit as it should).</summary>
    Vector3[] Normals(Vector3[] v, int[] tris, int[][] weld, Vector3[] own)
    {
        var normals = Normals(v, tris, weld);
        if (own.Length != normals.Length) return normals;
        double agree = 0;
        for (int i = 0; i < normals.Length; i++) agree += Vector3.Dot(normals[i], own[i]);
        if (agree >= 0) { Interlocked.Increment(ref FaceNormalsKept); return normals; }
        Interlocked.Increment(ref FaceNormalsTurned);
        for (int i = 0; i < normals.Length; i++) normals[i] = -normals[i];
        return normals;
    }

    /// <summary>Smooth normals: each triangle's (by area) summed over the vertices at each place.</summary>
    static Vector3[] Normals(Vector3[] v, int[] tris, int[][] weld)
    {
        var sum = new Vector3[v.Length];
        for (int t = 0; t + 2 < tris.Length; t += 3)
        {
            var n = Vector3.Cross(v[tris[t + 1]] - v[tris[t]], v[tris[t + 2]] - v[tris[t]]);
            sum[tris[t]] += n; sum[tris[t + 1]] += n; sum[tris[t + 2]] += n;
        }
        var normals = new Vector3[v.Length];
        foreach (var group in weld)
        {
            var total = Vector3.Zero;
            foreach (int i in group) total += sum[i];
            var n = total.LengthSquared() > 1e-12f ? Vector3.Normalize(total) : Vector3.UnitZ;
            foreach (int i in group) normals[i] = n;
        }
        return normals;
    }
}
