using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using HKX2;
using TamrielMerge.Morrowind;
using TamrielMerge.Skyrim.Creatures;
using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Skyrim.Animations;

/// <summary>
/// People moving as their own game's do (user, 2026-10-21: "all bones and animations need fixed across games", "npcs
/// too", "fix the npcs"): a converted game's NPCs are Skyrim actors on Skyrim's skeleton (their worn armour already
/// re-rigged onto it: WornMesh), so their own game's humanoid animations are retargeted onto that skeleton and replace
/// Skyrim's character animations for them alone, through Open Animation Replacer (allowed, credited: user 2026-10-17).
/// <list type="bullet">
/// <item>Morrowind's sets as its engine loads them (OpenMW 0.48 NpcAnimation::updateNpcBase: xbase_anim, then the
/// default skeleton's own - base_anim_female's for a woman, base_animKnA's for the beast races, xargonian_swimkna's for an
/// Argonian - each later one winning for the groups it has), each on its skeleton (base_anim.nif, base_anim_female.nif,
/// base_animKnA.nif) onto Skyrim's male or female skeleton.</item>
/// <item>Retargeting (<see cref="Retarget"/>): each Biped bone's anatomical frame (along the bone to the next, square to
/// the body's forward: WornMesh.Frame, as the armour re-rigging lines the bones up) is copied onto the Skyrim bone it
/// becomes, so every limb points as Morrowind's does whatever the two rigs' axes or bind poses; a bone with no next
/// carries its turn from the bone above; "Bip01" drives Skyrim's centre of mass, its height scaled by the two skeletons'
/// and its travel taken out (Skyrim moves the actor); the rest of Skyrim's bones keep their place on their parents.</item>
/// <item>Each Skyrim animation file its Morrowind group or part (<see cref="Table"/>), its events Skyrim's behaviour
/// expects (<see cref="VanillaEvents"/>: the character project's clip data and the file's own) at Morrowind's moments
/// where Morrowind has them (the blow at "Hit", the draw at "Equip Attach", a spell's release, footfalls), else at the
/// same share of the clip, written into the file and used alone (OAR's triggersFromAnnotationsOnly, with Skyrim's flag
/// against reading annotations lifted).</item>
/// <item>OAR submods under the TamrielMerge mod: one per set, on the NPCs the converted game marks with its keyword
/// (and female for the women's sets).</item>
/// </list>
/// `npc-anims` writes them, `npc-anim-check` reads them back against Morrowind bone by bone.
/// </summary>
public static partial class NpcAnimations
{
    public const float Fps = 30;
    const string AnimationsFolder = @"meshes\actors\character\animations\";
    const string MaleSkeleton = @"meshes\actors\character\character assets\skeleton.hkx";
    const string FemaleSkeleton = @"meshes\actors\character\character assets female\skeleton_female.hkx";

    /// <summary>A set of a game's people's animations: its skeleton, its .kf files (later ones win), the Skyrim skeleton it
    /// goes onto, the OAR submod it is, and the game it comes from (which table of files it fills).</summary>
    public sealed record Set(string Key, string Description, string MwSkeleton, string[] Kfs, bool Female, string Keyword, string Game = "Morrowind");

    /// <summary>Morrowind's: men, women, the beast races (Khajiit and Argonian men and women alike: their default skeleton is
    /// base_animKnA whatever their sex), Argonians (their swimming over the beast set).</summary>
    public static readonly Set[] Morrowind =
    [
        new("MorrowindMen", "Morrowind's men (xbase_anim)", @"meshes\base_anim.nif", [@"meshes\xbase_anim.kf"], false, "TMMwPersonMan"),
        new("MorrowindWomen", "Morrowind's women (xbase_anim, xbase_anim_female over it)", @"meshes\base_anim_female.nif", [@"meshes\xbase_anim_female.kf", @"meshes\xbase_anim.kf"], true, "TMMwPersonWoman"),
        new("MorrowindBeastMen", "Morrowind's Khajiit and Argonian men (xbase_anim, xbase_animKnA over it)", @"meshes\base_animKnA.nif", [@"meshes\xbase_animKnA.kf", @"meshes\xbase_anim.kf"], false, "TMMwBeastMan"),
        new("MorrowindBeastWomen", "Morrowind's Khajiit and Argonian women (xbase_anim, xbase_animKnA over it)", @"meshes\base_animKnA.nif", [@"meshes\xbase_animKnA.kf", @"meshes\xbase_anim.kf"], true, "TMMwBeastWoman"),
        new("MorrowindArgonianMen", "Morrowind's Argonian men swimming (xargonian_swimkna over the beast set)", @"meshes\base_animKnA.nif", [@"meshes\xargonian_swimkna.kf", @"meshes\xbase_animKnA.kf", @"meshes\xbase_anim.kf"], false, "TMMwArgonianMan"),
        new("MorrowindArgonianWomen", "Morrowind's Argonian women swimming (xargonian_swimkna over the beast set)", @"meshes\base_animKnA.nif", [@"meshes\xargonian_swimkna.kf", @"meshes\xbase_animKnA.kf", @"meshes\xbase_anim.kf"], true, "TMMwArgonianWoman"),
    ];

    // ---- what each Skyrim file is ----

    /// <summary>Where a Skyrim file's motion comes from: a Morrowind group (from its "Start" to its "Stop", or its loop
    /// part), or a part of one between two of its keys ("Slash Start" .. "Slash Medium Follow Stop"); <c>Hold</c> one frame
    /// of it held (a pose waited in); <c>Stretch</c> its loop repeated to the Skyrim file's own length (a canned turn,
    /// whose turning the engine does over that time); <c>Base</c> the group under it for the bones it leaves alone (an
    /// Oblivion attack over the walk it is made while walking; else the rig's own rule).</summary>
    public sealed record Source(string Group, string? From = null, string? To = null, bool Loop = false, bool Hold = false, bool Stretch = false, string? Base = null);

    /// <summary>The Morrowind group letters of a Skyrim animation family: its weapon's (Morrowind's 1h for Skyrim's one-handed,
    /// 2c for both two-handed kinds - greatswords, battleaxes and warhammers are all two-handed close weapons in Morrowind -
    /// hh for fists, the one-handed moves for a bow, none for a spell or nothing drawn) and its attack group.</summary>
    static readonly (string Family, string Moves, string? Attacks)[] Families =
    [
        ("mt", "", null), ("1hm", "1h", "WeaponOneHand"), ("2hm", "2c", "WeaponTwoHand"), ("2hw", "2c", "WeaponTwoHand"),
        ("h2h", "hh", "HandToHand"), ("bow", "1h", null), ("mag", "", null), ("magcast", "", null),
    ];

