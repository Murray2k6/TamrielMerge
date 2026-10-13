using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's items and containers as Skyrim records. Each item type is based on a similar vanilla record (a
/// longsword on the Iron Sword, a cuirass on Iron Armor, ...) so every field Skyrim needs is valid, then given
/// Morrowind's name, model, value, weight and stats. Loose items' models get dynamic physics. Worn armour and
/// clothing look like their Skyrim template for now: Morrowind's are fitted to Morrowind's skeleton.
/// </summary>
public sealed partial class MwPluginBuilder
{
    ISkyrimModDisposableGetter? skyrimEsm;
    Dictionary<string, IMajorRecordGetter>? vanilla;
    readonly Dictionary<string, IMajorRecord?> items = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Static> firstPerson = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Skyrim.esm, for item templates and magic effects; set before building.</summary>
    public string? SkyrimData { get; set; }

    T Vanilla<T>(string editorId) where T : class, IMajorRecordGetter
    {
        if (vanilla is null)
        {
            skyrimEsm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(SkyrimData ?? throw new InvalidOperationException("SkyrimData not set"), "Skyrim.esm"), SkyrimRelease.SkyrimSE);
            vanilla = new(StringComparer.OrdinalIgnoreCase);
            foreach (var r in skyrimEsm.EnumerateMajorRecords())
                if (r.EditorID is { } e && r is IWeaponGetter or IArmorGetter or IAmmunitionGetter or IMagicEffectGetter or IContainerGetter) vanilla.TryAdd(e, r);
        }
        return vanilla[editorId] as T ?? throw new KeyNotFoundException($"vanilla {typeof(T).Name} {editorId}");
    }

    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);

    /// <summary>The item's model converted with item physics (dynamic box of the item's weight).</summary>
    MwMeshConverter.Result? ItemMesh(string? model, float weight)
    {
        if (model is not { Length: > 0 }) return null;
        try { return meshes.Convert(model, modOutDir, itemMass: Math.Clamp(weight, 0.3f, 40f)); }
        catch (Exception e) { Problems.Add($"item model {model}: {e.Message}"); return null; }
    }

    static bool IsItemType(string type) => type is "MISC" or "WEAP" or "ARMO" or "CLOT" or "BOOK" or "ALCH" or "INGR" or "APPA" or "LOCK" or "PROB" or "REPA" or "LEVI";

    /// <summary>Morrowind's coins: Gold_001, and the piles Gold_005 to Gold_100 that are worth that many. They become
    /// Skyrim's gold, the money barter, dialogue and scripts count (drakes while the player is in Morrowind's time,
    /// TMStoryOrder); 0 for anything else.</summary>
    static int CoinsIn(string id) => id.ToLowerInvariant() switch
    {
        "gold_001" => 1, "gold_005" => 5, "gold_010" => 10, "gold_025" => 25, "gold_100" => 100, _ => 0,
    };
    static readonly FormKey SkyrimGold = new(ModKey.FromFileName("Skyrim.esm"), 0x00000F);

    /// <summary>How many of an item a stack of count Morrowind items is (a coin pile counts its coins).</summary>
    static int StackOf(string id, int count) => Math.Max(1, count) * Math.Max(1, CoinsIn(id));

    /// <summary>The Skyrim item for a Morrowind item ID (created on first use), or null. Coins are a stand-in carrying
    /// Skyrim's Gold001 key.</summary>
    IMajorRecord? Item(string id)
    {
        if (items.TryGetValue(id, out var hit)) return hit;
        if (CoinsIn(id) > 0) return items[id] = new MiscItem(SkyrimGold, SkyrimRelease.SkyrimSE);
        items[id] = null;
        if (!world.Objects.TryGetValue(id, out var rec) || !IsItemType(rec.Type)) return null;
        string edid = "MW" + Sanitize(id);
        string? name = rec.Str("FNAM");
        string? model = rec.Str("MODL");
        IMajorRecord? result = null;
        try
        {
            result = rec.Type switch
            {
                "MISC" => Misc(rec, edid, name, model),
                "APPA" => Simple(rec, edid, name, model, "AADT", weightAt: 8, valueAt: 12),
                "LOCK" or "PROB" => Tool(rec, edid, name, model, pick: rec.Type == "LOCK"),
                "REPA" => Simple(rec, edid, name, model, "RIDT", 0, 4),
                "BOOK" => ScrollItem(rec, edid, name, model) ?? BookItem(rec, edid, name, model),
                "ALCH" => Potion(rec, edid, name, model),
                "INGR" => IngredientItem(rec, edid, name, model),
                "WEAP" => WeaponItem(rec, edid, name, model),
                "ARMO" => ArmorItem(rec, edid, name, model),
                "CLOT" => ClothingItem(rec, edid, name, model),
                "LEVI" => LeveledList(rec, edid),
                _ => null,
            };
        }
        catch (Exception e) { Problems.Add($"item {id}: {e.Message}"); }
        items[id] = result;
        if (rec.Type is "WEAP" or "ARMO" or "CLOT") Enchant(rec, result);
        return result;
    }

    IMajorRecord Misc(Tes3Record rec, string edid, string? name, string? model)
    {
        var d = rec.Get("MCDT")!.Value;
        float weight = d.Float(0); uint value = (uint)Math.Max(0, d.Int(4));
        bool isKey = d.Data.Length >= 12 && d.Int(8) != 0;
        var mesh = ItemMesh(model, weight);
        if (isKey)
        {
            var key = new Key(mod) { EditorID = edid, Name = name, Value = value, Weight = weight, ObjectBounds = Bounds(mesh) };
            if (mesh is not null) key.Model = new Model { File = SkyrimModelPath(mesh.Path) };
            mod.Keys.Add(key);
            return key;
        }
        var misc = new MiscItem(mod) { EditorID = edid, Name = name, Value = value, Weight = weight, ObjectBounds = Bounds(mesh) };
        if (mesh is not null) misc.Model = new Model { File = SkyrimModelPath(mesh.Path) };
        mod.MiscItems.Add(misc);
        return misc;
    }

    /// <summary>Apparatus and repair hammers: misc items (Skyrim has no counterpart to use). Lockpicks and probes are
    /// tools to hold (MwLocks).</summary>
    IMajorRecord Simple(Tes3Record rec, string edid, string? name, string? model, string data, int weightAt, int valueAt)
    {
        var d = rec.Get(data)!.Value;
        float weight = d.Float(weightAt);
        var mesh = ItemMesh(model, weight);
        var misc = new MiscItem(mod) { EditorID = edid, Name = name, Value = (uint)Math.Max(0, d.Int(valueAt)), Weight = weight, ObjectBounds = Bounds(mesh) };
        if (mesh is not null) misc.Model = new Model { File = SkyrimModelPath(mesh.Path) };
        mod.MiscItems.Add(misc);
        return misc;
    }

    /// <summary>Morrowind book text is HTML-like (&lt;DIV ALIGN&gt;, &lt;BR&gt;, &lt;FONT&gt;, &lt;IMG&gt;); Skyrim's books
    /// read a Scaleform subset with &lt;p align&gt; and &lt;br&gt;. Fonts and pictures are dropped.</summary>
    static string BookText(string html)
    {
        string t = html.Replace("\r", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<\s*IMG[^>]*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<\s*/?\s*FONT[^>]*>", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<\s*DIV\s+ALIGN\s*=\s*""?(\w+)""?\s*>", m => $"<p align=\"{m.Groups[1].Value.ToLowerInvariant()}\">", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<\s*/\s*DIV\s*>", "</p>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<\s*BR\s*/?\s*>", "<br>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return t.Trim();
    }

    IMajorRecord BookItem(Tes3Record rec, string edid, string? name, string? model)
    {
        var d = rec.Get("BKDT")!.Value;
        float weight = d.Float(0);
        var mesh = ItemMesh(model, weight);
        var book = new Book(mod)
        {
            EditorID = edid,
            Name = name,
            BookText = BookText(rec.Str("TEXT") ?? ""),
            Value = (uint)Math.Max(0, d.Int(4)),
            Weight = weight,
            Type = d.Int(8) != 0 ? Book.BookType.NoteOrScroll : Book.BookType.BookOrTome,
            Teaches = new BookTeachesNothing(),
            ObjectBounds = Bounds(mesh),
        };
        if (mesh is not null) book.Model = new Model { File = SkyrimModelPath(mesh.Path) };
        mod.Books.Add(book);
        return book;
    }

    /// <summary>Morrowind magic effect index -> the Skyrim alchemy effect closest to it.</summary>
    static readonly Dictionary<int, string> AlchemyEffects = new()
    {
        [0] = "AlchWaterbreathing", [8] = "AlchFortifyCarryWeight", [23] = "AlchDamageHealth", [24] = "AlchDamageMagicka",
        [25] = "AlchDamageStamina", [27] = "AlchDamageHealth", [39] = "AlchInvisibility", [45] = "AlchParalysis",
        [69] = "AlchCureDisease", [70] = "AlchCureDisease", [71] = "AlchCureDisease", [75] = "AlchRestoreHealth",
        [76] = "AlchRestoreMagicka", [77] = "AlchRestoreStamina", [80] = "AlchFortifyHealth", [81] = "AlchFortifyMagicka",
        [82] = "AlchFortifyStamina", [84] = "AlchFortifyMagicka", [90] = "AlchResistFire", [91] = "AlchResistFrost",
        [92] = "AlchResistShock", [93] = "AlchResistMagic", [97] = "AlchResistPoison",
    };

    Effect? AlchemyEffect(int mwEffect, float magnitude, int duration)
    {
        if (!AlchemyEffects.TryGetValue(mwEffect, out var edid)) return null;
        IMagicEffectGetter mgef;
        try { mgef = Vanilla<IMagicEffectGetter>(edid); } catch (KeyNotFoundException) { return null; }
        return new Effect
        {
            BaseEffect = mgef.ToNullableLink(),
            Data = new EffectData { Magnitude = Math.Max(1, magnitude), Duration = Math.Max(0, duration), Area = 0 },
        };
    }

    IMajorRecord Potion(Tes3Record rec, string edid, string? name, string? model)
    {
        var d = rec.Get("ALDT")!.Value;
        float weight = d.Float(0);
        var mesh = ItemMesh(model, weight);
        var potion = new Ingestible(mod)
        {
            EditorID = edid,
            Name = name,
            Weight = weight,
            Value = (uint)Math.Max(0, d.Int(4)),
            ObjectBounds = Bounds(mesh),
            ConsumeSound = new FormLink<ISoundDescriptorGetter>(Sk(0x0B6435)),   // ITMPotionUse
            Effects = [],
        };
        foreach (var e in rec.All("ENAM"))
            if (AlchemyEffect(BitConverter.ToInt16(e.Data, 0), (e.Int(16) + e.Int(20)) / 2f, e.Int(12)) is { } eff) potion.Effects.Add(eff);
        if (potion.Effects.Count == 0 && AlchemyEffect(75, 1, 0) is { } fallback) potion.Effects.Add(fallback);
        if (mesh is not null) potion.Model = new Model { File = SkyrimModelPath(mesh.Path) };
        mod.Ingestibles.Add(potion);
        return potion;
    }

    IMajorRecord IngredientItem(Tes3Record rec, string edid, string? name, string? model)
    {
        var d = rec.Get("IRDT")!.Value;
        float weight = d.Float(0);
        var mesh = ItemMesh(model, weight);
        var ingredient = new Ingredient(mod)
        {
            EditorID = edid,
            Name = name,
            Weight = weight,
            Value = (uint)Math.Max(0, d.Int(4)),
            IngredientValue = Math.Max(0, d.Int(4)),
            ObjectBounds = Bounds(mesh),
            Effects = [],
        };
        for (int i = 0; i < 4; i++)
            if (d.Int(8 + 4 * i) is var fx and >= 0 && AlchemyEffect(fx, 5, fx is 75 or 76 or 77 ? 0 : 30) is { } eff) ingredient.Effects.Add(eff);
        if (ingredient.Effects.Count == 0 && AlchemyEffect(75, 1, 0) is { } fallback) ingredient.Effects.Add(fallback);
        if (mesh is not null) ingredient.Model = new Model { File = SkyrimModelPath(mesh.Path) };
        mod.Ingredients.Add(ingredient);
        return ingredient;
    }

    /// <summary>Morrowind weapon type -> the vanilla weapon whose animations and handling fit it.</summary>
    static string WeaponTemplate(int type) => type switch
    {
        0 => "IronDagger", 1 => "IronSword", 2 => "IronGreatsword", 3 => "IronMace", 4 => "IronWarhammer",
        5 => "IronWarhammer", 6 => "IronBattleaxe", 7 => "IronWarAxe", 8 => "IronBattleaxe",
        9 or 10 => "LongBow", 12 or 13 => "IronArrow", _ => "",
    };

    Static FirstPersonStatic(string modelPath)
    {
        if (firstPerson.TryGetValue(modelPath, out var hit)) return hit;
        var s = new Static(mod) { EditorID = "MW1st" + Sanitize(Path.GetFileNameWithoutExtension(modelPath)), ObjectBounds = new ObjectBounds(), Model = new Model { File = modelPath } };
        mod.Statics.Add(s);
        return firstPerson[modelPath] = s;
    }

    IMajorRecord? WeaponItem(Tes3Record rec, string edid, string? name, string? model)
    {
        var d = rec.Get("WPDT")!.Value;
        float weight = d.Float(0);
        int value = d.Int(4), type = BitConverter.ToInt16(d.Data, 8);
        float speed = d.Float(12), reach = d.Float(16);
        int maxDamage = Math.Max(d.Data[23], Math.Max(d.Data[25], d.Data[27]));
        string template = WeaponTemplate(type);
        var mesh = ItemMesh(model, weight);
        if (template.Length == 0)
        {
            // Thrown weapons (darts, stars, knives): Skyrim has no thrown weapons; kept as items.
            var misc = new MiscItem(mod) { EditorID = edid, Name = name, Value = (uint)Math.Max(0, value), Weight = weight, ObjectBounds = Bounds(mesh) };
            if (mesh is not null) misc.Model = new Model { File = SkyrimModelPath(mesh.Path) };
            mod.MiscItems.Add(misc);
            return misc;
        }
        if (type is 12 or 13)
        {
            var ammo = mod.Ammunitions.DuplicateInAsNewRecord(Vanilla<IAmmunitionGetter>(template));
            ammo.EditorID = edid;
            ammo.Name = name;
            ammo.Value = (uint)Math.Max(0, value);
            ammo.Weight = weight;
            ammo.Damage = Math.Max(1, maxDamage);
            if (mesh is not null) ammo.Model = new Model { File = SkyrimModelPath(mesh.Path) };
            return ammo;
        }
        // A weapon Bethesda remade for Skyrim (Dwemer, glass, ebony, daedric, orcish, iron, steel, silver of a kind
        // Skyrim has): that remake, looks and all, with Morrowind's name and numbers.
        var canon = CanonWeapon(rec.Id, type);
        var w = mod.Weapons.DuplicateInAsNewRecord(canon ?? Vanilla<IWeaponGetter>(template));
        w.EditorID = edid;
        w.Name = name;
        w.ObjectEffect.Clear();
        w.BasicStats = new WeaponBasicStats { Value = (uint)Math.Max(0, value), Weight = weight, Damage = (ushort)Math.Clamp((int)MathF.Round(maxDamage * 0.6f + 2), 1, 200) };
        if (w.Data is { } wd) { wd.Speed = speed > 0 ? speed : wd.Speed; wd.Reach = reach > 0 ? reach : wd.Reach; }
        if (mesh is not null && canon is null)
        {
            string path = SkyrimModelPath(mesh.Path);
            w.Model = new Model { File = path };
            w.FirstPersonModel = FirstPersonStatic(path).ToNullableLink<IStaticGetter>();
            w.ObjectBounds = Bounds(mesh);
        }
        return w;
    }

    /// <summary>Morrowind armour part -> vanilla template and the Skyrim slot it takes. Parts Skyrim has no slot for
    /// (pauldrons, greaves, left gauntlets and bracers) take unused slots and have no worn look.</summary>
    static (string Template, BipedObjectFlag? Slot) ArmorTemplate(int type) => type switch
    {
        0 => ("ArmorIronHelmet", null), 1 => ("ArmorIronCuirass", null), 2 => ("ArmorIronCuirass", Slot(57)),
        3 => ("ArmorIronCuirass", Slot(58)), 4 => ("ArmorIronBoots", Slot(53)), 5 => ("ArmorIronBoots", null),
        // Right gauntlets and bracers are Skyrim's hands; the left ones take an unused slot so both are worn.
        7 or 10 => ("ArmorIronGauntlets", null), 8 => ("ArmorIronShield", null), 6 or 9 => ("ArmorIronGauntlets", Slot(60)),
        _ => ("ArmorIronCuirass", null),
    };

    /// <summary>A biped slot by its number (30 = head ... 61); slots 44-61 have no names.</summary>
    static BipedObjectFlag Slot(int n) => (BipedObjectFlag)(1u << (n - 30));

    IMajorRecord ArmorItem(Tes3Record rec, string edid, string? name, string? model)
    {
        var d = rec.Get("AODT")!.Value;
        int type = d.Int(0); float weight = d.Float(4); int value = d.Int(8), rating = d.Int(20);
        // A set Bethesda remade for Skyrim or Dragonborn: wear that remake, looks and all (see CanonArmor).
        if (CanonArmor(rec.Id, type) is { } canon)
        {
            var a = mod.Armors.DuplicateInAsNewRecord(canon);
            a.EditorID = edid;
            a.Name = name;
            a.ObjectEffect.Clear();
            a.Value = (uint)Math.Max(0, value);
            a.Weight = weight;
            a.ArmorRating = rating * 1.5f;
            return a;
        }
        var (template, slot) = ArmorTemplate(type);
        return Wearable(Vanilla<IArmorGetter>(template), edid, name, model, weight, value, rating * 1.5f, slot);
    }

    Skyrim.VanillaActors? canonForms;

    /// <summary>The Skyrim weapon of the same material and kind, when Skyrim has one (see WeaponItem).</summary>
    IWeaponGetter? CanonWeapon(string id, int type)
    {
        string kind = type switch { 0 => "Dagger", 1 => "Sword", 2 => "Greatsword", 3 => "Mace", 4 => "Warhammer", 7 => "WarAxe", 8 => "Battleaxe", 9 => "Bow", _ => "" };
        if (kind.Length == 0) return null;
        string i = id.ToLowerInvariant();
        string material = i switch
        {
            _ when i.Contains("dwemer") || i.Contains("dwarven") => "Dwarven",
            _ when i.Contains("glass") => "Glass",
            _ when i.Contains("ebony") => "Ebony",
            _ when i.Contains("daedric") => "Daedric",
            _ when i.Contains("orcish") => "Orcish",
            _ when i.Contains("silver") => "Silver",
            _ when i.Contains("steel") => "Steel",
            _ when i.Contains("iron") => "Iron",
            _ => "",
        };
        if (material.Length == 0) return null;
        canonForms ??= new Skyrim.VanillaActors(SkyrimData!);
        string edid = material + kind;
        return canonForms.Get<IWeaponGetter>(edid) is { } wpn && wpn.EditorID!.Equals(edid, StringComparison.OrdinalIgnoreCase) ? wpn : null;
    }

    /// <summary>
    /// Morrowind armour sets Bethesda remade in Skyrim and Dragonborn, by the words in their IDs: chitin, bonemold,
    /// netch leather, Dwemer, glass, ebony, daedric, orcish, imperial, steel, iron, leather and fur. A helmet,
    /// cuirass, boots, gauntlet/bracer or shield of such a set is that set's Skyrim piece; pauldrons and greaves,
    /// which Skyrim doesn't have, and every other set keep Morrowind's own look.
    /// </summary>
    IArmorGetter? CanonArmor(string id, int type)
    {
        // Right gauntlets and bracers are the pair Skyrim draws; left ones keep their own slot (MwItems.ArmorTemplate).
        string piece = type switch { 0 => "Helmet", 1 => "Cuirass", 5 => "Boots", 7 or 10 => "Gauntlets", 8 => "Shield", _ => "" };
        if (piece.Length == 0) return null;
        string i = id.ToLowerInvariant();
        string[] sets = i switch
        {
            _ when i.Contains("chitin") => ["DLC2ArmorChitinLight", "DLC2ArmorChitin"],
            _ when i.Contains("bonemold") => i.Contains("armun") || i.Contains("gah-julan") || i.Contains("improved") ? ["DLC2ArmorBonemoldImproved", "DLC2ArmorBonemold"] : ["DLC2ArmorBonemold"],
            _ when i.Contains("netch") => ["DLC2ArmorNetchLeather", "DLC2ArmorNetch"],
            _ when i.Contains("dwemer") || i.Contains("dwarven") => ["ArmorDwarven"],
            _ when i.Contains("glass") => ["ArmorGlass"],
            _ when i.Contains("ebony") => ["ArmorEbony"],
            _ when i.Contains("daedric") => ["ArmorDaedric"],
            _ when i.Contains("orcish") => ["ArmorOrcish"],
            _ when i.Contains("imperial") && (i.Contains("chain") || i.Contains("leather")) => ["ArmorImperialLight"],
            _ when i.Contains("imperial") => ["ArmorImperial"],
            _ when i.Contains("steel") => ["ArmorSteel", "ArmorSteelA"],
            _ when i.Contains("iron") && !i.Contains("nordic") => ["ArmorIron"],
            _ when i.Contains("leather") && !i.Contains("boiled") => ["ArmorLeather"],
            _ when i.Contains("fur") => ["ArmorHide"],
            _ => [],
        };
        if (sets.Length == 0) return null;
        canonForms ??= new Skyrim.VanillaActors(SkyrimData!);
        foreach (var set in sets)
            foreach (var suffix in piece == "Cuirass" || piece == "Helmet" || piece == "Boots" || piece == "Gauntlets" ? new[] { piece, piece + "A" } : [piece])
                if (canonForms.Get<IArmorGetter>(set + suffix) is { } a && a.EditorID!.Equals(set + suffix, StringComparison.OrdinalIgnoreCase)) return a;
        return null;
    }

    /// <summary>Morrowind clothing -> vanilla clothes of the same kind (shirts and robes as farm clothes, shoes as
    /// boots, rings and amulets as jewellery); pants, belts, skirts and gloves take unused slots.</summary>
    IMajorRecord ClothingItem(Tes3Record rec, string edid, string? name, string? model)
    {
        var d = rec.Get("CTDT")!.Value;
        int type = d.Int(0); float weight = d.Float(4); int value = BitConverter.ToUInt16(d.Data, 8);
        (string template, BipedObjectFlag? slot) = type switch
        {
            1 => ("ClothesFarmBoots01", (BipedObjectFlag?)null),
            2 or 4 => ("ClothesFarmClothes01", null),
            8 => ("JewelryRingGold", null),
            9 => ("JewelryNecklaceGold", null),
            0 or 7 => ("ClothesFarmClothes01", Slot(52)),
            3 => ("ClothesFarmClothes01", Slot(49)),
            6 => ("ClothesJarlGloves", Slot(60)),   // left glove; the right one is Skyrim's hands
            _ => ("ClothesJarlGloves", null),
        };
        return Wearable(Vanilla<IArmorGetter>(template), edid, name, model, weight, value, 0, slot);
    }

    IMajorRecord Wearable(IArmorGetter template, string edid, string? name, string? model, float weight, int value, float rating, BipedObjectFlag? slot)
    {
        var a = mod.Armors.DuplicateInAsNewRecord(template);
        a.EditorID = edid;
        a.Name = name;
        a.ObjectEffect.Clear();
        a.Value = (uint)Math.Max(0, value);
        a.Weight = weight;
        a.ArmorRating = rating;
        if (slot is { } s)
        {
            // A part Skyrim has no slot for: its own unused slot, nothing drawn when worn.
            a.BodyTemplate ??= new BodyTemplate();
            a.BodyTemplate.FirstPersonFlags = s;
            a.Armature.Clear();
        }
        if (ItemMesh(model, weight) is { } mesh)
        {
            var world = new ArmorModel { Model = new Model { File = SkyrimModelPath(mesh.Path) } };
            a.WorldModel = new GenderedItem<ArmorModel?>(world, world.DeepCopy());
            a.ObjectBounds = Bounds(mesh);
        }
        return a;
    }

    /// <summary>A leveled item list (LEVI): flags 1 = from all levels up to the player's, 2 = each item rolled.</summary>
    IMajorRecord LeveledList(Tes3Record rec, string edid)
    {
        int flags = rec.Get("DATA")?.Int() ?? 0;
        var list = new LeveledItem(mod)
        {
            EditorID = edid,
            ObjectBounds = new ObjectBounds(),
            ChanceNone = new Percent(Math.Clamp((rec.Get("NNAM")?.Data[0] ?? 0) / 100.0, 0, 1)),
            Flags = ((flags & 1) != 0 ? LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer : 0) | ((flags & 2) != 0 ? LeveledItem.Flag.CalculateForEachItemInCount : 0),
            Entries = [],
        };
        mod.LeveledItems.Add(list);
        items[rec.Id] = list;   // lists can contain themselves indirectly
        string? pending = null;
        foreach (var f in rec.Fields)
        {
            if (f.Type == "INAM") pending = f.Text;
            else if (f.Type == "INTV" && pending is not null)
            {
                if (Item(pending) is { } item)
                    list.Entries.Add(new LeveledItemEntry { Data = new LeveledItemEntryData { Level = (short)BitConverter.ToUInt16(f.Data, 0), Count = (short)StackOf(pending, 1), Reference = new FormLink<IItemGetter>(item.FormKey) } });
                pending = null;
            }
        }
        return list;
    }

    /// <summary>A container with Morrowind's contents (NPCO: count, item ID); FLAG 2 = respawns.</summary>
    Container? ContainerItem(Tes3Record rec, string edid)
    {
        string? model = rec.Str("MODL");
        if (model is not { Length: > 0 }) return null;
        MwMeshConverter.Result? mesh;
        try { mesh = Mesh(model); } catch { mesh = null; }
        if (mesh is null) return null;
        var container = new Container(mod)
        {
            EditorID = edid,
            Name = rec.Str("FNAM"),
            Model = new Model { File = SkyrimModelPath(mesh.Path) },
            ObjectBounds = Bounds(mesh),
            Weight = rec.Get("CNDT")?.Float() ?? 0,
            Flags = ((rec.Get("FLAG")?.Int() ?? 0) & 2) != 0 ? Container.Flag.Respawns : 0,
            OpenSound = new FormLinkNullable<ISoundDescriptorGetter>(Sk(0x0E3496)),    // TRNChestOpen
            CloseSound = new FormLinkNullable<ISoundDescriptorGetter>(Sk(0x0E3497)),   // TRNChestClose
            Items = [],
        };
        foreach (var f in rec.All("NPCO"))
        {
            int count = Math.Abs(f.Int(0));   // negative counts restock in Morrowind
            string id = Tes3Plugin.Encoding.GetString(f.Data, 4, f.Data.Length - 4).TrimEnd('\0');
            if (Item(id) is { } item)
                container.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = new FormLink<IItemGetter>(item.FormKey), Count = StackOf(id, count) } });
        }
        mod.Containers.Add(container);
        return container;
    }
}
