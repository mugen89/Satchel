using System.Windows;
using System.Windows.Input;
using Satchel.Core;

namespace Satchel.Services;

/// <summary>The outcome of giving an action new keys: refused (with why), or done (with a note, and where the keys came from).</summary>
public sealed record KeySetResult(bool Ok, string? Message, string? MovedFrom);

/// <summary>
/// Your own shortcuts (Settings → Key bindings): a key for any tab, and the key that shows or hides private items.
/// Stored in settings as action → keys, e.g. "page:Board" → "Ctrl+Shift+B" ("" = no keys).
/// </summary>
public sealed class KeyBindingService
{
    public const string PrivacyAction = "privacy";
    public static readonly KeyChord PrivacyDefault = new(ModifierKeys.Control | ModifierKeys.Shift, Key.H);

    private readonly SettingsService _settings;
    private Dictionary<KeyChord, string>? _lookup;

    public KeyBindingService(SettingsService settings)
    {
        _settings = settings;
        // The cheat sheet and Settings → Keyboard shortcuts show the keys you picked.
        Shortcuts.PrivacyKeys = () => Text(PrivacyAction);
        Shortcuts.CustomPages = () => Bindings()
            .Where(b => PageOf(b.Action) != null)
            .OrderBy(b => b.Chord.ToString(), StringComparer.Ordinal)
            .Select(b => new ShortcutEntry(b.Chord.ToString(), $"Go to {PageOf(b.Action)}"))
            .ToList();
    }

    /// <summary>The settings box that is listening for keys; while it has the focus, the window's own shortcuts stay quiet.</summary>
    public static IInputElement? Recorder { get; set; }

    /// <summary>A binding was set, moved or cleared.</summary>
    public event Action? Changed;

    public static string PageAction(string pageKey) => "page:" + pageKey;
    public static string? PageOf(string action) => action.StartsWith("page:", StringComparison.Ordinal) ? action[5..] : null;

    private Dictionary<string, string> Stored => _settings.Current.KeyBindings ??= [];

    /// <summary>The keys for an action (its default when you haven't changed it), or null when it has none.</summary>
    public KeyChord? Get(string action)
    {
        if (Stored.TryGetValue(action, out var text)) return KeyChord.Parse(text);
        return action == PrivacyAction ? PrivacyDefault : null;
    }

    public string? Text(string action) => Get(action)?.ToString();

    /// <summary>Every action that has keys, defaults included.</summary>
    public IEnumerable<(string Action, KeyChord Chord)> Bindings()
    {
        foreach (var action in Stored.Keys.Append(PrivacyAction).Distinct().ToList())
            if (Get(action) is { } chord) yield return (action, chord);
    }

    /// <summary>Which action these keys run, if any.</summary>
    public string? ActionFor(KeyChord chord)
    {
        _lookup ??= Bindings().GroupBy(b => b.Chord).ToDictionary(g => g.Key, g => g.First().Action);
        return _lookup.TryGetValue(chord, out var action) ? action : null;
    }

    /// <summary>
    /// Gives an action new keys (null = none). Keys Satchel already uses are refused; keys another of your bindings had move here.
    /// </summary>
    public KeySetResult Set(string action, KeyChord? chord)
    {
        if (chord is { } c)
        {
            if (c.Modifiers.HasFlag(ModifierKeys.Windows)) return new(false, "The Windows key belongs to Windows — use Ctrl or Alt.", null);
            if (!c.IsUsable)
                return new(false, c.Modifiers.HasFlag(ModifierKeys.Control) || c.Modifiers.HasFlag(ModifierKeys.Alt)
                    ? $"{KeyChord.KeyName(c.Key)} can't be used — try a letter, a digit or F2–F12."
                    : "Add Ctrl or Alt — plain keys are for typing (F2–F12 work on their own).", null);
            if (TakenBy(c) is { } what) return new(false, $"{c} is already taken: {what}.", null);
        }
        if (chord == Get(action)) return new(true, null, null);

        string? movedFrom = chord is { } k && ActionFor(k) is { } other && other != action ? other : null;
        if (movedFrom != null) Stored[movedFrom] = "";
        if (action == PrivacyAction && chord == PrivacyDefault) Stored.Remove(action);
        else Stored[action] = chord?.ToString() ?? "";
        // An empty entry only means something for private items ("no keys" instead of the default).
        foreach (var key in Stored.Where(p => p.Value.Length == 0 && p.Key != PrivacyAction).Select(p => p.Key).ToList()) Stored.Remove(key);
        Save();

        var notes = new List<string>();
        if (movedFrom != null) notes.Add($"Taken from {Describe(movedFrom)}.");
        if (chord is { } n)
        {
            if (n.Modifiers == ModifierKeys.Control && n.Key is Key.D1 or Key.D2)
                notes.Add($"While you write in a note, {n} still makes a heading.");
            if (n.Modifiers.HasFlag(ModifierKeys.Control) && n.Modifiers.HasFlag(ModifierKeys.Alt))
                notes.Add("On keyboards with AltGr, Ctrl+Alt keys can also type a character — if one stops working, pick other keys.");
        }
        return new(true, notes.Count > 0 ? string.Join(" ", notes) : null, movedFrom);
    }

    /// <summary>Back to the defaults: no keys of your own for tabs, Ctrl+Shift+H for private items.</summary>
    public void ResetAll()
    {
        Stored.Clear();
        Save();
    }

    private void Save()
    {
        _lookup = null;
        _settings.ScheduleSave();
        Changed?.Invoke();
    }

    public static string Describe(string action) => action == PrivacyAction ? "showing private items" : PageOf(action) ?? action;

    /// <summary>What the keys already do in Satchel or in text boxes (so a binding of your own can't take them over), or null when they're free.</summary>
    public static string? TakenBy(KeyChord chord)
    {
        const ModifierKeys Ctrl = ModifierKeys.Control, CtrlShift = ModifierKeys.Control | ModifierKeys.Shift, Alt = ModifierKeys.Alt;
        string? what = (chord.Modifiers, chord.Key) switch
        {
            (Ctrl, Key.A or Key.C or Key.V or Key.X or Key.Z or Key.Y or Key.Insert) => "Copy, paste, cut, undo and select all",
            (CtrlShift, Key.Z) => "Redo",
            (Ctrl or CtrlShift, Key.Left or Key.Right or Key.Up or Key.Down or Key.Home or Key.End or Key.Back or Key.Delete) => "Moving and selecting in text",
            (Ctrl, Key.E or Key.J or Key.R) => "Aligning text in notes",
            (CtrlShift, Key.L or Key.N) => "Lists in notes",
            (Ctrl, Key.OemOpenBrackets or Key.OemCloseBrackets) => "Text size in notes",
            (CtrlShift, Key.OemPlus) => "Superscript in notes",
            (Ctrl, Key.Space) => "Clearing formatting in notes",
            (Ctrl, Key.D0 or Key.NumPad0) => "Interface size reset (and normal text in notes)",
            (Alt, Key.F4) => "Closing Satchel",
            (Alt, Key.Space) => "The window menu",
            (Alt, >= Key.NumPad0 and <= Key.NumPad9) => "Typing special characters (Alt codes)",
            (ModifierKeys.Shift, Key.F10) => "Opening the right-click menu",
            _ => null
        };
        return what ?? Shortcuts.UsedBy(chord);
    }
}
