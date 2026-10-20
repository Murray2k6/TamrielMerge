using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim.Progression;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Morrowind's and Oblivion's trainers and skill books in Skyrim's terms (docs/progression.md "The games' levelling on
/// the stones"). A trainer teaches the skills the game lets them (Morrowind's: their three best; Oblivion's: the one its
/// AI data names), each up to the trainer's level in it (Morrowind: their skill; Oblivion: MaximumTrainingLevel):
/// "I'd like training in &lt;skill&gt;." asked of them sets the class that teaches it (a class teaching that skill to
/// that level, as Skyrim's trainers are made) and opens Skyrim's own training menu, which keeps Skyrim's five sessions a
/// level (Oblivion's limit too) and prices. The progression's own skills (Spear, Mercantile...), which Skyrim's menu
/// can't train, are trained by TMTrainerTopic at the game's price (10 gold a level, Morrowind's iTrainingMod and
/// Oblivion's fTrainingCostMult). A skill book raises its skill by one the first time it is read, as in both games:
/// Skyrim's own book teaching for Skyrim's skills, TMSkillBook for the progression's.
/// </summary>
public static class GameTrainers
{
    public sealed record Trainer(Npc Npc, string SkillId, int MaxLevel);

    static readonly FormKey PlayerRef = new(ModKey.FromFileName("Skyrim.esm"), 0x000014);

    /// <summary>The skill's name as the topic says it.</summary>
    public static string SkillName(string id) => id switch
    {
        "OneHanded" => "One-Handed", "TwoHanded" => "Two-Handed", "HeavyArmor" => "Heavy Armor", "LightArmor" => "Light Armor",
        "HandToHand" => "Hand-to-Hand", "MediumArmor" => "Medium Armor", "CriticalStrike" => "Critical Strike", _ => id,
    };

    /// <summary>The trainers' quest, topics, classes and responses, prefixed "MW" or "OB"; returns how many trainer
    /// skills were made.</summary>
    public static int Build(SkyrimMod mod, ISkyrimModGetter skyrim, string prefix, IEnumerable<Trainer> trainers, int costPerLevel)
    {
        var list = trainers.Where(t => t.MaxLevel > 0).ToList();
        if (list.Count == 0) return 0;
        var quest = new Quest(mod)
        {
            EditorID = prefix + "Training",
            Name = prefix + " Training",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 45,
            Type = Quest.TypeEnum.Misc,
            Aliases = [new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(PlayerRef) }],
            NextAliasID = 1,
        };
        mod.Quests.Add(quest);
        var branch = new DialogBranch(mod) { EditorID = prefix + "TrainingBranch", Quest = quest.ToLink<IQuestGetter>(), Category = DialogBranch.CategoryType.Player, Flags = DialogBranch.Flag.TopLevel };
        mod.DialogBranches.Add(branch);
        // The yes/no for the progression's skills: "Train <skill> to <level> for <gold> gold?".
        var confirm = new Message(mod)
        {
            EditorID = prefix + "TrainingConfirm",
            Name = "Training",
            Description = "Train for %.0f gold?",
            MenuButtons = [new MessageButton { Text = "Train" }, new MessageButton { Text = "Not now" }],
            DisplayTime = 0,
            Flags = Message.Flag.MessageBox,
        };
        mod.Messages.Add(confirm);
        var template = skyrim.Classes.FirstOrDefault(c => c.EditorID == "Citizen") ?? skyrim.Classes.First();
        var classes = new Dictionary<(string, int), Class>();
        Class ClassFor(string id, int max)
        {
            if (classes.TryGetValue((id, max), out var hit)) return hit;
            var c = mod.Classes.DuplicateInAsNewRecord(template);
            c.EditorID = $"{prefix}Trainer{id}{max}";
            c.Name = $"{SkillName(id)} Trainer";
            c.Teaches = Enum.Parse<Skill>(id);
            c.MaxTrainingLevel = (byte)Math.Clamp(max, 1, 100);
            return classes[(id, max)] = c;
        }
        var topics = new Dictionary<string, DialogTopic>();
        int made = 0;
        foreach (var t in list.OrderBy(t => t.SkillId, StringComparer.Ordinal))
        {
            if (!topics.TryGetValue(t.SkillId, out var topic))
            {
                topic = new DialogTopic(mod)
                {
                    EditorID = $"{prefix}Train{t.SkillId}", Name = $"I'd like training in {SkillName(t.SkillId)}.", Quest = quest.ToNullableLink<IQuestGetter>(),
                    Priority = 50, Category = DialogTopic.CategoryEnum.Topic, Subtype = DialogTopic.SubtypeEnum.Custom, Branch = branch.ToNullableLink<IDialogBranchGetter>(), Responses = [],
                };
                mod.DialogTopics.Add(topic);
                topics[t.SkillId] = topic;
                branch.StartingTopic ??= topic.ToNullableLink<IDialogTopicGetter>();
            }
            bool skyrimSkill = GameLevelling.SkyrimSkills.Contains(t.SkillId);
            var isTrainer = new GetIsIDConditionData();
            isTrainer.Object = new FormLinkOrIndex<IReferenceableObjectGetter>(isTrainer, t.Npc.FormKey);
            topic.Responses.Add(new DialogResponses(mod)
            {
                Responses = [new DialogResponse { Text = "Very well.", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
                Flags = new DialogResponseFlags(),
                Conditions = [new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = isTrainer }],
                VirtualMachineAdapter = new DialogResponsesAdapter
                {
                    Scripts =
                    [
                        new ScriptEntry
                        {
                            Name = "TMTrainerTopic",
                            Properties =
                            [
                                new ScriptObjectProperty { Name = "TrainerClass", Object = new FormLink<ISkyrimMajorRecordGetter>(skyrimSkill ? ClassFor(t.SkillId, t.MaxLevel).FormKey : FormKey.Null) },
                                new ScriptStringProperty { Name = "SkillId", Data = t.SkillId },
                                new ScriptStringProperty { Name = "SkillName", Data = SkillName(t.SkillId) },
                                new ScriptIntProperty { Name = "MaxLevel", Data = t.MaxLevel },
                                new ScriptIntProperty { Name = "CostPerLevel", Data = costPerLevel },
                                new ScriptObjectProperty { Name = "Confirm", Object = confirm.ToLink<ISkyrimMajorRecordGetter>() },
                            ],
                        },
                    ],
                    ScriptFragments = new ScriptFragments
                    {
                        ExtraBindDataVersion = 2,
                        FileName = "TMTrainerTopic",
                        OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMTrainerTopic", FragmentName = "Fragment_End" },
                    },
                },
            });
            made++;
        }
        return made;
    }

    /// <summary>A skill book teaching <paramref name="id"/> (Skyrim's skill or the progression's; null none): Skyrim's
    /// book teaching, or TMSkillBook.</summary>
    public static bool Teach(Book book, string? id)
    {
        if (id is null) return false;
        if (GameLevelling.SkyrimSkills.Contains(id))
        {
            book.Teaches = new BookSkill { Skill = Enum.Parse<Skill>(id) };
            return true;
        }
        if (!GameLevelling.DefaultGoverning.ContainsKey(id)) return false;
        book.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts = [new ScriptEntry { Name = "TMSkillBook", Properties = [new ScriptStringProperty { Name = "SkillId", Data = id }] }],
        };
        return true;
    }
}
