using System.ComponentModel;
using System.Text.Json;
using System.Windows.Threading;
using Satchel.Core;
using Satchel.Data;
using Satchel.Security;

namespace Satchel.Services;

public sealed class SettingsService
{
    private const string Key = "app.settings";
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly DataStore _store;
    private readonly DispatcherTimer _saveTimer;

    public AppSettings Current { get; private set; } = new();

    public event Action<string>? Changed;

    public SettingsService(DataStore store)
    {
        _store = store;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _saveTimer.Tick += async (_, _) => { _saveTimer.Stop(); await SaveAsync(); };
    }

    public void Load()
    {
        try
        {
            var json = _store.GetValue(Key);
            if (!string.IsNullOrWhiteSpace(json))
                Current = JsonSerializer.Deserialize<AppSettings>(json, Json) ?? new AppSettings();
        }
        catch
        {
            Current = new AppSettings();
        }
        Current.PropertyChanged += OnChanged;
        Current.Generator.PropertyChanged += (_, _) => ScheduleSave();
        ApplyGlobals();
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        ApplyGlobals();
        ScheduleSave();
        Changed?.Invoke(e.PropertyName ?? "");
    }

    private void ApplyGlobals()
    {
        Format.Use24Hour = Current.Use24HourClock;
        Format.DatePattern = Current.DateFormat;
        Format.WeekStartsMonday = Current.WeekStartsMonday;
        SecureClipboard.ClearAfterSeconds = Current.ClipboardClearSeconds;
        Anim.Enabled = Current.AnimationsEnabled;
    }

    public void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    /// <summary>Background save (the UI never waits for the drive).</summary>
    public async Task SaveAsync()
    {
        try
        {
            await _store.SetValueAsync(Key, JsonSerializer.Serialize(Current, Json));
        }
        catch
        {
            // Settings are non-critical; ignore transient write failures (e.g. drive busy).
        }
    }

    public void SaveNow()
    {
        _saveTimer.Stop();
        try
        {
            _store.SetValue(Key, JsonSerializer.Serialize(Current, Json));
        }
        catch
        {
            // Settings are non-critical; ignore transient write failures (e.g. drive busy).
        }
    }

    public string? GetValue(string key) => _store.GetValue(key);
    public void SetValue(string key, string? value) => _store.SetValue(key, value);
}
