# The main menu and the Tamriel logo

User requests (2026-10-08): a new logo whose corner towns show what the converter builds; a main menu replacer with
Oblivion-style flybys; the 3D emblem red like the logo; the towns populated, people walking; the emblem and Skyrim's menu
fog out of the flybys' way.

## Rendering the conversions offline

Nothing here runs the game.

- `scene-export --plugins A.esm --world W [--centre x,y --radius r] --out DIR` (`Skyrim/Render/SceneExporter.cs`)
  writes a converted place as a scene: every placed object's mesh (shapes transformed, textures, alpha), the land
  (heights and painted texture weights), the water (the height most of its cells have) and its people.
- **People** are dressed as the game does: the race's body where the outfit leaves it bare, the outfit's clothes and
  armour, the head parts (the NPC's, else the race's defaults), traits and inventory through templates. Skinned parts
  are placed through their bone weights onto the race's skeleton, posed with the arms at the sides, standing and at 8
  steps of a walk cycle (thighs, knees, swinging arms). Night-only people (enabled opposite to a parent) are left out.
- `tools/scene3d/render_scene.py` draws a scene with moderngl: instanced meshes, alpha cut-outs, the land's textures
  blended by weight, water with a sky reflection, sun shadows, hemisphere light, fog, a sky with drifting clouds;
  `--overview` (top down, with a grid), `--camera/--target` (a still) or `--path` (a flythrough, keys of eye/target/
  time). A path may ask for a **crowd**: copies of the town's people (not its animals) on open, level ground at street
  level found from a top-down height map, most walking along the street they stand on, shown at the matching walk step.
  The 78 C GPU guard applies.
- `scene-worlds`, `scene-find --model`, `scene-mesh` help choose what and where.

## The logo

`tools/menu/make_logo.py` keeps the user's emblem and lettering exactly (`tools/menu/logo_source.png`) and replaces the
four corner vignettes with the converted towns, rendered and turned into the logo's grey style: Daggerfall top left,
Vivec top right, the Imperial City bottom left, Stros M'Kai bottom right; sparkles restored over them.

## Building it

`TamrielMerge.Cli menu [--plugins out/convert] [--out DIR] [--work out/menuart]` builds the whole menu from the
converted plugins: the shots' scenes exported (`intro.py export`, each shot's plugin, worldspace, centre and radius
are in intro.py), prepared for Blender (`intro.py prep`; Stros M'Kai's textures at 320 px, the rest 512),
ray-traced in EEVEE (`intro.py rt-render`; the raster renderer when Blender is missing), packed, the emblem's textures made
red from Skyrim's own, and the logo meshes built with `tools/menu/menu_camera.json`. `convert --gpu` runs it
(`--skip Menu` leaves it out). `setup` finds Blender: the newest 5.x installed, by its own version (blender.org's or Steam's), else blender.org's
portable 5.2.2 into tools\blender (5.1's EEVEE drew enclosed streets black; Blender 6 isn't out as of 2026-10-11).

## The menu

`menu-build --art DIR --out DIR` (`Skyrim/Render/MenuBuilder.cs`):

- **Emblem**: Skyrim's emblem in the logo's red stone (`tools/menu/red_logo.py` recolours the emblem's textures by
  brightness rank onto the emblem's own colours) on a **banner** (user, 2026-10-08: "a black with red lining banner
  behind the logo", "the logo will be on the left side"): black cloth hanging from the top edge on the video's left,
  edged in red with a thin red line inside, a swallowtail end above the captions, a soft shadow on the picture.
  `intro.py banner` draws it at twice the video's size into `banner.dds` (uncompressed RGBA) and `banner.json` (its
  place in the frame); the logo meshes show it as a see-through layer 20 units in front of the screen. The emblem
  picture is `emblem.png` in the work folder, else the tool's `tools/menu/emblem.png` (cut from the menu with the
  red textures, on black: alpha from brightness). Not in the video's frames: there the DXT1 compression bled its reds.
  The meshes' own 3D emblem is hidden: with anything added to the logo mesh it never drew (2026-10-08, bisected: the
  vanilla mesh re-saved by nifly, or only shrunk, shows it; any added shape hides it, wherever placed, with or without
  depth, whatever its bounds; a capture shows it drawn into the G-buffer after the screen). It stays the root's first
  child, hidden: moving it from there crashed the game at start.
