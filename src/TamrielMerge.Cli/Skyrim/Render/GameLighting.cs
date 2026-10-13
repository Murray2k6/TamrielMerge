using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Cache;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim.Render;

/// <summary>
/// A worldspace's lighting as the game has it (user, 2026-10-11: "then its the shader" - the menu's flyby to shade as the
/// game does): its climate (the parent's when it uses the parent's climate), the climate's most likely weather, and for
/// each time of day that weather's sun colour, six-direction ambient (DALC: the colour a surface facing each way takes)
/// and image space (the colour grade: saturation, brightness, contrast, tint, HDR sun and sky scale).
/// </summary>
public static class GameLighting
{
    public static Dictionary<string, object?>? Read(ILinkCache cache, IWorldspaceGetter world)
    {
        if (world.Parent is { } wp && !wp.Worldspace.IsNull && wp.Flags.HasFlag((Enum)Enum.Parse(wp.Flags.GetType(), "UseClimateData"))
            && cache.TryResolve<IWorldspaceGetter>(wp.Worldspace.FormKey, out var parent))
            world = parent;
        if (!cache.TryResolve<IClimateGetter>(world.Climate.FormKey, out var climate) || climate.WeatherTypes.Count == 0) return null;
        var type = climate.WeatherTypes.OrderByDescending(t => t.Chance).First();
        if (!cache.TryResolve<IWeatherGetter>(type.Weather.FormKey, out var weather)) return null;
        static float[] Rgb(System.Drawing.Color c) => [c.R / 255f, c.G / 255f, c.B / 255f];
        object Time(IAmbientColorsGetter amb, System.Drawing.Color sun, Mutagen.Bethesda.Plugins.IFormLinkGetter<IImageSpaceGetter>? imgsLink)
        {
            object? grade = null;
            if (imgsLink is { IsNull: false } && cache.TryResolve<IImageSpaceGetter>(imgsLink.FormKey, out var imgs))
                grade = new Dictionary<string, object?>
                {
                    ["imagespace"] = imgs.EditorID,
                    ["saturation"] = imgs.Cinematic?.Saturation, ["brightness"] = imgs.Cinematic?.Brightness, ["contrast"] = imgs.Cinematic?.Contrast,
                    ["tint_amount"] = imgs.Tint?.Amount, ["tint"] = imgs.Tint is { } tn ? Rgb(tn.Color) : null,
                    ["sunlight_scale"] = imgs.Hdr?.SunlightScale, ["sky_scale"] = imgs.Hdr?.SkyScale, ["white"] = imgs.Hdr?.White,
                };
            return new Dictionary<string, object?>
            {
                ["sun"] = Rgb(sun),
                ["ambient"] = new Dictionary<string, object>
                {
                    ["x_plus"] = Rgb(amb.DirectionalXPlus), ["x_minus"] = Rgb(amb.DirectionalXMinus), ["y_plus"] = Rgb(amb.DirectionalYPlus),
                    ["y_minus"] = Rgb(amb.DirectionalYMinus), ["z_plus"] = Rgb(amb.DirectionalZPlus), ["z_minus"] = Rgb(amb.DirectionalZMinus),
                    ["scale"] = amb.Scale,
                },
                ["grade"] = grade,
            };
        }
        var a = weather.DirectionalAmbientLightingColors; var s = weather.SunlightColor; var i = weather.ImageSpaces;
        var v = weather.VolumetricLighting;
        // The weather's sky, fog, clouds and volumetric light (user, 2026-10-11: "the weather needs done too", "and
        // volumetrics"): each time of day's sky colours (upper, lower, horizon), fog colours, cloud colours, and its
        // volumetric lighting (VOLI: the god rays' intensity, colour, density and scattering).
        object Sky(Func<IWeatherColorGetter, System.Drawing.Color> at) => new Dictionary<string, object>
        {
            ["sky_upper"] = Rgb(at(weather.SkyUpperColor)), ["sky_lower"] = Rgb(at(weather.SkyLowerColor)),
            ["horizon"] = Rgb(at(weather.HorizonColor)), ["fog_near"] = Rgb(at(weather.FogNearColor)),
            ["fog_far"] = Rgb(at(weather.FogFarColor)), ["sun_colour"] = Rgb(at(weather.SunColor)),
            ["sun_glare"] = Rgb(at(weather.SunGlareColor)), ["cloud_lod_diffuse"] = Rgb(at(weather.CloudLodDiffuseColor)),
            ["cloud_lod_ambient"] = Rgb(at(weather.CloudLodAmbientColor)), ["effect_lighting"] = Rgb(at(weather.EffectLightingColor)),
        };
        object? Volumetric(Mutagen.Bethesda.Plugins.IFormLinkGetter<IVolumetricLightingGetter>? link)
        {
            if (link is not { IsNull: false } || !cache.TryResolve<IVolumetricLightingGetter>(link.FormKey, out var vl)) return null;
            return new Dictionary<string, object?>
            {
                ["record"] = vl.EditorID, ["intensity"] = vl.Intensity, ["custom_colour_contribution"] = vl.CustomColorContribution,
                ["colour"] = new[] { vl.ColorR, vl.ColorG, vl.ColorB },
                ["density_contribution"] = vl.DensityContribution, ["density_size"] = vl.DensitySize,
                ["density_wind_speed"] = vl.DensityWindSpeed, ["density_falling_speed"] = vl.DensityFallingSpeed,
                ["phase_contribution"] = vl.PhaseFunctionContribution, ["phase_scattering"] = vl.PhaseFunctionScattering,
                ["sampling_range"] = vl.SamplingRepartitionRangeFactor,
            };
        }
        Dictionary<string, object?> Full(object time, Func<IWeatherColorGetter, System.Drawing.Color> at,
            Mutagen.Bethesda.Plugins.IFormLinkGetter<IVolumetricLightingGetter>? vol, bool night)
        {
            var d = (Dictionary<string, object?>)time;
            d["sky"] = Sky(at);
            d["fog"] = new Dictionary<string, object?>
            {
                ["near"] = night ? weather.FogDistanceNightNear : weather.FogDistanceDayNear,
                ["far"] = night ? weather.FogDistanceNightFar : weather.FogDistanceDayFar,
                ["power"] = night ? weather.FogDistanceNightPower : weather.FogDistanceDayPower,
                ["max"] = night ? weather.FogDistanceNightMax : weather.FogDistanceDayMax,
            };
            d["volumetric"] = Volumetric(vol);
            return d;
        }
        var clouds = new List<object>();
        var tex = weather.CloudTextures;
        for (int k = 0; k < tex.Count && k < weather.Clouds.Count; k++)
        {
            var c = weather.Clouds[k];
            if (tex[k]?.DataRelativePath.Path is not { Length: > 0 } cloudTex || c.Enabled != true) continue;
            clouds.Add(new Dictionary<string, object?>
            {
                // Its slot: the game draws layer k on shape k of meshes\sky\clouds.nif (layer 0 overhead, 1 east, 2 west,
                // ... 15-18 the horizon's four bands, 28 the ring on the horizon).
                ["layer"] = k, ["texture"] = cloudTex, ["speed_x"] = c.XSpeed, ["speed_y"] = c.YSpeed,
                ["day"] = Rgb(c.Colors.Day), ["sunrise"] = Rgb(c.Colors.Sunrise), ["sunset"] = Rgb(c.Colors.Sunset), ["night"] = Rgb(c.Colors.Night),
                ["alpha_day"] = c.Alphas.Day, ["alpha_sunrise"] = c.Alphas.Sunrise, ["alpha_sunset"] = c.Alphas.Sunset, ["alpha_night"] = c.Alphas.Night,
            });
        }
        return new Dictionary<string, object?>
        {
            ["world"] = world.EditorID, ["climate"] = climate.EditorID, ["weather"] = weather.EditorID, ["chance"] = type.Chance,
            ["sunrise"] = Full(Time(a.Sunrise, s.Sunrise, i?.Sunrise), w => w.Sunrise, v?.Sunrise, false),
            ["day"] = Full(Time(a.Day, s.Day, i?.Day), w => w.Day, v?.Day, false),
            ["sunset"] = Full(Time(a.Sunset, s.Sunset, i?.Sunset), w => w.Sunset, v?.Sunset, false),
            ["night"] = Full(Time(a.Night, s.Night, i?.Night), w => w.Night, v?.Night, true),
            ["clouds"] = clouds,
        };
    }
}
