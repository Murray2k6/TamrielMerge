using System.Numerics;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim;

/// <summary>
/// Daggerfall's townsfolk (the people sprites in RMB building interiors and streets) as Skyrim NPCs, identified the
/// way Daggerfall Unity identifies them (StaticNPC, TalkManager; MIT):
/// <list type="bullet">
/// <item>faction: the sprite's faction, or the region's court in a palace and its people elsewhere; a named
/// individual (faction type Individual, e.g. King Gothryd) keeps that faction's name;</item>
/// <item>sex: flag 0x20 is female; race: the faction's race, else the region's (Breton or Redguard);</item>
/// <item>name: Daggerfall's own, from NAMEGEN.DAT seeded with record position ^ (building key + location index),
/// so a town's people are named as in the game;</item>
/// <item>look: a vanilla face of that race and sex (its traits template), dressed by social group and workplace.</item>
/// </list>
/// Each NPC's Daggerfall faction, social group and building are kept (<see cref="Person"/>) for conversations.
/// </summary>
public sealed class TownPeople(SkyrimMod mod, VanillaActors vanilla, DfFactions factions, DfNames names)
{
    public DfFactions Factions => factions;
    static FormKey Sk(uint id) => new(ModKey.FromFileName("Skyrim.esm"), id);
    static readonly FormKey StayPackage = Sk(0x025BFC), SandboxSmall = Sk(0x09361E), SandboxMedium = Sk(0x01B217);

    public sealed record Person(Npc Npc, DfFaction? Faction, DfFaction? Effective, DfSocialGroup Group, DfBuildingType Building, string Name);

    static readonly Dictionary<int, string> FactionRaces = new()
    {
        [0] = "NordRace", [1] = "KhajiitRace", [2] = "RedguardRace", [3] = "BretonRace", [4] = "ArgonianRace",
        [5] = "WoodElfRace", [6] = "HighElfRace", [7] = "DarkElfRace", [17] = "OrcRace",
    };

    static DfNameBank BankOf(string race) => race switch
    {
        "RedguardRace" => DfNameBank.Redguard, "NordRace" => DfNameBank.Nord, "DarkElfRace" => DfNameBank.DarkElf, "HighElfRace" => DfNameBank.HighElf,
        "WoodElfRace" => DfNameBank.WoodElf, "KhajiitRace" => DfNameBank.Khajiit, "ArgonianRace" => DfNameBank.Imperial, _ => DfNameBank.Breton,
    };

    // Vanilla outfits by what the person does.
    static readonly FormKey[] Commoner = [Sk(0x0DAB76), Sk(0x0DAB77), Sk(0x0DAB78)];       // FarmClothesOutfit02/03/04Variant
    static readonly FormKey[] Merchant = [Sk(0x095921), Sk(0x09593C)];                     // MerchantClothesOutfit02 (NoHat)
    static readonly FormKey[] Noble = [Sk(0x0F8716), Sk(0x0F8717)];                        // FineClothesOutfit01/02Variant
    static readonly FormKey Scholar = Sk(0x0E9AC4), Barkeep = Sk(0x0958AA), Smith = Sk(0x0958AB);   // robes, BarkeepClothes02, BlacksmithOutfit02

    static FormKey OutfitFor(DfSocialGroup g, DfBuildingType b, int hash) => b switch
    {
        DfBuildingType.Tavern => Barkeep,
        DfBuildingType.Armorer or DfBuildingType.WeaponSmith => Smith,
        DfBuildingType.Temple or DfBuildingType.Library => Scholar,
        _ => g switch
        {
            DfSocialGroup.Merchants => Merchant[hash % Merchant.Length],
            DfSocialGroup.Scholars => Scholar,
            DfSocialGroup.Nobility => Noble[hash % Noble.Length],
            _ => Commoner[hash % Commoner.Length],
        },
    };

    /// <summary>What a Daggerfall people sprite shows, dressed as the nearest Skyrim look.</summary>
    public enum Kind { Commoner, Barkeep, Merchant, Smith, Chef, Noble, Ruler, Knight, Guard, Mage, Monk, Priest, Shrouded, Jester, Beggar, Wench, Prisoner, Necromancer, Hunter }

    /// <summary>A sprite's look: sex shown ('M', 'F', or '?' to trust the record's flag), kind, race if it shows one.</summary>
    public sealed record Look(char Sex, Kind Kind, string? Race = null);

