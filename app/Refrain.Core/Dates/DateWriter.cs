using Refrain.Core.Tags;

namespace Refrain.Core.Dates;

/// <summary>Writes an album's date into its tracks' tags (SPEC §34, §37 "Dates": only after the user said yes), one file
/// after another under a lock, so no task touches them meanwhile and the player leaves them alone.</summary>
public static class DateWriter
{
    public const string Owner = "Writing dates";

    /// <summary>The files whose date couldn't be written, with why; empty when all were.</summary>
    public static List<string> Write(IReadOnlyList<string> paths, string year, PathLocks locks)
    {
        var token = locks.Acquire(paths, Owner);
        var failed = new List<string>();
        try
        {
            foreach (var path in paths)
            {
                try
                {
                    TagFile.Save(path, new Dictionary<Field, string> { [Field.Year] = year }, null);
                }
                catch (TagSaveException e)
                {
                    failed.Add($"{Path.GetFileName(path)}: {e.Message}");
                }
            }
        }
        finally
        {
            locks.Release(token);
        }
        if (failed.Count > 0) Log.Info($"Dates not written: {string.Join("; ", failed)}");
        return failed;
    }
}