- **Intro video** (user, 2026-10-08: "an intro type video but for the menu", after the first street-level flybys
  looked wrong): `tools/menu/intro.py`. One slow aerial shot per game, each at its own time of day: an orbit of the
  Imperial City's White-Gold Tower, Vivec's cantons from the water at sunset, a glide up Daggerfall's boulevard to the
  castle, Stros M'Kai's harbour. Eased camera paths, a film grade (bloom, warm highlights, vignette), the place and its
  game captioned lower left while each shot plays, crossfades into a seamless loop (rendered 1200x675, 20 fps; upscaled, below; 3x6 frames to a
  DXT1 sheet). `intro.py preview` renders three small stills per shot to tune them; `render` every frame; `pack`
  grades, captions and packs. `--pause PID` suspends the voice generator while the GPU renders.
- **Path tracing** (user, 2026-10-08: "can videos be made to look like they have raytracing", chose real path
  tracing over shader effects): `tools/scene3d/blend_prep.py SCENE` turns a scene's textures into PNGs of at most
  512 px (Cycles keeps textures uncompressed; the shots are aerial) and packs the land's normalised texture weights
  four to an image; `tools/scene3d/blend_scene.py`, run inside Blender 5.1 (`intro.py rt-preview` / `rt-render`),
  builds the place in metres with its textures, vertex colours and cut-outs, the land's layers blended by weight,
  rippled reflective water, a physical sky, a sun lamp in the shot's direction and colour, and a thin haze volume the
  sun shines through, then renders each frame. `intro.py pack OUT MENU_DIR --frames rt` packs those frames.
- **Ray tracing, not path tracing** (user, 2026-10-09: "flybys need rt not pt"): the frames render in EEVEE with its
  ray tracing (screen-traced reflections, refraction and global illumination, shadow-map ray tracing, the haze as a
  lit volume; 64 anti-aliasing samples, AgX) instead of Cycles. Speed (user, 2026-10-09: "why is it going slow"):
  EEVEE walks every object on one CPU core for each sample, so with 14000 objects the graphics card idled at a third
  of its clock (5 s a frame); the still objects are now merged, one mesh per material and 16384-unit square
  (Whiterun: 13179 objects in 939 meshes, the same picture to 0.04% of pixels), and each shot renders as one
  animation (a frame hook moves camera, water and people) so the scene stays on the card: 3.3 s a frame with the
  card fully busy, under a minute to build a scene (was 3.5), up to 7.6 GB of memory, a whole shot per Blender run.
  EEVEE allows a material 32 textures, so the land is built in chunks of
  6 x 6 cells, each with only the layers painted there (at most 12, the most painted) and their weight maps.
- **Upscaled** (user, 2026-10-08: "the video needs upscaled to 4k"; 1440p chosen: the menu holds every frame, and
  520 frames at 4K would be 2.2 GB of textures): `intro.py upscale OUT --frames rt --size 2560x1440` runs each frame
  through Real-ESRGAN x4plus (`tools/video/remaster.py`, GPU, waiting while the GPU is above 78 C) into
  `OUT/rt1440/`; `pack --frames rt1440 --size 2560x1440` packs 2 frames to a 2560x2880 DXT1 sheet (about 0.96 GB).
- **Screen**: in each logo mesh, one unlit shape per frame (BSEffectShaderProperty), its UVs on its own cell of its
  sheet, shown for its 1/20 s in turn (NiVisController). The menu doesn't run effect-shader UV controllers: the first
  build stepped one shape per sheet through its cells by UV offset, and the menu showed only each sheet's first frame,
  about one a second (measured from a burst of screenshots matched to the frames; now 19.7 frames a second, every
  frame). The shape is a flat screen exactly filling the menu camera's view (3% oversized, a darkening apron past it
  for wider screens), standing on a plane 1200 units behind the emblem.
