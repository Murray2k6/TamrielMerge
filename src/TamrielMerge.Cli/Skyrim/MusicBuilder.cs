using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's soundtrack as Skyrim music types. Like vanilla (MUSExploreTundra), a music type cycles a list of
/// single tracks with 30-second silent spacers, and each track carries conditions: outdoors, Daggerfall's weather
/// playlists map to Skyrim's weather classes (pleasant, cloudy, raining, snowing) by day, and its night playlist
/// plays from 20:00 to 06:00 when it isn't raining or snowing. Battle music plays only in the Daggerfall world
/// (<see cref="BuildCombat"/>); uncut content adds the songs Daggerfall never plays.
/// </summary>
public sealed class MusicBuilder(SkyrimMod mod)
{
    const string MusicFolder = @"Data\Music\TamrielMerge";
    readonly Dictionary<string, MusicTrack> silent = new();

    public MusicType Explore { get; private set; } = null!;
    public MusicType Dungeon { get; private set; } = null!;
    public Dictionary<string, MusicType> Interiors { get; } = new();

    public void Build(bool uncut)
    {
        Explore = Type("DFMusExplore",
            Tracks("Sunny", Day(), [Weather(new IsPleasantConditionData())]),
            Tracks("Cloudy", Day(), [Weather(new IsCloudyConditionData())]),
            Tracks("Overcast", Day(), [Weather(new IsCloudyConditionData())]),
            Tracks("Rain", [], [Weather(new IsRainingConditionData())]),
            Tracks("Snow", [], [Weather(new IsSnowingConditionData())]),
            Tracks("Night", Night(), [Weather(new IsRainingConditionData(), 0), Weather(new IsSnowingConditionData(), 0)]));
        // Uncut: the unused dungeon songs join the dungeon playlist, and the unused sneaking songs play there while
        // the player sneaks.
        Dungeon = uncut
            ? Type("DFMusDungeon", Tracks("Dungeon", [], []), Tracks("UncutDungeon", [], []), Tracks("UncutSneaking", [], [Sneaking()]))
            : Type("DFMusDungeon", Tracks("Dungeon", [], []));
        foreach (var playlist in new[] { "Tavern", "TempleGood", "TempleNeutral", "TempleBad", "Shop", "MagesGuild", "Interior", "Knight", "Palace", "Castle", "Sneaking" })
            Interiors[playlist] = Type("DFMus" + playlist, Tracks(playlist, [], []));
    }

    MusicType Type(string edid, params List<MusicTrack>[] groups)
    {
        // Interleave the groups so each weather/time gets a turn as the list cycles, with a spacer after each song.
        var songs = new List<MusicTrack>();
        int longest = groups.Max(g => g.Count);
        for (int i = 0; i < longest; i++)
            foreach (var g in groups.Where(g => i < g.Count)) songs.Add(g[i]);
        var type = new MusicType(mod) { EditorID = edid, Flags = MusicType.Flag.CycleTracks, Data = new MusicTypeData { Priority = 49, DuckingDecibel = 0 }, FadeDuration = 12, Tracks = [] };
        foreach (var s in songs)
        {
            type.Tracks.Add(s.ToLink<IMusicTrackGetter>());
            type.Tracks.Add(Silence(30).ToLink<IMusicTrackGetter>());
        }
        mod.MusicTypes.Add(type);
        return type;
    }

    List<MusicTrack> Tracks(string playlist, Condition[] time, Condition[] weather) =>
        (DfMusic.Playlists.TryGetValue(playlist, out var list) ? list : DfMusic.UncutPlaylists[playlist]).Select(song =>
        {
            var track = new MusicTrack(mod)
            {
                EditorID = $"DFMus{playlist}{LocationPlacer.Sanitize(song)}",
                Type = MusicTrack.TypeEnum.SingleTrack,
                // Vanilla records name .wav and ship .xwm; the engine resolves either.
                TrackFilename = $@"{MusicFolder}\{song}.wav",
            };
            var conditions = time.Concat(weather).Select(c => c.DeepCopy()).ToList();
            if (conditions.Count > 0) { track.Conditions = []; track.Conditions.AddRange(conditions); }
            mod.MusicTracks.Add(track);
            return track;
        }).ToList();

