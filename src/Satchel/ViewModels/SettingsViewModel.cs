using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

namespace Satchel.ViewModels;

public sealed record AccentChoice(string? Key, string Name, Brush Brush);

public sealed partial class SettingsViewModel : PageViewModel, IDisposable
{
    private readonly INavigator _nav;
    private readonly SettingsService _settings;
    private readonly ThemeService _theme;
    private readonly SessionService _session;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly BackupService _backups;
    private readonly DataStore _store;
    private readonly PrivacyService _privacy;
    private readonly KeyBindingService _keys;

    public SettingsViewModel(INavigator nav, SettingsService settings, ThemeService theme, SessionService session, DialogService dialogs,
        ToastService toasts, BackupService backups, DataStore store, PrivacyService privacy, KeyBindingService keys, UpdateService updates)
    {
        Updates = new UpdatesViewModel(updates, settings, dialogs, backups);
        _keys = keys;
        _privacy = privacy;
        _privacy.VisibilityChanged += OnPrivacyChanged;
        _nav = nav;
        _settings = settings;
        _theme = theme;
        _session = session;
        _dialogs = dialogs;
        _toasts = toasts;
        _backups = backups;
        _store = store;
        _hint = session.Hint ?? "";
        BuildTabs();
        _customAccent = settings.Current.Accent?.StartsWith('#') == true ? settings.Current.Accent : "";
        _theme.Changed += OnThemeChanged;
        BuildAccents();
        BuildKeyRows();
        _keys.Changed += RefreshKeyRows;
        _settings.Changed += OnSidebarSettingChanged;
    }

    // ----- key bindings (your own keys for tabs and for private items) -----

    /// <summary>The tabs, grouped like the sidebar, in two columns.</summary>
    public IReadOnlyList<KeyBindingGroup> KeyGroupsLeft { get; private set; } = [];
    public IReadOnlyList<KeyBindingGroup> KeyGroupsRight { get; private set; } = [];

    public KeyBindingRow PrivacyKey { get; private set; } = new(KeyBindingService.PrivacyAction, "Show or hide private items", Glyphs.Hide);

    public string PrivacyKeyText => _keys.Text(KeyBindingService.PrivacyAction) is { } k
        ? $"Or press {k} anywhere in Satchel (change it under Key bindings)."
        : "You can give it keys under Key bindings.";

    public bool HasOwnKeys => _settings.Current.KeyBindings.Count > 0;

    private List<KeyBindingRow> _tabRows = [];

    private IEnumerable<KeyBindingRow> AllKeyRows => _tabRows.Prepend(PrivacyKey);

    private void BuildKeyRows()
    {
        CancelKeyRecording();
        PrivacyKey = new KeyBindingRow(KeyBindingService.PrivacyAction, "Show or hide private items", Glyphs.Hide) { Tooltip = "Ctrl+Shift+H unless you pick other keys" };
        OnPropertyChanged(nameof(PrivacyKey));
        var groups = new List<KeyBindingGroup>();
        _tabRows = [];
        if (_nav is ShellViewModel shell)
        {
            var hidden = ShellViewModel.HiddenKeys(_settings.Current.HiddenTabs);
            var ordered = ShellViewModel.Ordered(shell.AllNavItems, _settings.Current.TabOrder);
            foreach (var section in ordered.GroupBy(i => i.Section))
            {
                var rows = section.Select(item => new KeyBindingRow(KeyBindingService.PageAction(item.Key), item.Title, item.Glyph)
                {
                    IsHidden = hidden.Contains(item.Key) && item.Key != "Dashboard",
                }).ToList();
                if (section.Key.Length == 0) rows.Add(new KeyBindingRow(KeyBindingService.PageAction(shell.SettingsItem.Key), shell.SettingsItem.Title, shell.SettingsItem.Glyph));
                _tabRows.AddRange(rows);
                groups.Add(new KeyBindingGroup(section.Key.Length == 0 ? "Start" : section.Key, rows));
            }
        }
        // Two columns of about the same height, keeping the sidebar's order.
        var left = new List<KeyBindingGroup>();
        var right = new List<KeyBindingGroup>();
        int total = groups.Sum(g => g.Rows.Count + 1), leftRows = 0;
        foreach (var g in groups)
        {
            if (right.Count == 0 && leftRows + (g.Rows.Count + 1) / 2 <= total / 2) { left.Add(g); leftRows += g.Rows.Count + 1; }
            else right.Add(g);
        }
        (KeyGroupsLeft, KeyGroupsRight) = (left, right);
        OnPropertyChanged(nameof(KeyGroupsLeft));
        OnPropertyChanged(nameof(KeyGroupsRight));
        RefreshKeyRows();
    }

