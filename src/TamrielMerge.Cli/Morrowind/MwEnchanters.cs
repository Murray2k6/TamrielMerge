using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's enchanting and spellmaking services (AIDT flags; docs/game-systems.md, docs/script-gaps.md):
/// <list type="bullet">
/// <item>"Enchanting" (sEnchanting), for anyone in MWEnchanters: TMMwEnchanting places Skyrim's enchanting table at the
/// player, the player's enchanting raised to the enchanter's skill for as long as it is open, and takes the fee for each
/// item made. Skyrim's menu shows no price and hides which enchantment was made from scripts, so the fee stands on the soul
/// gem spent, the one thing a script can read: the median cost of Morrowind's own enchantments (<see cref="EnchantCosts"/>)
/// that a gem of that size can hold, priced as OpenMW Enchanting::getEnchantPrice prices one.</item>
/// <item>"Recharge" (sRechargeEnchantment), for the same people: a soul gem of the player's refills the weapon in a hand
/// (Skyrim's charge is only reachable on what is held: SKSE's WornObject and the Right/LeftItemCharge actor values).</item>
/// <item>"Spellmaking" (sSpellmaking), for anyone in MWSpellmakers: a line saying there is nothing to offer, as Skyrim can't
/// make a spell at run time (Papyrus has no way to create one; O70 is the same on Oblivion's side).</item>
/// </list>
/// </summary>
public sealed partial class MwPluginBuilder
{
    readonly List<(Npc Npc, Tes3Record Record)> enchanterNpcs = [];
    Faction? enchantersTag, spellmakersTag;
    /// <summary>Everyone with the Enchanting service (the Enchanting and Recharge topics' condition).</summary>
    Faction EnchantersTag => enchantersTag ??= Skyrim.CoinTrade.Tag(mod, "MWEnchanters", "Enchanters");
    /// <summary>Everyone with the Spellmaking service (the Spellmaking topic's condition).</summary>
    Faction SpellmakersTag => spellmakersTag ??= Skyrim.CoinTrade.Tag(mod, "MWSpellmakers", "Spellmakers");

    /// <summary>Enchanters made (<see cref="FinishEnchanters"/>).</summary>
    public int Enchanters { get; private set; }

    /// <summary>Marks a person's enchanting and spellmaking services as its actor is made (AddFactionAndClass).</summary>
    void MakeEnchanter(Npc npc, Tes3Record rec)
    {
        int services = Services(rec);
        if ((services & ServiceEnchanting) != 0)
        {
            npc.Factions.Add(new RankPlacement { Faction = EnchantersTag.ToLink<IFactionGetter>(), Rank = 0 });
            enchanterNpcs.Add((npc, rec));
        }
        if ((services & ServiceSpellmaking) != 0)
            npc.Factions.Add(new RankPlacement { Faction = SpellmakersTag.ToLink<IFactionGetter>(), Rank = 0 });
    }

    /// <summary>A service topic of the dialogue quest: top-level, for anyone in the faction, with one response.</summary>
    void ServiceTopic(string edid, string name, Faction who, DialogResponses response)
    {
        var branch = new DialogBranch(mod)
        {
            EditorID = edid + "Branch",
            Quest = dialogueQuest!.ToLink<IQuestGetter>(),
            Category = DialogBranch.CategoryType.Player,
            Flags = DialogBranch.Flag.TopLevel,
        };
        var topic = new DialogTopic(mod)
        {
            EditorID = edid + "Topic",
            Name = name,
            Quest = dialogueQuest!.ToNullableLink<IQuestGetter>(),
            Branch = branch.ToNullableLink<IDialogBranchGetter>(),
            Priority = 50,
            Category = DialogTopic.CategoryEnum.Topic,
            Subtype = DialogTopic.SubtypeEnum.Custom,
            Responses = [],
        };
        branch.StartingTopic = topic.ToNullableLink<IDialogTopicGetter>();
        var inIt = new GetInFactionConditionData(); inIt.Faction = new FormLinkOrIndex<IFactionGetter>(inIt, who.FormKey);
        response.Conditions.Add(Cond(inIt, CompareOperator.EqualTo, 1));
        topic.Responses.Add(response);
        mod.DialogBranches.Add(branch);
        mod.DialogTopics.Add(topic);
        DialogueTopics++;
    }

