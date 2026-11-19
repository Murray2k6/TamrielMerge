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
    // slow"); batches of 32 were no faster and filled the card. Its GPT in half precision (generate.py --half) takes
    // GpuWorkerMb of video memory at its peak, half the 4,100 MB of before, so several share the card (user, 2026-10-24:
    // "voice conversion needs to be multithreaded and fast"). Measured 2026-10-24, 480 lines of one voice: one worker
    // 1.11 lines a second (as before), two 1.54, three 1.81 (7.6 of the card's 8 GB), three and a CPU worker 1.98. Past
    // the card's memory (user: "it needs to use both gpu and ram") the driver pages the graphics memory into RAM: four
    // workers spoke 0.15 lines a second and five failed (illegal memory access), so workers are kept to what the card
    // holds and the rest of the machine goes to the CPU worker.
    const int GpuBatch = 16;
    const double GpuWorkerMb = 2150;
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
        // (Also once after the voice files' naming was corrected, VoicePlanner.Prefix: a plan from before has the old names.)
        string namingMark = jobsFile + ".names2";
        if (!File.Exists(jobsFile) || !File.Exists(namingMark) || File.GetLastWriteTimeUtc(plugin) > File.GetLastWriteTimeUtc(jobsFile))
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
            File.WriteAllText(namingMark, "voice files named by VoicePlanner.Prefix");
        }
        var jobs = JsonSerializer.Deserialize<List<VoicePlanner.Job>>(File.ReadAllText(jobsFile))!;
        // Generated voice files are packed beside the output (out\voices), then archived as the voices mod
        // (VoiceArchives); lines with a recording of their own go into the output with the rest of the game.
        string packedDir = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(outDir).TrimEnd('\\', '/'))!, "voices");
        string DirOf(VoicePlanner.Job j) => j.Recording is null ? packedDir : outDir;
        // Voice files made under the old naming keep their lines: moved to the names the game looks for.
        int renamed = VoicePlanner.Renamed(jobs.Where(j => j.Recording is null), packedDir) + VoicePlanner.Renamed(jobs.Where(j => j.Recording is not null), outDir);
        if (renamed > 0) Console.WriteLine($"Voices: {renamed:N0} voice files renamed to the names Skyrim looks for");
        // A line every voice file of which is packed already counts as spoken, with or without its .wav: the spoken lines
        // were lost once (2026-10-27, the voice folder deleted) while their packed files were kept, and speaking them all
        // again would have been days of work for files already there. The workers get only the lines still to speak.
        var packedLines = jobs.GroupBy(j => VoiceFiles.WavKey(j.Speech, j.Text))
            .Where(g => g.All(j => VoiceFiles.Current(Path.Combine(DirOf(j), j.File)))).Select(g => g.Key).ToHashSet();
        if (planOnly)
        {
            var lines = jobs.Select(j => VoiceFiles.WavKey(j.Speech, j.Text)).ToHashSet();
            int spoken = lines.Count(k => File.Exists(Path.Combine(wavs, k + ".wav")));
            Console.WriteLine($"Voices: {name}: {jobs.Count:N0} voice files, {lines.Count:N0} distinct lines, {spoken:N0} spoken (as .wav), {packedLines.Count:N0} packed already ({jobsFile})");
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
        int Spoken() => packedLines.Count + Directory.EnumerateFiles(wavs, "*.wav").Select(f => Path.GetFileNameWithoutExtension(f)).Count(k => keys.Contains(k) && !packedLines.Contains(k));
        int total = keys.Count, done = Spoken();
        if (packedLines.Count > 0) Console.WriteLine($"Voices: {packedLines.Count:N0} lines packed already");
        string speakJobsFile = jobsFile + ".speak.json";
        VoicePlanner.WriteJobs(jobs.Where(j => !packedLines.Contains(VoiceFiles.WavKey(j.Speech, j.Text))).ToList(), speakJobsFile);
        // The whole job's progress, speaking and packing weighted by their time: a line takes about 0.47 s to speak (one
        // batching worker), a voice file about 0.09 s to pack, so a voice file counts a fifth of a line.
        int files = jobs.Select(j => j.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int filesDone = jobs.DistinctBy(j => j.File, StringComparer.OrdinalIgnoreCase).Count(j => VoiceFiles.Current(Path.Combine(DirOf(j), j.File)));
        double speakShare = total / (total + files * 0.2);
        double Overall(double spokenFraction, double packedFraction) => speakShare * spokenFraction + (1 - speakShare) * packedFraction;
        Console.WriteLine($"Voices: {name}: {total:N0} distinct lines, {done:N0} spoken already");
        // xVASynth first (user, 2026-11-02: "its taking way to long even days"): the voice types it has a model for, in
        // one pass a line and batches of lines (tools\voice\xva.py); XTTS below speaks what it leaves (voice types with no
        // model), the files being the same either way.
        if (done < total) done = SpeakWithXva(py, voiceDir, speakJobsFile, wavs, gpu, Spoken, total, d => Progress.Report(Overall(d / (double)total, filesDone / (double)files), $"Voices: xVASynth, {d:N0} of {total:N0} lines spoken"));

        if (done < total)
        {
            string pauseFile = Path.Combine(voiceDir, "pause"), workersFile = Path.Combine(voiceDir, "workers.json");
            File.Delete(pauseFile);   // left by an app that closed while paused
            var workers = new List<Process>();
            // The graphics workers by their log's name. One that dies at once must be heard: 2026-11-01 an older generate.py
            // refused their options, all three stopped in a second, and the CPU worker alone went on at 0.1 lines a second
            // (442 hours by its own log) with nothing to say why.
            var gpuWorkers = new Dictionary<Process, string>();
            bool StartWorkers()
            {
                var (cards, cpuThreads) = Size(gpu);
                Console.WriteLine($"Voices: {(cards.Count == 0 ? "no workers on a graphics card" : string.Join(", ", cards.Select(c => $"{c.Workers} worker(s) on GPU {c.Card.Index} ({c.Card.Name})")))}, "
                    + (cpuThreads > 0 ? $"one on the CPU ({cpuThreads} threads)" : "none on the CPU"));
                // Started 20 seconds apart, so their loading peaks don't come together; each card's workers on that card.
                foreach (var (card, n) in cards)
                    for (int i = 0; i < n; i++)
                    {
                        if (workers.Count > 0) Thread.Sleep(20_000);
                        string tag = $"g{card.Index}-{i + 1}";
                        var worker = Start(py, script, voiceDir, speakJobsFile, tag, ["--half", "--batch", GpuBatch.ToString(), "--savers", "2", "--share"], card);
                        workers.Add(worker);
                        gpuWorkers[worker] = tag;
                    }
                if (cpuThreads > 0)
                {
                    if (workers.Count > 0) Thread.Sleep(20_000);
                    workers.Add(Start(py, script, voiceDir, speakJobsFile, "cpu", ["--device", "cpu", "--threads", cpuThreads.ToString(), "--reverse", "--share"], null));
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
                        workers.Clear(); watched.Clear(); gpuWorkers.Clear();
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
                // Every graphics worker failed: the CPU alone would take weeks, so the run stops and says why.
                if (gpuWorkers.Count > 0 && gpuWorkers.Keys.All(w => w.HasExited && w.ExitCode != 0))
                {
                    foreach (var (_, tag) in gpuWorkers)
                    {
                        string log = Path.Combine(voiceDir, $"generate.{tag}.log");
                        string last = File.Exists(log) ? File.ReadLines(log).LastOrDefault(l => l.Trim().Length > 0) ?? "" : "";
                        Console.WriteLine($"Voices: graphics worker {tag} failed: {last}");
                    }
                    Console.WriteLine("Voices: stopped, every graphics worker failed and the CPU alone would take weeks; what is spoken is kept");
                    foreach (var w in workers) try { if (!w.HasExited) { w.Kill(entireProcessTree: true); w.WaitForExit(10_000); } } catch { }
                    File.Delete(workersFile);
                    return 1;
                }
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

    /// <summary>Workers on each graphics card (as many as its free memory holds, at most 6 a card; user, 2026-10-27:
    /// "the tool also needs multi gpu support") and the CPU worker's threads (the threads the graphics workers leave:
    /// two each, for feeding the card and saving, and two for the rest of the machine; 0 when fewer than 4 are left),
    /// within the free RAM.</summary>
    static (List<(Util.Gpus.Gpu Card, int Workers)> Cards, int CpuThreads) Size(bool gpu)
    {
        int slots = (int)Math.Max(0, (FreeRamGb() - 4) / WorkerGb);
        var cards = new List<(Util.Gpus.Gpu, int)>();
        int g = 0;
        if (gpu)
            foreach (var card in Util.Gpus.List(fresh: true))
            {
                int n = Math.Min((int)Math.Clamp((card.FreeMb - 250) / GpuWorkerMb, 0, 6), slots - g);
                if (n > 0) { cards.Add((card, n)); g += n; }
            }
        int cores = Environment.ProcessorCount;
        // A CPU worker beside the graphics workers on the threads they leave (2026-10-24: three graphics workers and one
        // on 8 of 16 threads, 1.98 lines a second against 1.81 without it). On 2026-10-09 it took every thread but two
        // and starved the one worker feeding the card.
        int left = cores - 2 * g - 2;
        int cpu = slots > g && left >= 4 ? left : 0;
        return (cards, cpu);
    }

    /// <summary>
    /// The lines xVASynth has models for, spoken by tools\voice\xva.py (on the graphics card when there is one), while the
    /// app's Pause holds it (stopped, started again on resume: it skips what is spoken). Returns the lines spoken in all.
    /// Nothing happens without the engine or its models (setup's "xVASynth speech engine", the Skyrim voice models).
    /// </summary>
    static int SpeakWithXva(string py, string voiceDir, string jobsFile, string wavs, bool gpu, Func<int> spoken, int total, Action<int> report)
    {
        int models = Setup.Dependencies.XvaModels;
        if (!Setup.Dependencies.XvaReady || models == 0)
        {
            Console.WriteLine(models == 0 ? $"Voices: no xVASynth voice models in {Setup.Dependencies.XvaModelDir} (Skyrim SE mod 44184): XTTS speaks every line"
                : "Voices: the xVASynth speech engine isn't set up (run setup): XTTS speaks every line");
            return spoken();
        }
        bool card = gpu && Util.Gpus.List(fresh: true).Count > 0;
        string pauseFile = Path.Combine(voiceDir, "pause");
        string script = Setup.ToolScripts.Path("voice", "xva.py");
        Console.WriteLine($"Voices: xVASynth speaks the voice types its {models} models cover ({(card ? "graphics card" : "CPU")})");
        while (true)
        {
            var psi = new ProcessStartInfo(py) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = voiceDir };
            foreach (var a in new[] { "-u", script, jobsFile, "--models", Setup.Dependencies.XvaModelDir, "--wavs", wavs, "--claim",
                         "--device", card ? "cuda" : "cpu", "--batch", card ? "32" : "4" }.Concat(card ? [] : new[] { "--threads", Math.Max(1, Environment.ProcessorCount - 2).ToString() }))
                psi.ArgumentList.Add(a);
            if (!card) psi.Environment["CUDA_VISIBLE_DEVICES"] = "-1";   // "" unsets it on Windows: the card stays seen
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            using var p = Process.Start(psi)!;
            string log = Path.Combine(voiceDir, "generate.xva.log");
            var w = new StreamWriter(new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete)) { AutoFlush = true };
            p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (w) w.WriteLine(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data is not null && !e.Data.Contains("Warning", StringComparison.OrdinalIgnoreCase)) lock (w) w.WriteLine(e.Data); };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            bool paused = false;
            while (!p.WaitForExit(5_000))
            {
                report(spoken());
                if (File.Exists(pauseFile)) { paused = true; try { p.Kill(entireProcessTree: true); p.WaitForExit(10_000); } catch { } break; }
            }
            lock (w) w.Dispose();
            if (paused)
            {
                Console.WriteLine("Voices: paused; xVASynth stopped and its memory freed");
                while (File.Exists(pauseFile)) Thread.Sleep(2_000);
                Console.WriteLine("Voices: resumed");
                continue;
            }
            int done = spoken();
            // What it had claimed and not finished when it stopped: its claims would pass for another run's workers at
            // work (RunningWorkers looks for claims under three minutes old), and the XTTS workers were then never
            // started: "1 workers are already speaking these lines; watching them", ten minutes of nothing, twice in
            // the conversion of 2026-11-09, and its 113,304 and 10,914 lines left unspoken. Left alone when another
            // run's workers really are at work.
            string workersFile = Path.Combine(voiceDir, "workers.json");
            var others = File.Exists(workersFile) ? (JsonSerializer.Deserialize<List<int>>(File.ReadAllText(workersFile)) ?? []).Where(IsAlive).ToList() : [];
            if (others.Count == 0)
                foreach (var f in Directory.EnumerateFiles(wavs, "*.lock").Concat(Directory.EnumerateFiles(wavs, "*.xva.tmp.wav"))) try { File.Delete(f); } catch (IOException) { }
            if (p.ExitCode != 0)
            {
                string last = File.Exists(log) ? File.ReadLines(log).LastOrDefault(l => l.Trim().Length > 0) ?? "" : "";
                Console.WriteLine($"Voices: xVASynth stopped ({last}); XTTS speaks the rest");
            }
            else Console.WriteLine($"Voices: xVASynth done; {total - done:N0} lines left for XTTS");
            return done;
        }
    }

    /// <param name="card">The graphics card the worker speaks on (its own, of several), or null for the CPU.</param>
    static Process Start(string py, string script, string voiceDir, string jobsFile, string tag, string[] extra, Util.Gpus.Gpu? card)
    {
        var psi = new ProcessStartInfo(py) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = voiceDir };
        foreach (var a in new[] { "-u", script, jobsFile, "--refs", "refs", "--wavs", "wavs", "--claim" }.Concat(extra)) psi.ArgumentList.Add(a);
        if (card is { } c) foreach (var (k, v) in Util.Gpus.Env(c)) psi.Environment[k] = v;
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
