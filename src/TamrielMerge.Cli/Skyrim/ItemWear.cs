using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim.Progression;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Item durability and repair, the records one game's plugin needs (docs/game-systems.md; the rules are the scripts'
/// TMWear, TMWearMw, TMWearOb). Skyrim has no condition on items, so a quest keeps it for the player's weapons and armour:
/// <list type="bullet">
/// <item>The maximum per item is in form lists of its binary digits (<c>&lt;prefix&gt;WearMaxBit&lt;k&gt;</c>) beside a list of
/// them all. A form list and a parallel Int[] was the simple choice, but Papyrus arrays stop at 128 and each game has
/// thousands of these items, while a form list has no limit and HasForm is native. It costs one HasForm a digit at most (13
/// or so), the first time an item is hit; TMWear keeps the number while the item is damaged.</item>
/// <item>Twenty-five band lists (<c>&lt;prefix&gt;WearBand&lt;b&gt;</c>: b twentieths of the maximum, 0 to 125%) and a hidden perk
/// with a Mod Armor Rating and a Mod Attack Damage entry a band, on the item tab, each with the game's multiplier. Skyrim can't
/// scale one item, but this does per item, so each hand and each piece has its own, and needs nothing after a load: what
/// AddForm adds stays in the save (SKSE's SetNthEntryValue would not).</item>
/// <item>The repair tools: ingestibles whose one effect runs TMWearTool, as the alchemy apparatus are.</item>
/// <item>A message of a button per worn slot, shown by globals (Skyrim's buttons are fixed text), and the Repair topic.</item>
/// </list>
/// </summary>
public sealed class ItemWear
{
    const int FullBand = 20, TopBand = 25;
    static readonly FormKey PlayerKey = new(ModKey.FromFileName("Skyrim.esm"), 0x14);
    static readonly FormKey GoldKey = new(ModKey.FromFileName("Skyrim.esm"), 0x0F);
    static readonly FormKey ActorTypeNpcKey = new(ModKey.FromFileName("Skyrim.esm"), 0x013794);
    static readonly FormKey PotionUseSound = new(ModKey.FromFileName("Skyrim.esm"), 0x0B6435);
    /// <summary>Buttons of the first message, by slot (TMWear.Choose): the last two are the extra button and Cancel.</summary>
    static readonly (string Text, int Slot)[] FirstPage =
        [("Right hand", 0), ("Left hand", 1), ("Head", 2), ("Body", 3), ("Legs", 4), ("Feet", 5), ("Hands", 9), ("Shield", 10)];
    /// <summary>Morrowind's other armour slots, on a second message (a message box takes ten buttons).</summary>
    static readonly (string Text, int Slot)[] SecondPage = [("Left pauldron", 6), ("Right pauldron", 7), ("Left gauntlet", 8)];
    const int SlotCount = 11;

    readonly SkyrimMod mod;
    readonly string prefix;
    readonly List<(FormKey Item, int Max)> items = [];

    public ItemWear(SkyrimMod mod, string prefix)
    {
        this.mod = mod;
        this.prefix = prefix;
        Quest = new Quest(mod)
        {
            EditorID = prefix + "Wear",
            Name = "Item condition",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 50,
            Type = Quest.TypeEnum.None,
        };
        mod.Quests.Add(Quest);
    }

    /// <summary>The quest holding the game's TMWear script (its properties filled by <see cref="Finish"/>).</summary>
    public Quest Quest { get; }
    public int Items => items.Count;
    public int Tools { get; private set; }

    /// <summary>A weapon or armour piece with its maximum condition; none means it never wears.</summary>
    public void Add(IMajorRecord item, int maxCondition)
    {
        if (maxCondition > 0) items.Add((item.FormKey, maxCondition));
    }

    /// <summary>A repair tool as a usable item (an ingestible whose effect runs TMWearTool, which gives it back and opens the
    /// choice of what to mend), so it stays in shops and containers as an item.</summary>
    public Ingestible Tool(string edid, string? name, float weight, uint value, ObjectBounds bounds, string model, float quality, int uses)
    {
        var mgef = new MagicEffect(mod)
        {
            EditorID = edid + "Use",
            Name = name,
            Flags = MagicEffect.Flag.HideInUI | MagicEffect.Flag.NoDuration | MagicEffect.Flag.Painless,
            Archetype = new MagicEffectArchetype { Type = MagicEffectArchetype.TypeEnum.Script },
            CastType = CastType.FireAndForget,
            TargetType = TargetType.Self,
            MenuDisplayObject = new FormLinkNullable<IStaticGetter>(),
            Description = "",
        };
        mod.MagicEffects.Add(mgef);
        var item = new Ingestible(mod)
        {
            EditorID = edid, Name = name, Weight = weight, Value = value, ObjectBounds = bounds,
            ConsumeSound = new FormLink<ISoundDescriptorGetter>(PotionUseSound),
            Flags = Ingestible.Flag.NoAutoCalc | Ingestible.Flag.Medicine,
            Effects = [new Effect { BaseEffect = mgef.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData { Magnitude = 0, Area = 0, Duration = 0 } }],
        };
        item.Model = new Model { File = model };
        mod.Ingestibles.Add(item);
        mgef.VirtualMachineAdapter = new VirtualMachineAdapter
        {
            Scripts =
            [
                new ScriptEntry
                {
                    Name = "TMWearTool",
                    Properties =
                    [
                        Obj("Item", item.FormKey),
                        Obj("Wear", Quest.FormKey),
                        new ScriptFloatProperty { Name = "Quality", Data = quality },
                        new ScriptIntProperty { Name = "Uses", Data = uses },
                    ],
                },
            ],
        };
        Tools++;
        return item;
    }

