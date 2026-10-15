# TamrielMerge rules

- No Community Shaders. Never use, require, recommend or copy code from Skyrim Community Shaders. The PBR tool is this
  project's own work.
- No graphics plugin and no DLSS (user, 2026-10-15: "remove dlss and the plugin also from the tool"): the tool ships
  only TamrielMergeEra and TamrielMergeSeamless (the physics area for the seamless overland, nothing else).
- Update the tool with every fix and implementation. A fix applied to installed files or rebuilt by hand must also be
  in the code that `convert` runs (so a fresh conversion reproduces it), any data it needs must ship with the tool
  (csproj `None Include`), and the app is rebuilt into out/gui (`dotnet build src/TamrielMerge.Gui -c Release -o
  out/gui`) at the end of each piece of work.
