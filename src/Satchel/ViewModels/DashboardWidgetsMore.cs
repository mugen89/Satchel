using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

namespace Satchel.ViewModels;

/// <summary>Current weather and a 5-day forecast (Open-Meteo; the city is kept encrypted).</summary>
public sealed partial class WeatherWidget : WidgetViewModel
{
    public WeatherWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "weather";
    public override string Title => "Weather";
    public override string Glyph => Glyphs.Sun;

    [ObservableProperty] private WeatherReport? _report;
    [ObservableProperty] private bool _hasLocation = true;
    [ObservableProperty] private bool _isLoading;

    public ObservableCollection<WeatherDay> Days { get; } = [];
    public string Unit => Owner.Weather.Unit;
    public bool HasReport => Report != null;
    public bool IsOffline => !Owner.Weather.IsOnline;

    partial void OnReportChanged(WeatherReport? value)
    {
        Days.Clear();
        if (value != null) foreach (var d in value.Days.Skip(1).Take(5)) Days.Add(d);
        OnPropertyChanged(nameof(HasReport));
    }

    public override void Update(DashboardData data) => _ = LoadAsync(false);

    private async Task LoadAsync(bool force)
    {
        IsLoading = true;
        try
        {
            HasLocation = await Owner.Weather.GetLocationAsync() != null;
            Report = HasLocation ? await Owner.Weather.GetAsync(force) : null;
            OnPropertyChanged(nameof(Unit));
            OnPropertyChanged(nameof(IsOffline));
        }
        finally
        {
            IsLoading = false;
        }
        IsEmpty = Report == null;
    }

    [RelayCommand]
    private async Task SetCity()
    {
        var name = await Owner.Dialogs.PromptAsync("Your city", "Weather comes from Open-Meteo (free, no account). The city is stored encrypted.", "", "e.g. Sofia", "Search");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (!Owner.Weather.IsOnline) { Owner.Toasts.Info("You're offline (or online features are off in Settings)"); return; }
        var results = await Owner.Weather.SearchAsync(name);
        if (results.Count == 0) { Owner.Toasts.Warning("No place found with that name"); return; }
        WeatherLocation? location = results[0];
        if (results.Count > 1)
        {
            var picked = await Owner.Dialogs.ShowAsync(new PickDialogViewModel("Which one?", null,
                results.Select(r => new PickOption(r.Name, string.Join(", ", new[] { r.Region, r.Country }.Where(s => s.Length > 0)), Glyphs.MapPin, r))));
            location = (picked as PickOption)?.Value as WeatherLocation;
        }
        if (location == null) return;
        await Owner.Weather.SetLocationAsync(location);
        await LoadAsync(true);
    }

    [RelayCommand]
    private async Task ToggleUnit()
    {
        Owner.Settings.Current.WeatherFahrenheit = !Owner.Settings.Current.WeatherFahrenheit;
        await LoadAsync(true);
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync(true);
}

/// <summary>Local time plus the world clocks chosen on the Clock page.</summary>
public sealed partial class ClocksWidget : WidgetViewModel
{
    public ClocksWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "clocks";
    public override string Title => "World clock";
    public override string Glyph => Glyphs.World;
    public override string ActionText => "Clock";
    public override string ActionPage => "Clock";
    public ObservableCollection<WorldClockRow> Rows { get; } = [];

    public override void Update(DashboardData data)
    {
        Rows.Clear();
        foreach (var c in Owner.Settings.Current.WorldClocks)
        {
            try { Rows.Add(new WorldClockRow(c, TimeZoneInfo.FindSystemTimeZoneById(c.ZoneId))); }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        IsEmpty = Rows.Count == 0;
    }

    public void Tick()
    {
        foreach (var r in Rows) r.Update();
    }

    [RelayCommand]
    private void AddCity() => Owner.Navigator.NavigateTo("Clock");
}

/// <summary>Countdowns marked "show on the dashboard".</summary>
public sealed partial class CountdownsWidget : WidgetViewModel
{
    public CountdownsWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "countdowns";
    public override string Title => "Countdowns";
    public override string Glyph => Glyphs.Flag;
    public override string ActionText => "All";
    public override string ActionPage => "Clock";
    public ObservableCollection<Countdown> Items { get; } = [];
    protected override int DefaultCapacity => 5;

    public override void Update(DashboardData data)
    {
        Items.Clear();
        foreach (var c in data.Countdowns.Where(c => c.OnDashboard && c.DaysLeft >= 0).OrderBy(c => c.DaysLeft).Take(Capacity)) Items.Add(c);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private void Create() => Owner.Navigator.NavigateTo("Clock", "countdown");
}

/// <summary>A quote a day.</summary>
public sealed partial class QuoteWidget : WidgetViewModel
{
    private int _index = Quotes.IndexForToday();

    public QuoteWidget(DashboardViewModel owner) : base(owner) => Current = Quotes.All[_index];
    public override string Key => "quote";
    public override string Title => "Quote of the day";
    public override string Glyph => Glyphs.Quote;

    [ObservableProperty] private Quote _current;

    public override void Update(DashboardData data) { }

    [RelayCommand]
    private void Another()
    {
        _index = (_index + 1 + Random.Shared.Next(Quotes.All.Count - 1)) % Quotes.All.Count;
        Current = Quotes.All[_index];
    }
}

/// <summary>Rediscover: a random older note, to bring forgotten ideas back.</summary>
public sealed partial class RediscoverWidget : WidgetViewModel
{
    private List<Note> _pool = [];

    public RediscoverWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "rediscover";
    public override string Title => "From your notes";
    public override string Glyph => Glyphs.Lightbulb;
    public override string ActionText => "Notes";
    public override string ActionPage => "Notes";

    [ObservableProperty] private Note? _note;
    [ObservableProperty] private string _age = "";

    public override void Update(DashboardData data)
    {
        var cutoff = DateTime.Now.AddDays(-14);
        _pool = data.Notes.Where(n => !n.IsTrashed && n.UpdatedAt < cutoff && (n.HasPreview || n.Title.Length > 0 || n.ItemCount > 0)).ToList();
        if (_pool.Count == 0) _pool = data.Notes.Where(n => !n.IsTrashed && n.CreatedAt < DateTime.Now.AddDays(-2)).ToList();
        Pick();
    }

    private void Pick()
    {
        // Same note all day unless you ask for another one.
        Note = _pool.Count == 0 ? null : _pool[(DateTime.Today.DayOfYear * 31 + _offset) % _pool.Count];
        Age = Note == null ? "" : $"Written {Format.Relative(Note.CreatedAt)}";
        IsEmpty = Note == null;
    }

    private int _offset;

    [RelayCommand]
    private void Another()
    {
        _offset += 1 + Random.Shared.Next(Math.Max(1, _pool.Count - 1));
        Pick();
    }

    [RelayCommand]
    private void Open()
    {
        if (Note != null) Owner.Navigator.NavigateTo("Notes", Note.Id);
    }
}

/// <summary>What's playing (or a shortcut to start some music).</summary>
public sealed partial class MusicWidget : WidgetViewModel
{
    public MusicWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "music";
    public override string Title => "Music";
    public override string Glyph => Glyphs.Music;
    public override string ActionText => "Music";
    public override string ActionPage => "Music";
    public MusicService Player => Owner.Music;

    public override void Update(DashboardData data) => IsEmpty = !Player.HasTrack;
}
