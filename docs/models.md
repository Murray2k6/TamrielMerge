# Viewing and replacing models

User request (2026-10-08): "a model viewer and model replacer by viewing models in the tool", replacements "from any
of the user's games".

**In the app:** Models… (next to Convert) lists every model the converted plugins in the output folder use (about
14,000; by game, kind of record, how many records use it, and stand-ins marked "Skyrim's"), searchable. The selected
model turns round in a preview (drag to turn it). Replace with… picks any model the user's games offer (about 47,000):
Skyrim's own (archives and Data), and Daggerfall's, Redguard's, Morrowind's and Oblivion's as converted (installed
under meshes\TamrielMerge, or not yet installed in the output folder), each previewed the same way.

**How a replacement works:** the choices are kept in `model-overrides.json` in the output folder (used model ->
replacement). "Build the replacements plugin" (and every Convert) writes `TamrielMerge Models.esp` (ESL): every
record of the converted plugins that uses a replaced model, overridden with the replacement's path and object bounds
measured from the replacement itself; texture swaps of the old model are dropped. No file is overwritten, so leaving
the plugin out undoes all of it. It loads after the converted plugins.

**Commands** (Skyrim/Models/ModelCatalog.cs): `models-index [--available] --dir DIR [--out f.json]` (the lists; the
used list is cached in models-index.json until a plugin changes), `model-preview --model M --out DIR [--loose DIR]
[--frames 24]` (scene-mesh + tools/scene3d/preview_model.py; previews cached in out\model-previews),
`models-apply --dir DIR`.

## Textures and PBR

User request (2026-10-08): "a texture selector with the model viewer with a pbr tool", PBR "our own without community
shaders".

**Textures & PBR…** in the Models window shows the selected model's parts (as the record uses it: its replacement or
PBR copy if it has one) with their eight texture slots pictured. **Change…** picks any texture from any of the user's
games (about 34,000: Skyrim's and the converted games'; normal and helper maps hidden unless asked for). Swaps are kept
in `texture-overrides.json` (model -> part -> slot -> texture) and built into TamrielMerge Models.esp as a texture set
per swapped part, given to every record using that model as Skyrim's alternate textures (MODS): no file changes.

**Make a PBR material…** (tools/textures/pbr.py) turns the part's colour texture into a physically based set:
normals from an AI model (DeepBump, GPL-3.0: downloaded by setup into tools\pbr, run by our own tiling code, wrapped
so textures stay seamless), height integrated from them (fine relief only), roughness, metalness and crevice
occlusion derived and tuned by sliders, shown live on a material ball and flat swatch (about 4 s a change; the AI's
normals are cached). Skyrim can't draw PBR without Community Shaders, so the set is baked into what its own shaders
draw: occlusion into the colour, roughness into the normal map's alpha (shine) and the shader's glossiness and
specular, metalness and polish into an environment mask with Skyrim's cube maps (environment-map shader), height into a
parallax map (parallax shader). Skyrim has one or the other per part: reflections when the surface is metal or truly
smooth, parallax otherwise. **Bake onto the model** writes a copy of the model (meshes\TamrielMerge\pbr\<hash>.nif,
built on by later parts) with the part's textures and shader switched, the textures beside it
(textures\TamrielMerge\pbr), and replaces the model with the copy.

Commands: `model-textures --model M`, `textures-index --out f.json`, `texture-thumb --texture T --out f.png`,
`pbr-make --texture T --out DIR [--params JSON]`, `pbr-apply --model M --shape I --material DIR`.

Licences: DeepBump's model is GPL-3.0 (downloaded, not shipped); the intros' Depth Anything V2 Base is CC BY-NC 4.0
(non-commercial), like the voices' XTTS-v2.

## Models shipped with the tool

The tool carries the finished models it uses (user, 2026-10-10: "update tool with meshes that we have that are
downloaded including the redguard ship so they get installed if a users game is detected and converted"), copied
beside it under tools\ by the build (TamrielMerge.Cli.csproj), so a user's conversion builds and installs them with no
model making of its own:

- **The ship** (tools\ship: ship.fbx, its textures, links.json, and the parts and collision Blender exported): a free
  CGTrader model (cgtrader.com/free-3d-models/watercraft/historic-watercraft/ship-8d79bbda-ccc3-45f9-8b38-ec81c5a4d0cd),
  for Redguard's ships in Stros M'Kai's harbour (ShipModel; written into meshes\tamrielmerge\ship when Redguard is
  converted) and, once its "your ship" locations are built, Daggerfall's.
- **The sprite models** (tools\model3d\work\models\<sprite>\: mesh.obj, texture.png, collision.obj, input.png): the
  models made from Daggerfall's sprites that Skyrim has nothing for (SpriteModels), imported into meshes\TamrielMerge\DF
  whenever Daggerfall is converted (DaggerfallPluginBuilder.ImportSpriteModels). Only what the importer reads is shipped,
  not the model maker's working files; `sprite-models --make` still makes new ones where TripoSR is set up.

New models (the bat and imp to come) go the same way: their finished files in tools\, listed in the csproj.
