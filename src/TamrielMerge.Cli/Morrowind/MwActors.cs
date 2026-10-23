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
            dragonbornEsm ??= SkyrimMod.CreateFromBinaryOverlay(Path.Combine(SkyrimData!, "Dragonborn.esm"), SkyrimRelease.SkyrimSE);
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
    /// <summary>
    /// Morrowind's AI settings as Morrowind uses them (TMMwActorAI, on every person and creature): Hello, Fight, Flee and
    /// Alarm from the record (AIDT bytes 0, 2, 3, 4), the engine's settings from Morrowind's (GMSTs), whether it flies or
    /// swims (CREA flags 0x20, 0x10). Skyrim's own aggression is off (the script starts combat by Morrowind's fight term:
    /// 2026-10-22, a Fight of 80 had been "very aggressive" and under it never, whatever the distance or disposition), and
    /// Alarm sets Morality (Skyrim's witnesses report what they wouldn't do: Morrowind's report at Alarm 100).
    /// </summary>
    void ActorAI(Npc npc, Tes3Record rec, bool person)
    {
        var aidt = rec.Get("AIDT")?.Data;
        int At(int i) => aidt is { } d && d.Length > i ? d[i] : 0;
        int alarm = At(4);
        if (npc.AIData is { } ai)
        {
            ai.Aggression = Aggression.Unaggressive;
            ai.Responsibility = alarm >= 100 ? Responsibility.NoCrime : Responsibility.AnyCrime;
        }
        int flags = rec.Get("FLAG")?.Int() ?? 0;
        float Setting(string name, float fallback) => world.Settings.TryGetValue(name, out var g)
            ? g.Get("FLTV") is { } fl ? fl.Float() : g.Get("INTV") is { } iv ? iv.Int() : fallback : fallback;
        npc.VirtualMachineAdapter ??= new VirtualMachineAdapter();
        npc.VirtualMachineAdapter.Scripts.Add(new ScriptEntry
        {
            Name = "TMMwActorAI",
            Properties =
            [
                new ScriptIntProperty { Name = "Hello", Data = At(0) },
                new ScriptIntProperty { Name = "Fight", Data = At(2) },
                new ScriptIntProperty { Name = "Flee", Data = At(3) },
                new ScriptIntProperty { Name = "Alarm", Data = alarm },
                // NPDT's reputation: byte 45 of the full record, byte 3 of the auto-calculated one.
                new ScriptIntProperty { Name = "Reputation", Data = person && rec.Get("NPDT")?.Data is { } nd ? nd.Length == 12 ? nd[3] : nd.Length >= 52 ? nd[45] : 0 : 0 },
                new ScriptBoolProperty { Name = "IsPerson", Data = person },
                new ScriptBoolProperty { Name = "Flies", Data = !person && (flags & 0x20) != 0 },
                new ScriptBoolProperty { Name = "Swims", Data = !person && (flags & 0x10) != 0 },
                new ScriptFloatProperty { Name = "FightDistanceBase", Data = Setting("iFightDistanceBase", 10) },
                new ScriptFloatProperty { Name = "FightDistanceMultiplier", Data = Setting("fFightDistanceMultiplier", 0.005f) },
                new ScriptFloatProperty { Name = "FightDispMult", Data = Setting("fFightDispMult", 0.2f) },
                new ScriptFloatProperty { Name = "WerewolfFightMod", Data = Setting("iWerewolfFightMod", 100) },
                new ScriptFloatProperty { Name = "FleeHealthMult", Data = Setting("fAIFleeHealthMult", 7) },
                new ScriptFloatProperty { Name = "FleeFleeMult", Data = Setting("fAIFleeFleeMult", 0.3f) },
                new ScriptObjectProperty { Name = "WerewolfKills", Object = WerewolfKillsGlobal().ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptObjectProperty { Name = "LoadCount", Object = LoadCountGlobal().ToLink<ISkyrimMajorRecordGetter>() },
                // Its record's AI package (the first: Morrowind's current one): -1 none, 0 wander, 1 travel, 2 escort,
                // 3 follow, 4 activate (TMMwAI's numbers); greeting and idle remarks go by it (OpenMW actors.cpp).
                new ScriptIntProperty { Name = "RecordPackage", Data = rec.Fields.Select(f => f.Type).FirstOrDefault(t => t is "AI_W" or "AI_T" or "AI_E" or "AI_F" or "AI_A") switch { "AI_W" => 0, "AI_T" => 1, "AI_E" => 2, "AI_F" => 3, "AI_A" => 4, _ => -1 } },
                new ScriptFloatProperty { Name = "GreetDistanceMultiplier", Data = Setting("iGreetDistanceMultiplier", 6) },
                new ScriptFloatProperty { Name = "GreetDuration", Data = Setting("iGreetDuration", 4) },
                new ScriptFloatProperty { Name = "GreetDistanceReset", Data = Setting("fGreetDistanceReset", 512) },
                new ScriptFloatProperty { Name = "VoiceIdleOdds", Data = Setting("fVoiceIdleOdds", 10) },
                new ScriptFloatProperty { Name = "VoiceHitOdds", Data = Setting("iVoiceHitOdds", 30) },
                new ScriptFloatProperty { Name = "VoiceAttackOdds", Data = Setting("iVoiceAttackOdds", 10) },
                // NPDT's disposition: byte 44 of the full record, byte 2 of the auto-calculated one.
                new ScriptIntProperty { Name = "BaseDisposition", Data = person && rec.Get("NPDT")?.Data is { } npd ? npd.Length == 12 ? npd[2] : npd.Length >= 52 ? npd[44] : 50 : 50 },
                new ScriptObjectProperty { Name = "CommonDiseases", Object = SpellList(3).ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptObjectProperty { Name = "BlightDiseases", Object = SpellList(2).ToLink<ISkyrimMajorRecordGetter>() },
                new ScriptFloatProperty { Name = "DispRaceMod", Data = Setting("fDispRaceMod", 5) },
                new ScriptFloatProperty { Name = "DispPersonalityMult", Data = Setting("fDispPersonalityMult", 0.5f) },
                new ScriptFloatProperty { Name = "DispPersonalityBase", Data = Setting("fDispPersonalityBase", 50) },
                new ScriptFloatProperty { Name = "DispFactionRankMult", Data = Setting("fDispFactionRankMult", 0.5f) },
                new ScriptFloatProperty { Name = "DispFactionRankBase", Data = Setting("fDispFactionRankBase", 1) },
                new ScriptFloatProperty { Name = "DispFactionMod", Data = Setting("fDispFactionMod", 3) },
                new ScriptFloatProperty { Name = "DispCrimeMod", Data = Setting("fDispCrimeMod", 0) },
                new ScriptFloatProperty { Name = "DispDiseaseMod", Data = Setting("fDispDiseaseMod", -10) },
                new ScriptFloatProperty { Name = "DispWeaponDrawn", Data = Setting("fDispWeaponDrawn", -5) },
            ],
        });
        // A creature the scripts ask a soul gem of (HasSoulGem, AddSoulGem...): its own gems, which trapping it fills.
        if (!person && scriptSet is not null && scriptSet.All.SelectMany(o => o.Properties).Any(p => p.Fill.Equals("creaturegems:" + rec.Id, StringComparison.OrdinalIgnoreCase)
                || p.Fill.StartsWith("creaturegem:" + rec.Id + "|", StringComparison.OrdinalIgnoreCase)))
        {
            var entry = npc.VirtualMachineAdapter.Scripts.Last(s => s.Name == "TMMwActorAI");
            entry.Properties.Add(new ScriptObjectProperty { Name = "CreatureGems", Object = CreatureGemList(rec.Id).ToLink<ISkyrimMajorRecordGetter>() });
            entry.Properties.Add(new ScriptObjectProperty { Name = "GenericGems", Object = GenericGemList().ToLink<ISkyrimMajorRecordGetter>() });
            SoulTracked++;
        }
        ActorAIs++;
    }

    /// <summary>Creatures whose soul gems are their own (the scripts ask for them).</summary>
    public int SoulTracked { get; private set; }

    /// <summary>People and creatures given Morrowind's AI settings (TMMwActorAI).</summary>
    public int ActorAIs { get; private set; }

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
        npc.Configuration.Level = new NpcLevel { Level = Level(level) };
        npc.Packages.Add(new FormLink<IPackageGetter>(PackageFor(WanderDistance(rec))));
        ActorAI(npc, rec, person: true);
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
        // Moving as Morrowind's people do (Skyrim/Animations/NpcAnimations: its animations for this set, by keyword), and
        // standing as tall as Morrowind's (its race's height on its skeleton, set on each placed reference: PersonScale).
        bool beast = world.Objects.TryGetValue(raceId, out var mwRace) && mwRace.Get("RADT") is { Data.Length: >= 140 } radt && (BitConverter.ToInt32(radt.Data, 136) & 0x02) != 0;
        var set = Skyrim.Animations.NpcAnimations.Morrowind.First(s => s.Female == female && s.Key == (beast ? (female ? "MorrowindBeastWomen" : "MorrowindBeastMen") : (female ? "MorrowindWomen" : "MorrowindMen")));
        npc.Keywords = [PersonKeyword(set.Keyword).ToLink<IKeywordGetter>()];
        if (raceId.Equals("Argonian", StringComparison.OrdinalIgnoreCase))
            npc.Keywords.Add(PersonKeyword(female ? "TMMwArgonianWoman" : "TMMwArgonianMan").ToLink<IKeywordGetter>());
        if (mwRace?.Get("RADT") is { Data.Length: >= 128 } rd && PersonHeight(set, female ? BitConverter.ToSingle(rd.Data, 124) : BitConverter.ToSingle(rd.Data, 120), race, female, look) is float scale)
            personScale[npc.FormKey] = scale;
        return npc;
    }

    readonly Dictionary<string, Keyword> personKeywords = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<FormKey, float> personScale = [];

    /// <summary>A keyword marking Morrowind's people of one animation set (made once).</summary>
    Keyword PersonKeyword(string editorId)
    {
        if (personKeywords.TryGetValue(editorId, out var k)) return k;
        k = new Keyword(mod) { EditorID = editorId };
        mod.Keywords.Add(k);
        return personKeywords[editorId] = k;
    }

    /// <summary>How much a placed person is scaled to stand as tall as in Morrowind: the head's height on Morrowind's
    /// skeleton for the set times its race's height (Morrowind scales a person's height by its race's: OpenMW's
    /// Npc::adjustScale), over the head's height on Skyrim's skeleton times the Skyrim race's height and the borrowed
    /// look's own height (which Skyrim multiplies in). Null when a skeleton can't be read.</summary>
    float? PersonHeight(Skyrim.Animations.NpcAnimations.Set set, float mwRaceHeight, IRaceGetter race, bool female, INpcGetter look)
    {
        if (!headHeights.TryGetValue(set.Key, out var heights))
        {
            heights = null;
            try
            {
                var mw = Skyrim.Animations.NpcAnimations.MwRig.Load(data, set);
                var (mt, _) = mw.RestPose();
                int mh = mw.Index("Bip01 Head");
                var archives = new Skyrim.Creatures.CreatureBehavior(SkyrimData!);
                var sk = Skyrim.Animations.NpcAnimations.SkyrimRig.From(Skyrim.Animations.ClimbAnimation.ReadSkeleton(archives.ReadArchive(female
                    ? @"meshes\actors\character\character assets female\skeleton_female.hkx" : @"meshes\actors\character\character assets\skeleton.hkx")!));
                int sh = sk.Index("NPC Head [Head]");
                if (mh >= 0 && sh >= 0) heights = (mt[mh].Z, sk.WorldT[sh].Z);
            }
            catch (Exception e) { Problems.Add($"person height ({set.Key}): {e.Message}"); }
            headHeights[set.Key] = heights;
        }
        if (heights is not var (mwHead, skHead) || skHead <= 0 || mwRaceHeight <= 0) return null;
        float skRace = female ? race.Height.Female : race.Height.Male;
        float own = look.Height is > 0 ? look.Height : 1;
        return mwHead * mwRaceHeight / (skHead * (skRace > 0 ? skRace : 1) * own);
    }

    readonly Dictionary<string, (float Mw, float Sk)?> headHeights = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A placed actor's scale: its reference's own, a person's times its height (<see cref="PersonHeight"/>).</summary>
    float? PlacedScale(IMajorRecord actor, float refScale)
    {
        float s = refScale * (personScale.TryGetValue(actor.FormKey, out var p) ? p : 1);
        return MathF.Abs(s - 1) < 1e-4f ? null : s;
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
        ActorAI(npc, rec, person: false);
        var npdt = rec.Get("NPDT");
        int level = npdt is { } d ? d.Int(4) : 1;
        // Morrowind's level, not the stand-in's (a vanilla creature's own level or multiplier).
        npc.Configuration.Level = new NpcLevel { Level = Level(level) };
        // Morrowind's own wander (AI_W), as people's: the stand-in's packages were often none, and a creature without one
        // stands where it was placed (2026-10-21, creature-check: 861 of Morrowind's 2,248 placed creatures).
        npc.Packages.Clear();
        npc.Packages.Add(new FormLink<IPackageGetter>(PackageFor(WanderDistance(rec))));
        npc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.AIPackages;
        // Morrowind's flags, not the stand-in's: respawning (FLAG 0x02) and essential (0x80) as Morrowind has them; its numbers
        // are its own, not calculated from the stand-in's class.
        int mwFlags = rec.Get("FLAG")?.Int() ?? 0;
        npc.Configuration.Flags &= ~(NpcConfiguration.Flag.Respawn | NpcConfiguration.Flag.Essential);
        if ((mwFlags & 0x02) != 0) npc.Configuration.Flags |= NpcConfiguration.Flag.Respawn;
        if ((mwFlags & 0x80) != 0) npc.Configuration.Flags |= NpcConfiguration.Flag.Essential;
        // Morrowind's loot and spells only, not the stand-in's (a cliff racer dropped the ice wraith's teeth and essence).
        npc.DeathItem.Clear();
        npc.Items = null;
        npc.ActorEffect = null;
        npc.Configuration.TemplateFlags &= ~(NpcConfiguration.TemplateFlag.Inventory | NpcConfiguration.TemplateFlag.SpellList);
        AddInventory(npc, rec);
        AddSpells(npc, rec);
        return npc;
    }

    /// <summary>Morrowind's levels on Skyrim's scale (x1.5, at most 81): its actors' levels and the player levels its
    /// leveled lists (creatures and items) give their entries at, the same scale, so a list offers at a Skyrim level
    /// what Morrowind offered at the Morrowind level it stands for.</summary>
    internal static short Level(int morrowindLevel) => (short)Math.Clamp((int)MathF.Round(morrowindLevel * 1.5f), 1, 81);

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
                    list.Entries.Add(new LeveledNpcEntry { Data = new LeveledNpcEntryData { Level = Level(BitConverter.ToUInt16(f.Data, 0)), Count = 1, Reference = new FormLink<INpcSpawnGetter>(member.FormKey) } });
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
