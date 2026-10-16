using System.Text.RegularExpressions;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using EP = Mutagen.Bethesda.Skyrim.APerkEntryPointEffect.EntryType;

namespace TamrielMerge.Skyrim.Progression;

/// <summary>
/// The passive web (docs/progression.md "Passive web"), Path of Exile-like, in place of the standing stones: 136 nodes
/// in rings around the 13 birthsigns. Each sign is a start (a small bonus of its own); between neighbouring signs runs a
/// ring of small nodes; out from each sign a branch of small nodes ends in a notable, and in towards the centre another
/// branch ends in a notable and a hub; the hubs ring the six keystones in the middle. Small nodes add to stats (through
/// one hidden ability whose effects TMPassives sets to the sums), notables and keystones are perks; some need a rank in
/// a converted game's faction (the faction registry). One point per character level; a node can be taken when it
/// touches one already taken, starting from the player's sign.
/// </summary>
public static class PassiveWeb
{
    public enum Kind { Small = 0, Notable = 1, Keystone = 2, Start = 3 }

    public sealed record Node(int Index, float X, float Y, Kind Kind, string Name, string Description, int Stat = -1, float Amount = 0,
        IReadOnlyList<PerkFx>? Fx = null, int Gate = -1, int GateRank = 0, int Sign = -1)
    {
        public Perk? Perk { get; set; }
        public string GateText { get; set; } = "";
    }

    /// <summary>The 13 signs in the order of the constellations' seasons (the Warrior's charges, the Mage's, the Thief's,
    /// then the Serpent), with each one's Skyrim standing stone (doom&lt;Word&gt;Ability, which Morrowind's signs also give).</summary>
    public static readonly string[] Signs = ["Warrior", "Lady", "Steed", "Lord", "Mage", "Apprentice", "Atronach", "Ritual", "Thief", "Lover", "Shadow", "Tower", "Serpent"];

    /// <summary>The stats small nodes give: the hidden ability's effects, in this order, and how much one node gives.</summary>
    public static readonly (ActorValue Av, float Amount)[] Stats =
    [
        (ActorValue.Health, 8), (ActorValue.Magicka, 8), (ActorValue.Stamina, 8), (ActorValue.HealRateMult, 10), (ActorValue.MagickaRateMult, 10),
        (ActorValue.StaminaRateMult, 10), (ActorValue.CarryWeight, 10), (ActorValue.DamageResist, 15), (ActorValue.ResistMagic, 3),
        (ActorValue.ResistFire, 5), (ActorValue.ResistFrost, 5), (ActorValue.ResistShock, 5), (ActorValue.PoisonResist, 6), (ActorValue.ResistDisease, 10),
        (ActorValue.OneHanded, 2), (ActorValue.TwoHanded, 2), (ActorValue.Archery, 2), (ActorValue.Block, 2), (ActorValue.HeavyArmor, 2),
        (ActorValue.LightArmor, 2), (ActorValue.Sneak, 2), (ActorValue.Lockpicking, 2), (ActorValue.Pickpocket, 2), (ActorValue.Speech, 2),
        (ActorValue.Alchemy, 2), (ActorValue.Alteration, 2), (ActorValue.Conjuration, 2), (ActorValue.Destruction, 2), (ActorValue.Illusion, 2),
        (ActorValue.Restoration, 2), (ActorValue.Enchanting, 2), (ActorValue.Smithing, 2), (ActorValue.SpeedMult, 1),
    ];
    static int S(ActorValue av) => Array.FindIndex(Stats, s => s.Av == av);

