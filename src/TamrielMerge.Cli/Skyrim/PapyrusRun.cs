using System.Text.RegularExpressions;
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
        // The compiler takes a script from the folder it runs in before its import folders: run from a folder holding
        // scripts of the same names, it compiled those (2026-11-13: run from the source tree, a scratch build's edited
        // scripts came out as the tree's). A whole folder's compile (-all) runs in that folder.
        if (string.IsNullOrEmpty(info.WorkingDirectory) && info.ArgumentList.Count > 0 && Directory.Exists(info.ArgumentList[0]))
        {
            // Its paths were given from this process's folder: whole, they mean the same from the other.
            for (int i = 0; i < info.ArgumentList.Count; i++)
            {
                string a = info.ArgumentList[i];
                if (i == 0) info.ArgumentList[i] = Path.GetFullPath(a);
                else if (a.StartsWith("-o=", StringComparison.Ordinal)) info.ArgumentList[i] = "-o=" + Path.GetFullPath(a[3..]);
                else if (a.StartsWith("-i=", StringComparison.Ordinal))
                    info.ArgumentList[i] = "-i=" + string.Join(';', a[3..].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFullPath));
            }
            info.WorkingDirectory = info.ArgumentList[0];
        }
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

    /// <summary>
    /// How a set of scripts is compiled: how many compilers at once and how many scripts each takes. The compiler works
    /// one script at a time on one core, so the batches are dealt over half the logical cores (8 at most: measured
    /// 2026-11-11, 24 scripts each, 1 compiler 1.34 scripts a second, 4 at once 3.67, 8 at once 4.86, and 12 at once on
    /// 16 threads lost a whole batch;
    /// until 2026-11-11 a quarter of them, 4 at most: on an 8-core, 16-thread PC Oblivion's 11,223 scripts took 52
    /// minutes and Morrowind's 4,956 took 25 with the other cores idle; user: "there has to be a way to make this
    /// faster"), in batches small enough that every compiler has work to the end (two rounds of batches, 60 to 400
    /// scripts each).
    /// </summary>
    public static (int Parallel, int Batch) Plan(int scripts)
    {
        int parallel = Math.Clamp(Environment.ProcessorCount / 2, 1, 8);
        if (Environment.GetEnvironmentVariable("TM_PAPYRUS_PARALLEL") is { } p && int.TryParse(p, out int asked) && asked > 0) parallel = asked;
        int batch = Math.Clamp((scripts + parallel * 2 - 1) / (parallel * 2), 60, 400);
        return (parallel, batch);
    }

    /// <summary>
    /// The shipped helper scripts as the compiler needs them to compile a caller: each script's name line, its
    /// properties and its functions' and events' first lines, with nothing inside them, written to
    /// <paramref name="intoDir"/> (made again only when a helper is newer). A converted game's scripts are compiled
    /// against these in place of the helpers themselves. Papyrus binds a call by name when it runs, and what a caller
    /// takes from the helper at compile time is in those lines (the parameters, their default values, the return type,
    /// a property's type), so the compiled caller is the same; the compiler, which reads every script a script names
    /// (and every script those name) again for each script it compiles, has a twentieth to read. Measured 2026-11-11 on
    /// 24 scripts in one compiler: 4.1 s when they call only Skyrim's own scripts, 19.0 s with one call into TMMwScript
    /// (0.79 s a script against 0.17; Morrowind's 4,956 took 25 minutes, Oblivion's 11,223 took 52).
    /// </summary>
    public static string Stubs(string helpersDir, string intoDir)
    {
        Directory.CreateDirectory(intoDir);
        foreach (var source in Directory.GetFiles(helpersDir, "*.psc"))
        {
            string target = Path.Combine(intoDir, Path.GetFileName(source));
            if (File.Exists(target) && File.GetLastWriteTimeUtc(target) >= File.GetLastWriteTimeUtc(source)) continue;
            File.WriteAllText(target, Stub(File.ReadAllText(source)));
        }
        foreach (var old in Directory.GetFiles(intoDir, "*.psc"))
            if (!File.Exists(Path.Combine(helpersDir, Path.GetFileName(old)))) File.Delete(old);
        return intoDir;
    }

    static readonly Regex StubFunction = new(@"^\s*(?:[A-Za-z_][A-Za-z0-9_]*(?:\[\])?\s+)?(Function|Event)\s+[A-Za-z_]", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex StubProperty = new(@"^\s*[A-Za-z_][A-Za-z0-9_]*(?:\[\])?\s+Property\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>One script's stub (see <see cref="Stubs"/>).</summary>
    public static string Stub(string source)
    {
        const char NewLine = (char)10, Return = (char)13, Backslash = (char)92;
        var o = new System.Text.StringBuilder();
        // Comments out first (a ";" outside a string to the line's end, ";/ ... /;" blocks, "{ ... }" doc comments), so a
        // word in one is never taken for a declaration.
        var clean = new System.Text.StringBuilder();
        bool inString = false, block = false, doc = false;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == Return) continue;
            if (block) { if (c == '/' && i + 1 < source.Length && source[i + 1] == ';') { block = false; i++; } else if (c == NewLine) clean.Append(c); continue; }
            if (doc) { if (c == '}') doc = false; else if (c == NewLine) clean.Append(c); continue; }
            if (inString)
            {
                clean.Append(c);
                if (c == Backslash && i + 1 < source.Length) { clean.Append(source[++i]); continue; }
                if (c == '"') inString = false;
                continue;
            }
            if (c == '"') { inString = true; clean.Append(c); continue; }
            if (c == ';' && i + 1 < source.Length && source[i + 1] == '/') { block = true; i++; continue; }
            if (c == ';') { while (i < source.Length && source[i] != NewLine) i++; clean.Append(NewLine); continue; }
            if (c == '{') { doc = true; continue; }
            clean.Append(c);
        }
        // A line ending in a backslash goes on in the next.
        var lines = new List<string>();
        string held = "";
        foreach (var raw in clean.ToString().Split(NewLine))
        {
            string part = raw.TrimEnd();
            if (part.EndsWith(Backslash)) { held += part[..^1] + " "; continue; }
            lines.Add(held + part);
            held = "";
        }
        string skipUntil = "";   // the word that ends the block being left out
        bool inState = false;
        foreach (var line in lines)
        {
            string t = line.Trim();
            if (t.Length == 0) continue;
            if (skipUntil.Length > 0) { if (t.StartsWith(skipUntil, StringComparison.OrdinalIgnoreCase)) skipUntil = ""; continue; }
            if (inState) { if (t.StartsWith("EndState", StringComparison.OrdinalIgnoreCase)) inState = false; continue; }
            if (Regex.IsMatch(t, @"^(Auto\s+)?State\s+[A-Za-z_]", RegexOptions.IgnoreCase)) { inState = true; continue; }
            if (t.StartsWith("Scriptname", StringComparison.OrdinalIgnoreCase)) { o.AppendLine(t); continue; }
            var fn = StubFunction.Match(t);
            if (fn.Success)
            {
                o.AppendLine(t);
                if (Regex.IsMatch(t, @"\)\s*(?:[A-Za-z]+\s+)*Native\b", RegexOptions.IgnoreCase)) continue;
                string end = fn.Groups[1].Value.Equals("Event", StringComparison.OrdinalIgnoreCase) ? "EndEvent" : "EndFunction";
                o.AppendLine(end);
                skipUntil = end;
                continue;
            }
            if (StubProperty.IsMatch(t))
            {
                if (Regex.IsMatch(t, @"\bAuto(ReadOnly)?\b", RegexOptions.IgnoreCase)) o.AppendLine(t);
                else { o.AppendLine(Regex.Replace(t, @"\s*=.*$", "") + " Auto"); skipUntil = "EndProperty"; }
                continue;
            }
            // Anything else at the script's own level (its variables, its imports) is nothing a caller sees.
        }
        return o.ToString();
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
        RestoreCompiled(sourceDir, outDir, helpers, names, encoding);
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

    /// <summary>Scripts taken from the cache instead of compiled, and stored in it, by this process (for the log).</summary>
    public static int Restored, Stored;

    /// <summary>
    /// What a script's compiled form depends on beside its own source: the helpers' and every other script's signatures
    /// (Papyrus compiles a call against the callee's header, and puts default arguments in the caller) and the compiler.
    /// A changed body recompiles its own script; a changed header anywhere recompiles them all.
    /// </summary>
    static byte[] CompileContext(string sourceDir, string? helpers, IReadOnlyCollection<string> names, System.Text.Encoding encoding)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        void Add(string s) => sha.AppendData(System.Text.Encoding.UTF8.GetBytes(s + "\n"));
        Add("v1");
        if (helpers is not null && Directory.Exists(helpers))
            foreach (var psc in Directory.GetFiles(helpers, "*.psc").Order(StringComparer.OrdinalIgnoreCase))
                Add(Path.GetFileNameWithoutExtension(psc).ToLowerInvariant() + "\t" + Signature(File.ReadAllText(psc)));
        var parts = new string[names.Count];
        var ordered = names.Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Parallel.For(0, ordered.Length, i =>
        {
            string file = Path.Combine(sourceDir, ordered[i] + ".psc");
            parts[i] = ordered[i].ToLowerInvariant() + "\t" + (File.Exists(file) ? Signature(File.ReadAllText(file, encoding)) : "");
        });
        foreach (var part in parts) Add(part);
        return sha.GetHashAndReset();
    }

    static string CompiledKey(byte[] context, string pscFile)
    {
        using var sha = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        sha.AppendData(context);
        sha.AppendData(File.ReadAllBytes(pscFile));
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    static string? CompiledCache()
    {
        string dir = Util.Paths.Cache("pex");
        // Only the lasting cache (beside the output): the tool's own out folder is not one.
        return dir.Contains("TamrielMerge Cache", StringComparison.OrdinalIgnoreCase) || Environment.GetEnvironmentVariable("TAMRIELMERGE_CACHE") is { Length: > 0 } ? dir : null;
    }

    /// <summary>
    /// Compiled scripts an earlier conversion kept (<see cref="KeepCompiled"/>) put into <paramref name="outDir"/> for
    /// the sources that are the same, compiled against the same headers: a conversion starts from an empty build
    /// folder, and compiled Oblivion's 11,223 scripts in 47.7 minutes and Morrowind's 4,947 in 22.6 every time
    /// (2026-11-07), where a folder that still had them took 50 and 23 seconds (2026-11-01).
    /// </summary>
    static void RestoreCompiled(string sourceDir, string outDir, string? helpers, IReadOnlyCollection<string> names, System.Text.Encoding encoding)
    {
        if (CompiledCache() is not { } cache || !Directory.Exists(cache)) return;
        var missing = names.Where(n => !File.Exists(Path.Combine(outDir, n + ".pex")) && File.Exists(Path.Combine(sourceDir, n + ".psc"))).ToList();
        if (missing.Count == 0) return;
        var context = CompileContext(sourceDir, helpers, names, encoding);
        Directory.CreateDirectory(outDir);
        int got = 0;
        Parallel.ForEach(missing, n =>
        {
            string psc = Path.Combine(sourceDir, n + ".psc");
            string key = CompiledKey(context, psc), kept = Path.Combine(cache, key[..2], key + ".pex");
            if (!File.Exists(kept)) return;
            string pex = Path.Combine(outDir, n + ".pex");
            File.Copy(kept, pex, true);
            File.SetLastWriteTimeUtc(pex, DateTime.UtcNow);
            System.Threading.Interlocked.Increment(ref got);
        });
        if (got == 0) return;
        System.Threading.Interlocked.Add(ref Restored, got);
        // They were compiled against these helpers: nothing of them is stale on that account.
        if (!File.Exists(Path.Combine(outDir, SignatureFile))) KeepHelperSignatures(outDir, helpers);
        Console.WriteLine($"  scripts: {got} of {names.Count} taken from the compiled cache ({cache})");
    }

    /// <summary>Keeps this folder's compiled scripts for the next conversion (each under its source's and headers' hash).</summary>
    public static void KeepCompiled(string sourceDir, string outDir, string? helpers, IReadOnlyCollection<string> names, System.Text.Encoding? encoding = null)
    {
        if (CompiledCache() is not { } cache) return;
        encoding ??= new System.Text.UTF8Encoding(false);
        try
        {
            var context = CompileContext(sourceDir, helpers, names, encoding);
            int kept = 0;
            Parallel.ForEach(names, n =>
            {
                string psc = Path.Combine(sourceDir, n + ".psc"), pex = Path.Combine(outDir, n + ".pex");
                if (!File.Exists(psc) || !File.Exists(pex) || File.GetLastWriteTimeUtc(pex) < File.GetLastWriteTimeUtc(psc)) return;
                string key = CompiledKey(context, psc), target = Path.Combine(cache, key[..2], key + ".pex");
                if (File.Exists(target)) return;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(pex, target, true);
                System.Threading.Interlocked.Increment(ref kept);
            });
            System.Threading.Interlocked.Add(ref Stored, kept);
            if (kept > 0) Console.WriteLine($"  scripts: {kept} compiled scripts kept for the next conversion ({cache})");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Console.WriteLine($"  scripts: compiled cache not written ({e.Message})"); }
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
