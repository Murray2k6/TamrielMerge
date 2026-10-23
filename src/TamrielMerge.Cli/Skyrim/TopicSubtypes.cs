using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Fills each dialogue topic's SNAM (subtype name) from its subtype. Mutagen keeps SNAM as a separate field that
/// defaults to zero, and Skyrim crashes while loading a plugin whose topic has a zero SNAM, so every plugin runs this
/// before it is written. SNAM is what tells Skyrim which moment a topic is for: Skyrim.esm's own topics carry stray
/// DATA subtypes (2026-10-23, `sk-topics`: DialogueGenericHello DATA 79, TG05Hellos / T03Hellos / DA04Hello DATA 73,
/// all SNAM HELO and all hellos), so the codes here are the ones Skyrim.esm's topics use, each by its name
/// (`sk-condition-props --topics`), and only those: a code Skyrim.esm has no topic of isn't one its engine is known to say.
/// </summary>
public static class TopicSubtypes
{
    public static readonly Dictionary<DialogTopic.SubtypeEnum, string> Codes = new()
    {
        [DialogTopic.SubtypeEnum.Custom] = "CUST",
        [DialogTopic.SubtypeEnum.ForceGreet] = "PFGT",
        [DialogTopic.SubtypeEnum.Rumors] = "RUMO",
        [DialogTopic.SubtypeEnum.Intimidate] = "INTI",
        [DialogTopic.SubtypeEnum.Bribe] = "BRIB",
        [DialogTopic.SubtypeEnum.Scene] = "SCEN",
        [DialogTopic.SubtypeEnum.Agree] = "AGRE",
        [DialogTopic.SubtypeEnum.Refuse] = "REFU",
        [DialogTopic.SubtypeEnum.MoralRefusal] = "MREF",
        [DialogTopic.SubtypeEnum.Show] = "SHOW",
        [DialogTopic.SubtypeEnum.ExitFavorState] = "FEXT",
        // Combat
        [DialogTopic.SubtypeEnum.Attack] = "ATCK",
        [DialogTopic.SubtypeEnum.PowerAttack] = "POAT",
        [DialogTopic.SubtypeEnum.Bash] = "BASH",
        [DialogTopic.SubtypeEnum.Hit] = "HIT_",
        [DialogTopic.SubtypeEnum.Flee] = "FLEE",
        [DialogTopic.SubtypeEnum.Bleedout] = "BLED",
        [DialogTopic.SubtypeEnum.Death] = "DETH",
        [DialogTopic.SubtypeEnum.Block] = "BLOC",
        [DialogTopic.SubtypeEnum.Taunt] = "TAUT",
        [DialogTopic.SubtypeEnum.AcceptYield] = "ACYI",
        [DialogTopic.SubtypeEnum.Steal] = "STEA",
        [DialogTopic.SubtypeEnum.StealFromNC] = "STFN",
        [DialogTopic.SubtypeEnum.PickpocketCombat] = "PICC",
        [DialogTopic.SubtypeEnum.PickpocketNC] = "PICN",
        [DialogTopic.SubtypeEnum.Assault] = "ASSA",
        [DialogTopic.SubtypeEnum.AssaultNC] = "ASNC",
        [DialogTopic.SubtypeEnum.Murder] = "MURD",
        [DialogTopic.SubtypeEnum.MurderNC] = "MUNC",
        [DialogTopic.SubtypeEnum.Trespass] = "TRES",
        [DialogTopic.SubtypeEnum.TrespassAgainstNC] = "TRAN",
        [DialogTopic.SubtypeEnum.WerewolfTransformCrime] = "WTCR",
        [DialogTopic.SubtypeEnum.VoicePowerStartShort] = "VPSS",
        [DialogTopic.SubtypeEnum.VoicePowerStartLong] = "VPSL",
        [DialogTopic.SubtypeEnum.VoicePowerEndShort] = "VPES",
        [DialogTopic.SubtypeEnum.VoicePowerEndLong] = "VPEL",
        // Detection
        [DialogTopic.SubtypeEnum.AlertIdle] = "ALIL",
        [DialogTopic.SubtypeEnum.LostIdle] = "LOIL",
        [DialogTopic.SubtypeEnum.NormalToAlert] = "NOTA",
        [DialogTopic.SubtypeEnum.AlertToCombat] = "ALTC",
        [DialogTopic.SubtypeEnum.NormalToCombat] = "NOTC",
        [DialogTopic.SubtypeEnum.AlertToNormal] = "ALTN",
        [DialogTopic.SubtypeEnum.CombatToNormal] = "COTN",
        [DialogTopic.SubtypeEnum.CombatToLost] = "COLO",
        [DialogTopic.SubtypeEnum.LostToNormal] = "LOTN",
        [DialogTopic.SubtypeEnum.LostToCombat] = "LOTC",
        [DialogTopic.SubtypeEnum.DetectFriendDie] = "DFDA",
        // Misc
        [DialogTopic.SubtypeEnum.Hello] = "HELO",
        [DialogTopic.SubtypeEnum.Goodbye] = "GBYE",
        [DialogTopic.SubtypeEnum.Idle] = "IDLE",
        [DialogTopic.SubtypeEnum.ObserveCombat] = "OBCO",
        [DialogTopic.SubtypeEnum.NoticeCorpse] = "NOTI",
        [DialogTopic.SubtypeEnum.TimeToGo] = "TITG",
        [DialogTopic.SubtypeEnum.SwingMeleeWeapon] = "SWMW",
        [DialogTopic.SubtypeEnum.ShootBow] = "FIWE",
        [DialogTopic.SubtypeEnum.ZKeyObject] = "ZKEY",
        [DialogTopic.SubtypeEnum.KnockOverObject] = "KNOO",
        [DialogTopic.SubtypeEnum.DestroyObject] = "DEOB",
        [DialogTopic.SubtypeEnum.StandOnFurniture] = "STOF",
        [DialogTopic.SubtypeEnum.LockedObject] = "LOOB",
        [DialogTopic.SubtypeEnum.PickpocketTopic] = "PICT",
        [DialogTopic.SubtypeEnum.PursueIdleTopic] = "PURS",
        [DialogTopic.SubtypeEnum.SharedInfo] = "IDAT",
        [DialogTopic.SubtypeEnum.PlayerShout] = "PCSH",
        [DialogTopic.SubtypeEnum.PlayerInIronSights] = "PIRN",
        [DialogTopic.SubtypeEnum.EnterBowZoomBreath] = "ENBZ",
        [DialogTopic.SubtypeEnum.ExitBowZoomBreath] = "EXBZ",
        [DialogTopic.SubtypeEnum.ActorCollideWithActor] = "ACAC",
        [DialogTopic.SubtypeEnum.OutOfBreath] = "OUTB",
        [DialogTopic.SubtypeEnum.CombatGrunt] = "GRNT",
        [DialogTopic.SubtypeEnum.LeaveWaterBreath] = "LWBS",
        [DialogTopic.SubtypeEnum.EnterSprintBreath] = "BREA",
    };

    /// <summary>The subtype whose code is <paramref name="code"/>.</summary>
    public static DialogTopic.SubtypeEnum? Of(string code) => Codes.FirstOrDefault(kv => kv.Value == code) is { Value: not null } kv ? kv.Key : null;

    public static void Fill(ISkyrimMod mod)
    {
        foreach (var topic in mod.DialogTopics)
        {
            if (!Codes.TryGetValue(topic.Subtype, out var code))
                throw new InvalidOperationException($"No SNAM code for dialogue subtype {topic.Subtype} (topic {topic.EditorID}): add it to TopicSubtypes");
            topic.SubtypeName = new RecordType(code);
        }
    }
}
