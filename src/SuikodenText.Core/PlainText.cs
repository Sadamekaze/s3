using System.Text;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using static SuikodenText.Core.Files;
using static SuikodenText.Core.ArchiveReader;

namespace SuikodenText.Core;

public sealed record PlainEntry(string Id, int Block, int Offset, int Length, string Source)
{
    // Runtime metadata only. Translation JSON remains id/source/translation.
    public int[] PointerSlots { get; init; } = [];
    public uint TargetAddress { get; init; }
    public bool InExecutable { get; init; }
    // Nonzero only for inline slots addressed by base + index * stride.
    public int FixedCapacity { get; init; }
}

public static class PlainText
{
    private const long ElfMapping = 0x0165C000;
    private static string Hash(byte[] data, int start, int length) =>
        Convert.ToHexString(SHA256.HashData(data.AsSpan(start, length))).ToLowerInvariant();

    // These profiles are for the supplied US executable, not arbitrary BIN files.
    public static bool SupportsExecutable(byte[] data) => data.Length >= 0x2C36D0 &&
        data[0] == 0x7F && data[1] == (byte)'E' && data[2] == (byte)'L' && data[3] == (byte)'F' &&
        Hash(data, 0x1000, 0x2C26D0) == "711defd5c51fdc75d4ba05047d60e16a653d09235245c6b8ab0141bb940bc8a5";

    private sealed record ModuleProfile(long Base, int CodeStart, int CodeEnd,
        string Signature, int TableStart, int TableEnd, int NestedTable = 0, int ExtraStart = 0, int ExtraEnd = 0);
    private static readonly ModuleProfile[] AdditionalModules =
    [
        // The pointer list continues with the area-name catalogue: Test,
        // Vinay del Zexay, Ancient Highway, Plain of Amur-North, etc.
        new(0x6711C0, 0x234C0, 0x2EFC8, "ef9651c616127af373e18967611426161c1a2eedf77d3b350cd5dbb7301e4008", 0x23240, 0x233A0, ExtraStart: 0x23418, ExtraEnd: 0x2343C),
        new(0x11F19C0, 0x237C0, 0x32CF0, "5a42dfcc7ce83eab1d41efc5379db29ab01f7872fcaa71bbaebe6402e957f1bd", 0x22248, 0x223A8),
        new(0x6711C0, 0x237C0, 0x31D00, "1c9aafdfa0e714df7f006ea4d34f2459b35ed9e2a0bcadaf124ccdcdde4084ea", 0x22200, 0x22360),
        new(0x6711C0, 0x465C0, 0x54900, "fa5cd614bb36035e4c217168f731bd1ddcaf36ac32672d625fd66b9faed9a12e", 0x22278, 0x223D8),
        new(0x9F11C0, 0x5C0, 0x7928, "4e80c0956ec367f458388c4595e38618a4b7ae438feb86329f30e5f22aa65a5c", 0x448, 0x4A4, 0x560),
        new(0x9F11C0, 0x240, 0x4AD0, "8f15d2fcba8da15333f9dd4d3fa3fb5fe3703657e3aad9c60d8803699c3af70c", 0x188, 0x1C8),
        new(0xD711C0, 0x285C0, 0x40BE0, "78c27fa01691ac72c669c723cdd5136e5e14dcd428a1139ec222002f8c604b3f", 0x28, 0x50),
    ];
    private static ModuleProfile? FindModule(byte[] data)
    {
        if (data.Length < 24) return null;
        foreach (var p in AdditionalModules)
            if (data.Length >= p.CodeEnd && U32(data, 4) == p.Base + p.CodeStart &&
                Hash(data, p.CodeStart, p.CodeEnd - p.CodeStart) == p.Signature)
                return p;
        return null;
    }
    public static long? VerifiedOverlayBase(byte[] data)
    {
        var module = FindModule(data);
        if (module is not null) return module.Base;
        if (data.Length < 0x2000) return null;
        long address = (long)U32(data, 4) - 0x240;
        if (U32(data, 0x70) != address + 0x50) return null;
        string signature = Hash(data, 0x240, 0x17C0);
        return (address, signature) switch
        {
            (0x006711C0, "8d4667e283327a2d159339fd3292369048fa4676c1ececa99598b7ba2a10393e") => address,
            (0x009F11C0, "517fc597f47c8371e0e87a40d6eecdf6ec53b811f7715697e37cbfd30a56919c") => address,
            _ => null
        };
    }

