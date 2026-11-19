using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Plugins.Aspects;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's barter and vendors (docs/game-systems.md "Mercantile/barter formula, merchant gold"):
/// <list type="bullet">
/// <item>Every NPC with a barter service (AIDT services: weapons, armour, clothing, books, ingredients, picks, probes,
/// lights, apparatus, repair items, misc, spells, magic items, potions) is a Skyrim merchant: a vendor faction of its
/// own with a merchant chest in the holding cell MWVendorChests. The chest holds what Morrowind barters: the NPC's own
/// inventory (NPCO; a negative count restocks), the contents of the containers it owns in its cell and the items it
/// owns lying there (OpenMW mwgui/tradewindow.cpp TradeWindow::setPtr: getContainersOwnedBy, getItemsOwnedBy), a
/// spell tome for each spell a spell merchant sells (mwgui/spellbuyingwindow.cpp: its own spells of type Spell, at
/// cost x fSpellValueMult), and its barter gold: NPDT's gold (OpenMW MWClass::Npc::getBaseGold is the gold pool a
/// merchant trades with, not pocket money). What a vendor buys (Skyrim's vendor buy/sell list of VendorItem keywords)
/// follows its services, Morrowind's items carrying the keyword of their kind (<see cref="VendorKeywords"/>; an
/// enchanted item also MWVendorItemEnchanted, as OpenMW's Class::canSell lets enchanters buy any enchanted thing).</item>
/// <item>Restocking is Morrowind's (TMMwVendor): fBarterGoldResetDelay hours after the last restock the gold pool goes
/// back to the record's gold (TradeWindow::restock) and every restocking item is refilled to its count
/// (ContainerStore::restock); what the player sold stays. Skyrim's own 48-hour vendor reset is not used (the chest
/// doesn't respawn).</item>
/// <item>Prices are Morrowind's (TMMwBarterPrices, the hidden perk MWBarterPerk's Mod Buy Prices / Mod Sell Prices
/// set as the barter opens): OpenMW MechanicsManager::getBarterOffer from the vendor's disposition, both sides'
/// Mercantile, Luck, Personality and fatigue, improved by the best haggle Trading::haggle accepts for certain
/// (fBargainOfferBase, fBargainOfferMulti); Skyrim's menu has no offer and counter-offer, so the haggling itself,
/// its failures and iBarterFailDisposition can't be played. Each trade raises disposition by
/// iBarterSuccessDisposition, as an accepted offer does.</item>
/// <item>"Barter" (sBarter), a topic for anyone in MWVendors, opens the menu (TMMwBarter).</item>
/// </list>
/// </summary>
public sealed partial class MwPluginBuilder
{
    // AIDT services flags (OpenMW components/esm3/loadnpc.hpp ESM::NPC::Services; UESP Tes3Mod:NPC_, AIDT).
    internal const int ServiceWeapon = 0x1, ServiceArmor = 0x2, ServiceClothing = 0x4, ServiceBooks = 0x8, ServiceIngredients = 0x10,
        ServicePicks = 0x20, ServiceProbes = 0x40, ServiceLights = 0x80, ServiceApparatus = 0x100, ServiceRepairItems = 0x200,
        ServiceMisc = 0x400, ServiceSpells = 0x800, ServiceMagicItems = 0x1000, ServicePotions = 0x2000, ServiceTraining = 0x4000,
        ServiceSpellmaking = 0x8000, ServiceEnchanting = 0x10000, ServiceRepair = 0x20000;
    /// <summary>The services that are barter (OpenMW MWClass::Npc::getServices / TradeWindow: anyone with one of
    /// these trades; training, spellmaking, enchanting and repair are other services).</summary>
    const int BarterServices = 0x3FFF;

    static int Services(Tes3Record rec) => rec.Get("AIDT") is { Data.Length: >= 12 } aidt ? aidt.Int(8) : 0;

    /// <summary>Whether a Morrowind NPC barters (any barter service in AIDT).</summary>
    internal static bool IsVendor(Tes3Record rec) => rec.Type == "NPC_" && (Services(rec) & BarterServices) != 0;