    static readonly ActorValue[][] SignStats =
    [
        [ActorValue.Health, ActorValue.TwoHanded, ActorValue.OneHanded],
        [ActorValue.Health, ActorValue.StaminaRateMult, ActorValue.HealRateMult],
        [ActorValue.SpeedMult, ActorValue.CarryWeight, ActorValue.Stamina],
        [ActorValue.DamageResist, ActorValue.HeavyArmor, ActorValue.Block],
        [ActorValue.Magicka, ActorValue.MagickaRateMult, ActorValue.Destruction],
        [ActorValue.Magicka, ActorValue.Alteration, ActorValue.Conjuration],
        [ActorValue.Magicka, ActorValue.ResistMagic, ActorValue.Enchanting],
        [ActorValue.Restoration, ActorValue.Health, ActorValue.ResistDisease],
        [ActorValue.Sneak, ActorValue.Lockpicking, ActorValue.Pickpocket],
        [ActorValue.Speech, ActorValue.Stamina, ActorValue.LightArmor],
        [ActorValue.Sneak, ActorValue.Illusion, ActorValue.Archery],
        [ActorValue.Lockpicking, ActorValue.Smithing, ActorValue.DamageResist],
        [ActorValue.PoisonResist, ActorValue.Alchemy, ActorValue.Archery],
    ];
    static readonly ActorValue[] RingResists = [ActorValue.ResistFire, ActorValue.ResistFrost, ActorValue.ResistShock, ActorValue.ResistMagic, ActorValue.PoisonResist, ActorValue.ResistDisease];
    static readonly ActorValue[] Pools = [ActorValue.Health, ActorValue.Magicka, ActorValue.Stamina];

    /// <summary>The faction groups that gate nodes, and the registry's factions in each (by name).</summary>
    public static readonly (string Name, Regex Match)[] Groups =
    [
        ("Fighters", new("fighter|arena", RegexOptions.IgnoreCase)),
        ("Mages", new("mage", RegexOptions.IgnoreCase)),
        ("Thieves", new("thie", RegexOptions.IgnoreCase)),
        ("Assassins", new("dark brotherhood|morag tong", RegexOptions.IgnoreCase)),
        ("Temple", new("temple|cult|nine divines|resolution of|chantry|order of arkay|house of dibella|school of julianos|benevolence of mara|akatosh", RegexOptions.IgnoreCase)),
        ("Knights", new("knight|legion|blades", RegexOptions.IgnoreCase)),
        ("Houses", new("great house|ashlanders", RegexOptions.IgnoreCase)),
    ];
    static int G(string name) => Array.FindIndex(Groups, g => g.Name == name);

    static Cond Undead => new(2, "undead");

