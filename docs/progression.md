# Progression: factions, new skills and the passive tree

Decisions (2026-10-07):

- Factions from every converted game are separate (each game makes its own records: DFGuild*, MWFaction*, OBFaction*;
  none reuses Skyrim's), so the Mages Guild of Daggerfall, Morrowind and Oblivion and Skyrim's College never share
  membership, rank or reputation.
- TamrielMerge draws its own menus (no SkyUI or Custom Skills Framework): ActionScript 2 compiled to Flash with JPEXS
  FFDec (tools/ffdec, needs Java), opened with SKSE's `UI.OpenCustomMenu` and talking to Papyrus through SKSE mod events
  and `UI.Invoke*`.
- Skills Skyrim lacks become real skills that rise with use, count toward the character level, and have perk trees.
- The standing stones become a Path of Exile-like passive web: start at your birthsign, 1 point per level, faction
  ranks open notables and keystones.

## The Tamriel menu

Opened by the "Tamriel" lesser power or the **Tamriel Menu** control: U by default, rebindable (keyboard or mouse) in
Skyrim's Settings > Controls like any vanilla action. The progression build writes `Interface\Controls\PC\controlmap.txt`
(the vanilla map from the player's own `Skyrim - Interface.bsa` plus that line) and
`Interface\Translations\TamrielMerge Progression_<language>.txt` (`$Tamriel Menu`, the label the Controls list shows).
If another mod's controlmap.txt wins, the control is missing and the script falls back to U.

Tabs:

1. **Factions**: each converted game's joinable factions, the ones you have joined with their rank title and number.
2. **Coin**: each converted game's money and how much you have (also shown under the title: the coin carried).
3. **Travel**: Daggerfall's travel map for the Iliac Bay (TMDfOverland, in Daggerfall.esm): a region, then one of its
   towns with the time on the road; choosing one goes there and passes that time (Daggerfall Unity's travel on foot,
   108 minutes per map pixel, half on a horse). Skyrim's own map travels within the overland tile you are in.
4. **Skills**: the new skills with level and progress, and each skill's perk tree.
5. **Passives**: the web, with your start, allocated nodes and free points.

## The Iliac Bay's size and its tiles (user requests 2026-10-07)

Asked for Daggerfall's true scale, then for the game to load the next part at a threshold. True scale cannot be built:
a map pixel is 16 cells at our scale, so the map would be 16,000 x 8,000 cells (~90 million with land, ~7,800 times
Skyrim's; more records than 11 full plugins can hold and hundreds of GB), and Skyrim's positions lose precision past
~128 cells from a worldspace's origin. What is built: 2 map pixels per cell (`TerrainSettings.PixelsPerCell`,
`--pixels-per-cell`), 500 x 250 cells, about 8 times Skyrim's land, split into tiles (`Skyrim/Terrain/OverlandTiles`):

- 3 x 2 tiles of 167 x 125 cells, each its own worldspace `DFIliacBay_<column>_<row>` centred on its own origin (no
  tile reaches past 92 cells). A map that fits one worldspace (4 pixels per cell) stays the single `DFIliacBay`.
- Each tile has 8 cells of its neighbours' land; 2048 units past its edge TMDfOverland moves the player, their horse and
  followers into the neighbour at the same spot (a loading screen). The shared land is identical in both tiles.
- Everything on the overland (map markers, town and dungeon entrances) is placed in whole-map coordinates and moved into
  its tile at the end; doors leading to a moved reference arrive at its new position.
- The tiles' land is one plugin, `Daggerfall Land.esm` (master Daggerfall.esm), built a tile at a time, with each
  tile's distant terrain covering it and 32 cells around it.
- Weather regions are per tile and climate. `df-overland-test` builds and checks the overland alone.

Every town is built (user request 2026-10-07, "then batch it"): Daggerfall.esm holds Daggerfall city; the other 3,443
are `Daggerfall Towns.esm` (`Skyrim/DaggerfallTowns`, `df-towns --batch N --batches M`, `--list` shows the split). A
part builds its towns as Daggerfall.esm does, then swaps every shared record it made for Daggerfall.esm's of the same
EditorID (factions and reputation, guilds, spells, creatures, items, a region's street people), so guild ranks, regard
and bank accounts are the same in every town; its conversation quest (`DFTalk<N>`) is its own, and its banks keep their
accounts in Daggerfall.esm's (TMMoneyChanger.Accounts).

Few plugins (user request 2026-10-07, "too many esm files some need to be esl"): an ESL can add at most 4,096 records,
so only the small plugins are light (TamrielMerge Start, TamrielMerge Progression). The big ones are built in parts for
memory (a town part of ~450 blocks peaks at ~5 GB; the dungeons in 8 parts; the land a tile at a time), each part in
its own process, and `Skyrim/PluginMerger` joins a plugin's parts into one file: every part is the same plugin (same
ModKey and masters list) with its own FormID range, so records go in byte for byte; groups merge by type and label and a
record in several parts (the tiles' overrides) is kept once. Daggerfall is four full plugins: Daggerfall.esm,
Daggerfall Land.esm, Daggerfall Towns.esm, Daggerfall Dungeons.esm. `merge-plugins` and `plugin-stats` are the tools.

## How the game begins (user request 2026-10-12)

"the games start in the corrisponding story sequence if chosen. we need an alternative start if one is traveled to or
game started with the tutorial quest done and skyrims starting in riverwood with the imperial or stormcloak npc is
chosen", "and player owned games equivelant" (TMStoryOrder, TamrielMerge Start.esp):

- A new game, once the world is up: **the stories in order** (from the first converted game, as before) or **a time of
  the player's choosing** (only the games converted: a global per time shows its button), then **its opening or after
  it**. Skyrim's: **Play Helgen**, **Escaped Helgen with Hadvar** (an Imperial) or **with Ralof** (a Stormcloak), both at
  Riverwood. Then the intro (offered, as before) and the race menu; Helgen shows its own.
- **After the opening** (Skyrim/AltStarts.cs finds each in the converted plugins; TMStoryOrder.BeginAfterOpening):
  Daggerfall outside Privateer's Hold; Redguard on Stros M'Kai's docks (where its opening at sea ends); Morrowind out
  of Seyda Neen's Census and Excise Office with "Report to Caius Cosades" begun and his package; Oblivion out of the
  sewers' South East Tunnel with "Deliver the Amulet" begun (stage 10) and the Amulet of Kings. A door's spot is the
  outside door with the offset to where the game sets the player down coming out of it.
- **Skyrim after Helgen** (EscapedHelgen) is what vanilla's own quickstart (QF_MQ101 Fragment_27) leaves: the side's
  Before the Storm begun (MQ102A Hadvar / MQ102B Ralof, stage 1), Helgen's gates shut (25, 26) and its escape over
  (1000), its music and attack sounds gone; then that quest on past the escape (10 allied, 20 Riverwood ahead, 25
  agreed to go, 30 out of Helgen) and the player with Hadvar or Ralof at Riverwood, where his scene with Alvor or
  Gerdur plays. The side's gear: Imperial light armour and an Imperial sword, or the Stormcloak cuirass, boots,
  gauntlets and an iron war axe.
- **Travelling to a time not yet begun** (Travel Through Time, or the Tamriel menu's Travel tab) begins it there, its
  opening played or done (the same choice; TMStoryOrder.EnterNewTime); the story order's own place stays.
- Not tested in game.

## Coin by game (user request 2026-10-07)

Each time has its own money: Daggerfall's **Gold Pieces**, Redguard's **Gold**, Morrowind's **Drakes**, Oblivion's
**Septims**, Skyrim's **Gold**. They are all Skyrim's gold item (Gold001), so barter, vendors, dialogue costs, taverns
and converted scripts work unchanged; the converters map each game's coins to it (Morrowind's Gold_001 and the piles
Gold_005-Gold_100 count their coins; Oblivion's Gold001).

TMStoryOrder keeps them apart: the player carries only the coin of the time they are in (`MoneyStory`). Beginning a
story or Travel Through Time (`ChangeMoney`, before the move, so Skyrim's opening never stashes another time's coin)
puts the carried coin in that time's purse (`Purse`) and takes out the destination's (nothing on a first visit); the gold
item is renamed to the carried coin (SKSE `SetName`, reapplied after each load by the progression alias).

**Weight**: coin weighs what Daggerfall's does, 400 to the kilogram (Daggerfall Unity's gold piece, 0.0025 kg), as
0.005 per coin in Skyrim's units in every time (`CoinWeights`, SKSE `SetWeight` on the gold item): 10,000 coins weigh
50. Only the coin carried weighs anything; the other purses stay in their own time.

**Money changers** (`CoinTrade`, `TMMoneyChanger`, `TMExchangeTopic`): "I'd like to exchange coin." opens the Tamriel
menu's Coin tab with an amount picker and an Exchange button on each other time's purse, paying out this time's coin.
Fee 10% less 1% per 20 Speech (at least 2%); the rate is otherwise one coin for one. Who changes coin:

| Game | Money changers |
|---|---|
| Daggerfall | bankers (5% fee) |
| Morrowind | anyone who barters misc goods (AIDT services 0x400: pawnbrokers, traders, general merchants) |
| Oblivion | anyone who barters misc goods (AI services Miscellaneous) |
| Skyrim | its merchants (JobMerchantFaction) |
| Redguard | none (no shops to speak of) |

**Daggerfall's banks** (Daggerfall Unity DaggerfallBankManager / FormulaHelper; UESP Daggerfall:Banks): "I'd like to
do some banking." to a bank's keeper. An account in each of the 62 regions (the town's region, from TMDfTalk's town):
deposit, withdraw; letters of credit written from the account for 1% more and cashed into an account at any bank
(Letter of Credit items, one per gold piece of credit, weightless); loans up to level × 50,000, repaid with 10% within
360 days. An overdue loan defaults and that region's bank lends nothing more until it is repaid (the legal reputation
loss waits for the crime system). Houses and ships for sale are not done.

## Nights in town (user requests 2026-10-07)

- **Daggerfall's streets** follow Daggerfall Unity's PopulationManager: by day each town has (blocks / 16, 1 to 4)
  x 24 wanderers, drawn from a pool of 16 people per region (its race, Daggerfall names; 4 guards per region, one in 32
  of the crowd), standing a few paces out from the street doors and sandboxing; they share one enable parent
  (DFTownDaytimeMarker) that TMDfDaytime enables 6:00-18:00. At night the opposite: each town gets 3 night stalkers
  and 2 City Watch per lot, from its climate's "in location, night" encounter table (20 desert, 23 mountain,
  26 rainforest, 29 subtropical, 32 swamp/woodlands, 35 haunted woodlands: thieves, assassins, burglars, rogues,
  werewolves...).
- **Morrowind and Oblivion** (TMNightDanger, progression plugin): 20:00-6:00, outdoors within 8000 units of a town
  centre (MWTownCentres: one marker per named exterior; OBTownCentres: one per city worldspace), every 40 s a 20%
  chance that one of Skyrim's world-encounter thieves or assassins (LvlWEThief/-Ranged, LvlWEAssassin/-Ranged) comes
  out of the dark 1400 units behind the player and attacks; at most 2 at a time; those left at dawn slip away.
- **Guards sleep on the job**: each game's guards (DFGuards: City Watch and street guards; MWGuards/OBGuards: class or
  faction naming a guard or watch) are each judged once a night near the player; 30% nod off (TMDozeAbility,
  TMDozeEffect: unconscious, so they witness nothing) until dawn, or until struck or spoken to (a sneaking pickpocket
  doesn't wake them). TMNightDangerOn (1) turns the runtime part off.

## Pickpockets (user request 2026-10-07)

Some people quietly rob the player (`TMPickpockets` on the progression quest): Daggerfall's underworld
(`DFPickpockets`), Morrowind's thieves, paupers and Thieves Guild (`MWPickpockets`), Oblivion's thieves and beggars
(`OBPickpockets`), and Skyrim's Thieves Guild. Every 30 s there is a 25% chance that one within 250 units tries, if
they're not hostile, fighting, a follower or a child, share no faction with the player (no thief robs a fellow guild
member) and haven't tried in the last game day. Success: 30 + their Pickpocket / 2 − the player's Sneak / 2 percent
(5-70); they take coin (65%: 3 to 10 + Pickpocket, at most a quarter of what's carried + 1) or one small thing (a
potion, an ingredient, a lockpick, a soul gem; never anything equipped), into their own inventory, and the player isn't
told. A failed try is noticed half the time. Global `TMPickpocketsOn` (1) turns it off at 0.

