using System.Windows.Input;

namespace Satchel.Core;

public sealed record ShortcutEntry(string Keys, string Action);

public sealed record ShortcutGroup(string Title, string Glyph, IReadOnlyList<ShortcutEntry> Items);

/// <summary>Every keyboard shortcut — shown in the cheat sheet (F1 or Ctrl+/) and in Settings → Keyboard shortcuts.</summary>
public static class Shortcuts
{
    /// <summary>The keys picked for showing / hiding private items (null = none). Set by the key-binding service.</summary>
    public static Func<string?>? PrivacyKeys { get; set; }

    /// <summary>Your own tab shortcuts (Settings → Key bindings). Set by the key-binding service.</summary>
    public static Func<IReadOnlyList<ShortcutEntry>>? CustomPages { get; set; }

    /// <summary>The built-in shortcuts plus the keys you picked yourself.</summary>
    public static IReadOnlyList<ShortcutGroup> All
    {
        get
        {
            var privacy = PrivacyKeys?.Invoke();
            var groups = Fixed.Select(g => g.Title != "Everywhere" ? g : new ShortcutGroup(g.Title, g.Glyph,
                g.Items.Select(i => i.Action == PrivacyActionText
                    ? new ShortcutEntry(privacy ?? "Not set", i.Action)
                    : i).ToList())).ToList();
            var mine = CustomPages?.Invoke() ?? [];
            groups.Insert(1, new ShortcutGroup("Your key bindings", Glyphs.Keyboard, mine.Count > 0
                ? mine
                : [new ShortcutEntry("Settings → Key bindings", "Pick your own keys for any tab and for showing private items")]));
            return groups;
        }
    }

    private const string PrivacyActionText = "Show or hide private items (change it in Settings → Key bindings)";

    private static Dictionary<KeyChord, string>? _used;

    /// <summary>
    /// What these keys already do, from the list below (null when nothing), so your own key bindings can't take them.
    /// Ctrl+1…9 aren't counted: they follow the sidebar until you give one of them to a tab yourself.
    /// </summary>
    public static string? UsedBy(KeyChord chord)
    {
        if (_used == null)
        {
            var map = new Dictionary<KeyChord, string>();
            foreach (var group in Fixed)
                foreach (var entry in group.Items.Where(e => e.Action != PrivacyActionText))
                    foreach (var c in ChordsIn(entry.Keys))
                        if (!(c.Modifiers == ModifierKeys.Control && c.Key is >= Key.D1 and <= Key.D9)) map.TryAdd(c, entry.Action);
            _used = map;
        }
        return _used.TryGetValue(chord, out var action) ? action : null;
    }

    /// <summary>The key combinations in a cheat-sheet line such as "Ctrl+K or Ctrl+P", "Ctrl+B / I / U" or "Alt+1 … 9".</summary>
    public static IEnumerable<KeyChord> ChordsIn(string keys)
    {
        var parts = keys.Split([" or ", " / ", " · "], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        string prefix = "";
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (i == 0) prefix = part.LastIndexOf('+') is > 0 and var cut ? part[..(cut + 1)] : "";
            else if (!part.Contains('+')) part = prefix + part; // "Ctrl+B / I / U": I and U keep the Ctrl
            if (part.Split('…', StringSplitOptions.TrimEntries) is [var from, var to])
            {
                // "Alt+1 … 9": every digit from the first to the last
                if (KeyChord.Parse(from) is { Key: >= Key.D0 and <= Key.D9 } first && to.Length == 1 && char.IsAsciiDigit(to[0]))
                    for (var k = first.Key; k <= Key.D0 + (to[0] - '0'); k++) yield return first with { Key = k };
                continue;
            }
            if (KeyChord.Parse(part) is { } chord) yield return chord;
        }
    }

