using System.Diagnostics;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's scripts in the converted plugin (see <see cref="MwPapyrus"/> for the translation): compiled with the
/// Creation Kit's Papyrus compiler against Skyrim's, SKSE's and TamrielMerge's own script sources (TMMwScript, TMMwAI…),
/// and attached to the records that carried them, with their properties filled with the converted forms.
/// </summary>
public sealed partial class MwPluginBuilder
{
    /// <summary>
    /// Compiles every .psc in sourceDir into outDir, importing TamrielMerge's shipped scripts (the Papyrus folder beside
    /// the program), SKSE's sources (Data\Scripts\Source) and Skyrim's (Data\Source\Scripts). Returns the scripts that
    /// compiled (null without the compiler) and the compiler's output. The compiler works in batches and can drop a whole
    /// batch when memory runs short: those are compiled again one at a time (real errors fail again and stay in the log).
    /// </summary>
    public static (HashSet<string>? Compiled, string Log) CompilePapyrus(string skyrimData, string sourceDir, string outDir)
    {
        string game = Path.GetDirectoryName(Path.GetFullPath(skyrimData.TrimEnd('\\', '/')))!;
        string compiler = Path.Combine(game, "Papyrus Compiler", "PapyrusCompiler.exe");
        string vanilla = Path.Combine(skyrimData, "Source", "Scripts"), skse = Path.Combine(skyrimData, "Scripts", "Source");
        string shipped = Path.Combine(AppContext.BaseDirectory, "Papyrus");
        if (!File.Exists(compiler) || !File.Exists(Path.Combine(vanilla, "ObjectReference.psc")))
            return (null, "no Papyrus compiler or Skyrim script sources (install the Creation Kit)");
        Directory.CreateDirectory(outDir);
        string imports = $"-i={Path.GetFullPath(sourceDir)};{shipped};{skse};{vanilla}";
        var psi = new ProcessStartInfo(compiler)
        {
            ArgumentList = { Path.GetFullPath(sourceDir), "-all", "-q", "-f=TESV_Papyrus_Flags.flg", imports, $"-o={Path.GetFullPath(outDir)}" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        string log = Run(psi);
        bool Fresh(string n) => File.Exists(Path.Combine(outDir, n + ".pex")) && File.GetLastWriteTimeUtc(Path.Combine(outDir, n + ".pex")) >= File.GetLastWriteTimeUtc(Path.Combine(sourceDir, n + ".psc"));
        var names = Directory.GetFiles(sourceDir, "*.psc").Select(f => Path.GetFileNameWithoutExtension(f)!).ToList();
        var failedInLog = log.Split('\n').Where(l => l.Contains("): ")).Select(l => Path.GetFileNameWithoutExtension(l[..l.IndexOf('(')].Trim())).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var n in names.Where(n => !Fresh(n) && !failedInLog.Contains(n)))
        {
            var one = new ProcessStartInfo(compiler)
            {
                ArgumentList = { n + ".psc", "-q", "-f=TESV_Papyrus_Flags.flg", imports, $"-o={Path.GetFullPath(outDir)}" },
                WorkingDirectory = Path.GetFullPath(sourceDir),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            log += Run(one);
        }
        return (names.Where(Fresh).ToHashSet(StringComparer.OrdinalIgnoreCase), log);

        static string Run(ProcessStartInfo info)
        {
            using var p = Process.Start(info)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd() + err.Result;
            p.WaitForExit();
            return output;
        }
    }
}
