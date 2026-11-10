using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;
using Satchel.Models;

namespace Satchel.ViewModels;

/// <summary>Active projects with their progress.</summary>
public sealed partial class ProjectsWidget : WidgetViewModel
{
    public ProjectsWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "projects";
    public override string Title => "Projects";
    public override string Glyph => Glyphs.Projects;
    public override string ActionText => "All projects";
    public override string ActionPage => "Projects";
    public ObservableCollection<Project> Items { get; } = [];
    protected override int DefaultCapacity => 4;

    public override void Update(DashboardData data)
    {
        Items.Clear();
        foreach (var p in data.Projects.Where(p => p.Status is ProjectStatus.Active or ProjectStatus.Planning)
                     .OrderByDescending(p => p.IsPinned).ThenBy(p => p.DueDate ?? DateTime.MaxValue).Take(Capacity))
        {
            var tasks = p.TaskListId == null ? [] : data.Tasks.Where(t => t.ListId == p.TaskListId).ToList();
            p.TaskTotal = tasks.Count;
            p.TaskDone = tasks.Count(t => t.State == TaskState.Done);
            var milestones = data.Milestones.Where(m => m.ProjectId == p.Id).ToList();
            p.MilestoneTotal = milestones.Count;
            p.MilestoneDone = milestones.Count(m => m.IsDone);
            Items.Add(p);
        }
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private void Open(Project? p)
    {
        if (p != null) Owner.Navigator.NavigateTo("Projects", p.Id);
    }

    [RelayCommand]
    private void Create() => Owner.Navigator.NavigateTo("Projects", "new");
}

/// <summary>Books in progress.</summary>
public sealed partial class ReadingWidget : WidgetViewModel
{
    public ReadingWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "reading";
    public override string Title => "Continue reading";
    public override string Glyph => Glyphs.Book;
    public override string ActionText => "Bookshelf";
    public override string ActionPage => "Reading";
    public ObservableCollection<Book> Items { get; } = [];
    protected override int DefaultCapacity => 3;

    public override void Update(DashboardData data)
    {
        Items.Clear();
        foreach (var b in data.Books.Where(b => b.Status == BookStatus.Reading).OrderByDescending(b => b.UpdatedAt).Take(Capacity)) Items.Add(b);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private void Open(Book? b)
    {
        if (b != null) Owner.Navigator.NavigateTo("Reading", b.Id);
    }

    [RelayCommand]
    private void Create() => Owner.Navigator.NavigateTo("Reading", "new");
}

/// <summary>Fresh uploads from followed YouTube channels.</summary>
public sealed partial class VideosWidget : WidgetViewModel
{
    public VideosWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "videos";
    public override string Title => "New videos";
    public override string Glyph => Glyphs.Video;
    public override int DefaultSpan => 2;
    public override string ActionText => "Videos";
    public override string ActionPage => "Videos";
    public ObservableCollection<Video> Items { get; } = [];
    public bool HasChannels { get; private set; }

    /// <summary>About one row of thumbnails for the width (two rows when it's one column), all of them when it has a fixed height.</summary>
    protected override int Capacity => IsFixedHeight ? 16 : Math.Max(4, Span * 2);

    public override void Update(DashboardData data)
    {
        Items.Clear();
        foreach (var v in data.NewVideos.Take(Capacity)) Items.Add(v);
        HasChannels = data.ChannelCount > 0;
        OnPropertyChanged(nameof(HasChannels));
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private void Open(Video? v)
    {
        if (v != null) Owner.Navigator.NavigateTo("Videos", v.Id);
    }

    [RelayCommand]
    private void Follow() => Owner.Navigator.NavigateTo("Videos", "follow");
}

/// <summary>Games currently being played.</summary>
public sealed partial class GamesWidget : WidgetViewModel
{
    public GamesWidget(DashboardViewModel owner) : base(owner) { }
    public override string Key => "games";
    public override string Title => "Now playing";
    public override string Glyph => Glyphs.Game;
    public override string ActionText => "Games";
    public override string ActionPage => "Games";
    public ObservableCollection<Game> Items { get; } = [];
    protected override int DefaultCapacity => 3;

    public override void Update(DashboardData data)
    {
        Items.Clear();
        foreach (var g in data.Games.Where(g => g.Status == GameStatus.Playing).OrderByDescending(g => g.LastPlayedAt ?? g.UpdatedAt).Take(Capacity)) Items.Add(g);
        IsEmpty = Items.Count == 0;
    }

    [RelayCommand]
    private void Open(Game? g)
    {
        if (g != null) Owner.Navigator.NavigateTo("Games", g.Id);
    }

    [RelayCommand]
    private void Create() => Owner.Navigator.NavigateTo("Games", "new");
}
