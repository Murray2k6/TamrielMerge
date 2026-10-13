using System.Numerics;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's 2D clutter sprites ("flats") as Skyrim objects: each sprite (TEXTURE archive.record) maps to vanilla
/// clutter by EditorID, picked by eye from contact sheets of the sprites (flat-sheets). Small things become real
/// items (goblets, bones, books, weapons, coins), large ones statics scaled to the sprite's height. Sprites stand
/// on the floor, hang from the ceiling, or (cave formations above) hang upside down. Daggerfall Unity hides the
/// treasure sprites (archive 216) and gives fixed treasure instead; here they become coins, gems and ingots.
/// Animals, monster statues, corpses and vegetation wait for the creature and exterior work.
/// </summary>
public sealed class ClutterPlacer
{
    public enum Mount { Floor, Ceiling, UpsideDown }

    /// <summary>One object of a sprite's replacement; extra parts sit on top of the first or scatter around it.</summary>
    public sealed record Part(string Id, bool OnTop = false, float Dx = 0, float Dy = 0);

    public sealed record Spec(string Label, Mount Mount, bool FitHeight, Part[] Parts, string[]? Variants = null);

    /// <summary>A Skyrim reference to place. Z is filled in by the caller from the floor or ceiling.</summary>
    public sealed record Placement(FormKey Base, Vector3 PositionSkyrim, Vector3 Rotation, float Scale, bool IsItem, string Label, Mount Mount);

    sealed record Form(FormKey Key, Vector3 Min, Vector3 Max, bool IsItem);

    readonly Dictionary<string, Form> forms = new(StringComparer.OrdinalIgnoreCase);
    readonly List<FormKey> dfBooks;
    public SortedSet<string> Missing { get; } = new();

    readonly PropReplacer shapes;

