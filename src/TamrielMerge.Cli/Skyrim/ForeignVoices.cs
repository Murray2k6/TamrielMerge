using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Keeps Skyrim's own voices out of another game's people. Skyrim's engine says its barks by itself (greetings in
/// passing, idle remarks, combat and detection lines, crimes seen, services), choosing among every topic of that
/// moment by priority: a Morrowind or Oblivion person would say Skyrim's lines in Skyrim's voice wherever its own game
/// says nothing, or says it at another moment. So each of those moments gets, for that game's people (a faction every
/// converted person has), a silent line after the game's own ones (when it has a topic for the moment) or alone, in the
/// game's voice quest, above Skyrim's priority. A greeting in passing is silenced only outside a conversation: the
/// game's own greeting at a conversation's start is a Hello too (IsInDialogueWithPlayer 1).
/// </summary>
public static class ForeignVoices
{
    /// <summary>Skyrim's bark moments: every SNAM code (<see cref="TopicSubtypes"/>: what Skyrim goes by) of Skyrim.esm's
    /// Combat, Detection, Favors and Misc topics, without shared infos, with the category Skyrim.esm gives it.</summary>
    public static List<(DialogTopic.SubtypeEnum Subtype, DialogTopic.CategoryEnum Category)> Barks(ISkyrimModGetter skyrim) =>
        [.. skyrim.DialogTopics
            .Where(t => t.Category is DialogTopic.CategoryEnum.Combat or DialogTopic.CategoryEnum.Detection or DialogTopic.CategoryEnum.Favors or DialogTopic.CategoryEnum.Misc)
            .GroupBy(t => t.SubtypeName.Type)
            .Where(g => g.Key is not ("IDAT" or "CUST" or "SCEN" or "PFGT") && TopicSubtypes.Of(g.Key) is not null)
            .Select(g => (TopicSubtypes.Of(g.Key)!.Value, g.First().Category)).OrderBy(x => x.Value.ToString())];
    /// <summary>
    /// The silent lines (see the class summary) in <paramref name="quest"/> for the people of <paramref name="people"/>:
    /// added to the game's own topic of a moment (<paramref name="own"/>, after its lines) or in a topic of their own.
    /// Returns how many moments were silenced.
    /// </summary>
    /// <param name="greeting">A faction a person is in while walking up to start a conversation (Oblivion's OBGreetNow),
    /// when the game has one: the silent greeting stands aside for them too. Skyrim picks a walk-up's greeting a moment
    /// before the conversation counts as begun, so "not in a conversation" alone let the silent line take the Emperor's
    /// greeting in the prisoner's cell, the silent topic's quest being weighed first (runs r74 to r82, 2026-11-15: the
    /// conversation opened with nothing said and nothing of his to ask, once he wore a face of his own and so was among
    /// Oblivion's people; borrowing a Skyrim person's record before, he wasn't).</param>
    public static int Silence(ISkyrimMod mod, ISkyrimModGetter skyrim, Quest quest, FormKey people, string prefix, IReadOnlyDictionary<DialogTopic.SubtypeEnum, DialogTopic> own, FormKey? greeting = null)
    {
        int count = 0;
        foreach (var (subtype, category) in Barks(skyrim))
        {
            var topic = own.GetValueOrDefault(subtype);
            if (topic is null)
            {
                topic = new DialogTopic(mod)
                {
                    EditorID = $"{prefix}Silent{subtype}",
                    Quest = quest.ToNullableLink<IQuestGetter>(),
                    Priority = 50,
                    Category = category,
                    Subtype = subtype,
                    Responses = [],
                };
                mod.DialogTopics.Add(topic);
            }
            var inFaction = new GetInFactionConditionData();
            inFaction.Faction = new FormLinkOrIndex<IFactionGetter>(inFaction, people);
            var silent = new DialogResponses(mod)
            {
                Conditions = [new ConditionFloat { Data = inFaction, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 }],
                Flags = new DialogResponseFlags(),
                Responses = [new DialogResponse { Text = "", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
            };
            if (subtype == DialogTopic.SubtypeEnum.Hello)
            {
                silent.Conditions.Add(new ConditionFloat { Data = new IsInDialogueWithPlayerConditionData(), CompareOperator = CompareOperator.EqualTo, ComparisonValue = 0 });
                if (greeting is { } mark)
                {
                    var walking = new GetInFactionConditionData();
                    walking.Faction = new FormLinkOrIndex<IFactionGetter>(walking, mark);
                    silent.Conditions.Add(new ConditionFloat { Data = walking, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 0 });
                }
            }
            topic.Responses.Add(silent);
            count++;
        }
        return count;
    }

    /// <summary>A greeting that is a conversation's start, not one in passing (IsInDialogueWithPlayer 1).</summary>
    public static ConditionFloat InConversation() => new() { Data = new IsInDialogueWithPlayerConditionData(), CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 };
}
