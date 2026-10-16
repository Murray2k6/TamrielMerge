using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's law in Skyrim (docs/progression.md, "Crime by game and area"): each of the Iliac Bay's 62 regions is
/// a Skyrim crime faction of its own (DFCrime00-61, Daggerfall.esm at fixed numbers 0xDF0800 + region so the town and
/// dungeon plugins and saves keep them), named for the region. The region's townsfolk and guards belong to it (as
/// victims and witnesses); building interiors are owned by it, so taking a house's or a shop's things is theft. Skyrim
/// only sees the crime: its guards neither arrest nor attack for it (the faction's arrest and attack-on-sight are off),
/// TMDfLaw reads each rise in a region's crime gold as Daggerfall's crime (murder, assault, pickpocketing, trespassing,
/// theft; a street door bashed is breaking and entering, DFLawCrimeReport) and answers as Daggerfall Unity does: the City
/// Watch comes, the legal standing falls when they confront the player, and the court sentences. The region's jail is
/// Daggerfall's prison (<see cref="BuildPrison"/>): a cell made from a prison dungeon's entrance block, where the
/// sentence is served.
/// </summary>
public static class DfLaw
{
    public const int Regions = 62;
    const uint FactionBase = 0xDF0800;
    const uint ExteriorBase = 0xDF0880;
    const uint ReportKey = 0xDF08F0;
    /// <summary>The court's messages for each crime (DfQuestBuilder.Law): 0xDF0900 + 0-0x21.</summary>
    public const uint MessageKey = 0xDF0900;

    public static string EditorId(int region) => $"DFCrime{region:00}";
    public const string ReportId = "DFLawCrimeReport";

    static readonly Dictionary<(ModKey, int), Faction> regions = new();

    /// <summary>Daggerfall's crime values in Skyrim gold: Skyrim's own for the crimes (so a rise says which it was:
    /// murder 1000 and assault 40 are violent, pickpocketing 25 and trespassing 5 not, anything else is theft), and no
    /// Skyrim arrest or attack (TMDfLaw sends the City Watch).</summary>
    public static readonly CrimeFactions.Values Values = new(Arrest: false, AttackOnSight: false);

    /// <summary>The region's crime faction in <paramref name="mod"/> (made on first use; at its fixed number when
    /// <paramref name="fixedKey"/>). A plugin built on Daggerfall.esm swaps its copy for Daggerfall.esm's by EditorID
    /// (DaggerfallTowns.UseMasterRecords).</summary>
    public static Faction Region(SkyrimMod mod, int region, bool fixedKey = false)
    {
        region = Math.Clamp(region, 0, Regions - 1);
        lock (regions)
        {
            if (regions.TryGetValue((mod.ModKey, region), out var hit) && mod.Factions.ContainsKey(hit.FormKey)) return hit;
            string id = EditorId(region);
            var f = mod.Factions.FirstOrDefault(x => x.EditorID == id)
                ?? CrimeFactions.Make(mod, id, DfLocations.RegionNames[region], Values, fixedKey ? new FormKey(mod.ModKey, FactionBase + (uint)region) : null);
            return regions[(mod.ModKey, region)] = f;
        }
    }

    /// <summary>The global a street door sets to a Daggerfall crime number when the player is seen breaking in (TMDfLaw
    /// reads and clears it).</summary>
    public static GlobalShort CrimeReport(SkyrimMod mod, bool fixedKey = false)
    {
        lock (regions)
        {
            if (mod.Globals.FirstOrDefault(g => g.EditorID == ReportId) is GlobalShort own) return own;
            var g = fixedKey ? new GlobalShort(new FormKey(mod.ModKey, ReportKey), SkyrimRelease.SkyrimSE) : new GlobalShort(mod);
            g.EditorID = ReportId;
            g.Data = 0;
            mod.Globals.Add(g);
            return g;
        }
    }

    /// <summary>Every region's crime faction and the crime report, at their fixed numbers (Daggerfall.esm).</summary>
    public static void AddAll(SkyrimMod mod)
    {
        for (int r = 0; r < Regions; r++) Region(mod, r, fixedKey: true);
        CrimeReport(mod, fixedKey: true);
    }

    /// <summary>Daggerfall's crimes (Daggerfall Unity PlayerEntity.Crimes, 1-15), as the court names them.</summary>
    public static readonly string[] CrimeNames =
    [
        "attempted breaking and entering", "trespassing", "breaking and entering", "assault", "murder", "tax evasion",
        "criminal conspiracy", "vagrancy", "smuggling", "piracy", "high treason", "pickpocketing", "theft", "treason",
        "loan default",
    ];

