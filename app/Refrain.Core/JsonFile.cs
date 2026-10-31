using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Refrain.Core;

/// <summary>The app's own JSON files in the data folder (settings, ratings, homonyms, music videos): read whole, and
/// written whole through a temporary file, so a crash never leaves half a file. A file that can't be read or written is
/// logged, never thrown.</summary>
static class JsonFile
{
    /// <summary>The file's value; null when there is no file, or it can't be read (logged, with
    /// <paramref name="fallback"/> saying what happens instead).</summary>
    public static T? Load<T>(string? path, JsonTypeInfo<T> type, string fallback) where T : class
    {
        try
        {
            return path is not null && File.Exists(path) ? JsonSerializer.Deserialize(File.ReadAllText(path), type) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error($"{path} couldn't be read; {fallback}", e);
            return null;
        }
    }

    public static void Save<T>(string path, T value, JsonTypeInfo<T> type)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, type));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"{path} couldn't be saved", e);
        }
    }
}
