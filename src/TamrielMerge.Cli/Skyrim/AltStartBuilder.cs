using System.Diagnostics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;

namespace TamrielMerge.Skyrim;

/// <summary>
/// "TamrielMerge Start.esp": stories in order (Daggerfall, Redguard, Morrowind, Oblivion, Skyrim). The engine starts MQ101 on
/// every new game, so the TMStoryOrder script is attached to MQ101 and its stage 0 normal-start fragment
/// (Fragment_2, MQQuickstart == 0) is pointed at TMStoryOrder.Fragment_Takeover. Also adds the
/// Begin the Next Story and Travel Through Time lesser powers.
/// </summary>
public static class AltStartBuilder
{
    public const string PluginName = "TamrielMerge Start.esp";
    const string ScriptName = "TMStoryOrder";
    const string VanillaFragmentScript = "QF_MQ101_0003372B";
    static readonly ModKey Skyrim = ModKey.FromFileName("Skyrim.esm");
    static readonly FormKey MQ101 = new(Skyrim, 0x3372B);
    static readonly FormKey GameHour = new(Skyrim, 0x38);
    static readonly FormKey Gold = new(Skyrim, 0xF);
    static readonly FormKey VoiceEquipSlot = new(Skyrim, 0x25BEE);
    static readonly FormKey XMarker = new(Skyrim, 0x3B);

