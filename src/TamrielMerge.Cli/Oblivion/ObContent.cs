using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's items, containers, people and creatures as Skyrim records, the way Morrowind's are converted: each
/// item based on a similar vanilla record (Iron or Leather gear, farm clothes, iron weapons...) with Oblivion's name,
/// model, value, weight and stats, and dynamic physics for loose items; NPCs with a vanilla face of their race and
/// sex (template traits) and Oblivion's name, level, inventory and flags; creatures copied from the closest vanilla
/// or Dragonborn creature (<see cref="CreatureTable"/>).
/// </summary>
public sealed partial class ObPluginBuilder
{
    VanillaActors? vanillaActors;
    VanillaActors Vanilla => vanillaActors ??= new VanillaActors(SkyrimData ?? throw new InvalidOperationException("SkyrimData not set"));
    readonly Dictionary<FormKey, IMajorRecord?> items = new(), actors = new();
    static readonly FormKey ObGold = new(ModKey.FromFileName("Oblivion.esm"), 0x00000F);
    static readonly FormKey SkyrimGold = new(ModKey.FromFileName("Skyrim.esm"), 0x00000F);

    static bool IsItem(Ob.IOblivionMajorRecordGetter r) => r is Ob.IWeaponGetter or Ob.IArmorGetter or Ob.IClothingGetter or Ob.IBookGetter or Ob.IPotionGetter
        or Ob.IIngredientGetter or Ob.IMiscellaneousGetter or Ob.IKeyGetter or Ob.IAmmunitionGetter or Ob.ISoulGemGetter or Ob.ISigilStoneGetter or Ob.ILeveledItemGetter
        or Ob.IAlchemicalApparatusGetter;

    ObMeshConverter.Result? ItemMesh(string? model, float weight)
    {
        if (model is not { Length: > 0 }) return null;
        try { return meshes.Convert(model, modOutDir, itemMass: Math.Clamp(weight, 0.3f, 40f)); }
        catch (Exception e) { Problems.Add($"item model {model}: {e.Message}"); return null; }
    }

    void SetModel(Model? target, ObMeshConverter.Result? mesh, Action<Model> set) { if (mesh is not null) set(new Model { File = SkyrimModelPath(mesh.Path) }); }

    /// <summary>The Skyrim item for an Oblivion item (created on first use), or null.</summary>
    IMajorRecord? Item(FormKey key)
    {
        if (items.TryGetValue(key, out var hit)) return hit;
        // Oblivion's lockpick is Skyrim's, the one Skyrim's lockpicking takes (ObLocks); a stand-in carrying its key.
        if (key == ObLockpick) return items[key] = new MiscItem(SkyrimLockpick, SkyrimRelease.SkyrimSE);
        // Oblivion's gold is Skyrim's, the money barter, dialogue and scripts count (septims while the player is in
        // Oblivion's time, TMStoryOrder).
        if (key == ObGold) return items[key] = new MiscItem(SkyrimGold, SkyrimRelease.SkyrimSE);
        items[key] = null;
        if (!records.TryGetValue(key, out var rec) || !IsItem(rec)) return null;
        string edid = "OB" + Sanitize(rec.EditorID ?? key.ID.ToString("X6"));
        try
        {
            var made = items[key] = rec switch
            {
                Ob.IMiscellaneousGetter m => Misc(edid, m.Name, m.Model?.File, m.Data?.Weight ?? 0, (uint)Math.Max(0, m.Data?.Value ?? 0)),
                Ob.ISigilStoneGetter s => Misc(edid, s.Name, s.Model?.File, 0, s.Data?.Value ?? 0),
                Ob.IKeyGetter k => KeyItem(edid, k),
                // Skyrim's potions and soul gems carry no scripts: one with an object script (a quest's wine, a
                // collector's soul gem) is a misc item, which can.
                Ob.ISoulGemGetter g when ObScriptWorld.ScriptLink(g) is not null => Misc(edid, g.Name, g.Model?.File, g.Data?.Weight ?? 0, g.Data?.Value ?? 0),
                Ob.IPotionGetter p when ObScriptWorld.ScriptLink(p) is not null => Misc(edid, p.Name, p.Model?.File, p.Weight ?? 0, p.Data?.Value ?? 0),
                // Oblivion's apparatus (mortars, alembics...) are Skyrim's clutter.
                Ob.IAlchemicalApparatusGetter a => Misc(edid, a.Name, a.Model?.File, a.Data?.Weight ?? 0, a.Data?.Value ?? 0),
                Ob.ISoulGemGetter g => SoulGemItem(edid, g),
                Ob.IBookGetter b => ScrollItem(edid, b) ?? BookItem(edid, b),
                Ob.IPotionGetter p => PotionItem(edid, p),
                Ob.IIngredientGetter i => IngredientItem(edid, i),
                Ob.IWeaponGetter w => WeaponItem(edid, w),
                Ob.IAmmunitionGetter a => AmmoItem(edid, a),
                Ob.IArmorGetter a => ArmorItem(edid, a),
                Ob.IClothingGetter c => ClothingItem(edid, c),
                Ob.ILeveledItemGetter l => LeveledList(edid, l),
                _ => null,
            };
            Enchant(rec, made);
            return made;
        }
        catch (Exception e) { Problems.Add($"item {rec.EditorID}: {e.Message}"); return null; }
    }

