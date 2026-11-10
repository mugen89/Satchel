using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;

namespace Satchel.Security;

/// <summary>
/// Copies text to the clipboard. Sensitive values are excluded from Windows clipboard history /
/// cloud sync and are wiped after a configurable delay (only if they're still on the clipboard).
/// </summary>
public static class SecureClipboard
{
    private static DispatcherTimer? _timer;
    private static string? _sensitiveValue;

    public static int ClearAfterSeconds { get; set; } = 30;

    public static event Action<string>? Copied;

    public static bool Copy(string? text, bool sensitive, string? what = null)
    {
        if (string.IsNullOrEmpty(text)) return false;
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        if (sensitive)
        {
            data.SetData("ExcludeClipboardContentFromMonitorProcessing", new MemoryStream(new byte[] { 0 }));
            data.SetData("CanIncludeInClipboardHistory", new MemoryStream(BitConverter.GetBytes(0)));
            data.SetData("CanUploadToCloudClipboard", new MemoryStream(BitConverter.GetBytes(0)));
        }

        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                break;
            }
            catch (COMException) when (attempt < 5)
            {
                Thread.Sleep(40);
            }
            catch (COMException)
            {
                return false;
            }
        }

        if (sensitive)
        {
            _sensitiveValue = text;
            if (ClearAfterSeconds > 0)
            {
                _timer?.Stop();
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(ClearAfterSeconds) };
                _timer.Tick += (_, _) => { _timer?.Stop(); ClearIfOurs(); };
                _timer.Start();
            }
        }
        Copied?.Invoke(what ?? "Copied");
        return true;
    }

    /// <summary>Wipes the clipboard if it still holds the last sensitive value we put there.</summary>
    public static void ClearIfOurs()
    {
        if (_sensitiveValue == null) return;
        try
        {
            if (Clipboard.ContainsText() && Clipboard.GetText() == _sensitiveValue)
                Clipboard.Clear();
        }
        catch (COMException) { }
        _sensitiveValue = null;
    }
}
