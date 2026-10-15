using System.Text;

namespace TamrielMerge.Daggerfall;

/// <summary>Daggerfall's name banks (Daggerfall Unity NameHelper.BankTypes).</summary>
public enum DfNameBank { Breton, Redguard, Nord, DarkElf, HighElf, WoodElf, Khajiit, Imperial, Monster1, Monster2, Monster3 }

/// <summary>Classic Daggerfall's random generator (Daggerfall Unity DFRandom): a C library LCG, so seeded names come out as in the game.</summary>
public sealed class DfRandom(uint seed)
{
    ulong next = seed;
    public uint Rand()
    {
        next = next * 1103515245 + 12345;
        return (uint)((next >> 16) & 0x7FFF);
    }
}

/// <summary>
/// NAMEGEN.DAT: eleven banks (Breton, Redguard, Nord, Dark/High/Wood Elf, Khajiit, Imperial, three monster banks),
/// each a 48-byte header of six set offsets and six part counts, the parts being fixed 10-byte strings. Names are
/// built the way Daggerfall Unity's NameHelper builds them (MIT): first name from sets 0+1 (male) or 2+3 (female),
/// surname from 4+5 (Nords: 0+1 and "sen"); Redguards have one name of sets 0+1+2 and 3 (male, 75%) or 4 (female).
/// </summary>
public sealed class DfNames
{
    readonly string[][][] banks;

    DfNames(string[][][] banks) => this.banks = banks;

    public static DfNames Load(string arena2)
    {
        var d = File.ReadAllBytes(Path.Combine(arena2, "NAMEGEN.DAT"));
        var banks = new string[11][][];
        for (int b = 0; b < 11; b++)
        {
            var sets = new List<string[]>();
            for (int s = 0; s < 6; s++)
            {
                int offset = BitConverter.ToInt32(d, b * 48 + s * 4), count = BitConverter.ToInt32(d, b * 48 + 24 + s * 4);
                if (count <= 0) break;
                var parts = new string[count];
                for (int k = 0; k < count; k++)
                {
                    int p = offset + k * 10, end = Array.IndexOf(d, (byte)0, p, 10);
                    parts[k] = Encoding.Latin1.GetString(d, p, (end < 0 ? p + 10 : end) - p);
                }
                sets.Add(parts);
            }
            banks[b] = [.. sets];
        }
        return new DfNames(banks);
    }

    public IReadOnlyList<string> Parts(DfNameBank bank, int set) => banks[(int)bank][set];

    static string Pick(string[] parts, DfRandom r) => parts[r.Rand() % (uint)parts.Length];

    /// <summary>A first name (Daggerfall Unity NameHelper.FirstName: Redguards have a single name).</summary>
    public string FirstName(DfNameBank bank, bool female, DfRandom r)
    {
        var sets = banks[(int)bank];
        if (bank == DfNameBank.Redguard) return FullName(bank, female, r);
        return female ? Pick(sets[2], r) + Pick(sets[3], r) : Pick(sets[0], r) + Pick(sets[1], r);
    }

    /// <summary>A full name from this generator state (seed it with the NPC's name seed for Daggerfall's own name).</summary>
    public string FullName(DfNameBank bank, bool female, DfRandom r)
    {
        var sets = banks[(int)bank];
        switch (bank)
        {
            case DfNameBank.Redguard:
            {
                string name = Pick(sets[0], r) + Pick(sets[1], r) + Pick(sets[2], r);
                if (female || r.Rand() % 100 < 75) name += Pick(sets[female ? 4 : 3], r);
                return name;
            }
            case DfNameBank.Nord:
            {
                string first = female ? Pick(sets[2], r) + Pick(sets[3], r) : Pick(sets[0], r) + Pick(sets[1], r);
                return first + " " + Pick(sets[0], r) + Pick(sets[1], r) + "sen";
            }
            case >= DfNameBank.Monster1:
                return Pick(sets[0], r) + Pick(sets[^1], r);
            default:
            {
                string first = female ? Pick(sets[2], r) + Pick(sets[3], r) : Pick(sets[0], r) + Pick(sets[1], r);
                return first + " " + Pick(sets[4], r) + Pick(sets[5], r);
            }
        }
    }
}
