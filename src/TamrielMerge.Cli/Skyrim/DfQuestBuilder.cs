using System.Text;
using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's quests (the 232 QBN/QRC pairs in ARENA2) in Daggerfall.esm, data-driven: one quest record per Daggerfall
/// quest running the shared TMDfQuest script with its QBN program as properties (<see cref="DfQuestOps"/>), its messages
/// as Message records owned by it (so the names of its people and places fill in through name-tag aliases), its log as
/// objectives and its "Tell me about" and rumour topics gated by stages; the machine (DFQuestMachine, TMDfQuestMachine)
/// that offers them through "Do you have any work for me?", finds their places among the registered quest sites
/// (<see cref="QuestSites"/>) and starts the main quest; and the factions' reputation (DFQuestReputation, TMDfReputation)
/// on the DFReact&lt;id&gt; globals that conversations and the guilds read. Behaviour per operation follows Daggerfall
/// Unity's quest engine (MIT).
/// </summary>
public sealed class DfQuestBuilder(SkyrimMod mod, string arena2, IReadOnlyList<ISkyrimModGetter> masters, DfFactions factions, DfNames names, VanillaActors vanilla)
{
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey PlayerRef = Sk(0x14), XMarker = Sk(0x3B);

    /// <summary>Daggerfall's monsters and classes (CreatureBuilder.Spawn), for quest foes.</summary>
    public Func<int, FormKey?>? Foe { get; init; }
    /// <summary>Daggerfall's spells by SPELLS.STD index.</summary>
    public IReadOnlyDictionary<int, Spell>? Spells { get; init; }
    /// <summary>The guilds quest (DFGuilds, TMDfGuilds) and its guilds in order (TownGuilds.Built).</summary>
    public IQuestGetter? GuildsQuest { get; init; }
    public IReadOnlyList<(string Key, int RepFaction)> Guilds { get; init; } = [];
    public FormKey? IliacBay { get; init; }
    public IReadOnlyList<FormKey> Weapons { get; init; } = [];
    public IReadOnlyList<FormKey> Armor { get; init; } = [];
    public IReadOnlyList<FormKey> Books { get; init; } = [];

    public Quest? Machine { get; private set; }
    /// <summary>Places the quests name that are neither towns nor dungeons (the witches' covens): Daggerfall.esm registers
    /// them as sites with only a map marker.</summary>
    public List<DfLocation> ExteriorOnly { get; } = [];
    public int Messages { get; private set; }
    public int Objectives { get; private set; }
    public int Topics { get; private set; }
    public List<string> Problems { get; } = [];

    // ---- Shared records (get or make by EditorID, so the town and dungeon plugins share Daggerfall.esm's) ----

    /// <summary>A Daggerfall faction's Skyrim faction and its reaction global DFReact&lt;id&gt; (TownTalk's).</summary>
    public static (Faction Faction, Global React) FactionRecords(SkyrimMod mod, DfFaction f)
    {
        var faction = mod.Factions.FirstOrDefault(x => x.EditorID == $"DFFaction{f.Id}");
        if (faction is null)
        {
            faction = new Faction(mod) { EditorID = $"DFFaction{f.Id}", Name = f.Name, Ranks = [new Rank { Number = 0 }] };
            mod.Factions.Add(faction);
        }
        var react = mod.Globals.FirstOrDefault(g => g.EditorID == $"DFReact{f.Id}");
        if (react is null)
        {
            react = new GlobalFloat(mod) { EditorID = $"DFReact{f.Id}", Data = f.Rep };
            mod.Globals.Add(react);
        }
        return (faction, react);
    }

    /// <summary>The social group faction TownTalk puts townsfolk in.</summary>
    public static Faction GroupFaction(SkyrimMod mod, int g)
    {
        string edid = $"DFSocialGroup{(DfSocialGroup)g}";
        if (mod.Factions.FirstOrDefault(x => x.EditorID == edid) is { } f) return f;
        f = new Faction(mod) { EditorID = edid, Name = ((DfSocialGroup)g).ToString(), Ranks = [new Rank { Number = 0 }] };
        mod.Factions.Add(f);
        return f;
    }

