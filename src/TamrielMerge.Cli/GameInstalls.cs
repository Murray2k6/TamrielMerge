using Microsoft.Win32;

namespace TamrielMerge;

/// <summary>The Elder Scrolls games TamrielMerge converts, in story order.</summary>
public enum Game { Daggerfall, Redguard, Morrowind, Oblivion }

/// <summary>Where a game was bought and installed from.</summary>
public enum Store { Steam, GOG }

/// <summary>
/// Finds the games installed through a store they were bought (or claimed) from, and only those: a copy that is
/// merely present in some folder is not converted. A game counts as installed when
/// <list type="bullet">
/// <item>Steam lists it: its app manifest (steamapps\appmanifest_&lt;app id&gt;.acf) is in one of the Steam libraries
/// (libraryfolders.vdf), and the manifest's install folder holds the game's data; or</item>
/// <item>GOG installed it: GOG's installer registered it under SOFTWARE\GOG.com\Games (gameName, path), and that
/// folder holds the game's data.</item>
/// </list>
/// Skyrim Special Edition itself is found the same way. Games that aren't found are left out of the conversion and
/// the story order; nothing else is needed for the others.
/// </summary>
public static class GameInstalls
{
    /// <summary>A store install: which store, the game's folder and its data folder.</summary>
    public sealed record Install(Store Store, string Root, string DataDir);

    sealed record Spec(int[] SteamApps, string[] GogNames, Func<string, string?> DataDir);

    static readonly Dictionary<Game, Spec> Specs = new()
    {
        [Game.Daggerfall] = new([1812390], ["Daggerfall"], root => FirstDir(root, @"DF\DAGGER\ARENA2", @"DAGGER\ARENA2", "ARENA2")),
        [Game.Redguard] = new([1812410], ["Redguard"], root => FirstDir(root, "Redguard", @"Redguard\Redguard")),
        [Game.Morrowind] = new([22320], ["Morrowind"],
            root => File.Exists(Path.Combine(root, "Data Files", "Morrowind.esm")) ? Path.Combine(root, "Data Files") : null),
        [Game.Oblivion] = new([22330], ["Oblivion"],
            root => File.Exists(Path.Combine(root, "Data", "Oblivion.esm")) ? Path.Combine(root, "Data") : null),
    };

    static readonly Spec SkyrimSpec = new([489830], ["Skyrim Special Edition", "Skyrim Anniversary Edition"],
        root => File.Exists(Path.Combine(root, "Data", "Skyrim.esm")) && File.Exists(Path.Combine(root, "Data", "Dragonborn.esm")) ? Path.Combine(root, "Data") : null);

    static string? FirstDir(string root, params string[] subdirs) =>
        subdirs.Select(s => Path.Combine(root, s)).FirstOrDefault(Directory.Exists);

    /// <summary>The game's store install, or null when no store has it installed.</summary>
    public static Install? Find(Game game) => Find(Specs[game]);

    /// <summary>The game's data folder (ARENA2, Data Files, ...), or null when no store has it installed.</summary>
    public static string? DataDir(Game game) => Find(game)?.DataDir;

    /// <summary>Skyrim Special Edition's Data folder from its store install, or null.</summary>
    public static string? SkyrimData() => Find(SkyrimSpec)?.DataDir;

    /// <summary>Every game installed through a store, with its install.</summary>
    public static Dictionary<Game, Install> Detect() =>
        Enum.GetValues<Game>().Select(g => (g, Find(g))).Where(x => x.Item2 is not null).ToDictionary(x => x.g, x => x.Item2!);

    static Install? Find(Spec spec)
    {
        foreach (var root in SteamInstalls(spec.SteamApps))
            if (spec.DataDir(root) is { } data) return new Install(Store.Steam, root, data);
        foreach (var root in GogInstalls(spec.GogNames))
            if (spec.DataDir(root) is { } data) return new Install(Store.GOG, root, data);
        return null;
    }

    /// <summary>Install folders of these Steam apps: each library's steamapps\appmanifest_&lt;id&gt;.acf names its
    /// folder under steamapps\common.</summary>
    static IEnumerable<string> SteamInstalls(int[] apps)
    {
        foreach (var lib in SteamLibraries().Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var app in apps)
            {
                string manifest = Path.Combine(lib, "steamapps", $"appmanifest_{app}.acf");
                if (!File.Exists(manifest)) continue;
                var m = System.Text.RegularExpressions.Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                if (m.Success) yield return Path.Combine(lib, "steamapps", "common", m.Groups[1].Value);
            }
    }

    static IEnumerable<string> SteamLibraries()
    {
        if (!OperatingSystem.IsWindows()) yield break;
        var steam = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string
                    ?? Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string;
        if (steam is null) yield break;
        yield return Path.GetFullPath(steam);
        string vdf = Path.Combine(steam, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
            yield return Path.GetFullPath(m.Groups[1].Value.Replace(@"\\", @"\"));
    }

    /// <summary>Folders of GOG installs whose registered name contains one of the names (GOG's installer records each
    /// game under SOFTWARE\GOG.com\Games\{product id}).</summary>
    static IEnumerable<string> GogInstalls(string[] names)
    {
        if (!OperatingSystem.IsWindows()) yield break;
        foreach (var rootKey in new[] { @"SOFTWARE\WOW6432Node\GOG.com\Games", @"SOFTWARE\GOG.com\Games" })
        {
            using var games = Registry.LocalMachine.OpenSubKey(rootKey);
            if (games is null) continue;
            foreach (var id in games.GetSubKeyNames())
            {
                using var key = games.OpenSubKey(id);
                if (key?.GetValue("path") is not string path || key.GetValue("gameName") is not string name) continue;
                if (names.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase))) yield return path;
            }
        }
    }
}