    // Module-specific pointer tables; no raw string discovery and no filename restriction.
    // Keyboard selection data and neighboring resource-name tables are not translated.
    public static List<PlainEntry> ReadOverlay(byte[] data, int block, byte[]? executable = null)
    {
        long? address = VerifiedOverlayBase(data);
        if (address is null) return [];
        var module = FindModule(data);
        IEnumerable<int> slots = module is null
            ? new[] { 0x1E8, 0x1EC, 0x1F0, 0x1F8, 0x1FC, 0x200 }
            : Enumerable.Range(0, (module.TableEnd - module.TableStart) / 4)
                .Select(i => module.TableStart + i * 4).Where(p => U32(data, p) != 0);
        if (module?.ExtraEnd > module?.ExtraStart)
            slots = slots.Concat(Enumerable.Range(0, (module.ExtraEnd - module.ExtraStart) / 4)
                .Select(i => module.ExtraStart + i * 4));
        if (module?.NestedTable > 0)
        {
            // Six story tables referenced by the chapter-selection module.
            int[] counts = [9, 9, 9, 6, 3, 2];
            var nested = new List<int>();
            for (int i = 0; i < counts.Length; i++)
            {
                long first = U32(data, module.NestedTable + i * 4) - module.Base;
                Check(first >= 0 && first + counts[i] * 4 <= module.CodeStart,
                    "Invalid chapter story pointer table.");
                for (int j = 0; j < counts[i]; j++) nested.Add((int)first + j * 4);
            }
            slots = slots.Concat(nested);
        }
        return ReadPointers(data, address.Value, block, $"string_{block:D4}_", slots, executable);
    }

    public static List<PlainEntry> ReadExecutable(byte[] data)
    {
        Check(SupportsExecutable(data), "No verified UI text profile exists for this executable.");
        var slots = new List<int>();
        slots.AddRange(MainUiTextSlots(data));
        // LW a1 in the delay slot of JAL SetText. These are actual slot
        // references, not every adjacent word in the .sdata section.
        slots.Add(checked((int)(0x019E65C8 - ElfMapping))); // UneqAll: 0x0169D054
        slots.Add(checked((int)(0x019E65CC - ElfMapping))); // AutoEqip: 0x0169D0F4 (delay slot)
        // Inventory item-condition labels: food freshness and the empty-slot
        // separator, loaded through this .data pointer table.
        slots.Add(checked((int)(0x019E6528 - ElfMapping))); // (Fresh)
        slots.Add(checked((int)(0x019E652C - ElfMapping))); // (Rotten)
        slots.AddRange(InventoryActionTextSlots(data));
        slots.AddRange(FacilityMenuTextSlots(data));
        // Inventory heading: load -> object+0xFC -> SetText.
        // Writers 0x0168BF84/94 and 0x0168BFC4/D4; reader 0x01672CC4/C8.
        slots.Add(checked((int)(0x019E6540 - ElfMapping))); // Event Items
        slots.Add(checked((int)(0x019E6544 - ElfMapping))); // Party Items
        slots.AddRange(GameplayMessageTextSlots(data));
        slots.AddRange(GameplayDatabaseTextSlots(data));
        slots.AddRange(ConsumableItemTextSlots(data));
        slots.AddRange(UniteAttackTextSlots(data));
        // Settings title and options: callers verified in sub_16B52D8.
        foreach (var table in new[] { (0x019E6620, 2), (0x019E6638, 2) })
            for (int i = 0; i < table.Item2; i++)
                slots.Add(checked((int)(table.Item1 - ElfMapping + i * 4)));
        slots.AddRange(ItemDescriptorTextSlots(data));
        slots.AddRange(LocationNameTextSlots(data));
        slots.AddRange(TownDialogueTextSlots(data));
        slots.AddRange(WeaponNameTextSlots(data));
        slots.AddRange(SaveTransferTextSlots(data));
        // GetTextById -> GetScriptTextEntry: fields +0 (short name), +4 (full
        // name), and +8 (biography) per mapped record.
        for (int id = 1; id <= 216; id++)
        {
            int index = data[checked((int)(0x01969888 - ElfMapping)) + id];
            int baseSlot = checked((int)(0x01999368 - ElfMapping)) + index * 16;
            for (int field = 0; field < 3; field++)
            {
                int slot = baseSlot + field * 4;
                if (U32(data, slot) != 0) slots.Add(slot);
            }
        }
        var result = ReadPointers(data, ElfMapping, -1, "exe_", slots);
        result.AddRange(ReadFixedNames(data));
        result.AddRange(ReadUiLabels(data));
        result.AddRange(DirectUiLabels(data));
        return result.OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
    }