- **The menu camera** is placed by the game's code (`sUIMistMenu_DefaultLogoCameraPath` is empty), so it was measured:
  `tools/menu/calibration.py` writes ArUco markers, `menu-calibrate --cal DIR --out DIR` puts them in the logo meshes,
  a screenshot of the menu solves the camera (`calibration.py solve`). Two shots (2026-10-08, game 1.7.104): a box of
  markers showed the camera looking along +Y, then markers in that corridor at several depths gave it exactly
  (`tools/menu/menu_camera.json`): eye about (48, -651, 92) in the logo mesh's space, looking 7.7 degrees down at the
  emblem, a long lens of 16.5 x 9.3 degrees. The screen's corners checked against the photographed markers land on the
  screen's corners to 0.3 px.
- **Fog**: `meshes\interface\intmenufogparticles.nif` is Skyrim's own with everything it draws hidden: the particle
  system and the shape the particles are emitted from ("EmitGEo:0": 200 triangles of animated thin steam,
  FXSteamThinAnim at 45%). Hiding only the particles left that steam layer drifting over the flyby (user, 2026-10-10:
  "see if the menu fog layer is messing with it"). The menu drives the mesh (controller manager, behaviour graph): an
  empty mesh in its place crashed the game at the menu.

- **People** (user, 2026-10-08: "what happened to the npcs in the video", "have the people move around or down the
  streets"): `blend_prep.py` converts the textures of the town's people too, and `blend_scene.py` builds each person
  (posed body, clothes and head parts) with a pool of objects whose meshes are swapped for the step of the walk shown;
  four in five walk the way they face at 60 to 85 units a second, a stride every 90 units, as `render_scene.py` does
  (same seed). A shot may add a **crowd** along a street (`"crowd": [{"line": [[x, y], [x, y]], "width", "count"}]`):
  copies of the town's people (not its animals) on open, level ground at the street's height, found by casting rays
  down; most walk along the street, either way. Daggerfall's boulevard has 140.
- **Peopled, walking, never in each other's way** (user, 2026-10-09: "the npcs from those areas need to walk around
  and be populated and not obstructing each other"): every shot adds more of its own place's people (`"populate": n`:
  copies of its people 150 to 2000 units from one of them, on level ground at about its height and under open sky -
  streets, not roofs or houses). Then `plan_walks` plans everyone's way through the shot before rendering: each stands
  on the ground under it (rays cast down, so nobody floats or sinks), and a walker's route goes 30 units a step over
  ground rising or falling at most 26 a step (stairs, slopes), turning the least way where a wall at knee or head
  height or a drop is ahead, stopping where nothing is clear; a walker faces the way its route goes. A route is kept
  only if its walker never comes within 70 units of anyone already planned at any moment (checked every 0.25 s);
  otherwise other ways, then standing; one who'd stand in another's way is left out. The place's own people are
  planned first. First run: Whiterun 211 people (128 walking), the Imperial City 130, Vivec 149, Daggerfall 347,
  Stros M'Kai 167; 4 to 33 left out a shot.
- **The games' own worldspaces** (user, 2026-10-09: "the menu flybys need to be the games worldspace not a test
  area", "ground sinks/floats"): Daggerfall's town is a worldspace of its own without land, and stood on a stand-in
  plane. Its shot now exports `~+DFTDaggerfall17@door` (with Daggerfall Land.esm): the exporter finds the load door
  leading into the town, `~` becomes the worldspace holding it (the overland tile: DFIliacBay_0_1 with the default
  terrain settings), and the town is moved by the offset putting its arrival point on that door (84992, -141312, 2301
  here; `@dx,dy,dz` gives one by hand). The shot's centre, camera and crowd are in the town's own coordinates and move
  with the offset scene.json records, so other terrain settings need no new numbers. The town's own land and
  its stand-in country (cells with fewer than two objects) left out; the box of its cells becomes `holes` in scene.json,
  where the overland's land and objects are removed and ground at the town's street level fills in. Round the hole
  the overland is levelled to the town's ground over 8192 units (the overland there runs from 2700 to 5500, the
  town's streets 2297: it sat in a pit), as Daggerfall levels the land at its places. Stros M'Kai's camera follows the
  town on the stretched island (moved 8500, 3500: its buildings matched between the exports).