    /// <summary>The start plugin. It masters only Skyrim.esm, so it works whichever games were converted: each
    /// story's start is looked up at runtime from its own plugin (Daggerfall's by <paramref name="daggerfallStartId"/>,
    /// the local FormID of its start marker in Daggerfall.esm, when Daggerfall was converted).</summary>
    public static void Build(string skyrimData, string outDir, uint? daggerfallStartId, IReadOnlyList<AltStarts.AfterOpening>? afterOpenings = null)
    {
        var mod = new SkyrimMod(ModKey.FromFileName(PluginName), SkyrimRelease.SkyrimSE);
        mod.ModHeader.Flags |= SkyrimModHeader.HeaderFlag.Small;
        using var skyrim = SkyrimMod.CreateFromBinaryOverlay(Path.Combine(skyrimData, "Skyrim.esm"), SkyrimRelease.SkyrimSE);

        var confirm = AddMessage(mod, "TMConfirmNextStory", "Leave this land and begin the next story?", ["Yes", "No"]);
        var cont = AddMessage(mod, "TMContinuePrompt", "This story has reached its end. Continue to the next story?", ["Continue to the next story", "Stay and explore"]);
        var none = AddMessage(mod, "TMNoMoreStories", "There are no further stories to begin.", []);

        var power = AddPower(mod, "TMNextStory", "Begin the Next Story", "TMNextStoryEffect");

        // Travel Through Time: each game is a time and place in Tamriel's history. A persistent marker per story is
        // moved to the player when they leave it (where they were), each story keeps its own calendar (when), a global
        // per story shows its menu button, and the menu lists the times in history's order (Redguard first).
        var travel = AddPower(mod, "TMTravel", "Travel Through Time", "TMTravelEffect");
        var markerCell = new Cell(mod) { EditorID = "TMTravelMarkerCell", Name = "Outside of Time", Flags = Cell.Flag.IsInteriorCell };
        AddInteriorCell(mod, markerCell);
        // By story index (0 Daggerfall, 1 Redguard, 2 Morrowind, 3 Oblivion, 4 Skyrim): name, place, era, and the date
        // each game begins (Skyrim months from 0, Morning Star).
        string[] worlds = ["Daggerfall", "Redguard", "Morrowind", "Oblivion", "Skyrim"];
        string[] places = ["the Iliac Bay", "Stros M'Kai", "Vvardenfell", "Cyrodiil", "Skyrim"];
        string[] eras = ["3E", "2E", "3E", "3E", "4E"];
        int[] years = [405, 864, 427, 433, 201];
        int[] months = [8, 6, 7, 7, 7];
        int[] days = [13, 1, 16, 27, 17];
        int[] chronological = [1, 0, 2, 3, 4];
        var shows = new List<FormKey>();
        var markers = new List<FormKey>();
        var buttons = chronological.Select(i => $"{eras[i]} {years[i]}: {places[i]}{(places[i] == worlds[i] ? "" : $" ({worlds[i]})")}").Append("Stay in this time").ToArray();
        var travelMenu = AddMessage(mod, "TMTravelMenu", "To which time do you travel? A time you have been to resumes where and when you left it; one not begun yet starts there.", buttons);
        for (int i = 0; i < worlds.Length; i++)
        {
            var show = new GlobalShort(mod) { EditorID = $"TMTravelShow{worlds[i]}", Data = 0 };
            mod.Globals.Add(show);
            shows.Add(show.FormKey);
            travelMenu.MenuButtons[Array.IndexOf(chronological, i)].Conditions.Add(new ConditionFloat
            {
                Data = new GetGlobalValueConditionData { Global = new FormLinkOrIndex<IGlobalGetter>(new GetGlobalValueConditionData(), show.FormKey) },
                CompareOperator = CompareOperator.EqualTo,
                ComparisonValue = 1,
            });
            var marker = new PlacedObject(mod)
            {
                EditorID = $"TMTravelMarker{worlds[i]}",
                Base = XMarker.ToNullableLink<IPlaceableObjectGetter>(),
                Placement = new Placement { Position = new P3Float(i * 100, 0, 0) },
                MajorRecordFlagsRaw = 0x400,   // persistent
            };
            markerCell.Persistent.Add(marker);
            markers.Add(marker.FormKey);
        }
        var nowhere = AddMessage(mod, "TMTravelNowhere", "You have not yet been to another time. Each time you leave is remembered, and you can return to it.", []);
        // Holds the player's items while Skyrim's opening strips them (in this plugin, so it needs no other game).
        var stash = new PlacedObject(mod)
        {
            EditorID = "TMStoryStash",
            Base = new FormKey(Skyrim, 0xF8478).ToNullableLink<IPlaceableObjectGetter>(),   // TreasChestSmallEMPTYNoRespawn
            Placement = new Placement { Position = new P3Float(0, 500, 0) },
            MajorRecordFlagsRaw = 0x400,
        };
        markerCell.Persistent.Add(stash);

        FormKey ByEditorId<T>(IEnumerable<T> records, string edid) where T : IMajorRecordGetter =>
            records.First(r => r.EditorID == edid).FormKey;
        // Daggerfall Unity's starting kit (ItemHelper.AssignStartingGear): shirt and casual pants, an iron longsword
        // (the custom-class weapon; Skyrim picks no class), 100 gold, and torches.
        var startingItems = new[]
        {
            ByEditorId(skyrim.Armors, "ClothesFarmClothes01"),
            ByEditorId(skyrim.Armors, "ClothesFarmBoots01"),
            ByEditorId(skyrim.Weapons, "IronSword"),
        };
        var torch = ByEditorId(skyrim.Lights, "Torch01");

        // Silence that outranks the main menu's theme (MUSSpecialIntro, priority 1): Skyrim's own silent type (_NONE,
        // its silent track) at priority 1, cutting in abruptly. TMStoryOrder adds it while a story begins.
        var silence = mod.MusicTypes.DuplicateInAsNewRecord(skyrim.MusicTypes.First(m => m.EditorID == "_NONE"));
        silence.EditorID = "TMSilence";
        if (silence.Data is { } md) md.Priority = 1;
        silence.Flags = MusicType.Flag.AbruptTransition;
        // Each story's opening cinematic may be skipped (user, 2026-10-09: "add a skip intro as well if user wants to").
        // Made after every older record, so their FormIDs (and saves that hold them) stay as they were.
        var introPrompt = AddMessage(mod, "TMIntroPrompt", "Watch this story's opening cinematic?", ["Watch", "Skip", "Skip all intros"]);

        // How the game begins (user, 2026-10-12: "the games start in the corrisponding story sequence if chosen. we need
        // an alternative start if one is traveled to or game started with the tutorial quest done and skyrims starting
        // in riverwood with the imperial or stormcloak npc is chosen", "and player owned games equivelant"): the stories
        // in order, or a time of the player's choosing (only the games they have: a global per time shows its button),
        // its opening played or done. Made after every older record, so their FormIDs stay as they were.
        var startHow = AddMessage(mod, "TMStartHow", "How does your journey begin?", ["The stories in order, from the first", "In a time of my choosing"]);
        var startWhich = AddMessage(mod, "TMStartWhich", "In which time do you begin?",
            [.. Enumerable.Range(0, worlds.Length).Select(i => $"{eras[i]} {years[i]}: {places[i]}{(places[i] == worlds[i] ? "" : $" ({worlds[i]})")}")]);
        var startShows = new List<FormKey>();
        for (int i = 0; i < worlds.Length; i++)
        {
            var show = new GlobalShort(mod) { EditorID = $"TMStartShow{worlds[i]}", Data = 0 };
            mod.Globals.Add(show);
            startShows.Add(show.FormKey);
            startWhich.MenuButtons[i].Conditions.Add(new ConditionFloat
            {
                Data = new GetGlobalValueConditionData { Global = new FormLinkOrIndex<IGlobalGetter>(new GetGlobalValueConditionData(), show.FormKey) },
                CompareOperator = CompareOperator.EqualTo,
                ComparisonValue = 1,
            });
        }
        var startOpening = AddMessage(mod, "TMStartOpening", "Begin with this time's opening, or after it (its first steps done)?", ["Play the opening", "Begin after the opening"]);
        var startSkyrim = AddMessage(mod, "TMStartSkyrim", "Skyrim begins at Helgen.",
            ["Play Helgen", "Escaped Helgen with Hadvar: an Imperial, at Riverwood", "Escaped Helgen with Ralof: a Stormcloak, at Riverwood"]);
        // Each converted game's start after its opening (AltStarts): the reference, the offset from it, its opening's quest
        // and stage, and what the opening gives; by story (0 Daggerfall .. 3 Oblivion), 0 for none.
        var after = afterOpenings ?? [];
        int[] AfterInts(Func<AltStarts.AfterOpening, uint> f) => [.. Enumerable.Range(0, 4).Select(i => after.FirstOrDefault(a => a.Story == i) is { } a ? (int)f(a) : 0)];
        float[] offsets = [.. Enumerable.Range(0, 4).SelectMany(i => after.FirstOrDefault(a => a.Story == i) is { } a ? new[] { a.Dx, a.Dy, a.Dz } : new[] { 0f, 0f, 0f })];
        // Skyrim after Helgen: as vanilla's own quickstart (QF_MQ101 Fragment_27) leaves it, then on to Riverwood.
        var mq101Script = skyrim.Quests.First(q => q.FormKey == MQ101).VirtualMachineAdapter!.Scripts.First(sc => sc.Name == VanillaFragmentScript);
        FormKey Vanilla(string property) => mq101Script.Properties.OfType<IScriptObjectPropertyGetter>().First(pr => pr.Name == property).Object.FormKey;
        var kitImperial = new[] { "ArmorImperialLightCuirass", "ArmorImperialLightBoots", "ArmorImperialLightGauntlets", "ArmorImperialLightHelmet" }
            .Select(e => ByEditorId(skyrim.Armors, e)).Append(ByEditorId(skyrim.Weapons, "Imperialsword")).ToArray();
        var kitStormcloak = new[] { "ArmorStormcloakCuirass", "ArmorStormcloakBoots", "ArmorStormcloakGauntlets" }
            .Select(e => ByEditorId(skyrim.Armors, e)).Append(ByEditorId(skyrim.Weapons, "IronWarAxe")).ToArray();

        // Attach the story order to MQ101 and retarget stage 0 / entry 0 (the normal new-game path).
        var mq101 = mod.Quests.GetOrAddAsOverride(skyrim.Quests.First(q => q.FormKey == MQ101));
        var vm = mq101.VirtualMachineAdapter!;
        vm.Scripts.Add(new ScriptEntry
        {
            Name = ScriptName,
            Properties =
            [
                ObjectProp("GameHour", GameHour),
                new ScriptIntProperty { Name = "DaggerfallStartFormId", Data = (int)(daggerfallStartId ?? 0x010C81) },
                new ScriptStringProperty { Name = "DaggerfallPlugin", Data = "Daggerfall.esm" },
                ObjectProp("StashChest", stash.FormKey),
                ObjectProp("NextStoryPower", power.FormKey),
                ObjectProp("ConfirmNextStory", confirm.FormKey),
                ObjectProp("ContinuePrompt", cont.FormKey),
                ObjectProp("NoMoreStories", none.FormKey),
                ObjectProp("TravelPower", travel.FormKey),
                ObjectProp("TravelMenu", travelMenu.FormKey),
                ObjectProp("TravelNowhere", nowhere.FormKey),
                ObjectList("TravelShow", shows),
                ObjectList("TravelMarkers", markers),
                new ScriptIntListProperty { Name = "TravelOrder", Data = [.. chronological] },
                new ScriptStringListProperty { Name = "TimelineEra", Data = [.. eras] },
                new ScriptStringListProperty { Name = "TimelinePlace", Data = [.. places] },
                new ScriptIntListProperty { Name = "TimelineYear", Data = [.. years] },
                new ScriptIntListProperty { Name = "TimelineMonth", Data = [.. months] },
                new ScriptIntListProperty { Name = "TimelineDay", Data = [.. days] },
                ObjectProp("GameYear", new FormKey(Skyrim, 0x35)),
                ObjectProp("GameMonth", new FormKey(Skyrim, 0x36)),
                ObjectProp("GameDay", new FormKey(Skyrim, 0x37)),
                ObjectProp("Gold", Gold),
                new ScriptStringListProperty { Name = "CoinNames", Data = [.. CoinNames(skyrim)] },
                new ScriptFloatListProperty { Name = "CoinWeights", Data = [.. Enumerable.Repeat(CoinWeight, 5)] },
                new ScriptIntProperty { Name = "StartingGold", Data = 100 },
                ObjectProp("Torch", torch),
                new ScriptIntProperty { Name = "StartingTorches", Data = 5 },
                ObjectList("StartingItems", startingItems),
                ObjectProp("Silence", silence.FormKey),
                ObjectProp("IntroPrompt", introPrompt.FormKey),
                ObjectProp("StartHow", startHow.FormKey),
                ObjectProp("StartWhich", startWhich.FormKey),
                ObjectList("StartShow", startShows),
                ObjectProp("StartOpening", startOpening.FormKey),
                ObjectProp("StartSkyrim", startSkyrim.FormKey),
                new ScriptIntListProperty { Name = "AfterOpeningIds", Data = [.. AfterInts(a => a.RefId)] },
                new ScriptFloatListProperty { Name = "AfterOpeningOffsets", Data = [.. offsets] },
                new ScriptIntListProperty { Name = "OpeningQuestIds", Data = [.. AfterInts(a => a.QuestId)] },
                new ScriptIntListProperty { Name = "OpeningQuestStages", Data = [.. AfterInts(a => (uint)a.QuestStage)] },
                new ScriptIntListProperty { Name = "OpeningItemIds", Data = [.. AfterInts(a => a.ItemId)] },
                ObjectProp("MQ102A", new FormKey(Skyrim, 0x2BF9C)),
                ObjectProp("MQ102B", new FormKey(Skyrim, 0x2610A)),
                ObjectProp("Hadvar", new FormKey(Skyrim, 0x2BFA2)),
                ObjectProp("Ralof", new FormKey(Skyrim, 0x2BF9E)),
                ObjectProp("RiverwoodMarker", new FormKey(Skyrim, 0x162A4)),
                ObjectProp("HelgenMusicDungeon", Vanilla("MUSDungeonChargen")),
                ObjectProp("HelgenMusicBoss", Vanilla("MUSCombatBossChargen")),
                ObjectProp("HelgenAttackSound", Vanilla("ExtHelgenAttackASREF")),
                ObjectList("KitImperial", kitImperial),
                ObjectList("KitStormcloak", kitStormcloak),
            ],
        });
        // The guide for a new character (user, 2026-10-15: "i need a tutorial popup on where stuff is after character
        // creation", "we can start in any world", "its based on what games the user owns"): TMTutorial shows its pages
        // when the race menu first closes, the games' pages only for the games installed, and hands over this book.
        var owned = Enumerable.Range(0, 4).Where(i => File.Exists(Path.Combine(outDir, StoryPlugins[i]))).Append(4).ToList();
        var guidePages = GuidePages();
        var gamePages = GuideGamePages();
        // A plain book of Skyrim's own for its look (the first common book with a model and inventory art).
        var vanillaBook = skyrim.Books.FirstOrDefault(bk => bk.EditorID?.StartsWith("Book", StringComparison.Ordinal) == true && bk.Model is not null && !bk.InventoryArt.IsNull);
        var guide = new Book(mod)
        {
            EditorID = "TMGuideBook",
            Name = "TamrielMerge Guide",
            Model = vanillaBook?.Model is { } vm0 ? new Model { File = vm0.File.DataRelativePath.Path } : null,
            InventoryArt = vanillaBook?.InventoryArt.FormKeyNullable is { } art ? art.ToNullableLink<IStaticGetter>() : new FormLinkNullable<IStaticGetter>(),
            Value = 0,
            Weight = 0,
            BookText = GuideBookText(guidePages, gamePages, owned, worlds),
            ObjectBounds = new ObjectBounds { First = new P3Int16(-8, -11, 0), Second = new P3Int16(8, 11, 3) },
        };
        mod.Books.Add(guide);
        vm.Scripts.Add(new ScriptEntry
        {
            Name = "TMTutorial",
            Properties =
            [
                new ScriptStringListProperty { Name = "Pages", Data = [.. guidePages] },
                new ScriptStringListProperty { Name = "GamePages", Data = [.. gamePages] },
                new ScriptStringListProperty { Name = "GameNames", Data = [.. worlds] },
                new ScriptStringListProperty { Name = "KeyNames", Data = [.. KeyNames()] },
                ObjectProp("Guide", guide.FormKey),
            ],
        });

        var normalStart = vm.Fragments.Single(f => f.Stage == 0 && f.StageIndex == 0);
        if (normalStart.ScriptName != VanillaFragmentScript || normalStart.FragmentName != "Fragment_2")
            throw new InvalidOperationException($"MQ101 stage 0 entry 0 is {normalStart.ScriptName}.{normalStart.FragmentName}, expected vanilla Fragment_2");
        normalStart.ScriptName = ScriptName;
        normalStart.FragmentName = "Fragment_Takeover";

        Directory.CreateDirectory(outDir);
        TopicSubtypes.Fill(mod);
        mod.WriteToBinary(Path.Combine(outDir, PluginName));
        Console.WriteLine($"Wrote {Path.Combine(Path.GetFullPath(outDir), PluginName)}");
    }

