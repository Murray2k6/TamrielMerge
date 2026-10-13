using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim.Terrain;

namespace TamrielMerge.Skyrim;

/// <summary>Places a named map marker for every Daggerfall location in the overland worldspace.</summary>
public static class LocationPlacer
{
    static readonly FormKey MapMarkerStatic = new(ModKey.FromFileName("Skyrim.esm"), 0x10);
    const int PersistentFlag = 0x400;

    public static List<(DfLocation Location, PlacedObject Marker)> PlaceMarkers(SkyrimMod mod, Worldspace ws, HeightField f, IEnumerable<DfLocation> locations)
    {
        var usedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var placed = new List<(DfLocation, PlacedObject)>();
        foreach (var loc in locations)
        {
            var pos = WorldPosition(f, loc.PixelX + 0.5, loc.PixelY + 0.5);
            bool town = loc.Type is DfLocationType.TownCity or DfLocationType.TownHamlet or DfLocationType.TownVillage;
            var marker = new PlacedObject(mod)
            {
                EditorID = UniqueEditorId(usedIds, "DFMap" + Sanitize(loc.Name)),
                Base = MapMarkerStatic.ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = pos, Rotation = new P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)PersistentFlag,
                MapMarker = new MapMarker
                {
                    Name = loc.Name,
                    Type = MarkerType(loc),
                    // Daggerfall shows towns on the travel map from the start; everything else must be found.
                    Flags = town ? MapMarker.Flag.Visible | MapMarker.Flag.CanTravelTo : 0,
                },
            };
            ws.TopCell!.Persistent.Add(marker);
            placed.Add((loc, marker));
        }
        return placed;
    }

    /// <summary>Skyrim world position (units) for a continuous Daggerfall map-pixel coordinate, on the terrain surface.</summary>
    public static P3Float WorldPosition(HeightField f, double u, double v)
    {
        double vx = u * HeightField.QuadsPerCell / f.Settings.PixelsPerCell;
        double vy = (f.CellsY * f.Settings.PixelsPerCell - v) * HeightField.QuadsPerCell / f.Settings.PixelsPerCell;
        int x0 = (int)Math.Floor(vx), y0 = (int)Math.Floor(vy);
        double fx = vx - x0, fy = vy - y0;
        double h = (f.At(x0, y0) * (1 - fx) + f.At(x0 + 1, y0) * fx) * (1 - fy) + (f.At(x0, y0 + 1) * (1 - fx) + f.At(x0 + 1, y0 + 1) * fx) * fy;
        float x = (float)(f.MinCellX * HeightField.QuadsPerCell * HeightField.UnitsPerQuad + vx * HeightField.UnitsPerQuad);
        float y = (float)(f.MinCellY * HeightField.QuadsPerCell * HeightField.UnitsPerQuad + vy * HeightField.UnitsPerQuad);
        return new P3Float(x, y, (float)Math.Max(h, 0));
    }

    static MapMarker.MarkerType MarkerType(DfLocation loc) => loc.Type switch
    {
        DfLocationType.TownCity => MapMarker.MarkerType.City,
        DfLocationType.TownHamlet => MapMarker.MarkerType.Town,
        DfLocationType.TownVillage => MapMarker.MarkerType.Settlement,
        DfLocationType.HomeFarms => MapMarker.MarkerType.Farm,
        DfLocationType.Tavern => MapMarker.MarkerType.Settlement,
        DfLocationType.HomeWealthy => MapMarker.MarkerType.NordicDwelling,
        DfLocationType.HomePoor => MapMarker.MarkerType.Shack,
        DfLocationType.ReligionTemple => MapMarker.MarkerType.Shrine,
        DfLocationType.ReligionCult => MapMarker.MarkerType.Altar,
        DfLocationType.Coven => MapMarker.MarkerType.Altar,
        DfLocationType.Graveyard => MapMarker.MarkerType.NordicRuins,
        DfLocationType.HomeYourShips => MapMarker.MarkerType.Docks,
        _ => loc.DungeonType switch
        {
            DfDungeonType.NaturalCave or DfDungeonType.SpiderNest or DfDungeonType.ScorpionNest or DfDungeonType.VolcanicCaves
                or DfDungeonType.HarpyNest => MapMarker.MarkerType.Cave,
            DfDungeonType.DragonsDen => MapMarker.MarkerType.DragonLair,
            DfDungeonType.Mine => MapMarker.MarkerType.Mine,
            DfDungeonType.OrcStronghold => MapMarker.MarkerType.OrcStronghold,
            DfDungeonType.GiantStronghold => MapMarker.MarkerType.GiantCamp,
            DfDungeonType.BarbarianStronghold => MapMarker.MarkerType.Camp,
            DfDungeonType.Crypt or DfDungeonType.Cemetery or DfDungeonType.DesecratedTemple or DfDungeonType.VampireHaunt => MapMarker.MarkerType.NordicRuins,
            DfDungeonType.Laboratory or DfDungeonType.Coven => MapMarker.MarkerType.NordicTower,
            _ => MapMarker.MarkerType.Fort,
        },
    };

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (var c in name) if (char.IsAsciiLetterOrDigit(c)) sb.Append(c);
        return sb.ToString();
    }

    public static string UniqueEditorId(HashSet<string> used, string id)
    {
        string candidate = id;
        for (int i = 2; !used.Add(candidate); i++) candidate = id + i;
        return candidate;
    }
}
