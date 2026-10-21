namespace TamrielMerge.Daggerfall;

/// <summary>How a Daggerfall creature moves (Daggerfall Unity MobileBehaviour).</summary>
public enum DfMoves { General, Flying, Aquatic, Spectral, Spellcaster, Guard, CustomBodyParts }

/// <summary>
/// A Daggerfall creature's numbers as the game has them (Daggerfall Unity EnemyBasics, MIT, from FALL.EXE): its sounds
/// (DAGGER.SND record indexes: move, bark, attack; -1 none), its three attacks' least and most damage (0: none), its
/// health range, level, armour, its attack's sprite frames and the frame its blow lands on (-1: none), and the least
/// weapon material that hurts it.
/// </summary>
public sealed record DfMonster(int Id, string Name, DfMoves Moves, int MoveSound, int BarkSound, int AttackSound,
    int MinDamage, int MaxDamage, int MinDamage2, int MaxDamage2, int MinDamage3, int MaxDamage3,
    int MinHealth, int MaxHealth, int Level, int Armor, int AttackFrames, int HitFrame, string MinMetalToHit)
{
    /// <summary>The average blow over its attacks (Daggerfall picks one, then a damage in its range).</summary>
    public (float Min, float Max) Damage
    {
        get
        {
            var attacks = new[] { (MinDamage, MaxDamage), (MinDamage2, MaxDamage2), (MinDamage3, MaxDamage3) }.Where(a => a.Item2 > 0).ToList();
            return attacks.Count == 0 ? (0, 0) : ((float)attacks.Average(a => a.Item1), (float)attacks.Average(a => a.Item2));
        }
    }

    /// <summary>Where in its attack its blow lands, as a fraction of the attack (the hit frame over the frames).</summary>
    public float HitFraction => AttackFrames > 0 && HitFrame >= 0 ? (float)HitFrame / AttackFrames : 0.5f;
}

