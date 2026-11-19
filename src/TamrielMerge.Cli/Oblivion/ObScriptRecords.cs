using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using Ob = Mutagen.Bethesda.Oblivion;

namespace TamrielMerge.Oblivion;

/// <summary>
/// The records Oblivion's scripts need beyond the converted world, made when the scripts are attached
/// (<see cref="AttachScripts"/>):
/// <list type="bullet">
/// <item>TMObState, a quest keeping what Oblivion's engine kept for its scripts (crimes, animations, misc stats...), with
/// the player's alias (TMObPlayer) and one alias per script package use (AddScriptPackage puts the actor in it);</item>
/// <item>Oblivion's AI packages the scripts name, as Skyrim packages from vanilla ones of the same kind (Travel,
/// Sandbox, Follow, Escort, Sleep, Eat, Flee, Hold position) aimed where Oblivion's were;</item>
/// <item>sounds (Oblivion's own files), effect shaders and weathers (vanilla ones that look the part), races,
/// markers for PositionCell/PositionWorld, the birthsign menu.</item>
/// </list>
/// </summary>
public sealed partial class ObPluginBuilder
{
    readonly Dictionary<FormKey, Package?> obPackages = new();
    readonly Dictionary<FormKey, int> obPackageTypes = new();
    readonly Dictionary<FormKey, SoundMarker?> obSounds = new();
    readonly Dictionary<string, PlacedObject> anchors = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, FormList> effectLists = new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string, MagicEffect> scriptEffects = new(StringComparer.OrdinalIgnoreCase);
    Quest? stateQuest;
    public int PackagesMade => obPackages.Values.Count(p => p is not null);
    public int SoundsMade => obSounds.Values.Count(s => s is not null);

    // ---- packages ----

    /// <summary>The vanilla package a kind of Oblivion package is made from (one aimed at a reference or place).</summary>
    static string PackageTemplate(int type) => type switch
    {
        1 or 7 => "DA02FighterFollowPlayer",   // Follow, Accompany
        2 => "MG01NiryaTravel1",               // Escort
        3 => "EmbryEat7x1",                    // Eat
        4 => "EmbrySleep0x7",                  // Sleep
        5 => "DorthePlay",                     // Wander: sandbox around the place
        9 => "MG08AncanoHoldPosition",         // Ambush: hold the spot
        10 => "DB08AntonFlee",                 // FleeNotCombat
        _ => "MS14LamiWait",                   // Find, Travel, UseItemAt: go there
    };

    /// <summary>The vanilla package a spell-casting one is made from (UseMagic: its first target input the spell, its
    /// second whom it is cast at; cast once).</summary>
    const string CastTemplate = "WhiterunTempleCastHealingSpellFarmer";

    /// <summary>
    /// An Oblivion package's kind (0 Find ... 8 UseItemAt, 11 CastMagic): the low byte of what Mutagen reads. Its PKDT
    /// holds the kind in one byte and three unused ones, which Mutagen takes as part of it (2026-10-22: 2,498 of
    /// Oblivion.esm's 7,209 packages and 261 of Knights.esp's 303 read as CDCDCD08 and the like, the editor's fill,
    /// so every one of them was made as "go there", sleepers and eaters and followers among them).
    /// </summary>
    static int PackageKind(Ob.IAIPackageGetter p) => p.Data is { } d ? (int)d.Type & 0xFF : 6;

    /// <summary>
    /// An Oblivion package place's radius: a whole number of units, which Mutagen reads as a float's bits
    /// (2026-10-22: all 6,576 of Oblivion.esm's radii read under 1, the 50 of Relmyna's as 7E-44).
    /// </summary>
    static int PlaceRadius(Ob.IAIPackageLocationGetter l) => BitConverter.SingleToInt32Bits(l.Radius);

    /// <summary>Packages that cast a spell (CastMagic, and UseItemAt with a spell or a kind of spell as its target, as
    /// Relmyna Verenim's at her experiments' victims): made from <see cref="CastTemplate"/>.</summary>
    readonly HashSet<FormKey> castPackages = new();

    bool Casts(Ob.IAIPackageGetter p, int kind)
    {
        if (kind == 11) return true;
        if (kind != 8 || p.Target is not { } t || t.Object == 0) return false;
        if (t.ObjectType == Ob.AIPackageTarget.ObjectTypes.ObjectType) return t.Object is >= 26 and <= 29;   // any, target, touch, self actor effects
        return records.GetValueOrDefault(ResolveRaw(p, (uint)t.Object)) is Ob.ISpellGetter;
    }

    readonly Dictionary<FormKey, Package?> obScheduledPackages = new();
    /// <summary>NPCs and the Oblivion packages each had, filled in by <see cref="FinishNpcPackages"/> once the quests their
    /// conditions name exist.</summary>
    readonly List<(Npc Npc, List<FormKey> Packages)> npcPackageLists = [];
    /// <summary>NPCs given their Oblivion packages (<see cref="FinishNpcPackages"/>), and the packages made for them.</summary>
    public int NpcsWithSchedules { get; private set; }
    public int ScheduledPackagesMade => obScheduledPackages.Values.Count(p => p is not null);

    /// <summary>
    /// An Oblivion AI package as an NPC's own Skyrim package (made on first use; null when its conditions can never hold
    /// in Skyrim): as <see cref="ObPackage"/> makes it, with Oblivion's schedule (month, weekday, date, hour and how long)
    /// and its conditions, so an NPC keeps Oblivion's day: sleeps, eats, works and travels when Oblivion has it do so
    /// (user, 2026-10-21: "fix those issues", NPCs moving normally; every Oblivion NPC had only a sandbox before).
    /// </summary>
    Package? ObScheduledPackage(FormKey key)
    {
        if (obScheduledPackages.TryGetValue(key, out var hit)) return hit;
        obScheduledPackages[key] = null;
        if (records.GetValueOrDefault(key) is not Ob.IAIPackageGetter source || ObPackage(key) is not { } made) return null;
        var sourcePlugin = plugins.LastOrDefault(pl => pl.AIPackages.ContainsKey(key));
        var conditions = sourcePlugin is null ? [] : ObConditions(source.Conditions, sourcePlugin);
        if (conditions is null) return null;
        var package = mod.Packages.DuplicateInAsNewRecord(made);
        package.EditorID = "OBSched" + Sanitize(source.EditorID ?? key.ID.ToString("X6"));
        // An NPC's own package belongs to no quest. Some vanilla templates do (a training one, a quest 0D74FC's), and the
        // copy kept it (QNAM): Skyrim refuses a quest's package in an NPC's own list, and its removing the refused one
        // never ends, so the game sat at the main menu with its loading wheel, the data never loaded (2026-10-25: 21 of
        // Oblivion's NPCs, 58 packages; the loader spinning on M'raaj-Dar's OBSchedDarkMraajDarTrain1).
        package.OwnerQuest.Clear();
        package.Conditions.Clear();
        package.Conditions.AddRange(conditions);
        // A walk-up greeting waits while its greeter is still saying a line, as Oblivion's does: Skyrim opens a
        // conversation started mid-line without the greeting (the Emperor at stage 17, still saying stage 16's "You...
        // I've seen you" when he reached the player: run r84, 2026-11-15, the menu opened with nothing of his to ask).
        // TMObState looks the speaker's packages over again when a scripted line ends.
        if (GreetsPlayer(source))
            package.Conditions.Add(new ConditionFloat { Data = new IsTalkingConditionData(), CompareOperator = CompareOperator.EqualTo, ComparisonValue = 0 });
        // (No "not there yet" condition on a follow with a place: ObPapyrus's frame looks for the arrival.)
        // (An Ambush's wait for the player to come near is the ForceGreet package's own: AimPackage.)
        if (source.Schedule is { } s)
        {
            package.ScheduleMonth = (sbyte)(int)s.Month;
            package.ScheduleDayOfWeek = (Package.DayOfWeek)(int)s.DayOfWeek;
            package.ScheduleDate = s.Day;
            package.ScheduleHour = s.Time == 255 ? (sbyte)-1 : (sbyte)s.Time;
            // Any hour is any minute (-1), as Skyrim's own unscheduled packages have it.
            package.ScheduleMinute = (sbyte)(s.Time == 255 ? -1 : 0);
            package.ScheduleDurationInMinutes = Math.Max(0, s.Duration) * 60;
        }
        else
        {
            // No schedule of Oblivion's: at any time, not the vanilla package's it was copied from (2026-11-10: a sleep
            // package without one kept EmbrySleep0x7's midnight to seven, an eating one EmbryEat7x1's).
            package.ScheduleMonth = -1;
            package.ScheduleDayOfWeek = (Package.DayOfWeek)(-1);
            package.ScheduleDate = 0;
            package.ScheduleHour = -1;
            package.ScheduleMinute = -1;
            package.ScheduleDurationInMinutes = 0;
        }
        // Oblivion's package flags where Skyrim has the same one at the same bit (offers services, must complete, unlock
        // doors at start and at end, continue if the player is near, once a day, always sneak, allow swimming, weapons
        // unequipped), in place of the vanilla package's it was copied from; always run is Skyrim's preferred speed, run.
        // Oblivion has no "weapon drawn" package flag; the vanilla package copied from may (2026-11-13: 5,877 of 12,559
        // converted packages had it, 538 of Skyrim's own 5,961, and the Emperor's party stood in the cell fists up).
        package.Flags = (Package.Flag)(Convert.ToUInt32(package.Flags) & ~0x800000u);
        if (source.Data is { } pd)
        {
            const uint shared = 0x1 | 0x4 | 0x40 | 0x80 | 0x200 | 0x400 | 0x20000 | 0x40000 | 0x200000;
            uint ob = Convert.ToUInt32(pd.Flags);
            uint sk = (Convert.ToUInt32(package.Flags) & ~(shared | 0x2000)) | (ob & shared);
            if ((ob & 0x2000) != 0) { sk |= 0x2000; package.PreferredSpeed = Package.Speed.Run; }
            package.Flags = (Package.Flag)sk;
            // "Skip fallout behavior" (0x1000): on such a package Oblivion's people neither greet, mutter nor stop to
            // talk with one another (the Emperor's party through the whole opening: seen talking to each other and
            // muttering idle lines in the prison cell, 2026-11-14). Skyrim's own of those are the package's interrupt
            // flags; TMObConversations leaves such people alone by OBConvBusy.
            if ((ob & 0x1000) != 0)
            {
                // (A walk-up greeting's own speaking is its ForceGreet's, not a hello in passing: ObPackage.)
                package.InterruptFlags &= ~SocialInterrupts;
                busyPackages.Add(package.FormKey);
            }
        }
        // Oblivion's people see a fight near them on any package and take their side, whatever the vanilla package it
        // was copied from had (SocialInterrupts).
        package.InterruptFlags |= Package.InterruptFlag.ObserveCombatBehavior;
        return obScheduledPackages[key] = package;
    }

