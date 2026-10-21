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
        CreatureKind Kind, float WalkSpeed, float RunSpeed, bool Flies, bool Swims = false, bool Walks = true);

    public enum CreatureKind { Animal, Daedra, Undead, Humanoid }

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
    public int AddOwn(string id, string name, string dataDir, Stats raceStats, IReadOnlyDictionary<string, Stats> actorStats, string torsoBone, string? headBone)
    {
        string pascal = CreatureBehavior.ProjectName(id)[2..^"Project".Length];
        string folder = CreatureRig.MeshFolder(id)["meshes\\".Length..];
        string skeleton = $@"{folder}\character assets\skeleton.nif";

        // Its own sounds.
        string soundDir = Path.Combine(dataDir, CreatureRig.SoundFolder(id));
        if (Directory.Exists(soundDir))
            foreach (var wav in Directory.GetFiles(soundDir, "*.wav").Order(StringComparer.OrdinalIgnoreCase))
            {
                var sd = new SoundDescriptor(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
                {
                    EditorID = Path.GetFileNameWithoutExtension(wav),
                    Type = SoundDescriptor.DescriptorType.Standard,
                    Category = Vanilla<ISoundCategoryGetter>("AudioCategorySFX").ToNullableLink(),
                    OutputModel = Vanilla<ISoundOutputModelGetter>("SOMMono03000").ToNullableLink(),
                    Priority = 128,
                };
                sd.SoundFiles.Add(new Mutagen.Bethesda.Plugins.Assets.AssetLink<Mutagen.Bethesda.Skyrim.Assets.SkyrimSoundAssetType>(Path.GetRelativePath(Path.Combine(dataDir, "sound"), wav)));
                mod.SoundDescriptors.Add(sd);
            }

        // Movement at its game's speeds.
        var move = new MovementType(mod.GetNextFormKey(), SkyrimRelease.SkyrimSE)
        {
            EditorID = $"TMMove{pascal}",
            Name = $"TM{pascal}",
            ForwardWalk = raceStats.WalkSpeed, ForwardRun = raceStats.RunSpeed,
            LeftWalk = raceStats.WalkSpeed, LeftRun = raceStats.RunSpeed, RightWalk = raceStats.WalkSpeed, RightRun = raceStats.RunSpeed,
            BackWalk = raceStats.WalkSpeed, BackRun = raceStats.RunSpeed,
            RotateInPlaceWalk = 180, RotateInPlaceRun = 180, RotateWhileMovingRun = 180,
            AnimationChangeThresholds = new AnimationChangeThresholds { Directional = float.MaxValue, MovementSpeed = float.MaxValue, RotationSpeed = float.MaxValue },
        };
        mod.MovementTypes.Add(move);

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
            BaseMovementDefaultSwim = move.ToNullableLink<IMovementTypeGetter>(),
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
        race.Regen[BasicStat.Stamina] = 5;
        race.MovementTypeNames.AddRange(["WALK", "RUN1", "SNEK", "BLDO", "SWIM"]);
        race.EquipmentSlots.Add(Vanilla<IEquipTypeGetter>("RightHand").ToLink());
        race.BipedObjectNames[BipedObject.Body] = "BODY";
        race.Keywords = [.. KindKeywords(raceStats.Kind).Select(k => Vanilla<IKeywordGetter>(k).ToLink())];
        foreach (var attack in CreatureGraph.AttackEvents(Directory.GetFiles(Path.Combine(dataDir, CreatureRig.MeshFolder(id), "animations"), "*.hkx").Select(Path.GetFileNameWithoutExtension).OfType<string>()))
            race.Attacks.Add(new Attack
            {
                AttackEvent = attack,
                AttackData = new AttackData { DamageMult = 1, Chance = 1, StrikeAngle = 35, StaminaMult = 1 },
            });

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
                mod.Npcs.Add(o);
                switched++;
            }
        return switched;
    }

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