    static Look L(char sex, Kind kind, string? race = null) => new(sex, kind, race);

    /// <summary>
    /// Each people sprite as it looks (classified by eye from contact sheets of archives 176-177, 181-184): Common
    /// people, Nobles, Flat People II, the Dark Brotherhood, mages and temple folk.
    /// </summary>
    public static readonly Dictionary<(int Archive, int Record), Look> Looks = BuildLooks();

    static Dictionary<(int, int), Look> BuildLooks()
    {
        var d = new Dictionary<(int, int), Look>();
        void Set(int archive, string spec)
        {
            // "record:sex+kind" entries, e.g. "0:M Merchant".
            foreach (var e in spec.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = e.Split(' ');
                var head = parts[0].Split(':');
                d[(archive, int.Parse(head[0]))] = new Look(head[1][0], Enum.Parse<Kind>(parts[1]), parts.Length > 2 ? parts[2] : null);
            }
        }
        Set(182, "0:M Merchant; 1:M Barkeep; 2:M Barkeep; 3:M Barkeep; 4:M Commoner; 5:M Jester; 6:M Jester; 7:M Chef; 8:M Barkeep; 9:F Noble; 10:F Noble;"
            + "11:F Barkeep; 12:F Merchant; 13:M Beggar; 14:M Beggar; 15:M Commoner; 16:M Beggar; 17:M Commoner; 18:M Beggar; 19:M Commoner; 20:M Commoner;"
            + "21:M Beggar; 22:M Monk; 23:M Commoner; 24:M Monk; 25:M Commoner; 26:F Commoner; 27:F Commoner; 28:F Mage; 29:M Beggar; 30:M Beggar; 31:M Beggar;"
            + "32:F Wench; 33:F Wench; 34:F Wench; 35:M Commoner; 36:M Commoner; 37:M Commoner; 38:F Commoner; 39:M Commoner; 40:M Mage; 41:F Mage;"
            + "42:M Commoner; 43:M Hunter; 44:F Beggar; 45:F Commoner; 46:M Commoner; 47:F Noble; 48:F Wench; 49:M Jester; 50:F Wench; 51:M Noble;"
            + "52:M Commoner; 53:F Commoner; 54:M Monk; 55:M Monk; 56:F Monk; 57:M Prisoner; 58:M Prisoner; 59:M Smith");
        Set(183, "0:M Ruler; 1:F Noble; 2:M Knight; 3:M Knight; 4:M Guard; 5:M Monk; 6:M Ruler; 7:M Noble; 8:F Ruler; 9:F Noble; 10:M Noble; 11:F Noble;"
            + "12:M Monk; 13:M Noble; 14:F Noble; 15:F Necromancer; 16:M Noble; 17:F Monk; 18:F Noble; 19:M Commoner OrcRace; 20:M Noble; 21:F Noble");
        Set(184, "0:M Noble; 1:F Mage; 2:M Monk; 3:F Commoner; 4:M Noble; 5:F Noble; 6:F Wench; 7:F Wench; 8:F Wench; 9:F Wench; 10:F Wench; 11:F Wench;"
            + "12:F Wench; 13:F Wench; 14:F Wench; 15:M Commoner; 16:M Barkeep; 17:M Commoner; 18:F Wench; 19:F Commoner; 20:M Commoner; 21:M Necromancer;"
            + "22:F Hunter; 23:F Commoner; 24:M Commoner; 25:M Commoner; 26:F Commoner; 27:M Beggar; 28:F Commoner; 29:F Noble; 30:F Wench; 31:M Prisoner;"
            + "32:F Noble; 33:F Noble; 34:M Hunter");
        Set(176, "0:M Shrouded; 1:M Shrouded; 2:F Wench; 3:F Wench; 4:F Wench; 5:M Shrouded; 6:F Necromancer");
        Set(177, "0:M Mage; 1:M Mage; 2:M Mage; 3:F Mage; 4:M Necromancer; 5:M Smith");
        Set(181, "0:M Priest; 1:F Priest; 2:M Priest; 3:M Priest; 4:F Priest; 5:F Wench; 6:F Wench; 7:F Wench");
        // The castle courts' own sprites (dungeon blocks only): 346 Wayrest's court in Castle Wayrest (S0000080/81: King
        // Eadwyre 21, Queen Barenziah 22, Elysana 17, Helseth 16, Morgiah 0, Karethys 11, the Knights of the Rose 19, the
        // court's servants, bards and children), 178 the necromancers of Scourg Barrow (S0000205: the King of Worms 4).
        Set(346, "0:F Noble; 1:M Chef; 2:M Commoner; 3:M Jester; 4:M Chef; 5:M Chef; 6:M Commoner; 7:M Noble; 8:M Commoner; 9:F Wench; 10:F Noble;"
            + "11:F Wench; 12:M Commoner; 13:M Commoner; 14:M Mage; 15:M Commoner; 16:M Noble; 17:F Noble; 18:F Commoner; 19:M Knight; 20:M Guard;"
            + "21:M Ruler; 22:F Ruler");
        // 179, the witches' covens: two kinds of naked witch, the beldam (one a coven), a black-cowled dancer, a red-haired one.
        Set(179, "0:F Wench; 1:F Wench; 2:F Necromancer; 3:F Monk; 4:F Hunter");
        Set(178, "0:M Necromancer; 1:F Necromancer; 2:M Necromancer; 3:M Necromancer; 4:M Necromancer; 5:F Wench; 6:F Wench");
        return d;
    }