    // Shop/menu labels loaded directly by MIPS code; no pointer table holds
    // them.  Verified setter paths: 0x016ACFF8 / 0x016ACA18 store the a1 text
    // pointer into object+0xDC and raise a dirty flag; 0x01736300 stores the
    // Innkeeper pointer into object+0xF4.  Each row pins the lui/addiu code
    // site that loads the label address; import never modifies code bytes, so
    // this profile check also holds for a translated executable.
    private static IEnumerable<PlainEntry> ReadUiLabels(byte[] data)
    {
        (uint Address, uint CodeSite)[] labels =
        [
            (0x019BE268, 0x017362F4), // Innkeeper (shop type)
            (0x019BE318, 0x01737208), // SpplyShop
            (0x019BE368, 0x01737DA0), // No marketable items
            (0x019BE410, 0x0173F7F0), // DfnsVndr
            (0x019BE5A8, 0x01748454), // Blacksmith
            (0x019BE698, 0x0174BA8C), // Appraiser
            (0x019BE8C8, 0x0174DF84), // RuneSage
            (0x019BEFE0, 0x0175EFC0), // Discard what?
            (0x019BEFF0, 0x0175F018), // Bag Full
            (0x019BF118, 0x0176059C), // Trsr Box
            // Treasure appraisal categories: loader 0x016DBD98 selects
            // between the unidentified ('? X') and identified ('X') variants
            // per item by an appraised flag; both sets are player-facing.
            (0x019A9618, 0x016DBDD8), // ? Statue
            (0x019A9630, 0x016DBDF0), // ? Painting
            (0x019A9648, 0x016DBE08), // ? Figurine
            (0x019A9660, 0x016DBE20), // ? Figurine (second category slot)
            (0x019A9678, 0x016DBE88), // Figurine
            (0x019A96B8, 0x016DBEA0), // Painting
            (0x019A96F8, 0x016DBEB8), // Figurine
            (0x019A9738, 0x016DBED0), // Figurine
            // Facility labels via verified setter 0x016ACA18 (object+0xDC,
            // dirty flag) — the same shop-label setter path.
            (0x019E7CB8, 0x01734F20), // Inn
            (0x019E7D28, 0x01753AAC), // EduCtr
            (0x019E7D30, 0x01753AC4), // TrngCtr
            (0x019E7D60, 0x01759984), // Replace (equip menu, setter 0x016ACA18)
            (0x019E7D80, 0x0175D1D8), // Order (equip menu; lui at 0x0175D1D8, addiu at +0x50)
        ];
        foreach (var (address, codeSite) in labels)
        {
            int site = checked((int)(codeSite - ElfMapping));
            uint lui = U32(data, site);
            uint hi = lui & 0xFFFF;
            int rt = (int)((lui >> 16) & 0x1F);
            // The lui half is either the address high word or one more when
            // the addiu immediate sign-extends negative.
            bool paired = lui >> 26 == 0x0F &&
                (hi == address >> 16 || hi == (address >> 16) + 1);
            // The lui site may be followed by unrelated instructions that
            // reuse the same rt register; accept the first window word that
            // forms a real lui+addiu/ori pair for this exact address and
            // stop, instead of letting a later non-pair word clobber the
            // result.
            if (paired)
            {
                paired = false;
                for (int j = 1; j <= 32; j++)
                {
                    uint w = U32(data, site + j * 4);
                    if ((int)((w >> 21) & 0x1F) != rt || (int)((w >> 16) & 0x1F) != rt) continue;
                    int op = (int)(w >> 26);
                    if (op is not (0x09 or 0x0D)) continue;
                    uint imm = w & 0xFFFF;
                    long target = op == 0x0D
                        ? (hi << 16) | imm
                        : (long)(hi << 16) + (imm >= 0x8000 ? (long)imm - 0x10000 : imm);
                    if ((uint)target == address) { paired = true; break; }
                }
            }
            Check(paired, $"UI label code site 0x{codeSite:X8} does not load 0x{address:X8}.");
            var mapped = ResolveExecutable(data, address);
            Check(mapped is not null, "UI label is outside the executable.");
            int start = mapped!.Value.Offset;
            var decoded = DecodeReferenced(data, start, mapped.Value.End);
            Check(decoded.Length > 0, $"UI label at 0x{address:X8} is empty.");
            int capacity = decoded.Length + 1;
            while (start + capacity < mapped.Value.End && data[start + capacity] == 0)
                capacity++;
            yield return new PlainEntry($"exe_ui_{address:X8}", -1, start, decoded.Length,
                decoded.Text) { InExecutable = true, TargetAddress = address,
                    FixedCapacity = capacity };
        }
    }

    // Labels loaded via runtime struct pointers (.bss), not LUI+ADDIU in code.
    // Verified by debugger inspection (FUN_016B2768 etc.) and address presence
    // in the exe file. No static code site exists for these.
    private static IEnumerable<PlainEntry> DirectUiLabels(byte[] data)
    {
        uint[] addresses =
        [
            0x019E6BD0, // Mounted (equip/mount status label)
            // Character class/job labels (contiguous table in .data)
            0x019E6760, 0x019E6768, 0x019E6770, 0x019E6778, 0x019E6788,
            0x019E6790, 0x019E6798, 0x019E67A0, 0x019E67A8, 0x019E67B8,
            0x019E67C0, 0x019E67C8, 0x019E67D0, 0x019E67D8, 0x019E67E0,
            0x019E67E8, 0x019E67F0, 0x019E67F8, 0x019E6800, 0x019E6808,
            0x019E6810, 0x019E6818, 0x019E6820, 0x019E6828, 0x019E6830,
            0x019E6838, 0x019E6848, 0x019E6851, 0x019E6858, 0x019E6860,
            0x019E6878, 0x019E6880, 0x019E6888, 0x019E6898, 0x019E68A0,
            0x019E68A8, 0x019E68B0, 0x019E68C0, 0x019E68D0, 0x019E68F0,
            0x019E6900, 0x019E6918, 0x019E6920, 0x019E6930, 0x019E6940,
            0x019E6948, 0x019E6950, 0x019E6958, 0x019E6960, 0x019E6968,
            0x019E6970, 0x019E6978, 0x019E6980, 0x019E6988,
            // Menu labels
            0x019E6698, 0x019E66B8, 0x019E66C8,
            // Battle state labels
            0x019E7AF8, 0x019E7B00, 0x019E7B08, 0x019E7B10, 0x019E7B18,
            // UI labels
            0x019E81E8, 0x019E8208, 0x019E8620, 0x019E8628, 0x019E8630,
            0x019E8638, 0x019E8640, 0x019E8648, 0x019E8650,
        ];
        foreach (var address in addresses)
        {
            var mapped = ResolveExecutable(data, address);
            Check(mapped is not null, $"Direct UI label 0x{address:X8} is outside the executable.");
            int start = mapped!.Value.Offset;
            var decoded = DecodeReferenced(data, start, mapped.Value.End);
            Check(decoded.Length > 0, $"Direct UI label at 0x{address:X8} is empty.");
            int capacity = decoded.Length + 1;
            while (start + capacity < mapped.Value.End && data[start + capacity] == 0)
                capacity++;
            yield return new PlainEntry($"exe_ui_{address:X8}", -1, start, decoded.Length,
                decoded.Text) { InExecutable = true, TargetAddress = address,
                    FixedCapacity = capacity };
        }
    }