    readonly List<(Npc Npc, Tes3Record Record)> vendorNpcs = [];
    Faction? vendorsTag;
    /// <summary>Everyone who barters (the Barter topic's condition).</summary>
    Faction VendorsTag => vendorsTag ??= Skyrim.CoinTrade.Tag(mod, "MWVendors", "Vendors");

    /// <summary>Vendors made (<see cref="FinishVendors"/>).</summary>
    public int Vendors { get; private set; }
    /// <summary>Spell tomes made for spell merchants.</summary>
    public int SpellTomes { get; private set; }

    /// <summary>Marks a person as a vendor as its actor is made (AddFactionAndClass); the shop is built by
    /// <see cref="FinishVendors"/> once every item and cell exists.</summary>
    void MakeVendor(Npc npc, Tes3Record rec)
    {
        if (!IsVendor(rec)) return;
        npc.Factions.Add(new RankPlacement { Faction = VendorsTag.ToLink<IFactionGetter>(), Rank = 0 });
        vendorNpcs.Add((npc, rec));
    }

    // ---- what Morrowind's items are to a vendor ----

    readonly Dictionary<string, FormKey?> vanillaKeywords = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, Keyword> vendorKeywords = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A Skyrim.esm keyword by EditorID (VendorItemWeapon...), or null when Skyrim.esm lacks it.</summary>
    FormKey? VanillaKeyword(string editorId)
    {
        if (vanillaKeywords.TryGetValue(editorId, out var hit)) return hit;
        var key = SkyrimEsm.Keywords.FirstOrDefault(x => x.EditorID == editorId)?.FormKey;
        if (key is null) Problems.Add($"vendors: Skyrim.esm has no keyword {editorId}");
        return vanillaKeywords[editorId] = key;
    }

    /// <summary>A keyword of ours for a kind of item Skyrim has no vendor keyword for (picks, probes, apparatus, repair
    /// items, enchanted items), made once.</summary>
    Keyword VendorKeyword(string editorId)
    {
        if (vendorKeywords.TryGetValue(editorId, out var k)) return k;
        k = new Keyword(mod) { EditorID = editorId };
        mod.Keywords.Add(k);
        return vendorKeywords[editorId] = k;
    }

    /// <summary>
    /// The vendor keywords of a converted item, by its Morrowind kind (OpenMW MWClass::*::canSell: a weapon sells to a
    /// weapon service, an enchanted one to a magic-items service too; books and scrolls to books; clothing, rings and
    /// amulets to clothing; apparatus, lockpicks, probes and repair items each to their own service; misc, potions and
    /// ingredients to theirs). Skyrim's vendor buy/sell lists are of these keywords, so Morrowind's vendors buy what
    /// Morrowind's would, and Skyrim's own merchants buy Morrowind's goods of their kind.
    /// </summary>
    void VendorKeywords(Tes3Record rec, IMajorRecord? item)
    {
        if (item is not IKeyworded<IKeywordGetter> keyworded) return;
        var keys = new List<FormKey?>();
        switch (rec.Type)
        {
            case "WEAP": keys.Add(VanillaKeyword("VendorItemWeapon")); break;
            case "ARMO": keys.Add(VanillaKeyword("VendorItemArmor")); break;
            case "CLOT":
                keys.Add(VanillaKeyword("VendorItemClothing"));
                // CTDT type 8 ring, 9 amulet: jewellery as well, which Skyrim's jewellers buy.
                if (rec.Get("CTDT") is { Data.Length: >= 4 } ct && ct.Int(0) is 8 or 9) keys.Add(VanillaKeyword("VendorItemJewelry"));
                break;
            case "BOOK": keys.Add(VanillaKeyword(item is Scroll ? "VendorItemScroll" : "VendorItemBook")); break;
            case "INGR": keys.Add(VanillaKeyword("VendorItemIngredient")); break;
            case "ALCH": keys.Add(VanillaKeyword("VendorItemPotion")); break;
            case "MISC": keys.Add(VanillaKeyword(item is SoulGem ? "VendorItemSoulGem" : "VendorItemMisc")); break;
            case "LOCK": keys.Add(VendorKeyword("MWVendorItemPick").FormKey); break;
            case "PROB": keys.Add(VendorKeyword("MWVendorItemProbe").FormKey); break;
            case "APPA": keys.Add(VendorKeyword("MWVendorItemApparatus").FormKey); break;
            case "REPA": keys.Add(VendorKeyword("MWVendorItemRepair").FormKey); break;
            default: return;
        }
        if (rec.Str("ENAM") is { Length: > 0 }) keys.Add(VendorKeyword("MWVendorItemEnchanted").FormKey);
        foreach (var key in keys)
        {
            if (key is not { } k) continue;
            keyworded.Keywords ??= [];
            if (!keyworded.Keywords.Any(x => x.FormKey == k)) keyworded.Keywords.Add(new FormLink<IKeywordGetter>(k));
        }
    }