    private static IReadOnlyList<ShortcutGroup> Fixed { get; } =
    [
        new("Everywhere", Glyphs.Home,
        [
            new("Ctrl+K or Ctrl+P", "Search everything and run commands"),
            new("Ctrl+1 … 9", "Go to a page (sidebar order — or the tab you gave the keys in Settings → Key bindings)"),
            new("Ctrl+Tab / Ctrl+Shift+Tab", "Next / previous page"),
            new("Alt+← / Alt+→", "Back / forward (the mouse side buttons too)"),
            new("Ctrl+N", "New item on the current page"),
            new("Ctrl+F", "Search or find on the current page"),
            new("Ctrl+Shift+D", "Today"),
            new("Ctrl+T", "Clock: timers and stopwatch"),
            new("Ctrl+Shift+T", "Start a timer"),
            new("Ctrl+Shift+H", PrivacyActionText),
            new("Ctrl+\\", "Collapse or expand the sidebar"),
            new("Ctrl+= / Ctrl+− / Ctrl+0", "Interface size bigger / smaller / reset"),
            new("Ctrl+,", "Settings"),
            new("Ctrl+L", "Lock"),
            new("F1 or Ctrl+/", "Keyboard cheat sheet"),
            new("Esc", "Close a dialog, the palette or a preview"),
        ]),
        new("Search palette (Ctrl+K)", Glyphs.Search,
        [
            new("↑ / ↓", "Choose a result"),
            new("Enter", "Open it or run the command"),
            new("Esc", "Close the palette"),
        ]),
        new("Dashboard", Glyphs.Grid,
        [
            new("Alt+1 … 9", "Switch to a dashboard profile (opens the dashboard)"),
            new("Esc", "Finish customizing"),
            new("Ctrl+Enter", "Save the quick capture note"),
        ]),
        new("Music", Glyphs.Music,
        [
            new("Ctrl+Shift+Space", "Play / pause"),
            new("Ctrl+Shift+→ / ←", "Next / previous track"),
            new("Ctrl+Shift+↑ / ↓", "Volume up / down"),
            new("Media keys", "Play/pause, next, previous and stop"),
        ]),
        new("Text and notes", Glyphs.Font,
        [
            new("Ctrl+B / I / U", "Bold, italic, underline"),
            new("Ctrl+Shift+X", "Strikethrough"),
            new("Ctrl+Shift+C", "Checklist: put a box you can tick in front of the line"),
            new("Ctrl+1 / Ctrl+2", "Heading / subheading"),
            new("Ctrl+0", "Normal text"),
            new("Enter", "Add in quick-add boxes (tasks, links, cards…)"),
            new("Ctrl+Enter", "Save or add in multi-line boxes"),
        ]),
        new("Checklists", Glyphs.Checklist,
        [
            new("Click the box", "Tick or untick an item"),
            new("Enter", "New item below (on an empty item: end the list)"),
            new("Backspace at the start", "Remove the box (an empty checklist-note item is deleted)"),
            new("Tab / Shift+Tab", "Indent / outdent"),
            new("↑ / ↓", "Previous / next item (checklist notes)"),
        ]),
        new("Board", Glyphs.Tiles,
        [
            new("Double-click", "New note on the canvas"),
            new("Double-click a note's top bar", "Open the note large, with the full toolbar"),
            new("Ctrl+V", "Paste an image, link or text as a note"),
            new("Ctrl+wheel", "Zoom"),
            new("Delete / Backspace", "Remove the selected connection"),
            new("Esc", "Close a preview, stop connecting, deselect"),
            new("Enter (study panel)", "Add a note at the video's current time"),
        ]),
        new("Sheets", Glyphs.Sheet,
        [
            new("Type · F2 · double-click", "Edit a cell (Enter saves, Esc cancels)"),
            new("= then click cells", "Build a formula from cell references"),
            new("Enter / Tab", "Next cell down / right (Shift: back)"),
            new("Ctrl+arrows", "Jump to the edge of the data (add Shift to select)"),
            new("Ctrl+C / X / V", "Copy, cut, paste (also to and from Excel)"),
            new("Ctrl+D / Ctrl+R", "Fill down / right"),
            new("Ctrl+B / I", "Bold, italic"),
            new("Alt+=", "AutoSum"),
            new("Ctrl+; / Ctrl+Shift+;", "Today's date / the time"),
            new("Alt+Enter", "New line inside a cell"),
            new("Ctrl+Space / Shift+Space", "Select the column / row"),
            new("Delete", "Clear the selected cells"),
        ]),
        new("Sketch", Glyphs.Pen,
        [
            new("P / M / E / S", "Pen, marker, eraser, select"),
            new("Delete", "Remove the selection"),
            new("Ctrl+Z / Ctrl+Y", "Undo / redo"),
        ]),
        new("Library", Glyphs.Library,
        [
            new("Ctrl+F", "Find in the document"),
            new("Enter / Shift+Enter", "Next / previous match"),
            new("Esc", "Close find"),
            new("→ · ↓ · PgDn · Space", "Next page (page view)"),
            new("← · ↑ · PgUp", "Previous page (page view)"),
            new("Home / End", "First / last page"),
        ]),
        new("Flashcards", Glyphs.Education,
        [
            new("Space or Enter", "Show the answer — press again for Good"),
            new("1 / 2 / 3 / 4", "Again, hard, good, easy"),
            new("Esc", "End the session"),
            new("Ctrl+Enter", "Add the card you're typing"),
        ]),
        new("Clock", Glyphs.Timer,
        [
            new("Space", "Start / stop the stopwatch"),
            new("L", "Lap"),
            new("R", "Reset the stopwatch"),
        ]),
        new("Photos", Glyphs.Photo,
        [
            new("← / →", "Previous / next photo"),
            new("F", "Favorite"),
            new("Space", "Slideshow"),
            new("I", "Details and caption"),
            new("Wheel · double-click · 0", "Zoom · zoom in/out · reset"),
            new("Ctrl+V", "Paste a screenshot (grid)"),
            new("Esc", "Close the viewer or leave selection"),
        ]),
    ];

    /// <summary>Two columns of about the same height, keeping the reading order (each group goes to the shorter column).</summary>
    public static (IReadOnlyList<ShortcutGroup> Left, IReadOnlyList<ShortcutGroup> Right) Split(IReadOnlyList<ShortcutGroup> groups)
    {
        var left = new List<ShortcutGroup>();
        var right = new List<ShortcutGroup>();
        int leftRows = 0, rightRows = 0;
        foreach (var g in groups)
        {
            int rows = g.Items.Count + 2; // the group title takes about two rows
            if (leftRows <= rightRows) { left.Add(g); leftRows += rows; }
            else { right.Add(g); rightRows += rows; }
        }
        return (left, right);
    }

    /// <summary>The groups with only the shortcuts whose keys, action or group name contain every word of the query.</summary>
    public static IReadOnlyList<ShortcutGroup> Filter(string? query)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return All;
        bool Matches(ShortcutGroup g, ShortcutEntry e) => words.All(w =>
            e.Keys.Contains(w, StringComparison.CurrentCultureIgnoreCase) || e.Action.Contains(w, StringComparison.CurrentCultureIgnoreCase)
            || g.Title.Contains(w, StringComparison.CurrentCultureIgnoreCase));
        return All.Select(g => new ShortcutGroup(g.Title, g.Glyph, g.Items.Where(e => Matches(g, e)).ToList()))
            .Where(g => g.Items.Count > 0).ToList();
    }
}
