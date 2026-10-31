using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Refrain.Core.Search;

namespace Refrain.Core.Dates;

/// <summary>A print of an album (a release of its release group): its year and what tells it apart.</summary>
public sealed record AlbumPrint(string Year, string Description);

/// <summary>What MusicBrainz says about an album: its release group's first year, and its prints' distinct years, the
/// earliest first (several: a reissue, so the user chooses, SPEC §37); the artist it is credited to (for homonyms).</summary>
public sealed record AlbumDate(string Title, string Year, IReadOnlyList<AlbumPrint> Prints, string ArtistId, string ArtistName);

/// <summary>An artist of MusicBrainz: its id, name and the words that tell it from a namesake.</summary>
public sealed record MusicBrainzArtist(string Id, string Name, string Disambiguation);

/// <summary>MusicBrainz, the only source of dates (SPEC §37 "Dates"), and of artist identities: its web service's rules
/// (https://musicbrainz.org/doc/MusicBrainz_API/Rate_Limiting): one request a second at most, from one queue, with a
/// User-Agent naming Refrain, its version and where to reach it. Nothing is sent but the artist's and album's names.</summary>
public sealed class MusicBrainzClient : IDisposable
{
    const string Api = "https://musicbrainz.org/ws/2";
    readonly HttpClient http;
    readonly SemaphoreSlim queue = new(1, 1);
    readonly Func<TimeSpan, CancellationToken, Task> pause;
    DateTime last = DateTime.MinValue;

    public MusicBrainzClient(HttpMessageHandler? handler = null, Func<TimeSpan, CancellationToken, Task>? pause = null)
    {
        http = handler is null ? new HttpClient() : new HttpClient(handler);
        http.Timeout = TimeSpan.FromSeconds(30);
        var version = typeof(MusicBrainzClient).Assembly.GetName().Version?.ToString(3) ?? "0";
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"Refrain/{version} ( https://github.com/hugo-cacciatori/refrain )");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        this.pause = pause ?? Task.Delay;
    }

    async Task<T?> Get<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken cancel)
    {
        await queue.WaitAsync(cancel);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var wait = last + TimeSpan.FromSeconds(1.1) - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) await pause(wait, cancel);
                last = DateTime.UtcNow;
                using var response = await http.GetAsync($"{Api}/{path}", cancel);
                if (response.IsSuccessStatusCode) return JsonSerializer.Deserialize(await response.Content.ReadAsStreamAsync(cancel), type);
                if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest) return default;
                if (response.StatusCode is HttpStatusCode.ServiceUnavailable or HttpStatusCode.TooManyRequests && attempt < 4)
                {
                    await pause(TimeSpan.FromSeconds(2 * attempt), cancel);
                    continue;
                }
                throw new HttpRequestException($"MusicBrainz answered {(int)response.StatusCode}", null, response.StatusCode);
            }
        }
        finally
        {
            queue.Release();
        }
    }

    static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The album's release group credited to this artist (names compared as Refrain groups them), with its
    /// prints' years; null when MusicBrainz has none or no year. Throws when MusicBrainz can't be reached.</summary>
    public async Task<AlbumDate?> FindAlbum(string artist, string album, CancellationToken cancel = default)
    {
        var query = WebUtility.UrlEncode($"releasegroup:{Quote(album)} AND artist:{Quote(artist)}");
        var found = await Get($"release-group?query={query}&limit=10&fmt=json", MusicBrainzJson.Default.GroupSearch, cancel);
        var group = found?.Groups?.FirstOrDefault(g =>
            Names.AlbumKey(g.Title) == Names.AlbumKey(album) &&
            g.Credits?.Any(c => Names.Key(c.Name ?? c.Artist?.Name) == Names.Key(artist)) == true &&
            Dates.Year(g.FirstRelease) is not null);
        if (group is null) return null;
        var credit = group.Credits!.First(c => Names.Key(c.Name ?? c.Artist?.Name) == Names.Key(artist));
        var releases = await Get($"release?release-group={group.Id}&limit=100&fmt=json", MusicBrainzJson.Default.ReleaseList, cancel);
        var prints = (releases?.Releases ?? [])
            .Select(r => (Year: Dates.Year(r.Date), r))
            .Where(p => p.Year is not null)
            .GroupBy(p => p.Year!)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new AlbumPrint(g.Key, string.Join("; ", g.Select(p => Describe(p.r)).Distinct().Take(3))))
            .ToList();
        if (prints.Count == 0) prints.Add(new AlbumPrint(Dates.Year(group.FirstRelease)!, "first release"));
        return new AlbumDate(group.Title ?? album, Dates.Year(group.FirstRelease)!, prints, credit.Artist?.Id ?? "", credit.Artist?.Name ?? artist);
    }

    /// <summary>The MusicBrainz artist this album is credited to, by a release-group search alone; null when unknown.</summary>
    public async Task<string?> CreditedArtist(string artist, string album, CancellationToken cancel = default)
    {
        var query = WebUtility.UrlEncode($"releasegroup:{Quote(album)} AND artist:{Quote(artist)}");
        var found = await Get($"release-group?query={query}&limit=10&fmt=json", MusicBrainzJson.Default.GroupSearch, cancel);
        return found?.Groups?.Where(g => Names.AlbumKey(g.Title) == Names.AlbumKey(album))
            .SelectMany(g => g.Credits ?? []).FirstOrDefault(c => Names.Key(c.Name ?? c.Artist?.Name) == Names.Key(artist))?.Artist?.Id;
    }

    /// <summary>The song's music video on YouTube (SPEC §37): a video recording with its title, credited to its artist,
    /// that links to YouTube for streaming; the first such link, else null.</summary>
    public async Task<string?> MusicVideo(string artist, string title, CancellationToken cancel = default)
    {
        var query = WebUtility.UrlEncode($"recording:{Quote(title)} AND artist:{Quote(artist)} AND video:true");
        var found = await Get($"recording?query={query}&limit=10&fmt=json", MusicBrainzJson.Default.RecordingSearch, cancel);
        var candidates = (found?.Recordings ?? []).Where(r => r.Video == true && r.Id is not null &&
            Names.Key(r.Title) == Names.Key(title) && r.Credits?.Any(c => Names.Key(c.Name ?? c.Artist?.Name) == Names.Key(artist)) == true).Take(3);
        foreach (var candidate in candidates)
        {
            var recording = await Get($"recording/{candidate.Id}?inc=url-rels&fmt=json", MusicBrainzJson.Default.RecordingJson, cancel);
            var link = recording?.Relations?.Where(r => r.Type is "free streaming" or "streaming")
                .Select(r => MusicVideos.YouTubeId(r.Url?.Resource)).FirstOrDefault(id => id is not null);
            if (link is not null) return link;
        }
        return null;
    }

    static string Describe(ReleaseJson release)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(release.Disambiguation)) parts.Add(release.Disambiguation!);
        if (!string.IsNullOrWhiteSpace(release.Country)) parts.Add(release.Country!);
        if (!string.IsNullOrWhiteSpace(release.Status) && release.Status != "Official") parts.Add(release.Status!.ToLowerInvariant());
        return parts.Count > 0 ? string.Join(", ", parts) : release.Date ?? "";
    }

    /// <summary>The artists MusicBrainz knows under exactly this name (as Refrain groups names): two or more are namesakes.</summary>
    public async Task<List<MusicBrainzArtist>> Namesakes(string artist, CancellationToken cancel = default)
    {
        var query = WebUtility.UrlEncode($"artist:{Quote(artist)}");
        var found = await Get($"artist?query={query}&limit=25&fmt=json", MusicBrainzJson.Default.ArtistSearch, cancel);
        return (found?.Artists ?? []).Where(a => Names.Key(a.Name) == Names.Key(artist) && a.Id is not null)
            .Select(a => new MusicBrainzArtist(a.Id!, a.Name!, a.Disambiguation ?? "")).ToList();
    }

    public void Dispose()
    {
        http.Dispose();
        queue.Dispose();
    }
}

