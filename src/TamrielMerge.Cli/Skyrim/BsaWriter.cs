namespace TamrielMerge.Skyrim;

/// <summary>
/// Skyrim Special Edition archives (BSA version 105), uncompressed (voice files must not be compressed) or compressed
/// with LZ4 (meshes, textures, scripts: <see cref="Pack"/>, K4os.Compression.LZ4, MIT). Layout: header,
/// folder records (hash, file count, offset of the folder's block plus the file-name total, as Skyrim reads it), each
/// folder's name and file records (hash, size, data offset), the file names, then the files' data. Folders and files go
/// in hash order. The hash is Bethesda's (checked against every name in Skyrim - Sounds.bsa).
/// </summary>
public static class BsaWriter
{
    /// <summary>Archive flags: folder names and file names kept, as Skyrim's own sound archive has (0x13).</summary>
    const uint ArchiveFlags = 0x13;

    /// <summary>Content flags from what the archive holds: meshes 1, textures 2, sounds 8, voices 0x10.</summary>
    static uint ContentFlags(IEnumerable<string> names) => names.Aggregate(0u, (f, n) => f | Path.GetExtension(n).ToLowerInvariant() switch
    {
        ".nif" or ".kf" => 0x1u,
        ".dds" => 0x2u,
        ".wav" or ".xwm" => 0x8u,
        ".fuz" or ".lip" => 0x10u,
        _ => 0u,
    });

    /// <summary>Writes <paramref name="files"/> (path in the archive, file on disk) to <paramref name="path"/>.</summary>
    public static void Write(string path, IReadOnlyList<(string InArchive, string Source)> files)
    {
        var folders = files
            .Select(f => (Rel: f.InArchive.Replace('/', '\\').ToLowerInvariant().TrimStart('\\'), f.Source))
            .GroupBy(f => Path.GetDirectoryName(f.Rel) ?? "")
            .Select(g => (Name: g.Key, Hash: Hash(g.Key, ""),
                Files: g.Select(f => (Name: Path.GetFileName(f.Rel), Hash: FileHash(Path.GetFileName(f.Rel)), f.Source, Size: new FileInfo(f.Source).Length))
                    .OrderBy(f => f.Hash).ToList()))
            .OrderBy(f => f.Hash).ToList();
        int fileCount = folders.Sum(f => f.Files.Count);
        int folderNames = folders.Sum(f => f.Name.Length + 2);          // a length byte, the name, its null
        int fileNames = folders.Sum(f => f.Files.Sum(x => x.Name.Length + 1));
        long blocks = 36 + 24L * folders.Count;
        long data = blocks + folderNames + 16L * fileCount + fileNames;
        long total = data + folders.Sum(f => f.Files.Sum(x => x.Size));
        if (total > uint.MaxValue) throw new InvalidOperationException($"{path}: {total:N0} bytes is more than an archive can hold");

        using var w = new BinaryWriter(File.Create(path + ".tmp"));
        w.Write("BSA\0"u8);
        w.Write(105u); w.Write(36u); w.Write(ArchiveFlags);
        w.Write((uint)folders.Count); w.Write((uint)fileCount); w.Write((uint)folderNames); w.Write((uint)fileNames); w.Write(ContentFlags(files.Select(f => f.InArchive)));
        long block = blocks;
        foreach (var f in folders)
        {
            w.Write(f.Hash); w.Write((uint)f.Files.Count); w.Write(0u); w.Write((ulong)(block + fileNames));
            block += f.Name.Length + 2 + 16L * f.Files.Count;
        }
        long offset = data;
        foreach (var f in folders)
        {
            w.Write((byte)(f.Name.Length + 1)); w.Write(System.Text.Encoding.ASCII.GetBytes(f.Name)); w.Write((byte)0);
            foreach (var x in f.Files)
            {
                w.Write(x.Hash); w.Write((uint)x.Size); w.Write((uint)offset);
                offset += x.Size;
            }
        }
        foreach (var f in folders)
            foreach (var x in f.Files) { w.Write(System.Text.Encoding.ASCII.GetBytes(x.Name)); w.Write((byte)0); }
        foreach (var f in folders)
            foreach (var x in f.Files)
                using (var s = File.OpenRead(x.Source)) s.CopyTo(w.BaseStream);
        w.Flush();
        w.Close();
        File.Move(path + ".tmp", path, true);
    }

    /// <summary>A file as an archive stores it: its bytes (for a compressed one, the original size then an LZ4 frame,
    /// as Skyrim Special Edition reads it) and whether they are compressed.</summary>
    public readonly record struct Stored(string InArchive, byte[] Data, bool Compressed);

    /// <summary>A file's bytes compressed for an archive (LZ4 high compression: unpacking costs the same, packing is done
    /// once), or as they are when compressing doesn't make them smaller.</summary>
    public static Stored Pack(string inArchive, byte[] raw)
    {
        using var ms = new MemoryStream(raw.Length / 2 + 64);
        ms.Write(BitConverter.GetBytes((uint)raw.Length));
        using (var lz = K4os.Compression.LZ4.Streams.LZ4Stream.Encode(ms, new K4os.Compression.LZ4.Streams.LZ4EncoderSettings { CompressionLevel = K4os.Compression.LZ4.LZ4Level.L09_HC }, leaveOpen: true))
            lz.Write(raw);
        return ms.Length < raw.Length ? new Stored(inArchive, ms.ToArray(), true) : new Stored(inArchive, raw, false);
    }

