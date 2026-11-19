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

    /// <summary>Hostile kinds (Redguard's monsters and undead attack on sight). Not the thugs: their own scripts start
    /// their fights (rg-script --dump, 2026-11-11: 9 of the 13 THUG programs call beginCombat() on their own conditions;
    /// the tavern's three, THUGTV1-3, drink and talk until StartTavernBrawl is set), and in game a thug made hostile on
    /// sight attacked the player on walking into the Draggin Tale.</summary>
    static readonly HashSet<string> Hostile = ["GOBLIN", "OGRE", "TROLL", "SKEL", "ZOMBIE", "RAT", "GOLEM", "DRBOT", "VR_GARD"];

    readonly Dictionary<string, Npc?> actors = new(StringComparer.OrdinalIgnoreCase);
    public int ActorCount { get; private set; }
    /// <summary>Animals placed as their own models (AnimalModels: the seagulls).</summary>
    public int AnimalsPlaced { get; private set; }

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
        // Dressed as Stros M'Kai's people, not as the borrowed face's owner (2026-11-10, in game: Brennan came out in an
        // iron helmet and studded armour): Hammerfell's clothes on its Redguards, a sailor's on the captain; the others
        // keep the townsperson's clothes their look came with.
        npc.SleepingOutfit.Clear();
        if (script.EndsWith("BRENNAN", StringComparison.OrdinalIgnoreCase)) npc.DefaultOutfit.SetTo(Sk(0x045698));        // SailorClothesRND
        else if (p.Race == "Redguard") npc.DefaultOutfit.SetTo(Sk(VanillaActors.StableHash(p.Name) % 2 == 0 ? 0x06A15Cu : 0x06A15Bu));   // RedguardClothesOutfitNoHat / 01
        npc.Items = null;
        return npc;
    }

    Npc? KindNpc(string script, (string Prefix, string Name, string Template) kind)
    {
        var template = vanilla.Get<INpcGetter>(kind.Template);
        if (template is null) { Problems.Add($"{script}: template {kind.Template} not found"); return null; }
        var npc = mod.Npcs.DuplicateInAsNewRecord(template);
        npc.EditorID = "RG" + Sanitize(kind.Prefix) + "Kind";
        npc.Name = kind.Name;
        // Not the vanilla template's law (an Imperial soldier's crime faction): Stros M'Kai has none (RgLaw).
        WithoutSkyrimLaw(npc, guard: kind.Prefix == "GUARD");
        bool peaceful = !Hostile.Contains(kind.Prefix);
        // A person's face is the vanilla actor's own (a copy has none: black-faced guards, thugs and pirates).
        if (vanilla.Skyrim.Races.FirstOrDefault(r => r.FormKey == template.Race.FormKey) is { } race && race.Flags.HasFlag(Race.Flag.Playable))
            VanillaActors.Lookalike(npc, template, ownAI: peaceful);
        // Its name and level its own, and its temper when it is peaceful, whatever the vanilla actor took from a template
        // of its own (in game 2026-11-11: an island thug named "Bandit Outlaw", the bandit template's base data).
        npc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.BaseData;
        if (peaceful) npc.Configuration.TemplateFlags &= ~NpcConfiguration.TemplateFlag.AIData;
        if (peaceful)
        {
            npc.AIData ??= new AIData();
            npc.AIData.Aggression = Aggression.Unaggressive;
        }
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
    /// Gerrick's talking parrot: Skyrim has no birds that act, so a chicken under the name, on a copy of the chicken's
    /// race that may be talked to (and an old man's voice type for packing its own recordings), its body unseen and
    /// Redguard's own parrot model on the perch where it stands (PlaceParrotModel).
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

    /// <summary>
    /// Characters that are animals Skyrim has no actor for, which Redguard animates from its own models: each as a scripted
    /// prop of that model (its first frame), moving as its script flies it. Before 2026-11-04 a seagull had no carrier but
    /// an invisible marker: unseen, and its cries (Sound, played at it) and flights failed for want of 3D (user: "fix
    /// redguards seagull sounds"; they were silent).
    /// </summary>
    static readonly Dictionary<string, string> AnimalModels = new(StringComparer.OrdinalIgnoreCase) { ["SEAGULL"] = "SEAGA001" };

    /// <summary>Actors set on top of what they stood inside (TopUnder).</summary>
    public int ActorsRaised { get; private set; }

    /// <summary>
    /// The top of a placed model under a point when it is from 12 to 50 units above it (the point: the actor's true feet): what an actor reckoned to stand
    /// there is standing in (2026-11-11, in game and the user's "i saw a npc clipping": the Draggin Tale's thug THUGTV1,
    /// seated in Redguard, stood to his thighs in the hearth's step). Null when nothing is.
    /// </summary>
    float? TopUnder(Scene scene, Vector3 feet)
    {
        float? best = null;
        var q = new Vector2(feet.X, feet.Y);
        foreach (var o in scene.Data.Statics.Concat(scene.Data.Objects.Where((o, i) => scene.Data.ObjectTypes[i] == 1 && o.Model.Length > 0)))
        {
            string name = o.Model.Split('.')[0].Trim().ToUpperInvariant();
            if (name.StartsWith("BOAT") || Model(scene, name) is not { } model || model.Vertices.Count == 0) continue;
            var origin = At(scene, o.Position);
            origin.Z += SettleScenery(scene, o.Model, o.Position, o.Rotation);
            var local = model.Vertices.Select(v => RgSpace.Dir(Vector3.Transform(v, o.Rotation))).ToList();
            var mn = local.Aggregate(Vector3.Min) + origin; var mx = local.Aggregate(Vector3.Max) + origin;
            if (q.X < mn.X || q.X > mx.X || q.Y < mn.Y || q.Y > mx.Y || mx.Z < feet.Z + 12 || mn.Z > feet.Z + 50) continue;
            foreach (var f in model.Faces)
                for (int t = 1; t + 1 < f.Vertices.Length; t++)
                {
                    Vector3 a = origin + local[f.Vertices[0]], b = origin + local[f.Vertices[t]], c = origin + local[f.Vertices[t + 1]];
                    var n = Vector3.Cross(b - a, c - a);
                    if (n.Length() < 1e-3f || MathF.Abs(n.Z) / n.Length() < 0.5f) continue;
                    float d = (b.Y - c.Y) * (a.X - c.X) + (c.X - b.X) * (a.Y - c.Y);
                    if (MathF.Abs(d) < 1e-6f) continue;
                    float u = ((b.Y - c.Y) * (q.X - c.X) + (c.X - b.X) * (q.Y - c.Y)) / d;
                    float v = ((c.Y - a.Y) * (q.X - c.X) + (a.X - c.X) * (q.Y - c.Y)) / d;
                    if (u < 0 || v < 0 || u + v > 1) continue;
                    float z = u * a.Z + v * b.Z + (1 - u - v) * c.Z;
                    if (z >= feet.Z + 12 && z <= feet.Z + 50 && (best is null || z > best)) best = z;
                }
        }
        return best;
    }

    void PlaceActors(Scene scene)
    {
        for (int i = 0; i < scene.Data.Objects.Count; i++)
        {
            var o = scene.Data.Objects[i];
            if (scene.Data.ObjectTypes[i] != 1 || o.Model.Length > 0 || o.Script.Length == 0) continue;
            // A named character once; their first scene in WORLD.INI order is where they live.
            if (Homes.TryGetValue(o.Script, out var home) && !home.Equals(scene.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (People.ContainsKey(o.Script) && !placedPeople.Add(o.Script)) continue;
            // By the script's name or its start (the island's gulls are SEAGULL, SEAGULL2 and SEAGULL3: 27, 20 and 18).
            if (AnimalModels.FirstOrDefault(k => o.Script.StartsWith(k.Key, StringComparison.OrdinalIgnoreCase)).Value is { } animal)
            {
                if (ScriptOf(scene, o.Script) is not null && PlaceScriptedProp(scene, i, o with { Model = animal + ".3DC" }) is not null) AnimalsPlaced++;
                else Problems.Add($"{o.Script} in {scene.Name}: its model {animal} not placed");
                continue;
            }
            if (ActorFor(o.Script) is not { } npc) continue;
            var at = At(scene, o.Position);
            float yaw = RgSpace.Euler(o.Rotation).Z;
            var cell = CellFor(scene, at);
            // Redguard places actors by their middle; Skyrim by their feet (they settle onto the floor). Over a ship (the
            // tool's, in place of Redguard's boat, its deck higher than the boat's) on its deck, as the player's arrival
            // (2026-11-06: Brennan stood 100 units under his ship's deck, in the hull, on no navmesh).
            var feet = OnShip(scene, new Vector3(at.X, at.Y, at.Z - 40), null);
            // On what is under them when their legs would be inside it: a top from 12 to 50 above their true feet (66
            // under the middle: Cyrus's model CYRSA001 is 128 tall about its origin, rg-model).
            if (TopUnder(scene, new Vector3(feet.X, feet.Y, at.Z - 66)) is { } top) { feet.Z = top; ActorsRaised++; }
            var placed = new PlacedNpc(mod)
            {
                Base = new FormLinkNullable<INpcGetter>(npc.FormKey),
                Placement = new Placement { Position = new P3Float(feet.X, feet.Y, feet.Z), Rotation = new P3Float(0, 0, yaw) },
            };
            cell.Temporary.Add(placed);
            placedActors.TryAdd((scene.Name, o.Script.ToUpperInvariant()), placed);
            if (o.Script.Equals("PARROT", StringComparison.OrdinalIgnoreCase)) PlaceParrotModel(scene, o, npc);
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
            // The Empire's guards, who mind drawn weapons (RgLaw).
            if (o.Script.StartsWith("GUARD", StringComparison.OrdinalIgnoreCase)) guardSpots.Add((placed, scene));
            ActorCount++;
        }
    }
}
