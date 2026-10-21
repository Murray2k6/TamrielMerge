using HKX2;
using TamrielMerge.Morrowind;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// Whether a built Morrowind creature plays every animation group its game plays (user, 2026-10-21: "all bones and
/// animations need fixed across games"), read back from the written files: each group its animations have (its own .kf, a
/// biped's humanoid one) as the clip it became, that clip's file, the character naming it and a clip generator of the
/// behaviour playing it (a group carried nowhere is an issue unless Morrowind's engine never plays it); and the ways
/// Morrowind plays them, in the graph as its numbers say: its idles' odds per set of chances (fIdleChanceMultiplier and
/// each set's AI_W, recomputed), its hits and deaths each picked at random among all it has, the deaths from being knocked
/// down or out and in water, its moving clips' playback bound to the actor's speed over Morrowind's pace for them, the way
/// it moves switched where Morrowind's is, and the events its hit script sends. `creature-groups`.
/// </summary>
public static class CreatureGroups
{
    public sealed record Report(int Groups, int Carried, int Issues, List<string> Lines);

    /// <summary>What Morrowind's groups we do not carry yet are for (each one an issue until carried).</summary>
    static string? NotYet(string group)
    {
        string g = group.ToLowerInvariant();
        if (g.StartsWith("sneak") || g == "idlesneak") return "sneaking";
        if (g.StartsWith("jump")) return "jumping";
        if (g is "idlespell" || g.StartsWith("spellturn") || g.StartsWith("spellcast")) return "spellcasting";
        if (g == "idlestorm") return "walking into a storm";
        if (g == "torch") return "holding a torch";
        if (g == "pickprobe") return "picking a lock";
        if (g == "shield") return "blocking";
        if (System.Text.RegularExpressions.Regex.IsMatch(g, @"^walk(forward|back|left|right)(hh|1h|2c|2w)$")) return "walking with a weapon out (a creature walks and runs at one speed: nothing tells a fight's walk from its run)";
        if (System.Text.RegularExpressions.Regex.IsMatch(g, @"^(idle|turnleft|turnright|runforward|runback|runleft|runright)(hh|1h|2c|2w|crossbow)$")) return "a weapon style it has none of";
        return null;
    }

