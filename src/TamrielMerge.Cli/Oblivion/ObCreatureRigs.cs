using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Oblivion;

/// <summary>
/// A Skyrim creature skeleton by role: the bones Oblivion's creatures are re-rigged onto (<see cref="ObCreatureRetarget"/>).
/// Names are looked up on the skeleton the target race really names: exactly, or as the start of a bone's name
/// ("NPC Spine" finds "NPC Spine [Spn0]"), the shortest match winning ("NPC L Thigh [RThg]" over "NPC L Thigh [RThg]
/// Bones" on the giant); "a|b" tries a, then b. Bone lists were read from each skeleton.nif in Skyrim - Meshes0.bsa.
/// </summary>
public sealed record SkyrimRig(string Key, string Pelvis, string[] Spine, string[] Neck, string? Head, string? Jaw, string[] ArmL, string[] ArmR, string[] LegL, string[] LegR, string[] Tail)
{
    /// <summary>Further chains by role, used when the source has the same role (a horse's mane: Ponytail1, Ponytail2).</summary>
    public Dictionary<string, string[]> Extra { get; init; } = [];
    /// <summary>Many-legged skeletons: each side's legs, front to back.</summary>
    public string[][] LegsL { get; init; } = [];
    public string[][] LegsR { get; init; } = [];
    /// <summary>Has "L Finger00" .. "L Finger42" style fingers under this name prefix ("" none).</summary>
    public string Fingers { get; init; } = "";

    /// <summary>The bone a table name stands for on a skeleton, or null when it hasn't one.</summary>
    public static string? Find(BindSkeleton s, string name)
    {
        foreach (var option in name.Split('|'))
        {
            if (s.Has(option)) return option;
            var hit = s.World.Keys.Where(k => k.StartsWith(option + " ", StringComparison.OrdinalIgnoreCase) || k.StartsWith(option + "[", StringComparison.OrdinalIgnoreCase))
                .OrderBy(k => k.Length).FirstOrDefault();
            if (hit is not null) return hit;
        }
        return null;
    }

    /// <summary>Skyrim's Biped-built skeletons share one layout under a name prefix ("NPC ", "FireAtronach_", ...).</summary>
    static SkyrimRig Humanoid(string key, string p, string? jaw, string[]? neck = null, string? fingers = null, string? head = null, string? toeL = null, string? toeR = null) => new(key,
        p + "Pelvis", [p + "Spine", p + "Spine1", p + "Spine2"], neck ?? [p + "Neck"], head ?? p + "Head", jaw,
        [p + "L Clavicle", p + "L UpperArm", p + "L Forearm", p + "L Hand"], [p + "R Clavicle", p + "R UpperArm", p + "R Forearm", p + "R Hand"],
        [p + "L Thigh", p + "L Calf", p + "L Foot", toeL ?? p + "L Toe0"], [p + "R Thigh", p + "R Calf", p + "R Foot", toeR ?? p + "R Toe0"], [])
    { Fingers = fingers ?? "" };

    /// <summary>A four-footed skeleton whose legs are named per side.</summary>
    static SkyrimRig Quadruped(string key, string pelvis, string[] spine, string[] neck, string head, string? jaw, Func<string, string[]> fore, Func<string, string[]> hind, string[] tail) =>
        new(key, pelvis, spine, neck, head, jaw, fore("L"), fore("R"), hind("L"), hind("R"), tail);

    static string[] Numbered(string stem, int count, string? last = null) =>
        [.. Enumerable.Range(1, count).Select(i => stem + i), .. last is null ? Array.Empty<string>() : [last]];

    static string[] SpiderLeg(string side, int leg) =>
        [.. Enumerable.Range(0, 5).Select(j => $"Leg_{side}[{leg}{j}]"), $"[{side}Toe{leg}]"];

    static string[] CrabLeg(string side, int leg) =>
        [$"{side}Leg_{leg}0", $"{side}Leg_{leg}1", $"{side}Leg_{leg}2", leg == 0 && side == "L" ? "LLeg_LTip0" : $"{side}Leg_Tip{leg}"];

