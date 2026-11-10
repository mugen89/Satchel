using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Controls;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;

namespace Satchel.ViewModels;

public sealed record InsightStat(string Value, string Label, string Glyph, string Page);

/// <summary>Insights: charts of what you do in Satchel — activity, tasks, reading, play, watching, mood, studying.</summary>
public sealed partial class InsightsViewModel : PageViewModel
{
    private readonly INavigator _nav;
    private readonly DataStore _store;
    private DateTime _gridStart;

    public InsightsViewModel(INavigator nav, DataStore store)
    {
        _nav = nav;
        _store = store;
    }

    public override string Title => "Insights";
    public ObservableCollection<InsightStat> Stats { get; } = [];
    public ObservableCollection<ChartPoint> Activity { get; } = [];
    public ObservableCollection<ChartPoint> TasksWeekly { get; } = [];
    public ObservableCollection<ChartPoint> PagesWeekly { get; } = [];
    public ObservableCollection<ChartPoint> HoursMonthly { get; } = [];
    public ObservableCollection<ChartPoint> WatchedWeekly { get; } = [];
    public ObservableCollection<ChartPoint> Mood { get; } = [];
    public ObservableCollection<ChartPoint> Reviews { get; } = [];

    [ObservableProperty] private string _activityText = "";
    [ObservableProperty] private string _moodText = "";
    [ObservableProperty] private string _reviewText = "";
    [ObservableProperty] private bool _hasMood;
    [ObservableProperty] private bool _hasReviews;

    public override async Task OnNavigatedToAsync(object? parameter) => await LoadAsync();

    private sealed record Raw(
        List<DateTime> TasksDone, List<DateTime> NotesCreated, List<(DateTime Date, int Mood)> Moods, List<(DateTime Date, int Pages)> Reading,
        List<(DateTime Date, int Minutes)> Play, List<DateTime> VideosWatched, List<DateTime> Episodes, List<DateTime> Movies, List<(DateTime At, int Grade)> Reviews,
        List<DateTime> Photos, List<DateTime> Bookmarks, List<DateTime> BooksFinished, List<DateTime> GamesFinished, List<DateTime> Journals);

