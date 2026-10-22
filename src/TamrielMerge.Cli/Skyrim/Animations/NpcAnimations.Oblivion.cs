using System.Numerics;
using System.Runtime.InteropServices;
using TamrielMerge.Morrowind;
using TamrielMerge.Oblivion;
using TamrielMerge.Skyrim.Creatures;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Skyrim.Animations;

/// <summary>
/// Oblivion's people moving as Oblivion's do (user, 2026-10-21: "fix the npcs"), the same way as Morrowind's
/// (<see cref="NpcAnimations"/>): Oblivion's character animations (meshes\characters\_male\, one .kf per animation, the
/// same for men and women) on its skeletons (skeleton.nif for the human races, skeletonbeast.nif for Khajiit and
/// Argonians, as the races name them) retargeted onto Skyrim's male or female skeleton.
/// <list type="bullet">
/// <item>Each .kf is one group named as the file (idleanims\ponder01 for one in a folder): its sequence's start and
/// stop, the whole of it its loop when the sequence loops; its text keys under the group, those Skyrim's events are
/// timed by named as Morrowind's are (an equip's "Attach" its "Equip Attach", an unequip's "Detach" its "Unequip
/// Detach", the bow's "Attach", "Hold" and "Release" its "Shoot ..." keys, a cast's "Hit" its "Release"), and its
/// footfalls ("Enum: Left", "Enum: Right") as Morrowind's sound keys.</item>
/// <item>Oblivion plays a weapon's moves over its idle, each file keying the bones it moves (its attacks and casts
/// the upper body over the legs of the idle or the walk under them): a bone a file leaves alone comes from the group
/// under it (<see cref="ObBase"/>, or a Skyrim file's own: a walking attack over that walk).</item>
/// <item>Skyrim's centre of mass from "Bip01 NonAccum" (Oblivion's "Bip01" is the travel alone).</item>
/// </list>
/// Oblivion has no knock-out or death animations (its actors fall as ragdolls, as Skyrim's do after their own), so
/// those stay Skyrim's.
/// </summary>
public static partial class NpcAnimations
{
    const string ObFolder = @"meshes\characters\_male\";

    /// <summary>Oblivion's: men and women of the human races, and of the beast races (their own skeleton).</summary>
    public static readonly Set[] Oblivion =
    [
        new("OblivionMen", "Oblivion's men (characters\\_male, skeleton.nif)", ObFolder + "skeleton.nif", [ObFolder], false, "TMObPersonMan", "Oblivion"),
        new("OblivionWomen", "Oblivion's women (characters\\_male, skeleton.nif)", ObFolder + "skeleton.nif", [ObFolder], true, "TMObPersonWoman", "Oblivion"),
        new("OblivionBeastMen", "Oblivion's Khajiit and Argonian men (characters\\_male, skeletonbeast.nif)", ObFolder + "skeletonbeast.nif", [ObFolder], false, "TMObBeastMan", "Oblivion"),
        new("OblivionBeastWomen", "Oblivion's Khajiit and Argonian women (characters\\_male, skeletonbeast.nif)", ObFolder + "skeletonbeast.nif", [ObFolder], true, "TMObBeastWoman", "Oblivion"),
    ];

    /// <summary>The table of Skyrim files a set fills, by its game.</summary>
    public static List<(string File, Source Source)> TableFor(Set set, Func<string, bool> exists) =>
        set.Game == "Oblivion" ? ObTable(set.Female, exists) : Table(set.Female, exists);

    /// <summary>The weapon kinds' Oblivion file prefixes for Skyrim's families: unarmed none, one-handed, two-handed (both
    /// Skyrim kinds: Oblivion's two-handed blades and blunts share them), fists, the bow; Skyrim's magic stances
    /// Oblivion's fists (an Oblivion NPC readied with no weapon raises its fists, its spells cast over them).</summary>
    static readonly (string Family, string Prefix, bool Attacks)[] ObFamilies =
    [
        ("mt", "", false), ("1hm", "onehand", true), ("2hm", "twohand", true), ("2hw", "twohand", true),
        ("h2h", "handtohand", true), ("bow", "bow", false), ("mag", "handtohand", false), ("magcast", "handtohand", false),
    ];

    /// <summary>Oblivion's move file for a weapon prefix: walking ("walkforward", "onehandforward"), running (its fast
    /// moves: "walkfastforward", "onehandfastforward") or turning ("walkturnleft", "onehandturnleft").</summary>
    static string ObMove(string prefix, string kind, string way) => (prefix, kind) switch
    {
        ("", "walk") => "walk" + way,
        ("", "run") => "walkfast" + way,
        ("", "turn") => "walkturn" + way,
        (_, "walk") => prefix + way,
        (_, "run") => prefix + "fast" + way,
        _ => prefix + "turn" + way,
    };

