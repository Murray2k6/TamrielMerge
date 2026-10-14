using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's dialogue and journal as Skyrim's:
/// <list type="bullet">
/// <item>Each journal topic (DIAL type 4) is a quest named by its QSTN entry, with a stage per journal index whose log
/// entry is that index's text (QSTF ends the quest there).</item>
/// <item>Each topic (type 0) is a top-level player topic of the "MWDialogue" quest (start-game enabled, with a Player
/// alias for %PCName), its responses in Morrowind's order (the first that applies is said, in both games).</item>
/// <item>Greetings (type 2) are Hello topics.</item>
/// <item>Filters and conditions become Skyrim conditions: speaker, race, class, faction and rank, cell, sex,
/// the player's faction and rank, journal index (GetStage), globals, items carried, deaths, talked-to-PC, random,
/// levels, skills, health, the speaker's script locals. Conditions on things Skyrim doesn't track (expulsion,
/// vampirism, weather) are judged as Morrowind would with their usual value, 0.</item>
/// <item>A response offering choices (Choice "text" n in its result) links to a child topic per choice holding the
/// topic's responses for that choice (function 50 = n).</item>
/// <item>Result scripts are translated to Papyrus like the game's other scripts (MWR_ fragments, docs/morrowind-scripts.md);
/// where scripts aren't converted or one doesn't compile, journal updates, globals, items given or taken, joining a
/// faction or rising in it, combat and Goodbye run from TMMwInfo (one shared TopicInfo script, per-response properties).
/// Conditions on the speaker's script locals read the hidden faction ranks the translated scripts mirror them in.</item>
/// </list>
/// Morrowind's persuasion and voice topics (barks) are not converted here.
/// </summary>
public sealed partial class MwPluginBuilder
{
    static readonly FormKey PlayerRefKey = new(ModKey.FromFileName("Skyrim.esm"), 0x14);
    const string InfoScript = "TMMwInfo";

    MwDialogue? dialogueData;
    readonly Dictionary<string, Faction?> factions = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Class?> classes = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Global?> globals = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Quest> journals = new(StringComparer.OrdinalIgnoreCase);
    Quest? dialogueQuest;
    public int DialogueTopics { get; private set; }
    public int DialogueResponses { get; private set; }
    public int DialogueLines { get; private set; }
    public int JournalQuests => journals.Count;

    // ---- Factions, classes, globals ----

    /// <summary>A Morrowind faction (FACT) as a Skyrim faction with its rank titles (RNAM).</summary>
    Faction? MwFaction(string id)
    {
        if (factions.TryGetValue(id, out var hit)) return hit;
        if (!world.Objects.TryGetValue(id, out var rec) || rec.Type != "FACT") return factions[id] = null;
        var faction = new Faction(mod) { EditorID = "MWFaction" + Sanitize(id), Name = rec.Str("FNAM") ?? id, Ranks = [] };
        uint n = 0;
        foreach (var r in rec.All("RNAM"))
        {
            string title = r.Text;
            if (title.Length == 0) continue;
            faction.Ranks.Add(new Rank { Number = n++, Title = new GenderedItem<Mutagen.Bethesda.Strings.TranslatedString>(title, title) });
        }
        // FADT: attributes (8), ranks (10 x 20), skills (7 x 4), then flags: 1 = hidden from the player (not joinable).
        if (rec.Get("FADT") is { Data.Length: >= 240 } fadt && (fadt.Int(236) & 1) != 0) faction.Flags |= Faction.FactionFlag.HiddenFromPC;
        mod.Factions.Add(faction);
        return factions[id] = faction;
    }

    /// <summary>A Morrowind class (CLAS) as a Skyrim class, based on a vanilla one for its skill weights.</summary>
    Class? MwClass(string id)
    {
        if (classes.TryGetValue(id, out var hit)) return hit;
        if (!world.Objects.TryGetValue(id, out var rec) || rec.Type != "CLAS") return classes[id] = null;
        VanillaNpc("EncBandit");   // opens the masters
        var template = skyrimEsm!.Classes.FirstOrDefault(c => c.EditorID == "CombatWarrior1H") ?? skyrimEsm.Classes.First();
        var cls = mod.Classes.DuplicateInAsNewRecord(template);
        cls.EditorID = "MWClass" + Sanitize(id);
        cls.Name = rec.Str("FNAM") ?? id;
        return classes[id] = cls;
    }

    /// <summary>A Morrowind global (GLOB) as a Skyrim global with its starting value.</summary>
    Global? MwGlobal(string id)
    {
        if (globals.TryGetValue(id, out var hit)) return hit;
        // Globals have their own namespace: Tribunal has globals named like scripts (scriptWorld keeps them).
        if (!world.Objects.TryGetValue(id, out var rec) || rec.Type != "GLOB")
            if (scriptWorld?.Globals.TryGetValue(id, out rec) != true) return globals[id] = null;
        var g = new GlobalFloat(mod) { EditorID = "MWGlobal" + Sanitize(id), Data = rec.Get("FLTV")?.Float() ?? 0 };
        mod.Globals.Add(g);
        return globals[id] = g;
    }

