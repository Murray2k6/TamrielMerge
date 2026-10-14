using System.Text;

namespace TamrielMerge.Daggerfall;

/// <summary>
/// A Daggerfall quest: its QBN (the logic) and QRC (the text, a TEXT.RSC-style record database) from ARENA2.
/// QBN layout (UESP "Quest hacking guide" and "Quest pseudo-codes", checked on all 232 files): a 60-byte header (bytes
/// 16-35 ten UInt16 section record counts, bytes 36-57 eleven UInt16 section offsets), then the sections: 0 items (19
/// bytes a record), 3 people (20), 4 places (24), 6 clocks (33), 7 foes (14), 8 operations (87), 9 states (8) and, in
/// seven files, 10 the symbol names (27). Records name their text symbol (_sym_) by hash: h = (h &lt;&lt; 1) + c over
/// the name without its underscores.
/// </summary>
public sealed class DfQuest
{
    public required string Name { get; init; }
    public required List<QItem> Items { get; init; }
    public required List<QPerson> People { get; init; }
    public required List<QPlace> Places { get; init; }
    public required List<QClock> Clocks { get; init; }
    public required List<QFoe> Foes { get; init; }
    public required List<QOp> Ops { get; init; }
    public required List<QState> States { get; init; }
    /// <summary>Symbol names the file itself carries (only seven quests have them), by hash.</summary>
    public required Dictionary<uint, string> SymbolNames { get; init; }
    /// <summary>The quest's messages by id (the QRC), each message's variants (Daggerfall picks one at random).</summary>
    public required Dictionary<int, List<string>> Messages { get; init; }

    /// <summary>An item (section 0): Reward 0 artifact, 1 item, 2 gold, 3 (A0C01Y09 only); Category/Index the item group and
    /// its member (0xFFFF/0xFFFF random of the reward kind, 0xFFFF/0 nothing). Text1 is shown when it is received,
    /// Text2 when it is used (letters).</summary>
    public sealed record QItem(int Index, int Reward, int Category, int CategoryIndex, uint Hash, int Text1, int Text2);
    /// <summary>A person (section 3): Gender 0 male, 1 female, 0xFF any; Kind (UESP Unknown1) the kind of person (0xFFFE a
    /// Daedra prince); Faction the FACTION.TXT id (0 none). Text1/Text2 give them a "Tell me about" entry.</summary>
    public sealed record QPerson(int Index, int Gender, int Face, int Kind, int Faction, uint Hash, int Text1, int Text2);
    /// <summary>A place (section 4): Scope 0 fixed, 1 this town, 2 elsewhere; P1 0 building, 1 dungeon, 2 a town's
    /// exterior (fixed: the high half of the site id); P2 building or dungeon type, -1 any (fixed: the low half); P3 0 any,
    /// 1 house, 2 shop (or a guild's faction).</summary>
    public sealed record QPlace(int Index, int Flags, int Scope, int P1, int P2, int P3, int Unknown, uint Hash, int Text1, int Text2);
    /// <summary>A clock (section 6): Flags, Kind (0 random, 1 fixed, 2-5 by travel distance to Link1/Link2), minutes.</summary>
    public sealed record QClock(int Index, int Flags, int Kind, int Min, int Max, int Link1, int Link2, uint Hash);
    /// <summary>A foe (section 7): Daggerfall mobile type (0-42 monsters, 128+ classes) and how many.</summary>
    public sealed record QFoe(int Index, int Type, int Count, uint Hash);
    /// <summary>A state (section 9), linked to global variable GlobalIndex when IsGlobal.</summary>
    public sealed record QState(int Index, bool IsGlobal, int GlobalIndex, uint Hash);
    /// <summary>An operand: a reference to a record (Section = 0 item, 3 person, 4 place, 6 clock, 7 foe, 9 state; Value its
    /// index, 0xFF/0xFFFFFFFF none, 0xFFFFFFFE a filler), or a constant (Section -1, Value the number).</summary>
    public sealed record QArg(bool Not, int Section, uint Value)
    {
        public bool IsNone => Value is 0xFF or 0xFFFFFFFF or 0xFE or 0xFFFFFFFE;
        public int Int => unchecked((int)Value);
    }
    /// <summary>An operation (section 8): Code, Flags, the valid operands, and a message shown when it runs (-1 none).</summary>
    public sealed record QOp(int Position, int Code, int Flags, int ArgCount, QArg[] Args, int Message);

    public const int SecItems = 0, SecPeople = 3, SecPlaces = 4, SecClocks = 6, SecFoes = 7, SecOps = 8, SecStates = 9;

    /// <summary>Daggerfall's symbol hash.</summary>
    public static uint Hash(string symbol)
    {
        uint h = 0;
        foreach (char c in symbol.Trim('_', '=')) h = (h << 1) + c;
        return h;
    }