    /// <summary>Every target skeleton, by key.</summary>
    public static readonly Dictionary<string, SkyrimRig> All = new SkyrimRig[]
    {
        Humanoid("falmer", "NPC ", "NPC Jaw1", fingers: "NPC "),
        Humanoid("riekling", "NPC ", "NPC Jaw1", fingers: "NPC "),
        Humanoid("giant", "NPC ", "NPC Jaw", neck: ["NPC Neck [Neck]1", "NPC Neck [Neck]2"]),
        Humanoid("troll", "NPC ", "NPC Jaw [Jaw]"),
        Humanoid("draugr", "NPC ", "NPC Head [Jaw]"),
        Humanoid("spriggan", "NPC ", null),
        Humanoid("atronachfrost", "NPC ", null) with { LegL = ["NPC L Thigh", "NPC L Calf", "NPC L Foot"], LegR = ["NPC R Thigh", "NPC R Calf", "NPC R Foot"] },
        Humanoid("atronachstorm", "NPC ", "Jaw", neck: [], head: "HEAD"),
        Humanoid("atronachflame", "FireAtronach_", "FireAtronach_Head [Jaw]"),
        Humanoid("dragonpriest", "DragPriestNPC ", "DragPriestNPC Head Jaw]", toeL: "DragPriestNPC L Toe[LToe]", toeR: "DragPriestNPC R Toe0"),
        Quadruped("sabrecat", "Sabrecat_[pelv]",
            ["Sabrecat_Spine[Spn0]", "Sabrecat_Spine[Spn1]", "Sabrecat_Spine[Spn2]", "Sabrecat_Spine[Spn3]", "Sabrecat_Ribcage[Spn4]"],
            ["Sabrecat_Neck[Nek0]", "Sabrecat_Neck[Nek1]", "Sabrecat_Neck[Nek2]"], "Sabrecat_Head [Head]", "Sabrecat_Head[jaw]",
            s => { string w = s == "L" ? "Left" : "Right"; return [$"Sabrecat_{w}Clavicle[{s}Clv]", $"Sabrecat_{w}UpperArm[{s}Uar]", $"Sabrecat_{w}Forearm[{s}Far]", $"Sabrecat_{w}Hand[{s}Hnd]"]; },
            s => { string w = s == "L" ? "Left" : "Right"; return [$"Sabrecat_{w}Thigh[{s}Thi]", $"Sabrecat_{w}Calf[{s}Clf]", $"Sabrecat_{w}Foot[{s}Fot]", $"Sabrecat_{w}Toe0[{s}T00]", $"Sabrecat_{w}Toe0[{s}T01]"]; },
            ["Sabrecat_Tail0[Tal0]", "Sabrecat_Tail1[Tal1]", "Sabrecat_Tail2[Tal2]"]),
        Quadruped("bear", "NPC Pelvis", ["NPC Spine1", "NPC Spine2", "NPC Spine3", "NPC Spine4", "NPC Ribcage"], ["NPC Neck1", "NPC Neck2"], "NPC Head", "NPC Jaw",
            s => [$"NPC {s}ArmCollarbone", $"NPC {s}Arm1", $"NPC {s}Arm2", $"NPC {s}ArmPalm", $"NPC {s}ArmBall1"],
            s => [$"NPC {s}Leg1", $"NPC {s}Leg2", $"NPC {s}LegAnkle", $"NPC {s}LegBall1", $"NPC {s}_Toe"], []),
        Quadruped("canine", "Canine_Pelvis", [.. Numbered("Canine_Spine", 3, "Canine_Ribcage")], ["Canine_Neck1", "Canine_Neck2"], "Canine_Head", "Canine_JawBone",
            s => [$"Canine_{s}FrontLegShoulderblade", $"Canine_{s}FrontLeg1", $"Canine_{s}FrontLeg2", $"Canine_{s}FrontLegPalm", $"Canine_{s}FrontLegToe"],
            s => [$"Canine_{s}BackLeg1", $"Canine_{s}BackLeg2", $"Canine_{s}BackLegPalm", $"Canine_{s}BackLegToe"], Numbered("Canine_Tail", 3)),
        Quadruped("boar", "Boar_Pelvis", [.. Numbered("Boar_Spine", 3, "Boar_Chest01")], ["Boar_Neck_1", "Boar_Neck_2"], "Boar_Head", "Boar_Jaw1",
            s => s == "L" ? ["Boar_LFrontLegCollarbone", "Boar_LFrontLeg1", "Boar_LFrontLeg2", "Boar_LFrontLegAnkle", "Boar_LFrontLegHoof_1"]
                          : ["Boar_RFrontLeg_Collarbone", "Boar_RFrontLeg_1", "Boar_RFrontLeg_2", "Boar_RFrontLeg_Ankle", "Boar_RFrontLeg_Hoof_1"],
            s => [$"Boar_{s}BackLeg1", $"Boar_{s}BackLeg2", $"Boar_{s}BackLeg3", $"Boar_{s}BackLegAnkle"], Numbered("Boar_Tail", 3)),
        Quadruped("horse", "HorsePelvis", [.. Numbered("HorseSpine", 4, "HorseRibcage")], Numbered("HorseNeck", 4), "HorseScull", "HorseJaw",
            s => s == "L" ? ["HorseLScapula", "HorseLHumerus", "HorseLRadius", "HorseLMetacarpus", "HorseLPhalangesManus", "HorseLFrontHoof"]
                          : ["HorseFrontRLegScapula", "HorseFrontRLegHumerus", "HorseFrontRLegRadius", "HorseFrontRLegMetacarpus", "HorseFrontRLegPhalangesManus", "HorseFrontRLegFrontHoof"],
            s => [$"Horse{s}Femur", $"Horse{s}Tibia", $"Horse{s}LargeCannon", $"Horse{s}PhalanxPrima", $"Horse{s}RearHoof"], Numbered("HorseTail", 4))
        with { Extra = new() { ["Ponytail1"] = ["HorseMane_Upper01", "HorseMane_Upper02"], ["Ponytail2"] = ["HorseMane_Lower01", "HorseMane_Lower02"] } },
        Quadruped("deer", "ElkPelvis", [.. Numbered("ElkSpine", 3, "ElkRibcage")], Numbered("ElkNeck", 4), "ElkScull", "ElkJaw",
            s => [$"Elk{s}Scapula", $"Elk{s}Humerus", $"Elk{s}Radius", $"Elk{s}Metacarpus", $"Elk{s}PhalangesManus", $"Elk{s}FrontHoof"],
            s => [$"Elk{s}Femur", $"Elk{s}Tibia", $"Elk{s}LargeCannon", $"Elk{s}PhalanxPrima", $"Elk{s}RearHoof"], Numbered("ElkTail", 2)),
        Quadruped("goat", "Goat_Pelvis", [.. Numbered("Goat_Spine", 3, "Goat_Ribcage")], Numbered("Goat_Neck", 4), "Goat_Head", "Goat_JawBone",
            s => [$"Goat_{s}FrontLegShoulderblade", $"Goat_{s}FrontLeg_Thigh", $"Goat_{s}FrontLeg_Upper", $"Goat_{s}FrontLeg_Lower", $"Goat_{s}FrontLegPalm", $"Goat_{s}FrontLegToe"],
            s => [$"Goat_{s}BackLeg_Thigh", $"Goat_{s}BackLeg_Caf", $"Goat_{s}BackLegPalm", $"Goat_{s}BackLegToe"], Numbered("Goat_Tail", 2)),
        Quadruped("skeever", "Pelvis", ["SpineLowerSpine", "SpineUpperSpine", "Torso"], ["Neck"], "HEAD", "Jaw",
            s => [$"{s}Arm_Clavicle", $"{s}Arm_Upper", $"{s}Arm_Forearm", $"{s}Arm_Palm", $"{s}Arm_Pinky1"],
            s => [$"{s}Leg1", $"{s}Leg2", $"{s}Leg3", $"{s}LegAnkle", $"{s}Leg_Toe2"], Numbered("Tail", 4)),
        new("slaughterfish", "SlaughterfishBody", [], ["SlaughterfishNeck"], "SlaughterfishHead", "SlaughterfishJaw",
            ["SlaughterfishBodyLFin", "SlaughterfishBodyLFin1", "SlaughterfishBodyLFin2"], ["SlaughterfishBodyRFin", "SlaughterfishBodyRFin1", "SlaughterfishBodyRFin2"],
            ["Slaughterfish[Hip]LFin", "Slaughterfish[Hip]LFin1"], ["Slaughterfish[Hip]RFin", "Slaughterfish[Hip]RFin1"],
            ["SlaughterfishTail[Hip]", "SlaughterfishTail[1]", "SlaughterfishTail[2]", "SlaughterfishTail[3]", "SlaughterfishTail[4]", "SlaughterfishTail[5]", "SlaughterfishTail[6]"]),
        new("mudcrab", "Mcrab_Body", [], [], null, null, ["Mcrab_LArm", "Mcrab_LClaw", "Mcrab_LPincher"], ["Mcrab_RArm", "Mcrab_RClaw", "Mcrab_RPincher"], [], [], [])
        { LegsL = [CrabLeg("L", 0), CrabLeg("L", 1), CrabLeg("L", 2)], LegsR = [CrabLeg("R", 0), CrabLeg("R", 1), CrabLeg("R", 2)] },
        new("frostbitespider", "[body]", [], [], "NPC Head", null, ["ArmLCollarbone", "ArmL[01]", "ArmL[02]", "ArmLClaw"], ["ArmRCollarbone", "ArmR[01]", "ArmR[02]", "ArmRClaw"], [], [], ["Tail1", "Tail2", "Tail3"])
        { LegsL = [SpiderLeg("L", 0), SpiderLeg("L", 1), SpiderLeg("L", 2), SpiderLeg("L", 3)], LegsR = [SpiderLeg("R", 0), SpiderLeg("R", 1), SpiderLeg("R", 2), SpiderLeg("R", 3)] },
    }.ToDictionary(r => r.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>Oblivion's many-legged creatures: each side's legs by normalized bone name (<see cref="ObCreatureRetarget.Normal"/>).
    /// Sorted front to back from their own bind before they are matched to the target's legs.</summary>
    public static readonly Dictionary<string, Func<string, string[][]>> SourceLegs = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mudcrab"] = s => [.. Enumerable.Range(1, 3).Select(i => new[] { $"pelvis {s}{i}", $"thigh {s}{i}", $"calf {s}{i}", $"foot {s}{i}", $"toe {s}{i}" })],
        ["elytra"] = s => [.. Enumerable.Range(1, 3).Select(i => new[] { $"{s} thigh bone{i}", $"{s} calf bone{i}", $"{s} foot bone{i}" })],
        ["landdreugh"] = s => [[$"{s}thigh1", $"{s}calf1", $"{s}foot1", $"{s}foot2", $"{s}foot3"], [$"{s}thigh2", $"{s}calf2", $"{s}foot4", $"{s}foot5", $"{s}foot6"]],
    };
}