    /// <summary>"Repair": a silent topic for the members of <paramref name="people"/> that opens TMWear.Service when the
    /// conversation closes, as the Barter topic opens the barter menu.</summary>
    public void RepairTopic(Quest dialogue, Faction people, string name)
    {
        var branch = new DialogBranch(mod)
        {
            EditorID = prefix + "RepairBranch",
            Quest = dialogue.ToLink<IQuestGetter>(),
            Category = DialogBranch.CategoryType.Player,
            Flags = DialogBranch.Flag.TopLevel,
        };
        var topic = new DialogTopic(mod)
        {
            EditorID = prefix + "RepairTopic",
            Name = name,
            Quest = dialogue.ToNullableLink<IQuestGetter>(),
            Branch = branch.ToNullableLink<IDialogBranchGetter>(),
            Priority = 50,
            Category = DialogTopic.CategoryEnum.Topic,
            Subtype = DialogTopic.SubtypeEnum.Custom,
            Responses = [],
        };
        branch.StartingTopic = topic.ToNullableLink<IDialogTopicGetter>();
        var inIt = new GetInFactionConditionData();
        inIt.Faction = new FormLinkOrIndex<IFactionGetter>(inIt, people.FormKey);
        topic.Responses.Add(new DialogResponses(mod)
        {
            Conditions = [new ConditionFloat { Data = inIt, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 }],
            Responses = [new DialogResponse { Text = "", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
            Flags = new DialogResponseFlags { Flags = DialogResponses.Flag.Goodbye },
            VirtualMachineAdapter = new DialogResponsesAdapter
            {
                Scripts = [new ScriptEntry { Name = "TMWearService", Properties = [Obj("Wear", Quest.FormKey)] }],
                ScriptFragments = new ScriptFragments
                {
                    ExtraBindDataVersion = 2,
                    FileName = "TMWearService",
                    OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMWearService", FragmentName = "Fragment_End" },
                },
            },
        });
        mod.DialogBranches.Add(branch);
        mod.DialogTopics.Add(topic);
    }

    static ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };

    FormList NewList(string edid)
    {
        var list = new FormList(mod) { EditorID = edid, Items = [] };
        mod.FormLists.Add(list);
        return list;
    }

    /// <summary>Builds the lists, the perk, the messages and the quest's script (call when every item exists).</summary>
    /// <param name="script">The game's script, TMWearMw or TMWearOb.</param>
    /// <param name="extraButton">The button after the slots: "More" with a second page, else "Repair all".</param>
    /// <param name="armorMult">The armour rating's multiplier at a share of the maximum.</param>
    /// <param name="weaponMult">The weapon damage's multiplier at a share of the maximum.</param>
    /// <param name="sounds">The repair's and the failure's sound markers, if the game has them.</param>
    /// <param name="properties">The game's own settings.</param>
    public void Finish(string script, string extraButton, bool secondPage, Func<float, float> armorMult, Func<float, float> weaponMult,
        (FormKey? Repair, FormKey? Fail) sounds, string brokenEquip, string nothingToRepair, string notEnoughGold, IEnumerable<ScriptProperty> properties)
    {
        // The maximum's binary digits.
        var all = NewList(prefix + "WearAll");
        int bits = 1;
        while (items.Count > 0 && (1 << bits) <= items.Max(i => i.Max)) bits++;
        var maxBits = Enumerable.Range(0, bits).Select(k => NewList($"{prefix}WearMaxBit{k}")).ToList();
        foreach (var (item, max) in items)
        {
            all.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(item));
            for (int k = 0; k < bits; k++)
                if ((max >> k & 1) != 0) maxBits[k].Items.Add(new FormLink<ISkyrimMajorRecordGetter>(item));
        }
        // The bands and their perk.
        var perk = new Perk(mod) { EditorID = prefix + "WearPerk", Name = "Item condition", Description = "", Playable = false, Hidden = true, NumRanks = 1, Effects = [] };
        var bands = new List<FormList>();
        for (int b = 0; b <= TopBand; b++)
        {
            if (b == FullBand) continue;
            var band = NewList($"{prefix}WearBand{b}");
            bands.Add(band);
            float share = b / (float)FullBand;
            perk.Effects.Add(BandEntry(APerkEntryPointEffect.EntryType.ModArmorRating, armorMult(share), band));
            perk.Effects.Add(BandEntry(APerkEntryPointEffect.EntryType.ModAttackDamage, weaponMult(share), band));
        }
        mod.Perks.Add(perk);
        // The menus.
        var shown = Enumerable.Range(0, SlotCount + 1).Select(i =>
        {
            var g = new GlobalFloat(mod) { EditorID = $"{prefix}WearShown{i}", Data = 0 };
            mod.Globals.Add(g);
            return g;
        }).ToList();
        var menu = NewMessage(prefix + "WearMenu");
        foreach (var (text, slot) in FirstPage) menu.MenuButtons.Add(Button(text, shown[slot]));
        menu.MenuButtons.Add(Button(extraButton, shown[SlotCount]));
        menu.MenuButtons.Add(new MessageButton { Text = "Cancel" });
        var entry = new ScriptEntry { Name = script };
        if (secondPage)
        {
            var more = NewMessage(prefix + "WearMenuMore");
            foreach (var (text, slot) in SecondPage) more.MenuButtons.Add(Button(text, shown[slot]));
            more.MenuButtons.Add(new MessageButton { Text = "Back" });
            more.MenuButtons.Add(new MessageButton { Text = "Cancel" });
            entry.Properties.Add(Obj("MenuMore", more.FormKey));
        }
        // The script.
        entry.Properties.Add(Obj("PlayerRef", PlayerKey));
        entry.Properties.Add(Obj("WearPerk", perk.FormKey));
        entry.Properties.Add(Obj("All", all.FormKey));
        entry.Properties.Add(ObjList("MaxBits", maxBits.Select(l => l.FormKey)));
        entry.Properties.Add(ObjList("Bands", bands.Select(l => l.FormKey)));
        entry.Properties.Add(Obj("Menu", menu.FormKey));
        entry.Properties.Add(ObjList("Shown", shown.Select(g => g.FormKey)));
        entry.Properties.Add(Obj("Gold", GoldKey));
        entry.Properties.Add(Obj("ActorNpc", ActorTypeNpcKey));
        if (sounds.Repair is { } repair) entry.Properties.Add(Obj("RepairSound", repair));
        if (sounds.Fail is { } fail) entry.Properties.Add(Obj("FailSound", fail));
        entry.Properties.Add(new ScriptStringProperty { Name = "BrokenEquip", Data = brokenEquip });
        entry.Properties.Add(new ScriptStringProperty { Name = "NothingToRepair", Data = nothingToRepair });
        entry.Properties.Add(new ScriptStringProperty { Name = "NotEnoughGold", Data = notEnoughGold });
        foreach (var property in properties) entry.Properties.Add(property);
        Quest.Aliases.Clear();
        Quest.Aliases.Add(new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(PlayerKey) });
        Quest.NextAliasID = 1;
        Quest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts = { entry },
            Aliases =
            [
                new QuestFragmentAlias
                {
                    Property = new ScriptObjectProperty { Object = Quest.ToLink<ISkyrimMajorRecordGetter>(), Alias = 0 },
                    Scripts = [new ScriptEntry { Name = "TMWearPlayer", Properties = [] }],
                },
            ],
        };
    }

    static ScriptObjectListProperty ObjList(string name, IEnumerable<FormKey> keys) =>
        new() { Name = name, Objects = [.. keys.Select(k => new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(k) })] };

    /// <summary>An entry multiplying the value for the items (tab 1) in the band.</summary>
    static PerkEntryPointModifyValue BandEntry(APerkEntryPointEffect.EntryType point, float mult, FormList band)
    {
        var inBand = new IsInListConditionData();
        inBand.FormList = new FormLinkOrIndex<IFormListGetter>(inBand, band.FormKey);
        var tab = new PerkCondition { RunOnTabIndex = 1 };
        tab.Conditions.Add(new ConditionFloat { Data = inBand, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 });
        var entry = new PerkEntryPointModifyValue
        {
            EntryPoint = point,
            PerkConditionTabCount = ProgressionPerks.Tabs(point),
            Modification = PerkEntryPointModifyValue.ModificationType.Multiply,
            Value = mult,
            Rank = 0,
            Priority = 0,
        };
        entry.Conditions.Add(tab);
        return entry;
    }

    Message NewMessage(string edid)
    {
        var message = new Message(mod) { EditorID = edid, Description = "Which do you want repaired?", Flags = Message.Flag.MessageBox, MenuButtons = [] };
        mod.Messages.Add(message);
        return message;
    }

    /// <summary>A button shown while its global is 1.</summary>
    static MessageButton Button(string text, GlobalFloat shown)
    {
        var d = new GetGlobalValueConditionData();
        d.Global = new FormLinkOrIndex<IGlobalGetter>(d, shown.FormKey);
        return new MessageButton { Text = text, Conditions = [new ConditionFloat { Data = d, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 }] };
    }
}
