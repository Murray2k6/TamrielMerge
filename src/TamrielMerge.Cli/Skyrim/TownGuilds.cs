using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Strings;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's guilds (rules from Daggerfall Unity's Guild, MagesGuild, FightersGuild, ThievesGuild, DarkBrotherhood,
/// KnightlyOrder and Temple classes, MIT; rank titles from UESP; messages from TEXT.RSC) on the "DFGuilds" quest
/// (TMDfGuilds): a membership faction per guild whose ranks carry Daggerfall's titles, and on the town conversation
/// quest, said by the guild's people, "I'd like to join.", "I'd like some training." (members; the speaker trains one
/// of the guild's skills up to 50) and, at temples, "I need healing.". Promotion, demotion and expulsion run daily
/// with Daggerfall's 28-day rule; the Thieves Guild and Dark Brotherhood invite. Reputation is the player's standing
/// with the guild (DFReact_&lt;id&gt;, TownTalk).
/// </summary>
public sealed class TownGuilds(SkyrimMod mod, ISkyrimModGetter skyrim, DfText text, DfFactions factions)
{
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey PlayerRef = Sk(0x14);

    static readonly string[][] TitleKinds =
    [
        ["Apprentice", "Journeyman", "Evoker", "Conjurer", "Magician", "Enchanter", "Warlock", "Wizard", "Master Wizard", "Archmage"],
        ["Apprentice", "Journeyman", "Swordsman", "Protector", "Defender", "Warder", "Guardian", "Champion", "Warrior", "Master"],
        ["Apprentice", "Journeyman", "Filcher", "Crook", "Robber", "Bandit", "Thief", "Ringleader", "Mastermind", "Master Thief"],
        ["Apprentice", "Journeyman", "Operator", "Slayer", "Executioner", "Punisher", "Terminator", "Assassin", "Dark Brother", "Master Assassin"],
        ["Aspirant", "Squire", "Gallant", "Chevalier", "Keeper", "Knight Brother", "Commander", "Marshall", "Seneschal", "Paladin"],
        ["Novice", "Initiate", "Acolyte", "Adept", "Curate", "Disciple", "Brother", "Diviner", "Master", "Patriarch"],
    ];

    /// <summary>A Daggerfall skill as the Skyrim skill standing for it: actor value name (Papyrus) and trainer skill.</summary>
    static (string Av, Skill Skill) SkyrimSkill(string dfSkill) => dfSkill switch
    {
        "Alteration" => ("Alteration", Skill.Alteration),
        "Destruction" => ("Destruction", Skill.Destruction),
        "Illusion" => ("Illusion", Skill.Illusion),
        "Mysticism" or "Daedric" => ("Conjuration", Skill.Conjuration),
        "Restoration" or "Medical" => ("Restoration", Skill.Restoration),
        "Thaumaturgy" => ("Enchanting", Skill.Enchanting),
        "Archery" => ("Marksman", Skill.Archery),
        "Axe" or "BluntWeapon" => ("TwoHanded", Skill.TwoHanded),
        "LongBlade" or "ShortBlade" or "CriticalStrike" => ("OneHanded", Skill.OneHanded),
        "Backstabbing" or "Stealth" => ("Sneak", Skill.Sneak),
        "Lockpicking" => ("Lockpicking", Skill.Lockpicking),
        "Pickpocket" => ("Pickpocket", Skill.Pickpocket),
        "Climbing" or "Dodging" or "Running" or "Jumping" or "Swimming" => ("LightArmor", Skill.LightArmor),
        _ => ("Speechcraft", Skill.Speech),   // Etiquette, Streetwise, Mercantile and the languages
    };

    /// <summary>One guild: who speaks for it (Daggerfall factions), the faction whose reputation counts, its titles,
    /// skills, whether one may ask to join, its TEXT.RSC messages and promotion messages by rank.</summary>
    sealed record Guild(string Key, string Name, int[] Speakers, int RepFaction, int TitleKind, string[] Skills, bool Joinable,
        int Eligible, int LowSkill, int BadRep, int Welcome, int Promotion, Dictionary<int, int[]> PromotionByRank, bool Temple = false, string? God = null);