    MusicTrack Silence(float seconds)
    {
        string key = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (!silent.TryGetValue(key, out var track))
        {
            track = new MusicTrack(mod) { EditorID = $"DFMusSilent{key}", Type = MusicTrack.TypeEnum.SilentTrack, Duration = seconds };
            mod.MusicTracks.Add(track);
            silent[key] = track;
        }
        return track;
    }

    static Condition Time(CompareOperator op, float hour, bool or = false) => new ConditionFloat
    {
        Data = new GetCurrentTimeConditionData(),
        CompareOperator = op,
        ComparisonValue = hour,
        Flags = or ? Condition.Flag.OR : 0,
    };

    /// <summary>
    /// Daggerfall's battle music, for the Daggerfall world only. Skyrim picks combat music globally (MUSCombat, and
    /// MUSCombatBoss for bosses), so the vanilla types gain Daggerfall tracks that require the player to be in the
    /// Iliac Bay location, and the vanilla tracks gain the opposite condition. Every other world keeps its own.
    /// </summary>
    public void BuildCombat(ISkyrimModGetter skyrim, Location iliacBay)
    {
        var inBay = InLocation(iliacBay, 1);
        var combat = Tracks("Combat", [], [inBay]);
        foreach (var edid in new[] { "MUSCombat", "MUSCombatBoss" })
        {
            var type = mod.MusicTypes.GetOrAddAsOverride(skyrim.MusicTypes.First(t => t.EditorID == edid));
            type.Tracks ??= [];
            foreach (var t in combat) type.Tracks.Add(t.ToLink<IMusicTrackGetter>());
            foreach (var link in type.Tracks.ToList())
            {
                if (link.FormKey.ModKey != skyrim.ModKey || mod.MusicTracks.ContainsKey(link.FormKey)) continue;
                var vanilla = mod.MusicTracks.GetOrAddAsOverride(skyrim.MusicTracks.First(t => t.FormKey == link.FormKey));
                vanilla.Conditions ??= [];
                vanilla.Conditions.Add(InLocation(iliacBay, 0));
            }
        }
    }

    /// <summary>The player is (1) or isn't (0) in the location. Runs on PlayerRef, as vanilla music conditions do
    /// (a music track has no subject).</summary>
    static Condition InLocation(Location location, float equals) => new ConditionFloat
    {
        Data = new GetInCurrentLocConditionData
        {
            Location = new FormLinkOrIndex<ILocationGetter>(new GetInCurrentLocConditionData(), location.FormKey),
            RunOnType = Condition.RunOnType.Reference,
            Reference = new FormLink<ISkyrimMajorRecordGetter>(PlayerRef),
        },
        CompareOperator = CompareOperator.EqualTo,
        ComparisonValue = equals,
    };

    static readonly FormKey PlayerRef = new(ModKey.FromFileName("Skyrim.esm"), 0x14);

    static Condition Sneaking() => new ConditionFloat
    {
        Data = new IsSneakingConditionData { RunOnType = Condition.RunOnType.Reference, Reference = new FormLink<ISkyrimMajorRecordGetter>(PlayerRef) },
        CompareOperator = CompareOperator.EqualTo,
        ComparisonValue = 1,
    };

    static Condition[] Day() => [Time(CompareOperator.GreaterThanOrEqualTo, 6), Time(CompareOperator.LessThan, 20)];
    static Condition[] Night() => [Time(CompareOperator.GreaterThanOrEqualTo, 20, or: true), Time(CompareOperator.LessThan, 6)];

    static Condition Weather(ConditionData check, float equals = 1) => new ConditionFloat
    {
        Data = check,
        CompareOperator = CompareOperator.EqualTo,
        ComparisonValue = equals,
    };
}