    /// <summary>
    /// Every Skyrim character animation a Morrowind group stands for, by file (relative to the animations folder; the
    /// unarmed moves and idle in male\ and female\): standing, walking, running and sprinting every way (Skyrim's
    /// diagonals are Morrowind's forward or back: it strafes only past 63.4°), turning, drawing and putting away, each
    /// attack (Skyrim's right swing Morrowind's slash, left its chop, power attacks its thrust and the large follow-through,
    /// their moving and sneaking kinds the same blows), blocking, recoiling and staggering (its hits), bleeding out (knocked
    /// out), dying, casting (its SpellCast's target part; self for Skyrim's self casts), the bow, swimming, sneaking,
    /// jumping, the torch, and Skyrim's idle fidgets (Morrowind's Idle2..9 in turn).
    /// </summary>
    public static List<(string File, Source Source)> Table(bool female, Func<string, bool> exists)
    {
        var table = new List<(string, Source)>();
        void Add(string file, Source s) { if (exists(file)) table.Add((file, s)); }
        string sex = female ? "female" : "male";
        foreach (var (fam, s, attacks) in Families)
        {
            string P(string name) => fam == "mt" ? $@"{sex}\mt_{name}.hkx" : $"{fam}_{name}.hkx";
            string idle = fam switch { "mag" or "magcast" => "IdleSpell", "bow" => "Idle1h", _ => "Idle" + s };
            Add(P("idle"), new Source(idle, Loop: true));
            foreach (var (sk, mw) in new[] { ("walkforward", "WalkForward"), ("walkforwardleft", "WalkForward"), ("walkforwardright", "WalkForward"), ("walkforwrdleft", "WalkForward"), ("walkfrwrdright", "WalkForward"),
                ("walkbackward", "WalkBack"), ("walkbackwardleft", "WalkBack"), ("walkbackwardright", "WalkBack"), ("walkbckwrdleft", "WalkBack"), ("walkbckwrdrht", "WalkBack"),
                ("walkleft", "WalkLeft"), ("walkright", "WalkRight"),
                ("runforward", "RunForward"), ("runforwardleft", "RunForward"), ("runforwardright", "RunForward"), ("runfrwrdright", "RunForward"), ("sprintforward", "RunForward"), ("sprintforwardsword", "RunForward"), ("sprint", "RunForward"),
                ("runbackward", "RunBack"), ("runbackwardleft", "RunBack"), ("runbackwardright", "RunBack"), ("runbackwrdleft", "RunBack"), ("runbckwrdright", "RunBack"),
                ("runleft", "RunLeft"), ("runright", "RunRight"), ("runstrafeleft", "RunLeft"), ("runstraferight", "RunRight") })
                Add(P(sk), new Source(mw + s, Loop: true));
            foreach (var (sk, mw) in new[] { ("turnleft60", "TurnLeft"), ("turnleft180", "TurnLeft"), ("turnleft360", "TurnLeft"), ("turnright60", "TurnRight"), ("turnright180", "TurnRight"), ("turnright360", "TurnRight") })
                Add(fam == "mt" ? $"mt_{sk}.hkx" : P(sk), new Source(fam is "mag" or "magcast" ? "Spell" + mw : mw + s, Loop: true, Stretch: true));
            if (attacks is not null)
            {
                Add(P("equip"), new Source(attacks, "Equip Start", "Equip Stop"));
                Add(P("unequip"), new Source(attacks, "Unequip Start", "Unequip Stop"));
                Source Blow(string kind, string follow) => new(attacks, $"{kind} Start", $"{kind} {follow} Follow Stop");
                Add(P("attackright"), Blow("Slash", "Medium"));
                Add(P("attackleft"), Blow("Chop", "Medium"));
                Add(P("attackrightintro"), new Source(attacks, "Slash Start", "Slash Max Attack"));
                Add(P("attackleftintro"), new Source(attacks, "Chop Start", "Chop Max Attack"));
                foreach (var power in new[] { "attackpower", "attackpowerfwd", "attackpowerforward", "attackpowerforwardsprint", "attackforwardsprint", "attackrightpowerforward",
                    "attackpowerforwardlefthand", "attackpowerforwardrighthand", "attackpower3slashcombo" })
                    Add(P(power), Blow("Thrust", power == "attackforwardsprint" ? "Medium" : "Large"));
                Add(P("attackpowerbwd"), Blow("Chop", "Large"));
                Add(P("attackpowerleft"), Blow("Slash", "Large"));
                Add(P("attackpowerright"), Blow("Slash", "Large"));
                foreach (var move in new[] { "walkfwd", "walkbwd", "walkleft", "walkright", "walkrt", "runfwd", "runbwd", "runleft", "runright", "runforward", "runback", "walkforward", "walkback" })
                    foreach (var (sk, kind) in new[] { ("attackright", "Slash"), ("attackrt", "Slash"), ("attackleft", "Chop") })
                    {
                        Add(P(move + sk), Blow(kind, "Medium"));
                        Add(P(move + sk + "intro"), new Source(attacks, $"{kind} Start", $"{kind} Max Attack"));
                    }
                // Fists' jabs, crosses and hooks: Morrowind's three blows.
                foreach (var (sk, kind) in new[] { ("jab", "Thrust"), ("cross", "Slash"), ("leftbodyhook", "Chop"), ("rightbodyhook", "Chop") })
                {
                    Add(P(sk), Blow(kind, "Medium"));
                    foreach (var move in new[] { "walkforward", "walkback", "walkleft", "walkright", "runforward", "runback", "runleft", "runright" }) Add(P(move + sk), Blow(kind, "Medium"));
                }
                foreach (var sneakFile in fam switch { "1hm" => new[] { "sneak_1hmattack.hkx", "sneak_1hmattackintro.hkx", "sneak_1hmattacklefthand.hkx", "sneak_1hmattackpowerlefthand.hkx", "1hm_sneakattackleft.hkx", "1hm_sneakattackright.hkx", "1hm_sneakattackpower.hkx" },
                    "2hm" => ["sneak_2hmattack.hkx"], "2hw" => ["sneak_2hwattack.hkx"], "h2h" => ["sneak_h2hattacklefthand.hkx", "sneak_h2hattackrighthand.hkx", "sneak_beasth2hattacklefthand.hkx", "sneak_beasth2hattackrighthand.hkx"], _ => [] })
                    Add(sneakFile, sneakFile.Contains("intro") ? new Source(attacks, "Slash Start", "Slash Max Attack") : Blow(sneakFile.Contains("power") ? "Thrust" : "Slash", sneakFile.Contains("power") ? "Large" : "Medium"));
                // Blocking with it (Morrowind blocks with a shield alone, raising it as a blow lands: its Shield group).
                Add(P("blockhit"), new Source("Shield", "Block Start", "Block Stop"));
                Add(P("blockhita"), new Source("Shield", "Block Start", "Block Stop"));
                Add(P("blockhitb"), new Source("Shield", "Block Start", "Block Stop"));
                Add(P("blockanticipate"), new Source("Shield", "Block Start", "Block Hit"));
                Add(P("blockidle"), new Source("Shield", "Block Hit", "Block Hit", Hold: true));
                // Struck: recoiling and staggering are its hits.
                foreach (var (sk, hit) in new[] { ("recoilleft", "Hit1"), ("recoilright", "Hit2"), ("recoiltimed", "Hit3"), ("staggerbacksmallest", "Hit1"), ("staggerbacksmall", "Hit2"),
                    ("staggerbackmedium", "Hit3"), ("staggerbacklarge", "Hit4"), ("staggerbacklargest", "Hit5"), ("staggerforwardsmall", "Hit2"), ("staggerforwardmedium", "Hit3"),
                    ("staggerforwardlarge", "Hit4"), ("staggerforwardlargest", "Hit5"), ("staggeridle", "Hit1") })
                    Add(P(sk), new Source(hit));
            }
        }
        // The bow: drawn, held drawn, loosed, put up and away (Morrowind's BowAndArrow shot).
        Add("bow_equip.hkx", new Source("BowAndArrow", "Equip Start", "Equip Stop"));
        Add("bow_unequip.hkx", new Source("BowAndArrow", "Unequip Start", "Unequip Stop"));
        foreach (var draw in new[] { "bow_drawlight.hkx", "bow_drawheavy.hkx", "sneakbow_drawlight.hkx" }) Add(draw, new Source("BowAndArrow", "Shoot Start", "Shoot Max Attack"));
        foreach (var held in new[] { "bow_idledrawn.hkx", "bow_idleheld.hkx", "sneakbow_idledrawn.hkx" }) Add(held, new Source("BowAndArrow", "Shoot Max Attack", "Shoot Max Attack", Hold: true));
        foreach (var release in new[] { "bow_release.hkx", "sneakbow_release.hkx" }) Add(release, new Source("BowAndArrow", "Shoot Max Attack", "Shoot Follow Stop"));
        // Magic: readying, the cast up to the moment the spell goes and that pose held while Skyrim charges it, the spell
        // going, putting it away; Skyrim's self casts are Morrowind's self cast, the rest its target cast.
        foreach (var hand in new[] { "mlh", "mrh" })
        {
            Add($"{hand}_equip.hkx", new Source("SpellCast", "Equip Start", "Equip Stop"));
            Add($"{hand}_unequip.hkx", new Source("SpellCast", "Unequip Start", "Unequip Stop"));
            Add($"{hand}_idle.hkx", new Source("IdleSpell", Loop: true));
            foreach (var (sk, range) in new[] { ("precharge", "Target"), ("selfprecharge", "Self"), ("preready", "Target"), ("prewardcharge", "Target"), ("preaimedcon", "Target"), ("preselfcon", "Self"), ("pretelekinesis", "Target") })
                Add($"{hand}_{sk}.hkx", new Source("SpellCast", $"{range} Start", $"{range} Release"));
            foreach (var (sk, range) in new[] { ("chargeloop", "Target"), ("readyloop", "Target"), ("selfchargeloop", "Self"), ("selfreadyloop", "Self"), ("wardcharge", "Target"), ("wardloop", "Target"),
                ("prewardloop", "Target"), ("aimedconcentration", "Target"), ("selfconcentration", "Self"), ("telekinesisloop", "Target") })
                Add($"{hand}_{sk}.hkx", new Source("SpellCast", $"{range} Release", $"{range} Release", Hold: true));
            Add($"{hand}_release.hkx", new Source("SpellCast", "Target Release", "Target Stop"));
            Add($"{hand}_selfrelease.hkx", new Source("SpellCast", "Self Release", "Self Stop"));
        }
        Add("mrh_and_mlh_equip.hkx", new Source("SpellCast", "Equip Start", "Equip Stop"));
        Add("mrh_and_mlh_unequip.hkx", new Source("SpellCast", "Unequip Start", "Unequip Stop"));
        // Shield and torch.
        foreach (var block in new[] { "shd_blockhit.hkx", "shd_blockhit_vara.hkx", "shd_blockhit_varb.hkx", "shd_blocktimed.hkx", "tor_blockhit.hkx", "tor_blockhitmt.hkx", "bow_blockhit.hkx" })
            Add(block, new Source("Shield", "Block Start", "Block Stop"));
        foreach (var block in new[] { "shd_blockanticipate.hkx", "tor_blockanticipate.hkx", "tor_blockanticipatemt.hkx", "bow_blockanticipate.hkx" }) Add(block, new Source("Shield", "Block Start", "Block Hit"));
        foreach (var block in new[] { "shd_blockidle.hkx", "tor_blockidle.hkx", "bow_blockidle.hkx" }) Add(block, new Source("Shield", "Block Hit", "Block Hit", Hold: true));
        foreach (var torch in new[] { "tor_idle.hkx", "tor_mtpose.hkx", "tor_1hmpose.hkx", "tor_mrhpose.hkx" }) Add(torch, new Source("Torch", Loop: true));
        // Knocked out (Skyrim's bleeding out: down, staying down, getting up), and dying.
        Add("bleedout_idle.hkx", new Source("Knockout", Loop: true));
        Add("bleedout_transout.hkx", new Source("Knockout", "Loop Stop", "Stop"));
        foreach (var down in new[] { "bleedout_forward.hkx", "bleedout_backward.hkx", "bleedout_left.hkx", "bleedout_right.hkx" }) Add(down, new Source("Knockout", "Start", "Loop Start"));
        Add("deathanimationa.hkx", new Source("Death1"));
        // Swimming.
        Add("swimidle.hkx", new Source("IdleSwim", Loop: true));
        Add("swimforward.hkx", new Source("SwimRunForward", Loop: true));
        Add("swimbackward.hkx", new Source("SwimWalkBack", Loop: true));
        Add("swimleft.hkx", new Source("SwimWalkLeft", Loop: true));
        Add("swimright.hkx", new Source("SwimWalkRight", Loop: true));
        // Sneaking (Morrowind has no sneaking run: its sneak at Skyrim's).
        Add("sneakmtidle.hkx", new Source("IdleSneak", Loop: true));
        Add("sneak1hm_idle.hkx", new Source("IdleSneak", Loop: true));
        Add("sneakh2h_idle.hkx", new Source("IdleSneak", Loop: true));
        foreach (var (sk, mw) in new[] { ("sneakwalk_forward", "SneakForward"), ("sneakwalk_frwrdright", "SneakForward"), ("sneakwalk_fwrwrdleft", "SneakForward"), ("sneakwalk_bckward", "SneakBack"),
            ("sneakwalk_bckwrdleft", "SneakBack"), ("sneakwalk_bckwrdright", "SneakBack"), ("sneakwalk_left", "SneakLeft"), ("sneakwalk_right", "SneakRight"),
            ("sneakrun_forward", "SneakForward"), ("sneakrun_frwrdleft", "SneakForward"), ("sneakrun_frwrdright", "SneakForward"), ("sneakrun_backward", "SneakBack"),
            ("sneakrun_bckwrdleft", "SneakBack"), ("sneakrun_bckwrdright", "SneakBack"), ("sneakrun_left", "SneakLeft"), ("sneakrun_right", "SneakRight") })
            Add(sk + ".hkx", new Source(mw, Loop: true));
        foreach (var (sk, mw) in new[] { ("sneak_turnleft60", "TurnLeft"), ("sneak_turnleft180", "TurnLeft"), ("sneak_turnright60", "TurnRight"), ("sneak_turnright180", "TurnRight"), ("sneak_npcturnleft90", "TurnLeft"), ("sneak_npcturnright90", "TurnRight") })
            Add(sk + ".hkx", new Source(mw, Loop: true, Stretch: true));
        // NPC turns on the spot (male\ and female\).
        foreach (var (sk, mw) in new[] { ("npc_turnleft90", "TurnLeft"), ("npc_turnleft180", "TurnLeft"), ("npc_turnright90", "TurnRight"), ("npc_turnright180", "TurnRight"),
            ("npc_turntowalkleft90", "TurnLeft"), ("npc_turntowalkleft180", "TurnLeft"), ("npc_turntowalkright90", "TurnRight"), ("npc_turntowalkright180", "TurnRight") })
            Add($@"{sex}\{sk}.hkx", new Source(mw, Loop: true, Stretch: true));
        // Jumping: up to the top, the top held while falling, landing.
        foreach (var way in new[] { "", "left", "right", "fast", "fastleft", "fastright" }) Add($"mt_jump{way}.hkx", new Source("Jump", "Start", "Loop Start"));
        foreach (var way in new[] { "", "left", "right" })
        {
            Add($"mt_jumpfall{way}.hkx", new Source("Jump", "Loop Start", "Loop Start", Hold: true));
            Add($"mt_jumpland{way}.hkx", new Source("Jump", "Loop Stop", "Stop"));
        }
        // Skyrim's fidgets standing about, each one of Morrowind's (Idle2..9 in turn).
        var fidgets = new[] { "mt_idle_a_arms_crossed", "mt_idle_a_arms_crossedloop", "mt_idle_a_swat_flies", "mt_idle_b_long_left", "mt_idle_b_sigh", "mt_idle_b_sway_right", "mt_idle_nervous",
            "mt_idle_nervous_lookaround", "mt_idle_nervous_lookleft", "mt_idle_nervous_lookright", $@"{sex}\mt_idle_a_left_long", $@"{sex}\mt_idle_a_leg_shift", $@"{sex}\mt_idle_a_pull_pants",
            $@"{sex}\mt_idle_a_scratch_back", $@"{sex}\mt_idle_a_shakeout", $@"{sex}\mt_idle_a_shoulders", $@"{sex}\mt_idle_a_sigh_var1", $@"{sex}\mt_idle_a_sigh_var2", $@"{sex}\mt_idle_a_soft_right",
            $@"{sex}\mt_idle_a_sway_fast", $@"{sex}\mt_idlelookinga", $@"{sex}\mt_idlelookingb", $@"{sex}\mt_idlelookingc", $@"{sex}\mt_idlelookingd", $@"{sex}\mt_idlescratchhead", $@"{sex}\mt_idlescratchheadright" };
        for (int i = 0; i < fidgets.Length; i++) Add(fidgets[i] + ".hkx", new Source($"Idle{2 + i % 8}"));
        return table;
    }