    /// <summary>Shows each row's keys, and the Ctrl+number it has from its place in the sidebar (unless you gave that number to another tab).</summary>
    private void RefreshKeyRows()
    {
        var shell = _nav as ShellViewModel;
        foreach (var r in AllKeyRows)
        {
            r.Keys = _keys.Text(r.Action);
            if (KeyBindingService.PageOf(r.Action) is not { } page || shell == null) continue;
            bool settings = page == shell.SettingsItem.Key;
            var nav = settings ? shell.SettingsItem : shell.FindNav(page);
            r.SidebarKeys = nav?.Shortcut is { Length: > 0 } s && s != r.Keys ? s : null;
            r.Tooltip = r.IsHidden ? "Hidden from the sidebar — keys you give it still open it"
                : r.SidebarKeys == null ? null
                : settings ? $"{r.SidebarKeys} opens Settings too"
                : $"{r.SidebarKeys} opens it too — from its place in the sidebar";
        }
        OnPropertyChanged(nameof(PrivacyKeyText));
        OnPropertyChanged(nameof(HasOwnKeys));
        RefreshShortcuts();
    }

    /// <summary>Click a box: it listens for keys (click again, or Esc, to stop).</summary>
    [RelayCommand]
    private void RecordKey(KeyBindingRow? row)
    {
        if (row == null) return;
        bool start = !row.IsRecording;
        foreach (var r in AllKeyRows)
        {
            r.IsRecording = false;
            r.Message = null;
        }
        row.IsRecording = start;
        if (!start) KeyBindingService.Recorder = null;
    }

    /// <summary>The keys pressed while a box was listening. Refused keys keep it listening, so another combination can be tried.</summary>
    public void CommitKey(KeyBindingRow row, KeyChord chord)
    {
        // Which tab had these keys from its place in the sidebar (Ctrl+1…9) before.
        var shell = _nav as ShellViewModel;
        var positional = chord.Modifiers == System.Windows.Input.ModifierKeys.Control && chord.Key is >= System.Windows.Input.Key.D1 and <= System.Windows.Input.Key.D9
            && shell != null && chord.Key - System.Windows.Input.Key.D1 < shell.NavItems.Count && _keys.ActionFor(chord) == null
            ? shell.NavItems[chord.Key - System.Windows.Input.Key.D1] : null;

        var result = _keys.Set(row.Action, chord);
        row.Preview = null;
        row.Message = result.Message;
        row.IsError = !result.Ok;
        if (!result.Ok) return;
        row.IsRecording = false;
        KeyBindingService.Recorder = null;
        RefreshKeyRows();
        if (result.MovedFrom != null && AllKeyRows.FirstOrDefault(r => r.Action == result.MovedFrom) is { } from)
        {
            from.Message = $"Its keys went to {row.Title}.";
            from.IsError = false;
        }
        if (positional != null && KeyBindingService.PageAction(positional.Key) != row.Action
            && AllKeyRows.FirstOrDefault(r => r.Action == KeyBindingService.PageAction(positional.Key)) is { } lost)
        {
            lost.Message = $"{chord} opens {row.Title} now.";
            lost.IsError = false;
        }
    }

    /// <summary>Modifiers held down while a box listens: shown as "Ctrl+Shift+…".</summary>
    public static void PreviewKeys(KeyBindingRow row, System.Windows.Input.ModifierKeys modifiers) => row.Preview = KeyChord.ModifierText(modifiers) is { Length: > 0 } t ? t : null;

    public void CancelKeyRecording()
    {
        foreach (var r in AllKeyRows)
        {
            r.IsRecording = false;
            r.Preview = null;
        }
        KeyBindingService.Recorder = null;
    }

