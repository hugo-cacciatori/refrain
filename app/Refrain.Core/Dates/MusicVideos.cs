using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Refrain.Core.Search;

namespace Refrain.Core.Dates;

/// <summary>Music videos (SPEC §37 "A music video"), found through MusicBrainz, no key: a video recording of the song
/// (MusicBrainz marks music videos as video recordings) credited to its artist, and its YouTube link among the
/// recording's streaming links. MusicBrainz's guidelines take only official uploads there (the artist's or label's
/// channel), so no auto-generated "audio with the cover" upload; a song without one has no video. Answers are kept in
/// <c>videos.json</c> in the data folder (a song without a video is asked again after 30 days).</summary>
public sealed partial class MusicVideos
{
    static readonly TimeSpan Retry = TimeSpan.FromDays(30);
    readonly string? file;
    readonly Lock gate = new();
    readonly Dictionary<string, VideoJson> known;

    public MusicVideos(string? file)
    {
        this.file = file;
        known = Load(file);
    }

    public static string Key(string artist, string title) => $"{Names.Key(artist)}\u001f{Names.Key(title)}";

    /// <summary>The YouTube video already known for this song: its id, "" when known to have none, null when not asked
    /// yet (or long ago).</summary>
    public string? Known(string artist, string title)
    {
        lock (gate)
            return known.TryGetValue(Key(artist, title), out var entry) && (entry.Id is { Length: > 0 } || DateTime.UtcNow - entry.Checked < Retry)
                ? entry.Id ?? ""
                : null;
    }

    /// <summary>The song's YouTube video id, or null; asks MusicBrainz when not known. Throws when it can't be reached.</summary>
    public async Task<string?> Find(MusicBrainzClient client, string artist, string title, CancellationToken cancel = default)
    {
        if (Known(artist, title) is { } id) return id.Length > 0 ? id : null;
        var found = await client.MusicVideo(artist, title, cancel);
        lock (gate)
        {
            known[Key(artist, title)] = new VideoJson { Id = found, Checked = DateTime.UtcNow };
            Save();
        }
        return found;
    }

    /// <summary>A YouTube link's video id ("watch?v=…", "youtu.be/…", "embed/…"); null for any other link.</summary>
    public static string? YouTubeId(string? url)
    {
        if (url is null) return null;
        var match = YouTube().Match(url);
        return match.Success ? match.Groups[1].Value : null;
    }

    void Save()
    {
        if (file is null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(known, VideosJson.Default.DictionaryStringVideoJson));
            File.Move(file + ".tmp", file, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Log.Error($"{file} couldn't be saved", e);
        }
    }

    static Dictionary<string, VideoJson> Load(string? file)
    {
        try
        {
            return file is not null && File.Exists(file)
                ? JsonSerializer.Deserialize(File.ReadAllText(file), VideosJson.Default.DictionaryStringVideoJson) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error($"{file} couldn't be read; music videos are looked up again", e);
            return [];
        }
    }

    [GeneratedRegex(@"^https?://(?:www\.|m\.)?(?:youtube\.com/(?:watch\?(?:.*&)?v=|embed/|v/)|youtu\.be/)([A-Za-z0-9_-]{11})")]
    private static partial Regex YouTube();
}

public sealed class VideoJson
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("checked")] public DateTime Checked { get; set; }
}

[JsonSerializable(typeof(Dictionary<string, VideoJson>))]
internal sealed partial class VideosJson : JsonSerializerContext;