    // ---- Skyrim's side ----

    /// <summary>Skyrim's skeleton as its .hkx has it, with each bone's bind pose in model space.</summary>
    public sealed class SkyrimRig
    {
        public required ClimbAnimation.Skeleton Skeleton;
        public Vector3[] WorldT = [];
        public Quaternion[] WorldQ = [];
        public int Index(string name) => Array.FindIndex(Skeleton.Bones, b => b.Equals(name, StringComparison.OrdinalIgnoreCase));

        public static SkyrimRig From(ClimbAnimation.Skeleton s)
        {
            var r = new SkyrimRig { Skeleton = s, WorldT = new Vector3[s.Bones.Length], WorldQ = new Quaternion[s.Bones.Length] };
            for (int b = 0; b < s.Bones.Length; b++)
            {
                int p = s.Parents[b];
                r.WorldT[b] = p < 0 ? s.T[b] : r.WorldT[p] + Vector3.Transform(s.T[b], r.WorldQ[p]);
                r.WorldQ[b] = p < 0 ? s.Q[b] : Quaternion.Normalize(r.WorldQ[p] * s.Q[b]);
            }
            return r;
        }

        /// <summary>As a bind skeleton by name (WornMesh's bone mapping reads its names).</summary>
        public BindSkeleton Bind()
        {
            var b = new BindSkeleton();
            for (int i = 0; i < Skeleton.Bones.Length; i++)
            {
                b.World[Skeleton.Bones[i]] = Matrix4x4.CreateFromQuaternion(WorldQ[i]) * Matrix4x4.CreateTranslation(WorldT[i]);
                if (Skeleton.Parents[i] >= 0) b.Parent[Skeleton.Bones[i]] = Skeleton.Bones[Skeleton.Parents[i]];
            }
            return b;
        }
    }

    /// <summary>The clip data's clips no behaviour of the project plays (by name), from the last <see cref="VanillaEvents"/>.</summary>
    public static List<string> UnplayedClips { get; private set; } = [];

    /// <summary>Each file's clips an NPC plays and the events each fires, from the last <see cref="VanillaEvents"/>.</summary>
    public static Dictionary<string, List<(string Clip, List<(float Time, string Text)> Events)>> ClipsByFile { get; private set; } = [];