    /// <summary>
    /// Every Skyrim character animation an Oblivion file stands for: standing, walking and running each way (Skyrim's
    /// diagonals Oblivion's forward or back: Oblivion has four ways), turning, drawing and putting away, each attack
    /// (Skyrim's right and left swings Oblivion's, its power attacks Oblivion's standing, forward, back, left and right
    /// ones, moving and sneaking ones the same blows over that move; Skyrim's wind-ups the attack's first frame, as
    /// Oblivion swings at once), blocking, recoiling and staggering, the bow (drawn to its "Hold", held, loosed), casting
    /// (to the spell going, held, after), the shield and torch, swimming, sneaking, jumping, and Skyrim's fidgets as
    /// Oblivion's own standing idles.
    /// </summary>
    public static List<(string File, Source Source)> ObTable(bool female, Func<string, bool> exists)
    {
        var table = new List<(string, Source)>();
        void Add(string file, Source s) { if (exists(file)) table.Add((file, s)); }
        string sex = female ? "female" : "male";
        var walks = new[] { ("walkforward", "walk", "forward"), ("walkforwardleft", "walk", "forward"), ("walkforwardright", "walk", "forward"), ("walkforwrdleft", "walk", "forward"), ("walkfrwrdright", "walk", "forward"),
            ("walkbackward", "walk", "backward"), ("walkbackwardleft", "walk", "backward"), ("walkbackwardright", "walk", "backward"), ("walkbckwrdleft", "walk", "backward"), ("walkbckwrdrht", "walk", "backward"),
            ("walkleft", "walk", "left"), ("walkright", "walk", "right"),
            ("runforward", "run", "forward"), ("runforwardleft", "run", "forward"), ("runforwardright", "run", "forward"), ("runfrwrdright", "run", "forward"), ("sprintforward", "run", "forward"), ("sprintforwardsword", "run", "forward"), ("sprint", "run", "forward"),
            ("runbackward", "run", "backward"), ("runbackwardleft", "run", "backward"), ("runbackwardright", "run", "backward"), ("runbackwrdleft", "run", "backward"), ("runbckwrdright", "run", "backward"),
            ("runleft", "run", "left"), ("runright", "run", "right"), ("runstrafeleft", "run", "left"), ("runstraferight", "run", "right") };
        // A moving attack's move (Skyrim's names for them) as Oblivion's move under it.
        var attackMoves = new[] { ("walkfwd", "walk", "forward"), ("walkbwd", "walk", "backward"), ("walkleft", "walk", "left"), ("walkright", "walk", "right"), ("walkrt", "walk", "right"),
            ("runfwd", "run", "forward"), ("runbwd", "run", "backward"), ("runleft", "run", "left"), ("runright", "run", "right"), ("runforward", "run", "forward"), ("runback", "run", "backward"),
            ("walkforward", "walk", "forward"), ("walkback", "walk", "backward") };
        foreach (var (fam, p, attacks) in ObFamilies)
        {
            string P(string name) => fam == "mt" ? $@"{sex}\mt_{name}.hkx" : $"{fam}_{name}.hkx";
            Add(P("idle"), new Source(p + "idle", Loop: true));
            foreach (var (sk, kind, way) in walks) Add(P(sk), new Source(ObMove(p, kind, way), Loop: true));
            foreach (var (sk, way) in new[] { ("turnleft60", "left"), ("turnleft180", "left"), ("turnleft360", "left"), ("turnright60", "right"), ("turnright180", "right"), ("turnright360", "right") })
                Add(fam == "mt" ? $"mt_{sk}.hkx" : P(sk), new Source(ObMove(p, "turn", way), Loop: true, Stretch: true));
            if (fam is "mag" or "magcast")
            {
                Add(P("equip"), new Source("handtohandequip"));
                Add(P("unequip"), new Source("handtohandunequip"));
            }
            if (!attacks) continue;
            Add(P("equip"), new Source(p + "equip"));
            Add(P("unequip"), new Source(p + "unequip"));
            Source Blow(string g, string? under = null) => new(p + g, Base: under);
            Source WindUp(string g, string? under = null) => new(p + g, "Start", "Start", Hold: true, Base: under);
            Add(P("attackright"), Blow("attackright"));
            Add(P("attackleft"), Blow("attackleft"));
            Add(P("attackrightintro"), WindUp("attackright"));
            Add(P("attackleftintro"), WindUp("attackleft"));
            foreach (var power in new[] { "attackpowerfwd", "attackpowerforward", "attackpowerforwardsprint", "attackforwardsprint", "attackrightpowerforward", "attackpowerforwardlefthand", "attackpowerforwardrighthand" })
                Add(P(power), Blow("attackforwardpower"));
            Add(P("attackpower"), Blow("attackpower"));
            Add(P("attackpower3slashcombo"), Blow("attackpower"));
            Add(P("attackpowerbwd"), Blow("attackbackpower"));
            Add(P("attackpowerleft"), Blow("attackleftpower"));
            Add(P("attackpowerright"), Blow("attackrightpower"));
            foreach (var (move, kind, way) in attackMoves)
                foreach (var (sk, ob) in new[] { ("attackright", "attackright"), ("attackrt", "attackright"), ("attackleft", "attackleft") })
                {
                    Add(P(move + sk), Blow(ob, ObMove(p, kind, way)));
                    Add(P(move + sk + "intro"), WindUp(ob, ObMove(p, kind, way)));
                }
            // Fists' jabs, crosses and hooks: Oblivion's own.
            foreach (var (sk, ob) in new[] { ("jab", "attackleft_jab"), ("cross", "attackright_jab"), ("leftbodyhook", "attackleft_hook"), ("rightbodyhook", "attackright_hook") })
            {
                Add(P(sk), Blow(ob));
                foreach (var (move, kind, way) in new[] { ("walkforward", "walk", "forward"), ("walkback", "walk", "backward"), ("walkleft", "walk", "left"), ("walkright", "walk", "right"),
                    ("runforward", "run", "forward"), ("runback", "run", "backward"), ("runleft", "run", "left"), ("runright", "run", "right") })
                    Add(P(move + sk), Blow(ob, ObMove(p, kind, way)));
            }
            // Sneaking attacks: Oblivion's sneaking power attack, and its blows over its sneaking idle.
            string sneakIdle = "sneak" + p + "idle";
            foreach (var (sneakFile, s) in fam switch
            {
                "1hm" => new[] { ("sneak_1hmattack.hkx", Blow("attackright", sneakIdle)), ("sneak_1hmattackintro.hkx", WindUp("attackright", sneakIdle)), ("sneak_1hmattacklefthand.hkx", Blow("attackleft", sneakIdle)),
                    ("sneak_1hmattackpowerlefthand.hkx", new Source("sneakonehandattackpower")), ("1hm_sneakattackleft.hkx", Blow("attackleft", sneakIdle)), ("1hm_sneakattackright.hkx", Blow("attackright", sneakIdle)),
                    ("1hm_sneakattackpower.hkx", new Source("sneakonehandattackpower")) },
                "2hm" => [("sneak_2hmattack.hkx", Blow("attackright", sneakIdle))],
                "2hw" => [("sneak_2hwattack.hkx", Blow("attackright", sneakIdle))],
                "h2h" => [("sneak_h2hattacklefthand.hkx", Blow("attackleft", sneakIdle)), ("sneak_h2hattackrighthand.hkx", Blow("attackright", sneakIdle)),
                    ("sneak_beasth2hattacklefthand.hkx", Blow("attackleft", sneakIdle)), ("sneak_beasth2hattackrighthand.hkx", Blow("attackright", sneakIdle))],
                _ => [],
            })
                Add(sneakFile, s);
            // Blocking with it: struck while blocking, and its block held (raised into from its first frame).
            foreach (var hit in new[] { "blockhit", "blockhita", "blockhitb" }) Add(P(hit), new Source(p + "blockhit"));
            Add(P("blockanticipate"), new Source(p + "blockidle", "Start", "Start", Hold: true));
            Add(P("blockidle"), new Source(p + "blockidle", Loop: true));
            // Struck: Oblivion's recoils (three for a weapon, one for fists) and its stagger.
            string Recoil(int n) => p == "handtohand" ? "handtohandrecoil" : $"{p}recoil_0{n}";
            foreach (var (sk, n) in new[] { ("recoilleft", 1), ("recoilright", 2), ("recoiltimed", 3) }) Add(P(sk), new Source(Recoil(n)));
            foreach (var stagger in new[] { "staggerbacksmallest", "staggerbacksmall", "staggerbackmedium", "staggerbacklarge", "staggerbacklargest", "staggerforwardsmall", "staggerforwardmedium",
                "staggerforwardlarge", "staggerforwardlargest", "staggeridle" })
                Add(P(stagger), new Source(p + "stagger"));
        }
        // The bow: drawn to its "Hold", held there, loosed (Oblivion's one shot, its sneaking one when sneaking).
        Add("bow_equip.hkx", new Source("bowequip"));
        Add("bow_unequip.hkx", new Source("bowunequip"));
        foreach (var (draw, g) in new[] { ("bow_drawlight.hkx", "bowattack"), ("bow_drawheavy.hkx", "bowattack"), ("sneakbow_drawlight.hkx", "sneakbowattack") }) Add(draw, new Source(g, "Start", "Shoot Hold"));
        foreach (var (held, g) in new[] { ("bow_idledrawn.hkx", "bowattack"), ("bow_idleheld.hkx", "bowattack"), ("sneakbow_idledrawn.hkx", "sneakbowattack") }) Add(held, new Source(g, "Shoot Hold", "Shoot Hold", Hold: true));
        foreach (var (release, g) in new[] { ("bow_release.hkx", "bowattack"), ("sneakbow_release.hkx", "sneakbowattack") }) Add(release, new Source(g, "Shoot Hold", "Stop"));
        Add("bow_blockhit.hkx", new Source("bowblockhit"));
        Add("bow_blockanticipate.hkx", new Source("bowblockidle", "Start", "Start", Hold: true));
        Add("bow_blockidle.hkx", new Source("bowblockidle", Loop: true));
        // Magic: Oblivion casts with no readying, from its fists' stance: up to the spell going ("Hit"), that pose held
        // while Skyrim charges, the spell going; Skyrim's self casts Oblivion's self cast, the rest its target cast.
        foreach (var hand in new[] { "mlh", "mrh" })
        {
            Add($"{hand}_equip.hkx", new Source("handtohandequip"));
            Add($"{hand}_unequip.hkx", new Source("handtohandunequip"));
            Add($"{hand}_idle.hkx", new Source("handtohandidle", Loop: true));
            foreach (var (sk, range) in new[] { ("precharge", "target"), ("selfprecharge", "self"), ("preready", "target"), ("prewardcharge", "target"), ("preaimedcon", "target"), ("preselfcon", "self"), ("pretelekinesis", "target") })
                Add($"{hand}_{sk}.hkx", new Source("cast" + range, "Start", "Release"));
            foreach (var (sk, range) in new[] { ("chargeloop", "target"), ("readyloop", "target"), ("selfchargeloop", "self"), ("selfreadyloop", "self"), ("wardcharge", "target"), ("wardloop", "target"),
                ("prewardloop", "target"), ("aimedconcentration", "target"), ("selfconcentration", "self"), ("telekinesisloop", "target") })
                Add($"{hand}_{sk}.hkx", new Source("cast" + range, "Release", "Release", Hold: true));
            Add($"{hand}_release.hkx", new Source("casttarget", "Release", "Stop"));
            Add($"{hand}_selfrelease.hkx", new Source("castself", "Release", "Stop"));
        }
        Add("mrh_and_mlh_equip.hkx", new Source("handtohandequip"));
        Add("mrh_and_mlh_unequip.hkx", new Source("handtohandunequip"));
        // The shield (Oblivion blocks with it in its one-handed block) and the torch (its block with the torch held).
        foreach (var block in new[] { "shd_blockhit.hkx", "shd_blockhit_vara.hkx", "shd_blockhit_varb.hkx", "shd_blocktimed.hkx" }) Add(block, new Source("onehandblockhit"));
        Add("shd_blockanticipate.hkx", new Source("onehandblockidle", "Start", "Start", Hold: true));
        Add("shd_blockidle.hkx", new Source("onehandblockidle", Loop: true));
        foreach (var block in new[] { "tor_blockhit.hkx", "tor_blockhitmt.hkx" }) Add(block, new Source("blockhit"));
        foreach (var block in new[] { "tor_blockanticipate.hkx", "tor_blockanticipatemt.hkx" }) Add(block, new Source("blockidle", "Start", "Start", Hold: true));
        Add("tor_blockidle.hkx", new Source("blockidle", Loop: true));
        Add("tor_idle.hkx", new Source("torchidle", Loop: true));
        Add("tor_mtpose.hkx", new Source("torchidle", Loop: true));
        Add("tor_1hmpose.hkx", new Source("onehandtorchidle", Loop: true));
        Add("tor_mrhpose.hkx", new Source("handtohandtorchidle", Loop: true));
        // Swimming (Oblivion's running swim, as its people run by default).
        Add("swimidle.hkx", new Source("swimidle", Loop: true));
        Add("swimforward.hkx", new Source("swimfastforward", Loop: true));
        Add("swimbackward.hkx", new Source("swimfastbackward", Loop: true));
        Add("swimleft.hkx", new Source("swimfastleft", Loop: true));
        Add("swimright.hkx", new Source("swimfastright", Loop: true));
        // Sneaking: standing, walking (its sneaking walk), running (its sneaking fast moves), turning.
        Add("sneakmtidle.hkx", new Source("sneakidle", Loop: true));
        Add("sneak1hm_idle.hkx", new Source("sneakonehandidle", Loop: true));
        Add("sneakh2h_idle.hkx", new Source("sneakhandtohandidle", Loop: true));
        foreach (var (sk, ob) in new[] { ("sneakwalk_forward", "sneakforward"), ("sneakwalk_frwrdright", "sneakforward"), ("sneakwalk_fwrwrdleft", "sneakforward"), ("sneakwalk_bckward", "sneakbackward"),
            ("sneakwalk_bckwrdleft", "sneakbackward"), ("sneakwalk_bckwrdright", "sneakbackward"), ("sneakwalk_left", "sneakleft"), ("sneakwalk_right", "sneakright"),
            ("sneakrun_forward", "sneakfastforward"), ("sneakrun_frwrdleft", "sneakfastforward"), ("sneakrun_frwrdright", "sneakfastforward"), ("sneakrun_backward", "sneakfastbackward"),
            ("sneakrun_bckwrdleft", "sneakfastbackward"), ("sneakrun_bckwrdright", "sneakfastbackward"), ("sneakrun_left", "sneakfastleft"), ("sneakrun_right", "sneakfastright") })
            Add(sk + ".hkx", new Source(ob, Loop: true));
        foreach (var (sk, ob) in new[] { ("sneak_turnleft60", "sneakturnleft"), ("sneak_turnleft180", "sneakturnleft"), ("sneak_turnright60", "sneakturnright"), ("sneak_turnright180", "sneakturnright"),
            ("sneak_npcturnleft90", "sneakturnleft"), ("sneak_npcturnright90", "sneakturnright") })
            Add(sk + ".hkx", new Source(ob, Loop: true, Stretch: true));
        // NPC turns on the spot.
        foreach (var (sk, way) in new[] { ("npc_turnleft90", "left"), ("npc_turnleft180", "left"), ("npc_turnright90", "right"), ("npc_turnright180", "right"),
            ("npc_turntowalkleft90", "left"), ("npc_turntowalkleft180", "left"), ("npc_turntowalkright90", "right"), ("npc_turntowalkright180", "right") })
            Add($@"{sex}\{sk}.hkx", new Source("walkturn" + way, Loop: true, Stretch: true));
        // Jumping: Oblivion's take-off, its falling loop, its landing.
        foreach (var way in new[] { "", "left", "right", "fast", "fastleft", "fastright" }) Add($"mt_jump{way}.hkx", new Source("jumpstart"));
        foreach (var way in new[] { "", "left", "right" })
        {
            Add($"mt_jumpfall{way}.hkx", new Source("jumploop", Loop: true));
            Add($"mt_jumpland{way}.hkx", new Source("jumpland"));
        }
        // Skyrim's fidgets standing about as Oblivion's own standing idles of the same kind.
        foreach (var (sk, ob) in ObFidgets(sex)) Add(sk + ".hkx", new Source(ob));
        return table;
    }

