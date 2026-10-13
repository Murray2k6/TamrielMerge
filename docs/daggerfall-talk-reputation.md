# Daggerfall talk, reaction and reputation (reference for the converter)

Summarised from Daggerfall Unity's source (MIT; `Game/TalkManager.cs`, `Game/Player/PersistentFactionData.cs`,
`API/FactionFile.cs`, `Game/StaticNPC.cs`, `Game/MobilePersonNPC.cs`, `Game/BuildingDirectory.cs`,
`Game/Utility/NameHelper.cs`, `Game/Entities/PlayerEntity.cs`). TM = TalkManager.cs.

## NPC faction and social group
- Mobile (wandering) NPCs: faction "People of <region>" (type 15 People, sgroup 0 Commoners, ggroup 4).
- Static NPCs (RMB people flats): factionID 0 -> Court of region in a Palace, else People of region;
  Random_Ruler/Noble/Knight -> Court. Then walk up parents while type is not Group 2, Province 7, Temple 9,
  People 15, Courts 14 or Individual 4. Social group = sgroup < 5 ? sgroup : Merchants.

## Reaction
- Base: `reaction = faction.rep + BiographyReactionMod + reactionMods[sgroup] + SGroupReputations[sgroup]`
  (reactionMods: GoodRepWith +10, BadRepWith -10 enchantments, Masque of Clavicus +Personality/5).
- `reaction < -20` (or rejected once already): "You get no response" (text 7205).
- Greeting: rep = faction.rep + SGroupRep[sgroup], plus guild affinity (0 same guild; 1 shared parent +15;
  2 allies +10; 3 enemies -20; 4 shared enemy +5; 5 shared ally +5; 6 guild ally is NPC's enemy -5;
  7 guild enemy is NPC's ally -5; 8 none). Roll r = rand(0..15) - 10: rep < r -> rejection greeting.
  Fallback by reaction: >=30 7209, >=10 7208, >=0 7207, <0 7206.
- Per-question tone result 0/1/2 (TM 629-689): tone Polite/Normal/Blunt; sg index Commoners, Merchants,
  Scholars, Nobility, Underworld. Polite: toneMod = {-10,5,10,15,-15}[sg] (Etiquette); Blunt: {10,5,-10,-15,15}[sg]
  (Streetwise); non-Normal adds -10 on a failed skill roll else +5.
  `reaction = Personality/5 + questionTypeMods[q] + toneMod`, questionTypeMods = {5,0,0,0,5,0,0,0}
  (q: 0 where-is location, 1 person, 2 thing, 3 work, 4 quest location/organisation, 5 quest person, 6 quest item).
  roll = DFRandom(0..20) seeded by NPC hash; result 0 if reaction < roll, 1 if < roll+20, else 2.
  The base faction reaction does not enter this roll (it only gates refusal and greeting).
- Knowledge: known if same building, spymaster, own organisation; else d20 <= knowledgeModifiers[q*5+sg] + 10
  (table TM 114-115). Tell-me-about/rumours: one answer per NPC per talk.

## Reputation model
- Social groups: Commoners 0, Merchants 1, Scholars 2, Nobility 3, Underworld 4 (player `sGroupReputations[11]`).
- Faction rep per faction (FACTION.TXT start values), clamped -100..100.
- Legal rep per region (62), starts 0, -100..100; not used by talk — drives guards (< -10: 5% spawn) and court (< -20).
- ChangeReputation(id, amt, propagate): allies +amt/2, enemies -amt/2; knightly orders + Generic_Knightly_Order;
  else up to the root (initiator, root, questor service ids full amt; rest of tree amt/2); gods -> Generic_Temple.
- Changes: quest success +5 / failure -2 (quest faction, propagated); quest actions "change repute with",
  "legal repute"; crimes (loss by crime 0..15 = {0,10,5,10,8,20,10,2,1,2,2,75,2,8,36,10}) -> LegalRep -= loss,
  People of region rep -= loss/2; serving a sentence restores half; temple donation +1; every 112 days all reps
  drift 1 toward 0.
- Guild ranks from faction rep: {0,10,...,90}; skills high {22,23,31,39,47,55,63,71,79,87}, low {4,5,9,...,37};
  rep < 0 expels.

## NPC identity
- Static NPC: nameSeed = position ^ (buildingKey + locationIndex); female if flags & 0x20; race from the
  faction's race else the region's (MapsFile.RegionRaces: Breton or Redguard per region). Individual-type
  factions use the faction's name; otherwise DFRandom.srand(nameSeed) then NameHelper.FullName(bank, gender).
- Mobile NPCs: 1/32 guards; race by climate (Desert/Desert2/Rainforest Redguard, Mountain Nord, else Breton).

## Talk menu
- Tell me about: Any News, Where Am I, unhidden quest resources, 34 organisations.
- Where is: building groups per named building type present in town; "Regional" (a town in the region that has one);
  quest people. Answers give a compass direction, or (35%, outdoors) mark the building on the map.
- Work: 25% of interior Merchants/Commoners/Nobility NPCs in named buildings have work; reaction 0 refuses,
  1/2 names a questor and their building.

## Buildings
- Types: 0 Alchemist, 1 HouseForSale, 2 Armorer, 3 Bank, 4 Town4, 5 Bookseller, 6 ClothingStore, 7 FurnitureStore,
  8 GemStore, 9 GeneralStore, 10 Library, 11 GuildHall, 12 PawnShop, 13 WeaponSmith, 14 Temple, 15 Tavern,
  16 Palace, 17-22 House1-6, 23 Town23, 24 Ship.
- Named types (0, 2, 3, 5-16) take the town's MAPS.BSA building records of the same type in order (NameSeed,
  FactionId, Quality). Building key = (x<<16)+(y<<8)+recordIndex. Names: DFRandom.srand(NameSeed) then
  StoresA + type list / TavernsA+B / "The Bank of <region>" / guild = faction name / temple = faction's first child.
