using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Satchel.Services;

namespace Satchel.Controls;

/// <summary>
/// Plays audio (podcast episodes) in a one-pixel browser surface. The browser engine follows the redirect chains podcast
/// hosts use and plays MP3/AAC without Windows Media Player. It lives in the main window, so playback continues on every
/// page; it stays one visible pixel because the engine won't start media in a hidden page.
/// </summary>
public sealed class AudioHost : Border
{
    private const string PageName = "audio.html";
    private readonly WebView2 _view = new() { Width = 1, Height = 1 };
    private bool _ready, _failed, _initializing, _pageReady;
    private string _failMessage = "";

    public AudioHost()
    {
        Width = Height = 1;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Top;
        IsHitTestVisible = false;
        Focusable = false;
        Child = _view;
    }

    /// <summary>Position and duration in seconds, about once a second while playing.</summary>
    public event Action<double, double>? TimeChanged;
    public event Action<bool>? PlayingChanged;
    public event Action? Ended;
    public event Action<string>? Failed;

    public async Task PlayAsync(string url, double start, double rate, double volume)
    {
        if (!await EnsureAsync())
        {
            Failed?.Invoke(_failMessage);
            return;
        }
        await RunAsync($"podLoad({JsonSerializer.Serialize(url)}, {Num(start)}, {Num(rate)}, {Num(volume)})");
    }

    public Task ResumeAsync() => RunAsync("podResume()");
    public Task PauseAsync() => RunAsync("podPause()");
    public Task SeekAsync(double seconds) => RunAsync($"podSeek({Num(seconds)})");
    public Task SetRateAsync(double rate) => RunAsync($"podRate({Num(rate)})");
    public Task SetVolumeAsync(double volume) => RunAsync($"podVolume({Num(volume)})");
    public Task StopAsync() => RunAsync("podStop()");

    private static string Num(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private async Task RunAsync(string script)
    {
        if (!_ready) return;
        for (int i = 0; i < 100 && !_pageReady; i++) await Task.Delay(50);
        try { await _view.CoreWebView2.ExecuteScriptAsync(script); }
        catch { /* window closing */ }
    }

    private async Task<bool> EnsureAsync()
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
            core.SetVirtualHostNameToFolderMapping(WebViewEnv.Host, WebViewEnv.HostDir, CoreWebView2HostResourceAccessKind.Allow);
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.WebMessageReceived += OnMessage;
            core.NewWindowRequested += (_, e) => e.Handled = true;
            // Served over http so episodes that still use plain http addresses aren't blocked as mixed content.
            core.Navigate($"http://{WebViewEnv.Host}/{PageName}");
            _ready = true;
            return true;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            _failed = true;
            _failMessage = "Podcasts need Microsoft Edge WebView2 (built into Windows 11).";
            return false;
        }
        catch (Exception ex)
        {
            App.Log("Podcast player", ex);
            _failed = true;
            _failMessage = "The podcast player couldn't start on this PC.";
            return false;
        }
        finally
        {
            _initializing = false;
        }
    }

    private void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            double Num(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;
            switch (root.TryGetProperty("type", out var t) ? t.GetString() : null)
            {
                case "page":
                    _pageReady = true;
                    break;
                case "time":
                    TimeChanged?.Invoke(Num("time"), Num("duration"));
                    break;
                case "state":
                    PlayingChanged?.Invoke(root.TryGetProperty("playing", out var p) && p.ValueKind == JsonValueKind.True);
                    break;
                case "ended":
                    Ended?.Invoke();
                    break;
                case "error":
                    Failed?.Invoke(root.TryGetProperty("message", out var m) ? m.GetString() ?? "unknown error" : "unknown error");
                    break;
            }
        }
        catch (JsonException)
        {
            // ignore malformed messages
        }
    }

    private const string PageHtml = """
        <!doctype html>
        <html><head><meta charset="utf-8"><title>Satchel audio</title></head>
        <body style="margin:0;background:#000">
        <audio id="a" preload="auto"></audio>
        <script>
        const a = document.getElementById('a');
        const post = m => { try { window.chrome.webview.postMessage(m); } catch (e) { } };
        let startAt = 0, lastTick = 0;
        const dur = () => isFinite(a.duration) ? a.duration : 0;
        a.addEventListener('loadedmetadata', () => {
          if (startAt > 0 && startAt < dur() - 2) a.currentTime = startAt;
          startAt = 0;
          post({ type: 'time', time: a.currentTime, duration: dur() });
        });
        a.addEventListener('timeupdate', () => {
          const now = Date.now();
          if (now - lastTick < 900) return;
          lastTick = now;
          post({ type: 'time', time: a.currentTime, duration: dur() });
        });
        a.addEventListener('playing', () => post({ type: 'state', playing: true }));
        a.addEventListener('pause', () => { if (!a.ended) post({ type: 'state', playing: false }); });
        a.addEventListener('ended', () => post({ type: 'ended' }));
        a.addEventListener('error', () => {
          if (!a.getAttribute('src')) return;
          const names = { 1: 'loading was stopped', 2: 'a network error stopped the download', 3: "the audio couldn't be decoded", 4: "this audio format or address isn't supported" };
          post({ type: 'error', message: a.error ? (names[a.error.code] || 'error ' + a.error.code) : 'unknown error' });
        });
        function podLoad(url, start, rate, volume) {
          startAt = start || 0;
          a.defaultPlaybackRate = rate; a.playbackRate = rate; a.volume = volume;
          a.src = url;
          a.play().catch(err => { if (err && err.name !== 'AbortError') post({ type: 'error', message: String(err.message || err) }); });
        }
        function podResume() { a.play().catch(() => { }); }
        function podPause() { a.pause(); }
        function podSeek(t) { a.currentTime = t; post({ type: 'time', time: a.currentTime, duration: dur() }); }
        function podRate(r) { a.defaultPlaybackRate = r; a.playbackRate = r; }
        function podVolume(v) { a.volume = v; }
        function podStop() { a.pause(); a.removeAttribute('src'); a.load(); }
        post({ type: 'page' });
        </script>
        </body></html>
        """;
}
