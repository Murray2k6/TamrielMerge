# Checking the game

User, 2026-10-14: "make a script that can scan for failures and errors on game launch". The app's **Check game**
button runs `TamrielMerge.Cli game-check --prepare --launch` (Skyrim/GameCheck.cs):

1. **Prepare** turns Papyrus logging, tracing and debug line numbers on in `SkyrimCustom.ini` (in Documents, not the
   game folder) and copies Crash Logger SSE (MIT; setup downloads it from its GitHub release into `tools\crashlogger`)
   into `Data\SKSE\Plugins`, so a crash writes `Documents\My Games\Skyrim Special Edition\SKSE\Crashlog-*.log`.
2. **Launch** starts the game through `skse64_loader.exe` and waits for it to end, noting how long it ran and the most
   memory it used.
3. **Scan** reads that session's logs and writes `logs\game-check-<time>.txt`:
   - how far the game got, from SKSE's messages to its plugins (data loaded means every plugin was read; without it the
     game stopped while reading them);
   - the crash log: the exception, the top of the call stack, and every record it names with its plugin, TamrielMerge's
     plugins first;
   - Windows' own record of a crash or hang (module, exception code, offset), via `wevtutil`;
   - Papyrus errors and warnings from TamrielMerge's scripts (TM*, OB_*, MW*, DF*, RG*), grouped and counted;
   - errors from TamrielMerge's SKSE plugins' logs.

`game-check` alone (no `--launch`) scans the last hour, or `--since "yyyy-MM-dd HH:mm"`.

Offline checks of the converted plugins, without the game:

- `plugin-check --plugins "A.esm;B.esp"`: every form link of each plugin must reach a record of a type the link can
  hold (a plugin built against an older master, or a wrong hard-coded form, links to the wrong kind), and every exterior
  navmesh must name its own cell's grid, stored Y then X as Skyrim's are. On 2026-10-14 it found the containers of
  Morrowind.esm and Oblivion.esm naming placed objects as their sounds, and every converted exterior navmesh naming its
  grid X then Y (which crashed the game leaving Privateer's Hold; Skyrim.esm passes the same check).
- `df-dungeon-check`: Daggerfall's dungeons against their blocks' geometry: floating items, statues facing walls,
  people off the floor or the navmesh, moving parts that can't move.

The app also keeps its own log in `logs\TamrielMerge-<date>.log` and any error with its stack in `logs\errors.log`.
