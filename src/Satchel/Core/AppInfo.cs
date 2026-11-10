using System.Reflection;

namespace Satchel.Core;

public static class AppInfo
{
    /// <summary>Display name used across the UI. Change it here to rebrand the app.</summary>
    public const string Name = "Satchel";
    public const string Tagline = "Your portable organizer";

    /// <summary>The version from the project file (&lt;Version&gt; in Satchel.csproj), e.g. "1.3.0".</summary>
    public static string Version =>
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    /// <summary>
    /// The GitHub repository ("owner/name") updates come from, built in from &lt;SatchelUpdateRepository&gt; in Satchel.csproj.
    /// Settings → Updates can override it.
    /// </summary>
    public static string DefaultUpdateRepository { get; } =
        typeof(AppInfo).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().FirstOrDefault(a => a.Key == "UpdateRepository")?.Value ?? "";
}