    public static Report Check(Tes3Data data, MwWorld world, string model, string dataDir, string id)
    {
        var lines = new List<string>();
        int issues = 0, carried = 0;
        void Issue(string s) { issues++; lines.Add("  ISSUE " + s); }
        string mp = model.StartsWith(@"meshes\", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
        var (biped, held) = MwCreatureSource.Kind(world, mp);
        var sources = MwCreatureSource.Sources(data, mp, biped);
        var plan = MwCreatureSource.Plan(sources, held, biped);
        var styles = MwCreatureSource.Styles(sources, held, biped);

        string folder = Path.Combine(dataDir, CreatureRig.MeshFolder(id));
        string pascal = CreatureBehavior.ProjectName(id)[2..^"Project".Length];
        T Load<T>(string path) where T : class
        {
            var root = (hkRootLevelContainer)new PackFileDeserializer().Deserialize(new BinaryReaderEx(File.ReadAllBytes(path)));
            return root.m_namedVariants.Select(v => v?.m_variant).OfType<T>().First();
        }
        var graph = Load<hkbBehaviorGraph>(Path.Combine(folder, "Behaviors", $"TM{pascal}Behavior.hkx"));
        var character = Load<hkbCharacterData>(Path.Combine(folder, "Characters", $"TM{pascal}Character.hkx"));
        var events = graph.m_data!.m_stringData!.m_eventNames;
        var varNames = graph.m_data.m_stringData.m_variableNames;
        string E(int i) => i >= 0 && i < events.Count ? events[i] : $"#{i}";
        string V(int i) => i >= 0 && i < varNames.Count ? varNames[i] : $"#{i}";

        // Every node of the graph with the path of state names down to it.
        var machines = new List<(hkbStateMachine M, string Path)>();
        var clipGens = new List<(hkbClipGenerator G, string Path)>();
        var selectors = new List<hkbManualSelectorGenerator>();
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(hkbGenerator? g, string path)
        {
            if (g is null || !seen.Add(g)) return;
            switch (g)
            {
                case hkbStateMachine sm:
                    machines.Add((sm, path));
                    foreach (var st in sm.m_states) Walk(st.m_generator, $"{path}/{st.m_name}");
                    break;
                case hkbClipGenerator c: clipGens.Add((c, path)); break;
                case hkbModifierGenerator mg: Walk(mg.m_generator, path); break;
                case hkbManualSelectorGenerator ms:
                    selectors.Add(ms);
                    foreach (var child in ms.m_generators) Walk(child, path);
                    break;
                case hkbBlenderGenerator bl: foreach (var ch in bl.m_children) Walk(ch.m_generator, path); break;
            }
        }
        Walk(graph.m_rootGenerator, "");
        string ClipOf(hkbClipGenerator g) => Path.GetFileNameWithoutExtension(g.m_animationName);
        var playedAt = clipGens.GroupBy(c => ClipOf(c.G), StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(x => x.Path).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);
        var named = character.m_stringData!.m_animationNames.Select(n => Path.GetFileNameWithoutExtension(n)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // 1. Every group as the clip it became, written, named and played.
        var groups = sources.SelectMany(s => s.Groups.Where(g => g.Value.Whole).Select(g => g.Key)).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(g => g, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var group in groups)
        {
            var clips = plan.Where(p => p.Group.Equals(group, StringComparison.OrdinalIgnoreCase)).Select(p => p.Clip).Distinct().ToList();
            if (clips.Count == 0)
            {
                if (MwCreatureSource.NeverPlayed.TryGetValue(group, out var why)) { lines.Add($"  {group}: Morrowind never plays it ({why})"); continue; }
                if (biped && System.Text.RegularExpressions.Regex.IsMatch(group, @"^(swim)?attack\d$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                { lines.Add($"  {group}: a biped's attacks are its weapon's (OpenMW: random attacks only for non-bipedal creatures)"); continue; }
                if (!biped && System.Text.RegularExpressions.Regex.IsMatch(group, @"^(swim)?turn(left|right)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                { lines.Add($"  {group}: Morrowind turns only bipeds with their turning animations (OpenMW: \"It seems only bipedal actors use turning animations\")"); continue; }
                if (System.Text.RegularExpressions.Regex.IsMatch(group, @"(throw|thrown)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                { lines.Add($"  {group}: a thrown weapon's (misc items in Skyrim: nothing throws)"); continue; }
                Issue($"{group}: not carried ({NotYet(group) ?? "nothing plays it"})");
                continue;
            }
            var parts = new List<string>();
            foreach (var clip in clips)
            {
                if (!File.Exists(Path.Combine(folder, "animations", clip + ".hkx"))) { Issue($"{group}: {clip}.hkx not written"); continue; }
                if (!named.Contains(clip)) { Issue($"{group}: {clip} not among the character's animations"); continue; }
                if (!playedAt.TryGetValue(clip, out var at))
                {
                    // Why nothing plays it: an idle none of its creatures' chances picks (only a script's PlayGroup would), a
                    // plain run an armed biped only runs with nothing drawn, else a gap.
                    string why = System.Text.RegularExpressions.Regex.IsMatch(clip, @"^idle[2-9]$") ? "no creature's idle chances pick it: only a script's PlayGroup plays it, and PlayGroup isn't converted yet"
                        : styles.Count > 0 && System.Text.RegularExpressions.Regex.IsMatch(clip, @"^run(back|left|right)?$") ? "Morrowind runs this way with nothing drawn; Skyrim gives no sign of running at a creature's one speed outside a fight"
                        : "no clip generator plays it";
                    Issue($"{group}: {clip} written, but {why}");
                    continue;
                }
                parts.Add($"{clip} at {string.Join(", ", at.Take(3))}{(at.Count > 3 ? $" (+{at.Count - 3})" : "")}");
            }
            if (parts.Count == clips.Count) { carried++; lines.Add($"  {group}: {string.Join("; ", parts)}"); }
        }

        // 2. Idles by each set of chances: the odds recomputed from the game's records.
        float multiplier = world.Settings.TryGetValue("fIdleChanceMultiplier", out var gm) && gm.Get("FLTV") is { Data.Length: >= 4 } fv ? BitConverter.ToSingle(fv.Data, 0) : float.NaN;
        if (float.IsNaN(multiplier)) Issue("no fIdleChanceMultiplier in Morrowind's settings");
        var (sets, _) = MwCreatureSource.IdleSets(world, mp);
        var clipNames = plan.Select(p => p.Clip).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (int k = 0; k < sets.Count; k++)
        {
            var odds = MwCreatureSource.IdleOdds(sets[k], multiplier);
            var expected = Enumerable.Range(2, 8).Where(n => odds[n - 1] > 0 && clipNames.Contains($"idle{n}")).ToList();
            var machine = machines.FirstOrDefault(m => m.M.m_name == $"TMIdleSet{k}").M;
            if (expected.Count == 0)
            {
                if (machine is not null) Issue($"idle set {k} [{string.Join(" ", sets[k])}]: picks nothing it has, yet the graph rolls for it");
                else lines.Add($"  idle set {k} [{string.Join(" ", sets[k])}]: no idle it has can be picked; it stands in its idle");
                continue;
            }
            if (machine is null) { Issue($"idle set {k} [{string.Join(" ", sets[k])}]: no TMIdleSet{k} in the graph"); continue; }
            float stand = odds[0] + Enumerable.Range(2, 8).Where(n => !expected.Contains(n)).Sum(n => odds[n - 1]);
            var bad = new List<string>();
            var st0 = machine.m_states.FirstOrDefault(s => s.m_stateId == 0);
            if (st0 is null || MathF.Abs(st0.m_probability - stand) > 1e-6f) bad.Add($"standing {st0?.m_probability} for {stand:F6}");
            foreach (int n in expected)
                if (machine.m_states.FirstOrDefault(s => s.m_stateId == n) is not { } st || MathF.Abs(st.m_probability - odds[n - 1]) > 1e-6f)
                    bad.Add($"idle{n} {machine.m_states.FirstOrDefault(s => s.m_stateId == n)?.m_probability} for {odds[n - 1]:F6}");
            if (machine.m_states.Count != expected.Count + 1) bad.Add($"{machine.m_states.Count} states for {expected.Count + 1}");
            if (E(machine.m_randomTransitionEventId) != $"TMIdlePick{k}") bad.Add($"random on {E(machine.m_randomTransitionEventId)}");
            int wild = machine.m_wildcardTransitions?.m_transitions.Count ?? 0;
            if (wild != expected.Count + 1) bad.Add($"{wild} wildcards");
            // Rolls while standing: one every 1/30 s of its idle; each idle once through, then a roll.
            var standGen = clipGens.FirstOrDefault(c => c.G.m_name == $"Idle_{k}").G;
            int rolls = standGen?.m_triggers?.m_triggers.Count(t => E(t.m_event.m_id) == $"TMIdlePick{k}") ?? 0;
            if (rolls == 0) bad.Add("no rolls while standing");
            foreach (int n in expected)
                if (clipGens.FirstOrDefault(c => c.G.m_name == $"Idle{n}_{k}").G is not { } ig || ig.m_triggers?.m_triggers.Any(t => t.m_relativeToEndOfClip && E(t.m_event.m_id) == $"TMIdlePick{k}") != true)
                    bad.Add($"idle{n} rolls at no end");
            if (bad.Count > 0) Issue($"idle set {k} [{string.Join(" ", sets[k])}]: {string.Join("; ", bad)}");
            else lines.Add($"  idle set {k} [{string.Join(" ", sets[k])}]: stands {stand:P2}, " + string.Join(", ", expected.Select(n => $"idle{n} {odds[n - 1]:P2}")) + $" a roll; {rolls} rolls a pass of its idle");
        }
        if (sets.Count > 1)
        {
            if (!selectors.Any(s => s.m_name == "TMIdleSets" && s.m_variableBindingSet?.m_bindings.Any(b => V(b.m_variableIndex) == "iTMIdleSet") == true))
                Issue($"{sets.Count} idle sets but no selector on iTMIdleSet");
        }

        // 3. Hits and deaths: one at random of all it has, as Morrowind counts them.
        void Family(string prefix, string machineName)
        {
            var family = new List<string>();
            for (int i = 1; clipNames.Contains(prefix + i); i++) family.Add(prefix + i);
            if (family.Count <= 1) return;
            var pickers = machines.Where(m => m.M.m_name == machineName).Select(m => m.M).ToList();
            if (pickers.Count == 0) { Issue($"{family.Count} {prefix}s, but no {machineName} picking among them"); return; }
            foreach (var p in pickers)
            {
                var playing = p.m_states.Select(s => s.m_generator is hkbClipGenerator c ? ClipOf(c) : "?").ToList();
                if (p.m_startStateMode != 2 || !playing.ToHashSet().SetEquals(family) || p.m_states.Any(s => s.m_probability != 1))
                    Issue($"{machineName}: start mode {p.m_startStateMode}, plays {string.Join(",", playing)}, for {string.Join(",", family)} each as likely");
            }
            lines.Add($"  {prefix}: one of {string.Join(", ", family)} at random ({pickers.Count} pickers)");
        }
        Family("hit", "TMHitPick"); Family("hit", "TMStaggerPick"); Family("death", "TMDeathPick"); Family("swimhit", "TMSwimHitPick");

        // 4. Dying from where it is.
        var root = graph.m_rootGenerator as hkbStateMachine;
        foreach (var (clip, needs) in new[] { ("deathknockdown", "bTMKnockedDownNow == 1"), ("deathknockout", "bTMKnockedOutNow == 1"), ("swimdeath", "bTMSwimming == 1"),
            ("swimdeathknockdown", "bTMKnockedDownNow == 1"), ("swimdeathknockout", "bTMKnockedOutNow == 1") })
        {
            if (!clipNames.Contains(clip)) continue;
            var state = root?.m_states.FirstOrDefault(s => s.m_generator is hkbClipGenerator c && ClipOf(c) == clip);
            var ways = root?.m_wildcardTransitions?.m_transitions.Where(t => state is not null && t.m_toStateId == state.m_stateId).ToList() ?? [];
            var evs = ways.Select(t => E(t.m_eventId)).ToHashSet();
            bool conditioned = ways.All(t => t.m_condition is hkbExpressionCondition ec && ec.m_expression.Contains(needs));
            if (state is null || !evs.SetEquals(["DeathAnimation", "Ragdoll", "RagdollInstant"]) || !conditioned)
                Issue($"{clip}: {(state is null ? "no death state" : $"entered on {string.Join(",", evs)}{(conditioned ? "" : " without its condition")}")}");
            else lines.Add($"  {clip}: dying when {needs}");
        }

        // 5. Moving: each moving clip's playback is the actor's speed over Morrowind's pace (recomputed here).
        var evaluate = new List<string>();
        void Expressions(object? o, HashSet<object> visited)
        {
            if (o is null || o is string || o.GetType().IsPrimitive || o.GetType().IsEnum || !visited.Add(o)) return;
            if (o is System.Collections.IEnumerable list) { foreach (var it in list) Expressions(it, visited); return; }
            if (o is hkbExpressionData ed) evaluate.Add(ed.m_expression);
            if (o.GetType().IsValueType) return;
            foreach (var p in o.GetType().GetProperties().Where(p => p.Name.StartsWith("m_") && p.GetIndexParameters().Length == 0)) { try { Expressions(p.GetValue(o), visited); } catch { } }
        }
        Expressions(graph.m_rootGenerator, new HashSet<object>(ReferenceEqualityComparer.Instance));
        bool flies = MwCreatureSource.CreaturesWith(world, mp).Any(c => ((c.Get("FLAG")?.Int() ?? 0) & 0x20) != 0);
        foreach (var p in plan.Where(p => MwCreatureSource.Motion(p.Clip, 1, 1, 1).Speed > 0))
        {
            float pace = flies ? MwCreatureSource.Velocity(sources, p.Group) : MwCreatureSource.Velocity(sources, MwCreatureSource.WalkEquivalent(p.Clip));
            if (flies && pace <= 1) pace = p.Clip.Contains("run") ? 222.857f : 154.064f;
            string rate = "fTMRate_" + new string(p.Clip.Where(char.IsLetterOrDigit).ToArray());
            var gens = clipGens.Where(c => ClipOf(c.G) == p.Clip).ToList();
            var bound = gens.Where(c => c.G.m_variableBindingSet?.m_bindings.Any(b => b.m_memberPath == "playbackSpeed" && V(b.m_variableIndex) == rate) == true).ToList();
            string? expr = evaluate.FirstOrDefault(e => e.StartsWith(rate + " ="));
            if (pace <= 1)
            {
                if (bound.Count > 0 || expr is not null) Issue($"{p.Clip}: Morrowind plays it as it is (its walk's pace {pace:F3}), yet its speed is bound");
                else if (gens.Count > 0) lines.Add($"  {p.Clip}: played as it is (no walking pace: {pace:F3})");
                continue;
            }
            if (gens.Count == 0) continue;   // reported above
            string want = $"{rate} = clamp(Speed / {pace.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}, 0, 10)";
            if (bound.Count != gens.Count || expr != want) Issue($"{p.Clip}: {bound.Count} of {gens.Count} generators bound; expression {expr ?? "none"}, Morrowind's pace {pace:F3}");
            else lines.Add($"  {p.Clip}: at the actor's speed over {pace:F2} (Morrowind's {(flies ? p.Group : MwCreatureSource.WalkEquivalent(p.Clip))} pace)");
        }
        if (selectors.Any(s => s.m_variableBindingSet?.m_bindings.Any(b => V(b.m_variableIndex) == "iTMWay") == true))
        {
            const string way = "iTMWay = cond(Direction < 0.176208, 0, cond(Direction < 0.323792, 1, cond(Direction < 0.676208, 2, cond(Direction < 0.823792, 3, 0))))";
            // atan 2 = 63.4349°: sideways when sideways movement is more than twice the forward (OpenMW's mIsStrafing).
            float edge = MathF.Atan(2) * 180 / MathF.PI / 360;
            if (!evaluate.Contains(way) || MathF.Abs(edge - 0.176208f) > 1e-5f) Issue("the way it moves is not switched as Morrowind's");
            else lines.Add($"  ways: forward under {edge:F6} of a turn either side, sideways to {0.5f - edge:F6}, back beyond (Morrowind's strafe switch)");
        }

        // 6. What its hit script sends and sets, all in the graph.
        foreach (var e in new[] { "TMHit", "TMKnockDown", "TMKnockOut" })
            if (!events.Contains(e)) Issue($"its hit script sends {e}, the graph has no such event");
        foreach (var v in new[] { "iTMIdleSet", "bTMKnockedOut", "bTMInCombat" })
            if (!varNames.Contains(v)) Issue($"its hit script sets {v}, the graph has no such variable");
        var baseMachine = machines.FirstOrDefault(m => m.M.m_name == "TMBase").M;
        foreach (var (e, state) in new[] { ("TMHit", "Hit"), ("TMKnockDown", "KnockDown"), ("TMKnockOut", "KnockOut") })
        {
            var target = baseMachine?.m_states.FirstOrDefault(s => s.m_name == state);
            if (target is null || baseMachine!.m_wildcardTransitions?.m_transitions.Any(t => E(t.m_eventId) == e && t.m_toStateId == target.m_stateId) != true)
                Issue($"{e} leads to no {state} state");
        }
        return new Report(groups.Count, carried, issues, lines);
    }
}
