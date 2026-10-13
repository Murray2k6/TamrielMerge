using System.Text;
using System.Text.Json;

namespace TamrielMerge.Mods;

/// <summary>
/// Mods for the games that have them — Morrowind and Oblivion load plugins (.esm masters, .esp plugins) from their
/// data folder; Daggerfall and Redguard have no plugin system. Every plugin in the game's data folder is listed with
/// what its header says (masters, author, description; only the header is read, so big mods are quick), whether it is
/// part of the official game, and whether the game itself has it enabled (Morrowind.ini [Game Files]; Oblivion's
/// Plugins.txt). The converter converts a selection: by default the official content plus whatever the game has
/// enabled, or the user's own choice saved in TamrielMerge.mods.json. A selected plugin's masters are always brought
/// in too, and the selection is put in the game's load order.
/// </summary>
public static class ModCatalog
{
    public sealed record Plugin(string File, bool IsMaster, bool Official, bool EnabledInGame, List<string> Masters, string Author, string Description, DateTime Date, long Size)
    {
        /// <summary>A master that doesn't build on the base game: a total conversion (Nehrim), its own world.</summary>
        public bool Standalone(Game game) => IsMaster && !Official && !Masters.Any(m => BaseMasters(game).Contains(m, StringComparer.OrdinalIgnoreCase));
    }

    public static bool Supports(Game game) => game is Game.Morrowind or Game.Oblivion;

    static string[] BaseMasters(Game game) => game == Game.Morrowind ? ["Morrowind.esm", "Tribunal.esm", "Bloodmoon.esm"] : ["Oblivion.esm"];

    static readonly HashSet<string> MorrowindOfficial = new(StringComparer.OrdinalIgnoreCase)
    {
        "Morrowind.esm", "Tribunal.esm", "Bloodmoon.esm", "adamantiumarmor.esp", "AreaEffectArrows.esp", "bcsounds.esp",
        "EBQ_Artifact.esp", "entertainers.esp", "LeFemmArmor.esp", "master_index.esp", "Siege at Firemoth.esp",
    };

    static readonly HashSet<string> OblivionOfficial = new(StringComparer.OrdinalIgnoreCase)
    {
        "Oblivion.esm", "Knights.esp", "DLCBattlehornCastle.esp", "DLCFrostcrag.esp", "DLCHorseArmor.esp", "DLCMehrunesRazor.esp",
        "DLCOrrery.esp", "DLCShiveringIsles.esp", "DLCSpellTomes.esp", "DLCThievesDen.esp", "DLCVileLair.esp",
    };

    public static bool IsOfficial(Game game, string file) => (game == Game.Morrowind ? MorrowindOfficial : OblivionOfficial).Contains(file);

    /// <summary>Every plugin in the game's data folder, in the game's load order (masters first, then by date).</summary>
    public static List<Plugin> List(Game game, string dataDir)
    {
        var enabled = EnabledInGame(game, dataDir);
        var list = new List<Plugin>();
        foreach (var path in Directory.GetFiles(dataDir))
        {
            string file = Path.GetFileName(path);
            if (!file.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) continue;
            var (masters, author, description, isMaster) = game == Game.Morrowind ? Tes3Header(path) : Tes4Header(path);
            var info = new FileInfo(path);
            list.Add(new Plugin(file, isMaster || file.EndsWith(".esm", StringComparison.OrdinalIgnoreCase), IsOfficial(game, file),
                enabled.Contains(file) || BaseMasters(game).Contains(file, StringComparer.OrdinalIgnoreCase) && File.Exists(path),
                masters, author, description, info.LastWriteTimeUtc, info.Length));
        }
        return Order(game, list);
    }

    static List<Plugin> Order(Game game, IEnumerable<Plugin> plugins)
    {
        var baseMasters = BaseMasters(game);
        return plugins.OrderBy(p => Array.FindIndex(baseMasters, m => m.Equals(p.File, StringComparison.OrdinalIgnoreCase)) is var i and >= 0 ? i : 100)
            .ThenBy(p => p.IsMaster ? 0 : 1).ThenBy(p => p.Date).ToList();
    }

