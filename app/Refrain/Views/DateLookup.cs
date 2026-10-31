using Microsoft.Data.Sqlite;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Refrain.Core;
using Refrain.Core.Dates;
using Refrain.Core.Index;
using RefrainMenu;

namespace Refrain.Views;

/// <summary>Dates from MusicBrainz (SPEC §37 "Dates"), in blocking popups over the Search window: at a scan's end one
/// listing every undated album, offering to look them up; for each album found, "MusicBrainz says … Write it into its N
/// tracks?", or, for several prints, which print's year to take; when MusicBrainz has nothing or can't be reached, a
/// popup saying so with Type it in… (Edit tags on the album's tracks). Nothing is written without a yes. A declined
/// album keeps a yellow "!" in the discography view for the session.</summary>
public sealed class DateLookup : IDisposable
{
    readonly Core.Library library;
    readonly Func<XamlRoot> root;
    readonly MusicBrainzClient client = new();
    readonly HashSet<(string, string)> asked = [];
    bool quietScans, running;

    /// <summary>The albums the user declined a date for this session ("artist key|album key").</summary>
    public HashSet<string> Declined { get; } = [];

    /// <summary>Dates were written, or an album was declined: the view redraws.</summary>
    public event Action? Changed;

    public DateLookup(Core.Library library, Func<XamlRoot> root)
    {
        this.library = library;
        this.root = root;
    }

    public static string KeyOf(string artistKey, string albumKey) => $"{artistKey}|{albumKey}";

    Task<T> Read<T>(Func<SqliteConnection, T> query) => Task.Run(() =>
    {
        using var conn = library.Db.Read();
        return query(conn);
    });