    /// <summary>Skyrim's fidgets and the Oblivion idle each is (arms crossed, swatting flies, looking about, sighing,
    /// pondering, the weight shifted).</summary>
    static (string Skyrim, string Oblivion)[] ObFidgets(string sex) =>
    [
        ("mt_idle_a_arms_crossed", @"idleanims\armscrossed_sulking"), ("mt_idle_a_arms_crossedloop", @"idleanims\talk_armscrossed_still"),
        ("mt_idle_a_swat_flies", @"idleanims\specialidle_swattingflys"), ("mt_idle_b_long_left", @"idleanims\ponder01"), ("mt_idle_b_sigh", @"idleanims\exasperated"),
        ("mt_idle_b_sway_right", @"idleanims\talk_handsatside_moving"), ("mt_idle_nervous", @"idleanims\alertedscan"), ("mt_idle_nervous_lookaround", @"idleanims\scan"),
        ("mt_idle_nervous_lookleft", @"idleanims\scan"), ("mt_idle_nervous_lookright", @"idleanims\scan"),
        ($@"{sex}\mt_idle_a_left_long", @"idleanims\ponder02"), ($@"{sex}\mt_idle_a_leg_shift", @"idleanims\talk_handsatside_moving2"), ($@"{sex}\mt_idle_a_pull_pants", @"idleanims\stand1"),
        ($@"{sex}\mt_idle_a_scratch_back", @"idleanims\ponder03"), ($@"{sex}\mt_idle_a_shakeout", @"idleanims\exhausted"), ($@"{sex}\mt_idle_a_shoulders", @"idleanims\talk_handsclasped_sway"),
        ($@"{sex}\mt_idle_a_sigh_var1", @"idleanims\exasperated"), ($@"{sex}\mt_idle_a_sigh_var2", @"idleanims\exasperated"), ($@"{sex}\mt_idle_a_soft_right", @"idleanims\talk_handsclasped_motion"),
        ($@"{sex}\mt_idle_a_sway_fast", @"idleanims\talk_fistsclinched_sway"), ($@"{sex}\mt_idlelookinga", @"idleanims\scan"), ($@"{sex}\mt_idlelookingb", @"idleanims\alertedscan"),
        ($@"{sex}\mt_idlelookingc", @"idleanims\sentrylookout"), ($@"{sex}\mt_idlelookingd", @"idleanims\scan"), ($@"{sex}\mt_idlescratchhead", @"idleanims\ponder01"),
        ($@"{sex}\mt_idlescratchheadright", @"idleanims\ponder02"),
    ];

