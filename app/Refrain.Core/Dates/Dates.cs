using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Refrain.Core.Search;

namespace Refrain.Core.Dates;

/// <summary>An album of the library without a real date on every song (SPEC §37 "Dates"): its artist and album as the
/// discography view groups them, every track that carries it (copies included: they are all written), and whether
/// some songs have a date already (a partly dated album counts as undated; all its dates are replaced).</summary>
public sealed record UndatedAlbum(string ArtistKey, string Artist, string AlbumKey, string Album, IReadOnlyList<long> Tracks, bool Partly);

/// <summary>Dates (SPEC §37): any known date format is good, Refrain takes its year; a date with no real year ("?",
/// "0000", "Unknown", empty) is bad.</summary>
public static partial class Dates
{
    /// <summary>The year in a date tag: the first four-digit number from 1000 to 2999 standing alone ("1997",
    /// "1997-09-22", "22/09/1997", "Sept. 1997"); null otherwise.</summary>
    public static string? Year(string? date)
    {
        if (string.IsNullOrWhiteSpace(date)) return null;
        foreach (Match match in FourDigits().Matches(date))
            if (match.Value[0] is '1' or '2') return match.Value;
        return null;
    }

    /// <summary>Every album (by its artist and album tags; songs without an album name aside) of which a song has no
    /// real date, by artist then album.</summary>
    public static List<UndatedAlbum> Undated(SqliteConnection conn)
    {
        var groups = new Dictionary<(string, string), (List<string> Artists, List<string> Albums, List<long> Ids, int Dated)>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT entry_id, COALESCE(NULLIF(TRIM(albumartist), ''), NULLIF(TRIM(artist), '')), album, date
                FROM tracks WHERE read_error IS NULL ORDER BY entry_id
                """;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (r.IsDBNull(1) || r.IsDBNull(2)) continue;
                var artistKey = Names.Key(r.GetString(1));
                var albumKey = Names.AlbumKey(r.GetString(2));
                if (artistKey.Length == 0 || albumKey.Length == 0) continue;
                if (!groups.TryGetValue((artistKey, albumKey), out var group)) groups[(artistKey, albumKey)] = group = ([], [], [], 0);
                group.Artists.Add(r.GetString(1));
                group.Albums.Add(Names.WithoutMarkers(r.GetString(2)));
                group.Ids.Add(r.GetInt64(0));
                if (Year(r.IsDBNull(3) ? null : r.GetString(3)) is not null) groups[(artistKey, albumKey)] = group with { Dated = group.Dated + 1 };
            }
        }
        return groups.Where(g => g.Value.Dated < g.Value.Ids.Count)
            .Select(g => new UndatedAlbum(g.Key.Item1, Names.Shown(g.Value.Artists), g.Key.Item2, Names.Shown(g.Value.Albums), g.Value.Ids, g.Value.Dated > 0))
            .OrderBy(a => a.ArtistKey, StringComparer.Ordinal).ThenBy(a => a.Album, NaturalOrder.Instance).ToList();
    }

    /// <summary>Every track of an album as the discography view groups it (its artist and album keys), with its year.</summary>
    public static List<(long Id, string? Year)> AlbumTracks(SqliteConnection conn, string artistKey, string albumKey)
    {
        var tracks = new List<(long, string?)>();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT entry_id, COALESCE(NULLIF(TRIM(albumartist), ''), NULLIF(TRIM(artist), '')), album, date FROM tracks WHERE read_error IS NULL";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (!r.IsDBNull(1) && !r.IsDBNull(2) && Names.Key(r.GetString(1)) == artistKey && Names.AlbumKey(r.GetString(2)) == albumKey)
                tracks.Add((r.GetInt64(0), Year(r.IsDBNull(3) ? null : r.GetString(3))));
        return tracks;
    }

    [GeneratedRegex(@"(?<!\d)\d{4}(?!\d)")]
    private static partial Regex FourDigits();
}
