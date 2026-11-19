using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// What Oblivion keeps out of the world until its time: a reference marked "initially disabled" isn't there until a
/// script enables it, and one with an enable parent is there when its parent is (or, flagged so, when it isn't).
/// Oblivion's gates stand disabled until the main quest opens them, a quest's people and props wait for their stage,
/// a ruined Kvatch replaces a whole one. Until 2026-11-14 neither was carried over and all of it stood in the world
/// from the start (user: "the oblivion gates only appear after a quest"). Skyrim has both the same way: the record
/// flag 0x800 and XESP.
/// </summary>
public sealed partial class ObPluginBuilder
{
    /// <summary>Every Oblivion reference placed (object, door, NPC, creature) and its Skyrim record.</summary>
    readonly Dictionary<FormKey, IMajorRecord> placedOf = new();
    public int StartDisabled { get; private set; }
    public int EnableParents { get; private set; }
    public int EnableParentsLost { get; private set; }

    Dictionary<FormKey, Ob.IPlacedGetter>? sourceRefs;

    /// <summary>An Oblivion reference by its ID, as the last plugin that has it has it (the cells' merged references:
    /// the builder's records hold no placed ones. ApplyEnableStates looked there at first and found none: "0
    /// references start disabled" in the build of 2026-11-14).</summary>
    Ob.IPlacedGetter? SourceRef(FormKey key)
    {
        if (sourceRefs is null)
        {
            sourceRefs = new();
            foreach (var cell in Merge())
                foreach (var (k, r) in cell.Refs) sourceRefs[k] = r;
        }
        return sourceRefs.GetValueOrDefault(key);
    }

    static Ob.IEnableParentGetter? EnableParentOf(Ob.IPlacedGetter r) => r switch
    {
        Ob.IPlacedObjectGetter o => o.EnableParent,
        Ob.IPlacedNpcGetter n => n.EnableParent,
        Ob.IPlacedCreatureGetter c => c.EnableParent,
        _ => null,
    };

    /// <summary>The references others are enabled by: placed persistent and remembered, as those the scripts name
    /// (a parent in another cell has to be persistent to be found).</summary>
    IEnumerable<FormKey> EnableParentRefs()
    {
        foreach (var cell in Merge())
            foreach (var rec in cell.Refs.Values)
                if (EnableParentOf(rec) is { } ep && !ep.Reference.IsNull) yield return ep.Reference.FormKey;
    }

    /// <summary>Once everything is placed: each reference's own "initially disabled" and its enable parent.</summary>
    void ApplyEnableStates()
    {
        foreach (var (source, placed) in placedOf)
        {
            if (SourceRef(source) is not { } r) continue;
            if ((((Ob.IOblivionMajorRecordGetter)r).MajorRecordFlagsRaw & 0x800) != 0)
            {
                placed.MajorRecordFlagsRaw |= 0x800;
                StartDisabled++;
            }
            if (EnableParentOf(r) is not { } ep || ep.Reference.IsNull) continue;
            if (!placedOf.TryGetValue(ep.Reference.FormKey, out var parent)) { EnableParentsLost++; continue; }
            var link = new EnableParent
            {
                Reference = new FormLink<IPlacedGetter>(parent.FormKey),
                Flags = ep.Flags.HasFlag(Ob.EnableParent.Flag.SetEnableStateToOppositeOfParent) ? EnableParent.Flag.SetEnableStateToOppositeOfParent : 0,
            };
            switch (placed)
            {
                case PlacedObject po: po.EnableParent = link; break;
                case PlacedNpc pn: pn.EnableParent = link; break;
                default: EnableParentsLost++; continue;
            }
            EnableParents++;
        }
    }
}
