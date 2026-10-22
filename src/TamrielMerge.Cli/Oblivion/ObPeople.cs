using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's people moving and standing as Oblivion's do (user, 2026-10-21: "fix the npcs"): each NPC carries the
/// keyword of its animation set (Skyrim/Animations/NpcAnimations.Oblivion: men or women, on the skeleton its record
/// names: skeletonbeast.nif for the beast races), and each placed one is scaled to Oblivion's height for it.
/// </summary>
public sealed partial class ObPluginBuilder
{
    readonly Dictionary<string, Keyword> personKeywords = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<FormKey, float> personScale = [];
    readonly Dictionary<string, (float Ob, float Sk)?> headHeights = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What <see cref="Write"/> wrote of its people's animations.</summary>
    public List<string> NpcAnimationLog { get; } = [];

    /// <summary>A keyword marking Oblivion's people of one animation set (made once).</summary>
    Keyword PersonKeyword(string editorId)
    {
        if (personKeywords.TryGetValue(editorId, out var k)) return k;
        k = new Keyword(mod) { EditorID = editorId };
        mod.Keywords.Add(k);
        return personKeywords[editorId] = k;
    }

    /// <summary>The animation set of an Oblivion NPC: by sex, on the skeleton its record names (its model: Oblivion's
    /// beast races name skeletonbeast.nif, the rest skeleton.nif).</summary>
    static Skyrim.Animations.NpcAnimations.Set PersonSet(Ob.INpcGetter n, bool female)
    {
        bool beast = (n.Model?.File?.ToString() ?? "").Contains("beast", StringComparison.OrdinalIgnoreCase);
        return Skyrim.Animations.NpcAnimations.Oblivion.First(s => s.Female == female && s.MwSkeleton.Contains("beast", StringComparison.OrdinalIgnoreCase) == beast);
    }

    /// <summary>Marks a converted person with its set's keyword and notes how much it is scaled when placed.</summary>
    void AsOblivionPerson(Npc npc, Ob.INpcGetter n, bool female, IRaceGetter race, INpcGetter look)
    {
        var set = PersonSet(n, female);
        npc.Keywords = [PersonKeyword(set.Keyword).ToLink<IKeywordGetter>()];
        if (RaceHeights().TryGetValue(n.Race.FormKey, out var h) && PersonHeight(set, female ? h.Female : h.Male, race, female, look) is float scale)
            personScale[npc.FormKey] = scale;
    }

    Dictionary<FormKey, (float Male, float Female)>? raceHeights;

    /// <summary>
    /// Each race's heights from its DATA as the plugins hold it (later plugins win): seven skill boosts (14 bytes), two
    /// unused, then the male and female heights as floats. Read from the files, as Mutagen's Oblivion reader returns
    /// them misread (2026-10-21: every race's 2.278E-41, which scaled the people x0 to x2.5E+23).
    /// </summary>
    Dictionary<FormKey, (float Male, float Female)> RaceHeights() => raceHeights ??= ReadRaceHeights(data);

    public static Dictionary<FormKey, (float Male, float Female)> ReadRaceHeights(ObData data)
    {
        var raceHeights = new Dictionary<FormKey, (float Male, float Female)>();
        foreach (var plugin in data.Plugins)
        {
            string path = Path.Combine(data.DataDir, plugin);
            if (!File.Exists(path)) continue;
            var bytes = File.ReadAllBytes(path);
            string Sig(int at) => System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            // The header record (20 bytes and its data): its masters, which the FormIDs' top byte indexes.
            var masters = new List<ModKey>();
            int headerSize = BitConverter.ToInt32(bytes, 4);
            for (int p = 20; p + 6 <= 20 + headerSize;)
            {
                int size = BitConverter.ToUInt16(bytes, p + 4);
                if (Sig(p) == "MAST") masters.Add(ModKey.FromFileName(System.Text.Encoding.ASCII.GetString(bytes, p + 6, size).TrimEnd('\0')));
                p += 6 + size;
            }
            var self = ModKey.FromFileName(plugin);
            for (int pos = 20 + headerSize; pos + 20 <= bytes.Length;)
            {
                int groupSize = BitConverter.ToInt32(bytes, pos + 4);
                if (Sig(pos) != "GRUP" || groupSize < 20) break;
                if (Sig(pos + 8) == "RACE")
                    for (int r = pos + 20; r + 20 <= pos + groupSize;)
                    {
                        int dataSize = BitConverter.ToInt32(bytes, r + 4);
                        uint flags = BitConverter.ToUInt32(bytes, r + 8), formId = BitConverter.ToUInt32(bytes, r + 12);
                        byte[] body = bytes.AsSpan(r + 20, dataSize).ToArray();
                        if ((flags & 0x40000) != 0)
                        {
                            using var z = new System.IO.Compression.ZLibStream(new MemoryStream(body, 4, body.Length - 4), System.IO.Compression.CompressionMode.Decompress);
                            using var ms = new MemoryStream();
                            z.CopyTo(ms);
                            body = ms.ToArray();
                        }
                        for (int s = 0; s + 6 <= body.Length;)
                        {
                            int size = BitConverter.ToUInt16(body, s + 4);
                            if (System.Text.Encoding.ASCII.GetString(body, s, 4) == "DATA" && size >= 24)
                            {
                                int master = (int)(formId >> 24);
                                var key = new FormKey(master < masters.Count ? masters[master] : self, formId & 0xFFFFFF);
                                raceHeights[key] = (BitConverter.ToSingle(body, s + 6 + 16), BitConverter.ToSingle(body, s + 6 + 20));
                            }
                            s += 6 + size;
                        }
                        r += 20 + dataSize;
                    }
                pos += groupSize;
            }
        }
        return raceHeights;
    }

