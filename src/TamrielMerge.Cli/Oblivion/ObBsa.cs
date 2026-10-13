using System.IO.Compression;
using System.Text;

namespace TamrielMerge.Oblivion;

/// <summary>
/// An Oblivion BSA (version 103). Header: "BSA\0", version, folder record offset, archive flags, folder and file
/// counts, name lengths, file flags. Folder records (hash, file count, offset) are followed, per folder, by its name
/// (length-prefixed, NUL-terminated) and its file records (hash, size, offset); then a block of NUL-terminated file
/// names in the same order. A file is zlib-compressed (preceded by its original size) when the archive's
/// "compressed" flag differs from the file's size bit 30.
/// </summary>
public sealed class ObBsa
{
    readonly string path;
    readonly Dictionary<string, (long Offset, int Size, bool Compressed)> files = new(StringComparer.OrdinalIgnoreCase);

    public string FilePath => path;
    public IEnumerable<string> Names => files.Keys;
    public bool Contains(string name) => files.ContainsKey(ObData.Normalize(name));

    public ObBsa(string path)
    {
        this.path = path;
        using var r = new BinaryReader(File.OpenRead(path));
        if (r.ReadUInt32() != 0x00415342) throw new InvalidDataException($"{path} is not a BSA");
        uint version = r.ReadUInt32();
        if (version != 103) throw new InvalidDataException($"{path}: BSA version {version}, expected 103 (Oblivion)");
        uint folderOffset = r.ReadUInt32(), flags = r.ReadUInt32();
        int folderCount = r.ReadInt32(), fileCount = r.ReadInt32();
        r.ReadUInt32(); r.ReadUInt32(); r.ReadUInt32();   // folder/file name lengths, file flags
        bool compressedByDefault = (flags & 0x4) != 0;
        r.BaseStream.Position = folderOffset;
        var folderSizes = new int[folderCount];
        for (int i = 0; i < folderCount; i++) { r.ReadUInt64(); folderSizes[i] = r.ReadInt32(); r.ReadUInt32(); }
        var records = new List<(string Folder, int Size, uint Offset)>();
        for (int i = 0; i < folderCount; i++)
        {
            int len = r.ReadByte();
            string folder = Encoding.ASCII.GetString(r.ReadBytes(len)).TrimEnd('\0');
            for (int j = 0; j < folderSizes[i]; j++)
            {
                r.ReadUInt64();
                int size = r.ReadInt32();
                uint offset = r.ReadUInt32();
                records.Add((folder, size, offset));
            }
        }
        for (int i = 0; i < fileCount; i++)
        {
            var name = new StringBuilder();
            for (byte b; (b = r.ReadByte()) != 0;) name.Append((char)b);
            var (folder, size, offset) = records[i];
            bool toggle = (size & 0x40000000) != 0;
            files[ObData.Normalize(folder + "\\" + name)] = (offset, size & 0x3FFFFFFF, compressedByDefault != toggle);
        }
    }

    public byte[]? Read(string name)
    {
        if (!files.TryGetValue(ObData.Normalize(name), out var f)) return null;
        using var s = File.OpenRead(path);
        s.Position = f.Offset;
        if (!f.Compressed)
        {
            var raw = new byte[f.Size];
            s.ReadExactly(raw);
            return raw;
        }
        var lenBytes = new byte[4];
        s.ReadExactly(lenBytes);
        var output = new byte[BitConverter.ToInt32(lenBytes)];
        using var z = new ZLibStream(new SubStream(s, f.Size - 4), CompressionMode.Decompress);
        z.ReadExactly(output);
        return output;
    }

    /// <summary>A read-only window of a stream, so the decompressor stops at the file's end.</summary>
    sealed class SubStream(Stream inner, long length) : Stream
    {
        long left = length;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = inner.Read(buffer, offset, (int)Math.Min(count, left));
            left -= n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => length - left; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
