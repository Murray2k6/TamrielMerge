# Voices

How the converted games speak in Skyrim (`TamrielMerge.Cli voices`, Skyrim/VoiceGenerator.cs, VoicePlanner.cs,
VoiceFiles.cs, VoiceArchives.cs, tools/voice/generate.py).

## What speaks how

- **The games' own recordings** stay the games' own: Redguard's and Oblivion's voice acting (`rg-voices`, `ob-voices`)
  and Morrowind's barks (below) are packed from the original audio and installed with each game's conversion.
- **Lines no game recorded** (Morrowind's dialogue) are generated: XTTS-v2 clones each Skyrim voice type from its vanilla
  recordings (refs/&lt;voice type&gt;), one .wav per distinct (voice, text), then packed as .fuz with lip sync
  (LipGenerator). A person speaks with the voice type of the vanilla NPC whose look they wear, so race and sex match
  (checked 2026-10-10 with `voice-coverage`: 971 named speakers, no race or sex mismatches).
- **Stand-ins** (user, 2026-10-10: "they need a voice"; "dagoth is a ashen and vivec and almexia are gods from
  morrowind"; "yagrum needs a voice hes dwemer"; "dwarven spheres and atronachs are creatures"): a person whose voice
  type is a creature's or missing speaks in a human voice, its files in their own voice type's folder (which the game
  plays from). VoicePlanner.StandIn: Vivec, Almalexia (and Sotha Sil) as Dark Elves with a divine hall (+god: reverb and
  a faint voice a fifth below); Dagoth Ur, his kin and the Sixth House's ashen as Dark Elves pitched down and roughened
  (+ash); Yagrum Bagarn (a Dwemer on a dwarven sphere's legs) as an old scholar (MaleOldKindly); the talking dead as old
  voices. Skyrim's Dremora voice counts as speech (Daedra such as Krazzt speak with it). Creatures (dwarven automatons,
  atronachs, mudcrabs, beasts) stay silent. Looks and voices are read from Skyrim and its DLC (Dragonborn's ash spawn).

## Morrowind's barks

Morrowind's voice topics (DIAL type 1: Hello, Idle, Intruder, Thief, Alarm, Attack, Hit, Flee; about 6,000 recordings
under Sound\Vo) become Skyrim's topics of the same moment (Hello, Idle, Trespass, Steal, NormalToCombat, Attack, Hit,
Flee) in the MWVoices quest (Morrowind/MwBarks.cs): Morrowind's filters, picked at random, said only by Morrowind's
people (the MWPeople faction), above Skyrim's own generic barks. Each response's script notes name its recording
(`mwvoice:vo\d\m\...mp3`); the planner makes it a recorded job, the generator decodes the mp3 (ffmpeg) instead of
speaking it. Built after every other record, so the plugin's FormIDs stay as they were.

## Speed

One graphics-card worker speaks in batches of 16 (lines of one voice and the same length in tokens share each step of
the sound codes; each line keeps its own latents and decoding): 2.1 lines a second on an RTX 5060 against 0.24 one at
a time (user, 2026-10-09: "it shouldnt take days its too slow"). No CPU worker beside it (it starved the core feeding the
card). A worker holds 2.8 GB of RAM (its 7.4 GB of committed memory sits in the system-managed page file). Packing
encodes and lip-syncs each spoken line once and copies it to its other voice files (a quarter of Morrowind's 130,844
repeat another's line).

## The voices mod

The generated voices are a mod of their own (user, 2026-10-10: "the voices need to be a mod because incase a user wanted
to disable them to be vanilla"; "you can make multiple bsa"): packed into out\voices (never the conversion output), then
archived (BsaWriter: SSE version 105, uncompressed, Bethesda's name hash checked against Skyrim - Sounds.bsa) in archives
under 1.9 GB, two to an empty light plugin: `TamrielMerge Voices.esp` (+ `.bsa`, ` - Textures.bsa`), `TamrielMerge Voices
2.esp`... Disabling those plugins turns the generated voices off: the lines show as text, as in the original games.

## Installing

`convert --install` and `voices --install` copy the files into Skyrim's Data folder and switch the plugins on
(Setup/PluginList.cs): in Skyrim's own Plugins.txt and, when Mod Organizer 2 manages the game, in its selected profile's
plugins.txt and loadorder.txt (only while MO2 is closed). MO2 is found by its global instances, the folder chosen in the
app ("Mod Organizer 2", saved in TamrielMerge.settings.json, or `--mo2`), and a running MO2 (remembered).

## Progress

The run's progress also goes to tools\voice\progress.txt (its process, start and latest report), so the app shows a voice
run it didn't start, or one that outlived it: the bar, how much is done and left, the time so far, the time left at the
last quarter hour's pace and when it should be done.
