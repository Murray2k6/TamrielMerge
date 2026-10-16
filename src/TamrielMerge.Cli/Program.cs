using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim;

namespace TamrielMerge;

static class Program
{
    /// <summary>The main menu's intro video frames (tools/menu/intro.py upscale and pack).</summary>
    const string MenuVideoSize = "2560x1440";

    static int Main(string[] args)
    {
        var opts = ParseOptions(args.Skip(1));
        // Only games installed through a store they were bought from (Steam, GOG) are converted; a game that isn't
        // installed is simply left out (see GameInstalls).
        string arena2 = GameInstalls.DataDir(Game.Daggerfall) ?? "";
        string skyrimData = GameInstalls.SkyrimData() ?? "";

        switch (args.FirstOrDefault())
        {
            case "convert":
            {
                // Converts every game installed through Steam or GOG into one folder (--out, default out/convert): each
                // game's plugin and assets, then the start plugin (story order, time travel), the scripts and the era
                // plugin. First the tools are set up (setup) and the Creation Kit is checked for — when it's missing the user
                // is asked to install it (Steam), skip voices, or quit (--no-prompt: warn and skip). Redguard's and Oblivion's
                // own voice acting is packed with lip sync. Games that aren't installed, or are listed in --skip
                // (comma-separated: game names, Setup, Models, Voices, Videos, Menu), are left out; the
                // rest work without them. --install copies the result into Skyrim's Data folder. When --out is on a hard
                // disk the build is made on a solid-state drive and moved there (below); --no-staging builds in place.
                // --resume carries on a stopped run (user, 2026-10-13: "use the finish latest and start from there with
                // change"): Daggerfall.esm with its land, and every finished town and dungeon part already in --out, are
                // kept as they are and only the rest is built.
                if (skyrimData.Length == 0) { Console.Error.WriteLine("Skyrim Special Edition isn't installed through Steam or GOG."); return 1; }
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/convert"));
                var skip = opts.GetValueOrDefault("skip", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var found = GameInstalls.Detect();
                Directory.CreateDirectory(outDir);
                Console.WriteLine($"Skyrim SE: {skyrimData}");
                // Progress for the app's bar: each step a share by its usual length (voice generation, days, the most).
                bool Has(Game g) => found.ContainsKey(g) && !skip.Contains(g.ToString());
                var plan = new List<(string Step, double Weight)> { ("Setup", 1) };
                if (Has(Game.Daggerfall) || Has(Game.Morrowind)) plan.Add(("Upscaling textures", opts.ContainsKey("gpu") ? 6 : 20));
                if (Has(Game.Daggerfall)) plan.AddRange([("Missing models", 4), ("Daggerfall", 6), ("Daggerfall's towns", 25), ("Daggerfall's dungeons", 12)]);
                if (Has(Game.Redguard)) plan.Add(("Redguard", 2));
                if (Has(Game.Morrowind)) plan.Add(("Morrowind", 5));
                if (Has(Game.Oblivion)) plan.Add(("Oblivion", 8));
                plan.AddRange([("Recorded voices", 3), ("Intro videos", 6), ("Main menu", opts.ContainsKey("gpu") ? 25 : 0), ("Creature models", 3), ("Distant objects and trees", 4), ("Creatures", 1),
                               ("Story and progression", 2), ("Installing", 2)]);
                if (Has(Game.Morrowind) && !skip.Contains("Voices") && !skip.Contains("VoiceGen")) plan.Add(("Voice generation", 300));
                // Built on a solid-state drive when --out is on a hard disk (user, 2026-10-13: "why is the cpu not being
                // used", "with a users dir drives must be looked at automatically"): the builds write hundreds of thousands
                // of small files, which a hard disk's seeking slows to a crawl while the processors wait. Util.Drives finds a
                // drive that doesn't seek with room (the user's temp folder first); everything is written there in full and
                // moved into --out after the creature models. Intro videos and recorded voices, which keep what an earlier
                // run made, and the steps after (which read the user's model choices and earlier plugins) use --out itself.
                string finalDir = outDir;
                string? staging = opts.ContainsKey("no-staging") ? null
                    : opts.ContainsKey("resume") && Util.Drives.Seeks(finalDir) == true && Util.Drives.ExistingStaging("TamrielMergeBuild") is { } left ? left
                    : Util.Drives.FastStaging(finalDir, ConvertSize(Has), "TamrielMergeBuild");
                if (staging is null && !opts.ContainsKey("no-staging") && Util.Drives.Seeks(finalDir) == true)
                    // 2026-10-15: with 70 GB free on the solid-state drive the build went to the hard disk without a word,
                    // and its hundreds of thousands of small files took many times as long.
                    Console.WriteLine($"Output: {finalDir} is on a hard disk and no solid-state drive has {ConvertSize(Has) >> 30} GB free beyond what is kept for "
                        + "Windows: building there directly, which is much slower. Free some space on a solid-state drive to build faster.");
                if (staging is not null)
                {
                    // A stopped run's partial build is cleared, unless this run carries it on (--resume).
                    if (Directory.Exists(staging) && !opts.ContainsKey("resume")) Directory.Delete(staging, true);
                    Directory.CreateDirectory(staging);
                    outDir = staging;
                    plan.Insert(plan.FindIndex(x => x.Step == "Creatures"), ("Moving the build", 3));
                    Console.WriteLine($"Output: {finalDir} is on a hard disk; building on a solid-state drive in {staging}, moved there after");
                }
                double planTotal = plan.Sum(x => x.Weight), planDone = 0;
                IDisposable Begin(string step)
                {
                    double w = plan.FirstOrDefault(x => x.Step == step).Weight;
                    var scope = Progress.Within(planDone / planTotal, w / planTotal);
                    planDone += w;
                    Progress.Report(0, step);
                    return scope;
                }
                using (Begin("Setup")) if (!skip.Contains("Setup")) Main(["setup"]);
                // The Creation Kit (voices, lip sync, scripts) before anything is converted: asks to install it when missing.
                var ck = Setup.Dependencies.EnsureCreationKit(skyrimData, prompt: !opts.ContainsKey("no-prompt") && !Console.IsInputRedirected);
                if (ck == Setup.Dependencies.CreationKitChoice.Quit) { Console.WriteLine("Stopped: install the Creation Kit, then run convert again."); return 1; }
                bool voices = ck == Setup.Dependencies.CreationKitChoice.Ready && !skip.Contains("Voices");
                var plugins = new List<string>();
                var failed = new List<string>();
                // Daggerfall's models and textures being moved to --out in the background (below), and its finishing.
                Task<string>? earlyMove = null;
                void AwaitEarlyMove()
                {
                    if (earlyMove is null) return;
                    Console.WriteLine(earlyMove.GetAwaiter().GetResult());
                    earlyMove = null;
                }
                // Moves the staged build into --out; with all: false only when the solid-state drive runs short of room.
                void Settle(bool all)
                {
                    // Moved early when a step more would cut into the room the drive keeps (Windows' own, on its drive).
                    if (outDir == finalDir || (!all && Util.Drives.FreeBytes(outDir) >= Util.Drives.Reserve(outDir) + (12L << 30))) return;
                    AwaitEarlyMove();
                    Console.WriteLine($"Moving the build into {finalDir}{(all ? "" : " (the solid-state drive is short of room)")}...");
                    try
                    {
                        int moved = Util.Drives.MoveInto(outDir, finalDir, f => Progress.Report(f, "Moving the build to the output drive"));
                        Console.WriteLine($"  {moved} files moved");
                        outDir = finalDir;
                    }
                    // A file in --out in use (Skyrim open): the rest goes on from the staging folder, named in the summary.
                    catch (IOException e) { Console.WriteLine($"  moving failed: {e.Message}"); failed.Add($"moving the build (left in {outDir})"); }
                }
                bool resume = opts.ContainsKey("resume");
                // With --resume: a file the stopped run finished, brought into the build folder; false when there is none.
                bool Reuse(string file)
                {
                    if (resume && outDir != finalDir && File.Exists(Path.Combine(outDir, file))) return true;   // still in the staging folder
                    if (!resume || !File.Exists(Path.Combine(finalDir, file))) return false;
                    if (outDir != finalDir) File.Copy(Path.Combine(finalDir, file), Path.Combine(outDir, file), overwrite: true);
                    return true;
                }
                void Step(Game game, string plugin, string[] stepArgs)
                {
                    if (!found.TryGetValue(game, out var install)) { Console.WriteLine($"{game}: not installed through Steam or GOG, skipped"); return; }
                    if (skip.Contains(game.ToString())) { Console.WriteLine($"{game}: skipped (--skip)"); return; }
                    Console.WriteLine($"{game} ({install.Store}): converting...");
                    try
                    {
                        if (Child(stepArgs) == 0 && File.Exists(Path.Combine(outDir, plugin))) plugins.Add(plugin);
                        else failed.Add(game.ToString());
                    }
                    catch (Exception e) { Console.WriteLine($"{game}: failed: {e.Message}"); failed.Add(game.ToString()); }
                }
                // A step in a process of its own, its progress passed on within this step's share and the rest of its
                // output as it is. 2026-10-15: Daggerfall's build run in this process left it holding 30 GB (the land and
                // its navmesh), so the town parts after it saw 3 GB free and ran one at a time, five hours instead of one.
                int Child(string[] args)
                {
                    var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, RedirectStandardOutput = true };
                    foreach (var a in args) psi.ArgumentList.Add(a);
                    using var p = System.Diagnostics.Process.Start(psi)!;
                    while (p.StandardOutput.ReadLine() is { } line)
                    {
                        var m = System.Text.RegularExpressions.Regex.Match(line, @"^PROGRESS (\d+(?:\.\d+)?) (.*)$");
                        if (m.Success) Progress.Report(double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture), m.Groups[2].Value);
                        else Console.WriteLine(line);
                    }
                    p.WaitForExit();
                    return p.ExitCode;
                }
                // Models for the Daggerfall sprites Skyrim has none of (seagulls, palms, cacti...), made from the sprites
                // themselves before Daggerfall is built (which then uses them); --skip Models keeps the vanilla stand-ins.
                if (Has(Game.Daggerfall))
                    using (Begin("Missing models"))
                        if (!skip.Contains("Models")) Main(opts.ContainsKey("gpu") ? ["sprite-models", "--make", "--gpu"] : ["sprite-models", "--make"]);
                // The games' textures upscaled from the user's own installs (upscale-textures), before the builds use them;
                // only what isn't upscaled yet. --skip Upscale keeps what is there (or the 4x pixel-art scaling).
                if (!skip.Contains("Upscale"))
                    using (Begin("Upscaling textures"))
                        foreach (var g in new[] { Game.Daggerfall, Game.Morrowind }.Where(Has))
                            if (Main(opts.ContainsKey("gpu") ? ["upscale-textures", "--game", g.ToString(), "--gpu"] : ["upscale-textures", "--game", g.ToString()]) != 0)
                                failed.Add($"{g} texture upscaling");
                // The land's plugins a stopped run made (one, or the land split in several), all kept or none.
                bool ReuseLand()
                {
                    var made = Skyrim.Terrain.OverlandTiles.LandPluginsIn(resume && outDir != finalDir && File.Exists(Path.Combine(outDir, Skyrim.Terrain.OverlandTiles.LandPluginName)) ? outDir : finalDir);
                    return made.Count > 0 && made[0] == Skyrim.Terrain.OverlandTiles.LandPluginName && made.All(Reuse);
                }
                if (Has(Game.Daggerfall) && Reuse("Daggerfall.esm") && ReuseLand())
                {
                    Begin("Daggerfall").Dispose();
                    plugins.Add("Daggerfall.esm");
                    Console.WriteLine("Daggerfall: Daggerfall.esm and its land kept from the stopped run (--resume)");
                }
                else if (Has(Game.Daggerfall)) using (Begin("Daggerfall")) Step(Game.Daggerfall, "Daggerfall.esm", ["build", "--out", Path.Combine(outDir, "Daggerfall.esm")]);
                else Step(Game.Daggerfall, "Daggerfall.esm", ["build", "--out", Path.Combine(outDir, "Daggerfall.esm")]);
                // The overland's land (one plugin, or several when it is too big for one: built a tile at a time by the
                // Daggerfall build), right after it; land plugins of an earlier run split otherwise aren't left in --out.
                if (plugins.Contains("Daggerfall.esm") && File.Exists(Path.Combine(outDir, Skyrim.Terrain.OverlandTiles.LandPluginName)))
                {
                    var lands = Skyrim.Terrain.OverlandTiles.LandPluginsIn(outDir);
                    plugins.AddRange(lands);
                    if (outDir != finalDir)
                        foreach (var stale in Skyrim.Terrain.OverlandTiles.LandPluginNames.Except(lands).Select(n => Path.Combine(finalDir, n)).Where(File.Exists))
                            File.Delete(stale);
                }
                // Daggerfall's other towns and its other 4,228 dungeons: one plugin each, built in parts, each part in its
                // own process so its memory is given back before the next, then joined into the one plugin.
                foreach (var dir in new[] { outDir, finalDir }.Distinct())
                    foreach (var old in Directory.GetFiles(dir, "Daggerfall Towns *.esm").Concat(Directory.GetFiles(dir, "Daggerfall Dungeons *.esm")).Concat(Directory.GetFiles(dir, "Daggerfall Dungeons *.creatures.json")))
                        File.Delete(old);
                // Parts an earlier conversion left in --out (Daggerfall Towns.part10.esm, ...): a new conversion makes its
                // own, so they go; a resumed one keeps only its own (made after its Daggerfall.esm, BuildParts).
                if (!resume)
                    foreach (var old in Directory.GetFiles(finalDir, "Daggerfall Towns.part*.*").Concat(Directory.GetFiles(finalDir, "Daggerfall Dungeons.part*.*")))
                        File.Delete(old);
                Settle(all: false);
                if (plugins.Contains("Daggerfall.esm") && !skip.Contains("Towns"))
                {
                    int parts;
                    using (var master = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(outDir, "Daggerfall.esm"), SkyrimRelease.SkyrimSE))
                        parts = Skyrim.DaggerfallTowns.PartCount(Skyrim.DaggerfallTowns.All(arena2, master));
                    using (Begin("Daggerfall's towns"))
                        if (BuildParts("df-towns", parts, Skyrim.DaggerfallTowns.PluginName, k => Skyrim.DaggerfallTowns.PartPath(outDir, k))) plugins.Add(Skyrim.DaggerfallTowns.PluginName);
                }
                Settle(all: false);
                if (plugins.Contains("Daggerfall.esm") && !skip.Contains("Dungeons"))
                    using (Begin("Daggerfall's dungeons"))
                    if (BuildParts("df-dungeons", Skyrim.DaggerfallDungeons.Parts, Skyrim.DaggerfallDungeons.PluginName, k => Skyrim.DaggerfallDungeons.PartPath(outDir, k))) plugins.Add(Skyrim.DaggerfallDungeons.PluginName);
                // The overland's town gates and dungeon entrances joined to the land's navmeshes (a light plugin after them).
                if (plugins.Contains(Skyrim.Terrain.OverlandTiles.LandPluginName) && (plugins.Contains(Skyrim.DaggerfallTowns.PluginName) || plugins.Contains(Skyrim.DaggerfallDungeons.PluginName)))
                    using (Begin("Daggerfall's overland doors"))
                        // Daggerfall.esm's own doors on the overland too (Privateer's Hold's: 2026-10-14, no navmesh led to it).
                        if (Main(["overland-doors", "--dir", outDir, "--plugins", string.Join(";", new[] { "Daggerfall.esm", Skyrim.DaggerfallTowns.PluginName, Skyrim.DaggerfallDungeons.PluginName }.Where(plugins.Contains))]) == 0)
                            plugins.Add(Skyrim.Terrain.OverlandDoorLinks.PluginName);
                // Daggerfall's own models and textures (two thirds of a build's bytes, and its land's distant terrain's
                // 100,000 files) moved to the hard disk while the other games are built, which leave the disk idle, rather
                // than all at the end (user, 2026-10-15: "fix everything and the gaps"). Nothing after reads them from the
                // build folder alone: the distant objects look in --out too, and the menu's render (which doesn't) waits.
                if (outDir != finalDir && plugins.Contains("Daggerfall.esm"))
                {
                    string from = outDir, to = finalDir;
                    string[] daggerfallOnly = [@"meshes\TamrielMerge\DF", @"meshes\TamrielMerge\Towns", @"meshes\TamrielMerge\Dungeons", @"textures\TamrielMerge\DF",
                                               @"meshes\terrain\" + DaggerfallPluginBuilder.WorldEditorId, @"textures\terrain\" + DaggerfallPluginBuilder.WorldEditorId];
                    earlyMove = Task.Run(() =>
                    {
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        int moved = 0;
                        try
                        {
                            foreach (var rel in daggerfallOnly) moved += Util.Drives.MoveInto(Path.Combine(from, rel), Path.Combine(to, rel));
                            return $"Daggerfall's models and textures: {moved} files moved into {to} beside the other games' builds ({clock.Elapsed.TotalMinutes:F1} min)";
                        }
                        // A file in --out in use: the rest goes with the build at the end.
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return $"Daggerfall's models and textures: moving stopped after {moved} files ({e.Message}); the rest go at the end"; }
                    });
                }
                bool BuildParts(string command, int parts, string plugin, Func<int, string> partPath)
                {
                    // Resumed after its parts were joined: the joined plugin (made after this Daggerfall.esm) is kept.
                    if (resume && new[] { outDir, finalDir }.Select(d => Path.Combine(d, plugin)).FirstOrDefault(File.Exists) is { } joined
                        && File.GetLastWriteTimeUtc(joined) > File.GetLastWriteTimeUtc(Path.Combine(outDir, "Daggerfall.esm")))
                    {
                        if (Path.GetDirectoryName(joined) != outDir) File.Copy(joined, Path.Combine(outDir, plugin), overwrite: true);
                        Console.WriteLine($"{plugin}: kept from the stopped run (joined after Daggerfall.esm)");
                        return true;
                    }
                    var done = new List<string>();
                    var pending = new List<int>();
                    for (int k = 1; k <= parts; k++)
                    {
                        // A part the stopped run finished (with its creature spots), kept: only one made after this
                        // Daggerfall.esm (2026-10-15: a resume took parts 10-14 from an older conversion left in --out,
                        // built against an older Daggerfall.esm: 100,833 links to the wrong records, navmeshes on the
                        // wrong grid). An older one is made again.
                        string partName = Path.GetFileName(partPath(k));
                        if (Reuse(partName) && File.GetLastWriteTimeUtc(partPath(k)) <= File.GetLastWriteTimeUtc(Path.Combine(outDir, "Daggerfall.esm")))
                        {
                            Console.WriteLine($"{plugin}: part {k} of {parts} is older than Daggerfall.esm, made again");
                            File.Delete(partPath(k));
                            pending.Add(k);
                        }
                        else if (File.Exists(partPath(k)))
                        {
                            if (File.Exists(Skyrim.CreatureCatalog.PathFor(Path.Combine(finalDir, partName))) && outDir != finalDir)
                                File.Copy(Skyrim.CreatureCatalog.PathFor(Path.Combine(finalDir, partName)), Skyrim.CreatureCatalog.PathFor(partPath(k)), overwrite: true);
                            Console.WriteLine($"{plugin}: part {k} of {parts} kept from the stopped run");
                            done.Add(partPath(k));
                        }
                        else pending.Add(k);
                    }
                    // Several parts at once (user, 2026-10-14: "should be a 1 hour conversion"; one at a time, a part's town
                    // layout left most of the cores idle): as many as the free memory holds, at most a share of the cores,
                    // or --parts-at-once N. A town part lays its towns out on one core; a dungeon part makes up to 8
                    // navmeshes at once (a quarter of the cores). Measured 2026-10-14 (town part 9): 5.7 GB each with
                    // heights of its own, 3.4 GB with the shared heights
                    // (and 4.1 GB with the larger first generation the parts now get); the old 3 GB budget ran six parts in 32 GB, into the
                    // page file, and a part took twice as long. Through a town part's run its memory goes between 1.7 and 3.3
                    // GB, the peaks brief (a big town's navmeshes) and not at once in every part: budget 3 GB a part, plus
                    // the shared heights' 2 GB, plus 4 GB for everything else. Each part is built in a folder of its own
                    // beside a copy of Daggerfall.esm, so parts never write the same file at once; when one ends its files
                    // are moved into the build folder. The parts' form IDs are their own ranges, so the joined plugin is the
                    // same however many ran together.
                    // At most a share of the cores (or --parts-at-once N); each part starts when the memory is free for it,
                    // judged as it starts, not once at the beginning (2026-10-15: measured while the app was being rebuilt, the
                    // last town parts ran 3 at a time all through). A part started in the last 45 seconds may not show its
                    // memory yet, so each of those counts its budget too (it had been half a minute between any two starts,
                    // which once the parts took a minute or two held a town part to two a minute: 84 parts, 42 minutes
                    // at the least; 2026-10-15), and a few seconds go between starts.
                    bool fixedCount = opts.TryGetValue("parts-at-once", out var pa);
                    int atOnce = fixedCount ? Math.Max(1, int.Parse(pa!)) : Math.Max(1, Environment.ProcessorCount / (command == "df-towns" ? 2 : 4));
                    if (pending.Count > 0) Console.WriteLine($"{plugin}: {pending.Count} parts, up to {atOnce} at a time as the memory allows");
                    int running = 0, mostAtOnce = 0;
                    var lastStart = DateTime.MinValue;
                    var recentStarts = new List<DateTime>();
                    var startGate = new object();
                    void WaitForRoom()
                    {
                        while (true)
                        {
                            lock (startGate)
                            {
                                var now = DateTime.Now;
                                recentStarts.RemoveAll(t => now - t > TimeSpan.FromSeconds(45));
                                if (running == 0 || fixedCount || (now - lastStart > TimeSpan.FromSeconds(5) && Util.Machine.FreeRamGb() >= 4 + 3 * (1 + recentStarts.Count)))
                                {
                                    running++;
                                    mostAtOnce = Math.Max(mostAtOnce, running);
                                    lastStart = now;
                                    recentStarts.Add(now);
                                    return;
                                }
                            }
                            Thread.Sleep(1000);
                        }
                    }
                    // What every part would make the same: the overland's heights (made once here, read in place by
                    // all), and the town meshes (the first part to make one leaves a copy for the rest).
                    string sharedDir = Path.Combine(outDir, "_parts", "_shared");
                    string heights = Path.Combine(sharedDir, "heights.bin");
                    // Made again when older than the converter (a resumed run after an update).
                    if (pending.Count > 0 && (!File.Exists(heights) || File.GetLastWriteTimeUtc(heights) < File.GetLastWriteTimeUtc(Environment.ProcessPath!)))
                    {
                        if (Directory.Exists(Path.Combine(sharedDir, "meshes"))) Directory.Delete(Path.Combine(sharedDir, "meshes"), true);
                        if (Directory.Exists(Path.Combine(sharedDir, "navmeshes"))) Directory.Delete(Path.Combine(sharedDir, "navmeshes"), true);
                        Directory.CreateDirectory(sharedDir);
                        var clock = System.Diagnostics.Stopwatch.StartNew();
                        Skyrim.Terrain.HeightField.Generate(DfWorldMap.Load(arena2), TerrainSettingsFrom([])).Save(heights);
                        GC.Collect();
                        Console.WriteLine($"Heights for the parts: {new FileInfo(heights).Length / 1048576} MB in {clock.Elapsed.TotalSeconds:F0} s");
                    }
                    var built = new System.Collections.Concurrent.ConcurrentBag<int>();
                    var gate = new object();
                    int finished = 0;
                    Parallel.ForEach(pending, new ParallelOptions { MaxDegreeOfParallelism = atOnce }, k =>
                    {
                        string work = Path.Combine(outDir, "_parts", $"{command}-{k}");
                        Directory.CreateDirectory(work);
                        File.Copy(Path.Combine(outDir, "Daggerfall.esm"), Path.Combine(work, "Daggerfall.esm"), overwrite: true);
                        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                        foreach (var a in new[] { command, "--plugin", Path.Combine(work, "Daggerfall.esm"), "--batch", k.ToString(), "--batches", parts.ToString() }) psi.ArgumentList.Add(a);
                        psi.Environment["TAMRIELMERGE_PROGRESS"] = "off";   // the parent reports the parts
                        psi.Environment["TAMRIELMERGE_HEIGHTS"] = heights;
                        psi.Environment["TAMRIELMERGE_TOWN_MESHES"] = Path.Combine(sharedDir, "meshes");
                        // Navmeshes too: a town's street cell or building of the same input as one another part made takes its
                        // mesh (NavmeshBuilder.SharedExteriors). Not the dungeons: each is laid out of its own (2026-10-15, part 2
                        // of 40 found none of part 1's).
                        if (command == "df-towns") psi.Environment["TAMRIELMERGE_NAVMESHES"] = Path.Combine(sharedDir, "navmeshes");
                        // A first generation of 256 MB: a town part makes 500 MB of short-lived objects a second, and the default
                        // budget collected 200 times every 5 seconds (part 9: 2.2 minutes, with it 1.8; half a gigabyte more).
                        psi.Environment["DOTNET_GCgen0size"] = "0x10000000";
                        int code;
                        WaitForRoom();
                        try { using var p = System.Diagnostics.Process.Start(psi)!; p.WaitForExit(); code = p.ExitCode; }
                        finally { lock (startGate) running--; }
                        bool ok = code == 0 && File.Exists(Path.Combine(work, Path.GetFileName(partPath(k))));
                        lock (gate)
                        {
                            File.Delete(Path.Combine(work, "Daggerfall.esm"));
                            Util.Drives.MoveInto(work, outDir);
                            if (ok) built.Add(k); else failed.Add($"{plugin} part {k} of {parts}");
                            finished++;
                            Progress.Report(finished / (double)Math.Max(1, pending.Count), $"{plugin}: {finished} of {pending.Count} parts built");
                        }
                    });
                    if (pending.Count > 0) Console.WriteLine($"{plugin}: at most {mostAtOnce} parts ran at once");
                    try { if (Directory.Exists(Path.Combine(outDir, "_parts"))) Directory.Delete(Path.Combine(outDir, "_parts"), true); } catch (IOException) { }
                    done.AddRange(built.Order().Select(partPath));
                    done.Sort((x, y) => PartNumber(x).CompareTo(PartNumber(y)));
                    static int PartNumber(string path) => int.Parse(System.Text.RegularExpressions.Regex.Match(Path.GetFileName(path), @"part(\d+)").Groups[1].Value);
                    if (done.Count == 0) return false;
                    string merged = Path.Combine(outDir, plugin);
                    long records = Skyrim.PluginMerger.Merge(done, merged);
                    // The parts' creature spots (dungeons) as one catalog for the merged plugin.
                    var catalogs = done.Select(Skyrim.CreatureCatalog.Load).Where(c => c is not null).ToList();
                    if (catalogs.Count > 0)
                    {
                        var all = catalogs[0]!;
                        foreach (var c in catalogs.Skip(1)) all.Spots.AddRange(c!.Spots);
                        all.Plugin = plugin;
                        all.Save(merged);
                    }
                    foreach (var part in done) { File.Delete(part); File.Delete(Skyrim.CreatureCatalog.PathFor(part)); }
                    // A resumed run's kept parts, joined now, aren't left in --out.
                    if (outDir != finalDir)
                        foreach (var part in done.Select(Path.GetFileName).Select(n => Path.Combine(finalDir, n!)))
                            if (File.Exists(part)) { File.Delete(part); File.Delete(Skyrim.CreatureCatalog.PathFor(part)); }
                    Console.WriteLine($"{plugin}: {records} records and groups from {done.Count} of {parts} parts");
                    return true;
                }
                Settle(all: false);
                // Redguard, Morrowind and Oblivion side by side, each in a process of its own (user, 2026-10-14: "should be a
                // 1 hour conversion"; one after another they took about 50 minutes, Oblivion alone 35). They write their own
                // folders (TamrielMerge's rg, mw and ob folders); each one's plugin appearing is its success.
                var games = new (Game Game, string Plugin, string[] Args)[]
                {
                    (Game.Redguard, "Redguard.esm", ["rg-build", "--out", outDir]),
                    (Game.Morrowind, "Morrowind.esm", ["mw-build", "--out", outDir]),
                    (Game.Oblivion, "Oblivion.esm", ["ob-build", "--out", outDir]),
                };
                var toBuild = new List<(Game Game, string Plugin, string[] Args)>();
                foreach (var g in games)
                {
                    if (!found.TryGetValue(g.Game, out var install)) Console.WriteLine($"{g.Game}: not installed through Steam or GOG, skipped");
                    else if (skip.Contains(g.Game.ToString())) Console.WriteLine($"{g.Game}: skipped (--skip)");
                    // Resumed: a game the stopped run finished is kept from the build folder, which only this series of
                    // runs wrote (a new conversion clears it). 2026-10-15: one game's failure had the others built again.
                    else if (resume && outDir != finalDir && File.Exists(Path.Combine(outDir, g.Plugin)))
                    {
                        Console.WriteLine($"{g.Game}: {g.Plugin} kept from the stopped run (--resume)");
                        plugins.Add(g.Plugin);
                    }
                    else { Console.WriteLine($"{g.Game} ({install.Store}): converting..."); toBuild.Add(g); }
                }
                using (toBuild.Count > 0 ? Begin(toBuild[^1].Game.ToString()) : null)
                {
                    // The progress steps of the games built together, as one share.
                    foreach (var g in toBuild.SkipLast(1)) Begin(g.Game.ToString()).Dispose();
                    var results = new System.Collections.Concurrent.ConcurrentDictionary<Game, bool>();
                    int ended = 0;
                    Parallel.ForEach(toBuild, new ParallelOptions { MaxDegreeOfParallelism = toBuild.Count == 0 ? 1 : toBuild.Count }, g =>
                    {
                        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                        foreach (var a in g.Args) psi.ArgumentList.Add(a);
                        psi.Environment["TAMRIELMERGE_PROGRESS"] = "off";
                        int code;
                        try { using var p = System.Diagnostics.Process.Start(psi)!; p.WaitForExit(); code = p.ExitCode; }
                        catch (Exception e) { Console.WriteLine($"{g.Game}: failed: {e.Message}"); code = -1; }
                        results[g.Game] = code == 0 && File.Exists(Path.Combine(outDir, g.Plugin));
                        int n = Interlocked.Increment(ref ended);
                        Progress.Report(n / (double)toBuild.Count, $"{g.Game} converted ({n} of {toBuild.Count})");
                    });
                    foreach (var g in toBuild)
                        if (results.GetValueOrDefault(g.Game)) plugins.Add(g.Plugin); else failed.Add(g.Game.ToString());
                }
                Settle(all: false);
                var recorded = Begin("Recorded voices");
                // The games' own voice acting as Skyrim voice files (with lip sync), for the converted dialogue.
                // Into --out itself, where an earlier run's voice files are kept (packed only when missing).
                if (voices && plugins.Contains("Redguard.esm")) Main(["rg-voices", "--plugin", Path.Combine(outDir, "Redguard.esm"), "--out", finalDir]);
                if (voices && plugins.Contains("Oblivion.esm")) Main(["ob-voices", "--plugin", Path.Combine(outDir, "Oblivion.esm"), "--out", finalDir]);
                if (!voices) Console.WriteLine("Voices: skipped (no Creation Kit, or --skip Voices)");
                recorded.Dispose();
                using (Begin("Intro videos"))
                    if (!skip.Contains("Videos")) Main(opts.ContainsKey("gpu") ? ["videos", "--out", finalDir, "--gpu"] : ["videos", "--out", finalDir]);
                // The menu's render reads the models from the build folder only: Daggerfall's are there or in --out by now.
                AwaitEarlyMove();
                var menuStep = Begin("Main menu");
                // The main menu: the red emblem over a ray-traced intro flying over the conversions (GPU-heavy: --gpu); its music always.
                if (!skip.Contains("Menu"))
                {
                    // Its music needs no graphics card.
                    if (Main(["menu-music", "--out", outDir]) != 0) failed.Add("main menu music");
                    if (opts.ContainsKey("gpu")) { if (Main(["menu", "--plugins", outDir, "--out", outDir]) != 0) failed.Add("main menu"); }
                    else Console.WriteLine("Main menu: skipped (it renders on the graphics card: add --gpu)");
                    // Packed as its own part (TamrielMerge Menu.esp), so the app can switch it off for Skyrim's menu.
                    Main(["menu-pack", "--out", outDir]);
                    if (File.Exists(Path.Combine(outDir, Skyrim.Render.MenuArchive.PluginName))) plugins.Add(Skyrim.Render.MenuArchive.PluginName);
                }
                menuStep.Dispose();
                // The games' creatures with skeletons and animations of their own (docs/creatures.md): every creature in
                // tools\creatures whose game was converted, built into the output from its shipped export (Morrowind's from
                // the user's own game, remastered only with --gpu), then one light plugin per game over that game's plugin.
                using (Begin("Creature models"))
                    if (!skip.Contains("Creature models") && Directory.Exists(Path.Combine(Setup.Dependencies.Root, "creatures")))
                    {
                        var builtCreatures = new List<string>();
                        foreach (var cdir in Directory.GetDirectories(Path.Combine(Setup.Dependencies.Root, "creatures")).Order(StringComparer.OrdinalIgnoreCase))
                        {
                            string cfgPath = Path.Combine(cdir, "creature.json");
                            if (!File.Exists(cfgPath)) continue;
                            string cid = Path.GetFileName(cdir), gamePlugin;
                            using (var cfg = System.Text.Json.JsonDocument.Parse(File.ReadAllText(cfgPath)))
                                gamePlugin = Skyrim.Creatures.CreatureRecords.GamePlugin(cfg.RootElement.TryGetProperty("game", out var cg) ? cg.GetString() : null);
                            if (!plugins.Contains(gamePlugin)) continue;
                            string[] buildArgs = opts.ContainsKey("gpu") ? ["creature-build", "--creature", cid, "--out", outDir] : ["creature-build", "--creature", cid, "--out", outDir, "--no-remaster"];
                            try
                            {
                                if (Main(buildArgs) == 0) builtCreatures.Add(cid);
                                else failed.Add($"creature model {cid}");
                            }
                            catch (Exception e) { Console.WriteLine($"Creature model {cid}: failed: {e.Message}"); failed.Add($"creature model {cid}"); }
                        }
                        if (builtCreatures.Count > 0)
                        {
                            var gamePaths = plugins.Where(p => p is "Daggerfall.esm" or "Redguard.esm" or "Morrowind.esm" or "Oblivion.esm").Select(p => Path.Combine(outDir, p));
                            if (Main(["creature-plugin", "--creatures", string.Join(",", builtCreatures), "--plugins", string.Join(";", gamePaths), "--out", outDir]) == 0)
                            {
                                foreach (var gp in new[] { "Daggerfall.esm", "Redguard.esm", "Morrowind.esm", "Oblivion.esm" }.Select(Skyrim.Creatures.CreatureRecords.PluginFor))
                                    if (File.Exists(Path.Combine(outDir, gp))) plugins.Add(gp);
                            }
                            else failed.Add("creature models' plugins");
                        }
                    }
                // The distant view's objects and trees for the converted worlds (their land is made with each game).
                using (Begin("Distant objects and trees"))
                    if (!skip.Contains("LOD") && plugins.Any(p => p is "Daggerfall.esm" or "Redguard.esm" or "Morrowind.esm" or "Oblivion.esm"))
                        try { if (Main(outDir == finalDir ? ["lod", "--dir", outDir, "--only", "objects,trees"] : ["lod", "--dir", outDir, "--only", "objects,trees", "--assets", finalDir]) != 0) failed.Add("distant objects and trees"); }
                        catch (Exception e) { Console.WriteLine($"Distant LOD: failed: {e.Message}"); failed.Add("distant objects and trees"); }
                if (outDir != finalDir) using (Begin("Moving the build")) Settle(all: true);
                // An older menu's loose files where the archive now goes override it: removed (MenuArchive.RemoveLoose),
                // whether this run made the menu or one from before is there (2026-10-15: a conversion without --gpu left
                // the 10-10 loose flyby in Data, and the menu kept showing it).
                foreach (var dir in new[] { finalDir }.Concat(opts.ContainsKey("install") ? [skyrimData] : Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (plugins.Contains(Skyrim.Render.MenuArchive.PluginName) || File.Exists(Path.Combine(dir, Skyrim.Render.MenuArchive.PluginName)))
                        if (Skyrim.Render.MenuArchive.RemoveLoose(dir) is { Count: > 0 } removed)
                            Console.WriteLine($"Main menu: {removed.Count} older loose menu files removed from {dir} (they hid the new menu's archive)");
                // The graphics plugin and its DLSS runtime, left by earlier conversions: no longer part of the tool.
                foreach (var dir in new[] { finalDir }.Concat(opts.ContainsKey("install") ? [skyrimData] : Array.Empty<string>()).Distinct(StringComparer.OrdinalIgnoreCase))
                    if (Skyrim.AltStartBuilder.RemoveGraphicsPlugin(dir) is { Count: > 0 } gone)
                        Console.WriteLine($"Graphics plugin removed from {dir}: {string.Join(", ", gone)}");
                var creatureStep = Begin("Creatures");
                // Procedural creatures (--procedural-creatures): ordinary creatures from all the converted games, spawning
                // and respawning by habitat in every world (--respawn-scale multiplies the games' respawn times).
                if (opts.ContainsKey("procedural-creatures") && plugins.Count > 0)
                {
                    if (Main(["creatures", "--dir", outDir, "--respawn-scale", opts.GetValueOrDefault("respawn-scale", "1")]) == 0) plugins.Add(Skyrim.CreatureSpawns.PluginName);
                    else failed.Add("procedural creatures");
                }
                creatureStep.Dispose();
                var storyStep = Begin("Story and progression");
                if (Main(["start-build", "--dir", outDir]) == 0) plugins.Add(Skyrim.AltStartBuilder.PluginName);
                else failed.Add("start plugin");
                // Daggerfall's travel in every converted world, seen in real time (the Tamriel menu's Travel tab).
                try
                {
                    if (Main(["travel-build", "--dir", outDir]) == 0) plugins.Add(Skyrim.Travel.TravelPluginBuilder.PluginName);
                    else failed.Add("travel");
                }
                catch (Exception e) { Console.WriteLine($"Travel: failed: {e.Message}"); failed.Add("travel"); }
                // Factions, the other games' skills and the passive web, in the Tamriel menu.
                try
                {
                    if (Main(["progression-build", "--dir", outDir]) == 0) plugins.Add(Skyrim.Progression.ProgressionBuilder.PluginName);
                    else failed.Add("progression");
                }
                catch (Exception e) { Console.WriteLine($"Progression: failed: {e.Message}"); failed.Add("progression"); }
                // The user's model replacements (chosen in the app's model viewer) over the fresh plugins.
                if (Skyrim.Models.ModelCatalog.LoadOverrides(outDir).Count > 0)
                {
                    try
                    {
                        if (Skyrim.Models.ModelCatalog.Apply(outDir, skyrimData) > 0) plugins.Add(Skyrim.Models.ModelCatalog.PluginName);
                    }
                    catch (Exception e) { Console.WriteLine($"Model replacements: failed: {e.Message}"); failed.Add("model replacements"); }
                }

                AwaitEarlyMove();
                storyStep.Dispose();
                Console.WriteLine();
                Console.WriteLine($"Converted into {outDir}. Enable these plugins, in this order, after Skyrim's own:");
                foreach (var p in plugins) Console.WriteLine($"  {p}");
                if (failed.Count > 0) Console.WriteLine($"Failed: {string.Join(", ", failed)} (the others work without them)");
                Console.WriteLine("Needed: SKSE and Address Library for SKSE Plugins. They run the Tamriel menu, the guide, Daggerfall's seamless overland (SKSE/Plugins/TamrielMergeSeamless.dll: physics past 64 cells) and the era in the date (TamrielMergeEra.dll).");
                if (opts.ContainsKey("install"))
                using (Begin("Installing"))
                {
                    foreach (var file in Directory.EnumerateFiles(outDir, "*", SearchOption.AllDirectories))
                    {
                        string rel = Path.GetRelativePath(outDir, file);
                        if (rel.Equals("problems.txt", StringComparison.OrdinalIgnoreCase) || rel.EndsWith(".voicemap.json", StringComparison.OrdinalIgnoreCase)) continue;
                        string target = Path.Combine(skyrimData, rel);
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        File.Copy(file, target, overwrite: true);
                    }
                    Console.WriteLine($"Installed into {skyrimData}");
                    // Switched on where Skyrim reads its plugin list (its own, and Mod Organizer 2's profile; --mo2 DIR).
                    foreach (var line in Setup.PluginList.Enable(plugins, opts.GetValueOrDefault("mo2"))) Console.WriteLine($"  {line}");
                }
                // Last, as it takes days: Morrowind's dialogue spoken in Skyrim's voices (the voice generator; the app can
                // pause it, and running it again resumes). --skip VoiceGen leaves it for later.
                if (voices && plugins.Contains("Morrowind.esm") && !skip.Contains("VoiceGen"))
                    using (Begin("Voice generation"))
                    {
                        var vargs = new List<string> { "voices", "--out", outDir };
                        if (opts.ContainsKey("gpu")) vargs.Add("--gpu");
                        if (opts.ContainsKey("install")) vargs.Add("--install");
                        if (Main([.. vargs]) == 1) failed.Add("voice generation");
                    }
                Progress.Report(1, failed.Count == 0 ? "Done" : $"Done; failed: {string.Join(", ", failed)}");
                return failed.Count == 0 ? 0 : 2;
            }
            case "drives":
            {
                // The user's drives as the convert sees them (Util.Drives): which seek (hard disks), their free room, and
                // where a convert into --out (default out/convert) would be built.
                foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
                    Console.WriteLine($"  {d.Name} {(Util.Drives.Seeks(d.Name) switch { true => "hard disk", false => "solid-state", null => "unknown" })}, {d.AvailableFreeSpace / (double)(1L << 30):F0} GB free");
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/convert"));
                var games = GameInstalls.Detect();
                long need = ConvertSize(g => games.ContainsKey(g));
                Console.WriteLine($"A convert of the installed games needs about {need >> 30} GB while it builds");
                Console.WriteLine($"Convert into {outDir}: built {(Util.Drives.FastStaging(outDir, need, "TamrielMergeBuild") is { } st ? $"in {st}, then moved" : "in place")}");
                return 0;
            }
            case "faction-check":
            {
                // Checks that each converted game's guilds are its own factions (--dir, or --plugins A,B full paths):
                // same-named factions across the games' plugins, any link from them to Skyrim's own guild factions (which
                // would merge memberships), and any actor answering to Skyrim's crime factions (each game's law is its own).
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                using var sk = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var guildWords = new[] { "DarkBrotherhood", "ThievesGuild", "Companions", "College", "MagesGuild", "FightersGuild", "Blades", "Vampire", "Imperial", "Stormcloak" };
                var skyrimGuilds = sk.Factions.Where(f => f.EditorID is { } e && guildWords.Any(w => e.Contains(w, StringComparison.OrdinalIgnoreCase)))
                    .ToDictionary(f => f.FormKey, f => f.EditorID!);
                var byName = new List<(string Name, string Plugin, FormKey Key)>();
                int links = 0, crimeActors = 0;
                // Each game's law is its own (docs/progression.md, "Crime by game and area"): no converted actor may answer
                // to Skyrim's crime factions, by its own list, its crime faction, or what a vanilla template or leveled
                // list gives it through "use factions" (members only: vanilla's rank -1 is "not a member").
                var vanillaMods = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => Path.Combine(skyrimData, f)).Where(File.Exists)
                    .Select(f => (ISkyrimModDisposableGetter)SkyrimMod.CreateFromBinaryOverlay(f, SkyrimRelease.SkyrimSE)).ToList();
                var skyrimCrime = vanillaMods.SelectMany(v => v.Factions).Where(CrimeFactions.IsSkyrimCrimeFaction).GroupBy(f => f.FormKey).ToDictionary(g => g.Key, g => g.Last().EditorID ?? g.Key.ToString());
                var vanillaNpcs = vanillaMods.SelectMany(v => v.Npcs).GroupBy(n => n.FormKey).ToDictionary(g => g.Key, g => g.Last());
                var vanillaLists = vanillaMods.SelectMany(v => v.LeveledNpcs).GroupBy(n => n.FormKey).ToDictionary(g => g.Key, g => g.Last());
                var files = opts.TryGetValue("plugins", out var only) ? only.Split(',').Select(Path.GetFullPath).ToList()
                    : new[] { "Daggerfall.esm", "Redguard.esm", "Morrowind.esm", "Oblivion.esm" }.Select(p => Path.Combine(dir, p)).ToList();
                foreach (var file in files.Where(File.Exists))
                {
                    using var m = SkyrimMod.CreateFromBinaryOverlay(file, SkyrimRelease.SkyrimSE);
                    foreach (var f in m.Factions.Where(f => f.FormKey.ModKey == m.ModKey)) byName.Add((f.Name?.String ?? f.EditorID ?? "", Path.GetFileName(file), f.FormKey));
                    foreach (var rec in m.EnumerateMajorRecords())
                        foreach (var link in rec.EnumerateFormLinks())
                            if (skyrimGuilds.TryGetValue(link.FormKey, out var g)) { links++; Console.WriteLine($"  {Path.GetFileName(file)}: {rec.EditorID} -> Skyrim's {g}"); }
                    var npcs = m.Npcs.ToDictionary(n => n.FormKey);
                    var lists = m.LeveledNpcs.ToDictionary(n => n.FormKey);
                    var memo = new Dictionary<FormKey, HashSet<FormKey>>();
                    HashSet<FormKey> CrimeOf(FormKey key, int depth)
                    {
                        if (memo.TryGetValue(key, out var hit)) return hit;
                        var found = memo[key] = [];
                        if (depth > 12) return found;
                        if ((npcs.TryGetValue(key, out var n) ? n : vanillaNpcs.GetValueOrDefault(key)) is { } npc)
                        {
                            bool inherits = (npc.Configuration.TemplateFlags & NpcConfiguration.TemplateFlag.Factions) != 0 && !npc.Template.IsNull;
                            if (inherits) found.UnionWith(CrimeOf(npc.Template.FormKey, depth + 1));
                            else
                            {
                                if (skyrimCrime.ContainsKey(npc.CrimeFaction.FormKey)) found.Add(npc.CrimeFaction.FormKey);
                                foreach (var r in npc.Factions.Where(r => r.Rank >= 0 && skyrimCrime.ContainsKey(r.Faction.FormKey))) found.Add(r.Faction.FormKey);
                            }
                        }
                        else if ((lists.TryGetValue(key, out var l) ? l : vanillaLists.GetValueOrDefault(key)) is { } list)
                            foreach (var e in list.Entries ?? [])
                                if (e.Data?.Reference.FormKey is { } refKey) found.UnionWith(CrimeOf(refKey, depth + 1));
                        return found;
                    }
                    foreach (var npc in m.Npcs.Where(n => n.FormKey.ModKey == m.ModKey))
                        if (CrimeOf(npc.FormKey, 0) is { Count: > 0 } crimes)
                        {
                            if (crimeActors++ < 40) Console.WriteLine($"  {Path.GetFileName(file)}: {npc.EditorID} answers to Skyrim's {string.Join(", ", crimes.Select(c => skyrimCrime[c]))}");
                        }
                    var own = m.Factions.Where(f => f.FormKey.ModKey == m.ModKey && (f.Flags & Faction.FactionFlag.TrackCrime) != 0).ToList();
                    Console.WriteLine($"  {Path.GetFileName(file)}: {own.Count} crime factions of its own ({string.Join(", ", own.Take(4).Select(f => f.EditorID))}{(own.Count > 4 ? ", ..." : "")}), "
                        + $"{own.Count(f => !f.ExteriorJailMarker.IsNull && !f.PlayerInventoryContainer.IsNull && !f.StolenGoodsContainer.IsNull)} with a jail; "
                        + $"{m.Npcs.Count(n => n.FormKey.ModKey == m.ModKey && own.Any(f => f.FormKey == n.CrimeFaction.FormKey))} actors answer to them");
                }
                foreach (var v in vanillaMods) v.Dispose();
                foreach (var g in byName.GroupBy(n => n.Name.Replace("The ", ""), StringComparer.OrdinalIgnoreCase).Where(g => g.Select(x => x.Plugin).Distinct().Count() > 1))
                    Console.WriteLine($"  \"{g.Key}\": {string.Join("; ", g.Select(x => $"{x.Plugin} {x.Key.ID:X6}"))} (separate records)");
                Console.WriteLine($"links to Skyrim's guild factions: {links}; actors answering to Skyrim's crime factions: {crimeActors}");
                return links == 0 && crimeActors == 0 ? 0 : 2;
            }
            case "progression-build":
            {
                // "TamrielMerge Progression.esp" and the Tamriel menu, from the converted games in --dir.
                // --games DIR reads the converted games' plugins from there instead (e.g. Skyrim's Data, building into a
                // scratch --dir).
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                string? games = opts.TryGetValue("games", out var gamesDir) ? Path.GetFullPath(gamesDir) : null;
                var r = Skyrim.Progression.ProgressionBuilder.Build(dir, skyrimData, games, Console.WriteLine);
                foreach (var g in r.Factions.GroupBy(f => f.Game))
                    Console.WriteLine($"  {g.Key}: {g.Count()} factions ({string.Join(", ", g.Take(8).Select(f => $"{f.Name} [{f.Ranks.Length}]"))}{(g.Count() > 8 ? ", ..." : "")})");
                Console.WriteLine($"  skills: {string.Join(", ", r.Skills.Select(s => s.Name))}; {r.Perks} perk ranks; Daggerfall's own skills listed: {r.DaggerfallSkills}");
                var gated = r.Web.Nodes.Where(n => n.Gate >= 0).ToList();
                Console.WriteLine($"  passive web: {r.Web.Nodes.Count} nodes, {r.Web.Links.Count} links, {r.Web.Nodes.Count(n => n.Perk is not null)} notables and keystones, {gated.Count} gated by a faction ({string.Join(", ", gated.Select(n => $"{n.Name}: {Skyrim.Progression.PassiveWeb.Groups[n.Gate].Name} {n.GateRank}"))})");
                Console.WriteLine($"Progression: {r.Factions.Count} joinable factions; menu {r.Menu}; wrote {r.Path}");
                return 0;
            }
            case "flash-test":
            {
                // Dev aid: compiles a small AS2 menu (a panel, a title, a close button that tells Papyrus) to --out.
                string source = """
                    var panel:MovieClip = this.createEmptyMovieClip("panel", 1);
                    panel.beginFill(0x101014, 85);
                    panel.moveTo(240, 120); panel.lineTo(1040, 120); panel.lineTo(1040, 600); panel.lineTo(240, 600); panel.lineTo(240, 120);
                    panel.endFill();
                    this.createTextField("title", 2, 260, 140, 760, 40);
                    var fmt:TextFormat = new TextFormat();
                    fmt.font = "$EverywhereBoldFont"; fmt.size = 28; fmt.color = 0xFFFFFF;
                    title.embedFonts = true; title.setNewTextFormat(fmt); title.text = "Tamriel";
                    var close:MovieClip = this.createEmptyMovieClip("close", 3);
                    close.beginFill(0x303038, 100); close.moveTo(940, 550); close.lineTo(1020, 550); close.lineTo(1020, 585); close.lineTo(940, 585); close.lineTo(940, 550); close.endFill();
                    close.onRelease = function() { skse.SendModEvent("TM_MenuClose", "", 0); skse.CloseMenu("CustomMenu"); };
                    function SetTitle(text:String) { title.text = text; }
                    """;
                // --source FILE compiles that ActionScript instead (e.g. Interface/tamriel.as, to check it compiles).
                if (opts.TryGetValue("source", out var sourceFile)) source = File.ReadAllText(sourceFile);
                string outSwf = opts.GetValueOrDefault("out", "out/flashtest/Interface/TamrielMerge/test.swf");
                Console.WriteLine(Skyrim.Ui.FlashMenus.Compile(source, outSwf).Trim());
                Console.WriteLine($"wrote {Path.GetFullPath(outSwf)} ({new FileInfo(outSwf).Length} bytes)");
                return 0;
            }
            case "catalog-check":
            {
                // Dev aid: a game plugin's creature list (--plugin) with each spot's creature, by habitat.
                var catalog = Skyrim.CreatureCatalog.Load(opts["plugin"]) ?? throw new FileNotFoundException("no creature list");
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var bases = plugin.EnumerateMajorRecords<IPlacedNpcGetter>().ToDictionary(r => Skyrim.CreatureCatalog.Key(r.FormKey), r => r.Base.FormKey);
                var names = plugin.Npcs.ToDictionary(n => n.FormKey, n => n.EditorID ?? n.Name?.String ?? "?");
                foreach (var g in catalog.Spots.GroupBy(s => s.Habitat))
                    Console.WriteLine($"{g.Key}: {string.Join(", ", g.GroupBy(s => bases.TryGetValue(s.Ref, out var b) ? names.GetValueOrDefault(b, b.ToString()) : "?").Select(x => $"{x.Key} x{x.Count()}"))}");
                return 0;
            }
            case "creatures":
            {
                // Procedural creatures (the converter's option): the creatures plugin, built from the converted games in
                // --dir (their plugins and .creatures.json lists). --respawn-scale multiplies each game's respawn time.
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                float scale = float.Parse(opts.GetValueOrDefault("respawn-scale", "1"), System.Globalization.CultureInfo.InvariantCulture);
                var report = Skyrim.CreatureSpawns.Build(dir, skyrimData, scale);
                foreach (var line in report.Plugins) Console.WriteLine($"  {line}");
                Console.WriteLine($"Procedural creatures: {report.Creatures} creatures from {report.Games} games; {report.Replaced} placements became spawners, {report.NewPoints} new spawn points");
                Console.WriteLine($"  by habitat: {string.Join(", ", report.PerHabitat.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}");
                Console.WriteLine($"Wrote {Path.Combine(dir, Skyrim.CreatureSpawns.PluginName)}");
                return 0;
            }
            case "rg-cd":
            {
                // Dev aid: the files on Redguard's CD image, by folder (--list FOLDER lists one folder's files).
                string rgDir = GameInstalls.DataDir(Game.Redguard) ?? throw new DirectoryNotFoundException("Redguard isn't installed through Steam or GOG");
                using var cd = new Redguard.RgCdImage(Path.Combine(rgDir, "REDGUARD.bin"));
                foreach (var g in cd.Files.GroupBy(f => Path.GetDirectoryName(f) ?? "").OrderBy(g => g.Key))
                    Console.WriteLine($"{(g.Key.Length == 0 ? "(root)" : g.Key)}: {g.Count()} files, {g.Sum(f => cd.SizeOf(f)) / 1024 / 1024} MB  e.g. {string.Join(", ", g.Take(4).Select(Path.GetFileName))}");
                if (opts.TryGetValue("extract", out var pattern))
                {
                    // --extract NAME[,NAME] copies those files to --out (default out/rgcd).
                    string dest = opts.GetValueOrDefault("out", "out/rgcd");
                    Directory.CreateDirectory(dest);
                    foreach (var name in pattern.Split(','))
                        if (cd.Read(name) is { } bytes) { File.WriteAllBytes(Path.Combine(dest, name), bytes); Console.WriteLine($"extracted {name}"); }
                }
                if (opts.TryGetValue("list", out var folder))
                    foreach (var f in cd.Files.Where(f => (Path.GetDirectoryName(f) ?? "").Equals(folder, StringComparison.OrdinalIgnoreCase)).Order()) Console.WriteLine($"  {f} {cd.SizeOf(f)}");
                return 0;
            }
            case "rg-stats":
            {
                // Dev aid: how much of Redguard's data the readers parse: texture banks, models (loose and in ROB archives),
                // worlds and scenes, with coordinate ranges.
                string rg = GameInstalls.DataDir(Game.Redguard) ?? throw new DirectoryNotFoundException("Redguard isn't installed through Steam or GOG");
                string art = Path.Combine(rg, "3dart"), maps = Path.Combine(rg, "maps");
                var banks = Directory.GetFiles(art, "TEXTURE.*").Select(f => Redguard.RgTextureBank.Load(File.ReadAllBytes(f))).ToList();
                Console.WriteLine($"texture banks: {banks.Count(b => b is not null)}/{banks.Count}, {banks.Sum(b => b?.Images.Count ?? 0)} images");
                int ok = 0, all = 0;
                foreach (var f in Directory.GetFiles(art, "*.3D*")) { all++; if (Redguard.RgModel.Load(File.ReadAllBytes(f)) is not null) ok++; }
                Console.WriteLine($"loose models: {ok}/{all}");
                int segOk = 0, segAll = 0, refs = 0;
                foreach (var f in Directory.GetFiles(art, "*.ROB"))
                {
                    var rob = Redguard.RgRob.Load(File.ReadAllBytes(f));
                    if (rob is null) { Console.WriteLine($"  {Path.GetFileName(f)}: unreadable"); continue; }
                    foreach (var s in rob.Segments)
                    {
                        if (s.Type == 512) { refs++; continue; }
                        segAll++;
                        if (Redguard.RgModel.Load(rob.Data, s.DataOffset) is not null) segOk++;
                    }
                }
                Console.WriteLine($"ROB embedded models: {segOk}/{segAll}, external references: {refs}");
                foreach (var f in Directory.GetFiles(maps, "*.WLD"))
                {
                    var w = Redguard.RgWorld.Load(File.ReadAllBytes(f));
                    Console.WriteLine($"world {Path.GetFileName(f)}: {(w is null ? "unreadable" : $"heights {Enumerable.Range(0, 256 * 256).Min(i => w.HeightAt(i % 256, i / 256))}..{Enumerable.Range(0, 256 * 256).Max(i => w.HeightAt(i % 256, i / 256))}")}");
                }
                foreach (var f in Directory.GetFiles(maps, "*.RGM").Where(f => opts.GetValueOrDefault("scene", "ISLAND").Split(',').Any(n => Path.GetFileNameWithoutExtension(f).Equals(n, StringComparison.OrdinalIgnoreCase))))
                {
                    var sc = Redguard.RgScene.Load(File.ReadAllBytes(f))!;
                    Console.WriteLine($"scene {Path.GetFileName(f)}: sections {string.Join(",", sc.Sections.Keys)}; {sc.Statics.Count} statics, {sc.Objects.Count} objects");
                    foreach (var set in new[] { sc.Statics, sc.Objects })
                        if (set.Count > 0)
                            Console.WriteLine($"   x {set.Min(o => o.Position.X)}..{set.Max(o => o.Position.X)}  y {set.Min(o => o.Position.Y)}..{set.Max(o => o.Position.Y)}  z {set.Min(o => o.Position.Z)}..{set.Max(o => o.Position.Z)}  e.g. {string.Join(", ", set.Take(5).Select(o => $"{o.Model}{(o.Script.Length > 0 ? "/" + o.Script : "")}@{o.Position}"))}");
                }
                return 0;
            }
            case "water-template":
                // Dev aid: vanilla's placed-water mesh, as Skyrim/Meshes/WaterSurface copies it.
                Skyrim.Meshes.WaterSurface.Describe(skyrimData);
                return 0;
            case "sk-gmst":
            {
                // Dev aid: Skyrim.esm's game settings whose EditorID matches --match (regex), with their values.
                using var sk = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts["match"], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var g in sk.GameSettings.Where(g => g.EditorID is { } e && rx.IsMatch(e)))
                    Console.WriteLine($"{g.EditorID} = {g switch { IGameSettingFloatGetter f => f.Data?.ToString(), IGameSettingIntGetter i => i.Data?.ToString(), IGameSettingBoolGetter b => b.Data?.ToString(), IGameSettingStringGetter t => t.Data?.String, _ => "?" }}");
                return 0;
            }
            case "sk-music":
            {
                // Dev aid: Skyrim.esm's music types matching --match (regex): priority, flags, tracks.
                using var sk = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("match", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var m in sk.MusicTypes.Where(m => m.EditorID is { } e && rx.IsMatch(e)))
                    Console.WriteLine($"{m.FormKey.ID:X6} {m.EditorID}: priority {m.Data?.Priority}, flags {m.Flags}, {m.Tracks?.Count ?? 0} tracks");
                return 0;
            }
            case "sk-sounds":
            {
                // Dev aid: Skyrim.esm's sound categories, and sound descriptors whose EditorID matches --match, with category.
                using var sk = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var cats = sk.SoundCategories.ToDictionary(c => c.FormKey, c => c.EditorID ?? "");
                foreach (var c in sk.SoundCategories.Where(c => (c.EditorID ?? "").Contains("VOC", StringComparison.OrdinalIgnoreCase))) Console.WriteLine($"category {c.FormKey.ID:X6} {c.EditorID}");
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("match", "^$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var d in sk.SoundDescriptors.Where(d => d.EditorID is { } e && rx.IsMatch(e)).Take(15))
                    Console.WriteLine($"sound {d.FormKey.ID:X6} {d.EditorID}: category {cats.GetValueOrDefault(d.Category.FormKey)}, files {string.Join(",", d.SoundFiles.Select(f => f.DataRelativePath.Path))}");
                return 0;
            }
            case "sk-form":
            {
                // Dev aid: a Skyrim.esm record by FormID (--id hex): its type, EditorID, model and, for activators, water type.
                using var sk = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var key = new FormKey(sk.ModKey, Convert.ToUInt32(opts["id"], 16));
                var rec = sk.EnumerateMajorRecords().FirstOrDefault(r => r.FormKey == key);
                if (rec is null) { Console.WriteLine("not found"); return 1; }
                Console.WriteLine($"{rec.GetType().Name.Replace("BinaryOverlay", "")} {rec.EditorID}");
                if (rec is IModeledGetter { Model: { } model }) Console.WriteLine($"  model {model.File}");
                if (rec is IActivatorGetter act) Console.WriteLine($"  water type {act.WaterType.FormKeyNullable}");
                if (rec is IWaterGetter w) Console.WriteLine($"  water: flags {w.Flags}, opacity {w.Opacity}, material {w.Material.FormKeyNullable}");
                return 0;
            }
            case "sk-sizes":
            {
                // Dev aid: the object bounds (width x depth x height) of Skyrim.esm's doors, statics, furniture, containers
                // and activators whose EditorID matches --match (regex), for comparing other games' scale.
                using var sk = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts["match"], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var found = sk.Doors.Select(r => (r.EditorID, r.ObjectBounds)).Concat(sk.Statics.Select(r => (r.EditorID, r.ObjectBounds)))
                    .Concat(sk.Furniture.Select(r => (r.EditorID, r.ObjectBounds))).Concat(sk.Containers.Select(r => (r.EditorID, r.ObjectBounds)))
                    .Concat(sk.Activators.Select(r => (r.EditorID, r.ObjectBounds)));
                foreach (var (id, b) in found.Where(f => f.EditorID is { } e && rx.IsMatch(e)).Take(int.Parse(opts.GetValueOrDefault("max", "40"))))
                    Console.WriteLine($"{id,-36} {b.Second.X - b.First.X,6} x {b.Second.Y - b.First.Y,6} x {b.Second.Z - b.First.Z,6}");
                return 0;
            }
            case "rg-statics":
            {
                // Dev aid: a scene's (--scene, default ISLAND) placed models (statics and objects) by model: count, placed
                // footprint (x by z, scene units) and height, largest first, for telling buildings from scenery.
                string rg = GameInstalls.DataDir(Game.Redguard)!;
                string name = opts.GetValueOrDefault("scene", "ISLAND");
                var scene = Redguard.RgScene.Load(File.ReadAllBytes(Path.Combine(rg, "maps", name + ".RGM")))!;
                var rob = Redguard.RgRob.Load(File.ReadAllBytes(Path.Combine(rg, "3dart", name + ".ROB")))!;
                var models = new Dictionary<string, Redguard.RgModel?>(StringComparer.OrdinalIgnoreCase);
                Redguard.RgModel? Model(string m)
                {
                    m = m.Split('.')[0];
                    if (models.TryGetValue(m, out var hit)) return hit;
                    var seg = rob.Segments.FirstOrDefault(sg => sg.Name.Equals(m, StringComparison.OrdinalIgnoreCase) && sg.Type != 512);
                    return models[m] = seg is null ? null : Redguard.RgModel.Load(rob.Data, seg.DataOffset);
                }
                var rows = new Dictionary<string, (int Count, float W, float D, float H, bool Static)>(StringComparer.OrdinalIgnoreCase);
                foreach (var o in scene.Statics.Concat(scene.Objects))
                {
                    if (Model(o.Model) is not { } model || model.Vertices.Count == 0) continue;
                    var pts = model.Vertices.Select(v => System.Numerics.Vector3.Transform(v, o.Rotation)).ToList();
                    var mn = pts.Aggregate(System.Numerics.Vector3.Min); var mx = pts.Aggregate(System.Numerics.Vector3.Max);
                    string key = o.Model.Split('.')[0];
                    rows.TryGetValue(key, out var r);
                    rows[key] = (r.Count + 1, MathF.Max(r.W, mx.X - mn.X), MathF.Max(r.D, mx.Z - mn.Z), MathF.Max(r.H, mx.Y - mn.Y), o.IsStatic);
                }
                foreach (var (m, r) in rows.OrderByDescending(kv => MathF.Max(kv.Value.W, kv.Value.D)))
                    Console.WriteLine($"{m,-10} x{r.Count,-4} {r.W,6:F0} x {r.D,6:F0}  h {r.H,5:F0}");
                return 0;
            }
            case "rg-sizes":
            {
                // Dev aid: the size (width x height x depth, height = Redguard's -y) of every model in a ROB (--rob, default
                // ISLAND) whose name matches --match (regex), for measuring the game's scale (doors, steps, houses).
                string art = Path.Combine(GameInstalls.DataDir(Game.Redguard)!, "3dart");
                var rob = Redguard.RgRob.Load(File.ReadAllBytes(Path.Combine(art, opts.GetValueOrDefault("rob", "ISLAND") + ".ROB")))!;
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("match", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var seg in rob.Segments.Where(sg => sg.Type != 512 && rx.IsMatch(sg.Name)))
                {
                    var model = Redguard.RgModel.Load(rob.Data, seg.DataOffset);
                    if (model is null || model.Vertices.Count == 0) continue;
                    var mn = model.Vertices.Aggregate(System.Numerics.Vector3.Min); var mx = model.Vertices.Aggregate(System.Numerics.Vector3.Max);
                    Console.WriteLine($"{seg.Name,-10} {mx.X - mn.X,7:F0} x {mx.Y - mn.Y,7:F0} x {mx.Z - mn.Z,7:F0}");
                }
                return 0;
            }
            case "rg-model":
            {
                // Dev aid: a Redguard model's (--name, loose .3D/.3DC or ROB segment) counts, bounds and textures.
                string art = Path.Combine(GameInstalls.DataDir(Game.Redguard)!, "3dart");
                foreach (string name in opts["name"].Split(','))
                {
                    Redguard.RgModel? model = null; string from = "";
                    foreach (var ext in new[] { ".3D", ".3DC" })
                        if (File.Exists(Path.Combine(art, name + ext))) { model = Redguard.RgModel.Load(File.ReadAllBytes(Path.Combine(art, name + ext))); from = name + ext; break; }
                    if (model is null)
                        foreach (var f in Directory.GetFiles(art, "*.ROB"))
                        {
                            var rob = Redguard.RgRob.Load(File.ReadAllBytes(f));
                            var seg = rob?.Segments.FirstOrDefault(sg => sg.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                            if (seg is null) continue;
                            from = $"{Path.GetFileName(f)} type {seg.Type}";
                            if (seg.Type != 512) model = Redguard.RgModel.Load(rob!.Data, seg.DataOffset);
                            break;
                        }
                    if (model is null) { Console.WriteLine($"{name}: not found ({from})"); continue; }
                    var mn = model.Vertices.Aggregate(System.Numerics.Vector3.Min); var mx = model.Vertices.Aggregate(System.Numerics.Vector3.Max);
                    Console.WriteLine($"{name} ({from}, {model.Version}): {model.Vertices.Count} vertices, {model.Faces.Count} faces, bounds {mn}..{mx}");
                    Console.WriteLine($"   textures: {string.Join(" ", model.Faces.GroupBy(f => f.Solid ? $"c{f.ColorIndex}" : $"{f.TextureBank}/{f.ImageId}").OrderByDescending(g => g.Count()).Take(12).Select(g => $"{g.Key}x{g.Count()}"))}");
                    if (opts.ContainsKey("faces"))
                        foreach (var f in model.Faces.Take(int.Parse(opts["faces"])))
                            Console.WriteLine($"   {(f.Solid ? $"c{f.ColorIndex}" : $"{f.TextureBank}/{f.ImageId}")}: {string.Join("  ", f.Vertices.Select((v, i) => $"{v}:({f.Uv[i].X:F1},{f.Uv[i].Y:F1})"))}");
                }
                return 0;
            }
            case "rg-map":
            {
                // Dev aid: a Redguard world (--world, default ISLAND) as a PNG (--out): terrain tile colours shaded by height,
                // water blue, with the scene's statics (red) and objects (yellow) on top; +z is up the image.
                string rg = GameInstalls.DataDir(Game.Redguard)!;
                string world = opts.GetValueOrDefault("world", "ISLAND");
                var w = Redguard.RgWorld.Load(File.ReadAllBytes(Path.Combine(rg, "maps", world + ".WLD")))!;
                var scene = Redguard.RgScene.Load(File.ReadAllBytes(Path.Combine(rg, "maps", world + ".RGM")))!;
                var pal = Redguard.RgPalette.Load(File.ReadAllBytes(Path.Combine(rg, "3dart", opts.GetValueOrDefault("palette", "ISLAND") + ".COL")));
                var tiles = Redguard.RgTextureBank.Load(File.ReadAllBytes(Path.Combine(rg, "3dart", "TEXTURE.302")))!;
                const int px = 2, n = Redguard.RgWorld.Size * px;
                var rgb = new byte[n * n * 3];
                void Put(int x, int y, uint c) { if (x < 0 || y < 0 || x >= n || y >= n) return; int o = ((n - 1 - y) * n + x) * 3; rgb[o] = (byte)(c >> 16); rgb[o + 1] = (byte)(c >> 8); rgb[o + 2] = (byte)c; }
                for (int z = 0; z < Redguard.RgWorld.Size; z++)
                    for (int x = 0; x < Redguard.RgWorld.Size; x++)
                    {
                        int tile = w.Texture[x, z] & 0x3F;
                        float h = w.HeightAt(x, z);
                        uint c = tiles.Images.TryGetValue(tile, out var img) ? pal.Colors[img.AverageColor] : 0x808080;
                        if (tile is 0 or 5 or 30 or 31) c = 0x2050A0;
                        float shade = 0.5f + h / 3560f * 0.5f + (w.HeightAt(x - 1, z) - h) / 400f;
                        uint Sh(uint ch) => (uint)Math.Clamp(ch * shade, 0, 255);
                        c = Sh(c >> 16 & 0xFF) << 16 | Sh(c >> 8 & 0xFF) << 8 | Sh(c & 0xFF);
                        for (int dy = 0; dy < px; dy++) for (int dx = 0; dx < px; dx++) Put(x * px + dx, z * px + dy, c);
                    }
                foreach (var (set, color) in new[] { (scene.Statics, 0xFF2020u), (scene.Objects, 0xFFFF00u) })
                    foreach (var o in set) Put((int)(o.Position.X / Redguard.RgWorld.Cell * px), (int)((opts.ContainsKey("flip") ? Redguard.RgWorld.Size * Redguard.RgWorld.Cell - o.Position.Z : o.Position.Z) / Redguard.RgWorld.Cell * px), color);
                Util.Png.WriteRgb(opts["out"], n, n, rgb);
                Console.WriteLine($"wrote {opts["out"]}");
                return 0;
            }
            case "rg-doors":
            {
                // Dev aid: every LoadWorld call in Redguard's scenes: the actor making it, where that actor stands, and
                // the world and marker it loads (WORLD.INI world_map index).
                string rg = GameInstalls.DataDir(Game.Redguard)!;
                var fn = Redguard.RgScript.FunctionNames(Path.Combine(rg, "soup386", "SOUP386.DEF"));
                int loadWorld = fn.IndexOf("LoadWorld");
                Console.WriteLine($"{fn.Count} functions, LoadWorld = {loadWorld}");
                var ini = Redguard.RgWorldIni.Load(Path.Combine(rg, "WORLD.INI"));
                Console.WriteLine($"start {ini.StartWorld}/{ini.StartMarker}; maps: {string.Join(" ", ini.Maps.Values.OrderBy(m => m.Id).Select(m => $"{m.Id}={m.Scene}{(m.World is null ? "" : "+" + m.World)}/{m.Palette}"))}");
                foreach (var f in Directory.GetFiles(Path.Combine(rg, "maps"), "*.RGM").Order())
                {
                    var bytes = File.ReadAllBytes(f);
                    var scene = Redguard.RgScene.Load(bytes)!;
                    var script = Redguard.RgScript.Load(bytes, scene);
                    var loads = script.Calls.Where(c => c.Function == loadWorld).ToList();
                    Console.WriteLine($"{Path.GetFileName(f)}: {script.Actors.Count} actors, {script.Calls.Count} calls, {loads.Count} LoadWorld");
                    if (opts.ContainsKey("markers")) Console.WriteLine($"   markers: {string.Join(" ", scene.Markers.Select((m, i) => $"{i}:{m}"))}");
                    foreach (var g in loads.GroupBy(c => c.Actor))
                    {
                        var at = scene.Objects.Where(o => o.Script.Equals(g.Key, StringComparison.OrdinalIgnoreCase)).Select(o => o.Position.ToString()).Take(2);
                        Console.WriteLine($"   {g.Key,-9} @ {string.Join(" ", at)}: {string.Join("; ", g.Select(c => string.Join(",", c.Args.Select(x => x?.ToString() ?? "?"))).Distinct())}");
                    }
                }
                return 0;
            }
            case "rg-footprint":
            {
                // Dev aid: a top-down render of a Redguard scene's statics (their models' triangles, placed) over a window
                // (--x0 --z0 --size, scene units; +z up the image), with the MPSO matrix used as stored (--variant 0)
                // or transposed (1), to see which joins the buildings up.
                string rg = GameInstalls.DataDir(Game.Redguard)!;
                string name = opts.GetValueOrDefault("scene", "ISLAND");
                var scene = Redguard.RgScene.Load(File.ReadAllBytes(Path.Combine(rg, "maps", name + ".RGM")))!;
                var rob = Redguard.RgRob.Load(File.ReadAllBytes(Path.Combine(rg, "3dart", name + ".ROB")))!;
                var models = new Dictionary<string, Redguard.RgModel?>(StringComparer.OrdinalIgnoreCase);
                Redguard.RgModel? Model(string m)
                {
                    m = m.Split('.')[0];
                    if (models.TryGetValue(m, out var hit)) return hit;
                    var seg = rob.Segments.FirstOrDefault(sg => sg.Name.Equals(m, StringComparison.OrdinalIgnoreCase) && sg.Type != 512);
                    return models[m] = seg is null ? null : Redguard.RgModel.Load(rob.Data, seg.DataOffset);
                }
                float x0 = float.Parse(opts.GetValueOrDefault("x0", "36000")), z0 = float.Parse(opts.GetValueOrDefault("z0", "36000")), size = float.Parse(opts.GetValueOrDefault("size", "12000"));
                int variant = int.Parse(opts.GetValueOrDefault("variant", "0"));
                const int n = 700;
                var img = new byte[n * n * 3];
                Array.Fill(img, (byte)235);
                float k = n / size;
                var shade = new float[n * n];
                foreach (var o in scene.Statics)
                {
                    if (Model(o.Model) is not { } model) continue;
                    var r = o.Rotation;   // rows (m0, m3, m6) ... as Matrix4x4
                    System.Numerics.Vector3 W(System.Numerics.Vector3 v) => variant == 0
                        ? new(r.M11 * v.X + r.M12 * v.Y + r.M13 * v.Z, r.M21 * v.X + r.M22 * v.Y + r.M23 * v.Z, r.M31 * v.X + r.M32 * v.Y + r.M33 * v.Z)
                        : new(r.M11 * v.X + r.M21 * v.Y + r.M31 * v.Z, r.M12 * v.X + r.M22 * v.Y + r.M32 * v.Z, r.M13 * v.X + r.M23 * v.Y + r.M33 * v.Z);
                    foreach (var f in model.Faces)
                        for (int t = 1; t + 1 < f.Vertices.Length; t++)
                        {
                            var pts = new[] { f.Vertices[0], f.Vertices[t], f.Vertices[t + 1] }.Select(i => W(model.Vertices[i]) + o.Position).ToArray();
                            var q = pts.Select(pp => ((pp.X - x0) * k, n - 1 - (pp.Z - z0) * k)).ToArray();
                            int minX = (int)Math.Max(0, q.Min(z => z.Item1)), maxX = (int)Math.Min(n - 1, q.Max(z => z.Item1));
                            int minY = (int)Math.Max(0, q.Min(z => z.Item2)), maxY = (int)Math.Min(n - 1, q.Max(z => z.Item2));
                            if (maxX - minX > n / 2 || maxY - minY > n / 2) continue;
                            float height = -pts.Average(pp => pp.Y);
                            for (int y = minY; y <= maxY; y++)
                                for (int x = minX; x <= maxX; x++)
                                {
                                    float d = (q[1].Item1 - q[0].Item1) * (q[2].Item2 - q[0].Item2) - (q[1].Item2 - q[0].Item2) * (q[2].Item1 - q[0].Item1);
                                    if (MathF.Abs(d) < 1e-4f) continue;
                                    float w1 = ((x - q[0].Item1) * (q[2].Item2 - q[0].Item2) - (y - q[0].Item2) * (q[2].Item1 - q[0].Item1)) / d;
                                    float w2 = ((q[1].Item1 - q[0].Item1) * (y - q[0].Item2) - (q[1].Item2 - q[0].Item2) * (x - q[0].Item1)) / d;
                                    if (w1 < 0 || w2 < 0 || w1 + w2 > 1) continue;
                                    shade[y * n + x] = MathF.Max(shade[y * n + x], 1 + height);
                                }
                        }
                }
                float top = shade.Max();
                for (int i = 0; i < n * n; i++)
                    if (shade[i] > 0) { byte v = (byte)(40 + 160 * (shade[i] / top)); img[i * 3] = v; img[i * 3 + 1] = (byte)(v / 2); img[i * 3 + 2] = (byte)(v / 3); }
                Util.Png.WriteRgb(opts["out"], n, n, img);
                Console.WriteLine($"wrote {opts["out"]}");
                return 0;
            }
            case "rg-winding":
            {
                // Dev aid: the signed volume of each closed-looking model in a ROB (--scene): positive when faces wind
                // counter-clockwise seen from outside (y down as stored).
                string rg = GameInstalls.DataDir(Game.Redguard)!;
                var rob = Redguard.RgRob.Load(File.ReadAllBytes(Path.Combine(rg, "3dart", opts.GetValueOrDefault("scene", "ISLAND") + ".ROB")))!;
                int pos = 0, neg = 0;
                foreach (var seg in rob.Segments.Where(sg => sg.Type != 512))
                {
                    if (Redguard.RgModel.Load(rob.Data, seg.DataOffset) is not { } m) continue;
                    double vol = 0, area = 0;
                    foreach (var f in m.Faces)
                        for (int t = 1; t + 1 < f.Vertices.Length; t++)
                        {
                            var (a0, b0, c0) = (m.Vertices[f.Vertices[0]], m.Vertices[f.Vertices[t]], m.Vertices[f.Vertices[t + 1]]);
                            vol += System.Numerics.Vector3.Dot(a0, System.Numerics.Vector3.Cross(b0, c0)) / 6;
                            area += System.Numerics.Vector3.Cross(b0 - a0, c0 - a0).Length() / 2;
                        }
                    if (Math.Abs(vol) < 1) continue;
                    if (vol > 0) pos++; else neg++;
                    if (pos + neg <= 12) Console.WriteLine($"{seg.Name,-9} volume {vol,12:F0} area {area,10:F0}");
                }
                Console.WriteLine($"positive {pos}, negative {neg}");
                return 0;
            }
            case "rg-build":
            {
                // Redguard.esm: Stros M'Kai and Redguard's other maps from the Steam/GOG install, into --out (default out/rgmod).
                string rg = GameInstalls.DataDir(Game.Redguard) ?? throw new DirectoryNotFoundException("Redguard isn't installed through Steam or GOG");
                string outDir = opts.GetValueOrDefault("out", "out/rgmod");
                if (Directory.Exists("tools/upscale/out-rg") && !opts.ContainsKey("no-upscale")) Redguard.RgArt.UpscaleCache = Path.GetFullPath("tools/upscale/out-rg");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var builder = new Redguard.RgPluginBuilder(rg, outDir, skyrimData);
                builder.Build(msg => Console.WriteLine($"  {msg} ({sw.Elapsed.TotalSeconds:F0}s)"));
                foreach (var p in builder.Problems.Take(40)) Console.WriteLine($"  problem: {p}");
                if (builder.Problems.Count > 40) Console.WriteLine($"  ... {builder.Problems.Count - 40} more problems");
                Console.WriteLine($"  terrain LOD chunks: {builder.TerrainLodChunks}; Skyrim water surfaces: {builder.WaterSurfaces}; ships: {builder.Ships}");
                Console.WriteLine($"  records: {builder.Mod.Statics.Count} statics, {builder.Mod.Lights.Count} lights, {builder.Mod.Worldspaces.Count} worldspaces, {builder.Mod.Cells.Records.Sum(b => b.SubBlocks.Sum(sb => sb.Cells.Count))} interiors, {builder.Mod.LandscapeTextures.Count} land textures");
                builder.Write();
                Console.WriteLine($"Wrote {Path.GetFullPath(Path.Combine(outDir, Redguard.RgPluginBuilder.PluginName))} in {sw.Elapsed.TotalSeconds:F0}s");
                return 0;
            }
            case "rg-euler-test":
            {
                // Dev aid: RgSpace.Euler recomposes to the same rotation (random rotations, max element error).
                var rnd = new Random(1);
                float worst = 0;
                for (int i = 0; i < 2000; i++)
                {
                    var q = System.Numerics.Quaternion.Normalize(new((float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f, (float)rnd.NextDouble() - 0.5f));
                    if (i % 4 == 0) q = System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY, (float)rnd.NextDouble() * 6.28f);
                    var rot = System.Numerics.Matrix4x4.CreateFromQuaternion(q);
                    var e = Redguard.RgSpace.Euler(rot);
                    var sk = Redguard.RgSpace.SkyrimMatrix(e);
                    foreach (var v in new[] { System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ, new System.Numerics.Vector3(0.3f, -0.7f, 0.2f) })
                    {
                        var want = Redguard.RgSpace.Dir(System.Numerics.Vector3.Transform(v, rot));
                        var got = System.Numerics.Vector3.Transform(Redguard.RgSpace.Dir(v), sk);
                        worst = MathF.Max(worst, (want - got).Length());
                    }
                }
                Console.WriteLine($"worst error {worst:G3}");
                return 0;
            }
            case "rg-render":
            {
                // Dev aid: a Redguard model (--name, from --scene's ROB) drawn textured with the converter's UV mapping,
                // orthographic from the front (-z, Skyrim's south; --side for +x), z-buffered, to --out.
                string rg = GameInstalls.DataDir(Game.Redguard)!;
                string art = Path.Combine(rg, "3dart");
                var rob = Redguard.RgRob.Load(File.ReadAllBytes(Path.Combine(art, opts.GetValueOrDefault("scene", "ISLAND") + ".ROB")))!;
                var seg = rob.Segments.First(sg => sg.Name.Equals(opts["name"], StringComparison.OrdinalIgnoreCase) && sg.Type != 512);
                var model = Redguard.RgModel.Load(rob.Data, seg.DataOffset)!;
                var pal = Redguard.RgPalette.Load(File.ReadAllBytes(Directory.GetFiles(art, opts.GetValueOrDefault("palette", "ISLAND") + ".col", new EnumerationOptions { MatchCasing = MatchCasing.CaseInsensitive })[0]));
                var banks = new Dictionary<int, Redguard.RgTextureBank?>();
                bool side = opts.ContainsKey("side");
                // View: screen x = model x (or z from the side), screen y = model y (down), depth = model z.
                System.Numerics.Vector3 View(System.Numerics.Vector3 v) => side ? new(-v.Z, v.Y, v.X) : new(v.X, v.Y, v.Z);
                var vs = model.Vertices.Select(View).ToList();
                float minX = vs.Min(v => v.X), maxX = vs.Max(v => v.X), minY = vs.Min(v => v.Y), maxY = vs.Max(v => v.Y);
                const int n = 600;
                float k = (n - 20) / MathF.Max(maxX - minX, maxY - minY);
                var img = new byte[n * n * 3]; Array.Fill(img, (byte)90);
                var depth = new float[n * n]; Array.Fill(depth, float.MaxValue);
                foreach (var f in model.Faces)
                {
                    Redguard.RgTextureBank.Image? tex = null;
                    if (!f.Solid)
                    {
                        if (!banks.TryGetValue(f.TextureBank, out var bank)) banks[f.TextureBank] = bank = File.Exists(Path.Combine(art, $"TEXTURE.{f.TextureBank:D3}")) ? Redguard.RgTextureBank.Load(File.ReadAllBytes(Path.Combine(art, $"TEXTURE.{f.TextureBank:D3}"))) : null;
                        bank?.Images.TryGetValue(f.ImageId, out tex);
                    }
                    for (int t = 1; t + 1 < f.Vertices.Length; t++)
                    {
                        int[] idx = [0, t, t + 1];
                        var p = idx.Select(i => vs[f.Vertices[i]]).Select(v => new System.Numerics.Vector3(10 + (v.X - minX) * k, 10 + (v.Y - minY) * k, v.Z)).ToArray();
                        var uv = idx.Select(i => tex is null ? System.Numerics.Vector2.Zero : new System.Numerics.Vector2(f.Uv[i].X / tex.Width, f.Uv[i].Y / tex.Height)).ToArray();
                        float d = (p[1].X - p[0].X) * (p[2].Y - p[0].Y) - (p[1].Y - p[0].Y) * (p[2].X - p[0].X);
                        if (MathF.Abs(d) < 1e-3f) continue;
                        for (int y = (int)Math.Max(0, p.Min(q => q.Y)); y <= (int)Math.Min(n - 1, p.Max(q => q.Y)); y++)
                            for (int x = (int)Math.Max(0, p.Min(q => q.X)); x <= (int)Math.Min(n - 1, p.Max(q => q.X)); x++)
                            {
                                float w1 = ((x - p[0].X) * (p[2].Y - p[0].Y) - (y - p[0].Y) * (p[2].X - p[0].X)) / d;
                                float w2 = ((p[1].X - p[0].X) * (y - p[0].Y) - (p[1].Y - p[0].Y) * (x - p[0].X)) / d;
                                float w0 = 1 - w1 - w2;
                                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                                float z = w0 * p[0].Z + w1 * p[1].Z + w2 * p[2].Z;
                                if (z >= depth[y * n + x]) continue;
                                uint c;
                                if (tex is null) c = pal.Colors[f.Solid ? f.ColorIndex & 255 : 0];
                                else
                                {
                                    var u = uv[0] * w0 + uv[1] * w1 + uv[2] * w2;
                                    int tx = ((int)MathF.Floor(u.X * tex.Width) % tex.Width + tex.Width) % tex.Width, ty = ((int)MathF.Floor(u.Y * tex.Height) % tex.Height + tex.Height) % tex.Height;
                                    byte pi = tex.Pixels[ty * tex.Width + tx];
                                    if (pi == 0) continue;
                                    c = pal.Colors[pi];
                                }
                                depth[y * n + x] = z;
                                img[(y * n + x) * 3] = (byte)(c >> 16); img[(y * n + x) * 3 + 1] = (byte)(c >> 8); img[(y * n + x) * 3 + 2] = (byte)c;
                            }
                    }
                }
                Util.Png.WriteRgb(opts["out"], n, n, img);
                Console.WriteLine($"wrote {opts["out"]}");
                return 0;
            }
            case "rg-uvtest":
            {
                // Dev aid: which reading of the stored face U/V values fits Redguard's models: on faces of 4+ vertices the UVs
                // must be an affine function of position (residual, texels), and faces sharing a vertex and texture mostly
                // agree on its UV.
                string rg = GameInstalls.DataDir(Game.Redguard)!;
                var rob = Redguard.RgRob.Load(File.ReadAllBytes(Path.Combine(rg, "3dart", opts.GetValueOrDefault("scene", "ISLAND") + ".ROB")))!;
                var all = rob.Segments.Where(sg => sg.Type != 512).Select(sg => Redguard.RgModel.Load(rob.Data, sg.DataOffset)).OfType<Redguard.RgModel>().ToList();
                var readings = new (string Name, Func<System.Numerics.Vector2[], System.Numerics.Vector2[]> Uv)[]
                {
                    ("cumulative", r => { var o = new System.Numerics.Vector2[r.Length]; var acc = System.Numerics.Vector2.Zero; for (int i = 0; i < r.Length; i++) { acc += r[i]; o[i] = acc; } return o; }),
                    ("cumulative, last absolute", r => { var o = new System.Numerics.Vector2[r.Length]; var acc = System.Numerics.Vector2.Zero; for (int i = 0; i < r.Length; i++) { acc += r[i]; o[i] = i == r.Length - 1 && i > 0 ? r[i] : acc; } return o; }),
                    ("absolute", r => r.ToArray()),
                    ("relative to first", r => r.Select((x, i) => i == 0 ? x : r[0] + x).ToArray()),
                };
                foreach (var (rname, read) in readings)
                {
                    double residual = 0; int quads = 0, agree = 0, shared = 0, good4 = 0, all4 = 0, goodMore = 0, allMore = 0, shown = 0;
                    foreach (var m in all)
                    {
                        var seen = new Dictionary<(int Vertex, int Bank, int Image), System.Numerics.Vector2>();
                        foreach (var f in m.Faces.Where(f => !f.Solid && f.Raw.Length >= 3))
                        {
                            var uv = read(f.Raw);
                            if (f.Raw.Length >= 4)
                            {
                                // Affine map from the first three vertices (in the face's plane) predicts the fourth.
                                var p0 = m.Vertices[f.Vertices[0]]; var e1 = m.Vertices[f.Vertices[1]] - p0; var e2 = m.Vertices[f.Vertices[2]] - p0;
                                var q = m.Vertices[f.Vertices[3]] - p0;
                                float d11 = System.Numerics.Vector3.Dot(e1, e1), d12 = System.Numerics.Vector3.Dot(e1, e2), d22 = System.Numerics.Vector3.Dot(e2, e2);
                                float det = d11 * d22 - d12 * d12;
                                if (MathF.Abs(det) > 1e-3f)
                                {
                                    float q1 = System.Numerics.Vector3.Dot(q, e1), q2 = System.Numerics.Vector3.Dot(q, e2);
                                    float a = (q1 * d22 - q2 * d12) / det, b = (q2 * d11 - q1 * d12) / det;
                                    var predicted = uv[0] + a * (uv[1] - uv[0]) + b * (uv[2] - uv[0]);
                                    float err = (predicted - uv[3]).Length();
                                    residual += Math.Min(100, err); quads++;
                                    if (f.Raw.Length == 4) { if (err < 1) good4++; all4++; } else { if (err < 1) goodMore++; allMore++; }
                                    if (rname.Contains("last") && err > 5 && shown++ < 6)
                                        Console.WriteLine($"   n={f.Raw.Length} err {err:F1}: raw {string.Join(" ", f.Raw.Select(r => $"({r.X:F1},{r.Y:F1})"))}  pos {string.Join(" ", f.Vertices.Select(v => $"({m.Vertices[v].X:F0},{m.Vertices[v].Y:F0},{m.Vertices[v].Z:F0})"))}");
                                }
                            }
                            for (int i = 0; i < f.Vertices.Length; i++)
                            {
                                var key = (f.Vertices[i], f.TextureBank, f.ImageId);
                                if (seen.TryGetValue(key, out var prev)) { shared++; if ((prev - uv[i]).Length() < 0.6f) agree++; }
                                else seen[key] = uv[i];
                            }
                        }
                    }
                    Console.WriteLine($"{rname,-28} quad residual {residual / Math.Max(1, quads):F2} texels over {quads} quads; within a texel: {100.0 * good4 / Math.Max(1, all4):F1}% of {all4} quads, {100.0 * goodMore / Math.Max(1, allMore):F1}% of {allMore} larger");
                }
                return 0;
            }
            case "rg-calls":
            {
                // Dev aid: an actor's script calls (--scene, --actor), with 4-character arguments shown as text.
                string rg = GameInstalls.DataDir(Game.Redguard)!;
                var fn = Redguard.RgScript.FunctionNames(Path.Combine(rg, "soup386", "SOUP386.DEF"));
                var bytes = File.ReadAllBytes(Path.Combine(rg, "maps", opts.GetValueOrDefault("scene", "ISLAND") + ".RGM"));
                var script = Redguard.RgScript.Load(bytes, Redguard.RgScene.Load(bytes)!);
                string Arg(int? a)
                {
                    if (a is not int v) return "?";
                    var t = BitConverter.GetBytes(v);
                    return t.All(c => c >= 32 && c < 127) ? $"\"{System.Text.Encoding.ASCII.GetString(t)}\"" : v.ToString();
                }
                var rtx = Redguard.RgRtx.Load(Path.Combine(rg, "ENGLISH.RTX"));
                Console.WriteLine($"RTX: {rtx?.Entries.Count} entries, {rtx?.Entries.Values.Count(e => e.AudioLength > 0)} voiced");
                foreach (var c in script.Calls.Where(c => c.Actor.Equals(opts["actor"], StringComparison.OrdinalIgnoreCase)).Take(int.Parse(opts.GetValueOrDefault("max", "80"))))
                {
                    string line = $"{fn.ElementAtOrDefault(c.Function) ?? c.Function.ToString()}({string.Join(", ", c.Args.Select(Arg))})";
                    if (c.Args.Length > 0 && c.Args[0] is int t && rtx is not null && rtx.Entries.TryGetValue(System.Text.Encoding.Latin1.GetString(BitConverter.GetBytes(t)), out var e))
                        line += $"   \"{e.Text}\"{(e.AudioLength > 0 ? $" [{e.AudioLength * 8 / e.Bits / Math.Max(1, e.SampleRate)}s voice]" : "")}";
                    Console.WriteLine(line);
                }
                return 0;
            }
            case "rg-script":
            {
                // Dev aid: an actor's script (--scene, --actor) as readable code: function and flag names, dialogue text.
                string rg = GameInstalls.DataDir(Game.Redguard)!;
                string def = Path.Combine(rg, "soup386", "SOUP386.DEF");
                var fn = Redguard.RgScript.FunctionNames(def);
                var flags = Redguard.RgScript.FlagNames(def);
                var rtx = Redguard.RgRtx.Load(Path.Combine(rg, "ENGLISH.RTX"));
                // --dump DIR: every scene's every program, as DIR\SCENE\ACTOR.txt.
                string? dumpDir = opts.GetValueOrDefault("dump");
                foreach (var sceneFile in dumpDir is null ? [Path.Combine(rg, "maps", opts.GetValueOrDefault("scene", "ISLAND") + ".RGM")] : Directory.GetFiles(Path.Combine(rg, "maps"), "*.RGM").Order().ToArray())
                {
                var bytes = File.ReadAllBytes(sceneFile);
                var sceneData = Redguard.RgScene.Load(bytes)!;
                var script = Redguard.RgScript.Load(bytes, sceneData);
                if (dumpDir is not null)
                {
                    string dir = Path.Combine(dumpDir, Path.GetFileNameWithoutExtension(sceneFile).ToUpperInvariant());
                    Directory.CreateDirectory(dir);
                    File.WriteAllLines(Path.Combine(dir, "_objects.txt"), sceneData.Objects.Select((o, i) => $"{i} {sceneData.ObjectTypes[i]} {o.Script,-10} {o.Model,-10} at {o.Position}")
                        .Concat(sceneData.Markers.Select((m, i) => $"marker {i} at {m}")));
                    File.WriteAllLines(Path.Combine(dir, "_attrs.txt"), script.Attributes.Select(kv => $"{kv.Key,-10} {string.Join(" ", kv.Value.Select((v, i) => (v, i)).Where(t => t.v != 0).Select(t => $"{t.i}={t.v}"))}"));
                }
                foreach (var actorName in dumpDir is null ? [opts.GetValueOrDefault("actor", "")] : script.Programs.Keys.ToArray())
                {
                if (dumpDir is null && !opts.ContainsKey("actor"))
                {
                    // No actor named: the scene's objects (type, script, model) to pick from.
                    for (int i = 0; i < sceneData.Objects.Count; i++)
                        Console.WriteLine($"{sceneData.ObjectTypes[i]} {sceneData.Objects[i].Script,-10} {sceneData.Objects[i].Model,-10} at {sceneData.Objects[i].Position}");
                    return 0;
                }
                var strings = script.Strings.GetValueOrDefault(actorName) ?? [];
                string Tag(int v) { var t = BitConverter.GetBytes(v); return t.All(c => c >= 32 && c < 127) ? System.Text.Encoding.Latin1.GetString(t) : ""; }
                string V(Redguard.RgScript.Value v) => v switch
                {
                    Redguard.RgScript.Num n when Tag(n.V) is { Length: 4 } t && rtx?.Entries.ContainsKey(t) == true => $"\"{t}\"",
                    Redguard.RgScript.Num n => n.V.ToString(),
                    Redguard.RgScript.Flag f => f.Index < flags.Count ? flags[f.Index].Name : $"flag{f.Index}",
                    Redguard.RgScript.Local l => $"var{l.Index}",
                    Redguard.RgScript.FnValue f => $"{fn.ElementAtOrDefault(f.Function)}({string.Join(", ", f.Args.Select(V))})",
                    Redguard.RgScript.Other o when o.What.StartsWith("string") && int.TryParse(o.What[6..], out int si) && si < strings.Count => $"'{strings[si]}'",
                    Redguard.RgScript.Other o => o.What,
                    _ => "?",
                };
                string[] cmps = ["=", "!=", "<", ">", "<=", ">="];
                string[] ops = ["", "+", "-", "/", "*", "<<", ">>", "&", "|", "^", "++", "--"];
                var lines = new List<string>();
                void Print(List<Redguard.RgScript.Stmt> body, int depth)
                {
                    string ind = new(' ', depth * 2);
                    foreach (var st in body)
                    {
                        string head = $"{st.Address,5}: {ind}";
                        switch (st)
                        {
                            case Redguard.RgScript.CallStmt c:
                                string text = c.Args.Length > 0 && c.Args[0] is Redguard.RgScript.Num n && rtx?.Entries.TryGetValue(Tag(n.V), out var e) == true ? $"    // {e.Text}" : "";
                                lines.Add($"{head}{fn.ElementAtOrDefault(c.Function)}({string.Join(", ", c.Args.Select(V))}){text}"); break;
                            case Redguard.RgScript.IfStmt i:
                                lines.Add($"{head}if {string.Join(" ", i.Conditions.Select(k => $"{V(k.Lhs)} {cmps.ElementAtOrDefault(k.Cmp)} {V(k.Rhs)}{(k.Conj == 1 ? " and" : k.Conj == 2 ? " or" : "")}"))}");
                                Print(i.Body, depth + 1); break;
                            case Redguard.RgScript.AssignStmt a2:
                                lines.Add($"{head}{V(a2.Target)} = {string.Join(" ", a2.Formula.Select(f => V(f.Value) + " " + ops.ElementAtOrDefault(f.Op)))}"); break;
                            case Redguard.RgScript.JumpStmt j: lines.Add($"{head}{j.Kind} {j.Target}"); break;
                            case Redguard.RgScript.OtherStmt o: lines.Add($"{head}{o.What}"); break;
                        }
                    }
                }
                Print(script.Programs.GetValueOrDefault(actorName) ?? [], 0);
                if (dumpDir is not null)
                {
                    string dir = Path.Combine(dumpDir, Path.GetFileNameWithoutExtension(sceneFile).ToUpperInvariant());
                    Directory.CreateDirectory(dir);
                    File.WriteAllLines(Path.Combine(dir, string.Concat(actorName.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)) + ".txt"), lines);
                }
                else if (opts.TryGetValue("out", out var outFile)) File.WriteAllLines(outFile, lines); else foreach (var l in lines) Console.WriteLine(l);
                }
                }
                return 0;
            }
            case "rg-script-coverage":
            {
                // Redguard's scripts translated to Papyrus (RgPapyrus) without building the plugin: every scene's every
                // object program (as the audit counts them), what each statement became, and with --compile the
                // generated sources compiled (into --out, default out/rgscripts) against TMRgSoup. --functions lists
                // every SOUP function with its counts; --scene/--actor print one translated script.
                string rg = GameInstalls.DataDir(Game.Redguard) ?? throw new DirectoryNotFoundException("Redguard isn't installed through Steam or GOG");
                string def = Path.Combine(rg, "soup386", "SOUP386.DEF");
                var fn = Redguard.RgScript.FunctionNames(def);
                var (tasks, refs) = Redguard.RgPapyrus.DefInfo(def);
                var lib = Redguard.RgPapyrus.LoadLibrary(Path.Combine(AppContext.BaseDirectory, "Papyrus", "TMRgSoup.psc"));
                var translator = new Redguard.RgPapyrus(fn, tasks, refs, lib);
                var ini = Redguard.RgWorldIni.Load(Path.Combine(rg, "WORLD.INI"));
                var used = ini.Maps.Values.Select(m => m.Scene.ToUpperInvariant()).ToHashSet();
                string outDir = opts.GetValueOrDefault("out", "out/rgscripts");
                string srcDir = Path.Combine(outDir, "Source"), pexDir = Path.Combine(outDir, "Scripts");
                if (opts.ContainsKey("compile")) { if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true); Directory.CreateDirectory(srcDir); }
                var counts = new Dictionary<Redguard.RgPapyrus.Category, int>();
                var byFunction = new Dictionary<string, Dictionary<Redguard.RgPapyrus.Category, int>>();
                int total = 0, unusedScene = 0, programs = 0;
                var written = new Dictionary<string, string>();
                var ctx = new Redguard.RgPluginBuilder.CoverageContext(rg);
                foreach (var file in Directory.GetFiles(Path.Combine(rg, "maps"), "*.RGM").Order())
                {
                    string sceneName = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
                    var bytes = File.ReadAllBytes(file);
                    var sceneData = Redguard.RgScene.Load(bytes)!;
                    var script = Redguard.RgScript.Load(bytes, sceneData);
                    foreach (var (actor, program) in script.Programs)
                    {
                        bool talks = Redguard.RgPluginBuilder.IsTalker(script, actor, fn);
                        ctx.Actor = actor;
                        var locals = script.Locals.GetValueOrDefault(actor) ?? [];
                        var output = translator.Translate("TMRg" + new string((sceneName + "_" + actor).Where(c => char.IsLetterOrDigit(c) || c == '_').ToArray()),
                            $"Redguard {sceneName} {actor}", program, script.Entry.GetValueOrDefault(actor), locals, script.Strings.GetValueOrDefault(actor) ?? [], ctx, talks,
                            talks ? Enumerable.Range(0, locals.Length).ToHashSet() : []);
                        if (!used.Contains(sceneName)) { unusedScene += output.Statements; continue; }
                        programs++;
                        total += output.Statements;
                        foreach (var (c, n) in output.Counts) counts[c] = counts.GetValueOrDefault(c) + n;
                        foreach (var (f, d) in output.ByFunction)
                        {
                            var into = byFunction.TryGetValue(f, out var x) ? x : byFunction[f] = [];
                            foreach (var (c, n) in d) into[c] = into.GetValueOrDefault(c) + n;
                        }
                        string hash = script.Hash.GetValueOrDefault(actor, sceneName + actor) + talks;
                        if (opts.ContainsKey("compile") && !written.ContainsKey(hash)) { written[hash] = output.ScriptName; File.WriteAllText(Path.Combine(srcDir, output.ScriptName + ".psc"), output.Source); }
                        if (opts.GetValueOrDefault("scene", "").Equals(sceneName, StringComparison.OrdinalIgnoreCase) && opts.GetValueOrDefault("actor", "").Equals(actor, StringComparison.OrdinalIgnoreCase))
                            Console.WriteLine(output.Source);
                    }
                }
                Console.WriteLine($"Redguard scripts: {programs} programs in {used.Count} scenes, {total} statements (HIDEOUT.RGM, which WORLD.INI never loads: {unusedScene} more, not converted)");
                foreach (var c in Enum.GetValues<Redguard.RgPapyrus.Category>())
                    Console.WriteLine($"  {c,-12} {counts.GetValueOrDefault(c),6}  {100.0 * counts.GetValueOrDefault(c) / Math.Max(1, total),6:F2}%");
                Console.WriteLine($"  translated   {counts.Values.Sum(),6}  {100.0 * counts.Values.Sum() / Math.Max(1, total),6:F2}% (every statement is in the generated Papyrus; the categories say what it does there)");
                var functions = byFunction.OrderByDescending(kv => kv.Value.Values.Sum()).ToList();
                if (opts.ContainsKey("functions"))
                    foreach (var (f, d) in functions)
                        Console.WriteLine($"    {f,-24} {string.Join("  ", d.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key} {kv.Value}"))}");
                else
                {
                    foreach (var cat in new[] { Redguard.RgPapyrus.Category.Approximate, Redguard.RgPapyrus.Category.NoEffect })
                        Console.WriteLine($"  {cat}: {string.Join(", ", functions.Where(kv => kv.Value.ContainsKey(cat)).OrderByDescending(kv => kv.Value[cat]).Take(24).Select(kv => $"{kv.Key} {kv.Value[cat]}"))}");
                }
                if (opts.ContainsKey("compile"))
                {
                    string game = Path.GetDirectoryName(Path.GetFullPath(skyrimData.TrimEnd('\\', '/')))!;
                    string compiler = Path.Combine(game, "Papyrus Compiler", "PapyrusCompiler.exe");
                    if (!File.Exists(compiler)) { Console.WriteLine("  no Creation Kit Papyrus compiler: not compiled"); return 0; }
                    if (Directory.Exists(pexDir)) Directory.Delete(pexDir, true);
                    Directory.CreateDirectory(pexDir);
                    string shipped = Path.Combine(AppContext.BaseDirectory, "Papyrus");
                    var psi = new System.Diagnostics.ProcessStartInfo(compiler) { WorkingDirectory = Path.GetFullPath(srcDir), RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var a in new[] { Path.GetFullPath(srcDir), "-all", "-q", "-f=TESV_Papyrus_Flags.flg", $"-i={Path.GetFullPath(srcDir)};{shipped};{Path.Combine(skyrimData, "Scripts", "Source")};{Path.Combine(skyrimData, "Source", "Scripts")}", $"-o={Path.GetFullPath(pexDir)}" })
                        psi.ArgumentList.Add(a);
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    using var p = System.Diagnostics.Process.Start(psi)!;
                    var err = p.StandardError.ReadToEndAsync();
                    string log = p.StandardOutput.ReadToEnd() + err.Result;
                    p.WaitForExit();
                    int ok = written.Values.Count(n => File.Exists(Path.Combine(pexDir, n + ".pex")));
                    Console.WriteLine($"  compiled {ok} of {written.Count} distinct scripts in {sw.Elapsed.TotalSeconds:F0}s");
                    foreach (var line in log.Split('\n').Where(l => l.Contains("): ")).Take(int.Parse(opts.GetValueOrDefault("errors", "30")))) Console.WriteLine("    " + line.Trim());
                }
                return 0;
            }
            case "rg-items":
            {
                // Dev aid: Redguard's items (ITEM.INI) with their RTX names and descriptions, type, use script and models.
                string rg = GameInstalls.DataDir(Game.Redguard)!;
                var rtx = Redguard.RgRtx.Load(Path.Combine(rg, "ENGLISH.RTX"));
                string T(string tag) => tag.Length == 4 && rtx?.Entries.TryGetValue(tag, out var e) == true ? e.Text : tag;
                foreach (var d in Redguard.RgPluginBuilder.LoadItemDefs(rg))
                    Console.WriteLine($"{d.Id,3} t{d.Type} hide{d.Get("hide")} max{d.Get("player_max"),-6} {T(d.Get("name")),-28} use={d.UseScript,-9} game={d.GameModel,-22} inv={d.InventoryModel,-22} hand={d.Get("hand_object_file")}  | {T(d.Get("description"))}");
                return 0;
            }
            case "rg-dialogue-stats":
            {
                // Dev aid: per scene, the actors that hold conversations (a PlayerLineUp and a menu), their menu items and
                // the RTX lines their scripts speak (NPC: RTX/rtxAnim; Cyrus: RTXp/RTXpAnim).
                string rg = GameInstalls.DataDir(Game.Redguard)!;
                var fn = Redguard.RgScript.FunctionNames(Path.Combine(rg, "soup386", "SOUP386.DEF"));
                int Id(string n) => fn.IndexOf(n);
                int lineUp = Id("PlayerLineUp"), menuAdd = Id("menuAddItem");
                var npcFns = new[] { Id("RTX"), Id("rtxAnim") }; var pcFns = new[] { Id("RTXp"), Id("RTXpAnim") };
                var used = new HashSet<int>();
                int actors = 0, items = 0, npcLines = 0, pcLines = 0;
                foreach (var f in Directory.GetFiles(Path.Combine(rg, "maps"), "*.RGM").Order())
                {
                    var bytes = File.ReadAllBytes(f);
                    var script = Redguard.RgScript.Load(bytes, Redguard.RgScene.Load(bytes)!);
                    var talkers = script.Calls.GroupBy(c => c.Actor).Where(g => g.Any(c => c.Function == lineUp) && g.Any(c => c.Function == menuAdd)).ToList();
                    foreach (var g in talkers)
                    {
                        actors++; items += g.Count(c => c.Function == menuAdd);
                        foreach (var c in g) if (c.Args.Length > 0 && c.Args[0] is int t && used.Add(t)) { if (npcFns.Contains(c.Function)) npcLines++; else if (pcFns.Contains(c.Function)) pcLines++; }
                    }
                    if (talkers.Count > 0) Console.WriteLine($"{Path.GetFileName(f),-13} {string.Join(" ", talkers.Select(g => $"{g.Key}({g.Count(c => c.Function == menuAdd)})"))}");
                }
                Console.WriteLine($"{actors} talking actors, {items} menu items, {npcLines} NPC lines, {pcLines} Cyrus lines (distinct)");
                return 0;
            }
            case "rg-voices":
            {
                // Redguard's own recordings as the voice files of a built Redguard.esm (--plugin, voicemap next to it), under
                // --out (default out/rgvoice).
                string rg = GameInstalls.DataDir(Game.Redguard) ?? throw new DirectoryNotFoundException("Redguard isn't installed through Steam or GOG");
                string pluginPath = opts.GetValueOrDefault("plugin", "out/rgmod/Redguard.esm");
                var (packed, existing, missing) = Redguard.RgVoices.Build(skyrimData, rg, pluginPath,
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pluginPath))!, "Redguard.voicemap.json"), opts.GetValueOrDefault("out", "out/rgvoice"));
                Console.WriteLine($"{packed} voice files from Redguard's recordings ({Skyrim.VoiceFiles.CacheStats.Hits} of them from the lip sync cache), {existing} already there, {missing} lines without a recording");
                return 0;
            }
            case "df-town-render":
            {
                // Top-down render of a town (--name, --region optional) laid out like Daggerfall Unity: model polygons
                // shaded by height, building door surfaces red, flats as dots; --scale pixels per 16 Daggerfall units.
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var models = DfModel.LoadAll(arena2);
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name.Equals(opts["name"], StringComparison.OrdinalIgnoreCase)
                    && (!opts.ContainsKey("region") || l.RegionName.Equals(opts["region"], StringComparison.OrdinalIgnoreCase)) && l.ExteriorBlocks.Length > 0);
                float px = float.Parse(opts.GetValueOrDefault("scale", "1"), System.Globalization.CultureInfo.InvariantCulture) / 16f;
                int w = (int)(loc.BlockWidth * DfTownBlock.Side * px), h = (int)(loc.BlockHeight * DfTownBlock.Side * px);
                var rgb = new byte[w * h * 3];
                var zbuf = new float[w * h]; Array.Fill(zbuf, float.MinValue);
                void Plot(float x, float z, byte r, byte g, byte b, int size = 1)
                {
                    int ix = (int)(x * px), iy = h - 1 - (int)(z * px);
                    for (int dy = -size; dy <= size; dy++) for (int dx = -size; dx <= size; dx++)
                    {
                        int xx = ix + dx, yy = iy + dy;
                        if (xx < 0 || yy < 0 || xx >= w || yy >= h) continue;
                        int o = (yy * w + xx) * 3; rgb[o] = r; rgb[o + 1] = g; rgb[o + 2] = b;
                    }
                }
                int doors = 0, buildings = 0;
                for (int by = 0; by < loc.BlockHeight; by++)
                    for (int bx = 0; bx < loc.BlockWidth; bx++)
                    {
                        string? name = DfTownBlock.Resolve(bsa, loc.ExteriorBlocks[by * loc.BlockWidth + bx]);
                        if (name is null) { Console.WriteLine($"  block {loc.ExteriorBlocks[by * loc.BlockWidth + bx]} not found"); continue; }
                        var tb = DfTownBlock.Load(bsa, name);
                        buildings += tb.Buildings.Count;
                        var ext = DfTownLayout.Exterior(tb);
                        var origin = DfTownLayout.BlockOrigin(bx, by);
                        foreach (var o in ext.Objects)
                        {
                            if (o.Type == DfRdbObjectType.Flat) { Plot(origin.X + o.Position.X, origin.Z + o.Position.Z, 60, 160, 60); continue; }
                            if (!models.TryGetValue(o.ModelId, out var model)) continue;
                            var m = DfDungeon.ObjectTransform(o, origin);
                            foreach (var p in model.Polygons)
                            {
                                if (p.Points.Length < 3) continue;
                                var pts = p.Points.Select(v => System.Numerics.Vector3.Transform(v, m)).ToArray();
                                // Fill by sampling the triangle fan.
                                for (int i = 1; i + 1 < pts.Length; i++)
                                {
                                    var (a, b2, c) = (pts[0], pts[i], pts[i + 1]);
                                    float minX = MathF.Min(a.X, MathF.Min(b2.X, c.X)), maxX = MathF.Max(a.X, MathF.Max(b2.X, c.X));
                                    float minZ = MathF.Min(a.Z, MathF.Min(b2.Z, c.Z)), maxZ = MathF.Max(a.Z, MathF.Max(b2.Z, c.Z));
                                    for (float z = minZ; z <= maxZ; z += 1 / px)
                                        for (float x = minX; x <= maxX; x += 1 / px)
                                        {
                                            float d = (b2.Z - c.Z) * (a.X - c.X) + (c.X - b2.X) * (a.Z - c.Z);
                                            if (MathF.Abs(d) < 1e-6f) continue;
                                            float w0 = ((b2.Z - c.Z) * (x - c.X) + (c.X - b2.X) * (z - c.Z)) / d, w1 = ((c.Z - a.Z) * (x - c.X) + (a.X - c.X) * (z - c.Z)) / d, w2 = 1 - w0 - w1;
                                            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                                            float y = w0 * a.Y + w1 * b2.Y + w2 * c.Y;
                                            int ix = (int)(x * px), iy = h - 1 - (int)(z * px);
                                            if (ix < 0 || iy < 0 || ix >= w || iy >= h || y < zbuf[iy * w + ix]) continue;
                                            zbuf[iy * w + ix] = y;
                                            byte shade = (byte)Math.Clamp(70 + y / 4, 40, 255);
                                            int q = (iy * w + ix) * 3; rgb[q] = shade; rgb[q + 1] = shade; rgb[q + 2] = (byte)Math.Min(255, shade + 20);
                                        }
                                }
                            }
                        }
                        foreach (var d in DfTownLayout.Doors(ext, models)) { Plot(origin.X + d.Centre.X, origin.Z + d.Centre.Z, 230, 40, 40, 2); doors++; }
                    }
                string outPath = opts.GetValueOrDefault("out", $"out/town-{LocationPlacer.Sanitize(loc.Name)}.png");
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
                Util.Png.WriteRgb(outPath, w, h, rgb);
                // Daggerfall's own automap of the same blocks, laid out the same way (block (bx, by) at bx, by from the
                // bottom-left; automap rows drawn as stored, row 0 at the top of each block), for comparison.
                int aw = loc.BlockWidth * 64, ah = loc.BlockHeight * 64;
                var map = new byte[aw * ah * 3];
                for (int by = 0; by < loc.BlockHeight; by++)
                    for (int bx = 0; bx < loc.BlockWidth; bx++)
                        if (DfTownBlock.Resolve(bsa, loc.ExteriorBlocks[by * loc.BlockWidth + bx]) is { } n2)
                        {
                            var am = DfTownBlock.Load(bsa, n2).AutoMap;
                            for (int r = 0; r < 64; r++)
                                for (int c = 0; c < 64; c++)
                                {
                                    byte v = am[r * 64 + c];
                                    int ix = bx * 64 + c, iy = ah - 1 - (by * 64 + 63 - r);
                                    int o = (iy * aw + ix) * 3;
                                    if (v != 0) { map[o] = (byte)(80 + v * 7 % 170); map[o + 1] = (byte)(60 + v * 13 % 190); map[o + 2] = 200; }
                                }
                        }
                Util.Png.WriteRgb(Path.ChangeExtension(outPath, ".automap.png"), aw, ah, map);
                Console.WriteLine($"{loc}: {buildings} building records, {doors} door surfaces -> {Path.GetFullPath(outPath)}");
                return 0;
            }
            case "df-quest-dump":
            {
                // Daggerfall quests decoded into readable commands, close to Daggerfall Unity's text quests (--quest NAME[,NAME];
                // --raw: the QBN records as stored; --names DIR: a folder of Daggerfall Unity's text quests, only to borrow their
                // symbol names when comparing; --messages: the QRC text too).
                foreach (var name in opts["quest"].Split(','))
                {
                    var q = DfQuest.Load(arena2, name.ToUpperInvariant());
                    var names = DfQuestDump.NamesFrom(opts.GetValueOrDefault("names"), q.Name);
                    Console.Write(opts.ContainsKey("raw") ? DfQuestDump.Raw(q, names) : DfQuestDump.Readable(q, names));
                    if (opts.ContainsKey("messages"))
                        foreach (var (id, variants) in q.Messages.OrderBy(m => m.Key))
                            foreach (var v in variants) Console.WriteLine($"\nMessage {id}:\n{v}");
                }
                return 0;
            }
            case "df-quest-build":
            {
                // A scratch plugin with only Daggerfall's quest records (the machine, reputation, every quest, Privateer's
                // Hold's... no sites), to check the quest build without a whole Daggerfall build (--out, default
                // out/quest-test/DFQuestTest.esp; --scripts also compiles the Papyrus scripts into that folder).
                string outPath = Path.GetFullPath(opts.GetValueOrDefault("out", "out/quest-test/DFQuestTest.esp"));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                var mod = new SkyrimMod(ModKey.FromFileName(Path.GetFileName(outPath)), SkyrimRelease.SkyrimSE);
                var masters = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }
                    .Select(m => (ISkyrimModGetter)SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, m), SkyrimRelease.SkyrimSE)).ToList();
                var creatures = new CreatureBuilder(mod, skyrimData);
                creatures.Build();
                var spells = new SpellBuilder(mod, masters[0], DoorPlacer.BaseDoors);
                spells.Build(DfSpells.Load(arena2), new PlacedObject(mod));
                DfQuestGlobals.AddAll(mod);
                var builder = new DfQuestBuilder(mod, arena2, masters, DfFactions.Load(arena2), DfNames.Load(arena2), new VanillaActors(skyrimData))
                {
                    Foe = id => creatures.Spawn(id), CityWatch = creatures.Spawn(146, passive: true), Spells = spells.Spells, SoundDir = Path.GetDirectoryName(outPath),
                };
                builder.Build();
                foreach (var p in builder.Problems) Console.WriteLine("  " + p);
                var loadOrder = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => ModKey.FromFileName(f)).ToArray();
                TopicSubtypes.Fill(mod);
                mod.WriteToBinary(outPath, new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
                {
                    MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(loadOrder),
                });
                Console.WriteLine($"Wrote {outPath}: {mod.Quests.Count} quests, {mod.Messages.Count} messages, {mod.DialogTopics.Count} topics, {mod.EnumerateMajorRecords().Count()} records");
                Console.WriteLine(DfQuestBuilder.CheckScriptProperties(mod, Path.Combine(AppContext.BaseDirectory, "Papyrus")));
                if (opts.ContainsKey("scripts"))
                    AltStartBuilder.CompileScripts(skyrimData, Path.Combine(AppContext.BaseDirectory, "Papyrus"), Path.GetDirectoryName(outPath)!);
                return 0;
            }
            case "df-quest-markers":
            {
                // How many locations' map markers the town and dungeon parts find in a built Daggerfall.esm (--master PATH),
                // for their quest sites (QuestSites.MasterMarkers).
                using var master = SkyrimMod.CreateFromBinaryOverlay(opts["master"], SkyrimRelease.SkyrimSE);
                var lookup = QuestSites.MasterMarkers(master, arena2);
                var all = DfLocations.LoadAll(arena2);
                int found = all.Count(l => lookup(l) is not null);
                Console.WriteLine($"map markers found for {found} of {all.Count} locations; missing e.g. {string.Join(", ", all.Where(l => lookup(l) is null).Take(5).Select(l => l.Name))}");
                return 0;
            }
            case "df-quest-check":
            {
                // Coverage of Daggerfall's quests: per quest, how many of its operations the quest machine supports, and why
                // the rest aren't (like ob-script-inventory).
                var allQuests = DfQuest.LoadAll(arena2);
                Console.Write(DfQuestDump.Check(allQuests));
                if (opts.ContainsKey("simulate"))
                {
                    // --simulate: the quest machine's pass rules with nothing happening; quests that would end on their own.
                    var startRep = DfFactions.Load(arena2).ById.ToDictionary(f => f.Key, f => f.Value.Rep);
                    int endedAlone = 0;
                    foreach (var q in allQuests)
                    {
                        var (ran, ended) = DfQuestDump.SimulateStart(q, startRep);
                        if (ended) endedAlone++;
                        if (ended || opts.GetValueOrDefault("simulate") == q.Name)
                            Console.WriteLine($"{q.Name}: {(ended ? "ENDS ON ITS OWN" : "waits")} after {ran.Count} actions: {string.Join(", ", ran)}");
                    }
                    Console.WriteLine($"simulated start-up: {allQuests.Count - endedAlone} quests wait for the player, {endedAlone} end on their own");
                }
                if (opts.ContainsKey("places"))
                {
                    // --places: every fixed place and the Daggerfall location it resolves to.
                    var locs = DfLocations.LoadAll(arena2);
                    foreach (var q in allQuests)
                        foreach (var p in q.Places.Where(p => p.Scope == 0))
                        {
                            var (id, dungeon, loc) = DfQuestBuilder.ResolveFixed(locs, p.P1, p.P2);
                            Console.WriteLine($"{q.Name} place {p.Index}: 0x{p.P1:X4} 0x{(ushort)p.P2:X4} -> {(loc is null ? "NOT FOUND" : $"{loc.Name} ({loc.RegionName}, {loc.Type}) {(dungeon ? "dungeon" : "streets")}")}");
                        }
                }
                return 0;
            }
            case "df-text":
            {
                // TEXT.RSC records (--ids 7205,7250-7259), each variant on its own line.
                var text = DfText.Load(arena2);
                foreach (var part in opts["ids"].Split(','))
                {
                    var r = part.Split('-').Select(int.Parse).ToArray();
                    for (int id = r[0]; id <= r[^1]; id++)
                        foreach (var (v, i) in text[id].Select((v, i) => (v, i)))
                            Console.WriteLine($"{id}.{i}: {v.Replace("\n", " | ")}");
                }
                return 0;
            }
            case "df-names":
            {
                // NAMEGEN.DAT decoded; --check FILE compares every part with a NameGen JSON (Daggerfall Unity's).
                var names = DfNames.Load(arena2);
                if (opts.TryGetValue("check", out var json))
                {
                    // Daggerfall Unity's file lacks a comma between two sets (its reader is lenient).
                    using var doc = System.Text.Json.JsonDocument.Parse(System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(json), @"\}\s*\{", "},{"), new System.Text.Json.JsonDocumentOptions { AllowTrailingCommas = true });
                    int same = 0, diff = 0;
                    foreach (var bank in doc.RootElement.EnumerateObject())
                    {
                        var b = Enum.Parse<DfNameBank>(bank.Name);
                        foreach (var set in bank.Value.GetProperty("sets").EnumerateArray())
                        {
                            int si = set.GetProperty("setIndex").GetInt32();
                            var expected = set.GetProperty("parts").EnumerateArray().Select(p => p.GetString()).ToList();
                            var got = names.Parts(b, si);
                            if (expected.SequenceEqual(got)) same++;
                            else { diff++; Console.WriteLine($"  {b} set {si}: expected {expected.Count} [{string.Join(",", expected.Take(4))}], got {got.Count} [{string.Join(",", got.Take(4))}]"); }
                        }
                    }
                    Console.WriteLine($"{same} sets identical, {diff} differ");
                }
                foreach (var bank in new[] { DfNameBank.Breton, DfNameBank.Redguard, DfNameBank.Nord })
                    Console.WriteLine($"{bank}: {string.Join(", ", Enumerable.Range(1, 6).Select(s => names.FullName(bank, s % 2 == 0, new DfRandom((uint)(s * 7919)))))}");
                return 0;
            }
            case "df-town-test":
            {
                // A light test of the town pipeline: only the given towns (--towns, as for build) into a scratch plugin
                // (--out dir) with a stand-in overland worldspace, no terrain, dungeons or music.
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/dftown"));
                Directory.CreateDirectory(outDir);
                var mod = new SkyrimMod(ModKey.FromFileName("DaggerfallTownTest.esp"), SkyrimRelease.SkyrimSE);
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var tamriel = skyrim.Worldspaces.First(w => w.EditorID == "Tamriel");
                var overland = new Worldspace(mod) { EditorID = "DFTestOverland", Climate = new FormLinkNullable<IClimateGetter>(tamriel.Climate.FormKeyNullable), Water = new FormLinkNullable<IWaterGetter>(tamriel.Water.FormKeyNullable), TopCell = new Cell(mod) { Persistent = [], Temporary = [] } };
                mod.Worldspaces.Add(overland);
                var models = DfModel.LoadAll(arena2);
                var props = new PropReplacer(skyrim, skyrimData);
                var clutter = new ClutterPlacer(skyrim, props, []);
                DaggerfallPluginBuilder.ImportSpriteModels(mod, outDir, arena2, clutter);
                var townCreatures = new CreatureBuilder(mod, skyrimData);
                townCreatures.Build();
                var dungeons = new DungeonCellBuilder(mod, arena2, models, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), outDir, new Skyrim.Navmesh.NavmeshBuilder(mod), props) { Clutter = clutter, Creatures = townCreatures };
                var townBuilder = new TownBuilder(mod, arena2, models, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), DfWorldMap.Load(arena2), dungeons)
                {
                    People = new TownPeople(mod, new VanillaActors(skyrimData), DfFactions.Load(arena2), DfNames.Load(arena2)),
                    Clutter = clutter,
                    Shops = new TownShops(mod, skyrim),
                    Creatures = townCreatures,
                };
                var locs = DfLocations.LoadAll(arena2);
                var wanted = opts.GetValueOrDefault("towns", "Daggerfall@Daggerfall").Split(',').Select(s => s.Split('@')).ToList();
                var built = new List<TownBuilder.Built>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // "smallest:N:Region" picks the region's N smallest towns (quick tests).
                var spec = opts.GetValueOrDefault("towns", "");
                // "dungeon:N:Region": the N smallest towns there that have a dungeon of their own.
                // "guilds:N:Region": the N smallest towns there with a Mages Guild hall (faction 40) and a temple.
                var testBsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                bool HasGuilds(DfLocation l)
                {
                    var types = l.ExteriorBlocks.Select(n => DfTownBlock.Resolve(testBsa, n) is { } r ? DfTownBlock.Load(testBsa, r) : null).OfType<DfTownBlock>()
                        .SelectMany(b => b.Buildings).Select(b => (b.Data.Type, b.Data.FactionId)).ToList();
                    return types.Any(t => t.Type == DfBuildingType.GuildHall && t.FactionId == 40) && types.Any(t => t.Type == DfBuildingType.Temple);
                }
                // "banks:N:Region": the N smallest towns there with a bank.
                bool HasBank(DfLocation l) => l.ExteriorBlocks.Select(n => DfTownBlock.Resolve(testBsa, n) is { } r ? DfTownBlock.Load(testBsa, r) : null).OfType<DfTownBlock>()
                    .SelectMany(b => b.Buildings).Any(b => b.Data.Type == DfBuildingType.Bank);
                var picked = spec.StartsWith("smallest:") || spec.StartsWith("dungeon:") || spec.StartsWith("guilds:") || spec.StartsWith("banks:")
                    ? locs.Where(l => l.ExteriorBlocks.Length > 0 && l.Type is DfLocationType.TownCity or DfLocationType.TownHamlet or DfLocationType.TownVillage
                            && l.RegionName.Equals(spec.Split(':')[2], StringComparison.OrdinalIgnoreCase) && (!spec.StartsWith("dungeon:") || l.DungeonBlocks.Count > 0))
                        .OrderBy(l => l.ExteriorBlocks.Length).ThenBy(l => l.Name)
                        .Where(l => !spec.StartsWith("guilds:") || HasGuilds(l))
                        .Where(l => !spec.StartsWith("banks:") || HasBank(l))
                        .Take(int.Parse(spec.Split(':')[1])).ToList()
                    : locs.Where(l => l.ExteriorBlocks.Length > 0 && wanted.Any(w => l.Name.Equals(w[0], StringComparison.OrdinalIgnoreCase) && (w.Length < 2 || l.RegionName.Equals(w[1], StringComparison.OrdinalIgnoreCase)))).ToList();
                foreach (var town in picked)
                {
                    var t = townBuilder.Build(town, skyrim, overland, new Noggog.P3Float(0, 0, 0));
                    built.Add(t);
                    Console.WriteLine($"{town.Name} ({town.RegionName}): {t.Buildings} buildings, {t.Interiors} new interiors, {t.Doors} street doors ({t.Unlinked} without a way in) ({sw.Elapsed.TotalSeconds:F0}s)");
                    Console.WriteLine($"  {string.Join(", ", t.Fixtures.OrderByDescending(f => f.Value).Take(30).Select(f => $"{f.Key} {f.Value}"))}");
                }
                Console.WriteLine($"shared interiors: {townBuilder.SharedInteriors}, townsfolk: {townBuilder.Placed.Count}");
                Console.WriteLine($"town navmesh edge links: {dungeons.Navmeshes.LinkExteriorEdges()}");
                var talk = new TownTalk(mod, DfText.Load(arena2), townBuilder.People!.Factions);
                talk.Enlist(townBuilder.Placed);
                talk.Build(built, townBuilder.TalkQuest, townBuilder.Shops?.Shopkeepers, townBuilder.Shops?.TavernKeepers, townBuilder.Shops?.Bankers);
                Console.WriteLine($"talk: {talk.Responses} responses; shops: {townBuilder.Shops!.Shops}");
                townBuilder.AttachDaytime();
                var guilds = new TownGuilds(mod, skyrim, DfText.Load(arena2), townBuilder.People!.Factions);
                guilds.Build(talk, townBuilder.TalkQuest, townBuilder.Placed, townBuilder.BreakIns);
                Console.WriteLine($"guilds: {guilds.Members} with people here ({string.Join(", ", mod.Factions.Where(f => f.EditorID?.StartsWith("DFGuild") == true).Select(f => f.EditorID![7..]))}), {guilds.Trainers} trainers, {guilds.Responses} responses");
                if (clutter.Missing.Count > 0) Console.WriteLine($"clutter forms not found: {string.Join(", ", clutter.Missing)}");
                if (opts.ContainsKey("quests"))
                {
                    // --quests: Daggerfall's quests and these towns' quest sites too (DfQuestBuilder, QuestSites).
                    var questMasters = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }
                        .Select(m => (ISkyrimModGetter)SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, m), SkyrimRelease.SkyrimSE)).ToList();
                    DfQuestGlobals.AddAll(mod);
                    var qb = new DfQuestBuilder(mod, arena2, questMasters, DfFactions.Load(arena2), DfNames.Load(arena2), new VanillaActors(skyrimData))
                    {
                        Foe = id => townCreatures.Spawn(id), CityWatch = townCreatures.Spawn(146, passive: true), GuildsQuest = guilds.Quest, Guilds = guilds.Built, SoundDir = outDir,
                    };
                    qb.Build();
                    var questTowns = picked.Zip(built).Select(p => new QuestSites.Town(p.First, p.Second)).ToList();
                    var sites = QuestSites.Build(mod, "Test", qb.Machine!.FormKey, questTowns, dungeons.BuiltDungeons, dungeons.QuestMarkers,
                        QuestSites.Individuals(mod, townBuilder.Placed.Concat(dungeons.PlacedPeople)), _ => null);
                    foreach (var s in sites)
                    {
                        var p = s.VirtualMachineAdapter!.Scripts[0].Properties;
                        int Len(string n) => p.FirstOrDefault(x => x.Name == n) switch { ScriptIntListProperty i => i.Data.Count, ScriptObjectListProperty o => o.Objects.Count, ScriptStringListProperty t => t.Data.Count, _ => 0 };
                        int Filled(string n) => p.FirstOrDefault(x => x.Name == n) is ScriptObjectListProperty o ? o.Objects.Count(x => !x.Object.IsNull) : 0;
                        Console.WriteLine($"quest sites {s.EditorID}: {Len("TownName")} towns, {Len("BType")} buildings ({Filled("BArrival")} with arrival, {Filled("BSpawn")} with a spawn marker, {Filled("BItem")} item marker), "
                            + $"{Len("DName")} dungeons ({Filled("DSpawn")} with a spawn marker), {Len("IFaction")} individuals");
                        if (p.FirstOrDefault(x => x.Name == "BName") is ScriptStringListProperty bn && bn.Data.Any(n => n.Length > 0))
                            Console.WriteLine($"  building names: {bn.Data.Count(n => n.Length > 0)}, e.g. {string.Join("; ", bn.Data.Where(n => n.Length > 0).Distinct().Take(12))}");
                    }
                    Console.WriteLine($"quest markers: {dungeons.QuestMarkers.Values.Sum(m => m.Count)} in {dungeons.QuestMarkers.Count} cells");
                    // Covens: their witches in a coven faction that "Summon a Daedra Prince." answers to.
                    var covenFactions = DfFactions.Load(arena2).ById.Values.Where(f => f.Type == DfFactionType.WitchesCoven).Select(f => $"DFFaction{f.Id}").ToHashSet();
                    var factionEdid = mod.Factions.ToDictionary(f => f.FormKey, f => f.EditorID ?? "");
                    int witches = townBuilder.Placed.Count(p => p.Npc.Factions.Any(r => covenFactions.Contains(factionEdid.GetValueOrDefault(r.Faction.FormKey, ""))));
                    var summon = mod.DialogTopics.FirstOrDefault(t => t.EditorID == "DFQuestSummon");
                    int summonFactions = summon?.Responses.SelectMany(r => r.Conditions).Select(c => c.Data).OfType<GetInFactionConditionData>().Count(d => covenFactions.Contains(factionEdid.GetValueOrDefault(d.Faction.Link.FormKey, ""))) ?? 0;
                    if (witches > 0 || summon is not null) Console.WriteLine($"covens: {witches} witches in coven factions; the summoning topic answers {summonFactions} coven factions");
                    Console.WriteLine(DfQuestBuilder.CheckScriptProperties(mod, Path.Combine(AppContext.BaseDirectory, "Papyrus")));
                }
                // The law (DfLaw): who answers to which region's crime faction, and the interiors that are someone's.
                var crimeIds = mod.Factions.Where(f => f.EditorID?.StartsWith("DFCrime") == true).ToDictionary(f => f.FormKey, f => f.EditorID!);
                var allCells = mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(s => s.Cells).ToList();
                Console.WriteLine($"law: {crimeIds.Count} region crime factions ({string.Join(", ", crimeIds.Values.Order())}); "
                    + $"{townBuilder.Placed.Count(p => crimeIds.ContainsKey(p.Npc.CrimeFaction.FormKey) && p.Npc.Factions.Any(r => r.Faction.FormKey == p.Npc.CrimeFaction.FormKey))} of {townBuilder.Placed.Count} townsfolk answer to one; "
                    + $"{allCells.Count(c => crimeIds.ContainsKey(c.Owner.FormKey))} of {allCells.Count} interiors owned by one; "
                    + $"{mod.Npcs.Count(n => !n.CrimeFaction.IsNull && !crimeIds.ContainsKey(n.CrimeFaction.FormKey))} actors with another crime faction");
                var loadOrder = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => ModKey.FromFileName(f)).ToArray();
                TamrielMerge.Skyrim.TopicSubtypes.Fill(mod);
                mod.WriteToBinary(Path.Combine(outDir, "DaggerfallTownTest.esp"), new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
                {
                    MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(loadOrder),
                });
                Console.WriteLine($"Wrote {Path.Combine(outDir, "DaggerfallTownTest.esp")} ({new FileInfo(Path.Combine(outDir, "DaggerfallTownTest.esp")).Length / 1048576.0:F1} MB) in {sw.Elapsed.TotalSeconds:F0}s");
                return 0;
            }
            case "df-heights":
            {
                // Dev aid: the overland heights file the conversion gives its town and dungeon parts (TAMRIELMERGE_HEIGHTS).
                string path = Path.GetFullPath(opts.GetValueOrDefault("out", "heights.bin"));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                Skyrim.Terrain.HeightField.Generate(DfWorldMap.Load(arena2), TerrainSettingsFrom(opts)).Save(path);
                Console.WriteLine($"Wrote {path} ({new FileInfo(path).Length / 1048576} MB) in {sw.Elapsed.TotalSeconds:F0} s");
                return 0;
            }
            case "df-towns":
            {
                // Daggerfall's towns beyond Daggerfall.esm's, part --batch of --batches (default: by BlocksPerPart) of
                // "Daggerfall Towns.esm", written beside --plugin (the built Daggerfall.esm, its master) as
                // "Daggerfall Towns.partN.esm" for the conversion to join. --list prints how the towns divide.
                string main = Path.GetFullPath(opts.GetValueOrDefault("plugin", "out/convert/Daggerfall.esm"));
                int batches;
                using (var m = SkyrimMod.CreateFromBinaryOverlay(main, SkyrimRelease.SkyrimSE))
                    batches = int.Parse(opts.GetValueOrDefault("batches", Skyrim.DaggerfallTowns.PartCount(Skyrim.DaggerfallTowns.All(arena2, m)).ToString()));
                if (opts.ContainsKey("list"))
                {
                    using var master = SkyrimMod.CreateFromBinaryOverlay(main, SkyrimRelease.SkyrimSE);
                    var all = Skyrim.DaggerfallTowns.All(arena2, master);
                    Console.WriteLine($"{all.Count} towns, {all.Sum(t => t.ExteriorBlocks.Length)} blocks beyond {Path.GetFileName(main)}");
                    for (int b = 1; b <= batches; b++)
                    {
                        var slice = Skyrim.DaggerfallTowns.Slice(all, b, batches);
                        Console.WriteLine($"  batch {b}: {slice.Count} towns, {slice.Sum(t => t.ExteriorBlocks.Length)} blocks, biggest {slice.MaxBy(t => t.ExteriorBlocks.Length)?.Name} ({slice.Max(t => (int?)t.ExteriorBlocks.Length)})");
                    }
                    return 0;
                }
                int batch = int.Parse(opts.GetValueOrDefault("batch", "1"));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = Skyrim.DaggerfallTowns.Build(arena2, skyrimData, main, batch, batches, TerrainSettingsFrom(opts));
                Console.WriteLine($"Towns batch {batch}/{batches}: {r.Built} built, {r.Failed} failed, {r.People} people ({sw.Elapsed.TotalMinutes:F1} min) -> {r.Path}");
                Console.WriteLine($"  time: setup {r.SetupSeconds:F0} s, towns {r.TownsSeconds:F0} s, talk and guilds {r.TalkSeconds:F0} s, writing {r.WriteSeconds:F0} s");
                return r.Built > 0 ? 0 : 2;
            }
            case "df-dungeons":
            {
                // Daggerfall's dungeons outside its towns, batch --batch of --batches (default 8), as "Daggerfall Dungeons
                // N.esm" beside --plugin (the built Daggerfall.esm, their master).
                string main = Path.GetFullPath(opts.GetValueOrDefault("plugin", "out/convert/Daggerfall.esm"));
                int batch = int.Parse(opts.GetValueOrDefault("batch", "1")), batches = int.Parse(opts.GetValueOrDefault("batches", "8"));
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var r = Skyrim.DaggerfallDungeons.Build(arena2, skyrimData, main, batch, batches, TerrainSettingsFrom(opts));
                Console.WriteLine($"Dungeons batch {batch}/{batches}: {r.Built} built, {r.Failed} failed, {r.References} references, {r.NavmeshTriangles} navmesh triangles, {Skyrim.Navmesh.NavmeshBuilder.InteriorsShared} navmeshes made by other parts ({sw.Elapsed.TotalMinutes:F1} min) -> {r.Path}");
                Console.WriteLine($"  time: setup {r.SetupSeconds:F0} s, dungeons {r.LayoutSeconds:F0} s (navmeshes {r.NavmeshSeconds:F0} s of work across threads), writing {r.WriteSeconds:F0} s");
                return r.Built > 0 ? 0 : 2;
            }
            case "df-dungeon-test":
            {
                // Measures building Daggerfall's dungeons: --count N dungeons (not towns, not Privateer's Hold), from
                // --skip S on, into a scratch plugin: references, navmesh, time and memory per dungeon.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var mod = new SkyrimMod(ModKey.FromFileName("DaggerfallDungeonTest.esp"), SkyrimRelease.SkyrimSE);
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/dfdungeontest"));
                Directory.CreateDirectory(outDir);
                var overland = new Worldspace(mod) { EditorID = "DFTestOverland", TopCell = new Cell(mod) { Persistent = [], Temporary = [] } };
                mod.Worldspaces.Add(overland);
                var models = DfModel.LoadAll(arena2);
                var props = new PropReplacer(skyrim, skyrimData);
                var spells = new SpellBuilder(mod, skyrim, DoorPlacer.BaseDoors);
                spells.Build(DfSpells.Load(arena2), new PlacedObject(mod) { EditorID = "TestAnchor" });
                var creatures = new CreatureBuilder(mod, skyrimData);
                creatures.Build();
                var dungeons = new DungeonCellBuilder(mod, arena2, models, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), outDir, new Skyrim.Navmesh.NavmeshBuilder(mod), props)
                {
                    Spells = spells, Clutter = new ClutterPlacer(skyrim, props, []), Creatures = creatures,
                    People = new TownPeople(mod, new VanillaActors(skyrimData), DfFactions.Load(arena2), DfNames.Load(arena2)),
                };
                // --names "A@Region,B" picks dungeons by location name (a town's own dungeon too, e.g. Daggerfall's castle);
                // --block P picks dungeons with a block whose name starts with P (W = the water blocks).
                var every = DfLocations.LoadAll(arena2).Where(l => l.DungeonBlocks.Count > 0).ToList();
                var all = opts.TryGetValue("names", out var names)
                    ? every.Where(l => names.Split(',').Select(n => n.Split('@')).Any(n => l.Name.Equals(n[0], StringComparison.OrdinalIgnoreCase) && (n.Length < 2 || l.RegionName.Equals(n[1], StringComparison.OrdinalIgnoreCase)))).ToList()
                    : every.Where(l => l.Type is not (DfLocationType.TownCity or DfLocationType.TownHamlet or DfLocationType.TownVillage) && l.Name != "Privateer's Hold"
                        && (!opts.TryGetValue("block", out var bp) || l.DungeonBlocks.Any(b => b.BlockName.StartsWith(bp, StringComparison.OrdinalIgnoreCase)))).ToList();
                Console.WriteLine($"dungeons without a town: {all.Count}; blocks per dungeon: avg {all.Average(l => l.DungeonBlocks.Count):F1}, max {all.Max(l => l.DungeonBlocks.Count)}");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long refs = 0, tris = 0, failures = 0;
                foreach (var loc in all.Skip(int.Parse(opts.GetValueOrDefault("skip", "0"))).Take(int.Parse(opts.GetValueOrDefault("count", "10"))))
                {
                    try
                    {
                        var b = dungeons.Build(loc, overland, new Noggog.P3Float(0, 0, 0));
                        refs += b.Cell.Temporary.Count + b.Cell.Persistent.Count;
                        tris += b.Navmesh.Triangles;
                        Console.WriteLine($"  {loc.Name} ({loc.DungeonType}, {loc.DungeonBlocks.Count} blocks): {b.Cell.Temporary.Count + b.Cell.Persistent.Count} refs, navmesh {b.Navmesh.Triangles} tris; {sw.Elapsed.TotalSeconds:F0}s, {GC.GetTotalMemory(false) / 1048576} MB managed");
                        if (opts.ContainsKey("details"))
                        {
                            Console.WriteLine($"    {string.Join(", ", b.Fixtures.Where(f => !f.Key.StartsWith("clutter:") && !f.Key.StartsWith("person:")).OrderByDescending(f => f.Value).Take(40).Select(f => $"{f.Key} {f.Value}"))}");
                            // Statues: where each stands, the way its front faces, its size, and the nearest door.
                            var statueIds = skyrim.Statics.Where(st => (st.EditorID ?? "").Contains("Statue")).ToDictionary(st => st.FormKey, st => (st.EditorID!, st.ObjectBounds));
                            var doorsHere = b.Cell.Temporary.Concat(b.Cell.Persistent).OfType<IPlacedObjectGetter>().Where(r => DoorPlacer.BaseDoors.Contains(r.Base.FormKey)).ToList();
                            foreach (var r in b.Cell.Temporary.OfType<IPlacedObjectGetter>().Where(r => statueIds.ContainsKey(r.Base.FormKey)))
                            {
                                var (sid, ob) = statueIds[r.Base.FormKey];
                                float h = r.Placement!.Rotation.Z, sc = r.Scale ?? 1;
                                bool plusY = sid is "StatueMara01" or "StatueTalos01";
                                var front = plusY ? new System.Numerics.Vector2(MathF.Sin(h), MathF.Cos(h)) : new System.Numerics.Vector2(-MathF.Sin(h), -MathF.Cos(h));
                                var p0 = r.Placement.Position;
                                var nd = doorsHere.OrderBy(d => MathF.Pow(d.Placement!.Position.X - p0.X, 2) + MathF.Pow(d.Placement.Position.Y - p0.Y, 2)).FirstOrDefault();
                                float door = nd is null ? -1 : MathF.Sqrt(MathF.Pow(nd.Placement!.Position.X - p0.X, 2) + MathF.Pow(nd.Placement.Position.Y - p0.Y, 2));
                                Console.WriteLine($"    statue {sid} at {p0.X:F0},{p0.Y:F0},{p0.Z:F0} faces ({front.X:F2},{front.Y:F2}) scale {sc:F2}: {(ob.Second.X - ob.First.X) * sc:F0} x {(ob.Second.Y - ob.First.Y) * sc:F0} x {(ob.Second.Z - ob.First.Z) * sc:F0}; nearest door {door:F0}{(nd is null ? "" : $" at {nd.Placement!.Position.X:F0},{nd.Placement.Position.Y:F0},{nd.Placement.Position.Z:F0} heading {nd.Placement.Rotation.Z * 180 / MathF.PI:F0}")}");
                            }
                            var folk = b.Fixtures.Where(f => f.Key.StartsWith("person:")).Select(f => f.Key[8..]).ToList();
                            if (folk.Count > 0) Console.WriteLine($"    people ({folk.Count}): {string.Join(", ", folk.Take(30))}");
                        }
                    }
                    catch (Exception e) { failures++; Console.WriteLine($"  {loc.Name}: FAILED {e.Message}"); if (opts.ContainsKey("trace")) Console.WriteLine(e); }
                }
                Console.WriteLine($"total: {refs} refs, {tris} navmesh triangles, {failures} failed, {sw.Elapsed.TotalSeconds:F0}s, working set {Environment.WorkingSet / 1048576} MB");
                if (opts.ContainsKey("prison"))
                {
                    // --prison: Daggerfall's prison and every region's jail (DfLaw.BuildPrison) as Daggerfall.esm makes them.
                    var regionCrime = Enumerable.Range(0, DfLaw.Regions).Select(r => DfLaw.Region(mod, r)).ToList();
                    var exteriors = Enumerable.Range(0, DfLaw.Regions).Select(r =>
                        CrimeFactions.Exterior(mod, overland.TopCell!, new Noggog.P3Float(r * 512, 0, 0), 0, $"DFJail{r:00}Exterior")).ToArray();
                    var prison = DfLaw.BuildPrison(mod, arena2, outDir, props, spells, dungeons.Navmeshes, every, exteriors);
                    int jailed = regionCrime.Count(f => !f.ExteriorJailMarker.IsNull && !f.PlayerInventoryContainer.IsNull && !f.StolenGoodsContainer.IsNull && !f.FollowerWaitMarker.IsNull);
                    var prisonMarkers = prison.Cell.Persistent.OfType<PlacedObject>().Count(p => p.Base.FormKey == CrimeFactions.PrisonMarker);
                    Console.WriteLine($"prison: {prison.Cell.EditorID} \"{prison.Cell.Name}\" from {prison.From}: {prison.Cell.Persistent.Count + prison.Cell.Temporary.Count} refs, {prisonMarkers} prison markers; "
                        + $"{jailed} of {regionCrime.Count} crime factions with jail, wait marker, evidence and stolen-goods chests; exit door locked: {prison.Cell.Persistent.OfType<PlacedObject>().Any(p => p.EditorID?.StartsWith("DFExitDoor") == true && p.Lock is not null && p.TeleportDestination is null)}");
                }
                if (opts.ContainsKey("write"))
                {
                    var loadOrder = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => ModKey.FromFileName(f)).ToArray();
                    TamrielMerge.Skyrim.TopicSubtypes.Fill(mod);
                    mod.WriteToBinary(Path.Combine(outDir, "DaggerfallDungeonTest.esp"), new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
                    {
                        MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(loadOrder),
                    });
                    Console.WriteLine($"Wrote {Path.Combine(outDir, "DaggerfallDungeonTest.esp")}");
                }
                return 0;
            }
            case "stair-test-build":
            {
                // Dev aid (user-approved in-game test, 2026-10-09): TMStairTest.esp (light) and its meshes into --out: an
                // interior cell "TMStairTest" with lanes 400 apart along x, each climbing along +y from y = 0: ramps of
                // 40-60 degrees (rise 200), plain steps (raw collision, no ramp conversion) 16/24/32/40 high on 32-deep
                // treads and 40 on 16, and the 40/16 steps as the converter builds them (its ramps).
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/stairtest"));
                var tex = new Skyrim.Meshes.BlockMeshBuilder.Tex(@"textures\landscape\dirt01.dds", @"textures\landscape\dirt01_n.dds", false);
                var lanes = new List<(string Name, List<(System.Numerics.Vector3[] Pts, System.Numerics.Vector3 N, Skyrim.Meshes.BlockMeshBuilder.Tex Tex)> Polys, bool ViaConverter)>();
                bool stairsRun = opts.ContainsKey("stairs-material");   // the second run: steps of the stair material
                bool run3 = opts.ContainsKey("run3");   // the third: stair-material ramps, and Daggerfall's steps split finer
                bool run4 = opts.ContainsKey("converter");   // the converter's own stairs (BuildPolygons: steps + flight ramps)
                System.Numerics.Vector3 V(float x, float y, float z) => new(x, y, z);
                List<(System.Numerics.Vector3[] Pts, System.Numerics.Vector3 N, Skyrim.Meshes.BlockMeshBuilder.Tex Tex)> Box(float x0, float y0, float z0, float x1, float y1, float z1) =>
                [
                    ([V(x0, y0, z1), V(x1, y0, z1), V(x1, y1, z1), V(x0, y1, z1)], V(0, 0, 1), tex),
                    ([V(x0, y0, z0), V(x1, y0, z0), V(x1, y0, z1), V(x0, y0, z1)], V(0, -1, 0), tex),
                    ([V(x0, y1, z0), V(x0, y1, z1), V(x1, y1, z1), V(x1, y1, z0)], V(0, 1, 0), tex),
                    ([V(x0, y0, z0), V(x0, y0, z1), V(x0, y1, z1), V(x0, y1, z0)], V(-1, 0, 0), tex),
                    ([V(x1, y0, z0), V(x1, y1, z0), V(x1, y1, z1), V(x1, y0, z1)], V(1, 0, 0), tex),
                ];
                const float W = 200;
                foreach (int deg in run4 ? new int[0] : run3 ? new[] { 55, 60, 68 } : stairsRun ? new[] { 45 } : new[] { 40, 45, 50, 55, 60 })
                {
                    float run = 200 / MathF.Tan(deg * MathF.PI / 180);
                    var n = System.Numerics.Vector3.Normalize(V(0, -200, run));
                    var polys = new List<(System.Numerics.Vector3[] Pts, System.Numerics.Vector3 N, Skyrim.Meshes.BlockMeshBuilder.Tex Tex)> { ([V(0, 0, 0), V(W, 0, 0), V(W, run, 200), V(0, run, 200)], n, tex) };
                    polys.AddRange(Box(0, run, 0, W, run + 300, 200));
                    lanes.Add((run3 ? $"stairsramp{deg}" : $"ramp{deg}", polys, false));
                }
                if (run4)
                    foreach (var (rise, tread, n) in new[] { (40f, 16f, 5), (40f, 16f, 10), (24f, 32f, 5), (16f, 32f, 5) })
                    {
                        var polys = new List<(System.Numerics.Vector3[] Pts, System.Numerics.Vector3 N, Skyrim.Meshes.BlockMeshBuilder.Tex Tex)>();
                        for (int k = 0; k < n; k++) polys.AddRange(Box(0, k * tread, 0, W, k * tread + tread, (k + 1) * rise));
                        polys.AddRange(Box(0, n * tread, 0, W, n * tread + 300, n * rise));
                        lanes.Add(($"conv{rise}x{tread}x{n}", polys, true));
                    }
                foreach (var (rise, tread) in run4 ? new (float, float)[0] : run3 ? new[] { (40f, 16f), (20f, 8f), (40f / 3, 16f / 3) } : stairsRun ? new[] { (24f, 32f), (24f, 32f), (32f, 32f), (40f, 32f), (40f, 16f), (48f, 32f) } : new[] { (16f, 32f), (24f, 32f), (32f, 32f), (40f, 32f), (40f, 16f) })
                {
                    var polys = new List<(System.Numerics.Vector3[] Pts, System.Numerics.Vector3 N, Skyrim.Meshes.BlockMeshBuilder.Tex Tex)>();
                    int count = (int)MathF.Round(200 / rise);   // up to 200 (5 Daggerfall steps), however finely split
                    if (!run3) count = 5;
                    for (int k = 0; k < count; k++) polys.AddRange(Box(0, k * tread, 0, W, k * tread + tread, (k + 1) * rise));
                    polys.AddRange(Box(0, count * tread, 0, W, count * tread + 300, count * rise));
                    string nm = $"steps{rise}x{tread}";
                    // The stair run: the first 24 x 32 lane in plain stone (the control), the rest in the stair material.
                    if (stairsRun) nm = lanes.Any(l => l.Name == "stone" + nm) || rise != 24 ? "stairs" + nm : "stone" + nm;
                    if (run3) nm = $"stairs{rise:F0}x{tread:F0}";
                    lanes.Add((nm, polys, false));
                    if (!stairsRun && rise == 40 && tread == 16) lanes.Add(("df40x16", polys, true));
                }
                var mod = new SkyrimMod(ModKey.FromFileName("TMStairTest.esp"), SkyrimRelease.SkyrimSE);
                mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
                var cell = new Cell(mod)
                {
                    EditorID = "TMStairTest", Name = "Stair Test", Flags = Cell.Flag.IsInteriorCell, Temporary = [], Persistent = [],
                    Lighting = new CellLighting
                    {
                        AmbientColor = System.Drawing.Color.FromArgb(0, 160, 160, 160), DirectionalColor = System.Drawing.Color.FromArgb(0, 200, 200, 200),
                        FogNearColor = System.Drawing.Color.Black, FogFarColor = System.Drawing.Color.Black, FogFar = 100000, FogPower = 1, FogMax = 1,
                        LightFadeBegin = 6000, LightFadeEnd = 8000, DirectionalFade = 1,
                    },
                };
                mod.Cells.Records.Add(new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = [new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [cell] }] });
                int lane = 0;
                foreach (var (name, polys, via) in lanes.Append(("floor", Box(-400, -600, -20, lanes.Count * 400 + 400, 1200, 0), false)))
                {
                    string rel = $@"meshes\tamrielmerge\stairtest\{name}.nif";
                    string full = Path.Combine(outDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    if (via) Skyrim.Meshes.BlockMeshBuilder.BuildPolygons(polys, full);
                    else Skyrim.Meshes.BlockMeshBuilder.BuildRaw(polys, full, name.StartsWith("stairs") ? Skyrim.Meshes.BlockMeshBuilder.StairsStoneMaterial : null);
                    var st = new Static(mod) { EditorID = "TMStairTest" + name, Model = new Model { File = rel[7..] }, ObjectBounds = new ObjectBounds() };
                    mod.Statics.Add(st);
                    float x = name == "floor" ? 0 : lane++ * 400;
                    cell.Temporary.Add(new PlacedObject(mod) { Base = new FormLinkNullable<IPlaceableObjectGetter>(st.FormKey), Placement = new Placement { Position = new Noggog.P3Float(x, 0, 0), Rotation = new Noggog.P3Float(0, 0, 0) } });
                    if (name != "floor") Console.WriteLine($"lane {lane - 1} {name}: x {x + W / 2}");
                }
                // The automatic run (--script <TMStairTest.psc>): a start marker per lane, a quest whose script walks them.
                if (opts.TryGetValue("script", out var script))
                {
                    var starts = new List<PlacedObject>();
                    var names = new List<string>();
                    int li = 0;
                    foreach (var (name, _, _) in lanes)
                    {
                        var mk = new PlacedObject(mod)
                        {
                            EditorID = "TMStairTestStart" + name,
                            Base = new FormLinkNullable<IPlaceableObjectGetter>(new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x34)),
                            Placement = new Placement { Position = new Noggog.P3Float(li++ * 400 + W / 2, -250, 30), Rotation = new Noggog.P3Float(0, 0, 0) },
                            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
                        };
                        cell.Persistent.Add(mk);
                        starts.Add(mk); names.Add(name);
                    }
                    var list = new ScriptObjectListProperty { Name = "Starts" };
                    foreach (var mk in starts) list.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(mk.FormKey) });
                    mod.Quests.Add(new Quest(mod)
                    {
                        EditorID = "TMStairTestQuest",
                        Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce,
                        Priority = 10,
                        VirtualMachineAdapter = new QuestAdapter { Scripts = [new ScriptEntry { Name = "TMStairTest", Properties = [list, new ScriptStringListProperty { Name = "Names", Data = [.. names] }] }] },
                    });
                    string src = Path.Combine(outDir, "psc");
                    Directory.CreateDirectory(src);
                    File.Copy(script, Path.Combine(src, "TMStairTest.psc"), true);
                    Skyrim.AltStartBuilder.CompileScripts(skyrimData, src, outDir);
                }
                mod.WriteToBinary(Path.Combine(outDir, "TMStairTest.esp"));
                Console.WriteLine($"wrote {Path.Combine(outDir, "TMStairTest.esp")}");
                return 0;
            }
            case "nif-stairs":
            {
                // Dev aid (user, 2026-10-09: "find skyrims stairs and see how they work"): for each NIF (--file a;b): the
                // visible treads (upward faces) by height, and the collision (bhkCompressedMeshShapeData, decoded) by
                // slope, area-weighted: real steps show as 0 and 90 degree faces, ramps as faces in between.
                const float Hk = 69.99125f;
                foreach (var file in opts["file"].Split(';'))
                {
                    using var nif = new nifly.NifFile();
                    if (nif.Load(file) != 0) { Console.WriteLine($"{file}: can't load"); continue; }
                    Console.WriteLine($"== {Path.GetFileName(file)}");
                    var hdr = nif.GetHeader();
                    // Visible: tread heights (faces facing up) and how much area each has.
                    var treads = new SortedDictionary<int, float>();
                    float vMinZ = float.MaxValue, vMaxZ = float.MinValue;
                    foreach (var shape in nif.GetShapes())
                    {
                        using var verts = new nifly.vectorVector3();
                        nif.GetVertsForShape(shape, verts);
                        using var tris = new nifly.vectorTriangle();
                        shape.GetTriangles(tris);
                        var v = verts.Select(q => new System.Numerics.Vector3(q.x, q.y, q.z)).ToList();
                        foreach (var t in tris)
                        {
                            var (pa, pb, pc) = (v[t.p1], v[t.p2], v[t.p3]);
                            var n = System.Numerics.Vector3.Cross(pb - pa, pc - pa);
                            float area = n.Length() / 2;
                            if (area < 1) continue;
                            vMinZ = MathF.Min(vMinZ, MathF.Min(pa.Z, MathF.Min(pb.Z, pc.Z))); vMaxZ = MathF.Max(vMaxZ, MathF.Max(pa.Z, MathF.Max(pb.Z, pc.Z)));
                            if (MathF.Abs(n.Z) / (2 * area) < 0.95f) continue;
                            int h = (int)MathF.Round((pa.Z + pb.Z + pc.Z) / 3 / 2) * 2;
                            treads[h] = treads.GetValueOrDefault(h) + area;
                        }
                    }
                    var levels = treads.Where(kv => kv.Value > 200).Select(kv => kv.Key).ToList();
                    var rises = levels.Zip(levels.Skip(1), (x, y) => y - x).ToList();
                    Console.WriteLine($"  visible: height {vMinZ:F0}..{vMaxZ:F0}; tread levels {string.Join(" ", levels)}; rises {string.Join(" ", rises)}");
                    // Our own collision (convex slabs): the slope of each slab's walkable face (its most upward plane).
                    var slabSlopes = new SortedDictionary<int, int>();
                    using var slabCache = new nifly.niflycpp.BlockCache(hdr);
                    uint blockCount = hdr.GetNumBlocks();
                    for (uint id = 0; id < blockCount; id++)
                    {
                        if (hdr.GetBlockTypeStringById(id) != "bhkConvexVerticesShape") continue;
                        var cv = slabCache.EditableBlockById<nifly.bhkConvexVerticesShape>(id);
                        float best = -1;
                        foreach (var n4 in cv.normals.items()) best = MathF.Max(best, n4.z);
                        if (best <= 0.1f) continue;
                        int deg = (int)(MathF.Acos(Math.Clamp(best, 0, 1)) * 180 / MathF.PI) / 5 * 5;
                        slabSlopes[deg] = slabSlopes.GetValueOrDefault(deg) + 1;
                    }
                    if (slabSlopes.Count > 0) Console.WriteLine($"  collision slabs by walkable slope: {string.Join(", ", slabSlopes.Select(kv => $"{kv.Key}-{kv.Key + 4} deg: {kv.Value}"))}");
                    // --near x,y,z (the NIF's own units) and --r: each collision slab there, with its extent and walkable slope.
                    if (opts.TryGetValue("near", out var slabNear))
                    {
                        var c = slabNear.Split(',').Select(float.Parse).ToArray();
                        float r = float.Parse(opts.GetValueOrDefault("r", "300"));
                        for (uint id = 0; id < blockCount; id++)
                        {
                            if (hdr.GetBlockTypeStringById(id) != "bhkConvexVerticesShape") continue;
                            var cv = slabCache.EditableBlockById<nifly.bhkConvexVerticesShape>(id);
                            var pts = cv.verts.items().Select(q => new System.Numerics.Vector3(q.x, q.y, q.z) * Hk).ToList();
                            if (pts.Count == 0 || !pts.Any(q => MathF.Abs(q.X - c[0]) < r && MathF.Abs(q.Y - c[1]) < r && MathF.Abs(q.Z - c[2]) < r)) continue;
                            float up = cv.normals.items().Max(n4 => n4.z);
                            Console.WriteLine($"  slab {id}: x {pts.Min(q => q.X):F0}..{pts.Max(q => q.X):F0} y {pts.Min(q => q.Y):F0}..{pts.Max(q => q.Y):F0} z {pts.Min(q => q.Z):F0}..{pts.Max(q => q.Z):F0}, walkable slope {MathF.Acos(Math.Clamp(up, -1, 1)) * 180 / MathF.PI:F0} deg");
                        }
                    }
                    // (Vanilla's compressed collision meshes: tools/ingame/cmsd.py.)
                }
                return 0;
            }
            case "mesh-refresh":
            {
                // Reconverts, in place, the Morrowind or Oblivion models (--game mw|ob) already converted under each --dirs
                // root (meshes\tamrielmerge\<game>\..., ';'-separated), as the current converter makes them (2026-10-09:
                // stairs as Skyrim's). Only existing files are rewritten; moving and item variants are left as they are.
                string game = opts["game"].ToLowerInvariant();
                Func<string, string, bool> convert;
                if (game == "mw")
                {
                    var data = Morrowind.Tes3Data.Open(GameInstalls.DataDir(Game.Morrowind)!);
                    var cache = new Dictionary<string, Morrowind.MwMeshConverter>();
                    convert = (model, root) =>
                    {
                        if (!cache.TryGetValue(root, out var c)) cache[root] = c = new Morrowind.MwMeshConverter(data, new Morrowind.MwTextures(data, root));
                        c.Convert(model, root); return true;
                    };
                }
                else
                {
                    var data = new Oblivion.ObData(GameInstalls.DataDir(Game.Oblivion)!);
                    var cache = new Dictionary<string, Oblivion.ObMeshConverter>();
                    convert = (model, root) =>
                    {
                        if (!cache.TryGetValue(root, out var c)) cache[root] = c = new Oblivion.ObMeshConverter(data, new Oblivion.ObTextures(data, root));
                        c.Convert(model, root); return true;
                    };
                }
                int done = 0, failed = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var root in opts["dirs"].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string dir = Path.Combine(root, "meshes", "tamrielmerge", game);
                    if (!Directory.Exists(dir)) { Console.WriteLine($"{dir}: none"); continue; }
                    foreach (var nif in Directory.EnumerateFiles(dir, "*.nif", SearchOption.AllDirectories))
                    {
                        string name = Path.GetFileNameWithoutExtension(nif);
                        if (name.EndsWith("_moving", StringComparison.OrdinalIgnoreCase) || name.EndsWith("_item", StringComparison.OrdinalIgnoreCase)) continue;
                        string model = "meshes\\" + Path.GetRelativePath(dir, nif);
                        try { convert(model, root); done++; }
                        catch (Exception e) { failed++; if (failed <= 20) Console.WriteLine($"  {model}: {e.Message}"); }
                        if ((done + failed) % 500 == 0) Console.WriteLine($"  {done + failed} ({sw.Elapsed.TotalSeconds:F0}s)");
                    }
                    Console.WriteLine($"{dir}: {done} reconverted so far, {failed} failed ({sw.Elapsed.TotalSeconds:F0}s)");
                }
                return 0;
            }
            case "clutter-table":
            {
                // Dev aid: every Daggerfall sprite the clutter table stands in for, with the vanilla record's type, and
                // whether a sprite model (SpriteModels) replaces it.
                using var sky = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var types = sky.EnumerateMajorRecords().Where(r => r.EditorID is not null).GroupBy(r => r.EditorID!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.First().GetType().Name.Replace("BinaryOverlay", ""), StringComparer.OrdinalIgnoreCase);
                var targets = Skyrim.SpriteModels.Targets.Select(t => (t.Archive, t.Record)).ToHashSet();
                foreach (var ((a, r), spec) in Skyrim.ClutterPlacer.Table.OrderBy(k => k.Key))
                {
                    string id = spec.Parts[0].Id;
                    Console.WriteLine($"{a}.{r}\t{spec.Label}\t{id}\t{types.GetValueOrDefault(id, "?")}\t{(targets.Contains((a, r)) ? "MODEL" : "")}");
                }
                return 0;
            }
            case "df-items-test":
            {
                // Dev aid: Daggerfall's weapons and armor (Skyrim/DfItems) alone into --out (default out/dfitemstest):
                // how many, any without a Skyrim model, and a few made, with their numbers.
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/dfitemstest"));
                Directory.CreateDirectory(outDir);
                var masters = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }
                    .Select(m => (ISkyrimModGetter)SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, m), SkyrimRelease.SkyrimSE)).ToList();
                var mod = new SkyrimMod(ModKey.FromFileName("DaggerfallItemsTest.esp"), SkyrimRelease.SkyrimSE);
                var items = new Skyrim.DfItems(mod, masters, skyrimData, outDir);
                items.Build();
                foreach (var name in new[] { "Iron Dagger", "Steel Katana", "Silver Longsword", "Mithril Claymore", "Daedric Dai-katana", "Ebony Warhammer" })
                    if (items.Items.TryGetValue(name, out var k) && mod.Weapons.FirstOrDefault(x => x.FormKey == k) is { } w)
                        Console.WriteLine($"  {name}: damage {w.BasicStats?.Damage}, weight {w.BasicStats?.Weight}, value {w.BasicStats?.Value}, model {w.Model?.File}");
                foreach (var name in new[] { "Leather Cuirass", "Chain Cuirass", "Iron Helm", "Mithril Cuirass", "Adamantium Gauntlets", "Daedric Tower Shield", "Steel Greaves" })
                    if (items.Items.TryGetValue(name, out var k) && mod.Armors.FirstOrDefault(x => x.FormKey == k) is { } a)
                        Console.WriteLine($"  {name}: rating {a.ArmorRating}, weight {a.Weight}, value {a.Value}, pieces {a.Armature.Count}");
                mod.WriteToBinary(Path.Combine(outDir, "DaggerfallItemsTest.esp"));
                foreach (var m in masters) (m as IDisposable)?.Dispose();
                return 0;
            }
            case "df-land-textures":
            {
                // A built "Daggerfall Land.esm" (--plugin, default out/convert) in place with Daggerfall's own ground (user,
                // 2026-10-12: "now for the daggerfall textures for the game itself and worldspace"): its land textures
                // added (TerrainBuilder.AddDaggerfallTextures, upscaled pictures beside it) and every cell's layers moved
                // from the Skyrim ones to them; the old plugin kept as .skyrimtex.bak. New builds make them themselves.
                string landPath = Path.GetFullPath(opts.GetValueOrDefault("plugin", Path.Combine("out", "convert", Skyrim.Terrain.OverlandTiles.LandPluginName)));
                var landMod = SkyrimMod.CreateFromBinary(landPath, SkyrimRelease.SkyrimSE);
                if (landMod.LandscapeTextures.Any(l => l.EditorID?.StartsWith("DFLand") == true)) { Console.WriteLine("df-land-textures: already done"); return 0; }
                using var skyrimEsm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var forms = Skyrim.Terrain.TerrainBuilder.AddDaggerfallTextures(landMod, skyrimEsm, arena2, Path.GetDirectoryName(landPath)!);
                int cells = 0, layers = 0;
                foreach (var ws in landMod.Worldspaces)
                    foreach (var cell in ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                    {
                        if (cell.Landscape is not { } land) continue;
                        cells++;
                        foreach (var layer in land.Layers)
                            if (layer.Header is { } h && Skyrim.Terrain.TerrainBuilder.SkyrimIndex(h.Texture.FormKey) is var i and >= 0)
                            {
                                h.Texture.SetTo(forms[i]);
                                layers++;
                            }
                    }
                // Written beside it under its own name (a plugin's file name is its identity), then swapped in.
                string tmpDir = Path.Combine(Path.GetDirectoryName(landPath)!, "land-textures-tmp");
                Directory.CreateDirectory(tmpDir);
                string tmp = Path.Combine(tmpDir, Path.GetFileName(landPath));
                landMod.WriteToBinary(tmp);
                File.Move(landPath, landPath + ".skyrimtex.bak", overwrite: true);
                File.Move(tmp, landPath);
                Directory.Delete(tmpDir);
                Console.WriteLine($"df-land-textures: {cells} cells, {layers} layers moved to Daggerfall's ground ({forms.Distinct().Count()} land textures)");
                return 0;
            }
            case "df-grass":
            {
                // A built "Daggerfall Land.esm" (--plugin, default out/convert) in place with Daggerfall's own grass (user,
                // 2026-10-12: "the grass needs to be daggerfalls and the grass placement on where its suppose to be"): each
                // Daggerfall land texture's grass list set to its climate's grass (Skyrim/Terrain/DfGrass), the meshes and
                // upscaled sprites beside it; the old plugin kept as .skyrimgrass.bak. New builds make them themselves.
                string landPath = Path.GetFullPath(opts.GetValueOrDefault("plugin", Path.Combine("out", "convert", Skyrim.Terrain.OverlandTiles.LandPluginName)));
                var landMod = SkyrimMod.CreateFromBinary(landPath, SkyrimRelease.SkyrimSE);
                using var skyrimEsm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                // Grass made by an earlier run of this goes, so it isn't left twice.
                foreach (var old in landMod.Grasses.Where(g => g.EditorID?.StartsWith("DFGrass") == true).Select(g => g.FormKey).ToList()) landMod.Grasses.Remove(old);
                var grass = new Skyrim.Terrain.DfGrass(landMod, skyrimEsm, arena2, Path.GetDirectoryName(landPath)!);
                int textures = 0;
                foreach (var lt in landMod.LandscapeTextures)
                {
                    int i = Skyrim.Terrain.TerrainBuilder.DaggerfallIndex(lt.EditorID);
                    if (i < 0) continue;
                    lt.Grasses.Clear();
                    foreach (var g in grass.For(i)) lt.Grasses.Add(g.FormKey.ToLink<IGrassGetter>());
                    Console.WriteLine($"  {lt.EditorID}: {string.Join(", ", lt.Grasses.Select(g => landMod.Grasses.First(x => x.FormKey == g.FormKey)).Select(g => $"{g.EditorID} (density {g.Density}, slope {g.MinSlope}-{g.MaxSlope}, from water {g.UnitsFromWater} {g.UnitsFromWaterType})"))}");
                    textures++;
                }
                if (textures == 0) { Console.WriteLine("df-grass: no Daggerfall land textures (run df-land-textures first)"); return 1; }
                string tmpDir = Path.Combine(Path.GetDirectoryName(landPath)!, "grass-tmp");
                Directory.CreateDirectory(tmpDir);
                string tmp = Path.Combine(tmpDir, Path.GetFileName(landPath));
                landMod.WriteToBinary(tmp);
                if (!File.Exists(landPath + ".skyrimgrass.bak")) File.Copy(landPath, landPath + ".skyrimgrass.bak");
                File.Move(tmp, landPath, overwrite: true);
                Directory.Delete(tmpDir);
                Console.WriteLine($"df-grass: {textures} land textures, {landMod.Grasses.Count(g => g.EditorID?.StartsWith("DFGrass") == true)} grasses");
                return 0;
            }
            case "df-block-meshes":
            {
                // Rebuilds the Daggerfall dungeon block meshes already in each --dirs folder (Meshes\TamrielMerge\Dungeons\*.nif
                // under it; ';'-separated), in place, as the current converter makes them (2026-10-09: stairs as Skyrim's,
                // the stair material and one ramp per flight). Nothing else changes: same files, textures and plugins.
                var models = DfModel.LoadAll(arena2);
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                int done = 0, failed = 0;
                foreach (var root in opts["dirs"].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    string dir = Path.Combine(root, "meshes", "TamrielMerge", "Dungeons");
                    if (!Directory.Exists(dir)) { Console.WriteLine($"{dir}: none"); continue; }
                    Skyrim.Meshes.BlockMeshBuilder.Export = new Skyrim.Meshes.DfTextureExport(arena2, root);
                    foreach (var nif in Directory.GetFiles(dir, "*.nif"))
                    {
                        try
                        {
                            var block = DfDungeonBlock.Load(bsa, Path.GetFileNameWithoutExtension(nif) + ".RDB");
                            Skyrim.Meshes.BlockMeshBuilder.Build(block, models, nif);
                            done++;
                        }
                        catch (Exception e) { failed++; Console.WriteLine($"  {Path.GetFileName(nif)}: {e.Message}"); }
                    }
                    Console.WriteLine($"{dir}: {done} rebuilt so far, {failed} failed");
                }
                return failed == 0 ? 0 : 2;
            }
            case "df-town-meshes":
            {
                // Rebuilds the Daggerfall town building interiors already in each --dirs folder
                // (Meshes\TamrielMerge\Towns\<style>\<block>_<building>.nif under it; ';'-separated), in place, as the current
                // converter makes them (2026-10-10, user: "finish daggerfall" - the town parts built before the stair fix
                // keep their plugins; only their interiors' collision changes: stairs as Skyrim's); with --exteriors the
                // blocks' exteriors (<style>\<block>.nif) too. The ground is left as it is.
                var models = DfModel.LoadAll(arena2);
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var blocks = new Dictionary<string, DfTownBlock>(StringComparer.OrdinalIgnoreCase);
                int done = 0, failed = 0;
                var started = DateTime.Now;
                foreach (var root in opts["dirs"].Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var export = new Skyrim.Meshes.DfTextureExport(arena2, root);
                    Skyrim.Meshes.BlockMeshBuilder.Export = export;
                    var textures = new Skyrim.Meshes.TownTextures(arena2, export);
                    foreach (var style in Enum.GetValues<Skyrim.Meshes.TownStyle>())
                    {
                        // With --ground the blocks' ground too (Ground\<style>\<block>.nif): Daggerfall's own tiles.
                        string groundDir = Path.Combine(root, "meshes", "TamrielMerge", "Towns", "Ground", style.ToString());
                        if (opts.ContainsKey("ground") && Directory.Exists(groundDir))
                            foreach (var nif in Directory.GetFiles(groundDir, "*.nif").Where(n => !Path.GetFileName(n).Equals("Margin.nif", StringComparison.OrdinalIgnoreCase)))
                                try   // (the margin round a town isn't a block: it keeps its ground)
                                {
                                    string name = Path.GetFileNameWithoutExtension(nif) + ".RMB";
                                    if (!blocks.TryGetValue(name, out var tb)) blocks[name] = tb = DfTownBlock.Load(bsa, name);
                                    Skyrim.Meshes.BlockMeshBuilder.BuildPolygons(textures.GroundPolygons(tb, style), nif);
                                    done++;
                                }
                                catch (Exception e) { failed++; Console.WriteLine($"  ground {Path.GetFileName(nif)}: {e.Message}"); }
                        string dir = Path.Combine(root, "meshes", "TamrielMerge", "Towns", style.ToString());
                        if (!Directory.Exists(dir)) continue;
                        var picker = new Skyrim.Meshes.BlockMeshBuilder.Picker((a, r, n) => textures.Pick(a, r, n, style, interior: true));
                        var outside = new Skyrim.Meshes.BlockMeshBuilder.Picker((a, r, n) => textures.Pick(a, r, n, style, interior: false));
                        var files = Directory.GetFiles(dir, "*.nif");
                        foreach (var nif in files)
                        {
                            var m = System.Text.RegularExpressions.Regex.Match(Path.GetFileNameWithoutExtension(nif), @"^(.+)_(\d\d)$");
                            // A block's exterior (its buildings from outside): rebuilt only with --exteriors (2026-10-11, user:
                            // "daggerfall needs its building textures" - the exteriors take Daggerfall's own textures too).
                            if (!m.Success && !opts.ContainsKey("exteriors")) continue;
                            try
                            {
                                string name = (m.Success ? m.Groups[1].Value : Path.GetFileNameWithoutExtension(nif)) + ".RMB";
                                if (!blocks.TryGetValue(name, out var tb)) blocks[name] = tb = DfTownBlock.Load(bsa, name);
                                if (!m.Success)
                                {
                                    Skyrim.Meshes.BlockMeshBuilder.Build(DfTownLayout.Exterior(tb), models, nif, outside);
                                    if (++done % 500 == 0) Console.WriteLine($"  {done} rebuilt ({(DateTime.Now - started).TotalMinutes:F0} min)");
                                    continue;
                                }
                                var b = tb.Buildings.First(x => x.Index == int.Parse(m.Groups[2].Value));
                                Skyrim.Meshes.BlockMeshBuilder.Build(DfTownLayout.Interior(tb, b, models), models, nif, picker);
                                if (++done % 500 == 0) Console.WriteLine($"  {done} rebuilt ({(DateTime.Now - started).TotalMinutes:F0} min)");
                            }
                            catch (Exception e) { failed++; Console.WriteLine($"  {Path.GetFileName(nif)}: {e.Message}"); }
                        }
                        Console.WriteLine($"{dir}: {done} rebuilt so far, {failed} failed");
                    }
                }
                return failed == 0 ? 0 : 2;
            }
            case "df-stairs":
            {
                // Dev aid: Daggerfall's stair risers over the dungeon blocks: rise against the tread in front, as a histogram
                // of slope (what a ramp from nosing to nosing would be).
                var models = DfModel.LoadAll(arena2);
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var hist = new SortedDictionary<int, int>();
                int risers = 0, blocks = 0;
                // --name: only that location's dungeon blocks, with each riser and the collision ramps listed.
                IEnumerable<DfDungeonBlock> Blocks()
                {
                    if (opts.TryGetValue("name", out var locName))
                    {
                        var loc = DfLocations.LoadAll(arena2).First(l => l.Name == locName && l.DungeonBlocks.Count > 0);
                        foreach (var (b, _) in DfDungeon.Load(loc, bsa, []).Blocks) yield return b;
                        yield break;
                    }
                    foreach (var e in bsa.Entries.Where(e => e.Name.EndsWith(".RDB", StringComparison.OrdinalIgnoreCase)).Take(int.Parse(opts.GetValueOrDefault("max", "400"))))
                    {
                        DfDungeonBlock? bl = null;
                        try { bl = DfDungeonBlock.Load(bsa, e.Name); } catch { }
                        if (bl is not null) yield return bl;
                    }
                }
                var rampSlopes = new SortedDictionary<int, int>();
                var flightOrigins = new Dictionary<string, System.Numerics.Vector3>(StringComparer.OrdinalIgnoreCase);
                if (opts.TryGetValue("name", out var flightLoc))
                {
                    var loc = DfLocations.LoadAll(arena2).First(l => l.Name == flightLoc && l.DungeonBlocks.Count > 0);
                    foreach (var (b, placement) in DfDungeon.Load(loc, bsa, []).Blocks)
                    {
                        var o = DfDungeon.BlockOrigin(placement);
                        flightOrigins.TryAdd(b.Name, new System.Numerics.Vector3(o.X, o.Z, o.Y) * Skyrim.Meshes.BlockMeshBuilder.Scale);
                    }
                }
                foreach (var block in Blocks().DistinctBy(b => b.Name))
                {
                    blocks++;
                    if (opts.ContainsKey("name"))
                    {
                        Console.WriteLine($"block {block.Name} at {flightOrigins.GetValueOrDefault(block.Name)}");
                        // --flights: each flight in cell units (blocks sit at their origin, unrotated), and whether its
                        // ramp's run-out fits on the free floor beyond its last step.
                        // --near x,y,z: the solid surfaces within --r (default 700) of a cell point, by kind and height.
                        if (opts.TryGetValue("near", out var nearAt))
                        {
                            var place = flightOrigins.GetValueOrDefault(block.Name);
                            var c = nearAt.Split(',').Select(float.Parse).ToArray();
                            var at = new System.Numerics.Vector3(c[0], c[1], c[2]) - place;
                            float r = float.Parse(opts.GetValueOrDefault("r", "700"));
                            foreach (var (pts, n) in Skyrim.Meshes.BlockMeshBuilder.SolidPolygons(block, models)
                                .Where(q => q.Points.Any(v => System.Numerics.Vector2.Distance(new(v.X, v.Y), new(at.X, at.Y)) < r && MathF.Abs(v.Z - at.Z) < 600))
                                .OrderByDescending(q => q.Points.Max(v => v.Z)))
                            {
                                string kind = n.Z > 0.95f ? "floor" : n.Z > 0.3f ? $"slope {MathF.Acos(n.Z) * 180 / MathF.PI:F0}deg" : n.Z < -0.95f ? "ceiling" : MathF.Abs(n.Z) < 0.3f ? "wall" : "under";
                                if (kind == "ceiling" || kind == "under") continue;
                                Console.WriteLine($"  {kind,-12} z {pts.Min(v => v.Z) + place.Z:F0}..{pts.Max(v => v.Z) + place.Z:F0}  x {pts.Min(v => v.X) + place.X:F0}..{pts.Max(v => v.X) + place.X:F0}  y {pts.Min(v => v.Y) + place.Y:F0}..{pts.Max(v => v.Y) + place.Y:F0}  n ({n.X:F2},{n.Y:F2},{n.Z:F2})");
                            }
                        }
                        // --models a,b: where those models stand (cell units), with their turn and size (Daggerfall units).
                        if (opts.TryGetValue("models", out var modelList))
                        {
                            var place = flightOrigins.GetValueOrDefault(block.Name);
                            var ids = modelList.Split(',').Select(uint.Parse).ToHashSet();
                            foreach (var o in block.Objects.Where(o => o.Type == DfRdbObjectType.Model && ids.Contains(o.ModelId) && models.ContainsKey(o.ModelId)))
                            {
                                var dm = models[o.ModelId];
                                var at = new System.Numerics.Vector3(o.Position.X, o.Position.Z, o.Position.Y) * Skyrim.Meshes.BlockMeshBuilder.Scale + place;
                                Console.WriteLine($"  model {o.ModelId} at {at.X:F0},{at.Y:F0},{at.Z:F0} rot {o.Rotation}: {dm.Max.X - dm.Min.X:F0} x {dm.Max.Z - dm.Min.Z:F0} x {dm.Max.Y - dm.Min.Y:F0} tall");
                            }
                        }
                        // --doors: each hinged door's model size (Daggerfall units: across, tall, thick).
                        if (opts.ContainsKey("doors"))
                            foreach (var g in block.Objects.Where(o => o.Tag is "DOR" or "DDR" && models.ContainsKey(o.ModelId)).GroupBy(o => (o.Tag, o.ModelId)))
                            {
                                var dm = models[g.Key.ModelId];
                                float ex = dm.Max.X - dm.Min.X, ez = dm.Max.Z - dm.Min.Z;
                                Console.WriteLine($"  {g.Key.Tag} model {g.Key.ModelId} x{g.Count()}: {MathF.Max(ex, ez):F0} across, {dm.Max.Y - dm.Min.Y:F0} tall, {MathF.Min(ex, ez):F0} thick");
                            }
                        if (opts.ContainsKey("flights"))
                        {
                            var place = flightOrigins.GetValueOrDefault(block.Name);
                            foreach (var (top, down, n, height, run, free) in Skyrim.Meshes.BlockMeshBuilder.FlightReport(block, models))
                                Console.WriteLine($"  flight at {top.X + place.X:F0},{top.Y + place.Y:F0},{top.Z + place.Z:F0} down ({down.X:F2},{down.Y:F2}): {n} steps, {height:F0} high, ramp run {run:F0}, free floor {free:F0}{(run > free + 8 ? "  <-- ramp runs into a wall" : "")}");
                        }
                    }
                    if (opts.ContainsKey("name"))
                        foreach (var (pts, n) in Skyrim.Meshes.BlockMeshBuilder.SolidPolygons(block, models).Where(p => p.Normal.Z is > 0.3f and < 0.95f))
                        {
                            int deg = (int)(MathF.Acos(n.Z) * 180 / MathF.PI) / 5 * 5;
                            rampSlopes[deg] = rampSlopes.GetValueOrDefault(deg) + 1;
                        }
                    foreach (var (rise, tread) in Skyrim.Meshes.BlockMeshBuilder.StairStats(block, models))
                    {
                        risers++;
                        int deg = tread <= 0 ? 90 : (int)(MathF.Atan2(rise, tread) * 180 / MathF.PI) / 5 * 5;
                        hist[deg] = hist.GetValueOrDefault(deg) + 1;
                    }
                }
                Console.WriteLine($"{risers} risers in {blocks} dungeon blocks; slope of a nosing-to-nosing ramp:");
                foreach (var (deg, n) in hist) Console.WriteLine($"  {deg,2}-{deg + 4} deg: {n}");
                if (rampSlopes.Count > 0)
                {
                    Console.WriteLine("sloped collision (ramps and sloped floors) by steepness:");
                    foreach (var (deg, n) in rampSlopes) Console.WriteLine($"  {deg,2}-{deg + 4} deg: {n}");
                }
                return 0;
            }
            case "df-ships":
            {
                // Dev aid: where Daggerfall's town blocks have ships: buildings of the Ship type (their outside models) and
                // the long models (over --len units, default 1500) near water in any block, by model with sizes.
                var models = DfModel.LoadAll(arena2);
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                float len = float.Parse(opts.GetValueOrDefault("len", "1500"));
                var found = new Dictionary<uint, (int Count, HashSet<string> Blocks, string Why)>();
                foreach (var e in bsa.Entries.Where(e => e.Name.EndsWith(".RMB", StringComparison.OrdinalIgnoreCase)))
                {
                    DfTownBlock tb;
                    try { tb = DfTownBlock.Load(bsa, e.Name); } catch { continue; }
                    foreach (var b in tb.Buildings.Where(b => b.Data.Type == DfBuildingType.Ship))
                        foreach (var m in b.Exterior.Models)
                        {
                            found.TryGetValue(m.ModelId, out var f);
                            (f.Blocks ??= []).Add(e.Name);
                            found[m.ModelId] = (f.Count + 1, f.Blocks, "ship building");
                        }
                    // Any other placement of the ship models, or (--ids) of given models.
                    var ids = opts.TryGetValue("ids", out var idList) ? idList.Split(',').Select(uint.Parse).ToHashSet() : [909u, 910u];
                    foreach (var m in tb.MiscModels.Concat(tb.Buildings.Where(b => b.Data.Type != DfBuildingType.Ship).SelectMany(b => b.Exterior.Models)).Where(m => ids.Contains(m.ModelId)))
                    {
                        found.TryGetValue(m.ModelId, out var f);
                        (f.Blocks ??= []).Add(e.Name);
                        found[m.ModelId] = (f.Count + 1, f.Blocks, f.Why ?? "elsewhere");
                    }
                }
                foreach (var (id, f) in found.OrderBy(kv => kv.Key))
                {
                    var m = models.GetValueOrDefault(id);
                    string size = m is null ? "?" : $"{m.Max.X - m.Min.X:F0} x {m.Max.Y - m.Min.Y:F0} x {m.Max.Z - m.Min.Z:F0}";
                    Console.WriteLine($"{id}: x{f.Count} ({f.Why}) size {size}; blocks {string.Join(" ", f.Blocks.Take(8))}{(f.Blocks.Count > 8 ? " ..." : "")}");
                }
                return 0;
            }
            case "df-town-census":
            {
                // How big converting every Daggerfall location with town blocks would be: locations, blocks, buildings with
                // interiors, and how many unique interiors that is when shared per (block, building, style).
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var map = DfWorldMap.Load(arena2);
                var cache = new Dictionary<string, DfTownBlock?>(StringComparer.OrdinalIgnoreCase);
                int locs = 0, blocks = 0, interiors = 0;
                var uniqueBlocks = new HashSet<(string, Skyrim.Meshes.TownStyle)>();
                var uniqueInteriors = new HashSet<(string, int, Skyrim.Meshes.TownStyle)>();
                var uniqueNoStyle = new HashSet<(string, int)>();
                var byType = new Dictionary<DfLocationType, int>();
                var only = opts.TryGetValue("types", out var ts) ? ts.Split(',').Select(Enum.Parse<DfLocationType>).ToHashSet() : null;
                foreach (var loc in DfLocations.LoadAll(arena2).Where(l => l.ExteriorBlocks.Length > 0 && (only is null || only.Contains(l.Type))))
                {
                    locs++;
                    byType[loc.Type] = byType.GetValueOrDefault(loc.Type) + 1;
                    var style = Skyrim.Meshes.TownTextures.StyleOf(map.ClimateAt(loc.PixelX, loc.PixelY));
                    foreach (var n in loc.ExteriorBlocks)
                    {
                        if (!cache.TryGetValue(n, out var b)) cache[n] = b = DfTownBlock.Resolve(bsa, n) is { } r ? DfTownBlock.Load(bsa, r) : null;
                        if (b is null) continue;
                        blocks++;
                        uniqueBlocks.Add((b.Name, style));
                        foreach (var bd in b.Buildings.Where(x => x.Interior.Models.Count > 0))
                        {
                            interiors++;
                            uniqueInteriors.Add((b.Name, bd.Index, style));
                            uniqueNoStyle.Add((b.Name, bd.Index));
                        }
                    }
                }
                Console.WriteLine($"{locs} locations with town blocks ({string.Join(", ", byType.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))})");
                Console.WriteLine($"{blocks} blocks placed, {uniqueBlocks.Count} unique (block, style) meshes");
                Console.WriteLine($"{interiors} building interiors placed; shared per (block, building, style): {uniqueInteriors.Count}; per (block, building): {uniqueNoStyle.Count}");
                return 0;
            }
            case "df-flat-census":
            {
                // Every flat of the given texture archives (--archives 346,178) in the RDB and RMB blocks: block, record,
                // faction/mobile field, flags and how many locations use the block.
                var wantArchives = opts.GetValueOrDefault("archives", "346,178").Split(',').Select(int.Parse).ToHashSet();
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var allLocs = DfLocations.LoadAll(arena2);
                var uses = allLocs.SelectMany(l => l.DungeonBlocks.Select(b => b.BlockName).Concat(l.ExteriorBlocks)).GroupBy(n => n, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                var byRecord = new SortedDictionary<(int, int), int>();
                foreach (var e in bsa.Entries)
                {
                    IEnumerable<(DfRdbObject O, string Where)> flats;
                    if (e.Name.EndsWith(".RDB", StringComparison.OrdinalIgnoreCase))
                        flats = DfDungeonBlock.Load(bsa, e.Name).Objects.Where(o => o.Type == DfRdbObjectType.Flat).Select(o => (o, "dungeon"));
                    else if (e.Name.EndsWith(".RMB", StringComparison.OrdinalIgnoreCase))
                    {
                        var tb = DfTownBlock.Load(bsa, e.Name);
                        flats = DfTownLayout.Exterior(tb).Objects.Where(o => o.Type == DfRdbObjectType.Flat).Select(o => (o, "outside"))
                            .Concat(tb.Buildings.SelectMany(b => DfTownLayout.Interior(tb, b, new Dictionary<uint, DfModel>()).Objects.Where(o => o.Type == DfRdbObjectType.Flat).Select(o => (o, $"inside #{b.Index} {b.Data.Type}"))));
                    }
                    else continue;
                    foreach (var (o, where) in flats.Where(f => wantArchives.Contains(f.O.TextureArchive)))
                    {
                        byRecord[(o.TextureArchive, o.TextureRecord)] = byRecord.GetValueOrDefault((o.TextureArchive, o.TextureRecord)) + 1;
                        if (opts.ContainsKey("list"))
                            Console.WriteLine($"  {e.Name} ({uses.GetValueOrDefault(e.Name)} uses) {where}: {o.TextureArchive}.{o.TextureRecord} faction {o.FactionOrMobileId} flags 0x{o.FlatFlags:X} action {o.FlatAction} at ({o.Position.X:F0},{o.Position.Y:F0},{o.Position.Z:F0})");
                    }
                }
                Console.WriteLine(string.Join(", ", byRecord.Select(kv => $"{kv.Key.Item1}.{kv.Key.Item2} x{kv.Value}")));
                return 0;
            }
            case "df-action-census":
            {
                // The actions of the given dungeon models (--models 61032,54000): how many of each (type, axis, magnitude,
                // duration, tag), across every RDB block.
                var wantModels = opts.GetValueOrDefault("models", "61032").Split(',').Select(uint.Parse).ToHashSet();
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                // --location NAME: every action model in that location's dungeon blocks, one line each.
                if (opts.TryGetValue("location", out var locName))
                {
                    var loc = DfLocations.LoadAll(arena2).First(l => l.Name.Equals(locName, StringComparison.OrdinalIgnoreCase));
                    foreach (var blockName in loc.DungeonBlocks.Select(b => b.BlockName).Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        var block = DfDungeonBlock.Load(bsa, blockName);
                        for (int i = 0; i < block.Objects.Count; i++)
                            if (block.Objects[i] is { Action: { } a } o && o.Type == DfRdbObjectType.Model)
                                Console.WriteLine($"  {blockName} #{i} model {o.ModelId} tag {o.Tag} type {a.Flags} axis {a.Axis} magnitude {a.Magnitude} duration {a.Duration} at ({o.Position.X:F0},{o.Position.Y:F0},{o.Position.Z:F0}) rot ({o.Rotation.X},{o.Rotation.Y},{o.Rotation.Z}) trigger {o.TriggerAndLock} next {a.NextObjectIndex}");
                    }
                    return 0;
                }
                var tally = new Dictionary<string, int>();
                foreach (var e in bsa.Entries.Where(e => e.Name.EndsWith(".RDB", StringComparison.OrdinalIgnoreCase)))
                    foreach (var o in DfDungeonBlock.Load(bsa, e.Name).Objects.Where(o => o.Type == DfRdbObjectType.Model && wantModels.Contains(o.ModelId) && o.Action is not null))
                    {
                        var a = o.Action!;
                        string key = $"model {o.ModelId} type {a.Flags} axis {a.Axis} magnitude {a.Magnitude} duration {a.Duration} tag {o.Tag} next {(a.NextObjectIndex >= 0 ? "yes" : "no")}";
                        tally[key] = tally.GetValueOrDefault(key) + 1;
                    }
                foreach (var (k, n) in tally.OrderByDescending(kv => kv.Value).Take(40)) Console.WriteLine($"  {n,5}  {k}");
                return 0;
            }
            case "df-rmb-census":
            {
                // Parse check of every RMB (town) block in BLOCKS.BSA: totals, building types, people flats by archive,
                // and blocks whose parse doesn't end at the record's end. --block NAME prints one block in detail.
                var bsa = new Daggerfall.DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                if (opts.TryGetValue("block", out var one))
                {
                    var b = Daggerfall.DfTownBlock.Load(bsa, one);
                    Console.WriteLine($"{b.Name}: {b.Buildings.Count} buildings, {b.MiscModels.Count} misc models, {b.MiscFlats.Count} misc flats, unread {b.Unread}");
                    foreach (var bd in b.Buildings)
                        Console.WriteLine($"  #{bd.Index} {bd.Data.Type} q{bd.Data.Quality} faction {bd.Data.FactionId} at ({bd.XPos},{bd.ZPos}) rot {bd.YRotation}: ext {bd.Exterior.Models.Count} models [{string.Join(",", bd.Exterior.Models.Take(4).Select(m => m.ModelId))}] {bd.Exterior.Flats.Count} flats {bd.Exterior.Doors.Count} doors; int {bd.Interior.Models.Count} models {bd.Interior.Flats.Count} flats {bd.Interior.People.Count} people {bd.Interior.Doors.Count} doors");
                    return 0;
                }
                int blocks = 0, buildings = 0, people = 0, doors = 0, bad = 0;
                var types = new Dictionary<Daggerfall.DfBuildingType, int>();
                var archives = new Dictionary<int, int>();
                foreach (var e in bsa.Entries.Where(e => e.Name.EndsWith(".RMB", StringComparison.OrdinalIgnoreCase)))
                {
                    Daggerfall.DfTownBlock b;
                    try { b = Daggerfall.DfTownBlock.Load(bsa, e.Name); }
                    catch (Exception ex) { bad++; Console.WriteLine($"  {e.Name}: {ex.Message}"); continue; }
                    blocks++;
                    if (b.Unread is < 0 or > 16) { bad++; if (bad < 10) Console.WriteLine($"  {e.Name}: {b.Unread} bytes unread"); }
                    foreach (var bd in b.Buildings)
                    {
                        buildings++;
                        types[bd.Data.Type] = types.GetValueOrDefault(bd.Data.Type) + 1;
                        people += bd.Interior.People.Count + bd.Exterior.People.Count;
                        doors += bd.Interior.Doors.Count + bd.Exterior.Doors.Count;
                        foreach (var pf in bd.Interior.People) archives[pf.TextureArchive] = archives.GetValueOrDefault(pf.TextureArchive) + 1;
                    }
                }
                if (opts.ContainsKey("flats"))
                {
                    // Exterior flats (building exteriors, loose, ground scenery) by archive, and those with a faction.
                    var byArchive = new Dictionary<int, int>(); int withFaction = 0;
                    foreach (var e in bsa.Entries.Where(e => e.Name.EndsWith(".RMB", StringComparison.OrdinalIgnoreCase)))
                        foreach (var f in DfTownLayout.Exterior(DfTownBlock.Load(bsa, e.Name)).Objects.Where(o => o.Type == DfRdbObjectType.Flat))
                        {
                            byArchive[f.TextureArchive] = byArchive.GetValueOrDefault(f.TextureArchive) + 1;
                            if (f.FactionOrMobileId != 0) withFaction++;
                        }
                    Console.WriteLine($"exterior flats: {string.Join(", ", byArchive.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"))}; with a faction: {withFaction}");
                }
                if (opts.ContainsKey("textures"))
                {
                    // Texture families (archive % 100, as climates add 0/100/300/400) on exterior models, by polygon count.
                    var models = Daggerfall.DfModel.LoadAll(arena2);
                    var ext = new Dictionary<int, int>(); var inside = new Dictionary<int, int>();
                    foreach (var e in bsa.Entries.Where(e => e.Name.EndsWith(".RMB", StringComparison.OrdinalIgnoreCase)))
                    {
                        var b = Daggerfall.DfTownBlock.Load(bsa, e.Name);
                        foreach (var m in b.Buildings.SelectMany(x => x.Exterior.Models).Concat(b.MiscModels))
                            if (models.TryGetValue(m.ModelId, out var md)) foreach (var p in md.Polygons) ext[p.TextureArchive] = ext.GetValueOrDefault(p.TextureArchive) + 1;
                        foreach (var m in b.Buildings.SelectMany(x => x.Interior.Models))
                            if (models.TryGetValue(m.ModelId, out var md)) foreach (var p in md.Polygons) inside[p.TextureArchive] = inside.GetValueOrDefault(p.TextureArchive) + 1;
                    }
                    Console.WriteLine($"exterior archives: {string.Join(", ", ext.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"))}");
                    Console.WriteLine($"interior archives: {string.Join(", ", inside.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key}:{kv.Value}"))}");
                }
                Console.WriteLine($"{blocks} RMB blocks, {buildings} building records, {people} people, {doors} doors, {bad} bad");
                Console.WriteLine($"types: {string.Join(", ", types.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}");
                Console.WriteLine($"people archives: {string.Join(", ", archives.OrderByDescending(kv => kv.Value).Take(20).Select(kv => $"{kv.Key}:{kv.Value}"))}");
                return 0;
            }
            case "df-vid":
            {
                // A Daggerfall video (--file, e.g. ANIM0000.VID) as PNG frames at --fps (default 30) and audio.wav in --out.
                string file = Path.Combine(arena2, opts["file"]);
                var (frames, w, h, secs) = Daggerfall.DfVid.Export(file, opts.GetValueOrDefault("out", "out/video/" + Path.GetFileNameWithoutExtension(file)), double.Parse(opts.GetValueOrDefault("fps", "30"), System.Globalization.CultureInfo.InvariantCulture));
                Console.WriteLine($"{opts["file"]}: {w}x{h}, {frames} frames, {secs:F1}s");
                return 0;
            }
            case "videos":
            {
                // Each installed game's opening cinematic for its story's start (TMStoryOrder.PlayIntro plays it once),
                // as Video\TamrielMerge_<Game>Intro.bik under --out (default out/convert): remastered by
                // tools/video/remaster.py (Real-ESRGAN upscale to 1440x1080 / 1920x1080, Bink 1 via RAD Video Tools) when
                // its Python environment and tools are present; otherwise Morrowind's and Oblivion's own Bink intros are
                // used as they are (Skyrim plays them) and Daggerfall's and Redguard's, which need converting, are left out.
                // With --gpu, Daggerfall's, Oblivion's and Redguard's then get the ray-traced look (tools/video/rt_look.py:
                // light read from each frame's estimated depth, Daggerfall's lit by its torch), Daggerfall's tape footage
                // cleaned first (tools/video/deflare.py: tempered upscale, tape colour, soft flame, flare ring faded);
                // Morrowind's dream imagery stays upscaled only (user, 2026-10-08). --no-rt-look leaves them upscaled.
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/convert"));
                string videoDir = Path.Combine(outDir, "Video");
                Directory.CreateDirectory(videoDir);
                string python = Setup.Dependencies.Python ?? "", script = Path.Combine(AppContext.BaseDirectory, "tools", "video", "remaster.py");
                if (!File.Exists(script)) script = Path.GetFullPath(Path.Combine("tools", "video", "remaster.py"));
                bool canRemaster = File.Exists(python) && File.Exists(script) && File.Exists(Setup.Dependencies.RadVideo) && File.Exists(Setup.Dependencies.Ffmpeg)
                    && File.Exists(Setup.Dependencies.FastUpscaleModel) && !opts.ContainsKey("no-remaster");
                // On the CPU with the fast model unless --gpu (sustained GPU upscaling runs graphics cards hot).
                string[] device = opts.ContainsKey("gpu") ? ["--device", "cuda", "--model", Setup.Dependencies.UpscaleModel]
                    : ["--device", "cpu", "--threads", Math.Max(2, Environment.ProcessorCount - 4).ToString(), "--model", Setup.Dependencies.FastUpscaleModel];
                var found = GameInstalls.Detect();
                string work = Path.GetFullPath("out/video/work");
                void Remaster(string name, string input, params string[] extra) => RemasterTo(name, Path.Combine(videoDir, $"TamrielMerge_{name}Intro.bik"), input, extra);
                void RemasterTo(string name, string target, string input, params string[] extra)
                {
                    if (File.Exists(target)) { Console.WriteLine($"{name}: {target} already there"); return; }                    var psi = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false };
                    // --plain needs no model (and no device): frames are only resized.
                    bool plain = extra.Contains("--plain");
                    string[] how = plain ? [] : device;
                    // A resize's frames (3.5 MB each, 20 GB for Redguard's movie 2) are made on a solid-state drive when the
                    // work folder is on a hard disk, and deleted once the movie is made (2026-10-16: on a shingled hard disk
                    // the writes fell to a frame every ten seconds). An upscale's stay with the work, to resume.
                    string? fastWork = plain ? Util.Drives.FastStaging(work, 30L << 30, "TamrielMergeVideo") : null;
                    string jobWork = Path.Combine(fastWork ?? work, name.ToLowerInvariant());
                    // Resized only, at 960x720: the sources are 640x480 at most, so a bigger frame holds nothing more, and the
                    // Bink compressor's time goes with the pixels (2026-10-16: Redguard's movie 3 at 1440x1080 took 22
                    // minutes and 804 MB).
                    string[] size = plain && !extra.Contains("--size") ? ["--size", "960x720"] : [];
                    foreach (var arg in new[] { "-u", script, "--input", input, "--output", target, "--work", jobWork }.Concat(how).Concat(size).Concat(extra)) psi.ArgumentList.Add(arg);
                    psi.Environment["TAMRIELMERGE_TOOLS"] = Setup.Dependencies.Root;
                    if (!opts.ContainsKey("gpu")) psi.Environment["CUDA_VISIBLE_DEVICES"] = "";
                    Console.WriteLine($"{name}: remastering {input}");
                    using var proc = System.Diagnostics.Process.Start(psi)!;
                    proc.WaitForExit();
                    if (proc.ExitCode != 0) Console.WriteLine($"{name}: remaster failed ({proc.ExitCode})");
                    else if (fastWork is not null) try { Directory.Delete(jobWork, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
                bool rtLook = canRemaster && opts.ContainsKey("gpu") && !opts.ContainsKey("no-rt-look");
                void RtLook(string name, string light, bool clean)
                {
                    string target = Path.Combine(videoDir, $"TamrielMerge_{name}Intro.bik"), done = target + ".rt";
                    if (!rtLook || !File.Exists(target) || File.Exists(done)) return;
                    string w = Path.Combine(work, name.ToLowerInvariant()), up = Path.Combine(w, "up"), src = Path.Combine(w, "src"), frames = up;
                    if (!Directory.Exists(up)) { Console.WriteLine($"{name}: no upscaled frames for the ray-traced look"); return; }
                    if (clean)
                    {
                        frames = Path.Combine(w, "clean");
                        Console.WriteLine($"{name}: cleaning the tape footage");
                        if (Setup.ToolScripts.Run(python, ["-u", Setup.ToolScripts.Path("video", "deflare.py"), "--up", up, "--src", src, "--out", frames]) != 0)
                        { Console.WriteLine($"{name}: clean-up failed, remastered intro kept"); return; }
                    }
                    Console.WriteLine($"{name}: ray-traced look ({light} light)");
                    string lit = target + ".rt.bik";
                    if (Setup.ToolScripts.Run(python, ["-u", Setup.ToolScripts.Path("video", "rt_look.py"), "--frames", frames, "--audio", Path.Combine(src, "audio.wav"),
                            "--out", lit, "--work", Path.Combine(w, "rt"), "--light", light]) == 0 && File.Exists(lit))
                    {
                        File.Move(lit, target, true);
                        File.WriteAllText(done, light);
                    }
                    else Console.WriteLine($"{name}: ray-traced look failed, remastered intro kept");
                }
                void CopyOriginal(string name, string source)
                {
                    if (File.Exists(source)) { File.Copy(source, Path.Combine(videoDir, $"TamrielMerge_{name}Intro.bik"), true); Console.WriteLine($"{name}: original intro copied"); }
                }
                bool intros = !opts.ContainsKey("quest-videos-only");   // dev: only the quest videos (--anims 11,13 picks some)
                if (intros && found.ContainsKey(Game.Daggerfall) && canRemaster)
                {
                    string frames = Path.Combine(work, "dag2-frames");
                    if (!File.Exists(Path.Combine(frames, "audio.wav"))) Daggerfall.DfVid.Export(Path.Combine(arena2, "DAG2.VID"), frames);
                    Remaster("Daggerfall", frames, "--fps", "30");
                    RtLook("Daggerfall", "torch", clean: true);
                }
                // Daggerfall's quest videos (ANIMnnnn.VID, "play video n": the main quest's visions and endings), as
                // Video\TamrielMerge_DfAnim<n>.bik for TMDfQuestMachine.PlayVideo. Resized only (bicubic) unless
                // --upscale-quest-videos runs them through the upscaler as the intro is; they need RAD's Bink compressor,
                // ffmpeg and the Python environment, not the upscale model.
                bool canEncode = File.Exists(python) && File.Exists(script) && File.Exists(Setup.Dependencies.RadVideo) && File.Exists(Setup.Dependencies.Ffmpeg);
                if (found.ContainsKey(Game.Daggerfall) && canEncode && !opts.ContainsKey("no-quest-videos"))
                {
                    bool upscaleAnims = canRemaster && opts.ContainsKey("upscale-quest-videos");
                    foreach (var vid in Directory.GetFiles(arena2, "ANIM*.VID").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                    {
                        if (!int.TryParse(Path.GetFileNameWithoutExtension(vid)[4..], out int n)) continue;
                        if (opts.TryGetValue("anims", out var onlyAnims) && !onlyAnims.Split(',').Contains(n.ToString())) continue;
                        string target = Path.Combine(videoDir, $"TamrielMerge_DfAnim{n}.bik");
                        if (File.Exists(target)) continue;
                        string frames = Path.Combine(work, $"dfanim{n}-frames");
                        if (!File.Exists(Path.Combine(frames, "audio.wav"))) Daggerfall.DfVid.Export(vid, frames);
                        if (upscaleAnims) RemasterTo($"DfAnim{n}", target, frames, "--fps", "30");
                        else RemasterTo($"DfAnim{n}", target, frames, "--fps", "30", "--plain");
                    }
                    // The Daedra Princes' appearances when a coven summons them (ARENA2's FLC animations, through ffmpeg).
                    foreach (var d in Skyrim.DfQuestBuilder.Daedra)
                    {
                        string flc = Path.Combine(arena2, d.Video + ".FLC");
                        if (!File.Exists(flc) || opts.TryGetValue("anims", out var onlyVideos) && !onlyVideos.Split(',').Contains(d.Video, StringComparer.OrdinalIgnoreCase)) continue;
                        string target = Path.Combine(videoDir, Skyrim.DfQuestBuilder.DaedraVideoName(d.Video));
                        if (upscaleAnims) RemasterTo($"DfDaedra{d.Video}", target, flc, "--loop", "2");
                        else RemasterTo($"DfDaedra{d.Video}", target, flc, "--loop", "2", "--plain");
                    }
                }
                if (intros && found.ContainsKey(Game.Redguard) && canRemaster)
                {
                    string smk = Path.Combine(work, "INTRO.SMK");
                    if (!File.Exists(smk))
                    {
                        using var cd = new Redguard.RgCdImage(Path.Combine(GameInstalls.DataDir(Game.Redguard)!, "REDGUARD.bin"));
                        File.WriteAllBytes(smk, cd.Read("INTRO.SMK") ?? throw new FileNotFoundException("INTRO.SMK on Redguard's CD"));
                    }
                    Remaster("Redguard", smk, "--prescale", "640x480");
                    RtLook("Redguard", "auto", clean: false);
                    // The story's movies (QueueMovie n, WonGame's ending), their lines drawn on (RgMovies).
                    using var disc = new Redguard.RgCdImage(Path.Combine(GameInstalls.DataDir(Game.Redguard)!, "REDGUARD.bin"));
                    foreach (var movie in Redguard.RgMovies.Load(GameInstalls.DataDir(Game.Redguard)!).Where(m => m.Number > 0))
                    {
                        string movieWork = Path.Combine(work, $"redguard-movie{movie.Number}");
                        Directory.CreateDirectory(movieWork);
                        string file = Path.Combine(movieWork, movie.File);
                        if (!File.Exists(file))
                        {
                            if (disc.Read(movie.File) is not { } bytes) { Console.WriteLine($"Redguard: {movie.File} not on the CD"); continue; }
                            File.WriteAllBytes(file, bytes);
                        }
                        string subs = Path.Combine(movieWork, "lines.json");
                        File.WriteAllText(subs, Redguard.RgMovies.SubtitlesJson(movie));
                        var extra = new List<string> { "--prescale", "640x480", "--subtitles", subs };
                        // Resized only, as Daggerfall's quest videos are, unless --upscale-quest-videos (2026-10-16: on the
                        // CPU the upscale took nearly two hours for movie 1's 2,472 frames; ten movies would take a day).
                        if (opts.ContainsKey("no-upscale") || !opts.ContainsKey("upscale-quest-videos")) extra.Add("--plain");
                        RemasterTo($"Redguard movie {movie.Number}", Path.Combine(videoDir, $"TamrielMerge_RedguardMovie{movie.Number}.bik"), file, [.. extra]);
                    }
                }
                if (intros && GameInstalls.DataDir(Game.Morrowind) is { } mw)
                {
                    string intro = Path.Combine(mw, "Video", "mw_intro.bik");
                    if (canRemaster) Remaster("Morrowind", intro); else CopyOriginal("Morrowind", intro);
                }
                if (intros && GameInstalls.DataDir(Game.Oblivion) is { } ob)
                {
                    string intro = Path.Combine(ob, "Video", "OblivionIntro.bik");
                    if (canRemaster) Remaster("Oblivion", intro, "--prescale", "960x540", "--size", "1920x1080"); else CopyOriginal("Oblivion", intro);
                    RtLook("Oblivion", "auto", clean: false);
                }
                Console.WriteLine($"intros in {videoDir}: {string.Join(", ", Directory.GetFiles(videoDir, "*.bik").Select(Path.GetFileName))}");
                return 0;
            }
            case "voice-relip":
            {
                // Gives every voice file under --dir that lacks lip sync one (LipGenerator retried with simplified text),
                // taking each line's text from the plugin's dialogue (--plugin).
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var texts = Skyrim.VoicePlanner.Plan(plugin, skyrimData, out _).GroupBy(j => j.File, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Text, StringComparer.OrdinalIgnoreCase);
                var (fixedCount, failed, how) = Skyrim.VoiceFiles.Relip(skyrimData, opts["dir"], rel => texts.GetValueOrDefault(rel));
                Console.WriteLine($"{fixedCount} voice files given lip sync ({string.Join(", ", how.Select(kv => $"{kv.Value} with {kv.Key}"))}); {failed.Count} still without");
                foreach (var f in failed.Take(30)) Console.WriteLine($"  {f}");
                return 0;
            }
            case "setup":
            {
                // The converter's outside tools (ffmpeg, RAD Video Tools, Python with PyTorch/XTTS, Real-ESRGAN models) under
                // .	ools, installing what is missing. --check only reports; --cpu installs the CPU build of PyTorch.
                if (opts.ContainsKey("check"))
                {
                    foreach (var st in Setup.Dependencies.Check(skyrimData)) Console.WriteLine($"  {(st.Ready ? "ready  " : "MISSING")} {st.Name}: {st.Detail}");
                    return 0;
                }
                Console.WriteLine($"Setting up tools in {Setup.Dependencies.Root}");
                var left = Setup.Dependencies.Install(skyrimData, opts.ContainsKey("cpu"));
                foreach (var p in left) Console.WriteLine($"  still needed: {p}");
                Console.WriteLine(left.Count == 0 ? "All tools ready." : "Some tools are missing: the conversion still runs, without the parts that need them.");
                Console.WriteLine("Voices are generated with Coqui XTTS-v2, under the Coqui Public Model License (non-commercial use).");
                Console.WriteLine("The intros' depth model is Depth Anything V2 Base (CC BY-NC 4.0, non-commercial use); the PBR tool's normals model is DeepBump (GPL-3.0), downloaded, not shipped.");
                return 0;
            }
            case "ck-check":
            {
                // Dev aid: the Creation Kit check and prompt against --data (a Skyrim Data folder; default the real one).
                var choice = Setup.Dependencies.EnsureCreationKit(opts.GetValueOrDefault("data", skyrimData), prompt: !opts.ContainsKey("no-prompt"));
                Console.WriteLine($"result: {choice}");
                return 0;
            }
            case "mods":
            {
                // A game's plugins (--game Morrowind|Oblivion): [x] = converted. --set a,b,... chooses exactly those;
                // --enable / --disable a,b change the choice; --reset goes back to the default (official + enabled in game).
                var game = Enum.Parse<Game>(opts.GetValueOrDefault("game", "Morrowind"), ignoreCase: true);
                if (!Mods.ModCatalog.Supports(game)) { Console.WriteLine($"{game} has no plugin mods."); return 0; }
                string dir = GameInstalls.DataDir(game) ?? throw new DirectoryNotFoundException($"{game} isn't installed through Steam or GOG");
                var current = Mods.ModCatalog.Saved(game) ?? Mods.ModCatalog.DefaultSelection(game, dir);
                List<string> Names(string key) => opts.GetValueOrDefault(key, "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
                if (opts.ContainsKey("reset")) Mods.ModCatalog.Save(game, null);
                else if (opts.ContainsKey("set")) Mods.ModCatalog.Save(game, Names("set"));
                else if (opts.ContainsKey("enable") || opts.ContainsKey("disable"))
                    Mods.ModCatalog.Save(game, current.Concat(Names("enable")).Except(Names("disable"), StringComparer.OrdinalIgnoreCase).Distinct(StringComparer.OrdinalIgnoreCase));
                var problems = new List<string>();
                var selected = Mods.ModCatalog.Selected(game, dir, problems).ToHashSet(StringComparer.OrdinalIgnoreCase);
                Console.WriteLine($"{game} plugins in {dir} ({(Mods.ModCatalog.Saved(game) is null ? "default: official + enabled in game" : "your selection")}):");
                foreach (var p in Mods.ModCatalog.List(game, dir))
                    Console.WriteLine($"  [{(selected.Contains(p.File) ? "x" : " ")}] {p.File,-34} {(p.IsMaster ? "master" : "plugin"),-6} {(p.Official ? "official" : p.Standalone(game) ? "standalone" : "mod"),-10} {(p.EnabledInGame ? "enabled in game" : ""),-15} {(p.Masters.Count > 0 ? "needs " + string.Join(", ", p.Masters) : "")}");
                foreach (var m in problems) Console.WriteLine($"  note: {m}");
                // Script extender use: --scan checks every non-official plugin's scripts.
                Console.WriteLine($"  {Mods.ScriptExtenders.Name(game)} installed: {(Mods.ScriptExtenders.Installed(game, dir) ? "yes" : "no")}");
                if (game == Game.Morrowind && Mods.ScriptExtenders.LuaMods(dir) is { Count: > 0 } lua) Console.WriteLine($"  MWSE-Lua mods (not plugins): {string.Join(", ", lua)}");
                if (opts.ContainsKey("scan"))
                    foreach (var p in Mods.ModCatalog.List(game, dir).Where(p => !p.Official || opts.ContainsKey("all")))
                    {
                        var use = Mods.ScriptExtenders.Scan(game, Path.Combine(dir, p.File));
                        Console.WriteLine($"  {p.File}: {(use is null ? "no script extender" : $"uses {use.Extender} in {use.Scripts} of {use.TotalScripts} scripts ({string.Join(", ", use.Examples)})")}");
                    }
                return 0;
            }
            case "ob-script-src":
            {
                // Dev aid: Oblivion script sources from a plugin (--plugin file in Oblivion's Data), --id regex or --obse (only
                // scripts using OBSE), --max N.
                string dir = GameInstalls.DataDir(Game.Oblivion)!;
                using var plugin = Mutagen.Bethesda.Oblivion.OblivionMod.CreateFromBinaryOverlay(Path.Combine(dir, opts.GetValueOrDefault("plugin", "Oblivion.esm")), Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("id", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                int shown = 0, max = int.Parse(opts.GetValueOrDefault("max", "3"));
                foreach (var sc in plugin.Scripts)
                {
                    if (!rx.IsMatch(sc.EditorID ?? "")) continue;
                    if (opts.ContainsKey("obse") && Mods.ScriptExtenders.ExtenderOpcodes(sc.Fields.CompiledScript is { } c ? c.ToArray() : []).Count == 0) continue;
                    Console.WriteLine($"==== {sc.EditorID} ({sc.Fields.MetadataSummary.Type}) ====");
                    Console.WriteLine(sc.Fields.SourceCode);
                    if (++shown >= max) break;
                }
                return 0;
            }
            case "ob-script-inventory":
            {
                // What Oblivion scripts use (--plugins a,b,... default: the mod selection): block types and functions with
                // counts, and what the parser couldn't read (--unparsed N shows examples). Names that are variables or
                // forms' EditorIDs aren't functions.
                string dir = GameInstalls.DataDir(Game.Oblivion)!;
                var names = opts.TryGetValue("plugins", out var pl) ? pl.Split(',').ToList() : Mods.ModCatalog.Selected(Game.Oblivion, dir);
                var blocks = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var functions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var unparsed = new List<string>();
                int scripts = 0, statements = 0, bad = 0;
                foreach (var file in names)
                {
                    using var plugin = Mutagen.Bethesda.Oblivion.OblivionMod.CreateFromBinaryOverlay(Path.Combine(dir, file), Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion);
                    var edids = plugin.EnumerateMajorRecords().Select(r => r.EditorID).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var sc in plugin.Scripts)
                    {
                        if (sc.Fields.SourceCode is not { } src) continue;
                        scripts++;
                        var parsed = Oblivion.ObScript.Parse(src);
                        var vars = parsed.Variables.Select(v => v.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                        foreach (var b in parsed.Blocks) blocks[b.Type] = blocks.GetValueOrDefault(b.Type) + 1;
                        void Count(Oblivion.ObScript.Expr? e)
                        {
                            switch (e)
                            {
                                case Oblivion.ObScript.Call c:
                                    if (!vars.Contains(c.Function) && !edids.Contains(c.Function)) functions[c.Function] = functions.GetValueOrDefault(c.Function) + 1;
                                    foreach (var a2 in c.Args) Count(a2);
                                    if (c.Function == "Call" && c.Args.FirstOrDefault() is Oblivion.ObScript.Name callee) { }
                                    break;
                                case Oblivion.ObScript.Unary u: Count(u.Operand); break;
                                case Oblivion.ObScript.Binary b2: Count(b2.Left); Count(b2.Right); break;
                                case Oblivion.ObScript.Index ix: Count(ix.Array); Count(ix.Key); break;
                            }
                        }
                        foreach (var st in parsed.AllStatements())
                        {
                            statements++;
                            switch (st)
                            {
                                case Oblivion.ObScript.Set set: Count(set.Target); Count(set.Value); break;
                                case Oblivion.ObScript.If i: foreach (var br in i.Branches) Count(br.Condition); break;
                                case Oblivion.ObScript.While w: Count(w.Condition); break;
                                case Oblivion.ObScript.ForEach fe: Count(fe.Collection); break;
                                case Oblivion.ObScript.CallStmt cs:
                                    if (!vars.Contains(cs.Call.Function)) functions[cs.Call.Function] = functions.GetValueOrDefault(cs.Call.Function) + 1;
                                    foreach (var a3 in cs.Call.Args) Count(a3);
                                    break;
                                case Oblivion.ObScript.Unparsed u:
                                    bad++;
                                    if (unparsed.Count < int.Parse(opts.GetValueOrDefault("unparsed", "15"))) unparsed.Add($"{sc.EditorID}:{u.Line}: {u.Text}   ({u.Reason})");
                                    break;
                            }
                        }
                    }
                }
                Console.WriteLine($"{scripts} scripts, {statements} statements, {bad} not parsed ({100.0 * bad / Math.Max(1, statements):F2}%)");
                Console.WriteLine($"block types: {string.Join(", ", blocks.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))}");
                var table = Oblivion.ObFunctions.Load(dir);
                Console.WriteLine($"function table: {table.VanillaCount} of Oblivion's (from Oblivion.exe), {table.ObseCount} of OBSE's");
                Console.WriteLine($"{functions.Count} functions; most used:");
                foreach (var (f, n) in functions.OrderByDescending(kv => kv.Value).Take(int.Parse(opts.GetValueOrDefault("top", "60"))))
                    Console.WriteLine($"  {n,6} {f}   {(table[f] is { } fn ? $"{fn.Source} {fn.Name}({string.Join(", ", fn.Params.Select(pp => (pp.Optional ? "[" : "") + pp.Type + (pp.Optional ? "]" : "")))})" : "UNKNOWN")}");
                var unknown = functions.Where(kv => table[kv.Key] is null).OrderByDescending(kv => kv.Value).ToList();
                Console.WriteLine($"{unknown.Count} names not in the table ({unknown.Sum(kv => kv.Value)} uses): {string.Join(", ", unknown.Take(25).Select(kv => $"{kv.Key} {kv.Value}"))}");
                foreach (var u in unparsed) Console.WriteLine($"  unparsed {u}");
                if (opts.TryGetValue("out", out var outFile))
                    File.WriteAllLines(outFile, functions.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value}	{kv.Key}"));
                return 0;
            }
            case "script-stats":
            {
                // Offline check of a built plugin (--plugin path): scripts attached (by name, with how many of their
                // properties are filled), dialogue responses testing disposition, results changing it, persuasion topics.
                using var plugin = Mutagen.Bethesda.Skyrim.SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], Mutagen.Bethesda.Skyrim.SkyrimRelease.SkyrimSE);
                var byScript = new Dictionary<string, (int Records, int Props)>();
                foreach (var r in plugin.EnumerateMajorRecords())
                {
                    var vm = r.GetType().GetProperty("VirtualMachineAdapter")?.GetValue(r) as Mutagen.Bethesda.Skyrim.IAVirtualMachineAdapterGetter;
                    if (vm is null) continue;
                    foreach (var s in vm.Scripts)
                    {
                        var t = byScript.GetValueOrDefault(s.Name);
                        byScript[s.Name] = (t.Records + 1, t.Props + s.Properties.Count);
                    }
                }
                int dispConditions = 0, dispResults = 0, responses = 0;
                foreach (var topic in plugin.DialogTopics)
                    foreach (var resp in topic.Responses)
                    {
                        responses++;
                        if (resp.Conditions.Any(c => c.Data is Mutagen.Bethesda.Skyrim.IGetActorValueConditionDataGetter { ActorValue: Mutagen.Bethesda.Skyrim.ActorValue.Variable09 })) dispConditions++;
                        if (resp.VirtualMachineAdapter?.Scripts.Any(s => s.Properties.Any(p => p.Name == "Disposition")) == true) dispResults++;
                    }
                Console.WriteLine($"{responses} responses: {dispConditions} test disposition, {dispResults} change it; persuasion topics: {string.Join(", ", plugin.DialogTopics.Where(t => t.EditorID?.Contains("Persua") == true).Select(t => $"{t.Name} ({t.Responses.Count})"))}");
                Console.WriteLine($"{byScript.Count} scripts attached:");
                foreach (var (name, (recs, props)) in byScript.OrderByDescending(kv => kv.Value.Records).Take(int.Parse(opts.GetValueOrDefault("top", "25"))))
                    Console.WriteLine($"  {recs,6} x {name} ({props} properties)");
                return 0;
            }
            case "ob-script-translate":
            {
                // Oblivion scripts (--plugins a,b,... default: the mod selection) translated into Papyrus in --out
                // (default out/obscripts): one OB_<script>.psc each, and coverage.txt — per plugin the share of statements
                // translated, and what's missing most. --compile compiles them against Skyrim's and SKSE's sources.
                string dir = GameInstalls.DataDir(Game.Oblivion)!;
                var names = opts.TryGetValue("plugins", out var pl) ? pl.Split(',').ToList() : Mods.ModCatalog.Selected(Game.Oblivion, dir);
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/obscripts"));
                string srcDir = Path.Combine(outDir, "Source");
                if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);
                Directory.CreateDirectory(srcDir);
                var mods = names.Select(f => (Mutagen.Bethesda.Oblivion.IOblivionModDisposableGetter)Mutagen.Bethesda.Oblivion.OblivionMod.CreateFromBinaryOverlay(Path.Combine(dir, f), Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion)).ToList();
                try
                {
                    var world = new Oblivion.ObScriptWorld(mods);
                    var table = Oblivion.ObFunctions.Load(dir);
                    world.Functions = table;
                    var missing = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                    var perPlugin = new Dictionary<string, (int Scripts, int Statements, int Translated)>(StringComparer.OrdinalIgnoreCase);
                    var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("id", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    int failed = 0;
                    foreach (var (key, sc) in world.Scripts)
                    {
                        if (sc.Fields.SourceCode is not { } src || sc.EditorID is not { } id || !rx.IsMatch(id)) continue;
                        string scriptKind = world.QuestScripts.Contains(key) ? "Quest" : sc.Fields.MetadataSummary.Type.ToString();
                        try
                        {
                            var result = new Oblivion.ObPapyrus(world, table).Translate(Oblivion.ObScript.Parse(src), id, scriptKind, world.OnActor(key));
                            File.WriteAllText(Path.Combine(srcDir, result.ScriptName + ".psc"), result.Source);
                            foreach (var (m, n) in result.Missing) missing[m] = missing.GetValueOrDefault(m) + n;
                            string plugin = world.ScriptPlugin[key];
                            var t = perPlugin.GetValueOrDefault(plugin);
                            perPlugin[plugin] = (t.Scripts + 1, t.Statements + result.Statements, t.Translated + result.Translated);
                        }
                        catch (Exception e) { failed++; Console.WriteLine($"  {id}: {e.Message}"); }
                    }
                    var report = new List<string>();
                    foreach (var (plugin, (s, st, tr)) in perPlugin.OrderBy(kv => kv.Key))
                        report.Add($"{plugin}: {s} scripts, {tr}/{st} statements translated ({100.0 * tr / Math.Max(1, st):F1}%)");
                    report.Add($"{failed} scripts failed to translate");
                    report.Add("Missing most:");
                    report.AddRange(missing.OrderByDescending(kv => kv.Value).Select(kv => $"  {kv.Value,6} {kv.Key}"));
                    File.WriteAllLines(Path.Combine(outDir, "coverage.txt"), report);
                    // What carries each script (carriers.txt): the record types naming it, and scripts nothing carries.
                    var carriers = new List<string>();
                    foreach (var (key, sc) in world.Scripts.OrderBy(kv => kv.Value.EditorID))
                    {
                        if (sc.Fields.SourceCode is null || sc.EditorID is not { } cid || !rx.IsMatch(cid)) continue;
                        string ckind = world.QuestScripts.Contains(key) ? "Quest" : sc.Fields.MetadataSummary.Type.ToString();
                        var list = world.Carriers.GetValueOrDefault(key) ?? [];
                        carriers.Add($"{cid} ({ckind}): {(list.Count == 0 ? "NOTHING" : string.Join(", ", list.GroupBy(c => c.Type).Select(g => $"{g.Key} x{g.Count()} [{string.Join(" ", g.Take(3).Select(c => c.EditorID))}]")))}");
                    }
                    File.WriteAllLines(Path.Combine(outDir, "carriers.txt"), carriers);
                    foreach (var line in report.Take(int.Parse(opts.GetValueOrDefault("top", "40")) + perPlugin.Count + 2)) Console.WriteLine(line);
                }
                finally { foreach (var m in mods) m.Dispose(); }
                if (opts.ContainsKey("compile"))
                {
                    var (compiled, output) = Oblivion.ObPluginBuilder.CompilePapyrus(skyrimData, srcDir, Path.Combine(outDir, "Scripts"));
                    File.WriteAllText(Path.Combine(outDir, "compile.txt"), output);
                    foreach (var l in output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Contains("): ")).Take(int.Parse(opts.GetValueOrDefault("errors", "40")))) Console.WriteLine(l);
                    Console.WriteLine(compiled is null ? output : $"{compiled.Count} of {Directory.GetFiles(srcDir, "*.psc").Length} scripts compiled");
                }
                return 0;
            }
            case "ob-script-attach-test":
            {
                // Dev aid: Oblivion's scripts attached without the world (minutes, not hours): translated, compiled and
                // attached to the quests, magic, actors and items they need, plus a few interiors (--cells a,b), into a
                // plugin in --out; reports what carries scripts and what the attaching couldn't do.
                string dataDir = GameInstalls.DataDir(Game.Oblivion) ?? throw new DirectoryNotFoundException("Oblivion isn't installed through Steam or GOG");
                string outDir = Path.GetFullPath(opts["out"]);
                using var builder = new Oblivion.ObPluginBuilder(new Oblivion.ObData(dataDir, Mods.ModCatalog.Selected(Game.Oblivion, dataDir, [])), outDir) { SkyrimData = skyrimData };
                builder.PrepareScripts();
                foreach (var id in opts.GetValueOrDefault("cells", "").Split(',', StringSplitOptions.RemoveEmptyEntries))
                    if (builder.FindInterior(id) is { } cell) builder.BuildInterior(cell);
                builder.ConvertAllMagic();
                builder.BuildDialogue();
                int compiled = builder.AttachScripts();
                builder.Write();
                var m = builder.Mod;
                Console.WriteLine($"{compiled} scripts compiled, {builder.ScriptsAttached} attached, {builder.PropertiesFilled} properties filled; {builder.PackagesMade} packages, {builder.SoundsMade} sounds");
                var carriers = m.EnumerateMajorRecords().Select(r => (r, vm: r.GetType().GetProperty("VirtualMachineAdapter")?.GetValue(r) as Mutagen.Bethesda.Skyrim.IAVirtualMachineAdapterGetter))
                    .Where(x => x.vm is not null && x.vm.Scripts.Any(sc => sc.Name.StartsWith("OB_") || sc.Name.StartsWith("TMOb"))).ToList();
                foreach (var g in carriers.GroupBy(x => x.r.GetType().Name).OrderByDescending(g => g.Count())) Console.WriteLine($"  {g.Count(),6} {g.Key}");
                foreach (var p in builder.Problems.Where(p => p.StartsWith("script")).Distinct().Take(int.Parse(opts.GetValueOrDefault("problems", "40")))) Console.WriteLine($"  problem: {p}");
                return 0;
            }
            case "obse-opcode-check":
            {
                // Dev aid: checks the OBSE command table's opcodes against compiled scripts: each OBSE opcode in a script's
                // bytecode should be a function its source names.
                string dir = GameInstalls.DataDir(Game.Oblivion)!;
                var table = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Oblivion", "Data", "obse_commands.json"))).RootElement
                    .EnumerateArray().ToDictionary(e => e.GetProperty("opcode").GetInt32(), e => (e.GetProperty("name").GetString()!, e.GetProperty("alt").GetString()!));
                int hits = 0, misses = 0;
                var missed = new Dictionary<string, int>();
                foreach (var file in opts.GetValueOrDefault("plugins", "UVIII.esp,Nehrim.esm").Split(','))
                {
                    using var plugin = Mutagen.Bethesda.Oblivion.OblivionMod.CreateFromBinaryOverlay(Path.Combine(dir, file), Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion);
                    foreach (var sc in plugin.Scripts)
                    {
                        if (sc.Fields.SourceCode is not { } src) continue;
                        foreach (var op in Mods.ScriptExtenders.ExtenderOpcodes(sc.Fields.CompiledScript is { } c ? c.ToArray() : []))
                        {
                            var (name, alt) = table.TryGetValue(op, out var t) ? t : ($"0x{op:X4}?", "");
                            bool named = src.Contains(name, StringComparison.OrdinalIgnoreCase) || alt.Length > 0 && src.Contains(alt, StringComparison.OrdinalIgnoreCase);
                            if (named) hits++; else { misses++; missed[$"0x{op:X4} {name}"] = missed.GetValueOrDefault($"0x{op:X4} {name}") + 1; }
                        }
                    }
                }
                Console.WriteLine($"{hits} opcode uses match their source, {misses} don't");
                foreach (var (k, n) in missed.OrderByDescending(kv => kv.Value).Take(15)) Console.WriteLine($"  {n,5} {k}");
                return 0;
            }
            case "games":
            {
                // The original game installs found on this machine (Steam, GOG, usual folders); undetected games are
                // left out of the conversion and the story order.
                var found = GameInstalls.Detect();
                Console.WriteLine($"{"Skyrim SE",-11} {GameInstalls.SkyrimData() ?? "not installed through Steam or GOG"}");
                foreach (var g in Enum.GetValues<Game>())
                    Console.WriteLine($"{g,-11} {(found.TryGetValue(g, out var install) ? $"{install.Store}: {install.DataDir}" : "not installed through Steam or GOG")}");
                return 0;
            }
            case "mw-nif-census":
            {
                // Block types used by Morrowind's meshes (loose and archived). NIF 4.0.0.2 has no block sizes, so the
                // reader must know every type; this lists them by the length-prefixed type names in each file.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                var data = Morrowind.Tes3Data.Open(dataFiles);
                var counts = new Dictionary<string, (int Blocks, int Files)>();
                var rx = new System.Text.RegularExpressions.Regex("^(Ni|RootCollisionNode|AvoidNode|BS|Bone)[A-Za-z0-9]*$");
                int files = 0;
                foreach (var name in data.List("meshes").Where(n => n.EndsWith(".nif") || n.EndsWith(".kf")))
                {
                    var bytes = data.Read(name)!;
                    files++;
                    var seen = new HashSet<string>();
                    for (int i = 0; i + 4 < bytes.Length; i++)
                    {
                        int len = BitConverter.ToInt32(bytes, i);
                        if (len < 6 || len > 40 || i + 4 + len > bytes.Length || bytes[i + 4] is not ((byte)'N' or (byte)'R' or (byte)'A' or (byte)'B')) continue;
                        string t = System.Text.Encoding.ASCII.GetString(bytes, i + 4, len);
                        if (!rx.IsMatch(t)) continue;
                        var c = counts.GetValueOrDefault(t);
                        counts[t] = (c.Blocks + 1, c.Files + (seen.Add(t) ? 1 : 0));
                        i += 3 + len;
                    }
                }
                Console.WriteLine($"{files} mesh files");
                foreach (var (t, c) in counts.OrderByDescending(k => k.Value.Files))
                    Console.WriteLine($"  {t,-34} {c.Files,6} files {c.Blocks,7} blocks");
                return 0;
            }
            case "mw-nif-check":
            {
                // Parses every Morrowind mesh with the NIF 4.0.0.2 reader; failures grouped by cause.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                var data = Morrowind.Tes3Data.Open(dataFiles);
                var fails = new Dictionary<string, List<string>>();
                int ok = 0;
                foreach (var name in data.List("meshes").Where(n => n.EndsWith(".nif") || n.EndsWith(".kf")))
                {
                    try { Morrowind.Nif4.Read(data.Read(name)!, name); ok++; }
                    catch (Exception e)
                    {
                        string msg = System.Text.RegularExpressions.Regex.Replace(e.Message.Replace(name + ": ", ""), @"\d+", "#");
                        (fails.TryGetValue(msg, out var l) ? l : fails[msg] = []).Add(name);
                    }
                }
                Console.WriteLine($"{ok} parsed, {fails.Values.Sum(l => l.Count)} failed");
                foreach (var (msg, names) in fails.OrderByDescending(f => f.Value.Count).Take(25))
                    Console.WriteLine($"  {names.Count,5}  {msg}   e.g. {names[0]}");
                return 0;
            }
            case "mw-convert-meshes":
            {
                // Converts Morrowind models matching --filter (regex on the path) to Skyrim SE under --out, with their
                // textures. Prints shapes/collision per model and failures.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                string outDir = opts.GetValueOrDefault("out", "out/mw");
                var data = Morrowind.Tes3Data.Open(dataFiles);
                var textures = new Morrowind.MwTextures(data, outDir);
                var converter = new Morrowind.MwMeshConverter(data, textures);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("filter", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                int ok = 0, failed = 0;
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var name in data.List("meshes").Where(n => n.EndsWith(".nif") && rx.IsMatch(n)))
                {
                    try
                    {
                        var r = converter.Convert(name, outDir);
                        ok++;
                        if (opts.ContainsKey("verbose")) Console.WriteLine($"  {name}: {r.Shapes} shapes, {r.CollisionPolys} collision polys, size {r.Max - r.Min}{(r.Animated ? " animated" : "")}{(r.Skinned ? " skinned" : "")}");
                    }
                    catch (Exception e) { failed++; Console.WriteLine($"  FAIL {name}: {e.Message}"); }
                }
                Console.WriteLine($"{ok} converted, {failed} failed, {textures.Converted} textures, {textures.Missing.Count} missing textures in {sw.Elapsed.TotalSeconds:F0}s");
                foreach (var m in textures.Missing.Distinct().Take(10)) Console.WriteLine($"  missing texture {m}");
                return failed == 0 ? 0 : 1;
            }
            case "mw-build":
            {
                // Upscaled textures (tools/upscale/out, from upscale-textures, or --upscaled DIR) replace the originals where present.
                string upscaled = opts.GetValueOrDefault("upscaled", UpscaleFolder(Game.Morrowind));
                if (Directory.Exists(upscaled) && !opts.ContainsKey("no-upscale")) Morrowind.MwTextures.UpscaleCache = Path.GetFullPath(upscaled);
                // Builds Morrowind.esm (ours) from the original install (plus the selected mods) into --out: every
                // interior (or those whose names match --cells, a regex) with its navmesh; the exterior worldspace
                // MWVvardenfell (Vvardenfell and Solstheim: terrain, references, regions with Morrowind's weathers and
                // climates, its water, navmeshes, terrain LOD; --no-exterior skips it, --exterior-box limits it); load
                // doors linked; items, containers, NPCs, creatures, worn armour and clothing; dialogue (--no-dialogue)
                // and translated scripts (--no-scripts); the start marker. Models and textures go under --out too.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind) ?? throw new DirectoryNotFoundException("Morrowind isn't installed through Steam or GOG");
                string outDir = opts.GetValueOrDefault("out", "out/mwmod");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // The plugins to convert: --plugins a,b,... or the mod selection (TamrielMerge.mods.json, else the official
                // game plus what Morrowind has enabled), with their masters, in load order.
                var modProblems = new List<string>();
                var mwPlugins = opts.TryGetValue("plugins", out var mwList) ? mwList.Split(',').Select(x => x.Trim()).ToList() : Mods.ModCatalog.Selected(Game.Morrowind, dataFiles, modProblems);
                foreach (var note in modProblems) Console.WriteLine($"  mods: {note}");
                var world = Morrowind.MwWorld.Load(dataFiles, mwPlugins);
                Console.WriteLine($"Loaded {string.Join(", ", world.Plugins.Select(p => p.FileName))}: {world.Objects.Count} objects, {world.Interiors.Count} interiors, {world.Exteriors.Count} exterior cells ({sw.Elapsed.TotalSeconds:F0}s)");
                // --exterior-box "x0,y0,x1,y1" (Morrowind cell grid): only those exterior cells, for test builds.
                if (opts.TryGetValue("exterior-box", out var box))
                {
                    var b = box.Split(',').Select(int.Parse).ToArray();
                    bool Inside((int X, int Y) g) => g.X >= b[0] && g.Y >= b[1] && g.X <= b[2] && g.Y <= b[3];
                    foreach (var g in world.Exteriors.Keys.Where(g => !Inside(g)).ToList()) world.Exteriors.Remove(g);
                    foreach (var g in world.Lands.Keys.Where(g => !Inside(g)).ToList()) world.Lands.Remove(g);
                    Console.WriteLine($"  exterior box {box}: {world.Exteriors.Count} exterior cells");
                }
                var builder = new Morrowind.MwPluginBuilder(world, Morrowind.Tes3Data.Open(dataFiles, mwPlugins), outDir);
                builder.SkyrimData = skyrimData;
                // Scripts are translated first: the references they name are placed persistent, scripted activators are
                // activators. --no-scripts leaves them out (and the dialogue's results to TMMwInfo's fixed set).
                bool mwScripts = !opts.ContainsKey("no-scripts");
                if (mwScripts)
                {
                    builder.PrepareScripts();
                    Console.WriteLine($"  scripts: {builder.ScriptCount} translated, {builder.ScriptStatementsTranslated}/{builder.ScriptStatements} statements ({100.0 * builder.ScriptStatementsTranslated / Math.Max(1, builder.ScriptStatements):F1}%) ({sw.Elapsed.TotalSeconds:F0}s)");
                }
                // Worn armour and clothing meshes, all at once in parallel (the records come with the items later).
                var (wornMade, wornFailed) = builder.PrepareWorn();
                Console.WriteLine($"  worn meshes: {wornMade} items re-rigged to Skyrim's skeleton, {wornFailed} failed ({sw.Elapsed.TotalSeconds:F0}s)");
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("cells", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                int cells = 0, refs = 0;
                foreach (var cell in world.Interiors.Values.Where(c => rx.IsMatch(c.Name)))
                {
                    var built = builder.BuildInterior(cell);
                    cells++; refs += built.Temporary.Count + built.Persistent.Count;
                    if (opts.ContainsKey("verbose")) Console.WriteLine($"  {cell.Name}: {built.Temporary.Count + built.Persistent.Count} references of {cell.Refs.Count} ({built.EditorID})");
                }
                Console.WriteLine($"  {cells} interiors, {refs} references, {builder.Navmeshes} navmeshes ({builder.NavmeshTriangles} triangles) ({sw.Elapsed.TotalSeconds:F0}s)");
                // --doors <cell name>: that cell's teleporting doors and where they lead.
                if (opts.TryGetValue("doors", out var doorCell) && world.Interiors.TryGetValue(doorCell, out var dc))
                    foreach (var r in dc.Refs.Where(r => world.Objects.GetValueOrDefault(r.BaseId)?.Type == "DOOR"))
                        Console.WriteLine(r.DoorDestination is { } dd
                            ? $"  door {r.BaseId} at {r.Position} -> {r.DoorDestinationCell ?? "(outside)"} at {dd.Position}"
                            : $"  swinging door {r.BaseId} at {r.Position} rot {r.Rotation}");
                // --no-exterior skips the worldspace (faster when working on interiors).
                if (!opts.ContainsKey("no-exterior"))
                {
                    var ws = builder.BuildExteriors(skyrimData);
                    Console.WriteLine($"  exterior {ws.EditorID}: {ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).Count()} cells, {ws.TopCell!.Persistent.Count} doors, {builder.ExteriorNavmeshes} navmeshes ({builder.ExteriorEdgeLinks} edge links), {builder.TerrainLodChunks} terrain LOD chunks ({sw.Elapsed.TotalSeconds:F0}s)");
                    Console.WriteLine($"  weather: {builder.WeatherCount} Morrowind weathers, {builder.ClimateCount} climates (one per region + the worldspace's), {builder.Mod.Regions.Count} regions, water {builder.Mod.Waters.FirstOrDefault()?.EditorID}");
                }
                var missing = builder.MissingTextures.ToList();
                Console.WriteLine($"  missing textures: {missing.Count}{(missing.Count > 0 ? " (" + string.Join(", ", missing.Take(15)) + ")" : "")}");
                var (linked, unlinked) = builder.LinkDoors();
                Console.WriteLine($"  doors: {linked} load doors linked, {unlinked} lead outside or to unbuilt cells");
                Console.WriteLine($"  effects: {builder.EffectCount} vanilla effects standing in for particle systems");
                Console.WriteLine($"  locks: {builder.LockCount} locked, {builder.TrapCount} trapped (Morrowind security)");
                if (!opts.ContainsKey("no-dialogue"))
                {
                    builder.BuildDialogue();
                    Console.WriteLine($"  dialogue: {builder.DialogueTopics} topics, {builder.DialogueResponses} responses ({builder.DialogueLines} lines), {builder.JournalQuests} journal quests ({sw.Elapsed.TotalSeconds:F0}s)");
                }
                if (mwScripts)
                {
                    int compiled = builder.AttachScripts(msg => Console.WriteLine($"  scripts: {msg} ({sw.Elapsed.TotalSeconds:F0}s)"));
                    Console.WriteLine($"  scripts: {compiled} compiled, {builder.ScriptsAttached} attached, {builder.ResultScriptsAttached} dialogue results, {builder.PropertiesFilled} properties filled ({builder.PropertiesUnfilled} without a record) ({sw.Elapsed.TotalSeconds:F0}s)");
                }
                Console.WriteLine(builder.Start is { } st
                    ? $"  start: {st.Cell} at {st.Position}, {st.Degrees} degrees -> {builder.StartMarker.FormKey}{(builder.StartMarker.Placement is { } placed ? $" on the floor at {placed.Position}" : " (cell not built: marker unplaced)")}"
                    : "  start: not found in the CharGen script");
                // --headroom "cell;x,y,z;dx,dy;steps": floor, ceiling and the gap between them along a line (a body needs
                // about 130 units to pass standing).
                if (opts.TryGetValue("headroom", out var probe))
                {
                    var parts = probe.Split(';');
                    var at = parts[1].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var step = parts[2].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                    var solid = builder.CellSolids[parts[0]];
                    for (int i = 0; i <= int.Parse(parts[3]); i++)
                    {
                        var p = new System.Numerics.Vector3(at[0] + step[0] * i, at[1] + step[1] * i, at[2]);
                        var (floor, ceiling) = Morrowind.MwPluginBuilder.Clearance(solid, p);
                        Console.WriteLine($"  ({p.X,6:F0},{p.Y,6:F0}) floor {floor,7:F0} ceiling {ceiling,7:F0} gap {(ceiling - floor),5:F0}");
                    }
                }
                var m = builder.Mod;
                Console.WriteLine($"  records: {m.Statics.Count} statics, {m.Doors.Count} doors, {m.Activators.Count} swinging doors, {m.Lights.Count} lights, {m.Containers.Count} containers, " +
                    $"{m.MiscItems.Count} misc, {m.Keys.Count} keys, {m.Books.Count} books, {m.Weapons.Count} weapons, {m.Ammunitions.Count} ammo, {m.Armors.Count} armour/clothing, " +
                    $"{m.Ingestibles.Count} potions, {m.Ingredients.Count} ingredients, {m.LeveledItems.Count} leveled lists, {m.Npcs.Count} actors, {m.LeveledNpcs.Count} leveled actor lists");
                Console.WriteLine($"  worn: {builder.WornConverted} armour/clothing with Morrowind's own worn look (re-rigged to Skyrim's skeleton), {builder.WornFallbacks} kept their Skyrim template's look (see problems)");
                builder.Write();
                Console.WriteLine($"  barks: {builder.BarkCount} recorded lines in {builder.BarkTopicCount} voice topics (Morrowind's own recordings)");
                // Morrowind's law (MwLaw): its crime faction and jail, who answers to it, the arrest greetings, TMMwLaw's markers.
                if (m.Factions.FirstOrDefault(f => f.EditorID == "MWCrimeFaction") is { } mwCrime)
                {
                    var lawScript = m.Quests.FirstOrDefault(q => q.EditorID == "MWLaw")?.VirtualMachineAdapter?.Scripts.FirstOrDefault();
                    int lawMarkers = lawScript?.Properties.OfType<ScriptObjectListProperty>().FirstOrDefault(p => p.Name == "Markers")?.Objects.Count ?? 0;
                    var people = m.Npcs.Where(n => n.CrimeFaction.FormKey == mwCrime.FormKey).ToList();
                    Console.WriteLine($"  law: {mwCrime.EditorID} (murder {mwCrime.CrimeValues?.Murder}, assault {mwCrime.CrimeValues?.Assault}, trespass {mwCrime.CrimeValues?.Trespass}, pickpocket {mwCrime.CrimeValues?.Pickpocket}, theft x{mwCrime.CrimeValues?.StealMult}); "
                        + $"jail {(mwCrime.PlayerInventoryContainer.IsNull ? "none" : "set")}; {people.Count} people answer to it, {people.Count(n => n.Factions.Any(r => r.Faction.FormKey == Skyrim.CrimeFactions.IsGuardFaction))} of them guards; "
                        + $"{builder.ArrestResponses} arrest greetings; TMMwLaw: {lawMarkers} prison markers, {lawScript?.Properties.Count ?? 0} properties");
                }
                var problems = builder.Problems.Distinct().ToList();
                foreach (var p in problems.Take(60)) Console.WriteLine($"  problem: {p}");
                if (problems.Count > 60) Console.WriteLine($"  ... {problems.Count - 60} more problems");
                Console.WriteLine($"Wrote {Path.GetFullPath(Path.Combine(outDir, Morrowind.MwPluginBuilder.PluginName))} in {sw.Elapsed.TotalSeconds:F0}s");
                return 0;
            }
            case "ob-stats":
            {
                // The Oblivion install as the converter sees it: active plugins in load order with record counts, and
                // the official archives' file counts. --nif-test N loads N meshes with nifly to check it reads them.
                string dataDir = GameInstalls.DataDir(Game.Oblivion) ?? throw new DirectoryNotFoundException("Oblivion isn't installed through Steam or GOG");
                var data = new Oblivion.ObData(dataDir);
                foreach (var p in data.Plugins)
                {
                    using var mod = Mutagen.Bethesda.Oblivion.OblivionMod.CreateFromBinaryOverlay(Path.Combine(dataDir, p), Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion);
                    var counts = mod.EnumerateMajorRecords().GroupBy(r => r.GetType().Name.Replace("BinaryOverlay", "")).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}");
                    Console.WriteLine($"{p}: masters [{string.Join(", ", mod.ModHeader.MasterReferences.Select(m => m.Master))}]");
                    Console.WriteLine("  " + string.Join(" ", counts.Take(30)));
                }
                foreach (var bsa in data.Archives)
                    Console.WriteLine($"{Path.GetFileName(bsa.FilePath)}: {bsa.Names.Count()} files");
                if (opts.TryGetValue("nif-test", out var nifTest))
                {
                    var tmp = Path.Combine(Path.GetTempPath(), "tm-ob-nif.nif");
                    int ok = 0, bad = 0;
                    foreach (var n in data.List("meshes").Where(n => n.EndsWith(".nif")).Where((_, i) => i % 50 == 0).Take(int.Parse(nifTest)))
                    {
                        File.WriteAllBytes(tmp, data.Read(n)!);
                        using var nif = new nifly.NifFile();
                        if (nif.Load(tmp) == 0 && nif.GetShapes().Count > 0) ok++;
                        else { bad++; if (bad <= 5) Console.WriteLine($"  nifly could not read {n}"); }
                    }
                    Console.WriteLine($"nifly: {ok} read with shapes, {bad} failed or empty");
                }
                return 0;
            }
            case "ob-cell":
            {
                // Dev aid: Oblivion interior cells whose EditorID or name matches --name, with reference counts and the
                // most-used base objects (type, EditorID, model).
                string dataDir = GameInstalls.DataDir(Game.Oblivion)!;
                using var ob = Mutagen.Bethesda.Oblivion.OblivionMod.CreateFromBinaryOverlay(Path.Combine(dataDir, "Oblivion.esm"), Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "Prison"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var records = ob.EnumerateMajorRecords().GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.First());
                // --player: where the player reference (FormID 14) starts a new game.
                if (opts.ContainsKey("player"))
                {
                    foreach (var c in ob.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                        foreach (var r in c.Persistent.Concat(c.Temporary).Where(r => r.FormKey.ID == 0x14))
                            Console.WriteLine($"{r.GetType().Name} {r.FormKey} in {c.EditorID} \"{c.Name}\" at {(r as Mutagen.Bethesda.Oblivion.IPlacedNpcGetter)?.Location?.Position ?? (r as Mutagen.Bethesda.Oblivion.IPlacedObjectGetter)?.Location?.Position} rot {(r as Mutagen.Bethesda.Oblivion.IPlacedNpcGetter)?.Location?.Rotation}");
                    return 0;
                }
                // --refs: references (in any interior) whose own EditorID matches --refs, with their cell and position.
                if (opts.TryGetValue("refs", out var refPattern))
                {
                    var refRx = new System.Text.RegularExpressions.Regex(refPattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    foreach (var c in ob.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                        foreach (var r in c.Persistent.Concat(c.Temporary).OfType<Mutagen.Bethesda.Oblivion.IPlacedObjectGetter>().Where(r => refRx.IsMatch(r.EditorID ?? "")))
                            Console.WriteLine($"{r.EditorID} {r.FormKey} base {records.GetValueOrDefault(r.Base.FormKey)?.EditorID} in {c.EditorID} \"{c.Name}\" at {r.Location?.Position} rot {r.Location?.Rotation}");
                    return 0;
                }
                foreach (var cell in ob.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).Where(c => rx.IsMatch(c.EditorID ?? "") || rx.IsMatch(c.Name ?? "")))
                {
                    var refs = cell.Persistent.Concat(cell.Temporary).Concat(cell.VisibleWhenDistant).ToList();
                    Console.WriteLine($"{cell.EditorID} \"{cell.Name}\" {cell.FormKey}: {refs.Count} refs");
                    if (!opts.ContainsKey("bases")) continue;
                    foreach (var g in refs.OfType<Mutagen.Bethesda.Oblivion.IPlacedObjectGetter>().GroupBy(r => r.Base.FormKey).OrderByDescending(g => g.Count()).Take(int.Parse(opts["bases"])))
                    {
                        var b = records.GetValueOrDefault(g.Key);
                        string model = (b as Mutagen.Bethesda.Oblivion.IModeledGetter)?.Model?.File ?? "";
                        Console.WriteLine($"    {g.Count(),4}x {b?.GetType().Name.Replace("BinaryOverlay", "")} {b?.EditorID} {model}");
                    }
                }
                return 0;
            }
            case "ob-strips-probe":
            {
                // Dev aid: tries reading a bhkNiTriStripsShape's strips data through a temporary NiTriStrips.
                string dataDir = GameInstalls.DataDir(Game.Oblivion)!;
                var data = new Oblivion.ObData(dataDir);
                var tmp = Path.Combine(Path.GetTempPath(), "tm-strips.nif");
                File.WriteAllBytes(tmp, data.Read(opts["file"])!);
                using var nif = new nifly.NifFile();
                nif.Load(tmp);
                var hdr = nif.GetHeader();
                for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                {
                    if (hdr.GetBlockTypeStringById(i) == "hkPackedNiTriStripsData")
                    {
                        var pk = (nifly.hkPackedNiTriStripsData)hdr.GetBlockById(i);
                        Console.WriteLine($"packed [{i}] keyCount {pk.keyCount} numVerts {pk.numVerts} compressed {pk.compressed} triData {pk.triData.Count} triNorm {pk.triNormData.Count} verts {pk.compressedVertData.Count} subparts {pk.subPartData.size()}");
                    }
                    if (hdr.GetBlockTypeStringById(i) != "bhkNiTriStripsShape") continue;
                    var s0 = (nifly.bhkNiTriStripsShape)hdr.GetBlockById(i);
                    Console.WriteLine($"shape [{i}] parts {s0.partRefs.GetSize()} scale {s0.scale.x},{s0.scale.y},{s0.scale.z}");
                    for (uint k = 0; k < s0.partRefs.GetSize(); k++)
                    {
                        uint dataId = s0.partRefs.GetBlockRef(k);
                        Console.WriteLine($"  part {k} -> block {dataId} {hdr.GetBlockTypeStringById(dataId)}");
                        var probe = new nifly.NiTriStrips();
                        GC.SuppressFinalize(probe);
                        probe.SetDataRef(dataId);
                        Console.WriteLine($"    before add: HasData {probe.HasData()} dataRef {probe.DataRef().index}");
                        uint pid = hdr.AddBlock(probe);
                        var shape = hdr.GetBlockById(pid) as nifly.NiShape;
                        Console.WriteLine($"    added as {pid} {hdr.GetBlockTypeStringById(pid)} shape? {shape is not null} hasData {shape?.HasData()} dataRef {(shape as nifly.NiGeometry)?.DataRef().index}");
                        var verts = shape is null ? null : nif.GetVertsForShape(shape);
                        Console.WriteLine($"    verts {verts?.Count}");
                        if (shape is not null) { using var t = new nifly.vectorTriangle(); shape.GetTriangles(t); Console.WriteLine($"    tris {t.Count}"); }
                    }
                }
                return 0;
            }
            case "ob-dialogue-stats":
            {
                // Dev aid: Oblivion dialogue by topic type, condition functions used, result script commands.
                string dataDir = GameInstalls.DataDir(Game.Oblivion)!;
                using var ob = Mutagen.Bethesda.Oblivion.OblivionMod.CreateFromBinaryOverlay(Path.Combine(dataDir, "Oblivion.esm"), Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion);
                var topics = ob.DialogTopics.ToList();
                foreach (var g in topics.GroupBy(t => t.DialogType))
                    Console.WriteLine($"{g.Key}: {g.Count()} topics, {g.Sum(t => t.Items.Count)} responses, {g.Sum(t => t.Items.Sum(i => i.Responses.Sum(r => r.ResponseText?.Length ?? 0)))} chars");
                var items = topics.SelectMany(t => t.Items).ToList();
                foreach (var g in items.SelectMany(i => i.Conditions).GroupBy(c => c.Function).OrderByDescending(g => g.Count()).Take(45))
                    Console.WriteLine($"  {g.Key}: {g.Count()}");
                var cmds = items.Select(i => i.Script?.SourceCode ?? "").SelectMany(sc => sc.Split('\n')).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith(";"))
                    .Select(l => System.Text.RegularExpressions.Regex.Match(l, @"^(?:[A-Za-z0-9_]+\.)?(\w+)").Groups[1].Value.ToLowerInvariant());
                Console.WriteLine("result commands: " + string.Join(", ", cmds.GroupBy(c => c).OrderByDescending(g => g.Count()).Take(30).Select(g => $"{g.Key} {g.Count()}")));
                Console.WriteLine($"quests: {ob.Quests.Count}, stages {ob.Quests.Sum(q => q.Stages.Count)}, log entries {ob.Quests.Sum(q => q.Stages.Sum(st => st.LogEntries.Count))}");
                return 0;
            }
            case "ob-voice-list":
            {
                // Dev aid: Oblivion voice archive entries matching --match (substring), and per-folder counts.
                string dataDir = GameInstalls.DataDir(Game.Oblivion)!;
                foreach (var bsa in Directory.GetFiles(dataDir, "*Voices*.bsa"))
                {
                    var b = new Oblivion.ObBsa(bsa);
                    var names = b.Names.ToList();
                    Console.WriteLine($"{Path.GetFileName(bsa)}: {names.Count} files");
                    foreach (var g in names.GroupBy(n => Path.GetDirectoryName(n)).OrderBy(g => g.Key).Take(40)) Console.WriteLine($"  {g.Key}: {g.Count()}");
                    foreach (var n in names.Where(n => n.Contains(opts.GetValueOrDefault("match", "_00"), StringComparison.OrdinalIgnoreCase)).Take(8)) Console.WriteLine($"    {n}");
                }
                return 0;
            }
            case "ob-light-models":
            {
                // Dev aid: Oblivion light models by how many references place them.
                string dataDir = GameInstalls.DataDir(Game.Oblivion)!;
                using var ob = Mutagen.Bethesda.Oblivion.OblivionMod.CreateFromBinaryOverlay(Path.Combine(dataDir, "Oblivion.esm"), Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion);
                var models = ob.Lights.Where(l => l.Model?.File is { Length: > 0 }).ToDictionary(l => l.FormKey, l => l.Model!.File);
                var counts = ob.EnumerateMajorRecords<Mutagen.Bethesda.Oblivion.IPlacedObjectGetter>().Where(r => models.ContainsKey(r.Base.FormKey))
                    .GroupBy(r => models[r.Base.FormKey], StringComparer.OrdinalIgnoreCase).Select(g => (g.Key, g.Count())).OrderByDescending(x => x.Item2);
                foreach (var (model, n) in counts.Take(int.Parse(opts.GetValueOrDefault("top", "30")))) Console.WriteLine($"{n,6} {model}");
                return 0;
            }
            case "ob-mesh":
            {
                // Dev aid: converts Oblivion models (--file, ';'-separated) to out/obprobe with timings, shape and
                // collision counts, and the visible bounds.
                string dataDir = GameInstalls.DataDir(Game.Oblivion)!;
                var data = new Oblivion.ObData(dataDir);
                var conv = new Oblivion.ObMeshConverter(data, new Oblivion.ObTextures(data, "out/obprobe"));
                foreach (var f in opts["file"].Split(';'))
                {
                    var t0 = System.Diagnostics.Stopwatch.StartNew();
                    var r = conv.Convert(f, "out/obprobe");
                    Console.WriteLine($"{f}: {r.Shapes} shapes, {r.CollisionPolys} collision triangles, bounds {r.Min} .. {r.Max}, {t0.ElapsedMilliseconds} ms, {new FileInfo(Path.Combine("out/obprobe", r.Path)).Length / 1024} KB");
                    foreach (var e in r.Emitters) Console.WriteLine($"   emitter at {e.Position} size {e.Size:F1} rise {e.Rise:F1} texture {e.Texture}");
                    if (r.Sequences.Count > 0) Console.WriteLine($"   plays {string.Join(", ", r.Sequences.Select(s => $"{s.Name} ({s.Length:F1} s)"))}");
                    foreach (var n in conv.Notes) Console.WriteLine($"   note: {n}");
                    conv.Notes.Clear();
                }
                return 0;
            }
            case "ob-seats":
            {
                // Dev aid: Oblivion furniture models (--file, ';'-separated; --sleep for beds) converted to out/obprobe
                // with their Skyrim seat or bed markers (ObFurniture): position, heading, entry sides.
                string dataDir = GameInstalls.DataDir(Game.Oblivion)!;
                var data = new Oblivion.ObData(dataDir);
                var conv = new Oblivion.ObMeshConverter(data, new Oblivion.ObTextures(data, "out/obprobe"));
                foreach (var f in opts["file"].Split(';'))
                {
                    var r = conv.Convert(f, "out/obprobe");
                    var seats = conv.AddSeats(f, Path.Combine("out/obprobe", r.Path), opts.ContainsKey("sleep"));
                    Console.WriteLine($"{f}: bounds {r.Min} .. {r.Max}");
                    foreach (var s in seats) Console.WriteLine($"   {(s.Lie ? "lie" : "sit")} at {s.At} heading {s.Heading:F2} entries {s.Entries} from markers {string.Join(",", s.Sources)}");
                }
                return 0;
            }
            case "nifly-load":
            {
                // Dev aid (ob-nif10-bisect's child): loads --file with nifly; exit 0 when it reads, 1 when it reports an
                // error. nifly ends the process on what it can't follow, which the parent sees as a crash.
                using var nif = new nifly.NifFile();
                return nif.Load(opts["file"]) == 0 ? 0 : 1;
            }
            case "ob-nif10-bisect":
            {
                // Dev aid: the block nifly fails on in an Oblivion model it can't read (--file, data-relative): the model
                // cut after 1, 2, ... blocks (Ob10Nif knows where each starts) is loaded by nifly in a child process
                // until a cut crashes it; that cut's last block is the cause.
                string dataDir = GameInstalls.DataDir(Game.Oblivion)!;
                var data = new Oblivion.ObData(dataDir);
                foreach (var f in opts["file"].Split(';'))
                {
                    string file = f.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? f : @"meshes\" + f;
                    var bytes = data.Read(file) ?? throw new FileNotFoundException(file);
                    var parsed = Oblivion.Ob10Nif.Read(bytes, file);
                    var h = Oblivion.Ob10Nif.Header(bytes)!.Value;
                    string tmp = Path.Combine(Path.GetTempPath(), $"tm-bisect-{Environment.ProcessId}.nif");
                    int Try(int keep)
                    {
                        File.WriteAllBytes(tmp, parsed.Truncated(bytes, keep));
                        var psi = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "nifly-load", "--file", tmp }, RedirectStandardOutput = true, RedirectStandardError = true };
                        using var child = System.Diagnostics.Process.Start(psi)!;
                        child.StandardOutput.ReadToEnd(); child.StandardError.ReadToEnd();
                        child.WaitForExit();
                        return child.ExitCode;
                    }
                    int full = Try(parsed.Blocks.Count);
                    // The first cut that crashes: blocks are read in order, so it's the first block nifly can't follow.
                    int lo = 1, hi = parsed.Blocks.Count, first = -1;
                    while (lo <= hi)
                    {
                        int mid = (lo + hi) / 2;
                        if (Try(mid) is 0 or 1) lo = mid + 1; else { first = mid; hi = mid - 1; }
                    }
                    string type = first > 0 ? h.Types[h.BlockTypes[first - 1]] : "-";
                    // The cut just before: 0 when nifly read every block up to it and the footer right after (those blocks
                    // read as they should), 1 when it read them but not to the footer (one of them misread).
                    int before = first > 1 ? Try(first - 1) : 0;
                    Console.WriteLine(first > 0
                        ? $"{f}: nifly fails on block {first - 1}, {type}; the blocks before it read {(before == 0 ? "to the footer" : $"but not to the footer (exit {before})")}; block {first - 2} is {h.Types[h.BlockTypes[Math.Max(0, first - 2)]]} (the whole file: exit {full})"
                        : $"{f}: no cut crashes nifly (the whole file: exit {full})");
                    File.Delete(tmp);
                }
                return 0;
            }
            case "ob-nif10":
            {
                // Dev aid: reads Oblivion's models nifly can't (Gamebryo 10.1, 10.2 with particles) with Ob10Nif: every
                // such model in the game (or --file a;b), with what failed where.
                string dataDir = GameInstalls.DataDir(Game.Oblivion)!;
                var data = new Oblivion.ObData(dataDir);
                var files = opts.TryGetValue("file", out var fl) ? fl.Split(';').Select(f => f.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? f : @"meshes\" + f) : data.List("meshes");
                int ok = 0, bad = 0;
                foreach (var f in files)
                {
                    var bytes = data.Read(f);
                    if (bytes is null || !Oblivion.Ob10Nif.IsUnreadableByNifly(bytes, out string why)) continue;
                    try
                    {
                        var nif = Oblivion.Ob10Nif.Read(bytes, f);
                        ok++;
                        if (opts.ContainsKey("verbose"))
                            Console.WriteLine($"  ok {f}: {nif.Blocks.Count} blocks, {nif.Blocks.OfType<Oblivion.Ob10Nif.Shape>().Count(s => s.Type == "NiParticleSystem")} particle systems; {why}");
                    }
                    catch (Exception e) { bad++; Console.WriteLine($"  FAIL {e.Message}"); }
                }
                Console.WriteLine($"{ok} read, {bad} failed");
                return 0;
            }
            case "ob-nif":
            {
                // Dev aid: an Oblivion model (--file, data-relative) as nifly reads it: block types, and per shape its
                // type, vertex/triangle counts, textures and alpha.
                string dataDir = GameInstalls.DataDir(Game.Oblivion)!;
                var data = new Oblivion.ObData(dataDir);
                string file = opts["file"].StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? opts["file"] : @"meshes\" + opts["file"];
                var tmp = Path.Combine(Path.GetTempPath(), "tm-ob-probe.nif");
                File.WriteAllBytes(tmp, data.Read(file) ?? throw new FileNotFoundException(file));
                using var nif = new nifly.NifFile();
                Console.WriteLine($"load {nif.Load(tmp)}");
                var hdr = nif.GetHeader();
                Console.WriteLine(string.Join(" ", Enumerable.Range(0, (int)hdr.GetNumBlocks()).Select(i => hdr.GetBlockTypeStringById((uint)i)).GroupBy(t => t).Select(g => $"{g.Key}x{g.Count()}")));
                foreach (var shape in nif.GetShapes())
                {
                    using var tris = new nifly.vectorTriangle();
                    shape.GetTriangles(tris);
                    var tex = Enumerable.Range(0, 3).Select(i => nif.GetTexturePathByIndex(shape, (uint)i)).ToList();
                    var parents = new List<string>();
                    for (var parent = nif.GetParentNode(shape); parent is not null; parent = nif.GetParentNode(parent)) parents.Add($"{parent.name.get()}[{parent.flags:X}]");
                    Console.WriteLine($"  {hdr.GetBlockTypeStringById(hdr.GetBlockID(shape))} '{shape.name.get()}' flags {shape.flags:X} verts {nif.GetVertsForShape(shape)?.Count} tris {tris.Count} uvs {nif.GetUvsForShape(shape)?.Count} colors {nif.GetColorsForShape(shape)?.Count} tex [{string.Join(", ", tex)}] alpha {nif.GetAlphaProperty(shape)?.flags} parents {string.Join(" < ", parents)}");
                }
                return 0;
            }
            case "compile-scripts":
            {
                // Compiles TamrielMerge's Papyrus scripts into --out (default out/mod) without rebuilding the plugins.
                AltStartBuilder.CompileScripts(skyrimData, Path.Combine(AppContext.BaseDirectory, "Papyrus"), opts.GetValueOrDefault("out", "out/mod"));
                return 0;
            }
            case "ob-build":
            {
                // Builds Oblivion.esm (ours) from the original install into --out: every interior and worldspace of the
                // active plugins, load doors linked. --cells <EditorIDs> builds just those interiors (quick tests).
                string dataDir = GameInstalls.DataDir(Game.Oblivion) ?? throw new DirectoryNotFoundException("Oblivion isn't installed through Steam or GOG");
                string outDir = opts.GetValueOrDefault("out", "out/obmod");
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // The plugins to convert: --plugins a,b,... or the mod selection (TamrielMerge.mods.json, else the official
                // game plus what Oblivion has enabled), with their masters, in load order.
                var obProblems = new List<string>();
                var obPlugins = opts.TryGetValue("plugins", out var obList) ? obList.Split(',').Select(x => x.Trim()).ToList() : Mods.ModCatalog.Selected(Game.Oblivion, dataDir, obProblems);
                foreach (var note in obProblems) Console.WriteLine($"  mods: {note}");
                using var builder = new Oblivion.ObPluginBuilder(new Oblivion.ObData(dataDir, obPlugins), outDir) { SkyrimData = skyrimData };
                Console.WriteLine($"Loaded {string.Join(", ", builder.Plugins.Select(p => p.ModKey.FileName))} ({sw.Elapsed.TotalSeconds:F0}s)");
                // Scripts are translated first: the references they name are placed persistent.
                bool scripts = !opts.ContainsKey("no-scripts") && !opts.ContainsKey("no-dialogue");
                if (scripts)
                {
                    builder.PrepareScripts();
                    Console.WriteLine($"  scripts: {builder.ScriptCount} translated, {builder.ScriptStatementsTranslated}/{builder.ScriptStatements} statements ({100.0 * builder.ScriptStatementsTranslated / Math.Max(1, builder.ScriptStatements):F1}%) ({sw.Elapsed.TotalSeconds:F0}s)");
                }
                // Worn armour and clothing meshes, all at once in parallel (the records come with the items later).
                var (obWornMade, obWornFailed) = builder.PrepareWorn();
                Console.WriteLine($"  worn meshes: {obWornMade} model sets re-rigged to Skyrim's skeleton, {obWornFailed} failed ({sw.Elapsed.TotalSeconds:F0}s)");
                if (opts.TryGetValue("cells", out var only))
                    foreach (var id in only.Split(','))
                    {
                        var cell = builder.FindInterior(id);
                        if (cell is null) { Console.WriteLine($"  no interior {id}"); continue; }
                        var built = builder.BuildInterior(cell);
                        Console.WriteLine($"  {cell.Name}: {built.Temporary.Count + built.Persistent.Count} references ({built.EditorID})");
                    }
                else
                {
                    Console.WriteLine($"  {builder.BuildAllInteriors()} interiors ({sw.Elapsed.TotalSeconds:F0}s)");
                    var (worlds, cells) = builder.BuildWorldspaces();
                    Console.WriteLine($"  {worlds} worldspaces, {cells} exterior cells, {builder.RegionCount} regions, {builder.ExteriorNavmeshes} exterior navmeshes ({builder.ExteriorEdgeLinks} edge links), {builder.TerrainLodChunks} terrain LOD chunks ({sw.Elapsed.TotalSeconds:F0}s)");
                }
                var (linked, unlinked) = builder.LinkDoors();
                Console.WriteLine($"  doors: {linked} load doors linked, {unlinked} partners not built");
                Console.WriteLine($"  effects: {builder.EffectCount} vanilla effects standing in for particle systems");
                Console.WriteLine($"  locks: {builder.LockCount} locked doors and containers (Oblivion lockpicking)");
                if (!opts.ContainsKey("no-dialogue"))
                {
                    builder.BuildDialogue();
                    Console.WriteLine($"  dialogue: {builder.DialogueTopics} topics, {builder.DialogueResponses} responses ({builder.DialogueLines} lines), {builder.QuestCount} quests ({sw.Elapsed.TotalSeconds:F0}s)");
                }
                if (scripts)
                {
                    int compiled = builder.AttachScripts();
                    Console.WriteLine($"  scripts: {compiled} compiled, {builder.ScriptsAttached} attached, {builder.PropertiesFilled} properties filled ({sw.Elapsed.TotalSeconds:F0}s)");
                    File.WriteAllLines(Path.Combine(outDir, "scripts-missing.txt"), builder.ScriptMissing.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value,6} {kv.Key}"));
                }
                var m = builder.Mod;
                Console.WriteLine($"  records: {m.Statics.Count} statics, {m.Doors.Count} doors, {m.Activators.Count} swinging doors, {m.Lights.Count} lights, {m.LandscapeTextures.Count} landscape textures, " +
                    $"{m.Containers.Count} containers, {m.MiscItems.Count} misc, {m.Keys.Count} keys, {m.Books.Count} books, {m.Weapons.Count} weapons, {m.Ammunitions.Count} ammo, {m.Armors.Count} armour/clothing, " +
                    $"{m.Ingestibles.Count} potions, {m.Ingredients.Count} ingredients, {m.SoulGems.Count} soul gems, {m.LeveledItems.Count} leveled lists, {m.Npcs.Count} actors, {m.LeveledNpcs.Count} leveled actor lists; {builder.Navmeshes} navmeshes ({builder.NavmeshTriangles} triangles)");
                Console.WriteLine($"  worn: {builder.WornConverted} armour/clothing with Oblivion's own worn look (re-rigged to Skyrim's skeleton), {builder.WornFallbacks} kept their Skyrim template's look (see problems)");
                builder.Write();
                var problems = builder.Problems.Distinct().ToList();
                File.WriteAllLines(Path.Combine(outDir, "problems.txt"), problems);
                foreach (var p in problems.Take(40)) Console.WriteLine($"  problem: {p}");
                if (problems.Count > 40) Console.WriteLine($"  ... {problems.Count - 40} more problems (all in {Path.Combine(outDir, "problems.txt")})");
                Console.WriteLine($"Wrote {Path.GetFullPath(Path.Combine(outDir, Oblivion.ObPluginBuilder.PluginName))} in {sw.Elapsed.TotalSeconds:F0}s");
                return 0;
            }
            case "mw-script-translate":
            {
                // Morrowind's scripts (the converter's plugins: --plugins a,b,... or the mod selection) translated into
                // Papyrus in --out (default out/mwscripts): object scripts (MW_), global scripts (MWG_) and dialogue result
                // scripts (MWR_) in Source, and coverage.txt — per plugin the share of statements translated and what's
                // missing most. --compile compiles them against Skyrim's, SKSE's and TamrielMerge's sources (compile.txt);
                // --id <regex> limits it to matching script IDs ("results" for the dialogue's).
                string dataFiles = GameInstalls.DataDir(Game.Morrowind) ?? throw new DirectoryNotFoundException("Morrowind isn't installed through Steam or GOG");
                var mwPlugins = opts.TryGetValue("plugins", out var mwList) ? mwList.Split(',').Select(x => x.Trim()).ToList() : Mods.ModCatalog.Selected(Game.Morrowind, dataFiles, []);
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/mwscripts"));
                string srcDir = Path.Combine(outDir, "Source");
                if (Directory.Exists(srcDir)) Directory.Delete(srcDir, true);
                Directory.CreateDirectory(srcDir);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var world = Morrowind.MwWorld.Load(dataFiles, mwPlugins);
                var scriptWorld = new Morrowind.MwScriptWorld(world, Morrowind.MwDialogue.Load(world.Plugins), Morrowind.Tes3Data.Open(dataFiles, mwPlugins));
                var only = opts.TryGetValue("id", out var idRx) ? new System.Text.RegularExpressions.Regex(idRx, System.Text.RegularExpressions.RegexOptions.IgnoreCase) : null;
                var set = Morrowind.MwScriptSet.Translate(scriptWorld, only);
                Morrowind.MwPluginBuilder.WriteScriptSources(set.All, srcDir);
                var report = set.Report();
                report.Insert(0, $"Plugins: {string.Join(", ", world.Plugins.Select(p => p.FileName))}; {scriptWorld.Scripts.Count} scripts, {scriptWorld.StartScripts.Count} start scripts ({string.Join(", ", scriptWorld.StartScripts)}), {scriptWorld.GlobalScripts.Count} run as global scripts");
                File.WriteAllLines(Path.Combine(outDir, "coverage.txt"), report);
                foreach (var line in set.Report(int.Parse(opts.GetValueOrDefault("top", "30"))).Take(80)) Console.WriteLine(line);
                Console.WriteLine($"Translated in {sw.Elapsed.TotalSeconds:F0}s into {srcDir}");
                if (opts.ContainsKey("compile"))
                {
                    var (compiled, output) = Morrowind.MwPluginBuilder.CompilePapyrus(skyrimData, srcDir, Path.Combine(outDir, "Scripts"), m => Console.WriteLine($"  {m} ({sw.Elapsed.TotalSeconds:F0}s)"));
                    File.WriteAllText(Path.Combine(outDir, "compile.txt"), output);
                    var errors = output.Split('\n').Select(l => l.TrimEnd()).Where(l => l.Contains("): ")).ToList();
                    foreach (var l in errors.Take(int.Parse(opts.GetValueOrDefault("errors", "40")))) Console.WriteLine(l);
                    Console.WriteLine(compiled is null ? output : $"{compiled.Count} of {Directory.GetFiles(srcDir, "*.psc").Length} scripts compiled, {errors.Count} error lines ({sw.Elapsed.TotalSeconds:F0}s)");
                }
                return 0;
            }
            case "mw-script-attach-test":
            {
                // Dev aid: the plugin builder's script steps on a few interiors (--cells regex, default the prison ship and
                // the census office) with the dialogue, using scripts compiled already by mw-script-translate (--scripts,
                // default out/mwscripts/Scripts), into --out (default out/mwscripts-attach); lists what was attached.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/mwscripts-attach"));
                var mwPlugins = Mods.ModCatalog.Selected(Game.Morrowind, dataFiles, []);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var world = Morrowind.MwWorld.Load(dataFiles, mwPlugins);
                var builder = new Morrowind.MwPluginBuilder(world, Morrowind.Tes3Data.Open(dataFiles, mwPlugins), outDir) { SkyrimData = skyrimData };
                builder.PrepareScripts();
                Console.WriteLine($"  scripts: {builder.ScriptCount} translated ({sw.Elapsed.TotalSeconds:F0}s)");
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("cells", "^Imperial Prison Ship$|^Seyda Neen, Census and Excise Office$"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var cell in world.Interiors.Values.Where(c => rx.IsMatch(c.Name))) builder.BuildInterior(cell);
                builder.BuildDialogue();
                Console.WriteLine($"  cells and dialogue built ({sw.Elapsed.TotalSeconds:F0}s)");
                int n = builder.AttachScripts(precompiled: Path.GetFullPath(opts.GetValueOrDefault("scripts", "out/mwscripts/Scripts")));
                // --write: the test's plugin (only these cells, the dialogue and the scripts' records) into --out, for plugin-check.
                if (opts.ContainsKey("write"))
                {
                    TamrielMerge.Skyrim.TopicSubtypes.Fill(builder.Mod);
                    var masterOrder = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => ModKey.FromFileName(f)).ToArray();
                    builder.Mod.WriteToBinary(Path.Combine(outDir, Morrowind.MwPluginBuilder.PluginName), new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
                    {
                        MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(masterOrder),
                    });
                    Console.WriteLine($"  wrote {Path.Combine(outDir, Morrowind.MwPluginBuilder.PluginName)}");
                }
                Console.WriteLine($"  {n} compiled scripts, {builder.ScriptsAttached} attached, {builder.ResultScriptsAttached} dialogue results, {builder.PropertiesFilled} properties filled ({builder.PropertiesUnfilled} without a record) ({sw.Elapsed.TotalSeconds:F0}s)");
                var m = builder.Mod;
                foreach (var rec in m.EnumerateMajorRecords().OfType<Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter>().Take(int.MaxValue))
                {
                    var vm = rec.GetType().GetProperty("VirtualMachineAdapter")?.GetValue(rec) as Mutagen.Bethesda.Skyrim.IAVirtualMachineAdapterGetter;
                    if (vm is null || !vm.Scripts.Any(s => s.Name.StartsWith("MW", StringComparison.Ordinal) && !s.Name.StartsWith("MWR_"))) continue;
                    foreach (var s in vm.Scripts.Where(s => s.Name.StartsWith("MW")))
                        Console.WriteLine($"  {rec.EditorID ?? rec.FormKey.ToString()}: {s.Name} ({s.Properties.Count} properties: {string.Join(", ", s.Properties.Take(6).Select(p => p.Name))})");
                }
                foreach (var npc in m.Npcs)
                    Console.WriteLine($"  npc {npc.EditorID}: {string.Join(", ", npc.VirtualMachineAdapter?.Scripts.Select(s => $"{s.Name} ({s.Properties.Count})") ?? [])} template flags {npc.Configuration.TemplateFlags}");
                // The records the scripts' state needs: the state quest's tables, journal indexes and objectives, Companion Share.
                foreach (var q in m.Quests.Where(q => q.EditorID is "MWScriptState"))
                    foreach (var s in q.VirtualMachineAdapter?.Scripts ?? [])
                        Console.WriteLine($"  state {q.EditorID}: {s.Name} " + string.Join(", ", s.Properties.Select(p => p switch
                        {
                            Mutagen.Bethesda.Skyrim.ScriptObjectListProperty l => $"{p.Name}[{l.Objects.Count}]",
                            Mutagen.Bethesda.Skyrim.ScriptIntListProperty il => $"{p.Name}[{il.Data.Count}] ({string.Join(" ", il.Data.Take(104))})",
                            _ => p.Name,
                        })));
                Console.WriteLine($"  state: journal index globals {m.Globals.Count(g => g.EditorID?.StartsWith("MWJournalIndex") == true)}, player stat globals {m.Globals.Count(g => g.EditorID?.StartsWith("MWPlayer") == true)}, " +
                    $"journal objectives {m.Quests.Sum(q => q.EditorID?.StartsWith("MWQ") == true ? q.Objectives.Count : 0)}, companion share {m.DialogTopics.Any(t => t.EditorID == "MWCompanionShare")}, " +
                    $"carriers with MwBaseStats {m.Npcs.Count(n => n.VirtualMachineAdapter?.Scripts.Any(s => s.Properties.Any(p => p.Name == "MwBaseStats")) == true)}");
                foreach (var actor in m.Npcs)
                    foreach (var s in actor.VirtualMachineAdapter?.Scripts ?? [])
                        if (s.Properties.OfType<Mutagen.Bethesda.Skyrim.ScriptIntListProperty>().FirstOrDefault(p => p.Name == "MwBaseStats") is { } stats)
                            Console.WriteLine($"  state {actor.EditorID} {s.Name} MwBaseStats: {string.Join(" ", stats.Data)}");
                foreach (var p in builder.Problems.Where(p => p.StartsWith("script")).Distinct().Take(30)) Console.WriteLine($"  problem: {p}");
                return 0;
            }
            case "mw-script-grep":
            {
                // Dev aid: Morrowind scripts (SCPT source text, SCTX) containing --text (regex), with the matching lines;
                // start scripts (SSCR) are listed first.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                var world = Morrowind.MwWorld.Load(dataFiles);
                var rx = new System.Text.RegularExpressions.Regex(opts["text"], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var p in world.Plugins)
                    foreach (var r in p.Records.Where(r => r.Type == "SSCR"))
                        Console.WriteLine($"{p.FileName} start script: {r.Str("NAME")} -> {r.Str("DATA")}");
                foreach (var r in world.Objects.Values.Where(r => r.Type == "SCPT"))
                {
                    string text = r.Str("SCTX") ?? "";
                    var lines = text.Split('\n').Select((l, i) => (l, i)).Where(x => rx.IsMatch(x.l)).ToList();
                    if (lines.Count == 0) continue;
                    Console.WriteLine($"== {r.Get("SCHD")?.Text}");
                    foreach (var (l, i) in lines.Take(12)) Console.WriteLine($"  {i + 1,4}: {l.Trim()}");
                }
                return 0;
            }
            case "scale-check":
            {
                // Dev aid: how tall a character is in each game — the head bone's height above the root in Morrowind's
                // base_anim.nif and in Skyrim's character skeleton — to check that both worlds share a scale.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                var data = Morrowind.Tes3Data.Open(dataFiles);
                var nif = Morrowind.Nif4.Read(data.Read(@"meshes\base_anim.nif")!, "base_anim.nif");
                void Walk(int index, Morrowind.NifTransform parent)
                {
                    if (nif.Get<Morrowind.NiNode>(index) is not { } n) return;
                    var w = n.Transform.Then(parent);
                    if (System.Text.RegularExpressions.Regex.IsMatch(n.Name, "^Bip01 (Head|Pelvis|L Foot|R Foot|Neck)$|^Head$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        Console.WriteLine($"Morrowind {n.Name,-14} z {w.Translation.Z,7:F1}");
                    foreach (var c in n.Children) Walk(c, w);
                }
                Walk(nif.Roots[0], Morrowind.NifTransform.Identity);
                var reader = Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, Path.Combine(skyrimData, "Skyrim - Meshes0.bsa"));
                var skel = reader.Files.First(f => f.Path.Equals(@"meshes\actors\character\character assets\skeleton.nif", StringComparison.OrdinalIgnoreCase));
                var tmp = Path.Combine(Path.GetTempPath(), "tm-skel.nif");
                File.WriteAllBytes(tmp, skel.GetBytes());
                using var sk = new nifly.NifFile();
                sk.Load(tmp);
                foreach (var name in new[] { "NPC Head [Head]", "NPC Neck [Neck]", "NPC Pelvis [Pelv]", "NPC L Foot [Lft ]" })
                {
                    using var t = new nifly.MatTransform();
                    if (sk.GetNodeTransformToGlobal(name, t)) Console.WriteLine($"Skyrim    {name,-18} z {t.translation.z,7:F1}");
                }
                return 0;
            }
            case "mw-door-check":
            {
                // Dev aid: Morrowind doors whose destination cell has no door of its own (they can't be Skyrim load doors).
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                var world = Morrowind.MwWorld.Load(dataFiles);
                bool IsDoor(Morrowind.Tes3Ref r) => world.Objects.GetValueOrDefault(r.BaseId)?.Type == "DOOR";
                var cellsWithDoors = world.Interiors.Values.Where(c => c.Refs.Any(r => IsDoor(r) && r.DoorDestination is not null)).Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var (from, r) in world.Interiors.Values.SelectMany(c => c.Refs.Select(r => (c.Name, r))).Concat(world.Exteriors.Values.SelectMany(c => c.Refs.Select(r => ("(exterior)", r)))))
                    if (r.DoorDestination is { } d && r.DoorDestinationCell is { } dc && !cellsWithDoors.Contains(dc))
                        Console.WriteLine($"{r.BaseId} ({world.Objects.GetValueOrDefault(r.BaseId)?.Type}) in {from} at {r.Position} -> \"{dc}\" at {d.Position}; cell exists: {world.Interiors.ContainsKey(dc)}");
                return 0;
            }
            case "mw-files":
            {
                // Dev aid: Morrowind data files (loose and archived) whose path matches --match (a regex).
                var data = Morrowind.Tes3Data.Open(GameInstalls.DataDir(Game.Morrowind)!);
                var rx = new System.Text.RegularExpressions.Regex(opts["match"], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var folder in new[] { "meshes", "textures", "sound", "icons", "bookart" })
                    foreach (var n in data.List(folder).Where(n => rx.IsMatch(n)).Take(400)) Console.WriteLine(n);
                return 0;
            }
            case "mw-nif":
            {
                // Dev aid: a Morrowind model's node tree (--model a;b): nodes with their world position, shapes with
                // vertex counts and textures, skins with their bones, particle controllers with their texture.
                var data = Morrowind.Tes3Data.Open(GameInstalls.DataDir(Game.Morrowind)!);
                foreach (var model in opts["model"].Split(';'))
                {
                    string path = model.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? model : @"meshes\" + model;
                    var nif = Morrowind.Nif4.Read(data.Read(path) ?? throw new FileNotFoundException(path), path);
                    Console.WriteLine($"== {path}: {nif.Blocks.Count} blocks");
                    string Tex(Morrowind.NiNode n) => string.Join(",", n.Properties.Select(nif.Get<Morrowind.NifBlock>).OfType<Morrowind.NiTexturingProperty>()
                        .Select(t => t.Base is { } b ? nif.Get<Morrowind.NiSourceTexture>(b.Source)?.FileName : null).Where(s => s is not null));
                    void Walk(int i, Morrowind.NifTransform parent, int depth)
                    {
                        if (nif.Get<Morrowind.NiNode>(i) is not { } n) return;
                        var w = n.Transform.Then(parent);
                        string extra = "";
                        if (n is Morrowind.NiTriShape s && nif.Get<Morrowind.NiGeometryData>(s.Data) is { } d)
                        {
                            extra = $" verts {d.Vertices.Length} tex [{Tex(n)}]";
                            if (nif.Get<Morrowind.NiSkinInstance>(s.Skin) is { } sk)
                                extra += $" SKIN root {nif.Get<Morrowind.NiNode>(sk.SkeletonRoot)?.Name} bones {string.Join("|", sk.Bones.Select(b => nif.Get<Morrowind.NiNode>(b)?.Name))}";
                        }
                        else if (Tex(n) is { Length: > 0 } t) extra = $" tex [{t}]";
                        Console.WriteLine($"{new string(' ', depth * 2)}{n.Type} \"{n.Name}\"{(n.Hidden ? " hidden" : "")} at {w.Translation:F1} s {w.Scale:F2}{extra}");
                        foreach (var c in n.Children) Walk(c, w, depth + 1);
                    }
                    foreach (var r in nif.Roots) Walk(r, Morrowind.NifTransform.Identity, 0);
                    foreach (var c in nif.Blocks.OfType<Morrowind.NiParticleSystemController>())
                        Console.WriteLine($"  particles: target {nif.Get<Morrowind.NiNode>(c.Target)?.Name} size {c.InitialSize} speed {c.Speed} life {c.Lifetime} rate {c.BirthRate} n {c.NumParticles} dims {c.EmitterDimensions}");
                }
                return 0;
            }
            case "worn-bones":
            {
                // Dev aid: how a Morrowind skeleton's Bip01 bones line up with Skyrim's for worn armour (--model: a skinned
                // body part, whose own skeleton is its bind pose; default base_anim.nif; --female).
                var data = Morrowind.Tes3Data.Open(GameInstalls.DataDir(Game.Morrowind)!);
                bool female = opts.ContainsKey("female");
                var assets = new Skyrim.Meshes.SkyrimCharacterAssets(skyrimData);
                var source = new Morrowind.MwWornSource(data, new Morrowind.MwTextures(data, Path.GetTempPath()));
                var problems = new List<string>();
                var from = opts.TryGetValue("model", out var model) ? source.Part(model, int.Parse(opts.GetValueOrDefault("part", "3")), female, problems).Skeleton : source.CharacterSkeleton(female);
                foreach (var line in Skyrim.Meshes.WornMesh.Report(from, assets.Skeleton(female))) Console.WriteLine(line);
                foreach (var p in problems) Console.WriteLine("problem: " + p);
                return 0;
            }
            case "ob-worn":
            {
                // Dev aid: Oblivion armour and clothing (EditorID matching --match, --take n) with their worn look converted
                // into --out, and those records written as --out\Oblivion.esm.
                string dataDir = GameInstalls.DataDir(Game.Oblivion) ?? throw new DirectoryNotFoundException("Oblivion isn't installed through Steam or GOG");
                string outDir = opts.GetValueOrDefault("out", "out/obworn");
                using var builder = new Oblivion.ObPluginBuilder(new Oblivion.ObData(dataDir), outDir) { SkyrimData = skyrimData };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                foreach (var line in builder.ConvertWorn(new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("match", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase), int.Parse(opts.GetValueOrDefault("take", "20"))))
                    Console.WriteLine(line);
                Console.WriteLine($"worn looks: {builder.WornConverted} Oblivion's, {builder.WornFallbacks} template fallbacks in {sw.Elapsed.TotalSeconds:F1}s");
                var masterOrder = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => ModKey.FromFileName(f)).ToArray();
                Directory.CreateDirectory(outDir);
                builder.Mod.WriteToBinary(Path.Combine(outDir, Oblivion.ObPluginBuilder.PluginName), new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
                {
                    MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(masterOrder),
                });
                return 0;
            }
            case "worn-check":            {
                // Dev aid: a written worn NIF (--file a;b) read back on Skyrim's skeleton (--female): per shape its bounds,
                // bones, and how much of it lies inside Skyrim's naked --part (body/hands/feet) at --weight (0/1).
                bool female = opts.ContainsKey("female");
                var assets = new Skyrim.Meshes.SkyrimCharacterAssets(skyrimData);
                var skeleton = assets.Skeleton(female);
                var surface = assets.Surface(opts.GetValueOrDefault("part", "body"), female, int.Parse(opts.GetValueOrDefault("weight", "1")));
                // The naked part's normals should point out of the body: on the torso, away from the body's centre line.
                foreach (var b in assets.Naked(opts.GetValueOrDefault("part", "body"), female, int.Parse(opts.GetValueOrDefault("weight", "1"))))
                {
                    var torso = Enumerable.Range(0, b.V.Length).Where(i => MathF.Abs(b.V[i].X) < 10 && b.V[i].Z > 80 && b.V[i].Z < 110 && b.N.Length == b.V.Length).ToList();
                    if (torso.Count > 0)
                        Console.WriteLine($"naked {b.Name}: torso normals outward {torso.Average(i => System.Numerics.Vector3.Dot(b.N[i], System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(b.V[i].X, b.V[i].Y + 2, 0)))):F2} (1 = all out)");
                }
                // --png out.png: every file's shapes drawn together, front and side, flat-shaded (skin tan, the rest by file).
                var drawn = new List<(Skyrim.Meshes.WornShape Shape, int File)>();
                int fileIndex = 0;
                foreach (var file in opts["file"].Split(';'))
                {
                    using var nif = new nifly.NifFile();
                    if (nif.Load(file) != 0) { Console.WriteLine($"{file}: unreadable"); continue; }
                    Console.WriteLine($"== {Path.GetFileName(file)}");
                    fileIndex++;
                    foreach (var s in Skyrim.Meshes.WornMesh.ReadSkinned(nif, skeleton))
                    {
                        drawn.Add((s, fileIndex));
                        var lo = s.V.Aggregate(System.Numerics.Vector3.Min); var hi = s.V.Aggregate(System.Numerics.Vector3.Max);
                        int inside = 0; float deepest = 0;
                        foreach (var v in s.V)
                            if (surface.Nearest(v) is { } hit)
                            {
                                float d = System.Numerics.Vector3.Dot(v - hit.Point, hit.Normal);
                                if (d < 0) { inside++; deepest = MathF.Min(deepest, d); }
                            }
                        var bones = s.Weights.SelectMany(w => w.Select(x => x.Bone)).Distinct().ToList();
                        Console.WriteLine($"  {s.Name,-28} {s.V.Length,5} verts  {lo:F0} .. {hi:F0}  inside {inside} (deepest {deepest:F1})  {bones.Count} bones: {string.Join(", ", bones.Take(8))}{(bones.Count > 8 ? " ..." : "")}");
                    }
                }
                if (opts.TryGetValue("png", out var png) && drawn.Count > 0)
                {
                    // Two orthographic views side by side: front (looking along +Y at the body's front, x right) and the
                    // left side (looking along +X); a z-buffer and light from the viewer's upper left.
                    const int size = 512;
                    var rgb = new byte[size * 2 * size * 3];
                    var depth = new float[size * 2 * size];
                    Array.Fill(depth, float.MaxValue);
                    for (int i = 0; i < rgb.Length; i++) rgb[i] = 40;
                    float zmin = -2, zmax = 160, scale = size / (zmax - zmin);
                    System.Numerics.Vector3[] palette = [new(0.55f, 0.6f, 0.75f), new(0.75f, 0.55f, 0.45f), new(0.5f, 0.7f, 0.5f), new(0.7f, 0.7f, 0.4f), new(0.6f, 0.5f, 0.75f)];
                    foreach (var (s, f) in drawn)
                        for (int view = 0; view < 2; view++)
                        {
                            // Screen x, screen y (down), depth towards the viewer negative.
                            (float X, float Y, float D) P(System.Numerics.Vector3 v) => view == 0
                                ? (size / 2 + v.X * scale, size - (v.Z - zmin) * scale, v.Y)
                                : (size + size / 2 - v.Y * scale, size - (v.Z - zmin) * scale, -v.X);
                            var colour = s.IsSkin ? new System.Numerics.Vector3(0.85f, 0.68f, 0.55f) : palette[(f - 1) % palette.Length];
                            var eye = view == 0 ? new System.Numerics.Vector3(0, -1, 0) : new System.Numerics.Vector3(1, 0, 0);
                            var light = System.Numerics.Vector3.Normalize(eye * 2 + new System.Numerics.Vector3(-0.5f, 0, 1));
                            for (int t = 0; t + 2 < s.Tris.Length; t += 3)
                            {
                                var a = s.V[s.Tris[t]]; var b = s.V[s.Tris[t + 1]]; var c = s.V[s.Tris[t + 2]];
                                var n = System.Numerics.Vector3.Cross(b - a, c - a);
                                if (n.LengthSquared() < 1e-10f) continue;
                                n = System.Numerics.Vector3.Normalize(n);
                                float shade = 0.25f + 0.75f * MathF.Abs(System.Numerics.Vector3.Dot(n, light));
                                var (ax, ay, ad) = P(a); var (bx, by, bd) = P(b); var (cx, cy, cd) = P(c);
                                int x0 = (int)MathF.Max(view * size, MathF.Floor(MathF.Min(ax, MathF.Min(bx, cx)))), x1 = (int)MathF.Min(view * size + size - 1, MathF.Ceiling(MathF.Max(ax, MathF.Max(bx, cx))));
                                int y0 = (int)MathF.Max(0, MathF.Floor(MathF.Min(ay, MathF.Min(by, cy)))), y1 = (int)MathF.Min(size - 1, MathF.Ceiling(MathF.Max(ay, MathF.Max(by, cy))));
                                float area = (bx - ax) * (cy - ay) - (cx - ax) * (by - ay);
                                if (MathF.Abs(area) < 1e-6f) continue;
                                for (int y = y0; y <= y1; y++)
                                    for (int x = x0; x <= x1; x++)
                                    {
                                        float px = x + 0.5f, py = y + 0.5f;
                                        float w0 = ((bx - px) * (cy - py) - (cx - px) * (by - py)) / area;
                                        float w1 = ((cx - px) * (ay - py) - (ax - px) * (cy - py)) / area;
                                        float w2 = 1 - w0 - w1;
                                        if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                                        float d = w0 * ad + w1 * bd + w2 * cd;
                                        int k = y * size * 2 + x;
                                        if (d >= depth[k]) continue;
                                        depth[k] = d;
                                        rgb[k * 3] = (byte)(255 * colour.X * shade); rgb[k * 3 + 1] = (byte)(255 * colour.Y * shade); rgb[k * 3 + 2] = (byte)(255 * colour.Z * shade);
                                    }
                            }
                        }
                    Util.Png.WriteRgb(png, size * 2, size, rgb);
                    Console.WriteLine($"wrote {png}");
                }
                return 0;
            }
            case "mw-worn":
            {
                // Dev aid: Morrowind items' worn look (--items id,id,...) converted into --out with their records; writes
                // --out\MorrowindWornTest.esp holding just those records.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                string outDir = opts.GetValueOrDefault("out", "out/mwworn");
                var world = Morrowind.MwWorld.Load(dataFiles);
                var builder = new Morrowind.MwPluginBuilder(world, Morrowind.Tes3Data.Open(dataFiles), outDir) { SkyrimData = skyrimData };
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // --items id,id or --match regex (ARMO/CLOT ids; --take n, default 20).
                var ids = opts.TryGetValue("items", out var itemList) ? itemList.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList()
                    : world.Objects.Values.Where(o => o.Type is "ARMO" or "CLOT" && System.Text.RegularExpressions.Regex.IsMatch(o.Id, opts.GetValueOrDefault("match", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        .Select(o => o.Id).Take(int.Parse(opts.GetValueOrDefault("take", "20"))).ToList();
                foreach (var line in builder.ConvertWorn(ids)) Console.WriteLine(line);
                Console.WriteLine($"worn looks: {builder.WornConverted} Morrowind's, {builder.WornFallbacks} template fallbacks in {sw.Elapsed.TotalSeconds:F1}s");
                var loadOrder = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => ModKey.FromFileName(f)).ToArray();
                Directory.CreateDirectory(outDir);
                builder.Mod.WriteToBinary(Path.Combine(outDir, "Morrowind.esm"), new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
                {
                    MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(loadOrder),
                });
                return 0;
            }
            case "esm-dump":
            {
                // Dev aid: every field of the records of --plugin (default Skyrim.esm) whose editor ID matches --edid.
                string plugin = Path.Combine(skyrimData, opts.GetValueOrDefault("plugin", "Skyrim.esm"));   // a file name is in Skyrim's Data
                var re = new System.Text.RegularExpressions.Regex(opts["edid"], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                using var m = SkyrimMod.CreateFromBinaryOverlay(plugin, SkyrimRelease.SkyrimSE);
                foreach (var r in m.EnumerateMajorRecords().Where(r => r.EditorID is { } e && re.IsMatch(e)
                             && (!opts.TryGetValue("type", out var t) || r.Registration.Name.Equals(t, StringComparison.OrdinalIgnoreCase))).Take(int.Parse(opts.GetValueOrDefault("max", "4"))))
                {
                    Console.WriteLine($"== {r.Registration.Name} {r.EditorID} {r.FormKey}");
                    DumpFields(r, "  ", 0);
                }
                return 0;
                static void DumpFields(object? o, string indent, int depth)
                {
                    if (o is null || depth > 4) return;
                    foreach (var p in o.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                    {
                        if (p.GetIndexParameters().Length > 0 || p.Name is "Registration" or "StaticRegistration" or "EditorID" or "FormKey" or "ContainedFormLinks" or "EnumerateFormLinks") continue;
                        object? v;
                        try { v = p.GetValue(o); } catch { continue; }
                        if (v is null) { Console.WriteLine($"{indent}{p.Name}: null"); continue; }
                        var vt = v.GetType();
                        if (vt.IsPrimitive || vt.IsEnum || v is string || v is System.Drawing.Color || vt.Name.StartsWith("FormLink") || vt.Name.StartsWith("AssetLink") || vt.Name.StartsWith("P3") || vt.Name.StartsWith("P2") || vt.Name is "Percent" or "TimeOnly" or "FormKey")
                            Console.WriteLine($"{indent}{p.Name}: {v}");
                        else if (v is System.Collections.IEnumerable list && v is not string)
                        {
                            int i = 0;
                            foreach (var item in list)
                            {
                                if (i++ >= 40) { Console.WriteLine($"{indent}  ..."); break; }
                                var it = item?.GetType();
                                if (it is null || it.IsPrimitive || item is string || it.Name.StartsWith("FormLink") || it.Name.StartsWith("AssetLink")) Console.WriteLine($"{indent}{p.Name}[{i - 1}]: {item}");
                                else { Console.WriteLine($"{indent}{p.Name}[{i - 1}]:"); DumpFields(item, indent + "  ", depth + 1); }
                            }
                        }
                        else if (vt.Namespace?.StartsWith("Mutagen") == true || vt.Namespace?.StartsWith("Noggog") == true) { Console.WriteLine($"{indent}{p.Name}:"); DumpFields(v, indent + "  ", depth + 1); }
                        else Console.WriteLine($"{indent}{p.Name}: {v}");
                    }
                }
            }
            case "mw-fx-census":
            {
                // Dev aid: Morrowind models with particle systems, by how many references place them.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                var world = Morrowind.MwWorld.Load(dataFiles);
                var data = Morrowind.Tes3Data.Open(dataFiles);
                var uses = world.Interiors.Values.Concat(world.Exteriors.Values).SelectMany(c => c.Refs).GroupBy(r => r.BaseId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
                var byModel = new Dictionary<string, (int Refs, string Types)>(StringComparer.OrdinalIgnoreCase);
                foreach (var (id, rec) in world.Objects)
                {
                    if (rec.Str("MODL") is not { Length: > 0 } model || !uses.TryGetValue(id, out int n)) continue;
                    if (byModel.TryGetValue(model, out var known)) { byModel[model] = (known.Refs + n, known.Types); continue; }
                    var bytes = data.Read(Path.Combine("meshes", model));
                    if (bytes is null) continue;
                    string text = System.Text.Encoding.ASCII.GetString(bytes);
                    if (!text.Contains("NiBSPArrayController") && !text.Contains("NiParticleSystemController")) continue;
                    byModel[model] = (n, rec.Type);
                }
                foreach (var (model, (refs, type)) in byModel.OrderByDescending(kv => kv.Value.Refs).Take(int.Parse(opts.GetValueOrDefault("top", "80"))))
                    Console.WriteLine($"{refs,6} {type} {model}");
                Console.WriteLine($"{byModel.Count} models with particles, {byModel.Values.Sum(v => v.Refs)} references");
                return 0;
            }
            case "mw-vfx":
            {
                // Converts Morrowind effect models (--model a;b, under meshes\) to Skyrim particle effects in --out and
                // reads the result back: blocks and the emission keys written.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                var mwData = Morrowind.Tes3Data.Open(dataFiles);
                string outDir = opts.GetValueOrDefault("out", "out/vfxtest");
                var vfx = new Morrowind.MwVfx(mwData, new Morrowind.MwTextures(mwData, outDir));
                foreach (var model in opts["model"].Split(';'))
                {
                    var rel = vfx.Convert(model, outDir);
                    Console.WriteLine($"{model} -> {rel ?? "FAILED"}");
                    if (rel is null) continue;
                    string path = Path.Combine(outDir, "Meshes", rel);
                    using var nif = new nifly.NifFile();
                    nif.Load(path);
                    var hdr = nif.GetHeader();
                    var counts = new Dictionary<string, int>();
                    for (uint i = 0; i < hdr.GetNumBlocks(); i++) { var tn = hdr.GetBlockTypeStringById(i); counts[tn] = counts.GetValueOrDefault(tn) + 1; }
                    Console.WriteLine($"  {hdr.GetNumBlocks()} blocks: {string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}"))}");
                    for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                    {
                        switch (hdr.GetBlockById(i))
                        {
                            case nifly.NiBoolData bd: Console.WriteLine($"  [{i}] NiBoolData keys {bd.data.GetNumKeys()}"); break;
                            case nifly.NiColorData cd: Console.WriteLine($"  [{i}] NiColorData keys {cd.data.GetNumKeys()}"); break;
                            case nifly.NiTransformData td:
                                Console.WriteLine($"  [{i}] NiTransformData translations {td.translations.GetNumKeys()} z-rotations {td.zRotations.GetNumKeys()}"
                                    + (td.zRotations.GetNumKeys() > 1 ? $" e.g. {Skyrim.Meshes.NifKeys.Read(td.zRotations, 1)}" : ""));
                                break;
                            case nifly.NiPSysData pd: Console.WriteLine($"  [{i}] NiPSysData max particles {pd.GetNumVertices()}"); break;
                            case nifly.NiPSysBoxEmitter em: Console.WriteLine($"  [{i}] emitter speed {em.speed:G3} size {em.radius:G3} life {em.lifeSpan:G3} box {em.width:G3}x{em.height:G3}x{em.depth:G3} node {em.emitterNodeRef.index}"); break;
                            case nifly.NiFloatInterpolator fi: Console.WriteLine($"  [{i}] birth rate {fi.floatValue:G3}"); break;
                            case nifly.BSEffectShaderProperty es: Console.WriteLine($"  [{i}] texture {es.sourceTexture.get()}"); break;
                        }
                    }
                }
                foreach (var p in vfx.Problems) Console.WriteLine($"  problem: {p}");
                return 0;
            }
            case "mw-emitters":
            {
                // Dev aid: particle emitters of Morrowind models (--model a;b): position, size, rise, texture.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                var mwData = Morrowind.Tes3Data.Open(dataFiles);
                var conv = new Morrowind.MwMeshConverter(mwData, new Morrowind.MwTextures(mwData, "out/emittertest"));
                foreach (var model in opts["model"].Split(';'))
                {
                    var r = conv.Convert(model, "out/emittertest");
                    Console.WriteLine($"{model}: bounds {r.Min} - {r.Max}");
                    foreach (var e in r.Emitters) Console.WriteLine($"   at {e.Position} size {e.Size:F1} rise {e.Rise:F1} texture {e.Texture}");
                }
                return 0;
            }
            case "mw-dialogue-stats":
            {
                // Dev aid: Morrowind dialogue by topic type, filters used, condition kinds and function codes, results.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                var world = Morrowind.MwWorld.Load(dataFiles);
                var dlg = Morrowind.MwDialogue.Load(world.Plugins);
                if (opts.TryGetValue("grep", out var dgrep))
                {
                    // The responses whose text or result script matches --grep (regex), with their filters.
                    var drx = new System.Text.RegularExpressions.Regex(dgrep, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                    foreach (var t in dlg.Topics.Values)
                        foreach (var i in t.Infos.Where(i => drx.IsMatch(i.Text) || drx.IsMatch(i.Result ?? "")))
                            Console.WriteLine($"[{t.Type}] {t.Name} | actor {i.Actor} class {i.Class} faction {i.Faction} pcfaction {i.PcFaction}:{i.PcRank} | "
                                + string.Join(", ", i.Conditions.Select(c => $"{c.Kind}{c.Function} {c.Name} {c.Compare} {c.Value}"))
                                + $"\n    \"{i.Text}\"\n    result: {(i.Result ?? "").Replace("\r", "").Replace("\n", " / ")}");
                    return 0;
                }
                foreach (var g in dlg.Topics.Values.GroupBy(t => t.Type))
                    Console.WriteLine($"{g.Key}: {g.Count()} topics, {g.Sum(t => t.Infos.Count)} responses, {g.Sum(t => t.Infos.Sum(i => i.Text.Length))} chars");
                var infos = dlg.Topics.Values.Where(t => t.Type != Morrowind.MwDialogue.TopicType.Journal).SelectMany(t => t.Infos).ToList();
                Console.WriteLine($"filters: actor {infos.Count(i => i.Actor != null)}, race {infos.Count(i => i.Race != null)}, class {infos.Count(i => i.Class != null)}, faction {infos.Count(i => i.Faction != null)}, cell {infos.Count(i => i.Cell != null)}, pc faction {infos.Count(i => i.PcFaction != null)}, sex {infos.Count(i => i.Sex >= 0)}, disposition {infos.Count(i => i.Disposition > 0)}, result scripts {infos.Count(i => i.Result != null)}, sounds {infos.Count(i => i.Sound != null)}");
                foreach (var g in infos.SelectMany(i => i.Conditions).GroupBy(c => c.Kind == '1' ? "func " + c.Function : "kind " + c.Kind).OrderByDescending(g => g.Count()).Take(40))
                    Console.WriteLine($"  {g.Key}: {g.Count()}  e.g. {g.First().Name} {g.First().Compare} {g.First().Value}");
                return 0;
            }
            case "upscale-textures":
            {
                // A game's textures (--game Daggerfall or Morrowind) at four times their size, from the user's own install:
                // exported as raw pixels to tools\upscale\in-df or in (df-/mw-upscale-export), then Real-ESRGAN
                // (tools/upscale/upscale.py) into the folder the game's build reads (UpscaleFolder); textures already there
                // are kept, so a stopped run resumes. On the processor with the fast model unless --gpu (the graphics card
                // with x4plus: sustained upscaling runs it hot). convert runs it before building each game.
                var game = Enum.Parse<Game>(opts.GetValueOrDefault("game", "Daggerfall"), ignoreCase: true);
                if (game is not (Game.Daggerfall or Game.Morrowind)) { Console.Error.WriteLine("upscale-textures: --game Daggerfall or Morrowind"); return 1; }
                string py = Setup.Dependencies.Python ?? throw new InvalidOperationException("the Python environment is missing: run setup");
                string input = Path.Combine(Setup.Dependencies.Root, "upscale", game == Game.Daggerfall ? "in-df" : "in");
                string output = UpscaleFolder(game);
                Directory.CreateDirectory(output);
                if (Main([game == Game.Daggerfall ? "df-upscale-export" : "mw-upscale-export", "--out", input]) != 0) return 1;
                bool gpu = opts.ContainsKey("gpu");
                string model = gpu ? Setup.Dependencies.UpscaleModel : Setup.Dependencies.FastUpscaleModel;
                if (!File.Exists(model)) { Console.Error.WriteLine($"upscale-textures: {model} is missing: run setup"); return 1; }
                string[] device = gpu ? ["--device", "cuda"] : ["--device", "cpu", "--threads", Math.Max(2, Environment.ProcessorCount - 2).ToString()];
                return Setup.ToolScripts.Run(py, ["-u", Setup.ToolScripts.Path("upscale", "upscale.py"), "--input", input, "--output", output, "--model", model, .. device]);
            }
            case "df-upscale-export":
            {
                // Daggerfall's wall, floor and building textures as raw pixels (--out, default tools/upscale/in-df) for
                // tools/upscale/upscale.py; its output in tools/upscale/out-df replaces Scale2x (Skyrim/Meshes/DfTextureExport).
                int n = Skyrim.Meshes.DfTextureExport.ExportAll(arena2, opts.GetValueOrDefault("out", "tools/upscale/in-df"));
                // And the overland's grass sprites (Skyrim/Terrain/DfGrass), see-through where Daggerfall's are.
                n += Skyrim.Meshes.DfTextureExport.ExportSprites(arena2, opts.GetValueOrDefault("out", "tools/upscale/in-df"), Skyrim.Terrain.DfGrass.Sprites);
                Console.WriteLine($"{n} textures exported");
                return 0;
            }
            case "mw-upscale-export":
            {
                // Every Morrowind texture as raw pixels (--out, default tools/upscale/in) for tools/upscale/upscale.py.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                int n = Morrowind.MwTextures.ExportAll(Morrowind.Tes3Data.Open(dataFiles), opts.GetValueOrDefault("out", "tools/upscale/in"));
                Console.WriteLine($"{n} textures exported");
                return 0;
            }
            case "mw-near":
            {
                // Dev aid: Morrowind exterior references within --r units of --at x,y, with model and how many of the
                // model's shapes have no texture (those render purple / are meant to be invisible).
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                var world = Morrowind.MwWorld.Load(dataFiles);
                var data = Morrowind.Tes3Data.Open(dataFiles);
                var at = opts["at"].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                float radius = float.Parse(opts.GetValueOrDefault("r", "600"), System.Globalization.CultureInfo.InvariantCulture);
                foreach (var r in world.Exteriors.Values.SelectMany(c => c.Refs).Where(r => MathF.Sqrt((r.Position.X - at[0]) * (r.Position.X - at[0]) + (r.Position.Y - at[1]) * (r.Position.Y - at[1])) < radius))
                {
                    string? model = world.Model(r.BaseId);
                    string info = "";
                    if (model is { Length: > 0 } && data.Read(@"meshes\" + model) is { } bytes)
                    {
                        var nif = Morrowind.Nif4.Read(bytes, model);
                        var shapes = nif.Blocks.OfType<Morrowind.NiTriShape>().Where(t => t is not Morrowind.NiParticles).ToList();
                        int untextured = shapes.Count(t => !t.Properties.Select(nif.Get<Morrowind.NiTexturingProperty>).Any(p => p?.Base is not null)
                            && !Ancestors(nif, t).SelectMany(n => n.Properties).Select(nif.Get<Morrowind.NiTexturingProperty>).Any(p => p?.Base is not null));
                        var alpha = nif.Blocks.OfType<Morrowind.NiMaterialProperty>().Select(m => m.Alpha).DefaultIfEmpty(1).Min();
                        info = $"{shapes.Count} shapes, {untextured} untextured, min material alpha {alpha}";
                    }
                    Console.WriteLine($"{r.BaseId,-28} {world.Objects.GetValueOrDefault(r.BaseId)?.Type} {model} at {r.Position} {info}");
                }
                return 0;

                static IEnumerable<Morrowind.NiNode> Ancestors(Morrowind.Nif4 nif, Morrowind.NifBlock child) =>
                    nif.Blocks.OfType<Morrowind.NiNode>().Where(n => n.Children.Contains(child.Index)).SelectMany(p => Ancestors(nif, p).Prepend(p));
            }
            case "mw-shape-census":
            {
                // Dev aid: across Morrowind's models, visible shapes with a texture but no UVs, and with no texture at all
                // (by whether they carry vertex colours / material colour), with examples.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind)!;
                var data = Morrowind.Tes3Data.Open(dataFiles);
                var groups = new Dictionary<string, List<string>>();
                void Add(string k, string v) { (groups.TryGetValue(k, out var l) ? l : groups[k] = []).Add(v); }
                foreach (var name in data.List("meshes").Where(n => n.EndsWith(".nif")))
                {
                    Morrowind.Nif4 nif;
                    try { nif = Morrowind.Nif4.Read(data.Read(name)!, name); } catch { continue; }
                    var parent = new Dictionary<int, Morrowind.NiNode>();
                    foreach (var n in nif.Blocks.OfType<Morrowind.NiNode>()) foreach (var c in n.Children) parent[c] = n;
                    foreach (var shape in nif.Blocks.OfType<Morrowind.NiTriShape>().Where(t => t is not Morrowind.NiParticles))
                    {
                        var chain = new List<Morrowind.NiNode> { shape };
                        for (var p = parent.GetValueOrDefault(shape.Index); p is not null; p = parent.GetValueOrDefault(p.Index)) chain.Add(p);
                        if (chain.Any(n => n.Type == "RootCollisionNode" || n.Hidden)) continue;
                        var props = chain.SelectMany(n => n.Properties).Select(nif.Get<Morrowind.NifBlock>).ToList();
                        bool textured = props.OfType<Morrowind.NiTexturingProperty>().Any(t => t.Base is not null);
                        var d = nif.Get<Morrowind.NiTriShapeData>(shape.Data);
                        if (d is null) continue;
                        if (textured && d.UVSets.Length == 0) Add("textured, no UVs", $"{name} '{shape.Name}'");
                        if (!textured) Add(d.Colors.Length > 0 ? "untextured, vertex colours" : "untextured, material only", $"{name} '{shape.Name}'");
                    }
                }
                // Emissive materials on placed models (Skyrim adds emissive as a flat tint, Morrowind multiplies it in).
                if (opts.ContainsKey("emissive"))
                {
                    groups.Clear();
                    foreach (var name in data.List("meshes").Where(n => n.EndsWith(".nif") && !Path.GetFileName(n).StartsWith("x")))
                    {
                        Morrowind.Nif4 nif2;
                        try { nif2 = Morrowind.Nif4.Read(data.Read(name)!, name); } catch { continue; }
                        foreach (var m in nif2.Blocks.OfType<Morrowind.NiMaterialProperty>().Where(m => m.Emissive.X + m.Emissive.Y + m.Emissive.Z > 0.1f))
                            Add(m.Emissive.X + m.Emissive.Y + m.Emissive.Z > 1.5f ? "bright emissive" : "dim emissive", $"{name} {m.Emissive}");
                    }
                }
                foreach (var (k, v) in groups) { Console.WriteLine($"{k}: {v.Count} shapes"); foreach (var e in v.Take(opts.ContainsKey("all") ? int.MaxValue : 8)) Console.WriteLine($"   {e}"); }
                return 0;
            }
            case "ob-actor-bases":
            {
                // Dev aid: what Oblivion's placed NPC/creature references point at, by record type.
                string dataDir = GameInstalls.DataDir(Game.Oblivion)!;
                using var ob = Mutagen.Bethesda.Oblivion.OblivionMod.CreateFromBinaryOverlay(Path.Combine(dataDir, "Oblivion.esm"), Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion);
                var types = ob.EnumerateMajorRecords().GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.First().GetType().Name.Replace("BinaryOverlay", ""));
                var acre = ob.EnumerateMajorRecords<Mutagen.Bethesda.Oblivion.IPlacedCreatureGetter>().Select(r => r.Base.FormKey);
                var achr = ob.EnumerateMajorRecords<Mutagen.Bethesda.Oblivion.IPlacedNpcGetter>().Select(r => r.Base.FormKey);
                if (opts.ContainsKey("weathers"))
                {
                    foreach (var w in ob.Weathers) Console.WriteLine($"  WTHR {w.FormKey} {w.EditorID}");
                    foreach (var rg in ob.Regions.Take(int.Parse(opts["weathers"])))
                        Console.WriteLine($"  REGN {rg.EditorID} ws={rg.Worldspace.FormKey} areas={rg.Areas.Count} weathers=[{string.Join(", ", rg.Weather?.Weathers.Select(x => x.Weather.FormKey.ID.ToString("X6") + ":" + x.Chance) ?? [])}]");
                    return 0;
                }
                if (opts.TryGetValue("armor", out var apat))
                {
                    foreach (var ar in ob.Armors.Where(x => (x.Name ?? "").Contains(apat, StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"  {ar.Name}: biped {ar.ClothingFlags?.BipedFlags} ({(uint)(ar.ClothingFlags?.BipedFlags ?? 0):X}) general {ar.ClothingFlags?.GeneralFlags}");
                    return 0;
                }
                var lvlc = ob.LeveledCreatures.Select(l => l.FormKey).ToHashSet();
                Console.WriteLine($"LVLC records: {lvlc.Count}");
                var users = ob.EnumerateMajorRecords().Where(r => r.EnumerateFormLinks().Any(l => lvlc.Contains(l.FormKey)))
                    .GroupBy(r => r.GetType().Name.Replace("BinaryOverlay", "")).Select(g => $"{g.Key} {g.Count()}");
                Console.WriteLine("referenced by: " + string.Join(", ", users));
                foreach (var g in ob.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(b => b.Cells)
                    .Select(c => (c.EditorID, N: c.Persistent.Concat(c.Temporary).OfType<Mutagen.Bethesda.Oblivion.IPlacedObjectGetter>().Count(r => lvlc.Contains(r.Base.FormKey))))
                    .Where(x => x.N > 0).OrderByDescending(x => x.N).Take(8))
                    Console.WriteLine($"  {g.EditorID}: {g.N} leveled spawns");
                foreach (var (label, keys) in new[] { ("ACRE", acre), ("ACHR", achr) })
                    Console.WriteLine($"{label}: " + string.Join(", ", keys.GroupBy(k => types.GetValueOrDefault(k, "?")).Select(g => $"{g.Key} {g.Count()}")));
                return 0;
            }
            case "ob-trees":
            {
                // Dev aid: Oblivion tree records (SpeedTree models), counted by placed references.
                string dataDir = GameInstalls.DataDir(Game.Oblivion)!;
                using var ob = Mutagen.Bethesda.Oblivion.OblivionMod.CreateFromBinaryOverlay(Path.Combine(dataDir, "Oblivion.esm"), Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion);
                var trees = ob.Trees.ToDictionary(t => t.FormKey);
                var counts = ob.EnumerateMajorRecords<Mutagen.Bethesda.Oblivion.IPlacedObjectGetter>().GroupBy(r => r.Base.FormKey).Where(g => trees.ContainsKey(g.Key)).ToDictionary(g => g.Key, g => g.Count());
                foreach (var t in trees.Values.OrderByDescending(t => counts.GetValueOrDefault(t.FormKey)).Take(60))
                    Console.WriteLine($"{counts.GetValueOrDefault(t.FormKey),7} {t.EditorID,-28} {t.Model?.File}");
                Console.WriteLine($"{trees.Count} tree types");
                return 0;
            }
            case "mw-stats":
            {
                // Record counts by type for each Morrowind plugin in the install, and the archives' file counts.
                string dataFiles = GameInstalls.DataDir(Game.Morrowind) ?? throw new DirectoryNotFoundException("Morrowind isn't installed through Steam or GOG");
                if (opts.TryGetValue("refs", out var refBase))
                {
                    // --refs ID: the cells holding references to that base object (e.g. PrisonMarker), with how many.
                    var w = Morrowind.MwWorld.Load(dataFiles);
                    foreach (var c in w.Interiors.Values.Concat(w.Exteriors.Values))
                        if (c.Refs.Count(r => r.BaseId.Equals(refBase, StringComparison.OrdinalIgnoreCase)) is > 0 and var n)
                            Console.WriteLine($"  {(c.Name.Length > 0 ? c.Name : "(wilderness)")} [{c.GridX},{c.GridY}]{(w.Interiors.ContainsKey(c.Name) && w.Interiors[c.Name] == c ? " inside" : "")}: {n}");
                    return 0;
                }
                foreach (var file in Directory.GetFiles(dataFiles).Where(f => f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)).OrderBy(f => f.EndsWith(".esp")).ThenBy(f => f))
                {
                    var plugin = Morrowind.Tes3Plugin.Load(file);
                    Console.WriteLine($"{plugin.FileName}: masters [{string.Join(", ", plugin.Masters)}] {plugin.Records.Count} records");
                    Console.WriteLine("  " + string.Join(" ", plugin.Records.GroupBy(r => r.Type).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}")));
                }
                var data = Morrowind.Tes3Data.Open(dataFiles);
                foreach (var bsa in data.Archives)
                    Console.WriteLine($"{Path.GetFileName(bsa.FilePath)}: " + string.Join(" ", bsa.Names.GroupBy(n => n.Split('\\')[0]).Select(g => $"{g.Key}:{g.Count()}")));
                return 0;
            }
            case "dump-books":
            {
                string outDir = opts.GetValueOrDefault("out", "out/books");
                Directory.CreateDirectory(outDir);
                foreach (var book in DfBook.LoadAll(arena2))
                {
                    var text = string.Join("\n\n", book.Paragraphs.Select(p => (p.Align == DfAlign.Center ? "[C] " : "") + p.Text));
                    File.WriteAllText(Path.Combine(outDir, Path.ChangeExtension(book.FileName, ".md")),
                        $"# {book.Title}\nauthor={book.Author} price={book.Price} pages={book.PageCount} naughty={book.IsNaughty}\n\n{text}\n");
                }
                Console.WriteLine($"Wrote books to {Path.GetFullPath(outDir)}");
                return 0;
            }
            case "nifly-api":
            {
                // Dev aid: public methods/properties of niflysharp types matching --type, filtered by --name (regex).
                var trx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("type", "^NifFile$"));
                var nrx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var t in typeof(nifly.NifFile).Assembly.GetExportedTypes().Where(t => trx.IsMatch(t.Name)))
                {
                    Console.WriteLine($"{t.Name} : {t.BaseType?.Name}");
                    foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly).Where(m => !m.IsSpecialName && nrx.IsMatch(m.Name)))
                        Console.WriteLine($"    {m.ReturnType.Name} {m.Name}({string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
                    foreach (var p in t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Where(p => nrx.IsMatch(p.Name)))
                        Console.WriteLine($"    prop {p.PropertyType.Name} {p.Name}");
                }
                return 0;
            }
            case "prop-type":
            {
                // Dev aid: the full type of a Mutagen Skyrim class's property (--type T --prop P), with enum values.
                var t = typeof(SkyrimMod).Assembly.GetExportedTypes().First(x => x.Name == opts["type"] && !x.IsInterface);
                var p = t.GetProperty(opts["prop"])!;
                var pt = Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType;
                Console.WriteLine(pt.FullName);
                if (pt.IsEnum) Console.WriteLine(string.Join(", ", Enum.GetNames(pt)));
                foreach (var g in pt.GetGenericArguments()) Console.WriteLine("  arg " + g.FullName);
                return 0;
            }
            case "reflect":
            {
                // Dev aid: members of Mutagen Skyrim types matching --name (regex), to check the record API.
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "^$"));
                // --game oblivion: the Oblivion record types instead.
                var asm = opts.GetValueOrDefault("game") == "oblivion" ? typeof(Mutagen.Bethesda.Oblivion.OblivionMod).Assembly : typeof(SkyrimMod).Assembly;
                foreach (var t in asm.GetExportedTypes().Where(t => rx.IsMatch(t.Name) && !t.IsInterface))
                {
                    Console.WriteLine($"{t.Name} : {t.BaseType?.Name}");
                    if (t.IsEnum) Console.WriteLine("    " + string.Join(", ", Enum.GetNames(t)));
                    else Console.WriteLine("    " + string.Join(", ", t.GetProperties().Where(p => p.DeclaringType == t).Select(p => $"{p.Name}:{p.PropertyType.Name}")));
                }
                return 0;
            }
            case "verify-spells":
            {
                // Every converted spell: effects resolve, and each effect's casting/delivery matches its spell (the engine
                // refuses mismatches); every tome teaches a spell of this plugin.
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var mgefs = plugin.MagicEffects.Concat(skyrim.MagicEffects).GroupBy(m => m.FormKey).ToDictionary(g => g.Key, g => g.First());
                var spells = plugin.Spells.ToDictionary(s => s.FormKey);
                int bad = 0;
                foreach (var s in plugin.Spells.OrderBy(s => s.EditorID))
                {
                    var parts = new List<string>();
                    foreach (var e in s.Effects)
                    {
                        if (!mgefs.TryGetValue(e.BaseEffect.FormKey, out var m)) { bad++; parts.Add("UNRESOLVED"); continue; }
                        bool ok = m.CastType == s.CastType && m.TargetType == s.TargetType;
                        if (!ok) bad++;
                        string script = m.VirtualMachineAdapter?.Scripts.FirstOrDefault()?.Name is { } sn ? $" script {sn}" : "";
                        parts.Add($"{m.EditorID} [{m.MagicSkill} {(MagicEffectArchetype.TypeEnum)(int)((dynamic)m.Archetype).Type} {m.Archetype.ActorValue}{script}] m{e.Data?.Magnitude:0.#} d{e.Data?.Duration} a{e.Data?.Area}{(ok ? "" : $" MISMATCH {m.CastType}/{m.TargetType}")}");
                    }
                    if (opts.ContainsKey("all") || !s.EditorID!.StartsWith("DFTrap"))
                        Console.WriteLine($"{s.EditorID,-34} {s.Name,-22} {s.TargetType,-11} cost {s.BaseCost,3}  " + string.Join("  ", parts));
                }
                foreach (var b in plugin.Books.Where(b => b.Teaches is IBookSpellGetter))
                    if (!spells.ContainsKey(((IBookSpellGetter)b.Teaches!).Spell.FormKey)) { bad++; Console.WriteLine($"TOME {b.EditorID} teaches an unknown spell"); }
                Console.WriteLine($"{plugin.Spells.Count} spells, {plugin.Books.Count(b => b.Teaches is IBookSpellGetter)} tomes, {plugin.MagicEffects.Count} effects, {bad} problems");
                return bad == 0 ? 0 : 1;
            }
            case "npc-info":
            {
                // Dev aid: actors (and leveled lists) whose EditorID matches --name, in Skyrim.esm and the SE DLC masters.
                // With --plugins a;b (paths): as the game loads those after the masters, each match's winning copy with
                // its template flags, race, skin, abilities, scripts and the race's own abilities.
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "^$"));
                if (opts.TryGetValue("plugins", out var npcPlugins))
                {
                    var loaded = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => Path.Combine(skyrimData, f))
                        .Concat(npcPlugins.Split(';', StringSplitOptions.RemoveEmptyEntries))
                        .Select(p => (ISkyrimModGetter)SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE)).ToList();
                    var lc = loaded.ToImmutableLinkCache();
                    string Ed(FormKey k) => lc.TryResolve<ISkyrimMajorRecordGetter>(k, out var r) ? r.EditorID ?? k.ToString() : k + " (not found)";
                    foreach (var key in loaded.SelectMany(m => m.Npcs).Where(n => rx.IsMatch(n.EditorID ?? "")).Select(n => n.FormKey).Distinct())
                    {
                        var ctx = lc.ResolveContext<INpc, INpcGetter>(key);
                        var n = ctx.Record;
                        Console.WriteLine($"{n.EditorID} {key} from {ctx.ModKey}: race {Ed(n.Race.FormKey)}, skin {(n.WornArmor.IsNull ? "-" : Ed(n.WornArmor.FormKey))}, attack race {(n.AttackRace.IsNull ? "-" : Ed(n.AttackRace.FormKey))}, height {n.Height}");
                        Console.WriteLine($"  template {(n.Template.IsNull ? "-" : Ed(n.Template.FormKey))} flags {n.Configuration.TemplateFlags}; config {n.Configuration.Flags}");
                        Console.WriteLine($"  abilities: {string.Join(", ", (n.ActorEffect ?? []).Select(s => Ed(s.FormKey)))}");
                        Console.WriteLine($"  scripts: {string.Join(", ", n.VirtualMachineAdapter?.Scripts.Select(s => s.Name) ?? [])}");
                        if (lc.TryResolve<IRaceGetter>(n.Race.FormKey, out var race))
                            Console.WriteLine($"  race abilities: {string.Join(", ", (race.ActorEffect ?? []).Select(s => Ed(s.FormKey)))}; race flags {race.Flags}; size {race.Size}");
                    }
                    return 0;
                }
                foreach (var master in new[] { "Skyrim.esm", "Dawnguard.esm", "Dragonborn.esm" })
                {
                    using var m = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, master), SkyrimRelease.SkyrimSE);
                    var races = m.Races.ToDictionary(r => r.FormKey, r => r.EditorID);
                    foreach (var n in m.Npcs.Where(n => n.FormKey.ModKey == m.ModKey && rx.IsMatch(n.EditorID ?? "")))
                    {
                        string level = n.Configuration.Level switch { INpcLevelGetter l => $"L{l.Level}", IPcLevelMultGetter p => $"x{p.LevelMult} [{n.Configuration.CalcMinLevel}-{n.Configuration.CalcMaxLevel}]", _ => "?" };
                        Console.WriteLine($"NPC  {master,-14} {n.EditorID,-36} {n.FormKey.ID:X6} \"{n.Name}\" {level} race {races.GetValueOrDefault(n.Race.FormKey, n.Race.FormKey.ToString())} hp+{n.Configuration.HealthOffset} template {n.Template.FormKey.IsNull} flags {n.Configuration.Flags}");
                    }
                    foreach (var l in m.LeveledNpcs.Where(n => n.FormKey.ModKey == m.ModKey && rx.IsMatch(n.EditorID ?? "")))
                        Console.WriteLine($"LVLN {master,-14} {l.EditorID,-36} {l.FormKey.ID:X6} flags {l.Flags} entries {l.Entries?.Count}: " + string.Join(", ", (l.Entries ?? []).Take(8).Select(e => $"L{e.Data?.Level}")));
                }
                return 0;
            }
            case "list-forms":
            {
                // Dev aid: every placeable Skyrim.esm record with its type, EditorID, model and bounds (TSV), for choosing clutter.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, opts.GetValueOrDefault("master", "Skyrim.esm")), SkyrimRelease.SkyrimSE);
                using var w = new StreamWriter(opts.GetValueOrDefault("out", "out/skyrim-forms.tsv"));
                foreach (var r in skyrim.EnumerateMajorRecords().Where(r => r is IPlaceableObjectGetter or INpcGetter or ILeveledNpcGetter))
                {
                    if (r.EditorID is null) continue;
                    string model = (r as IModeledGetter)?.Model?.File.DataRelativePath.Path ?? "";
                    string bounds = r is IObjectBoundedGetter { ObjectBounds: { } b } ? $"{b.Second.X - b.First.X}x{b.Second.Y - b.First.Y}x{b.Second.Z - b.First.Z}" : "";
                    w.WriteLine($"{r.GetType().Name.Replace("BinaryOverlay", "")}\t{r.EditorID}\t{r.FormKey.ID:X6}\t{bounds}\t{model}");
                }
                return 0;
            }
            case "verify-clutter":
            {
                // Every clutter reference touches what it stands on or hangs from: the bottom of floor clutter within a
                // few units of the dungeon floor under it, the top of hanging clutter at the ceiling over it.
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var shapes = new Skyrim.PropReplacer(skyrim, skyrimData);
                var clutterIds = Skyrim.ClutterPlacer.Table.Values.SelectMany(s => s.Parts.Select(p => p.Id).Concat(s.Variants ?? [])).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var names = skyrim.EnumerateMajorRecords().OfType<IPlaceableObjectGetter>().Where(r => r.EditorID is { } e && clutterIds.Contains(e)).ToDictionary(r => r.FormKey, r => r.EditorID!);
                foreach (var b in plugin.Books) names.TryAdd(b.FormKey, b.EditorID!);
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                var polys = DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []).Polygons(DfModel.LoadAll(arena2))
                    .Select(p => (Points: p.Points.Select(v => new System.Numerics.Vector3(v.X, v.Z, v.Y) * Skyrim.Meshes.BlockMeshBuilder.Scale).ToArray(), Up: p.Normal.Y)).ToList();
                float? Surface(float x, float y, float z, bool up)
                {
                    float? best = null;
                    foreach (var (pts, n) in polys.Where(p => up ? p.Up < -0.7f : p.Up > 0.7f))
                        for (int i = 1; i + 1 < pts.Length; i++)
                        {
                            var (a, b2, c) = (pts[0], pts[i], pts[i + 1]);
                            float den = (b2.Y - c.Y) * (a.X - c.X) + (c.X - b2.X) * (a.Y - c.Y);
                            if (MathF.Abs(den) < 1e-6f) continue;
                            float w0 = ((b2.Y - c.Y) * (x - c.X) + (c.X - b2.X) * (y - c.Y)) / den, w1 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / den, w2 = 1 - w0 - w1;
                            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                            float h = w0 * a.Z + w1 * b2.Z + w2 * c.Z;
                            if (up ? h >= z - 40 && (best is null || h < best) : h <= z + 40 && (best is null || h > best)) best = h;
                        }
                    return best;
                }
                int bad = 0, total = 0;
                var cell = plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).First(c => c.EditorID == "DF" + Skyrim.LocationPlacer.Sanitize(loc.Name));
                foreach (var r in cell.Temporary.OfType<IPlacedObjectGetter>().Where(r => names.ContainsKey(r.Base.FormKey)))
                {
                    total++;
                    var (min, max, _) = shapes.SkyrimShape(r.Base.FormKey);
                    float sc = r.Scale ?? 1;
                    var p = r.Placement!.Position;
                    bool flipped = MathF.Abs(r.Placement.Rotation.X) > 3;
                    float bottom = flipped ? p.Z - max.Z * sc : p.Z + min.Z * sc, top = flipped ? p.Z - min.Z * sc : p.Z + max.Z * sc;
                    float? floor = Surface(p.X, p.Y, bottom, up: false), ceiling = Surface(p.X, p.Y, top, up: true);
                    float gapFloor = floor is { } f ? bottom - f : float.NaN, gapCeiling = ceiling is { } cz ? cz - top : float.NaN;
                    bool ok = MathF.Abs(gapFloor) <= 6 || MathF.Abs(gapCeiling) <= 6 || (names[r.Base.FormKey].StartsWith("Bone") || names[r.Base.FormKey].StartsWith("DF_Book")) && gapFloor is > -6 and < 400;   // parts resting on another part (skulls on pikes)
                    if (!ok) bad++;
                    if (!ok || opts.ContainsKey("all"))
                        Console.WriteLine($"{(ok ? "  " : "! ")}{names[r.Base.FormKey],-30} at ({p.X:F0},{p.Y:F0},{p.Z:F0}) scale {sc:F2} bottom {bottom:F0} top {top:F0}  floor gap {gapFloor:F0}  ceiling gap {gapCeiling:F0}");
                }
                Console.WriteLine($"{total} clutter references, {bad} not touching floor or ceiling");
                return bad == 0 ? 0 : 1;
            }
            case "texture-sheets":
            {
                // One contact sheet per group of texture archives (--groups "12,112,41;26,126"), with each archive's name.
                string outDir = opts.GetValueOrDefault("out", "out/texsheets");
                Directory.CreateDirectory(outDir);
                int n = 0;
                foreach (var group in opts["groups"].Split(';'))
                {
                    var archives = group.Split(',').Select(int.Parse).Where(a => DfTexture.Load(arena2, a) is not null).ToList();
                    DfTexture.Sheet(arena2, archives, Path.Combine(outDir, $"sheet{n}.png"), cell: 64, columns: 16);
                    Console.WriteLine($"sheet{n}: " + string.Join(", ", archives.Select(a => $"{a} {DfTexture.Load(arena2, a)!.Name} ({DfTexture.Load(arena2, a)!.Count})")));
                    n++;
                }
                return 0;
            }
            case "flat-sheets":
            {
                // Contact sheets of Daggerfall sprite archives (--archives 200,201), to identify clutter by eye.
                string outDir = opts.GetValueOrDefault("out", "out/flats");
                Directory.CreateDirectory(outDir);
                foreach (var a in opts.GetValueOrDefault("archives", "200").Split(',').Select(int.Parse))
                {
                    var t = DfTexture.Load(arena2, a);
                    if (t is null) { Console.WriteLine($"TEXTURE.{a:D3} missing"); continue; }
                    DfTexture.Sheet(arena2, [a], Path.Combine(outDir, $"flats-{a}.png"));
                    Console.WriteLine($"{a} {t.Name}: {t.Count} records  " + string.Join(" ", Enumerable.Range(0, t.Count).Select(r => { var i = t.Image(r); var s = i.WorldSize; return $"{r}:{s.Width:F0}x{s.Height:F0}"; })));
                }
                return 0;
            }
            case "flat-census":
            {
                // How often each sprite (archive.record) appears in dungeon blocks, overall and in one dungeon (--name).
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var all = new Dictionary<(int, int), int>();
                foreach (var n in bsa.Entries.Select(e => e.Name).Where(n => n.EndsWith(".RDB", StringComparison.OrdinalIgnoreCase)))
                    foreach (var o in DfDungeonBlock.Load(bsa, n).Objects.Where(o => o.Type == DfRdbObjectType.Flat))
                        all[(o.TextureArchive, o.TextureRecord)] = all.GetValueOrDefault((o.TextureArchive, o.TextureRecord)) + 1;
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                var here = DfDungeon.Load(loc, bsa, []).Blocks.SelectMany(b => b.Block.Objects).Where(o => o.Type == DfRdbObjectType.Flat)
                    .GroupBy(o => (o.TextureArchive, o.TextureRecord)).ToDictionary(g => g.Key, g => g.Count());
                foreach (var arch in all.GroupBy(k => k.Key.Item1).OrderBy(g => g.Key))
                    Console.WriteLine($"{arch.Key}: {arch.Sum(k => k.Value)} total; " + string.Join(" ", arch.OrderBy(k => k.Key.Item2).Select(k => $"{k.Key.Item2}x{k.Value}{(here.TryGetValue(k.Key, out var h) ? $"[{h}]" : "")}")));
                return 0;
            }
            case "dump-spells":
            {
                foreach (var s in DfSpells.Load(arena2))
                    Console.WriteLine($"{s.Index,3} {s.Name,-24} {s.Element,-6} {s.Range,-16} cost {s.Cost,3} icon {s.Icon,2}  " + string.Join("  ",
                        s.Effects.Select(e => $"[{e.Type},{e.SubType} dur {e.DurationBase}+{e.DurationMod}/{e.DurationPerLevel} chance {e.ChanceBase}+{e.ChanceMod}/{e.ChancePerLevel} mag {e.MagnitudeBaseLow}-{e.MagnitudeBaseHigh}+{e.MagnitudeLevelBase}-{e.MagnitudeLevelHigh}/{e.MagnitudePerLevel}]")));
                // Spell indexes used by dungeon spell traps (action flag 9), across every dungeon block.
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var used = new Dictionary<int, int>();
                foreach (var name in bsa.Entries.Select(e => e.Name).Where(n => n.EndsWith(".RDB", StringComparison.OrdinalIgnoreCase)))
                    foreach (var o in DfDungeonBlock.Load(bsa, name).Objects.Where(o => o.Action?.Flags == 9))
                        used[o.SoundIndex] = used.GetValueOrDefault(o.SoundIndex) + 1;
                Console.WriteLine("trap spell indexes: " + string.Join(", ", used.OrderBy(k => k.Key).Select(k => $"{k.Key} x{k.Value}")));
                return 0;
            }
            case "probe-vanilla":
            {
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                foreach (var b in skyrim.Books.Where(b => b.EditorID?.Contains(opts.GetValueOrDefault("filter", "Lusty")) == true).Take(2))
                    Console.WriteLine($"== {b.EditorID} model={b.Model?.File} type={b.Type}\n{b.BookText.String?[..Math.Min(800, b.BookText.String.Length)]}\n");
                return 0;
            }
            case "probe-worlds":
            {
                // Report each worldspace's exterior cell grid extent in an existing plugin.
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, opts["plugin"]), SkyrimRelease.SkyrimSE);
                foreach (var ws in plugin.Worldspaces)
                {
                    var grids = ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items)
                        .Select(c => c.Grid?.Point).Where(p => p.HasValue).Select(p => p!.Value).ToList();
                    if (grids.Count == 0) continue;
                    Console.WriteLine($"{ws.EditorID,-28} cells={grids.Count,7}  x {grids.Min(p => p.X)}..{grids.Max(p => p.X)}  y {grids.Min(p => p.Y)}..{grids.Max(p => p.Y)}");
                }
                return 0;
            }
            case "locations":
            {
                // Summarise MAPS.BSA: location counts by type, block usage, and a few named samples.
                var locs = DfLocations.LoadAll(arena2);
                var blocks = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                Console.WriteLine($"{locs.Count} locations in {locs.Select(l => l.Region).Distinct().Count()} regions");
                foreach (var g in locs.GroupBy(l => l.Type).OrderByDescending(g => g.Count()))
                    Console.WriteLine($"  {g.Key,-18} {g.Count(),5}  blocks avg {g.Average(l => l.ExteriorBlocks.Length):F1} max {g.Max(l => l.ExteriorBlocks.Length)}  with dungeon {g.Count(l => l.DungeonBlocks.Count > 0)}");
                var rmb = locs.SelectMany(l => l.ExteriorBlocks).ToList();
                var rdb = locs.SelectMany(l => l.DungeonBlocks.Select(b => b.BlockName)).ToList();
                Console.WriteLine($"RMB block placements {rmb.Count}, unique {rmb.Distinct().Count()}, missing from BLOCKS.BSA {rmb.Distinct().Count(n => !blocks.Contains(n))}");
                Console.WriteLine($"RDB block placements {rdb.Count}, unique {rdb.Distinct().Count()}, missing from BLOCKS.BSA {rdb.Distinct().Count(n => !blocks.Contains(n))}");
                Console.WriteLine($"BLOCKS.BSA entries: {blocks.Entries.Count} ({blocks.Entries.Count(e => e.Name.EndsWith(".RMB"))} RMB, {blocks.Entries.Count(e => e.Name.EndsWith(".RDB"))} RDB, {blocks.Entries.Count(e => e.Name.EndsWith(".RDI"))} RDI)");
                string[] btypes = ["Alchemist", "HouseForSale", "Armorer", "Bank", "Town4", "Bookseller", "ClothingStore", "FurnitureStore", "GemStore", "GeneralStore", "Library", "GuildHall", "PawnShop", "WeaponSmith", "Temple", "Tavern", "Palace", "House1", "House2", "House3", "House4", "House5", "House6", "Town23", "Ship"];
                var allBuildings = locs.SelectMany(l => l.Buildings).ToList();
                Console.WriteLine($"buildings: {allBuildings.Count}");
                foreach (var g in allBuildings.GroupBy(b => b.BuildingType).OrderByDescending(g => g.Count()))
                    Console.Write($"{(g.Key < btypes.Length ? btypes[g.Key] : "0x" + g.Key.ToString("x"))}={g.Count()} ");
                Console.WriteLine();
                foreach (var name in new[] { "Daggerfall", "Wayrest", "Sentinel", "Privateer's Hold", "Orsinium", "Castle Necromoghan" })
                    foreach (var l in locs.Where(l => l.Name == name)) Console.WriteLine($"  {l}");
                // --list TYPE: every location of a type with its id, blocks and buildings (e.g. --list Coven).
                if (opts.TryGetValue("list", out var listType) && Enum.TryParse<DfLocationType>(listType, true, out var lt))
                    foreach (var l in locs.Where(l => l.Type == lt))
                        Console.WriteLine($"  {l.LocationId} {l.Name} ({l.RegionName}, region {l.Region}) @({l.PixelX},{l.PixelY}) blocks [{string.Join(",", l.ExteriorBlocks)}] buildings [{string.Join(",", l.Buildings.Select(b => $"{b.BuildingType}/f{b.FactionId}/q{b.Quality}"))}]");
                return 0;
            }
            case "ground-preview":
            {
                // The overland's ground from above around a place (--near NAME, default Daggerfall; --pixels N map pixels
                // across, default 24), one image pixel per land vertex: each land texture's average colour as the land
                // builder weighs them (TerrainBuilder.Weights), lit from the north-west. For tuning the ground's patterns.
                var settings = TerrainSettingsFrom(opts);
                var map = DfWorldMap.Load(arena2);
                var field = Skyrim.Terrain.HeightField.Generate(map, settings);
                string near = opts.GetValueOrDefault("near", "Daggerfall");
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name.Equals(near, StringComparison.OrdinalIgnoreCase));
                int pixels = int.Parse(opts.GetValueOrDefault("pixels", "24"));
                double perVert = (double)settings.PixelsPerCell / Skyrim.Terrain.HeightField.QuadsPerCell;
                int cvx = (int)((loc.PixelX + 0.5) / perVert), cvy = (int)((field.CellsY * settings.PixelsPerCell - (loc.PixelY + 0.5)) / perVert);
                int size = (int)(pixels / perVert);
                var palette = DfTexture.Palette(arena2);
                var colours = Skyrim.Terrain.TerrainBuilder.Ground.Select(g =>
                {
                    var img = DfTexture.Load(arena2, g.Archive)!.Image(g.Record);
                    double r = 0, gg = 0, b = 0;
                    foreach (var i in img.Pixels) { r += palette[i * 3]; gg += palette[i * 3 + 1]; b += palette[i * 3 + 2]; }
                    return (r / img.Pixels.Length, gg / img.Pixels.Length, b / img.Pixels.Length);
                }).ToArray();
                var rgb = new byte[size * size * 3];
                var w = new float[colours.Length];
                Parallel.For(0, size, y =>
                {
                    var wl = new float[colours.Length];
                    for (int x = 0; x < size; x++)
                    {
                        int vx = cvx - size / 2 + x, vy = cvy + size / 2 - y;   // north up
                        Skyrim.Terrain.TerrainBuilder.Weights(field, map, vx, vy, 0, wl);
                        double r = 0, g = 0, b = 0, t = 0;
                        for (int k = 0; k < wl.Length; k++) { r += wl[k] * colours[k].Item1; g += wl[k] * colours[k].Item2; b += wl[k] * colours[k].Item3; t += wl[k]; }
                        double dx = (field.At(vx + 1, vy) - field.At(vx - 1, vy)) / 256.0, dy = (field.At(vx, vy + 1) - field.At(vx, vy - 1)) / 256.0;
                        double shade = Math.Clamp(0.85 + (-dx + dy) * 0.6, 0.35, 1.3);
                        if (field.At(vx, vy) < 0) { r = r * 0.4; g = g * 0.5; b = b * 0.4 + 90; }
                        int o = (y * size + x) * 3;
                        rgb[o] = (byte)Math.Clamp(r / Math.Max(t, 1e-6) * shade, 0, 255);
                        rgb[o + 1] = (byte)Math.Clamp(g / Math.Max(t, 1e-6) * shade, 0, 255);
                        rgb[o + 2] = (byte)Math.Clamp(b / Math.Max(t, 1e-6) * shade, 0, 255);
                    }
                });
                string png = opts.GetValueOrDefault("out", "out/ground-preview.png");
                Util.Png.WriteRgb(png, size, size, rgb);
                Console.WriteLine($"{png}: {size}x{size} vertices ({size * Skyrim.Terrain.HeightField.UnitsPerQuad / 70.0:F0} m) around {loc}");
                return 0;
            }
            case "terrain-preview":
            {
                var settings = TerrainSettingsFrom(opts);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var map = DfWorldMap.Load(arena2);
                var field = Skyrim.Terrain.HeightField.Generate(map, settings);
                Console.WriteLine($"generated {field.VertsX}x{field.VertsY} vertices ({field.CellsX}x{field.CellsY} cells) in {sw.Elapsed.TotalSeconds:F1}s");
                Skyrim.Terrain.TerrainPreview.Report(field, map, opts.GetValueOrDefault("out", "out/terrain-preview.png"), arena2);
                return 0;
            }
            case "find-markers":
            {
                // List map markers whose name matches --name (regex) in --plugin, to pick start points.
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, opts["plugin"]), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var ws in plugin.Worldspaces)
                {
                    var refs = (ws.TopCell?.Persistent ?? []).Concat(ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).SelectMany(c => c.Persistent.Concat(c.Temporary)));
                    foreach (var r in refs.OfType<IPlacedObjectGetter>().Where(r => r.MapMarker?.Name?.String is { } n && rx.IsMatch(n)))
                        Console.WriteLine($"{ws.EditorID,-16} {r.FormKey} {r.MapMarker!.Name} {r.MapMarker.Type} pos={r.Placement?.Position} edid={r.EditorID}");
                }
                return 0;
            }
            case "probe-markers":
            {
                // Dev aid: Mutagen's map marker shape plus how vanilla Skyrim stores a few markers.
                foreach (var t in new[] { typeof(MapMarker), typeof(PlacedObject), typeof(WorldspaceMap) })
                    Console.WriteLine($"{t.Name}: " + string.Join(", ", t.GetProperties().Where(p => p.DeclaringType == t).Select(p => $"{p.Name}:{(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType).Name}")));
                var typeProp = typeof(MapMarker).GetProperty("Type")!.PropertyType;
                typeProp = Nullable.GetUnderlyingType(typeProp) ?? typeProp;
                Console.WriteLine($"{typeProp.FullName}: {string.Join(" ", Enum.GetNames(typeProp))}");
                var flagProp = typeof(MapMarker).GetProperty("Flags")!.PropertyType;
                Console.WriteLine($"{flagProp.FullName}: {string.Join(" ", Enum.GetNames(flagProp))}");
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var tamriel = skyrim.Worldspaces.First(w => w.EditorID == "Tamriel");
                Console.WriteLine($"Tamriel MapData: usable {tamriel.MapData?.UsableDimensions} NW {tamriel.MapData?.NorthwestCellCoords} SE {tamriel.MapData?.SoutheastCellCoords}");
                int shown = 0;
                foreach (var r in tamriel.TopCell!.Persistent.OfType<IPlacedObjectGetter>().Where(r => r.MapMarker is not null))
                {
                    Console.WriteLine($"  marker base={r.Base.FormKey} name={r.MapMarker!.Name} type={r.MapMarker.Type} flags={r.MapMarker.Flags} majorFlags={r.SkyrimMajorRecordFlags} pos={r.Placement?.Position}");
                    if (++shown >= 4) break;
                }
                return 0;
            }
            case "probe-land":
            {
                // Dev aid: dump the shape of Mutagen's landscape types and a vanilla Tamriel cell's terrain data.
                foreach (var t in new[] { typeof(Landscape), typeof(LandscapeVertexHeightMap), typeof(BaseLayer), typeof(AlphaLayer), typeof(Worldspace), typeof(Cell), typeof(WorldspaceBlock), typeof(LandscapeTexture) })
                    Console.WriteLine($"{t.Name}: " + string.Join(", ", t.GetProperties().Where(p => p.DeclaringType == t || p.DeclaringType?.Namespace?.Contains("Skyrim") == true).Select(p => $"{p.Name}:{p.PropertyType.Name}")));
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var tamriel = skyrim.Worldspaces.First(w => w.EditorID == "Tamriel");
                Console.WriteLine($"Tamriel: water={tamriel.Water.FormKey} climate={tamriel.Climate.FormKey} flags={tamriel.Flags}");
                int gx = int.Parse(opts.GetValueOrDefault("x", "5")), gy = int.Parse(opts.GetValueOrDefault("y", "-5"));
                var cell = tamriel.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).First(c => c.Grid?.Point.X == gx && c.Grid?.Point.Y == gy);
                var land = cell.Landscape!;
                Console.WriteLine($"cell {cell.FormKey} flags={cell.Flags} landFlags={land.Flags} offset={land.VertexHeightMap?.Offset}");
                var hm = land.VertexHeightMap!.HeightMap;
                Console.WriteLine("VHGT row0: " + string.Join(" ", Enumerable.Range(0, 12).Select(i => (sbyte)hm[i, 0])));
                Console.WriteLine("VNML row0: " + string.Join(" ", Enumerable.Range(0, 6).Select(i => land.VertexNormals![i, 0].ToString())));
                foreach (var l in land.Layers ?? [])
                    Console.WriteLine($"layer {l.GetType().Name} tex={l.Header?.Texture.FormKey} quad={l.Header?.Quadrant} n={(l as IAlphaLayerGetter)?.AlphaLayerData?.Count}");
                foreach (var ltex in skyrim.LandscapeTextures.Take(400))
                    Console.Write($"{ltex.EditorID}[{ltex.FormKey.ID:X}]{(ltex.Grasses.Count > 0 ? "*" : "")} ");
                Console.WriteLine();
                return 0;
            }
            case "overrides":
            {
                // What a plugin (--plugin path) changes of its masters' records, by record type, with examples.
                using var p = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var own = p.ModKey;
                foreach (var g in p.EnumerateMajorRecords().Where(r => r.FormKey.ModKey != own).GroupBy(r => r.GetType().Name.Replace("BinaryOverlay", "")))
                    Console.WriteLine($"{g.Key}: {g.Count()} ({string.Join(", ", g.Take(8).Select(r => $"{r.EditorID ?? "?"} {r.FormKey}"))})");
                return 0;
            }
            case "nif-fields":
            {
                // Dev aid: every block of a NIF (--file) with its readable fields (nifly wrapper properties).
                using var nif = new nifly.NifFile();
                nif.Load(opts["file"]);
                var hdr = nif.GetHeader();
                for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                {
                    var b = hdr.GetBlockById(i);
                    var t = b.GetType();
                    var fields = new List<string>();
                    for (var bt = t; bt is not null && bt != typeof(object); bt = bt.BaseType)
                        foreach (var p in bt.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly))
                        {
                            object? v;
                            try { v = p.GetValue(b); } catch { continue; }
                            string s = v switch
                            {
                                null => "null",
                                float or double or int or uint or short or ushort or byte or bool or Enum => v.ToString()!,
                                nifly.NiStringRef sr => $"\"{sr.get()}\"",
                                nifly.Vector3 v3 => $"({v3.x:G4},{v3.y:G4},{v3.z:G4})",
                                nifly.Color4 c4 => $"({c4.r:G3},{c4.g:G3},{c4.b:G3},{c4.a:G3})",
                                nifly.Color3 c3 => $"({c3.r:G3},{c3.g:G3},{c3.b:G3})",
                                nifly.NiString ns => $"\"{ns.get()}\"",
                                nifly.NiRef r => $"->{r.index}",
                                _ => "",
                            };
                            if (s.Length > 0) fields.Add($"{p.Name}={s}");
                        }
                    if (b is nifly.BSShaderProperty sp) fields.Add($"flags1=0x{sp.shaderFlags1:X} flags2=0x{sp.shaderFlags2:X}");
                    if (b is nifly.NiShape shp) { var bs = shp.GetBounds(); fields.Add($"bounds=({bs.center.x:G5},{bs.center.y:G5},{bs.center.z:G5}) r={bs.radius:G5}"); }
                    if (b is nifly.NiAVObject av) { var tr = av.transform.translation; fields.Add($"at=({tr.x:G5},{tr.y:G5},{tr.z:G5}) scale={av.transform.scale:G4}"); }
                    Console.WriteLine($"[{i}] {hdr.GetBlockTypeStringById(i)}: {string.Join(" ", fields)}");
                }
                return 0;
            }
            case "nifly-types":
            {
                // Dev aid: nifly wrapper classes matching --match, with their settable properties (--props).
                var rx = new System.Text.RegularExpressions.Regex(opts["match"]);
                var assembly = opts.ContainsKey("oblivion") ? typeof(Mutagen.Bethesda.Oblivion.OblivionMod).Assembly
                    : opts.ContainsKey("mutagen") ? typeof(SkyrimMod).Assembly : typeof(nifly.NifFile).Assembly;
                foreach (var t in assembly.GetTypes().Where(t => rx.IsMatch(t.Name)).OrderBy(t => t.Name))
                {
                    Console.WriteLine($"{t.Name} : {t.BaseType?.Name}");
                    if (t.IsEnum) Console.WriteLine("   " + string.Join(", ", Enum.GetNames(t)));
                    if (opts.ContainsKey("props"))
                        Console.WriteLine("   " + string.Join(", ", t.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly).Select(p => $"{p.Name}:{p.PropertyType.Name}")));
                    if (opts.ContainsKey("methods"))
                        Console.WriteLine("   methods: " + string.Join(", ", t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.DeclaredOnly)
                            .Where(m => !m.IsSpecialName).Select(m => $"{m.Name}({string.Join(",", m.GetParameters().Select(p => p.ParameterType.Name))})").Distinct()));
                }
                return 0;
            }
            case "df-spell-test":
            {
                // Builds Daggerfall's spells alone into a scratch plugin and lists each spell with the Skyrim effects
                // its effects look like.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var mod = new SkyrimMod(ModKey.FromFileName("DaggerfallSpellTest.esp"), SkyrimRelease.SkyrimSE);
                var spells = new SpellBuilder(mod, skyrim, DoorPlacer.BaseDoors);
                var anchor = new PlacedObject(mod) { EditorID = "TestAnchor" };
                spells.Build(DfSpells.Load(arena2), anchor);
                var effects = mod.MagicEffects.ToDictionary(m => m.FormKey, m => m.EditorID!);
                foreach (var s in mod.Spells.Where(s => !s.EditorID!.StartsWith("DFTrap")))
                    Console.WriteLine($"{s.Name,-26} {string.Join(" + ", s.Effects.Select(e => effects.GetValueOrDefault(e.BaseEffect.FormKey) is { } id ? $"{id[8..]} -> {spells.LookOf.GetValueOrDefault(id, "?")}" : "?"))}");
                Console.WriteLine($"{mod.Spells.Count} spells, {mod.MagicEffects.Count} effects, {spells.LookOf.Values.Distinct().Count()} Skyrim looks; skipped: {string.Join(", ", spells.Skipped)}");
                return 0;
            }
            case "mgef-dump":
            {
                // Dev aid: a vanilla magic effect's delivery and visuals (--edid A,B).
                using var esm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var byId = esm.EnumerateMajorRecords().Where(r => r.EditorID is not null).GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.First().EditorID);
                string N(FormKey k) => k.IsNull ? "-" : byId.GetValueOrDefault(k) ?? k.ToString();
                foreach (var id in opts["edid"].Split(','))
                {
                    var m = esm.MagicEffects.First(e => e.EditorID == id);
                    Console.WriteLine($"{id}: {m.CastType} {m.TargetType} archetype {m.Archetype.GetType().Name}");
                    Console.WriteLine($"  projectile {N(m.Projectile.FormKey)} explosion {N(m.Explosion.FormKey)} castingArt {N(m.CastingArt.FormKey)} hitArt {N(m.HitEffectArt.FormKey)} enchantArt {N(m.EnchantArt.FormKey)} impact {N(m.ImpactData.FormKey)}");
                    Console.WriteLine($"  hitShader {N(m.HitShader.FormKey)} enchantShader {N(m.EnchantShader.FormKey)} castingLight {N(m.CastingLight.FormKey)} dualCast {N(m.DualCastArt.FormKey)} imageSpace {N(m.ImageSpaceModifier.FormKey)}");
                    Console.WriteLine($"  sounds: {string.Join(", ", m.Sounds?.Select(s => $"{s.Type} {N(s.Sound.FormKey)}") ?? [])}");
                }
                return 0;
            }
            case "magic-test":
            {
                // Converts one game's magic only (--game mw|ob): every spell, enchanted item and scroll, into a plugin
                // kept in memory; reports what was made and what didn't carry over, and checks each spell's effects are
                // delivered the way the spell is.
                bool ob = opts.GetValueOrDefault("game", "mw").Equals("ob", StringComparison.OrdinalIgnoreCase);
                SkyrimMod made;
                IEnumerable<string> skipped;
                int spellCount, enchCount;
                if (ob)
                {
                    string dataDir = GameInstalls.DataDir(Game.Oblivion) ?? throw new DirectoryNotFoundException("Oblivion isn't installed through Steam or GOG");
                    using var builder = new Oblivion.ObPluginBuilder(new Oblivion.ObData(dataDir, Mods.ModCatalog.Selected(Game.Oblivion, dataDir, [])), Path.Combine(Path.GetTempPath(), "tm-magic-test")) { SkyrimData = skyrimData };
                    builder.ConvertAllMagic();
                    made = builder.Mod; skipped = builder.SkippedEffects.ToList(); spellCount = builder.SpellCount; enchCount = builder.EnchantmentCount;
                }
                else
                {
                    string dataFiles = GameInstalls.DataDir(Game.Morrowind) ?? throw new DirectoryNotFoundException("Morrowind isn't installed through Steam or GOG");
                    var mwPlugins = Mods.ModCatalog.Selected(Game.Morrowind, dataFiles, []);
                    var world = Morrowind.MwWorld.Load(dataFiles, mwPlugins);
                    var builder = new Morrowind.MwPluginBuilder(world, Morrowind.Tes3Data.Open(dataFiles, mwPlugins), Path.Combine(Path.GetTempPath(), "tm-magic-test")) { SkyrimData = skyrimData };
                    builder.ConvertAllMagic();
                    made = builder.Mod; skipped = builder.SkippedEffects.ToList(); spellCount = builder.SpellCount; enchCount = builder.EnchantmentCount;
                }
                var mgefs = made.MagicEffects.ToDictionary(m => m.FormKey);
                int mismatched = made.Spells.Count(s => s.Effects.Any(e => mgefs.TryGetValue(e.BaseEffect.FormKey, out var m) && (m.TargetType != s.TargetType || m.CastType != s.CastType)));
                int enchMismatched = made.ObjectEffects.Count(o => o.Effects.Any(e => mgefs.TryGetValue(e.BaseEffect.FormKey, out var m) && (m.TargetType != o.TargetType || m.CastType != o.CastType)));
                Console.WriteLine($"{(ob ? "Oblivion" : "Morrowind")}: {spellCount} spells/abilities/powers/diseases, {enchCount} enchantments, {made.Scrolls.Count} scrolls, {made.MagicEffects.Count} magic effects");
                Console.WriteLine($"  by type: {string.Join(", ", made.Spells.GroupBy(s => s.Type).Select(g => $"{g.Key} {g.Count()}"))}");
                Console.WriteLine($"  enchanted weapons {made.Weapons.Count(w => !w.ObjectEffect.IsNull)}, apparel {made.Armors.Count(a => !a.ObjectEffect.IsNull)}");
                Console.WriteLine($"  delivery mismatches: {mismatched} spells, {enchMismatched} enchantments (effects delivered differently from their spell)");
                Console.WriteLine($"  not carried over: {string.Join(", ", skipped)}");
                // Summons and bound items: their look (the game's own art and sounds where its builder sets them).
                var artNames = made.ArtObjects.ToDictionary(a => a.FormKey, a => $"{a.EditorID} ({a.Model?.File})");
                var soundNames = made.SoundDescriptors.ToDictionary(s => s.FormKey, s => $"{s.EditorID} ({string.Join(",", s.SoundFiles.Select(f => f.DataRelativePath.Path))})");
                foreach (var m in made.MagicEffects.Where(m => m.EditorID!.Contains("Summon")).Take(int.Parse(opts.GetValueOrDefault("looks", "3"))).Concat(made.MagicEffects.Where(m => m.EditorID!.Contains("Bound")).Take(int.Parse(opts.GetValueOrDefault("looks", "3")))))
                    Console.WriteLine($"  {m.EditorID}: cast {artNames.GetValueOrDefault(m.CastingArt.FormKey, m.CastingArt.IsNull ? "-" : "skyrim")} hit {artNames.GetValueOrDefault(m.HitEffectArt.FormKey, m.HitEffectArt.IsNull ? "-" : "skyrim")} sounds {string.Join("; ", m.Sounds?.Select(s => $"{s.Type} {soundNames.GetValueOrDefault(s.Sound.FormKey, "skyrim")}") ?? [])}");
                Console.WriteLine($"  bound armour pieces: {string.Join(", ", made.Armors.Where(a => a.EditorID?.Contains("Bound") == true).Select(a => $"{a.EditorID} \"{a.Name}\""))}");
                foreach (var s in made.Spells.Where(s => s.Type == SpellType.Spell).Take(int.Parse(opts.GetValueOrDefault("show", "8"))))
                    Console.WriteLine($"  {s.EditorID} \"{s.Name}\" cost {s.BaseCost}: {string.Join("; ", s.Effects.Select(e => $"{mgefs.GetValueOrDefault(e.BaseEffect.FormKey)?.EditorID} {e.Data?.Magnitude:F0} for {e.Data?.Duration}s area {e.Data?.Area}"))}");
                return 0;
            }
            case "quest-dump":
            {
                // Dev aid: a quest's stages (index, flags, its fragment), objectives and aliases (--quest EDID, --plugin as edid-list).
                using var esm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, opts.GetValueOrDefault("plugin", "Skyrim.esm")), SkyrimRelease.SkyrimSE);
                foreach (var q in esm.Quests.Where(q => q.EditorID == opts["quest"]))
                {
                    Console.WriteLine($"{q.EditorID} {q.FormKey} flags {q.Flags}");
                    var frags = (q.VirtualMachineAdapter as IQuestAdapterGetter)?.Fragments.ToDictionary(f => (f.Stage, f.StageIndex), f => f.FragmentName) ?? [];
                    foreach (var st in q.Stages)
                    {
                        string fr = string.Join(",", frags.Where(f => f.Key.Stage == st.Index).Select(f => f.Value));
                        Console.WriteLine($"  stage {st.Index} {st.Flags} {fr}");
                    }
                    foreach (var o in q.Objectives) Console.WriteLine($"  objective {o.Index}");
                    foreach (var al in q.Aliases) Console.WriteLine($"  alias {al.ID} {al.Name} forced {al.ForcedReference.FormKeyNullable} unique {al.UniqueActor.FormKeyNullable}");
                }
                return 0;
            }
            case "edid-list":
            {
                // EditorIDs of a Skyrim.esm record group (--group LandscapeTextures) matching --match.
                using var esm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, opts.GetValueOrDefault("plugin", "Skyrim.esm")), SkyrimRelease.SkyrimSE);
                var group = (System.Collections.IEnumerable)esm.GetType().GetProperty(opts["group"])!.GetValue(esm)!;
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("match", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                Console.WriteLine(string.Join(" ", group.Cast<Mutagen.Bethesda.Plugins.Records.IMajorRecordGetter>().Select(r => $"{r.EditorID}:{r.FormKey.ID:X6}").Where(s => rx.IsMatch(s))));
                return 0;
            }
            case "bsa-list":
            {
                // Paths matching --match (regex) in the Skyrim archives matching --bsa (default the texture archives).
                var rx = new System.Text.RegularExpressions.Regex(opts["match"], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var b in Directory.GetFiles(skyrimData, opts.GetValueOrDefault("bsa", "Skyrim - Textures*.bsa")))
                    foreach (var f in Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, b).Files.Where(f => rx.IsMatch(f.Path)))
                        Console.WriteLine($"{Path.GetFileName(b)}\t{f.Path}");
                return 0;
            }
            case "extract":
            {
                // Extract files matching --match (regex on the archive path) from --archive into --out.
                var rx = new System.Text.RegularExpressions.Regex(opts["match"], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                string outDir = opts.GetValueOrDefault("out", "out/extract");
                var reader = Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, Path.Combine(skyrimData, opts["archive"]));
                int n = 0, max = int.Parse(opts.GetValueOrDefault("take", "20"));
                foreach (var file in reader.Files.Where(f => rx.IsMatch(f.Path)).Take(max))
                {
                    var dest = Path.Combine(outDir, file.Path);
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    File.WriteAllBytes(dest, file.GetBytes());
                    Console.WriteLine($"{file.Path} ({file.Size} bytes)");
                    n++;
                }
                Console.WriteLine($"extracted {n}");
                return 0;
            }
            case "collision-survey":
            {
                // Dev aid: how vanilla static meshes build collision: for each sampled static's NIF (from the BSAs), the
                // chain of shape block types under its rigid bodies, with layer and motion system, tallied.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("model", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var paths = skyrim.Statics.Select(s => s.Model?.File.DataRelativePath.Path).OfType<string>().Where(p => rx.IsMatch(p)).Distinct().Take(int.Parse(opts.GetValueOrDefault("n", "400"))).ToList();
                var archives = Directory.GetFiles(skyrimData, opts.GetValueOrDefault("bsa", "Skyrim - Meshes*.bsa")).Select(p => Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, p)).ToArray();
                var files = archives.SelectMany(a => a.Files).GroupBy(f => f.Path.ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
                var tally = new Dictionary<string, int>();
                var examples = new Dictionary<string, string>();
                string tmp = Path.Combine(Path.GetTempPath(), "tm_survey.nif");
                foreach (var path in paths)
                {
                    if (!files.TryGetValue(path.Replace('/', '\\').ToLowerInvariant(), out var f) && !files.TryGetValue(("meshes\\" + path).Replace('/', '\\').ToLowerInvariant(), out f)) continue;
                    File.WriteAllBytes(tmp, f.GetBytes());
                    using var nif = new nifly.NifFile();
                    if (nif.Load(tmp) != 0) continue;
                    var hdr = nif.GetHeader();
                    using var cache = new nifly.niflycpp.BlockCache(hdr);
                    for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                    {
                        string type = hdr.GetBlockById(i).GetBlockName();
                        if (type is not ("bhkRigidBody" or "bhkRigidBodyT")) continue;
                        var body = cache.EditableBlockById<nifly.bhkRigidBody>(i);
                        var chain = new List<string>();
                        uint shape = body.shapeRef.index;
                        for (int depth = 0; depth < 4 && shape != uint.MaxValue && shape < hdr.GetNumBlocks(); depth++)
                        {
                            string st = hdr.GetBlockById(shape).GetBlockName();
                            chain.Add(st);
                            if (st == "bhkMoppBvTreeShape") shape = cache.EditableBlockById<nifly.bhkMoppBvTreeShape>(shape).shapeRef.index;
                            else if (st == "bhkListShape") { var l = cache.EditableBlockById<nifly.bhkListShape>(shape); var refs = new nifly.vectoruint32(); l.subShapeRefs.GetIndices(refs); chain.Add($"[{refs.Count} x {(refs.Count > 0 ? hdr.GetBlockById(refs[0]).GetBlockName() : "-")}]"); break; }
                            else break;
                        }
                        string key = $"{type} layer {body.collisionFilter.layer} motion {body.motionSystem} quality {body.qualityType}: {string.Join(" > ", chain)}";
                        if (opts.ContainsKey("targets"))
                        {
                            // Which node each collision object hangs on: the root or a child, and how many per file.
                            int objects = 0, onChild = 0;
                            for (uint j = 0; j < hdr.GetNumBlocks(); j++)
                                if (hdr.GetBlockById(j).GetBlockName() == "bhkCollisionObject")
                                {
                                    objects++;
                                    if (cache.EditableBlockById<nifly.bhkCollisionObject>(j).targetRef.index != 0) onChild++;
                                }
                            key = $"layer {body.collisionFilter.layer}: {objects} collision objects, {onChild} on child nodes";
                            tally[key] = tally.GetValueOrDefault(key) + 1;
                            examples.TryAdd(key, path);
                            break;
                        }
                        tally[key] = tally.GetValueOrDefault(key) + 1;
                        examples.TryAdd(key, path);
                    }
                }
                foreach (var (k, v) in tally.OrderByDescending(t => t.Value)) Console.WriteLine($"{v,5}  {k}   e.g. {examples[k]}");
                return 0;
            }
            case "nav-points":
            {
                // Dev aid: for each dungeon block square (side --side Skyrim units), the centre of its largest flat
                // navmesh triangle: a floor point to test standing on.
                float side = float.Parse(opts.GetValueOrDefault("side", "4096"));
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                var nav = plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).SelectMany(c => c.NavigationMeshes).First().Data!;
                var best = new Dictionary<(int, int), (float Area, Noggog.P3Float C)>();
                foreach (var t in nav.Triangles)
                {
                    var (a, b, c) = (nav.Vertices[t.Vertices.X], nav.Vertices[t.Vertices.Y], nav.Vertices[t.Vertices.Z]);
                    if (MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) - MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) > 2) continue;
                    float area = MathF.Abs((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X)) / 2;
                    var ctr = new Noggog.P3Float((a.X + b.X + c.X) / 3, (a.Y + b.Y + c.Y) / 3, (a.Z + b.Z + c.Z) / 3);
                    var key = ((int)MathF.Floor(ctr.X / side), (int)MathF.Floor(ctr.Y / side));
                    if (!best.TryGetValue(key, out var cur) || area > cur.Area) best[key] = (area, ctr);
                }
                foreach (var (k, v) in best.OrderBy(k => k.Key)) Console.WriteLine($"block {k}: {v.C.X:F0} {v.C.Y:F0} {v.C.Z:F0} (triangle area {v.Area:F0})");
                return 0;
            }
            case "find-marker-in":
            {
                // Dev aid: in another plugin (--plugin), references whose EditorID, base EditorID or map-marker name
                // matches --name, with FormIDs (as Game.GetFormFromFile wants them) and cells.
                using var other = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, opts["plugin"]), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "Seyda"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                Console.WriteLine($"{other.ModKey}: masters {string.Join(", ", other.ModHeader.MasterReferences.Select(m => m.Master))}");
                foreach (var ws in other.Worldspaces)
                    foreach (var cell in ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).Append(ws.TopCell).OfType<ICellGetter>())
                        foreach (var r in cell.Persistent.Concat(cell.Temporary).OfType<IPlacedObjectGetter>())
                        {
                            string marker = r.MapMarker?.Name?.String ?? "";
                            if (rx.IsMatch(r.EditorID ?? "") || rx.IsMatch(marker))
                                Console.WriteLine($"  {r.FormKey} edid {r.EditorID} marker \"{marker}\" in {ws.EditorID} cell {cell.FormKey} at {r.Placement?.Position} rot {r.Placement?.Rotation} base {r.Base.FormKey} {(cell.Persistent.Contains(r) ? "persistent" : "temporary")}");
                        }
                // Interior cells whose EditorID or name matches: every door and marker in them.
                foreach (var cell in other.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                {
                    if (!rx.IsMatch(cell.EditorID ?? "") && !rx.IsMatch(cell.Name?.String ?? "")) continue;
                    Console.WriteLine($"  interior {cell.FormKey} {cell.EditorID} \"{cell.Name?.String}\"");
                    foreach (var r in cell.Persistent.Concat(cell.Temporary).OfType<IPlacedObjectGetter>())
                        if (r.TeleportDestination is not null || r.Base.FormKey.ModKey.FileName == "Skyrim.esm" && r.Base.FormKey.ID is 0x32 or 0x34 or 0x3B or 0x10)
                            Console.WriteLine($"    {r.FormKey} edid {r.EditorID} base {r.Base.FormKey} at {r.Placement?.Position} rot {r.Placement?.Rotation} {(r.TeleportDestination is { } t ? $"door -> {t.Door.FormKey}" : "")} {(cell.Persistent.Contains(r) ? "persistent" : "temporary")}");
                }
                return 0;
            }
            case "export-df-textures":
            {
                // Dev aid: export Daggerfall pictures (--list 163.0,63.2) the way the mesh builder does, into --out.
                var export = new Skyrim.Meshes.DfTextureExport(arena2, opts.GetValueOrDefault("out", "out/mod"));
                foreach (var item in opts["list"].Split(','))
                {
                    var parts = item.Split('.');
                    var (path, w, h) = export.Ensure(int.Parse(parts[0]), int.Parse(parts[1]));
                    Console.WriteLine($"{item}: {path} ({w}x{h} texels)");
                }
                return 0;
            }
            case "dungeon-textures":
            {
                // Dev aid: Daggerfall textures (archive.record) used by a dungeon's models, by polygon count; with --sheet
                // a labelled contact sheet of every record of those archives.
                var models = DfModel.LoadAll(arena2);
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                var counts = DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []).Polygons(models)
                    .GroupBy(p => (p.TextureArchive, p.TextureRecord)).ToDictionary(g => g.Key, g => g.Count());
                foreach (var arch in counts.GroupBy(c => c.Key.TextureArchive).OrderBy(g => g.Key))
                    Console.WriteLine($"{arch.Key} {DfTexture.Load(arena2, arch.Key)?.Name}: " + string.Join(" ", arch.OrderBy(c => c.Key.TextureRecord).Select(c => $"{c.Key.TextureRecord}x{c.Value}")));
                if (opts.TryGetValue("where", out var whereArchive))
                    foreach (var poly in DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []).Polygons(models).Where(p => p.TextureArchive == int.Parse(whereArchive)).Take(6))
                    {
                        var c = poly.Points.Aggregate(System.Numerics.Vector3.Zero, (a, b) => a + b) / poly.Points.Length * Skyrim.Meshes.BlockMeshBuilder.Scale;
                        Console.WriteLine($"  {poly.TextureArchive}.{poly.TextureRecord} at Skyrim ({c.X:F0},{c.Z:F0},{c.Y:F0}) facing ({poly.Normal.X:F2},{poly.Normal.Z:F2},{poly.Normal.Y:F2})");
                    }
                if (opts.ContainsKey("sheet"))
                {
                    Directory.CreateDirectory("out/textures");
                    foreach (var a in counts.Keys.Select(k => k.TextureArchive).Distinct()) DfTexture.Sheet(arena2, [a], $"out/textures/tex-{a}.png", 96, 10);
                }
                return 0;
            }
            case "nif-shapes":
            {
                // Dev aid: each geometry shape's flags, parent chain transforms and vertex bounds (local and with transforms).
                using var nif = new nifly.NifFile();
                nif.Load(opts["file"]);
                var hdr = nif.GetHeader();
                foreach (var shape in nif.GetShapes())
                {
                    var verts = nif.GetVertsForShape(shape);
                    var lo = new System.Numerics.Vector3(float.MaxValue); var hi = new System.Numerics.Vector3(float.MinValue);
                    foreach (var v in verts) { var p = new System.Numerics.Vector3(v.x, v.y, v.z); lo = System.Numerics.Vector3.Min(lo, p); hi = System.Numerics.Vector3.Max(hi, p); }
                    var t = shape.transform;
                    Console.WriteLine($"{nif.GetNodeName(hdr.GetBlockID(shape))} [{shape.GetBlockName()}] flags 0x{shape.flags:X} verts {verts.Count} local {lo} .. {hi}  translation ({t.translation.x:F1},{t.translation.y:F1},{t.translation.z:F1}) scale {t.scale}");
                }
                // Every node's own transform (an animated door's leaf sits under one).
                using (var nodeCache = new nifly.niflycpp.BlockCache(hdr))
                    for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                        if (hdr.GetBlockTypeStringById(i) is "NiNode" or "BSFadeNode")
                        {
                            var nd = nodeCache.EditableBlockById<nifly.NiNode>(i);
                            var nt = nd.transform;
                            Console.WriteLine($"node [{i}] translation ({nt.translation.x:F1},{nt.translation.y:F1},{nt.translation.z:F1}) scale {nt.scale}");
                        }
                return 0;
            }
            case "nif-body":
            {
                // Dev aid: each rigid body's translation, rotation quaternion, centre and inertia/mass fields.
                using var nif = new nifly.NifFile();
                nif.Load(opts["file"]);
                var hdr = nif.GetHeader();
                using var cache = new nifly.niflycpp.BlockCache(hdr);
                for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                {
                    string type = hdr.GetBlockById(i).GetBlockName();
                    if (type is not ("bhkRigidBody" or "bhkRigidBodyT")) continue;
                    var b = cache.EditableBlockById<nifly.bhkRigidBody>(i);
                    Console.WriteLine($"[{i}] {type} translation ({b.translation.x},{b.translation.y},{b.translation.z},{b.translation.w}) rotation ({b.rotation.x},{b.rotation.y},{b.rotation.z},{b.rotation.w}) center ({b.center.x},{b.center.y},{b.center.z},{b.center.w}) mass {b.mass}");
                    if (opts.ContainsKey("first")) break;
                }
                return 0;
            }
            case "nif-slab":
            {
                // Dev aid: a bhkConvexVerticesShape's vertices and face planes (Havok units), and whether every vertex
                // lies on the inner side of every plane (dot(n, v) + w <= 0), as Havok requires.
                using var nif = new nifly.NifFile();
                nif.Load(opts["file"]);
                var hdr = nif.GetHeader();
                using var cache = new nifly.niflycpp.BlockCache(hdr);
                var ids = opts.TryGetValue("id", out var one) ? [uint.Parse(one)] : Enumerable.Range(0, (int)hdr.GetNumBlocks()).Select(i => (uint)i).Where(i => hdr.GetBlockById(i).GetBlockName() == "bhkConvexVerticesShape").ToArray();
                int bad = 0, nans = 0;
                foreach (var i in ids)
                {
                    var s = cache.EditableBlockById<nifly.bhkConvexVerticesShape>(i);
                    var vs = s.verts.items().ToList(); var ns = s.normals.items().ToList();
                    float worst = vs.SelectMany(v => ns.Select(n => n.x * v.x + n.y * v.y + n.z * v.z + n.w)).Max();
                    bool nan = vs.Concat(ns).Any(v => !float.IsFinite(v.x) || !float.IsFinite(v.y) || !float.IsFinite(v.z) || !float.IsFinite(v.w));
                    if (nan) nans++;
                    if (worst > float.Parse(opts.GetValueOrDefault("tol", "0.001")) || float.IsNaN(worst)) { bad++; if (opts.ContainsKey("list") && bad <= 3) Console.WriteLine($"bad slab {i}: worst {worst:F4}, {vs.Count} verts, {ns.Count} planes"); }
                    if (opts.ContainsKey("id"))
                    {
                        Console.WriteLine($"slab {i}: radius {s.radius} material {s.GetMaterial()} {vs.Count} verts, {ns.Count} planes, worst {worst:F4}");
                        foreach (var v in vs) Console.WriteLine($"  v ({v.x:F4},{v.y:F4},{v.z:F4},{v.w:F2})");
                        foreach (var n in ns) Console.WriteLine($"  n ({n.x:F3},{n.y:F3},{n.z:F3}) w {n.w:F4}");
                    }
                }
                Console.WriteLine($"{ids.Length} convex shapes, {bad} with vertices outside their own planes, {nans} with NaN/infinite values");
                return 0;
            }
            case "nif-probe-point":
            {
                // Which visible triangles and collision slabs lie under (x, y) in a NIF's own space?
                float px = float.Parse(opts["x"], System.Globalization.CultureInfo.InvariantCulture), py = float.Parse(opts["y"], System.Globalization.CultureInfo.InvariantCulture);
                using var nif = new nifly.NifFile();
                nif.Load(opts["file"]);
                var hdr = nif.GetHeader();
                foreach (var shape in nif.GetShapes())
                {
                    var verts = nif.GetVertsForShape(shape);
                    var tris = new nifly.vectorTriangle();
                    shape.GetTriangles(tris);
                    foreach (var t in tris)
                    {
                        var (a, b, c) = (verts[t.p1], verts[t.p2], verts[t.p3]);
                        float d = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
                        if (Math.Abs(d) < 1e-6) continue;
                        float w0 = ((b.y - c.y) * (px - c.x) + (c.x - b.x) * (py - c.y)) / d, w1 = ((c.y - a.y) * (px - c.x) + (a.x - c.x) * (py - c.y)) / d;
                        if (w0 < 0 || w1 < 0 || w0 + w1 > 1) continue;
                        Console.WriteLine($"  visible {nif.GetNodeName(hdr.GetBlockID(shape))}: z={w0 * a.z + w1 * b.z + (1 - w0 - w1) * c.z:F1}");
                    }
                }
                using var cache = new nifly.niflycpp.BlockCache(hdr);
                for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                {
                    if (hdr.GetBlockById(i).GetBlockName() != "bhkConvexVerticesShape") continue;
                    var s = cache.EditableBlockById<nifly.bhkConvexVerticesShape>(i);
                    var vs = s.verts.items().Select(v => (x: v.x * 69.99125f, y: v.y * 69.99125f, z: v.z * 69.99125f)).ToList();
                    if (px < vs.Min(v => v.x) || px > vs.Max(v => v.x) || py < vs.Min(v => v.y) || py > vs.Max(v => v.y)) continue;
                    // Inside the slab's outline (its side planes), not just its bounding box?
                    var ns = s.normals.items().ToList();
                    bool inside = ns.Where(n => MathF.Abs(n.z) < 0.5f).All(n => n.x * px / 69.99125f + n.y * py / 69.99125f + n.w <= 1e-3f
                        || MathF.Abs(n.z) >= 0.5f);
                    Console.WriteLine($"  collision slab [{i}] z {vs.Min(v => v.z):F1}..{vs.Max(v => v.z):F1}{(inside ? "" : "  (bbox only, point outside the outline)")}");
                }
                return 0;
            }
            case "extract-mesh":
            {
                // Dev aid: copies Skyrim meshes (--path, ';'-separated, data-relative) out of the archives (--bsa pattern,
                // default the mesh archives) to out/extract.
                var archives = Directory.GetFiles(skyrimData, opts.GetValueOrDefault("bsa", "Skyrim - Meshes*.bsa")).Select(b => Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, b)).ToList();
                Directory.CreateDirectory("out/extract");
                foreach (var path in opts["path"].Split(';'))
                {
                    var file = archives.SelectMany(a => a.Files).FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (file is null)
                    {
                        // --path with a * lists matching archive paths instead.
                        if (path.Contains('*')) foreach (var f in archives.SelectMany(a => a.Files).Where(f => System.IO.Enumeration.FileSystemName.MatchesSimpleExpression(path.Replace('\\', '/'), f.Path.Replace('\\', '/'))).Take(40)) Console.WriteLine($"  {f.Path} ({f.Size} bytes)");
                        else Console.WriteLine($"not found: {path}");
                        continue;
                    }
                    string outPath = Path.Combine("out/extract", Path.GetFileName(path));
                    File.WriteAllBytes(outPath, file.GetBytes());
                    Console.WriteLine($"extracted {outPath}");
                }
                return 0;
            }
            case "vanilla-doors":
            {
                // Dev aid: Skyrim.esm door records matching --name (regex on EditorID or model) and, with --extract N,
                // writes the first N models from the archives to out/extract for inspection (nif-dump / nif-anim).
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var doors = skyrim.Doors.Where(d => rx.IsMatch(d.EditorID ?? "") || rx.IsMatch(d.Model?.File.DataRelativePath.Path ?? "")).ToList();
                foreach (var d in doors.Take(40)) Console.WriteLine($"{d.EditorID,-36} {d.Flags,-20} {d.Model?.File.DataRelativePath.Path}");
                if (opts.TryGetValue("extract", out var n))
                {
                    var archives = Directory.GetFiles(skyrimData, "Skyrim - Meshes*.bsa").Select(b => Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, b)).ToList();
                    Directory.CreateDirectory("out/extract");
                    foreach (var d in doors.Take(int.Parse(n)))
                    {
                        string path = d.Model!.File.DataRelativePath.Path.ToLowerInvariant();
                        var file = archives.SelectMany(a => a.Files).FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                        if (file is null) { Console.WriteLine($"  not in archives: {path}"); continue; }
                        string outPath = Path.Combine("out/extract", Path.GetFileName(path));
                        File.WriteAllBytes(outPath, file.GetBytes());
                        Console.WriteLine($"  extracted {outPath}");
                    }
                }
                return 0;
            }
            case "weathers":
            {
                // Dev aid: weathers in Skyrim.esm and Dragonborn.esm whose EditorID matches --name, with their class flags.
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var master in new[] { "Skyrim.esm", "Dragonborn.esm" })
                {
                    using var m = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, master), SkyrimRelease.SkyrimSE);
                    foreach (var w in m.Weathers.Where(w => rx.IsMatch(w.EditorID ?? "")))
                        Console.WriteLine($"{master,-15} {w.FormKey} {w.EditorID,-36} {w.Flags}");
                }
                return 0;
            }
            case "npc-faces":
            {
                // Dev aid: vanilla NPCs with their own FaceGen head (meshes\actors\character\facegendata\facegeom                // skyrim.esm\<id>.nif in the archives), counted per race and sex, with --race examples.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var faces = Directory.GetFiles(skyrimData, "Skyrim - Meshes*.bsa").Select(b => Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, b))
                    .SelectMany(a => a.Files).Select(f => f.Path.ToLowerInvariant())
                    .Where(p => p.Contains(@"facegeom\skyrim.esm\")).Select(p => Path.GetFileNameWithoutExtension(p)).ToHashSet();
                var races = skyrim.Races.ToDictionary(r => r.FormKey, r => r.EditorID);
                var withFace = skyrim.Npcs.Where(n => faces.Contains(n.FormKey.ID.ToString("x8"))).ToList();
                foreach (var g in withFace.GroupBy(n => (races.GetValueOrDefault(n.Race.FormKey), n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female))).OrderBy(g => g.Key.Item1))
                    Console.WriteLine($"{g.Key.Item1,-24} {(g.Key.Item2 ? "F" : "M")} {g.Count(),4}  generic(Enc*/Lvl*): {g.Count(n => n.EditorID?.StartsWith("Enc") == true || n.EditorID?.StartsWith("Lvl") == true)}");
                if (opts.TryGetValue("race", out var race))
                    foreach (var n in withFace.Where(n => races.GetValueOrDefault(n.Race.FormKey) == race).Take(30)) Console.WriteLine($"   {n.EditorID} {(n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) ? "F" : "M")} {(n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Unique) ? "unique" : "")}");
                return 0;
            }
            case "creature-races":
            {
                // Dev aid: per non-playable race in Skyrim.esm/Dawnguard/Dragonborn, the Enc* actors using it (candidates
                // for creature templates).
                foreach (var master in new[] { "Skyrim.esm", "Dawnguard.esm", "Dragonborn.esm" })
                {
                    using var m = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, master), SkyrimRelease.SkyrimSE);
                    using var sk = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                    var races = sk.Races.Concat(m.Races).GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.First());
                    foreach (var g in m.Npcs.Where(n => n.FormKey.ModKey == m.ModKey && n.EditorID?.Contains("Enc") == true && races.TryGetValue(n.Race.FormKey, out var r) && !r.Flags.HasFlag(Race.Flag.Playable))
                        .GroupBy(n => races[n.Race.FormKey].EditorID).OrderBy(g => g.Key))
                        Console.WriteLine($"{master,-15} {g.Key,-32} {string.Join(", ", g.Select(n => n.EditorID).Take(4))}");
                }
                return 0;
            }
            case "packages":
            {
                // Dev aid: Skyrim.esm AI packages whose EditorID matches --name.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var p in skyrim.Packages.Where(p => rx.IsMatch(p.EditorID ?? "")).Take(40)) Console.WriteLine($"{p.FormKey} {p.EditorID}");
                return 0;
            }
            case "sound-forms":
            {
                // Dev aid: Skyrim.esm sound descriptors and markers whose EditorID matches --name.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var d in skyrim.SoundDescriptors.Where(d => rx.IsMatch(d.EditorID ?? "")).Take(40)) Console.WriteLine($"SNDR {d.FormKey} {d.EditorID}");
                foreach (var d in skyrim.SoundMarkers.Where(d => rx.IsMatch(d.EditorID ?? "")).Take(40)) Console.WriteLine($"SOUN {d.FormKey} {d.EditorID}");
                return 0;
            }
            case "nif-anim":
            {
                // Dev aid: the animation side of a NIF (--file): every block's type, and for controller managers,
                // sequences (name, cycle, frequency, times, controlled blocks) and transform data keys.
                using var nif = new nifly.NifFile();
                nif.Load(opts["file"]);
                var hdr = nif.GetHeader();
                using var cache = new nifly.niflycpp.BlockCache(hdr);
                for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                {
                    string type = hdr.GetBlockTypeStringById(i);
                    var block = hdr.GetBlockById(i);
                    string detail = "";
                    if (block is nifly.NiObjectNET net) detail += $" name='{net.name.get()}'";
                    if (type == "NiControllerSequence")
                    {
                        var seq = cache.EditableBlockById<nifly.NiControllerSequence>(i);
                        detail += $" cycle={seq.cycleType} freq={seq.frequency} start={seq.startTime} stop={seq.stopTime} accumRoot='{seq.accumRootName.get()}' textKeys={seq.textKeyRef.index}";   // (nifly's bindings don't expose the controlled blocks)
                    }
                    Console.WriteLine($"  [{i}] {type}{detail}");
                }
                return 0;
            }
            case "lod":
            {
                // The converted worlds' distant view (Skyrim/Distant/DistantLod; the app's Distant LOD button): land, objects
                // and trees for every exterior worldspace the converted game plugins in --dir (default out/convert) add,
                // into --out (default the same folder). --only terrain,objects,trees picks parts; --worlds A,B picks
                // worldspaces by editor ID; --plugins Morrowind.esm,... picks games. --install copies the result into Skyrim's Data folder.
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", dir));
                var only = opts.GetValueOrDefault("only", "terrain,objects,trees").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
                var worlds = opts.GetValueOrDefault("worlds", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var games = opts.GetValueOrDefault("plugins", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // --assets DIR: another folder whose loose models and textures the converted plugins use (a resumed
                // convert's earlier output).
                string[] assets = opts.TryGetValue("assets", out var assetDir) ? [Path.GetFullPath(assetDir)] : [];
                int code = Skyrim.Distant.DistantLod.Run(dir, outDir, skyrimData, new(only.Contains("terrain"), only.Contains("objects"), only.Contains("trees")), worlds, Console.WriteLine, games, assets);
                Console.WriteLine($"Distant LOD: {(code == 0 ? "done" : "failed")} in {sw.Elapsed.TotalMinutes:F1} min");
                if (code == 0 && opts.ContainsKey("install"))
                {
                    int copied = 0;
                    foreach (var sub in new[] { "lodsettings", Path.Combine("meshes", "terrain"), Path.Combine("textures", "terrain") })
                        if (Directory.Exists(Path.Combine(outDir, sub)))
                            foreach (var file in Directory.EnumerateFiles(Path.Combine(outDir, sub), "*", SearchOption.AllDirectories))
                            {
                                string target = Path.Combine(skyrimData, Path.GetRelativePath(outDir, file));
                                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                                File.Copy(file, target, overwrite: true);
                                copied++;
                            }
                    Console.WriteLine($"Installed {copied} distant LOD files into {skyrimData}");
                }
                return code;
            }
            case "sprite-import-test":
            {
                // Dev aid: imports the finished sprite models alone into --out (a scratch folder), timed, and prints the
                // statics' editor IDs and form IDs in order (to compare one run with another).
                string outDir = Path.GetFullPath(opts["out"]);
                if (opts.TryGetValue("threads", out var th)) Skyrim.Meshes.SpriteModelImporter.Threads = int.Parse(th);
                var mod = new SkyrimMod(ModKey.FromFileName("SpriteImportTest.esp"), SkyrimRelease.SkyrimSE);
                var problems = new List<string>();
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var made = new Skyrim.Meshes.SpriteModelImporter(mod, outDir, arena2).ImportAll(Path.Combine(Setup.Dependencies.Root, "model3d", "work"), problems);
                Console.WriteLine($"{made.Count} models in {sw.Elapsed.TotalSeconds:F1} s; {problems.Count} problems");
                if (opts.TryGetValue("list", out var list)) File.WriteAllLines(list, mod.Statics.Select(st => $"{st.FormKey.ID:X6} {st.EditorID} {st.Model?.File} {st.ObjectBounds.First} {st.ObjectBounds.Second}"));
                return 0;
            }
            case "df-dungeon-check":
            {
                // Checks the built dungeons in --dir (default out/convert) for floating items, statues facing walls and
                // misplaced people (Skyrim/DungeonCheck); --limit N cells, --cells A,B by editor ID; every problem to --out (CSV).
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                var cellNames = opts.GetValueOrDefault("cells", "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var found = Skyrim.DungeonCheck.Run(dir, skyrimData, Console.WriteLine, int.Parse(opts.GetValueOrDefault("limit", "0")), cellNames);
                foreach (var g in found.GroupBy(f => f.Kind))
                    foreach (var f in g.Take(3)) Console.WriteLine($"    e.g. {f.Kind}: {f.Cell} {f.Ref} {f.Base} at ({f.X:F0},{f.Y:F0},{f.Z:F0}) {f.Detail}");
                if (opts.TryGetValue("out", out var csv))
                    File.WriteAllLines(csv, found.Select(f => string.Join(",", f.Kind, f.Cell, f.Ref, f.Base, f.X.ToString("F0"), f.Y.ToString("F0"), f.Z.ToString("F0"), f.Detail)).Prepend("kind,cell,ref,base,x,y,z,detail"));
                return 0;
            }
            case "game-check":
            {
                // What went wrong in a game session (Skyrim/GameCheck): --prepare turns Papyrus logging on and installs the
                // crash logger; --launch starts the game through SKSE and waits for it to end; then the session's logs are
                // scanned (--since "yyyy-MM-dd HH:mm", default the launch, else the last hour) and a report written to logs\.
                if (opts.ContainsKey("prepare")) Skyrim.GameCheck.Prepare(skyrimData, Console.WriteLine);
                DateTime since = opts.TryGetValue("since", out var s0) ? DateTime.Parse(s0, System.Globalization.CultureInfo.CurrentCulture) : DateTime.Now.AddHours(-1);
                if (opts.ContainsKey("launch") && Skyrim.GameCheck.Launch(skyrimData, Console.WriteLine) is { } run) since = run.Started;
                string workspace = Directory.GetCurrentDirectory();
                Console.Write(Skyrim.GameCheck.Scan(since, workspace, skyrimData));
                return 0;
            }
            case "df-senses-check":
            {
                // Daggerfall's enemy senses (DfSenses, docs/daggerfall-senses.md) in a built plugin (--plugin; --master the
                // plugin it builds on, e.g. Daggerfall.esm under a dungeon part): the control quest, every actor carrying
                // TMDfSenses (its properties, that its own scripts count), what each encounter list picks, and placed
                // actors without the senses; --list prints every actor.
                string plugin = Path.GetFullPath(opts["plugin"]);
                using var m = SkyrimMod.CreateFromBinaryOverlay(plugin, SkyrimRelease.SkyrimSE);
                var others = new List<ISkyrimModDisposableGetter> { m };
                if (opts.TryGetValue("master", out var masterPath)) others.Add(SkyrimMod.CreateFromBinaryOverlay(Path.GetFullPath(masterPath), SkyrimRelease.SkyrimSE));
                foreach (var f in new[] { "Skyrim.esm", "Dawnguard.esm", "Dragonborn.esm" }) others.Add(SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, f), SkyrimRelease.SkyrimSE));
                try
                {
                    var npcs = others.SelectMany(o => o.Npcs).GroupBy(n => n.FormKey).ToDictionary(g => g.Key, g => g.First());
                    var lists = others.SelectMany(o => o.LeveledNpcs).GroupBy(n => n.FormKey).ToDictionary(g => g.Key, g => g.First());
                    var quests = others.SelectMany(o => o.Quests).GroupBy(q => q.FormKey).ToDictionary(g => g.Key, g => g.First());
                    int problems = 0;
                    void Problem(string s) { problems++; Console.WriteLine("  PROBLEM: " + s); }
                    IScriptEntryGetter? Senses(INpcGetter n) => n.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == Skyrim.DfSenses.ActorScript);
                    bool UsesScript(INpcGetter n) => !n.Template.IsNull && n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Script);
                    // Whether an actor ends up with the senses: its own (not Use Script), or through its template.
                    bool Has(FormKey k, int depth = 0)
                    {
                        if (depth > 8) return false;
                        if (npcs.TryGetValue(k, out var n)) return UsesScript(n) ? Has(n.Template.FormKey, depth + 1) : Senses(n) is not null;
                        if (lists.TryGetValue(k, out var l)) return (l.Entries ?? []).All(e => e.Data is { } d && Has(d.Reference.FormKey, depth + 1));
                        return false;
                    }

                    var quest = m.Quests.FirstOrDefault(q => q.EditorID == Skyrim.DfSenses.QuestEditorId);
                    Console.WriteLine($"{Path.GetFileName(plugin)}: control quest {(quest is null ? "none" : $"{quest.FormKey.ID:X6} flags {quest.Flags}")}");
                    if (quest?.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == Skyrim.DfSenses.ControlScript) is { } cs)
                        Console.WriteLine("  " + string.Join(", ", cs.Properties.Select(p => p is IScriptObjectPropertyGetter o ? $"{p.Name}={o.Object.FormKey}" : p.Name)));
                    else if (quest is not null) Problem("the control quest has no TMDfSensesControl");

                    int carrying = 0;
                    foreach (var n in m.Npcs.Where(n => n.FormKey.ModKey == m.ModKey).OrderBy(n => n.EditorID))
                    {
                        var s = Senses(n);
                        string e = n.EditorID ?? "";
                        if (s is null)
                        {
                            if (opts.ContainsKey("list")) Console.WriteLine($"  -        {e}{(UsesScript(n) ? " (scripts from its template)" : "")}");
                            continue;
                        }
                        carrying++;
                        var senses = s.Properties.OfType<IScriptObjectPropertyGetter>().FirstOrDefault(p => p.Name == "Senses")?.Object.FormKey;
                        bool sees = s.Properties.OfType<IScriptBoolPropertyGetter>().FirstOrDefault(p => p.Name == "SeesInvisible")?.Data ?? false;
                        string others2 = string.Join(",", n.VirtualMachineAdapter!.Scripts.Where(x => x.Name != Skyrim.DfSenses.ActorScript).Select(x => x.Name));
                        if (opts.ContainsKey("list")) Console.WriteLine($"  senses   {e}{(sees ? " sees invisible" : "")}{(others2.Length > 0 ? " +" + others2 : "")}");
                        if (!e.StartsWith("DFCreature") && !e.StartsWith("DFLvl")) Problem($"{e} isn't one of Daggerfall's enemy actors");
                        if (e.EndsWith("Passive")) Problem($"{e} is a passive actor");
                        if (UsesScript(n)) Problem($"{e} takes its scripts from its template: its TMDfSenses doesn't count");
                        if (senses is not { } sk || !quests.TryGetValue(sk, out var q) || q.EditorID != Skyrim.DfSenses.QuestEditorId) Problem($"{e}: Senses isn't the control quest");
                    }
                    Console.WriteLine($"actors with TMDfSenses: {carrying} of {m.Npcs.Count(n => n.FormKey.ModKey == m.ModKey)}");
                    // Language pacification: which language each carrying actor answers to.
                    var byLanguage = m.Npcs.Where(n => n.FormKey.ModKey == m.ModKey).Select(n => Senses(n)).OfType<IScriptEntryGetter>()
                        .Select(s => s.Properties.OfType<IScriptIntPropertyGetter>().FirstOrDefault(p => p.Name == "Language")?.Data ?? -1)
                        .GroupBy(l => l < 0 || l >= Skyrim.DfSenses.LanguageSkills.Length ? "none" : Skyrim.DfSenses.LanguageSkills[l]).OrderByDescending(g => g.Count());
                    Console.WriteLine($"languages: {string.Join(", ", byLanguage.Select(g => $"{g.Key} {g.Count()}"))}");
                    if (m.FormLists.FirstOrDefault(f => f.EditorID == "DFRestEncounters") is { } rest) Console.WriteLine($"rest encounters: {rest.Items.Count} dungeon encounter lists");
                    foreach (var l in m.LeveledNpcs.Where(l => l.EditorID?.StartsWith("DFLCharEncounter") == true))
                        foreach (var en in l.Entries ?? [])
                            if (en.Data is { } d && !Has(d.Reference.FormKey))
                                Problem($"{l.EditorID} picks {(npcs.TryGetValue(d.Reference.FormKey, out var pn) ? pn.EditorID : lists.TryGetValue(d.Reference.FormKey, out var pl) ? pl.EditorID : d.Reference.FormKey.ToString())} without the senses");
                    foreach (var a in m.Npcs.Where(n => n.EditorID?.StartsWith("DFLvlEncounter") == true))
                        if (!UsesScript(a)) Problem($"{a.EditorID} doesn't take its scripts from its list");

                    // Placed actors: how many have the senses (Daggerfall's hostile enemies) and what the rest are.
                    var placed = m.EnumerateMajorRecords<IPlacedNpcGetter>().ToList();
                    var without = placed.Where(p => !Has(p.Base.FormKey)).GroupBy(p => npcs.TryGetValue(p.Base.FormKey, out var b) ? b.EditorID ?? "?" : p.Base.FormKey.ToString())
                        .OrderByDescending(g => g.Count()).ToList();
                    Console.WriteLine($"placed actors: {placed.Count}, with the senses {placed.Count - without.Sum(g => g.Count())}; without: {string.Join(", ", without.Take(25).Select(g => $"{g.Key} {g.Count()}"))}");
                    var spawnTypes = placed.Select(p => p.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == "TMDfSpawnType")?.Properties.OfType<IScriptIntPropertyGetter>().FirstOrDefault()?.Data ?? 0)
                        .GroupBy(t => t).OrderBy(g => g.Key);
                    Console.WriteLine($"spawn distance types: {string.Join(", ", spawnTypes.Select(g => $"type {g.Key} {g.Count()}"))}");
                    Console.WriteLine(problems == 0 ? "no problems" : $"{problems} problems");
                    return problems == 0 ? 0 : 1;
                }
                finally { foreach (var o in others) o.Dispose(); }
            }
            case "esm-find":
            {
                // Dev aid: records of --plugin (default Skyrim.esm) whose editor ID matches --edid (a regex), with type and
                // form ID; --type limits the record type (e.g. SoundDescriptor).
                string plugin = Path.Combine(skyrimData, opts.GetValueOrDefault("plugin", "Skyrim.esm"));   // a file name is in Skyrim's Data
                var re = new System.Text.RegularExpressions.Regex(opts["edid"], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                using var m = SkyrimMod.CreateFromBinaryOverlay(plugin, SkyrimRelease.SkyrimSE);
                foreach (var r in m.EnumerateMajorRecords().Where(r => r.EditorID is { } e && re.IsMatch(e)
                             && (!opts.TryGetValue("type", out var t) || r.Registration.Name.Equals(t, StringComparison.OrdinalIgnoreCase))).Take(60))
                    Console.WriteLine($"{r.FormKey.ID:X6} {r.Registration.Name,-18} {r.EditorID}");
                return 0;
            }
            case "plugin-check":
            {
                // A plugin's links checked against the load order (--data, default Skyrim's Data; --plugins A;B the ones
                // to check, by file name): every form link must reach a record, of a type the link can hold. Links
                // into a missing record or the wrong kind (a plugin built against an older master) crash Skyrim.
                string data = opts.GetValueOrDefault("data", skyrimData);
                var loadOrder = File.ReadAllLines(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Skyrim Special Edition", "Plugins.txt"))
                    .Where(l => l.StartsWith('*')).Select(l => l[1..].Trim()).ToList();
                // A master not in --data (Skyrim's own, beside a scratch folder's plugins) is read from Skyrim's Data.
                string PathOf(string f) => File.Exists(Path.Combine(data, f)) ? Path.Combine(data, f) : Path.Combine(skyrimData, f);
                var order = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Concat(loadOrder)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Where(f => File.Exists(PathOf(f))).ToList();
                var targets = opts["plugins"].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                // A target not in Plugins.txt (a scratch plugin in a --data folder of its own) is checked after its masters.
                order.AddRange(targets.Where(t => !order.Contains(t, StringComparer.OrdinalIgnoreCase) && File.Exists(Path.Combine(data, t))));
                // Only the masters each target needs, plus the target, in load order.
                foreach (var targetSpec in targets)
                {
                    // A full path: a scratch plugin elsewhere, checked against its masters (in --data, else Skyrim's).
                    string target = Path.GetFileName(targetSpec);
                    string targetDir = Path.IsPathRooted(targetSpec) ? Path.GetDirectoryName(targetSpec)! : data;
                    var masters = Setup.Parts.MastersOf(targetDir, target);
                    var mods = order.Where(f => masters.Contains(f, StringComparer.OrdinalIgnoreCase) && !f.Equals(target, StringComparison.OrdinalIgnoreCase))
                        .Select(f => (ISkyrimModDisposableGetter)SkyrimMod.CreateFromBinaryOverlay(PathOf(f), SkyrimRelease.SkyrimSE))
                        .Append(SkyrimMod.CreateFromBinaryOverlay(Path.Combine(targetDir, target), SkyrimRelease.SkyrimSE)).ToList();
                    try
                    {
                        var cache = mods.ToImmutableLinkCache();
                        var mine = mods.Last(m => m.ModKey.FileName.String.Equals(target, StringComparison.OrdinalIgnoreCase));
                        long records = 0, links = 0;
                        var bad = new Dictionary<string, int>();
                        // --parts N: bad records counted by the part (of N, PluginMerger's FormID ranges) that made them.
                        var byPart = opts.TryGetValue("parts", out var partsText) ? new Dictionary<int, int>() : null;
                        int PartOf(uint id) => (int)((id - 0x800) / Skyrim.PluginMerger.Stride(int.Parse(partsText!))) + 1;
                        var examples = new List<string>();
                        foreach (var rec in mine.EnumerateMajorRecords())
                        {
                            records++;
                            foreach (var link in rec.EnumerateFormLinks())
                            {
                                // The engine's own forms (the player, 0x14, and the rest below 0x800) aren't in any file.
                                if (link.IsNull || link.FormKey.ID < 0x800 && link.FormKey.ModKey.FileName.String.Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase)) continue;
                                links++;
                                string? problem = null;
                                if (!cache.TryResolve(link.FormKey, out var found)) problem = "missing";
                                else if (!link.Type.IsAssignableFrom(found.GetType())) problem = $"wrong kind ({found.Registration.Name} where a {link.Type.Name} goes)";
                                if (problem is null) continue;
                                string key = $"{rec.Registration.Name} -> {problem}";
                                bad[key] = bad.GetValueOrDefault(key) + 1;
                                if (byPart is not null && rec.FormKey.ModKey == mine.ModKey) byPart[PartOf(rec.FormKey.ID)] = byPart.GetValueOrDefault(PartOf(rec.FormKey.ID)) + 1;
                                if (examples.Count < 12) examples.Add($"    {rec.Registration.Name} {rec.FormKey} {rec.EditorID}: {link.FormKey} {problem}");
                            }
                            if (records % 1_000_000 == 0) Console.WriteLine($"  {target}: {records:N0} records...");
                        }
                        // Exterior navmeshes: the grid they name must be their cell's, stored Y then X as Skyrim's own are
                        // (2026-10-14: every converted one was X then Y, naming missing cells in the Iliac Bay).
                        int navms = 0, wrongGrid = 0;
                        foreach (var w in mine.Worldspaces)
                            foreach (var c in w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                                if (c.Grid is { } g)
                                    foreach (var n in c.NavigationMeshes)
                                        if (n.Data?.Parent is IWorldspaceNavmeshParentGetter wp)
                                        {
                                            navms++;
                                            if (wp.Coordinates.X != g.Point.Y || wp.Coordinates.Y != g.Point.X)
                                            {
                                                wrongGrid++;
                                                if (byPart is not null) byPart[PartOf(n.FormKey.ID)] = byPart.GetValueOrDefault(PartOf(n.FormKey.ID)) + 1;
                                                if (examples.Count < 12) examples.Add($"    NavigationMesh {n.FormKey} in cell ({g.Point.X},{g.Point.Y}) names grid Y={wp.Coordinates.X} X={wp.Coordinates.Y}");
                                            }
                                        }
                        if (wrongGrid > 0) bad[$"exterior navmesh -> wrong cell grid ({navms:N0} checked)"] = wrongGrid;
                        Console.WriteLine($"{target}: {records:N0} records, {links:N0} links, {bad.Values.Sum():N0} bad");
                        foreach (var (k, n) in bad.OrderByDescending(kv => kv.Value).Take(20)) Console.WriteLine($"  {n,8:N0}  {k}");
                        foreach (var e in examples) Console.WriteLine(e);
                        if (byPart is { Count: > 0 }) Console.WriteLine("  bad records by part: " + string.Join(", ", byPart.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}: {kv.Value:N0}")));
                    }
                    finally { foreach (var m in mods) m.Dispose(); }
                }
                return 0;
            }
            case "plugin-compare":
            {
                // Dev aid: two plugins' records by type, and their navmeshes' and land's contents, compared (--a, --b).
                (Dictionary<string, int> Types, long Nav, long Land, long NavTris, long NavVerts) Read(string path)
                {
                    using var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                    var types = m.EnumerateMajorRecords().GroupBy(r => r.Registration.Name).ToDictionary(g => g.Key, g => g.Count());
                    long nav = 0, land = 0, navTris = 0, navVerts = 0;
                    foreach (var r in m.EnumerateMajorRecords())
                        if (r is INavigationMeshGetter n && n.Data is { } d)
                        {
                            navTris += d.Triangles.Count; navVerts += d.Vertices.Count;
                            nav += d.Vertices.Count * 31 + d.Triangles.Count * 7 + d.Vertices.Sum(v => (long)(v.X * 3 + v.Y * 5 + v.Z * 7)) + d.Triangles.Sum(t => (long)t.Vertices.X + t.Vertices.Y * 3 + t.Vertices.Z * 5);
                        }
                        else if (r is ILandscapeGetter l && l.VertexHeightMap is { } h)
                            land += (long)(h.Offset * 10) + h.HeightMap.Sum(x => (long)x.Value);
                    return (types, nav, land, navTris, navVerts);
                }
                var a = Read(opts["a"]); var b = Read(opts["b"]);
                foreach (var t in a.Types.Keys.Union(b.Types.Keys).Order())
                    if (a.Types.GetValueOrDefault(t) != b.Types.GetValueOrDefault(t)) Console.WriteLine($"  {t}: {a.Types.GetValueOrDefault(t)} vs {b.Types.GetValueOrDefault(t)}");
                Console.WriteLine($"records {a.Types.Values.Sum()} vs {b.Types.Values.Sum()}; navmesh {a.Nav} vs {b.Nav} ({a.NavTris:N0} vs {b.NavTris:N0} triangles, {a.NavVerts:N0} vs {b.NavVerts:N0} vertices); land {a.Land} vs {b.Land}");
                return a.Types.Values.Sum() == b.Types.Values.Sum() && a.Nav == b.Nav && a.Land == b.Land ? 0 : 2;
            }
            case "plugin-compress":
            {
                // A plugin rewritten with its navmesh and land records compressed (PluginMerger), in place unless --out.
                string file = Path.GetFullPath(opts["plugin"]);
                string target = opts.TryGetValue("out", out var o) ? Path.GetFullPath(o) : file + ".tmp";
                long before = new FileInfo(file).Length;
                var head = new byte[12];
                using (var fs = File.OpenRead(file)) fs.ReadExactly(head);
                long records = Skyrim.PluginMerger.Merge([file], target, (BitConverter.ToUInt32(head, 8) & 1) != 0);
                if (!opts.ContainsKey("out")) File.Move(target, file, overwrite: true);
                Console.WriteLine($"{Path.GetFileName(file)}: {records} records and groups, {before >> 20} MB -> {new FileInfo(opts.ContainsKey("out") ? target : file).Length >> 20} MB");
                return 0;
            }
            case "lod-test":
            {
                // Dev aid: terrain LOD for one Morrowind cell block from a built Morrowind.esm (--plugin), into --out.
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var ws = m.Worldspaces.First();
                var copy = new SkyrimMod(m.ModKey, SkyrimRelease.SkyrimSE);
                copy.DeepCopyIn(m);
                var w = copy.Worldspaces.First();
                var cells = w.SubCells.SelectMany(b => b.Items).SelectMany(s => s.Items).Where(c => c.Grid is not null && Math.Abs(c.Grid.Point.X + 18) < 4 && Math.Abs(c.Grid.Point.Y + 10) < 4)
                    .ToDictionary(c => (c.Grid!.Point.X, c.Grid.Point.Y));
                var lod = new Skyrim.TerrainLod(copy, opts.GetValueOrDefault("out", "out/lodtest"), skyrimData);
                lod.Build(w.EditorID!, cells, c => c.Flags.HasFlag(Cell.Flag.HasWater) ? 0f : null, -2048);
                Console.WriteLine($"{lod.Chunks} chunks; {string.Join("; ", lod.Problems)}");
                return 0;
            }
            case "dds-png":
            {
                // Dev aid: a DDS (--file) as a PNG (--out), for looking at generated textures.
                var img = Util.DdsImage.Decode(File.ReadAllBytes(opts["file"])) ?? throw new InvalidDataException("unsupported DDS");
                var rgb = new byte[img.W * img.H * 3];
                for (int i = 0; i < img.Px.Length; i++) { rgb[i * 3] = (byte)(img.Px[i] >> 16); rgb[i * 3 + 1] = (byte)(img.Px[i] >> 8); rgb[i * 3 + 2] = (byte)img.Px[i]; }
                Util.Png.WriteRgb(opts["out"], img.W, img.H, rgb);
                return 0;
            }
            case "btr-info":
            {
                // Dev aid: a terrain LOD chunk's shapes: vertex and UV ranges, colours, vertex format, transforms, bounds.
                using var nif = new nifly.NifFile();
                nif.Load(opts["file"]);
                var hdr = nif.GetHeader();
                for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                {
                    var block = hdr.GetBlockById(i);
                    Console.Write($"[{i}] {hdr.GetBlockTypeStringById(i)}");
                    if (block is nifly.NiAVObject av) Console.Write($" name={av.name.get()} t=({av.transform.translation.x},{av.transform.translation.y},{av.transform.translation.z}) s={av.transform.scale} flags={av.flags}");
                    if (block is nifly.BSMultiBoundAABB bb) Console.Write($" pos=({bb.center.x},{bb.center.y},{bb.center.z}) half=({bb.halfExtent.x},{bb.halfExtent.y},{bb.halfExtent.z})");
                    if (block is nifly.BSMultiBoundNode mbn) Console.Write($" cull={mbn.cullingMode} mb={mbn.multiBoundRef.index}");
                    if (block is nifly.BSTriShape ts)
                    {
                        var v = nif.GetVertsForShape(ts); var uv = nif.GetUvsForShape(ts);
                        Console.Write($" numVerts={ts.GetNumVertices()} numTris={ts.GetNumTriangles()} vertData={ts.vertData.Count}");
                        if (v is null || v.Count == 0) { Console.WriteLine(" (no vertices)"); continue; }
                        Console.Write($" verts={v.Count} x=[{v.Min(p => p.x)},{v.Max(p => p.x)}] y=[{v.Min(p => p.y)},{v.Max(p => p.y)}] z=[{v.Min(p => p.z)},{v.Max(p => p.z)}]");
                        if (uv is { Count: > 0 }) Console.Write($" u=[{uv.Min(p => p.u)},{uv.Max(p => p.u)}] v=[{uv.Min(p => p.v)},{uv.Max(p => p.v)}]");
                        Console.Write($" shader={ts.ShaderPropertyRef().index} alpha={ts.AlphaPropertyRef().index}");
                        Console.Write($" colors={ts.HasVertexColors()} normals={ts.HasNormals()} tangents={ts.HasTangents()} vsize={ts.vertexSize} desc={ts.vertexDesc.GetFlags()} bound=({ts.GetBounds().center.x},{ts.GetBounds().center.y},{ts.GetBounds().center.z}) r={ts.GetBounds().radius}");
                        for (int k = 0; k < 3; k++) Console.Write($" v{k}=({v[k].x},{v[k].y},{v[k].z})/{(uv is { Count: > 0 } ? $"{uv[k].u},{uv[k].v}" : "")}");
                    }
                    // Sub-index shapes (object LOD): their segments, read by reflection (--segments).
                    if (opts.ContainsKey("segments") && block is nifly.BSSubIndexTriShape)
                    {
                        Console.WriteLine();
                        void Show(object? o, string indent, int depth)
                        {
                            if (o is null || depth > 5) return;
                            foreach (var p in o.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                            {
                                if (p.GetIndexParameters().Length > 0 || p.DeclaringType == typeof(object)) continue;
                                if (depth == 0 && p.DeclaringType != o.GetType()) continue;
                                object? val; try { val = p.GetValue(o); } catch { continue; }
                                if (val is null) continue;
                                var t = val.GetType();
                                if (t.IsPrimitive || val is string || t.IsEnum) { Console.WriteLine($"{indent}{p.Name} = {val}"); continue; }
                                if (val is System.Collections.IEnumerable list)
                                {
                                    int k = 0;
                                    foreach (var item in list)
                                    {
                                        if (k < 24)
                                        {
                                            if (item.GetType().IsPrimitive) Console.WriteLine($"{indent}{p.Name}[{k}] = {item}");
                                            else { Console.WriteLine($"{indent}{p.Name}[{k}]:"); Show(item, indent + "  ", depth + 1); }
                                        }
                                        k++;
                                    }
                                    Console.WriteLine($"{indent}{p.Name}: {k} items");
                                    continue;
                                }
                                if (t.Namespace == "nifly" && depth < 4) { Console.WriteLine($"{indent}{p.Name}: ({t.Name})"); Show(val, indent + "  ", depth + 1); }
                            }
                        }
                        Show(block, "    ", 0);
                        var sits = (nifly.BSSubIndexTriShape)block;
                        var inf = new nifly.NifSegmentationInfo();
                        var triParts = new nifly.vectorint();
                        sits.GetSegmentation(inf, triParts);
                        Console.WriteLine($"    segments {inf.segs.Count} ssf '{inf.ssfFile}'");
                        foreach (var sg in inf.segs) Console.WriteLine($"      part {sg.partID} subs {sg.subs.Count}: {string.Join(" ", sg.subs.Select(sub => $"[{sub.partID} user {sub.userSlotID} mat {sub.material}]"))}");
                        Console.WriteLine($"    triangles per part: {string.Join(" ", triParts.GroupBy(x => x).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"))}");
                    }
                    Console.WriteLine();
                }
                return 0;
            }
            case "fx-info":
            {
                // Dev aid: Skyrim particle effect emitters (--file a;b): particle radius, speed x life span, volume.
                foreach (var file in opts["file"].Split(';'))
                {
                    using var nif = new nifly.NifFile();
                    if (nif.Load(file) != 0) { Console.WriteLine($"{file}: unreadable"); continue; }
                    var hdr = nif.GetHeader();
                    Console.WriteLine(Path.GetFileName(file));
                    for (uint i = 0; i < hdr.GetNumBlocks(); i++)
                        if (hdr.GetBlockById(i) is nifly.NiPSysEmitter e)
                        {
                            string vol = e switch { nifly.NiPSysBoxEmitter b => $"box {b.width}x{b.depth}x{b.height}", nifly.NiPSysCylinderEmitter c => $"cylinder r{c.radius} h{c.height}", nifly.NiPSysSphereEmitter sp => $"sphere r{sp.radius}", _ => hdr.GetBlockTypeStringById(i) };
                            Console.WriteLine($"   radius {e.radius} speed {e.speed} life {e.lifeSpan} rise {e.speed * e.lifeSpan:F0} {vol}");
                        }
                }
                return 0;
            }
            case "nif-dump":
                foreach (var f in opts["file"].Split(';')) Util.NifDump.Dump(f);
                return 0;
            case "probe-nifly":
            {
                // Dev aid: list nifly binding types/members matching --name.
                var asm = opts.GetValueOrDefault("asm") == "recast" ? typeof(DotRecast.Recast.RcBuilder).Assembly : typeof(nifly.NifFile).Assembly;
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "NifFile"));
                foreach (var t in asm.GetExportedTypes().Where(t => rx.IsMatch(t.Name)).OrderBy(t => t.Name))
                {
                    Console.WriteLine($"type {t.FullName} : {t.BaseType?.Name}");
                    if (opts.ContainsKey("members"))
                        foreach (var m in t.GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly).Where(m => m is not System.Reflection.MethodInfo { IsSpecialName: true }))
                            Console.WriteLine($"   {m.MemberType} {m}");
                }
                return 0;
            }
            case "find-textures":
            {
                // Dev aid: vanilla texture sets matching --name, with their diffuse/normal paths.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var t in skyrim.TextureSets.Where(t => rx.IsMatch(t.EditorID ?? "") || rx.IsMatch(t.Diffuse?.DataRelativePath.Path ?? "")).Take(int.Parse(opts.GetValueOrDefault("take", "80"))))
                    Console.WriteLine($"{t.EditorID,-36} {t.Diffuse?.DataRelativePath.Path} | {t.NormalOrGloss?.DataRelativePath.Path}");
                return 0;
            }
            case "block-mesh":
            {
                // Build the NIF for one RDB block (--block) and dump it back for inspection.
                var blocksBsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var block = DfDungeonBlock.Load(blocksBsa, opts.GetValueOrDefault("block", "S0000999.RDB"));
                string path = Path.Combine(opts.GetValueOrDefault("out", "out/mod"), "Meshes", "TamrielMerge", "Dungeons", Path.ChangeExtension(block.Name, ".nif"));
                var r = Skyrim.Meshes.BlockMeshBuilder.Build(block, DfModel.LoadAll(arena2), path);
                Console.WriteLine($"{r.Path}: {r.Polygons} polygons, {r.Shapes} shapes, {r.CollisionShapes} collision slabs, bounds {r.Min} .. {r.Max}");
                Util.NifDump.Dump(path);
                return 0;
            }
            case "prop-sheets":
            {
                // Contact sheets of prop models (by tag) across all dungeon blocks, most used first, with a legend.
                var models = DfModel.LoadAll(arena2);
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var objs = bsa.Entries.Where(e => e.Name.EndsWith(".RDB")).SelectMany(e => DfDungeonBlock.Load(bsa, e.Name).Objects).Where(o => o.Type == DfRdbObjectType.Model).ToList();
                var tags = opts.GetValueOrDefault("tags", "XXX").Split(',');
                var only = opts.TryGetValue("ids", out var idList) ? idList.Split(',').Select(uint.Parse).ToHashSet() : null;
                var ids = objs.Where(o => only is null ? tags.Contains(o.Tag) : only.Contains(o.ModelId)).GroupBy(o => o.ModelId).OrderByDescending(g => g.Count()).Select(g => (Id: g.Key, Count: g.Count(), Tag: g.First().Tag)).Where(x => models.ContainsKey(x.Id)).ToList();
                Directory.CreateDirectory("out/props");
                for (int page = 0; page * 48 < ids.Count; page++)
                {
                    var chunk = ids.Skip(page * 48).Take(48).ToList();
                    ModelSheet.Render(chunk.Select(x => models[x.Id]).ToList(), $"out/props/sheet{page}.png");
                    Console.WriteLine($"sheet{page}: " + string.Join(" ", chunk.Select((x, i) => { var s = models[x.Id].Max - models[x.Id].Min; return $"{i}={x.Id}({x.Tag} x{x.Count} {s.X:F0}x{s.Y:F0}x{s.Z:F0})"; })));
                }
                return 0;
            }
            case "objects-near":
            {
                // Dev aid: Daggerfall objects of a dungeon near a Skyrim-space point (x, y), with tags and actions.
                float sx = float.Parse(opts["x"], System.Globalization.CultureInfo.InvariantCulture) / Skyrim.Meshes.BlockMeshBuilder.Scale, sz = float.Parse(opts["y"], System.Globalization.CultureInfo.InvariantCulture) / Skyrim.Meshes.BlockMeshBuilder.Scale;
                var models = DfModel.LoadAll(arena2);
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                var dungeon = DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []);
                float near = float.Parse(opts.GetValueOrDefault("r", "200"));
                if (opts.ContainsKey("flats"))
                    foreach (var (block, pl) in dungeon.Blocks)
                        foreach (var o in block.Objects.Where(o => o.Type is DfRdbObjectType.Flat or DfRdbObjectType.Light))
                        {
                            var p = DfDungeon.BlockOrigin(pl) + o.Position;
                            if (MathF.Abs(p.X - sx) > near || MathF.Abs(p.Z - sz) > near) continue;
                            Console.WriteLine($"  {o.Type} {(o.Type == DfRdbObjectType.Flat ? $"{o.TextureArchive}.{o.TextureRecord}" : $"radius {o.LightRadius}")} at DF ({p.X:F0},{p.Y:F0},{p.Z:F0}) = Skyrim ({p.X * Skyrim.Meshes.BlockMeshBuilder.Scale:F0},{p.Z * Skyrim.Meshes.BlockMeshBuilder.Scale:F0},{p.Y * Skyrim.Meshes.BlockMeshBuilder.Scale:F0})");
                        }
                foreach (var (block, pl) in dungeon.Blocks)
                    foreach (var o in block.Objects.Where(o => o.Type == DfRdbObjectType.Model))
                    {
                        var p = DfDungeon.BlockOrigin(pl) + o.Position;
                        if (MathF.Abs(p.X - sx) > near || MathF.Abs(p.Z - sz) > near) continue;
                        var m = models.GetValueOrDefault(o.ModelId); var size = m is null ? default : m.Max - m.Min;
                        Console.WriteLine($"  {o.ModelId} tag {o.Tag} at DF ({p.X:F0},{p.Y:F0},{p.Z:F0}) size {size.X:F0}x{size.Y:F0}x{size.Z:F0} top {p.Y + (m?.Max.Y ?? 0):F0} action {(o.Action is null ? "-" : $"axis {o.Action.Axis} flags {o.Action.Flags} mag {o.Action.Magnitude}")} replaced {Skyrim.PropReplacer.IsReplaced(o.ModelId)}");
                    }
                return 0;
            }
            case "verify-actions":
            {
                // Every scripted Daggerfall action reference: base, pose, script properties, chain target.
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                var refs = plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).SelectMany(c => c.Persistent.Concat(c.Temporary)).OfType<IPlacedObjectGetter>().ToList();
                var byKey = refs.ToDictionary(r => r.FormKey);
                var names = plugin.EnumerateMajorRecords().Where(r => r.EditorID is not null).ToDictionary(r => r.FormKey, r => r.EditorID!);
                int bad = 0;
                foreach (var r in refs.Where(r => r.VirtualMachineAdapter?.Scripts.Any(s => s.Name.StartsWith("TMDf")) == true))
                {
                    var s = r.VirtualMachineAdapter!.Scripts.First();
                    var rot = r.Placement!.Rotation;
                    string Prop(IScriptPropertyGetter p) => p switch
                    {
                        IScriptIntPropertyGetter i => i.Data.ToString(),
                        IScriptFloatPropertyGetter f => f.Data.ToString("F2"),
                        IScriptBoolPropertyGetter b => b.Data.ToString(),
                        IScriptFloatListPropertyGetter fl => "[" + string.Join(", ", fl.Data.Select(v => v.ToString("F0"))) + "]",
                        IScriptObjectPropertyGetter o => byKey.TryGetValue(o.Object.FormKey, out var t) ? $"ref {names.GetValueOrDefault(t.Base.FormKey, t.Base.FormKey.ToString())}" : o.Object.FormKey.ToString(),
                        _ => "?",
                    };
                    Console.WriteLine($"{s.Name} on {names.GetValueOrDefault(r.Base.FormKey, r.Base.FormKey.ToString())} at {r.Placement.Position} angles ({rot.X * 180 / MathF.PI:F0},{rot.Y * 180 / MathF.PI:F0},{rot.Z * 180 / MathF.PI:F0}) persistent={(r.SkyrimMajorRecordFlags & (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400) != 0}");
                    Console.WriteLine("    " + string.Join("  ", s.Properties.Select(p => $"{p.Name}={Prop(p)}")));
                    foreach (var p in s.Properties.OfType<IScriptObjectPropertyGetter>())
                        if (!p.Object.FormKey.IsNull && p.Object.FormKey.ModKey == plugin.ModKey && !byKey.ContainsKey(p.Object.FormKey) && !names.ContainsKey(p.Object.FormKey)) { bad++; Console.WriteLine($"    UNRESOLVED {p.Name}"); }
                }
                return bad == 0 ? 0 : 2;
            }
            case "check-euler":
            {
                // Round trip: Daggerfall transform -> Skyrim angles -> rotation matrix, compared with the original.
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                float worst = 0; int n = 0;
                foreach (var e in bsa.Entries.Where(e => e.Name.EndsWith(".RDB")))
                    foreach (var o in DfDungeonBlock.Load(bsa, e.Name).Objects.Where(o => o.Type == DfRdbObjectType.Model))
                    {
                        var m = DfDungeon.ObjectTransform(o, System.Numerics.Vector3.Zero);
                        var ang = Skyrim.ActionObjectBuilder.SkyrimAngles(m);
                        float a = -ang.X, b = -ang.Y, c = -ang.Z;
                        // R = Rz(c) Ry(b) Rx(a), column vectors
                        float ca = MathF.Cos(a), sa = MathF.Sin(a), cb = MathF.Cos(b), sb = MathF.Sin(b), cc = MathF.Cos(c), sc = MathF.Sin(c);
                        float[,] R = { { cc * cb, cc * sb * sa - sc * ca, cc * sb * ca + sc * sa }, { sc * cb, sc * sb * sa + cc * ca, sc * sb * ca - cc * sa }, { -sb, cb * sa, cb * ca } };
                        float[,] u = { { m.M11, m.M12, m.M13 }, { m.M21, m.M22, m.M23 }, { m.M31, m.M32, m.M33 } };
                        int[] map = [0, 2, 1];
                        for (int i = 0; i < 3; i++) for (int j = 0; j < 3; j++) worst = MathF.Max(worst, MathF.Abs(R[i, j] - u[map[j], map[i]]));
                        n++;
                    }
                Console.WriteLine($"{n} object orientations, worst round-trip error {worst:E2}");
                return worst < 1e-3f ? 0 : 2;
            }
            case "verify-doors":
            {
                // Each placed door should sit in a doorway: just beyond each edge of the leaf there should be a wall
                // face parallel to the door (the door's facing is that wall's normal, either sign).
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                var models = DfModel.LoadAll(arena2);
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == "Privateer's Hold" && l.DungeonBlocks.Count > 0);
                var dungeon = DfDungeon.Load(loc, bsa, []);
                var tris = new List<(System.Numerics.Vector3 A, System.Numerics.Vector3 B, System.Numerics.Vector3 C)>();
                foreach (var (block, pl) in dungeon.Blocks)
                {
                    var o = DfDungeon.BlockOrigin(pl);
                    var shift = new System.Numerics.Vector3(o.X, o.Z, o.Y) * Skyrim.Meshes.BlockMeshBuilder.Scale;
                    foreach (var poly in Skyrim.Meshes.BlockMeshBuilder.SolidPolygons(block, models))
                        for (int i = 1; i + 1 < poly.Points.Length; i++) tris.Add((poly.Points[0] + shift, poly.Points[i] + shift, poly.Points[i + 1] + shift));
                }
                float Ray(System.Numerics.Vector3 o, System.Numerics.Vector3 dir, float max)
                {
                    float best = max;
                    foreach (var (A, B, C) in tris)
                    {
                        var e1 = B - A; var e2 = C - A; var pv = System.Numerics.Vector3.Cross(dir, e2); float det = System.Numerics.Vector3.Dot(e1, pv);
                        if (MathF.Abs(det) < 1e-6f) continue;
                        var tv = o - A; float u = System.Numerics.Vector3.Dot(tv, pv) / det; if (u < 0 || u > 1) continue;
                        var qv = System.Numerics.Vector3.Cross(tv, e1); float v = System.Numerics.Vector3.Dot(dir, qv) / det; if (v < 0 || u + v > 1) continue;
                        float t = System.Numerics.Vector3.Dot(e2, qv) / det; if (t > 0.5f && t < best) best = t;
                    }
                    return best;
                }
                // Props (everything placed that isn't a door, light, marker or the block statics): standing on a floor,
                // clear of walls in every direction.
                {
                    using var skyrimForBounds = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                    var propNames = new Dictionary<uint, string> { [0x2EC1C] = "chair", [0x6B691] = "throne", [0x2E6CF] = "bench", [0x6B57B] = "pew", [0x74EC6] = "stool", [0x24CB9] = "table", [0xE2604] = "long table", [0xE25DB] = "low table", [0x30091] = "bed", [0x2E6CD] = "bookcase", [0x24CA7] = "wardrobe", [0x101E97] = "counter", [0x7A603] = "crate", [0xDAE80] = "big crate", [0x3AC21] = "chest", [0xEB093] = "coffin", [0xEB094] = "open coffin", [0xAA045] = "torture rack", [0xE3FC7] = "statue", [0x91712] = "big statue", [0xF2D2F] = "rocks", [0x1BCC2] = "anvil", [0xF4B4B] = "lectern" };
                    int propOk = 0, propTotal = 0;
                    // Same mesh-derived bounds the placement used.
                    var shapes = new Skyrim.PropReplacer(skyrimForBounds, skyrimData);
                    var bounds = propNames.Keys.ToDictionary(id => id, id => { var sh = shapes.SkyrimShape(new Mutagen.Bethesda.Plugins.FormKey(Mutagen.Bethesda.Plugins.ModKey.FromFileName("Skyrim.esm"), id)); return (sh.Min, sh.Max); });
                    var props = plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).SelectMany(c => c.Temporary).OfType<IPlacedObjectGetter>()
                        .Where(r => r.Base.FormKey.ModKey.FileName == "Skyrim.esm" && propNames.ContainsKey(r.Base.FormKey.ID)).ToList();
                    // World-space vertical extent and (axis-aligned, conservative) footprint of each prop.
                    (float Bottom, float Top, float X0, float X1, float Y0, float Y1) Extent(IPlacedObjectGetter r)
                    {
                        var (bmin, bmax) = bounds[r.Base.FormKey.ID]; float sc = r.Scale ?? 1; var p = r.Placement!.Position;
                        float rad = MathF.Max(MathF.Max(MathF.Abs(bmin.X), MathF.Abs(bmax.X)), MathF.Max(MathF.Abs(bmin.Y), MathF.Abs(bmax.Y))) * sc;
                        return (p.Z + bmin.Z * sc, p.Z + bmax.Z * sc, p.X - rad, p.X + rad, p.Y - rad, p.Y + rad);
                    }
                    foreach (var r in props)
                    {
                        propTotal++;
                        var p0 = r.Placement!.Position;
                        var e = Extent(r);
                        var probe = new System.Numerics.Vector3(p0.X, p0.Y, e.Bottom + 40);
                        float floorGap = Ray(probe, -System.Numerics.Vector3.UnitZ, 1000) - 40;
                        // Daggerfall stacks crates: a prop resting on another prop is fine.
                        bool onProp = props.Any(o => !ReferenceEquals(o, r) && Extent(o) is var oe && MathF.Abs(oe.Top - e.Bottom) < 24
                            && p0.X > oe.X0 && p0.X < oe.X1 && p0.Y > oe.Y0 && p0.Y < oe.Y1);
                        float wallClear = new[] { System.Numerics.Vector3.UnitX, -System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, -System.Numerics.Vector3.UnitY }
                            .Min(dd => Ray(new System.Numerics.Vector3(p0.X, p0.Y, e.Bottom + 40), dd, 600));
                        bool good = (MathF.Abs(floorGap) < 24 || onProp) && wallClear > 10;
                        if (good) propOk++;
                        else
                        {
                            var below = props.Where(o => !ReferenceEquals(o, r)).Select(o => (o, oe: Extent(o))).Where(x => p0.X > x.oe.X0 - 40 && p0.X < x.oe.X1 + 40 && p0.Y > x.oe.Y0 - 40 && p0.Y < x.oe.Y1 + 40 && x.oe.Top <= e.Bottom + 30)
                                .Select(x => $"{propNames[x.o.Base.FormKey.ID]} top {e.Bottom - x.oe.Top:F0} below");
                            Console.WriteLine($"  {propNames[r.Base.FormKey.ID]} at {p0}: lowest point {floorGap:F0} above floor, nearest wall {wallClear:F0}; props under it: {string.Join("; ", below)}");
                        }
                    }
                    Console.WriteLine($"props standing on a floor, clear of walls: {propOk}/{propTotal}");
                }
                var doorBases = new HashSet<uint> { 0x4DFD9, 0x439C2 };
                int ok = 0, total = 0;
                foreach (var cell in plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                    foreach (var r in cell.Temporary.OfType<IPlacedObjectGetter>().Where(r => doorBases.Contains(r.Base.FormKey.ID)))
                    {
                        total++;
                        float h = r.Placement!.Rotation.Z, s = r.Scale ?? 1;
                        var facing = new System.Numerics.Vector3(MathF.Sin(h), MathF.Cos(h), 0);
                        var across = new System.Numerics.Vector3(facing.Y, -facing.X, 0);
                        float leafOffset = r.Base.FormKey.ID == 0x439C2 ? 264 * s : -72 * s;
                        float halfWidth = (r.Base.FormKey.ID == 0x439C2 ? 146 : 67) * s;
                        var p0 = r.Placement.Position;
                        var leaf = new System.Numerics.Vector3(p0.X, p0.Y, p0.Z + 100 * s) + facing * leafOffset;
                        float left = Ray(leaf, across, 600), right = Ray(leaf, -across, 600), front = Ray(leaf, facing, 600), back = Ray(leaf, -facing, 600);
                        bool good = left < halfWidth + 80 && right < halfWidth + 80 && front > 60 && back > 60;
                        if (good) ok++;
                        else Console.WriteLine($"  door heading {h * 180 / MathF.PI:F0} halfwidth {halfWidth:F0}: walls left {left:F0} right {right:F0}, open front {front:F0} back {back:F0}");
                    }
                Console.WriteLine($"doors in a doorway: {ok}/{total}");
                return 0;
            }
            case "survey-objects":
            {
                // What all RDB dungeon blocks contain: model tags, actions, and flats by archive.
                var models = DfModel.LoadAll(arena2);
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var blocks = bsa.Entries.Where(e => e.Name.EndsWith(".RDB")).Select(e => DfDungeonBlock.Load(bsa, e.Name)).ToList();
                var objs = blocks.SelectMany(b => b.Objects).ToList();
                Console.WriteLine($"{blocks.Count} blocks, {objs.Count(o => o.Type == DfRdbObjectType.Model)} model placements, {objs.Count(o => o.Type == DfRdbObjectType.Flat)} flats, {objs.Count(o => o.Type == DfRdbObjectType.Light)} lights");
                Console.WriteLine("model tags (placements, distinct models, examples id:size):");
                foreach (var g in objs.Where(o => o.Type == DfRdbObjectType.Model).GroupBy(o => o.Tag).OrderByDescending(g => g.Count()))
                {
                    var ex = g.GroupBy(o => o.ModelId).OrderByDescending(x => x.Count()).Take(5).Select(x =>
                    {
                        var m = models.GetValueOrDefault(x.Key); var s = m is null ? System.Numerics.Vector3.Zero : m.Max - m.Min;
                        return $"{x.Key}:{s.X:F0}x{s.Y:F0}x{s.Z:F0}";
                    });
                    Console.WriteLine($"  {g.Key,-4} {g.Count(),6} {g.Select(o => o.ModelId).Distinct().Count(),4}  actions {g.Count(o => o.HasAction),4}  {string.Join("  ", ex)}");
                }
                var acts = objs.Where(o => o.Action is not null).ToList();
                Console.WriteLine($"actions: {acts.Count}; axes {string.Join(" ", acts.GroupBy(o => o.Action!.Axis).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"))}; flags {string.Join(" ", acts.GroupBy(o => o.Action!.Flags).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}"))}; chained {acts.Count(o => o.Action!.NextObjectIndex >= 0)}");
                foreach (var g in acts.GroupBy(o => o.Action!.Flags).OrderByDescending(g => g.Count()).Take(14))
                    Console.WriteLine($"  action type {g.Key,3}: {g.Count(),4}  axes {string.Join(" ", g.GroupBy(o => o.Action!.Axis).OrderBy(x => x.Key).Select(x => $"{x.Key}x{x.Count()}"))}  triggers {string.Join(" ", g.GroupBy(o => o.TriggerAndLock & 0xF).OrderBy(x => x.Key).Select(x => $"{x.Key}x{x.Count()}"))}  tags {string.Join(" ", g.GroupBy(o => o.Tag).OrderByDescending(x => x.Count()).Take(6).Select(x => $"{x.Key}x{x.Count()}"))}  static rot non-yaw {g.Count(o => MathF.Abs(o.Rotation.X) > 0.1f || MathF.Abs(o.Rotation.Z) > 0.1f)}");
                Console.WriteLine("lock indices on doors: " + string.Join(" ", objs.Where(o => o.Tag is "DOR" or "DDR" or "NEW" or "CAV").GroupBy(o => o.TriggerAndLock >> 4).OrderBy(g => g.Key).Select(g => $"{g.Key}x{g.Count()}")));
                Console.WriteLine("flats by archive: " + string.Join(" ", objs.Where(o => o.Type == DfRdbObjectType.Flat).GroupBy(o => o.TextureArchive).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}x{g.Count()}({g.Select(o => o.TextureRecord).Distinct().Count()})")));
                return 0;
            }
            case "seams":
            {
                // Floor openings along each block's edges (block-local DF units), to check how blocks join.
                var models = DfModel.LoadAll(arena2);
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                foreach (var pl in loc.DungeonBlocks)
                {
                    var block = DfDungeonBlock.Load(bsa, pl.BlockName);
                    var single = new DfDungeon { Location = loc, Blocks = [(block, pl with { X = 0, Z = 0 })] };
                    var floors = single.Polygons(models).Where(p => p.Normal.Y > 0.7f).ToList();
                    string Edge(Func<System.Numerics.Vector3, float> dist, Func<System.Numerics.Vector3, float> along) =>
                        string.Join(" ", floors.Where(p => p.Points.Any(v => dist(v) < 48)).Select(p => $"{p.Points.Where(v => dist(v) < 48).Average(along):F0}@{p.Points.Average(v => v.Y):F0}").Distinct().Take(8));
                    Console.WriteLine($"{pl.BlockName} at ({pl.X},{pl.Z}):");
                    Console.WriteLine($"   west  x=0    : {Edge(v => v.X, v => v.Z)}");
                    Console.WriteLine($"   east  x=2048 : {Edge(v => 2048 - v.X, v => v.Z)}");
                    Console.WriteLine($"   south z=0    : {Edge(v => v.Z, v => v.X)}");
                    Console.WriteLine($"   north z=2048 : {Edge(v => 2048 - v.Z, v => v.X)}");
                }
                return 0;
            }
            case "step-heights":
            {
                // Rise between adjacent horizontal levels inside each model used by a dungeon: stair step heights.
                var models = DfModel.LoadAll(arena2);
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                var dungeon = DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []);
                var rises = new Dictionary<int, int>();
                foreach (var id in dungeon.Blocks.SelectMany(b => b.Block.Objects).Where(o => o.Type == DfRdbObjectType.Model).Select(o => o.ModelId).Distinct())
                {
                    if (!models.TryGetValue(id, out var m)) continue;
                    var levels = m.Polygons.Where(p => p.Normal.Y > 0.9f).Select(p => (int)MathF.Round(p.Points.Average(q => q.Y))).Distinct().OrderBy(y => y).ToList();
                    if (levels.Count < 4) continue; // stairs have many small levels
                    foreach (var r in levels.Zip(levels.Skip(1), (a, b) => b - a).Where(r => r > 0 && r <= 64)) rises[r] = rises.GetValueOrDefault(r) + 1;
                }
                Console.WriteLine("step rise (DF units) x count: " + string.Join(" ", rises.OrderByDescending(r => r.Value).Take(10).Select(r => $"{r.Key}x{r.Value}")));
                return 0;
            }
            case "model-info":
            {
                // Dev aid: a model's bounds and polygon normals grouped by texture archive.
                var m = DfModel.LoadAll(arena2)[uint.Parse(opts["id"])];
                Console.WriteLine($"model {m.Id}: min {m.Min} max {m.Max}, {m.Polygons.Count} polygons");
                foreach (var g in m.Polygons.GroupBy(p => (p.TextureArchive, Normal: new System.Numerics.Vector3(MathF.Round(p.Normal.X, 2), MathF.Round(p.Normal.Y, 2), MathF.Round(p.Normal.Z, 2)))))
                    Console.WriteLine($"  archive {g.Key.TextureArchive,3} normal {g.Key.Normal} x{g.Count()}");
                return 0;
            }
            case "dungeon-info":
            {
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                var dungeon = DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []);
                DungeonPreview.Report(dungeon, DfModel.LoadAll(arena2), opts.GetValueOrDefault("out", $"out/dungeon-{Skyrim.LocationPlacer.Sanitize(loc.Name)}.png"));
                return 0;
            }
            case "find-rooms":
            {
                // Dev aid: small vanilla interior cells built only from statics and lights (no actors, doors,
                // containers or scripted objects), as candidates to copy for a start room.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var kind = new Dictionary<Mutagen.Bethesda.Plugins.FormKey, string>();
                foreach (var s in skyrim.Statics) kind[s.FormKey] = "STAT";
                foreach (var s in skyrim.Lights) kind[s.FormKey] = "LIGH";
                foreach (var s in skyrim.MoveableStatics) kind[s.FormKey] = "MSTT";
                var cells = skyrim.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells);
                foreach (var c in cells)
                {
                    var refs = c.Temporary.Concat(c.Persistent).ToList();
                    if (refs.Count is < 25 or > 400) continue;
                    var objs = refs.OfType<IPlacedObjectGetter>().ToList();
                    if (objs.Count != refs.Count) continue; // has actors or other placed types
                    var kinds = objs.Select(o => kind.GetValueOrDefault(o.Base.FormKey, "OTHER")).ToList();
                    if (kinds.Count(k => k == "OTHER") > 2 || kinds.Count(k => k == "LIGH") == 0) continue;
                    var models = objs.Select(o => skyrim.Statics.FirstOrDefault(s => s.FormKey == o.Base.FormKey)?.Model?.File.DataRelativePath.Path).Where(m => m is not null).ToList();
                    string kit = models.GroupBy(m => Path.GetDirectoryName(m!)).OrderByDescending(g => g.Count()).First().Key ?? "";
                    Console.WriteLine($"{c.EditorID,-36} {c.FormKey} refs={refs.Count} lights={kinds.Count(k => k == "LIGH")} other={kinds.Count(k => k == "OTHER")} kit={kit} lt={c.LightingTemplate.FormKey}");
                }
                return 0;
            }
            case "music-types":
            {
                // Dev aid: music types matching --name (regex) in Skyrim.esm, Update.esm and the DLC masters, with their
                // tracks (EditorID, type, file, conditions) — which plugins override which.
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "^MUSCombat"));
                // --plugin adds a built plugin (e.g. out/mod/Daggerfall.esm) after the masters.
                var files = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => Path.Combine(skyrimData, f)).ToList();
                if (opts.TryGetValue("plugin", out var extra)) files.Add(extra);
                using var vanillaSkyrim = SkyrimMod.CreateFromBinaryOverlay(files[0], SkyrimRelease.SkyrimSE);
                foreach (var master in files)
                {
                    using var m = SkyrimMod.CreateFromBinaryOverlay(master, SkyrimRelease.SkyrimSE);
                    var tracks = m.MusicTracks.Concat(vanillaSkyrim.MusicTracks).GroupBy(t => t.FormKey).ToDictionary(g => g.Key, g => g.First());
                    foreach (var t in m.MusicTypes.Where(t => rx.IsMatch(t.EditorID ?? "")))
                    {
                        Console.WriteLine($"{Path.GetFileName(master),-15} {t.EditorID} {t.FormKey} priority {t.Data?.Priority} flags {t.Flags} fade {t.FadeDuration} tracks {t.Tracks?.Count}");
                        foreach (var link in t.Tracks ?? [])
                            Console.WriteLine(tracks.TryGetValue(link.FormKey, out var tr)
                                ? $"    {tr.EditorID} {tr.Type} {tr.TrackFilename} cond [{string.Join("; ", (tr.Conditions ?? []).Select(c => $"{c.Data.GetType().Name} {c.CompareOperator} {(c as IConditionFloatGetter)?.ComparisonValue} flags {c.Flags} run {c.Data.RunOnType} {c.Data.Reference.FormKey}"))}]"
                                : $"    {link.FormKey}");
                    }
                }
                return 0;
            }
            case "nif-uvs":
            {
                // Dev aid: each shape's diffuse texture, vertex count and UV range (--file, --tex filters by texture regex).
                using var nif = new nifly.NifFile();
                nif.Load(opts["file"]);
                var trx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("tex", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var shape in nif.GetShapes())
                {
                    string tex = nif.GetTexturePathByIndex(shape, 0);
                    if (!trx.IsMatch(tex)) continue;
                    var uvs = nif.GetUvsForShape(shape).ToList();
                    var verts = nif.GetVertsForShape(shape).ToList();
                    Console.WriteLine($"{tex}: {uvs.Count} verts, u {uvs.Min(t => t.u):F2}..{uvs.Max(t => t.u):F2}, v {uvs.Min(t => t.v):F2}..{uvs.Max(t => t.v):F2}");
                    for (int i = 0; i < Math.Min(8, uvs.Count); i++)
                        Console.WriteLine($"    ({verts[i].x:F0},{verts[i].y:F0},{verts[i].z:F0}) uv ({uvs[i].u:F2},{uvs[i].v:F2})");
                }
                return 0;
            }
            case "shape":
            {
                // Dev aid: a Skyrim.esm object's mesh bounds as the prop and clutter placers see them (--id hex FormID).
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var shapes = new Skyrim.PropReplacer(skyrim, skyrimData);
                var (min, max, back) = shapes.SkyrimShape(new Mutagen.Bethesda.Plugins.FormKey(Mutagen.Bethesda.Plugins.ModKey.FromFileName("Skyrim.esm"), Convert.ToUInt32(opts["id"], 16)));
                Console.WriteLine($"min {min} max {max} back {back}");
                return 0;
            }
            case "verify-light":
            {
                // Daggerfall's lighting stays in the Daggerfall world: no vanilla weather or climate is overridden; the
                // Iliac Bay's regions and climate use only the plugin's relit copies. Dungeon cells use Daggerfall's
                // ambient and light. Prints one sample weather's colours.
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                int bad = 0;
                int overrides = plugin.Weathers.Count(w => w.FormKey.ModKey != plugin.ModKey) + plugin.Climates.Count(c => c.FormKey.ModKey != plugin.ModKey);
                if (overrides > 0) { bad++; Console.WriteLine($"overrides {overrides} vanilla weathers/climates"); }
                var ours = plugin.Weathers.Select(w => w.FormKey).ToHashSet();
                foreach (var r in plugin.Regions.Where(r => r.EditorID?.StartsWith("DFRegion") == true))
                {
                    var foreign = (r.Weather?.Weathers ?? []).Count(w => !ours.Contains(w.Weather.FormKey));
                    if (foreign > 0) { bad++; Console.WriteLine($"{r.EditorID}: {foreign} weathers not Daggerfall's"); }
                }
                foreach (var ws in plugin.Worldspaces)
                {
                    var climate = plugin.Climates.FirstOrDefault(c => c.FormKey == ws.Climate.FormKey);
                    bool ok = climate is not null && climate.WeatherTypes.All(t => ours.Contains(t.Weather.FormKey));
                    if (!ok) bad++;
                    Console.WriteLine($"worldspace {ws.EditorID}: climate {climate?.EditorID ?? ws.Climate.FormKey.ToString()} {(ok ? "uses only relit weathers" : "NOT Daggerfall's")}");
                }
                if (plugin.Weathers.FirstOrDefault() is { } sample)
                    Console.WriteLine($"sample {sample.EditorID}: sun day {sample.SunlightColor.Day} night {sample.SunlightColor.Night}; ambient day {sample.DirectionalAmbientLightingColors.Day.DirectionalZPlus} night {sample.DirectionalAmbientLightingColors.Night.DirectionalZPlus}");
                foreach (var cell in plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                    Console.WriteLine($"cell {cell.EditorID}: ambient {cell.Lighting?.AmbientColor} inherits {cell.Lighting?.Inherits}; lights: " + string.Join(", ", cell.Temporary.OfType<IPlacedObjectGetter>().Where(p => plugin.Lights.Any(l => l.FormKey == p.Base.FormKey)).GroupBy(p => p.Base.FormKey).Select(g => $"{plugin.Lights.First(l => l.FormKey == g.Key).EditorID} x{g.Count()}")));
                Console.WriteLine($"{plugin.Weathers.Count} Daggerfall weathers, {bad} problems");
                return bad == 0 ? 0 : 1;
            }
            case "ref-info":
            {
                // Dev aid: placed references of the plugin whose EditorID matches --name, with cell and position.
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "^$"));
                // --base: match the base object's EditorID instead (e.g. a dungeon block static).
                var baseRx = opts.TryGetValue("base", out var bp) ? new System.Text.RegularExpressions.Regex(bp) : null;
                var baseIds = plugin.EnumerateMajorRecords().Where(m => baseRx?.IsMatch(m.EditorID ?? "") == true).Select(m => m.FormKey).ToHashSet();
                foreach (var cell in plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                    foreach (var r in cell.Persistent.Concat(cell.Temporary).OfType<IPlacedObjectGetter>().Where(r => baseRx is null ? rx.IsMatch(r.EditorID ?? "") : baseIds.Contains(r.Base.FormKey)))
                        Console.WriteLine($"{r.EditorID} [{r.FormKey}] base {plugin.EnumerateMajorRecords().FirstOrDefault(m => m.FormKey == r.Base.FormKey)?.EditorID ?? r.Base.FormKey.ToString()} in {cell.EditorID} at {r.Placement?.Position} rot {r.Placement?.Rotation}");
                return 0;
            }
            case "nav-compare":
            {
                // Dev aid: compare the real navmesh with a permissive reference build (--ref plugin: tiny agent, high climb
                // and slope, no ledge filter). For each real piece cut off from the main one, follow the reference mesh
                // from the piece towards the main piece and report where the real navmesh stops along the way.
                INavigationMeshDataGetter Load(string path, out IDisposable owner)
                {
                    var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE); owner = m;
                    return m.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).SelectMany(c => c.NavigationMeshes).First().Data!;
                }
                var real = Load(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), out var o1);
                var refm = Load(opts.GetValueOrDefault("ref", "out/ref-nav.esm"), out var o2);
                (System.Numerics.Vector3[][] Tris, int[][] Links) Read(INavigationMeshDataGetter d) =>
                    (d.Triangles.Select(t => new[] { d.Vertices[t.Vertices.X], d.Vertices[t.Vertices.Y], d.Vertices[t.Vertices.Z] }.Select(v => new System.Numerics.Vector3(v.X, v.Y, v.Z)).ToArray()).ToArray(),
                     d.Triangles.Select(t => new int[] { t.EdgeLink_0_1, t.EdgeLink_1_2, t.EdgeLink_2_0 }).ToArray());
                var (rt, rl) = Read(real); var (ft, fl) = Read(refm);
                int[] Pieces(int[][] links, out int count)
                {
                    var comp = new int[links.Length]; Array.Fill(comp, -1); count = 0;
                    for (int i = 0; i < links.Length; i++)
                    {
                        if (comp[i] >= 0) continue;
                        var st = new Stack<int>([i]); comp[i] = count;
                        while (st.Count > 0) foreach (int e in links[st.Pop()]) if (e >= 0 && e < links.Length && comp[e] < 0) { comp[e] = count; st.Push(e); }
                        count++;
                    }
                    return comp;
                }
                var rc = Pieces(rl, out int rn); var fc = Pieces(fl, out int fn);
                int main = Enumerable.Range(0, rn).MaxBy(c => rc.Count(x => x == c));
                System.Numerics.Vector3 Centre(System.Numerics.Vector3[] t) => (t[0] + t[1] + t[2]) / 3;
                // Which real triangle (if any) a point lies on: inside in plan and within 48 in height.
                int RealAt(System.Numerics.Vector3 p)
                {
                    for (int i = 0; i < rt.Length; i++)
                    {
                        var (a, b, c) = (rt[i][0], rt[i][1], rt[i][2]);
                        float den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                        if (MathF.Abs(den) < 1e-6f) continue;
                        float w0 = ((b.Y - c.Y) * (p.X - c.X) + (c.X - b.X) * (p.Y - c.Y)) / den, w1 = ((c.Y - a.Y) * (p.X - c.X) + (a.X - c.X) * (p.Y - c.Y)) / den, w2 = 1 - w0 - w1;
                        if (w0 >= -0.02f && w1 >= -0.02f && w2 >= -0.02f && MathF.Abs(w0 * a.Z + w1 * b.Z + w2 * c.Z - p.Z) < 48) return i;
                    }
                    return -1;
                }
                var refOnReal = ft.Select(t => RealAt(Centre(t))).ToArray();
                Console.WriteLine($"real: {rt.Length} triangles in {rn} pieces; reference: {ft.Length} triangles in {fn} pieces");
                for (int piece = 0; piece < rn; piece++)
                {
                    if (piece == main) continue;
                    // Reference triangles on this piece; breadth-first through the reference mesh to one on the main piece.
                    var starts = Enumerable.Range(0, ft.Length).Where(i => refOnReal[i] >= 0 && rc[refOnReal[i]] == piece).ToList();
                    if (starts.Count == 0) { Console.WriteLine($"piece {piece}: not under the reference mesh"); continue; }
                    var prev = new int[ft.Length]; Array.Fill(prev, -2);
                    var q = new Queue<int>(); foreach (var s0 in starts) { prev[s0] = -1; q.Enqueue(s0); }
                    int goal = -1;
                    while (q.Count > 0 && goal < 0)
                    {
                        int k = q.Dequeue();
                        foreach (int e in fl[k])
                            if (e >= 0 && e < ft.Length && prev[e] == -2)
                            {
                                prev[e] = k;
                                if (refOnReal[e] >= 0 && rc[refOnReal[e]] == main) { goal = e; break; }
                                q.Enqueue(e);
                            }
                    }
                    int size = rc.Count(x => x == piece);
                    if (goal < 0) { Console.WriteLine($"piece {piece} ({size} tris): no reference path to the main piece either (really separate)"); continue; }
                    // Walk back from the goal; report the transitions between covered and uncovered stretches.
                    var path = new List<int>(); for (int k = goal; k >= 0; k = prev[k]) path.Add(k);
                    path.Reverse();
                    var sb2 = new System.Text.StringBuilder();
                    string Label(int k) => refOnReal[k] < 0 ? "none" : rc[refOnReal[k]] == main ? "main" : $"piece {rc[refOnReal[k]]}";
                    string last = Label(path[0]);
                    for (int j = 1; j < path.Count; j++)
                    {
                        string cur = Label(path[j]);
                        if (cur == last) continue;
                        var at = Centre(ft[path[j]]);
                        sb2.Append($" -> {cur} at ({at.X:F0},{at.Y:F0},{at.Z:F0})");
                        last = cur;
                    }
                    Console.WriteLine($"piece {piece} ({size} tris), path of {path.Count}: {Label(path[0])}{sb2}");
                }
                o1.Dispose(); o2.Dispose();
                return 0;
            }
            case "nav-coverage":
            {
                // Dev aid: top-down image of walkable input floors (grey) under the navmesh (each connected piece its own
                // colour, the main piece green). Grey showing through = floor the navmesh doesn't cover.
                float upp = float.Parse(opts.GetValueOrDefault("upp", "10"));   // Skyrim units per pixel
                float zmin = float.Parse(opts.GetValueOrDefault("zmin", "-100000")), zmax = float.Parse(opts.GetValueOrDefault("zmax", "100000"));
                var view = opts.TryGetValue("view", out var vw) ? vw.Split(',').Select(float.Parse).ToArray() : null;   // minX,minY,maxX,maxY
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                var nav = plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).SelectMany(c => c.NavigationMeshes).First().Data!;
                var models = DfModel.LoadAll(arena2);
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                var floors = new List<System.Numerics.Vector3[]>();
                foreach (var (block, placement) in DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []).Blocks)
                {
                    var o = DfDungeon.BlockOrigin(placement);
                    var shift = new System.Numerics.Vector3(o.X, o.Z, o.Y) * Skyrim.Meshes.BlockMeshBuilder.Scale;
                    floors.AddRange(Skyrim.Meshes.BlockMeshBuilder.SolidPolygons(block, models).Where(p => p.Normal.Z > 0.7f).Select(p => p.Points.Select(v => v + shift).ToArray())
                        .Where(f => f.Max(v => v.Z) >= zmin && f.Min(v => v.Z) <= zmax));
                }
                int n = nav.Triangles.Count;
                var comp = new int[n]; Array.Fill(comp, -1); int pieces = 0;
                for (int i = 0; i < n; i++)
                {
                    if (comp[i] >= 0) continue;
                    var st = new Stack<int>([i]); comp[i] = pieces;
                    while (st.Count > 0) { var t = nav.Triangles[st.Pop()]; foreach (int e in new int[] { t.EdgeLink_0_1, t.EdgeLink_1_2, t.EdgeLink_2_0 }) if (e >= 0 && e < n && comp[e] < 0) { comp[e] = pieces; st.Push(e); } }
                    pieces++;
                }
                int main = Enumerable.Range(0, pieces).MaxBy(c => comp.Count(x => x == c));
                var all = floors.SelectMany(f => f).ToList();
                float minX = view?[0] ?? all.Min(v => v.X) - 50, maxX = view?[2] ?? all.Max(v => v.X) + 50, minY = view?[1] ?? all.Min(v => v.Y) - 50, maxY = view?[3] ?? all.Max(v => v.Y) + 50;
                int w = (int)((maxX - minX) / upp), h = (int)((maxY - minY) / upp);
                var rgb = new byte[w * h * 3];
                var depth = new float[w * h]; Array.Fill(depth, float.MinValue);
                void Fill(System.Numerics.Vector3[] tri, byte r, byte g, byte b, bool onTop)
                {
                    var (a, b2, c) = (tri[0], tri[1], tri[2]);
                    int x0 = (int)((Math.Min(a.X, Math.Min(b2.X, c.X)) - minX) / upp), x1 = (int)((Math.Max(a.X, Math.Max(b2.X, c.X)) - minX) / upp);
                    int y0 = (int)((Math.Min(a.Y, Math.Min(b2.Y, c.Y)) - minY) / upp), y1 = (int)((Math.Max(a.Y, Math.Max(b2.Y, c.Y)) - minY) / upp);
                    float den = (b2.Y - c.Y) * (a.X - c.X) + (c.X - b2.X) * (a.Y - c.Y);
                    if (MathF.Abs(den) < 1e-6f) return;
                    for (int py = Math.Max(0, y0); py <= Math.Min(h - 1, y1); py++)
                        for (int px = Math.Max(0, x0); px <= Math.Min(w - 1, x1); px++)
                        {
                            float x = minX + (px + 0.5f) * upp, y = minY + (py + 0.5f) * upp;
                            float w0 = ((b2.Y - c.Y) * (x - c.X) + (c.X - b2.X) * (y - c.Y)) / den, w1 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / den, w2 = 1 - w0 - w1;
                            if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                            float z = w0 * a.Z + w1 * b2.Z + w2 * c.Z;
                            int k = (h - 1 - py) * w + px;   // north up
                            // Floors: highest wins. Navmesh: drawn where it is within 40 of the top floor (or above it).
                            if (!onTop) { if (z <= depth[k]) continue; depth[k] = z; }
                            else if (z < depth[k] - 40) continue;
                            float shade = onTop ? 1 : 0.35f + 0.65f * Math.Clamp((z + 500) / 6000, 0, 1);
                            rgb[k * 3] = (byte)(r * shade); rgb[k * 3 + 1] = (byte)(g * shade); rgb[k * 3 + 2] = (byte)(b * shade);
                        }
                }
                foreach (var f in floors) for (int i = 1; i + 1 < f.Length; i++) Fill([f[0], f[i], f[i + 1]], 200, 200, 200, false);
                for (int i = 0; i < n; i++)
                {
                    var t = nav.Triangles[i];
                    var vs = new[] { nav.Vertices[t.Vertices.X], nav.Vertices[t.Vertices.Y], nav.Vertices[t.Vertices.Z] }.Select(v => new System.Numerics.Vector3(v.X, v.Y, v.Z)).ToArray();
                    if (vs.Average(v => v.Z) < zmin || vs.Average(v => v.Z) > zmax) continue;
                    int c = comp[i];
                    var (r, g, b) = c == main ? (40, 200, 60) : ((byte)(80 + c * 53 % 170), (byte)(60 + c * 97 % 120), (byte)(120 + c * 31 % 130));
                    Fill(vs, (byte)r, (byte)g, (byte)b, true);
                }
                // Doors (white) and actors (yellow) on top.
                var cellRefs = plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).SelectMany(c => c.Persistent.Concat(c.Temporary)).ToList();
                var doorBases = Skyrim.DoorPlacer.BaseDoors.ToHashSet();
                void Mark(Noggog.P3Float p, byte r, byte g, byte b)
                {
                    int cx = (int)((p.X - minX) / upp), cy = h - 1 - (int)((p.Y - minY) / upp);
                    for (int dy = -3; dy <= 3; dy++) for (int dx = -3; dx <= 3; dx++)
                        if (cx + dx >= 0 && cx + dx < w && cy + dy >= 0 && cy + dy < h && (Math.Abs(dx) == 3 || Math.Abs(dy) == 3)) { int k = (cy + dy) * w + cx + dx; rgb[k * 3] = r; rgb[k * 3 + 1] = g; rgb[k * 3 + 2] = b; }
                }
                foreach (var r in cellRefs.OfType<IPlacedObjectGetter>().Where(r => doorBases.Contains(r.Base.FormKey))) Mark(r.Placement!.Position, 255, 255, 255);
                foreach (var r in cellRefs.OfType<IPlacedNpcGetter>()) Mark(r.Placement!.Position, 255, 230, 0);
                string png = opts.GetValueOrDefault("out", "out/nav-coverage.png");
                Util.Png.WriteRgb(png, w, h, rgb);
                Console.WriteLine($"{png}: {w}x{h}, {upp} units/px, origin ({minX:F0},{minY:F0}) bottom-left, {pieces} pieces");
                return 0;
            }
            case "models-at":
            {
                // Dev aid: placed bounds (Skyrim units) of every model within --r of a Skyrim point (--x --y), and why it might be excluded.
                float px = float.Parse(opts["x"]), py = float.Parse(opts["y"]), r = float.Parse(opts.GetValueOrDefault("r", "600"));
                var models = DfModel.LoadAll(arena2);
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                float sc = Skyrim.Meshes.BlockMeshBuilder.Scale;
                foreach (var (block, placement) in DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []).Blocks)
                    foreach (var o in block.Objects.Where(o => o.Type == DfRdbObjectType.Model))
                    {
                        if (!models.TryGetValue(o.ModelId, out var m)) { Console.WriteLine($"{block.Name} model {o.ModelId} NOT LOADED"); continue; }
                        var t = DfDungeon.ObjectTransform(o, DfDungeon.BlockOrigin(placement));
                        var pts = m.Polygons.SelectMany(p => p.Points).Select(v => System.Numerics.Vector3.Transform(v, t)).Select(v => new System.Numerics.Vector3(v.X, v.Z, v.Y) * sc).ToList();
                        if (pts.Count == 0) continue;
                        var lo = pts.Aggregate(System.Numerics.Vector3.Min); var hi = pts.Aggregate(System.Numerics.Vector3.Max);
                        if (px < lo.X - r || px > hi.X + r || py < lo.Y - r || py > hi.Y + r) continue;
                        string why = Skyrim.Meshes.BlockMeshBuilder.IsDoor(o) ? "door" : o.HasAction ? "action" : Skyrim.PropReplacer.IsReplaced(o.ModelId) ? "prop" : o.ModelId == Skyrim.Meshes.BlockMeshBuilder.ExitDoorModel ? "exit" : "";
                        Console.WriteLine($"{block.Name} {o.ModelId} {o.Tag} x {lo.X:F0}..{hi.X:F0} y {lo.Y:F0}..{hi.Y:F0} z {lo.Z:F0}..{hi.Z:F0} rot ({o.Rotation.X:F0},{o.Rotation.Y:F0},{o.Rotation.Z:F0}) {why}");
                    }
                return 0;
            }
            case "steps":
            {
                // Dev aid: floor-to-floor steps taller than the navmesh climb (32) but short enough to be stairs (<= 80),
                // where two floors share an edge in plan view, with the models involved and the riser between them.
                var models = DfModel.LoadAll(arena2);
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                var dungeon = DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []);
                float sc = Skyrim.Meshes.BlockMeshBuilder.Scale;
                var polys = dungeon.Polygons(models).Select(p => (Pts: p.Points.Select(v => new System.Numerics.Vector3(v.X, v.Z, v.Y) * sc).ToArray(), N: System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(p.Normal.X, p.Normal.Z, p.Normal.Y)), p.ModelId)).ToList();
                var floors = polys.Where(p => p.N.Z > 0.7f).ToList();
                var walls = polys.Where(p => MathF.Abs(p.N.Z) < 0.3f).ToList();
                var seen = new Dictionary<string, int>();
                foreach (var a in floors)
                    for (int i = 0; i < a.Pts.Length; i++)
                    {
                        var (p0, p1) = (a.Pts[i], a.Pts[(i + 1) % a.Pts.Length]);
                        var mid = (p0 + p1) / 2;
                        foreach (var b in floors)
                        {
                            float dz = b.Pts.Average(v => v.Z) - a.Pts.Average(v => v.Z);
                            if (dz <= 32 || dz > 80) continue;
                            // b has an edge over a's edge (same line in plan view, overlapping)?
                            for (int j = 0; j < b.Pts.Length; j++)
                            {
                                var (q0, q1) = (b.Pts[j], b.Pts[(j + 1) % b.Pts.Length]);
                                var qm = (q0 + q1) / 2;
                                var dir = System.Numerics.Vector3.Normalize(new System.Numerics.Vector3(p1.X - p0.X, p1.Y - p0.Y, 0));
                                var off = new System.Numerics.Vector3(qm.X - mid.X, qm.Y - mid.Y, 0);
                                if ((off - dir * System.Numerics.Vector3.Dot(off, dir)).Length() > 4) continue;   // not on the same line
                                if (MathF.Abs(System.Numerics.Vector3.Dot(off, dir)) > System.Numerics.Vector3.Distance(p0, p1) / 2 + System.Numerics.Vector3.Distance(q0, q1) / 2 - 8) continue;
                                // The riser: a wall polygon along this edge.
                                var riser = walls.FirstOrDefault(w => w.Pts.Any(v => MathF.Abs(v.Z - a.Pts[0].Z) < 3) && w.Pts.Any(v => MathF.Abs(v.Z - b.Pts[0].Z) < 3)
                                    && w.Pts.Min(v => new System.Numerics.Vector2(v.X - mid.X, v.Y - mid.Y).Length()) < 200);
                                string riserInfo = riser.Pts is null ? "no riser" : $"riser model {riser.ModelId} rise {riser.Pts.Max(v => v.Z) - riser.Pts.Min(v => v.Z):F0} normal ({riser.N.X:F2},{riser.N.Y:F2},{riser.N.Z:F2})";
                                string key = $"lower {a.ModelId} upper {b.ModelId} step {dz:F0} {riserInfo}";
                                if (seen.TryAdd(key, 1)) Console.WriteLine($"{key} at ({mid.X:F0},{mid.Y:F0},{mid.Z:F0})"); else seen[key]++;
                            }
                        }
                    }
                Console.WriteLine($"{seen.Count} kinds of step, {seen.Values.Sum()} edges");
                return 0;
            }
            case "nav-input":
            {
                // Dev aid: navmesh input polygons (block collision, Skyrim units) whose bounds cover a point (--x --y).
                float px = float.Parse(opts["x"]), py = float.Parse(opts["y"]);
                var models = DfModel.LoadAll(arena2);
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                foreach (var (block, placement) in DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []).Blocks)
                {
                    var o = DfDungeon.BlockOrigin(placement);
                    var shift = new System.Numerics.Vector3(o.X, o.Z, o.Y) * Skyrim.Meshes.BlockMeshBuilder.Scale;
                    foreach (var (pts, n) in Skyrim.Meshes.BlockMeshBuilder.SolidPolygons(block, models))
                    {
                        var w = pts.Select(v => v + shift).ToArray();
                        if (px < w.Min(v => v.X) - 1 || px > w.Max(v => v.X) + 1 || py < w.Min(v => v.Y) - 1 || py > w.Max(v => v.Y) + 1) continue;
                        Console.WriteLine($"{block.Name} normal ({n.X:F2},{n.Y:F2},{n.Z:F2}) z {w.Min(v => v.Z):F0}..{w.Max(v => v.Z):F0} x {w.Min(v => v.X):F0}..{w.Max(v => v.X):F0} y {w.Min(v => v.Y):F0}..{w.Max(v => v.Y):F0}");
                    }
                }
                return 0;
            }
            case "nav-probe":
            {
                // Dev aid: navmesh triangles near a point (--at x,y,z Skyrim units, --r radius), with their heights.
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                if (opts.ContainsKey("gaps"))
                {
                    // For each piece cut off from the largest, where it comes closest to the largest (and the height step there).
                    var nav = plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).SelectMany(c => c.NavigationMeshes).First().Data!;
                    int n = nav.Triangles.Count;
                    var comp = new int[n]; Array.Fill(comp, -1);
                    int comps = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (comp[i] >= 0) continue;
                        var st = new Stack<int>([i]); comp[i] = comps;
                        while (st.Count > 0)
                        {
                            var t = nav.Triangles[st.Pop()];
                            foreach (int e in new int[] { t.EdgeLink_0_1, t.EdgeLink_1_2, t.EdgeLink_2_0 })
                                if (e >= 0 && e < n && comp[e] < 0) { comp[e] = comps; st.Push(e); }
                        }
                        comps++;
                    }
                    var sizes = Enumerable.Range(0, comps).Select(c => comp.Count(x => x == c)).ToArray();
                    int main = Array.IndexOf(sizes, sizes.Max());
                    var verts = Enumerable.Range(0, n).Select(i => { var t = nav.Triangles[i]; return new[] { nav.Vertices[t.Vertices.X], nav.Vertices[t.Vertices.Y], nav.Vertices[t.Vertices.Z] }; }).ToArray();
                    if (opts.TryGetValue("piece", out var pieceArg))
                    {
                        // Where this piece's open edges have walkable floor just beyond them (within 80 up or down):
                        // the places the navmesh should have continued.
                        int want = int.Parse(pieceArg);
                        var models = DfModel.LoadAll(arena2);
                        var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                        var floorPolys = new List<System.Numerics.Vector3[]>();
                        foreach (var (block, placement) in DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []).Blocks)
                        {
                            var o = DfDungeon.BlockOrigin(placement);
                            var shift = new System.Numerics.Vector3(o.X, o.Z, o.Y) * Skyrim.Meshes.BlockMeshBuilder.Scale;
                            floorPolys.AddRange(Skyrim.Meshes.BlockMeshBuilder.SolidPolygons(block, models).Where(q => q.Normal.Z > 0.7f).Select(q => q.Points.Select(v => v + shift).ToArray()));
                        }
                        float? Floor(float x, float y, float near)
                        {
                            float? best = null;
                            foreach (var pts in floorPolys)
                                for (int i = 1; i + 1 < pts.Length; i++)
                                {
                                    var (a0, b0, c0) = (pts[0], pts[i], pts[i + 1]);
                                    float den = (b0.Y - c0.Y) * (a0.X - c0.X) + (c0.X - b0.X) * (a0.Y - c0.Y);
                                    if (MathF.Abs(den) < 1e-6f) continue;
                                    float w0 = ((b0.Y - c0.Y) * (x - c0.X) + (c0.X - b0.X) * (y - c0.Y)) / den, w1 = ((c0.Y - a0.Y) * (x - c0.X) + (a0.X - c0.X) * (y - c0.Y)) / den, w2 = 1 - w0 - w1;
                                    if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                                    float z = w0 * a0.Z + w1 * b0.Z + w2 * c0.Z;
                                    if (MathF.Abs(z - near) <= 80 && (best is null || MathF.Abs(z - near) < MathF.Abs(best.Value - near))) best = z;
                                }
                            return best;
                        }
                        for (int i = 0; i < n; i++)
                        {
                            if (comp[i] != want) continue;
                            var t = nav.Triangles[i];
                            var vs = verts[i];
                            var links = new[] { t.EdgeLink_0_1, t.EdgeLink_1_2, t.EdgeLink_2_0 };
                            var cx = vs.Average(v => v.X); var cy = vs.Average(v => v.Y);
                            for (int e = 0; e < 3; e++)
                            {
                                if (links[e] >= 0) continue;
                                var (a0, b0) = (vs[e], vs[(e + 1) % 3]);
                                float mx = (a0.X + b0.X) / 2, my = (a0.Y + b0.Y) / 2, mz = (a0.Z + b0.Z) / 2;
                                float ox = mx - cx, oy = my - cy, ol = MathF.Sqrt(ox * ox + oy * oy);
                                if (ol < 1e-3f) continue;
                                // Beyond the agent margin (24) the floor should still be there if the edge is a false stop.
                                float px = mx + ox / ol * 40, py = my + oy / ol * 40;
                                if (Floor(px, py, mz) is { } fz)
                                    Console.WriteLine($"  open edge at ({mx:F0},{my:F0},{mz:F0}): floor beyond at {fz:F0} (step {fz - mz:F0})");
                            }
                        }
                        return 0;
                    }
                    for (int c = 0; c < comps; c++)
                    {
                        if (c == main) continue;
                        float best = float.MaxValue; (Noggog.P3Float A, Noggog.P3Float B) pair = default;
                        foreach (int i in Enumerable.Range(0, n).Where(i => comp[i] == c))
                            foreach (int j in Enumerable.Range(0, n).Where(j => comp[j] == main))
                                foreach (var a in verts[i]) foreach (var b in verts[j])
                                {
                                    float d = (a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y);
                                    if (d < best) { best = d; pair = (a, b); }
                                }
                        Console.WriteLine($"piece {c} ({sizes[c]} tris): closest to main at {pair.A} vs {pair.B}: {MathF.Sqrt(best):F0} apart, height step {pair.A.Z - pair.B.Z:F0}");
                    }
                    return 0;
                }
                var at = opts["at"].Split(',').Select(float.Parse).ToArray();
                float rad = float.Parse(opts.GetValueOrDefault("r", "300"));
                if (opts.ContainsKey("refs"))
                {
                    // Placed references near the point instead of triangles.
                    var names = plugin.EnumerateMajorRecords().Where(r => r.EditorID is not null).ToDictionary(r => r.FormKey, r => r.EditorID!);
                    using var sky = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                    foreach (var r in sky.EnumerateMajorRecords().Where(r => r.EditorID is not null)) names.TryAdd(r.FormKey, r.EditorID!);
                    foreach (var r in plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).SelectMany(c => c.Persistent.Concat(c.Temporary)))
                    {
                        var (pos, bas) = r switch { IPlacedObjectGetter o => (o.Placement?.Position, o.Base.FormKey), IPlacedNpcGetter npc => (npc.Placement?.Position, npc.Base.FormKey), _ => (null, FormKey.Null) };
                        if (pos is not { } q) continue;
                        if (MathF.Sqrt((q.X - at[0]) * (q.X - at[0]) + (q.Y - at[1]) * (q.Y - at[1])) < rad && MathF.Abs(q.Z - at[2]) < 400)
                            Console.WriteLine($"  {names.GetValueOrDefault(bas, bas.ToString()),-40} at {q} scale {(r as IPlacedObjectGetter)?.Scale}");
                    }
                    return 0;
                }
                foreach (var cell in plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                    foreach (var nav in cell.NavigationMeshes.Select(n => n.Data!))
                        for (int i = 0; i < nav.Triangles.Count; i++)
                        {
                            var t = nav.Triangles[i];
                            var vs = new[] { nav.Vertices[t.Vertices.X], nav.Vertices[t.Vertices.Y], nav.Vertices[t.Vertices.Z] };
                            float cx = vs.Average(v => v.X), cy = vs.Average(v => v.Y), cz = vs.Average(v => v.Z);
                            if (MathF.Sqrt((cx - at[0]) * (cx - at[0]) + (cy - at[1]) * (cy - at[1])) < rad && MathF.Abs(cz - at[2]) < 200)
                                Console.WriteLine($"  tri {i} centre ({cx:F0},{cy:F0},{cz:F0}) neighbours {t.EdgeLink_0_1},{t.EdgeLink_1_2},{t.EdgeLink_2_0}");
                        }
                return 0;
            }
            case "door-floors":
            {
                // Dev aid: is there floor under each Daggerfall door, in the block mesh (doors excluded) and in the door model itself?
                var models = DfModel.LoadAll(arena2);
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts.GetValueOrDefault("name", "Privateer's Hold") && l.DungeonBlocks.Count > 0);
                var dungeon = DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []);
                var floors = new List<System.Numerics.Vector3[]>();
                foreach (var (block, placement) in dungeon.Blocks)
                {
                    var o = DfDungeon.BlockOrigin(placement);
                    var shift = new System.Numerics.Vector3(o.X, o.Z, o.Y) * Skyrim.Meshes.BlockMeshBuilder.Scale;
                    floors.AddRange(Skyrim.Meshes.BlockMeshBuilder.SolidPolygons(block, models).Where(p => p.Normal.Z > 0.7f).Select(p => p.Points.Select(v => v + shift).ToArray()));
                }
                bool FloorAt(float x, float y, float z) => floors.Any(pts => Enumerable.Range(1, pts.Length - 2).Any(i =>
                {
                    var (a, b, c) = (pts[0], pts[i], pts[i + 1]);
                    float den = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                    if (MathF.Abs(den) < 1e-6f) return false;
                    float w0 = ((b.Y - c.Y) * (x - c.X) + (c.X - b.X) * (y - c.Y)) / den, w1 = ((c.Y - a.Y) * (x - c.X) + (a.X - c.X) * (y - c.Y)) / den, w2 = 1 - w0 - w1;
                    return w0 >= 0 && w1 >= 0 && w2 >= 0 && MathF.Abs(w0 * a.Z + w1 * b.Z + w2 * c.Z - z) < 48;
                }));
                int missing = 0, total = 0;
                foreach (var (block, placement) in dungeon.Blocks)
                    foreach (var o in block.Objects.Where(Skyrim.Meshes.BlockMeshBuilder.IsDoor))
                    {
                        total++;
                        var m = DfDungeon.ObjectTransform(o, DfDungeon.BlockOrigin(placement));
                        var model = models[o.ModelId];
                        var c = System.Numerics.Vector3.Transform(new System.Numerics.Vector3((model.Min.X + model.Max.X) / 2, model.Min.Y, (model.Min.Z + model.Max.Z) / 2), m);
                        var s = new System.Numerics.Vector3(c.X, c.Z, c.Y) * Skyrim.Meshes.BlockMeshBuilder.Scale;
                        bool floor = FloorAt(s.X, s.Y, s.Z);
                        int modelFloorPolys = model.Polygons.Count(p => p.Normal.Y > 0.7f);
                        if (!floor) missing++;
                        Console.WriteLine($"{block.Name} door {o.ModelId} {o.Tag} at ({s.X:F0},{s.Y:F0},{s.Z:F0}) size {model.Max - model.Min}: floor under it {floor}, model's own up-facing polys {modelFloorPolys}");
                    }
                Console.WriteLine($"{missing}/{total} doors without floor under them");
                return 0;
            }
            case "verify-creatures":
            {
                // Daggerfall creatures: every actor and encounter list resolves; every placed actor stands on the navmesh.
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                var masters = plugin.ModHeader.MasterReferences.Select(m => SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, m.Master.FileName), SkyrimRelease.SkyrimSE)).ToList();
                var npcs = masters.Append(plugin).SelectMany(m => m.Npcs).GroupBy(n => n.FormKey).ToDictionary(g => g.Key, g => g.Last());
                var lists = masters.Append(plugin).SelectMany(m => m.LeveledNpcs).GroupBy(n => n.FormKey).ToDictionary(g => g.Key, g => g.Last());
                var races = masters.SelectMany(m => m.Races).GroupBy(r => r.FormKey).ToDictionary(g => g.Key, g => g.Last().EditorID);
                int bad = 0;
                foreach (var n in plugin.Npcs.OrderBy(n => n.EditorID))
                {
                    string level = n.Configuration.Level is INpcLevelGetter l ? $"L{l.Level}" : "leveled";
                    string body = n.Template.FormKey.IsNull ? $"race {races.GetValueOrDefault(n.Race.FormKey, "MISSING")}" : $"template {(lists.TryGetValue(n.Template.FormKey, out var t) ? t.EditorID : npcs.TryGetValue(n.Template.FormKey, out var tn) ? tn.EditorID : "MISSING")}";
                    if (body.Contains("MISSING")) bad++;
                    Console.WriteLine($"  {n.EditorID,-36} \"{n.Name}\" {level,-7} {body} aggression {n.AIData?.Aggression}");
                }
                foreach (var l in plugin.LeveledNpcs)
                {
                    int unresolved = (l.Entries ?? []).Count(e => e.Data is not { } d || !(npcs.ContainsKey(d.Reference.FormKey) || lists.ContainsKey(d.Reference.FormKey)));
                    bad += unresolved;
                    Console.WriteLine($"  {l.EditorID,-36} {l.Entries?.Count} entries, levels {string.Join(",", (l.Entries ?? []).Select(e => e.Data!.Level))}{(unresolved > 0 ? $" UNRESOLVED {unresolved}" : "")}");
                }
                foreach (var cell in plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                {
                    var actors = cell.Persistent.Concat(cell.Temporary).OfType<IPlacedNpcGetter>().ToList();
                    if (actors.Count == 0) continue;
                    var nav = cell.NavigationMeshes.FirstOrDefault()?.Data;
                    // Connected pieces of the navmesh, to count actors that can reach the main one (where the player starts).
                    var comp = new int[nav?.Triangles.Count ?? 0]; Array.Fill(comp, -1);
                    int pieces = 0;
                    for (int i = 0; nav is not null && i < comp.Length; i++)
                    {
                        if (comp[i] >= 0) continue;
                        var st = new Stack<int>([i]); comp[i] = pieces;
                        while (st.Count > 0)
                        {
                            var t = nav.Triangles[st.Pop()];
                            foreach (int e in new int[] { t.EdgeLink_0_1, t.EdgeLink_1_2, t.EdgeLink_2_0 })
                                if (e >= 0 && e < comp.Length && comp[e] < 0) { comp[e] = pieces; st.Push(e); }
                        }
                        pieces++;
                    }
                    int mainPiece = pieces == 0 ? -1 : Enumerable.Range(0, pieces).MaxBy(c => comp.Count(x => x == c));
                    int onNav = 0, onMain = 0;
                    foreach (var a in actors)
                    {
                        var p = a.Placement!.Position;
                        int tri = -1;
                        bool on = nav is not null && nav.Triangles.Select((t, i) => (t, i)).Any(ti =>
                        {
                            var (t, i) = ti; tri = i;
                            var (v0, v1, v2) = (nav.Vertices[t.Vertices.X], nav.Vertices[t.Vertices.Y], nav.Vertices[t.Vertices.Z]);
                            float den = (v1.Y - v2.Y) * (v0.X - v2.X) + (v2.X - v1.X) * (v0.Y - v2.Y);
                            if (MathF.Abs(den) < 1e-6f) return false;
                            float w0 = ((v1.Y - v2.Y) * (p.X - v2.X) + (v2.X - v1.X) * (p.Y - v2.Y)) / den, w1 = ((v2.Y - v0.Y) * (p.X - v2.X) + (v0.X - v2.X) * (p.Y - v2.Y)) / den, w2 = 1 - w0 - w1;
                            return w0 >= -0.01f && w1 >= -0.01f && w2 >= -0.01f && MathF.Abs(w0 * v0.Z + w1 * v1.Z + w2 * v2.Z - p.Z) < 64;
                        });
                        if (on) { onNav++; if (comp[tri] == mainPiece) onMain++; }
                        else Console.WriteLine($"  off navmesh: {npcs.GetValueOrDefault(a.Base.FormKey)?.EditorID} at {p}");
                        if (!npcs.ContainsKey(a.Base.FormKey)) { bad++; Console.WriteLine($"  UNRESOLVED actor base {a.Base.FormKey}"); }
                    }
                    Console.WriteLine($"{cell.EditorID}: {actors.Count} actors, {onNav} on the navmesh, {onMain} on its main piece (of {pieces}): " + string.Join(", ", actors.GroupBy(a => npcs.GetValueOrDefault(a.Base.FormKey)?.EditorID).Select(g => $"{g.Key} x{g.Count()}")));
                }
                masters.ForEach(m => m.Dispose());
                Console.WriteLine($"{plugin.Npcs.Count} actors, {plugin.LeveledNpcs.Count} leveled lists, {bad} problems");
                return bad == 0 ? 0 : 1;
            }
            case "verify-navmesh":
            {
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                bool ok = true;
                foreach (var cell in plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                {
                    // A new game begins at the enter marker (Daggerfall Unity's preference), else the start marker.
                    var start = (cell.Persistent.OfType<IPlacedObjectGetter>().FirstOrDefault(r => r.EditorID?.StartsWith("DFEnter") == true)
                        ?? cell.Persistent.OfType<IPlacedObjectGetter>().FirstOrDefault(r => r.EditorID?.StartsWith("DFStart") == true))?.Placement?.Position;
                    foreach (var navm in cell.NavigationMeshes)
                        ok &= Skyrim.Navmesh.NavmeshVerifier.Verify(navm, $"out/navmesh-{cell.EditorID}.png", start);
                }
                foreach (var navi in plugin.NavigationMeshInfoMaps)
                    Console.WriteLine($"NAVI {navi.FormKey}: {navi.MapInfos.Count} entries, doors {string.Join(",", navi.MapInfos.Select(i => i.LinkedDoors.Count))}");
                return ok ? 0 : 2;
            }
            case "verify-music":
            {
                // Every music track's file must exist (records name .wav like vanilla; the files are .xwm).
                string dir = opts.GetValueOrDefault("dir", "out/mod");
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, "Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                int missing = 0, singles = 0;
                foreach (var t in plugin.MusicTracks.Where(t => t.Type == MusicTrack.TypeEnum.SingleTrack))
                {
                    singles++;
                    var rel = t.TrackFilename!.GivenPath.TrimStart('\\');
                    if (rel.StartsWith(@"Data\", StringComparison.OrdinalIgnoreCase)) rel = rel[5..];
                    if (!File.Exists(Path.Combine(dir, Path.ChangeExtension(rel, ".xwm")))) { missing++; Console.WriteLine($"  missing {rel}"); }
                }
                foreach (var m in plugin.MusicTypes)
                    Console.WriteLine($"{m.EditorID}: {m.Tracks?.Count ?? 0} entries");
                var ws = plugin.Worldspaces.First();
                Console.WriteLine($"worldspace music: {ws.Music.FormKey}; regions {plugin.Regions.Count}: {string.Join(", ", plugin.Regions.Select(r => $"{r.EditorID}({r.RegionAreas.Count} areas, {r.Weather?.Weathers?.Count ?? 0} weathers, {r.Sounds?.Sounds?.Count ?? 0} sounds)"))}");
                var cells = ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).ToList();
                Console.WriteLine($"cells with a region: {cells.Count(c => c.Regions?.Count > 0)}/{cells.Count}");
                foreach (var c in plugin.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                    Console.WriteLine($"interior {c.EditorID}: music {c.Music.FormKey} acoustic {c.AcousticSpace.FormKey} imagespace {c.ImageSpace.FormKey}");
                Console.WriteLine($"{singles} song tracks, {missing} missing files");
                return missing == 0 ? 0 : 2;
            }
            case "render-music":
            {
                var lengths = DfMusic.RenderAll(arena2, MusicTools(skyrimData), Path.Combine(opts.GetValueOrDefault("out", "out/mod"), "Music", "TamrielMerge"), Path.GetFullPath("out/music-work"), uncut: !opts.ContainsKey("no-uncut"), only: opts.GetValueOrDefault("only"));
                Console.WriteLine($"{lengths.Count} songs, total {lengths.Values.Sum() / 60:F0} minutes");
                return 0;
            }
            case "probe-music":
            {
                // Dev aid: how a vanilla music type and its tracks are built.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var tracks = skyrim.MusicTracks.ToDictionary(t => t.FormKey);
                string D(object? o, int depth = 0)
                {
                    if (o is null) return "null";
                    var t = o.GetType();
                    if (depth > 3 || o is string || t.IsPrimitive || o is Enum || t.Name.Contains("FormLink") || t.Name.Contains("MemorySlice")) return o.ToString() ?? "";
                    if (o is System.Collections.IEnumerable e) { var items = e.Cast<object>().ToList(); return $"[{items.Count}: {string.Join(" | ", items.Take(4).Select(x => D(x, depth + 1)))}]"; }
                    var props = t.GetProperties().Where(p => p.GetIndexParameters().Length == 0 && p.Name is not ("StaticRegistration" or "Registration" or "FormKey" or "VersionControl" or "FormVersion" or "Version2" or "IsCompressed" or "IsDeleted" or "MajorRecordFlagsRaw" or "SkyrimMajorRecordFlags"));
                    return "{" + string.Join(", ", props.Select(p => { object? v; try { v = p.GetValue(o); } catch { v = "?"; } return $"{p.Name}={D(v, depth + 1)}"; })) + "}";
                }
                var musc = skyrim.MusicTypes.First(m => m.EditorID == opts.GetValueOrDefault("musc", "MUSExploreTundra"));
                Console.WriteLine($"MUSC {D(musc)}");
                foreach (var tk in (musc.Tracks ?? []).Select(l => tracks[l.FormKey]))
                {
                    Console.WriteLine($"  MUST {D(tk)}");
                    foreach (var sub in (tk.Tracks ?? []).Take(3).Select(l => tracks.GetValueOrDefault(l.FormKey)))
                        Console.WriteLine($"     sub {D(sub)}");
                }
                return 0;
            }
            case "probe-atmosphere":
            {
                // Dev aid: vanilla music types, acoustic spaces, image spaces, and Tamriel's regions with their
                // weather, sound and music data.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var names = skyrim.EnumerateMajorRecords().Where(r => r.EditorID is not null).ToDictionary(r => r.FormKey, r => r.EditorID!);
                string N(Mutagen.Bethesda.Plugins.FormKey k) => names.GetValueOrDefault(k, k.ToString());
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "Dungeon|Explore|Town|Tavern|Imperial|Fort|Cave|Default"), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                Console.WriteLine("MUSC: " + string.Join(" ", skyrim.MusicTypes.Where(m => rx.IsMatch(m.EditorID ?? "")).Select(m => $"{m.EditorID}[{m.FormKey.ID:X}]")));
                foreach (var a in skyrim.AcousticSpaces.Where(m => rx.IsMatch(m.EditorID ?? "")))
                    Console.WriteLine($"ASPC {a.EditorID}[{a.FormKey.ID:X}] ambient={N(a.AmbientSound.FormKey)} region={N(a.UseSoundFromRegion.FormKey)} reverb={N(a.EnvironmentType.FormKey)}");
                Console.WriteLine("IMGS: " + string.Join(" ", skyrim.ImageSpaces.Where(m => rx.IsMatch(m.EditorID ?? "")).Select(m => $"{m.EditorID}[{m.FormKey.ID:X}]")));
                Console.WriteLine("CLMT: " + string.Join(" ", skyrim.Climates.Select(m => $"{m.EditorID}[{m.FormKey.ID:X}] weathers={m.WeatherTypes?.Count}")));
                var tamriel = skyrim.Worldspaces.First(w => w.EditorID == "Tamriel").FormKey;
                foreach (var r in skyrim.Regions.Where(r => r.Worldspace.FormKey == tamriel && (r.Weather is not null || r.Sounds is not null)))
                    Console.WriteLine($"REGN {r.EditorID}[{r.FormKey.ID:X}] areas={r.RegionAreas.Count} weather=[{string.Join(", ", r.Weather?.Weathers?.Select(w => $"{N(w.Weather.FormKey)}:{w.Chance}") ?? [])}] music={N(r.Sounds?.Music.FormKey ?? default)} sounds={r.Sounds?.Sounds?.Count ?? 0}");
                return 0;
            }
            case "probe-navi":
            {
                // Do working plugins override the navmesh info map with only their own entries?
                foreach (var file in (opts.GetValueOrDefault("plugins", "Morrowind.esm;Arktwend_English.esm;Dawnguard.esm;Update.esm")).Split(';'))
                {
                    using var plugin = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, file), SkyrimRelease.SkyrimSE);
                    foreach (var navi in plugin.NavigationMeshInfoMaps)
                    {
                        var owners = navi.MapInfos.GroupBy(i => i.NavigationMesh.FormKey.ModKey.FileName.String).Select(g => $"{g.Key}x{g.Count()}");
                        Console.WriteLine($"{file}: NAVI {navi.FormKey} entries={navi.MapInfos.Count} by navmesh owner: {string.Join(", ", owners)}");
                    }
                }
                return 0;
            }
            case "probe-triggers":
            {
                // Dev aid: vanilla trigger-box references (activator base + primitive) and their fields.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var activators = skyrim.Activators.ToDictionary(a => a.FormKey);
                int shown = 0;
                foreach (var cell in skyrim.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                    foreach (var r in cell.Temporary.Concat(cell.Persistent).OfType<IPlacedObjectGetter>().Where(r => r.Primitive is not null && activators.ContainsKey(r.Base.FormKey)))
                    {
                        var a = activators[r.Base.FormKey];
                        Console.WriteLine($"{cell.EditorID}: base {a.EditorID} model={a.Model?.File.DataRelativePath.Path ?? "none"} scripts=[{string.Join(",", a.VirtualMachineAdapter?.Scripts.Select(s => s.Name) ?? [])}] refScripts=[{string.Join(",", r.VirtualMachineAdapter?.Scripts.Select(s => s.Name) ?? [])}]");
                        Console.WriteLine($"   primitive type={r.Primitive!.Type} bounds={r.Primitive.Bounds} color={r.Primitive.Color} unknown={r.Primitive.Unknown} collisionLayer={r.CollisionLayer} flags={r.SkyrimMajorRecordFlags} scale={r.Scale}");
                        if (++shown >= 6) return 0;
                    }
                return 0;
            }
            case "probe-navmesh":
            {
                // Dev aid: Mutagen's navmesh types and one vanilla interior navmesh + its NAVI entry.
                void Props(Type t) => Console.WriteLine($"{t.Name}: " + string.Join(", ", t.GetProperties().Where(p => p.DeclaringType == t).Select(p => $"{p.Name}:{(Nullable.GetUnderlyingType(p.PropertyType) ?? p.PropertyType).Name}")));
                foreach (var t in typeof(NavigationMesh).Assembly.GetExportedTypes().Where(t => t.IsClass && !t.IsAbstract && !t.Name.Contains("Binary") && !t.Name.Contains("Mask") && !t.Name.Contains("Common") && !t.Name.Contains("Setter") && (t.Name.StartsWith("NavigationMesh") || t.Name.StartsWith("Navmesh") || t.Name.StartsWith("ANavigationMesh") || t.Name.StartsWith("NavigationDoor") || t.Name.StartsWith("EdgeLink") || t.Name.StartsWith("DoorTriangle") || t.Name.StartsWith("IslandData") || t.Name == "PreferredPathing"))) Props(t);
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var cell = skyrim.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).First(c => c.EditorID == opts.GetValueOrDefault("cell", "RiverwoodSleepingGiantInn"));
                var nav = cell.NavigationMeshes.First();
                string D(object? o, int depth = 0)
                {
                    if (o is null) return "null";
                    var t = o.GetType();
                    if (depth > 3 || o is string || t.IsPrimitive || o is Enum || t.Name.Contains("FormLink") || t.Name.StartsWith("P2") || t.Name.StartsWith("P3") || t.Name.Contains("MemorySlice")) return o.ToString() ?? "";
                    if (o is System.Collections.IEnumerable e) { var items = e.Cast<object>().ToList(); return $"[{items.Count}: {string.Join(" | ", items.Take(3).Select(x => D(x, depth + 1)))}]"; }
                    var props = t.GetProperties().Where(p => p.GetIndexParameters().Length == 0 && p.Name is not ("StaticRegistration" or "Registration"));
                    return "{" + string.Join(", ", props.Select(p => { object? v; try { v = p.GetValue(o); } catch { v = "?"; } return $"{p.Name}={D(v, depth + 1)}"; })) + "}";
                }
                Console.WriteLine($"NAVM {nav.FormKey} in {cell.EditorID}: {D(nav)}");
                var navi = skyrim.NavigationMeshInfoMaps.First();
                var info = navi.MapInfos.First(i => i.NavigationMesh.FormKey == nav.FormKey);
                Console.WriteLine($"NAVI {navi.FormKey} entries={navi.MapInfos.Count}: {D(navi, 2)}");
                Console.WriteLine($"NAVI entry: {D(info)}");
                var grid = nav.Data!.NavmeshGrid.ToArray();
                int pos = 0, cells = 0, refs = 0;
                var firstCells = new List<string>();
                while (pos + 4 <= grid.Length)
                {
                    int count = BitConverter.ToInt32(grid, pos); pos += 4;
                    var tris = Enumerable.Range(0, count).Select(i => BitConverter.ToInt16(grid, pos + i * 2)).ToList(); pos += count * 2;
                    if (firstCells.Count < 4) firstCells.Add($"{count}:[{string.Join(",", tris.Take(6))}]");
                    cells++; refs += count;
                }
                Console.WriteLine($"grid bytes={grid.Length} parsed cells={cells} (divisor^2={nav.Data.NavmeshGridDivisor * nav.Data.NavmeshGridDivisor}) refs={refs} leftover={grid.Length - pos} first {string.Join(" ", firstCells)}");
                foreach (var n in skyrim.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).SelectMany(c => c.NavigationMeshes).Where(n => n.Data is not null).Take(4000).Where((n, i) => i % 200 == 0))
                {
                    var nd = n.Data!;
                    float dx = nd.Max.X - nd.Min.X, dy = nd.Max.Y - nd.Min.Y;
                    Console.WriteLine($"  sample dims={dx:F0}x{dy:F0} tris={nd.Triangles.Count} divisor={nd.NavmeshGridDivisor} maxDist=({nd.MaxDistanceX:F1},{nd.MaxDistanceY:F1}) => dx/div={dx / nd.NavmeshGridDivisor:F1}");
                }
                {
                    // Which grid cell order matches vanilla: index = y*div + x, or x*div + y?
                    var nd = nav.Data!; int div = (int)nd.NavmeshGridDivisor;
                    var lists = new List<HashSet<int>>(); int gp = 0;
                    while (gp + 4 <= grid.Length) { int c = BitConverter.ToInt32(grid, gp); gp += 4; lists.Add(Enumerable.Range(0, c).Select(i => (int)BitConverter.ToInt16(grid, gp + i * 2)).ToHashSet()); gp += c * 2; }
                    foreach (bool yMajor in new[] { true, false })
                    {
                        int hits = 0, total = 0;
                        for (int ti = 0; ti < nd.Triangles.Count; ti++)
                        {
                            var tv = nd.Triangles[ti].Vertices; var vs = new[] { nd.Vertices[tv.X], nd.Vertices[tv.Y], nd.Vertices[tv.Z] };
                            int x0 = Math.Clamp((int)((vs.Min(v => v.X) - nd.Min.X) / nd.MaxDistanceX), 0, div - 1), x1 = Math.Clamp((int)((vs.Max(v => v.X) - nd.Min.X) / nd.MaxDistanceX), 0, div - 1);
                            int y0 = Math.Clamp((int)((vs.Min(v => v.Y) - nd.Min.Y) / nd.MaxDistanceY), 0, div - 1), y1 = Math.Clamp((int)((vs.Max(v => v.Y) - nd.Min.Y) / nd.MaxDistanceY), 0, div - 1);
                            for (int gx = x0; gx <= x1; gx++) for (int gy = y0; gy <= y1; gy++) { total++; if (lists[yMajor ? gy * div + gx : gx * div + gy].Contains(ti)) hits++; }
                        }
                        Console.WriteLine($"grid order {(yMajor ? "y*div+x" : "x*div+y")}: bbox cells containing tri {hits}/{total} (grid refs {lists.Sum(l => l.Count)})");
                    }
                }
                {
                    int up = 0, down = 0; var doorUnknowns = new Dictionary<int, int>();
                    foreach (var n in skyrim.Cells.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).SelectMany(c => c.NavigationMeshes).Where(n => n.Data is not null).Take(300))
                    {
                        var nd = n.Data!;
                        foreach (var t in nd.Triangles)
                        {
                            var a = nd.Vertices[t.Vertices.X]; var b2 = nd.Vertices[t.Vertices.Y]; var c = nd.Vertices[t.Vertices.Z];
                            float cz = (b2.X - a.X) * (c.Y - a.Y) - (b2.Y - a.Y) * (c.X - a.X);
                            if (cz > 0) up++; else if (cz < 0) down++;
                        }
                        foreach (var dt in nd.DoorTriangles) doorUnknowns[dt.Unknown] = doorUnknowns.GetValueOrDefault(dt.Unknown) + 1;
                    }
                    Console.WriteLine($"winding over 300 navmeshes: counter-clockwise from above {up}, clockwise {down}");
                    Console.WriteLine("door triangle unknown values: " + string.Join(" ", doorUnknowns.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key}(0x{k.Key:X8})x{k.Value}")));
                }
                Console.WriteLine("triangle flag values: " + string.Join(" ", Enum.GetValues<NavmeshTriangle.Flag>().Select(v => $"{v}=0x{Convert.ToInt32(v):X}")));
                Console.WriteLine("flags used: " + string.Join(", ", nav.Data.Triangles.GroupBy(t => t.Flags).Select(g => $"{g.Key} x{g.Count()}")));
                foreach (var mi in navi.MapInfos.Where(m => m.Parent?.GetType().Name.Contains("Cell") == true).Take(5))
                    Console.WriteLine($"  interior entry parent={D(mi.Parent)} unknown={mi.Unknown} unknown2={mi.Unknown2} doors={mi.LinkedDoors.Count} island={(mi.Island is null ? "null" : "yes")} merges={mi.PreferredMerges.Count}");
                return 0;
            }
            case "find-forms":
            {
                // Dev aid: lighting templates, lights, doors and containers whose EditorID matches --name.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var t in skyrim.LightingTemplates.Where(x => rx.IsMatch(x.EditorID ?? ""))) Console.WriteLine($"LGTM {t.EditorID,-40} {t.FormKey} ambient={t.AmbientColor} directional={t.DirectionalColor} fogNear={t.FogNear} fogFar={t.FogFar}");
                foreach (var t in skyrim.Lights.Where(x => rx.IsMatch(x.EditorID ?? ""))) Console.WriteLine($"LIGH {t.EditorID,-40} {t.FormKey} radius={t.Radius} color={t.Color} flags={t.Flags} model={t.Model?.File.DataRelativePath.Path}");
                foreach (var t in skyrim.Doors.Where(x => rx.IsMatch(x.EditorID ?? ""))) Console.WriteLine($"DOOR {t.EditorID,-40} {t.FormKey} flags={t.Flags} bounds=({t.ObjectBounds.First})..({t.ObjectBounds.Second}) model={t.Model?.File.DataRelativePath.Path}");
                foreach (var t in skyrim.Containers.Where(x => rx.IsMatch(x.EditorID ?? ""))) Console.WriteLine($"CONT {t.EditorID,-40} {t.FormKey} flags={t.Flags} items={t.Items?.Count ?? 0} bounds=({t.ObjectBounds.First})..({t.ObjectBounds.Second}) model={t.Model?.File.DataRelativePath.Path}");
                var mgefNames = skyrim.MagicEffects.ToDictionary(m => m.FormKey, m => m.EditorID);
                foreach (var t in skyrim.Spells.Where(x => rx.IsMatch(x.EditorID ?? ""))) Console.WriteLine($"SPEL {t.EditorID,-40} {t.FormKey} type={t.Type} cast={t.CastType} target={t.TargetType} cost={t.BaseCost} flags={t.Flags} level={t.HalfCostPerk.FormKey} effects=" + string.Join(",", t.Effects.Select(e => $"{mgefNames.GetValueOrDefault(e.BaseEffect.FormKey)}(m{e.Data?.Magnitude} a{e.Data?.Area} d{e.Data?.Duration})")));
                foreach (var t in skyrim.MagicEffects.Where(x => rx.IsMatch(x.EditorID ?? ""))) Console.WriteLine($"MGEF {t.EditorID,-40} {t.FormKey} {t.Archetype.Type} av={t.Archetype.ActorValue} cast={t.CastType} target={t.TargetType} proj={t.Projectile.FormKey} expl={t.Explosion.FormKey} resist={t.ResistValue} school={t.MagicSkill} flags={t.Flags}");
                foreach (var t in skyrim.Furniture.Where(x => rx.IsMatch(x.EditorID ?? ""))) Console.WriteLine($"FURN {t.EditorID,-40} {t.FormKey} bounds=({t.ObjectBounds.First})..({t.ObjectBounds.Second}) model={t.Model?.File.DataRelativePath.Path}");
                foreach (var t in skyrim.Statics.Where(x => rx.IsMatch(x.EditorID ?? ""))) Console.WriteLine($"STAT {t.EditorID,-40} {t.FormKey} bounds=({t.ObjectBounds.First})..({t.ObjectBounds.Second}) model={t.Model?.File.DataRelativePath.Path}");
                foreach (var t in skyrim.MoveableStatics.Where(x => rx.IsMatch(x.EditorID ?? ""))) Console.WriteLine($"MSTT {t.EditorID,-40} {t.FormKey} bounds=({t.ObjectBounds.First})..({t.ObjectBounds.Second}) model={t.Model?.File.DataRelativePath.Path}");
                return 0;
            }
            case "find-statics":
            {
                // Dev aid: statics whose EditorID matches --name, with their bounds, to pick room pieces.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("name", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                IEnumerable<(string? id, Mutagen.Bethesda.Plugins.FormKey key, string? model, IObjectBoundsGetter? b)> all =
                    skyrim.Statics.Select(s => (s.EditorID, s.FormKey, s.Model?.File.DataRelativePath.Path, (IObjectBoundsGetter?)s.ObjectBounds))
                    .Concat(skyrim.Lights.Select(s => (s.EditorID, s.FormKey, s.Model?.File.DataRelativePath.Path, (IObjectBoundsGetter?)s.ObjectBounds)))
                    .Concat(skyrim.LightingTemplates.Select(s => (s.EditorID, s.FormKey, (string?)null, (IObjectBoundsGetter?)null)));
                foreach (var s in all.Where(s => s.id is { } e && rx.IsMatch(e)).Take(60))
                    Console.WriteLine($"{s.id,-40} {s.key} {(s.b is null ? "" : $"bounds ({s.b.First}) .. ({s.b.Second})")} {s.model}");
                return 0;
            }
            case "compare-world":
            {
                // Field-by-field dump of a worldspace, its persistent cell and a sample exterior cell/LAND, for
                // diffing our output against a worldspace known to load in game.
                string Dump(object? o, int depth = 0)
                {
                    if (o is null) return "null";
                    if (depth > 2 || o is string || o.GetType().IsPrimitive || o is Enum || o.GetType().Name.Contains("FormLink") || o.GetType().Name.StartsWith("P2") || o.GetType().Name.StartsWith("P3")) return o.ToString() ?? "";
                    if (o is System.Collections.IEnumerable e) return $"[{e.Cast<object>().Count()} items]";
                    var props = o.GetType().GetProperties().Where(p => p.GetIndexParameters().Length == 0 && p.Name is not ("StaticRegistration" or "Registration"));
                    return "{" + string.Join(", ", props.Select(p => { object? v; try { v = p.GetValue(o); } catch { v = "?"; } return $"{p.Name}={Dump(v, depth + 1)}"; })) + "}";
                }
                foreach (var (file, edid) in new[] { ("Skyrim.esm", "Tamriel"), (opts.GetValueOrDefault("a", "Morrowind.esm"), opts.GetValueOrDefault("aws", "WrldMorrowind")), (opts.GetValueOrDefault("b", "out/mod/Daggerfall.esm"), DaggerfallPluginBuilder.WorldEditorId) })
                {
                    string path = File.Exists(file) ? file : Path.Combine(skyrimData, file);
                    using var plugin = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                    var ws = plugin.Worldspaces.First(w => w.EditorID == edid);
                    var allCells = ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).ToList();
                    Console.WriteLine($"BOUNDS {edid}: min=({ws.ObjectBoundsMin.X},{ws.ObjectBoundsMin.Y}) max=({ws.ObjectBoundsMax.X},{ws.ObjectBoundsMax.Y}) cellsX {allCells.Min(c => c.Grid?.Point.X)}..{allCells.Max(c => c.Grid?.Point.X)} worldMapScale={ws.WorldMapOffsetScale} offset={ws.WorldMapCellOffset} landCompressed={allCells.Count(c => c.Landscape?.IsCompressed == true)}/{allCells.Count(c => c.Landscape is not null)} topCellFlags=0x{(int)ws.TopCell!.SkyrimMajorRecordFlags:X} cellFlags={string.Join(",", allCells.Select(c => c.Flags).Distinct().Take(4))}");
                    Console.WriteLine($"===== {file} header flags={plugin.ModHeader.Flags} version={plugin.ModHeader.Stats.Version} formVersion={plugin.ModHeader.FormVersion}");
                    Console.WriteLine($"WRLD {Dump(ws)}");
                    Console.WriteLine($"TopCell {Dump(ws.TopCell)} persistent={ws.TopCell?.Persistent.Count}");
                    var cell = ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).First(c => c.Landscape is not null);
                    Console.WriteLine($"Cell {Dump(cell)}");
                    Console.WriteLine($"LAND {Dump(cell.Landscape)}");
                    Console.WriteLine($"blocks={ws.SubCells.Count} first block {ws.SubCells[0].BlockNumberX},{ws.SubCells[0].BlockNumberY} type={ws.SubCells[0].GroupType} sub0 type={ws.SubCells[0].Items[0].GroupType}");
                }
                return 0;
            }
            case "probe-newgame":
            {
                // Dev aid: what starts MQ101 on a new game? Check start-game quests, default objects and
                // story manager nodes for references to it.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var mq = skyrim.Quests.First(q => q.EditorID == "MQ101");
                Console.WriteLine($"MQ101 flags={mq.Flags} raw type={mq.Flags.GetType().Name} event={mq.Event} type={mq.Type}");
                Console.WriteLine("Quest.Flag values: " + string.Join(" ", Enum.GetValues<Quest.Flag>().Select(v => $"{v}=0x{(int)(object)v:X}")));
                foreach (var rec in skyrim.EnumerateMajorRecords().Where(r => r is not IQuestGetter || r.EditorID != "MQ101"))
                    if (rec.EnumerateFormLinks().Any(l => l.FormKey == mq.FormKey))
                        Console.WriteLine($"  references MQ101: {rec.GetType().Name.Replace("BinaryOverlay", "")} {rec.EditorID} {rec.FormKey}");
                Console.WriteLine("start-game-enabled quests: " + skyrim.Quests.Count(q => q.Flags.HasFlag(Quest.Flag.StartGameEnabled)));
                var vm = mq.VirtualMachineAdapter!;
                Console.WriteLine($"VMAD scripts: {string.Join(", ", vm.Scripts.Select(s => s.Name))}");
                foreach (var stage in mq.Stages.Where(s => s.Index <= 10))
                {
                    Console.WriteLine($"stage {stage.Index} flags={stage.Flags} entries={stage.LogEntries.Count}");
                    for (int e = 0; e < stage.LogEntries.Count; e++)
                    {
                        var le = stage.LogEntries[e];
                        var frag = vm.Fragments.FirstOrDefault(f => f.Stage == stage.Index && f.StageIndex == e);
                        string conds = string.Join(" AND ", le.Conditions.Select(c => c is IConditionFloatGetter cf ? $"{cf.Data.GetType().Name.Replace("ConditionDataBinaryOverlay", "").Replace("BinaryOverlay", "")} {cf.CompareOperator} {cf.ComparisonValue}" : c.GetType().Name));
                        Console.WriteLine($"  entry {e}: flags={le.Flags} conds=[{conds}] fragment={frag?.ScriptName}.{frag?.FragmentName}");
                        foreach (var c in le.Conditions) foreach (var l in c.EnumerateFormLinks()) Console.WriteLine($"     cond link {l.FormKey}");
                    }
                }
                return 0;
            }
            case "travel-build":
            {
                // "TamrielMerge Travel.esp" (docs/travel.md): Daggerfall's travel for every converted world in --dir (default
                // out/convert) and Skyrim's, written into --out (default --dir). Run by convert after the start plugin.
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", dir));
                Directory.CreateDirectory(outDir);
                int n = Skyrim.Travel.TravelPluginBuilder.Build(dir, skyrimData, GameInstalls.DataDir(Game.Daggerfall), Console.WriteLine, outDir, GameInstalls.DataDir(Game.Oblivion));
                return n > 0 ? 0 : 1;
            }
            case "start-build":
            {
                // Rebuilds just the start plugin into --dir (default out/mod) and compiles the scripts. Daggerfall's start
                // marker is found in a converted Daggerfall.esm (in --dir or Skyrim's Data), if there is one.
                string dir = opts.GetValueOrDefault("dir", "out/mod");
                uint? dfStart = null;
                foreach (var candidate in new[] { Path.Combine(dir, "Daggerfall.esm"), Path.Combine(skyrimData, "Daggerfall.esm") })
                {
                    if (!File.Exists(candidate)) continue;
                    using var df = SkyrimMod.CreateFromBinaryOverlay(candidate, SkyrimRelease.SkyrimSE);
                    dfStart = df.EnumerateMajorRecords<IPlacedObjectGetter>().FirstOrDefault(r => r.EditorID == "DFEnterPrivateersHold")?.FormKey.ID;   // where a new game begins (DungeonCellBuilder.StartMarker)
                    if (dfStart is not null) break;
                }
                Console.WriteLine(dfStart is { } id ? $"Daggerfall start: {id:X6}" : "Daggerfall not converted: the stories begin with the first converted game");
                // Each converted game's start after its opening, for the alternative starts.
                var afterOpenings = Skyrim.AltStarts.Find(dir);
                foreach (var a in afterOpenings) Console.WriteLine($"After the opening ({a.Story}): ref {a.RefId:X6} offset ({a.Dx:F0},{a.Dy:F0},{a.Dz:F0}) quest {a.QuestId:X6} stage {a.QuestStage} item {a.ItemId:X6}");
                Skyrim.AltStartBuilder.Build(skyrimData, dir, dfStart, afterOpenings);
                AltStartBuilder.CompileScripts(skyrimData, Path.Combine(AppContext.BaseDirectory, "Papyrus"), dir);
                AltStartBuilder.CopyEraPlugin(dir);
                return 0;
            }
            case "voice-plan":
            {
                // The voice files a built plugin's dialogue needs (--plugin), by voice type; --out writes the job list (JSON).
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var jobs = Skyrim.VoicePlanner.Plan(m, skyrimData, out var per);
                foreach (var (v, n) in per.OrderByDescending(kv => kv.Value)) Console.WriteLine($"{n,7} {v}");
                Console.WriteLine($"{jobs.Count} voice files for {jobs.Select(j => j.Text).Distinct().Count()} distinct lines ({jobs.Sum(j => j.Text.Length)} characters to speak)");
                if (opts.TryGetValue("out", out var jobFile)) Skyrim.VoicePlanner.WriteJobs(jobs, jobFile);
                return 0;
            }
            case "voice-refs":
            {
                // Reference recordings (vanilla lines as .wav) for voice types (--voices a,b), into --out (default tools/voice/refs).
                int n = Skyrim.VoiceFiles.ExtractReferences(skyrimData, opts["voices"].Split(','), opts.GetValueOrDefault("out", "tools/voice/refs"));
                Console.WriteLine($"{n} reference recordings");
                return 0;
            }
            case "voice-pack":
            {
                // Packs one .wav (--wav) with its text (--text) into a .fuz (--fuz), to test the tools.
                Console.WriteLine(Skyrim.VoiceFiles.Pack(skyrimData, opts["wav"], opts["text"], opts["fuz"]) ? $"wrote {opts["fuz"]}" : "failed");
                return 0;
            }
            case "df-navmesh-test":
            {
                // Dev aid: the overland's terrain navmesh (NavmeshBuilder.GenerateTerrainGrid) on --cells N land cells of
                // --plugin (Daggerfall Land.esm), checked: triangles a cell, how far a vertex... the surface lies from the
                // ground, and inner edges not shared by exactly two triangles (cracks).
                string plugin = Path.GetFullPath(opts.GetValueOrDefault("plugin", Path.Combine("out", "convert", Skyrim.Terrain.OverlandTiles.LandPluginName)));
                int want = int.Parse(opts.GetValueOrDefault("cells", "2000"));
                using var land = SkyrimMod.CreateFromBinaryOverlay(plugin, SkyrimRelease.SkyrimSE);
                var rnd = new Random(1);
                var all = land.Worldspaces.SelectMany(w => w.SubCells).SelectMany(b => b.Items).SelectMany(sb => sb.Items)
                    .Where(c => c.Landscape?.VertexHeightMap is not null && c.Grid is not null).ToList();
                long tris = 0; int made = 0, cracked = 0; double worst = 0, sumErr = 0; long samples = 0;
                foreach (var cell in all.OrderBy(_ => rnd.Next()).Take(want))
                {
                    int cx = cell.Grid!.Point.X, cy = cell.Grid.Point.Y;
                    float? water = cell.Flags.HasFlag(Cell.Flag.HasWater) ? cell.WaterHeight ?? 0f : null;
                    if (Skyrim.Navmesh.NavmeshBuilder.GenerateTerrainGrid(cell.Landscape!, cx, cy, water, []) is not { } g) continue;
                    made++; tris += g.Tris.Count;
                    // Inner edges: each must be shared by two triangles, unless it borders left-out ground.
                    var edges = new Dictionary<(int, int), int>();
                    foreach (var (ta, tb, tc) in g.Tris)
                        foreach (var (e0, e1) in new[] { (ta, tb), (tb, tc), (tc, ta) })
                        {
                            var k = (Math.Min(e0, e1), Math.Max(e0, e1));
                            edges[k] = edges.GetValueOrDefault(k) + 1;
                        }
                    if (edges.Values.Any(n => n > 2)) cracked++;
                    // A T-junction: a vertex lying inside another triangle's edge.
                    bool tee = false;
                    var onEdge = g.Verts.Select(v => (v.X, v.Y)).ToHashSet();
                    foreach (var ((e0, e1), n) in edges)
                    {
                        if (n != 1) continue;
                        var p0 = g.Verts[e0]; var p1 = g.Verts[e1];
                        float mx = (p0.X + p1.X) / 2, my = (p0.Y + p1.Y) / 2;
                        bool border = (p0.X == p1.X && (p0.X % 4096 == 0)) || (p0.Y == p1.Y && (p0.Y % 4096 == 0));
                        if (!border && onEdge.Contains((mx, my)) && edges.Keys.Any(k2 => (g.Verts[k2.Item1].X, g.Verts[k2.Item1].Y) == (mx, my) || (g.Verts[k2.Item2].X, g.Verts[k2.Item2].Y) == (mx, my))) { tee = true; break; }
                    }
                    if (tee) cracked++;
                    // The surface against the ground at every height point under a triangle.
                    var vh = cell.Landscape!.VertexHeightMap!;
                    var hh = new float[33, 33]; float row = vh.Offset;
                    for (int y = 0; y < 33; y++) { row += vh.HeightMap[0, y]; float v = row; hh[0, y] = v * 8; for (int x = 1; x < 33; x++) { v += vh.HeightMap[x, y]; hh[x, y] = v * 8; } }
                    foreach (var (ta, tb, tc) in g.Tris)
                    {
                        var A = g.Verts[ta]; var B = g.Verts[tb]; var C = g.Verts[tc];
                        float minX = Math.Min(A.X, Math.Min(B.X, C.X)), maxX = Math.Max(A.X, Math.Max(B.X, C.X)), minY = Math.Min(A.Y, Math.Min(B.Y, C.Y)), maxY = Math.Max(A.Y, Math.Max(B.Y, C.Y));
                        float det = (B.Y - C.Y) * (A.X - C.X) + (C.X - B.X) * (A.Y - C.Y);
                        for (float py = minY; py <= maxY; py += 128)
                            for (float px = minX; px <= maxX; px += 128)
                            {
                                float l1 = ((B.Y - C.Y) * (px - C.X) + (C.X - B.X) * (py - C.Y)) / det, l2 = ((C.Y - A.Y) * (px - C.X) + (A.X - C.X) * (py - C.Y)) / det, l3 = 1 - l1 - l2;
                                if (l1 < -1e-4 || l2 < -1e-4 || l3 < -1e-4) continue;
                                float z = l1 * A.Z + l2 * B.Z + l3 * C.Z;
                                int ix = (int)MathF.Round((px - cx * 4096) / 128), iy = (int)MathF.Round((py - cy * 4096) / 128);
                                double e = Math.Abs(z - hh[ix, iy]); worst = Math.Max(worst, e); sumErr += e; samples++;
                            }
                    }
                }
                Console.WriteLine($"{made} cells with a navmesh of {Math.Min(want, all.Count)}: {tris / (double)Math.Max(1, made):F1} triangles a cell (x{all.Count:N0} land cells = {tris / (double)Math.Max(1, made) * all.Count / 1e6:F1} million); off the ground: mean {sumErr / Math.Max(1, samples):F1}, worst {worst:F0} units; cells with cracks: {cracked}");
                return 0;
            }
            case "df-navmesh":
            {
                // Navmeshes for Daggerfall's overland, added to a built "Daggerfall Land.esm" (--plugin, default
                // out/convert): Skyrim/Terrain/OverlandNavmesh. The full Daggerfall build runs this after the land. A
                // split land (its other plugins beside it) gets them in its last plugin.
                string plugin = Path.GetFullPath(opts.GetValueOrDefault("plugin", Path.Combine("out", "convert", Skyrim.Terrain.OverlandTiles.LandPluginName)));
                var landSet = Skyrim.Terrain.OverlandTiles.LandPluginsIn(Path.GetDirectoryName(plugin)!);
                var (meshes, tris, links) = Skyrim.Terrain.OverlandNavmesh.Build(landSet.Contains(Path.GetFileName(plugin)) ? landSet.Select(l => Path.Combine(Path.GetDirectoryName(plugin)!, l)).ToList() : [plugin],
                    line => Console.WriteLine("Navmesh: " + line));
                Console.WriteLine($"Overland navmesh: {meshes:N0} navmeshes, {tris:N0} triangles, {links:N0} links between cells");
                return 0;
            }
            case "voices":
            {
                // Speaks the converted dialogue that has no recordings (Morrowind's) in Skyrim's own voices and packs it as
                // voice files (Skyrim/VoiceGenerator): workers on the graphics card (--gpu) and the CPU share the lines,
                // with progress for the app; --plugin (default Morrowind.esm in --out, default out/convert), --install.
                // Resumes where it stopped; workers already running are watched rather than doubled.
                // Without --plugin: every converted game with lines to speak, one after another: Morrowind's dialogue (it
                // recorded only barks) and the lines Oblivion never recorded.
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/convert"));
                var voicePlugins = opts.TryGetValue("plugin", out var onePlugin) ? [Path.GetFullPath(onePlugin)]
                    : new[] { "Morrowind.esm", "Oblivion.esm" }.Select(p => Path.Combine(outDir, p)).Where(File.Exists).ToList();
                if (voicePlugins.Count == 0) { Console.WriteLine($"Voices: no converted Morrowind.esm or Oblivion.esm in {outDir} (convert first)"); return 1; }
                int voicesResult = 0;
                foreach (var plugin in voicePlugins)
                {
                    int r = Skyrim.VoiceGenerator.Run(skyrimData, plugin, Path.Combine(Setup.Dependencies.Root, "voice"), outDir,
                        opts.ContainsKey("install"), opts.ContainsKey("gpu"), planOnly: opts.ContainsKey("plan-only"));
                    if (r == 1) return 1;
                    voicesResult = Math.Max(voicesResult, r);
                }
                return voicesResult;
            }
            case "voice-pack-all":
            {
                // Packs every spoken line (--wavs, from tools/voice/generate.py) of a job list (--jobs) as .fuz files under --out.
                var jobs = System.Text.Json.JsonSerializer.Deserialize<List<Skyrim.VoicePlanner.Job>>(File.ReadAllText(opts["jobs"]))!;
                var (packed, existing, missing) = Skyrim.VoiceFiles.PackAll(skyrimData, jobs, opts.GetValueOrDefault("wavs", "tools/voice/wavs"), opts.GetValueOrDefault("out", "out/voice"));
                Console.WriteLine($"{packed} voice files packed, {existing} already there, {missing} lines not spoken yet");
                return 0;
            }
            case "ob-voices":
            {
                // Oblivion's own recordings as the voice files of a built Oblivion.esm (--plugin, with its voicemap next
                // to it), packed under --out (default out/obvoice).
                string dataDir = GameInstalls.DataDir(Game.Oblivion) ?? throw new DirectoryNotFoundException("Oblivion isn't installed through Steam or GOG");
                string pluginPath = opts.GetValueOrDefault("plugin", "out/obmod/Oblivion.esm");
                var (packed, existing, unrecorded) = Oblivion.ObVoices.Build(skyrimData, new Oblivion.ObData(dataDir), pluginPath,
                    Path.Combine(Path.GetDirectoryName(Path.GetFullPath(pluginPath))!, "Oblivion.voicemap.json"), opts.GetValueOrDefault("out", "out/obvoice"));
                Console.WriteLine($"{packed} voice files from Oblivion's recordings ({Skyrim.VoiceFiles.CacheStats.Hits} of them from the lip sync cache), {existing} already there, {unrecorded} lines Oblivion didn't record");
                return 0;
            }
            case "ob-voice-test":
            {
                // Dev aid: decodes one Oblivion recording (--path, archive path) to a .wav in out/voicetest.
                var data = new Oblivion.ObData(GameInstalls.DataDir(Game.Oblivion)!);
                Directory.CreateDirectory("out/voicetest");
                var wav = Oblivion.ObVoices.Decode(data, opts["path"], Path.GetFullPath("out/voicetest"));
                Console.WriteLine(wav is null ? "could not decode" : $"{wav}: {new FileInfo(wav).Length} bytes");
                return 0;
            }
            case "voice-cache-test":
            {
                // Dev aid: the lip sync cache (VoiceFiles.Pack) on --count N (default 8) of Oblivion's recordings, in
                // --out (a scratch folder, its cache inside): each decoded twice (the same bytes, or the cache can't
                // match), packed under one set of names with the cache empty, then under new names (renumbered
                // FormIDs), timed; the second set must come from the cache, byte for byte the first.
                string dir = Path.GetFullPath(opts["out"]);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.CreateDirectory(dir);
                Skyrim.VoiceFiles.CacheDirOverride = Path.Combine(dir, "lipcache");
                var data = new Oblivion.ObData(GameInstalls.DataDir(Game.Oblivion)!);
                int count = int.Parse(opts.GetValueOrDefault("count", "8"));
                var mp3s = data.List(@"sound\voice").Where(p => p.EndsWith(".mp3")).Order(StringComparer.OrdinalIgnoreCase).Where((_, i) => i % 997 == 0).Take(count).ToList();
                var wavs = new List<string>();
                int same = 0;
                foreach (var mp3 in mp3s)
                {
                    string? a = Oblivion.ObVoices.Decode(data, mp3, dir), b = Oblivion.ObVoices.Decode(data, mp3, dir);
                    if (a is null || b is null) continue;
                    if (File.ReadAllBytes(a).AsSpan().SequenceEqual(File.ReadAllBytes(b))) same++;
                    File.Delete(b);
                    wavs.Add(a);
                }
                Console.WriteLine($"{wavs.Count} recordings decoded, {same} the same both times");
                double Pass(string names)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Parallel.For(0, wavs.Count, i =>
                    {
                        string copy = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".wav");
                        File.Copy(wavs[i], copy);
                        try { Skyrim.VoiceFiles.Pack(skyrimData, copy, $"Test line {i}, as Oblivion says it.", Path.Combine(dir, names, $"line_{names}_{i}.fuz")); }
                        finally { File.Delete(copy); }
                    });
                    return sw.Elapsed.TotalSeconds;
                }
                double first = Pass("first");
                var afterFirst = Skyrim.VoiceFiles.CacheStats;
                double second = Pass("second");
                var afterSecond = Skyrim.VoiceFiles.CacheStats;
                int identical = Enumerable.Range(0, wavs.Count).Count(i => File.Exists(Path.Combine(dir, "first", $"line_first_{i}.fuz"))
                    && File.ReadAllBytes(Path.Combine(dir, "first", $"line_first_{i}.fuz")).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(dir, "second", $"line_second_{i}.fuz"))));
                Console.WriteLine($"first pass {first:F1} s ({afterFirst.Packed} packed, {afterFirst.Hits} from the cache); renumbered pass {second:F2} s ({afterSecond.Packed - afterFirst.Packed} packed, {afterSecond.Hits - afterFirst.Hits} from the cache); {identical} of {wavs.Count} identical");
                return identical == wavs.Count && afterSecond.Hits - afterFirst.Hits == wavs.Count ? 0 : 2;
            }
            case "voice-coverage":
            {
                // Dev aid: each speaker (a GetIsID in a response's conditions) of --plugin: race and voice type (its look's,
                // as VoicePlanner takes it), responses, and how many have a voice file under --dir (sound\voice\<plugin>).
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                using var sk = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var skNpcs = sk.Npcs.ToDictionary(n => n.FormKey);
                var voices = sk.VoiceTypes.ToDictionary(v => v.FormKey, v => v.EditorID ?? "");
                var races = sk.Races.ToDictionary(r => r.FormKey, r => r.EditorID ?? "");
                string vdir = Path.Combine(opts["dir"], "sound", "voice", Path.GetFileName(opts["plugin"]));
                var have = Directory.Exists(vdir) ? Directory.GetDirectories(vdir).ToDictionary(d => Path.GetFileName(d).ToLowerInvariant(),
                    d => Directory.GetFiles(d, "*.fuz").Select(f => Path.GetFileNameWithoutExtension(f).Split('_')[^2].ToLowerInvariant()).ToHashSet()) : [];
                var npcs = m.Npcs.ToDictionary(n => n.FormKey);
                var rows = new Dictionary<FormKey, (int Infos, int Voiced)>();
                foreach (var t in m.DialogTopics)
                    foreach (var info in t.Responses)
                        foreach (var c in info.Conditions.OfType<IConditionFloatGetter>())
                            if (c.Data is IGetIsIDConditionDataGetter d && c.ComparisonValue == 1 && npcs.TryGetValue(d.Object.Link.FormKey, out var npc))
                            {
                                string vt = npc.Template.IsNull ? "" : skNpcs.TryGetValue(npc.Template.FormKey, out var look) && voices.TryGetValue(look.Voice.FormKey, out var v) ? v : "";
                                string id8 = info.FormKey.ID.ToString("x8");
                                bool voiced = have.Values.Any(ids => ids.Contains(id8));   // in any voice the speaker can have
                                rows.TryGetValue(npc.FormKey, out var r);
                                rows[npc.FormKey] = (r.Infos + 1, r.Voiced + (voiced ? 1 : 0));
                            }
                foreach (var (k, r) in rows.OrderBy(kv => kv.Value.Voiced * 1.0 / Math.Max(1, kv.Value.Infos)))
                {
                    var npc = npcs[k];
                    var look = npc.Template.IsNull ? null : skNpcs.GetValueOrDefault(npc.Template.FormKey);
                    // Sex: the person's own (as converted) and its look's, whose voice it speaks with.
                    string own = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) ? "F" : "M";
                    string lookSex = look is null ? "?" : look.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) ? "F" : "M";
                    Console.WriteLine($"{npc.Name?.String ?? npc.EditorID,-18} {(look is null ? "?" : races.GetValueOrDefault(look.Race.FormKey)),-20} {(look is null ? "?" : voices.GetValueOrDefault(look.Voice.FormKey)),-24} {own}/{lookSex} {r.Voiced}/{r.Infos} voiced");
                    if (look is null && opts.ContainsKey("why"))
                        Console.WriteLine($"    template {npc.Template.FormKey} traits {npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)}, own race {npc.Race.FormKey}, own voice {npc.Voice.FormKey}");
                }
                return 0;
            }
            case "town-site":
            {
                // Dev aid: where a town worldspace (--world EditorID) is entered from the overland: the load doors in --plugins
                // (';'-separated) leading into it, with the overland worldspace, door position and arrival position.
                var mods = opts["plugins"].Split(';').Select(f => SkyrimMod.CreateFromBinaryOverlay(f, SkyrimRelease.SkyrimSE)).ToList();
                var refs = new Dictionary<FormKey, (string World, IPlacedObjectGetter Ref)>();
                foreach (var m in mods)
                    foreach (var w in m.Worldspaces)
                    {
                        var cells = w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).ToList();
                        if (w.TopCell is { } top) cells.Add(top);
                        foreach (var c in cells)
                            foreach (var r in c.Persistent.Concat(c.Temporary).OfType<IPlacedObjectGetter>())
                                refs[r.FormKey] = (w.EditorID ?? "?", r);
                    }
                foreach (var (key, (world, r)) in refs)
                {
                    var d = r.TeleportDestination;
                    if (d is null || !refs.TryGetValue(d.Door.FormKey, out var dest) || dest.World != opts["world"]) continue;
                    var p = r.Placement!.Position;
                    Console.WriteLine($"{world}: door at {p.X:F0}, {p.Y:F0}, {p.Z:F0} -> {dest.World} at {d.Position.X:F0}, {d.Position.Y:F0}, {d.Position.Z:F0}");
                }
                return 0;
            }
            case "refs-of":
            {
                // Dev aid: placements in --plugin of the base whose EditorID is --edid: cell, position, rotation (degrees), scale.
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var key = m.EnumerateMajorRecords().First(r => r.EditorID == opts["edid"]).FormKey;
                foreach (var ctx in m.EnumerateMajorRecordContexts<IPlacedObject, IPlacedObjectGetter>(m.ToImmutableLinkCache()).Where(c => c.Record.Base.FormKey == key))
                {
                    var r = ctx.Record; var pl = r.Placement!;
                    string cell = ctx.Parent?.Record is ICellGetter c ? (c.EditorID ?? $"{c.Grid?.Point}") : "?";
                    Console.WriteLine($"{cell}: at {pl.Position.X:F0}, {pl.Position.Y:F0}, {pl.Position.Z:F0}; rot {pl.Rotation.X * 180 / MathF.PI:F0}, {pl.Rotation.Y * 180 / MathF.PI:F0}, {pl.Rotation.Z * 180 / MathF.PI:F0}; scale {r.Scale ?? 1:F2}");
                }
                return 0;
            }
            case "plugin-diff":
            {
                // Two builds of a plugin (--a old, --b new) by FormID per record type: in both with the same EditorID, in
                // both with another EditorID, or only in one. A rebuild keeps voice files and saves working only if the
                // records they name keep their FormIDs.
                using var pa = SkyrimMod.CreateFromBinaryOverlay(opts["a"], SkyrimRelease.SkyrimSE);
                using var pb = SkyrimMod.CreateFromBinaryOverlay(opts["b"], SkyrimRelease.SkyrimSE);
                var ra = pa.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == pa.ModKey).ToDictionary(r => r.FormKey.ID, r => (Type: r.GetType().Name.Replace("BinaryOverlay", ""), r.EditorID));
                var rb = pb.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == pb.ModKey).ToDictionary(r => r.FormKey.ID, r => (Type: r.GetType().Name.Replace("BinaryOverlay", ""), r.EditorID));
                foreach (var type in ra.Values.Select(v => v.Type).Concat(rb.Values.Select(v => v.Type)).Distinct().OrderBy(t => t))
                {
                    var ia = ra.Where(kv => kv.Value.Type == type).ToDictionary(kv => kv.Key, kv => kv.Value.EditorID);
                    var ib = rb.Where(kv => kv.Value.Type == type).ToDictionary(kv => kv.Key, kv => kv.Value.EditorID);
                    int same = ia.Count(kv => ib.TryGetValue(kv.Key, out var e) && e == kv.Value), other = ia.Count(kv => ib.TryGetValue(kv.Key, out var e) && e != kv.Value);
                    int onlyA = ia.Keys.Count(k => !ib.ContainsKey(k)), onlyB = ib.Keys.Count(k => !ia.ContainsKey(k));
                    if (other + onlyA + onlyB > 0 || opts.ContainsKey("all"))
                        Console.WriteLine($"{type}: {same} same, {other} renamed, {onlyA} only in a, {onlyB} only in b");
                }
                Console.WriteLine($"{ra.Count} records in a, {rb.Count} in b, {ra.Count(kv => rb.TryGetValue(kv.Key, out var v) && v == kv.Value)} identical by FormID, type and EditorID");
                return 0;
            }
            case "nav-ext-verify":
            {
                // Exterior navmeshes of a built plugin: vertex indices, winding, internal neighbours symmetric, and every
                // edge link pointing at an existing navmesh triangle that links back.
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var navs = m.EnumerateMajorRecords<INavigationMeshGetter>().Where(n => n.Data?.Parent is IWorldspaceNavmeshParentGetter).ToDictionary(n => n.FormKey);
                int bad = 0, cw = 0, asym = 0, badLinks = 0, oneWay = 0, links = 0, tris = 0;
                foreach (var nav in navs.Values)
                {
                    var d = nav.Data!; var t = d.Triangles; var v = d.Vertices; tris += t.Count;
                    for (int i = 0; i < t.Count; i++)
                    {
                        var tri = t[i];
                        int[] idx = [tri.Vertices.X, tri.Vertices.Y, tri.Vertices.Z];
                        if (idx.Any(k => k < 0 || k >= v.Count)) { bad++; continue; }
                        var (a, b, c) = (v[idx[0]], v[idx[1]], v[idx[2]]);
                        if ((b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X) < 0) cw++;
                        (short Val, NavmeshTriangle.Flag F)[] edges = [(tri.EdgeLink_0_1, NavmeshTriangle.Flag.EdgeLink_0_1), (tri.EdgeLink_1_2, NavmeshTriangle.Flag.EdgeLink_1_2), (tri.EdgeLink_2_0, NavmeshTriangle.Flag.EdgeLink_2_0)];
                        foreach (var (val, f) in edges.Where(e => e.Val >= 0))
                        {
                            if (tri.Flags.HasFlag(f))
                            {
                                links++;
                                if (val >= d.EdgeLinks.Count) { badLinks++; continue; }
                                var link = d.EdgeLinks[val];
                                if (!navs.TryGetValue(link.Mesh.FormKey, out var other) || link.TriangleIndex >= other.Data!.Triangles.Count) { badLinks++; continue; }
                                var ot = other.Data.Triangles[link.TriangleIndex];
                                bool back = new[] { (ot.EdgeLink_0_1, NavmeshTriangle.Flag.EdgeLink_0_1), (ot.EdgeLink_1_2, NavmeshTriangle.Flag.EdgeLink_1_2), (ot.EdgeLink_2_0, NavmeshTriangle.Flag.EdgeLink_2_0) }
                                    .Any(e => e.Item1 >= 0 && ot.Flags.HasFlag(e.Item2) && e.Item1 < other.Data.EdgeLinks.Count && other.Data.EdgeLinks[e.Item1].Mesh.FormKey == nav.FormKey);
                                if (!back) oneWay++;
                            }
                            else
                            {
                                var o = t[val];
                                if (o.EdgeLink_0_1 != i && o.EdgeLink_1_2 != i && o.EdgeLink_2_0 != i) asym++;
                            }
                        }
                    }
                }
                Console.WriteLine($"{navs.Count} exterior navmeshes, {tris} triangles: {bad} bad indices, {cw} clockwise, {asym} one-sided neighbours; {links} edge links, {badLinks} broken, {oneWay} not linked back");
                // The cells (and their land) per worldspace, and the navmesh map: one entry per navmesh, each in the plugin.
                foreach (var w in m.Worldspaces)
                {
                    var wcells = w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).ToList();
                    // Cells without a navmesh whose land rises above their water (64 units, as the builder leaves out).
                    int dry = 0, sliver = 0, part = 0, most = 0;
                    foreach (var c in wcells.Where(c => c.NavigationMeshes.Count == 0 && c.Landscape?.VertexHeightMap is not null))
                    {
                        var vh = c.Landscape!.VertexHeightMap!;
                        float? water = c.Flags.HasFlag(Cell.Flag.HasWater) ? c.WaterHeight ?? 0f : null;
                        float row = vh.Offset, top = float.MinValue;
                        int above = 0;
                        for (int y = 0; y < 33; y++)
                        {
                            row += vh.HeightMap[0, y];
                            float v = row;
                            for (int x = 0; x < 33; x++)
                            {
                                if (x > 0) v += vh.HeightMap[x, y];
                                top = MathF.Max(top, v * 8);
                                if (water is not { } w0 || v * 8 >= w0) above++;
                            }
                        }
                        if (water is not { } wh || top >= wh - 64)
                        {
                            dry++;
                            if (above < 20) sliver++; else if (above < 400) part++; else most++;
                            if (above >= 20 && opts.ContainsKey("list"))
                            {
                                // The share of the cell's quads too steep to walk (over 50 degrees, as Recast's setting).
                                var hh = new float[33, 33];
                                float r2 = vh.Offset;
                                for (int y = 0; y < 33; y++) { r2 += vh.HeightMap[0, y]; float v = r2; hh[0, y] = v * 8; for (int x = 1; x < 33; x++) { v += vh.HeightMap[x, y]; hh[x, y] = v * 8; } }
                                int steep = 0;
                                for (int y = 0; y < 32; y++)
                                    for (int x = 0; x < 32; x++)
                                    {
                                        float gx = (hh[x + 1, y] + hh[x + 1, y + 1] - hh[x, y] - hh[x, y + 1]) / 256f, gy = (hh[x, y + 1] + hh[x + 1, y + 1] - hh[x, y] - hh[x + 1, y]) / 256f;
                                        if (MathF.Atan(MathF.Sqrt(gx * gx + gy * gy)) * 180 / MathF.PI > 50) steep++;
                                    }
                                Console.WriteLine($"    {c.Grid!.Point.X},{c.Grid.Point.Y}: {above} of 1089 points above water, {steep * 100 / 1024}% too steep to walk");
                            }
                        }
                    }
                    Console.WriteLine($"  {w.EditorID}: {wcells.Count} cells, {wcells.Count(c => c.Landscape is not null)} with land, {wcells.Count(c => c.NavigationMeshes.Count > 0)} with a navmesh; without one, {dry} have land above water ({sliver} a sliver of under 20 of 1089 points, {part} under 400, {most} more)");
                    var walk = wcells.Where(c => c.NavigationMeshes.Count > 0 && c.Grid is not null).Select(c => c.Grid!.Point).ToList();
                    if (walk.Count > 0)
                        Console.WriteLine($"    walkable cells span x {walk.Min(p => p.X)}..{walk.Max(p => p.X)}, y {walk.Min(p => p.Y)}..{walk.Max(p => p.Y)}: {(walk.Max(p => p.X) - walk.Min(p => p.X) + 1) * 4096 * 0.01428:F0} x {(walk.Max(p => p.Y) - walk.Min(p => p.Y) + 1) * 4096 * 0.01428:F0} m");
                }
                var infos = m.NavigationMeshInfoMaps.SelectMany(map => map.MapInfos).ToList();
                Console.WriteLine($"Navmesh map: {m.NavigationMeshInfoMaps.Count} map record(s), {infos.Count} entries, {infos.Count(i => !navs.ContainsKey(i.NavigationMesh.FormKey))} not in the plugin, {navs.Keys.Except(infos.Select(i => i.NavigationMesh.FormKey)).Count()} navmeshes without one");
                return 0;
            }
            case "dlg-dump":
            {
                // Dev aid: a built plugin's dialogue topic (--topic EditorID regex): each response's conditions, text,
                // choices and script properties (--max responses).
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var names = m.EnumerateMajorRecords().ToDictionary(r => r.FormKey, r => r.EditorID ?? r.FormKey.ToString());
                var rx = new System.Text.RegularExpressions.Regex(opts["topic"], System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                foreach (var t in m.DialogTopics.Where(t => t.EditorID is { } e && rx.IsMatch(e)).Take(3))
                {
                    Console.WriteLine($"{t.EditorID} \"{t.Name}\" {t.Category}/{t.Subtype}: {t.Responses.Count} responses");
                    foreach (var r in t.Responses.Take(int.Parse(opts.GetValueOrDefault("max", "6"))))
                    {
                        string Cond(IConditionGetter c) => $"{(c.Flags.HasFlag(Condition.Flag.OR) ? "OR " : "")}{c.Data.GetType().Name.Replace("ConditionData", "").Replace("BinaryOverlay", "")}" +
                            $"({string.Join(",", c.Data.EnumerateFormLinks().Select(l => names.GetValueOrDefault(l.FormKey, l.FormKey.ToString())))}){(c.Data.RunOnType == Condition.RunOnType.Reference ? "[PC]" : "")} {(c as IConditionFloatGetter)?.CompareOperator} {(c as IConditionFloatGetter)?.ComparisonValue}";
                        Console.WriteLine($"  - {string.Join(" & ", r.Conditions.Select(Cond))}");
                        foreach (var line in r.Responses) Console.WriteLine($"      \"{line.Text}\"");
                        if (r.LinkTo.Count > 0) Console.WriteLine($"      -> {string.Join(", ", r.LinkTo.Select(l => names.GetValueOrDefault(l.FormKey, "?")))}");
                        if (r.VirtualMachineAdapter is { } vm) Console.WriteLine($"      script {string.Join(";", vm.Scripts.SelectMany(sc => sc.Properties).Select(p => p.Name))}");
                        if (r.Flags?.Flags.HasFlag(DialogResponses.Flag.Goodbye) == true) Console.WriteLine("      (goodbye)");
                    }
                }
                return 0;
            }
            case "npc-outfits":
            {
                // Dev aid: each converted NPC's outfit and what stays in its inventory (--plugin, --limit).
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var names = m.EnumerateMajorRecords().ToDictionary(r => r.FormKey, r => (r as Mutagen.Bethesda.Plugins.Aspects.INamedGetter)?.Name ?? r.EditorID ?? r.FormKey.ToString());
                var outfitsByKey = m.Outfits.ToDictionary(o => o.FormKey);
                int dressed = 0, total = 0;
                foreach (var n in m.Npcs.Where(n => !n.Template.IsNull && n.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)))
                {
                    total++;
                    var o = outfitsByKey.GetValueOrDefault(n.DefaultOutfit.FormKey);
                    if (o is not null) dressed++;
                    if (total <= int.Parse(opts.GetValueOrDefault("limit", "15")))
                        Console.WriteLine($"{n.EditorID} \"{n.Name}\": wears [{string.Join(", ", o?.Items?.Select(i => names.GetValueOrDefault(i.FormKey, i.FormKey.ToString())) ?? [])}] carries [{string.Join(", ", n.Items?.Select(i => names.GetValueOrDefault(i.Item.Item.FormKey, i.Item.Item.FormKey.ToString())) ?? [])}]");
                }
                Console.WriteLine($"{dressed}/{total} people have an outfit; {m.Outfits.Count} outfits");
                if (opts.TryGetValue("armor", out var pat))
                    foreach (var ar in m.Armors.Where(x => (x.Name?.String ?? "").Contains(pat, StringComparison.OrdinalIgnoreCase)))
                        Console.WriteLine($"  {ar.Name} {ar.EditorID}: {ar.BodyTemplate?.FirstPersonFlags}");
                return 0;
            }
            case "form-info":
            {
                // Dev aid: records of a plugin (--plugin) by local FormID (--ids hex,hex) or EditorID (--edid regex).
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var ids = opts.GetValueOrDefault("ids", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => Convert.ToUInt32(x, 16)).ToHashSet();
                var rx = opts.TryGetValue("edid", out var e) ? new System.Text.RegularExpressions.Regex(e) : null;
                foreach (var r in m.EnumerateMajorRecords())
                    if (ids.Contains(r.FormKey.ID) || (rx is not null && r.EditorID is { } ed && rx.IsMatch(ed)))
                        Console.WriteLine($"{r.FormKey} {r.GetType().Name.Replace("BinaryOverlay", "")} {r.EditorID} {(r is IPlacedObjectGetter po ? $"base {po.Base.FormKey} at {po.Placement?.Position}" : "")}{(r is IModeledGetter { Model: { } md } ? $" model {md.File}" : "")}{(r is IObjectBoundedGetter { ObjectBounds: { } ob } ? $" bounds {ob.First}..{ob.Second}" : "")}{(r is IRaceGetter race ? $" skeleton {race.SkeletalModel?.Male?.File} behavior {race.BehaviorGraph?.Male?.File}" : "")}");
                return 0;
            }
            case "verify-start":
            {
                // Re-read the start plugin: masters, ESL range, the MQ101 override, and that script properties resolve.
                string dir = opts.GetValueOrDefault("dir", "out/mod");
                using var start = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, Skyrim.AltStartBuilder.PluginName), SkyrimRelease.SkyrimSE);
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var known = skyrim.EnumerateMajorRecords().Select(r => r.FormKey)
                    .Concat(start.EnumerateMajorRecords().Select(r => r.FormKey)).ToHashSet();
                Console.WriteLine($"masters: {string.Join(", ", start.ModHeader.MasterReferences.Select(m => m.Master))}  flags: {start.ModHeader.Flags}");
                int bad = 0;
                foreach (var rec in start.EnumerateMajorRecords())
                {
                    bool own = rec.FormKey.ModKey == start.ModKey;
                    if (own && (rec.FormKey.ID < 0x800 || rec.FormKey.ID > 0xFFF)) { bad++; Console.WriteLine($"  outside ESL range: {rec.FormKey}"); }
                    Console.WriteLine($"  {rec.GetType().Name.Replace("BinaryOverlay", ""),-8} {rec.FormKey} {rec.EditorID}{(own ? "" : " (override)")}");
                    // 0x14 is the player reference, which the engine creates rather than Skyrim.esm.
                    foreach (var link in rec.EnumerateFormLinks().Where(l => !l.FormKey.IsNull && l.FormKey.ID != 0x14 && !known.Contains(l.FormKey)))
                    { bad++; Console.WriteLine($"    unresolved {link.FormKey}"); }
                }
                var vanillaMq = skyrim.Quests.First(q => q.EditorID == "MQ101");
                var mq = start.Quests.First(q => q.EditorID == "MQ101");
                Console.WriteLine($"MQ101 flags vanilla={vanillaMq.Flags} ours={mq.Flags}; stages {vanillaMq.Stages.Count}/{mq.Stages.Count}, aliases {vanillaMq.Aliases.Count}/{mq.Aliases.Count}");
                var vf = vanillaMq.VirtualMachineAdapter!.Fragments.Select(f => $"{f.Stage}/{f.StageIndex} {f.ScriptName}.{f.FragmentName}").ToList();
                var of = mq.VirtualMachineAdapter!.Fragments.Select(f => $"{f.Stage}/{f.StageIndex} {f.ScriptName}.{f.FragmentName}").ToList();
                Console.WriteLine($"fragments {vf.Count}/{of.Count}; changed: {string.Join("; ", vf.Zip(of).Where(p => p.First != p.Second).Select(p => p.First + " -> " + p.Second))}");
                foreach (var s in mq.VirtualMachineAdapter.Scripts)
                {
                    Console.WriteLine($"script {s.Name} ({s.Properties.Count} properties)");
                    if (!s.Name.StartsWith("TM")) continue;
                    foreach (var p in s.Properties)
                        Console.WriteLine($"  {p.Name} = " + p switch
                        {
                            IScriptObjectPropertyGetter o => o.Object.FormKey.ToString(),
                            IScriptObjectListPropertyGetter l => string.Join(", ", l.Objects.Select(o => o.Object.FormKey)),
                            _ => p.GetType().Name,
                        });
                }
                foreach (var m in start.Messages)
                    Console.WriteLine($"message {m.EditorID}: \"{m.Description}\" [{string.Join(" | ", m.MenuButtons.Select(b => b.Text?.String))}]");
                return bad == 0 ? 0 : 2;
            }
            case "scene-export":
            {
                // A converted place as a scene for tools/scene3d/render_scene.py: --plugins a.esm,b.esm (loaded over
                // Skyrim's), --world <worldspace EditorID>, --centre x,y and --radius r (units; 0 = all), the land out to
                // --land-radius when larger, --out dir.
                var plugins = opts["plugins"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(Path.GetFullPath).ToList();
                var c = opts.GetValueOrDefault("centre", "0,0").Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                float radius = float.Parse(opts.GetValueOrDefault("radius", "0"), System.Globalization.CultureInfo.InvariantCulture);
                float landRadius = float.Parse(opts.GetValueOrDefault("land-radius", "0"), System.Globalization.CultureInfo.InvariantCulture);
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/scenes/" + opts["world"]));
                var loose = plugins.Select(p => Path.GetDirectoryName(p)!).Distinct().ToList();
                var exporter = new Skyrim.Render.SceneExporter(skyrimData, loose, outDir);
                exporter.Export(skyrimData, plugins, opts["world"], new System.Numerics.Vector2(c[0], c[1]), radius, landRadius);
                foreach (var m in exporter.Missing.Distinct().Take(15)) Console.WriteLine($"  missing {m}");
                return 0;
            }
            case "bsa-extract":
            {
                // Copies files whose path contains --match from Skyrim's archives (or --archive NAME) into --out.
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/bsa"));
                var archives = opts.TryGetValue("archive", out var an) ? [Path.Combine(skyrimData, an)] : Directory.GetFiles(skyrimData, "Skyrim - *.bsa");
                int n = 0;
                foreach (var a in archives)
                    foreach (var f in Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, a).Files.Where(f => f.Path.Contains(opts["match"], StringComparison.OrdinalIgnoreCase)))
                    {
                        string dest = Path.Combine(outDir, f.Path);
                        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                        File.WriteAllBytes(dest, f.GetBytes());
                        Console.WriteLine($"{Path.GetFileName(a)}: {f.Path}");
                        n++;
                    }
                return n > 0 ? 0 : 1;
            }
            case "scene-find":
            {
                // Where references whose model path contains --model stand in --world of --plugin: count, centre, extent.
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var models = m.EnumerateMajorRecords().Where(r => r is IModeledGetter { Model: not null } && r is IPlaceableObjectGetter)
                    .ToDictionary(r => r.FormKey, r => ((IModeledGetter)r).Model!.File.DataRelativePath.Path);
                var ws = m.Worldspaces.First(w => w.EditorID == opts["world"]);
                var hits = ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).Concat(ws.TopCell is { } t ? [t] : [])
                    .SelectMany(c => c.Persistent.Concat(c.Temporary)).OfType<IPlacedObjectGetter>()
                    .Where(o => models.TryGetValue(o.Base.FormKey, out var p) && p.Contains(opts["model"], StringComparison.OrdinalIgnoreCase) && o.Placement is not null)
                    .Select(o => (Model: models[o.Base.FormKey], P: o.Placement!.Position)).ToList();
                if (hits.Count == 0) { Console.WriteLine("none"); return 1; }
                Console.WriteLine($"{hits.Count} refs, centre {hits.Average(h => h.P.X):F0},{hits.Average(h => h.P.Y):F0}, x {hits.Min(h => h.P.X):F0}..{hits.Max(h => h.P.X):F0}, y {hits.Min(h => h.P.Y):F0}..{hits.Max(h => h.P.Y):F0}, z {hits.Min(h => h.P.Z):F0}..{hits.Max(h => h.P.Z):F0}");
                foreach (var g in hits.GroupBy(h => h.Model).OrderByDescending(g => g.Count()).Take(8)) Console.WriteLine($"  {g.Count()} x {g.Key} e.g. {g.First().P}");
                return 0;
            }
            case "parts-switch":
            {
                // A part (Setup/Parts) switched --on or --off as the app's mod organizer does it (--part: its name): on, its
                // plugins whose masters are there and on; off, with every installed plugin that needs it.
                var part = Setup.Parts.All.First(p => p.Name.Equals(opts["part"], StringComparison.OrdinalIgnoreCase));
                var files = Setup.Parts.Files(part, skyrimData);
                if (files.Count == 0) { Console.Error.WriteLine($"{part.Name} isn't installed"); return 1; }
                if (opts.ContainsKey("on"))
                {
                    var (ready, blocked) = Setup.Parts.Switchable(skyrimData, files, f => Setup.PluginList.IsEnabled(f));
                    foreach (var line in Setup.PluginList.SetEnabled(ready, true)) Console.WriteLine($"{part.Name} on: {line}");
                    foreach (var (plugin, needs) in blocked) Console.WriteLine($"  left off: {plugin} (needs {needs})");
                }
                else
                {
                    var dependents = Setup.Parts.Dependents(skyrimData, files).Where(f => Setup.PluginList.IsEnabled(f)).ToList();
                    foreach (var line in Setup.PluginList.SetEnabled([.. files, .. dependents], false)) Console.WriteLine($"{part.Name} off: {line}");
                    if (dependents.Count > 0) Console.WriteLine($"  also off, they need it: {string.Join(", ", dependents)}");
                }
                return 0;
            }
            case "parts-deps":
            {
                // Dev aid: what the app's parts switches would do (Setup/Parts) for --off (';' list of plugins switched off):
                // the installed TamrielMerge plugins that need them and go off too; and with them off, which plugins of each
                // part could be switched on.
                var off = opts["off"].Split(';', StringSplitOptions.RemoveEmptyEntries);
                var deps = Setup.Parts.Dependents(skyrimData, off);
                Console.WriteLine($"off {string.Join(", ", off)} -> also off: {(deps.Count > 0 ? string.Join(", ", deps) : "nothing")}");
                var gone = off.Concat(deps).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var part in Setup.Parts.All)
                {
                    var (ready, blocked) = Setup.Parts.Switchable(skyrimData, Setup.Parts.Files(part, skyrimData), f => !gone.Contains(f));
                    if (blocked.Count > 0) Console.WriteLine($"  {part.Name}: could switch on {ready.Count}; left off: {string.Join(", ", blocked.Select(b => $"{b.Plugin} (needs {b.Needs})"))}");
                }
                return 0;
            }
            case "overland-doors":
            {
                // The overland's load doors (Daggerfall Towns.esm's gates, Daggerfall Dungeons.esm's entrances) joined to
                // the navmeshes of Daggerfall Land.esm in --dir (default out/convert), as "Daggerfall Overland Doors.esp".
                // --plugins (';' list of names in --dir) picks which of the two (convert passes the ones it built this run,
                // so a plugin left from an earlier run never becomes a master); else whichever are there.
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                // The navmeshes are in the last of the land's plugins (all of them, when the land is split).
                string land = Path.Combine(dir, Skyrim.Terrain.OverlandTiles.NavmeshPluginIn(dir) ?? Skyrim.Terrain.OverlandTiles.LandPluginName);
                var doorPlugins = (opts.TryGetValue("plugins", out var chosenDoors) ? chosenDoors.Split(';', StringSplitOptions.RemoveEmptyEntries)
                        : [Skyrim.DaggerfallTowns.PluginName, Skyrim.DaggerfallDungeons.PluginName])
                    .Select(p => Path.Combine(dir, p)).Where(File.Exists).ToList();
                if (!File.Exists(land) || doorPlugins.Count == 0) { Console.Error.WriteLine($"overland-doors: needs {land} and the towns or dungeons plugin"); return 1; }
                var r = Skyrim.Terrain.OverlandDoorLinks.Build(land, doorPlugins, Path.Combine(dir, Skyrim.Terrain.OverlandDoorLinks.PluginName), Console.WriteLine);
                return r.Linked > 0 ? 0 : 2;
            }
            case "menu-build":
            {
                // The main menu (Skyrim/Render/MenuBuilder): the red emblem and the intro video from --art (the sheets
                // of tools/menu/intro.py and the textures of tools/menu/red_logo.py) into --out, the video's screen
                // placed for the menu camera in --camera (tools/menu/menu_camera.json, from tools/menu/calibration.py).
                string art = Path.GetFullPath(opts.GetValueOrDefault("art", "out/menuart/menu"));
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/convert"));
                string camera = Path.GetFullPath(opts.GetValueOrDefault("camera", "tools/menu/menu_camera.json"));
                Skyrim.Render.MenuBuilder.Build(skyrimData, art, outDir, camera);
                return 0;
            }
            case "dupes":
            {
                // Dev aid: duplicates in --plugin that crash the game: a FormID used twice, and two cells on one grid square
                // of a worldspace (a merge gone wrong).
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var ids = m.EnumerateMajorRecords().GroupBy(r => r.FormKey).Where(g => g.Count() > 1).ToList();
                Console.WriteLine($"FormIDs used more than once: {ids.Count}");
                foreach (var g in ids.Take(10)) Console.WriteLine($"  {g.Key}: {string.Join(", ", g.Select(r => r.GetType().Name.Replace("BinaryOverlay", "")))}");
                foreach (var w in m.Worldspaces)
                {
                    var cells = w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).Where(c => c.Grid is not null)
                        .GroupBy(c => (c.Grid!.Point.X, c.Grid.Point.Y)).Where(g => g.Count() > 1).ToList();
                    Console.WriteLine($"{w.EditorID}: {cells.Count} grid squares with more than one cell");
                    foreach (var g in cells.Take(5)) Console.WriteLine($"  {g.Key}: {string.Join(", ", g.Select(c => c.FormKey))}");
                }
                return 0;
            }
            case "refs-near":
            {
                // Dev aid: what stands near a point of a worldspace (--world, --at x,y, --radius in units) in --plugin
                // (';'-separated, loaded over Skyrim's): each base's model and count, and for converted models (loose
                // files under the game's Data) whether the file is there and nifly loads it. For crashes on entering an area.
                var mods = new List<ISkyrimModDisposableGetter>();
                foreach (var m in new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" })
                    mods.Add(SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, m), SkyrimRelease.SkyrimSE));
                foreach (var f in opts["plugin"].Split(';')) mods.Add(SkyrimMod.CreateFromBinaryOverlay(f, SkyrimRelease.SkyrimSE));
                var models = new Dictionary<FormKey, (string Type, string? Model)>();
                foreach (var m in mods)
                    foreach (var r in m.EnumerateMajorRecords().OfType<IPlaceableObjectGetter>())
                        models[r.FormKey] = (r.GetType().Name.Replace("BinaryOverlay", ""), (r as IModeledGetter)?.Model?.File.DataRelativePath.Path);
                var at = opts["at"].Split(',').Select(v => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture)).ToArray();
                float radius = float.Parse(opts.GetValueOrDefault("radius", "12288"), System.Globalization.CultureInfo.InvariantCulture);
                var counts = new Dictionary<FormKey, int>();
                int npcs = 0;
                foreach (var m in mods.Skip(5))
                    foreach (var w in m.Worldspaces.Where(w => w.EditorID == opts["world"]))
                    {
                        var cells = w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).ToList();
                        if (w.TopCell is { } top) cells.Add(top);
                        foreach (var c in cells)
                            foreach (var r in c.Persistent.Concat(c.Temporary))
                            {
                                var pos = r switch { IPlacedObjectGetter o => o.Placement?.Position, IPlacedNpcGetter n => n.Placement?.Position, _ => null };
                                if (pos is not { } p || MathF.Abs(p.X - at[0]) > radius || MathF.Abs(p.Y - at[1]) > radius) continue;
                                if (r is IPlacedNpcGetter) { npcs++; continue; }
                                if (r is IPlacedObjectGetter po) counts[po.Base.FormKey] = counts.GetValueOrDefault(po.Base.FormKey) + 1;
                            }
                    }
                int missing = 0, broken = 0;
                foreach (var (k, n) in counts.OrderByDescending(c => c.Value))
                {
                    var (type, model) = models.GetValueOrDefault(k, ("?", null));
                    string state = "";
                    if (model is { Length: > 0 } && model.Contains("tamrielmerge", StringComparison.OrdinalIgnoreCase))
                    {
                        string file = Path.Combine(skyrimData, "Meshes", model);
                        if (!File.Exists(file)) { state = "MISSING"; missing++; }
                        else
                        {
                            using var nif = new nifly.NifFile();
                            state = nif.Load(file) != 0 ? "UNREADABLE" : $"{nif.GetShapes().Count()} shapes";
                            if (state == "UNREADABLE") broken++;
                        }
                    }
                    Console.WriteLine($"{n,5} {type,-14} {k} {model} {state}");
                }
                Console.WriteLine($"{counts.Values.Sum()} objects of {counts.Count} kinds, {npcs} actors; {missing} converted models missing, {broken} unreadable");
                return 0;
            }
            case "creature-animdata-check":
            {
                // Dev aid: the readers of the game's animation single files (CreatureBehavior) checked against the projects
                // that have files of their own too.
                var (compared, same, diffs) = new Skyrim.Creatures.CreatureBehavior(skyrimData).CheckSingleFiles();
                Console.WriteLine($"{compared} projects compared, {same} read the same from the single files");
                foreach (var d in diffs) Console.WriteLine($"  {d}");
                return same == compared ? 0 : 1;
            }
            case "creature-skeleton-fix":
            {
                // The creatures built before 2026-10-11 given their skeletons' bounding box and BSX flags (CreatureRig
                // .AddMissingRootData; without the box the game drew none of them), in --dir (a Data folder or the creature
                // output, default Skyrim's Data) for every creature in tools\creatures.
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", skyrimData));
                foreach (var cdir in Directory.GetDirectories(Path.Combine(Setup.Dependencies.Root, "creatures")))
                {
                    string id = Path.GetFileName(cdir);
                    if (Skyrim.Creatures.CreatureRig.AddMissingRootData(Path.Combine(dir, "meshes"), id)) Console.WriteLine($"{id}: bounding box and BSX added");
                }
                return 0;
            }
            case "game-lighting":
            {
                // The game's own lighting for a worldspace, every time of day, as JSON (Skyrim/Render/GameLighting).
                // --plugins: ';' plugin paths; --world: the worldspace's EditorID.
                var lmods = opts["plugins"].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(f => (ISkyrimModGetter)SkyrimMod.CreateFromBinaryOverlay(f, SkyrimRelease.SkyrimSE)).ToList();
                var world = lmods.SelectMany(m => m.Worldspaces).Last(w => w.EditorID == opts["world"]);
                var lighting = Skyrim.Render.GameLighting.Read(lmods.ToImmutableLinkCache(), world);
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(lighting, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
                foreach (var m in lmods) (m as IDisposable)?.Dispose();
                return lighting is null ? 2 : 0;
            }
            case "land-textures":
            {
                // Dev aid (user, 2026-10-11: "look for skyrims road textures"): the landscape textures of --plugin
                // (default Skyrim.esm) whose EditorID or diffuse matches --match (regex), with their diffuse.
                using var lm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, opts.GetValueOrDefault("plugin", "Skyrim.esm")), SkyrimRelease.SkyrimSE);
                var rx = new System.Text.RegularExpressions.Regex(opts.GetValueOrDefault("match", "."), System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                var sets = lm.TextureSets.ToDictionary(t => t.FormKey);
                foreach (var lt in lm.LandscapeTextures)
                {
                    string diffuse = sets.TryGetValue(lt.TextureSet.FormKey, out var ts) ? ts.Diffuse?.DataRelativePath.Path ?? "" : "";
                    if (rx.IsMatch(lt.EditorID ?? "") || rx.IsMatch(diffuse)) Console.WriteLine($"{lt.FormKey} {lt.EditorID,-32} {diffuse}");
                }
                return 0;
            }
            case "actor-models":
            {
                // Dev aid (user, 2026-10-11: "some creatures are invisible in dungeons"): the actors placed in the interior
                // cells whose EditorID contains --cell, loaded as the game loads them (Skyrim's masters, then --plugins:
                // ';' names in Data). Each base is followed through its templates (traits) and leveled lists to the actors
                // it can be; for each: its race, skeleton, skin and the body the skin gives that race (an addon for the race
                // the game matches: the race's armor race when it has one), and whether those files are there, loose or
                // in an archive. "NO BODY" or a missing file is an actor the game draws as nothing.
                var names = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }
                    .Concat(opts["plugins"].Split(';', StringSplitOptions.RemoveEmptyEntries)).ToList();
                var mods = names.Select(n => (ISkyrimModGetter)SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, n), SkyrimRelease.SkyrimSE)).ToList();
                var cache = mods.ToImmutableLinkCache();
                var archived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var b in Directory.GetFiles(skyrimData, "*.bsa"))
                    try { foreach (var f in Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, b).Files) archived.Add(f.Path.Replace('/', '\\')); }
                    catch (Exception) { }
                string Mesh(string? rel)
                {
                    if (string.IsNullOrEmpty(rel)) return "(none)";
                    rel = rel.Replace('/', '\\').TrimStart('\\');
                    if (!rel.StartsWith("meshes\\", StringComparison.OrdinalIgnoreCase)) rel = "meshes\\" + rel;
                    bool there = File.Exists(Path.Combine(skyrimData, rel)) || archived.Contains(rel);
                    return there ? rel : rel + "  <-- MISSING";
                }
                // The interior cells' placed actors (a later plugin's copy of a reference wins).
                var placed = new Dictionary<FormKey, (string Cell, IPlacedNpcGetter Ref)>();
                foreach (var m in mods)
                    foreach (var c in m.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).Where(c => (c.EditorID ?? "").Contains(opts["cell"], StringComparison.OrdinalIgnoreCase)))
                        foreach (var r in c.Persistent.Concat(c.Temporary).OfType<IPlacedNpcGetter>())
                            placed[r.FormKey] = (c.EditorID!, r);
                // The actors a base can be: through leveled lists, and through templates that give it its traits.
                IEnumerable<INpcGetter> Leaves(FormKey key, int depth)
                {
                    if (depth > 12) yield break;
                    if (cache.TryResolve<INpcGetter>(key, out var npc))
                    {
                        bool traitsFromTemplate = npc.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits) && !npc.Template.IsNull;
                        if (traitsFromTemplate) foreach (var l in Leaves(npc.Template.FormKey, depth + 1)) yield return l;
                        else yield return npc;
                    }
                    else if (cache.TryResolve<ILeveledNpcGetter>(key, out var list))
                        foreach (var e in list.Entries ?? [])
                            if (e.Data is { } d) foreach (var l in Leaves(d.Reference.FormKey, depth + 1)) yield return l;
                }
                var seen = new Dictionary<FormKey, int>();
                foreach (var (cell, r) in placed.Values)
                    foreach (var leaf in Leaves(r.Base.FormKey, 0).DistinctBy(l => l.FormKey))
                        seen[leaf.FormKey] = seen.GetValueOrDefault(leaf.FormKey) + 1;
                Console.WriteLine($"{placed.Count} actors placed in cells matching \"{opts["cell"]}\"; {seen.Count} actors they can be:");
                foreach (var (key, count) in seen.OrderBy(s => cache.Resolve<INpcGetter>(s.Key).EditorID))
                {
                    var npc = cache.Resolve<INpcGetter>(key);
                    var winner = cache.ResolveContext<INpc, INpcGetter>(key).ModKey;
                    string line = $"{npc.EditorID} ({key}, from {winner}) x{count}";
                    if (!cache.TryResolve<IRaceGetter>(npc.Race.FormKey, out var race)) { Console.WriteLine($"  {line}: race {npc.Race.FormKey} NOT FOUND"); continue; }
                    var armorRace = race.ArmorRace.IsNull ? race.FormKey : race.ArmorRace.FormKey;
                    string skel = Mesh(race.SkeletalModel?.Male?.File.DataRelativePath.Path);
                    var skinKey = !npc.WornArmor.IsNull ? npc.WornArmor.FormKey : race.Skin.FormKey;
                    string body = "no skin";
                    if (cache.TryResolve<IArmorGetter>(skinKey, out var skin))
                    {
                        var addons = skin.Armature.Select(a => cache.TryResolve<IArmorAddonGetter>(a.FormKey, out var aa) ? aa : null).OfType<IArmorAddonGetter>().ToList();
                        var fits = addons.Where(a => a.Race.FormKey == armorRace || a.AdditionalRaces.Any(x => x.FormKey == armorRace)).ToList();
                        body = fits.Count == 0
                            ? $"NO BODY: skin {skin.EditorID}'s {addons.Count} addons are for {string.Join(", ", addons.Select(a => cache.TryResolve<IRaceGetter>(a.Race.FormKey, out var ar) ? ar.EditorID : a.Race.FormKey.ToString()).Distinct())}, the game looks for {(cache.TryResolve<IRaceGetter>(armorRace, out var wr) ? wr.EditorID : armorRace.ToString())}"
                            : string.Join("; ", fits.Select(a => Mesh(a.WorldModel?.Male?.File.DataRelativePath.Path)));
                    }
                    Console.WriteLine($"  {line}: race {race.EditorID}{(race.ArmorRace.IsNull ? "" : $" (armor race {(cache.TryResolve<IRaceGetter>(armorRace, out var ar2) ? ar2.EditorID : armorRace.ToString())})")}, skeleton {skel}, body {body}");
                }
                foreach (var m in mods) (m as IDisposable)?.Dispose();
                return 0;
            }
            case "doors-of":
            {
                // Dev aid: the load doors in the interior cell holding --ref (hex FormID in --plugin, ';'-separated plugins
                // loaded for the lookup): each door's position and where it leads (worldspace and cell grid, or interior).
                var mods = opts["plugin"].Split(';').Select(f => SkyrimMod.CreateFromBinaryOverlay(f, SkyrimRelease.SkyrimSE)).ToList();
                var main = mods[0];
                var target = new FormKey(main.ModKey, Convert.ToUInt32(opts.GetValueOrDefault("ref", "0"), 16));
                var where = new Dictionary<FormKey, string>();
                ICellGetter? home = null;
                foreach (var m in mods)
                {
                    foreach (var c in m.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
                        foreach (var r in c.Persistent.Concat(c.Temporary))
                        {
                            where[r.FormKey] = $"interior {c.EditorID}";
                            if (r.FormKey == target || opts.TryGetValue("cell", out var cn) && home is null && (c.EditorID ?? "").Contains(cn, StringComparison.OrdinalIgnoreCase)) home = c;
                        }
                    foreach (var w in m.Worldspaces)
                    {
                        var cells = w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).ToList();
                        if (w.TopCell is { } top) cells.Add(top);
                        foreach (var c in cells)
                            foreach (var r in c.Persistent.Concat(c.Temporary))
                                where[r.FormKey] = $"{w.EditorID} cell {c.Grid?.Point.X},{c.Grid?.Point.Y}";
                    }
                }
                if (home is null) { Console.WriteLine($"ref not in an interior: {where.GetValueOrDefault(target, "not found")}"); return 1; }
                Console.WriteLine($"cell {home.EditorID} ({home.FormKey})");
                foreach (var r in home.Persistent.Concat(home.Temporary).OfType<IPlacedObjectGetter>().Where(o => !string.IsNullOrEmpty(o.EditorID)))
                    Console.WriteLine($"  named {r.EditorID} {r.FormKey} at {r.Placement?.Position}");
                foreach (var d in home.Persistent.Concat(home.Temporary).OfType<IPlacedObjectGetter>().Where(o => o.TeleportDestination is not null))
                {
                    var t = d.TeleportDestination!;
                    Console.WriteLine($"  door {d.FormKey} at {d.Placement?.Position} -> {where.GetValueOrDefault(t.Door.FormKey, "?")} door {t.Door.FormKey} arrive {t.Position}");
                }
                // --all: also the Daggerfall dungeon doors (DoorPlacer's Imperial doors), with their scale.
                if (opts.ContainsKey("all"))
                    foreach (var d in home.Persistent.Concat(home.Temporary).OfType<IPlacedObjectGetter>().Where(o => Skyrim.DoorPlacer.BaseDoors.Contains(o.Base.FormKey)))
                        Console.WriteLine($"  dungeon door {d.FormKey} {(d.Base.FormKey.ID == 0x439C2 ? "double" : "single")} at {d.Placement?.Position} scale {d.Scale ?? 1:F2}");
                if (opts.ContainsKey("all"))
                    foreach (var d in home.Persistent.Concat(home.Temporary).OfType<IPlacedObjectGetter>().Where(o => o.Base.FormKey.ModKey.FileName == "Skyrim.esm" && o.Base.FormKey.ID is 0xE3FC7 or 0x91712 or 0x8F965 or 0xD068D or 0xAA93D or 0x39DF9 or 0xAA93F))
                        Console.WriteLine($"  statue {d.FormKey} base {d.Base.FormKey.ID:X} at {d.Placement?.Position} heading {d.Placement!.Rotation.Z * 180 / MathF.PI:F0} scale {d.Scale ?? 1:F2}");
                return 0;
            }
            case "rmb-dump":
            {
                // Dev aid: an RMB block's buildings (type, position, turn) with their outside models, and its loose models.
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var models = DfModel.LoadAll(arena2);
                var tb = DfTownBlock.Load(bsa, opts["block"]);
                foreach (var b in tb.Buildings)
                    Console.WriteLine($"building {b.Index} {b.Data.Type} at {b.XPos},{b.ZPos} turn {b.YRotation}: outside {string.Join(" ", b.Exterior.Models.Select(m => $"{m.ModelId}{(models.ContainsKey(m.ModelId) ? "" : "(missing)")}@{m.Position.X:F0},{m.Position.Z:F0}r{m.YRotation}t{m.ObjectType}"))}; flats {b.Exterior.Flats.Count}; inside models {b.Interior.Models.Count}");
                Console.WriteLine($"loose: {string.Join(" ", tb.MiscModels.Select(m => $"{m.ModelId}{(models.ContainsKey(m.ModelId) ? "" : "(missing)")}@{m.Position.X:F0},{m.Position.Z:F0}r{m.YRotation}"))}");
                return 0;
            }
            case "creature-build":
            {
                // A creature with its own skeleton (Skyrim/Creatures/CreatureRig) from tools\creatures\<--creature>: exported
                // by Blender (export_creature.py, unless work\creature.json is newer than the config), then written as Skyrim
                // SE files under --out (a Data folder; default out\creatures).
                string id = opts["creature"];
                string dir = Path.Combine(Setup.Dependencies.Root, "creatures", id);
                string config = Path.Combine(dir, "creature.json"), work = Path.Combine(dir, "work"), exported = Path.Combine(work, "creature.json");
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/creatures"));
                Skyrim.Creatures.CreatureRig rig;
                using (var src = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config)))
                {
                    var sr = src.RootElement;
                    if (sr.TryGetProperty("game", out var game) && game.GetString() == "morrowind")
                    {
                        // A Morrowind creature from the user's own install (user: "morrowind has a guar model"): its model and
                        // .kf (MwCreatureSource), textures upscaled where the cache has them, remastered when the config says.
                        var mwData = Morrowind.Tes3Data.Open(GameInstalls.DataDir(Game.Morrowind) ?? throw new InvalidOperationException("Morrowind isn't installed"));
                        string cache = Path.Combine(Setup.Dependencies.Root, "upscale", "out");
                        if (Directory.Exists(cache)) Morrowind.MwTextures.UpscaleCache = cache;
                        string mwTexDir = Path.Combine(work, "mwtextures");
                        var problems = new List<string>();
                        rig = Skyrim.Creatures.MwCreatureSource.Build(mwData, sr.GetProperty("model").GetString()!, new Morrowind.MwTextures(mwData, mwTexDir), mwTexDir, problems);
                        // --no-remaster: as the game has it (the remaster runs Blender and the texture model).
                        if (sr.TryGetProperty("remaster", out var levels) && !opts.ContainsKey("no-remaster"))
                            Skyrim.Creatures.CreatureRemaster.Run(rig, work, levels.GetInt32(), problems);
                        foreach (var pr in problems) Console.WriteLine("  problem: " + pr);
                    }
                    else
                    {
                        if (!File.Exists(exported) || File.GetLastWriteTimeUtc(exported) < File.GetLastWriteTimeUtc(config) || opts.ContainsKey("export"))
                        {
                            string blender = Setup.Dependencies.Blender ?? throw new InvalidOperationException("Blender is needed: run setup");
                            int code = Setup.ToolScripts.Run(blender, ["-b", "--disable-autoexec", "--python", Path.Combine(Setup.Dependencies.Root, "creatures", "export_creature.py"), "--", config, work]);
                            if (code != 0 || !File.Exists(exported)) { Console.WriteLine($"export failed ({code})"); return 1; }
                        }
                        rig = Skyrim.Creatures.CreatureRig.Load(exported);
                    }
                }
                rig.EnsureDeath();   // a fall for models without a death of their own
                var files = rig.Write(outDir, id);
                // Behaviour from the template the config names (default the mudcrab's), its look-at on the config's head bone.
                using (var cfg = System.Text.Json.JsonDocument.Parse(File.ReadAllText(config)))
                {
                    string templateName = cfg.RootElement.TryGetProperty("template", out var tp) ? tp.GetString()! : "mudcrab";
                    var template = Skyrim.Creatures.CreatureBehavior.Templates.GetValueOrDefault(templateName) ?? throw new InvalidDataException($"unknown behaviour template {templateName}");
                    string? head = cfg.RootElement.TryGetProperty("head", out var hp) ? hp.GetString() : null;
                    int headBone = head is null ? -1 : rig.Bones.FindIndex(b => b.Name == head);
                    var behaviour = new Skyrim.Creatures.CreatureBehavior(skyrimData);
                    files.AddRange(behaviour.Write(outDir, id, rig, template, headBone));
                    Console.WriteLine($"  behaviour from {templateName}: {behaviour.DeathClipsRewired} death clips play its death");
                }
                Console.WriteLine($"{id}: {rig.Bones.Count} bones, {rig.Meshes.Sum(m => m.Tris.Length)} triangles, {rig.Clips.Count} animations ({string.Join(", ", rig.Clips.Select(c => $"{c.Key} {c.Value.Frames.Length}"))})");
                foreach (var f in files) Console.WriteLine($"  {f} ({new FileInfo(Path.Combine(outDir, f)).Length / 1024} KB)");
                return 0;
            }
            case "creature-pose":
            {
                // Dev aid: proves a creature-build creature offline. Reads back its body NIF (skin binds, weights) and an
                // animation .hkx, skins the mesh at --frames (comma list) the way the engine does (linear blend: bone pose
                // x skin-to-bone x vertex) and draws each pose from the side, front and above to --out (PNG).
                string id = opts["creature"];
                string data = Path.GetFullPath(opts.GetValueOrDefault("data", "out/creatures"));
                string folder = Path.Combine(data, Skyrim.Creatures.CreatureRig.MeshFolder(id));
                var skelRoot = (HKX2.hkRootLevelContainer)new HKX2.PackFileDeserializer().Deserialize(new HKX2.BinaryReaderEx(File.ReadAllBytes(Path.Combine(folder, "character assets", "skeleton.hkx"))));
                var skel = ((HKX2.hkaAnimationContainer)skelRoot.m_namedVariants[0]!.m_variant!).m_skeletons[0];
                var animRoot = (HKX2.hkRootLevelContainer)new HKX2.PackFileDeserializer().Deserialize(new HKX2.BinaryReaderEx(File.ReadAllBytes(Path.Combine(folder, "animations", opts.GetValueOrDefault("anim", "walk") + ".hkx"))));
                var anim = (HKX2.hkaInterleavedUncompressedAnimation)((HKX2.hkaAnimationContainer)animRoot.m_namedVariants[0]!.m_variant!).m_animations[0];
                int tracks = anim.m_numberOfTransformTracks, frameCount = anim.m_transforms.Count / tracks;
                System.Numerics.Matrix4x4 M(System.Numerics.Matrix4x4 qs) =>
                    System.Numerics.Matrix4x4.CreateFromQuaternion(new System.Numerics.Quaternion(qs.M21, qs.M22, qs.M23, qs.M24)) * System.Numerics.Matrix4x4.CreateTranslation(qs.M11, qs.M12, qs.M13);
                using var nif = new nifly.NifFile();
                nif.Load(Path.Combine(folder, id + ".nif"));
                var hdr = nif.GetHeader();
                var frames = opts.GetValueOrDefault("frames", "0").Split(',').Select(int.Parse).Select(f => Math.Clamp(f, 0, frameCount - 1)).ToList();
                using var cache = new nifly.niflycpp.BlockCache(hdr);
                const int W = 360;
                var rgb = new byte[W * 3 * W * frames.Count * 3];
                for (int fi = 0; fi < frames.Count; fi++)
                {
                    // Bone poses to the root for this frame (row-vector matrices: local * parent).
                    var world = new System.Numerics.Matrix4x4[tracks];
                    for (int b = 0; b < tracks; b++)
                    {
                        var local = M(anim.m_transforms[frames[fi] * tracks + b]);
                        world[b] = skel.m_parentIndices[b] < 0 ? local : local * world[skel.m_parentIndices[b]];
                    }
                    var boneIndex = skel.m_bones.Select((bn, i) => (bn.m_name, i)).ToDictionary(x => x.m_name, x => x.i);
                    var pts = new List<System.Numerics.Vector3>();
                    foreach (var shape in nif.GetShapes())
                    {
                        var verts = nif.GetVertsForShape(shape);
                        using var names = new nifly.vectorstring();
                        nif.GetShapeBoneList(shape, names);
                        var posed = new System.Numerics.Vector3[verts.Count];
                        var inst = cache.EditableBlockById<nifly.NiSkinInstance>(shape.SkinInstanceRef().index);
                        var sd = cache.EditableBlockById<nifly.NiSkinData>(inst.dataRef.index);
                        for (int i = 0; i < names.Count; i++)
                        {
                            var stb = new nifly.MatTransform();
                            nif.GetShapeTransformSkinToBone(shape, (uint)i, stb);
                            var r = stb.rotation;
                            var cx = r.opMult(new nifly.Vector3(1, 0, 0)); var cy = r.opMult(new nifly.Vector3(0, 1, 0)); var cz = r.opMult(new nifly.Vector3(0, 0, 1));
                            // nifly: p' = R p + t (columns cx, cy, cz) -> row-vector matrix rows are the columns.
                            var skin = new System.Numerics.Matrix4x4(cx.x, cx.y, cx.z, 0, cy.x, cy.y, cy.z, 0, cz.x, cz.y, cz.z, 0, stb.translation.x, stb.translation.y, stb.translation.z, 1);
                            var full = skin * world[boneIndex[names[i]]];
                            foreach (var sw in sd.bones[i].vertexWeights)
                            {
                                var v = verts[sw.index];
                                posed[sw.index] += System.Numerics.Vector3.Transform(new System.Numerics.Vector3(v.x, v.y, v.z), full) * sw.weight;
                            }
                        }
                        pts.AddRange(posed);
                    }
                    // Three views, scaled to fit: side (x = -Y... looking along +X), front (along -Y), top.
                    var lo = pts.Aggregate(System.Numerics.Vector3.Min); var hi = pts.Aggregate(System.Numerics.Vector3.Max);
                    float span = MathF.Max(hi.X - lo.X, MathF.Max(hi.Y - lo.Y, hi.Z - lo.Z)) * 1.1f;
                    var centre = (lo + hi) / 2;
                    for (int view = 0; view < 3; view++)
                        foreach (var p in pts)
                        {
                            var d = p - centre;
                            (float u, float v, float depth) = view switch { 0 => (d.Y, d.Z, d.X), 1 => (d.X, d.Z, -d.Y), _ => (d.X, d.Y, d.Z) };
                            int px = (int)((u / span + 0.5f) * W), py = (int)((0.5f - v / span) * W);
                            if (px < 0 || py < 0 || px >= W || py >= W) continue;
                            byte shade = (byte)Math.Clamp(140 + depth / span * 200, 30, 255);
                            int q = ((fi * W + py) * W * 3 + view * W + px) * 3;
                            if (rgb[q] < shade) { rgb[q] = shade; rgb[q + 1] = (byte)(shade * 0.85f); rgb[q + 2] = (byte)(shade * 0.6f); }
                        }
                    Console.WriteLine($"frame {frames[fi]}: posed {pts.Count} vertices, bounds {lo} .. {hi}");
                }
                Util.Png.WriteRgb(opts.GetValueOrDefault("out", "out/creature-pose.png"), W * 3, W * frames.Count, rgb);
                return 0;
            }
            case "mw-creature":
            {
                // A Morrowind creature with its own skeleton (Skyrim/Creatures/MwCreatureSource) from its model (--model,
                // e.g. r\guar.nif) and x<model>.kf, written as Skyrim SE files under --out (default out\creatures) as --id;
                // --template names the behaviour template (default mudcrab), --head its look-at bone.
                var mwData = Morrowind.Tes3Data.Open(GameInstalls.DataDir(Game.Morrowind)!);
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/creatures"));
                // Morrowind's textures upscaled 4x where the upscale cache has them (tools\upscale\out).
                string cache = Path.Combine(Setup.Dependencies.Root, "upscale", "out");
                if (Directory.Exists(cache)) Morrowind.MwTextures.UpscaleCache = cache;
                string mwTexDir = Path.Combine(outDir, "_mwtextures");
                var mwTex = new Morrowind.MwTextures(mwData, mwTexDir);
                var problems = new List<string>();
                var rig = Skyrim.Creatures.MwCreatureSource.Build(mwData, opts["model"], mwTex, mwTexDir, problems);
                // --remaster [levels]: the body subdivided and smoothed, the textures made into matte PBR pairs.
                if (opts.TryGetValue("remaster", out var lv))
                    Skyrim.Creatures.CreatureRemaster.Run(rig, Path.Combine(Setup.Dependencies.Root, "creatures", opts["id"], "work"), int.TryParse(lv, out int n) ? n : 1, problems);
                rig.EnsureDeath();
                foreach (var pr in problems) Console.WriteLine("  problem: " + pr);
                string id = opts["id"];
                var files = rig.Write(outDir, id);
                int headBone = opts.TryGetValue("head", out var hb) ? rig.Bones.FindIndex(b => b.Name.Equals(hb, StringComparison.OrdinalIgnoreCase)) : -1;
                files.AddRange(new Skyrim.Creatures.CreatureBehavior(skyrimData).Write(outDir, id, rig, Skyrim.Creatures.CreatureBehavior.Mudcrab, headBone));
                Console.WriteLine($"{id}: {rig.Bones.Count} bones, {rig.Meshes.Count} meshes, {rig.Meshes.Sum(m => m.Tris.Length)} triangles, clips {string.Join(", ", rig.Clips.Select(c => $"{c.Key} {c.Value.Frames.Length}"))}; {files.Count} files");
                Console.WriteLine($"  bones: {string.Join(", ", rig.Bones.Select(b => b.Name).Take(40))}");
                return 0;
            }
            case "creature-plugin":
            {
                // The creature models' plugins (Skyrim/Creatures/CreatureRecords), one per game ("Daggerfall Creatures.esp" over
                // Daggerfall.esm, ...), for the creatures in --creatures (ids, comma list; each tools\creatures\<id>\creature.json
                // gives name, templateRace, actors and its "game", Daggerfall's when none) over the game plugins in --plugins
                // (';' list), into --out (default out\creatures). A game not among --plugins (not converted) gets none.
                var masterMods = new[] { "Skyrim.esm", "Dawnguard.esm", "Dragonborn.esm" }.Select(m => (ISkyrimModGetter)SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, m), SkyrimRelease.SkyrimSE)).ToList();
                var games = opts["plugins"].Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p2 => SkyrimMod.CreateFromBinaryOverlay(p2, SkyrimRelease.SkyrimSE)).ToList();
                var byGame = new Dictionary<string, Skyrim.Creatures.CreatureRecords>(StringComparer.OrdinalIgnoreCase);
                foreach (string id in opts["creatures"].Split(',', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var cfg = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(Setup.Dependencies.Root, "creatures", id, "creature.json")));
                    var r = cfg.RootElement;
                    string gamePlugin = Skyrim.Creatures.CreatureRecords.GamePlugin(r.TryGetProperty("game", out var gp) ? gp.GetString() : null);
                    var game = games.FirstOrDefault(g => g.ModKey.FileName.String.Equals(gamePlugin, StringComparison.OrdinalIgnoreCase));
                    if (game is null) { Console.WriteLine($"{id}: {gamePlugin} not converted, left out"); continue; }
                    if (!byGame.TryGetValue(gamePlugin, out var records))
                    {
                        records = new Skyrim.Creatures.CreatureRecords(masterMods, Skyrim.Creatures.CreatureRecords.PluginFor(gamePlugin));
                        records.AddGame(game);
                        byGame[gamePlugin] = records;
                    }
                    string race = r.TryGetProperty("templateRace", out var tr) ? tr.GetString()!
                        : Skyrim.Creatures.CreatureBehavior.Templates[r.TryGetProperty("template", out var tt) ? tt.GetString()! : "mudcrab"].Race;
                    int n = records.Add(id, r.GetProperty("name").GetString()!, race, r.GetProperty("actors").EnumerateArray().Select(a2 => a2.GetString()!));
                    Console.WriteLine($"{id}: race, skin, {n} actors switched in {records.PluginName}");
                }
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/creatures"));
                Directory.CreateDirectory(outDir);
                var written = byGame.Values.Select(rec => rec.Write(outDir)).ToList();
                foreach (var w in written) Console.WriteLine($"wrote {w}");
                foreach (var g in games) g.Dispose();
                // --install: the creatures' files (meshes, textures, animation data) and the plugin into Skyrim's Data,
                // the plugin switched on in Skyrim's list and Mod Organizer's.
                if (opts.ContainsKey("install"))
                {
                    int copied = 0;
                    // An earlier install of these creatures goes first (another template left other files).
                    foreach (string cid in opts["creatures"].Split(',', StringSplitOptions.RemoveEmptyEntries))
                    {
                        string installed = Path.Combine(skyrimData, Skyrim.Creatures.CreatureRig.MeshFolder(cid));
                        if (Directory.Exists(installed)) Directory.Delete(installed, recursive: true);
                        string project = Skyrim.Creatures.CreatureBehavior.ProjectName(cid);
                        foreach (var stale in Skyrim.Creatures.CreatureBehavior.StaleData(skyrimData, project)) if (File.Exists(stale)) File.Delete(stale);
                        string setDir = Path.Combine(skyrimData, "meshes", "animationsetdata", project + "Data");
                        if (Directory.Exists(setDir)) Directory.Delete(setDir, recursive: true);
                    }
                    foreach (string sub in new[] { "meshes", "textures" })
                    {
                        string from = Path.Combine(outDir, sub);
                        if (!Directory.Exists(from)) continue;
                        foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
                        {
                            string to = Path.Combine(skyrimData, Path.GetRelativePath(outDir, f));
                            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                            File.Copy(f, to, overwrite: true);
                            copied++;
                        }
                    }
                    var names = written.Select(w => Path.GetFileName(w)!).ToList();
                    foreach (var name in names) File.Copy(Path.Combine(outDir, name), Path.Combine(skyrimData, name), overwrite: true);
                    Console.WriteLine($"installed {copied} files and {string.Join(", ", names)}; {string.Join("; ", Setup.PluginList.Enable(names))}");
                    // The single plugin earlier builds made over every game is switched off (not deleted: the game folder is
                    // left for the user to tidy).
                    if (File.Exists(Path.Combine(skyrimData, Skyrim.Creatures.CreatureRecords.OldPluginName)))
                        Console.WriteLine($"{Skyrim.Creatures.CreatureRecords.OldPluginName} is replaced by these: {string.Join("; ", Setup.PluginList.SetEnabled([Skyrim.Creatures.CreatureRecords.OldPluginName], false))}; it can be deleted");
                }
                return 0;
            }
            case "rot-convention":
            {
                // Dev aid: one bone's local rotation in a vanilla skeleton.hkx (quaternion) and its skeleton.nif (matrix, as
                // the images of the axes), to settle how the two conventions relate. --hkx, --nif (files), --bone.
                var root = (HKX2.hkRootLevelContainer)new HKX2.PackFileDeserializer().Deserialize(new HKX2.BinaryReaderEx(File.ReadAllBytes(opts["hkx"])));
                var sk = ((HKX2.hkaAnimationContainer)root.m_namedVariants[0].m_variant!).m_skeletons[0];
                int bi = sk.m_bones.Select(b => b.m_name).ToList().IndexOf(opts["bone"]);
                var qs = sk.m_referencePose[bi];
                var q = new System.Numerics.Quaternion(qs.M21, qs.M22, qs.M23, qs.M24);
                Console.WriteLine($"hkx {opts["bone"]}: t ({qs.M11:F3},{qs.M12:F3},{qs.M13:F3}) q {q}");
                foreach (var axis in new[] { System.Numerics.Vector3.UnitX, System.Numerics.Vector3.UnitY, System.Numerics.Vector3.UnitZ })
                    Console.WriteLine($"  q rotates {axis} to {System.Numerics.Vector3.Transform(axis, q)}");
                using var nif = new nifly.NifFile();
                nif.Load(opts["nif"]);
                var mt = new nifly.MatTransform();
                nif.GetNodeTransformToParent(opts["bone"], mt);
                Console.WriteLine($"nif {opts["bone"]}: t ({mt.translation.x:F3},{mt.translation.y:F3},{mt.translation.z:F3})");
                foreach (var axis in new[] { new nifly.Vector3(1, 0, 0), new nifly.Vector3(0, 1, 0), new nifly.Vector3(0, 0, 1) })
                {
                    var r = mt.rotation.opMult(axis);
                    Console.WriteLine($"  R * ({axis.x},{axis.y},{axis.z}) = ({r.x:F3},{r.y:F3},{r.z:F3})");
                }
                return 0;
            }
            case "hkx-summary":
            {
                // Dev aid: every Havok object reachable in an .hkx (--file: on disk or in the game's archives), counted by
                // class; the clips (hkbClipGenerator animation names), the character's file lists, and every field naming
                // a bone (bone index > -1 / bone names), which a borrowed behaviour graph would tie to its own skeleton.
                byte[] bytes = File.Exists(opts["file"]) ? File.ReadAllBytes(opts["file"])
                    : Directory.GetFiles(skyrimData, "Skyrim - *.bsa").Select(b => Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, b))
                        .SelectMany(a2 => a2.Files).FirstOrDefault(f => f.Path.Equals(opts["file"], StringComparison.OrdinalIgnoreCase))?.GetBytes()
                      ?? throw new FileNotFoundException(opts["file"]);
                var root = new HKX2.PackFileDeserializer().Deserialize(new HKX2.BinaryReaderEx(bytes));
                var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
                var counts = new SortedDictionary<string, int>();
                var notes = new List<string>();
                void Walk(object? o, string path)
                {
                    if (o is null || o is string || o.GetType().IsPrimitive || o.GetType().IsEnum || o is System.Numerics.Vector4 || o is System.Numerics.Matrix4x4) return;
                    if (o is System.Collections.IEnumerable list)
                    {
                        int i = 0;
                        foreach (var item in list) Walk(item, $"{path}[{i++}]");
                        return;
                    }
                    if (!seen.Add(o)) return;
                    var t = o.GetType();
                    counts[t.Name] = counts.GetValueOrDefault(t.Name) + 1;
                    foreach (var prop in t.GetProperties().Where(p2 => p2.Name.StartsWith("m_")))
                    {
                        object? v;
                        try { v = prop.GetValue(o); } catch { continue; }
                        string n = prop.Name;
                        if (n.Contains("bone", StringComparison.OrdinalIgnoreCase) && v is short or int or sbyte && Convert.ToInt32(v) >= 0)
                            notes.Add($"bone index {t.Name}.{n} = {v}");
                        if (n.Contains("bone", StringComparison.OrdinalIgnoreCase) && v is System.Collections.IEnumerable bl && v is not string)
                        {
                            var items = bl.Cast<object>().Take(12).ToList();
                            if (items.Count > 0 && items[0] is short or int or string) notes.Add($"bones {t.Name}.{n} = {string.Join(",", items)}");
                        }
                        if (t.Name == "hkbClipGenerator" && n == "m_animationName") notes.Add($"clip {v}");
                        if (opts.ContainsKey("states") && t.Name == "hkbStateMachineStateInfo" && n == "m_name") notes.Add($"state {v}");
                        if (opts.ContainsKey("states") && t.Name == "hkbClipGenerator" && n == "m_name" && o is HKX2.hkbClipGenerator cg) notes.Add($"clipgen {v} -> {cg.m_animationName} (mode {cg.m_mode})");
                        if (t.Name.EndsWith("StringData") && v is System.Collections.IEnumerable sl && v is not string)
                            notes.Add($"{t.Name}.{n}: {string.Join(" | ", sl.Cast<object>().Take(60))}");
                        if (t.Name.EndsWith("StringData") && v is string sv) notes.Add($"{t.Name}.{n} = {sv}");
                        Walk(v, path + "." + n);
                    }
                }
                Walk(root, "root");
                Console.WriteLine(string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}")));
                foreach (var g in notes.GroupBy(x => x).OrderBy(g => g.Key)) Console.WriteLine($"  {g.Key}{(g.Count() > 1 ? $" (x{g.Count()})" : "")}");
                return 0;
            }
            case "hkx-roundtrip":
            {
                // Dev aid: reads an SE .hkx (--file: on disk or a path in the game's archives) with HKX2, lists what it holds,
                // writes it back as Skyrim SE and compares the bytes (proves the library writes files the game reads).
                byte[] original = File.Exists(opts["file"]) ? File.ReadAllBytes(opts["file"])
                    : Directory.GetFiles(skyrimData, "Skyrim - *.bsa").Select(b => Mutagen.Bethesda.Archives.Archive.CreateReader(Mutagen.Bethesda.GameRelease.SkyrimSE, b))
                        .SelectMany(a2 => a2.Files).FirstOrDefault(f => f.Path.Equals(opts["file"], StringComparison.OrdinalIgnoreCase))?.GetBytes()
                      ?? throw new FileNotFoundException(opts["file"]);
                var des = new HKX2.PackFileDeserializer();
                var root = des.Deserialize(new HKX2.BinaryReaderEx(original));
                if (root is HKX2.hkRootLevelContainer rc)
                    foreach (var v in rc.m_namedVariants)
                    {
                        Console.WriteLine($"  {v.m_name} ({v.m_className}): {v.m_variant?.GetType().Name}");
                        if (v.m_variant is HKX2.hkaAnimationContainer ac)
                        {
                            foreach (var sk in ac.m_skeletons) Console.WriteLine($"    skeleton {sk.m_name}: {sk.m_bones.Count} bones, first {string.Join(", ", sk.m_bones.Take(6).Select(b => b.m_name))}");
                            foreach (var an in ac.m_animations) Console.WriteLine($"    animation {an.GetType().Name}: {an.m_duration:F2} s, {an.m_numberOfTransformTracks} tracks");
                        }
                    }
                using var ms = new MemoryStream();
                new HKX2.PackFileSerializer().Serialize(root, new HKX2.BinaryWriterEx(ms), HKX2.HKXHeader.SkyrimSE());
                var written = ms.ToArray();
                int first = Enumerable.Range(0, Math.Min(written.Length, original.Length)).FirstOrDefault(i => written[i] != original[i], -1);
                Console.WriteLine($"read {original.Length} bytes, wrote {written.Length}: {(written.AsSpan().SequenceEqual(original) ? "identical" : $"differ (first at {first})")}");
                if (opts.TryGetValue("out", out var outPath)) File.WriteAllBytes(outPath, written);
                return 0;
            }
            case "df-town-check":
            {
                // Dev aid (user, 2026-10-10: "make sure the buildings are the rotation they need to be in"): each town
                // block's buildings, as our layout places them seen from above, against the block's own automap (64 x 64,
                // 64 units a pixel). The automap's orientation is found once (the best of its 8 turns and flips over all
                // blocks); then each building's share of footprint on the automap's buildings, the poorest listed.
                // --name TOWN (or --all, --max N towns).
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var models = DfModel.LoadAll(arena2);
                var towns = DfLocations.LoadAll(arena2).Where(l => l.ExteriorBlocks.Length > 0
                    && (opts.ContainsKey("all") || l.Name.Equals(opts.GetValueOrDefault("name", "Daggerfall"), StringComparison.OrdinalIgnoreCase)))
                    .Take(int.Parse(opts.GetValueOrDefault("max", "100000"))).ToList();
                var blockNames = towns.SelectMany(l => l.ExteriorBlocks.Select(b => DfTownBlock.Resolve(bsa, b))).Where(n => n is not null).Distinct().ToList();
                // Per block: the automap, and each building's footprint pixels (block space, x east, z north).
                var blocks = new List<(string Name, byte[] Map, List<(int Index, HashSet<(int, int)> Pixels)> Buildings)>();
                foreach (var name in blockNames)
                {
                    var tb = DfTownBlock.Load(bsa, name!);
                    if (tb.AutoMap.Length != 64 * 64) continue;
                    var ext = DfTownLayout.Exterior(tb);
                    var foot = new Dictionary<int, HashSet<(int, int)>>();
                    foreach (var o in ext.Objects.Where(o => o.Type == DfRdbObjectType.Model && models.ContainsKey(o.ModelId)))
                    {
                        var m = DfDungeon.ObjectTransform(o, System.Numerics.Vector3.Zero);
                        if (!foot.TryGetValue(o.RecordOffset, out var set)) foot[o.RecordOffset] = set = [];
                        foreach (var p in models[o.ModelId].Polygons.Where(p => p.Points.Length >= 3))
                        {
                            var pts = p.Points.Select(v => System.Numerics.Vector3.Transform(v, m)).ToArray();
                            // Edges too, so upright walls (no area from above) show as lines.
                            for (int i = 0; i < pts.Length; i++)
                            {
                                var e0 = pts[i]; var e1 = pts[(i + 1) % pts.Length];
                                int steps = (int)(MathF.Sqrt((e1.X - e0.X) * (e1.X - e0.X) + (e1.Z - e0.Z) * (e1.Z - e0.Z)) / 16) + 1;
                                for (int k = 0; k <= steps; k++)
                                {
                                    var q = System.Numerics.Vector3.Lerp(e0, e1, k / (float)steps);
                                    int qx = (int)MathF.Floor(q.X / 64), qz = (int)MathF.Floor(q.Z / 64);
                                    if (qx is >= 0 and < 64 && qz is >= 0 and < 64) set.Add((qx, qz));
                                }
                            }
                            for (int i = 1; i + 1 < pts.Length; i++)
                            {
                                var (a2, b2, c2) = (pts[0], pts[i], pts[i + 1]);
                                int x0 = (int)MathF.Floor(MathF.Min(a2.X, MathF.Min(b2.X, c2.X)) / 64), x1 = (int)MathF.Floor(MathF.Max(a2.X, MathF.Max(b2.X, c2.X)) / 64);
                                int z0 = (int)MathF.Floor(MathF.Min(a2.Z, MathF.Min(b2.Z, c2.Z)) / 64), z1 = (int)MathF.Floor(MathF.Max(a2.Z, MathF.Max(b2.Z, c2.Z)) / 64);
                                float d = (b2.Z - c2.Z) * (a2.X - c2.X) + (c2.X - b2.X) * (a2.Z - c2.Z);
                                for (int pz = Math.Max(z0, 0); pz <= Math.Min(z1, 63); pz++)
                                    for (int px = Math.Max(x0, 0); px <= Math.Min(x1, 63); px++)
                                    {
                                        float x = px * 64 + 32, z = pz * 64 + 32;
                                        if (MathF.Abs(d) < 1e-6f) continue;
                                        float w0 = ((b2.Z - c2.Z) * (x - c2.X) + (c2.X - b2.X) * (z - c2.Z)) / d, w1 = ((c2.Z - a2.Z) * (x - c2.X) + (a2.X - c2.X) * (z - c2.Z)) / d;
                                        if (w0 >= 0 && w1 >= 0 && w0 + w1 <= 1) set.Add((px, pz));
                                    }
                            }
                        }
                    }
                    // Loose block models (walls, fountains: record -1) count as cover but aren't buildings of their own.
                    blocks.Add((name!, tb.AutoMap, foot.Select(kv => (kv.Key, kv.Value)).Where(f => f.Value.Count > 0).ToList()));
                }
                // The automap pixel under block pixel (x, z) for each of the 8 orientations.
                static (int, int) Orient(int k, int x, int z) => k switch
                {
                    0 => (x, z), 1 => (63 - x, z), 2 => (x, 63 - z), 3 => (63 - x, 63 - z),
                    4 => (z, x), 5 => (63 - z, x), 6 => (z, 63 - x), _ => (63 - z, 63 - x),
                };
                bool On(byte[] map, int k, int x, int z) { var (u, v) = Orient(k, x, z); return map[v * 64 + u] != 0; }
                var scores = Enumerable.Range(0, 8).Select(k => blocks.Sum(b => b.Buildings.Sum(f => f.Pixels.Count(p => On(b.Map, k, p.Item1, p.Item2)))) / (double)Math.Max(1, blocks.Sum(b => b.Buildings.Sum(f => f.Pixels.Count)))).ToList();
                int best = scores.IndexOf(scores.Max());
                Console.WriteLine($"{blocks.Count} blocks, {blocks.Sum(b => b.Buildings.Count)} buildings; footprint on the automap's buildings by orientation: {string.Join(", ", scores.Select((s2, k) => $"{k}:{s2:P0}"))}; using {best}");
                var poor = new List<(string Block, int Index, double Share, int Pixels)>();
                foreach (var b in blocks)
                    foreach (var (index, pixels) in b.Buildings.Where(f => f.Index >= 0))
                        poor.Add((b.Name, index, pixels.Count(p => On(b.Map, best, p.Item1, p.Item2)) / (double)pixels.Count, pixels.Count));
                // The other way round: each automap value's pixels, and how many our buildings cover.
                var byValue = new SortedDictionary<int, (int All, int Covered)>();
                foreach (var b in blocks)
                {
                    var ours = b.Buildings.SelectMany(f => f.Pixels).ToHashSet();
                    for (int z = 0; z < 64; z++)
                        for (int x = 0; x < 64; x++)
                        {
                            var (u, v) = Orient(best, x, z);
                            int val = b.Map[v * 64 + u];
                            if (val == 0) continue;
                            var t = byValue.GetValueOrDefault(val);
                            byValue[val] = (t.All + 1, t.Covered + (ours.Contains((x, z)) ? 1 : 0));
                        }
                }
                if (opts.TryGetValue("value", out var valueText))
                    foreach (var b in blocks)
                    {
                        int n = 0;
                        for (int i = 0; i < 64 * 64; i++) if (b.Map[i] == int.Parse(valueText)) n++;
                        if (n > 0) Console.WriteLine($"  value {valueText}: {n} pixels in {b.Name}");
                    }
                Console.WriteLine($"automap pixels covered by ours, by value: {string.Join(", ", byValue.Select(kv => $"{kv.Key}: {kv.Value.Covered}/{kv.Value.All}"))}");
                Console.WriteLine($"buildings by share on the automap: {string.Join(", ", new[] { 0.9, 0.75, 0.5, 0.25 }.Select(t => $">= {t:P0}: {poor.Count(p => p.Share >= t)}"))}, below 25%: {poor.Count(p => p.Share < 0.25)}");
                foreach (var p in poor.Where(p => p.Pixels >= 4).OrderBy(p => p.Share).Take(int.Parse(opts.GetValueOrDefault("list", "15"))))
                    Console.WriteLine($"  {p.Block} building {p.Index}: {p.Share:P0} of {p.Pixels} pixels");
                // --image BLOCK: that block drawn 8x (automap grey, ours red, both yellow, building numbers' centres white) to --out.
                if (opts.TryGetValue("image", out var imageBlock) && blocks.FirstOrDefault(b => b.Name.StartsWith(imageBlock, StringComparison.OrdinalIgnoreCase)) is { Name: not null } ib)
                {
                    const int Z = 8;
                    var rgb = new byte[64 * Z * 64 * Z * 3];
                    var ours = ib.Buildings.SelectMany(f => f.Pixels).ToHashSet();
                    for (int z = 0; z < 64; z++)
                        for (int x = 0; x < 64; x++)
                        {
                            bool a3 = On(ib.Map, best, x, z), o3 = ours.Contains((x, z));
                            (byte r, byte g, byte b) c = a3 && o3 ? ((byte)230, (byte)210, (byte)40) : o3 ? ((byte)220, (byte)40, (byte)40) : a3 ? ((byte)110, (byte)110, (byte)110) : ((byte)20, (byte)20, (byte)24);
                            for (int dy = 0; dy < Z; dy++) for (int dx = 0; dx < Z; dx++)
                            {
                                int q = (((63 - z) * Z + dy) * 64 * Z + x * Z + dx) * 3;   // north up
                                rgb[q] = c.r; rgb[q + 1] = c.g; rgb[q + 2] = c.b;
                            }
                        }
                    Util.Png.WriteRgb(opts.GetValueOrDefault("out", "out/town-check.png"), 64 * Z, 64 * Z, rgb);
                    Console.WriteLine($"wrote {opts.GetValueOrDefault("out", "out/town-check.png")}");
                }
                return 0;
            }
            case "df-fix-statues":
            {
                // Places the statues of Daggerfall plugins built before 2026-10-10 again as the converter now does, in
                // their dungeons and building interiors (--plugins a;b, in place; each plugin once; Skyrim.StatueRefFix).
                foreach (var line in Skyrim.StatueRefFix.Fix(opts["plugins"].Split(';', StringSplitOptions.RemoveEmptyEntries), arena2, skyrimData, opts.ContainsKey("details") ? Console.WriteLine : null)) Console.WriteLine(line);
                return 0;
            }
            case "room-facing":
            {
                // Dev aid: from --at x,y,z (Skyrim cell units, z at the floor) in --name's dungeon, the walls' distance along
                // each level ray (Daggerfall units) and the way a statue there would face.
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name == opts["name"] && l.DungeonBlocks.Count > 0);
                var models = DfModel.LoadAll(arena2);
                var dungeon = DfDungeon.Load(loc, new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), []);
                var rooms = new Skyrim.RoomFacing(dungeon.Polygons(models));
                var c = opts["at"].Split(',').Select(float.Parse).ToArray();
                var at = new System.Numerics.Vector3(c[0], c[2] + 96, c[1]) / Skyrim.Meshes.BlockMeshBuilder.Scale;
                Console.WriteLine($"Daggerfall point {at}");
                foreach (var (d, hit) in rooms.RaysFrom(at)) Console.WriteLine($"  ({d.X:F2},{d.Y:F2}): {hit:F0}");
                Console.WriteLine($"faces {rooms.Toward(at)}");
                return 0;
            }
            case "df-fix-doors":
            {
                // Seats the dungeon doors and turns the town doors of Daggerfall plugins built before 2026-10-10 (--plugins
                // a;b, in place; --with: plugins whose door activators they use, e.g. the main Daggerfall.esm). Each
                // plugin once (Skyrim.DoorRefFix).
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var props = new Skyrim.PropReplacer(skyrim, skyrimData);
                var plugins = opts["plugins"].Split(';', StringSplitOptions.RemoveEmptyEntries);
                var with = opts.GetValueOrDefault("with", "").Split(';', StringSplitOptions.RemoveEmptyEntries);
                foreach (var line in Skyrim.DoorRefFix.Fix(plugins, with, props)) Console.WriteLine(line);
                return 0;
            }
            case "town-door-sizes":
            {
                // Dev aid (user, 2026-10-10: "the door frames dont match some of the doors"): Daggerfall's building door
                // surfaces in --name's town (outside, and inside the buildings), as a histogram of width x height
                // (Daggerfall units).
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var models = DfModel.LoadAll(arena2);
                var loc = DfLocations.LoadAll(arena2).First(l => l.Name.Equals(opts["name"], StringComparison.OrdinalIgnoreCase) && l.ExteriorBlocks.Length > 0);
                var sizes = new SortedDictionary<string, int>();
                for (int i = 0; i < loc.ExteriorBlocks.Length; i++)
                {
                    if (DfTownBlock.Resolve(bsa, loc.ExteriorBlocks[i]) is not { } name) continue;
                    var tb = DfTownBlock.Load(bsa, name);
                    foreach (var d in DfTownLayout.Doors(DfTownLayout.Exterior(tb), models))
                        sizes[$"outside {d.Width,3:F0} x {d.Height,3:F0}"] = sizes.GetValueOrDefault($"outside {d.Width,3:F0} x {d.Height,3:F0}") + 1;
                    foreach (var b in tb.Buildings)
                        foreach (var d in DfTownLayout.Doors(DfTownLayout.Interior(tb, b, models), models))
                            sizes[$"inside  {d.Width,3:F0} x {d.Height,3:F0}"] = sizes.GetValueOrDefault($"inside  {d.Width,3:F0} x {d.Height,3:F0}") + 1;
                }
                foreach (var (k, n) in sizes) Console.WriteLine($"{k}: {n}");
                return 0;
            }
            case "door-front":
            {
                // Dev aid: which way a door model's front faces in its own frame, learned from Skyrim's own placements
                // (--plugin, --bases hex,hex): for each ref with a far side, the arrival point outside it (the far
                // door's destination) turned into the ref's frame. Averaged per base.
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var bases = opts["bases"].Split(',').Select(x => Convert.ToUInt32(x, 16)).ToHashSet();
                var all = m.EnumerateMajorRecords<IPlacedObjectGetter>().ToDictionary(r => r.FormKey);
                foreach (var g in all.Values.Where(r => r.Base.FormKey.ModKey == m.ModKey && bases.Contains(r.Base.FormKey.ID) && r.TeleportDestination is not null).GroupBy(r => r.Base.FormKey))
                {
                    var sum = System.Numerics.Vector2.Zero; int n = 0;
                    foreach (var a in g)
                    {
                        if (!all.TryGetValue(a.TeleportDestination!.Door.FormKey, out var far) || far.TeleportDestination is null || a.Placement is null) continue;
                        var arrive = far.TeleportDestination.Position;
                        float dx = arrive.X - a.Placement.Position.X, dy = arrive.Y - a.Placement.Position.Y, h = a.Placement.Rotation.Z;
                        var local = new System.Numerics.Vector2(dx * MathF.Cos(h) - dy * MathF.Sin(h), dx * MathF.Sin(h) + dy * MathF.Cos(h));
                        if (local.Length() < 1 || local.Length() > 400) continue;
                        sum += System.Numerics.Vector2.Normalize(local); n++;
                    }
                    var avg = n > 0 ? sum / n : default;
                    Console.WriteLine($"{g.Key}: {n} doors, front in its own frame ({avg.X:F2},{avg.Y:F2})");
                }
                return 0;
            }
            case "statue-count":
            {
                // Dev aid: statue references (the Skyrim.esm statues used for Daggerfall's) in --plugin's interior cells
                // and outdoors.
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                uint[] ids = [0xE3FC7, 0x91712, 0x8F965, 0xD068D, 0xAA93D, 0x39DF9, 0xAA93F];
                bool IsStatue(IPlacedObjectGetter r) => r.Base.FormKey.ModKey.FileName == "Skyrim.esm" && ids.Contains(r.Base.FormKey.ID);
                var inside = m.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells).SelectMany(c => c.Temporary.Concat(c.Persistent).OfType<IPlacedObjectGetter>()).Where(IsStatue).ToList();
                var outside = m.Worldspaces.SelectMany(w => w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).Concat(w.TopCell is { } t ? [t] : []))
                    .SelectMany(c => c.Temporary.Concat(c.Persistent).OfType<IPlacedObjectGetter>()).Where(IsStatue).ToList();
                Console.WriteLine($"inside {inside.Count} ({string.Join(", ", inside.GroupBy(r => r.Base.FormKey.ID).Select(g => $"{g.Key:X} {g.Count()}"))}), outside {outside.Count}");
                return 0;
            }
            case "door-scales":
            {
                // Dev aid (user, 2026-10-10: "some doors are too small for npcs too"): every placed door or door
                // activator in --plugin by base, as a histogram of scale (0.05 steps), with an example ref of the smallest.
                using var m = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var names = m.Doors.Select(d => (d.FormKey, d.EditorID)).Concat(m.Activators.Where(a => (a.EditorID ?? "").Contains("Door")).Select(a => (a.FormKey, a.EditorID)))
                    .ToDictionary(x => x.FormKey, x => x.EditorID ?? "");
                foreach (var (k, n) in new[] { (0x4DFD9u, "ImpDoorExtSingleSmall01"), (0x439C2u, "ImpDoorDouble01"), (0x94193u, "WRDoor02Load"), (0x2ABABu, "NorDoorSmLoad01"), (0x4D6EDu, "RiftenDoorLoad01"), (0x4203Au, "ImpDoorSingleLoad01") })
                    names[new FormKey(ModKey.FromFileName("Skyrim.esm"), k)] = n;
                var refs = new List<(string Base, float Scale, string Where, Noggog.P3Float At)>();
                void Scan(ICellGetter c, string where)
                {
                    foreach (var r in c.Persistent.Concat(c.Temporary).OfType<IPlacedObjectGetter>())
                        if (names.TryGetValue(r.Base.FormKey, out var bn)) refs.Add((bn, r.Scale ?? 1, where, r.Placement?.Position ?? default));
                }
                foreach (var c in m.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells)) Scan(c, c.EditorID ?? "");
                foreach (var w in m.Worldspaces)
                {
                    if (w.TopCell is { } top) Scan(top, w.EditorID ?? "");
                    foreach (var c in w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items)) Scan(c, $"{w.EditorID} {c.Grid?.Point.X},{c.Grid?.Point.Y}");
                }
                foreach (var g in refs.GroupBy(r => r.Base).OrderByDescending(g => g.Count()))
                {
                    Console.WriteLine($"{g.Key}: {g.Count()}");
                    foreach (var h in g.GroupBy(r => MathF.Floor(r.Scale * 20) / 20).OrderBy(h => h.Key))
                    {
                        var ex = h.First();
                        Console.WriteLine($"  scale {h.Key:F2}-{h.Key + 0.05f:F2}: {h.Count()}  e.g. {ex.Where} at {ex.At}");
                    }
                }
                return 0;
            }
            case "status-timing":
            {
                // Dev aid: how long each part of the app's status check takes (game detection, mod lists, tools).
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var found = GameInstalls.Detect();
                Console.WriteLine($"detect games: {sw.ElapsedMilliseconds} ms ({string.Join(", ", found.Keys)})");
                sw.Restart(); var sky = GameInstalls.SkyrimData(); Console.WriteLine($"skyrim: {sw.ElapsedMilliseconds} ms");
                foreach (var (g, install) in found)
                    if (Mods.ModCatalog.Supports(g))
                    {
                        sw.Restart(); int sel = Mods.ModCatalog.Selected(g, install.DataDir).Count; long a1 = sw.ElapsedMilliseconds;
                        sw.Restart(); int all = Mods.ModCatalog.List(g, install.DataDir).Count;
                        Console.WriteLine($"mods {g}: selected {a1} ms, list {sw.ElapsedMilliseconds} ms ({sel} of {all})");
                    }
                foreach (var d in Setup.Dependencies.Check(sky ?? ""))
                {
                }
                sw.Restart(); var checks = Setup.Dependencies.Check(sky ?? ""); Console.WriteLine($"tools check: {sw.ElapsedMilliseconds} ms ({checks.Count()} tools)");
                sw.Restart(); var ck = Setup.Dependencies.CreationKitMissing(sky ?? ""); Console.WriteLine($"creation kit: {sw.ElapsedMilliseconds} ms");
                return 0;
            }
            case "menu-pack":
            {
                // The main menu and its music in --out (default out/convert) as the "TamrielMerge Menu.esp" part (Render/
                // MenuArchive): archives of a light plugin instead of loose files over Skyrim's, so it can be switched off.
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/convert"));
                int n = Skyrim.Render.MenuArchive.Build(outDir);
                Console.WriteLine(n == 0 ? "menu-pack: no menu files to pack" : $"menu-pack: {n} files in {Skyrim.Render.MenuArchive.PluginName}");
                return 0;
            }
            case "menu-music":
            {
                // The main menu's own music (user, 2026-10-09: "write a musical score to replace the skyrim menu music"):
                // tools/menu/menu_theme.py writes the score as MIDI, FluidSynth plays it with GeneralUser GS (hall reverb),
                // faded and normalised, xWMAEncode makes it music\special\mus_maintheme.xwm in --out (default out/convert):
                // the file the main menu plays (the game setting sMainMenuMusic:General, "\Data\Music\Special\MUS_MainTheme.xwm" in
                // the executable), so no plugin changes. Not mus_intro.xwm, MUSSpecialIntro01's: the
                // menu doesn't play that music type, so Skyrim's theme went on playing (user, 2026-10-11: "why does skyrims
                // music take priority"). Without the Creation Kit's encoder it is left as mus_maintheme.wav, which the menu
                // may not play (the setting names the .xwm). Work files in --work (default out/music-work).
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/convert"));
                string work = Path.GetFullPath(opts.GetValueOrDefault("work", "out/music-work"));
                Directory.CreateDirectory(work);
                var tools = MusicTools(skyrimData);
                string py = Setup.Dependencies.Python ?? throw new InvalidOperationException("the Python environment is missing: run setup");
                string mid = Path.Combine(work, "menu_theme.mid"), wav = Path.Combine(work, "menu_theme.wav");
                if (Setup.ToolScripts.Run(py, ["-u", Setup.ToolScripts.Path("menu", "menu_theme.py"), mid]) != 0) { Console.Error.WriteLine("menu-music: composing failed"); return 1; }
                var psi = new System.Diagnostics.ProcessStartInfo(tools.FluidSynth) { WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };
                foreach (var a in new[] { "-ni", "-q", "-g", "0.5", "-r", "44100", "-R", "1", "-C", "1",
                             "-o", "synth.reverb.room-size=0.85", "-o", "synth.reverb.damp=0.35", "-o", "synth.reverb.width=1.0", "-o", "synth.reverb.level=0.8",
                             "-F", wav, tools.SoundFont, mid })
                    psi.ArgumentList.Add(a);
                using (var fs = System.Diagnostics.Process.Start(psi)!) { fs.StandardOutput.ReadToEnd(); fs.StandardError.ReadToEnd(); fs.WaitForExit(); if (fs.ExitCode != 0) { Console.Error.WriteLine("menu-music: FluidSynth failed"); return 1; } }
                // The score's last bars fade out themselves; a short fade only keeps the reverb tail's end clean.
                Daggerfall.Wav.FadeAndNormalise(wav, fadeSeconds: 3, maxSeconds: 600);
                string dir = Path.Combine(outDir, "music", "special");
                Directory.CreateDirectory(dir);
                if (File.Exists(tools.XwmaEncode))
                {
                    string xwm = Path.Combine(dir, "mus_maintheme.xwm");
                    var enc = new System.Diagnostics.ProcessStartInfo(tools.XwmaEncode) { WorkingDirectory = work, RedirectStandardOutput = true, RedirectStandardError = true };
                    foreach (var a in new[] { "-b", "192000", wav, xwm }) enc.ArgumentList.Add(a);
                    using var e = System.Diagnostics.Process.Start(enc)!; e.StandardOutput.ReadToEnd(); e.StandardError.ReadToEnd(); e.WaitForExit();
                    if (e.ExitCode != 0) { Console.Error.WriteLine("menu-music: xWMAEncode failed"); return 1; }
                    Console.WriteLine($"menu-music: the menu's theme -> {xwm}");
                }
                else
                {
                    File.Copy(wav, Path.Combine(dir, "mus_maintheme.wav"), true);
                    Console.WriteLine("menu-music: no xWMAEncode (Creation Kit); the menu's theme left as mus_maintheme.wav");
                }
                return 0;
            }
            case "menu":
            {
                // The whole main menu (docs/menu.md) from the converted plugins in --plugins (default out/convert) into --out
                // (default the same folder): the intro's scenes exported (scene-export; only the games converted get a shot),
                // ray-traced in Blender's EEVEE (user, 2026-10-09: "flybys need rt not pt")
                // (tools/menu/intro.py, tools/scene3d/blend_scene.py; the raster renderer when Blender is missing), graded,
                // captioned and packed; Skyrim's emblem textures in the logo's red (tools/menu/red_logo.py); the logo meshes
                // with the intro on a screen fitted to the measured menu camera (tools/menu/menu_camera.json). GPU-heavy:
                // `convert` runs it only with --gpu. Work files under --work (default out/menuart), scenes in --scenes
                // (default out/scenes); finished steps (scenes, frames, textures) are kept and skipped on a rerun.
                string conv = Path.GetFullPath(opts.GetValueOrDefault("plugins", "out/convert"));
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", conv));
                string art = Path.GetFullPath(opts.GetValueOrDefault("work", "out/menuart"));
                string shots = Path.Combine(art, "intro"), menuArt = Path.Combine(art, "menu");
                string py = Setup.Dependencies.Python ?? throw new InvalidOperationException("the Python environment is missing: run setup");
                string intro = Setup.ToolScripts.Path("menu", "intro.py");
                var env = new Dictionary<string, string> { ["TAMRIELMERGE_SCENES"] = Path.GetFullPath(opts.GetValueOrDefault("scenes", "out/scenes")) };
                int Py(params string[] a) => Setup.ToolScripts.Run(py, ["-u", .. a], env);
                if (Py(intro, "export", shots, "--cli", Environment.ProcessPath!, "--plugins", conv, "--data", skyrimData) != 0) { Console.Error.WriteLine("menu: scene export failed"); return 1; }
                bool traced = Setup.Dependencies.Blender is not null;
                if (traced && Py(intro, "prep", shots) != 0) { Console.Error.WriteLine("menu: preparing the scenes failed"); return 1; }
                Console.WriteLine(traced ? "menu: ray-tracing the intro in Blender's EEVEE" : "menu: Blender missing, rendering the intro with the raster renderer");
                if (Py(intro, traced ? "rt-render" : "render", shots) != 0) { Console.Error.WriteLine("menu: rendering failed"); return 1; }
                // Upscaled to 1440p with Real-ESRGAN (user, 2026-10-08: "the video needs upscaled to 4k"; 1440p chosen,
                // as 4K's 520 frames would be 2.2 GB of textures in the menu).
                string frames = traced ? "rt" : "";
                if (Py(intro, "upscale", shots, "--frames", frames, "--size", MenuVideoSize) != 0) { Console.Error.WriteLine("menu: upscaling failed"); return 1; }
                // The red emblem on its banner, a layer in front of the video (the menu mesh's 3D emblem doesn't draw with
                // the video): emblem.png in the work folder (cut from the game's menu with the red textures), else the
                // tool's own copy.
                string emblem = Path.Combine(art, "emblem.png");
                if (!File.Exists(emblem)) emblem = Setup.ToolScripts.Path("menu", "emblem.png");
                string[] emblemArg = File.Exists(emblem) ? ["--emblem", emblem] : [];
                if (!File.Exists(emblem)) Console.WriteLine("menu: no emblem.png; the menu goes without the emblem and its banner");
                if (Py([intro, "pack", shots, menuArt, "--frames", frames + MenuVideoSize.Split('x')[1], "--size", MenuVideoSize, .. emblemArg]) != 0) { Console.Error.WriteLine("menu: packing failed"); return 1; }
                // The emblem's textures recoloured, from Skyrim's own.
                string[] logos = ["logo01_d.dds", "logo01ae_d.dds", "logo02_d.dds"];
                if (logos.Any(l => !File.Exists(Path.Combine(menuArt, l))))
                {
                    string vanilla = Path.Combine(art, "vanilla");
                    Main(["bsa-extract", "--match", @"interface\objects\logo", "--out", vanilla]);
                    Py([Setup.ToolScripts.Path("menu", "red_logo.py"), Setup.ToolScripts.Path("menu", "logo_source.png"), menuArt,
                        .. logos.Select(l => Path.Combine(vanilla, "textures", "interface", "objects", l))]);
                }
                Skyrim.Render.MenuBuilder.Build(skyrimData, menuArt, outDir, Setup.ToolScripts.Path("menu", "menu_camera.json"));
                return 0;
            }
            case "models-index":
            {
                // The model viewer's lists (Skyrim/Models/ModelCatalog): the models the converted plugins in --dir (default
                // out/convert) use, and with --available the models the user's games offer, as JSON to --out (or stdout).
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                object result = opts.ContainsKey("available") ? Skyrim.Models.ModelCatalog.Available(skyrimData, dir)
                    : Skyrim.Models.ModelCatalog.Used(dir, m => Console.Error.WriteLine(m));
                string json = System.Text.Json.JsonSerializer.Serialize(result);
                if (opts.TryGetValue("out", out var o)) WriteWhole(o, json); else Console.WriteLine(json);
                return 0;
            }
            case "model-preview":
            {
                // A model (--model, as records hold it) turned round in --frames pictures (default 24) into --out: exported
                // (scene-mesh, files from --loose folders first, then Skyrim's Data and archives) and drawn by
                // tools/scene3d/preview_model.py (smooth, matte, its own textures). Kept: an existing preview is reused.
                string model = opts["model"];
                string outDir = Path.GetFullPath(opts["out"]);
                // Finished previews have done.txt; one is made in a folder of its own and moved into place whole, so the
                // app never reads pictures still being drawn (2026-10-14: half-written frames threw in the app).
                if (File.Exists(Path.Combine(outDir, "done.txt"))) return 0;
                string work = outDir + ".tmp-" + Environment.ProcessId;
                if (Directory.Exists(work)) Directory.Delete(work, true);
                string scene = Path.Combine(work, "scene");
                var loose = opts.GetValueOrDefault("loose", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFullPath);
                new Skyrim.Render.SceneExporter(skyrimData, loose, scene).ExportMesh(model.StartsWith("meshes", StringComparison.OrdinalIgnoreCase) ? model : Path.Combine("meshes", model));
                string py = Setup.Dependencies.ModelPython ?? throw new InvalidOperationException("the 3D model maker's Python is missing: run setup");
                int code = Setup.ToolScripts.Run(py, ["-I", Setup.ToolScripts.Path("scene3d", "preview_model.py"), scene, work, "--frames", opts.GetValueOrDefault("frames", "24")]);
                if (code != 0 || !File.Exists(Path.Combine(work, "view_00.png"))) { try { Directory.Delete(work, true); } catch (IOException) { } return code != 0 ? code : 1; }
                File.WriteAllText(Path.Combine(work, "done.txt"), model);
                if (Directory.Exists(outDir)) Directory.Delete(outDir, true);   // an old, unfinished one
                try { Directory.Move(work, outDir); }
                catch (IOException) when (File.Exists(Path.Combine(outDir, "done.txt"))) { Directory.Delete(work, true); }   // another run finished first
                return 0;
            }
            case "model-textures":
            {
                // A model's parts and their eight texture slots, as JSON (the texture selector).
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(Skyrim.Models.TextureTools.Shapes(opts["model"], Skyrim.Models.TextureTools.For(dir, skyrimData))));
                return 0;
            }
            case "textures-index":
            {
                // Every texture the user's games offer, as JSON to --out (the texture picker).
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                WriteWhole(opts["out"], System.Text.Json.JsonSerializer.Serialize(Skyrim.Models.TextureTools.Available(skyrimData, dir)));
                return 0;
            }
            case "texture-thumb":
            {
                // A picture (PNG, --size px) of a texture (--texture, Data- or textures\-relative) to --out; kept if there.
                string outFile = Path.GetFullPath(opts["out"]);
                if (File.Exists(outFile)) return 0;
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                // Made under names of this run's own and moved into place whole: two thumbnails of one texture at once
                // (parts sharing it) no longer share a temporary file or hand the app a half-written picture.
                string dds = $"{outFile}.{Environment.ProcessId}.dds", png = $"{outFile}.{Environment.ProcessId}.png";
                if (!Skyrim.Models.TextureTools.Extract(opts["texture"], Skyrim.Models.TextureTools.For(dir, skyrimData), dds)) { Console.Error.WriteLine("texture not found"); return 1; }
                string py = Setup.Dependencies.Python ?? throw new InvalidOperationException("the Python environment is missing: run setup");
                int code = Setup.ToolScripts.Run(py, ["-I", Setup.ToolScripts.Path("textures", "pbr.py"), "thumb", dds, png, "--size", opts.GetValueOrDefault("size", "256")]);
                File.Delete(dds);
                if (code == 0 && File.Exists(png)) { try { File.Move(png, outFile, overwrite: true); } catch (IOException) { File.Delete(png); } }
                return code;
            }
            case "pbr-make":
            {
                // The PBR tool (tools/textures/pbr.py): --texture (a colour texture) made into a PBR set and baked for
                // Skyrim's shaders, into --out, with --params (JSON or a .json file); --out keeps the AI's normals for re-tuning.
                string outDir = Path.GetFullPath(opts["out"]);
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                string src = Path.Combine(outDir, "source.dds");
                if (!File.Exists(src) && !Skyrim.Models.TextureTools.Extract(opts["texture"], Skyrim.Models.TextureTools.For(dir, skyrimData), src)) { Console.Error.WriteLine("texture not found"); return 1; }
                string py = Setup.Dependencies.Python ?? throw new InvalidOperationException("the Python environment is missing: run setup");
                return Setup.ToolScripts.Run(py, ["-I", Setup.ToolScripts.Path("textures", "pbr.py"), "make", src, outDir, "--params", opts.GetValueOrDefault("params", "{}"),
                    "--model", Setup.Dependencies.NormalsModel]);
            }
            case "pbr-apply":
            {
                // A PBR material (--material, pbr-make's folder) baked onto part --shape of --model (as records hold it; its
                // replacement if it has one) in --dir: a copy of the model with the material, which the model is then
                // replaced with; the models plugin is rebuilt.
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                var overrides = Skyrim.Models.ModelCatalog.LoadOverrides(dir);
                string original = opts["model"];
                string current = overrides.TryGetValue(original, out var rep) ? rep : original;
                string copy = Skyrim.Models.TextureTools.BakePbr(original, current, int.Parse(opts["shape"]), Path.GetFullPath(opts["material"]), dir, Skyrim.Models.TextureTools.For(dir, skyrimData));
                overrides[original] = copy;
                Skyrim.Models.ModelCatalog.SaveOverrides(dir, overrides);
                int n = Skyrim.Models.ModelCatalog.Apply(dir, skyrimData);
                Console.WriteLine($"{original}: part {opts["shape"]} baked into {copy}; {Skyrim.Models.ModelCatalog.PluginName}: {n} records");
                return 0;
            }
            case "models-apply":
            {
                // The user's model replacements (model-overrides.json in --dir, default out/convert) as TamrielMerge Models.esp.
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", "out/convert"));
                int n = Skyrim.Models.ModelCatalog.Apply(dir, skyrimData);
                Console.WriteLine(n > 0 ? $"{Skyrim.Models.ModelCatalog.PluginName}: {n} records changed (models and textures)" : "No model or texture replacements.");
                return 0;
            }
            case "menu-calibrate":
            {
                // The logo meshes with camera calibration markers (tools/menu/calibration.py make --cal) into --out.
                Skyrim.Render.MenuBuilder.BuildCalibration(skyrimData, Path.GetFullPath(opts["cal"]), Path.GetFullPath(opts["out"]));
                return 0;
            }
            case "scene-mesh":
            {
                // One mesh (--mesh, Data-relative) as a scene for tools/scene3d/render_scene.py, its files looked up in
                // --loose folders (comma-separated) first, then Skyrim's Data and archives.
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/scenes/mesh"));
                var loose = opts.GetValueOrDefault("loose", "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Path.GetFullPath);
                new Skyrim.Render.SceneExporter(skyrimData, loose, outDir).ExportMesh(opts["mesh"]);
                return 0;
            }
            case "scene-worlds":
            {
                // The worldspaces in --plugins with their cell extents, to pick what to export.
                foreach (var p in opts["plugins"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    using var m = SkyrimMod.CreateFromBinaryOverlay(p, SkyrimRelease.SkyrimSE);
                    foreach (var ws in m.Worldspaces)
                    {
                        var cells = ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).Where(c => c.Grid is not null).ToList();
                        if (cells.Count == 0) continue;
                        Console.WriteLine($"{Path.GetFileName(p)} {ws.EditorID} \"{ws.Name?.String}\": {cells.Count} cells x {cells.Min(c => c.Grid!.Point.X)}..{cells.Max(c => c.Grid!.Point.X)} y {cells.Min(c => c.Grid!.Point.Y)}..{cells.Max(c => c.Grid!.Point.Y)}, refs {cells.Sum(c => c.Temporary.Count + c.Persistent.Count)}, land {cells.Count(c => c.Landscape is not null)}"
                            + (ws.Parent is { } par && !par.Worldspace.IsNull ? $", parent {par.Worldspace.FormKey} uses its {par.Flags}" : ""));
                    }
                }
                return 0;
            }
            case "npc-check":
            {
                // NPC and leveled-list problems the game can crash on while loading --plugin (with Skyrim's masters):
                // template chains that loop, inherit flags without a template, leveled lists that contain themselves.
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts["plugin"], SkyrimRelease.SkyrimSE);
                var mods = new List<ISkyrimModGetter>();
                foreach (var m in plugin.ModHeader.MasterReferences) mods.Add(SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, m.Master.FileName), SkyrimRelease.SkyrimSE));
                mods.Add(plugin);
                var cache = mods.ToImmutableLinkCache();
                int loops = 0, noTemplate = 0, selfLists = 0, badRace = 0, deadTemplate = 0;
                foreach (var npc in plugin.Npcs)
                {
                    if (npc.Template.IsNull && npc.Configuration.TemplateFlags != 0) { noTemplate++; if (noTemplate <= 5) Console.WriteLine($"  template flags without a template: {npc.EditorID} {npc.FormKey} {npc.Configuration.TemplateFlags}"); }
                    if (!npc.Template.IsNull && !cache.TryResolve<INpcSpawnGetter>(npc.Template.FormKey, out _)) { deadTemplate++; if (deadTemplate <= 5) Console.WriteLine($"  template not found: {npc.EditorID} -> {npc.Template.FormKey}"); }
                    if (!cache.TryResolve<IRaceGetter>(npc.Race.FormKey, out _)) { badRace++; if (badRace <= 5) Console.WriteLine($"  race not found: {npc.EditorID} {npc.Race.FormKey}"); }
                    var seen = new HashSet<FormKey> { npc.FormKey };
                    var key = npc.Template.FormKey;
                    for (int depth = 0; depth < 32 && !key.IsNull; depth++)
                    {
                        if (!seen.Add(key)) { loops++; if (loops <= 5) Console.WriteLine($"  template loop: {npc.EditorID} {npc.FormKey} via {key}"); break; }
                        if (cache.TryResolve<INpcGetter>(key, out var t)) key = t.Template.FormKey;
                        else if (cache.TryResolve<ILeveledNpcGetter>(key, out var l)) key = l.Entries?.FirstOrDefault()?.Data?.Reference.FormKey ?? FormKey.Null;
                        else break;
                    }
                }
                foreach (var l in plugin.LeveledNpcs)
                {
                    var stack = new Stack<FormKey>(); var visited = new HashSet<FormKey>();
                    stack.Push(l.FormKey);
                    while (stack.Count > 0)
                    {
                        var k = stack.Pop();
                        if (!visited.Add(k)) continue;
                        if (!cache.TryResolve<ILeveledNpcGetter>(k, out var ll)) continue;
                        foreach (var e in ll.Entries ?? [])
                            if (e.Data?.Reference.FormKey is { } r) { if (r == l.FormKey) { selfLists++; Console.WriteLine($"  leveled list contains itself: {l.EditorID}"); } else stack.Push(r); }
                    }
                }
                Console.WriteLine($"{plugin.Npcs.Count} NPCs, {plugin.LeveledNpcs.Count} leveled lists: {loops} template loops, {noTemplate} with template flags but no template, {deadTemplate} templates not found, {badRace} races not found, {selfLists} self-containing lists");
                return 0;
            }
            case "plugin-strip":
            {
                // A copy of --plugin without its top-level groups of --drop TYPES (comma-separated, e.g. DIAL,QUST) as --out.
                var drop = opts["drop"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
                long n = Skyrim.PluginMerger.Strip(opts["plugin"], opts["out"], drop);
                Console.WriteLine($"{opts["out"]}: {n} records and groups (without {string.Join(", ", drop)})");
                return 0;
            }
            case "merge-plugins":
            {
                // Joins parts of one plugin (PluginMerger: same ModKey and masters, separate FormID ranges) into --out.
                var parts = args.Skip(1).Where(a => !a.StartsWith("--") && a != opts.GetValueOrDefault("out")).ToList();
                long n = Skyrim.PluginMerger.Merge(parts, opts["out"]);
                Console.WriteLine($"{opts["out"]}: {n} records and groups from {parts.Count} parts");
                return 0;
            }
            case "plugin-stats":
            {
                // For each plugin named (or every .esm/.esp in --dir): the records it adds, its highest FormID and
                // whether it could be an ESL (at most 4096 new records, as Skyrim 1.6.1130+ allows).
                var files = opts.TryGetValue("dir", out var statsDir) ? Directory.GetFiles(statsDir).Where(f => f.EndsWith(".esm") || f.EndsWith(".esp") || f.EndsWith(".esl")).OrderBy(f => f).ToList()
                    : args.Skip(1).Where(a => !a.StartsWith("--")).ToList();
                foreach (var file in files)
                {
                    using var m = SkyrimMod.CreateFromBinaryOverlay(file, SkyrimRelease.SkyrimSE);
                    var own = m.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == m.ModKey).ToList();
                    uint max = own.Count == 0 ? 0 : own.Max(r => r.FormKey.ID);
                    Console.WriteLine($"{Path.GetFileName(file)}: {own.Count} new records, highest ID {max:X6}, {m.ModHeader.MasterReferences.Count} masters, {new FileInfo(file).Length / 1048576.0:F1} MB{(own.Count <= 4096 ? " - fits an ESL" : "")}");
                }
                return 0;
            }
            case "df-overland-test":
            {
                // The tiled overland on its own: worldspace, map markers, tiles, regions and the overland quest into
                // --out (default out/overlandtest/Daggerfall.esm), and the land of --tiles (default 0, "all" for every
                // tile). Checks every tile stays within the precision limit, every reference stands inside its tile,
                // map markers sit on the land, and land shared by neighbouring tiles is identical.
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                string outPath = Path.GetFullPath(opts.GetValueOrDefault("out", "out/overlandtest/Daggerfall.esm"));
                Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
                var mod = new SkyrimMod(ModKey.FromFileName(Path.GetFileName(outPath)), SkyrimRelease.SkyrimSE);
                mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Master;
                var map = DfWorldMap.Load(arena2);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var field = Skyrim.Terrain.HeightField.Generate(map, TerrainSettingsFrom(opts));
                Console.WriteLine($"Height field {field.CellsX}x{field.CellsY} cells in {sw.Elapsed.TotalSeconds:F0}s");
                var world = Skyrim.Terrain.TerrainBuilder.NewWorldspace(mod, skyrim, field, DaggerfallPluginBuilder.WorldEditorId, "Iliac Bay");
                var layout = new Skyrim.Terrain.OverlandTiles(field);
                var markers = LocationPlacer.PlaceMarkers(mod, world, field, DfLocations.LoadAll(arena2));
                var global = markers.ToDictionary(m => m.Marker.FormKey, m => m.Marker.Placement!.Position);
                var tiles = layout.Split(mod, world);
                var (areas, regionOf) = AtmosphereBuilder.AddRegions(mod, skyrim, tiles, layout, field, map);
                Console.WriteLine($"{layout.Columns}x{layout.Rows} tiles, core {layout.CoreX}x{layout.CoreY} cells; {markers.Count} markers; {areas.Values.Sum()} region areas");
                int bad = 0;
                for (int t = 0; t < tiles.Count; t++)
                {
                    var ws = tiles[t];
                    float reach = new[] { ws.ObjectBoundsMin.X, ws.ObjectBoundsMin.Y, ws.ObjectBoundsMax.X, ws.ObjectBoundsMax.Y }.Max(MathF.Abs);
                    var refs = ws.TopCell!.Persistent.OfType<PlacedObject>().ToList();
                    int outside = refs.Count(r => r.Placement!.Position.X < ws.ObjectBoundsMin.X * 4096 || r.Placement.Position.X > ws.ObjectBoundsMax.X * 4096
                        || r.Placement.Position.Y < ws.ObjectBoundsMin.Y * 4096 || r.Placement.Position.Y > ws.ObjectBoundsMax.Y * 4096);
                    // Moved by exactly the tile's shift.
                    var shift = layout.Shift(t);
                    int wrong = refs.Count(r => global.TryGetValue(r.FormKey, out var g) && (MathF.Abs(g.X + shift.X - r.Placement!.Position.X) > 0.01f || MathF.Abs(g.Y + shift.Y - r.Placement.Position.Y) > 0.01f));
                    Console.WriteLine($"  {ws.EditorID}: bounds {ws.ObjectBoundsMin}..{ws.ObjectBoundsMax} (reach {reach} cells), {refs.Count} refs, {outside} outside, {wrong} misplaced, map {ws.MapData!.NorthwestCellCoords}..{ws.MapData.SoutheastCellCoords}");
                    if (reach > 128 || outside > 0 || wrong > 0) bad++;
                }
                // The overland quest's data, as the full build makes it.
                var bay = new Location(mod) { EditorID = "DFLocIliacBay", Name = "Iliac Bay" };
                mod.Locations.Add(bay);
                DaggerfallPluginBuilder.AddOverlandQuest(mod, layout, tiles, markers, bay, field.Settings.PixelsPerCell, map);
                TamrielMerge.Skyrim.TopicSubtypes.Fill(mod);
                mod.WriteToBinary(outPath, new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
                {
                    MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(new[] { ModKey.FromFileName("Skyrim.esm") }),
                });
                string spec = opts.GetValueOrDefault("tiles", "0");
                List<int>? only = spec == "all" ? null : spec.Split(',').Select(int.Parse).ToList();
                sw.Restart();
                string dir = Path.GetDirectoryName(outPath)!;
                // --land-plugins N: the land split into N plugins (as a full map's is when too big for one); --navmesh: the
                // overland navmeshes made over them too (OverlandNavmesh, as the build does), then checked: each land
                // cell's navmesh found once, in the last plugin, its edge links reaching navmeshes there.
                if (opts.TryGetValue("land-plugins", out var landPluginsWanted)) Skyrim.Terrain.OverlandTiles.LandPluginsWanted = int.Parse(landPluginsWanted);
                var lands = layout.LandPlugins(outPath, skyrimData, field, map, regionOf, l => Console.WriteLine("  land: " + l), only);
                Console.WriteLine($"Land in {sw.Elapsed.TotalSeconds:F0}s: {string.Join(", ", lands.Select(l => $"{l} {new FileInfo(Path.Combine(dir, l)).Length / 1048576} MB"))}");
                if (opts.ContainsKey("navmesh") && lands.Count > 0)
                {
                    sw.Restart();
                    var (navmeshes, navTris, navLinks) = Skyrim.Terrain.OverlandNavmesh.Build(lands.Select(l => Path.Combine(dir, l)).ToList(), l => Console.WriteLine("  navmesh: " + l));
                    Console.WriteLine($"Navmesh in {sw.Elapsed.TotalSeconds:F0}s: {navmeshes:N0} navmeshes, {navTris:N0} triangles, {navLinks:N0} links; {string.Join(", ", lands.Select(l => $"{l} {new FileInfo(Path.Combine(dir, l)).Length / 1048576} MB"))}");
                    var mods = lands.Select(l => SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, l), SkyrimRelease.SkyrimSE)).ToList();
                    try
                    {
                        var landAt = new Dictionary<(FormKey, int, int), string>();
                        var navAt = new Dictionary<(FormKey, int, int), int>();
                        var navKeys = new HashSet<FormKey>();
                        foreach (var m in mods)
                            foreach (var w in m.Worldspaces)
                                foreach (var c in w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                                {
                                    if (c.Grid is null) continue;
                                    if (c.Landscape is not null) landAt[(w.FormKey, c.Grid.Point.X, c.Grid.Point.Y)] = m.ModKey.FileName;
                                    if (c.NavigationMeshes.Count == 0) continue;
                                    if (m != mods[^1]) { Console.WriteLine($"  PROBLEM: navmesh in {m.ModKey.FileName}, not the last land plugin"); bad++; }
                                    navAt[(w.FormKey, c.Grid.Point.X, c.Grid.Point.Y)] = navAt.GetValueOrDefault((w.FormKey, c.Grid.Point.X, c.Grid.Point.Y)) + c.NavigationMeshes.Count;
                                    foreach (var n in c.NavigationMeshes) navKeys.Add(n.FormKey);
                                }
                        int twice = navAt.Values.Count(v => v > 1), dangling = 0, crossLinks = 0;
                        var navCellPlugin = new Dictionary<FormKey, string>();
                        foreach (var w in mods[^1].Worldspaces)
                            foreach (var c in w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                                foreach (var n in c.NavigationMeshes)
                                {
                                    navCellPlugin[n.FormKey] = c.FormKey.ModKey.FileName;
                                    foreach (var e in n.Data?.EdgeLinks ?? [])
                                        if (!navKeys.Contains(e.Mesh.FormKey)) dangling++;
                                }
                        foreach (var w in mods[^1].Worldspaces)
                            foreach (var c in w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                                foreach (var n in c.NavigationMeshes)
                                    foreach (var e in n.Data?.EdgeLinks ?? [])
                                        if (navCellPlugin.TryGetValue(e.Mesh.FormKey, out var other) && other != c.FormKey.ModKey.FileName) crossLinks++;
                        var byPlugin = landAt.Values.GroupBy(v => v).Select(g => $"{g.Key} {g.Count():N0}");
                        Console.WriteLine($"  land cells by plugin: {string.Join(", ", byPlugin)}; cells with a navmesh {navAt.Count:N0} (more than one: {twice}), without land under them {navAt.Keys.Count(k => !landAt.ContainsKey(k))}");
                        Console.WriteLine($"  edge links reaching no navmesh: {dangling}; links between cells of different land plugins: {crossLinks:N0}; navigation map entries {mods[^1].NavigationMeshInfoMaps.Sum(x => x.MapInfos.Count):N0} for {navKeys.Count:N0} navmeshes");
                        if (twice > 0 || dangling > 0 || navAt.Keys.Any(k => !landAt.ContainsKey(k)) || mods[^1].NavigationMeshInfoMaps.Sum(x => x.MapInfos.Count) != navKeys.Count) bad++;
                        // Overland doors (OverlandDoorLinks, as the conversion joins the towns' gates): a stand-in door
                        // plugin with a door on a navmesh triangle of 20 cells of each land plugin, joined to the navmeshes.
                        var doorsMod = new SkyrimMod(ModKey.FromFileName("DoorsTest.esp"), SkyrimRelease.SkyrimSE);
                        int placedDoors = 0;
                        foreach (var w in mods[^1].Worldspaces)
                        {
                            var ws = w.DeepCopy(new Worldspace.TranslationMask(defaultOn: true) { SubCells = false, TopCell = false });
                            ws.SubCells.Clear(); ws.TopCell = null;
                            doorsMod.Worldspaces.Add(ws);
                            foreach (var group in w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).Where(c => c.NavigationMeshes.Count > 0).GroupBy(c => c.FormKey.ModKey))
                                foreach (var c in group.Take(20))
                                {
                                    var nd = c.NavigationMeshes[0].Data!;
                                    var tri = nd.Triangles[0];
                                    var (a, b2, c2) = (nd.Vertices[tri.Vertices.X], nd.Vertices[tri.Vertices.Y], nd.Vertices[tri.Vertices.Z]);
                                    var copy = c.DeepCopy(new Cell.TranslationMask(defaultOn: true) { Temporary = false, Persistent = false, NavigationMeshes = false, Landscape = false });
                                    copy.Temporary.Clear(); copy.Persistent.Clear(); copy.NavigationMeshes.Clear(); copy.Landscape = null;
                                    var door = new PlacedObject(doorsMod) { Placement = new Placement { Position = new Noggog.P3Float((a.X + b2.X + c2.X) / 3, (a.Y + b2.Y + c2.Y) / 3, (a.Z + b2.Z + c2.Z) / 3) } };
                                    door.TeleportDestination = new TeleportDestination { Door = new Mutagen.Bethesda.Plugins.FormLink<IPlacedObjectGetter>(door.FormKey), Position = door.Placement.Position };
                                    copy.Temporary.Add(door);
                                    Skyrim.Terrain.TerrainBuilder.AddCell(ws, copy);
                                    placedDoors++;
                                }
                        }
                        var doorMasters = Skyrim.PluginMerger.Masters([Path.GetFileName(outPath), .. lands]);
                        doorsMod.WriteToBinary(Path.Combine(dir, "DoorsTest.esp"), new Mutagen.Bethesda.Plugins.Binary.Parameters.BinaryWriteParameters
                        { MastersListOrdering = new Mutagen.Bethesda.Plugins.Binary.Parameters.MastersListOrderingByLoadOrder(doorMasters) });
                        var dl = Skyrim.Terrain.OverlandDoorLinks.Build(Path.Combine(dir, lands[^1]), [Path.Combine(dir, "DoorsTest.esp")], Path.Combine(dir, "DoorsTestLinks.esp"), l => Console.WriteLine("  doors: " + l));
                        using (var links = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, "DoorsTestLinks.esp"), SkyrimRelease.SkyrimSE))
                            Console.WriteLine($"  door links plugin: masters {string.Join(", ", links.ModHeader.MasterReferences.Select(m => m.Master.FileName))}");
                        if (dl.Linked != placedDoors) { Console.WriteLine($"  PROBLEM: {dl.Linked} of {placedDoors} doors joined"); bad++; }
                    }
                    finally { foreach (var m in mods) m.Dispose(); }
                }
                // Land checks: heights match the field, markers on the ground, overlap identical between neighbours.
                static float Height(ILandscapeGetter land, int qx, int qy)
                {
                    var v = land.VertexHeightMap!;
                    float row = v.Offset;
                    for (int y = 0; y <= qy; y++) row += v.HeightMap[0, y];
                    float h = row;
                    for (int x = 1; x <= qx; x++) h += v.HeightMap[x, qy];
                    return h * 8;
                }
                var landCells = new Dictionary<int, Dictionary<(int, int), (int Offset, sbyte[] Deltas)>>();
                foreach (var l in lands)
                {
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(dir, l), SkyrimRelease.SkyrimSE);
                Console.WriteLine($"  {l}: {plugin.ModHeader.Stats.NumRecords} records and groups, {plugin.Worldspaces.Count} worldspaces, masters {string.Join(", ", plugin.ModHeader.MasterReferences.Select(m => m.Master.FileName))}");
                foreach (var ws in plugin.Worldspaces)
                {
                    int t = Enumerable.Range(0, layout.Count).First(i => layout.EditorId(i) == ws.EditorID);
                    var (ox, oy) = layout.Origin(t);
                    var cells = new Dictionary<(int, int), ILandscapeGetter>();
                    float worst = 0;
                    // (Cells without land: another land plugin's, carrying the navmeshes over them.)
                    foreach (var c in ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).Where(c => c.Landscape is not null))
                    {
                        cells[(c.Grid!.Point.X, c.Grid.Point.Y)] = c.Landscape!;
                        int gx = c.Grid.Point.X + ox - field.MinCellX, gy = c.Grid.Point.Y + oy - field.MinCellY;
                        for (int k = 0; k < 33; k += 8)
                            worst = MathF.Max(worst, MathF.Abs(Height(c.Landscape!, k, k) - field.At(gx * 32 + k, gy * 32 + k)));
                    }
                    int onLand = 0, off = 0;
                    foreach (var m in tiles[t].TopCell!.Persistent.OfType<PlacedObject>().Where(r => r.MapMarker is not null))
                    {
                        var p = m.Placement!.Position;
                        int cx = (int)MathF.Floor(p.X / 4096), cy = (int)MathF.Floor(p.Y / 4096);
                        if (!cells.TryGetValue((cx, cy), out var land) || p.Z <= 0) continue;
                        int qx = (int)MathF.Round((p.X - cx * 4096) / 128), qy = (int)MathF.Round((p.Y - cy * 4096) / 128);
                        if (MathF.Abs(Height(land, qx, qy) - p.Z) < 200) onLand++; else off++;
                    }
                    Console.WriteLine($"  {ws.EditorID}: {cells.Count} cells, worst height error {worst} units vs the field; markers on the land {onLand}, off it {off}");
                    if (worst > 8 || off > 0) bad++;
                    // A tile's cells from every land plugin it is split over.
                    if (!landCells.TryGetValue(t, out var tileCells)) landCells[t] = tileCells = [];
                    foreach (var kv in cells)
                        if (!tileCells.TryAdd((kv.Key.Item1 + ox, kv.Key.Item2 + oy), (kv.Value.VertexHeightMap!.Offset is var o ? (int)o : 0, kv.Value.VertexHeightMap!.HeightMap.Select(e => e.Value).ToArray())))
                        {
                            Console.WriteLine($"  PROBLEM: cell {kv.Key} of {ws.EditorID} in more than one land plugin");
                            bad++;
                        }
                }
                }
                foreach (var (a, ca) in landCells)
                    foreach (var (b, cb) in landCells)
                    {
                        if (a >= b) continue;
                        var shared = ca.Keys.Intersect(cb.Keys).ToList();
                        int diff = shared.Count(k => ca[k].Offset != cb[k].Offset || !ca[k].Deltas.SequenceEqual(cb[k].Deltas));
                        Console.WriteLine($"  tiles {a} and {b}: {shared.Count} shared cells, {diff} differ");
                        if (diff > 0) bad++;
                    }
                Console.WriteLine(bad == 0 ? "overland ok" : $"{bad} problems");
                return bad == 0 ? 0 : 2;
            }
            case "verify-terrain":
            {
                using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                var (v1, v2) = Skyrim.Terrain.TerrainVerifier.EdgeMismatch(skyrim.Worldspaces.First(w => w.EditorID == "Tamriel"));
                Console.WriteLine($"vanilla Tamriel edge mismatch: as [x,y] {v1}  transposed {v2}  (0 = correct convention)");
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm"), SkyrimRelease.SkyrimSE);
                var ws = plugin.Worldspaces.First(w => w.EditorID == DaggerfallPluginBuilder.WorldEditorId);
                var (m1, _) = Skyrim.Terrain.TerrainVerifier.EdgeMismatch(ws, int.MaxValue);
                var field = Skyrim.Terrain.HeightField.Generate(DfWorldMap.Load(arena2), TerrainSettingsFrom(opts));
                float src = Skyrim.Terrain.TerrainVerifier.SourceMismatch(ws, field);
                var cells = ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items).ToList();
                var land = cells.Select(c => c.Landscape).Where(l => l is not null).ToList();
                Console.WriteLine($"{ws.EditorID}: {cells.Count} cells, {land.Count} LAND, compressed {land.Count(l => l!.IsCompressed)}, layers/cell avg {land.Average(l => l!.Layers?.Count ?? 0):F1} max {land.Max(l => l!.Layers?.Count ?? 0)}");
                Console.WriteLine($"own edge mismatch {m1} units, max error vs source field {src} units (quantization step is 8)");
                // Bounds are cell coordinates; they must enclose the cells and stay near them (the engine sizes its grid from them).
                var gx = cells.Select(c => c.Grid!.Point.X).ToList(); var gy = cells.Select(c => c.Grid!.Point.Y).ToList();
                bool boundsOk = ws.ObjectBoundsMin.X <= gx.Min() && ws.ObjectBoundsMin.Y <= gy.Min() && ws.ObjectBoundsMax.X >= gx.Max() && ws.ObjectBoundsMax.Y >= gy.Max()
                    && ws.ObjectBoundsMax.X - ws.ObjectBoundsMin.X <= gx.Max() - gx.Min() + 2 && ws.ObjectBoundsMax.Y - ws.ObjectBoundsMin.Y <= gy.Max() - gy.Min() + 2;
                Console.WriteLine($"bounds ({ws.ObjectBoundsMin}) .. ({ws.ObjectBoundsMax}) vs cells x {gx.Min()}..{gx.Max()} y {gy.Min()}..{gy.Max()}: {(boundsOk ? "ok" : "WRONG")}; world map scale {ws.WorldMapOffsetScale}");
                return v1 == 0 && m1 == 0 && src <= 4 && boundsOk && ws.WorldMapOffsetScale > 0 ? 0 : 2;
            }
            case "df-palettes":
            {
                // Decodes every palette in ARENA2 (.COL: 8-byte header + 256 RGB; .PAL/.RAW: 256 RGB; SKYPAL.DAT: two
                // palettes): whether it is 6-bit VGA (0-63, x4 for 8-bit) or 8-bit, its brightness, a swatch sheet, and
                // a sprite (--sprite archive.record, default the seagull 201.11) drawn through it, into --out.
                string outDir = Path.GetFullPath(opts.GetValueOrDefault("out", "out/palettes"));
                Directory.CreateDirectory(outDir);
                var sprite = opts.GetValueOrDefault("sprite", "201.11").Split('.').Select(int.Parse).ToArray();
                var img = DfTexture.Load(arena2, sprite[0])!.Image(sprite[1]);
                foreach (var file in Directory.GetFiles(arena2).Where(f => Path.GetExtension(f).ToUpperInvariant() is ".COL" or ".PAL" or ".RAW" or ".DAT"
                             && new FileInfo(f).Length is 768 or 776 or 1536).OrderBy(f => f))
                {
                    var bytes = File.ReadAllBytes(file);
                    string name = Path.GetFileName(file);
                    if (name.Equals("ARROW.RAW", StringComparison.OrdinalIgnoreCase)) continue;
                    var palettes = bytes.Length == 1536 ? new[] { bytes[..768], bytes[768..] } : [bytes.Length == 776 ? bytes[8..] : bytes];
                    for (int k = 0; k < palettes.Length; k++)
                    {
                        var raw = palettes[k];
                        bool sixBit = raw.Max() <= 63;
                        var pal = raw.Select(b => (byte)(sixBit ? Math.Min(255, b * 255 / 63) : b)).ToArray();
                        double mean = pal.Select(b => (double)b).Average();
                        string label = palettes.Length > 1 ? $"{name}#{k}" : name;
                        Console.WriteLine($"{label,-16} {(sixBit ? "6-bit VGA (x4)" : "8-bit")}  max {raw.Max(),3}  mean {mean,5:F0}  header {(bytes.Length == 776 ? BitConverter.ToString(bytes, 0, 8) : "none")}");
                        // Swatch: 16 x 16 squares of 16 px; then the sprite through this palette at 4x, beside it.
                        int sw = 16 * 16, sh = 16 * 16, scale = 4, iw = img.Width * scale, ih = img.Height * scale;
                        int w = sw + 16 + iw, h = Math.Max(sh, ih);
                        var rgb = new byte[w * h * 3];
                        for (int i = 0; i < 256; i++)
                            for (int y = 0; y < 16; y++)
                                for (int x = 0; x < 16; x++)
                                {
                                    int o = ((i / 16 * 16 + y) * w + i % 16 * 16 + x) * 3;
                                    rgb[o] = pal[i * 3]; rgb[o + 1] = pal[i * 3 + 1]; rgb[o + 2] = pal[i * 3 + 2];
                                }
                        for (int y = 0; y < ih; y++)
                            for (int x = 0; x < iw; x++)
                            {
                                byte p = img.Pixels[y / scale * img.Width + x / scale];
                                int o = (y * w + sw + 16 + x) * 3;
                                if (p == 0) { rgb[o] = 60; rgb[o + 1] = 60; rgb[o + 2] = 60; continue; }
                                rgb[o] = pal[p * 3]; rgb[o + 1] = pal[p * 3 + 1]; rgb[o + 2] = pal[p * 3 + 2];
                            }
                        Util.Png.WriteRgb(Path.Combine(outDir, label.Replace('#', '_') + ".png"), w, h, rgb);
                    }
                }
                Console.WriteLine($"swatches and sprites in {outDir}");
                return 0;
            }
            case "sprite-models":
            {
                // Models for Daggerfall sprites Skyrim lacks (SpriteModels): --export writes the sprites + job list into
                // --dir (default tools/model3d/work) for tools/model3d/sprite2mesh.py.
                // --make exports them and runs the model maker: on the GPU with --gpu (short bursts, waiting whenever the
                // card passes 78 C), else on the CPU (much slower). Finished models are kept, so a run resumes.
                string dir = Path.GetFullPath(opts.GetValueOrDefault("dir", Path.Combine(Setup.Dependencies.Root, "model3d", "work")));
                if (opts.ContainsKey("export") || opts.ContainsKey("make"))
                    Console.WriteLine($"exported {Skyrim.SpriteModels.Export(arena2, dir)} of {Skyrim.SpriteModels.Targets.Count} sprites to {dir}");
                if (opts.TryGetValue("import", out var importDir))
                {
                    // Dev aid: the finished models as NIFs, textures and statics in a scratch plugin under --import DIR.
                    var test = new SkyrimMod(ModKey.FromFileName("SpriteModelTest.esp"), SkyrimRelease.SkyrimSE);
                    var problems = new List<string>();
                    var made = new Skyrim.Meshes.SpriteModelImporter(test, Path.GetFullPath(importDir), arena2).ImportAll(dir, problems);
                    foreach (var ((a, r), m) in made) Console.WriteLine($"  {a}.{r} ({m.Kind}): {m.Max.X - m.Min.X:F0} x {m.Max.Y - m.Min.Y:F0} x {m.Max.Z - m.Min.Z:F0}");
                    foreach (var p in problems) Console.WriteLine($"  problem: {p}");
                    TamrielMerge.Skyrim.TopicSubtypes.Fill(test);
                    test.WriteToBinary(Path.Combine(Path.GetFullPath(importDir), "SpriteModelTest.esp"));
                    Console.WriteLine($"imported {made.Count} models");
                }
                if (!opts.ContainsKey("make")) return 0;
                string? python = Setup.Dependencies.ModelPython;
                string script = Path.Combine(AppContext.BaseDirectory, "tools", "model3d", "sprite2mesh.py");
                if (!File.Exists(script)) script = Path.Combine(Setup.Dependencies.Root, "model3d", "sprite2mesh.py");
                if (python is null || !File.Exists(script)) { Console.WriteLine("Sprite models: the model maker isn't set up (run setup); the vanilla stand-ins are used."); return 0; }
                bool gpu = opts.ContainsKey("gpu");
                if (!gpu) Console.WriteLine("Sprite models: on the CPU (pass --gpu to use the graphics card); this takes a few hours the first time.");
                var psi = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false };
                foreach (var a in new[] { script, "--list", Path.Combine(dir, "sprites.txt"), "--out", Path.Combine(dir, "models"), "--upscaler", Setup.Dependencies.UpscaleModel, "--device", gpu ? "cuda:0" : "cpu" })
                    psi.ArgumentList.Add(a);
                psi.Environment["HF_HOME"] = Setup.Dependencies.ModelCache;
                if (!gpu) psi.Environment["CUDA_VISIBLE_DEVICES"] = "";
                using (var proc = System.Diagnostics.Process.Start(psi)!)
                {
                    proc.WaitForExit();
                    if (proc.ExitCode != 0) Console.WriteLine($"Sprite models: the model maker stopped ({proc.ExitCode}); what it finished is kept.");
                }
                // Every model held to its sprite: the outline check (and a contact sheet, models.png, to look them over).
                // Not again when no model changed since the last check (2026-10-15: it took 10 minutes a conversion with
                // nothing new made).
                string scores = Path.Combine(dir, "fidelity.txt");
                var meshes = Directory.Exists(Path.Combine(dir, "models")) ? Directory.GetFiles(Path.Combine(dir, "models"), "mesh.obj", SearchOption.AllDirectories) : [];
                if (File.Exists(scores) && meshes.Length > 0 && meshes.Max(File.GetLastWriteTimeUtc) <= File.GetLastWriteTimeUtc(scores))
                {
                    Console.WriteLine($"Sprite models: {meshes.Length} checked already ({scores})");
                    return 0;
                }
                var check = new System.Diagnostics.ProcessStartInfo(python) { UseShellExecute = false };
                foreach (var a in new[] { Path.Combine(Path.GetDirectoryName(script)!, "check.py"), Path.Combine(dir, "models"), Path.Combine(dir, "sprites.txt") })
                    check.ArgumentList.Add(a);
                check.Environment["CUDA_VISIBLE_DEVICES"] = "";
                using (var proc = System.Diagnostics.Process.Start(check)!) proc.WaitForExit();
                return 0;
            }
            case "df-water":
            {
                // Dev aid: dungeon blocks with water (their 199.10 start flat's sound index, Daggerfall Unity: level =
                // 8 x index), with the block's floor heights, and how many dungeons use them.
                var bsa = new DfBsa(Path.Combine(arena2, "BLOCKS.BSA"));
                var models = DfModel.LoadAll(arena2);
                var uses = new Dictionary<string, int>();
                var someLocation = new Dictionary<string, DfLocation>();
                foreach (var l in DfLocations.LoadAll(arena2).Where(l => l.DungeonBlocks.Count > 0))
                    foreach (var b in l.DungeonBlocks) { uses[b.BlockName] = uses.GetValueOrDefault(b.BlockName) + 1; someLocation.TryAdd(b.BlockName, l); }
                int shown = 0;
                foreach (var name in uses.Keys.OrderBy(n => n))
                {
                    var block = DfDungeonBlock.Load(bsa, name);
                    var marker = block.Objects.FirstOrDefault(o => o.Type == DfRdbObjectType.Flat && o.TextureArchive == 199 && o.TextureRecord == 10);
                    if (marker is null || marker.SoundIndex == 0) continue;
                    var floors = new DfDungeon { Location = someLocation[name], Blocks = [(block, new DfDungeonBlockPlacement(0, 0, name, true))] }.Polygons(models).Where(p => p.Normal.Y > 0.7f).Select(p => p.Points.Average(q => q.Y)).ToList();
                    if (shown++ < int.Parse(opts.GetValueOrDefault("n", "25")))
                        Console.WriteLine($"{name}: index {marker.SoundIndex} -> level {8 * marker.SoundIndex}; floors {floors.Min():F0}..{floors.Max():F0} (median {floors.OrderBy(f => f).ElementAt(floors.Count / 2):F0}); {uses[name]} dungeons");
                }
                Console.WriteLine($"{shown} of {uses.Count} dungeon blocks have water");
                // Message actions: text (11), riddles (12), door notices (99), by block and index.
                foreach (var name in uses.Keys.OrderBy(n => n))
                    foreach (var o in DfDungeonBlock.Load(bsa, name).Objects.Where(o => o.Action is { Flags: 11 or 12 or 99 }))
                        if (o.Action!.Flags != 99 || opts.ContainsKey("doors"))
                            Console.WriteLine($"  {name}: action {o.Action.Flags} index {o.SoundIndex} ({uses[name]} dungeons)");
                return 0;
            }
            case "water-forms":
            {
                // Dev aid: vanilla activators that are water (a water type), with their model and bounds: placeable water.
                using var esm = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
                foreach (var a in esm.Activators.Where(a => !a.WaterType.IsNull).Take(int.Parse(opts.GetValueOrDefault("n", "40"))))
                    Console.WriteLine($"{a.EditorID} {a.FormKey.ID:X6} model {a.Model?.File} water {a.WaterType.FormKey.ID:X6} bounds {a.ObjectBounds.First}..{a.ObjectBounds.Second}");
                return 0;
            }
            case "coin-check":
            {
                // Coin between the times (CoinTrade) in a built plugin (--plugin): members of its money changers, bankers
                // and pickpockets, its exchange topics and the TMMoneyChanger scripts they open.
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(Path.GetFullPath(opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm")), SkyrimRelease.SkyrimSE);
                foreach (var f in plugin.Factions.Where(f => f.EditorID is { } e && (e.EndsWith("MoneyChangers") || e.EndsWith("Bankers") || e.EndsWith("Pickpockets"))))
                {
                    var members = plugin.Npcs.Where(n => n.Factions.Any(r => r.Faction.FormKey == f.FormKey)).Select(n => n.Name?.String ?? n.EditorID).ToList();
                    Console.WriteLine($"{f.EditorID}: {members.Count} people ({string.Join(", ", members.Take(6))}{(members.Count > 6 ? ", ..." : "")})");
                }
                foreach (var t in plugin.DialogTopics.Where(t => t.EditorID?.EndsWith("Exchange") == true))
                    Console.WriteLine($"topic {t.EditorID} \"{t.Name?.String}\": {t.Responses.Count} line(s), fragment {t.Responses.FirstOrDefault()?.VirtualMachineAdapter?.Scripts.FirstOrDefault()?.Name}");
                foreach (var q in plugin.Quests)
                    foreach (var s in q.VirtualMachineAdapter?.Scripts.Where(s => s.Name is "TMMoneyChanger" or "TMPickpockets") ?? [])
                        Console.WriteLine($"quest {q.EditorID}: {s.Name} ({string.Join(", ", s.Properties.Select(p => p switch
                        {
                            IScriptIntPropertyGetter i => $"{p.Name}={i.Data}",
                            IScriptBoolPropertyGetter b => $"{p.Name}={b.Data}",
                            IScriptStringPropertyGetter str => $"{p.Name}={str.Data}",
                            IScriptIntListPropertyGetter il => $"{p.Name}[{il.Data.Count}]",
                            IScriptStringListPropertyGetter sl => $"{p.Name}[{sl.Data.Count}]",
                            _ => p.Name,
                        }))})");
                foreach (var m in plugin.MiscItems.Where(m => m.EditorID?.Contains("LetterOfCredit") == true))
                    Console.WriteLine($"item {m.EditorID} \"{m.Name?.String}\" model {m.Model?.File} weight {m.Weight}");
                return 0;
            }
            case "verify":
            {
                // Re-read a built plugin and check every reference it makes resolves, in its masters or in itself.
                string path = opts.GetValueOrDefault("plugin", "out/mod/Daggerfall.esm");
                using var plugin = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                var skyrimKeys = plugin.EnumerateMajorRecords().Select(r => r.FormKey).ToHashSet();
                foreach (var master in plugin.ModHeader.MasterReferences)
                {
                    // A master beside the plugin (the converted games, for the creatures plugin) before Skyrim's own.
                    string beside = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, master.Master.FileName);
                    using var m = SkyrimMod.CreateFromBinaryOverlay(File.Exists(beside) ? beside : Path.Combine(skyrimData, master.Master.FileName), SkyrimRelease.SkyrimSE);
                    skyrimKeys.UnionWith(m.EnumerateMajorRecords().Select(r => r.FormKey));
                }
                // Hard-coded engine forms that aren't records in Skyrim.esm: the player reference and player actor.
                skyrimKeys.Add(new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x14));
                skyrimKeys.Add(new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x7));
                Console.WriteLine($"masters: {string.Join(", ", plugin.ModHeader.MasterReferences.Select(m => m.Master))}  header version: {plugin.ModHeader.Stats.Version}");
                int records = 0, broken = 0;
                foreach (var rec in plugin.EnumerateMajorRecords())
                {
                    records++;
                    foreach (var link in rec.EnumerateFormLinks())
                        if (!link.FormKey.IsNull && !skyrimKeys.Contains(link.FormKey))
                        { broken++; Console.WriteLine($"  unresolved {link.FormKey} in {rec.EditorID}"); }
                }
                Console.WriteLine($"records: {records}, unresolved links: {broken}");
                foreach (var g in plugin.Books.GroupBy(b => b.Model?.File.DataRelativePath.Path).OrderByDescending(g => g.Count()))
                    Console.WriteLine($"  {g.Count(),3} x {g.Key}");
                if (plugin.Books.FirstOrDefault(b => b.EditorID == opts.GetValueOrDefault("sample", "DF_Book00000")) is { } sample)
                    Console.WriteLine($"--- {sample.EditorID} \"{sample.Name}\" value={sample.Value} weight={sample.Weight}\n{sample.BookText.String![..Math.Min(600, sample.BookText.String!.Length)]}");
                // Every TamrielMerge model and texture the plugin names must exist in the build output (--assets,
                // default the plugin's folder); vanilla paths live in Skyrim's archives and aren't checked.
                string assets = opts.GetValueOrDefault("assets", Path.GetDirectoryName(Path.GetFullPath(path))!);
                var referenced = plugin.EnumerateMajorRecords().SelectMany(r => r.EnumerateAssetLinks(Mutagen.Bethesda.Plugins.Assets.AssetLinkQuery.Listed))
                    .Select(a => a.DataRelativePath.Path).Where(p => p.Contains("tamrielmerge", StringComparison.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                var missingAssets = referenced.Where(a => !File.Exists(Path.Combine(assets, a))).ToList();
                Console.WriteLine($"TamrielMerge assets referenced: {referenced.Count}, missing from {assets}: {missingAssets.Count}");
                foreach (var a in missingAssets.Take(10)) Console.WriteLine($"  missing {a}");
                return broken == 0 && missingAssets.Count == 0 ? 0 : 2;
            }
            case "build":
            {
                if (arena2.Length == 0) { Console.Error.WriteLine("Daggerfall isn't installed through Steam or GOG."); return 1; }
                string outPath = opts.GetValueOrDefault("out", "out/mod/Daggerfall.esm");
                // --no-uncut leaves out uncut content (songs Daggerfall never plays).
                // --towns "Daggerfall,Wayrest" (town names, optionally "Name@Region"), "all", or "none"; default Daggerfall.
                // --land-plugins N splits the land into N plugins (default: as its size needs, OverlandTiles.LandPluginNames).
                if (opts.TryGetValue("land-plugins", out var landPlugins)) Skyrim.Terrain.OverlandTiles.LandPluginsWanted = int.Parse(landPlugins);
                return DaggerfallPluginBuilder.Build(arena2, skyrimData, outPath, TerrainSettingsFrom(opts), uncut: !opts.ContainsKey("no-uncut"),
                    towns: opts.GetValueOrDefault("towns", "Daggerfall@Daggerfall"));
            }
            default:
                Console.Error.WriteLine("""
                    usage: TamrielMerge <command> [--arena2 PATH] [--skyrim-data PATH] [--out PATH]
                      dump-books      write re-flowed Daggerfall books as text for review
                      probe-vanilla   print sample vanilla Skyrim book markup (--filter EDID)
                      terrain-preview generate the Iliac Bay terrain and write stats + a relief PNG
                      build           build Daggerfall.esm for Skyrim SE (--no-uncut: leave out uncut content)
                      verify          check a built plugin's references and terrain encoding
                    """);
                return 1;
        }
    }

    /// <summary>Music tools: WildMIDI, FluidSynth and the soundfont under tools/, xWMAEncode from the Creation Kit.</summary>
    public static DfMusic.Tools MusicTools(string skyrimData)
    {
        // The repository's tools folder: the nearest ancestor of the executable that has one (works from bin\Debug
        // and from other output folders such as out\devbin).
        string tools = "tools";
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "tools", "wildmidi"))) { tools = Path.Combine(dir.FullName, "tools"); break; }
        string game = Path.GetDirectoryName(Path.GetFullPath(skyrimData).TrimEnd(Path.DirectorySeparatorChar))!;
        return new DfMusic.Tools(
            Path.GetFullPath(Path.Combine(tools, "wildmidi", "wildmidi-0.5.0-windows", "x64", "wildmidi.exe")),
            Path.GetFullPath(Path.Combine(tools, "fluidsynth", "fluidsynth-v2.6.1-win10-x64-cpp11", "bin", "fluidsynth.exe")),
            Path.GetFullPath(Path.Combine(tools, "soundfonts", "GeneralUser-GS.sf2")),
            Path.Combine(game, "Tools", "Audio", "xwmaencode.exe"));
    }

    /// <summary>Where a game's upscaled textures are kept (upscale-textures writes them, its build reads them): in the
    /// tool's own tools folder, so a run keeps what earlier runs upscaled.</summary>
    internal static string UpscaleFolder(Game game) =>
        Path.Combine(Setup.Dependencies.Root, "upscale", game == Game.Daggerfall ? "out-df" : "out");

    /// <summary>Writes a file under a name of this process's own and moves it into place, so a reader (the app) never sees
    /// it half-written and two writers don't mix.</summary>
    static void WriteWhole(string path, string text)
    {
        string full = Path.GetFullPath(path), tmp = $"{full}.{Environment.ProcessId}.tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(tmp, text);
        File.Move(tmp, full, overwrite: true);
    }

    /// <summary>About how much a convert writes while it builds, by the games it converts (2026-10-13: Daggerfall's land,
    /// LOD, towns and dungeons came to about 30 GB before its dungeons), with room to spare.</summary>
    static long ConvertSize(Func<Game, bool> has) =>
        ((has(Game.Daggerfall) ? 40L : 0) + (has(Game.Morrowind) ? 6L : 0) + (has(Game.Oblivion) ? 8L : 0) + (has(Game.Redguard) ? 1L : 0) + 2L) << 30;

    static Skyrim.Terrain.TerrainSettings TerrainSettingsFrom(Dictionary<string, string> opts)
    {
        var s = new Skyrim.Terrain.TerrainSettings();
        if (opts.TryGetValue("pixels-per-cell", out var ppc)) s = s with { PixelsPerCell = int.Parse(ppc) };
        if (opts.TryGetValue("vscale", out var vs)) s = s with { VerticalScale = float.Parse(vs, System.Globalization.CultureInfo.InvariantCulture) };
        if (opts.TryGetValue("exaggeration", out var ex)) s = s with { MountainExaggeration = float.Parse(ex, System.Globalization.CultureInfo.InvariantCulture) };
        return s;
    }

    static Dictionary<string, string> ParseOptions(IEnumerable<string> args)
    {
        var result = new Dictionary<string, string>();
        string? key = null;
        foreach (var a in args)
        {
            if (a.StartsWith("--"))
            {
                if (key is not null) result[key] = "true";   // a flag without a value
                key = a[2..];
            }
            else if (key is not null) { result[key] = a; key = null; }
        }
        if (key is not null) result[key] = "true";
        return result;
    }
}
