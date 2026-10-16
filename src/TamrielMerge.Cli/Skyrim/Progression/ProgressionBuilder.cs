using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim.Progression;

/// <summary>
/// "TamrielMerge Progression.esp" (docs/progression.md): the Tamriel menu and what it shows, built after the games from
/// whichever were converted. Like the start plugin it masters only Skyrim.esm and finds each game's records at run
/// time by plugin name and FormID, so a game left out is simply missing from the lists.
/// <list type="bullet">
/// <item>Faction registry: each converted game's joinable factions (its own records, never Skyrim's: Daggerfall's
/// guilds, temples and orders, Morrowind's and Oblivion's factions with three or more titled ranks that the game doesn't
/// hide from the player), with rank titles.</item>
/// <item>The TMProgression quest (with the player alias that re-registers the menu key after loading), the "Tamriel"
/// lesser power and the "Tamriel Menu" control (U, rebindable in Settings > Controls) that open the menu, and the menu
/// itself (Interface\TamrielMerge\tamriel.swf).</item>
/// <item>The new skills (ProgressionSkills, TMSkills) of the games converted, with their perk trees, and the passive web
/// (PassiveWeb, TMPassives); the menu's Skills and Passives tabs draw them from data the build writes into the menu.</item>
/// </list>
/// </summary>
public static class ProgressionBuilder
{
    public const string PluginName = "TamrielMerge Progression.esp";
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey PlayerRef = Sk(0x14);

    /// <summary>The converted games' plugins, in story order, with how the menu names each game.</summary>
    static readonly (string Plugin, string Game, string Prefix)[] Games =
    [
        ("Daggerfall.esm", "Daggerfall", "DFGuild"), ("Redguard.esm", "Redguard", "RGFaction"),
        ("Morrowind.esm", "Morrowind", "MWFaction"), ("Oblivion.esm", "Oblivion", "OBFaction"),
    ];

    public sealed record FactionEntry(string Plugin, uint Id, string Name, string Game, string[] Ranks);
    public sealed record Report(IReadOnlyList<FactionEntry> Factions, string Menu, string Path, IReadOnlyList<SkillDef> Skills,
        int Perks, int DaggerfallSkills, PassiveWeb.Web Web, IReadOnlyList<ProgressionSkills.Listed> Listed);