    MiscItem Misc(string edid, string? name, string? model, float weight, uint value)
    {
        var mesh = ItemMesh(model, weight);
        var misc = new MiscItem(mod) { EditorID = edid, Name = name, Value = value, Weight = weight, ObjectBounds = Bounds(mesh) };
        if (mesh is not null) misc.Model = new Model { File = SkyrimModelPath(mesh.Path) };
        mod.MiscItems.Add(misc);
        return misc;
    }

    Key KeyItem(string edid, Ob.IKeyGetter k)
    {
        var mesh = ItemMesh(k.Model?.File, k.Data?.Weight ?? 0);
        var key = new Key(mod) { EditorID = edid, Name = k.Name, Value = k.Data?.Value ?? 0, Weight = k.Data?.Weight ?? 0, ObjectBounds = Bounds(mesh) };
        if (mesh is not null) key.Model = new Model { File = SkyrimModelPath(mesh.Path) };
        mod.Keys.Add(key);
        return key;
    }

    IMajorRecord SoulGemItem(string edid, Ob.ISoulGemGetter g)
    {
        string template = g.MaximumCapacity switch
        {
            Ob.SoulLevel.Petty => "SoulGemPetty", Ob.SoulLevel.Lesser => "SoulGemLesser", Ob.SoulLevel.Common => "SoulGemCommon",
            Ob.SoulLevel.Greater => "SoulGemGreater", _ => "SoulGemGrand",
        };
        var gem = mod.SoulGems.DuplicateInAsNewRecord(Vanilla.Get<ISoulGemGetter>(template)!);
        gem.EditorID = edid;
        gem.Name = g.Name;
        gem.Value = g.Data?.Value ?? gem.Value;
        gem.Weight = g.Data?.Weight ?? gem.Weight;
        if (ItemMesh(g.Model?.File, gem.Weight) is { } mesh) { gem.Model = new Model { File = SkyrimModelPath(mesh.Path) }; gem.ObjectBounds = Bounds(mesh); }
        return gem;
    }

    Book BookItem(string edid, Ob.IBookGetter b)
    {
        var mesh = ItemMesh(b.Model?.File, b.Data?.Weight ?? 0);
        var book = new Book(mod)
        {
            EditorID = edid,
            Name = b.Name,
            BookText = VanillaActors.BookText(b.Description ?? ""),
            Value = (uint)Math.Max(0, b.Data?.Value ?? 0),
            Weight = b.Data?.Weight ?? 0,
            Type = b.Data?.Flags.HasFlag(Ob.Book.BookFlag.Scroll) == true ? Book.BookType.NoteOrScroll : Book.BookType.BookOrTome,
            Teaches = new BookTeachesNothing(),
            ObjectBounds = Bounds(mesh),
        };
        if (mesh is not null) book.Model = new Model { File = SkyrimModelPath(mesh.Path) };
        mod.Books.Add(book);
        return book;
    }

    /// <summary>Oblivion magic effect code -> the closest Skyrim alchemy effect.</summary>
    static readonly Dictionary<string, string> AlchemyEffects = new()
    {
        ["REHE"] = "AlchRestoreHealth", ["REMA"] = "AlchRestoreMagicka", ["REFA"] = "AlchRestoreStamina",
        ["FOHE"] = "AlchFortifyHealth", ["FOMA"] = "AlchFortifyMagicka", ["FOFA"] = "AlchFortifyStamina",
        ["RSFI"] = "AlchResistFire", ["RSFR"] = "AlchResistFrost", ["RSSH"] = "AlchResistShock", ["RSMA"] = "AlchResistMagic",
        ["RSPO"] = "AlchResistPoison", ["CUDI"] = "AlchCureDisease", ["WABR"] = "AlchWaterbreathing", ["INVI"] = "AlchInvisibility",
        ["CHML"] = "AlchInvisibility", ["DGHE"] = "AlchDamageHealth", ["DGFA"] = "AlchDamageStamina", ["DGMA"] = "AlchDamageMagicka",
        ["PARA"] = "AlchParalysis", ["FEAT"] = "AlchFortifyCarryWeight", ["DRHE"] = "AlchDamageHealth", ["FIDG"] = "AlchDamageHealth",
        ["FRDG"] = "AlchDamageHealth", ["SHDG"] = "AlchDamageHealth",
    };