/// <summary>
/// One row of the table of Oblivion's creatures: the model folder a creature's skeleton is in, the vanilla actor it is
/// built from (its race's skeleton, behaviour, animations and attack data drive the body) and the rig the body goes on.
/// </summary>
public sealed record CreatureLook(string Folder, string Template, string Rig, string Why)
{
    /// <summary>
    /// The nearest shape for each of Oblivion's skeletons (2026-10-29, ledger O67). Folders not here keep their stand-in:
    /// the will-o-the-wisp (all particles, no body), "endgame" and "boxtest" (scenery and a test).
    /// </summary>
    public static readonly CreatureLook[] Table =
    [
        // People-shaped: the stance, hands for weapons and spells.
        new("goblin", "EncFalmer01", "falmer", "a hunched two-legged tribesman with weapons and shamans' spells; Falmer stoop, fight and cast the same"),
        new("grummite", "EncFalmer01", "falmer", "a two-legged fish-man with weapons and spells, stooped as the goblin"),
        new("xivilai", "EncFalmer01", "falmer", "a tall armed caster. Dremora, its first thought, are people: their FaceGen head would draw over its own head, and the Falmer's body is one mesh that casts"),
        new("spiderdaedra", "EncFalmer01", "falmer", "a caster with a woman's top: her torso, arms and head ride the humanoid bones and her spider half hangs rigid off the pelvis (humanoid skeleton, legs attached)"),
        new("scamp", "DLC2EncRiekling", "riekling", "small, two-legged, long arms"),
        new("imp", "DLC2EncRiekling", "riekling", "small and two-legged; the wisp's stand-in has no body to carry one, and its wings stay on the spine as unweighted parts"),
        new("zombie", "EncDraugr", "draugr", "a shambling corpse; the draugr is one"),
        new("shambles", "EncDraugr", "draugr", "bones and meat on two legs, a corpse's walk"),
        new("skeleton", "EncSkeleton01", "draugr", "Skyrim's skeletons are on the draugr's skeleton and animations"),
        new("ghost", "EncSkeleton01", "draugr", "upright undead with a sword; Oblivion's body stays translucent and glowing through its own material"),
        new("lich", "EncDragonPriestStaff", "dragonpriest", "a robed spellcasting undead; a dragon priest floats and casts as it does"),
        new("wraith", "EncDragonPriestStaff", "dragonpriest", "a floating robed undead with a sword and spells"),
        // Big and heavy: the sizes come from the race's height.
        new("minotaur", "EncGiant01", "giant", "huge two-legged brute, its reverse-kneed leg running calf to foot to toe along the giant's"),
        new("ogre", "EncGiant01", "giant", "two-legged brute; the giant scaled down to it"),
        new("gatekeeper", "EncGiant01", "giant", "the Shivering Isles' giant of flesh, with a giant's swings"),
        new("jyggylag", "EncGiant01", "giant", "Jyggalag is a giant armoured man, not a machine: no centurion's chassis"),
        new("mehrunesdagon", "EncGiant01", "giant", "the four-armed giant at the end: his upper arms drive the giant's, the lower pair ride the torso"),
        new("troll", "EncTroll", "troll", "the same creature in both games"),
        new("hunger", "EncTroll", "troll", "long-armed, loping, clawed"),
        new("fleshatronach", "EncTroll", "troll", "a heavy lumbering stitched body without a weapon"),
        new("gnarl", "EncSpriggan", "spriggan", "a walking tree-thing on two legs"),
        new("spriggan", "EncSpriggan", "spriggan", "the same creature in both games"),
        new("flameatronach", "EncAtronachFlame", "atronachflame", "the same creature in both games"),
        new("frostatronach", "EncAtronachFrost", "atronachfrost", "the same creature in both games"),
        new("stormatronach", "EncAtronachStorm", "atronachstorm", "the same creature in both games; its rocks are shapes in its skeleton.nif and ride the bones"),
        // Four feet.
        new("daedroth", "EncTroll", "troll", "a crocodile-headed brute standing on two legs (measured: hands 153 up, pelvis 101)"),
        new("clannfear", "EncSabreCat", "sabrecat", "a level-backed raptor (measured: pelvis 58, head 89 up and 49 ahead), not an upright one: on the troll its back stood up and its tail stuck out; the cat keeps the level back and animates the tail, its short arms drawn down as forelegs"),
        new("murkdweller", "EncTroll", "troll", "the scalon, by stance: two legs (measured: hands 98 up, pelvis 85, head 149), so the troll, not the sabre cat of its stand-in"),
        new("horse", "EncHorseBrown", "horse", "the same animal; its saddle, bridle, mane, tail and the horse armour plugin's barding ride its bones"),
        new("dog", "EncWolf", "canine", "dogs and wolves share Oblivion's skeleton as Skyrim's share the canine one"),
        new("bear", "EncBear", "bear", "the same animal"),
        new("boar", "DLC2EncBoarWild", "boar", "the same animal"),
        new("mountainlion", "EncSabreCat", "sabrecat", "the big cat"),
        new("deer", "EncDeer", "deer", "the same animal"),
        new("sheep", "EncGoatDomestic", "goat", "a small horned grazer"),
        new("rat", "EncSkeever", "skeever", "the same shape, large rat"),
        new("baliwog", "EncSkeever", "skeever", "low, four-footed and tailed; closer to the skeever's body than the ash hopper's six legs"),
        // Many legs, no spine.
        new("mudcrab", "EncMudcrabMedium", "mudcrab", "the same animal"),
        new("slaughterfish", "EncSlaughterfish", "slaughterfish", "the same animal"),
        new("landdreugh", "EncFrostbiteSpider", "frostbitespider", "arms, four walking legs and a tail on a low body: the spider has all of them, the mudcrab has no tail"),
        new("elytra", "EncFrostbiteSpider", "frostbitespider", "an insect with six legs, forelimbs and a stinger; the spider has legs to spare and a tail"),
    ];

    /// <summary>The row for a creature's skeleton path ("Creatures\Goblin\Skeleton.NIF"), or null.</summary>
    public static CreatureLook? For(string? skeleton)
    {
        if (skeleton is null) return null;
        var parts = ObData.Normalize(skeleton).Split('\\');
        int at = Array.IndexOf(parts, "creatures");
        return at >= 0 && at + 1 < parts.Length ? Table.FirstOrDefault(r => r.Folder == parts[at + 1]) : null;
    }
}
