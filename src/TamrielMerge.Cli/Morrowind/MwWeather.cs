using System.Drawing;
using System.Globalization;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Noggog;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's own weather, climate and water for Vvardenfell and Solstheim, from Morrowind.ini (the game keeps its
/// weather there: [Weather Clear] ... [Weather Blizzard], [Weather], [Moons], [Water]) and its textures, models and
/// sounds. Each of Morrowind's ten weathers becomes a Skyrim weather record built on the vanilla weather of the same
/// kind (so its image spaces, lens flare and header are valid) and given Morrowind's sky, fog, ambient and sun colours
/// at sunrise/day/sunset/night, its fog depth, wind and cloud speed, its cloud texture, its precipitation (rain drops,
/// snowflakes) and storm particles (ashcloud.nif, blightcloud.nif, blizzard.nif converted by <see cref="MwVfx"/>,
/// attached to the camera as Skyrim's weather effects are), and its loop and thunder sounds. Each region (REGN) gets
/// its own climate with its weather chances and Morrowind's sunrise/sunset; the worldspace's climate mixes them by
/// area. Water is one record with Morrowind's colour, opacity, underwater fog and its animated surface texture as the
/// water's noise (normal) maps, at sea level 0.
/// </summary>
public sealed partial class MwPluginBuilder
{
    /// <summary>Morrowind's weathers in REGN WEAT order: ini section, our name, the vanilla weather it is built on
    /// (Dragonborn's for the storms), the Skyrim weather class.</summary>
    static readonly (string Section, string Name, string Template, Weather.Flag Flags)[] WeatherKinds =
    [
        ("Weather Clear", "Clear", "SkyrimClear", Weather.Flag.Pleasant),
        ("Weather Cloudy", "Cloudy", "SkyrimCloudy", Weather.Flag.Pleasant),
        ("Weather Foggy", "Foggy", "SkyrimFog", Weather.Flag.Cloudy),
        ("Weather Overcast", "Overcast", "SkyrimCloudy", Weather.Flag.Cloudy),
        ("Weather Rain", "Rain", "SkyrimOvercastRain", Weather.Flag.Rainy),
        ("Weather Thunderstorm", "Thunder", "SkyrimStormRain", Weather.Flag.Rainy),
        ("Weather Ashstorm", "Ashstorm", "DLC02VolcanicAshStorm01", Weather.Flag.Cloudy),
        ("Weather Blight", "Blight", "DLC02VolcanicAshStorm01", Weather.Flag.Cloudy),
        ("Weather Snow", "Snow", "SkyrimOvercastSnow", Weather.Flag.Snow),
        ("Weather Blizzard", "Blizzard", "SkyrimStormSnow", Weather.Flag.Snow),
    ];

    /// <summary>Morrowind's storm particle models, by weather (the game spawns them around the player).</summary>
    static readonly Dictionary<string, string> StormModels = new()
    {
        ["Ashstorm"] = "ashcloud.nif", ["Blight"] = "blightcloud.nif", ["Blizzard"] = "blizzard.nif",
    };

    /// <summary>Skyrim's directional light reads brighter than Morrowind's (its image spaces adapt the eye): vanilla
    /// clear-day sunlight is about 0.64 of full, so Morrowind's sun colours are scaled by that to light the same.</summary>
    const float SunScale = 0.64f;
    /// <summary>The sun disc: vanilla's clear-day disc colour is about 0.31 of full white.</summary>
    const float SunDiscScale = 0.31f;

    Weather[]? mwWeathers;
    Water? mwWater;
    Dictionary<string, Dictionary<string, string>>? mwIni;
    public int WeatherCount => mwWeathers?.Length ?? 0;
    public int ClimateCount { get; private set; }