    /// <summary>
    /// Each region's jail exterior: where a released prisoner walks out, by the region's largest town's map marker (a
    /// region without a town: its first location's). On the overland's persistent cell (before it is split into tiles),
    /// at fixed numbers 0xDF0880 + region.
    /// </summary>
    public static PlacedObject[] PlaceExteriors(SkyrimMod mod, Worldspace overland, List<(DfLocation Location, PlacedObject Marker)> markers)
    {
        var result = new PlacedObject[Regions];
        for (int r = 0; r < Regions; r++)
        {
            var here = markers.Where(m => m.Location.Region == r).ToList();
            var best = here.Where(m => m.Location.Type is DfLocationType.TownCity or DfLocationType.TownHamlet or DfLocationType.TownVillage)
                .OrderByDescending(m => m.Location.ExteriorBlocks.Length).ThenBy(m => m.Location.Name, StringComparer.Ordinal)
                .Concat(here).Select(m => m.Marker).FirstOrDefault();
            var at = best?.Placement?.Position ?? new P3Float(0, 0, 0);
            var exterior = new PlacedObject(new FormKey(mod.ModKey, ExteriorBase + (uint)r), SkyrimRelease.SkyrimSE)
            {
                EditorID = $"DFJail{r:00}Exterior",
                Base = CrimeFactions.XMarkerHeading.ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = new P3Float(at.X, at.Y - 256, at.Z), Rotation = new P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            overland.TopCell!.Persistent.Add(exterior);
            result[r] = exterior;
        }
        return result;
    }

    /// <summary>What <see cref="BuildPrison"/> made: the cell, the source dungeon, and each region's prison marker.</summary>
    public sealed record Prison(Cell Cell, string From, PlacedObject[] Markers);

    /// <summary>
    /// Daggerfall's prison: an interior made from the entrance block of the first of Daggerfall's prison dungeons (by
    /// region, then name), without its monsters or loot, its exit door locked (the court lets the prisoner out), and in
    /// it every region's jail (CrimeFactions.PlaceJail: prison marker, evidence chests, follower wait marker) with the
    /// exteriors <see cref="PlaceExteriors"/> placed. Build it last in Daggerfall.esm: what it makes takes the next
    /// numbers.
    /// </summary>
    public static Prison BuildPrison(SkyrimMod mod, string arena2, string outDir, PropReplacer props, SpellBuilder spells, Navmesh.NavmeshBuilder navmeshes, IReadOnlyList<DfLocation> locations,
        PlacedObject[] exteriors)
    {
        var source = locations.Where(l => l.DungeonType == DfDungeonType.Prison && l.DungeonBlocks.Any(b => b.IsStart))
            .OrderBy(l => l.Region).ThenBy(l => l.Name, StringComparer.Ordinal).First();
        var start = source.DungeonBlocks.First(b => b.IsStart);
        var jail = new DfLocation
        {
            Region = source.Region, RegionName = source.RegionName, Index = source.Index, Name = "Daggerfall Prison", MapId = source.MapId,
            LocationId = source.LocationId, Type = source.Type, DungeonType = DfDungeonType.Prison, PixelX = source.PixelX, PixelY = source.PixelY,
            BlockWidth = 0, BlockHeight = 0, ExteriorBlocks = [], Buildings = [],
            DungeonBlocks = [new DfDungeonBlockPlacement(0, 0, start.BlockName, true)],
        };
        // A builder of its own: no creatures, people or clutter; its exit door's partner stands in a worldspace that
        // isn't written.
        var builder = new DungeonCellBuilder(mod, arena2, DfModel.LoadAll(arena2), new DfBsa(Path.Combine(arena2, "BLOCKS.BSA")), outDir, navmeshes, props)
        { Spells = spells };
        var scratchKey = ModKey.FromFileName("TMScratch.esp");
        var nowhere = new Worldspace(new FormKey(scratchKey, 0x800), SkyrimRelease.SkyrimSE) { TopCell = new Cell(new FormKey(scratchKey, 0x801), SkyrimRelease.SkyrimSE) { Persistent = [], Temporary = [] } };
        var built = builder.Build(jail, nowhere, new P3Float(0, 0, 0));
        built.Cell.Name = "Prison";
        built.InteriorDoor.TeleportDestination = null;
        built.InteriorDoor.Lock = new LockData { Level = LockLevel.RequiresKey };
        var spot = built.StartMarker.Placement!;
        var markers = new PlacedObject[Regions];
        for (int r = 0; r < Regions; r++)
        {
            var j = CrimeFactions.PlaceJail(mod, Region(mod, r), built.Cell, spot.Position, spot.Rotation.Z, exteriors[r], $"DFJail{r:00}");
            markers[r] = j.PrisonMarker;
        }
        return new Prison(built.Cell, source.Name, markers);
    }
}
