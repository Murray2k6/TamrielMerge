using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// The records of creatures with their own skeleton, as a light plugin per game ("Daggerfall Creatures.esp" over
/// Daggerfall.esm, "Morrowind Creatures.esp" over Morrowind.esm, ...), so each needs only its own game (user, 2026-10-11:
/// "are they set for when a user does not have a certain game": one plugin over every game's would stop Skyrim loading
/// for anyone without one of them) and the stand-in bodies come back when it is switched off: per creature a race copied
/// from its behaviour template's (whose attacks name the template graph's events) with the creature's skeleton and
/// behaviour project, a skin (armour + addon wearing its body), and overrides of the game plugin's actors for it
/// (race and skin switched).
/// </summary>
public sealed class CreatureRecords
{
    /// <summary>The single plugin over every game's that earlier builds made (replaced by one per game).</summary>
    public const string OldPluginName = "TamrielMerge Creatures.esp";

    /// <summary>The game plugin a creature config's "game" names (Daggerfall's when it names none).</summary>
    public static string GamePlugin(string? game) => (game ?? "daggerfall").ToLowerInvariant() switch
    {
        "redguard" => "Redguard.esm",
        "morrowind" => "Morrowind.esm",
        "oblivion" => "Oblivion.esm",
        _ => "Daggerfall.esm",
    };

    /// <summary>The creatures plugin over a game plugin: "Daggerfall Creatures.esp" for Daggerfall.esm.</summary>
    public static string PluginFor(string gamePlugin) => Path.GetFileNameWithoutExtension(gamePlugin) + " Creatures.esp";

    public string PluginName => mod.ModKey.FileName;
    readonly SkyrimMod mod;
    readonly IReadOnlyList<ISkyrimModGetter> masters;
    readonly List<ISkyrimModGetter> games = [];

    /// <param name="masters">Skyrim.esm and its DLCs, where template races are looked up.</param>
    /// <param name="pluginName">The plugin's name (<see cref="PluginFor"/>).</param>
    public CreatureRecords(IReadOnlyList<ISkyrimModGetter> masters, string pluginName)
    {
        this.masters = masters;
        mod = new SkyrimMod(ModKey.FromFileName(pluginName), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
    }

    /// <summary>A converted game's plugin whose actors this one overrides.</summary>
    public void AddGame(ISkyrimModGetter game) => games.Add(game);

    /// <summary>Gives the creature <paramref name="id"/> its records; <paramref name="actorEditorIds"/> are the game
    /// plugins' actors that become it (e.g. DFCreatureGiantScorpion). Returns how many actors were switched.</summary>
    public int Add(string id, string name, string templateRace, IEnumerable<string> actorEditorIds)
    {
        string pascal = CreatureBehavior.ProjectName(id)[2..^"Project".Length];
        var source = masters.SelectMany(m => m.Races).First(r => r.EditorID == templateRace);
        var race = source.Duplicate(mod.GetNextFormKey());
        race.EditorID = $"TMRace{pascal}";
        race.Name = name;
        string folder = CreatureRig.MeshFolder(id)["meshes\\".Length..];
        race.SkeletalModel = new GenderedItem<SimpleModel?>(new SimpleModel { File = $@"{folder}\character assets\skeleton.nif" }, new SimpleModel { File = $@"{folder}\character assets\skeleton.nif" });
        race.BehaviorGraph = new GenderedItem<ModelBehavior?>(new ModelBehavior { File = $@"{folder}\{CreatureBehavior.ProjectName(id)}.hkx" }, new ModelBehavior { File = $@"{folder}\{CreatureBehavior.ProjectName(id)}.hkx" });

        var addon = new ArmorAddon(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
        {
            EditorID = $"TMSkinAA{pascal}",
            Race = race.ToNullableLink<IRaceGetter>(),
            BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, Flags = BodyTemplate.Flag.NonPlayable, ArmorType = ArmorType.Clothing },
            WorldModel = new GenderedItem<Model?>(new Model { File = $@"{folder}\{id}.nif" }, new Model { File = $@"{folder}\{id}.nif" }),
            Priority = new GenderedItem<byte>(0, 0),
        };
        mod.ArmorAddons.Add(addon);
        var skin = new Armor(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
        {
            EditorID = $"TMSkin{pascal}",
            Race = race.ToNullableLink<IRaceGetter>(),
            BodyTemplate = new BodyTemplate { FirstPersonFlags = BipedObjectFlag.Body, Flags = BodyTemplate.Flag.NonPlayable, ArmorType = ArmorType.Clothing },
            Armature = [addon.ToLink<IArmorAddonGetter>()],
            MajorFlags = Armor.MajorFlag.NonPlayable,
        };
        mod.Armors.Add(skin);
        race.Skin = skin.ToNullableLink<IArmorGetter>();
        mod.Races.Add(race);

        int switched = 0;
        var wanted = actorEditorIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var game in games)
            foreach (var npc in game.Npcs.Where(n => n.EditorID is { } e && wanted.Contains(e)))
            {
                var o = npc.DeepCopy();
                // Its own race and skin (not a template's).
                o.Configuration.TemplateFlags &= ~(NpcConfiguration.TemplateFlag.Traits | NpcConfiguration.TemplateFlag.Inventory);
                o.Race.SetTo(race);
                o.WornArmor.SetTo(skin);
                // Its attacks are the race's (they name the behaviour graph's events), not a stand-in's.
                o.AttackRace.SetTo(race);
                mod.Npcs.Add(o);
                switched++;
            }
        return switched;
    }

    /// <summary>Writes the plugin (masters in load order: Skyrim's, then the game plugin).</summary>
    public string Write(string dir)
    {
        string path = Path.Combine(dir, PluginName);
        var order = new[] { "Skyrim.esm", "Update.esm", "Dawnguard.esm", "HearthFires.esm", "Dragonborn.esm" }.Select(f => ModKey.FromFileName(f))
            .Concat(games.Select(g => g.ModKey)).ToArray();
        mod.WriteToBinary(path, new BinaryWriteParameters { MastersListOrdering = new MastersListOrderingByLoadOrder(order) });
        return path;
    }
}
