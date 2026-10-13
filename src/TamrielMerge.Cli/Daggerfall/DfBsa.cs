using System.Text;

namespace TamrielMerge.Daggerfall;

/// <summary>
/// Daggerfall's BSA archive (unrelated to Skyrim's BSA): Int16 record count, UInt16 directory type, records
/// stored back to back from offset 4, and the directory at the end of the file. Name directories have 18-byte
/// entries (14-byte name + Int32 size); number directories have 8-byte entries (UInt32 id + Int32 size).
/// Format reference: Daggerfall Unity BsaFile.cs.
/// </summary>
public sealed class DfBsa
{
    public sealed record Entry(string Name, uint Id, int Offset, int Size);

    readonly byte[] data;
    public IReadOnlyList<Entry> Entries { get; }
    readonly Dictionary<string, Entry> byName = new(StringComparer.OrdinalIgnoreCase);

    public DfBsa(string path)
    {
        data = File.ReadAllBytes(path);
        int count = BitConverter.ToInt16(data, 0);
        ushort type = BitConverter.ToUInt16(data, 2);
        bool named = type == 0x0100;
        int entrySize = named ? 18 : 8;
        int dir = data.Length - entrySize * count;
        var entries = new List<Entry>(count);
        int offset = 4;
        for (int i = 0; i < count; i++)
        {
            int p = dir + i * entrySize;
            string name; uint id = 0; int size;
            if (named)
            {
                int end = Array.IndexOf(data, (byte)0, p, 14);
                name = Encoding.ASCII.GetString(data, p, (end < 0 ? p + 14 : end) - p);
                size = BitConverter.ToInt32(data, p + 14);
            }
            else
            {
                id = BitConverter.ToUInt32(data, p);
                name = id.ToString();
                size = BitConverter.ToInt32(data, p + 4);
            }
            var e = new Entry(name, id, offset, size);
            entries.Add(e);
            byName.TryAdd(name, e);
            offset += size;
        }
        Entries = entries;
    }

    public ReadOnlySpan<byte> Record(int index) => data.AsSpan(Entries[index].Offset, Entries[index].Size);
    public byte[] Record(string name) => byName.TryGetValue(name, out var e) ? data.AsSpan(e.Offset, e.Size).ToArray() : throw new KeyNotFoundException(name);
    public bool Contains(string name) => byName.ContainsKey(name);
}