    static readonly Dictionary<Kind, FormKey[]> KindOutfits = new()
    {
        [Kind.Commoner] = Commoner,
        [Kind.Barkeep] = [Barkeep],
        [Kind.Merchant] = Merchant,
        [Kind.Smith] = [Smith],
        [Kind.Chef] = [Sk(0x01BC5E)],                               // ChefOutfit
        [Kind.Noble] = Noble,
        [Kind.Ruler] = [Sk(0x0DAB7A)],                              // JarlClothesOutfit03
        [Kind.Knight] = [Sk(0x0579A3)],                             // ArmorSteelPlateNoHelmetOutfit
        [Kind.Guard] = [Sk(0x0AD23F)],                              // ArmorImperialLightOutfitNoHelmet
        [Kind.Mage] = [Sk(0x0E9AC4), Sk(0x0D3E06), Sk(0x0D3E04)],    // mage robes, college adept robes
        [Kind.Monk] = [Sk(0x0C2A5A)],                               // MonkOutfit (hooded)
        [Kind.Priest] = [Sk(0x0C2A5B)],                             // MonkOutfitNoHood
        [Kind.Shrouded] = [Sk(0x0D3BF8)],                           // DBArmorWithHood
        [Kind.Jester] = [Sk(0x0CF755)],                             // DBJesterOutfit
        [Kind.Beggar] = [Sk(0x028B60), Sk(0x028B61), Sk(0x028B58)],  // BeggarOutfit, WithHat, NoShoes
        [Kind.Wench] = [Sk(0x0F966E)],                              // ClothesTavernWenchOutfit
        [Kind.Prisoner] = [Sk(0x0A1984)],                           // PrisonerPantsOutfit
        [Kind.Necromancer] = [Sk(0x09EF86)],                        // NecromancerOutfit
        [Kind.Hunter] = [Sk(0x10698C)],                             // HunterOutfit01Hooded
    };

    static bool IsShop(DfBuildingType t) => t is DfBuildingType.Alchemist or DfBuildingType.Armorer or DfBuildingType.Bookseller or DfBuildingType.ClothingStore
        or DfBuildingType.FurnitureStore or DfBuildingType.GemStore or DfBuildingType.GeneralStore or DfBuildingType.PawnShop or DfBuildingType.WeaponSmith;

    /// <summary>Daggerfall Unity's building key: (block x << 16) + (block y << 8) + record index, 0 remapped.</summary>
    public static int BuildingKey(int bx, int by, int record) { int k = (bx << 16) + (by << 8) + record; return k == 0 ? 1 << 24 : k; }

    /// <summary>The name this sprite has in this town (Daggerfall's own: same faction, sex and race rules as <see cref="Make"/>).</summary>
    public string NameIn(DfLocation loc, int bx, int by, int buildingIndex, DfBuildingType buildingType, DfRdbObject flat, int recordOffset)
    {
        var (own, _, _, female, race) = Identity(loc, buildingType, flat);
        if (own is { Type: DfFactionType.Individual }) return own.Name;
        int seed = recordOffset ^ (BuildingKey(bx, by, buildingIndex) + loc.Index);
        return names.FullName(BankOf(race), female, new DfRandom((uint)seed));
    }