    void AddEffects(IList<Effect> target, IEnumerable<Ob.IEffectGetter> source)
    {
        foreach (var e in source)
        {
            if (e.Data is not { } d) continue;
            string code = d.MagicEffect.EDID.Type;
            // A scripted effect: the converted script runs when it's drunk or eaten.
            if (ScriptEffectOf(e) is { } scripted && Magic.EffectOf(scripted, TesCast.CastOnce) is { } scriptMgef)
            {
                target.Add(new Effect { BaseEffect = scriptMgef.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData { Duration = (int)d.Duration } });
                continue;
            }
            if (!AlchemyEffects.TryGetValue(code, out var edid) || Vanilla.Get<IMagicEffectGetter>(edid) is not { } mgef) continue;
            target.Add(new Effect { BaseEffect = mgef.ToNullableLink(), Data = new EffectData { Magnitude = Math.Max(1, d.Magnitude), Duration = (int)d.Duration, Area = (int)d.Area } });
        }
        if (target.Count == 0 && Vanilla.Get<IMagicEffectGetter>("AlchRestoreHealth") is { } fallback)
            target.Add(new Effect { BaseEffect = fallback.ToNullableLink(), Data = new EffectData { Magnitude = 1 } });
    }

    Ingestible PotionItem(string edid, Ob.IPotionGetter p)
    {
        var mesh = ItemMesh(p.Model?.File, p.Weight ?? 0);
        var potion = new Ingestible(mod)
        {
            EditorID = edid, Name = p.Name, Weight = p.Weight ?? 0, Value = p.Data?.Value ?? 0, ObjectBounds = Bounds(mesh),
            ConsumeSound = new FormLink<ISoundDescriptorGetter>(Sk(0x0B6435)), Effects = [],
        };
        AddEffects(potion.Effects, p.Effects);
        if (mesh is not null) potion.Model = new Model { File = SkyrimModelPath(mesh.Path) };
        mod.Ingestibles.Add(potion);
        return potion;
    }

    Ingredient IngredientItem(string edid, Ob.IIngredientGetter i)
    {
        var mesh = ItemMesh(i.Model?.File, i.Weight ?? 0);
        var ingredient = new Ingredient(mod)
        {
            EditorID = edid, Name = i.Name, Weight = i.Weight ?? 0, Value = i.Data?.Value ?? 0, IngredientValue = (int)(i.Data?.Value ?? 0),
            ObjectBounds = Bounds(mesh), Effects = [],
        };
        AddEffects(ingredient.Effects, i.Effects);
        if (mesh is not null) ingredient.Model = new Model { File = SkyrimModelPath(mesh.Path) };
        mod.Ingredients.Add(ingredient);
        return ingredient;
    }

    readonly Dictionary<string, Static> firstPerson = new(StringComparer.OrdinalIgnoreCase);
    Static FirstPersonStatic(string modelPath)
    {
        if (firstPerson.TryGetValue(modelPath, out var hit)) return hit;
        var s = new Static(mod) { EditorID = "OB1st" + Sanitize(Path.GetFileNameWithoutExtension(modelPath)), ObjectBounds = new ObjectBounds(), Model = new Model { File = modelPath } };
        mod.Statics.Add(s);
        return firstPerson[modelPath] = s;
    }

    Weapon WeaponItem(string edid, Ob.IWeaponGetter w)
    {
        var d = w.Data!;
        string name = (w.Name ?? "").ToLowerInvariant();
        string template = d.Type switch
        {
            Ob.Weapon.WeaponType.BladeOneHand => name.Contains("dagger") ? "IronDagger" : "IronSword",
            Ob.Weapon.WeaponType.BladeTwoHand => "IronGreatsword",
            Ob.Weapon.WeaponType.BluntOneHand => name.Contains("axe") ? "IronWarAxe" : "IronMace",
            Ob.Weapon.WeaponType.BluntTwoHand => name.Contains("axe") ? "IronBattleaxe" : "IronWarhammer",
            Ob.Weapon.WeaponType.Staff => "StaffTemplateDestruction",
            _ => "LongBow",
        };
        var weapon = mod.Weapons.DuplicateInAsNewRecord(Vanilla.Get<IWeaponGetter>(template)!);
        weapon.EditorID = edid;
        weapon.Name = w.Name;
        weapon.ObjectEffect.Clear();
        weapon.BasicStats = new WeaponBasicStats { Value = d.Value, Weight = d.Weight, Damage = (ushort)Math.Clamp((int)d.Damage, 1, 200) };
        if (weapon.Data is { } wd) { if (d.Speed > 0) wd.Speed = d.Speed; if (d.Reach > 0) wd.Reach = d.Reach; }
        if (ItemMesh(w.Model?.File, d.Weight) is { } mesh)
        {
            string path = SkyrimModelPath(mesh.Path);
            weapon.Model = new Model { File = path };
            weapon.FirstPersonModel = FirstPersonStatic(path).ToNullableLink<IStaticGetter>();
            weapon.ObjectBounds = Bounds(mesh);
        }
        return weapon;
    }

