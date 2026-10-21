using System.Numerics;
using TamrielMerge.Morrowind;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// A Morrowind creature as a <see cref="CreatureRig"/> (user, 2026-10-10: "do the type of stuff we are doing for the
/// enemies be the same for morrowind"), from the game's own files: its model (meshes\r\X.nif: the Bip01 skeleton and
/// skinned NiTriShapes, NiSkinData per-bone skin-to-bone transforms and weights) and its animations
/// (meshes\r\xX.kf: NiSequenceStreamHelper with one NiKeyframeController per bone, named by NiStringExtraData in
/// order, and NiTextKeyExtraData group keys "Idle: Start" .. "Death1: Stop"), each wanted group sampled at 30 frames a
/// second. Units stay Morrowind's (one to one, as the rest of the conversion). Textures go through
/// <see cref="MwTextures"/> (DDS already).
/// </summary>
public static class MwCreatureSource
{
    /// <summary>Skyrim clip names and the Morrowind animation groups that give them (first found wins).</summary>
    static readonly (string Clip, string[] Groups)[] Wanted =
    [
        ("idle", ["Idle", "Idle1", "Idle2"]),
        ("walk", ["WalkForward", "Walkforward", "SwimWalkForward"]),
        ("run", ["RunForward", "Runforward", "SwimRunForward"]),
        ("attack", ["Attack1", "Attack2", "Attack3"]),
        ("attack2", ["Attack2"]),
        ("hit", ["Hit1", "Hit2", "Hit3"]),
        ("death", ["Death1", "Death2", "Death3"]),
        ("knockdown", ["KnockDown"]),
    ];
    const float Fps = 30;

    /// <summary>The node whose horizontal travel is the actor's movement (as Morrowind's engine takes it): "Bip01", else
    /// "Root Bone". Its X and Y stay where they are at the start of each animation group, so a flight or a walk plays in
    /// place and the actor itself moves (2026-10-21: left in, the cliff racer's flight carried the body 1,888 units ahead
    /// in 20 frames).</summary>
    public static readonly string[] MovementRoots = ["Bip01", "Root Bone"];

    /// <summary>An animation group's times: "Group: Start" .. "Group: Stop", and the part it repeats ("Loop Start" ..
    /// "Loop Stop"; NaN when it has none).</summary>
    public readonly record struct GroupTimes(float Start, float Stop, float LoopStart, float LoopStop)
    {
        public bool Whole => !float.IsNaN(Start) && !float.IsNaN(Stop) && Stop > Start;
        public bool Loops => !float.IsNaN(LoopStart) && !float.IsNaN(LoopStop) && LoopStop > LoopStart;
    }

