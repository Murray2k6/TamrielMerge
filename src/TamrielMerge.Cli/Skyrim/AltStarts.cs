using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Where each converted game begins after its opening (user, 2026-10-12: "we need an alternative start if one is
/// traveled to or game started with the tutorial quest done", "and player owned games equivelant"): its opening's end,
/// the quest the opening hands on (set to its first stage) and what it gives. Found in the converted plugins (TMStoryOrder
/// looks them up by FormID, as the plugins are optional):
/// <list type="bullet">
/// <item>Daggerfall: outside Privateer's Hold (where its door leads on the Iliac Bay).</item>
/// <item>Redguard: Stros M'Kai's docks, where its opening at sea ends (RGOpeningDocks).</item>
/// <item>Morrowind: out of Seyda Neen's Census and Excise Office, "Report to Caius Cosades" (A1_1) begun, with the
/// package for him.</item>
/// <item>Oblivion: out of the sewers' South East Tunnel, "Deliver the Amulet" (MQ01) begun, with the Amulet of Kings.</item>
/// </list>
/// A door's spot is the outside door with the offset to where the game sets the player down coming out of it.
/// </summary>
public static class AltStarts
{
    public sealed record AfterOpening(int Story, uint RefId, float Dx, float Dy, float Dz, uint QuestId, int QuestStage, uint ItemId);

    public static List<AfterOpening> Find(string dir)
    {
        var found = new List<AfterOpening>();
        IDisposable? Open(string name, out ISkyrimModGetter? mod)
        {
            mod = null;
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) return null;
            var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            mod = m;
            return m;
        }
        using (Open("Daggerfall.esm", out var df))
            if (df is not null)
            {
                // Privateer's Hold: the dungeon cell holding the new game's start marker.
                var hold = Interiors(df).FirstOrDefault(c => (c.Persistent.Concat(c.Temporary)).Any(r => r.EditorID == "DFEnterPrivateersHold"));
                if (hold is not null && OutsideOf(df, hold) is { } d) found.Add(new(0, d.Ref, d.Dx, d.Dy, d.Dz, 0, 0, 0));
            }
        using (Open("Redguard.esm", out var rg))
            if (rg is not null && AllRefs(rg).FirstOrDefault(r => r.EditorID == "RGOpeningDocks") is { } docks)
                found.Add(new(1, docks.FormKey.ID, 0, 0, 0, 0, 0, 0));
        using (Open("Morrowind.esm", out var mw))
            if (mw is not null && Interiors(mw).FirstOrDefault(c => c.EditorID == "MWSeydaNeenCensusandExciseOffice") is { } office && OutsideOf(mw, office) is { } d)
            {
                var quest = mw.Quests.FirstOrDefault(q => q.EditorID == "MWQA11FindSpymaster");
                var package = mw.EnumerateMajorRecords().FirstOrDefault(r => r.EditorID?.Contains("caiuspackage", StringComparison.OrdinalIgnoreCase) == true);
                found.Add(new(2, d.Ref, d.Dx, d.Dy, d.Dz, quest?.FormKey.ID ?? 0, quest?.Stages.Select(s => (int)s.Index).DefaultIfEmpty(0).Min() ?? 0, package?.FormKey.ID ?? 0));
            }
        using (Open("Oblivion.esm", out var ob))
            if (ob is not null && Interiors(ob).FirstOrDefault(c => c.EditorID == "OBImperialSewerSystemSE") is { } tunnel && OutsideOf(ob, tunnel) is { } d)
            {
                var quest = ob.Quests.FirstOrDefault(q => q.EditorID == "OBQMQ01");
                var amulet = ob.EnumerateMajorRecords().FirstOrDefault(r => r.EditorID is { } e && e.Contains("AmuletofKings", StringComparison.OrdinalIgnoreCase) && r is IArmorGetter or IMiscItemGetter);
                // MQ01's stage 10: the Emperor's dying charge, the amulet to Jauffre (its first entry the player reads).
                int stage = quest?.Stages.Any(s => s.Index == 10) == true ? 10 : quest?.Stages.Select(s => (int)s.Index).DefaultIfEmpty(0).Min() ?? 0;
                found.Add(new(3, d.Ref, d.Dx, d.Dy, d.Dz, quest?.FormKey.ID ?? 0, stage, amulet?.FormKey.ID ?? 0));
            }
        return found;
    }

    static IEnumerable<ICellGetter> Interiors(ISkyrimModGetter mod) => mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells);

    static IEnumerable<IPlacedObjectGetter> AllRefs(ISkyrimModGetter mod) =>
        Interiors(mod).Concat(mod.Worldspaces.SelectMany(w => (w.TopCell is { } t ? new[] { t } : []).Concat(w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))))
            .SelectMany(c => c.Persistent.Concat(c.Temporary)).OfType<IPlacedObjectGetter>();

    /// <summary>The door outside that an interior's way out leads to (in a worldspace), and the offset from it to where
    /// the player is set down coming out.</summary>
    static (uint Ref, float Dx, float Dy, float Dz)? OutsideOf(ISkyrimModGetter mod, ICellGetter interior)
    {
        var outside = new Dictionary<FormKey, IPlacedObjectGetter>();
        foreach (var ws in mod.Worldspaces)
            foreach (var r in (ws.TopCell?.Persistent ?? []).OfType<IPlacedObjectGetter>()) outside[r.FormKey] = r;
        foreach (var door in interior.Persistent.Concat(interior.Temporary).OfType<IPlacedObjectGetter>())
            if (door.TeleportDestination is { } t && outside.TryGetValue(t.Door.FormKey, out var ext) && ext.Placement is { } p)
                return (ext.FormKey.ID, t.Position.X - p.Position.X, t.Position.Y - p.Position.Y, t.Position.Z - p.Position.Z);
        return null;
    }
}
