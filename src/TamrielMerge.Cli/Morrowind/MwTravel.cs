using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Morrowind;

/// <summary>
/// Morrowind's travel services (silt striders, boats, Vivec's gondolas, the Mages Guild's guild guides, Solstheim's
/// boats): every person whose record lists travel destinations (NPC_ DODT, each followed by DNAM when the destination
/// is an interior; OpenMW ESM::Transport) offers Morrowind's "Travel" topic (sTravel), which shows their destinations
/// with the price of each and takes the player there (TMMwTravel, on the quest MWTravel), as OpenMW's travel window
/// (apps/openmw/mwgui/travelwindow.cpp) does:
/// <list type="bullet">
/// <item>a destination's name: its cell's name (DNAM), or for the outside the name of the cell the position falls in,
/// else its region's, else sDefaultCellname (OpenMW World::getCellName);</item>
/// <item>the price: a service inside (the guild guides) costs fMagesGuildTravel flat; outside, the distance from the
/// player to the destination over fTravelMult, at least 1, through Morrowind's barter offer (disposition, Mercantile,
/// Luck, Personality, fatigue), times one more than the followers travelling along;</item>
/// <item>the journey: to an outside destination the horizontal distance over fTravelTimeMult hours pass, the player
/// resting through them (as a sleep: TMMwRest.Restore); the player and their followers are moved to the destination's
/// position and heading; GetPCTraveling is true meanwhile (OpenMW setPlayerTraveling).</item>
/// </list>
/// The markers at the destinations are XMarkerHeadings placed persistent in the destination's cell.
/// </summary>
public sealed partial class MwPluginBuilder
{
    Quest? travelQuest;

    /// <summary>Travel services built (people with destinations) and their destinations.</summary>
    public int TravelServices { get; private set; }
    public int TravelDestinations { get; private set; }

    /// <summary>A game setting's text (sTravel, sCancel...), or the fallback when the plugins have none.</summary>
    string SettingText(string name, string fallback) =>
        world.Settings.TryGetValue(name, out var g) && g.Get("STRV") is { } v && v.Text.Length > 0 ? v.Text : fallback;

    /// <summary>The quest holding TMMwTravel (its lists filled by <see cref="BuildTravel"/>).</summary>
    Quest TravelQuest()
    {
        if (travelQuest is not null) return travelQuest;
        travelQuest = new Quest(mod)
        {
            EditorID = "MWTravel",
            Name = "Morrowind travel services",
            Flags = Quest.Flag.StartGameEnabled,
            Priority = 0,
            Type = Quest.TypeEnum.None,
            VirtualMachineAdapter = new QuestAdapter { Scripts = [new ScriptEntry { Name = "TMMwTravel" }] },
        };
        mod.Quests.Add(travelQuest);
        return travelQuest;
    }

    /// <summary>The name Morrowind gives an outside position (OpenMW World::getCellName): the cell's own name, else its
    /// region's name (REGN FNAM), else sDefaultCellname.</summary>
    string ExteriorName(float x, float y)
    {
        var grid = ((int)MathF.Floor(x / MwTerrain.MwCellUnits), (int)MathF.Floor(y / MwTerrain.MwCellUnits));
        if (world.Exteriors.TryGetValue(grid, out var cell))
        {
            if (cell.Name.Length > 0) return cell.Name;
            if (cell.Region is { } region && world.Objects.TryGetValue(region, out var rec) && rec.Type == "REGN" && rec.Str("FNAM") is { Length: > 0 } fname) return fname;
        }
        return SettingText("sDefaultCellname", "Wilderness");
    }

