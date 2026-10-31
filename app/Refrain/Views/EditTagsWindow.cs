using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Refrain.Core;
using Refrain.Core.Dates;
using Refrain.Core.Index;
using Refrain.Core.Search;
using Refrain.Core.Tags;
using RefrainMenu;

namespace Refrain.Views;

/// <summary>"Edit tags…" (SPEC §36 "11."): the old side panel's Metadata tab as a window of its own, on the music files
/// right-clicked: the tags of one or several files and the cover, saved only by Save. Closing with edits not saved asks
/// first (SPEC §7). Its Trim… and New cue sheet… open those entries' windows.</summary>
public sealed partial class EditTagsWindow : Window, ITaskWindow
{
    readonly MetadataPage metadata;
    readonly List<string> audio;
    readonly List<string> skipped;
    bool closing;
    Dictionary<string, string?> yearsBefore = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Tags were saved in these folders: the index re-reads them.</summary>
    public event Action<IReadOnlyList<string>>? Finished;

    public EditTagsWindow(IReadOnlyList<string> paths)
    {
        audio = paths.Where(p => Formats.IsRead(Path.GetExtension(p))).ToList();
        skipped = paths.Where(p => !Formats.IsRead(Path.GetExtension(p))).Select(p => Path.GetFileName(p)!).ToList();
        Ui.Prepare(this, audio.Count == 1 ? $"Refrain · Edit tags · {Path.GetFileName(audio[0])}" : $"Refrain · Edit tags · {audio.Count} tracks");
        metadata = new MetadataPage(() => Content.XamlRoot, () => WinRT.Interop.WindowNative.GetWindowHandle(this));
        Content = new Grid { Padding = new Thickness(8), Children = { metadata } };

        metadata.Saved += saved =>
        {
            Finished?.Invoke(saved.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            _ = OfferDate(saved);
        };
        metadata.NewCueRequested += path => App.Instance.RunEntry(MenuLogic.NewCue, [path]);
        metadata.TrimRequested += path => App.Instance.RunEntry(MenuLogic.Trim, [path]);
        AppWindow.Closing += async (_, e) =>
        {
            if (closing || !metadata.Dirty) return;
            e.Cancel = true;  // edits not saved: ask first
            if (!await ConfirmLeave()) return;
            closing = true;
            Close();
        };
    }

    public void Start()
    {
        Ui.Show(this);
        _ = Load();
    }

    async Task Load()
    {
        yearsBefore = await Task.Run(() => YearsOf(audio));
        await metadata.Load(audio, skipped);
        Ui.Fit(this);
    }

    static Dictionary<string, string?> YearsOf(IEnumerable<string> paths)
    {
        var years = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in paths)
        {
            try
            {
                years[path] = Dates.Year(TagFile.Read(path).Values.GetValueOrDefault(Field.Year));
            }
            catch (Exception)
            {
                years[path] = null;  // unreadable here, unreadable on the page too: it says why
            }
        }
        return years;
    }

    /// <summary>A date typed into a song's tags is offered to its album's other songs in the library (SPEC §37), by the
    /// album's artist and album tags, wherever their files are; written only after a yes.</summary>
    async Task OfferDate(IReadOnlyList<string> saved)
    {
        var library = App.Instance.Library;
        var offers = await Task.Run(() =>
        {
            var found = new List<(string Year, string Album, List<string> Paths)>();
            var done = new HashSet<(string, string)>();
            using var conn = library.Db.Read();
            foreach (var path in saved)
            {
                FileTags tags;
                try
                {
                    tags = TagFile.Read(path);
                }
                catch (Exception)
                {
                    continue;  // its date can't be read back: nothing to offer
                }
                var year = Dates.Year(tags.Values.GetValueOrDefault(Field.Year));
                if (year is null || year == yearsBefore.GetValueOrDefault(path)) continue;
                var artist = tags.Values.GetValueOrDefault(Field.AlbumArtist) is { Length: > 0 } albumArtist ? albumArtist : tags.Values.GetValueOrDefault(Field.Artist);
                var album = tags.Values.GetValueOrDefault(Field.Album);
                var (artistKey, albumKey) = (Names.Key(artist), Names.AlbumKey(album));
                if (artistKey.Length == 0 || albumKey.Length == 0 || !done.Add((artistKey, albumKey))) continue;
                var others = Dates.AlbumTracks(conn, artistKey, albumKey).Where(t => t.Year != year)
                    .Select(t => Queries.PathOf(conn, t.Id)).Where(p => p is not null && !saved.Contains(p, StringComparer.OrdinalIgnoreCase))
                    .Select(p => p!).ToList();
                if (others.Count > 0) found.Add((year, Names.WithoutMarkers(album), others));
            }
            return found;
        });
        foreach (var (year, album, paths) in offers)
        {
            var dialog = new ContentDialog
            {
                Title = "Date",
                Content = new TextBlock
                {
                    Text = $"Give {year} to the {(paths.Count == 1 ? "other track" : $"{paths.Count} other tracks")} of {album} too?",
                    TextWrapping = TextWrapping.Wrap,
                },
                PrimaryButtonText = "Write", CloseButtonText = "No", DefaultButton = ContentDialogButton.Primary, XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) continue;
            List<string> failed;
            try
            {
                failed = await Task.Run(() => DateWriter.Write(paths, year, App.Instance.Locks));
            }
            catch (LockedException e)
            {
                failed = [e.Message];
            }
            Finished?.Invoke(paths.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            if (failed.Count > 0)
                await new ContentDialog
                {
                    Title = "Date", CloseButtonText = "Close", XamlRoot = Content.XamlRoot,
                    Content = new TextBlock { Text = $"{year} couldn't be written into {Core.ReplayGain.Report.Plural(failed.Count, "file")}:\n{string.Join("\n", failed.Take(10))}", TextWrapping = TextWrapping.Wrap },
                }.ShowAsync();
        }
    }

    /// <summary>Save, Discard or Cancel (SPEC §7). False when the user cancels, or the save failed.</summary>
    async Task<bool> ConfirmLeave()
    {
        var dialog = new ContentDialog
        {
            Title = "Save your edits?",
            Content = "The tags have edits that aren't saved yet.",
            PrimaryButtonText = "Save",
            SecondaryButtonText = "Discard",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        switch (await dialog.ShowAsync())
        {
            case ContentDialogResult.Primary:
                return await metadata.Save();
            case ContentDialogResult.Secondary:
                metadata.Revert();
                return true;
            default:
                return false;
        }
    }
}