- **Towns split over worldspaces** (user, 2026-10-09: "the games have interiors and exteriors so the worldspace needs
  to be correct"): Oblivion's Imperial City is a worldspace a district on Tamriel's coordinates (Tamriel holds only its
  walls, tower and Waterfront), so the shot exports Tamriel plus the Market, Temple, Arena, Elven Gardens, Arboretum,
  Arcane University, Talos Plaza, Prison and Palace districts (not the main quest's MQ16 variants): 7261 objects and
  113 people instead of 1895 and 23. Redguard's palace courtyard is its own worldspace on the island's coordinates
  (EXTPALAC: columns, lanterns, doors, 9 people), exported with the island. Each district repeats the walls, the tower
  and the shore, so with several worlds an object at the same place with the same model is kept once, and each land
  cell comes from the world with the most objects in it (the overland's on a tie) - but only land painted in all four
  quarters: a district's land outside its walls is never seen from inside it and has no textures, and it showed as a
  ring of the game's default dark dirt round the city (user, 2026-10-09: "this does not look right").
- **Water by the part, not the name** (user, 2026-10-09: "with vivec why are the drains miscolored"): a model's part
  takes the water material only when it has no texture of its own (Skyrim draws it with its water shader) in a model
  named for water, a river, stream, falls, pool or trough, or when its own texture is a water surface's (not a
  waterfall's). Named-for-water models are often not water: Vivec's stone drain spouts (ex_vivec_waterspout_*) came
  out as clear water and their falls lost their foam; Whiterun's untextured stream past the Gildergreen showed white.
  Water running down a slope (over 150 units of drop) is rough and pale, not a mirror.
- **Trees in leaf** (user, 2026-10-09: "wheres the trees"): Skyrim's trees and plants keep how much each vertex sways
  in the wind in the vertex colours' alpha (0.25 to 0.4 on average on the aspens' branches); taken as transparency it
  cut nearly every leaf away. Tree and plant models now take their cut-out from the texture alone. The sky's light is
  2.2 times what Cycles needed (blend_scene SKY_FILL): EEVEE gathers less bounced light, and shadowed ground (the
  Imperial City's long morning shadow) went near black. Morrowind's Vivec is exterior in
  Vvardenfell itself (only its insides are interior cells). The Imperial City's orbit rose to look over the walls
  into the districts (eye 14000 high, 34000 out; streets at about 3500, the tower's top near 17500).
- **Only the games converted** (user, 2026-10-09: "with a persons converted or owned games equivulant the flybys will
  be different depending on what game they own"): `export` leaves out a shot whose plugin isn't converted, and every
  later step (paths, renders, upscale, pack) works on the shots exported, so the loop holds the person's games only.
- **Music** (user, 2026-10-09: "write a musical score to replace the skyrim menu music"): `tools/menu/menu_theme.py`
  writes an original orchestral theme as MIDI (D minor, 66 bpm, 58 bars, about 3 min 40 s; intro, the theme in the
  horn, the strings' answer, a rising second phrase, a climax turning to D major, a flute interlude, a coda fading
  into the opening so it loops). `TamrielMerge.Cli menu-music` plays it with FluidSynth and GeneralUser GS (hall
  reverb), normalises it and encodes `music\special\mus_maintheme.xwm`: the file the main menu plays, named by the game
  setting sMainMenuMusic:General (its default in the executable is `\Data\Music\Special\MUS_MainTheme.xwm`; it isn't a
  music type), so no record changes. The first builds wrote `mus_intro.xwm`, MUSSpecialIntro01's track, taken for the
  menu's: the menu went on playing Skyrim's theme (user, 2026-10-11: "why does skyrims music take priority"), and
  Skyrim's intro music was replaced instead. `convert` runs it with the main menu step, without
  needing `--gpu`. FluidSynth, the soundfont and WildMIDI ship with the tool (they were only in the repository's
  tools folder; Daggerfall's songs use them too).
- **Without Blender** (setup normally fetches it) the raster renderer `render_scene.py` draws the shots: it levels
  the land round a placed town and fills its hole too, but its walkers keep the older straight walks, without the
  planned routes and spacing.
- **Skyrim** (user, 2026-10-08: "we never added skyrim to the flyby"): the first shot orbits Whiterun, exported from
  Skyrim.esm in the game's Data folder (`"skyrim": true` in the shot's export; the scene exporter doesn't load
  Skyrim.esm twice). Skyrim's ground textures are dark under the grass the game grows on them, which the export
  doesn't have, so the shot's `"land_tint"` warms and brightens the land, and its `"ground"` carries the tundra past the
  land's edge to the horizon (else the sky shows below it and reads as a sea).
- **Land to the horizon** (user, 2026-10-08: "why is the ground sinking in the video", "and the others"): a shot's
  export may reach further for land than for objects (`"land_radius"`, scene-export `--land-radius`): Whiterun 70000,
  the Imperial City 60000, Vivec 50000. With land only within the objects' circle each town stood on an island whose
  edge dropped away to the plain or water. Ground beyond the land continues at the level of the land's edge (low
  among its border cells), and Daggerfall's ground (it has no land) at the level of the town's object bases (low
  among them, not the lowest: cellars and foundations sit far below the streets).
- **Whiterun whole** (user, 2026-10-08: "was whiterun models whole on its video"): Tamriel holds only Whiterun's
  distant-view stand-ins inside the walls (Dragonsreach, Jorrvaskr, the houses and districts as *LOD.nif); the real city
  is its own worldspace on the same coordinates. The shot exports `Tamriel+WhiterunWorld`: the city's cells and land
  over Tamriel's, Tamriel's *LOD.nif left out (6759 objects, 128 people: the townsfolk, the Gildergreen, the market).
  Blender then needed more memory than is free beside the paused voice generator (10 GB), so Whiterun's textures are
  320 px and land cells beyond 30000 units from a scene's centre are built at 9 x 9 points (blend_scene FAR_LAND).
- **Level orbits** (user, 2026-10-09: "ground is sinking again", "its all"): every shot is now an orbit at one height,
  one aim height and one distance. Rising cameras slid the landscape down the frame, and closing in (or Daggerfall's
  glide up its boulevard: 3 to 4 px a frame downward in the lower third, measured with optical flow) streamed the
  ground downward; now the lower third moves under 0.1 px a frame vertically, only sideways as the camera circles.
  Daggerfall's orbit starts looking up the boulevard. Beyond the land, the ground plane only covers cells the land
  lacks (a ring plus fill), so it can't flood valleys inside the land.
- **Sideways passes, not orbits** (user, 2026-10-10: the terrain still sinking, "its when it rotates"): a camera
  circling a point while looking down at it turns the ground like a tilted plate, one side of the picture sinking while
  the other rises. Measured with optical flow on the rendered frames: the right side down and the left side up by about
  0.5 px a frame at 1200 wide in every shot, while the average over the lower third stayed at zero (which is why the
  earlier check passed). Each shot now slides its eye and aim together along the camera's sideways axis, as far as the
  old arc's length through its middle, at the same height, distance and aim. Ground points projected along the paths
  move vertically by 0.4 to 1.1 px a frame on the orbits and by none on the passes. `"circle": true` in a shot's orbit
  keeps the old orbit.
- **Roads** (user, 2026-10-11: "did you fix the roads in the flyby"): two faults, both in the tools.
  - Whiterun's roads showed as black trenches. Skyrim's road meshes (meshes\landscape\roads: RoadChunk*,
    RoadStraight*, RoadCurve*, a road surface and a dirt fringe sloping into the ground) are textured from
    textures\landscape\ (road01 averages 58 of 255, dirt02 44), dark like all of Skyrim's ground textures, which the
    shot's `land_tint` brightens about 5x on the land. Untinted, beside the land they read as black channels.
    blend_scene now gives the road models the shot's land tint. Only the road models: tinting everything textured
    from textures\landscape\ turned Whiterun's rocks (mountainslab, rocks01) white.
  - The Imperial City's black crack down its hillside wasn't the land data (its cells' edges match) but blend_scene's
    coarse far land: cells beyond FAR_LAND (30000 from the scene's centre) are built at 9 x 9 points, and where one met
    a full 33 x 33 cell the edges didn't (up to 232 units apart); the city's camera orbits 34000 out, so that boundary
    ran through its foreground. A full cell's edge beside a coarse one now follows the coarse one's straight runs, so
    they meet exactly.
  - The black line was a road after all (user, 2026-10-11: "thats a road"): the Imperial City's cobbled road west of
    the walls, painted on the land in cobblestone01. A land chunk (6 x 6 cells) keeps only its 12 most painted layers
    (EEVEE's 32 textures a material), ranked by their mean over the chunk, and a road is a thin strip: cobblestone01
    averaged 0.006 there and was left out with 12 others (the roadstones too). The kept weights were then divided by
    their sum, floored at 0.05, which left the road, almost all cobblestone01, black. Now the weight the left-out
    layers carry (1 minus the kept weights) takes their colour: their textures' average, weighted by how much each is
    painted in the chunk (layers_colour).
  - Roads painted on the land are kept first (user, 2026-10-11: "its roads almost like oblivion had its roads"):
    ranked by weight alone, Whiterun's dirtpath01 came 15th of the 21 layers in the city's chunk and was left out
    (smaller chunks don't help: 20 layers in 3 x 3 cells round the Imperial City). A chunk now keeps the layers whose
    names say road, path, cobble or street before the rest.
  - Black streets between Whiterun's walls and plots (user, 2026-10-11: "the roads of the city"): they rendered black
    only in the tool's renders, never in Blender run by hand. The tool took Blender 5.1.2 (the newest blender.org
    folder) while the hand runs found Steam's 5.2.2, and 5.1's EEVEE drew the enclosed streets black (1.9% of the
    city's pixels; 0 with 5.2). Setup/Dependencies.Blender and intro.py now take the newest Blender by its own
    version, and setup downloads 5.2.2. Along the way: one-sided shapes show only their front (material backface
    culling, as the game draws them), the sun's shadows at 0.6 resolution (Whiterun asked for about 3670 shadow
    pages a frame of the 2048 allowed, "Shadow buffer full"), and the scene exporter keeps vertex colours only where
    the shader uses them.
  - The horizon (user, 2026-10-11: "look up top whats rendered up there"): the stand-in ground past the land ended at
    5.7 km, just under the horizon, and the sky model's own ground showed past it as a blue band. It now reaches the
    camera's 20 km and fades out (blended) from 4 to 12 km, and camera rays below the horizon see the horizon's own
    colour (the light still comes from the sky's own: lit from a bright lower half the whole place washed out).
  - Renders take blend_scene.py from the tool's own copy (bin\tools) when it is there: after a script change, rebuild
    the tool (or copy the script) before rendering, or the old one runs.
  - Inside Whiterun's walls the streets are the city's own pieces (WRMainRoadPlains, WRMainRoadMarket, the house
    platforms: wrstonefloor02 about 60 of 255, the slate edging about 28 with its vertex shading) and really do run
    below the raised house platforms between stone retaining walls, as in the game. In the shade between the plots,
    untinted beside the tinted land, they still read as black (user, 2026-10-11: "black still"), so a shot may name
    textures that take its land tint too (`"tint_textures"`, by how their names start): Whiterun's are its ground
    (wrstonefloor, wrslate, wrbasedirt, wrdirtbase, wrfieldgrass, wrstonebase) and its walls' grassy tops
    (wrcitywalltopcap, 40 of 255 with a ragged cut-out edge: a black wavy strip along every wall), not its walls or
    roofs.
  - The black blobs left on Whiterun's streets in game shading (user, 2026-10-11: "the black road patches are there
    still", "is it two different road textures"): two textures do lie there, the street stone (wrstonefloor02) and a
    grass sheet (wrfieldgrass01) just above it that its vertex alpha cuts away over the street, as the game's alpha
    test does. Not water (Whiterun's water plane is at -14000, 10600 units under the streets), not inverted faces
    (normals and windings checked), not the lighting (black unlit too). Drawn dithered, EEVEE's depth pass kept the
    sheet where its shading pass cut it, so the stone behind was hidden and nothing was drawn: black. A cut-out cut
    by its vertex alpha is now drawn blended (`surface_render_method`): the cut is 0 or 1, so the edge is the same.
    Plants keep dithered (their vertex alpha is wind sway, not cut). Also: texture times vertex colour is a Vector
    Math multiply (the colours only), and the land's layer textures load without their alpha (Skyrim keeps specular
    there).
  - Trees and mountains to the horizon (user, 2026-10-11: "the trees arent there wither"): objects stopped at the shot's
    `radius` (Whiterun: 22000) while its land reaches `land_radius` (70000), so the hills and mountains past it stood
    bare. Past the radius the scene exporter now keeps trees (`meshes\landscape\trees\*tree*`, not stumps, driftwood or
    branch piles) and mountains (meshes\landscape\mountains\) out to the land's reach, as the game's distant view does:
    Whiterun 7502 more (6418 trees, 1084 mountain pieces), frames 13 to 16 s instead of 9.
    Converted games: Oblivion's great-forest rocks of size 200 and up (rockgreatforest200+, as its own distant view
    keeps the big ones) and its mountain and cliff rocks, Morrowind's terrain rocks (terrain_rock_*), its trees
    (flora_tree_*) and emperor parasols.
  - Grass (user, 2026-10-11: "theres some shadows or something here", "do the grass"): the dark streaks along
    Whiterun's street edges were the land between the city's pieces, about 150 units under the street, painted
    fieldgrass01 - in the game its grass grows there. The scene export now writes each land texture's grass (LTEX ->
    GRAS: model, density, slope limits, distance from water above or below, position, height and colour range, fit to
    slope; terrain "grasses", by texture) and the grass models; blend_prep converts their textures. blend_scene grows it
    as the game does: on a grid 60 units apart (iMinGrassSize; wider when the circle would hold over 750000 points:
    at most 300000 grasses, as real geometry Daggerfall's 524000 took Blender to 21 GB of 32 and stopped the render;
    the far trees and mountains are instances, one geometry a model, for the same reason - 22 GB before) within the scene's radius - on far cells at the coarse heights their mesh has (Daggerfall's town fills 15 x
    15 cells of holes, so a circle short of the far land held no land at all), each point takes a land texture by its painted weight there, then the first of its grasses its density lets grow,
    if slope and water allow; moved by its position range, scaled by its height range, shaded by its colour range,
    tilted to the slope. No shadows (the game's grass casts none). Each kind is merged into one mesh (geometry nodes,
    realized): kept as 194000 instances, EEVEE walked them on the CPU every sample and a frame ran over 20 minutes with
    the GPU idle. A shot can set `"grass": {"radius", "step", "max"}` or `"grass": false`.
  - Weather and volumetrics (user, 2026-10-11: "the weather needs done too", "and volumetrics"): lighting.json now holds
    the weather's sky colours for each time of day (upper, lower, horizon, sun, sun glare, fog near and far, cloud LOD),
    its fog distances (near, far, power, max), its cloud layers (texture, speed, colour and alpha each time of day) and
    its volumetric lighting (VOLI: intensity, colour, density, phase scattering); the scene export copies the cloud
    textures and blend_prep converts them (up to 1024). In game shading the camera sees the weather's sky (horizon to
    lower to upper by height, the sun's disc and glare, the cloud layers drifting at their speeds: the horizon's each
    on one of the four bands Skyrim's horizon texture holds, wrapped round the horizon to about 13 degrees; the rest
    projected on the dome and faded out toward the horizon, where they crowded into streaks; the fill layer tints the
    whole sky at its alpha). The fog is the game's own formula on every pixel by its depth, in the compositor:
    min(max, ((distance - near) / (far - near)) ^ power), its colour from the near fog colour to the far as it
    thickens, the sky left unfogged as the game draws it (a fog volume fogged the sky away and washed Whiterun out).
    The haze box carries only the volumetric light: the sun scattered forward (its phase scattering) by thin air, its
    shafts shadowed; none when the weather has no VOLI (Daggerfall's). Whiterun: SkyrimCloudy, VLCloudyDay; the
    Imperial City, Vivec and Stros M'Kai SkyrimCloudy; Daggerfall DFSkyrimCloudy.
  - The clouds on the game's own cloud mesh (user, 2026-10-12: "what is that" - streaky curtains under the clouds;
    "the games have a skydome mesh"): Skyrim draws a weather's cloud layer k on shape k of meshes\sky\clouds.nif
    (29 shapes, CDUpper_04 overhead and its _E and _W, CDUpper_01/02, CDDome_Horizon, CDTop, CDLowerDome_Horizon_I,
    CDHorizon_1-4 each on a quarter of the horizon texture with _E/_W, CDLower, CDFog the ring on the horizon; their
    order matches the weather guides' layer 0 overhead, 1 east, 2 west, 28 the horizon). lighting.json gives each
    layer's slot ("layer") and the exported cloud mesh ("cloud_mesh", from the scene export); blend_scene draws each
    enabled layer's shape (Builder.sky_clouds) 24 times its size (about 12 km out), following the camera, unlit: its
    texture times the layer's colour, see-through by the texture's, its vertices' and the layer's alpha, drifting at
    its speed, later layers in front. The projected clouds of before are gone. Vivec's SkyrimCloudy: the lower
    texture on layers 8-11 (CDDome_Horizon, CDTop), the horizon's on 16, 21, 22, the fill on 28.
  - The floating rock over Vivec is Baar Dau, held over the Ministry of Truth as in Morrowind.
  - Sunsets (user, 2026-10-11: "fix the sunsets"): Vivec's and Stros M'Kai's skies came out blood red. Two causes:
    the grade's contrast pivoted on mid-grey, so the dusk image space's 1.5 crushed the dusky sky to near black and red
    (the game's pivots on the picture's average brightness: intro.py game_grade); and a shot took its sunrise's or
    sunset's colours whole though its sun stood 11 to 22 degrees up, where the game is still moving from its day colours.
    Now every colour of a shot's time (sun, ambient, sky, fog, clouds and their alphas, volumetric light, grade) is
    between the day's and the sunrise's or sunset's by the sun's height (intro.py game_blend, written as the path's
    "game_blend"; blend_scene and pack mix them): all day at 10 degrees or more, all sunset at the horizon, as the
    game turns to its sunset colours only in the last stretch before the sun sets (from 25 degrees, 2026-10-12: "vivecs
    sky again" - Vivec's, its sun at 11, was still half sunset, a pink sky). The shots (11 to 22 degrees) are all day
    colours, lit by their low suns. The horizon cloud
    bands fade in over the first degrees above the horizon (cut off there, their bottom row of texels showed as
    streaks over a hard edge).
  - The other games' grass: Daggerfall's land grows Skyrim's (its overland uses Skyrim's land textures), Stros M'Kai's
    only its sea bed's kelp and coral, Vivec none (Morrowind's land has no grass). The Oblivion conversion made its land
    textures without Oblivion's grass, so converted Cyrodiil grew none, in the game too: Oblivion/ObWorld now converts
    each land texture's grass (GRAS: model converted, density, slopes, water, ranges, wave period, flags). Its grass
    models (Plants\GroundCover*.NIF and the rest a land texture grows) are NetImmerse 10.0.1.2, older than the rest of
    Oblivion's, which nifly can't read; Oblivion/ObOldNif.cs reads that version's blocks (nodes, shapes and strips,
    texturing, source texture, alpha, vertex colour, material, stencil; a zero word before each block, no "has
    triangles" flag) into the converter's pieces. Reconverted 2026-10-11: the Imperial City grows 27 kinds.
  - The full flyby (2026-10-11) through `menu`: the earlier work in out/menuart/intro moved to intro-preweather (a
    render skips frames already there).
  - Dev aids in blend_scene.py, all off by default: `TM_IDPASS=1` renders every object one flat colour, unlit and uncut,
    and writes idpass.json naming them and idpass_textures.json their textures (which object a pixel shows); `TM_PROBE="x,y;..."` ray-casts pixels without
    rendering, and with `TM_PROBE_ALL=1` lists every surface along each ray; `TM_UNLIT=1` renders base colours unlit.
    A render skips frames already in its output folder: use a fresh folder for each test.
  - The scene exporter uses a shape's vertex colours only when its lighting shader has them switched on
    (HasVertexColors, SLSF2_Vertex_Colors), as the game does; it counts dark ones it leaves out. Whiterun had none.
  - The Imperial City's hillside had a black seam. A worldspace that uses its parent's land (UseLandData: WhiterunWorld,
    every Imperial City district) is drawn on the parent's in the game, and its own land records are unused leftovers at
    other heights. The scene exporter took them for 12 of the city's cells (up to 880 units off); it now leaves out
    the land of such a worldspace. `scene-worlds` lists each world's land count and its parent's flags.