    /// <summary>The race a sprite has in a town (its look's, its faction's, else the region's).</summary>
    public string RaceIn(DfLocation loc, DfBuildingType buildingType, DfRdbObject flat) => Identity(loc, buildingType, flat).Race;

    (DfFaction? Own, DfFaction? Effective, DfSocialGroup Group, bool Female, string Race) Identity(DfLocation loc, DfBuildingType buildingType, DfRdbObject flat)
    {
        int factionId = flat.FactionOrMobileId;
        var own = factions[factionId];
        if (factionId == 0 || own is null)
            own = factions.RegionFaction(loc.Region, buildingType == DfBuildingType.Palace ? DfFactionType.Courts : DfFactionType.People);
        var effective = own is null ? null : factions.Effective(own.Id);
        var group = effective is { SGroup: >= DfSocialGroup.Commoners and < DfSocialGroup.SGroup5 } ? effective.SGroup
                  : effective is null ? DfSocialGroup.Commoners : DfSocialGroup.Merchants;
        if (IsShop(buildingType) && group == DfSocialGroup.Commoners) group = DfSocialGroup.Merchants;
        var look = Looks.GetValueOrDefault((flat.TextureArchive, flat.TextureRecord));
        bool female = look?.Sex switch { 'F' => true, 'M' => false, _ => (flat.FlatFlags & 0x20) != 0 };
        string race = look?.Race ?? (own is { Race: >= 0 } && FactionRaces.TryGetValue(own.Race, out var fr) ? fr
                    : loc.Region < DfFactions.RegionIsRedguard.Length && DfFactions.RegionIsRedguard[loc.Region] ? "RedguardRace" : "BretonRace");
        return (own, effective, group, female, race);
    }

    // ---- The street population ----

    /// <summary>Wanderers per region: the townsfolk Daggerfall walks through its streets by day (Daggerfall Unity
    /// PopulationManager) are drawn from this many people of the region, each placed in many towns.</summary>
    public const int WandererSlots = 16;
    const int WandererBuilding = 253;
    static readonly FormKey SandboxLarge = Sk(0x0317C8);   // DefaultSandboxEditorLocation1024
    readonly Dictionary<(int Region, int Slot), Person?> wanderers = new();

    /// <summary>
    /// One of a region's street people (slot 0 to <see cref="WandererSlots"/> - 1; Daggerfall's mobile townsfolk are
    /// the climate's race, half of them women), or one of its guards. Made on first use with a name of the region and
    /// a wider wander than indoor folk; <paramref name="created"/> says whether this call made it.
    /// </summary>
    public Person? Wanderer(DfLocation loc, int slot, bool guard, out bool created)
    {
        created = false;
        var key = (loc.Region, guard ? -1 - slot % 4 : slot % WandererSlots);
        if (wanderers.TryGetValue(key, out var hit)) return hit;
        // A sprite record standing for the person: no faction (the region's people), a guard's look for guards.
        var flat = new DfRdbObject(DfRdbObjectType.Flat, Vector3.Zero, Vector3.Zero, 0, guard ? 183 : 0, guard ? 4 : 0,
            (ushort)(slot % 2 == 1 ? 0x20 : 0), 0, 0, false) { RecordOffset = 4096 + (key.Item2 + 8) };
        var person = Make(loc, 0, 0, WandererBuilding, DfBuildingType.None, flat, flat.RecordOffset);
        if (person is not null)
        {
            // Placed in many towns, so not unique; they wander the streets.
            person.Npc.Configuration.Flags &= ~NpcConfiguration.Flag.Unique;
            person.Npc.Packages.Clear();
            person.Npc.Packages.Add(new FormLink<IPackageGetter>(SandboxLarge));
            if (guard) person.Npc.Factions.Add(new RankPlacement { Faction = TownNights.Guards(mod, "DF").ToLink<IFactionGetter>(), Rank = 0 });
            created = true;
        }
        return wanderers[key] = person;
    }