    static readonly string[] StoryPlugins = ["Daggerfall.esm", "Redguard.esm", "Morrowind.esm", "Oblivion.esm", "Skyrim.esm"];

    /// <summary>The guide's general pages (TMTutorial: %KEY% the Tamriel Menu control's key, %GAMES% the games installed).</summary>
    static string[] GuidePages() =>
    [
        "Welcome to TamrielMerge.\n\nYour games: %GAMES%. Each is a time and place in Tamriel's history, and one character can live in all of them.\n\nThis guide is also in your inventory as a book: TamrielMerge Guide.",
        "The Tamriel menu: press %KEY% (Tamriel Menu in Settings > Controls), or cast the Tamriel power (Magic > Powers).\n\nFactions: every game's guilds and factions, and your rank in each.\nCoin: every game's money, and what you carry.\nTravel: the towns of every world you have reached, travelled to as Daggerfall travels: days on the road, inns or camping, ships and horses, encounters on the way.\nSkills: the games' skills Skyrim lacks, rising with use, with their perks.\nPassives: the standing stones' web, from your birthsign, a point each level.",
        "Moving between the games\n\nTravel Through Time (Magic > Powers): go to another time you have been to; you come back where and when you left it. A time not begun yet starts at its beginning.\n\nWhen a game's story ends you are asked whether to go on to the next; the Begin the Next Story power does it later.\n\nEach time keeps its own date and its own coin; money changers in its towns exchange coin.",
    ];

