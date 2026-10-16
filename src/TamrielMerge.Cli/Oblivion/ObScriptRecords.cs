using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// The records Oblivion's scripts need beyond the converted world, made when the scripts are attached
/// (<see cref="AttachScripts"/>):
/// <list type="bullet">
/// <item>TMObState, a quest keeping what Oblivion's engine kept for its scripts (crimes, animations, misc stats...), with
/// the player's alias (TMObPlayer) and one alias per script package use (AddScriptPackage puts the actor in it);</item>
/// <item>Oblivion's AI packages the scripts name, as Skyrim packages from vanilla ones of the same kind (Travel,
/// Sandbox, Follow, Escort, Sleep, Eat, Flee, Hold position) aimed where Oblivion's were;</item>
/// <item>sounds (Oblivion's own files), effect shaders and weathers (vanilla ones that look the part), races,
/// markers for PositionCell/PositionWorld, the birthsign menu.</item>
/// </list>
/// </summary>
public sealed partial class ObPluginBuilder
{
    readonly Dictionary<FormKey, Package?> obPackages = new();
    readonly Dictionary<FormKey, int> obPackageTypes = new();
    readonly Dictionary<FormKey, SoundMarker?> obSounds = new();
    readonly Dictionary<string, PlacedObject> anchors = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, FormList> effectLists = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, MagicEffect> scriptEffects = new(StringComparer.OrdinalIgnoreCase);
    Quest? stateQuest;
    public int PackagesMade => obPackages.Values.Count(p => p is not null);
    public int SoundsMade => obSounds.Values.Count(s => s is not null);

    // ---- packages ----

    /// <summary>The vanilla package a kind of Oblivion package is made from (one aimed at a reference or place).</summary>
    static string PackageTemplate(int type) => type switch
    {
        1 or 7 => "DA02FighterFollowPlayer",   // Follow, Accompany
        2 => "MG01NiryaTravel1",               // Escort
        3 => "EmbryEat7x1",                    // Eat
        4 => "EmbrySleep0x7",                  // Sleep
        5 => "DorthePlay",                     // Wander: sandbox around the place
        9 => "MG08AncanoHoldPosition",         // Ambush: hold the spot
        10 => "DB08AntonFlee",                 // FleeNotCombat
        _ => "MS14LamiWait",                   // Find, Travel, UseItemAt, CastMagic: go there
    };

    /// <summary>An Oblivion AI package as a Skyrim package (made on first use).</summary>
    Package? ObPackage(FormKey key)
    {
        if (obPackages.TryGetValue(key, out var hit)) return hit;
        obPackages[key] = null;
        if (records.GetValueOrDefault(key) is not Ob.IAIPackageGetter source) return null;
        int type = source.Data is { } d ? (int)d.Type : 6;
        obPackageTypes[key] = type;
        var template = SkyrimEsm.Packages.FirstOrDefault(p => p.EditorID == PackageTemplate(type))
            ?? (type is 1 or 7 ? SkyrimEsm.Packages.FirstOrDefault(p => p.EditorID?.Contains("Follow") == true && p.Data.Values.OfType<IPackageDataTargetGetter>().Any(t => t.Target is IPackageTargetSpecificReferenceGetter)) : null)
            ?? SkyrimEsm.Packages.First(p => p.EditorID == "MS14LamiWait");
        var package = mod.Packages.DuplicateInAsNewRecord(template);
        package.EditorID = "OBPack" + Sanitize(source.EditorID ?? key.ID.ToString("X6"));
        package.Conditions.Clear();
        package.VirtualMachineAdapter = null;
        var place = PackagePlace(source);
        var target = PackageTarget(source);
        foreach (var data in package.Data.Values)
        {
            if (data is PackageDataLocation loc && place is not null) loc.Location = place;
            else if (data is PackageDataTarget t && target is not null) t.Target = target;
        }
        return obPackages[key] = package;
    }

