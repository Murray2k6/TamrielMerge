using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// The quest sites one plugin adds (Daggerfall.esm, a town part, a dungeon part), for the quest machine: start-game
/// quests running TMDfQuestSites that register with DFQuestMachine. Per town its name, region, Daggerfall location id,
/// map pixel, location and map marker; per building a few of each type (Daggerfall picks a random one), with its street
/// door, where one arrives inside and the interior's quest markers (199.11 people and foes, 199.18 items; shared
/// interiors share them); per dungeon its type, region, doors, start marker and quest markers; and the named individuals
/// (Daggerfall's faction of type Individual) standing in the plugin's cells. Split into quests small enough for one
/// record's script data (64 KB).
/// </summary>
public static class QuestSites
{
    public sealed record Town(DfLocation Location, TownBuilder.Built Built);

    const int MaxBuildingsPerQuest = 900, MaxDungeonsPerQuest = 500, PerType = 3, PerHouseType = 2;

    static ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = key.ToLink<ISkyrimMajorRecordGetter>() };
    static ScriptObjectListProperty Objs(string name, IEnumerable<FormKey> keys)
    {
        var p = new ScriptObjectListProperty { Name = name };
        foreach (var k in keys) p.Objects.Add(new ScriptObjectProperty { Object = k.ToLink<ISkyrimMajorRecordGetter>() });
        return p;
    }
    static ScriptIntListProperty Ints(string name, IEnumerable<int> v) => new() { Name = name, Data = [.. v] };

    /// <summary>The map markers' EditorIDs as LocationPlacer.PlaceMarkers gives them (every location, in order), by
    /// (region, index), so plugins built on Daggerfall.esm find its markers.</summary>
    public static Dictionary<(int Region, int Index), string> MarkerIds(IEnumerable<DfLocation> all)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return all.ToDictionary(l => (l.Region, l.Index), l => LocationPlacer.UniqueEditorId(used, "DFMap" + LocationPlacer.Sanitize(l.Name)));
    }

    /// <summary>Daggerfall.esm's map markers by location (for the town and dungeon parts).</summary>
    public static Func<DfLocation, FormKey?> MasterMarkers(ISkyrimModGetter master, string arena2)
    {
        var ids = MarkerIds(DfLocations.LoadAll(arena2));
        var byEdid = new Dictionary<string, FormKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in master.EnumerateMajorRecords<IPlacedObjectGetter>())
            if (r.EditorID is { } e && e.StartsWith("DFMap", StringComparison.Ordinal)) byEdid.TryAdd(e, r.FormKey);
        return l => ids.TryGetValue((l.Region, l.Index), out var e) && byEdid.TryGetValue(e, out var k) ? k : null;
    }

    /// <summary>The named individuals placed in <paramref name="mod"/>'s interiors: (faction, reference).</summary>
    public static List<(int Faction, FormKey Ref)> Individuals(SkyrimMod mod, IEnumerable<TownPeople.Person> people)
    {
        var byNpc = people.Where(p => p.Faction is { Type: DfFactionType.Individual }).GroupBy(p => p.Npc.FormKey).ToDictionary(g => g.Key, g => g.First().Faction!.Id);
        var found = new List<(int, FormKey)>();
        var seen = new HashSet<int>();
        foreach (var cell in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(s => s.Cells))
            foreach (var r in cell.Persistent.OfType<PlacedNpc>())
                if (byNpc.TryGetValue(r.Base.FormKey, out var f) && seen.Add(f)) found.Add((f, r.FormKey));
        return found;
    }

    /// <summary>The quests (none when there is nothing to register).</summary>
    public static List<Quest> Build(SkyrimMod mod, string part, FormKey machine, IReadOnlyList<Town> towns,
        IReadOnlyList<(DfLocation Location, DungeonCellBuilder.Built Built)> dungeons, IReadOnlyDictionary<Cell, List<(PlacedObject Marker, bool Item)>> markers,
        IReadOnlyList<(int Faction, FormKey Ref)> individuals, Func<DfLocation, FormKey?> mapMarker)
    {
        // Where each arrival marker is (the building interiors).
        var cellOf = new Dictionary<FormKey, Cell>();
        foreach (var cell in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(s => s.Cells))
            foreach (var r in cell.Persistent) cellOf[r.FormKey] = cell;
        (FormKey Spawn, FormKey Item) MarkersIn(Cell? cell)
        {
            if (cell is null || !markers.TryGetValue(cell, out var list)) return (FormKey.Null, FormKey.Null);
            var spawn = list.FirstOrDefault(m => !m.Item).Marker?.FormKey ?? FormKey.Null;
            var item = list.FirstOrDefault(m => m.Item).Marker?.FormKey ?? spawn;
            return (spawn, item);
        }

        var quests = new List<Quest>();
        var t = new List<(Town Town, List<(int Type, FormKey Door, FormKey Arrival, FormKey Spawn, FormKey Item)> Buildings)>();
        foreach (var town in towns)
        {
            var list = new List<(int, FormKey, FormKey, FormKey, FormKey)>();
            foreach (var (type, doors) in town.Built.DoorsByType.OrderBy(d => d.Key))
            {
                bool house = (int)type is >= 17 and <= 22;
                foreach (var door in doors.Take(house ? PerHouseType : PerType))
                {
                    var arrival = door.VirtualMachineAdapter?.Scripts.SelectMany(s => s.Properties).OfType<ScriptObjectProperty>().FirstOrDefault(p => p.Name == "Arrival")?.Object.FormKey ?? FormKey.Null;
                    var (spawn, item) = MarkersIn(cellOf.GetValueOrDefault(arrival));
                    list.Add(((int)type, door.FormKey, arrival, spawn, item));
                }
            }
            t.Add((town, list));
        }

        // Chunks: whole towns up to MaxBuildingsPerQuest buildings, then dungeons, individuals in the first.
        var chunks = new List<(List<int> Towns, List<int> Dungeons)>();
        var cur = (Towns: new List<int>(), Dungeons: new List<int>());
        int count = 0;
        for (int i = 0; i < t.Count; i++)
        {
            if (count + t[i].Buildings.Count > MaxBuildingsPerQuest && cur.Towns.Count > 0) { chunks.Add(cur); cur = ([], []); count = 0; }
            cur.Towns.Add(i);
            count += t[i].Buildings.Count;
        }
        for (int d = 0; d < dungeons.Count; d++)
        {
            if (cur.Dungeons.Count >= MaxDungeonsPerQuest) { chunks.Add(cur); cur = ([], []); }
            cur.Dungeons.Add(d);
        }
        if (cur.Towns.Count > 0 || cur.Dungeons.Count > 0 || (chunks.Count == 0 && individuals.Count > 0)) chunks.Add(cur);

        for (int c = 0; c < chunks.Count; c++)
        {
            var (ti, di) = chunks[c];
            var ts = ti.Select(i => t[i]).ToList();
            var ds = di.Select(i => dungeons[i]).ToList();
            var first = new List<int>();
            int at = 0;
            foreach (var x in ts) { first.Add(at); at += x.Buildings.Count; }
            var bs = ts.SelectMany((x, i) => x.Buildings.Select(b => (Town: i, B: b))).ToList();
            var dMarkers = ds.Select(d => MarkersIn(d.Built.Cell)).ToList();
            var ind = c == 0 ? individuals : [];
            var q = new Quest(mod) { EditorID = $"DFQuestSites{part}_{c}", Name = "Daggerfall Quest Sites", Flags = Quest.Flag.StartGameEnabled };
            q.VirtualMachineAdapter = new QuestAdapter
            {
                Scripts =
                [
                    new ScriptEntry
                    {
                        Name = "TMDfQuestSites",
                        Properties =
                        [
                            Obj("Machine", machine),
                            Ints("Regions", ts.Select(x => x.Town.Location.Region).Concat(ds.Select(d => d.Location.Region)).Distinct().OrderBy(r => r)),
                            new ScriptStringListProperty { Name = "TownName", Data = [.. ts.Select(x => x.Town.Location.Name)] },
                            Ints("TownRegion", ts.Select(x => x.Town.Location.Region)), Ints("TownLocId", ts.Select(x => x.Town.Location.LocationId)),
                            Ints("TownX", ts.Select(x => x.Town.Location.PixelX)), Ints("TownY", ts.Select(x => x.Town.Location.PixelY)),
                            Ints("TownFirst", first), Ints("TownCount", ts.Select(x => x.Buildings.Count)),
                            Objs("TownLocation", ts.Select(x => x.Town.Built.Location.FormKey)),
                            Objs("TownMap", ts.Select(x => mapMarker(x.Town.Location) ?? FormKey.Null)),
                            Ints("BType", bs.Select(b => b.B.Type)), Ints("BTown", bs.Select(b => b.Town)),
                            Objs("BDoor", bs.Select(b => b.B.Door)), Objs("BArrival", bs.Select(b => b.B.Arrival)),
                            Objs("BSpawn", bs.Select(b => b.B.Spawn)), Objs("BItem", bs.Select(b => b.B.Item)),
                            new ScriptStringListProperty { Name = "DName", Data = [.. ds.Select(d => d.Location.Name)] },
                            Ints("DRegion", ds.Select(d => d.Location.Region)), Ints("DLocId", ds.Select(d => d.Location.LocationId)),
                            Ints("DType", ds.Select(d => (int)d.Location.DungeonType)),
                            Ints("DX", ds.Select(d => d.Location.PixelX)), Ints("DY", ds.Select(d => d.Location.PixelY)),
                            Objs("DMap", ds.Select(d => mapMarker(d.Location) ?? FormKey.Null)),
                            Objs("DDoor", ds.Select(d => d.Built.ExteriorDoor.FormKey)), Objs("DStart", ds.Select(d => d.Built.StartMarker.FormKey)),
                            Objs("DSpawn", dMarkers.Select(m => m.Spawn)), Objs("DItem", dMarkers.Select(m => m.Item)),
                            Ints("IFaction", ind.Select(i => i.Faction)), Objs("IRef", ind.Select(i => i.Ref)),
                        ],
                    },
                ],
            };
            mod.Quests.Add(q);
            quests.Add(q);
        }
        return quests;
    }
}