    /// <summary>Each game's page (by story), shown only when its plugin is installed.</summary>
    static string[] GuideGamePages() =>
    [
        "Daggerfall: the Iliac Bay, 3E 405\n\nOne seamless land, far bigger than Skyrim: the Tamriel menu's Travel tab crosses it in days. Ask townsfolk for work to be given Daggerfall's quests; guilds promote you as your reputation with them grows. Enemies notice you as in Daggerfall, by sight and by hearing: sneak to stay unseen. You cannot rest, wait or travel with enemies nearby.",
        "Redguard: Stros M'Kai, 2E 864\n\nThe island of Cyrus's story. Talk to everyone: conversations open the way on. Locks and puzzles that take an item ask which of yours to use. Ropes and ledges are climbed and swung from as in Redguard.",
        "Morrowind: Vvardenfell, 3E 427\n\nYour journal's entries are kept as quest objectives. Morrowind's own spells work as there: Mark and Recall, and the Divine and Almsivi Interventions. Companions share their goods through the Companion Share topic.",
        "Oblivion: Cyrodiil, 3E 433\n\nLocks open with Oblivion's lockpicking. The Oblivion gates, the guilds and the Arena are as they were; crimes are seen and paid for as in Oblivion.",
        "Skyrim: 4E 201\n\nSkyrim's own story, as it is. Its gold, its guilds and its towns sit beside the other games' in the Tamriel menu.",
    ];

