using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Satchel.Core;

namespace Satchel.Services;

/// <summary>A published version on GitHub (one "release").</summary>
public sealed record ReleaseInfo(Version Version, string Tag, string Title, string Notes, DateTime? PublishedAt, string PageUrl,
    string? ZipUrl, string? ZipName, long ZipSize, string? ChecksumUrl, string? Digest)
{
    /// <summary>Has a download Satchel can install: the zip and its SHA-256 checksum.</summary>
    public bool CanInstall => ZipUrl != null && (ChecksumUrl != null || Digest != null);
    public string VersionText => Version.ToString(3);
}

/// <summary>What a check found.</summary>
public sealed record UpdateCheck(bool Ok, string Message, ReleaseInfo? Available);

/// <summary>
/// Updates from GitHub releases: checks the repository's releases (also the "What's new" feed), downloads the zip and
/// verifies its SHA-256 checksum, swaps the app files and restarts. The Data folder is never touched.
/// <para>
/// A running program can't be overwritten on Windows, but it can be renamed: every replaced file is renamed to
/// "*.old" first and the new one copied in its place. The new version deletes the old files once the old one has closed
/// (it's started with "--wait-for &lt;pid&gt;"). If anything fails halfway, the renamed files are put back.
/// </para>
/// </summary>
public sealed partial class UpdateService
{
    private static readonly HttpClient Http = CreateClient();
    private readonly SettingsService _settings;

    public UpdateService(SettingsService settings)
    {
        _settings = settings;
        Releases = ReadCache();
    }

    /// <summary>The releases found by the last check (kept in Data\Updates for offline viewing), newest first.</summary>
    public IReadOnlyList<ReleaseInfo> Releases { get; private set; }

    public static Version CurrentVersion { get; } = Version.Parse(AppInfo.Version);

    public static string UpdatesDirectory => Path.Combine(AppPaths.DataDirectory, "Updates");
    private static string CachePath => Path.Combine(UpdatesDirectory, "releases.json");
    private static string MarkerPath => Path.Combine(UpdatesDirectory, "installed.json");

    /// <summary>Set at start when this run follows an update: (from, to).</summary>
    public static (string From, string To)? JustUpdated { get; private set; }

    /// <summary>"owner/name" from Settings, else the one built into the app (see the csproj). Empty when neither is set.</summary>
    public string Repository => NormalizeRepository(_settings.Current.UpdateRepository) ?? NormalizeRepository(AppInfo.DefaultUpdateRepository) ?? "";

    public string ReleasesPageUrl => Repository.Length > 0 ? $"https://github.com/{Repository}/releases" : "";

    /// <summary>Accepts "owner/name", "github.com/owner/name" or a full https://github.com/… address (optionally ending in .git).</summary>
    public static string? NormalizeRepository(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var t = text.Trim();
        t = Regex.Replace(t, @"^(https?://)?(www\.)?github\.com/", "", RegexOptions.IgnoreCase);
        t = Regex.Replace(t, @"(\.git)?/*$", "", RegexOptions.IgnoreCase);
        var m = RepoRegex().Match(t);
        return m.Success ? $"{m.Groups[1].Value}/{m.Groups[2].Value}" : null;
    }

    [GeneratedRegex(@"^([A-Za-z0-9](?:[A-Za-z0-9-]{0,38}))/([A-Za-z0-9._-]{1,100})(?:/.*)?$")]
    private static partial Regex RepoRegex();