    static readonly string[] MagesSkills = ["Alteration", "Destruction", "Illusion", "Mysticism", "Restoration", "Thaumaturgy"];
    static readonly string[] FightersSkills = ["Archery", "Axe", "BluntWeapon", "Giantish", "LongBlade", "Orcish", "ShortBlade"];
    static readonly string[] ThievesSkills = ["Backstabbing", "Climbing", "Lockpicking", "Pickpocket", "ShortBlade", "Stealth", "Streetwise"];
    static readonly string[] BrotherhoodSkills = ["Archery", "Backstabbing", "Climbing", "CriticalStrike", "Daedric", "Destruction", "ShortBlade", "Stealth", "Streetwise"];
    static readonly string[] KnightSkills = ["Archery", "CriticalStrike", "Dragonish", "Etiquette", "Giantish", "LongBlade", "Medical"];

    /// <summary>Temple services by the rank they open (UESP Daggerfall:Temples) as their promotion messages (TEXT.RSC
    /// 6600 potions, 6601 library, 6602 potion maker, 6603 soul gems, 6604 summoning, 6605 healing, 6606 spells, 6607
    /// spellmaker, 6608 magic items, 6609 item maker); rank 9 is 5241.</summary>
    static Dictionary<int, int[]> TempleRanks(params (int Rank, int Text)[] opens)
    {
        var d = opens.GroupBy(o => o.Rank).ToDictionary(g => g.Key, g => g.Select(o => o.Text).ToArray());
        d[9] = [5241];
        return d;
    }

    static List<Guild> Guilds()
    {
        var list = new List<Guild>
        {
            new("Mages", "the Mages Guild", [40], 40, 0, MagesSkills, true, 606, 611, 612, 5293, 5236,
                new() { [2] = [5230], [3] = [5231], [5] = [5232], [6] = [5233], [8] = [5234] }),
            new("Fighters", "the Fighters Guild", [41], 41, 1, FightersSkills, true, 681, 680, 679, 684, 686, []),
            new("Thieves", "the Thieves Guild", [42], 42, 2, ThievesSkills, false, 0, 0, 0, 5225, 5235, []),
            new("Brotherhood", "the Dark Brotherhood", [108], 108, 3, BrotherhoodSkills, false, 0, 0, 0, 5292, 666, []),
        };
        (string Key, int Id, string Name)[] orders =
        [
            ("Horn", 411, "the Host of the Horn"), ("Dragon", 368, "the Knights of the Dragon"), ("Flame", 410, "the Knights of the Flame"),
            ("Hawk", 417, "the Knights of the Hawk"), ("Owl", 413, "the Knights of the Owl"), ("Rose", 409, "the Knights of the Rose"),
            ("Wheel", 415, "the Knights of the Wheel"), ("Candle", 408, "the Order of the Candle"), ("Raven", 414, "the Order of the Raven"),
            ("Scarab", 416, "the Order of the Scarab"),
        ];
        foreach (var (key, id, name) in orders)
            list.Add(new(key, name, [id], id, 4, KnightSkills, true, 752, 750, 751, 5291, 5237, new() { [4] = [5238], [6] = [5239], [9] = [5240] }));
        list.Add(new("Akatosh", "the Akatosh Chantry", [92], 92, 5, ["Alteration", "Daedric", "Destruction", "Dragonish", "LongBlade", "Running", "Stealth"], true, 740, 744, 745, 0, 0,
            TempleRanks((1, 6605), (2, 6601), (4, 6600), (5, 6602), (7, 6604)), true, "Akatosh"));
        list.Add(new("Arkay", "the Order of Arkay", [82], 82, 5, ["Axe", "Backstabbing", "Daedric", "Destruction", "Medical", "Restoration", "ShortBlade"], true, 740, 744, 745, 0, 0,
            TempleRanks((1, 6600), (3, 6601), (4, 6602), (4, 6603), (7, 6604)), true, "Arkay"));
        list.Add(new("Dibella", "the House of Dibella", [98], 98, 5, ["Daedric", "Etiquette", "Illusion", "Lockpicking", "LongBlade", "Nymph", "Orcish", "Restoration"], true, 740, 744, 745, 0, 0,
            TempleRanks((1, 6600), (2, 6605), (4, 6601), (5, 6602), (7, 6604)), true, "Dibella"));
        list.Add(new("Julianos", "the School of Julianos", [94], 94, 5, ["Alteration", "Daedric", "Impish", "Lockpicking", "Mysticism", "ShortBlade", "Thaumaturgy"], true, 740, 744, 745, 0, 0,
            TempleRanks((2, 6605), (3, 6608), (4, 6603), (5, 6609), (6, 6604)), true, "Julianos"));
        list.Add(new("Kynareth", "the Temple of Kynareth", [36], 36, 5, ["Archery", "Climbing", "Daedric", "Destruction", "Dodging", "Dragonish", "Harpy", "Illusion", "Jumping", "Running", "Stealth"], true, 740, 744, 745, 0, 0,
            TempleRanks((1, 6605), (3, 6606), (4, 6601), (6, 6607), (7, 6604)), true, "Kynareth"));
        list.Add(new("Mara", "the Benevolence of Mara", [88], 88, 5, ["Archery", "CriticalStrike", "Daedric", "Etiquette", "Harpy", "Illusion", "Medical", "Nymph", "Restoration", "Streetwise"], true, 740, 744, 745, 0, 0,
            TempleRanks((1, 6605), (2, 6600), (4, 6601), (5, 6602), (7, 6604)), true, "Mara"));
        list.Add(new("Stendarr", "the Temple of Stendarr", [106], 106, 5, ["Axe", "BluntWeapon", "CriticalStrike", "Daedric", "Dodging", "Medical", "Restoration"], true, 740, 744, 745, 0, 0,
            TempleRanks((2, 6600), (4, 6601), (5, 6602), (7, 6604)), true, "Stendarr"));
        list.Add(new("Zenithar", "the Resolution of Z'en", [84], 84, 5, ["BluntWeapon", "Centaurian", "Daedric", "Etiquette", "Giantish", "Harpy", "Mercantile", "Orcish", "Pickpocket", "Spriggan", "Streetwise", "Thaumaturgy"], true, 740, 744, 745, 0, 0,
            TempleRanks((1, 6600), (2, 6605), (4, 6601), (6, 6602), (8, 6604)), true, "Zenithar"));
        return list;
    }

