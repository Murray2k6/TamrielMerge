using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's people and creatures as Skyrim actors.
/// <list type="bullet">
/// <item>NPCs keep Morrowind's name, level, inventory, gold, essential/respawn flags and how far they wander; their
/// race, sex, face and voice come from a vanilla NPC of the same race and sex whose FaceGen head ships with Skyrim
/// (template "traits"), chosen by the NPC's ID among generic encounter NPCs. A new NPC without FaceGen data would
/// render with Skyrim's dark-face bug. Fight 80+ makes them attack on sight.</item>
/// <item>Creatures copy the closest vanilla (or Dragonborn) creature, keeping its body, animations, attacks and AI,
/// renamed and levelled as in Morrowind; <see cref="CreatureTable"/> lists the stand-ins.</item>
/// <item>Leveled creature lists (LEVC) become leveled actor lists, placed through an actor templated on the list.</item>
/// </list>
/// </summary>
public sealed partial class MwPluginBuilder
{
    readonly Dictionary<string, IMajorRecord?> actors = new(StringComparer.OrdinalIgnoreCase);
    Dictionary<(FormKey Race, bool Female), List<INpcGetter>>? faces;
    Dictionary<string, INpcGetter>? vanillaNpcs;
    ISkyrimModDisposableGetter? dragonbornEsm;

