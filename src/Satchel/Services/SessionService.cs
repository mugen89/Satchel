using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Satchel.Data;
using Satchel.Security;

namespace Satchel.Services;

/// <summary>Master password, unlock/lock lifecycle, recovery key and auto-lock on inactivity.</summary>
public sealed class SessionService
{
    private const string KeysKey = "security.keys";
    private const string HintKey = "security.hint";

    private readonly DataStore _store;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _idleTimer;
    private long _lastActivity = Environment.TickCount64;
    private Point _lastMouse;
    private byte[]? _recoveredKey;
    private bool _locking;

    public event Func<Task>? Locking;
    public event Action? Locked;
    public event Action? Unlocked;

    public SessionService(DataStore store, SettingsService settings)
    {
        _store = store;
        _settings = settings;
        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _idleTimer.Tick += OnIdleTick;
    }

    public bool IsSetUp => _store.GetValue(KeysKey) != null;
    public bool IsUnlocked => Crypto.IsUnlocked;
    public string? Hint => _store.GetValue(HintKey);
    public DateTime? PasswordChangedAt => LoadMaterial()?.PasswordChangedAt;

    private KeyMaterial? LoadMaterial()
    {
        var json = _store.GetValue(KeysKey);
        return string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<KeyMaterial>(json);
    }

    private void SaveMaterial(KeyMaterial m) => _store.SetValue(KeysKey, JsonSerializer.Serialize(m));

    public void SetHint(string? hint) => _store.SetValue(HintKey, string.IsNullOrWhiteSpace(hint) ? null : hint.Trim());

    /// <summary>First run: creates the data key and returns the recovery key to show the user.</summary>
    public async Task<string> CreateVaultAsync(string password, string? hint)
    {
        var (material, dataKey, recovery) = await Task.Run(() => KeyManager.Create(password));
        try
        {
            SaveMaterial(material);
            SetHint(hint);
            Crypto.SetKey(dataKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dataKey);
        }
        return recovery;
    }

    public async Task<bool> UnlockAsync(string password)
    {
        var m = LoadMaterial();
        if (m == null) return false;
        var key = await Task.Run(() => KeyManager.TryUnwrapWithPassword(m, password));
        if (key == null) return false;
        Crypto.SetKey(key);
        CryptographicOperations.ZeroMemory(key);
        return true;
    }

    public async Task<bool> VerifyRecoveryKeyAsync(string recoveryKey)
    {
        var m = LoadMaterial();
        if (m == null) return false;
        var key = await Task.Run(() => KeyManager.TryUnwrapWithRecovery(m, recoveryKey));
        if (key == null) return false;
        if (_recoveredKey != null) CryptographicOperations.ZeroMemory(_recoveredKey);
        _recoveredKey = key;
        return true;
    }

    public async Task<bool> ResetPasswordWithRecoveryAsync(string newPassword)
    {
        var m = LoadMaterial();
        if (m == null || _recoveredKey == null) return false;
        var key = _recoveredKey;
        await Task.Run(() => KeyManager.WrapWithPassword(m, key, newPassword));
        SaveMaterial(m);
        Crypto.SetKey(key);
        CryptographicOperations.ZeroMemory(key);
        _recoveredKey = null;
        return true;
    }

    public async Task<bool> ChangePasswordAsync(string currentPassword, string newPassword)
    {
        var m = LoadMaterial();
        if (m == null) return false;
        var key = await Task.Run(() => KeyManager.TryUnwrapWithPassword(m, currentPassword));
        if (key == null) return false;
        try
        {
            await Task.Run(() => KeyManager.WrapWithPassword(m, key, newPassword));
            SaveMaterial(m);
            return true;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<string?> RegenerateRecoveryKeyAsync(string currentPassword)
    {
        var m = LoadMaterial();
        if (m == null) return null;
        var key = await Task.Run(() => KeyManager.TryUnwrapWithPassword(m, currentPassword));
        if (key == null) return null;
        try
        {
            var recovery = KeyManager.GenerateRecoveryKey();
            await Task.Run(() => KeyManager.WrapWithRecovery(m, key, recovery));
            SaveMaterial(m);
            return recovery;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public async Task<bool> VerifyPasswordAsync(string password)
    {
        var m = LoadMaterial();
        if (m == null) return false;
        var key = await Task.Run(() => KeyManager.TryUnwrapWithPassword(m, password));
        if (key == null) return false;
        CryptographicOperations.ZeroMemory(key);
        return true;
    }

    /// <summary>Called once the UI is ready after a successful unlock/setup.</summary>
    public void BeginSession()
    {
        _lastActivity = Environment.TickCount64;
        InputManager.Current.PreProcessInput -= OnPreProcessInput;
        InputManager.Current.PreProcessInput += OnPreProcessInput;
        _idleTimer.Start();
        Unlocked?.Invoke();
    }

    public async Task LockAsync()
    {
        if (_locking || !Crypto.IsUnlocked) return;
        _locking = true;
        try
        {
            _idleTimer.Stop();
            InputManager.Current.PreProcessInput -= OnPreProcessInput;
            if (Locking != null)
            {
                foreach (var handler in Locking.GetInvocationList().Cast<Func<Task>>())
                {
                    try { await handler(); } catch { /* never block locking */ }
                }
            }
            await _store.WaitIdleAsync();
            SecureClipboard.ClearIfOurs();
            Crypto.Clear();
            Locked?.Invoke();
        }
        finally
        {
            _locking = false;
        }
    }

    public void ResetIdle() => _lastActivity = Environment.TickCount64;

    private void OnPreProcessInput(object sender, PreProcessInputEventArgs e)
    {
        switch (e.StagingItem.Input)
        {
            case KeyboardEventArgs:
            case MouseButtonEventArgs:
            case MouseWheelEventArgs:
                _lastActivity = Environment.TickCount64;
                break;
            case MouseEventArgs me:
                // Ignore synthetic mouse moves raised by layout changes: only real movement counts.
                var window = Application.Current?.MainWindow;
                if (window == null) break;
                var p = me.GetPosition(window);
                if (Math.Abs(p.X - _lastMouse.X) + Math.Abs(p.Y - _lastMouse.Y) > 3)
                {
                    _lastMouse = p;
                    _lastActivity = Environment.TickCount64;
                }
                break;
        }
    }

    private async void OnIdleTick(object? sender, EventArgs e)
    {
        var minutes = _settings.Current.AutoLockMinutes;
        if (minutes <= 0 || !Crypto.IsUnlocked) return;
        if (Environment.TickCount64 - _lastActivity > minutes * 60_000L)
            await LockAsync();
    }
}