    [RelayCommand]
    private void ClearKey(KeyBindingRow? row)
    {
        if (row == null) return;
        CancelKeyRecording();
        _keys.Set(row.Action, null);
        foreach (var r in AllKeyRows) r.Message = null;
        if (row.Action == KeyBindingService.PrivacyAction) row.Message = "No keys now — switch it in Settings → Privacy or from search (Ctrl+K).";
        row.IsError = false;
        RefreshKeyRows();
    }

    [RelayCommand]
    private async Task ResetKeys()
    {
        CancelKeyRecording();
        if (!await _dialogs.ConfirmAsync("Reset key bindings?", "The keys you gave tabs are removed, and private items go back to Ctrl+Shift+H.", "Reset")) return;
        _keys.ResetAll();
        foreach (var r in AllKeyRows) r.Message = null;
        RefreshKeyRows();
        _toasts.Success("Key bindings reset");
    }

    // ----- keyboard shortcuts -----

    [ObservableProperty] private string _shortcutQuery = "";
    public IReadOnlyList<ShortcutGroup> ShortcutsLeft { get; private set; } = [];
    public IReadOnlyList<ShortcutGroup> ShortcutsRight { get; private set; } = [];
    public bool HasShortcuts => ShortcutsLeft.Count + ShortcutsRight.Count > 0;
    public string ShortcutsIntro => $"All {Shortcuts.All.Sum(g => g.Items.Count)} shortcuts. Press F1 or Ctrl+/ anywhere for the same list as a cheat sheet.";

    partial void OnShortcutQueryChanged(string value) => RefreshShortcuts();

    private void RefreshShortcuts()
    {
        (ShortcutsLeft, ShortcutsRight) = Shortcuts.Split(Shortcuts.Filter(ShortcutQuery));
        OnPropertyChanged(nameof(ShortcutsLeft));
        OnPropertyChanged(nameof(ShortcutsRight));
        OnPropertyChanged(nameof(HasShortcuts));
        OnPropertyChanged(nameof(ShortcutsIntro));
    }

    [RelayCommand]
    private Task OpenCheatSheet() => _dialogs.ShowAsync(new ShortcutsDialogViewModel());

    public override string Title => "Settings";
    public AppSettings S => _settings.Current;
    public IReadOnlyList<ThemeDefinition> Themes => ThemeService.Themes;
    public ObservableCollection<AccentChoice> Accents { get; } = [];
    public ObservableCollection<BackupInfo> Backups { get; } = [];

    public static IReadOnlyList<Choice<string>> Fonts { get; } =
    [
        new("Segoe UI Variable Text, Segoe UI", "Segoe UI Variable (default)"), new("Segoe UI", "Segoe UI"), new("Bahnschrift", "Bahnschrift"),
        new("Calibri", "Calibri"), new("Candara", "Candara"), new("Corbel", "Corbel"), new("Verdana", "Verdana"), new("Tahoma", "Tahoma"),
        new("Georgia", "Georgia (serif)"), new("Cascadia Code, Consolas", "Cascadia Code (mono)")
    ];

    public static IReadOnlyList<Choice<string>> StartPages { get; } =
    [
        new("Dashboard", "Dashboard"), new("Today", "Today"), new("Notes", "Notes"), new("Tasks", "Tasks"), new("Calendar", "Calendar"), new("Projects", "Projects"),
        new("Vault", "Vault"), new("Contacts", "Contacts"), new("Money", "Money"), new("Gamble", "Gamble"), new("Bookmarks", "Bookmarks"), new("Board", "Board"), new("Sketch", "Sketch"),
        new("Library", "Library"), new("Sheets", "Sheets"), new("Photos", "Photos"), new("Reading", "Reading"), new("Games", "Games"), new("Watching", "Watching"),
        new("Collections", "Collections"), new("Videos", "Videos"), new("Music", "Music"), new("Feeds", "Feeds"), new("Flashcards", "Flashcards"),
        new("Clock", "Clock"), new("Insights", "Insights")
    ];

