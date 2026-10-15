using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using TamrielMerge.Skyrim;

namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's people and creatures. Redguard's actors are scripted objects without a model of their own (MPOB type 1);
/// their SOUP script names say who they are. Named characters become unique NPCs of their race and sex (UESP's
/// Redguard character pages) with a vanilla FaceGen look of that race (VanillaActors.Faces), named as in the game;
/// guards are the Empire's soldiers on Stros M'Kai, thugs the island's bandits, the Restless League's pirates sailors;
/// creatures stand in as Skyrim's nearest kind. A character appearing in several scenes (Richton in the palace, the
/// catacombs and the palace grounds) is placed once, where they live; the other appearances are scripted moments.
/// </summary>
public sealed partial class RgPluginBuilder
{
    sealed record Person(string Name, string Race, bool Female, bool Essential = true);

    /// <summary>Named characters by script name (the scene where each lives is the one listing that script).</summary>
    static readonly Dictionary<string, Person> People = new(StringComparer.OrdinalIgnoreCase)
    {
        ["AVIK"] = new("Avik", "Redguard", false), ["BASIL"] = new("Basil", "Redguard", false), ["BRENNAN"] = new("Brennan", "Redguard", false),
        ["COYLE"] = new("Coyle", "Redguard", false), ["CRENDAL"] = new("Crendal", "Breton", false), ["DRAM"] = new("Dram", "DarkElf", false),
        ["DREEKIUS"] = new("Dreekius", "Argonian", false), ["ERASMO"] = new("Erasmo", "WoodElf", false), ["FALICIA"] = new("Falicia", "WoodElf", true),
        ["FAVIS"] = new("Favis", "Redguard", false), ["GERRICK"] = new("Gerrick", "HighElf", false), ["ISZARA"] = new("Iszara", "Redguard", true),
        ["JAGANVIR"] = new("Jaganvir", "Redguard", false), ["JFFER"] = new("J'ffer", "Khajiit", false), ["JOTO"] = new("Joto", "Khajiit", false),
        ["KOTARO"] = new("Kotaro", "Redguard", false), ["KRISAND"] = new("Krisandra", "Redguard", true), ["LAKENE"] = new("Lakene", "Redguard", false),
        ["MAIKO"] = new("Maiko K'Elmar", "Redguard", false), ["MARIAH"] = new("Mariah", "Redguard", true), ["NGASTA"] = new("N'Gasta", "DarkElf", false, false),
        ["NIDAL"] = new("Nidal", "Imperial", false), ["PRISONER"] = new("Prisoner", "Redguard", false, false), ["PRNELL"] = new("Prnell", "Redguard", false),
        ["RICHTON"] = new("Amiel Richton", "Imperial", false), ["ROLLO"] = new("Rollo", "Redguard", false), ["SABAN"] = new("Saban", "Redguard", true),
        ["SIONA"] = new("Siona", "Redguard", true), ["TOBIAS"] = new("Tobias", "Nord", false), ["TRITHICK"] = new("Trithik", "Redguard", false),
        ["URIK"] = new("Urik", "Nord", false), ["VANDAR"] = new("Vander", "Redguard", false), ["YAELI"] = new("Yaeli", "Redguard", true),
        // Yaeli on the island (before she hides out), and Clavicus Vile in the child's form he takes in his realm.
        ["YAELIISL"] = new("Yaeli", "Redguard", true), ["VILEKID"] = new("Clavicus Vile", "BretonRaceChild", false),
    };

    /// <summary>Generic people and creatures by script-name prefix: display name and vanilla template (EditorID prefix).</summary>
    static readonly (string Prefix, string Name, string Template)[] Kinds =
    [
        ("GUARD", "Imperial Guard", "EncGuardImperialM05MaleGuard"), ("THUG", "Island Thug", "EncBandit02Melee1HRedguardM"),
        ("PIRATE", "League Pirate", "EncSailorRedguardM"), ("GOBLIN", "Goblin", "EncFalmer01Melee"), ("OGRE", "Ogre", "EncGiant01"),
        ("TROLL", "Troll", "EncTroll"), ("SKEL", "Skeleton", "EncSkeleton01Melee"), ("ZOMBIE", "Zombie", "EncDraugr01Melee1H"),
        ("RAT", "Rat", "EncSkeever"), ("GOLEM", "Dwarven Golem", "EncDwarvenCenturion01"), ("DRBOT", "Dwarven Automaton", "EncDwarvenSphere01"),
        ("VILEDOG", "Barbas", "EncDog"), ("VR_GARD", "Guardian", "EncDremoraMelee01"),
    ];

    /// <summary>Hostile kinds (Redguard's thugs, monsters and undead attack on sight).</summary>
    static readonly HashSet<string> Hostile = ["THUG", "GOBLIN", "OGRE", "TROLL", "SKEL", "ZOMBIE", "RAT", "GOLEM", "DRBOT", "VR_GARD"];

