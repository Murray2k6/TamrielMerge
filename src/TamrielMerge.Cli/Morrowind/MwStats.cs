namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's attributes and the skills Skyrim has no actor value for, as Morrowind reckons them from the records:
/// an NPC's own (NPDT), or for an auto-calculated NPC its race's base values (RADT), its class's two favoured
/// attributes (+10) and its level's growth by its class's major and minor skills (Morrowind's, as OpenMW reimplements
/// it); a creature's own (NPDT). The races' and birthsigns' tables are for the player, whose values the game reckons
/// at run time (TMMwState).
/// </summary>
public sealed class MwStats
{
    /// <summary>The stats kept: Morrowind's eight attributes, then the skills Skyrim has no actor value for.</summary>
    public static readonly string[] Names = ["strength", "intelligence", "willpower", "agility", "speed", "endurance", "personality", "luck", "athletics", "acrobatics", "unarmored"];
    /// <summary>Morrowind's skill indexes of the kept skills (Athletics, Acrobatics, Unarmored).</summary>
    static readonly int[] KeptSkills = [8, 20, 17];
    public const int Count = 11;

    readonly MwWorld world;
    /// <summary>Morrowind's skills (SKIL by INDX): governing attribute and specialization (0 combat, 1 magic, 2 stealth).</summary>
    readonly Dictionary<int, (int Attribute, int Specialization)> skills = [];
    readonly Dictionary<string, int[]?> cache = new(StringComparer.OrdinalIgnoreCase);

    public MwStats(MwWorld world)
    {
        this.world = world;
        foreach (var plugin in world.Plugins)
            foreach (var r in plugin.Records.Where(r => r.Type == "SKIL"))
                if (r.Get("INDX") is { Data.Length: >= 4 } i && r.Get("SKDT") is { Data.Length: >= 8 } d) skills[i.Int()] = (d.Int(0), d.Int(4));
    }

    /// <summary>An NPC's or creature's stats (<see cref="Names"/>), or null for anything else.</summary>
    public int[]? Of(string id)
    {
        if (cache.TryGetValue(id, out var hit)) return hit;
        int[]? v = null;
        if (world.Objects.TryGetValue(id, out var rec))
            v = rec.Type switch { "NPC_" => Npc(rec), "CREA" => Creature(rec), _ => null };
        return cache[id] = v;
    }

    int[]? Creature(Tes3Record rec)
    {
        if (rec.Get("NPDT") is not { Data.Length: >= 68 } d) return null;
        var v = new int[Count];
        for (int i = 0; i < 8; i++) v[i] = d.Int(8 + i * 4);
        // A creature's skills are its combat, magic or stealth value, by the skill's specialization.
        for (int k = 0; k < KeptSkills.Length; k++)
            v[8 + k] = d.Int(56 + 4 * (skills.TryGetValue(KeptSkills[k], out var s) ? Math.Clamp(s.Specialization, 0, 2) : 0));
        return v;
    }

    int[]? Npc(Tes3Record rec)
    {
        if (rec.Get("NPDT") is not { } d) return null;
        var v = new int[Count];
        if (d.Data.Length >= 52)
        {
            for (int i = 0; i < 8; i++) v[i] = d.Data[2 + i];
            for (int k = 0; k < KeptSkills.Length; k++) v[8 + k] = d.Data[10 + KeptSkills[k]];
            return v;
        }
        if (d.Data.Length < 2) return null;
        int level = BitConverter.ToInt16(d.Data, 0);
        bool female = ((rec.Get("FLAG")?.Int() ?? 0) & 1) != 0;
        var race = rec.Str("RNAM") is { } r && world.Objects.TryGetValue(r, out var rr) && rr.Type == "RACE" ? rr : null;
        var cls = rec.Str("CNAM") is { } c && world.Objects.TryGetValue(c, out var cr) && cr.Type == "CLAS" ? cr : null;
        var radt = race?.Get("RADT");
        var cldt = cls?.Get("CLDT");
        // Attributes: the race's, the class's two favoured +10, then each level's growth by the skills they govern.
        for (int a = 0; a < 8; a++) v[a] = radt is { Data.Length: >= 120 } ? radt.Value.Int(56 + a * 8 + (female ? 4 : 0)) : 40;
        if (cldt is { Data.Length: >= 52 } cd)
            for (int i = 0; i < 2; i++) if (cd.Int(i * 4) is >= 0 and < 8 and var fa) v[fa] += 10;
        for (int a = 0; a < 8; a++)
        {
            float sum = 0;
            foreach (var (index, (attribute, _)) in skills)
            {
                if (attribute != a) continue;
                sum += Major(cldt, index) switch { 1 => 1f, 0 => 0.5f, _ => 0.2f };
            }
            v[a] = Math.Min((int)MathF.Round(v[a] + (level - 1) * sum, MidpointRounding.ToEven), 100);
        }
        // The kept skills, as Morrowind auto-calculates skills.
        for (int k = 0; k < KeptSkills.Length; k++)
        {
            int skill = KeptSkills[k];
            int major = Major(cldt, skill);
            float value = major switch { 1 => 25, 0 => 10, _ => 0 };
            float multiplier = major switch { 1 => 1f, 0 => 0.5f, _ => 0.1f };
            int raceBonus = 0;
            if (radt is { Data.Length: >= 56 } rd)
                for (int i = 0; i < 7; i++) if (rd.Int(i * 8) == skill) raceBonus = rd.Int(i * 8 + 4);
            int specBonus = 0;
            if (cldt is { Data.Length: >= 12 } sd && skills.TryGetValue(skill, out var s) && s.Specialization == sd.Int(8)) { specBonus = 5; multiplier += 0.5f; }
            v[8 + k] = Math.Min((int)MathF.Round(value + 5 + raceBonus + specBonus + (level - 1) * multiplier, MidpointRounding.ToEven), 100);
        }
        return v;
    }

