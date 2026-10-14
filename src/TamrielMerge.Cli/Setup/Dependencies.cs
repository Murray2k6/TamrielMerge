using System.Diagnostics;
using System.IO.Compression;

namespace TamrielMerge.Setup;

/// <summary>
/// The outside tools the converter uses, found or installed under the tools folder (TAMRIELMERGE_TOOLS, else .\tools):
/// <list type="bullet">
/// <item>ffmpeg (BtbN's Windows build) — decodes the games' videos (Smacker, Bink) and their audio;</item>
/// <item>RAD Video Tools (radgametools.com, a password-protected .7z holding an NSIS installer) — encodes Bink 1, the
/// format Skyrim plays; extracted with 7-Zip when installed, else the standalone 7zr.exe, and installed silently into
/// tools\rad\bin;</item>
/// <item>a Python 3.12 environment (made by uv, which brings its own Python) with PyTorch — the CUDA build when an
/// NVIDIA GPU is present, else the CPU build — spandrel, Pillow and coqui-tts (XTTS-v2), at the versions known to work
/// together, for the video upscaler (tools\video\remaster.py) and the voice generator (tools\voice\generate.py);</item>
/// <item>the Real-ESRGAN models (x4plus, and the compact general-x4v3 that is fast on a CPU);</item>
/// <item>OpenCV and psutil in that environment, and the Depth Anything V2 model (Hugging Face, Apache-2.0), for the
/// intros' ray-traced look and Daggerfall's clean-up (tools\video\rt_look.py, deflare.py);</item>
/// <item>Blender (5.x) for the main menu's ray-traced intro (EEVEE) (tools\scene3d\blend_scene.py): an installed one when
/// there is, else blender.org's portable build in tools\blender;</item>
/// <item>onnxruntime and DeepBump's colour-to-normals model (GPL-3.0, downloaded, run by our own code) for the model
/// viewer's PBR tool (tools\textures\pbr.py);</item>
/// <item>NVIDIA's DLSS runtime (nvngx_dlss.dll, NVIDIA RTX SDKs licence, which allows shipping it with an app) beside
/// the graphics plugin (tools\skse\TamrielMergeGraphics), from NVIDIA's DLSS repository.</item>
/// </list>
/// The Creation Kit's tools (xWMAEncode, LipGenerator, the Papyrus compiler) come only with the Creation Kit on Steam,
/// so they are checked for, not installed.
/// </summary>
public static class Dependencies
{
    public static string Root => Environment.GetEnvironmentVariable("TAMRIELMERGE_TOOLS") is { Length: > 0 } t ? Path.GetFullPath(t) : Path.GetFullPath("tools");