    /// <summary>A scan ended (the indexer idle): one popup listing every undated album not asked about yet this session.</summary>
    public async Task AskAtScanEnd()
    {
        if (running) return;
        var albums = await Read(Dates.Undated);
        if (albums.All(a => asked.Contains((a.ArtistKey, a.AlbumKey)))) return;
        foreach (var album in albums) asked.Add((album.ArtistKey, album.AlbumKey));
        var text = albums.Count == 1 ? "1 album has no date, or not on every song." : $"{albums.Count} albums have no date, or not on every song.";
        if (SearchWindow.Offscreen)
        {
            Log.Info($"Check dates popup: {text} ({string.Join("; ", albums.Select(a => $"{a.Artist} – {a.Album}"))})");
            return;
        }
        var list = new StackPanel { Spacing = 2 };
        foreach (var album in albums.Take(20)) list.Children.Add(new TextBlock { Text = $"{album.Artist} – {album.Album}{(album.Partly ? " (some songs dated)" : "")}" });
        if (albums.Count > 20) list.Children.Add(Ui.Secondary($"and {albums.Count - 20} more"));
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = text + " Look them up on MusicBrainz?", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 260 });
        body.Children.Add(Ui.Secondary("Each date found is written only after you say yes, album by album."));
        if (await Ask("Dates", body, "Look them up", null, "Not now") == ContentDialogResult.Primary) await LookUp(albums, fromScan: true);
    }

    /// <summary>One album, opened in the discography view as a stray: asked again even after "Don't remind me".</summary>
    public async Task LookUpOne(string artistKey, string albumKey)
    {
        if (running) return;
        var album = (await Read(Dates.Undated)).FirstOrDefault(a => a.ArtistKey == artistKey && a.AlbumKey == albumKey);
        if (album is not null) await LookUp([album], fromScan: false);
    }

    async Task LookUp(IReadOnlyList<UndatedAlbum> albums, bool fromScan)
    {
        running = true;
        try
        {
            foreach (var album in albums)
            {
                AlbumDate? found;
                try
                {
                    found = await client.FindAlbum(album.Artist, album.Album);
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
                {
                    Log.Info($"MusicBrainz lookup of {album.Artist} – {album.Album} failed: {e.Message}");
                    await NotFound(album, $"MusicBrainz can't be reached ({e.Message}), so {album.Album} has no date yet.", fromScan, last: true);
                    return;  // the next albums would fail the same way
                }
                if (found is null)
                {
                    if (!await NotFound(album, $"MusicBrainz has no date for {album.Album} by {album.Artist}.", fromScan, last: false)) return;
                    continue;
                }
                if (!await Offer(album, found)) return;
            }
        }
        finally
        {
            running = false;
        }
    }

    /// <summary>Found: write after a yes; several prints, the user chooses which year. False: stop the lookups.</summary>
    async Task<bool> Offer(UndatedAlbum album, AlbumDate found)
    {
        var files = album.Tracks.Count == 1 ? "its track" : $"its {album.Tracks.Count} tracks";
        var body = new StackPanel { Spacing = 8 };
        var choices = new List<RadioButton>();
        if (found.Prints.Count > 1)
        {
            body.Children.Add(new TextBlock
            {
                Text = $"MusicBrainz knows several prints of {found.Title} by {album.Artist}. Which one's year goes into {files}?",
                TextWrapping = TextWrapping.Wrap,
            });
            for (var i = 0; i < found.Prints.Count; i++)
            {
                var print = found.Prints[i];
                var choice = new RadioButton
                {
                    GroupName = "print", IsChecked = i == 0,
                    Content = $"{print.Year}{(i == 0 ? " (the first release)" : "")}{(print.Description.Length > 0 ? $": {print.Description}" : "")}",
                };
                choices.Add(choice);
                body.Children.Add(choice);
            }
        }
        else
            body.Children.Add(new TextBlock { Text = $"MusicBrainz says {found.Title} by {album.Artist} came out in {found.Year}. Write it into {files}?", TextWrapping = TextWrapping.Wrap });
        if (album.Partly) body.Children.Add(Ui.Secondary("Some of its songs have a date already; every song gets this one."));
        switch (await Ask("Dates", body, "Write", "Skip", "Stop"))
        {
            case ContentDialogResult.Primary:
                var year = choices.Count > 0 ? found.Prints[choices.FindIndex(c => c.IsChecked == true)].Year : found.Year;
                await Write(album, year);
                return true;
            case ContentDialogResult.Secondary:
                Decline(album);
                return true;
            default:
                Decline(album);
                return false;
        }
    }

    /// <summary>Nothing found, or offline: say so, and offer to type the date by hand. In a scan's lookups "Don't remind
    /// me" silences these until Refrain closes. False: stop.</summary>
    async Task<bool> NotFound(UndatedAlbum album, string text, bool fromScan, bool last)
    {
        if (fromScan && quietScans)
        {
            Decline(album);
            return true;
        }
        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(Ui.Secondary("Type it in opens the album's tracks in Edit tags, where the date can be typed and saved."));
        var quiet = new CheckBox { Content = "Don't remind me until Refrain closes", Visibility = fromScan ? Visibility.Visible : Visibility.Collapsed };
        body.Children.Add(quiet);
        var answer = await Ask("Dates", body, "Type it in…", last ? null : "Skip", last ? "Close" : "Stop");
        quietScans |= quiet.IsChecked == true;
        Decline(album);
        if (answer == ContentDialogResult.Primary)
        {
            await TypeIn(album);
            return !last;
        }
        return answer == ContentDialogResult.Secondary;
    }

    void Decline(UndatedAlbum album)
    {
        Declined.Add(KeyOf(album.ArtistKey, album.AlbumKey));
        Changed?.Invoke();
    }

    /// <summary>Edit tags on the album's tracks, where its date is typed by hand.</summary>
    public async Task TypeIn(UndatedAlbum album)
    {
        var paths = await Read(conn => album.Tracks.Select(id => Queries.PathOf(conn, id)).Where(p => p is not null).Select(p => p!).ToList());
        if (paths.Count > 0) App.Instance.RunEntry(MenuLogic.EditTags, paths);
    }

    /// <summary>The album's tracks for "Type it in…" from the view's info panel.</summary>
    public async Task TypeIn(string artistKey, string albumKey)
    {
        var album = (await Read(Dates.Undated)).FirstOrDefault(a => a.ArtistKey == artistKey && a.AlbumKey == albumKey);
        if (album is not null) await TypeIn(album);
    }

    async Task Write(UndatedAlbum album, string year)
    {
        var paths = await Read(conn => album.Tracks.Select(id => Queries.PathOf(conn, id)).Where(p => p is not null).Select(p => p!).ToList());
        List<string> failed;
        try
        {
            failed = await Task.Run(() => DateWriter.Write(paths, year, App.Instance.Locks));
        }
        catch (LockedException e)
        {
            failed = [e.Message];
        }
        foreach (var folder in paths.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase))
            library.Indexer.ScanPath(folder, recursive: false);
        Declined.Remove(KeyOf(album.ArtistKey, album.AlbumKey));
        if (failed.Count > 0)
            await Ask("Dates", new TextBlock { Text = $"{year} couldn't be written into {failed.Count} of {paths.Count} files:\n{string.Join("\n", failed.Take(10))}", TextWrapping = TextWrapping.Wrap }, null, null, "Close");
        // The index reads the new tags, then the view redraws.
        for (var i = 0; i < 40 && library.Indexer.Busy; i++) await Task.Delay(250);
        Changed?.Invoke();
    }

    async Task<ContentDialogResult> Ask(string title, UIElement body, string? primary, string? secondary, string close)
    {
        var dialog = new ContentDialog
        {
            Title = title, Content = body, CloseButtonText = close, XamlRoot = root(),
            DefaultButton = primary is null ? ContentDialogButton.Close : ContentDialogButton.Primary,
        };
        if (primary is not null) dialog.PrimaryButtonText = primary;
        if (secondary is not null) dialog.SecondaryButtonText = secondary;
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Log.Info($"A dates popup couldn't show (another dialog is open): {e.Message}");
            return ContentDialogResult.None;
        }
    }

    public void Dispose() => client.Dispose();
}
