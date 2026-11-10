using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;
using Satchel.Services;

namespace Satchel.ViewModels.Dialogs;

/// <summary>Asks for the master password again (e.g. before showing private items).</summary>
public sealed partial class VerifyPasswordDialogViewModel : DialogViewModel
{
    private readonly SessionService _session;

    public VerifyPasswordDialogViewModel(SessionService session, string title, string message)
    {
        _session = session;
        Title = title;
        Message = message;
    }

    public string Title { get; }
    public string Message { get; }
    public override double DialogWidth => 420;

    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isBusy;

    [RelayCommand]
    private async Task Confirm()
    {
        if (IsBusy || Password.Length == 0) return;
        IsBusy = true;
        try
        {
            if (await _session.VerifyPasswordAsync(Password)) Close(true);
            else
            {
                Error = "That isn't your master password.";
                Password = "";
            }
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>The keyboard shortcut cheat sheet (F1 / Ctrl+/).</summary>
public sealed class ShortcutsDialogViewModel : DialogViewModel
{
    public IReadOnlyList<ShortcutGroup> Groups => Shortcuts.All;
    public IReadOnlyList<ShortcutGroup> Left => Shortcuts.Split(Groups).Left;
    public IReadOnlyList<ShortcutGroup> Right => Shortcuts.Split(Groups).Right;
    public override double DialogWidth => 860;
}

/// <summary>
/// Create or edit a dashboard profile: a name, an optional icon (profiles with one get a top-bar shortcut) and, for a new
/// profile, what it starts with ("copy", "default" or "empty"). Returns true, or "delete" from the edit view.
/// </summary>
public sealed partial class DashboardProfileDialogViewModel : DialogViewModel
{
    private readonly DashboardProfileService _profiles;
    private readonly DashboardProfile? _profile;

    public DashboardProfileDialogViewModel(DashboardProfile? profile, string currentName, DashboardProfileService profiles)
    {
        _profiles = profiles;
        _profile = profile;
        IsNew = profile == null;
        CurrentName = currentName;
        CanDelete = !IsNew && profiles.All.Count > 1;
        _name = profile?.Name ?? "";
        _icon = profile?.Icon ?? "";
    }

    public bool IsNew { get; }
    public bool CanDelete { get; }
    public string CurrentName { get; }
    public string Heading => IsNew ? "New dashboard profile" : "Edit dashboard profile";
    public string CopyLabel => $"Copy of “{CurrentName}”";
    public string SaveText => IsNew ? "Create" : "Save";
    public override double DialogWidth => 520;

    public static IReadOnlyList<string> Icons { get; } =
    [
        "", Glyphs.Home, Glyphs.Work, Glyphs.Laptop, Glyphs.Code, Glyphs.Projects, Glyphs.Tasks, Glyphs.Calendar, Glyphs.Lightbulb, Glyphs.Lightning,
        Glyphs.Book, Glyphs.Education, Glyphs.Library, Glyphs.Pen, Glyphs.Palette, Glyphs.Camera, Glyphs.Game, Glyphs.Puzzle, Glyphs.Music, Glyphs.Video,
        Glyphs.Heart, Glyphs.Health, Glyphs.Sun, Glyphs.Moon, Glyphs.Airplane, Glyphs.Car, Glyphs.Globe, Glyphs.Shop, Glyphs.Card, Glyphs.Pulse,
        Glyphs.Star, Glyphs.Flag
    ];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    private string _name;

    [ObservableProperty] private string _icon;
    [ObservableProperty] private string _start = "copy";
    [ObservableProperty] private string? _error;

    partial void OnNameChanged(string value) => Error = null;

    [RelayCommand]
    private void SetIcon(string? icon) => Icon = icon ?? "";

    private bool CanSave => !string.IsNullOrWhiteSpace(Name);

    [RelayCommand(CanExecute = nameof(CanSave))]
    private void Save()
    {
        if (_profiles.NameExists(Name, _profile)) { Error = $"You already have a profile called “{Name.Trim()}”."; return; }
        Name = Name.Trim();
        Close(true);
    }

    [RelayCommand]
    private void Delete() => Close("delete");
}

/// <summary>A board note in a large editor with the full toolbar. It edits the same note, so changes show up on the board straight away.</summary>
public sealed partial class StickyEditorDialogViewModel(Models.StickyNote sticky) : DialogViewModel
{
    public Models.StickyNote Sticky { get; } = sticky;
    public IReadOnlyList<ColorOption> Colors => Palettes.Stickies;
    public override double DialogWidth => 840;

    [RelayCommand]
    private void SetColor(string? key)
    {
        if (key != null) Sticky.Color = key;
    }

    [RelayCommand]
    private void Done() => Close(true);
}

/// <summary>A simple list picker: returns the chosen <see cref="PickOption"/>.</summary>
public sealed partial class PickDialogViewModel : DialogViewModel
{
    public PickDialogViewModel(string title, string? message, IEnumerable<PickOption> options, string? placeholder = null)
    {
        Title = title;
        Message = message;
        _all = options.ToList();
        Placeholder = placeholder ?? "Search";
        Filter();
    }

    private readonly List<PickOption> _all;

    public string Title { get; }
    public string? Message { get; }
    public string Placeholder { get; }
    public bool HasMessage => !string.IsNullOrWhiteSpace(Message);
    public bool ShowSearch => _all.Count > 8;
    public ObservableCollection<PickOption> Options { get; } = [];
    public override double DialogWidth => 480;

    [ObservableProperty] private string _query = "";

    partial void OnQueryChanged(string value) => Filter();

    private void Filter()
    {
        Options.Clear();
        var q = Query.Trim();
        foreach (var o in _all.Where(o => q.Length == 0 || o.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || o.Subtitle.Contains(q, StringComparison.CurrentCultureIgnoreCase)))
            Options.Add(o);
    }

    [RelayCommand]
    private void Pick(PickOption? option)
    {
        if (option != null) Close(option);
    }

    [RelayCommand]
    private void PickFirst()
    {
        if (Options.Count > 0) Close(Options[0]);
    }
}

/// <summary>Drag (or use the arrows) to set the sidebar order. Returns the keys in order, or "reset".</summary>
public sealed partial class TabOrderDialogViewModel : DialogViewModel
{
    public TabOrderDialogViewModel(IEnumerable<TabToggle> items) => Items = new ObservableCollection<TabToggle>(items);

    public ObservableCollection<TabToggle> Items { get; }
    public override double DialogWidth => 460;

    [RelayCommand]
    private void Up(TabToggle? t)
    {
        if (t == null) return;
        int i = Items.IndexOf(t);
        if (i > 0) Items.Move(i, i - 1);
    }

    [RelayCommand]
    private void Down(TabToggle? t)
    {
        if (t == null) return;
        int i = Items.IndexOf(t);
        if (i >= 0 && i < Items.Count - 1) Items.Move(i, i + 1);
    }

    [RelayCommand]
    private void Reorder(Behaviors.ReorderEventArgs? args) { /* the drag already moved the item */ }

    [RelayCommand]
    private void Save() => Close(Items.Select(i => i.Key).ToList());

    [RelayCommand]
    private void ResetOrder() => Close("reset");
}

/// <summary>Create or edit a countdown. Returns true (saved) or "delete".</summary>
public sealed partial class CountdownDialogViewModel : DialogViewModel
{
    public CountdownDialogViewModel(Models.Countdown item)
    {
        Item = item;
        IsNew = item.Id == 0;
        _title = item.Title;
        _date = item.Date;
        _yearly = item.Yearly;
        _emoji = item.Emoji;
        _onDashboard = item.OnDashboard;
        _color = item.Color;
    }

    public Models.Countdown Item { get; }
    public bool IsNew { get; }
    public string Heading => IsNew ? "New countdown" : "Edit countdown";
    public override double DialogWidth => 440;

    [ObservableProperty] private string _title;
    [ObservableProperty] private DateTime? _date;
    [ObservableProperty] private bool _yearly;
    [ObservableProperty] private string _emoji;
    [ObservableProperty] private bool _onDashboard;
    [ObservableProperty] private string _color;
    [ObservableProperty] private string? _error;

    public static IReadOnlyList<string> Emojis { get; } = ["✈️", "🎂", "🎉", "🏖️", "🎄", "📚", "💍", "🏁", "🎮", "🎬", "🏠", "⭐"];

    [RelayCommand]
    private void PickEmoji(string? emoji) => Emoji = Emoji == emoji ? "" : emoji ?? "";

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Title)) { Error = "Give it a name."; return; }
        if (Date is not { } d) { Error = "Pick a date."; return; }
        Item.Title = Title.Trim();
        Item.Date = d.Date;
        Item.Yearly = Yearly;
        Item.Emoji = Emoji;
        Item.OnDashboard = OnDashboard;
        Item.Color = Color;
        Item.Refresh();
        Close(true);
    }

