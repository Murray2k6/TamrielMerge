using System.Diagnostics;

namespace TamrielMerge.Util;

/// <summary>
/// The machine's graphics cards (user, 2026-10-27: "the tool also needs multi gpu support"), from nvidia-smi: each card's
/// index, name and memory. The GPU work (the voice generator's workers, the texture and video upscalers, the sprite
/// model maker) is shared across them: each process is given one card (<see cref="Env"/>: CUDA_VISIBLE_DEVICES names it,
/// so "cuda" in the script is that card, and TM_GPU, which the scripts' temperature checks read) and a share of the
/// work. CUDA_VISIBLE_DEVICES already set for this process (the app's GPU switch off: "") is honoured: those cards only.
/// </summary>
public static class Gpus
{
    public sealed record Gpu(int Index, string Name, double TotalMb, double FreeMb);

    static List<Gpu>? cached;

    /// <summary>The cards nvidia-smi lists (none without it), limited to CUDA_VISIBLE_DEVICES when that is set.</summary>
    public static IReadOnlyList<Gpu> List(bool fresh = false)
    {
        if (cached is not null && !fresh) return cached;
        var list = new List<Gpu>();
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=index,name,memory.total,memory.used --format=csv,noheader,nounits")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string all = p.StandardOutput.ReadToEnd();
            p.WaitForExit(10_000);
            foreach (var line in all.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(',');
                if (parts.Length < 4 || !int.TryParse(parts[0].Trim(), out int index)) continue;
                double total = double.Parse(parts[2].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                double used = double.Parse(parts[3].Trim(), System.Globalization.CultureInfo.InvariantCulture);
                list.Add(new Gpu(index, parts[1].Trim(), total, total - used));
            }
        }
        catch (Exception) { }
        if (Environment.GetEnvironmentVariable("CUDA_VISIBLE_DEVICES") is { } visible)
        {
            var allowed = visible.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(v => int.TryParse(v, out int i) ? i : -1).ToHashSet();
            list = list.Where(g => allowed.Contains(g.Index)).ToList();
        }
        return cached = list;
    }

    /// <summary>The environment giving a process one card: CUDA's view of the machine is that card alone (its "cuda" or
    /// "cuda:0"), and TM_GPU names it for nvidia-smi (the scripts' temperature checks).</summary>
    public static Dictionary<string, string> Env(Gpu gpu) => new()
    {
        ["CUDA_VISIBLE_DEVICES"] = gpu.Index.ToString(),
        ["TM_GPU"] = gpu.Index.ToString(),
    };

    public static string Describe() => List().Count == 0 ? "no NVIDIA graphics card found"
        : string.Join("; ", List().Select(g => $"GPU {g.Index} {g.Name} ({g.TotalMb / 1024:F0} GB, {g.FreeMb / 1024:F1} free)"));

    /// <summary>
    /// Runs <paramref name="script"/> once per card, each with its card's environment and <c>--shard i/n</c> (the script
    /// takes every n-th item of its work from the i-th), together, and waits; one card (or none: the script on the CPU as
    /// <paramref name="args"/> ask) runs it once without a shard. Returns the first non-zero exit code, else 0.
    /// </summary>
    public static int RunSharded(string python, IEnumerable<string> args, Action<string>? log = null, IDictionary<string, string>? env = null)
    {
        var gpus = List();
        if (gpus.Count <= 1) return Setup.ToolScripts.Run(python, args, gpus.Count == 1 ? Merge(env, Env(gpus[0])) : env);
        log?.Invoke($"on {gpus.Count} graphics cards: {Describe()}");
        var procs = new List<Process>();
        for (int i = 0; i < gpus.Count; i++)
        {
            var psi = new ProcessStartInfo(python) { UseShellExecute = false };
            foreach (var a in args.Concat(["--shard", $"{i}/{gpus.Count}"])) psi.ArgumentList.Add(a);
            psi.Environment["TAMRIELMERGE_TOOLS"] = Setup.Dependencies.Root;
            psi.Environment["HF_HOME"] = Setup.Dependencies.ModelCache;
            foreach (var (k, v) in Merge(env, Env(gpus[i]))) psi.Environment[k] = v;
            procs.Add(Process.Start(psi)!);
        }
        int code = 0;
        foreach (var p in procs) { p.WaitForExit(); if (p.ExitCode != 0 && code == 0) code = p.ExitCode; p.Dispose(); }
        return code;
    }

    static Dictionary<string, string> Merge(IDictionary<string, string>? a, IDictionary<string, string> b)
    {
        var d = a is null ? new Dictionary<string, string>() : new Dictionary<string, string>(a);
        foreach (var (k, v) in b) d[k] = v;
        return d;
    }
}