    // Equip-menu facility action labels: 0x019BF020 table loaded by
    // FUN_016ACA18 setter path; three pointers terminated by a zero word.
    private static IEnumerable<int> FacilityMenuTextSlots(byte[] data)
    {
        const uint Table = 0x019BF020;
        for (int i = 0; i < 3; i++)
        {
            int slot = checked((int)(Table - ElfMapping + i * 4));
            Check(slot >= 0 && slot <= data.Length - 4 &&
                ResolveExecutable(data, U32(data, slot)) is not null,
                "Facility menu pointer table does not match the executable.");
            yield return slot;
        }
    }

    // Selection labels: source table -> 0x016B18D8(a1) -> object+0x118+4*i.
    // 0x016B0CA0 reads that field, then 0x016B0CA4 -> 0x016621F8 ->
    // 0x016620B8 -> 0x01661EC8. Counts below follow explicit source loads
    // and index assignments, not adjacent strings or a zero-word heuristic.
    private static IEnumerable<int> InventoryActionTextSlots(byte[] data)
    {
        (uint Address, int Count)[] tables =
        [
            (0x0195FFDC, 5), // Use/Unequip/Equip/Discard/Buy; 0x0168DF68..0x0168E388
            (0x0195FFF4, 2), // Discard/Keep; 0x0168E598, 0x0168E59C
            (0x01960004, 2), // Sell/Keep; 0x0168E618, 0x0168E61C
            (0x0196003C, 2), // Use/Don't; 0x0168E714, 0x0168E718
            (0x0196004C, 4), // Equip/Unequip; 0x0169015C, 0x01690180
        ];
        foreach (var table in tables)
            for (int i = 0; i < table.Count; i++)
            {
                int slot = checked((int)(table.Address - ElfMapping + i * 4));
                Check(slot >= 0 && slot <= data.Length - 4 &&
                    ResolveExecutable(data, U32(data, slot)) is not null,
                    "Inventory action pointer table does not match the executable.");
                yield return slot;
            }
    }

    // GetFixedWidthStringEntry 0x016C7098 calls 0x016C6F88, then
    // returns 0x0199FCE0 + mappedIndex * 20. The ID map ends at ID zero.
    // Export each referenced slot once; IDs are not assumed sequential.
    private static IEnumerable<PlainEntry> ReadFixedNames(byte[] data)
    {
        const int Map = 0x01969968 - (int)ElfMapping;
        const int End = 0x01969AF8 - (int)ElfMapping;
        const uint TextBase = 0x0199FCE0;
        const int Stride = 20, Count = 100;
        var seen = new HashSet<int>();
        for (int slot = Map; slot <= End; slot += 4)
        {
            uint pair = U32(data, slot);
            int id = (int)(pair & 0xFFFF), index = (int)(pair >> 16);
            if (id == 0)
            {
                Check(slot == End, "The fixed-name ID map terminates unexpectedly.");
                yield break;
            }
            Check(slot < End && index < Count, "Invalid fixed-name mapping.");
            if (!seen.Add(index)) continue;
            uint address = TextBase + (uint)(index * Stride);
            var mapped = ResolveExecutable(data, address);
            Check(mapped is not null && mapped.Value.Offset + Stride <= mapped.Value.End,
                "The fixed-name slot is outside loaded ELF data.");
            int start = mapped!.Value.Offset;
            var decoded = DecodeReferenced(data, start, start + Stride);
            yield return new PlainEntry($"exe_fixed_{index:D3}", -1, start,
                decoded.Length, decoded.Text)
            { InExecutable = true, TargetAddress = address, FixedCapacity = Stride };
        }
        throw new InvalidDataException("The fixed-name map has no terminator.");
    }