    /// <summary>The guide as a book: the general pages, then the pages of the games converted here.</summary>
    static string GuideBookText(string[] pages, string[] gamePages, List<int> owned, string[] worlds)
    {
        string Html(string text) => string.Join("", text.Split("\n\n").Select(par => $"<p>{System.Net.WebUtility.HtmlEncode(par).Replace("\n", "<br>")}</p>"));
        var sb = new System.Text.StringBuilder("<p align=\"center\"><font size=\"30\">TamrielMerge Guide</font></p>");
        foreach (var page in pages)
            sb.Append(Html(page.Replace("%KEY%", "U (or the key set for Tamriel Menu)").Replace("%GAMES%", string.Join(", ", owned.Select(i => worlds[i])))));
        foreach (int i in owned) sb.Append("[pagebreak]").Append(Html(gamePages[i]));
        return sb.ToString();
    }

    /// <summary>DirectX scan codes' names (index = code), for the guide's "press ..." (SKSE's Input.GetMappedKey).</summary>
    static string[] KeyNames()
    {
        var names = new string[282];
        Array.Fill(names, "");
        string[] row1 = ["", "Esc", "1", "2", "3", "4", "5", "6", "7", "8", "9", "0", "-", "=", "Backspace", "Tab", "Q", "W", "E", "R", "T", "Y", "U", "I", "O", "P", "[", "]", "Enter", "Left Ctrl",
            "A", "S", "D", "F", "G", "H", "J", "K", "L", ";", "'", "`", "Left Shift", "\\", "Z", "X", "C", "V", "B", "N", "M", ",", ".", "/", "Right Shift", "Num *", "Left Alt", "Space", "Caps Lock",
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "Num Lock", "Scroll Lock", "Num 7", "Num 8", "Num 9", "Num -", "Num 4", "Num 5", "Num 6", "Num +", "Num 1", "Num 2", "Num 3", "Num 0", "Num ."];
        for (int i = 0; i < row1.Length; i++) names[i] = row1[i];
        names[0x57] = "F11"; names[0x58] = "F12"; names[0x9C] = "Num Enter"; names[0x9D] = "Right Ctrl"; names[0xB5] = "Num /";
        names[0xB8] = "Right Alt"; names[0xC7] = "Home"; names[0xC8] = "Up"; names[0xC9] = "Page Up"; names[0xCB] = "Left"; names[0xCD] = "Right";
        names[0xCF] = "End"; names[0xD0] = "Down"; names[0xD1] = "Page Down"; names[0xD2] = "Insert"; names[0xD3] = "Delete";
        // SKSE's mouse codes: 256 left button, 257 right, 258 middle, 259.. the side buttons, 264/265 the wheel.
        names[256] = "Left Mouse"; names[257] = "Right Mouse"; names[258] = "Middle Mouse"; names[259] = "Mouse 4"; names[260] = "Mouse 5";
        names[261] = "Mouse 6"; names[262] = "Mouse 7"; names[263] = "Mouse 8"; names[264] = "Wheel Up"; names[265] = "Wheel Down";
        return names;
    }