    /// <summary>
    /// The travel services: for each person of this build with destinations, a marker per destination, a message
    /// listing them with their prices (filled at run time), the hidden faction MWTravelServices that the Travel topic
    /// asks for, and the lists TMMwTravel reads (form lists: Papyrus arrays from a plugin can't pass 128). Called from
    /// BuildDialogue, once the cells and actors exist.
    /// </summary>
    void BuildTravel()
    {
        if (dialogueQuest is null) return;
        var quest = TravelQuest();
        var entry = quest.VirtualMachineAdapter!.Scripts[0];
        var top = mod.Worldspaces.FirstOrDefault(w => w.EditorID == "MWVvardenfell")?.TopCell;
        var services = new Faction(mod) { EditorID = "MWTravelServices", Name = "Travel services", Flags = Faction.FactionFlag.HiddenFromPC, Ranks = [new Rank { Number = 0 }] };
        mod.Factions.Add(services);
        var npcList = new FormList(mod) { EditorID = "MWTravelServiceNpcs" };
        var destLists = new FormList(mod) { EditorID = "MWTravelDestinations" };
        var menus = new FormList(mod) { EditorID = "MWTravelMenus" };
        string cancel = SettingText("sCancel", "Cancel");
        string title = SettingText("sTravelServiceTitle", "Select destination");
        string gold = SettingText("sGold", "Gold");
        int markerCount = 0;
        foreach (var rec in world.Objects.Values.Where(r => r.Type == "NPC_" && r.Has("DODT")).OrderBy(r => r.Id, StringComparer.OrdinalIgnoreCase))
        {
            // Only people this build made (a build of a few interiors leaves the others out).
            if (!actors.TryGetValue(rec.Id, out var made) || made is not Npc npc) continue;
            var list = new FormList(mod) { EditorID = "MWTravel" + Sanitize(rec.Id) };
            var msg = new Message(mod) { EditorID = "MWTravelMenu" + Sanitize(rec.Id), Flags = Message.Flag.MessageBox, MenuButtons = [] };
            var text = new System.Text.StringBuilder(title);
            // ESM::Transport: each DODT (position, rotation) followed by its DNAM when the destination is an interior.
            for (int i = 0; i < rec.Fields.Count; i++)
            {
                var f = rec.Fields[i];
                if (f.Type != "DODT" || f.Data.Length < 24) continue;
                string? cellName = i + 1 < rec.Fields.Count && rec.Fields[i + 1].Type == "DNAM" ? rec.Fields[i + 1].Text : null;
                var position = new P3Float(f.Float(0), f.Float(4), f.Float(8));
                var rotation = new P3Float(f.Float(12), f.Float(16), f.Float(20));
                Cell? cell;
                string name;
                if (cellName is { Length: > 0 })
                {
                    if (!interiorsByName.TryGetValue(cellName, out cell)) { Problems.Add($"travel: {rec.Id}'s destination \"{cellName}\" isn't built"); continue; }
                    name = cellName;
                }
                else
                {
                    cell = top;
                    if (cell is null) { Problems.Add($"travel: {rec.Id}'s destination at {position} is outside, which isn't built"); continue; }
                    name = ExteriorName(position.X, position.Y);
                }
                // A message box shows at most nine values and ten buttons (one is Cancel).
                if (list.Items.Count >= 9) { Problems.Add($"travel: {rec.Id} offers more than nine destinations; {name} dropped"); continue; }
                var marker = new PlacedObject(mod)
                {
                    EditorID = $"MWTravelDest{markerCount++:000}",
                    Base = Skyrim.CrimeFactions.XMarkerHeading.ToNullableLink<IPlaceableObjectGetter>(),
                    Placement = new Placement { Position = position, Rotation = rotation },
                    SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
                };
                cell.Persistent.Add(marker);
                list.Items.Add(marker.ToLink<ISkyrimMajorRecordGetter>());
                msg.MenuButtons.Add(new MessageButton { Text = name });
                text.Append('\n').Append(name).Append(": %.0f ").Append(gold);
            }
            if (list.Items.Count == 0) continue;
            msg.MenuButtons.Add(new MessageButton { Text = cancel });
            msg.Description = text.ToString();
            mod.Messages.Add(msg);
            mod.FormLists.Add(list);
            npc.Factions.Add(new RankPlacement { Faction = services.ToLink<IFactionGetter>(), Rank = 0 });
            npcList.Items.Add(npc.ToLink<ISkyrimMajorRecordGetter>());
            destLists.Items.Add(list.ToLink<ISkyrimMajorRecordGetter>());
            menus.Items.Add(msg.ToLink<ISkyrimMajorRecordGetter>());
            TravelServices++;
            TravelDestinations += list.Items.Count;
        }
        mod.FormLists.Add(npcList);
        mod.FormLists.Add(destLists);
        mod.FormLists.Add(menus);

        static ScriptObjectProperty Obj(string name, FormKey key) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(key) };
        entry.Properties.Add(Obj("ServiceNpcs", npcList.FormKey));
        entry.Properties.Add(Obj("Destinations", destLists.FormKey));
        entry.Properties.Add(Obj("Menus", menus.FormKey));
        entry.Properties.Add(Obj("PlayerRef", PlayerRefKey));
        entry.Properties.Add(Obj("GameHour", new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x38)));
        entry.Properties.Add(Obj("Coin", new FormKey(ModKey.FromFileName("Skyrim.esm"), 0x0F)));
        entry.Properties.Add(Obj("Rest", RestQuest().FormKey));
        // Morrowind's own values (GMST), as OpenMW's travel window reads them.
        entry.Properties.Add(new ScriptFloatProperty { Name = "TravelMult", Data = SettingFloat("fTravelMult", 4000) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "GuildTravel", Data = SettingFloat("fMagesGuildTravel", 10) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "TravelTimeMult", Data = SettingFloat("fTravelTimeMult", 16000) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "FatigueBase", Data = SettingFloat("fFatigueBase", 1.25f) });
        entry.Properties.Add(new ScriptFloatProperty { Name = "FatigueMult", Data = SettingFloat("fFatigueMult", 0.5f) });
        // A guild guide's teleport plays Morrowind's "mysticism area" (OpenMW onTravelButtonClick).
        if (SoundMarkerFor("mysticism area", loop: false) is { } guildSound) entry.Properties.Add(Obj("GuildSound", guildSound));
        // The kept attributes and skills for the barter offer, and Morrowind's companions (AIFollow) to travel along,
        // when the scripts are converted.
        if (scriptWorld is not null)
        {
            entry.Properties.Add(Obj("MwState", StateQuest().FormKey));
            entry.Properties.Add(Obj("MwAI", AiQuest().FormKey));
        }

        // The Travel topic: a top-level player topic of the dialogue quest for whoever is in the services faction; its
        // response says nothing (Morrowind's window opens without a line) and ends the dialogue, TMMwTravelInfo offering
        // the destinations as it ends.
        var branch = new DialogBranch(mod)
        {
            EditorID = "MWTravelBranch",
            Quest = dialogueQuest.ToLink<IQuestGetter>(),
            Category = DialogBranch.CategoryType.Player,
            Flags = DialogBranch.Flag.TopLevel,
        };
        var topic = new DialogTopic(mod)
        {
            EditorID = "MWTravelTopic",
            Name = SettingText("sTravel", "Travel"),
            Quest = dialogueQuest.ToNullableLink<IQuestGetter>(),
            Branch = branch.ToNullableLink<IDialogBranchGetter>(),
            Priority = 50,
            Category = DialogTopic.CategoryEnum.Topic,
            Subtype = DialogTopic.SubtypeEnum.Custom,
            Responses = [],
        };
        branch.StartingTopic = topic.ToNullableLink<IDialogTopicGetter>();
        var inIt = new GetInFactionConditionData(); inIt.Faction = new FormLinkOrIndex<IFactionGetter>(inIt, services.FormKey);
        topic.Responses.Add(new DialogResponses(mod)
        {
            Conditions = [Cond(inIt, CompareOperator.EqualTo, 1)],
            Responses = [new DialogResponse { Text = "", ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
            Flags = new DialogResponseFlags { Flags = DialogResponses.Flag.Goodbye },
            VirtualMachineAdapter = new DialogResponsesAdapter
            {
                Scripts = [new ScriptEntry { Name = "TMMwTravelInfo", Properties = [Obj("Travel", quest.FormKey)] }],
                ScriptFragments = new ScriptFragments
                {
                    ExtraBindDataVersion = 2,
                    FileName = "TMMwTravelInfo",
                    OnEnd = new ScriptFragment { ExtraBindDataVersion = 1, ScriptName = "TMMwTravelInfo", FragmentName = "Fragment_End" },
                },
            },
        });
        mod.DialogBranches.Add(branch);
        mod.DialogTopics.Add(topic);
        DialogueTopics++;
    }
}