    // Main ELF UI pointer catalogue.  Every span is a fixed pointer table
    // bounded by its terminating zero field; entries are never found by
    // searching the text sections.  In particular:
    //   0x0195FF68 -> Browse skill points...
    //   0x0195FF6C -> Browse profile.
    //   0x0196002C/30 -> equip/exchange error messages.
    // Runtime evidence: sub_166DA28 reads the selected pointer from UI state
    // and passes it as a1 to sub_1662860, the text-stream parser.
    private static IEnumerable<int> MainUiTextSlots(byte[] data)
    {
        (uint Address, int Count)[] tables =
        [
            (0x0195FF68, 2), (0x0195FF78, 5), (0x0195FF90, 8),
            (0x0195FFB4, 8), (0x01960010, 9), (0x01960060, 7),
            (0x01960080, 5), (0x01960098, 3), (0x019600A8, 4),
            (0x019600B8, 13), (0x019600F0, 3), (0x01960100, 34),
            (0x01960188, 21), (0x019601E0, 7),
        ];
        foreach (var table in tables)
            for (int i = 0; i < table.Count; i++)
            {
                int slot = checked((int)(table.Address - ElfMapping + i * 4));
                Check(slot >= 0 && slot <= data.Length - 4 && U32(data, slot) != 0,
                    "Main UI pointer table does not match the verified executable.");
                yield return slot;
            }
    }

    // Name/description record tables used by the item and skill menus.
    // Both pointer fields are validated per record; no text-region scan is
    // involved.  The fixed counts are the complete verified runs for the
    // supplied US executable.
    private static IEnumerable<int> GameplayDatabaseTextSlots(byte[] data)
    {
        // Every (Address, Count, Stride) row is a verified US-executable table
        // of name+description records.  Both pointer fields (+0 and +4) are
        // validated per record.  No text-region scan is involved.
        (uint Address, int Count, int Stride)[] tables =
        [
            (0x019A0EB0, 33, 0x10),  // Hammers, Seeds, Medal sets, Recipes
            (0x019A0F00, 28, 0x10),  // item name + description (original)
            (0x019A1200, 45, 0x10),  // Books, Scripts, Kennels, Bath items
            (0x019A1A14, 123, 0x24), // Food dishes
            (0x019A4AA8, 80, 0x20),  // Battle spells & weapon runes
            (0x019A5F18, 49, 0x20),  // Runes
            (0x019A6E78, 23, 0x20),  // Passive runes
            (0x019A9058, 43, 0x1C),  // Battle skills (complete 43)
            (0x019A9778, 52, 0x14),  // Antiques, Art, Figurines
        ];
        foreach (var table in tables)
            for (int row = 0; row < table.Count; row++)
                for (int field = 0; field < 2; field++)
                {
                    int slot = checked((int)(table.Address - ElfMapping)) + row * table.Stride + field * 4;
                    Check(slot >= 0 && slot <= data.Length - 4 && ResolveExecutable(data, U32(data, slot)) is not null,
                        "Gameplay text table does not match the verified executable.");
                    yield return slot;
                }
    }

    // 37 consumable/scroll records at stride 36, 0x019A14E0..0x019A1A14.
    // Two text pointers per record: name (+0) and description (+4).
    private static IEnumerable<int> ConsumableItemTextSlots(byte[] data)
    {
        const uint Table = 0x019A14E0;
        const int Count = 37, Stride = 36;
        for (int row = 0; row < Count; row++)
            for (int field = 0; field < 2; field++)
            {
                int slot = checked((int)(Table - ElfMapping)) + row * Stride + field * 4;
                Check(slot >= 0 && slot <= data.Length - 4 && ResolveExecutable(data, U32(data, slot)) is not null,
                    "Consumable item table does not match the verified executable.");
                yield return slot;
            }
    }

    // Unite attacks: two 2-slot header tables + 39 body records (stride 40).
    private static IEnumerable<int> UniteAttackTextSlots(byte[] data)
    {
        (uint Address, int Count)[] headers =
        [
            (0x019A5728, 2),
            (0x019A5748, 2),
        ];
        foreach (var header in headers)
            for (int i = 0; i < header.Count; i++)
            {
                int slot = checked((int)(header.Address - ElfMapping + i * 4));
                Check(slot >= 0 && slot <= data.Length - 4 && ResolveExecutable(data, U32(data, slot)) is not null,
                    "Unite attack header table does not match the verified executable.");
                yield return slot;
            }
        const uint Body = 0x019A5770;
        const int BodyCount = 39, BodyStride = 40;
        for (int row = 0; row < BodyCount; row++)
            for (int field = 0; field < 2; field++)
            {
                int slot = checked((int)(Body - ElfMapping)) + row * BodyStride + field * 4;
                Check(slot >= 0 && slot <= data.Length - 4 && ResolveExecutable(data, U32(data, slot)) is not null,
                    "Unite attack body table does not match the verified executable.");
                yield return slot;
            }
    }

    // 36 location names loaded by the world-map / area-selection UI.
    // Terminated by a zero word at +36.
    private static IEnumerable<int> LocationNameTextSlots(byte[] data)
    {
        const uint Table = 0x01979B90;
        const int Count = 36;
        for (int i = 0; i < Count; i++)
        {
            int slot = checked((int)(Table - ElfMapping + i * 4));
            Check(slot >= 0 && slot <= data.Length - 4 && U32(data, slot) != 0,
                "Location name table does not match the verified executable.");
            yield return slot;
        }
        int terminator = checked((int)(Table - ElfMapping + Count * 4));
        Check(U32(data, terminator) == 0, "Location name table has no verified terminator.");
    }

