using System.IO;

namespace Satchel.Core;

/// <summary>
/// Everything lives next to the executable so the whole folder can be carried on a flash drive.
/// Nothing is written to AppData or the registry.
/// </summary>
public static class AppPaths
{
    public static string AppDirectory { get; private set; } = AppContext.BaseDirectory;
    public static string DataDirectory { get; private set; } = Path.Combine(AppContext.BaseDirectory, "Data");

    public static string DatabasePath => Path.Combine(DataDirectory, "satchel.db");
    public static string BackupDirectory => Path.Combine(DataDirectory, "Backups");
    public static string LogDirectory => Path.Combine(DataDirectory, "Logs");
    public static string ExportDirectory => Path.Combine(DataDirectory, "Exports");

    /// <summary>Supports "--data &lt;folder&gt;" to point the app at another data folder.</summary>
    public static void Initialize(string[] args)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "--data", StringComparison.OrdinalIgnoreCase))
            {
                var custom = args[i + 1];
                DataDirectory = Path.GetFullPath(Path.IsPathRooted(custom) ? custom : Path.Combine(AppDirectory, custom));
            }
        }
        Directory.CreateDirectory(DataDirectory);
    }

    public static string DriveLabel
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(AppDirectory);
                if (string.IsNullOrEmpty(root)) return "";
                var drive = new DriveInfo(root);
                var kind = drive.DriveType == DriveType.Removable ? "Removable drive" : drive.DriveType == DriveType.Network ? "Network drive" : "Local drive";
                return $"{kind} {root.TrimEnd('\\')}";
            }
            catch { return ""; }
        }
    }

    public static bool IsRemovableDrive
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(AppDirectory);
                return !string.IsNullOrEmpty(root) && new DriveInfo(root).DriveType == DriveType.Removable;
            }
            catch { return false; }
        }
    }

    public static string FreeSpaceText
    {
        get
        {
            try
            {
                var root = Path.GetPathRoot(AppDirectory);
                if (string.IsNullOrEmpty(root)) return "";
                var d = new DriveInfo(root);
                return $"{Format.Bytes(d.AvailableFreeSpace)} free of {Format.Bytes(d.TotalSize)}";
            }
            catch { return ""; }
        }
    }
}
