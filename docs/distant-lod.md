# Distant LOD

The converted worlds' distant view (user, 2026-10-13: "need a generate lod button for the tool too", "distant lod",
everything: land, objects and trees). The app's **Distant LOD** button runs `lod`; `convert` runs its objects and trees
after the creature models (the land is made with each game's build). Code: `Skyrim/Distant/`.

```
TamrielMerge lod [--dir out/convert] [--out DIR] [--only terrain,objects,trees] [--worlds A,B] [--plugins Morrowind.esm,...] [--install]
```

Every exterior worldspace the converted game plugins add (Daggerfall's, Redguard's, Morrowind's, Oblivion's) gets:

- **Land**: `Skyrim/TerrainLod.cs`, from the winning cells' landscape (the Iliac Bay a 128-cell region at a time).
- **Objects**: every placed static big enough to see from afar, as chunks at levels 4, 8 and 16:
  half its largest extent at least 220 units (level 4), 450 (8), 900 (16). Deleted, initially disabled and
  enable-parented references are left out. A static's own distant meshes are used when it has them (Skyrim's);
  otherwise its model is made coarser for the level (vertex clustering, a fourteenth, an eighth and a fifth of its size),
  and cut-out parts (railings, ivy) are dropped beyond level 4.
- **Trees**: in the same chunks, two crossed cards with the tree drawn from the side (lit, its leaves cut out); all
  trees at level 4, every second at 8, every fourth at 16. Being in the object chunks, a loaded cell hides its trees'
  distant copies with its objects', and no load-order-bound tree list (.lst/.btt, which names references by FormID) is
  needed.

**Textures**: one sheet per worldspace like Skyrim's `tamriel.objects.dds`: 4096-pixel DXT5 pages of 128-pixel slots
(120 inside a 4-pixel border of the texture's own edge), and 16-pixel swatches. A shape whose UVs stay within 0-1 is
mapped into its texture's slot; a repeating one (a wall, a road) takes its texture's average colour from a swatch.
The normal map is flat.

**Chunks** follow Skyrim's own (`tamriel.4.0.0.bto`, `tamriel.8.0.0.bto` from Skyrim - Meshes1.bsa):

- NiNode `Obj` → BSMultiBoundNode with its AABB → BSSubIndexTriShapes named `Obj`.
- Local units 0-4096 across the chunk, scaled by the level (the shape's scale); full-precision vertices.
- BSLightingShaderProperty flags 0x80400000 / 0x5 (0x15 with double-sided for the cut-out cards, which also get
  NiAlphaProperty 0x12EC / 128).
- At level 4 each shape's triangles run cell by cell: segment `(x - x0) * 4 + (y - y0)`, by the reference's own cell.
  The game hides a loaded cell's segment (in Skyrim's files 16 segments, trailing empty ones left off; read across all
  517 level-4 chunks). Levels 8 and 16 have one segment.
- nifly can't write sub-index segments, so `BtoWriter` saves BSTriShapes and patches the file: the type renamed, each
  shape's segments appended in Skyrim SE's form (a count, then per segment a flags byte, first index, triangle count),
  checked against each shape's triangle count.

Written as `meshes\terrain\<world>\objects\<world>.<level>.<x>.<y>.bto` and `textures\terrain\<world>\objects\`, the
old ones cleared first. `lodsettings\<world>.lod` is written when the land didn't write it. The work is staged on a
solid-state drive when `--out` is on a hard disk (docs/building.md).

Checked offline (2026-10-13) on Redguard's five worldspaces: Stros M'Kai got 119 land chunks and 28 object chunks from
754 objects and trees in 1.7 minutes. The chunks read back with Skyrim's layout and segments, and a preview showed the
town in place. It hasn't been seen in the game yet.

## The world map

Skyrim's map menu draws the current worldspace's land LOD in 3D, within its map data (MNAM: the area the camera may
pan over, its lowest and highest height, its pitch). User, 2026-10-16: "we need a world map when we open the map menu
in 3d like in skyrim". Every converted exterior has land LOD and its own map data, with these exceptions:

- **Daggerfall's towns** are worldspaces of their own: no land, in the town's own coordinates. Vanilla's cities show
  Tamriel's map. Most share Tamriel's coordinates; Blackreach does not, and takes Tamriel's map data with a scale and
  offset (ONAM 2.5, -8192, 73728, -20480). The same is done for Daggerfall's towns: the overland tile they stand on is
  their parent for its map data only, and their ONAM offset is the town's spot on that tile, scale 1, so the map shows
  the Iliac Bay with the player where the town is.
- **The Iliac Bay** is 1000 x 500 cells, against Skyrim's map of about 120 cells across. Its highest camera is raised
  in proportion, at most 3 times vanilla's (240,000 units), so a zoomed-out map shows a region of the bay.
- Oblivion's cities already take Oblivion's Tamriel's map data, as in Oblivion. That world, which has no name of its
  own, is named Cyrodiil.

`world-map-info [--data DIR] [--plugins a.esm;b.esm] [--all]` lists what the map reads from each worldspace (MNAM,
ONAM, parent, flags, the land LOD chunks there), next to vanilla Tamriel and Solstheim (`--all`: every vanilla world).
This hasn't been seen in the game yet.
