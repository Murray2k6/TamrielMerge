using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Daggerfall;
using TamrielMerge.Skyrim.Terrain;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Weather, ambient sound and interior atmosphere from Skyrim's own assets. Each Daggerfall climate gets a region
/// cloned from the vanilla Skyrim weather region that fits it (weather list and ambient sounds), covering the cells
/// where that climate dominates. Dungeons get an Imperial fort acoustic space and the dungeon image space.
/// </summary>
public static class AtmosphereBuilder
{
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    public static readonly FormKey FortAcousticSpace = Sk(0x4075D);   // IntDungeonFortAcousticSpace
    public static readonly FormKey DungeonImageSpace = Sk(0xA2687);   // DefaultImageSpaceDungeon

    /// <summary>Vanilla weather region standing in for each Daggerfall climate.</summary>
    static readonly Dictionary<DfClimate, string> RegionFor = new()
    {
        [DfClimate.Woodlands] = "WeatherPineForest",
        [DfClimate.Subtropical] = "WeatherFallForest",
        [DfClimate.Rainforest] = "WeatherFalkreath",
        [DfClimate.HauntedWoodlands] = "WeatherFFRiften",
        [DfClimate.Swamp] = "WeatherTundraMarsh",
        [DfClimate.Mountain] = "WeatherMountains",
        [DfClimate.MountainWoods] = "WeatherReach",
        [DfClimate.Desert] = "WeatherTundraNoPrecip",
        [DfClimate.Desert2] = "WeatherVolcanicTundraNoPrecip",
        [DfClimate.Ocean] = "WeatherFallForestNoPrecip",
    };

    public static void ApplyToDungeon(Cell cell, MusicType music)
    {
        cell.AcousticSpace = FortAcousticSpace.ToNullableLink<IAcousticSpaceGetter>();
        cell.ImageSpace = DungeonImageSpace.ToNullableLink<IImageSpaceGetter>();
        cell.Music = music.ToNullableLink<IMusicTypeGetter>();
    }

    /// <summary>
    /// A weather region per Daggerfall climate in each overland tile, covering the tile's cells where that climate
    /// dominates. Returns the areas per climate and each cell's region (tile, global grid), which the land plugins list
    /// on the cells themselves as the Creation Kit does.
    /// </summary>
    public static (Dictionary<DfClimate, int> Areas, Func<int, int, int, FormKey?> RegionOf) AddRegions(SkyrimMod mod, ISkyrimModGetter skyrim,
        IReadOnlyList<Worldspace> tiles, OverlandTiles layout, HeightField f, DfWorldMap map)
    {
        const int cellUnits = HeightField.QuadsPerCell * HeightField.UnitsPerQuad;
        int ppc = f.Settings.PixelsPerCell;

        // Dominant climate of each cell's Daggerfall pixels.
        var climate = new DfClimate[f.CellsX, f.CellsY];
        Parallel.For(0, f.CellsY, cy =>
        {
            for (int cx = 0; cx < f.CellsX; cx++)
            {
                int py0 = (f.CellsY - 1 - cy) * ppc; // cell rows run south to north, map rows north to south
                climate[cx, cy] = Enumerable.Range(0, ppc * ppc)
                    .Select(i => map.ClimateAt(cx * ppc + i % ppc, py0 + i / ppc))
                    .GroupBy(c => c).OrderByDescending(g => g.Count()).First().Key;
            }
        });

        var regions = new Dictionary<(int Tile, DfClimate), Region>();
        var counts = new Dictionary<DfClimate, int>();
        for (int t = 0; t < tiles.Count; t++)
        {
            var (x0, y0, x1, y1) = layout.Extent(t);
            var (ox, oy) = layout.Origin(t);
            var part = new DfClimate[x1 - x0 + 1, y1 - y0 + 1];
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                    part[x - x0, y - y0] = climate[x - f.MinCellX, y - f.MinCellY];
            foreach (var (clim, vanillaId) in RegionFor)
            {
                var rects = Rectangles(part, clim);
                if (rects.Count == 0) continue;
                var vanilla = skyrim.Regions.First(r => r.EditorID == vanillaId);
                var region = mod.Regions.DuplicateInAsNewRecord(vanilla);
                region.EditorID = "DFRegion" + clim + (layout.Single ? "" : "_" + t);
                region.Worldspace = tiles[t].ToNullableLink<IWorldspaceGetter>();
                region.RegionAreas.Clear();
                if (region.Sounds is not null) region.Sounds.Music.Clear(); // Daggerfall's music comes from the worldspace
                foreach (var rect in rects)
                {
                    float rx0 = (x0 - ox + rect.X0) * cellUnits, rx1 = (x0 - ox + rect.X1 + 1) * cellUnits;
                    float ry0 = (y0 - oy + rect.Y0) * cellUnits, ry1 = (y0 - oy + rect.Y1 + 1) * cellUnits;
                    var area = new RegionArea { EdgeFallOff = 0 };
                    area.RegionPointListData = [new P2Float(rx0, ry0), new P2Float(rx0, ry1), new P2Float(rx1, ry1), new P2Float(rx1, ry0)];
                    region.RegionAreas.Add(area);
                }
                regions[(t, clim)] = region;
                counts[clim] = counts.GetValueOrDefault(clim) + region.RegionAreas.Count;
            }
        }
        FormKey? RegionOf(int tile, int gx, int gy)
        {
            int cx = gx - f.MinCellX, cy = gy - f.MinCellY;
            if (cx < 0 || cy < 0 || cx >= f.CellsX || cy >= f.CellsY) return null;
            return regions.TryGetValue((tile, climate[cx, cy]), out var r) ? r.FormKey : null;
        }
        return (counts, RegionOf);
    }

