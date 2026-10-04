using System.Buffers.Binary;
using static SuikodenText.Core.Files;

namespace SuikodenText.Core;

public static class ArchiveReader
{
    public const int Sector = 2048, EventBase = 0x21D000, Capacity = 0x40000, FsBase = 0x1572000;
    public static uint U32(ReadOnlySpan<byte> data, int offset)
    {
        Check(offset >= 0 && offset <= data.Length - 4, "The offset is outside the file.");
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }
    public static void Put(byte[] data, int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
    public static int Align(int n, int size = Sector) => checked((n + size - 1) / size * size);
    public static List<Descriptor> Descriptors(byte[] index, int table, long archiveSize)
    {
        Check(archiveSize > 0 && archiveSize % Sector == 0, "The archive size is not a multiple of the sector size.");
        Check(table >= 0 && table % 4 == 0, "Invalid table offset.");
        List<Descriptor> records = [];
        long sector = 0;
        while (sector * Sector < archiveSize)
        {
            int off = checked(table + records.Count * 4);
            uint value = U32(index, off);
            long start = value & 0xFFFFF; int count = (int)(value >> 20);
            Check(start == sector && count > 0 && (start + count) * Sector <= archiveSize,
                "The descriptor does not match the archive.");
            records.Add(new(records.Count, off, start * Sector, count * Sector));
            sector += count;
        }
        return records;
    }
    private static (int Table, List<Descriptor> Records) FindTable(byte[] index, long size)
    {
        Check(size > 0 && size % Sector == 0,
            "The archive size is not a multiple of the sector size.");
        long totalSectors = size / Sector;
        int match = -1, matches = 0;
        for (int off = 0; off <= index.Length - 4; off += 4)
        {
            uint value = U32(index, off);
            if ((value & 0xFFFFF) != 0 || (value >> 20) == 0) continue;

            // Candidate rejection is normal, so do not throw exceptions or
            // allocate Descriptor objects until the entire chain is valid.
            long sector = 0;
            int cursor = off;
            while (sector < totalSectors && cursor <= index.Length - 4)
            {
                value = U32(index, cursor);
                long start = value & 0xFFFFF;
                int count = (int)(value >> 20);
                if (start != sector || count == 0 || count > totalSectors - sector)
                    break;
                sector += count;
                cursor += 4;
            }
            if (sector != totalSectors) continue;
            match = off;
            matches++;
        }
        Check(matches == 1,
            $"Found {matches} matching tables. Check the archive/index pair.");
        return (match, Descriptors(index, match, size));
    }
    private static long ElfBase(byte[] data, int table, int count)
    {
        Check(data.Length >= 52 && data.AsSpan(0, 6).SequenceEqual(new byte[] {127, 69, 76, 70, 1, 1}),
            "--exe must be a 32-bit little-endian ELF file.");
        Check(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(18, 2)) == 8, "The executable is not MIPS.");
        int phoff = checked((int)U32(data, 28));
        int phsize = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(42, 2));
        int phcount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(44, 2));
        Check(phsize >= 32, "Invalid ELF program header.");
        List<long> bases = [];
        for (int i = 0; i < phcount; i++)
        {
            int off = checked(phoff + i * phsize);
            if (U32(data, off) != 1) continue;
            long fileOff = U32(data, off + 4), addr = U32(data, off + 8), size = U32(data, off + 16);
            Check(fileOff + size <= data.Length, "The ELF segment is outside the file.");
            if (fileOff <= table && table + (long)count * 4 <= fileOff + size) bases.Add(addr - fileOff);
        }
        Check(bases.Count == 1, "The table is not contained in a single ELF segment.");
        return bases[0];
    }
    private static readonly int[] EventPointerFields = [8, 12, 20, 24, 28, 32, 36, 40, 48, 56, 60];

    private static int? EventRoot(byte[] data)
    {
        if (data.Length < 0x60 || data.Length > Capacity) return null;
        long relative = (long)U32(data, 0) - EventBase;
        if (relative < 8 || relative % 8 != 0 || relative + 0x58 > data.Length) return null;
        int root = (int)relative;
        if (U32(data, root) != 0 || U32(data, root + 0x34) is not (0x259000 or 0x25D000)) return null;
        foreach (int field in EventPointerFields)
        {
            uint ptr = U32(data, root + field);
            if (ptr < EventBase || ptr >= EventBase + data.Length) return null;
        }
        return root;
    }
    private static int? DetectBank(byte[] data)
    {
        const int bankBase = 0x259000;
        if (data.Length == 0 || data.Length > 0x4000) return null;
        int pointers = 0;
        for (int off = 0; off < Math.Min(data.Length - 3, 0x800); off += 4)
        {
            uint value = U32(data, off);
            if (value == 0) continue;
            if (value >= bankBase && value < bankBase + data.Length && value % 4 == 0) { pointers++; continue; }
            var parsed = TextCodec.Read(data, off);
            return pointers > 0 && parsed is not null && parsed.Value.Text.Length > 0 ? bankBase : null;
        }
        return null;
    }
    private static List<TextEntry> Catalog(byte[] data, int? root, int logicalSize, int baseAddress)
    {
        int first = root.HasValue ? root.Value + 0x58 : 0;
        var refs = new SortedDictionary<int, List<int>>();
        for (int slot = first; slot < logicalSize - 3; slot += 4)
        {
            long target = (long)U32(data, slot) - baseAddress;
            if (target < first || target >= logicalSize) continue;
            if (!refs.TryGetValue((int)target, out var slots)) refs[(int)target] = slots = [];
            slots.Add(slot);
        }
        List<TextEntry> parsed = [];
        foreach (var (offset, slots) in refs)
        {
            var read = TextCodec.Read(data.AsSpan(0, logicalSize), offset);
            if (read is not null && read.Value.Text.Length > 0)
                parsed.Add(new() { Offset = offset, End = read.Value.End, Source = read.Value.Text, PointerSlots = slots });
        }
        List<(int Start, int End)> spans = [];
        foreach (var row in parsed)
        {
            if (spans.Count > 0 && row.Offset <= spans[^1].End)
                spans[^1] = (spans[^1].Start, Math.Max(spans[^1].End, row.End));
            else spans.Add((row.Offset, row.End));
        }
        bool InText(int slot)
        {
            int low = 0, high = spans.Count;
            while (low < high) { int mid = (low + high) / 2; if (spans[mid].Start <= slot + 3) low = mid + 1; else high = mid; }
            return low > 0 && spans[low - 1].End > slot;
        }
        foreach (var row in parsed) row.PointerSlots = row.PointerSlots.Where(p => !InText(p)).ToList();
        var entries = parsed.Where(e => e.PointerSlots.Count > 0).ToList();
        // refs is sorted by target offset. Search only each string's interior.
        var targets = refs.Keys.ToArray();
        foreach (var row in entries)
        {
            int low = 0, high = targets.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (targets[mid] <= row.Offset) low = mid + 1;
                else high = mid;
            }
            List<int> interior = [];
            for (int i = low; i < targets.Length && targets[i] < row.End; i++)
                foreach (int slot in refs[targets[i]])
                    if (!InText(slot)) interior.Add(slot);
            row.InteriorSlots = interior;
        }
        return entries;
    }
    public static bool UsesExecutable(string archive) =>
        Path.GetFileName(archive).Equals("etc.bin", StringComparison.OrdinalIgnoreCase);

    public static ArchiveState Load(string archive, string? fsect = null, string? exe = null)
    {
        var state = new ArchiveState(archive);
        try
        {
            Check(state.Stream.Length > 0, "The archive is empty.");
            if (UsesExecutable(archive) || (string.IsNullOrWhiteSpace(fsect) && !string.IsNullOrWhiteSpace(exe)))
            {
                Check(!string.IsNullOrWhiteSpace(exe), "ETC.BIN requires SLUS_203.87 (--exe).");
                state.Index = File.ReadAllBytes(exe!);
                (state.Table, state.Records) = FindTable(state.Index, state.Stream.Length);
                state.IndexBase = ElfBase(state.Index, state.Table, state.Records.Count);
                state.IndexKind = "exe"; state.IndexName = Path.GetFileName(exe!);
            }
            else
            {
                Check(!string.IsNullOrWhiteSpace(fsect), "Archives other than ETC.BIN require FSECT.BIN.");
                state.Fsect = File.ReadAllBytes(fsect!);
                Check(state.Fsect.Length >= 0x20 && U32(state.Fsect, 4) >= FsBase &&
                    U32(state.Fsect, 4) < FsBase + state.Fsect.Length, "The FSECT layout or base address does not match.");
                (state.Table, state.Records) = FindTable(state.Fsect, state.Stream.Length);
                state.Index = state.Fsect; state.IndexKind = "fsect"; state.IndexBase = FsBase; state.IndexName = "FSECT.BIN";
            }
            var wanted = state.Records.Select(r => state.IndexBase + r.DescriptorOffset).ToHashSet();
            var sizeFields = new Dictionary<long, List<int>>();
            for (int off = 8; off <= state.Index.Length - 4; off += 4)
            {
                long value = U32(state.Index, off);
                if (!wanted.Contains(value)) continue;
                if (!sizeFields.TryGetValue(value, out var fields)) sizeFields[value] = fields = [];
                fields.Add(off - 4);
            }
            foreach (var record in state.Records)
            {
                byte[] data = state.ReadBlock(record);
                int? root = EventRoot(data), bank = root is null ? DetectBank(data) : null;
                if (root is null && bank is null)
                {
                    state.Strings.AddRange(PlainText.ReadOverlay(data, record.Index, state.IndexKind == "exe" ? state.Index : null));
                    continue;
                }
                int baseAddress = root is not null ? EventBase : bank!.Value;
                int capacity = root is not null ? checked((int)U32(data, root.Value + 0x34) - EventBase) : 0x25D000 - baseAddress;
                Check(data.Length <= capacity, "The block exceeds its RAM region.");
                var fields = sizeFields.GetValueOrDefault(state.IndexBase + record.DescriptorOffset, [])
                    .Where(p => record.Size - Sector < U32(state.Index, p) && U32(state.Index, p) <= record.Size).ToList();
                var sizes = fields.Select(p => checked((int)U32(state.Index, p))).Distinct().ToList();
                Check(sizes.Count == 1, $"block_{record.Index:D4}: the logical size could not be determined.");
                int logical = sizes[0];
                Check(data.AsSpan(logical).IndexOfAnyExcept((byte)0) < 0, "Trailing padding contains data.");
                var entries = Catalog(data, root, logical, baseAddress);

                foreach (var e in entries) e.Id = $"block_{record.Index:D4}_text_{e.Offset:X6}";
                state.Blocks.Add(new(record, root, baseAddress, capacity, root is null ? "dialog_bank" : "event",
                    logical, fields, entries));
            }
            // Hanya tabel UI ELF yang sudah diverifikasi di PlainText.ReadExecutable.
            // Ini bukan fallback scan string mentah.
            if (state.IndexKind == "exe" && PlainText.SupportsExecutable(state.Index))
                state.Executable.AddRange(PlainText.ReadExecutable(state.Index));
            return state;
        }
        catch { state.Dispose(); throw; }
    }
    public static TranslationDocument Export(ArchiveState state) => new()
    {
        Strings = state.Strings.Count > 0 ? PlainText.Export(state.Strings) : null,
        Executable = state.Executable.Count > 0 ? PlainText.Export(state.Executable) : null,
        Blocks = state.Blocks.Where(b => b.Entries.Count > 0).Select(b => new TranslationBlock
        {
            Index = b.Record.Index,
            Entries = b.Entries.Select(e => new TranslationEntry { Id = e.Id, Source = e.Source, Translation = e.Source }).ToList()
        }).ToList()
    };
    public static object Summary(ArchiveState state) => new
    {
        archive = Path.GetFileName(state.ArchivePath),
        entries = state.Blocks.Sum(b => b.Entries.Count) + state.Strings.Count + state.Executable.Count,
        strings = state.Strings.Count, executable = state.Executable.Count,
        blocks = state.Blocks.Where(b => b.Entries.Count > 0).Select(b => new
        {
            index = b.Record.Index, entries = b.Entries.Count
        }).ToArray()
    };
}