    /// <summary>Whether the class has the skill as a major (1) or minor (0) skill; -1 for neither.</summary>
    static int Major(Tes3Field? cldt, int skill)
    {
        if (cldt is not { Data.Length: >= 52 } cd) return -1;
        for (int k = 0; k < 5; k++) if (cd.Int(16 + k * 8) == skill) return 1;
        for (int k = 0; k < 5; k++) if (cd.Int(12 + k * 8) == skill) return 0;
        return -1;
    }

    /// <summary>A race's eight base attributes (male, female), from RADT.</summary>
    public (int[] Male, int[] Female)? Race(string raceId)
    {
        if (!world.Objects.TryGetValue(raceId, out var rec) || rec.Type != "RACE" || rec.Get("RADT") is not { Data.Length: >= 120 } d) return null;
        var male = new int[8]; var female = new int[8];
        for (int a = 0; a < 8; a++) { male[a] = d.Int(56 + a * 8); female[a] = d.Int(60 + a * 8); }
        return (male, female);
    }

    /// <summary>
    /// A birthsign's effect on the attributes: its abilities' Fortify Attribute (79, plus) and Drain Attribute (17,
    /// minus) effects, by its name ("The Lady"), for each of Morrowind's birthsigns (BSGN).
    /// </summary>
    public Dictionary<string, int[]> Birthsigns()
    {
        var signs = new Dictionary<string, int[]>(StringComparer.OrdinalIgnoreCase);
        // From the plugins themselves: a birthsign's ID can be a spell's too ("Lady's Favor"), and the world's objects
        // keep one record per ID.
        var records = new Dictionary<string, Tes3Record>(StringComparer.OrdinalIgnoreCase);
        var spells = new Dictionary<string, Tes3Record>(StringComparer.OrdinalIgnoreCase);
        foreach (var plugin in world.Plugins)
            foreach (var r in plugin.Records.Where(r => r.Type is "BSGN" or "SPEL" && r.Id.Length > 0))
            {
                var into = r.Type == "BSGN" ? records : spells;
                if (r.Deleted) into.Remove(r.Id); else into[r.Id] = r;
            }
        foreach (var sign in records.Values)
        {
            var v = new int[8];
            foreach (var spellId in sign.All("NPCS").Select(f => f.Text))
            {
                if (!spells.TryGetValue(spellId, out var spell)) continue;
                // SPDT type 1: an ability (always on); powers and spells don't change the attributes.
                if (spell.Get("SPDT") is not { } spdt || spdt.Int(0) != 1) continue;
                foreach (var e in spell.All("ENAM"))
                {
                    if (e.Data.Length < 24) continue;
                    int effect = BitConverter.ToInt16(e.Data, 0);
                    int attribute = (sbyte)e.Data[3];
                    int magnitude = e.Int(16);
                    if (attribute is < 0 or >= 8) continue;
                    if (effect == 79) v[attribute] += magnitude;
                    else if (effect == 17) v[attribute] -= magnitude;
                }
            }
            signs[sign.Str("FNAM") ?? sign.Id] = v;
        }
        return signs;
    }
}
