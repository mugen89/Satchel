using System.Windows.Input;

namespace Satchel.Core;

/// <summary>A key with its modifiers, e.g. Ctrl+Shift+B. Stored and shown as text ("Ctrl+Shift+B", "Alt+G", "F6").</summary>
public readonly record struct KeyChord(ModifierKeys Modifiers, Key Key)
{
    // Names for keys whose enum name isn't what's printed on the key. None of them may contain '+' (it separates the parts).
    private static readonly (Key Key, string Name)[] Names =
    [
        (Key.OemComma, ","), (Key.OemPeriod, "."), (Key.OemQuestion, "/"), (Key.Oem5, "\\"), (Key.OemPlus, "="), (Key.OemMinus, "-"),
        (Key.OemSemicolon, ";"), (Key.OemQuotes, "'"), (Key.OemOpenBrackets, "["), (Key.OemCloseBrackets, "]"), (Key.OemTilde, "`"),
        (Key.Left, "←"), (Key.Right, "→"), (Key.Up, "↑"), (Key.Down, "↓"),
        (Key.Back, "Backspace"), (Key.Return, "Enter"), (Key.Escape, "Esc"), (Key.Prior, "PageUp"), (Key.Next, "PageDown"),
        (Key.Space, "Space"), (Key.Delete, "Delete"), (Key.Insert, "Insert"),
        (Key.Add, "Num Plus"), (Key.Subtract, "Num Minus"), (Key.Multiply, "Num *"), (Key.Divide, "Num /"), (Key.Decimal, "Num ."),
    ];

    /// <summary>The keys as pressed. The ISO key next to left Shift counts as the backslash key (as Ctrl+\ already does).</summary>
    public static KeyChord From(ModifierKeys modifiers, Key key) => new(modifiers, key == Key.OemBackslash ? Key.Oem5 : key);

    public static bool IsModifierKey(Key key) => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt
        or Key.LWin or Key.RWin or Key.System or Key.ImeProcessed or Key.DeadCharProcessed or Key.None;

    /// <summary>Media, volume, browser and lock keys belong to Windows and the players.</summary>
    private static bool IsReservedKey(Key key) => key is Key.MediaPlayPause or Key.MediaNextTrack or Key.MediaPreviousTrack or Key.MediaStop
        or Key.VolumeMute or Key.VolumeDown or Key.VolumeUp or Key.BrowserBack or Key.BrowserForward or Key.BrowserRefresh or Key.BrowserStop
        or Key.BrowserSearch or Key.BrowserFavorites or Key.BrowserHome or Key.LaunchMail or Key.SelectMedia or Key.LaunchApplication1
        or Key.LaunchApplication2 or Key.Sleep or Key.Apps or Key.Capital or Key.NumLock or Key.Scroll or Key.Snapshot or Key.Tab;

    public static string KeyName(Key key)
    {
        foreach (var (k, name) in Names)
            if (k == key) return name;
        if (key is >= Key.D0 and <= Key.D9) return ((int)(key - Key.D0)).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "Num " + (int)(key - Key.NumPad0);
        return key.ToString();
    }

    private static Key? ParseKey(string name)
    {
        foreach (var (k, n) in Names)
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) return k;
        if (name.Length == 1 && char.IsAsciiDigit(name[0])) return Key.D0 + (name[0] - '0');
        if (name.Length == 5 && name.StartsWith("Num ", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(name[4])) return Key.NumPad0 + (name[4] - '0');
        if (name == "−") return Key.OemMinus; // the minus sign the cheat sheet uses
        // Only real key names from here on ("B", "F5", "Home") — Enum.TryParse would also take numbers and symbols.
        if (name.Length == 0 || !name.All(char.IsAsciiLetterOrDigit) || !char.IsAsciiLetter(name[0])) return null;
        return Enum.TryParse<Key>(name, ignoreCase: true, out var key) && Enum.IsDefined(key) && !IsModifierKey(key) ? key : null;
    }

    /// <summary>Just the modifiers, while they're held down: "Ctrl+Shift+…".</summary>
    public static string ModifierText(ModifierKeys modifiers)
    {
        var text = new KeyChord(modifiers, Key.None).ToString();
        return text.Length == 0 ? "" : text + "…";
    }

    public override string ToString()
    {
        var parts = new List<string>(4);
        if (Modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (Key != Key.None) parts.Add(KeyName(Key));
        return string.Join("+", parts) + (Key == Key.None && parts.Count > 0 ? "+" : "");
    }

    public static KeyChord? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        // The "=" key is written "=", so "+" only ever separates the parts.
        var tokens = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0) return null;
        var mods = ModifierKeys.None;
        foreach (var t in tokens[..^1])
        {
            switch (t.ToLowerInvariant())
            {
                case "ctrl" or "control": mods |= ModifierKeys.Control; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                default: return null;
            }
        }
        return ParseKey(tokens[^1]) is { } key ? From(mods, key) : null;
    }

    /// <summary>Usable as a shortcut of your own: with Ctrl or Alt (plain keys are for typing), or a function key F2–F24.</summary>
    public bool IsUsable =>
        !IsModifierKey(Key) && !IsReservedKey(Key) && !Modifiers.HasFlag(ModifierKeys.Windows) &&
        (Modifiers.HasFlag(ModifierKeys.Control) || Modifiers.HasFlag(ModifierKeys.Alt) || Key is >= Key.F2 and <= Key.F24);
}