    /// <summary>Where an Oblivion package takes place (near a reference, in a cell, near where the actor is or was placed).</summary>
    LocationTargetRadius? PackagePlace(Ob.IAIPackageGetter p)
    {
        if (p.Location is not { } l) return null;
        float radius = Math.Max(l.Radius, 64);
        ALocationTarget where = l.Type switch
        {
            Ob.AIPackageLocation.LocationType.NearReference when l.LocationReference.FormKeyNullable is { } r && placedBySource.TryGetValue(r, out var placed) =>
                new LocationTarget { Link = new FormLink<IPlacedGetter>(placed.FormKey) },
            Ob.AIPackageLocation.LocationType.InCell when l.LocationReference.FormKeyNullable is { } c && interiorsBySource.TryGetValue(c, out var cell) =>
                new LocationCell { Link = new FormLink<ICellGetter>(cell.FormKey) },
            Ob.AIPackageLocation.LocationType.NearCurrentLocation => new LocationFallback { Type = LocationTargetRadius.LocationType.NearPackageStart },
            Ob.AIPackageLocation.LocationType.ObjectID when l.LocationReference.FormKeyNullable is { } o && (Item(o) ?? Base(o)) is { } obj =>
                new LocationObjectId { Link = new FormLink<IObjectIdGetter>(obj.FormKey) },
            _ => new LocationFallback { Type = LocationTargetRadius.LocationType.NearEditorLocation },
        };
        return new LocationTargetRadius { Target = where, Radius = (uint)radius };
    }

    /// <summary>Whom or what an Oblivion package is about (the one to follow, escort, flee from...).</summary>
    APackageTarget? PackageTarget(Ob.IAIPackageGetter p)
    {
        if (p.Target is not { } t || t.Object == 0) return null;
        if ((uint)t.Object == 0x14) return new PackageTargetSpecificReference { Reference = new FormLink<IPlacedGetter>(PlayerRefKey) };
        var key = new FormKey(p.FormKey.ModKey, (uint)t.Object & 0xFFFFFF);
        if (placedBySource.TryGetValue(key, out var placed))
            return new PackageTargetSpecificReference { Reference = new FormLink<IPlacedGetter>(placed.FormKey), CountOrDistance = t.Count };
        if ((Item(key) ?? Base(key) ?? Actor(key)) is { } obj)
            return new PackageTargetObjectID { Reference = new FormLink<IObjectIdGetter>(obj.FormKey), CountOrDistance = t.Count };
        return null;
    }

    /// <summary>The references an Oblivion package names (placed persistent, so the package can point at them).</summary>
    static IEnumerable<FormKey> PackageRefs(Ob.IAIPackageGetter p)
    {
        if (p.Location?.LocationReference.FormKeyNullable is { } l) yield return l;
        if (p.Target is { } t && (uint)t.Object is var raw and not 0) yield return new FormKey(p.FormKey.ModKey, raw & 0xFFFFFF);
    }

    // ---- sounds ----

