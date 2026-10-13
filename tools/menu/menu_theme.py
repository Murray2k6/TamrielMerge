"""The main menu's music: an original orchestral theme in place of Skyrim's (user, 2026-10-09: "write a musical score to
replace the skyrim menu music"), written here as a standard MIDI file for FluidSynth and the General MIDI orchestra in
GeneralUser GS (TamrielMerge.Cli `menu-music` renders it to music\\special\\mus_maintheme.xwm, the file the main menu
plays: the game setting sMainMenuMusic, not a music type).

  python menu_theme.py OUT.mid

D minor, 66 beats a minute, about three and a half minutes, quiet at both ends so it loops:
  A  intro      strings and basses alone, the choir, harp; the horn foretells the theme; timpani swell
  B  theme      the horn sings the theme over strings and harp
  B' theme      the strings take it up, the horns answer, an ostinato begins
  C  rising     the second phrase (F major) in horns and cellos, brass gathering
  D  climax     the theme in trumpets and violins over the full orchestra; it turns to D major
  E  interlude  the flute alone over harp and soft strings
  F  coda       the horn's first phrase again, fading into the opening's D minor
Nothing here is taken from another work: melodies, harmony and orchestration are this file's own.
"""
import random
import struct
import sys

PPQ = 480
BAR = 4 * PPQ
NOTE = {"C": 0, "C#": 1, "Db": 1, "D": 2, "D#": 3, "Eb": 3, "E": 4, "F": 5, "F#": 6, "Gb": 6, "G": 7, "G#": 8, "Ab": 8,
        "A": 9, "A#": 10, "Bb": 10, "B": 11}
CHORDS = {"Dm": (2, 3), "D": (2, 4), "Bb": (10, 4), "F": (5, 4), "C": (0, 4), "Gm": (7, 3), "A": (9, 4), "Asus": (9, 5)}
rng = random.Random(7)


def n(name):
    """'A4' -> 69."""
    return 12 * (int(name[-1]) + 1) + NOTE[name[:-1]]


def pcs(chord):
    root, third = CHORDS[chord]
    return [root, (root + third) % 12, (root + 7) % 12]


def T(beats):
    return int(round(beats * PPQ))


