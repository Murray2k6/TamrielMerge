namespace TamrielMerge.Redguard;

/// <summary>
/// Redguard's CD as the Steam release ships it: a raw image (REDGUARD.bin with a cue sheet, REDGUARD.ins) that
/// DOSBox mounts. Track 1 is MODE1/2352 data — 2352-byte sectors whose 2048 data bytes start after a 16-byte sync
/// and header — holding an ISO 9660 file system (the primary volume descriptor at sector 16); the other tracks are
/// the game's CD audio. Files are found by their path on the disc (case-insensitive, without ";1" versions).
/// </summary>
public sealed class RgCdImage : IDisposable
{
    const int RawSector = 2352, DataOffset = 16, SectorData = 2048;
    readonly FileStream stream;
    readonly Dictionary<string, (uint Sector, uint Size)> files = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> Files => files.Keys;

    public RgCdImage(string binPath)
    {
        stream = File.OpenRead(binPath);
        var pvd = Sector(16);
        if (pvd[0] != 1 || System.Text.Encoding.ASCII.GetString(pvd, 1, 5) != "CD001") throw new InvalidDataException($"{binPath}: no ISO 9660 volume");
        // Root directory record at offset 156 of the primary volume descriptor.
        Walk(BitConverter.ToUInt32(pvd, 156 + 2), BitConverter.ToUInt32(pvd, 156 + 10), "");
    }

    byte[] Sector(uint lba)
    {
        var raw = new byte[RawSector];
        stream.Position = (long)lba * RawSector;
        stream.ReadExactly(raw);
        return raw[DataOffset..(DataOffset + SectorData)];
    }

    byte[] ReadExtent(uint lba, uint size)
    {
        var data = new byte[size];
        for (uint read = 0, s = lba; read < size; s++)
        {
            var sector = Sector(s);
            int n = (int)Math.Min(SectorData, size - read);
            Array.Copy(sector, 0, data, read, n);
            read += (uint)n;
        }
        return data;
    }

    void Walk(uint lba, uint size, string prefix)
    {
        var dir = ReadExtent(lba, size);
        for (int at = 0; at < dir.Length;)
        {
            int len = dir[at];
            if (len == 0) { at = (at / SectorData + 1) * SectorData; continue; }   // records don't cross sectors
            uint extent = BitConverter.ToUInt32(dir, at + 2), length = BitConverter.ToUInt32(dir, at + 10);
            byte flags = dir[at + 25];
            int nameLen = dir[at + 32];
            string name = System.Text.Encoding.ASCII.GetString(dir, at + 33, nameLen);
            at += len;
            if (nameLen == 1 && (name[0] == '\0' || name[0] == '\u0001')) continue;   // . and ..
            int version = name.IndexOf(';');
            if (version >= 0) name = name[..version];
            name = name.TrimEnd('.');
            string path = prefix.Length == 0 ? name : prefix + "\\" + name;
            if ((flags & 2) != 0) Walk(extent, length, path);
            else files[path] = (extent, length);
        }
    }

    public bool Exists(string path) => files.ContainsKey(path.Replace('/', '\\'));

    public byte[]? Read(string path) =>
        files.TryGetValue(path.Replace('/', '\\'), out var f) ? ReadExtent(f.Sector, f.Size) : null;

    public long SizeOf(string path) => files.TryGetValue(path.Replace('/', '\\'), out var f) ? f.Size : -1;

    public void Dispose() => stream.Dispose();
}
