using System.Numerics;
using System.Text;
using HKX2;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// A creature's own behaviour, written from nothing but its own clips (user, 2026-10-21: "ice wraith and cliffracer arent
/// the same", "same goes for daggerfalls imp"): no vanilla creature's graph, animations, events or sounds borrowed. The
/// graph answers what Skyrim's engine sends every creature (every one of the 53 vanilla creature graphs has these:
/// `hkx-graph --contract`) and sets the variables it reads, laid out as vanilla graphs are (`hkx-graph` on the wisp's):
/// <list type="bullet">
/// <item>at rest: standing, picking its idles by its own chances as Morrowind's wander package does (<see cref="Standing"/>),
/// walking any way on moveStart until moveStop (<see cref="Ways"/>), a biped turning on turnLeft / turnRight;</item>
/// <item>weapEquip draws (weaponDraw on leaving), weapUnequip sheathes (weaponSheathe on entering);</item>
/// <item>in combat: its idle with IsAttackReady, running any way while moving, turning, and each of its attacks on that
/// attack's attackStart_ event (the race names them) with IsAttacking, ending in attackStop;</item>
/// <item>struck: its hit script's TMHit plays one of its hits at random, TMKnockDown its knockdown, TMKnockOut its knock-out
/// until it comes to; staggerStart and recoilStart one of its hits too;</item>
/// <item>in water (swimStart until swimStop) all of it with its swimming animations, a dry one where it has none;</item>
/// <item>DeathAnimation, Ragdoll and RagdollInstant from anywhere play its death once and hold its last frame (no ragdoll:
/// it stays where it fell), the one Morrowind would pick from where it is; GetUpStart brings it back;</item>
/// <item>look-at on its head bone when it has one.</item>
/// </list>
/// Nothing blends: Morrowind switches animations outright. Moving clips play at the actor's speed over Morrowind's pace
/// for them. Its animation data is its own too: every clip generator's events (its own sounds, hit moments, the ends
/// above), motion for its moving clips at their speeds and ways (Y forward, as vanilla's), and attack sets naming its
/// attacks. `creature-groups` checks it against the game.
/// </summary>
public sealed class CreatureGraph
{
    readonly CreatureRig rig;
    readonly CreatureBehavior archive;
    readonly string id;
    readonly List<string> events = [];
    readonly List<(string Name, sbyte Type, int Value)> variables = [];

    /// <summary>The attacks: Skyrim's event (the race's attack data names it) and the clip it plays.</summary>
    public List<(string Event, string Clip)> Attacks { get; } = [];

    /// <summary>Clip generators in the graph: name, animation clip, and the event the graph fires at its end (or null).</summary>
    readonly List<(string Name, string Clip, string? EndEvent)> generators = [];

    /// <param name="archive">The game's archives (the animation data indexes extend the game's own).</param>
    public CreatureGraph(CreatureRig rig, string id, CreatureBehavior archive)
    {
        this.rig = rig;
        this.id = id;
        this.archive = archive;
        Attacks.AddRange(AttackList(rig.Clips.Keys));
    }

    static readonly string[] MeleeKinds = ["chop", "slash", "thrust"];

    /// <summary>The attacks a creature with these clips has: each "attack…" clip (in order) on attackStart_Attack1, 2...; for one
    /// that fights with weapons ("weapon_&lt;style&gt;_&lt;kind&gt;" clips), each kind of blow any of its styles has (chop, slash,
    /// thrust), its clip then "weapon:&lt;kind&gt;" (the graph picks the style by the weapon in hand).</summary>
    public static List<(string Event, string Clip)> AttackList(IEnumerable<string> clips)
    {
        var list = clips.Select(c => c.ToLowerInvariant()).ToList();
        if (list.Any(c => c.StartsWith("weapon_")))
            return MeleeKinds.Where(k => list.Any(c => c.StartsWith("weapon_") && c.EndsWith("_" + k))).Select((k, i) => ($"attackStart_Attack{i + 1}", "weapon:" + k)).ToList();
        return list.Where(c => c.StartsWith("attack")).OrderBy(c => c, StringComparer.Ordinal).Select((c, i) => ($"attackStart_Attack{i + 1}", c)).ToList();
    }

    public static IEnumerable<string> AttackEvents(IEnumerable<string> clips) => AttackList(clips).Select(a => a.Event);

    static string Pascal(string id) => CreatureBehavior.ProjectName(id)[2..^"Project".Length];
    public static string ProjectName(string id) => CreatureBehavior.ProjectName(id);
    static string AnimFile(string clip) => $@"Animations\{clip}.hkx";

    int Ev(string name)
    {
        int i = events.FindIndex(e => e.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0) return i;
        events.Add(name);
        return events.Count - 1;
    }

    /// <summary>A graph variable (types: 0 bool, 3 int32, 4 real).</summary>
    int Var(string name, sbyte type = 0, int value = 0)
    {
        int i = variables.FindIndex(v => v.Name == name);
        if (i >= 0) return i;
        variables.Add((name, type, value));
        return variables.Count - 1;
    }

    static int Real(float f) => BitConverter.SingleToInt32Bits(f);

    string Clip(string preferred, params string[] fallbacks) =>
        new[] { preferred }.Concat(fallbacks).FirstOrDefault(rig.Clips.ContainsKey) ?? (rig.Clips.ContainsKey("idle") ? "idle" : rig.Clips.Keys.First());

    // From one animation to the next at once: Morrowind's engine blends none (as vanilla's DefaultBlendTransition, 0 s).
    readonly hkbBlendingTransitionEffect cut = new() { m_name = "TMCut", m_duration = 0, m_selfTransitionMode = 0, m_eventMode = 0 };
    // The same, starting a state's animation over when it goes to itself.
    readonly hkbBlendingTransitionEffect restart = new() { m_name = "TMRestart", m_duration = 0, m_selfTransitionMode = 2, m_eventMode = 0 };

    /// <summary>A transition on <paramref name="evt"/> to <paramref name="state"/>: only while <paramref name="when"/> holds
    /// (a Havok expression, as vanilla's "IsNPC == 1"); <paramref name="local"/> a random machine's wildcard (vanilla's
    /// flags 2304), <paramref name="self"/> one that may go to the state it is in, starting it over (2816).</summary>
    hkbStateMachineTransitionInfo To(string evt, int state, string? when = null, bool local = false, bool self = false) => new()
    {
        m_triggerInterval = new hkbStateMachineTimeInterval { m_enterEventId = -1, m_exitEventId = -1 },
        m_initiateInterval = new hkbStateMachineTimeInterval { m_enterEventId = -1, m_exitEventId = -1 },
        m_transition = self ? restart : cut,
        m_condition = when is null ? null : new hkbExpressionCondition { m_expression = when },
        m_eventId = Ev(evt),
        m_toStateId = state,
        m_flags = (short)((when is null ? 256 : 0) | (self ? 512 : 0) | (local ? 2048 : 0)),
    };

    /// <summary>The variable a moving clip's playback speed is bound to (<see cref="CreatureRig.MovePaces"/>).</summary>
    static string RateVar(string clip) => "fTMRate_" + new string(clip.Where(char.IsLetterOrDigit).ToArray());

