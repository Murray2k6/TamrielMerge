using System.Text;
using System.Text.Json;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Every function Oblivion scripts can call, with its parameters — what the script translator maps onto Papyrus:
/// <list type="bullet">
/// <item>the game's own (opcodes 0x1000–0x13FF), read from the user's Oblivion.exe: the script command table (the
/// address OBSE uses for 1.2.0.416, 0x00B0C8C0–0x00B10268) of 40-byte entries — long name, short name, opcode, help,
/// needs a reference (u16), parameter count (u16), parameters (12-byte entries: name, type, optional), handlers;</item>
/// <item>OBSE's (0x1400 on), from Data\obse_commands.json (tools\obse\gen_commands.py, from xOBSE's source).</item>
/// </list>
/// Looked up by name or short name, case-insensitive.
/// </summary>
public sealed class ObFunctions
{
    public sealed record Param(string Name, string Type, bool Optional);
    public sealed record Function(int Opcode, string Name, string Alias, bool Reference, List<Param> Params, bool Expression, string Returns, string Source, string Description)
    {
        public int Required => Params.Count(p => !p.Optional);
    }

    readonly Dictionary<string, Function> byName = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<Function> All => byName.Values.Distinct().ToList();
    public int VanillaCount { get; private set; }
    public int ObseCount { get; private set; }

    public Function? this[string name] => byName.GetValueOrDefault(name);

    static readonly string[] ParamTypes =
    [
        "String", "Integer", "Float", "InventoryObject", "ObjectRef", "ActorValue", "Actor", "SpellItem", "Axis", "Cell",
        "AnimationGroup", "MagicItem", "Sound", "Topic", "Quest", "Race", "Class", "Faction", "Sex", "Global", "Furniture",
        "TESObject", "VariableName", "QuestStage", "MapMarker", "ActorBase", "Container", "WorldSpace", "CrimeType",
        "AIPackage", "CombatStyle", "MagicEffect", "Birthsign", "FormType", "WeatherID", "NPC", "Owner", "EffectShader",
        "Array", "Climate",
    ];

    public static ObFunctions Load(string oblivionDataDir)
    {
        var f = new ObFunctions();
        f.ReadExe(Path.Combine(Path.GetDirectoryName(oblivionDataDir.TrimEnd('\\', '/'))!, "Oblivion.exe"));
        f.ReadObse(Path.Combine(AppContext.BaseDirectory, "Oblivion", "Data", "obse_commands.json"));
        string plugins = Path.Combine(oblivionDataDir, "OBSE", "plugins");
        if (Directory.Exists(plugins))
            foreach (var dll in Directory.GetFiles(plugins, "*.dll")) f.ReadObsePlugin(dll);
        f.AddAliases();
        return f;
    }

    readonly List<Function> pendingAliases = [];

    /// <summary>Full names first; short names only where no function has that full name (MoveTo is MoveTo, not
    /// MoveToMarker, whose short name is also MoveTo).</summary>
    void Add(Function fn)
    {
        byName[fn.Name] = byName.TryGetValue(fn.Name, out var existing) && existing.Name.Equals(fn.Name, StringComparison.OrdinalIgnoreCase) ? existing : fn;
        if (fn.Alias.Length > 0) pendingAliases.Add(fn);
    }

    void AddAliases()
    {
        foreach (var fn in pendingAliases) byName.TryAdd(fn.Alias, fn);
        pendingAliases.Clear();
    }

