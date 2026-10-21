using TamrielMerge.Daggerfall;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// What Daggerfall itself says about one of its creatures, added to the model it was given (user, 2026-10-21: "same goes
/// for daggerfalls imp", "all creatures"): its own sounds from the user's DAGGER.SND, when they play as Daggerfall plays
/// them (Daggerfall Unity EnemySounds: every 3 to 9 seconds while the player is near, its bark four times in five, else its
/// move sound; its attack sound on half its attacks), the moment its blow lands (its attack's hit frame among its sprite
/// frames: EnemyBasics PrimaryAttackAnimFrames), its pace from MONSTER.BSA, and its numbers for its records.
/// </summary>
public static class DfCreatureSource
{
    /// <summary>Seconds between attract sounds, on average (3 to 9).</summary>
    const float AttractEvery = 6;

    public static void Apply(CreatureRig rig, DfMonster m, string arena2, string id, List<string> problems)
    {
        string pascal = CreatureBehavior.ProjectName(id)[2..^"Project".Length];
        var snd = DfSound.Load(arena2);
        if (snd is null) problems.Add($"{id}: no DAGGER.SND in {arena2}");
        byte[]? Wav(int index)
        {
            if (index < 0 || snd is null) return null;
            var w = snd.WavAt(index);
            if (w is null) problems.Add($"{id}: DAGGER.SND has no sound {index}");
            return w;
        }
        byte[] silence = Silence();

        // The attract sound, once a loop of its idle and its moving with the chance that gives one every six seconds on
        // average: bark four parts in five, move one, silences making up the rest.
        var bark = Wav(m.BarkSound); var move = Wav(m.MoveSound);
        string attract = $"TMSnd{pascal}Attract";
        if (bark is not null || move is not null)
        {
            float loop = new[] { "idle", "walk", "run" }.Where(rig.Clips.ContainsKey).Select(rig.Duration).DefaultIfEmpty(2).Average();
            float chance = Math.Clamp(loop / AttractEvery, 0.05f, 1);
            int sounding = 5, total = Math.Max(sounding, (int)MathF.Round(sounding / chance));
            int n = 0;
            for (int i = 0; i < 4; i++) rig.Sounds[$"{attract}#{++n}"] = bark ?? move!;
            rig.Sounds[$"{attract}#{++n}"] = move ?? bark!;
            while (n < total) rig.Sounds[$"{attract}#{++n}"] = silence;
            foreach (var clip in new[] { "idle", "walk", "run" }.Where(rig.Clips.ContainsKey))
                AddEvent(rig, clip, 0, "SoundPlay." + attract);
        }

        // Its attack: the sound on half of them, the blow at its hit frame.
        string attackSound = $"TMSnd{pascal}Attack";
        if (Wav(m.AttackSound) is { } attack)
        {
            rig.Sounds[$"{attackSound}#1"] = attack;
            rig.Sounds[$"{attackSound}#2"] = silence;
        }
        foreach (var clip in rig.Clips.Keys.Where(c => c.StartsWith("attack")).ToList())
        {
            if (rig.Sounds.ContainsKey($"{attackSound}#1")) AddEvent(rig, clip, 0, "SoundPlay." + attackSound);
            float at = rig.Duration(clip) * m.HitFraction;
            AddEvent(rig, clip, MathF.Max(0, at - 0.1f), "preHitFrame");
            AddEvent(rig, clip, at, "HitFrame");
        }

        // One pace, walking or running.
        if (DfMonsters.Speed(arena2, m.Id) is { } speed)
            rig.MoveSpeeds["walk"] = rig.MoveSpeeds["run"] = DfMonsters.UnitsPerSecond(speed);
        else problems.Add($"{id}: no MONSTER.BSA speed for monster {m.Id}");
    }

    static void AddEvent(CreatureRig rig, string clip, float time, string text)
    {
        if (!rig.Events.TryGetValue(clip, out var list)) rig.Events[clip] = list = [];
        list.Add((time, text));
        list.Sort((a, b) => a.Time.CompareTo(b.Time));
    }

    /// <summary>A twentieth of a second of silence (16-bit mono, 44100 Hz).</summary>
    static byte[] Silence()
    {
        const int rate = 44100, samples = rate / 20;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("RIFF"u8); w.Write(36 + samples * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(samples * 2);
        for (int i = 0; i < samples; i++) w.Write((short)0);
        w.Flush();
        return ms.ToArray();
    }

    /// <summary>
    /// Its numbers for its records: health the middle of its range (Daggerfall rolls one in it), its blows' range, no
    /// magicka unless its game gives it, Daggerfall's melee reach (Daggerfall Unity: 2.25 m), its pace, and how it moves
    /// (flying, swimming, else walking; a spectre walks); what kind it is for Skyrim's spells.
    /// </summary>
    public static CreatureRecords.Stats Stats(DfMonster m, string arena2)
    {
        var (min, max) = m.Damage;
        float pace = DfMonsters.Speed(arena2, m.Id) is { } s ? DfMonsters.UnitsPerSecond(s) : DfMonsters.UnitsPerSecond(50);
        return new CreatureRecords.Stats((m.MinHealth + m.MaxHealth) / 2, 0, 100, min, max, 2.25f * 69.99f, 0, Kind(m.Id), pace, pace,
            Flies: m.Moves == DfMoves.Flying, Swims: m.Moves == DfMoves.Aquatic, Walks: m.Moves is not (DfMoves.Flying or DfMoves.Aquatic));
    }

    /// <summary>What kind of creature it is (Skyrim's spells tell Daedra, the undead and animals apart).</summary>
    public static CreatureRecords.CreatureKind Kind(int id) => id switch
    {
        25 or 26 or 27 or 29 or 31 or 35 or 36 or 37 or 38 => CreatureRecords.CreatureKind.Daedra,
        15 or 17 or 18 or 19 or 23 or 28 or 30 or 32 or 33 => CreatureRecords.CreatureKind.Undead,
        0 or 3 or 4 or 5 or 6 or 11 or 20 or 39 or 41 => CreatureRecords.CreatureKind.Animal,
        _ => CreatureRecords.CreatureKind.Humanoid,
    };
}