    readonly Dictionary<int, FormList> vendorLists = [];

    /// <summary>The buy/sell list for a set of barter services: the keywords of what those services deal in (Skyrim's
    /// own kinds under misc, where Morrowind's misc covers them: clutter, gems, ingots, hides, animal parts, tools).</summary>
    FormList VendorList(int services)
    {
        services &= BarterServices;
        if (vendorLists.TryGetValue(services, out var hit)) return hit;
        var list = new FormList(mod) { EditorID = $"MWVendorItems{services:X4}", Items = [] };
        void AddVanilla(string edid) { if (VanillaKeyword(edid) is { } k) list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(k)); }
        void AddOwn(string edid) => list.Items.Add(VendorKeyword(edid).ToLink<ISkyrimMajorRecordGetter>());
        if ((services & ServiceWeapon) != 0) AddVanilla("VendorItemWeapon");
        if ((services & ServiceArmor) != 0) AddVanilla("VendorItemArmor");
        if ((services & ServiceClothing) != 0) { AddVanilla("VendorItemClothing"); AddVanilla("VendorItemJewelry"); }
        if ((services & ServiceBooks) != 0) { AddVanilla("VendorItemBook"); AddVanilla("VendorItemScroll"); }
        if ((services & ServiceIngredients) != 0) AddVanilla("VendorItemIngredient");
        if ((services & ServicePicks) != 0) AddOwn("MWVendorItemPick");
        if ((services & ServiceProbes) != 0) AddOwn("MWVendorItemProbe");
        if ((services & ServiceApparatus) != 0) AddOwn("MWVendorItemApparatus");
        if ((services & ServiceRepairItems) != 0) AddOwn("MWVendorItemRepair");
        if ((services & ServiceMisc) != 0)
            foreach (var edid in new[] { "VendorItemMisc", "VendorItemClutter", "VendorItemGem", "VendorItemOreIngot", "VendorItemAnimalHide", "VendorItemAnimalPart", "VendorItemTool", "VendorItemSoulGem" })
                AddVanilla(edid);
        if ((services & ServiceSpells) != 0) AddVanilla("VendorItemSpellTome");
        if ((services & ServiceMagicItems) != 0) { AddOwn("MWVendorItemEnchanted"); AddVanilla("VendorItemStaff"); }
        if ((services & ServicePotions) != 0) { AddVanilla("VendorItemPotion"); AddVanilla("VendorItemPoison"); }
        // Lights: Morrowind's lights aren't converted as items; a lights service buys nothing more.
        mod.FormLists.Add(list);
        return vendorLists[services] = list;
    }

    // ---- spell tomes ----

    readonly Dictionary<string, Book?> spellTomes = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Vanilla spell tomes by school (the ActorValue's number; -1 any), for their look.</summary>
    readonly Dictionary<int, IBookGetter> tomeTemplates = [];

    /// <summary>A spell tome teaching a Morrowind spell (a spell merchant's stock), or null: only spells of type Spell
    /// are sold (OpenMW SpellBuyingWindow::startSpellBuying), at cost x fSpellValueMult (SpellBuyingWindow::addSpell;
    /// the barter offer then applies to it as to any price). Made once per spell.</summary>
    Book? SpellTome(string spellId)
    {
        if (spellTomes.TryGetValue(spellId, out var hit)) return hit;
        spellTomes[spellId] = null;
        if (!world.Objects.TryGetValue(spellId, out var rec) || rec.Type != "SPEL" || rec.Get("SPDT") is not { Data.Length: >= 8 } d || d.Int(0) != 0) return null;
        if (MwSpell(spellId) is not { } spell) return null;
        string name = rec.Str("FNAM") ?? spellId;
        var tome = mod.Books.DuplicateInAsNewRecord(TomeTemplate(spell));
        tome.EditorID = "MWSpellTome" + Sanitize(spellId);
        tome.Name = "Spell Tome: " + name;
        tome.Teaches = new BookSpell { Spell = spell.ToLink<ISpellGetter>() };
        tome.Value = (uint)Math.Max(1, (int)(d.Int(4) * Setting("fSpellValueMult", 5)));
        tome.BookText = name;
        tome.VirtualMachineAdapter = null;
        SpellTomes++;
        return spellTomes[spellId] = tome;
    }

    /// <summary>A vanilla spell tome of the spell's school (its first effect's skill), for its look.</summary>
    IBookGetter TomeTemplate(Spell spell)
    {
        int school = -1;
        if (spell.Effects.FirstOrDefault()?.BaseEffect.FormKey is { } fx)
        {
            var effect = mod.MagicEffects.FirstOrDefault(e => e.FormKey == fx) is { } own ? own : (IMagicEffectGetter?)SkyrimEsm.MagicEffects.FirstOrDefault(e => e.FormKey == fx);
            if (effect is not null) school = (int)effect.MagicSkill;
        }
        if (tomeTemplates.Count == 0)
        {
            foreach (var book in SkyrimEsm.Books.Where(b => b.Teaches is IBookSpellGetter && (b.EditorID ?? "").StartsWith("SpellTome", StringComparison.Ordinal)))
            {
                var taught = SkyrimEsm.Spells.FirstOrDefault(s => s.FormKey == ((IBookSpellGetter)book.Teaches!).Spell.FormKey);
                var first = taught?.Effects.FirstOrDefault()?.BaseEffect.FormKey;
                int sk = first is { } f && SkyrimEsm.MagicEffects.FirstOrDefault(e => e.FormKey == f) is { } me ? (int)me.MagicSkill : -1;
                tomeTemplates.TryAdd(sk, book);
                tomeTemplates.TryAdd(-1, book);
            }
        }
        return tomeTemplates.GetValueOrDefault(school) ?? tomeTemplates[-1];
    }

    // ---- the shops ----

    static readonly FormKey SkyrimGoldKey = new(ModKey.FromFileName("Skyrim.esm"), 0x00000F);
    Skyrim.ModelStandIns? vendorStandIns;
    Perk? barterPerk;
    GlobalFloat? barterActive;
    Quest? barterQuest;

    /// <summary>
    /// Builds every vendor's shop (call from Write, after the actors and items): the holding cell MWVendorChests with a
    /// merchant chest per vendor, its faction, its buy/sell list and TMMwVendor; the barter quest MWBarter
    /// (TMMwBarterPrices, the hidden perk MWBarterPerk, the player alias TMMwBarterTrades).
    /// </summary>
    void FinishVendors()
    {
        if (vendorNpcs.Count == 0) return;
        var quest = BarterQuest();
        var holding = new Cell(mod)
        {
            EditorID = "MWVendorChests",
            Name = "Morrowind vendor chests",
            Flags = Cell.Flag.IsInteriorCell,
            LightingTemplate = LightingTemplate.ToLink<ILightingTemplateGetter>(),
            Persistent = [],
            Temporary = [],
        };
        AddInteriorCell(holding);
        var stats = new MwStats(world);
        float resetDelay = Setting("fBarterGoldResetDelay", 24);
        var gold = new FormLink<IItemGetter>(SkyrimGoldKey);
        // What a person keeps on it: its weapons and armour (Morrowind's clothing is Skyrim armour too).
        var gear = mod.Weapons.Select(w => w.FormKey).Concat(mod.Armors.Select(a => a.FormKey)).ToHashSet();
        int n = 0;
        foreach (var (npc, rec) in vendorNpcs)
        {
            int services = Services(rec);
            string edid = Sanitize(rec.Id);
            string name = rec.Str("FNAM") ?? rec.Id;
            var chest = new Container(mod)
            {
                EditorID = "MWVendorChest" + edid,
                Name = name + "'s wares",
                ObjectBounds = new ObjectBounds(),
                Items = [],
                Model = new Model { File = (vendorStandIns ??= new Skyrim.ModelStandIns(SkyrimEsm)).Container },
            };
            var restock = new List<(FormKey Item, int Count)>();
            void Stock(string itemId, int count)
            {
                // Coins among the goods aren't bartered (OpenMW's TradeItemModel leaves gold out); the gold pool is NPDT's.
                if (CoinsIn(itemId) > 0 || Item(itemId) is not { } item) return;
                int stack = StackOf(itemId, Math.Abs(count));
                chest.Items!.Add(new ContainerEntry { Item = new ContainerItem { Item = new FormLink<IItemGetter>(item.FormKey), Count = stack } });
                // A negative count restocks (Morrowind's inventory lists; OpenMW ContainerStore::restock).
                if (count < 0) restock.Add((item.FormKey, stack));
            }
            foreach (var f in rec.All("NPCO"))
                Stock(Tes3Plugin.Encoding.GetString(f.Data, 4, f.Data.Length - 4).TrimEnd('\0'), f.Int(0));
            // The containers it owns in its cell, and its items lying there (TradeWindow::setPtr); every cell is looked
            // through, as Morrowind's vendors stand in their own.
            foreach (var cell in world.Interiors.Values.Concat(world.Exteriors.Values))
                foreach (var r in cell.Refs)
                {
                    if (r.Owner is null || !r.Owner.Equals(rec.Id, StringComparison.OrdinalIgnoreCase) || !world.Objects.TryGetValue(r.BaseId, out var b)) continue;
                    if (b.Type == "CONT")
                        foreach (var f in b.All("NPCO"))
                            Stock(Tes3Plugin.Encoding.GetString(f.Data, 4, f.Data.Length - 4).TrimEnd('\0'), f.Int(0));
                    else if (IsItemType(b.Type)) Stock(b.Id, 1);
                }
            // A spell merchant's spells, always in stock.
            if ((services & ServiceSpells) != 0)
                foreach (var spellId in rec.All("NPCS").Select(f => f.Text))
                    if (SpellTome(spellId) is { } tome)
                    {
                        chest.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = tome.ToLink<IItemGetter>(), Count = 1 } });
                        restock.Add((tome.FormKey, 1));
                    }
            // NPDT's gold: byte 48 of the full record, byte 8 of the auto-calculated one (OpenMW Npc::getBaseGold).
            var npdt = rec.Get("NPDT");
            int barterGold = npdt is { } g ? (g.Data.Length == 12 ? g.Int(8) : g.Data.Length >= 52 ? g.Int(48) : 0) : 0;
            if (barterGold > 0) chest.Items.Add(new ContainerEntry { Item = new ContainerItem { Item = gold, Count = barterGold } });
            mod.Containers.Add(chest);
            var placed = new PlacedObject(mod)
            {
                EditorID = "MWVendorChestRef" + edid,
                Base = chest.ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = new P3Float(n % 16 * 128f, n / 16 * 128f, 0), Rotation = new P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            holding.Persistent.Add(placed);
            n++;
            // The goods are the chest's: the person keeps its gear (weapons, armour, clothing: what it wears and wields)
            // and any coin of its own, not a second copy of its stock.
            npc.Items?.RemoveAll(e => e.Item.Item.FormKey != SkyrimGoldKey && !gear.Contains(e.Item.Item.FormKey));
            var vendor = new Faction(mod)
            {
                EditorID = "MWVendor" + edid,
                Name = name,
                Flags = Faction.FactionFlag.Vendor | Faction.FactionFlag.HiddenFromPC,
                Ranks = [new Rank { Number = 0 }],
                // Morrowind's merchants trade at any hour.
                VendorValues = new VendorValues { StartHour = 0, EndHour = 24, Radius = 0 },
                VendorBuySellList = VendorList(services).ToNullableLink<IFormListGetter>(),
                MerchantContainer = placed.ToNullableLink<IPlacedObjectGetter>(),
            };
            mod.Factions.Add(vendor);
            npc.Factions.Add(new RankPlacement { Faction = vendor.ToLink<IFactionGetter>(), Rank = 0 });
            var own = stats.Of(rec.Id);
            npc.VirtualMachineAdapter ??= new VirtualMachineAdapter();
            var items = new ScriptObjectListProperty { Name = "RestockItems" };
            foreach (var (item, _) in restock) items.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(item) });
            npc.VirtualMachineAdapter.Scripts.Add(new ScriptEntry
            {
                Name = "TMMwVendor",
                Properties =
                [
                    new ScriptObjectProperty { Name = "Chest", Object = placed.ToLink<ISkyrimMajorRecordGetter>() },
                    new ScriptObjectProperty { Name = "Gold", Object = new FormLink<ISkyrimMajorRecordGetter>(SkyrimGoldKey) },
                    new ScriptObjectProperty { Name = "BarterQuest", Object = quest.ToLink<ISkyrimMajorRecordGetter>() },
                    new ScriptIntProperty { Name = "BarterGold", Data = barterGold },
                    items,
                    new ScriptIntListProperty { Name = "RestockCounts", Data = [.. restock.Select(r => r.Count)] },
                    new ScriptFloatProperty { Name = "ResetDelay", Data = resetDelay },
                    // The record's Mercantile (skill 24), Luck and Personality: TMMwState adds what scripts changed.
                    new ScriptIntProperty { Name = "Mercantile", Data = own?[8 + 24] ?? 0 },
                    new ScriptIntProperty { Name = "Luck", Data = own?[7] ?? 0 },
                    new ScriptIntProperty { Name = "Personality", Data = own?[6] ?? 0 },
                ],
            });
            Vendors++;
        }
    }

    /// <summary>The hidden perk whose Mod Buy Prices (entry 0) and Mod Sell Prices (entry 1) are set to Morrowind's price
    /// terms while a Morrowind barter is open (MWBarterActive 1): Set Value, so Skyrim's own Speech-based multiplier
    /// is replaced, not scaled. Each entry's condition is on the perk owner (tab 0).</summary>
    Perk BarterPerk()
    {
        if (barterPerk is not null) return barterPerk;
        barterPerk = new Perk(mod)
        {
            EditorID = "MWBarterPerk",
            Name = "Morrowind barter",
            Playable = false,
            Hidden = true,
            NumRanks = 1,
        };
        foreach (var entryPoint in new[] { APerkEntryPointEffect.EntryType.ModBuyPrices, APerkEntryPointEffect.EntryType.ModSellPrices })
        {
            var active = new GetGlobalValueConditionData(); active.Global = new FormLinkOrIndex<IGlobalGetter>(active, BarterActive().FormKey);
            var owner = new PerkCondition { RunOnTabIndex = 0 };
            owner.Conditions.Add(new ConditionFloat { CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1, Data = active });
            var entry = new PerkEntryPointModifyValue
            {
                EntryPoint = entryPoint,
                PerkConditionTabCount = 3,
                Modification = PerkEntryPointModifyValue.ModificationType.Set,
                Value = 1,
            };
            entry.Conditions.Add(owner);
            barterPerk.Effects.Add(entry);
        }
        mod.Perks.Add(barterPerk);
        return barterPerk;
    }

    /// <summary>1 while a Morrowind vendor's barter is open (the perk's condition).</summary>
    GlobalFloat BarterActive()
    {
        if (barterActive is not null) return barterActive;
        barterActive = new GlobalFloat(mod) { EditorID = "MWBarterActive", Data = 0 };
        mod.Globals.Add(barterActive);
        return barterActive;
    }

    /// <summary>The quest MWBarter: TMMwBarterPrices with Morrowind's settings, and the player alias whose
    /// TMMwBarterTrades counts the trades (disposition).</summary>
    Quest BarterQuest()
    {
        if (barterQuest is not null) return barterQuest;
        static ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };
        var prices = new ScriptEntry
        {
            Name = "TMMwBarterPrices",
            Properties =
            [
                Obj("BarterPerk", BarterPerk().FormKey),
                Obj("Active", BarterActive().FormKey),
                new ScriptFloatProperty { Name = "BargainOfferBase", Data = Setting("fBargainOfferBase", 50) },
                new ScriptFloatProperty { Name = "BargainOfferMulti", Data = Setting("fBargainOfferMulti", -4) },
                new ScriptFloatProperty { Name = "DispositionMod", Data = Setting("fDispositionMod", 1) },
                new ScriptFloatProperty { Name = "FatigueBase", Data = Setting("fFatigueBase", 1.25f) },
                new ScriptFloatProperty { Name = "FatigueMult", Data = Setting("fFatigueMult", 0.5f) },
                new ScriptIntProperty { Name = "SuccessDisposition", Data = SettingInt("iBarterSuccessDisposition", 1) },
            ],
        };
        // The player's Morrowind stats (Mercantile, Luck, Personality) live on TMMwState when the scripts are converted.
        if (scriptWorld is not null) prices.Properties.Add(Obj("MwState", StateQuest().FormKey));
        barterQuest = new Quest(mod)
        {
            EditorID = "MWBarter",
            Name = "Morrowind barter",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 0,
            Type = Quest.TypeEnum.None,
            Aliases = [new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(PlayerRefKey) }],
            NextAliasID = 1,
        };
        barterQuest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts = [prices],
            Aliases =
            [
                new QuestFragmentAlias
                {
                    Property = new ScriptObjectProperty { Object = barterQuest.ToLink<ISkyrimMajorRecordGetter>(), Alias = 0 },
                    Scripts = [new ScriptEntry { Name = "TMMwBarterTrades", Properties = [Obj("Gold", SkyrimGoldKey), Obj("Active", BarterActive().FormKey)] }],
                },
            ],
        };
        mod.Quests.Add(barterQuest);
        return barterQuest;
    }

    /// <summary>"Barter" (sBarter): the topic that opens a vendor's barter menu (TMMwBarter), for anyone in MWVendors.
    /// Morrowind's vendors say nothing to it, so the response is silent, as Companion Share's is.</summary>
    void BarterTopic()
    {
        string name = world.Settings.TryGetValue("sBarter", out var gmst) && gmst.Str("STRV") is { Length: > 0 } t ? t : "Barter";
        var branch = new DialogBranch(mod)
        {
            EditorID = "MWBarterBranch",
            Quest = dialogueQuest!.ToLink<IQuestGetter>(),
            Category = DialogBranch.CategoryType.Player,
            Flags = DialogBranch.Flag.TopLevel,
        };
        var topic = new DialogTopic(mod)
        {
            EditorID = "MWBarterTopic",
            Name = name,
            Quest = dialogueQuest!.ToNullableLink<IQuestGetter>(),
            Branch = branch.ToNullableLink<IDialogBranchGetter>(),
            Priority = 50,
            Category = DialogTopic.CategoryEnum.Topic,
            Subtype = DialogTopic.SubtypeEnum.Custom,
            Responses = [],
        };
        branch.StartingTopic = topic.ToNullableLink<IDialogTopicGetter>();
        var inIt = new GetInFactionConditionData(); inIt.Faction = new FormLinkOrIndex<IFactionGetter>(inIt, VendorsTag.FormKey);
        var open = new DialogResponses(mod)
        {
            Conditions = [Cond(inIt, CompareOperator.EqualTo, 1)],
            Responses = [new DialogResponse { Text = "", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
            Flags = new DialogResponseFlags { Flags = DialogResponses.Flag.Goodbye },
            VirtualMachineAdapter = new DialogResponsesAdapter
            {
                Scripts = [new ScriptEntry { Name = "TMMwBarter" }],
                ScriptFragments = new ScriptFragments
                {
                    ExtraBindDataVersion = 2,
                    FileName = "TMMwBarter",
                    OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMMwBarter", FragmentName = "Fragment_End" },
                },
            },
        };
        topic.Responses.Add(open);
        mod.DialogBranches.Add(branch);
        mod.DialogTopics.Add(topic);
        DialogueTopics++;
    }
}
