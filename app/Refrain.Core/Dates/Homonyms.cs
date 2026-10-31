using System.Text.Json;
using System.Text.Json.Serialization;

namespace Refrain.Core.Dates;

/// <summary>Artists sharing a name (SPEC §37 "Homonyms"): MusicBrainz says which artists bear it, the user says which
/// albums are whose. The answers are the user's own data (<c>homonyms.json</c> in the data folder): an artist checked
/// with one namesake or none, split (each album to one of MusicBrainz's artists), or said to be one artist. Declining to
/// sort them leaves every album of that artist a stray, for the session. Thread-safe; one store for the process,
/// which the library's grouping reads (<see cref="Search.ArtistAlbums"/>).</summary>
public sealed class Homonyms
{
    public const char Separator = '~';

    /// <summary>The process's store; an empty one (nothing split) until the app loads its file.</summary>
    public static Homonyms Current { get; set; } = new(null);

    readonly string? file;
    readonly Lock gate = new();
    readonly Dictionary<string, HomonymEntry> entries;
    readonly HashSet<string> refused = [];

    public Homonyms(string? file)
    {
        this.file = file;
        entries = Load(file);
    }

    /// <summary>The artist's key in the library: its own, or, split, its namesake's ("nightmare~1").</summary>
    public string Resolve(string artistKey, string albumKey)
    {
        lock (gate)
            return entries.TryGetValue(artistKey, out var entry) && entry.State == "split" && entry.Albums is { } albums &&
                   albums.TryGetValue(albumKey, out var id) && entry.Artists?.FindIndex(a => a.Id == id) is >= 0 and var at
                ? $"{artistKey}{Separator}{at}"
                : artistKey;
    }

    /// <summary>What tells a split artist from its namesakes ("Japanese rock band"); null for an artist not split.</summary>
    public string? Disambiguation(string key)
    {
        var at = key.IndexOf(Separator);
        if (at < 0 || !int.TryParse(key[(at + 1)..], out var index)) return null;
        lock (gate)
            return entries.TryGetValue(key[..at], out var entry) && entry.Artists is { } artists && index < artists.Count
                ? artists[index].Disambiguation is { Length: > 0 } d ? d : $"artist {index + 1}"
                : null;
    }

    public static string BaseKey(string key) => key.IndexOf(Separator) is var at and >= 0 ? key[..at] : key;

    /// <summary>Whether the user was asked already (any answer kept in the file), or declined this session.</summary>
    public bool Settled(string artistKey)
    {
        lock (gate) return entries.ContainsKey(artistKey) || refused.Contains(artistKey);
    }

    /// <summary>Declined to sort this session: every album of the artist is a stray (SPEC §37).</summary>
    public bool Refused(string artistKey)
    {
        lock (gate) return refused.Contains(artistKey);
    }

    public void Refuse(string artistKey)
    {
        lock (gate) refused.Add(artistKey);
    }

    /// <summary>MusicBrainz knows one artist or none by this name: nothing to ask.</summary>
    public void Single(string artistKey) => Set(artistKey, new HomonymEntry { State = "checked" });

    /// <summary>The user said the albums are all one artist's.</summary>
    public void One(string artistKey) => Set(artistKey, new HomonymEntry { State = "one" });

    /// <summary>The user sorted the albums: each album key to a MusicBrainz artist.</summary>
    public void Split(string artistKey, IReadOnlyList<MusicBrainzArtist> artists, IReadOnlyDictionary<string, string> albums) =>
        Set(artistKey, new HomonymEntry
        {
            State = "split",
            Artists = artists.Select(a => new HomonymArtist { Id = a.Id, Name = a.Name, Disambiguation = a.Disambiguation }).ToList(),
            Albums = new Dictionary<string, string>(albums),
        });

    void Set(string artistKey, HomonymEntry entry)
    {
        lock (gate)
        {
            entries[artistKey] = entry;
            refused.Remove(artistKey);
            if (file is null) return;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(entries, HomonymsJson.Default.DictionaryStringHomonymEntry));
                File.Move(file + ".tmp", file, overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Error($"{file} couldn't be saved", e);
            }
        }
    }

    static Dictionary<string, HomonymEntry> Load(string? file)
    {
        try
        {
            return file is not null && File.Exists(file)
                ? JsonSerializer.Deserialize(File.ReadAllText(file), HomonymsJson.Default.DictionaryStringHomonymEntry) ?? []
                : [];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Error($"{file} couldn't be read; homonyms start unsorted", e);
            return [];
        }
    }
}

public sealed class HomonymEntry
{
    [JsonPropertyName("state")] public string State { get; set; } = "";
    [JsonPropertyName("artists")] public List<HomonymArtist>? Artists { get; set; }
    [JsonPropertyName("albums")] public Dictionary<string, string>? Albums { get; set; }
}

public sealed class HomonymArtist
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("disambiguation")] public string Disambiguation { get; set; } = "";
}

[JsonSerializable(typeof(Dictionary<string, HomonymEntry>))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal sealed partial class HomonymsJson : JsonSerializerContext;
