using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's quest items (an item group and index, Daggerfall Unity ItemGroups) as Skyrim items: Skyrim's own where it
/// has the same thing (Azura's Star, a ruby, the Ebony Blade), else a copy of a look-alike under Daggerfall's name (Jade,
/// Lich Dust, a Totem), and per group a list to pick a random one from (Daggerfall's "random gem", "magic item").
/// </summary>
public sealed class DfQuestItems
{
    readonly SkyrimMod mod;
    readonly Dictionary<string, IItemGetter> byName = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, FormKey> made = new(StringComparer.OrdinalIgnoreCase);
    readonly List<ISkyrimModGetter> masters;
    public SortedSet<string> Missing { get; } = [];

    /// <summary>Group names, Daggerfall Unity ItemGroups 0-28.</summary>
    public static readonly string[] Groups =
    [
        "Drugs", "UselessItems1", "Armor", "Weapons", "MagicItems", "Artifacts", "MensClothing", "Books", "Furniture", "UselessItems2",
        "ReligiousItems", "Maps", "WomensClothing", "Paintings", "Gems", "PlantIngredients1", "PlantIngredients2", "CreatureIngredients1",
        "CreatureIngredients2", "CreatureIngredients3", "MiscellaneousIngredients1", "MetalIngredients", "MiscellaneousIngredients2",
        "Transportation", "Deeds", "Jewellery", "QuestItems", "MiscItems", "Currency",
    ];

    /// <summary>(group, index) to Daggerfall's name and the Skyrim item that is (or stands in for) it.</summary>
    static readonly Dictionary<(int, int), (string Name, string Skyrim)> Known = new()
    {
        [(5, 0)] = ("Masque of Clavicus Vile", "Masque of Clavicus Vile"), [(5, 1)] = ("Mehrunes' Razor", "Mehrunes' Razor"),
        [(5, 2)] = ("Mace of Molag Bal", "Mace of Molag Bal"), [(5, 3)] = ("Ring of Hircine", "Ring of Hircine"),
        [(5, 4)] = ("Sanguine Rose", "Sanguine Rose"), [(5, 5)] = ("Oghma Infinium", "Oghma Infinium"), [(5, 6)] = ("Wabbajack", "Wabbajack"),
        [(5, 7)] = ("Ring of Namira", "Ring of Namira"), [(5, 8)] = ("Skull of Corruption", "Skull of Corruption"),
        [(5, 9)] = ("Azura's Star", "Azura's Star"), [(5, 10)] = ("Volendrung", "Volendrung"), [(5, 11)] = ("Warlock's Ring", "Gold Diamond Ring"),
        [(5, 12)] = ("Auriel's Bow", "Auriel's Bow"), [(5, 13)] = ("Necromancer's Amulet", "Necromancer Amulet"),
        [(5, 14)] = ("Chrysamere", "Daedric Greatsword"), [(5, 15)] = ("Lord's Mail", "Elven Armor"), [(5, 16)] = ("Staff of Magnus", "Staff of Magnus"),
        [(5, 17)] = ("Ring of Khajiit", "Silver Ring"), [(5, 18)] = ("Ebony Mail", "Ebony Mail"), [(5, 19)] = ("Auriel's Shield", "Auriel's Shield"),
        [(5, 20)] = ("Spell Breaker", "Spellbreaker"), [(5, 21)] = ("Skeleton's Key", "Skeleton Key"), [(5, 22)] = ("Ebony Blade", "Ebony Blade"),
        [(6, 8)] = ("Boots", "Boots"), [(12, 18)] = ("Robe", "Black Robes"),
        [(13, 0)] = ("Portrait", "Ruined Book"), [(13, 3)] = ("Painting", "Ruined Book"),
        [(14, 0)] = ("Ruby", "Ruby"), [(14, 1)] = ("Emerald", "Emerald"), [(14, 2)] = ("Sapphire", "Sapphire"), [(14, 3)] = ("Diamond", "Diamond"),
        [(14, 4)] = ("Jade", "Emerald"), [(14, 5)] = ("Turquoise", "Sapphire"), [(14, 6)] = ("Malachite", "Emerald"), [(14, 7)] = ("Amber", "Garnet"),
        [(10, 2)] = ("Common Symbol", "Amulet of Mara"), [(10, 5)] = ("Icon", "Amulet of Arkay"), [(10, 9)] = ("Scarab", "Amulet of Zenithar"),
        [(16, 3)] = ("Yellow Flowers", "Dragon's Tongue"), [(16, 4)] = ("Red Flowers", "Red Mountain Flower"),
        [(17, 0)] = ("Werewolf's Blood", "Troll Fat"), [(17, 7)] = ("Snake Venom", "Chaurus Eggs"), [(17, 9)] = ("Lich Dust", "Vampire Dust"),
        [(17, 12)] = ("Daedra Heart", "Daedra Heart"), [(18, 3)] = ("Mummy Wrappings", "Linen Wrap"), [(18, 4)] = ("Gorgon Snake", "Slaughterfish Scales"),
        [(19, 0)] = ("Lycanthrope Blood", "Troll Fat"), [(19, 2)] = ("Wraith Essence", "Ectoplasm"),
        [(20, 0)] = ("Holy Relic", "Amulet of Akatosh"), [(20, 4)] = ("Pure Water", "Potion of Cure Disease"), [(20, 6)] = ("Elixir Vitae", "Potion of Ultimate Healing"),
        [(21, 3)] = ("Lodestone", "Iron Ore"), [(21, 9)] = ("Gold Bar", "Gold Ingot"), [(22, 1)] = ("Pearl", "Pearl"),
        [(25, 2)] = ("Ring", "Gold Ring"), [(25, 3)] = ("Bracelet", "Silver Necklace"),
        [(26, 0)] = ("Telescope", "Dwarven Lever"), [(26, 4)] = ("Totem", "Bear Claws"), [(26, 7)] = ("Finger", "Skeever Tail"),
        [(1, 2)] = ("Small Statue", "Silver Candlestick"), [(6, 14)] = ("Tunic", "Fine Clothes"),
    };

