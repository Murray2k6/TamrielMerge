using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// Crime and arrest in Oblivion's lands, through Skyrim's own crime system. Oblivion keeps one bounty; here each land
/// has a crime faction that tracks crime (OBCrimeCyrodiil, and OBCrimeShiveringIsles for the Isles' people, editor IDs
/// SE*), with Oblivion's crime values, which every Oblivion person reports to (their crime faction). Oblivion's guards
/// (their class has Oblivion's guard flag) are Skyrim guards: IsGuardFaction and GuardDialogueFaction, so Skyrim's
/// guards' crime dialogue (DialogueCrimeGuards: pay, jail, resist) arrests the player. Each land's faction has a jail
/// from Oblivion's own prisons: Oblivion's PrisonMarker references become Skyrim's (the player is put at the prison
/// marker in the cell of the faction's belongings chest); the chests (belongings, stolen goods) are placed in that
/// jail cell, out of sight under the floor; the player is let out where the jail's door leads (a Skyrim prison marker
/// and follower wait marker there). The Imperial City's prison (the Bastion) is Cyrodiil's jail and Aichan prison the
/// Isles', or the first prison of that land. TMOB_PCInJail (IsPlayerInJail) is set by TMObState while the player is
/// in a jail cell near its prison marker (TMObPlayer checks every few seconds).
/// </summary>
public sealed partial class ObPluginBuilder
{
    static readonly FormKey IsGuardFaction = Sk(0x086EEE), GuardDialogueFaction = Sk(0x02BE3B), SkPrisonMarker = Sk(0x000004),
        EvidencePlayerInventory = Sk(0x108D36), EvidenceStolenGoods = Sk(0x0267E7), JailOutfit = Sk(0x028B60);

    /// <summary>The cells each land's jail should be, best first (else its first prison).</summary>
    static readonly string[] CyrodiilJails = ["ICImperialLegionHQTheBastion"], IslesJails = ["XPAichan01", "XPCorpserotPassage01"];

    readonly Faction?[] landCrime = new Faction?[2];
    FormList? jailMarkers;
    /// <summary>Prisons placed: the Skyrim marker, its cell, the Oblivion cell's editor ID and land.</summary>
    readonly List<(PlacedObject Marker, Cell Cell, bool Isles)> prisons = [];
    public int JailCount { get; private set; }

    static bool IsIsles(string? editorId) => editorId is { Length: > 2 } e && (e.StartsWith("SE", StringComparison.Ordinal) || e.StartsWith("XP", StringComparison.Ordinal));

    /// <summary>The land's crime faction (made on first use).</summary>
    Faction LandCrime(bool isles)
    {
        int i = isles ? 1 : 0;
        if (landCrime[i] is { } hit) return hit;
        var f = landCrime[i] = new Faction(mod)
        {
            EditorID = isles ? "OBCrimeShiveringIsles" : "OBCrimeCyrodiil",
            Name = isles ? "Shivering Isles" : "Cyrodiil",
            Flags = Faction.FactionFlag.TrackCrime | Faction.FactionFlag.CanBeOwner,
            // Oblivion's bounties: murder 1000, assault 40, trespass 5, pickpocket 25, jail break 50.
            CrimeValues = new CrimeValues { Arrest = true, AttackOnSight = true, Murder = 1000, Assault = 40, Trespass = 5, Pickpocket = 25, StealMult = 0.5f, Escape = 50, Werewolf = 1000 },
            JailOutfit = new FormLinkNullable<IOutfitGetter>(JailOutfit),
            Ranks = [new Rank { Number = 0 }],
        };
        mod.Factions.Add(f);
        return f;
    }

