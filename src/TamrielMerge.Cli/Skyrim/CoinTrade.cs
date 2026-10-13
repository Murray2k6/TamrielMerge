using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Coin between the times (docs/progression.md, "Coin by game"), for any game's plugin:
/// <list type="bullet">
/// <item>Money changers: "I'd like to exchange coin." to a member of the game's money changers opens the Tamriel menu's
/// Coin tab, where any other time's purse is changed into this time's coin for a fee (TMMoneyChanger on the game's
/// dialogue quest, TMExchangeTopic on the line). Daggerfall's bankers do it at their bank, with its accounts, letters of
/// credit and loans (<see cref="AddExchange"/> with bank properties).</item>
/// <item>Pickpockets: the game's thieves and beggars, tagged with a "{prefix}Pickpockets" faction the progression plugin
/// finds by name (TMPickpockets).</item>
/// </list>
/// </summary>
public static class CoinTrade
{
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    /// <summary>MQ101, which carries the story order (TMStoryOrder) and with it every time's purse.</summary>
    public static readonly FormKey StoryOrderQuest = Sk(0x03372B);

    /// <summary>A hidden faction marking people (money changers, pickpockets); nobody else sees it.</summary>
    public static Faction Tag(SkyrimMod mod, string edid, string name)
    {
        var f = new Faction(mod) { EditorID = edid, Name = name, Flags = Faction.FactionFlag.HiddenFromPC, Ranks = [new Rank { Number = 0 }] };
        mod.Factions.Add(f);
        return f;
    }

    public static string PickpocketsId(string prefix) => prefix + "Pickpockets";

    static ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };

    /// <summary>
    /// The exchange (or bank) topic on <paramref name="quest"/> for members of <paramref name="changers"/>, and the
    /// TMMoneyChanger script it opens, paying out <paramref name="story"/>'s coin (0 Daggerfall, 1 Redguard,
    /// 2 Morrowind, 3 Oblivion, 4 Skyrim). <paramref name="extra"/> adds script properties (a bank's).
    /// </summary>
    public static void AddExchange(SkyrimMod mod, Quest quest, FormKey changers, int story, string prefix, string title, int baseFee,
        string ask = "I'd like to exchange coin.", string answer = "Let's see what you have.", IEnumerable<ScriptProperty>? extra = null)
    {
        var entry = new ScriptEntry
        {
            Name = "TMMoneyChanger",
            Properties =
            [
                new ScriptIntProperty { Name = "Story", Data = story },
                Obj("StoryOrderQuest", StoryOrderQuest),
                new ScriptIntProperty { Name = "BaseFee", Data = baseFee },
                new ScriptStringProperty { Name = "Title", Data = title },
            ],
        };
        if (extra is not null) entry.Properties.AddRange(extra);
        quest.VirtualMachineAdapter ??= new QuestAdapter();
        quest.VirtualMachineAdapter.Scripts.Add(entry);

        var branch = new DialogBranch(mod) { EditorID = prefix + "ExchangeBranch", Quest = quest.ToLink<IQuestGetter>(), Category = DialogBranch.CategoryType.Player, Flags = DialogBranch.Flag.TopLevel };
        mod.DialogBranches.Add(branch);
        var topic = new DialogTopic(mod)
        {
            EditorID = prefix + "Exchange",
            Name = ask,
            Quest = quest.ToNullableLink<IQuestGetter>(),
            Branch = branch.ToNullableLink<IDialogBranchGetter>(),
            Priority = 50,
            Category = DialogTopic.CategoryEnum.Topic,
            Subtype = DialogTopic.SubtypeEnum.Custom,
            Responses = [],
        };
        mod.DialogTopics.Add(topic);
        branch.StartingTopic = topic.ToNullableLink<IDialogTopicGetter>();
        var inFaction = new GetInFactionConditionData();
        inFaction.Faction = new FormLinkOrIndex<IFactionGetter>(inFaction, changers);
        // Goodbye: the conversation closes and the menu opens when the line ends.
        var line = new DialogResponses(mod)
        {
            Responses = [new DialogResponse { Text = answer, ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
            Flags = new DialogResponseFlags { Flags = DialogResponses.Flag.Goodbye },
            Conditions = [new ConditionFloat { Data = inFaction, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 }],
            VirtualMachineAdapter = new DialogResponsesAdapter
            {
                Scripts = [new ScriptEntry { Name = "TMExchangeTopic", Properties = [Obj("Changer", quest.FormKey)] }],
                ScriptFragments = new ScriptFragments
                {
                    ExtraBindDataVersion = 2,
                    FileName = "TMExchangeTopic",
                    OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMExchangeTopic", FragmentName = "Fragment_End" },
                },
            },
        };
        topic.Responses.Add(line);
    }
}
