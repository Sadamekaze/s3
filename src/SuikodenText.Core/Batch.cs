using System.Text.RegularExpressions;
using System.Text.Json;
using static SuikodenText.Core.Files;

namespace SuikodenText.Core;

public static class Batch
{
    public static List<object> ExportFolder(string folder, string? fsect, string output, string? exe = null, Action<string>? log = null, Func<string, bool>? overwrite = null)
    {
        Check(Directory.Exists(folder), "The archive folder was not found.");
        Check(!File.Exists(output), "The output path is a file; select a folder.");
        string[] paths = Directory.GetFiles(folder)
            .Where(p => Path.GetExtension(p).Equals(".bin", StringComparison.OrdinalIgnoreCase))
            .Where(p => !Path.GetFileName(p).Equals("fsect.bin", StringComparison.OrdinalIgnoreCase))
            .Where(p => string.IsNullOrWhiteSpace(fsect) || !Path.GetFullPath(p).Equals(Path.GetFullPath(fsect),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            .Order(StringComparer.Ordinal).ToArray();
        Check(paths.Length > 0, "No BIN archives were found.");
        ValidateInputs(paths, fsect, exe);
        Directory.CreateDirectory(output);
        List<object> reports = [];
        foreach (string path in paths)
        {
            string name = Path.GetFileName(path);
            try
            {
                using var state = ArchiveReader.Load(path, fsect, exe);
                var doc = ArchiveReader.Export(state);
                string savedPath = WriteJson(Path.Combine(output, ArchiveReader.UsesExecutable(path) ? "etc.json" : Path.GetFileNameWithoutExtension(path).ToLowerInvariant() + "_teks.json"), doc, overwrite);
                var report = JsonSerializer.SerializeToNode(ArchiveReader.Summary(state), JsonOptions)!.AsObject();
                report["status"] = (doc.Blocks.Count + (doc.Strings?.Count ?? 0) + (doc.Executable?.Count ?? 0)) > 0 ? "exported" : "no_supported_dialog";
                reports.Add(report); log?.Invoke($"{name}: {(doc.Blocks.Sum(b => b.Entries.Count) + (doc.Strings?.Count ?? 0) + (doc.Executable?.Count ?? 0))} entries -> {savedPath}");
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException or OverflowException or ArgumentException)
            {
                reports.Add(new { archive = name, status = "unsupported_or_error", reason = e.Message });
                log?.Invoke($"{name}: {e.Message}");
            }
        }
        return reports;
    }
    public static List<BuildReport> ImportFolder(string folder, string? fsect, string translations, string output,
        string? exe = null, Action<string>? log = null, Func<string, bool>? overwrite = null)
    {
        Check(Directory.Exists(folder) && Directory.Exists(translations), "The source or JSON folder was not found.");
        var jsons = Directory.GetFiles(translations, "*.json")
            .Where(path =>
                Path.GetFileName(path).EndsWith("_teks.json", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(path).Equals("etc.json", StringComparison.OrdinalIgnoreCase))
            .ToArray();

                Check(jsons.Length > 0, "No *_teks.json or etc.json files were found.");
        Dictionary<string, string> archives = new(StringComparer.OrdinalIgnoreCase);
        foreach (string path in Directory.GetFiles(folder).Where(p => Path.GetExtension(p).Equals(".bin", StringComparison.OrdinalIgnoreCase)))
            Check(archives.TryAdd(Path.GetFileName(path), path), "Duplicate archive filenames differ only in letter case.");
        string[] selected = jsons.Select(p => TranslationArchiveName(p)!).ToArray();
        HashSet<string> selectedNames = new(StringComparer.OrdinalIgnoreCase);
        foreach (string name in selected)
        {
            Check(archives.ContainsKey(name), "The source archive for the JSON file was not found: " + name);
            Check(selectedNames.Add(name), "Duplicate JSON files for the same archive.");
            Check(!name.Equals("fsect.bin", StringComparison.OrdinalIgnoreCase), "FSECT is an index, not an archive.");
        }
        ValidateInputs(selected, fsect, exe);
        bool needsFs = selected.Any(p => !ArchiveReader.UsesExecutable(p));
        bool needsExe = selected.Any(ArchiveReader.UsesExecutable);
        string temp = Staging(output);
        try
        {
            string? currentFs = null;
            if (needsFs)
            {
                currentFs = Path.Combine(temp, "FSECT.BIN");
                File.Copy(fsect!, currentFs);
            }
            string? currentExe = null;
            if (needsExe)
            {
                currentExe = Path.Combine(temp, Path.GetFileName(exe!));
                Check(!archives.ContainsKey(Path.GetFileName(exe!)) && !Path.GetFileName(exe!).Equals("FSECT.BIN", StringComparison.OrdinalIgnoreCase), "The executable filename conflicts with another output.");
                File.Copy(exe!, currentExe);
            }
            List<BuildReport> reports = []; HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
            foreach (string path in jsons)
            {
                var doc = ReadDocument(path);
                string archiveName = TranslationArchiveName(path)!;
                Check(archives.ContainsKey(archiveName), "The source archive for the JSON file was not found: " + archiveName);
                Check(seen.Add(archiveName), "Duplicate JSON files for the same archive.");
                using var state = ArchiveReader.Load(archives[archiveName], currentFs, currentExe);
                string step = Path.Combine(temp, "step_" + reports.Count);
                var report = ArchiveWriter.Import(state, doc, step);
                File.Move(Path.Combine(step, report.Archive), Path.Combine(temp, report.Archive));
                if (state.IndexKind == "fsect") File.Copy(Path.Combine(step, "FSECT.BIN"), currentFs!, true);
                if (state.IndexKind == "exe") File.Copy(Path.Combine(step, state.IndexName), currentExe!, true);
                Directory.Delete(step, true); reports.Add(report);
                log?.Invoke($"{report.Archive}: {report.Changed} changed, {report.Relocated} relocated, {report.Kept} kept English");
            }
            Publish(temp, output, overwrite);
            return reports;
        }
        finally { if (Directory.Exists(temp)) Directory.Delete(temp, true); }
    }
    private static string? TranslationArchiveName(string path)
    {
        if (Regex.IsMatch(Path.GetFileName(path), @"^etc(?:_edit[0-9]*)?\.json$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return "etc.bin";
        var match = Regex.Match(Path.GetFileName(path),
            @"^(.+)_teks(?:_edit[0-9]*)?\.json$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return match.Success ? match.Groups[1].Value + ".bin" : null;
    }

    private static void ValidateInputs(IEnumerable<string> archives, string? fsect, string? exe)
    {
        if (archives.Any(p => !ArchiveReader.UsesExecutable(p)))
            Check(!string.IsNullOrWhiteSpace(fsect) && File.Exists(fsect),
                "Archives other than ETC.BIN require a valid FSECT.BIN file.");
        if (archives.Any(ArchiveReader.UsesExecutable))
            Check(!string.IsNullOrWhiteSpace(exe) && File.Exists(exe),
                "ETC.BIN requires SLUS_203.87 (--exe).");
    }

}