    /// <summary>An Oblivion person's part in the law: their land's crime faction, and Skyrim's guard factions for
    /// Oblivion's guards.</summary>
    void AddCrime(Npc npc, Ob.INpcGetter n)
    {
        var land = LandCrime(IsIsles(n.EditorID));
        npc.CrimeFaction.SetTo(land);
        if (!n.Class.IsNull && records.GetValueOrDefault(n.Class.FormKey) is Ob.IClassGetter cls && cls.Data?.Flags.HasFlag(Ob.ClassFlag.Guard) == true)
        {
            npc.Factions.Add(new RankPlacement { Faction = land.ToLink<IFactionGetter>(), Rank = 0 });
            npc.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(IsGuardFaction), Rank = 0 });
            npc.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(GuardDialogueFaction), Rank = 0 });
        }
    }

    FormList JailMarkers()
    {
        if (jailMarkers is not null) return jailMarkers;
        jailMarkers = new FormList(mod) { EditorID = "TMObJailMarkers" };
        mod.FormLists.Add(jailMarkers);
        return jailMarkers;
    }

    /// <summary>One of Oblivion's prison markers, as Skyrim's (persistent, listed for TMObState).</summary>
    PlacedObject PlacePrisonMarker(Ob.IPlacedObjectGetter r, Cell cell, Cell persistentCell)
    {
        var placed = new PlacedObject(mod)
        {
            Base = new FormLinkNullable<IPlaceableObjectGetter>(SkPrisonMarker),
            Placement = new Placement { Position = r.Location!.Position, Rotation = r.Location.Rotation },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        persistentCell.Persistent.Add(placed);
        JailMarkers().Items.Add(placed.ToLink<ISkyrimMajorRecordGetter>());
        if (cell.Flags.HasFlag(Cell.Flag.IsInteriorCell)) prisons.Add((placed, cell, IsIsles(cell.EditorID?.StartsWith("OB") == true ? cell.EditorID[2..] : cell.EditorID)));
        if (scriptRefs.Contains(r.FormKey)) placedBySource[r.FormKey] = placed;
        return placed;
    }

    /// <summary>
    /// Each land's jail, once the cells are built: the chosen prison cell gets the faction's chests (its belongings
    /// chest is what tells Skyrim the jail), and the release markers go where the jail's way out leads.
    /// </summary>
    void FinishJails()
    {
        for (int i = 0; i < 2; i++)
        {
            if (landCrime[i] is not { } faction) continue;
            bool isles = i == 1;
            var preferred = isles ? IslesJails : CyrodiilJails;
            var candidates = prisons.Where(p => p.Isles == isles).ToList();
            if (candidates.Count == 0) continue;
            var jail = candidates.OrderBy(p => Array.FindIndex(preferred, n => ("OB" + n).Equals(p.Cell.EditorID, StringComparison.OrdinalIgnoreCase)) is var k and >= 0 ? k : 99).First();
            var at = jail.Marker.Placement!.Position;
            PlacedObject Chest(FormKey container, float dx) => new(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(container),
                Placement = new Placement { Position = new P3Float(at.X + dx, at.Y, at.Z - 2000), Rotation = new P3Float(0, 0, 0) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            var belongings = Chest(EvidencePlayerInventory, 0);
            var stolen = Chest(EvidenceStolenGoods, 80);
            jail.Cell.Persistent.Add(belongings);
            jail.Cell.Persistent.Add(stolen);
            faction.PlayerInventoryContainer = new FormLinkNullable<IPlacedObjectGetter>(belongings);
            faction.StolenGoodsContainer = new FormLinkNullable<IPlacedObjectGetter>(stolen);

            // Let out where the jail's way out leads (a load door of the jail cell, at its partner's side), else in the
            // jail itself.
            (Cell Cell, P3Float Pos, P3Float Rot)? release = null;
            foreach (var door in jail.Cell.Persistent.OfType<PlacedObject>())
            {
                var source = placedDoors.FirstOrDefault(kv => kv.Value == door).Key;
                if (source.IsNull || !sourceDoors.TryGetValue(source, out var src) || src.TeleportDestination is not { } dest) continue;
                if (!placedDoors.TryGetValue(dest.Destination.FormKey, out var partner) || CellOf(partner) is not { } outside || outside == jail.Cell) continue;
                release = (outside, dest.Position, dest.Rotation);
                break;
            }
            var (relCell, relPos, relRot) = release ?? (jail.Cell, at, jail.Marker.Placement.Rotation);
            var exterior = new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(SkPrisonMarker),
                Placement = new Placement { Position = relPos, Rotation = relRot },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            var wait = new PlacedObject(mod)
            {
                Base = new FormLinkNullable<IPlaceableObjectGetter>(XMarkerHeading),
                Placement = new Placement { Position = relPos, Rotation = relRot },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
            };
            relCell.Persistent.Add(exterior);
            relCell.Persistent.Add(wait);
            faction.ExteriorJailMarker = new FormLinkNullable<IPlacedObjectGetter>(exterior);
            faction.FollowerWaitMarker = new FormLinkNullable<IPlacedObjectGetter>(wait);
            JailCount++;
        }
    }

    readonly Dictionary<string, GlobalFloat> scriptGlobals = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A global the converted scripts and dialogue share (TMOB_*), made by whichever asks first.</summary>
    GlobalFloat ScriptGlobal(string edid)
    {
        if (scriptGlobals.TryGetValue(edid, out var hit)) return hit;
        var g = new GlobalFloat(mod) { EditorID = edid, Data = 0 };
        mod.Globals.Add(g);
        return scriptGlobals[edid] = g;
    }

    /// <summary>The built cell a reference is in (interiors, and exterior cells with their persistent cell).</summary>
    Cell? CellOf(PlacedObject placed)
    {
        foreach (var cell in mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells))
            if (cell.Persistent.Contains(placed) || cell.Temporary.Contains(placed)) return cell;
        foreach (var ws in mod.Worldspaces)
        {
            if (ws.TopCell is { } top && (top.Persistent.Contains(placed) || top.Temporary.Contains(placed))) return top;
            foreach (var cell in ws.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))
                if (cell.Persistent.Contains(placed) || cell.Temporary.Contains(placed)) return cell;
        }
        return null;
    }
}