    /// <summary>Per group: Skyrim items a random pick comes from.</summary>
    static readonly Dictionary<int, string[]> Randoms = new()
    {
        [0] = ["Skooma", "Sleeping Tree Sap", "Balmora Blue"],
        [1] = ["Silver Candlestick", "Gold Diamond Ring", "Silver Ingot", "Dwarven Gear"],
        [6] = ["Fine Clothes", "Farm Clothes", "Boots", "Fine Boots", "Hat"],
        [9] = ["Silver Candlestick", "Dwarven Gear", "Ruined Book"],
        [10] = ["Amulet of Akatosh", "Amulet of Arkay", "Amulet of Dibella", "Amulet of Julianos", "Amulet of Kynareth", "Amulet of Mara", "Amulet of Stendarr", "Amulet of Zenithar"],
        [12] = ["Fine Clothes", "Black Robes", "Blue Robes", "Red Robes", "Boots"],
        [13] = ["Ruined Book"],
        [14] = ["Ruby", "Emerald", "Sapphire", "Diamond", "Garnet", "Amethyst", "Flawless Ruby", "Flawless Sapphire"],
        [15] = ["Blue Mountain Flower", "Red Mountain Flower", "Lavender", "Nightshade", "Deathbell", "Nirnroot", "Bleeding Crown"],
        [16] = ["Dragon's Tongue", "Purple Mountain Flower", "Tundra Cotton", "Juniper Berries", "Snowberries"],
        [17] = ["Troll Fat", "Daedra Heart", "Vampire Dust", "Bear Claws", "Wolf Pelt"],
        [18] = ["Chaurus Eggs", "Spriggan Sap", "Hagraven Feathers", "Briar Heart"],
        [19] = ["Ectoplasm", "Fire Salts", "Frost Salts", "Void Salts"],
        [20] = ["Potion of Cure Disease", "Potion of Healing", "Potion of Plentiful Healing"],
        [21] = ["Gold Ingot", "Silver Ingot", "Iron Ore", "Corundum Ingot", "Quicksilver Ingot"],
        [22] = ["Pearl", "Salt Pile", "Bone Meal"],
        [25] = ["Gold Ring", "Silver Ring", "Gold Necklace", "Silver Necklace", "Gold Diamond Ring", "Silver Sapphire Necklace"],
        [26] = ["Dwarven Gear", "Bear Claws"],
        [27] = ["Silver Candlestick", "Dwarven Gear"],
    };

    public DfQuestItems(SkyrimMod mod, IEnumerable<ISkyrimModGetter> masters)
    {
        this.mod = mod;
        this.masters = masters.ToList();
        foreach (var m in this.masters)
            foreach (var r in m.EnumerateMajorRecords<IItemGetter>())
                if (r is ITranslatedNamedGetter { Name.String: { Length: > 0 } n } && !byName.ContainsKey(n) && Playable(r))
                    byName[n] = r;
    }

