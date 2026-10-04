using System.Buffers.Binary;
using static SuikodenText.Core.ArchiveReader;
using static SuikodenText.Core.Files;
namespace SuikodenText.Core;

// Reference checks for verified ELF text tables.
public static class TextRelocation
{
    public static long? InferOverlayBase(byte[] data, List<PlainEntry> entries)
    {
        return PlainText.VerifiedOverlayBase(data);
    }

    public static List<(int High, int Low, bool Or)> References(byte[] data, uint target,
        IEnumerable<PlainEntry> strings, int length = 1)
    {
        var occupied = strings.OrderBy(e => e.Offset).ToArray();
        bool InString(int off)
        {
            int low = 0, high = occupied.Length;
            while (low < high)
            {
                int mid = low + (high - low) / 2;
                if (occupied[mid].Offset < off + 4) low = mid + 1; else high = mid;
            }
            return low > 0 && occupied[low - 1].Offset + occupied[low - 1].Length + 1 > off;
        }
        bool Matches(uint v) => v >= target && (long)v < (long)target + length;
        var result = new List<(int, int, bool)>();
        for (int p = 0; p <= data.Length - 4; p += 4)
        {
            uint v = U32(data, p);
            if (Matches(v) && !InString(p)) result.Add((-1, p, false));
            if (v >> 26 != 15 || ((v >> 21) & 31) != 0 || InString(p)) continue;
            uint reg = (v >> 16) & 31;
            if (reg == 0) continue;
            for (int q = p + 4; q <= Math.Min(data.Length - 4, p + 24); q += 4)
            {
                uint w = U32(data, q), op = w >> 26, rs = (w >> 21) & 31, rt = (w >> 16) & 31;
                if (InString(q)) break;
                if ((op == 9 || op == 13) && rs == reg)
                {
                    uint address = op == 13 ? (v << 16) | (w & 0xFFFF)
                        : unchecked((uint)((v << 16) + (int)(short)(w & 0xFFFF)));
                    if (Matches(address)) result.Add((p, q, op == 13));
                }
                if (op is >= 1 and <= 7 || (op == 0 && (w & 63) is 8 or 9)) break;
                bool writes = op == 0 ? ((w >> 11) & 31) == reg
                    : (op is >= 8 and <= 15 or >= 32 and <= 39) && rt == reg;
                if (writes) break;
            }
        }
        return result;
    }
    public static void Redirect(byte[] data, List<(int High, int Low, bool Or)> refs, uint address)
    {
        Check(refs.Count > 0, "No references were found for relocation.");
        foreach (var r in refs)
        {
            if (r.High < 0) { Put(data, r.Low, address); continue; }
            uint old = U32(data, r.High);
            uint high = r.Or ? address >> 16 : unchecked(address + 0x8000) >> 16;
            Check((old & 0xFFFF) == high,
                "This MIPS reference requires a shared LUI rewrite; its relocation is not yet supported.");
            Put(data, r.Low, (U32(data, r.Low) & 0xFFFF0000) | (address & 0xFFFF));
        }
    }
}


// The US menu loader uses a descriptor table and a separate runtime-size table.
// Metadata addresses are decoded from the verified getters, not from text offsets.
public sealed class OverlayTextAllocation
{
    private readonly byte[] index;
    private readonly int sizeField;
    private readonly int capacity;
    private int end;
    public long Base { get; }

    public OverlayTextAllocation(ArchiveState state, byte[] executable, byte[] data, int block)
    {
        Check(state.IndexKind == "exe" && PlainText.SupportsExecutable(executable),
            "Growing this menu requires the verified paired US executable.");
        index = executable;
        Base = PlainText.VerifiedOverlayBase(data)
            ?? throw new InvalidDataException("No verified menu module layout.");
        int Offset(long address) => checked((int)(address - state.IndexBase));
        long Table(int highAddress, int lowAddress, uint highMask, uint lowMask)
        {
            uint high = U32(index, Offset(highAddress)), low = U32(index, Offset(lowAddress));
            Check((high & 0xFFFF0000) == highMask && (low & 0xFFFF0000) == lowMask,
                "The menu loader table instructions do not match.");
            return ((long)(high & 0xFFFF) << 16) + (short)(low & 0xFFFF);
        }
        // sub_1732670: descriptorPointers[moduleId].
        long descriptors = Table(0x1732678, 0x1732680, 0x3C020000, 0x24420000);
        // sub_1732920: runtimeSizes[moduleId], including module work data/BSS.
        long sizes = Table(0x1732928, 0x1732930, 0x3C020000, 0x24420000);
        uint id = U32(data, 0);
        Check(id < 0x46, "Invalid menu module ID.");
        Check(U32(index, Offset(descriptors + id * 4)) ==
            state.IndexBase + state.Records[block].DescriptorOffset,
            "The menu ID does not reference this ETC descriptor.");
        sizeField = Offset(sizes + id * 4);
        int runtimeSize = checked((int)U32(index, sizeField));
        Check(runtimeSize > 0 && data.Length <= ((long)runtimeSize + Sector) / Sector * Sector,
            "The menu file is larger than its loader allocation.");

        // Read the actual arena base/size tables used by GetRegionBase/GetRegionSize.
        long bases = Table(0x188B764, 0x188B76C, 0x3C020000, 0x24420000);
        long sizes64 = Table(0x188B794, 0x188B79C, 0x3C020000, 0x24420000);
        int limit = int.MaxValue;
        for (int region = 1; region < 0x44; region++)
        {
            long start = U32(index, Offset(bases + region * 4));
            long length = BinaryPrimitives.ReadInt64LittleEndian(
                index.AsSpan(Offset(sizes64 + region * 8), 8));
            // Verified overlay profiles load at arena + 0x1C0, after allocator/object headers.
            if (start + 0x1C0 != Base || length < 0x1C0 + (long)runtimeSize + Sector) continue;
            limit = Math.Min(limit, checked((int)(length - 0x1C0 - Sector)));
        }
        Check(limit != int.MaxValue, "No matching runtime arena for this menu.");
        capacity = limit;
        end = Math.Max(data.Length, runtimeSize);
    }

    public uint Allocate(ref byte[] data, byte[] bytes)
    {
        int target = Align(end, 8);
        int next = checked(target + bytes.Length + 1);
        int padded = Align(next);
        Check(padded <= capacity, "The expanded menu exceeds its verified runtime arena.");
        if (data.Length < padded) Array.Resize(ref data, padded);
        bytes.CopyTo(data, target);
        data[target + bytes.Length] = 0;
        end = next;
        // The loader rounds (size + 0x800) down to a sector before allocation.
        Put(index, sizeField, checked((uint)end));
        return checked((uint)(Base + target));
    }
}
