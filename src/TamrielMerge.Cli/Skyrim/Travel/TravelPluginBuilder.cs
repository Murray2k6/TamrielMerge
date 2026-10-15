using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Order;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim.Travel;

/// <summary>
/// "TamrielMerge Travel.esp" (docs/travel.md; user, 2026-10-12: "i need the travel like daggerfalls, like it can take
/// days to get to a town", in every world of "the users game list and what they own", seen "realtime seeing yourself
/// travel without loading"): Daggerfall's travel for every converted world in the folder and Skyrim's own. For each
/// world a TMTravelWorld quest (never run) holding its towns by region (map markers) and, for the Iliac Bay, Daggerfall's
/// map for pricing the road (TMTravelClimate: its climates and landmasses) and its ships (harbours, sea hubs and the
/// routes between them); the TMTravel quest running it all from the Tamriel menu's Travel tab; a holding cell with the
/// ship that sails and the marker walks head for. Built from whatever is in the folder, so a game not owned (not
/// converted) isn't in it. Light (ESL).
/// </summary>
public static class TravelPluginBuilder
{
    public const string PluginName = "TamrielMerge Travel.esp";
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey MapMarkerRefType = Sk(0x10F63C);
    static readonly FormKey ShipModelStatic = Sk(0x04DD2D);   // ShipLarge01

    /// <summary>The converted games' plugins, in the story's order (the time each world belongs to).</summary>
    static readonly (string Plugin, string World, int Story)[] Games =
    [
        ("Daggerfall.esm", "The Iliac Bay", 0),
        ("Redguard.esm", "Stros M'Kai", 1),
        ("Morrowind.esm", "Vvardenfell", 2),
        ("Oblivion.esm", "Cyrodiil", 3),
    ];

    /// <summary>Squares (map pixels a side) Daggerfall's map is kept in for travel (as DaggerfallPluginBuilder's).</summary>
    const int Step = 4;
    /// <summary>A landmass's character; no two the same to Papyrus, whose string comparisons ignore case.</summary>
    const string LandChars = "0123456789abcdefghijklmnopqrstuvwxyz!#$%&()*+-/<=>@[]^_{}~";

