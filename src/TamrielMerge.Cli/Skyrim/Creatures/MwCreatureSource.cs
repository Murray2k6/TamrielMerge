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
    /// <summary>
    /// Skyrim clip names and the Morrowind animation groups that give them (first found wins): every group Morrowind's engine
    /// plays on a creature (OpenMW's reading, apps/openmw/mwmechanics/character.cpp and aiwander.cpp): its idle and the idles
    /// its wander package picks (Idle2..9), walking and running every way, turning (a biped's only: "It seems only bipedal
    /// actors use turning animations"), its three attacks (Morrowind picks one at random; they are chop, slash and thrust),
    /// its hits and deaths (one at random of those it has, counting up from 1), knocked down and knocked out (and the deaths
    /// from them), and all of it again in water (IdleSwim, SwimRunForward... SwimDeath; a swimming hit it lacks is a dry one).
    /// <c>Biped</c> true: only a biped's; false: only a non-biped's (OpenMW's chooseRandomAttackAnimation: "Randomize attacks for
    /// non-bipedal creatures"; a biped fights with its weapon's groups, hand to hand without one).
    /// </summary>
    static readonly (string Clip, string[] Groups, bool? Biped)[] Wanted =
    [
        ("idle", ["Idle", "Idle1"], null),
        .. Enumerable.Range(2, 8).Select(i => ($"idle{i}", new[] { $"Idle{i}" }, (bool?)null)),
        ("walk", ["WalkForward", "SwimWalkForward"], null),
        ("run", ["RunForward", "SwimRunForward"], null),
        ("walkback", ["WalkBack"], null), ("walkleft", ["WalkLeft"], null), ("walkright", ["WalkRight"], null),
        ("runback", ["RunBack"], null), ("runleft", ["RunLeft"], null), ("runright", ["RunRight"], null),
        ("turnleft", ["TurnLeft"], true), ("turnright", ["TurnRight"], true),
        .. Enumerable.Range(1, 3).Select(i => ($"attack{i}", new[] { $"Attack{i}" }, false)),
        .. Enumerable.Range(1, 5).Select(i => ($"hit{i}", new[] { $"Hit{i}" }, (bool?)null)),
        .. Enumerable.Range(1, 5).Select(i => ($"death{i}", new[] { $"Death{i}" }, (bool?)null)),
        ("knockdown", ["KnockDown"], null), ("deathknockdown", ["DeathKnockDown"], null), ("deathknockout", ["DeathKnockOut"], null),
        ("idleswim", ["IdleSwim"], null),
        ("swimwalk", ["SwimWalkForward"], null), ("swimrun", ["SwimRunForward"], null),
        ("swimwalkback", ["SwimWalkBack"], null), ("swimwalkleft", ["SwimWalkLeft"], null), ("swimwalkright", ["SwimWalkRight"], null),
        ("swimrunback", ["SwimRunBack"], null), ("swimrunleft", ["SwimRunLeft"], null), ("swimrunright", ["SwimRunRight"], null),
        ("swimturnleft", ["SwimTurnLeft"], true), ("swimturnright", ["SwimTurnRight"], true),
        .. Enumerable.Range(1, 3).Select(i => ($"swimattack{i}", new[] { $"SwimAttack{i}" }, false)),
        .. Enumerable.Range(1, 3).Select(i => ($"swimhit{i}", new[] { $"SwimHit{i}" }, (bool?)null)),
        ("swimknockdown", ["SwimKnockDown"], null), ("swimdeath", ["SwimDeath"], null),
        ("swimdeathknockdown", ["SwimDeathKnockDown"], null), ("swimdeathknockout", ["SwimDeathKnockOut"], null),
    ];

    /// <summary>The groups knocked out plays (Morrowind repeats the loop part while the actor's fatigue is below zero, then
    /// plays on to the end): as three clips "&lt;clip&gt;_in", "_loop", "_out", or one that repeats whole when the group has no
    /// loop keys (the engine starts it again while still out).</summary>
    static readonly (string Clip, string Group)[] KnockOuts = [("knockout", "KnockOut"), ("swimknockout", "SwimKnockOut")];
    const float Fps = 30;

    /// <summary>The Morrowind groups the engine never plays on a creature, so none of ours comes from them (OpenMW's
    /// character.cpp names every group it plays; these it does not): what each is for.</summary>
    public static readonly Dictionary<string, string> NeverPlayed = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SwimDeath2"] = "a swimming death is SwimDeath alone (deathStateToAnimGroup)",
        ["SwimDeath3"] = "a swimming death is SwimDeath alone (deathStateToAnimGroup)",
    };

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

    /// <summary>Whether our clip repeats (Skyrim loops it): standing, moving and turning, and a knock-out's loop part.
    /// Morrowind plays such a group once from its start, then repeats the loop part, so the loop part is what they are.</summary>
    public static bool IsLooping(string clip)
    {
        string c = clip.ToLowerInvariant();
        string bare = c.StartsWith("swim") ? c[4..] : c;
        if (c.StartsWith("cidle_") || c.StartsWith("cwalk") || c.StartsWith("crun") || c.StartsWith("cturn") || c.EndsWith("_loop")) return true;
        return bare is "idle" or "idleswim" || c == "idleswim" || bare.StartsWith("walk") || bare.StartsWith("run") || bare.StartsWith("turn");
    }

    /// <summary>The group each of our plain clips comes from (the first of its candidates the .kf has whole), and the time
    /// span taken: the loop part for repeating clips that have one, else the whole group.</summary>
    public static List<(string Clip, string Group, float Start, float Stop)> ChosenGroups(Dictionary<string, GroupTimes> groups, bool biped = true) =>
        Wanted.Where(w => w.Biped is null || w.Biped == biped).Select(w => (w.Clip, Group: w.Groups.FirstOrDefault(c => groups.TryGetValue(c, out var g) && g.Whole)))
            .Where(x => x.Group is not null).Select(x =>
            {
                var g = groups[x.Group!];
                return IsLooping(x.Clip) && g.Loops ? (x.Clip, x.Group!, g.LoopStart, g.LoopStop) : (x.Clip, x.Group!, g.Start, g.Stop);
            }).ToList();

    /// <summary>A .kf a creature's clips come from: its own (x&lt;model&gt;.kf) and, for a biped, the humanoid one
    /// (meshes\xbase_anim.kf) under it, as Morrowind loads them (the own file's groups win).</summary>
    public sealed record ClipSource(string Path, NiTextKeyExtraData? Keys, Dictionary<string, NiKeyframeData> Tracks, Dictionary<string, GroupTimes> Groups);

    /// <summary>One of our clips: the source and group it comes from, its time span there, and the text key marking the
    /// moment its blow lands ("Attack1: Hit", "WeaponOneHand: Chop Hit"; null for none).</summary>
    public sealed record ClipPlan(string Clip, int Source, string Group, float Start, float Stop, string? HitKey, string HitEvent = "HitFrame", string? AttachKey = null);

    public const string HumanoidKf = @"meshes\xbase_anim.kf";

    public static List<ClipSource> Sources(Tes3Data data, string model, bool biped)
    {
        model = model.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var paths = new List<string> { Path.Combine(Path.GetDirectoryName(model)!, "x" + Path.GetFileNameWithoutExtension(model) + ".kf") };
        if (biped) paths.Add(HumanoidKf);
        var result = new List<ClipSource>();
        foreach (var p in paths)
            if (data.Read(p) is { } bytes)
            {
                var (keys, tracks) = ReadKf(Nif4.Read(bytes, p));
                result.Add(new ClipSource(p, keys, tracks, Groups(keys)));
            }
        return result;
    }

    /// <summary>A weapon style: its letters in our clip names, Morrowind's attack group, its idle, walk and run groups, and
    /// its attacks (each a part of the attack group: "&lt;Kind&gt; Start" .. its follow-through's stop, landing at
    /// "&lt;Kind&gt; Hit", or a shot's "Release").</summary>
    public sealed record WeaponStyle(string Key, string Group, string Idle, string Walk, string Run, string[] Kinds)
    {
        /// <summary>The letters its other movement groups end in (RunBack1h, TurnLeft2c): a bow's, a crossbow's and a thrown
        /// weapon's are the one-handed ones (OpenMW's fallbackShortWeaponGroup).</summary>
        public string Moves => Key is "bow" or "xbow" or "throw" ? "1h" : Key;
    }

    public static readonly WeaponStyle[] WeaponStyles =
    [
        new("hh", "HandToHand", "Idlehh", "WalkForwardhh", "RunForwardhh", ["Chop", "Slash", "Thrust"]),
        new("1h", "WeaponOneHand", "Idle1h", "WalkForward1h", "RunForward1h", ["Chop", "Slash", "Thrust"]),
        new("2c", "WeaponTwoHand", "Idle2c", "WalkForward2c", "RunForward2c", ["Chop", "Slash", "Thrust"]),
        new("2w", "WeaponTwoWide", "Idle2w", "WalkForward2w", "RunForward2w", ["Chop", "Slash", "Thrust"]),
        new("bow", "BowAndArrow", "Idle1h", "WalkForward1h", "RunForward1h", ["Shoot"]),
        new("xbow", "Crossbow", "IdleCrossbow", "WalkForward1h", "RunForward1h", ["Shoot"]),
        new("throw", "ThrowWeapon", "Idle1h", "WalkForward1h", "RunForward1h", ["Shoot"]),
    ];

    /// <summary>Whether the creatures drawn with <paramref name="model"/> are bipeds (FLAG 0x01: Morrowind gives them the
    /// humanoid animations) and the weapon styles their inventories hold (leveled lists opened; none: hand to hand).</summary>
    public static (bool Biped, List<string> Styles) Kind(MwWorld world, string model)
    {
        var creatures = CreaturesWith(world, model);
        bool biped = creatures.Any(c => ((c.Get("FLAG")?.Int() ?? 0) & 0x01) != 0);
        if (!biped) return (false, []);
        var styles = new HashSet<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Collect(string id)
        {
            if (!seen.Add(id) || !world.Objects.TryGetValue(id, out var r)) return;
            if (r.Type == "WEAP" && r.Get("WPDT") is { Data.Length: >= 10 } d)
            {
                string? style = BitConverter.ToInt16(d.Data, 8) switch { 0 or 1 or 3 or 7 => "1h", 2 or 4 or 8 => "2c", 5 or 6 => "2w", 9 => "bow", 10 => "xbow", _ => null };   // thrown weapons are misc items in Skyrim: hand to hand
                if (style is not null) styles.Add(style);
            }
            else if (r.Type == "LEVI") foreach (var f in r.All("INAM")) Collect(f.Text.TrimEnd('\0'));
        }
        foreach (var c in creatures)
            foreach (var f in c.All("NPCO")) Collect(Tes3Plugin.Encoding.GetString(f.Data, 4, f.Data.Length - 4).TrimEnd('\0'));
        if (styles.Count == 0) styles.Add("hh");
        return (true, WeaponStyles.Select(s => s.Key).Where(styles.Contains).ToList());
    }

    /// <summary>The weapon styles a creature fights in: a biped every style its animations have (Morrowind picks the style by
    /// what it holds when it holds it, whatever its items were to start with), its items' styles first; else none.</summary>
    public static List<string> Styles(IReadOnlyList<ClipSource> sources, IReadOnlyCollection<string> held, bool biped)
    {
        if (!biped) return [];
        // (Not a thrown weapon's: thrown weapons are misc items in Skyrim, so nothing ever holds one to throw.)
        var animated = WeaponStyles.Where(w => w.Key != "throw" && sources.Any(s => (s.Keys?.Keys ?? []).Any(k => k.Text.Split('\n').Any(l => l.Trim().StartsWith(w.Group + ":", StringComparison.OrdinalIgnoreCase)))))
            .Select(w => w.Key).ToHashSet();
        return WeaponStyles.Select(w => w.Key).Where(k => held.Contains(k) || animated.Contains(k)).ToList();
    }

    /// <summary>
    /// Every clip a creature gets and where it comes from: the plain ones (<see cref="Wanted"/>, knocked out's parts) from
    /// the first source with the group (its own .kf first); and for each weapon style it fights in (<see cref="Styles"/>),
    /// that style's idle, walk and run ("cidle_1h", "cwalk_1h", "crun_1h"), running other ways and turning, its attacks
    /// ("weapon_1h_chop" .. a shot's "weapon_bow_shoot"), its drawing and putting away ("equip_1h", "unequip_1h").
    /// </summary>
    public static List<ClipPlan> Plan(IReadOnlyList<ClipSource> sources, IReadOnlyCollection<string> held, bool biped)
    {
        var styles = Styles(sources, held, biped);
        var plan = new List<ClipPlan>();
        ClipPlan? Whole(string clip, IEnumerable<string> candidates, bool loop, string? hit = null)
        {
            foreach (var c in candidates)
                for (int s = 0; s < sources.Count; s++)
                    if (sources[s].Groups.TryGetValue(c, out var g) && g.Whole)
                        return loop && g.Loops ? new ClipPlan(clip, s, c, g.LoopStart, g.LoopStop, null) : new ClipPlan(clip, s, c, g.Start, g.Stop, hit is null ? null : $"{c}: {hit}");
            return null;
        }
        foreach (var (clip, candidates, who) in Wanted)
            if ((who is null || who == biped) && Whole(clip, candidates, IsLooping(clip), clip.Contains("attack") ? "Hit" : null) is { } p) plan.Add(p);
        // Knocked out: up to its loop, the loop, and on from it (one clip repeated whole when it has no loop keys).
        foreach (var (clip, group) in KnockOuts)
            for (int s = 0; s < sources.Count; s++)
            {
                if (!sources[s].Groups.TryGetValue(group, out var g) || !g.Whole) continue;
                if (g.Loops && g.LoopStart >= g.Start && g.LoopStop <= g.Stop)
                {
                    if (g.LoopStart - g.Start >= 1 / Fps) plan.Add(new ClipPlan($"{clip}_in", s, group, g.Start, g.LoopStart, null));
                    plan.Add(new ClipPlan($"{clip}_loop", s, group, g.LoopStart, g.LoopStop, null));
                    if (g.Stop - g.LoopStop >= 1 / Fps) plan.Add(new ClipPlan($"{clip}_out", s, group, g.LoopStop, g.Stop, null));
                }
                else plan.Add(new ClipPlan(clip, s, group, g.Start, g.Stop, null));
                break;
            }
        // A part of an attack group between two of its keys, from the first source that has both.
        ClipPlan? Part(string clip, string group, string from, string[] to, string? hit)
        {
            for (int s = 0; s < sources.Count; s++)
            {
                var keys = sources[s].Keys?.Keys ?? [];
                float? Time(string label) => keys.Where(k => k.Text.Split('\n').Any(l => l.Trim().Equals($"{group}: {label}", StringComparison.OrdinalIgnoreCase))).Select(k => (float?)k.Time).FirstOrDefault();
                if (Time(from) is not { } start) continue;
                var stop = to.Select(Time).FirstOrDefault(t => t is not null && t > start);
                if (stop is null) continue;
                return new ClipPlan(clip, s, group, start, stop.Value, hit is null ? null : $"{group}: {hit}");
            }
            return null;
        }
        foreach (var style in WeaponStyles.Where(w => styles.Contains(w.Key)))
        {
            if (Whole($"cidle_{style.Key}", [style.Idle, "Idle1h", "Idle"], loop: true) is { } i) plan.Add(i);
            if (Whole($"cwalk_{style.Key}", [style.Walk, "WalkForward1h", "WalkForward"], loop: true) is { } w) plan.Add(w);
            if (Whole($"crun_{style.Key}", [style.Run, "RunForward1h", "RunForward"], loop: true) is { } r) plan.Add(r);
            // Running any other way and turning with it drawn (Morrowind's RunBack1h, TurnLeft2c...; the plain group where the
            // style has none, as OpenMW falls back), and the same in water (the swimming groups have no weapon letters).
            foreach (var (way, group) in new[] { ("back", "RunBack"), ("left", "RunLeft"), ("right", "RunRight") })
                if (Whole($"crun{way}_{style.Key}", [group + style.Moves, group + "1h", group], loop: true) is { } rw) plan.Add(rw);
            foreach (var (way, group) in new[] { ("left", "TurnLeft"), ("right", "TurnRight") })
                if (Whole($"cturn{way}_{style.Key}", [group + style.Moves, group + "1h", group], loop: true) is { } tw) plan.Add(tw);
            foreach (var kind in style.Kinds)
            {
                if (kind == "Shoot")
                {
                    // A shot as Skyrim's bow attack wants it: the draw (the arrow in hand at "Shoot Attach") up to full draw, that
                    // pose held, and the release from there (the arrow away at "Shoot Release").
                    if (Part($"weapon_{style.Key}_draw", style.Group, "Shoot Start", ["Shoot Max Attack"], null) is { } draw)
                    {
                        plan.Add(draw with { AttachKey = $"{style.Group}: Shoot Attach" });
                        plan.Add(new ClipPlan($"weapon_{style.Key}_hold", draw.Source, draw.Group, draw.Stop, draw.Stop + 1 / Fps, null));
                    }
                    if (Part($"weapon_{style.Key}_release", style.Group, "Shoot Max Attack", ["Shoot Follow Stop"], "Shoot Release") is { } rel)
                        plan.Add(rel with { HitEvent = "arrowRelease" });
                    continue;
                }
                if (Part($"weapon_{style.Key}_{kind.ToLowerInvariant()}", style.Group, $"{kind} Start",
                    [$"{kind} Large Follow Stop", $"{kind} Medium Follow Stop", $"{kind} Small Follow Stop", $"{kind} Follow Stop"], $"{kind} Hit") is { } a) plan.Add(a);
            }
            if (Part($"equip_{style.Key}", style.Group, "Equip Start", ["Equip Stop"], null) is { } e) plan.Add(e);
            if (Part($"unequip_{style.Key}", style.Group, "Unequip Start", ["Unequip Stop"], null) is { } u) plan.Add(u);
        }
        return plan;
    }

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
        var (biped, styles) = world is not null ? Kind(world, model) : (false, []);
        var sources = Sources(data, model, biped);
        if (sources.Count == 0) problems.Add($"{model}: no animations (no x{Path.GetFileNameWithoutExtension(model)}.kf, not a biped)");
        var plan = Plan(sources, styles, biped);
        var rig = BuildBody(data, model, textures, modOutDir, problems, sources, plan);
        rig.WeaponStyles.AddRange(Styles(sources, styles, biped));
        rig.Biped = biped;
        if (world is not null)
        {
            float multiplier = Setting(world, "fIdleChanceMultiplier", float.NaN);
            if (float.IsNaN(multiplier)) problems.Add($"{model}: no fIdleChanceMultiplier in the game's settings (idles taken as always rolled)");
            rig.IdleSets.AddRange(IdleSets(world, model).Sets.Select(c => IdleOdds(c, float.IsNaN(multiplier) ? 1 : multiplier)));
        }
        if (world is not null && id is not null) AddGameData(rig, data, world, model, id, problems, sources, plan);
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

    /// <summary>The idle chances (AI_W: Idle2..Idle9, 0..100) of the creatures drawn with <paramref name="model"/>, as the
    /// distinct sets among them, and which set each creature (by Morrowind ID) has: none for one without a wander package
    /// (only wandering picks idles). The original's set is first.</summary>
    public static (List<int[]> Sets, Dictionary<string, int> Of) IdleSets(MwWorld world, string model)
    {
        var sets = new List<int[]>();
        var of = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in CreaturesWith(world, model))
        {
            int[] chances = c.Get("AI_W") is { Data.Length: >= 13 } w ? w.Data.Skip(5).Take(8).Select(b => (int)b).ToArray() : new int[8];
            int i = sets.FindIndex(s => s.SequenceEqual(chances));
            if (i < 0) { sets.Add(chances); i = sets.Count - 1; }
            of[c.Id] = i;
        }
        return (sets, of);
    }

    /// <summary>
    /// How often each idle follows a roll, as Morrowind's wander package rolls them (OpenMW's AiWander::getRandomIdle): no
    /// idle at all unless a first roll passes <paramref name="multiplier"/> (fIdleChanceMultiplier); then each idle rolls 0..100
    /// and is a candidate when the roll is within its chance, the highest candidate playing. [0] is no idle (the actor stands
    /// in its plain idle and rolls again), [1..8] Idle2..Idle9. Exact: idle i wins at roll r when every other j either misses
    /// its chance or rolls below r, so P(i) = multiplier ∫₀^cᵢ ∏ⱼ≠ᵢ (1 − max(0, cⱼ − r)/100) dr/100, integrated in 0.001 steps
    /// (the integrand is a polynomial between the chances: midpoint error below 1e-7).
    /// </summary>
    public static float[] IdleOdds(int[] chances, float multiplier)
    {
        var c = chances.Select(x => Math.Min(100.0, x)).ToArray();
        var odds = new double[9];
        const int Steps = 100000;
        for (int i = 0; i < 8; i++)
        {
            if (c[i] <= 0) continue;
            double sum = 0;
            int n = (int)Math.Ceiling(c[i] / 100 * Steps);
            double dr = c[i] / n;
            for (int k = 0; k < n; k++)
            {
                double r = (k + 0.5) * dr, p = 1;
                for (int j = 0; j < 8; j++) if (j != i) p *= 1 - Math.Max(0, c[j] - r) / 100;
                sum += p * dr / 100;
            }
            odds[i + 1] = multiplier * sum;
        }
        odds[0] = 1 - odds.Skip(1).Sum();
        return odds.Select(o => (float)o).ToArray();
    }

    /// <summary>Morrowind's knockdown settings (GMST fKnockDownMult, iKnockDownOddsMult, iKnockDownOddsBase), for its
    /// creatures' hit ability; a missing one is an error, not a guess.</summary>
    public static CreatureRecords.HitRules HitRules(MwWorld world)
    {
        int Int(string name) => world.Settings.TryGetValue(name, out var s) && s.Get("INTV") is { Data.Length: >= 4 } v ? v.Int() : throw new InvalidDataException($"no {name} in Morrowind's settings");
        float mult = Setting(world, "fKnockDownMult", float.NaN);
        if (float.IsNaN(mult)) throw new InvalidDataException("no fKnockDownMult in Morrowind's settings");
        return new CreatureRecords.HitRules(mult, Int("iKnockDownOddsMult"), Int("iKnockDownOddsBase"));
    }

    static float Setting(MwWorld world, string name, float fallback) =>
        world.Settings.TryGetValue(name, out var s) && s.Get("FLTV") is { Data.Length: >= 4 } f ? BitConverter.ToSingle(f.Data, 0) : fallback;

    /// <summary>
    /// What the game's records add to a creature's body and animations, so it plays as it did:
    /// <list type="bullet">
    /// <item>its animations' sound keys ("SoundGen: Roar" through the creature's own sound generators (SNDG, by its
    /// original's ID), else the game's defaults; "Sound: X" the sound X) as its own sounds at the same moments, but no
    /// footfalls for a creature that flies (none in the air); "Attack1: Hit" as the moment its attack lands (HitFrame);</item>
    /// <item>its "Bounding Box" node as the actor's collision box;</item>
    /// <item>its speeds from the game's settings and its Speed (<see cref="Speeds"/>, <see cref="SwimSpeed"/>): each
    /// moving clip's speed and way (<see cref="Motion"/>).</item>
    /// </list>
    /// </summary>
    static void AddGameData(CreatureRig rig, Tes3Data data, MwWorld world, string model, string id, List<string> problems, List<ClipSource> sources, List<ClipPlan> plan)
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

        // Each clip's text keys from the source it was taken from (its times are the group's, a loop part's or an attack's part).
        {
            foreach (var (clip, source, group, start, stop, hitKey, hitEvent, attachKey) in plan)
            {
                if (!rig.Clips.ContainsKey(clip)) continue;
                var keys = sources[source].Keys;
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
                        else if (hitKey is not null && line.Equals(hitKey, StringComparison.OrdinalIgnoreCase))
                        {
                            if (hitEvent == "HitFrame") events.Add((Math.Max(0, at - 0.1f), "preHitFrame"));
                            events.Add((at, hitEvent));
                        }
                        else if (attachKey is not null && line.Equals(attachKey, StringComparison.OrdinalIgnoreCase))
                            events.Add((at, "arrowAttach"));
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

        // Speeds: each moving clip's, its way too (the animation data's motion).
        var first = Stats(creatures[0]);
        var (walk, run) = Speeds(world, first[6], flies);
        float swim = SwimSpeed(world, first, walk, problems);
        foreach (var clip in rig.Clips.Keys)
            if (Motion(clip, walk, run, swim) is { Speed: > 0 } m) { rig.MoveSpeeds[clip] = m.Speed; rig.MoveWays[clip] = m.Way; }

        // How fast its moving clips play: Morrowind plays a movement group at the actor's speed over the group's own pace
        // (OpenMW 0.48 CharacterController::update: speed / mMovementAnimSpeed, at most 10), that pace being the walking
        // group's for a creature that doesn't fly (even when it runs: "MW uses the Walk animation to calculate the animation
        // velocity even if we are running") and the played group's own for one that does; a walking pace of 1 or less, none
        // (played as it is).
        var groupOf = plan.ToDictionary(p => p.Clip, p => p.Group, StringComparer.OrdinalIgnoreCase);
        foreach (var clip in rig.MoveSpeeds.Keys)
        {
            if (!groupOf.TryGetValue(clip, out var played)) continue;
            float pace;
            if (!flies)
            {
                pace = Velocity(sources, WalkEquivalent(clip));
                if (pace <= 1) continue;
            }
            else
            {
                pace = Velocity(sources, played);
                if (pace <= 1) pace = clip.Contains("run", StringComparison.OrdinalIgnoreCase) ? 222.857f : 154.064f;
            }
            rig.MovePaces[clip] = pace;
        }
    }

    /// <summary>The walking group whose pace a creature's moving clip is played by (OpenMW's runStateToWalkState then
    /// movementStateToAnimGroup: no weapon letters, no fallback): "crunback_1h" and "runback" → WalkBack, "swimrun" →
    /// SwimWalkForward.</summary>
    public static string WalkEquivalent(string clip)
    {
        string c = clip.ToLowerInvariant();
        bool swim = c.StartsWith("swim");
        string bare = swim ? c[4..] : c.StartsWith("crun") ? "run" + c[4..c.IndexOf('_')] : c.StartsWith("cwalk") ? "walk" + c[5..c.IndexOf('_')] : c;
        string way = bare.EndsWith("back") ? "Back" : bare.EndsWith("left") ? "Left" : bare.EndsWith("right") ? "Right" : "Forward";
        return (swim ? "SwimWalk" : "Walk") + way;
    }

    /// <summary>
    /// A group's pace (units a second) as Morrowind takes it (OpenMW 0.48 Animation::getVelocity, calcAnimVelocity): in the
    /// newest source with the group (our first: the model's own .kf before the humanoid one), the movement root's ("Bip01",
    /// else "Root Bone") horizontal travel between the group's last "Start" or "Loop Start" key and its last "Loop Stop" key
    /// (else its "Stop"), over that time; 1 or less there, the next source's. 0 for none.
    /// </summary>
    public static float Velocity(IReadOnlyList<ClipSource> sources, string group)
    {
        float Of(ClipSource s)
        {
            var track = MovementRoots.Select(r => s.Tracks.FirstOrDefault(t => t.Key.Equals(r, StringComparison.OrdinalIgnoreCase)).Value).FirstOrDefault(t => t is not null);
            if (track is null || track.Translations.Count == 0) return 0;
            var keys = (s.Keys?.Keys ?? []).SelectMany(k => k.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => (k.Time, Text: l.ToLowerInvariant()))).ToList();
            string g = group.ToLowerInvariant();
            float start = float.MaxValue, stop = 0;
            for (int i = keys.Count - 1; i >= 0; i--)
                if (keys[i].Text == $"{g}: start" || keys[i].Text == $"{g}: loop start") { start = keys[i].Time; break; }
            for (int i = keys.Count - 1; i >= 0; i--)
            {
                if (keys[i].Text == $"{g}: stop") stop = keys[i].Time;
                else if (keys[i].Text == $"{g}: loop stop") { stop = keys[i].Time; break; }
            }
            if (stop <= start) return 0;
            var a = MwKeys.Vector(track.Translations, track.TranslationType, track.TranslationExtra, start);
            var b = MwKeys.Vector(track.Translations, track.TranslationType, track.TranslationExtra, stop);
            return new Vector2(a.X - b.X, a.Y - b.Y).Length() / (stop - start);
        }
        int first = -1;
        for (int i = 0; i < sources.Count; i++) if (sources[i].Groups.ContainsKey(group)) { first = i; break; }
        if (first < 0) return 0;
        float v = 0;
        for (int i = first; i < sources.Count && !(v > 1); i++) v = Of(sources[i]);
        return v;
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
        var idle = IdleSets(world, model);
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
                Setting(world, "fCombatDistance", 128), flee, kind, walk, run, flies, Swims: (flag & 0x10) != 0, Walks: (flag & 0x40) != 0)
            {
                SwimSpeed = SwimSpeed(world, s, walk),
                Agility = s[5],
                IdleSet = idle.Of.GetValueOrDefault(c.Id),
                // Each attack's average blow (Morrowind: least + (most - least) × the attack's strength; chop, slash, thrust).
                AttackAverages = Enumerable.Range(0, 3).Select(i => (s[17 + i * 2] + s[18 + i * 2]) / 2f).ToArray(),
            };
        }
        return (Of(creatures[0]), creatures.ToDictionary(c => "MW" + MwPluginBuilder.Sanitize(c.Id), Of, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>Walking and running speeds (units a second) for a creature's Speed, as Morrowind's engine moves creatures
    /// (OpenMW 0.48 Creature::getMaxSpeed, getWalkSpeed, getRunSpeed): a flier's fMinFlySpeed + Speed% of the way to
    /// fMaxFlySpeed; else fMinWalkSpeedCreature + Speed% of the way to fMaxWalkSpeedCreature, running the same (a creature's
    /// run speed is its walk speed: no fBaseRunMultiplier).</summary>
    static (float Walk, float Run) Speeds(MwWorld world, float speed, bool flies)
    {
        if (flies)
        {
            float fly = Setting(world, "fMinFlySpeed", 5) + speed / 100f * (Setting(world, "fMaxFlySpeed", 300) - Setting(world, "fMinFlySpeed", 5));
            return (fly, fly);
        }
        float walk = Setting(world, "fMinWalkSpeedCreature", 5) + speed / 100f * (Setting(world, "fMaxWalkSpeedCreature", 300) - Setting(world, "fMinWalkSpeedCreature", 5));
        return (walk, walk);
    }

    /// <summary>How much slower than straight on Morrowind moves an actor sideways (OpenMW's Actor::getCurrentSpeed: a
    /// strafing actor's speed times 0.75; it strafes when its sideways movement is more than twice its forward).</summary>
    public const float StrafeFactor = 0.75f;

    /// <summary>A creature's swimming speed (OpenMW 0.48 Creature::getSwimSpeed, Actor::getSwimSpeedImpl): its walk speed
    /// times fSwimRunBase + Athletics × fSwimRunAthleticsMult / 100, a creature's Athletics being its combat, magic or
    /// stealth value as the Athletics skill's specialization (SKIL 8's SKDT) says.</summary>
    public static float SwimSpeed(MwWorld world, int[] stats, float walk, List<string>? problems = null)
    {
        int spec = -1;
        foreach (var plugin in world.Plugins)
            foreach (var r in plugin.Records.Where(r => r.Type == "SKIL"))
                if (r.Get("INDX") is { Data.Length: >= 4 } i && i.Int() == 8 && r.Get("SKDT") is { Data.Length: >= 8 } d) spec = d.Int(4);
        if (spec is < 0 or > 2) { problems?.Add("no Athletics skill record (SKIL 8): swimming at fSwimRunBase alone"); spec = -1; }
        float athletics = spec < 0 ? 0 : stats[14 + spec];
        return walk * (Setting(world, "fSwimRunBase", 0.5f) + athletics / 100f * Setting(world, "fSwimRunAthleticsMult", 0.1f));
    }

    /// <summary>The speed and way (+Y forward) a moving clip carries the actor: walking and running at its speed (sideways
    /// at <see cref="StrafeFactor"/> of it), swimming at its swimming speed; zero for the rest.</summary>
    public static (float Speed, Vector2 Way) Motion(string clip, float walk, float run, float swim)
    {
        string c = clip.ToLowerInvariant();
        bool swimming = c.StartsWith("swim") && !c.StartsWith("swimattack") && !c.StartsWith("swimhit") && !c.StartsWith("swimdeath") && !c.StartsWith("swimknock") && !c.StartsWith("swimturn");
        string bare = swimming ? c[4..] : c.StartsWith("crun") ? "run" + c[4..c.IndexOf('_')] : c.StartsWith("cwalk") ? "walk" + c[5..c.IndexOf('_')] : c;
        if (!bare.StartsWith("walk") && !bare.StartsWith("run")) return (0, Vector2.Zero);
        float speed = swimming ? swim : bare.StartsWith("run") ? run : walk;
        var way = bare.EndsWith("back") ? new Vector2(0, -1) : bare.EndsWith("left") ? new Vector2(-1, 0) : bare.EndsWith("right") ? new Vector2(1, 0) : new Vector2(0, 1);
        return (way.X != 0 ? speed * StrafeFactor : speed, way);
    }

    static CreatureRig BuildBody(Tes3Data data, string model, MwTextures textures, string modOutDir, List<string> problems, List<ClipSource> sources, List<ClipPlan> plan)
    {
        model = model.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var nif = Nif4.Read(data.Read(model) ?? throw new FileNotFoundException(model), model);

        var root = nif.Root as NiNode ?? throw new InvalidDataException($"{model}: no root node");
        // Nodes parents-first; shapes and collision left out of the skeleton.
        var parent = new Dictionary<int, int>();
        var order = new List<NiNode>();
        // Depth first, children in order: the order Morrowind's engine meets them in, so of two nodes with one name the first
        // here is the one its animation tracks drive (Almalexia's two "Dummy07": breadth first took the other, 80 units off).
        void Walk(NiNode n)
        {
            foreach (int c in n.Children)
                if (nif.Get<NiNode>(c) is { } child && child is not NiTriShape && child.Type != "RootCollisionNode")
                {
                    parent[child.Index] = n.Index;
                    order.Add(child);
                    Walk(child);
                }
        }
        Walk(root);
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
            // Skyrim binds bones by name, and a name with a control character (the lich king's "Liche_thigh_left" ends in a tab)
            // doesn't survive the skeleton's files: such characters and the ends' spaces go.
            string name = BoneName(n.Name);
            string bare = name;
            for (int k = 2; rig.Bones.Any(b => b.Name == name); k++) name = $"{bare} {k}";
            rig.Bones.Add(new CreatureRig.Bone(name, p == root.Index ? 0 : boneOf[p], local.Translation, Rot(local.Rotation)));
            restLocal.Add(local);
            if (MathF.Abs(n.Transform.Scale - 1) > 1e-3f) problems.Add($"{model}: bone {n.Name} scale {n.Transform.Scale} (taken as 1)");
        }
        // Where Skyrim hangs a held weapon and shield: exactly on Morrowind's "Weapon Bone" and "Shield Bone" (both games'
        // weapons point along +Y from the grip: `weapon-frames`). Sheathed weapons Morrowind never shows: Skyrim's sheath
        // nodes are there, hidden (scale 0), so a weapon put away is out of sight as in Morrowind.
        foreach (var (node, holder) in new[] { ("WEAPON", "Weapon Bone"), ("SHIELD", "Shield Bone") })
            if (rig.Bones.FindIndex(b => b.Name == holder) is > 0 and var hi && !rig.Bones.Any(b => b.Name == node))
            {
                rig.Bones.Add(new CreatureRig.Bone(node, hi, Vector3.Zero, Quaternion.Identity));
                restLocal.Add(NifTransform.Identity);
            }
        if (rig.Bones.Any(b => b.Name == "WEAPON"))
            foreach (var sheath in new[] { "WeaponSword", "WeaponDagger", "WeaponAxe", "WeaponMace", "WeaponBack", "WeaponBow", "QUIVER", "WeaponStaff" })
            {
                rig.HiddenBones.Add(rig.Bones.Count);
                rig.Bones.Add(new CreatureRig.Bone(sheath, 0, Vector3.Zero, Quaternion.Identity));
                restLocal.Add(NifTransform.Identity);
            }

        // Meshes: skinned shapes from their bind, the rest riding their parent node.
        var shapeParent = new Dictionary<int, int>();
        var meshOf = new Dictionary<int, int>();
        foreach (var b in nif.Blocks.OfType<NiNode>())
            foreach (int c in b.Children) if (nif.Get<NiTriShape>(c) is { } s) shapeParent[s.Index] = b.Index;
        var bodyShapes = BodyShapes(nif);
        var visKeys = VisibilityKeys(nif);
        var scene = SceneParents(nif);
        var visKeyedOf = new Dictionary<int, List<int>>();   // visibility bone -> the keyed blocks it follows
        foreach (var shape in bodyShapes)
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
            var (tex, look, uvSet) = Look(nif, scene, shape, d, textures, modOutDir, model, problems);
            var uv = d.UVSets.Length > uvSet ? d.UVSets[uvSet] : d.UVSets.Length > 0 ? d.UVSets[0] : new Vector2[nv];
            var tris = Enumerable.Range(0, d.Triangles.Length / 3).Select(t => new[] { (int)d.Triangles[t * 3], d.Triangles[t * 3 + 1], d.Triangles[t * 3 + 2] }).ToArray();
            meshOf[shape.Index] = rig.Meshes.Count;
            rig.Meshes.Add(new CreatureRig.Mesh(shape.Name.Length > 0 ? shape.Name : $"shape{shape.Index}", v, nrm, uv, weights, tris, tex, skinToBone,
                Hidden: NeverShown(nif, scene, visKeys, shape.Index), Look: look));
        }

        // Showing and hiding as Morrowind does (its visibility keys: the dremora's armour pieces, the fire atronach's sword
        // flames): a part whose showing depends on keyed blocks rides on "visibility bones", children of its own bones at
        // their very places, scaled 1 while Morrowind shows it and 0 while it doesn't (Skyrim draws no animated visibility;
        // a bone at scale 0 draws what it carries in one point). One set per bone and set of keyed blocks.
        var visBones = new Dictionary<(int Bone, string Keyed), int>();
        int VisBone(int bone, List<int> keyed)
        {
            var key = (bone, string.Join(",", keyed));
            if (visBones.TryGetValue(key, out int vb)) return vb;
            vb = rig.Bones.Count;
            rig.Bones.Add(new CreatureRig.Bone($"{rig.Bones[bone].Name} [vis {visBones.Count + 1}]", bone, Vector3.Zero, Quaternion.Identity));
            restLocal.Add(NifTransform.Identity);
            visBones[key] = vb;
            visKeyedOf[vb] = keyed;
            return vb;
        }
        foreach (var shape in bodyShapes)
        {
            var keyed = VisibilityOf(scene, visKeys, shape.Index);
            if (keyed.Count == 0 || !meshOf.TryGetValue(shape.Index, out int mi)) continue;
            var m = rig.Meshes[mi];
            var weights = m.Weights.Select(ws => ws.Select(p => (VisBone(p.Bone, keyed), p.W)).ToArray()).ToArray();
            var stb = m.SkinToBone?.ToDictionary(kv => VisBone(kv.Key, keyed), kv => kv.Value);
            rig.Meshes[mi] = m with { Weights = weights, SkinToBone = stb };
        }
        // A particle system shows while it and its emitter do (a hidden emitter stops it).
        var particleParent = new Dictionary<int, int>();   // Morrowind node a system hangs from -> its visibility bone
        foreach (var c in nif.Blocks.OfType<NiParticleSystemController>())
        {
            if (nif.Get<NiParticles>(c.Target) is not { } particles || !scene.TryGetValue(particles.Index, out int parentNode) || !boneOf.TryGetValue(parentNode, out int parentBone)) continue;
            var keyed = VisibilityOf(scene, visKeys, particles.Index).Union(c.Emitter >= 0 ? VisibilityOf(scene, visKeys, c.Emitter) : []).Distinct().ToList();
            if (keyed.Count > 0) particleParent[parentNode] = VisBone(parentBone, keyed);
        }

        // Vertex morphs (NiGeomMorpherController: a bear's or a wolf's jaw, a netch's swell, a kwama's feelers), every one
        // on a rigid shape: the vertices that move alike in every target ride one "morph bone" under the bone they hang
        // from, which moves each frame by Morrowind's weighted sum of those targets' offsets (relative morphs: the base and
        // the targets at their weights). Exact: only vertices whose offsets agree in every target share a bone.
        var morphOf = new Dictionary<int, (NiMorphData Data, Vector3[] Offsets)>();   // morph bone -> its targets' offsets (bone space)
        foreach (var c in nif.Blocks.OfType<NiController>().Where(c => c.Type == "NiGeomMorpherController"))
        {
            if (nif.Get<NiTriShape>(c.Target) is not { } shape || !meshOf.TryGetValue(shape.Index, out int mi)) continue;
            if (nif.Get<NiMorphData>(c.Data) is not { } md || md.Morphs.Count < 2) continue;
            if (!md.Relative) { problems.Add($"{model}: {shape.Name}: an absolute morph (not carried)"); continue; }
            var m = rig.Meshes[mi];
            if (m.SkinToBone is not null) { problems.Add($"{model}: {shape.Name}: a morph on a skinned shape (not carried)"); continue; }
            // The shape's own turn and size within its node: offsets are in the shape's space.
            var local = shape.Transform;
            var groups = new Dictionary<string, int>();
            var weights = m.Weights.ToArray();
            for (int vi = 0; vi < md.Morphs[0].Length && vi < weights.Length; vi++)
            {
                var offsets = Enumerable.Range(1, md.Morphs.Count - 1).Select(k => local.Scale * local.Rotate(md.Morphs[k][vi])).ToArray();
                if (offsets.All(o => o.Length() <= 1e-4f)) continue;
                int holder = weights[vi][0].Bone;
                string key = holder + ":" + string.Join(";", offsets.Select(o => $"{MathF.Round(o.X * 1e4f)},{MathF.Round(o.Y * 1e4f)},{MathF.Round(o.Z * 1e4f)}"));
                if (!groups.TryGetValue(key, out int mb))
                {
                    mb = rig.Bones.Count;
                    rig.Bones.Add(new CreatureRig.Bone($"{rig.Bones[holder].Name} [morph {morphOf.Count + 1}]", holder, Vector3.Zero, Quaternion.Identity));
                    restLocal.Add(NifTransform.Identity);
                    morphOf[mb] = (md, offsets);
                    groups[key] = mb;
                }
                weights[vi] = [(mb, 1f)];
            }
            rig.Meshes[mi] = m with { Weights = weights };
        }

        // Where its particle arrays emit (EmitterPoints: along the bones of each emitter's subtree, as Morrowind places them):
        // a hidden mesh per array, a vertex per point, skinned so each sits exactly on its bone (between the node's own bone
        // and an "emit bone" under it that follows the parent's origin in the node's frame: the blend of the two is the point
        // between them, whatever either does), facing the node's +Z, which Morrowind shoots its particles along.
        var emitBoneOf = new Dictionary<int, (int Bone, int ParentNode)>();   // node -> its emit bone, and the parent it follows
        var emitterMeshOf = new Dictionary<int, int>();                       // particle controller -> its emitter mesh
        foreach (var c in nif.Blocks.OfType<NiParticleSystemController>().Where(c => c.Type == "NiBSPArrayController"))
        {
            var points = EmitterPoints(nif, scene, c);
            if (points.Count == 0) continue;
            var pv = new List<Vector3>(); var pn = new List<Vector3>(); var pw = new List<(int Bone, float W)[]>();
            foreach (var p in points)
            {
                if (!boneOf.TryGetValue(p.Node, out int nb)) { problems.Add($"{model}: particle array {c.Index}: emitter node {p.Node} isn't a bone"); continue; }
                var nodeWorld = world.GetValueOrDefault(p.Node, NifTransform.Identity);
                var parentOrigin = p.Parent >= 0 && world.TryGetValue(p.Parent, out var pwld) ? pwld.Translation : root.Transform.Translation;
                if (!emitBoneOf.TryGetValue(p.Node, out var eb))
                {
                    // At rest: the parent's origin in the node's frame.
                    Matrix4x4.Invert(RowMatrix(nodeWorld), out var toNode);
                    var rest = Vector3.Transform(parentOrigin, toNode);
                    eb = (rig.Bones.Count, p.Parent);
                    rig.Bones.Add(new CreatureRig.Bone($"{rig.Bones[nb].Name} [emit]", nb, rest, Quaternion.Identity));
                    restLocal.Add(new NifTransform(Matrix4x4.Identity, rest, 1));
                    emitBoneOf[p.Node] = eb;
                }
                pv.Add(Vector3.Lerp(parentOrigin, nodeWorld.Translation, p.Along) + nodeWorld.Rotate(p.Offset));
                pn.Add(Vector3.Normalize(nodeWorld.Rotate(Vector3.UnitZ)));
                pw.Add(p.Along >= 1 ? [(nb, 1f)] : [(nb, p.Along), (eb.Bone, 1 - p.Along)]);
            }
            if (pv.Count == 0) continue;
            while (pv.Count % 3 != 0) { pv.Add(pv[^1]); pn.Add(pn[^1]); pw.Add(pw[^1]); }
            emitterMeshOf[c.Index] = rig.Meshes.Count;
            rig.Meshes.Add(new CreatureRig.Mesh($"{nif.Get<NiObjectNET>(c.Emitter)?.Name} #{c.Index} [emit]", pv.ToArray(), pn.ToArray(), new Vector2[pv.Count], pw.ToArray(),
                Enumerable.Range(0, pv.Count / 3).Select(t => new[] { 3 * t, 3 * t + 1, 3 * t + 2 }).ToArray(), [], Hidden: true));
        }

        // Its particle systems (flames, steam, sparks), written into the body under the bones their nodes became, a particle
        // array emitting from its own points (user, 2026-10-21: "we need a particle tool then as well"): MwVfx's systems.
        if (nif.Blocks.OfType<NiParticleSystemController>().Any())
        {
            var vfx = new MwVfx(data, textures);
            rig.BodyExtras.Add((body, dataDir, boneIds, meshIds) =>
            {
                var nodes = boneOf.Where(kv => kv.Value < boneIds.Length).ToDictionary(kv => kv.Key, kv => boneIds[kv.Value]);
                foreach (var (node, vb) in particleParent) if (vb < boneIds.Length) nodes[node] = boneIds[vb];
                int made = vfx.AddCreatureParticles(body, nif, nodes,
                    mwShape => meshOf.TryGetValue(mwShape, out int mi) && mi < meshIds.Length ? meshIds[mi] : null,
                    controller => emitterMeshOf.TryGetValue(controller, out int ei) && ei < meshIds.Length ? meshIds[ei] : null);
                rig.ParticleSystems = made;
                // The particles' textures beside the rest of the data.
                foreach (var rel in vfx.UsedTextures)
                {
                    string from = Path.Combine(modOutDir, rel), to = Path.Combine(dataDir, rel);
                    if (!File.Exists(from)) { problems.Add($"{model}: particle texture {rel} not converted"); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Copy(from, to, overwrite: true);
                }
                problems.AddRange(vfx.Problems.Select(p => $"{model}: {p}"));
            });
        }

        // Animations, clip by clip from the source the plan names (its own .kf, or the humanoid one under a biped): each source's
        // tracks by the model's node name (the first node of that name, as Morrowind binds them).
        var tracksBySource = sources.Select(src =>
        {
            var t = new Dictionary<int, NiKeyframeData>();
            foreach (var (name, kd) in src.Tracks)
                if (order.FirstOrDefault(n => n.Name == name) is { } node && boneOf.TryGetValue(node.Index, out int bi)) t[bi] = kd;
            return t;
        }).ToList();
        foreach (var kd in tracksBySource.SelectMany(t => t.Values).Where(kd => kd.RotationType == 4).Take(1))
            problems.Add($"{model}: a track with per-axis rotation keys (not carried)");
        int movementRoot = MovementRoots.Select(m => rig.Bones.FindIndex(b => b.Name.Equals(m, StringComparison.OrdinalIgnoreCase))).FirstOrDefault(i => i > 0, -1);
        // The model's visibility keys run on its own .kf's time.
        string ownKf = Path.Combine(Path.GetDirectoryName(model)!, "x" + Path.GetFileNameWithoutExtension(model) + ".kf");
        int ownSource = sources.FindIndex(s => s.Path.Equals(ownKf, StringComparison.OrdinalIgnoreCase));
        {
            foreach (var (clip, source, _, start, stop, _, _, _) in plan)
            {
                if (rig.Clips.ContainsKey(clip)) continue;
                var tracks = tracksBySource[source];
                // Scale keys (the Heart of Akulakhan's beat, a riekling's) are carried, frame by frame.
                bool scaled = tracks.Values.Any(kd => kd.Scales.Any(s => MathF.Abs(s.Value.X - 1) > 1e-4f)) || restLocal.Any(r => MathF.Abs(r.Scale - 1) > 1e-4f) || rig.HiddenBones.Count > 0 || visKeyedOf.Count > 0;
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
                        float s = rest.Scale;
                        if (tracks.TryGetValue(b, out var kd))
                        {
                            if (kd.Translations.Count > 0) tr = MwKeys.Vector(kd.Translations, kd.TranslationType, kd.TranslationExtra, t);
                            if (b == movementRoot && kd.Translations.Count > 0)
                            {
                                var at = MwKeys.Vector(kd.Translations, kd.TranslationType, kd.TranslationExtra, start);
                                tr = new Vector3(at.X, at.Y, tr.Z);
                            }
                            if (kd.Rotations.Count > 0) q = MwKeys.Rotation(kd.Rotations, kd.RotationType, kd.RotationExtra, t);
                            if (kd.Scales.Count > 0) s = MwKeys.Vector(kd.Scales, kd.ScaleType, kd.ScaleExtra, t).X;
                            // Keys are relative to the node's parent; under the model root they carry its transform too.
                            if (rig.Bones[b].Parent == 0 && !rig.HiddenBones.Contains(b))
                            {
                                var lt = new NifTransform(Matrix(q), tr, s).Then(root.Transform);
                                tr = lt.Translation; q = Rot(lt.Rotation); s = lt.Scale;
                            }
                        }
                        if (rig.HiddenBones.Contains(b)) s = 0;
                        else if (visKeyedOf.TryGetValue(b, out var keyed)) s = keyed.All(k => Shown(nif, visKeys, k, source == ownSource, t)) ? 1 : 0;
                        // A morph bone: Morrowind's weighted targets at this moment (its keys run on the model's own .kf's time).
                        if (morphOf.TryGetValue(b, out var morph))
                        {
                            var sum = Vector3.Zero;
                            if (source == ownSource)
                                for (int k = 1; k < morph.Data.Morphs.Count; k++)
                                    if (k < morph.Data.Weights.Count && morph.Data.Weights[k].Count > 0)
                                        sum += MwKeys.Float(morph.Data.Weights[k], morph.Data.WeightTypes[k], morph.Data.WeightExtra[k], t) * morph.Offsets[k - 1];
                            tr = sum;
                        }
                        pose[b] = (tr, q);
                        scales[b] = s;
                    }
                    // A hidden visibility bone gathers what it carries into one point: its origin moved to the root's, so a part
                    // on several of them (a body skinned to many bones) folds into that one point, not onto its skeleton. An emit
                    // bone sits at its node's parent's origin, in its node's frame, as they are posed now.
                    var hiddenVis = visKeyedOf.Keys.Where(vb => scales[vb] == 0).ToList();
                    if (hiddenVis.Count > 0 || emitBoneOf.Count > 0)
                    {
                        var poseWorld = new Matrix4x4[rig.Bones.Count];
                        for (int b = 0; b < rig.Bones.Count; b++)
                        {
                            var local = Matrix4x4.CreateScale(scales[b]) * Matrix4x4.CreateFromQuaternion(pose[b].Item2) * Matrix4x4.CreateTranslation(pose[b].Item1);
                            poseWorld[b] = rig.Bones[b].Parent < 0 ? local : local * poseWorld[rig.Bones[b].Parent];
                        }
                        foreach (var (node, (eb, parentNode)) in emitBoneOf)
                        {
                            var parentOrigin = boneOf.TryGetValue(parentNode, out int pb) ? poseWorld[pb].Translation : root.Transform.Translation;
                            if (Matrix4x4.Invert(poseWorld[rig.Bones[eb].Parent], out var toNode))
                                pose[eb] = (Vector3.Transform(parentOrigin, toNode), Quaternion.Identity);
                        }
                        foreach (int vb in hiddenVis)
                            if (Matrix4x4.Invert(poseWorld[rig.Bones[vb].Parent], out var inv))
                                pose[vb] = (Vector3.Transform(Vector3.Zero, inv), Quaternion.Identity);
                    }
                    clipFrames[f] = pose;
                    if (clipScales is not null) clipScales[f] = scales;
                }
                rig.Clips[clip] = new CreatureRig.Clip(Fps, clipFrames, clipScales);
            }
            // Shader values Morrowind animates on its animation groups' time (`mw-shape-controllers`: the ancestor ghost's and the
            // specter's alpha, the Heart of Akulakhan's scrolling and fading), each clip's frames as its bones' are; every clip
            // of a creature with any carries every one (outside its own .kf's clips, each value as it rests).
            var animated = ShaderTracks(nif, scene, bodyShapes.Where(s => meshOf.ContainsKey(s.Index) && !rig.Meshes[meshOf[s.Index]].Hidden).ToList());
            if (animated.Count > 0)
                foreach (var (clip, source, _, start, stop, _, _, _) in plan)
                {
                    if (rig.Looks.ContainsKey(clip) || !rig.Clips.TryGetValue(clip, out var made)) continue;
                    var tracks = new List<CreatureRig.LookTrack>();
                    foreach (var a in animated)
                    {
                        // Every frame, and between frames wherever a straight line would leave Morrowind's curve (its quadratic
                        // keys: 2026-10-21, the ghost's fading in death was 0.0152 off between frames with frames alone).
                        float Value(float local) => source == ownSource ? a.At(Math.Min(start + local, stop)) : a.Rest;
                        var keys = new List<(float Time, float Value)>();
                        void Between(float t0, float v0, float t1, float v1, int depth)
                        {
                            float tm = (t0 + t1) / 2, vm = Value(tm);
                            if (depth >= 6 || MathF.Abs(vm - (v0 + v1) / 2) <= 2e-4f) return;
                            Between(t0, v0, tm, vm, depth + 1);
                            keys.Add((tm, vm));
                            Between(tm, vm, t1, v1, depth + 1);
                        }
                        for (int f = 0; f < made.Frames.Length; f++)
                        {
                            float t = f / Fps, v = Value(t);
                            if (f > 0) Between(keys[^1].Time, keys[^1].Value, t, v, 0);
                            keys.Add((t, v));
                        }
                        tracks.Add(new CreatureRig.LookTrack(meshOf[a.Shape], a.Variable, Thin(keys)));
                    }
                    rig.Looks[clip] = new CreatureRig.ClipLook(IsLooping(clip), tracks);
                }
            if (rig.Clips.Count == 0) problems.Add($"{model}: no animation groups found ({string.Join(", ", sources.SelectMany(s => s.Groups.Keys).Take(12))})");
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
    /// <summary>A Morrowind node's name as a Skyrim bone's: control characters out, the ends' spaces trimmed.</summary>
    public static string BoneName(string name) => new string(name.Where(c => !char.IsControl(c)).ToArray()).Trim();

    /// <summary>Each block's visibility keys from the model's visibility controllers (NiVisController on that block).</summary>
    public static Dictionary<int, List<(float Time, bool Visible)>> VisibilityKeys(Nif4 nif) =>
        nif.Blocks.OfType<NiController>().Where(c => c.Type == "NiVisController" && nif.Get<NiVisData>(c.Data) is { Keys.Count: > 0 })
            .GroupBy(c => c.Target).ToDictionary(g => g.Key, g => nif.Get<NiVisData>(g.First().Data)!.Keys);

    /// <summary>Each block's parent in the model's scene (nodes, shapes and particles alike).</summary>
    public static Dictionary<int, int> SceneParents(Nif4 nif)
    {
        var parents = new Dictionary<int, int>();
        foreach (var b in nif.Blocks.OfType<NiNode>()) foreach (int c in b.Children) parents.TryAdd(c, b.Index);
        return parents;
    }

    /// <summary>
    /// How Morrowind draws a shape (`mw-creature-looks`: of the creatures' 1,376 body shapes 127 are alpha-blended or
    /// alpha-tested, 26 see-through by their material, 23 tinted by vertex colours, 14 lit by their own emissive colour,
    /// 4 with glow maps, 2 with dark maps, 13 untextured), with its properties inherited as Morrowind does: its textures
    /// (base; dark map multiplied in; glow map; untextured: its material colour, as MwMeshConverter draws Morrowind's), the
    /// UV set its texture uses, and its look - alpha property, material alpha, tint (its vertex colours where their mode
    /// uses them for colour, which is Morrowind's default; else its material's diffuse colour when it isn't white), emissive
    /// colour, depth test and write, texture wrapping.
    /// </summary>
    public static (Dictionary<string, string> Textures, CreatureRig.MeshLook Look, int UvSet) Look(Nif4 nif, Dictionary<int, int> scene, NiTriShape shape, NiTriShapeData d,
        MwTextures textures, string modOutDir, string model, List<string> problems)
    {
        var tx = Property<NiTexturingProperty>(nif, scene, shape.Index);
        var mat = Property<NiMaterialProperty>(nif, scene, shape.Index);
        var alpha = Property<NiAlphaProperty>(nif, scene, shape.Index);
        var vc = Property<NiVertexColorProperty>(nif, scene, shape.Index);
        var zbuffer = Chain(scene, shape.Index).Select(nif.Get<NiNode>).OfType<NiNode>()
            .SelectMany(n => n.Properties.Reverse().Select(nif.Get<NiFlagProperty>)).FirstOrDefault(p => p?.Type == "NiZBufferProperty");
        string name = shape.Name.Trim();
        string? File(TexDesc? slot) => slot is { } s ? nif.Get<NiSourceTexture>(s.Source)?.FileName : null;
        var tex = new Dictionary<string, string>();
        int uvSet = (int)(tx?.Base?.UVSet ?? 0);

        // Vertex colours tint it where their mode takes them for its colour (Morrowind's default, mode 2); mode 0 ignores them.
        uint vertexMode = vc?.VertexMode ?? 2;
        bool colorsUsed = vertexMode == 2 && d.Colors.Length == d.Vertices.Length && d.Colors.Any(c => c != Vector4.One);
        if (vertexMode == 1 && d.Colors.Length > 0) problems.Add($"{model}: {name}: vertex colours as its emissive colour (not carried)");

        if (File(tx?.Base) is { } baseFile && textures.Ensure(baseFile).Path is { } baseRel)
        {
            string diffuseRel = baseRel;
            // A dark map multiplies the base (Skyrim has no such slot): the two are baked into one.
            if (tx!.Textures.Length > 1 && tx.Textures[1] is { } dark && File(dark) is { } darkFile && textures.Ensure(darkFile).Path is { } darkRel)
            {
                if (dark.UVSet != tx.Base!.Value.UVSet) problems.Add($"{model}: {name}: dark map on UV set {dark.UVSet}, base on {tx.Base.Value.UVSet} (dark map not carried)");
                else diffuseRel = BakeDark(baseRel, darkRel, modOutDir) ?? baseRel;
            }
            tex["diffuse"] = Path.Combine(modOutDir, diffuseRel);
        }
        else tex["diffuse"] = Path.Combine(modOutDir, textures.Solid(colorsUsed ? Vector3.One : mat?.Diffuse ?? Vector3.One));
        if (tx?.Glow is { } glow && File(glow) is { } glowFile && textures.Ensure(glowFile).Path is { } glowRel)
        {
            if (glow.UVSet != uvSet) problems.Add($"{model}: {name}: glow map on UV set {glow.UVSet}, base on {uvSet} (drawn on the base's)");
            tex["glow"] = Path.Combine(modOutDir, glowRel);
            if (mat is { } gm && gm.Emissive.X + gm.Emissive.Y + gm.Emissive.Z > 0.05f) problems.Add($"{model}: {name}: a glow map and an emissive colour (the glow map is drawn)");
        }
        foreach (int slot in new[] { 2, 3, 5, 6 })
            if (tx is not null && tx.Textures.Length > slot && tx.Textures[slot] is not null)
                problems.Add($"{model}: {name}: a {new[] { "", "dark", "detail", "gloss", "glow", "bump", "decal" }[slot]} map (not carried)");

        Vector4[]? colors = null;
        if (colorsUsed) colors = d.Colors.ToArray();
        else if (tx?.Base is not null && mat is { } m && Vector3.Distance(m.Diffuse, Vector3.One) > 0.004f)
            colors = Enumerable.Repeat(new Vector4(m.Diffuse, 1), d.Vertices.Length).ToArray();
        var look = new CreatureRig.MeshLook(alpha?.Flags, alpha?.Threshold ?? 0, mat?.Alpha ?? 1, colors, mat?.Emissive ?? Vector3.Zero,
            ZTest: zbuffer is null || (zbuffer.Flags & 1) != 0, ZWrite: zbuffer is null || (zbuffer.Flags & 2) != 0, Clamp: tx?.Base?.Clamp ?? 3);
        return (tex, look, uvSet);
    }

    /// <summary>A shader value Morrowind animates on a shape: the Skyrim lighting shader's variable (12 alpha, 20 U offset,
    /// 21 U scale, 22 V offset, 23 V scale), its value at a moment of the model's own animation time, and as it rests.</summary>
    public sealed record ShaderTrack(int Shape, int Variable, Func<float, float> At, float Rest);

    /// <summary>
    /// The shader values Morrowind's controllers animate on these shapes: the alpha of the material each draws with
    /// (NiAlphaController), and the texture offset and tiling of each (NiUVController: U and V offset, U and V tiling), every
    /// active controller with keys. An inactive one leaves its value as it rests.
    /// </summary>
    public static List<ShaderTrack> ShaderTracks(Nif4 nif, Dictionary<int, int> scene, List<NiTriShape> shapes)
    {
        var result = new List<ShaderTrack>();
        var controllers = nif.Blocks.OfType<NiController>().Where(c => (c.Flags & 8) != 0).ToList();
        foreach (var shape in shapes)
        {
            if (Property<NiMaterialProperty>(nif, scene, shape.Index) is { } mat)
                foreach (var c in controllers.Where(c => c.Type == "NiAlphaController" && c.Target == mat.Index))
                    if (nif.Get<NiKeyData>(c.Data) is { Keys.Count: > 0 } k)
                        result.Add(new ShaderTrack(shape.Index, 12, t => MwKeys.Vector(k.Keys, k.Interpolation, k.Extra, ControllerTime(c, t)).X, mat.Alpha));
            foreach (var c in controllers.Where(c => c.Type == "NiUVController" && c.Target == shape.Index))
                if (nif.Get<NiUVData>(c.Data) is { } uv)
                    for (int g = 0; g < 4; g++)
                        if (uv.Groups[g] is { Keys.Count: > 0 } k)
                            result.Add(new ShaderTrack(shape.Index, new[] { 20, 22, 21, 23 }[g], t => MwKeys.Vector(k.Keys, k.Interpolation, k.Extra, ControllerTime(c, t)).X, g < 2 ? 0 : 1));
        }
        return result;
    }

    /// <summary>A controller's own time at a moment of the animation: scaled by its frequency, moved by its phase, then
    /// within its span as its cycle says (flags bits 1-2: 0 repeat, 1 back and forth, 2 held at its ends).</summary>
    public static float ControllerTime(NiController c, float t)
    {
        float ct = t * c.Frequency + c.Phase, span = c.Stop - c.Start;
        if (span <= 1e-6f) return c.Start;
        switch ((c.Flags >> 1) & 3)
        {
            case 0: return c.Start + ((ct - c.Start) % span + span) % span;
            case 1:
            {
                float p = ((ct - c.Start) % (2 * span) + 2 * span) % (2 * span);
                return c.Start + (p <= span ? p : 2 * span - p);
            }
            default: return Math.Clamp(ct, c.Start, c.Stop);
        }
    }

    /// <summary>Keys without those a straight line through their neighbours already gives (exact at every frame).</summary>
    static (float Time, float Value)[] Thin(List<(float Time, float Value)> keys)
    {
        if (keys.Count <= 2) return keys.ToArray();
        var kept = new List<(float Time, float Value)> { keys[0] };
        for (int i = 1; i < keys.Count - 1; i++)
        {
            var a = kept[^1]; var b = keys[i + 1];
            // Kept when leaving it out would move any frame between the last kept key and the next one.
            bool needed = false;
            for (int j = keys.IndexOf(a) + 1; j <= i && !needed; j++)
            {
                float uj = (keys[j].Time - a.Time) / (b.Time - a.Time);
                needed = MathF.Abs(a.Value + (b.Value - a.Value) * uj - keys[j].Value) > 1e-5f;
            }
            if (needed) kept.Add(keys[i]);
        }
        kept.Add(keys[^1]);
        return kept.ToArray();
    }

    /// <summary>A base texture with its dark map multiplied in (the dark map stretched to the base's size), beside the
    /// converted textures; null when either can't be read.</summary>
    static string? BakeDark(string baseRel, string darkRel, string modOutDir)
    {
        string rel = $@"{MwTextures.OutFolder}\baked\{MwTextures.FlatName(Path.ChangeExtension(baseRel, null)[(MwTextures.OutFolder.Length + 1)..])}__dark__{MwTextures.FlatName(Path.ChangeExtension(darkRel, null)[(MwTextures.OutFolder.Length + 1)..])}.dds";
        string full = Path.Combine(modOutDir, rel);
        if (System.IO.File.Exists(full)) return rel;
        if (Util.DdsImage.Decode(System.IO.File.ReadAllBytes(Path.Combine(modOutDir, baseRel))) is not { } b || Util.DdsImage.Decode(System.IO.File.ReadAllBytes(Path.Combine(modOutDir, darkRel))) is not { } k) return null;
        var px = new uint[b.Px.Length];
        bool alpha = false;
        for (int y = 0; y < b.H; y++)
            for (int x = 0; x < b.W; x++)
            {
                // The dark map's texel under this one (bilinear, wrapping, as it tiles with the base).
                float u = (x + 0.5f) / b.W * k.W - 0.5f, v = (y + 0.5f) / b.H * k.H - 0.5f;
                int x0 = (int)MathF.Floor(u), y0 = (int)MathF.Floor(v);
                float fx = u - x0, fy = v - y0;
                Vector3 At(int xx, int yy) { uint c = k.Px[((yy % k.H + k.H) % k.H) * k.W + (xx % k.W + k.W) % k.W]; return new Vector3(c >> 16 & 255, c >> 8 & 255, c & 255) / 255f; }
                var dk = Vector3.Lerp(Vector3.Lerp(At(x0, y0), At(x0 + 1, y0), fx), Vector3.Lerp(At(x0, y0 + 1), At(x0 + 1, y0 + 1), fx), fy);
                uint c = b.Px[y * b.W + x];
                var col = new Vector3(c >> 16 & 255, c >> 8 & 255, c & 255) / 255f * dk;
                uint a = c >> 24;
                if (a != 255) alpha = true;
                px[y * b.W + x] = a << 24 | (uint)MathF.Round(col.X * 255) << 16 | (uint)MathF.Round(col.Y * 255) << 8 | (uint)MathF.Round(col.Z * 255);
            }
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        MwTextures.WriteBcDds(full, px, b.W, b.H, alpha);
        return rel;
    }

    /// <summary>The properties a block is drawn with, as Morrowind inherits them down the scene: its ancestors' from the root
    /// down, then its own; of each kind the last (nearest) one is the one in effect.</summary>
    public static T? Property<T>(Nif4 nif, Dictionary<int, int> parents, int block) where T : NiFlagProperty =>
        Chain(parents, block).Select(nif.Get<NiNode>).OfType<NiNode>()
            .SelectMany(n => n.Properties.Reverse().Select(nif.Get<NiFlagProperty>).OfType<T>()).FirstOrDefault();

    /// <summary>A block and its scene ancestors, itself first.</summary>
    public static IEnumerable<int> Chain(Dictionary<int, int> parents, int block)
    {
        for (int b = block, guard = 0; guard < 256; guard++)
        {
            yield return b;
            if (!parents.TryGetValue(b, out b)) yield break;
        }
    }

    /// <summary>Whether a block is shown at a moment of an animation from its own .kf (<paramref name="own"/>): its keys
    /// (a step from each to the next, the first held before it) when it has some and the clip is its own, else its flag.</summary>
    public static bool Shown(Nif4 nif, Dictionary<int, List<(float Time, bool Visible)>> keys, int block, bool own, float t)
    {
        if (own && keys.TryGetValue(block, out var k))
        {
            bool v = k[0].Visible;
            foreach (var (time, visible) in k) if (time <= t) v = visible; else break;
            return v;
        }
        return nif.Get<NiNode>(block) is not { Hidden: true };
    }

    /// <summary>The visibility-keyed blocks a block's showing depends on (itself and its ancestors with keys), in order.</summary>
    public static List<int> VisibilityOf(Dictionary<int, int> parents, Dictionary<int, List<(float Time, bool Visible)>> keys, int block) =>
        Chain(parents, block).Where(keys.ContainsKey).ToList();

    /// <summary>Whether a block is never shown: it or an ancestor is hidden with no keys to show it.</summary>
    public static bool NeverShown(Nif4 nif, Dictionary<int, int> parents, Dictionary<int, List<(float Time, bool Visible)>> keys, int block) =>
        Chain(parents, block).Any(b => nif.Get<NiNode>(b) is { Hidden: true } && !keys.ContainsKey(b));

    /// <summary>The shapes a Morrowind model's Skyrim body holds, in order: those it can show (none of it and its ancestors
    /// hidden for good; some shown only while their visibility keys say, as the dremora's armour pieces), then a hidden one
    /// a particle array emits from at its vertices (<see cref="EmitsAtVertex"/>: the first geometry under its emitter),
    /// kept but not drawn.</summary>
    public static List<NiTriShape> BodyShapes(Nif4 nif)
    {
        var parents = SceneParents(nif);
        var keys = VisibilityKeys(nif);
        var emitterShapes = nif.Blocks.OfType<NiParticleSystemController>().Where(EmitsAtVertex).Select(c => FirstGeometry(nif, c.Emitter)).OfType<NiTriShape>().Select(s => s.Index).ToHashSet();
        bool HasData(NiTriShape s) => nif.Get<NiTriShapeData>(s.Data) is { Vertices.Length: > 0 };
        var shapes = nif.Blocks.OfType<NiTriShape>().Where(s => s is not NiParticles && parents.ContainsKey(s.Index) && HasData(s)).ToList();
        var drawn = shapes.Where(s => !NeverShown(nif, parents, keys, s.Index)).ToList();
        return drawn.Concat(shapes.Where(s => !drawn.Contains(s) && emitterShapes.Contains(s.Index))).ToList();
    }

    /// <summary>Whether a particle array emits from its emitter's geometry (controller flag 0x10; OpenMW's reading of
    /// Morrowind's, components/nifosg/particle.cpp), not from its emitter's nodes.</summary>
    public static bool EmitsAtVertex(NiParticleSystemController c) => c.Type == "NiBSPArrayController" && (c.Flags & 0x10) != 0;

    /// <summary>The first geometry under a node, depth first (what a particle array emitting at vertices emits from).</summary>
    public static NiTriShape? FirstGeometry(Nif4 nif, int block)
    {
        if (nif.Get<NiTriShape>(block) is { } s && s is not NiParticles) return s;
        if (nif.Get<NiNode>(block) is { } n) foreach (int ch in n.Children) if (FirstGeometry(nif, ch) is { } f) return f;
        return null;
    }

    /// <summary>A point a particle array emits from: between a node of its emitter's subtree and that node's parent (at
    /// <paramref name="Along"/>: 0 the parent, 1 the node), moved by <paramref name="Offset"/> in the node's own frame.</summary>
    public readonly record struct EmitterPoint(int Node, int Parent, float Along, Vector3 Offset);

    /// <summary>Points along each emitter bone: enough that particles spread along it as Morrowind's do.</summary>
    public const int PointsPerBone = 5;

    /// <summary>
    /// Where a particle array that doesn't emit at vertices emits from, as Morrowind places its particles (OpenMW's reading,
    /// components/nifosg/particle.cpp; `mw-particle-arrays`: all 21 of the creatures' arrays have flag 0x08): each emission
    /// a node of its emitter's subtree (the emitter and every node under it, all alike), with flag 0x08 at a random point
    /// between that node and its parent (here <see cref="PointsPerBone"/> points evenly along it), else at the node; within
    /// a box of its emitter dimensions in the node's frame (here its corners and centre). In order: node by node (the
    /// subtree depth first), point by point, offset by offset. Empty for an array emitting at vertices.
    /// </summary>
    public static List<EmitterPoint> EmitterPoints(Nif4 nif, Dictionary<int, int> scene, NiParticleSystemController c)
    {
        var points = new List<EmitterPoint>();
        if (EmitsAtVertex(c)) return points;
        var nodes = new List<int>();
        void All(int i) { if (nif.Get<NiNode>(i) is { } n && n is not NiTriShape) { nodes.Add(i); foreach (int ch in n.Children) All(ch); } }
        All(c.Emitter);
        bool alongBone = (c.Flags & 0x08) != 0;
        var half = c.EmitterDimensions / 2;
        var offsets = half.LengthSquared() < 1e-8f ? [Vector3.Zero]
            : new[] { Vector3.Zero }.Concat(from x in new[] { -1, 1 } from y in new[] { -1, 1 } from z in new[] { -1, 1 } select new Vector3(x * half.X, y * half.Y, z * half.Z)).ToList();
        foreach (int n in nodes)
        {
            int parent = scene.GetValueOrDefault(n, -1);
            var along = alongBone ? Enumerable.Range(0, PointsPerBone).Select(k => (k + 0.5f) / PointsPerBone) : [1f];
            foreach (float s in along)
                foreach (var o in offsets) points.Add(new EmitterPoint(n, parent, s, o));
        }
        return points;
    }

    /// <summary>Whether the body draws a shape (else it is only there for a particle array to emit from).</summary>
    public static bool Drawn(Nif4 nif, NiTriShape shape) => !NeverShown(nif, SceneParents(nif), VisibilityKeys(nif), shape.Index);
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