    public static string FfmpegDir => Path.Combine(Root, "ffmpeg", "ffmpeg-master-latest-win64-gpl", "bin");
    public static string Ffmpeg => Path.Combine(FfmpegDir, "ffmpeg.exe");
    public static string RadVideo => Path.Combine(Root, "rad", "bin", "radvideo64.exe");
    public static string UpscaleModel => Path.Combine(Root, "upscale", "RealESRGAN_x4plus.pth");
    public static string FastUpscaleModel => Path.Combine(Root, "upscale", "realesr-general-x4v3.pth");
    static string Uv => Path.Combine(Root, "uv", "uv.exe");
    static string Venv => Path.Combine(Root, "py", ".venv");
    /// <summary>The model maker's own environment (TripoSR), apart from the voice and video one so neither disturbs
    /// the other.</summary>
    static string ModelVenv => Path.Combine(Root, "model3d", ".venv");
    public static string? ModelPython => File.Exists(Path.Combine(ModelVenv, "Scripts", "python.exe")) ? Path.Combine(ModelVenv, "Scripts", "python.exe") : null;
    /// <summary>Where TripoSR's weights are kept (Hugging Face's cache, inside the tools folder).</summary>
    public static string ModelCache => Path.Combine(Root, "model3d", "hf");
    /// <summary>The depth model the intros' ray-traced look estimates each frame's depth with.</summary>
    public const string DepthModel = "depth-anything/Depth-Anything-V2-Base-hf";
    /// <summary>DeepBump's colour-to-normals model (GPL-3.0), for the PBR tool.</summary>
    public static string NormalsModel => Path.Combine(Root, "pbr", "deepbump256.onnx");
    const string NormalsModelUrl = "https://raw.githubusercontent.com/HugoTini/DeepBump/master/deepbump256.onnx";
    /// <summary>NVIDIA's DLSS runtime, beside the graphics plugin: the first copy up from the tool (a project's
    /// tools\skse), else in the tools folder where setup downloads it.</summary>
    public static string DlssRuntime
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Path.Combine(dir.FullName, "tools", "skse", "TamrielMergeGraphics", "nvngx_dlss.dll") is var p && File.Exists(p)) return p;
            return Path.Combine(Root, "skse", "TamrielMergeGraphics", "nvngx_dlss.dll");
        }
    }

    /// <summary>Crash Logger SSE (MIT): an SKSE plugin that writes what a crash happened in (game-check installs it).</summary>
    public static string CrashLogger => Path.Combine(Root, "crashlogger", "SKSE", "Plugins", "CrashLogger.dll");
    const string CrashLoggerUrl = "https://github.com/alandtse/CrashLoggerSSE/releases/download/v1.25.0/CrashLogger_1.25.0.7z";

    /// <summary>JPEXS FFDec (GPL-3.0), which builds the Tamriel menu's Flash movie (Skyrim/Ui/FlashMenus), in tools\ffdec.</summary>
    public static string Ffdec => Path.Combine(Root, "ffdec", "ffdec-cli.jar");
    const string FfdecUrl = "https://github.com/jindrapetrik/jpexs-decompiler/releases/download/version26.2.1/ffdec_26.2.1.zip";
    /// <summary>Eclipse Temurin's Java runtime (GPL-2.0 with Classpath Exception), unpacked into tools\jre when Java
    /// isn't installed: FFDec runs on it.</summary>
    const string JreUrl = "https://api.adoptium.net/v3/binary/latest/21/ga/windows/x64/jre/hotspot/normal/eclipse";
    static string JreDir => Path.Combine(Root, "jre");
    static bool? javaOnPath;
    /// <summary>Java for FFDec: the installed one ("java" on the PATH), else the runtime setup unpacked, else null.</summary>
    public static string? Java
    {
        get
        {
            javaOnPath ??= RunQuiet("java", "-version", 20_000) == 0;
            if (javaOnPath == true) return "java";
            return Directory.Exists(JreDir) ? Directory.EnumerateFiles(JreDir, "java.exe", SearchOption.AllDirectories).FirstOrDefault() : null;
        }
    }
    const string DlssRuntimeUrl = "https://raw.githubusercontent.com/NVIDIA/DLSS/main/lib/Windows_x86_64/rel/nvngx_dlss.dll";
    static bool DepthModelReady => Directory.Exists(Path.Combine(ModelCache, "hub", "models--depth-anything--Depth-Anything-V2-Base-hf", "snapshots"));

    const string BlenderVersion = "5.2.2";   // 5.1.2's EEVEE drew enclosed streets black in the flyby (2026-10-11)
    static string BlenderUrl => $"https://download.blender.org/release/Blender{BlenderVersion[..BlenderVersion.LastIndexOf('.')]}/blender-{BlenderVersion}-windows-x64.zip";
    /// <summary>
    /// Blender 5.x, the newest found by its version (the exe's own): the portable copy under tools\blender, blender.org's
    /// installs, Steam's. Not by folder name: with 5.1 installed and 5.2 from Steam, 5.1 was taken, and its EEVEE drew
    /// Whiterun's enclosed streets black in the menu's flyby where 5.2 doesn't (user, 2026-10-11: "the roads of the city").
    /// </summary>
    public static string? Blender
    {
        get
        {
            var found = new List<string>();
            if (Directory.Exists(Path.Combine(Root, "blender"))) found.AddRange(Directory.GetFiles(Path.Combine(Root, "blender"), "blender.exe", SearchOption.AllDirectories));
            string bf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Blender Foundation");
            if (Directory.Exists(bf))
                found.AddRange(Directory.GetDirectories(bf, "Blender 5*").Select(d => Path.Combine(d, "blender.exe")).Where(File.Exists));
            string steam = @"C:\Program Files (x86)\Steam\steamapps\common\Blender\blender.exe";
            if (File.Exists(steam)) found.Add(steam);
            return found.Where(f => BlenderVersionOf(f).Major >= 5).OrderBy(BlenderVersionOf).ThenBy(f => f).LastOrDefault();
        }
    }

    /// <summary>A blender.exe's version, from the exe itself (5.2 for 5.2.2).</summary>
    public static Version BlenderVersionOf(string exe) =>
        Version.TryParse(FileVersionInfo.GetVersionInfo(exe).ProductVersion?.Split(' ')[0], out var v) ? v : new Version(0, 0);

    /// <summary>Whether a Python is on the PATH (not the Microsoft Store's stand-in in WindowsApps).</summary>
    static bool PythonOnPath() =>
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Where(d => !d.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase))
            .Any(d => File.Exists(Path.Combine(d.Trim(), "python.exe")));

    /// <summary>The Python with the converter's packages: the setup's environment, else the voice tool's older one.</summary>
    public static string? Python
    {
        get
        {
            foreach (var venv in new[] { Venv, Path.Combine(Root, "voice", ".venv") })
            {
                string py = Path.Combine(venv, "Scripts", "python.exe");
                if (File.Exists(py)) return py;
            }
            return null;
        }
    }

    const string FfmpegUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
    const string RadUrl = "https://www.radgametools.com/down/Bink/RADTools.7z";
    const string SevenZrUrl = "https://www.7-zip.org/a/7zr.exe";
    const string UvUrl = "https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip";
    const string X4PlusUrl = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.1.0/RealESRGAN_x4plus.pth";
    const string GeneralX4v3Url = "https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesr-general-x4v3.pth";

    public sealed record Status(string Name, bool Ready, string Detail);

    /// <summary>What is present and what is missing.</summary>
    public static List<Status> Check(string skyrimData)
    {
        var list = new List<Status>
        {
            new("ffmpeg", File.Exists(Ffmpeg), Ffmpeg),
            new("RAD Video Tools (Bink encoder)", File.Exists(RadVideo), RadVideo),
            new("Real-ESRGAN models", File.Exists(UpscaleModel) && File.Exists(FastUpscaleModel), Path.GetDirectoryName(UpscaleModel)!),
            new("Python environment (PyTorch, spandrel, XTTS)", Python is { } py && PackagesWork(py), Python ?? Venv),
            new("3D model maker (TripoSR)", ModelPython is { } mp && ModelPackagesWork(mp), ModelPython ?? ModelVenv),
            new("Depth model (Depth Anything V2)", DepthModelReady, ModelCache),
            new("Blender (ray-traced menu intro)", Blender is not null, Blender ?? Path.Combine(Root, "blender")),
            new("Normals model (PBR tool)", File.Exists(NormalsModel), NormalsModel),
            new("DLSS runtime (graphics plugin)", File.Exists(DlssRuntime), DlssRuntime),
            new("FFDec (Tamriel menu)", File.Exists(Ffdec), Ffdec),
            new("Java (for FFDec)", Java is not null, Java ?? JreDir),
            new("Crash Logger (game check)", File.Exists(CrashLogger), CrashLogger),
        };
        var missing = CreationKitMissing(skyrimData);
        list.Add(new("Creation Kit", missing.Count == 0, missing.Count == 0 ? SkyrimRoot(skyrimData) : "missing " + string.Join(", ", missing.Select(m => m.File))));
        return list;
    }

    /// <summary>Steam's app id for "Skyrim Special Edition: Creation Kit".</summary>
    public const int CreationKitAppId = 1946180;

    static string SkyrimRoot(string skyrimData) => skyrimData.Length > 0 ? Path.GetDirectoryName(Path.GetFullPath(skyrimData.TrimEnd('\\', '/')))! : "";

    /// <summary>The Creation Kit's files the converter uses that aren't there, with what each is for.</summary>
    public static List<(string File, string Purpose)> CreationKitMissing(string skyrimData)
    {
        string root = SkyrimRoot(skyrimData);
        var needed = new (string File, string Purpose)[]
        {
            ("CreationKit.exe", "the Creation Kit itself"),
            (@"Tools\Audio\xWMAEncode.exe", "voice files (Skyrim's voice audio format)"),
            (@"Tools\LipGen\LipGenerator\LipGenerator.exe", "lip sync for every voiced line"),
            (@"Papyrus Compiler\PapyrusCompiler.exe", "compiling the story scripts (prebuilt copies are used without it)"),
        };
        return needed.Where(n => root.Length == 0 || !File.Exists(Path.Combine(root, n.File))).ToList();
    }

    public enum CreationKitChoice { Ready, Skip, Quit }

    /// <summary>
    /// Makes sure the Creation Kit is installed before converting. When it isn't, explains what it is needed for and
    /// asks: install it (opens Steam's install page for it, then waits to check again), check again, skip (convert
    /// without voices and lip sync), or quit. Without a console to ask (--no-prompt, redirected input) it warns and skips.
    /// </summary>
    public static CreationKitChoice EnsureCreationKit(string skyrimData, bool prompt)
    {
        while (true)
        {
            var missing = CreationKitMissing(skyrimData);
            if (missing.Count == 0) { Console.WriteLine("Creation Kit: found"); return CreationKitChoice.Ready; }
            Console.WriteLine();
            Console.WriteLine("The Skyrim Special Edition Creation Kit isn't installed (free on Steam, under Library > Tools).");
            Console.WriteLine("TamrielMerge uses it for:");
            foreach (var (file, purpose) in missing) Console.WriteLine($"  - {purpose}  ({file})");
            if (!prompt)
            {
                Console.WriteLine("Continuing without it: the games convert, but without voices and lip sync.");
                return CreationKitChoice.Skip;
            }
            Console.Write("Install it now? [I]nstall (opens Steam)  [C]heck again  [S]kip (no voices)  [Q]uit: ");
            string answer = (Console.ReadLine() ?? "q").Trim().ToLowerInvariant();
            switch (answer.FirstOrDefault())
            {
                case 'i':
                    // Its page in the Steam Library (shown whether or not it's installed; steam://install does nothing visible
                    // in some cases), where the Install button is.
                    try
                    {
                        Process.Start(new ProcessStartInfo($"steam://nav/games/details/{CreationKitAppId}") { UseShellExecute = true });
                        Console.WriteLine("Steam is showing \"Skyrim Special Edition: Creation Kit\" in your Library: click Install there.");
                    }
                    catch { Console.WriteLine($"Couldn't open Steam: install \"Skyrim Special Edition: Creation Kit\" from your Steam Library (Tools)."); }
                    Console.WriteLine("Once Steam has finished installing it, start the Creation Kit once (it unpacks its tools and scripts), close it,");
                    Console.Write("then press Enter here to continue... ");
                    Console.ReadLine();
                    break;
                case 'c': break;
                case 's': return CreationKitChoice.Skip;
                case 'q': return CreationKitChoice.Quit;
            }
        }
    }

    static bool PackagesWork(string python) => Verified(python, "packages", () =>
        RunQuiet(python, $"-c \"import torch, torchaudio, spandrel, PIL, TTS, cv2, psutil, transformers, onnxruntime{CudaCheck(python)}\"", 120_000) == 0);

    static bool ModelPackagesWork(string python) => Verified(python, "model-packages", () =>
        RunQuiet(python, $"-c \"import torch, omegaconf, einops, transformers, trimesh, mcubes, xatlas, moderngl, imageio, spandrel, fast_simplification, huggingface_hub, PIL, scipy{CudaCheck(python)}\"", 120_000) == 0);

    /// <summary>With an NVIDIA card, the import check also asks for PyTorch's CUDA build, unless the CPU build was chosen
    /// (setup --cpu; remembered in the environment) (2026-10-12: a package added later without the pin swapped the model
    /// maker's PyTorch for the CPU build, and every model was made on the CPU for days without anything saying so).</summary>
    static string CudaCheck(string python)
    {
        string flavour = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(python)!)!, ".tamrielmerge-torch");
        if (File.Exists(flavour) && File.ReadAllText(flavour).Trim() == "cpu") return "";
        return RunQuiet("nvidia-smi", "-L", 20_000) == 0 ? "; assert torch.cuda.is_available()" : "";
    }

    static void RememberFlavour(string venv, string flavour)
    {
        try { File.WriteAllText(Path.Combine(venv, ".tamrielmerge-torch"), flavour); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A Python environment's import check, remembered (user, 2026-10-10: "why was the detected games list empty in the
    /// tool" - importing PyTorch and XTTS took 20 to 40 s every time the app opened): once it passes, a stamp in the
    /// environment stands for it until the environment's packages change (site-packages written after the stamp).
    /// </summary>
    static bool Verified(string python, string what, Func<bool> check)
    {
        string venv = Path.GetDirectoryName(Path.GetDirectoryName(python)!)!;
        string site = Path.Combine(venv, "Lib", "site-packages");
        string stamp = Path.Combine(venv, $".tamrielmerge-{what}.ok");
        if (File.Exists(stamp) && Directory.Exists(site) && File.GetLastWriteTimeUtc(stamp) >= Directory.GetLastWriteTimeUtc(site)) return true;
        bool ok = check();
        if (ok) try { File.WriteAllText(stamp, DateTime.Now.ToString("O")); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        else try { File.Delete(stamp); } catch (IOException) { }
        return ok;
    }

    /// <summary>Installs everything missing; returns the problems left (empty when all is ready).</summary>
    public static List<string> Install(string skyrimData, bool forceCpu = false)
    {
        var problems = new List<string>();
        Directory.CreateDirectory(Root);
        void Step(string name, Func<bool> ready, Action install)
        {
            if (ready()) { Console.WriteLine($"  {name}: ready"); return; }
            Console.WriteLine($"  {name}: installing...");
            try { install(); }
            catch (Exception e) { problems.Add($"{name}: {e.Message}"); return; }
            if (!ready()) problems.Add($"{name}: installed but not usable");
            else Console.WriteLine($"  {name}: done");
        }

        Step("ffmpeg", () => File.Exists(Ffmpeg), () =>
        {
            string zip = Download(FfmpegUrl, Path.Combine(Root, "downloads", "ffmpeg.zip"));
            ZipFile.ExtractToDirectory(zip, Path.Combine(Root, "ffmpeg"), overwriteFiles: true);
        });

        Step("RAD Video Tools", () => File.Exists(RadVideo), () =>
        {
            string archive = Download(RadUrl, Path.Combine(Root, "downloads", "RADTools.7z"));
            string unpacked = Path.Combine(Root, "downloads", "RADTools");
            string sevenZip = SevenZip();
            if (RunQuiet(sevenZip, $"x -pRAD -y \"-o{unpacked}\" \"{archive}\"", 300_000) != 0) throw new InvalidOperationException("could not unpack RADTools.7z");
            string installer = Directory.GetFiles(unpacked, "*.exe", SearchOption.AllDirectories).First();
            string bin = Path.GetDirectoryName(RadVideo)!;
            Directory.CreateDirectory(bin);
            // The tools come straight out of the NSIS installer (running it would ask for administrator rights).
            if (RunQuiet(sevenZip, $"x -y \"-o{bin}\" \"{installer}\" radvideo64.exe binkplay.exe", 120_000) != 0)
                throw new InvalidOperationException("could not unpack the RAD Video Tools installer");
        });

        Step("Real-ESRGAN models", () => File.Exists(UpscaleModel) && File.Exists(FastUpscaleModel), () =>
        {
            Download(X4PlusUrl, UpscaleModel);
            Download(GeneralX4v3Url, FastUpscaleModel);
        });

        Step("Python environment", () => Python is { } py && PackagesWork(py), () =>
        {
            if (!File.Exists(Uv))
            {
                string zip = Download(UvUrl, Path.Combine(Root, "downloads", "uv.zip"));
                ZipFile.ExtractToDirectory(zip, Path.Combine(Root, "uv"), overwriteFiles: true);
            }
            if (!File.Exists(Path.Combine(Venv, "Scripts", "python.exe")) && Run(Uv, $"venv \"{Venv}\" --python 3.12") != 0)
                throw new InvalidOperationException("uv could not make the Python 3.12 environment");
            bool cuda = !forceCpu && RunQuiet("nvidia-smi", "-L", 20_000) == 0;
            string flavour = cuda ? "cu128" : "cpu";
            Console.WriteLine($"    PyTorch build: {(cuda ? "CUDA (NVIDIA GPU found)" : "CPU")}");
            // One resolve with PyTorch pinned, so nothing pulls a newer PyTorch these packages don't work with.
            string packages = $"torch==2.8.0+{flavour} torchvision==0.23.0+{flavour} torchaudio==2.8.0+{flavour} " +
                "coqui-tts==0.27.5 \"transformers>=4.57,<5\" spandrel pillow numpy opencv-python psutil onnxruntime";
            if (Run(Uv, $"pip install --python \"{Path.Combine(Venv, "Scripts", "python.exe")}\" {packages} " +
                    $"--extra-index-url https://download.pytorch.org/whl/{flavour} --index-strategy unsafe-best-match") != 0)
                throw new InvalidOperationException("package install failed");
            RememberFlavour(Venv, flavour);
        });

        Step("3D model maker", () => ModelPython is { } mp && ModelPackagesWork(mp), () =>
        {
            if (!File.Exists(Uv))
            {
                string zip = Download(UvUrl, Path.Combine(Root, "downloads", "uv.zip"));
                ZipFile.ExtractToDirectory(zip, Path.Combine(Root, "uv"), overwriteFiles: true);
            }
            if (ModelPython is null && Run(Uv, $"venv \"{ModelVenv}\" --python 3.12") != 0)
                throw new InvalidOperationException("uv could not make the model maker's Python 3.12 environment");
            bool cuda = !forceCpu && RunQuiet("nvidia-smi", "-L", 20_000) == 0;
            string flavour = cuda ? "cu128" : "cpu";
            Console.WriteLine($"    PyTorch build: {(cuda ? "CUDA (NVIDIA GPU found)" : "CPU")}");
            // TripoSR's needs (its code ships with the converter): PyMCubes stands in for torchmcubes, which needs a
            // CUDA compiler; moderngl bakes the textures.
            // torchvision pinned too: spandrel needs it, and an unpinned one brings the newest PyTorch (CPU) from PyPI.
            string packages = $"torch==2.11.0+{flavour} torchvision==0.26.0+{flavour} \"omegaconf==2.3.0\" einops \"transformers>=4.40,<5\" trimesh huggingface_hub " +
                "PyMCubes xatlas moderngl imageio spandrel fast_simplification pillow numpy scipy";
            if (Run(Uv, $"pip install --python \"{Path.Combine(ModelVenv, "Scripts", "python.exe")}\" {packages} " +
                    $"--extra-index-url https://download.pytorch.org/whl/{flavour} --index-strategy unsafe-best-match") != 0)
                throw new InvalidOperationException("package install failed");
            RememberFlavour(ModelVenv, flavour);
        });

        Step("Depth model", () => DepthModelReady, () =>
        {
            string py = Python ?? throw new InvalidOperationException("needs the Python environment");
            var psi = new ProcessStartInfo(py) { UseShellExecute = false };
            foreach (var a in new[] { "-c", $"from transformers import AutoImageProcessor, AutoModelForDepthEstimation as M; AutoImageProcessor.from_pretrained('{DepthModel}'); M.from_pretrained('{DepthModel}')" })
                psi.ArgumentList.Add(a);
            psi.Environment["HF_HOME"] = ModelCache;
            using var p = Process.Start(psi)!;
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("could not download the depth model");
        });

        Step("Normals model", () => File.Exists(NormalsModel), () => Download(NormalsModelUrl, NormalsModel));
        Step("DLSS runtime", () => File.Exists(DlssRuntime), () => Download(DlssRuntimeUrl, DlssRuntime));
        Step("FFDec", () => File.Exists(Ffdec), () =>
        {
            string zip = Download(FfdecUrl, Path.Combine(Root, "downloads", "ffdec.zip"));
            string dir = Path.GetDirectoryName(Ffdec)!;
            ZipFile.ExtractToDirectory(zip, dir, overwriteFiles: true);
            // Its files at the top of tools\ffdec, should the archive hold them in a folder.
            if (!File.Exists(Ffdec) && Directory.EnumerateFiles(dir, "ffdec-cli.jar", SearchOption.AllDirectories).FirstOrDefault() is { } jar)
            {
                string from = Path.GetDirectoryName(jar)!;
                foreach (var f in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).ToList())
                {
                    string to = Path.Combine(dir, Path.GetRelativePath(from, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                    File.Move(f, to, overwrite: true);
                }
            }
        });
        Step("Crash Logger", () => File.Exists(CrashLogger), () =>
        {
            string archive = Download(CrashLoggerUrl, Path.Combine(Root, "downloads", "CrashLogger.7z"));
            string dir = Path.Combine(Root, "crashlogger");
            if (RunQuiet(SevenZip(), $"x -y \"-o{dir}\" \"{archive}\"", 120_000) != 0) throw new InvalidOperationException("could not unpack CrashLogger.7z");
        });
        Step("Java", () => Java is not null, () =>
        {
            string zip = Download(JreUrl, Path.Combine(Root, "downloads", "jre.zip"));
            ZipFile.ExtractToDirectory(zip, JreDir, overwriteFiles: true);
        });

        // Python on the PATH (user, 2026-10-11: "python goes on path auto installed"): when no Python is on it (the
        // Microsoft Store's stand-in doesn't count), uv installs its Python 3.12 as the default and puts its folder on
        // the user's PATH.
        Step("Python on the PATH", PythonOnPath, () =>
        {
            if (!File.Exists(Uv))
            {
                string zip = Download(UvUrl, Path.Combine(Root, "downloads", "uv.zip"));
                ZipFile.ExtractToDirectory(zip, Path.Combine(Root, "uv"), overwriteFiles: true);
            }
            if (Run(Uv, "python install 3.12 --default") != 0) throw new InvalidOperationException("uv could not install Python 3.12");
            if (Run(Uv, "python update-shell") != 0) throw new InvalidOperationException("uv could not put Python on the PATH");
        });

        // Blender 5.2 or newer (5.1's EEVEE drew enclosed streets black in the menu's flyby): an older one gets the tool's
        // own portable copy beside it, which Blender then picks as the newest.
        Step("Blender", () => Blender is { } bl && BlenderVersionOf(bl) >= new Version(5, 2), () =>
        {
            string zip = Download(BlenderUrl, Path.Combine(Root, "downloads", $"blender-{BlenderVersion}.zip"));
            ZipFile.ExtractToDirectory(zip, Path.Combine(Root, "blender"), overwriteFiles: true);
        });

        if (CreationKitMissing(skyrimData) is { Count: > 0 } ck)
            problems.Add($"Creation Kit (Steam, free): missing {string.Join(", ", ck.Select(m => m.File))}");
        return problems;
    }

    const string SevenZipSetupUrl = "https://www.7-zip.org/a/7z2604-x64.exe";

    /// <summary>A full 7-Zip (it opens NSIS installers): the installed one, else a private copy in tools\7zip, unpacked
    /// from 7-Zip's own setup (a 7z archive) with the standalone 7zr.exe — nothing is installed system-wide.</summary>
    static string SevenZip()
    {
        if (Environment.GetEnvironmentVariable("TAMRIELMERGE_NO_7ZIP") is null)
            foreach (var installed in new[] { @"C:\Program Files\7-Zip\7z.exe", @"C:\Program Files (x86)\7-Zip\7z.exe" })
                if (File.Exists(installed)) return installed;
        string dir = Path.Combine(Root, "7zip"), exe = Path.Combine(dir, "7z.exe");
        if (File.Exists(exe)) return exe;
        string sevenZr = Download(SevenZrUrl, Path.Combine(Root, "downloads", "7zr.exe"));
        string setup = Download(SevenZipSetupUrl, Path.Combine(Root, "downloads", "7z-x64-setup.exe"));
        if (RunQuiet(sevenZr, $"x -y \"-o{dir}\" \"{setup}\"", 120_000) != 0 || !File.Exists(exe))
            throw new InvalidOperationException("could not unpack 7-Zip");
        return exe;
    }

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };

    static string Download(string url, string path)
    {
        if (File.Exists(path) && new FileInfo(path).Length > 0) return path;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        Http.DefaultRequestHeaders.UserAgent.TryParseAdd("TamrielMerge-setup");
        Console.WriteLine($"    downloading {url}");
        using (var response = Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult())
        {
            response.EnsureSuccessStatusCode();
            using var input = response.Content.ReadAsStream();
            using var output = File.Create(path + ".part");
            input.CopyTo(output);
        }
        File.Move(path + ".part", path, true);
        return path;
    }

    /// <summary>Runs a program with its output shown.</summary>
    static int Run(string exe, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })!;
        p.WaitForExit();
        return p.ExitCode;
    }

    static int RunQuiet(string exe, string args, int timeoutMs)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            p.OutputDataReceived += (_, _) => { };
            p.ErrorDataReceived += (_, _) => { };
            p.BeginOutputReadLine(); p.BeginErrorReadLine();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return -1; }
            return p.ExitCode;
        }
        catch { return -1; }
    }
}