    /// <summary>How much a placed person is scaled to stand as tall as in Oblivion: the head's height on Oblivion's
    /// skeleton for the set times its race's height (Oblivion scales a person by its race's height), over the head's height
    /// on Skyrim's skeleton times the Skyrim race's height and the borrowed look's own height (which Skyrim multiplies
    /// in). Null when a skeleton can't be read.</summary>
    float? PersonHeight(Skyrim.Animations.NpcAnimations.Set set, float obRaceHeight, IRaceGetter race, bool female, INpcGetter look)
    {
        string key = set.MwSkeleton + (female ? "|f" : "|m");
        if (!headHeights.TryGetValue(key, out var heights))
        {
            heights = null;
            try
            {
                var ob = Skyrim.Animations.NpcAnimations.LoadOblivion(data, set, animations: false);
                var (ot, _) = ob.RestPose();
                int oh = ob.Index("Bip01 Head");
                var archives = new Skyrim.Creatures.CreatureBehavior(SkyrimData!);
                var sk = Skyrim.Animations.NpcAnimations.SkyrimRig.From(Skyrim.Animations.ClimbAnimation.ReadSkeleton(archives.ReadArchive(female
                    ? @"meshes\actors\character\character assets female\skeleton_female.hkx" : @"meshes\actors\character\character assets\skeleton.hkx")!));
                int sh = sk.Index("NPC Head [Head]");
                if (oh >= 0 && sh >= 0) heights = (ot[oh].Z, sk.WorldT[sh].Z);
            }
            catch (Exception e) { Problems.Add($"person height ({set.Key}): {e.Message}"); }
            headHeights[key] = heights;
        }
        if (heights is not var (obHead, skHead) || skHead <= 0 || obRaceHeight <= 0) return null;
        float skRace = female ? race.Height.Female : race.Height.Male;
        float own = look.Height is > 0 ? look.Height : 1;
        return obHead * obRaceHeight / (skHead * (skRace > 0 ? skRace : 1) * own);
    }

    /// <summary>A placed actor's scale: its reference's own, a person's times its height (<see cref="PersonHeight"/>).</summary>
    float? PlacedScale(FormKey actor, float? refScale)
    {
        float s = (refScale ?? 1) * (personScale.TryGetValue(actor, out var p) ? p : 1);
        return MathF.Abs(s - 1) < 0.001f ? null : s;
    }

    /// <summary>Oblivion's people's animations, for the keywords its people carry: every set's keyword made (whoever this
    /// build placed: the animations are written for all of them) before the plugin is written.</summary>
    void MakePersonKeywords()
    {
        foreach (var set in Skyrim.Animations.NpcAnimations.Oblivion) PersonKeyword(set.Keyword);
    }

    /// <summary>The sets written beside the plugin (after it, with their keywords' FormIDs).</summary>
    void WritePeopleAnimations()
    {
        if (SkyrimData is null || personKeywords.Count == 0) return;
        var written = Skyrim.Animations.NpcAnimations.Write(modOutDir, SkyrimData, s => Skyrim.Animations.NpcAnimations.LoadOblivion(data, s), Skyrim.Animations.NpcAnimations.Oblivion, PluginName,
            personKeywords.ToDictionary(k => k.Key, k => k.Value.FormKey.ID, StringComparer.OrdinalIgnoreCase), s => NpcAnimationLog.Add(s));
        NpcAnimationLog.Add($"{written.Count} people's animations; {personScale.Count} people scaled to Oblivion's height (x{(personScale.Count > 0 ? personScale.Values.Min() : 1):F3} .. x{(personScale.Count > 0 ? personScale.Values.Max() : 1):F3})");
    }
}