    /// <summary>
    /// Each NPC's Oblivion packages, in Oblivion's order (Skyrim, like Oblivion, runs the first whose schedule and
    /// conditions hold), ahead of the sandbox it was given, which stays last for when none holds.
    /// </summary>
    void FinishNpcPackages()
    {
        // By index: making a package can make the actor it names, whose own packages join the list (2026-10-21: a
        // one-interior build ended "Collection was modified" here).
        for (int i = 0; i < npcPackageLists.Count; i++)
        {
            var (npc, keys) = npcPackageLists[i];
            var own = keys.Select(ObScheduledPackage).OfType<Package>().ToList();
            if (own.Count == 0) continue;
            var fallback = npc.Packages.ToList();
            npc.Packages.Clear();
            foreach (var p in own)
            {
                npc.Packages.Add(p.ToLink<IPackageGetter>());
                if (greetWaits.TryGetValue(p.FormKey, out var wait)) npc.Packages.Add(wait.ToLink<IPackageGetter>());
            }
            foreach (var p in fallback) npc.Packages.Add(p);
            NpcsWithSchedules++;
        }
        npcPackageLists.Clear();
    }

    /// <summary>An Oblivion AI package as a Skyrim package (made on first use).</summary>
    Package? ObPackage(FormKey key)
    {
        if (obPackages.TryGetValue(key, out var hit)) return hit;
        obPackages[key] = null;
        if (records.GetValueOrDefault(key) is not Ob.IAIPackageGetter source) return null;
        int type = PackageKind(source);
        obPackageTypes[key] = type;
        if (Casts(source, type)) castPackages.Add(key);
        bool greets = GreetsPlayer(source);
        var template = VanillaPackage(castPackages.Contains(key) ? CastTemplate : greets ? ForceGreetTemplate : PackageTemplate(type))
            ?? (type is 1 or 7 ? SkyrimEsm.Packages.FirstOrDefault(p => p.EditorID?.Contains("Follow") == true && p.Data.Values.OfType<IPackageDataTargetGetter>().Any(t => t.Target is IPackageTargetSpecificReferenceGetter)) : null)
            ?? VanillaPackage("MS14LamiWait")!;
        var package = mod.Packages.DuplicateInAsNewRecord(template);
        package.EditorID = "OBPack" + Sanitize(source.EditorID ?? key.ID.ToString("X6"));
        package.Conditions.Clear();
        package.VirtualMachineAdapter = null;
        if (greets)
        {
            // Skyrim's own ForceGreet does the waiting, the walk and the greeting; TMObGreet only marks the while.
            // No hellos in passing on the way (the template's interrupt flag; 190 of Skyrim.esm's 302 forcegreets have
            // it off): with it, the Emperor said his greeting as a hello at 150 units while still walking up, and it was
            // spent (said once) when he reached the player, who got a conversation with nothing in it (run r83,
            // 2026-11-15).
            package.InterruptFlags &= ~Package.InterruptFlag.HellosToPlayer;
            package.VirtualMachineAdapter = new PackageAdapter
            {
                Scripts = [new ScriptEntry { Name = GreetScript }],
                ScriptFragments = new PackageScriptFragments
                {
                    ExtraBindDataVersion = 2,
                    FileName = GreetScript,
                    OnBegin = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = GreetScript, FragmentName = "Fragment_Begin" },
                    OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = GreetScript, FragmentName = "Fragment_End" },
                    // Another package taking over (a quest stage moving on mid-walk) ends the greeting as well.
                    OnChange = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = GreetScript, FragmentName = "Fragment_Change" },
                },
            };
            GreetPackages++;
        }
        else if (UsesReference(source))
        {
            // Its end, at the thing, activates it (TMObUse; the thing is named when it is placed: AimPackage).
            package.VirtualMachineAdapter = new PackageAdapter
            {
                Scripts = [new ScriptEntry { Name = UseScript }],
                ScriptFragments = new PackageScriptFragments
                {
                    ExtraBindDataVersion = 2,
                    FileName = UseScript,
                    OnBegin = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = UseScript, FragmentName = "Fragment_Begin" },
                    OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = UseScript, FragmentName = "Fragment_End" },
                },
            };
            UsePackages++;
        }
        AimPackage(package, source);
        return obPackages[key] = package;
    }

    /// <summary>Skyrim.esm's package by EditorID, looked up once each (each package's template was a walk over all of
    /// Skyrim.esm's packages: 2026-10-22, the most of the minutes FinishNpcPackages took).</summary>
    IPackageGetter? VanillaPackage(string editorId)
    {
        vanillaPackages ??= SkyrimEsm.Packages.Where(p => p.EditorID is not null).GroupBy(p => p.EditorID!).ToDictionary(g => g.Key, g => g.First());
        return vanillaPackages.GetValueOrDefault(editorId);
    }
    Dictionary<string, IPackageGetter>? vanillaPackages;

    readonly HashSet<Condition> followEnds = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// The interrupts Oblivion's "skip fallout behavior" stops: greeting, talking and remarking in passing. Not seeing a
    /// fight and joining it, nor attacking within aggro radius: with every interrupt off (until 2026-11-17) the prison's
    /// Blades and Mythic Dawn assassins, all on such packages, fought only whom a script's StartCombat named, nobody
    /// came to anybody's help, and the first ambush stood still with three assassins alive by the Blades (run r110).
    /// </summary>
    const Package.InterruptFlag SocialInterrupts = Package.InterruptFlag.HellosToPlayer | Package.InterruptFlag.RandomConversations
        | Package.InterruptFlag.GreetCorpseBehavior | Package.InterruptFlag.FriendlyFireComments | Package.InterruptFlag.AllowIdleChatter;

    /// <summary>The scheduled packages made from Oblivion's "skip fallout behavior" ones (their greeting waits too).</summary>
    readonly HashSet<FormKey> busyPackages = [];
    public int BusyPackages => busyPackages.Count;

    /// <summary>OBConvBusy: the packages whose people don't join conversations, on OBDialogue's controller
    /// (TMObConversations.Busy). After every NPC's packages are made.</summary>
    void FillBusyPackages()
    {
        foreach (var (key, wait) in greetWaits) if (busyPackages.Contains(key)) { wait.InterruptFlags &= ~SocialInterrupts; busyPackages.Add(wait.FormKey); }
        // The greeting packages told of TMObState (who is speaking: a greeting waits for the greeter's own line to end).
        if (stateQuest is not null)
            foreach (var package in mod.Packages)
            {
                if (package.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == GreetScript) is { } greet && greet.Properties.Count == 0)
                {
                    greet.Properties.Add(new ScriptObjectProperty { Name = "ObState", Object = stateQuest.ToLink<ISkyrimMajorRecordGetter>() });
                    greet.Properties.Add(new ScriptObjectProperty { Name = "GreetNow", Object = GreetNowFaction().ToLink<ISkyrimMajorRecordGetter>() });
                }
                // And the use packages: the thing used, the actor's script is told its package is done.
                if (package.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == UseScript) is { } used && !used.Properties.Any(p => p.Name == "ObState"))
                    used.Properties.Add(new ScriptObjectProperty { Name = "ObState", Object = stateQuest.ToLink<ISkyrimMajorRecordGetter>() });
            }
        if (obDialogueQuest?.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == "TMObConversations") is not { } controller) return;
        var list = new FormList(mod) { EditorID = "OBConvBusy" };
        foreach (var k in busyPackages.OrderBy(k => k.ID)) list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(k));
        mod.FormLists.Add(list);
        controller.Properties.RemoveAll(p => p.Name == "Busy");
        controller.Properties.Add(new ScriptObjectProperty { Name = "Busy", Object = list.ToLink<ISkyrimMajorRecordGetter>() });
    }

    const string GreetScript = "TMObGreet";
    /// <summary>
    /// A vanilla package made from Skyrim's ForceGreet template (03C1C4), which is Oblivion's "walk up to the player and
    /// speak" as Skyrim has it: its inputs are a place the actor waits at [8], a place the player's coming into sets
    /// it off [62], how near the actor comes before speaking [75], and the topic [7] ("subtype HELO": the actor's own greeting).
    /// Until 2026-11-15 these were a travel package whose script activated the actor for the player at its end, and
    /// the Emperor's greeting came in only about half the runs (seen in game, some fifteen of them: the conversation
    /// opened with nothing of his to ask; neither his last line's timing, the greeting's conditions nor the package's
    /// interrupt flags was why).
    /// </summary>
    const string ForceGreetTemplate = "MS05InductionForcegreet";
    const string UseScript = "TMObUse";
    /// <summary>Packages made to walk to a thing and activate it (<see cref="UsesReference"/>), and those of them whose
    /// thing is placed.</summary>
    public int UsePackages { get; private set; }
    public int UsePackagesAimed => obPackages.Values.Count(p => p?.VirtualMachineAdapter?.Scripts.Any(s => s.Name == UseScript && s.Properties.Count > 0) == true);

    /// <summary>
    /// Oblivion's "use item at", or its "find", aimed at a placed thing that is used by activating it: an activator, a
    /// door, a container (not an item to pick up and use, nor a spell: <see cref="Casts"/>). Oblivion's actor goes to it
    /// and activates it. Made "go there" like the rest until 2026-11-14, Captain Renault walked to the switch of the
    /// secret wall in the prisoner's cell and never touched it (the switch's script opens the wall when she does). A
    /// find likewise (2026-11-16): the prison's rat (CGRatAmbushAPushBricks) finds the crumbling wall and pushes it,
    /// and the wall's script falls only for someone other than the player activating it.
    /// </summary>
    bool UsesReference(Ob.IAIPackageGetter p)
    {
        if (PackageKind(p) is not (8 or 0) || p.Target is not { } t || t.Object == 0 || t.ObjectType == Ob.AIPackageTarget.ObjectTypes.ObjectType) return false;
        if (((uint)t.Object & 0xFFFFFF) == 0x14 || Casts(p, 8)) return false;
        return SourceRef(ResolveRaw(p, (uint)t.Object)) is Ob.IPlacedObjectGetter placed
            && records.GetValueOrDefault(placed.Base.FormKey) is Ob.IActivatorGetter or Ob.IDoorGetter or Ob.IContainerGetter;
    }
    /// <summary>Packages made to walk up to the player and start talking (<see cref="GreetsPlayer"/>).</summary>
    public int GreetPackages { get; private set; }
    /// <summary>A greeting package's own copy on an NPC's list, and the wait Oblivion's Ambush has before it.</summary>
    readonly Dictionary<FormKey, Package> greetWaits = new();

    /// <summary>
    /// Oblivion's way of having someone walk up to the player and start talking: a Find or an Ambush package whose
    /// target is the player (64 Ambush packages in Oblivion.esm name the player, the Emperor's
    /// CGEmperorGreetPlayerInCell among them: made as "hold the spot", he stood at his marker and the opening held at
    /// stage 17, seen in game 2026-11-13). Made as a walk to the player whose end has the player's activation of the
    /// actor, which opens the conversation with Oblivion's GREETING.
    /// </summary>
    static bool GreetsPlayer(Ob.IAIPackageGetter p) =>
        PackageKind(p) is 0 or 9 && p.Target is { } t && t.ObjectType != Ob.AIPackageTarget.ObjectTypes.ObjectType && ((uint)t.Object & 0xFFFFFF) == 0x14;

    /// <summary>How near the player a greeting package brings its actor: the target's count (Oblivion's distance).</summary>
    static uint GreetDistance(Ob.IAIPackageGetter p) => (uint)Math.Clamp(p.Target?.Count ?? 0, 96, 192);

    /// <summary>
    /// A Follow or Accompany package with a place is over when the follower gets there (Oblivion: the Emperor follows
    /// Renote "to marker B" in the prisoner's cell, and the package's end sets the next stage; a Skyrim follow never
    /// ends, so the opening stood still: 2026-11-13). The package holds only while the follower is further from the
    /// place than its radius and the following distance.
    /// </summary>
    Condition? FollowEnd(Ob.IAIPackageGetter source)
    {
        if (FollowPlace(source) is not var (at, reach)) return null;
        var d = new GetDistanceConditionData();
        d.Target = new FormLinkOrIndex<IPlacedSimpleGetter>(d, at);
        return new ConditionFloat { Data = d, CompareOperator = CompareOperator.GreaterThan, ComparisonValue = reach };
    }

    /// <summary>Where a Follow or Accompany package with a place ends, and how near that place counts as there.</summary>
    (FormKey At, float Reach)? FollowPlace(Ob.IAIPackageGetter source)
    {
        if (PackageKind(source) is not (1 or 7) || source.Location is not { Type: Ob.AIPackageLocation.LocationType.NearReference } l
            || l.LocationReference.FormKeyNullable is not { } r) return null;
        FormKey? at = (r.ID & 0xFFFFFF) == 0x14 && r.ModKey.Name.Equals("Oblivion", StringComparison.OrdinalIgnoreCase) ? PlayerRefKey
            : placedBySource.TryGetValue(r, out var placed) ? placed.FormKey : null;
        if (at is null) return null;
        float reach = Math.Max(PlaceRadius(l), 64) + (source.Target is { } t && t.ObjectType != Ob.AIPackageTarget.ObjectTypes.ObjectType ? Math.Max(0, t.Count) : 0);
        return (at.Value, reach);
    }

    /// <summary>How many packages TMObState.FollowArrived knows (both copies of each follow that ends on arrival).</summary>
    public int FollowEndsListed { get; private set; }

    /// <summary>
    /// TMObState's list of the follows that end on arrival (<see cref="FollowEnd"/>), their places and distances, so a
    /// script's "package done" block runs when Skyrim drops one (ObPapyrus: OnPackageChange). After the world is placed
    /// and the NPCs' own copies are made.
    /// </summary>
    void FillFollowEnds()
    {
        if (mod.Quests.FirstOrDefault(q => q.EditorID == "TMObState")?.VirtualMachineAdapter?.Scripts.FirstOrDefault(e => e.Name == "TMObState") is not { } entry) return;
        var packages = new ScriptObjectListProperty { Name = "FollowPackages" };
        var places = new ScriptObjectListProperty { Name = "FollowPlaces" };
        var reaches = new List<float>();
        foreach (var (key, package) in obPackages.OrderBy(kv => kv.Key.ModKey.Name, StringComparer.OrdinalIgnoreCase).ThenBy(kv => kv.Key.ID))
        {
            if (records.GetValueOrDefault(key) is not Ob.IAIPackageGetter source || FollowPlace(source) is not var (at, reach)) continue;
            foreach (var copy in new[] { package, obScheduledPackages.GetValueOrDefault(key) })
            {
                if (copy is null) continue;
                packages.Objects.Add(new ScriptObjectProperty { Object = copy.ToLink<ISkyrimMajorRecordGetter>() });
                places.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(at) });
                reaches.Add(reach);
            }
        }
        entry.Properties.RemoveAll(pr => pr.Name is "FollowPackages" or "FollowPlaces" or "FollowReach");
        entry.Properties.Add(packages);
        entry.Properties.Add(places);
        entry.Properties.Add(new ScriptFloatListProperty { Name = "FollowReach", Data = [.. reaches] });
        FollowEndsListed = reaches.Count;
    }

    /// <summary>A package's place and target from its Oblivion package's (what it names that is made or placed by now).</summary>
    (bool Place, bool Target) AimPackage(Package package, Ob.IAIPackageGetter source)
    {
        if (castPackages.Contains(source.FormKey)) return AimCast(package, source);
        if (GreetsPlayer(source) && package.Data.Any(kv => kv.Key == 62)) return AimGreeting(package, source);
        var place = GreetsPlayer(source)
            ? new LocationTargetRadius { Target = new LocationTarget { Link = new FormLink<IPlacedGetter>(PlayerRefKey) }, Radius = GreetDistance(source) }
            : PackagePlace(source);
        var target = PackageTarget(source);
        if (package.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == UseScript) is { } use
            && source.Target is { } used && placedBySource.TryGetValue(ResolveRaw(source, (uint)used.Object), out var thing))
        {
            // The thing to activate, from the package's own place as in Oblivion (Renault works the wall's switch from
            // her marker B); a package with no place of its own walks to the thing.
            use.Properties.Clear();
            use.Properties.Add(new ScriptObjectProperty { Name = "Target", Object = thing.ToLink<ISkyrimMajorRecordGetter>() });
            place ??= new LocationTargetRadius { Target = new LocationTarget { Link = new FormLink<IPlacedGetter>(thing.FormKey) }, Radius = 96 };
        }
        // A find with no place of its own goes to what it finds. Left as it was, it kept its template's own place, a
        // spot in Skyrim (MS14LamiWait's, 02DD31): the prison's rat was sent there for the crumbling wall (2026-11-16).
        if (place is null && PackageKind(source) == 0 && target is PackageTargetSpecificReference found && !found.Reference.IsNull)
            place = new LocationTargetRadius { Target = new LocationTarget { Link = new FormLink<IPlacedGetter>(found.Reference.FormKey) }, Radius = 96 };
        if (PackageKind(source) is 1 or 7)
        {
            // Oblivion's following distance (the target's count), not the template's 300 to 500.
            if (source.Target is { } ft && ft.ObjectType != Ob.AIPackageTarget.ObjectTypes.ObjectType && ft.Count > 0
                && package.Data.Values.OfType<PackageDataFloat>().ToList() is { Count: >= 2 } floats)
            {
                floats[0].Data = Math.Max(64, ft.Count);
                floats[1].Data = Math.Max(64, ft.Count) + 150;
            }
            package.Conditions.RemoveAll(c => followEnds.Contains(c));
            // (No "not there yet" condition: a follower already at the place never took the package up, and its end
            // never came. ObPapyrus's frame looks for the arrival: TMObState.FollowPoll.)
        }
        foreach (var data in package.Data.Values)
        {
            if (data is PackageDataLocation loc && place is not null) loc.Location = place;
            else if (data is PackageDataTarget t)
            {
                // Whom Oblivion's names, else the actor itself, not the template's own (the player, in every template
                // here): until 2026-11-04 a package whose one wasn't made or placed kept it, and dogs following their
                // owners, a shepherd going to his sheepfold and the like followed the player instead, also on fast travel.
                if (target is not null) t.Target = target;
                else if (t.Target is IPackageTargetSpecificReferenceGetter s && s.Reference.FormKey == PlayerRefKey
                         && !(source.Target is { } st && ((uint)st.Object & 0xFFFFFF) == 0x14))
                    t.Target = new PackageTargetSelf();
            }
        }
        return (place is not null, target is not null);
    }

    /// <summary>
    /// A greeting package (Skyrim's ForceGreet) aimed from Oblivion's Find or Ambush at the player: the actor waits at
    /// Oblivion's place (where it stands, without one); the player within the place's radius sets it off (an Ambush:
    /// Oblivion's waits for the player to come that near; a Find goes to the player wherever they are); it speaks at
    /// Oblivion's distance (the target's count).
    /// </summary>
    (bool Place, bool Target) AimGreeting(Package package, Ob.IAIPackageGetter source)
    {
        var at = PackagePlace(source);
        ALocationTarget where = at?.Target ?? new LocationFallback { Type = LocationTargetRadius.LocationType.NearPackageStart };
        uint radius = source.Location is { } l ? (uint)Math.Max(PlaceRadius(l), 0) : 0;
        bool ambush = PackageKind(source) == 9;
        foreach (var kv in package.Data)
        {
            switch ((int)kv.Key, kv.Value)
            {
                case (7, PackageDataTopic topic):
                    // The actor's own greeting (Oblivion's GREETING: the Hello topics): "subtype HELO", the template's
                    // own default and what 21 of Skyrim's forcegreets hold (Lydia's, Irileth's). Not a specific topic
                    // left empty, as until 2026-11-15: with that the Emperor walked up to 60 units from the player and
                    // said nothing for eight minutes (run r70; 6 of vanilla's 409 forcegreets hold that, 382 a topic).
                    topic.Topics.Clear();
                    topic.Topics.Add(new TopicReferenceSubtype { Subtype = new RecordType("HELO") });
                    break;
                case (8, PackageDataLocation wait):
                    wait.Location = new LocationTargetRadius { Target = where, Radius = Math.Min(radius, 150) };
                    break;
                case (62, PackageDataLocation trigger):
                    trigger.Location = ambush && radius > 0 && source.Location?.Type != Ob.AIPackageLocation.LocationType.InCell
                        ? new LocationTargetRadius { Target = where, Radius = Math.Max(radius, 150) }
                        : new LocationTargetRadius { Target = where is LocationCell ? where : new LocationFallback { Type = LocationTargetRadius.LocationType.NearSelf }, Radius = where is LocationCell ? 0u : 8192u };
                    break;
                case (75, PackageDataLocation distance):
                    distance.Location = new LocationTargetRadius { Target = new LocationTarget { Link = new FormLink<IPlacedGetter>(PlayerRefKey) }, Radius = GreetDistance(source) };
                    break;
                case (79, PackageDataBool detected):
                    detected.Data = false;
                    break;
                case (17, PackageDataTarget who):
                    who.Target = new PackageTargetSpecificReference { Reference = new FormLink<IPlacedGetter>(PlayerRefKey) };
                    break;
            }
        }
        return (true, true);
    }

    /// <summary>
    /// A spell-casting package aimed: the spell (the Oblivion target where it is a spell or a kind of spell, else any),
    /// whom it is cast at (the target where that is a reference, else the reference the package takes place near, else
    /// the caster) and the place. Target is false when a spell or a reference it names isn't made or placed.
    /// </summary>
    (bool Place, bool Target) AimCast(Package package, Ob.IAIPackageGetter source)
    {
        var place = PackagePlace(source);
        APackageTarget? spell = null, at = null;
        bool found = true;
        if (source.Target is { } t && t.Object != 0)
        {
            if (t.ObjectType == Ob.AIPackageTarget.ObjectTypes.ObjectType)
                spell = ObjectTypeOf(t.Object) is { } kind and (TargetObjectType.AnyActorEffects or TargetObjectType.TargetActorEffects or TargetObjectType.TouchActorEffects or TargetObjectType.SelfActorEffects)
                    ? new PackageTargetObjectType { Type = kind } : null;
            else if (ResolveRaw(source, (uint)t.Object) is var key && records.GetValueOrDefault(key) is Ob.ISpellGetter)
            {
                if (ObSpell(key) is { } made) spell = new PackageTargetObjectID { Reference = new FormLink<IObjectIdGetter>(made.FormKey) };
                else found = false;
            }
            else if ((at = PackageTarget(source)) is null) found = false;
        }
        spell ??= new PackageTargetObjectType { Type = TargetObjectType.AnyActorEffects };
        if (at is null && source.Location is { Type: Ob.AIPackageLocation.LocationType.NearReference } l && l.LocationReference.FormKeyNullable is { } near)
        {
            if ((near.ID & 0xFFFFFF) == 0x14 && near.ModKey.Name.Equals("Oblivion", StringComparison.OrdinalIgnoreCase)) at = new PackageTargetSpecificReference { Reference = new FormLink<IPlacedGetter>(PlayerRefKey) };
            else if (placedBySource.TryGetValue(near, out var placed)) at = new PackageTargetSpecificReference { Reference = new FormLink<IPlacedGetter>(placed.FormKey) };
            else found = false;
        }
        at ??= new PackageTargetSelf();
        int targets = 0;
        foreach (var data in package.Data.Values)
        {
            if (data is PackageDataLocation loc && place is not null) loc.Location = place;
            else if (data is PackageDataTarget pt) pt.Target = targets++ == 0 ? spell : at;
        }
        return (place is not null, found);
    }

    /// <summary>
    /// Every package made so far aimed again once the world is placed: scripts and dialogue make packages before the
    /// cells are built, when the references they follow, escort or meet aren't placed yet (2026-10-22: a full build's
    /// packages lost 266 such targets, Phintias's, Delphine Jend's, Casta Scribonia's among them). Before the scheduled
    /// copies are made from them. What still names nothing placed or made is noted.
    /// </summary>
    void AimPackagesAgain()
    {
        foreach (var (key, package) in obPackages)
        {
            if (package is null || records.GetValueOrDefault(key) is not Ob.IAIPackageGetter source) continue;
            var (place, target) = AimPackage(package, source);
            if (!target && source.Target is { } t && t.Object != 0 && t.ObjectType != Ob.AIPackageTarget.ObjectTypes.ObjectType)
                Problems.Add($"package {source.EditorID}: its target {ResolveRaw(source, (uint)t.Object)} is neither placed nor made");
            // (A place not found falls back to "near where the actor is placed": that is the miss to note.)
            if (source.Location?.LocationReference.FormKeyNullable is { } l && !l.IsNull
                && source.Location.Type is Ob.AIPackageLocation.LocationType.NearReference or Ob.AIPackageLocation.LocationType.InCell or Ob.AIPackageLocation.LocationType.ObjectID
                && PackagePlace(source)?.Target is LocationFallback)
                Problems.Add($"package {source.EditorID}: its place {l} ({source.Location.Type}) is neither placed nor made");
        }
    }

    /// <summary>Where an Oblivion package takes place (near a reference, in a cell, near where the actor is or was placed).</summary>
    LocationTargetRadius? PackagePlace(Ob.IAIPackageGetter p)
    {
        if (p.Location is not { } l) return null;
        // Oblivion's own radius: 0 is "at the place" (a marker: the actor stands on it and takes its heading, as
        // Skyrim's does at a heading marker). Until 2026-11-14 every place was at least 64 wide, and the Blades stopped
        // anywhere within 64 of their markers, facing as they came (user: "the guards werent facing the door either
        // like in oblivion", "the near wall marker for the guard is ... very close to the wall not in the corner").
        // A wander keeps room to wander in.
        float radius = PackageKind(p) == 5 ? Math.Max(PlaceRadius(l), 64) : Math.Max(PlaceRadius(l), 0);
        ALocationTarget where = l.Type switch
        {
            // Near the player (reference 14; Glenroy's walk to the prisoner's cell is "within 120 of the player": written
            // as "near where he stands" it ended at once and the opening stood still at stage 15, seen 2026-11-13).
            Ob.AIPackageLocation.LocationType.NearReference when l.LocationReference.FormKeyNullable is { } pr && (pr.ID & 0xFFFFFF) == 0x14 && pr.ModKey.Name.Equals("Oblivion", StringComparison.OrdinalIgnoreCase) =>
                new LocationTarget { Link = new FormLink<IPlacedGetter>(PlayerRefKey) },
            Ob.AIPackageLocation.LocationType.NearReference when l.LocationReference.FormKeyNullable is { } r && placedBySource.TryGetValue(r, out var placed) =>
                new LocationTarget { Link = new FormLink<IPlacedGetter>(placed.FormKey) },
            Ob.AIPackageLocation.LocationType.InCell when l.LocationReference.FormKeyNullable is { } c && interiorsBySource.TryGetValue(c, out var cell) =>
                new LocationCell { Link = new FormLink<ICellGetter>(cell.FormKey) },
            Ob.AIPackageLocation.LocationType.NearCurrentLocation => new LocationFallback { Type = LocationTargetRadius.LocationType.NearPackageStart },
            Ob.AIPackageLocation.LocationType.ObjectID when l.LocationReference.FormKeyNullable is { } o && records.ContainsKey(o) && (Item(o) ?? Base(o)) is { } obj =>
                new LocationObjectId { Link = new FormLink<IObjectIdGetter>(obj.FormKey) },
            // Near a kind of object: the type's code where a form would be (read as a form link).
            Ob.AIPackageLocation.LocationType.ObjectType when l.LocationReference.FormKeyNullable is { } code && ObjectTypeOf((int)code.ID) is { } kind =>
                new LocationObjectType { Type = kind },
            _ => new LocationFallback { Type = LocationTargetRadius.LocationType.NearEditorLocation },
        };
        return new LocationTargetRadius { Target = where, Radius = (uint)radius };
    }

    /// <summary>Whom or what an Oblivion package is about (the one to follow, escort, flee from...).</summary>
    APackageTarget? PackageTarget(Ob.IAIPackageGetter p)
    {
        if (p.Target is not { } t || t.Object == 0) return null;
        // A kind of object, not a form (2026-10-17: read as forms they were "unknown base 00000D:Oblivion.esm", Agronak's
        // and Bruscus Dannus's weapons, and "00000C:Knights.esp", the Knights' praying at furniture).
        if (t.ObjectType == Ob.AIPackageTarget.ObjectTypes.ObjectType)
            return ObjectTypeOf(t.Object) is { } kind ? new PackageTargetObjectType { Type = kind, CountOrDistance = t.Count } : null;
        if (((uint)t.Object & 0xFFFFFF) == 0x14) return new PackageTargetSpecificReference { Reference = new FormLink<IPlacedGetter>(PlayerRefKey) };
        var key = ResolveRaw(p, (uint)t.Object);
        // The player's base (form 7; "000007:Knights.esp", a minion attacking the player): the player.
        if (key.ID == 7) return new PackageTargetSpecificReference { Reference = new FormLink<IPlacedGetter>(PlayerRefKey) };
        if (placedBySource.TryGetValue(key, out var placed))
            return new PackageTargetSpecificReference { Reference = new FormLink<IPlacedGetter>(placed.FormKey), CountOrDistance = t.Count };
        // A reference not placed (yet: AimPackagesAgain) is no base object to look up.
        if (records.ContainsKey(key) && (Item(key) ?? Base(key) ?? Actor(key)) is { } obj)
            return new PackageTargetObjectID { Reference = new FormLink<IObjectIdGetter>(obj.FormKey), CountOrDistance = t.Count };
        return null;
    }

    /// <summary>The references an Oblivion package names (placed persistent, so the package can point at them).</summary>
    IEnumerable<FormKey> PackageRefs(Ob.IAIPackageGetter p)
    {
        if (p.Location?.LocationReference.FormKeyNullable is { } l) yield return l;
        if (p.Target is { ObjectType: not Ob.AIPackageTarget.ObjectTypes.ObjectType } t && (uint)t.Object is var raw and not 0) yield return ResolveRaw(p, raw);
    }

    /// <summary>
    /// Oblivion's object types (a package's target) as Skyrim's: Oblivion's list has apparatus (2) and soul gems (17),
    /// which Skyrim's hasn't (as misc items here), so the rest are one or two further on (Oblivion's 12 furniture is
    /// Skyrim's 11, 13 any weapon 12, 30 any actor 28).
    /// </summary>
    static TargetObjectType? ObjectTypeOf(int oblivion) => oblivion switch
    {
        1 => TargetObjectType.Activators,
        2 or 10 or 17 => TargetObjectType.Misc,   // apparatus, misc, soul gems
        3 => TargetObjectType.Armor,
        4 => TargetObjectType.Books,
        5 => TargetObjectType.Clothing,
        6 => TargetObjectType.Containers,
        7 => TargetObjectType.Doors,
        8 => TargetObjectType.Ingredients,
        9 => TargetObjectType.Lights,
        11 => TargetObjectType.Flora,
        12 => TargetObjectType.Furniture,
        13 => TargetObjectType.AnyWeapons,
        14 => TargetObjectType.Ammo,
        15 => TargetObjectType.Npcs,
        16 => TargetObjectType.Creatures,
        18 => TargetObjectType.Keys,
        19 => TargetObjectType.Alchemy,
        20 => TargetObjectType.Food,
        21 => TargetObjectType.AllCombatWearable,
        22 => TargetObjectType.AllWearable,
        23 => TargetObjectType.RangedWeapons,
        24 => TargetObjectType.MeleeWeapons,
        25 => TargetObjectType.NoWeapons,
        26 => TargetObjectType.AnyActorEffects,
        27 => TargetObjectType.TargetActorEffects,
        28 => TargetObjectType.TouchActorEffects,
        29 => TargetObjectType.SelfActorEffects,
        30 => TargetObjectType.AnyActors,
        _ => null,
    };

    /// <summary>A package's raw FormID (its target, written as a number): the top byte indexes the masters of the plugin
    /// whose version of the package wins, then that plugin itself.</summary>
    FormKey ResolveRaw(Ob.IAIPackageGetter p, uint raw)
    {
        var source = plugins.LastOrDefault(pl => pl.AIPackages.ContainsKey(p.FormKey));
        if (source is null) return new FormKey(p.FormKey.ModKey, raw & 0xFFFFFF);
        int index = (int)(raw >> 24);
        var masters = source.ModHeader.MasterReferences;
        return new FormKey(index < masters.Count ? masters[index].Master : source.ModKey, raw & 0xFFFFFF);
    }

    // ---- sounds ----

    /// <summary>An Oblivion sound as a Skyrim sound (its own files, copied), made on first use.</summary>
    SoundMarker? ObSound(FormKey key)
    {
        if (obSounds.TryGetValue(key, out var hit)) return hit;
        obSounds[key] = null;
        if (records.GetValueOrDefault(key) is not Ob.ISoundGetter s || s.File is not { Length: > 0 } file) return null;
        string source = ObData.Normalize(@"sound\" + file);
        var files = new List<string>();
        if (Path.HasExtension(source) && data.Exists(source)) files.Add(source);
        else
        {
            // A folder: Oblivion picks one of its files at random, as Skyrim does among a descriptor's files.
            string folder = source.TrimEnd('\\') + @"\";
            files.AddRange(data.List(folder).Where(f => !f[folder.Length..].Contains('\\')));
        }
        files = files.Where(f => f.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) { Problems.Add($"script sound {s.EditorID}: {file} not found (or not a wav)"); return null; }
        var descriptor = mod.SoundDescriptors.DuplicateInAsNewRecord(SkyrimEsm.SoundDescriptors.First(d => d.EditorID == "QSTTG01UrnSmashSD"));
        descriptor.EditorID = "OBSound" + Sanitize(s.EditorID ?? key.ID.ToString("X6")) + "SD";
        descriptor.SoundFiles.Clear();
        foreach (var f in files)
        {
            var bytes = data.Read(f);
            if (bytes is null) continue;
            string rel = Path.Combine("fx", "TamrielMerge", "OB", f[6..]);
            string target = Path.Combine(modOutDir, "Sound", rel);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, bytes);
            descriptor.SoundFiles.Add(new Mutagen.Bethesda.Plugins.Assets.AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType>(rel));
        }
        var marker = new SoundMarker(mod) { EditorID = "OBSound" + Sanitize(s.EditorID ?? key.ID.ToString("X6")), ObjectBounds = new ObjectBounds(), SoundDescriptor = descriptor.ToNullableLink<ISoundDescriptorGetter>() };
        mod.SoundMarkers.Add(marker);
        return obSounds[key] = marker;
    }

    // ---- look-alikes ----

    /// <summary>The vanilla effect shader that looks most like an Oblivion one (by its name) or a magic effect (by code).</summary>
    FormKey? ShaderStandIn(string name)
    {
        string n = name.ToLowerInvariant();
        string pick =
            n.Contains("fire") || n.Contains("flame") || n is "fidg" or "fish" ? "FireFXShader"
            : n.Contains("frost") || n.Contains("ice") || n.Contains("cold") || n is "frdg" or "frsh" ? "FrostFXShader"
            : n.Contains("shock") || n.Contains("lightning") || n.Contains("storm") || n is "shdg" or "lish" ? "ShockFXShader"
            : n.Contains("ghost") || n.Contains("spirit") || n.Contains("dead") || n is "zgho" ? "GhostFXShader"
            : n.Contains("invis") || n.Contains("chameleon") || n is "invi" or "chml" ? "InvisFXShader"
            : n.Contains("soul") || n is "strp" ? "SoulTrapFXShader"
            : n.Contains("para") || n is "para" ? "ParalyzeFxShader"
            : n.Contains("reanim") || n.Contains("summon") || n.Contains("conjur") || n is "rean" || n.StartsWith('z') && n.Length == 4 ? "ReanimateFXShader"
            : n.Contains("shield") || n.Contains("ward") || n.Contains("reflect") || n.Contains("absorption") || n is "shld" or "rflc" or "sabs" ? "ShieldSpellFXS"
            : n.Contains("heal") || n.Contains("restor") || n.Contains("cure") || n.StartsWith("re") && n.Length == 4 || n.StartsWith("fo") && n.Length == 4 || n.StartsWith("cu") && n.Length == 4 ? "HealFXS"
            : n.Contains("absorb") || n.StartsWith("ab") && n.Length == 4 ? "AbsorbHealthFXS"
            : n.Contains("drain") || n.Contains("damage") || n.StartsWith("dr") && n.Length == 4 || n.StartsWith("dg") && n.Length == 4 ? "DamageHealthFXS"
            : n.Contains("poison") ? "ChaurusPoisonFXShader"
            : n.Contains("turn") || n.Contains("undead") || n is "turn" ? "TurnUndeadFlamesFXS"
            : n.Contains("bound") || (n.StartsWith("bw") || n.StartsWith("ba")) && n.Length == 4 || n is "myth" or "myhl" ? "EnchBoundSwordFXS"
            : n.Contains("calm") || n.Contains("charm") || n.Contains("rally") || n is "calm" or "chrm" or "raly" or "cohu" or "cocr" ? "IllusionPositiveFXS"
            : n.Contains("fear") || n.Contains("frenzy") || n.Contains("silence") || n is "frnz" or "demo" or "slnc" ? "IllusionNegativeFXS"
            : n.Contains("water") || n is "wabr" or "wawa" ? "WaterBreathingFXS"
            : n.Contains("detect") || n.Contains("life") || n is "dtct" ? "LifeDetected"
            : n.Contains("shadow") || n.Contains("dark") || n.Contains("night") || n.Contains("order") ? "NightmareFXShader"
            : n.Contains("fade") ? "TimeFadeOut01FXS"
            : n.Contains("green") ? "EnchGreenFXShader"
            : n.Contains("red") || n.Contains("blood") ? "EnchRedFXShader"
            : n.Contains("blue") || n.Contains("light") || n is "lght" or "neye" ? "EnchBlueFXShader"
            : n.Contains("violet") || n.Contains("purple") ? "GhostVioletFXShader"
            : "EnchPurpleFXShader";
        return Vanilla.Get<IEffectShaderGetter>(pick)?.FormKey;
    }

    /// <summary>The vanilla weather most like an Oblivion one (by name): Oblivion's red skies are a storm.</summary>
    FormKey? WeatherStandIn(string name)
    {
        string n = name.ToLowerInvariant();
        string pick =
            n.Contains("oblivion") || n.Contains("thunder") || n.Contains("storm") ? "SkyrimStormRain"
            : n.Contains("snow") ? "SkyrimOvercastSnow"
            : n.Contains("rain") ? "SkyrimOvercastRain"
            : n.Contains("whiteout") || n.Contains("fog") || n.Contains("mist") ? "SkyrimFog"
            : n.Contains("cloud") || n.Contains("overcast") || n.Contains("gloom") ? "SkyrimCloudy"
            : "SkyrimClear";
        return Vanilla.Get<IWeatherGetter>(pick)?.FormKey;
    }

    FormKey? CombatStyleStandIn(string name)
    {
        string n = name.ToLowerInvariant();
        string pick = n.Contains("archer") || n.Contains("missile") || n.Contains("marks") ? "csHumanMissile"
            : n.Contains("mage") || n.Contains("magic") || n.Contains("spell") ? "csHumanMagic"
            : "csHumanMeleeLvl1";
        return Vanilla.Get<ICombatStyleGetter>(pick)?.FormKey;
    }

    /// <summary>A persistent marker in an interior or worldspace, for moving there to coordinates (PositionCell).</summary>
    FormKey? Anchor(string cellOrWorld)
    {
        if (anchors.TryGetValue(cellOrWorld, out var hit)) return hit.FormKey;
        if (scriptWorld?.KeyOf(cellOrWorld) is not { } key) return null;
        Cell? cell = interiorsBySource.GetValueOrDefault(key) ?? (worldspaces.TryGetValue(key, out var ws) ? ws.TopCell : null);
        if (cell is null) return null;
        var marker = new PlacedObject(mod)
        {
            EditorID = "OBAnchor" + Sanitize(cellOrWorld),
            Base = new FormLinkNullable<IPlaceableObjectGetter>(Skyrim.TownNights.XMarker),
            Placement = new Placement { Position = new P3Float(0, 0, 0), Rotation = new P3Float(0, 0, 0) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        cell.Persistent.Add(marker);
        anchors[cellOrWorld] = marker;
        return marker.FormKey;
    }

    /// <summary>The Skyrim magic effects an Oblivion effect code became (for HasMagicEffect), in a list.</summary>
    FormKey EffectList(string code)
    {
        if (effectLists.TryGetValue(code, out var hit)) return hit.FormKey;
        var list = new FormList(mod) { EditorID = "OBEffects" + Sanitize(code) };
        string? name = code.Equals("SEFF", StringComparison.OrdinalIgnoreCase) ? "Script" : Skyrim.TesMagic.OblivionName(code.ToUpperInvariant());
        if (name is not null)
            foreach (var m in mod.MagicEffects.Where(m => m.EditorID?.StartsWith("OBEffect" + name, StringComparison.OrdinalIgnoreCase) == true))
                list.Items.Add(m.ToLink<ISkyrimMajorRecordGetter>());
        mod.FormLists.Add(list);
        effectLists[code] = list;
        return list.FormKey;
    }

    /// <summary>A generated record a script property names by a made-up EditorID (@anchor:, @effects:, @mod:...).</summary>
    FormKey? Special(ObPapyrus.Property p)
    {
        string id = p.EditorID;
        int colon = id.IndexOf(':');
        string kind = id[..colon], arg = id[(colon + 1)..];
        switch (kind)
        {
            case "@anchor": return Anchor(arg);
            case "@effects": return EffectList(arg);
            case "@effectshader":
                // The magic effect's own shader (as its look-alike), else one for the effect itself.
                if (scriptWorld!.MagicEffects.TryGetValue(arg, out var mgef) && mgef.Data?.EffectShader.FormKeyNullable is { } efsh && records.GetValueOrDefault(efsh)?.EditorID is { } efshId)
                    return ShaderStandIn(efshId) ?? ShaderStandIn(arg);
                return ShaderStandIn(arg);
            case "@mod" when arg == "OBGuards": return Skyrim.TownNights.Guards(mod, "OB").FormKey;
            case "@ref": return scriptWorld!.KeyOf(id) is { } rk && placedBySource.TryGetValue(rk, out var placed) ? placed.FormKey : null;
            // AddTopic's topic: its OBTopicKnown global (any topic; only the hidden ones' responses ask for it).
            case "@topic": return scriptWorld!.KeyOf(arg) is { } tk && records.GetValueOrDefault(tk) is Ob.IDialogTopicGetter ? TopicKnownGlobal(tk).FormKey : null;
            // A package's copy on the actors' own lists (ObScheduledPackage), which is the one they run and the package
            // events report; the script copy (ObPackage) where it has none.
            case "@sched": return scriptWorld!.KeyOf(arg) is { } pk && records.GetValueOrDefault(pk) is Ob.IAIPackageGetter ? (ObScheduledPackage(pk) ?? ObPackage(pk))?.FormKey : null;
        }
        return null;
    }

    // ---- the state quest ----

    /// <summary>TMObState: the quest with the state, the player's alias and the script package aliases.</summary>
    Quest StateQuest(Func<ObPapyrus.Property, FormKey?> fill)
    {
        if (stateQuest is not null) return stateQuest;
        var quest = stateQuest = new Quest(mod)
        {
            EditorID = "TMObState",
            Name = "Oblivion's engine state",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 90,
            Type = Quest.TypeEnum.None,
        };
        mod.Quests.Add(quest);
        var player = new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x14);
        quest.Aliases.Add(new QuestAlias { ID = 0, Name = "Player", ForcedReference = new FormLinkNullable<IPlacedGetter>(player) });
        // One alias per use of each script package (as many as the scripts add it, up to 4): AddScriptPackage puts the
        // actor in a free one.
        var slots = new List<(uint Alias, Package Package)>();
        uint next = 1;
        foreach (var (pkgKey, uses) in scriptPackageUses.OrderBy(kv => kv.Key.ID))
        {
            if (ObPackage(pkgKey) is not { } package) continue;
            for (int i = 0; i < Math.Clamp(uses, 1, 4) && slots.Count < 120; i++)
            {
                quest.Aliases.Add(new QuestAlias
                {
                    ID = next,
                    Name = $"ScriptPackage{next}",
                    Flags = QuestAlias.Flag.Optional | QuestAlias.Flag.AllowReuseInQuest | QuestAlias.Flag.AllowReserved,
                    PackageData = [package.ToLink<IPackageGetter>()],
                });
                slots.Add((next, package));
                next++;
            }
        }
        FormList NewList(string edid)
        {
            var l = new FormList(mod) { EditorID = edid };
            mod.FormLists.Add(l);
            return l;
        }
        ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };
        var entry = new ScriptEntry { Name = "TMObState" };
        entry.Properties.Add(Obj("PlayerRef", player));
        if (fill(new ObPapyrus.Property("PCCrimeGold", "GlobalVariable", "TMOB_PCCrimeGold", Generated: true)) is { } crimeGold) entry.Properties.Add(Obj("PCCrimeGold", crimeGold));
        if (fill(new ObPapyrus.Property("PCAmountSoldStolen", "GlobalVariable", "TMOB_PCAmountSoldStolen", Generated: true)) is { } sold) entry.Properties.Add(Obj("PCAmountSoldStolen", sold));
        foreach (var name in new[] { "TalkedToPC", "DestroyedRefs", "RestrainedActors", "NoRumors", "StolenItems" })
            entry.Properties.Add(Obj(name, NewList("TMOb" + name).FormKey));
        var crimeFactions = new ScriptObjectListProperty { Name = "CrimeFactions" };
        foreach (var f in translations.Values.SelectMany(o => o.Properties).Where(p => p.Name.StartsWith("cf_", StringComparison.Ordinal)).Select(p => p.EditorID).Distinct(StringComparer.OrdinalIgnoreCase).Take(128))
            if (scriptWorld!.KeyOf(f) is { } fk && ObFaction(fk) is { } faction) crimeFactions.Objects.Add(new ScriptObjectProperty { Object = faction.ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(crimeFactions);
        // The lands' crime factions and jails (ObCrime): the bounty Skyrim keeps, and IsPlayerInJail.
        entry.Properties.Add(Obj("CyrodiilCrime", LandCrime(false).FormKey));
        entry.Properties.Add(Obj("IslesCrime", LandCrime(true).FormKey));
        entry.Properties.Add(new ScriptObjectListProperty { Name = "CityCrimes", Objects = [.. CityCrimes().Select(f => new ScriptObjectProperty { Object = f.ToLink<ISkyrimMajorRecordGetter>() })] });
        entry.Properties.Add(Obj("PCInSEWorld", ScriptGlobal("TMOB_PCInSEWorld").FormKey));
        entry.Properties.Add(Obj("PCInJail", ScriptGlobal("TMOB_PCInJail").FormKey));
        // The play clock, for every script to read without asking TMObState (ObPapyrus.Clock).
        entry.Properties.Add(Obj("ClockPaused", ScriptGlobal("TMOB_ClockPaused").FormKey));
        entry.Properties.Add(Obj("ClockHeld", ScriptGlobal("TMOB_ClockHeld").FormKey));
        entry.Properties.Add(Obj("ClockLoadsGlobal", ScriptGlobal("TMOB_ClockLoads").FormKey));
        entry.Properties.Add(Obj("StageBusy", ScriptGlobal("TMOB_StageBusy").FormKey));
        entry.Properties.Add(Obj("JailMarkers", JailMarkers().FormKey));
        // GetGameRestarted: a form never placed, whose name (SetName, which no save keeps) tells a fresh start.
        var marker = new MiscItem(mod) { EditorID = "TMObLaunchMarker", Name = "TamrielMerge", Value = 0, Weight = 0 };
        mod.MiscItems.Add(marker);
        entry.Properties.Add(Obj("LaunchMarker", marker.FormKey));
        entry.Properties.Add(new ScriptObjectListProperty { Name = "PackageSlots", Objects = [.. slots.Select(s => new ScriptObjectProperty { Object = quest.ToLink<ISkyrimMajorRecordGetter>(), Alias = (short)s.Alias })] });
        entry.Properties.Add(new ScriptObjectListProperty { Name = "SlotPackages", Objects = [.. slots.Select(s => new ScriptObjectProperty { Object = s.Package.ToLink<ISkyrimMajorRecordGetter>() })] });
        var made = obPackages.Where(kv => kv.Value is not null).Take(128).ToList();
        entry.Properties.Add(new ScriptObjectListProperty { Name = "Packages", Objects = [.. made.Select(kv => new ScriptObjectProperty { Object = kv.Value!.ToLink<ISkyrimMajorRecordGetter>() })] });
        entry.Properties.Add(new ScriptIntListProperty { Name = "PackageTypes", Data = [.. made.Select(kv => obPackageTypes.GetValueOrDefault(kv.Key, 6))] });
        AddBirthsigns(entry);
        // Oblivion's attributes and skills (ObStats): where the player's live, the nearest Skyrim value for others.
        entry.Properties.Add(new ScriptStringListProperty { Name = "PlayerStatAv", Data = [.. PlayerStatHome.Select(h => h.Av)] });
        entry.Properties.Add(new ScriptStringListProperty { Name = "PlayerStatId", Data = [.. PlayerStatHome.Select(h => h.Id)] });
        entry.Properties.Add(new ScriptStringListProperty { Name = "SkyrimStatAv", Data = [.. Enumerable.Repeat("", 8).Concat(StatSkills.Select(ObPapyrus.SkyrimAv)).Concat(SkyrimAiAv)] });
        quest.VirtualMachineAdapter = new QuestAdapter
        {
            Scripts = { entry },
            Aliases =
            [
                new QuestFragmentAlias
                {
                    Property = new ScriptObjectProperty { Object = quest.ToLink<ISkyrimMajorRecordGetter>(), Alias = 0 },
                    Scripts = [new ScriptEntry { Name = "TMObPlayer", Properties = [Obj("ObState", quest.FormKey), Obj("PlayerRef", player)] }],
                },
            ],
        };
        return quest;
    }

    /// <summary>The animation groups of every converted model that has them, with their lengths and whether they loop
    /// (TMObState.PlayGroup, IsAnimPlaying): "model|group" keys, by the model path a record carries. Once every model is
    /// made, the world's moving doors with them.</summary>
    public int AnimationGroups { get; private set; }

    void FinishAnimationLengths()
    {
        if (stateQuest?.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == "TMObState") is not { } entry) return;
        var groups = meshes.Animated.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .SelectMany(kv => kv.Value.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Select(g => (Key: (kv.Key + "|" + g.Key).ToLowerInvariant(), g.First().Length, g.First().Loop)))
            .ToList();
        entry.Properties.Add(new ScriptStringListProperty { Name = "AnimKeys", Data = [.. groups.Select(g => g.Key)] });
        entry.Properties.Add(new ScriptFloatListProperty { Name = "AnimLengths", Data = [.. groups.Select(g => g.Length)] });
        entry.Properties.Add(new ScriptBoolListProperty { Name = "AnimLoops", Data = [.. groups.Select(g => g.Loop)] });
        AnimationGroups = groups.Count;

        // The sounds the groups play (Oblivion's "Sound: X" text keys): each with its group's key, its second and
        // the sound as Skyrim's. A sound Oblivion's data hasn't is said so.
        var soundsByName = records.Values.OfType<Ob.ISoundGetter>().Where(s => s.EditorID is { Length: > 0 })
            .GroupBy(s => s.EditorID!, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().FormKey, StringComparer.OrdinalIgnoreCase);
        var cueKeys = new List<string>(); var cueTimes = new List<float>(); var cueSounds = new List<ScriptObjectProperty>();
        foreach (var (model, cues) in meshes.AnimatedCues.OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase))
            foreach (var (group, time, sound) in cues.OrderBy(c => c.Group, StringComparer.OrdinalIgnoreCase).ThenBy(c => c.Time))
            {
                if (!soundsByName.TryGetValue(sound, out var soundKey)) { Problems.Add($"animation sound {sound} ({model}, {group}): Oblivion has no such sound"); continue; }
                if (ObSound(soundKey) is not { } marker) continue;
                cueKeys.Add((model + "|" + group).ToLowerInvariant());
                cueTimes.Add(time);
                cueSounds.Add(new ScriptObjectProperty { Object = marker.ToLink<ISkyrimMajorRecordGetter>() });
            }
        entry.Properties.Add(new ScriptStringListProperty { Name = "AnimCueKeys", Data = [.. cueKeys] });
        entry.Properties.Add(new ScriptFloatListProperty { Name = "AnimCueTimes", Data = [.. cueTimes] });
        entry.Properties.Add(new ScriptObjectListProperty { Name = "AnimCueSounds", Objects = [.. cueSounds] });
        AnimationSounds = cueKeys.Count;
    }

    /// <summary>Sounds played from within animation groups (Oblivion's text keys), carried as TMObState's cues.</summary>
    public int AnimationSounds { get; private set; }

    /// <summary>Responses of the topics the scripts time with Say's value, those measured in every voice from Oblivion's
    /// recordings, and the seconds a character Oblivion's recorded lines are spoken at (a line it didn't record, by its
    /// words).</summary>
    public int TimedResponses { get; private set; }
    public int TimedRecorded { get; private set; }
    /// <summary>Of the timed responses' voices (16 each), those Oblivion recorded.</summary>
    public int TimedVoices { get; private set; }
    public double SecondsPerCharacter { get; private set; }

    /// <summary>
    /// Every response's TMObLine (or result script, which extends it) told of TMObState, which keeps who is talking; the
    /// responses of the topics the scripts take Say's value of given their length in each voice (TMObState.VoiceTypes):
    /// the sum of their lines' recordings, the ones ObVoices gives that voice; a line Oblivion recorded in no voice of
    /// that sex, by its characters at the rate Oblivion's recorded lines go (negative: an estimate, which the line's
    /// first saying replaces).
    /// </summary>
    void FinishLines()
    {
        if (stateQuest is null) return;
        var state = stateQuest.ToLink<ISkyrimMajorRecordGetter>();
        var timed = translations.Values.Concat(resultTranslations.Values).Concat(stageTranslations.Values)
            .SelectMany(o => o.TimedTopics).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var sources = VoiceMap.ToDictionary(v => v.Info);
        var voices = ObVoices.VoiceTypes;
        Dictionary<(string Race, string Sex, uint Id, int Line), string>? recordings = null;
        var measured = new List<(ScriptEntry Entry, double?[] Seconds, int Characters)>();
        var lengths = new Dictionary<string, double?>(StringComparer.OrdinalIgnoreCase);
        double? Length(string mp3) => lengths.TryGetValue(mp3, out var hit) ? hit : lengths[mp3] = data.Read(mp3) is { } bytes ? ObVoices.Mp3Seconds(bytes) : null;
        foreach (var (r, topic) in responseTopics)
        {
            if (r.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == LineScript || s.Name == PersuadeScript || s.Name.StartsWith("OB_R_", StringComparison.OrdinalIgnoreCase)) is not { } entry) continue;
            entry.Properties.Add(new ScriptObjectProperty { Name = "ObState", Object = state });
            if (!timed.Contains(topic) || !sources.TryGetValue(r.FormKey.ID, out var source)) continue;
            recordings ??= ObVoices.Recordings(data);
            var seconds = new double?[voices.Count];
            for (int v = 0; v < voices.Count; v++)
            {
                double total = 0;
                foreach (var (number, _) in source.Lines)
                {
                    if (ObVoices.Recording(recordings, voices[v], source.OriginId, number) is not { } mp3 || Length(mp3) is not { } s) { total = double.NaN; break; }
                    total += s;
                }
                seconds[v] = double.IsNaN(total) ? null : total;
            }
            measured.Add((entry, seconds, source.Lines.Sum(l => l.Text.Length)));
        }
        // The rate: every voice's recording of every line measured, against its characters.
        var known = measured.SelectMany(m => m.Seconds.OfType<double>().Select(s => (s, m.Characters))).Where(x => x.Characters > 0).ToList();
        SecondsPerCharacter = known.Count > 0 ? known.Sum(x => x.s) / known.Sum(x => x.Characters) : 0;
        foreach (var (entry, seconds, characters) in measured)
        {
            entry.Properties.Add(new ScriptFloatListProperty { Name = "Lengths", Data = [.. seconds.Select(s => s is { } v ? (float)v : -(float)(characters * SecondsPerCharacter))] });
            TimedResponses++;
            if (seconds.Any(s => s is not null)) TimedRecorded++;
            TimedVoices += seconds.Count(s => s is not null);
        }
        if (stateQuest.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == "TMObState") is { } st)
        {
            // The listener the conversations' "Run on Target" conditions ask of (ObConversations): a script's Say fills
            // it with the one it speaks to.
            if (obDialogueQuest?.Aliases.FirstOrDefault(a => a.Name == "OBListener") is { } listener)
                st.Properties.Add(new ScriptObjectProperty { Name = "Listener", Object = obDialogueQuest.ToLink<ISkyrimMajorRecordGetter>(), Alias = (short)listener.ID });
            st.Properties.Add(new ScriptObjectListProperty { Name = "VoiceTypes", Objects = [.. voices.Select(v => new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(Vanilla.Get<IVoiceTypeGetter>(v)?.FormKey ?? FormKey.Null) })] });
        }
    }

    /// <summary>Oblivion's quest items: those its records flag (0x400, the Construction Set's Quest Item), which the
    /// player can't drop, store, sell or give away (TMObPlayer); SetQuestObject adds and takes them off at run time.</summary>
    public int QuestItems { get; private set; }

    void FinishQuestItems()
    {
        if (stateQuest?.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == "TMObState") is not { } entry) return;
        var list = new FormList(mod) { EditorID = "TMObQuestItems" };
        foreach (var (key, item) in items)
            if (item is not null && records.GetValueOrDefault(key) is { } rec && (rec.MajorRecordFlagsRaw & 0x400) != 0)
                list.Items.Add(item.ToLink<ISkyrimMajorRecordGetter>());
        mod.FormLists.Add(list);
        var cleared = new FormList(mod) { EditorID = "TMObQuestItemsCleared" };
        mod.FormLists.Add(cleared);
        entry.Properties.Add(new ScriptObjectProperty { Name = "QuestItems", Object = list.ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(new ScriptObjectProperty { Name = "QuestItemsCleared", Object = cleared.ToLink<ISkyrimMajorRecordGetter>() });
        QuestItems = list.Items.Count;
    }

    /// <summary>What the conditions read that only scripts know (ObDialogue: the stats' globals and factions, the misc
    /// stats, the birthsign, the evil factions), told to TMObState, which keeps them.</summary>
    void FinishConditionMirrors()
    {
        if (stateQuest?.VirtualMachineAdapter?.Scripts.FirstOrDefault(s => s.Name == "TMObState") is not { } entry) return;
        var stats = playerStatGlobals.Keys.Union(statFactions.Keys).Order().ToList();
        entry.Properties.Add(new ScriptIntListProperty { Name = "StatMirrors", Data = [.. stats] });
        entry.Properties.Add(new ScriptObjectListProperty { Name = "StatGlobals", Objects = [.. stats.Select(s => new ScriptObjectProperty { Object = PlayerStatGlobal(s).ToLink<ISkyrimMajorRecordGetter>() })] });
        entry.Properties.Add(new ScriptObjectListProperty { Name = "StatFactions", Objects = [.. stats.Select(s => new ScriptObjectProperty { Object = StatFaction(s).ToLink<ISkyrimMajorRecordGetter>() })] });
        var misc = miscStatGlobals.Keys.Order().ToList();
        entry.Properties.Add(new ScriptIntListProperty { Name = "MiscMirrors", Data = [.. misc] });
        entry.Properties.Add(new ScriptObjectListProperty { Name = "MiscGlobals", Objects = [.. misc.Select(s => new ScriptObjectProperty { Object = MiscStatGlobal(s).ToLink<ISkyrimMajorRecordGetter>() })] });
        entry.Properties.Add(new ScriptObjectProperty { Name = "BirthsignGlobal", Object = ScriptGlobal("TMOB_PCBirthsign").ToLink<ISkyrimMajorRecordGetter>() });
        var evil = new FormList(mod) { EditorID = "TMObEvilFactions" };
        var all = new FormList(mod) { EditorID = "TMObFactions" };
        foreach (var (key, faction) in obFactions)
        {
            if (faction is null) continue;
            all.Items.Add(faction.ToLink<ISkyrimMajorRecordGetter>());
            if (records.GetValueOrDefault(key) is Ob.IFactionGetter f && f.Flags is { } fl && fl.HasFlag(Ob.Faction.FactionFlag.Evil)) evil.Items.Add(faction.ToLink<ISkyrimMajorRecordGetter>());
        }
        mod.FormLists.Add(evil);
        mod.FormLists.Add(all);
        entry.Properties.Add(new ScriptObjectProperty { Name = "EvilFactions", Object = evil.ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(new ScriptObjectProperty { Name = "OblivionFactions", Object = all.ToLink<ISkyrimMajorRecordGetter>() });
        entry.Properties.Add(new ScriptObjectProperty { Name = "EvilFaction", Object = EvilFaction().ToLink<ISkyrimMajorRecordGetter>() });
        MirroredStats = stats.Count;
        EvilFactionCount = evil.Items.Count;
    }

    public int MirroredStats { get; private set; }
    public int EvilFactionCount { get; private set; }

    /// <summary>Script packages and how many times the scripts add each (for the aliases that run them).</summary>
    readonly Dictionary<FormKey, int> scriptPackageUses = new();

    /// <summary>The birthsign menu (two pages of buttons past nine signs) and each sign's spells.</summary>
    void AddBirthsigns(ScriptEntry entry)
    {
        var signs = scriptWorld!.Birthsigns.Select(b => scriptWorld.KeyOf(b) is { } k ? records.GetValueOrDefault(k) as Ob.IBirthsignGetter : null).ToList();
        if (signs.Count == 0 || signs.Any(s => s is null)) return;
        Message Page(string edid, IEnumerable<string> buttons)
        {
            var m = new Message(mod) { EditorID = edid, Description = "Under which sign were you born?", Flags = Message.Flag.MessageBox, MenuButtons = [] };
            foreach (var b in buttons) m.MenuButtons.Add(new MessageButton { Text = b });
            mod.Messages.Add(m);
            return m;
        }
        var names = signs.Select(s => s!.Name ?? s.EditorID ?? "?").ToList();
        if (names.Count <= 10)
            entry.Properties.Add(new ScriptObjectProperty { Name = "BirthsignPage1", Object = Page("TMObBirthsigns", names).ToLink<ISkyrimMajorRecordGetter>() });
        else
        {
            entry.Properties.Add(new ScriptObjectProperty { Name = "BirthsignPage1", Object = Page("TMObBirthsigns1", names.Take(9).Append("More...")).ToLink<ISkyrimMajorRecordGetter>() });
            entry.Properties.Add(new ScriptObjectProperty { Name = "BirthsignPage2", Object = Page("TMObBirthsigns2", names.Skip(9).Take(9).Append("Back")).ToLink<ISkyrimMajorRecordGetter>() });
        }
        var lists = new ScriptObjectListProperty { Name = "BirthsignSpells" };
        foreach (var s in signs)
        {
            var list = new FormList(mod) { EditorID = "TMObBirthsign" + Sanitize(s!.EditorID ?? "") };
            foreach (var spell in s.Spells)
                if (ObSpell(spell.FormKey) is { } converted) list.Items.Add(converted.ToLink<ISkyrimMajorRecordGetter>());
            mod.FormLists.Add(list);
            lists.Objects.Add(new ScriptObjectProperty { Object = list.ToLink<ISkyrimMajorRecordGetter>() });
        }
        entry.Properties.Add(lists);
    }
}
