using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;
using Noggog;

using TamrielMerge.Skyrim.Meshes;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's weapons and armor as Skyrim items (user, 2026-10-12: "DF items, Skyrim shapes"): its 20 weapon types
/// in its 10 materials and its 11 armor pieces in leather, chain and the 10 plate materials, named as Daggerfall names
/// them ("Dwarven Dai-katana", "Ebony Left Pauldron"). Numbers from Daggerfall's own item data (Daggerfall Unity's
/// ItemTemplates and ItemBuilder): weight base x material quarters (iron 4, steel 5, silver 4, elven 4, dwarven 3, mithril
/// 4, adamantium 4, ebony 2, orcish 4, daedric 5) / 4, leather half; value base x 3 x 1, 2, 4 ... 512, chain x 2. Each
/// wears Skyrim's model of its kind and material (found by Skyrim's own keywords); where Skyrim has no such material -
/// silver (but its silver swords), mithril, adamantium - its steel model with the textures recoloured to the metal.
/// Damage and armor rating keep Skyrim's balance for the material, scaled by how Daggerfall's types differ (a dagger's
/// average against a longsword's). Greaves and pauldrons, which Skyrim's bodies have no part for, give their rating
/// unseen (biped slots 38, 44, 45).
/// </summary>
public sealed class DfItems
{
    public enum Material { Iron, Steel, Silver, Elven, Dwarven, Mithril, Adamantium, Ebony, Orcish, Daedric }

    static readonly int[] WeightQuarters = [4, 5, 4, 4, 3, 4, 4, 2, 4, 5];
    static readonly int[] ValueMultipliers = [1, 2, 4, 8, 16, 32, 64, 128, 256, 512];
    /// <summary>Daggerfall's plate armor rating by material (leather 3, chain 6 before these).</summary>
    static readonly int[] PlateRating = [7, 9, 9, 11, 13, 15, 15, 17, 19, 21];

    /// <summary>A weapon type: Daggerfall's name, damage, weight (kg), base price; and Skyrim's weapon kind it wears.</summary>
    sealed record WeaponType(string Name, int Min, int Max, float Weight, int Price, string Kind, float KindAverage);

    /// <summary>Skyrim kinds by WeapType keyword; KindAverage is the Daggerfall type Skyrim's kind is balanced as.</summary>
    static readonly WeaponType[] Weapons =
    [
        new("Dagger", 1, 6, 0.5f, 1, "Dagger", 3.5f),
        new("Tanto", 1, 8, 0.75f, 3, "Dagger", 3.5f),
        new("Shortsword", 1, 8, 2.5f, 5, "Sword", 9),
        new("Wakizashi", 1, 10, 2, 8, "Sword", 9),
        new("Broadsword", 1, 12, 5, 10, "Sword", 9),
        new("Saber", 3, 12, 3.75f, 12, "Sword", 9),
        new("Longsword", 2, 16, 4.5f, 15, "Sword", 9),
        new("Katana", 3, 16, 2.5f, 25, "Sword", 9),
        new("Claymore", 2, 18, 7.5f, 30, "Greatsword", 10),
        new("Dai-katana", 3, 21, 3.5f, 50, "Greatsword", 10),
        new("Battle Axe", 2, 12, 6, 20, "WarAxe", 7),          // Daggerfall's one-handed axe
        new("War Axe", 2, 16, 7.5f, 20, "Battleaxe", 9),       // its two-handed one
        new("Staff", 1, 8, 2, 5, "Warhammer", 10.5f),
        new("Mace", 1, 12, 4.5f, 10, "Mace", 6.5f),
        new("Flail", 2, 14, 7, 15, "Mace", 6.5f),
        new("Warhammer", 3, 18, 7, 20, "Warhammer", 10.5f),
        new("Short Bow", 4, 16, 1, 10, "Bow", 11),
        new("Long Bow", 4, 18, 1.5f, 20, "Bow", 11),
    ];

    /// <summary>An armor piece: name, weight (kg), price, Skyrim kind (ArmorX keyword, or null for one Skyrim has no
    /// part for), its share of a cuirass's rating where Skyrim has no part, and its biped slot then.</summary>
    sealed record Piece(string Name, float Weight, int Price, string? Kind, float Share, int Slot);