    Ammunition AmmoItem(string edid, Ob.IAmmunitionGetter a)
    {
        var ammo = mod.Ammunitions.DuplicateInAsNewRecord(Vanilla.Get<IAmmunitionGetter>("IronArrow")!);
        ammo.EditorID = edid;
        ammo.Name = a.Name;
        ammo.Value = a.Data?.Value ?? 0;
        ammo.Weight = a.Data?.Weight ?? 0;
        ammo.Damage = Math.Max(1f, (float)(a.Data?.Damage ?? 1));
        if (ItemMesh(a.Model?.File, ammo.Weight) is { } mesh) ammo.Model = new Model { File = SkyrimModelPath(mesh.Path) };
        return ammo;
    }

    /// <summary>The vanilla template an Oblivion biped part's record is based on (keywords, sounds, armour type), and
    /// the slot it falls back to if its worn look can't be made: greaves, which Skyrim has no slot for, an unused one,
    /// drawing nothing. The worn look itself and its slots: ObWorn.</summary>
    static (string Heavy, string Light, BipedObjectFlag? Slot) ArmorTemplate(Ob.BipedFlag f) =>
        f.HasFlag(Ob.BipedFlag.UpperBody) ? ("ArmorIronCuirass", "ArmorLeatherCuirass", null)
        : f.HasFlag(Ob.BipedFlag.Head) || f.HasFlag(Ob.BipedFlag.Hair) ? ("ArmorIronHelmet", "ArmorLeatherHelmet", null)
        : f.HasFlag(Ob.BipedFlag.Hand) ? ("ArmorIronGauntlets", "ArmorLeatherGauntlets", null)
        : f.HasFlag(Ob.BipedFlag.Foot) ? ("ArmorIronBoots", "ArmorLeatherBoots", null)
        : f.HasFlag(Ob.BipedFlag.Shield) ? ("ArmorIronShield", "ArmorHideShield", null)
        : f.HasFlag(Ob.BipedFlag.LowerBody) ? ("ArmorIronBoots", "ArmorLeatherBoots", VanillaActors.Slot(53))
        : ("ArmorIronCuirass", "ArmorLeatherCuirass", null);

    IMajorRecord ArmorItem(string edid, Ob.IArmorGetter a)
    {
        var flags = a.ClothingFlags?.BipedFlags ?? 0;
        bool heavy = a.ClothingFlags?.GeneralFlags.HasFlag(Ob.EquipmentFlag.HeavyArmor) == true;
        var (h, l, slot) = ArmorTemplate(flags);
        return Wearable(Vanilla.Get<IArmorGetter>(heavy ? h : l)!, edid, a.Name, a.MaleWorldModel?.File, a.Data?.Weight ?? 0, a.Data?.Value ?? 0, (a.Data?.ArmorValue ?? 0) / 100f * 1.5f, slot,
            (a.MaleBipedModel?.File, a.FemaleBipedModel?.File, flags, true));
    }

    IMajorRecord ClothingItem(string edid, Ob.IClothingGetter c)
    {
        var f = c.ClothingFlags?.BipedFlags ?? 0;
        (string template, BipedObjectFlag? slot) =
            f.HasFlag(Ob.BipedFlag.UpperBody) ? ("ClothesFarmClothes01", (BipedObjectFlag?)null)
            : f.HasFlag(Ob.BipedFlag.Foot) ? ("ClothesFarmBoots01", null)
            : f.HasFlag(Ob.BipedFlag.Hand) ? ("ClothesJarlGloves", null)
            : f.HasFlag(Ob.BipedFlag.Amulet) ? ("JewelryNecklaceGold", null)
            : f.HasFlag(Ob.BipedFlag.RightRing) || f.HasFlag(Ob.BipedFlag.LeftRing) ? ("JewelryRingGold", null)
            : f.HasFlag(Ob.BipedFlag.Head) || f.HasFlag(Ob.BipedFlag.Hair) ? ("ClothesFarmHat", null)
            : ("ClothesFarmClothes01", VanillaActors.Slot(52));
        var vanilla = Vanilla.Get<IArmorGetter>(template) ?? Vanilla.Get<IArmorGetter>("ClothesFarmClothes01")!;
        return Wearable(vanilla, edid, c.Name, c.MaleWorldModel?.File, c.Data?.Weight ?? 0, c.Data?.Value ?? 0, 0, slot,
            (c.MaleBipedModel?.File, c.FemaleBipedModel?.File, f, false));
    }

