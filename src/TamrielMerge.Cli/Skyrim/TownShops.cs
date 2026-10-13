using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's shops as Skyrim merchants: in each shop (and tavern) one keeper — the person dressed as a merchant,
/// barkeep or smith, else the first in the room — gets a vendor faction of their own with a merchant chest under the
/// floor, stocked and trading like the nearest vanilla merchant (blacksmith for armorers and weaponsmiths, apothecary
/// for alchemists, innkeeper for taverns, Urag's books for booksellers, tailor, jeweller, general goods), open 6:00 to
/// 18:00 as Daggerfall's shops are. "Let me see your wares." opens Skyrim's barter menu (TMDfBarter). Mages Guild halls
/// sell Daggerfall's spell tomes (its spell merchant serves everyone) and temples sell potions, like an apothecary.
/// </summary>
public sealed class TownShops(SkyrimMod mod, ISkyrimModGetter skyrim)
{
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);

    /// <summary>The vanilla merchant a shop trades like: its chest (contents) and its buy/sell list.</summary>
    static (FormKey Chest, FormKey Items)? Template(DfBuildingType t) => t switch
    {
        DfBuildingType.Armorer or DfBuildingType.WeaponSmith => (Sk(0x072786), Sk(0x066333)),                 // MerchantFalkreathBlacksmithChest, VendorItemsBlacksmith
        DfBuildingType.Alchemist => (Sk(0x06A876), Sk(0x09379F)),                                              // MerchantFalkreathGraveConcoctionsChest, VendorItemsApothecary
        DfBuildingType.Tavern => (Sk(0x065C36), Sk(0x0914F0)),                                                 // MerchantRiftenBeeandBarbTalenChest, VendorItemsInnkeeper
        DfBuildingType.Bookseller or DfBuildingType.Library => (Sk(0x10C430), Sk(0x10C42E)),                   // MerchantWCollegeUragChest, VendorItemsCollegeUrag
        DfBuildingType.ClothingStore => (Sk(0x0C644F), Sk(0x0937A0)),                                          // misc chest, VendorItemsTailor
        DfBuildingType.GemStore => (Sk(0x0C644F), Sk(0x0937A7)),                                               // misc chest, VendorItemsJeweler
        DfBuildingType.GeneralStore or DfBuildingType.PawnShop or DfBuildingType.FurnitureStore => (Sk(0x0C644F), Sk(0x06CB48)),   // VendorItemsMisc
        _ => null,
    };

    readonly Dictionary<FormKey, Container> chests = new();
    Container? spellChest;
    const int MagesGuild = 40;

    /// <summary>Daggerfall's spell tomes, for the Mages Guild's spell merchant.</summary>
    public IReadOnlyCollection<Book>? SpellTomes { get; init; }

    Container? SpellChest()
    {
        if (spellChest is not null || SpellTomes is not { Count: > 0 } tomes) return spellChest;
        spellChest = new Container(mod) { EditorID = "DFShopChestSpellTomes", ObjectBounds = new ObjectBounds(), Items = [] };
        foreach (var tome in tomes)
            spellChest.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = tome.ToLink<IItemGetter>(), Count = 1 } });
        mod.Containers.Add(spellChest);
        return spellChest;
    }
    Faction? shopkeepers;
    public int Shops { get; private set; }

    /// <summary>All shopkeepers (the barter line's condition).</summary>
    public Faction Shopkeepers => shopkeepers ??= NewFaction("DFShopkeepers", "Shopkeepers");
    Faction? tavernKeepers;
    /// <summary>Tavern keepers (who rent rooms).</summary>
    public Faction TavernKeepers => tavernKeepers ??= NewFaction("DFTavernKeepers", "Tavern keepers");
    /// <summary>The bed Daggerfall's beds become (PropReplacer), which tavern rooms are made of.</summary>
    public static readonly FormKey Bed = Sk(0x30091);

    Faction NewFaction(string edid, string name)
    {
        var f = new Faction(mod) { EditorID = edid, Name = name, Ranks = [new Rank { Number = 0 }] };
        mod.Factions.Add(f);
        return f;
    }

    /// <summary>Daggerfall's weapons and armor (DfItems' leveled lists), which its armorers and weaponsmiths sell
    /// instead of Skyrim's.</summary>
    public (FormKey Weapons, FormKey Armor)? DaggerfallSmith { get; init; }

    Container Chest(FormKey template)
    {
        if (chests.TryGetValue(template, out var hit)) return hit;
        if (template == Sk(0x072786) && DaggerfallSmith is { } lists)
        {
            var smith = new Container(mod) { EditorID = "DFShopChestDaggerfallSmith", ObjectBounds = new ObjectBounds(), Items = [] };
            smith.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = lists.Weapons.ToLink<IItemGetter>(), Count = 10 } });
            smith.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = lists.Armor.ToLink<IItemGetter>(), Count = 10 } });
            mod.Containers.Add(smith);
            return chests[template] = smith;
        }
        var source = skyrim.Containers.First(c => c.FormKey == template);
        var copy = mod.Containers.DuplicateInAsNewRecord(source);
        copy.EditorID = "DFShopChest" + source.EditorID;
        copy.VirtualMachineAdapter = null;
        return chests[template] = copy;
    }

    Faction? bankers;
    /// <summary>Bank keepers (the bank's business: TMMoneyChanger with its accounts, letters of credit and loans).</summary>
    public Faction Bankers => bankers ??= CoinTrade.Tag(mod, "DFBankers", "Bankers");

    /// <summary>Makes the shop's keeper a merchant (a bank's, its banker). Returns the keeper, or null for buildings
    /// that aren't shops.</summary>
    public TownPeople.Person? Equip(Cell interior, string shopId, DfBuildingType type, IReadOnlyList<TownPeople.Person> people, int factionId = -1)
    {
        if (people.Count == 0) return null;
        if (type == DfBuildingType.Bank)
        {
            var banker = KeeperOf(people);
            banker.Npc.Factions.Add(new RankPlacement { Faction = Bankers.ToLink<IFactionGetter>(), Rank = 0 });
            return banker;
        }
        // A Mages Guild hall's spell merchant (Urag's buy list: books); a temple's apothecary.
        var spells = type == DfBuildingType.GuildHall && factionId == MagesGuild ? SpellChest() : null;
        (FormKey Chest, FormKey Items)? template = spells is not null ? (spells.FormKey, Sk(0x10C42E))
            : type == DfBuildingType.Temple ? Template(DfBuildingType.Alchemist) : Template(type);
        if (template is not { } t) return null;
        var keeper = KeeperOf(people);
        var chest = new PlacedObject(mod)
        {
            EditorID = "DFShopChest" + shopId,
            Base = (spells ?? Chest(t.Chest)).ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = new P3Float(0, 0, -3000), Rotation = new P3Float(0, 0, 0) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        interior.Persistent.Add(chest);
        var vendor = NewFaction("DFShop" + shopId, keeper.Name + "'s shop");
        vendor.Flags |= Faction.FactionFlag.Vendor;
        vendor.VendorValues = new VendorValues { StartHour = 6, EndHour = 18, Radius = 0 };        vendor.VendorBuySellList = new FormLinkNullable<IFormListGetter>(t.Items);
        vendor.MerchantContainer = chest.ToNullableLink<IPlacedObjectGetter>();
        keeper.Npc.Factions.Add(new RankPlacement { Faction = vendor.ToLink<IFactionGetter>(), Rank = 0 });
        keeper.Npc.Factions.Add(new RankPlacement { Faction = Shopkeepers.ToLink<IFactionGetter>(), Rank = 0 });
        if (type == DfBuildingType.Tavern)
        {
            // A tavern's beds are the house's until a room is rented (TMDfTavern).
            keeper.Npc.Factions.Add(new RankPlacement { Faction = TavernKeepers.ToLink<IFactionGetter>(), Rank = 0 });
            foreach (var bed in interior.Temporary.OfType<PlacedObject>().Where(r => r.Base.FormKey == Bed || DfPropBuilder.Beds.Contains(r.Base.FormKey)))
                bed.Owner.SetTo(vendor.FormKey);
        }
        Shops++;
        return keeper;
    }

    /// <summary>Who keeps a shop: the person dressed as a merchant, barkeep or smith, else the first in the room.</summary>
    static TownPeople.Person KeeperOf(IReadOnlyList<TownPeople.Person> people) =>
        people.FirstOrDefault(p => TownPeople.Looks.Values.Count > 0 && p.Npc.DefaultOutfit.FormKeyNullable is { } o
            && (o == Sk(0x095921) || o == Sk(0x09593C) || o == Sk(0x0958AA) || o == Sk(0x0958AB))) ?? people[0];
}