    static readonly Piece[] Pieces =
    [
        new("Cuirass", 12.5f, 100, "ArmorCuirass", 0, 0),
        new("Gauntlets", 1.25f, 50, "ArmorGauntlets", 0, 0),
        new("Greaves", 2.5f, 80, null, 0.35f, 38),
        new("Left Pauldron", 2, 60, null, 0.2f, 44),
        new("Right Pauldron", 2, 60, null, 0.2f, 45),
        new("Helm", 2.5f, 80, "ArmorHelmet", 0, 0),
        new("Boots", 2, 50, "ArmorBoots", 0, 0),
        new("Buckler", 2, 10, "ArmorShield", 0, 0),
        new("Round Shield", 4.5f, 20, "ArmorShield", 0, 0),
        new("Kite Shield", 7.5f, 30, "ArmorShield", 0, 0),
        new("Tower Shield", 12.5f, 50, "ArmorShield", 0, 0),
    ];

    /// <summary>Daggerfall's shields' fixed ratings (buckler 1 to tower 4), against a Skyrim shield's material rating.</summary>
    static float ShieldScale(string name) => name switch { "Buckler" => 0.6f, "Round Shield" => 0.8f, "Kite Shield" => 1f, _ => 1.2f };

    /// <summary>Skyrim's material keyword for each (null: none - steel's model, recoloured).</summary>
    static string? WeaponMaterialKeyword(Material m) => m switch
    {
        Material.Iron => "WeapMaterialIron", Material.Steel => "WeapMaterialSteel", Material.Silver => "WeapMaterialSilver",
        Material.Elven => "WeapMaterialElven", Material.Dwarven => "WeapMaterialDwarven", Material.Ebony => "WeapMaterialEbony",
        Material.Orcish => "WeapMaterialOrcish", Material.Daedric => "WeapMaterialDaedric", _ => null,
    };

    static string? ArmorMaterialKeyword(int armorMaterial) => armorMaterial switch
    {
        0 => "ArmorMaterialLeather", 1 => "ArmorMaterialImperialHeavy", 2 => "ArmorMaterialIron", 3 => "ArmorMaterialSteel",
        5 => "ArmorMaterialElven", 6 => "ArmorMaterialDwarven", 9 => "ArmorMaterialEbony", 10 => "ArmorMaterialOrcish",
        11 => "ArmorMaterialDaedric", _ => null,
    };

    /// <summary>Armor materials: 0 leather, 1 chain, then 2 + plate material (iron to daedric).</summary>
    static string ArmorMaterialName(int a) => a switch { 0 => "Leather", 1 => "Chain", _ => ((Material)(a - 2)).ToString() };

    /// <summary>The colour a recoloured metal takes (multiplied into steel's textures, its brightness kept).</summary>
    static (float R, float G, float B)? Tint(Material m) => m switch
    {
        Material.Silver => (1.12f, 1.14f, 1.18f),
        Material.Mithril => (0.92f, 1.04f, 1.22f),
        Material.Adamantium => (0.62f, 0.7f, 0.8f),
        _ => null,
    };

    readonly SkyrimMod mod;
    readonly ILinkCache cache;
    /// <summary>Numbers from 0xD00000 up, in a fixed order: placed in Daggerfall.esm's running order they would renumber
    /// everything built after them, which the plugins built on it and saves refer to.</summary>
    uint nextId = 0xD00000;
    FormKey NextKey() => new(mod.ModKey, nextId++);
    readonly List<ISkyrimModGetter> sources;
    readonly ItemRecolour recolour;

    /// <summary>The made items by Daggerfall name ("Steel Katana"), and lists by material tier.</summary>
    public Dictionary<string, FormKey> Items { get; } = new();
    public Dictionary<Material, LeveledItem> WeaponsOf { get; } = new();
    public Dictionary<int, LeveledItem> ArmorOf { get; } = new();
    public LeveledItem? AllWeapons { get; private set; }
    public LeveledItem? AllArmor { get; private set; }
    public SortedSet<string> Missing { get; } = new();