    static ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = key.ToLink<ISkyrimMajorRecordGetter>() };
    static ScriptObjectListProperty Objs(string name, IEnumerable<FormKey> keys)
    {
        var p = new ScriptObjectListProperty { Name = name };
        foreach (var k in keys) p.Objects.Add(new ScriptObjectProperty { Object = k.ToLink<ISkyrimMajorRecordGetter>() });
        return p;
    }
    static ScriptIntListProperty Ints(string name, IEnumerable<int> v) => new() { Name = name, Data = [.. v] };
    static ScriptStringListProperty Strs(string name, IEnumerable<string> v) => new() { Name = name, Data = [.. v] };
    static ScriptIntProperty Int(string name, int v) => new() { Name = name, Data = v };

    T? Vanilla<T>(string edid) where T : class, IMajorRecordGetter =>
        masters.SelectMany(m => m.EnumerateMajorRecords<T>()).FirstOrDefault(r => r.EditorID == edid);

    /// <summary>Builds the reputation, the machine and every quest in ARENA2.</summary>
    public void Build()
    {
        var quests = DfQuest.LoadAll(arena2);
        var groupFactions = Enumerable.Range(0, 5).Select(g => GroupFaction(mod, g)).ToList();
        var reputation = BuildReputation();
        Machine = new Quest(mod) { EditorID = "DFQuestMachine", Name = "Daggerfall Quests", Flags = Quest.Flag.StartGameEnabled, Priority = 40, Type = Quest.TypeEnum.Misc };
        mod.Quests.Add(Machine);

        // Where quest items, name tags and spare people wait: a closed cell no door leads to.
        var holdingCell = new Cell(mod) { EditorID = "DFQuestHolding", Name = "Daggerfall Quests", Flags = Cell.Flag.IsInteriorCell, Persistent = [], Temporary = [] };
        var holding = new PlacedObject(mod)
        {
            EditorID = "DFQuestHoldingMarker",
            Base = XMarker.ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        holdingCell.Persistent.Add(holding);
        AddInterior(holdingCell);
        var xmarkerModel = masters[0].Statics.FirstOrDefault(s => s.FormKey == XMarker)?.Model?.File.DataRelativePath.Path ?? @"Marker_X.nif";
        var nameTag = new Mutagen.Bethesda.Skyrim.Activator(mod) { EditorID = "DFQuestNameTag", Name = " ", ObjectBounds = new ObjectBounds(), Model = new Model { File = xmarkerModel } };
        mod.Activators.Add(nameTag);
        var letter = new Book(mod)
        {
            EditorID = "DFQuestLetter", Name = "Letter", ObjectBounds = new ObjectBounds(), Model = new Model { File = @"Clutter\Common\Letter01.nif" },
            BookText = "<p align=\"center\">(Its words appear as you read it.)</p>", Description = "", Value = 0, Weight = 0.1f, Type = Book.BookType.NoteOrScroll,
            Teaches = new BookTeachesNothing(), InventoryArt = new FormLinkNullable<IStaticGetter>(),
        };
        mod.Books.Add(letter);

        // Items: Skyrim's or stand-ins, and random lists per Daggerfall item group.
        var items = new DfQuestItems(mod, masters);
        var groupLists = items.GroupLists(Weapons, Armor, Books, items.MagicItems());

        // People quests make: Bretons and Redguards in a few dresses, named at run time from Daggerfall's name banks.
        var (male, female) = PersonBases();
        var maleNames = new List<string>();
        var femaleNames = new List<string>();
        for (int i = 0; i < 64; i++)
        {
            var bank = i % 2 == 0 ? DfNameBank.Breton : DfNameBank.Redguard;
            maleNames.Add(names.FullName(bank, false, new DfRandom((uint)(1000 + i * 7919))));
            femaleNames.Add(names.FullName(bank, true, new DfRandom((uint)(5000 + i * 7919))));
        }

        // Sounds, cures and diseases.
        var cure = new Spell(mod)
        {
            EditorID = "DFQuestCureDisease", Name = "Cure Disease", ObjectBounds = new ObjectBounds(), Type = SpellType.Spell, CastType = CastType.FireAndForget,
            TargetType = TargetType.Self, Description = "", Flags = SpellDataFlag.ManualCostCalc | SpellDataFlag.NoAbsorbOrReflect,
            Effects = [new Effect { BaseEffect = (Vanilla<IMagicEffectGetter>("AlchCureDisease")?.FormKey ?? FormKey.Null).ToNullableLink<IMagicEffectGetter>(), Data = new EffectData() }],
        };
        mod.Spells.Add(cure);
        var diseases = new FormList(mod) { EditorID = "DFQuestDiseases" };
        foreach (var d in new[] { "DiseaseAtaxia", "DiseaseBoneBreakFever", "DiseaseBrainRot", "DiseaseRattles", "DiseaseRockjoint", "DiseaseWitbane", "DiseaseCollywobbles" })
            if (Vanilla<ISpellGetter>(d) is { } s) diseases.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(s.FormKey));
        mod.FormLists.Add(diseases);
        var sound = masters[0].SoundMarkers.Where(s => s.EditorID?.StartsWith("QST") == true).OrderBy(s => s.EditorID, StringComparer.Ordinal).FirstOrDefault();

        // The quests.
        var built = new List<(DfQuest Q, Quest Rec)>();
        foreach (var q in quests)
        {
            try { built.Add((q, BuildQuest(q, items, letter))); }
            catch (Exception e) { Problems.Add($"{q.Name}: {e.Message}"); }
        }

        // The machine.
        var factionForms = new FormList(mod) { EditorID = "DFQuestFactionForms" };
        var factionIds = new List<int>();
        foreach (var f in factions.ById.Values.OrderBy(f => f.Id))
        {
            factionForms.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(FactionRecords(mod, f).Faction.FormKey));
            factionIds.Add(f.Id);
        }
        mod.FormLists.Add(factionForms);
        FormList Runtime(string edid) { var l = new FormList(mod) { EditorID = edid }; mod.FormLists.Add(l); return l; }
        var props = new List<ScriptProperty>
        {
            Obj("Reputation", reputation.FormKey), Obj("PlayerRef", PlayerRef), Obj("Holding", holding.FormKey), Obj("NameTag", nameTag.FormKey),
            Obj("Sources", Runtime("DFQuestSources").FormKey), Obj("RunningQuests", Runtime("DFQuestRunning").FormKey), Obj("DoneOnce", Runtime("DFQuestDoneOnce").FormKey),
            Obj("FactionForms", factionForms.FormKey), Ints("FactionFormIds", factionIds),
            Objs("QuestGlobals", Enumerable.Range(0, DfQuestGlobals.Count).Select(n => DfQuestGlobals.For(mod, n, null, fixedKey: true))),
            Strs("QuestNames", built.Select(b => b.Q.Name)), Objs("Quests", built.Select(b => b.Rec.FormKey)),
            Ints("QuestLetter", built.Select(b => (int)b.Q.Name[0])), Ints("QuestMembership", built.Select(b => (int)b.Q.Name[2])),
            Ints("QuestMinReq", built.Select(b => MinReq(b.Q.Name))),
            new ScriptBoolListProperty { Name = "QuestOneTime", Data = [.. built.Select(b => b.Q.Name.Length > 4 && b.Q.Name[4] == '1')] },
            Ints("GuildFaction", Guilds.Select(g => g.RepFaction)), Strs("GuildLetters", Guilds.Select(g => GuildLetters(g.Key))),
            Objs("MaleBases", male), Objs("FemaleBases", female), Strs("MaleNames", maleNames), Strs("FemaleNames", femaleNames),
            Objs("GroupItems", groupLists),
            Obj("CureDisease", cure.FormKey), Obj("Diseases", diseases.FormKey),
            Strs("RegionNames", DfLocations.RegionNames), Strs("BuildingLabels", BuildingLabels),
            Strs("Gods", ["Akatosh", "Arkay", "Dibella", "Julianos", "Kynareth", "Mara", "Stendarr", "Zenithar"]),
            Strs("Oaths", ["By the gods", "By Akatosh", "Gods' blood", "By the Nine", "By Stendarr"]),
            Obj("MerchantsGroup", groupFactions[1].FormKey), Obj("NobilityGroup", groupFactions[3].FormKey),
            Int("CommonersFaction", factions.ById.Values.FirstOrDefault(f => f.Type == DfFactionType.People)?.Id ?? 0),
        };
        if (GuildsQuest is not null) props.Add(Obj("Guilds", GuildsQuest.FormKey));
        if (IliacBay is { } bay) props.Add(Obj("IliacBay", bay));
        if (Spells is { Count: > 0 })
        {
            var ordered = Spells.OrderBy(s => s.Key).ToList();
            props.Add(Objs("DfSpells", ordered.Select(s => s.Value.FormKey)));
            props.Add(Ints("DfSpellIds", ordered.Select(s => s.Key)));
        }
        if (Vanilla<IQuestGetter>("PlayerVampireQuest") is { } vq) props.Add(Obj("VampireQuest", vq.FormKey));
        if (Vanilla<ISpellGetter>("WerewolfChange") is { } wp) props.Add(Obj("WerewolfPower", wp.FormKey));
        if (sound is not null) props.Add(Obj("QuestSound", sound.FormKey));
        // The sounds quests play ("play sound N": DAGGER.SND's record N), as Daggerfall's own sounds.
        var soundIds = quests.SelectMany(q => q.Ops).Where(o => o.Code is 0x3C or 0x54 && o.ArgCount > 1).Select(o => o.Args[1].Int).Distinct().Order().ToList();
        var madeSounds = QuestSounds(soundIds);
        if (madeSounds.Count > 0)
        {
            props.Add(Ints("DfSoundIds", madeSounds.Select(m => m.Id)));
            props.Add(Objs("DfSounds", madeSounds.Select(m => m.Marker)));
        }
        Machine.Aliases.Add(new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(PlayerRef) });
        Machine.NextAliasID = 1;
        Machine.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts = [new ScriptEntry { Name = "TMDfQuestMachine", Properties = [.. props] }],
            Aliases =
            [
                new QuestFragmentAlias
                {
                    Property = new ScriptObjectProperty { Object = Machine.ToLink<ISkyrimMajorRecordGetter>(), Alias = 0 },
                    Scripts = [new ScriptEntry { Name = "TMDfQuestPlayer" }],
                },
            ],
        };
        foreach (var (_, rec) in built)
            rec.VirtualMachineAdapter!.Scripts[0].Properties.Add(Obj("Machine", Machine.FormKey));

        BuildWorkTopic(groupFactions);
        Console.WriteLine($"Quests: {built.Count} Daggerfall quests, {Messages} messages, {Objectives} log entries, {Topics} topics"
            + (items.Missing.Count > 0 ? $"; items without a stand-in: {string.Join(", ", items.Missing)}" : "")
            + (Problems.Count > 0 ? $"; problems: {string.Join("; ", Problems.Take(5))}" : ""));
    }

    /// <summary>Daggerfall's sounds the quests play, from DAGGER.SND by record id: each a WAV under
    /// Sound\fx\TamrielMerge\Daggerfall in <see cref="SoundDir"/> with a sound descriptor (a copy of a vanilla quest
    /// sound's settings) and a sound marker for Papyrus. None without SoundDir or DAGGER.SND.</summary>
    List<(int Id, FormKey Marker)> QuestSounds(IEnumerable<int> ids)
    {
        var made = new List<(int, FormKey)>();
        if (SoundDir is null || DfSound.Load(arena2) is not { } snd) return made;
        var template = Vanilla<ISoundDescriptorGetter>("QSTTG01UrnSmashSD");
        if (template is null) return made;
        foreach (int id in ids)
        {
            string rel = Path.Combine("fx", "TamrielMerge", "Daggerfall", $"dfsnd{id:D3}.wav");
            if (id < 0 || !snd.WriteWav((uint)id, Path.Combine(SoundDir, "Sound", rel))) { Problems.Add($"sound {id}: not in DAGGER.SND"); continue; }
            var descriptor = mod.SoundDescriptors.DuplicateInAsNewRecord(template);
            descriptor.EditorID = $"DFQuestSound{id:D3}SD";
            descriptor.SoundFiles.Clear();
            descriptor.SoundFiles.Add(new Mutagen.Bethesda.Plugins.Assets.AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType>(rel));
            var marker = new SoundMarker(mod) { EditorID = $"DFQuestSound{id:D3}", ObjectBounds = new ObjectBounds(), SoundDescriptor = descriptor.ToNullableLink<ISoundDescriptorGetter>() };
            mod.SoundMarkers.Add(marker);
            made.Add((id, marker.FormKey));
        }
        return made;
    }

    /// <summary>The folder the plugin's loose files go in (its Sound folder is made here), for the quests' sounds.</summary>
    public string? SoundDir { get; init; }

    static int MinReq(string name)
    {
        if (name.Length < 4) return 0;
        char c = name[3];
        return char.IsDigit(c) ? c - '0' : char.IsLetter(c) ? 10 + (char.ToUpperInvariant(c) - 'A') * 10 : 0;
    }

    /// <summary>The quest-name letters a guild's people give (UESP Appendix B; Daggerfall Unity QuestList-Classic).</summary>
    static string GuildLetters(string key) => key switch
    {
        "Mages" => "N", "Fighters" => "M", "Thieves" => "O", "Brotherhood" => "L",
        "Akatosh" => "CD", "Arkay" => "CE", "Dibella" => "CF", "Julianos" => "C0", "Kynareth" => "CG", "Mara" => "CH", "Stendarr" => "CI", "Zenithar" => "CJ",
        _ => "B",
    };

    /// <summary>How Daggerfall's quest text names a building of each type (_place_).</summary>
    static readonly string[] BuildingLabels =
    [
        "the alchemist's", "a house for sale", "the armorer's", "the bank", "the town hall", "the bookseller's", "the clothing store", "the furniture store",
        "the gem store", "the general store", "the library", "the guild hall", "the pawn shop", "the weaponsmith's", "the temple", "the tavern", "the palace",
        "a house", "a house", "a house", "a house", "a house", "a house", "a house", "the ship",
    ];

    void AddInterior(Cell cell)
    {
        var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == 0);
        if (block is null) { block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = [] }; mod.Cells.Records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == 0);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [] }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }

    Quest BuildReputation()
    {
        var all = factions.ById.Values.OrderBy(f => f.Id).ToList();
        var q = new Quest(mod) { EditorID = "DFQuestReputation", Name = "Daggerfall Reputation" };
        mod.Quests.Add(q);
        int Pad(IReadOnlyList<int> l, int i) => i < l.Count ? l[i] : 0;
        q.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts =
            [
                new ScriptEntry
                {
                    Name = "TMDfReputation",
                    Properties =
                    [
                        Ints("Ids", all.Select(f => f.Id)), Ints("Parents", all.Select(f => f.Parent)), Ints("Kind", all.Select(f => (int)f.Type)),
                        Ints("GGroup", all.Select(f => f.GGroup)), Ints("Region", all.Select(f => f.Region)),
                        Ints("Allies", all.SelectMany(f => Enumerable.Range(0, 3).Select(i => Pad(f.Allies, i)))),
                        Ints("Enemies", all.SelectMany(f => Enumerable.Range(0, 3).Select(i => Pad(f.Enemies, i)))),
                        Strs("Names", all.Select(f => f.Name)), Objs("React", all.Select(f => FactionRecords(mod, f).React.FormKey)),
                    ],
                },
            ],
        };
        return q;
    }

    /// <summary>Generic people for quests: a few faces of Bretons and Redguards of each sex, dressed by kind.</summary>
    (List<FormKey> Male, List<FormKey> Female) PersonBases()
    {
        var male = new List<FormKey>();
        var female = new List<FormKey>();
        FormKey[] outfits = [Sk(0x0DAB76), Sk(0x095921), Sk(0x0F8716), Sk(0x0E9AC4)];   // farm clothes, merchant, fine clothes, robes
        int n = 0;
        foreach (var race in new[] { "BretonRace", "RedguardRace" })
        {
            var raceRec = vanilla.Get<IRaceGetter>(race);
            if (raceRec is null) continue;
            foreach (bool isFemale in new[] { false, true })
            {
                var pool = vanilla.Faces(raceRec.FormKey, isFemale);
                for (int i = 0; i < 4 && i < pool.Count; i++)
                {
                    var face = pool[(i * 7) % pool.Count];
                    var npc = mod.Npcs.DuplicateInAsNewRecord(face);
                    npc.EditorID = $"DFQuestPerson{n++:00}";
                    npc.Name = isFemale ? "Woman" : "Man";
                    npc.ShortName = null;
                    npc.VirtualMachineAdapter = null;
                    npc.Factions.Clear();
                    npc.CrimeFaction.Clear();
                    npc.Items = null;
                    npc.SleepingOutfit.Clear();
                    npc.DeathItem.Clear();
                    npc.Packages.Clear();
                    npc.Packages.Add(new FormLink<IPackageGetter>(Sk(0x09361E)));   // DefaultSandboxEditorLocation256
                    npc.Keywords = null;
                    npc.Perks = null;
                    npc.Template = new FormLinkNullable<INpcSpawnGetter>(face.FormKey);
                    npc.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
                    npc.Configuration.Flags = isFemale ? NpcConfiguration.Flag.Female : 0;
                    npc.Configuration.Level = new NpcLevel { Level = 6 };
                    npc.DefaultOutfit.SetTo(outfits[i % outfits.Length]);
                    if (npc.AIData is { } ai) { ai.Aggression = Aggression.Unaggressive; ai.Confidence = Confidence.Cowardly; }
                    (isFemale ? female : male).Add(npc.FormKey);
                }
            }
        }
        return (male, female);
    }

    /// <summary>"Do you have any work for me?" to any Daggerfall townsperson (TMDfQuestTopic: the machine's offer).</summary>
    void BuildWorkTopic(List<Faction> groups)
    {
        var branch = new DialogBranch(mod) { EditorID = "DFQuestWorkBranch", Quest = Machine!.ToLink<IQuestGetter>(), Category = DialogBranch.CategoryType.Player, Flags = DialogBranch.Flag.TopLevel };
        mod.DialogBranches.Add(branch);
        var topic = NewTopic("DFQuestWork", "Do you have any work for me?", Machine!, branch);
        branch.StartingTopic = topic.ToNullableLink<IDialogTopicGetter>();
        var line = Line("Let me think.", AnyTownsperson(groups));
        line.VirtualMachineAdapter = new DialogResponsesAdapter
        {
            Scripts = [new ScriptEntry { Name = "TMDfQuestTopic", Properties = [Obj("Machine", Machine!.FormKey), Int("Service", 0)] }],
            ScriptFragments = new ScriptFragments
            {
                ExtraBindDataVersion = 2, FileName = "TMDfQuestTopic",
                OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMDfQuestTopic", FragmentName = "Fragment_End" },
            },
        };
        topic.Responses.Add(line);
    }

    DialogTopic NewTopic(string edid, string name, Quest owner, DialogBranch branch)
    {
        var t = new DialogTopic(mod)
        {
            EditorID = edid, Name = name, Quest = owner.ToNullableLink<IQuestGetter>(), Priority = 50,
            Category = DialogTopic.CategoryEnum.Topic, Subtype = DialogTopic.SubtypeEnum.Custom, Branch = branch.ToNullableLink<IDialogBranchGetter>(), Responses = [],
        };
        mod.DialogTopics.Add(t);
        Topics++;
        return t;
    }

    DialogResponses Line(string text, IEnumerable<ConditionFloat> conditions, bool random = false) => new(mod)
    {
        Responses = [new DialogResponse { Text = text, ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
        Flags = new DialogResponseFlags { Flags = random ? DialogResponses.Flag.Random : 0 },
        Conditions = [.. conditions],
    };

    static ConditionFloat Cond(ConditionData data, CompareOperator op, float value, bool or = false) =>
        new() { Data = data, CompareOperator = op, ComparisonValue = value, Flags = or ? Condition.Flag.OR : 0 };

    static List<ConditionFloat> AnyTownsperson(List<Faction> groups) =>
        [.. groups.Select((g, i) => { var d = new GetInFactionConditionData(); d.Faction = new FormLinkOrIndex<IFactionGetter>(d, g.FormKey); return Cond(d, CompareOperator.EqualTo, 1, or: i < groups.Count - 1); })];

    static ConditionFloat StageDone(Quest q, int stage)
    {
        var d = new GetStageDoneConditionData { Stage = stage };
        d.Quest = new FormLinkOrIndex<IQuestGetter>(d, q.FormKey);
        return Cond(d, CompareOperator.EqualTo, 1);
    }

    // ---- One quest ----

    static string Edid(string name) => "DFQ" + LocationPlacer.Sanitize(name);

    static string Title(string name) => DfQuest.GroupOf(name) switch
    {
        "Main" => "Daggerfall: The Emperor's Errand",
        "Start" => name.StartsWith("_TUTOR") ? "Daggerfall: Privateer's Hold" : "Daggerfall: A Letter from Lady Brisienna",
        "Cure" => name.Contains("VAM") ? "Daggerfall: A Cure for Vampirism" : "Daggerfall: A Cure for Lycanthropy",
        "FightersGuild" => "Fighters Guild Contract", "MagesGuild" => "Mages Guild Task", "ThievesGuild" => "Thieves Guild Job",
        "DarkBrotherhood" => "Dark Brotherhood Contract", "KnightlyOrder" => "A Knightly Quest", "Merchants" => "A Merchant's Errand",
        "Commoners" => "A Favour for a Townsperson", "Nobility" => "A Noble's Commission", "Vampires" => "A Vampire's Errand",
        "Witches" => "A Coven's Task", "Daedra" => "A Daedra Lord's Quest",
        var g => $"A Task for {g switch { "Julianos" or "Akatosh" or "Arkay" or "Dibella" or "Kynareth" or "Mara" or "Stendarr" or "Zenithar" => g, _ => "the Temple" }}",
    };

    sealed class Tokens(DfQuest q)
    {
        public readonly List<(int Kind, int Index, int Mode)> List = [];
        readonly Dictionary<(int, int, int), int> map = [];
        public int Of(int kind, int index, int mode)
        {
            if (map.TryGetValue((kind, index, mode), out var k)) return k;
            map[(kind, index, mode)] = List.Count;
            List.Add((kind, index, mode));
            return List.Count - 1;
        }

        static readonly Regex Symbol = new(@"(_{1,4}|={1,3})([A-Za-z0-9][A-Za-z0-9.]*)_");
        static readonly Regex Macro = new(@"%[a-z0-9]+");
        static readonly Dictionary<string, int> Statics = new()
        {
            ["%ra"] = 1, ["%reg"] = 2, ["%crn"] = 2, ["%oth"] = 3, ["%god"] = 4, ["%vam"] = 5, ["%pct"] = 7, ["%di"] = 8, ["%cn"] = 9,
            ["%t"] = 10, ["%rt"] = 10, ["%rn"] = 11, ["%kno"] = 12, ["%qdt"] = 13, ["%dat"] = 13, ["%qdat"] = 13, ["%n"] = 14, ["%nam"] = 14, ["%nrn"] = 14,
            ["%fn"] = 14, ["%mn"] = 14,
        };
        static readonly Dictionary<string, int> Pronouns = new() { ["%g"] = 1, ["%g1"] = 1, ["%g2"] = 2, ["%g3"] = 3, ["%g2self"] = 4 };

        /// <summary>Daggerfall's text with its symbols and macros as alias text replacement (TagAlias + token).</summary>
        public string Convert(string text, int tagAlias)
        {
            var sb = new StringBuilder();
            int last = -1, at = 0;
            var spans = Symbol.Matches(text).Cast<Match>().Select(m => (m.Index, m.Length, Sym: true, M: m))
                .Concat(Macro.Matches(text).Cast<Match>().Select(m => (m.Index, m.Length, Sym: false, M: m))).OrderBy(s => s.Index).ToList();
            foreach (var (index, length, sym, m) in spans)
            {
                if (index < at) continue;
                sb.Append(text, at, index - at);
                at = index + length;
                if (sym)
                {
                    string pre = m.Groups[1].Value, name = m.Groups[2].Value;
                    int mode = pre[0] == '_' ? pre.Length : pre.Length == 1 ? 5 : 6;
                    uint h = DfQuest.Hash(name);
                    (int Kind, int Index)? r =
                        q.People.FindIndex(p => p.Hash == h) is >= 0 and var pi ? (1, pi)
                        : q.Places.FindIndex(p => p.Hash == h) is >= 0 and var li ? (2, li)
                        : q.Items.FindIndex(p => p.Hash == h) is >= 0 and var ii ? (0, ii)
                        : q.Foes.FindIndex(p => p.Hash == h) is >= 0 and var fi ? (4, fi)
                        : q.Clocks.FindIndex(p => p.Hash == h) is >= 0 and var ci ? (3, ci) : null;
                    if (r is not { } res) { sb.Append(name); continue; }
                    if (res.Kind == 1) last = res.Index;
                    sb.Append($"<Alias={TagName(Of(res.Kind, res.Index, mode))}>");
                }
                else
                {
                    string mac = m.Value;
                    if (mac is "%pcf" or "%pcn") sb.Append("<Alias=Player>");
                    else if (Pronouns.TryGetValue(mac, out var form)) sb.Append($"<Alias={TagName(Of(6, last, form))}>");
                    else if (Statics.TryGetValue(mac, out var code)) sb.Append($"<Alias={TagName(Of(5, code, 0))}>");
                    // Others (a joke, %jok) are left out.
                }
            }
            sb.Append(text, at, text.Length - at);
            return sb.ToString().Replace("  ", " ").Trim();
        }

        public static string TagName(int k) => $"T{k}";
    }

    Quest BuildQuest(DfQuest q, DfQuestItems items, Book letter)
    {
        string edid = Edid(q.Name);
        var rec = new Quest(mod)
        {
            EditorID = edid,
            Name = Title(q.Name),
            Priority = 50,
            Type = DfQuest.GroupOf(q.Name) is "Main" or "Start" ? Quest.TypeEnum.MainQuest : Quest.TypeEnum.SideQuest,
            Flags = Quest.Flag.AllowRepeatedStages,
        };
        mod.Quests.Add(rec);
        var tokens = new Tokens(q);
        int itemIndex(uint v) => q.Items.FindIndex(i => i.Index == v);
        int personIndex(uint v) => q.People.FindIndex(i => i.Index == v);
        int placeIndex(uint v) => q.Places.FindIndex(i => i.Index == v);
        int clockIndex(uint v) => q.Clocks.FindIndex(i => i.Index == v);
        int foeIndex(uint v) => q.Foes.FindIndex(i => i.Index == v);

        // The program.
        var ops = new List<int>();
        var prompts = new HashSet<int> { 1000 };
        var logIds = new List<int>();
        var rumorIds = new List<int>();
        var individuals = new List<int>();
        var dialogKeys = new SortedSet<int>();
        foreach (var o in q.Ops)
        {
            var info = DfQuestOps.Of(o.Code);
            int notMask = 0;
            var a = new int[5];
            for (int k = 0; k < 5; k++)
            {
                char role = k < info.Args.Length ? info.Args[k] : '-';
                var g = o.Args[k];
                bool valid = k < o.ArgCount;
                if (valid && g.Not) notMask |= 1 << k;
                a[k] = role switch
                {
                    _ when !valid => role == '#' ? 0 : -1,
                    '#' => g.Int,
                    _ when g.IsNone => -1,
                    's' => (int)g.Value,
                    'i' => itemIndex(g.Value),
                    'n' => personIndex(g.Value),
                    'p' => placeIndex(g.Value),
                    'c' => clockIndex(g.Value),
                    'f' => foeIndex(g.Value),
                    _ => -1,
                };
            }
            int kind = info.Kind switch { DfQuestOps.OpKind.Trigger => 0, DfQuestOps.OpKind.AlwaysOn => 1, _ => 2 };
            ops.AddRange([o.Code, kind, notMask, o.Message, .. a]);
            if (o.Code == 0x1D) prompts.Add(a[3]);
            if (o.Code == 0x17 && !logIds.Contains(a[1])) logIds.Add(a[1]);
            if (o.Code == 0x26 && !rumorIds.Contains(a[1])) rumorIds.Add(a[1]);
            if (o.Code == 0x4E && !individuals.Contains(a[1])) individuals.Add(a[1]);
            if (o.Code is 0x0A or 0x0B)
            {
                if (a[1] >= 0) dialogKeys.Add(a[1]);
                if (a[2] >= 0) dialogKeys.Add(20 + a[2]);
                if (a[3] >= 0) dialogKeys.Add(40 + a[3]);
            }
        }

        // Aliases: the player, people, items, individuals, then name tags (after the text is converted).
        rec.Aliases.Add(new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(PlayerRef) });
        var aliasScripts = new List<QuestFragmentAlias>();
        uint next = 1;
        QuestAlias Optional(string name) => new() { ID = next++, Name = name, Flags = QuestAlias.Flag.Optional | QuestAlias.Flag.AllowReserved | QuestAlias.Flag.AllowReuseInQuest };
        void Script(uint id, int kind, int index) => aliasScripts.Add(new QuestFragmentAlias
        {
            Property = new ScriptObjectProperty { Object = rec.ToLink<ISkyrimMajorRecordGetter>(), Alias = (short)id },
            Scripts = [new ScriptEntry { Name = "TMDfQuestAlias", Properties = [Int("Kind", kind), Int("Index", index)] }],
        });
        int personAlias = (int)next;
        for (int i = 0; i < q.People.Count; i++) { var al = Optional($"Person{i}"); rec.Aliases.Add(al); Script(al.ID, 1, i); }
        int itemAlias = (int)next;
        for (int i = 0; i < q.Items.Count; i++) { var al = Optional($"Item{i}"); rec.Aliases.Add(al); Script(al.ID, 0, i); }
        int individualAlias = (int)next;
        foreach (int f in individuals) { var al = Optional($"Individual{f}"); rec.Aliases.Add(al); Script(al.ID, 2, f); }
        int tagAlias = (int)next;

        // Messages (all variants), owned by the quest so the tags fill in; prompts get Yes and No.
        var msgRecs = new List<FormKey>();
        var msgIds = new List<int>();
        string Text(string raw) => tokens.Convert(raw, tagAlias);
        foreach (var (id, variants) in q.Messages.OrderBy(m => m.Key))
            for (int v = 0; v < variants.Count; v++)
            {
                var m = new Message(mod)
                {
                    EditorID = $"{edid}_{id}_{v}",
                    Description = Text(variants[v]),
                    Quest = rec.ToNullableLink<IQuestGetter>(),
                    Flags = Message.Flag.MessageBox,
                    MenuButtons = [],
                };
                if (prompts.Contains(id)) { m.MenuButtons.Add(new MessageButton { Text = "Yes" }); m.MenuButtons.Add(new MessageButton { Text = "No" }); }
                mod.Messages.Add(m);
                msgRecs.Add(m.FormKey);
                msgIds.Add(id);
                Messages++;
            }

        // The log: one objective per log message.
        for (int i = 0; i < logIds.Count; i++)
        {
            string text = q.Messages.TryGetValue(logIds[i], out var v) && v.Count > 0 ? Text(v[0]) : "";
            rec.Objectives.Add(new QuestObjective { Index = (ushort)i, DisplayText = text, Targets = [] });
            Objectives++;
        }

        // "Tell me about ..." per resource with an answer (anyInfo), opened by "add dialog"/"dialog link"; rumours.
        var groups = Enumerable.Range(0, 5).Select(g => GroupFaction(mod, g)).ToList();
        var stages = new SortedSet<int>();
        void About(int key, int kind, int index, int answer, int rumors)
        {
            if (answer <= 0 || !q.Messages.TryGetValue(answer, out var lines) || lines.Count == 0) return;
            var branch = new DialogBranch(mod) { EditorID = $"{edid}About{key}Branch", Quest = rec.ToLink<IQuestGetter>(), Category = DialogBranch.CategoryType.Player, Flags = DialogBranch.Flag.TopLevel };
            mod.DialogBranches.Add(branch);
            var topic = NewTopic($"{edid}About{key}", $"Tell me about <Alias={Tokens.TagName(tokens.Of(kind, index, 1))}>.", rec, branch);
            branch.StartingTopic = topic.ToNullableLink<IDialogTopicGetter>();
            stages.Add(100 + key);
            var said = lines.Concat(rumors > 0 && q.Messages.TryGetValue(rumors, out var r) ? r : []).ToList();
            foreach (var l in said)
            {
                var resp = Line(Text(l), [StageDone(rec, 100 + key), .. AnyTownsperson(groups)], random: said.Count > 1);
                if (kind == 2)
                    resp.VirtualMachineAdapter = new DialogResponsesAdapter
                    {
                        Scripts = [new ScriptEntry { Name = "TMDfQuestTopic", Properties = [Obj("Machine", Machine!.FormKey), Int("Service", 1), Obj("DfQuest", rec.FormKey), Int("Place", index)] }],
                        ScriptFragments = new ScriptFragments
                        {
                            ExtraBindDataVersion = 2, FileName = "TMDfQuestTopic",
                            OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMDfQuestTopic", FragmentName = "Fragment_End" },
                        },
                    };
                topic.Responses.Add(resp);
            }
        }
        foreach (int key in dialogKeys)
        {
            if (key < 20 && key < q.Places.Count) About(key, 2, key, q.Places[key].Text1, q.Places[key].Text2);
            else if (key is >= 20 and < 40 && key - 20 < q.People.Count) About(key, 1, key - 20, q.People[key - 20].Text1, q.People[key - 20].Text2);
            else if (key >= 40 && key - 40 < q.Items.Count) About(key, 0, key - 40, q.Items[key - 40].Text1, 0);
        }
        if (rumorIds.Count > 0)
        {
            var branch = new DialogBranch(mod) { EditorID = $"{edid}RumorsBranch", Quest = rec.ToLink<IQuestGetter>(), Category = DialogBranch.CategoryType.Player, Flags = DialogBranch.Flag.TopLevel };
            mod.DialogBranches.Add(branch);
            var topic = NewTopic($"{edid}Rumors", "Have you heard any rumors?", rec, branch);
            branch.StartingTopic = topic.ToNullableLink<IDialogTopicGetter>();
            for (int r = 0; r < rumorIds.Count; r++)
            {
                stages.Add(200 + r);
                foreach (var l in q.Messages.GetValueOrDefault(rumorIds[r]) ?? [])
                    topic.Responses.Add(Line(Text(l), [StageDone(rec, 200 + r), .. AnyTownsperson(groups)], random: true));
            }
        }
        foreach (int s in stages) rec.Stages.Add(new QuestStage { Index = (ushort)s, LogEntries = [] });

        // Name tags, now that every text is converted.
        for (int k = 0; k < tokens.List.Count; k++) rec.Aliases.Add(Optional(Tokens.TagName(k)));
        rec.NextAliasID = next;

        // Resources.
        var props = new List<ScriptProperty>
        {
            new ScriptStringProperty { Name = "DfName", Data = q.Name },
            Ints("Ops", ops),
            Ints("StateGlobal", Enumerable.Range(0, 64).Select(i => q.States.FirstOrDefault(s => s.Index == i) is { IsGlobal: true } s ? s.GlobalIndex : -1)),
            Ints("ClockState", q.Clocks.Select(c => q.States.FirstOrDefault(s => s.Hash == c.Hash && c.Hash != 0)?.Index ?? c.Index)),
            Ints("ClockKind", q.Clocks.Select(c => c.Kind)), Ints("ClockFlags", q.Clocks.Select(c => c.Flags)),
            Ints("ClockMin", q.Clocks.Select(c => c.Min)), Ints("ClockMax", q.Clocks.Select(c => c.Max)),
            Ints("ClockLink1", q.Clocks.Select(c => c.Link1)), Ints("ClockLink2", q.Clocks.Select(c => c.Link2)),
            Ints("ItemReward", q.Items.Select(i => i.Reward)), Ints("ItemGroup", q.Items.Select(i => i.Category)), Ints("ItemIndex", q.Items.Select(i => i.CategoryIndex)),
            Ints("ItemText1", q.Items.Select(i => i.Text1)), Ints("ItemText2", q.Items.Select(i => i.Text2)),
            Ints("GoldMin", q.Items.Select(i => i.Reward >= 2 && i.Category != 0xFFFF ? i.CategoryIndex : 0)),
            Ints("GoldMax", q.Items.Select(i => i.Reward >= 2 && i.Category != 0xFFFF ? i.Category : 0)),
            Objs("ItemBase", q.Items.Select(i => ItemBase(i, items, letter))),
            Ints("PersonKind", q.People.Select(p => (int)(short)p.Kind)), Ints("PersonFaction", q.People.Select(p => p.Faction)),
            Ints("PersonScope", q.People.Select(p => p.Gender)),
            Ints("FoeType", q.Foes.Select(f => f.Type)), Ints("FoeCount", q.Foes.Select(f => f.Count)),
            Objs("FoeBase", q.Foes.Select(f => Foe?.Invoke(f.Type) ?? FormKey.Null)),
            Objs("Msgs", msgRecs), Ints("MsgIds", msgIds), Ints("LogIds", logIds), Ints("RumorIds", rumorIds),
            Ints("TokenKind", tokens.List.Select(t => t.Kind)), Ints("TokenIndex", tokens.List.Select(t => t.Index)), Ints("TokenMode", tokens.List.Select(t => t.Mode)),
            Int("PersonAlias", personAlias), Int("ItemAlias", itemAlias), Int("IndividualAlias", individualAlias), Int("TagAlias", tagAlias),
            Ints("IndividualFactions", individuals),
        };
        var placeData = q.Places.Select(p => ResolvePlace(q, p)).ToList();
        props.Add(Ints("PlaceScope", placeData.Select(p => p.Scope)));
        props.Add(Ints("PlaceP1", placeData.Select(p => p.P1)));
        props.Add(Ints("PlaceP2", placeData.Select(p => p.P2)));
        props.Add(Ints("PlaceP3", placeData.Select(p => p.P3)));
        rec.VirtualMachineAdapter = new QuestAdapter { Scripts = [new ScriptEntry { Name = "TMDfQuest", Properties = [.. props] }], Aliases = [.. aliasScripts] };
        return rec;
    }

    FormKey ItemBase(DfQuest.QItem i, DfQuestItems items, Book letter)
    {
        if (i.Reward >= 2 || i.Reward == 1 || i.CategoryIndex == 0xFFFF) return FormKey.Null;
        if (i.Category == 9 && i.CategoryIndex == 5) return letter.FormKey;
        return items.Specific(i.Category, i.CategoryIndex) ?? FormKey.Null;
    }

    List<DfLocation>? locations;

    /// <summary>A place's scope and parameters for TMDfQuestMachine.FindSite; a fixed site becomes its Daggerfall location
    /// id and whether it is the town's streets (0) or its dungeon (1) (Daggerfall Unity Place.SetupFixedLocation: the id
    /// itself is a town, the id - 1 a location whose dungeon is meant).</summary>
    (int Scope, int P1, int P2, int P3) ResolvePlace(DfQuest q, DfQuest.QPlace p)
    {
        if (p.Scope != 0) return (p.Scope, p.P1, p.P2, p.P3);
        locations ??= DfLocations.LoadAll(arena2);
        var (id, dungeon, loc) = ResolveFixed(locations, p.P1, p.P2);
        if (loc is null) Problems.Add($"{q.Name}: fixed place 0x{p.P1:X4} not found");
        else if (!dungeon && loc.Type is not (DfLocationType.TownCity or DfLocationType.TownHamlet or DfLocationType.TownVillage) && !ExteriorOnly.Contains(loc))
            ExteriorOnly.Add(loc);
        return (0, id, dungeon ? 1 : 0, 0);
    }

    /// <summary>A fixed place's Daggerfall location (Daggerfall Unity Place.SetupFixedLocation): the location with that id,
    /// its dungeon when it is not a town (or the second half names a marker inside, 0xFAnn), else the town's streets; or the
    /// location one id lower, its dungeon.</summary>
    public static (int LocationId, bool Dungeon, DfLocation? Location) ResolveFixed(IReadOnlyList<DfLocation> locations, int p1, int p2)
    {
        if (locations.FirstOrDefault(l => l.LocationId == p1) is { } exact)
        {
            bool town = exact.Type is DfLocationType.TownCity or DfLocationType.TownHamlet or DfLocationType.TownVillage;
            return (p1, exact.DungeonBlocks.Count > 0 && (!town || (ushort)p2 >> 8 == 0xFA), exact);
        }
        if (locations.FirstOrDefault(l => l.LocationId == p1 - 1 && l.DungeonBlocks.Count > 0) is { } below)
            return (below.LocationId, true, below);
        return (p1, true, null);
    }

    /// <summary>Every TMDf* script property the plugin sets that its script doesn't declare (they would be dropped in game),
    /// and every Object property left empty, from the Papyrus sources.</summary>
    public static string CheckScriptProperties(ISkyrimModGetter plugin, string papyrusDir)
    {
        var declared = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        HashSet<string> Of(string script)
        {
            if (declared.TryGetValue(script, out var set)) return set;
            set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string file = Path.Combine(papyrusDir, script + ".psc");
            if (File.Exists(file))
                foreach (Match m in Regex.Matches(File.ReadAllText(file), @"^\s*\S+\s+Property\s+(\w+)", RegexOptions.Multiline | RegexOptions.IgnoreCase))
                    set.Add(m.Groups[1].Value);
            return declared[script] = set;
        }
        var problems = new SortedDictionary<string, int>();
        int checkedProps = 0;
        void Check(IScriptEntryGetter e, string where)
        {
            if (!e.Name.StartsWith("TMDf", StringComparison.OrdinalIgnoreCase)) return;
            var set = Of(e.Name);
            foreach (var p in e.Properties)
            {
                checkedProps++;
                if (!set.Contains(p.Name)) problems[$"{e.Name}.{p.Name} not declared"] = problems.GetValueOrDefault($"{e.Name}.{p.Name} not declared") + 1;
                if (p is IScriptObjectPropertyGetter o && o.Object.IsNull) problems[$"{e.Name}.{p.Name} empty"] = problems.GetValueOrDefault($"{e.Name}.{p.Name} empty") + 1;
            }
        }
        foreach (var r in plugin.EnumerateMajorRecords())
        {
            if (r is IHaveVirtualMachineAdapterGetter { VirtualMachineAdapter: { } vm })
                foreach (var e in vm.Scripts) Check(e, r.EditorID ?? "");
            if (r is IQuestGetter { VirtualMachineAdapter: { } qa })
                foreach (var a in qa.Aliases) foreach (var e in a.Scripts) Check(e, r.EditorID ?? "");
            if (r is IDialogResponsesGetter { VirtualMachineAdapter: { } da })
                foreach (var e in da.Scripts) Check(e, r.EditorID ?? "");
        }
        return $"Script properties: {checkedProps} checked" + (problems.Count == 0 ? ", all declared and filled" : ": " + string.Join("; ", problems.Select(p => $"{p.Key} x{p.Value}")));
    }
}
