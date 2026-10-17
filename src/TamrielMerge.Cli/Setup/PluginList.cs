using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TamrielMerge.Setup;

/// <summary>
/// The converted plugins switched on where Skyrim reads its plugin list (user, 2026-10-10: "update the tool then" - the
/// install enables them, the voices mod's too): Skyrim's own Plugins.txt (played without a mod manager) and, when Mod
/// Organizer 2 manages the game, its selected profile's plugins.txt and loadorder.txt - only while MO2 is closed, as it
/// writes them when it exits. Plugins missing from a list go in after the one before them in the conversion's order
/// (else at the end), enabled; those already there keep their place and are enabled.
/// MO2 is found by its global instances (%LOCALAPPDATA%\ModOrganizer\&lt;instance&gt;), the folder chosen in the app
/// (TamrielMerge.settings.json "mo2", or --mo2), and a running MO2's folder, remembered for next time.
/// </summary>
public static class PluginList
{
    const string SettingsFile = "TamrielMerge.settings.json";

    /// <summary>Enables <paramref name="plugins"/> (in order) in every list found; returns what was done, one line each.</summary>
    public static List<string> Enable(IReadOnlyList<string> plugins, string? mo2 = null)
    {
        var done = new List<string>();
        if (plugins.Count == 0) return done;
        string own = OwnList;
        Merge(own, plugins, star: true);
        done.Add($"enabled in {own}");
        var profiles = Mo2Profiles(mo2).ToList();
        if (profiles.Count == 0) return done;
        if (Process.GetProcessesByName("ModOrganizer").Length > 0)
        {
            done.Add("Mod Organizer 2 is open: close it and install again to enable the plugins there (it rewrites its lists when it exits)");
            return done;
        }
        foreach (var profile in profiles)
        {
            Merge(Path.Combine(profile, "plugins.txt"), plugins, star: true);
            Merge(Path.Combine(profile, "loadorder.txt"), plugins, star: false);
            done.Add($"enabled in Mod Organizer 2's profile {profile}");
        }
        return done;
    }

    /// <summary>Switches <paramref name="plugins"/> on or off where they are listed (and adds them, on, where they aren't);
    /// returns what was done.</summary>
    public static List<string> SetEnabled(IReadOnlyList<string> plugins, bool on, string? mo2 = null)
    {
        if (on) return Enable(plugins, mo2);
        var done = new List<string>();
        string own = OwnList;
        Disable(own, plugins);
        done.Add($"switched off in {own}");
        var profiles = Mo2Profiles(mo2).ToList();
        if (profiles.Count > 0 && Process.GetProcessesByName("ModOrganizer").Length > 0)
            done.Add("Mod Organizer 2 is open: close it and switch again to change its profile too");
        else
            foreach (var profile in profiles) { Disable(Path.Combine(profile, "plugins.txt"), plugins); done.Add($"switched off in Mod Organizer 2's profile {profile}"); }
        return done;
    }

    /// <summary>Takes <paramref name="plugins"/> (gone from Data: stale or retired) off every list; returns what was done.</summary>
    public static List<string> Remove(IReadOnlyList<string> plugins, string? mo2 = null)
    {
        var done = new List<string>();
        if (plugins.Count == 0) return done;
        void Drop(string file)
        {
            if (!File.Exists(file)) return;
            var lines = File.ReadAllLines(file).ToList();
            int before = lines.Count;
            lines.RemoveAll(l => plugins.Contains(l.TrimStart('*').Trim(), StringComparer.OrdinalIgnoreCase));
            if (lines.Count != before) File.WriteAllLines(file, lines, new UTF8Encoding(false));
        }
        Drop(OwnList);
        done.Add($"{string.Join(", ", plugins)} taken off {OwnList}");
        var profiles = Mo2Profiles(mo2).ToList();
        if (profiles.Count > 0 && Process.GetProcessesByName("ModOrganizer").Length > 0)
            done.Add("Mod Organizer 2 is open: close it and install again to take them off its profile too");
        else
            foreach (var profile in profiles) { Drop(Path.Combine(profile, "plugins.txt")); Drop(Path.Combine(profile, "loadorder.txt")); }
        return done;
    }

    /// <summary>Whether a plugin is switched on: in Mod Organizer 2's profile when MO2 manages the game, else in
    /// Skyrim's own list.</summary>
    public static bool IsEnabled(string plugin, string? mo2 = null)
    {
        string list = Mo2Profiles(mo2).FirstOrDefault() is { } profile ? Path.Combine(profile, "plugins.txt") : OwnList;
        return File.Exists(list) && File.ReadLines(list).Any(l => l.StartsWith('*') && l[1..].Trim().Equals(plugin, StringComparison.OrdinalIgnoreCase));
    }

