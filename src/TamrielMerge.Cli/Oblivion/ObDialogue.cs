using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's quests and dialogue as Skyrim's. Oblivion's dialogue is already shaped like Skyrim's (topics of
/// responses, each with conditions on condition functions, choices, a result script), so most of it carries over:
/// <list type="bullet">
/// <item>Quests keep their stages and log entries; quest-script variables, which dialogue tests (GetQuestVariable)
/// and result scripts set (set Quest.var to n), become one Skyrim global each.</item>
/// <item>Topics (type Topic) are player topics of the "OBDialogue" quest; GREETING is Hello. A topic only ever offered
/// as a choice goes in a branch that isn't top-level. A response only holds while its quest runs (GetQuestRunning)
/// unless that quest starts with the game.</item>
/// <item>Conditions keep their function where Skyrim has it, with the forms they name converted (NPCs, races,
/// factions, classes, quests, globals, items, interiors); disposition counts as 50, script variables and things
/// Skyrim doesn't track as 0, and anything else that can't be tested passes.</item>
/// <item>Result scripts: SetStage, StartQuest/StopQuest, set (globals and quest variables), AddItem/RemoveItem on the
/// player and StartCombat run from TMMwInfo (the shared TopicInfo script).</item>
/// </list>
/// Conversation, combat, detection and service topics are not converted here.
/// </summary>
public sealed partial class ObPluginBuilder
{
    static readonly FormKey PlayerRefKey = new(ModKey.FromFileName("Skyrim.esm"), 0x14);
    const string InfoScript = "TMMwInfo";
    /// <summary>Every converted response's script (or its result script, which extends it): the topics it gives the
    /// player, and its speaker talking while it is said.</summary>
    const string LineScript = "TMObLine";

    /// <summary>The player's topics Oblivion keeps hidden until the player learns them (AddTopic, or a response's Add
    /// Topics said): every topic of type Topic but GREETING and those only ever offered as a choice.</summary>
    readonly HashSet<FormKey> gatedTopics = [];
    readonly Dictionary<FormKey, Global> topicKnown = new();
    /// <summary>The Oblivion topic (EditorID) each converted response belongs to.</summary>
    readonly Dictionary<DialogResponses, string> responseTopics = new();
    public int TopicsGated => gatedTopics.Count;
    public int ResponsesLearning { get; private set; }

    /// <summary>OBTopicKnown...: whether the player knows the topic (1 once learned).</summary>
    Global TopicKnownGlobal(FormKey topic)
    {
        if (topicKnown.TryGetValue(topic, out var hit)) return hit;
        string edid = records.GetValueOrDefault(topic)?.EditorID ?? topic.ID.ToString("X6");
        var g = new GlobalFloat(mod) { EditorID = "OBTopicKnown" + Sanitize(edid), Data = 0 };
        mod.Globals.Add(g);
        return topicKnown[topic] = g;
    }

    Quest? obDialogueQuest;
    readonly Dictionary<FormKey, Quest> obQuests = new();
    readonly Dictionary<FormKey, Faction?> obFactions = new();
    readonly Dictionary<FormKey, Class?> obClasses = new();
    readonly Dictionary<FormKey, Global?> obGlobals = new();
    readonly Dictionary<(FormKey Quest, string Var), Global> questVariables = new();
    readonly Dictionary<FormKey, DialogTopic> obTopics = new();
    readonly Dictionary<FormKey, Cell> interiorsBySource = new();
    DialogBranch? choiceBranch;
    public int DialogueTopics { get; private set; }
    public int DialogueResponses { get; private set; }
    public int DialogueLines { get; private set; }
    public int QuestCount => obQuests.Count;

    /// <summary>A converted response and the Oblivion response it came from (plugin and FormID), with its lines, so
    /// Oblivion's own recordings can become its voice files.</summary>
    public sealed record VoiceSource(uint Info, string SourcePlugin, string OriginPlugin, uint OriginId, List<(int Number, string Text)> Lines);

    public List<VoiceSource> VoiceMap { get; } = [];

    // ---- Forms the dialogue names ----

    Faction? ObFaction(FormKey key)
    {
        if (obFactions.TryGetValue(key, out var hit)) return hit;
        if (records.GetValueOrDefault(key) is not Ob.IFactionGetter f) return obFactions[key] = null;
        var faction = new Faction(mod) { EditorID = "OBFaction" + Sanitize(f.EditorID ?? key.ID.ToString("X6")), Name = f.Name ?? f.EditorID, Ranks = [] };
        uint n = 0;
        foreach (var r in f.Ranks)
        {
            string title = r.Name?.Male ?? r.Name?.Female ?? "";
            faction.Ranks.Add(new Rank { Number = n++, Title = new GenderedItem<Mutagen.Bethesda.Strings.TranslatedString>(title, r.Name?.Female ?? title) });
        }
        if (f.Flags is { } obFlags && obFlags.HasFlag(Ob.Faction.FactionFlag.HiddenFromPlayer)) faction.Flags |= Faction.FactionFlag.HiddenFromPC;
        mod.Factions.Add(faction);
        return obFactions[key] = faction;
    }

    Class? ObClass(FormKey key)
    {
        if (obClasses.TryGetValue(key, out var hit)) return hit;
        if (records.GetValueOrDefault(key) is not Ob.IClassGetter c) return obClasses[key] = null;
        var template = SkyrimEsm.Classes.FirstOrDefault(x => x.EditorID == "CombatWarrior1H") ?? SkyrimEsm.Classes.First();
        var cls = mod.Classes.DuplicateInAsNewRecord(template);
        cls.EditorID = "OBClass" + Sanitize(c.EditorID ?? key.ID.ToString("X6"));
        cls.Name = c.Name ?? c.EditorID;
        return obClasses[key] = cls;
    }

    Global? ObGlobal(FormKey key)
    {
        if (obGlobals.TryGetValue(key, out var hit)) return hit;
        if (records.GetValueOrDefault(key) is not Ob.IGlobalGetter g) return obGlobals[key] = null;
        var global = new GlobalFloat(mod) { EditorID = "OBGlobal" + Sanitize(g.EditorID ?? key.ID.ToString("X6")), Data = g switch { Ob.IGlobalFloatGetter f => f.Data ?? 0, Ob.IGlobalShortGetter sh => sh.Data ?? 0, Ob.IGlobalIntGetter i => i.Data ?? 0, _ => 0 } };
        mod.Globals.Add(global);
        return obGlobals[key] = global;
    }

    /// <summary>A quest-script variable as a Skyrim global (dialogue reads and sets these).</summary>
    Global QuestVariable(FormKey quest, string name)
    {
        if (questVariables.TryGetValue((quest, name.ToLowerInvariant()), out var hit)) return hit;
        string q = records.GetValueOrDefault(quest)?.EditorID ?? quest.ID.ToString("X6");
        var g = new GlobalFloat(mod) { EditorID = $"OBQV{Sanitize(q)}_{Sanitize(name)}", Data = 0 };
        mod.Globals.Add(g);
        return questVariables[(quest, name.ToLowerInvariant())] = g;
    }

    /// <summary>The name of a quest's script variable by its index (GetQuestVariable names variables by index).</summary>
    string? QuestVariableName(FormKey quest, int index) =>
        records.GetValueOrDefault(quest) is Ob.IQuestGetter q && records.GetValueOrDefault(q.Script.FormKey) is Ob.IScriptGetter s
            ? s.Fields.LocalVariables.FirstOrDefault(v => v.Data?.Index == index)?.Name : null;

    FormKey? QuestByEditorId(string edid) =>
        records.Values.OfType<Ob.IQuestGetter>().FirstOrDefault(q => string.Equals(q.EditorID, edid, StringComparison.OrdinalIgnoreCase))?.FormKey;

    Lazy<Dictionary<string, FormKey>>? byEditorId;
    FormKey? FormByEditorId(string edid)
    {
        byEditorId ??= new(() => records.Values.Where(r => r.EditorID is not null).GroupBy(r => r.EditorID!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Last().FormKey, StringComparer.OrdinalIgnoreCase));
        return byEditorId.Value.TryGetValue(edid, out var k) ? k : null;
    }

    /// <summary>A raw FormID as written in a record of <paramref name="source"/>: its top byte indexes the plugin's masters, then itself.</summary>
    static FormKey Resolve(Ob.IOblivionModGetter source, uint raw)
    {
        int index = (int)(raw >> 24);
        var masters = source.ModHeader.MasterReferences;
        var mod = index < masters.Count ? masters[index].Master : source.ModKey;
        return new FormKey(mod, raw & 0xFFFFFF);
    }

    // ---- Building ----

    public void BuildDialogue()
    {
        obDialogueQuest = new Quest(mod)
        {
            EditorID = "OBDialogue",
            Name = "Oblivion Dialogue",
            Flags = Quest.Flag.StartGameEnabled | Quest.Flag.AllowRepeatedStages,
            Priority = 40,
            Type = Quest.TypeEnum.Misc,
            Aliases = [new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(PlayerRefKey) }],
            NextAliasID = 1,
        };
        mod.Quests.Add(obDialogueQuest);
        choiceBranch = new DialogBranch(mod)
        {
            EditorID = "OBDialogueChoices",
            Quest = obDialogueQuest.ToLink<IQuestGetter>(),
            Category = DialogBranch.CategoryType.Player,
            Flags = 0,
        };
        mod.DialogBranches.Add(choiceBranch);

        foreach (var q in plugins.SelectMany(p => p.Quests).GroupBy(q => q.FormKey).Select(g => g.Last())) ConvertQuest(q);
        // The log entries' conditions, once every quest exists (they test other quests' stages).
        foreach (var q in plugins.SelectMany(p => p.Quests).GroupBy(q => q.FormKey).Select(g => g.Last())) LogEntryConditions(q);

        // Topics merged across plugins; each response remembers the plugin it came from (to read its FormIDs).
        var topics = new Dictionary<FormKey, (Ob.IDialogTopicGetter Topic, List<(Ob.IDialogItemGetter Item, Ob.IOblivionModGetter Source)> Items)>();
        foreach (var p in plugins)
            foreach (var t in p.DialogTopics)
            {
                if (!topics.TryGetValue(t.FormKey, out var entry)) topics[t.FormKey] = entry = (t, []);
                else topics[t.FormKey] = entry = (t, entry.Items);
                foreach (var item in t.Items)
                {
                    int existing = entry.Items.FindIndex(x => x.Item.FormKey == item.FormKey);
                    if (existing >= 0) entry.Items[existing] = (item, p);
                    else
                    {
                        int after = item.PreviousItem.IsNull ? -1 : entry.Items.FindIndex(x => x.Item.FormKey == item.PreviousItem.FormKey);
                        entry.Items.Insert(after >= 0 ? after + 1 : entry.Items.Count, (item, p));
                    }
                }
            }
        var added = topics.Values.SelectMany(t => t.Items).SelectMany(i => i.Item.Topics.Select(x => x.FormKey)).ToHashSet();
        var choiceOnly = topics.Values.SelectMany(t => t.Items).SelectMany(i => i.Item.Choices.Select(x => x.FormKey)).Where(k => !added.Contains(k)).ToHashSet();

        // Create every topic first (choices link to topics), then fill them.
        var player = topics.Values.Where(t => t.Topic.DialogType == Ob.DialogType.Topic).ToList();
        foreach (var (topic, _) in player) TopicShell(topic, choiceOnly.Contains(topic.FormKey));
        foreach (var (topic, _) in player)
            if (!choiceOnly.Contains(topic.FormKey) && !string.Equals(topic.EditorID, "GREETING", StringComparison.OrdinalIgnoreCase)) gatedTopics.Add(topic.FormKey);
        foreach (var (topic, items) in player)
        {
            var skyTopic = obTopics[topic.FormKey];
            bool gated = gatedTopics.Contains(topic.FormKey);
            foreach (var (item, source) in items)
                if (Response(item, source) is { } r)
                {
                    // Offered once the player knows the topic: first, so the response's own OR groups stay as they were.
                    if (gated)
                    {
                        var known = new GetGlobalValueConditionData();
                        known.Global = new FormLinkOrIndex<IGlobalGetter>(known, TopicKnownGlobal(topic.FormKey).FormKey);
                        r.Conditions.Insert(0, Cond(known, CompareOperator.EqualTo, 1, false, false));
                    }
                    // GREETING starts a conversation (a Hello said in passing is Oblivion's HELLO's, or nothing).
                    if (string.Equals(topic.EditorID, "GREETING", StringComparison.OrdinalIgnoreCase)) r.Conditions.Insert(0, Skyrim.ForeignVoices.InConversation());
                    responseTopics[r] = topic.EditorID ?? "";
                    skyTopic.Responses.Add(r);
                }
            // Kept even when empty: other responses may link to it as a choice (an empty topic is never offered).
            mod.DialogTopics.Add(skyTopic);
            if (skyTopic.Responses.Count > 0) DialogueTopics++;
        }
        BuildBarks(topics);
        BuildConversations(topics);
        Persuasion();
        // Money changers (CoinTrade): the faction is shared with the people who join it, made now or later.
        moneyChangers ??= Skyrim.CoinTrade.Tag(mod, "OBMoneyChangers", "Money changers");
        Skyrim.CoinTrade.AddExchange(mod, obDialogueQuest, moneyChangers.FormKey, story: 3, prefix: "OB", title: "Money changer", baseFee: 10);
    }

    /// <summary>
    /// Oblivion's persuasion (the Admire/Boast/Joke/Coerce wheel and bribes) as a "Persuasion" topic of choices: each
    /// attempt succeeds on a roll (drawn by the response before it) under a chance set by the player's Speech, and
    /// raises the speaker's disposition on success or lowers it a little on failure; a bribe needs the gold, takes it
    /// and always works, raising disposition more for more gold. The wheel has no spoken lines, so the answers are short.
    /// </summary>
    void Persuasion()
    {
        var roll = new GlobalFloat(mod) { EditorID = "OBPersuasionRoll", Data = 50 };
        mod.Globals.Add(roll);
        var branch = new DialogBranch(mod) { EditorID = "OBPersuasionBranch", Quest = obDialogueQuest!.ToLink<IQuestGetter>(), Category = DialogBranch.CategoryType.Player, Flags = DialogBranch.Flag.TopLevel };
        mod.DialogBranches.Add(branch);
        DialogTopic NewTopic(string edid, string name) => new(mod)
        {
            EditorID = edid, Name = name, Quest = obDialogueQuest!.ToNullableLink<IQuestGetter>(), Branch = branch.ToNullableLink<IDialogBranchGetter>(),
            Priority = 50, Category = DialogTopic.CategoryEnum.Topic, Subtype = DialogTopic.SubtypeEnum.Custom, Responses = [],
        };
        var menu = NewTopic("OBPersuasion", "Persuasion");
        branch.StartingTopic = menu.ToNullableLink<IDialogTopicGetter>();
        DialogResponses Line(string text, Emotion emotion, IEnumerable<ConditionFloat> conditions, int change, int gold)
        {
            var r = new DialogResponses(mod)
            {
                Responses = [new DialogResponse { Text = text, ResponseNumber = 1, Emotion = emotion, EmotionValue = 50 }],
                Flags = new DialogResponseFlags(),
                Conditions = [.. conditions],
            };
            var entry = new ScriptEntry { Name = InfoScript };
            if (change != 0) entry.Properties.Add(new ScriptIntProperty { Name = "Disposition", Data = change });
            if (gold > 0) entry.Properties.Add(new ScriptIntProperty { Name = "Gold", Data = gold });
            entry.Properties.Add(new ScriptObjectProperty { Name = "Roll", Object = roll.ToLink<ISkyrimMajorRecordGetter>() });
            r.VirtualMachineAdapter = new DialogResponsesAdapter
            {
                Scripts = [entry],
                ScriptFragments = new ScriptFragments
                {
                    ExtraBindDataVersion = 2, FileName = InfoScript,
                    OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = InfoScript, FragmentName = "Fragment_End" },
                },
            };
            return r;
        }
        ConditionFloat Speech(int at)
        {
            var d = new GetActorValueConditionData { ActorValue = ActorValue.Speech };
            d.RunOnType = Condition.RunOnType.Reference;
            d.Reference = new FormLink<ISkyrimMajorRecordGetter>(PlayerRefKey);
            return new ConditionFloat { Data = d, CompareOperator = CompareOperator.GreaterThanOrEqualTo, ComparisonValue = at };
        }
        ConditionFloat RollBelow(int chance)
        {
            var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, roll.FormKey);
            return new ConditionFloat { Data = d, CompareOperator = CompareOperator.LessThan, ComparisonValue = Math.Clamp(chance, 5, 95) };
        }
        ConditionFloat HasGold(int gold)
        {
            var d = new GetItemCountConditionData(); d.ItemOrList = new FormLinkOrIndex<IItemOrListGetter>(d, Sk(0x00000F));
            d.RunOnType = Condition.RunOnType.Reference;
            d.Reference = new FormLink<ISkyrimMajorRecordGetter>(PlayerRefKey);
            return new ConditionFloat { Data = d, CompareOperator = CompareOperator.GreaterThanOrEqualTo, ComparisonValue = gold };
        }
        (int Speech, int Chance)[] bands = [(75, 80), (50, 60), (25, 45), (0, 30)];
        (string Name, string Win, string Lose, int Up, int Down)[] wheel =
        [
            ("Admire", "That's kind of you to say.", "Flattery won't get you anywhere with me.", 8, -3),
            ("Boast", "Impressive. I'll remember that.", "Is that so? I'm not impressed.", 8, -3),
            ("Joke", "Ha! That's a good one.", "That isn't funny.", 8, -3),
            ("Coerce", "All right, all right. No need for that.", "Are you threatening me?", 10, -6),
        ];
        var choices = new List<DialogTopic>();
        foreach (var (name, win, lose, up, down) in wheel)
        {
            var choice = NewTopic("OBPersuade" + name, name);
            foreach (var (speech, chance) in bands) choice.Responses.Add(Line(win, Emotion.Happy, [Speech(speech), RollBelow(chance)], up, 0));
            choice.Responses.Add(Line(lose, Emotion.Anger, [], down, 0));
            choices.Add(choice);
        }
        foreach (var (gold, up) in new[] { (10, 4), (100, 12), (1000, 30) })
        {
            var bribe = NewTopic($"OBPersuadeBribe{gold}", $"Bribe ({gold} gold)");
            bribe.Responses.Add(Line("Well, I suppose I could use the coin.", Emotion.Happy, [HasGold(gold)], up, gold));
            choices.Add(bribe);
        }
        foreach (var c in choices)
            foreach (var r in c.Responses) foreach (var other in choices) r.LinkTo.Add(other.ToLink<IDialogTopicGetter>());
        var open = Line("", Emotion.Neutral, [], 0, 0);
        foreach (var c in choices) open.LinkTo.Add(c.ToLink<IDialogTopicGetter>());
        menu.Responses.Add(open);
        mod.DialogTopics.Add(menu);
        foreach (var c in choices) mod.DialogTopics.Add(c);
        DialogueTopics += choices.Count + 1;
    }

    void ConvertQuest(Ob.IQuestGetter q)
    {
        var quest = new Quest(mod)
        {
            EditorID = "OBQ" + Sanitize(q.EditorID ?? q.FormKey.ID.ToString("X6")),
            Name = q.Name,
            Priority = q.Data?.Priority ?? 50,
            Type = q.EditorID?.StartsWith("MQ", StringComparison.OrdinalIgnoreCase) == true ? Quest.TypeEnum.MainQuest : Quest.TypeEnum.Misc,
            Flags = q.Data?.Flags.HasFlag(Ob.Quest.Flag.StartGameEnabled) == true ? Quest.Flag.StartGameEnabled : 0,
            Stages = [],
        };
        foreach (var s in q.Stages)
        {
            // Every log entry, in Oblivion's order: a stage shows the first whose conditions hold and runs its result
            // script (ObResultScripts: fragments by entry index), as Skyrim does; an entry without text still counts.
            var stage = new QuestStage { Index = s.Stage, LogEntries = [] };
            foreach (var e in s.LogEntries)
            {
                string flags = e.Flags?.ToString() ?? "";
                stage.LogEntries.Add(new QuestLogEntry
                {
                    Entry = e.Entry is { Length: > 0 } text ? text : null,
                    Flags = flags.Contains("Complet", StringComparison.OrdinalIgnoreCase) ? QuestLogEntry.Flag.CompleteQuest
                        : flags.Contains("Fail", StringComparison.OrdinalIgnoreCase) ? QuestLogEntry.Flag.FailQuest : null,
                });
            }
            quest.Stages.Add(stage);
        }
        mod.Quests.Add(quest);
        obQuests[q.FormKey] = quest;
    }

    /// <summary>
    /// A quest's log entries' conditions as Skyrim's (ObConditions); an entry that can never hold gets one condition
    /// that never does, so the next entry of its stage is taken, as in Oblivion.
    /// </summary>
    void LogEntryConditions(Ob.IQuestGetter q)
    {
        if (!obQuests.TryGetValue(q.FormKey, out var quest)) return;
        var source = plugins.LastOrDefault(p => p.Quests.ContainsKey(q.FormKey));
        if (source is null) return;
        foreach (var s in q.Stages)
        {
            var stage = quest.Stages.FirstOrDefault(x => x.Index == s.Stage);
            if (stage is null) continue;
            for (int e = 0; e < s.LogEntries.Count && e < stage.LogEntries.Count; e++)
            {
                if (s.LogEntries[e].Conditions.Count == 0) continue;
                if (ObConditions(s.LogEntries[e].Conditions, source) is { } conditions) stage.LogEntries[e].Conditions.AddRange(conditions);
                else stage.LogEntries[e].Conditions.Add(Cond(new GetRandomPercentConditionData(), CompareOperator.LessThan, 0, false, false));
            }
        }
    }

    void TopicShell(Ob.IDialogTopicGetter topic, bool choiceOnly)
    {
        bool greeting = string.Equals(topic.EditorID, "GREETING", StringComparison.OrdinalIgnoreCase);
        string edid = (greeting ? "OBHello" : "OBTopic") + Sanitize(topic.EditorID ?? topic.FormKey.ID.ToString("X6"));
        var t = new DialogTopic(mod)
        {
            EditorID = edid,
            Quest = obDialogueQuest!.ToNullableLink<IQuestGetter>(),
            Priority = 50,
            Category = greeting ? DialogTopic.CategoryEnum.Misc : DialogTopic.CategoryEnum.Topic,
            Subtype = greeting ? DialogTopic.SubtypeEnum.Hello : DialogTopic.SubtypeEnum.Custom,
            Responses = [],
        };
        if (!greeting)
        {
            t.Name = topic.Name ?? topic.EditorID ?? "";
            if (choiceOnly) t.Branch = choiceBranch!.ToNullableLink<IDialogBranchGetter>();
            else
            {
                var branch = new DialogBranch(mod)
                {
                    EditorID = edid + "Branch",
                    Quest = obDialogueQuest.ToLink<IQuestGetter>(),
                    Category = DialogBranch.CategoryType.Player,
                    Flags = DialogBranch.Flag.TopLevel,
                    StartingTopic = t.ToNullableLink<IDialogTopicGetter>(),
                };
                mod.DialogBranches.Add(branch);
                t.Branch = branch.ToNullableLink<IDialogBranchGetter>();
            }
        }
        obTopics[topic.FormKey] = t;
    }

    /// <summary>Responses left out because a condition of theirs is one Skyrim knows never holds for them.</summary>
    readonly HashSet<FormKey> responsesNeverHolding = new();
    /// <summary>The conditions (dialogue, packages, journal) judged never to hold, by function: what left each out.</summary>
    public Dictionary<string, int> NeverHoldingBy { get; } = new();

    DialogResponses? Response(Ob.IDialogItemGetter item, Ob.IOblivionModGetter source)
    {
        if (Conditions(item, source) is not { } conditions) { lock (responsesNeverHolding) responsesNeverHolding.Add(item.FormKey); return null; }
        var r = new DialogResponses(mod) { Conditions = conditions, Responses = [], Flags = new DialogResponseFlags() };
        byte n = 1;
        // One line per Oblivion line, numbered as Oblivion numbered them: each has its own recording, which becomes
        // this line's voice file (VoiceMap, ObVoices).
        var voiced = new List<(int Number, string Text)>();
        foreach (var line in item.Responses.OrderBy(x => x.Data?.ResponseNumber ?? 0))
        {
            var emotion = Enum.TryParse<Emotion>(line.Data?.Emotion.ToString(), out var e) ? e : Emotion.Neutral;
            byte number = line.Data?.ResponseNumber is { } rn && rn > 0 ? rn : n;
            string text = (line.ResponseText ?? "").Replace("\r", "").Replace("\n", " ").Trim();
            r.Responses.Add(new DialogResponse { Text = text, ResponseNumber = number, Emotion = emotion, EmotionValue = (uint)Math.Clamp(line.Data?.EmotionValue ?? 50, 0, 100) });
            voiced.Add((number, text));
            n = (byte)(number + 1);
        }
        VoiceMap.Add(new VoiceSource(r.FormKey.ID, source.ModKey.FileName.String, item.FormKey.ModKey.FileName.String, item.FormKey.ID, voiced));
        if (r.Responses.Count == 0) r.Responses.Add(new DialogResponse { Text = "", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 });
        DialogueLines += r.Responses.Count;
        DialogueResponses++;
        var flags = item.Data?.Flags ?? 0;
        if (flags.HasFlag(Ob.DialogItem.Flag.Goodbye)) r.Flags.Flags |= DialogResponses.Flag.Goodbye;
        if (flags.HasFlag(Ob.DialogItem.Flag.SayOnce)) r.Flags.Flags |= DialogResponses.Flag.SayOnce;
        if (flags.HasFlag(Ob.DialogItem.Flag.Random)) r.Flags.Flags |= DialogResponses.Flag.Random;
        foreach (var choice in item.Choices)
            if (obTopics.TryGetValue(choice.FormKey, out var target)) r.LinkTo.Add(target.ToLink<IDialogTopicGetter>());
        // TMObLine: the topics it gives the player once said (its Add Topics; a hidden topic it offers as a choice, which
        // the choice needs known), and its speaker talking. The regex's subset of its result script (Result) on it,
        // replaced by the whole script once that is compiled (ObResultScripts).
        var entry = Result(item.Script?.SourceCode) ?? new ScriptEntry();
        entry.Name = LineScript;
        var learns = item.Topics.Select(t => t.FormKey).Concat(item.Choices.Select(t => t.FormKey)).Where(gatedTopics.Contains).Distinct().ToList();
        if (learns.Count > 0)
        {
            entry.Properties.Add(new ScriptObjectListProperty { Name = "Learns", Objects = [.. learns.Select(k => new ScriptObjectProperty { Object = TopicKnownGlobal(k).ToLink<ISkyrimMajorRecordGetter>() })] });
            ResponsesLearning++;
        }
        r.VirtualMachineAdapter = LineAdapter(entry, LineScript);
        responsesBySource[item.FormKey] = r;
        return r;
    }

    // ---- Conditions ----

    /// <summary>Conditions Skyrim can't test as Oblivion did, by function: taken as holding at Oblivion's usual value
    /// (the dialogue's, packages' and journal's; conditions-untested.txt).</summary>
    public readonly Dictionary<string, int> ConditionsUntested = new(StringComparer.OrdinalIgnoreCase);

    readonly Dictionary<int, Global> playerStatGlobals = new(), miscStatGlobals = new();
    Faction? evilFaction;

    /// <summary>TMOB_PCMisc_n: the player's Oblivion misc stat n for the conditions (TMObPlayer keeps it).</summary>
    Global MiscStatGlobal(int stat)
    {
        if (miscStatGlobals.TryGetValue(stat, out var hit)) return hit;
        var g = new GlobalFloat(mod) { EditorID = $"TMOB_PCMisc_{stat}", Data = 0 };
        mod.Globals.Add(g);
        return miscStatGlobals[stat] = g;
    }

    /// <summary>OBEvil: Oblivion's evil people (IsActorEvil: in at least one evil faction and no other; no creature),
    /// kept by TMObActor as their factions change.</summary>
    Faction EvilFaction()
    {
        if (evilFaction is not null) return evilFaction;
        evilFaction = new Faction(mod) { EditorID = "OBEvil", Name = "", Flags = Faction.FactionFlag.HiddenFromPC, Ranks = [] };
        mod.Factions.Add(evilFaction);
        return evilFaction;
    }
    readonly Dictionary<int, Faction> statFactions = new();

    /// <summary>TMOB_PCStat_n: the player's Oblivion stat n (ObStats' numbering) for the conditions (TMObPlayer keeps it).</summary>
    Global PlayerStatGlobal(int stat)
    {
        if (playerStatGlobals.TryGetValue(stat, out var hit)) return hit;
        var g = new GlobalFloat(mod) { EditorID = $"TMOB_PCStat_{stat}", Data = 0 };
        mod.Globals.Add(g);
        return playerStatGlobals[stat] = g;
    }

    /// <summary>OBStat_n: an Oblivion person's or creature's stat n as its rank, for the conditions (TMObActor sets it).</summary>
    Faction StatFaction(int stat)
    {
        if (statFactions.TryGetValue(stat, out var hit)) return hit;
        var f = new Faction(mod) { EditorID = $"OBStat_{stat}", Name = "", Flags = Faction.FactionFlag.HiddenFromPC, Ranks = [] };
        mod.Factions.Add(f);
        return statFactions[stat] = f;
    }

    /// <summary>The persistent reference placed for an Oblivion reference (those the scripts, dialogue and packages name
    /// are made persistent: <see cref="ConditionRefs"/>).</summary>
    FormKey? PlacedRef(FormKey source) => placedBySource.TryGetValue(source, out var p) ? p.FormKey : null;

    /// <summary>A reference's script variable by its index (GetScriptVariable): the variable of that index in the
    /// script of the reference's base.</summary>
    string? ScriptVariableName(FormKey reference, int index)
    {
        FormKey? baseKey = records.GetValueOrDefault(reference) switch
        {
            Ob.IPlacedObjectGetter o => o.Base.FormKey,
            Ob.IPlacedNpcGetter n => n.Base.FormKey,
            Ob.IPlacedCreatureGetter c => c.Base.FormKey,
            _ => null,
        };
        if (baseKey is not { } b || records.GetValueOrDefault(b) is not { } rec || ObScriptWorld.ScriptLink(rec) is not { } s || records.GetValueOrDefault(s) is not Ob.IScriptGetter script) return null;
        return script.Fields.LocalVariables.FirstOrDefault(v => v.Data?.Index == index)?.Name;
    }

    /// <summary>The references the dialogue's, packages' and journal entries' conditions name (their first or second
    /// parameter a placed reference): made persistent like the scripts' (before the world is placed).</summary>
    IEnumerable<FormKey> ConditionRefs()
    {
        var conditions = WinningResponses().SelectMany(i => i.Conditions)
            .Concat(plugins.SelectMany(p => p.AIPackages).SelectMany(p => p.Conditions))
            .Concat(WinningQuests().SelectMany(q => q.Stages).SelectMany(s => s.LogEntries).SelectMany(e => e.Conditions));
        foreach (var c in conditions)
            foreach (var raw in new[] { (uint)c.FirstParameter, (uint)c.SecondParameter })
            {
                if (raw == 0) continue;
                foreach (var p in plugins)
                {
                    var key = Resolve(p, raw);
                    if (records.GetValueOrDefault(key) is Ob.IPlacedObjectGetter or Ob.IPlacedNpcGetter or Ob.IPlacedCreatureGetter) { yield return key; break; }
                }
            }
    }

    static ConditionFloat Cond(ConditionData data, CompareOperator op, float value, bool onTarget, bool or)
    {
        if (onTarget) data.RunOnType = Condition.RunOnType.Target;
        return new ConditionFloat { Data = data, CompareOperator = op, ComparisonValue = value, Flags = or ? Condition.Flag.OR : 0 };
    }

    static bool Holds(CompareOperator op, float lhs, float rhs) => op switch
    {
        CompareOperator.EqualTo => lhs == rhs, CompareOperator.NotEqualTo => lhs != rhs, CompareOperator.GreaterThan => lhs > rhs,
        CompareOperator.GreaterThanOrEqualTo => lhs >= rhs, CompareOperator.LessThan => lhs < rhs, CompareOperator.LessThanOrEqualTo => lhs <= rhs, _ => true,
    };

    IRaceGetter? SkyrimRace(FormKey obRace)
    {
        string raceName = records.GetValueOrDefault(obRace)?.EditorID ?? "";
        raceName = RaceStandIns.GetValueOrDefault(raceName, raceName);
        return Vanilla.Get<IRaceGetter>(raceName + "Race");
    }

    /// <summary>The response's conditions in Skyrim's terms, or null when it can never hold.</summary>
    ExtendedList<Condition>? Conditions(Ob.IDialogItemGetter item, Ob.IOblivionModGetter source)
    {
        var list = new ExtendedList<Condition>();
        // Oblivion offers a response only while its quest runs.
        if (!item.Quest.IsNull && obQuests.TryGetValue(item.Quest.FormKey, out var owner) && !owner.Flags.HasFlag(Quest.Flag.StartGameEnabled))
        {
            var d = new GetQuestRunningConditionData(); d.Quest = new FormLinkOrIndex<IQuestGetter>(d, owner.FormKey);
            list.Add(Cond(d, CompareOperator.EqualTo, 1, false, false));
        }
        return ObConditions(item.Conditions, source, list);
    }

    /// <summary>
    /// Oblivion conditions in Skyrim's terms, added to <paramref name="list"/>, or null when they can never hold: what
    /// Skyrim can test as Skyrim's own condition, what it can't by whether it holds at Oblivion's usual value. Dialogue
    /// responses' and AI packages' (both on the actor that has them).
    /// </summary>
    internal ExtendedList<Condition>? ObConditions(IEnumerable<Ob.IConditionGetter> conditions, Ob.IOblivionModGetter source, ExtendedList<Condition>? list = null)
    {
        list ??= [];
        foreach (var c in conditions)
        {
            var op = (CompareOperator)(int)c.CompareOperator;
            float value = c.ComparisonValue;
            bool target = c.Flags.HasFlag(Ob.Condition.Flag.RunOnTarget), or = c.Flags.HasFlag(Ob.Condition.Flag.OR);
            FormKey P1() => Resolve(source, (uint)c.FirstParameter);
            ConditionData? data = null;
            bool? unknownHolds = null;   // set when Skyrim can't test it: does it hold with Oblivion's usual value?
            switch (c.Function.ToString())
            {
                case "GetIsID":
                    if (Actor(P1()) is Npc npc) { var d = new GetIsIDConditionData(); d.Object = new FormLinkOrIndex<IReferenceableObjectGetter>(d, npc.FormKey); data = d; }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetIsRace":
                case "GetPCIsRace":
                    if (SkyrimRace(P1()) is { } race)
                    {
                        if (c.Function.ToString() == "GetIsRace") { var d = new GetIsRaceConditionData(); d.Race = new FormLinkOrIndex<IRaceGetter>(d, race.FormKey); data = d; }
                        else { var d = new GetPCIsRaceConditionData(); d.Race = new FormLinkOrIndex<IRaceGetter>(d, race.FormKey); data = d; }
                    }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetIsSex": data = new GetIsSexConditionData { MaleFemaleGender = c.FirstParameter == 1 ? MaleFemaleGender.Female : MaleFemaleGender.Male }; break;
                case "GetPCIsSex": data = new GetPCIsSexConditionData { MaleFemaleGender = c.FirstParameter == 1 ? MaleFemaleGender.Female : MaleFemaleGender.Male }; break;
                case "GetInFaction":
                case "GetFactionRank":
                case "GetPCInFaction":
                    if (ObFaction(P1()) is { } faction)
                    {
                        if (c.Function.ToString() == "GetFactionRank") { var d = new GetFactionRankConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, faction.FormKey); data = d; }
                        else if (c.Function.ToString() == "GetPCInFaction") { var d = new GetPCInFactionConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, faction.FormKey); data = d; }
                        else { var d = new GetInFactionConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, faction.FormKey); data = d; }
                    }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetIsClass":
                    if (ObClass(P1()) is { } cls) { var d = new GetIsClassConditionData(); d.Class = new FormLinkOrIndex<IClassGetter>(d, cls.FormKey); data = d; }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetStage":
                case "GetQuestRunning":
                    if (obQuests.TryGetValue(P1(), out var q))
                    {
                        if (c.Function.ToString() == "GetStage") { var d = new GetStageConditionData(); d.Quest = new FormLinkOrIndex<IQuestGetter>(d, q.FormKey); data = d; }
                        else { var d = new GetQuestRunningConditionData(); d.Quest = new FormLinkOrIndex<IQuestGetter>(d, q.FormKey); data = d; }
                    }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetStageDone":
                    if (obQuests.TryGetValue(P1(), out var qd)) { var d = new GetStageDoneConditionData { Stage = c.SecondParameter }; d.Quest = new FormLinkOrIndex<IQuestGetter>(d, qd.FormKey); data = d; }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetQuestVariable":
                    if (QuestVariableName(P1(), c.SecondParameter) is { } varName)
                    {
                        var g = QuestVariable(P1(), varName);
                        var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, g.FormKey); data = d;
                        target = false;
                    }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetGlobalValue":
                    if (ObGlobal(P1()) is { } glob) { var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, glob.FormKey); data = d; target = false; }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetDeadCount":
                    if (Actor(P1()) is Npc dead) { var d = new GetDeadCountConditionData(); d.Npc = new FormLinkOrIndex<INpcGetter>(d, dead.FormKey); data = d; }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetItemCount":
                    if (Item(P1()) is { } it) { var d = new GetItemCountConditionData(); d.ItemOrList = new FormLinkOrIndex<IItemOrListGetter>(d, it.FormKey); data = d; }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetInCell":
                    if (interiorsBySource.TryGetValue(P1(), out var cell)) { var d = new GetInCellConditionData(); d.Cell = new FormLinkOrIndex<ICellGetter>(d, cell.FormKey); data = d; }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetTalkedToPC": data = new GetTalkedToPCConditionData(); break;
                case "GetRandomPercent": data = new GetRandomPercentConditionData(); break;
                case "GetCurrentTime": data = new GetCurrentTimeConditionData(); break;
                case "GetLevel": data = new GetLevelConditionData(); break;
                case "GetCrimeGold": data = new GetCrimeGoldConditionData(); break;
                case "IsGuard": data = new IsGuardConditionData(); break;
                case "SameSexAsPC": data = new SameSexAsPCConditionData(); break;
                case "GetIsPlayableRace": data = new GetIsPlayableRaceConditionData(); break;
                case "GetDisposition":
                    // The speaker's disposition (Skyrim.Disposition), added directly: it isn't a ConditionData of its own.
                    list.Add(Skyrim.Disposition.Condition(op, value, or));
                    continue;
                case "IsPlayerInJail":
                    {
                        // TMOB_PCInJail, which TMObState keeps while the player is in an Oblivion jail (ObCrime).
                        var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, ScriptGlobal("TMOB_PCInJail").FormKey); data = d; target = false;
                        break;
                    }
                // Skyrim's own of the same name and meaning, on the same subject.
                case "GetIsCurrentPackage":
                    if (ObPackage(P1()) is { } pkg) { var d = new GetIsCurrentPackageConditionData(); d.Package = new FormLinkOrIndex<IPackageGetter>(d, pkg.FormKey); data = d; }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetPCExpelled":
                    if (ObFaction(P1()) is { } ef) { var d = new GetPCExpelledConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, ef.FormKey); data = d; }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetIsClassDefault" or "GetPCIsClass":
                    if (ObClass(P1()) is { } dc)
                    {
                        if (c.Function.ToString() == "GetPCIsClass") { var d = new GetPCIsClassConditionData(); d.Class = new FormLinkOrIndex<IClassGetter>(d, dc.FormKey); data = d; }
                        else { var d = new GetIsClassDefaultConditionData(); d.Class = new FormLinkOrIndex<IClassGetter>(d, dc.FormKey); data = d; }
                    }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetEquipped":
                    if (Item(P1()) is { } eq) { var d = new GetEquippedConditionData(); d.ItemOrList = new FormLinkOrIndex<IItemOrListGetter>(d, eq.FormKey); data = d; }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "CanPayCrimeGold":
                {
                    // Oblivion's one bounty, kept on Cyrodiil's crime faction (ObCrime).
                    var d = new CanPayCrimeGoldConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, LandCrime(false).FormKey); data = d;
                    break;
                }
                case "GetFactionRankDifference":
                    if (ObFaction(P1()) is { } rf && PlacedRef(Resolve(source, (uint)c.SecondParameter)) is { } rr)
                    {
                        var d = new GetFactionRankDifferenceConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, rf.FormKey);
                        d.TargetNpc = new FormLinkOrIndex<IPlacedNpcGetter>(d, rr); data = d;
                    }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "IsCellOwner":
                    if (interiorsBySource.TryGetValue(P1(), out var oc) && (Actor(Resolve(source, (uint)c.SecondParameter)) is Npc ownerNpc ? ownerNpc.FormKey : ObFaction(Resolve(source, (uint)c.SecondParameter))?.FormKey) is { } ownerKey)
                    {
                        var d = new IsCellOwnerConditionData(); d.Cell = new FormLinkOrIndex<ICellGetter>(d, oc.FormKey); d.Owner = new FormLinkOrIndex<IOwnerGetter>(d, ownerKey); data = d;
                    }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetInCellParam":
                    if (interiorsBySource.TryGetValue(P1(), out var pc) && PlacedRef(Resolve(source, (uint)c.SecondParameter)) is { } pr)
                    {
                        var d = new GetInCellParamConditionData(); d.Cell = new FormLinkOrIndex<ICellGetter>(d, pc.FormKey); d.Target = new FormLinkOrIndex<IPlacedSimpleGetter>(d, pr); data = d;
                    }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetInWorldspace":
                    if (worldspaces.GetValueOrDefault(P1()) is { } ws) { var d = new GetInWorldspaceConditionData(); d.WorldspaceOrList = new FormLinkOrIndex<IWorldspaceOrListGetter>(d, ws.FormKey); data = d; }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                // A reference named: the persistent one placed for it (the scripts', dialogue's and packages' references are).
                case "GetDistance" or "GetInSameCell" or "GetIsReference" or "GetLineOfSight" or "GetDetected" or "SameSex" or "SameRace" or "GetShouldAttack" or "GetHeadingAngle":
                {
                    if (PlacedRef(P1()) is not { } target2) { unknownHolds = Holds(op, 0, value); break; }
                    switch (c.Function.ToString())
                    {
                        case "GetDistance": { var d = new GetDistanceConditionData(); d.Target = new FormLinkOrIndex<IPlacedSimpleGetter>(d, target2); data = d; break; }
                        case "GetInSameCell": { var d = new GetInSameCellConditionData(); d.Target = new FormLinkOrIndex<IPlacedSimpleGetter>(d, target2); data = d; break; }
                        case "GetIsReference": { var d = new GetIsReferenceConditionData(); d.Target = new FormLinkOrIndex<IPlacedSimpleGetter>(d, target2); data = d; break; }
                        case "GetLineOfSight": { var d = new GetLineOfSightConditionData(); d.Target = new FormLinkOrIndex<IPlacedSimpleGetter>(d, target2); data = d; break; }
                        case "GetHeadingAngle": { var d = new GetHeadingAngleConditionData(); d.Target = new FormLinkOrIndex<IPlacedSimpleGetter>(d, target2); data = d; break; }
                        case "GetDetected": { var d = new GetDetectedConditionData(); d.TargetNpc = new FormLinkOrIndex<IPlacedNpcGetter>(d, target2); data = d; break; }
                        case "SameSex": { var d = new SameSexConditionData(); d.TargetNpc = new FormLinkOrIndex<IPlacedNpcGetter>(d, target2); data = d; break; }
                        case "SameRace": { var d = new SameRaceConditionData(); d.TargetNpc = new FormLinkOrIndex<IPlacedNpcGetter>(d, target2); data = d; break; }
                        default: { var d = new GetShouldAttackConditionData(); d.TargetNpc = new FormLinkOrIndex<IPlacedNpcGetter>(d, target2); data = d; break; }
                    }
                    break;
                }
                // A reference's script variable (by its index in the script of the reference's base): the translated
                // script's property of it, read as Papyrus keeps it (an auto property's "::v_<name>_var").
                case "GetScriptVariable":
                    if (PlacedRef(P1()) is { } sref && ScriptVariableName(P1(), c.SecondParameter) is { } sv)
                    {
                        var d = new GetVMScriptVariableConditionData { VariableName = "::v_" + ObPapyrus.Clean(sv) + "_var" };
                        d.Target = new FormLinkOrIndex<IPlacedSimpleGetter>(d, sref); data = d;
                    }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                // An attribute or skill: Oblivion's own (ObStats), mirrored where conditions can read them: the player's in
                // a global (TMObPlayer keeps them), another's in a faction rank (TMObActor). Health, magicka and fatigue
                // are Skyrim's own.
                case "GetActorValue":
                {
                    int av = c.FirstParameter;
                    int stat = av <= 7 ? av : av is >= 12 and <= 32 ? 8 + av - 12 : av is >= 33 and <= 36 ? 29 + av - 33 : -1;
                    if (stat >= 0)
                    {
                        if (target)
                        {
                            var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, PlayerStatGlobal(stat).FormKey); data = d; target = false;
                        }
                        else { var d = new GetFactionRankConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, StatFaction(stat).FormKey); data = d; }
                    }
                    else if (av is 8 or 9 or 10)
                        data = new GetActorValueConditionData { ActorValue = av switch { 8 => ActorValue.Health, 9 => ActorValue.Magicka, _ => ActorValue.Stamina } };
                    else unknownHolds = Holds(op, 0, value);
                    break;
                }
                case "GetPCFame" or "GetPCInfamy":
                {
                    var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, ScriptGlobal(c.Function.ToString() == "GetPCFame" ? "TMOB_PCFame" : "TMOB_PCInfamy").FormKey); data = d; target = false;
                    break;
                }
                // The player's misc stat (TMObState.MiscStat), in a global TMObPlayer keeps.
                case "GetPCMiscStat" when c.FirstParameter is >= 0 and <= 33:
                {
                    var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, MiscStatGlobal(c.FirstParameter).FormKey); data = d; target = false;
                    break;
                }
                // The player's birthsign (TMObState's choice or the standing stone, by TMObState's numbering) in a global:
                // 1 when it is this one.
                case "GetIsPlayerBirthsign":
                {
                    int sign = scriptWorld?.Birthsigns.FindIndex(b => string.Equals(b, records.GetValueOrDefault(P1())?.EditorID, StringComparison.OrdinalIgnoreCase)) ?? -1;
                    bool isIt = op == CompareOperator.EqualTo && value == 1 || op == CompareOperator.NotEqualTo && value == 0;
                    bool isNot = op == CompareOperator.EqualTo && value == 0 || op == CompareOperator.NotEqualTo && value == 1;
                    if (sign >= 0 && (isIt || isNot))
                    {
                        var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, ScriptGlobal("TMOB_PCBirthsign").FormKey);
                        list.Add(Cond(d, isIt ? CompareOperator.EqualTo : CompareOperator.NotEqualTo, sign, false, or));
                        continue;
                    }
                    unknownHolds = Holds(op, 0, value);
                    break;
                }
                // Evil: in an evil faction and no other (a person; ObEvil keeps OBEvil so as factions change).
                case "IsActorEvil":
                {
                    var d = new GetInFactionConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, EvilFaction().FormKey); data = d;
                    break;
                }
                // A guard spoken to while the player has a bounty.
                case "IsTurnArrest" when !or && (op == CompareOperator.EqualTo && value == 1 || op == CompareOperator.NotEqualTo && value == 0):
                {
                    list.Add(Cond(new IsGuardConditionData(), CompareOperator.EqualTo, 1, target, false));
                    var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, ScriptGlobal("TMOB_PCCrimeGold").FormKey);
                    list.Add(Cond(d, CompareOperator.GreaterThan, 0, false, false));
                    continue;
                }
                case "GetGold": data = new GetGoldConditionData(); break;
                case "GetDisease": data = new GetDiseaseConditionData(); break;
                case "IsPlayersLastRiddenHorse": data = new IsPlayersLastRiddenMountConditionData(); break;
                case "IsSpellTarget":
                    if (ObSpell(P1()) is { } st) { var d = new IsSpellTargetConditionData(); d.MagicItem = new FormLinkOrIndex<IMagicItemGetter>(d, st.FormKey); data = d; }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "GetPCFactionMurder" or "GetPCFactionAttack":
                    if (ObFaction(P1()) is { } cf)
                    {
                        if (c.Function.ToString() == "GetPCFactionMurder") { var d = new GetPCFactionMurderConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, cf.FormKey); data = d; }
                        else { var d = new GetPCFactionAttackConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, cf.FormKey); data = d; }
                    }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                // GetCrime criminal type: Oblivion's crime types (0 steal, 1 pickpocket, 2 trespass, 3 attack, 4 murder) are Skyrim's.
                case "GetCrime":
                    if (PlacedRef(P1()) is { } criminal || P1() == new FormKey(ModKey.FromFileName("Oblivion.esm"), 0x14))
                    {
                        var d = new GetCrimeConditionData { CrimeType = (CrimeType)c.SecondParameter };
                        d.Criminal = new FormLinkOrIndex<IPlacedNpcGetter>(d, PlacedRef(P1()) ?? PlayerRefKey); data = d;
                    }
                    else unknownHolds = Holds(op, 0, value);
                    break;
                case "IsTrespassing": data = new IsTrespassingConditionData(); break;
                case "IsInMyOwnedCell": data = new IsInMyOwnedCellConditionData(); break;
                case "IsInInterior": data = new IsInInteriorConditionData(); break;
                case "IsRidingHorse": data = new IsRidingMountConditionData(); break;
                case "GetDead": data = new GetDeadConditionData(); break;
                case "GetFriendHit": data = new GetFriendHitConditionData(); break;
                case "GetIsCreature": data = new GetIsCreatureConditionData(); break;
                case "GetSitting": data = new GetSittingConditionData(); break;
                case "IsRaining": data = new IsRainingConditionData(); break;
                case "IsSnowing": data = new IsSnowingConditionData(); break;
                case "IsPleasant": data = new IsPleasantConditionData(); break;
                case "IsSneaking": data = new IsSneakingConditionData(); break;
                case "GetClothingValue": data = new GetClothingValueConditionData(); break;
                case "GetIsGhost": data = new GetIsGhostConditionData(); break;
                case "GetIsAlerted": data = new GetIsAlertedConditionData(); break;
                case "GetClassDefaultMatch": data = new GetClassDefaultMatchConditionData(); break;
                case "GetAlarmed": data = new GetAlarmedConditionData(); break;
                case "IsTorchOut": data = new IsTorchOutConditionData(); break;
                case "IsWeaponOut": data = new IsWeaponOutConditionData(); break;
                case "SameRaceAsPC": data = new SameRaceAsPCConditionData(); break;
                case "IsInCombat": data = new IsInCombatConditionData(); break;
                case "IsPCAMurderer": data = new IsPCAMurdererConditionData(); break;
                case "GetTimeDead": data = new GetTimeDeadConditionData(); break;
                case "GetAttacked": data = new GetAttackedConditionData(); break;
                case "GetLocked": data = new GetLockedConditionData(); break;
                case "GetDisabled": data = new GetDisabledConditionData(); break;
                case "GetDayOfWeek": data = new GetDayOfWeekConditionData(); break;
                case "GetCurrentAIProcedure": data = new GetCurrentAIProcedureConditionData(); break;
                case "IsPlayerMovingIntoNewSpace": data = new IsPlayerMovingIntoNewSpaceConditionData(); break;
                case "IsContinuingPackagePCNear": data = new IsContinuingPackagePCNearConditionData(); break;
                case "GetTrespassWarningLevel": data = new GetTrespassWarningLevelConditionData(); break;
                case "IsActorAVictim": data = new IsActorAVictimConditionData(); break;
                // The player isn't on an Xbox.
                case "IsXBox": unknownHolds = Holds(op, 0, value); break;
                case "GetPlayerInSEWorld":
                {
                    var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, ScriptGlobal("TMOB_PCInSEWorld").FormKey); data = d; target = false;
                    break;
                }
                case "GetAmountSoldStolen":
                {
                    var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, ScriptGlobal("TMOB_PCAmountSoldStolen").FormKey); data = d; target = false;
                    break;
                }
                case "GetPersuasionNumber": case "GetTotalPersuasionNumber":
                    unknownHolds = Holds(op, 0, value); break;
                default: unknownHolds = true; break;   // thresholds and states Skyrim can't test pass
            }
            if (data is not null) list.Add(Cond(data, op, value, target, or));
            else
            {
                if (c.Function.ToString() != "IsXBox") lock (ConditionsUntested) ConditionsUntested[c.Function.ToString()] = ConditionsUntested.GetValueOrDefault(c.Function.ToString()) + 1;
                if (unknownHolds == false && !or)
                {
                    lock (NeverHoldingBy) NeverHoldingBy[c.Function.ToString()] = NeverHoldingBy.GetValueOrDefault(c.Function.ToString()) + 1;
                    return null;
                }
            }
        }
        return list;
    }

    // ---- Result scripts ----

    /// <summary>
    /// The parts of a result script Skyrim can act on (SetStage, StartQuest, StopQuest, set of globals and quest
    /// variables, AddItem/RemoveItem on the player, StartCombat player), as TMMwInfo properties.
    /// </summary>
    /// <summary>A response's script with its Begin and End fragments (the script's, or inherited from TMObLine).</summary>
    static DialogResponsesAdapter LineAdapter(ScriptEntry entry, string script) => new()
    {
        Scripts = [entry],
        ScriptFragments = new ScriptFragments
        {
            ExtraBindDataVersion = 2,
            FileName = script,
            OnBegin = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = script, FragmentName = "Fragment_Begin" },
            OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = script, FragmentName = "Fragment_End" },
        },
    };

    /// <summary>The regex's subset of a result script as TMMwInfo's properties (on TMObLine, which extends it).</summary>
    ScriptEntry? Result(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        var stageQuests = new List<FormKey>(); var stages = new List<int>();
        var startQuests = new List<FormKey>(); var stopQuests = new List<FormKey>();
        var globals = new List<FormKey>(); var values = new List<float>();
        var items = new List<FormKey>(); var counts = new List<int>();
        bool attack = false;
        int disposition = 0;
        foreach (var raw in source.Split('\n'))
        {
            string line = raw.Trim();
            int comment = line.IndexOf(';');
            if (comment >= 0) line = line[..comment].Trim();
            if (line.Length == 0) continue;
            Match m;
            if ((m = Regex.Match(line, @"^setstage\s+(\w+)\s+(\d+)", RegexOptions.IgnoreCase)).Success)
            {
                if (QuestByEditorId(m.Groups[1].Value) is { } k && obQuests.TryGetValue(k, out var q)) { stageQuests.Add(q.FormKey); stages.Add(int.Parse(m.Groups[2].Value)); }
            }
            else if ((m = Regex.Match(line, @"^(startquest|stopquest)\s+(\w+)", RegexOptions.IgnoreCase)).Success)
            {
                if (QuestByEditorId(m.Groups[2].Value) is { } k && obQuests.TryGetValue(k, out var q))
                    (m.Groups[1].Value.Equals("startquest", StringComparison.OrdinalIgnoreCase) ? startQuests : stopQuests).Add(q.FormKey);
            }
            else if ((m = Regex.Match(line, @"^set\s+(\w+)\.(\w+)\s+to\s+(-?[\d.]+)\s*$", RegexOptions.IgnoreCase)).Success)
            {
                if (QuestByEditorId(m.Groups[1].Value) is { } k) { globals.Add(QuestVariable(k, m.Groups[2].Value).FormKey); values.Add(float.Parse(m.Groups[3].Value, System.Globalization.CultureInfo.InvariantCulture)); }
            }
            else if ((m = Regex.Match(line, @"^set\s+(\w+)\s+to\s+(-?[\d.]+)\s*$", RegexOptions.IgnoreCase)).Success)
            {
                if (FormByEditorId(m.Groups[1].Value) is { } k && ObGlobal(k) is { } g) { globals.Add(g.FormKey); values.Add(float.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)); }
            }
            else if ((m = Regex.Match(line, @"^player\.(additem|removeitem)\s+(\w+)\s+(\d+)", RegexOptions.IgnoreCase)).Success)
            {
                int count = int.Parse(m.Groups[3].Value) * (m.Groups[1].Value.Equals("removeitem", StringComparison.OrdinalIgnoreCase) ? -1 : 1);
                FormKey? item = m.Groups[2].Value.Equals("gold001", StringComparison.OrdinalIgnoreCase) ? new FormKey(ModKey.FromFileName("Skyrim.esm"), 0xF)
                    : FormByEditorId(m.Groups[2].Value) is { } k ? Item(k)?.FormKey : null;
                if (item is { } i) { items.Add(i); counts.Add(count); }
            }
            else if (Regex.IsMatch(line, @"^startcombat\s+player", RegexOptions.IgnoreCase)) attack = true;
            // The speaker's disposition towards the player.
            else if ((m = Regex.Match(line, @"^moddisposition\s+player\s*,?\s*(-?\d+)", RegexOptions.IgnoreCase)).Success)
                disposition += int.Parse(m.Groups[1].Value);
        }
        if (stageQuests.Count + startQuests.Count + stopQuests.Count + globals.Count + items.Count == 0 && !attack && disposition == 0) return null;
        var entry = new ScriptEntry { Name = InfoScript };
        static ScriptObjectListProperty Objects(string name, IEnumerable<FormKey> keys)
        {
            var p = new ScriptObjectListProperty { Name = name };
            foreach (var k in keys) p.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(k) });
            return p;
        }
        if (stageQuests.Count > 0) { entry.Properties.Add(Objects("StageQuests", stageQuests)); entry.Properties.Add(new ScriptIntListProperty { Name = "Stages", Data = [.. stages] }); }
        if (startQuests.Count > 0) entry.Properties.Add(Objects("StartQuests", startQuests));
        if (stopQuests.Count > 0) entry.Properties.Add(Objects("StopQuests", stopQuests));
        if (globals.Count > 0) { entry.Properties.Add(Objects("Globals", globals)); entry.Properties.Add(new ScriptFloatListProperty { Name = "GlobalValues", Data = [.. values] }); }
        if (items.Count > 0) { entry.Properties.Add(Objects("Items", items)); entry.Properties.Add(new ScriptIntListProperty { Name = "ItemCounts", Data = [.. counts] }); }
        if (attack) entry.Properties.Add(new ScriptBoolProperty { Name = "Attack", Data = true });
        if (disposition != 0) entry.Properties.Add(new ScriptIntProperty { Name = "Disposition", Data = disposition });
        return entry;
    }
}
