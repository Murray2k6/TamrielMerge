using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Skyrim crime factions for a converted game's own law (docs/progression.md, "Crime by game and area"): each game's
/// areas keep their own bounty, apart from Skyrim's holds and from each other, so a crime faction here never lists
/// Skyrim's shared crime factions (CrimeFactionsList) and nothing it makes links to Skyrim's crime factions. Shared by
/// the games' builders:
/// <list type="bullet">
/// <item><see cref="Make"/>: the faction, tracking crime, with its crime gold values (Skyrim's own for a hold unless the
/// game says otherwise);</item>
/// <item><see cref="PlaceJail"/>: what Skyrim's arrest and jail need of it, placed in the game's prison: the prison marker
/// where a jailed player wakes and the player-inventory chest in the same cell (Skyrim finds the jail through them), the
/// stolen-goods chest, the follower wait marker and the exterior marker a released player walks out to;</item>
/// <item><see cref="MakeGuard"/> and <see cref="MakeResident"/>: a game's guards (Skyrim's IsGuard, so they pursue a
/// wanted player and force-greet them, with Skyrim's own arrest dialogue only when asked) and its townsfolk (whose
/// crimes, as victims and witnesses, go to the area).</item>
/// </list>
/// </summary>
public static class CrimeFactions
{
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);

    /// <summary>Skyrim's guard tag: its members are guards to the engine (Actor.IsGuard, DialogueCrimeGuards' IsGuard).</summary>
    public static readonly FormKey IsGuardFaction = Sk(0x086EEE);
    /// <summary>Skyrim's guard dialogue (DialogueCrimeGuards and DialogueGuardsGeneral speak only for its members).</summary>
    public static readonly FormKey GuardDialogueFaction = Sk(0x02BE3B);
    public static readonly FormKey PrisonMarker = Sk(0x000004);                    // DOOR PrisonMarker
    public static readonly FormKey EvidenceChestPlayerInventory = Sk(0x108D36);
    public static readonly FormKey EvidenceChestStolenGoods = Sk(0x0267E7);
    public static readonly FormKey XMarker = Sk(0x00003B), XMarkerHeading = Sk(0x000034);
    /// <summary>The jail clothes Skyrim's holds give (rags and boots).</summary>
    public static readonly FormKey JailOutfit = Sk(0x028B60);                      // BeggarOutfit
    /// <summary>Skyrim's crime factions (its holds, the Empire, the Stormcloaks, the Orcs...), which no converted game's
    /// people may belong to.</summary>
    public static bool IsSkyrimCrimeFaction(IFactionGetter f) =>
        f.FormKey.ModKey.FileName.String is "Skyrim.esm" or "Update.esm" or "Dawnguard.esm" or "HearthFires.esm" or "Dragonborn.esm"
        && (f.Flags & Faction.FactionFlag.TrackCrime) != 0;

    /// <summary>Crime gold for each crime (Skyrim's GMST defaults for a hold: murder 1000, assault 40, trespass 5,
    /// pickpocketing 25, theft half the stolen value, escaping jail 100, a werewolf seen 1000). <see cref="Arrest"/>:
    /// guards arrest (else they attack); <see cref="AttackOnSight"/>: guards attack a violent criminal on sight.</summary>
    public sealed record Values(bool Arrest = true, bool AttackOnSight = true, ushort Murder = 1000, ushort Assault = 40, ushort Trespass = 5,
        ushort Pickpocket = 25, float StealMult = 0.5f, ushort Escape = 100, ushort Werewolf = 1000);

    /// <summary>A crime faction: tracks crime, named for its bounty as a hold's is (shown, as Skyrim's holds are), with
    /// <paramref name="values"/> (Skyrim's defaults when null). At <paramref name="key"/> when given (a fixed number in
    /// a plugin other plugins build on).</summary>
    public static Faction Make(SkyrimMod mod, string editorId, string name, Values? values = null, FormKey? key = null)
    {
        var v = values ?? new Values();
        var f = key is { } k ? new Faction(k, SkyrimRelease.SkyrimSE) : new Faction(mod);
        f.EditorID = editorId;
        f.Name = name;
        f.Flags = Faction.FactionFlag.TrackCrime;
        f.Ranks.Add(new Rank { Number = 0 });
        f.CrimeValues = new CrimeValues
        {
            Arrest = v.Arrest, AttackOnSight = v.AttackOnSight, Murder = v.Murder, Assault = v.Assault, Trespass = v.Trespass,
            Pickpocket = v.Pickpocket, StealMult = v.StealMult, Escape = v.Escape, Werewolf = v.Werewolf,
        };
        f.JailOutfit.SetTo(JailOutfit);
        mod.Factions.Add(f);
        return f;
    }

    /// <summary>What <see cref="PlaceJail"/> placed.</summary>
    public sealed record Jail(PlacedObject PrisonMarker, PlacedObject PlayerInventory, PlacedObject StolenGoods, PlacedObject FollowerWait, PlacedObject Exterior);

    const int PersistentFlag = 0x400;

    static PlacedObject Ref(SkyrimMod mod, FormKey baseForm, P3Float at, float heading, string? editorId, FormKey? key)
    {
        var r = key is { } k ? new PlacedObject(k, SkyrimRelease.SkyrimSE) : new PlacedObject(mod);
        r.EditorID = editorId;
        r.Base = baseForm.ToNullableLink<IPlaceableObjectGetter>();
        r.Placement = new Placement { Position = at, Rotation = new P3Float(0, 0, heading) };
        r.SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)PersistentFlag;
        return r;
    }

    /// <summary>The exterior marker a released prisoner walks out to (Skyrim's "exterior jail marker"), placed persistent
    /// in <paramref name="cell"/> (an interior or a worldspace's persistent cell); at <paramref name="key"/> if given.</summary>
    public static PlacedObject Exterior(SkyrimMod mod, Cell cell, P3Float at, float heading, string editorId, FormKey? key = null)
    {
        var r = Ref(mod, XMarkerHeading, at, heading, editorId, key);
        cell.Persistent.Add(r);
        return r;
    }

    /// <summary>
    /// The jail of <paramref name="faction"/>: its prison marker at <paramref name="prisonSpot"/> in
    /// <paramref name="jailCell"/> (an interior), the evidence chests (owned by the faction, locked, out of reach
    /// below the floor) and the follower wait marker beside it, and <paramref name="exterior"/> (<see cref="Exterior"/>)
    /// as where the released player goes. Several factions may share a cell, each with its own markers and chests.
    /// <paramref name="keys"/>: fixed numbers for the four references placed (prison marker, inventory, stolen goods,
    /// wait), or null.
    /// </summary>
    public static Jail PlaceJail(SkyrimMod mod, Faction faction, Cell jailCell, P3Float prisonSpot, float heading, PlacedObject exterior,
        string editorPrefix, FormKey[]? keys = null)
    {
        FormKey? K(int i) => keys is { Length: 4 } ks ? ks[i] : null;
        var prison = Ref(mod, PrisonMarker, prisonSpot, heading, editorPrefix + "PrisonMarker", K(0));
        var below = new P3Float(prisonSpot.X, prisonSpot.Y, prisonSpot.Z - 2000);
        var inventory = Ref(mod, EvidenceChestPlayerInventory, below, 0, editorPrefix + "EvidenceChest", K(1));
        var stolen = Ref(mod, EvidenceChestStolenGoods, new P3Float(below.X + 120, below.Y, below.Z), 0, editorPrefix + "StolenGoodsChest", K(2));
        foreach (var chest in new[] { inventory, stolen })
        {
            chest.Owner.SetTo(faction);
            chest.Lock = new LockData { Level = LockLevel.RequiresKey };
        }
        var wait = Ref(mod, XMarkerHeading, new P3Float(prisonSpot.X + 64, prisonSpot.Y + 64, prisonSpot.Z), heading, editorPrefix + "FollowerWaitMarker", K(3));
        jailCell.Persistent.Add(prison);
        jailCell.Persistent.Add(inventory);
        jailCell.Persistent.Add(stolen);
        jailCell.Persistent.Add(wait);
        faction.ExteriorJailMarker.SetTo(exterior);
        faction.FollowerWaitMarker.SetTo(wait);
        faction.PlayerInventoryContainer.SetTo(inventory);
        faction.StolenGoodsContainer.SetTo(stolen);
        return new Jail(prison, inventory, stolen, wait, exterior);
    }

    /// <summary>A crime faction's jail markers and chests pointed at references already placed (another faction's jail
    /// shared, or references a plugin placed itself).</summary>
    public static void UseJail(Faction faction, Jail jail)
    {
        faction.ExteriorJailMarker.SetTo(jail.Exterior);
        faction.FollowerWaitMarker.SetTo(jail.FollowerWait);
        faction.PlayerInventoryContainer.SetTo(jail.PlayerInventory);
        faction.StolenGoodsContainer.SetTo(jail.StolenGoods);
    }

    /// <summary>
    /// A game's guard of <paramref name="crime"/>: the area's crime faction (theirs, and a member), Skyrim's guard tag (so
    /// the engine has them pursue a wanted player and force-greet them), and Skyrim's guard dialogue only when
    /// <paramref name="skyrimArrestDialogue"/> (the game has none of its own). Factions inherited from a vanilla template
    /// are dropped: the record's own list is used.
    /// </summary>
    public static void MakeGuard(Npc npc, IFormLinkGetter<IFactionGetter> crime, bool skyrimArrestDialogue = false)
    {
        OwnFactions(npc);
        npc.CrimeFaction.SetTo(crime.FormKey);
        Join(npc, crime.FormKey);
        Join(npc, IsGuardFaction);
        if (skyrimArrestDialogue) Join(npc, GuardDialogueFaction);
    }

    /// <summary>One of an area's people: crimes against them, and the crimes they see, go to <paramref name="crime"/>.</summary>
    public static void MakeResident(Npc npc, IFormLinkGetter<IFactionGetter> crime)
    {
        OwnFactions(npc);
        npc.CrimeFaction.SetTo(crime.FormKey);
        Join(npc, crime.FormKey);
    }

    /// <summary>Drops what a vanilla template gives a copy through "use factions" (Skyrim's crime and civil-war factions):
    /// the copy's own faction list and crime faction count.</summary>
    public static void OwnFactions(Npc npc) => npc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.Factions;

    static void Join(Npc npc, FormKey faction)
    {
        if (npc.Factions.Any(r => r.Faction.FormKey == faction)) return;
        npc.Factions.Add(new RankPlacement { Faction = new FormLink<IFactionGetter>(faction), Rank = 0 });
    }
}
