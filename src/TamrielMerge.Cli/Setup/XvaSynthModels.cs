using System.Text;
using System.Text.Json;

namespace TamrielMerge.Setup;

/// <summary>
/// xVASynth's Skyrim voice models, a requirement published only on Nexus Mods (Skyrim Special Edition mod 44184), installed
/// into tools\xvasynth\models\skyrim (Dependencies.XvaModelDir), where the voice generator's xVASynth step
/// (tools\voice\xva.py) reads them.
/// <para>
/// A model is a pair &lt;name&gt;.pt + &lt;name&gt;.json; the .json's games[] entry for "skyrim" names its voice (voiceId
/// "sk_femalenord", voiceName "Female Nord"), as xVASynth's own installer reads it (javascript/dragdrop_model_install.js),
/// and every file named for it (a HiFi-GAN .hg.pt, a preview .wav) goes with it. The row counts the voice types the
/// converter speaks that have one by xva.py's rule (sk_&lt;voice type&gt;.pt or &lt;voice type&gt;.pt, with its .json):
/// the voices of the converted dialogue (tools\voice\*-jobs.json, each line's SpeakAs or VoiceType without its effect, as
/// xva.py groups them; recorded lines don't count), the voices cloned for it (tools\voice\refs), and the one voice per
/// race and sex every converted person speaks with (VanillaActors.RaceVoices).
/// </para>
/// </summary>
public static class XvaSynthModels
{
    public const string RowName = "Skyrim voice models (xVASynth)";
    public static string ModelsDir => Dependencies.XvaModelDir;
    /// <summary>The Nexus files already imported, so a pack that holds none of the missing voices isn't fetched again
    /// and again; kept with the models, so deleting the folder starts over.</summary>
    static string ImportedList => Path.Combine(ModelsDir, "nexus-files.json");

    public static NexusSource Source { get; } = new("skyrimspecialedition", 44184, "xVASynth's Skyrim voice models")
    {
        Category = "MAIN",
        Wanted = Wanted,
        Import = Import,
        Ready = () => Coverage().Missing.Count == 0,
    };

    /// <summary>The requirement row: how many of the spoken voice types have a model.</summary>
    public static Dependencies.Status Status()
    {
        var (spoken, missing) = Coverage();
        // The count first, before "; ": the app shows that much on the row and the rest as its tooltip.
        string detail = $"{spoken.Count - missing.Count} of {spoken.Count} voice types have a model";
        if (missing.Count > 0)
            detail += $"; missing {string.Join(", ", missing.Take(6))}{(missing.Count > 6 ? $" and {missing.Count - 6} more" : "")}";
        detail += $"; in {ModelsDir}";
        return new(RowName, missing.Count == 0, detail, Source);
    }

    /// <summary>The voice types the converter speaks, and those of them without a model.</summary>
    public static (List<string> Spoken, List<string> Missing) Coverage()
    {
        var spoken = SpokenVoiceTypes();
        var have = ModelVoices();
        return (spoken, spoken.Where(v => !have.Contains(v)).ToList());
    }

    public static List<string> SpokenVoiceTypes()
    {
        var set = new SortedSet<string>(Skyrim.VanillaActors.RaceVoices.Values, StringComparer.OrdinalIgnoreCase);
        string voice = Path.Combine(Dependencies.Root, "voice");
        if (Directory.Exists(voice))
        {
            foreach (var jobs in Directory.GetFiles(voice, "*-jobs.json")) set.UnionWith(SpeechOf(jobs));
            string refs = Path.Combine(voice, "refs");
            if (Directory.Exists(refs)) set.UnionWith(Directory.GetDirectories(refs).Select(d => Path.GetFileName(d)));
        }
        return set.ToList();
    }

