using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Satchel.Behaviors;
using Satchel.Services;
using Satchel.ViewModels;

namespace Satchel.Controls;

/// <summary>
/// The embedded YouTube player (IFrame API in WebView2) used by Videos, Music and the board's study panel.
/// It hides itself while in-app dialogs or the command palette are open, because a native window always draws on top ("airspace").
/// </summary>
public sealed class YouTubePlayer : Grid
{
    public const string PageName = "player.html";

    private readonly WebView2 _view = new() { DefaultBackgroundColor = System.Drawing.Color.Black };
    private readonly Border _fallback;
    private readonly TextBlock _fallbackText;
    private bool _ready, _failed, _initializing, _pageReady, _wanted = true, _covered;
    private (string Id, double Start, bool Autoplay)? _pending;
    private readonly DialogService _dialogs = App.Services.GetRequiredService<DialogService>();
    private readonly MainWindowViewModel _main = App.Services.GetRequiredService<MainWindowViewModel>();

    public YouTubePlayer()
    {
        // No background: the hidden music player must neither show a box nor swallow clicks.
        Background = null;
        _fallbackText = new TextBlock
        {
            Foreground = Brushes.White, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(24),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Opacity = 0.85
        };
        _fallback = new Border { Background = Brushes.Black, Child = _fallbackText, Visibility = Visibility.Collapsed };
        Children.Add(_view);
        Children.Add(_fallback);
        Loaded += (_, _) =>
        {
            _dialogs.Dialogs.CollectionChanged += OnOverlaysChanged;
            _main.PropertyChanged += OnMainChanged;
            UpdateVisibility();
        };
        Unloaded += (_, _) =>
        {
            _dialogs.Dialogs.CollectionChanged -= OnOverlaysChanged;
            _main.PropertyChanged -= OnMainChanged;
            if (StopWhenUnloaded) Stop();
        };
    }

    /// <summary>Stop the video when the control leaves the screen (off for the background music player).</summary>
    public bool StopWhenUnloaded { get; set; } = true;

    /// <summary>Hide while dialogs or the palette are open (off for the hidden music player).</summary>
    public bool HideUnderOverlays { get; set; } = true;

    /// <summary>
    /// Background mode (music): when not shown, the player shrinks to a single pixel in the corner instead of being hidden,
    /// because the browser engine won't start media in a hidden page.
    /// </summary>
    public bool KeepAlive { get; set; }

    public bool IsReady => _ready;
    public bool HasFailed => _failed;

    /// <summary>Playback position (seconds) and duration, about once a second while playing.</summary>
    public event Action<double, double>? TimeChanged;
    /// <summary>YouTube player state (-1 unstarted, 0 ended, 1 playing, 2 paused, 3 buffering, 5 cued), time, duration.</summary>
    public event Action<int, double, double>? StateChanged;
    /// <summary>YouTube error code (2 bad id, 5 HTML5 error, 100 not found, 101/150/152/153 embedding not allowed).</summary>
    public event Action<int>? ErrorOccurred;
    /// <summary>Title and channel of the loaded video (id, title, author).</summary>
    public event Action<string, string, string>? VideoInfo;

    /// <summary>Show or hide the video surface (playback continues when hidden).</summary>
    public bool IsShown
    {
        get => _wanted;
        set
        {
            _wanted = value;
            UpdateVisibility();
        }
    }

    private void OnOverlaysChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateVisibility();