    /// <summary>Each story's coin, in story order (TMStoryOrder's money): Daggerfall's gold pieces, Redguard's gold,
    /// Morrowind's drakes, Oblivion's septims, and Skyrim's gold under its own name. All are Skyrim's gold item, which
    /// the story order renames to the coin of the time the player is in.</summary>
    static string[] CoinNames(ISkyrimModGetter skyrim)
    {
        string skyrimName = "Gold";
        try { skyrimName = skyrim.MiscItems.FirstOrDefault(m => m.FormKey == Gold)?.Name?.String is { Length: > 0 } n ? n : skyrimName; }
        catch (Exception) { }   // no strings files: the English name
        return ["Gold Pieces", "Gold", "Drakes", "Septims", skyrimName];
    }

    /// <summary>What one coin weighs, in every time: Daggerfall's 400 coins to the kilogram (Daggerfall Unity's gold
    /// piece, 0.0025 kg) in Skyrim's pound-like units, so 10,000 coins weigh 50, and a fortune wants a bank.</summary>
    const float CoinWeight = 0.005f;

    /// <summary>A lesser power (voice slot) whose one scripted effect hands off to the story order on MQ101.</summary>
    static Spell AddPower(SkyrimMod mod, string edidPrefix, string name, string effectScript)
    {
        var effect = new MagicEffect(mod)
        {
            EditorID = edidPrefix + "Effect",
            Name = name,
            Archetype = new MagicEffectArchetype { Type = MagicEffectArchetype.TypeEnum.Script },
            CastType = CastType.FireAndForget,
            TargetType = TargetType.Self,
            Flags = MagicEffect.Flag.HideInUI | MagicEffect.Flag.NoHitEvent | MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoMagnitude | MagicEffect.Flag.NoArea | MagicEffect.Flag.Painless,
            VirtualMachineAdapter = new VirtualMachineAdapter
            {
                Scripts = [new ScriptEntry { Name = effectScript, Properties = [ObjectProp("StoryOrder", MQ101)] }],
            },
        };
        mod.MagicEffects.Add(effect);
        var power = new Spell(mod)
        {
            EditorID = edidPrefix + "Power",
            Name = name,
            Type = SpellType.LesserPower,
            CastType = CastType.FireAndForget,
            TargetType = TargetType.Self,
            EquipmentType = VoiceEquipSlot.ToNullableLink<IEquipTypeGetter>(),
            Effects = [new Effect { BaseEffect = effect.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData() }],
        };
        mod.Spells.Add(power);
        return power;
    }

