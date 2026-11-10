using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.Models;

public enum WatchKind { Movie = 0, Show = 1 }
public enum WatchStatus { Watching = 0, Planned = 1, Completed = 2, OnHold = 3, Dropped = 4 }

/// <summary>One episode of a show (stored as JSON inside the show).</summary>
public sealed partial class Episode : ObservableObject
{
    public int Season { get; set; }
    public int Number { get; set; }
    public string Title { get; set; } = "";
    public string Airdate { get; set; } = "";
    public int Runtime { get; set; }
    [ObservableProperty] private bool _watched;
    public DateTime? WatchedAt { get; set; }

    [System.Text.Json.Serialization.JsonIgnore] public string Code => $"S{Season:00}E{Number:00}";
    [System.Text.Json.Serialization.JsonIgnore] public string Label => string.IsNullOrWhiteSpace(Title) ? Code : $"{Code} · {Title}";
    [System.Text.Json.Serialization.JsonIgnore] public bool HasAired =>
        string.IsNullOrEmpty(Airdate) || !DateTime.TryParse(Airdate, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d) || d.Date <= DateTime.Today;
}

/// <summary>A movie or TV show on the watch list.</summary>
public partial class WatchItem : Entity
{
    [ObservableProperty][property: Encrypted] private string _title = "";
    [ObservableProperty][property: Encrypted] private string _overview = "";
    [ObservableProperty][property: Encrypted] private string _genres = "";
    [ObservableProperty][property: Encrypted] private string _notes = "";
    [ObservableProperty][property: Encrypted] private string _network = "";
    [ObservableProperty][property: Encrypted] private string _url = "";
    [ObservableProperty][property: Encrypted] private string _tags = "";
    [ObservableProperty][property: Encrypted] private string _episodesJson = "";
    [ObservableProperty][property: Encrypted] private byte[]? _poster;
    [ObservableProperty] private WatchKind _kind;
    [ObservableProperty] private WatchStatus _status = WatchStatus.Planned;
    [ObservableProperty] private int _year;
    [ObservableProperty] private int _rating;
    [ObservableProperty] private int _runtime;
    /// <summary>"tvmaze:123" or "itunes:456" — where the details came from.</summary>
    [ObservableProperty] private string _externalId = "";
    [ObservableProperty] private DateTime? _startedAt;
    [ObservableProperty] private DateTime? _finishedAt;
    [ObservableProperty] private DateTime? _lastWatchedAt;
    [ObservableProperty] private double _sortOrder;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private string _color = "sky";

    private List<Episode>? _episodes;

    [NotMapped] public bool IsShow => Kind == WatchKind.Show;
    [NotMapped] public bool IsMovie => Kind == WatchKind.Movie;
    [NotMapped] public IReadOnlyList<Episode> Episodes => _episodes ??= ParseEpisodes(EpisodesJson);
    [NotMapped] public int EpisodeCount => Episodes.Count;
    [NotMapped] public int WatchedCount => Episodes.Count(e => e.Watched);
    [NotMapped] public Episode? NextEpisode => Episodes.FirstOrDefault(e => !e.Watched && e.HasAired);
    [NotMapped] public bool HasNextEpisode => NextEpisode != null;
    [NotMapped] public double Progress => Status == WatchStatus.Completed ? 1 : EpisodeCount == 0 ? 0 : (double)WatchedCount / EpisodeCount;

    [NotMapped] public string ProgressText => IsMovie
        ? (Runtime > 0 ? $"{Runtime / 60} h {Runtime % 60:00} min" : "Movie")
        : EpisodeCount == 0 ? "Show" : $"{WatchedCount} / {EpisodeCount} episodes";

    [NotMapped] public string StatusText => Status switch
    {
        WatchStatus.Watching => "Watching",
        WatchStatus.Planned => "Plan to watch",
        WatchStatus.Completed => "Completed",
        WatchStatus.OnHold => "On hold",
        _ => "Dropped"
    };

    [NotMapped] public string Subtitle => string.Join(" · ", new[] { Year > 0 ? Year.ToString() : "", IsShow ? "TV" : "Movie", Network }.Where(s => s.Length > 0));

    partial void OnEpisodesJsonChanged(string value) => RefreshEpisodes();
    partial void OnStatusChanged(WatchStatus value)
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Progress));
    }
    partial void OnKindChanged(WatchKind value)
    {
        OnPropertyChanged(nameof(IsShow));
        OnPropertyChanged(nameof(IsMovie));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(Subtitle));
    }
    partial void OnYearChanged(int value) => OnPropertyChanged(nameof(Subtitle));
    partial void OnNetworkChanged(string value) => OnPropertyChanged(nameof(Subtitle));
    partial void OnRuntimeChanged(int value) => OnPropertyChanged(nameof(ProgressText));

    public void RefreshEpisodes()
    {
        _episodes = null;
        OnPropertyChanged(nameof(Episodes));
        RaiseProgress();
    }

    public void RaiseProgress()
    {
        OnPropertyChanged(nameof(EpisodeCount));
        OnPropertyChanged(nameof(WatchedCount));
        OnPropertyChanged(nameof(NextEpisode));
        OnPropertyChanged(nameof(HasNextEpisode));
        OnPropertyChanged(nameof(Progress));
        OnPropertyChanged(nameof(ProgressText));
    }

    /// <summary>Writes the (possibly edited) episode list back into <see cref="EpisodesJson"/>.</summary>
    public void SaveEpisodes()
    {
        var list = Episodes.ToList();
        _episodes = list;
        EpisodesJson = JsonSerializer.Serialize(list);
        _episodes = list;
        RaiseProgress();
    }

    public static List<Episode> ParseEpisodes(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<Episode>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