    private void OnMainChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainWindowViewModel.Palette)) UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        _covered = HideUnderOverlays && (_dialogs.Dialogs.Count > 0 || _main.Palette != null);
        bool show = _wanted && !_covered && !_failed;
        if (!KeepAlive)
        {
            _view.Visibility = show ? Visibility.Visible : Visibility.Hidden;
            return;
        }
        _view.Visibility = _failed ? Visibility.Hidden : Visibility.Visible;
        if (show)
        {
            _view.Width = _view.Height = double.NaN;
            _view.HorizontalAlignment = HorizontalAlignment.Stretch;
            _view.VerticalAlignment = VerticalAlignment.Stretch;
        }
        else
        {
            _view.Width = _view.Height = 1;
            _view.HorizontalAlignment = HorizontalAlignment.Left;
            _view.VerticalAlignment = VerticalAlignment.Top;
            Margin = new Thickness(0);
        }
    }

    public void Load(string videoId, double start = 0, bool autoplay = true)
    {
        _pending = (videoId, start, autoplay);
        _ = LoadCoreAsync();
    }

    private async Task LoadCoreAsync()
    {
        if (!await EnsureAsync()) return;
        if (_pending is not { } p) return;
        _pending = null;
        _fallback.Visibility = Visibility.Collapsed;
        UpdateVisibility();
        var start = ((int)p.Start).ToString(CultureInfo.InvariantCulture);
        if (_pageReady)
        {
            await RunAsync($"load('{p.Id}', {start}, {(p.Autoplay ? "true" : "false")})");
            return;
        }
        _view.CoreWebView2.Navigate($"https://{WebViewEnv.Host}/{PageName}?v={p.Id}&t={start}&a={(p.Autoplay ? 1 : 0)}");
    }

    public void Play() => _ = RunAsync("play()");
    public void Pause() => _ = RunAsync("pause()");
    public void Seek(double seconds) => _ = RunAsync($"seek({seconds.ToString(CultureInfo.InvariantCulture)})");
    public void SetVolume(double volume) => _ = RunAsync($"vol({Math.Clamp((int)Math.Round(volume), 0, 100)})");

    /// <summary>Stops the video but keeps the page (and its media permission) alive.</summary>
    public void Halt() => _ = RunAsync("halt()");

    public void Stop()
    {
        if (!_ready) return;
        _pageReady = false;
        try { _view.CoreWebView2.Navigate("about:blank"); } catch { /* closing */ }
    }

    private async Task RunAsync(string script)
    {
        if (!_ready || !_pageReady) return;
        try { await _view.CoreWebView2.ExecuteScriptAsync(script); }
        catch { /* page reloading */ }
    }

    public async Task<bool> EnsureAsync()
    {
        if (_ready) return true;
        if (_failed) return false;
        if (_initializing)
        {
            while (_initializing) await Task.Delay(50);
            return _ready;
        }
        _initializing = true;
        try
        {
            await WebViewEnv.WritePageAsync(PageName, PageHtml);
            var env = await WebViewEnv.GetAsync();
            await _view.EnsureCoreWebView2Async(env);
            var core = _view.CoreWebView2;
            WebViewEnv.Configure(core, env);
            core.WebMessageReceived += OnMessage;
            // Anything that tries to leave the player (YouTube logo, end screens) opens in the normal browser.
            core.NewWindowRequested += (_, e) => { e.Handled = true; UiCommands.OpenUrl.Execute(e.Uri); };
            core.NavigationStarting += (_, e) =>
            {
                if (e.Uri.StartsWith($"https://{WebViewEnv.Host}/", StringComparison.OrdinalIgnoreCase) || e.Uri == "about:blank") return;
                e.Cancel = true;
                UiCommands.OpenUrl.Execute(e.Uri);
            };
            _ready = true;
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            Fail("The in-app player needs Microsoft Edge WebView2 (built into Windows 11).");
            return false;
        }
        catch (Exception ex)
        {
            App.Log("YouTube player", ex);
            Fail("The in-app player couldn't start on this PC.");
            return false;
        }
        finally
        {
            _initializing = false;
        }
    }

    private void Fail(string message)
    {
        _failed = true;
        ShowMessage(message);
    }

    public void ShowMessage(string message)
    {
        _fallbackText.Text = message;
        _fallback.Visibility = Visibility.Visible;
        _view.Visibility = Visibility.Hidden;
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            double Num(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            string Str(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
            switch (type)
            {
                case "page":
                    _pageReady = true;
                    break;
                case "ready":
                    _pageReady = true;
                    TimeChanged?.Invoke(Num("time"), Num("duration"));
                    break;
                case "time":
                    TimeChanged?.Invoke(Num("time"), Num("duration"));
                    break;
                case "state":
                    StateChanged?.Invoke((int)Num("state"), Num("time"), Num("duration"));
                    break;
                case "info":
                    VideoInfo?.Invoke(Str("id"), Str("title"), Str("author"));
                    break;
                case "error":
                    ErrorOccurred?.Invoke((int)Num("code"));
                    break;
            }
        }
        catch (JsonException) { }
    }

    public static string ErrorText(int code) => code is 101 or 150 or 152 or 153
        ? "The owner of this video doesn't allow playing it inside other apps."
        : code is 100 ? "This video isn't available any more." : "This video can't be played here.";

    private const string PageHtml = """
<!doctype html>
<html><head><meta charset="utf-8"><meta name="referrer" content="strict-origin-when-cross-origin">
<style>html,body{margin:0;height:100%;background:#000;overflow:hidden}#p{position:absolute;inset:0;width:100%;height:100%}</style></head>
<body><div id="p"></div>
<script>
const q = new URLSearchParams(location.search);
let player = null, ready = false, pending = null, lastId = '';
function post(m){ try { chrome.webview.postMessage(m); } catch(e){} }
function t(){ try { return player.getCurrentTime() || 0; } catch(e){ return 0; } }
function d(){ try { return player.getDuration() || 0; } catch(e){ return 0; } }
function info(){
  try { const v = player.getVideoData(); if (v && v.video_id) { lastId = v.video_id; post({ type: 'info', id: v.video_id, title: v.title || '', author: v.author || '' }); } } catch(e){}
}
const tag = document.createElement('script'); tag.src = 'https://www.youtube.com/iframe_api'; document.head.appendChild(tag);
function onYouTubeIframeAPIReady(){
  const id = q.get('v') || '';
  const vars = { autoplay: q.get('a') === '0' ? 0 : 1, rel: 0, modestbranding: 1, playsinline: 1, iv_load_policy: 3, origin: location.origin };
  if (id) vars.start = parseInt(q.get('t') || '0');
  const opts = { width: '100%', height: '100%', host: 'https://www.youtube-nocookie.com', playerVars: vars,
    events: {
      onReady: () => { ready = true; post({ type: 'ready', time: t(), duration: d() }); if (pending) { const p = pending; pending = null; p(); } },
      onStateChange: e => { post({ type: 'state', state: e.data, time: t(), duration: d() }); if (e.data === 1 || e.data === 5 || e.data === -1) info(); },
      onError: e => post({ type: 'error', code: e.data })
    } };
  if (id) opts.videoId = id;
  player = new YT.Player('p', opts);
  setInterval(() => { if (ready && player.getPlayerState && player.getPlayerState() === 1) post({ type: 'time', time: t(), duration: d() }); }, 1000);
}
function load(id, start, autoplay){
  const f = () => { if (autoplay === false) player.cueVideoById({ videoId: id, startSeconds: start || 0 }); else player.loadVideoById({ videoId: id, startSeconds: start || 0 }); };
  if (ready) f(); else pending = f;
}
function play(){ if (ready) player.playVideo(); }
function pause(){ if (ready) player.pauseVideo(); }
function seek(s){ if (ready) { player.seekTo(s, true); player.playVideo(); } }
function vol(v){ if (ready) { player.setVolume(v); if (v > 0) player.unMute(); } }
function halt(){ if (ready) player.stopVideo(); }
post({ type: 'page' });
</script></body></html>
""";
}