    public static IReadOnlyList<Choice<int>> FeedIntervals { get; } =
        [new(30, "Every 30 minutes"), new(60, "Every hour"), new(180, "Every 3 hours"), new(720, "Twice a day")];

    public static IReadOnlyList<Choice<string>> Currencies { get; } =
    [
        new("EUR", "Euro (€)"), new("USD", "US dollar ($)"), new("GBP", "Pound (£)"), new("BGN", "Bulgarian lev (лв)"), new("CHF", "Swiss franc"),
        new("PLN", "Polish złoty"), new("RON", "Romanian leu"), new("SEK", "Swedish krona"), new("NOK", "Norwegian krone"), new("DKK", "Danish krone"), new("JPY", "Yen (¥)")
    ];

    // ----- privacy -----

    /// <summary>"Show private items" goes through the privacy service (it may ask for the master password).</summary>
    public bool ShowPrivate
    {
        get => _privacy.ShowPrivate;
        set => _ = SetShowPrivateAsync(value);
    }

    private async Task SetShowPrivateAsync(bool value)
    {
        await _privacy.SetVisibleAsync(value);
        OnPropertyChanged(nameof(ShowPrivate));
    }

    private void OnPrivacyChanged() => OnPropertyChanged(nameof(ShowPrivate));

    public static IReadOnlyList<Choice<int>> VideoIntervals { get; } =
        [new(30, "Every 30 minutes"), new(60, "Every hour"), new(180, "Every 3 hours"), new(720, "Twice a day")];

    /// <summary>Show/hide switches for the sidebar tabs.</summary>
    public ObservableCollection<TabToggle> Tabs { get; } = [];

    private void BuildTabs()
    {
        if (_nav is not ShellViewModel shell) return;
        var hidden = ShellViewModel.HiddenKeys(_settings.Current.HiddenTabs);
        Tabs.Clear();
        foreach (var item in ShellViewModel.Ordered(shell.AllNavItems, _settings.Current.TabOrder).Where(n => n.Key != "Dashboard"))
        {
            var toggle = new TabToggle(item.Key, item.Title, item.Glyph, !hidden.Contains(item.Key), item.Section);
            toggle.PropertyChanged += (_, _) => SaveTabs();
            Tabs.Add(toggle);
        }
    }

    private void SaveTabs() => _settings.Current.HiddenTabs = string.Join(",", Tabs.Where(t => !t.IsVisible).Select(t => t.Key));

    [RelayCommand]
    private async Task ReorderTabs()
    {
        var copy = Tabs.Select(t => new TabToggle(t.Key, t.Title, t.Glyph, t.IsVisible, t.Section)).ToList();
        var result = await _dialogs.ShowAsync(new TabOrderDialogViewModel(copy));
        if (result is "reset") _settings.Current.TabOrder = "";
        else if (result is List<string> keys) _settings.Current.TabOrder = string.Join(",", keys);
        else return;
        BuildTabs();
        _toasts.Success("Sidebar order saved");
    }

    [RelayCommand]
    private void ShowAllTabs()
    {
        foreach (var t in Tabs) t.IsVisible = true;
    }

    [RelayCommand]
    private async Task ClearPlayerData()
    {
        var folder = System.IO.Path.Combine(Core.AppPaths.DataDirectory, "WebView2");
        if (!System.IO.Directory.Exists(folder)) { _toasts.Info("Nothing to clear"); return; }
        if (!await _dialogs.ConfirmAsync("Clear video player data?", "The player's cache and cookies on this drive will be removed.", "Clear")) return;
        try
        {
            foreach (var dir in System.IO.Directory.GetDirectories(folder).Where(d => !d.EndsWith("host", StringComparison.OrdinalIgnoreCase)))
                System.IO.Directory.Delete(dir, recursive: true);
            _toasts.Success("Player data cleared");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            _toasts.Warning("The player is in use. Restart Satchel and clear it before opening Videos.");
        }
    }

    public static IReadOnlyList<Choice<string>> DateFormats { get; } =
    [
        new("", "Culture default"), new("dd.MM.yyyy", "31.12.2026"), new("dd/MM/yyyy", "31/12/2026"), new("MM/dd/yyyy", "12/31/2026"),
        new("yyyy-MM-dd", "2026-12-31"), new("d MMM yyyy", "31 Dec 2026")
    ];