    /// <param name="triggers">More events it sends: at a time into it, or that long before its end.</param>
    hkbClipGenerator ClipGen(string name, string clip, bool loop, string? endEvent = null, IEnumerable<(float Time, string Event, bool FromEnd)>? triggers = null)
    {
        generators.Add((name, clip, endEvent));
        var g = new hkbClipGenerator
        {
            m_name = name,
            m_animationName = AnimFile(clip),
            m_playbackSpeed = 1,
            m_animationBindingIndex = -1,
            m_mode = (sbyte)(loop ? 1 : 0),
        };
        var list = new List<hkbClipTrigger>();
        if (endEvent is not null)
            list.Add(new hkbClipTrigger { m_localTime = Math.Max(0, rig.Duration(clip) - 0.02f), m_event = new hkbEventProperty { m_id = Ev(endEvent) } });
        foreach (var (time, evt, fromEnd) in triggers ?? [])
            list.Add(new hkbClipTrigger { m_localTime = fromEnd ? -time : time, m_relativeToEndOfClip = fromEnd, m_event = new hkbEventProperty { m_id = Ev(evt) } });
        if (list.Count > 0) g.m_triggers = new hkbClipTriggerArray { m_triggers = list };
        // Moving at the actor's own speed over Morrowind's pace for it (the root's expressions keep the rate).
        if (rig.MovePaces.ContainsKey(clip))
            g.m_variableBindingSet = new hkbVariableBindingSet
            {
                m_indexOfBindingToEnable = -1,
                m_bindings = [new hkbVariableBindingSetBinding { m_memberPath = "playbackSpeed", m_variableIndex = Var(RateVar(clip), 4, Real(1)), m_bitIndex = -1 }],
            };
        return g;
    }

    /// <summary>The clips of a family as Morrowind counts them ("hit1", "hit2"... up from 1 while there is one: OpenMW's
    /// chooseRandomGroup), else the one bare name a rig from another game has ("hit").</summary>
    List<string> Family(string prefix)
    {
        var list = new List<string>();
        for (int i = 1; rig.Clips.ContainsKey(prefix + i); i++) list.Add(prefix + i);
        if (list.Count == 0 && rig.Clips.ContainsKey(prefix)) list.Add(prefix);
        return list;
    }

    /// <summary>One of <paramref name="clips"/> at random each time it starts, all as likely (as Morrowind picks a hit or
    /// a death): a state machine starting in a random state, as vanilla's DeathBehavior (start mode 2).</summary>
    hkbGenerator OneOf(string name, IReadOnlyList<string> clips, bool loop, string? endEvent)
    {
        if (clips.Count == 1) return ClipGen(name, clips[0], loop, endEvent);
        var m = Machine($"TM{name}Pick", 0, clips.Select((c, i) => State(i, $"{name}_{c}", ClipGen($"{name}_{c}", c, loop, endEvent))).ToList());
        m.m_startStateMode = 2;
        return m;
    }

    /// <summary>
    /// Moving any way: forward, right, back or left by the engine's Direction (0 forward, .25 right, .5 back, .75 left, as
    /// vanilla's directional blends), switched outright where Morrowind switches (OpenMW's update: sideways once the
    /// sideways movement is more than twice the forward, atan 2 = 63.43° off straight; backwards past 116.57°), never
    /// blended. A way the creature has no animation for plays <paramref name="none"/> (Morrowind plays no movement group
    /// then: its idle stays).
    /// </summary>
    hkbGenerator Ways(string name, Func<string, hkbGenerator?> way, Func<hkbGenerator> none)
    {
        var forward = way("forward") ?? none();
        var right = way("right"); var back = way("back"); var left = way("left");
        if (right is null && back is null && left is null) return forward;
        Var("iTMWay", 3);
        hkbGenerator? still = null;
        hkbGenerator Still() => still ??= none();
        return new hkbManualSelectorGenerator
        {
            m_name = name,
            m_generators = [forward, right ?? Still(), back ?? Still(), left ?? Still()],
            m_variableBindingSet = new hkbVariableBindingSet
            {
                m_indexOfBindingToEnable = -1,
                m_bindings = [new hkbVariableBindingSetBinding { m_memberPath = "selectedGeneratorIndex", m_variableIndex = Var("iTMWay", 3), m_bitIndex = -1 }],
            },
        };
    }

    /// <summary>Its standing idle and the idles its wander package picks (<see cref="CreatureRig.IdleSets"/>): per set of
    /// chances a state machine standing in its idle and rolling every 1/30 s (Morrowind rolls each frame while the actor
    /// stands; OpenMW's AiWander), each roll going to Idle2..9 by that set's odds or staying, an idle once through and
    /// then rolling again; the set the actor's iTMIdleSet names (a manual selector when the model's creatures differ).</summary>
    hkbGenerator Standing(string idle)
    {
        var sets = rig.IdleSets.Select((odds, k) => IdleSet(k, odds, idle)).ToList();
        if (sets.Count == 0) return ClipGen("Idle", idle, loop: true);
        if (sets.Count == 1) return sets[0];
        return new hkbManualSelectorGenerator
        {
            m_name = "TMIdleSets",
            m_generators = sets,
            m_variableBindingSet = new hkbVariableBindingSet
            {
                m_indexOfBindingToEnable = -1,
                m_bindings = [new hkbVariableBindingSetBinding { m_memberPath = "selectedGeneratorIndex", m_variableIndex = Var("iTMIdleSet", 3), m_bitIndex = -1 }],
            },
        };
    }

    hkbGenerator IdleSet(int k, float[] odds, string idle)
    {
        string pick = $"TMIdlePick{k}", any = $"TMIdleAny{k}";
        // A pick of an idle it has no animation for leaves it standing (Morrowind: playIdle fails, it rolls again).
        float stand = odds[0];
        var picks = new List<(int N, float P)>();
        for (int n = 2; n <= 9; n++)
            if (odds[n - 1] > 0)
            {
                if (rig.Clips.ContainsKey($"idle{n}")) picks.Add((n, odds[n - 1]));
                else stand += odds[n - 1];
            }
        if (picks.Count == 0) return ClipGen($"Idle_{k}", idle, loop: true);
        int rolls = Math.Max(1, (int)MathF.Round(rig.Duration(idle) * 30));
        var states = new List<hkbStateMachineStateInfo>
        {
            State(0, "Stand", ClipGen($"Idle_{k}", idle, loop: true, triggers: Enumerable.Range(0, rolls).Select(f => (f / 30f, pick, false)))),
        };
        states[0].m_probability = stand;
        foreach (var (n, p) in picks)
        {
            var s = State(n, $"Idle{n}", ClipGen($"Idle{n}_{k}", $"idle{n}", loop: false, triggers: [(0f, pick, true)]));
            s.m_probability = p;
            states.Add(s);
        }
        var wild = new List<hkbStateMachineTransitionInfo> { To(any, 0, local: true) };
        wild.AddRange(picks.Select(p => To(any, p.N, local: true, self: true)));
        var m = Machine($"TMIdleSet{k}", 0, states, wild);
        m.m_randomTransitionEventId = Ev(pick);
        return m;
    }

