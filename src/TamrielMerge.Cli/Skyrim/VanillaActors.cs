using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Vanilla Skyrim (and Dragonborn) records the converted games build their actors and items on: NPCs and other
/// records by EditorID, and, per race and sex, the vanilla NPCs whose FaceGen head ships in Skyrim's archives — a
/// converted NPC takes its appearance from one of those (a new NPC without FaceGen data renders with the dark-face
/// bug). Shared by the Morrowind and Oblivion builders.
/// </summary>
public sealed class VanillaActors(string skyrimData)
{
    ISkyrimModDisposableGetter? skyrim, dragonborn;
    Dictionary<string, IMajorRecordGetter>? byEditorId;
    Dictionary<(FormKey Race, bool Female), List<INpcGetter>>? faces;

    public ISkyrimModGetter Skyrim => skyrim ??= SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);
    ISkyrimModGetter Dragonborn => dragonborn ??= SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Dragonborn.esm"), SkyrimRelease.SkyrimSE);

    /// <summary>A vanilla (Skyrim or Dragonborn) record by EditorID, or by EditorID prefix (first alphabetically).</summary>
    public T? Get<T>(string editorIdOrPrefix) where T : class, IMajorRecordGetter
    {
        if (byEditorId is null)
        {
            byEditorId = new(StringComparer.OrdinalIgnoreCase);
            foreach (var r in Skyrim.EnumerateMajorRecords()) if (r.EditorID is { } e) byEditorId.TryAdd(e, r);
            foreach (var r in Dragonborn.EnumerateMajorRecords().Where(r => r.FormKey.ModKey == Dragonborn.ModKey)) if (r.EditorID is { } e) byEditorId.TryAdd(e, r);
        }
        if (byEditorId.TryGetValue(editorIdOrPrefix, out var exact) && exact is T t) return t;
        return byEditorId.Where(kv => kv.Value is T && kv.Key.StartsWith(editorIdOrPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).Select(kv => (T)kv.Value).FirstOrDefault();
    }

    /// <summary>Vanilla NPCs with their own FaceGen head of this race and sex (generic encounter NPCs preferred).</summary>
    public List<INpcGetter> Faces(FormKey race, bool female)
    {
        if (faces is null)
        {
            var withFace = Directory.GetFiles(skyrimData, "Skyrim - Meshes*.bsa")
                .Select(b => Mutagen.Bethesda.Archives.Archive.CreateReader(GameRelease.SkyrimSE, b))
                .SelectMany(a => a.Files).Select(f => f.Path.ToLowerInvariant())
                .Where(p => p.Contains(@"facegeom\skyrim.esm\")).Select(Path.GetFileNameWithoutExtension).ToHashSet();
            faces = Skyrim.Npcs.Where(n => withFace.Contains(n.FormKey.ID.ToString("x8")))
                .GroupBy(n => (n.Race.FormKey, n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female)))
                .ToDictionary(g => g.Key, g =>
                {
                    var voiced = WithRaceVoice(Skyrim, g.Where(n => !n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Essential)).ToList(), g.Key.Item1, g.Key.Item2);
                    var generic = voiced.Where(n => n.EditorID?.StartsWith("Enc") == true).ToList();
                    return (generic.Count >= 3 ? generic : voiced).OrderBy(n => n.FormKey.ID).ToList();
                });
        }
        return faces.GetValueOrDefault((race, female)) ?? [];
    }

    /// <summary>
    /// Dresses a converted NPC in the clothing and armour it carries: Skyrim NPCs wear their outfit, not what is in
    /// their inventory. The first item for each body slot goes into an outfit (one outfit per distinct set, shared)
    /// and leaves the inventory, since an outfit's items are added when worn.
    /// </summary>
    public static void WearInventory(SkyrimMod mod, Npc npc, Dictionary<string, Outfit> outfits, string prefix)
    {
        if (npc.Items is null) return;
        var worn = new List<IMajorRecord>();
        BipedObjectFlag used = 0;
        // A wearable's body slots: an armour's own, or for a leveled list of nothing but armour, all its entries'.
        BipedObjectFlag? Slots(FormKey key, int depth = 0)
        {
            if (mod.Armors.TryGetValue(key, out var armor)) return armor.BodyTemplate?.FirstPersonFlags ?? 0;
            if (depth > 4 || !mod.LeveledItems.TryGetValue(key, out var list) || list.Entries is not { Count: > 0 } entries) return null;
            BipedObjectFlag all = 0;
            foreach (var e in entries)
            {
                if (e.Data?.Reference.FormKey is not { } k || Slots(k, depth + 1) is not { } f) return null;
                all |= f;
            }
            return all;
        }
        foreach (var entry in npc.Items.ToList())
        {
            var key = entry.Item.Item.FormKey;
            if (Slots(key) is not { } slots || slots == 0 || (slots & used) != 0) continue;
            used |= slots;
            worn.Add(mod.Armors.TryGetValue(key, out var a) ? a : mod.LeveledItems[key]);
            if (entry.Item.Count > 1) entry.Item.Count--;
            else npc.Items.Remove(entry);
        }
        if (npc.Items.Count == 0) npc.Items = null;
        if (worn.Count == 0) return;
        string setKey = string.Join(",", worn.Select(w => w.FormKey.ID).Order());
        if (!outfits.TryGetValue(setKey, out var outfit))
        {
            outfit = new Outfit(mod) { EditorID = $"{prefix}Outfit{outfits.Count:D4}", Items = [] };
            foreach (var w in worn) outfit.Items.Add(new FormLink<IOutfitTargetGetter>(w.FormKey));
            mod.Outfits.Add(outfit);
            outfits[setKey] = outfit;
        }
        npc.DefaultOutfit.SetTo(outfit);
    }

    /// <summary>
    /// One voice per race and sex, as Morrowind and Oblivion had: converted people take faces only from vanilla NPCs
    /// with that voice (when there are any), so every line needs at most one recording per race and sex
    /// (Imperials and Bretons share the even-toned voices, High and Wood Elves the haughty ones).
    /// </summary>
    public static readonly Dictionary<(string Race, bool Female), string> RaceVoices = new()
    {
        [("DarkElfRace", false)] = "MaleDarkElf", [("DarkElfRace", true)] = "FemaleDarkElf",
        [("NordRace", false)] = "MaleNord", [("NordRace", true)] = "FemaleNord",
        [("ImperialRace", false)] = "MaleEvenToned", [("ImperialRace", true)] = "FemaleEvenToned",
        [("BretonRace", false)] = "MaleEvenToned", [("BretonRace", true)] = "FemaleEvenToned",
        [("RedguardRace", false)] = "MaleEvenTonedAccented", [("RedguardRace", true)] = "FemaleSultry",
        [("HighElfRace", false)] = "MaleElfHaughty", [("HighElfRace", true)] = "FemaleElfHaughty",
        [("WoodElfRace", false)] = "MaleElfHaughty", [("WoodElfRace", true)] = "FemaleElfHaughty",
        [("OrcRace", false)] = "MaleOrc", [("OrcRace", true)] = "FemaleOrc",
        [("KhajiitRace", false)] = "MaleKhajiit", [("KhajiitRace", true)] = "FemaleKhajiit",
        [("ArgonianRace", false)] = "MaleArgonian", [("ArgonianRace", true)] = "FemaleArgonian",
    };

    /// <summary>The faces among <paramref name="pool"/> that speak with the race's voice, or the whole pool if none do.</summary>
    public static List<INpcGetter> WithRaceVoice(ISkyrimModGetter skyrim, List<INpcGetter> pool, FormKey race, bool female)
    {
        string? raceId = skyrim.Races.FirstOrDefault(r => r.FormKey == race)?.EditorID;
        if (raceId is null || !RaceVoices.TryGetValue((raceId, female), out var voice)) return pool;
        var voiceKey = skyrim.VoiceTypes.FirstOrDefault(v => v.EditorID == voice)?.FormKey;
        var matching = pool.Where(n => n.Voice.FormKey == voiceKey).ToList();
        return matching.Count > 0 ? matching : pool;
    }

    /// <summary>A stable hash of an ID (case-insensitive), for picking among candidates repeatably.</summary>
    public static int StableHash(string s) { unchecked { int h = 17; foreach (char c in s.ToLowerInvariant()) h = h * 31 + c; return h & 0x7FFFFFFF; } }

    /// <summary>A biped slot by its number (30 = head ... 61); slots 44-61 have no names.</summary>
    public static BipedObjectFlag Slot(int n) => (BipedObjectFlag)(1u << (n - 30));

    /// <summary>Morrowind/Oblivion book text is HTML-like (&lt;DIV ALIGN&gt;, &lt;BR&gt;, &lt;FONT&gt;, &lt;IMG&gt;); Skyrim's books
    /// read a Scaleform subset with &lt;p align&gt; and &lt;br&gt;. Fonts and pictures are dropped.</summary>
    public static string BookText(string html)
    {
        var o = System.Text.RegularExpressions.RegexOptions.IgnoreCase;
        string t = html.Replace("\r", "");
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<\s*IMG[^>]*>", "", o);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<\s*/?\s*FONT[^>]*>", "", o);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<\s*DIV\s+ALIGN\s*=\s*""?(\w+)""?\s*>", m => $"<p align=\"{m.Groups[1].Value.ToLowerInvariant()}\">", o);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<\s*/\s*DIV\s*>", "</p>", o);
        t = System.Text.RegularExpressions.Regex.Replace(t, @"<\s*BR\s*/?\s*>", "<br>", o);
        return t.Trim();
    }
}
