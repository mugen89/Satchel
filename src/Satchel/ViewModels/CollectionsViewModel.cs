using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

namespace Satchel.ViewModels;

/// <summary>One field of the selected item, editable through a template picked by the field type.</summary>
public sealed partial class FieldValue : ObservableObject
{
    private readonly Action _changed;

    public FieldValue(FieldDef def, TrackerItem item, Action changed)
    {
        Def = def;
        Item = item;
        _changed = changed;
    }

    public FieldDef Def { get; }
    public TrackerItem Item { get; }
    public string Name => Def.Name;
    public FieldKind Kind => Def.Kind;
    public IReadOnlyList<string> Options => Def.OptionList;

    public string Text
    {
        get => Item.Get(Def.Id);
        set
        {
            Item.Set(Def.Id, value?.Trim() ?? "");
            OnPropertyChanged();
            _changed();
        }
    }

    public bool BoolValue
    {
        get => Text == "1";
        set { Text = value ? "1" : ""; OnPropertyChanged(); }
    }

    public int Rating => int.TryParse(Text, out var r) ? r : 0;

    public DateTime? DateValue
    {
        get => DateTime.TryParse(Text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        set { Text = value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? ""; OnPropertyChanged(); }
    }

    [RelayCommand]
    private void SetRating(string? value)
    {
        if (!int.TryParse(value, out var r)) return;
        Text = Rating == r ? "" : r.ToString(CultureInfo.InvariantCulture);
        OnPropertyChanged(nameof(Rating));
    }

    [RelayCommand]
    private void OpenUrl()
    {
        if (Text.Length > 0) UiCommands.OpenUrl.Execute(Text.Contains("://") ? Text : "https://" + Text);
    }

    /// <summary>The value as shown on a card or in the table.</summary>
    public static string Display(FieldDef f, string raw) => f.Kind switch
    {
        FieldKind.Check => raw == "1" ? "✓" : "",
        FieldKind.Rating => int.TryParse(raw, out var r) && r > 0 ? new string('★', r) : "",
        FieldKind.Date => DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? Format.Date(d) : raw,
        FieldKind.Url => Uri.TryCreate(raw.Contains("://") ? raw : "https://" + raw, UriKind.Absolute, out var u) ? u.Host.Replace("www.", "") : raw,
        _ => raw.Replace('\n', ' ')
    };
}

/// <summary>Picks the editor template for a field type.</summary>
public sealed class FieldTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Text { get; set; }
    public DataTemplate? LongText { get; set; }
    public DataTemplate? Check { get; set; }
    public DataTemplate? Rating { get; set; }
    public DataTemplate? Date { get; set; }
    public DataTemplate? Choice { get; set; }
    public DataTemplate? Url { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => (item as FieldValue)?.Kind switch
    {
        FieldKind.LongText => LongText,
        FieldKind.Check => Check,
        FieldKind.Rating => Rating,
        FieldKind.Date => Date,
        FieldKind.Choice => Choice,
        FieldKind.Url => Url,
        _ => Text
    };
}

public sealed record TrackerTemplate(string Name, string Icon, string Color, string Description, FieldDef[] Fields);

public sealed record TableRow(TrackerItem Item, IReadOnlyList<string> Cells);

/// <summary>Collections: your own trackers with the fields you choose — wines, plants, records, recipes, gift ideas…</summary>
public sealed partial class CollectionsViewModel : PageViewModel, IDisposable
{
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly DispatcherTimer _saveTimer;
    private readonly HashSet<TrackerItem> _dirty = [];
    private List<TrackerItem> _items = [];
    private bool _loaded;

    public CollectionsViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts)
    {
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _saveTimer.Tick += async (_, _) => await FlushAsync();
    }

    public override string Title => "Collections";
    public ObservableCollection<Tracker> Trackers { get; } = [];
    public ObservableCollection<TrackerItem> Items { get; } = [];
    public ObservableCollection<FieldValue> Fields { get; } = [];
    public ObservableCollection<string> Columns { get; } = [];
    public ObservableCollection<TableRow> Rows { get; } = [];

    [ObservableProperty] private Tracker? _selectedTracker;
    [ObservableProperty] private TrackerItem? _selected;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _sortField = "";

    public bool HasTracker => SelectedTracker != null;
    public bool HasTrackers => Trackers.Count > 0;
    public bool HasSelection => Selected != null;
    public bool IsEmpty => Items.Count == 0;
    public bool IsGrid => SelectedTracker?.View != "table";
    public bool IsTable => SelectedTracker?.View == "table";

