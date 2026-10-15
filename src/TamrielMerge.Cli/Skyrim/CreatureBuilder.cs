using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's creatures and enemy classes as Skyrim actors.
/// <list type="bullet">
/// <item>Each Daggerfall monster is its own actor named as in Daggerfall, copied from the closest vanilla (or
/// Dawnguard/Dragonborn, which every Skyrim SE install has) creature, so it keeps that creature's body, AI, attacks
/// and faction. Its level is Daggerfall's times two (Daggerfall levels run to ~21, Skyrim's creatures to ~50);
/// stats are auto-calculated from it. Monsters with no Skyrim counterpart (Imp, Centaur, Nymph, Harpy, Dreugh...)
/// use a stand-in body until their own models exist; <see cref="StandIns"/> lists them.</item>
/// <item>Enemy classes (Mage, Nightblade, Knight...) use Skyrim's leveled bandit and warlock lists.</item>
/// <item>Each dungeon type gets a leveled list built from Daggerfall's encounter table: entries are ordered low to
/// high, and Daggerfall picks around the player's level, so entry i becomes available at player level ~2(i-2).</item>
/// </list>
/// </summary>
public sealed class CreatureBuilder
{
    public const int LevelScale = 2;

    /// <summary>How a Daggerfall mobile becomes Skyrim: a copy of an actor, or an existing leveled list.</summary>
    sealed record Map(string Template, bool StandIn = false, bool OwnFactions = false);

    static readonly Dictionary<int, Map> Table = new()
    {
        [0] = new("EncSkeever"),                         // Rat
        [1] = new("EncWisp", StandIn: true),              // Imp
        [2] = new("EncSpriggan"),
        [3] = new("EncIceWraith", StandIn: true),         // Giant Bat
        [4] = new("EncBear"),                            // Grizzly Bear
        [5] = new("EncSabreCat"),                        // Sabertooth Tiger
        [6] = new("EncFrostbiteSpider"),
        [7] = new("LCharBanditMeleeOrcM"),               // Orc
        [8] = new("EncTroll", StandIn: true),             // Centaur
        [9] = new("LCharWerewolf"),
        [10] = new("EncSprigganMatron", StandIn: true),   // Nymph
        [11] = new("EncSlaughterfish"),
        [12] = new("LCharBanditMeleeOrcM"),              // Orc Sergeant
        [13] = new("EncHagraven", StandIn: true),         // Harpy
        [14] = new("DLC2EncWerebear", StandIn: true),     // Wereboar
        [15] = new("EncSkeleton01Melee1H"),              // Skeletal Warrior
        [16] = new("EncGiant01"),
        [17] = new("EncDraugr01Melee1HHeadM00"),         // Zombie
        [18] = new("encGhost01Magic"),                   // Ghost
        [19] = new("EncDraugr01Melee2HHeadM00"),         // Mummy
        [20] = new("EncChaurus", StandIn: true),          // Giant Scorpion
        [21] = new("LCharBanditWizard"),                 // Orc Shaman
        [22] = new("DLC1EncGargoyleSmall"),
        [23] = new("encGhost03Magic"),                   // Wraith
        [24] = new("LCharBanditBossOrcM"),               // Orc Warlord
        [25] = new("EncAtronachFrost", StandIn: true),    // Frost Daedra
        [26] = new("EncDremoraMelee03", StandIn: true),   // Fire Daedra
        [27] = new("DLC2EncLurker01", StandIn: true),     // Daedroth
        [28] = new("LCharVampire"),
        [29] = new("EncDremoraWarlock04", StandIn: true), // Daedra Seducer
        [30] = new("LCharVampireBoss"),                  // Vampire Ancient
        [31] = new("EncDremoraMelee06"),                 // Daedra Lord
        [32] = new("EncDragonPriest", StandIn: true),     // Lich
        [33] = new("EncDragonPriest", StandIn: true),     // Ancient Lich
        [34] = new("EncIceWraith", StandIn: true),        // Dragonling
        [35] = new("EncAtronachFlame"),                  // Fire Atronach
        [36] = new("EncDwarvenSphere01", StandIn: true),  // Iron Atronach
        [37] = new("EncTroll", StandIn: true),            // Flesh Atronach
        [38] = new("EncAtronachFrost"),                  // Ice Atronach
        [40] = new("EncIceWraith", StandIn: true),        // Dragonling
        [41] = new("EncMudcrabGiant", StandIn: true),     // Dreugh
        [42] = new("EncSlaughterfish", StandIn: true),    // Lamia
        [128] = new("LCharBanditWizard"),                // Mage
        [129] = new("LCharBanditMelee1H"),               // Spellsword
        [130] = new("LCharBanditWizard"),                // Battlemage
        [131] = new("LCharWarlockConjurer"),             // Sorcerer
        [132] = new("LCharBanditWizard"),                // Healer
        [133] = new("LCharBanditMelee1H"),               // Nightblade
        [134] = new("LCharBanditMelee1H"),               // Bard
        [135] = new("LCharBanditMelee1H"),               // Burglar
        [136] = new("LCharBanditMelee1H"),               // Rogue
        [137] = new("LCharBanditMelee1H"),               // Acrobat
        [138] = new("LCharBanditMelee1H"),               // Thief
        [139] = new("LCharBanditMelee1H"),               // Assassin
        [140] = new("LCharBanditMelee1H"),               // Monk
        [141] = new("LCharBanditMissile"),               // Archer
        [142] = new("LCharBanditMissile"),               // Ranger
        [143] = new("LCharBanditMelee2H"),               // Barbarian
        [144] = new("LCharBanditMelee1HTank"),           // Warrior
        [145] = new("LCharBanditMelee1HTank"),           // Knight
        [146] = new("EncSoldierImperialBretonM01MaleSoldier", OwnFactions: true),   // City Watch: an Imperial soldier's look
    };

