using System.Text.RegularExpressions;
using Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Mods;

/// <summary>
/// Which mods rely on a game's script extender: MWSE for Morrowind (its script functions all start with "x", such as
/// xGetRef; MWSE-Lua mods are folders under Data Files\MWSE\mods rather than plugins), OBSE for Oblivion (compiled
/// script statements and expression calls with opcodes from 0x1400 up are OBSE's or its plugins'; the game's own stop
/// below). Also whether the extender itself is installed.
/// </summary>
public static class ScriptExtenders
{
    public sealed record Use(string Extender, int Scripts, int TotalScripts, List<string> Examples);

    public static string Name(Game game) => game == Game.Morrowind ? "MWSE" : "OBSE";

    /// <summary>Whether the game's script extender is installed (its loader or DLL in the game folder).</summary>
    public static bool Installed(Game game, string dataDir)
    {
        string root = Path.GetDirectoryName(dataDir.TrimEnd('\\', '/'))!;
        return game == Game.Morrowind
            ? File.Exists(Path.Combine(root, "MWSE.dll")) || File.Exists(Path.Combine(root, "MWSE-Update.exe"))
            : File.Exists(Path.Combine(root, "obse_loader.exe")) || Directory.GetFiles(root, "obse_1_2_416.dll").Length > 0 || Directory.GetFiles(root, "obse_steam_loader.dll").Length > 0;
    }

    /// <summary>MWSE-Lua mods: the folders under Data Files\MWSE\mods with a main.lua.</summary>
    public static List<string> LuaMods(string dataDir)
    {
        string mods = Path.Combine(dataDir, "MWSE", "mods");
        if (!Directory.Exists(mods)) return [];
        return Directory.EnumerateFiles(mods, "main.lua", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(mods, Path.GetDirectoryName(f)!)).Order().ToList();
    }

    static readonly Regex MwseCall = new(@"\bx[A-Z][A-Za-z]+\b", RegexOptions.Compiled);

    /// <summary>A plugin's use of the script extender, or null when its scripts don't use it.</summary>
    public static Use? Scan(Game game, string pluginPath)
    {
        try { return game == Game.Morrowind ? ScanMorrowind(pluginPath) : ScanOblivion(pluginPath); }
        catch { return null; }
    }