    private static string ApiBase
    {
        get
        {
#if DEBUG
            // Tests point the updater at a local stand-in for GitHub.
            var args = Environment.GetCommandLineArgs();
            var i = Array.FindIndex(args, a => string.Equals(a, "--debug-update-feed", StringComparison.OrdinalIgnoreCase));
            if (i >= 0 && i + 1 < args.Length) return args[i + 1].TrimEnd('/');
#endif
            return "https://api.github.com";
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            AllowAutoRedirect = true, // release downloads redirect to GitHub's file servers
            MaxAutomaticRedirections = 6
        };
        // Per-request timeouts below (a download can take minutes on a slow connection).
        var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        // GitHub's API asks for a User-Agent naming the app.
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"{AppInfo.Name}/{AppInfo.Version}");
        return http;
    }

    // ----- checking -----

    /// <summary>Is a check possible right now? (Online features on, a repository set.)</summary>
    public string? CannotCheckReason =>
        !_settings.Current.OnlineFeatures ? "Online features are off, so Satchel doesn't use the internet."
        : Repository.Length == 0 ? "Enter the GitHub repository Satchel should get its updates from."
        : null;

    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        if (CannotCheckReason is { } reason) return new(false, reason, null);
        if (!NetworkInterface.GetIsNetworkAvailable()) return new(false, "You're offline — connect to the internet and try again.", null);
        var url = $"{ApiBase}/repos/{Repository}/releases?per_page=20";
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            request.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");
            // (No ConfigureAwait(false): the results land on the UI thread — settings and the feed are bound to the UI.)
            using var response = await Http.SendAsync(request, timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(false, $"GitHub has no public repository “{Repository}”. Check the name — and that the repository is public.", null);
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return new(false, "GitHub's limit for update checks from this network was reached. Try again in an hour.", null);
            if (!response.IsSuccessStatusCode)
                return new(false, $"GitHub answered “{(int)response.StatusCode} {response.ReasonPhrase}”. Try again later.", null);
            var json = await response.Content.ReadAsStringAsync(timeout.Token);
            var releases = Parse(json);
            Releases = releases;
            WriteCache(json);
            _settings.Current.LastUpdateCheck = DateTime.Now;
            var newest = releases.Where(r => r.CanInstall).MaxBy(r => r.Version);
            if (releases.Count == 0) return new(true, "No versions have been published in this repository yet.", null);
            if (newest == null || newest.Version <= CurrentVersion)
                return new(true, $"You're up to date — {AppInfo.Name} {AppInfo.Version} is the newest version.", null);
            return new(true, $"{AppInfo.Name} {newest.VersionText} is available.", newest);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new(false, "GitHub didn't answer in time. Try again in a moment.", null);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or IOException)
        {
            App.Log("Update check", ex);
            return new(false, "Couldn't reach GitHub. Check your internet connection and try again.", null);
        }
    }

    /// <summary>The newest installable release from the last check, if it's newer than this version.</summary>
    public ReleaseInfo? AvailableUpdate => Releases.Where(r => r.CanInstall && r.Version > CurrentVersion).MaxBy(r => r.Version);

    /// <summary>Reads GitHub's release list (drafts and pre-releases are left out).</summary>
    public static List<ReleaseInfo> Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<ReleaseInfo>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var r in doc.RootElement.EnumerateArray())
        {
            if (Bool(r, "draft") || Bool(r, "prerelease")) continue;
            var tag = Str(r, "tag_name") ?? "";
            if (ParseVersion(tag) is not { } version) continue;
            string? zipUrl = null, zipName = null, sumUrl = null, digest = null;
            long size = 0;
            var assets = r.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : [];
            // The app's zip: the one for 64-bit Windows if there are several.
            var zip = assets.Where(x => (Str(x, "name") ?? "").EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            .OrderByDescending(x => (Str(x, "name") ?? "").Contains("win", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();
            if (zip.ValueKind == JsonValueKind.Object)
            {
                zipName = Str(zip, "name");
                zipUrl = Str(zip, "browser_download_url");
                size = zip.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                // GitHub reports a checksum for assets itself ("sha256:…"); a "<zip>.sha256" file works too.
                if (Str(zip, "digest") is { } d && d.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)) digest = d[7..].Trim().ToLowerInvariant();
                var sum = assets.FirstOrDefault(x => string.Equals(Str(x, "name"), zipName + ".sha256", StringComparison.OrdinalIgnoreCase));
                if (sum.ValueKind != JsonValueKind.Object)
                    sum = assets.FirstOrDefault(x => (Str(x, "name") ?? "") is var n2 && (n2.EndsWith(".sha256", StringComparison.OrdinalIgnoreCase) || n2.Equals("SHA256SUMS", StringComparison.OrdinalIgnoreCase) || n2.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)));
                if (sum.ValueKind == JsonValueKind.Object) sumUrl = Str(sum, "browser_download_url");
            }
            DateTime? published = DateTime.TryParse(Str(r, "published_at"), System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out var p) ? p.ToLocalTime() : null;
            var title = Str(r, "name");
            list.Add(new ReleaseInfo(version, tag, string.IsNullOrWhiteSpace(title) ? $"{AppInfo.Name} {version.ToString(3)}" : title.Trim(),
                (Str(r, "body") ?? "").Replace("\r", ""), published, Str(r, "html_url") ?? "", zipUrl, zipName, size, sumUrl, digest));
        }
        return list.OrderByDescending(x => x.Version).ToList();
    }

    /// <summary>"v1.3.1", "1.3.1" or "Satchel-1.3" → the version; null if there's no number.</summary>
    public static Version? ParseVersion(string tag)
    {
        var m = Regex.Match(tag ?? "", @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    private static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static IReadOnlyList<ReleaseInfo> ReadCache()
    {
        try { return File.Exists(CachePath) ? Parse(File.ReadAllText(CachePath)) : []; }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private static void WriteCache(string json)
    {
        try
        {
            Directory.CreateDirectory(UpdatesDirectory);
            File.WriteAllText(CachePath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log("Update cache", ex); }
    }

    // ----- downloading -----

    /// <summary>Can this copy of Satchel replace its own files? Null when it can, else why not.</summary>
    public static string? CannotInstallReason()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) return "Satchel couldn't find its own program file.";
        var dir = AppPaths.AppDirectory;
        // A development build runs from Satchel.dll next to a small launcher; the release is one Satchel.exe.
        if (File.Exists(Path.Combine(dir, AppInfo.Name + ".dll")))
            return "This is a development build. Updates install into the published app (see “GitHub and update instructions.md”).";
        try
        {
            var probe = Path.Combine(dir, $".update-probe-{Environment.ProcessId}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "Satchel can't write to its own folder (is the drive write-protected, or is it in Program Files?).";
        }
        return null;
    }

    /// <summary>Downloads the release, checks its SHA-256 and unpacks it into Data\Updates. Returns the unpacked folder.</summary>
    public async Task<string> DownloadAsync(ReleaseInfo release, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        if (!release.CanInstall) throw new UpdateException("This release has no download with a checksum, so Satchel won't install it.");
        Directory.CreateDirectory(UpdatesDirectory);
        var expected = release.Digest;
        if (release.ChecksumUrl != null)
        {
            using var sumTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            sumTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            var text = await GetTextAsync(release.ChecksumUrl, sumTimeout.Token).ConfigureAwait(false);
            expected = ReadChecksum(text, release.ZipName!) ?? throw new UpdateException("The checksum file of this release couldn't be read.");
        }

        var zipPath = Path.Combine(UpdatesDirectory, SafeName(release.ZipName!));
        var part = zipPath + ".part";
        using (var request = new HttpRequestMessage(HttpMethod.Get, release.ZipUrl))
        {
            request.Headers.Accept.ParseAdd("application/octet-stream");
            using var headersTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            headersTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headersTimeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new UpdateException($"The download failed ({(int)response.StatusCode} {response.ReasonPhrase}).");
            long total = response.Content.Headers.ContentLength ?? release.ZipSize;
            if (total > 2L * 1024 * 1024 * 1024) throw new UpdateException("The download is unexpectedly large — not installing it.");
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
            using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[1 << 16];
                long done = 0;
                var lastReport = Stopwatch.StartNew();
                while (true)
                {
                    // A stalled connection gives up after a minute without data.
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(TimeSpan.FromSeconds(60));
                    int read;
                    try { read = await source.ReadAsync(buffer, stall.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new UpdateException("The download stalled. Check your connection and try again."); }
                    if (read == 0) break;
                    await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    if (lastReport.ElapsedMilliseconds > 120) { progress?.Report((done, total)); lastReport.Restart(); }
                }
                progress?.Report((done, Math.Max(total, done)));
                var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                {
                    file.Close();
                    TryDelete(part);
                    throw new UpdateException("The download doesn't match its checksum (it may be damaged or incomplete), so it wasn't installed. Try again.");
                }
            }
        }
        TryDelete(zipPath);
        File.Move(part, zipPath);
        return await Task.Run(() => Unpack(zipPath, release), ct).ConfigureAwait(false);
    }

    private static async Task<string> GetTextAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new UpdateException($"The checksum file couldn't be downloaded ({(int)response.StatusCode}).");
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>"&lt;hash&gt;", "&lt;hash&gt;  file.zip" or a list of such lines (the one naming the zip wins).</summary>
    public static string? ReadChecksum(string text, string zipName)
    {
        string? first = null;
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            var m = Regex.Match(line, @"\b([0-9a-fA-F]{64})\b");
            if (!m.Success) continue;
            if (line.Contains(zipName, StringComparison.OrdinalIgnoreCase)) return m.Groups[1].Value.ToLowerInvariant();
            first ??= m.Groups[1].Value.ToLowerInvariant();
        }
        return first;
    }

    /// <summary>Unpacks the zip into Data\Updates\staged-&lt;version&gt; (a single top folder in the zip is skipped).</summary>
    private static string Unpack(string zipPath, ReleaseInfo release)
    {
        var staged = Path.Combine(UpdatesDirectory, "staged-" + release.VersionText);
        if (Directory.Exists(staged)) Directory.Delete(staged, recursive: true);
        Directory.CreateDirectory(staged);
        var root = Path.GetFullPath(staged) + Path.DirectorySeparatorChar;
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var files = zip.Entries.Where(e => e.Name.Length > 0).ToList();
            var names = files.Select(e => e.FullName.Replace('\\', '/')).ToList();
            // "Satchel/Satchel.exe, Satchel/…" → strip the "Satchel/" folder.
            var tops = names.Select(n => n.Contains('/') ? n[..n.IndexOf('/')] : "").Distinct().ToList();
            var prefix = tops.Count == 1 && tops[0].Length > 0 ? tops[0] + "/" : "";
            for (int i = 0; i < files.Count; i++)
            {
                var rel = names[i][prefix.Length..];
                // Never anything for the data folder, whatever the zip contains.
                if (rel.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)) continue;
                var target = Path.GetFullPath(Path.Combine(staged, rel));
                if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new UpdateException("The download contains an unsafe file path — not installing it.");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                files[i].ExtractToFile(target, overwrite: true);
            }
        }
        if (!File.Exists(Path.Combine(staged, AppInfo.Name + ".exe")))
            throw new UpdateException($"The download doesn't contain {AppInfo.Name}.exe — is it the right file?");
        TryDelete(zipPath);
        return staged;
    }

    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    // ----- installing -----

    /// <summary>What an update left for the next start: which version it was, and the replaced files to delete.</summary>
    private sealed class InstallMarker
    {
        public string? From { get; set; }
        public string? To { get; set; }
        /// <summary>Paths relative to the app folder — only these are ever deleted afterwards.</summary>
        public List<string> Backups { get; set; } = [];
    }

    /// <summary>
    /// Puts the unpacked files in place of the current ones (slow on a flash drive: run it off the UI thread).
    /// Throws, with everything put back, if a file can't be replaced. Then <see cref="StartNewVersion"/> and close this window.
    /// </summary>
    public static void InstallFiles(string staged, ReleaseInfo release)
    {
        if (CannotInstallReason() is { } reason) throw new UpdateException(reason);
        var appDir = AppPaths.AppDirectory;
        var exeName = Path.GetFileName(Environment.ProcessPath!);
        var data = Path.GetFullPath(AppPaths.DataDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var moved = new List<(string Target, string Backup)>();
        var copied = new List<string>();
        try
        {
            foreach (var file in Directory.EnumerateFiles(staged, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(staged, file);
                // A renamed program file (e.g. "Organizer.exe") gets the new version under its own name.
                if (string.Equals(rel, AppInfo.Name + ".exe", StringComparison.OrdinalIgnoreCase)) rel = exeName;
                var target = Path.GetFullPath(Path.Combine(appDir, rel));
                if (target.StartsWith(data, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                if (File.Exists(target))
                {
                    if (SameFile(file, target)) continue;
                    var backup = target + ".old";
                    if (File.Exists(backup) && !TryDelete(backup)) backup = target + $".old-{DateTime.Now.Ticks}";
                    File.Move(target, backup);
                    moved.Add((target, backup));
                }
                File.Copy(file, target);
                copied.Add(target);
            }
        }
        catch (Exception ex)
        {
            App.Log("Update install", ex);
            foreach (var c in copied) TryDelete(c);
            foreach (var (target, backup) in moved) { try { File.Move(backup, target, overwrite: true); } catch (Exception) { /* best effort */ } }
            throw new UpdateException(ex is UnauthorizedAccessException or IOException
                ? $"A file couldn't be replaced ({ex.Message}). Nothing was changed — close other programs using Satchel's folder and try again."
                : "The update couldn't be installed: " + ex.Message);
        }

        WriteMarker(new InstallMarker { From = AppInfo.Version, To = release.VersionText, Backups = moved.Select(m => Path.GetRelativePath(appDir, m.Backup)).ToList() });
    }

    private static void WriteMarker(InstallMarker marker)
    {
        try
        {
            Directory.CreateDirectory(UpdatesDirectory);
            File.WriteAllText(MarkerPath, JsonSerializer.Serialize(marker));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log("Update marker", ex); }
    }

    /// <summary>Starts the (just installed) program; it waits until this one has closed.</summary>
    public static void StartNewVersion()
    {
        var appDir = AppPaths.AppDirectory;
        var start = new ProcessStartInfo(Path.Combine(appDir, Path.GetFileName(Environment.ProcessPath!))) { UseShellExecute = false, WorkingDirectory = appDir };
        foreach (var a in RestartArguments()) start.ArgumentList.Add(a);
        start.ArgumentList.Add("--wait-for");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Process.Start(start);
    }

    /// <summary>The new version starts like this one did: the same data folder (and, in Debug builds, the test options).</summary>
    public static List<string> RestartArguments()
    {
        var args = Environment.GetCommandLineArgs().Skip(1).ToList();
        var keep = new List<string>();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i];
            bool hasValue = i + 1 < args.Count;
            if (a.Equals("--data", StringComparison.OrdinalIgnoreCase) && hasValue) { keep.Add(a); keep.Add(args[++i]); }
#if DEBUG
            else if (a.Equals("--debug-update-test", StringComparison.OrdinalIgnoreCase) && hasValue) { keep.Add("--debug-after-update"); keep.Add(args[++i]); }
            else if ((a.Equals("--debug-password", StringComparison.OrdinalIgnoreCase) || a.Equals("--debug-update-feed", StringComparison.OrdinalIgnoreCase)) && hasValue) { keep.Add(a); keep.Add(args[++i]); }
            else if (a.Equals("--debug-exit", StringComparison.OrdinalIgnoreCase)) keep.Add(a);
#endif
        }
        return keep;
    }

    private static bool SameFile(string a, string b)
    {
        var fa = new FileInfo(a);
        var fb = new FileInfo(b);
        if (fa.Length != fb.Length) return false;
        using var sa = fa.OpenRead();
        using var sb = fb.OpenRead();
        return SHA256.HashData(sa).AsSpan().SequenceEqual(SHA256.HashData(sb));
    }

    private static bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    // ----- after an update (runs first thing at start) -----

    /// <summary>
    /// "--wait-for &lt;pid&gt;": the previous version is still closing — wait for it (it holds the data folder).
    /// Then the replaced files ("*.old") and the downloaded files are cleaned up.
    /// </summary>
    public static void PrepareStartup(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, "--wait-for", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var pid))
        {
            try
            {
                using var previous = Process.GetProcessById(pid);
                previous.WaitForExit(60_000);
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { /* already gone */ }
        }
        InstallMarker? marker = null;
        try
        {
            if (File.Exists(MarkerPath)) marker = JsonSerializer.Deserialize<InstallMarker>(File.ReadAllText(MarkerPath));
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { App.Log("Update marker", ex); }
        if (marker != null)
        {
            if (marker.To == AppInfo.Version && marker.From != null) JustUpdated = (marker.From, marker.To);
            // Only the files the update itself renamed are deleted — nothing else in the app's folder.
            var root = Path.GetFullPath(AppPaths.AppDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var left = marker.Backups.Where(rel =>
            {
                var path = Path.GetFullPath(Path.Combine(root, rel));
                return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Regex.IsMatch(path, @"\.old(-\d+)?$", RegexOptions.IgnoreCase) && !TryDelete(path);
            }).ToList();
            // Still in use (the old version hadn't closed yet)? Try again next time.
            if (left.Count > 0) WriteMarker(new InstallMarker { Backups = left });
            else TryDelete(MarkerPath);
        }
        CleanUp();
    }

    /// <summary>Removes what the last update downloaded and unpacked (keeps the feed).</summary>
    private static void CleanUp()
    {
        try
        {
            if (Directory.Exists(UpdatesDirectory))
            {
                foreach (var dir in Directory.EnumerateDirectories(UpdatesDirectory, "staged-*")) { try { Directory.Delete(dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
                foreach (var file in Directory.EnumerateFiles(UpdatesDirectory).Where(f => f.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".part", StringComparison.OrdinalIgnoreCase)))
                    TryDelete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { App.Log("Update clean-up", ex); }
    }
}

/// <summary>An update step failed; the message is meant for the person using Satchel.</summary>
public sealed class UpdateException(string message) : Exception(message);

/// <summary>Release notes (Markdown) as simple lines for the "What's new" feed.</summary>
public static partial class ReleaseNotes
{
    public enum Kind { Heading, Bullet, Text }

    public sealed record Line(Kind Kind, string Text, int Level = 0);

    public static List<Line> Parse(string markdown)
    {
        var lines = new List<Line>();
        bool afterBlank = true;
        foreach (var raw in (markdown ?? "").Replace("\r", "").Split('\n'))
        {
            var t = raw.TrimEnd();
            if (t.Trim().Length == 0 || t.Trim() is "---" or "***" or "___") { afterBlank = true; continue; }
            var h = Regex.Match(t, @"^\s{0,3}#{1,6}\s+(.+?)\s*#*$");
            var b = Regex.Match(t, @"^(\s*)(?:[-*+]|\d+[.)])\s+(?:\[[ xX]\]\s+)?(.+)$");
            if (h.Success) lines.Add(new(Kind.Heading, Clean(h.Groups[1].Value)));
            else if (b.Success) lines.Add(new(Kind.Bullet, Clean(b.Groups[2].Value), Math.Min(2, b.Groups[1].Value.Length / 2)));
            // The next line of a paragraph or of a wrapped list item (no blank line in between) joins it.
            else if (!afterBlank && lines.Count > 0 && lines[^1].Kind is Kind.Bullet or Kind.Text)
                lines[^1] = lines[^1] with { Text = lines[^1].Text + " " + Clean(t.Trim()) };
            else lines.Add(new(Kind.Text, Clean(t.Trim())));
            afterBlank = false;
        }
        return lines;
    }

    /// <summary>Markdown markup goes, the words stay.</summary>
    private static string Clean(string s)
    {
        s = Regex.Replace(s, @"!\[[^\]]*\]\([^)]*\)", "");
        s = Regex.Replace(s, @"\[([^\]]+)\]\([^)]*\)", "$1");
        s = Regex.Replace(s, @"(\*\*|__)(.+?)\1", "$2");
        s = Regex.Replace(s, @"(?<![\w*])[*_](\S(?:.*?\S)?)[*_](?![\w*])", "$1");
        s = s.Replace("`", "");
        s = Regex.Replace(s, @"^>\s?", "");
        return s.Trim();
    }
}