    public static IReadOnlyList<Choice<string>> Cultures { get; } =
    [
        new("en-GB", "English (UK)"), new("en-US", "English (US)"), new("system", $"System language ({App.SystemCulture.NativeName})")
    ];

    public static IReadOnlyList<Choice<int>> AutoLockChoices { get; } =
    [
        new(1, "After 1 minute"), new(5, "After 5 minutes"), new(10, "After 10 minutes"), new(15, "After 15 minutes"),
        new(30, "After 30 minutes"), new(60, "After 1 hour"), new(0, "Never")
    ];

    public static IReadOnlyList<Choice<int>> ClipboardChoices { get; } =
    [
        new(10, "After 10 seconds"), new(20, "After 20 seconds"), new(30, "After 30 seconds"), new(60, "After 1 minute"), new(120, "After 2 minutes"), new(0, "Never")
    ];

    public static IReadOnlyList<Choice<int>> KeepChoices { get; } = [new(5, "Keep 5"), new(10, "Keep 10"), new(20, "Keep 20"), new(50, "Keep 50")];

    [ObservableProperty] private string _hint;
    [ObservableProperty] private string _customAccent;
    [ObservableProperty] private string _activeSection = "Appearance";

    public string DataFolder => AppPaths.DataDirectory;
    public string DatabaseFile => AppPaths.DatabasePath;
    public string DatabaseSize => Format.Bytes(BackupService.DatabaseSize());
    public string DriveText => $"{AppPaths.DriveLabel} · {AppPaths.FreeSpaceText}";
    public string LastBackupText => S.LastBackupAt.HasValue ? $"Last backup {Format.Relative(S.LastBackupAt.Value)}" : "No backup yet";
    public string PasswordChangedText => _session.PasswordChangedAt is { } d ? $"Master password set {Format.Relative(d)}" : "";
    public string Version => $"Version {AppInfo.Version}";
    /// <summary>Settings → Updates.</summary>
    public UpdatesViewModel Updates { get; }
    public string AppName => AppInfo.Name;
    public string ScaleText => $"{Math.Round(S.UiScale * 100)}%";
    public bool HasBackups => Backups.Count > 0;

    public Choice<string>? SelectedFont
    {
        get => Fonts.FirstOrDefault(f => f.Value == S.FontFamily) ?? Fonts[0];
        set { if (value != null) S.FontFamily = value.Value; }
    }

    public override Task OnNavigatedToAsync(object? parameter)
    {
        RefreshBackups();
        BuildKeyRows();
        Updates.Refresh();
        foreach (var n in new[] { nameof(DatabaseSize), nameof(LastBackupText), nameof(PasswordChangedText), nameof(DriveText) }) OnPropertyChanged(n);
        // e.g. "Bindings" from the command palette: open at that section (the page may still be appearing — it scrolls once it's there).
        if (parameter is string section)
        {
            PendingSection = section;
            GoToSection(section);
        }
        return Task.CompletedTask;
    }

    public override Task OnNavigatedFromAsync()
    {
        CancelKeyRecording();
        return base.OnNavigatedFromAsync();
    }

    private void OnThemeChanged()
    {
        BuildAccents();
        OnPropertyChanged(nameof(ScaleText));
    }

    private void BuildAccents()
    {
        Accents.Clear();
        var t = _theme.Current;
        Accents.Add(new AccentChoice(null, "Theme default", ColorUtil.Brush(ThemeService.ResolveAccent(null, t))));
        foreach (var a in Palettes.Accents) Accents.Add(new AccentChoice(a.Key, a.Name, ColorUtil.Brush(a.Resolve(t.IsDark))));
    }

    private void RefreshBackups()
    {
        Backups.Clear();
        foreach (var b in _backups.ListBackups().Take(12)) Backups.Add(b);
        OnPropertyChanged(nameof(HasBackups));
    }

    // ----- appearance -----

    [RelayCommand]
    private void SetTheme(string? key)
    {
        if (key == null) return;
        S.Theme = key;
    }

    [RelayCommand]
    private void SetAccent(string? key)
    {
        S.Accent = key;
        CustomAccent = "";
    }

