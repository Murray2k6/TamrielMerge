# Models made from Daggerfall's sprites

Skyrim has no seagulls, palms, cacti, giant mushrooms, iron maidens, model houses, rat statues... Daggerfall drew them as
sprites. The converter makes a 3D model of each from its own sprite (user choice 2026-10-07: AI-generated from the
sprites), then uses it wherever Daggerfall placed that sprite, instead of the nearest vanilla stand-in.

## Which sprites

`Skyrim/SpriteModels.cs` (`Targets`, 105 sprites): the seagull (201.11); rainforest, subtropical and swamp palms, bulb
plants, coral and glowing orbs (500-502); desert cacti, yucca and agave (503); giant mushrooms (504, 505, 508, 509);
rat and spider statues (98); the odd things of 211 (rags, tripods, scarecrow, rocking horse, noose, scales, pipes, iron
maidens, pillory, model houses, banners, bell, necklace, goblet, sceptre, portraits, scarab, chest, idol, painting);
the scholar's things of 208 (globe, magnifier, scales, telescope, mirror, hourglass); tools of 214 the table left out.
Each has a kind: prop, plant, tree or hanging. Since 2026-10-12 also 523 of Daggerfall's scenery sprites (statues,
furnishings, columns, remains, stalactites, crops, every climate's trees and plants; 628 in all).

## How a model is made

`sprite-models --make [--gpu]` (also a step of `convert`, before Daggerfall is built; `--skip Models` leaves it out;
the GUI's "Missing models" option):

1. **Export**: each sprite as an RGBA PNG (Daggerfall's palette, index 0 transparent) and `sprites.txt` (name, path,
   face budget: trees 8000, the rest 5000) in `tools/model3d/work`.
2. **Upscale**: Real-ESRGAN x4plus (twice for sprites under 96 px), colour and alpha separately. Enlarged pixels make
   blocky slabs; upscaled shapes give TripoSR form to read depth from.
3. **TripoSR** (VAST-AI-Research/TripoSR, MIT; weights stabilityai/TripoSR from Hugging Face, cached in
   `tools/model3d/hf`): the sprite centred on grey at 512 px; its solid carved by the sprite before it becomes a
   mesh (`carve.py`): what falls outside the sprite's outline seen from TripoSR's camera goes (the gap between a
   statue's staff and its robe), and parts under 24 px across are kept within about their width of their middle front
   to back, so poles and legs are rods, not fins; the solid blurred by 1 voxel (that is the smoothing: smoothing the
   finished mesh, Taubin, as until 2026-10-12, melted thin parts, a staff into the robe, a crown into a cone, a square
   plinth into a blob; user: "fix the robed figure"), except pieces too thin to live through the blur (a few voxels
   across, 30+ voxels), which keep their unblurred shape (pieces floating free stay: sparkles over a dirt pile are
   in the sprite, and without them the model is fitted to the sprite's height stretched);
   marching cubes at 192, simplified to the face budget, texture
   baked into a 1024 atlas (xatlas + moderngl); a 150-face collision mesh of the same shape.
   **Painted with the sprite** (`reproject.py`): every texel takes the sprite's colour through TripoSR's camera
   (matte, Daggerfall's own colours); sides seen edge-on take the sprite's colour there shaded by the bake's light and
   dark only (its hues are noise: green and pink specks).
   **Scored**: the outline through the sprite's view against the sprite's (intersection over union, as `check.py`),
   in `score.txt`. The importer leaves out models under 0.65 and the stand-in stays: parts 2-3 px wide in the sprite
   (a spider statue's legs, a cage's bars) are thinner than TripoSR's grid, which makes a mangled body of them.
   Patched for Windows: PyMCubes for torchmcubes (no CUDA compiler), rembg optional, baking on the model's device.
4. **GPU care**: only with `--gpu` (else CPU, hours); between sprites the script reads nvidia-smi and waits while the
   card is above 78 C. About 4-5 s a model on an RTX 5060 (2026-10-12, user: "its slow too": the painting drawn on
   the GPU instead of a triangle at a time in Python, the edge colour fill by a distance transform, TripoSR's density
   queried in chunks of 262144 points, not 4096; it was 10-11 s). Finished sprites are skipped, so a run resumes; ones made by an older pipeline
   (`pipeline.txt`, now 5) are made again.

The model maker lives in its own environment (`tools/model3d/.venv`, made by `setup`: PyTorch CUDA or CPU, omegaconf,
einops, transformers, trimesh, PyMCubes, xatlas, moderngl, imageio, spandrel, fast_simplification, scipy; PyTorch and
torchvision pinned together, and with an NVIDIA card setup's check asks for the CUDA build unless `--cpu` chose the CPU
one: until 2026-10-12 an unpinned torchvision had swapped in the CPU build and every model was made on the CPU,
30-130 s each instead of ~11 s, without a word); its code (`sprite2mesh.py`, `carve.py`, `reproject.py`,
`TripoSR/tsr`) ships next to the converter.

## How a model is used

`Skyrim/Meshes/SpriteModelImporter.cs`, during the Daggerfall build: TripoSR's frame (x towards the viewer, y across,
z up) turned into Skyrim's, stood on z = 0 (hanging things hang from it), centred, scaled to the sprite's own height in
the world (its TEXTURE size x the build scale); `meshes\TamrielMerge\DF\Models\<name>.nif` (one BSTriShape, double-
sided, the baked texture as `textures\tamrielmerge\df\models\<name>.dds` with mips, flat normal map) and a static
`DFSpriteModel<name>`. **Collision** follows the model's shape: the collision mesh, one convex slab per triangle;
trees stand on a trunk column up to 60 % of their height so their crowns can be walked under.
The clutter placer (`ClutterPlacer.Generated`) puts the static wherever the sprite stood, in dungeons, building
interiors and streets, turned at random; sprites without a made model keep their vanilla stand-in.

Dev aids: `sprite-models --export`, `--import DIR` (the finished models into a scratch plugin), and
`tools/model3d/preview.py DIR` (front, side and top views of each model next to its input).
