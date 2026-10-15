namespace TamrielMerge.Daggerfall;

/// <summary>
/// Daggerfall's names for a town's buildings ("The Dancing Griffin", "Lord Mobar's Armory", "The Bank of Daggerfall"),
/// as Daggerfall Unity's FormulaHelper.GenerateBuildingName (MIT) makes them from the building's name seed in
/// MAPS.BSA: the generator seeded with it, the second part drawn before the first. The word lists are Daggerfall
/// Unity's Internal_Strings (from FALL.EXE): StoresA, TavernsA/B and each shop type's StoresB.
/// </summary>
public static class DfBuildingNames
{
    static readonly string[] StoresA =
    [
        "%ef's", "%cn's Best", "The Essential", "Lord %ef's", "The Adventurer's", "The Odd", "%ef's Finest", "Bargain", "Vintage",
        "The Emperor's", "%cn", "%ef's General", "The Superior", "%ef's Quality", "First Class", "The %rt's", "The Champion", "Doctor %ef's",
        "Lady %ef's",
    ];
    static readonly string[] TavernsA =
    [
        "The Queen's", "The King's", "The Dirty", "The Black", "The Mole and", "The Green", "The Red", "The Gold", "The White", "The Silver",
        "The Crimson", "The Flying", "The Dancing", "The Laughing", "The Restless", "The Thirsty", "The Unfortunate", "The Lucky",
        "The Devil's", "The Rusty", "The Howling", "The Screaming", "The Bat and", "The Lion and", "The Lynx and", "The Dwarf and",
        "The Beaver and", "The Fox and", "The Mouse and", "The Pig and", "The Feather and", "The Toad and", "The Rat and", "The Savage",
        "The Knave and", "The Dead",
    ];
    static readonly string[] TavernsB =
    [
        "Chasm", "Mug", "Pit", "Cat", "Dog", "Goblin", "Griffin", "Dragon", "Ogre", "Giant", "Djinn", "Wolf", "Huntsman", "Dagger", "Skull",
        "Sword", "Guard", "Dungeon", "Helm", "Castle", "Jug", "Bird", "Gnome", "Hedgehog", "Muskrat", "Woodchuck", "Scorpion", "Badger",
        "Goat", "Porcupine", "Priest", "Fawn", "Stag", "Barbarian", "Rascal", "Fairy",
    ];
    static readonly Dictionary<DfBuildingType, string[]> StoresB = new()
    {
        [DfBuildingType.GeneralStore] = ["Supplies", "Supply Store", "Gear", "Gear Store", "Equipment", "Equipment Store", "Sundries", "Provisions", "Merchandise", "General Store", "Retail Store", "Trading Post", "Market", "Wares", "Warehouse"],
        [DfBuildingType.WeaponSmith] = ["Weapons", "Weaponry", "Arms", "Armaments", "Arsenal", "Armsmaker", "Blades", "Blacksmith", "Metalsmith", "Weaponsmith"],
        [DfBuildingType.Armorer] = ["Armory", "Mail", "Shielding", "Armor", "Shields", "Aegis", "Metalworks", "Blacksmith", "Metalsmith", "Armorer", "Smith", "Smithy"],
        [DfBuildingType.Bookseller] = ["Books", "Bookstore", "Bookshop", "Book Dealer", "Book Center", "Bookseller", "Bookstall", "Incunabula"],
        [DfBuildingType.ClothingStore] = ["Clothing", "Clothes", "Garments", "Apparel", "Costumes", "Vestments", "Attire", "Fashion", "Tailoring", "Outfits", "Finery"],
        [DfBuildingType.Alchemist] = ["Herbs", "Potherbs", "Spices", "Remedies", "Antidotes", "Physics", "Medicines", "Potions", "Tinctures", "Medicaments", "Elixirs", "Pharmacy", "Apothecary", "Unguents", "Medicinal Agents", "Herb Garden", "Pharmaceuticals", "Chemistry", "Chemicals", "Experimental Products", "Alchemistry", "Alchemical Solutions", "Metallurgy"],
        [DfBuildingType.GemStore] = ["Gems", "Gemstones", "Jewelry", "Jewels", "Precious Stones", "Bijoutry", "Jewelers", "Jewel Box", "Jewelry Shop", "Gemcutter"],
        [DfBuildingType.PawnShop] = ["Pawnshop", "Pawnbrokers", "Used Supplies", "Used Gear", "Used Equipment", "Used Merchandise", "Hockshop", "Antiquities"],
        [DfBuildingType.FurnitureStore] = ["Furniture", "Furnishings", "Interior Design", "Furniture Shop", "Decor", "Carpentry", "Woodworking", "Crafts", "Woodwork"],
        [DfBuildingType.Library] = ["Library", "Bookroom", "Athenaeum", "Public Library", "Historians", "Bookroom", "Seminary", "Lyceum"],
    };

