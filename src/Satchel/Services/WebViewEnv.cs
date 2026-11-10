using System.IO;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Web.WebView2.Core;
using Satchel.Core;

namespace Satchel.Services;

/// <summary>
/// One WebView2 environment for every in-app browser surface (video player, music, board study panel). Its profile lives in
/// <c>Data\WebView2</c> next to the app, and pages are served from a virtual host so YouTube sees a proper origin.
/// All WebView2 controls in a process must share identical environment options, hence the single cached instance.
/// </summary>
public static class WebViewEnv
{
    public const string Host = "satchel.player";
    private static Task<CoreWebView2Environment>? _environment;

    public static string Folder => Path.Combine(AppPaths.DataDirectory, "WebView2");
    public static string HostDir => Path.Combine(Folder, "host");

    public static Task<CoreWebView2Environment> GetAsync() => _environment ??= CreateAsync();

    private static async Task<CoreWebView2Environment> CreateAsync()
    {
        Directory.CreateDirectory(HostDir);
        var options = new CoreWebView2EnvironmentOptions("--disk-cache-size=67108864 --autoplay-policy=no-user-gesture-required");
        try
        {
            return await CoreWebView2Environment.CreateAsync(null, Folder, options);
        }
        catch
        {
            _environment = null; // let a later attempt try again (e.g. after installing the runtime)
            throw;
        }
    }

    /// <summary>Writes a page into the virtual host folder (overwritten on every start so updates apply).</summary>
    public static async Task WritePageAsync(string name, string html)
    {
        Directory.CreateDirectory(HostDir);
        var path = Path.Combine(HostDir, name);
        try
        {
            if (File.Exists(path) && await File.ReadAllTextAsync(path) == html) return;
            await File.WriteAllTextAsync(path, html);
        }
        catch (IOException)
        {
            // another player is writing the same file right now — the content is identical
        }
    }

    /// <summary>Common settings for a player surface: host mapping, no browser chrome, ad blocking, links open outside.</summary>
    public static void Configure(CoreWebView2 core, CoreWebView2Environment env)
    {
        core.SetVirtualHostNameToFolderMapping(Host, HostDir, CoreWebView2HostResourceAccessKind.Allow);
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        AdBlock.Attach(core, env);
    }
}

/// <summary>
/// Ad blocking for the embedded YouTube players: ad and tracking requests are refused, the ad entries are stripped from
/// the player's data before it reads them, and any ad that still starts is muted and skipped.
/// Controlled by Settings → Sidebar &amp; online → "Block ads in the video player".
/// </summary>
public static class AdBlock
{
    private static readonly string[] Patterns =
    [
        "*://*.doubleclick.net/*",
        "*://*.googlesyndication.com/*",
        "*://*.googleadservices.com/*",
        "*://*.google-analytics.com/*",
        "*://*.googletagservices.com/*",
        "*://imasdk.googleapis.com/*",
        "*://*.2mdn.net/*",
        "*://*.youtube.com/api/stats/ads*",
        "*://*.youtube-nocookie.com/api/stats/ads*",
        "*://*.youtube.com/pagead/*",
        "*://*.youtube-nocookie.com/pagead/*",
        "*://*.youtube.com/ptracking*",
        "*://*.youtube-nocookie.com/ptracking*",
        "*://*.youtube.com/get_midroll_*",
        "*://*.youtube-nocookie.com/get_midroll_*",
        "*://*.youtube.com/youtubei/v1/log_event*",
        "*://*.youtube-nocookie.com/youtubei/v1/log_event*",
    ];

    /// <summary>Runs in every frame (including YouTube's own iframe) before the page's scripts.</summary>
    public const string Script = """
(() => {
  if (!/(^|\.)youtube(-nocookie)?\.com$/.test(location.hostname)) return;
  const KEYS = ['adPlacements', 'adSlots', 'playerAds', 'adBreakHeartbeatParams', 'adBreakParams'];
  const prune = o => {
    try {
      if (o && typeof o === 'object') {
        for (const k of KEYS) if (k in o) delete o[k];
        if (o.playerResponse && typeof o.playerResponse === 'object') prune(o.playerResponse);
      }
    } catch (e) {}
    return o;
  };
  const parse = JSON.parse;
  JSON.parse = function () { return prune(parse.apply(this, arguments)); };
  const json = Response.prototype.json;
  Response.prototype.json = function () { return json.apply(this, arguments).then(prune); };
  let initial;
  try {
    Object.defineProperty(window, 'ytInitialPlayerResponse', { configurable: true, get() { return initial; }, set(v) { initial = prune(v); } });
  } catch (e) {}
  let silenced = false;
  setInterval(() => {
    const p = document.querySelector('.html5-video-player');
    const v = p && p.querySelector('video');
    if (p && p.classList.contains('ad-showing')) {
      if (v) {
        if (!silenced) { silenced = true; v.dataset.satchelMuted = v.muted ? '1' : '0'; }
        v.muted = true;
        if (isFinite(v.duration) && v.duration > 0 && v.currentTime < v.duration - 0.2) v.currentTime = v.duration - 0.1;
      }
      document.querySelectorAll('.ytp-ad-skip-button, .ytp-ad-skip-button-modern, .ytp-skip-ad-button, .videoAdUiSkipButton, .ytp-ad-skip-button-slot button')
        .forEach(b => { try { b.click(); } catch (e) {} });
    } else if (silenced && v) {
      silenced = false;
      v.muted = v.dataset.satchelMuted === '1';
    }
    document.querySelectorAll('.ytp-ad-overlay-container, .ytp-ad-overlay-slot, .ytp-ad-image-overlay, .ytp-ad-text-overlay')
      .forEach(el => { try { el.style.display = 'none'; } catch (e) {} });
  }, 250);
})();
""";

    private static bool Enabled => App.Services.GetService<SettingsService>()?.Current.BlockVideoAds ?? true;

    public static void Attach(CoreWebView2 core, CoreWebView2Environment env)
    {
        foreach (var pattern in Patterns)
        {
            try
            {
                core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All, CoreWebView2WebResourceRequestSourceKinds.All);
            }
            catch (Exception)
            {
                // older runtimes: frame/worker requests can't be filtered, the document ones still are
                try { core.AddWebResourceRequestedFilter(pattern, CoreWebView2WebResourceContext.All); } catch { }
            }
        }
        core.WebResourceRequested += (_, e) =>
        {
            if (!Enabled) return;
            e.Response = env.CreateWebResourceResponse(null, 204, "No Content", "");
        };
        if (Enabled) _ = core.AddScriptToExecuteOnDocumentCreatedAsync(Script);
    }
}