    /// <summary>
    /// Each Skyrim character animation's events, by file (relative to the animations folder), as Skyrim fires them: the
    /// character project's clip data gives each clip's events by the clip's name (its number there only ties it to its
    /// motion data: 2026-10-21, clip 965 is MT_WalkForward while the character's 965th file is MLh_WardCharge), and the
    /// clip generator of that name in the project's behaviours gives the file it plays. A file several clips play gets the
    /// events most of them have (MT_WalkForward's footfalls, not the dismount sound of the Dismount clip that also plays
    /// it: one replacement file serves them all), and the file's own annotations where a clip playing it takes them as
    /// events (its "don't convert annotations to triggers" flag, 0x10, off).
    /// </summary>
    public static Dictionary<string, (float Duration, List<(float Time, string Text)> Events, bool Shared, float LastEvent)> VanillaEvents(CreatureBehavior archives, bool female)
    {
        string project = female ? "DefaultFemale" : "DefaultMale";
        var data = archives.ProjectData(project)?.Data ?? throw new InvalidDataException($"no {project} animation data");
        // The data: "1", the file count, the files (the project's behaviours among them), the motion flag, then per clip:
        // name, number, speed, two crops, its event count, its events ("text:time"), a blank line.
        int files = int.Parse(data[1]);
        var clips = new Dictionary<string, (string File, bool Annotations)>(StringComparer.OrdinalIgnoreCase);
        foreach (var behavior in data.Skip(2).Take(files).Where(f => f.StartsWith(@"Behaviors\", StringComparison.OrdinalIgnoreCase)))
        {
            var bytes = archives.ReadArchive(@"meshes\actors\character\" + behavior) ?? throw new FileNotFoundException(behavior);
            CreatureBehavior.Visit(CreatureBehavior.Hkx(bytes), o =>
            {
                if (o is hkbClipGenerator c && c.m_animationName is { Length: > 0 } a)
                    clips.TryAdd(c.m_name, (Regex.Replace(a.Replace('/', '\\'), @"^Animations\\", "", RegexOptions.IgnoreCase).ToLowerInvariant(), (c.m_flags & 0x10) == 0));
            });
        }
        var dataEvents = new Dictionary<string, List<(float Time, string Text)>>(StringComparer.OrdinalIgnoreCase);
        var unplayed = new List<string>();
        var played = new Dictionary<string, List<(string Clip, List<(float Time, string Text)> Events)>>(StringComparer.OrdinalIgnoreCase);
        int i = 2 + files + 1;
        while (i + 5 < data.Count)
        {
            if (!int.TryParse(data[i + 1], out _) || !int.TryParse(data[i + 5], out int count)) { i++; continue; }
            string name = data[i];
            var events = new List<(float, string)>();
            for (int e = 0; e < count && i + 6 + e < data.Count; e++)
            {
                string line = data[i + 6 + e];
                int colon = line.LastIndexOf(':');
                if (colon > 0 && float.TryParse(line[(colon + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float t))
                    events.Add((t, line[..colon]));
            }
            if (clips.TryGetValue(name, out var clip))
            {
                dataEvents[name] = events;
            }
            else unplayed.Add(name);
            i += 6 + count;
            while (i < data.Count && data[i].Length == 0) i++;
        }
        UnplayedClips = unplayed;
        ClipsByFile = played;
        var result = new Dictionary<string, (float, List<(float, string)>, bool, float)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in clips.Values.Select(c => c.File).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var bytes = archives.ReadArchive(AnimationsFolder + file);
            if (bytes is null) continue;
            var anim = CreatureBehavior.Hkx(bytes).m_namedVariants.Select(v => v?.m_variant).OfType<hkaAnimationContainer>().FirstOrDefault()?.m_animations.FirstOrDefault();
            if (anim is null) continue;
            var annotations = anim.m_annotationTracks.SelectMany(t => t.m_annotations).Select(a => (a.m_time, a.m_text)).ToList();
            // Each clip playing it (first-person ones aside: no NPC plays them) and the events it fires: its clip data's,
            // and the file's annotations where it takes them.
            // A clip with an NPC twin ("2HM_AttackRight" and "2HM_AttackRightNPC", 2026-10-21: its blow at 0.2 s, the twin's
            // at 0.8) is the player's alone: the people these files go on are never the player.
            var lists = clips.Where(c => c.Value.File.Equals(file, StringComparison.OrdinalIgnoreCase) && !FirstPerson(c.Key)
                && !clips.ContainsKey(c.Key + "NPC") && !clips.ContainsKey(c.Key + "_NPC")).Select(c =>
            {
                var events = new List<(float Time, string Text)>(dataEvents.GetValueOrDefault(c.Key, []));
                if (c.Value.Annotations)
                    foreach (var a in annotations) if (!events.Any(e => e.Text == a.m_text && MathF.Abs(e.Time - a.m_time) < 1e-3f)) events.Add(a);
                var sorted = events.OrderBy(e => e.Time).ThenBy(e => e.Text, StringComparer.Ordinal).ToList();
                if (!played.TryGetValue(file, out var by)) played[file] = by = [];
                by.Add((c.Key + (c.Value.Annotations ? " (+ its file's annotations)" : ""), sorted));
                return sorted;
            }).ToList();
            // Clips firing the same events (at their own times: a fast run's second footfall at 0.063 s, the run's at 0.333;
            // and the fast run's right foot twice, its clip data's and its file's annotation) are one kind: placed by what
            // they are (Retime), their times and repeats don't matter.
            // A sound only some of them play is left out (a dual draw's second hand: 1HM_Dual_Equip's
            // SoundPlay.WPNLeftHandDraw), heard wrongly in the rest otherwise and nothing the clip waits on.
            static bool Sound(string text) => text.StartsWith("SoundPlay", StringComparison.OrdinalIgnoreCase) || text.StartsWith("SoundStop", StringComparison.OrdinalIgnoreCase);
            var everyClip = lists.Count == 0 ? [] : lists.Select(l => l.Select(e => e.Text).ToHashSet()).Aggregate((a, b) => { a.IntersectWith(b); return a; });
            lists = lists.Select(l => l.Where(e => !Sound(e.Text) || everyClip.Contains(e.Text)).ToList()).ToList();
            var kinds = lists.GroupBy(l => string.Join("|", l.Select(e => e.Text).Distinct().Order(StringComparer.Ordinal))).OrderByDescending(g => g.Count()).ThenByDescending(g => g.First().Count).ToList();
            // One kind all its clips fire: it is the file's, retimed into it; else each clip keeps its own (see Write).
            result[file] = (anim.m_duration, kinds.Count > 0 ? kinds[0].First() : [], kinds.Count <= 1, lists.SelectMany(l => l).Select(e => e.Time).DefaultIfEmpty(0).Max());
        }
        return result;
    }

    /// <summary>A string as a JSON string literal (a description's "characters\_male" is no valid escape as it stands).</summary>
    static string Json(string s) => System.Text.Json.JsonSerializer.Serialize(s);

    /// <summary>A first-person clip (the player's view alone): "1HM_1stPAttackRight", "1stPerson...".</summary>
    static bool FirstPerson(string clip) => clip.Contains("1stP", StringComparison.OrdinalIgnoreCase);

    // ---- Morrowind's side ----

    /// <summary>A Morrowind person's skeleton (its .nif's nodes, parents first) and the sources its groups come from (the
    /// set's .kf files, the first with a group winning).</summary>
    public sealed class MwRig
    {
        public List<string> Names = [];
        public List<int> Parents = [];
        public List<(Vector3 T, Quaternion Q)> Rest = [];
        public List<MwCreatureSource.ClipSource> Sources = [];
        /// <summary>The model root's own transform (a track on a node right under it is relative to it).</summary>
        public NifTransform Root = NifTransform.Identity;
        /// <summary>The node driving Skyrim's centre of mass: Morrowind's "Bip01"; Oblivion's "Bip01 NonAccum" under it
        /// (Oblivion's "Bip01" carries the travel alone, the body's sway and bob are on the node below).</summary>
        public string ComBone = "Bip01";
        public int Index(string name) => Names.FindIndex(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));

        public static MwRig Load(Tes3Data data, Set set)
        {
            var nif = Nif4.Read(data.Read(set.MwSkeleton) ?? throw new FileNotFoundException(set.MwSkeleton), set.MwSkeleton);
            var rig = new MwRig();
            var root = nif.Root as NiNode ?? throw new InvalidDataException($"{set.MwSkeleton}: no root");
            rig.Root = root.Transform;
            void Walk(NiNode n, int parent)
            {
                foreach (int c in n.Children)
                    if (nif.Get<NiNode>(c) is { } child && child is not NiTriShape && child.Type != "RootCollisionNode")
                    {
                        rig.Names.Add(child.Name.Trim());
                        rig.Parents.Add(parent);
                        var local = parent < 0 ? child.Transform.Then(root.Transform) : child.Transform;
                        rig.Rest.Add((local.Translation, Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(local.Rotation)))));
                        Walk(child, rig.Names.Count - 1);
                    }
            }
            Walk(root, -1);
            foreach (var kf in set.Kfs)
                if (data.Read(kf) is { } bytes)
                {
                    var (keys, tracks) = MwCreatureSource.ReadKf(Nif4.Read(bytes, kf));
                    rig.Sources.Add(new MwCreatureSource.ClipSource(kf, keys, tracks, MwCreatureSource.Groups(keys)));
                }
            return rig;
        }

        /// <summary>The source a group comes from and its times; a part's times between two of its keys.</summary>
        public (int Source, float Start, float Stop)? Times(Source s)
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                var src = Sources[i];
                if (s.From is null)
                {
                    if (!src.Groups.TryGetValue(s.Group, out var g) || !g.Whole) continue;
                    return s.Loop && g.Loops ? (i, g.LoopStart, g.LoopStop) : (i, g.Start, g.Stop);
                }
                float? T(string label) => Key(src, s.Group, label);
                if (T(s.From) is float a && T(s.To!) is float b && b >= a) return (i, a, b);
            }
            return null;
        }

        public static float? Key(MwCreatureSource.ClipSource src, string group, string label) =>
            (src.Keys?.Keys ?? []).Where(k => k.Text.Split('\n').Any(l => l.Trim().Equals($"{group}: {label}", StringComparison.OrdinalIgnoreCase))).Select(k => (float?)k.Time).FirstOrDefault();

        /// <summary>The group under each group for the bones it leaves alone (Oblivion's: a weapon's moves over its idle;
        /// null for none, Morrowind's: its groups drive the whole body).</summary>
        public Func<string, string?>? DefaultBase;

        /// <summary>The sources of the groups under <paramref name="s"/>, nearest first: its own <c>Base</c> or the rig's
        /// rule's group, then the rule's group under that, and so on (empty for none).</summary>
        public List<int> BaseOf(Source s)
        {
            var chain = new List<int>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { s.Group };
            for (string? group = s.Base ?? DefaultBase?.Invoke(s.Group); group is not null && seen.Add(group); group = DefaultBase?.Invoke(group))
            {
                int i = Sources.FindIndex(x => x.Groups.TryGetValue(group, out var g) && g.Whole);
                if (i >= 0) chain.Add(i);
            }
            return chain;
        }

        /// <summary>The pose at <paramref name="t"/> on <paramref name="source"/>'s time: each node's model-space position and
        /// turn (a node its tracks don't drive from the first of <paramref name="bases"/>' groups that does, each played
        /// round from its start for the same time since <paramref name="start"/>; else at its rest), "Bip01"'s travel
        /// across the ground held at <paramref name="start"/>.</summary>
        public (Vector3[] T, Quaternion[] Q) Pose(int source, float t, float start, IReadOnlyList<int>? bases = null)
        {
            var tracks = Sources[source].Tracks;
            var under = new List<(Dictionary<string, NiKeyframeData> Tracks, float T, float Start)>();
            foreach (int bs in bases ?? [])
                if (Sources[bs].Groups.Values.FirstOrDefault(g => g.Whole) is { } bg && bg.Whole)
                {
                    float from = bg.Loops ? bg.LoopStart : bg.Start, to = bg.Loops ? bg.LoopStop : bg.Stop;
                    under.Add((Sources[bs].Tracks, to > from ? from + (MathF.Max(0, t - start) % (to - from)) : from, from));
                }
            int n = Names.Count;
            var wt = new Vector3[n]; var wq = new Quaternion[n];
            for (int b = 0; b < n; b++)
            {
                var (lt, lq) = Rest[b];
                float at0 = t, hold = start;
                if (!tracks.TryGetValue(Names[b], out var kd))
                    foreach (var u in under)
                        if (u.Tracks.TryGetValue(Names[b], out kd)) { at0 = u.T; hold = u.Start; break; }
                if (kd is not null)
                {
                    Vector3? Move(float time) => kd.TranslationCurve is { } tc ? tc(time) : kd.Translations.Count > 0 ? MwKeys.Vector(kd.Translations, kd.TranslationType, kd.TranslationExtra, time) : null;
                    if (Move(at0) is { } moved) lt = moved;
                    if (kd.RotationCurve is { } rc) lq = rc(at0);
                    else if (kd.Rotations.Count > 0) lq = MwKeys.Rotation(kd.Rotations, kd.RotationType, kd.RotationExtra, at0);
                    if (Names[b].Equals("Bip01", StringComparison.OrdinalIgnoreCase) && Move(hold) is { } at)
                        lt = new Vector3(at.X, at.Y, lt.Z);
                    // Right under the model root, the keys are relative to it: they carry its transform too.
                    if (Parents[b] < 0)
                    {
                        var withRoot = new NifTransform(Matrix4x4.Transpose(Matrix4x4.CreateFromQuaternion(lq)), lt, 1).Then(Root);
                        lt = withRoot.Translation;
                        lq = Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(Matrix4x4.Transpose(withRoot.Rotation)));
                    }
                }
                int p = Parents[b];
                wt[b] = p < 0 ? lt : wt[p] + Vector3.Transform(lt, wq[p]);
                wq[b] = p < 0 ? lq : Quaternion.Normalize(wq[p] * lq);
            }
            return (wt, wq);
        }

