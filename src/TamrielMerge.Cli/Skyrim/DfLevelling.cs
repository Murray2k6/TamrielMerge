using TamrielMerge.Skyrim.Progression;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's levelling for the Mundus stones (<see cref="GameLevelling"/>): its 18 classes from arena2's
/// CLASS00.CFG..CLASS17.CFG (74 bytes each, as Daggerfall Unity's ClassFile and DFCareer read them: tolerances, special
/// abilities and spell points, rapid healing, regeneration, spell absorption, bonus to hit or phobia, forbidden
/// materials, weapons, armour and shields, expertise, the 3 primary / 3 major / 6 minor skills, the name, hit points per
/// level, the advancement multiplier and the 8 attributes). Daggerfall Unity's rules: skills start at 28-31 (primary),
/// 18-21 (major), 13-16 (minor) with 6 points to spread in each group, others 3-6; a skill's rise needs its uses times the
/// class's advancement multiplier; each level brings 4-6 attribute points and half to all of the class's hit points per
/// level plus Endurance / 10 - 5. Daggerfall has no birthsigns, and its races no attributes (the class gives them).
/// </summary>
public static class DfLevelling
{
    /// <summary>Daggerfall's 35 skills (DFCareer.Skills order): Skyrim's or the progression's skill, or "DF:" and
    /// Daggerfall's name for those only Daggerfall has (its etiquette, streetwise and languages, kept by DfSenses;
    /// climbing).</summary>
    static readonly string[] Skills =
    [
        "Medical", "DF:Etiquette", "DF:Streetwise", "Acrobatics", "DF:Orcish", "DF:Harpy", "DF:Giantish", "DF:Dragonish", "DF:Nymph", "DF:Daedric",
        "DF:Spriggan", "DF:Centaurian", "DF:Impish", "Lockpicking", "Mercantile", "Pickpocket", "Sneak", "Swimming", "DF:Climbing", "Backstabbing",
        "Dodging", "Athletics", "Destruction", "Restoration", "Illusion", "Alteration", "Thaumaturgy", "Mysticism", "OneHanded", "OneHanded",
        "HandToHand", "TwoHanded", "TwoHanded", "Archery", "CriticalStrike",
    ];

    /// <summary>Each skill's governing attribute (Daggerfall Unity's DaggerfallSkills.GetPrimaryStat), as
    /// <see cref="GameLevelling.Attributes"/> indexes.</summary>
    static readonly int[] SkillAttribute =
    [
        GameLevelling.Intelligence, GameLevelling.Personality, GameLevelling.Personality, GameLevelling.Strength,
        .. Enumerable.Repeat(GameLevelling.Intelligence, 9), GameLevelling.Intelligence, GameLevelling.Personality, GameLevelling.Agility,
        GameLevelling.Agility, GameLevelling.Endurance, GameLevelling.Strength, GameLevelling.Agility, GameLevelling.Speed, GameLevelling.Speed,
        .. Enumerable.Repeat(GameLevelling.Willpower, 6), GameLevelling.Agility, GameLevelling.Agility, GameLevelling.Agility, GameLevelling.Strength,
        GameLevelling.Strength, GameLevelling.Agility, GameLevelling.Agility,
    ];

    /// <summary>Daggerfall's attribute order in CLASS*.CFG (STR INT WIL AGI END PER SPD LCK) as ours.</summary>
    static readonly int[] AttributeOrder = [GameLevelling.Strength, GameLevelling.Intelligence, GameLevelling.Willpower, GameLevelling.Agility,
        GameLevelling.Endurance, GameLevelling.Personality, GameLevelling.Speed, GameLevelling.Luck];

    static readonly float[] SpellPointMultipliers = [3.0f, 2.0f, 1.75f, 1.5f, 1.0f, 0.5f];

    /// <summary>A Daggerfall race's own abilities (Daggerfall Unity: RaceTemplate's resistance and immunity flags,
    /// FormulaHelper's saving throw +30 for a racial resistance and certain success for an immunity,
    /// CalculateRacialModifiers' damage, CalculateClimbingChance's Khajiit +30), by the Skyrim race it is.
    /// <paramref name="DamageDivisor"/>: weapon damage +level / that (0 none) with <paramref name="Weapons"/>
    /// ("any", "melee": any but a bow, "bow").</summary>
    public sealed record DfRace(string Race, string Name, string Text, string? Resist, int ResistAmount, bool ParalysisImmune, int DamageDivisor, string Weapons, int ClimbBonus);

