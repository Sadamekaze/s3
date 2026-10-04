using SuikodenText.Core;
using System.Text.Json;

try
{
    if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
    {
        Console.WriteLine("""
        SuikodenText — C# port of Python v3
        scan          archive.bin [fsect.bin] [--exe SLUS_203.87]
        export        archive.bin [fsect.bin] text.json [--exe SLUS_203.87]
        import|build  archive.bin [fsect.bin] text.json output [--exe SLUS_203.87]
        export-folder DATA [DATA/fsect.bin] translations [--exe SLUS_203.87]
        import-folder DATA [DATA/fsect.bin] translations output [--exe SLUS_203.87]

        Edit translation only. Legacy JSON files are supported; new exports contain only blocks and text.
        Existing output folders are supported. ETC requires only --exe, without FSECT.
        Folders: FSECT is required for archives other than ETC; --exe is required only for ETC.
        Folder import processes the archives selected by *_teks.json files.
        FSECT can also be supplied using --fsect PATH.

        Examples:
        export etc.bin etc_teks.json --exe SLUS_203.87
        import etc.bin etc_teks.json output --exe SLUS_203.87
        export-folder DATA translations --exe SLUS_203.87
        import-folder DATA translations output --exe SLUS_203.87
        """);
        return 0;
    }
    string command = args[0]; List<string> positional = []; string? exe = null; string? fsect = null;
    for (int i = 1; i < args.Length; i++)
    {
        if (args[i] == "--exe")
        {
            Files.Check(exe is null && i + 1 < args.Length, "--exe requires one executable path.");
            exe = args[++i];
        }
        else if (args[i] == "--fsect")
        {
            Files.Check(fsect is null && i + 1 < args.Length, "--fsect requires one FSECT path.");
            fsect = args[++i];
        }
        else
        {
            Files.Check(!args[i].StartsWith("--", StringComparison.Ordinal), "Unknown option: " + args[i]);
            positional.Add(args[i]);
        }
    }
    int count = command switch { "scan" => 2, "export" or "export-folder" => 3, "import" or "build" or "import-folder" => 4, _ => -1 };
    Files.Check(count >= 0 && (positional.Count == count || positional.Count == count - 1), "Invalid command or arguments. Run --help.");
    if (positional.Count == count)
    {
        Files.Check(fsect is null, "Specify FSECT only once: as a positional argument or with --fsect.");
        fsect = positional[1];
        positional.RemoveAt(1);
    }
    var p = positional;
    if (command == "export-folder")
    {
        var report = Batch.ExportFolder(p[0], fsect, p[1], exe, Console.WriteLine, AskOverwrite);
        Console.WriteLine("Folder export completed -> " + p[1]);
    }
    else if (command == "import-folder")
    {
        Batch.ImportFolder(p[0], fsect, p[1], p[2], exe, Console.WriteLine, AskOverwrite);
        Console.WriteLine("Folder import completed -> " + p[2]);
    }
    else
    {
        using var state = ArchiveReader.Load(p[0], fsect, exe);
        if (command == "scan") Console.WriteLine(JsonSerializer.Serialize(ArchiveReader.Summary(state), Files.JsonOptions));
        else if (command == "export")
        {
            var doc = ArchiveReader.Export(state); string savedPath = Files.WriteJson(p[1], doc, AskOverwrite);
            Console.WriteLine($"Export {(doc.Blocks.Sum(b => b.Entries.Count) + (doc.Strings?.Count ?? 0) + (doc.Executable?.Count ?? 0))} entries -> {savedPath}");
        }
        else
        {
            var report = ArchiveWriter.Import(state, Files.ReadDocument(p[1]), p[2], AskOverwrite);
            Console.WriteLine($"Completed: {report.Changed} changed, {report.Relocated} relocated, {report.Kept} kept English -> {p[2]}");
        }
    }
    return 0;
}
catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or JsonException or OverflowException or ArgumentException)
{
    Console.Error.WriteLine("Error: " + e.Message);
    return 1;
}


// Core code never opens a UI; the CLI supplies this interaction callback.
static bool AskOverwrite(string path)
{
    while (true)
    {
        Console.WriteLine($"File already exists: {path}");
        Console.Write("Overwrite [O] or save with an _edit suffix [E]? ");
        string? answer = Console.ReadLine();
        if (answer is null) throw new IOException("No overwrite choice was provided. Operation cancelled.");
        if (answer.Trim().Equals("O", StringComparison.OrdinalIgnoreCase)) return true;
        if (answer.Trim().Equals("E", StringComparison.OrdinalIgnoreCase)) return false;
        Console.WriteLine("Please enter O or E.");
    }
}
