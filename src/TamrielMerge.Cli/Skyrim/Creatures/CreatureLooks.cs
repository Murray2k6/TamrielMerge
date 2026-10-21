using System.Numerics;
using HKX2;
using TamrielMerge.Morrowind;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// Whether a built Morrowind creature looks as Morrowind draws it (user, 2026-10-21: "all bones and animations need fixed
/// across games"): read back from the written files, against the model's own properties (inherited down its scene as
/// Morrowind inherits them), worked out here and not by the converter:
/// <list type="bullet">
/// <item>each shape: its alpha property (flags, threshold), material alpha, tint (vertex colours where their mode takes
/// them, else a material colour that isn't white), glow (its glow map, or its texture by its emissive colour), depth test
/// and write, texture wrapping, its texture there and see-through where Morrowind's is, a dark map multiplied in (the
/// written texture against Morrowind's two, texel by texel);</item>
/// <item>its animated shader values (Morrowind's alpha and texture controllers): every clip's sequence in the body, each
/// value's keys against Morrowind's controller at every frame and half frame (its own .kf's clips; the rest as it rests),
/// looping as the clip does, with its start and end;</item>
/// <item>that they play: each clip sends its look's event as it starts (its animation data), the main graph knows the
/// event, and the body's own graph (its BGED project, character, skeleton, behaviour, animation data and index entry)
/// plays that clip's sequence on it.</item>
/// </list>
/// `creature-looks`.
/// </summary>
public static class CreatureLooks
{
    public sealed record Report(int Shapes, int Animated, int Issues, List<string> Lines);