        /// <summary>The pose at rest (the skeleton's own).</summary>
        public (Vector3[] T, Quaternion[] Q) RestPose()
        {
            int n = Names.Count;
            var wt = new Vector3[n]; var wq = new Quaternion[n];
            for (int b = 0; b < n; b++)
            {
                int p = Parents[b];
                wt[b] = p < 0 ? Rest[b].T : wt[p] + Vector3.Transform(Rest[b].T, wq[p]);
                wq[b] = p < 0 ? Rest[b].Q : Quaternion.Normalize(wq[p] * Rest[b].Q);
            }
            return (wt, wq);
        }
    }

    // ---- retargeting ----

    /// <summary>How the two skeletons line up: for each Skyrim bone the Morrowind bone driving it (-1: none, it keeps its
    /// place on its parent), the two bones' anatomical offsets (bone frame to anatomical frame), the centre of mass bone
    /// and "Bip01", and the height ratio.</summary>
    public sealed class Mapping
    {
        public int[] Source = [];
        public Quaternion[] MwOffset = [];
        public Quaternion[] SkOffset = [];
        public int Com = -1, MwRoot = -1;
        public float Height = 1;
        public List<string> Notes = [];
    }

    static Quaternion FrameQ(Vector3 dir)
    {
        bool up = MathF.Abs(Vector3.Dot(Vector3.Normalize(dir), Vector3.UnitY)) > 0.7f;
        return Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(WornMesh.Frame(dir, up)));
    }

    public static Mapping Map(MwRig mw, SkyrimRig sk)
    {
        var bind = sk.Bind();
        var (mwT, mwQ) = mw.RestPose();
        int n = sk.Skeleton.Bones.Length;
        var m = new Mapping { Source = Enumerable.Repeat(-1, n).ToArray(), MwOffset = new Quaternion[n], SkOffset = new Quaternion[n] };
        bool five = mw.Names.Any(x => x.Contains("Finger3", StringComparison.OrdinalIgnoreCase));
        var skTargets = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        string[] TargetsOf(string bip) => skTargets.TryGetValue(bip, out var t) ? t : skTargets[bip] = WornMesh.SkyrimBones(bip, bind, five).Select(x => x.Bone).ToArray();
        bool Below(int b, int top) { for (int x = b; x >= 0; x = sk.Skeleton.Parents[x]) if (x == top) return true; return false; }
        m.MwRoot = mw.Index(mw.ComBone);
        m.Com = Array.FindIndex(sk.Skeleton.Bones, b => b.Contains("[COM ]", StringComparison.OrdinalIgnoreCase));
        if (m.MwRoot >= 0 && m.Com >= 0)
        {
            m.Source[m.Com] = m.MwRoot;
            m.Height = mwT[m.MwRoot].Z > 1 ? sk.WorldT[m.Com].Z / mwT[m.MwRoot].Z : 1;
        }
        // Anatomical directions at rest: Morrowind's bone to its next (a hand to its fingers' middle), Skyrim's to the bones
        // its next becomes below it.
        Vector3? MwDir(int b)
        {
            string name = mw.Names[b];
            var nexts = name.EndsWith(" Hand", StringComparison.OrdinalIgnoreCase) ? new[] { name.Replace(" Hand", " Finger1"), name.Replace(" Hand", " Finger2") }
                : WornMesh.NextOf(name) is { } nx ? [nx] : [];
            var found = nexts.Select(mw.Index).Where(i => i >= 0).ToList();
            return found.Count == 0 ? null : found.Aggregate(Vector3.Zero, (a, i) => a + mwT[i]) / found.Count - mwT[b];
        }
        Vector3? SkDir(int skBone, int mwBone)
        {
            string name = mw.Names[mwBone];
            var nexts = name.EndsWith(" Hand", StringComparison.OrdinalIgnoreCase) ? new[] { name.Replace(" Hand", " Finger1"), name.Replace(" Hand", " Finger2") }
                : WornMesh.NextOf(name) is { } nx ? [nx] : [];
            var all = nexts.SelectMany(TargetsOf).Select(sk.Index).Where(i => i >= 0 && i != skBone).Distinct().ToList();
            // Those below it (a three-finger hand's finger onto Skyrim's two: each its own); else wherever they hang (Skyrim's
            // spine hangs from its centre of mass, beside its pelvis, not below it).
            var found = all.Where(i => Below(i, skBone)).ToList();
            if (found.Count == 0) found = all;
            return found.Count == 0 ? null : found.Aggregate(Vector3.Zero, (a, i) => a + sk.WorldT[i]) / found.Count - sk.WorldT[skBone];
        }
        // Each Biped bone onto the Skyrim bones it becomes.
        var frames = new Dictionary<int, (Quaternion Mw, Quaternion Sk)>();
        for (int b = 0; b < mw.Names.Count; b++)
        {
            string name = mw.Names[b];
            if (!name.StartsWith("Bip01 ", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var target in TargetsOf(name))
            {
                int s = sk.Index(target);
                if (s < 0 || s == m.Com || m.Source[s] >= 0) continue;
                m.Source[s] = b;
            }
        }
        // Their offsets: anatomical frames where both run on to a next bone; an end bone (head, toes, finger tips) in the
        // frame of the bone above it that has one.
        for (int s = 0; s < n; s++)
        {
            int b = m.Source[s];
            if (b < 0 || s == m.Com) continue;
            Quaternion? mwF = MwDir(b) is { } dm && dm.LengthSquared() > 1e-4f ? FrameQ(dm) : null;
            Quaternion? skF = SkDir(s, b) is { } ds && ds.LengthSquared() > 1e-4f ? FrameQ(ds) : null;
            if (mwF is null || skF is null)
            {
                // The nearest mapped bone above with frames of its own.
                int up = sk.Skeleton.Parents[s];
                while (up >= 0 && !frames.ContainsKey(up)) up = sk.Skeleton.Parents[up];
                if (up < 0) { m.Notes.Add($"{sk.Skeleton.Bones[s]}: no frame above it, left as it rests"); m.Source[s] = -1; continue; }
                (mwF, skF) = frames[up];
            }
            else frames[s] = (mwF.Value, skF.Value);
            m.MwOffset[s] = Quaternion.Normalize(Quaternion.Inverse(mwQ[b]) * mwF.Value);
            m.SkOffset[s] = Quaternion.Normalize(Quaternion.Inverse(sk.WorldQ[s]) * skF.Value);
        }
        return m;
    }

    /// <summary>Skyrim's skeleton posed as Morrowind's pose (see the class summary): each bone's local translation and
    /// rotation, parents first.</summary>
    public static (Vector3 T, Quaternion Q)[] Retarget(Mapping m, SkyrimRig sk, (Vector3[] T, Quaternion[] Q) mwPose, (Vector3[] T, Quaternion[] Q) mwRest)
    {
        int n = sk.Skeleton.Bones.Length;
        var wt = new Vector3[n]; var wq = new Quaternion[n];
        var local = new (Vector3 T, Quaternion Q)[n];
        for (int s = 0; s < n; s++)
        {
            int p = sk.Skeleton.Parents[s];
            int b = m.Source[s];
            if (s == m.Com && b >= 0)
            {
                // The centre of mass: Morrowind's root's turn since rest on Skyrim's, its place scaled to Skyrim's height.
                wq[s] = Quaternion.Normalize(mwPose.Q[b] * Quaternion.Inverse(mwRest.Q[b]) * sk.WorldQ[s]);
                wt[s] = mwPose.T[b] * m.Height;
            }
            else if (b >= 0)
            {
                wq[s] = Quaternion.Normalize(mwPose.Q[b] * m.MwOffset[s] * Quaternion.Inverse(m.SkOffset[s]));
                wt[s] = p < 0 ? sk.Skeleton.T[s] : wt[p] + Vector3.Transform(sk.Skeleton.T[s], wq[p]);
            }
            else
            {
                wq[s] = p < 0 ? sk.Skeleton.Q[s] : Quaternion.Normalize(wq[p] * sk.Skeleton.Q[s]);
                wt[s] = p < 0 ? sk.Skeleton.T[s] : wt[p] + Vector3.Transform(sk.Skeleton.T[s], wq[p]);
            }
            if (p < 0) local[s] = (wt[s], wq[s]);
            else
            {
                var inv = Quaternion.Inverse(wq[p]);
                local[s] = (Vector3.Transform(wt[s] - wt[p], inv), Quaternion.Normalize(inv * wq[s]));
            }
        }
        return local;
    }

    // ---- events ----

    /// <summary>Where a Skyrim event goes in Morrowind's clip: at the Morrowind key that is the same moment (the blow at
    /// "Hit", the swing at "Min Hit", the draw at "Equip Attach", putting away at "Unequip Detach", an arrow nocked at
    /// "Shoot Attach" and loosed at "Shoot Release", a spell going at its "Release", footfalls at Morrowind's), else at
    /// the same share of the clip; the clip's end at its end.</summary>
    sealed class Feet(List<float> left, List<float> right)
    {
        int l, r;
        public float? Left() => l < left.Count ? left[l++] : null;
        public float? Right() => r < right.Count ? right[r++] : null;
    }

    static float Retime(string text, float time, float skDuration, float length, Func<string, float?> mwKey, Feet feet)
    {
        string e = text.Split('.')[0];
        float? at = e switch
        {
            "HitFrame" => mwKey("Hit"),
            "preHitFrame" or "weaponSwing" or "weaponLeftSwing" => mwKey("Min Hit"),
            "SoundPlay" when text.Contains("Swing", StringComparison.OrdinalIgnoreCase) => mwKey("Min Hit"),
            "weaponDraw" => mwKey("Equip Attach") ?? mwKey("Equip Stop"),
            "weaponSheathe" => mwKey("Unequip Detach") ?? mwKey("Unequip Start"),
            "arrowAttach" => mwKey("Shoot Attach"),
            "arrowRelease" or "BowRelease" => mwKey("Shoot Release"),
            "MLh_SpellFire_Event" or "MRh_SpellFire_Event" or "SpellFire" => mwKey("Release"),
            "FootLeft" or "FootScuffLeft" or "FootSprintLeft" => feet.Left(),
            "FootRight" or "FootScuffRight" or "FootSprintRight" => feet.Right(),
            "attackStop" or "Spell_Stop" or "bowReset" or "IdleStop" => length,
            _ => null,
        };
        return Math.Clamp(at ?? (skDuration > 0 ? time / skDuration * length : 0), 0, length);
    }

    // ---- writing ----

    /// <summary>A written replacement: its file (relative to the animations folder), the set, where it came from, its
    /// frames and events.</summary>
    public sealed record Written(string File, string Set, Source Source, string MwSource, float Start, float Stop, int Frames, int Events, bool Shared = true);

    /// <summary>The folder (beside a set's own) of its files played by clips with differing events (see <see cref="Write"/>).</summary>
    public const string SkyrimEventsSuffix = "_SkyrimEvents";

    /// <summary>
    /// A written file's length and the source time at each of its frames: the part played through, one frame held, or its
    /// loop repeated to Skyrim's length (a canned turn). A file whose clips keep their own events (not
    /// <paramref name="shared"/>) runs on past the latest of them (<paramref name="lastEvent"/>, at Skyrim's times) so
    /// each comes: a loop by whole turns of it (no jump where it starts again: Oblivion's 1 s walk stays one turn, its
    /// footfalls at 0.2 and 0.77 s; its 6 s idle two, for PickNewIdle at 8.93 s), else its last frame held; the turns of
    /// a loop so repeated (<c>Cycles</c>) past its first carry the events its clips share at its own moments (Write).
    /// </summary>
    static (float Length, Func<int, float> TimeOf, int Cycles) FrameTimes(Source s, float start, float stop, float skDuration, bool shared, float lastEvent)
    {
        float cycle = stop - start;
        bool repeat = s.Stretch || (!shared && s.Loop && !s.Hold);
        float length = s.Hold ? 1 / Fps : s.Stretch && skDuration > 0 ? skDuration : cycle;
        int cycles = 1;
        if (!shared && lastEvent >= length)
        {
            if (repeat && !s.Stretch && cycle > 0) { cycles = (int)MathF.Ceiling((lastEvent + 1 / Fps) / cycle); length = cycle * cycles; }
            else length = lastEvent + 1 / Fps;
        }
        return (length, f => s.Hold ? start : repeat && cycle > 0 ? start + (f / Fps) % cycle : Math.Min(start + f / Fps, stop), cycles);
    }

    /// <summary>
    /// Every set's replacements under <paramref name="dataDir"/>'s OAR folder for the character, and the OAR mod's and
    /// submods' configs. <paramref name="keywords"/>: each set's keyword (by EditorID) in <paramref name="plugin"/>.
    /// </summary>
    public static List<Written> Write(string dataDir, string skyrimData, Tes3Data mwData, IReadOnlyList<Set> sets, string plugin, IReadOnlyDictionary<string, uint> keywords, Action<string>? log = null) =>
        Write(dataDir, skyrimData, s => MwRig.Load(mwData, s), sets, plugin, keywords, log);

    /// <summary>The same for any game's people: <paramref name="load"/> each set's rig.</summary>
    public static List<Written> Write(string dataDir, string skyrimData, Func<Set, MwRig> load, IReadOnlyList<Set> sets, string plugin, IReadOnlyDictionary<string, uint> keywords, Action<string>? log = null)
    {
        var archives = new CreatureBehavior(skyrimData);
        var vanillaFiles = archives.ArchivePaths(AnimationsFolder).Select(p => p[AnimationsFolder.Length..].ToLowerInvariant()).ToHashSet();
        var written = new List<Written>();
        string oar = Path.Combine(dataDir, AnimationsFolder, "OpenAnimationReplacer", "TamrielMerge");
        Directory.CreateDirectory(oar);
        if (!File.Exists(Path.Combine(oar, "config.json")))
            File.WriteAllText(Path.Combine(oar, "config.json"), """
                {
                    "name": "TamrielMerge",
                    "author": "TamrielMerge",
                    "description": "TamrielMerge's own animations: climbing walls, and the converted games' people moving as their own games' do."
                }
                """);
        var skeletons = new Dictionary<bool, SkyrimRig>();
        var events = new Dictionary<bool, Dictionary<string, (float Duration, List<(float Time, string Text)> Events, bool Shared, float LastEvent)>>();
        int priority = 1000;
        foreach (var set in sets)
        {
            var mw = load(set);
            if (mw.Sources.Count == 0) { log?.Invoke($"{set.Key}: none of its animation files ({string.Join(", ", set.Kfs)})"); continue; }
            if (!skeletons.TryGetValue(set.Female, out var sk))
                skeletons[set.Female] = sk = SkyrimRig.From(ClimbAnimation.ReadSkeleton(archives.ReadArchive(set.Female ? FemaleSkeleton : MaleSkeleton) ?? throw new FileNotFoundException(set.Female ? FemaleSkeleton : MaleSkeleton)));
            if (!events.TryGetValue(set.Female, out var vanilla)) events[set.Female] = vanilla = VanillaEvents(archives, set.Female);
            var map = Map(mw, sk);
            var rest = mw.RestPose();
            string folder = Path.Combine(oar, set.Key), clipFolder = Path.Combine(oar, set.Key + SkyrimEventsSuffix);
            foreach (var d in new[] { folder, clipFolder }) if (Directory.Exists(d)) Directory.Delete(d, true);
            Directory.CreateDirectory(folder);
            int count = 0, clipCount = 0;
            // The swimming sets carry swimming alone (over the beast set).
            bool swimOnly = set.Kfs[0].Contains("swim", StringComparison.OrdinalIgnoreCase);
            foreach (var (file, source) in TableFor(set, f => vanillaFiles.Contains(f.ToLowerInvariant())))
            {
                if (swimOnly && !file.StartsWith("swim", StringComparison.OrdinalIgnoreCase)) continue;
                if (mw.Times(source) is not var (src, start, stop)) continue;
                if (swimOnly && !mw.Sources[src].Path.Contains("swim", StringComparison.OrdinalIgnoreCase)) continue;
                var (skDuration, skEvents, shared, lastEvent) = vanilla.GetValueOrDefault(file.ToLowerInvariant(), (0f, new List<(float Time, string Text)>(), true, 0f));
                var (length, timeOf, cycles) = FrameTimes(source, start, stop, skDuration, shared, lastEvent);
                int frames = Math.Max(2, (int)MathF.Round(length * Fps) + 1);
                var poses = new (Vector3 T, Quaternion Q)[frames][];
                var under = mw.BaseOf(source);
                for (int f = 0; f < frames; f++) poses[f] = Retarget(map, sk, mw.Pose(src, timeOf(f), start, under), rest);
                // Its events at Morrowind's moments.
                var mwSource = mw.Sources[src];
                string keyGroup = source.Group;
                float? MwKey(string label)
                {
                    string full = source.From is not null && Regex.Match(source.From, @"^(Chop|Slash|Thrust|Shoot|Self|Touch|Target)\b") is { Success: true } km ? $"{km.Value} {label}" : label;
                    var at = MwRig.Key(mwSource, keyGroup, full) ?? MwRig.Key(mwSource, keyGroup, label);
                    return at is float a && a >= start - 1e-4f && a <= stop + 1e-4f ? a - start : null;
                }
                var feet = (mwSource.Keys?.Keys ?? []).Where(k => k.Time >= start - 1e-4f && k.Time <= stop + 1e-4f)
                    .SelectMany(k => k.Text.Split('\n').Select(l => (k.Time - start, l.Trim().ToLowerInvariant()))).ToList();
                var leftFeet = feet.Where(x => x.Item2 is "soundgen: left" or "soundgen: swimleft").Select(x => x.Item1).ToList();
                var rightFeet = feet.Where(x => x.Item2 is "soundgen: right" or "soundgen: swimright").Select(x => x.Item1).ToList();
                float clipLength = (frames - 1) / Fps;
                // Shared events at its own moments; a file whose clips keep their own, repeated for them, the events its
                // clips share in each turn after the first (the first's come from each clip).
                List<(float Time, string Text)> Placed(float within, float offset)
                {
                    var footfalls = new Feet(leftFeet, rightFeet);
                    return skEvents.Select(e => (Time: offset + Retime(e.Text, e.Time, skDuration, within, MwKey, footfalls), e.Text)).ToList();
                }
                var annotations = shared ? Placed(clipLength, 0).OrderBy(e => e.Time).ToList()
                    : Enumerable.Range(1, cycles - 1).SelectMany(k => Placed(stop - start, k * (stop - start))).OrderBy(e => e.Time).ToList();
                string path = Path.Combine(shared ? folder : clipFolder, file);
                WriteHkx(path, sk.Skeleton, poses, annotations);
                written.Add(new Written(file, set.Key, source, mwSource.Path, start, stop, frames, annotations.Count, shared));
                if (shared) count++; else clipCount++;
            }
            // The submod: on this set's people, its events from its own files alone.
            uint keyword = keywords.TryGetValue(set.Keyword, out var k) ? k : throw new InvalidDataException($"no keyword {set.Keyword} in {plugin}");
            var conditions = new StringBuilder();
            conditions.Append($$"""
                        {
                            "condition": "HasKeyword",
                            "Keyword": { "pluginName": {{Json(plugin)}}, "formID": "{{keyword:X}}" }
                        }
                """);
            File.WriteAllText(Path.Combine(folder, "config.json"), $$"""
                {
                    "name": {{Json(set.Key)}},
                    "description": {{Json($"{set.Description}, retargeted onto Skyrim's {(set.Female ? "female" : "male")} skeleton, its events at its own moments.")}},
                    "priority": {{priority++}},
                    "replaceOnLoop": true,
                    "ignoreDontConvertAnnotationsToTriggersFlag": true,
                    "triggersFromAnnotationsOnly": true,
                    "conditions": [
                {{conditions}}
                    ]
                }
                """);
            // The files whose clips fire different events: each clip its own (Skyrim's clip data), and the files' own events
            // (the shared ones in a repeated loop's later turns) besides.
            if (clipCount > 0)
                File.WriteAllText(Path.Combine(clipFolder, "config.json"), $$"""
                    {
                        "name": {{Json(set.Key + SkyrimEventsSuffix)}},
                        "description": {{Json($"{set.Description}, retargeted onto Skyrim's {(set.Female ? "female" : "male")} skeleton: the animations Skyrim plays in clips with different events, each clip keeping its own.")}},
                        "priority": {{priority++}},
                        "replaceOnLoop": true,
                        "ignoreDontConvertAnnotationsToTriggersFlag": true,
                        "triggersFromAnnotationsOnly": false,
                        "conditions": [
                    {{conditions}}
                        ]
                    }
                    """);
            string from = mw.Sources.Count <= 6 ? string.Join(", ", mw.Sources.Select(s => Path.GetFileName(s.Path))) : $"{mw.Sources.Count} of its files";
            log?.Invoke($"{set.Key}: {count + clipCount} of Skyrim's character animations ({count} with their events at its own moments, {clipCount} played by clips with differing events, which keep their own), from {from} ({map.Source.Count(x => x >= 0)} of Skyrim's {sk.Skeleton.Bones.Length} bones driven; height x{map.Height:F3}){(map.Notes.Count > 0 ? "; " + string.Join("; ", map.Notes.Take(4)) : "")}");
        }
        return written;
    }

    /// <summary>A written replacement's check: its worst bone (the angle between Morrowind's anatomical frame and the Skyrim
    /// bone's, degrees; the centre of mass's distance from Morrowind's root scaled, units), its frames and events.</summary>
    public sealed record Checked(string Set, string File, int Frames, float WorstAngle, string WorstBone, int WorstFrame, float WorstCom, int Events, int MwEvents, bool Shared = true, bool Placed = true, int FramesWanted = 0, float EventsPastEnd = 0);

    /// <summary>
    /// Every written replacement read back against Morrowind (`npc-anim-check`): its frames posed on Skyrim's skeleton from
    /// the file, each driven bone's anatomical frame against its Morrowind bone's at the same moment, the centre of mass
    /// against Morrowind's root scaled to Skyrim's height, and its events (those Skyrim's clip has, all there).
    /// </summary>
    public static List<Checked> Check(string dataDir, string skyrimData, Tes3Data mwData, IReadOnlyList<Set> sets) =>
        Check(dataDir, skyrimData, s => MwRig.Load(mwData, s), sets);

    public static List<Checked> Check(string dataDir, string skyrimData, Func<Set, MwRig> load, IReadOnlyList<Set> sets)
    {
        var archives = new CreatureBehavior(skyrimData);
        var vanillaFiles = archives.ArchivePaths(AnimationsFolder).Select(p => p[AnimationsFolder.Length..].ToLowerInvariant()).ToHashSet();
        string oar = Path.Combine(dataDir, AnimationsFolder, "OpenAnimationReplacer", "TamrielMerge");
        var result = new List<Checked>();
        var events = new Dictionary<bool, Dictionary<string, (float Duration, List<(float Time, string Text)> Events, bool Shared, float LastEvent)>>();
        foreach (var set in sets)
        {
            var mw = load(set);
            var sk = SkyrimRig.From(ClimbAnimation.ReadSkeleton(archives.ReadArchive(set.Female ? FemaleSkeleton : MaleSkeleton)!));
            if (!events.TryGetValue(set.Female, out var vanilla)) events[set.Female] = vanilla = VanillaEvents(archives, set.Female);
            var map = Map(mw, sk);
            var rest = mw.RestPose();
            bool swimOnly = set.Kfs[0].Contains("swim", StringComparison.OrdinalIgnoreCase);
            foreach (var (file, source) in TableFor(set, f => vanillaFiles.Contains(f.ToLowerInvariant())))
            {
                var (skDuration, skEvents, shared, lastEvent) = vanilla.GetValueOrDefault(file.ToLowerInvariant(), (0f, new List<(float Time, string Text)>(), true, 0f));
                string path = Path.Combine(oar, shared ? set.Key : set.Key + SkyrimEventsSuffix, file);
                // In the folder its events call for, and not in the other.
                bool placed = !File.Exists(Path.Combine(oar, shared ? set.Key + SkyrimEventsSuffix : set.Key, file));
                if (!File.Exists(path))
                {
                    if (!placed) result.Add(new Checked(set.Key, file, 0, 0, "", 0, 0, 0, 0, shared, false, 0));
                    continue;
                }
                if (mw.Times(source) is not var (src, start, stop)) continue;
                var (length, timeOf, cycles) = FrameTimes(source, start, stop, skDuration, shared, lastEvent);
                var anim = CreatureBehavior.Hkx(File.ReadAllBytes(path)).m_namedVariants.Select(v => v?.m_variant).OfType<hkaAnimationContainer>().First().m_animations[0] as hkaInterleavedUncompressedAnimation;
                if (anim is null) continue;
                var under = mw.BaseOf(source);
                int n = sk.Skeleton.Bones.Length, frames = anim.m_transforms.Count / n;
                float worst = 0, worstCom = 0; string worstBone = ""; int worstFrame = 0;
                for (int f = 0; f < frames; f++)
                {
                    var pose = mw.Pose(src, timeOf(f), start, under);
                    var wt = new Vector3[n]; var wq = new Quaternion[n];
                    for (int b = 0; b < n; b++)
                    {
                        var m = anim.m_transforms[f * n + b];
                        var lt = new Vector3(m.M11, m.M12, m.M13);
                        var lq = Quaternion.Normalize(new Quaternion(m.M21, m.M22, m.M23, m.M24));
                        int p = sk.Skeleton.Parents[b];
                        wt[b] = p < 0 ? lt : wt[p] + Vector3.Transform(lt, wq[p]);
                        wq[b] = p < 0 ? lq : Quaternion.Normalize(wq[p] * lq);
                    }
                    for (int b = 0; b < n; b++)
                    {
                        int mb = map.Source[b];
                        if (mb < 0) continue;
                        if (b == map.Com)
                        {
                            float d = Vector3.Distance(wt[b], pose.T[mb] * map.Height);
                            if (d > worstCom) worstCom = d;
                            continue;
                        }
                        var want = Quaternion.Normalize(pose.Q[mb] * map.MwOffset[b]);
                        var got = Quaternion.Normalize(wq[b] * map.SkOffset[b]);
                        float angle = 2 * MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(want, got)), 0, 1)) * 180 / MathF.PI;
                        if (angle > worst) { worst = angle; worstBone = sk.Skeleton.Bones[b]; worstFrame = f; }
                    }
                }
                int written = anim.m_annotationTracks.Sum(tr => tr.m_annotations.Count);
                result.Add(new Checked(set.Key, file, frames, worst, worstBone, worstFrame, worstCom, written, shared ? skEvents.Count : skEvents.Count * (cycles - 1), shared, placed, Math.Max(2, (int)MathF.Round(length * Fps) + 1),
                    shared ? 0 : MathF.Max(0, lastEvent - anim.m_duration)));
            }
        }
        return result;
    }

    /// <summary>
    /// Proof to look at (the game isn't launched): for one written replacement at some of its frames, Skyrim's own body
    /// (male or female) skinned to the file's pose beside Morrowind's skeleton in the same pose (its bones drawn as lines,
    /// scaled to Skyrim's height), each from the side and the front, to a PNG (rows: frames; columns: Skyrim side, Morrowind
    /// side, Skyrim front, Morrowind front).
    /// </summary>
    public static void Preview(string dataDir, string skyrimData, Tes3Data mwData, Set set, string file, int[] frames, string png) =>
        Preview(dataDir, skyrimData, s => MwRig.Load(mwData, s), set, file, frames, png);

    public static void Preview(string dataDir, string skyrimData, Func<Set, MwRig> load, Set set, string file, int[] frames, string png)
    {
        var archives = new CreatureBehavior(skyrimData);
        var sk = SkyrimRig.From(ClimbAnimation.ReadSkeleton(archives.ReadArchive(set.Female ? FemaleSkeleton : MaleSkeleton)!));
        var mw = load(set);
        var map = Map(mw, sk);
        var source = TableFor(set, _ => true).First(x => x.File.Equals(file, StringComparison.OrdinalIgnoreCase)).Source;
        var (src, start, stop) = mw.Times(source) ?? throw new InvalidDataException($"{file}: Morrowind has no {source.Group}");
        string oarSet = Path.Combine(dataDir, AnimationsFolder, "OpenAnimationReplacer", "TamrielMerge", set.Key);
        string path = File.Exists(Path.Combine(oarSet, file)) ? Path.Combine(oarSet, file) : Path.Combine(oarSet + SkyrimEventsSuffix, file);
        bool sharedFile = File.Exists(Path.Combine(oarSet, file));
        var vanillaPreview = VanillaEvents(archives, set.Female);
        var (_, timeOfPreview, _) = FrameTimes(source, start, stop, vanillaPreview.TryGetValue(file, out var vp) ? vp.Duration : 0, sharedFile, vp.LastEvent);
        var anim = (hkaInterleavedUncompressedAnimation)CreatureBehavior.Hkx(File.ReadAllBytes(path)).m_namedVariants.Select(v => v?.m_variant).OfType<hkaAnimationContainer>().First().m_animations[0];
        int n = sk.Skeleton.Bones.Length, total = anim.m_transforms.Count / n;
        string bodyPath = set.Female ? @"meshes\actors\character\character assets\femalebody_1.nif" : @"meshes\actors\character\character assets\malebody_1.nif";
        byte[]? body = null;
        foreach (var bsa in Directory.GetFiles(skyrimData, "Skyrim - Meshes*.bsa"))
            body ??= Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, bsa).Files.FirstOrDefault(f => f.Path.Equals(bodyPath, StringComparison.OrdinalIgnoreCase))?.GetBytes();
        if (body is null) throw new FileNotFoundException(bodyPath);
        string tmp = Path.Combine(Path.GetTempPath(), $"tm-npc-body-{Environment.ProcessId}.nif");
        File.WriteAllBytes(tmp, body);
        using var nif = new nifly.NifFile();
        nif.Load(tmp);
        using var cache = new nifly.niflycpp.BlockCache(nif.GetHeader());
        var index = sk.Skeleton.Bones.Select((b, i) => (b, i)).ToDictionary(x => x.b, x => x.i);
        const int W = 300;
        var rgb = new byte[W * 4 * W * frames.Length * 3];
        for (int fi = 0; fi < frames.Length; fi++)
        {
            int f = Math.Clamp(frames[fi], 0, total - 1);
            var world = new Matrix4x4[n];
            for (int b = 0; b < n; b++)
            {
                var m = anim.m_transforms[f * n + b];
                var local = Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new Quaternion(m.M21, m.M22, m.M23, m.M24))) * Matrix4x4.CreateTranslation(m.M11, m.M12, m.M13);
                world[b] = sk.Skeleton.Parents[b] < 0 ? local : local * world[sk.Skeleton.Parents[b]];
            }
            var skin = new List<Vector3>();
            foreach (var shape in nif.GetShapes())
            {
                var verts = nif.GetVertsForShape(shape);
                using var names = new nifly.vectorstring();
                nif.GetShapeBoneList(shape, names);
                var posed = new Vector3[verts.Count];
                var inst = cache.EditableBlockById<nifly.NiSkinInstance>(shape.SkinInstanceRef().index);
                var sd = cache.EditableBlockById<nifly.NiSkinData>(inst.dataRef.index);
                for (int i = 0; i < names.Count; i++)
                {
                    if (!index.TryGetValue(names[i], out int bone)) continue;
                    var stb = new nifly.MatTransform();
                    nif.GetShapeTransformSkinToBone(shape, (uint)i, stb);
                    var r = stb.rotation;
                    var cx = r.opMult(new nifly.Vector3(1, 0, 0)); var cy = r.opMult(new nifly.Vector3(0, 1, 0)); var cz = r.opMult(new nifly.Vector3(0, 0, 1));
                    var toBone = new Matrix4x4(cx.x, cx.y, cx.z, 0, cy.x, cy.y, cy.z, 0, cz.x, cz.y, cz.z, 0, stb.translation.x, stb.translation.y, stb.translation.z, 1);
                    var full = toBone * world[bone];
                    foreach (var sw in sd.bones[i].vertexWeights)
                    {
                        var v = verts[sw.index];
                        posed[sw.index] += Vector3.Transform(new Vector3(v.x, v.y, v.z), full) * sw.weight;
                    }
                }
                skin.AddRange(posed);
            }
            // Morrowind's skeleton at the same moment, its bones as lines, scaled to Skyrim's height.
            var pose = mw.Pose(src, timeOfPreview(f), start, mw.BaseOf(source));
            var bones = new List<Vector3>();
            for (int b = 0; b < mw.Names.Count; b++)
            {
                int p = mw.Parents[b];
                if (p < 0 || !mw.Names[b].StartsWith("Bip01") || !mw.Names[p].StartsWith("Bip01")) continue;
                for (int k = 0; k <= 40; k++) bones.Add(Vector3.Lerp(pose.T[p], pose.T[b], k / 40f) * map.Height);
            }
            var lo = skin.Concat(bones).Aggregate(Vector3.Min); var hi = skin.Concat(bones).Aggregate(Vector3.Max);
            float span = MathF.Max(hi.X - lo.X, MathF.Max(hi.Y - lo.Y, hi.Z - lo.Z)) * 1.1f;
            var centre = (lo + hi) / 2;
            void Draw(IEnumerable<Vector3> pts, int column, bool side, (float R, float G, float B) tint)
            {
                foreach (var pnt in pts)
                {
                    var d = pnt - centre;
                    (float u, float v, float depth) = side ? (d.Y, d.Z, d.X) : (d.X, d.Z, -d.Y);
                    int px = (int)((u / span + 0.5f) * W), py = (int)((0.5f - v / span) * W);
                    if (px < 0 || py < 0 || px >= W || py >= W) continue;
                    byte shade = (byte)Math.Clamp(140 + depth / span * 200, 30, 255);
                    int q = ((fi * W + py) * W * 4 + column * W + px) * 3;
                    if (rgb[q] < shade * tint.R) { rgb[q] = (byte)(shade * tint.R); rgb[q + 1] = (byte)(shade * tint.G); rgb[q + 2] = (byte)(shade * tint.B); }
                }
            }
            Draw(skin, 0, true, (1, 0.85f, 0.6f)); Draw(bones, 1, true, (1, 0.3f, 0.3f));
            Draw(skin, 2, false, (1, 0.85f, 0.6f)); Draw(bones, 3, false, (1, 0.3f, 0.3f));
        }
        Util.Png.WriteRgb(png, W * 4, W * frames.Length, rgb);
        File.Delete(tmp);
    }

    /// <summary>An animation for Skyrim's skeleton: its frames (each bone's local transform), its events on its first track.</summary>
    public static void WriteHkx(string path, ClimbAnimation.Skeleton s, (Vector3 T, Quaternion Q)[][] frames, List<(float Time, string Text)> annotations)
    {
        int tracks = s.Bones.Length;
        static Matrix4x4 Qs(Vector3 t, Quaternion q) => new(t.X, t.Y, t.Z, 0, q.X, q.Y, q.Z, q.W, 1, 1, 1, 0, 0, 0, 0, 0);
        var anim = new hkaInterleavedUncompressedAnimation
        {
            m_type = 1,
            m_duration = (frames.Length - 1) / Fps,
            m_numberOfTransformTracks = tracks,
            m_numberOfFloatTracks = 0,
            m_annotationTracks = s.Bones.Select((b, i) => new hkaAnnotationTrack
            {
                m_trackName = b,
                m_annotations = i == 0 ? annotations.Select(a => new hkaAnnotationTrackAnnotation { m_time = a.Time, m_text = a.Text }).ToList() : [],
            }).ToList(),
            m_transforms = [.. frames.SelectMany(f => f.Select(x => Qs(x.T, x.Q)))],
            m_floats = [],
        };
        var binding = new hkaAnimationBinding
        {
            m_originalSkeletonName = s.Name,
            m_animation = anim,
            m_transformTrackToBoneIndices = Enumerable.Range(0, tracks).Select(i => (short)i).ToList(),
            m_floatTrackToFloatSlotIndices = [],
            m_blendHint = 0,
        };
        var root = new hkRootLevelContainer
        {
            m_namedVariants = [new hkRootLevelContainerNamedVariant { m_name = "Merged Animation Container", m_className = "hkaAnimationContainer",
                m_variant = new hkaAnimationContainer { m_animations = [anim], m_bindings = [binding] } }],
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        new PackFileSerializer().Serialize(root, new BinaryWriterEx(fs), HKXHeader.SkyrimSE());
    }
}