    public DfItems(SkyrimMod mod, IEnumerable<ISkyrimModGetter> skyrimMasters, string skyrimData, string outDir)
    {
        this.mod = mod;
        this.outDir = outDir;
        skyrimDataDir = skyrimData;
        sources = skyrimMasters.ToList();
        cache = sources.ToImmutableLinkCache();
        recolour = new ItemRecolour(skyrimData, outDir, NextKey);
    }

    readonly string outDir;
    DfWornParts? wornParts;
    readonly string skyrimDataDir = "";

    /// <summary>The made greaves' and pauldrons' checks, and the pieces that couldn't be made (with why).</summary>
    public IReadOnlyList<string> WornChecks => wornParts?.Checks ?? [];
    public List<string> WornMissing { get; } = [];

    /// <summary>A worn part for a greave or pauldron made over <paramref name="cuirassName"/>'s worn look (DfWornParts),
    /// with the cuirass part's races; null when it can't be made (WornMissing says why).</summary>
    ArmorAddon? WornPieceOver(string cuirassName, string edid, string piece, BipedObjectFlag slot, int partition)
    {
        if (!Items.TryGetValue(cuirassName, out var cuirassKey) || !mod.Armors.TryGetValue(cuirassKey, out var cuirass) || cuirass.Armature.Count == 0)
        { WornMissing.Add($"{edid}: no {cuirassName} to fit over"); return null; }
        var link = cuirass.Armature[0].FormKey;
        IArmorAddonGetter? part = mod.ArmorAddons.TryGetValue(link, out var ours) ? ours : cache.TryResolve<IArmorAddonGetter>(link, out var theirs) ? theirs : null;
        if (part?.WorldModel?.Male?.File.DataRelativePath.Path is not { Length: > 0 } male) { WornMissing.Add($"{edid}: {cuirassName}'s worn part has no model"); return null; }
        wornParts ??= new DfWornParts(new SkyrimCharacterAssets(skyrimDataDir), outDir);
        var problems = new List<string>();
        var paths = wornParts.Make(edid, piece, male, part.WorldModel.Female?.File.DataRelativePath.Path, partition, problems);
        WornMissing.AddRange(problems);
        if (paths is null) return null;
        var aa = new ArmorAddon(NextKey(), SkyrimRelease.SkyrimSE)
        {
            EditorID = edid + "AA",
            BodyTemplate = new BodyTemplate { FirstPersonFlags = slot, ArmorType = part.BodyTemplate?.ArmorType ?? ArmorType.LightArmor },
            Race = part.Race.FormKeyNullable is { } r ? r.ToNullableLink<IRaceGetter>() : new FormLinkNullable<IRaceGetter>(),
            WorldModel = new GenderedItem<Model?>(new Model { File = paths[(false, false)] }, new Model { File = paths.TryGetValue((true, false), out var f) ? f : paths[(false, false)] }),
            Priority = new GenderedItem<byte>(1, 1),
            WeightSliderEnabled = new GenderedItem<bool>(true, true),
            FootstepSound = part.FootstepSound.FormKeyNullable is { } fs ? fs.ToNullableLink<IFootstepSetGetter>() : new FormLinkNullable<IFootstepSetGetter>(),
        };
        foreach (var extra in part.AdditionalRaces) aa.AdditionalRaces.Add(extra.FormKey);
        mod.ArmorAddons.Add(aa);
        return aa;
    }

    FormKey? Keyword(string editorId) =>
        sources.SelectMany(s => s.Keywords).FirstOrDefault(k => k.EditorID == editorId)?.FormKey;