    [RelayCommand]
    private void Delete() => Close("delete");
}

/// <summary>Like a prompt, with a multi-line box (for pasting lists).</summary>
public sealed partial class TextAreaDialogViewModel(string title, string message, string placeholder, string confirmText) : DialogViewModel
{
    public string Title { get; } = title;
    public string Message { get; } = message;
    public string Placeholder { get; } = placeholder;
    public string ConfirmText { get; } = confirmText;
    public override double DialogWidth => 560;

    [ObservableProperty] private string _value = "";

    [RelayCommand]
    private void Confirm()
    {
        if (!string.IsNullOrWhiteSpace(Value)) Close(Value);
    }
}

/// <summary>Edits the fields of a collection (name, type, choices, shown on cards).</summary>
public sealed partial class FieldsDialogViewModel : DialogViewModel
{
    public FieldsDialogViewModel(IEnumerable<Models.FieldDef> fields)
    {
        foreach (var f in fields) Rows.Add(new FieldRow(f.Id, f.Name, f.Kind, f.Options, f.ShowOnCard));
    }

    public ObservableCollection<FieldRow> Rows { get; } = [];
    public override double DialogWidth => 640;
    public override bool CloseOnBackdrop => false;

    public static IReadOnlyList<Choice<Models.FieldKind>> Kinds { get; } =
    [
        new(Models.FieldKind.Text, "Text"), new(Models.FieldKind.LongText, "Long text"), new(Models.FieldKind.Number, "Number"), new(Models.FieldKind.Date, "Date"),
        new(Models.FieldKind.Rating, "Rating (stars)"), new(Models.FieldKind.Check, "Yes / no"), new(Models.FieldKind.Choice, "Choice"), new(Models.FieldKind.Url, "Link")
    ];