    public static IReadOnlyList<TrackerTemplate> Templates { get; } =
    [
        new("Blank collection", Glyphs.Tiles, "teal", "Start with just a title and add fields later", []),
        new("Wine cellar", Glyphs.Shop, "red", "Bottles, vintages, where they are", [F("Producer"), F("Vintage", FieldKind.Number), F("Type", FieldKind.Choice, "Red,White,Rosé,Sparkling,Dessert"), F("Region"), F("Rating", FieldKind.Rating), F("Bottles", FieldKind.Number), F("Drink by", FieldKind.Date, show: false)]),
        new("Plants", Glyphs.Lightbulb, "green", "Watering, light, when they were repotted", [F("Species"), F("Light", FieldKind.Choice, "Full sun,Bright indirect,Partial shade,Shade"), F("Water every (days)", FieldKind.Number), F("Last watered", FieldKind.Date), F("Repotted", FieldKind.Date, show: false)]),
        new("Recipes", Glyphs.Calculator, "orange", "Ingredients, time, how it turned out", [F("Cuisine"), F("Time (min)", FieldKind.Number), F("Rating", FieldKind.Rating), F("Source", FieldKind.Url), F("Ingredients", FieldKind.LongText, show: false), F("Steps", FieldKind.LongText, show: false)]),
        new("Board games", Glyphs.Game, "amber", "Players, length, plays", [F("Players"), F("Minutes", FieldKind.Number), F("Owned", FieldKind.Check), F("Rating", FieldKind.Rating), F("Last played", FieldKind.Date)]),
        new("Records", Glyphs.Music, "slate", "Vinyl, CDs and tapes", [F("Artist"), F("Year", FieldKind.Number), F("Format", FieldKind.Choice, "Vinyl,CD,Cassette,Digital"), F("Condition", FieldKind.Choice, "Mint,Very good,Good,Fair"), F("Rating", FieldKind.Rating)]),
        new("Things I lent", Glyphs.People, "sky", "Who has what, since when", [F("Lent to"), F("Since", FieldKind.Date), F("Returned", FieldKind.Check)]),
        new("Places to visit", Glyphs.MapPin, "cyan", "Cities, restaurants, trails", [F("Where"), F("Type", FieldKind.Choice, "City,Restaurant,Museum,Nature,Bar,Shop"), F("Been there", FieldKind.Check), F("Link", FieldKind.Url, show: false)]),
        new("Gift ideas", Glyphs.Heart, "rose", "For birthdays and holidays", [F("For"), F("Price", FieldKind.Number), F("Occasion"), F("Bought", FieldKind.Check), F("Link", FieldKind.Url, show: false)]),
    ];

    private static FieldDef F(string name, FieldKind kind = FieldKind.Text, string options = "", bool show = true) =>
        new() { Name = name, Kind = kind, Options = options, ShowOnCard = show };

    partial void OnSelectedTrackerChanged(Tracker? value)
    {
        Selected = null;
        SortField = "";
        Apply();
        OnPropertyChanged(nameof(HasTracker));
        OnPropertyChanged(nameof(IsGrid));
        OnPropertyChanged(nameof(IsTable));
    }

    partial void OnSearchChanged(string value) => Apply();
    partial void OnSortFieldChanged(string value) => Apply();

    partial void OnSelectedChanged(TrackerItem? oldValue, TrackerItem? newValue)
    {
        if (oldValue != null) oldValue.PropertyChanged -= OnItemChanged;
        if (newValue != null) newValue.PropertyChanged += OnItemChanged;
        Fields.Clear();
        if (newValue != null && SelectedTracker != null)
            foreach (var f in SelectedTracker.Fields) Fields.Add(new FieldValue(f, newValue, () => MarkDirty(newValue)));
        OnPropertyChanged(nameof(HasSelection));
    }