    /// <summary>Bytes an archive of these files takes besides their data: header, folder and file records, names.</summary>
    public static long Overhead(IEnumerable<string> inArchive)
    {
        var names = inArchive.Select(n => n.Replace('/', '\\').ToLowerInvariant().TrimStart('\\')).ToList();
        var folders = names.Select(n => Path.GetDirectoryName(n) ?? "").Distinct().ToList();
        return 36 + 24L * folders.Count + folders.Sum(f => f.Length + 2) + names.Sum(n => 16 + Path.GetFileName(n).Length + 1);
    }

    /// <summary>
    /// Writes files already stored as <see cref="Pack"/> makes them. A compressed archive (flag 4) holds compressed files;
    /// one stored as it is (it didn't get smaller) has bit 30 of its size set, which reverses the archive's setting for it.
    /// An uncompressed archive holds files as they are (voices and sounds, which Skyrim's own archives don't compress).
    /// </summary>
    public static void Write(string path, IReadOnlyList<Stored> files, bool compressed)
    {
        var folders = files
            .Select(f => (Rel: f.InArchive.Replace('/', '\\').ToLowerInvariant().TrimStart('\\'), f))
            .GroupBy(f => Path.GetDirectoryName(f.Rel) ?? "")
            .Select(g => (Name: g.Key, Hash: Hash(g.Key, ""),
                Files: g.Select(f => (Name: Path.GetFileName(f.Rel), Hash: FileHash(Path.GetFileName(f.Rel)), File: f.f)).OrderBy(f => f.Hash).ToList()))
            .OrderBy(f => f.Hash).ToList();
        int fileCount = folders.Sum(f => f.Files.Count);
        int folderNames = folders.Sum(f => f.Name.Length + 2);
        int fileNames = folders.Sum(f => f.Files.Sum(x => x.Name.Length + 1));
        long blocks = 36 + 24L * folders.Count;
        long data = blocks + folderNames + 16L * fileCount + fileNames;
        long total = data + files.Sum(f => (long)f.Data.Length);
        if (total > int.MaxValue) throw new InvalidOperationException($"{path}: {total:N0} bytes is more than an archive can hold");

        using (var w = new BinaryWriter(File.Create(path + ".tmp")))
        {
            w.Write("BSA\0"u8);
            w.Write(105u); w.Write(36u); w.Write(ArchiveFlags | (compressed ? 0x4u : 0));
            w.Write((uint)folders.Count); w.Write((uint)fileCount); w.Write((uint)folderNames); w.Write((uint)fileNames); w.Write(ContentFlags(files.Select(f => f.InArchive)));
            long block = blocks;
            foreach (var f in folders)
            {
                w.Write(f.Hash); w.Write((uint)f.Files.Count); w.Write(0u); w.Write((ulong)(block + fileNames));
                block += f.Name.Length + 2 + 16L * f.Files.Count;
            }
            long offset = data;
            foreach (var f in folders)
            {
                w.Write((byte)(f.Name.Length + 1)); w.Write(System.Text.Encoding.ASCII.GetBytes(f.Name)); w.Write((byte)0);
                foreach (var x in f.Files)
                {
                    uint size = (uint)x.File.Data.Length;
                    if (x.File.Compressed != compressed) size |= 0x40000000;
                    w.Write(x.Hash); w.Write(size); w.Write((uint)offset);
                    offset += x.File.Data.Length;
                }
            }
            foreach (var f in folders)
                foreach (var x in f.Files) { w.Write(System.Text.Encoding.ASCII.GetBytes(x.Name)); w.Write((byte)0); }
            foreach (var f in folders)
                foreach (var x in f.Files) w.Write(x.File.Data);
        }
        File.Move(path + ".tmp", path, true);
    }

    static ulong FileHash(string file)
    {
        int dot = file.LastIndexOf('.');
        return dot >= 0 ? Hash(file[..dot], file[dot..]) : Hash(file, "");
    }

    /// <summary>Bethesda's archive hash of a lower-case name (folder path, or file name without its extension).</summary>
    static ulong Hash(string name, string ext)
    {
        int n = name.Length;
        ulong h1 = n == 0 ? 0 : (ulong)(byte)name[n - 1] | ((ulong)(n > 2 ? (byte)name[n - 2] : 0) << 8) | ((ulong)n << 16) | ((ulong)(byte)name[0] << 24);
        h1 |= ext switch { ".kf" => 0x80UL, ".nif" => 0x8000UL, ".dds" => 0x8080UL, ".wav" => 0x80000000UL, _ => 0UL };
        uint h2 = 0;
        for (int i = 1; i < n - 2; i++) h2 = unchecked(h2 * 0x1003F + (byte)name[i]);
        uint h3 = 0;
        foreach (char c in ext) h3 = unchecked(h3 * 0x1003F + (byte)c);
        return unchecked(h1 + ((ulong)unchecked(h2 + h3) << 32));
    }
}