    /// <summary>A person sprite as a Skyrim NPC (not yet placed).</summary>
    public Person? Make(DfLocation loc, int bx, int by, int buildingIndex, DfBuildingType buildingType, DfRdbObject flat, int recordOffset)
    {
        int factionId = flat.FactionOrMobileId;
        var own = factions[factionId];
        if (factionId == 0 || own is null)
            own = factions.RegionFaction(loc.Region, buildingType == DfBuildingType.Palace ? DfFactionType.Courts : DfFactionType.People);
        var effective = own is null ? null : factions.Effective(own.Id);
        var group = effective is { SGroup: >= DfSocialGroup.Commoners and < DfSocialGroup.SGroup5 } ? effective.SGroup
                  : effective is null ? DfSocialGroup.Commoners : DfSocialGroup.Merchants;
        if (IsShop(buildingType) && group == DfSocialGroup.Commoners) group = DfSocialGroup.Merchants;
        // The sprite shows who this is: its sex and dress (and a race, for the odd one) win over the record's flag.
        var look = Looks.GetValueOrDefault((flat.TextureArchive, flat.TextureRecord));
        bool female = look?.Sex switch { 'F' => true, 'M' => false, _ => (flat.FlatFlags & 0x20) != 0 };
        string race = look?.Race ?? (own is { Race: >= 0 } && FactionRaces.TryGetValue(own.Race, out var fr) ? fr
                    : loc.Region < DfFactions.RegionIsRedguard.Length && DfFactions.RegionIsRedguard[loc.Region] ? "RedguardRace" : "BretonRace");

        string name;
        if (own is { Type: DfFactionType.Individual }) name = own.Name;
        else
        {
            int seed = recordOffset ^ (BuildingKey(bx, by, buildingIndex) + loc.Index);
            name = names.FullName(BankOf(race), female, new DfRandom((uint)seed));
        }

        var raceRecord = vanilla.Get<IRaceGetter>(race) ?? vanilla.Get<IRaceGetter>("BretonRace")!;
        var pool = vanilla.Faces(raceRecord.FormKey, female);
        if (pool.Count == 0) return null;
        int hash = VanillaActors.StableHash($"{loc.Name}{bx}{by}{buildingIndex}{recordOffset}");
        var face = pool[hash % pool.Count];
        var npc = mod.Npcs.DuplicateInAsNewRecord(face);
        npc.EditorID = $"DFP{LocationPlacer.Sanitize(loc.Name)}{loc.Region}_{bx}{by}_{buildingIndex:00}_{recordOffset}";
        npc.Name = name;
        npc.ShortName = null;
        npc.VirtualMachineAdapter = null;
        npc.Factions.Clear();
        // The vanilla look's crime faction too: each game's people answer to their own factions, never Skyrim's.
        npc.CrimeFaction.Clear();
        npc.Items = null;
        npc.SleepingOutfit.Clear();
        npc.DeathItem.Clear();
        npc.Packages.Clear();
        npc.Keywords = null;
        npc.Perks = null;
        npc.Template = new FormLinkNullable<INpcSpawnGetter>(face.FormKey);
        npc.Configuration.TemplateFlags = NpcConfiguration.TemplateFlag.Traits;
        npc.Configuration.Flags = NpcConfiguration.Flag.Unique | (female ? NpcConfiguration.Flag.Female : 0)
            | (own is { Type: DfFactionType.Individual } ? NpcConfiguration.Flag.Essential : 0);
        npc.Configuration.Level = new NpcLevel { Level = (short)(group == DfSocialGroup.Nobility ? 15 : 6) };
        // Dressed as the sprite is (a barkeep's apron, a noble's gown, a knight's plate), else by trade and standing.
        npc.DefaultOutfit.SetTo(look is not null ? KindOutfits[look.Kind][hash % KindOutfits[look.Kind].Length] : OutfitFor(group, buildingType, hash));
        // Shopkeepers and court mind their place; everyone else potters about the room.
        npc.Packages.Add(new FormLink<IPackageGetter>(IsShop(buildingType) || buildingType is DfBuildingType.Palace or DfBuildingType.Bank ? StayPackage
            : buildingType == DfBuildingType.Tavern ? SandboxMedium : SandboxSmall));
        if (npc.AIData is { } ai) { ai.Aggression = Aggression.Unaggressive; ai.Confidence = Confidence.Cowardly; }
        return new Person(npc, own, effective, group, buildingType, name);
    }
}
