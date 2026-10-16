using System.Collections;
using System.Text;
using HKX2;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;

namespace TamrielMerge.Skyrim.Creatures;

/// <summary>
/// The behaviour of a creature with its own skeleton (<see cref="CreatureRig"/>): a vanilla creature's behaviour project
/// (from the user's own game archives) taken as the template, since its graph already answers every event Skyrim's AI
/// sends (moveStart, attackStart..., staggerStart, HitFrame) and sets the variables it reads, turned to the new creature:
/// <list type="bullet">
/// <item>project, character and behaviour files of its own (TM&lt;Id&gt;Project.hkx, Characters\, Behaviors\) using its
/// skeleton; every animation file the template's character lists written from one of the creature's clips (by kind:
/// attack, idle, move, hit);</item>
/// <item>the graph's bone ties remapped: bone weight arrays sized to the new skeleton, bone index lists cleared, look-at
/// bones moved to its head (or switched off), other bone indices kept within it, the mirror map made the identity;</item>
/// <item>clip trigger times (HitFrame, foot steps, attack ends) scaled from the template's clip lengths to the creature's;</item>
/// <item>the engine's animation data: meshes\animationdata\&lt;project&gt;.txt (clips and triggers), boundanims\anims_
/// &lt;project&gt;.txt (motion), meshes\animationsetdata\&lt;project&gt;Data\ (attack sets, each animation file's CRC32s:
/// plain CRC32 without inversion of the lower-case folder and of the name, then "hkx" packed), and both dirlist.txt
/// indexes with the project added (loose copies of the game's own, extended).</item>
/// </list>
/// </summary>
public sealed class CreatureBehavior
{
    /// <summary>A template: the vanilla creature's folder (e.g. meshes\actors\mudcrab), project file name and race (whose
    /// attack data names the graph's events). Single-file graphs only.</summary>
    public sealed record Template(string Folder, string ProjectFile, string Race);