    /// <summary>The group under an Oblivion group for the bones it leaves alone, one step: a move its weapon's idle
    /// (sneaking or swimming its sneaking or swimming one); a weapon's idle the idle without the weapon; a sneaking or
    /// swimming idle the standing idle; the standing idle none (its bones left alone stay as the skeleton has them:
    /// 2026-10-21, idle.kf keys 44 of the 63 Bip01 bones, onehandidle.kf 27, twohandattackforwardpower.kf 18).</summary>
    public static string? ObBase(string group)
    {
        string g = group.ToLowerInvariant();
        if (g.Contains('\\')) return "idle";
        string mode = g.StartsWith("sneak") ? "sneak" : g.StartsWith("swim") ? "swim" : "";
        string rest = g[mode.Length..];
        string weapon = new[] { "onehand", "twohand", "handtohand", "bow", "staff" }.FirstOrDefault(rest.StartsWith) ?? "";
        if (rest == weapon + "idle")
            return weapon.Length > 0 ? mode + "idle" : mode.Length > 0 ? "idle" : null;
        return mode + weapon + "idle";
    }

    /// <summary>Oblivion's text keys, renamed where Skyrim's events are timed by Morrowind's names for the same moment
    /// (see the class summary); the footfalls as Morrowind's sound keys.</summary>
    static IEnumerable<string> ObKeyLines(string group, string text)
    {
        string t = text.Trim();
        if (t.Equals("Enum: Left", StringComparison.OrdinalIgnoreCase)) { yield return "SoundGen: Left"; yield break; }
        if (t.Equals("Enum: Right", StringComparison.OrdinalIgnoreCase)) { yield return "SoundGen: Right"; yield break; }
        yield return $"{group}: {t}";
        string g = group.ToLowerInvariant();
        string? named = t.ToLowerInvariant() switch
        {
            "attach" when g.Contains("bowattack") => "Shoot Attach",
            "hold" when g.Contains("bowattack") => "Shoot Hold",
            "release" when g.Contains("bowattack") => "Shoot Release",
            "attach" when g.EndsWith("equip") && !g.EndsWith("unequip") => "Equip Attach",
            "detach" when g.EndsWith("unequip") => "Unequip Detach",
            "hit" when g.Contains("cast") => "Release",
            _ => null,
        };
        if (named is not null) yield return $"{group}: {named}";
    }

