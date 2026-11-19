using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NAudio.Wave;
using Noggog;
using TamrielMerge.Skyrim;

namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's opening (START.RGM; user, 2026-10-09: "the start of redguard cyrus fights etc that needs fixed as well"),
/// played in Skyrim as the game plays it: Cyrus on Brennan's boat out at sea among sharks, two of the League's pirates
/// boarding (SPIRATE1, SPIRATE2: beginCombat; gone when dead or in the water), then Brennan out of his cabin and their
/// talk in the game's own recordings (Brennan's lines through his dialogue, Cyrus's as the player's voice with the line
/// shown), Vandar's boat in view ("Look! Over there!") and away, "Let us move on", and on to Stros M'Kai's docks
/// (LoadWorld(1, 1, 256)). The scene is an open-sea worldspace; both boats are the tool's ship (navmeshed decks). Papyrus:
/// TMRgOpening, on the start marker (where TMStoryOrder begins Redguard's story).
/// </summary>
public sealed partial class RgPluginBuilder
{
    const string OpeningScene = "START";
    readonly Dictionary<(string Scene, string Script), (PlacedObject Ref, float Scale)> shipsByScript = [];
    PlacedObject? docksArrival;

    /// <summary>Brennan's and Cyrus's lines in the opening (RTX tags), in order: who speaks each.</summary>
    static readonly (string Tag, bool Cyrus)[] OpeningTalk =
        [("b71d", false), ("bh1c", true), ("bh1a", false), ("bo1c", true), ("b72a", false), ("b73a", false)];

    /// <summary>Every ship placed (PlaceShip), by scene.</summary>
    readonly List<(string Scene, PlacedObject Ref)> placedShips = [];

    /// <summary>The ship's collision (model space) placed where a reference puts it.</summary>
    internal static List<Vector3[]> ShipSolid(Skyrim.Meshes.ShipModel.Made ship, P3Float position, P3Float rotation, float scale)
    {
        var m = Skyrim.SkyrimRotation.Matrix(rotation);
        var p = new Vector3(position.X, position.Y, position.Z);
        return ship.Solid.Select(t => t.Select(v => Vector3.Transform(v * scale, m) + p).ToArray()).ToList();
    }

    /// <summary>
    /// Where the player stands at one of Redguard's spots over a ship put in place of its boat: on the ship's own surface
    /// there (up to 100 units over the spot: the ship's deck isn't the boat's) at the nearest place a standing body is
    /// clear (Morrowind's ClearSpot), else the spot as it is. Redguard's arrival at the docks stood 25 units under the deck
    /// of the ship that replaced Brennan's boat (2026-11-04, user: "redguard i cant move on start").
    /// </summary>
    Vector3 OnShip(Scene scene, Vector3 at, Action<string>? log)
    {
        if (ship is null) return at;
        foreach (var (_, r) in placedShips.Where(s => s.Scene == scene.Name))
        {
            float scale = r.Scale ?? 1;
            var c = r.Placement!.Position;
            // A spot on Redguard's boat goes with the ship moored in its place (2026-11-10, in game: the docks' arrival stayed
            // where the boat had lain, 620 units from the ship moved to the dock, in the air over the harbour: the player
            // dropped into the sea), then the spot as it is.
            var move = shipMoves.GetValueOrDefault(r.FormKey);
            foreach (var spot in move == Vector2.Zero ? new[] { at } : new[] { new Vector3(at.X + move.X, at.Y + move.Y, at.Z), at })
            {
                if (Vector2.Distance(new Vector2(c.X, c.Y), new Vector2(spot.X, spot.Y)) > Skyrim.Meshes.ShipModel.Length * scale / 2) continue;
                var solid = ShipSolid(ship, r.Placement.Position, r.Placement.Rotation, scale);
                // From the deck's height where the spot is under it (the ship's deck is far over the boat's).
                var from = new Vector3(spot.X, spot.Y, MathF.Max(spot.Z, c.Z + ship.Deck * scale - 60));
                if (Morrowind.MwPluginBuilder.ClearSpot(from, solid, above: 100) is { } landed)
                {
                    log?.Invoke($"start: on the ship at {landed} (Redguard's spot {at}: {Morrowind.MwPluginBuilder.BodyHits(at, solid)} collision points in a standing body, landed {Morrowind.MwPluginBuilder.BodyHits(landed, solid)})");
                    return landed;
                }
            }
        }
        return at;
    }