    // Each sign's inner notable (open to anyone) and outer notable (some need a faction rank: Gate, rank 3).
    static readonly (string Name, string Description, PerkFx[] Fx)[] Inner =
    [
        ("Blade and Hammer", "Weapons do 10% more damage.", [PerkFx.Times(EP.ModAttackDamage, 1.1f)]),
        ("Lady's Grace", "+25 health; health regenerates 25% faster.", [PerkFx.Raise(ActorValue.Health, 25), PerkFx.Raise(ActorValue.HealRateMult, 25)]),
        ("Swift Hooves", "6% faster.", [PerkFx.Raise(ActorValue.SpeedMult, 6)]),
        ("Iron Hide", "Armour's rating is 10% higher.", [PerkFx.Times(EP.ModArmorRating, 1.1f)]),
        ("Arcane Flow", "Magicka regenerates 40% faster.", [PerkFx.Raise(ActorValue.MagickaRateMult, 40)]),
        ("Spellcraft", "Spells last 20% longer.", [PerkFx.Times(EP.ModSpellDuration, 1.2f)]),
        ("Absorption", "A 10% chance to absorb a hostile spell's magicka.", [PerkFx.Raise(ActorValue.AbsorbChance, 10)]),
        ("Mara's Gift", "Restoration spells are 15% stronger.", [PerkFx.Times(EP.ModSpellMagnitude, 1.15f, new Cond(1, "skill", "Restoration"))]),
        ("Light Fingers", "Pickpocketing is 25% likelier to succeed.", [PerkFx.Times(EP.ModPickpocketChance, 1.25f)]),
        ("Silver Tongue", "Buy 8% cheaper and sell 8% dearer.", [PerkFx.Times(EP.ModBuyPrices, 0.92f), PerkFx.Times(EP.ModSellPrices, 1.08f)]),
        ("Soft Steps", "Sneaking is 15% harder to see through.", [PerkFx.Times(EP.ModDetectionSneakSkill, 1.15f)]),
        ("Locksmith", "Locks' sweet spots are 25% wider.", [PerkFx.Times(EP.ModLockpickSweetSpot, 1.25f)]),
        ("Venom Lore", "A poison lasts one more hit; +25% poison resistance.", [PerkFx.Plus(EP.ModPoisonDoseCount, 1), PerkFx.Raise(ActorValue.PoisonResist, 25)]),
    ];
    static readonly (string Name, string Description, PerkFx[] Fx, string? Gate)[] Outer =
    [
        ("Guild Champion", "Power attacks do 20% more damage and cost 20% less stamina.", [PerkFx.Times(EP.ModPowerAttackDamage, 1.2f), PerkFx.Times(EP.ModPowerAttackStamina, 0.8f)], "Fighters"),
        ("Second Wind", "+30 stamina; stamina regenerates 30% faster.", [PerkFx.Raise(ActorValue.Stamina, 30), PerkFx.Raise(ActorValue.StaminaRateMult, 30)], null),
        ("Pack Mule", "+60 carry weight; armour weighs 15% less.", [PerkFx.Raise(ActorValue.CarryWeight, 60), PerkFx.Times(EP.ModArmorWeight, 0.85f)], null),
        ("Knight's Vow", "10% less damage from attacks; +10% magic resistance.", [PerkFx.Times(EP.ModIncomingDamage, 0.9f), PerkFx.Raise(ActorValue.ResistMagic, 10)], "Knights"),
        ("Guild Magister", "Spells cost 10% less; +30 magicka.", [PerkFx.Times(EP.ModSpellCost, 0.9f), PerkFx.Raise(ActorValue.Magicka, 30)], "Mages"),
        ("Wild Talent", "+50 magicka, but 10% weaker to magic.", [PerkFx.Raise(ActorValue.Magicka, 50), PerkFx.Lower(ActorValue.ResistMagic, 10)], null),
        ("Wellspring", "+40 magicka; +15% shock resistance.", [PerkFx.Raise(ActorValue.Magicka, 40), PerkFx.Raise(ActorValue.ResistShock, 15)], null),
        ("Sun's Wrath", "Weapons and spells do 25% more to the undead.", [PerkFx.Times(EP.ModAttackDamage, 1.25f, Undead), PerkFx.Times(EP.ModSpellMagnitude, 1.25f, Undead)], "Temple"),
        ("Guild Fence", "Any merchant buys stolen goods, and you sell 10% dearer.", [PerkFx.Raise(ActorValue.BypassVendorStolenCheck, 1), PerkFx.Times(EP.ModSellPrices, 1.1f)], "Thieves"),
        ("House Favour", "+15 Speech; buy 5% cheaper.", [PerkFx.Raise(ActorValue.Speech, 15), PerkFx.Times(EP.ModBuyPrices, 0.95f)], "Houses"),
        ("Silent Blade", "Sneak attacks do half as much damage again.", [PerkFx.Times(EP.ModSneakAttackMult, 1.5f)], "Assassins"),
        ("Tower Ward", "+40 armour rating; +15% frost resistance.", [PerkFx.Raise(ActorValue.DamageResist, 40), PerkFx.Raise(ActorValue.ResistFrost, 15)], null),
        ("Serpent's Fang", "Bows do 10% more damage; +10 Archery.", [PerkFx.Times(EP.ModAttackDamage, 1.1f, new Cond(1, "keyword", "WeapTypeBow")), PerkFx.Raise(ActorValue.Archery, 10)], null),
    ];
    // The keystones: big trade-offs, most needing rank 5 in a faction.
    static readonly (string Name, string Description, PerkFx[] Fx, string? Gate)[] Keystones =
    [
        ("Avatar of Steel", "Weapons do 25% more damage and +60 armour rating, but spells cost half as much again.",
            [PerkFx.Times(EP.ModAttackDamage, 1.25f), PerkFx.Raise(ActorValue.DamageResist, 60), PerkFx.Times(EP.ModSpellCost, 1.5f)], "Fighters"),
        ("Atronach's Vessel", "Absorb hostile spells half the time, but magicka never regenerates.",
            [PerkFx.Raise(ActorValue.AbsorbChance, 50), PerkFx.Lower(ActorValue.MagickaRateMult, 100)], "Mages"),
        ("Shadowborn", "Sneak attacks do double damage and +25 Sneak, but -50 health.",
            [PerkFx.Times(EP.ModSneakAttackMult, 2), PerkFx.Raise(ActorValue.Sneak, 25), PerkFx.Lower(ActorValue.Health, 50)], "Thieves"),
        ("Martyr of the Nine", "Restoration spells are half as strong again, but Destruction spells half as strong.",
            [PerkFx.Times(EP.ModSpellMagnitude, 1.5f, new Cond(1, "skill", "Restoration")), PerkFx.Times(EP.ModSpellMagnitude, 0.5f, new Cond(1, "skill", "Destruction"))], "Temple"),
        ("Blood Price", "Weapons do 30% more damage, but health never regenerates.",
            [PerkFx.Times(EP.ModAttackDamage, 1.3f), PerkFx.Lower(ActorValue.HealRateMult, 100)], "Assassins"),
        ("Unbound", "15% faster and stamina regenerates half as fast again, but armour's rating is halved.",
            [PerkFx.Raise(ActorValue.SpeedMult, 15), PerkFx.Raise(ActorValue.StaminaRateMult, 50), PerkFx.Times(EP.ModArmorRating, 0.5f)], null),
    ];