    static string OwnList => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition", "Plugins.txt");

    static void Disable(string file, IReadOnlyList<string> plugins)
    {
        if (!File.Exists(file)) return;
        var lines = File.ReadAllLines(file).Select(l => l.StartsWith('*') && plugins.Contains(l[1..].Trim(), StringComparer.OrdinalIgnoreCase) ? l[1..] : l).ToList();
        File.WriteAllLines(file, lines, new UTF8Encoding(false));
    }

    /// <summary>The folder chosen for Mod Organizer 2 (settings), or null.</summary>
    public static string? Mo2Folder
    {
        get
        {
            try { return File.Exists(SettingsFile) && JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsFile)) is { } s && s.TryGetValue("mo2", out var v) ? v : null; }
            catch (JsonException) { return null; }
        }
        set
        {
            Dictionary<string, string> s;
            // A settings file that can't be read is started again rather than failing (the getter reads it the same way).
            try { s = File.Exists(SettingsFile) ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsFile)) ?? [] : []; }
            catch (JsonException) { s = []; }
            if (string.IsNullOrWhiteSpace(value)) s.Remove("mo2"); else s["mo2"] = value;
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(s, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    static void Merge(string file, IReadOnlyList<string> plugins, bool star)
    {
        var lines = File.Exists(file) ? File.ReadAllLines(file).ToList() : [];
        static string Bare(string l) => l.TrimStart('*').Trim();
        string? previous = null;
        foreach (var p in plugins)
        {
            int at = lines.FindIndex(l => Bare(l).Equals(p, StringComparison.OrdinalIgnoreCase));
            if (at >= 0)
            {
                if (star && !lines[at].StartsWith('*')) lines[at] = "*" + Bare(lines[at]);
            }
            else
            {
                int after = previous is null ? -1 : lines.FindIndex(l => Bare(l).Equals(previous, StringComparison.OrdinalIgnoreCase));
                string line = star ? "*" + p : p;
                if (after >= 0) lines.Insert(after + 1, line); else lines.Add(line);
            }
            previous = p;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllLines(file, lines, new UTF8Encoding(false));
    }

    /// <summary>The selected profile folder of each Mod Organizer 2 instance that manages Skyrim Special Edition.</summary>
    static IEnumerable<string> Mo2Profiles(string? chosen)
    {
        var instances = new List<string>();
        if (!string.IsNullOrWhiteSpace(chosen)) instances.Add(chosen);
        if (Mo2Folder is { } saved) instances.Add(saved);
        // A running MO2: its folder, remembered.
        foreach (var p in Process.GetProcessesByName("ModOrganizer"))
            try
            {
                if (p.MainModule?.FileName is { } exe && Path.GetDirectoryName(exe) is { } dir && File.Exists(Path.Combine(dir, "ModOrganizer.ini")))
                {
                    instances.Add(dir);
                    if (Mo2Folder is null) Mo2Folder = dir;
                }
            }
            catch (System.ComponentModel.Win32Exception) { }
        string global = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ModOrganizer");
        if (Directory.Exists(global)) instances.AddRange(Directory.GetDirectories(global));
        foreach (var dir in instances.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string ini = Path.Combine(dir, "ModOrganizer.ini");
            if (!File.Exists(ini)) continue;
            string text = File.ReadAllText(ini);
            string? Value(string key) => Regex.Match(text, $@"^{key}=(.*)$", RegexOptions.Multiline) is { Success: true } m ? m.Groups[1].Value.Trim() : null;
            if (Value("gameName") is not { } game || !game.Contains("Skyrim Special Edition", StringComparison.OrdinalIgnoreCase)) continue;
            string profile = Regex.Replace(Value("selected_profile") ?? "Default", @"^@ByteArray\((.*)\)$", "$1");
            string baseDir = Value("base_directory") is { Length: > 0 } b ? b.Replace('/', '\\') : dir;
            string profiles = Value("profiles_directory") is { Length: > 0 } pd ? pd.Replace("%BASE_DIR%", baseDir).Replace('/', '\\') : Path.Combine(baseDir, "profiles");
            string folder = Path.Combine(profiles, profile);
            if (Directory.Exists(folder)) yield return folder;
        }
    }
}