    static int Range(DfRandom r, int max) => (int)r.Rand() % max;

    /// <summary>Building types that carry a name (Daggerfall Unity RMBLayout.IsNamedBuilding).</summary>
    public static bool IsNamed(DfBuildingType t) => t is DfBuildingType.Alchemist or DfBuildingType.Armorer or DfBuildingType.Bank
        or (>= DfBuildingType.Bookseller and <= DfBuildingType.Palace);

    /// <summary>
    /// Each named building of a town with the location's own building record (Daggerfall Unity
    /// RMBLayout.GetLocationBuildingData): the town's MAPS.BSA records of named types are handed out in order, block by
    /// block, to the blocks' buildings of the same type; by (block x, block y, building index): name seed and faction.
    /// </summary>
    public static Dictionary<(int X, int Y, int Index), (int Seed, int Faction)> Assign(DfLocation loc, Func<string, DfTownBlock?> block)
    {
        var pool = loc.Buildings.Where(b => IsNamed((DfBuildingType)b.BuildingType)).Select(b => (B: b, Used: false)).ToList();
        var result = new Dictionary<(int, int, int), (int, int)>();
        for (int y = 0; y < loc.BlockHeight; y++)
            for (int x = 0; x < loc.BlockWidth; x++)
            {
                if (block(loc.ExteriorBlocks[y * loc.BlockWidth + x]) is not { } tb) continue;
                foreach (var b in tb.Buildings)
                {
                    if (!IsNamed(b.Data.Type)) continue;
                    int k = pool.FindIndex(p => !p.Used && (DfBuildingType)p.B.BuildingType == b.Data.Type);
                    if (k < 0) continue;
                    pool[k] = pool[k] with { Used = true };
                    result[(x, y, b.Index)] = (pool[k].B.NameSeed, pool[k].B.FactionId);
                }
            }
        return result;
    }

    /// <summary>The building's name, or null for houses (Daggerfall names those only in quests).</summary>
    public static string? Name(int seed, DfBuildingType type, int factionId, string town, int region, DfFactions factions, DfNames names, DfText? text)
    {
        var r = new DfRandom((uint)seed);
        string a, b = "";
        bool single = false;
        switch (type)
        {
            case DfBuildingType.HouseForSale: return "House for sale";
            case DfBuildingType.Tavern:
                b = TavernsB[Range(r, TavernsB.Length)];
                a = TavernsA[Range(r, TavernsA.Length)];
                break;
            case DfBuildingType.Bank:
                a = "The Bank of";
                b = region >= 0 && region < DfLocations.RegionNames.Length ? DfLocations.RegionNames[region] : "the Iliac Bay";
                break;
            case DfBuildingType.GuildHall:
                if (factions[factionId] is not { } guild) return null;
                a = guild.Name; single = true;
                break;
            case DfBuildingType.Temple:
                // The temple's name is its faction's first child's (Daggerfall Unity).
                if (factions.ById.Values.FirstOrDefault(f => f.Parent == factionId && factionId != 0) is not { } order) return null;
                a = order.Name; single = true;
                break;
            case DfBuildingType.Palace:
                int id = town switch { "Daggerfall" => 475, "Wayrest" => 476, "Sentinel" => 477, _ => 0 };
                a = id > 0 && text is not null && text[id].FirstOrDefault() is { Length: > 0 } castle ? castle.Split('\n')[0].Trim().TrimEnd('.') : "Palace";
                single = true;
                break;
            case DfBuildingType.Town23: return "City Wall";
            default:
                if (!StoresB.TryGetValue(type, out var list)) return null;
                b = list[Range(r, list.Length)];
                a = StoresA[Range(r, StoresA.Length)];
                break;
        }
        a = a.Replace("%cn", town);
        if (a.Contains("%ef"))
        {
            // Classic burns a draw before expanding the macro; the region's people's name bank (Daggerfall Unity's fix).
            r.Rand();
            bool redguard = region >= 0 && region < DfFactions.RegionIsRedguard.Length && DfFactions.RegionIsRedguard[region];
            a = a.Replace("%ef", names.FirstName(redguard ? DfNameBank.Redguard : DfNameBank.Breton, false, r));
        }
        a = a.Replace("%rt", "King");
        return single ? a : $"{a} {b}";
    }
}
