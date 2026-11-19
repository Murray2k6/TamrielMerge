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

**xVASynth first** (user, 2026-11-02: "there has to be a way or third party free program to convert all these voices";
"use the source on github if its free for use"). xVASynth's xVAPitch models (Dan Ruta, GPL-3.0) are trained one per
Skyrim voice type and make a whole line in one pass, in batches of lines, where XTTS writes its sound codes one step at
a time (83% of XTTS's time). VoiceGenerator.SpeakWithXva runs tools/voice/xva.py before the XTTS workers: on the
graphics card in batches of 32 (halved whenever the card runs out of memory), or on the processor in batches of 4.
It reads the same job list and writes the same files (same hash names, 44.1 kHz 16-bit mono, +god and +ash treated as
generate.py treats them, claimed with the same .lock files), so packing doesn't change and either engine finishes what
the other began. Voice types with no model (sk_&lt;voice type&gt;.pt with its .json in tools\xvasynth\models\skyrim, from
Skyrim SE mod 44184) are left for XTTS, so every race and sex keeps its own voice. Pause stops xva.py (freeing its
memory) and starts it again on resume. Its log is generate.xva.log in the voice folder.

Setup's "xVASynth speech engine" step downloads xVASynth's `python/xvapitch` code (with its bundled eSpeak NG) and `arpabet` dictionaries, and h2p-parser (ionite34,
Apache-2.0) at pinned commits into tools\xvasynth (only that code and their licences: none of xVASynth's Electron app,
server or old pinned packages), installs the packages its text code reads into the converter's own Python environment
(PyTorch kept at the environment's version) and fetches NLTK's tokenizer and tagger data, which it would otherwise
download the first time it speaks. Not yet measured: its speed on the full job list.

**XTTS**:
One graphics-card worker speaks in batches of 16 (lines of one voice and the same length in tokens share each step of
the sound codes; each line keeps its own latents and decoding): 2.1 lines a second on an RTX 5060 against 0.24 one at
a time (user, 2026-10-09: "it shouldnt take days its too slow"). No CPU worker beside it (it starved the core feeding the
card). A worker holds 2.8 GB of RAM (its 7.4 GB of committed memory sits in the system-managed page file). Packing
encodes and lip-syncs each spoken line once and copies it to its other voice files (a quarter of Morrowind's 130,844
repeat another's line).

### The graphics workers failing (2026-11-01)

A run spoke 0.1 lines a second, 442 hours by its own log. `generate.py` had gone back to an older copy when the voice tool's folder was lost on 2026-10-27, its `--half` and `--savers` never committed, so every graphics worker stopped at once on "unrecognized arguments" and the CPU worker went on alone. Restored from the session's history. Every graphics worker failing now stops the run with the workers' last line in the log. The tool folder's copy step leaves `tools` out, so a change to `generate.py` must be copied to `C:\TamrielMerge Tool	oolsoice` by hand.

### Morrowind's size against the rate

The job list is 129,558 lines, 26,625 distinct sentences in 27 voices. The rate measured on 2026-10-24 (three graphics workers and one CPU worker, 1.98 lines a second) is 18.2 hours. An hour at that rate is 7,128 lines. Fewer voices for lines many people share: 17 voices 87,474 lines (12.3 h), about 9 voices 63,992 (9.0 h), male and female only 42,689 (6.0 h).

### Shared sound codes (2026-11-01)

`--share` (on in the app) writes a sentence's sound codes once for the male voices saying it and once for the female ones, then makes each voice's speech from them with that voice's own latents and speaker, up to `--render` voices of a sentence at once. Every voice stays (user: "all the races are different and all sound different"). A sentence one voice says is written in that voice; +god and +ash are the Dark Elf speech treated; a wav's name and the packing are unchanged. Writing is 15.0 s of a line's 18.0 s on the CPU, and the job is 36,892 writes for 129,558 lines (123,797 made). Voices of one sex share a line's length and rhythm. Checked on the CPU on 19 real lines (5.1 s a line); not yet run on a graphics card, where the batched step's memory is unmeasured.

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