    // Town, shop, inn, and tutor dialogue pointers in 0x01980648..0x01981544.
    // This region mixes text pointers with numeric data.  Original text lives
    // in .rodata (0x019B4F80..0x019E4522) or in the shop/menu facility label
    // blob at 0x019E7CB8..0x019E7F60 (.data; 229 slot references verified
    // 2026-10-04: retail equip/town menu labels Order, Sell, Okay?, ...).
    // A previous import may have redirected some slots into allocator regions
    // (zero gaps between sections, or an additional PT_LOAD segment).  Those
    // regions never hold original data, so a printable target there is a
    // verified relocation.
    private static IEnumerable<int> TownDialogueTextSlots(byte[] data)
    {
        const uint Start = 0x01980648, End = 0x01981544;
        const uint RodataLow = 0x019B4F80, RodataHigh = 0x019E4522;
        const uint BlobLow = 0x019E7CB8, BlobHigh = 0x019E7F60;
        var allocatable = AllocatorFileRanges(data);
        for (uint addr = Start; addr < End; addr += 4)
        {
            int slot = checked((int)(addr - ElfMapping));
            uint target = U32(data, slot);
            var mapped = ResolveExecutable(data, target);
            if (mapped is null) continue;
            bool original = target >= RodataLow && target < RodataHigh
                || target >= BlobLow && target < BlobHigh;
            if (!original && !allocatable.Any(r => r.Offset <= mapped.Value.Offset && mapped.Value.Offset < r.End))
                continue;
            if (data[mapped.Value.Offset] < 32 || data[mapped.Value.Offset] > 126) continue;
            yield return slot;
        }
    }

    // File ranges the ELF text allocator may write into: zero gaps between
    // section extents inside the primary PT_LOAD, plus any additional
    // PT_LOAD segments.  Mirrors the allocator region scan without
    // the reference check; original data can never sit in a zero gap.
    private static List<(int Offset, int End)> AllocatorFileRanges(byte[] data)
    {
        var ranges = new List<(int, int)>();
        int table = checked((int)U32(data, 28));
        int size = data[42] | data[43] << 8, count = data[44] | data[45] << 8;
        Check(size >= 32 && table >= 0 && (long)table + (long)size * count <= data.Length,
            "Invalid ELF load table.");
        int primaryLow = 0, primaryHigh = 0; bool primary = true;
        for (int i = 0; i < count; i++)
        {
            int h = table + i * size;
            if (U32(data, h) != 1) continue;
            int file = checked((int)U32(data, h + 4));
            int length = checked((int)U32(data, h + 16));
            Check(file + length <= data.Length, "ELF load segment exceeds the file.");
            if (primary) { primaryLow = file; primaryHigh = file + length; primary = false; }
            else ranges.Add((file, file + length));
        }
        if (primary) return ranges;
        int shoff = checked((int)U32(data, 32));
        int shsize = data[46] | data[47] << 8, shcount = data[48] | data[49] << 8;
        if (shsize < 40 || (long)shoff + (long)shsize * shcount > data.Length) return ranges;
        var used = new List<(int, int)>();
        for (int i = 0; i < shcount; i++)
        {
            int h = shoff + i * shsize;
            if (U32(data, h + 4) == 8) continue; // SHT_NOBITS has no file storage.
            int s = checked((int)U32(data, h + 16));
            int l = checked((int)U32(data, h + 20));
            if (l > 0 && s < primaryHigh && (long)s + l > primaryLow)
                used.Add((Math.Max(s, primaryLow), Math.Min(checked(s + l), primaryHigh)));
        }
        int cursor = primaryLow;
        foreach (var r in used.OrderBy(r => r.Item1))
        {
            int start = Align(cursor, 4), end = r.Item1;
            // A gap between section extents is an allocator region even if a
            // previous import already wrote relocated text into it.
            if (end > start) ranges.Add((start, end));
            cursor = Math.Max(cursor, r.Item2);
        }
        return ranges;
    }

    // 71 weapon-forge name records at stride 16.  Fields +0, +4, +8 are the
    // three upgrade-level names.  Null pointers (e.g. row 51) are skipped
    // silently; ReadPointers drops zero-start entries.
    private static IEnumerable<int> WeaponNameTextSlots(byte[] data)
    {
        const uint Table = 0x01998A98;
        const int Count = 71, Stride = 16;
        for (int row = 0; row < Count; row++)
            for (int field = 0; field < 3; field++)
            {
                int slot = checked((int)(Table - ElfMapping)) + row * Stride + field * 4;
                uint target = U32(data, slot);
                if (target == 0) continue;
                Check(slot >= 0 && slot <= data.Length - 4 && ResolveExecutable(data, target) is not null,
                    "Weapon name table does not match the verified executable.");
                yield return slot;
            }
    }

    // 8 save-transfer character names from Suikoden I & II.
    private static IEnumerable<int> SaveTransferTextSlots(byte[] data)
    {
        const uint Table = 0x0199F1D0;
        const int Count = 8;
        for (int i = 0; i < Count; i++)
        {
            int slot = checked((int)(Table - ElfMapping + i * 4));
            Check(slot >= 0 && slot <= data.Length - 4 && ResolveExecutable(data, U32(data, slot)) is not null,
                "Save transfer name table does not match the verified executable.");
            yield return slot;
        }
    }

