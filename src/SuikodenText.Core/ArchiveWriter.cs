using System.Buffers.Binary;
using System.Security.Cryptography;
using static SuikodenText.Core.Files;
using static SuikodenText.Core.ArchiveReader;

namespace SuikodenText.Core;

public static class ArchiveWriter
{
    private static Dictionary<string, TranslationEntry> Validate(ArchiveState state, TranslationDocument doc)
    {
        Check(doc.Blocks is not null && doc.Blocks.All(b => b is not null), "blocks must be a list of blocks.");
        var originals = state.Blocks.Where(b => b.Entries.Count > 0).ToList();
        Check(doc.Blocks!.Count == originals.Count && doc.Blocks.Select(b => b.Index).Distinct().Count() == originals.Count,
            "Block IDs are missing, duplicated, or modified.");
        var blocks = doc.Blocks.ToDictionary(b => b.Index);
        Dictionary<string, TranslationEntry> rows = new(StringComparer.Ordinal);
        foreach (var b in originals)
        {
            Check(blocks.TryGetValue(b.Record.Index, out var incoming), "A block ID was modified.");
            Check(incoming!.Entries is not null && incoming.Entries.All(e => e is not null && !string.IsNullOrEmpty(e.Id)), "Invalid entries.");
            Check(incoming.Entries!.Count == b.Entries.Count && incoming.Entries.Select(e => e.Id).Distinct(StringComparer.Ordinal).Count() == b.Entries.Count,
                "Text IDs are missing or duplicated.");
            var lookup = incoming.Entries.ToDictionary(e => e.Id, StringComparer.Ordinal);
            foreach (var e in b.Entries)
            {
                Check(lookup.TryGetValue(e.Id, out var row), "A text ID was modified: " + e.Id);
                Check(row!.Source == e.Source, e.Id + ": source text was modified.");
                TextCodec.Encode(row.Translation, e.Source);
                Check(rows.TryAdd(e.Id, row), "Duplicate text ID across blocks.");
            }
        }
        return rows;
    }
    public static (byte[] Data, BlockReport Report) PatchBlock(byte[] original, DialogueBlock block,
        IReadOnlyDictionary<string, TranslationEntry> rows)
    {
        // Allocate only the verified memory region, never grow past the runtime slot.
        byte[] buffer = new byte[block.Capacity];
        original.CopyTo(buffer, 0); int length = original.Length, changed = 0;
        List<Relocation> moves = [];
        var byOffset = block.Entries.ToDictionary(x => x.Offset);
        var finalOffset = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var e in block.Entries)
        {
            string text = rows[e.Id].Translation;
            if (text == e.Source) continue;
            byte[] encoded = TextCodec.Encode(text, e.Source);
            int oldLength = e.End - e.Offset;
            if (encoded.Length <= oldLength)
            {
                Array.Clear(buffer, e.Offset, oldLength);
                encoded.CopyTo(buffer, e.Offset);
                finalOffset[e.Id] = e.Offset;
            }
            else
            {
                int target = Align(length, 8);
                Check((long)target + encoded.Length <= block.Capacity,
                    $"block_{block.Record.Index:D4}: exceeds the RAM capacity of {block.Capacity} bytes.");
                encoded.CopyTo(buffer, target); length = target + encoded.Length;
                finalOffset[e.Id] = target;
                moves.Add(new(e.Id, e.Offset, target, e.PointerSlots));
            }
            changed++;
        }
        // An in-place edit must not clobber a neighbour that stays put; relocated
        // entries live past the original data and never overlap it.
        foreach (var e in block.Entries)
        {
            if (!finalOffset.TryGetValue(e.Id, out int here) || here != e.Offset) continue;
            foreach (var o in block.Entries)
            {
                if (o.Id == e.Id) continue;
                if (o.Offset >= e.End || e.Offset >= o.End) continue;
                bool stays = !finalOffset.TryGetValue(o.Id, out int there) || there == o.Offset;
                if (!stays) continue;
                bool clobbered = !buffer.AsSpan(o.Offset, o.End - o.Offset)
                    .SequenceEqual(original.AsSpan(o.Offset, o.End - o.Offset));
                Check(!clobbered, e.Id + ": ambiguous interior references; editing is not supported.");
            }
        }
        // Every pointer slot follows its own entry to its final copy.
        foreach (var e in block.Entries)
        {
            int where = finalOffset.TryGetValue(e.Id, out int at) ? at : e.Offset;
            foreach (int slot in e.PointerSlots)
            {
                Check(U32(original, slot) == block.Base + e.Offset, "A source pointer was modified.");
                Put(buffer, slot, checked((uint)(block.Base + where)));
            }
        }
        // Interior slots are pointers into another entry's old bytes. Each must be a
        // known entry start; redirect it to that entry's final copy so shared-tail
        // strings stay readable after either side moves.
        foreach (var e in block.Entries)
        {
            if (rows[e.Id].Translation == e.Source) continue;
            foreach (int slot in e.InteriorSlots)
            {
                long t = (long)U32(original, slot) - block.Base;
                if (t <= e.Offset || t >= e.End || !byOffset.TryGetValue((int)t, out var child) || child is null)
                    throw new InvalidDataException(e.Id + ": ambiguous interior references; editing is not supported.");
                int childAt = finalOffset.TryGetValue(child.Id, out int cw) ? cw : child.Offset;
                Put(buffer, slot, checked((uint)(block.Base + childAt)));
            }
        }
        int logical = length > original.Length ? length : block.LogicalSize;
        int padded = Align(length);
        Check(padded <= block.Capacity, "The block exceeds its RAM capacity.");
        byte[] result = buffer.AsSpan(0, padded).ToArray();
        foreach (var e in block.Entries)
        {
            var targets = e.PointerSlots.Select(slot => (long)U32(result, slot) - block.Base).Distinct().ToList();
            Check(targets.Count == 1 && targets[0] >= 0 && targets[0] < result.Length, e.Id + ": invalid pointer.");
            var read = TextCodec.Read(result, (int)targets[0]);
            Check(read is not null && read.Value.Text == rows[e.Id].Translation, e.Id + ": text verification failed.");
        }
        return (result, new(block.Record.Index, changed, moves, logical, padded, block.Base, block.Capacity));
    }
    public static BuildReport Import(ArchiveState state, TranslationDocument doc, string output, Func<string, bool>? overwrite = null)
    {
        var rows = Validate(state, doc);
        var stringRows = PlainText.Validate(state.Strings, doc.Strings);
        var exeRows = PlainText.Validate(state.Executable, doc.Executable);
        Check(!File.Exists(output), "The output path is a file; select a folder.");
        Dictionary<int, byte[]> patched = [];
        List<BlockReport> reports = [];
        foreach (var b in state.Blocks.Where(b => b.Entries.Count > 0))
        {
            var (data, report) = PatchBlock(state.ReadBlock(b.Record), b, rows);
            patched[b.Record.Index] = data; reports.Add(report);
        }
        int plainChanged = 0, plainRelocated = 0;
        byte[] index = (byte[])state.Index.Clone();
        var allPlain = state.Strings.ToArray();
        string Requested(PlainEntry e)
        {
            return stringRows.TryGetValue(e.Id, out var row) ? row.Translation : e.Source;
        }
        bool SameStorage(PlainEntry a, PlainEntry b) => a.InExecutable == b.InExecutable &&
            (a.InExecutable || a.Block == b.Block) && a.Offset == b.Offset;
        int PatchPlain(ref byte[] data, List<PlainEntry> entries, Dictionary<string, TranslationEntry> translations)
        {
            int changed = 0;
            OverlayTextAllocation? allocation = null;
            foreach (var entry in entries)
            {
                string text = translations.TryGetValue(entry.Id, out var row) ? row.Translation : entry.Source;
                if (text == entry.Source && !entry.InExecutable) continue;
                byte[] bytes = PlainText.Encode(text, entry);
                uint target = entry.TargetAddress;
                Check(entry.PointerSlots.Length > 0, entry.Id + ": no pointer slots.");
                foreach (int slot in entry.PointerSlots)
                    Check(U32(data, slot) == target, entry.Id + ": the source pointer changed.");
                // Check references in the owning module, excluding only text actually stored there.
                var localText = entries.Where(e => e.InExecutable == (entry.Block < 0)).ToList();
                bool hasInteriorReferences =
                    TextRelocation.References(data, target + 1, localText, entry.Length).Count > 0;
                var knownSlots = entries.Where(e => e.TargetAddress == target)
                    .SelectMany(e => e.PointerSlots).ToHashSet();
                bool exclusive = TextRelocation.References(data, target, localText)
                    .All(r => r.High < 0 && knownSlots.Contains(r.Low));
                byte[] storage = data;
                var aliases = allPlain.Where(e => SameStorage(e, entry)).ToArray();
                bool sharedEdit = aliases.All(e => Requested(e) == text);
                if (!entry.InExecutable && bytes.Length <= entry.Length && sharedEdit && exclusive && !hasInteriorReferences)
                {
                    storage.AsSpan(entry.Offset, entry.Length + 1).Clear();
                    bytes.CopyTo(storage, entry.Offset);
                    changed++;
                    continue;
                }
                // Copy-on-write: redirect only this verified reference slot.
                // Keep old bytes for other direct/interior references, including uncatalogued users.
                allocation ??= new OverlayTextAllocation(state, index, data, entry.Block);
                uint address = allocation.Allocate(ref data, bytes);
                foreach (int slot in entry.PointerSlots) Put(data, slot, address);
                int offset = checked((int)(address - allocation.Base));
                Check(data.AsSpan(offset, bytes.Length).SequenceEqual(bytes) && data[offset + bytes.Length] == 0,
                    "ETC text verification failed: " + entry.Id);
                plainRelocated++; changed++;
            }
            return changed;
        }
        foreach (var group in state.Strings.GroupBy(e => e.Block))
        {
            if (!group.Any(e => e.InExecutable || (stringRows.TryGetValue(e.Id, out var row) && row.Translation != e.Source))) continue;
            byte[] data = patched.GetValueOrDefault(group.Key) ?? state.ReadBlock(state.Records[group.Key]);
            var entries = group.ToList();
            plainChanged += PatchPlain(ref data, entries, stringRows);
            var reread = PlainText.ReadOverlay(data, group.Key, index);
            Check(reread.Count == entries.Count, "Menu entry count changed after import.");
            foreach (var entry in reread)
                Check(!entry.InExecutable && entry.Source == Requested(entries.Single(e => e.Id == entry.Id)),
                    entry.Id + ": menu pointer/text verification failed.");
            patched[group.Key] = data;
        }
        // ELF UI: write in-place only. The PS2 maps the original PT_LOAD image;
        // a pointer past the loaded file reads unmapped memory and crashes.
        // A slot's writable span runs from the string start through its NUL and
        // any following zero padding (capped at 32 bytes, bounded by the next
        // entry or the PT_LOAD segment end). Translations that do not fit keep
        // the source bytes (Kept).
        int exeKept = 0;
        // Next-entry offsets for bounding the writable span.
        var exeOffsets = state.Executable.Select(e => e.Offset).Distinct().OrderBy(x => x).ToArray();
        var nextOffset = new Dictionary<int, int>();
        for (int k = 0; k < exeOffsets.Length; k++)
            nextOffset[exeOffsets[k]] = k + 1 < exeOffsets.Length ? exeOffsets[k + 1] : int.MaxValue;
        // PT_LOAD segment file end (only meaningful for exe-indexed archives).
        int segFileEnd = int.MaxValue;
        if (state.IndexKind == "exe")
        {
            int phoff = checked((int)U32(index, 28));
            int phentsize = BinaryPrimitives.ReadUInt16LittleEndian(index.AsSpan(42, 2));
            int phnum = BinaryPrimitives.ReadUInt16LittleEndian(index.AsSpan(44, 2));
            for (int k = 0; k < phnum; k++)
            {
                int h = phoff + k * phentsize;
                if (U32(index, h) != 1) continue;
                segFileEnd = Math.Min(segFileEnd,
                    checked((int)U32(index, h + 4)) + checked((int)U32(index, h + 16)));
            }
        }
        foreach (var entry in state.Executable)
        {
            if (!exeRows.TryGetValue(entry.Id, out var row) || row.Translation == entry.Source) continue;
            byte[] text = PlainText.Encode(row.Translation, entry);
            if (entry.FixedCapacity > 0)
            {
                if (!entry.InExecutable || text.Length >= entry.FixedCapacity) { exeKept++; continue; }
                index.AsSpan(entry.Offset, entry.FixedCapacity).Clear();
                text.CopyTo(index, entry.Offset);
                plainChanged++;
                continue;
            }
            Check(entry.InExecutable && entry.PointerSlots.Length > 0,
                entry.Id + ": invalid executable text reference.");
            foreach (int slot in entry.PointerSlots)
                Check(U32(index, slot) == entry.TargetAddress, entry.Id + ": source pointer changed.");
            var aliases = state.Executable.Where(e => e.Offset == entry.Offset).ToArray();
            bool sameTextForAliases = aliases.All(e =>
                !exeRows.TryGetValue(e.Id, out var alias) || alias.Translation == row.Translation);
            // Writable span: [entry.Offset, entry.Offset + entry.Length + 1 + zeroRun)
            // where zeroRun is zero bytes after the NUL, bounded by next entry,
            // segment end, and 32 bytes.
            int bound = Math.Min(nextOffset[entry.Offset], segFileEnd);
            int avail = Math.Min(entry.Length + 1, bound - entry.Offset);
            while (entry.Offset + avail < bound && avail - entry.Length <= 32 && index[entry.Offset + avail] == 0)
                avail++;
            if (sameTextForAliases && text.Length < avail)
            {
                index.AsSpan(entry.Offset, avail).Clear();
                text.CopyTo(index, entry.Offset);
                plainChanged++;
            }
            else exeKept++;
        }
        long sector = 0;
        foreach (var r in state.Records)
        {
            int count = (patched.TryGetValue(r.Index, out var bytes) ? bytes.Length : r.Size) / Sector;
            Check(count is > 0 and <= 0xFFF && sector is >= 0 and <= 0xFFFFF, "The descriptor exceeds its bitfield capacity.");
            Put(index, r.DescriptorOffset, ((uint)count << 20) | (uint)sector); sector += count;
        }
        var byBlock = reports.ToDictionary(r => r.Index);
        foreach (var b in state.Blocks)
            if (byBlock.TryGetValue(b.Record.Index, out var report))
                foreach (int field in b.SizeFields) Put(index, field, (uint)report.LogicalSize);
        var newRecords = Descriptors(index, state.Table, sector * Sector);
        Check(newRecords.Count == state.Records.Count, "The block count changed.");
        string archiveName = Path.GetFileName(state.ArchivePath).ToUpperInvariant();
        Check(!new[] {"FSECT.BIN", state.IndexName.ToUpperInvariant()}.Contains(archiveName), "Output filenames conflict.");
        string temp = Staging(output);
        try
        {
            string built = Path.Combine(temp, archiveName);
            using (var f = File.Create(built))
                foreach (var r in state.Records) f.Write(patched.GetValueOrDefault(r.Index) ?? state.ReadBlock(r));
            using (var f = File.OpenRead(built))
            {
                foreach (var r in state.Records)
                {
                    byte[] expected = patched.GetValueOrDefault(r.Index) ?? state.ReadBlock(r);
                    Check(newRecords[r.Index].Size == expected.Length && newRecords[r.Index].Offset == f.Position, "The output descriptor does not match.");
                    byte[] actual = new byte[expected.Length]; f.ReadExactly(actual);
                    Check(actual.AsSpan().SequenceEqual(expected), "Written archive verification failed.");
                }
                Check(f.Position == f.Length, "The output archive contains unexpected trailing data.");
            }
            if (state.IndexKind == "fsect") File.WriteAllBytes(Path.Combine(temp, "FSECT.BIN"), index);
            if (state.IndexKind == "exe") File.WriteAllBytes(Path.Combine(temp, state.IndexName), index);
            var result = new BuildReport(archiveName, state.IndexKind, reports.Sum(r => r.Changed) + plainChanged, reports.Sum(r => r.Relocated.Count) + plainRelocated,
                reports, exeKept);
            Publish(temp, output, overwrite);
            return result;
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }

}
