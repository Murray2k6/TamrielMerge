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

    /// <summary>A creature's numbers as its game has them (Morrowind's NPDT, AIDT and flags; the speeds its settings give).</summary>
    public sealed record Stats(int Health, int Magicka, int Stamina, float AttackMin, float AttackMax, float Reach, int Flee,
        CreatureKind Kind, float WalkSpeed, float RunSpeed, bool Flies, bool Swims = false, bool Walks = true)
    {
        /// <summary>Swimming speed (Morrowind's: MwCreatureSource.SwimSpeed); NaN: as it walks.</summary>
        public float SwimSpeed { get; init; } = float.NaN;
        /// <summary>Agility (Morrowind's knockdown odds on each hit); -1: none (no knockdowns from its game).</summary>
        public int Agility { get; init; } = -1;
        /// <summary>How much stamina comes back a second (Morrowind's fatigue return); NaN: Skyrim's own.</summary>
        public float StaminaReturn { get; init; } = float.NaN;
        /// <summary>Which of its model's idle sets it picks idles by (CreatureRig.IdleSets).</summary>
        public int IdleSet { get; init; }
        /// <summary>Each of its attacks' average blow (Morrowind's chop, slash and thrust: attack 1, 2, 3); null: one blow for all.</summary>
        public float[]? AttackAverages { get; init; }
    }

    public enum CreatureKind { Animal, Daedra, Undead, Humanoid }

    /// <summary>Its game's rules for a blow that lands (Morrowind's fKnockDownMult, iKnockDownOddsMult, iKnockDownOddsBase):
    /// with them its race gets the ability that flinches and knocks it down as its game does (TMMwCreatureHits.psc).</summary>
    public sealed record HitRules(float KnockDownMult, int KnockDownOddsMult, int KnockDownOddsBase)
    {
        /// <summary>Its game's spells (by the converted EditorID) whose first effect is cast on oneself, and by touch: the
        /// rest are cast at a target (its cast animation is that range's).</summary>
        public IReadOnlySet<string> SelfSpells { get; init; } = new HashSet<string>();
        public IReadOnlySet<string> TouchSpells { get; init; } = new HashSet<string>();
        /// <summary>Its game's storm weathers (EditorID prefixes in the game plugin): those blowing from Red Mountain (ash,
        /// blight) and the rest (a blizzard's, along +Y).</summary>
        public IReadOnlyList<string> AshStorms { get; init; } = [];
        public IReadOnlyList<string> OtherStorms { get; init; } = [];
    }

    public const string HitScript = "TMMwCreatureHits";

    T Vanilla<T>(string editorId) where T : class, IMajorRecordGetter =>
        masters.SelectMany(m => m.EnumerateMajorRecords<T>()).FirstOrDefault(r => r.EditorID == editorId) ?? throw new InvalidDataException($"{editorId} not in the masters");

    /// <summary>
    /// Gives the creature <paramref name="id"/> records of its own, nothing taken from another creature (user, 2026-10-21:
    /// "ice wraith and cliffracer arent the same"): a race made here (its skeleton and own behaviour; its attacks, one per
    /// attack clip, named as <see cref="CreatureGraph"/> names them; its game's health, magicka and stamina as the race's
    /// start, its average attack as unarmed damage, its reach; flesh that bleeds; no abilities), its body parts (torso and
    /// head on its own bones), a movement type at its game's speeds, its own voice type (no other creature's grunts),
    /// class and combat style, a sound descriptor for each of its sounds (sound\fx\tamrielmerge\creatures\&lt;id&gt;\*.wav
    /// under <paramref name="dataDir"/>: the names its animations play), and overrides of the game plugin's actors
    /// (<paramref name="actorStats"/>, by EditorID) with that race, skin, voice, class and combat style, no stand-in's
    /// sounds, faction or death item, and their own health, magicka and stamina (offsets from the race's), a stronger or
    /// weaker attack than the race's through an ability. Returns how many actors were switched.
    /// </summary>
    /// <param name="hits">Its game's rules for a blow that lands (Morrowind's): its race then flinches and is knocked down and
    /// out as there (<see cref="HitScript"/>, which also gives each actor its idle set).</param>
    public int AddOwn(string id, string name, string dataDir, Stats raceStats, IReadOnlyDictionary<string, Stats> actorStats, string torsoBone, string? headBone, HitRules? hits = null)
    {
        string pascal = CreatureBehavior.ProjectName(id)[2..^"Project".Length];
        string folder = CreatureRig.MeshFolder(id)["meshes\\".Length..];
        string skeleton = $@"{folder}\character assets\skeleton.nif";

        // Its own sounds.
        string soundDir = Path.Combine(dataDir, CreatureRig.SoundFolder(id));
        if (Directory.Exists(soundDir))
            // One descriptor per sound name; "Name#1", "Name#2"... are its files, one picked at random each time it plays (a
            // Daggerfall creature's attract sound: four barks and a move for its 80 / 20, silences for how often).
            foreach (var group in Directory.GetFiles(soundDir, "*.wav").Order(StringComparer.OrdinalIgnoreCase).GroupBy(f => Path.GetFileNameWithoutExtension(f).Split('#')[0], StringComparer.OrdinalIgnoreCase))
            {
                var sd = new SoundDescriptor(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
                {
                    EditorID = group.Key,
                    Type = SoundDescriptor.DescriptorType.Standard,
                    Category = Vanilla<ISoundCategoryGetter>("AudioCategorySFX").ToNullableLink(),
                    OutputModel = Vanilla<ISoundOutputModelGetter>("SOMMono03000").ToNullableLink(),
                    Priority = 128,
                };
                foreach (var wav in group)
                    sd.SoundFiles.Add(new Mutagen.Bethesda.Plugins.Assets.AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType>(Path.GetRelativePath(Path.Combine(dataDir, "sound"), wav)));
                mod.SoundDescriptors.Add(sd);
            }

        // Movement at its game's speeds: sideways at Morrowind's strafing three quarters (OpenMW's Actor::getCurrentSpeed),
        // and swimming at its own (Morrowind's: its walk times the swimming settings and its Athletics).
        MovementType Move(string key, float walk, float run)
        {
            var m = new MovementType(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
            {
                EditorID = $"TMMove{key}{pascal}",
                Name = $"TM{key}{pascal}",
                ForwardWalk = walk, ForwardRun = run,
                LeftWalk = walk * MwCreatureSource.StrafeFactor, LeftRun = run * MwCreatureSource.StrafeFactor,
                RightWalk = walk * MwCreatureSource.StrafeFactor, RightRun = run * MwCreatureSource.StrafeFactor,
                BackWalk = walk, BackRun = run,
                RotateInPlaceWalk = 180, RotateInPlaceRun = 180, RotateWhileMovingRun = 180,
                AnimationChangeThresholds = new AnimationChangeThresholds { Directional = float.MaxValue, MovementSpeed = float.MaxValue, RotationSpeed = float.MaxValue },
            };
            mod.MovementTypes.Add(m);
            return m;
        }
        var move = Move("", raceStats.WalkSpeed, raceStats.RunSpeed);
        var swimMove = float.IsNaN(raceStats.SwimSpeed) ? move : Move("Swim", raceStats.SwimSpeed, raceStats.SwimSpeed);

        var voice = new VoiceType(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = $"TMVoice{pascal}" };
        mod.VoiceTypes.Add(voice);

        // Body parts: what hits, magic and the camera aim at.
        var parts = new BodyPartData(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = $"TMBodyParts{pascal}", Model = new Model { File = skeleton } };
        BodyPart Part(string partName, string node, BodyPart.PartType type) => new()
        {
            Name = partName, PartNode = node, VatsTarget = node, IkStartNode = "", GoreTargetBone = "", LimbReplacementModel = "",
            Type = type, DamageMult = 1, HealthPercent = 100, ToHitChance = 100, ActorValue = ActorValue.None,
            ExplodableDebrisScale = 1, SeverableDebrisScale = 1, LimbReplacementScale = 1,
        };
        parts.Parts.Add(Part("Torso", torsoBone, BodyPart.PartType.Torso));
        if (headBone is not null) parts.Parts.Add(Part("Head", headBone, BodyPart.PartType.Head));
        mod.BodyParts.Add(parts);

        var race = new Race(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
        {
            EditorID = $"TMRace{pascal}",
            Name = name,
            Description = "",
            // How it moves, as its game has it: a flier keeps to the ground's navmesh (its body hangs in the air in its own animations),
            // a swimmer to the water's, one that does neither (a kwama queen) stays put.
            Flags = Race.Flag.CantOpenDoors | Race.Flag.NoKnockdowns
                | (raceStats.Walks || raceStats.Flies ? Race.Flag.Walks : 0) | (raceStats.Swims ? Race.Flag.Swims : 0)
                | (!raceStats.Walks && !raceStats.Flies && !raceStats.Swims ? Race.Flag.Immobile : 0),
            BaseCarryWeight = 150,
            BaseMass = 1,
            AccelerationRate = 1,
            DecelerationRate = 1,
            Size = Size.Medium,
            HeadBipedObject = BipedObject.None,
            HairBipedObject = BipedObject.None,
            ShieldBipedObject = BipedObject.None,
            BodyBipedObject = BipedObject.None,
            UnarmedDamage = (raceStats.AttackMin + raceStats.AttackMax) / 2,
            UnarmedReach = raceStats.Reach,
            AngularAccelerationRate = 0.25f,
            AngularTolerance = 5,
            FacegenMainClamp = 5,
            FacegenFaceClamp = 3,
            Height = new GenderedItem<float>(1, 1),
            Weight = new GenderedItem<float>(1, 1),
            SkeletalModel = new GenderedItem<SimpleModel?>(new SimpleModel { File = skeleton }, new SimpleModel { File = skeleton }),
            BehaviorGraph = new GenderedItem<ModelBehavior?>(new ModelBehavior { File = $@"{folder}\{CreatureBehavior.ProjectName(id)}.hkx" }, new ModelBehavior { File = $@"{folder}\{CreatureBehavior.ProjectName(id)}.hkx" }),
            Voices = new GenderedItem<IFormLinkGetter<IVoiceTypeGetter>>(voice.ToLink<IVoiceTypeGetter>(), voice.ToLink<IVoiceTypeGetter>()),
            BodyPartData = parts.ToNullableLink<IBodyPartDataGetter>(),
            // Flesh: weapons draw blood from it (no ice, no smoke); its own blows land as claws do.
            MaterialType = Vanilla<IMaterialTypeGetter>("MaterialSkin").ToNullableLink(),
            ImpactDataSet = Vanilla<IImpactDataSetGetter>("FXMeleeClawMedium").ToNullableLink(),
            OpenLootSound = Vanilla<ISoundDescriptorGetter>("DRScBodyCreatureOpenSD").ToNullableLink(),
            CloseLootSound = Vanilla<ISoundDescriptorGetter>("DRScBodyCreatureCloseSD").ToNullableLink(),
            BaseMovementDefaultWalk = move.ToNullableLink<IMovementTypeGetter>(),
            BaseMovementDefaultRun = move.ToNullableLink<IMovementTypeGetter>(),
            BaseMovementDefaultSwim = swimMove.ToNullableLink<IMovementTypeGetter>(),
            BaseMovementDefaultSneak = move.ToNullableLink<IMovementTypeGetter>(),
            BaseMovementDefaultSprint = move.ToNullableLink<IMovementTypeGetter>(),
            UnarmedEquipSlot = Vanilla<IEquipTypeGetter>("RightHand").ToNullableLink(),
            EquipmentFlags = (EquipTypeFlag)Enum.ToObject(typeof(EquipTypeFlag), 2147475457L),
            BodyTemplate = new BodyTemplate { ArmorType = ArmorType.Clothing },
            BodyData = new GenderedItem<BodyData?>(new BodyData(), new BodyData()),
        };
        race.Starting[BasicStat.Health] = raceStats.Health;
        race.Starting[BasicStat.Magicka] = raceStats.Magicka;
        race.Starting[BasicStat.Stamina] = raceStats.Stamina;
        race.Regen[BasicStat.Health] = 0;     // Morrowind's creatures heal only by resting or magic
        race.Regen[BasicStat.Magicka] = 0;
        // Stamina comes back as its game's fatigue does (its points a second as Skyrim's percent of the most a second).
        race.Regen[BasicStat.Stamina] = StaminaRate(raceStats) ?? 5;
        race.MovementTypeNames.AddRange(["WALK", "RUN1", "SNEK", "BLDO", "SWIM"]);
        race.EquipmentSlots.Add(Vanilla<IEquipTypeGetter>("RightHand").ToLink());
        race.BipedObjectNames[BipedObject.Body] = "BODY";
        race.Keywords = [.. KindKeywords(raceStats.Kind).Select(k => Vanilla<IKeywordGetter>(k).ToLink())];
        // One that fights with weapons (Morrowind's dremora, skeletons, liches...): both hands, a shield on its SHIELD node, any
        // weapon its inventory gives it, as Skyrim's armed creatures (the draugr) have; aiming while it draws a bow.
        var clipNames = Directory.GetFiles(Path.Combine(dataDir, CreatureRig.MeshFolder(id), "animations"), "*.hkx").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToList();
        if (clipNames.Any(c => c.StartsWith("weapon_", StringComparison.OrdinalIgnoreCase)))
        {
            race.EquipmentSlots.Add(Vanilla<IEquipTypeGetter>("LeftHand").ToLink());
            race.UnarmedEquipSlot = Vanilla<IEquipTypeGetter>("EitherHand").ToNullableLink();
            race.EquipmentFlags = (EquipTypeFlag)Enum.ToObject(typeof(EquipTypeFlag), -6401L);
            race.ShieldBipedObject = BipedObject.Shield;
            race.BipedObjectNames[BipedObject.Shield] = "SHIELD";
            if (clipNames.Any(c => c.EndsWith("_draw", StringComparison.OrdinalIgnoreCase))) race.Keywords.Add(Vanilla<IKeywordGetter>("UpdateDuringArchery").ToLink());
        }
        // Its attacks, all as likely (Morrowind picks one at random), each with its own blow: Morrowind's attack 1, 2 and 3
        // (chop, slash, thrust) are its attack clips 1, 2 and 3, each its average over the race's unarmed damage (a weapon's
        // blows are the weapon's).
        float unarmed = (raceStats.AttackMin + raceStats.AttackMax) / 2;
        foreach (var (attackEvent, attackClip) in CreatureGraph.AttackList(clipNames))
        {
            float mult = 1;
            if (raceStats.AttackAverages is { } avg && unarmed > 0 && System.Text.RegularExpressions.Regex.Match(attackClip, @"^attack(\d)$") is { Success: true } am
                && int.Parse(am.Groups[1].Value) is int n and >= 1 and <= 3)
                mult = avg[n - 1] / unarmed;
            race.Attacks.Add(new Attack
            {
                AttackEvent = attackEvent,
                AttackData = new AttackData { DamageMult = mult, Chance = 1, StrikeAngle = 35, StaminaMult = 1 },
            });
        }

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

        var cls = new Class(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = $"TMClass{pascal}", Name = name, Description = "", BleedoutDefault = 0.1f };
        foreach (var sk in Enum.GetValues<Skill>()) cls.SkillWeights[sk] = 0;
        cls.StatWeights[BasicStat.Health] = 1; cls.StatWeights[BasicStat.Magicka] = 0; cls.StatWeights[BasicStat.Stamina] = 1;
        mod.Classes.Add(cls);
        // Straight at the enemy, little defence (Morrowind's creatures close in and strike), no flanking.
        var style = new CombatStyle(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
        {
            EditorID = $"TMCombat{pascal}",
            OffensiveMult = 0.75f, DefensiveMult = 0.1f, GroupOffensiveMult = 0.75f,
            EquipmentScoreMultMelee = 1, EquipmentScoreMultMagic = 1, EquipmentScoreMultRanged = 1, EquipmentScoreMultShout = 1, EquipmentScoreMultUnarmed = 1, EquipmentScoreMultStaff = 1,
            AvoidThreatChance = 0.2f,
        };
        mod.CombatStyles.Add(style);
        var creatureFaction = Vanilla<IFactionGetter>("CreatureFaction");

        int switched = 0;
        var bases = new List<(FormKey Key, Stats Stats)>();
        float raceAttack = (raceStats.AttackMin + raceStats.AttackMax) / 2;
        foreach (var game in games)
            foreach (var npc in game.Npcs.Where(n => n.EditorID is { } e && actorStats.ContainsKey(e)))
            {
                var st = actorStats[npc.EditorID!];
                var o = npc.DeepCopy();
                o.Configuration.TemplateFlags = 0;
                o.Template.Clear();
                o.Race.SetTo(race);
                o.WornArmor.SetTo(skin);
                o.AttackRace.SetTo(race);
                o.Voice.SetTo(voice);
                o.Class.SetTo(cls);
                o.CombatStyle.SetTo(style);
                o.Sound = null;               // no other creature's sounds: its own play from its animations
                o.DeathItem.Clear();          // its loot is its inventory, as in its game
                o.Factions.Clear();
                o.Factions.Add(new RankPlacement { Faction = creatureFaction.ToLink(), Rank = 0 });
                o.Keywords = null;
                o.Perks = null;
                // Its own numbers: the race's start plus offsets, not calculated from a class.
                o.Configuration.Flags &= ~NpcConfiguration.Flag.AutoCalcStats;
                o.Configuration.HealthOffset = (short)(st.Health - raceStats.Health);
                o.Configuration.MagickaOffset = (short)(st.Magicka - raceStats.Magicka);
                o.Configuration.StaminaOffset = (short)(st.Stamina - raceStats.Stamina);
                if (o.AIData is { } ai)
                    ai.Confidence = st.Flee >= 90 ? Confidence.Cowardly : st.Flee >= 70 ? Confidence.Cautious : st.Flee >= 40 ? Confidence.Average : st.Flee >= 10 ? Confidence.Brave : Confidence.Foolhardy;
                float attack = (st.AttackMin + st.AttackMax) / 2;
                if (raceAttack > 0 && MathF.Abs(attack / raceAttack - 1) > 0.01f)
                {
                    o.ActorEffect ??= [];
                    o.ActorEffect.Add(AttackAbility($"{pascal}{o.EditorID}", attack / raceAttack).ToLink<ISpellRecordGetter>());
                }
                // Faster or slower than the race's first creature (its own Speed): its speed through SpeedMult.
                if (raceStats.WalkSpeed > 0 && MathF.Abs(st.WalkSpeed / raceStats.WalkSpeed - 1) > 0.005f)
                {
                    o.ActorEffect ??= [];
                    o.ActorEffect.Add(ValueAbility($"Speed{pascal}{o.EditorID}", ActorValue.SpeedMult, (st.WalkSpeed / raceStats.WalkSpeed - 1) * 100).ToLink<ISpellRecordGetter>());
                }
                // Its own fatigue return where it isn't the race's.
                if (StaminaRate(st) is float rate && StaminaRate(raceStats) is float raceRate && MathF.Abs(rate - raceRate) > 0.01f)
                {
                    o.ActorEffect ??= [];
                    o.ActorEffect.Add(ValueAbility($"Stamina{pascal}{o.EditorID}", ActorValue.StaminaRate, rate - raceRate).ToLink<ISpellRecordGetter>());
                }
                mod.Npcs.Add(o);
                bases.Add((o.FormKey, st));
                switched++;
            }

        // Struck as in its game, and its idles by its own chances (TMMwCreatureHits): the race's ability, knowing each of its
        // creatures' agility and idle set.
        if (hits is not null)
        {
            var entry = new ScriptEntry { Name = HitScript };
            var list = new ScriptObjectListProperty { Name = "Bases" };
            foreach (var (key, _) in bases) list.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(key) });
            entry.Properties.Add(list);
            entry.Properties.Add(new ScriptIntListProperty { Name = "Agility", Data = [.. bases.Select(b => b.Stats.Agility)] });
            entry.Properties.Add(new ScriptIntListProperty { Name = "IdleSet", Data = [.. bases.Select(b => b.Stats.IdleSet)] });
            entry.Properties.Add(new ScriptFloatProperty { Name = "KnockDownMult", Data = hits.KnockDownMult });
            entry.Properties.Add(new ScriptIntProperty { Name = "KnockDownOddsMult", Data = hits.KnockDownOddsMult });
            entry.Properties.Add(new ScriptIntProperty { Name = "KnockDownOddsBase", Data = hits.KnockDownOddsBase });
            // Its creatures' spells by Morrowind's range, and its game's weathers when it faces into storms (it has IdleStorm).
            var spellKeys = mod.Npcs.Where(n => bases.Any(b => b.Key == n.FormKey)).SelectMany(n => n.ActorEffect ?? []).Select(l => l.FormKey).Distinct().ToList();
            var spellIds = games.SelectMany(g => g.Spells).Where(sp => spellKeys.Contains(sp.FormKey)).ToDictionary(sp => sp.FormKey, sp => sp.EditorID ?? "");
            ScriptObjectListProperty Spells(string propName, IReadOnlySet<string> ids)
            {
                var p = new ScriptObjectListProperty { Name = propName };
                foreach (var (key, edid) in spellIds) if (ids.Contains(edid)) p.Objects.Add(new ScriptObjectProperty { Object = new FormLink<ISkyrimMajorRecordGetter>(key) });
                return p;
            }
            entry.Properties.Add(Spells("SelfSpells", hits.SelfSpells));
            entry.Properties.Add(Spells("TouchSpells", hits.TouchSpells));
            if (clipNames.Contains("idlestorm", StringComparer.OrdinalIgnoreCase) && hits.AshStorms.Count > 0)
            {
                entry.Properties.Add(new ScriptObjectProperty { Name = "AshStorms", Object = new FormLink<ISkyrimMajorRecordGetter>(StormList("TMStormsFromRedMountain", hits.AshStorms).FormKey) });
                if (hits.OtherStorms.Count > 0)
                    entry.Properties.Add(new ScriptObjectProperty { Name = "OtherStorms", Object = new FormLink<ISkyrimMajorRecordGetter>(StormList("TMStormsOther", hits.OtherStorms).FormKey) });
            }
            var effect = new MagicEffect(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
            {
                EditorID = $"TMHitsFx{pascal}",
                Name = "Struck",
                Flags = MagicEffect.Flag.Recover | MagicEffect.Flag.HideInUI | MagicEffect.Flag.NoHitEvent | MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoArea,
                Archetype = new MagicEffectArchetype(MagicEffectArchetype.TypeEnum.Script),
                CastType = CastType.ConstantEffect,
                TargetType = TargetType.Self,
                VirtualMachineAdapter = new VirtualMachineAdapter { Scripts = [entry] },
            };
            mod.MagicEffects.Add(effect);
            var spell = new Spell(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
            {
                EditorID = $"TMHitsAb{pascal}",
                Name = "Struck",
                Type = SpellType.Ability,
                CastType = CastType.ConstantEffect,
                TargetType = TargetType.Self,
                Effects = [new Effect { BaseEffect = effect.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData() }],
            };
            mod.Spells.Add(spell);
            race.ActorEffect ??= [];
            race.ActorEffect.Add(spell.ToLink<ISpellRecordGetter>());
        }
        return switched;
    }

    /// <summary>A list (made once per plugin) of the game plugin's weathers whose EditorIDs start with one of
    /// <paramref name="prefixes"/>.</summary>
    FormList StormList(string editorId, IReadOnlyList<string> prefixes)
    {
        if (mod.FormLists.FirstOrDefault(f => f.EditorID == editorId) is { } made) return made;
        var list = new FormList(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE) { EditorID = editorId };
        foreach (var w in games.SelectMany(g => g.Weathers).Where(w => w.EditorID is { } e && prefixes.Any(p => e.StartsWith(p, StringComparison.OrdinalIgnoreCase))))
            list.Items.Add(new FormLink<ISkyrimMajorRecordGetter>(w.FormKey));
        mod.FormLists.Add(list);
        return list;
    }

    /// <summary>An ability adding <paramref name="magnitude"/> to an actor value (SpeedMult: a faster or slower actor).</summary>
    Spell ValueAbility(string key, ActorValue value, float magnitude)
    {
        var effect = new MagicEffect(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
        {
            EditorID = $"TMValueFx{key}",
            Name = value.ToString(),
            Flags = MagicEffect.Flag.Recover | MagicEffect.Flag.HideInUI | MagicEffect.Flag.NoHitEvent | MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoArea,
            Archetype = new MagicEffectArchetype(MagicEffectArchetype.TypeEnum.ValueModifier) { ActorValue = value },
            CastType = CastType.ConstantEffect,
            TargetType = TargetType.Self,
        };
        mod.MagicEffects.Add(effect);
        var spell = new Spell(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
        {
            EditorID = $"TMValueAb{key}",
            Name = value.ToString(),
            Type = SpellType.Ability,
            CastType = CastType.ConstantEffect,
            TargetType = TargetType.Self,
            Effects = [new Effect { BaseEffect = effect.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData { Magnitude = magnitude } }],
        };
        mod.Spells.Add(spell);
        return spell;
    }

    /// <summary>Its game's fatigue return as Skyrim's stamina rate (percent of its most a second); null when its game gives none.</summary>
    static float? StaminaRate(Stats s) => float.IsNaN(s.StaminaReturn) || s.Stamina <= 0 ? null : s.StaminaReturn / s.Stamina * 100;

    static IEnumerable<string> KindKeywords(CreatureKind kind) => kind switch
    {
        CreatureKind.Daedra => ["ActorTypeDaedra", "ActorTypeCreature"],
        CreatureKind.Undead => ["ActorTypeUndead", "ActorTypeCreature"],
        CreatureKind.Humanoid => ["ActorTypeCreature"],
        _ => ["ActorTypeAnimal", "ActorTypeCreature"],
    };

    /// <summary>An ability multiplying its attacks' damage (an actor whose game gives it harder blows than its race's).</summary>
    Spell AttackAbility(string key, float mult)
    {
        var effect = new MagicEffect(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
        {
            EditorID = $"TMAttackFx{key}",
            Name = "Attack",
            Flags = MagicEffect.Flag.Recover | MagicEffect.Flag.HideInUI | MagicEffect.Flag.NoHitEvent | MagicEffect.Flag.NoDuration | MagicEffect.Flag.NoArea,
            Archetype = new MagicEffectArchetype(MagicEffectArchetype.TypeEnum.ValueModifier) { ActorValue = ActorValue.AttackDamageMult },
            CastType = CastType.ConstantEffect,
            TargetType = TargetType.Self,
        };
        mod.MagicEffects.Add(effect);
        var spell = new Spell(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
        {
            EditorID = $"TMAttackAb{key}",
            Name = "Attack",
            Type = SpellType.Ability,
            CastType = CastType.ConstantEffect,
            TargetType = TargetType.Self,
            Effects = [new Effect { BaseEffect = effect.ToNullableLink<IMagicEffectGetter>(), Data = new EffectData { Magnitude = mult - 1 } }],
        };
        mod.Spells.Add(spell);
        return spell;
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