    /// <summary>Morrowind.ini (beside Data Files) as sections of keys; later keys win, as the game reads it.</summary>
    Dictionary<string, Dictionary<string, string>> MwIni
    {
        get
        {
            if (mwIni is not null) return mwIni;
            mwIni = new(StringComparer.OrdinalIgnoreCase);
            string ini = Path.Combine(Path.GetDirectoryName(data.DataFiles.TrimEnd('\\', '/'))!, "Morrowind.ini");
            if (!File.Exists(ini)) { Problems.Add($"weather: {ini} not found (Skyrim's weather values kept)"); return mwIni; }
            Dictionary<string, string>? section = null;
            foreach (var raw in File.ReadAllLines(ini, Tes3Plugin.Encoding))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';')) continue;
                if (line.StartsWith('[') && line.EndsWith(']'))
                {
                    string name = line[1..^1].Trim();
                    if (!mwIni.TryGetValue(name, out section)) mwIni[name] = section = new(StringComparer.OrdinalIgnoreCase);
                    continue;
                }
                int eq = line.IndexOf('=');
                if (section is not null && eq > 0) section[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
            return mwIni;
        }
    }

    string? IniValue(string section, string key) => MwIni.TryGetValue(section, out var s) && s.TryGetValue(key, out var v) ? v : null;

    float IniFloat(string section, string key, float fallback) =>
        IniValue(section, key) is { } v && float.TryParse(v.Split(',')[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : fallback;

    /// <summary>An ini colour "R,G,B" (0-255), scaled; null when absent.</summary>
    Color? IniColor(string section, string key, float scale = 1)
    {
        if (IniValue(section, key) is not { } v) return null;
        var parts = v.Split(',').Select(p => int.TryParse(p.Trim(), out int n) ? n : -1).ToArray();
        if (parts.Length < 3 || parts.Take(3).Any(p => p < 0)) return null;
        int C(int x) => Math.Clamp((int)MathF.Round(x * scale), 0, 255);
        return Color.FromArgb(0, C(parts[0]), C(parts[1]), C(parts[2]));
    }

    static Color Lerp(Color a, Color b, float t) => Color.FromArgb(0, (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));

    /// <summary>A Morrowind ini colour at each of Skyrim's four times of day ("Sky Sunrise Color" ... "Sky Night Color").</summary>
    bool SetTimes(WeatherColor target, string section, string what, float scale = 1)
    {
        var sunrise = IniColor(section, $"{what} Sunrise Color", scale);
        var day = IniColor(section, $"{what} Day Color", scale);
        var sunset = IniColor(section, $"{what} Sunset Color", scale);
        var night = IniColor(section, $"{what} Night Color", scale);
        if (sunrise is null || day is null || sunset is null || night is null) return false;
        target.Sunrise = sunrise.Value; target.Day = day.Value; target.Sunset = sunset.Value; target.Night = night.Value;
        return true;
    }

    static void Copy(WeatherColor target, IWeatherColorGetter from)
    {
        target.Sunrise = from.Sunrise; target.Day = from.Day; target.Sunset = from.Sunset; target.Night = from.Night;
    }

    static AmbientColors Flat(Color c) => new()
    {
        DirectionalXPlus = c, DirectionalXMinus = c, DirectionalYPlus = c, DirectionalYMinus = c, DirectionalZPlus = c, DirectionalZMinus = c,
        Specular = Color.FromArgb(0, 0, 0, 0), Scale = 1,
    };

    IWeatherGetter VanillaWeather(string editorId)
    {
        var w = SkyrimEsm.Weathers.FirstOrDefault(x => x.EditorID == editorId);
        if (w is not null) return w;
        dragonbornEsm ??= SkyrimMod.CreateFromBinaryOverlay(Path.Combine(SkyrimData!, "Dragonborn.esm"), SkyrimRelease.SkyrimSE);
        return dragonbornEsm.Weathers.FirstOrDefault(x => x.EditorID == editorId) ?? throw new KeyNotFoundException($"vanilla weather {editorId}");
    }

    IShaderParticleGeometryGetter? VanillaParticles(FormKey key) =>
        key.ModKey.FileName == "Skyrim.esm" ? SkyrimEsm.ShaderParticleGeometries.FirstOrDefault(p => p.FormKey == key)
        : (dragonbornEsm ??= SkyrimMod.CreateFromBinaryOverlay(Path.Combine(SkyrimData!, "Dragonborn.esm"), SkyrimRelease.SkyrimSE)).ShaderParticleGeometries.FirstOrDefault(p => p.FormKey == key);

    /// <summary>A converted Morrowind texture's path as Skyrim records hold it (under textures\).</summary>
    string? TexturePath(string file)
    {
        var (path, _) = textures.Ensure(file);
        if (path is null) { Problems.Add($"weather: texture {file} not found"); return null; }
        return path.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? path[9..] : path;
    }

    /// <summary>Morrowind's ten weathers as Skyrim weathers (built once; see the class summary).</summary>
    Weather[] MwWeathers()
    {
        if (mwWeathers is not null) return mwWeathers;
        var result = new List<Weather>();
        var calm = VanillaWeather("SkyrimCloudy");
        foreach (var (section, name, templateId, flags) in WeatherKinds)
        {
            var template = VanillaWeather(templateId);
            var w = mod.Weathers.DuplicateInAsNewRecord(template);
            w.EditorID = "MWWeather" + name;
            w.Flags = flags;
            if (!MwIni.ContainsKey(section)) { Problems.Add($"weather: Morrowind.ini has no [{section}] (vanilla {templateId} kept)"); result.Add(w); continue; }

            // Sky: Morrowind's sky colour overhead, blending to its fog colour at the horizon.
            var fog = new WeatherColor();
            SetTimes(fog, section, "Fog");
            SetTimes(w.SkyUpperColor, section, "Sky");
            w.SkyLowerColor.Sunrise = Lerp(w.SkyUpperColor.Sunrise, fog.Sunrise, 0.5f);
            w.SkyLowerColor.Day = Lerp(w.SkyUpperColor.Day, fog.Day, 0.5f);
            w.SkyLowerColor.Sunset = Lerp(w.SkyUpperColor.Sunset, fog.Sunset, 0.5f);
            w.SkyLowerColor.Night = Lerp(w.SkyUpperColor.Night, fog.Night, 0.5f);
            Copy(w.HorizonColor, fog);
            Copy(w.FogNearColor, fog);
            Copy(w.FogFarColor, fog);
            Copy(w.WaterMultiplierColor, fog);
            Copy(w.CloudLodAmbientColor, fog);
            // Light: Morrowind's ambient is flat (the same from every side); its sun lights and colours the disc.
            SetTimes(w.AmbientColor, section, "Ambient");
            w.DirectionalAmbientLightingColors.Sunrise = Flat(w.AmbientColor.Sunrise);
            w.DirectionalAmbientLightingColors.Day = Flat(w.AmbientColor.Day);
            w.DirectionalAmbientLightingColors.Sunset = Flat(w.AmbientColor.Sunset);
            w.DirectionalAmbientLightingColors.Night = Flat(w.AmbientColor.Night);
            SetTimes(w.SunlightColor, section, "Sun", SunScale);
            Copy(w.EffectLightingColor, w.SunlightColor);
            Copy(w.CloudLodDiffuseColor, w.SunlightColor);
            SetTimes(w.SunColor, section, "Sun", SunDiscScale);
            if (IniColor(section, "Sun Disc Sunset Color", SunDiscScale) is { } disc) w.SunColor.Sunset = disc;
            w.SunColor.Night = Color.FromArgb(0, 0, 0, 0);
            if (IniColor("Weather", "Sun Glare Fader Color", 0.4f) is { } glare) { w.SunGlareColor.Sunrise = glare; w.SunGlareColor.Sunset = glare; }
            float glareView = IniFloat(section, "Glare View", 0);
            w.SunGlare = new Percent(Math.Clamp(glareView, 0, 1));
            // Skyrim's horizon cloud statics belong to its own skies; Morrowind's sky is its one cloud layer.
            w.SkyStatics.Clear();

            // Fog: Morrowind's fog ends at the view distance and starts (1 - depth) of the way there; a depth above 1
            // fogs the near ground too. The view distance is the vanilla weather's own visibility (Skyrim draws far
            // land), shortened by the depth beyond 1 so blizzards and storms close in as they do in Morrowind.
            float dayDepth = IniFloat(section, "Land Fog Day Depth", 0.7f), nightDepth = IniFloat(section, "Land Fog Night Depth", 0.7f);
            float dayView = template.FogDistanceDayFar, nightView = template.FogDistanceNightFar;
            w.FogDistanceDayNear = MathF.Max(0, dayView * (1 - dayDepth));
            w.FogDistanceDayFar = dayView / MathF.Max(1, dayDepth);
            w.FogDistanceNightNear = MathF.Max(0, nightView * (1 - nightDepth));
            w.FogDistanceNightFar = nightView / MathF.Max(1, nightDepth);
            w.WindSpeed = new Percent(Math.Clamp(IniFloat(section, "Wind Speed", 0.1f), 0, 1));

            // Clouds: Morrowind's one cloud texture overhead, drifting at its cloud speed, tinted by the fog colour
            // and as dense as its "Clouds Maximum Percent"; every other layer off.
            string? cloud = IniValue(section, "Cloud Texture") is { Length: > 0 } ct ? TexturePath(ct) : null;
            float cloudAlpha = Math.Clamp(IniFloat(section, "Clouds Maximum Percent", 1), 0, 1);
            float cloudSpeed = Math.Min(0.1f, IniFloat(section, "Cloud Speed", 1.25f) * 0.02f);
            for (int i = 0; i < w.Clouds.Length; i++)
            {
                var layer = w.Clouds[i] ??= new CloudLayer();
                bool on = i == 0 && cloud is not null;
                layer.Enabled = on;
                if (i < w.CloudTextures.Length) w.CloudTextures[i] = on ? new AssetLink<SkyrimTextureAssetType>(cloud!) : null!;
                if (!on) continue;
                layer.XSpeed = cloudSpeed;
                layer.YSpeed = 0;
                layer.Colors ??= new WeatherColor();
                Copy(layer.Colors, fog);
                layer.Alphas ??= new WeatherAlpha();
                layer.Alphas.Sunrise = layer.Alphas.Day = layer.Alphas.Sunset = layer.Alphas.Night = cloudAlpha;
            }

            // Precipitation: Skyrim's particle settings of the kind, drawn with Morrowind's drop or flake.
            w.Precipitation.SetToNull();
            string? flake = name switch { "Rain" or "Thunder" => "tx_raindrop_01.tga", "Snow" or "Blizzard" => "tx_bm_snowflakes_01.tga", _ => null };
            if (flake is not null && VanillaParticles(template.Precipitation.FormKey) is { } like && TexturePath(flake) is { } flakeTex)
            {
                var p = mod.ShaderParticleGeometries.DuplicateInAsNewRecord(like);
                p.EditorID = "MWParticles" + name;
                p.ParticleTexture = flakeTex;
                p.NumSubtexturesX = 1;
                p.NumSubtexturesY = 1;
                // Morrowind's drop counts (Max Raindrops / Max Snowflakes) against rain's 450 and snow's 750.
                float most = IniFloat(section, flake.Contains("rain") ? "Max Raindrops" : "Max Snowflakes", flake.Contains("rain") ? 450 : 750);
                p.ParticleDensity = like.ParticleDensity * MathF.Max(0.5f, most / (flake.Contains("rain") ? 450f : 750f));
                w.Precipitation.SetTo(p);
            }
            // Storm particles: Morrowind's own storm model around the camera.
            w.VisualEffect.SetToNull();
            if (StormModels.TryGetValue(name, out var stormModel) && StormEffect(name, stormModel) is { } effect) w.VisualEffect.SetTo(effect);
            if (name is "Ashstorm" or "Blight")
            {
                // Dragonborn's grading is Solstheim's own; Morrowind's storms take the neutral overcast image spaces.
                w.ImageSpaces = calm.ImageSpaces?.DeepCopy();
                w.VolumetricLighting = null;
            }
            w.Sounds.Clear();
            if (IniValue(section, "Ambient Loop Sound ID") is { Length: > 0 } amb && !amb.Equals("None", StringComparison.OrdinalIgnoreCase)
                && Sound(amb, "AMBWeatherRainDrizzleLP") is { } ambient)
                w.Sounds.Add(new WeatherSound { Sound = ambient.ToLink<ISoundGetter>(), Type = WeatherSound.TypeEnum.Default });
            if (IniValue(section, "Rain Loop Sound ID") is { Length: > 0 } rain && !rain.Equals("None", StringComparison.OrdinalIgnoreCase)
                && Sound(rain, "AMBWeatherRainDrizzleLP") is { } rainSound)
                w.Sounds.Add(new WeatherSound { Sound = rainSound.ToLink<ISoundGetter>(), Type = WeatherSound.TypeEnum.Precipitation });
            for (int i = 0; i < 8; i++)
                if (IniValue(section, $"Thunder Sound ID {i}") is { Length: > 0 } thunder && Sound(thunder, "AMBWeatherThunderDistant") is { } thunderSound)
                    w.Sounds.Add(new WeatherSound { Sound = thunderSound.ToLink<ISoundGetter>(), Type = WeatherSound.TypeEnum.Thunder });
            if (name != "Thunder") w.ThunderLightningFrequency = new Percent(0);
            result.Add(w);
        }
        return mwWeathers = result.ToArray();
    }

    /// <summary>A Morrowind storm model (converted particles) as a camera-attached weather effect, as Skyrim's are.</summary>
    VisualEffect? StormEffect(string name, string model)
    {
        vfx ??= new MwVfx(data, textures);
        if (vfx.Convert(model, modOutDir) is not { } rel) { Problems.AddRange(vfx.Problems); vfx.Problems.Clear(); return null; }
        var art = new ArtObject(mod) { EditorID = "MWArtStorm" + name, ObjectBounds = new ObjectBounds(), Model = new Model { File = rel } };
        mod.ArtObjects.Add(art);
        var effect = new VisualEffect(mod)
        {
            EditorID = "MWStorm" + name + "Effect",
            EffectArt = art.ToLink<IArtObjectGetter>(),
            Flags = VisualEffect.Flag.AttachToCamera | VisualEffect.Flag.InheritRotation,
        };
        mod.VisualEffects.Add(effect);
        return effect;
    }

    /// <summary>A climate of Morrowind's weathers with these chances (by REGN WEAT index): Morrowind's sun and its
    /// sunrise and sunset ([Weather]: from the sky's pre-sunrise to the end of the post-sunrise time, and the same at
    /// sunset), Skyrim's moons (Masser and Secunda, the same two) and stars.</summary>
    Climate MwClimate(string editorId, IReadOnlyList<int> chances)
    {
        var weathers = MwWeathers();
        var climate = mod.Climates.DuplicateInAsNewRecord(SkyrimEsm.Climates.First(c => c.EditorID == "SkyrimClimate"));
        climate.EditorID = editorId;
        climate.WeatherTypes.Clear();
        for (int i = 0; i < Math.Min(chances.Count, weathers.Length); i++)
            if (chances[i] > 0) climate.WeatherTypes.Add(new WeatherType { Weather = weathers[i].ToLink<IWeatherGetter>(), Chance = chances[i] });
        if (climate.WeatherTypes.Count == 0) climate.WeatherTypes.Add(new WeatherType { Weather = weathers[0].ToLink<IWeatherGetter>(), Chance = 100 });
        if (TexturePath("tx_sun_05.tga") is { } sun) climate.SunTexture = new AssetLink<SkyrimTextureAssetType>(sun);
        if (TexturePath("tx_sun_flash_grey_05.tga") is { } flash) climate.SunGlareTexture = new AssetLink<SkyrimTextureAssetType>(flash);
        float sunrise = IniFloat("Weather", "Sunrise Time", 6), sunriseLength = IniFloat("Weather", "Sunrise Duration", 2);
        float sunset = IniFloat("Weather", "Sunset Time", 18), sunsetLength = IniFloat("Weather", "Sunset Duration", 2);
        // Skyrim keeps these in steps of ten minutes.
        static TimeOnly At(float hours) => new TimeOnly(0).Add(TimeSpan.FromMinutes(Math.Clamp(MathF.Round(hours * 6) * 10, 0, 23 * 60 + 50)));
        climate.SunriseBegin = At(sunrise - IniFloat("Weather", "Sky Pre-Sunrise Time", 0.5f));
        climate.SunriseEnd = At(sunrise + sunriseLength + IniFloat("Weather", "Sky Post-Sunrise Time", 1));
        climate.SunsetBegin = At(sunset - IniFloat("Weather", "Sky Pre-Sunset Time", 1.5f));
        climate.SunsetEnd = At(sunset + sunsetLength + IniFloat("Weather", "Sky Post-Sunset Time", 0.5f));
        climate.Moons = Climate.Moon.Masser | Climate.Moon.Secunda;
        ClimateCount++;
        return climate;
    }

    /// <summary>
    /// Morrowind's water ([Water], textures\water\water00..31): Skyrim's default water given Morrowind's surface
    /// colour (the average of its animated texture) for shallow water and reflections, its underwater colour for the
    /// deep and its underwater fog, its world alpha as the opacity, and three frames of its surface animation as the
    /// three noise layers (normal maps from the frames' brightness), tiled as Morrowind tiles them across a cell.
    /// </summary>
    Water MwWater()
    {
        if (mwWater is not null) return mwWater;
        var w = mod.Waters.DuplicateInAsNewRecord(SkyrimEsm.Waters.First(x => x.EditorID == "DefaultWater"));
        w.EditorID = "MWWater";
        mwWater = w;
        string surface = IniValue("Water", "SurfaceTexture") is { Length: > 0 } s ? s : "water";
        int frames = (int)IniFloat("Water", "SurfaceFrameCount", 32);
        var frameFiles = Enumerable.Range(0, Math.Max(1, frames)).Select(i => $@"textures\water\{surface}{i:D2}")
            .Select(f => new[] { ".dds", ".tga", ".bmp" }.Select(e => f + e).FirstOrDefault(data.Exists)).Where(f => f is not null).Cast<string>().ToList();
        if (frameFiles.Count == 0) { Problems.Add($"water: no {surface}NN textures (Skyrim's default water kept)"); return w; }
        static (uint[] Px, int W, int H)? Decode(byte[] b, string name) =>
            name.EndsWith(".dds") ? Util.DdsImage.Decode(b) : name.EndsWith(".tga") ? MwTextures.DecodeTga(b) : MwTextures.DecodeBmp(b);
        // The surface colour: the average of the first frame.
        if (Decode(data.Read(frameFiles[0])!, frameFiles[0]) is { } first && first.Px.Length > 0)
        {
            double r = 0, g = 0, b = 0;
            foreach (var px in first.Px) { r += px >> 16 & 255; g += px >> 8 & 255; b += px & 255; }
            int n = first.Px.Length;
            var shallow = Color.FromArgb(0, (int)(r / n), (int)(g / n), (int)(b / n));
            w.ShallowColor = shallow;
            w.ReflectionColor = Lerp(shallow, Color.FromArgb(0, 255, 255, 255), 0.35f);
        }
        if (IniColor("Water", "UnderwaterColor") is { } deep) w.DeepColor = deep;
        w.Opacity = (byte)Math.Clamp((int)MathF.Round(IniFloat("Water", "World Alpha", 0.75f) * 100), 0, 100);
        // Underwater fog: Skyrim's default reaches 1600 units; Morrowind's day factor 2.5 is that default (4096 / 2.5).
        w.FogUnderWaterDistanceFarPlane = 4096 / MathF.Max(0.5f, IniFloat("Water", "UnderwaterDayFog", 2.5f));
        // Noise: three frames spread over the animation, as normal maps.
        float tile = 8192f / MathF.Max(1, IniFloat("Water", "SurfaceTileCount", 10));
        float scale = tile / w.NoiseLayerOneUvScale;
        w.NoiseLayerOneUvScale *= scale; w.NoiseLayerTwoUvScale *= scale; w.NoiseLayerThreeUvScale *= scale;
        var noise = new string[3];
        for (int k = 0; k < 3; k++)
        {
            string file = frameFiles[k * frameFiles.Count / 3];
            string rel = $@"{MwTextures.OutFolder}\water\{Path.GetFileNameWithoutExtension(file)}_n.dds";
            string full = Path.Combine(modOutDir, rel);
            if (!File.Exists(full) && Decode(data.Read(file)!, file) is { } img) MwTextures.WriteNormalMap(img.Px, img.W, img.H, full);
            noise[k] = rel[9..];
        }
        w.NoiseLayerOneTexture = new AssetLink<SkyrimTextureAssetType>(noise[0]);
        w.NoiseLayerTwoTexture = new AssetLink<SkyrimTextureAssetType>(noise[1]);
        w.NoiseLayerThreeTexture = new AssetLink<SkyrimTextureAssetType>(noise[2]);
        return w;
    }

    /// <summary>A REGN's weather chances in WEAT order (8 bytes in Morrowind, 10 with Bloodmoon's snow and blizzard).</summary>
    static int[] Chances(Tes3Record region) => region.Get("WEAT")?.Data.Select(b => (int)b).ToArray() ?? [];
}
