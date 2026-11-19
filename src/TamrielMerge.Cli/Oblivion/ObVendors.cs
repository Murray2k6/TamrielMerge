using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's merchants (2026-11-10: there were none; an Oblivion shopkeeper had its topics and nothing to trade, user:
/// "dialogue does not work right and npc interactions"). Everyone whose AI data offers a barter service (weapons, armour,
/// clothing, books, ingredients, lights, apparatus, miscellaneous, magic items, potions) is a Skyrim merchant:
/// <list type="bullet">
/// <item>a vendor faction of its own, trading at any hour, buying what its services deal in (Skyrim's VendorItem keywords,
/// which the converted items are given by their kind in <see cref="FinishVendors"/>; an enchanted weapon or armour piece
/// also OBVendorItemEnchanted, what a magic-items service buys);</item>
/// <item>a merchant chest in the holding cell OBVendorChests with what Oblivion's merchant sells: the contents of the
/// containers it owns (Oblivion's merchant chests, which stay where they are in the world as well) and what it carries
/// besides what it wears and wields;</item>
/// <item>its barter gold (the NPC record's), which in Oblivion is what it can pay for one item and never runs out: the
/// chest's gold is set back to it each time the barter opens (TMObVendor);</item>
/// <item>"Barter", a topic for anyone in OBVendors, which opens the menu (TMObBarter). Silent, as Repair is.</item>
/// </list>
/// Not carried: Oblivion's haggle slider and its prices by Mercantile and disposition (Skyrim's prices by Speech apply),
/// the hours a merchant offers services (its packages' Offers Services flag), and spell selling.
/// </summary>
public sealed partial class ObPluginBuilder
{
    static readonly string[] BarterServiceNames = ["Weapons", "Armor", "Clothing", "Books", "Ingredients", "Lights", "Apparatus", "Miscellaneous", "MagicItems", "Potions"];

    readonly List<(Npc Npc, Ob.INpcGetter Source, string Services)> vendorNpcs = [];
    readonly Dictionary<string, FormList> vendorLists = new();
    readonly Dictionary<string, Keyword> vendorKeywords = new();
    Faction? vendorsTag;

    /// <summary>Everyone who barters (the Barter topic's condition).</summary>
    Faction VendorsTag => vendorsTag ??= CoinTrade.Tag(mod, "OBVendors", "Vendors");

    /// <summary>Merchants made (<see cref="FinishVendors"/>).</summary>
    public int Vendors { get; private set; }
    /// <summary>Converted items given a vendor keyword of their kind.</summary>
    public int VendorKeyworded { get; private set; }

    /// <summary>The barter services of an NPC's AI data, by name (the flags are read by name, as Training and Repair are).</summary>
    static string BarterServices(Ob.INpcGetter n)
    {
        string all = n.AIData?.BuySellServices.ToString() ?? "";
        var parts = all.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(",", BarterServiceNames.Where(s => parts.Contains(s)));
    }

    /// <summary>Marks a person as a merchant as its actor is made; the shop is built by <see cref="FinishVendors"/>.</summary>
    void MakeVendor(Npc npc, Ob.INpcGetter n)
    {
        string services = BarterServices(n);
        if (services.Length == 0) return;
        npc.Factions.Add(new RankPlacement { Faction = VendorsTag.ToLink<IFactionGetter>(), Rank = 0 });
        vendorNpcs.Add((npc, n, services));
    }

    FormKey? VendorVanilla(string editorId) =>
        Vanilla.Get<IKeywordGetter>(editorId) is { } k && string.Equals(k.EditorID, editorId, StringComparison.OrdinalIgnoreCase) ? k.FormKey : null;

    Keyword VendorOwn(string editorId)
    {
        if (vendorKeywords.TryGetValue(editorId, out var k)) return k;
        k = new Keyword(mod) { EditorID = editorId };
        mod.Keywords.Add(k);
        return vendorKeywords[editorId] = k;
    }

