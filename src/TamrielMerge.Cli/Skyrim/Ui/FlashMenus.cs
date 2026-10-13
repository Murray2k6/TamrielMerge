using System.Diagnostics;

namespace TamrielMerge.Skyrim.Ui;

/// <summary>
/// TamrielMerge's own Skyrim menus: ActionScript 2 compiled into Flash 8 files (Skyrim's Scaleform plays AS2) with
/// JPEXS FFDec (tools/ffdec, Java). A menu starts as a minimal SWF — one frame holding an empty DoAction block — into
/// which FFDec imports the script; the result is decompiled back as a check. In game SKSE's UI.OpenCustomMenu shows it
/// (Data\Interface\...), and the script talks to Papyrus through skse.SendModEvent; Papyrus answers with UI.Invoke*.
/// </summary>
public static class FlashMenus
{
    public const int Width = 1280, Height = 720, FramesPerSecond = 30;

    /// <summary>FFDec's command-line jar, from TAMRIELMERGE_TOOLS or the workspace's tools folder.</summary>
    public static string? FfdecJar()
    {
        foreach (var root in new[] { Environment.GetEnvironmentVariable("TAMRIELMERGE_TOOLS"), Path.Combine(Directory.GetCurrentDirectory(), "tools"), FindTools() })
            if (root is not null && File.Exists(Path.Combine(root, "ffdec", "ffdec-cli.jar"))) return Path.Combine(root, "ffdec", "ffdec-cli.jar");
        return null;
    }

    static string? FindTools()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "tools", "ffdec"))) return Path.Combine(d.FullName, "tools");
        return null;
    }

    /// <summary>A minimal Flash 8 movie: stage size, frame rate, one frame with an empty DoAction and ShowFrame.</summary>
    public static byte[] EmptyMovie()
    {
        var body = new List<byte>();
        // Frame RECT: 16 bits per value (twips: 1280 x 20 = 25600 fits a signed 16-bit field).
        var bits = new List<bool>();
        void Put(int value, int n) { for (int i = n - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0); }
        Put(16, 5); Put(0, 16); Put(Width * 20, 16); Put(0, 16); Put(Height * 20, 16);
        while (bits.Count % 8 != 0) bits.Add(false);
        for (int i = 0; i < bits.Count; i += 8)
        {
            int b = 0;
            for (int k = 0; k < 8; k++) b = (b << 1) | (bits[i + k] ? 1 : 0);
            body.Add((byte)b);
        }
        body.AddRange([0x00, (byte)FramesPerSecond]);   // frame rate, 8.8 fixed
        body.AddRange(BitConverter.GetBytes((ushort)1)); // frame count
        void Tag(int code, byte[] data)
        {
            if (data.Length < 63) body.AddRange(BitConverter.GetBytes((ushort)((code << 6) | data.Length)));
            else { body.AddRange(BitConverter.GetBytes((ushort)((code << 6) | 0x3F))); body.AddRange(BitConverter.GetBytes(data.Length)); }
            body.AddRange(data);
        }
        Tag(9, [0, 0, 0]);   // SetBackgroundColor (menus draw over the game; Scaleform ignores the stage colour)
        Tag(12, [0x00]);     // DoAction: ActionEnd only (FFDec fills it)
        Tag(1, []);          // ShowFrame
        Tag(0, []);          // End
        var file = new List<byte> { (byte)'F', (byte)'W', (byte)'S', 8 };
        file.AddRange(BitConverter.GetBytes(8 + body.Count));
        file.AddRange(body);
        return [.. file];
    }

    /// <summary>
    /// Compiles AS2 source into a menu SWF at <paramref name="outSwf"/>. Returns FFDec's output; throws when FFDec is
    /// missing or the script didn't make it into the movie.
    /// </summary>
    public static string Compile(string as2Source, string outSwf)
    {
        var jar = FfdecJar() ?? throw new FileNotFoundException("FFDec (tools/ffdec) is missing: run setup");
        string work = Path.Combine(Path.GetTempPath(), "tm-flash-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            string scripts = Path.Combine(work, "scripts", "frame_1");
            Directory.CreateDirectory(scripts);
            File.WriteAllText(Path.Combine(scripts, "DoAction.as"), as2Source);
            string empty = Path.Combine(work, "empty.swf");
            File.WriteAllBytes(empty, EmptyMovie());
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outSwf))!);
            string output = Run(jar, ["-importScript", empty, Path.GetFullPath(outSwf), Path.Combine(work, "scripts")]);
            if (!File.Exists(outSwf)) throw new InvalidOperationException($"FFDec made no movie: {output}");
            // Read the script back out: FFDec drops what it can't compile, so a missing function shows here.
            string back = Path.Combine(work, "back");
            Run(jar, ["-export", "script", back, Path.GetFullPath(outSwf)]);
            var exported = Directory.Exists(back) ? Directory.GetFiles(back, "*.as", SearchOption.AllDirectories) : [];
            if (exported.Length == 0 || new FileInfo(exported[0]).Length < as2Source.Length / 4)
                throw new InvalidOperationException($"the script didn't compile into {outSwf}: {output}");
            return output;
        }
        finally { try { Directory.Delete(work, true); } catch { } }
    }

    static string Run(string jar, string[] args)
    {
        // The installed Java, else the runtime setup unpacked (Setup.Dependencies.Java).
        var psi = new ProcessStartInfo(Setup.Dependencies.Java ?? "java") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(jar);
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        string text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return text;
    }
}