    public const float CentreX = 560, CentreY = 372;

    public sealed class Web
    {
        public List<Node> Nodes = [];
        public List<(int A, int B)> Links = [];
        public int[] Starts = new int[13];
        public Spell Stats = null!;
        /// <summary>Each registry faction's group (-1 none), aligned with TMProgression's faction arrays.</summary>
        public int[] FactionGroup = [];
    }

    static string StatText(int stat, float amount)
    {
        var av = Stats[stat].Av;
        bool percent = av is ActorValue.HealRateMult or ActorValue.MagickaRateMult or ActorValue.StaminaRateMult or ActorValue.SpeedMult
            or ActorValue.ResistMagic or ActorValue.ResistFire or ActorValue.ResistFrost or ActorValue.ResistShock or ActorValue.PoisonResist or ActorValue.ResistDisease;
        return $"+{amount:0.#}{(percent ? "%" : "")} {ProgressionPerks.AvName(av)}";
    }

    /// <summary>The web's layout, records and gates.</summary>
    public static Web Build(SkyrimMod mod, ProgressionPerks perks, IReadOnlyList<ProgressionBuilder.FactionEntry> factions)
    {
        var web = new Web();
        var nodes = web.Nodes;
        int Add(float r, double angle, Kind kind, string name, string description, int stat = -1, float amount = 0, PerkFx[]? fx = null, int gate = -1, int gateRank = 0, int sign = -1)
        {
            var n = new Node(nodes.Count, CentreX + (float)(r * Math.Cos(angle)), CentreY + (float)(r * Math.Sin(angle)), kind, name, description, stat, amount, fx, gate, gateRank, sign);
            nodes.Add(n);
            return n.Index;
        }
        int Small(float r, double angle, ActorValue av, float times = 1)
        {
            int s = S(av);
            float amount = Stats[s].Amount * times;
            string t = StatText(s, amount);
            return Add(r, angle, Kind.Small, t, t, s, amount);
        }
        void Link(int a, int b) => web.Links.Add((a, b));

        double step = 2 * Math.PI / 13;
        var signNode = new int[13];
        var hubs = new int[13];
        for (int i = 0; i < 13; i++)
        {
            double a = -Math.PI / 2 + i * step;
            var st = SignStats[i];
            int s0 = S(st[0]);
            signNode[i] = Add(160, a, Kind.Start, "The " + Signs[i], $"Born under the {Signs[i]}: {StatText(s0, Stats[s0].Amount * 2)}.", s0, Stats[s0].Amount * 2, sign: i);
            int o1 = Small(188, a, st[0]), o2 = Small(212, a, st[1]);
            var outer = Outer[i];
            int on = Add(238, a, Kind.Notable, outer.Name, outer.Description, fx: outer.Fx, gate: outer.Gate is { } g ? G(g) : -1, gateRank: 3);
            Link(signNode[i], o1); Link(o1, o2); Link(o2, on);
            int i1 = Small(133, a, st[1]), i2 = Small(108, a, st[2]);
            var inner = Inner[i];
            int inn = Add(84, a, Kind.Notable, inner.Name, inner.Description, fx: inner.Fx);
            hubs[i] = Small(58, a, Pools[i % 3]);
            Link(signNode[i], i1); Link(i1, i2); Link(i2, inn); Link(inn, hubs[i]);
        }
        for (int i = 0; i < 13; i++)
        {
            double a = -Math.PI / 2 + i * step;
            int r1 = Small(162, a + step / 3, RingResists[i % RingResists.Length]);
            int r2 = Small(162, a + 2 * step / 3, SignStats[(i + 1) % 13][0]);
            Link(signNode[i], r1); Link(r1, r2); Link(r2, signNode[(i + 1) % 13]);
            Link(hubs[i], hubs[(i + 1) % 13]);
        }
        for (int k = 0; k < Keystones.Length; k++)
        {
            double a = -Math.PI / 2 + (k + 0.5) * 2 * Math.PI / Keystones.Length;
            var key = Keystones[k];
            int n = Add(26, a, Kind.Keystone, key.Name, key.Description, fx: key.Fx, gate: key.Gate is { } g ? G(g) : -1, gateRank: 5);
            // The hub nearest in angle.
            int hub = Enumerable.Range(0, 13).OrderBy(i => Math.Abs(Math.IEEERemainder(a - (-Math.PI / 2 + i * step), 2 * Math.PI))).First();
            Link(hubs[hub], n);
        }
        web.Starts = signNode;

        // Gates: a group with no faction among the converted games gates nothing.
        web.FactionGroup = [.. factions.Select(f => Array.FindIndex(Groups, g => g.Match.IsMatch(f.Name)))];
        foreach (var n in nodes.Where(n => n.Gate >= 0).ToList())
        {
            var names = factions.Where((f, i) => web.FactionGroup[i] == n.Gate).Select(f => $"{f.Name} ({f.Game})").ToList();
            if (names.Count == 0) { nodes[n.Index] = n with { Gate = -1, GateRank = 0 }; continue; }
            n.GateText = $"Needs rank {n.GateRank} in {string.Join(", or ", names)}.";
        }

        // The records: the stats ability (one hidden effect per stat, magnitudes set by TMPassives) and a perk per
        // notable and keystone.
        web.Stats = perks.Ability("TMWebStats", "Passive web", Stats.Select(s => PerkFx.Raise(s.Av, 0)), hidden: true);
        foreach (var n in nodes.Where(n => n.Fx is not null))
            n.Perk = perks.Perk("TMWeb" + Regex.Replace(n.Name, "[^A-Za-z]", ""), n.Name, n.Description + (n.Kind == Kind.Keystone ? " (keystone)" : ""), n.Fx!);
        return web;
    }
}
