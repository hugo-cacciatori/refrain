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

/// <summary>Namesakes (SPEC §37 "Homonyms"): the user's sorting splits one name into several artists; declining leaves
/// every album a stray. The store is the process's: these tests use a name no other test uses.</summary>
public sealed class HomonymsTests : IDisposable
{
    readonly HandIndex lib = new();
    readonly TempFolder temp = new();

    public void Dispose()
    {
        lib.Dispose();
        temp.Dispose();
    }

    [Fact]
    public void Sorted_namesakes_become_artists_of_their_own()
    {
        var folder = lib.Folder(lib.Top, "N");
        var jp = lib.Track(folder, "1.flac", title: "Jibun", artist: "Zzhomonym", album: "Anima", date: "2004");
        lib.Track(folder, "2.flac", title: "Black", artist: "ZZHOMONYM", album: "Cross Fire", date: "1999");
        var file = System.IO.Path.Combine(temp.Path, "homonyms.json");
        var store = new Homonyms(file);
        var before = Homonyms.Current;
        Homonyms.Current = store;
        try
        {
            Assert.False(store.Settled("zzhomonym"));
            store.Refuse("zzhomonym");
            Assert.True(ArtistAlbums.For(lib.Conn, jp)!.AllStrays);  // declined: every album floats apart

            MusicBrainzArtist[] artists = [new("a", "Zzhomonym", "Japanese rock band"), new("b", "Zzhomonym", "French metal band")];
            store.Split("zzhomonym", artists, new Dictionary<string, string> { ["anima"] = "a", ["cross fire"] = "b" });
            var japanese = ArtistAlbums.For(lib.Conn, jp)!;
            Assert.Equal(("zzhomonym~0", "Zzhomonym (Japanese rock band)", false), (japanese.Key, japanese.Artist, japanese.AllStrays));
            Assert.Equal(["Anima"], japanese.Albums.Select(a => a.Name));
            Assert.Equal(["ZZHOMONYM (French metal band) 1", "Zzhomonym (Japanese rock band) 1"],
                ArtistAlbums.Library(lib.Conn).Where(a => a.Key.StartsWith("zzhomonym", StringComparison.Ordinal)).Select(a => $"{a.Name} {a.Albums}").Order());
            Assert.True(new Homonyms(file).Settled("zzhomonym"));  // kept in the file
            Assert.Equal("zzhomonym~1", new Homonyms(file).Resolve("zzhomonym", "cross fire"));
        }
        finally
        {
            Homonyms.Current = before;
        }
    }
}

/// <summary>Music videos (SPEC §37): a video recording's YouTube link through MusicBrainz, kept once found.</summary>
public sealed class MusicVideosTests
{
    [Fact]
    public async Task A_song_finds_its_music_video_through_musicbrainz_and_keeps_it()
    {
        var fake = new FakeLrclib(request =>
        {
            var path = request.RequestUri!.PathAndQuery;
            if (path.Contains("recording?query", StringComparison.Ordinal))
                return FakeLrclib.Json("""
                    {"recordings": [
                      {"id": "audio", "title": "Jóga", "video": false, "artist-credit": [{"name": "Björk"}]},
                      {"id": "live", "title": "Jóga (live)", "video": true, "artist-credit": [{"name": "Björk"}]},
                      {"id": "clip", "title": "Joga", "video": true, "artist-credit": [{"name": "Bjork"}]}
                    ]}
                    """);
            if (path.Contains("recording/clip", StringComparison.Ordinal))
                return FakeLrclib.Json("""
                    {"relations": [{"type": "purchase for download", "url": {"resource": "https://example.com/joga"}},
                                   {"type": "free streaming", "url": {"resource": "https://www.youtube.com/watch?v=Vq_9xx3bLDU&t=5"}}]}
                    """);
            return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        });
        using var client = new MusicBrainzClient(fake, (_, _) => Task.CompletedTask);
        using var temp = new TempFolder();
        var file = System.IO.Path.Combine(temp.Path, "videos.json");
        var videos = new MusicVideos(file);
        Assert.Null(videos.Known("Björk", "Jóga"));
        Assert.Equal("Vq_9xx3bLDU", await videos.Find(client, "Björk", "Jóga", TestContext.Current.CancellationToken));
        var asked = fake.Requests.Count;
        Assert.Equal("Vq_9xx3bLDU", await new MusicVideos(file).Find(client, "BJORK", "joga", TestContext.Current.CancellationToken));
        Assert.Equal(asked, fake.Requests.Count);  // kept: not asked again
        Assert.Null(await videos.Find(client, "Björk", "Hunter", TestContext.Current.CancellationToken));  // none found
        Assert.Equal("", videos.Known("Björk", "Hunter"));  // known to have none, for 30 days
    }

    [Fact]
    public void Only_youtube_links_give_a_video()
    {
        Assert.Equal("Vq_9xx3bLDU", MusicVideos.YouTubeId("https://youtu.be/Vq_9xx3bLDU"));
        Assert.Equal("Vq_9xx3bLDU", MusicVideos.YouTubeId("https://www.youtube.com/embed/Vq_9xx3bLDU"));
        Assert.Null(MusicVideos.YouTubeId("https://music.youtube.com/watch?v=Vq_9xx3bLDU"));  // YouTube Music: never
        Assert.Null(MusicVideos.YouTubeId("https://vimeo.com/123"));
    }
}