    static int obTemp;

    /// <summary>
    /// An Oblivion .kf as a group: its sequence's transform tracks by node (Gamebryo 20.0's controlled blocks, named
    /// through the string palette), each from its keyframe data and, for a channel the data has no keys for, the
    /// interpolator's own pose; and its text keys with the group's start, stop and loop.
    /// </summary>
    public static MwCreatureSource.ClipSource ReadObKf(byte[] bytes, string path, string group)
    {
        string tmp = Path.Combine(Path.GetTempPath(), $"tm-obkf-{Environment.ProcessId}-{Interlocked.Increment(ref obTemp)}.kf");
        File.WriteAllBytes(tmp, bytes);
        try
        {
            using var nif = new nifly.NifFile();
            if (nif.Load(tmp) != 0) throw new InvalidDataException($"{path}: nifly can't read it");
            var hdr = nif.GetHeader();
            uint count = hdr.GetNumBlocks();
            nifly.NiControllerSequence? seq = null;
            for (uint i = 0; i < count && seq is null; i++) seq = hdr.GetBlockById(i) as nifly.NiControllerSequence;
            if (seq is null) throw new InvalidDataException($"{path}: no controller sequence");
            float start = seq.startTime, stop = seq.stopTime;
            var tracks = new Dictionary<string, NiKeyframeData>(StringComparer.OrdinalIgnoreCase);
            var splines = new Dictionary<uint, short[]>();
            byte[] palette = [];
            foreach (var link in NiflyRaw.Links(seq))
            {
                uint pal = NiflyRaw.Ref(link.stringPaletteRef).index;
                if (palette.Length == 0 && pal < count && hdr.GetBlockById(pal) is nifly.NiStringPalette sp) palette = NiflyRaw.Palette(bytes, sp);
                string node = (link.nodeName.get() is { Length: > 0 } nn ? nn : NiflyRaw.PaletteString(palette, link.nodeNameOffset)).Trim();
                string type = link.ctrlType.get() is { Length: > 0 } ct ? ct : NiflyRaw.PaletteString(palette, link.controllerTypeOffset);
                uint ii = link.interpolatorRef.index;
                if (type != "NiTransformController" || node.Length == 0 || ii >= count) continue;
                var kd = new NiKeyframeData();
                nifly.Vector3 still; nifly.Quaternion stillTurn;
                switch (hdr.GetBlockById(ii))
                {
                    case nifly.NiTransformInterpolator ti:
                        if (ti.dataRef.index < count && hdr.GetBlockById(ti.dataRef.index) is nifly.NiTransformData td) ReadKeys(td, kd, path, node);
                        (still, stillTurn) = (ti.translation, ti.rotation);
                        break;
                    case nifly.NiBSplineCompTransformInterpolator bi:
                        Spline(hdr, bi, kd, splines, path, node);
                        (still, stillTurn) = (bi.translation, bi.rotation);
                        break;
                    default:
                        throw new InvalidDataException($"{path}: {node} is moved by a {hdr.GetBlockTypeStringById(ii)}, which isn't read");
                }
                // A channel without keys holds the interpolator's pose (Gamebryo marks a channel it doesn't hold with -FLT_MAX).
                static bool Valid(float v) => MathF.Abs(v) < 1e30f;
                if (kd.Translations.Count == 0 && kd.TranslationCurve is null && Valid(still.x) && Valid(still.y) && Valid(still.z))
                {
                    kd.TranslationType = 1;
                    kd.Translations.Add((start, new Vector3(still.x, still.y, still.z)));
                }
                if (kd.Rotations.Count == 0 && kd.RotationCurve is null && Valid(stillTurn.w) && Valid(stillTurn.x) && Valid(stillTurn.y) && Valid(stillTurn.z))
                {
                    kd.RotationType = 1;
                    kd.Rotations.Add((start, Quaternion.Normalize(new Quaternion(stillTurn.x, stillTurn.y, stillTurn.z, stillTurn.w))));
                }
                if (kd.Translations.Count > 0 || kd.Rotations.Count > 0 || kd.TranslationCurve is not null || kd.RotationCurve is not null) tracks[node] = kd;
            }
            // Its text keys under the group, with its start, stop and (looping) loop.
            var keys = new NiTextKeyExtraData();
            var lines = new SortedDictionary<float, List<string>>();
            void Line(float time, string text) { if (!lines.TryGetValue(time, out var l)) lines[time] = l = []; l.Add(text); }
            Line(start, $"{group}: Start");
            Line(stop, $"{group}: Stop");
            if (seq.cycleType == nifly.CycleType.CYCLE_LOOP) { Line(start, $"{group}: Loop Start"); Line(stop, $"{group}: Loop Stop"); }
            if (seq.textKeyRef.index < count && hdr.GetBlockById(seq.textKeyRef.index) is nifly.NiTextKeyExtraData tk)
                foreach (var k in NiflyRaw.TextKeys(tk))
                {
                    string text = k.value.get() ?? "";
                    if (text.Trim().Length == 0 || text.Trim().Equals("Start", StringComparison.OrdinalIgnoreCase) || text.Trim().Equals("End", StringComparison.OrdinalIgnoreCase)) continue;
                    foreach (var l in ObKeyLines(group, text)) Line(k.time, l);
                }
            keys.Keys = lines.Select(kv => (kv.Key, string.Join("\n", kv.Value))).ToList();
            return new MwCreatureSource.ClipSource(path, keys, tracks, MwCreatureSource.Groups(keys));
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
    }

    static float F(IntPtr p, int offset) => BitConverter.Int32BitsToSingle(Marshal.ReadInt32(p, offset));

    /// <summary>
    /// A compressed B-spline interpolator's channels as curves (Oblivion keeps many of its people's bones so: 23 of idle.kf's
    /// 73). Its spline data's short control points (shared by the file's interpolators) hold each channel from its
    /// handle on (65535: none), a point's values one after another (position x, y, z; rotation w, x, y, z), each
    /// bias + short / 32767 x multiplier; the basis data gives the point count. Gamebryo's curve is the cubic B-spline over
    /// clamped uniform knots, run over the interpolator's start to stop, a rotation's values made unit after.
    /// </summary>
    static void Spline(nifly.NiHeader hdr, nifly.NiBSplineCompTransformInterpolator bi, NiKeyframeData kd, Dictionary<uint, short[]> splines, string path, string node)
    {
        uint count = hdr.GetNumBlocks();
        uint dataId = bi.splineDataRef.index, basisId = bi.basisDataRef.index;
        if (dataId >= count || basisId >= count || hdr.GetBlockById(basisId) is not nifly.NiBSplineBasisData basis) throw new InvalidDataException($"{path}: {node}'s B-spline has no data");
        if (!splines.TryGetValue(dataId, out var shorts))
            splines[dataId] = shorts = hdr.GetBlockById(dataId) is nifly.NiBSplineData data ? data.shortControlPoints.items().ToArray() : throw new InvalidDataException($"{path}: {node}'s B-spline data is missing");
        int points = (int)basis.numControlPoints;
        float t0 = bi.startTime, t1 = bi.stopTime;
        const uint None = 65535;
        Func<float, float[]>? Channel(uint handle, int dims, float bias, float multiplier)
        {
            if (handle == None) return null;
            if (points < 2 || handle + (long)points * dims > shorts.Length)
                throw new InvalidDataException($"{path}: {node}'s B-spline channel at {handle} ({points} points of {dims}) runs past its {shorts.Length} values");
            var control = new float[points * dims];
            for (int i = 0; i < control.Length; i++) control[i] = bias + shorts[handle + i] / 32767f * multiplier;
            return t => BSpline(control, points, dims, t1 > t0 ? Math.Clamp((t - t0) / (t1 - t0), 0, 1) : 0);
        }
        if (Channel(bi.translationOffset, 3, bi.translationBias, bi.translationMultiplier) is { } move)
            kd.TranslationCurve = t => { var v = move(t); return new Vector3(v[0], v[1], v[2]); };
        if (Channel(bi.rotationOffset, 4, bi.rotationBias, bi.rotationMultiplier) is { } turn)
        {
            kd.RotationCurve = t => { var v = turn(t); return Quaternion.Normalize(new Quaternion(v[1], v[2], v[3], v[0])); };
            // The decoding's proof (`npc-anims --tracks`): a rotation's control points, decoded right, are near unit length.
            var norms = new float[points];
            for (int i = 0; i < points; i++)
            {
                double sum = 0;
                for (int c = 0; c < 4; c++) { float v = bi.rotationBias + shorts[bi.rotationOffset + i * 4 + c] / 32767f * bi.rotationMultiplier; sum += v * v; }
                norms[i] = (float)Math.Sqrt(sum);
            }
            lock (SplineRotationNorms) SplineRotationNorms.Add(($"{Path.GetFileName(path)} {node} ({points} points, bias {bi.rotationBias:G4}, multiplier {bi.rotationMultiplier:G4})", norms));
        }
    }

    /// <summary>Every B-spline rotation control point's length as decoded (see <see cref="Spline"/>).</summary>
    public static readonly List<(string Channel, float[] Norms)> SplineRotationNorms = [];

    /// <summary>A clamped uniform B-spline (cubic, or lower with fewer than four points) at <paramref name="u"/> (0..1 over
    /// it), by de Boor's algorithm: the knots 0 (degree + 1 times), 1, 2 .. up to points - degree (degree + 1 times).</summary>
    public static float[] BSpline(float[] control, int points, int dims, float u)
    {
        int p = Math.Min(3, points - 1);
        int spans = points - p;
        float Knot(int j) => j <= p ? 0 : j < points ? j - p : spans;
        float x = u * spans;
        // The span: the last knot interval starting at or before x (the end belongs to the last one).
        int k = Math.Clamp((int)MathF.Floor(x) + p, p, points - 1);
        var d = new float[p + 1][];
        for (int j = 0; j <= p; j++) { d[j] = new float[dims]; Array.Copy(control, (j + k - p) * dims, d[j], 0, dims); }
        for (int r = 1; r <= p; r++)
            for (int j = p; j >= r; j--)
            {
                float lo = Knot(j + k - p), hi = Knot(j + 1 + k - r);
                float a = hi > lo ? (x - lo) / (hi - lo) : 0;
                for (int c = 0; c < dims; c++) d[j][c] = (1 - a) * d[j - 1][c] + a * d[j][c];
            }
        return d[p];
    }

    /// <summary>
    /// A transform data's keys into Morrowind's form. nifly keeps each key as type, time, value, forward and backward
    /// tangents, tension/continuity/bias (read from its memory: the wrapper keeps the keys opaque); quaternions stored w,
    /// x, y, z. Rotations by Euler angles (each axis its own float keys, X then Y then Z applied) become quaternion keys
    /// at every frame of the sequence's keys.
    /// </summary>
    static void ReadKeys(nifly.NiTransformData td, NiKeyframeData kd, string path, string node)
    {
        int rotationType = (int)td.rotationType;
        if (rotationType == 4)
        {
            var axes = new[] { td.xRotations, td.yRotations, td.zRotations }.Select(FloatKeys).ToArray();
            var times = axes.SelectMany(a => a.Keys.Select(k => k.Time)).ToList();
            if (times.Count > 0)
            {
                float a0 = times.Min(), a1 = times.Max();
                int frames = Math.Max(1, (int)MathF.Ceiling((a1 - a0) * Fps));
                kd.RotationType = 1;
                for (int f = 0; f <= frames; f++)
                {
                    float t = a0 + (a1 - a0) * f / frames;
                    float Angle(int i) => axes[i].Keys.Count == 0 ? 0 : MwKeys.Float(axes[i].Keys, axes[i].Type, axes[i].Extra, t);
                    var q = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, Angle(2)) * Quaternion.CreateFromAxisAngle(Vector3.UnitY, Angle(1)) * Quaternion.CreateFromAxisAngle(Vector3.UnitX, Angle(0));
                    kd.Rotations.Add((t, Quaternion.Normalize(q)));
                }
            }
        }
        else if (td.quaternionKeys.Count > 0)
        {
            kd.RotationType = rotationType;
            for (int k = 0; k < td.quaternionKeys.Count; k++)
            {
                var p = NiflyRaw.Ptr(td.quaternionKeys[k]);
                var q = new Quaternion(F(p, 12), F(p, 16), F(p, 20), F(p, 8));
                if (q.LengthSquared() < 1e-8f) throw new InvalidDataException($"{path}: {node}'s rotation key {k} is no rotation");
                kd.Rotations.Add((F(p, 4), Quaternion.Normalize(q)));
                if (rotationType == 3) kd.RotationExtra.Add([F(p, 56), F(p, 60), F(p, 64)]);
            }
        }
        uint n = td.translations.GetNumKeys();
        if (n > 0)
        {
            kd.TranslationType = (int)td.translations.GetInterpolationType();
            for (int k = 0; k < n; k++)
            {
                var key = td.translations.GetKey(k);
                var p = NiflyRaw.Ptr(key);
                kd.Translations.Add((F(p, 4), new Vector3(F(p, 8), F(p, 12), F(p, 16))));
                if (kd.TranslationType == 2) kd.TranslationExtra.Add([F(p, 20), F(p, 24), F(p, 28), F(p, 32), F(p, 36), F(p, 40)]);
                else if (kd.TranslationType == 3) kd.TranslationExtra.Add([F(p, 44), F(p, 48), F(p, 52)]);
            }
        }
    }