    /// <summary>Each converted game's joinable factions: its own records with three or more titled ranks.</summary>
    public static List<FactionEntry> Registry(string dir)
    {
        var list = new List<FactionEntry>();
        foreach (var (plugin, game, prefix) in Games)
        {
            string path = System.IO.Path.Combine(dir, plugin);
            if (!File.Exists(path)) continue;
            using var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            foreach (var f in m.Factions.Where(f => f.FormKey.ModKey == m.ModKey && f.EditorID?.StartsWith(prefix) == true && !f.Flags.HasFlag(Faction.FactionFlag.HiddenFromPC)))
            {
                var titles = f.Ranks.Select(r => r.Title?.Male?.String ?? r.Title?.Female?.String ?? "").ToArray();
                if (titles.Count(t => t.Length > 0) < 3) continue;
                list.Add(new(plugin, f.FormKey.ID, f.Name?.String ?? f.EditorID!, game, titles));
            }
        }
        return [.. list.OrderBy(f => Array.FindIndex(Games, g => g.Game == f.Game)).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>A record each converted game's plugin may have, by an EditorID made from the game's two-letter prefix.</summary>
    static List<(string Plugin, uint Id)> Tagged(string dir, Func<string, string> editorId, Func<ISkyrimModGetter, string, IMajorRecordGetter?> find)
    {
        var list = new List<(string, uint)>();
        foreach (var (plugin, _, prefix) in Games)
        {
            if (!File.Exists(System.IO.Path.Combine(dir, plugin))) continue;
            // Daggerfall's other towns (Daggerfall Towns.esm) have their own lists (their factions are Daggerfall.esm's).
            var files = new List<string> { plugin };
            if (plugin == "Daggerfall.esm" && File.Exists(System.IO.Path.Combine(dir, DaggerfallTowns.PluginName))) files.Add(DaggerfallTowns.PluginName);
            foreach (var file in files)
            {
                using var m = SkyrimMod.CreateFromBinaryOverlay(System.IO.Path.Combine(dir, file), SkyrimRelease.SkyrimSE);
                if (find(m, editorId(prefix[..2])) is { } r) list.Add((file, r.FormKey.ID));
            }
        }
        return list;
    }

    /// <summary>Each converted game's pickpocket faction ("{prefix}Pickpockets", CoinTrade).</summary>
    static List<(string Plugin, uint Id)> Pickpockets(string dir)
    {
        var list = new List<(string, uint)>();
        foreach (var (plugin, _, prefix) in Games)
        {
            string path = System.IO.Path.Combine(dir, plugin);
            if (!File.Exists(path)) continue;
            string edid = CoinTrade.PickpocketsId(prefix[..2]);
            using var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            if (m.Factions.FirstOrDefault(f => f.FormKey.ModKey == m.ModKey && f.EditorID == edid) is { } f) list.Add((plugin, f.FormKey.ID));
        }
        return list;
    }

    /// <summary>Builds the plugin and the menu into <paramref name="dir"/> from the converted games' plugins in
    /// <paramref name="games"/> (by default <paramref name="dir"/> itself, where a conversion writes them).</summary>
    public static Report Build(string dir, string skyrimData, string? games = null, Action<string>? log = null)
    {
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        using var skyrim = SkyrimMod.CreateFromBinaryOverlay(System.IO.Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
        string outDir = dir;
        dir = games ?? dir;
        var factions = Registry(dir);

        var quest = new Quest(mod)
        {
            EditorID = "TMProgression",
            Name = "Tamriel",
            Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce,
            Priority = 10,
            Type = Quest.TypeEnum.Misc,
            Aliases = [new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(PlayerRef) }],
            NextAliasID = 1,
        };
        mod.Quests.Add(quest);

        // The "Tamriel" lesser power: a self effect that opens the menu.
        var effect = skyrim.MagicEffects.First(e => e.EditorID == "WaterbreathingFFSelf").Duplicate(mod.GetNextFormKey());
        effect.EditorID = "TMTamrielEffect";
        effect.Name = "Tamriel";
        effect.Archetype = new MagicEffectArchetype { Type = MagicEffectArchetype.TypeEnum.Script };
        effect.Flags |= MagicEffect.Flag.NoMagnitude | MagicEffect.Flag.NoDuration | MagicEffect.Flag.HideInUI;
        effect.Flags &= ~(MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental);
        effect.CastingArt.Clear();
        effect.HitEffectArt.Clear();
        effect.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts = [new ScriptEntry { Name = "TMProgressionPowerEffect", Properties = [new ScriptObjectProperty { Name = "Progression", Object = quest.ToLink<ISkyrimMajorRecordGetter>() }] }],
        };
        mod.MagicEffects.Add(effect);
        var voice = skyrim.Spells.First(s => s.Type == SpellType.Power && !s.EquipmentType.IsNull).EquipmentType.FormKey;
        var power = new Spell(mod)
        {
            EditorID = "TMTamrielPower",
            Name = "Tamriel",
            ObjectBounds = new ObjectBounds(),
            Type = SpellType.LesserPower,
            CastType = CastType.FireAndForget,
            TargetType = TargetType.Self,
            EquipmentType = voice.ToNullableLink<IEquipTypeGetter>(),
            Description = "Shows the factions you have joined in each land, the coin you hold in each, your skills from them, and your passive web.",
            Flags = SpellDataFlag.ManualCostCalc,
            Effects = [new Effect { BaseEffect = effect.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData() }],
        };
        mod.Spells.Add(power);

        static ScriptStringListProperty Strings(string name, IEnumerable<string> values) => new() { Name = name, Data = [.. values] };
        quest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts =
            [
                new ScriptEntry
                {
                    Name = "TMProgression",
                    Properties =
                    [
                        new ScriptObjectProperty { Name = "PlayerRef", Object = PlayerRef.ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "TamrielPower", Object = power.ToLink<ISkyrimMajorRecordGetter>() },
                        Strings("FactionPlugins", factions.Select(f => f.Plugin)),
                        new ScriptIntListProperty { Name = "FactionIds", Data = [.. factions.Select(f => (int)f.Id)] },
                        Strings("FactionNames", factions.Select(f => f.Name)),
                        Strings("FactionGames", factions.Select(f => f.Game)),
                        Strings("FactionRanks", factions.Select(f => string.Join("|", f.Ranks.Select(r => r.Replace("|", "/"))))),
                    ],
                },
            ],
            Aliases =
            [
                new QuestFragmentAlias
                {
                    Property = new ScriptObjectProperty { Object = quest.ToLink<ISkyrimMajorRecordGetter>(), Alias = 0 },
                    Scripts = [new ScriptEntry { Name = "TMProgressionPlayer" }],
                },
            ],
        };

        // Skyrim's own merchants change other times' coin into septims (CoinTrade).
        if (skyrim.Factions.FirstOrDefault(f => f.EditorID == "JobMerchantFaction") is { } merchants)
            CoinTrade.AddExchange(mod, quest, merchants.FormKey, story: 4, prefix: "TMSkyrim", title: "Money changer", baseFee: 10);

        // Pickpockets: each converted game's (found by name in its plugin) and Skyrim's Thieves Guild.
        var thieves = Pickpockets(dir);
        if (skyrim.Factions.FirstOrDefault(f => f.EditorID == "ThievesGuildFaction") is { } guild) thieves.Add(("Skyrim.esm", guild.FormKey.ID));
        var pickpocketsOn = new GlobalShort(mod) { EditorID = "TMPickpocketsOn", Data = 1 };
        mod.Globals.Add(pickpocketsOn);
        quest.VirtualMachineAdapter.Scripts.Add(new ScriptEntry
        {
            Name = "TMPickpockets",
            Properties =
            [
                new ScriptObjectProperty { Name = "PlayerRef", Object = PlayerRef.ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptObjectProperty { Name = "Enabled", Object = pickpocketsOn.ToLink<ISkyrimMajorRecordGetter>() },
                Strings("FactionPlugins", thieves.Select(t => t.Plugin)),
                new ScriptIntListProperty { Name = "FactionIds", Data = [.. thieves.Select(t => (int)t.Id)] },
            ],
        });

        // Nights in town: the converted games' guards (who may doze) and Morrowind's and Oblivion's town centres.
        var nightGuards = Tagged(dir, p => TownNights.GuardsId(p), (m, id) => m.Factions.FirstOrDefault(f => f.FormKey.ModKey == m.ModKey && f.EditorID == id));
        var nightTowns = Tagged(dir, p => TownNights.TownCentresId(p), (m, id) => m.FormLists.FirstOrDefault(f => f.FormKey.ModKey == m.ModKey && f.EditorID == id));
        var dozeEffect = skyrim.MagicEffects.First(e => e.EditorID == "WaterbreathingFFSelf").Duplicate(mod.GetNextFormKey());
        dozeEffect.EditorID = "TMDozeEffect";
        dozeEffect.Name = "Dozing";
        dozeEffect.Archetype = new MagicEffectArchetype { Type = MagicEffectArchetype.TypeEnum.Script };
        dozeEffect.CastType = CastType.ConstantEffect;
        dozeEffect.Flags |= MagicEffect.Flag.NoMagnitude | MagicEffect.Flag.NoDuration | MagicEffect.Flag.HideInUI;
        dozeEffect.Flags &= ~(MagicEffect.Flag.Hostile | MagicEffect.Flag.Detrimental);
        dozeEffect.CastingArt.Clear();
        dozeEffect.HitEffectArt.Clear();
        mod.MagicEffects.Add(dozeEffect);
        var doze = new Spell(mod)
        {
            EditorID = "TMDozeAbility",
            Name = "Dozing",
            ObjectBounds = new ObjectBounds(),
            Type = SpellType.Ability,
            CastType = CastType.ConstantEffect,
            TargetType = TargetType.Self,
            Flags = SpellDataFlag.ManualCostCalc,
            Effects = [new Effect { BaseEffect = dozeEffect.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData() }],
        };
        mod.Spells.Add(doze);
        dozeEffect.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts = [new ScriptEntry { Name = "TMDozeEffect", Properties = [new ScriptObjectProperty { Name = "Doze", Object = doze.ToLink<ISkyrimMajorRecordGetter>() }] }],
        };
        var nightOn = new GlobalShort(mod) { EditorID = "TMNightDangerOn", Data = 1 };
        mod.Globals.Add(nightOn);
        quest.VirtualMachineAdapter.Scripts.Add(new ScriptEntry
        {
            Name = "TMNightDanger",
            Properties =
            [
                new ScriptObjectProperty { Name = "PlayerRef", Object = PlayerRef.ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptObjectProperty { Name = "Enabled", Object = nightOn.ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptObjectProperty { Name = "GameHour", Object = Sk(0x38).ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptObjectProperty { Name = "Doze", Object = doze.ToLink<ISkyrimMajorRecordGetter>() },
                Strings("GuardPlugins", nightGuards.Select(t => t.Plugin)),
                new ScriptIntListProperty { Name = "GuardIds", Data = [.. nightGuards.Select(t => (int)t.Id)] },
                Strings("TownPlugins", nightTowns.Select(t => t.Plugin)),
                new ScriptIntListProperty { Name = "TownIds", Data = [.. nightTowns.Select(t => (int)t.Id)] },
            ],
        });

        // The new skills (TMSkills) and the passive web (TMPassives).
        var skills = ProgressionSkills.Converted(dir);
        var listed = ProgressionSkills.FindListed(dir, skills, log);
        var built = ProgressionSkills.Prepare(mod, skills);
        var perks = new ProgressionPerks(mod, skyrim, built.Lists.ToDictionary(kv => kv.Key, kv => kv.Value.FormKey), built.Globals);
        ProgressionSkills.Build(mod, skyrim, perks, built);
        built.HitEffect.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts = [new ScriptEntry { Name = "TMSkillHitEffect", Properties = [new ScriptObjectProperty { Name = "Skills", Object = quest.ToLink<ISkyrimMajorRecordGetter>() }] }],
        };
        var dfSkills = ProgressionSkills.DaggerfallSkills(dir);
        quest.VirtualMachineAdapter.Scripts.Add(SkillsScript(built, listed, dfSkills));
        var web = PassiveWeb.Build(mod, perks, factions);
        quest.VirtualMachineAdapter.Scripts.Add(WebScript(web, skyrim, dir));

        // The menu, with the build's skills and web written into it.
        string source = File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Interface", "tamriel.as"));
        if (!source.Contains(MenuDataLine)) throw new InvalidDataException($"tamriel.as has no line \"{MenuDataLine}\" for the build's data");
        source = source.Replace(MenuDataLine, MenuData(built, dfSkills, web, factions));
        string menu = System.IO.Path.Combine(outDir, "Interface", "TamrielMerge", "tamriel.swf");
        Ui.FlashMenus.Compile(source, menu);
        WriteControls(outDir, skyrimData);

        string path = System.IO.Path.Combine(outDir, PluginName);
        // A light plugin (ESL) when its records fit one's FormIDs (0x800-0xFFF), so it takes no full plugin slot.
        if (mod.ModHeader.Stats.NextFormID <= 0x1000) mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
        TopicSubtypes.Fill(mod);
        mod.WriteToBinary(path, new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
        {
            MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(
                new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => ModKey.FromFileName(f)).ToArray()),
        });
        return new Report(factions, menu, path, skills, built.Tree.Count, dfSkills.Count, web, listed);
    }

    static ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };
    static ScriptObjectListProperty Objs(string name, IEnumerable<FormKey?> keys)
    {
        var p = new ScriptObjectListProperty { Name = name };
        foreach (var k in keys) p.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(k ?? FormKey.Null) });
        return p;
    }
    static ScriptIntListProperty Ints(string name, IEnumerable<int> values) => new() { Name = name, Data = [.. values] };
    static ScriptFloatListProperty Floats(string name, IEnumerable<float> values) => new() { Name = name, Data = [.. values] };
    static ScriptStringListProperty Texts(string name, IEnumerable<string> values) => new() { Name = name, Data = [.. values] };

    /// <summary>TMSkills: each skill's globals, curve, ability and effect perk with how they grow, its perk tree, the
    /// records the form lists are filled with, and Daggerfall's own skills (DFSkill&lt;name&gt; in Daggerfall.esm).</summary>
    static ScriptEntry SkillsScript(ProgressionSkills.Built b, List<ProgressionSkills.Listed> listed, List<(string Name, uint Id)> dfSkills)
    {
        var s = b.Skills;
        var abilityFirst = new List<int>();
        var abilityPerLevel = new List<float>();
        var entryFirst = new List<int>();
        var entryBase = new List<float>();
        var entryPerLevel = new List<float>();
        foreach (var d in s)
        {
            abilityFirst.Add(abilityPerLevel.Count);
            abilityPerLevel.AddRange(d.Avs.Select(a => a.PerLevel));
            entryFirst.Add(entryBase.Count);
            entryBase.AddRange(d.Entries.Select(e => e.Base));
            entryPerLevel.AddRange(d.Entries.Select(e => e.PerLevel));
        }
        // Stamped by content, so TMSkills fills its lists again when a new build lists other records.
        int stamp = listed.Aggregate(17, (h, l) => unchecked(h * 31 + l.Plugin.GetHashCode(StringComparison.OrdinalIgnoreCase) * 7 + (int)l.Id * 3 + l.List));
        return new ScriptEntry
        {
            Name = "TMSkills",
            Properties =
            [
                Obj("PlayerRef", PlayerRef),
                Texts("SkillNames", s.Select(d => d.Name)),
                Texts("SkillIds", s.Select(d => d.Id)),
                Objs("Levels", b.Levels.Select(g => (FormKey?)g.FormKey)),
                Objs("Experience", b.Xp.Select(g => (FormKey?)g.FormKey)),
                Floats("ImproveMult", s.Select(d => d.ImproveMult)),
                Floats("ImproveOffset", s.Select(d => d.ImproveOffset)),
                Floats("UseExperience", s.Select(d => d.Use)),
                Objs("Abilities", b.Abilities.Select(a => a?.FormKey)),
                Ints("AbilityFirst", abilityFirst),
                Floats("AbilityPerLevel", abilityPerLevel),
                Objs("EffectPerks", b.EffectPerks.Select(p => p?.FormKey)),
                Ints("EntryFirst", entryFirst),
                Ints("EntryCount", s.Select(d => d.Entries.Length)),
                Floats("EntryBase", entryBase),
                Floats("EntryPerLevel", entryPerLevel),
                Objs("TreePerks", b.Tree.Select(t => (FormKey?)t.Perk.FormKey)),
                Ints("TreeSkill", b.Tree.Select(t => t.Skill)),
                Ints("TreeLevel", b.Tree.Select(t => t.Level)),
                Ints("TreeAfter", b.Tree.Select(t => t.After)),
                Objs("Lists", ProgressionSkills.ListNames.Select(n => (FormKey?)b.Lists[n].FormKey)),
                Texts("ListPlugins", listed.Select(l => l.Plugin)),
                Ints("ListIds", listed.Select(l => (int)l.Id)),
                Ints("ListOf", listed.Select(l => l.List)),
                new ScriptIntProperty { Name = "ListStamp", Data = stamp },
                Obj("MediumWorn", b.Globals["TMMediumArmorWorn"]),
                Obj("UnarmoredOn", b.Globals["TMUnarmored"]),
                Obj("HitPerk", b.HitPerk.FormKey),
                Obj("Gold", Sk(0x00000F)),
                Obj("ArmorLight", Sk(0x06BBD3)),
                Obj("ArmorHeavy", Sk(0x06BBD2)),
                Texts("DfSkillNames", dfSkills.Select(d => d.Name)),
                Ints("DfSkillIds", dfSkills.Select(d => (int)d.Id)),
            ],
        };
    }

    /// <summary>TMPassives: the web's nodes (kind, stat, perk, gate), links, the sign starts with Skyrim's standing stones
    /// (Morrowind's signs give the same ones) and Oblivion's sign numbers (TMObState's), the stats ability, and each
    /// registry faction's gate group.</summary>
    static ScriptEntry WebScript(PassiveWeb.Web web, ISkyrimModGetter skyrim, string dir)
    {
        var stones = PassiveWeb.Signs.Select(n => skyrim.Spells.FirstOrDefault(s => s.EditorID == $"doom{n}Ability")?.FormKey).ToList();
        var (obQuest, obSigns) = OblivionSigns(dir);
        return new ScriptEntry
        {
            Name = "TMPassives",
            Properties =
            [
                Obj("PlayerRef", PlayerRef),
                Ints("NodeKind", web.Nodes.Select(n => (int)n.Kind)),
                Ints("NodeStat", web.Nodes.Select(n => n.Stat)),
                Floats("NodeAmount", web.Nodes.Select(n => n.Amount)),
                Objs("NodePerks", web.Nodes.Select(n => n.Perk?.FormKey)),
                Ints("NodeGate", web.Nodes.Select(n => n.Gate)),
                Ints("NodeGateRank", web.Nodes.Select(n => n.GateRank)),
                Ints("LinkA", web.Links.Select(l => l.A)),
                Ints("LinkB", web.Links.Select(l => l.B)),
                Ints("SignStart", web.Starts),
                Objs("SignStones", stones),
                Ints("FactionGroup", web.FactionGroup),
                Ints("ObSigns", obSigns),
                new ScriptIntProperty { Name = "ObStateId", Data = (int)obQuest },
                Obj("StatsAbility", web.Stats.FormKey),
            ],
        };
    }

    /// <summary>Oblivion's TMObState quest and, for each sign in its birthsign menu's order, which of the 13 it is.</summary>
    static (uint Quest, int[] Signs) OblivionSigns(string dir)
    {
        string path = System.IO.Path.Combine(dir, "Oblivion.esm");
        if (!File.Exists(path)) return (0, []);
        using var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        if (m.Quests.FirstOrDefault(q => q.FormKey.ModKey == m.ModKey && q.EditorID == "TMObState") is not { } quest) return (0, []);
        var lists = quest.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == "TMObState")?.Properties
            .OfType<IScriptObjectListPropertyGetter>().FirstOrDefault(p => p.Name == "BirthsignSpells");
        var signs = (lists?.Objects ?? []).Select(o => m.FormLists.FirstOrDefault(l => l.FormKey == o.Object.FormKey)?.EditorID ?? "")
            .Select(e => Array.FindIndex(PassiveWeb.Signs, s => e.Contains(s, StringComparison.OrdinalIgnoreCase))).ToArray();
        return (quest.FormKey.ID, signs);
    }

    /// <summary>The line of tamriel.as the build replaces with its data (the menu compiled on its own draws the empty
    /// states).</summary>
    public const string MenuDataLine = "var built = null; // the build's skills and passive web (ProgressionBuilder.MenuData)";

    static string Q(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
    static string F(float f) => f.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The skills (names, where from, what they do, their perk trees), Daggerfall's own skills, the stats and the
    /// web's nodes and links, as an ActionScript object for the menu.</summary>
    static string MenuData(ProgressionSkills.Built b, List<(string Name, uint Id)> dfSkills, PassiveWeb.Web web, List<FactionEntry> factions)
    {
        var games = new Dictionary<string, string> { ["DF"] = "Daggerfall", ["MW"] = "Morrowind", ["OB"] = "Oblivion" };
        var sb = new System.Text.StringBuilder("var built = {\n");
        sb.Append("  skills: [").AppendJoin(", ", b.Skills.Select(s =>
            $"{{n: {Q(s.Name)}, f: {Q(string.Join(", ", s.Games.Select(g => games[g])))}, r: {Q(s.Rises)}, e: {Q(s.Effect)}}}")).Append("],\n");
        sb.Append("  perks: [").AppendJoin(", ", b.Tree.Select(t =>
            $"[{t.Skill}, {t.Level}, {t.After}, {Q(t.Name)}, {t.Rank}, {t.Ranks}, {Q(t.Description)}]")).Append("],\n");
        sb.Append("  df: [").AppendJoin(", ", dfSkills.Select(d => Q(d.Name))).Append("],\n");
        sb.Append("  stats: [").AppendJoin(", ", PassiveWeb.Stats.Select(s => Q(ProgressionPerks.AvName(s.Av)))).Append("],\n");
        sb.Append("  signs: [").AppendJoin(", ", PassiveWeb.Signs.Select(Q)).Append("],\n");
        sb.Append("  nodes: [").AppendJoin(",\n    ", web.Nodes.Select(n =>
            $"[{F(n.X)}, {F(n.Y)}, {(int)n.Kind}, {Q(n.Name)}, {Q(n.Description)}, {Q(n.GateText)}, {n.Sign}, {n.Stat}, {F(n.Amount)}]")).Append("],\n");
        sb.Append("  links: [").AppendJoin(", ", web.Links.Select(l => $"{l.A}, {l.B}")).Append("],\n");
        sb.Append("  cx: ").Append(F(PassiveWeb.CentreX)).Append(", cy: ").Append(F(PassiveWeb.CentreY)).Append(",\n");
        sb.Append("  factions: ").Append(factions.Count).Append('\n');
        sb.Append("};");
        return sb.ToString();
    }

    /// <summary>The menu key as a Skyrim control: the vanilla PC control map (read from the player's own Interface
    /// archive) plus "Tamriel Menu" on U, remappable to a key or mouse button, so it shows in Settings > Controls; the
    /// list labels it "$Tamriel Menu", which SKSE translates from Interface\Translations\{plugin}_{language}.txt.</summary>
    public const string MenuControl = "Tamriel Menu";
    static readonly string[] Languages = ["english", "french", "german", "italian", "spanish", "polish", "russian", "czech", "japanese", "chinese"];

    static void WriteControls(string dir, string skyrimData)
    {
        var reader = Mutagen.Bethesda.Archives.Archive.CreateReader(GameRelease.SkyrimSE, System.IO.Path.Combine(skyrimData, "Skyrim - Interface.bsa"));
        var file = reader.Files.First(f => f.Path.Replace('/', '\\').Equals(@"interface\controls\pc\controlmap.txt", StringComparison.OrdinalIgnoreCase));
        var lines = System.Text.Encoding.UTF8.GetString(file.GetBytes()).Replace("\r\n", "\n").Split('\n').ToList();
        // The first input context is Main Gameplay; add ours after its last line (Quick Map).
        int at = lines.FindIndex(l => l.StartsWith("Quick Map", StringComparison.Ordinal));
        if (at < 0) throw new InvalidDataException("controlmap.txt has no Quick Map line");
        lines.RemoveAll(l => l.StartsWith(MenuControl + "\t", StringComparison.Ordinal));
        lines.Insert(at + 1, $"{MenuControl}\t\t0x16\t\t\t\t0xff\t0xff\t\t\t\t1\t1\t0\t0x908");
        string controls = System.IO.Path.Combine(dir, "Interface", "Controls", "PC", "controlmap.txt");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(controls)!);
        File.WriteAllText(controls, string.Join("\r\n", lines), new System.Text.UTF8Encoding(false));

        string translations = System.IO.Path.Combine(dir, "Interface", "Translations");
        Directory.CreateDirectory(translations);
        string stem = System.IO.Path.GetFileNameWithoutExtension(PluginName);
        foreach (var language in Languages)
            File.WriteAllText(System.IO.Path.Combine(translations, $"{stem}_{language}.txt"), $"${MenuControl}\t{MenuControl}\r\n", System.Text.Encoding.Unicode);
    }
}
