using System.IO;
using System.Buffers.Binary;
using System.Text;

namespace Satchel.Services.Import;

/// <summary>
/// Read-only access to an OLE compound file ([MS-CFB]) — the container of old .doc, .xls and .ppt files: a small FAT
/// file system inside one file. Malformed files throw <see cref="InvalidDataException"/>.
/// </summary>
public sealed class CompoundFile
{
    private static readonly byte[] Signature = [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1];
    private const uint EndOfChain = 0xFFFFFFFE, FreeSector = 0xFFFFFFFF;

    private readonly byte[] _data;
    private readonly int _sectorSize;
    private readonly int _miniSectorSize;
    private readonly uint _miniCutoff;
    private readonly uint[] _fat;
    private readonly uint[] _miniFat;
    private readonly byte[] _miniStream;
    private readonly List<Entry> _entries = [];

    private sealed record Entry(string Name, int Type, uint Start, long Size);

    public static bool IsCompoundFile(ReadOnlySpan<byte> data) => data.Length >= 512 && data[..8].SequenceEqual(Signature);

    public CompoundFile(byte[] data)
    {
        if (!IsCompoundFile(data)) throw new InvalidDataException("Not a compound file");
        _data = data;
        int sectorShift = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x1E));
        int miniShift = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(0x20));
        if (sectorShift is not (9 or 12) || miniShift != 6) throw new InvalidDataException("Unsupported sector size");
        _sectorSize = 1 << sectorShift;
        _miniSectorSize = 1 << miniShift;
        uint fatSectors = U32(0x2C);
        uint firstDir = U32(0x30);
        _miniCutoff = U32(0x38);
        uint firstMiniFat = U32(0x3C);
        uint firstDifat = U32(0x44);
        uint difatSectors = U32(0x48);

        // The FAT's own sectors: 109 listed in the header, the rest in a chain of DIFAT sectors.
        var fatList = new List<uint>();
        for (int i = 0; i < 109 && fatList.Count < fatSectors; i++)
        {
            uint s = U32(0x4C + i * 4);
            if (s != FreeSector) fatList.Add(s);
        }
        uint difat = firstDifat;
        int perDifat = _sectorSize / 4 - 1;
        for (uint n = 0; n < difatSectors && difat < EndOfChain && fatList.Count < fatSectors; n++)
        {
            int off = SectorOffset(difat);
            for (int i = 0; i < perDifat && fatList.Count < fatSectors; i++)
            {
                uint s = U32(off + i * 4);
                if (s != FreeSector) fatList.Add(s);
            }
            difat = U32(off + perDifat * 4);
        }
        int perSector = _sectorSize / 4;
        _fat = new uint[fatList.Count * perSector];
        for (int i = 0; i < fatList.Count; i++)
        {
            int off = SectorOffset(fatList[i]);
            for (int j = 0; j < perSector; j++) _fat[i * perSector + j] = U32(off + j * 4);
        }

        // Directory: 128-byte entries along the directory chain.
        var dir = ReadChain(firstDir, -1);
        for (int off = 0; off + 128 <= dir.Length; off += 128)
        {
            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(dir.AsSpan(off + 0x40));
            int type = dir[off + 0x42];
            var name = nameLength >= 2 ? Encoding.Unicode.GetString(dir, off, Math.Min(64, nameLength) - 2) : "";
            uint start = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(off + 0x74));
            long size = (long)BinaryPrimitives.ReadUInt64LittleEndian(dir.AsSpan(off + 0x78));
            if (_sectorSize == 512) size &= 0xFFFFFFFF; // version 3 files only use the low 32 bits
            _entries.Add(new Entry(name, type, start, size));
        }
        if (_entries.Count == 0 || _entries[0].Type != 5) throw new InvalidDataException("No root entry");

        // Small streams live in the "mini stream" (owned by the root entry), addressed through the mini FAT.
        var root = _entries[0];
        _miniStream = root.Size > 0 ? ReadChain(root.Start, root.Size) : [];
        var miniFatBytes = firstMiniFat < EndOfChain ? ReadChain(firstMiniFat, -1) : [];
        _miniFat = new uint[miniFatBytes.Length / 4];
        for (int i = 0; i < _miniFat.Length; i++) _miniFat[i] = BinaryPrimitives.ReadUInt32LittleEndian(miniFatBytes.AsSpan(i * 4));
    }

    public IEnumerable<string> StreamNames => _entries.Where(e => e.Type == 2).Select(e => e.Name);

    /// <summary>The bytes of a stream (searched by name anywhere in the file), or null.</summary>
    public byte[]? Stream(string name)
    {
        var entry = _entries.FirstOrDefault(e => e.Type == 2 && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase));
        if (entry == null) return null;
        if (entry.Size == 0) return [];
        return entry.Size < _miniCutoff ? ReadMiniChain(entry.Start, entry.Size) : ReadChain(entry.Start, entry.Size);
    }

    private uint U32(int offset)
    {
        if (offset < 0 || offset + 4 > _data.Length) throw new InvalidDataException("Truncated file");
        return BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(offset));
    }

    private int SectorOffset(uint sector)
    {
        long off = ((long)sector + 1) * _sectorSize;
        if (off < 0 || off + _sectorSize > _data.Length + (long)_sectorSize) throw new InvalidDataException("Sector outside the file");
        return (int)off;
    }

    /// <summary>Follows a FAT chain. <paramref name="size"/> −1 reads the whole chain.</summary>
    private byte[] ReadChain(uint start, long size)
    {
        using var ms = new MemoryStream();
        uint s = start;
        int guard = _fat.Length + 1;
        while (s < EndOfChain && s != FreeSector && guard-- > 0)
        {
            int off = SectorOffset(s);
            int count = Math.Min(_sectorSize, _data.Length - off);
            if (count <= 0) break;
            ms.Write(_data, off, count);
            if (size >= 0 && ms.Length >= size) break;
            if (s >= _fat.Length) throw new InvalidDataException("Broken sector chain");
            s = _fat[s];
        }
        var bytes = ms.ToArray();
        return size >= 0 && bytes.Length > size ? bytes[..(int)size] : bytes;
    }

    private byte[] ReadMiniChain(uint start, long size)
    {
        var result = new byte[size];
        long written = 0;
        uint s = start;
        int guard = _miniFat.Length + 1;
        while (s < EndOfChain && written < size && guard-- > 0)
        {
            long off = (long)s * _miniSectorSize;
            if (off + _miniSectorSize > _miniStream.Length) throw new InvalidDataException("Mini sector outside the stream");
            int count = (int)Math.Min(_miniSectorSize, size - written);
            Array.Copy(_miniStream, off, result, written, count);
            written += count;
            if (s >= _miniFat.Length) break;
            s = _miniFat[s];
        }
        return result;
    }
}