    static readonly Dictionary<string, string> MwRaces = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dark Elf"] = "DarkElfRace", ["High Elf"] = "HighElfRace", ["Wood Elf"] = "WoodElfRace", ["Imperial"] = "ImperialRace",
        ["Nord"] = "NordRace", ["Breton"] = "BretonRace", ["Redguard"] = "RedguardRace", ["Orc"] = "OrcRace",
        ["Khajiit"] = "KhajiitRace", ["Argonian"] = "ArgonianRace",
    };

    /// <summary>Morrowind creature ID fragment -> vanilla creature actor (first match wins).</summary>
    static readonly (string Match, string Template)[] CreatureTable =
    [
        ("netch_bull", "DLC2EncNetchBull"), ("netch_betty", "DLC2EncNetchBetty"), ("netch", "DLC2EncNetchBetty"),
        ("riekling", "DLC2EncRiekling04Melee"), ("boar", "DLC2EncBoarWild"), ("rat", "EncSkeever"), ("mudcrab", "EncMudcrabMedium"),
        ("kagouti", "DLC2EncBoarWild"), ("alit", "EncSabreCat"), ("guar", "EncCow"), ("cliff racer", "EncIceWraith"), ("cliffracer", "EncIceWraith"),
        ("nix", "EncWolf"), ("durzog", "EncDog"), ("scrib", "DLC2EncAshHopper"), ("kwama forager", "DLC2EncAshHopper"),
        ("kwama worker", "EncChaurus"), ("kwama warrior", "EncChaurusReaper"), ("kwama queen", "EncChaurusReaper"), ("shalk", "DLC2EncAshHopper"),
        ("slaughterfish", "EncSlaughterfish"), ("dreugh", "EncMudcrabGiant"), ("ash_", "DLC2EncAshSpawn2H03"), ("ash ", "DLC2EncAshSpawn2H03"),
        ("ascended", "DLC2EncAshSpawn2H03"), ("corprus", "DLC2EncAshSpawn2H03"), ("dagoth", "DLC2EncAshSpawnMagic03"),
        ("bonewalker", "EncDraugr"), ("draugr", "EncDraugr"), ("bonelord", "EncSkeletonNecro"), ("skeleton", "EncSkeleton01"),
        ("ghost", "EncSkeleton01"), ("atronach_flame", "EncAtronachFlame"), ("atronach_frost", "EncAtronachFrost"), ("atronach_storm", "EncAtronachStorm"),
        ("dremora", "EncDremoraMelee"), ("golden saint", "EncDremoraWarlock"), ("golden_saint", "EncDremoraWarlock"), ("scamp", "EncSkeever"),
        ("clannfear", "EncSabreCat"), ("daedroth", "DLC2EncLurker01"), ("hunger", "EncTroll"), ("ogrim", "EncGiant01"), ("winged twilight", "EncHagraven"),
        ("centurion_spider", "EncDwarvenSpider01"), ("centurion_sphere", "EncDwarvenSphere01"), ("centurion_steam", "EncDwarvenCenturion01"),
        ("centurion_projectile", "DLC2EncDwarvenBallista01"), ("centurion", "EncDwarvenSphere01"), ("fabricant", "EncDwarvenCenturion01"),
        ("bear", "EncBear"), ("wolf", "EncWolf"), ("spriggan", "EncSpriggan"), ("troll", "EncTrollFrost"), ("goblin", "EncFalmer01"),
        ("lich", "EncDragonPriestStaff"), ("werewolf", "EncWerewolf03Boss"), ("horker", "EncHorker"),
        // Unique creatures: the nearest body until their own models exist.
        ("ancestor", "EncSkeleton01"), ("haunt", "EncSkeleton01"), ("wraith", "EncSkeleton01"), ("dahrk mezalf", "EncSkeleton01"),
        ("worm lord", "EncDragonPriestStaff"), ("lustidrike", "EncDremoraMelee"), ("vivec", "EncDremoraWarlock"), ("almalexia", "EncDremoraWarlock"),
        ("yagrum", "EncDwarvenSphere01"), ("imperfect", "EncDwarvenCenturion01"), ("glenmoril", "EncHagraven"), ("udyrfrykte", "EncTroll"),
        ("hircine", "EncWerewolf03Boss"), ("giant", "EncGiant01"), ("akulakhan", "EncAtronachStorm"),
    ];

    INpcGetter? VanillaNpc(string editorIdOrPrefix)
    {
        if (vanillaNpcs is null)
        {
            Vanilla<IWeaponGetter>("IronSword");   // opens Skyrim.esm
            dragonbornEsm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(SkyrimData!, "Dragonborn.esm"), SkyrimRelease.SkyrimSE);
            vanillaNpcs = new(StringComparer.OrdinalIgnoreCase);
            foreach (var n in skyrimEsm!.Npcs.Concat(dragonbornEsm.Npcs.Where(n => n.FormKey.ModKey == dragonbornEsm.ModKey)))
                if (n.EditorID is { } e) vanillaNpcs.TryAdd(e, n);
        }
        if (vanillaNpcs.TryGetValue(editorIdOrPrefix, out var exact)) return exact;
        return vanillaNpcs.Where(kv => kv.Key.StartsWith(editorIdOrPrefix, StringComparison.OrdinalIgnoreCase)).OrderBy(kv => kv.Key).Select(kv => kv.Value).FirstOrDefault();
    }

    /// <summary>Vanilla NPCs with their own FaceGen head, by race and sex (generic encounter NPCs preferred).</summary>
    List<INpcGetter> FacesFor(FormKey race, bool female)
    {
        if (faces is null)
        {
            VanillaNpc("EncBandit");
            var withFace = Directory.GetFiles(SkyrimData!, "Skyrim - Meshes*.bsa")
                .Select(b => Mutagen.Bethesda.Archives.Archive.CreateReader(GameRelease.SkyrimSE, b))
                .SelectMany(a => a.Files).Select(f => f.Path.ToLowerInvariant())
                .Where(p => p.Contains(@"facegeom\skyrim.esm\")).Select(Path.GetFileNameWithoutExtension).ToHashSet();
            faces = skyrimEsm!.Npcs.Where(n => withFace.Contains(n.FormKey.ID.ToString("x8")))
                .GroupBy(n => (n.Race.FormKey, n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female)))
                .ToDictionary(g => g.Key, g =>
                {
                    var voiced = Skyrim.VanillaActors.WithRaceVoice(skyrimEsm!, g.Where(n => !n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Essential)).ToList(), g.Key.Item1, g.Key.Item2);
                    var generic = voiced.Where(n => n.EditorID?.StartsWith("Enc") == true).ToList();
                    return (generic.Count >= 3 ? generic : voiced).OrderBy(n => n.FormKey.ID).ToList();
                });
        }
        return faces.GetValueOrDefault((race, female)) ?? [];
    }

    static int StableHash(string s) { unchecked { int h = 17; foreach (char c in s.ToLowerInvariant()) h = h * 31 + c; return h & 0x7FFFFFFF; } }

    /// <summary>The Skyrim actor for a Morrowind NPC_, CREA or LEVC ID (created on first use), or null.</summary>
    IMajorRecord? Actor(string id)
    {
        if (actors.TryGetValue(id, out var hit)) return hit;
        actors[id] = null;
        if (!world.Objects.TryGetValue(id, out var rec)) return null;
        try
        {
            return actors[id] = rec.Type switch
            {
                "NPC_" => Person(rec),
                "CREA" => Creature(rec),
                "LEVC" => LeveledCreature(rec),
                _ => null,
            };
        }
        catch (Exception e) { Problems.Add($"actor {id}: {e.Message}"); return null; }
    }

    static FormKey PackageFor(int wanderDistance) => wanderDistance switch
    {
        <= 0 => Sk(0x025BFC),     // DefaultStayAtEditorLocation
        <= 256 => Sk(0x09361E),   // DefaultSandboxEditorLocation256
        <= 512 => Sk(0x01B217),   // DefaultSandboxEditorLocation512
        _ => Sk(0x0317C8),        // DefaultSandboxEditorLocation1024
    };

    /// <summary>Items an actor carries (NPCO): each converted item with its count.</summary>
    void AddInventory(Npc npc, Tes3Record rec)
    {
        foreach (var f in rec.All("NPCO"))
        {
            int count = Math.Abs(f.Int(0));
            string itemId = Tes3Plugin.Encoding.GetString(f.Data, 4, f.Data.Length - 4).TrimEnd('\0');
            if (Item(itemId) is { } item)
            {
                npc.Items ??= [];
                npc.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = new FormLink<IItemGetter>(item.FormKey), Count = StackOf(itemId, count) } });
            }
        }
    }

    static int WanderDistance(Tes3Record rec) => rec.Get("AI_W") is { } w ? BitConverter.ToUInt16(w.Data, 0) : 0;
    static int Fight(Tes3Record rec) => rec.Get("AIDT") is { } a ? a.Data[2] : 0;

    Npc? Person(Tes3Record rec)
    {
        uint flags = (uint)(rec.Get("FLAG")?.Int() ?? 0);
        bool female = (flags & 1) != 0;
        string? raceId = rec.Str("RNAM");
        if (raceId is null || !MwRaces.TryGetValue(raceId, out var skyRace)) { Problems.Add($"npc {rec.Id}: race {raceId} has no Skyrim counterpart"); return null; }
        VanillaNpc("EncBandit");   // opens the masters
        var race = skyrimEsm!.Races.First(r => r.EditorID == skyRace);
        var pool = FacesFor(race.FormKey, female);
        if (pool.Count == 0) { Problems.Add($"npc {rec.Id}: no vanilla face for {skyRace} {(female ? "F" : "M")}"); return null; }
        var look = pool[StableHash(rec.Id) % pool.Count];

        var npc = mod.Npcs.DuplicateInAsNewRecord(look);
        npc.EditorID = "MW" + Sanitize(rec.Id);
        npc.Name = rec.Str("FNAM");
        npc.ShortName = null;
        npc.VirtualMachineAdapter = null;
        npc.Factions.Clear();
        // Not the vanilla look's crime faction: Morrowind's people answer to Morrowind's law (MwLaw), never Skyrim's.
        UnderLaw(npc, guard: false);
        npc.Items = null;
        npc.DefaultOutfit.Clear();
        npc.SleepingOutfit.Clear();
        npc.DeathItem.Clear();
        npc.Packages.Clear();
        npc.Keywords = null;
        npc.Perks = null;
        // Appearance (race, sex, face, voice, height) from the vanilla NPC; everything else is Morrowind's.
        npc.Template = new FormLinkNullable<INpcSpawnGetter>(look.FormKey);
        npc.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        var nflags = NpcConfiguration.Flag.Unique | (female ? NpcConfiguration.Flag.Female : 0);
        if ((flags & 2) != 0) nflags |= NpcConfiguration.Flag.Essential;
        if ((flags & 4) != 0) nflags |= NpcConfiguration.Flag.Respawn;
        npc.Configuration.Flags = nflags;
        var npdt = rec.Get("NPDT");
        int level = npdt is { } d ? BitConverter.ToInt16(d.Data, 0) : 1;
        npc.Configuration.Level = new NpcLevel { Level = (short)Math.Clamp((int)MathF.Round(level * 1.5f), 1, 81) };
        npc.Packages.Add(new FormLink<IPackageGetter>(PackageFor(WanderDistance(rec))));
        if (npc.AIData is { } ai) ai.Aggression = Fight(rec) >= 80 ? Aggression.VeryAggressive : Aggression.Unaggressive;
        AddInventory(npc, rec);
        AddSpells(npc, rec);
        int gold = npdt is { } g ? (g.Data.Length == 12 ? g.Int(8) : g.Data.Length >= 52 ? g.Int(48) : 0) : 0;
        if (gold > 0)
        {
            npc.Items ??= [];
            npc.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = new FormLink<IItemGetter>(Sk(0x00000F)), Count = gold } });
        }
        Skyrim.VanillaActors.WearInventory(mod, npc, outfits, "MW");
        AddFactionAndClass(npc, rec);
        // NPDT's disposition: byte 44 of the full record, byte 2 of the auto-calculated one.
        if (npdt is { } nd && (nd.Data.Length >= 52 || nd.Data.Length == 12))
            Skyrim.Disposition.Start(npc, nd.Data[nd.Data.Length == 12 ? 2 : 44]);
        return npc;
    }

    readonly Dictionary<string, Outfit> outfits = new();

    Npc? Creature(Tes3Record rec)
    {
        string id = rec.Id.ToLowerInvariant(), name = (rec.Str("FNAM") ?? "").ToLowerInvariant();
        var map = CreatureTable.FirstOrDefault(t => id.Contains(t.Match) || name.Contains(t.Match));
        if (map.Template is null) { Problems.Add($"creature {rec.Id} ({rec.Str("FNAM")}): no stand-in"); return null; }
        var template = VanillaNpc(map.Template);
        if (template is null) { Problems.Add($"creature {rec.Id}: template {map.Template} not found"); return null; }
        var npc = mod.Npcs.DuplicateInAsNewRecord(template);
        npc.EditorID = "MW" + Sanitize(rec.Id);
        npc.Name = rec.Str("FNAM");
        npc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.BaseData;   // keep Morrowind's name
        // Morrowind's Fight decides hostility: below 80 a creature (guar, netch, Yagrum Bagarn) doesn't attack on sight.
        if (Fight(rec) < 80 && npc.AIData is { } ai) ai.Aggression = Aggression.Unaggressive;
        var npdt = rec.Get("NPDT");
        int level = npdt is { } d ? d.Int(4) : 1;
        if (npc.Configuration.Level is NpcLevel) npc.Configuration.Level = new NpcLevel { Level = (short)Math.Clamp((int)MathF.Round(level * 1.5f), 1, 81) };
        AddInventory(npc, rec);
        AddSpells(npc, rec);
        return npc;
    }

    INpcGetter? listActorTemplate;

    /// <summary>A leveled creature list (LEVC) as a leveled actor list, placed through a copy of LvlBanditMelee1H
    /// templated on it (Skyrim places leveled lists through such an actor).</summary>
    Npc? LeveledCreature(Tes3Record rec)
    {
        int flags = rec.Get("DATA")?.Int() ?? 0;
        var list = new LeveledNpc(mod)
        {
            EditorID = "MWList" + Sanitize(rec.Id),
            ObjectBounds = new ObjectBounds(),
            ChanceNone = new Percent(Math.Clamp((rec.Get("NNAM")?.Data[0] ?? 0) / 100.0, 0, 1)),
            Flags = (flags & 1) != 0 ? LeveledNpc.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer : 0,
            Entries = [],
        };
        mod.LeveledNpcs.Add(list);
        string? pending = null;
        foreach (var f in rec.Fields)
        {
            if (f.Type == "CNAM") pending = f.Text;
            else if (f.Type == "INTV" && pending is not null)
            {
                if (Actor(pending) is Npc member)
                    list.Entries.Add(new LeveledNpcEntry { Data = new LeveledNpcEntryData { Level = (short)BitConverter.ToUInt16(f.Data, 0), Count = 1, Reference = new FormLink<INpcSpawnGetter>(member.FormKey) } });
                pending = null;
            }
        }
        if (list.Entries.Count == 0) return null;
        listActorTemplate ??= VanillaNpc("LvlBanditMelee1H") ?? throw new InvalidOperationException("LvlBanditMelee1H not found");
        var actor = mod.Npcs.DuplicateInAsNewRecord(listActorTemplate);
        actor.EditorID = "MWLvl" + Sanitize(rec.Id);
        actor.Template = new FormLinkNullable<INpcSpawnGetter>(list.FormKey);
        return actor;
    }
}
