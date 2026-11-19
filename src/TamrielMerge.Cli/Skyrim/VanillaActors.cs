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

    Dictionary<FormKey, IMajorRecordGetter>? byKey;

    /// <summary>A vanilla (Skyrim or Dragonborn) record with an EditorID by its FormKey, when it is a T.</summary>
    public T? ByKey<T>(FormKey key) where T : class, IMajorRecordGetter
    {
        if (byKey is null)
        {
            Get<IMajorRecordGetter>("\u0001");   // builds the index
            byKey = [];
            foreach (var r in byEditorId!.Values) byKey.TryAdd(r.FormKey, r);
        }
        return byKey.GetValueOrDefault(key) as T;
    }

    /// <summary>
    /// Vanilla NPCs with their own FaceGen head of this race and sex, as ordinary people look: none with war paint,
    /// tattoos, dirt or blood painted on (their race's tint masks of those, at 10% or more) or a scar, and speaking with
    /// the race's voice or one of Skyrim's townsfolk voices. Until 2026-11-04 Skyrim's encounter NPCs were preferred and
    /// only the race's voice taken: for Bretons that left Forsworn alone, tattooed, dirty and scarred (25,189 of Daggerfall
    /// Towns.esm's 29,105 people; user: "the npcs all have tatoos on faces"). The race's voice alone when no face is clean.
    /// </summary>
    /// <summary>Well-known people left out of the faces (jarls and their courts, the titled).</summary>
    public int NotableFacesLeftOut { get; private set; }

    public List<INpcGetter> Faces(FormKey race, bool female)
    {
        if (faces is null)
        {
            var withFace = Directory.GetFiles(skyrimData, "Skyrim - Meshes*.bsa")
                .Select(b => Mutagen.Bethesda.Archives.Archive.CreateReader(GameRelease.SkyrimSE, b))
                .SelectMany(a => a.Files).Select(f => f.Path.ToLowerInvariant())
                .Where(p => p.Contains(@"facegeom\skyrim.esm\")).Select(Path.GetFileNameWithoutExtension).ToHashSet();
            // Head part type 5: scars, but for its "none" parts (MarksMaleHumanoid00NoScar and the like), which most faces wear.
            var scars = Skyrim.HeadParts.Where(h => (int?)h.Type == 5 && !System.Text.RegularExpressions.Regex.IsMatch(h.EditorID ?? "", @"\d\dNo[A-Z]")).Select(h => h.FormKey).ToHashSet();
            // Eyes (type 2) no townsperson has: a demon's, blind, a vampire's, glowing (the warlock faces wear them).
            scars.UnionWith(Skyrim.HeadParts.Where(h => (int?)h.Type == 2 && System.Text.RegularExpressions.Regex.IsMatch(h.EditorID ?? "", "Demon|Blind|Vampire|Glow|Dremora|Hag", System.Text.RegularExpressions.RegexOptions.IgnoreCase)).Select(h => h.FormKey));
            var notable = Skyrim.Factions.Where(f => f.EditorID is { } e && System.Text.RegularExpressions.Regex.IsMatch(e, "^Job(Jarl|Steward|CourtWizard|Housecarl)Faction$")).Select(f => f.FormKey).ToHashSet();
            var races = Skyrim.Races.ToDictionary(r => r.FormKey);
            var townVoices = Skyrim.VoiceTypes.Where(v => v.EditorID is { } e && TownVoice(e)).Select(v => v.FormKey).ToHashSet();
            var enemyVoices = Skyrim.VoiceTypes.Where(v => v.EditorID is { } e && System.Text.RegularExpressions.Regex.IsMatch(e, "Warlock|Bandit|Forsworn|Vampire|Brute")).Select(v => v.FormKey).ToHashSet();
            faces = Skyrim.Npcs.Where(n => withFace.Contains(n.FormKey.ID.ToString("x8")))
                .GroupBy(n => (n.Race.FormKey, n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female)))
                .ToDictionary(g => g.Key, g =>
                {
                    // Nobody a player knows by sight (user, 2026-11-11, on a Redguard patron: "jarl bulgraf shouldnt be
                    // there"): no jarl, steward, court wizard or housecarl (Skyrim's Job...Faction of each), nobody
                    // titled, when others of the race and sex are left.
                    var pool = g.Where(n => !n.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Essential)).ToList();
                    var plain = pool.Where(n => !n.Factions.Any(f => notable.Contains(f.Faction.FormKey))
                        && !System.Text.RegularExpressions.Regex.IsMatch(n.Name?.String ?? "", @"^(Jarl|General|Legate|High King|Emperor|Arch-Mage|Thane|Queen|King|Captain|Commander) ")).ToList();
                    NotableFacesLeftOut += pool.Count - plain.Count;
                    if (plain.Count > 0) pool = plain;
                    var masks = races.TryGetValue(g.Key.Item1, out var r) ? (g.Key.Item2 ? r.HeadData?.Female : r.HeadData?.Male)?.TintMasks : null;
                    bool Clean(INpcGetter n) =>
                        !n.HeadParts.Any(h => scars.Contains(h.FormKey)) &&
                        !n.TintLayers.Any(t => (t.InterpolationValue ?? 0) >= 0.1f && masks?.FirstOrDefault(m => m.Index == t.Index)?.FileName?.GivenPath is { } file && Marked(file));
                    var voiced = WithRaceVoice(Skyrim, pool, g.Key.Item1, g.Key.Item2);
                    var raceVoice = voiced.Count > 0 && voiced.Count < pool.Count ? voiced[0].Voice.FormKey : (FormKey?)null;
                    var clean = pool.Where(n => Clean(n) && (n.Voice.FormKey == raceVoice || townVoices.Contains(n.Voice.FormKey))).ToList();
                    // Townspeople before Skyrim's enemies: its bandits, warlocks, witches and Forsworn are sculpted to glower
                    // (2026-11-10: 46 of the 76 clean High Elf men were EncWarlock faces, and Morrowind's Jiub wore
                    // EncWarlockStorm06DarkElfM's, tattooed and glowing-eyed; user: "npcs look like they are upset").
                    var civil = clean.Where(n => !Enemy(n.EditorID ?? "") && !enemyVoices.Contains(n.Voice.FormKey)).ToList();
                    return (civil.Count >= 3 ? civil : clean.Count >= 3 ? clean : voiced).OrderBy(n => n.FormKey.ID).ToList();
                });
        }
        return faces.GetValueOrDefault((race, female)) ?? [];
    }

    /// <summary>Skyrim's generic people's voices (MaleCommoner, FemaleSultry, MaleSlyCynical...), which carry its generic lines:
    /// not a unique one, a child's, or a guard's or soldier's (their lines are their duty's). With the townsfolk ones alone
    /// (2026-11-04, first try) clean Breton men were 7 faces.</summary>
    static bool TownVoice(string voice) => (voice.StartsWith("Male", StringComparison.Ordinal) || voice.StartsWith("Female", StringComparison.Ordinal))
        && !voice.Contains("Unique", StringComparison.OrdinalIgnoreCase) && !voice.Contains("Child", StringComparison.OrdinalIgnoreCase)
        && !voice.Contains("Guard", StringComparison.OrdinalIgnoreCase) && !voice.Contains("Soldier", StringComparison.OrdinalIgnoreCase);

    /// <summary>One of Skyrim's enemies or props by its EditorID: an encounter or leveled actor (Enc..., Lvl...), a dungeon's
    /// (dun...), a corpse or a test actor.</summary>
    static bool Enemy(string editorId) => System.Text.RegularExpressions.Regex.IsMatch(editorId, @"^(Enc|Lvl|dun|TreasCorpse|Test)|Corpse|Bandit|Warlock|Forsworn|Vampire|Necromancer|Guard|Soldier|Thalmor", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A race tint mask of paint or grime rather than skin: war paint, tattoos, dirt, blood (by its texture's name;
    /// many have no mask type set).</summary>
    static bool Marked(string mask) => mask.Contains("Paint", StringComparison.OrdinalIgnoreCase) || mask.Contains("Tattoo", StringComparison.OrdinalIgnoreCase)
        || mask.Contains("Dirt", StringComparison.OrdinalIgnoreCase) || mask.Contains("Blood", StringComparison.OrdinalIgnoreCase) || mask.Contains("Forsworn", StringComparison.OrdinalIgnoreCase);

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

    /// <summary>
    /// Makes a copy of a vanilla actor look like it: Skyrim keeps a face (FaceGen head) per actor record, so a copy under a
    /// new FormID has none and is drawn with a black face (2026-11-10, in game: Redguard's boarding pirates). The copy
    /// takes its traits from the original as its template, and everything the original took from its own template through
    /// it; its name and the like stay its own, and its AI data when <paramref name="ownAI"/>.
    /// </summary>
    public static void Lookalike(Npc copy, INpcGetter original, bool ownAI)
    {
        if (original.Configuration.TemplateFlags.HasFlag(NpcConfiguration.TemplateFlag.Traits)) return;   // its own template's face already
        copy.Template = new Mutagen.Bethesda.Plugins.FormLinkNullable<INpcSpawnGetter>(original.FormKey);
        var flags = original.Template.IsNull ? 0 : original.Configuration.TemplateFlags;
        flags |= NpcConfiguration.TemplateFlag.Traits;
        flags &= ~NpcConfiguration.TemplateFlag.BaseData;
        if (ownAI) flags &= ~NpcConfiguration.TemplateFlag.AIData;
        copy.Configuration.TemplateFlags = flags;
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