    private async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var since = DateTime.Today.AddDays(-400);
            var raw = await _store.RunAsync(db =>
            {
                var watch = db.WatchItems.Select(w => new { w.Kind, w.FinishedAt, w.EpisodesJson }).ToList();
                return new Raw(
                    db.Tasks.Where(t => t.CompletedAt != null && t.CompletedAt >= since).Select(t => t.CompletedAt!.Value).ToList(),
                    db.Notes.Where(n => !n.IsTrashed && n.CreatedAt >= since).Select(n => n.CreatedAt).ToList(),
                    db.Notes.Where(n => n.Type == NoteType.Journal && n.Mood > 0 && n.JournalDate != null && n.JournalDate >= since).Select(n => new { n.JournalDate, n.Mood }).ToList()
                        .Select(n => (n.JournalDate!.Value.Date, n.Mood)).ToList(),
                    db.ReadingSessions.Where(s => s.Date >= since).Select(s => new { s.Date, s.Pages }).ToList().Select(s => (s.Date, s.Pages)).ToList(),
                    db.PlaySessions.Where(s => s.Date >= since).Select(s => new { s.Date, s.Minutes }).ToList().Select(s => (s.Date, s.Minutes)).ToList(),
                    db.Videos.Where(v => v.WatchedAt != null && v.WatchedAt >= since).Select(v => v.WatchedAt!.Value).ToList(),
                    watch.Where(w => w.Kind == WatchKind.Show).SelectMany(w => WatchItem.ParseEpisodes(w.EpisodesJson)).Where(e => e.WatchedAt >= since).Select(e => e.WatchedAt!.Value).ToList(),
                    watch.Where(w => w.Kind == WatchKind.Movie && w.FinishedAt >= since).Select(w => w.FinishedAt!.Value).ToList(),
                    db.ReviewLogs.Where(r => r.ReviewedAt >= since).Select(r => new { r.ReviewedAt, r.Grade }).ToList().Select(r => (r.ReviewedAt, r.Grade)).ToList(),
                    db.Photos.Where(p => p.CreatedAt >= since).Select(p => p.CreatedAt).ToList(),
                    db.Bookmarks.Where(b => b.CreatedAt >= since).Select(b => b.CreatedAt).ToList(),
                    db.Books.Where(b => b.FinishedAt != null).Select(b => b.FinishedAt!.Value).ToList(),
                    db.Games.Where(g => g.FinishedAt != null).Select(g => g.FinishedAt!.Value).ToList(),
                    db.Notes.Where(n => n.Type == NoteType.Journal && !n.IsTrashed && n.JournalDate != null && n.JournalDate >= since).Select(n => n.JournalDate!.Value).ToList());
            });
            Build(raw);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void Build(Raw r)
    {
        var today = DateTime.Today;
        var culture = CultureInfo.CurrentCulture;

        // ----- activity grid: 26 weeks, one cell per day -----
        _gridStart = Format.StartOfWeek(today.AddDays(-7 * 25));
        var perDay = new Dictionary<DateTime, int>();
        void Count(IEnumerable<DateTime> dates) { foreach (var d in dates) perDay[d.Date] = perDay.GetValueOrDefault(d.Date) + 1; }
        Count(r.TasksDone); Count(r.NotesCreated); Count(r.Reading.Select(x => x.Date)); Count(r.Play.Select(x => x.Date)); Count(r.VideosWatched);
        Count(r.Episodes); Count(r.Movies); Count(r.Photos.Select(d => d.Date).Distinct()); Count(r.Bookmarks); Count(r.Journals);
        Count(r.Reviews.Select(x => x.At.Date).Distinct());
        Activity.Clear();
        for (var d = _gridStart; d <= today; d = d.AddDays(1))
            Activity.Add(new ChartPoint(d.ToString("MMM", culture), perDay.GetValueOrDefault(d), d.ToString("ddd d MMM yyyy", culture)));
        int activeDays = Activity.Count(p => p.Value > 0);
        int streak = 0;
        for (var d = today; perDay.GetValueOrDefault(d) > 0; d = d.AddDays(-1)) streak++;
        ActivityText = $"{activeDays} active days in the last 6 months · {(streak == 0 ? "no streak today yet" : streak == 1 ? "1-day streak" : $"{streak}-day streak")}";

        // ----- weekly series (12 weeks) -----
        var weekStart = Format.StartOfWeek(today).AddDays(-7 * 11);
        List<ChartPoint> Weekly(IEnumerable<(DateTime Date, double Amount)> source, string unit)
        {
            var list = new List<ChartPoint>();
            var data = source.ToList();
            for (int i = 0; i < 12; i++)
            {
                var start = weekStart.AddDays(7 * i);
                var value = data.Where(x => x.Date >= start && x.Date < start.AddDays(7)).Sum(x => x.Amount);
                list.Add(new ChartPoint(start.ToString("d MMM", culture), value, $"Week of {start.ToString("d MMM", culture)}"));
            }
            return list;
        }
        Fill(TasksWeekly, Weekly(r.TasksDone.Select(d => (d, 1.0)), "tasks"));
        Fill(PagesWeekly, Weekly(r.Reading.Select(x => (x.Date, (double)x.Pages)), "pages"));
        Fill(WatchedWeekly, Weekly(r.VideosWatched.Concat(r.Episodes).Concat(r.Movies).Select(d => (d, 1.0)), "watched"));

        // ----- hours played per month (12 months) -----
        HoursMonthly.Clear();
        var month = new DateTime(today.Year, today.Month, 1).AddMonths(-11);
        for (int i = 0; i < 12; i++)
        {
            var m = month.AddMonths(i);
            var hours = r.Play.Where(x => x.Date >= m && x.Date < m.AddMonths(1)).Sum(x => x.Minutes) / 60.0;
            HoursMonthly.Add(new ChartPoint(m.ToString("MMM", culture), Math.Round(hours, 1), m.ToString("MMMM yyyy", culture)));
        }

        // ----- mood (journal), last 60 days -----
        Mood.Clear();
        var moods = r.Moods.GroupBy(m => m.Date).ToDictionary(g => g.Key, g => g.Average(x => x.Mood));
        for (var d = today.AddDays(-59); d <= today; d = d.AddDays(1))
            Mood.Add(new ChartPoint(d.ToString("d MMM", culture), moods.TryGetValue(d, out var v) ? v : -1, d.ToString("ddd d MMM", culture)));
        HasMood = moods.Keys.Any(d => d >= today.AddDays(-59));
        MoodText = HasMood ? $"Average {moods.Where(kv => kv.Key >= today.AddDays(-29)).Select(kv => kv.Value).DefaultIfEmpty(0).Average():0.0} over the last 30 days (1 rough … 5 great)" : "";

        // ----- flashcards, last 30 days -----
        Reviews.Clear();
        for (var d = today.AddDays(-29); d <= today; d = d.AddDays(1))
        {
            var day = r.Reviews.Where(x => x.At.Date == d).ToList();
            Reviews.Add(new ChartPoint(d.ToString("d MMM", culture), day.Count, d.ToString("ddd d MMM", culture)));
        }
        HasReviews = r.Reviews.Any(x => x.At >= today.AddDays(-29));
        var recent = r.Reviews.Where(x => x.At >= today.AddDays(-29)).ToList();
        ReviewText = recent.Count == 0 ? "" : $"{recent.Count} answers · {Math.Round(100.0 * recent.Count(x => x.Grade >= 3) / recent.Count)}% remembered";

        // ----- headline numbers -----
        Stats.Clear();
        var monthStart = new DateTime(today.Year, today.Month, 1);
        Stats.Add(new InsightStat(r.TasksDone.Count(d => d >= monthStart).ToString(culture), "tasks done this month", Glyphs.Completed, "Tasks"));
        Stats.Add(new InsightStat(r.NotesCreated.Count(d => d >= monthStart).ToString(culture), "notes this month", Glyphs.Notes, "Notes"));
        Stats.Add(new InsightStat(r.Reading.Where(x => x.Date.Year == today.Year).Sum(x => x.Pages).ToString("#,0", culture), "pages read this year", Glyphs.Book, "Reading"));
        Stats.Add(new InsightStat((r.Play.Where(x => x.Date.Year == today.Year).Sum(x => x.Minutes) / 60.0).ToString("0", culture), "hours played this year", Glyphs.Game, "Games"));
        Stats.Add(new InsightStat((r.BooksFinished.Count(d => d.Year == today.Year) + r.GamesFinished.Count(d => d.Year == today.Year)).ToString(culture), "books & games finished this year", Glyphs.Flag, "Reading"));
        Stats.Add(new InsightStat(r.Episodes.Count(d => d >= monthStart).ToString(culture), "episodes this month", Glyphs.Monitor, "Watching"));
    }

    private static void Fill(ObservableCollection<ChartPoint> target, IEnumerable<ChartPoint> points)
    {
        target.Clear();
        foreach (var p in points) target.Add(p);
    }

    /// <summary>Clicking a day in the activity grid opens it on Today (with its auto-diary).</summary>
    public void OpenDay(int index)
    {
        if (index >= 0) _nav.NavigateTo("Today", _gridStart.AddDays(index));
    }

    [RelayCommand]
    private void OpenStat(InsightStat? stat)
    {
        if (stat != null) _nav.NavigateTo(stat.Page);
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();
}
