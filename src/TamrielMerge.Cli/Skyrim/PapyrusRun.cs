using System.Diagnostics;

namespace TamrielMerge.Skyrim;

/// <summary>Runs the Creation Kit's Papyrus compiler.</summary>
public static class PapyrusRun
{
    /// <summary>
    /// Runs the compiler and returns its output, with null for the exit code when it stopped answering and was stopped.
    /// Both of its streams are read at once, and it is given up after <paramref name="timeout"/> with the assemblers it
    /// started (2026-10-15: a 76-script -all compile sat for 24 minutes with ten PapyrusAssembler processes doing nothing,
    /// holding the output pipe open, so the build never went on).
    /// </summary>
    public static (int? ExitCode, string Output) Run(ProcessStartInfo info, TimeSpan timeout)
    {
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var p = Process.Start(info)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        int? code = null;
        if (p.WaitForExit(timeout)) code = p.ExitCode;
        else try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { }
        // An assembler left behind (started after the tree was read) can still hold the pipe: the output read so far is enough.
        Task.WaitAll([output, error], TimeSpan.FromSeconds(30));
        string text = (output.IsCompletedSuccessfully ? output.Result : "") + (error.IsCompletedSuccessfully ? error.Result : "");
        return (code, code is null ? text + $"\n(the compiler stopped answering after {timeout.TotalMinutes:0} minutes and was stopped)\n" : text);
    }

    /// <summary>
    /// What the compiler said, counted: its own messages ("file(line,col): ..."), every one an error (the compiler,
    /// PCompiler.dll, has no warnings: 2026-10-23, none among its message strings), and the assembler's, which counts its
    /// own errors and warnings per script ("N error(s), M warning(s)") and prints a warning's line before it. The compiler
    /// must run without -q, which hides the assembler's lines.
    /// </summary>
    public static (int Errors, int Warnings, List<string> Lines) Tally(string log)
    {
        var lines = new List<string>();
        int errors = 0, warnings = 0;
        var summary = new System.Text.RegularExpressions.Regex(@"^(\d+) error\(s\), (\d+) warning\(s\)");
        foreach (var raw in log.Split('\n'))
        {
            string l = raw.Trim();
            if (summary.Match(l) is { Success: true } m)
            {
                errors += int.Parse(m.Groups[1].Value);
                warnings += int.Parse(m.Groups[2].Value);
            }
            else if (l.Contains("): ") || l.Contains("warning", StringComparison.OrdinalIgnoreCase) && !l.StartsWith("Batch compile")) lines.Add(l);
        }
        lines = lines.Distinct().ToList();
        // The compiler's messages are errors of its own, before assembly.
        errors += lines.Count(l => l.Contains("): ") && !l.Contains("warning", StringComparison.OrdinalIgnoreCase));
        return (errors, warnings, lines);
    }

    /// <summary>The batch compile's time limit: generous for a few hundred scripts, short of a hang.</summary>
    public static readonly TimeSpan Batch = TimeSpan.FromMinutes(10);

    /// <summary>One script's time limit.</summary>
    public static readonly TimeSpan One = TimeSpan.FromMinutes(2);

    /// <summary>Writes a script's source unless the file already holds it (its date then still says when it last
    /// changed, which <see cref="Stale"/> goes by). True when written.</summary>
    public static bool WriteIfChanged(string path, string source, System.Text.Encoding? encoding = null)
    {
        encoding ??= new System.Text.UTF8Encoding(false);
        if (File.Exists(path) && File.ReadAllText(path, encoding) == source) return false;
        File.WriteAllText(path, source, encoding);
        return true;
    }