    static Use? ScanMorrowind(string path)
    {
        var plugin = Morrowind.Tes3Plugin.Load(path);
        int total = 0, using_ = 0;
        var examples = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var rec in plugin.Records.Where(r => r.Type == "SCPT"))
        {
            total++;
            if (rec.Str("SCTX") is not { } text) continue;
            // Strip comments; MWSE's functions are x-prefixed (xGetRef, xSetValue, ...).
            var code = string.Join('\n', text.Split('\n').Select(l => l.Split(';')[0]));
            var hits = MwseCall.Matches(code).Select(m => m.Value).ToList();
            if (hits.Count == 0) continue;
            using_++;
            foreach (var h in hits) if (examples.Count < 12) examples.Add(h);
        }
        return using_ > 0 ? new Use("MWSE", using_, total, examples.ToList()) : null;
    }

    static Use? ScanOblivion(string path)
    {
        using var plugin = OblivionMod.CreateFromBinaryOverlay(path, OblivionRelease.Oblivion);
        int total = 0, using_ = 0;
        var examples = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var script in plugin.Scripts)
        {
            total++;
            byte[] data = script.Fields.CompiledScript is { } compiled ? compiled.ToArray() : [];
            var opcodes = ExtenderOpcodes(data);
            if (opcodes.Count == 0) continue;
            using_++;
            // Name the functions from the source where it's there (the identifier followed by its arguments).
            if (script.Fields.SourceCode is { } source)
                foreach (Match m in Regex.Matches(source, @"\b([A-Z][A-Za-z]+)\b"))
                    if (examples.Count < 12 && ObseNames.Contains(m.Groups[1].Value)) examples.Add(m.Groups[1].Value);
            if (examples.Count == 0) foreach (var op in opcodes.Take(4)) examples.Add($"opcode 0x{op:X4}");
        }
        return using_ > 0 ? new Use("OBSE", using_, total, examples.ToList()) : null;
    }

    /// <summary>
    /// OBSE opcodes in an Oblivion compiled script, found by walking it as the game reads it. Statements are opcode
    /// (u16), length (u16) and data, a function call's opcode being the function's; a call on a reference is prefixed by
    /// 0x1C and the reference's index (u16). set (0x15: variable, expression length, expression) and if/elseif (0x16,
    /// 0x18: jump, expression length, expression) carry expressions in postfix: 's'/'l'/'f'/'G'/'r' + index (u16),
    /// 'n' + int32, 'z' + double, 'X' + opcode (u16) + parameter length (u16) + parameters for a function, anything else
    /// a one-byte operator or digit. The game's own functions are below 0x1400; OBSE's and its plugins' from 0x1400.
    /// </summary>
    public static HashSet<int> ExtenderOpcodes(byte[] data)
    {
        var found = new HashSet<int>();
        int U16(int at) => at + 2 <= data.Length ? BitConverter.ToUInt16(data, at) : 0;
        void Expression(int from, int to)
        {
            to = Math.Min(to, data.Length);
            for (int i = from; i < to;)
            {
                byte t = data[i];
                switch ((char)t)
                {
                    case 's': case 'l': case 'f': case 'G': case 'r': i += 3; break;
                    case 'n': i += 5; break;
                    case 'z': i += 9; break;
                    case 'X':
                        int f = U16(i + 1), plen = U16(i + 3);
                        if (f >= 0x1400 && f < 0x4000) found.Add(f);
                        i += 5 + plen;
                        break;
                    default: i++; break;
                }
            }
        }
        int p = 0;
        while (p + 4 <= data.Length)
        {
            int op = U16(p);
            if (op == 0x1C) { p += 4; continue; }   // reference prefix: the call follows
            int len = U16(p + 2), body = p + 4;
            if (op >= 0x1400 && op < 0x4000) found.Add(op);
            if (op == 0x15 && body < data.Length)
            {
                int q = body;
                if (data[q] == 'r') q += 3;
                q += 3;   // the variable: 's'/'l'/'f' + index
                Expression(q + 2, q + 2 + U16(q));
            }
            else if (op is 0x16 or 0x18) Expression(body + 4, body + 4 + U16(body + 2));
            p = body + len;
        }
        return found;
    }

    /// <summary>Common OBSE functions, to name what a script uses (detection itself is by opcode).</summary>
    static readonly HashSet<string> ObseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "GetEquippedObject", "GetEquippedCurrentHealth", "GetBaseObject", "GetName", "SetName", "GetNumItems", "GetInventoryObject",
        "GetObjectType", "IsKeyPressed", "IsKeyPressed2", "IsKeyPressed3", "GetKeyPress", "GetNumKeysPressed", "IsControlPressed",
        "DisableKey", "EnableKey", "GetControl", "SetControl", "GetMouseButtonPress", "Con_SCR", "PrintToConsole", "SetString",
        "StringCompare", "sv_Construct", "sv_Destruct", "sv_Length", "ar_Construct", "ar_Size", "ar_Find", "ar_Erase", "ar_Insert",
        "ar_Append", "ForEach", "Loop", "While", "Let", "Call", "SetFunctionValue", "GetCallingScript", "GetCrosshairRef",
        "GetActiveMenuMode", "GetActiveUIComponentName", "MessageBoxEx", "MessageEx", "GetGameLoaded", "GetGameRestarted",
        "GetFormFromMod", "GetModIndex", "IsModLoaded", "GetPlayerSpell", "AddSpellNS", "RemoveSpellNS", "GetSpellCount",
        "GetNthSpell", "GetEquipmentSlot", "SetEquipmentSlot", "GetWeight", "SetWeight", "GetGoldValue", "SetGoldValue",
        "GetHealth", "SetHealth", "GetAttackDamage", "SetAttackDamage", "GetArmorRating", "SetArmorRating", "GetEnchantment",
        "SetEnchantment", "GetCurrentHealth", "SetCurrentHealth", "GetRefCount", "SetRefCount", "IsRefEssential",
        "GetTravelHorse", "SetTravelHorse", "GetOwner", "SetOwner", "GetParentCell", "GetParentWorldspace", "GetTeleportCell",
        "GetLinkedDoor", "IsLoadDoor", "GetFirstRef", "GetNextRef", "GetNumRefs", "GetCellChanged", "GetPCTrainingSessionsUsed",
        "SetPCTrainingSessionsUsed", "OnKeyDown", "GetCurrentRegions", "IsPlayable", "SetPlayable", "GetModelPath", "SetModelPath",
        "GetIconPath", "SetIconPath", "GetTexturePath", "SetTexturePath", "GetSoundPlaying", "PlaySound2", "GetPlayersLastActivatedLoadDoor",
        "GetLastCreatedSpell", "CloneForm", "IsClonedForm", "SetInvertFastTravel", "GetRace", "GetSex", "SetEventHandler",
        "RemoveEventHandler", "GetMagicEffectCode", "MagicItemHasEffect", "GetSpellEffectCode", "GetNthEffectItemCode",
        "GetNumEffectItems", "GetHotKeyItem", "SetHotKeyItem", "GetCurrentSoulLevel", "GetBookText", "IsFormValid", "GetSourceModIndex",
    };
}
