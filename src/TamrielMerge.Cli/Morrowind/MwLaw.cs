using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's law (docs/progression.md, "Crime by game and area"): its one bounty is one Skyrim crime faction,
/// MWCrimeFaction ("Morrowind"), with Morrowind's crime values (iCrimeKilling, iCrimeAttack, iCrimeTresspass,
/// iCrimePickPocket, fCrimeStealing; iWereWolfBounty), apart from Skyrim's holds:
/// <list type="bullet">
/// <item>every Morrowind person answers to it; its guards (class or faction "guard") are Skyrim's guards of it, so they
/// pursue a wanted player and force-greet them with Morrowind's own arrest greetings (the Greeting 0 responses for an
/// alarmed guard: pay, go to jail, resist), not Skyrim's;</item>
/// <item>its jail is Morrowind's: each PrisonMarker it placed is kept (Skyrim's prison marker), the nearest one is where
/// GoToJail takes the player (TMMwLaw, the quest MWLaw), and the crime faction's own jail (evidence chests, follower wait
/// and exterior markers, for Skyrim's confiscations) stands at the first interior one;</item>
/// <item>TMMwLaw keeps the dialogue's crime globals (PCHasCrimeGold, CrimeGoldDiscount, CrimeGoldTurnIn...) up to
/// date, so the Thieves Guild clears a bounty for half (fCrimeGoldDiscountMult) and guards take the fine.</item>
/// </list>
/// </summary>
public sealed partial class MwPluginBuilder
{
    Faction? mwCrime;
    Quest? lawQuest;
    readonly List<(PlacedObject Marker, string Cell)> prisonMarkers = [];
    bool lawFinished;

    int SettingInt(string name, int fallback) => world.Settings.TryGetValue(name, out var g) && g.Get("INTV") is { } v ? v.Int() : fallback;
    float SettingFloat(string name, float fallback) => world.Settings.TryGetValue(name, out var g) && g.Get("FLTV") is { } v ? v.Float() : fallback;

    /// <summary>Morrowind's crime faction, made on first use.</summary>
    Faction MwCrime => mwCrime ??= Skyrim.CrimeFactions.Make(mod, "MWCrimeFaction", "Morrowind", new Skyrim.CrimeFactions.Values(
        Arrest: true, AttackOnSight: true,
        Murder: (ushort)Math.Clamp(SettingInt("iCrimeKilling", 1000), 0, ushort.MaxValue),
        Assault: (ushort)Math.Clamp(SettingInt("iCrimeAttack", 40), 0, ushort.MaxValue),
        Trespass: (ushort)Math.Clamp(SettingInt("iCrimeTresspass", 5), 0, ushort.MaxValue),
        Pickpocket: (ushort)Math.Clamp(SettingInt("iCrimePickPocket", 25), 0, ushort.MaxValue),
        StealMult: SettingFloat("fCrimeStealing", 1),
        Escape: 100,
        Werewolf: (ushort)Math.Clamp(SettingInt("iWereWolfBounty", 1000), 0, ushort.MaxValue)));

    /// <summary>A Morrowind person under its law: a guard (Skyrim's guard of the crime faction) or a resident.</summary>
    void UnderLaw(Npc npc, bool guard)
    {
        var crime = MwCrime.ToLink<IFactionGetter>();
        if (guard) Skyrim.CrimeFactions.MakeGuard(npc, crime);
        else Skyrim.CrimeFactions.MakeResident(npc, crime);
    }

    /// <summary>The quest holding TMMwLaw (filled when the plugin is written, once every prison marker is placed).</summary>
    Quest LawQuest()
    {
        if (lawQuest is not null) return lawQuest;
        lawQuest = new Quest(mod)
        {
            EditorID = "MWLaw",
            Name = "Morrowind law",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 0,
            Type = Quest.TypeEnum.None,
            VirtualMachineAdapter = new QuestAdapter { Scripts = [new ScriptEntry { Name = "TMMwLaw" }] },
        };
        mod.Quests.Add(lawQuest);
        return lawQuest;
    }

