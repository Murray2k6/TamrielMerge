using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using static TamrielMerge.Skyrim.PluginMerger;

namespace TamrielMerge.Skyrim;

/// <summary>
/// The last step of Daggerfall's build (`pack-plugins`): the world plugins joined into as few as a budget allows, the
/// game seeing the same records (user, 2026-10-29: "we can load so many esm files with skyrim and we need to find a
/// workaround"). Every plugin we write must stay under 1,900 MB, what Mutagen reads (the engine's own limit is 4 GB a
/// top-level group), and a build makes 10 to 14 full plugins of about 15 GB in all, so about 8 is the floor.
///
/// <para>Which plugins may join. Two groups of plugins join when all of these hold:</para>
/// <list type="bullet">
/// <item>The joined size stays under the budget: the files' sizes added (a copy dropped makes it smaller), half the
/// navmesh and land not compressed yet taken off (the merger's zlib: 2.2 times smaller, measured).</item>
/// <item>Its new records fit a plugin's 24-bit FormIDs: 16,775,168 from 0x800.</item>
/// <item>At most one of them is <see cref="Frozen"/>, and it gives the join its name. Those keep their name and FormIDs
/// because something outside the records knows them: Daggerfall.esm and Daggerfall Towns.esm by the FormIDs and plugin
/// names in script properties (FactionIds, TownIds, GuardIds), and any plugin with dialogue by its voice files
/// (sound\voice\&lt;plugin&gt;\...\&lt;quest&gt;_&lt;topic&gt;_&lt;info id&gt;_1.fuz). Without a frozen one the join takes the
/// first plugin's name.</item>
/// <item>A place in the load order exists where every plugin still loads after its masters: a third plugin that masters
/// one of them and is a master of the other leaves none (the cycle).</item>
/// <item>At that place every contested record still has the same winner. Plugins between two that join could win or lose
/// a record by the move: each record held by two or more plugins is looked at (by which plugins hold it; there are few
/// such sets, so a million contested cells cost a handful of comparisons).</item>
/// </list>
/// <para>Of all pairs that may join the largest result under the budget is joined, and again until none may
/// (best fit: it leaves the fewest bins).</para>
///
/// <para>Only Daggerfall's world plugins (<see cref="Joinable"/>) join: a game's own plugin and the small light ones stay
/// themselves (the parts can be switched separately; a light plugin takes no load-order slot anyway). Every other plugin
/// with a folded master is rewritten against the new names. Nothing is touched until the results are written beside
/// the plugins (in _pack) and verified: a failure leaves the build as it was.</para>
/// </summary>
public static class PluginPack
{
    public const string ManifestName = "plugins.json", ReportName = "pack-report.txt";
    const string WorkFolder = "_pack";
    /// <summary>The most a plugin may be on disk: under the 2 GB Mutagen reads.</summary>
    public const long DefaultBudget = 1_900L << 20;
    /// <summary>A plugin's own FormIDs: 0x800 to 0xFFFFFF.</summary>
    const long MaxOwn = 0xFFFFFF - 0x800 + 1;

    static readonly HashSet<string> Joinable = new([
        "Daggerfall.esm", .. Terrain.OverlandTiles.LandPluginNames, .. Terrain.OverlandTiles.NavmeshPluginNames,
        DaggerfallTowns.PluginName, DaggerfallDungeons.PluginName, .. Terrain.DfFoliage.PluginNames], StringComparer.OrdinalIgnoreCase);

    /// <summary>Keeps its name and FormIDs (see the class's rules).</summary>
    static bool Frozen(PluginScan p) =>
        p.Name.Equals("Daggerfall.esm", StringComparison.OrdinalIgnoreCase) || p.Name.Equals(DaggerfallTowns.PluginName, StringComparison.OrdinalIgnoreCase) || p.OwnTypes.ContainsKey("INFO");

    /// <summary>The final plugin set, written by the pack: names in load order, each one's masters and what joined it.</summary>
    public sealed record Entry(string Name, List<string> Masters, List<string> Joined, long Size);
    public sealed record Manifest(long Budget, List<Entry> Plugins, List<string> Folded);
    public sealed record Outcome(List<string> Order, List<string> Folded, bool Changed);