    private void OnItemChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is TrackerItem i && e.PropertyName is nameof(TrackerItem.Title) or nameof(TrackerItem.Notes) or nameof(TrackerItem.Tags) or nameof(TrackerItem.Image) or nameof(TrackerItem.IsFavorite))
            MarkDirty(i);
    }

    private void MarkDirty(TrackerItem item)
    {
        _dirty.Add(item);
        _saveTimer.Stop();
        _saveTimer.Start();
        UpdateCardText(item);
    }

    public override async Task FlushAsync()
    {
        _saveTimer.Stop();
        if (_dirty.Count == 0) return;
        var batch = _dirty.ToList();
        _dirty.Clear();
        foreach (var i in batch) i.UpdatedAt = DateTime.Now;
        await _store.UpdateRangeAsync(batch);
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is int id && Trackers.FirstOrDefault(t => t.Id == id) is { } t) SelectedTracker = t;
        else if (parameter is ItemRef r && Trackers.FirstOrDefault(x => x.Id == r.ParentId) is { } tracker)
        {
            SelectedTracker = tracker;
            if (r.Id != 0 && _items.FirstOrDefault(i => i.Id == r.Id) is { } item) Selected = item;
        }
    }

    public override void OnNewShortcut() => _ = AddItem();

    private async Task LoadAsync()
    {
        var (trackers, items) = await _store.RunAsync(db => (db.Trackers.OrderBy(t => t.SortOrder).ToList(), db.TrackerItems.ToList()));
        _items = items;
        Trackers.Clear();
        foreach (var t in trackers)
        {
            t.ItemCount = items.Count(i => i.TrackerId == t.Id);
            Trackers.Add(t);
        }
        _loaded = true;
        SelectedTracker ??= Trackers.FirstOrDefault();
        OnPropertyChanged(nameof(HasTrackers));
    }

    private void Apply()
    {
        Items.Clear();
        Rows.Clear();
        Columns.Clear();
        if (SelectedTracker is not { } t) { OnPropertyChanged(nameof(IsEmpty)); return; }
        var q = Search.Trim();
        IEnumerable<TrackerItem> items = _items.Where(i => i.TrackerId == t.Id);
        if (q.Length > 0) items = items.Where(i => i.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.ValuesJson.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                                                   i.Notes.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Tags.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        var sortDef = t.Fields.FirstOrDefault(f => f.Id == SortField);
        items = sortDef == null ? items.OrderByDescending(i => i.IsFavorite).ThenBy(i => i.SortOrder).ThenBy(i => i.Title)
            : sortDef.Kind is FieldKind.Number or FieldKind.Rating
                ? items.OrderByDescending(i => double.TryParse(i.Get(sortDef.Id), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.MinValue)
                : items.OrderBy(i => i.Get(sortDef.Id).Length == 0).ThenBy(i => i.Get(sortDef.Id), StringComparer.CurrentCultureIgnoreCase);
        foreach (var i in items)
        {
            UpdateCardText(i);
            Items.Add(i);
        }
        Columns.Add("Name");
        foreach (var f in t.Fields) Columns.Add(f.Name);
        foreach (var i in Items) Rows.Add(new TableRow(i, t.Fields.Select(f => FieldValue.Display(f, i.Get(f.Id))).ToList()));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void UpdateCardText(TrackerItem item)
    {
        var t = Trackers.FirstOrDefault(x => x.Id == item.TrackerId);
        if (t == null) return;
        item.CardText = string.Join("  ·  ", t.Fields.Where(f => f.ShowOnCard).Select(f => FieldValue.Display(f, item.Get(f.Id))).Where(v => v.Length > 0).Take(4));
    }

    // ----- collections -----

    [RelayCommand] private void SelectTracker(Tracker? t) { if (t != null) SelectedTracker = t; }

    [RelayCommand]
    private async Task NewTracker()
    {
        var options = Templates.Select(t => new PickOption(t.Name, t.Description, t.Icon, t, t.Color)).ToList();
        if (await _dialogs.ShowAsync(new PickDialogViewModel("New collection", "Start from a template — you can change every field afterwards.", options)) is not PickOption { Value: TrackerTemplate template }) return;
        var name = template.Fields.Length == 0
            ? await _dialogs.PromptAsync("Name your collection", null, "", "e.g. Watches, Sneakers, Coins", "Create")
            : template.Name;
        if (string.IsNullOrWhiteSpace(name)) return;
        var tracker = new Tracker { Name = name.Trim(), Icon = template.Icon, Color = template.Color, SortOrder = Trackers.Count };
        tracker.SaveFields(template.Fields.Select(f => new FieldDef { Name = f.Name, Kind = f.Kind, Options = f.Options, ShowOnCard = f.ShowOnCard }));
        await _store.AddAsync(tracker);
        Trackers.Add(tracker);
        SelectedTracker = tracker;
        OnPropertyChanged(nameof(HasTrackers));
    }

    [RelayCommand]
    private async Task RenameTracker()
    {
        if (SelectedTracker is not { } t) return;
        var name = await _dialogs.PromptAsync("Rename collection", null, t.Name, "Name", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        t.Name = name.Trim();
        await _store.UpdateAsync(t);
    }

    [RelayCommand]
    private async Task EditFields()
    {
        if (SelectedTracker is not { } t) return;
        var dialog = new FieldsDialogViewModel(t.Fields);
        if (await _dialogs.ShowAsync(dialog) is not true) return;
        t.SaveFields(dialog.Result());
        await _store.UpdateAsync(t);
        var keep = Selected;
        Apply();
        Selected = null;
        Selected = keep;
        _toasts.Success("Fields saved");
    }

    [RelayCommand]
    private async Task ToggleView()
    {
        if (SelectedTracker is not { } t) return;
        t.View = t.View == "table" ? "grid" : "table";
        await _store.UpdateAsync(t);
        OnPropertyChanged(nameof(IsGrid));
        OnPropertyChanged(nameof(IsTable));
    }

    [RelayCommand] private void SortBy(string? fieldId) => SortField = fieldId ?? "";

    [RelayCommand]
    private async Task DeleteTracker()
    {
        if (SelectedTracker is not { } t) return;
        if (!await _dialogs.ConfirmAsync("Delete collection?", $"“{t.Name}” and its {t.ItemCount} item(s) will be deleted.", "Delete", danger: true)) return;
        await _store.DeleteAsync(t);
        _items.RemoveAll(i => i.TrackerId == t.Id);
        Trackers.Remove(t);
        SelectedTracker = Trackers.FirstOrDefault();
        OnPropertyChanged(nameof(HasTrackers));
    }

    // ----- items -----

    [RelayCommand] private void Select(TrackerItem? i) => Selected = i;
    [RelayCommand] private void SelectRow(TableRow? r) => Selected = r?.Item;
    [RelayCommand] private void CloseDetail() => Selected = null;
    [RelayCommand] private void ClearSearch() => Search = "";

    [RelayCommand]
    private async Task AddItem()
    {
        if (SelectedTracker is not { } t) { await NewTracker(); return; }
        var title = await _dialogs.PromptAsync($"Add to {t.Name}", null, "", "Name", "Add");
        if (string.IsNullOrWhiteSpace(title)) return;
        var item = new TrackerItem { TrackerId = t.Id, Title = title.Trim(), SortOrder = _items.Count(i => i.TrackerId == t.Id) };
        await _store.AddAsync(item);
        _items.Add(item);
        t.ItemCount++;
        Apply();
        Selected = item;
    }

    [RelayCommand]
    private async Task DeleteItem()
    {
        if (Selected is not { } i) return;
        _dirty.Remove(i);
        await _store.DeleteAsync(i);
        _items.Remove(i);
        if (SelectedTracker != null) SelectedTracker.ItemCount--;
        Selected = null;
        Apply();
        _toasts.Show($"Deleted “{i.Title}”", ToastKind.Info, "Undo", async () =>
        {
            i.Id = 0;
            await _store.AddAsync(i);
            _items.Add(i);
            if (SelectedTracker != null) SelectedTracker.ItemCount++;
            Apply();
        });
    }

    [RelayCommand]
    private async Task DuplicateItem()
    {
        if (Selected is not { } i) return;
        var copy = new TrackerItem { TrackerId = i.TrackerId, Title = i.Title + " (copy)", ValuesJson = i.ValuesJson, Notes = i.Notes, Tags = i.Tags, Image = i.Image, SortOrder = i.SortOrder + 0.5 };
        await _store.AddAsync(copy);
        _items.Add(copy);
        if (SelectedTracker != null) SelectedTracker.ItemCount++;
        Apply();
        Selected = copy;
    }

    [RelayCommand]
    private void PickImage()
    {
        if (Selected == null) return;
        var img = ImageService.PickImage(ImageService.CoverSize, photo: true);
        if (img != null) Selected.Image = img;
    }

    [RelayCommand]
    private void PasteImage()
    {
        if (Selected == null) return;
        var img = ImageService.FromClipboard(ImageService.CoverSize, photo: true);
        if (img != null) Selected.Image = img;
        else _toasts.Info("Copy an image first");
    }

    [RelayCommand]
    private void RemoveImage()
    {
        if (Selected != null) Selected.Image = null;
    }

    public void Dispose()
    {
        _saveTimer.Stop();
        if (Selected != null) Selected.PropertyChanged -= OnItemChanged;
    }
}