    /// <summary>A Morrowind PrisonMarker reference kept as Skyrim's prison marker (persistent, in the cell it stands in).</summary>
    bool PrisonMarker(Tes3Ref r, Cell persistentCell, string cellName)
    {
        if (!r.BaseId.Equals("PrisonMarker", StringComparison.OrdinalIgnoreCase)) return false;
        var marker = new PlacedObject(mod)
        {
            EditorID = $"MWPrisonMarker{prisonMarkers.Count:000}",
            Base = new FormLinkNullable<IPlaceableObjectGetter>(Skyrim.CrimeFactions.PrisonMarker),
            Placement = new Placement { Position = new P3Float(r.Position.X, r.Position.Y, r.Position.Z), Rotation = new P3Float(0, 0, r.Rotation.Z) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        persistentCell.Persistent.Add(marker);
        prisonMarkers.Add((marker, cellName));
        return true;
    }

    /// <summary>
    /// After the doors are linked: TMMwLaw's markers and where each is in the world (an interior's through its door to
    /// the outside), the crime faction's jail at the first interior marker (exterior marker at that interior's door to
    /// the world), and the dialogue's crime globals.
    /// </summary>
    void FinishLaw()
    {
        if (lawFinished) return;
        lawFinished = true;
        var outside = OutsideDoors();
        var quest = LawQuest();
        var entry = quest.VirtualMachineAdapter!.Scripts[0];
        static ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };
        entry.Properties.Add(Obj("Crime", MwCrime.FormKey));
        entry.Properties.Add(Obj("PlayerRef", new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x14)));
        entry.Properties.Add(Obj("GameHour", new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x38)));
        entry.Properties.Add(Obj("Coin", new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x0F)));
        if (magic?.WorldSpot is { } spot) entry.Properties.Add(Obj("WorldSpot", spot.FormKey));
        var markers = new ScriptObjectListProperty { Name = "Markers" };
        var xs = new ScriptFloatListProperty { Name = "MarkerX" };
        var ys = new ScriptFloatListProperty { Name = "MarkerY" };
        foreach (var (marker, cell) in prisonMarkers)
        {
            // One inside with no way out to the world (Mournhold, reached by teleport) is for those in its own cell only
            // (TMMwLaw tries the player's cell first): far away for everyone else.
            var p = cell == Exterior ? marker.Placement!.Position : outside.TryGetValue(cell, out var door) ? door.Placement!.Position : new P3Float(1e9f, 1e9f, 0);
            markers.Objects.Add(new ScriptObjectProperty { Object = marker.ToLink<ISkyrimMajorRecordGetter>() });
            xs.Data.Add(p.X);
            ys.Data.Add(p.Y);
        }
        entry.Properties.Add(markers);
        entry.Properties.Add(xs);
        entry.Properties.Add(ys);
        foreach (var (property, global) in new[] { ("PCHasCrimeGold", "PCHasCrimeGold"), ("PCHasGoldDiscount", "PCHasGoldDiscount"),
            ("CrimeGoldDiscount", "CrimeGoldDiscount"), ("CrimeGoldTurnIn", "CrimeGoldTurnIn"), ("PCHasTurnIn", "PCHasTurnIn") })
            if (MwGlobal(global) is { } g) entry.Properties.Add(Obj(property, g.FormKey));
        entry.Properties.Add(new ScriptFloatProperty { Name = "DiscountMult", Data = SettingFloat("fCrimeGoldDiscountMult", 0.5f) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "TurnInMult", Data = SettingFloat("fCrimeGoldTurnInMult", 0.9f) });
        entry.Properties.Add(new ScriptIntProperty { Name = "DaysPerGold", Data = Math.Max(1, SettingInt("iDaysinPrisonMod", 100)) });

        // Skyrim's side of the jail (its confiscations put stolen goods in the faction's chest): at the first prison
        // marker inside, released at that interior's door to the world.
        // Morrowind's markers are mostly outside (Seyda Neen, Pelagiad, the Legion forts; Mournhold's palace courtyard is
        // inside): the first inside one, else the first outside. Without the outside built (a build of a few interiors)
        // the exterior marker stands by the prison marker.
        var top = mod.Worldspaces.FirstOrDefault(w => w.EditorID == "MWVvardenfell")?.TopCell;
        var jailAt = prisonMarkers.FirstOrDefault(m => m.Cell != Exterior && interiorsByName.ContainsKey(m.Cell));
        if (jailAt.Marker is null && top is not null) jailAt = prisonMarkers.FirstOrDefault(m => m.Cell == Exterior);
        if (jailAt.Marker is { } jm)
        {
            var jailCell = jailAt.Cell == Exterior ? top! : interiorsByName[jailAt.Cell];
            var exterior = top is not null && jailAt.Cell != Exterior && outside.TryGetValue(jailAt.Cell, out var door)
                ? Skyrim.CrimeFactions.Exterior(mod, top, door.Placement!.Position, door.Placement.Rotation.Z, "MWJailExterior")
                : Skyrim.CrimeFactions.Exterior(mod, jailCell, jm.Placement!.Position, jm.Placement.Rotation.Z, "MWJailExterior");
            Skyrim.CrimeFactions.PlaceJail(mod, MwCrime, jailCell, jm.Placement!.Position, jm.Placement.Rotation.Z, exterior, "MWJail");
        }
        else Problems.Add("law: no prison marker built; Morrowind's crime faction has no jail of Skyrim's");
    }

    /// <summary>For each interior, the outside door that leads (through other interiors if need be) into it: breadth-first
    /// from the outside's doors.</summary>
    Dictionary<string, PlacedObject> OutsideDoors()
    {
        var outside = new Dictionary<string, PlacedObject>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        foreach (var (r, placed, cell) in doors)
            if (cell == Exterior && r.DoorDestinationCell is { } into && !outside.ContainsKey(into)) { outside[into] = placed; queue.Enqueue(into); }
        var byCell = doors.GroupBy(d => d.Cell, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            string here = queue.Dequeue();
            foreach (var (r, _, _) in byCell.GetValueOrDefault(here) ?? [])
                if (r.DoorDestinationCell is { } next && !outside.ContainsKey(next)) { outside[next] = outside[here]; queue.Enqueue(next); }
        }
        return outside;
    }
}