    /// <summary>An Oblivion sound as a Skyrim sound (its own files, copied), made on first use.</summary>
    SoundMarker? ObSound(FormKey key)
    {
        if (obSounds.TryGetValue(key, out var hit)) return hit;
        obSounds[key] = null;
        if (records.GetValueOrDefault(key) is not Ob.ISoundGetter s || s.File is not { Length: > 0 } file) return null;
        string source = ObData.Normalize(@"sound\" + file);
        var files = new List<string>();
        if (Path.HasExtension(source) && data.Exists(source)) files.Add(source);
        else
        {
            // A folder: Oblivion picks one of its files at random, as Skyrim does among a descriptor's files.
            string folder = source.TrimEnd('\\') + @"\";
            files.AddRange(data.List(folder).Where(f => !f[folder.Length..].Contains('\\')));
        }
        files = files.Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) { Problems.Add($"script sound {s.EditorID}: {file} not found (or not a wav)"); return null; }
        var descriptor = mod.SoundDescriptors.DuplicateInAsNewRecord(SkyrimEsm.SoundDescriptors.First(d => d.EditorID == "QSTTG01UrnSmashSD"));
        descriptor.EditorID = "OBSound" + Sanitize(s.EditorID ?? key.ID.ToString("X6")) + "SD";
        descriptor.SoundFiles.Clear();
        foreach (var f in files)
        {
            var bytes = data.Read(f);
            if (bytes is null) continue;
            string rel = Path.Combine("fx", "TamrielMerge", "OB", f[6..]);
            string target = Path.Combine(modOutDir, "Sound", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            descriptor.SoundFiles.Add(new Mutagen.Bethesda.Plugins.Assets.AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType>(rel));
        }
        var marker = new SoundMarker(mod) { EditorID = "OBSound" + Sanitize(s.EditorID ?? key.ID.ToString("X6")), ObjectBounds = new ObjectBounds(), SoundDescriptor = descriptor.ToNullableLink<ISoundDescriptorGetter>() };
        mod.SoundMarkers.Add(marker);
        return obSounds[key] = marker;
    }

    // ---- look-alikes ----

    /// <summary>The vanilla effect shader that looks most like an Oblivion one (by its name) or a magic effect (by code).</summary>
    FormKey? ShaderStandIn(string name)
    {
        string n = name.ToLowerInvariant();
        string pick =
            n.Contains("fire") || n.Contains("flame") || n is "fidg" or "fish" ? "FireFXShader"
            : n.Contains("frost") || n.Contains("ice") || n.Contains("cold") || n is "frdg" or "frsh" ? "FrostFXShader"
            : n.Contains("shock") || n.Contains("lightning") || n.Contains("storm") || n is "shdg" or "lish" ? "ShockFXShader"
            : n.Contains("ghost") || n.Contains("spirit") || n.Contains("dead") || n is "zgho" ? "GhostFXShader"
            : n.Contains("invis") || n.Contains("chameleon") || n is "invi" or "chml" ? "InvisFXShader"
            : n.Contains("soul") || n is "strp" ? "SoulTrapFXShader"
            : n.Contains("para") || n is "para" ? "ParalyzeFxShader"
            : n.Contains("reanim") || n.Contains("summon") || n.Contains("conjur") || n is "rean" || n.StartsWith('z') && n.Length == 4 ? "ReanimateFXShader"
            : n.Contains("shield") || n.Contains("ward") || n.Contains("reflect") || n.Contains("absorption") || n is "shld" or "rflc" or "sabs" ? "ShieldSpellFXS"
            : n.Contains("heal") || n.Contains("restor") || n.Contains("cure") || n.StartsWith("re") && n.Length == 4 || n.StartsWith("fo") && n.Length == 4 || n.StartsWith("cu") && n.Length == 4 ? "HealFXS"
            : n.Contains("absorb") || n.StartsWith("ab") && n.Length == 4 ? "AbsorbHealthFXS"
            : n.Contains("drain") || n.Contains("damage") || n.StartsWith("dr") && n.Length == 4 || n.StartsWith("dg") && n.Length == 4 ? "DamageHealthFXS"
            : n.Contains("poison") ? "ChaurusPoisonFXShader"
            : n.Contains("turn") || n.Contains("undead") || n is "turn" ? "TurnUndeadFlamesFXS"
            : n.Contains("bound") || (n.StartsWith("bw") || n.StartsWith("ba")) && n.Length == 4 || n is "myth" or "myhl" ? "EnchBoundSwordFXS"
            : n.Contains("calm") || n.Contains("charm") || n.Contains("rally") || n is "calm" or "chrm" or "raly" or "cohu" or "cocr" ? "IllusionPositiveFXS"
            : n.Contains("fear") || n.Contains("frenzy") || n.Contains("silence") || n is "frnz" or "demo" or "slnc" ? "IllusionNegativeFXS"
            : n.Contains("water") || n is "wabr" or "wawa" ? "WaterBreathingFXS"
            : n.Contains("detect") || n.Contains("life") || n is "dtct" ? "LifeDetected"
            : n.Contains("shadow") || n.Contains("dark") || n.Contains("night") || n.Contains("order") ? "NightmareFXShader"
            : n.Contains("fade") ? "TimeFadeOut01FXS"
            : n.Contains("green") ? "EnchGreenFXShader"
            : n.Contains("red") || n.Contains("blood") ? "EnchRedFXShader"
            : n.Contains("blue") || n.Contains("light") || n is "lght" or "neye" ? "EnchBlueFXShader"
            : n.Contains("violet") || n.Contains("purple") ? "GhostVioletFXShader"
            : "EnchPurpleFXShader";
        return Vanilla.Get<IEffectShaderGetter>(pick)?.FormKey;
    }

    /// <summary>The vanilla weather most like an Oblivion one (by name): Oblivion's red skies are a storm.</summary>
    FormKey? WeatherStandIn(string name)
    {
        string n = name.ToLowerInvariant();
        string pick =
            n.Contains("oblivion") || n.Contains("thunder") || n.Contains("storm") ? "SkyrimStormRain"
            : n.Contains("snow") ? "SkyrimOvercastSnow"
            : n.Contains("rain") ? "SkyrimOvercastRain"
            : n.Contains("whiteout") || n.Contains("fog") || n.Contains("mist") ? "SkyrimFog"
            : n.Contains("cloud") || n.Contains("overcast") || n.Contains("gloom") ? "SkyrimCloudy"
            : "SkyrimClear";
        return Vanilla.Get<IWeatherGetter>(pick)?.FormKey;
    }

    FormKey? CombatStyleStandIn(string name)
    {
        string n = name.ToLowerInvariant();
        string pick = n.Contains("archer") || n.Contains("missile") || n.Contains("marks") ? "csHumanMissile"
            : n.Contains("mage") || n.Contains("magic") || n.Contains("spell") ? "csHumanMagic"
            : "csHumanMeleeLvl1";
        return Vanilla.Get<ICombatStyleGetter>(pick)?.FormKey;
    }

    /// <summary>A persistent marker in an interior or worldspace, for moving there to coordinates (PositionCell).</summary>
    FormKey? Anchor(string cellOrWorld)
    {
        if (anchors.TryGetValue(cellOrWorld, out var hit)) return hit.FormKey;
        if (scriptWorld?.KeyOf(cellOrWorld) is not { } key) return null;
        Cell? cell = interiorsBySource.GetValueOrDefault(key) ?? (worldspaces.TryGetValue(key, out var ws) ? ws.TopCell : null);
        if (cell is null) return null;
        var marker = new PlacedObject(mod)
        {
            EditorID = "OBAnchor" + Sanitize(cellOrWorld),
            Base = new FormLinkNullable<IPlaceableObjectGetter>(Skyrim.TownNights.XMarker),
            Placement = new Placement { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        cell.Persistent.Add(marker);
        anchors[cellOrWorld] = marker;
        return marker.FormKey;
    }

    /// <summary>The Skyrim magic effects an Oblivion effect code became (for HasMagicEffect), in a list.</summary>
    FormKey EffectList(string code)
    {
        if (effectLists.TryGetValue(code, out var hit)) return hit.FormKey;
        var list = new FormList(mod) { EditorID = "OBEffects" + Sanitize(code) };
        string? name = code.Equals("SEFF", StringComparison.OrdinalIgnoreCase) ? "Script" : Skyrim.TesMagic.OblivionName(code.ToUpperInvariant());
        if (name is not null)
            foreach (var m in mod.MagicEffects.Where(m => m.EditorID?.StartsWith("OBEffect" + name, StringComparison.OrdinalIgnoreCase) == true))
                list.Items.Add(m.ToLink<ISkyrimMajorRecordGetter>());
        mod.FormLists.Add(list);
        effectLists[code] = list;
        return list.FormKey;
    }

    /// <summary>A generated record a script property names by a made-up EditorID (@anchor:, @effects:, @mod:...).</summary>
    FormKey? Special(ObPapyrus.Property p)
    {
        string id = p.EditorID;
        int colon = id.IndexOf(':');
        string kind = id[..colon], arg = id[(colon + 1)..];
        switch (kind)
        {
            case "@anchor": return Anchor(arg);
            case "@effects": return EffectList(arg);
            case "@effectshader":
                // The magic effect's own shader (as its look-alike), else one for the effect itself.
                if (scriptWorld!.MagicEffects.TryGetValue(arg, out var mgef) && mgef.Data?.EffectShader.FormKeyNullable is { } efsh && records.GetValueOrDefault(efsh)?.EditorID is { } efshId)
                    return ShaderStandIn(efshId) ?? ShaderStandIn(arg);
                return ShaderStandIn(arg);
            case "@mod" when arg == "OBGuards": return Skyrim.TownNights.Guards(mod, "OB").FormKey;
            case "@ref": return scriptWorld!.KeyOf(id) is { } rk && placedBySource.TryGetValue(rk, out var placed) ? placed.FormKey : null;
        }
        return null;
    }

    // ---- the state quest ----

    /// <summary>TMObState: the quest with the state, the player's alias and the script package aliases.</summary>
    Quest StateQuest(Func<ObPapyrus.Property, FormKey?> fill)
    {
        if (stateQuest is not null) return stateQuest;
        var quest = stateQuest = new Quest(mod)
        {
            EditorID = "TMObState",
            Name = "Oblivion's engine state",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 90,
            Type = Quest.TypeEnum.None,
        };
        mod.Quests.Add(quest);
        var player = new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x14);
        quest.Aliases.Add(new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(player) });
        // One alias per use of each script package (as many as the scripts add it, up to 4): AddScriptPackage puts the
        // actor in a free one.
        var slots = new List<(uint Alias, Package Package)>();
        uint next = 1;
        foreach (var (pkgKey, uses) in scriptPackageUses.OrderBy(kv => kv.Key.ID))
        {
            if (ObPackage(pkgKey) is not { } package) continue;
            for (int i = 0; i < Math.Clamp(uses, 1, 4) && slots.Count < 120; i++)
            {
                quest.Aliases.Add(new QuestAlias
                {
                    ID = next,
                    Name = $"ScriptPackage{next}",
                    Flags = QuestAlias.Flag.Optional | QuestAlias.Flag.AllowReuseInQuest | QuestAlias.Flag.AllowReserved,
                    PackageData = [package.ToLink<IPackageGetter>()],
                });
                slots.Add((next, package));
                next++;
            }
        }
        FormList NewList(string edid)
        {
            var l = new FormList(mod) { EditorID = edid };
            mod.FormLists.Add(l);
            return l;
        }
        ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };
        var entry = new ScriptEntry { Name = "TMObState" };
        entry.Properties.Add(Obj("PlayerRef", player));
        if (fill(new ObPapyrus.Property("PCCrimeGold", "GlobalVariable", "TMOB_PCCrimeGold", Generated: true)) is { } crimeGold) entry.Properties.Add(Obj("PCCrimeGold", crimeGold));
        if (fill(new ObPapyrus.Property("PCAmountSoldStolen", "GlobalVariable", "TMOB_PCAmountSoldStolen", Generated: true)) is { } sold) entry.Properties.Add(Obj("PCAmountSoldStolen", sold));
        foreach (var name in new[] { "TalkedToPC", "DestroyedRefs", "RestrainedActors", "NoRumors", "StolenItems" })
            entry.Properties.Add(Obj(name, NewList("TMOb" + name).FormKey));
        var crimeFactions = new ScriptObjectListProperty { Name = "CrimeFactions" };
        foreach (var f in translations.Values.SelectMany(o => o.Properties).Where(p => p.Name.StartsWith("cf_", StringComparison.Ordinal)).Select(p => p.EditorID).Distinct(StringComparer.OrdinalIgnoreCase).Take(128))
            if (scriptWorld!.KeyOf(f) is { } fk && ObFaction(fk) is { } faction) crimeFactions.Objects.Add(new ScriptObjectProperty { Object = faction.ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(crimeFactions);
        // The lands' crime factions and jails (ObCrime): the bounty Skyrim keeps, and IsPlayerInJail.
        entry.Properties.Add(Obj("CyrodiilCrime", LandCrime(false).FormKey));
        entry.Properties.Add(Obj("IslesCrime", LandCrime(true).FormKey));
        entry.Properties.Add(Obj("PCInSEWorld", ScriptGlobal("TMOB_PCInSEWorld").FormKey));
        entry.Properties.Add(Obj("PCInJail", ScriptGlobal("TMOB_PCInJail").FormKey));
        entry.Properties.Add(Obj("JailMarkers", JailMarkers().FormKey));
        entry.Properties.Add(new ScriptObjectListProperty { Name = "PackageSlots", Objects = [.. slots.Select(s => new ScriptObjectProperty { Object = quest.ToLink<ISkyrimMajorRecordGetter>(), Alias = (short)s.Alias })] });
        entry.Properties.Add(new ScriptObjectListProperty { Name = "SlotPackages", Objects = [.. slots.Select(s => new ScriptObjectProperty { Object = s.Package.ToLink<ISkyrimMajorRecordGetter>() })] });
        var made = obPackages.Where(kv => kv.Value is not null).Take(128).ToList();
        entry.Properties.Add(new ScriptObjectListProperty { Name = "Packages", Objects = [.. made.Select(kv => new ScriptObjectProperty { Object = kv.Value!.ToLink<ISkyrimMajorRecordGetter>() })] });
        entry.Properties.Add(new ScriptIntListProperty { Name = "PackageTypes", Data = [.. made.Select(kv => obPackageTypes.GetValueOrDefault(kv.Key, 6))] });
        AddBirthsigns(entry);
        quest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts = { entry },
            Aliases =
            [
                new QuestFragmentAlias
                {
                    Property = new ScriptObjectProperty { Object = quest.ToLink<ISkyrimMajorRecordGetter>(), Alias = 0 },
                    Scripts = [new ScriptEntry { Name = "TMObPlayer", Properties = [Obj("ObState", quest.FormKey), Obj("PlayerRef", player)] }],
                },
            ],
        };
        return quest;
    }

    /// <summary>Script packages and how many times the scripts add each (for the aliases that run them).</summary>
    readonly Dictionary<FormKey, int> scriptPackageUses = new();

    /// <summary>The birthsign menu (two pages of buttons past nine signs) and each sign's spells.</summary>
    void AddBirthsigns(ScriptEntry entry)
    {
        var signs = scriptWorld!.Birthsigns.Select(b => scriptWorld.KeyOf(b) is { } k ? records.GetValueOrDefault(k) as Ob.IBirthsignGetter : null).ToList();
        if (signs.Count == 0 || signs.Any(s => s is null)) return;
        Message Page(string edid, IEnumerable<string> buttons)
        {
            var m = new Message(mod) { EditorID = edid, Description = "Under which sign were you born?", Flags = Message.Flag.MessageBox, MenuButtons = [] };
            foreach (var b in buttons) m.MenuButtons.Add(new MessageButton { Text = b });
            mod.Messages.Add(m);
            return m;
        }
        var names = signs.Select(s => s!.Name ?? s.EditorID ?? "?").ToList();
        if (names.Count <= 10)
            entry.Properties.Add(new ScriptObjectProperty { Name = "BirthsignPage1", Object = Page("TMObBirthsigns", names).ToLink<ISkyrimMajorRecordGetter>() });
        else
        {
            entry.Properties.Add(new ScriptObjectProperty { Name = "BirthsignPage1", Object = Page("TMObBirthsigns1", names.Take(9).Append("More...")).ToLink<ISkyrimMajorRecordGetter>() });
            entry.Properties.Add(new ScriptObjectProperty { Name = "BirthsignPage2", Object = Page("TMObBirthsigns2", names.Skip(9).Take(9).Append("Back")).ToLink<ISkyrimMajorRecordGetter>() });
        }
        var lists = new ScriptObjectListProperty { Name = "BirthsignSpells" };
        foreach (var s in signs)
        {
            var list = new FormList(mod) { EditorID = "TMObBirthsign" + Sanitize(s!.EditorID ?? "") };
            foreach (var spell in s.Spells)
                if (ObSpell(spell.FormKey) is { } converted) list.Items.Add(converted.ToLink<ISkyrimMajorRecordGetter>());
            mod.FormLists.Add(list);
            lists.Objects.Add(new ScriptObjectProperty { Object = list.ToLink<ISkyrimMajorRecordGetter>() });
        }
        entry.Properties.Add(lists);
    }
}