    /// <summary>Interior cells live in block (ID % 10) / sub-block ((ID / 10) % 10) groups.</summary>
    static void AddInteriorCell(SkyrimMod mod, Cell cell)
    {
        int blockNo = (int)(cell.FormKey.ID % 10), subNo = (int)(cell.FormKey.ID / 10 % 10);
        var block = mod.Cells.Records.FirstOrDefault(b => b.BlockNumber == blockNo);
        if (block is null) { block = new CellBlock { BlockNumber = blockNo, GroupType = GroupTypeEnum.InteriorCellBlock, SubBlocks = [] }; mod.Cells.Records.Add(block); }
        var sub = block.SubBlocks.FirstOrDefault(s => s.BlockNumber == subNo);
        if (sub is null) { sub = new CellSubBlock { BlockNumber = subNo, GroupType = GroupTypeEnum.InteriorCellSubBlock, Cells = [] }; block.SubBlocks.Add(sub); }
        sub.Cells.Add(cell);
    }

    static Message AddMessage(SkyrimMod mod, string edid, string text, string[] buttons)
    {
        var msg = new Message(mod) { EditorID = edid, Description = text, Flags = Message.Flag.MessageBox, MenuButtons = [] };
        foreach (var b in buttons) msg.MenuButtons.Add(new MessageButton { Text = b });
        mod.Messages.Add(msg);
        return msg;
    }

    static ScriptObjectProperty ObjectProp(string name, FormKey key) =>
        new() { Name = name, Object = key.ToLink<ISkyrimMajorRecordGetter>() };

    static ScriptObjectListProperty ObjectList(string name, IEnumerable<FormKey> keys)
    {
        var p = new ScriptObjectListProperty { Name = name };
        foreach (var k in keys) p.Objects.Add(new ScriptObjectProperty { Object = k.ToLink<ISkyrimMajorRecordGetter>() });
        return p;
    }

