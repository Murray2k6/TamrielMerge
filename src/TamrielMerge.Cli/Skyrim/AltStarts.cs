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
/// <item>Oblivion: out of the sewers' South East Tunnel, "Deliver the Amulet" (MQ02) begun, with the Amulet of Kings.</item>
/// </list>
/// A door's spot is the outside door with the offset to where the game sets the player down coming out of it.
/// <para>Each game's own start-up (<see cref="Steps"/>), as its engine does it: what a new game starts, and on the way
/// in after the opening what the opening would have done by its end (2026-11-04, user: "make sure the quest initizlize on
/// starting morrwind, oblivion and redguard"). Redguard needs none: its scripts, dialogue and law are start-game quests
/// and its flags start at the game's own values; Cyrus's log begins with its first entry, as in the game.</para>
/// </summary>
public static class AltStarts
{
    public sealed record AfterOpening(int Story, uint RefId, float Dx, float Dy, float Dz, uint QuestId, int QuestStage, uint ItemId);

    /// <summary>One start-up step (TMStoryOrder.RunSteps): for a story's opening (After false) or the way in after it; a
    /// quest started (Value &lt; 0) or set to the stage Value, a global set to Value (globals before the player moves), or
    /// an item given, Value of it. The form is in the story's plugin, or in Plugin.</summary>
    public sealed record Step(int Story, bool After, uint FormId, float Value, string What, string? Plugin = null);

    /// <summary>What Sellus Gravius's answer about "duties" gives at the end of Morrowind's character making (its result
    /// script, INFO 1729913769476329813): the topics it adds, the directions it hands over (the package is the opening's
    /// item) and 87 gold; its Journal A1_1_FindSpymaster 1 is the opening's quest and the journal's index.</summary>
    static readonly string[] MorrowindDutiesTopics =
        ["Caius Cosades", "South Wall", "specific place", "someone in particular", "services", "my trade", "little secret", "latest rumors", "little advice"];
    const int MorrowindDutiesGold = 87;