    // 0x01983030 is the bounded gameplay-message pointer list.  The game
    // reads these pointers for acquisition, party, lottery, mirror and map
    // prompts; e.g. +4 is "You take ".  It ends at the zero word at
    // 0x01983110, so this is a table traversal, not a search of .rodata.
    private static IEnumerable<int> GameplayMessageTextSlots(byte[] data)
    {
        const uint Table = 0x01983030;
        const int Count = 56;
        for (int i = 0; i < Count; i++)
        {
            int slot = checked((int)(Table - ElfMapping + i * 4));
            Check(slot >= 0 && slot <= data.Length - 4 && U32(data, slot) != 0,
                "Gameplay message table does not match the verified executable.");
            yield return slot;
        }
        int terminator = checked((int)(Table - ElfMapping + Count * 4));
        Check(U32(data, terminator) == 0, "Gameplay message table has no verified terminator.");
    }

    // 156 × 0x44-byte item/menu descriptors.  The UI constructor copies these
    // two source pointers into the runtime object; 0x0169CC68 later reads its
    // text field (+0x100) and passes it to sub_1661F50.  This is therefore a
    // descriptor reference table, not a discovered string range.
    private static IEnumerable<int> ItemDescriptorTextSlots(byte[] data)
    {
        const uint Table = 0x01993948;
        const int Count = 156, Stride = 0x44;
        var slots = new List<int>(Count * 2);
        for (int row = 0; row < Count; row++)
        {
            int start = checked((int)(Table - ElfMapping)) + row * Stride;
            Check(start >= 0 && start + Stride <= data.Length, "Item descriptor table exceeds ELF.");
            for (int field = 0; field < 2; field++)
            {
                int slot = start + field * 4;
                uint target = U32(data, slot);
                // Each pointer must remain a file-backed string in this known
                // US profile.  This rejects a changed/nonmatching descriptor
                // layout before any JSON is exported.
                Check(ResolveExecutable(data, target) is not null,
                    "Item descriptor text pointer is outside the executable.");
                slots.Add(slot);
            }
        }
        return slots;
    }


    private static List<PlainEntry> ReadPointers(byte[] data, long address, int block,
        string prefix, IEnumerable<int> slots, byte[]? executable = null)
    {
        var result = new List<PlainEntry>();
        // One identity per reference slot, even when several slots share a target.
        foreach (int slot in slots.Distinct().OrderBy(p => p))
        {
            Check(slot >= 0 && slot <= data.Length - 4, "Invalid text pointer slot.");
            uint target = U32(data, slot);
            long relative = target - address;
            byte[] storage = data;
            bool inExecutable = block < 0;
            int limit = data.Length;
            if (block < 0)
            {
                // ELF pointers must always follow program headers, even if
                // target - the original base happens to fit inside the file.
                var mapped = ResolveExecutable(data, target);
                Check(mapped is not null, $"ELF slot 0x{slot:X8} targets unloaded memory 0x{target:X8}.");
                relative = mapped!.Value.Offset; limit = mapped.Value.End;
            }
            else if (relative < 0 || relative >= data.Length)
            {
                Check(executable is not null, "Referenced text requires its paired executable.");
                var mapped = ResolveExecutable(executable!, target);
                Check(mapped is not null, "Text pointer is outside file-backed executable memory.");
                relative = mapped!.Value.Offset; limit = mapped.Value.End;
                storage = executable!; inExecutable = true;
            }
            int start = checked((int)relative);
            // A pointer-table field can also represent an empty label or a
            // non-text resource.  Do not turn an unterminated/fill buffer
            // into JSON merely because it is addressable in a PT_LOAD.
            if (storage[start] == 0) continue;
            Check(TryDecodeReferenced(storage, start, limit, out var decoded),
                $"{prefix}ptr_{slot:X8}: suspicious or unterminated stream at 0x{target:X8}; export stopped instead of silently dropping text.");
            result.Add(new(prefix + "ptr_" + slot.ToString("X8"), block, start, decoded.Length,
                decoded.Text) { PointerSlots = [slot], TargetAddress = target,
                    InExecutable = inExecutable });
        }
        return result;
    }

    internal static (int Offset, int End)? ResolveExecutable(byte[] data, uint target)
    {
        Check(data.Length >= 52 && data[0] == 127 && data[1] == 69 && data[2] == 76 &&
            data[3] == 70 && data[4] == 1 && data[5] == 1, "Invalid paired ELF header.");
        int table = checked((int)U32(data, 28));
        int size = data[42] | data[43] << 8, count = data[44] | data[45] << 8;
        Check(size >= 32 && table >= 0 && (long)table + (long)size * count <= data.Length,
            "Invalid ELF load table.");
        (int Offset, int End)? result = null;
        for (int i = 0; i < count; i++)
        {
            int h = table + i * size;
            if (U32(data, h) != 1) continue;
            long file = U32(data, h + 4), ram = U32(data, h + 8), length = U32(data, h + 16);
            Check(file + length <= data.Length, "ELF load segment exceeds the file.");
            if (target < ram || target >= ram + length) continue;
            Check(result is null, "Ambiguous executable text mapping.");
            result = (checked((int)(file + target - ram)), checked((int)(file + length)));
        }
        return result;
    }
    private static (string Text, int Length) DecodeReferenced(byte[] data, int start, int limit)
    {
        var text = new StringBuilder();
        int cursor = start;
        while (cursor < limit)
        {
            byte c = data[cursor];
            if (c == 0) return (text.ToString(), cursor - start);
            // The menu stream has two-byte control units, including zero parameters.
            int length = c is 1 or 2 or 4 or 5 or 6 or 8 or 9 or 11 || c >= 0x80 ? 2 : 1;
            Check(cursor + length <= limit, "Truncated referenced text unit.");
            if (length == 1 && (c is >= 32 and <= 126 && c != 64 && c != 123 || c is 10 or 13))
                text.Append((char)c);
            else
                text.Append("{RAW:").Append(Convert.ToHexString(data.AsSpan(cursor, length))).Append('}');
            cursor += length;
        }
        throw new InvalidDataException("Unterminated referenced menu text.");
    }

