namespace Satchel.Core;

/// <summary>Process-wide "show private items" switch. Every new database context takes a snapshot of it.</summary>
public static class PrivacyState
{
    private static volatile bool _showPrivate;

    public static bool ShowPrivate
    {
        get => _showPrivate;
        set => _showPrivate = value;
    }
}