    /// <summary>An NPC's faction and rank and class (Person calls this once the NPC exists).</summary>
    void AddFactionAndClass(Npc npc, Tes3Record rec)
    {
        var npdt = rec.Get("NPDT")?.Data;
        int rank = npdt is null ? 0 : npdt.Length == 12 ? npdt[4] : npdt.Length > 46 ? npdt[46] : 0;
        if (rec.Str("ANAM") is { Length: > 0 } fid && MwFaction(fid) is { } faction)
            npc.Factions.Add(new RankPlacement { Faction = faction.ToLink<IFactionGetter>(), Rank = (sbyte)Math.Clamp(rank, 0, 9) });
        if (rec.Str("CNAM") is { Length: > 0 } cid && MwClass(cid) is { } cls) npc.Class.SetTo(cls);
        // Coin (CoinTrade): whoever barters misc goods (AIDT services 0x400: pawnbrokers, traders, general merchants)
        // changes coin; thieves, paupers and the Thieves Guild pick pockets.
        if (rec.Get("AIDT") is { Data.Length: >= 12 } aidt && (aidt.Int(8) & 0x400) != 0)
            npc.Factions.Add(new RankPlacement { Faction = (moneyChangers ??= Skyrim.CoinTrade.Tag(mod, "MWMoneyChangers", "Money changers")).ToLink<IFactionGetter>(), Rank = 0 });
        string who = ((rec.Str("CNAM") ?? "") + " " + (rec.Str("ANAM") ?? "")).ToLowerInvariant();
        // Guards may doze on night watch (TMNightDanger).
        if (who.Contains("guard"))
            npc.Factions.Add(new RankPlacement { Faction = Skyrim.TownNights.Guards(mod, "MW").ToLink<IFactionGetter>(), Rank = 0 });
        if (who.Contains("thief") || who.Contains("thieves") || who.Contains("pauper"))
            npc.Factions.Add(new RankPlacement { Faction = (pickpockets ??= Skyrim.CoinTrade.Tag(mod, Skyrim.CoinTrade.PickpocketsId("MW"), "Pickpockets")).ToLink<IFactionGetter>(), Rank = 0 });
    }
    Faction? moneyChangers, pickpockets;

    // ---- Building ----

    /// <summary>Converts the journal and dialogue. Call after interiors, exteriors and actors exist.</summary>
    public void BuildDialogue()
    {
        dialogueData ??= MwDialogue.Load(world.Plugins);
        dialogueQuest = new Quest(mod)
        {
            EditorID = "MWDialogue",
            Name = "Morrowind Dialogue",
            Flags = Quest.Flag.StartGameEnabled | Quest.Flag.AllowRepeatedStages,
            Priority = 40,
            Type = Quest.TypeEnum.Misc,
            Aliases = [new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(PlayerRefKey) }],
            NextAliasID = 1,
        };
        mod.Quests.Add(dialogueQuest);