    public static Manifest? Read(string dir)
    {
        string path = Path.Combine(dir, ManifestName);
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path)); }
        catch (JsonException) { return null; }
    }

    /// <summary>The final plugins in load order, when <paramref name="dir"/> holds a manifest.</summary>
    public static List<string>? OrderIn(string dir) => Read(dir)?.Plugins.Select(p => p.Name).ToList();

    /// <summary>Whether <paramref name="dir"/>'s Daggerfall plugins are joined: a stopped run's pieces aren't there to carry on from.</summary>
    public static bool Packed(string dir) => Read(dir) is { Folded.Count: > 0 };

    sealed class Group
    {
        public required List<PluginScan> Members;
        public PluginScan Target => Members.FirstOrDefault(Frozen) ?? Members[0];
        public long Size => Members.Sum(m => m.Size - m.Uncompressed / 2);
        public long Own => Members.Sum(m => m.Own);
        public bool Single => Members.Count == 1;
    }

    /// <summary>The records two or more plugins hold, as the sets of plugins that hold them (a bit each, by load order).</summary>
    sealed class Contested(List<PluginScan> order)
    {
        public readonly Dictionary<ModKey, int> Ordinals = [];
        public readonly Dictionary<ulong, ulong> Holders = [];
        /// <summary>Of a contested record, the holders taken out of <see cref="Holders"/> as their copy equals the winner's.</summary>
        public readonly Dictionary<ulong, ulong> Same = [];
        readonly Dictionary<ulong, long>[] offsets = order.Select(_ => new Dictionary<ulong, long>()).ToArray();

        public int Ordinal(ModKey k) { if (!Ordinals.TryGetValue(k, out int i)) Ordinals[k] = i = Ordinals.Count; return i; }
        public static ulong Key(int ordinal, uint id) => (ulong)ordinal << 24 | (id & 0xFFFFFF);

        public void Fill()
        {
            var bit = order.Select((p, i) => (p.Key, i)).ToDictionary(x => x.Key, x => x.i);
            for (int i = 0; i < order.Count; i++)
                for (int k = 0; k < order[i].Overrides.Count; k++)
                {
                    uint fid = order[i].Overrides[k];
                    var owner = order[i].Masters[(int)(fid >> 24)];
                    ulong key = Key(Ordinal(owner), fid), mask = 1UL << i;
                    if (bit.TryGetValue(owner, out int b)) mask |= 1UL << b;
                    Holders[key] = Holders.GetValueOrDefault(key) | mask;
                    offsets[i][key] = order[i].OverrideOffsets[k];
                }
        }

        /// <summary>Takes out of each contested cell and worldspace the holders whose copy equals the winner's (their place in
        /// the order can't change what the game sees); returns how many holders went.</summary>
        public long Settle()
        {
            var bit = order.Select((p, i) => (p.Key, i)).ToDictionary(x => x.Key, x => x.i);
            var keyOf = Ordinals.ToDictionary(x => x.Value, x => x.Key);
            // The owners' own copies, which the scan didn't keep the places of.
            var keys = Holders.Where(h => BitOperations.PopCount(h.Value) > 1).Select(h => h.Key).ToArray();
            for (int i = 0; i < order.Count; i++)
            {
                var wanted = new Bits();
                int me = Ordinals.TryGetValue(order[i].Key, out int o) ? o : -1;
                bool any = false;
                foreach (var key in keys) if ((int)(key >> 24) == me) { wanted.Set((uint)key); any = true; }
                if (!any) continue;
                foreach (var (id, at) in OwnOffsets(order[i], wanted)) offsets[i][Key(me, id)] = at;
            }
            using var copies = new CopyComparer(order);
            var kept = new ulong[keys.Length];
            Parallel.For(0, keys.Length, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount - 2) }, n =>
            {
                ulong key = keys[n], mask = Holders[key], same = 0;
                int winner = 63 - BitOperations.LeadingZeroCount(mask);
                if (offsets[winner].TryGetValue(key, out long at) && copies.Load(winner, at) is { } last)
                    for (int i = 0; i < winner; i++)
                        if ((mask >> i & 1) != 0 && offsets[i].TryGetValue(key, out long other) && last.Equals(copies.Load(i, other))) same |= 1UL << i;
                kept[n] = same;
            });
            long gone = 0;
            for (int n = 0; n < keys.Length; n++)
                if (kept[n] != 0)
                {
                    Holders[keys[n]] &= ~kept[n];
                    Same[keys[n]] = kept[n];
                    gone += BitOperations.PopCount(kept[n]);
                }
            return gone;
        }

        /// <summary>The distinct sets, with how many records each holds; a set of one contests nothing.</summary>
        public Dictionary<ulong, long> Sets()
        {
            var sets = new Dictionary<ulong, long>();
            foreach (var mask in Holders.Values) if (BitOperations.PopCount(mask) > 1) sets[mask] = sets.GetValueOrDefault(mask) + 1;
            return sets;
        }
    }

    /// <summary>Whether this order of groups keeps every plugin after its masters and every contested record's winner.</summary>
    static bool Legal(List<Group> order, List<PluginScan> plugins, Dictionary<ulong, long> sets)
    {
        var at = new Dictionary<ModKey, int>();
        for (int g = 0; g < order.Count; g++) foreach (var m in order[g].Members) at[m.Key] = g;
        foreach (var p in plugins)
            foreach (var master in p.Masters)
                if (at.TryGetValue(master, out int g) && g > at[p.Key]) return false;
        foreach (var set in sets.Keys)
        {
            int winner = 63 - BitOperations.LeadingZeroCount(set);   // the load order's last
            int bestGroup = -1, best = -1;
            for (int i = 0; i < plugins.Count; i++)
                if ((set >> i & 1) != 0 && at[plugins[i].Key] is var g && (g > bestGroup || (g == bestGroup && i > best))) { bestGroup = g; best = i; }
            if (best != winner) return false;
        }
        return true;
    }

    /// <summary>The order with <paramref name="a"/> and <paramref name="b"/> joined, or null when no place keeps it legal.</summary>
    static List<Group>? Join(List<Group> order, Group a, Group b, List<PluginScan> plugins, Dictionary<ulong, long> sets)
    {
        var rest = order.Where(g => g != a && g != b).ToList();
        var pos = plugins.Select((p, i) => (p.Key, i)).ToDictionary(x => x.Key, x => x.i);
        var joined = new Group { Members = [.. a.Members.Concat(b.Members).OrderBy(m => pos[m.Key])] };
        // Tried from the later one's place backward: the fewest plugins moved over.
        for (int place = Math.Max(order.IndexOf(a), order.IndexOf(b)) - 1; place >= 0; place--)
        {
            var tried = new List<Group>(rest);
            tried.Insert(Math.Min(place, tried.Count), joined);
            if (Legal(tried, plugins, sets)) return tried;
        }
        return null;
    }

    /// <summary>The joins, as the final order of groups (see the class's rules).</summary>
    static List<Group> Plan(List<PluginScan> plugins, Dictionary<ulong, long> sets, long budget)
    {
        var order = plugins.Select(p => new Group { Members = [p] }).ToList();
        while (true)
        {
            (long Size, List<Group> Order)? best = null;
            for (int i = 0; i < order.Count; i++)
                for (int j = i + 1; j < order.Count; j++)
                {
                    Group a = order[i], b = order[j];
                    if (!a.Members.Concat(b.Members).All(m => Joinable.Contains(m.Name))) continue;
                    long size = a.Size + b.Size;
                    if (size >= budget || a.Own + b.Own > MaxOwn || a.Members.Concat(b.Members).Count(Frozen) > 1) continue;
                    if (best is { } b0 && size <= b0.Size) continue;
                    if (Join(order, a, b, plugins, sets) is { } joined) best = (size, joined);
                }
            if (best is null) return order;
            order = best.Value.Order;
        }
    }

    /// <summary>The plugins in <paramref name="dir"/> (not a build's .partN files), <paramref name="order"/> first, then by
    /// name, each moved after its masters.</summary>
    static List<string> LoadOrder(string dir, IEnumerable<string> order, out Dictionary<string, List<ModKey>> masters)
    {
        var part = new Regex(@"\.part\d+\.", RegexOptions.IgnoreCase);
        var files = Directory.GetFiles(dir).Select(f => Path.GetFileName(f)!)
            .Where(f => (f.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) && !part.IsMatch(f)).ToList();
        var known = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
        var wanted = order.Where(known.Contains).Concat(files.Order(StringComparer.OrdinalIgnoreCase)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var found = wanted.ToDictionary(f => f, f => MastersOf(Path.Combine(dir, f)), StringComparer.OrdinalIgnoreCase);
        var done = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sorted = new List<string>();
        void Visit(string f)
        {
            if (!done.Add(f)) return;
            foreach (var m in found[f]) if (known.Contains(m.FileName.String)) Visit(wanted.First(w => w.Equals(m.FileName.String, StringComparison.OrdinalIgnoreCase)));
            sorted.Add(f);
        }
        foreach (var f in wanted) Visit(f);
        masters = found;
        return sorted;
    }

    /// <summary>
    /// Measures <paramref name="dir"/>'s plugins, plans the joins and (without <paramref name="planOnly"/>) makes them,
    /// verifies them against their sources and puts them in place, with <see cref="ManifestName"/> and
    /// <see cref="ReportName"/>. Throws, leaving the plugins as they were, when a result fails a check.
    /// </summary>
    public static Outcome Run(string dir, long budget, IEnumerable<string> order, Action<string> log, bool planOnly = false)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var report = new StringBuilder();
        void Say(string s) { log(s); report.AppendLine(s); }
        var load = LoadOrder(dir, order, out var masters);
        // The plugins that matter: the joinable ones, what they master here, and all that master them, through each other.
        var byKey = load.ToDictionary(f => ModKey.FromFileName(f));
        var related = new HashSet<string>(load.Where(Joinable.Contains), StringComparer.OrdinalIgnoreCase);
        for (bool more = true; more;)
        {
            more = false;
            foreach (var f in load)
            {
                if (!related.Contains(f) && masters[f].Any(m => byKey.TryGetValue(m, out var n) && related.Contains(n))) more = related.Add(f) || more;
                if (Joinable.Contains(f)) foreach (var m in masters[f]) if (byKey.TryGetValue(m, out var n)) more = related.Add(n) || more;
            }
        }
        var scans = load.Where(related.Contains).Select(f => Scan(Path.Combine(dir, f))).ToList();
        if (scans.Count > 64) throw new InvalidOperationException($"{scans.Count} plugins depend on each other: the contested records' sets hold 64");
        Say($"Measured {scans.Count} of the {load.Count} plugins in {dir} in {clock.Elapsed.TotalSeconds:F0} s (budget {budget >> 20:N0} MB):");
        foreach (var p in scans)
        {
            var dependents = scans.Where(q => q.Masters.Contains(p.Key)).Select(q => q.Name).ToList();
            Say($"  {p.Name}: {p.Size / 1048576.0:N1} MB, {p.Types.Values.Sum():N0} records, {p.Own:N0} new ({string.Join(" ", p.Types.OrderByDescending(t => t.Value).Take(8).Select(t => $"{t.Key} {t.Value:N0}"))}){(p.Uncompressed > 0 ? $", {p.Uncompressed >> 20:N0} MB of navmesh and land not compressed" : "")}{(Frozen(p) ? ", keeps its name and FormIDs" : "")}{(Joinable.Contains(p.Name) ? "" : ", not joined")}");
            Say($"    masters: {string.Join(", ", p.Masters.Select(m => m.FileName.String).Where(m => byKey.ContainsKey(ModKey.FromFileName(m))))}{(p.Masters.Any(m => !byKey.ContainsKey(m)) ? $" and {p.Masters.Count(m => !byKey.ContainsKey(m))} of Skyrim's" : "")}; masters of: {(dependents.Count == 0 ? "none" : string.Join(", ", dependents))}");
        }
        var contested = new Contested(scans);
        contested.Fill();
        long settled = contested.Settle();
        var sets = contested.Sets();
        Say($"  {sets.Values.Sum():N0} records held by more than one plugin with different copies ({settled:N0} copies of cells and worldspaces equal to the winning one left out):");
        foreach (var (set, n) in sets.OrderByDescending(s => s.Value).Take(24))
            Say($"  {n:N0} records held by {string.Join(" + ", scans.Where((_, i) => (set >> i & 1) != 0).Select(p => p.Name))}");

        for (int attempt = 0; ; attempt++)
        {
            var groups = Plan(scans, sets, budget);
            var joins = groups.Where(g => !g.Single).ToList();
            Say($"Plan: {scans.Count(s => Joinable.Contains(s.Name))} joinable plugins into {groups.Count(g => g.Members.Any(m => Joinable.Contains(m.Name)))}:");
            foreach (var g in joins) Say($"  {g.Target.Name} ({g.Size / 1048576.0:N0} MB, {g.Own:N0} new records): {string.Join(" + ", g.Members.Select(m => m.Name))}");
            if (joins.Count == 0) Say("  nothing can join under the rules");
            // The final order: the groups in the related plugins' slots, the unrelated where they were.
            var finalOrder = new List<string>();
            {
                var slots = new Queue<string>(groups.Select(g => g.Target.Name));
                foreach (var f in load)
                    if (!related.Contains(f)) finalOrder.Add(f);
                    else if (slots.Count > 0) finalOrder.Add(slots.Dequeue());
            }
            var folded = joins.SelectMany(g => g.Members.Where(m => m != g.Target)).Select(m => m.Name).ToList();
            if (planOnly) return new Outcome(finalOrder, folded, false);
            string work = Path.Combine(dir, WorkFolder);
            if (Directory.Exists(work)) Directory.Delete(work, true);
            try
            {
                if (joins.Count == 0)
                {
                    WriteManifest(dir, budget, finalOrder, [], new Dictionary<string, List<string>>());
                    File.WriteAllText(Path.Combine(dir, ReportName), report.ToString());
                    return new Outcome(finalOrder, [], false);
                }
                Directory.CreateDirectory(work);
                var map = new FormMap();
                foreach (var g in joins) AssignIds(g, map, Say);
                var jobs = MakeJobs(groups, scans, map, finalOrder, work);
                long need = jobs.Sum(j => j.Job.Sources.Sum(s => new FileInfo(s).Length));
                if (Util.Drives.FreeBytes(dir) < need + (1L << 30))
                    throw new IOException($"packing needs {need >> 20:N0} MB free beside the plugins, and {Util.Drives.FreeBytes(dir) >> 20:N0} MB are");
                var dropped = new Dictionary<string, SortedDictionary<string, long>>();
                long over = 0;
                foreach (var (job, _) in jobs)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    var stats = WriteJoin(job);
                    dropped[job.Key.FileName.String] = stats.Dropped;
                    Say($"  {job.Key.FileName}: {stats.Bytes / 1048576.0:N1} MB written in {sw.Elapsed.TotalSeconds:F0} s, {stats.Rewritten:N0} records rewritten, {stats.Copied:N0} copied, {stats.Dropped.Values.Sum():N0} copies replaced by a later one{(stats.NaviEntries > 0 ? $", {stats.NaviEntries:N0} navmesh map entries" : "")}");
                    if (stats.Bytes >= budget && job.Sources.Count > 1) over = Math.Max(over, stats.Bytes - budget + (16L << 20));
                }
                if (over > 0)
                {
                    // The sizes added were a guess about compression: planned again that much tighter.
                    if (attempt >= 3) throw new InvalidOperationException("the joins stayed over the budget after three plans");
                    Say($"A join came out over the budget: planned again {over >> 20:N0} MB tighter");
                    budget -= over;
                    continue;
                }
                Verify(dir, jobs, scans, contested, map, groups, dropped, Say);
                // Put in place: the manifest first, so that a stop part-way is seen as packed and never resumed from.
                var joinedOf = joins.ToDictionary(g => g.Target.Name, g => g.Members.Select(m => m.Name).ToList(), StringComparer.OrdinalIgnoreCase);
                FoldCatalogs(dir, work, map, joins);
                var finalNames = finalOrder;
                WriteManifest(dir, budget, finalNames, folded, joinedOf, jobs.ToDictionary(j => j.Job.Key.FileName.String, j => j.Job.OutPath, StringComparer.OrdinalIgnoreCase));
                foreach (var f in Directory.GetFiles(work)) File.Move(f, Path.Combine(dir, Path.GetFileName(f)), overwrite: true);
                foreach (var f in folded)
                {
                    File.Delete(Path.Combine(dir, f));
                    File.Delete(CreatureCatalog.PathFor(Path.Combine(dir, f)));
                }
                WriteManifest(dir, budget, finalNames, folded, joinedOf);
                Say($"Packed in {clock.Elapsed.TotalMinutes:F1} min: {scans.Count} plugins to {scans.Count - folded.Count}, {folded.Count} folded ({string.Join(", ", folded)}); largest {finalNames.Where(n => File.Exists(Path.Combine(dir, n))).Max(n => new FileInfo(Path.Combine(dir, n)).Length) / 1048576.0:N1} MB");
                File.WriteAllText(Path.Combine(dir, ReportName), report.ToString());
                return new Outcome(finalNames, folded, true);
            }
            finally { if (Directory.Exists(work)) Directory.Delete(work, true); }
        }
    }

    /// <summary>The members' new FormIDs in the join: the target's stay, a member's stay where they are free, the rest take
    /// the first free ones from 0x800 (a build's town and dungeon parts number from the same ranges, so most of the
    /// dungeons' move).</summary>
    static void AssignIds(Group g, FormMap map, Action<string> say)
    {
        var target = g.Target;
        var used = new Bits();
        for (uint id = 0; id <= 0xFFFFFF; id++) if (target.OwnIds.Get(id)) used.Set(id);
        uint free = 0x800;
        foreach (var m in g.Members.Where(m => m != target))
        {
            var ids = new uint[1 << 24];
            long moved = 0;
            for (uint id = 0; id <= 0xFFFFFF; id++)
            {
                if (!m.OwnIds.Get(id)) continue;
                if (!used.Get(id)) { used.Set(id); continue; }
                while (free <= 0xFFFFFF && used.Get(free)) free++;
                if (free > 0xFFFFFF) throw new InvalidOperationException($"{target.Name} ran out of FormIDs");
                used.Set(free);
                ids[id] = free;
                moved++;
            }
            map.Move(m.Key, target.Key, moved > 0 ? ids : null);
            say($"  {m.Name} into {target.Name}: {moved:N0} of its {m.Own:N0} new records take new FormIDs");
        }
    }

    /// <summary>A plugin to write for each join, and for each other plugin with a folded master.</summary>
    static List<(JoinJob Job, Group? Group)> MakeJobs(List<Group> groups, List<PluginScan> scans, FormMap map, List<string> finalOrder, string work)
    {
        var rank = finalOrder.Select((f, i) => (f, i)).ToDictionary(x => x.f, x => x.i, StringComparer.OrdinalIgnoreCase);
        List<ModKey> MastersFor(Group g, ModKey key)
        {
            var list = new List<ModKey>();
            foreach (var m in g.Members.SelectMany(p => p.Masters).Select(map.Target))
                if (m != key && !list.Contains(m)) list.Add(m);
            // Skyrim's own as they came, the rest in the load order.
            return [.. list.Where(m => !rank.ContainsKey(m.FileName.String)), .. list.Where(m => rank.ContainsKey(m.FileName.String)).OrderBy(m => rank[m.FileName.String])];
        }
        var jobs = new List<(JoinJob, Group?)>();
        foreach (var g in groups)
        {
            var key = g.Target.Key;
            if (g.Single && !g.Target.Masters.Any(map.Moves)) continue;
            jobs.Add((new JoinJob(g.Members.Select(m => m.Path).ToList(), g.Members.IndexOf(g.Target), key, MastersFor(g, key), map, Path.Combine(work, g.Target.Name)), g));
        }
        return jobs;
    }

    /// <summary>The checks before anything is put in place: each result against its sources; record counts by type; no
    /// dangling link that wasn't dangling; every contested record's winner the same.</summary>
    static void Verify(string dir, List<(JoinJob Job, Group? Group)> jobs, List<PluginScan> scans, Contested contested, FormMap map, List<Group> groups,
        Dictionary<string, SortedDictionary<string, long>> dropped, Action<string> say)
    {
        var problems = new List<string>();
        var written = jobs.ToDictionary(j => j.Job.Key, j => Scan(j.Job.OutPath));
        // The final plugins: written ones and those left alone, in the final order of groups.
        var final = groups.Select(g => written.TryGetValue(g.Target.Key, out var w) ? w : g.Target).ToList();
        var before = new Known();
        var after = new Known();
        foreach (var p in scans) before.Add(p.Key, p.OwnIds, p.Navmeshes);
        foreach (var p in final) after.Add(p.Key, p.OwnIds, p.Navmeshes);
        foreach (var (job, _) in jobs)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var c = CheckJoin(job, before, after);
            say($"  {job.Key.FileName}: {c.Compared:N0} records read back and equal to their sources but {c.Different:N0}, {c.Copied:N0} copied; {c.Collisions:N0} FormID collisions; {c.DanglingAfter:N0} dangling links ({c.DanglingBefore:N0} before); {c.EdgeLinks:N0} navmesh edge links, {c.BadEdgeLinks:N0} not to a navmesh; {c.DoorLinks:N0} door links, {c.BadDoorLinks:N0} to nothing ({sw.Elapsed.TotalSeconds:F0} s)");
            problems.AddRange(c.Notes);
            if (c.Different > 0) problems.Add($"{job.Key.FileName}: {c.Different:N0} records differ from their sources");
            if (c.Collisions > 0) problems.Add($"{job.Key.FileName}: {c.Collisions:N0} FormID collisions");
            if (c.DanglingAfter != c.DanglingBefore) problems.Add($"{job.Key.FileName}: {c.DanglingAfter:N0} dangling links, {c.DanglingBefore:N0} before");
            if (c.BadEdgeLinks > 0) problems.Add($"{job.Key.FileName}: {c.BadEdgeLinks:N0} navmesh edge links to no navmesh");
            if (c.BadDoorLinks > 0) problems.Add($"{job.Key.FileName}: {c.BadDoorLinks:N0} door links to nothing");
            // Record counts by type: the sources' less the copies a later one replaced.
            var expected = new SortedDictionary<string, long>(StringComparer.Ordinal);
            foreach (var s in job.Sources.Select(s => scans.First(p => p.Path == s)))
                foreach (var (t, n) in s.Types) expected[t] = expected.GetValueOrDefault(t) + n;
            foreach (var (t, n) in dropped[job.Key.FileName.String]) expected[t] -= n;
            var actual = written[job.Key].Types;
            foreach (var t in expected.Keys.Union(actual.Keys).Where(t => expected.GetValueOrDefault(t) != actual.GetValueOrDefault(t)))
                problems.Add($"{job.Key.FileName}: {actual.GetValueOrDefault(t):N0} {t} records, {expected.GetValueOrDefault(t):N0} expected");
        }
        long allBefore = scans.Sum(s => s.Types.Values.Sum()), allDropped = dropped.Values.Sum(d => d.Values.Sum()), allAfter = final.Sum(s => s.Types.Values.Sum());
        say($"  records: {allBefore:N0} before, {allDropped:N0} copies replaced by a later one in their join, {allAfter:N0} after");
        if (allBefore - allDropped != allAfter) problems.Add($"records: {allBefore:N0} - {allDropped:N0} is not {allAfter:N0}");

        // Winners: for each contested record the plugin holding the last copy after is the join of the one that did before.
        var finalOf = new Dictionary<ModKey, int>();
        for (int g = 0; g < groups.Count; g++) foreach (var m in groups[g].Members) finalOf[m.Key] = g;
        var check = new Contested(final);
        foreach (var (k, i) in contested.Ordinals) check.Ordinals[k] = i;
        check.Fill();
        var keyOf = contested.Ordinals.ToDictionary(x => x.Value, x => x.Key);
        long changed = 0, contestedCount = 0;
        foreach (var (key, mask) in contested.Holders)
        {
            if (BitOperations.PopCount(mask) < 2) continue;
            contestedCount++;
            var was = scans[63 - BitOperations.LeadingZeroCount(mask)];
            var moved = map.Map(new FormKey(keyOf[(int)(key >> 24)], (uint)(key & 0xFFFFFF)));
            ulong nowKey = Contested.Key(check.Ordinal(moved.ModKey), moved.ID);
            if (check.Holders.TryGetValue(nowKey, out var now) && BitOperations.PopCount(now) > 1)
            {
                // Its last copy is the winner's, or one found equal to it.
                int last = 63 - BitOperations.LeadingZeroCount(now);
                ulong same = contested.Same.GetValueOrDefault(key);
                bool equal = false;
                for (int i = 0; i < scans.Count; i++) if ((same >> i & 1) != 0 && finalOf[scans[i].Key] == last) equal = true;
                if (last != finalOf[was.Key] && !equal) changed++;
            }
            else
            {
                // All its copies in one plugin now: they must all have joined.
                ulong groupsHolding = 0;
                for (int i = 0; i < scans.Count; i++) if ((mask >> i & 1) != 0) groupsHolding |= 1UL << finalOf[scans[i].Key];
                if (BitOperations.PopCount(groupsHolding) != 1) changed++;
            }
        }
        say($"  contested records: {contestedCount:N0}, {changed:N0} with another winner");
        if (changed > 0) problems.Add($"{changed:N0} contested records change winner");
        if (problems.Count > 0) throw new InvalidDataException("the packed plugins failed their checks, and the build is left as it was:\n  " + string.Join("\n  ", problems.Take(30)));
    }

    /// <summary>The creature lists (read by a later `creatures` step, keyed "000ABC:Plugin.esm") in the joins' FormIDs, a
    /// folded plugin's list added to its target's. Written to <paramref name="work"/>, to go with the plugins.</summary>
    static void FoldCatalogs(string dir, string work, FormMap map, List<Group> joins)
    {
        string Key(string k) => k.Length == 0 ? k : CreatureCatalog.Key(map.Map(CreatureCatalog.Key(k)));
        var catalogs = new Dictionary<string, CreatureCatalog>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(dir, "*.creatures.json"))
        {
            string name = Path.GetFileName(file)[..^".creatures.json".Length];
            string plugin = name + (File.Exists(Path.Combine(dir, name + ".esm")) ? ".esm" : ".esp");
            if (CreatureCatalog.Load(Path.Combine(dir, plugin)) is not { } c) continue;
            c.Creatures = c.Creatures.Select(x => x with { FormKey = Key(x.FormKey) }).ToList();
            c.Spots = c.Spots.Select(s => s with { Ref = Key(s.Ref), Cell = Key(s.Cell), Worldspace = Key(s.Worldspace) }).ToList();
            string target = map.Target(ModKey.FromFileName(plugin)).FileName.String;
            if (catalogs.TryGetValue(target, out var there))
            {
                there.Creatures = there.Creatures.Concat(c.Creatures).DistinctBy(x => x.FormKey).ToList();
                there.Spots.AddRange(c.Spots);
            }
            else { c.Plugin = target; catalogs[target] = c; }
        }
        foreach (var (plugin, c) in catalogs) c.Save(Path.Combine(work, plugin));
    }

    static void WriteManifest(string dir, long budget, List<string> order, List<string> folded, Dictionary<string, List<string>> joined, Dictionary<string, string>? pending = null)
    {
        var entries = new List<Entry>();
        foreach (var name in order)
        {
            string path = pending?.GetValueOrDefault(name) ?? Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            entries.Add(new Entry(name, MastersOf(path).Select(m => m.FileName.String).ToList(), joined.GetValueOrDefault(name) ?? [name], new FileInfo(path).Length));
        }
        string text = JsonSerializer.Serialize(new Manifest(budget, entries, folded), new JsonSerializerOptions { WriteIndented = true });
        string tmp = Path.Combine(dir, ManifestName + ".tmp");
        File.WriteAllText(tmp, text);
        File.Move(tmp, Path.Combine(dir, ManifestName), overwrite: true);
    }
}