    /// <summary>Where the opening ends: Stros M'Kai's docks, where the old start was.</summary>
    void PlaceDocksArrival(Scene scene, Vector3 at, float facing, Action<string>? log = null)
    {
        at = OnShip(scene, at, log);
        docksArrival = new PlacedObject(mod)
        {
            EditorID = "RGOpeningDocks",
            Base = new FormLinkNullable<IPlaceableObjectGetter>(XMarkerHeading),
            Placement = new Placement { Position = new P3Float(at.X, at.Y, at.Z + 8), Rotation = new P3Float(0, 0, facing) },
            SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)0x400,
        };
        PersistentCellFor(scene).Persistent.Add(docksArrival);
    }

    /// <summary>The opening, once the scene's ships are placed and the dialogue quest exists; false when it can't be
    /// built (no START scene or no ship), so the story starts at the docks.</summary>
    bool BuildOpening(Action<string>? log)
    {
        if (!scenes.TryGetValue(OpeningScene, out var scene) || scene.World is null || docksArrival is null || dialogueQuest is null) return false;
        if (!shipsByScript.TryGetValue((OpeningScene, "SBOAT"), out var boat) || ship is null) { Problems.Add("opening: Brennan's boat not placed"); return false; }
        var data = scene.Data;
        Vector3? Where(string script) => data.Objects.FirstOrDefault(o => o.Script.Equals(script, StringComparison.OrdinalIgnoreCase)) is { } o ? At(scene, o.Position) : null;
        float DeckOf(PlacedObject r, float scale) => r.Placement!.Position.Z + ship.Deck * scale + 8;
        // On whichever boat stands nearest.
        (PlacedObject Ref, float Scale) BoatNear(Vector3 p) => shipsByScript.Where(kv => kv.Key.Scene == OpeningScene).Select(kv => kv.Value)
            .OrderBy(b => Vector2.Distance(new Vector2(b.Ref.Placement!.Position.X, b.Ref.Placement.Position.Y), new Vector2(p.X, p.Y))).First();

        // Actors: two boarders (hostile), Brennan, Vandar and his crew (shown later; they keep to their boat).
        Npc Copy(string template, string edid, string name, bool hostile)
        {
            var t = vanilla.Get<INpcGetter>(template) ?? throw new InvalidOperationException($"template {template} not found");
            var npc = mod.Npcs.DuplicateInAsNewRecord(t);
            npc.EditorID = edid;
            npc.Name = name;
            // Its face the vanilla actor's own (a copy has none: the boarders were black-faced, 2026-11-10 in game), its AI
            // its own (the template's was the vanilla actor's, whatever was set here).
            VanillaActors.Lookalike(npc, t, ownAI: true);
            npc.AIData ??= new AIData();
            // Very aggressive, as Skyrim's own bandits are: its bandit faction is no enemy of the player's (the console's
            // GetFactionRelation gave 0 for a boarder), so a merely aggressive boarder stood on the deck muttering and the
            // opening, which waits for both to be dead or in the sea, never went on (seen in a new game, 2026-11-12; the
            // vanilla actor's own AI had applied until the copies got their own on 2026-11-10).
            npc.AIData.Aggression = hostile ? Aggression.VeryAggressive : Aggression.Unaggressive;
            npc.AIData.Confidence = hostile ? Confidence.Brave : Confidence.Average;
            npc.AIData.Assistance = hostile ? Assistance.HelpsAllies : Assistance.HelpsNobody;
            return npc;
        }
        var boarder = Copy("EncBandit02Melee1HRedguardM", "RGOpeningPirate", "Pirate", hostile: true);
        var crew = Copy("EncSailorRedguardM", "RGOpeningVandarCrew", "League Pirate", hostile: false);
        var brennanNpc = PersonNpc("SBRENNAN", People["BRENNAN"]);
        var vandarNpc = PersonNpc("SVANDAR", People["VANDAR"]);
        if (brennanNpc is null || vandarNpc is null) { Problems.Add("opening: Brennan or Vandar couldn't be made"); return false; }
        brennanNpc.EditorID = "RGOpeningBrennan";
        vandarNpc.EditorID = "RGOpeningVandar";
        vandarNpc.Packages.Clear();   // stays where he stands on his boat
        brennanNpc.Packages.Clear();

        var cell = PersistentCellFor(scene);
        PlacedNpc Put(Npc npc, string script, bool disabled, Vector3? at = null)
        {
            var p = at ?? Where(script) ?? new Vector3(boat.Ref.Placement!.Position.X, boat.Ref.Placement.Position.Y, boat.Ref.Placement.Position.Z);
            var on = BoatNear(p);
            float yaw = data.Objects.FirstOrDefault(o => o.Script.Equals(script, StringComparison.OrdinalIgnoreCase)) is { } o ? RgSpace.Euler(o.Rotation).Z : 0;
            var r = new PlacedNpc(mod)
            {
                Base = new FormLinkNullable<INpcGetter>(npc.FormKey),
                Placement = new Placement { Position = new P3Float(p.X, p.Y, DeckOf(on.Ref, on.Scale)), Rotation = new P3Float(0, 0, yaw) },
                SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)(0x400 | (disabled ? 0x800 : 0)),
            };
            cell.Persistent.Add(r);
            return r;
        }
        var boarders = new[] { Put(boarder, "SPIRATE1", false), Put(boarder, "SPIRATE2", false) };
        // Brennan comes up out of his ship's hold: he appears on its hatch (RgHolds) and walks over (TMRgOpening).
        // ...out of his quarters when the ship has them (user, 2026-11-10: "the captain suppose to come out of catains
        // quarters of the ship"): before the cabin door; else on the hold's hatch.
        Vector3? hatchAt = cabinOf.TryGetValue(boat.Ref.FormKey, out var cabin) ? cabin.Front
            : hatchOf.TryGetValue(boat.Ref.FormKey, out var hatch) ? new Vector3(hatch.Placement!.Position.X, hatch.Placement.Position.Y, hatch.Placement.Position.Z) : null;
        var brennan = Put(brennanNpc, "SBRENNAN", true, hatchAt);
        if (hatchAt is null) Problems.Add("opening: Brennan's ship has neither quarters nor a hatch; he appears where Redguard stood him");
        var vandarCrew = new[] { Put(vandarNpc, "SVANDAR", true), Put(crew, "SPIRATE3", true), Put(crew, "SPIRATE4", true), Put(crew, "SPIRATE5", true) };

        // Vandar's boat: hidden until Brennan points at it, then sailing south (MoveObjectAxis(2, -1024000, ..): away
        // down the scene's z, Skyrim's -y).
        PlacedObject? vandarShip = null;
        if (shipsByScript.TryGetValue((OpeningScene, "VANDBOAT"), out var vb))
        {
            vandarShip = vb.Ref;
            foreach (var c in scene.Cells.Values) c.Temporary.Remove(vandarShip);
            vandarShip.SkyrimMajorRecordFlags = (SkyrimMajorRecord.SkyrimMajorRecordFlag)(0x400 | 0x800);
            cell.Persistent.Add(vandarShip);
        }

        // The start: on Brennan's deck, by the boat's middle, facing the boarders.
        var bp = boat.Ref.Placement!.Position;
        var mid = boarders.Aggregate(Vector2.Zero, (a, r) => a + new Vector2(r.Placement!.Position.X, r.Placement.Position.Y)) / boarders.Length;
        var from = new Vector2(bp.X, bp.Y);
        var toward = mid - from;
        if (toward.LengthSquared() < 1) toward = Vector2.UnitY;
        var stand = from - Vector2.Normalize(toward) * MathF.Min(200, toward.Length());
        var standAt = new Vector3(stand.X, stand.Y, DeckOf(boat.Ref, boat.Scale));
        // Where Redguard itself starts Cyrus (WORLD.INI's start_marker of the START map), on the ship in its boat's
        // place (user, 2026-11-12: "the start is near the hull like in redguard"; it had been a place of ours by the
        // boat's middle): the marker's own spot when it is over the ship, set on the deck there.
        if (ini.StartMarker >= 0 && ini.StartMarker < data.Markers.Count)
        {
            var own = At(scene, data.Markers[ini.StartMarker]);
            float reach = Skyrim.Meshes.ShipModel.Length * boat.Scale / 2;
            if (Vector2.Distance(new Vector2(own.X, own.Y), from) <= reach)
            {
                var landed = OnShip(scene, own, log);
                standAt = new Vector3(landed.X, landed.Y, MathF.Max(landed.Z, DeckOf(boat.Ref, boat.Scale) - 8) + 8);
                var look = mid - new Vector2(standAt.X, standAt.Y);
                if (look.LengthSquared() > 1) toward = look;
                log?.Invoke($"opening: Cyrus starts at Redguard's own marker {ini.StartMarker} ({own.X:F0}, {own.Y:F0}), on the deck at {standAt.X:F0}, {standAt.Y:F0}, {standAt.Z:F0}, {Vector2.Distance(new Vector2(standAt.X, standAt.Y), from):F0} from the ship's middle");
            }
            else log?.Invoke($"opening: Redguard's start marker {ini.StartMarker} at {own.X:F0}, {own.Y:F0} is {Vector2.Distance(new Vector2(own.X, own.Y), from):F0} from the ship's middle (not on it): the start stays by the middle");
        }
        else log?.Invoke($"opening: no start marker {ini.StartMarker} among START's {data.Markers.Count}: the start stays by the ship's middle");
        StartMarker.Placement = new Placement
        {
            Position = new P3Float(standAt.X, standAt.Y, standAt.Z),
            Rotation = new P3Float(0, 0, MathF.Atan2(toward.X, toward.Y)),
        };
        cell.Persistent.Add(StartMarker);

        // The talk: Brennan's lines as his dialogue (voiced from the RTX by rg-voices), Cyrus's as sounds.
        var topics = new List<DialogTopic>();
        var brennanWaits = new List<float>();
        var cyrusSounds = new List<SoundMarker>();
        var cyrusTexts = new List<string>();
        var cyrusWaits = new List<float>();
        string soundDir = Path.Combine(modOutDir, "sound", "fx", "tamrielmerge", "rg", "opening");
        Directory.CreateDirectory(soundDir);
        foreach (var (tag, cyrus) in OpeningTalk)
        {
            if (!rtx!.Entries.TryGetValue(tag, out var e)) { Problems.Add($"opening: line {tag} not in ENGLISH.RTX"); continue; }
            string text = Text(BitConverter.ToInt32(System.Text.Encoding.Latin1.GetBytes(tag))) ?? e.Text;
            float seconds = e.AudioLength > 0 && e.SampleRate > 0 ? e.AudioLength / (float)(e.SampleRate * Math.Max(1, e.Bits / 8)) : text.Length / 14f;
            if (cyrus)
            {
                string wav = Path.Combine(soundDir, tag + ".wav");
                string raw = Path.Combine(Path.GetTempPath(), $"tm_rg_{tag}.raw.wav");
                if (rtx.WriteWav(e, raw))
                {
                    using (var reader = new WaveFileReader(raw))
                    using (var resampler = new MediaFoundationResampler(reader, new WaveFormat(44100, 16, 1)) { ResamplerQuality = 60 })
                        WaveFileWriter.CreateWaveFile(wav, resampler);
                    File.Delete(raw);
                }
                // A copy of a vanilla wav descriptor (a made-up category link crashed the game at startup), in the
                // voice category (AudioCategoryVOCGeneral).
                var descriptor = mod.SoundDescriptors.DuplicateInAsNewRecord(SkyrimEsm.SoundDescriptors.First(d => d.EditorID == "VOCShoutFXThrowVoice"));
                descriptor.EditorID = $"RGOpeningCyrus{tag}";
                descriptor.SoundFiles.Clear();
                descriptor.SoundFiles.Add(new Mutagen.Bethesda.Plugins.Assets.AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType>(Path.Combine("fx", "tamrielmerge", "rg", "opening", tag + ".wav")));
                descriptor.Category = new FormLinkNullable<ISoundCategoryGetter>(SkyrimEsm.SoundCategories.First(c => c.EditorID == "AudioCategoryVOCGeneral").FormKey);
                var marker = new SoundMarker(mod) { EditorID = $"RGOpeningCyrus{tag}Sound", SoundDescriptor = descriptor.ToNullableLink<ISoundDescriptorGetter>() };
                mod.SoundMarkers.Add(marker);
                cyrusSounds.Add(marker);
                cyrusTexts.Add(text);
                cyrusWaits.Add(seconds + 0.6f);
            }
            else
            {
                var topic = new DialogTopic(mod)
                {
                    EditorID = $"RGOpeningBrennan{tag}",
                    Quest = dialogueQuest.ToNullableLink<IQuestGetter>(),
                    Priority = 50,
                    Category = DialogTopic.CategoryEnum.Topic,
                    Subtype = DialogTopic.SubtypeEnum.Custom,
                    Responses = [],
                };
                var said = new GetIsIDConditionData();
                said.Object = new FormLinkOrIndex<IReferenceableObjectGetter>(said, brennanNpc.FormKey);
                var r = new DialogResponses(mod)
                {
                    Conditions = [new ConditionFloat { Data = said, CompareOperator = CompareOperator.EqualTo, ComparisonValue = 1 }],
                    Responses = [new DialogResponse { Text = text, ResponseNumber = 1, Emotion = Emotion.Neutral, EmotionValue = 50 }],
                    Flags = new DialogResponseFlags(),
                };
                topic.Responses.Add(r);
                mod.DialogTopics.Add(topic);
                if (e.AudioLength > 0) VoiceMap.Add(new VoiceLine(r.FormKey.ID, 1, tag));
                topics.Add(topic);
                brennanWaits.Add(seconds + 0.6f);
            }
        }

        static ScriptObjectProperty Obj(string name, IMajorRecordGetter r) => new() { Name = name, Object = new FormLink<ISkyrimMajorRecordGetter>(r.FormKey) };
        static ScriptObjectListProperty Objs(string name, IEnumerable<IMajorRecordGetter> rs)
        {
            var l = new ScriptObjectListProperty { Name = name };
            foreach (var r in rs) l.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(r.FormKey) });
            return l;
        }
        var entry = new ScriptEntry { Name = "TMRgOpening" };
        entry.Properties.Add(Objs("Boarders", boarders));
        entry.Properties.Add(Obj("Brennan", brennan));
        entry.Properties.Add(Objs("VandarCrew", vandarCrew));
        if (vandarShip is not null) entry.Properties.Add(Obj("VandarShip", vandarShip));
        entry.Properties.Add(Objs("BrennanLines", topics));
        entry.Properties.Add(new ScriptFloatListProperty { Name = "BrennanWaits", Data = [.. brennanWaits] });
        entry.Properties.Add(Objs("CyrusLines", cyrusSounds));
        entry.Properties.Add(new ScriptStringListProperty { Name = "CyrusTexts", Data = [.. cyrusTexts] });
        entry.Properties.Add(new ScriptFloatListProperty { Name = "CyrusWaits", Data = [.. cyrusWaits] });
        entry.Properties.Add(new ScriptFloatProperty { Name = "SailAway", Data = 6000 });
        entry.Properties.Add(Obj("Docks", docksArrival));
        StartMarker.VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] };
        log?.Invoke($"opening: Brennan's boat at {bp.X:F0}, {bp.Y:F0}; 2 boarders, Brennan, Vandar's boat and 4 aboard; {topics.Count} lines for Brennan, {cyrusSounds.Count} for Cyrus");
        return true;
    }
}