    /// <summary>All quests in ARENA2 (every QBN with its QRC), by name.</summary>
    public static List<DfQuest> LoadAll(string arena2) =>
        [.. Directory.GetFiles(arena2, "*.QBN").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).Select(f => Load(arena2, Path.GetFileNameWithoutExtension(f).ToUpperInvariant()))];

    public static DfQuest Load(string arena2, string name)
    {
        var b = File.ReadAllBytes(Path.Combine(arena2, name + ".QBN"));
        int Count(int s) => BitConverter.ToUInt16(b, 16 + s * 2);
        int Offset(int s) => BitConverter.ToUInt16(b, 36 + s * 2);
        int U16(int o) => BitConverter.ToUInt16(b, o);
        short S16(int o) => BitConverter.ToInt16(b, o);
        uint U32(int o) => BitConverter.ToUInt32(b, o);

        var items = new List<QItem>();
        for (int i = 0, o = Offset(SecItems); i < Count(SecItems); i++, o += 19)
            items.Add(new QItem(S16(o), b[o + 2], U16(o + 3), U16(o + 5), U32(o + 7), U16(o + 15), U16(o + 17)));
        var people = new List<QPerson>();
        for (int i = 0, o = Offset(SecPeople); i < Count(SecPeople); i++, o += 20)
            people.Add(new QPerson(S16(o), b[o + 2], b[o + 3], U16(o + 4), U16(o + 6), U32(o + 8), U16(o + 16), U16(o + 18)));
        var places = new List<QPlace>();
        for (int i = 0, o = Offset(SecPlaces); i < Count(SecPlaces); i++, o += 24)
            places.Add(new QPlace(U16(o), b[o + 2], b[o + 3], U16(o + 4), S16(o + 6), S16(o + 8), U16(o + 10), U32(o + 12), U16(o + 20), U16(o + 22)));
        var clocks = new List<QClock>();
        for (int i = 0, o = Offset(SecClocks); i < Count(SecClocks); i++, o += 33)
            clocks.Add(new QClock(S16(o), U16(o + 2), b[o + 4], BitConverter.ToInt32(b, o + 5), BitConverter.ToInt32(b, o + 9), BitConverter.ToInt32(b, o + 21), BitConverter.ToInt32(b, o + 25), U32(o + 29)));
        var foes = new List<QFoe>();
        for (int i = 0, o = Offset(SecFoes); i < Count(SecFoes); i++, o += 14)
            foes.Add(new QFoe(b[o], b[o + 3], U16(o + 4), U32(o + 6)));
        var states = new List<QState>();
        for (int i = 0, o = Offset(SecStates); i < Count(SecStates); i++, o += 8)
            states.Add(new QState(S16(o), b[o + 2] != 0, b[o + 3], U32(o + 4)));
        var ops = new List<QOp>();
        for (int i = 0, o = Offset(SecOps); i < Count(SecOps); i++, o += 87)
        {
            int argCount = U16(o + 4);
            var args = new QArg[5];
            for (int a = 0; a < 5; a++)
            {
                int p = o + 6 + a * 15;
                uint ptr = U32(p + 1);
                // A constant has the pointer 0x12345678; a reference names its section in the pointer's second byte.
                int section = ptr == 0x12345678 ? -1 : (int)((ptr >> 8) & 0xFF);
                args[a] = new QArg(b[p] == 1, section, U32(p + 7));
            }
            int msg = U16(o + 81);
            ops.Add(new QOp(i, U16(o), U16(o + 2), argCount, args, msg == 0xFFFF ? -1 : msg));
        }
        var names = new Dictionary<uint, string>();
        if (b[15] != 0 && Offset(10) is int t && t > 0)
            for (int o = t; o + 27 <= b.Length && b[o] != 0; o += 27)
            {
                string n = Encoding.ASCII.GetString(b, o, 20).TrimEnd('\0');
                int z = n.IndexOf('\0');
                if (z >= 0) n = n[..z];
                names.TryAdd(Hash(n), n);
            }

        var messages = new Dictionary<int, List<string>>();
        string qrc = Path.Combine(arena2, name + ".QRC");
        if (File.Exists(qrc))
        {
            var text = DfText.Load(arena2, name + ".QRC");
            foreach (int id in text.Ids) messages[id] = [.. text[id]];
        }
        return new DfQuest
        {
            Name = name, Items = items, People = people, Places = places, Clocks = clocks, Foes = foes, Ops = ops, States = states,
            SymbolNames = names, Messages = messages,
        };
    }

    /// <summary>Who offers the quest, from the first letter of its name (UESP Appendix B).</summary>
    public static string GroupOf(string name) => name[0] switch
    {
        '_' => "Start", '$' => "Cure", 'S' => "Main",
        >= '1' and <= '9' or (>= 'T' and <= 'Z') => "Daedra",
        '0' => "Julianos", 'A' => "Commoners", 'B' => "KnightlyOrder", 'C' => "Temple", 'D' => "Akatosh", 'E' => "Arkay", 'F' => "Dibella",
        'G' => "Kynareth", 'H' => "Mara", 'I' => "Stendarr", 'J' => "Zenithar", 'K' => "Merchants", 'L' => "DarkBrotherhood",
        'M' => "FightersGuild", 'N' => "MagesGuild", 'O' => "ThievesGuild", 'P' => "Vampires", 'Q' => "Witches", 'R' => "Nobility",
        _ => "Other",
    };
}
