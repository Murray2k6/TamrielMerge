# TamrielMerge's parts (the tool's mod organizer)

The app's **Mod organizer…** (TamrielMerge.Gui/PartsDialog.cs) switches TamrielMerge's parts on or off (user, 2026-10-10:
"can we add our own mod organizer to the tool", chose "TamrielMerge's parts"). Each part (Setup/Parts.cs) is its plugins:

| Part | Plugins |
|---|---|
| Daggerfall | Daggerfall.esm, Daggerfall Land.esm, Daggerfall Towns.esm, Daggerfall Dungeons.esm, Daggerfall Overland Doors.esp |
| Redguard | Redguard.esm |
| Morrowind | Morrowind.esm |
| Oblivion | Oblivion.esm |
| Story and travel (needed, always on) | TamrielMerge Start.esp |
| Progression | TamrielMerge Progression.esp |
| Voices | TamrielMerge Voices*.esp (+ their archives; docs/voices.md) |
| Main menu and music | TamrielMerge Menu.esp (+ archive) |
| Remade models | TamrielMerge Models.esp |
| Creatures | TamrielMerge - Creatures.esp |
| Creature models | Daggerfall / Redguard / Morrowind / Oblivion Creatures.esp (one per game) |

Switching a part unticks or ticks its plugins in Skyrim's own Plugins.txt and, when Mod Organizer 2 manages the game, its
selected profile (Setup/PluginList.cs; MO2 must be closed). Nothing is deleted from the game folder. A part's files are
its own (paths under tamrielmerge\..., or in its archives), so off, nothing of it is used. The two parts that replace
Skyrim's own files are archives of their plugin, so off brings Skyrim's back: the voices (docs/voices.md) and the main
menu and music (`menu-pack`, Skyrim/Render/MenuArchive.cs: the logo meshes, emblem textures, menu fog, flyby sheets and
music\special\mus_maintheme.xwm, packed and the loose copies removed from the output; `convert` runs it after the menu).
The order of the parts is the load order (the games' story order); installs enable missing plugins in that order.

**Masters** (user, 2026-10-11: "are they set for when a user does not have a certain game"): a plugin is never left on
without its masters, or Skyrim stops loading. Switching a part off also switches off every installed TamrielMerge plugin
that needs one of its plugins, directly or through another (the procedural creatures and the creature models need each
game they cover); switching a part on leaves off any of its plugins whose game is off or not installed, and says which
(Parts.Dependents, Parts.Switchable; `parts-deps --off <plugin;...>` shows what the switches would do, and
`parts-switch --part <name> --on|--off` does it from the command line).

Missing games otherwise: only the games owned are converted (store detection), and each game's plugins need only Skyrim's
and that game's own. TamrielMerge Start.esp and Progression.esp need only Skyrim.esm: their scripts (TMStoryOrder,
TMProgression, TMNightDanger, TMPickpockets) find each game's records while the game runs (Game.GetFormFromFile) and
skip a game that isn't there. The procedural creatures plugin is built over the games converted; the main menu's flyby
has a shot only for the games converted.