    /// <param name="worn">Oblivion's worn models (male, female) and biped flags (ObWorn: its own worn look).</param>
    /// <param name="slot">The slot used if the worn look can't be made, for parts Skyrim has no slot for.</param>
    IMajorRecord Wearable(IArmorGetter template, string edid, string? name, string? model, float weight, uint value, float rating, BipedObjectFlag? slot,
        (string? Male, string? Female, Ob.BipedFlag Flags, bool Armour) worn)
    {
        var a = mod.Armors.DuplicateInAsNewRecord(template);
        a.EditorID = edid;
        a.Name = name;
        a.ObjectEffect.Clear();
        a.Value = value;
        a.Weight = weight;
        a.ArmorRating = rating;
        ApplyWornLook(a, worn.Male, worn.Female, worn.Flags, worn.Armour, template, slot);
        if (ItemMesh(model, weight) is { } mesh)
        {
            var world = new ArmorModel { Model = new Model { File = SkyrimModelPath(mesh.Path) } };
            a.WorldModel = new GenderedItem<ArmorModel?>(world, world.DeepCopy());
            a.ObjectBounds = Bounds(mesh);
        }
        return a;
    }

    LeveledItem LeveledList(string edid, Ob.ILeveledItemGetter l)
    {
        var list = new LeveledItem(mod)
        {
            EditorID = edid, ObjectBounds = new ObjectBounds(), ChanceNone = l.ChanceNone ?? new Percent(0),
            Flags = (l.Flags is { } f && f.HasFlag(Ob.LeveledFlag.CalculateFromAllLevelsLessThanPlayers) ? LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer : 0)
                  | (l.Flags is { } g && g.HasFlag(Ob.LeveledFlag.CalculateForEachItemInCount) ? LeveledItem.Flag.CalculateForEachItemInCount : 0),
            Entries = [],
        };
        mod.LeveledItems.Add(list);
        items[l.FormKey] = list;
        foreach (var e in l.Entries)
            if (Item(e.Reference.FormKey) is { } item)
                list.Entries.Add(new LeveledItemEntry { Data = new LeveledItemEntryData { Level = e.Level, Count = (short)(e.Count ?? 1), Reference = new FormLink<IItemGetter>(item.FormKey) } });
        return list;
    }