    /// <summary>A silent response ending the dialogue, its end fragment in a script of its own (Morrowind's enchanters
    /// say nothing as their window opens; the pattern of <see cref="BarterTopic"/>).</summary>
    DialogResponses ServiceResponse(string script) => new(mod)
    {
        Conditions = [],
        Responses = [new DialogResponse { Text = "", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
        Flags = new DialogResponseFlags { Flags = DialogResponses.Flag.Goodbye },
        VirtualMachineAdapter = new DialogResponsesAdapter
        {
            Scripts = [new ScriptEntry { Name = script }],
            ScriptFragments = new ScriptFragments
            {
                ExtraBindDataVersion = 2,
                FileName = script,
                OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = script, FragmentName = "Fragment_End" },
            },
        },
    };

    /// <summary>The Enchanting, Recharge and Spellmaking topics (called from BuildDialogue).</summary>
    void EnchantingTopics()
    {
        ServiceTopic("MWEnchanting", SettingText("sEnchanting", "Enchanting"), EnchantersTag, ServiceResponse("TMMwEnchantingInfo"));
        ServiceTopic("MWRecharge", SettingText("sRechargeEnchantment", "Recharge Enchanted Item").Trim(), EnchantersTag, ServiceResponse("TMMwRechargeInfo"));
        // No game setting says this: Morrowind's spellmakers open their window without a word.
        ServiceTopic("MWSpellmaking", SettingText("sSpellmaking", "Spellmaking"), SpellmakersTag, new DialogResponses(mod)
        {
            Conditions = [],
            Responses = [new DialogResponse { Text = "I can't make new spells for you here. I have nothing to offer you in spellmaking.", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
            Flags = new DialogResponseFlags(),
        });
    }

    /// <summary>The cost Morrowind reckons for an enchantment's last effect, which is its whole cost: OpenMW
    /// Enchanting::getEffectCosts, each effect's ((min + max, each at least 1) x duration + area, at least 1) x base cost x
    /// fEffectCostMult x 0.05 added to what came before, at least 1, the running total x 1.5 on a Target effect.</summary>
    static float FinalEffectCost(Tes3Record ench, Dictionary<int, float> baseCosts, float effectCostMult)
    {
        float cost = 0;
        foreach (var e in ench.All("ENAM"))
        {
            if (!baseCosts.TryGetValue(BitConverter.ToInt16(e.Data, 0), out float baseCost)) continue;
            cost += ((Math.Max(1, e.Int(16)) + Math.Max(1, e.Int(20))) * e.Int(12) + Math.Max(1, e.Int(8))) * baseCost * effectCostMult * 0.05f;
            cost = Math.Max(1, cost);
            if (e.Int(4) == 2) cost *= 1.5f;
        }
        return cost;
    }

    /// <summary>
    /// What an enchantment costs by the soul gem that made it, petty to grand: the median <see cref="FinalEffectCost"/> of
    /// Morrowind's on-strike and on-use enchantments (ENDT type 1, 2) whose charge fits that gem and not the one below.
    /// Morrowind.esm alone: 3.2, 10.0, 16.2, 30.1, 56.3. A size with none keeps the size below's.
    /// </summary>
    float[] EnchantCosts(float[] souls)
    {
        var baseCosts = MwEffectBaseCosts();
        float effectCostMult = SettingFloat("fEffectCostMult", 0.5f);
        var costs = new float[souls.Length];
        for (int s = 0; s < souls.Length; s++)
        {
            float low = s == 0 ? 0 : souls[s - 1];
            var fitting = new List<float>();
            foreach (var rec in world.Objects.Values.Where(o => o.Type == "ENCH"))
                if (rec.Get("ENDT") is { Data.Length: >= 12 } d && d.Int(0) is 1 or 2 && d.Int(8) > low && d.Int(8) <= souls[s])
                    fitting.Add(FinalEffectCost(rec, baseCosts, effectCostMult));
            costs[s] = fitting.Count > 0 ? Median(fitting) : s > 0 ? costs[s - 1] : 1;
        }
        return costs;
    }

    /// <summary>The quest MWEnchantingService (TMMwEnchanting) and TMMwEnchanter on each enchanter, with the record's
    /// Enchant, Intelligence and Luck (TMMwState adds what scripts changed).</summary>
    void FinishEnchanters()
    {
        if (enchanterNpcs.Count == 0) return;
        static ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };
        // A gem's soul in Morrowind: its value x fSoulGemMult (Morrowind.esm: 30, 60, 120, 180, 600).
        string[] gems = ["misc_soulgem_petty", "misc_soulgem_lesser", "misc_soulgem_common", "misc_soulgem_greater", "misc_soulgem_grand"];
        int[] gemValues = [10, 20, 40, 60, 200];
        float soulGemMult = SettingFloat("fSoulGemMult", 3);
        float[] souls = [.. gems.Select((g, i) => soulGemMult * (world.Objects.TryGetValue(g, out var rec) && rec.Get("MCDT") is { Data.Length: >= 8 } d ? d.Int(4) : gemValues[i]))];
        var boost = new Spell(mod)
        {
            EditorID = "MWEnchanterSkill", Name = "Enchanter's skill", ObjectBounds = new ObjectBounds(), Type = Mutagen.Bethesda.Skyrim.SpellType.Ability, CastType = CastType.ConstantEffect,
            TargetType = TargetType.Self, Description = "", Effects = [],
        };
        // Skyrim's Fortify Enchanting (EnchRobesFortifyEnchantingConstantSelf, read from Skyrim.esm); magnitude set in the script.
        boost.Effects.Add(new Effect { BaseEffect = new FormLinkNullable<IMagicEffectGetter>(Sk(0x109632)), Data = new EffectData { Magnitude = 0, Area = 0, Duration = 0 } });
        mod.Spells.Add(boost);
        Message Menu(string edid, string text, params string[] buttons)
        {
            var msg = new Message(mod) { EditorID = edid, Description = text, Flags = Message.Flag.MessageBox, MenuButtons = [] };
            foreach (var b in buttons) msg.MenuButtons.Add(new MessageButton { Text = b });
            mod.Messages.Add(msg);
            return msg;
        }
        string cancel = SettingText("sCancel", "Cancel");
        string recharge = SettingText("sRechargeEnchantment", "Recharge Enchanted Item").Trim();
        var entry = new ScriptEntry
        {
            Name = "TMMwEnchanting",
            Properties =
            [
                Obj("PlayerRef", PlayerRefKey),
                Obj("Gold", SkyrimGoldKey),
                Obj("Table", Sk(0x0BAD0D)),       // Skyrim's enchanting table, as TMObState.ShowEnchanting places it
                Obj("AzuraStar", Sk(0x063B27)),
                Obj("Boost", boost.FormKey),
                Obj("Travel", TravelQuest().FormKey),   // TMMwTravel.BarterOffer: OpenMW getBarterOffer
                Obj("HandMenu", Menu("MWRechargeHand", recharge, SettingText("sRight", "Right"), SettingText("sLeft", "Left"), cancel).FormKey),
                Obj("GemMenu", Menu("MWRechargeGem", SettingText("sSoulGemsWithSouls", "Soul Gems with Souls"), "Petty", "Lesser", "Common", "Greater", "Grand", cancel).FormKey),
                new ScriptFloatListProperty { Name = "Costs", Data = [.. EnchantCosts(souls)] },
                new ScriptFloatListProperty { Name = "Souls", Data = [.. souls] },
                // Skyrim's own soul levels, the charge its gems give what Skyrim enchanted (UESP Skyrim:Soul Gems).
                new ScriptFloatListProperty { Name = "SkyrimSouls", Data = [250, 500, 1000, 2000, 3000] },
                new ScriptFloatProperty { Name = "ValueMult", Data = SettingFloat("fEnchantmentValueMult", 1000) },
                new ScriptFloatProperty { Name = "SoulGemMult", Data = soulGemMult },
                new ScriptFloatProperty { Name = "FatigueBase", Data = SettingFloat("fFatigueBase", 1.25f) },
                new ScriptFloatProperty { Name = "FatigueMult", Data = SettingFloat("fFatigueMult", 0.5f) },
                new ScriptStringProperty { Name = "NoGem", Data = SettingText("sNotifyMessage52", "You must have a soul gem with a soul to make an Enchanted Item") },
                new ScriptStringProperty { Name = "NoGold", Data = SettingText("sBarterDialog1", "You cannot afford this transaction.") },
                new ScriptStringProperty { Name = "Failed", Data = SettingText("sNotifyMessage34", "The enchantment failed and your gem is destroyed.") },
            ],
        };
        if (scriptWorld is not null) entry.Properties.Add(Obj("MwState", StateQuest().FormKey));
        var quest = new Quest(mod)
        {
            EditorID = "MWEnchantingService",
            Name = "Morrowind enchanters",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 0,
            Type = Quest.TypeEnum.None,
            VirtualMachineAdapter = new QuestAdapter { Scripts = [entry] },
        };
        mod.Quests.Add(quest);
        var stats = new MwStats(world);
        foreach (var (npc, rec) in enchanterNpcs)
        {
            var own = stats.Of(rec.Id);
            npc.VirtualMachineAdapter ??= new VirtualMachineAdapter();
            npc.VirtualMachineAdapter.Scripts.Add(new ScriptEntry
            {
                Name = "TMMwEnchanter",
                Properties =
                [
                    Obj("Service", quest.FormKey),
                    new ScriptIntProperty { Name = "Enchant", Data = own?[8 + 9] ?? 0 },   // skill 9
                    new ScriptIntProperty { Name = "Intelligence", Data = own?[1] ?? 0 },
                    new ScriptIntProperty { Name = "Luck", Data = own?[7] ?? 0 },
                ],
            });
            Enchanters++;
        }
    }
}