    [RelayCommand]
    private void Add() => Rows.Add(new FieldRow(Guid.NewGuid().ToString("N")[..8], "", Models.FieldKind.Text, "", true));

    [RelayCommand]
    private void Remove(FieldRow? row)
    {
        if (row != null) Rows.Remove(row);
    }

    [RelayCommand]
    private void Up(FieldRow? row)
    {
        if (row == null) return;
        int i = Rows.IndexOf(row);
        if (i > 0) Rows.Move(i, i - 1);
    }

    [RelayCommand]
    private void Save() => Close(true);

    public List<Models.FieldDef> Result() => Rows.Where(r => !string.IsNullOrWhiteSpace(r.Name))
        .Select(r => new Models.FieldDef { Id = r.Id, Name = r.Name.Trim(), Kind = r.Kind, Options = r.Options, ShowOnCard = r.ShowOnCard }).ToList();
}

public sealed partial class FieldRow(string id, string name, Models.FieldKind kind, string options, bool showOnCard) : ObservableObject
{
    public string Id { get; } = id;
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private string _options = options;
    [ObservableProperty] private bool _showOnCard = showOnCard;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoice))]
    private Models.FieldKind _kind = kind;

    public bool IsChoice => Kind == Models.FieldKind.Choice;
}

public sealed record PickOption(string Title, string Subtitle, string Glyph, object? Value, string? ColorKey = null)
{
    public bool HasSubtitle => !string.IsNullOrWhiteSpace(Subtitle);
}