## Faction registry

Built after the games (like procedural creatures): for each converted plugin, the factions a player can join (Daggerfall's
guilds, temples and orders; Morrowind's and Oblivion's joinable factions with their ranks) with display names and rank
titles, game by game.

## New skills

Only the skills of games that were converted. A skill's level is a global; use adds experience (Skyrim's skill curve),
a rise adds character experience as a Skyrim skill rise does (SKSE `SetPlayerExperience`), and an ability scaled by the
level gives its effect.

| Skill | From | Rises with | Effect |
|---|---|---|---|
| Mysticism | DF MW OB | casting mysticism spells | cheaper mysticism spells |
| Athletics | MW OB (DF Running) | running and swimming | speed, stamina regeneration |
| Acrobatics | MW OB (DF Jumping) | jumping and falling | jump height, less fall damage |
| Hand-to-Hand | DF MW OB | unarmed hits | unarmed damage |
| Spear | MW | hits with spears | spear damage |
| Medium Armor | MW | being hit in medium armour | medium armour rating |
| Mercantile | DF MW OB | bartering | better prices |
| Unarmored | MW | being hit without armour | armour rating without armour |
| Thaumaturgy | DF | casting thaumaturgy spells | cheaper thaumaturgy spells |
| Swimming | DF | swimming | swim speed, breath |
| Backstabbing | DF | sneak attacks | sneak attack damage |
| Critical Strike | DF | critical hits | critical chance |
| Medical | DF | healing | healing received |
| Etiquette, Streetwise | DF | talking with nobles / the underworld | their reactions |
| 9 languages | DF | meeting their creatures | calming them |

## Passive web

One web: small nodes (+attribute-like stats), notables (perks), keystones (big trade-offs); starts at the 13 birthsigns
(the Morrowind/Oblivion signs; Daggerfall's and Skyrim's stones mapped to them); 1 point per level; faction-gated
notables and keystones need a rank in a converted game's faction.

## Crime by game and area (user request 2026-10-07)

Each converted game keeps its own law, apart from Skyrim's holds and from each other: a bounty in one game's area
never follows the player into another's.

| Game | Areas (Skyrim crime factions) | Arrest and punishment |
|---|---|---|
| Daggerfall | one per region (62; legal reputation per region) | city watch appears when a crime is seen; court: fine, prison days or banishment |
| Morrowind | one (Morrowind's single bounty) | guards arrest; pay, prison (its PrisonMarker spots), Thieves Guild clears half |
| Oblivion | Cyrodiil, Shivering Isles | guards, jail, fences, Thieves Guild doyens clear half |
| Redguard | none | guards attack drawn weapons only |

Each game's guards and townsfolk join their area's crime faction; each area gets its jail from that game's prisons.
Characters copied from vanilla looks no longer keep Skyrim's crime faction (checked with `faction-check`).
