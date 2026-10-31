namespace Refrain.Core;

/// <summary>Where the app keeps its own files (Ground rule 1): <c>%LOCALAPPDATA%\Refrain\</c> for the installer's build,
/// where the Python app's files are migrated (SPEC §36 "10."); <c>%LOCALAPPDATA%\Refrain\next\</c> for every other build,
/// so a preview never touches the Python app's live files. REFRAIN_DATA overrides it, so tests and manual checks never
/// touch the real one.</summary>
public static class AppPaths
{
#if REFRAIN_RELEASE
    public const bool Release = true;
    const string Sub = "";
#else
    public const bool Release = false;
    const string Sub = "next";
#endif

    /// <summary>Whether the data folder is the real one, not REFRAIN_DATA's (a test's, the release gate's, an agent's
    /// check): only the real one may take MusicDesk's data (measured, 2026-10-31: the gate's off-screen run moved Hugo's).</summary>
    public static bool IsRealDataFolder => string.IsNullOrEmpty(Environment.GetEnvironmentVariable("REFRAIN_DATA"));

    public static string DataFolder
    {
        get
        {
            var set = Environment.GetEnvironmentVariable("REFRAIN_DATA");
            var folder = string.IsNullOrEmpty(set)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Refrain", Sub)
                : set;
            Directory.CreateDirectory(folder);
            return folder;
        }
    }

    public static string Database => Path.Combine(DataFolder, "library.db");
    public static string Settings => Path.Combine(DataFolder, "settings.json");
    /// <summary>Ratings and notes (SPEC §37): the user's own data, kept apart from the index.</summary>
    public static string Ratings => Path.Combine(DataFolder, "ratings.json");
    /// <summary>How the user sorted artists sharing a name (SPEC §37).</summary>
    public static string Homonyms => Path.Combine(DataFolder, "homonyms.json");
    /// <summary>Songs' music videos found through MusicBrainz (SPEC §37), and songs known to have none.</summary>
    public static string Videos => Path.Combine(DataFolder, "videos.json");
    /// <summary>The page the video player is (served to the web view under its own host name, so YouTube sees a
    /// referrer), written at start.</summary>
    public static string VideoPage => Path.Combine(DataFolder, "video");
    public static string Logs => Directory.CreateDirectory(Path.Combine(DataFolder, "logs")).FullName;
}
