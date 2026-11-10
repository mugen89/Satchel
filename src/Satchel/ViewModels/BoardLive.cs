using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Satchel.Core;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>Data shown by a live card on a board (refreshed while the board is open).</summary>
public abstract partial class LiveCardData : ObservableObject
{
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    public abstract string Glyph { get; }
    public abstract string ColorKey { get; }
}

public sealed partial class LiveTasksData : LiveCardData
{
    public ObservableCollection<TaskItem> Items { get; } = [];
    [ObservableProperty] private int _more;
    public override string Glyph => Glyphs.Tasks;
    public override string ColorKey => "teal";
}

public sealed partial class LiveNoteData : LiveCardData
{
    [ObservableProperty] private Note? _note;
    public ObservableCollection<ChecklistItem> Items { get; } = [];
    public bool IsChecklist => Note?.IsItemList == true;
    public override string Glyph => Note?.TypeGlyph ?? Glyphs.Notes;
    public override string ColorKey => "amber";
    partial void OnNoteChanged(Note? value) => OnPropertyChanged(nameof(IsChecklist));
}

public sealed partial class LiveProjectData : LiveCardData
{
    [ObservableProperty] private Project? _project;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _detail = "";
    public override string Glyph => Glyphs.Projects;
    public override string ColorKey => Project?.Color ?? "teal";
}

public sealed partial class LiveCountdownData : LiveCardData
{
    [ObservableProperty] private Countdown? _countdown;
    public override string Glyph => Glyphs.Flag;
    public override string ColorKey => Countdown?.Color ?? "teal";
}

public sealed partial class LiveClockData : LiveCardData
{
    public LiveClockData(TimeZoneInfo zone) => Zone = zone;
    public TimeZoneInfo Zone { get; }
    [ObservableProperty] private string _time = "";
    [ObservableProperty] private string _date = "";
    [ObservableProperty] private double _hourAngle;
    [ObservableProperty] private double _minuteAngle;
    [ObservableProperty] private double _secondAngle;
    public override string Glyph => Glyphs.Clock;
    public override string ColorKey => "sky";

    public void Tick()
    {
        var now = TimeZoneInfo.ConvertTime(DateTime.Now, TimeZoneInfo.Local, Zone);
        Time = Format.Time(now);
        Date = now.ToString("ddd d MMM", System.Globalization.CultureInfo.CurrentCulture);
        HourAngle = (now.Hour % 12 + now.Minute / 60.0) * 30;
        MinuteAngle = (now.Minute + now.Second / 60.0) * 6;
        SecondAngle = now.Second * 6;
    }
}

public sealed partial class LiveWeatherData : LiveCardData
{
    [ObservableProperty] private WeatherReport? _report;
    [ObservableProperty] private string _unit = "°C";
    public override string Glyph => Glyphs.Sun;
    public override string ColorKey => "amber";
}

public sealed partial class LiveMusicData : LiveCardData
{
    public LiveMusicData(MusicService player) => Player = player;
    public MusicService Player { get; }
    public override string Glyph => Glyphs.Music;
    public override string ColorKey => "teal";
}

public sealed partial class LiveGameData : LiveCardData
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorKey))]
    private Game? _game;

    [ObservableProperty] private string _detail = "";
    public override string Glyph => Glyphs.Game;
    public override string ColorKey => Game?.Color ?? "sky";
}

public sealed partial class LiveBookData : LiveCardData
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorKey))]
    private Book? _book;

    [ObservableProperty] private string _detail = "";
    public override string Glyph => Glyphs.Book;
    public override string ColorKey => Book?.Color ?? "teal";
}

public sealed partial class LiveShowData : LiveCardData
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ColorKey))]
    private WatchItem? _item;

    [ObservableProperty] private string _detail = "";
    public override string Glyph => Glyphs.Monitor;
    public override string ColorKey => Item?.Color ?? "sky";
}

/// <summary>A choice in the "add live card" pickers.</summary>
public sealed record LiveTarget(string Kind, string Ref, string Title, int Width, int Height);