    Container? ContainerFor(Ob.IContainerGetter c, string edid)
    {
        if (c.Model?.File is not { Length: > 0 } file || IsMarker(file)) return null;
        var mesh = Mesh(file);
        if (mesh is null) return null;
        var container = new Container(mod)
        {
            EditorID = edid, Name = c.Name, Model = new Model { File = SkyrimModelPath(mesh.Path) }, ObjectBounds = Bounds(mesh),
            Weight = c.Data?.Weight ?? 0,
            Flags = c.Data?.Flags.HasFlag(Ob.Container.ContainerFlag.Respawns) == true ? Container.Flag.Respawns : 0,
            // Skyrim's chest sounds, DRScTreasureOpenSD / DRScTreasureCloseSD (2026-10-14: 0E3496/0E3497 were placed
            // objects, not sounds; 3,694 containers named a reference as their sound, which crashes the game).
            OpenSound = new FormLinkNullable<ISoundDescriptorGetter>(Sk(0x03C8E7)),
            CloseSound = new FormLinkNullable<ISoundDescriptorGetter>(Sk(0x03C8EA)),
            Items = [],
        };
        foreach (var i in c.Items)
            if (Item(i.Item.FormKey) is { } item)
                container.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = new FormLink<IItemGetter>(item.FormKey), Count = (int)Math.Max(1, i.Count) } });
        mod.Containers.Add(container);
        return container;
    }

    // ---- actors ----

    static readonly Dictionary<string, string> RaceStandIns = new(StringComparer.OrdinalIgnoreCase)
    {
        ["DarkSeducer"] = "DarkElf", ["GoldenSaint"] = "HighElf", ["Dremora"] = "DarkElf", ["Sheogorath"] = "Breton", ["Vampire"] = "Imperial", ["VampireRace"] = "Imperial",
    };

    static readonly (string Match, string Template)[] CreatureTable =
    [
        ("rat", "EncSkeever"), ("mudcrab", "EncMudcrabMedium"), ("dreugh", "EncMudcrabGiant"), ("slaughterfish", "EncSlaughterfish"),
        ("wolf", "EncWolf"), ("bear", "EncBear"), ("boar", "DLC2EncBoarWild"), ("deer", "EncDeer"), ("horse", "EncHorseBrown"), ("unicorn", "EncHorseGrey"),
        ("sheep", "EncGoatDomestic"), ("cow", "EncCow"), ("dog", "EncDog"), ("mountainlion", "EncSabreCat"), ("mountain lion", "EncSabreCat"),
        ("goblin", "EncFalmer01"), ("grummite", "EncFalmer01"), ("minotaur", "EncGiant01"), ("ogre", "EncGiant01"), ("gatekeeper", "EncGiant01"),
        ("troll", "EncTroll"), ("hunger", "EncTroll"), ("fleshatronach", "EncTroll"), ("flesh atronach", "EncTroll"),
        ("spriggan", "EncSpriggan"), ("gnarl", "EncSpriggan"), ("imp", "EncWisp"), ("wisp", "EncWisp"),
        ("zombie", "EncDraugr"), ("shambles", "EncDraugr"), ("skeleton", "EncSkeleton01"), ("ghost", "EncSkeleton01"), ("wraith", "EncSkeleton01"),
        ("lich", "EncDragonPriestStaff"), ("dremora", "EncDremoraMelee"), ("xivilai", "EncDremoraWarlock"), ("goldensaint", "EncDremoraWarlock"),
        ("golden saint", "EncDremoraWarlock"), ("darkseducer", "EncDremoraMelee"), ("dark seducer", "EncDremoraMelee"),
        ("clannfear", "EncSabreCat"), ("scalon", "EncSabreCat"), ("scamp", "EncSkeever"), ("daedroth", "DLC2EncLurker01"),
        ("spiderdaedra", "EncFrostbiteSpiderGiant"), ("spider daedra", "EncFrostbiteSpiderGiant"), ("spider", "EncFrostbiteSpider"),
        ("flameatronach", "EncAtronachFlame"), ("flame atronach", "EncAtronachFlame"), ("frostatronach", "EncAtronachFrost"),
        ("frost atronach", "EncAtronachFrost"), ("stormatronach", "EncAtronachStorm"), ("storm atronach", "EncAtronachStorm"),
        ("baliwog", "DLC2EncAshHopper"), ("elytra", "EncChaurus"), ("knightoforder", "EncDwarvenCenturion01"), ("knight of order", "EncDwarvenCenturion01"),
        ("darkguardian", "EncSkeleton01"), ("dark guardian", "EncSkeleton01"), ("priestoforder", "EncDremoraWarlock"), ("priest of order", "EncDremoraWarlock"), ("horker", "EncHorker"),
        // Quest uniques and Shivering Isles oddities.
        ("hound", "EncWolf"), ("auroran", "EncDremoraMelee"), ("umaril", "EncDremoraWarlock"), ("kastav", "EncDremoraMelee"),
        ("failedexperiment", "EncDraugr"), ("failed experiment", "EncDraugr"), ("rockhorror", "EncFrostbiteSpiderGiant"), ("uderfrykte", "EncFrostbiteSpiderGiant"),
        ("combatant", "EncDraugr"), ("uncleleo", "EncSkeever"), ("jyggalag", "EncDwarvenCenturion01"), ("cremator", "EncDraugr"),
        ("spirit", "EncSkeleton01"), ("ancestor", "EncSkeleton01"), ("deadlover", "EncSkeleton01"), ("mehrunesdagon", "EncGiant01"), ("ayleid", "EncDraugr"),
    ];

    IMajorRecord? Actor(FormKey key)
    {
        if (actors.TryGetValue(key, out var hit)) return hit;
        actors[key] = null;
        if (!records.TryGetValue(key, out var rec)) return null;
        try
        {
            return actors[key] = rec switch
            {
                Ob.INpcGetter n => Person(n),
                Ob.ICreatureGetter c => Creature(c),
                Ob.ILeveledCreatureGetter l => LeveledCreature(l),
                _ => null,
            };
        }
        catch (Exception e) { Problems.Add($"actor {rec.EditorID}: {e.Message}"); return null; }
    }

    void AddInventory(Npc npc, IEnumerable<Ob.IItemEntryGetter> source)
    {
        foreach (var i in source)
            if (Item(i.Item.FormKey) is { } item)
            {
                npc.Items ??= [];
                npc.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = new FormLink<IItemGetter>(item.FormKey), Count = Math.Max(1, i.Count ?? 1) } });
            }
    }

    Npc? Person(Ob.INpcGetter n)
    {
        bool female = n.Configuration?.Flags.HasFlag(Ob.Npc.NpcFlag.Female) == true;
        string raceName = records.GetValueOrDefault(n.Race.FormKey)?.EditorID ?? "";
        // Shivering Isles' Daedric races (and Dremora, whose Skyrim race has no FaceGen NPCs) wear the nearest mortal look.
        raceName = RaceStandIns.GetValueOrDefault(raceName, raceName);
        var race = Vanilla.Get<IRaceGetter>(raceName + "Race");
        if (race is null) { Problems.Add($"npc {n.EditorID}: race {raceName} has no Skyrim counterpart"); return null; }
        var pool = Vanilla.Faces(race.FormKey, female);
        if (pool.Count == 0 && Vanilla.Get<IRaceGetter>("ImperialRace") is { } imperial) { race = imperial; pool = Vanilla.Faces(race.FormKey, female); }
        if (pool.Count == 0) { Problems.Add($"npc {n.EditorID}: no vanilla face for {raceName} {(female ? "F" : "M")}"); return null; }
        var look = pool[VanillaActors.StableHash(n.EditorID ?? n.FormKey.ToString()) % pool.Count];
        var npc = mod.Npcs.DuplicateInAsNewRecord(look);
        npc.EditorID = "OB" + Sanitize(n.EditorID ?? n.FormKey.ID.ToString("X6"));
        npc.Name = n.Name;
        npc.ShortName = null;
        npc.VirtualMachineAdapter = null;
        npc.Factions.Clear();
        // The vanilla look's crime faction too: each game's people answer to their own factions, never Skyrim's.
        npc.CrimeFaction.Clear();
        npc.Items = null;
        npc.DefaultOutfit.Clear();
        npc.SleepingOutfit.Clear();
        npc.DeathItem.Clear();
        npc.Packages.Clear();
        npc.Keywords = null;
        npc.Perks = null;
        npc.Template = new FormLinkNullable<INpcSpawnGetter>(look.FormKey);
        npc.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        var flags = NpcConfiguration.Flag.Unique | (female ? NpcConfiguration.Flag.Female : 0);
        if (n.Configuration?.Flags.HasFlag(Ob.Npc.NpcFlag.Essential) == true) flags |= NpcConfiguration.Flag.Essential;
        if (n.Configuration?.Flags.HasFlag(Ob.Npc.NpcFlag.Respawn) == true) flags |= NpcConfiguration.Flag.Respawn;
        npc.Configuration.Flags = flags;
        npc.Configuration.Level = n.Configuration?.Flags.HasFlag(Ob.Npc.NpcFlag.PCLevelOffset) == true
            ? new PcLevelMult { LevelMult = 1 }
            : new NpcLevel { Level = (short)Math.Clamp((int)(n.Configuration?.LevelOffset ?? 1), 1, 81) };
        npc.Packages.Add(new FormLink<IPackageGetter>(Sk(0x01B217)));   // DefaultSandboxEditorLocation512
        if (npc.AIData is { } ai) ai.Aggression = (n.AIData?.Aggression ?? 0) >= 80 ? Aggression.VeryAggressive : Aggression.Unaggressive;
        AddInventory(npc, n.Items);
        AddSpells(npc, n.Spells);
        VanillaActors.WearInventory(mod, npc, outfits, "OB");
        AddFactionsAndClass(npc, n);
        AddCrime(npc, n);
        return npc;
    }

    Faction? moneyChangers, pickpockets;

    /// <summary>Whether an Oblivion NPC buys and sells misc goods (its AI services).</summary>
    static bool BartersMisc(Ob.INpcGetter n) => n.AIData?.BuySellServices.ToString().Contains("Miscellaneous") == true;

    /// <summary>Oblivion's factions (with rank) and class, each game's own records (OBFaction*, OBClass*); and for coin
    /// (CoinTrade), whoever barters misc goods changes coin, and thieves and beggars pick pockets.</summary>
    void AddFactionsAndClass(Npc npc, Ob.INpcGetter n)
    {
        var names = new List<string>();
        foreach (var rp in n.Factions)
        {
            if (ObFaction(rp.Faction.FormKey) is not { } f) continue;
            npc.Factions.Add(new RankPlacement { Faction = f.ToLink<IFactionGetter>(), Rank = (sbyte)Math.Clamp((int)rp.Rank, 0, 127) });
            names.Add(records.GetValueOrDefault(rp.Faction.FormKey)?.EditorID ?? "");
        }
        if (!n.Class.IsNull && ObClass(n.Class.FormKey) is { } cls)
        {
            npc.Class.SetTo(cls);
            names.Add(records.GetValueOrDefault(n.Class.FormKey)?.EditorID ?? "");
        }
        if (BartersMisc(n))
            npc.Factions.Add(new RankPlacement { Faction = (moneyChangers ??= CoinTrade.Tag(mod, "OBMoneyChangers", "Money changers")).ToLink<IFactionGetter>(), Rank = 0 });
        string who = string.Join(" ", names).ToLowerInvariant();
        // Guards may doze on night watch (TMNightDanger).
        if (who.Contains("guard") || who.Contains("watch"))
            npc.Factions.Add(new RankPlacement { Faction = TownNights.Guards(mod, "OB").ToLink<IFactionGetter>(), Rank = 0 });
        if (who.Contains("thie") || who.Contains("beggar") || who.Contains("pickpocket"))
            npc.Factions.Add(new RankPlacement { Faction = (pickpockets ??= CoinTrade.Tag(mod, CoinTrade.PickpocketsId("OB"), "Pickpockets")).ToLink<IFactionGetter>(), Rank = 0 });
    }

    readonly Dictionary<string, Outfit> outfits = new();

    Npc? Creature(Ob.ICreatureGetter c)
    {
        string id = (c.EditorID ?? "").ToLowerInvariant(), name = (c.Name ?? "").ToLowerInvariant();
        var map = CreatureTable.FirstOrDefault(t => id.Contains(t.Match) || name.Contains(t.Match));
        if (map.Template is null) { Problems.Add($"creature {c.EditorID} ({c.Name}): no stand-in"); return null; }
        var template = Vanilla.Get<INpcGetter>(map.Template);
        if (template is null) { Problems.Add($"creature {c.EditorID}: template {map.Template} not found"); return null; }
        var npc = mod.Npcs.DuplicateInAsNewRecord(template);
        npc.EditorID = "OB" + Sanitize(c.EditorID ?? c.FormKey.ID.ToString("X6"));
        npc.Name = c.Name;
        npc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.BaseData;
        if ((c.AIData?.Aggression ?? 0) < 80 && npc.AIData is { } ai) ai.Aggression = Aggression.Unaggressive;
        if (npc.Configuration.Level is NpcLevel && c.Configuration?.Flags.HasFlag(Ob.Creature.CreatureFlag.PCLevelOffset) != true)
            npc.Configuration.Level = new NpcLevel { Level = (short)Math.Clamp((int)(c.Configuration?.LevelOffset ?? 1), 1, 81) };
        AddInventory(npc, c.Items);
        AddSpells(npc, c.Spells);
        return npc;
    }

    Npc? LeveledCreature(Ob.ILeveledCreatureGetter l)
    {
        var list = new LeveledNpc(mod)
        {
            EditorID = "OBList" + Sanitize(l.EditorID ?? l.FormKey.ID.ToString("X6")),
            ObjectBounds = new ObjectBounds(),
            ChanceNone = l.ChanceNone ?? new Percent(0),
            Flags = l.Flags is { } f && f.HasFlag(Ob.LeveledFlag.CalculateFromAllLevelsLessThanPlayers) ? LeveledNpc.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer : 0,
            Entries = [],
        };
        mod.LeveledNpcs.Add(list);
        foreach (var e in l.Entries)
            if (Actor(e.Reference.FormKey) is Npc member)
                list.Entries.Add(new LeveledNpcEntry { Data = new LeveledNpcEntryData { Level = e.Level, Count = 1, Reference = new FormLink<INpcSpawnGetter>(member.FormKey) } });
        if (list.Entries.Count == 0) return null;
        var actor = mod.Npcs.DuplicateInAsNewRecord(Vanilla.Get<INpcGetter>("LvlBanditMelee1H")!);
        actor.EditorID = "OBLvl" + Sanitize(l.EditorID ?? l.FormKey.ID.ToString("X6"));
        actor.Template = new FormLinkNullable<INpcSpawnGetter>(list.FormKey);
        return actor;
    }

    /// <summary>Places an Oblivion NPC or creature reference.</summary>
    void PlaceActor(Ob.IPlacedGetter r, Cell cell, Cell persistentCell)
    {
        var (baseKey, loc, scale) = r switch
        {
            Ob.IPlacedNpcGetter n => (n.Base.FormKey, n.Location, n.Scale),
            Ob.IPlacedCreatureGetter c => (c.Base.FormKey, c.Location, c.Scale),
            _ => (FormKey.Null, null, null),
        };
        PlaceActorAt(r.FormKey, baseKey, loc, scale, cell, persistentCell);
    }

    /// <summary>Places an actor base (NPC, creature or leveled creature list) at a location. Oblivion places most of its
    /// leveled spawns through plain object references (REFR) rather than creature references.</summary>
    void PlaceActorAt(FormKey source, FormKey baseKey, Ob.ILocationGetter? loc, float? scale, Cell cell, Cell persistentCell)
    {
        if (loc is null || baseKey.IsNull || Actor(baseKey) is not Npc actor) return;
        var placed = new PlacedNpc(mod)
        {
            Base = new FormLinkNullable<INpcGetter>(actor.FormKey),
            Placement = new Placement { Position = loc.Position, Rotation = loc.Rotation },
            Scale = scale is { } s && MathF.Abs(s - 1) > 0.001f ? s : null,
        };
        if (!PlaceForScripts(source, placed, cell, persistentCell)) cell.Temporary.Add(placed);
        NoteActor(placed, baseKey, cell);
    }
}