    static readonly HashSet<string> PartnerScripts = new(StringComparer.OrdinalIgnoreCase) { "WitchlightActorScript" };

    /// <summary>Creatures that only live in water: placed only under a dungeon block's water (DungeonCellBuilder).</summary>
    public static readonly HashSet<int> Aquatic = [11, 41, 42];

    readonly SkyrimMod mod;
    readonly Dictionary<string, ISkyrimMajorRecordGetter> templates = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<int, FormKey> spawns = new();          // what leveled lists contain: an actor or a list
    readonly Dictionary<int, FormKey> placeables = new();      // what a placed reference uses: always an actor
    readonly Dictionary<(FormKey, int), FormKey> listActors = new();   // our "DFLvl..." actors standing for a leveled list (per pacifying language)
    INpcGetter? listActorTemplate;
    readonly Dictionary<int, FormKey> passiveSpawns = new();
    readonly Dictionary<int, FormKey> encounterLists = new();
    readonly List<IDisposable> masters = new();

    public SortedSet<string> Missing { get; } = new();
    public IEnumerable<string> StandIns => Table.Where(t => t.Value.StandIn).Select(t => $"{DfCreatures.Mobiles[t.Key].Name} ({t.Value.Template})").Distinct();

    public CreatureBuilder(SkyrimMod mod, string skyrimData)
    {
        this.mod = mod;
        var wanted = Table.Values.Select(m => m.Template).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in new[] { "Skyrim.esm", "Dawnguard.esm", "Dragonborn.esm" })
        {
            var m = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, file), SkyrimRelease.SkyrimSE);
            masters.Add(m);
            // Only records the master defines (not its overrides of Skyrim.esm).
            foreach (var r in m.Npcs.Cast<ISkyrimMajorRecordGetter>().Concat(m.LeveledNpcs))
                if (r.FormKey.ModKey == m.ModKey && r.EditorID is { } id && wanted.Contains(id)) templates.TryAdd(id, r);
            // Skyrim places leveled lists through an actor templated on the list; LvlBanditMelee1H is copied for ours.
            listActorTemplate ??= m.Npcs.FirstOrDefault(n => n.FormKey.ModKey == m.ModKey && n.EditorID == "LvlBanditMelee1H");
        }
        foreach (var id in wanted.Where(w => !templates.ContainsKey(w))) Missing.Add(id);
    }

    /// <summary>Daggerfall's enemy senses (DfSenses): the quest its hostile actors' TMDfSenses report to.</summary>
    public Quest? Senses { get; private set; }

    Dictionary<FormKey, INpcGetter>? vanillaNpcs;
    INpcGetter? Vanilla(FormKey key)
    {
        vanillaNpcs ??= masters.OfType<ISkyrimModGetter>().SelectMany(m => m.Npcs).GroupBy(n => n.FormKey).ToDictionary(g => g.Key, g => g.Last());
        return vanillaNpcs.GetValueOrDefault(key);
    }

    public void Build()
    {
        Senses = DfSenses.Quest(mod);
        foreach (var (id, map) in Table)
        {
            if (!templates.TryGetValue(map.Template, out var template)) continue;
            var mobile = DfCreatures.Mobiles[id];
            if (template is INpcGetter npc) spawns[id] = placeables[id] = CopyActor(npc, mobile, passive: false).FormKey;
            else
            {
                // Enemy classes: Skyrim's leveled lists (no passive variant).
                // Encounter lists hold the actor standing for Skyrim's list (it carries TMDfSenses), not the list
                // itself, so a class an encounter list picks has Daggerfall's senses too.
                placeables[id] = passiveSpawns[id] = ListActor(template.FormKey, template.EditorID!, senses: true, DfSenses.SeesThroughInvisibility.Contains(id), DfSenses.LanguageOf(id));
                spawns[id] = placeables[id];
            }
        }
        // Dungeon tables (0-19) and the overland ones by climate, in and out of locations, day and night (20-37).
        foreach (var table in DfCreatures.EncounterTables.Where(t => t.Index <= 37))
        {
            var list = new LeveledNpc(mod)
            {
                EditorID = "DFLCharEncounter" + new string(table.Name.Where(char.IsLetterOrDigit).ToArray()),
                Flags = LeveledNpc.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer,
                Entries = [],
            };
            for (int i = 0; i < table.Mobiles.Length; i++)
            {
                int m = table.Mobiles[i];
                if ((table.Index != 19 && Aquatic.Contains(m)) || !spawns.TryGetValue(m, out var spawn)) continue;
                list.Entries.Add(new LeveledNpcEntry { Data = new LeveledNpcEntryData { Level = (short)Math.Max(1, LevelScale * (i - 2)), Count = 1, Reference = spawn.ToLink<INpcSpawnGetter>() } });
            }
            mod.LeveledNpcs.Add(list);
            encounterLists[table.Index] = ListActor(list.FormKey, list.EditorID!);
            // A dungeon type's random encounters may come while the player rests there (TMDfSensesControl).
            if (table.Index < UnderwaterTable && mod.FormLists.FirstOrDefault(f => f.EditorID == "DFRestEncounters") is { } rest)
                rest.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(encounterLists[table.Index]));
        }
    }

    /// <summary>An actor to place for a leveled list: a copy of LvlBanditMelee1H templated on the list. One standing for
    /// Skyrim's list of an enemy class (<paramref name="senses"/>) carries TMDfSenses as its own script (not the vanilla
    /// bandit's the list picks); one for a Daggerfall encounter list takes its scripts from what the list picks (Use
    /// Script), and everything those lists hold carries TMDfSenses.</summary>
    FormKey ListActor(FormKey list, string listEditorId, bool senses = false, bool seesInvisible = false, int language = -1)
    {
        // Vanilla has placeholders too, but for some lists only odd variants (ghostly or pre-aggroed bandits).
        if (listActors.TryGetValue((list, language), out var actor)) return actor;
        var copy = (listActorTemplate ?? throw new InvalidOperationException("LvlBanditMelee1H not found")).Duplicate(mod.GetNextFormKey());
        copy.EditorID = "DFLvl" + (listEditorId.StartsWith("LChar") ? listEditorId[5..] : listEditorId.StartsWith("DFLChar") ? listEditorId[7..] : listEditorId)
            + (language == 1 ? "Streetwise" : "");   // the classes Streetwise pacifies (thieves, assassins) apart from those Etiquette does
        copy.Template.SetTo(list);
        copy.Configuration.TemplateFlags |= NpcConfiguration.TemplateFlag.Script;
        if (senses) DfSenses.Attach(copy, Senses!, seesInvisible, Vanilla, language);
        mod.Npcs.Add(copy);
        return listActors[(list, language)] = copy.FormKey;
    }

    Npc CopyActor(INpcGetter template, DfMobile mobile, bool passive)
    {
        var copy = template.Duplicate(mod.GetNextFormKey());
        string clean = new(mobile.Name.Where(char.IsLetterOrDigit).ToArray());
        copy.EditorID = $"DFCreature{clean}{(mobile.Id == 40 ? "B" : "")}{(passive ? "Passive" : "")}";
        copy.Name = mobile.Name.Split(" - ")[0];   // "City Watch - The Haltmeister" is the City Watch
        copy.ShortName = null;
        copy.Configuration.Level = new NpcLevel { Level = (short)Math.Max(1, mobile.Level * LevelScale) };
        // Daggerfall's passive markers (guards, residents) fight only when attacked.
        // Daggerfall's monsters attack on sight; some vanilla creatures (bears, spriggans, wisps) only defend themselves.
        if (copy.AIData is { } ai)
            ai.Aggression = passive ? Aggression.Unaggressive : ai.Aggression == Aggression.Unaggressive ? Aggression.Aggressive : ai.Aggression;
        // Scripts that only work alongside their vanilla partners: the wisp's expects a linked Wispmother.
        if (copy.VirtualMachineAdapter is { } vm)
        {
            vm.Scripts.RemoveAll(sc => PartnerScripts.Contains(sc.Name));
            if (vm.Scripts.Count == 0) copy.VirtualMachineAdapter = null;
        }
        // People of Daggerfall's own (the City Watch) keep none of the vanilla soldier's Skyrim factions.
        if (Table.TryGetValue(mobile.Id, out var map) && map.OwnFactions)
        {
            copy.Factions.Clear();
            copy.CrimeFaction.Clear();
            copy.Factions.Add(new RankPlacement { Faction = TownNights.Guards(mod, "DF").ToLink<IFactionGetter>(), Rank = 0 });
            if (copy.AIData is { } watch) watch.Aggression = passive ? Aggression.Unaggressive : Aggression.Aggressive;
        }
        // Templated copies (draugr, ghosts) would otherwise take their name from the template.
        copy.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.BaseData;
        // Daggerfall's senses for its hostile ones (DfSenses); the passive ones fight only when attacked.
        if (!passive) DfSenses.Attach(copy, Senses!, DfSenses.SeesThroughInvisibility.Contains(mobile.Id), Vanilla, DfSenses.LanguageOf(mobile.Id));
        mod.Npcs.Add(copy);
        return copy;
    }

    /// <summary>The actor or leveled list for a Daggerfall mobile, or null when it has no Skyrim form (or lives in water
    /// and the spot is dry).</summary>
    public FormKey? Spawn(int mobileId, bool passive = false, bool inWater = false)
    {
        if ((Aquatic.Contains(mobileId) && !inWater) || !placeables.TryGetValue(mobileId, out var spawn)) return null;
        if (!passive) return spawn;
        // Passive variants are made on first use (with a master, the master's if it has one, else the hostile one).
        if (!passiveSpawns.TryGetValue(mobileId, out var p))
        {
            if (usingMaster) return spawn;
            passiveSpawns[mobileId] = p = CopyActor((INpcGetter)templates[Table[mobileId].Template], DfCreatures.Mobiles[mobileId], passive: true).FormKey;
        }
        return p;
    }

    bool usingMaster;

    /// <summary>
    /// Points every actor and list this builder made at the same-named records of an already built plugin (Daggerfall.esm),
    /// for plugins that extend it (the dungeon batches) without copies of its creatures. Build into a plugin that is
    /// thrown away first.
    /// </summary>
    public void UseMaster(ISkyrimModGetter master)
    {
        var mine = mod.Npcs.Where(n => n.EditorID is not null).ToDictionary(n => n.FormKey, n => n.EditorID!);
        var theirs = master.Npcs.Where(n => n.EditorID is not null).GroupBy(n => n.EditorID!).ToDictionary(g => g.Key, g => g.First().FormKey);
        FormKey Map(FormKey k) => mine.TryGetValue(k, out var e) && theirs.TryGetValue(e, out var t) ? t : k;
        foreach (var d in new[] { placeables, passiveSpawns, encounterLists })
            foreach (var key in d.Keys.ToList()) d[key] = Map(d[key]);
        // Passive copies the master doesn't have fall back to the hostile ones.
        foreach (var key in passiveSpawns.Keys.ToList())
            if (passiveSpawns[key].ModKey == mod.ModKey) passiveSpawns.Remove(key);
        usingMaster = true;
    }

    /// <summary>Bosses (Vampire Ancient, Daedra Lord, Ancient Lich) and people (enemy classes, 128 and up) aren't
    /// ordinary creatures for procedural creatures.</summary>
    public static bool IsOrdinary(int mobileId) => mobileId < 128 && mobileId is not (30 or 31 or 33) && Table.ContainsKey(mobileId);

    /// <summary>Daggerfall's ordinary creatures as made: actor, name, level.</summary>
    public IEnumerable<(FormKey Actor, string Name, int Level)> Ordinary() =>
        placeables.Where(p => IsOrdinary(p.Key)).Select(p => (p.Value, DfCreatures.Mobiles[p.Key].Name, Math.Max(1, DfCreatures.Mobiles[p.Key].Level * LevelScale)));

    /// <summary>The leveled list for a dungeon type's random encounters; under water, Daggerfall's underwater table
    /// (19) whatever the dungeon (Daggerfall Unity RDBLayout.AddRandomRDBEnemy).</summary>
    public FormKey? RandomEncounter(DfDungeonType type, bool underwater = false) =>
        encounterLists.TryGetValue(underwater ? UnderwaterTable : (int)type, out var l) ? l : null;
    public const int UnderwaterTable = 19;

    /// <summary>The actor to place for an encounter table (a leveled list; e.g. a climate's town-at-night table).</summary>
    public FormKey? Encounter(int table) => encounterLists.TryGetValue(table, out var l) ? l : null;
}