/// <summary>Daggerfall's creatures' numbers, and their speeds from the game's own MONSTER.BSA.</summary>
public static class DfMonsters
{
    public static readonly IReadOnlyDictionary<int, DfMonster> All = new Dictionary<int, DfMonster>
    {
        [0] = new(0, "Rat", DfMoves.General, 115, 116, 117, 1, 4, 0, 0, 0, 0, 9, 16, 1, 6, 7, 3, "None"),
        [1] = new(1, "Imp", DfMoves.Flying, 118, 119, 120, 2, 15, 0, 0, 0, 0, 11, 18, 2, 3, 6, 3, "Steel"),
        [2] = new(2, "Spriggan", DfMoves.General, 121, 122, 123, 1, 8, 1, 8, 1, 10, 12, 26, 3, -4, 7, 2, "None"),
        [3] = new(3, "Giant Bat", DfMoves.Flying, 124, 125, 126, 2, 12, 0, 0, 0, 0, 12, 26, 3, 6, 5, 2, "None"),
        [4] = new(4, "Grizzly Bear", DfMoves.General, 127, 128, 129, 1, 8, 1, 8, 1, 10, 13, 34, 4, 6, 6, 3, "None"),
        [5] = new(5, "Sabertooth Tiger", DfMoves.General, 130, 131, 132, 1, 10, 1, 10, 3, 15, 13, 34, 4, 6, 7, 3, "None"),
        [6] = new(6, "Spider", DfMoves.General, 133, 134, 135, 5, 15, 0, 0, 0, 0, 13, 34, 4, 5, 7, 2, "None"),
        [7] = new(7, "Orc", DfMoves.General, 136, 137, 138, 1, 6, 0, 0, 0, 0, 13, 34, 5, 7, 9, 3, "None"),
        [8] = new(8, "Centaur", DfMoves.General, 139, 140, 141, 5, 15, 0, 0, 0, 0, 14, 46, 5, 6, 9, 5, "None"),
        [9] = new(9, "Werewolf", DfMoves.General, 142, 143, 144, 1, 10, 1, 10, 2, 12, 17, 66, 6, 5, 6, 2, "Silver"),
        [10] = new(10, "Nymph", DfMoves.General, 145, 146, 147, 1, 5, 0, 0, 0, 0, 15, 50, 6, 0, 7, 5, "Silver"),
        [11] = new(11, "Slaughterfish", DfMoves.Aquatic, 148, 149, 150, 2, 12, 0, 0, 0, 0, 15, 50, 7, 6, 9, 1, "None"),
        [12] = new(12, "Orc Sergeant", DfMoves.General, 151, 152, 153, 5, 15, 0, 0, 0, 0, 15, 50, 7, 5, 9, 1, "None"),
        [13] = new(13, "Harpy", DfMoves.Flying, 154, 155, 156, 5, 15, 0, 0, 0, 0, 16, 85, 8, 2, 5, 3, "Dwarven"),
        [14] = new(14, "Wereboar", DfMoves.General, 157, 158, 159, 2, 12, 2, 12, 5, 15, 17, 66, 8, 3, 5, 1, "Silver"),
        [15] = new(15, "Skeletal Warrior", DfMoves.General, 160, 161, 162, 5, 15, 0, 0, 0, 0, 17, 66, 9, 2, 7, 4, "None"),
        [16] = new(16, "Giant", DfMoves.General, 163, 164, 165, 10, 30, 0, 0, 0, 0, 18, 74, 10, 3, 8, 2, "None"),
        [17] = new(17, "Zombie", DfMoves.General, 166, 167, 168, 15, 50, 0, 0, 0, 0, 52, 66, 10, 0, 6, 3, "None"),
        [18] = new(18, "Ghost", DfMoves.Spectral, 169, 170, 171, 10, 35, 0, 0, 0, 0, 17, 66, 11, 0, 5, 2, "Silver"),
        [19] = new(19, "Mummy", DfMoves.General, 172, 173, 174, 5, 15, 0, 0, 0, 0, 17, 66, 11, 2, 6, 3, "Silver"),
        [20] = new(20, "Giant Scorpion", DfMoves.General, 175, 176, 177, 15, 25, 0, 0, 0, 0, 18, 74, 12, 0, 7, 2, "None"),
        [21] = new(21, "Orc Shaman", DfMoves.General, 178, 179, 138, 2, 20, 0, 0, 0, 0, 18, 74, 13, 7, 7, 2, "None"),
        [22] = new(22, "Gargoyle", DfMoves.General, 181, 182, 180, 10, 15, 0, 0, 0, 0, 19, 82, 14, 0, 8, 5, "Mithril"),
        [23] = new(23, "Wraith", DfMoves.Spectral, 183, 184, 185, 20, 45, 0, 0, 0, 0, 30, 90, 15, 0, 5, 3, "Silver"),
        [24] = new(24, "Orc Warlord", DfMoves.General, 186, 187, 188, 5, 50, 0, 0, 0, 0, 20, 90, 16, 0, 8, 2, "None"),
        [25] = new(25, "Frost Daedra", DfMoves.General, 189, 190, 191, 50, 100, 0, 0, 0, 0, 25, 130, 17, -5, 9, 2, "Mithril"),
        [26] = new(26, "Fire Daedra", DfMoves.General, 192, 193, 194, 15, 50, 0, 0, 0, 0, 26, 138, 17, 1, 7, 2, "Mithril"),
        [27] = new(27, "Daedroth", DfMoves.General, 195, 196, 197, 15, 50, 0, 0, 0, 0, 27, 146, 18, 1, 8, 2, "Mithril"),
        [28] = new(28, "Vampire", DfMoves.General, 198, 199, 200, 20, 50, 0, 0, 0, 0, 28, 154, 19, -2, 7, 4, "Silver"),
        [29] = new(29, "Daedra Seducer", DfMoves.General, 201, 202, 203, 15, 50, 0, 0, 0, 0, 27, 146, 19, 1, 4, 2, "Mithril"),
        [30] = new(30, "Vampire Ancient", DfMoves.General, 204, 205, 206, 20, 60, 0, 0, 0, 0, 30, 170, 20, -5, 7, 4, "Mithril"),
        [31] = new(31, "Daedra Lord", DfMoves.General, 207, 208, 209, 15, 50, 0, 0, 0, 0, 35, 210, 20, -10, 7, 2, "Mithril"),
        [32] = new(32, "Lich", DfMoves.General, 210, 211, 212, 70, 100, 0, 0, 0, 0, 30, 170, 20, -10, 8, 4, "Mithril"),
        [33] = new(33, "Ancient Lich", DfMoves.General, 213, 214, 215, 70, 100, 0, 0, 0, 0, 30, 170, 21, -12, 8, 4, "Mithril"),
        [34] = new(34, "Dragonling", DfMoves.Flying, 216, 217, 218, 5, 15, 0, 0, 0, 0, 14, 42, 16, 6, 5, 3, "None"),
        [35] = new(35, "Fire Atronach", DfMoves.General, 219, 220, 221, 5, 15, 0, 0, 0, 0, 25, 130, 16, 6, 6, 1, "None"),
        [36] = new(36, "Iron Atronach", DfMoves.General, 222, 223, 224, 5, 15, 0, 0, 0, 0, 25, 130, 16, 6, 6, 3, "None"),
        [37] = new(37, "Flesh Atronach", DfMoves.General, 225, 226, 227, 5, 15, 0, 0, 0, 0, 25, 130, 16, 6, 6, 3, "None"),
        [38] = new(38, "Ice Atronach", DfMoves.General, 228, 229, 230, 5, 15, 0, 0, 0, 0, 25, 130, 16, 6, 6, 3, "None"),
        [39] = new(39, "Horse", DfMoves.General, -1, -1, -1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, "None"),
        [40] = new(40, "Dragonling", DfMoves.Flying, 216, 217, 218, 5, 15, 0, 0, 0, 0, 14, 42, 16, 6, 5, 2, "None"),
        [41] = new(41, "Dreugh", DfMoves.Aquatic, 237, 238, 239, 5, 15, 0, 0, 0, 0, 13, 34, 16, 6, 10, 4, "None"),
        [42] = new(42, "Lamia", DfMoves.Aquatic, 240, 241, 242, 5, 15, 0, 0, 0, 0, 16, 58, 16, 6, 9, 1, "None"),
    };

    /// <summary>
    /// A creature's Speed attribute from ARENA2\MONSTER.BSA (record ENEMY&lt;id&gt;.CFG, a class file: the eight attributes
    /// as 16-bit values from byte 42, Speed the seventh: Daggerfall Unity ClassFile / DFCareer), or null without it.
    /// </summary>
    public static int? Speed(string arena2, int id)
    {
        string path = Path.Combine(arena2, "MONSTER.BSA");
        if (!File.Exists(path)) return null;
        var bsa = new DfBsa(path);
        string name = $"ENEMY{id:000}.CFG";
        if (!bsa.Contains(name)) return null;
        var data = bsa.Record(name);
        return data.Length >= 56 ? BitConverter.ToUInt16(data, 54) : null;
    }

    /// <summary>How fast it moves, in Skyrim units a second: Daggerfall Unity's enemies move (Speed + 150) classic units
    /// times 0.025 metres a second (EnemyMotor), and a metre is 69.99 of Skyrim's units. Daggerfall's creatures have one
    /// pace (no walk and run).</summary>
    public static float UnitsPerSecond(int speed) => (speed + 150) * 0.025f * 69.99f;
}
