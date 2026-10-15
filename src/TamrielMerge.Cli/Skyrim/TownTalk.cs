using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's town conversations in Skyrim's dialogue (rules from Daggerfall Unity's TalkManager, MIT; lines from
/// TEXT.RSC), on the "DFTalk" quest:
/// <list type="bullet">
/// <item>Each townsperson belongs to a Skyrim faction for their Daggerfall faction and one for their social group.</item>
/// <item>Reaction to the player per faction (DFReact_&lt;id&gt; = the faction's reputation plus the player's standing with
/// its social group, starting from FACTION.TXT) picks the greeting: Daggerfall's "very like / like / neutral /
/// dislike" lines, and "You get no response." below -20.</item>
/// <item>"Where is..." offers each kind of building the town has; the answer gives the compass direction to the nearest
/// (worked out by TMDfTalk when the question is asked), or, as often as Daggerfall's knowledge odds for the
/// speaker's social group say, a "don't know" line of that group.</item>
/// </list>
/// </summary>
public sealed class TownTalk(SkyrimMod mod, DfText text, DfFactions factions)
{
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey PlayerRef = Sk(0x14);

    /// <summary>The building kinds "Where is..." asks about, with how Daggerfall names them in conversation.</summary>
    public static readonly (DfBuildingType Type, string Label)[] Kinds =
    [
        (DfBuildingType.Tavern, "a tavern"), (DfBuildingType.GeneralStore, "a general store"), (DfBuildingType.Temple, "the temple"),
        (DfBuildingType.Alchemist, "an alchemist"), (DfBuildingType.Armorer, "an armorer"), (DfBuildingType.WeaponSmith, "a weaponsmith"),
        (DfBuildingType.Bank, "the bank"), (DfBuildingType.Bookseller, "a bookseller"), (DfBuildingType.ClothingStore, "a clothing store"),
        (DfBuildingType.FurnitureStore, "a furniture store"), (DfBuildingType.GemStore, "a gem store"), (DfBuildingType.PawnShop, "a pawn shop"),
        (DfBuildingType.Library, "the library"), (DfBuildingType.GuildHall, "a guild hall"), (DfBuildingType.Palace, "the palace"),
    ];

    static readonly string[] Directions = ["north", "north-east", "east", "south-east", "south", "south-west", "west", "north-west"];
    /// <summary>Chance (%) a speaker of each social group knows where a building is: Daggerfall's d20 &lt;= modifier + 10.</summary>
    static readonly int[] KnowChance = [75, 85, 50, 50, 70];
    /// <summary>"Don't know" lines (liked answers) by social group: commoners, merchants, scholars, nobles, underworld.</summary>
    static readonly int[] DontKnow = [7266, 7265, 7267, 7268, 7269];

    readonly Dictionary<int, Faction> dfFactions = new();
    readonly Faction[] groups = new Faction[5];
    readonly Dictionary<int, Global> reactions = new();
    Quest? quest;

    public int Responses { get; private set; }

    Faction GroupFaction(int g)
    {
        if (groups[g] is { } f) return f;
        // Shared with the quest machine (DfQuestBuilder makes the same records by EditorID).
        return groups[g] = DfQuestBuilder.GroupFaction(mod, g);
    }

    Faction DfFaction(DfFaction f)
    {
        if (dfFactions.TryGetValue(f.Id, out var hit)) return hit;
        // The player starts with each faction's reputation from FACTION.TXT and 0 with every social group. Shared with the
        // quest machine's reputation (DfQuestBuilder makes the same records by EditorID).
        var (faction, g) = DfQuestBuilder.FactionRecords(mod, f);
        reactions[f.Id] = g;
        return dfFactions[f.Id] = faction;
    }

    /// <summary>The Skyrim faction townsfolk of a Daggerfall faction belong to, if any were enlisted.</summary>
    public Faction? FactionOf(int dfId) => dfFactions.GetValueOrDefault(dfId);
    /// <summary>The player's standing with a Daggerfall faction (DFReact_&lt;id&gt;), if any of its people were enlisted.</summary>
    public Global? ReactionOf(int dfId) => reactions.GetValueOrDefault(dfId);

    /// <summary>Daggerfall's text macros for a line said to the player.</summary>
    static string Say(string line, string? key = null, string? direction = null)
    {
        var s = line.Replace("%pcf", "<Alias=Player>").Replace("%pcn", "<Alias=Player>").Replace("%oth", "By the gods").Replace("%ra", "outlander")
            .Replace("%hnr", "friend").Replace("%cn", "town").Replace("%reg", "this realm").Replace("%t %rn", "the king").Replace("%rn", "the king");
        if (key is not null) s = s.Replace("%key", key).Replace("%loc", key);
        if (direction is not null) s = s.Replace("%di", direction);
        s = Regex.Replace(s, @"%\w+", "").Replace("  ", " ").Trim();
        return s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : s;
    }

    static ConditionFloat Cond(ConditionData data, CompareOperator op, float value, bool or = false) =>
        new() { Data = data, CompareOperator = op, ComparisonValue = value, Flags = or ? Condition.Flag.OR : 0 };

    ConditionFloat InFaction(Faction f) { var d = new GetInFactionConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, f.FormKey); return Cond(d, CompareOperator.EqualTo, 1); }
    static ConditionFloat Global(IGlobalGetter g, CompareOperator op, float v, bool or = false) => Global(g.FormKey, op, v, or);
    static ConditionFloat Global(FormKey g, CompareOperator op, float v, bool or = false) { var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, g); return Cond(d, op, v, or); }
    static readonly FormKey GameMonth = Sk(0x36), GameDay = Sk(0x37);

    DialogResponses Line(string textLine, IEnumerable<ConditionFloat> conditions, bool random = true, bool goodbye = false)
    {
        var r = new DialogResponses(mod)
        {
            Responses = [new DialogResponse { Text = textLine, ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
            Flags = new DialogResponseFlags(),
            Conditions = [.. conditions],
        };
        if (random) r.Flags.Flags |= DialogResponses.Flag.Random;
        if (goodbye) r.Flags.Flags |= DialogResponses.Flag.Goodbye;
        Responses++;
        return r;
    }

    Faction? pickpockets;
    /// <summary>Puts each townsperson in their faction and social group; the underworld's people pick pockets
    /// (TMPickpockets, which finds DFPickpockets by name).</summary>
    public void Enlist(IEnumerable<TownPeople.Person> people)
    {
        foreach (var p in people)
        {
            if (p.Effective is { } eff) p.Npc.Factions.Add(new RankPlacement { Faction = DfFaction(eff).ToLink<IFactionGetter>(), Rank = 0 });
            int g = Math.Clamp((int)p.Group, 0, 4);
            p.Npc.Factions.Add(new RankPlacement { Faction = GroupFaction(g).ToLink<IFactionGetter>(), Rank = 0 });
            if (p.Group == DfSocialGroup.Underworld)
            {
                pickpockets ??= CoinTrade.Tag(mod, CoinTrade.PickpocketsId("DF"), "Pickpockets");
                p.Npc.Factions.Add(new RankPlacement { Faction = pickpockets.ToLink<IFactionGetter>(), Rank = 0 });
            }
        }
    }

    /// <summary>
    /// "I'd like to do some banking." to a banker: the Bank of the town's region (TMMoneyChanger with IsBank: the
    /// account, letters of credit and loans of Daggerfall Unity's DaggerfallBankManager, and changing other times' coin
    /// for 5%). A letter of credit is a weightless Letter of Credit item, one per gold piece of credit.
    /// </summary>
    void BuildBank(IReadOnlyList<TownBuilder.Built> towns, Faction bankers)
    {
        var letter = new MiscItem(mod)
        {
            EditorID = "DFLetterOfCredit",
            Name = "Letter of Credit",
            ObjectBounds = new ObjectBounds(),
            Model = new Model { File = @"Clutter\Books\Note01.nif" },
            Value = 0,
            Weight = 0,
        };
        mod.MiscItems.Add(letter);
        CoinTrade.AddExchange(mod, quest!, bankers.FormKey, story: 0, prefix: "DFBank", title: "The bank", baseFee: 5,
            ask: "I'd like to do some banking.", answer: "Of course. How may the bank serve you?",
            extra:
            [
                new ScriptBoolProperty { Name = "IsBank", Data = true },
                new ScriptObjectProperty { Name = "LetterOfCredit", Object = letter.ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptObjectProperty { Name = "Talk", Object = quest!.ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptIntListProperty { Name = "TownRegions", Data = [.. towns.Select(t => t.Region)] },
                new ScriptStringListProperty { Name = "RegionNames", Data = [.. DfLocations.RegionNames] },
            ]);
    }

    /// <summary>The conversation quest, greetings and "Where is..." for the built towns.</summary>
    public void Build(IReadOnlyList<TownBuilder.Built> towns, Quest talkQuest, Faction? shopkeepers = null, Faction? tavernKeepers = null, Faction? bankers = null)
    {
        quest = talkQuest;
        quest.Flags = Quest.Flag.StartGameEnabled | Quest.Flag.AllowRepeatedStages;
        quest.Priority = 45;
        quest.Type = Quest.TypeEnum.Misc;
        quest.Aliases.Add(new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(PlayerRef) });
        quest.NextAliasID = 1;

        // Directions data for TMDfTalk.
        var present = Kinds.Select(k => new GlobalFloat(mod) { EditorID = $"DFWhereHas{k.Type}", Data = 0 }).ToArray();
        var direction = Kinds.Select(k => new GlobalFloat(mod) { EditorID = $"DFWhereDir{k.Type}", Data = 0 }).ToArray();
        foreach (var g in present.Concat(direction)) mod.Globals.Add(g);
        FormList List(string edid, IEnumerable<FormKey> items)
        {
            var l = new FormList(mod) { EditorID = edid };
            foreach (var k in items) l.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(k));
            mod.FormLists.Add(l);
            return l;
        }
        var locations = List("DFTalkTownLocations", towns.Select(t => t.Location.FormKey));
        var byKind = new List<FormKey>();
        // Kinds a town hasn't got share one empty list.
        var none = List("DFTalkNoDoors", []);
        foreach (var t in towns)
        {
            string id = t.World.EditorID!;
            foreach (var (type, _) in Kinds)
                byKind.Add(t.DoorsByType.TryGetValue(type, out var ds) && ds.Count > 0 ? List($"DFTalk{id}Doors{type}", ds.Select(d => d.FormKey)).FormKey : none.FormKey);
        }
        static ScriptObjectProperty Obj(string name, FormKey k) => new() { Name = name, Object = k.ToLink<ISkyrimMajorRecordGetter>() };
        static ScriptObjectListProperty Objs(string name, IEnumerable<FormKey> keys)
        {
            var p = new ScriptObjectListProperty { Name = name };
            foreach (var k in keys) p.Objects.Add(new ScriptObjectProperty { Object = k.ToLink<ISkyrimMajorRecordGetter>() });
            return p;
        }
        quest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts =
            [
                new ScriptEntry
                {
                    Name = "TMDfTalk",
                    Properties =
                    [
                        Obj("TownLocations", locations.FormKey), Obj("DoorsByKind", List("DFTalkDoorsByKind", byKind).FormKey),
                        Objs("Present", present.Select(g => g.FormKey)), Objs("Direction", direction.Select(g => g.FormKey)),
                        new ScriptIntProperty { Name = "KindCount", Data = Kinds.Length }, Obj("PlayerRef", PlayerRef),
                    ],
                },
            ],
        };

        BuildGreetings();
        BuildWhereIs(present, direction);
        BuildNews();
        if (shopkeepers is not null) BuildBarter(shopkeepers);
        if (tavernKeepers is not null) BuildRooms(tavernKeepers);
        if (bankers is not null) BuildBank(towns, bankers);
    }

    /// <summary>"I'd like a room.": a tavern keeper rents a room for 1, 3 or 7 nights (TMDfTavern on the talk quest).</summary>
    void BuildRooms(Faction tavernKeepers)
    {
        var choice = new Message(mod)
        {
            EditorID = "DFTavernRentChoice",
            Description = "A room is %.0f gold for a night, %.0f for three nights, %.0f for a week.",
            Flags = Message.Flag.MessageBox,
            MenuButtons = [],
        };
        foreach (var b in new[] { "One night", "Three nights", "A week", "Never mind" }) choice.MenuButtons.Add(new MessageButton { Text = b });
        mod.Messages.Add(choice);
        quest!.VirtualMachineAdapter ??= new QuestAdapter();
        quest.VirtualMachineAdapter.Scripts.Add(new ScriptEntry
        {
            Name = "TMDfTavern",
            Properties =
            [
                new ScriptObjectProperty { Name = "RentChoice", Object = choice.ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptObjectProperty { Name = "Bed", Object = TownShops.Bed.ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptObjectProperty { Name = "GameMonth", Object = Sk(0x36).ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptObjectProperty { Name = "GameDay", Object = Sk(0x37).ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptObjectProperty { Name = "Ale", Object = Sk(0x034C5E).ToLink<ISkyrimMajorRecordGetter>() },
            ],
        });
        var branch = new DialogBranch(mod) { EditorID = "DFTalkRoomBranch", Quest = quest.ToLink<IQuestGetter>(), Category = DialogBranch.CategoryType.Player, Flags = DialogBranch.Flag.TopLevel };
        mod.DialogBranches.Add(branch);
        var room = Topic("DFTalkRoom", "I'd like a room.", branch, DialogTopic.SubtypeEnum.Custom, DialogTopic.CategoryEnum.Topic);
        branch.StartingTopic = room.ToNullableLink<IDialogTopicGetter>();
        var line = Line("How long will you be staying?", [InFaction(tavernKeepers)], random: false);
        line.VirtualMachineAdapter = new DialogResponsesAdapter
        {
            Scripts = [new ScriptEntry { Name = "TMDfTavernTopic", Properties = [new ScriptObjectProperty { Name = "Tavern", Object = quest.ToLink<ISkyrimMajorRecordGetter>() }] }],
            ScriptFragments = new ScriptFragments
            {
                ExtraBindDataVersion = 2,
                FileName = "TMDfTavernTopic",
                OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMDfTavernTopic", FragmentName = "Fragment_End" },
            },
        };
        room.Responses.Add(line);

        var drink = Topic("DFTalkDrink", "I'll have a drink.", branch, DialogTopic.SubtypeEnum.Custom, DialogTopic.CategoryEnum.Topic);
        var pour = Line("Here you are.", [InFaction(tavernKeepers)], random: false);
        pour.VirtualMachineAdapter = new DialogResponsesAdapter
        {
            Scripts = [new ScriptEntry { Name = "TMDfTavernTopic", Properties = [new ScriptObjectProperty { Name = "Tavern", Object = quest.ToLink<ISkyrimMajorRecordGetter>() }, new ScriptBoolProperty { Name = "Drink", Data = true }] }],
            ScriptFragments = new ScriptFragments
            {
                ExtraBindDataVersion = 2,
                FileName = "TMDfTavernTopic",
                OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMDfTavernTopic", FragmentName = "Fragment_End" },
            },
        };
        drink.Responses.Add(pour);
    }

    /// <summary>"Let me see your wares.": a shopkeeper opens the barter menu (TMDfBarter), or says the shop is closed.</summary>
    void BuildBarter(Faction shopkeepers)
    {
        var branch = new DialogBranch(mod) { EditorID = "DFTalkBarterBranch", Quest = quest!.ToLink<IQuestGetter>(), Category = DialogBranch.CategoryType.Player, Flags = DialogBranch.Flag.TopLevel };
        mod.DialogBranches.Add(branch);
        var barter = Topic("DFTalkBarter", "Let me see your wares.", branch, DialogTopic.SubtypeEnum.Custom, DialogTopic.CategoryEnum.Topic);
        branch.StartingTopic = barter.ToNullableLink<IDialogTopicGetter>();
        // Daggerfall's shops keep 6:00-18:00, and shut on Sun's Rest (20 Sun's Height).
        var open = Line("Take a look.", [InFaction(shopkeepers), Cond(new GetCurrentTimeConditionData(), CompareOperator.GreaterThanOrEqualTo, 6), Cond(new GetCurrentTimeConditionData(), CompareOperator.LessThan, 18),
            Global(GameMonth, CompareOperator.NotEqualTo, 6, or: true), Global(GameDay, CompareOperator.NotEqualTo, 20)], random: false);
        open.VirtualMachineAdapter = new DialogResponsesAdapter
        {
            Scripts = [new ScriptEntry { Name = "TMDfBarter" }],
            ScriptFragments = new ScriptFragments
            {
                ExtraBindDataVersion = 2,
                FileName = "TMDfBarter",
                OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMDfBarter", FragmentName = "Fragment_End" },
            },
        };
        barter.Responses.Add(open);
        barter.Responses.Add(Line("We're closed. Come back in the morning.", [InFaction(shopkeepers)], random: false, goodbye: true));
    }

    /// <summary>
    /// "Any news?": Daggerfall's general rumours (TEXT.RSC 1410-1418, 1450-1456; the faction-politics ones need its
    /// political simulation), each said by any townsperson at random, or "Hmmm. Can't think of any." (1457).
    /// </summary>
    void BuildNews()
    {
        var branch = new DialogBranch(mod) { EditorID = "DFTalkNewsBranch", Quest = quest!.ToLink<IQuestGetter>(), Category = DialogBranch.CategoryType.Player, Flags = DialogBranch.Flag.TopLevel };
        mod.DialogBranches.Add(branch);
        var news = Topic("DFTalkNews", "Any news?", branch, DialogTopic.SubtypeEnum.Custom, DialogTopic.CategoryEnum.Topic);
        branch.StartingTopic = news.ToNullableLink<IDialogTopicGetter>();
        var anyone = Enumerable.Range(0, 5).Select(g => Cond(GroupCondition(g), CompareOperator.EqualTo, 1, or: g < 4)).ToList();
        // Rumours that depend on events Daggerfall simulates (holidays, the Thieves Guild master's fate, a mad wizard) are left out.
        int[] rumours = [1410, 1417, 1418, 1450, 1451, 1452, 1453, 1454, 1455];
        foreach (int id in rumours)
            foreach (var line in text[id])
            {
                string said = Say(line.Replace("%n", "Someone").Replace("%lt1", "the lord"));
                if (said.Length > 0) news.Responses.Add(Line(said, [.. anyone, Cond(new GetRandomPercentConditionData(), CompareOperator.LessThan, 70)]));
            }
        foreach (var line in text[1457]) news.Responses.Add(Line(Say(line), anyone));
    }

    DialogTopic Topic(string edid, string? name, DialogBranch? branch, DialogTopic.SubtypeEnum subtype, DialogTopic.CategoryEnum category)
    {
        var t = new DialogTopic(mod)
        {
            EditorID = edid,
            Quest = quest!.ToNullableLink<IQuestGetter>(),
            Priority = 50,
            Category = category,
            Subtype = subtype,
            Responses = [],
        };
        if (name is not null) t.Name = name;
        if (branch is not null) t.Branch = branch.ToNullableLink<IDialogBranchGetter>();
        mod.DialogTopics.Add(t);
        return t;
    }

    /// <summary>Greetings by the speaker's faction's reaction to the player: very like (30+), like (10+), neutral (0+), dislike, none (below -20).</summary>
    void BuildGreetings()
    {
        var hello = Topic("DFTalkHello", null, null, DialogTopic.SubtypeEnum.Hello, DialogTopic.CategoryEnum.Misc);
        foreach (var (dfId, faction) in dfFactions)
        {
            var react = reactions[dfId];
            (CompareOperator Op, float Value, int TextId, bool Goodbye)[] bands =
            [
                (CompareOperator.LessThan, -20, 7205, true), (CompareOperator.LessThan, 0, 7206, false), (CompareOperator.LessThan, 10, 7207, false),
                (CompareOperator.LessThan, 30, 7208, false), (CompareOperator.GreaterThanOrEqualTo, 30, 7209, false),
            ];
            float below = float.NegativeInfinity;
            foreach (var (op, value, textId, goodbye) in bands)
            {
                foreach (var line in text[textId])
                {
                    var conds = new List<ConditionFloat> { InFaction(faction) };
                    if (op == CompareOperator.LessThan) conds.Add(Global(react, CompareOperator.LessThan, value));
                    else conds.Add(Global(react, CompareOperator.GreaterThanOrEqualTo, value));
                    if (op == CompareOperator.LessThan && !float.IsNegativeInfinity(below)) conds.Add(Global(react, CompareOperator.GreaterThanOrEqualTo, below));
                    hello.Responses.Add(Line(Say(line), conds, goodbye: goodbye));
                }
                if (op == CompareOperator.LessThan) below = value;
            }
        }
    }

    /// <summary>"Where is..." and a choice per kind of building present, answered with a direction or a group's "don't know".</summary>
    void BuildWhereIs(GlobalFloat[] present, GlobalFloat[] direction)
    {
        var branch = new DialogBranch(mod) { EditorID = "DFTalkWhereIsBranch", Quest = quest!.ToLink<IQuestGetter>(), Category = DialogBranch.CategoryType.Player, Flags = DialogBranch.Flag.TopLevel };
        mod.DialogBranches.Add(branch);
        var whereIs = Topic("DFTalkWhereIs", "Where is...", branch, DialogTopic.SubtypeEnum.Custom, DialogTopic.CategoryEnum.Topic);
        branch.StartingTopic = whereIs.ToNullableLink<IDialogTopicGetter>();
        var ask = Line("What are you looking for?", [InFaction(dfFactions.Values.First())], random: false);
        // Only Daggerfall townsfolk answer: any of the social group factions.
        ask.Conditions.Clear();
        for (int g = 0; g < 5; g++) ask.Conditions.Add(Cond(GroupCondition(g), CompareOperator.EqualTo, 1, or: g < 4));
        ask.VirtualMachineAdapter = new DialogResponsesAdapter
        {
            Scripts = [new ScriptEntry { Name = "TMDfWhereIs", Properties = [new ScriptObjectProperty { Name = "Talk", Object = quest.ToLink<ISkyrimMajorRecordGetter>() }] }],
            ScriptFragments = new ScriptFragments
            {
                ExtraBindDataVersion = 2,
                FileName = "TMDfWhereIs",
                OnBegin = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMDfWhereIs", FragmentName = "Fragment_Begin" },
            },
        };
        whereIs.Responses.Add(ask);

        var directionLines = text[7333];
        for (int k = 0; k < Kinds.Length; k++)
        {
            var (type, label) = Kinds[k];
            var choice = Topic($"DFTalkWhereIs{type}", char.ToUpperInvariant(label[0]) + label[1..], branch, DialogTopic.SubtypeEnum.Custom, DialogTopic.CategoryEnum.Topic);
            ask.LinkTo.Add(choice.ToLink<IDialogTopicGetter>());
            var has = Global(present[k], CompareOperator.EqualTo, 1);
            // Not known: a "don't know" line of the speaker's social group, as often as Daggerfall's odds.
            for (int g = 0; g < 5; g++)
                foreach (var line in text[DontKnow[g]])
                    choice.Responses.Add(Line(Say(line, label), [has, Cond(GroupCondition(g), CompareOperator.EqualTo, 1), Cond(new GetRandomPercentConditionData(), CompareOperator.GreaterThanOrEqualTo, KnowChance[g])]));
            // Known: the way there.
            for (int d = 0; d < 8; d++)
                foreach (var line in directionLines)
                    choice.Responses.Add(Line(Say(line, label, Directions[d]), [has, Global(direction[k], CompareOperator.EqualTo, d)]));
        }
    }

    GetInFactionConditionData GroupCondition(int g) { var d = new GetInFactionConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, GroupFaction(g).FormKey); return d; }
}
