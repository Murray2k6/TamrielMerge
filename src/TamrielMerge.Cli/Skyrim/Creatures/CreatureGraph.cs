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
/// <item>at rest: idle, and its walk on moveStart until moveStop;</item>
/// <item>weapEquip draws (weaponDraw on leaving), weapUnequip sheathes (weaponSheathe on entering);</item>
/// <item>in combat: its idle with IsAttackReady, its run while moving, and each of its attacks on that attack's
/// attackStart_ event (the race names them) with IsAttacking, ending in attackStop;</item>
/// <item>staggerStart and recoilStart play its hit with IsStaggering / IsRecoiling, ending in staggerStop / recoilStop;</item>
/// <item>DeathAnimation, Ragdoll and RagdollInstant from anywhere play its death once and hold its last frame (no ragdoll:
/// it stays where it fell); GetUpStart brings it back;</item>
/// <item>look-at on its head bone when it has one.</item>
/// </list>
/// Its animation data is its own too: every clip generator's events (its own sounds, hit moments, the ends above),
/// motion for its walk and run at its own speeds (Y forward, as vanilla's), and attack sets naming its attacks.
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

    /// <summary>The attacks a creature with these clips has: each "attack…" clip (in order) on attackStart_Attack1, 2...</summary>
    public static List<(string Event, string Clip)> AttackList(IEnumerable<string> clips) =>
        clips.Where(c => c.StartsWith("attack", StringComparison.OrdinalIgnoreCase)).Select(c => c.ToLowerInvariant()).OrderBy(c => c, StringComparer.Ordinal).Select((c, i) => ($"attackStart_Attack{i + 1}", c)).ToList();

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

    readonly hkbBlendingTransitionEffect blend = new() { m_name = "TMBlend", m_duration = 0.2f, m_selfTransitionMode = 0, m_eventMode = 0 };

    hkbStateMachineTransitionInfo To(string evt, int state) => new()
    {
        m_triggerInterval = new hkbStateMachineTimeInterval { m_enterEventId = -1, m_exitEventId = -1 },
        m_initiateInterval = new hkbStateMachineTimeInterval { m_enterEventId = -1, m_exitEventId = -1 },
        m_transition = blend,
        m_eventId = Ev(evt),
        m_toStateId = state,
        m_flags = 256,   // no condition
    };

    hkbClipGenerator ClipGen(string name, string clip, bool loop, string? endEvent = null)
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
        if (endEvent is not null)
            g.m_triggers = new hkbClipTriggerArray { m_triggers = [new hkbClipTrigger { m_localTime = Math.Max(0, rig.Duration(clip) - 0.02f), m_event = new hkbEventProperty { m_id = Ev(endEvent) } }] };
        return g;
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
        Var("iSyncIdleLocomotion", 3); Var("iState", 3); Var("iSyncTurnState", 3, 1);
        Var("Direction", 4); Var("Speed", 4); Var("TurnDelta", 4); Var("staggerMagnitude", 4, Real(1));
        // The events the engine sends and listens for (the same).
        foreach (var e in new[] { "moveStart", "moveStop", "preHitFrame", "HitFrame", "weaponDraw", "weaponSheathe", "weapEquip", "weapUnequip", "weapEquipOut", "weapUnequipOut",
            "attackStop", "weaponSwing", "recoilStart", "recoilStop", "recoilLargeStart", "staggerStart", "staggerStop", "staggerForwardStart", "turnLeft", "turnRight", "turnStop",
            "GetUpStart", "GetUpEnd", "GetUpBegin", "Getup", "Ragdoll", "RagdollInstant", "DeathAnimation", "AddCharacterControllerToWorld", "RemoveCharacterControllerFromWorld",
            "Reanimated", "AddRagdollToWorld", "IdleStop", "SoundPlay", "returnToDefault", "PickNewIdle" })
            Ev(e);

        string idle = Clip("idle"), walk = Clip("walk", "run"), run = Clip("run", "walk"), hit = Clip("hit", "idle"), death = Clip("death", "idle");

        // At rest.
        var nonCombat = Machine("TMNonCombat", 0,
        [
            State(0, "Idle", ClipGen("Idle", idle, loop: true), [To("moveStart", 1)]),
            State(1, "Walk", ClipGen("Walk", walk, loop: true), [To("moveStop", 0)]),
        ]);

        // In combat: each attack is its own state, entered from idle or moving on its event.
        var combatStates = new List<hkbStateMachineStateInfo>();
        var attackTransitions = Attacks.Select((a, i) => (a.Event, State: 2 + i)).ToList();
        combatStates.Add(State(0, "CombatIdle", While(ClipGen("CombatIdle", idle, loop: true), "CombatIdle", "IsAttackReady"),
            [To("moveStart", 1), .. attackTransitions.Select(a => To(a.Event, a.State))]));
        combatStates.Add(State(1, "CombatRun", While(ClipGen("CombatRun", run, loop: true), "CombatRun", "IsAttackReady"),
            [To("moveStop", 0), .. attackTransitions.Select(a => To(a.Event, a.State))]));
        for (int i = 0; i < Attacks.Count; i++)
            combatStates.Add(State(2 + i, $"Attack{i + 1}", While(ClipGen($"Attack{i + 1}", Attacks[i].Clip, loop: false, endEvent: "attackStop"), $"Attack{i + 1}", "IsAttacking", "bAllowRotation"),
                [To("attackStop", 0)]));
        var combat = Machine("TMCombat", 0, combatStates);

        // Drawing and sheathing (the engine waits for weaponDraw before it attacks).
        var equip = ClipGen("Equip", idle, loop: false);
        equip.m_triggers = new hkbClipTriggerArray { m_triggers = [new hkbClipTrigger { m_localTime = 0.1f, m_event = new hkbEventProperty { m_id = Ev("weapEquipOut") } }] };
        var unequip = ClipGen("Unequip", idle, loop: false);
        unequip.m_triggers = new hkbClipTriggerArray { m_triggers = [new hkbClipTrigger { m_localTime = 0.1f, m_event = new hkbEventProperty { m_id = Ev("weapUnequipOut") } }] };

        var baseMachine = Machine("TMBase", 0,
        [
            State(0, "NonCombat", nonCombat),
            State(1, "Combat", combat, [To("weapUnequip", 6)]),
            State(3, "Stagger", While(ClipGen("Stagger", hit, loop: false, endEvent: "staggerStop"), "Stagger", "IsStaggering"), [To("staggerStop", 1)]),
            State(4, "Recoil", While(ClipGen("Recoil", hit, loop: false, endEvent: "recoilStop"), "Recoil", "IsRecoiling"), [To("recoilStop", 1)]),
            State(5, "Equip", equip, [To("weapEquipOut", 1)], exitEvents: [Ev("weaponDraw")]),
            State(6, "Unequip", unequip, [To("weapUnequipOut", 0)], enterEvents: [Ev("weaponSheathe")]),
        ],
        [To("staggerStart", 3), To("staggerForwardStart", 3), To("recoilStart", 4), To("recoilLargeStart", 4), To("weapEquip", 5), To("returnToDefault", 0)]);

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

        // Dying: its death once, held.
        var root = Machine("TMRoot", 0,
        [
            State(0, "Base", baseGen),
            State(1, "Death", ClipGen("Death", death, loop: false), [To("GetUpStart", 0)]),
        ],
        [To("DeathAnimation", 1), To("Ragdoll", 1), To("RagdollInstant", 1)]);

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
        return written.Select(f => Path.GetRelativePath(dataDir, f)).ToList();
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
            motion.Add(cid.ToString()); motion.Add(Num(dur));
            motion.Add("1"); motion.Add($"{Num(dur)} 0 {Num(speed * dur)} 0");
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
