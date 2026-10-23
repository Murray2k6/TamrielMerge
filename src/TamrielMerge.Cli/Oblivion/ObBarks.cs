using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's combat, detection and misc topics as the Skyrim topics of the same moments, which Skyrim's engine says
/// at those moments as Oblivion's did: attacks, power attacks, hits, fleeing and accepting a yield, crimes seen (assault,
/// murder, theft, pickpocketing, trespass, each with and without a crime to report), the detection states (Noticed as
/// Skyrim's normal to alert, Seen as alert, normal or lost to combat, Unseen as combat to lost, Lost as lost or alert to
/// normal), corpses, idle remarks, combat watched and time to go. Each topic's responses keep Oblivion's conditions,
/// lines and recordings (<see cref="Response"/>); they are in the OBVoices quest, above Skyrim's own generic topics, and
/// only Oblivion's people (OBPeople) say them; at every moment Oblivion says nothing, or none of its lines holds, they say
/// nothing either (Skyrim.ForeignVoices), not Skyrim's lines. Yield and the service lines (Barter, BarterBuyItem,
/// BarterSellItem, BarterFail, BarterExit, Repair, Recharge, Training, ServiceRefusal) are moments Skyrim.esm has no
/// topic for, which its engine isn't known to say: they aren't made here.
/// </summary>
public sealed partial class ObPluginBuilder
{
    static readonly Dictionary<string, (DialogTopic.CategoryEnum Category, DialogTopic.SubtypeEnum[] Subtypes)> BarkTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Attack"] = (DialogTopic.CategoryEnum.Combat, [DialogTopic.SubtypeEnum.Attack]),
        ["PowerAttack"] = (DialogTopic.CategoryEnum.Combat, [DialogTopic.SubtypeEnum.PowerAttack]),
        ["Hit"] = (DialogTopic.CategoryEnum.Combat, [DialogTopic.SubtypeEnum.Hit]),
        ["Flee"] = (DialogTopic.CategoryEnum.Combat, [DialogTopic.SubtypeEnum.Flee]),
        ["AcceptYield"] = (DialogTopic.CategoryEnum.Combat, [DialogTopic.SubtypeEnum.AcceptYield]),
        ["Assault"] = (DialogTopic.CategoryEnum.Favors, [DialogTopic.SubtypeEnum.Assault]),
        ["AssaultNoCrime"] = (DialogTopic.CategoryEnum.Favors, [DialogTopic.SubtypeEnum.AssaultNC]),
        ["Murder"] = (DialogTopic.CategoryEnum.Favors, [DialogTopic.SubtypeEnum.Murder]),
        ["MurderNoCrime"] = (DialogTopic.CategoryEnum.Favors, [DialogTopic.SubtypeEnum.MurderNC]),
        ["Steal"] = (DialogTopic.CategoryEnum.Favors, [DialogTopic.SubtypeEnum.Steal]),
        ["StealNoCrime"] = (DialogTopic.CategoryEnum.Favors, [DialogTopic.SubtypeEnum.StealFromNC]),
        ["Pickpocket"] = (DialogTopic.CategoryEnum.Favors, [DialogTopic.SubtypeEnum.PickpocketCombat]),
        ["PickpocketNoCrime"] = (DialogTopic.CategoryEnum.Favors, [DialogTopic.SubtypeEnum.PickpocketNC]),
        ["Trespass"] = (DialogTopic.CategoryEnum.Favors, [DialogTopic.SubtypeEnum.Trespass]),
        ["TrespassNoCrime"] = (DialogTopic.CategoryEnum.Favors, [DialogTopic.SubtypeEnum.TrespassAgainstNC]),
        ["Noticed"] = (DialogTopic.CategoryEnum.Detection, [DialogTopic.SubtypeEnum.NormalToAlert]),
        ["Seen"] = (DialogTopic.CategoryEnum.Detection, [DialogTopic.SubtypeEnum.AlertToCombat, DialogTopic.SubtypeEnum.NormalToCombat, DialogTopic.SubtypeEnum.LostToCombat]),
        ["Unseen"] = (DialogTopic.CategoryEnum.Detection, [DialogTopic.SubtypeEnum.CombatToLost]),
        ["Lost"] = (DialogTopic.CategoryEnum.Detection, [DialogTopic.SubtypeEnum.LostToNormal, DialogTopic.SubtypeEnum.AlertToNormal]),
        ["Corpse"] = (DialogTopic.CategoryEnum.Misc, [DialogTopic.SubtypeEnum.NoticeCorpse]),
        ["Idle"] = (DialogTopic.CategoryEnum.Misc, [DialogTopic.SubtypeEnum.Idle]),
        ["ObserveCombat"] = (DialogTopic.CategoryEnum.Misc, [DialogTopic.SubtypeEnum.ObserveCombat]),
        ["TimeToGo"] = (DialogTopic.CategoryEnum.Misc, [DialogTopic.SubtypeEnum.TimeToGo]),
    };

    Faction? obPeople;
    public int BarkTopicsMade { get; private set; }
    public int BarkResponses { get; private set; }

    /// <summary>OBPeople: every converted Oblivion person and creature, whom the OBVoices topics are for.</summary>
    Faction ObPeople()
    {
        if (obPeople is not null) return obPeople;
        obPeople = new Faction(mod) { EditorID = "OBPeople", Name = "Oblivion's people", Flags = Faction.FactionFlag.HiddenFromPC, Ranks = [] };
        mod.Factions.Add(obPeople);
        return obPeople;
    }

    /// <summary>The bark topics (see the class summary), from the merged topics of every plugin.</summary>
    void BuildBarks(Dictionary<FormKey, (Ob.IDialogTopicGetter Topic, List<(Ob.IDialogItemGetter Item, Ob.IOblivionModGetter Source)> Items)> topics)
    {
        var people = ObPeople();
        foreach (var npc in actors.Values.OfType<Npc>().Distinct())
            if (!npc.Factions.Any(f => f.Faction.FormKey == people.FormKey))
                npc.Factions.Add(new RankPlacement { Faction = people.ToLink<IFactionGetter>(), Rank = 0 });
        var quest = new Quest(mod)
        {
            EditorID = "OBVoices",
            Name = "Oblivion Voices",
            Flags = Quest.Flag.StartGameEnabled,
            // Above Skyrim's own generic topics, so Oblivion's people say theirs first.
            Priority = 70,
            Type = Quest.TypeEnum.Misc,
        };
        mod.Quests.Add(quest);
        var own = new Dictionary<DialogTopic.SubtypeEnum, DialogTopic>();
        foreach (var (topic, items) in topics.Values)
        {
            if (topic.DialogType is Ob.DialogType.Topic or Ob.DialogType.Conversation or Ob.DialogType.Persuasion) continue;
            if (topic.EditorID is not { } name || !BarkTopics.TryGetValue(name, out var kind)) continue;
            foreach (var subtype in kind.Subtypes)
            {
                var bark = new DialogTopic(mod)
                {
                    EditorID = "OBVoice" + Sanitize(name) + (kind.Subtypes.Length > 1 ? subtype.ToString() : ""),
                    Quest = quest.ToNullableLink<IQuestGetter>(),
                    Priority = 50,
                    Category = kind.Category,
                    Subtype = subtype,
                    Responses = [],
                };
                foreach (var (item, source) in items)
                {
                    if (Response(item, source) is not { } r) continue;
                    var d = new GetInFactionConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, people.FormKey);
                    r.Conditions.Insert(0, Cond(d, CompareOperator.EqualTo, 1, false, false));
                    responseTopics[r] = name;
                    bark.Responses.Add(r);
                    BarkResponses++;
                }
                if (bark.Responses.Count == 0) continue;
                mod.DialogTopics.Add(bark);
                own[subtype] = bark;
                BarkTopicsMade++;
            }
        }
        // Skyrim's own lines kept from Oblivion's people: a silent one at every moment Oblivion has none for.
        SkyrimMomentsSilenced = Skyrim.ForeignVoices.Silence(mod, SkyrimEsm, quest, people.FormKey, "OB", own);
    }

    public int SkyrimMomentsSilenced { get; private set; }
}