    public static readonly DfRace[] Races =
    [
        new("BretonRace", "Breton", "Resists magic (+30).", "ResistMagic", 30, false, 0, "", 0),
        new("RedguardRace", "Redguard", "Weapons other than bows do +level/3 damage.", null, 0, false, 3, "melee", 0),
        new("NordRace", "Nord", "Resists frost (+30).", "ResistFrost", 30, false, 0, "", 0),
        new("DarkElfRace", "Dark Elf", "Weapons do +level/4 damage.", null, 0, false, 4, "any", 0),
        new("HighElfRace", "High Elf", "Immune to paralysis.", null, 0, true, 0, "", 0),
        new("WoodElfRace", "Wood Elf", "Bows do +level/3 damage.", null, 0, false, 3, "bow", 0),
        new("KhajiitRace", "Khajiit", "Climbs as if 30 points more skilled.", null, 0, false, 0, "", 30),
        // Daggerfall's Argonians swim without tiring and lose breath at half the rate: Skyrim's Argonians already swim
        // without tiring and breathe under water.
        new("ArgonianRace", "Argonian", "Swims without tiring; slow to lose breath (Skyrim's Argonians already breathe under water).", null, 0, false, 0, "", 0),
    ];

    /// <summary>Daggerfall's levelling from <paramref name="arena2"/>; problems (a missing or short class file) added.</summary>
    public static GameLevelling Read(string arena2, string plugin, List<string> problems)
    {
        var classes = new List<GameClass>();
        for (int i = 0; i < 18; i++)
        {
            string path = Path.Combine(arena2, $"CLASS{i:D2}.CFG");
            if (!File.Exists(path)) { problems.Add($"levelling: {path} missing"); continue; }
            var b = File.ReadAllBytes(path);
            if (b.Length < 74) { problems.Add($"levelling: {path} is {b.Length} bytes, not 74"); continue; }
            classes.Add(Class(i, b));
        }
        var governing = new Dictionary<string, int>();
        for (int s = 0; s < Skills.Length; s++)
            if (!Skills[s].StartsWith("DF:")) governing.TryAdd(Skills[s], SkillAttribute[s]);
        return new GameLevelling
        {
            Game = "DF",
            Plugin = plugin,
            Classes = classes,
            Rule = "pool",
            PoolMin = 4,
            PoolMax = 6,
            PrimaryStart = 31,
            MajorStart = 21,
            MinorStart = 15,
            HealthRule = "daggerfall",
            Governing = governing,
        };
    }

    static GameClass Class(int index, byte[] b)
    {
        static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);
        string name = System.Text.Encoding.ASCII.GetString(b, 28, 16).Split('\0')[0].Trim();
        int bits = U16(b, 4);
        uint weaponArmorShields = (uint)((b[13] << 16) | (b[15] << 8) | b[14]);
        int[] tolerance = new int[7];
        for (int e = 0; e < 7; e++)
        {
            int bit = 1 << e;
            tolerance[e] = (b[0] & bit) != 0 ? 1 : (b[1] & bit) != 0 ? 2 : (b[2] & bit) != 0 ? 3 : (b[3] & bit) != 0 ? 4 : 0;
        }
        int spellPoints = (bits & 0x1C00) >> 8;
        int Level(int flags, int light, int dark, int always) => (flags & always) != 0 ? 3 : (flags & dark) != 0 ? 2 : (flags & light) != 0 ? 1 : 0;
        int[] against = new int[4];
        for (int k = 0; k < 4; k++) against[k] = (b[10] & (1 << k)) != 0 ? 1 : (b[10] & (0x10 << k)) != 0 ? -1 : 0;
        var traits = new DfTraits(
            tolerance,
            spellPoints / 4 < SpellPointMultipliers.Length ? SpellPointMultipliers[spellPoints / 4] : 3.0f,
            (bits & 0x300) >> 8, (bits & 0xC0) >> 6,
            Level(b[6], 1, 2, 4),
            (b[7] & 8) != 0 ? 4 : (b[7] & 4) != 0 ? 3 : (b[7] & 2) != 0 ? 2 : (b[7] & 1) != 0 ? 1 : 0,
            Level(b[9], 1, 2, 4),
            (bits & 1) != 0, (bits & 2) != 0, (bits & 4) != 0, (bits & 8) != 0, (bits & 16) != 0, (bits & 32) != 0,
            against,
            (int)((weaponArmorShields >> 16) & 0x3f), (int)(weaponArmorShields & 0x3f), (int)((weaponArmorShields >> 6) & 0x07),
            (int)((weaponArmorShields >> 9) & 0x0f), U16(b, 11));
        uint adv = BitConverter.ToUInt32(b, 54);
        float advancement = MathF.Round((adv >> 16) + (adv & 0xffff) / 65536f, 2);
        var attributes = new int[8];
        for (int a = 0; a < 8; a++) attributes[AttributeOrder[a]] = U16(b, 58 + a * 2);
        string[] Pick(int from, int count) => [.. Enumerable.Range(from, count).Select(o => b[o]).Where(s => s < Skills.Length).Select(s => Skills[s])];
        var primary = Pick(16, 3).Distinct().ToArray();
        var major = Pick(19, 3).Distinct().Except(primary).ToArray();
        var minor = Pick(22, 6).Distinct().Except(primary).Except(major).ToArray();
        // The two attributes the class has most of stand for its favoured ones (the Character view lists them).
        int[] favored = [.. Enumerable.Range(0, 8).OrderByDescending(a => attributes[a]).Take(2)];
        return new GameClass($"CLASS{index:D2}", name, "", -1, favored, major, minor, primary, advancement, U16(b, 52), attributes, traits);
    }
}