        foreach (var topic in dialogueData.Topics.Values.Where(t => t.Type == MwDialogue.TopicType.Journal)) Journal(topic);
        foreach (var topic in dialogueData.Topics.Values.Where(t => t.Type == MwDialogue.TopicType.Topic)) Topic(topic, greeting: false);
        foreach (var topic in dialogueData.Topics.Values.Where(t => t.Type == MwDialogue.TopicType.Greeting)) Topic(topic, greeting: true);
        Persuasion();
        // Money changers (CoinTrade): the faction is shared with the people who join it, made now or later.
        moneyChangers ??= Skyrim.CoinTrade.Tag(mod, "MWMoneyChangers", "Money changers");
        Skyrim.CoinTrade.AddExchange(mod, dialogueQuest, moneyChangers.FormKey, story: 2, prefix: "MW", title: "Money changer", baseFee: 10);
    }

    // ---- Persuasion ----

    /// <summary>
    /// Morrowind's persuasion menu as a "Persuasion" topic whose choices are Admire, Intimidate, Taunt and the three
    /// bribes. Each attempt succeeds on a roll (0-99, drawn by the response before it, so all of an attempt's responses
    /// see the same one) under a chance set by the player's Speech; the speaker answers with Morrowind's own success or
    /// fail lines (the persuasion topics "Admire Success" ...), and the disposition changes as in Morrowind: admiring
    /// and bribing raise it, intimidating raises it on success, taunting lowers it. Bribes need the gold and take it on
    /// success. After an attempt the choices are offered again.
    /// </summary>
    void Persuasion()
    {
        if (dialogueData is null) return;
        var roll = new GlobalFloat(mod) { EditorID = "MWPersuasionRoll", Data = 50 };
        mod.Globals.Add(roll);
        var branch = new DialogBranch(mod)
        {
            EditorID = "MWPersuasionBranch",
            Quest = dialogueQuest!.ToLink<IQuestGetter>(),
            Category = DialogBranch.CategoryType.Player,
            Flags = DialogBranch.Flag.TopLevel,
        };
        mod.DialogBranches.Add(branch);
        DialogTopic NewTopic(string edid, string name) => new(mod)
        {
            EditorID = edid,
            Name = name,
            Quest = dialogueQuest!.ToNullableLink<IQuestGetter>(),
            Branch = branch.ToNullableLink<IDialogBranchGetter>(),
            Priority = 50,
            Category = DialogTopic.CategoryEnum.Topic,
            Subtype = DialogTopic.SubtypeEnum.Custom,
            Responses = [],
        };
        var menu = NewTopic("MWPersuasion", "Persuasion");
        branch.StartingTopic = menu.ToNullableLink<IDialogTopicGetter>();

        // (choice, success topic, fail topic, gold, disposition on success, on fail, chance bonus)
        (string Name, string Success, string Fail, int Gold, int Win, int Lose, int Bonus)[] actions =
        [
            ("Admire", "Admire Success", "Admire Fail", 0, 8, -3, 0),
            ("Intimidate", "Intimidate Success", "Intimidate Fail", 0, 10, -6, -5),
            ("Taunt", "Taunt Success", "Taunt Fail", 0, -10, -2, 0),
            ("Bribe (10 gold)", "Bribe Success", "Bribe Fail", 10, 4, -1, 10),
            ("Bribe (100 gold)", "Bribe Success", "Bribe Fail", 100, 12, -2, 25),
            ("Bribe (1000 gold)", "Bribe Success", "Bribe Fail", 1000, 30, -3, 40),
        ];
        // The chance by Speech: (at least this Speech, chance %), best first.
        (int Speech, int Chance)[] bands = [(75, 80), (50, 60), (25, 45), (0, 30)];
        var choices = new List<DialogTopic>();
        var none = new Dictionary<(int, string), DialogTopic>();
        foreach (var (name, successTopic, failTopic, gold, win, lose, bonus) in actions)
        {
            var choice = NewTopic("MWPersuade" + Sanitize(name), name);
            choices.Add(choice);
            ConditionFloat HasGold()
            {
                var d = new GetItemCountConditionData(); d.ItemOrList = new FormLinkOrIndex<IItemOrListGetter>(d, Sk(0x00000F));
                return Cond(d, CompareOperator.GreaterThanOrEqualTo, gold, player: true);
            }
            void Add(MwDialogue.Topic source, IEnumerable<ConditionFloat> extra, int change, int takes)
            {
                foreach (var info in source.Infos)
                {
                    if (Response(info, choice, none, source, branch) is not { } r) continue;
                    r.Conditions.AddRange(extra);
                    if (gold > 0) r.Conditions.Add(HasGold());
                    var entry = InfoEntry(r);
                    entry.Properties.RemoveAll(p => p.Name is "Disposition" or "SetsDisposition");
                    if (change != 0) entry.Properties.Add(new ScriptIntProperty { Name = "Disposition", Data = change });
                    if (takes > 0) entry.Properties.Add(new ScriptIntProperty { Name = "Gold", Data = takes });
                    entry.Properties.Add(new ScriptObjectProperty { Name = "Roll", Object = roll.ToLink<ISkyrimMajorRecordGetter>() });
                    choice.Responses.Add(r);
                }
            }
            if (dialogueData.Topics.TryGetValue(successTopic, out var success))
                foreach (var (speech, chance) in bands)
                {
                    var speechCond = Cond(new GetActorValueConditionData { ActorValue = ActorValue.Speech }, CompareOperator.GreaterThanOrEqualTo, speech, player: true);
                    var rollData = new GetGlobalValueConditionData(); rollData.Global = new FormLinkOrIndex<IGlobalGetter>(rollData, roll.FormKey);
                    var rollCond = Cond(rollData, CompareOperator.LessThan, Math.Clamp(chance + bonus, 5, 95));
                    Add(success, [speechCond, rollCond], win, gold);
                }
            if (dialogueData.Topics.TryGetValue(failTopic, out var fail)) Add(fail, [], lose, 0);
            if (choice.Responses.Count == 0) { choices.Remove(choice); continue; }
            mod.DialogTopics.Add(choice);
        }
        if (choices.Count == 0) { mod.DialogBranches.Remove(branch); return; }
        // Choosing "Persuasion" draws the first roll and offers the choices; each attempt offers them again.
        var open = new DialogResponses(mod) { Responses = [new DialogResponse { Text = "", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }], Flags = new DialogResponseFlags(), Conditions = [] };
        InfoEntry(open).Properties.Add(new ScriptObjectProperty { Name = "Roll", Object = roll.ToLink<ISkyrimMajorRecordGetter>() });
        foreach (var c in choices) open.LinkTo.Add(c.ToLink<IDialogTopicGetter>());
        menu.Responses.Add(open);
        mod.DialogTopics.Add(menu);
        // After an attempt the choices come back.
        foreach (var c in choices)
            foreach (var r in c.Responses) { r.LinkTo.Clear(); foreach (var other in choices) r.LinkTo.Add(other.ToLink<IDialogTopicGetter>()); }
        DialogueTopics += choices.Count + 1;
    }

    /// <summary>A response's TMMwInfo entry, adding the script (and its fragment) when it has none.</summary>
    static ScriptEntry InfoEntry(DialogResponses r)
    {
        // TMMwInfo, or a translated result script (MWR_…), which extends it.
        if (r.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == InfoScript || s.Name.StartsWith("MWR_", StringComparison.Ordinal)) is { } existing) return existing;
        var entry = new ScriptEntry { Name = InfoScript };
        r.VirtualMachineAdapter = new DialogResponsesAdapter
        {
            Scripts = [entry],
            ScriptFragments = new ScriptFragments
            {
                ExtraBindDataVersion = 2,
                FileName = InfoScript,
                OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = InfoScript, FragmentName = "Fragment_End" },
            },
        };
        return entry;
    }

    /// <summary>A journal topic as a quest: its name entry (QSTN) names it, every other entry is a stage's log entry.</summary>
    void Journal(MwDialogue.Topic topic)
    {
        var quest = new Quest(mod)
        {
            EditorID = "MWQ" + Sanitize(topic.Name),
            Name = topic.Infos.FirstOrDefault(i => i.QuestName)?.Text ?? topic.Name,
            Priority = 50,
            Type = topic.Name.StartsWith("A1_") || topic.Name.StartsWith("A2_") || topic.Name.StartsWith("B") && topic.Name.Length > 2 && char.IsDigit(topic.Name[1]) || topic.Name.StartsWith("C3_")
                ? Quest.TypeEnum.MainQuest : Quest.TypeEnum.Misc,
            Stages = [],
        };
        foreach (var info in topic.Infos.Where(i => !i.QuestName).OrderBy(i => i.JournalIndex))
        {
            if (quest.Stages.Any(s => s.Index == info.JournalIndex)) continue;
            quest.Stages.Add(new QuestStage
            {
                Index = (ushort)Math.Clamp(info.JournalIndex, 0, ushort.MaxValue),
                LogEntries =
                [
                    new QuestLogEntry
                    {
                        Entry = Substitute(info.Text, info),
                        Flags = info.QuestFinished ? QuestLogEntry.Flag.CompleteQuest : null,
                    },
                ],
            });
        }
        mod.Quests.Add(quest);
        journals[topic.Name] = quest;
    }

    /// <summary>A topic (top-level player topic) or the greetings (Hello), with their choices' child topics.</summary>
    void Topic(MwDialogue.Topic topic, bool greeting)
    {
        string edid = (greeting ? "MWHello" : "MWTopic") + Sanitize(topic.Name);
        DialogBranch? branch = null;
        var main = new DialogTopic(mod)
        {
            EditorID = edid,
            Quest = dialogueQuest!.ToNullableLink<IQuestGetter>(),
            Priority = 50,
            Category = greeting ? DialogTopic.CategoryEnum.Misc : DialogTopic.CategoryEnum.Topic,
            Subtype = greeting ? DialogTopic.SubtypeEnum.Hello : DialogTopic.SubtypeEnum.Custom,
            Responses = [],
        };
        if (!greeting)
        {
            main.Name = char.ToUpperInvariant(topic.Name[0]) + topic.Name[1..];
            branch = new DialogBranch(mod)
            {
                EditorID = edid + "Branch",
                Quest = dialogueQuest.ToLink<IQuestGetter>(),
                Category = DialogBranch.CategoryType.Player,
                Flags = DialogBranch.Flag.TopLevel,
                StartingTopic = main.ToNullableLink<IDialogTopicGetter>(),
            };
            main.Branch = branch.ToNullableLink<IDialogBranchGetter>();
            mod.DialogBranches.Add(branch);
        }

        // Responses for a choice (function 50 = n) go to that choice's child topics; the rest to the topic itself.
        static int? ChoiceOf(MwDialogue.Info i) => i.Conditions.FirstOrDefault(c => c.Kind == '1' && c.Function == "50") is { } c ? (int)c.Value : null;
        var children = new Dictionary<(int Number, string Text), DialogTopic>();
        foreach (var info in topic.Infos.Where(i => ChoiceOf(i) is null))
            if (Response(info, main, children, topic, branch) is { } r) main.Responses.Add(r);
        if (main.Responses.Count == 0) { if (branch is not null) mod.DialogBranches.Remove(branch); return; }
        mod.DialogTopics.Add(main);
        DialogueTopics++;
    }

    /// <summary>One response, or null when it can never be said here (a filter on something not converted).</summary>
    DialogResponses? Response(MwDialogue.Info info, DialogTopic owner, Dictionary<(int Number, string Text), DialogTopic> children, MwDialogue.Topic topic, DialogBranch? branch)
    {
        if (Conditions(info) is not { } conditions) return null;
        var r = new DialogResponses(mod) { Conditions = conditions, Responses = [], Flags = new DialogResponseFlags() };
        byte n = 1;
        foreach (var line in Lines(Substitute(info.Text, info)))
            r.Responses.Add(new DialogResponse { Text = line, ResponseNumber = n++, Emotion = Emotion.Neutral, EmotionValue = 50 });
        if (r.Responses.Count == 0) r.Responses.Add(new DialogResponse { Text = "", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 });
        DialogueLines += r.Responses.Count;
        DialogueResponses++;
        var result = ParseResult(info);
        if (result.Goodbye || ResultSaysGoodbye(info)) r.Flags.Flags |= DialogResponses.Flag.Goodbye;
        // The translated result script (MWR_…), with TMMwInfo's fixed set as its fallback.
        if (ResultAdapter(info, r, result.Adapter) is { } vm) r.VirtualMachineAdapter = vm;
        // Choices: a child topic per (choice, prompt), holding the topic's responses for that choice.
        foreach (var (text, number) in result.Choices)
        {
            if (!children.TryGetValue((number, text), out var child))
            {
                var childBranch = branch;
                if (childBranch is null)
                {
                    childBranch = new DialogBranch(mod)
                    {
                        EditorID = owner.EditorID + "Choices",
                        Quest = dialogueQuest!.ToLink<IQuestGetter>(),
                        Category = DialogBranch.CategoryType.Player,
                        Flags = 0,
                    };
                    mod.DialogBranches.Add(childBranch);
                    branch = childBranch;
                }
                child = new DialogTopic(mod)
                {
                    EditorID = $"{owner.EditorID}Choice{number}_{children.Count}",
                    Name = text,
                    Quest = dialogueQuest!.ToNullableLink<IQuestGetter>(),
                    Branch = childBranch.ToNullableLink<IDialogBranchGetter>(),
                    Priority = 50,
                    Category = DialogTopic.CategoryEnum.Topic,
                    Subtype = DialogTopic.SubtypeEnum.Custom,
                    Responses = [],
                };
                children[(number, text)] = child;
                foreach (var answer in topic.Infos.Where(i => i.Conditions.Any(c => c.Kind == '1' && c.Function == "50" && (int)c.Value == number)))
                    if (Response(answer, child, children, topic, branch) is { } a) child.Responses.Add(a);
                if (child.Responses.Count == 0)
                    child.Responses.Add(new DialogResponses(mod) { Responses = [new DialogResponse { Text = "", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }], Flags = new DialogResponseFlags() });
                mod.DialogTopics.Add(child);
            }
            r.LinkTo.Add(child.ToLink<IDialogTopicGetter>());
        }
        return r;
    }

    // ---- Text ----

    /// <summary>Morrowind's text variables, as far as Skyrim can show them.</summary>
    string Substitute(string text, MwDialogue.Info info)
    {
        // A response for one speaker knows who is talking: their name, race, class and faction.
        var speaker = info.Actor is { } a && world.Objects.TryGetValue(a, out var npcRec) && npcRec.Type == "NPC_" ? npcRec : null;
        string? Named(string? id) => id is not null && world.Objects.TryGetValue(id, out var r) ? r.Str("FNAM") ?? id : id;
        string factionName = Named(info.Faction ?? speaker?.Str("ANAM")) ?? "the guild";
        string name = speaker?.Str("FNAM") ?? "I";
        string race = Named(info.Race ?? speaker?.Str("RNAM")) ?? "my people";
        string cls = Named(info.Class ?? speaker?.Str("CNAM")) ?? "worker";
        var t = Regex.Replace(text, "%PCName", "<Alias=Player>", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "%PCRace", "outlander", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "%PCClass", "adventurer", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "%PCRank|%NextPCRank", "member", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "%Faction", factionName, RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "%Rank", "member", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "%Race", race, RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "%Class", cls, RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "%Name", name, RegexOptions.IgnoreCase);
        t = Regex.Replace(t, "%Cell", info.Cell ?? "here", RegexOptions.IgnoreCase);
        return t.Replace("\r", "").Replace("\n", " ").Trim();
    }

    /// <summary>Skyrim response lines hold about 150 characters: whole sentences per line, long ones cut at a space.</summary>
    public static IEnumerable<string> Lines(string text, int max = 149)
    {
        var sentences = Regex.Split(text, @"(?<=[.!?])\s+").Where(s => s.Length > 0);
        var line = "";
        foreach (var sentence in sentences)
        {
            var s = sentence;
            while (s.Length > max)
            {
                if (line.Length > 0) { yield return line; line = ""; }
                int cut = s.LastIndexOf(' ', max);
                if (cut <= 0) cut = max;
                yield return s[..cut].Trim();
                s = s[cut..].Trim();
            }
            if (line.Length + s.Length + 1 > max) { yield return line; line = ""; }
            line = line.Length == 0 ? s : line + " " + s;
        }
        if (line.Length > 0) yield return line;
    }

    // ---- Conditions ----

    static ConditionFloat Cond(ConditionData data, CompareOperator op, float value, bool player = false, bool or = false)
    {
        if (player)
        {
            data.RunOnType = Condition.RunOnType.Reference;
            data.Reference = new FormLink<ISkyrimMajorRecordGetter>(PlayerRefKey);
        }
        return new ConditionFloat { Data = data, CompareOperator = op, ComparisonValue = value, Flags = or ? Condition.Flag.OR : 0 };
    }

    static bool Holds(int compare, float lhs, float rhs) => compare switch
    {
        0 => lhs == rhs, 1 => lhs != rhs, 2 => lhs > rhs, 3 => lhs >= rhs, 4 => lhs < rhs, 5 => lhs <= rhs, _ => true,
    };

    IEnumerable<Cell> CellsNamed(string name) =>
        interiorsByName.Where(kv => kv.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value);

    /// <summary>
    /// A response's filters and conditions, or null if it can never apply (it names a speaker, race, class, faction
    /// or item that wasn't converted, or a condition Skyrim can't test that fails with its usual value).
    /// </summary>
    ExtendedList<Condition>? Conditions(MwDialogue.Info info)
    {
        var list = new ExtendedList<Condition>();
        if (info.Actor is { } actor)
        {
            if (Actor(actor) is not Npc npc) return null;
            var d = new GetIsIDConditionData(); d.Object = new FormLinkOrIndex<IReferenceableObjectGetter>(d, npc.FormKey);
            list.Add(Cond(d, CompareOperator.EqualTo, 1));
        }
        if (info.Race is { } race)
        {
            if (!MwRaces.TryGetValue(race, out var skyRace)) return null;
            VanillaNpc("EncBandit");
            var r = skyrimEsm!.Races.FirstOrDefault(x => x.EditorID == skyRace);
            if (r is null) return null;
            var d = new GetIsRaceConditionData(); d.Race = new FormLinkOrIndex<IRaceGetter>(d, r.FormKey);
            list.Add(Cond(d, CompareOperator.EqualTo, 1));
        }
        if (info.Class is { } cls)
        {
            if (MwClass(cls) is not { } c) return null;
            var d = new GetIsClassConditionData(); d.Class = new FormLinkOrIndex<IClassGetter>(d, c.FormKey);
            list.Add(Cond(d, CompareOperator.EqualTo, 1));
        }
        if (info.Faction is { } fac)
        {
            if (MwFaction(fac) is not { } f) return null;
            var d = new GetInFactionConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, f.FormKey);
            list.Add(Cond(d, CompareOperator.EqualTo, 1));
            if (info.Rank > 0)
            {
                var rd = new GetFactionRankConditionData(); rd.Faction = new FormLinkOrIndex<IFactionGetter>(rd, f.FormKey);
                list.Add(Cond(rd, CompareOperator.GreaterThanOrEqualTo, info.Rank));
            }
        }
        if (info.Cell is { } cellName)
        {
            var cells = CellsNamed(cellName).ToList();
            for (int i = 0; i < cells.Count; i++)
            {
                var d = new GetInCellConditionData(); d.Cell = new FormLinkOrIndex<ICellGetter>(d, cells[i].FormKey);
                list.Add(Cond(d, CompareOperator.EqualTo, 1, or: i < cells.Count - 1));
            }
        }
        if (info.PcFaction is { } pcFac)
        {
            if (MwFaction(pcFac) is not { } f) return null;
            var d = new GetInFactionConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, f.FormKey);
            list.Add(Cond(d, CompareOperator.EqualTo, 1, player: true));
            if (info.PcRank > 0)
            {
                var rd = new GetFactionRankConditionData(); rd.Faction = new FormLinkOrIndex<IFactionGetter>(rd, f.FormKey);
                list.Add(Cond(rd, CompareOperator.GreaterThanOrEqualTo, info.PcRank, player: true));
            }
        }
        if (info.Sex is 0 or 1)
            list.Add(Cond(new GetIsSexConditionData { MaleFemaleGender = info.Sex == 1 ? MaleFemaleGender.Female : MaleFemaleGender.Male }, CompareOperator.EqualTo, 1));
        // The speaker's disposition must be at least the response's (only topics, greetings and persuasion come here;
        // journal entries use the same field for their index).
        if (info.Disposition > 0)
            list.Add(Skyrim.Disposition.Condition(CompareOperator.GreaterThanOrEqualTo, info.Disposition));

        foreach (var c in info.Conditions)
        {
            var op = (CompareOperator)Math.Clamp(c.Compare, 0, 5);
            switch (c.Kind)
            {
                case '1':
                    if (c.Function == "50") break;   // choice: handled by topic placement
                    if (FunctionCondition(c, op) is { } fc) list.Add(fc);
                    else if (!Holds(c.Compare, 0, c.Value) && !SatisfiedWhenUnknown(c.Function)) return null;
                    break;
                case '2':
                    if (c.Name.Equals("Random100", StringComparison.OrdinalIgnoreCase)) { list.Add(Cond(new GetRandomPercentConditionData(), op, c.Value)); break; }
                    if (MwGlobal(c.Name) is { } g)
                    {
                        var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, g.FormKey);
                        list.Add(Cond(d, op, c.Value));
                    }
                    else if (!Holds(c.Compare, 0, c.Value)) return null;
                    break;
                case '3':
                    // A local of the speaker's script: mirrored by the translated scripts as the rank in a hidden faction
                    // (MWLocal<name>); a speaker without that local isn't in it, and the response isn't theirs.
                    if (LocalFaction(c.Name) is { } lf)
                    {
                        var inIt = new GetInFactionConditionData(); inIt.Faction = new FormLinkOrIndex<IFactionGetter>(inIt, lf);
                        list.Add(Cond(inIt, CompareOperator.EqualTo, 1));
                        var rank = new GetFactionRankConditionData(); rank.Faction = new FormLinkOrIndex<IFactionGetter>(rank, lf);
                        list.Add(Cond(rank, op, c.Value));
                    }
                    else if (!Holds(c.Compare, 0, c.Value)) return null;   // no scripts: the local reads 0
                    break;
                case 'C':   // the speaker's script lacks a local: not in its mirror faction
                    if (LocalFaction(c.Name) is { } nf)
                    {
                        var notIn = new GetInFactionConditionData(); notIn.Faction = new FormLinkOrIndex<IFactionGetter>(notIn, nf);
                        list.Add(Cond(notIn, CompareOperator.EqualTo, 0));
                    }
                    break;
                case '4':
                    if (journals.TryGetValue(c.Name, out var quest))
                    {
                        var d = new GetStageConditionData(); d.Quest = new FormLinkOrIndex<IQuestGetter>(d, quest.FormKey);
                        list.Add(Cond(d, op, c.Value));
                    }
                    else if (!Holds(c.Compare, 0, c.Value)) return null;
                    break;
                case '5':
                    if (Item(c.Name) is { } item)
                    {
                        var d = new GetItemCountConditionData(); d.ItemOrList = new FormLinkOrIndex<IItemOrListGetter>(d, item.FormKey);
                        list.Add(Cond(d, op, c.Value, player: true));
                    }
                    else if (!Holds(c.Compare, 0, c.Value)) return null;
                    break;
                case '6':
                    if (Actor(c.Name) is Npc dead)
                    {
                        var d = new GetDeadCountConditionData(); d.Npc = new FormLinkOrIndex<INpcGetter>(d, dead.FormKey);
                        list.Add(Cond(d, op, c.Value));
                    }
                    else if (!Holds(c.Compare, 0, c.Value)) return null;
                    break;
                case '7':
                    if (Actor(c.Name) is Npc notMe)
                    {
                        var d = new GetIsIDConditionData(); d.Object = new FormLinkOrIndex<IReferenceableObjectGetter>(d, notMe.FormKey);
                        list.Add(Cond(d, CompareOperator.EqualTo, 0));
                    }
                    break;
                case '8':
                    if (MwFaction(c.Name) is { } notFaction)
                    {
                        var d = new GetInFactionConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, notFaction.FormKey);
                        list.Add(Cond(d, CompareOperator.EqualTo, 0));
                    }
                    break;
                case '9':
                    if (MwClass(c.Name) is { } notClass)
                    {
                        var d = new GetIsClassConditionData(); d.Class = new FormLinkOrIndex<IClassGetter>(d, notClass.FormKey);
                        list.Add(Cond(d, CompareOperator.EqualTo, 0));
                    }
                    break;
                case 'A':
                    if (MwRaces.TryGetValue(c.Name, out var notRaceId) && skyrimEsm?.Races.FirstOrDefault(x => x.EditorID == notRaceId) is { } notRace)
                    {
                        var d = new GetIsRaceConditionData(); d.Race = new FormLinkOrIndex<IRaceGetter>(d, notRace.FormKey);
                        list.Add(Cond(d, CompareOperator.EqualTo, 0));
                    }
                    break;
                case 'B':
                    foreach (var cell in CellsNamed(c.Name))
                    {
                        var d = new GetInCellConditionData(); d.Cell = new FormLinkOrIndex<ICellGetter>(d, cell.FormKey);
                        list.Add(Cond(d, CompareOperator.EqualTo, 0));
                    }
                    break;
            }
        }
        return list;
    }

    /// <summary>Morrowind skills (function codes 10-37) as Skyrim actor values, where Skyrim has one.</summary>
    static readonly Dictionary<string, ActorValue> SkillFunctions = new()
    {
        ["11"] = ActorValue.Block, ["12"] = ActorValue.Smithing, ["13"] = ActorValue.LightArmor, ["14"] = ActorValue.HeavyArmor,
        ["15"] = ActorValue.TwoHanded, ["16"] = ActorValue.OneHanded, ["17"] = ActorValue.TwoHanded, ["18"] = ActorValue.TwoHanded,
        ["20"] = ActorValue.Enchanting, ["21"] = ActorValue.Destruction, ["22"] = ActorValue.Alteration, ["23"] = ActorValue.Illusion,
        ["24"] = ActorValue.Conjuration, ["25"] = ActorValue.Alteration, ["26"] = ActorValue.Restoration, ["27"] = ActorValue.Alchemy,
        ["29"] = ActorValue.Lockpicking, ["30"] = ActorValue.Sneak, ["32"] = ActorValue.LightArmor, ["33"] = ActorValue.OneHanded,
        ["34"] = ActorValue.Archery, ["35"] = ActorValue.Speech, ["36"] = ActorValue.Speech,
    };

    /// <summary>Thresholds on the player's skills, attributes and reputation that Skyrim can't test pass, so the
    /// content stays reachable; states (expelled, diseased, vampire, alarmed...) count as 0.</summary>
    static bool SatisfiedWhenUnknown(string function) =>
        int.TryParse(function, out int f) && (f is >= 3 and <= 5 || f is >= 8 and <= 37 || f is >= 51 and <= 57);

    ConditionFloat? FunctionCondition(MwDialogue.Condition c, CompareOperator op)
    {
        switch (c.Function)
        {
            case "63": return Cond(new GetTalkedToPCConditionData(), op, c.Value);
            case "06": return Cond(new GetLevelConditionData(), op, c.Value, player: true);
            case "61": return Cond(new GetLevelConditionData(), op, c.Value);
            case "04": return Cond(new GetActorValuePercentConditionData { ActorValue = ActorValue.Health }, op, c.Value / 100f);
            case "07": return Cond(new GetActorValuePercentConditionData { ActorValue = ActorValue.Health }, op, c.Value / 100f, player: true);
            case "43": return Cond(new GetCrimeGoldConditionData(), op, c.Value, player: true);
            default:
                if (SkillFunctions.TryGetValue(c.Function, out var av))
                    return Cond(new GetActorValueConditionData { ActorValue = av }, op, c.Value, player: true);
                return null;
        }
    }

    // ---- Result scripts ----

    sealed record ResultActions(bool Goodbye, List<(string Text, int Number)> Choices, DialogResponsesAdapter? Adapter);

    /// <summary>
    /// The parts of a result script Skyrim can act on: Journal, Set (globals), AddItem/RemoveItem on the player,
    /// PCJoinFaction/PCRaiseRank, StartCombat player, Goodbye and Choice. The rest (AddTopic, dispositions, script
    /// variables) is dropped.
    /// </summary>
    ResultActions ParseResult(MwDialogue.Info info)
    {
        bool goodbye = false, attack = false, raiseRank = false, setsDisposition = false;
        int disposition = 0;
        var choices = new List<(string, int)>();
        var stageQuests = new List<FormKey>(); var stages = new List<int>();
        var globalForms = new List<FormKey>(); var globalValues = new List<float>();
        var itemForms = new List<FormKey>(); var itemCounts = new List<int>();
        FormKey? joinFaction = null, rankFaction = null;
        foreach (var raw in (info.Result ?? "").Split('\n'))
        {
            string line = raw.Trim();
            int comment = line.IndexOf(';');
            if (comment >= 0) line = line[..comment].Trim();
            if (line.Length == 0) continue;
            Match m;
            if (Regex.IsMatch(line, @"^goodbye\b", RegexOptions.IgnoreCase)) goodbye = true;
            else if ((m = Regex.Match(line, @"^journal\s*,?\s*""?([^"",\s]+)""?\s*,?\s*(\d+)", RegexOptions.IgnoreCase)).Success)
            {
                if (journals.TryGetValue(m.Groups[1].Value, out var q)) { stageQuests.Add(q.FormKey); stages.Add(int.Parse(m.Groups[2].Value)); }
            }
            else if ((m = Regex.Match(line, @"^choice\s*,?\s*(.*)$", RegexOptions.IgnoreCase)).Success)
            {
                foreach (Match p in Regex.Matches(m.Groups[1].Value, @"""([^""]*)""\s*,?\s*(\d+)"))
                    choices.Add((p.Groups[1].Value, int.Parse(p.Groups[2].Value)));
            }
            else if ((m = Regex.Match(line, @"^set\s+([A-Za-z0-9_]+)\s+to\s+(-?[\d.]+)\s*$", RegexOptions.IgnoreCase)).Success)
            {
                if (MwGlobal(m.Groups[1].Value) is { } g) { globalForms.Add(g.FormKey); globalValues.Add(float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)); }
            }
            else if ((m = Regex.Match(line, @"^player\s*->\s*(additem|removeitem)\s*,?\s*""?([^"",]+?)""?\s*,?\s*(\d+)", RegexOptions.IgnoreCase)).Success)
            {
                string id = m.Groups[2].Value.Trim();
                int count = StackOf(id, int.Parse(m.Groups[3].Value)) * (m.Groups[1].Value.Equals("removeitem", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
                FormKey? key = Item(id)?.FormKey;
                if (key is { } k) { itemForms.Add(k); itemCounts.Add(count); }
            }
            else if ((m = Regex.Match(line, @"^pcjoinfaction\s*,?\s*""?([^""\s]*)""?", RegexOptions.IgnoreCase)).Success)
            {
                string fid = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : info.Faction ?? "";
                if (MwFaction(fid) is { } f) joinFaction = f.FormKey;
            }
            else if ((m = Regex.Match(line, @"^pcraiserank\s*,?\s*""?([^""\s]*)""?", RegexOptions.IgnoreCase)).Success)
            {
                string fid = m.Groups[1].Value.Length > 0 ? m.Groups[1].Value : info.Faction ?? "";
                if (MwFaction(fid) is { } f) { rankFaction = f.FormKey; raiseRank = true; }
            }
            else if (Regex.IsMatch(line, @"^startcombat\s*,?\s*""?player""?", RegexOptions.IgnoreCase)) attack = true;
            // The speaker's own disposition (another NPC's, "x"->ModDisposition, isn't tracked here).
            else if ((m = Regex.Match(line, @"^(mod|set)disposition\s*,?\s*(-?\d+)", RegexOptions.IgnoreCase)).Success)
            {
                int n = int.Parse(m.Groups[2].Value);
                if (m.Groups[1].Value.Equals("set", StringComparison.OrdinalIgnoreCase)) { setsDisposition = true; disposition = n; }
                else disposition += n;
            }
        }
        DialogResponsesAdapter? adapter = null;
        if (stageQuests.Count > 0 || globalForms.Count > 0 || itemForms.Count > 0 || joinFaction is not null || raiseRank || attack || disposition != 0 || setsDisposition)
        {
            var entry = new ScriptEntry { Name = InfoScript };
            static ScriptObjectListProperty Objects(string name, IEnumerable<FormKey> keys)
            {
                var p = new ScriptObjectListProperty { Name = name };
                foreach (var k in keys) p.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(k) });
                return p;
            }
            if (stageQuests.Count > 0) { entry.Properties.Add(Objects("StageQuests", stageQuests)); entry.Properties.Add(new ScriptIntListProperty { Name = "Stages", Data = [.. stages] }); }
            if (globalForms.Count > 0) { entry.Properties.Add(Objects("Globals", globalForms)); entry.Properties.Add(new ScriptFloatListProperty { Name = "GlobalValues", Data = [.. globalValues] }); }
            if (itemForms.Count > 0) { entry.Properties.Add(Objects("Items", itemForms)); entry.Properties.Add(new ScriptIntListProperty { Name = "ItemCounts", Data = [.. itemCounts] }); }
            if (joinFaction is { } jf) entry.Properties.Add(new ScriptObjectProperty { Name = "JoinFaction", Object = new FormLink<ISkyrimMajorRecordGetter>(jf) });
            if (rankFaction is { } rf) entry.Properties.Add(new ScriptObjectProperty { Name = "RankFaction", Object = new FormLink<ISkyrimMajorRecordGetter>(rf) });
            if (attack) entry.Properties.Add(new ScriptBoolProperty { Name = "Attack", Data = true });
            if (disposition != 0 || setsDisposition) entry.Properties.Add(new ScriptIntProperty { Name = "Disposition", Data = disposition });
            if (setsDisposition) entry.Properties.Add(new ScriptBoolProperty { Name = "SetsDisposition", Data = true });
            adapter = new DialogResponsesAdapter
            {
                Scripts = [entry],
                ScriptFragments = new ScriptFragments
                {
                    ExtraBindDataVersion = 2,
                    FileName = InfoScript,
                    OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = InfoScript, FragmentName = "Fragment_End" },
                },
            };
        }
        return new ResultActions(goodbye, choices, adapter);
    }
}