    /// <summary>Skyrim's plainest weapon with both keywords: playable, unenchanted, the shortest EditorID.</summary>
    IWeaponGetter? WeaponTemplate(string kind, string materialKeyword)
    {
        if (Keyword("WeapType" + kind) is not { } type || Keyword(materialKeyword) is not { } mat) return null;
        return sources.SelectMany(s => s.Weapons)
            .Where(w => w.Keywords is { } k && k.Any(x => x.FormKey == type) && k.Any(x => x.FormKey == mat)
                && w.ObjectEffect.IsNull && w.Template.IsNull && w.Model?.File.DataRelativePath.Path is { Length: > 0 }
                && (w.MajorFlags & Weapon.MajorFlag.NonPlayable) == 0 && w.Name?.String is { Length: > 0 })
            .OrderBy(w => w.EditorID?.StartsWith(materialKeyword["WeapMaterial".Length..], StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
            .ThenBy(w => w.EditorID?.Length ?? 99).FirstOrDefault();
    }

    IArmorGetter? ArmorTemplate(string kind, string materialKeyword)
    {
        if (Keyword(kind) is not { } type || Keyword(materialKeyword) is not { } mat) return null;
        return sources.SelectMany(s => s.Armors)
            .Where(a => a.Keywords is { } k && k.Any(x => x.FormKey == type) && k.Any(x => x.FormKey == mat)
                && a.ObjectEffect.IsNull && a.TemplateArmor.IsNull && a.Armature.Count > 0
                && (a.MajorFlags & Armor.MajorFlag.NonPlayable) == 0 && a.Name?.String is { Length: > 0 })
            .OrderBy(a => a.EditorID?.StartsWith("Armor" + materialKeyword["ArmorMaterial".Length..], StringComparison.OrdinalIgnoreCase) == true ? 0 : 1)
            .ThenBy(a => a.EditorID?.Length ?? 99).FirstOrDefault();
    }

    public void Build()
    {
        var allWeapons = new List<FormKey>();
        foreach (Material m in Enum.GetValues<Material>())
        {
            var list = new List<FormKey>();
            foreach (var t in Weapons)
            {
                // Silver swords are Skyrim's own; other silver, mithril and adamantium steel recoloured.
                var tpl = (WeaponMaterialKeyword(m) is { } key ? WeaponTemplate(t.Kind, key) : null) ?? WeaponTemplate(t.Kind, "WeapMaterialSteel");
                if (tpl is null) { Missing.Add($"{m} {t.Name}"); continue; }
                bool tinted = !(WeaponMaterialKeyword(m) is { } k2 && WeaponTemplate(t.Kind, k2) is not null);
                var w = (Weapon)tpl.Duplicate(NextKey());
                mod.Weapons.Add(w);
                string name = $"{m} {t.Name}";
                w.EditorID = "DFWeap" + Clean(name);
                w.Name = name;
                if (w.BasicStats is { } b)
                {
                    b.Weight = MathF.Round(MathF.Round(t.Weight * 4) * WeightQuarters[(int)m] / 4f) / 4f * 2;   // Daggerfall kg to Skyrim weight (x2)
                    b.Value = (uint)(t.Price * 3 * ValueMultipliers[(int)m]);
                    b.Damage = (ushort)Math.Max(1, MathF.Round(b.Damage * ((t.Min + t.Max) / 2f) / t.KindAverage));
                }
                if (tinted && Tint(m) is { } tint) recolour.Weapon(w, tpl, m.ToString(), tint, mod);
                Items[name] = w.FormKey;
                list.Add(w.FormKey);
                if (DfWeaponKind(t.Name) is >= 0 and var kind) weaponKinds[kind].Add(w.FormKey);
            }
            WeaponsOf[m] = List($"DFWeapons{m}", list, 1);
            allWeapons.AddRange(list);
        }

        for (int a = 0; a < 12; a++)
        {
            var list = new List<FormKey>();
            foreach (var p in Pieces)
            {
                if (p.Name.Contains("Shield") || p.Name == "Buckler")
                    if (a < 2) continue;   // Daggerfall's shields are plate
                string matKey = ArmorMaterialKeyword(a) ?? "ArmorMaterialSteel";
                string kind = p.Kind ?? "ArmorCuirass";
                var tpl = ArmorTemplate(kind, matKey) ?? ArmorTemplate(kind, "ArmorMaterialSteel");
                if (tpl is null) { Missing.Add($"{ArmorMaterialName(a)} {p.Name}"); continue; }
                bool tinted = ArmorMaterialKeyword(a) is null || ArmorTemplate(kind, matKey) is null;
                var arm = (Armor)tpl.Duplicate(NextKey());
                mod.Armors.Add(arm);
                string name = $"{ArmorMaterialName(a)} {p.Name}";
                arm.EditorID = "DFArmor" + Clean(name);
                arm.Name = name;
                float weight = a == 0 ? MathF.Round((int)(p.Weight * 4) / 2f) / 4f
                    : a == 1 ? p.Weight
                    : MathF.Round(MathF.Round(p.Weight * 4) * WeightQuarters[a - 2] / 4f) / 4f;
                arm.Weight = weight * 2;
                arm.Value = (uint)(a == 0 ? p.Price : a == 1 ? p.Price * 2 : p.Price * 3 * ValueMultipliers[a - 2]);
                // Rating: Skyrim's for the material; recoloured metals by Daggerfall's ratings against steel's.
                float rating = arm.ArmorRating;
                if (tinted && a >= 2) rating = rating * PlateRating[a - 2] / PlateRating[(int)Material.Steel];
                if (p.Kind == "ArmorShield") rating *= ShieldScale(p.Name);
                if (p.Kind is null)
                {
                    // Greaves and pauldrons: a share of a cuirass's rating, on a slot of their own, made to be seen over
                    // this material's cuirass (DfWornParts).
                    rating = MathF.Round(arm.ArmorRating * p.Share);
                    var slot = (BipedObjectFlag)(1u << (p.Slot - 30));
                    arm.Armature.Clear();
                    if (arm.BodyTemplate is { } body) body.FirstPersonFlags = slot;
                    arm.WorldModel = null;
                    if (WornPieceOver($"{ArmorMaterialName(a)} Cuirass", arm.EditorID!, p.Name, slot, p.Slot) is { } made) arm.Armature.Add(made.ToLink<IArmorAddonGetter>());
                    else
                    {
                        // Couldn't be made (logged in WornMissing, so it stays on the open list): a part drawing nothing.
                        var races = sources.SelectMany(s => s.ArmorAddons).First(x => x.EditorID == "IronCuirassAA");
                        arm.Armature.Add(WornNothing.Addon(mod, outDir, arm.EditorID + "AA", slot, races, NextKey()).ToLink<IArmorAddonGetter>());
                    }
                }
                arm.ArmorRating = MathF.Round(rating);
                if (tinted && a >= 2 && Tint((Material)(a - 2)) is { } tint && p.Kind is not null) recolour.Armor(arm, tpl, ArmorMaterialName(a), tint, mod, cache);
                Items[name] = arm.FormKey;
                list.Add(arm.FormKey);
            }
            ArmorOf[a] = List($"DFArmor{ArmorMaterialName(a)}", list, 1);
        }

        // Every weapon and armor piece, its material showing up as Daggerfall's do with level.
        int[] weaponLevels = [1, 1, 3, 6, 9, 12, 15, 18, 21, 24];
        AllWeapons = new LeveledItem(NextKey(), SkyrimRelease.SkyrimSE) { EditorID = "DFWeaponsAll", Entries = [], Flags = LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer };
        foreach (var (m, l) in WeaponsOf) AllWeapons.Entries.Add(Entry(l.FormKey, (short)weaponLevels[(int)m]));
        mod.LeveledItems.Add(AllWeapons);
        int[] armorLevels = [1, 1, 1, 1, 3, 6, 9, 12, 15, 18, 21, 24];
        AllArmor = new LeveledItem(NextKey(), SkyrimRelease.SkyrimSE) { EditorID = "DFArmorAll", Entries = [], Flags = LeveledItem.Flag.CalculateFromAllLevelsLessThanOrEqualPlayer };
        foreach (var (a, l) in ArmorOf) AllArmor.Entries.Add(Entry(l.FormKey, (short)armorLevels[a]));
        mod.LeveledItems.Add(AllArmor);
        Console.WriteLine($"Daggerfall items: {Items.Count} weapons and armor pieces{(Missing.Count > 0 ? "; no Skyrim model for " + string.Join(", ", Missing.Take(8)) : "")}");
    }

    /// <summary>Daggerfall's weapon skill of a weapon type: 0 Short Blade, 1 Long Blade, 2 Axe, 3 Blunt Weapon (the
    /// progression's hidden skills, ProgressionSkills.WeaponSkills); -1 for bows.</summary>
    static int DfWeaponKind(string type) => type switch
    {
        "Dagger" or "Tanto" or "Shortsword" or "Wakizashi" => 0,
        "Broadsword" or "Saber" or "Longsword" or "Katana" or "Claymore" or "Dai-katana" => 1,
        "Battle Axe" or "War Axe" => 2,
        "Staff" or "Mace" or "Flail" or "Warhammer" => 3,
        _ => -1,
    };
    readonly List<FormKey>[] weaponKinds = [[], [], [], []];

    /// <summary>Daggerfall's weapons by weapon skill (DFWeaponsShortBlade...), for the progression; made last, so the
    /// other records keep their form IDs.</summary>
    public void WeaponKindLists()
    {
        string[] names = ["ShortBlade", "LongBlade", "Axe", "BluntWeapon"];
        for (int k = 0; k < names.Length; k++)
        {
            var l = new FormList(NextKey(), SkyrimRelease.SkyrimSE) { EditorID = "DFWeapons" + names[k] };
            foreach (var w in weaponKinds[k]) l.Items.Add(w.ToLink<ISkyrimMajorRecordGetter>());
            mod.FormLists.Add(l);
        }
    }

    LeveledItem List(string edid, List<FormKey> items, short level)
    {
        var l = new LeveledItem(NextKey(), SkyrimRelease.SkyrimSE) { EditorID = edid, Entries = [], Flags = LeveledItem.Flag.CalculateForEachItemInCount };
        foreach (var i in items) l.Entries.Add(Entry(i, level));
        mod.LeveledItems.Add(l);
        return l;
    }

    static LeveledItemEntry Entry(FormKey item, short level) =>
        new() { Data = new LeveledItemEntryData { Reference = item.ToLink<IItemGetter>(), Level = level, Count = 1 } };

    static string Clean(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());

    /// <summary>The Daggerfall items a plugin holds (Daggerfall.esm, or the one being built), by name, with their bounds.</summary>
    public static Func<string, (FormKey Key, System.Numerics.Vector3 Min, System.Numerics.Vector3 Max)?> Lookup(ISkyrimModGetter plugin)
    {
        var byName = new Dictionary<string, (FormKey, System.Numerics.Vector3, System.Numerics.Vector3)>(StringComparer.OrdinalIgnoreCase);
        static System.Numerics.Vector3 V(P3Int16 p) => new(p.X, p.Y, p.Z);
        foreach (var w in plugin.Weapons.Where(w => w.EditorID?.StartsWith("DFWeap") == true && w.Name?.String is not null))
            byName[w.Name!.String!] = (w.FormKey, V(w.ObjectBounds.First), V(w.ObjectBounds.Second));
        foreach (var a in plugin.Armors.Where(a => a.EditorID?.StartsWith("DFArmor") == true && a.Name?.String is not null))
            byName[a.Name!.String!] = (a.FormKey, V(a.ObjectBounds.First), V(a.ObjectBounds.Second));
        return name => byName.TryGetValue(name, out var hit) ? hit : null;
    }

    /// <summary>The leveled lists of all Daggerfall weapons and of all armor in a plugin, if it has them.</summary>
    public static (FormKey Weapons, FormKey Armor)? Lists(ISkyrimModGetter plugin) =>
        plugin.LeveledItems.FirstOrDefault(l => l.EditorID == "DFWeaponsAll") is { } w && plugin.LeveledItems.FirstOrDefault(l => l.EditorID == "DFArmorAll") is { } a
            ? (w.FormKey, a.FormKey) : null;

    /// <summary>The floor sprites of Daggerfall's equipment archive (207) and the items each can be (Daggerfall
    /// Unity's ItemTemplates world pictures), in iron and steel as dungeon floors have them.</summary>
    public static readonly Dictionary<int, string[]> FloorSprites = new()
    {
        [0] = ["Longsword", "Broadsword"], [1] = ["Longsword", "Saber"], [2] = ["Wakizashi", "Broadsword", "Saber", "Longsword", "Claymore", "Dai-katana"],
        [3] = ["Shortsword"], [4] = ["Battle Axe", "War Axe"], [5] = ["Dagger"], [6] = ["Mace", "Flail"], [7] = ["Staff"],
        [8] = ["Short Bow", "Long Bow"], [9] = ["Kite Shield", "Tower Shield"], [10] = ["Buckler", "Round Shield"],
        [11] = ["Cuirass"], [12] = ["Helm"], [13] = ["Gauntlets", "Greaves", "Left Pauldron", "Right Pauldron"], [14] = ["Helm"],
        [15] = ["Katana"],
    };
}
