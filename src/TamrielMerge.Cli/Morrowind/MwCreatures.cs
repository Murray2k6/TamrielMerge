using System.Text.RegularExpressions;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's part of procedural creatures (<see cref="CreatureCatalog"/>). A creature is ordinary — not a boss, unique
/// or quest creature — when it isn't essential (CREA FLAG 0x80), no script names it, and it is in a leveled list or
/// placed more than once; leveled list placements are ordinary when the list holds only creatures. A spot respawns only when its creature has
/// Morrowind's Respawn flag (0x2), after Morrowind's fCorpseRespawnDelay (hours); otherwise it never comes back, as in
/// Morrowind. New points use that delay.
/// </summary>
public sealed partial class MwPluginBuilder
{
    readonly List<(PlacedNpc Ref, Tes3Ref Source, string? Cell)> creatureRefs = [];

    /// <summary>Notes a placed actor that may be a creature (for the catalog).</summary>
    void NoteActor(PlacedNpc placed, Tes3Ref r, string? interiorCell)
    {
        if (world.Objects.TryGetValue(r.BaseId, out var rec) && rec.Type is "CREA" or "LEVC") creatureRefs.Add((placed, r, interiorCell));
    }

    public void WriteCreatureCatalog(string pluginPath)
    {
        // Who scripts name, who is in leveled lists, how often each is placed.
        var scripted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in world.Objects.Values.Where(o => o.Type == "SCPT"))
            foreach (Match m in Regex.Matches(s.Str("SCTX") ?? "", "\"([^\"]+)\"|([A-Za-z0-9_]+)")) scripted.Add(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in world.Objects.Values.Where(o => o.Type == "LEVC"))
            foreach (var f in l.All("CNAM")) listed.Add(f.Text);
        var placedCount = world.Interiors.Values.Concat(world.Exteriors.Values).SelectMany(c => c.Refs)
            .GroupBy(r => r.BaseId, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        // A leveled list is ordinary when it holds only creatures (Morrowind's lists can hold people, e.g. vampire cattle).
        var creatureLists = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        bool OnlyCreatures(Tes3Record list)
        {
            if (creatureLists.TryGetValue(list.Id, out var known)) return known;
            creatureLists[list.Id] = false;   // a list inside itself counts as not
            bool only = list.All("CNAM").All(f => world.Objects.TryGetValue(f.Text, out var m) && (m.Type == "CREA" || m.Type == "LEVC" && OnlyCreatures(m)));
            return creatureLists[list.Id] = only;
        }
        bool Ordinary(Tes3Record rec)
        {
            if (rec.Type == "LEVC") return OnlyCreatures(rec);
            int flags = rec.Get("FLAG")?.Int() ?? 0;
            return (flags & 0x80) == 0 && !scripted.Contains(rec.Id) && (listed.Contains(rec.Id) || placedCount.GetValueOrDefault(rec.Id) >= 2);
        }
        float delay = world.Settings.TryGetValue("fCorpseRespawnDelay", out var g) && g.Get("FLTV") is { } v ? v.Float() : 72;
        var catalog = new CreatureCatalog { Game = "Morrowind", Plugin = PluginName, ExteriorRespawnHours = delay, InteriorRespawnHours = delay };
        foreach (var (id, actor) in actors)
            if (actor is Npc npc && world.Objects.TryGetValue(id, out var rec) && rec.Type == "CREA" && Ordinary(rec))
                catalog.Creatures.Add(new(CreatureCatalog.Key(npc.FormKey), npc.Name?.String ?? id, Level(npc), CreatureCatalog.HabitatsOf(npc.Name?.String ?? id)));
        string ws = mod.Worldspaces.FirstOrDefault(w => w.EditorID == "MWVvardenfell") is { } w ? CreatureCatalog.Key(w.FormKey) : "";
        foreach (var (placed, r, cell) in creatureRefs)
        {
            if (!world.Objects.TryGetValue(r.BaseId, out var rec) || !Ordinary(rec)) continue;
            string habitat = rec.Type == "LEVC" ? ListHabitat(rec) : CreatureCatalog.HabitatsOf(rec.Str("FNAM") ?? r.BaseId)[0];
            bool respawns = rec.Type == "CREA" && ((rec.Get("FLAG")?.Int() ?? 0) & 0x2) != 0;
            var p = placed.Placement!.Position;
            string cellKey = cell is not null && interiorsByName.TryGetValue(cell, out var c) ? CreatureCatalog.Key(c.FormKey) : "";
            int level = actors.GetValueOrDefault(r.BaseId) is Npc a ? Level(a) : 1;
            catalog.Spots.Add(new(CreatureCatalog.Key(placed.FormKey), cellKey, cellKey.Length == 0 ? ws : "", p.X, p.Y, p.Z, habitat, level, respawns ? delay : -1));
        }
        catalog.Save(pluginPath);
    }

    static int Level(Npc npc) => npc.Configuration.Level is NpcLevel l ? l.Level : 1;

    /// <summary>A leveled creature list's habitat: its ID's words (ex_ashland_lev, in_tomb_..., in_dwrv_...), else its
    /// members' most common one.</summary>
    string ListHabitat(Tes3Record list)
    {
        // Whole words between underscores (ex_ashland_lev-2, in_dwe_all_lev0), so letters running into other words don't count.
        var words = list.Id.ToLowerInvariant().Split(['_', ' ', '-', '+'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();
        bool Any(params string[] w) => w.Any(words.Contains) || words.Any(x => w.Any(y => x.StartsWith(y) && y.Length >= 4));
        if (Any("ash", "ashland", "ashlands", "redmountain", "6th", "sixth")) return "Ash";
        if (Any("dwrv", "dwe", "dwemer", "dwarven")) return "Construct";
        if (Any("tomb", "ancestral", "undead", "crypt", "barrow", "vamp")) return "Undead";
        if (Any("daedric", "daedra")) return "Daedra";
        if (Any("water", "fish", "uw")) return "Water";
        if (Any("cave", "mine", "eggmine", "kwama")) return "Cave";
        if (Any("solstheim", "bm", "snow", "ice")) return "Cold";
        return list.All("CNAM").Select(f => world.Objects.TryGetValue(f.Text, out var m) ? CreatureCatalog.HabitatsOf(m.Str("FNAM") ?? f.Text)[0] : "Beast")
            .GroupBy(h => h).OrderByDescending(g2 => g2.Count()).Select(g2 => g2.Key).FirstOrDefault() ?? "Beast";
    }
}