    /// <summary>
    /// A clip picked by the weapon in its right hand, as Skyrim's armed creatures pick theirs (a manual selector on
    /// iRightHandType: 0 hand to hand, 1 sword, 2 dagger, 3 war axe, 4 mace, 5 greatsword, 6 battleaxe or warhammer, 7 bow,
    /// as the draugr's graph has them; 8 staff; 9 and up the rest): that type's style among the creature's, else the
    /// nearest it has, else <paramref name="fallback"/>.
    /// </summary>
    hkbGenerator ByWeapon(string name, Func<string, string> clipFor, string fallback, bool loop, string? endEvent)
    {
        string Pick(int type)
        {
            string[] wanted = type switch
            {
                0 => ["hh"], 1 or 2 or 3 or 4 or 10 => ["1h"], 5 => ["2c"], 6 => ["2c", "2w"], 7 => ["bow", "xbow"], 8 => ["2w"], 9 or 11 or 12 => ["xbow", "bow"], _ => ["hh"],
            };
            foreach (var s in wanted.Concat(["1h", "2c", "2w", "hh", "bow", "xbow"]))
                if (rig.WeaponStyles.Contains(s) && rig.Clips.ContainsKey(clipFor(s))) return clipFor(s);
            return fallback;
        }
        var made = new Dictionary<string, hkbGenerator>();
        var children = new List<hkbGenerator>();
        for (int type = 0; type <= 12; type++)
        {
            string clip = Pick(type);
            if (!made.TryGetValue(clip, out var g)) made[clip] = g = ClipGen($"{name}_{clip}", clip, loop, endEvent);
            children.Add(g);
        }
        return new hkbManualSelectorGenerator
        {
            m_name = name,
            m_generators = children,
            m_variableBindingSet = new hkbVariableBindingSet
            {
                m_indexOfBindingToEnable = -1,
                m_bindings = [new hkbVariableBindingSetBinding { m_memberPath = "selectedGeneratorIndex", m_variableIndex = Var("iRightHandType", 3), m_bitIndex = -1 }],
            },
        };
    }

    /// <summary>A generator with the named bool variables true while it plays (BSIsActiveModifier, as vanilla's).</summary>
    hkbGenerator While(hkbGenerator g, string name, params string[] flags)
    {
        if (flags.Length == 0) return g;
        var mod = new BSIsActiveModifier { m_name = name + "_IsActive", m_enable = true, m_variableBindingSet = new hkbVariableBindingSet { m_indexOfBindingToEnable = -1 } };
        mod.m_variableBindingSet.m_bindings = flags.Select((f, i) => new hkbVariableBindingSetBinding { m_memberPath = $"bIsActive{i}", m_variableIndex = Var(f), m_bitIndex = -1 }).ToList();
        return new hkbModifierGenerator { m_name = name + "_MG", m_modifier = mod, m_generator = g };
    }

    static hkbStateMachineStateInfo State(int stateId, string name, hkbGenerator g, IEnumerable<hkbStateMachineTransitionInfo>? transitions = null,
        IEnumerable<int>? enterEvents = null, IEnumerable<int>? exitEvents = null)
    {
        var s = new hkbStateMachineStateInfo { m_stateId = stateId, m_name = name, m_generator = g, m_probability = 1, m_enable = true };
        if (transitions?.ToList() is { Count: > 0 } t) s.m_transitions = new hkbStateMachineTransitionInfoArray { m_transitions = t };
        if (enterEvents?.ToList() is { Count: > 0 } en) s.m_enterNotifyEvents = new hkbStateMachineEventPropertyArray { m_events = en.Select(e => new hkbEventProperty { m_id = e }).ToList() };
        if (exitEvents?.ToList() is { Count: > 0 } ex) s.m_exitNotifyEvents = new hkbStateMachineEventPropertyArray { m_events = ex.Select(e => new hkbEventProperty { m_id = e }).ToList() };
        return s;
    }

    static hkbStateMachine Machine(string name, int start, List<hkbStateMachineStateInfo> states, List<hkbStateMachineTransitionInfo>? wildcards = null) => new()
    {
        m_name = name,
        m_eventToSendWhenStateOrTransitionChanges = new hkbEvent { m_id = -1 },
        m_startStateId = start,
        m_returnToPreviousStateEventId = -1,
        m_randomTransitionEventId = -1,
        m_transitionToNextHigherStateEventId = -1,
        m_transitionToNextLowerStateEventId = -1,
        m_syncVariableIndex = -1,
        m_maxSimultaneousTransitions = 32,
        m_states = states,
        m_wildcardTransitions = wildcards is { Count: > 0 } ? new hkbStateMachineTransitionInfoArray { m_transitions = wildcards } : null,
    };