    /// <summary>The plugins the game itself loads.</summary>
    public static HashSet<string> EnabledInGame(Game game, string dataDir)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (game == Game.Morrowind)
        {
            string ini = Path.Combine(Path.GetDirectoryName(dataDir.TrimEnd('\\', '/'))!, "Morrowind.ini");
            if (File.Exists(ini))
                foreach (var line in File.ReadAllLines(ini, Encoding.Latin1).SkipWhile(l => !l.Trim().Equals("[Game Files]", StringComparison.OrdinalIgnoreCase)).Skip(1)
                             .TakeWhile(l => !l.TrimStart().StartsWith('[')))
                    if (line.Split('=', 2) is [_, var name] && name.Trim().Length > 0) set.Add(name.Trim());
        }
        else
        {
            string list = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Oblivion", "Plugins.txt");
            if (File.Exists(list))
                foreach (var line in File.ReadAllLines(list)) if (line.Trim() is { Length: > 0 } l && !l.StartsWith('#')) set.Add(l);
        }
        return set;
    }

    // ---- the selection ----

    /// <summary>Where the user's choices are kept (the working folder).</summary>
    public static string SelectionFile => Path.GetFullPath("TamrielMerge.mods.json");

    static Dictionary<string, List<string>> LoadSelections()
    {
        try { return File.Exists(SelectionFile) ? JsonSerializer.Deserialize<Dictionary<string, List<string>>>(File.ReadAllText(SelectionFile)) ?? [] : []; }
        catch { return []; }
    }

    /// <summary>The user's saved choice for a game, or null when they haven't made one.</summary>
    public static List<string>? Saved(Game game) => LoadSelections().TryGetValue(game.ToString(), out var list) ? list : null;

    /// <summary>Saves the choice for a game (null forgets it: back to the default).</summary>
    public static void Save(Game game, IEnumerable<string>? plugins)
    {
        var all = LoadSelections();
        if (plugins is null) all.Remove(game.ToString()); else all[game.ToString()] = plugins.ToList();
        File.WriteAllText(SelectionFile, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The default: the official game plus what the game itself has enabled.</summary>
    public static List<string> DefaultSelection(Game game, string dataDir) =>
        List(game, dataDir).Where(p => p.Official || p.EnabledInGame).Select(p => p.File).ToList();

    /// <summary>
    /// What to convert, in load order: the saved choice (else the default), plus every master a chosen plugin needs;
    /// plugins whose masters aren't installed are left out (listed in <paramref name="problems"/>).
    /// </summary>
    public static List<string> Selected(Game game, string dataDir, List<string>? problems = null)
    {
        var all = List(game, dataDir);
        var byName = all.ToDictionary(p => p.File, StringComparer.OrdinalIgnoreCase);
        var chosen = new HashSet<string>(Saved(game) ?? DefaultSelection(game, dataDir), StringComparer.OrdinalIgnoreCase);
        chosen.RemoveWhere(f => !byName.ContainsKey(f));
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (var f in chosen.ToList())
                foreach (var m in byName[f].Masters)
                {
                    if (!byName.ContainsKey(m)) { chosen.Remove(f); problems?.Add($"{f} needs {m}, which isn't installed: left out"); changed = true; break; }
                    if (chosen.Add(m)) { problems?.Add($"{f} needs {m}: added"); changed = true; }
                }
        }
        return all.Where(p => chosen.Contains(p.File)).Select(p => p.File).ToList();
    }

    // ---- plugin headers ----

    /// <summary>Morrowind's TES3 header: HEDR (version, file type 1 = master, author 32 bytes, description 256 bytes),
    /// then MAST/DATA pairs.</summary>
    static (List<string> Masters, string Author, string Description, bool IsMaster) Tes3Header(string path)
    {
        var masters = new List<string>();
        string author = "", description = "";
        bool isMaster = false;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "TES3") return (masters, author, description, false);
            int size = r.ReadInt32(); r.ReadBytes(8);
            long end = r.BaseStream.Position + size;
            while (r.BaseStream.Position + 8 <= end)
            {
                string tag = Encoding.ASCII.GetString(r.ReadBytes(4));
                int len = r.ReadInt32();
                var data = r.ReadBytes(len);
                if (tag == "HEDR" && data.Length >= 300)
                {
                    isMaster = BitConverter.ToInt32(data, 4) == 1;
                    author = Text(data, 8, 32);
                    description = Text(data, 40, 256);
                }
                else if (tag == "MAST") masters.Add(Text(data, 0, data.Length));
            }
        }
        catch { }
        return (masters, author, description, isMaster);
    }

    /// <summary>Oblivion's TES4 header record (20-byte record header, flag 1 = master): MAST, CNAM author, SNAM
    /// description.</summary>
    static (List<string> Masters, string Author, string Description, bool IsMaster) Tes4Header(string path)
    {
        var masters = new List<string>();
        string author = "", description = "";
        bool isMaster = false;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "TES4") return (masters, author, description, false);
            int size = r.ReadInt32();
            isMaster = (r.ReadUInt32() & 1) != 0;
            r.ReadBytes(8);
            long end = r.BaseStream.Position + size;
            while (r.BaseStream.Position + 6 <= end)
            {
                string tag = Encoding.ASCII.GetString(r.ReadBytes(4));
                int len = r.ReadUInt16();
                var data = r.ReadBytes(len);
                if (tag == "MAST") masters.Add(Text(data, 0, data.Length));
                else if (tag == "CNAM") author = Text(data, 0, data.Length);
                else if (tag == "SNAM") description = Text(data, 0, data.Length);
            }
        }
        catch { }
        return (masters, author, description, isMaster);
    }

    static string Text(byte[] b, int at, int len)
    {
        len = Math.Min(len, b.Length - at);
        int zero = Array.IndexOf(b, (byte)0, at, len);
        return Encoding.Latin1.GetString(b, at, (zero < 0 ? at + len : zero) - at).Trim();
    }
}