    readonly Dictionary<string, Npc?> actors = new(StringComparer.OrdinalIgnoreCase);
    public int ActorCount { get; private set; }

    Npc? PersonNpc(string script, Person p)
    {
        var race = vanilla.Get<IRaceGetter>(p.Race.Contains("Race") ? p.Race : p.Race + "Race");
        if (race is null) { Problems.Add($"{p.Name}: race {p.Race} not found"); return null; }
        var pool = VanillaActors.WithRaceVoice(vanilla.Skyrim, vanilla.Faces(race.FormKey, p.Female), race.FormKey, p.Female);
        if (pool.Count == 0) pool = vanilla.Faces(race.FormKey, p.Female);
        if (pool.Count == 0) { Problems.Add($"{p.Name}: no vanilla face for {p.Race}"); return null; }
        var look = pool[VanillaActors.StableHash(p.Name) % pool.Count];
        var npc = mod.Npcs.DuplicateInAsNewRecord(look);
        npc.EditorID = "RG" + Sanitize(script);
        npc.Name = p.Name;
        npc.ShortName = null;
        npc.VirtualMachineAdapter = null;
        npc.Factions.Clear();
        // The vanilla look's crime faction too: each game's people answer to their own factions, never Skyrim's.
        npc.CrimeFaction.Clear();
        npc.DeathItem.Clear();
        npc.Packages.Clear();
        npc.Keywords = null;
        npc.Perks = null;
        npc.Template = new FormLinkNullable<INpcSpawnGetter>(look.FormKey);
        npc.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        npc.Configuration.Flags = NpcConfiguration.Flag.Unique | (p.Female ? NpcConfiguration.Flag.Female : 0) | (p.Essential ? NpcConfiguration.Flag.Essential : 0);
        npc.Configuration.Level = new NpcLevel { Level = 10 };
        npc.Packages.Add(new FormLink<IPackageGetter>(Sk(0x01B217)));   // DefaultSandboxEditorLocation512
        if (npc.AIData is { } ai) ai.Aggression = Aggression.Unaggressive;
        return npc;
    }

    Npc? KindNpc(string script, (string Prefix, string Name, string Template) kind)
    {
        var template = vanilla.Get<INpcGetter>(kind.Template);
        if (template is null) { Problems.Add($"{script}: template {kind.Template} not found"); return null; }
        var npc = mod.Npcs.DuplicateInAsNewRecord(template);
        npc.EditorID = "RG" + Sanitize(kind.Prefix) + "Kind";
        npc.Name = kind.Name;
        if (!Hostile.Contains(kind.Prefix) && npc.AIData is { } ai) ai.Aggression = Aggression.Unaggressive;
        return npc;
    }

    /// <summary>The actor for a script name: a named character (only in their own scene's main script name), a
    /// generic kind, or null for what isn't an actor in Skyrim (seagulls, scripted props, cutscene doubles).</summary>
    Npc? ActorFor(string script)
    {
        if (actors.TryGetValue(script, out var hit)) return hit;
        Npc? npc = null;
        if (script.Equals("PARROT", StringComparison.OrdinalIgnoreCase)) npc = ParrotNpc();
        else if (People.TryGetValue(script, out var person)) npc = PersonNpc(script, person);
        else if (Kinds.FirstOrDefault(k => script.StartsWith(k.Prefix, StringComparison.OrdinalIgnoreCase)) is { Prefix: not null } kind)
        {
            // One record per kind, shared by every script of that kind.
            npc = actors.GetValueOrDefault(kind.Prefix) ?? KindNpc(script, kind);
            actors[kind.Prefix] = npc;
        }
        return actors[script] = npc;
    }

    /// <summary>
    /// Gerrick's talking parrot: Skyrim has no birds that act, so a chicken's body under the name, on a copy of the
    /// chicken's race that may be talked to (and an old man's voice type for packing its own recordings).
    /// </summary>
    Npc? ParrotNpc()
    {
        var template = vanilla.Get<INpcGetter>("EncChicken");
        if (template is null) { Problems.Add("parrot: EncChicken not found"); return null; }
        var npc = mod.Npcs.DuplicateInAsNewRecord(template);
        npc.EditorID = "RGPARROT";
        npc.Name = "Parrot";
        if (template.Race.FormKeyNullable is { } raceKey && vanilla.Skyrim.Races.FirstOrDefault(r => r.FormKey == raceKey) is { } race)
        {
            var talking = mod.Races.DuplicateInAsNewRecord(race);
            talking.EditorID = "RGParrotRace";
            talking.Flags |= Race.Flag.AllowPcDialog;
            npc.Race.SetTo(talking);
        }
        if (vanilla.Get<IVoiceTypeGetter>("MaleOldKindly") is { } voice) npc.Voice.SetTo(voice);
        npc.Configuration.Flags |= NpcConfiguration.Flag.Unique | NpcConfiguration.Flag.Essential;
        npc.Packages.Clear();
        npc.Packages.Add(new FormLink<IPackageGetter>(Sk(0x01B217)));
        if (npc.AIData is { } ai) ai.Aggression = Aggression.Unaggressive;
        return npc;
    }