class Part:
    def __init__(self, name, ch, program, pan, volume=100, reverb=100):
        self.name, self.ch, self.events = name, ch, []
        self.cc(0, 7, volume); self.cc(0, 10, pan); self.cc(0, 91, reverb); self.cc(0, 93, 20); self.cc(0, 11, 110)
        if ch != 9:
            self.events.append((0, 0, bytes([0xC0 | ch, program])))

    def cc(self, t, c, v):
        self.events.append((t, 0, bytes([0xB0 | self.ch, c, max(0, min(127, int(v)))])))

    def note(self, t, dur, pitch, vel, human=0):
        if human:
            t = max(0, t + rng.randint(-human, human)); vel += rng.randint(-5, 5)
        vel = max(1, min(127, int(vel)))
        self.events.append((t, 2, bytes([0x90 | self.ch, pitch, vel])))
        self.events.append((t + max(dur, 10), 1, bytes([0x80 | self.ch, pitch, 0])))

    def ramp(self, t0, t1, c, v0, v1, steps=24):
        for k in range(steps + 1):
            self.cc(t0 + (t1 - t0) * k // steps, c, v0 + (v1 - v0) * k / steps)


def voicing(chord, prev, lo, hi, count):
    """The chord's tones stacked from some start in [lo, hi], the stack nearest the previous voicing."""
    tones = pcs(chord)
    best = None
    for start in range(lo, lo + 12):
        if start % 12 not in tones:
            continue
        stack, p = [], start
        while len(stack) < count and p <= hi:
            if p % 12 in tones:
                stack.append(p)
            p += 1
        if len(stack) < count:
            continue
        cost = sum(abs(a - b) for a, b in zip(stack, prev)) if prev else abs(stack[0] - (lo + hi) // 2)
        if best is None or cost < best[0]:
            best = (cost, stack)
    return best[1]


def merged(chords):
    """Runs of the same chord: (first bar, bars, chord)."""
    out = []
    for i, c in enumerate(chords):
        if out and out[-1][2] == c:
            out[-1] = (out[-1][0], out[-1][1] + 1, c)
        else:
            out.append((i, 1, c))
    return out


def pad(part, chords, bar, vel, lo, hi, count=3):
    prev = None
    for i, bars, c in merged(chords):
        prev = voicing(c, prev, lo, hi, count)
        for p in prev:
            part.note((bar + i) * BAR, bars * BAR - 20, p, vel)


def bass(part, chords, bar, vel, octave=False):
    for i, bars, c in merged(chords):
        root = CHORDS[c][0]
        p = 33 + (root - 9) % 12          # A1 .. G#2
        part.note((bar + i) * BAR, bars * BAR - 20, p, vel)
        if octave:
            part.note((bar + i) * BAR, bars * BAR - 20, p + 12, vel - 10)


def harp(part, chords, bar, vel):
    for i, c in enumerate(chords):
        r, th, fi = pcs(c)
        base = 48 + (r - 0) % 12
        third = base + (th - r) % 12; fifth = base + (fi - r) % 12
        for k, p in enumerate([base, fifth, base + 12, third + 12, fifth + 12, third + 12, base + 12, fifth]):
            part.note((bar + i) * BAR + k * PPQ // 2, T(1.5), p, vel * (1.0 if k in (0, 4) else 0.8), human=6)


def ostinato(part, chords, bar, vel):
    accents = [1.0, 0.7, 0.8, 0.7, 0.95, 0.7, 0.8, 0.75]
    for i, c in enumerate(chords):
        r, _, fi = pcs(c)
        root = 50 + (r - 2) % 12             # D3 .. C#4
        fifth = root + (fi - r) % 12
        for k, p in enumerate([root, root, fifth, root, root, root, fifth, root + 12]):
            part.note((bar + i) * BAR + k * PPQ // 2, T(0.42), p, vel * accents[k], human=4)


def melody(part, notes, bar, vel, shift=0):
    """notes: (name or None for a rest, beats); a little legato, higher notes a little louder."""
    t = bar * BAR
    for k, (name, beats) in enumerate(notes):
        if name:
            p = n(name) + shift
            nxt = next((nm for nm, _ in notes[k + 1:k + 2] if nm), None)
            overlap = 25 if nxt and n(nxt) + shift != p else -15
            part.note(t, T(beats) + overlap, p, vel + (p - 72) * 0.6, human=5)
        t += T(beats)


def counter(part, chords, bar, vel, lo=n("C4"), hi=n("A4")):
    """Half notes on each chord's third then fifth, each the nearest to the last."""
    last = (lo + hi) // 2
    for i, c in enumerate(chords):
        _, th, fi = pcs(c)
        for h, pc in enumerate((th, fi)):
            cands = [p for p in range(lo, hi + 1) if p % 12 == pc] or [p for p in range(lo - 12, hi + 13) if p % 12 == pc]
            last = min(cands, key=lambda p: abs(p - last))
            part.note((bar + i) * BAR + h * 2 * PPQ, 2 * PPQ - 20, last, vel, human=6)


def roll(part, t0, t1, pitch, v0, v1):
    step = PPQ // 8
    for k, t in enumerate(range(t0, t1, step)):
        part.note(t, step, pitch, v0 + (v1 - v0) * (t - t0) / max(t1 - t0, 1))


TIMP = {2: 38, 9: 33, 5: 41, 10: 34, 0: 36, 7: 43}


def timp_hits(part, chords, bar, vel, beats=(0,)):
    for i, c in enumerate(chords):
        for b in beats:
            part.note((bar + i) * BAR + T(b), T(1), TIMP[CHORDS[c][0]], vel - 12 * (b > 0))


INTRO = ["Dm", "Dm", "Bb", "C", "Dm", "F", "Gm", "A"]
THEME_CHORDS = ["Dm", "Bb", "F", "C", "Dm", "Gm", "A", "Dm"]
THEME = [("A4", 1.5), ("D5", 0.5), ("E5", 1), ("F5", 1),
         ("G5", 1.5), ("F5", 0.5), ("D5", 2),
         ("C5", 1), ("F5", 1), ("A5", 1.5), ("G5", 0.5),
         ("E5", 3), ("C5", 1),
         ("D5", 1.5), ("E5", 0.5), ("F5", 1), ("A5", 1),
         ("Bb5", 2), ("A5", 1), ("G5", 1),
         ("E5", 1.5), ("F5", 0.5), ("E5", 1), ("C#5", 1),
         ("D5", 4)]
RISE_CHORDS = ["F", "C", "Dm", "Bb", "F", "C", "Bb", "A"]
RISE = [("C5", 1), ("F5", 1), ("G5", 1), ("A5", 1),
        ("G5", 2), ("E5", 1), ("C5", 1),
        ("D5", 1), ("F5", 1), ("A5", 1.5), ("C6", 0.5),
        ("Bb5", 3), ("A5", 0.5), ("G5", 0.5),
        ("A5", 1.5), ("G5", 0.5), ("F5", 1), ("C5", 1),
        ("E5", 1), ("G5", 1), ("C6", 2),
        ("D6", 1.5), ("C6", 0.5), ("Bb5", 1), ("G5", 1),
        ("A5", 4)]
TURN_CHORDS = ["Bb", "C", "D", "D"]
TURN = [("F5", 1), ("Bb5", 1), ("D6", 2),
        ("E6", 1.5), ("D6", 0.5), ("C6", 1), ("G5", 1),
        ("A5", 1), ("D6", 1), ("F#6", 2),
        ("D6", 4)]
CALM_CHORDS = ["Bb", "F", "Gm", "A", "Bb", "F", "Gm", "A"]
CALM = [("D5", 1), ("F5", 1), ("Bb5", 2),
        ("A5", 1.5), ("G5", 0.5), ("F5", 1), ("C5", 1),
        ("D5", 1), ("G5", 1), ("Bb5", 1), ("A5", 1),
        ("E5", 2), ("C#5", 2),
        ("F5", 1.5), ("G5", 0.5), ("F5", 1), ("D5", 1),
        ("C5", 1), ("F5", 1), ("A5", 2),
        ("G5", 1), ("F5", 1), ("D5", 1), ("Bb4", 1),
        ("A4", 4)]
CODA_CHORDS = ["Dm", "Bb", "Gm", "A", "Dm", "Dm"]
CODA = [("A4", 1.5), ("D5", 0.5), ("E5", 1), ("F5", 1),
        ("G5", 1.5), ("F5", 0.5), ("D5", 2),
        (None, 0.5), ("Bb4", 1.5), ("A4", 2),
        ("A4", 2), ("C#5", 2),
        ("D5", 4)]


def compose():
    strings = Part("Strings", 0, 49, 56, 100)
    basses = Part("Basses", 1, 43, 78, 100)
    horns = Part("Horns", 2, 60, 74, 112)
    trumpets = Part("Trumpets", 3, 56, 60, 98)
    choir = Part("Choir", 4, 52, 64, 88)
    harp_ = Part("Harp", 5, 46, 40, 96)
    flute = Part("Flute", 6, 73, 52, 100)
    timp = Part("Timpani", 7, 47, 64, 112)
    ost = Part("Ostinato", 8, 48, 46, 84)
    violins = Part("Violins", 10, 48, 50, 106)
    brass = Part("Brass", 11, 61, 78, 92)
    cellos = Part("Cellos", 12, 42, 84, 96)
    cymbal = Part("Cymbal", 9, 0, 64, 90)

    # A: intro (bars 0-7)
    pad(strings, INTRO, 0, 72, n("D3"), n("D5"), 4); strings.ramp(0, 8 * BAR, 11, 62, 100)
    bass(basses, INTRO, 0, 62); basses.ramp(0, 8 * BAR, 11, 72, 100)
    pad(choir, INTRO[4:], 4, 56, n("A3"), n("F5")); choir.ramp(4 * BAR, 8 * BAR, 11, 30, 85)
    harp(harp_, INTRO[2:], 2, 46)
    melody(horns, THEME[:7], 4, 66)
    roll(timp, 7 * BAR, 8 * BAR, 33, 18, 88)
    # B: the theme in the horn (8-15)
    timp.note(8 * BAR, T(2), 38, 82)
    pad(strings, THEME_CHORDS, 8, 74, n("D3"), n("D5"), 4)
    bass(basses, THEME_CHORDS, 8, 72)
    pad(choir, THEME_CHORDS, 8, 46, n("A3"), n("F5")); choir.ramp(8 * BAR, 9 * BAR, 11, 85, 70)
    harp(harp_, THEME_CHORDS, 8, 50)
    melody(horns, THEME, 8, 90)
    # B': the strings take the theme, the horns answer (16-23)
    pad(strings, THEME_CHORDS, 16, 70, n("D3"), n("A4"), 3)
    bass(basses, THEME_CHORDS, 16, 78, octave=True)
    melody(violins, THEME, 16, 92)
    melody(flute, THEME[13:], 20, 56, 12)
    counter(horns, THEME_CHORDS, 16, 72)
    ostinato(ost, THEME_CHORDS, 16, 62); ost.ramp(16 * BAR, 24 * BAR, 11, 70, 100)
    pad(choir, THEME_CHORDS, 16, 56, n("A3"), n("F5"))
    harp(harp_, THEME_CHORDS[:4], 16, 46)
    timp_hits(timp, ["Dm"], 16, 70); timp_hits(timp, ["Dm"], 20, 70)
    # C: the second phrase rises (24-31)
    melody(horns, RISE, 24, 94, -12)
    melody(cellos, RISE, 24, 82, -24)
    ostinato(ost, RISE_CHORDS, 24, 74); ost.ramp(24 * BAR, 32 * BAR, 11, 80, 118)
    pad(strings, RISE_CHORDS, 24, 76, n("F3"), n("D5"), 4); strings.ramp(24 * BAR, 32 * BAR, 11, 90, 115)
    bass(basses, RISE_CHORDS, 24, 84, octave=True)
    pad(choir, RISE_CHORDS, 24, 62, n("A3"), n("F5")); choir.ramp(24 * BAR, 32 * BAR, 11, 70, 105)
    for i, c in enumerate(RISE_CHORDS[4:]):
        v = voicing(c, None, n("F3"), n("D5"), 3)
        for h in range(2):
            for p in v:
                brass.note((28 + i) * BAR + h * 2 * PPQ, 2 * PPQ - 30, p, 60 + 8 * i + 4 * h)
    timp_hits(timp, ["F"], 24, 78); timp_hits(timp, ["F"], 28, 84)
    roll(timp, 31 * BAR, 32 * BAR, 33, 30, 112)
    trumpets.note(31 * BAR, BAR - 20, n("A4"), 90); trumpets.ramp(31 * BAR, 32 * BAR, 11, 40, 120)
    # D: the climax (32-39) and its turn to D major (40-43)
    cymbal.note(32 * BAR, T(4), 49, 78)
    trumpets.cc(32 * BAR, 11, 112)
    melody(trumpets, THEME, 32, 104)
    melody(violins, THEME, 32, 104, 12)
    counter(horns, THEME_CHORDS, 32, 92)
    for i, c in enumerate(THEME_CHORDS):
        v = voicing(c, None, n("F3"), n("D5"), 3)
        for h in range(2):
            for p in v:
                brass.note((32 + i) * BAR + h * 2 * PPQ, 2 * PPQ - 30, p, 92 - 6 * h)
    pad(choir, THEME_CHORDS, 32, 92, n("A3"), n("A5"), 4)
    pad(strings, THEME_CHORDS, 32, 90, n("D3"), n("D5"), 4)
    bass(basses, THEME_CHORDS, 32, 100, octave=True)
    counter(cellos, THEME_CHORDS, 32, 86, n("D3"), n("A3"))
    ostinato(ost, THEME_CHORDS, 32, 88)
    timp_hits(timp, THEME_CHORDS[:7], 32, 98, beats=(0, 2))
    roll(timp, 39 * BAR, 40 * BAR, 33, 50, 115)
    cymbal.note(40 * BAR, T(4), 49, 70)
    melody(violins, TURN, 40, 106)
    melody(trumpets, TURN, 40, 100, -12)
    pad(brass, TURN_CHORDS, 40, 96, n("F3"), n("D5"))
    pad(choir, TURN_CHORDS, 40, 96, n("A3"), n("A5"), 4)
    pad(strings, TURN_CHORDS, 40, 92, n("D3"), n("D5"), 4)
    bass(basses, TURN_CHORDS, 40, 104, octave=True)
    ostinato(ost, TURN_CHORDS[:2], 40, 90)
    timp_hits(timp, TURN_CHORDS[:2], 40, 100, beats=(0, 2))
    roll(timp, 42 * BAR, 43 * BAR, 38, 40, 118)
    timp.note(43 * BAR, T(3), 38, 120)
    cymbal.note(43 * BAR, T(4), 49, 84)
    for part in (strings, choir, brass, basses, trumpets, violins):
        part.ramp(43 * BAR, 44 * BAR, 11, 115, 0)
    # E: the flute's interlude (44-51)
    for part in (strings, choir, basses):
        part.cc(44 * BAR, 11, 80)
    melody(flute, CALM, 44, 78)
    harp(harp_, CALM_CHORDS, 44, 50)
    pad(strings, CALM_CHORDS, 44, 60, n("D3"), n("A4"), 3)
    bass(basses, CALM_CHORDS, 44, 52)
    pad(choir, CALM_CHORDS[4:], 48, 44, n("A3"), n("F5"))
    # F: coda (52-57), fading into the opening
    melody(horns, CODA, 52, 70)
    pad(strings, CODA_CHORDS, 52, 58, n("D3"), n("D5"), 4); strings.ramp(54 * BAR, 58 * BAR, 11, 80, 0)
    pad(choir, CODA_CHORDS, 52, 46, n("A3"), n("F5")); choir.ramp(54 * BAR, 58 * BAR, 11, 80, 0)
    bass(basses, CODA_CHORDS, 52, 48); basses.ramp(55 * BAR, 58 * BAR, 11, 80, 0)
    harp(harp_, CODA_CHORDS[:4], 52, 40)
    roll(timp, 55 * BAR, 56 * BAR, 33, 14, 44)
    timp.note(56 * BAR, T(2), 38, 44)
    parts = [strings, basses, horns, trumpets, choir, harp_, flute, timp, ost, violins, brass, cellos, cymbal]

    # The tempo: steady, broadening into the D major turn and the coda.
    tempo = [(0, 66)]
    for k in range(8):
        tempo.append((42 * BAR + k * BAR // 4, 66 - 1.2 * k))
    tempo.append((44 * BAR, 62))
    for k in range(12):
        tempo.append((55 * BAR + k * BAR // 4, 62 - 0.8 * k))
    return parts, tempo, 58 * BAR


def vlq(v):
    out = [v & 0x7F]
    v >>= 7
    while v:
        out.append(0x80 | (v & 0x7F)); v >>= 7
    return bytes(reversed(out))


def track(events, name):
    body = b"\x00\xff\x03" + vlq(len(name)) + name.encode()
    last = 0
    for t, _, ev in sorted(events, key=lambda e: (e[0], e[1])):
        body += vlq(t - last) + ev; last = t
    body += b"\x00\xff\x2f\x00"
    return b"MTrk" + struct.pack(">I", len(body)) + body


def write(path):
    parts, tempo, end = compose()
    meta = [(0, 0, b"\xff\x58\x04\x04\x02\x18\x08")]
    for t, bpm in tempo:
        us = int(60e6 / bpm)
        meta.append((t, 0, b"\xff\x51\x03" + us.to_bytes(3, "big")))
    tracks = [track(meta, "TamrielMerge main theme")]
    for p in parts:
        ev = [e for e in p.events if e[0] <= end + BAR]
        tracks.append(track(ev, p.name))
    with open(path, "wb") as f:
        f.write(b"MThd" + struct.pack(">IHHH", 6, 1, len(tracks), PPQ))
        for t in tracks:
            f.write(t)
    print(f"{path}: {len(parts)} parts, {end // BAR} bars")


if __name__ == "__main__":
    write(sys.argv[1])