    private static bool TryDecodeReferenced(byte[] data, int start, int limit,
        out (string Text, int Length) decoded)
    {
        decoded = default;
        // UI messages in the verified catalogues are short.  A hard upper
        // bound prevents a stale redirected pointer from consuming a whole
        // zero-free data region (the reported thousands of 'u' characters).
        const int MaxTextBytes = 0x800;
        if (start < 0 || start >= limit) return false;
        if (limit - start > MaxTextBytes)
            limit = Math.Min(data.Length, start + MaxTextBytes);
        try { decoded = DecodeReferenced(data, start, limit); }
        catch (InvalidDataException) { return false; }
        if (decoded.Length == 0) return false;

        // Repeated fill is not a display message.  This only runs after a
        // known pointer slot was followed; it does not discover strings.
        string visible = Regex.Replace(decoded.Text, @"\{RAW:[0-9A-F]+\}", "");
        if (visible.Length >= 64)
        {
            int mostCommon = visible.GroupBy(c => c).Max(g => g.Count());
            if (mostCommon * 100 >= visible.Length * 95) return false;
        }
        return true;
    }
    public static List<TranslationEntry> Export(IEnumerable<PlainEntry> entries) =>
        entries.Select(e => new TranslationEntry { Id = e.Id, Source = e.Source, Translation = e.Source }).ToList();

    public static Dictionary<string, TranslationEntry> Validate(List<PlainEntry> originals, List<TranslationEntry>? incoming)
    {
        if (incoming is null) return new(StringComparer.Ordinal); // Legacy JSON.
        Check(incoming.All(e => e is not null && !string.IsNullOrEmpty(e.Id)), "Invalid plain-text entries.");
        Check(incoming.Count == originals.Count && incoming.Select(e => e.Id).Distinct().Count() == originals.Count,
            "Plain-text IDs are missing or duplicated.");
        var rows = incoming.ToDictionary(e => e.Id, StringComparer.Ordinal);
        foreach (var entry in originals)
        {
            Check(rows.TryGetValue(entry.Id, out var row) && row.Source == entry.Source,
                "Plain-text ID or source was modified: " + entry.Id);
            Check(row!.Translation is not null, "translation must be a string: " + entry.Id);
            if (row.Translation != entry.Source) TryEncode(row.Translation!, entry);
        }
        return rows;
    }
    internal static byte[] Encode(string text, PlainEntry entry)
    {
        var bytes = EncodeCore(text, entry);
        Check(entry.FixedCapacity == 0 || bytes.Count < entry.FixedCapacity,
            entry.Id + $": fixed {entry.FixedCapacity}-byte runtime slot (including NUL); relocation requires a verified getter patch.");
        return bytes.ToArray();
    }
    internal static byte[] TryEncode(string text, PlainEntry entry)
    {
        return EncodeCore(text, entry).ToArray();
    }
    private static List<byte> EncodeCore(string text, PlainEntry entry)
    {
        const string rawPattern = @"\{RAW:([0-9A-F]+)\}";
        Check(Regex.Matches(text, rawPattern).Select(m => m.Value).SequenceEqual(
            Regex.Matches(entry.Source, rawPattern).Select(m => m.Value)),
            entry.Id + ": RAW tokens must be preserved in their original order.");
        var bytes = new List<byte>();
        int cursor = 0;
        foreach (Match token in Regex.Matches(text, rawPattern))
        {
            Append(text[cursor..token.Index]);
            bytes.AddRange(Convert.FromHexString(token.Groups[1].Value));
            cursor = token.Index + token.Length;
        }
        Append(text[cursor..]);
        void Append(string part)
        {
            Check(part.All(c => c is >= ' ' and <= '~' && c != '{' && c != '@' || c is '\n' or '\r'),
                entry.Id + ": use ASCII text and preserve RAW tokens for special bytes.");
            bytes.AddRange(Encoding.ASCII.GetBytes(part));
        }
        // A space after '%' is normally prose ("10% chance"), not printf.
        // Keep the actual flags used by this game, including zero padding.
        const string format = @"%(?:%|[-+#0]*[0-9]*(?:\.[0-9]+)?[hlL]*[diuoxXfFeEgGaAcspn])";
        Check(Regex.Matches(text, format).Select(m => m.Value).SequenceEqual(
            Regex.Matches(entry.Source, format).Select(m => m.Value)),
            entry.Id + ": printf format tokens must be preserved.");
        return bytes;
    }
}