    /// <summary>The buy/sell list for a set of services: the keywords of what they deal in.</summary>
    FormList VendorList(string services)
    {
        if (vendorLists.TryGetValue(services, out var hit)) return hit;
        var list = new FormList(mod) { EditorID = "OBVendorItems" + Sanitize(services.Replace(",", "")), Items = [] };
        void Add(params string[] edids)
        {
            foreach (var e in edids)
                if (VendorVanilla(e) is { } k && !list.Items.Any(i => i.FormKey == k)) list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(k));
        }
        foreach (var s in services.Split(','))
            switch (s)
            {
                case "Weapons": Add("VendorItemWeapon", "VendorItemArrow"); break;
                case "Armor": Add("VendorItemArmor"); break;
                case "Clothing": Add("VendorItemClothing", "VendorItemJewelry"); break;
                case "Books": Add("VendorItemBook", "VendorItemScroll", "VendorItemRecipe"); break;
                case "Ingredients": Add("VendorItemIngredient", "VendorItemFood", "VendorItemFoodRaw"); break;
                case "Apparatus": Add("VendorItemTool"); break;
                case "Miscellaneous": Add("VendorItemClutter", "VendorItemGem", "VendorItemOreIngot", "VendorItemAnimalHide", "VendorItemAnimalPart", "VendorItemTool", "VendorItemSoulGem", "VendorItemKey"); break;
                case "MagicItems":
                    list.Items.Add(VendorOwn("OBVendorItemEnchanted").ToLink<ISkyrimMajorRecordGetter>());
                    Add("VendorItemStaff", "VendorItemSoulGem", "VendorItemScroll");
                    break;
                case "Potions": Add("VendorItemPotion", "VendorItemPoison"); break;
                // Lights: Oblivion's torches are Skyrim's torch (no vendor keyword of its own); nothing more is bought.
            }
        mod.FormLists.Add(list);
        return vendorLists[services] = list;
    }

    /// <summary>Gives every converted item without one the vendor keyword of its kind, so merchants (Oblivion's here and
    /// Skyrim's own) buy it as what it is.</summary>
    void KeywordItems()
    {
        var known = Vanilla.Skyrim.Keywords.Where(k => k.EditorID?.StartsWith("VendorItem", StringComparison.OrdinalIgnoreCase) == true).Select(k => k.FormKey).ToHashSet();
        var enchanted = VendorOwn("OBVendorItemEnchanted");
        void Give<T>(IEnumerable<T> recs, Func<T, string> kind, Func<T, bool>? isEnchanted = null) where T : class, IKeyworded<IKeywordGetter>
        {
            foreach (var r in recs)
            {
                r.Keywords ??= [];
                if (!r.Keywords.Any(k => known.Contains(k.FormKey)) && VendorVanilla(kind(r)) is { } key)
                {
                    r.Keywords.Add(new FormLink<IKeywordGetter>(key));
                    VendorKeyworded++;
                }
                if (isEnchanted?.Invoke(r) == true && !r.Keywords.Any(k => k.FormKey == enchanted.FormKey)) r.Keywords.Add(enchanted.ToLink<IKeywordGetter>());
                if (r.Keywords.Count == 0) r.Keywords = null;
            }
        }
        Give(mod.Weapons, w => w.Data?.AnimationType == WeaponAnimationType.Staff ? "VendorItemStaff" : "VendorItemWeapon", w => !w.ObjectEffect.IsNull);
        Give(mod.Ammunitions, _ => "VendorItemArrow");
        Give(mod.Armors, a =>
        {
            var slots = a.BodyTemplate?.FirstPersonFlags ?? 0;
            if ((slots & (BipedObjectFlag.Ring | BipedObjectFlag.Amulet)) != 0) return "VendorItemJewelry";
            return a.BodyTemplate?.ArmorType == ArmorType.Clothing ? "VendorItemClothing" : "VendorItemArmor";
        }, a => !a.ObjectEffect.IsNull);
        Give(mod.Books, _ => "VendorItemBook");
        Give(mod.Scrolls, _ => "VendorItemScroll");
        Give(mod.Ingredients, _ => "VendorItemIngredient");
        Give(mod.Ingestibles, i => i.Flags.HasFlag(Ingestible.Flag.Poison) ? "VendorItemPoison" : i.Flags.HasFlag(Ingestible.Flag.FoodItem) ? "VendorItemFood" : "VendorItemPotion");
        Give(mod.SoulGems, _ => "VendorItemSoulGem");
        Give(mod.Keys, _ => "VendorItemKey");
        Give(mod.MiscItems, _ => "VendorItemClutter");
    }

    /// <summary>
    /// Builds every merchant's shop (from Write, once every actor, item and cell exists): the holding cell OBVendorChests
    /// with a chest per merchant, its faction and buy/sell list, TMObVendor, and the vendor keywords on the items.
    /// </summary>
    void FinishVendors()
    {
        if (vendorNpcs.Count == 0) return;
        KeywordItems();
        // The containers each merchant owns, anywhere in Oblivion's cells (its merchant chests; a later plugin's version of a
        // reference wins).
        var merchants = vendorNpcs.Select(v => v.Source.FormKey).ToHashSet();
        var ownedRefs = new Dictionary<FormKey, (FormKey Owner, FormKey Base)>();
        foreach (var plugin in plugins)
            foreach (var r in plugin.EnumerateMajorRecords<Ob.IPlacedObjectGetter>())
            {
                if (r.Owner.IsNull || !merchants.Contains(r.Owner.FormKey)) { ownedRefs.Remove(r.FormKey); continue; }
                ownedRefs[r.FormKey] = (r.Owner.FormKey, r.Base.FormKey);
            }
        var chestsOf = ownedRefs.Values.Where(v => records.GetValueOrDefault(v.Base) is Ob.IContainerGetter)
            .GroupBy(v => v.Owner).ToDictionary(g => g.Key, g => g.Select(v => (Ob.IContainerGetter)records[v.Base]).ToList());

        var holding = new Cell(mod)
        {
            EditorID = "OBVendorChests",
            Name = "Oblivion vendor chests",
            Flags = Cell.Flag.IsInteriorCell,
            Persistent = [],
            Temporary = [],
        };
        AddInteriorCell(holding);
        var gold = new FormLink<IItemGetter>(SkyrimGold);
        var gear = mod.Weapons.Select(w => w.FormKey).Concat(mod.Armors.Select(a => a.FormKey)).ToHashSet();
        int n = 0;
        foreach (var (npc, source, services) in vendorNpcs)
        {
            string edid = Sanitize(source.EditorID ?? source.FormKey.ID.ToString("X6"));
            string name = source.Name ?? edid;
            var chest = new Container(mod)
            {
                EditorID = "OBVendorChest" + edid,
                Name = name + "'s wares",
                ObjectBounds = new ObjectBounds(),
                Items = [],
                Model = new Model { File = StandIns.Container },
            };
            void Stock(FormKey obItem, int count)
            {
                if (obItem == ObGold || Item(obItem) is not { } item) return;
                var key = item.FormKey;
                if (chest.Items!.FirstOrDefault(e => e.Item.Item.FormKey == key) is { } have) have.Item.Count += Math.Max(1, count);
                else chest.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = new FormLink<IItemGetter>(key), Count = Math.Max(1, count) } });
            }
            foreach (var c in chestsOf.GetValueOrDefault(source.FormKey) ?? [])
                foreach (var i in c.Items) Stock(i.Item.FormKey, (int)i.Count);
            // What it carries besides its gear and its own coin is its stock too, sold from the chest, not twice.
            if (npc.Items is { } carried)
                foreach (var e in carried.Where(e => e.Item.Item.FormKey != SkyrimGold && !gear.Contains(e.Item.Item.FormKey)).ToList())
                {
                    if (chest.Items.FirstOrDefault(x => x.Item.Item.FormKey == e.Item.Item.FormKey) is { } have) have.Item.Count += e.Item.Count;
                    else chest.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = e.Item.Item, Count = e.Item.Count } });
                    carried.Remove(e);
                }
            if (npc.Items is { Count: 0 }) npc.Items = null;
            int barterGold = (int)(source.Configuration?.BarterGold ?? 0);
            if (barterGold > 0) chest.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = gold, Count = barterGold } });
            mod.Containers.Add(chest);
            var placed = new PlacedObject(mod)
            {
                EditorID = "OBVendorChestRef" + edid,
                Base = chest.ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = new P3Float(n % 16 * 128f, n / 16 * 128f, 0), Rotation = new P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            holding.Persistent.Add(placed);
            n++;
            var vendor = new Faction(mod)
            {
                EditorID = "OBVendor" + edid,
                Name = name,
                Flags = Faction.FactionFlag.Vendor | Faction.FactionFlag.HiddenFromPC,
                Ranks = [new Rank { Number = 0 }],
                VendorValues = new VendorValues { StartHour = 0, EndHour = 24, Radius = 0 },
                VendorBuySellList = VendorList(services).ToNullableLink<IFormListGetter>(),
                MerchantContainer = placed.ToNullableLink<IPlacedObjectGetter>(),
            };
            mod.Factions.Add(vendor);
            npc.Factions.Add(new RankPlacement { Faction = vendor.ToLink<IFactionGetter>(), Rank = 0 });
            npc.VirtualMachineAdapter ??= new VirtualMachineAdapter();
            npc.VirtualMachineAdapter.Scripts.Add(new ScriptEntry
            {
                Name = "TMObVendor",
                Properties =
                [
                    new ScriptObjectProperty { Name = "Chest", Object = placed.ToLink<ISkyrimMajorRecordGetter>() },
                    new ScriptObjectProperty { Name = "Gold", Object = new FormLink<ISkyrimMajorRecordGetter>(SkyrimGold) },
                    new ScriptIntProperty { Name = "BarterGold", Data = barterGold },
                ],
            });
            Vendors++;
        }
    }

    /// <summary>"Barter": the topic that opens a merchant's barter menu (TMObBarter), for anyone in OBVendors.</summary>
    void BarterTopic()
    {
        if (vendorNpcs.Count == 0 || obDialogueQuest is null) return;
        var branch = new DialogBranch(mod)
        {
            EditorID = "OBBarterBranch",
            Quest = obDialogueQuest.ToLink<IQuestGetter>(),
            Category = DialogBranch.CategoryType.Player,
            Flags = DialogBranch.Flag.TopLevel,
        };
        var topic = new DialogTopic(mod)
        {
            EditorID = "OBBarterTopic",
            Name = GmstText("sBarter", "Barter"),
            Quest = obDialogueQuest.ToNullableLink<IQuestGetter>(),
            Branch = branch.ToNullableLink<IDialogBranchGetter>(),
            Priority = 50,
            Category = DialogTopic.CategoryEnum.Topic,
            Subtype = DialogTopic.SubtypeEnum.Custom,
            Responses = [],
        };
        branch.StartingTopic = topic.ToNullableLink<IDialogTopicGetter>();
        var inIt = new GetInFactionConditionData();
        inIt.Faction = new FormLinkOrIndex<IFactionGetter>(inIt, VendorsTag.FormKey);
        topic.Responses.Add(new DialogResponses(mod)
        {
            Conditions = [new ConditionFloat { Data = inIt, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 }],
            Responses = [new DialogResponse { Text = "", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
            Flags = new DialogResponseFlags { Flags = DialogResponses.Flag.Goodbye },
            VirtualMachineAdapter = new DialogResponsesAdapter
            {
                Scripts = [new ScriptEntry { Name = "TMObBarter" }],
                ScriptFragments = new ScriptFragments
                {
                    ExtraBindDataVersion = 2,
                    FileName = "TMObBarter",
                    OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMObBarter", FragmentName = "Fragment_End" },
                },
            },
        });
        mod.DialogBranches.Add(branch);
        mod.DialogTopics.Add(topic);
        DialogueTopics++;
    }
}