    /// <summary>
    /// The converted games' own start-up steps, found in the converted plugins:
    /// <list type="bullet">
    /// <item>Morrowind after its opening: CharGenState -1 (as the census office's exit door, CharGenDoorExitCaptain, sets it;
    /// Main otherwise runs CharGen, the prison ship, on arrival); what the "duties" answer gives (MorrowindDutiesTopics:
    /// without them no one had anything to talk about, 2026-11-04, user: "dont see any dialogue options when you talk to
    /// morrowind npcs"), A1_1's journal index at 1 (the dialogue's GetJournalIndex reads it), the directions and the gold;
    /// and Main's start-up scripts that run only while CharGenState is 1: Startup (its run-once disables of quest people)
    /// and VampireCheck. Its opening needs none: CharGenState starts at 1
    /// (MwScriptsAttach) and Main runs CharGen once the player is in Morrowind.</item>
    /// <item>Oblivion's opening: CharacterGen started, as Oblivion's engine starts it on a new game (not start-game-enabled,
    /// and nothing in the data starts it; its stage 5 starts the MQ01 tutorial and the Emperor's scene). After the opening:
    /// CharacterGen's stage 88 (the end of Baurus's charge: Deliver the Amulet, MQ02, at 10 and 20, the topics) and MQ01's
    /// 100 (the tutorial over, both quests stopped), as the sewers' exit leaves them. MQ01 is the tutorial: its stage 10
    /// starts the prison's CharacterGen at 6 (before 2026-11-04 the way in after the opening set it).</item>
    /// </list>
    /// </summary>
    public static List<Step> Steps(string dir)
    {
        var steps = new List<Step>();
        IDisposable? Open(string name, out ISkyrimModGetter? mod)
        {
            mod = null;
            string path = Path.Combine(dir, name);
            if (!File.Exists(path)) return null;
            var m = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
            mod = m;
            return m;
        }
        using (Open("Morrowind.esm", out var mw))
            if (mw is not null)
            {
                static string San(string s) => new(s.Where(char.IsLetterOrDigit).ToArray());   // MwPluginBuilder.Sanitize
                var globals = mw.Globals.Where(g => g.EditorID is not null).GroupBy(g => g.EditorID!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
                if (globals.GetValueOrDefault("MWGlobalCharGenState") is { } chargen) steps.Add(new(2, true, chargen.FormKey.ID, -1, "CharGenState = -1"));
                if (globals.GetValueOrDefault("MWJournalIndexA11FindSpymaster") is { } index) steps.Add(new(2, true, index.FormKey.ID, 1, "A1_1 journal index = 1"));
                foreach (var topic in MorrowindDutiesTopics)
                    if (globals.GetValueOrDefault("MWTopicKnown" + San(topic)) is { } known) steps.Add(new(2, true, known.FormKey.ID, 1, $"topic \"{topic}\" known"));
                if (mw.EnumerateMajorRecords().FirstOrDefault(r => string.Equals(r.EditorID, "MWbkA11DirectionsCaiusCosades", StringComparison.OrdinalIgnoreCase)) is { } directions)
                    steps.Add(new(2, true, directions.FormKey.ID, 1, "the directions to Caius"));
                steps.Add(new(2, true, 0x00000F, MorrowindDutiesGold, $"{MorrowindDutiesGold} gold", "Skyrim.esm"));
                foreach (var script in new[] { "Startup", "VampireCheck" })
                    if (mw.Quests.FirstOrDefault(q => q.EditorID == "MWScript" + script) is { } q) steps.Add(new(2, true, q.FormKey.ID, -1, "start " + script));
            }
        using (Open("Oblivion.esm", out var ob))
            if (ob is not null)
            {
                var chargen = ob.Quests.FirstOrDefault(q => q.EditorID == "OBQCharactergen");
                var tutorial = ob.Quests.FirstOrDefault(q => q.EditorID == "OBQMQ01");
                if (chargen is not null) steps.Add(new(3, false, chargen.FormKey.ID, -1, "start CharacterGen"));
                if (chargen?.Stages.Any(s => s.Index == 88) == true) steps.Add(new(3, true, chargen.FormKey.ID, 88, "CharacterGen 88"));
                if (tutorial?.Stages.Any(s => s.Index == 100) == true) steps.Add(new(3, true, tutorial.FormKey.ID, 100, "MQ01 100"));
            }
        return steps;
    }

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
            if (mw is not null && Interiors(mw).FirstOrDefault(c => c.EditorID == "MWSeydaNeenCensusandExciseOffice") is { } office
                // The office has four doors outside: the opening ends at the last, "CharGen Exit Door" (the first found
                // was a courtyard door: the player began shut in the courtyard, and its barrel's tutorial ran; seen 2026-11-12).
                && OutsideOf(mw, office, [.. mw.EnumerateMajorRecords().Where(r => r.EditorID?.Contains("CharGenExitDoor", StringComparison.OrdinalIgnoreCase) == true).Select(r => r.FormKey)]) is { } d)
            {
                var quest = mw.Quests.FirstOrDefault(q => q.EditorID == "MWQA11FindSpymaster");
                var package = mw.EnumerateMajorRecords().FirstOrDefault(r => r.EditorID?.Contains("caiuspackage", StringComparison.OrdinalIgnoreCase) == true);
                found.Add(new(2, d.Ref, d.Dx, d.Dy, d.Dz, quest?.FormKey.ID ?? 0, quest?.Stages.Select(s => (int)s.Index).DefaultIfEmpty(0).Min() ?? 0, package?.FormKey.ID ?? 0));
            }
        using (Open("Oblivion.esm", out var ob))
            if (ob is not null && Interiors(ob).FirstOrDefault(c => c.EditorID == "OBImperialSewerSystemSE") is { } tunnel && OutsideOf(ob, tunnel) is { } d)
            {
                var amulet = ob.EnumerateMajorRecords().FirstOrDefault(r => r.EditorID is { } e && e.Contains("AmuletofKings", StringComparison.OrdinalIgnoreCase) && r is IArmorGetter or IMiscItemGetter);
                // Deliver the Amulet (MQ02) begins in Steps: CharacterGen's stage 88 sets it, as the game does.
                found.Add(new(3, d.Ref, d.Dx, d.Dy, d.Dz, 0, 0, amulet?.FormKey.ID ?? 0));
            }
        return found;
    }

    static IEnumerable<ICellGetter> Interiors(ISkyrimModGetter mod) => mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(sb => sb.Cells);

    static IEnumerable<IPlacedObjectGetter> AllRefs(ISkyrimModGetter mod) =>
        Interiors(mod).Concat(mod.Worldspaces.SelectMany(w => (w.TopCell is { } t ? new[] { t } : []).Concat(w.SubCells.SelectMany(b => b.Items).SelectMany(sb => sb.Items))))
            .SelectMany(c => c.Persistent.Concat(c.Temporary)).OfType<IPlacedObjectGetter>();

    /// <summary>The door outside that an interior's way out leads to (in a worldspace), and the offset from it to where
    /// the player is set down coming out.</summary>
    static (uint Ref, float Dx, float Dy, float Dz)? OutsideOf(ISkyrimModGetter mod, ICellGetter interior, HashSet<FormKey>? preferred = null)
    {
        var outside = new Dictionary<FormKey, IPlacedObjectGetter>();
        foreach (var ws in mod.Worldspaces)
            foreach (var r in (ws.TopCell?.Persistent ?? []).OfType<IPlacedObjectGetter>()) outside[r.FormKey] = r;
        foreach (var door in interior.Persistent.Concat(interior.Temporary).OfType<IPlacedObjectGetter>().OrderBy(r => preferred?.Contains(r.Base.FormKey) == true ? 0 : 1))
            if (door.TeleportDestination is { } t && outside.TryGetValue(t.Door.FormKey, out var ext) && ext.Placement is { } p)
                return (ext.FormKey.ID, t.Position.X - p.Position.X, t.Position.Y - p.Position.Y, t.Position.Z - p.Position.Z);
        return null;
    }
}
