using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's enemy senses for Daggerfall's own enemies (docs/daggerfall-senses.md; user, 2026-10-15: "daggerfall has a
/// enemy detection system that needs in for the daggerfall part only"). Daggerfall Unity's EnemySenses/EnemyMotor (MIT)
/// decide when one of Daggerfall's hostile actors notices the player: sight (range, field of view, line of sight),
/// hearing once it has noticed, and the classic Stealth check for an unseen player (Skyrim's Sneak standing for
/// Daggerfall's Stealth, sneaking for its sneak mode); how long it keeps looking once it has lost the player; and the
/// "There are enemies nearby" rule for resting, waiting and travel. Nothing global changes: Skyrim's detection settings
/// (fDetection*, iDetection*) and every other game's actors are untouched.
/// <list type="bullet">
/// <item>TMDfSenses on each hostile Daggerfall actor CreatureBuilder makes (its monsters, the City Watch, and the actors
/// standing for its enemy classes' leveled lists). Its encounter-list actors keep their template's script, so whatever
/// the list picks brings its own.</item>
/// <item>TMDfSensesControl, the quest "TMDfSensesQuest" (start-game enabled, at a fixed number in Daggerfall.esm): the
/// loaded enemies, the player's pace, the Sleep/Wait menu check, and EnemiesNearby for TMTravel.</item>
/// </list>
/// </summary>
public static class DfSenses
{
    public const string QuestEditorId = "TMDfSensesQuest";
    public const string ActorScript = "TMDfSenses";
    public const string ControlScript = "TMDfSensesControl";

    /// <summary>Fixed number in Daggerfall.esm: a record made in its running order would renumber everything after it
    /// (the dungeon and town plugins and saves use those FormIDs).</summary>
    const uint FixedId = 0xDF0300;

    /// <summary>Daggerfall's monsters that see through invisibility (Daggerfall Unity EnemyBasics: SeesThroughInvisibility):
    /// Imp, Skeletal Warrior, Ghost, Mummy, Wraith, Frost and Fire Daedra, Daedroth, Vampire, Daedra Seducer, Vampire
    /// Ancient, Daedra Lord, Lich.</summary>
    public static readonly HashSet<int> SeesThroughInvisibility = [1, 15, 18, 19, 23, 25, 26, 27, 28, 29, 30, 31, 32];

    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);

    /// <summary>The control quest in <paramref name="mod"/> (made on first call, at its fixed number).</summary>
    public static Quest Quest(SkyrimMod mod)
    {
        if (mod.Quests.FirstOrDefault(q => q.EditorID == QuestEditorId) is { } existing) return existing;
        var quest = new Quest(new FormKey(mod.ModKey, FixedId), SkyrimRelease.SkyrimSE)
        {
            EditorID = QuestEditorId,
            Name = "Daggerfall's enemy senses",
            Flags = Mutagen.Bethesda.Skyrim.Quest.Flag.StartGameEnabled,
            Aliases = [new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(Sk(0x14)) }],
        };
        quest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts =
            {
                new ScriptEntry
                {
                    Name = ControlScript,
                    Properties = [new ScriptObjectProperty { Name = "PlayerRef", Object = Sk(0x14).ToLink<ISkyrimMajorRecordGetter>() }],
                },
            },
            Aliases =
            [
                new QuestFragmentAlias
                {
                    Property = new ScriptObjectProperty { Object = quest.ToLink<ISkyrimMajorRecordGetter>(), Alias = 0 },
                    Scripts = [new ScriptEntry { Name = "TMDfSensesPlayer" }],
                },
            ],
        };
        mod.Quests.Add(quest);
        return quest;
    }

    /// <summary>The Iliac Bay location (DFLocIliacBay) on the control quest: the senses work only where it is the player's
    /// location (or a parent of it), so Daggerfall's creatures elsewhere (procedural creatures) keep Skyrim's own.</summary>
    public static void SetWorld(Quest quest, FormKey iliacBay)
    {
        var props = quest.VirtualMachineAdapter!.Scripts.First(s => s.Name == ControlScript).Properties;
        props.RemoveAll(p => p.Name == "IliacBay");
        props.Add(new ScriptObjectProperty { Name = "IliacBay", Object = iliacBay.ToLink<ISkyrimMajorRecordGetter>() });
    }

    /// <summary>
    /// Gives an actor TMDfSenses (replacing an earlier one) and makes its own scripts count: a templated actor whose
    /// template supplied its scripts (Use Script) takes the template's into its own first, so nothing it had is lost.
    /// </summary>
    public static void Attach(Npc npc, Quest quest, bool seesInvisible, Func<FormKey, INpcGetter?> resolve)
    {
        var flags = npc.Configuration.TemplateFlags;
        if (!npc.Template.IsNull && flags.HasFlag(NpcConfiguration.TemplateFlag.Script))
        {
            // The scripts the template chain gives (an actor's, not a leveled list's: a list's picks differ).
            var from = resolve(npc.Template.FormKey);
            for (int depth = 0; from is not null && depth < 8; depth++)
            {
                if (from.Template.IsNull || !from.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Script))
                {
                    if (from.VirtualMachineAdapter is { } vm && vm.Scripts.Count > 0)
                    {
                        npc.VirtualMachineAdapter ??= new VirtualMachineAdapter();
                        foreach (var s in vm.Scripts.Where(s => npc.VirtualMachineAdapter.Scripts.All(o => o.Name != s.Name)))
                            npc.VirtualMachineAdapter.Scripts.Add(s.DeepCopy());
                    }
                    break;
                }
                from = resolve(from.Template.FormKey);
            }
            npc.Configuration.TemplateFlags = flags & ~NpcConfiguration.TemplateFlag.Script;
        }
        npc.VirtualMachineAdapter ??= new VirtualMachineAdapter();
        npc.VirtualMachineAdapter.Scripts.RemoveAll(s => s.Name == ActorScript);
        npc.VirtualMachineAdapter.Scripts.Add(new ScriptEntry
        {
            Name = ActorScript,
            Properties =
            [
                new ScriptObjectProperty { Name = "Senses", Object = quest.ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptBoolProperty { Name = "SeesInvisible", Data = seesInvisible },
            ],
        });
    }
}
