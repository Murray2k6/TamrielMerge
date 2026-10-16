using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Speaks the converted dialogue that has no recordings (Morrowind's) in Skyrim's own voices and packs it as voice files
/// (user, 2026-10-09: "the tool needs to convert like this"): the lines planned from the plugin (VoicePlanner), each
/// voice type's reference recordings extracted where missing, then several tools/voice/generate.py workers sharing the
/// lines (--claim: a lock file per line), sized to the machine — as many on the graphics card as its memory holds (each
/// keeps about one CPU core busy feeding it: speech is made a token at a time), and one on the rest of the CPU — then
/// the .wavs packed as .fuz with lip sync and, if asked, installed. Everything resumes: spoken lines are files, packed
/// voice files are kept. Workers already running on the same lines (started earlier) are watched, not doubled.
/// The workers' process ids go to voiceDir\workers.json. Pausing (user, 2026-10-09: "when paused uses no load"): while
/// voiceDir\pause exists (the app's Pause makes it) the workers are stopped, not held, so their memory (about 7 GB of RAM
/// each, and the graphics card's) is freed too; lines being spoken are let go and spoken again later. When it goes, new
/// workers start, sized to what is free then.
/// </summary>
public static class VoiceGenerator
{
    // A graphics-card worker speaks its lines in batches of GpuBatch (lines of one voice and length together: 1.15 to
    // 1.46 lines a second on an RTX 5060 against 0.24 one at a time; user, 2026-10-09: "it shouldnt take days its too
    // slow"), using GpuWorkerMb of video memory (measured); batches of 32 were no faster and filled the card.
    const int GpuBatch = 16;
    const double GpuWorkerMb = 4100;
    // Physical RAM per worker (user, 2026-10-09: "it shouldnt take days its too slow"): 2.8 GB once speaking, 4.4 GB at
    // its peak while loading (workers start apart). Its 7.4 GB of committed memory was counted as RAM before, which held
    // the machine to one worker with the graphics card three-quarters idle; Windows' own page file grows to hold it.
    const double WorkerGb = 3.5;

    /// <param name="planOnly">Only the job list, planned (or kept) and counted: no workers, nothing spoken or packed.</param>
    public static int Run(string skyrimData, string plugin, string voiceDir, string outDir, bool install, bool gpu, bool planOnly = false)
    {
        if (!File.Exists(plugin)) { Console.WriteLine($"Voices: {plugin} isn't there (convert that game first)"); return 1; }
        // The app shows this run's progress from voiceDir\progress.txt too (a run started elsewhere, or before the app);
        // a plan alone isn't a run.
        using var status = planOnly ? null : Progress.ToStatusFile(Path.Combine(voiceDir, "progress.txt"));
        string py = planOnly ? "" : Setup.Dependencies.Python ?? throw new InvalidOperationException("the Python environment is missing: run setup");
        string script = Setup.ToolScripts.Path("voice", "generate.py");
        string name = Path.GetFileNameWithoutExtension(plugin);
        string jobsFile = Path.Combine(voiceDir, name.Equals("Morrowind", StringComparison.OrdinalIgnoreCase) ? "mw-jobs.json" : name.ToLowerInvariant() + "-jobs.json");
        string refs = Path.Combine(voiceDir, "refs"), wavs = Path.Combine(voiceDir, "wavs");
        Directory.CreateDirectory(wavs);

        // The lines, planned again only when the plugin is newer than the plan.
        if (!File.Exists(jobsFile) || File.GetLastWriteTimeUtc(plugin) > File.GetLastWriteTimeUtc(jobsFile))
        {
            Progress.Report(0, $"Voices: planning {name}'s lines");
            using var m = SkyrimMod.CreateFromBinaryOverlay(plugin, SkyrimRelease.SkyrimSE);
            var planned = VoicePlanner.Plan(m, skyrimData, out _);
            // Oblivion's: only the lines Oblivion never recorded (user, 2026-10-16: "rest of oblivions missing voices
            // need in"); the recorded ones are its own voice acting, packed by the conversion (ObVoices).
            if (name.Equals("Oblivion", StringComparison.OrdinalIgnoreCase))
            {
                string map = Path.Combine(Path.GetDirectoryName(plugin)!, "Oblivion.voicemap.json");
                string? obData = GameInstalls.DataDir(Game.Oblivion);
                if (obData is null || !File.Exists(map)) { Console.WriteLine($"Voices: Oblivion's recordings or {map} not found: convert Oblivion again"); return 1; }
                var obRecorded = Oblivion.ObVoices.Sources(new Oblivion.ObData(obData), planned, map);
                planned = planned.Where(j => !obRecorded.ContainsKey(j.File)).ToList();
                Console.WriteLine($"Voices: Oblivion: {obRecorded.Count:N0} voice files from its own recordings, {planned.Count:N0} to speak");
            }
            VoicePlanner.WriteJobs(planned, jobsFile);
        }
        var jobs = JsonSerializer.Deserialize<List<VoicePlanner.Job>>(File.ReadAllText(jobsFile))!;
        if (planOnly)
        {
            var lines = jobs.Select(j => VoiceFiles.WavKey(j.Speech, j.Text)).ToHashSet();
            int spoken = lines.Count(k => File.Exists(Path.Combine(wavs, k + ".wav")));
            Console.WriteLine($"Voices: {name}: {jobs.Count:N0} voice files, {lines.Count:N0} distinct lines, {spoken:N0} spoken already ({jobsFile})");
            return 0;
        }
        // Lines with a recording of their own (Morrowind's barks) are that recording, decoded into the spoken lines.
        int recorded = Recordings(jobs, wavs);
        if (recorded > 0) Console.WriteLine($"Voices: {recorded:N0} of the game's own recordings ready");
        var missingRefs = jobs.Where(j => j.Recording is null).Select(j => j.Speech.Split('+')[0]).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(v => !Directory.Exists(Path.Combine(refs, v)) || !Directory.EnumerateFiles(Path.Combine(refs, v), "*.wav").Any()).ToList();
        if (missingRefs.Count > 0)
        {
            Progress.Report(0, $"Voices: reference recordings for {missingRefs.Count} voice types");
            VoiceFiles.ExtractReferences(skyrimData, missingRefs, refs);
        }
        var keys = jobs.Select(j => VoiceFiles.WavKey(j.Speech, j.Text)).ToHashSet();
        int Spoken() => Directory.EnumerateFiles(wavs, "*.wav").Select(f => Path.GetFileNameWithoutExtension(f)).Count(keys.Contains);
        int total = keys.Count, done = Spoken();
        // The whole job's progress, speaking and packing weighted by their time: a line takes about 0.47 s to speak (one
        // batching worker), a voice file about 0.09 s to pack, so a voice file counts a fifth of a line.
        // Generated voice files are packed beside the output (out\voices), then archived as the voices mod
        // (VoiceArchives); lines with a recording of their own go into the output with the rest of the game.
        string packedDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outDir).TrimEnd('\\', '/'))!, "voices");
        string DirOf(VoicePlanner.Job j) => j.Recording is null ? packedDir : outDir;
        int files = jobs.Select(j => j.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int filesDone = jobs.DistinctBy(j => j.File, StringComparer.OrdinalIgnoreCase).Count(j => VoiceFiles.Current(Path.Combine(DirOf(j), j.File)));
        double speakShare = total / (total + files * 0.2);
        double Overall(double spokenFraction, double packedFraction) => speakShare * spokenFraction + (1 - speakShare) * packedFraction;
        Console.WriteLine($"Voices: {name}: {total:N0} distinct lines, {done:N0} spoken already");

        if (done < total)
        {
            string pauseFile = Path.Combine(voiceDir, "pause"), workersFile = Path.Combine(voiceDir, "workers.json");
            File.Delete(pauseFile);   // left by an app that closed while paused
            var workers = new List<Process>();
            bool StartWorkers()
            {
                var (gpuWorkers, cpuThreads) = Size(gpu);
                Console.WriteLine($"Voices: {gpuWorkers} worker(s) on the graphics card, {(cpuThreads > 0 ? $"one on the CPU ({cpuThreads} threads)" : "none on the CPU")}");
                // Started 20 seconds apart, so their loading peaks don't come together.
                for (int i = 0; i < gpuWorkers; i++)
                {
                    if (workers.Count > 0) Thread.Sleep(20_000);
                    workers.Add(Start(py, script, voiceDir, jobsFile, $"g{i + 1}", ["--fp16", "--batch", GpuBatch.ToString()], gpu: true));
                }
                if (cpuThreads > 0)
                {
                    if (workers.Count > 0) Thread.Sleep(20_000);
                    workers.Add(Start(py, script, voiceDir, jobsFile, "cpu", ["--device", "cpu", "--threads", cpuThreads.ToString(), "--reverse"], gpu: false));
                }
                if (workers.Count == 0) return false;
                File.WriteAllText(workersFile, JsonSerializer.Serialize(workers.Select(w => w.Id).ToList()));
                return true;
            }
            var watched = RunningWorkers(voiceDir);
            if (watched.Count > 0) Console.WriteLine($"Voices: {watched.Count} workers are already speaking these lines; watching them");
            else if (!StartWorkers()) { Console.WriteLine("Voices: not enough free memory for a worker"); return 1; }
            // Progress until the workers are done (or, when watching others, until the lines stop coming).
            var history = new Queue<(DateTime At, int Done)>();
            DateTime lastNew = DateTime.UtcNow, lastCheck = DateTime.MinValue;
            int lastDone = done;
            bool stopped = false;
            while (true)
            {
                Thread.Sleep(1000);
                if (File.Exists(pauseFile))
                {
                    if (!stopped)
                    {
                        // Paused: every worker stopped (ours and watched ones), their claims let go.
                        foreach (var w in workers) try { w.Kill(entireProcessTree: true); w.WaitForExit(10_000); } catch { }
                        foreach (int id in watched.Where(IsAlive)) try { using var p = Process.GetProcessById(id); p.Kill(entireProcessTree: true); p.WaitForExit(10_000); } catch { }
                        workers.Clear(); watched.Clear();
                        File.Delete(workersFile);
                        foreach (var f in Directory.EnumerateFiles(wavs, "*.lock").Concat(Directory.EnumerateFiles(wavs, "*.tmp.wav"))) try { File.Delete(f); } catch { }
                        stopped = true;
                        Console.WriteLine("Voices: paused; the workers are stopped and their memory freed");
                        Progress.Report(Overall(Spoken() / (double)total, filesDone / (double)files), "Voices: paused");
                    }
                    continue;
                }
                if (stopped)
                {
                    stopped = false;
                    history.Clear();
                    lastNew = DateTime.UtcNow;
                    if (!StartWorkers()) { Console.WriteLine("Voices: not enough free memory for a worker"); break; }
                }
                if (DateTime.UtcNow - lastCheck < TimeSpan.FromSeconds(15)) continue;
                lastCheck = DateTime.UtcNow;
                done = Spoken();
                var now = DateTime.UtcNow;
                if (done > lastDone) { lastNew = now; lastDone = done; }
                history.Enqueue((now, done));
                while (history.Count > 1 && now - history.Peek().At > TimeSpan.FromMinutes(10)) history.Dequeue();
                var (t0, d0) = history.Peek();
                double rate = (now - t0).TotalSeconds > 30 ? (done - d0) / (now - t0).TotalSeconds : 0;
                string pace = rate > 0 ? $"{rate:F2} a second, about {Eta((total - done) / rate)} left" : "working out the time left";
                Progress.Report(Overall(done / (double)total, filesDone / (double)files), $"Voices: {done:N0} of {total:N0} lines spoken ({pace})");
                if (done >= total) break;
                bool alive = workers.Count > 0 ? workers.Any(w => !w.HasExited) : watched.Any(IsAlive) || now - lastNew < TimeSpan.FromMinutes(10);
                if (!alive) break;
            }
            foreach (var w in workers) if (!w.HasExited) w.WaitForExit(60_000);
            if (done < total)
                Console.WriteLine($"Voices: {total - done:N0} lines not spoken yet (the workers stopped); run the voice generator again to finish them");
        }

        // The spoken lines as voice files (with lip sync), and installed.
        Progress.Report(Overall(1, filesDone / (double)files), "Voices: packing voice files with lip sync");
        int recordedFiles = jobs.Where(j => j.Recording is not null).Select(j => j.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var (packedR, existingR, missingR) = VoiceFiles.PackAll(skyrimData, jobs.Where(j => j.Recording is not null).ToList(), wavs, outDir,
            (ready, all) => Progress.Report(Overall(1, ready / (double)files), $"Voices: {ready:N0} of {all:N0} of the game's own recordings packed"));
        var (packedG, existingG, missingG) = VoiceFiles.PackAll(skyrimData, jobs.Where(j => j.Recording is null).ToList(), wavs, packedDir,
            (ready, all) => Progress.Report(Overall(1, (recordedFiles + ready) / (double)files), $"Voices: {ready:N0} of {all:N0} voice files packed with lip sync"));
        int packed = packedR + packedG, existing = existingR + existingG, missing = missingR + missingG;
        // The generated voices as their own mod: archives and light plugins, which turn them off when disabled.
        // Voice files no line names any more (a rebuilt plugin's responses moved to new FormIDs): left out of the mod.
        // Only this plugin's (sound\voice\<plugin>): the other games' generated voices are in packedDir too.
        string ownVoices = Path.Combine(packedDir, "sound", "voice", Path.GetFileName(plugin));
        if (Directory.Exists(ownVoices))
        {
            var wanted = jobs.Where(j => j.Recording is null).Select(j => Path.GetFullPath(Path.Combine(packedDir, j.File))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            int stale = 0;
            foreach (var f in Directory.EnumerateFiles(ownVoices, "*.fuz", SearchOption.AllDirectories).ToList())
                if (!wanted.Contains(Path.GetFullPath(f))) { File.Delete(f); stale++; }
            if (stale > 0) Console.WriteLine($"Voices: {stale:N0} voice files no line names any more, left out");
        }
        Progress.Report(0.99, "Voices: the voices mod's archives");
        var voicePlugins = VoiceArchives.Build(packedDir, outDir);
        Console.WriteLine($"Voices: {packed:N0} voice files packed, {existing:N0} already there, {missing:N0} lines not spoken yet");
        if (install)
        {
            string from = Path.Combine(outDir, "sound", "voice", Path.GetFileName(plugin));
            if (Directory.Exists(from))
            {
                Progress.Report(0.99, "Voices: installing");
                foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                {
                    string target = Path.Combine(skyrimData, Path.GetRelativePath(outDir, file));
                    if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(file).Length) continue;
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
            }
            foreach (var f in Directory.EnumerateFiles(outDir, VoiceArchives.Name + "*"))
                File.Copy(f, Path.Combine(skyrimData, Path.GetFileName(f)), overwrite: true);
            Console.WriteLine($"Voices: installed into {skyrimData} (the voices mod: {string.Join(", ", voicePlugins)} - enable it to hear them, disable it for the games as they were)");
            foreach (var line in Setup.PluginList.Enable(voicePlugins)) Console.WriteLine($"Voices: {line}");
        }
        Progress.Report(1, missing == 0 ? "Voices: done" : $"Voices: {missing:N0} lines still to speak");
        return missing == 0 ? 0 : 3;
    }

    /// <summary>Each recording a line is spoken with (Morrowind's Sound\Vo mp3s) decoded to the generator's .wav
    /// (44.1 kHz mono 16-bit, named like a spoken line) where missing; the number now there.</summary>
    static int Recordings(List<VoicePlanner.Job> jobs, string wavs)
    {
        var wanted = jobs.Where(j => j.Recording is not null).GroupBy(j => VoiceFiles.WavKey(j.Speech, j.Text)).ToList();
        if (wanted.Count == 0) return 0;
        string? sound = GameInstalls.DataDir(Game.Morrowind) is { } mw ? Path.Combine(mw, "Sound") : null;
        if (sound is null) { Console.WriteLine("Voices: Morrowind isn't installed; its recorded barks are left out"); return 0; }
        int ready = 0, missing = 0;
        Parallel.ForEach(wanted, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount }, line =>
        {
            string wav = Path.Combine(wavs, line.Key + ".wav");
            if (File.Exists(wav)) { Interlocked.Increment(ref ready); return; }
            string src = Path.Combine(sound, line.First().Recording!);
            if (!File.Exists(src)) { Interlocked.Increment(ref missing); return; }
            var psi = new ProcessStartInfo(Setup.Dependencies.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "-y", "-v", "error", "-i", src, "-ac", "1", "-ar", "44100", "-sample_fmt", "s16", wav + ".tmp.wav" }) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            p.StandardError.ReadToEnd(); p.WaitForExit();
            if (p.ExitCode == 0 && File.Exists(wav + ".tmp.wav")) { File.Move(wav + ".tmp.wav", wav, true); Interlocked.Increment(ref ready); }
        });
        if (missing > 0) Console.WriteLine($"Voices: {missing:N0} recordings not found under {sound}");
        return ready;
    }

    static string Eta(double seconds) =>
        seconds > 86400 * 2 ? $"{seconds / 86400:F1} days" : seconds > 5400 ? $"{seconds / 3600:F0} hours" : $"{Math.Max(1, seconds / 60):F0} minutes";

    /// <summary>Workers on the graphics card (by its free memory, at most 3) and the CPU worker's threads (the cores the
    /// graphics workers leave, 0 when there are few), within the free RAM.</summary>
    static (int Gpu, int CpuThreads) Size(bool gpu)
    {
        int slots = (int)Math.Max(0, (FreeRamGb() - 4) / WorkerGb);
        int g = 0;
        if (gpu && FreeVideoMb() is { } freeMb) g = (int)Math.Clamp((freeMb - 600) / GpuWorkerMb, 0, 3);
        g = Math.Min(g, slots);
        int cores = Environment.ProcessorCount;
        // A CPU worker only without the graphics card: beside a batching GPU worker it starved the core feeding the card
        // (all 16 threads busy) and spoke a tenth as fast.
        int cpu = g == 0 && slots > 0 && cores >= 8 ? Math.Max(2, cores - 2) : 0;
        return (g, cpu);
    }

    static Process Start(string py, string script, string voiceDir, string jobsFile, string tag, string[] extra, bool gpu)
    {
        var psi = new ProcessStartInfo(py) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = voiceDir };
        foreach (var a in new[] { "-u", script, jobsFile, "--refs", "refs", "--wavs", "wavs", "--claim" }.Concat(extra)) psi.ArgumentList.Add(a);
        if (gpu) psi.Environment.Remove("CUDA_VISIBLE_DEVICES");
        else psi.Environment["CUDA_VISIBLE_DEVICES"] = "";
        psi.RedirectStandardOutput = psi.RedirectStandardError = true;
        var p = Process.Start(psi)!;
        string log = Path.Combine(voiceDir, $"generate.{tag}.log");
        // Shared: a worker started again after a pause appends to the log its stopped predecessor's writer still holds
        // (opening it alone crashed the generator on resume, 2026-10-10).
        var w = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (w) w.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null && !e.Data.Contains("Warning", StringComparison.OrdinalIgnoreCase)) lock (w) w.WriteLine(e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        return p;
    }

    /// <summary>Workers from an earlier run still alive (workers.json), or, without that, any whose lock files are fresh.</summary>
    static List<int> RunningWorkers(string voiceDir)
    {
        string file = Path.Combine(voiceDir, "workers.json");
        var ids = File.Exists(file) ? JsonSerializer.Deserialize<List<int>>(File.ReadAllText(file)) ?? [] : [];
        ids = ids.Where(IsAlive).ToList();
        if (ids.Count > 0) return ids;
        bool fresh = Directory.EnumerateFiles(Path.Combine(voiceDir, "wavs"), "*.lock").Any(f => DateTime.UtcNow - File.GetLastWriteTimeUtc(f) < TimeSpan.FromMinutes(3));
        return fresh ? [-1] : [];
    }

    static bool IsAlive(int id)
    {
        if (id < 0) return false;
        try { var p = Process.GetProcessById(id); return !p.HasExited && p.ProcessName.StartsWith("python", StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    static double? FreeVideoMb()
    {
        try
        {
            var psi = new ProcessStartInfo("nvidia-smi", "--query-gpu=memory.total,memory.used --format=csv,noheader,nounits")
            { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string line = p.StandardOutput.ReadLine() ?? "";
            p.WaitForExit(10_000);
            var parts = line.Split(',');
            return double.Parse(parts[0].Trim()) - double.Parse(parts[1].Trim());
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhys, AvailPhys, TotalPageFile, AvailPageFile, TotalVirtual, AvailVirtual, AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);

    static double FreeRamGb()
    {
        var s = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        return GlobalMemoryStatusEx(ref s) ? s.AvailPhys / 1073741824.0 : 8;
    }
}
