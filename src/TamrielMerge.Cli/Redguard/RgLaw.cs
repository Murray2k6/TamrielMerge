using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;

namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's law (docs/progression.md, "Crime by game and area"): Stros M'Kai has no bounty and no crime faction.
/// The Empire's guards there attack Cyrus only when he goes about with a drawn weapon where they see him (TMRgLaw, the
/// quest RGLaw, with every placed guard), and leave him be once it is put away (unless he has hurt them); striking anyone
/// else is answered by them alone, as Skyrim's people without a crime faction answer. No Redguard actor keeps a vanilla
/// look's Skyrim crime faction (<see cref="WithoutSkyrimLaw"/>).
/// </summary>
public sealed partial class RgPluginBuilder
{
    readonly List<(PlacedNpc Ref, Scene Scene)> guardSpots = [];
    Faction? rgGuards;
    Dictionary<FormKey, IFactionGetter>? skyrimFactionsByKey;

    /// <summary>Redguard's guard tag (night watch dozing, TMNightDanger; and TMRgLaw's guards).</summary>
    Faction RgGuards => rgGuards ??= Skyrim.TownNights.Guards(mod, "RG");

    /// <summary>
    /// A copy of a vanilla actor without Skyrim's law: its crime faction cleared, and its factions its own (not the
    /// template's through "use factions") with Skyrim's crime factions and the template's non-memberships (rank -1)
    /// dropped. A guard keeps none of the vanilla soldier's factions but Redguard's guard tag.
    /// </summary>
    void WithoutSkyrimLaw(Npc npc, bool guard)
    {
        Skyrim.CrimeFactions.OwnFactions(npc);
        npc.CrimeFaction.Clear();
        var skyrimFactions = skyrimFactionsByKey ??= vanilla.Skyrim.Factions.ToDictionary(f => f.FormKey);
        npc.Factions.RemoveAll(r => guard || r.Rank < 0
            || skyrimFactions.TryGetValue(r.Faction.FormKey, out var f) && Skyrim.CrimeFactions.IsSkyrimCrimeFaction(f));
        if (guard) npc.Factions.Add(new RankPlacement { Faction = RgGuards.ToLink<IFactionGetter>(), Rank = 0 });
    }

    /// <summary>After the actors, dialogue and scripts are placed: every guard made persistent for TMRgLaw, on RGLaw.</summary>
    void BuildLaw()
    {
        var guards = new ScriptObjectListProperty { Name = "Guards" };
        foreach (var (placed, scene) in guardSpots)
        {
            if ((placed.MajorRecordFlagsRaw & 0x400) == 0)
            {
                foreach (var c in scene.Interior is { } i ? [i] : scene.Cells.Values.ToList()) c.Temporary.Remove(placed);
                placed.MajorRecordFlagsRaw |= 0x400;
                PersistentCellFor(scene).Persistent.Add(placed);
            }
            guards.Objects.Add(new ScriptObjectProperty { Object = placed.ToLink<ISkyrimMajorRecordGetter>() });
        }
        var quest = new Quest(mod)
        {
            EditorID = "RGLaw",
            Name = "Redguard law",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 0,
            Type = Quest.TypeEnum.None,
            VirtualMachineAdapter = new QuestAdapter
            {
                Scripts =
                [
                    new ScriptEntry
                    {
                        Name = "TMRgLaw",
                        Properties =
                        [
                            guards,
                            new ScriptObjectProperty { Name = "PlayerRef", Object = new FormLink<ISkyrimMajorRecordGetter>(new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x14)) },
                        ],
                    },
                ],
            },
        };
        mod.Quests.Add(quest);
        GuardCount = guardSpots.Count;
    }

    /// <summary>The guards TMRgLaw watches.</summary>
    public int GuardCount { get; private set; }
}