    public ClutterPlacer(ISkyrimModGetter skyrim, PropReplacer shapes, IEnumerable<FormKey> daggerfallBooks)
    {
        this.shapes = shapes;
        var wanted = Table.Values.SelectMany(s => s.Parts.Select(p => p.Id).Concat(s.Variants ?? [])).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var r in skyrim.EnumerateMajorRecords().OfType<IPlaceableObjectGetter>())
        {
            if (r.EditorID is not { } id || !wanted.Contains(id) || forms.ContainsKey(id)) continue;
            forms[id] = new Form(r.FormKey, Vector3.Zero, Vector3.Zero, r is IItemGetter);
        }
        foreach (var id in wanted.Where(w => !forms.ContainsKey(w) && w != Book)) Missing.Add(id);
        dfBooks = daggerfallBooks.ToList();
    }

    const string Book = "@DaggerfallBook";

    /// <summary>Daggerfall's statue sprites: 97 (people), 98 (monsters), 202 (art on plinths).</summary>
    public static bool IsStatue(DfRdbObject o) => o.Type == DfRdbObjectType.Flat && o.TextureArchive is 97 or 98 or 202;

    /// <summary>Statues whose front is local +Y (seen in model-preview, 2026-10-10); the rest used here face -Y
    /// (StatueDibella, MageStatue, StatueVaermina01, NorDragonStatue01, TG08StatueFalmer).</summary>
    static readonly HashSet<string> FrontPlusY = ["StatueMara01", "StatueTalos01"];

    public static bool Handles(DfRdbObject o) => o.Type == DfRdbObjectType.Flat && Table.ContainsKey((o.TextureArchive, o.TextureRecord));

    public static Mount MountOf(DfRdbObject o) => Table[(o.TextureArchive, o.TextureRecord)].Mount;

    Func<string, (FormKey Key, Vector3 Min, Vector3 Max)?>? dfItems;

    /// <summary>Daggerfall's own weapons and armor (DfItems) for its equipment sprites (207), as its dungeon floors have
    /// them: one of the items that sprite pictures, in iron or steel.</summary>
    public void UseDaggerfallItems(Func<string, (FormKey Key, Vector3 Min, Vector3 Max)?> lookup) => dfItems = lookup;

    /// <summary>Models made from Daggerfall's own sprites (SpriteModels), which stand in for the table's nearest
    /// vanilla object (or fill a gap it has).</summary>
    public Dictionary<(int Archive, int Record), Meshes.SpriteModelImporter.Made> Generated { get; } = new();

    /// <summary>Uses a model generated from a sprite (and makes its bounds known for settling and navmesh).</summary>
    public void AddGenerated((int Archive, int Record) sprite, Meshes.SpriteModelImporter.Made made)
    {
        Generated[sprite] = made;
        shapes.AddShape(made.Static, made.Min, made.Max);
    }

    /// <summary>Whether this placer has something for a sprite: a model of its own, or the table's.</summary>
    public bool Covers(DfRdbObject o) => o.Type == DfRdbObjectType.Flat && (Generated.ContainsKey((o.TextureArchive, o.TextureRecord)) || Table.ContainsKey((o.TextureArchive, o.TextureRecord)));

    public Mount MountFor(DfRdbObject o) => Generated.TryGetValue((o.TextureArchive, o.TextureRecord), out var g)
        ? (g.Kind == SpriteModels.Kind.Hanging ? Mount.Ceiling : Mount.Floor) : MountOf(o);

    /// <summary>
    /// The objects standing for a sprite. <paramref name="spriteHeightSkyrim"/> is the sprite's in-world height;
    /// <paramref name="anchorZ"/> is the floor (Floor) or ceiling (Ceiling, UpsideDown) height under/over it.
    /// </summary>
    public IEnumerable<Placement> Place(DfRdbObject o, Vector3 centreSkyrim, float spriteHeightSkyrim, float anchorZ, Vector2? facing = null, float spriteWidthSkyrim = 0)
    {
        // Deterministic per-object variety (heading, variant).
        uint hash = (uint)HashCode.Combine(o.RecordOffset, o.TextureArchive, o.TextureRecord, (int)centreSkyrim.X, (int)centreSkyrim.Y);
        float heading = hash % 3600 / 3600f * MathF.Tau;
        bool statue = IsStatue(o);
        if (Generated.TryGetValue((o.TextureArchive, o.TextureRecord), out var model))
        {
            // Sprite models face -Y (the sprite's own side; SpriteModelImporter).
            if (statue && facing is { } f) heading = RoomFacing.Heading(f, frontPlusY: false);
            // Made at the sprite's own height: stood on the floor or hung from the ceiling, turned at random.
            bool hangs = model.Kind == SpriteModels.Kind.Hanging;
            float gz = hangs ? anchorZ - model.Max.Z : anchorZ - model.Min.Z;
            yield return new Placement(model.Static, new Vector3(centreSkyrim.X, centreSkyrim.Y, gz), new Vector3(0, 0, heading), 1, false,
                "model: " + model.Kind.ToString().ToLowerInvariant(), hangs ? Mount.Ceiling : Mount.Floor);
            yield break;
        }
        if (dfItems is not null && o.TextureArchive == 207 && DfItems.FloorSprites.TryGetValue(o.TextureRecord, out var kinds)
            && dfItems($"{(hash / 11 % 3 == 0 ? "Steel" : "Iron")} {kinds[hash / 7 % (uint)kinds.Length]}") is { } item)
        {
            yield return new Placement(item.Key, new Vector3(centreSkyrim.X, centreSkyrim.Y, anchorZ - item.Min.Z + 1), new Vector3(0, 0, heading), 1, true,
                "daggerfall item", Mount.Floor);
            yield break;
        }
        var spec = Table[(o.TextureArchive, o.TextureRecord)];
        var first = spec.Parts[0];
        string firstId = spec.Variants is { Length: > 0 } v ? v[hash / 7 % (uint)v.Length] : first.Id;
        if (Resolve(firstId, hash) is not { } main) yield break;

        float height = main.Max.Z - main.Min.Z;
        float scale = !main.IsItem && spec.FitHeight && height > 1 ? Math.Clamp(spriteHeightSkyrim / height, statue ? 0.05f : 0.4f, 3f) : 1;
        if (statue)
        {
            // Into the room, and no broader than the sprite (a wide Skyrim statue fitted to a tall sprite would fill
            // its doorway; user, 2026-10-10: "statues obstruct doors in the throne room").
            if (facing is { } f) heading = RoomFacing.Heading(f, FrontPlusY.Contains(firstId));
            float foot = MathF.Max(main.Max.X - main.Min.X, main.Max.Y - main.Min.Y);
            if (spriteWidthSkyrim > 0 && foot > 1) scale = MathF.Min(scale, spriteWidthSkyrim * 1.5f / foot);
        }
        Vector3 rotation = new(spec.Mount == Mount.UpsideDown ? MathF.PI : 0, 0, heading);
        float z = spec.Mount switch
        {
            Mount.Floor => anchorZ - main.Min.Z * scale + (main.IsItem ? 1 : 0),
            Mount.Ceiling => anchorZ - main.Max.Z * scale,
            _ => anchorZ + main.Min.Z * scale,   // flipped: the object's base meets the ceiling
        };
        var pos = new Vector3(centreSkyrim.X, centreSkyrim.Y, z);
        yield return new Placement(main.Key, pos, rotation, scale, main.IsItem, spec.Label, spec.Mount);

        float top = spec.Mount == Mount.Floor ? z + main.Max.Z * scale : anchorZ;
        float c = MathF.Cos(heading), s = MathF.Sin(heading);
        foreach (var part in spec.Parts.Skip(1))
        {
            if (Resolve(part.Id, hash) is not { } f) continue;
            var offset = new Vector3(part.Dx * c + part.Dy * s, -part.Dx * s + part.Dy * c, 0);
            float pz = part.OnTop
                ? (spec.Mount == Mount.Ceiling ? z + main.Min.Z * scale - f.Min.Z + 2 : top - f.Min.Z)
                : anchorZ - f.Min.Z + (f.IsItem ? 1 : 0);
            yield return new Placement(f.Key, new Vector3(pos.X + offset.X, pos.Y + offset.Y, pz), new Vector3(0, 0, heading), 1, f.IsItem, spec.Label, spec.Mount);
        }
    }

    /// <summary>The form and its mesh bounds (vanilla OBND is missing or loose for some clutter).</summary>
    Form? Resolve(string id, uint hash)
    {
        if (id == Book)
            return dfBooks.Count == 0 ? null : new Form(dfBooks[(int)(hash % (uint)dfBooks.Count)], new Vector3(-9, -11, -2), new Vector3(9, 11, 2), true);
        if (forms.GetValueOrDefault(id) is not { } f) return null;
        var (min, max, _) = shapes.SkyrimShape(f.Key);
        return f with { Min = min, Max = max };
    }

    // ---- The table: Daggerfall sprite -> Skyrim objects ----

    static Spec Floor(string label, bool fit, params string[] ids) => new(label, Mount.Floor, fit, [new Part(ids[0])], ids.Length > 1 ? ids : null);
    static Spec Hang(string label, bool fit, params string[] ids) => new(label, Mount.Ceiling, fit, [new Part(ids[0])], ids.Length > 1 ? ids : null);
    static Spec Group(string label, Mount mount, bool fit, params Part[] parts) => new(label, mount, fit, parts);

    static Spec Coins(int n, params string[] extras)
    {
        var parts = new List<Part> { new("Gold001") };
        for (int i = 1; i < n; i++)
        {
            float a = i * 2.4f, r = 4 + 3 * MathF.Sqrt(i);
            parts.Add(new Part("Gold001", Dx: r * MathF.Cos(a), Dy: r * MathF.Sin(a)));
        }
        for (int i = 0; i < extras.Length; i++) parts.Add(new Part(extras[i], Dx: 14 + 8 * i, Dy: -6));
        return new Spec("treasure", Mount.Floor, false, [.. parts]);
    }

    static readonly Part[] Bones = [new("BoneHumanRibcageFull"), new("BoneHumanSkull", Dx: 30, Dy: 6), new("BoneHumanArmL", Dx: -24, Dy: 18), new("BoneHumanLegR", Dx: 10, Dy: -30)];
    static readonly Part[] BloodyBones = [new("BoneHumanBloodyRibcage"), new("BoneHumanBloodySkull", Dx: 28, Dy: 4), new("BoneHumanBloodyLeg", Dx: -20, Dy: -22), new("BoneHumanBloodyArm", Dx: 4, Dy: 26)];
    static Spec Impaled(string topId) => Group("impaled remains", Mount.Floor, true, new Part("StockadePike01"), new Part(topId, OnTop: true));

    static readonly string[] Potions = ["RestoreHealth01", "RestoreMagicka01", "RestoreStamina01"];
    static readonly string[] Planters = ["SBigPlanter02"];
    static readonly string[] Vines = ["NorVineWall02NoCol", "NorVineWall03NoCol"];

    public static readonly Dictionary<(int Archive, int Record), Spec> Table = Build();

    static Dictionary<(int, int), Spec> Build()
    {
        var t = new Dictionary<(int, int), Spec>();
        void Add(int archive, IEnumerable<int> records, Spec spec) { foreach (var r in records) t[(archive, r)] = spec; }
        void One(int archive, int record, Spec spec) => t[(archive, record)] = spec;

        // 200 House furnishings
        Add(200, [0, 2, 4], Floor("goblet", false, "SilverGoblet01", "SilverGoblet02"));
        Add(200, [1, 3, 5], Floor("goblet", false, "GlazedGoblet01"));
        One(200, 6, Floor("cup", false, "BasicTankard01"));
        Add(200, [7, 8, 9], Hang("hanging lantern", false, "CandleLanternwithCandle01"));
        One(200, 10, Floor("lantern", true, "CandleLanternwithCandle01"));
        Add(200, [11, 12, 13], Floor("cushion", false, "RuinsLinenPile01"));
        Add(200, [14, 20], Floor("chair", false, "CommonChair01Static"));
        Add(200, [15, 16], Floor("bench", false, "CommonBench01STATIC"));
        One(200, 17, Floor("coal", false, "Coal01"));
        One(200, 18, Floor("trough", false, "HorseTrough01"));
        One(200, 19, Floor("bucket", false, "Bucket01"));
        // 202 Art (monster statues on plinths)
        Add(202, [0, 1, 2, 3, 4], Floor("statue", true, "StatueVaermina01"));
        Add(202, [5, 6], Floor("statue", true, "StatueMara01"));
        // 203 Buildings (columns)
        Add(203, [0, 1, 5, 6], Floor("column", true, "WRIntCastleStoneColumn01"));
        Add(203, [2, 3], Floor("broken column", true, "RuinsSmallPillar"));
        One(203, 4, Floor("rubble", false, "RockPileM02CaveDirt01"));
        // 204 Clothing
        Add(204, [0, 6, 7, 8], Floor("cloth", false, "RuinsLinenPile01"));
        Add(204, [1, 2, 3, 4, 5], Floor("hat", false, "ClothesFarmHat01", "ClothesFarmHat02", "ClothesFineHat02", "ClothesMerchantHat"));
        One(204, 9, Floor("shoes", false, "ClothesBarKeeperShoes"));
        // 205 Containers
        One(205, 0, Floor("barrel", false, "Barrel02Static"));
        Add(205, [1, 5, 6, 7, 31, 32, 33, 34, 35], Floor("potion", false, Potions));
        Add(205, [2, 3, 4, 41, 43], Floor("pot", false, "GlazedPot01", "GlazedPot02"));
        Add(205, [8, 9, 10], Floor("basket", false, "Basket01", "Basket03", "Basket04"));
        Add(205, [11, 12, 13, 14, 15, 16], Floor("bottle", false, "WineBottle01AEmpty", "WineBottle02AEmpty"));
        Add(205, [17, 18, 19, 20], Floor("sack", false, "MiscSackLargeFlat01_NoRespawn", "MiscSack02LargeFlat_NoRespawn", "MiscSackLarge_NoRespawn"));
        Add(205, [21, 22, 23], Floor("chest", false, "TreasChestSmallEMPTYNoRespawn"));
        Add(205, [24, 25, 26], Floor("crates", false, "CrateSmall01EECo", "CrateSmall03EECo"));
        Add(205, [27, 28], Floor("cart", true, "HandCart01"));
        Add(205, [29, 30], Floor("bucket", false, "Bucket01", "Bucket02a"));
        One(205, 36, Floor("sack", false, "MiscSack02Small_NoRespawn"));
        Add(205, [37, 38, 39, 40], Floor("potted plant", true, Planters));
        One(205, 42, Floor("log", false, "Firewood01"));
        // 206 Death
        Add(206, [0, 1], Floor("skull", false, "BoneHumanSkull"));
        Add(206, [2, 7, 9], Impaled("BoneHumanSkullStatic"));
        Add(206, [3, 4, 5, 6], Floor("big skull", false, "BoneTrollSkull01"));
        Add(206, [8, 10, 22, 23, 24, 25], Group("bones", Mount.Floor, false, Bones));
        Add(206, [11, 12, 13, 15, 16], Impaled("BoneHumanBloodyRibcage"));
        Add(206, [14, 35], Group("remains", Mount.Floor, false, BloodyBones));
        Add(206, [17, 18], Impaled("BoneHumanBloodySkull"));
        One(206, 19, Floor("grave marker", true, "RTGravestone01"));
        Add(206, [20, 21], Floor("gravestone", true, "Tombstone01", "Tombstone02", "Tombstone03"));
        One(206, 26, Group("cage", Mount.Ceiling, true, new Part("MrkMetalCage01"), new Part("BoneHumanSkullStatic", OnTop: true)));
        One(206, 27, Floor("pike", true, "StockadePike01"));
        One(206, 28, Floor("dagger", false, "IronDagger"));
        One(206, 29, Floor("ribcage", false, "BoneHumanRibcageFull"));
        One(206, 30, Floor("arm bones", false, "BoneHumanArmR"));
        One(206, 31, Floor("leg bone", false, "BoneHumanLegR"));
        One(206, 32, Floor("horned skull", true, "BoneDeerSkullHornedStatic"));
        One(206, 36, Hang("hanging remains", true, "GiantHangingBones"));
        // 207 Equipment
        One(207, 0, Floor("sword", false, "IronSword"));
        One(207, 1, Floor("sword", false, "SteelSword"));
        Add(207, [2, 6], Floor("mace", false, "IronMace"));
        One(207, 3, Floor("dagger", false, "IronDagger"));
        One(207, 4, Floor("axe", false, "IronWarAxe"));
        One(207, 5, Floor("sword", false, "SteelSword"));
        One(207, 8, Floor("bow", false, "LongBow"));
        One(207, 9, Floor("armour", false, "ArmorIronCuirass"));
        One(207, 10, Floor("shield", false, "ArmorIronShield"));
        One(207, 11, Floor("gauntlets", false, "ArmorIronGauntlets"));
        Add(207, [12, 14], Floor("helmet", false, "ArmorIronHelmet"));
        One(207, 13, Floor("mail", false, "ArmorIronBandedCuirass"));
        One(207, 16, Floor("arrow", false, "IronArrow"));
        // 208 Exotica
        One(208, 2, Floor("alembic", false, "GlazedPot02"));
        // 209 Library: Daggerfall's own books
        Add(209, [0, 1, 2, 3, 4, 11, 12, 13, 14, 15], new Spec("book", Mount.Floor, false, [new Part(Book)]));
        Add(209, [5, 6, 7, 8, 9, 10], Floor("scroll", false, "PaperRoll"));
        // 211 Misc
        One(211, 1, Group("quill and ink", Mount.Floor, false, new Part("Inkwell01"), new Part("Quill01", Dx: 6)));
        One(211, 2, Floor("urn", false, "GlazedPot01"));
        Add(211, [4, 5, 6, 7], Hang("chain", true, "ShipAnchorChain01"));
        Add(211, [8, 9], Floor("fish", false, "FoodSalmon"));
        One(211, 11, Hang("hanging herbs", false, "HangingGarlicBraid"));
        Add(211, [15, 16, 17], Floor("wheel", false, "WagonWheel"));
        Add(211, [18, 19], Floor("stocks", true, "WRStocks01"));
        One(211, 29, Floor("pike", true, "StockadePike01"));
        One(211, 31, Floor("bread", false, "FoodBread01A"));
        One(211, 33, Hang("hanging remains", true, "GiantHangingBones"));
        One(211, 34, Hang("cage", true, "MrkMetalCage01"));
        One(211, 35, Floor("anvil", false, "BlacksmithAnvilStatic"));
        One(211, 36, Floor("hook", false, "Hook"));
        One(211, 40, Floor("meat", false, "FoodBread01B"));
        // 213 Plants
        One(213, 0, Floor("fruit", false, "FoodApple"));
        One(213, 1, Floor("tomato", false, "FoodTomato"));
        Add(213, [2, 3, 4, 5, 15, 16, 17], Floor("potted plant", true, Planters));
        One(213, 6, Floor("flowers", false, "FlowerBasketDrop"));
        Add(213, [7, 8, 9, 10], Hang("hanging roots", true, Vines));
        Add(213, [11, 12], Floor("logs", false, "Firewood01"));
        Add(213, [13, 14], Hang("hanging plant", false, "HangingElvesEar01"));
        // 214 Tools
        Add(214, [0, 1], Floor("shovel", false, "Shovel01", "Shovel02"));
        Add(214, [2, 3], Floor("hammer", false, "BlacksmithHammer01"));
        One(214, 6, Floor("pickaxe", false, "weapPickaxe"));
        One(214, 7, Floor("hoe", false, "Hoe"));
        One(214, 10, Floor("broom", false, "Broom01"));
        Add(214, [12, 13], Floor("tongs", false, "BlacksmithTongs01"));
        // 216 Treasure (fixed treasure in Daggerfall Unity)
        Add(216, [0, 1, 2, 4, 5], Coins(6));
        One(216, 3, Floor("gold bar", false, "IngotGold"));
        Add(216, [6, 7, 8, 9], Floor("circlet", false, "ClothesCirclet01", "ClothesCirclet05"));
        Add(216, [10, 11, 12, 13, 14, 15, 16, 17], Floor("gem", false, "GemDiamond", "GemEmerald", "GemSapphire", "GemRuby", "GemAmethyst", "GemGarnet"));
        Add(216, [18, 19], Coins(3, "GemRuby", "GemSapphire"));
        Add(216, [20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 32, 33, 35, 36, 38, 40, 41, 42, 43, 44, 45], Coins(10));
        Add(216, [30, 31, 37], Coins(15, "TGCoinpurseMed"));
        Add(216, [34, 39, 46, 47], Coins(20, "GemEmerald", "GemDiamond"));
        // 218 Pots and pans
        Add(218, [0, 1, 2, 3], Floor("pot", false, "GlazedPot01", "CastIronPotMedium01"));
        Add(218, [4, 5], Floor("pan", false, "CastIronPotSmall01"));
        One(218, 6, Floor("ladle", false, "WoodenLadle01"));
        // ---- Outdoors (town blocks) ----
        // 504 Temperate woodland nature: trees, bushes, rocks, plants (Daggerfall Unity swaps the archive by climate).
        One(504, 1, Floor("bush", true, "TreeReachBush01", "TreeReachBush02"));
        One(504, 2, Floor("flowering shrub", true, "TreeYellowShrub01", "TreeYellowShrub02"));
        Add(504, [3, 4, 5, 6], Floor("rocks", true, "RockPileM01FieldGrass01WR", "RockPileM02FieldGrass01WR", "RockPileL02FieldGrass01WR"));
        Add(504, [7, 8, 26, 29], Floor("fern", true, "TreeSwordFernCluster01", "TreeSwordFernCluster02", "TreeSwordFernCluster03"));
        One(504, 9, Floor("dead plant", true, "TreeDeadShrub"));
        Add(504, [10, 11], Floor("small tree", true, "TreeVineMaple01"));
        Add(504, [12, 14, 15, 16, 18], Floor("tree", true, "TreeAspen01", "TreeAspen02", "TreeAspen03", "TreeAspen04", "TreeReachTree01"));
        One(504, 17, Floor("autumn tree", true, "TreeAspen05", "TreeAspen06"));
        Add(504, [13, 25], Floor("pine", true, "TreePineForest01", "TreePineForest02", "TreePineForest03"));
        Add(504, [19, 20], Floor("stump", true, "TreePineForestCutStump01", "TreeAspenStump01"));
        Add(504, [21, 22], Floor("flowers", false, "TreeFloraMountainFlower01Purple", "TreeFloraMountainFlower01Blue", "TreeFloraMountainFlower01Red"));
        One(504, 23, Floor("mushroom", false, "FloraMushroom01", "FloraMushroom03"));
        Add(504, [24, 30], Floor("dead tree", true, "TreePineForest01Dead", "TreePineForest02Dead"));
        Add(504, [27, 28], Floor("big bush", true, "TreeFallForestShrub01", "TreeFallForestShrub02"));
        One(504, 31, Floor("fallen log", true, "FirewoodPileLarge01"));
        AddNature(t);
        // 97 Statues (people: robed figures, nobles, dancers) and 98 statues of monsters, fitted to the sprite's height.
        Add(97, [0, 3, 6, 7, 9], Floor("statue", true, "MageStatue"));
        Add(97, [1, 5, 18, 19, 20, 21], Floor("statue", true, "StatueTalos01"));
        Add(97, [2, 4, 8, 10], Floor("statue", true, "StatueMara01"));
        Add(97, [11, 12, 13, 14, 15, 16, 17], Floor("statue", true, "StatueDibella"));
        Add(98, [0, 2], Floor("monster statue", true, "NorDragonStatue01"));          // winged beasts
        Add(98, [1, 3, 4, 5, 6, 7, 8, 9, 12, 14], Floor("monster statue", true, "TG08StatueFalmer"));   // ghosts, skeletons, orcs
        // 212 Outdoor: wells, hay, fountains, signs, pumps, woodpiles, posts, scaffolds, standing stones.
        One(212, 0, Floor("well", true, "GenericWell01"));
        One(212, 1, Floor("haypile", true, "HayBale01"));
        Add(212, [2, 3], Floor("fountain", true, "RTPlazaWell01"));
        Add(212, [4, 5, 6], Floor("signpost", true, "RoadSignPost"));
        One(212, 7, Floor("rubble", false, "RockPileS01FieldGrass01WR"));
        Add(212, [8, 9], Floor("water pump", true, "FarmWell01"));
        Add(212, [10, 13, 14], Floor("post", true, "FarmBannerPost01"));
        Add(212, [11, 12], Floor("woodpile", false, "FirewoodPileMedium01", "FirewoodPileLarge01"));
        Add(212, [15, 16], Floor("scaffold", true, "StockadeScaffoldBase0Sided01"));
        Add(212, [17, 18], Floor("standing stone", true, "TG08StandingStone"));
        // 301 Crops: sheaves, stalks, sunflowers, berries, vegetables, flowers.
        Add(301, [0, 1, 6], Floor("sheaf", true, "HayBale01"));
        Add(301, [2, 7, 8, 9, 19, 21], Floor("wheat", false, "TreeFloraWheat01"));
        Add(301, [3, 4], Floor("sunflowers", false, "TreeFloraMountainFlower02Red"));
        Add(301, [5, 11, 20], Floor("berry bush", false, "TreeFloraJazBay01", "TreeFloraSnowberry01"));
        Add(301, [10, 12], Floor("vegetables", false, "TreeFloraPotatoPlant01", "TreeFloraLeek01"));
        Add(301, [13, 14, 15, 16, 17], Floor("flowers", false, "TreeFloraLavender01", "TreeFloraMountainFlower01Purple", "TreeFloraMountainFlower02Blue"));
        One(301, 18, Floor("cabbage", false, "TreeFloraCabbage01"));
        Add(301, [22, 23], Floor("grass", false, "TreeFloraSpikyGrass01", "TreeFloraSpikyGrass02"));
        // 300 Cave formations: stalactites (0-5) hang upside down, stalagmites stand.
        Add(300, [0, 1, 2, 3, 4, 5], new Spec("stalactite", Mount.UpsideDown, true, [new Part("RockS01Wet")], ["RockS01Wet", "RockS03Wet"]));
        Add(300, Enumerable.Range(6, 10), Floor("stalagmite", true, "RockS01Wet", "RockS03Wet"));
        // 100 "temp E3" flats: copies of death/dungeon sprites.
        One(100, 2, Floor("arm bones", false, "BoneHumanArmR"));
        One(100, 3, Floor("leg bone", false, "BoneHumanLegR"));
        One(100, 4, Group("cage", Mount.Ceiling, true, new Part("MrkMetalCage01"), new Part("BoneHumanSkullStatic", OnTop: true)));
        Add(100, [5, 6, 7, 8], Hang("chain", true, "ShipAnchorChain01"));
        One(100, 9, Floor("horned skull", true, "BoneDeerSkullHornedStatic"));
        One(100, 10, Group("remains", Mount.Floor, false, BloodyBones));
        Add(100, [11, 12], Impaled("BoneHumanBloodySkull"));
        Add(100, [13, 14, 15, 16], Floor("post", true, "StockadePike01"));
        One(100, 17, Group("bones", Mount.Floor, false, Bones));
        One(100, 18, Floor("ribcage", false, "BoneHumanRibcageFull"));
        Add(100, [19, 20, 21, 22], Hang("hanging roots", true, Vines));
        One(100, 23, Floor("skull", false, "BoneHumanSkull"));
        One(100, 24, Impaled("BoneHumanSkullStatic"));
        Add(100, [25, 26, 27], Floor("big skull", false, "BoneTrollSkull01"));
        Add(100, [28, 29], Impaled("BoneHumanBloodyRibcage"));
        return t;
    }

    /// <summary>
    /// Daggerfall's climate nature sets (500-511; Daggerfall Unity swaps 504 for the town's climate and to the _Snow set
    /// in winter), by eye from their contact sheets. Skyrim has no palms, cacti or giant mushrooms: palms are Reach trees,
    /// cacti thistle and juniper, giant mushrooms scaled mushrooms; snow sets take Skyrim's snow-covered kinds.
    /// </summary>
    static void AddNature(Dictionary<(int, int), Spec> t)
    {
        void Add(int archive, Spec spec, params int[] records) { foreach (var r in records) t[(archive, r)] = spec; }
        var bush = Floor("bush", true, "TreeReachBush01", "TreeReachBush02");
        var yellowBush = Floor("bush", true, "TreeYellowShrub01", "TreeYellowShrub02");
        var flowers = Floor("flowers", false, "TreeFloraMountainFlower01Purple", "TreeFloraMountainFlower01Blue", "TreeFloraMountainFlower01Red");
        var grass = Floor("grass", false, "TreeFloraSpikyGrass01", "TreeFloraSpikyGrass02");
        var fern = Floor("fern", true, "TreeSwordFernCluster01", "TreeSwordFernCluster02", "TreeSwordFernCluster03");
        var jungle = Floor("jungle plant", true, "TreeReachFern01", "TreeReachFern02", "TreeReachFern03");
        var rocks = Floor("rocks", true, "RockPileM01FieldGrass01WR", "RockPileM02FieldGrass01WR", "RockPileL02FieldGrass01WR");
        var tallRock = Floor("tall rock", true, "RockL01", "RockL02", "RockL03", "RockL04");
        var cairn = Floor("stacked stones", true, "RockCairn01", "RockCairn02", "RockCairn03");
        var twisted = Floor("twisted tree", true, "TreeReachTree01", "TreeReachTree02");
        var cliffTree = Floor("small tree", true, "TreeReachCliffTree01", "TreeReachCliffTree02");
        var maple = Floor("small tree", true, "TreeVineMaple01");
        var broad = Floor("tree", true, "TreeAspen01", "TreeAspen02", "TreeAspen03", "TreeAspen04");
        var autumn = Floor("autumn tree", true, "TreeAspen05", "TreeAspen06");
        var pine = Floor("pine", true, "TreePineForest01", "TreePineForest02", "TreePineForest03");
        var hollow = Floor("hollow trunk", true, "TreePineForestHollow01", "TreePineForestStump01");
        var dead = Floor("dead tree", true, "TreePineForest01Dead", "TreePineForest02Dead");
        var gnarled = Floor("gnarled tree", true, "TreeTundraDriftWoodTree01");
        var deadPlant = Floor("dead plant", true, "TreeDeadShrub");
        var bigBush = Floor("big bush", true, "TreeFallForestShrub01", "TreeFallForestShrub02");
        var mushroom = Floor("giant mushroom", true, "FloraMushroom01", "FloraMushroom03");
        var log = Floor("fallen log", true, "TreePineForestLogSm01", "TreePineForestLog01");
        var glow = Floor("glowing plant", true, "BlackreachBush01", "BlackreachBush02");
        var shrub = Floor("shrub", true, "TreeTundraShrub01", "TreeTundraShrub02", "TreeTundraShrub03");
        var desertPlant = Floor("desert plant", true, "TreeFloraThistle01", "TreeFloraJuniper01");
        var redPlant = Floor("red plant", false, "TreeFloraDragonsTongue01", "TreeFloraDragonsTongue02");
        var nightshade = Floor("nightshade", false, "TreeFloraNightshade01", "TreeFloraNightshade02");
        // Snow-covered kinds.
        var snowRocks = Floor("snowy rocks", true, "RockPileM01Snow01_LightSN", "RockPileL01Snow", "RockPileM02Snow");
        var snowTallRock = Floor("snowy rock", true, "RockL01Snow", "RockL02Snow", "RockL03Snow");
        var snowCairn = Floor("stacked stones", true, "RockCairn01Snow", "RockCairn02Snow");
        var snowPine = Floor("snowy pine", true, "TreePineForestSnow01", "TreePineForestSnow02", "TreePineForestSnow03");
        var snowDead = Floor("dead tree", true, "TreePineForestSnow01Dead", "TreePineForestSnow02Dead");
        var snowBroad = Floor("snowy tree", true, "TreeReachTree01Snow", "TreeVineMaple01Snow");
        var snowBush = Floor("snowy bush", true, "TreeThicket01Snow", "TreePineShrub01Snow");
        var snowShrub = Floor("snowy shrub", true, "TreeDeadShrubSnow");
        var snowHollow = Floor("hollow trunk", true, "TreePineForestHollow01Snow", "TreePineForestStump01Snow");
        var snowLog = Floor("fallen log", true, "TreePineForestLogSm01_HeavySN", "TreePineForestLog01Heavy_SN");
        var snowFern = Floor("snowy shrub", true, "TreePineShrub02Snow", "TreePineShrub03Snow");

        // 500 Rainforest.
        Add(500, bush, 1); Add(500, flowers, 2, 6, 20, 22); Add(500, cliffTree, 3); Add(500, tallRock, 4, 17); Add(500, jungle, 5, 11, 23, 24, 26);
        Add(500, grass, 7, 8); Add(500, fern, 9, 10); Add(500, twisted, 12, 13, 14, 15, 16, 21, 27); Add(500, broad, 18, 30); Add(500, rocks, 19, 25, 31);
        Add(500, glow, 28, 29);
        // 501 Subtropical.
        Add(501, twisted, 1, 11, 12, 13, 16, 27, 28, 30, 31); Add(501, jungle, 2, 14, 18, 21); Add(501, rocks, 3, 4, 5, 10); Add(501, tallRock, 6, 23);
        Add(501, grass, 7, 8); Add(501, fern, 9); Add(501, cliffTree, 15, 17, 20); Add(501, gnarled, 19, 24); Add(501, mushroom, 22);
        Add(501, flowers, 25, 26); Add(501, shrub, 29);
        // 502 Swamp.
        Add(502, redPlant, 1); Add(502, glow, 2, 22); Add(502, tallRock, 3); Add(502, deadPlant, 4, 9, 30); Add(502, cairn, 5, 6, 10);
        Add(502, grass, 7, 8); Add(502, rocks, 11); Add(502, twisted, 12, 13, 16, 17); Add(502, jungle, 14, 21, 23, 26, 27, 31);
        Add(502, gnarled, 15, 19, 24, 25); Add(502, maple, 18); Add(502, fern, 20, 28); Add(502, nightshade, 29);
        // 503 Desert.
        Add(503, desertPlant, 1, 13, 14, 15, 16, 17); Add(503, rocks, 2, 3, 20, 21, 22); Add(503, tallRock, 4, 18, 19); Add(503, cliffTree, 5);
        Add(503, deadPlant, 6, 10, 23); Add(503, grass, 7, 8); Add(503, yellowBush, 9, 31); Add(503, gnarled, 11, 12, 28, 30);
        Add(503, shrub, 24, 27, 29); Add(503, jungle, 25); Add(503, nightshade, 26);
        // 505 Temperate woodland in winter (504's records).
        Add(505, snowBush, 1, 27, 28); Add(505, flowers, 2, 21); Add(505, snowRocks, 3, 4, 5, 6); Add(505, grass, 7, 8); Add(505, snowShrub, 9, 22);
        Add(505, snowBroad, 10, 11, 12, 14, 16, 17, 18); Add(505, snowPine, 13, 25); Add(505, snowDead, 15, 24, 30); Add(505, snowHollow, 19, 20);
        Add(505, mushroom, 23); Add(505, snowFern, 26, 29); Add(505, snowLog, 31);
        // 506 Woodland hills, 507 in winter.
        Add(506, tallRock, 1, 3, 4, 6, 17, 18, 28); Add(506, flowers, 2, 21, 22); Add(506, pine, 5, 11, 12, 13, 25, 30); Add(506, jungle, 7, 23, 26);
        Add(506, grass, 8); Add(506, bush, 9); Add(506, cliffTree, 10); Add(506, broad, 14, 15, 16); Add(506, hollow, 19, 20); Add(506, dead, 24);
        Add(506, bigBush, 27); Add(506, fern, 29, 31);
        Add(507, snowTallRock, 1, 3, 4, 6, 17, 18, 28); Add(507, snowShrub, 2, 9, 22); Add(507, snowPine, 5, 11, 12, 13, 25); Add(507, snowFern, 7, 26, 29, 30, 31);
        Add(507, grass, 8); Add(507, snowBroad, 10, 14, 16); Add(507, snowDead, 15, 24); Add(507, snowHollow, 19, 20); Add(507, flowers, 21);
        Add(507, snowBush, 23, 27);
        // 508 Haunted woodlands, 509 in winter.
        Add(508, tallRock, 1, 3, 4, 5, 6); Add(508, yellowBush, 2); Add(508, grass, 7, 8); Add(508, deadPlant, 9, 17); Add(508, cliffTree, 10);
        Add(508, rocks, 12); Add(508, autumn, 13); Add(508, fern, 14, 26, 29); Add(508, broad, 15); Add(508, gnarled, 16, 18, 25); Add(508, hollow, 19, 20);
        Add(508, flowers, 21); Add(508, mushroom, 22, 23); Add(508, dead, 24, 30); Add(508, bigBush, 27, 28); Add(508, log, 31);
        Add(509, snowTallRock, 1, 3, 4, 5, 6); Add(509, snowShrub, 2, 9, 17); Add(509, grass, 7, 8); Add(509, snowBroad, 10); Add(509, snowRocks, 12);
        Add(509, snowDead, 13, 15, 16, 24, 30); Add(509, snowFern, 14, 26, 29); Add(509, gnarled, 18, 25); Add(509, snowHollow, 19, 20); Add(509, flowers, 21);
        Add(509, mushroom, 22, 23); Add(509, snowBush, 27, 28); Add(509, snowLog, 31);
        // 510 Mountains, 511 in winter.
        Add(510, tallRock, 1, 3, 4, 6, 14, 17, 18, 27, 28); Add(510, flowers, 2, 22); Add(510, pine, 5, 11, 12, 13, 25, 30); Add(510, jungle, 7, 23, 26);
        Add(510, grass, 8); Add(510, yellowBush, 9); Add(510, cliffTree, 10); Add(510, broad, 15); Add(510, gnarled, 16); Add(510, hollow, 19, 20);
        Add(510, maple, 21); Add(510, dead, 24); Add(510, fern, 29); Add(510, deadPlant, 31);
        Add(511, snowTallRock, 1, 3, 4, 6, 14, 17, 18, 27, 28); Add(511, snowShrub, 2, 9, 22, 31); Add(511, snowPine, 5, 11, 13, 25); Add(511, snowFern, 7, 26, 29, 30);
        Add(511, grass, 8); Add(511, snowBroad, 10, 21); Add(511, snowCairn, 12); Add(511, snowDead, 15, 24); Add(511, gnarled, 16); Add(511, snowHollow, 19, 20);
        Add(511, snowBush, 23);
    }
}
