using System.Drawing;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Noggog;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's weathers (WTHR) as Skyrim weather records of their own, as Morrowind's are (MwWeather; the rule: Skyrim's
/// own sky and weather records made to behave like each game's sky, no custom sky system). Each is built on the vanilla
/// weather of its kind (so its image spaces, volumetric lighting and header are valid) and given Oblivion's own data:
/// its ten colours at sunrise/day/sunset/night (NAM0: sky upper, fog, cloud layer lower, ambient, sunlight, sun, stars,
/// sky lower, horizon, cloud layer upper; UESP "Oblivion Mod:Mod File Format/WTHR"), its fog distances (FNAM: day and
/// night near/far, in the same units as Skyrim's), its two cloud layers (CNAM lower, DNAM upper: Oblivion's own DDS
/// textures copied by ObTextures, which load in Skyrim as they are) with their speeds and colours, its wind, sun glare
/// and damage, its precipitation fades, its lightning colour and frequency, its classification (DATA) and its sounds
/// (SNAM: default, precipitation, wind, thunder). Rain and snow are drawn with Skyrim's vanilla particle records of the
/// kind Oblivion's MODL names (RainLight.NIF, RainHeavy.NIF, Snow.NIF). Regions point at these (ObWorld.AddRegions);
/// the vanilla look-alike by name (WeatherFor) stays only for a weather with no record data.
/// </summary>
public sealed partial class ObPluginBuilder
{
    readonly Dictionary<FormKey, Weather?> obWeatherRecords = new();
    /// <summary>Oblivion weathers converted to records of their own.</summary>
    public int WeatherCount => obWeatherRecords.Values.Count(w => w is not null);

    /// <summary>Skyrim's directional light reads brighter than Oblivion's (its image spaces adapt the eye): vanilla
    /// clear-day sunlight is about 0.64 of full (measured for Morrowind, MwWeather.SunScale), so Oblivion's sunlight
    /// colours are scaled by that to light the same; the sun disc by vanilla's 0.31.</summary>
    const float ObSunScale = 0.64f, ObSunDiscScale = 0.31f;

    /// <summary>Oblivion's cloud speed is a byte (0-255); Skyrim's a float of at most 0.1 (Mutagen stores it as
    /// (byte - 127) / 1270). A full Oblivion byte is Skyrim's full speed: Oblivion's Thunderstorm (101) then drifts
    /// as Skyrim's StormRain (0.040) does, its Clear (42) at 0.016.</summary>
    const float ObCloudSpeedScale = 0.1f / 255f;

    /// <summary>The Oblivion weather as a Skyrim record of its own (made once), or null when Oblivion has no data for it.</summary>
    Weather? ObWeather(FormKey key)
    {
        if (obWeatherRecords.TryGetValue(key, out var hit)) return hit;
        obWeatherRecords[key] = null;
        if (records.GetValueOrDefault(key) is not Ob.IWeatherGetter src) return null;
        // Every WTHR of the official plugins has its ten colours (NAM0); one without is Oblivion's "no data" case.
        if (src.Colors is not { Count: >= 10 }) { Problems.Add($"weather {src.EditorID}: no colours (vanilla look-alike kept)"); return null; }
        try { return obWeatherRecords[key] = BuildObWeather(src); }
        catch (Exception e) { Problems.Add($"weather {src.EditorID}: {e.Message} (vanilla look-alike kept)"); return null; }
    }

    static Color Rgb(Color c) => Color.FromArgb(0, c.R, c.G, c.B);
    static Color Scale(Color c, float scale) => Color.FromArgb(0, Math.Clamp((int)MathF.Round(c.R * scale), 0, 255), Math.Clamp((int)MathF.Round(c.G * scale), 0, 255), Math.Clamp((int)MathF.Round(c.B * scale), 0, 255));

    static void SetTimes(WeatherColor target, Ob.IWeatherColorsGetter from, float scale = 1)
    {
        target.Sunrise = Scale(from.Sunrise, scale); target.Day = Scale(from.Day, scale); target.Sunset = Scale(from.Sunset, scale); target.Night = Scale(from.Night, scale);
    }

    static void CopyTimes(WeatherColor target, IWeatherColorGetter from)
    {
        target.Sunrise = from.Sunrise; target.Day = from.Day; target.Sunset = from.Sunset; target.Night = from.Night;
    }

    static AmbientColors FlatAmbient(Color c) => new()
    {
        DirectionalXPlus = c, DirectionalXMinus = c, DirectionalYPlus = c, DirectionalYMinus = c, DirectionalZPlus = c, DirectionalZMinus = c,
        Specular = Color.FromArgb(0, 0, 0, 0), Scale = 1,
    };

    static Percent ByteFraction(byte b) => new(b / 255.0);

    /// <summary>A copied Oblivion sky texture as Skyrim records hold it (under textures\), or null when the game lacks it.</summary>
    string? ObSkyTexture(string? file, string weather)
    {
        if (file is not { Length: > 0 }) return null;
        var path = meshes.Textures.Ensure(file);
        if (path is null) { Problems.Add($"weather {weather}: cloud texture {file} not found"); return null; }
        return path.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase) ? path[9..] : path;
    }

    /// <summary>The vanilla weather an Oblivion one is built on: by its precipitation model, else its classification
    /// (Pleasant, Cloudy, Rainy, Snow; the planes of Oblivion have none and take Helgen's burning sky, whose image space
    /// and lightning suit a red sky); fog by name among the cloudy ones.</summary>
    IWeatherGetter ObWeatherTemplate(Ob.IWeatherGetter src)
    {
        string model = (src.Model?.File?.ToString() ?? "").ToLowerInvariant();
        string id = (src.EditorID ?? "").ToLowerInvariant();
        string cls = src.Data?.Classification.ToString() ?? "";
        bool Has(string what) => cls.Contains(what, StringComparison.OrdinalIgnoreCase);
        string pick =
            model.Contains("rainheavy") ? "SkyrimStormRain"
            : model.Contains("rain") ? "SkyrimOvercastRain"
            : model.Contains("snow") ? "SkyrimOvercastSnow"
            : Has("Snow") ? "SkyrimOvercastSnow"
            : Has("Rainy") ? "SkyrimOvercastRain"
            : Has("Cloudy") ? (id.Contains("fog") || id.Contains("whiteout") || id.Contains("mist") ? "SkyrimFog" : "SkyrimCloudy")
            : Has("Pleasant") ? "SkyrimClear"
            : "HelgenAttackWeather";
        return Vanilla.Get<IWeatherGetter>(pick) ?? Vanilla.Get<IWeatherGetter>("SkyrimClear") ?? throw new KeyNotFoundException($"vanilla weather {pick}");
    }

    Weather BuildObWeather(Ob.IWeatherGetter src)
    {
        string name = src.EditorID ?? src.FormKey.ID.ToString("X6");
        var template = ObWeatherTemplate(src);
        var w = mod.Weathers.DuplicateInAsNewRecord(template);
        w.EditorID = "OBWeather" + Sanitize(name);
        var c = src.Colors!;   // (ten of them: ObWeather looked) NAM0 order: 0 sky upper, 1 fog, 2 cloud lower, 3 ambient, 4 sunlight, 5 sun, 6 stars, 7 sky lower, 8 horizon, 9 cloud upper
        string cls = src.Data?.Classification.ToString() ?? "";
        w.Flags = cls.Contains("Snow") ? Weather.Flag.Snow : cls.Contains("Rainy") ? Weather.Flag.Rainy : cls.Contains("Cloudy") ? Weather.Flag.Cloudy : cls.Contains("Pleasant") ? Weather.Flag.Pleasant : 0;

        // Sky: Oblivion's sky overhead and at the horizon, its fog colour near and far, its horizon band, its stars.
        SetTimes(w.SkyUpperColor, c[0]);
        SetTimes(w.SkyLowerColor, c[7]);
        SetTimes(w.HorizonColor, c[8]);
        SetTimes(w.FogNearColor, c[1]);
        SetTimes(w.FogFarColor, c[1]);
        SetTimes(w.WaterMultiplierColor, c[1]);
        SetTimes(w.CloudLodAmbientColor, c[1]);
        SetTimes(w.StarsColor, c[6]);
        // Light: Oblivion's ambient is flat (the same from every side); its sunlight lights, its sun colours the disc.
        SetTimes(w.AmbientColor, c[3]);
        // (Every vanilla template has its directional ambient: the record's own is filled.)
        var ambient = w.DirectionalAmbientLightingColors ??= new();
        ambient.Sunrise = FlatAmbient(w.AmbientColor.Sunrise);
        ambient.Day = FlatAmbient(w.AmbientColor.Day);
        ambient.Sunset = FlatAmbient(w.AmbientColor.Sunset);
        ambient.Night = FlatAmbient(w.AmbientColor.Night);
        SetTimes(w.SunlightColor, c[4], ObSunScale);
        CopyTimes(w.EffectLightingColor, w.SunlightColor);
        CopyTimes(w.CloudLodDiffuseColor, w.SunlightColor);
        SetTimes(w.SunColor, c[5], ObSunDiscScale);
        // Skyrim's horizon cloud statics belong to its own skies; Oblivion's sky is its two cloud layers.
        w.SkyStatics.Clear();

        // Fog: Oblivion's near and far distances as they are (both games' units are the same: a cell is 4096); a near
        // distance below the camera (Oblivion's -750 and the like) starts at it. The power and maximum stay the template's.
        if (src.FogDistance is { } fog)
        {
            w.FogDistanceDayNear = MathF.Max(0, fog.DayNear);
            w.FogDistanceDayFar = MathF.Max(w.FogDistanceDayNear + 1, fog.DayFar);
            w.FogDistanceNightNear = MathF.Max(0, fog.NightNear);
            w.FogDistanceNightFar = MathF.Max(w.FogDistanceNightNear + 1, fog.NightFar);
        }

        // Clouds: Oblivion's lower (CNAM) and upper (DNAM) layers, its own textures, drifting at its speeds, tinted by
        // its cloud colours per time of day; every other layer off. A layer without a texture is off (SigilWhiteOut).
        string? lower = ObSkyTexture(src.TextureLowerLayer, name), upper = ObSkyTexture(src.TextureUpperLayer, name);
        for (int i = 0; i < w.Clouds.Length; i++)
        {
            var layer = w.Clouds[i] ??= new CloudLayer();
            string? texture = i == 0 ? lower : i == 1 ? upper : null;
            bool on = texture is not null;
            layer.Enabled = on;
            if (i < w.CloudTextures.Length) w.CloudTextures[i] = on ? new AssetLink<SkyrimTextureAssetType>(texture!) : null!;
            if (!on) continue;
            layer.XSpeed = (i == 0 ? src.Data?.CloudSpeedLower ?? 0 : src.Data?.CloudSpeedUpper ?? 0) * ObCloudSpeedScale;
            layer.YSpeed = 0;
            layer.Colors ??= new WeatherColor();
            SetTimes(layer.Colors, c[i == 0 ? 2 : 9]);
            layer.Alphas ??= new WeatherAlpha();
            // Oblivion draws each layer whole: the texture's own alpha is the clouds.
            layer.Alphas.Sunrise = layer.Alphas.Day = layer.Alphas.Sunset = layer.Alphas.Night = 1;
        }

        if (src.Data is { } d)
        {
            w.WindSpeed = ByteFraction(d.WindSpeed);
            w.SunGlare = ByteFraction(d.SunGlare);
            w.SunDamage = ByteFraction(d.SunDamage);
            w.PrecipitationBeginFadeIn = ByteFraction(d.PrecipitationBeginFadeIn);
            w.PrecipitationEndFadeOut = ByteFraction(d.PrecipitationEndFadeOut);
            w.ThunderLightningBeginFadeIn = ByteFraction(d.ThunderLightningBeginFadeIn);
            w.ThunderLightningEndFadeOut = ByteFraction(d.ThunderLightningEndFadeOut);
            w.ThunderLightningFrequency = ByteFraction(d.ThunderLightningFrequency);
            w.LightningColor = Rgb(d.LightningColor);
        }

        // Precipitation: Skyrim's particles of the kind Oblivion's model names (the template's: light rain, heavy rain,
        // snow); none where Oblivion has no model. Oblivion has no camera-attached weather effect.
        if ((src.Model?.File?.ToString() ?? "").Length == 0) w.Precipitation.SetToNull();
        w.VisualEffect.SetToNull();

        // Sounds: Oblivion's own (ObSound copies their files), of its type: default, precipitation, wind, thunder
        // (the same four, in the same order, in both games' records). Lightning only where thunder is heard.
        w.Sounds.Clear();
        bool thunder = false;
        foreach (var s in src.Sounds)
        {
            if (ObSound(s.Sound.FormKey) is not { } sound) continue;
            int type = Math.Clamp((int)s.Type, 0, 3);
            if (type == 3) thunder = true;
            w.Sounds.Add(new WeatherSound { Sound = sound.ToLink<ISoundGetter>(), Type = (WeatherSound.TypeEnum)type });
        }
        if (!thunder) w.ThunderLightningFrequency = new Percent(0);
        return w;
    }
}
