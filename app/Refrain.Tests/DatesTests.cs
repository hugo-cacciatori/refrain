using System.Net;
using Refrain.Core;
using Refrain.Core.Dates;
using Refrain.Core.Search;
using Refrain.Core.Tags;
using Xunit;

namespace Refrain.Tests;

/// <summary>Dates (SPEC §37): what a good date is, which albums are undated, what MusicBrainz says (canned answers:
/// tests never reach the network), and the write.</summary>
public sealed class DatesTests : IDisposable
{
    readonly HandIndex lib = new();

    public void Dispose() => lib.Dispose();

    [Fact]
    public void Any_date_with_a_real_year_is_good()
    {
        Assert.Equal("1997", Dates.Year("1997"));
        Assert.Equal("1997", Dates.Year("1997-09-22"));
        Assert.Equal("1997", Dates.Year("22/09/1997"));
        Assert.Equal("1997", Dates.Year("Sept. 1997"));
        Assert.Equal("2004", Dates.Year("2004-05-01T00:00:00"));
        foreach (var bad in new[] { null, "", "?", "0000", "Unknown", "19xx", "12345" })
            Assert.Null(Dates.Year(bad));
    }

    [Fact]
    public void An_album_is_undated_when_a_song_has_no_date_and_strays_in_the_view()
    {
        var folder = lib.Folder(lib.Top, "A");
        var debut = lib.Track(folder, "1.flac", title: "Human Behaviour", artist: "Björk", album: "Debut", date: "1993");
        lib.Track(folder, "2.flac", title: "Venus", artist: "Björk", album: "Debut", date: "?");
        lib.Track(folder, "3.flac", title: "Hunter", artist: "Björk", album: "Homogenic", date: "1997-09-22");
        lib.Track(folder, "4.flac", title: "Loose", artist: "Björk");  // no album: nothing to look up
        var undated = Assert.Single(Dates.Undated(lib.Conn));
        Assert.Equal(("Björk", "Debut", true, 2), (undated.Artist, undated.Album, undated.Partly, undated.Tracks.Count));
        var view = ArtistAlbums.For(lib.Conn, debut)!;
        Assert.Null(view.Albums.Single(a => a.Name == "Debut").Year);  // partly dated: a stray
        Assert.Equal("1997", view.Albums.Single(a => a.Name == "Homogenic").Year);
    }

    static FakeLrclib MusicBrainz(Dictionary<string, string> answers) => new(request =>
    {
        var path = request.RequestUri!.PathAndQuery;
        var found = answers.FirstOrDefault(a => path.Contains(a.Key, StringComparison.Ordinal));
        return found.Value is null ? new HttpResponseMessage(HttpStatusCode.NotFound) : FakeLrclib.Json(found.Value);
    });

    [Fact]
    public async Task MusicBrainz_gives_the_album_its_first_year_and_its_prints()
    {
        var fake = MusicBrainz(new()
        {
            ["/release-group?"] = """
                {"release-groups": [
                  {"id": "x", "title": "Homogenic Live", "first-release-date": "1998", "artist-credit": [{"name": "Björk", "artist": {"id": "b", "name": "Björk"}}]},
                  {"id": "g", "title": "Homogenic", "first-release-date": "1997-09-22", "artist-credit": [{"name": "Björk", "artist": {"id": "b", "name": "Björk"}}]}
                ]}
                """,
            ["release-group=g"] = """
                {"releases": [{"date": "1997-09-22", "country": "GB", "status": "Official"},
                              {"date": "2009", "country": "XE", "status": "Official", "disambiguation": "remastered"},
                              {"date": "", "country": "JP"}]}
                """,
        });
        using var client = new MusicBrainzClient(fake, (_, _) => Task.CompletedTask);
        var found = (await client.FindAlbum("BJORK", "Homogenic (CD1)", TestContext.Current.CancellationToken))!;
        Assert.Equal(("Homogenic", "1997", "b"), (found.Title, found.Year, found.ArtistId));
        Assert.Equal(["1997 GB", "2009 remastered, XE"], found.Prints.Select(p => $"{p.Year} {p.Description}"));
        Assert.Null(await client.FindAlbum("Björk", "Vespertine", TestContext.Current.CancellationToken));  // nothing there
        Assert.All(fake.Requests, r => Assert.Contains("fmt=json", r));
    }

    [Fact]
    public async Task MusicBrainz_names_the_namesakes()
    {
        var fake = MusicBrainz(new()
        {
            ["/artist?"] = """
                {"artists": [{"id": "1", "name": "Nightmare", "disambiguation": "Japanese rock band"},
                             {"id": "2", "name": "NIGHTMARE", "disambiguation": "French metal band"},
                             {"id": "3", "name": "Nightmare of You"}]}
                """,
        });
        using var client = new MusicBrainzClient(fake, (_, _) => Task.CompletedTask);
        var namesakes = await client.Namesakes("Nightmare", TestContext.Current.CancellationToken);
        Assert.Equal(["Japanese rock band", "French metal band"], namesakes.Select(a => a.Disambiguation));
    }

    [Fact]
    public void A_date_is_written_into_every_track_under_a_lock()
    {
        using var temp = new TempFolder();
        var file = System.IO.Path.Combine(temp.Path, "01 Tone.flac");
        File.Copy(System.IO.Path.Combine(Setup.Fixtures, "replaygain", "Fixture Artist", "Album A", "01 Tone.flac"), file);
        var locks = new PathLocks();
        var held = false;
        locks.Changed += () => held |= locks.Conflict(file) == DateWriter.Owner;
        Assert.Empty(DateWriter.Write([file], "1997", locks));
        Assert.True(held);
        Assert.Null(locks.Conflict(file));
        Assert.Equal("1997", TagFile.Read(file).Values[Field.Year]);
    }
}