    [RelayCommand]
    private void ApplyCustomAccent()
    {
        var hex = CustomAccent.Trim();
        if (!hex.StartsWith('#')) hex = "#" + hex;
        if (!ColorUtil.TryParse(hex, out var c)) { _toasts.Warning("Use a hex color like #1E9E8A."); return; }
        var hue = Hue(c);
        if (hue is >= 245 and <= 300 && Saturation(c) > 0.25)
            _toasts.Info("That's in the purple/indigo range — applied anyway, it's your call 🙂");
        S.Accent = ColorUtil.ToHex(c);
    }

    private static double Hue(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        if (d == 0) return 0;
        double h = max == r ? (g - b) / d % 6 : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        h *= 60;
        return h < 0 ? h + 360 : h;
    }

    private static double Saturation(Color c)
    {
        double max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0, min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
        return max == 0 ? 0 : (max - min) / max;
    }

    partial void OnActiveSectionChanged(string value) => SectionRequested?.Invoke(value);

    /// <summary>A section to open at, kept until the page is on screen to scroll there.</summary>
    public string? PendingSection { get; set; }
    public event Action<string>? SectionRequested;

    [RelayCommand]
    private void GoToSection(string? section)
    {
        if (section != null) ActiveSection = section;
        SectionRequested?.Invoke(section ?? "Appearance");
    }

    [RelayCommand]
    private void ScaleChanged() => OnPropertyChanged(nameof(ScaleText));

    [RelayCommand]
    private void ResetAppearance()
    {
        S.Theme = "daylight";
        S.Accent = null;
        S.FontFamily = "Segoe UI Variable Text, Segoe UI";
        S.UiScale = 1;
        S.CornerRadius = 10;
        S.AnimationsEnabled = true;
        S.AmbientEffects = true;
        S.CardShadows = true;
        OnPropertyChanged(nameof(SelectedFont));
        OnPropertyChanged(nameof(ScaleText));
    }

    // ----- security -----

    [RelayCommand]
    private async Task ChangePassword()
    {
        var result = await _dialogs.ShowAsync(new ChangePasswordDialogViewModel(_session));
        if (result is true)
        {
            _toasts.Success("Master password changed");
            OnPropertyChanged(nameof(PasswordChangedText));
        }
    }

    [RelayCommand]
    private async Task NewRecoveryKey() => await _dialogs.ShowAsync(new RecoveryKeyDialogViewModel(_session, _toasts));

    [RelayCommand]
    private void SaveHint()
    {
        _session.SetHint(Hint);
        _toasts.Success(string.IsNullOrWhiteSpace(Hint) ? "Hint removed" : "Hint saved");
    }

    [RelayCommand]
    private async Task LockNow() => await _session.LockAsync();

    // ----- data -----

