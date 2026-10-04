using SuikodenText.Core;
using System.Text.Json;
using static SuikodenText.Core.Files;
using static SuikodenText.Core.ArchiveReader;

string temp = Path.Combine(Path.GetTempPath(), "s3-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
int assertions = 0;
void Assert(bool condition, string name) { Check(condition, "TEST FAILED: " + name); assertions++; }
void Reject(Action action, string name)
{
    try { action(); } catch (Exception e) when (e is InvalidDataException or JsonException or IOException) { assertions++; return; }
    throw new Exception("TEST FAILED: accepted " + name);
}
TranslationDocument Clone(TranslationDocument d) => JsonSerializer.Deserialize<TranslationDocument>(JsonSerializer.Serialize(d, JsonOptions), JsonOptions)!;
try
{
    string named = "{CTRL:1140}Zexen Knight{CTRL:0040}\n\"Yes, sir!\"";
    byte[] bytes = TextCodec.Encode(named, named);
    Assert(TextCodec.Read(bytes, 0)?.Text == named, "speaker name + body");
    Reject(() => TextCodec.Encode(named.Replace("{CTRL:0040}", ""), named), "control removed");
    Reject(() => TextCodec.Encode("emoji 😀", "text"), "non-ASCII");
    Reject(() => TextCodec.Encode("{CTRL:zz}", "text"), "malformed control");
    Reject(() => TextCodec.Encode(new string('x', 9000), "text"), "parser limit");

    // Synthetic sparse HNKT bank; separate payload verifies shifted descriptors.
    byte[] bank = new byte[2048];
    Put(bank, 0, 0x259080); Put(bank, 0x80, 0x259030); Put(bank, 0x84, 0x259100);
    TextCodec.Encode("\"Hello from bank.\"", "\"Hello from bank.\"").CopyTo(bank, 0x30);
    bytes.CopyTo(bank, 0x100);
    byte[] arc = new byte[8192]; bank.CopyTo(arc, 0); Array.Fill(arc, (byte)0xA5, 2048, 6144);
    byte[] fs = new byte[512]; Put(fs, 4, FsBase + 32); Put(fs, 48, 0x500); Put(fs, 52, FsBase + 128);
    Put(fs, 128, 0x100000); Put(fs, 132, 0x300001);
    string source = Path.Combine(temp, "hnkt.bin"), fsect = Path.Combine(temp, "fsect.bin");
    File.WriteAllBytes(source, arc); File.WriteAllBytes(fsect, fs);
    using (var state = Load(source, fsect))
    {
        Assert(state.Blocks.Count == 1 && state.Blocks[0].Capacity == 16384 && state.Blocks[0].Entries.Count == 2, "bank detection");
        var doc = Export(state);
        var tree = JsonSerializer.SerializeToElement(doc, JsonOptions);
        Assert(tree.EnumerateObject().Select(x => x.Name).SequenceEqual(new[]{"blocks"}), "minimal top-level JSON");
        Assert(tree.GetProperty("blocks")[0].EnumerateObject().Select(x => x.Name).SequenceEqual(new[]{"index", "entries"}), "minimal block JSON");
        string no = Path.Combine(temp, "no"); var r = ArchiveWriter.Import(state, doc, no);
        Assert(HashFile(Path.Combine(no, "HNKT.BIN")) == Hash(arc) && HashFile(Path.Combine(no, "FSECT.BIN")) == Hash(fs), "no-op bank");
        var longer = Clone(doc); longer.Blocks[0].Entries[0].Translation += new string('x', 3000);
        string grown = Path.Combine(temp, "grown"); r = ArchiveWriter.Import(state, longer, grown);
        Assert(r.Relocated == 1 && r.Blocks[0].PaddedSize > 2048, "bank growth");
        using var reloaded = Load(Path.Combine(grown, "HNKT.BIN"), Path.Combine(grown, "FSECT.BIN"));
        Assert(reloaded.Blocks[0].Entries.Any(e => e.Source == longer.Blocks[0].Entries[0].Translation), "relocated bank readback");
        Assert(reloaded.ReadBlock(reloaded.Records[1]).SequenceEqual(arc.AsSpan(2048).ToArray()), "other block preserved");
        var bad = Clone(doc); bad.Blocks[0].Entries[0].Source = "tampered";
        Reject(() => ArchiveWriter.Import(state, bad, Path.Combine(temp, "invalid")), "source changed");
        bad = Clone(doc); bad.Blocks[0].Entries.Add(bad.Blocks[0].Entries[0]);
        Reject(() => ArchiveWriter.Import(state, bad, Path.Combine(temp, "invalid")), "duplicate id");
        bad = Clone(doc); bad.Blocks[0].Entries[0].Translation += new string('x', 7200); bad.Blocks[0].Entries[1].Translation += new string('x', 7200);
        Reject(() => ArchiveWriter.Import(state, bad, Path.Combine(temp, "invalid")), "16 KiB capacity");
        Assert(!Exists(Path.Combine(temp, "invalid")), "invalid build produces no output");
        string missing = JsonSerializer.Serialize(doc, JsonOptions).Replace("\"translation\":", "\"removed_translation\":");
        Reject(() => JsonSerializer.Deserialize<TranslationDocument>(missing, JsonOptions), "missing translation key");
    }
    // ELF-indexed archive: same bank fixture, descriptors/size fields in PT_LOAD.
    byte[] elf = new byte[1024]; new byte[] {127,69,76,70,1,1}.CopyTo(elf,0);
    elf[18] = 8; Put(elf, 28, 52); elf[42] = 32; elf[44] = 1;
    Put(elf, 52, 1); Put(elf, 56, 0); Put(elf, 60, 0x165C000); Put(elf, 68, 1024);
    Put(elf, 108, 0x500); Put(elf, 112, 0x165C100); Put(elf, 256, 0x100000); Put(elf, 260, 0x300001);
    byte[] emptyFs = new byte[512]; Put(emptyFs, 4, FsBase + 32);
    string exe = Path.Combine(temp, "SLUS_203.87"), ef = Path.Combine(temp, "empty-fsect.bin"), etc = Path.Combine(temp, "etc.bin");
    File.WriteAllBytes(exe, elf); File.WriteAllBytes(ef, emptyFs); File.WriteAllBytes(etc, arc);
    using (var state = Load(etc, ef, exe))
    {
        Assert(state.IndexKind == "exe" && state.Table == 256, "ELF table mapping");
        var doc = Export(state); doc.Blocks[0].Entries[0].Translation += new string('x', 3000);
        string output = Path.Combine(temp, "elf-out"); ArchiveWriter.Import(state, doc, output);
        Assert(!File.Exists(Path.Combine(output, "FSECT.BIN")), "ETC import does not produce FSECT");
        using var n = Load(Path.Combine(output, "ETC.BIN"), ef, Path.Combine(output, "SLUS_203.87"));
        Assert(n.Blocks[0].Entries.Any(e => e.Source == doc.Blocks[0].Entries[0].Translation), "ELF growth readback");
    }
    Console.WriteLine("PASS codec, bank, ELF, metadata, negative cases");

    if (args.Length > 0)
    {
        string dataFolder = args[0], fsPath = Path.Combine(dataFolder, "fsect.bin");
        foreach (string name in new[] {"krvi", "cvis"})
        {
            string path = Path.Combine(dataFolder, name + ".bin"); if (!File.Exists(path)) continue;
            using var state = Load(path, fsPath);
            var doc = Export(state);
            if (args.Length > 1)
            {
                var python = ReadDocument(Path.Combine(args[1], name + "_teks.json"));
                Assert(JsonSerializer.Serialize(doc, JsonOptions) == JsonSerializer.Serialize(python, JsonOptions), name + " Python JSON parity");
            }
            string output = Path.Combine(temp, name + "-noop"); var r = ArchiveWriter.Import(state, doc, output);
            Assert(HashFile(Path.Combine(output, name.ToUpperInvariant() + ".BIN")) == HashFile(path) && HashFile(Path.Combine(output, "FSECT.BIN")) == Hash(state.Index), name + " no-op exact");
            var edited = Clone(doc);
            foreach (var b in edited.Blocks) b.Entries[0].Translation += "\nTambahan terjemahan Indonesia untuk pengujian pointer baru.";
            output = Path.Combine(temp, name + "-long"); r = ArchiveWriter.Import(state, edited, output);
            Assert(r.Relocated == edited.Blocks.Count, name + " every block relocated");
            using var n = Load(Path.Combine(output, name.ToUpperInvariant() + ".BIN"), Path.Combine(output, "FSECT.BIN"));
            foreach (var original in state.Blocks.Where(b => b.Entries.Count > 0))
            {
                var nb = n.Blocks.Single(b => b.Record.Index == original.Record.Index); byte[] data = n.ReadBlock(nb.Record);
                var rows = edited.Blocks.Single(b => b.Index == original.Record.Index).Entries.ToDictionary(e => e.Id);
                foreach (var e in original.Entries)
                {
                    int target = checked((int)U32(data, e.PointerSlots[0]) - original.Base);
                    Assert(TextCodec.Read(data, target)?.Text == rows[e.Id].Translation, name + " all strings");
                }
            }
            foreach (var record in state.Records.Where(r0 => !state.Blocks.Any(b => b.Record.Index == r0.Index && b.Entries.Count > 0)))
                Assert(state.ReadBlock(record).SequenceEqual(n.ReadBlock(n.Records[record.Index])), name + " unchanged payload");
            Console.WriteLine($"PASS real {name}: {doc.Blocks.Sum(b => b.Entries.Count)} entries");
        }
        string texts = Path.Combine(temp, "batch-json"); Batch.ExportFolder(dataFolder, fsPath, texts);
        foreach (string path in Directory.GetFiles(texts, "*_teks.json"))
        {
            var d = ReadDocument(path); if (d.Blocks.Count > 0) d.Blocks[0].Entries[0].Translation += "\nBatch translation test.";
            File.Delete(path); WriteJson(path, d);
        }
        string batch = Path.Combine(temp, "batch"); var reports = Batch.ImportFolder(dataFolder, fsPath, texts, batch);
        Assert(!Directory.GetFiles(batch, "*.json").Any(), "no report files in import output");
        Assert(!File.Exists(Path.Combine(texts, "scan_report.json")), "no scan report file");
        Assert(reports.Count >= 1 && reports.All(r => r.Relocated == 1), "batch changes");
        foreach (var r in reports)
        {
            using var state = Load(Path.Combine(batch, r.Archive), Path.Combine(batch, "FSECT.BIN"));
            Assert(state.Blocks.Any(b => b.Entries.Any(e => e.Source.Contains("Batch translation test."))), "combined FSECT");
        }
        string last = Directory.GetFiles(texts, "*_teks.json").Order().Last();
        var invalid = ReadDocument(last); invalid.Blocks[0].Entries[0].Source = "changed";
        File.Delete(last); WriteJson(last, invalid);
        Reject(() => Batch.ImportFolder(dataFolder, fsPath, texts, Path.Combine(temp, "bad-batch")), "late batch failure");
        Assert(!Exists(Path.Combine(temp, "bad-batch")), "batch atomicity");
        Console.WriteLine("PASS folder export/import and rollback");
    }
    if (args.Length > 1)
    {
        int count = 0;
        foreach (string path in Directory.GetFiles(args[1], "*_teks.json"))
            foreach (var block in ReadDocument(path).Blocks)
                foreach (var row in block.Entries)
                {
                    var raw = TextCodec.Encode(row.Source, row.Source);
                    Assert(TextCodec.Read(raw, 0)?.Text == row.Source, "reference codec parity"); count++;
                }
        Console.WriteLine($"PASS {count} reference strings encoded/decoded");
    }
    Console.WriteLine($"PASS {assertions} assertions");
    return 0;
}
finally { Directory.Delete(temp, true); }