    /// <summary>The animation groups a model's .kf names (text keys "Group: Start" .. "Group: Stop"), with their times.</summary>
    public static Dictionary<string, GroupTimes> Groups(NiTextKeyExtraData? keys)
    {
        var groups = new Dictionary<string, GroupTimes>(StringComparer.OrdinalIgnoreCase);
        foreach (var (time, text) in keys?.Keys ?? [])
            foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                int colon = line.IndexOf(':');
                if (colon < 0) continue;
                string g = line[..colon].Trim(), what = line[(colon + 1)..].Trim().ToLowerInvariant();
                var cur = groups.GetValueOrDefault(g, new GroupTimes(float.NaN, float.NaN, float.NaN, float.NaN));
                groups[g] = what switch
                {
                    "start" => cur with { Start = time },
                    "stop" => cur with { Stop = time },
                    "loop start" => cur with { LoopStart = time },
                    "loop stop" => cur with { LoopStop = time },
                    _ => cur,
                };
            }
        foreach (var g in groups.Keys.ToList()) if (!groups[g].Whole && float.IsNaN(groups[g].Start)) groups.Remove(g);
        return groups;
    }

    /// <summary>Our clips that repeat (Skyrim loops them): Morrowind plays their group once from its start, then repeats
    /// the loop part, so the loop part is what they are.</summary>
    static readonly HashSet<string> Looping = ["idle", "walk", "run"];

    /// <summary>The group each of our clips comes from (the first of its candidates the .kf has whole), and the time span
    /// taken: the loop part for repeating clips that have one, else the whole group.</summary>
    public static List<(string Clip, string Group, float Start, float Stop)> ChosenGroups(Dictionary<string, GroupTimes> groups) =>
        Wanted.Select(w => (w.Clip, Group: w.Groups.FirstOrDefault(c => groups.TryGetValue(c, out var g) && g.Whole)))
            .Where(x => x.Group is not null).Select(x =>
            {
                var g = groups[x.Group!];
                return Looping.Contains(x.Clip) && g.Loops ? (x.Clip, x.Group!, g.LoopStart, g.LoopStop) : (x.Clip, x.Group!, g.Start, g.Stop);
            }).ToList();

    /// <summary>Morrowind's sound generator types (SNDG DATA) by the name its animations' "SoundGen:" keys use.</summary>
    static readonly Dictionary<string, int> SoundGenTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["left"] = 0, ["right"] = 1, ["swimleft"] = 2, ["swimright"] = 3, ["moan"] = 4, ["roar"] = 5, ["scream"] = 6, ["land"] = 7,
    };

    /// <summary>The model's .kf text keys and its controllers' keyframe data by node name (the order the string extra data
    /// names them in).</summary>
    public static (NiTextKeyExtraData? Keys, Dictionary<string, NiKeyframeData> Tracks) ReadKf(Nif4 kf)
    {
        var tracks = new Dictionary<string, NiKeyframeData>(StringComparer.Ordinal);
        if (kf.Root is not NiObjectNET helper) return (null, tracks);
        var names = new List<string>();
        NiTextKeyExtraData? keys = null;
        for (int e = helper.ExtraData; e >= 0;)
        {
            var x = kf.Get<NiExtraData>(e);
            if (x is NiTextKeyExtraData tk) keys = tk;
            else if (x is NiStringExtraData se) names.Add(se.Value);
            e = x?.Next ?? -1;
        }
        int k = 0;
        for (int c = helper.Controller; c >= 0; k++)
        {
            var ctl = kf.Get<NiController>(c);
            if (ctl is null) break;
            if (k < names.Count && kf.Get<NiKeyframeData>(ctl.Data) is { } kd) tracks.TryAdd(names[k], kd);
            c = ctl.Next;
        }
        return (keys, tracks);
    }

    /// <param name="world">The game's records: with them the creature gets its own sounds where its animations play them,
    /// its attacks' hit moments, and its speeds (<see cref="AddGameData"/>); <paramref name="id"/> names its sounds.</param>
    public static CreatureRig Build(Tes3Data data, string model, MwTextures textures, string modOutDir, List<string> problems, MwWorld? world = null, string? id = null)
    {
        var rig = BuildBody(data, model, textures, modOutDir, problems);
        if (world is not null && id is not null) AddGameData(rig, data, world, model, id, problems);
        return rig;
    }

    /// <summary>The creatures (CREA) drawn with <paramref name="model"/>, the original first (others name it in CNAM).</summary>
    public static List<Tes3Record> CreaturesWith(MwWorld world, string model)
    {
        string bare = model.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? model[7..] : model;
        return world.Objects.Values.Where(o => o.Type == "CREA" && string.Equals(o.Str("MODL"), bare, StringComparison.OrdinalIgnoreCase))
            .OrderBy(o => o.Str("CNAM") is null ? 0 : 1).ThenBy(o => o.Id, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>A creature's NPDT as integers: type, level, the eight attributes (Strength, Intelligence, Willpower, Agility,
    /// Speed, Endurance, Personality, Luck), health, magicka, fatigue, soul, combat, magic, stealth, three attacks' minimum
    /// and maximum, gold.</summary>
    public static int[] Stats(Tes3Record crea) => crea.Get("NPDT") is { Data.Length: >= 96 } d ? Enumerable.Range(0, 24).Select(i => BitConverter.ToInt32(d.Data, i * 4)).ToArray() : new int[24];

    static float Setting(MwWorld world, string name, float fallback) =>
        world.Settings.TryGetValue(name, out var s) && s.Get("FLTV") is { Data.Length: >= 4 } f ? BitConverter.ToSingle(f.Data, 0) : fallback;

    /// <summary>
    /// What the game's records add to a creature's body and animations, so it plays as it did:
    /// <list type="bullet">
    /// <item>its animations' sound keys ("SoundGen: Roar" through the creature's own sound generators (SNDG, by its
    /// original's ID), else the game's defaults; "Sound: X" the sound X) as its own sounds at the same moments, but no
    /// footfalls for a creature that flies (none in the air); "Attack1: Hit" as the moment its attack lands (HitFrame);</item>
    /// <item>its "Bounding Box" node as the actor's collision box;</item>
    /// <item>its speeds from the game's settings and its Speed: flying fMinFlySpeed + Speed% of the way to fMaxFlySpeed,
    /// walking fMinWalkSpeedCreature .. fMaxWalkSpeedCreature and running that times fBaseRunMultiplier.</item>
    /// </list>
    /// </summary>
    static void AddGameData(CreatureRig rig, Tes3Data data, MwWorld world, string model, string id, List<string> problems)
    {
        var creatures = CreaturesWith(world, model);
        if (creatures.Count == 0) { problems.Add($"{model}: no creature uses it"); return; }
        var ids = creatures.Select(c => c.Id).Concat(creatures.Select(c => c.Str("CNAM")).OfType<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool flies = creatures.Any(c => ((c.Get("FLAG")?.Int() ?? 0) & 0x20) != 0);
        var gens = world.Objects.Values.Where(o => o.Type == "SNDG").ToList();
        string? SoundFor(int type) =>
            (gens.FirstOrDefault(g => g.Get("DATA")?.Int() == type && g.Str("CNAM") is { } c && ids.Contains(c))
             ?? gens.FirstOrDefault(g => g.Get("DATA")?.Int() == type && g.Str("CNAM") is null))?.Str("SNAM");
        string pascal = CreatureBehavior.ProjectName(id)[2..^"Project".Length];
        string? Sound(string soundId, string kind)
        {
            if (!world.Objects.TryGetValue(soundId, out var soun) || soun.Type != "SOUN" || soun.Str("FNAM") is not { Length: > 0 } file)
            { problems.Add($"{model}: sound {soundId} not found"); return null; }
            string name = "TMSnd" + pascal + new string(kind.Where(char.IsLetterOrDigit).ToArray());
            if (rig.Sounds.ContainsKey(name)) return name;
            var bytes = data.Read(@"sound\" + file);
            if (bytes is null) { problems.Add($"{model}: sound file {file} not found"); return null; }
            rig.Sounds[name] = Daggerfall.Wav.To16Bit(bytes);
            return name;
        }

        // The model's .kf again for its text keys (the clips' times are the groups' or their loop parts').
        string kfPath = Path.Combine(Path.GetDirectoryName(model.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model)!, "x" + Path.GetFileNameWithoutExtension(model) + ".kf");
        if (data.Read(kfPath) is { } kb)
        {
            var (keys, _) = ReadKf(Nif4.Read(kb, kfPath));
            foreach (var (clip, group, start, stop) in ChosenGroups(Groups(keys)))
            {
                if (!rig.Clips.ContainsKey(clip)) continue;
                var events = new List<(float Time, string Text)>();
                foreach (var (time, text) in keys?.Keys ?? [])
                {
                    if (time < start - 1e-4f || time > stop + 1e-4f) continue;
                    float at = Math.Clamp(time - start, 0, stop - start);
                    foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        int colon = line.IndexOf(':');
                        if (colon < 0) continue;
                        string what = line[..colon].Trim(), arg = line[(colon + 1)..].Trim();
                        if (what.Equals("SoundGen", StringComparison.OrdinalIgnoreCase) && SoundGenTypes.TryGetValue(arg, out int type))
                        {
                            if (flies && type <= 3) continue;   // no footfalls or strokes in the air
                            if (SoundFor(type) is { } soundId && Sound(soundId, arg) is { } name) events.Add((at, "SoundPlay." + name));
                        }
                        else if (what.Equals("Sound", StringComparison.OrdinalIgnoreCase))
                        {
                            if (Sound(arg, arg) is { } name) events.Add((at, "SoundPlay." + name));
                        }
                        else if (what.Equals(group, StringComparison.OrdinalIgnoreCase) && arg.Equals("Hit", StringComparison.OrdinalIgnoreCase) && clip.StartsWith("attack"))
                        {
                            events.Add((Math.Max(0, at - 0.1f), "preHitFrame"));
                            events.Add((at, "HitFrame"));
                        }
                    }
                }
                if (events.Count > 0) rig.Events[clip] = events.OrderBy(e => e.Time).ToList();
            }
        }

        // The collision box.
        var nif = Nif4.Read(data.Read(model.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model)!, model);
        if (nif.Blocks.OfType<NiNode>().FirstOrDefault(n => n.Name.Equals("Bounding Box", StringComparison.OrdinalIgnoreCase) && n.Box is not null) is { Box: { } box } bn)
        {
            // Standing on the ground as Skyrim's actor boxes do (the controller rises from the feet), up to the top of the game's box,
            // its width and depth the game's: for a flier the whole height its body hangs at.
            var c = bn.Transform.Apply(box.Center); var e = box.Extents * bn.Transform.Scale;
            float top = MathF.Max(c.Z + e.Z, 16);
            rig.Box = (new Vector3(c.X, c.Y, top / 2), new Vector3(e.X, e.Y, top / 2));
        }

        // Speeds.
        var (walk, run) = Speeds(world, Stats(creatures[0])[6], flies);
        rig.MoveSpeeds["walk"] = walk;
        rig.MoveSpeeds["run"] = run;
    }

    /// <summary>
    /// The numbers of the creatures drawn with <paramref name="model"/> for their own race and actors
    /// (<see cref="CreatureRecords.AddOwn"/>): the original's for the race, each creature's by the Skyrim actor the
    /// conversion makes of it ("MW" + its ID's letters and digits). Health, magicka, fatigue as stamina, the three attacks'
    /// least and most (Morrowind picks one of them), reach (fCombatDistance), flee, kind (NPDT type: creature, daedra,
    /// undead, humanoid) and speeds.
    /// </summary>
    public static (CreatureRecords.Stats Race, Dictionary<string, CreatureRecords.Stats> Actors) RecordStats(MwWorld world, string model)
    {
        var creatures = CreaturesWith(world, model);
        if (creatures.Count == 0) throw new InvalidDataException($"{model}: no creature uses it");
        CreatureRecords.Stats Of(Tes3Record c)
        {
            var s = Stats(c);
            var attacks = Enumerable.Range(0, 3).Select(i => (Min: s[17 + i * 2], Max: s[18 + i * 2])).Where(a => a.Max > 0).ToList();
            int flag = c.Get("FLAG")?.Int() ?? 0;
            bool flies = (flag & 0x20) != 0;
            var (walk, run) = Speeds(world, s[6], flies);
            int flee = c.Get("AIDT") is { Data.Length: >= 4 } ai ? ai.Data[3] : 0;
            var kind = s[0] switch { 1 => CreatureRecords.CreatureKind.Daedra, 2 => CreatureRecords.CreatureKind.Undead, 3 => CreatureRecords.CreatureKind.Humanoid, _ => CreatureRecords.CreatureKind.Animal };
            return new CreatureRecords.Stats(s[10], s[11], s[12], attacks.Count > 0 ? (float)attacks.Average(a => a.Min) : 0, attacks.Count > 0 ? (float)attacks.Average(a => a.Max) : 0,
                Setting(world, "fCombatDistance", 128), flee, kind, walk, run, flies, Swims: (flag & 0x10) != 0, Walks: (flag & 0x40) != 0);
        }
        return (Of(creatures[0]), creatures.ToDictionary(c => "MW" + MwPluginBuilder.Sanitize(c.Id), Of, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Walking and running speeds (units a second) for a creature's Speed: a flier's fMinFlySpeed + Speed% of the
    /// way to fMaxFlySpeed both; else fMinWalkSpeedCreature .. fMaxWalkSpeedCreature, running fBaseRunMultiplier times that.</summary>
    static (float Walk, float Run) Speeds(MwWorld world, float speed, bool flies)
    {
        if (flies)
        {
            float fly = Setting(world, "fMinFlySpeed", 5) + speed / 100f * (Setting(world, "fMaxFlySpeed", 300) - Setting(world, "fMinFlySpeed", 5));
            return (fly, fly);
        }
        float walk = Setting(world, "fMinWalkSpeedCreature", 5) + speed / 100f * (Setting(world, "fMaxWalkSpeedCreature", 300) - Setting(world, "fMinWalkSpeedCreature", 5));
        return (walk, walk * Setting(world, "fBaseRunMultiplier", 1.75f));
    }

    static CreatureRig BuildBody(Tes3Data data, string model, MwTextures textures, string modOutDir, List<string> problems)
    {
        model = model.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var nif = Nif4.Read(data.Read(model) ?? throw new FileNotFoundException(model), model);
        string kfPath = Path.Combine(Path.GetDirectoryName(model)!, "x" + Path.GetFileNameWithoutExtension(model) + ".kf");
        var kf = data.Read(kfPath) is { } kb ? Nif4.Read(kb, kfPath) : null;
        if (kf is null) problems.Add($"{model}: no {kfPath}");

        var root = nif.Root as NiNode ?? throw new InvalidDataException($"{model}: no root node");
        // Nodes parents-first; shapes and collision left out of the skeleton.
        var parent = new Dictionary<int, int>();
        var order = new List<NiNode>();
        var queue = new Queue<NiNode>();
        queue.Enqueue(root);
        while (queue.Count > 0)
        {
            var n = queue.Dequeue();
            foreach (int c in n.Children)
                if (nif.Get<NiNode>(c) is { } child && child is not NiTriShape && child.Type != "RootCollisionNode")
                {
                    parent[child.Index] = n.Index;
                    order.Add(child);
                    queue.Enqueue(child);
                }
        }
        var world = new Dictionary<int, NifTransform> { [root.Index] = root.Transform };
        foreach (var n in order) world[n.Index] = n.Transform.Then(world[parent[n.Index]]);

        var rig = new CreatureRig();
        rig.Bones.Add(new CreatureRig.Bone(CreatureRig.RootBone, -1, Vector3.Zero, Quaternion.Identity));
        var boneOf = new Dictionary<int, int>();
        var restLocal = new List<NifTransform> { NifTransform.Identity };
        foreach (var n in order)
        {
            int p = parent[n.Index];
            // Bones right under the model's root carry the root's own transform (the root becomes "NPC Root [Root]").
            var local = p == root.Index ? n.Transform.Then(root.Transform) : n.Transform;
            boneOf[n.Index] = rig.Bones.Count;
            // Skyrim binds bones by name: a repeated name (Morrowind's "Dummy15" twice) gets a number.
            string name = n.Name;
            for (int k = 2; rig.Bones.Any(b => b.Name == name); k++) name = $"{n.Name} {k}";
            rig.Bones.Add(new CreatureRig.Bone(name, p == root.Index ? 0 : boneOf[p], local.Translation, Rot(local.Rotation)));
            restLocal.Add(local);
            if (MathF.Abs(n.Transform.Scale - 1) > 1e-3f) problems.Add($"{model}: bone {n.Name} scale {n.Transform.Scale} (taken as 1)");
        }

        // Meshes: skinned shapes from their bind, the rest riding their parent node.
        var shapeParent = new Dictionary<int, int>();
        foreach (var b in nif.Blocks.OfType<NiNode>())
            foreach (int c in b.Children) if (nif.Get<NiTriShape>(c) is { } s) shapeParent[s.Index] = b.Index;
        foreach (var shape in nif.Blocks.OfType<NiTriShape>().Where(s => s is not NiParticles && !s.Hidden && shapeParent.ContainsKey(s.Index)))
        {
            if (nif.Get<NiTriShapeData>(shape.Data) is not { } d || d.Vertices.Length == 0) continue;
            int nv = d.Vertices.Length;
            var v = new Vector3[nv]; var nrm = new Vector3[nv];
            var weights = new (int Bone, float W)[nv][];
            Dictionary<int, Matrix4x4>? skinToBone = null;
            if (nif.Get<NiSkinInstance>(shape.Skin) is { } skin && nif.Get<NiSkinData>(skin.Data) is { } sd)
            {
                // As Morrowind skins it: each vertex in the mesh's skin space, each bone's own skin-to-bone transform (the pose the
                // mesh was bound in, which isn't always the skeleton's rest), so Skyrim's sum is Morrowind's.
                skinToBone = [];
                var acc = new Dictionary<int, float>[nv];
                for (int i = 0; i < nv; i++) acc[i] = [];
                for (int bi = 0; bi < skin.Bones.Length && bi < sd.Bones.Count; bi++)
                {
                    int node = skin.Bones[bi];
                    if (!boneOf.TryGetValue(node, out int bone)) continue;
                    skinToBone[bone] = RowMatrix(sd.Bones[bi].Transform);
                    foreach (var (vi, w) in sd.Bones[bi].Weights)
                        if (vi < nv && w > 0) acc[vi][bone] = acc[vi].GetValueOrDefault(bone) + w;
                }
                for (int i = 0; i < nv; i++)
                {
                    v[i] = d.Vertices[i];
                    nrm[i] = d.Normals.Length == nv && d.Normals[i].LengthSquared() > 0 ? Vector3.Normalize(d.Normals[i]) : Vector3.UnitZ;
                    var top = acc[i].OrderByDescending(kv => kv.Value).Take(4).ToList();
                    float total = top.Sum(kv => kv.Value);
                    weights[i] = top.Count == 0 ? [(0, 1f)] : top.Select(kv => (kv.Key, kv.Value / total)).ToArray();
                }
                // A vertex on no bone rides the shape's own node at its rest.
                for (int i = 0; i < nv; i++)
                    if (acc[i].Count == 0)
                    {
                        int holder = boneOf.GetValueOrDefault(shapeParent[shape.Index], 0);
                        weights[i] = [(holder, 1f)];
                        if (!skinToBone.ContainsKey(holder))
                        {
                            Matrix4x4.Invert(RowMatrix(world.GetValueOrDefault(shapeParent[shape.Index], NifTransform.Identity)), out var inv);
                            skinToBone[holder] = inv;
                        }
                    }
            }
            else
            {
                int holder = shapeParent[shape.Index];
                var toWorld = shape.Transform.Then(world[holder]);
                int bone = boneOf.GetValueOrDefault(holder, 0);
                for (int i = 0; i < nv; i++)
                {
                    v[i] = toWorld.Apply(d.Vertices[i]);
                    nrm[i] = d.Normals.Length == nv ? Vector3.Normalize(toWorld.Rotate(d.Normals[i])) : Vector3.UnitZ;
                    weights[i] = [(bone, 1f)];
                }
            }
            var uv = d.UVSets.Length > 0 ? d.UVSets[0] : new Vector2[nv];
            var tris = Enumerable.Range(0, d.Triangles.Length / 3).Select(t => new[] { (int)d.Triangles[t * 3], d.Triangles[t * 3 + 1], d.Triangles[t * 3 + 2] }).ToArray();
            var tex = new Dictionary<string, string>();
            foreach (int pi in shape.Properties)
                if (nif.Get<NiTexturingProperty>(pi)?.Base is { } bt && nif.Get<NiSourceTexture>(bt.Source)?.FileName is { } file)
                {
                    var (rel, _) = textures.Ensure(file);
                    if (rel is not null) tex["diffuse"] = Path.Combine(modOutDir, rel);
                }
            rig.Meshes.Add(new CreatureRig.Mesh(shape.Name.Length > 0 ? shape.Name : $"shape{shape.Index}", v, nrm, uv, weights, tris, tex, skinToBone));
        }

        // Animations from the .kf: controllers in order, each named by the next string extra data after the text keys.
        if (kf is not null && kf.Root is NiObjectNET)
        {
            var (keys, byName) = ReadKf(kf);
            // By the model's node name (the first node of that name, as Morrowind binds them).
            var tracks = new Dictionary<int, NiKeyframeData>();
            foreach (var (name, kd) in byName)
                if (order.FirstOrDefault(n => n.Name == name) is { } node && boneOf.TryGetValue(node.Index, out int bi)) tracks[bi] = kd;
            foreach (var kd in tracks.Values.Where(kd => kd.RotationType == 4).Take(1))
                problems.Add($"{model}: a track with per-axis rotation keys (not carried)");
            // Scale keys (the Heart of Akulakhan's beat, a riekling's) are carried, frame by frame.
            bool scaled = tracks.Values.Any(kd => kd.Scales.Any(s => MathF.Abs(s.Value.X - 1) > 1e-4f));
            int movementRoot = MovementRoots.Select(m => rig.Bones.FindIndex(b => b.Name.Equals(m, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i > 0, -1);
            var groups = Groups(keys);
            foreach (var (clip, _, start, stop) in ChosenGroups(groups))
            {
                if (rig.Clips.ContainsKey(clip)) continue;
                int frames = Math.Max(2, (int)MathF.Round((stop - start) * Fps) + 1);
                var clipFrames = new (Vector3, Quaternion)[frames][];
                var clipScales = scaled ? new float[frames][] : null;
                for (int f = 0; f < frames; f++)
                {
                    float t = Math.Min(start + f / Fps, stop);
                    var pose = new (Vector3, Quaternion)[rig.Bones.Count];
                    var scales = new float[rig.Bones.Count];
                    pose[0] = (Vector3.Zero, Quaternion.Identity);
                    scales[0] = 1;
                    for (int b = 1; b < rig.Bones.Count; b++)
                    {
                        var rest = restLocal[b];
                        var tr = rest.Translation; var q = Rot(rest.Rotation);
                        float s = 1;
                        if (tracks.TryGetValue(b, out var kd))
                        {
                            if (kd.Translations.Count > 0) tr = Sample(kd.Translations, t, Vector3.Lerp);
                            if (b == movementRoot && kd.Translations.Count > 0)
                            {
                                var at = Sample(kd.Translations, start, Vector3.Lerp);
                                tr = new Vector3(at.X, at.Y, tr.Z);
                            }
                            if (kd.Rotations.Count > 0) q = Sample(kd.Rotations, t, Quaternion.Slerp);
                            if (kd.Scales.Count > 0) s = Sample(kd.Scales, t, Vector3.Lerp).X;
                            // Keys are relative to the node's parent; under the model root they carry its transform too.
                            if (rig.Bones[b].Parent == 0)
                            {
                                var lt = new NifTransform(Matrix(q), tr, s).Then(root.Transform);
                                tr = lt.Translation; q = Rot(lt.Rotation); s = lt.Scale;
                            }
                        }
                        pose[b] = (tr, q);
                        scales[b] = s;
                    }
                    clipFrames[f] = pose;
                    if (clipScales is not null) clipScales[f] = scales;
                }
                rig.Clips[clip] = new CreatureRig.Clip(Fps, clipFrames, clipScales);
            }
            if (rig.Clips.Count == 0) problems.Add($"{model}: no animation groups found ({string.Join(", ", groups.Keys.Take(12))})");
        }
        return rig;
    }

    static T Sample<T>(List<(float Time, T Value)> keys, float t, Func<T, T, float, T> lerp)
    {
        if (t <= keys[0].Time) return keys[0].Value;
        for (int i = 1; i < keys.Count; i++)
            if (t <= keys[i].Time)
            {
                float span = keys[i].Time - keys[i - 1].Time;
                return lerp(keys[i - 1].Value, keys[i].Value, span > 1e-6f ? (t - keys[i - 1].Time) / span : 1);
            }
        return keys[^1].Value;
    }

    /// <summary>A NIF transform (p' = T + s·R·p) as a row-vector matrix (p' = p·M).</summary>
    internal static Matrix4x4 RowMatrix(NifTransform t)
    {
        var r = t.Rotation; float s = t.Scale;
        return new Matrix4x4(
            r.M11 * s, r.M21 * s, r.M31 * s, 0,
            r.M12 * s, r.M22 * s, r.M32 * s, 0,
            r.M13 * s, r.M23 * s, r.M33 * s, 0,
            t.Translation.X, t.Translation.Y, t.Translation.Z, 1);
    }

    /// <summary>A NIF rotation (column-vector convention, M_ij = row i, column j) as a quaternion.</summary>
    static Quaternion Rot(Matrix4x4 r) => Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(r)));

    static Matrix4x4 Matrix(Quaternion q) => Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(q));
}