    void ReadExe(string exe)
    {
        if (!File.Exists(exe)) return;
        var b = File.ReadAllBytes(exe);
        int pe = BitConverter.ToInt32(b, 0x3C);
        int sections = BitConverter.ToUInt16(b, pe + 6), optional = BitConverter.ToUInt16(b, pe + 20);
        uint imageBase = BitConverter.ToUInt32(b, pe + 24 + 28);
        var map = new List<(uint Va, uint Size, uint Raw)>();
        for (int i = 0; i < sections; i++)
        {
            int o = pe + 24 + optional + i * 40;
            map.Add((BitConverter.ToUInt32(b, o + 12), Math.Max(BitConverter.ToUInt32(b, o + 8), BitConverter.ToUInt32(b, o + 16)), BitConverter.ToUInt32(b, o + 20)));
        }
        long Offset(uint va)
        {
            uint rva = va - imageBase;
            foreach (var (sva, size, raw) in map) if (rva >= sva && rva < sva + size) return raw + rva - sva;
            return -1;
        }
        string Str(uint va)
        {
            long o = Offset(va);
            if (va == 0 || o < 0 || o >= b.Length) return "";
            int end = Array.IndexOf(b, (byte)0, (int)o);
            return Encoding.Latin1.GetString(b, (int)o, Math.Max(0, (end < 0 ? b.Length : end) - (int)o));
        }
        long table = Offset(0x00B0C8C0);
        if (table < 0) return;
        for (long o = table; o + 40 <= Offset(0x00B10268) && o + 40 <= b.Length; o += 40)
        {
            string name = Str(BitConverter.ToUInt32(b, (int)o)), shortName = Str(BitConverter.ToUInt32(b, (int)o + 4));
            int opcode = BitConverter.ToInt32(b, (int)o + 8);
            if (name.Length == 0 || opcode < 0x1000 || opcode > 0x13FF) continue;
            string help = Str(BitConverter.ToUInt32(b, (int)o + 12));
            bool reference = BitConverter.ToUInt16(b, (int)o + 16) != 0;
            int count = BitConverter.ToUInt16(b, (int)o + 18);
            long ps = Offset(BitConverter.ToUInt32(b, (int)o + 20));
            var parameters = new List<Param>();
            for (int k = 0; k < count && ps > 0 && ps + (k + 1) * 12 <= b.Length; k++)
            {
                long po = ps + k * 12;
                int type = BitConverter.ToInt32(b, (int)po + 4);
                parameters.Add(new Param(Str(BitConverter.ToUInt32(b, (int)po)), type >= 0 && type < ParamTypes.Length ? ParamTypes[type] : $"type{type}", BitConverter.ToInt32(b, (int)po + 8) != 0));
            }
            Add(new Function(opcode, name, shortName, reference, parameters, false, "", "Oblivion", help));
            VanillaCount++;
        }
    }

