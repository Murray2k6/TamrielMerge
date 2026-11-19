using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's voice topics (DIAL type 1, the "barks") as Skyrim's (user, 2026-10-10: "they do have some spoken dialog with
/// not interacting"; "Barks first, then scripted"): each race's recorded greetings in passing, idle remarks, warnings to
/// intruders and thieves, alarms and battle cries, spoken with Morrowind's own recordings (Sound\Vo\...) rather than
/// generated voices. Each voice topic is a topic of the MWVoices quest that Skyrim's engine never says by itself: it is
/// said when OpenMW says it (TMMwVoices holds them): Hello and Idle by TMMwActorAI's greeting and idle remarks
/// (actors.cpp updateGreetingState, playIdleDialogue), Attack as a fight begins, Hit as a person is hurt (iVoiceHitOdds),
/// Flee as it turns to run, Thief and Intruder by witnesses of theft and of trespass (TMMwLaw); never over a line already
/// being said (TMMwSayLine marks it). Its responses keep Morrowind's filters (race, sex, speaker, faction...), are picked
/// at random as Morrowind picks them, and are said only by Morrowind's people (the MWPeople faction every converted
/// person gets). A response's script notes hold its recording
/// ("mwvoice:vo\d\m\...mp3"): the voice planner packs that file for every voice type that can say the line.
/// Built after everything else, so the records made before keep their FormIDs.
/// </summary>
public sealed partial class MwPluginBuilder
{
    /// <summary>Script notes of a response spoken with a Morrowind recording: the prefix, then the file under Sound.</summary>
    public const string RecordingNote = "mwvoice:";

    /// <summary>Morrowind's voice topics, as TMMwVoices' properties.</summary>
    static readonly string[] BarkTopics = ["Hello", "Idle", "Intruder", "Thief", "Alarm", "Attack", "Hit", "Flee"];

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
        // Morrowind's dialogue for Morrowind's people (the responses made so far: topics, greetings, services).
        Skyrim.DialogueOwners.Restrict(mod, people, Console.WriteLine);

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
        var voices = new ScriptEntry { Name = "TMMwVoices" };
        foreach (var topic in dialogueData.Topics.Values.Where(t => t.Type == MwDialogue.TopicType.Voice))
        {
            string? name = BarkTopics.FirstOrDefault(b => b.Equals(topic.Name, StringComparison.OrdinalIgnoreCase));
            if (name is null) { Problems.Add($"voice topic {topic.Name}: not one of Morrowind's eight"); continue; }
            var main = new DialogTopic(mod)
            {
                EditorID = "MWVoice" + Sanitize(topic.Name),
                Quest = quest.ToNullableLink<IQuestGetter>(),
                Priority = 50,
                Category = DialogTopic.CategoryEnum.Misc,
                Subtype = DialogTopic.SubtypeEnum.Custom,
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
                    // The speaker is saying it until its last word (TMMwSayLine): no other voice line over it.
                    VirtualMachineAdapter = new DialogResponsesAdapter
                    {
                        Scripts = [new ScriptEntry { Name = "TMMwSayLine" }],
                        ScriptFragments = new ScriptFragments
                        {
                            ExtraBindDataVersion = 2,
                            FileName = "TMMwSayLine",
                            OnBegin = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMMwSayLine", FragmentName = "Fragment_Begin" },
                            OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMMwSayLine", FragmentName = "Fragment_End" },
                        },
                    },
                };
                if (info.Result is { Length: > 0 }) BarksWithResults++;
                main.Responses.Add(r);
                BarkCount++;
            }
            if (main.Responses.Count == 0) continue;
            mod.DialogTopics.Add(main);
            voices.Properties.Add(new ScriptObjectProperty { Name = name + "Topic", Object = main.ToLink<ISkyrimMajorRecordGetter>() });
            BarkTopicCount++;
        }
        quest.VirtualMachineAdapter = new QuestAdapter { Scripts = [voices] };
        // Skyrim's own lines kept from Morrowind's people: its engine says none of Morrowind's (they are said by OpenMW's
        // rules, above), so a silent one at every moment of Skyrim's.
        SkyrimMomentsSilenced = Skyrim.ForeignVoices.Silence(mod, skyrimEsm!, quest, people.FormKey, "MW", new Dictionary<DialogTopic.SubtypeEnum, DialogTopic>());
    }

    public int SkyrimMomentsSilenced { get; private set; }

    /// <summary>Voice lines with a result script (OpenMW runs it as the line is said).</summary>
    public int BarksWithResults { get; private set; }
}