    public static int Build(string dir, string skyrimData, string? arena2, Action<string>? log = null, string? outDir = null, string? oblivionData = null)
    {
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
        using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
        var worlds = new List<FormKey>();
        var opened = new List<IDisposable>();
        var masters = new List<string> { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" };
        FormKey? dfSenses = null;
        try
        {
            foreach (var (plugin, worldName, story) in Games)
            {
                string path = Path.Combine(dir, plugin);
                if (!File.Exists(path)) continue;
                var src = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
                opened.Add(src);
                FormKey? world = story == 0 && arena2 is not null
                    ? Daggerfall(mod, src, arena2, log)
                    : MarkerWorld(mod, src, worldName, story, log);
                if (world is null && story == 3 && oblivionData is not null) world = OblivionWorld(mod, src, oblivionData, log);
                if (world is { } w) { worlds.Add(w); masters.Add(plugin); }
                // Daggerfall's "enemies nearby" (its enemy senses' quest) for journeys in the Iliac Bay.
                if (world is not null && story == 0) dfSenses = src.Quests.FirstOrDefault(q => q.EditorID == DfSenses.QuestEditorId)?.FormKey;
            }
            if (SkyrimWorld(mod, skyrim, log) is { } sw) worlds.Add(sw);
            if (worlds.Count == 0) { log?.Invoke("travel: no worlds"); return 0; }
            AddTravelQuest(mod, worlds, dfSenses);
            string outPath = Path.Combine(outDir ?? dir, PluginName);
            mod.WriteToBinary(outPath, new BinaryWriteParameters
            {
                MastersListOrdering = new MastersListOrderingByLoadOrder(masters.Select(m => ModKey.FromFileName(m)).ToArray()),
            });
            log?.Invoke($"{PluginName}: {worlds.Count} worlds");
            return worlds.Count;
        }
        finally
        {
            foreach (var o in opened) o.Dispose();
        }
    }

    // ---- worlds ----

    static Quest WorldQuest(SkyrimMod mod, string editorId, string name, int story, IEnumerable<FormKey> worldspaces,
        IReadOnlyList<(string Name, List<(string Town, FormKey Marker)> Towns)> groups, List<ScriptProperty> extra)
    {
        var lists = new List<FormKey>();
        foreach (var (gname, towns) in groups)
        {
            var list = new FormList(mod) { EditorID = editorId + "Group" + lists.Count };
            foreach (var (_, marker) in towns) list.Items.Add(marker.ToLink<ISkyrimMajorRecordGetter>());
            mod.FormLists.Add(list);
            lists.Add(list.FormKey);
        }
        var props = new List<ScriptProperty>
        {
            new ScriptStringProperty { Name = "WorldName", Data = name },
            new ScriptIntProperty { Name = "Story", Data = story },
            Objects("Worlds", worldspaces),
            new ScriptStringListProperty { Name = "GroupNames", Data = [.. groups.Select(g => g.Name)] },
            new ScriptStringListProperty { Name = "GroupTowns", Data = [.. groups.Select(g => string.Join("|", g.Towns.Select(t => t.Town.Replace("|", "/"))))] },
            Objects("GroupMarkers", lists),
            new ScriptFloatProperty { Name = "UnitsPerPixel", Data = 4096f },
        };
        props.AddRange(extra);
        var quest = new Quest(mod) { EditorID = editorId, Name = name + " (travel)" };
        quest.VirtualMachineAdapter = new QuestAdapter { Scripts = { new ScriptEntry { Name = "TMTravelWorld", Properties = [.. props] } } };
        mod.Quests.Add(quest);
        return quest;
    }

    static ScriptObjectListProperty Objects(string name, IEnumerable<FormKey> keys)
    {
        var p = new ScriptObjectListProperty { Name = name };
        foreach (var k in keys) p.Objects.Add(new ScriptObjectProperty { Object = k.ToLink<ISkyrimMajorRecordGetter>() });
        return p;
    }

    /// <summary>A world from its plugin's map markers for towns (cities, towns, settlements), by the region of the cell
    /// each stands in (or one group). Null when it has none.</summary>
    static FormKey? MarkerWorld(SkyrimMod mod, ISkyrimModGetter src, string worldName, int story, Action<string>? log)
    {
        var towns = new List<(string Group, string Town, FormKey Marker, IWorldspaceGetter World)>();
        foreach (var ws in src.Worldspaces)
        {
            var cells = ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items)
                .Where(c => c.Grid is not null).GroupBy(c => (c.Grid!.Point.X, c.Grid.Point.Y)).ToDictionary(g => g.Key, g => g.First());
            foreach (var r in (ws.TopCell?.Persistent ?? []).OfType<IPlacedObjectGetter>())
            {
                if (r.MapMarker is not { } mm || mm.Type is not (MapMarker.MarkerType.City or MapMarker.MarkerType.Town or MapMarker.MarkerType.Settlement)) continue;
                string name = mm.Name?.String ?? "";
                if (name.Length == 0 || r.Placement is null) continue;
                string group = worldName;
                var key = ((int)Math.Floor(r.Placement.Position.X / 4096), (int)Math.Floor(r.Placement.Position.Y / 4096));
                if (cells.TryGetValue(key, out var cell) && cell.Regions?.FirstOrDefault() is { } region
                    && region.TryResolve(src.ToImmutableLinkCache()) is { } reg)
                    group = RegionName(reg) ?? group;
                towns.Add((group, name, r.FormKey, ws));
            }
        }
        if (towns.Count == 0) { log?.Invoke($"travel: {worldName}: no town map markers"); return null; }
        var groups = towns.GroupBy(t => t.Group).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.OrderBy(t => t.Town, StringComparer.Ordinal).Select(t => (t.Town, t.Marker)).ToList())).ToList();
        var q = WorldQuest(mod, "TMTravelWorld" + Sanitize(worldName), worldName, story, towns.Select(t => t.World.FormKey).Distinct(), groups, []);
        log?.Invoke($"travel: {worldName}: {towns.Count} towns in {groups.Count} groups");
        return q.FormKey;
    }

    static string? RegionName(IRegionGetter region)
    {
        string? id = region.EditorID;
        if (id is null) return null;
        // "MWWestGashRegion" -> "West Gash"
        foreach (var prefix in new[] { "MW", "OB", "RG", "DF" }) if (id.StartsWith(prefix) && id.Length > prefix.Length && char.IsUpper(id[prefix.Length])) id = id[prefix.Length..];
        if (id.EndsWith("Region")) id = id[..^6];
        return System.Text.RegularExpressions.Regex.Replace(id, "(?<=[a-z])(?=[A-Z])", " ").Trim();
    }

    static string Sanitize(string s) => new([.. s.Where(char.IsLetterOrDigit)]);

    /// <summary>Skyrim: its cities, towns and settlements by hold (each map marker's location, up to the hold).</summary>
    static FormKey? SkyrimWorld(SkyrimMod mod, ISkyrimModGetter skyrim, Action<string>? log)
    {
        var tamriel = skyrim.Worldspaces.FirstOrDefault(w => w.EditorID == "Tamriel");
        if (tamriel is null) return null;
        var found = new List<(string Town, FormKey Marker, float X, float Y)>();
        var gates = new Gates(skyrim, tamriel);
        foreach (var ws in skyrim.Worldspaces.Where(w => w.FormKey == tamriel.FormKey || w.Parent?.Worldspace.FormKey == tamriel.FormKey))
            foreach (var r in (ws.TopCell?.Persistent ?? []).OfType<IPlacedObjectGetter>())
            {
                // Cities, towns and settlements; the hold capitals have marker types of their own ("WhiterunCapitol").
                if (r.MapMarker is not { } mm || !(mm.Type is MapMarker.MarkerType.City or MapMarker.MarkerType.Town or MapMarker.MarkerType.Settlement
                    || mm.Type.ToString().EndsWith("Capitol"))) continue;
                string name = mm.Name?.String ?? "";
                if (name.Length == 0 || r.Placement is null) continue;
                var p = r.Placement.Position;
                // A walled city's marker is inside it (its own worldspace): the journey ends at its gate.
                FormKey target = ws.FormKey == tamriel.FormKey ? r.FormKey : gates.Into(ws.FormKey, p.X, p.Y) ?? FormKey.Null;
                if (target.IsNull) continue;
                found.Add((name, target, p.X, p.Y));
            }
        // Each town in the hold of the nearest hold capital (the nine capitals are among the towns).
        var towns = ByNearest(found, SkyrimHolds);
        if (towns.Count == 0) return null;
        var groups = towns.GroupBy(t => t.Group).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.OrderBy(t => t.Town, StringComparer.Ordinal).Select(t => (t.Town, t.Marker)).ToList())).ToList();
        var q = WorldQuest(mod, "TMTravelWorldSkyrim", "Skyrim", 4, [tamriel.FormKey], groups, []);
        log?.Invoke($"travel: Skyrim: {towns.Count} towns in {groups.Count} holds");
        return q.FormKey;
    }

    /// <summary>A worldspace's load doors into its child worldspaces (walled cities), to end a journey at a city's gate.</summary>
    sealed class Gates
    {
        readonly List<(FormKey Door, FormKey Into, float X, float Y)> doors = [];

        public Gates(ISkyrimModGetter plugin, IWorldspaceGetter outside)
        {
            // Which worldspace each persistent reference of the plugin stands in.
            var worldOf = new Dictionary<FormKey, FormKey>();
            foreach (var ws in plugin.Worldspaces)
                foreach (var r in ws.TopCell?.Persistent ?? []) worldOf[r.FormKey] = ws.FormKey;
            foreach (var r in (outside.TopCell?.Persistent ?? []).OfType<IPlacedObjectGetter>())
                if (r.TeleportDestination is { } d && r.Placement is { } p && worldOf.TryGetValue(d.Door.FormKey, out var into) && into != outside.FormKey)
                    doors.Add((r.FormKey, into, p.Position.X, p.Position.Y));
        }

        /// <summary>The door nearest (x, y) leading into the worldspace <paramref name="into"/>, if any.</summary>
        public FormKey? Into(FormKey into, float x, float y) =>
            doors.Where(d => d.Into == into).OrderBy(d => (d.X - x) * (d.X - x) + (d.Y - y) * (d.Y - y)).Select(d => (FormKey?)d.Door).FirstOrDefault();
    }

    /// <summary>Skyrim's hold capitals and their holds.</summary>
    static readonly Dictionary<string, string> SkyrimHolds = new()
    {
        ["Solitude"] = "Haafingar", ["Markarth"] = "The Reach", ["Whiterun"] = "Whiterun Hold", ["Windhelm"] = "Eastmarch",
        ["Riften"] = "The Rift", ["Dawnstar"] = "The Pale", ["Falkreath"] = "Falkreath Hold", ["Morthal"] = "Hjaalmarch",
        ["Winterhold"] = "Winterhold",
    };

    /// <summary>Cyrodiil's cities and their counties.</summary>
    static readonly Dictionary<string, string> CyrodiilCounties = new()
    {
        ["Anvil"] = "Anvil (the Gold Coast)", ["Bravil"] = "Bravil", ["Bruma"] = "Bruma", ["Cheydinhal"] = "Cheydinhal",
        ["Chorrol"] = "Chorrol", ["Imperial City"] = "The Imperial City", ["Kvatch"] = "Kvatch", ["Leyawiin"] = "Leyawiin",
        ["Skingrad"] = "Skingrad",
    };

    /// <summary>Towns grouped by the nearest of the named seats among them (seat name -> group); with none found, one.</summary>
    static List<(string Group, string Town, FormKey Marker)> ByNearest(List<(string Town, FormKey Marker, float X, float Y)> found, Dictionary<string, string> seats)
    {
        var present = found.Where(t => seats.ContainsKey(t.Town)).GroupBy(t => t.Town).Select(g => g.First()).ToList();
        return found.Select(t =>
        {
            if (present.Count == 0) return ("Towns", t.Town, t.Marker);
            var seat = present.MinBy(c => (c.X - t.X) * (c.X - t.X) + (c.Y - t.Y) * (c.Y - t.Y));
            return (seats[seat.Town], t.Town, t.Marker);
        }).ToList();
    }

    /// <summary>
    /// Cyrodiil: the converted Oblivion.esm has no map markers, so Oblivion's own (cities and settlements, read from the
    /// Oblivion install, which is only read) are placed in the converted Tamriel worldspace at the same spots (the
    /// conversion keeps positions), as markers of this plugin's in its persistent cell; by county (the nearest city).
    /// </summary>
    static FormKey? OblivionWorld(SkyrimMod mod, ISkyrimModGetter converted, string oblivionData, Action<string>? log)
    {
        string esm = Path.Combine(oblivionData, "Oblivion.esm");
        if (!File.Exists(esm)) return null;
        var target = converted.Worldspaces.FirstOrDefault(w => w.EditorID == "OBTamriel");
        if (target?.TopCell is null) { log?.Invoke("travel: Cyrodiil: no converted Tamriel worldspace"); return null; }
        using var ob = Mutagen.Bethesda.Oblivion.OblivionMod.CreateFromBinaryOverlay(esm, Mutagen.Bethesda.Oblivion.OblivionRelease.Oblivion);
        var tam = ob.Worldspaces.FirstOrDefault(w => w.EditorID == "Tamriel");
        if (tam?.TopCell is null) return null;
        var gates = new Gates(converted, target);
        var convertedWorld = converted.Worldspaces.Where(w => w.EditorID is not null).GroupBy(w => w.EditorID!).ToDictionary(g => g.Key, g => g.First().FormKey);
        var found = new List<(string Town, FormKey Marker, float X, float Y)>();
        var ws = target.DeepCopy(new Worldspace.TranslationMask(defaultOn: true) { SubCells = false, TopCell = false });
        ws.SubCells.Clear();
        var top = target.TopCell.DeepCopy(new Cell.TranslationMask(defaultOn: true) { Temporary = false, Persistent = false, Landscape = false, NavigationMeshes = false });
        top.Persistent.Clear();
        top.Temporary.Clear();
        ws.TopCell = top;
        foreach (var obWs in ob.Worldspaces.Where(w => w.FormKey == tam.FormKey || w.Parent.FormKey == tam.FormKey))
            foreach (var r in (obWs.TopCell?.Persistent ?? []).OfType<Mutagen.Bethesda.Oblivion.IPlacedObjectGetter>())
            {
                if (r.MapMarker is not { } mm || r.Location is not { } pos) continue;
                if (!mm.Types.Any(t => t is Mutagen.Bethesda.Oblivion.MapMarker.Type.City or Mutagen.Bethesda.Oblivion.MapMarker.Type.Settlement)) continue;
                string name = mm.Name ?? "";
                if (name.Length == 0 || found.Any(f => f.Town == name)) continue;
                if (obWs.FormKey != tam.FormKey)
                {
                    // A walled city (its own worldspace; its markers are its gates and castle): one destination named
                    // for the city, the journey ending at its converted gate.
                    string city = obWs.Name is { Length: > 0 } wn ? wn : name;
                    if (found.Any(f => f.Town == city) || city.StartsWith("Test")) continue;
                    if (!convertedWorld.TryGetValue("OB" + Sanitize(obWs.EditorID ?? ""), out var cityWs) || gates.Into(cityWs, pos.Position.X, pos.Position.Y) is not { } gate) continue;
                    found.Add((city, gate, pos.Position.X, pos.Position.Y));
                    continue;
                }
                var marker = new PlacedObject(mod)
                {
                    EditorID = "TMTravelCyrodiil" + Sanitize(name),
                    Base = Sk(0x3B).ToNullableLink<IPlaceableObjectGetter>(),
                    Placement = new Placement { Position = new P3Float(pos.Position.X, pos.Position.Y, pos.Position.Z), Rotation = new P3Float(0, 0, 0) },
                    SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
                };
                top.Persistent.Add(marker);
                found.Add((name, marker.FormKey, pos.Position.X, pos.Position.Y));
            }
        if (found.Count == 0) { log?.Invoke("travel: Cyrodiil: Oblivion.esm has no city map markers"); return null; }
        mod.Worldspaces.Add(ws);
        var towns = ByNearest(found, CyrodiilCounties);
        var groups = towns.GroupBy(t => t.Group).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => (g.Key, g.OrderBy(t => t.Town, StringComparer.Ordinal).Select(t => (t.Town, t.Marker)).ToList())).ToList();
        var q = WorldQuest(mod, "TMTravelWorldCyrodiil", "Cyrodiil", 3, [target.FormKey], groups, []);
        log?.Invoke($"travel: Cyrodiil: {found.Count} towns (Oblivion's map markers) in {groups.Count} counties");
        return q.FormKey;
    }

    // ---- the Iliac Bay ----

    /// <summary>Daggerfall's world: its regions and towns as the overland quest has them (DFOverlandQuest), Daggerfall's
    /// map for the road's price and the landmasses, and its ships.</summary>
    static FormKey? Daggerfall(SkyrimMod mod, ISkyrimModGetter df, string arena2, Action<string>? log)
    {
        var quest = df.Quests.FirstOrDefault(q => q.EditorID == "DFOverlandQuest");
        var script = quest?.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == "TMDfOverland");
        if (script is null) { log?.Invoke("travel: Daggerfall.esm has no overland quest"); return null; }
        List<string> Strings(string n) => script.Properties.OfType<IScriptStringListPropertyGetter>().FirstOrDefault(p => p.Name == n)?.Data.ToList() ?? [];
        List<FormKey> Forms(string n) => script.Properties.OfType<IScriptObjectListPropertyGetter>().FirstOrDefault(p => p.Name == n)?.Objects.Select(o => o.Object.FormKey).ToList() ?? [];
        var regionNames = Strings("RegionNames");
        var regionTowns = Strings("RegionTowns");
        var regionLists = Forms("RegionMarkers");
        var tiles = Forms("Tiles");
        var world = df.Worldspaces.FirstOrDefault(w => tiles.Count > 0 && w.FormKey == tiles[0]);
        if (world is null || regionNames.Count == 0) { log?.Invoke("travel: Daggerfall's overland isn't one worldspace"); return null; }
        if (tiles.Count > 1) log?.Invoke("travel: Daggerfall's overland is in tiles: travel stays within one");
        // The map's frame: the worldspace's bounds in cells (one worldspace, centred on 0,0).
        var min = world.ObjectBoundsMin;
        var max = world.ObjectBoundsMax;
        int cellsX = (int)Math.Round(max.X - min.X);
        float unitsPerPixel = 4096f * cellsX / DfWorldMap.Width;
        float westX = min.X * 4096f, northY = max.Y * 4096f;

        // The towns, as the overland quest lists them (its region lists hold their map markers).
        var lists = df.FormLists.Where(f => regionLists.Contains(f.FormKey)).ToDictionary(f => f.FormKey);
        var groups = new List<(string, List<(string, FormKey)>)>();
        for (int r = 0; r < regionNames.Count && r < regionLists.Count; r++)
        {
            if (!lists.TryGetValue(regionLists[r], out var list)) continue;
            var names = regionTowns[r].Split('|');
            var towns = list.Items.Select((m, i) => (i < names.Length ? names[i] : "?", m.FormKey)).ToList();
            groups.Add((regionNames[r], towns));
        }
        var markerAt = new Dictionary<FormKey, (float X, float Y)>();
        foreach (var r in (world.TopCell?.Persistent ?? []).OfType<IPlacedObjectGetter>())
            if (r.Placement is { } p) markerAt[r.FormKey] = (p.Position.X, p.Position.Y);

        // Daggerfall's map in squares: terrain for the price, landmasses for what a ship must reach.
        var map = DfWorldMap.Load(arena2);
        int sw = DfWorldMap.Width / Step, sh = DfWorldMap.Height / Step;
        var terrain = new char[sw, sh];
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                var counts = new Dictionary<char, int>();
                for (int j = 0; j < Step; j++)
                    for (int i = 0; i < Step; i++)
                    {
                        char c = TravelClass(map.ClimateAt(x * Step + i, y * Step + j));
                        counts[c] = counts.GetValueOrDefault(c) + 1;
                    }
                terrain[x, y] = counts.GetValueOrDefault('O') * 2 >= Step * Step ? 'O' : counts.Where(p => p.Key != 'O').MaxBy(p => p.Value).Key;
            }
        (int X, int Y) Square(float x, float y) => ((int)Math.Clamp((x - westX) / unitsPerPixel / Step, 0, sw - 1), (int)Math.Clamp((northY - y) / unitsPerPixel / Step, 0, sh - 1));
        (float X, float Y) Units(int sx, int sy) => (westX + (sx * Step + Step / 2f) * unitsPerPixel, northY - (sy * Step + Step / 2f) * unitsPerPixel);
        var landId = new int[sw, sh];
        for (int y = 0; y < sh; y++) for (int x = 0; x < sw; x++) landId[x, y] = -1;
        int masses = 0;
        for (int y = 0; y < sh; y++)
            for (int x = 0; x < sw; x++)
            {
                if (terrain[x, y] == 'O' || landId[x, y] >= 0) continue;
                var q = new Queue<(int, int)>();
                q.Enqueue((x, y)); landId[x, y] = masses;
                while (q.Count > 0)
                {
                    var (a, b) = q.Dequeue();
                    foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        int nx = a + dx, ny = b + dy;
                        if (nx < 0 || ny < 0 || nx >= sw || ny >= sh || terrain[nx, ny] == 'O' || landId[nx, ny] >= 0) continue;
                        landId[nx, ny] = masses;
                        q.Enqueue((nx, ny));
                    }
                }
                masses++;
            }
        // Landmasses with towns get a character, the biggest first.
        var townMasses = groups.SelectMany(g => g.Item2).Where(t => markerAt.ContainsKey(t.Item2))
            .Select(t => { var s = Square(markerAt[t.Item2].X, markerAt[t.Item2].Y); return landId[s.X, s.Y]; }).Where(m => m >= 0)
            .GroupBy(m => m).OrderByDescending(g => g.Count()).Select(g => g.Key).ToList();
        var charOf = new Dictionary<int, char>();
        for (int i = 0; i < townMasses.Count && i < LandChars.Length; i++) charOf[townMasses[i]] = LandChars[i];
        var rows = new List<string>();
        var lands = new List<string>();
        for (int y = 0; y < sh; y++)
        {
            var row = new char[sw];
            var land = new char[sw];
            for (int x = 0; x < sw; x++)
            {
                row[x] = terrain[x, y];
                land[x] = terrain[x, y] == 'O' ? '.' : charOf.TryGetValue(landId[x, y], out var ch) ? ch : '?';
            }
            rows.Add(new string(row));
            lands.Add(new string(land));
        }
        var climate = new Quest(mod) { EditorID = "TMTravelClimateIliacBay", Name = "Iliac Bay map (travel)" };
        climate.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts =
            {
                new ScriptEntry
                {
                    Name = "TMTravelClimate",
                    Properties =
                    [
                        new ScriptStringListProperty { Name = "Rows", Data = [.. rows] },
                        new ScriptStringListProperty { Name = "Lands", Data = [.. lands] },
                        new ScriptIntProperty { Name = "Step", Data = Step },
                    ],
                },
            },
        };
        mod.Quests.Add(climate);

        // Harbours: Daggerfall's port towns, each with the nearest sea square ships leave from.
        var locations = DfLocations.LoadAll(arena2).Where(l => l.IsPort && l.Type is DfLocationType.TownCity or DfLocationType.TownHamlet or DfLocationType.TownVillage)
            .Select(l => (l.RegionName, l.Name)).ToHashSet();
        var ports = new List<(string Name, FormKey Marker, int SeaX, int SeaY)>();
        foreach (var (region, towns) in groups)
            foreach (var (name, marker) in towns)
            {
                if (!locations.Contains((region, name)) || !markerAt.TryGetValue(marker, out var at)) continue;
                var (tx, ty) = Square(at.X, at.Y);
                (int, int)? sea = null;
                for (int radius = 0; radius <= 6 && sea is null; radius++)
                    for (int dy = -radius; dy <= radius && sea is null; dy++)
                        for (int dx = -radius; dx <= radius; dx++)
                        {
                            int x = tx + dx, y = ty + dy;
                            if (x >= 0 && y >= 0 && x < sw && y < sh && terrain[x, y] == 'O') { sea = (x, y); break; }
                        }
                if (sea is { } s) ports.Add((name, marker, s.Item1, s.Item2));
            }
        // A landmass with towns but none of Daggerfall's harbours (an island) gets its town nearest the sea as one.
        var served = ports.Select(p => { var s = Square(markerAt[p.Marker].X, markerAt[p.Marker].Y); return landId[s.X, s.Y]; }).ToHashSet();
        foreach (var mass in townMasses.Where(m => !served.Contains(m)))
        {
            (string Name, FormKey Marker, int SeaX, int SeaY, int D)? best = null;
            foreach (var (region, towns) in groups)
                foreach (var (name, marker) in towns)
                {
                    if (!markerAt.TryGetValue(marker, out var at)) continue;
                    var (tx, ty) = Square(at.X, at.Y);
                    if (landId[tx, ty] != mass) continue;
                    for (int radius = 0; radius <= 8; radius++)
                    {
                        (int, int)? sea = null;
                        for (int dy = -radius; dy <= radius && sea is null; dy++)
                            for (int dx = -radius; dx <= radius; dx++)
                            {
                                int x = tx + dx, y = ty + dy;
                                if (x >= 0 && y >= 0 && x < sw && y < sh && terrain[x, y] == 'O') { sea = (x, y); break; }
                            }
                        if (sea is { } s) { if (best is null || radius < best.Value.D) best = (name, marker, s.Item1, s.Item2, radius); break; }
                    }
                }
            if (best is { } b) ports.Add((b.Name, b.Marker, b.SeaX, b.SeaY));
        }
        // Sea hubs: harbours within 20 squares of each other share one; routes between hubs over the sea.
        var hubs = new List<(int X, int Y)>();
        var portHub = new List<int>();
        foreach (var p in ports)
        {
            int h = hubs.FindIndex(hb => Math.Abs(hb.X - p.SeaX) + Math.Abs(hb.Y - p.SeaY) <= 20);
            if (h < 0) { hubs.Add((p.SeaX, p.SeaY)); h = hubs.Count - 1; }
            portHub.Add(h);
        }
        var routes = new List<string>();
        for (int a = 0; a < hubs.Count; a++)
        {
            var parts = new List<string>();
            var from = SeaPaths(terrain, sw, sh, hubs[a]);
            for (int b = 0; b < hubs.Count; b++)
            {
                if (a == b || from(hubs[b]) is not { } path) continue;
                var simple = Simplify(path, terrain, sw, sh);
                parts.Add(b + ":" + string.Join(" ", simple.Skip(1).Select(sq => { var u = Units(sq.X, sq.Y); return $"{u.X:F0},{u.Y:F0}"; })));
            }
            routes.Add(string.Join(";", parts));
        }
        var portList = new FormList(mod) { EditorID = "TMTravelPortsIliacBay" };
        foreach (var p in ports) portList.Items.Add(p.Marker.ToLink<ISkyrimMajorRecordGetter>());
        mod.FormLists.Add(portList);
        var extra = new List<ScriptProperty>
        {
            new ScriptFloatProperty { Name = "UnitsPerPixel", Data = unitsPerPixel },
            new ScriptFloatProperty { Name = "MapWestX", Data = westX },
            new ScriptFloatProperty { Name = "MapNorthY", Data = northY },
            new ScriptObjectProperty { Name = "Climate", Object = climate.ToLink<ISkyrimMajorRecordGetter>() },
            new ScriptObjectProperty { Name = "Ports", Object = portList.ToLink<ISkyrimMajorRecordGetter>() },
            new ScriptStringProperty { Name = "PortNames", Data = string.Join("|", ports.Select(p => p.Name.Replace("|", "/"))) },
            new ScriptStringProperty { Name = "PortSea", Data = string.Join("|", ports.Select(p => { var u = Units(p.SeaX, p.SeaY); return $"{u.X:F0},{u.Y:F0}"; })) },
            new ScriptStringProperty { Name = "PortHubs", Data = string.Join("|", portHub) },
            new ScriptStringProperty { Name = "HubPoints", Data = string.Join("|", hubs.Select(h => { var u = Units(h.X, h.Y); return $"{u.X:F0},{u.Y:F0}"; })) },
            new ScriptStringListProperty { Name = "HubRoutes", Data = [.. routes] },
        };
        var worldQuest = WorldQuest(mod, "TMTravelWorldIliacBay", "The Iliac Bay", 0, [world.FormKey], groups, extra);
        // The UnitsPerPixel WorldQuest sets for every world is replaced by the map's own.
        var props = worldQuest.VirtualMachineAdapter!.Scripts[0].Properties;
        props.Remove(props.First(p => p.Name == "UnitsPerPixel"));
        log?.Invoke($"travel: The Iliac Bay: {groups.Sum(g => g.Item2.Count)} towns in {groups.Count} regions, {masses} landmasses ({charOf.Count} with towns), {ports.Count} harbours, {hubs.Count} sea hubs");
        return worldQuest.FormKey;
    }

    static char TravelClass(DfClimate c) => c switch
    {
        DfClimate.Ocean => 'O',
        DfClimate.Desert or DfClimate.Desert2 => '0',
        DfClimate.Mountain => '1',
        DfClimate.Rainforest => '2',
        DfClimate.Swamp => '3',
        DfClimate.Subtropical => '4',
        _ => '5',
    };

    /// <summary>Shortest ways over the sea from a square (breadth first, eight ways): a function giving the squares from
    /// it to any other square, or null when the sea doesn't reach.</summary>
    static Func<(int X, int Y), List<(int X, int Y)>?> SeaPaths(char[,] terrain, int w, int h, (int X, int Y) start)
    {
        var prev = new Dictionary<(int, int), (int, int)>();
        var q = new Queue<(int X, int Y)>();
        q.Enqueue(start);
        prev[start] = start;
        while (q.Count > 0)
        {
            var (x, y) = q.Dequeue();
            for (int dy = -1; dy <= 1; dy++)
                for (int dx = -1; dx <= 1; dx++)
                {
                    int nx = x + dx, ny = y + dy;
                    if ((dx == 0 && dy == 0) || nx < 0 || ny < 0 || nx >= w || ny >= h || terrain[nx, ny] != 'O' || prev.ContainsKey((nx, ny))) continue;
                    prev[(nx, ny)] = (x, y);
                    q.Enqueue((nx, ny));
                }
        }
        return to =>
        {
            if (!prev.ContainsKey(to)) return null;
            var path = new List<(int, int)> { to };
            while (path[^1] != start) path.Add(prev[path[^1]]);
            path.Reverse();
            return path;
        };
    }

    /// <summary>A path cut to the squares where it must turn: from each kept square, the furthest one in a straight
    /// line over the sea.</summary>
    static List<(int X, int Y)> Simplify(List<(int X, int Y)> path, char[,] terrain, int w, int h)
    {
        bool Clear((int X, int Y) a, (int X, int Y) b)
        {
            int n = Math.Max(Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));
            for (int i = 0; i <= n; i++)
            {
                int x = (int)Math.Round(a.X + (b.X - a.X) * (double)i / Math.Max(n, 1)), y = (int)Math.Round(a.Y + (b.Y - a.Y) * (double)i / Math.Max(n, 1));
                if (x < 0 || y < 0 || x >= w || y >= h || terrain[x, y] != 'O') return false;
            }
            return true;
        }
        var kept = new List<(int, int)> { path[0] };
        int at = 0;
        while (at < path.Count - 1)
        {
            int next = at + 1;
            for (int j = path.Count - 1; j > at + 1; j--)
                if (Clear(path[at], path[j])) { next = j; break; }
            kept.Add(path[next]);
            at = next;
        }
        return kept;
    }

    // ---- the travel quest ----

    static void AddTravelQuest(SkyrimMod mod, List<FormKey> worlds, FormKey? dfSenses)
    {
        // A holding cell: the marker walks head for, and the ship (disabled until a voyage).
        var shipBase = new Mutagen.Bethesda.Skyrim.Activator(mod)
        {
            EditorID = "TMTravelShip",
            Name = "Ship",
            Model = new Model { File = @"Dungeons\Ship\ShipLarge01.nif" },
            ObjectBounds = new ObjectBounds { First = new P3Int16(-904, -2202, -33), Second = new P3Int16(904, 2112, 2247) },
        };
        mod.Activators.Add(shipBase);
        var waypoint = new PlacedObject(mod)
        {
            EditorID = "TMTravelWaypoint",
            Base = Sk(0x3B).ToNullableLink<IPlaceableObjectGetter>(),   // XMarker
            Placement = new Placement { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        var ship = new PlacedObject(mod)
        {
            EditorID = "TMTravelShipRef",
            Base = shipBase.ToNullableLink<IPlaceableObjectGetter>(),
            Placement = new Placement { Position = new P3Float(0, 1024, 0), Rotation = new P3Float(0, 0, 0) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)(0x400 | 0x800),   // persistent, initially disabled
        };
        var hold = new Cell(mod)
        {
            EditorID = "TMTravelHold",
            Flags = Cell.Flag.IsInteriorCell,
            Persistent = [waypoint, ship],
        };
        var block = new CellBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = [new CellSubBlock { BlockNumber = 0, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [hold] }] };
        mod.Cells.Records.Add(block);

        var quest = new Quest(mod)
        {
            EditorID = "TMTravelQuest",
            Name = "Travel",
            Flags = Quest.Flag.StartGameEnabled,
            Aliases = [new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(Sk(0x14)) }],
        };
        quest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts =
            {
                new ScriptEntry
                {
                    Name = "TMTravel",
                    Properties =
                    [
                        new ScriptObjectProperty { Name = "PlayerRef", Object = Sk(0x14).ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "GameHour", Object = Sk(0x38).ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "TimeScale", Object = Sk(0x3A).ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "Gold", Object = Sk(0xF).ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "StoryOrderQuest", Object = Sk(0x3372B).ToLink<ISkyrimMajorRecordGetter>() },
                        Objects("Worlds", worlds),
                        new ScriptObjectProperty { Name = "Ship", Object = ship.ToLink<ISkyrimMajorRecordGetter>() },
                        new ScriptObjectProperty { Name = "Waypoint", Object = waypoint.ToLink<ISkyrimMajorRecordGetter>() },
                        .. (dfSenses is { } senses ? new ScriptProperty[] { new ScriptObjectProperty { Name = "DfSenses", Object = senses.ToLink<ISkyrimMajorRecordGetter>() } } : []),
                    ],
                },
            },
            Aliases =
            [
                new QuestFragmentAlias
                {
                    Property = new ScriptObjectProperty { Object = quest.ToLink<ISkyrimMajorRecordGetter>(), Alias = 0 },
                    Scripts = [new ScriptEntry { Name = "TMTravelPlayer" }],
                },
            ],
        };
        mod.Quests.Add(quest);
    }
}