    public static Report Check(Tes3Data data, string model, string dataDir, string id, MwWorld? world)
    {
        var lines = new List<string>();
        int issues = 0;
        void Issue(string s) { issues++; lines.Add("  ISSUE " + s); }
        string mp = model.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var mw = Nif4.Read(data.Read(mp) ?? throw new FileNotFoundException(mp), mp);
        var scene = MwCreatureSource.SceneParents(mw);
        var shapes = MwCreatureSource.BodyShapes(mw);

        string bodyPath = Path.Combine(dataDir, CreatureRig.MeshFolder(id), id + ".nif");
        using var nif = new nifly.NifFile();
        if (nif.Load(bodyPath) != 0) { Issue($"no body at {bodyPath}"); return new Report(0, 0, issues, lines); }
        var hdr = nif.GetHeader();
        var bodyShapes = new List<nifly.BSTriShape>();
        for (uint i = 0; i < hdr.GetNumBlocks(); i++)
            if (hdr.GetBlockTypeStringById(i) == "BSTriShape") bodyShapes.Add((nifly.BSTriShape)hdr.GetBlockById(i));
        if (bodyShapes.Count < shapes.Count) Issue($"{shapes.Count} Morrowind shapes, {bodyShapes.Count} in the body");

        // ---- Each shape as Morrowind draws it ----
        using var shapeCache = new nifly.niflycpp.BlockCache(hdr);
        var shapeOf = new Dictionary<int, nifly.BSTriShape>();
        for (int si = 0; si < shapes.Count && si < bodyShapes.Count; si++)
        {
            var s = shapes[si];
            var b = bodyShapes[si];
            string want = CreatureRig.FileSafe($"{id}_{(s.Name.Length > 0 ? s.Name : $"shape{s.Index}")}");
            string name = b.name.get();
            if (name != want) { Issue($"shape {si}: {name} where Morrowind's {want} should be"); continue; }
            shapeOf[s.Index] = b;
            if (mw.Get<NiTriShapeData>(s.Data) is not { } d) continue;
            bool drawn = MwCreatureSource.Drawn(mw, s);
            T? P<T>() where T : NiFlagProperty => MwCreatureSource.Property<T>(mw, scene, s.Index);
            var tx = P<NiTexturingProperty>(); var mat = P<NiMaterialProperty>(); var alpha = P<NiAlphaProperty>(); var vc = P<NiVertexColorProperty>();
            var zb = MwCreatureSource.Chain(scene, s.Index).Select(mw.Get<NiNode>).OfType<NiNode>().SelectMany(n => n.Properties.Reverse().Select(mw.Get<NiFlagProperty>)).FirstOrDefault(p => p?.Type == "NiZBufferProperty");
            uint shaderId = nif.GetShader(b) is { } shader ? hdr.GetBlockID(shader) : uint.MaxValue;
            if (shaderId == uint.MaxValue || hdr.GetBlockTypeStringById(shaderId) != "BSLightingShaderProperty") { Issue($"{name}: no lighting shader"); continue; }
            var sh = shapeCache.EditableBlockById<nifly.BSLightingShaderProperty>(shaderId);
            var notes = new List<string>();
            void Differs(string what, object mwValue, object skValue) => Issue($"{name}: {what} Morrowind {mwValue}, Skyrim {skValue}");

            var skAlpha = nif.GetAlphaProperty(b);
            if (alpha is null != skAlpha is null) Differs("alpha property", alpha is null ? "none" : $"{alpha.Flags:X}", skAlpha is null ? "none" : $"{skAlpha.flags:X}");
            else if (alpha is not null && (alpha.Flags != skAlpha!.flags || alpha.Threshold != skAlpha.threshold)) Differs("alpha", $"{alpha.Flags:X} >{alpha.Threshold}", $"{skAlpha.flags:X} >{skAlpha.threshold}");
            else if (alpha is not null) notes.Add($"alpha {alpha.Flags:X} >{alpha.Threshold}");
            float matAlpha = mat?.Alpha ?? 1;
            if (MathF.Abs(sh.alpha - matAlpha) > 1e-4f) Differs("material alpha", matAlpha, sh.alpha);
            else if (matAlpha < 1) notes.Add($"material alpha {matAlpha:F2}");

            // Tint: vertex colours where their mode (Morrowind's default 2) takes them, else a material colour not white on a textured shape.
            uint mode = vc?.VertexMode ?? 2;
            Vector4[]? tint = mode == 2 && d.Colors.Length == d.Vertices.Length && d.Colors.Any(c => c != Vector4.One) ? d.Colors
                : tx?.Base is not null && mat is { } m && Vector3.Distance(m.Diffuse, Vector3.One) > 0.004f ? Enumerable.Repeat(new Vector4(m.Diffuse, 1), d.Vertices.Length).ToArray() : null;
            bool skTinted = (sh.shaderFlags2 & 0x20) != 0;
            var skColors = nif.GetColorsForShape(b)?.Select(c => new Vector4(c.r, c.g, c.b, c.a)).ToList() ?? [];
            if ((tint is not null) != skTinted) Differs("tint", tint is null ? "none" : "vertex colours", skTinted ? "vertex colours" : "none");
            else if (tint is not null)
            {
                float worst = skColors.Count == tint.Length ? tint.Select((c, i) => Vector4.Distance(c, skColors[i])).Max() : float.MaxValue;
                if (worst > 1.5f / 255) Differs("vertex colours", $"{tint.Length}", skColors.Count == tint.Length ? $"up to {worst * 255:F1}/255 off" : $"{skColors.Count}");
                else notes.Add($"tinted ({worst * 255:F2}/255)");
            }

            // Glow: a glow map, else its texture by its emissive colour.
            var emissive = mat?.Emissive ?? Vector3.Zero;
            bool glowMap = tx?.Glow is not null;
            bool glows = drawn && (glowMap || (tx?.Base is not null && emissive.X + emissive.Y + emissive.Z > 0.05f));
            bool skGlows = sh.GetShaderType() == 2 && (sh.shaderFlags2 & 0x40) != 0;
            if (glows != skGlows && drawn) Differs("glow", glows ? "glows" : "none", skGlows ? "glows" : "none");
            else if (glows)
            {
                var wantColor = glowMap ? Vector3.One : emissive;
                var skColor = new Vector3(sh.emissiveColor.x, sh.emissiveColor.y, sh.emissiveColor.z);
                if (Vector3.Distance(wantColor, skColor) > 1e-4f || MathF.Abs(sh.emissiveMultiple - 1) > 1e-4f) Differs("glow colour", wantColor, $"{skColor} x{sh.emissiveMultiple}");
                else if (nif.GetTexturePathByIndex(b, 2) is not { Length: > 0 } g || !File.Exists(Path.Combine(dataDir, g))) Differs("glow map", glowMap ? "its glow map" : "its texture", "none");
                else notes.Add(glowMap ? "glow map" : $"glows ({emissive.X:F2},{emissive.Y:F2},{emissive.Z:F2})");
            }

            bool blended = alpha is { } a2 && (a2.Flags & 1) != 0 && (a2.Flags & 0x200) == 0;
            bool zTest = zb is null || (zb.Flags & 1) != 0, zWrite = (zb is null || (zb.Flags & 2) != 0) && !blended;
            if (((sh.shaderFlags1 & 0x80000000u) != 0) != zTest) Differs("depth test", zTest, !zTest);
            if (((sh.shaderFlags2 & 1) != 0) != zWrite) Differs("depth write", zWrite, !zWrite);
            uint clamp = tx?.Base?.Clamp ?? 3;
            if ((uint)sh.textureClampMode != clamp) Differs("texture wrapping", clamp, (uint)sh.textureClampMode);
            else if (clamp != 3) notes.Add($"wrapping {clamp}");
            if ((sh.shaderFlags1 & 1) != 0 || sh.specularStrength != 0) Differs("specular", "none", $"{sh.specularStrength}");

            // Its texture: there, see-through where Morrowind's is, a dark map multiplied in.
            string tex0 = nif.GetTexturePathByIndex(b, 0) ?? "";
            if (tex0.Length == 0 || !File.Exists(Path.Combine(dataDir, tex0))) { Differs("texture", tx?.Base is null ? "its colour" : "its texture", tex0.Length == 0 ? "none" : $"{tex0} (missing)"); }
            else if (drawn)
            {
                var written = Util.DdsImage.Decode(File.ReadAllBytes(Path.Combine(dataDir, tex0)));
                string? baseFile = tx?.Base is { } bt ? mw.Get<NiSourceTexture>(bt.Source)?.FileName : null;
                var source = baseFile is null ? null : Picture(data, baseFile);
                if (written is null) Differs("texture", baseFile ?? "its colour", $"{tex0} (unreadable)");
                else if (baseFile is null)
                {
                    var c = tint is not null ? Vector3.One : mat?.Diffuse ?? Vector3.One;
                    uint px = written.Value.Px[0];
                    var got = new Vector3(px >> 16 & 255, px >> 8 & 255, px & 255) / 255f;
                    if (Vector3.Distance(got, c) > 3f / 255) Differs("colour", c, got); else notes.Add($"its colour ({c.X:F2},{c.Y:F2},{c.Z:F2})");
                }
                else if (source is { } src)
                {
                    bool mwSeeThrough = src.Px.Any(p => p >> 24 != 255), skSeeThrough = written.Value.Px.Any(p => p >> 24 != 255);
                    if (alpha is not null && mwSeeThrough != skSeeThrough) Differs("texture alpha", mwSeeThrough ? "see-through texels" : "opaque", skSeeThrough ? "see-through texels" : "opaque");
                    // A dark map multiplied in: texel by texel against Morrowind's base times its dark map.
                    if (tx!.Textures.Length > 1 && tx.Textures[1] is { } dk && mw.Get<NiSourceTexture>(dk.Source)?.FileName is { } darkFile && Picture(data, darkFile) is { } dark)
                    {
                        var w = written.Value;
                        // An upscaled base (tools/upscale) is written larger: averaged back down to Morrowind's size to compare.
                        string scaledNote = "";
                        if ((w.W != src.W || w.H != src.H) && w.W % src.W == 0 && w.H % src.H == 0 && w.W / src.W == w.H / src.H)
                        {
                            int f = w.W / src.W;
                            var down = new uint[src.W * src.H];
                            for (int y = 0; y < src.H; y++)
                                for (int x = 0; x < src.W; x++)
                                {
                                    uint r = 0, g = 0, bl = 0, al = 0;
                                    for (int dy = 0; dy < f; dy++)
                                        for (int dx = 0; dx < f; dx++) { uint q = w.Px[(y * f + dy) * w.W + x * f + dx]; al += q >> 24; r += q >> 16 & 255; g += q >> 8 & 255; bl += q & 255; }
                                    uint n = (uint)(f * f);
                                    down[y * src.W + x] = (al / n) << 24 | (r / n) << 16 | (g / n) << 8 | bl / n;
                                }
                            w = (down, src.W, src.H);
                            scaledNote = $", its upscaled base averaged down {f}x";
                        }
                        if (w.W != src.W || w.H != src.H) Differs("dark-mapped texture size", $"{src.W}x{src.H}", $"{w.W}x{w.H}");
                        else
                        {
                            double sum = 0; float worstTexel = 0;
                            for (int y = 0; y < w.H; y++)
                                for (int x = 0; x < w.W; x++)
                                {
                                    uint bp = src.Px[y * w.W + x], kp = dark.Px[(y * dark.H / w.H) * dark.W + x * dark.W / w.W], sp = w.Px[y * w.W + x];
                                    for (int ch = 0; ch < 3; ch++)
                                    {
                                        float product = (bp >> (16 - 8 * ch) & 255) * (kp >> (16 - 8 * ch) & 255) / 255f, got = sp >> (16 - 8 * ch) & 255;
                                        float e = MathF.Abs(product - got); sum += e; worstTexel = MathF.Max(worstTexel, e);
                                    }
                                }
                            double mean = sum / (w.W * w.H * 3);
                            if (mean > 6) Differs("dark map", "multiplied in", $"mean {mean:F1}/255 off");
                            else notes.Add($"dark map multiplied in (mean {mean:F1}/255, worst {worstTexel:F0}/255 off: compression and the dark map's sampling{scaledNote})");
                        }
                    }
                }
            }
            if (notes.Count > 0) lines.Add($"  {name}: {string.Join(", ", notes)}");
        }

        // ---- Animated shader values, clip by clip ----
        var controllers = mw.Blocks.OfType<NiController>().Where(c => (c.Flags & 8) != 0).ToList();
        var expected = new List<(int Shape, int Variable, Func<float, float> At, float Resting)>();
        foreach (var s in shapes.Where(s => MwCreatureSource.Drawn(mw, s)))
        {
            if (MwCreatureSource.Property<NiMaterialProperty>(mw, scene, s.Index) is { } mat)
                foreach (var c in controllers.Where(c => c.Type == "NiAlphaController" && c.Target == mat.Index))
                    if (mw.Get<NiKeyData>(c.Data) is { Keys.Count: > 0 } k)
                        expected.Add((s.Index, 12, t => MwKeys.Vector(k.Keys, k.Interpolation, k.Extra, MwCreatureSource.ControllerTime(c, t)).X, mat.Alpha));
            foreach (var c in controllers.Where(c => c.Type == "NiUVController" && c.Target == s.Index))
                if (mw.Get<NiUVData>(c.Data) is { } uv)
                    for (int g = 0; g < 4; g++)
                        if (uv.Groups[g] is { Keys.Count: > 0 } k)
                            expected.Add((s.Index, new[] { 20, 22, 21, 23 }[g], t => MwKeys.Vector(k.Keys, k.Interpolation, k.Extra, MwCreatureSource.ControllerTime(c, t)).X, g < 2 ? 0 : 1));
        }
        foreach (var c in controllers.Where(c => c.Type == "NiMaterialColorController"))
            Issue($"{mw.Get<NiObjectNET>(c.Target)?.Name}: material colour {(c.Flags >> 4) & 7} animated by Morrowind (no Skyrim shader value for it)");

        var root = nif.GetRootNode();
        var manager = hdr.GetBlockById(root.controllerRef.index) as nifly.NiControllerManager;
        var sequences = new Dictionary<string, nifly.NiControllerSequence>(StringComparer.Ordinal);
        if (manager is not null)
        {
            using var refs = new nifly.vectoruint32();
            manager.controllerSequenceRefs.GetIndices(refs);
            foreach (uint r in refs) if (hdr.GetBlockById(r) is nifly.NiControllerSequence seq) sequences[seq.name.get()] = seq;
        }
        if (expected.Count == 0)
        {
            if (sequences.Count > 0) Issue($"{sequences.Count} look sequences, Morrowind animates none");
            return new Report(shapes.Count, 0, issues, lines);
        }

        var (biped, styles) = world is not null ? MwCreatureSource.Kind(world, mp) : (false, []);
        var sources = MwCreatureSource.Sources(data, mp, biped);
        var plan = MwCreatureSource.Plan(sources, styles, biped);
        string ownKf = Path.Combine(Path.GetDirectoryName(mp)!, "x" + Path.GetFileNameWithoutExtension(mp) + ".kf");
        int ownSource = sources.FindIndex(s => s.Path.Equals(ownKf, StringComparison.OrdinalIgnoreCase));
        string animDir = Path.Combine(dataDir, CreatureRig.MeshFolder(id), "animations");
        string[] looping = ["idle", "walk", "run"];
        using var cache = new nifly.niflycpp.BlockCache(hdr);
        var seen = new HashSet<string>();
        foreach (var p in plan)
        {
            if (!seen.Add(p.Clip) || !File.Exists(Path.Combine(animDir, p.Clip + ".hkx"))) continue;
            string seqName = CreatureRig.LookEvent(p.Clip);
            if (!sequences.TryGetValue(seqName, out var seq)) { Issue($"{p.Clip}: no sequence {seqName}"); continue; }
            int frames = Math.Max(2, (int)MathF.Round((p.Stop - p.Start) * 30) + 1);
            float length = (frames - 1) / 30f;
            if (MathF.Abs(seq.stopTime - length) > 1e-3f || seq.startTime != 0) Issue($"{seqName}: {seq.startTime}..{seq.stopTime}, its clip 0..{length}");
            bool loops = looping.Contains(p.Clip);
            if ((seq.cycleType == nifly.CycleType.CYCLE_LOOP) != loops) Issue($"{seqName}: cycle {seq.cycleType}, its clip {(loops ? "repeats" : "plays once")}");
            var textKeys = seq.textKeyRef.index < hdr.GetNumBlocks() && hdr.GetBlockTypeStringById(seq.textKeyRef.index) == "NiTextKeyExtraData"
                ? Skyrim.Meshes.NiflyRaw.TextKeys(cache.EditableBlockById<nifly.NiTextKeyExtraData>(seq.textKeyRef.index)).Select(k => (Time: k.time, Text: k.value.get())).ToList() : [];
            if (!textKeys.Any(k => k.Time == 0 && k.Text == "start") || !textKeys.Any(k => k.Text == "end" && MathF.Abs(k.Time - length) < 1e-3f))
                Issue($"{seqName}: no start and end text keys ({string.Join(", ", textKeys.Select(k => $"{k.Time}={k.Text}"))})");
            var links = Skyrim.Meshes.NiflyRaw.Links(seq);
            float worst = 0; string worstWhere = "";
            foreach (var e in expected)
            {
                string shapeName = shapeOf.TryGetValue(e.Shape, out var bs) ? bs.name.get() : "";
                var link = links.FirstOrDefault(l => l.nodeName.get() == shapeName && l.ctrlID.get() == e.Variable.ToString(System.Globalization.CultureInfo.InvariantCulture));
                if (link is null) { Issue($"{seqName}: {shapeName} variable {e.Variable} not in it"); continue; }
                if (link.propType.get() != "BSLightingShaderProperty" || link.ctrlType.get() != "BSLightingShaderPropertyFloatController") Issue($"{seqName}: {shapeName} {e.Variable}: {link.propType.get()} {link.ctrlType.get()}");
                // Its controller: on that shape's shader, that variable, a blend interpolator the manager sets.
                if (hdr.GetBlockById(link.controllerRef.index) is not nifly.BSLightingShaderPropertyFloatController fc || fc.typeOfControlledVariable != (uint)e.Variable
                    || bs is null || fc.targetRef.index != hdr.GetBlockID(nif.GetShader(bs)) || hdr.GetBlockById(fc.interpolatorRef.index) is not nifly.NiBlendFloatInterpolator)
                { Issue($"{seqName}: {shapeName} {e.Variable}: its controller isn't that shader's"); continue; }
                bool onShader = false;
                for (uint cr = nif.GetShader(bs).controllerRef.index, guard = 0; cr != uint.MaxValue && guard < 32; guard++)
                {
                    if (cr == link.controllerRef.index) { onShader = true; break; }
                    cr = (hdr.GetBlockById(cr) as nifly.NiTimeController)?.nextControllerRef.index ?? uint.MaxValue;
                }
                if (!onShader) Issue($"{seqName}: {shapeName} {e.Variable}: its controller isn't in the shader's chain");
                if (hdr.GetBlockById(link.interpolatorRef.index) is not nifly.NiFloatInterpolator fi || hdr.GetBlockById(fi.dataRef.index) is not nifly.NiFloatData fd)
                { Issue($"{seqName}: {shapeName} {e.Variable}: no keys"); continue; }
                var k = Enumerable.Range(0, (int)fd.data.GetNumKeys()).Select(i => Skyrim.Meshes.NifKeys.Read(fd.data, i)).ToList();
                float Written(float t)
                {
                    if (t <= k[0].Time) return k[0].Value;
                    for (int i = 1; i < k.Count; i++)
                        if (t <= k[i].Time) return k[i - 1].Value + (k[i].Value - k[i - 1].Value) * (t - k[i - 1].Time) / MathF.Max(k[i].Time - k[i - 1].Time, 1e-6f);
                    return k[^1].Value;
                }
                // Every frame and every quarter frame between (as Skyrim blends keys).
                for (int h = 0; h <= 4 * (frames - 1); h++)
                {
                    float local = h / 120f, t = Math.Min(p.Start + local, p.Stop);
                    float want = p.Source == ownSource ? e.At(t) : e.Resting, got = Written(local);
                    float err = MathF.Abs(want - got);
                    if (err > worst) { worst = err; worstWhere = $"{shapeName} {e.Variable} at {local:F3}s: Morrowind {want:F4}, ours {got:F4}"; }
                }
            }
            if (worst > 0.01f) Issue($"{seqName}: up to {worst:F4} off ({worstWhere})");
            else lines.Add($"  {seqName}: {links.Count} values, up to {worst:F4} off over {frames} frames and the quarter frames between{(p.Source == ownSource ? "" : " (not its own .kf: as they rest)")}");
        }
        foreach (var extra in sequences.Keys.Where(k => !plan.Any(p => CreatureRig.LookEvent(p.Clip) == k))) Issue($"{extra}: a sequence for no clip");

        // ---- That they play ----
        string project = CreatureGraph.ProjectName(id), meshes = Path.Combine(dataDir, "meshes");
        string animData = Path.Combine(meshes, "animationdata", project + ".txt");
        var dataLines = File.Exists(animData) ? File.ReadAllLines(animData) : [];
        var mainBehavior = Directory.GetFiles(Path.Combine(dataDir, CreatureRig.MeshFolder(id), "Behaviors"), "*.hkx").FirstOrDefault();
        var mainEvents = mainBehavior is null ? [] : CreatureBehavior.Variant<hkbBehaviorGraph>(CreatureBehavior.Hkx(File.ReadAllBytes(mainBehavior))).m_data!.m_stringData!.m_eventNames.ToHashSet();
        var mainCharacter = Directory.GetFiles(Path.Combine(dataDir, CreatureRig.MeshFolder(id), "Characters"), "*.hkx").FirstOrDefault();
        var clipNames = mainCharacter is null ? [] : CreatureBehavior.Variant<hkbCharacterData>(CreatureBehavior.Hkx(File.ReadAllBytes(mainCharacter))).m_stringData!.m_animationNames
            .Select(a => Path.GetFileNameWithoutExtension(a)).ToList();
        // The animation data's clip generators (name, its animation's index, speed, crops, event count, events): each that
        // plays a clip with a look must send it at its start.
        var playedClips = new HashSet<string>();
        for (int i = 6; i + 5 < dataLines.Length;)
        {
            if (dataLines[i].Length == 0) { i++; continue; }
            if (!int.TryParse(dataLines[i + 1], out int clipIndex) || !int.TryParse(dataLines[i + 5], out int count)) break;
            var evs = dataLines.Skip(i + 6).Take(count).ToList();
            string clip = clipIndex < clipNames.Count ? clipNames[clipIndex] : "";
            playedClips.Add(clip);
            if (sequences.ContainsKey(CreatureRig.LookEvent(clip)) && !evs.Contains(CreatureRig.LookEvent(clip) + ":0"))
                Issue($"{dataLines[i]}: plays {clip} but doesn't send {CreatureRig.LookEvent(clip)} as it starts");
            i += 6 + count;
        }
        foreach (var seqName in sequences.Keys)
        {
            if (!mainEvents.Contains(seqName)) Issue($"{seqName}: not one of the main graph's events");
            if (!playedClips.Any(c => CreatureRig.LookEvent(c) == seqName)) lines.Add($"  {seqName}: its clip isn't one the graph plays");
        }
        string? bged = null;
        using (var extras = new nifly.vectoruint32())
        {
            root.extraDataRefs.GetIndices(extras);
            foreach (uint x in extras)
                if (hdr.GetBlockTypeStringById(x) == "BSBehaviorGraphExtraData")
                {
                    var g = cache.EditableBlockById<nifly.BSBehaviorGraphExtraData>(x);
                    bged = g.behaviorGraphFile.get();
                    if (g.controlsBaseSkel) Issue("its BGED drives the skeleton");
                }
        }
        if (bged is null) { Issue("no BGED: nothing plays its sequences"); return new Report(shapes.Count, expected.Count, issues, lines); }
        if (!bged.Equals(CreatureRig.LookProject(id), StringComparison.OrdinalIgnoreCase)) Issue($"BGED {bged}, expected {CreatureRig.LookProject(id)}");
        string projectFile = Path.Combine(meshes, bged), folder = Path.GetDirectoryName(projectFile)!, subProject = Path.GetFileNameWithoutExtension(projectFile);
        if (!File.Exists(projectFile)) { Issue($"no {bged}"); return new Report(shapes.Count, expected.Count, issues, lines); }
        var proj = CreatureBehavior.Variant<hkbProjectData>(CreatureBehavior.Hkx(File.ReadAllBytes(projectFile)));
        string characterFile = Path.Combine(folder, proj.m_stringData!.m_characterFilenames[0]);
        if (!File.Exists(characterFile)) { Issue($"no {characterFile}"); return new Report(shapes.Count, expected.Count, issues, lines); }
        var character = CreatureBehavior.Variant<hkbCharacterData>(CreatureBehavior.Hkx(File.ReadAllBytes(characterFile)));
        if (!File.Exists(Path.Combine(folder, character.m_stringData!.m_rigName))) Issue($"no {character.m_stringData.m_rigName}");
        string behaviorFile = Path.Combine(folder, character.m_stringData.m_behaviorFilename);
        if (!File.Exists(behaviorFile)) { Issue($"no {behaviorFile}"); return new Report(shapes.Count, expected.Count, issues, lines); }
        var graph = CreatureBehavior.Variant<hkbBehaviorGraph>(CreatureBehavior.Hkx(File.ReadAllBytes(behaviorFile)));
        var events = graph.m_data!.m_stringData!.m_eventNames;
        var machine = graph.m_rootGenerator as hkbStateMachine;
        var played = new Dictionary<string, string>();   // event -> the sequence its transition's state plays
        foreach (var tr in machine?.m_wildcardTransitions?.m_transitions ?? [])
        {
            var state = machine!.m_states.FirstOrDefault(st => st.m_stateId == tr.m_toStateId);
            if (tr.m_eventId >= 0 && tr.m_eventId < events.Count && state?.m_generator is BGSGamebryoSequenceGenerator sg) played[events[tr.m_eventId]] = sg.m_pSequence;
            if ((tr.m_flags & 512) == 0) Issue($"{events.ElementAtOrDefault(tr.m_eventId)}: its state can't start over");
        }
        foreach (var seqName in sequences.Keys)
            if (!played.TryGetValue(seqName, out var plays) || plays != seqName) Issue($"{seqName}: the body's graph doesn't play it on its event");
        string subData = Path.Combine(meshes, "animationdata", subProject + ".txt");
        if (!File.Exists(subData)) Issue($"no animation data for {subProject}");
        string dirlist = Path.Combine(meshes, "animationdata", "dirlist.txt");
        if (!File.Exists(dirlist) || !File.ReadAllLines(dirlist).Contains(subProject + ".txt", StringComparer.OrdinalIgnoreCase)) Issue($"{subProject}.txt not in animationdata\\dirlist.txt");
        lines.Add($"  played by {bged}: {played.Count} clips' looks on their events");
        return new Report(shapes.Count, expected.Count, issues, lines);
    }

    /// <summary>A Morrowind texture's pixels as the game finds it (a .dds of the name first, then the named file, .tga, .bmp).</summary>
    static (uint[] Px, int W, int H)? Picture(Tes3Data data, string name)
    {
        string key = Path.ChangeExtension(Tes3Bsa.Normalize(name).Replace(@"textures\", ""), null);
        string? source = new[] { ".dds", Path.GetExtension(name), ".tga", ".bmp" }.Select(ext => @"textures\" + key + ext).FirstOrDefault(data.Exists);
        if (source is null || data.Read(source) is not { } bytes) return null;
        return source.EndsWith(".dds", StringComparison.OrdinalIgnoreCase) ? Util.DdsImage.Decode(bytes) : source.EndsWith(".tga", StringComparison.OrdinalIgnoreCase) ? MwTextures.DecodeTga(bytes) : MwTextures.DecodeBmp(bytes);
    }
}
