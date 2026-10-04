using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace SuikodenText.Core;

public sealed class TranslationDocument
{
    public required List<TranslationBlock> Blocks { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<TranslationEntry>? Strings { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<TranslationEntry>? Executable { get; set; }
}
public sealed class TranslationBlock
{
    public required int Index { get; set; }
    public required List<TranslationEntry> Entries { get; set; }
}
public sealed class TranslationEntry
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public required string Translation { get; set; }
}
public sealed record Descriptor(int Index, int DescriptorOffset, long Offset, int Size);
public sealed class TextEntry
{
    public string Id { get; set; } = "";
    public int Offset { get; init; }
    public int End { get; init; }
    public string Source { get; init; } = "";
    public List<int> PointerSlots { get; set; } = [];
    public List<int> InteriorSlots { get; set; } = [];
}
public sealed record DialogueBlock(Descriptor Record, int? Root, int Base, int Capacity,
    string Kind, int LogicalSize, List<int> SizeFields, List<TextEntry> Entries);
public sealed record Relocation(string Id, int OldOffset, int NewOffset, List<int> PointerSlots);
public sealed record BlockReport(int Index, int Changed, List<Relocation> Relocated,
    int LogicalSize, int PaddedSize, int Base, int RamCapacity);
public sealed record BuildReport(string Archive, string IndexKind, int Changed, int Relocated,
    List<BlockReport> Blocks, int Kept = 0);

public sealed class ArchiveState : IDisposable
{
    internal FileStream Stream { get; }
    public List<PlainEntry> Strings { get; } = [];
    public List<PlainEntry> Executable { get; } = [];
    public string ArchivePath { get; }
    public byte[] Fsect { get; internal set; } = [];
    public byte[] Index { get; internal set; } = [];
    public string IndexKind { get; internal set; } = "";
    public string IndexName { get; internal set; } = "";
    public long IndexBase { get; internal set; }
    public int Table { get; internal set; }
    public List<Descriptor> Records { get; internal set; } = [];
    public List<DialogueBlock> Blocks { get; } = [];
    internal ArchiveState(string path)
    {
        ArchivePath = Path.GetFullPath(path);
        Stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
    }
    public byte[] ReadBlock(Descriptor block)
    {
        byte[] result = new byte[block.Size];
        Stream.Position = block.Offset;
        Stream.ReadExactly(result);
        return result;
    }
    public void Dispose() => Stream.Dispose();
}

public static class Files
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };
    public static string Hash(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
    public static string HashFile(string path)
    {
        using var f = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
    }
    public static void Check(bool ok, string message)
    {
        if (!ok) throw new InvalidDataException(message);
    }
    public static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public static string WriteJson<T>(string path, T doc, Func<string, bool>? overwrite = null)
    {
        path = ResolveOutputPath(path, overwrite, new HashSet<string>(PathComparer));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var f = new FileStream(path, FileMode.Create, FileAccess.Write);
        JsonSerializer.Serialize(f, doc, JsonOptions);
        f.WriteByte(10);
        return path;
    }
    public static TranslationDocument ReadDocument(string path)
    {
        // File.ReadAllText accepts UTF-8 BOM, matching Python utf-8-sig.
        return JsonSerializer.Deserialize<TranslationDocument>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException("The JSON document is empty.");
    }
    public static string Staging(string output)
    {
        Check(!File.Exists(output), "The output path is a file; select a folder.");
        string parent = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(parent);
        string temp = Path.Combine(parent, ".suikoden-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        return temp;
    }
    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    // Return true to overwrite, false to save with an _edit suffix.
    // GUI callers provide a dialog callback. No callback means a collision is an error.
    private static string ResolveOutputPath(string path, Func<string, bool>? overwrite,
        HashSet<string> reserved)
    {
        path = Path.GetFullPath(path);
        if (!Exists(path) && reserved.Add(path)) return path;
        if (File.Exists(path))
        {
            Check(overwrite is not null,
                "The output file already exists: " + path + ". Choose overwrite or save as _edit.");
            if (overwrite!(path))
            {
                Check(reserved.Add(path), "Multiple outputs resolve to the same file: " + path);
                return path;
            }
        }
        string folder = Path.GetDirectoryName(path)!;
        string stem = Path.GetFileNameWithoutExtension(path);
        string extension = Path.GetExtension(path);
        for (int i = 1; ; i++)
        {
            string suffix = i == 1 ? "_edit" : "_edit" + i;
            string candidate = Path.Combine(folder, stem + suffix + extension);
            if (!Exists(candidate) && reserved.Add(candidate)) return candidate;
        }
    }

    public static void Publish(string staging, string output, Func<string, bool>? overwrite = null)
    {
        Check(!File.Exists(output), "The output path is a file; select a folder.");
        var reserved = new HashSet<string>(PathComparer);
        // Ask about all collisions before moving any staged file.
        var moves = Directory.GetFiles(staging).Order(StringComparer.Ordinal)
            .Select(source => (Source: source, Target: ResolveOutputPath(
                Path.Combine(output, Path.GetFileName(source)), overwrite, reserved))).ToArray();
        Directory.CreateDirectory(output);
        foreach (var move in moves) File.Move(move.Source, move.Target, true);
    }

}