    readonly HashSet<string> placedPeople = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The first actor placed for each scene object script name (upper case).</summary>
    readonly Dictionary<(string Scene, string Script), PlacedNpc> placedActors = [];

    /// <summary>Redguard's ordinary creatures for procedural creatures (the ogre, golem, guardians and Barbas are its
    /// bosses and set pieces).</summary>
    static readonly string[] Ordinary = ["GOBLIN", "TROLL", "SKEL", "ZOMBIE", "RAT", "DRBOT"];
    readonly List<(PlacedNpc Ref, Scene Scene, string Kind)> creatureSpots = [];

    /// <summary>
    /// Redguard's part of procedural creatures (<see cref="CreatureCatalog"/>): its ordinary creatures and where it
    /// places them. Redguard brings no one back (its zombies get up and its trolls regenerate, but dead stays dead), so
    /// nothing respawns.
    /// </summary>
    public void WriteCreatureCatalog(string pluginPath)
    {
        var catalog = new CreatureCatalog { Game = "Redguard", Plugin = PluginName };
        foreach (var kind in Ordinary)
            if (actors.GetValueOrDefault(kind) is { } npc)
                catalog.Creatures.Add(new(CreatureCatalog.Key(npc.FormKey), npc.Name?.String ?? kind, npc.Configuration.Level is NpcLevel l ? l.Level : 5, CreatureCatalog.HabitatsOf(npc.Name?.String ?? kind)));
        foreach (var (placed, scene, kind) in creatureSpots)
        {
            var p = placed.Placement!.Position;
            bool outside = scene.World is not null;
            string cellKey = outside ? "" : mod.Cells.Records.SelectMany(b => b.SubBlocks).SelectMany(s => s.Cells).FirstOrDefault(c => c.Temporary.Contains(placed)) is { } c ? CreatureCatalog.Key(c.FormKey) : "";
            string name = actors.GetValueOrDefault(kind)?.Name?.String ?? kind;
            catalog.Spots.Add(new(CreatureCatalog.Key(placed.FormKey), cellKey, outside ? CreatureCatalog.Key(scene.World!.FormKey) : "", p.X, p.Y, p.Z, CreatureCatalog.HabitatsOf(name)[0], 5, -1));
        }
        catalog.Save(pluginPath);
    }

    /// <summary>Where characters seen in several scenes live (the others are the story's scripted moments).</summary>
    static readonly Dictionary<string, string> Homes = new(StringComparer.OrdinalIgnoreCase) { ["RICHTON"] = "PALACE", ["DRAM"] = "PALACE" };

    void PlaceActors(Scene scene)
    {
        for (int i = 0; i < scene.Data.Objects.Count; i++)
        {
            var o = scene.Data.Objects[i];
            if (scene.Data.ObjectTypes[i] != 1 || o.Model.Length > 0 || o.Script.Length == 0) continue;
            // A named character once; their first scene in WORLD.INI order is where they live.
            if (Homes.TryGetValue(o.Script, out var home) && !home.Equals(scene.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (People.ContainsKey(o.Script) && !placedPeople.Add(o.Script)) continue;
            if (ActorFor(o.Script) is not { } npc) continue;
            var at = At(scene, o.Position);
            float yaw = RgSpace.Euler(o.Rotation).Z;
            var cell = CellFor(scene, at);
            var placed = new PlacedNpc(mod)
            {
                Base = new FormLinkNullable<INpcGetter>(npc.FormKey),
                // Redguard places actors by their middle; Skyrim by their feet (they settle onto the floor).
                Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z - 40), Rotation = new P3Float(0, 0, yaw) },
            };
            cell.Temporary.Add(placed);
            placedActors.TryAdd((scene.Name, o.Script.ToUpperInvariant()), placed);
            if (ScriptOf(scene, o.Script) is not null)
            {
                if (namedObjects.Contains((scene.Name, o.Script)))
                {
                    cell.Temporary.Remove(placed);
                    placed.SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400;
                    PersistentCellFor(scene).Persistent.Add(placed);
                }
                carriers[(scene.Name, i)] = (placed, ObjectReferenceKind.Actor);
            }
            if (Ordinary.FirstOrDefault(k => o.Script.StartsWith(k, StringComparison.OrdinalIgnoreCase)) is { } kind)
                creatureSpots.Add((placed, scene, kind));
            ActorCount++;
        }
    }
}
