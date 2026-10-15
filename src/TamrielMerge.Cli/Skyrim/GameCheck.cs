using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Finds what went wrong in a game session (user, 2026-10-14: "make a script that can scan for failures and errors on
/// game launch"; the converted plugins crashed the game while it loaded, and nothing said where).
/// <list type="bullet">
/// <item>Prepare: Papyrus logging, tracing and debug line numbers on (SkyrimCustom.ini), and Crash Logger SSE (MIT,
/// fetched by setup) in Data\SKSE\Plugins, so a crash writes Documents\My Games\Skyrim Special Edition\SKSE\Crashlog-*.log.</item>
/// <item>Launch (when asked): the game through skse64_loader.exe, watched until it ends: how long it ran and the most memory
/// it used.</item>
/// <item>Scan, of the session's logs: how far the game got (SKSE's messages to its plugins: data loaded means the plugins
/// were all read), the crash log (the exception, the call stack, the records it names and their plugins), Windows' record
/// of the crash, and Papyrus' errors and warnings from TamrielMerge's scripts (TM*, OB_*, MW*, DF*, RG*), grouped.</item>
/// </list>
/// The report goes to the console and logs\game-check-&lt;time&gt;.txt.
/// </summary>
public static class GameCheck
{
    static string Docs => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games", "Skyrim Special Edition");

    /// <summary>The converted games' plugins and TamrielMerge's own: what a crash or an error is checked against.</summary>
    static readonly string[] OurPlugins = ["Daggerfall", "Redguard", "Morrowind", "Oblivion", "TamrielMerge"];
    static readonly Regex OurScript = new(@"\b(TM[A-Z]\w*|OB_\w+|MW\w*|DF[A-Z]\w*|RG[A-Z]\w*)\b", RegexOptions.Compiled);

    public static void Prepare(string skyrimData, Action<string> log)
    {
        // Papyrus logging (the game's own settings in Documents, not the game folder).
        string ini = Path.Combine(Docs, "SkyrimCustom.ini");
        var lines = File.Exists(ini) ? File.ReadAllLines(ini).ToList() : [];
        void Set(string section, string key, string value)
        {
            int start = lines.FindIndex(l => l.Trim().Equals($"[{section}]", StringComparison.OrdinalIgnoreCase));
            if (start < 0) { lines.Add($"[{section}]"); lines.Add($"{key}={value}"); return; }
            int end = lines.FindIndex(start + 1, l => l.TrimStart().StartsWith('['));
            if (end < 0) end = lines.Count;
            int at = lines.FindIndex(start + 1, end - start - 1, l => l.Split('=')[0].Trim().Equals(key, StringComparison.OrdinalIgnoreCase));
            if (at >= 0) lines[at] = $"{key}={value}"; else lines.Insert(end, $"{key}={value}");
        }
        Set("Papyrus", "bEnableLogging", "1");
        Set("Papyrus", "bEnableTrace", "1");
        Set("Papyrus", "bLoadDebugInformation", "1");
        Directory.CreateDirectory(Docs);
        File.WriteAllLines(ini, lines);
        log($"Papyrus logging on ({ini})");

        // The crash logger beside the other SKSE plugins.
        string from = Setup.Dependencies.CrashLogger;
        if (!File.Exists(from)) { log("Crash Logger isn't set up: run Install missing tools (setup) first; a crash won't say where it happened"); return; }
        string plugins = Path.Combine(skyrimData, "SKSE", "Plugins");
        Directory.CreateDirectory(plugins);
        foreach (var f in Directory.GetFiles(Path.GetDirectoryName(from)!))
            File.Copy(f, Path.Combine(plugins, Path.GetFileName(f)), overwrite: true);
        log($"Crash Logger installed ({plugins})");
    }

    /// <summary>Starts the game through SKSE and waits for it to end; returns when it started, and the most memory it used.</summary>
    public static (DateTime Started, long PeakBytes, TimeSpan Ran)? Launch(string skyrimData, Action<string> log)
    {
        string root = Path.GetDirectoryName(Path.GetFullPath(skyrimData.TrimEnd('\\', '/')))!;
        string loader = Path.Combine(root, "skse64_loader.exe");
        if (!File.Exists(loader)) { log($"SKSE isn't installed ({loader} is missing)"); return null; }
        var started = DateTime.Now;
        Process.Start(new ProcessStartInfo(loader) { WorkingDirectory = root, UseShellExecute = true });
        log("Starting Skyrim through SKSE...");
        Process? game = null;
        for (int i = 0; i < 120 && game is null; i++)
        {
            Thread.Sleep(1000);
            game = Process.GetProcessesByName("SkyrimSE").FirstOrDefault();
        }
        if (game is null) { log("Skyrim didn't start within two minutes"); return (started, 0, TimeSpan.Zero); }
        long peak = 0;
        var watch = Stopwatch.StartNew();
        while (!game.HasExited)
        {
            try { game.Refresh(); peak = Math.Max(peak, game.PrivateMemorySize64); } catch (InvalidOperationException) { break; }
            Thread.Sleep(1000);
        }
        log($"Skyrim ended after {watch.Elapsed:hh\\:mm\\:ss}; it used up to {peak / (double)(1L << 30):F1} GB of memory");
        return (started, peak, watch.Elapsed);
    }

    /// <summary>The newest copy of an SKSE plugin's log: beside the plugin in the game's Data, in Mod Organizer 2's
    /// overwrite folder, or in Documents\My Games\Skyrim Special Edition\SKSE.</summary>
    static string? PluginLog(string name, string? skyrimData)
    {
        var places = new List<string>();
        if (!string.IsNullOrEmpty(skyrimData)) places.Add(Path.Combine(skyrimData, "SKSE", "Plugins", name));
        if (Setup.PluginList.Mo2Folder is { } mo2) places.Add(Path.Combine(mo2, "overwrite", "SKSE", "Plugins", name));
        places.Add(Path.Combine(Docs, "SKSE", name));
        return places.Where(File.Exists).OrderByDescending(File.GetLastWriteTime).FirstOrDefault();
    }

    /// <summary>The graphics plugin's state in the session: DLSS, the ray tracing (its camera, sun and tonemapping pass
    /// found, its last report: meshes, GPU time, the line-up check), the seamless transitions' menus.</summary>
    static void GraphicsSummary(string[] lines, string path, StringBuilder r)
    {
        r.AppendLine($"Graphics plugin ({path}):");
        string? Last(string pattern) => lines.LastOrDefault(l => Regex.IsMatch(l, pattern));
        string? First(string pattern) => lines.FirstOrDefault(l => Regex.IsMatch(l, pattern));
        var picks = new List<string?>
        {
            First("^DLSS available"), Last("^DLSS: (DLAA|DLSS)"), First("^DLSS: first frame"), Last("dynamic resolution (locked|let go)"),
            First("^jitter check"),
            First("^ray tracing: Direct3D 12 device"), First("^ray tracing: ready"), Last("^ray tracing: OFF"),
            First("^ray tracing: camera frustum"), First("^ray tracing: camera axes|world-to-clip matrix didn't"), Last("^ray tracing: sun at"),
            First("^ray tracing: tonemapping pass found"), Last("^ray tracing: .*instances"), Last("line up|lined up again"),
            First("^transitions: the game's menus found"), Last("^transitions: "),
        };
        var shown = picks.Where(p => p is not null).Distinct().ToList();
        if (shown.Count == 0) r.AppendLine("  (nothing from DLSS, ray tracing or the transitions in this session)");
        foreach (var l in shown) r.AppendLine($"  {l!.Trim()}");
        var fps = lines.Where(l => l.StartsWith("frame ")).ToList();
        if (fps.Count > 0) r.AppendLine($"  {fps[^1].Trim()} (the last of {fps.Count} frame-rate lines)");
    }

    public static string Scan(DateTime since, string workspace, string? skyrimData = null)
    {
        var r = new StringBuilder();
        r.AppendLine($"TamrielMerge game check, {DateTime.Now:yyyy-MM-dd HH:mm} (the session since {since:yyyy-MM-dd HH:mm})");
        r.AppendLine();

        // How far it got: SKSE's messages to its plugins.
        string skse = Path.Combine(Docs, "SKSE", "skse64.log");
        if (File.Exists(skse) && File.GetLastWriteTime(skse) >= since.AddMinutes(-1))
        {
            var text = File.ReadAllLines(skse);
            var messages = text.Select(l => Regex.Match(l, @"dispatch message \((\d+)\)")).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value)).ToList();
            string stage = messages.Contains(8)
                ? messages.Contains(7) ? "a new game was started" : messages.Contains(3) ? "a saved game was loaded" : "every plugin was read (the main menu was reached)"
                : messages.Contains(6) ? "STOPPED WHILE READING THE PLUGINS: SKSE never heard that the game's data had loaded" : "stopped before SKSE's plugins were ready";
            r.AppendLine($"How far the game got: {stage}");
            foreach (var l in text.Where(l => l.Contains("couldn't load", StringComparison.OrdinalIgnoreCase) || l.Contains("disabled", StringComparison.OrdinalIgnoreCase) || l.Contains("error", StringComparison.OrdinalIgnoreCase)))
                r.AppendLine($"  SKSE: {l.Trim()}");
        }
        else r.AppendLine("How far the game got: no SKSE log from this session (was the game started through SKSE?)");
        r.AppendLine();

        // The crash log.
        var crash = Directory.Exists(Path.Combine(Docs, "SKSE"))
            ? Directory.GetFiles(Path.Combine(Docs, "SKSE"), "Crashlog-*.log").Select(f => new FileInfo(f)).Where(f => f.LastWriteTime >= since).OrderByDescending(f => f.LastWriteTime).FirstOrDefault()
            : null;
        if (crash is not null) CrashLog(crash.FullName, r);
        else r.AppendLine("Crash log: none from this session (no crash, or Crash Logger isn't installed: run the check with --prepare first)");
        r.AppendLine();

        // Windows' record of a crash.
        foreach (var e in WindowsCrashes(since)) r.AppendLine($"Windows: {e}");

        // Papyrus.
        string papyrus = Path.Combine(Docs, "Logs", "Script", "Papyrus.0.log");
        if (File.Exists(papyrus) && File.GetLastWriteTime(papyrus) >= since.AddMinutes(-1)) PapyrusErrors(papyrus, r);
        else r.AppendLine("Papyrus: no log from this session (logging is turned on by --prepare)");

        // TamrielMerge's own SKSE plugins. They write their logs beside themselves (Data\SKSE\Plugins, or Mod Organizer
        // 2's overwrite folder when the game runs through it), not in Documents.
        foreach (var name in new[] { "TamrielMergeEra.log", "TamrielMergeGraphics.log" })
        {
            string? f = PluginLog(name, skyrimData);
            if (f is null || File.GetLastWriteTime(f) < since.AddMinutes(-1)) continue;
            var lines = File.ReadAllLines(f);
            var bad = lines.Where(l => Regex.IsMatch(l, "error|fail|not found|unsupported|OFF:|doesn't line up|took over", RegexOptions.IgnoreCase)).ToList();
            if (bad.Count > 0) { r.AppendLine($"{name}:"); foreach (var l in bad.Take(20)) r.AppendLine($"  {l.Trim()}"); }
            if (name == "TamrielMergeGraphics.log") GraphicsSummary(lines, f, r);
        }

        string report = r.ToString();
        string dir = Path.Combine(workspace, "logs");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, $"game-check-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
        File.WriteAllText(file, report);
        return report + Environment.NewLine + $"Report saved to {file}" + Environment.NewLine;
    }

    /// <summary>A Crash Logger SSE log: the exception, the top of the call stack, and every record it names with its plugin
    /// (TamrielMerge's plugins first: the likely cause).</summary>
    static void CrashLog(string path, StringBuilder r)
    {
        var lines = File.ReadAllLines(path);
        r.AppendLine($"CRASH: {Path.GetFileName(path)}");
        if (lines.FirstOrDefault(l => l.StartsWith("Unhandled exception", StringComparison.OrdinalIgnoreCase)) is { } ex) r.AppendLine($"  {ex.Trim()}");
        int stack = Array.FindIndex(lines, l => l.StartsWith("PROBABLE CALL STACK", StringComparison.OrdinalIgnoreCase));
        if (stack >= 0)
        {
            r.AppendLine("  Call stack (top):");
            foreach (var l in lines.Skip(stack + 1).TakeWhile(l => l.StartsWith('\t') || l.StartsWith(' ')).Take(10)) r.AppendLine($"    {l.Trim()}");
        }
        // Records named in the registers and on the stack: a block of indented lines with Name/EditorID/FormID/File.
        var named = new List<(string Plugin, string What)>();
        for (int i = 0; i < lines.Length; i++)
        {
            var file = Regex.Match(lines[i], "File:\\s*\"([^\"]+)\"");
            if (!file.Success) continue;
            var block = new List<string>();
            for (int j = Math.Max(0, i - 8); j <= Math.Min(lines.Length - 1, i + 2); j++)
                if (Regex.IsMatch(lines[j], @"(Name|EditorID|FormID|FormType):", RegexOptions.IgnoreCase)) block.Add(lines[j].Trim());
            named.Add((file.Groups[1].Value, string.Join("; ", block.Distinct())));
        }
        var ours = named.Where(n => OurPlugins.Any(p => n.Plugin.Contains(p, StringComparison.OrdinalIgnoreCase))).ToList();
        if (ours.Count > 0)
        {
            r.AppendLine("  Records from TamrielMerge's plugins the crash involves:");
            foreach (var g in ours.GroupBy(n => (n.Plugin, n.What)).Take(15)) r.AppendLine($"    {g.Key.Plugin}: {g.Key.What}");
        }
        foreach (var g in named.Except(ours).GroupBy(n => n.Plugin).Take(8)) r.AppendLine($"  Also named: {g.Key} ({g.Count()})");
        if (named.Count == 0) r.AppendLine("  No records named: the crash happened outside a record (reading a plugin's file, memory, a DLL)");
    }

    /// <summary>Windows' record of Skyrim crashing or hanging (its Application log, read with wevtutil).</summary>
    static IEnumerable<string> WindowsCrashes(DateTime since)
    {
        var found = new List<string>();
        try
        {
            string time = since.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.000Z");
            var psi = new ProcessStartInfo("wevtutil", $"qe Application /q:\"*[System[(Provider[@Name='Application Error'] or Provider[@Name='Application Hang']) and TimeCreated[@SystemTime>='{time}']]]\" /f:text /c:50")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string text = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            foreach (var e in text.Split("Event[", StringSplitOptions.RemoveEmptyEntries).Where(e => e.Contains("SkyrimSE.exe", StringComparison.OrdinalIgnoreCase)))
            {
                var module = Regex.Match(e, @"Faulting module name: ([^,
]+)");
                var code = Regex.Match(e, @"Exception code: (0x[0-9a-fA-F]+)");
                var offset = Regex.Match(e, @"Fault offset: (0x[0-9a-fA-F]+)");
                var date = Regex.Match(e, @"Date: (\S+)");
                found.Add(module.Success
                    ? $"Skyrim crashed ({date.Groups[1].Value}): module {module.Groups[1].Value.Trim()}, exception {code.Groups[1].Value}, offset {offset.Groups[1].Value}"
                    : $"Skyrim stopped responding ({date.Groups[1].Value})");
            }
        }
        catch (Exception e) { found.Add($"(couldn't read Windows' event log: {e.Message})"); }
        return found;
    }

    /// <summary>Papyrus' errors and warnings that come from TamrielMerge's scripts (by the message or its stack), grouped.</summary>
    static void PapyrusErrors(string path, StringBuilder r)
    {
        var lines = File.ReadAllLines(path);
        var groups = new Dictionary<string, (int Count, string Example)>();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = Regex.Match(lines[i], @"^\[[^\]]+\]\s*(error|warning):\s*(.*)$", RegexOptions.IgnoreCase);
            var missing = Regex.Match(lines[i], "Cannot open store for class \"([^\"]+)\"");
            string? message = m.Success ? $"{m.Groups[1].Value.ToLowerInvariant()}: {m.Groups[2].Value}" : missing.Success ? $"missing script: {missing.Groups[1].Value}" : null;
            if (message is null) continue;
            var stack = new List<string>();
            for (int j = i + 1; j < lines.Length && (lines[j].StartsWith('\t') || lines[j].StartsWith("stack:", StringComparison.OrdinalIgnoreCase)); j++) stack.Add(lines[j].Trim());
            if (!OurScript.IsMatch(message) && !stack.Any(OurScript.IsMatch)) continue;
            // Grouped by the message without its form IDs and numbers, so one fault counts once.
            string key = Regex.Replace(message, @"\(([0-9A-Fa-f]{8})\)|\b\d+(\.\d+)?\b", "#");
            string where = stack.FirstOrDefault(s => OurScript.IsMatch(s)) ?? "";
            groups[key] = groups.TryGetValue(key, out var g) ? (g.Count + 1, g.Example) : (1, where.Length > 0 ? $"{message}\n      at {where}" : message);
        }
        if (groups.Count == 0) { r.AppendLine("Papyrus: no errors or warnings from TamrielMerge's scripts"); return; }
        r.AppendLine($"Papyrus: {groups.Values.Sum(g => g.Count)} errors and warnings from TamrielMerge's scripts, {groups.Count} kinds:");
        foreach (var (_, g) in groups.OrderByDescending(kv => kv.Value.Count).Take(60)) r.AppendLine($"  {g.Count,5} x {g.Example}");
    }
}
