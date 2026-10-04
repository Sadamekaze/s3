using System.Threading.Tasks;
using SuikodenText.Core;

public static class ArchiveLoader
{
    public static Task<ArchiveState> LoadAsync(
        string path,
        string? fsect = null,
        string? exe = null)
        => Task.Run(() => ArchiveReader.Load(path, fsect, exe));
}