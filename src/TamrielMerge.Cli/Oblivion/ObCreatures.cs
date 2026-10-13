using System.Text.RegularExpressions;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Oblivion's part of procedural creatures (<see cref="CreatureCatalog"/>). A creature is ordinary — not a boss, unique
/// or quest creature — when it isn't essential, no script names it, and it is in a leveled creature list or placed
/// more than once; leveled list placements when the list holds only creatures; references scripts point at are left alone.
/// Oblivion resets a cell's creatures after 72 hours (iHoursToRespawnCell), so every spot and new point respawns then.
/// </summary>
public sealed partial class ObPluginBuilder
{
    const float RespawnHours = 72;
    readonly List<(PlacedNpc Ref, FormKey Base, Cell Cell)> creatureRefs = [];

    void NoteActor(PlacedNpc placed, FormKey baseKey, Cell cell)
    {
        if (records.GetValueOrDefault(baseKey) is Ob.ICreatureGetter or Ob.ILeveledCreatureGetter) creatureRefs.Add((placed, baseKey, cell));
    }

    public void WriteCreatureCatalog(string pluginPath)
    {
        var scripted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in records.Values.OfType<Ob.IScriptGetter>())
            foreach (Match m in Regex.Matches(s.Fields?.SourceCode ?? "", "[A-Za-z0-9_]+")) scripted.Add(m.Value);
        var listed = records.Values.OfType<Ob.ILeveledCreatureGetter>().SelectMany(l => l.Entries).Select(e => e.Reference.FormKey).ToHashSet();
        var placedCount = creatureRefs.GroupBy(c => c.Base).ToDictionary(g => g.Key, g => g.Count());
        // A leveled list is ordinary when it holds only creatures (Oblivion's can hold people: bandits, marauders).
        var creatureLists = new Dictionary<FormKey, bool>();
        bool OnlyCreatures(Ob.ILeveledCreatureGetter list)
        {
            if (creatureLists.TryGetValue(list.FormKey, out var known)) return known;
            creatureLists[list.FormKey] = false;
            bool only = list.Entries.All(e => records.GetValueOrDefault(e.Reference.FormKey) is Ob.ICreatureGetter || records.GetValueOrDefault(e.Reference.FormKey) is Ob.ILeveledCreatureGetter inner && OnlyCreatures(inner));
            return creatureLists[list.FormKey] = only;
        }
        bool Ordinary(FormKey key) => records.GetValueOrDefault(key) switch
        {
            Ob.ILeveledCreatureGetter l => OnlyCreatures(l),
            Ob.ICreatureGetter c => c.Configuration?.Flags.HasFlag(Ob.Creature.CreatureFlag.Essential) != true
                && (c.EditorID is null || !scripted.Contains(c.EditorID)) && (listed.Contains(key) || placedCount.GetValueOrDefault(key) >= 2),
            _ => false,
        };
        var catalog = new CreatureCatalog { Game = "Oblivion", Plugin = PluginName, ExteriorRespawnHours = RespawnHours, InteriorRespawnHours = RespawnHours };
        foreach (var (key, actor) in actors)
            if (actor is Npc npc && records.GetValueOrDefault(key) is Ob.ICreatureGetter && Ordinary(key))
                catalog.Creatures.Add(new(CreatureCatalog.Key(npc.FormKey), npc.Name?.String ?? npc.EditorID ?? "", Level(npc), CreatureCatalog.HabitatsOf(npc.Name?.String ?? npc.EditorID ?? "")));
        // Which worldspace each exterior cell is in.
        var worldOf = new Dictionary<FormKey, FormKey>();
        foreach (var ws in mod.Worldspaces)
            foreach (var c in ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items)) worldOf[c.FormKey] = ws.FormKey;
        foreach (var (placed, baseKey, cell) in creatureRefs)
        {
            if (!Ordinary(baseKey) || !cell.Temporary.Contains(placed)) continue;   // persistent ones belong to scripts
            var source = records.GetValueOrDefault(baseKey);
            string name = source is Ob.ICreatureGetter c ? c.Name ?? c.EditorID ?? "" : source?.EditorID ?? "";
            string habitat = source is Ob.ILeveledCreatureGetter l ? ListHabitat(l) : CreatureCatalog.HabitatsOf(name)[0];
            bool interior = cell.Flags.HasFlag(Cell.Flag.IsInteriorCell);
            var p = placed.Placement!.Position;
            int level = Actor(baseKey) is Npc a ? Level(a) : 1;
            catalog.Spots.Add(new(CreatureCatalog.Key(placed.FormKey), interior ? CreatureCatalog.Key(cell.FormKey) : "",
                interior ? "" : worldOf.TryGetValue(cell.FormKey, out var w) ? CreatureCatalog.Key(w) : "", p.X, p.Y, p.Z, habitat, level, RespawnHours));
        }
        catalog.Save(pluginPath);
    }

    static int Level(Npc npc) => npc.Configuration.Level is NpcLevel l ? l.Level : 1;

    /// <summary>A leveled list's habitat: its creatures' most common one (nested lists included). Oblivion's EditorIDs
    /// run words together (LL2MythicEnemy...), so they aren't read for words.</summary>
    string ListHabitat(Ob.ILeveledCreatureGetter list) =>
        Members(list, 0).Select(c => CreatureCatalog.HabitatsOf(c.Name ?? c.EditorID ?? "")[0])
            .GroupBy(h => h).OrderByDescending(g => g.Count()).Select(g => g.Key).FirstOrDefault() ?? "Beast";

    IEnumerable<Ob.ICreatureGetter> Members(Ob.ILeveledCreatureGetter list, int depth)
    {
        foreach (var e in list.Entries)
            switch (records.GetValueOrDefault(e.Reference.FormKey))
            {
                case Ob.ICreatureGetter c: yield return c; break;
                case Ob.ILeveledCreatureGetter inner when depth < 8:
                    foreach (var c in Members(inner, depth + 1)) yield return c;
                    break;
            }
    }
}