    hkbBehaviorGraph Graph(int headBone)
    {
        // The variables the engine reads and writes (every vanilla creature graph has them).
        foreach (var b in new[] { "bAnimationDriven", "IsAttackReady", "bEquipOK", "bAllowRotation", "IsRecoiling", "IsStaggering", "IsAttacking", "bHeadTrackingOn", "bHeadTracking" })
            Var(b);
        Var("iSyncIdleLocomotion", 3); Var("iState", 3); Var("iSyncTurnState", 3, 1); Var("iRightHandType", 3);
        Var("Direction", 4); Var("Speed", 4); Var("TurnDelta", 4); Var("staggerMagnitude", 4, Real(1));
        // The events the engine sends and listens for (the same).
        foreach (var e in new[] { "moveStart", "moveStop", "preHitFrame", "HitFrame", "weaponDraw", "weaponSheathe", "weapEquip", "weapUnequip", "weapEquipOut", "weapUnequipOut",
            "attackStop", "weaponSwing", "recoilStart", "recoilStop", "recoilLargeStart", "staggerStart", "staggerStop", "staggerForwardStart", "turnLeft", "turnRight", "turnStop",
            "GetUpStart", "GetUpEnd", "GetUpBegin", "Getup", "Ragdoll", "RagdollInstant", "DeathAnimation", "AddCharacterControllerToWorld", "RemoveCharacterControllerFromWorld",
            "Reanimated", "AddRagdollToWorld", "IdleStop", "SoundPlay", "returnToDefault", "PickNewIdle",
            "bowAttackStart", "bowDrawn", "attackRelease", "BowRelease", "bowReset", "arrowAttach", "arrowRelease" })
            Ev(e);
        // Each clip's look: its event, sent as it starts (the body's own graph plays that sequence).
        foreach (var clip in rig.Looks.Keys) Ev(CreatureRig.LookEvent(clip));

        // Variables of its own: the idle set (per actor), knocked out and in combat (its hit script sets them), and where it is.
        foreach (var b in new[] { "bTMKnockedOut", "bTMInCombat", "bTMSwimming", "bTMKnockedDownNow", "bTMKnockedOutNow" }) Var(b);
        Var("iTMIdleSet", 3); Var("iTMRun", 3);

        string idle = Clip("idle"), walk = Clip("walk", "run"), run = Clip("run", "walk");
        var hits = Family("hit"); if (hits.Count == 0) hits.Add(idle);
        var deaths = Family("death"); if (deaths.Count == 0) deaths.Add(idle);
        bool turns = rig.Biped && (rig.Clips.ContainsKey("turnleft") || rig.Clips.ContainsKey("turnright"));
        hkbGenerator? Plain(string name, string? clip) => clip is not null && rig.Clips.ContainsKey(clip) ? ClipGen(name, clip, loop: true) : null;
        // A moving clip, else Morrowind's fallbacks for it: a run's walk ("run" for "walk" when there is no run group).
        string? Moving(string clip) => rig.Clips.ContainsKey(clip) ? clip : clip.Contains("run") && rig.Clips.ContainsKey(clip.Replace("run", "walk")) ? clip.Replace("run", "walk") : null;
        string Suffix(string w) => w == "forward" ? "" : w;

        // At rest: standing (and its idles), walking any way, a biped turning on the spot.
        var nonCombatStates = new List<hkbStateMachineStateInfo>
        {
            State(0, "Idle", Standing(idle), [To("moveStart", 1), .. turns ? new[] { To("turnLeft", 2), To("turnRight", 3) } : []]),
            State(1, "Walk", Ways("Walk", w => Plain($"Walk_{w}", w == "forward" ? walk : Moving("walk" + w)), () => ClipGen("Walk_still", idle, loop: true)), [To("moveStop", 0)]),
        };
        if (turns)
        {
            nonCombatStates.Add(State(2, "TurnLeft", ClipGen("TurnLeft", Clip("turnleft", "turnright"), loop: true), [To("turnStop", 0), To("turnRight", 3), To("moveStart", 1)]));
            nonCombatStates.Add(State(3, "TurnRight", ClipGen("TurnRight", Clip("turnright", "turnleft"), loop: true), [To("turnStop", 0), To("turnLeft", 2), To("moveStart", 1)]));
        }
        var nonCombat = Machine("TMNonCombat", 0, nonCombatStates);

        // In combat: each attack is its own state, entered from idle or moving on its event. A creature with weapons plays its
        // held weapon's style (its idle, run, blows, drawing and putting away) by the engine's iRightHandType, as Skyrim's own
        // armed creatures do; a bow's shot is the engine's draw / hold / release (bowAttackStart, bowDrawn, attackRelease).
        bool styled = rig.WeaponStyles.Count > 0;
        string? bowStyle = rig.WeaponStyles.FirstOrDefault(s => s is "bow" or "xbow" && rig.Clips.ContainsKey($"weapon_{s}_draw") && rig.Clips.ContainsKey($"weapon_{s}_release"));
        var combatStates = new List<hkbStateMachineStateInfo>();
        var attackTransitions = Attacks.Select((a, i) => (a.Event, State: 2 + i)).ToList();
        if (bowStyle is not null) attackTransitions.Add(("bowAttackStart", 10));
        hkbGenerator Gen(string name, string clip, bool loop, string? end = null, Func<string, string>? styleClip = null) =>
            styled && styleClip is not null ? ByWeapon(name, styleClip, clip, loop, end) : ClipGen(name, clip, loop, end);
        var combatTurns = turns ? new[] { To("turnLeft", 20), To("turnRight", 21) } : [];
        combatStates.Add(State(0, "CombatIdle", While(Gen("CombatIdle", idle, true, null, s => $"cidle_{s}"), "CombatIdle", "IsAttackReady"),
            [To("moveStart", 1), .. combatTurns, .. attackTransitions.Select(a => To(a.Event, a.State))]));
        // Running any way with what it holds (its style's RunBack1h..., else the plain group, else a walk, else standing).
        hkbGenerator? CombatWay(string w)
        {
            string plain = w == "forward" ? run : Moving("run" + w) ?? "";
            if (styled) return rig.Clips.ContainsKey(plain) || rig.WeaponStyles.Any(s => rig.Clips.ContainsKey(w == "forward" ? $"crun_{s}" : $"crun{w}_{s}"))
                ? ByWeapon($"CombatRun_{w}", s => w == "forward" ? $"crun_{s}" : $"crun{w}_{s}", rig.Clips.ContainsKey(plain) ? plain : idle, loop: true, endEvent: null) : null;
            return Plain($"CombatRun_{w}", plain);
        }
        combatStates.Add(State(1, "CombatRun", While(Ways("CombatRun", CombatWay, () => Gen("CombatRun_still", idle, true, null, s => $"cidle_{s}")), "CombatRun", "IsAttackReady"),
            [To("moveStop", 0), .. attackTransitions.Select(a => To(a.Event, a.State))]));
        if (turns)
        {
            combatStates.Add(State(20, "CombatTurnLeft", While(Gen("CombatTurnLeft", Clip("turnleft", "turnright"), true, null, s => $"cturnleft_{s}"), "CombatTurnLeft", "IsAttackReady"),
                [To("turnStop", 0), To("turnRight", 21), To("moveStart", 1), .. attackTransitions.Select(a => To(a.Event, a.State))]));
            combatStates.Add(State(21, "CombatTurnRight", While(Gen("CombatTurnRight", Clip("turnright", "turnleft"), true, null, s => $"cturnright_{s}"), "CombatTurnRight", "IsAttackReady"),
                [To("turnStop", 0), To("turnLeft", 20), To("moveStart", 1), .. attackTransitions.Select(a => To(a.Event, a.State))]));
        }
        for (int i = 0; i < Attacks.Count; i++)
        {
            string clip = Attacks[i].Clip;
            var gen = clip.StartsWith("weapon:")
                ? ByWeapon($"Attack{i + 1}", s => $"weapon_{s}_{clip[7..]}", idle, loop: false, endEvent: "attackStop")
                : ClipGen($"Attack{i + 1}", clip, loop: false, endEvent: "attackStop");
            combatStates.Add(State(2 + i, $"Attack{i + 1}", While(gen, $"Attack{i + 1}", "IsAttacking", "bAllowRotation"), [To("attackStop", 0)]));
        }
        if (bowStyle is not null)
        {
            var shot = Machine("TMBowShot", 0,
            [
                State(0, "BowDraw", ClipGen("BowDraw", $"weapon_{bowStyle}_draw", loop: false, endEvent: "bowDrawn"), [To("bowDrawn", 1)]),
                State(1, "BowDrawn", ClipGen("BowHold", $"weapon_{bowStyle}_hold", loop: true), [To("attackRelease", 2)]),
                State(2, "BowRelease", ClipGen("BowRelease", $"weapon_{bowStyle}_release", loop: false, endEvent: "attackStop"), enterEvents: [Ev("BowRelease")]),
            ]);
            combatStates.Add(State(10, "BowAttack", While(shot, "BowAttack", "IsAttacking", "bAllowRotation"), [To("attackStop", 0)], exitEvents: [Ev("bowReset")]));
        }
        var combat = Machine("TMCombat", 0, combatStates);

        // Drawing and sheathing (the engine waits for weaponDraw before it attacks): a weapon's own drawing and putting away.
        hkbGenerator equip, unequip;
        if (styled)
        {
            equip = ByWeapon("Equip", s => $"equip_{s}", idle, loop: false, endEvent: "weapEquipOut");
            unequip = ByWeapon("Unequip", s => $"unequip_{s}", idle, loop: false, endEvent: "weapUnequipOut");
        }
        else
        {
            var e = ClipGen("Equip", idle, loop: false);
            e.m_triggers = new hkbClipTriggerArray { m_triggers = [new hkbClipTrigger { m_localTime = 0.1f, m_event = new hkbEventProperty { m_id = Ev("weapEquipOut") } }] };
            var u = ClipGen("Unequip", idle, loop: false);
            u.m_triggers = new hkbClipTriggerArray { m_triggers = [new hkbClipTrigger { m_localTime = 0.1f, m_event = new hkbEventProperty { m_id = Ev("weapUnequipOut") } }] };
            equip = e; unequip = u;
        }

        // Struck (its hit script, on every blow that lands: Morrowind's hit recovery, or knocked down by Creature::onHit's
        // odds; knocked out while its fatigue is below zero): one of its hits at random, knocked down, knocked out until it
        // comes to (the loop part again and again, then on to the end), all of it unable to act or move (IsStaggering, and
        // its animation driving it), then back to fighting. In water the same with its swimming ones (a dry one where it
        // has none: OpenMW's refreshHitRecoilAnims).
        var swimHits = Family("swimhit");
        hkbGenerator KnockOut(string name, string prefix, string fallbackPrefix)
        {
            string p = rig.Clips.ContainsKey(prefix + "_loop") || rig.Clips.ContainsKey(prefix) ? prefix : fallbackPrefix;
            if (rig.Clips.ContainsKey(p + "_loop"))
            {
                bool hasIn = rig.Clips.ContainsKey(p + "_in"), hasOut = rig.Clips.ContainsKey(p + "_out");
                var parts = new List<hkbStateMachineStateInfo>();
                if (hasIn) parts.Add(State(0, "In", ClipGen($"{name}_in", p + "_in", loop: false, triggers: [(0f, "TMKnockOutLooping", true)]), [To("TMKnockOutLooping", 1)]));
                parts.Add(State(1, "Loop", ClipGen($"{name}_loop", p + "_loop", loop: true, triggers: [(0f, "TMKnockOutCycle", true)]),
                    hasOut ? [To("TMKnockOutCycle", 2, "bTMKnockedOut == 0")] : []));
                if (hasOut) parts.Add(State(2, "Out", ClipGen($"{name}_out", p + "_out", loop: false, endEvent: "TMKnockStop")));
                return Machine($"TM{name}", hasIn ? 0 : 1, parts);
            }
            // No loop keys: the whole group again while still out (OpenMW starts it over while knocked out).
            string whole = rig.Clips.ContainsKey(p) ? p : rig.Clips.ContainsKey("knockdown") ? "knockdown" : hits[0];
            return Machine($"TM{name}", 0, [State(0, "Whole", ClipGen($"{name}_whole", whole, loop: false, triggers: [(0f, "TMKnockOutCycle", true)]),
                [To("TMKnockOutCycle", 0, "bTMKnockedOut == 1", self: true)])]);
        }
        bool KnockOutEndsInLoop(string prefix, string fallbackPrefix)
        {
            string p = rig.Clips.ContainsKey(prefix + "_loop") || rig.Clips.ContainsKey(prefix) ? prefix : fallbackPrefix;
            return !rig.Clips.ContainsKey(p + "_loop") || !rig.Clips.ContainsKey(p + "_out");
        }
        // Leaving a knock-out: at its end, or (no part after the loop) when a cycle ends with it come to.
        IEnumerable<hkbStateMachineTransitionInfo> OutOf(string prefix, string fallbackPrefix, int to) =>
            KnockOutEndsInLoop(prefix, fallbackPrefix) ? [To("TMKnockOutCycle", to, "bTMKnockedOut == 0"), To("TMKnockStop", to)] : [To("TMKnockStop", to)];
        string knockdown = Clip("knockdown", hits[0]);
        string swimKnockdown = Clip("swimknockdown", knockdown);

        bool swims = rig.Clips.Keys.Any(c => c.StartsWith("swim", StringComparison.OrdinalIgnoreCase) || c == "idleswim");
        var baseStates = new List<hkbStateMachineStateInfo>
        {
            State(0, "NonCombat", nonCombat),
            State(1, "Combat", combat, [To("weapUnequip", 6)]),
            State(3, "Stagger", While(OneOf("Stagger", hits, loop: false, endEvent: "staggerStop"), "Stagger", "IsStaggering"), [To("staggerStop", 1)]),
            State(4, "Recoil", While(OneOf("Recoil", hits, loop: false, endEvent: "recoilStop"), "Recoil", "IsRecoiling"), [To("recoilStop", 1)]),
            State(5, "Equip", equip, [To("weapEquipOut", 1)], exitEvents: [Ev("weaponDraw")]),
            State(6, "Unequip", unequip, [To("weapUnequipOut", 0)], enterEvents: [Ev("weaponSheathe")]),
            State(8, "Hit", While(OneOf("Hit", hits, loop: false, endEvent: "TMHitStop"), "Hit", "IsStaggering", "bAnimationDriven"), [To("TMHitStop", 1)]),
            State(9, "KnockDown", While(ClipGen("KnockDown", knockdown, loop: false, endEvent: "TMKnockStop"), "KnockDown", "IsStaggering", "bAnimationDriven", "bTMKnockedDownNow"), [To("TMKnockStop", 1)]),
            State(10, "KnockOut", While(KnockOut("KnockOut", "knockout", "knockout"), "KnockOut", "IsStaggering", "bAnimationDriven", "bTMKnockedOutNow"), OutOf("knockout", "knockout", 1)),
        };
        var baseWild = new List<hkbStateMachineTransitionInfo>
        {
            To("staggerStart", 3), To("staggerForwardStart", 3), To("recoilStart", 4), To("recoilLargeStart", 4), To("weapEquip", 5), To("returnToDefault", 0),
        };
        if (swims)
        {
            // In water: swimming still and moving any way (running in a fight, as Morrowind's combat runs), a biped turning, its
            // swimming attacks (each attack's own, else the dry one), and its swimming hits and knocks.
            hkbGenerator? SwimWay(string w, bool running)
            {
                string? c = running ? Moving("swimrun" + Suffix(w)) ?? Moving("run" + Suffix(w)) : Moving("swimwalk" + Suffix(w)) ?? Moving("walk" + Suffix(w));
                if (w == "forward") c ??= running ? Moving("swimrun") ?? run : Moving("swimwalk") ?? walk;
                return Plain($"Swim{(running ? "Run" : "Walk")}_{w}", c);
            }
            string swimIdle = Clip("idleswim", "idle");
            var swimMove = new hkbManualSelectorGenerator
            {
                m_name = "TMSwimMove",
                m_generators = [Ways("SwimWalk", w => SwimWay(w, false), () => ClipGen("SwimWalk_still", swimIdle, loop: true)), Ways("SwimRun", w => SwimWay(w, true), () => ClipGen("SwimRun_still", swimIdle, loop: true))],
                m_variableBindingSet = new hkbVariableBindingSet { m_indexOfBindingToEnable = -1, m_bindings = [new hkbVariableBindingSetBinding { m_memberPath = "selectedGeneratorIndex", m_variableIndex = Var("iTMRun", 3), m_bitIndex = -1 }] },
            };
            var swimAttacks = Attacks.Select((a, i) => (a.Event, State: 4 + i)).ToList();
            var swimStates = new List<hkbStateMachineStateInfo>
            {
                State(0, "SwimIdle", ClipGen("SwimIdle", swimIdle, loop: true), [To("moveStart", 1), .. turns ? new[] { To("turnLeft", 2), To("turnRight", 3) } : [], .. swimAttacks.Select(a => To(a.Event, a.State))]),
                State(1, "SwimMove", swimMove, [To("moveStop", 0), .. swimAttacks.Select(a => To(a.Event, a.State))]),
            };
            if (turns)
            {
                swimStates.Add(State(2, "SwimTurnLeft", ClipGen("SwimTurnLeft", Clip("swimturnleft", "turnleft", "turnright"), loop: true), [To("turnStop", 0), To("turnRight", 3), To("moveStart", 1)]));
                swimStates.Add(State(3, "SwimTurnRight", ClipGen("SwimTurnRight", Clip("swimturnright", "turnright", "turnleft"), loop: true), [To("turnStop", 0), To("turnLeft", 2), To("moveStart", 1)]));
            }
            for (int i = 0; i < Attacks.Count; i++)
            {
                string clip = Attacks[i].Clip;
                var gen = clip.StartsWith("weapon:")
                    ? ByWeapon($"SwimAttack{i + 1}", s => $"weapon_{s}_{clip[7..]}", idle, loop: false, endEvent: "attackStop")
                    : ClipGen($"SwimAttack{i + 1}", Clip("swim" + clip, clip), loop: false, endEvent: "attackStop");
                swimStates.Add(State(4 + i, $"SwimAttack{i + 1}", While(gen, $"SwimAttack{i + 1}", "IsAttacking", "bAllowRotation"), [To("attackStop", 0)]));
            }
            baseStates.Add(State(7, "Swim", While(Machine("TMSwim", 0, swimStates), "Swim", "bTMSwimming"), [To("swimStop", 0)]));
            baseStates.Add(State(11, "SwimHit", While(OneOf("SwimHit", swimHits.Count > 0 ? swimHits : hits, loop: false, endEvent: "TMHitStop"), "SwimHit", "IsStaggering", "bAnimationDriven", "bTMSwimming"), [To("TMHitStop", 7)]));
            baseStates.Add(State(12, "SwimKnockDown", While(ClipGen("SwimKnockDown", swimKnockdown, loop: false, endEvent: "TMKnockStop"), "SwimKnockDown", "IsStaggering", "bAnimationDriven", "bTMKnockedDownNow", "bTMSwimming"), [To("TMKnockStop", 7)]));
            baseStates.Add(State(13, "SwimKnockOut", While(KnockOut("SwimKnockOut", "swimknockout", "knockout"), "SwimKnockOut", "IsStaggering", "bAnimationDriven", "bTMKnockedOutNow", "bTMSwimming"), OutOf("swimknockout", "knockout", 7)));
            baseWild.Add(To("swimStart", 7));
            baseWild.Add(To("TMHit", 11, "(bTMSwimming == 1) && (IsAttacking == 0)"));
            baseWild.Add(To("TMKnockDown", 12, "bTMSwimming == 1"));
            baseWild.Add(To("TMKnockOut", 13, "bTMSwimming == 1"));
        }
        // A blow lands while it strikes: its attack goes on (Morrowind's attack outranks a hit's animation); knocks interrupt.
        baseWild.Add(To("TMHit", 8, swims ? "(bTMSwimming == 0) && (IsAttacking == 0)" : "IsAttacking == 0"));
        baseWild.Add(To("TMKnockDown", 9, swims ? "bTMSwimming == 0" : null));
        baseWild.Add(To("TMKnockOut", 10, swims ? "bTMSwimming == 0" : null));
        var baseMachine = Machine("TMBase", 0, baseStates, baseWild);

        hkbGenerator baseGen = baseMachine;
        if (headBone >= 0)
        {
            var look = new BSLookAtModifier
            {
                m_name = "TMLookAt", m_enable = true, m_lookAtTarget = true, m_limitAngleDegrees = 90, m_limitAngleThresholdDegrees = 15,
                m_onGain = 0.075f, m_offGain = 0.05f, m_targetOutOfLimitEvent = new hkbEventProperty { m_id = -1 },
                m_bones = [new BSLookAtModifierBoneData { m_index = (short)headBone, m_fwdAxisLS = new Vector4(0, 1, 0, 0), m_limitAngleDegrees = 60, m_onGain = 0.05f, m_offGain = 0.05f, m_enabled = true }],
                m_eyeBones = [],
                m_variableBindingSet = new hkbVariableBindingSet { m_indexOfBindingToEnable = -1, m_bindings = [new hkbVariableBindingSetBinding { m_memberPath = "lookAtTarget", m_variableIndex = Var("bHeadTrackingOn"), m_bitIndex = -1 }] },
            };
            baseGen = new hkbModifierGenerator { m_name = "TMLookAt_MG", m_modifier = look, m_generator = baseMachine };
        }

        // Dying: once, held. As Morrowind picks it (OpenMW's playRandomDeath): knocked down or out, its death from there
        // (swimming, its swimming one); else swimming, its swimming death; else one of its deaths at random. One it has no
        // animation for is a random death.
        var rootStates = new List<hkbStateMachineStateInfo>
        {
            State(0, "Base", baseGen),
            State(1, "Death", OneOf("Death", deaths, loop: false, endEvent: null), [To("GetUpStart", 0)]),
        };
        var deathWays = new List<(string Clip, string When)>
        {
            ("swimdeathknockout", "(bTMKnockedOutNow == 1) && (bTMSwimming == 1)"),
            ("swimdeathknockdown", "(bTMKnockedDownNow == 1) && (bTMSwimming == 1)"),
            ("deathknockout", "(bTMKnockedOutNow == 1) && (bTMSwimming == 0)"),
            ("deathknockdown", "(bTMKnockedDownNow == 1) && (bTMSwimming == 0)"),
            ("swimdeath", "(bTMSwimming == 1) && (bTMKnockedOutNow == 0) && (bTMKnockedDownNow == 0)"),
        };
        var rootWild = new List<hkbStateMachineTransitionInfo>();
        foreach (var evt in new[] { "DeathAnimation", "Ragdoll", "RagdollInstant" })
        {
            for (int i = 0; i < deathWays.Count; i++)
                if (rig.Clips.ContainsKey(deathWays[i].Clip)) rootWild.Add(To(evt, 2 + i, deathWays[i].When));
            rootWild.Add(To(evt, 1));
        }
        for (int i = 0; i < deathWays.Count; i++)
            if (rig.Clips.ContainsKey(deathWays[i].Clip))
                rootStates.Add(State(2 + i, $"Death_{deathWays[i].Clip}", ClipGen($"Death_{deathWays[i].Clip}", deathWays[i].Clip, loop: false), [To("GetUpStart", 0)]));
        var root = Machine("TMRoot", 0, rootStates, rootWild);

        // What the graph works out every frame while it lives (the base state's modifier): which way it moves (Morrowind's
        // switch), whether it runs in water (in a fight), and each moving clip's playback speed (the actor's Speed over
        // Morrowind's pace for it, at most 10).
        var expressions = new List<string>();
        if (variables.Any(v => v.Name == "iTMWay"))
            expressions.Add("iTMWay = cond(Direction < 0.176208, 0, cond(Direction < 0.323792, 1, cond(Direction < 0.676208, 2, cond(Direction < 0.823792, 3, 0))))");
        expressions.Add("iTMRun = cond(bTMInCombat, 1, 0)");
        foreach (var (clip, pace) in rig.MovePaces.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (variables.Any(v => v.Name == RateVar(clip)))
                expressions.Add($"{RateVar(clip)} = clamp(Speed / {pace.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture)}, 0, 10)");
        var evaluate = new hkbEvaluateExpressionModifier
        {
            m_name = "TMEvaluate", m_enable = true,
            m_expressions = new hkbExpressionDataArray { m_expressionsData = expressions.Select(e => new hkbExpressionData { m_expression = e, m_assignmentVariableIndex = -1, m_assignmentEventIndex = -1, m_eventMode = 0 }).ToList() },
        };
        rootStates[0].m_generator = new hkbModifierGenerator { m_name = "TMEvaluate_MG", m_modifier = evaluate, m_generator = baseGen };

        return new hkbBehaviorGraph
        {
            m_name = $"TM{Pascal(id)}Behavior.hkb",
            m_variableMode = 0,
            m_rootGenerator = root,
            m_data = new hkbBehaviorGraphData
            {
                m_attributeDefaults = [],
                m_variableInfos = variables.Select(v => new hkbVariableInfo { m_role = new hkbRoleAttribute(), m_type = v.Type }).ToList(),
                m_characterPropertyInfos = [],
                m_eventInfos = events.Select(_ => new hkbEventInfo()).ToList(),
                m_wordMinVariableValues = [],
                m_wordMaxVariableValues = [],
                m_variableInitialValues = new hkbVariableValueSet { m_wordVariableValues = variables.Select(v => new hkbVariableValue { m_value = v.Value }).ToList(), m_quadVariableValues = [], m_variantVariableValues = [] },
                m_stringData = new hkbBehaviorGraphStringData { m_eventNames = events.ToList(), m_attributeNames = [], m_variableNames = variables.Select(v => v.Name).ToList(), m_characterPropertyNames = [] },
            },
        };
    }

    /// <summary>Writes the project, character and behaviour files and the animation data under <paramref name="dataDir"/>
    /// (the clips themselves are the rig's: <see cref="CreatureRig.Write"/>); <paramref name="headBone"/> is the bone it looks
    /// with (-1: none). Returns the files written, relative to <paramref name="dataDir"/>.</summary>
    public List<string> Write(string dataDir, int headBone)
    {
        var written = new List<string>();
        string pascal = Pascal(id), project = ProjectName(id), character = $"TM{pascal}Character", behavior = $"TM{pascal}Behavior";
        string folder = Path.Combine(dataDir, CreatureRig.MeshFolder(id));
        var graph = Graph(headBone);

        string projectPath = Path.Combine(folder, project + ".hkx");
        Save(projectPath, "hkbProjectData", new hkbProjectData
        {
            m_worldUpWS = new Vector4(0, 0, 1, 0),
            m_defaultEventMode = 2,
            m_stringData = new hkbProjectStringData { m_characterFilenames = [$@"Characters\{character}.hkx"], m_animationFilenames = [], m_behaviorFilenames = [], m_eventNames = [] },
        });
        written.Add(projectPath);

        var box = rig.Box ?? (Vector3.Zero, new Vector3(30, 30, 60));
        const float Havok = 69.99125f;
        string characterPath = Path.Combine(folder, "Characters", character + ".hkx");
        Save(characterPath, "hkbCharacterData", new hkbCharacterData
        {
            m_characterControllerInfo = new hkbCharacterDataCharacterControllerInfo
            {
                m_capsuleHeight = MathF.Max(box.HalfExtents.Z * 2 / Havok, 0.2f),
                m_capsuleRadius = MathF.Max(MathF.Min(box.HalfExtents.X, box.HalfExtents.Y) / Havok, 0.1f),
                m_collisionFilterInfo = 1,
            },
            // Our skeletons face +Y (Morrowind's and the exported models' alike).
            m_modelUpMS = new Vector4(0, 0, 1, 0),
            m_modelForwardMS = new Vector4(0, 1, 0, 0),
            m_modelRightMS = new Vector4(1, 0, 0, 0),
            m_characterPropertyInfos = [],
            m_numBonesPerLod = [],
            m_characterPropertyValues = new hkbVariableValueSet { m_wordVariableValues = [], m_quadVariableValues = [], m_variantVariableValues = [] },
            m_stringData = new hkbCharacterStringData
            {
                m_name = character,
                m_rigName = @"Character Assets\skeleton.HKX",
                m_ragdollName = @"Character Assets\skeleton.HKX",
                m_behaviorFilename = $@"Behaviors\{behavior}.hkx",
                m_animationNames = rig.Clips.Keys.Select(AnimFile).ToList(),
                m_deformableSkinNames = [], m_rigidSkinNames = [], m_animationFilenames = [], m_characterPropertyNames = [],
                m_retargetingSkeletonMapperFilenames = [], m_lodNames = [], m_mirroredSyncPointSubstringsA = [], m_mirroredSyncPointSubstringsB = [],
            },
            m_mirroredSkeletonInfo = new hkbMirroredSkeletonInfo { m_mirrorAxis = new Vector4(1, 0, 0, 0), m_bonePairMap = Enumerable.Range(0, rig.Bones.Count).Select(i => (short)i).ToList() },
            m_scale = 1,
        });
        written.Add(characterPath);

        string behaviorPath = Path.Combine(folder, "Behaviors", behavior + ".hkx");
        Save(behaviorPath, "hkbBehaviorGraph", graph);
        written.Add(behaviorPath);

        written.AddRange(AnimationData(dataDir, project, character, behavior));
        written.AddRange(LooksGraph(dataDir));
        return written.Select(f => Path.GetRelativePath(dataDir, f)).ToList();
    }

    /// <summary>
    /// The behaviour graph the body plays its looks with (<see cref="CreatureRig.Looks"/>; the body's BGED names its project,
    /// <see cref="CreatureRig.LookProject"/>), laid out as vanilla's own for a body's sequences (the spriggan matron's
    /// SprigganFX01): a project, a character on a one-bone skeleton that drives nothing, and a behaviour whose states each
    /// play one sequence (BGSGamebryoSequenceGenerator), entered from any state - itself too, so a repeating clip restarts
    /// its look - on that clip's event, with no blend; its animation data names the three files and has no clips. Without
    /// looks, an earlier build's are taken out.
    /// </summary>
    List<string> LooksGraph(string dataDir)
    {
        var written = new List<string>();
        string projectFile = Path.Combine(dataDir, "meshes", CreatureRig.LookProject(id));
        string folder = Path.GetDirectoryName(projectFile)!, project = Path.GetFileNameWithoutExtension(projectFile);
        string meshes = Path.Combine(dataDir, "meshes"), dataFile = Path.Combine(meshes, "animationdata", project + ".txt");
        if (rig.Looks.Count == 0)
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            if (File.Exists(dataFile)) File.Delete(dataFile);
            CreatureBehavior.RemoveFromIndex(Path.Combine(meshes, "animationdata", "dirlist.txt"), project + ".txt");
            return written;
        }
        string character = project + "Character", behavior = project + "Behavior";
        Save(projectFile, "hkbProjectData", new hkbProjectData
        {
            m_worldUpWS = new Vector4(0, 0, 1, 0),
            m_defaultEventMode = 2,
            m_stringData = new hkbProjectStringData { m_characterFilenames = [$@"Characters\{character}.hkx"], m_animationFilenames = [], m_behaviorFilenames = [], m_eventNames = [] },
        });
        written.Add(projectFile);

        string skeletonFile = Path.Combine(folder, "CharacterAssets", "SingleBoneSkeleton.hkx");
        Directory.CreateDirectory(Path.GetDirectoryName(skeletonFile)!);
        CreatureRig.WriteHavok(skeletonFile, new hkaAnimationContainer
        {
            m_skeletons = [new hkaSkeleton
            {
                m_name = "x_SingleBone", m_parentIndices = [-1], m_bones = [new hkaBone { m_name = "x_SingleBone", m_lockTranslation = false }],
                m_referencePose = [CreatureRig.QsTransform(Vector3.Zero, Quaternion.Identity)], m_referenceFloats = [], m_floatSlots = [], m_localFrames = [],
            }],
            m_animations = [], m_bindings = [], m_attachments = [], m_skins = [],
        });
        written.Add(skeletonFile);

        string characterFile = Path.Combine(folder, "Characters", character + ".hkx");
        Save(characterFile, "hkbCharacterData", new hkbCharacterData
        {
            m_characterControllerInfo = new hkbCharacterDataCharacterControllerInfo { m_capsuleHeight = 1.7f, m_capsuleRadius = 0.4f, m_collisionFilterInfo = 1 },
            m_modelUpMS = new Vector4(0, 0, 1, 0), m_modelForwardMS = new Vector4(1, 0, 0, 0), m_modelRightMS = new Vector4(0, -1, 0, 0),
            m_characterPropertyInfos = [], m_numBonesPerLod = [],
            m_characterPropertyValues = new hkbVariableValueSet { m_wordVariableValues = [], m_quadVariableValues = [], m_variantVariableValues = [] },
            m_stringData = new hkbCharacterStringData
            {
                m_name = character, m_rigName = @"CharacterAssets\SingleBoneSkeleton.hkx", m_ragdollName = "", m_behaviorFilename = $@"Behaviors\{behavior}.hkx",
                m_animationNames = [], m_deformableSkinNames = [], m_rigidSkinNames = [], m_animationFilenames = [], m_characterPropertyNames = [],
                m_retargetingSkeletonMapperFilenames = [], m_lodNames = [], m_mirroredSyncPointSubstringsA = [], m_mirroredSyncPointSubstringsB = [],
            },
            m_mirroredSkeletonInfo = new hkbMirroredSkeletonInfo { m_mirrorAxis = new Vector4(1, 0, 0, 0), m_bonePairMap = [0] },
            m_scale = 1,
        });
        written.Add(characterFile);

        var clips = rig.Looks.Keys.ToList();
        var noBlend = new hkbBlendingTransitionEffect { m_name = "ZeroDuration", m_duration = 0, m_selfTransitionMode = 2, m_eventMode = 0 };   // a self transition starts it over
        var states = clips.Select((c, i) => new hkbStateMachineStateInfo
        {
            m_stateId = i, m_name = c + "Look", m_probability = 1, m_enable = true,
            m_generator = new BGSGamebryoSequenceGenerator { m_name = c + "Look", m_pSequence = CreatureRig.LookEvent(c), m_eBlendModeFunction = 0, m_fPercent = 1 },
        }).ToList();
        var wildcards = clips.Select((c, i) => new hkbStateMachineTransitionInfo
        {
            m_triggerInterval = new hkbStateMachineTimeInterval { m_enterEventId = -1, m_exitEventId = -1 },
            m_initiateInterval = new hkbStateMachineTimeInterval { m_enterEventId = -1, m_exitEventId = -1 },
            m_transition = noBlend, m_eventId = i, m_toStateId = i,
            m_flags = 256 | 512,   // no condition; may go from a state to itself
        }).ToList();
        int start = Math.Max(0, clips.IndexOf("idle"));
        var root = Machine($"{project}Root", start, states, wildcards);
        Save(Path.Combine(folder, "Behaviors", behavior + ".hkx"), "hkbBehaviorGraph", new hkbBehaviorGraph
        {
            m_name = behavior + ".hkb",
            m_variableMode = 0,
            m_rootGenerator = root,
            m_data = new hkbBehaviorGraphData
            {
                m_attributeDefaults = [], m_variableInfos = [], m_characterPropertyInfos = [],
                m_eventInfos = clips.Select(_ => new hkbEventInfo()).ToList(),
                m_wordMinVariableValues = [], m_wordMaxVariableValues = [],
                m_variableInitialValues = new hkbVariableValueSet { m_wordVariableValues = [], m_quadVariableValues = [], m_variantVariableValues = [] },
                m_stringData = new hkbBehaviorGraphStringData { m_eventNames = clips.Select(CreatureRig.LookEvent).ToList(), m_attributeNames = [], m_variableNames = [], m_characterPropertyNames = [] },
            },
        });
        written.Add(Path.Combine(folder, "Behaviors", behavior + ".hkx"));

        // Its animation data: the three files and no clips (as SprigganFX01's in the game's single file).
        written.Add(WriteText(dataFile, ["1", "3", $@"Behaviors\{behavior}.hkx", $@"Characters\{character}.hkx", @"CharacterAssets\SingleBoneSkeleton.hkx", "0"]));
        written.Add(archive.AddToIndex(Path.Combine(meshes, "animationdata", "dirlist.txt"), @"meshes\animationdata\dirlist.txt", project + ".txt"));
        return written;
    }

