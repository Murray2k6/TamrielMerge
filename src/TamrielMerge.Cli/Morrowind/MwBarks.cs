using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's voice topics (DIAL type 1, the "barks") as Skyrim's (user, 2026-10-10: "they do have some spoken dialog with
/// not interacting"; "Barks first, then scripted"): each race's recorded greetings in passing, idle remarks, warnings to
/// intruders and thieves, alarms and battle cries, spoken with Morrowind's own recordings (Sound\Vo\...) rather than
/// generated voices. Each voice topic becomes the Skyrim topic of the same moment (Hello, Idle, Trespass, Steal,
/// NormalToCombat, Attack, Hit, Flee) in the MWVoices quest; its responses keep Morrowind's filters (race, sex, speaker,
/// faction...), are picked at random as Morrowind picks them, and are said only by Morrowind's people (the MWPeople
/// faction every converted person gets), so Skyrim's own never use them. A response's script notes hold its recording
/// ("mwvoice:vo\d\m\...mp3"): the voice planner packs that file for every voice type that can say the line.
/// Built after everything else, so the records made before keep their FormIDs.
/// </summary>
public sealed partial class MwPluginBuilder
{
    /// <summary>Script notes of a response spoken with a Morrowind recording: the prefix, then the file under Sound.</summary>
    public const string RecordingNote = "mwvoice:";

    static readonly Dictionary<string, (DialogTopic.CategoryEnum Category, DialogTopic.SubtypeEnum Subtype)> BarkTopics = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Hello"] = (DialogTopic.CategoryEnum.Misc, DialogTopic.SubtypeEnum.Hello),
        ["Idle"] = (DialogTopic.CategoryEnum.Misc, DialogTopic.SubtypeEnum.Idle),
        ["Intruder"] = (DialogTopic.CategoryEnum.Combat, DialogTopic.SubtypeEnum.Trespass),
        ["Thief"] = (DialogTopic.CategoryEnum.Combat, DialogTopic.SubtypeEnum.Steal),
        ["Alarm"] = (DialogTopic.CategoryEnum.Detection, DialogTopic.SubtypeEnum.NormalToCombat),
        ["Attack"] = (DialogTopic.CategoryEnum.Combat, DialogTopic.SubtypeEnum.Attack),
        ["Hit"] = (DialogTopic.CategoryEnum.Combat, DialogTopic.SubtypeEnum.Hit),
        ["Flee"] = (DialogTopic.CategoryEnum.Combat, DialogTopic.SubtypeEnum.Flee),
    };

    public int BarkTopicCount { get; private set; }
    public int BarkCount { get; private set; }

    /// <summary>The voice topics, after every other record (see the class summary).</summary>
    void BuildBarks()
    {
        if (dialogueData is null) return;
        var people = new Faction(mod) { EditorID = "MWPeople", Name = "Morrowind's people", Flags = Faction.FactionFlag.HiddenFromPC, Ranks = [] };
        mod.Factions.Add(people);
        foreach (var npc in mod.Npcs)
            npc.Factions.Add(new RankPlacement { Faction = people.ToLink<IFactionGetter>(), Rank = 0 });

        var quest = new Quest(mod)
        {
            EditorID = "MWVoices",
            Name = "Morrowind Voices",
            Flags = Quest.Flag.StartGameEnabled,
            // Above Skyrim's own generic barks, so Morrowind's people use theirs first.
            Priority = 70,
            Type = Quest.TypeEnum.Misc,
        };
        mod.Quests.Add(quest);
        var isPeople = new ConditionFloat
        {
            CompareOperator = CompareOperator.EqualTo,
            ComparisonValue = 1,
            Data = new GetInFactionConditionData { Faction = new FormLinkOrIndex<IFactionGetter>(new GetInFactionConditionData(), people.FormKey) },
        };
        foreach (var topic in dialogueData.Topics.Values.Where(t => t.Type == MwDialogue.TopicType.Voice))
        {
            if (!BarkTopics.TryGetValue(topic.Name, out var kind)) { Problems.Add($"voice topic {topic.Name}: no Skyrim counterpart"); continue; }
            var main = new DialogTopic(mod)
            {
                EditorID = "MWVoice" + Sanitize(topic.Name),
                Quest = quest.ToNullableLink<IQuestGetter>(),
                Priority = 50,
                Category = kind.Category,
                Subtype = kind.Subtype,
                Responses = [],
            };
            foreach (var info in topic.Infos)
            {
                if (info.Sound is not { Length: > 0 } sound) continue;   // Morrowind's barks are all recorded
                if (Conditions(info) is not { } conditions) continue;
                conditions.Add(isPeople.DeepCopy());
                var r = new DialogResponses(mod)
                {
                    Conditions = conditions,
                    Flags = new DialogResponseFlags { Flags = DialogResponses.Flag.Random },
                    Responses =
                    [
                        new DialogResponse
                        {
                            Text = Substitute(info.Text, info), ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50,
                            ScriptNotes = RecordingNote + sound.Replace('/', '\\').ToLowerInvariant(),
                        },
                    ],
                };
                main.Responses.Add(r);
                BarkCount++;
            }
            if (main.Responses.Count == 0) continue;
            mod.DialogTopics.Add(main);
            BarkTopicCount++;
        }
    }
}
