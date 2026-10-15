namespace TamrielMerge.Daggerfall;

/// <summary>
/// Daggerfall's sound effects (ARENA2\DAGGER.SND): a Daggerfall BSA with numbered records, each record the raw sound,
/// 8-bit unsigned mono PCM at 11025 Hz (Daggerfall Unity SndFile, MIT). Quests name a sound by its record id
/// (Daggerfall Unity PlaySound: SoundReader.GetSoundIndex(id)).
/// </summary>
public sealed class DfSound
{
    public const int SampleRate = 11025;
    readonly DfBsa bsa;

    DfSound(DfBsa bsa) => this.bsa = bsa;

    public static DfSound? Load(string arena2)
    {
        string path = Path.Combine(arena2, "DAGGER.SND");
        return File.Exists(path) ? new DfSound(new DfBsa(path)) : null;
    }

    public int Count => bsa.Entries.Count;

    /// <summary>The raw samples of the sound with this record id, or null.</summary>
    public byte[]? Samples(uint id)
    {
        for (int i = 0; i < bsa.Entries.Count; i++)
            if (bsa.Entries[i].Id == id) return bsa.Record(i).ToArray();
        return null;
    }

    /// <summary>Writes the sound as a 16-bit mono WAV at 44100 Hz (resampled linearly; Skyrim's own effects are 16-bit),
    /// returning false when Daggerfall has no sound with that id.</summary>
    public bool WriteWav(uint id, string path)
    {
        if (Samples(id) is not { Length: > 0 } raw) return false;
        const int rate = 44100, factor = rate / SampleRate;
        var pcm = new short[raw.Length * factor];
        for (int i = 0; i < raw.Length; i++)
        {
            int a = (raw[i] - 128) << 8, b = ((i + 1 < raw.Length ? raw[i + 1] : raw[i]) - 128) << 8;
            for (int k = 0; k < factor; k++) pcm[i * factor + k] = (short)(a + (b - a) * k / factor);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + pcm.Length * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(pcm.Length * 2);
        foreach (var s in pcm) w.Write(s);
        return true;
    }
}