    /// <summary>
    /// Daggerfall Unity's outdoor light for the Iliac Bay only. Weather records are shared by every worldspace, so
    /// the weathers its regions and climate use are copied ("DF" + name), relit, and the copies swapped in; other
    /// worlds (Skyrim, Morrowind, Redguard) keep their own. DFU: white sun scaled by the time of day and by weather
    /// (overcast 0.65, rain and snow 0.45, storm 0.25); ambient grey lerped from 0.25 at night to 0.9 by that same
    /// daylight (PlayerAmbientLight, SunlightManager, WeatherManager).
    /// </summary>
    public static int ApplyDaggerfallLight(SkyrimMod mod, ISkyrimModGetter skyrim, Worldspace ws)
    {
        var vanillaWeathers = skyrim.Weathers.ToDictionary(w => w.FormKey);
        var copies = new Dictionary<FormKey, Weather>();
        FormKey Copy(FormKey key)
        {
            if (copies.TryGetValue(key, out var done)) return done.FormKey;
            if (!vanillaWeathers.TryGetValue(key, out var vanilla)) return key;
            var w = mod.Weathers.DuplicateInAsNewRecord(vanilla);
            w.EditorID = "DF" + vanilla.EditorID;
            Relight(w);
            copies[key] = w;
            return w.FormKey;
        }
        foreach (var region in mod.Regions.Where(r => r.EditorID?.StartsWith("DFRegion") == true))
            foreach (var wt in region.Weather?.Weathers ?? [])
                wt.Weather.SetTo(Copy(wt.Weather.FormKey));
        // The worldspace's default climate: Tamriel's, copied with the relit weathers.
        if (skyrim.Climates.FirstOrDefault(c => c.FormKey == ws.Climate.FormKey) is { } vanillaClimate)
        {
            var climate = mod.Climates.DuplicateInAsNewRecord(vanillaClimate);
            climate.EditorID = "DFClimateIliacBay";
            foreach (var wt in climate.WeatherTypes) wt.Weather.SetTo(Copy(wt.Weather.FormKey));
            ws.Climate.SetTo(climate);
        }
        return copies.Count;
    }

    static void Relight(Weather w)
    {
        bool storm = w.Flags.HasFlag(Weather.Flag.Rainy) && w.ThunderLightningFrequency.Value > 0.01;
        float weatherScale = storm ? 0.25f
            : w.Flags.HasFlag(Weather.Flag.Rainy) || w.Flags.HasFlag(Weather.Flag.Snow) ? 0.45f
            : w.Flags.HasFlag(Weather.Flag.Cloudy) ? 0.65f : 1f;
        // Daylight at each of Skyrim's four times of day: noon 1, sunrise and sunset half, night none.
        System.Drawing.Color Grey(float v) { int c = (int)Math.Round(Math.Clamp(v, 0, 1) * 255); return System.Drawing.Color.FromArgb(0, c, c, c); }
        System.Drawing.Color Sun(float daylight) => Grey(daylight * weatherScale);
        System.Drawing.Color Ambient(float daylight) => Grey(0.25f + (0.9f - 0.25f) * daylight * weatherScale);
        w.SunlightColor.Sunrise = Sun(0.5f); w.SunlightColor.Day = Sun(1); w.SunlightColor.Sunset = Sun(0.5f); w.SunlightColor.Night = Grey(0.03f);
        w.AmbientColor.Sunrise = Ambient(0.5f); w.AmbientColor.Day = Ambient(1); w.AmbientColor.Sunset = Ambient(0.5f); w.AmbientColor.Night = Ambient(0);
        AmbientColors Flat(System.Drawing.Color c) => new()
        {
            DirectionalXPlus = c, DirectionalXMinus = c, DirectionalYPlus = c, DirectionalYMinus = c, DirectionalZPlus = c, DirectionalZMinus = c,
            Specular = System.Drawing.Color.FromArgb(0, 0, 0, 0), Scale = 1,
        };
        w.DirectionalAmbientLightingColors.Sunrise = Flat(Ambient(0.5f));
        w.DirectionalAmbientLightingColors.Day = Flat(Ambient(1));
        w.DirectionalAmbientLightingColors.Sunset = Flat(Ambient(0.5f));
        w.DirectionalAmbientLightingColors.Night = Flat(Ambient(0));
    }

    readonly record struct Rect(int X0, int Y0, int X1, int Y1);

    /// <summary>Covers a climate's cells with rectangles: row runs merged with identical runs on following rows.</summary>
    static List<Rect> Rectangles(DfClimate[,] grid, DfClimate target)
    {
        int w = grid.GetLength(0), h = grid.GetLength(1);
        var open = new Dictionary<(int, int), Rect>();
        var done = new List<Rect>();
        for (int y = 0; y < h; y++)
        {
            var runs = new List<(int, int)>();
            for (int x = 0; x < w; x++)
            {
                if (grid[x, y] != target) continue;
                int start = x;
                while (x + 1 < w && grid[x + 1, y] == target) x++;
                runs.Add((start, x));
            }
            var next = new Dictionary<(int, int), Rect>();
            foreach (var run in runs)
                next[run] = open.TryGetValue(run, out var r) ? r with { Y1 = y } : new Rect(run.Item1, y, run.Item2, y);
            done.AddRange(open.Where(kv => !next.ContainsKey(kv.Key)).Select(kv => kv.Value));
            open = next;
        }
        done.AddRange(open.Values);
        return done;
    }
}