public sealed class GroupSearch
{
    [JsonPropertyName("release-groups")] public List<GroupJson>? Groups { get; set; }
}

public sealed class GroupJson
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("first-release-date")] public string? FirstRelease { get; set; }
    [JsonPropertyName("artist-credit")] public List<CreditJson>? Credits { get; set; }
}

public sealed class CreditJson
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("artist")] public ArtistJson? Artist { get; set; }
}

public sealed class ArtistJson
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("disambiguation")] public string? Disambiguation { get; set; }
}

public sealed class ReleaseList
{
    [JsonPropertyName("releases")] public List<ReleaseJson>? Releases { get; set; }
}

public sealed class ReleaseJson
{
    [JsonPropertyName("date")] public string? Date { get; set; }
    [JsonPropertyName("country")] public string? Country { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("disambiguation")] public string? Disambiguation { get; set; }
}

public sealed class RecordingSearch
{
    [JsonPropertyName("recordings")] public List<RecordingJson>? Recordings { get; set; }
}

public sealed class RecordingJson
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("video")] public bool? Video { get; set; }
    [JsonPropertyName("artist-credit")] public List<CreditJson>? Credits { get; set; }
    [JsonPropertyName("relations")] public List<RelationJson>? Relations { get; set; }
}

public sealed class RelationJson
{
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("url")] public UrlJson? Url { get; set; }
}

public sealed class UrlJson
{
    [JsonPropertyName("resource")] public string? Resource { get; set; }
}

public sealed class ArtistSearch
{
    [JsonPropertyName("artists")] public List<ArtistJson>? Artists { get; set; }
}

[JsonSerializable(typeof(GroupSearch))]
[JsonSerializable(typeof(ReleaseList))]
[JsonSerializable(typeof(ArtistSearch))]
[JsonSerializable(typeof(RecordingSearch))]
[JsonSerializable(typeof(RecordingJson))]
internal sealed partial class MusicBrainzJson : JsonSerializerContext;