    /// <summary>A float key group's keys (time, value in X), interpolation and tangents or TBC.</summary>
    static (List<(float Time, float Value)> Keys, int Type, List<float[]> Extra) FloatKeys(nifly.NiAnimationKeyGroupfloat group)
    {
        var keys = new List<(float, float)>();
        var extra = new List<float[]>();
        uint n = group.GetNumKeys();
        int type = n > 0 ? (int)group.GetInterpolationType() : 0;
        for (int k = 0; k < n; k++)
        {
            var key = group.GetKey(k);
            var p = NiflyRaw.Ptr(key);
            keys.Add((F(p, 4), F(p, 8)));
            if (type == 2) extra.Add([F(p, 12), F(p, 16)]);
            else if (type == 3) extra.Add([F(p, 20), F(p, 24), F(p, 28)]);
        }
        return (keys, type, extra);
    }

    /// <summary>
    /// An Oblivion set's rig: its skeleton's nodes (parents first, each with its rest transform; the root's own
    /// transform kept apart, as Morrowind's), and (with <paramref name="animations"/>) the groups its table and their bases
    /// need, each from its .kf.
    /// </summary>
    public static MwRig LoadOblivion(ObData ob, Set set, bool animations = true)
    {
        var rig = new MwRig { ComBone = "Bip01 NonAccum", DefaultBase = ObBase };
        byte[] skeleton = ob.Read(set.MwSkeleton) ?? throw new FileNotFoundException(set.MwSkeleton);
        string tmp = Path.Combine(Path.GetTempPath(), $"tm-obskel-{Environment.ProcessId}-{Interlocked.Increment(ref obTemp)}.nif");
        File.WriteAllBytes(tmp, skeleton);
        try
        {
            using var nif = new nifly.NifFile();
            if (nif.Load(tmp) != 0) throw new InvalidDataException($"{set.MwSkeleton}: nifly can't read it");
            var hdr = nif.GetHeader();
            var root = nif.GetRootNode();
            NifTransform Of(nifly.MatTransform t)
            {
                var m = WornMesh.ToMatrix(t);
                float s = t.scale;
                var rot = s > 1e-6f ? new Matrix4x4(m.M11 / s, m.M12 / s, m.M13 / s, 0, m.M21 / s, m.M22 / s, m.M23 / s, 0, m.M31 / s, m.M32 / s, m.M33 / s, 0, 0, 0, 0, 1) : Matrix4x4.Identity;
                return new NifTransform(Matrix4x4.Transpose(rot), new Vector3(m.M41, m.M42, m.M43), s);
            }
            rig.Root = Of(root.transform);
            void Walk(nifly.NiNode n, int parent)
            {
                for (uint k = 0; k < n.childRefs.GetSize(); k++)
                {
                    uint id = n.childRefs.GetBlockRef(k);
                    if (id >= hdr.GetNumBlocks() || hdr.GetBlockById(id) is not nifly.NiNode child) continue;
                    string name = (child.name.get() ?? "").Trim();
                    var own = Of(child.transform);
                    var local = parent < 0 ? own.Then(rig.Root) : own;
                    rig.Names.Add(name);
                    rig.Parents.Add(parent);
                    rig.Rest.Add((local.Translation, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(local.Rotation)))));
                    Walk(child, rig.Names.Count - 1);
                }
            }
            Walk(root, -1);
        }
        finally { try { File.Delete(tmp); } catch (IOException) { } }
        if (!animations) return rig;
        // The groups: each the table names, and those under each.
        var groups = new List<string>();
        void Need(string? g)
        {
            for (; g is not null && !groups.Contains(g, StringComparer.OrdinalIgnoreCase); g = ObBase(g)) groups.Add(g);
        }
        foreach (var (_, s) in ObTable(set.Female, _ => true)) { Need(s.Group); Need(s.Base); }
        foreach (var g in groups)
            if (ob.Read(ObFolder + g + ".kf") is { } bytes)
                rig.Sources.Add(ReadObKf(bytes, ObFolder + g + ".kf", g));
        return rig;
    }
}
