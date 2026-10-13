namespace TamrielMerge.Daggerfall;

/// <summary>One of a spell's three effect slots. Type/SubType are Daggerfall's classic effect group and subgroup.</summary>
public sealed record DfSpellEffect(int Type, int SubType,
    int DurationBase, int DurationMod, int DurationPerLevel,
    int ChanceBase, int ChanceMod, int ChancePerLevel,
    int MagnitudeBaseLow, int MagnitudeBaseHigh, int MagnitudeLevelBase, int MagnitudeLevelHigh, int MagnitudePerLevel)
{
    /// <summary>Average magnitude at the given caster level, per Daggerfall's base + per-level formula.</summary>
    public float MagnitudeAt(int level)
    {
        float baseMag = (MagnitudeBaseLow + MagnitudeBaseHigh) / 2f;
        float perLevel = (MagnitudeLevelBase + MagnitudeLevelHigh) / 2f;
        return baseMag + (MagnitudePerLevel > 0 ? perLevel * (level / MagnitudePerLevel) : 0);
    }

    public int DurationAt(int level) => DurationBase + (DurationPerLevel > 0 ? DurationMod * (level / DurationPerLevel) : 0);
    public int ChanceAt(int level) => ChanceBase + (ChancePerLevel > 0 ? ChanceMod * (level / ChancePerLevel) : 0);
}

public enum DfElement : byte { Fire = 0, Cold = 1, Poison = 2, Shock = 3, Magic = 4 }
public enum DfSpellRange : byte { Caster = 0, Touch = 1, Target = 2, AreaAroundCaster = 3, AreaAtRange = 4 }

/// <summary>A standard spell from SPELLS.STD.</summary>
public sealed record DfSpell(int Index, string Name, DfElement Element, DfSpellRange Range, int Cost, int Icon, IReadOnlyList<DfSpellEffect> Effects);

/// <summary>Reads ARENA2\SPELLS.STD. Layout reference: Daggerfall Unity DaggerfallSpellReader.cs (0x59-byte records).</summary>
public static class DfSpells
{
    const int RecordSize = 0x59;

    public static List<DfSpell> Load(string arena2)
    {
        var d = File.ReadAllBytes(Path.Combine(arena2, "SPELLS.STD"));
        var spells = new List<DfSpell>();
        for (int pos = 0; pos + RecordSize <= d.Length; pos += RecordSize)
        {
            var types = new (int Type, int Sub)[3];
            for (int i = 0; i < 3; i++) types[i] = ((sbyte)d[pos + i * 2], (sbyte)d[pos + i * 2 + 1]);
            var element = (DfElement)d[pos + 6];
            var range = (DfSpellRange)d[pos + 7];
            int cost = BitConverter.ToUInt16(d, pos + 8);
            int dur = pos + 14, chance = dur + 9, mag = chance + 9, name = mag + 15;
            var effects = new List<DfSpellEffect>();
            for (int i = 0; i < 3; i++)
            {
                if (types[i].Type < 0) continue;
                effects.Add(new DfSpellEffect(types[i].Type, types[i].Sub,
                    d[dur + i * 3], d[dur + i * 3 + 1], d[dur + i * 3 + 2],
                    d[chance + i * 3], d[chance + i * 3 + 1], d[chance + i * 3 + 2],
                    d[mag + i * 5], d[mag + i * 5 + 1], d[mag + i * 5 + 2], d[mag + i * 5 + 3], d[mag + i * 5 + 4]));
            }
            int len = Array.IndexOf(d, (byte)0, name, 25) is var z and >= 0 ? z - name : 25;
            string spellName = System.Text.Encoding.Latin1.GetString(d, name, len).Trim();
            if (effects.Count == 0) continue;
            spells.Add(new DfSpell(d[name + 26], spellName, element, range, cost, d[name + 25], effects));
        }
        return spells;
    }
}
