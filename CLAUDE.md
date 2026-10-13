# TamrielMerge rules

- No Community Shaders. Never use, require, recommend or copy code from Skyrim Community Shaders. The graphics
  plugin (tools/skse/TamrielMergeGraphics: DLSS, ray tracing, sharpening) and the PBR tool are this project's own work.
- Update the tool with every fix and implementation. A fix applied to installed files or rebuilt by hand must also be
  in the code that `convert` runs (so a fresh conversion reproduces it), any data it needs must ship with the tool
  (csproj `None Include`), and the app is rebuilt into out/gui (`dotnet build src/TamrielMerge.Gui -c Release -o
  out/gui`) at the end of each piece of work.