    public int Members { get; private set; }
    public int Trainers { get; private set; }
    public int Responses { get; private set; }

    /// <summary>Daggerfall's macros fixed when converting (the rest, the player's name and title, at run time).</summary>
    string Say(int textId, Guild g)
    {
        if (textId <= 0 || text[textId] is not { Count: > 0 } lines) return "";
        return lines[0].Replace("%god", g.God ?? "the gods").Replace("%fon", g.Name).Replace("%ra", "traveller").Replace("%cn", "this town")
            .Replace("%reg", "the Iliac Bay").Replace("%dng", "the old crypt outside town").Replace("%dbp", "Sithis").Replace("%oth", "By the gods");
    }

    static ConditionFloat Cond(ConditionData data, CompareOperator op, float value, bool or = false) =>
        new() { Data = data, CompareOperator = op, ComparisonValue = value, Flags = or ? Condition.Flag.OR : 0 };

    static ConditionFloat InFaction(IFactionGetter f, bool or = false)
    {
        var d = new GetInFactionConditionData();
        d.Faction = new FormLinkOrIndex<IFactionGetter>(d, f.FormKey);
        return Cond(d, CompareOperator.EqualTo, 1, or);
    }

    public void Build(TownTalk talk, Quest talkQuest, IReadOnlyList<TownPeople.Person> people, Global breakIns)
    {
        var guilds = Guilds().Where(g => g.Speakers.Any(s => talk.FactionOf(s) is not null)).ToList();
        if (guilds.Count == 0) return;
        var quest = new Quest(mod)
        {
            EditorID = "DFGuilds",
            Name = "Daggerfall Guilds",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 40,
            Type = Quest.TypeEnum.Misc,
        };
        mod.Quests.Add(quest);

        // Membership factions with Daggerfall's titles.
        var members = new List<Faction>();
        foreach (var g in guilds)
        {
            var f = new Faction(mod) { EditorID = $"DFGuild{g.Key}", Name = char.ToUpperInvariant(g.Name[0]) + g.Name[1..], Ranks = [] };
            var titles = TitleKinds[g.TitleKind];
            for (int r = 0; r < 10; r++)
                f.Ranks.Add(new Rank { Number = (uint)r, Title = new GenderedItem<TranslatedString?>(titles[r], titles[r]) });
            mod.Factions.Add(f);
            members.Add(f);
        }

        // Skills by guild (Skyrim's standing in for Daggerfall's, one each).
        var skills = new List<string>();
        var first = new List<int>();
        var count = new List<int>();
        foreach (var g in guilds)
        {
            var avs = g.Skills.Select(s => SkyrimSkill(s).Av).Distinct().ToList();
            first.Add(skills.Count);
            count.Add(avs.Count);
            skills.AddRange(avs);
        }

        string Promotions(Guild g) => string.Join("|", Enumerable.Range(0, 10).Select(r =>
            g.PromotionByRank.TryGetValue(r, out var ids) ? string.Join("\n\n", ids.Select(id => Say(id, g)).Where(s => s.Length > 0)) : Say(g.Promotion, g)).Select(s => s.Replace("|", "/")));

        var cure = new Spell(mod)
        {
            EditorID = "DFTempleCure",
            Name = "Temple Healing",
            ObjectBounds = new ObjectBounds(),
            Type = SpellType.Spell,
            CastType = CastType.FireAndForget,
            TargetType = TargetType.Self,
            Description = "",
            Effects = [new Effect { BaseEffect = skyrim.MagicEffects.First(m => m.EditorID == "AlchCureDisease").ToNullableLink(), Data = new EffectData() }],
            Flags = SpellDataFlag.ManualCostCalc | SpellDataFlag.NoAbsorbOrReflect,
        };
        mod.Spells.Add(cure);

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
                    Name = "TMDfGuilds",
                    Properties =
                    [
                        new ScriptObjectProperty { Name = "PlayerRef", Object = PlayerRef.ToLink<ISkyrimMajorRecordGetter>() },
                        Objs("Members", members.Select(m => m.FormKey)),
                        Objs("Reputation", guilds.Select(g => talk.ReactionOf(g.RepFaction)?.FormKey ?? talk.ReactionOf(g.Speakers[0])!.FormKey)),
                        new ScriptIntListProperty { Name = "SkillFirst", Data = [.. first] },
                        new ScriptIntListProperty { Name = "SkillCount", Data = [.. count] },
                        new ScriptStringListProperty { Name = "Skills", Data = [.. skills] },
                        new ScriptBoolListProperty { Name = "Joinable", Data = [.. guilds.Select(g => g.Joinable)] },
                        new ScriptIntListProperty { Name = "TitleKind", Data = [.. guilds.Select(g => g.TitleKind)] },
                        new ScriptStringListProperty { Name = "Titles", Data = [.. TitleKinds.SelectMany(t => t)] },
                        new ScriptStringListProperty { Name = "GuildNames", Data = [.. guilds.Select(g => g.Name)] },
                        new ScriptStringListProperty { Name = "EligibleText", Data = [.. guilds.Select(g => Say(g.Eligible, g))] },
                        new ScriptStringListProperty { Name = "LowSkillText", Data = [.. guilds.Select(g => Say(g.LowSkill, g))] },
                        new ScriptStringListProperty { Name = "BadRepText", Data = [.. guilds.Select(g => Say(g.BadRep, g))] },
                        new ScriptStringListProperty { Name = "WelcomeText", Data = [.. guilds.Select(g => g.Welcome > 0 ? Say(g.Welcome, g) : $"Welcome to {g.Name}, %pcn. You are now a %lev.")] },
                        new ScriptStringListProperty { Name = "PromotionText", Data = [.. guilds.Select(Promotions)] },
                        new ScriptStringProperty { Name = "DemotionText", Data = Say(667, guilds[0]) },
                        new ScriptStringProperty { Name = "ExpulsionText", Data = Say(668, guilds[0]) },
                        new ScriptIntProperty { Name = "ThievesIndex", Data = guilds.FindIndex(g => g.Key == "Thieves") },
                        new ScriptIntProperty { Name = "BrotherhoodIndex", Data = guilds.FindIndex(g => g.Key == "Brotherhood") },
                        new ScriptIntProperty { Name = "ArkayIndex", Data = guilds.FindIndex(g => g.Key == "Arkay") },
                        new ScriptObjectProperty { Name = "BreakIns", Object = breakIns.ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "TempleCure", Object = cure.ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "GameMonth", Object = Sk(0x36).ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "GameDay", Object = Sk(0x37).ToLink<ISkyrimMajorRecordGetter>() },
                    ],
                },
            ],
        };

        // Dialogue, said by the guild's people.
        var branch = new DialogBranch(mod) { EditorID = "DFGuildBranch", Quest = talkQuest.ToLink<IQuestGetter>(), Category = DialogBranch.CategoryType.Player, Flags = DialogBranch.Flag.TopLevel };
        mod.DialogBranches.Add(branch);
        DialogTopic Topic(string edid, string name)
        {
            var t = new DialogTopic(mod)
            {
                EditorID = edid, Name = name, Quest = talkQuest.ToNullableLink<IQuestGetter>(), Priority = 50,
                Category = DialogTopic.CategoryEnum.Topic, Subtype = DialogTopic.SubtypeEnum.Custom, Branch = branch.ToNullableLink<IDialogBranchGetter>(), Responses = [],
            };
            mod.DialogTopics.Add(t);
            return t;
        }
        var join = Topic("DFGuildJoin", "I'd like to join.");
        branch.StartingTopic = join.ToNullableLink<IDialogTopicGetter>();
        var train = Topic("DFGuildTrain", "I'd like some training.");
        var heal = Topic("DFGuildHeal", "I need healing.");
        DialogResponses Response(string line, int guild, int service, IEnumerable<ConditionFloat> conditions)
        {
            Responses++;
            return new DialogResponses(mod)
            {
                Responses = [new DialogResponse { Text = line, ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
                Flags = new DialogResponseFlags(),
                Conditions = [.. conditions],
                VirtualMachineAdapter = new DialogResponsesAdapter
                {
                    Scripts =
                    [
                        new ScriptEntry
                        {
                            Name = "TMDfGuildTopic",
                            Properties =
                            [
                                new ScriptObjectProperty { Name = "Guilds", Object = quest.ToLink<ISkyrimMajorRecordGetter>() },
                                new ScriptIntProperty { Name = "Guild", Data = guild },
                                new ScriptIntProperty { Name = "Service", Data = service },
                            ],
                        },
                    ],
                    ScriptFragments = new ScriptFragments
                    {
                        ExtraBindDataVersion = 2,
                        FileName = "TMDfGuildTopic",
                        OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMDfGuildTopic", FragmentName = "Fragment_End" },
                    },
                },
            };
        }
        for (int i = 0; i < guilds.Count; i++)
        {
            var g = guilds[i];
            var speakers = g.Speakers.Select(talk.FactionOf).OfType<Faction>().ToList();
            List<ConditionFloat> By() => [.. speakers.Select((f, n) => InFaction(f, or: n < speakers.Count - 1))];
            if (g.Joinable) join.Responses.Add(Response("Let me see what you have done.", i, 0, By()));
            train.Responses.Add(Response("Very well.", i, 1, By()));
            if (g.Temple) heal.Responses.Add(Response($"{g.God} grant you health.", i, 2, By()));
        }

        // Trainers: each of a guild's people teaches one of its skills, up to 50 (Daggerfall's training cap).
        var classes = new Dictionary<Skill, Class>();
        var template = skyrim.Classes.FirstOrDefault(c => c.EditorID == "Citizen") ?? skyrim.Classes.First();
        Class TrainerClass(Skill skill)
        {
            if (classes.TryGetValue(skill, out var hit)) return hit;
            var c = mod.Classes.DuplicateInAsNewRecord(template);
            c.EditorID = $"DFTrainer{skill}";
            c.Name = $"{skill} Trainer";
            c.Teaches = skill;
            c.MaxTrainingLevel = 50;
            return classes[skill] = c;
        }
        var bySpeaker = new Dictionary<int, Guild>();
        foreach (var g in guilds) foreach (var s in g.Speakers) bySpeaker[s] = g;
        int n2 = 0;
        foreach (var p in people)
        {
            if (p.Effective is not { } eff || !bySpeaker.TryGetValue(eff.Id, out var g)) continue;
            var guildSkills = g.Skills.Select(s => SkyrimSkill(s).Skill).Distinct().ToList();
            p.Npc.Class.SetTo(TrainerClass(guildSkills[n2++ % guildSkills.Count]));
            Trainers++;
        }
        Members = guilds.Count;
    }
}