    public int PluginCount { get; private set; }
    public Dictionary<string, int> PluginCommands { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An OBSE plugin's commands, found in its DLL: OBSE plugins register CommandInfo structures (the same 40-byte
    /// layout as the game's) kept in the DLL's data — a pointer to the name, to the short name, the opcode (filled in
    /// at registration), a pointer to the help text, needs-a-reference (u16), parameter count (u16), a pointer to the
    /// parameters (12-byte name / type / optional entries) and a pointer to the code. Every 4-byte-aligned spot in the
    /// data sections that reads as such a structure is one.
    /// </summary>
    void ReadObsePlugin(string dll)
    {
        byte[] b;
        try { b = File.ReadAllBytes(dll); } catch { return; }
        if (b.Length < 0x200 || b[0] != 'M' || b[1] != 'Z') return;
        int pe = BitConverter.ToInt32(b, 0x3C);
        if (pe <= 0 || pe + 0x100 > b.Length || BitConverter.ToUInt16(b, pe + 4) != 0x14C) return;   // 32-bit only
        int sections = BitConverter.ToUInt16(b, pe + 6), optional = BitConverter.ToUInt16(b, pe + 20);
        uint imageBase = BitConverter.ToUInt32(b, pe + 24 + 28);
        var map = new List<(string Name, uint Va, uint Size, uint Raw, uint RawSize, bool Code)>();
        for (int i = 0; i < sections; i++)
        {
            int o = pe + 24 + optional + i * 40;
            uint characteristics = BitConverter.ToUInt32(b, o + 36);
            map.Add((Encoding.ASCII.GetString(b, o, 8).TrimEnd('\0'), BitConverter.ToUInt32(b, o + 12), BitConverter.ToUInt32(b, o + 8),
                BitConverter.ToUInt32(b, o + 20), BitConverter.ToUInt32(b, o + 16), (characteristics & 0x20000000) != 0));
        }
        long Offset(uint va, out bool code)
        {
            code = false;
            if (va < imageBase) return -1;
            uint rva = va - imageBase;
            foreach (var s in map)
                if (rva >= s.Va && rva < s.Va + Math.Max(s.Size, s.RawSize) && rva - s.Va < s.RawSize) { code = s.Code; return s.Raw + rva - s.Va; }
            return -1;
        }
        string? Str(uint va, bool allowEmpty = false)
        {
            long o = Offset(va, out bool code);
            if (o < 0 || code) return null;
            int end = Array.IndexOf(b, (byte)0, (int)o);
            if (end < 0 || end - o > 300) return null;
            var text = Encoding.Latin1.GetString(b, (int)o, end - (int)o);
            if (text.Length == 0) return allowEmpty ? "" : null;
            return text.All(c => c >= 32 && c < 127) ? text : null;
        }
        var nameRx = new System.Text.RegularExpressions.Regex("^[A-Za-z_][A-Za-z0-9_]{2,47}$");
        string plugin = Path.GetFileNameWithoutExtension(dll);
        int found = 0;
        foreach (var s in map.Where(m => !m.Code))
            for (long o = s.Raw; o + 40 <= s.Raw + s.RawSize && o + 40 <= b.Length; o += 4)
            {
                string? name = Str(BitConverter.ToUInt32(b, (int)o));
                if (name is null || !nameRx.IsMatch(name)) continue;
                string? alias = Str(BitConverter.ToUInt32(b, (int)o + 4), allowEmpty: true);
                if (alias is null || alias.Length > 0 && !nameRx.IsMatch(alias)) continue;
                string? help = Str(BitConverter.ToUInt32(b, (int)o + 12), allowEmpty: true);
                int needs = BitConverter.ToUInt16(b, (int)o + 16), count = BitConverter.ToUInt16(b, (int)o + 18);
                uint paramsVa = BitConverter.ToUInt32(b, (int)o + 20), exec = BitConverter.ToUInt32(b, (int)o + 24);
                if (help is null || needs > 1 || count > 24) continue;
                if (Offset(exec, out bool execIsCode) < 0 || !execIsCode) continue;
                var parameters = new List<Param>();
                if (count > 0)
                {
                    long ps = Offset(paramsVa, out bool pCode);
                    if (ps < 0 || pCode) continue;
                    bool ok = true;
                    for (int k = 0; k < count && ok; k++)
                    {
                        long po = ps + k * 12;
                        if (po + 12 > b.Length) { ok = false; break; }
                        string? pname = Str(BitConverter.ToUInt32(b, (int)po), allowEmpty: true);
                        int type = BitConverter.ToInt32(b, (int)po + 4), opt = BitConverter.ToInt32(b, (int)po + 8);
                        if (pname is null || opt is < 0 or > 1) { ok = false; break; }
                        parameters.Add(new Param(pname, type >= 0 && type < ParamTypes.Length ? ParamTypes[type] : $"OBSE:{type}", opt != 0));
                    }
                    if (!ok) continue;
                }
                Add(new Function(0, name, alias, needs != 0, parameters, false, "", "OBSE plugin " + plugin, help));
                found++;
            }
        if (found > 0) { PluginCount++; PluginCommands[plugin] = found; }
    }

    void ReadObse(string json)
    {
        if (!File.Exists(json)) return;
        foreach (var e in JsonDocument.Parse(File.ReadAllText(json)).RootElement.EnumerateArray())
        {
            string name = e.GetProperty("name").GetString()!;
            if (name.Length == 0 || name.StartsWith('@') || name.StartsWith('k')) continue;
            var parameters = e.GetProperty("params").EnumerateArray().Select(p => new Param(
                p.GetProperty("name").GetString()!, p.GetProperty("type").GetString()!.Replace("kParamType_", "").Replace("kOBSEParamType_", "OBSE:"),
                p.GetProperty("optional").GetBoolean())).ToList();
            Add(new Function(e.GetProperty("opcode").GetInt32(), name, e.GetProperty("alt").GetString()!, e.GetProperty("reference").GetBoolean(),
                parameters, e.GetProperty("expression").GetBoolean(), e.TryGetProperty("returns", out var r) ? r.GetString()! : "", "OBSE",
                e.GetProperty("description").GetString()!));
            ObseCount++;
        }
    }
}