    static readonly Dictionary<string, (DateTime Written, long Length, HashSet<string> Voices)> speechCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The voices a job list speaks in, remembered until the file changes: Morrowind's is 56 MB, and the app
    /// checks its tools every time it refreshes.</summary>
    static HashSet<string> SpeechOf(string jobs)
    {
        var info = new FileInfo(jobs);
        lock (speechCache)
            if (speechCache.TryGetValue(jobs, out var hit) && hit.Written == info.LastWriteTimeUtc && hit.Length == info.Length) return hit.Voices;
        var voices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        byte[] bytes;
        // Being rewritten by a voice run: counted on the next check.
        try { bytes = File.ReadAllBytes(jobs); } catch (IOException) { return voices; }
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true });
            string? voiceType = null, speakAs = null, speech = null;
            while (reader.Read())
            {
                // Jobs are flat objects in one array (VoicePlanner.WriteJobs): depth 1 is a job, depth 2 its fields.
                if (reader.TokenType == JsonTokenType.StartObject && reader.CurrentDepth == 1) { voiceType = speakAs = speech = null; continue; }
                if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 1)
                {
                    // The voice the line is said in: the stand-in's when it has one, else its own voice type's (as
                    // xva.py takes it); Speech, written beside them, when a list has only that.
                    string? said = speakAs is { Length: > 0 } ? speakAs : voiceType ?? speech;
                    if (said is { Length: > 0 } && !said.StartsWith(Skyrim.VoicePlanner.RecordingPrefix, StringComparison.Ordinal))
                        voices.Add(said.Split('+')[0]);
                    continue;
                }
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 2) continue;
                ref string? slot = ref voiceType;
                if (reader.ValueTextEquals("VoiceType"u8)) slot = ref voiceType;
                else if (reader.ValueTextEquals("SpeakAs"u8)) slot = ref speakAs;
                else if (reader.ValueTextEquals("Speech"u8)) slot = ref speech;
                else { reader.Skip(); continue; }
                reader.Read();
                slot = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            }
        }
        catch (JsonException) { return voices; }   // cut short by a run writing it: counted on the next check
        lock (speechCache) speechCache[jobs] = (info.LastWriteTimeUtc, info.Length, voices);
        return voices;
    }

    /// <summary>The voice types the installed models are for, by xva.py's rule (model_for): a .pt with its .json, named
    /// sk_&lt;voice type&gt; or &lt;voice type&gt;. A model named otherwise isn't one the engine finds, so it doesn't count.</summary>
    static HashSet<string> ModelVoices()
    {
        var voices = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(ModelsDir)) return voices;
        foreach (var pt in Directory.GetFiles(ModelsDir, "*.pt"))
        {
            string stem = Path.GetFileNameWithoutExtension(pt);
            // A HiFi-GAN vocoder beside a model (sk_x.hg.pt) is part of that model, not one of its own.
            if (stem.EndsWith(".hg", StringComparison.OrdinalIgnoreCase) || !File.Exists(Path.ChangeExtension(pt, ".json"))) continue;
            voices.Add(stem.StartsWith("sk_", StringComparison.OrdinalIgnoreCase) ? stem[3..] : stem);
        }
        return voices;
    }

    /// <summary>A model .json's Skyrim voices (voiceId, voiceName); none when it isn't a model for Skyrim or can't be read.</summary>
    static List<(string Id, string Name)> SkyrimVoices(string json)
    {
        var voices = new List<(string, string)>();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("games", out var games) || games.ValueKind != JsonValueKind.Array) return voices;
            foreach (var g in games.EnumerateArray())
            {
                string Text(string key) => g.ValueKind == JsonValueKind.Object && g.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                if (Text("gameId").Equals("skyrim", StringComparison.OrdinalIgnoreCase)) voices.Add((Text("voiceId"), Text("voiceName")));
            }
        }
        catch (JsonException) { }
        catch (IOException) { }
        return voices;
    }

    /// <summary>A voice's name for comparing: letters and digits, lower case, without xVASynth's "sk_" game prefix, and a
    /// creature's voice without Skyrim's "Cr" and "Voice" (CrDremoraVoice, sk_crdremoravoice and "Dremora" are one).</summary>
    static string Key(string name)
    {
        string s = name.Trim();
        if (s.StartsWith("sk_", StringComparison.OrdinalIgnoreCase)) s = s[3..];
        var b = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) b.Append(char.ToLowerInvariant(c));
        s = b.ToString();
        if (s.Length > 7 && s.StartsWith("cr", StringComparison.Ordinal) && s.EndsWith("voice", StringComparison.Ordinal)) s = s[2..^5];
        return s;
    }

    /// <summary>Skyrim's spoken voice types, to tell a file named for one from a file named for a longer one ("Male Nord
    /// Commander" isn't MaleNord's).</summary>
    static readonly string[] SkyrimVoiceTypes =
    [
        "MaleArgonian", "MaleBandit", "MaleBrute", "MaleChild", "MaleCommander", "MaleCommoner", "MaleCommonerAccented",
        "MaleCondescending", "MaleCoward", "MaleDarkElf", "MaleDrunk", "MaleElfHaughty", "MaleEvenToned", "MaleEvenTonedAccented",
        "MaleGuard", "MaleKhajiit", "MaleNord", "MaleNordCommander", "MaleOldGrumpy", "MaleOldKindly", "MaleOrc", "MaleSlyCynical",
        "MaleSoldier", "MaleWarlock", "MaleYoungEager", "FemaleArgonian", "FemaleChild", "FemaleCommander", "FemaleCondescending",
        "FemaleCoward", "FemaleDarkElf", "FemaleElfHaughty", "FemaleEvenToned", "FemaleKhajiit", "FemaleNord", "FemaleOldGrumpy",
        "FemaleOldKindly", "FemaleOrc", "FemaleShrill", "FemaleSultry", "FemaleYoungEager", "CrDremoraVoice",
    ];

    /// <summary>The voice types a Nexus file is named for: each whose name is in the file's name, not as part of a longer
    /// one (Female Even Toned holds "maleeventoned").</summary>
    static HashSet<string> VoicesNamed(Nexus.ModFile file, IEnumerable<string> candidates)
    {
        string text = Key(file.Name);
        var keys = candidates.Select(Key).Where(k => k.Length > 0).Distinct().ToList();
        var found = keys.Where(k => text.Contains(k, StringComparison.Ordinal)).ToList();
        return found.Where(k => !found.Any(o => o != k && o.Contains(k, StringComparison.Ordinal))).ToHashSet();
    }

    /// <summary>
    /// Of the mod's MAIN files, those still wanted: the files named for a voice still missing, not yet imported; when no
    /// file is named for one, the packs (files named for no single voice) not yet imported.
    /// </summary>
    static IReadOnlyList<Nexus.ModFile> Wanted(IReadOnlyList<Nexus.ModFile> files)
    {
        var (spoken, missing) = Coverage();
        var missingKeys = missing.Select(Key).ToHashSet();
        var imported = Imported();
        var candidates = SkyrimVoiceTypes.Concat(spoken).ToList();
        var fresh = files.Where(f => !imported.Contains(f.FileId)).ToList();
        var named = fresh.Select(f => (File: f, Voices: VoicesNamed(f, candidates))).ToList();
        var forMissing = named.Where(n => n.Voices.Overlaps(missingKeys)).Select(n => n.File).ToList();
        if (forMissing.Count > 0) return forMissing;
        return named.Where(n => n.Voices.Count == 0).Select(n => n.File).ToList();
    }

    static HashSet<int> Imported()
    {
        try { return File.Exists(ImportedList) ? JsonSerializer.Deserialize<HashSet<int>>(File.ReadAllText(ImportedList)) ?? [] : []; }
        catch (JsonException) { return []; }
        catch (IOException) { return []; }
    }

    static void RememberImported(int fileId)
    {
        var ids = Imported();
        if (!ids.Add(fileId)) return;
        Directory.CreateDirectory(ModelsDir);
        File.WriteAllText(ImportedList, JsonSerializer.Serialize(ids.Order().ToList()));
    }

    /// <summary>
    /// Installs the Skyrim voice models in one archive: unpacked in tools\downloads, each .json naming the game skyrim
    /// (or naming no game, with its .pt beside it) is moved into the models folder with every file named for it. The
    /// archive itself is left where it is. Returns the voices installed.
    /// </summary>
    static List<string> Import(string archive, Nexus.ModFile? file)
    {
        string temp = Path.Combine(Dependencies.Root, "downloads", "nexus-unpacked", Guid.NewGuid().ToString("N")[..12]);
        try
        {
            Nexus.Unpack(archive, temp);
            // Remembered once it unpacks, whatever it holds, so a file without what is missing isn't fetched again.
            if (file is not null) RememberImported(file.FileId);
            Directory.CreateDirectory(ModelsDir);
            var installed = new List<string>();
            var skipped = new List<string>();
            foreach (var json in Directory.EnumerateFiles(temp, "*.json", SearchOption.AllDirectories).ToList())
            {
                string stem = Path.GetFileNameWithoutExtension(json), dir = Path.GetDirectoryName(json)!;
                if (!File.Exists(Path.Combine(dir, stem + ".pt"))) continue;   // not a model: a readme's or a plugin's .json
                var voices = SkyrimVoices(json);
                bool forSkyrim = voices.Count > 0 || !HasGames(json);
                if (!forSkyrim) { skipped.Add(stem + " (not a Skyrim model)"); continue; }
                foreach (var part in Directory.GetFiles(dir).Where(f => Path.GetFileName(f).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase)))
                    File.Move(part, Path.Combine(ModelsDir, Path.GetFileName(part)), overwrite: true);
                installed.Add(voices.FirstOrDefault() is { Name.Length: > 0 } v ? $"{v.Name} ({stem})" : stem);
            }
            if (installed.Count == 0)
                throw new InvalidDataException($"{Path.GetFileName(archive)} holds no xVASynth Skyrim voice model (a .pt with its .json){(skipped.Count > 0 ? ": " + string.Join(", ", skipped) : "")}");
            if (skipped.Count > 0) installed.Add($"left out {string.Join(", ", skipped)}");
            return installed;
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    static bool HasGames(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(json));
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("games", out var g) && g.ValueKind == JsonValueKind.Array && g.GetArrayLength() > 0;
        }
        catch (JsonException) { return false; }
    }
}
