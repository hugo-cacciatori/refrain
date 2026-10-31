using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using Refrain.Core;

namespace Refrain.Views;

/// <summary>A music video (SPEC §37 "A music video"): YouTube's own embedded player in a web view (WebView2, which comes
/// with Windows), on a small local page served under its own host name (YouTube's embeds need a referring page). As an
/// <b>excerpt</b> (the preview): no controls, about 30 seconds from a third in, fading, looping, as the audio excerpt.
/// <b>Whole</b> (the info panel's Video tab): YouTube's controls, started by the user, stopping at its end.</summary>
public sealed partial class VideoView : UserControl
{
    const string Host = "refrain.video";
    readonly WebView2 web = new();
    readonly TextBlock problem = Ui.Secondary();
    Task<bool>? ready;

    /// <summary>"playing", "ended" or "error" (YouTube refused the video: removed, or not to be embedded).</summary>
    public event Action<string>? Message;

    public VideoView()
    {
        var grid = new Grid();
        grid.Children.Add(web);
        problem.Visibility = Visibility.Collapsed;
        problem.TextWrapping = TextWrapping.Wrap;
        grid.Children.Add(problem);
        Content = grid;
    }

    Task<bool> Ready() => ready ??= Start();

    async Task<bool> Start()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.VideoPage);
            await File.WriteAllTextAsync(Path.Combine(AppPaths.VideoPage, "player.html"), Page);
            await web.EnsureCoreWebView2Async();
            web.CoreWebView2.SetVirtualHostNameToFolderMapping(Host, AppPaths.VideoPage, CoreWebView2HostResourceAccessKind.Allow);
            web.CoreWebView2.Settings.AreDevToolsEnabled = false;
            web.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            web.CoreWebView2.WebMessageReceived += (_, e) =>
            {
                var text = e.TryGetWebMessageAsString();
                if (text.StartsWith("error", StringComparison.Ordinal)) Log.Info($"YouTube refused the video ({text})");
                Message?.Invoke(text.StartsWith("error", StringComparison.Ordinal) ? "error" : text);
            };
            web.CoreWebView2.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess) Log.Info($"The video page didn't load: {e.WebErrorStatus}");
            };
            // Links (YouTube's logo, its title) open in the browser, never inside Refrain.
            web.CoreWebView2.NewWindowRequested += (_, e) =>
            {
                e.Handled = true;
                Shell.Open(e.Uri);
            };
            return true;
        }
        catch (Exception e)
        {
            Log.Error("The video player couldn't start (WebView2)", e);
            problem.Text = "Videos need Microsoft Edge WebView2, which isn't working on this PC.";
            problem.Visibility = Visibility.Visible;
            web.Visibility = Visibility.Collapsed;
            return false;
        }
    }

    /// <summary>Plays YouTube video <paramref name="id"/>: an excerpt (autoplay, looping) or whole (waits for its play button).</summary>
    public async Task Play(string id, bool excerpt, bool muted)
    {
        if (!await Ready()) return;
        web.CoreWebView2.Navigate($"https://{Host}/player.html?v={Uri.EscapeDataString(id)}&mode={(excerpt ? "excerpt" : "full")}&muted={(muted ? 1 : 0)}");
    }

    public async Task SetMuted(bool muted)
    {
        if (ready is { IsCompletedSuccessfully: true, Result: true }) await web.CoreWebView2.ExecuteScriptAsync($"window.setMuted && setMuted({(muted ? "true" : "false")})");
    }

    public async Task Replay()
    {
        if (ready is { IsCompletedSuccessfully: true, Result: true }) await web.CoreWebView2.ExecuteScriptAsync("window.replay && replay()");
    }

    public void Stop()
    {
        if (ready is { IsCompletedSuccessfully: true, Result: true }) web.CoreWebView2.Navigate("about:blank");
    }

    // The player page: YouTube's IFrame API on the privacy-enhanced host; the excerpt's window and fades mirror the audio
    // excerpt's (Core.Playback.Excerpt), computed on the video's own length.
    const string Page = """
        <!doctype html><html><head><meta charset="utf-8">
        <style>html,body,#p{margin:0;width:100%;height:100%;background:#000;overflow:hidden}</style></head>
        <body><div id="p"></div>
        <script>
        const q = new URLSearchParams(location.search), excerpt = q.get('mode') === 'excerpt';
        let muted = q.get('muted') === '1', player, start = 0, length = 0, fade = 2;
        const send = m => window.chrome && chrome.webview && chrome.webview.postMessage(m);
        send('page');
        function onYouTubeIframeAPIReady() {
          send('api');
          player = new YT.Player('p', { width: '100%', height: '100%', videoId: q.get('v'), host: 'https://www.youtube-nocookie.com',
            playerVars: { autoplay: excerpt ? 1 : 0, controls: excerpt ? 0 : 1, rel: 0, playsinline: 1, fs: 0, disablekb: excerpt ? 1 : 0, mute: muted ? 1 : 0 },
            events: {
              onReady: e => {
                send('ready');
                const d = e.target.getDuration() || 0;
                if (d <= 0) { start = 0; length = 30; } else if (d <= 30) { start = 0; length = d; fade = Math.min(2, d / 6); }
                else { start = Math.min(d / 3, d - 30); length = 30; }
                if (muted) e.target.mute();
                if (excerpt) { e.target.seekTo(start, true); e.target.setVolume(0); e.target.playVideo(); }
              },
              onStateChange: e => { if (e.data === YT.PlayerState.PLAYING) send('playing'); if (e.data === YT.PlayerState.ENDED) send('ended'); },
              onError: e => send('error:' + e.data)
            } });
        }
        setInterval(() => {
          if (!excerpt || !player || !player.getCurrentTime) return;
          const t = player.getCurrentTime();
          if (t >= start + length) { player.seekTo(start, true); return; }
          const level = Math.max(0, Math.min(1, (t - start) / fade, (start + length - t) / fade));
          player.setVolume(Math.round(level * 100));
        }, 100);
        window.setMuted = m => { muted = m; if (player && player.mute) { m ? player.mute() : player.unMute(); } };
        window.replay = () => { if (player && player.seekTo) { player.seekTo(0, true); player.playVideo(); } };
        </script>
        <script src="https://www.youtube.com/iframe_api"></script>
        </body></html>
        """;
}
