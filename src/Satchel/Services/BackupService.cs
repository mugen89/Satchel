using System.Diagnostics;
using System.IO;
using System.Windows;
using Microsoft.Data.Sqlite;
using Satchel.Core;
using Satchel.Data;

namespace Satchel.Services;

public sealed record BackupInfo(string Path, string Name, DateTime Created, long Size)
{
    public string SizeText => Format.Bytes(Size);
    public string CreatedText => Format.DateAndTime(Created);
    public string Kind => Name.Contains("-auto") ? "Automatic" : Name.Contains("-pre-restore") ? "Before restore" : "Manual";
}

/// <summary>
/// Backups are full copies of the (already encrypted) database made with SQLite's online
/// backup API, so they are safe to take while the app is running.
/// </summary>
public sealed class BackupService
{
    private readonly DataStore _store;
    private readonly SettingsService _settings;

    public BackupService(DataStore store, SettingsService settings)
    {
        _store = store;
        _settings = settings;
    }

    public Task<string> CreateBackupAsync(string tag = "") => _store.RunAsync(_ => CreateBackupCore(tag));

    private string CreateBackupCore(string tag)
    {
        Directory.CreateDirectory(AppPaths.BackupDirectory);
        var name = $"satchel-{DateTime.Now:yyyyMMdd-HHmmss}{(string.IsNullOrEmpty(tag) ? "" : "-" + tag)}.db";
        var path = Path.Combine(AppPaths.BackupDirectory, name);
        using (var source = new SqliteConnection(AppDbContext.ConnectionString))
        using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()))
        {
            source.Open();
            target.Open();
            source.BackupDatabase(target);
        }
        Prune();
        return path;
    }

    public IReadOnlyList<BackupInfo> ListBackups()
    {
        try
        {
            if (!Directory.Exists(AppPaths.BackupDirectory)) return [];
            return new DirectoryInfo(AppPaths.BackupDirectory).GetFiles("*.db")
                .OrderByDescending(f => f.CreationTime)
                .Select(f => new BackupInfo(f.FullName, f.Name, f.CreationTime, f.Length))
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private void Prune()
    {
        int keep = Math.Max(1, _settings.Current.BackupKeepCount);
        foreach (var old in ListBackups().Skip(keep))
        {
            try { File.Delete(old.Path); } catch { }
        }
    }

    public async Task AutoBackupIfDueAsync()
    {
        if (!_settings.Current.AutoBackup) return;
        var last = _settings.Current.LastBackupAt;
        if (last.HasValue && DateTime.Now - last.Value < TimeSpan.FromHours(20)) return;
        try
        {
            await Task.Delay(4000); // let the UI settle first
            await CreateBackupAsync("auto");
            _settings.Current.LastBackupAt = DateTime.Now;
        }
        catch
        {
            // Never bother the user about a failed background backup (e.g. drive removed).
        }
    }

    /// <summary>Copies a backup over the live database and restarts the app.</summary>
    public async Task RestoreAsync(string backupPath)
    {
        await CreateBackupAsync("pre-restore");
        await _store.RunAsync(_ =>
        {
            SqliteConnection.ClearAllPools();
            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = backupPath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
            using var target = new SqliteConnection(AppDbContext.ConnectionString);
            source.Open();
            target.Open();
            source.BackupDatabase(target);
        });
        Restart();
    }

    public static void Restart()
    {
        var exe = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exe))
        {
            var start = new ProcessStartInfo(exe) { UseShellExecute = false };
            var args = Environment.GetCommandLineArgs().Skip(1).ToList();
            for (int i = 0; i < args.Count; i++)
            {
                if (args[i].Equals("--wait-for", StringComparison.OrdinalIgnoreCase)) { i++; continue; } // from an earlier restart
                start.ArgumentList.Add(args[i]);
            }
            // The new copy waits until this one has let go of the data folder (otherwise it could see it still open and quit).
            start.ArgumentList.Add("--wait-for");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Process.Start(start);
        }
        Application.Current.Shutdown();
    }

    public static long DatabaseSize()
    {
        try { return new FileInfo(AppPaths.DatabasePath).Length; } catch { return 0; }
    }
}
