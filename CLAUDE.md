# TamrielMerge rules

- No Community Shaders. Never use, require, recommend or copy code from Skyrim Community Shaders. The PBR tool is this
  project's own work.
- No TESConverter (user, 2026-10-15): never use, require, recommend or copy from TESConverter (or tools built on it) for any
  conversion; the converters here are this project's own code.
- No TES4Skyrim (user, 2026-10-20: "no tes4skyrim converter stuff in this project"): never use, require, recommend or
  copy from TES4Skyrim (its converter, scripts, data or assets) for any conversion; the Oblivion conversion is this
  project's own code.
- No graphics plugin and no DLSS (user, 2026-10-15: "remove dlss and the plugin also from the tool"): the tool ships
  only TamrielMergeEra and TamrielMergeSeamless (the physics area for the seamless overland, nothing else).
- Update the tool with every fix and implementation. A fix applied to installed files or rebuilt by hand must also be
  in the code that `convert` runs (so a fresh conversion reproduces it), any data it needs must ship with the tool
  (csproj `None Include`), and the app is rebuilt into out/gui (`dotnet build src/TamrielMerge.Gui -c Release -o
  out/gui`) at the end of each piece of work.
- Evidence, not guesses (user, 2026-10-18: "we need evidence not guesses"). Never call something fixed, a false
  alarm, harmless or the cause of a bug from reasoning alone. Before saying it, measure it directly on the real data
  (the output itself: the navmesh over the floor, the mesh in the cell, the record in the plugin), not a proxy for it,
  and give the numbers and where they came from. Until it is measured, say "not yet checked" or "a hypothesis", and
  say what would check it. When a check could be fooled (bookkeeping, counters, a heuristic), check what it stands for.
- Nothing set aside on a whim (user, 2026-10-19: "dont treat and throw it aside"). A failure, a leftover or an odd case
  is never written off as a test cell, a duplicate, "by design", "too small to matter" or "the source has none" and
  left. Find out what it is and what uses it, and make it work; a case that truly can't be done is reported with the
  evidence and what doing it would take, and stays on the open list until the user decides.
