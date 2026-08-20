using System.Text.Json;

namespace Lebiru.FileService.Services;

/// <summary>Provides crash-safe JSON persistence.</summary>
internal static class AtomicJsonStore
{
    public static T? Read<T>(string path)
    {
        if (!File.Exists(path)) return default;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return JsonSerializer.Deserialize<T>(stream);
    }

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + ".tmp";
        var backupPath = path + ".bak";
        using (var stream = new FileStream(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.None,
                   64 * 1024, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, value);
            stream.Flush(flushToDisk: true);
        }

        if (File.Exists(path))
            File.Replace(temporaryPath, path, backupPath, ignoreMetadataErrors: true);
        else
            File.Move(temporaryPath, path);
    }
}
