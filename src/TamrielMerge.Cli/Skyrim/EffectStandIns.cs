using Mutagen.Bethesda.Plugins;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Vanilla Skyrim effects (moveable statics from Skyrim.esm) standing in for the particle systems of converted
/// models, which nothing converts yet: candle and lamp flames, torch and brazier fires, mist, steam, waterfall spray
/// and vent smoke. Picked by the particles' texture (Morrowind names them tx_firealpha*, tx_smokealpha*,
/// tx_alpha_mist*, ...) and scaled by their size, against the vanilla effects' measured sizes: the candle flame
/// stands about 16 units, FXFire02 is about 60 across, low mist about 900, waterfall mist emits over 150.
/// Sparks, ash and bubbles get nothing (the fires carry their own embers).
/// </summary>
public static class EffectStandIns
{
    static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm");
    public static readonly FormKey CandleFlame = new(Skyrim, 0x0B9FAD);       // TESTCandleFlame01
    public static readonly FormKey Fire = new(Skyrim, 0x013B42);              // FXFire02 (FXFire02New.nif)
    public static readonly FormKey MistLow = new(Skyrim, 0x077772);           // FXMistLow01
    public static readonly FormKey WaterfallMist = new(Skyrim, 0x01ACAD);     // FXWaterfallMistBlastLite
    public static readonly FormKey Steam = new(Skyrim, 0x030B39);             // FXSteamBillow
    public static readonly FormKey Smoke = new(Skyrim, 0x0B09E4);             // FXSmokeChimney01

    /// <summary>A particle emitter in model space: where it sits, its particles' size and how far they rise (speed x
    /// lifetime), and its texture's file name.</summary>
    public sealed record Emitter(System.Numerics.Vector3 Position, float Size, float Rise, string Texture);

    public enum Kind { Flame, Smoke, Mist, WaterfallMist, Steam, None }

    /// <summary>
    /// The emitters to stand in for: by kind, largest first, one per cluster of the same kind (a fire's flame,
    /// inner-flame and glow emitters give one fire; separate candles each keep theirs), with the effect and scale.
    /// </summary>
    public static IEnumerable<(System.Numerics.Vector3 At, FormKey Effect, float Scale)> Pick(IEnumerable<Emitter> emitters, float refScale)
    {
        var picked = new List<(System.Numerics.Vector3 At, float Size, Kind Kind)>();
        foreach (var e in emitters.OrderByDescending(e => e.Size))
        {
            var kind = KindOf(e.Texture);
            if (kind == Kind.None) continue;
            if (picked.Any(p => p.Kind == kind && System.Numerics.Vector3.Distance(p.At, e.Position) < MathF.Max(p.Size, e.Size) * 1.5f)) continue;
            picked.Add((e.Position, e.Size, kind));
        }
        foreach (var (at, size, kind) in picked)
            if (For(kind, size * refScale) is { } fx) yield return (at, fx.Effect, fx.Scale);
    }

    /// <summary>A middling particle size for an effect known only by name.</summary>
    public static float TypicalSize(Kind kind) => kind switch
    {
        Kind.Flame => 40, Kind.Smoke => 50, Kind.Mist => 300, Kind.WaterfallMist => 150, Kind.Steam => 50, _ => 0,
    };

    /// <summary>What a particle texture shows.</summary>
    public static Kind KindOf(string texture)
    {
        string t = texture.ToLowerInvariant();
        if (t.Contains("spark") || t.Contains("ash") || t.Contains("bubble")) return Kind.None;
        if (t.Contains("waterfall")) return Kind.WaterfallMist;
        if (t.Contains("steam")) return Kind.Steam;
        if (t.Contains("mist") || t.Contains("fog")) return Kind.Mist;
        if (t.Contains("smoke")) return Kind.Smoke;
        if (t.Contains("fire") || t.Contains("flame")) return Kind.Flame;
        return Kind.None;
    }

    /// <summary>The stand-in effect and its scale for particles of this kind and size, or null for none.</summary>
    public static (FormKey Effect, float Scale)? For(Kind kind, float size) => kind switch
    {
        Kind.Flame when size <= 5 => (CandleFlame, Math.Clamp(size * 0.16f, 0.25f, 1f)),
        Kind.Flame => (Fire, Math.Clamp(size / 42f, 0.15f, 2.5f)),
        Kind.Smoke when size >= 40 => (Smoke, Math.Clamp(size / 50f, 0.5f, 2f)),   // only big plumes (vents); small fires carry their own
        Kind.Mist => (MistLow, Math.Clamp(size / 350f, 0.1f, 2f)),
        Kind.WaterfallMist => (WaterfallMist, Math.Clamp(size / 150f, 0.2f, 2f)),
        Kind.Steam => (Steam, Math.Clamp(size / 50f, 0.3f, 1.5f)),
        _ => null,
    };
}