    /// <summary>
    /// The scripts of <paramref name="sourceDir"/> that need compiling into <paramref name="outDir"/>: those without a
    /// .pex as new as their source, all of them when a shipped helper script (<paramref name="helpers"/>) is newer than
    /// their .pex, and those naming a script that needs compiling. Papyrus binds calls by name when the script runs and
    /// puts a call's default arguments in the caller, so a script left as it was would call a changed one the old way.
    /// </summary>
    public static HashSet<string> Stale(string sourceDir, string outDir, string? helpers, IReadOnlyCollection<string> names, System.Text.Encoding? encoding = null)
    {
        encoding ??= new System.Text.UTF8Encoding(false);
        DateTime Built(string n) => File.Exists(Path.Combine(outDir, n + ".pex")) ? File.GetLastWriteTimeUtc(Path.Combine(outDir, n + ".pex")) : DateTime.MinValue;
        var stale = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names)
        {
            var built = Built(n);
            if (built == DateTime.MinValue || built < File.GetLastWriteTimeUtc(Path.Combine(sourceDir, n + ".psc"))) stale.Add(n);
        }
        // The helpers whose signatures (what a caller compiles against) changed since this folder's last compile: those
        // naming them are compiled again. A changed body needs nothing of its callers.
        var changedHelpers = ChangedHelpers(outDir, helpers);
        if (stale.Count == names.Count) return stale;
        if (stale.Count == 0 && changedHelpers.Count == 0) return stale;
        var words = new System.Text.RegularExpressions.Regex(@"[A-Za-z_][A-Za-z0-9_]*");
        var uses = names.Where(n => !stale.Contains(n)).ToDictionary(n => n,
            n => words.Matches(File.ReadAllText(Path.Combine(sourceDir, n + ".psc"), encoding)).Select(m => m.Value).ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        foreach (var (n, used) in uses)
            if (used.Overlaps(changedHelpers)) stale.Add(n);
        // Those naming a stale one, and so on (a quest's fragments name its stage scripts, a script the functions it calls).
        for (bool more = true; more;)
        {
            more = false;
            foreach (var (n, used) in uses)
                if (!stale.Contains(n) && used.Overlaps(stale)) { stale.Add(n); more = true; }
        }
        return stale;
    }

    const string SignatureFile = "helpers.signatures";

    /// <summary>A helper script's signature: what a script calling it compiles against (its name and what it extends,
    /// its properties, its functions' and events' headers), comments and bodies left out.</summary>
    static string Signature(string source)
    {
        var lines = new List<string>();
        foreach (var raw in source.Split('\n'))
        {
            string line = raw.Trim();
            int comment = line.IndexOf(';');
            if (comment >= 0) line = line[..comment].Trim();
            if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^(ScriptName\b|\S+\s+Property\b|(\S+\s+)?Function\b|Event\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                lines.Add(System.Text.RegularExpressions.Regex.Replace(line, @"\s+", " ").ToLowerInvariant());
        }
        return string.Join("\n", lines);
    }

    /// <summary>The helpers (script names) whose signature differs from the one this output folder was last compiled
    /// against (all of them the first time); <see cref="KeepHelperSignatures"/> records the new ones.</summary>
    static HashSet<string> ChangedHelpers(string outDir, string? helpers)
    {
        var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (helpers is null || !Directory.Exists(helpers)) return changed;
        var known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string file = Path.Combine(outDir, SignatureFile);
        if (File.Exists(file))
            foreach (var line in File.ReadAllLines(file))
                if (line.Split('\t') is [var name, var hash]) known[name] = hash;
        foreach (var psc in Directory.GetFiles(helpers, "*.psc"))
        {
            string name = Path.GetFileNameWithoutExtension(psc);
            string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Signature(File.ReadAllText(psc)))));
            if (!known.TryGetValue(name, out var was) || was != hash) changed.Add(name);
        }
        return changed;
    }

    /// <summary>Records the helpers' signatures this folder's scripts are now compiled against.</summary>
    public static void KeepHelperSignatures(string outDir, string? helpers)
    {
        if (helpers is null || !Directory.Exists(helpers)) return;
        Directory.CreateDirectory(outDir);
        File.WriteAllLines(Path.Combine(outDir, SignatureFile), Directory.GetFiles(helpers, "*.psc").Order(StringComparer.OrdinalIgnoreCase).Select(psc =>
            Path.GetFileNameWithoutExtension(psc) + "\t" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(Signature(File.ReadAllText(psc)))))));
    }
}