    static bool Playable(IItemGetter r) => r switch
    {
        IArmorGetter a => (a.MajorFlags & Armor.MajorFlag.NonPlayable) == 0,
        IWeaponGetter w => (w.MajorFlags & Weapon.MajorFlag.NonPlayable) == 0 && w.Template.IsNull,
        _ => true,
    };

    /// <summary>The Skyrim item for Daggerfall's (group, index), or null when the group's random list should be used.</summary>
    public FormKey? Specific(int group, int index)
    {
        if (Known.TryGetValue((group, index), out var k)) return Named(k.Name, k.Skyrim);
        return null;
    }

    /// <summary>Skyrim's item of that name, or a copy of the stand-in renamed.</summary>
    public FormKey? Named(string dfName, string skyrimName)
    {
        if (byName.TryGetValue(dfName, out var own)) return own.FormKey;
        if (made.TryGetValue(dfName, out var hit)) return hit;
        if (!byName.TryGetValue(skyrimName, out var template)) { Missing.Add($"{dfName} ({skyrimName})"); return null; }
        string edid = "DFQItem" + LocationPlacer.Sanitize(dfName);
        IMajorRecord copy = template switch
        {
            IMiscItemGetter m => mod.MiscItems.DuplicateInAsNewRecord(m),
            IIngredientGetter i => mod.Ingredients.DuplicateInAsNewRecord(i),
            IArmorGetter a => mod.Armors.DuplicateInAsNewRecord(a),
            IWeaponGetter w => mod.Weapons.DuplicateInAsNewRecord(w),
            IIngestibleGetter p => mod.Ingestibles.DuplicateInAsNewRecord(p),
            ISoulGemGetter s => mod.SoulGems.DuplicateInAsNewRecord(s),
            IBookGetter b => mod.Books.DuplicateInAsNewRecord(b),
            _ => throw new InvalidOperationException($"no copy for {template.GetType().Name}"),
        };
        copy.EditorID = edid;
        if (copy is ITranslatedNamed named) named.Name = dfName;
        return made[dfName] = copy.FormKey;
    }

    /// <summary>A form list per group for random picks (weapons, armor, books and magic items from the given lists).</summary>
    public List<FormKey> GroupLists(IReadOnlyList<FormKey> weapons, IReadOnlyList<FormKey> armor, IReadOnlyList<FormKey> books, IReadOnlyList<FormKey> magic)
    {
        var result = new List<FormKey>();
        for (int g = 0; g < Groups.Length; g++)
        {
            var list = new FormList(mod) { EditorID = "DFQuestItems" + Groups[g] };
            IEnumerable<FormKey> items = g switch
            {
                2 => armor,
                3 => weapons,
                4 => magic,
                5 => Known.Where(k => k.Key.Item1 == 5).Select(k => Named(k.Value.Name, k.Value.Skyrim)).OfType<FormKey>(),
                7 => books,
                _ => (Randoms.GetValueOrDefault(g) ?? []).Select(n => byName.TryGetValue(n, out var r) ? r.FormKey : (FormKey?)null).OfType<FormKey>(),
            };
            foreach (var k in items.Distinct()) list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(k));
            mod.FormLists.Add(list);
            result.Add(list.FormKey);
        }
        return result;
    }

    /// <summary>Enchanted rings and necklaces of Skyrim's, for Daggerfall's magic items.</summary>
    public List<FormKey> MagicItems(int count = 24)
    {
        var jewelry = new[] { "ClothingRing", "ClothingNecklace" }
            .Select(k => masters.SelectMany(m => m.Keywords).FirstOrDefault(x => x.EditorID == k)?.FormKey).OfType<FormKey>().ToHashSet();
        return masters.Take(1).SelectMany(m => m.Armors)
            .Where(a => !a.ObjectEffect.IsNull && a.Keywords is { } kw && kw.Any(x => jewelry.Contains(x.FormKey)) && (a.MajorFlags & Armor.MajorFlag.NonPlayable) == 0
                && a.Name?.String is { Length: > 0 } && a.EditorID?.StartsWith("Ench") == true)
            .OrderBy(a => a.EditorID, StringComparer.Ordinal).Take(count).Select(a => a.FormKey).ToList();
    }
}
