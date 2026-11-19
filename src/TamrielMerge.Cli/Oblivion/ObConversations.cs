using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's conversations between people (the Conversation tab's topics; the Construction Set wiki's Conversation Tab
/// and Editing Dialogue pages, read 2026-10-23), run by TMObConversations on the OBDialogue quest:
/// <list type="bullet">
/// <item>Every Conversation topic as a Skyrim topic of OBDialogue no player menu shows (no branch); each response keeps
/// Oblivion's conditions, lines, recordings and result script (ObResultScripts attaches it, as for the player's), what
/// Oblivion runs on the listener ("Run on Target") on the OBListener alias, which the controller fills before each line.</item>
/// <item>"Link From": a response is said only right after one of those topics (OBConvFrom, the topic just said); one with
/// none only opens a conversation (HELLO's start random ones; a StartConversation topic a scripted one), except GOODBYE's,
/// which close any.</item>
/// <item>"Link To" and "Next Speaker" (the INFO DATA's second byte, which Mutagen reads as the high byte of the dialogue
/// type: 0 the target, 1 the speaker again, 2 either) on the line (TMObLine ConvLinks, ConvNext), "Goodbye" ending it
/// (ConvGoodbye); topics not said twice in one conversation unless a quest of theirs allows repeated conversation topics.</item>
/// </list>
/// </summary>
public sealed partial class ObPluginBuilder
{
    public int ConversationTopics { get; private set; }
    public int ConversationResponses { get; private set; }
    /// <summary>Oblivion's people given to the conversations (TMObActor.Conversations).</summary>
    public int ConversationPeople { get; private set; }