    /// <summary>The templates by body: crawlers (mudcrab), big bipeds (troll), swimmers (slaughterfish), floating or
    /// flying (ice wraith, chaurus hunter).</summary>
    public static readonly Dictionary<string, Template> Templates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["mudcrab"] = new(@"meshes\actors\mudcrab", "mudcrabproject.hkx", "MudcrabRace"),
        ["troll"] = new(@"meshes\actors\troll", "trollproject.hkx", "TrollRace"),
        ["slaughterfish"] = new(@"meshes\actors\slaughterfish", "slaughterfishproject.hkx", "SlaughterfishRace"),
        ["icewraith"] = new(@"meshes\actors\icewraith", "icewraithproject.hkx", "IceWraithRace"),
        ["chaurushunter"] = new(@"meshes\actors\dlc01\chaurusflyer", "chaurusflyer.hkx", "DLC1ChaurusHunterRace"),
        // Graphs that play a death clip on "DeathAnimation" (user, 2026-10-10: "fix the deaths"): crawlers (scrib), big
        // bipeds (frost atronach), casters (hagraven), small humanoids (riekling), floating (wisp, storm atronach).
        ["scrib"] = new(@"meshes\actors\dlc02\scrib", "scribproject.hkx", "DLC2AshHopperRace"),
        ["atronachfrost"] = new(@"meshes\actors\atronachfrost", "atronachfrostproject.hkx", "AtronachFrostRace"),
        ["hagraven"] = new(@"meshes\actors\hagraven", "hagravenproject.hkx", "HagravenRace"),
        ["riekling"] = new(@"meshes\actors\dlc02\riekling", "rieklingproject.hkx", "DLC2RieklingRace"),
        ["wisp"] = new(@"meshes\actors\wisp", "wispproject.hkx", "WispRace"),
        ["atronachstorm"] = new(@"meshes\actors\atronachstorm", "atronachstormproject.hkx", "AtronachStormRace"),
    };

    public static Template Mudcrab => Templates["mudcrab"];

    readonly Dictionary<string, IArchiveFile> archive;

    public CreatureBehavior(string skyrimData)
    {
        archive = new(StringComparer.OrdinalIgnoreCase);
        foreach (var bsa in Directory.GetFiles(skyrimData, "Skyrim - Animations.bsa").Concat(Directory.GetFiles(skyrimData, "Skyrim - Misc.bsa")))
            foreach (var f in Archive.CreateReader(GameRelease.SkyrimSE, bsa).Files) archive.TryAdd(f.Path, f);
    }

    byte[] Read(string path) => archive.TryGetValue(path, out var f) ? f.GetBytes() : throw new FileNotFoundException(path);
    string Text(string path) => Encoding.ASCII.GetString(Read(path));
    static hkRootLevelContainer Hkx(byte[] b) => (hkRootLevelContainer)new PackFileDeserializer().Deserialize(new BinaryReaderEx(b));
    static T Variant<T>(hkRootLevelContainer r) where T : class => r.m_namedVariants.Select(v => v?.m_variant).OfType<T>().First();

    /// <summary>The creature's clip for one of the template's animation files: attacks, moves, hits, the rest idle.</summary>
    public static string ClipFor(string templateAnimation, CreatureRig rig)
    {
        string n = Path.GetFileNameWithoutExtension(templateAnimation).ToLowerInvariant();
        string Pick(params string[] wanted) => wanted.FirstOrDefault(rig.Clips.ContainsKey) ?? (rig.Clips.ContainsKey("idle") ? "idle" : rig.Clips.Keys.First());
        if (n.Contains("attack") || n.Contains("bite") || n.Contains("bash") || n.Contains("lunge")) return Pick("attack");
        if (n.Contains("death") || n.Contains("die")) return Pick("death", "idle");
        if (n.Contains("stagger") || n.Contains("recoil") || n.Contains("hit") || n.Contains("block")) return Pick("hit", "block", "idle");
        if (n.Contains("run")) return Pick("run", "walk");
        if (n.Contains("walk") || n.Contains("left") || n.Contains("right") || n.Contains("turn") || n.Contains("forward") || n.Contains("backward") || n.Contains("sprint")) return Pick("walk");
        return Pick("idle");
    }

    static string Pascal(string id) => string.Concat(id.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>The project's name (its animation data files and race behaviour path use it).</summary>
    public static string ProjectName(string id) => $"TM{Pascal(id)}Project";

    /// <summary>Writes the creature's behaviour files under <paramref name="dataDir"/> from <paramref name="template"/>;
    /// <paramref name="headBone"/> is the bone the look-at modifier turns (-1: none). Returns the files written.</summary>
    public List<string> Write(string dataDir, string id, CreatureRig rig, Template template, int headBone)
    {
        var written = new List<string>();
        string project = ProjectName(id), character = $"TM{Pascal(id)}Character", behavior = $"TM{Pascal(id)}Behavior";
        string folder = Path.Combine(dataDir, CreatureRig.MeshFolder(id));
        int bones = rig.Bones.Count;

        // Template files.
        var tProjectRoot = Hkx(Read(Path.Combine(template.Folder, template.ProjectFile)));
        var tProject = Variant<hkbProjectData>(tProjectRoot);
        string tCharacterFile = tProject.m_stringData!.m_characterFilenames[0];
        var characterRoot = Hkx(Read(Path.Combine(template.Folder, tCharacterFile)));
        var characterData = Variant<hkbCharacterData>(characterRoot);
        var cs = characterData.m_stringData!;
        string tBehaviorFile = cs.m_behaviorFilename;
        var behaviorRoot = Hkx(Read(Path.Combine(template.Folder, tBehaviorFile)));
        string tProjectName = Path.GetFileNameWithoutExtension(template.ProjectFile);

        // Animations: every file the character lists, from one of the creature's clips; scale factors by file.
        var scale = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (string anim in cs.m_animationNames)
        {
            string clip = ClipFor(anim, rig);
            // The template clip's own annotations (its events when the project has no animation data), retimed to ours.
            var (theirDuration, notes) = TemplateClip(Path.Combine(template.Folder, anim));
            float ours = rig.Duration(clip);
            float theirs = theirDuration ?? ours;
            float k = theirs > 0 ? ours / theirs : 1;
            rig.WriteClip(Path.Combine(folder, anim), clip, notes.Select(n => (n.Time * k, n.Text)).ToList());
            scale[anim] = theirs > 0 ? ours / theirs : 1;
            written.Add(Path.Combine(folder, anim));
        }

        // Deaths (user, 2026-10-10: "fix the deaths"): the templates' death clips play whatever file came to hand (the frost
        // atronach its stagger, the hagraven its idle, looping) before the ragdoll takes over. Ours has no ragdoll, so every
        // clip named for dying, and every clip under a death or ragdoll state (not getting up), plays the creature's death
        // once and holds its last frame: it stays where it fell.
        const string DeathFile = @"Animations\TMDeath.HKX";
        if (rig.Clips.ContainsKey("death"))
        {
            rig.WriteClip(Path.Combine(folder, DeathFile), "death");
            written.Add(Path.Combine(folder, DeathFile));
            if (!cs.m_animationNames.Contains(DeathFile)) cs.m_animationNames = [.. cs.m_animationNames, DeathFile];
            scale[DeathFile] = 1;
            var deathClips = new HashSet<hkbClipGenerator>(ReferenceEqualityComparer.Instance);
            Visit(behaviorRoot, o =>
            {
                if (o is hkbStateMachineStateInfo st && IsDeathState(st.m_name) && st.m_generator is { } g)
                    Visit(g, x => { if (x is hkbClipGenerator c) deathClips.Add(c); });
                if (o is hkbClipGenerator named && IsDeathName(named.m_name)) deathClips.Add(named);
            });
            foreach (var c in deathClips)
            {
                c.m_animationName = DeathFile;
                c.m_mode = 0;   // MODE_SINGLE_PLAY: holds the last frame
                c.m_playbackSpeed = 1;
                c.m_cropStartAmountLocalTime = 0;
                c.m_cropEndAmountLocalTime = 0;
            }
            DeathClipsRewired = deathClips.Count;
        }

        // Project and character.
        tProject.m_stringData.m_characterFilenames = [$@"Characters\{character}.hkx"];
        cs.m_name = character;
        cs.m_rigName = @"Character Assets\skeleton.HKX";
        cs.m_ragdollName = @"Character Assets\skeleton.HKX";
        cs.m_behaviorFilename = $@"Behaviors\{behavior}.hkx";
        if (characterData.m_mirroredSkeletonInfo is { } mirror) mirror.m_bonePairMap = Enumerable.Range(0, bones).Select(i => (short)i).ToList();
        if (characterData.m_numBonesPerLod.Count > 0) characterData.m_numBonesPerLod = characterData.m_numBonesPerLod.Select(_ => bones).ToList();

        // Behaviour graph: bone ties and clip trigger times.
        var clipAnimation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        Visit(behaviorRoot, o =>
        {
            switch (o)
            {
                case hkbBoneWeightArray w:
                    float v = w.m_boneWeights.Count > 0 && w.m_boneWeights.All(x => x == w.m_boneWeights[0]) ? w.m_boneWeights[0] : 1;
                    w.m_boneWeights = Enumerable.Repeat(v, bones).ToList();
                    break;
                case hkbBoneIndexArray bi:
                    bi.m_boneIndices = [];
                    break;
                case BSLookAtModifierBoneData lb:
                    if (headBone >= 0) lb.m_index = (short)headBone; else { lb.m_index = 0; lb.m_enabled = false; }
                    break;
                case hkbClipGenerator clip:
                    clipAnimation[clip.m_name] = clip.m_animationName;
                    float f = scale.GetValueOrDefault(clip.m_animationName, 1);
                    clip.m_cropStartAmountLocalTime *= f;
                    clip.m_cropEndAmountLocalTime *= f;
                    if (clip.m_triggers is { } tr)
                        foreach (var t in tr.m_triggers) t.m_localTime *= f;
                    break;
            }
            // Any other bone index field: kept within the new skeleton.
            foreach (var p in o.GetType().GetProperties().Where(p => p.Name.Contains("BoneIndex") && p.PropertyType == typeof(short) && p.CanWrite))
                if ((short)p.GetValue(o)! >= bones) p.SetValue(o, (short)0);
        });

        string projectPath = Path.Combine(folder, project + ".hkx");
        Save(projectPath, tProjectRoot); written.Add(projectPath);
        string characterPath = Path.Combine(folder, "Characters", character + ".hkx");
        Save(characterPath, characterRoot); written.Add(characterPath);
        string behaviorPath = Path.Combine(folder, "Behaviors", behavior + ".hkx");
        Save(behaviorPath, behaviorRoot); written.Add(behaviorPath);

        // Animation data.
        written.AddRange(AnimationData(dataDir, id, project, tProjectName, characterFile: $@"Characters\{character}.hkx", behaviorFile: $@"Behaviors\{behavior}.hkx",
            clipAnimation, scale));
        return written.Select(f => Path.GetRelativePath(dataDir, f)).ToList();
    }

    /// <summary>A template animation's length and every annotation on any of its tracks.</summary>
    (float? Duration, List<(float Time, string Text)> Notes) TemplateClip(string path)
    {
        if (!archive.ContainsKey(path)) return (null, []);
        var c = Variant<hkaAnimationContainer>(Hkx(Read(path)));
        if (c.m_animations.Count == 0) return (null, []);
        var a = c.m_animations[0];
        var notes = a.m_annotationTracks.SelectMany(t => t.m_annotations).Select(n => (n.m_time, n.m_text)).Distinct().OrderBy(n => n.m_time).ToList();
        return (a.m_duration, notes);
    }

    static void Save(string path, hkRootLevelContainer root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var fs = File.Create(path);
        new PackFileSerializer().Serialize(root, new BinaryWriterEx(fs), HKXHeader.SkyrimSE());
    }

    /// <summary>Calls <paramref name="visit"/> on every Havok object reachable from <paramref name="root"/> once.</summary>
    static void Visit(object root, Action<object> visit)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        void Walk(object? o)
        {
            if (o is null || o is string || o.GetType().IsPrimitive || o.GetType().IsEnum || o.GetType().IsValueType) return;
            if (o is IEnumerable list) { foreach (var item in list) Walk(item); return; }
            if (!seen.Add(o)) return;
            visit(o);
            foreach (var p in o.GetType().GetProperties().Where(p => p.Name.StartsWith("m_") && p.GetIndexParameters().Length == 0))
            {
                object? v;
                try { v = p.GetValue(o); } catch { continue; }
                Walk(v);
            }
        }
        Walk(root);
    }

    /// <summary>Plain CRC32 (reflected, polynomial 0x04C11DB7) with no inversion before or after: Skyrim's animation set
    /// file hashes (checked against the mudcrab's).</summary>
    public static uint Crc(string s)
    {
        uint c = 0;
        foreach (byte b in Encoding.ASCII.GetBytes(s.ToLowerInvariant()))
        {
            c ^= b;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0xEDB88320u : c >> 1;
        }
        return c;
    }

    List<string> AnimationData(string dataDir, string id, string project, string tProject, string characterFile, string behaviorFile,
        Dictionary<string, string> clipAnimation, Dictionary<string, float> scale)
    {
        var written = new List<string>();
        string meshes = Path.Combine(dataDir, "meshes");
        // The project's animation data from an earlier build (another template) goes first.
        foreach (var stale in StaleData(dataDir, project)) if (File.Exists(stale)) File.Delete(stale);
        if (Directory.Exists(Path.Combine(meshes, "animationsetdata", project + "Data"))) Directory.Delete(Path.Combine(meshes, "animationsetdata", project + "Data"), true);
        float Factor(string clipName) => clipAnimation.TryGetValue(StripMirror(clipName), out var a) ? scale.GetValueOrDefault(a, 1) : 1;
        static string StripMirror(string n) { int i = n.IndexOf('['); return i > 0 ? n[..i] : n; }

        // animationdata\<project>.txt: header (project files), then a block per clip: name, id, speed, crop start, crop
        // end, trigger count, "event:time" lines, blank.
        // A template whose data isn't in files of its own is taken from the game's single files (Dawnguard's chaurus
        // hunter and Dragonborn's ash hopper are only there: 2026-10-16, built without it the imp, giant scorpion,
        // giant bat and dragonling had no animation data, so the game drew none of them).
        if (TemplateData(tProject) is not { } templateData)
        {
            RemoveFromIndex(Path.Combine(meshes, "animationdata", "dirlist.txt"), project + ".txt");
            RemoveFromIndex(Path.Combine(meshes, "animationsetdata", "dirlist.txt"), $@"{project}Data\{project}.txt");
            return written;
        }
        var lines = templateData.Data;
        var outLines = new List<string>();
        int p = 0;
        outLines.Add(lines[p++]);                                  // 1
        int files = int.Parse(lines[p]); outLines.Add(lines[p++]);
        outLines.Add(behaviorFile); outLines.Add(characterFile); outLines.Add(@"Character Assets\skeleton.HKX");
        p += files;
        var clipIds = new Dictionary<int, float>();
        outLines.Add(lines[p++]);                                  // has clip data
        while (p < lines.Count)
        {
            if (lines[p].Length == 0) { outLines.Add(""); p++; continue; }
            string name = lines[p];
            if (p + 5 >= lines.Count || !int.TryParse(lines[p + 1], out int clipId)) { outLines.Add(lines[p++]); continue; }
            float f = Factor(name);
            clipIds[clipId] = f;
            outLines.Add(name); outLines.Add(lines[p + 1]); outLines.Add(lines[p + 2]);
            outLines.Add(Num(float.Parse(lines[p + 3], System.Globalization.CultureInfo.InvariantCulture) * f));
            outLines.Add(Num(float.Parse(lines[p + 4], System.Globalization.CultureInfo.InvariantCulture) * f));
            int triggers = int.Parse(lines[p + 5]); outLines.Add(lines[p + 5]);
            for (int t = 0; t < triggers; t++)
            {
                string tl = lines[p + 6 + t];
                int colon = tl.LastIndexOf(':');
                outLines.Add(colon > 0 && float.TryParse(tl[(colon + 1)..], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float time)
                    ? tl[..(colon + 1)] + Num(time * f) : tl);
            }
            p += 6 + triggers;
        }
        written.Add(WriteText(Path.Combine(meshes, "animationdata", project + ".txt"), outLines));

        // boundanims\anims_<project>.txt: per clip id: id, duration, translation count, "t x y z" lines, rotation count,
        // "t x y z w" lines, blank. Times scale with the clip.
        var motion = templateData.Motion;
        var outMotion = new List<string>();
        for (int i = 0; i < motion.Count;)
        {
            if (motion[i].Length == 0) { outMotion.Add(""); i++; continue; }
            int clipId = int.Parse(motion[i]);
            float f = clipIds.GetValueOrDefault(clipId, 1);
            outMotion.Add(motion[i]);
            outMotion.Add(Num(float.Parse(motion[i + 1], System.Globalization.CultureInfo.InvariantCulture) * f));
            int n = int.Parse(motion[i + 2]); outMotion.Add(motion[i + 2]);
            for (int k = 0; k < n; k++) outMotion.Add(ScaleFirst(motion[i + 3 + k], f));
            int r = int.Parse(motion[i + 3 + n]); outMotion.Add(motion[i + 3 + n]);
            for (int k = 0; k < r; k++) outMotion.Add(ScaleFirst(motion[i + 4 + n + k], f));
            i += 4 + n + r;
        }
        written.Add(WriteText(Path.Combine(meshes, "animationdata", "boundanims", $"anims_{project}.txt"), outMotion));

        // animationsetdata\<project>Data\: the template's set files, the folder CRC switched to the creature's.
        uint theirFolder = 0, ourFolder = Crc($@"{CreatureRig.MeshFolder(id)}\animations");
        foreach (var (fileName0, setLines0) in TemplateSets(tProject))
        {
            var setLines = setLines0.ToList();
            string fileName = fileName0;
            if (fileName.Equals(tProject + ".txt", StringComparison.OrdinalIgnoreCase)) fileName = project + ".txt";
            else
            {
                // The animation-file triplets: folder CRC, name CRC, 7891816 ("hkx").
                for (int i = 2; i < setLines.Count; i++)
                    if (setLines[i] == "7891816" && uint.TryParse(setLines[i - 2], out uint folderCrc))
                    {
                        if (theirFolder == 0) theirFolder = folderCrc;
                        if (folderCrc == theirFolder) setLines[i - 2] = ourFolder.ToString();
                    }
            }
            written.Add(WriteText(Path.Combine(meshes, "animationsetdata", project + "Data", fileName), setLines));
        }

        // The indexes.
        written.Add(AddToIndex(Path.Combine(meshes, "animationdata", "dirlist.txt"), @"meshes\animationdata\dirlist.txt", project + ".txt"));
        written.Add(AddToIndex(Path.Combine(meshes, "animationsetdata", "dirlist.txt"), @"meshes\animationsetdata\dirlist.txt", $@"{project}Data\{project}.txt"));
        return written;
    }

    /// <summary>
    /// A template project's animation data (animationdata\&lt;project&gt;.txt's lines) and motion (boundanims'): its own
    /// files when the archives have them, else its part of meshes\animationdatasinglefile.txt (the project count, the
    /// project names, then per project a counted block of data lines and, when its clip flag (the line after its file
    /// list) is 1, a counted block of motion lines). Null when it is in neither.
    /// </summary>
    (List<string> Data, List<string> Motion)? TemplateData(string tProject)
    {
        if (archive.ContainsKey($@"meshes\animationdata\{tProject}.txt"))
        {
            var data = Text($@"meshes\animationdata\{tProject}.txt").Replace("\r", "").Split('\n').ToList();
            string motionPath = $@"meshes\animationdata\boundanims\anims_{tProject}.txt";
            var motion = archive.ContainsKey(motionPath) ? Text(motionPath).Replace("\r", "").Split('\n').ToList() : [];
            return (data, motion);
        }
        singleData ??= ParseSingleData();
        return singleData.TryGetValue(tProject, out var single) ? single : null;
    }

    Dictionary<string, (List<string> Data, List<string> Motion)>? singleData;

    /// <summary>
    /// Checks the single-file readers against the projects the archives also have as files of their own: (projects
    /// compared, those whose data, motion and set files read the same, the first differences).
    /// </summary>
    public (int Compared, int Same, List<string> Differences) CheckSingleFiles()
    {
        singleData ??= ParseSingleData();
        singleSets ??= ParseSingleSets();
        int compared = 0, same = 0;
        var diffs = new List<string>();
        static List<string> Trim(List<string> x) { var y = x.ToList(); while (y.Count > 0 && y[^1].Length == 0) y.RemoveAt(y.Count - 1); return y; }
        foreach (var (name, (data, motion)) in singleData)
        {
            string dataPath = $@"meshes\animationdata\{name}.txt";
            if (!archive.ContainsKey(dataPath)) continue;
            compared++;
            var ownData = Trim(Text(dataPath).Replace("\r", "").Split('\n').ToList());
            string motionPath = $@"meshes\animationdata\boundanims\anims_{name}.txt";
            var ownMotion = archive.ContainsKey(motionPath) ? Trim(Text(motionPath).Replace("\r", "").Split('\n').ToList()) : [];
            bool ok = ownData.SequenceEqual(Trim(data)) && ownMotion.SequenceEqual(Trim(motion));
            string setDir = $@"meshes\animationsetdata\{name}data\";
            if (singleSets.TryGetValue(name, out var sets))
                foreach (var (setName, setLines) in sets)
                    if (archive.TryGetValue(setDir + setName, out var f) && Trim(Encoding.ASCII.GetString(f.GetBytes()).Replace("\r", "").Split('\n').ToList()) is var ownSet && !ownSet.SequenceEqual(Trim(setLines)))
                    {
                        ok = false;
                        var mine = Trim(setLines);
                        int at = 0; while (at < ownSet.Count && at < mine.Count && ownSet[at] == mine[at]) at++;
                        if (diffs.Count < 8) diffs.Add($"{name}: set {setName}: {ownSet.Count} lines own, {mine.Count} single; first difference at {at}: '{(at < ownSet.Count ? ownSet[at] : "(end)")}' / '{(at < mine.Count ? mine[at] : "(end)")}'");
                    }
            if (ok) same++;
            else if (diffs.Count < 8 && !(ownData.SequenceEqual(Trim(data)) && ownMotion.SequenceEqual(Trim(motion))))
            {
                var d = Trim(data);
                int at = 0; while (at < ownData.Count && at < d.Count && ownData[at] == d[at]) at++;
                diffs.Add($"{name}: data {ownData.Count} own / {d.Count} single lines, first difference at {at}: '{(at < ownData.Count ? ownData[at] : "(end)")}' / '{(at < d.Count ? d[at] : "(end)")}'; motion same {ownMotion.SequenceEqual(Trim(motion))}");
            }
        }
        return (compared, same, diffs);
    }

    Dictionary<string, (List<string> Data, List<string> Motion)> ParseSingleData()
    {
        var result = new Dictionary<string, (List<string>, List<string>)>(StringComparer.OrdinalIgnoreCase);
        const string single = @"meshes\animationdatasinglefile.txt";
        if (!archive.ContainsKey(single)) return result;
        var l = Text(single).Replace("\r", "").Split('\n');
        int n = int.Parse(l[0]), p = n + 1;
        for (int k = 0; k < n && p < l.Length; k++)
        {
            string name = Path.GetFileNameWithoutExtension(l[1 + k]);
            int count = int.Parse(l[p]);
            var data = l[(p + 1)..(p + 1 + count)].ToList();
            p += 1 + count;
            var motion = new List<string>();
            int files = data.Count > 1 && int.TryParse(data[1], out int f) ? f : 0;
            if (data.Count > 2 + files && data[2 + files] == "1")
            {
                int motionCount = int.Parse(l[p]);
                motion = l[(p + 1)..(p + 1 + motionCount)].ToList();
                p += 1 + motionCount;
            }
            result[name] = (data, motion);
        }
        return result;
    }

    /// <summary>
    /// A template project's animation set files (name, lines): its own animationsetdata\&lt;project&gt;data\ files, else
    /// its part of meshes\animationsetdatasinglefile.txt (the project count and names, then per project the count and
    /// names of its set files, then each set file: "V3", counted triggers, counted conditions of three lines, counted
    /// attacks (name, a flag, counted clips) and counted animation-file triplets), with the list file
    /// (&lt;project&gt;.txt: the set files' names) first.
    /// </summary>
    IEnumerable<(string Name, List<string> Lines)> TemplateSets(string tProject)
    {
        string tSetDir = $@"meshes\animationsetdata\{tProject}data\";
        var own = archive.Where(kv => kv.Key.StartsWith(tSetDir, StringComparison.OrdinalIgnoreCase)).ToList();
        if (own.Count > 0)
        {
            foreach (var (path, file) in own)
                yield return (Path.GetFileName(path), Encoding.ASCII.GetString(file.GetBytes()).Replace("\r", "").Split('\n').ToList());
            yield break;
        }
        singleSets ??= ParseSingleSets();
        if (!singleSets.TryGetValue(tProject, out var sets)) yield break;
        yield return (tProject + ".txt", sets.Select(s => s.Name).ToList());
        foreach (var s in sets) yield return s;
    }

    Dictionary<string, List<(string Name, List<string> Lines)>>? singleSets;

    Dictionary<string, List<(string Name, List<string> Lines)>> ParseSingleSets()
    {
        var result = new Dictionary<string, List<(string, List<string>)>>(StringComparer.OrdinalIgnoreCase);
        const string single = @"meshes\animationsetdatasinglefile.txt";
        if (!archive.ContainsKey(single)) return result;
        var l = Text(single).Replace("\r", "").Split('\n');
        int n = int.Parse(l[0]), p = n + 1;
        for (int k = 0; k < n && p < l.Length; k++)
        {
            string name = Path.GetFileNameWithoutExtension(l[1 + k].Replace('/', '\\').Split('\\')[^1]);
            int fileCount = int.Parse(l[p++]);
            var names = l[p..(p + fileCount)].ToList();
            p += fileCount;
            var sets = new List<(string, List<string>)>();
            foreach (var setName in names)
            {
                int start = p;
                p++;                                                        // "V3"
                int triggers = int.Parse(l[p++]); p += triggers;
                int conditions = int.Parse(l[p++]); p += conditions * 3;
                int attacks = int.Parse(l[p++]);
                for (int a = 0; a < attacks; a++) { p += 2; int clips = int.Parse(l[p++]); p += clips; }
                int infos = int.Parse(l[p++]); p += infos * 3;
                sets.Add((setName, l[start..p].ToList()));
            }
            result[name] = sets;
        }
        return result;
    }

    /// <summary>How many clip generators the last <see cref="Write"/> turned to the creature's death.</summary>
    public int DeathClipsRewired { get; private set; }

    static bool IsDeathState(string name)
    {
        string n = name.ToLowerInvariant();
        return (n.Contains("death") || n.Contains("dead") || n.Contains("dying") || n.Contains("ragdoll"))
            && !n.Contains("getup") && !n.Contains("get up") && !n.Contains("reanimat");
    }

    static bool IsDeathName(string name)
    {
        string n = name.ToLowerInvariant();
        return n.Contains("death") || n.Contains("dying") || n.Contains("dead");
    }

    /// <summary>The project's animation data files (any build, any template).</summary>
    public static IEnumerable<string> StaleData(string dataDir, string project)
    {
        yield return Path.Combine(dataDir, "meshes", "animationdata", project + ".txt");
        yield return Path.Combine(dataDir, "meshes", "animationdata", "boundanims", $"anims_{project}.txt");
    }

    static void RemoveFromIndex(string path, string entry)
    {
        if (!File.Exists(path)) return;
        var lines = File.ReadAllText(path).Replace("\r", "").Split('\n').Where(l => l.Length > 0 && !l.Equals(entry, StringComparison.OrdinalIgnoreCase)).ToList();
        WriteText(path, lines);
    }

    string AddToIndex(string path, string vanilla, string entry)
    {
        var lines = (File.Exists(path) ? File.ReadAllText(path) : Text(vanilla)).Replace("\r", "").Split('\n').Where(l => l.Length > 0).ToList();
        if (!lines.Contains(entry, StringComparer.OrdinalIgnoreCase)) lines.Add(entry);
        return WriteText(path, lines);
    }

    static string WriteText(string path, IEnumerable<string> lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join("\r\n", lines) + "\r\n", Encoding.ASCII);
        return path;
    }

    static string Num(float v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    static string ScaleFirst(string line, float f)
    {
        var parts = line.Split(' ');
        if (parts.Length > 0 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float t))
            parts[0] = Num(t * f);
        return string.Join(' ', parts);
    }
}