    static void Save(string path, string className, hkReferencedObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var container = new hkRootLevelContainer { m_namedVariants = [new hkRootLevelContainerNamedVariant { m_name = className, m_className = className, m_variant = root }] };
        using var fs = File.Create(path);
        new PackFileSerializer().Serialize(container, new BinaryWriterEx(fs), HKXHeader.SkyrimSE());
    }

    static string Num(float v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    List<string> AnimationData(string dataDir, string project, string character, string behavior)
    {
        var written = new List<string>();
        string meshes = Path.Combine(dataDir, "meshes");
        foreach (var stale in CreatureBehavior.StaleData(dataDir, project)) if (File.Exists(stale)) File.Delete(stale);
        string setDir = Path.Combine(meshes, "animationsetdata", project + "Data");
        if (Directory.Exists(setDir)) Directory.Delete(setDir, true);

        // animationdata\<project>.txt: the files, then per clip generator its name, its animation's id, speed, crops and
        // events ("event:time"; payload events as "SoundPlay.X:time").
        var clipIds = rig.Clips.Keys.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => x.i);
        var lines = new List<string> { "1", "3", $@"Behaviors\{behavior}.hkx", $@"Characters\{character}.hkx", @"Character Assets\skeleton.HKX", "1" };
        foreach (var (name, clip, end) in generators)
        {
            var evs = rig.Events.GetValueOrDefault(clip)?.ToList() ?? [];
            if (end is not null) evs.Add((Math.Max(0, rig.Duration(clip) - 0.02f), end));
            if (name == "Equip") evs.Add((0.1f, "weapEquipOut"));
            if (name == "Unequip") evs.Add((0.1f, "weapUnequipOut"));
            // Its look as it starts (and each time it repeats), as vanilla's clips start their attached models' sequences
            // (the benthic lurker's MouthWhipLong, WhipDustLtoR).
            if (rig.Looks.ContainsKey(clip)) evs.Add((0, CreatureRig.LookEvent(clip)));
            lines.Add(name); lines.Add(clipIds[clip].ToString()); lines.Add("1"); lines.Add("0"); lines.Add("0");
            lines.Add(evs.Count.ToString());
            lines.AddRange(evs.OrderBy(e => e.Time).Select(e => $"{e.Text}:{Num(e.Time)}"));
            lines.Add("");
        }
        written.Add(WriteText(Path.Combine(meshes, "animationdata", project + ".txt"), lines));

        // boundanims\anims_<project>.txt: per animation its length and motion, the walk and run moving forward (+Y) at the
        // creature's speeds, the rest in place.
        var motion = new List<string>();
        foreach (var (clip, cid) in clipIds)
        {
            float dur = rig.Duration(clip), speed = rig.MoveSpeeds.GetValueOrDefault(clip);
            var way = rig.MoveWays.GetValueOrDefault(clip, new Vector2(0, 1));
            motion.Add(cid.ToString()); motion.Add(Num(dur));
            motion.Add("1"); motion.Add($"{Num(dur)} {Num(way.X * speed * dur)} {Num(way.Y * speed * dur)} 0");
            motion.Add("1"); motion.Add($"{Num(dur)} 0 0 0 1");
            motion.Add("");
        }
        written.Add(WriteText(Path.Combine(meshes, "animationdata", "boundanims", $"anims_{project}.txt"), motion));

        // animationsetdata\<project>Data\: the list, and one set naming each attack's clip generator and every animation file
        // (folder CRC, name CRC, "hkx").
        uint folderCrc = CreatureBehavior.Crc($@"{CreatureRig.MeshFolder(id)}\animations");
        var set = new List<string> { "V3", "0", "0", Attacks.Count.ToString() };
        for (int i = 0; i < Attacks.Count; i++) { set.Add(Attacks[i].Event); set.Add("0"); set.Add("1"); set.Add($"Attack{i + 1}"); }
        set.Add(rig.Clips.Count.ToString());
        foreach (var clip in rig.Clips.Keys) { set.Add(folderCrc.ToString()); set.Add(CreatureBehavior.Crc(clip).ToString()); set.Add("7891816"); }
        written.Add(WriteText(Path.Combine(setDir, project + ".txt"), ["FullCharacter.txt"]));
        written.Add(WriteText(Path.Combine(setDir, "FullCharacter.txt"), set));

        written.Add(archive.AddToIndex(Path.Combine(meshes, "animationdata", "dirlist.txt"), @"meshes\animationdata\dirlist.txt", project + ".txt"));
        written.Add(archive.AddToIndex(Path.Combine(meshes, "animationsetdata", "dirlist.txt"), @"meshes\animationsetdata\dirlist.txt", $@"{project}Data\{project}.txt"));
        return written;
    }

    static string WriteText(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
        return path;
    }
}