    /// <summary>
    /// The converter's SKSE plugins (tools/skse) into outDir\SKSE\Plugins: TamrielMergeEra.dll makes the game's dates
    /// show the era of the time travelled to (Skyrim prints "4E" whatever the year); TamrielMergeSeamless.dll widens
    /// Skyrim's physics box to 512 cells from a worldspace's centre, which Daggerfall's overland (one worldspace of
    /// 1000 x 500 cells) needs: without it physics ends 64 cells out (actors sink, doors stick). SKSE is needed anyway
    /// (the Tamriel menu). The graphics plugin (DLSS, ray tracing) was removed from the tool on 2026-10-15 (user).
    /// </summary>
    public static void CopyEraPlugin(string outDir)
    {
        RemoveGraphicsPlugin(outDir);
        (string Folder, string[] Files)[] plugins =
        [
            ("TamrielMergeEra", ["TamrielMergeEra.dll"]),
            ("TamrielMergeSeamless", ["TamrielMergeSeamless.dll"]),
        ];
        string target = Path.Combine(outDir, "SKSE", "Plugins");
        foreach (var (folder, files) in plugins)
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string src = Path.Combine(dir.FullName, "tools", "skse", folder);
                if (!File.Exists(Path.Combine(src, files[0]))) continue;
                Directory.CreateDirectory(target);
                foreach (var f in files)
                {
                    string from = Path.Combine(src, f);
                    if (File.Exists(from)) File.Copy(from, Path.Combine(target, f), overwrite: true);
                }
                break;
            }
    }

    /// <summary>
    /// The graphics plugin's files where an earlier conversion put them (the plugin, NVIDIA's DLSS runtime in its folder,
    /// its settings and logs): removed with it from the tool (user, 2026-10-15: "remove dlss and the plugin"). In
    /// <paramref name="dataDir"/> (an output folder, or Skyrim's Data on --install); returns what was removed.
    /// </summary>
    public static List<string> RemoveGraphicsPlugin(string dataDir)
    {
        string plugins = Path.Combine(dataDir, "SKSE", "Plugins");
        var removed = new List<string>();
        if (!Directory.Exists(plugins)) return removed;
        foreach (var f in Directory.GetFiles(plugins, "TamrielMergeGraphics*"))
            try { File.Delete(f); removed.Add(Path.GetFileName(f)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        string folder = Path.Combine(plugins, "TamrielMergeGraphics");
        if (Directory.Exists(folder))
            try { Directory.Delete(folder, true); removed.Add("TamrielMergeGraphics\\"); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        return removed;
    }

    /// <summary>
    /// The mod's Papyrus scripts into outDir\Scripts (and their sources into outDir\Source\Scripts). With the Creation
    /// Kit installed (its Papyrus compiler and Skyrim's script sources) they are compiled, and the compiled copies kept
    /// with the sources (Papyrus\Compiled) are refreshed; without it those compiled copies are used, so converting
    /// needs no Creation Kit.
    /// </summary>
    public static void CompileScripts(string skyrimData, string sourceDir, string outDir)
    {
        string game = Path.GetDirectoryName(Path.GetFullPath(skyrimData.TrimEnd('\\', '/')))!;
        string compiler = Path.Combine(game, "Papyrus Compiler", "PapyrusCompiler.exe");
        string vanillaSources = Path.Combine(skyrimData, "Source", "Scripts");
        string scriptsOut = Path.Combine(outDir, "Scripts");
        string sourceOut = Path.Combine(outDir, "Source", "Scripts");
        Directory.CreateDirectory(scriptsOut);
        Directory.CreateDirectory(sourceOut);
        var sources = Directory.GetFiles(sourceDir, "*.psc");
        foreach (var psc in sources)
            File.Copy(psc, Path.Combine(sourceOut, Path.GetFileName(psc)), overwrite: true);
        string precompiled = Path.Combine(sourceDir, "Compiled");
        if (!File.Exists(compiler) || !File.Exists(Path.Combine(vanillaSources, "ObjectReference.psc")))
        {
            int copied = 0;
            foreach (var psc in sources)
            {
                string pex = Path.Combine(precompiled, Path.ChangeExtension(Path.GetFileName(psc), ".pex"));
                if (!File.Exists(pex)) { Console.WriteLine($"  no compiled copy of {Path.GetFileName(psc)}"); continue; }
                File.Copy(pex, Path.Combine(scriptsOut, Path.GetFileName(pex)), overwrite: true);
                copied++;
            }
            Console.WriteLine($"No Creation Kit Papyrus compiler: used {copied} precompiled scripts");
            return;
        }
        var psi = new ProcessStartInfo(compiler)
        {
            // SKSE's script sources (Data\Scripts\Source, when SKSE is installed) come before vanilla's, so scripts may use
            // SKSE functions; without SKSE in the game those calls just do nothing.
            ArgumentList = { sourceDir, "-all", "-f=TESV_Papyrus_Flags.flg", $"-i={sourceDir};{Path.Combine(skyrimData, "Scripts", "Source")};{vanillaSources}", $"-o={scriptsOut}" },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0 || !output.Contains(" 0 failed"))
            throw new InvalidOperationException($"Papyrus compile failed:\n{output}");
        Console.WriteLine($"Compiled {sources.Length} scripts");
        // Keep the compiled copies beside the sources (in the source tree, when running from it) up to date.
        foreach (var dir in new[] { precompiled, RepoPapyrusCompiled() }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
        {
            Directory.CreateDirectory(dir);
            foreach (var psc in sources)
            {
                string pex = Path.Combine(scriptsOut, Path.ChangeExtension(Path.GetFileName(psc), ".pex"));
                if (File.Exists(pex)) File.Copy(pex, Path.Combine(dir, Path.GetFileName(pex)), overwrite: true);
            }
        }
    }

    /// <summary>src\TamrielMerge.Cli\Papyrus\Compiled in the source tree this build runs from, if any.</summary>
    static string? RepoPapyrusCompiled()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "TamrielMerge.Cli", "Papyrus");
            if (Directory.Exists(candidate)) return Path.Combine(candidate, "Compiled");
        }
        return null;
    }
}