    [RelayCommand]
    private async Task BackupNow()
    {
        try
        {
            var path = await _backups.CreateBackupAsync();
            S.LastBackupAt = DateTime.Now;
            RefreshBackups();
            OnPropertyChanged(nameof(LastBackupText));
            _toasts.Success($"Backup saved: {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            _toasts.Error("Backup failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task Restore(BackupInfo? backup)
    {
        if (backup == null) return;
        if (!await _dialogs.ConfirmAsync("Restore this backup?",
                $"Everything will be replaced with the backup from {backup.CreatedText}. A safety copy of the current data is made first. " +
                "The app will restart — you'll need the master password that was active when this backup was made.", "Restore and restart", danger: true)) return;
        await _backups.RestoreAsync(backup.Path);
    }

    [RelayCommand]
    private async Task RestoreFromFile()
    {
        var dialog = new OpenFileDialog { Title = "Restore from a backup file", Filter = "Satchel database (*.db)|*.db", InitialDirectory = AppPaths.BackupDirectory };
        if (dialog.ShowDialog() != true) return;
        await Restore(new BackupInfo(dialog.FileName, Path.GetFileName(dialog.FileName), File.GetCreationTime(dialog.FileName), new FileInfo(dialog.FileName).Length));
    }

    [RelayCommand]
    private void OpenDataFolder() => UiCommands.OpenFolder(AppPaths.DataDirectory);

    [RelayCommand]
    private void OpenBackupFolder()
    {
        Directory.CreateDirectory(AppPaths.BackupDirectory);
        UiCommands.OpenFolder(AppPaths.BackupDirectory);
    }

    [RelayCommand]
    private async Task Compact()
    {
        var before = BackupService.DatabaseSize();
        await _store.RunAsync(_ => DatabaseInitializer.Compact());
        OnPropertyChanged(nameof(DatabaseSize));
        _toasts.Success($"Database compacted ({Format.Bytes(before)} → {DatabaseSize})");
    }

    [RelayCommand]
    private async Task ExportNotes()
    {
        var dialog = new OpenFolderDialog { Title = "Choose a folder for your notes (Markdown)" };
        if (dialog.ShowDialog() != true) return;
        var notes = await _store.ListAsync<Note>(q => q.Where(n => !n.IsTrashed));
        foreach (var n in notes)
        {
            var name = string.Concat(n.DisplayTitle.Split(Path.GetInvalidFileNameChars())).Trim();
            if (name.Length == 0) name = $"note-{n.Id}";
            var path = Path.Combine(dialog.FolderName, name + ".md");
            if (File.Exists(path)) path = Path.Combine(dialog.FolderName, $"{name}-{n.Id}.md");
            await File.WriteAllTextAsync(path, NotesViewModel.ToMarkdown(n), Encoding.UTF8);
        }
        _toasts.Success($"Exported {notes.Count} notes (unencrypted)");
    }

    [RelayCommand]
    private void GoImport(string? page)
    {
        if (page != null) _nav.NavigateTo(page, "import");
    }

    [RelayCommand]
    private void GoExport(string? page)
    {
        if (page != null) _nav.NavigateTo(page, "export");
    }

    public void Dispose()
    {
        _theme.Changed -= OnThemeChanged;
        _privacy.VisibilityChanged -= OnPrivacyChanged;
        _keys.Changed -= RefreshKeyRows;
        _settings.Changed -= OnSidebarSettingChanged;
    }

    /// <summary>The sidebar's order, hidden tabs or sections changed: the Ctrl+numbers and groups in Key bindings follow.</summary>
    private void OnSidebarSettingChanged(string name)
    {
        if (name is nameof(AppSettings.TabOrder) or nameof(AppSettings.HiddenTabs) or nameof(AppSettings.ShowSectionHeaders)) BuildKeyRows();
    }
}


/// <summary>A sidebar section's tabs in Settings → Key bindings.</summary>
public sealed record KeyBindingGroup(string Title, IReadOnlyList<KeyBindingRow> Rows);

/// <summary>One line in Settings → Key bindings: a tab (or private items) and the keys that open it.</summary>
public sealed partial class KeyBindingRow(string action, string title, string glyph) : ObservableObject
{
    public string Action { get; } = action;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;

    /// <summary>Hidden from the sidebar (its keys still open it).</summary>
    public bool IsHidden { get; init; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeys), nameof(BoxText), nameof(CanClear))]
    private string? _keys;

    /// <summary>The Ctrl+number the tab has from its place in the sidebar, when it isn't the same as its own keys.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSidebarKeys))]
    private string? _sidebarKeys;

    [ObservableProperty] private string? _tooltip;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoxText), nameof(CanClear))]
    private bool _isRecording;

    /// <summary>Modifiers held down while listening ("Ctrl+Shift+…").</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BoxText))]
    private string? _preview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty] private bool _isError;

    public bool HasKeys => Keys != null;
    public bool HasSidebarKeys => SidebarKeys != null;
    public bool CanClear => HasKeys && !IsRecording;
    public bool HasMessage => !string.IsNullOrEmpty(Message);
    public string BoxText => IsRecording ? Preview ?? "Press keys…" : Keys ?? "Not set";
}

public sealed partial class TabToggle(string key, string title, string glyph, bool isVisible, string section = "") : ObservableObject
{
    public string Key { get; } = key;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    public string Section { get; } = section;
    public bool HasSection => Section.Length > 0;
    [ObservableProperty] private bool _isVisible = isVisible;
}