    void BuildConversations(Dictionary<FormKey, (Ob.IDialogTopicGetter Topic, List<(Ob.IDialogItemGetter Item, Ob.IOblivionModGetter Source)> Items)> topics)
    {
        var conv = topics.Values.Where(t => t.Topic.DialogType == Ob.DialogType.Conversation).OrderBy(t => t.Topic.EditorID, StringComparer.OrdinalIgnoreCase).ToList();
        if (conv.Count == 0) return;
        var quest = obDialogueQuest!;
        // The listener, whom Oblivion's "Run on Target" conditions test.
        uint listenerId = quest.NextAliasID ?? (uint)quest.Aliases.Count;
        quest.Aliases.Add(new QuestAlias { ID = listenerId, Name = "OBListener", Flags = QuestAlias.Flag.Optional | QuestAlias.Flag.AllowReserved | QuestAlias.Flag.AllowReuseInQuest });
        quest.NextAliasID = listenerId + 1;
        var from = new GlobalShort(mod) { EditorID = "OBConvFrom", Data = 0 };
        mod.Globals.Add(from);
        // Each topic's number (1 on): its place in OBConvTopics.
        var index = new Dictionary<FormKey, int>();
        var made = new Dictionary<FormKey, DialogTopic>();
        var list = new FormList(mod) { EditorID = "OBConvTopics" };
        var repeatable = new FormList(mod) { EditorID = "OBConvRepeatable" };
        foreach (var (topic, items) in conv)
        {
            var t = new DialogTopic(mod)
            {
                EditorID = "OBConv" + Sanitize(topic.EditorID ?? topic.FormKey.ID.ToString("X6")),
                Name = topic.Name ?? topic.EditorID ?? "",
                Quest = quest.ToNullableLink<IQuestGetter>(),
                Priority = 50,
                Category = DialogTopic.CategoryEnum.Misc,
                Subtype = DialogTopic.SubtypeEnum.Custom,
                Responses = [],
            };
            made[topic.FormKey] = t;
            // Scripts name it (StartConversation, Say): their Topic properties are filled from obTopics.
            obTopics[topic.FormKey] = t;
            index[topic.FormKey] = list.Items.Count + 1;
            list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(t.FormKey));
            // Said again in one conversation when a quest of its responses allows repeated conversation topics.
            if (items.Any(i => !i.Item.Quest.IsNull && records.GetValueOrDefault(i.Item.Quest.FormKey) is Ob.IQuestGetter q && q.Data?.Flags.HasFlag(Ob.Quest.Flag.AllowRepeatedConversationTopics) == true))
                repeatable.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(t.FormKey));
        }
        mod.FormLists.Add(list);
        mod.FormLists.Add(repeatable);
        foreach (var (topic, items) in conv)
        {
            var t = made[topic.FormKey];
            bool goodbye = string.Equals(topic.EditorID, "GOODBYE", StringComparison.OrdinalIgnoreCase);
            foreach (var (item, source) in items)
            {
                if (Response(item, source) is not { } r) continue;
                // What Oblivion runs on the target, on the listener.
                foreach (var c in r.Conditions)
                    if (c.Data.RunOnType == Condition.RunOnType.Target)
                    {
                        c.Data.RunOnType = Condition.RunOnType.QuestAlias;
                        c.Data.RunOnTypeIndex = (int)listenerId;
                    }
                // Link From: said right after one of its topics; none, only to open (GOODBYE's close any).
                var linkFrom = item.LinkFrom.Select(l => l.FormKey).Where(index.ContainsKey).Distinct().ToList();
                var gate = new List<Condition>();
                if (linkFrom.Count > 0)
                    for (int k = 0; k < linkFrom.Count; k++)
                    {
                        var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, from.FormKey);
                        gate.Add(Cond(d, CompareOperator.EqualTo, index[linkFrom[k]], false, k < linkFrom.Count - 1));
                    }
                else if (!goodbye)
                {
                    var d = new GetGlobalValueConditionData(); d.Global = new FormLinkOrIndex<IGlobalGetter>(d, from.FormKey);
                    gate.Add(Cond(d, CompareOperator.EqualTo, 0, false, false));
                }
                r.Conditions.InsertRange(0, gate);
                // The line's part in the conversation (TMObLine).
                if (r.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == LineScript || s.Name.StartsWith("OB_R_", StringComparison.OrdinalIgnoreCase)) is { } line)
                {
                    line.Properties.Add(new ScriptIntProperty { Name = "ConvTopic", Data = index[topic.FormKey] });
                    line.Properties.Add(new ScriptIntProperty { Name = "ConvNext", Data = ((int)(item.Data?.DialogType ?? 0) >> 8) & 0xFF });
                    if (item.Data?.Flags.HasFlag(Ob.DialogItem.Flag.Goodbye) == true) line.Properties.Add(new ScriptBoolProperty { Name = "ConvGoodbye", Data = true });
                    var links = new ScriptObjectListProperty { Name = "ConvLinks" };
                    foreach (var to in item.Choices.Select(l => l.FormKey).Where(made.ContainsKey).Distinct())
                        links.Objects.Add(new ScriptObjectProperty { Object = made[to].ToLink<ISkyrimMajorRecordGetter>() });
                    if (links.Objects.Count > 0) line.Properties.Add(links);
                    line.Properties.Add(new ScriptObjectProperty { Name = "Conversations", Object = quest.ToLink<ISkyrimMajorRecordGetter>() });
                }
                responseTopics[r] = topic.EditorID ?? "";
                t.Responses.Add(r);
                ConversationResponses++;
            }
            mod.DialogTopics.Add(t);
            if (t.Responses.Count > 0) ConversationTopics++;
        }
        // The controller.
        var hello = conv.FirstOrDefault(c => string.Equals(c.Topic.EditorID, "HELLO", StringComparison.OrdinalIgnoreCase)).Topic;
        var bye = conv.FirstOrDefault(c => string.Equals(c.Topic.EditorID, "GOODBYE", StringComparison.OrdinalIgnoreCase)).Topic;
        var props = new List<ScriptProperty>
        {
            new ScriptObjectProperty { Name = "ConvFrom", Object = from.ToLink<ISkyrimMajorRecordGetter>() },
            new ScriptObjectProperty { Name = "Topics", Object = list.ToLink<ISkyrimMajorRecordGetter>() },
            new ScriptObjectProperty { Name = "Repeatable", Object = repeatable.ToLink<ISkyrimMajorRecordGetter>() },
            new ScriptIntProperty { Name = "ListenerAlias", Data = (int)listenerId },
            new ScriptObjectProperty { Name = "PlayerRef", Object = new FormLink<ISkyrimMajorRecordGetter>(PlayerRefKey) },
        };
        if (hello is not null) props.Add(new ScriptObjectProperty { Name = "Hello", Object = made[hello.FormKey].ToLink<ISkyrimMajorRecordGetter>() });
        if (bye is not null) props.Add(new ScriptObjectProperty { Name = "Goodbye", Object = made[bye.FormKey].ToLink<ISkyrimMajorRecordGetter>() });
        // Oblivion's settings (Oblivion.esm's own where it sets them, else the game's: cs.uesp.net, read 10-23).
        props.Add(new ScriptFloatProperty { Name = "RadiusExterior", Data = Gmst("fAISocialRadiusToTriggerConversation", 1800) });
        props.Add(new ScriptFloatProperty { Name = "RadiusInterior", Data = Gmst("fAISocialRadiusToTriggerConversationInterior", 500) });
        props.Add(new ScriptFloatProperty { Name = "ChanceExterior", Data = Gmst("fAISocialchanceForConversation", 100) });
        props.Add(new ScriptFloatProperty { Name = "ChanceInterior", Data = Gmst("fAISocialchanceForConversationInterior", 25) });
        props.Add(new ScriptFloatProperty { Name = "TalkAgain", Data = Gmst("fAItalktoNPCtimer", 60) });
        props.Add(new ScriptFloatProperty { Name = "TalkSameAgain", Data = Gmst("fAItalktosameNPCtimer", 120) });
        quest.VirtualMachineAdapter ??= new QuestAdapter();
        quest.VirtualMachineAdapter.Scripts.Add(new ScriptEntry { Name = "TMObConversations", Properties = [.. props] });
    }

    /// <summary>An Oblivion float game setting: the plugins' last value, else <paramref name="fallback"/> (the game's own).</summary>
    float Gmst(string name, float fallback) =>
        plugins.SelectMany(p => p.GameSettings).LastOrDefault(g => string.Equals(g.EditorID, name, StringComparison.OrdinalIgnoreCase)) is Ob.IGameSettingFloatGetter f && f.Data is { } v ? v : fallback;
}
