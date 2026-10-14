# Game systems checklist

Everything each game has inside it, from UESP, Daggerfall Unity and OpenMW research (2026-10-07), against what
TamrielMerge converts. Status: **done**, **partial**, **missing**, or **n/a** (the game doesn't have it).
Numbers marked (u) are unverified; settings should be read from the game's own master files (GMST) when built.

## Daggerfall

| System | Status | Notes |
|---|---|---|
| Dungeons (RDB) | done | water per block (199.10 sound index x 8; vanilla interior water planes, water-flagged navmesh, aquatic creatures and the underwater table below it), random treasure (knapsacks), people incl. castle courts, City Watch, corpses (dead adventurers), statues, E3 lights, text/riddle/door-text actions; Privateer's Hold and town dungeons in Daggerfall.esm; the other 4,228 in "Daggerfall Dungeons.esm", built in 40 parts (df-dungeons; each part's navmeshes made on worker threads while the next dungeons are laid out, 2026-10-10: 3.3 to 1.6 min a part) and merged: 2,374,085 records, 0 unresolved |
| Towns (RMB), interiors, doors | done | shared interiors, street/exit door scripts, opening hours. Building placement and turn checked against Daggerfall's own block automaps (2026-10-10, user: "make sure the buildings are the rotation they need to be in"): `df-town-check --all` draws every block's buildings from above over its automap (north up): 85% of footprints on the automap's buildings against at most 48% for any other turn or flip, every Daggerfall-city building on its own, and the few low scores are things the automap doesn't draw (ships, garden and curtain walls) or WEAPBS00, whose automap doesn't match its own records. `--image BLOCK` draws one; `rmb-dump --block X.RMB` lists a block's records. Buildings wear Daggerfall's own textures, inside and out (user, 2026-10-11: "daggerfall needs its building textures and upscaled"; they had been Skyrim's Solitude, Windhelm, Whiterun and Riften textures by climate), each in the town's climate as Daggerfall swaps them (a family's desert +0, mountain +100, temperate +300 and swamp +400 archives; swamp falls back to temperate) - only to an archive of the same name apart from the climate word ("Desert Manor"/"Woodland Manor"; "Int.", "Interior", "Interiors" alike), found anywhere below 500 when not at the usual number, else the model's own: Daggerfall reuses the numbers for unrelated archives (2026-10-12, user: "daggerfall beuilding textures look wrong" - "Desert Store Int" 71 had become 371 "Misc Mantella texs", a patterned sheet; 334 is "Daggerfall people"), laid by the models' own texture coordinates (DfModel reads them: in sixteenths of a texel, the first point's absolute, the next two steps from the point before, the rest on their plane; BlockMeshBuilder uses them for any Daggerfall texture). Four times their size: Real-ESRGAN from the cache when it's there (`df-upscale-export` writes every record of archives below 500 to tools/upscale/in-df, 6,070; `tools/upscale/upscale.py --input tools/upscale/in-df --output tools/upscale/out-df`), else Scale2x twice. The town's ground is Daggerfall's too (2026-10-12, user: "now for the daggerfall textures for the game itself and worldspace"): each block's 16 x 16 tiles (GroundTiles: record, a quarter turn 0x40, flipped 0x80) as squares of its climate's terrain tile set (desert 2, mountain 102, temperate 302, swamp 402: grass, dirt, stone, water, paving and the tiles blending them), turned and flipped as Daggerfall lays them (TownTextures.GroundPolygons; `df-town-meshes --ground` rebuilds built ground in place). Dungeons too: every surface its Daggerfall texture (BlockMeshBuilder.PickTexture; its wooden families keep Skyrim's wood stair material; `df-block-meshes` rebuilds built ones). The overland: its ground is Daggerfall's, laid as Daggerfall lays it (2026-10-12, user: "the landscape needs worked on" - Daggerfall's ground patterns): each climate's terrain tile set (desert 2, mountain 102, temperate 302, swamp 402) with its dirt, grass and stone (records 1-3; the subtropical climate takes the desert set with full grass) in patches by Daggerfall Unity's ground noise (DefaultTerrainTexturing: Perlin at 0.05 a tile, 3 octaves; dirt below 0.5, stone above 0.95, grass between) at Daggerfall's own tile size (6.4 m, 448 units: patches of about 130 m whatever the map's scale), blended at the thresholds where Daggerfall has transition tiles; its dirt on the beach and the sea floor, its stone on slopes over ~31-44 degrees, no snowy peaks (Daggerfall's snow is winter's) (TerrainBuilder.Ground/Weights; `ground-preview --near NAME` draws it from above). The land build makes them in its first part; `df-land-textures` moves a pre-2026-10-12 build's Skyrim land textures over in place (the old kept as .skyrimtex.bak) (Skyrim/Meshes/TownTextures, DfTextureExport). The map is twice as big since 2026-10-12 (user: "daggerfall has a big map", chose twice as big): one map pixel per cell, 1000 x 500 cells in 15 tiles (was 2 per cell, 500 x 250 in 6), the altitude exaggeration 2000 (was 1100): peaks ~22,000 units, half the land under 5.6 degrees, 1% over 26 (was 29,000, 7.7 and 42); heights between the map's samples are a cubic B-spline (Daggerfall Unity's interpolator overshoots, a terrace per map pixel that showed as a grid) (HeightField; `terrain-preview --pixels-per-cell N --exaggeration E` for the figures). Its grass is Daggerfall's (2026-10-12, user: "the grass needs to be daggerfalls and the grass placement on where its suppose to be"; "they need upscaled"): each climate's grass tufts (its nature archive's records 7 and 8, upscaled with their see-through kept: `df-upscale-export` writes them too, the see-through pixels coloured as their nearest neighbour so no dark fringe comes in) as Skyrim grass, three crossed quads set up as Skyrim's fieldgrass01.nif, at Skyrim field grass's density, slopes and wave; on the ground as Daggerfall Unity lays nature flats (DaggerfallTerrain.LayoutNature: a flat on 0.9 of grass tiles, 0.2 of dirt, 0.05 of stone, the desert a quarter of that): thick on grass, thin on dirt, none on stone (too thin to show), so the beach and sea floor (dirt) and the steep slopes (stone) are nearly bare (Terrain/DfGrass; `df-grass` sets a built Land.esm's in place, the old kept as .skyrimgrass.bak). The grass sprites Daggerfall's town blocks place by hand are the same meshes as statics (DfGrass.AddBlockGrass) instead of Skyrim's spiky grass |
| Stairs | done | As Skyrim's own (measured 2026-10-09 in game: the player walks ramps to 45 deg, not 50; climbs steps of the stair material up to ~24-32 units, plain-stone steps not at all): every step's riser and treads get SKY_HAV_MAT_STAIRS_STONE/WOOD, and each flight one smooth plain ramp from its top nosing to the floor below, along the nosings or at 44 deg if gentler (Daggerfall's ~68 deg flights get longer ramps that run out past the foot; user: "use the one that was smooth it will just make stairs longer"). BlockMeshBuilder.CollisionPolygons/Flights/FlightRamp; `df-block-meshes` rebuilds existing dungeon meshes in place. Other games (user: "do other games need the stair fix"): Morrowind (steps 16) and Oblivion (~18) keep their own collision meshes, which are already ramps of 25-45 deg; Redguard (16-32, no collision meshes) needed it. All three converters now pass static collision through BlockMeshBuilder.StairCollision (ModelStairs rules: risers 8-40 with a tread on top, flights of 2+, so furniture isn't taken for steps); `mesh-refresh --game mw|ob --dirs` reconverts installed models in place. A flight's steps join across sloping treads (Daggerfall's stair models: treads falling 8 to a 24 riser, which before 2026-10-10 left every ordinary dungeon staircase as single steps with no ramp). A ramp runs on under the floor past its foot, 24 below it (2026-10-11, user: "can't walk up" in Privateer's Hold: a flight's lowest tread falls 8 to the floor, so the ramp ended in a plain 8-unit lip the player can't step up; `df-block-meshes` and `df-town-meshes` rebuilt the installed meshes). Tools: stair-test-build, nif-stairs (--near: the slabs there), df-stairs --name LOC --flights/--near/--doors (also our collision's slab slopes), tools/ingame/cmsd.py (vanilla collision reader) |
| Doors | done | Daggerfall's hinged dungeon doors are Skyrim's ImpDoorExtSingleSmall01/ImpDoorDouble01 (DoorPlacer), scaled to the doorway and stood on their own floor (frame -36..192, leaf -37..172 about the single's origin; the double's threshold 4 above), so frame and opening match (user, 2026-10-10: "some doors are too small for npcs too", "the door frames dont match some of the doors"). Town doors (SurfaceDoor) turn by where each style's door faces in its model, learned from Skyrim's own placements (`door-front`): Imperial -Y, Riften +Y, Nordic and Whiterun -X. Plugins built before are fixed in place by `df-fix-doors --plugins a;b --with Daggerfall.esm` (DoorRefFix: only the door refs' position and rotation, once per plugin, marked in the header). Tools: door-scales, town-door-sizes, door-front, doors-of --all |
| Statues | done | Daggerfall's statue models (62323/62327, 62324/62328) are StatueDibella, slim like them: as tall as Daggerfall's and no broader than its base (+25%); Mara and Talos fitted to that height had spread to ~550 units and blocked the throne-room doors of Privateer's Hold. Statue sprites (97, 98, 202) keep their table's statue but no broader than 1.5 x the sprite. Every statue faces into its room (RoomFacing: level rays against the walls; straight out from a wall it stands against, else towards the most open side; replaced props aren't walls), its model's front known from model-preview (+Y: Mara, Talos; -Y: Dibella, MageStatue, Vaermina, NorDragonStatue01, the Falmer; sprite models -Y). User, 2026-10-10: "statues obstruct doors", "some statues are rotated". Plugins built before are fixed in place by `df-fix-statues --plugins a;b` (StatueRefFix: the statues of dungeon and building-interior cells placed again from Daggerfall's data and given to the nearest old statue refs; street statues wait for the towns' rebuild; once per plugin, marked in the header). Tools: df-dungeon-test --details (statues, their facing, size and nearest door), room-facing --name LOC --at x,y,z, statue-count, doors-of --all |
| Travel | built, not tested in game | Daggerfall's travel in every converted world of the user's games, made in real time without loading screens (the Tamriel menu's Travel tab; TamrielMerge Travel.esp, `travel-build`): worlds labelled with their era, regions, towns, Daggerfall's trip (days, price, cautiously/recklessly, inns/camp, ship, horse, a pace slider bounded by uGridsToLoad), the player walked there by the game's pathing while the clock runs Daggerfall's time, ships sailing between harbours, fights stopping it, another time's towns reached through time, Skyrim's map travel taking Daggerfall's time too. For seamless travel the Iliac Bay is one worldspace, its physics reach widened by TamrielMergeGraphics. See docs/travel.md |
| Overland navmesh | done | every land cell of the overland tiles (15 since the map doubled, 2026-10-12; figures below are from the 6-tile map) ("Daggerfall Land.esm"), Recast in an open-country setting (32-unit voxels, long edges) from the cells' own heights, underwater ground left out, cells joined across edges within each tile, one NAVI override with every entry; built a tile at a time as parts merged into the land plugin (Skyrim/Terrain/OverlandNavmesh.cs, `df-navmesh`, run by the Daggerfall build after the land). 2026-10-09: 99,359 navmeshes, 1.0 M triangles, 411k links, +54 MB; cells left without one are coastal slivers or cliffs over 50 degrees (`nav-ext-verify --list`). Town gates and dungeon entrances are joined to them by "Daggerfall Overland Doors.esp" (light, after the towns and dungeons; Skyrim/Terrain/OverlandDoorLinks.cs, `overland-doors`, run by convert after the dungeons): the triangle each door stands on (under it in plan, else the nearest edge within 384, height counted half) tagged as its door triangle and listed in the navmesh map's override. 2026-10-10: 7,613 of 7,671 doors joined (1 in a cell without a navmesh, 57 with no triangle within 384), 0 unresolved |
| Townsfolk as 3D NPCs, names, races | done | NAMEGEN, FACTION.TXT, region races; street population by day, night stalkers and watch (docs/progression.md "Nights in town") |
| Talk: greetings, Where is, news, rumours | done | reaction = faction + social-group rep; refusal below −20 |
| Talk tone (polite/normal/blunt via Etiquette/Streetwise) | missing | tone gives social-group modifiers (e.g. Etiquette + Nobility +15) |
| Reputation (faction, social group) | partial | read at build; no runtime changes, no decay (1 point toward 0 per 112 days) |
| Legal reputation per region, crime, arrest, court, banishment | missing | crimes: B&E, assault, murder, pickpocket, theft, vagrancy |
| Lockpicking, bashing, magically held locks | done | TMDfLock, TMDfStreetDoor (DFU formulas) |
| Pickpocket (skill + 5·(level − target level), 5–95) | missing | Skyrim's own pickpocket in use |
| Shops (quality 1–20), barter | partial | vendor factions + chests from vanilla templates; armorers and weaponsmiths sell Daggerfall's own weapons and armor (DfItems' leveled lists, `DFShopChestDaggerfallSmith`) |
| Weapons and armor | done | Skyrim/DfItems (user, 2026-10-12: "DF items, Skyrim shapes"): Daggerfall's 18 weapon types in its 10 materials and its 11 armor pieces in leather, chain and the 10 plate materials (304 items), named as Daggerfall names them; weight, value from Daggerfall's data (Daggerfall Unity ItemTemplates/ItemBuilder: weight x material quarters / 4, leather half; value x 3 x 1..512, chain x 2); Skyrim's model of the kind and material (by its WeapType/WeapMaterial, ArmorX/ArmorMaterial keywords, preferring the one named after the material), damage and rating Skyrim's for the material scaled by Daggerfall's type differences; silver (but its swords), mithril and adamantium are steel recoloured (ItemRecolour: models and diffuse textures copied under TamrielMerge\DF\Items\{metal}); greaves and pauldrons give their rating unseen (biped slots 38, 44, 45). Leveled lists by material tier (`DFWeaponsAll`, `DFArmorAll`). Records at fixed numbers from 0xD00000 in Daggerfall.esm (nothing after them renumbered). On floors, equipment sprites (207) are the items each pictures, in iron or steel (ClutterPlacer.UseDaggerfallItems). Enemies still carry vanilla gear. `df-items-test` builds them alone |
| Furniture and props | done | Skyrim/DfPropBuilder (user, 2026-10-12: "DF models, Skyrim use"): PropReplacer's table models (chairs, thrones, benches, pews, stools, beds, tables, bookcases, wardrobes, counters, crates, chests, coffins, racks, statues, rocks...) are Daggerfall's own models (Meshes\TamrielMerge\Props\{model}.nif: its polygons and upscaled textures, static collision) in a copy of the Skyrim record each stood for - containers open and keep their loot, furniture seats and beds: its mesh is built in the stand-in's space with the stand-in model's furniture marker block cloned in, and placed as the stand-in was; the rest stand as Daggerfall places them. In Daggerfall.esm at 0xE00000 + the model number. Tavern rooms rent Daggerfall's beds too (TMDfTavern: Skyrim's bed or one of its name) |
| Clutter (sprites) | partial | Daggerfall's scenery sprites become 3D models from the sprites (SpriteModels.Targets: the palms, cacti, mushrooms, statues, misc, study and tool items, and since 2026-10-12 every other sprite the clutter table stood a Skyrim static, tree or movable static in for - 523 more, from `clutter-table`); items, containers, potions and lights keep their Skyrim objects |
| DF weapons/armour/materials, item condition, repair | missing | 9 materials, 7 armour slots, condition bands |
| Spells, Open/Lock, Recall | partial | SpellBuilder; spellmaker missing |
| Spellmaker, item maker, potion maker, recharge, identify | missing | guild services by rank |
| Guilds and ranks (Mages, Fighters, Thieves, DB, 10 knightly orders, 8 temples) | partial | TownGuilds/TMDfGuilds: join, ranks with titles, daily 28-day promotion/demotion/expulsion, TG/DB invitations, training (to 50), temple healing, Mages Guild spell tomes, temple potions; covens, repair, free rooms, armour gifts, item/spell/potion makers missing; reputation only moves once quests exist |
| Quests (QBN/QRC, 227) and main quest | missing | time limits, courier letters, multiple endings |
| Character creation: 18 classes, custom class, advantages/disadvantages, background questions | missing | level-speed multiplier ("difficulty dagger") |
| 35 skills incl. 9 languages, skill-use levelling | missing | Skyrim skills stand in; language pacification of creatures missing |
| Racial abilities (Breton/Nord resist 30%, Khajiit climb +30, …) | missing | |
| Vampirism (9 bloodlines), lycanthropy (wolf/boar, moon) | missing | |
| Diseases | missing | |
| Banks, letters of credit, loans, buying houses and ships | banks, letters of credit and loans done (docs/progression.md); houses and ships missing | |
| Holidays (half-price shops, free cures) | partial | TMDfHolidays: morning notice, Merchants' Festival half price (SKSE), free temple healing days, free ale days, Heart's Day rooms, Sun's Rest closed; Warriors Festival, Tales and Tallows, Witches Festival, summoning days missing |
| Travel map: cautious/reckless, inns/camp, horse, cart, ship fares | partial | Tamriel menu Travel tab: region, town, time on the road (on foot / horse); cautious/reckless, inns/camp, cart and ship fares missing |
| Taverns: rooms, food/drink | done | TMDfTavern: rooms 7 gold a night (beds owned by the tavern until rented), ale 2 gold |
| Climbing skill, swimming breath, levitation | missing | |
| Weather and seasons, day/night | partial | AtmosphereBuilder |
| Books | done | |
| Music | done | |
| Daedra summoning, artifacts (16 Daedric + 7 others) | missing | |
| Automap | n/a | Skyrim's map |

## Redguard

| System | Status | Notes |
|---|---|---|
| Stros M'Kai world, interiors | done | |
| Overworld land and navmesh | done | 4 worldspaces with land from the .WLD heightmaps (Stros M'Kai, palace grounds, Isle of N'Gasta, hideout; terrain LOD) and navmeshes on land and buildings, cells joined across edges. Made bigger 2026-10-09 (user: "make the island bigger", chose "stretch the wilds"): Redguard's objects already match Skyrim's scale (door 186 vs 178, table 60 vs 63, people ~130 vs 128), so Redguard/RgWarp.cs keeps every settlement (rigid pieces that touch, as convex shells) exactly and spreads the open land about 2x by a least-squares sheet (open edges want 2x, settlements may only shift, the sea's edge pinned at 2x): Stros M'Kai's land 618 x 600 m -> 1009 x 1098 m (land area median x3.5, 99% under x5.5, no folds), 33 x 33 cells. 622 navmeshes, 0 faults. Dev: TAMRIELMERGE_WARP_IMAGE / _FOLDS draw it. Record IDs shifted with the rebuild (voices repacked; saves inside Redguard's world don't carry over) |
| Water | done | Skyrim's water in place of Redguard's (user 2026-10-09: "redguards water needs replaced with skyrims and daggerfalls"): faces with Redguard's water images (RgMeshConverter.WaterImages) are left out of the models; flat ones become water surfaces of the same outline per level (Skyrim/Meshes/WaterSurface: vanilla water2048.nif's water shader, phantom volume and bound refitted; activators of the sea's water type outdoors, DefaultIntWaterWaistHeightStill indoors), steep ones vanilla's scrolling waterfall sheet (fxwaterfallthin512x128.nif refitted); the sea is Tamriel's water as Daggerfall's, and its bed LCoastOceanFloor01 (Daggerfall's) instead of Redguard's water tiles. 43 surfaces, 6 falls |
| Actors, voices, dialogue topics | done | voiced topic menu |
| Friend/enemy flags (NPC attitude) | done | fixed attitudes per group, not a scale |
| Logbook | done | log quest |
| Game scripts (story flags, puzzles, mechanisms) | partial | Redguard scripts are next in the plan |
| Keys and keyed doors (Rusty, Gold, Silver, Palace Silver, Bone, …) | partial | no lockpicking in the game; rune doors are puzzle scripts |
| Puzzles: levers, pads, timed doors, traps, rope swings, elevators, lighthouse signal, star chart | missing | come with the scripts |
| Using items on objects (torch on troll, bucket on fire, shovel on dig spot) | missing | |
| Mixing ingredients (ectoplasm, Hist sap, orc's blood, spider's milk) | missing | three recipes open Clavicus Vile's portal |
| Healing items: potion 15, well water 20 × 5, water + aloe 20 × 15, bandage 30; Arkay priest/statue | missing | |
| Strength/Ironskin potions, Hunding brazier, Blood Lust | missing | |
| Canah bird flight between 6 Kynareth shrines (10 gold feather) | missing | |
| Combat moves, enemies' rules (zombies rise, trolls regenerate unless burnt, Vermai) | missing | boss mechanics too (giant's heel, heated blade vs dragon, reflected spells) |
| Gremlin transformation | missing | |
| Torches (1 minute each), gold cap 500 | missing | |
| Skills, levels, magic, lockpicking, crime, factions | n/a | |

## Morrowind (Tribunal, Bloodmoon)

| System | Status | Notes |
|---|---|---|
| Interiors, exteriors, terrain, doors | done | |
| Items, containers, leveled lists | done | |
| NPCs and creatures | done | |
| Dialogue, journal, greetings | done | |
| Disposition, persuasion (admire/intimidate/taunt/bribe) | done | |
| Factions in dialogue conditions | partial | joining, ranks by attributes/skills/rep, expulsion missing |
| Locks, lockpicks, probes, traps | done | TMMwLock, TMMwSecurity (OpenMW formula); traps cast their own converted spell |
| Spells, enchantments, magic effects | done | TesMagic + MwMagic: 1,385 spells/abilities/powers/diseases, 650 enchantments, 140 scrolls (magic-test); Blind, Sound, Corprus, Vampirism, Detect Key/Enchantment not carried |
| Spellmaking, enchanting (soul gems, constant effect ≥ 400), recharge | missing | |
| Alchemy apparatus (4 kinds, qualities) and formulas | missing | apparatus are misc items |
| Repair hammers and the repair formula | missing | hammers are misc items |
| MWScript (local, global, result scripts) | partial | dialogue results only; Morrowind scripts are next in the plan |
| Travel: silt striders, boats, Mages Guild guides, gondolas, Propylon chambers | missing | |
| Mark/Recall, Divine/Almsivi Intervention | done | scripts; Intervention indoors via WorldSpot markers linked to the outside door |
| Races (powers, resistances), birthsigns, classes, attributes | missing | |
| 27 skills, major/minor, levelling multipliers | missing | Skyrim skills stand in |
| Skill books (+1 on first read), trainers | missing | books teach nothing yet |
| Mercantile/barter formula, merchant gold | missing | |
| Crime: bounties, jail (days per 100), Thieves Guild clears 50% | missing | |
| Pickpocket formula | missing | |
| Diseases, blight, corprus | missing | |
| Vampirism (3 clans), werewolves (Bloodmoon) | missing | |
| Strongholds, Raven Rock colony | missing | quest/script driven |
| Weather: ash and blight storms, snow | missing | |
| Rest rules, sleep interruption from region lists | missing | |
| Tribunal Museum of Artifacts, mercenaries | missing | script driven |

## Oblivion (Shivering Isles, Knights of the Nine, official plugins)

| System | Status | Notes |
|---|---|---|
| World, interiors, terrain, doors | done | |
| Items, containers, leveled lists | done | |
| Dialogue, voices, quests | done | |
| Game scripts | done | translator (vanilla and OBSE): every statement translated (46,945 of 46,945 in the official plugins), MenuMode, OnAlarm, script packages, crimes, misc stats, gates through TMObState; see [oblivion-scripts.md](oblivion-scripts.md) |
| Disposition | done | |
| Persuasion | partial | as a topic (Admire/Boast/Joke/Coerce/Bribe), not the wheel minigame with loved/hated wedges |
| Locks: pick by hand or Auto Attempt, tiers, keys | done | TMObLock; auto formula from UESP talk page, unconfirmed |
| Security perks (tumblers stay set when a pick breaks) | missing | |
| Spells, enchantments, staffs, scrolls | done | TesMagic + ObMagic: 1,234 spells, 1,333 enchantments, 339 scrolls (magic-test); scripted effects (SEFF) run their converted scripts on magic effects of their own |
| Sigil stones, Altar of Enchanting, spellmaking | missing | |
| Alchemy apparatus (mortar, retort, calcinator, alembic), poisons | missing | |
| Armorer repair and hammers | missing | |
| Birthsigns, classes, attributes, +5 levelling | missing | |
| 21 skills, mastery perks (power attacks, block, sneak ×6, …) | missing | |
| Trainers (5 per level), skill books | missing | books teach nothing yet |
| Crime: bounties, jail, fences, Thieves Guild doyens | missing | |
| Fame/Infamy | missing | |
| Horses (7 stables), horse armour | missing | |
| Player houses and furnishing | partial | depends on translated scripts |
| Oblivion gates (60, random sites) | partial | script driven |
| Fast travel (map markers) | missing | |
| Vampirism stages (feeding, 25–100%), diseases | missing | |
| Arena (ranks, betting) | partial | script driven |
| Shivering Isles: Gatekeeper rebuild, Amber/Madness Ore smiths | partial | script driven |
| Knights of the Nine relics (Infamy limit, levelled bands) | partial | script driven |
| Frostcrag, Vile Lair, Thieves Den, Battlehorn, Orrery, Mehrunes' Razor, Spell Tomes | partial | script driven if the plugins are selected |

## Sources

- UESP: Daggerfall, Redguard, Morrowind, Oblivion, Shivering Isles pages (en.uesp.net/wiki), incl. Oblivion talk:Security.
- Daggerfall Unity: github.com/Interkarma/daggerfall-unity (FormulaHelper.cs, TravelTimeCalculator.cs, PlayerActivate.cs).
- OpenMW: github.com/OpenMW/openmw (mechanicsmanagerimp.cpp, security.cpp, pickpocket.cpp, repair.cpp, tradewindow.cpp).
