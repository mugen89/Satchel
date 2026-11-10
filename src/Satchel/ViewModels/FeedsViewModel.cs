using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Satchel.Behaviors;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>Feeds: RSS / Atom subscriptions and podcasts, with a reader and a podcast player that keeps playing in the background.</summary>
public sealed partial class FeedsViewModel : PageViewModel, IDisposable
{
    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly FeedService _feeds;
    private readonly WebService _web;
    private List<FeedItem> _items = [];
    private bool _loaded;

    public FeedsViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, FeedService feeds, PodcastService podcast, WebService web)
    {
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _feeds = feeds;
        Podcast = podcast;
        _web = web;
        AllFilter = new SideFilter("all", "All", Glyphs.Feed);
        UnreadFilter = new SideFilter("unread", "Unread", Glyphs.Bell);
        StarredFilter = new SideFilter("starred", "Starred", Glyphs.Star);
        PodcastsFilter = new SideFilter("podcasts", "Episodes", Glyphs.Music);
        _selectedFilter = UnreadFilter;
        UnreadFilter.IsSelected = true;
        _feeds.Updated += OnFeedsUpdated;
        Podcast.Finished += OnEpisodeFinished;
    }

    /// <summary>The app-wide podcast player (it keeps playing on other pages).</summary>
    public PodcastService Podcast { get; }

    public override string Title => "Feeds";
    public SideFilter AllFilter { get; }
    public SideFilter UnreadFilter { get; }
    public SideFilter StarredFilter { get; }
    public SideFilter PodcastsFilter { get; }
    public IReadOnlyList<SideFilter> MainFilters => [UnreadFilter, AllFilter, StarredFilter, PodcastsFilter];
    public ObservableCollection<Feed> Feeds { get; } = [];
    public ObservableCollection<FeedItem> Items { get; } = [];

    [ObservableProperty] private SideFilter _selectedFilter;
    [ObservableProperty] private Feed? _selectedFeed;
    [ObservableProperty] private FeedItem? _selected;
    [ObservableProperty] private string _readerText = "";
    [ObservableProperty] private bool _isRefreshing;
    [ObservableProperty] private string _search = "";

    public bool HasSelection => Selected != null;
    public bool HasFeeds => Feeds.Count > 0;
    public bool IsEmpty => Items.Count == 0;
    public string HeaderTitle => SelectedFeed?.Title ?? SelectedFilter.Title;

    partial void OnSelectedFilterChanged(SideFilter? oldValue, SideFilter newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        newValue.IsSelected = true;
        SelectedFeed = null;
        Apply();
    }

    partial void OnSelectedFeedChanged(Feed? value)
    {
        if (value != null) { SelectedFilter.IsSelected = false; }
        Apply();
        OnPropertyChanged(nameof(HeaderTitle));
    }

    partial void OnSearchChanged(string value) => Apply();

    partial void OnSelectedChanged(FeedItem? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        ReaderText = value == null ? "" : FeedService.StripHtml(value.Content.Length > 0 ? value.Content : value.Summary);
        if (value is { IsRead: false }) _ = MarkReadAsync(value, true);
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is "add") await Subscribe();
        else if (parameter is ItemRef { Id: 0 } feedRef && Feeds.FirstOrDefault(f => f.Id == feedRef.ParentId) is { } feed) SelectedFeed = feed;
        else if (parameter is ItemRef postRef && _items.FirstOrDefault(i => i.Id == postRef.Id) is { } post)
        {
            SelectedFeed = Feeds.FirstOrDefault(f => f.Id == post.FeedId);
            Selected = post;
        }
    }

    public override void OnNewShortcut() => _ = Subscribe();

    private async void OnFeedsUpdated()
    {
        if (_loaded) await LoadAsync();
    }

    private async Task LoadAsync()
    {
        var keepFeed = SelectedFeed?.Id;
        var keepItem = Selected?.Id;
        var (feeds, items) = await _store.RunAsync(db => (db.Feeds.OrderBy(f => f.SortOrder).ToList(),
            db.FeedItems.OrderByDescending(i => i.PublishedAt).Take(3000).ToList()));
        var byId = feeds.ToDictionary(f => f.Id);
        foreach (var i in items)
            if (byId.TryGetValue(i.FeedId, out var f)) { i.FeedTitle = f.Title; i.FeedColor = f.Color; }
        _items = items;
        Feeds.Clear();
        foreach (var f in feeds)
        {
            f.UnreadCount = items.Count(i => i.FeedId == f.Id && !i.IsRead);
            Feeds.Add(f);
        }
        _loaded = true;
#pragma warning disable MVVMTK0034 // reload: keep the same feed and post selected without re-running the change handlers
        if (keepFeed is int fid) _selectedFeed = Feeds.FirstOrDefault(f => f.Id == fid);
        RefreshCounts();
        Apply();
        if (keepItem is int iid && Items.FirstOrDefault(i => i.Id == iid) is { } same) _selected = same;
#pragma warning restore MVVMTK0034
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectedFeed));
        OnPropertyChanged(nameof(HasFeeds));
    }

    private void RefreshCounts()
    {
        UnreadFilter.Count = _items.Count(i => !i.IsRead);
        AllFilter.Count = 0;
        StarredFilter.Count = _items.Count(i => i.IsStarred);
        PodcastsFilter.Count = _items.Count(i => i.IsAudio && !i.IsPlayed && !i.IsRead);
        foreach (var f in Feeds) f.UnreadCount = _items.Count(i => i.FeedId == f.Id && !i.IsRead);
        (_nav as ShellViewModel)?.SetBadge("Feeds", UnreadFilter.Count);
    }

    private void Apply()
    {
        IEnumerable<FeedItem> items = _items;
        if (SelectedFeed != null) items = items.Where(i => i.FeedId == SelectedFeed.Id);
        else if (SelectedFilter == UnreadFilter) items = items.Where(i => !i.IsRead);
        else if (SelectedFilter == StarredFilter) items = items.Where(i => i.IsStarred);
        else if (SelectedFilter == PodcastsFilter) items = items.Where(i => i.IsAudio);
        var q = Search.Trim();
        if (q.Length > 0) items = items.Where(i => i.Title.Contains(q, StringComparison.CurrentCultureIgnoreCase) || i.Summary.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        Items.Clear();
        foreach (var i in items.Take(500)) Items.Add(i);
        OnPropertyChanged(nameof(IsEmpty));
    }

    // ----- navigation -----

    [RelayCommand]
    private void SelectFilter(SideFilter? f)
    {
        if (f == null) return;
        SelectedFeed = null;
        if (!ReferenceEquals(SelectedFilter, f)) SelectedFilter = f;
        f.IsSelected = true;
        Apply();
        OnPropertyChanged(nameof(HeaderTitle));
    }
    [RelayCommand] private void SelectFeed(Feed? f) { if (f != null) SelectedFeed = f; }
    [RelayCommand] private void Select(FeedItem? i) => Selected = i;
    [RelayCommand] private void CloseReader() => Selected = null;
    [RelayCommand] private void ClearSearch() => Search = "";

    // ----- feeds -----

    [RelayCommand]
    private async Task Subscribe()
    {
        var url = await _dialogs.PromptAsync("Follow a site or podcast", "Paste the address of a blog, news site or podcast (or its RSS / Atom feed). Satchel finds the feed.", "", "https://…", "Follow");
        if (string.IsNullOrWhiteSpace(url)) return;
        await SubscribeUrlAsync(url);
    }

    private async Task<bool> SubscribeUrlAsync(string url, bool quiet = false)
    {
        IsRefreshing = true;
        try
        {
            var (feed, parsed, error) = await _feeds.ResolveAsync(url);
            if (feed == null)
            {
                if (!quiet) _toasts.Warning(error ?? "Couldn't add that feed");
                return false;
            }
            if (Feeds.FirstOrDefault(f => f.Url.Equals(feed.Url, StringComparison.OrdinalIgnoreCase)) is { } existing)
            {
                if (!quiet) { _toasts.Info($"You already follow {existing.Title}"); SelectedFeed = existing; }
                return false;
            }
            feed.SortOrder = Feeds.Count;
            await _store.AddAsync(feed);
            await _feeds.RefreshAsync(feed, [], firstImport: true, parsed);
            await LoadAsync();
            if (!quiet)
            {
                SelectedFeed = Feeds.FirstOrDefault(f => f.Id == feed.Id);
                _toasts.Success($"Following {feed.Title}");
            }
            await _feeds.PublishUnreadAsync();
            return true;
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    private async Task RefreshAll()
    {
        if (!_web.IsAvailable) { _toasts.Info(_web.Enabled ? "You're offline" : "Online features are turned off in Settings"); return; }
        if (Feeds.Count == 0) { await Subscribe(); return; }
        IsRefreshing = true;
        var added = await _feeds.RefreshAllAsync(notify: false);
        IsRefreshing = false;
        _toasts.Info(added == 0 ? "Nothing new" : added == 1 ? "1 new post" : $"{added} new posts and episodes");
    }

    [RelayCommand]
    private async Task Unsubscribe(Feed? feed)
    {
        feed ??= SelectedFeed;
        if (feed == null) return;
        if (!await _dialogs.ConfirmAsync("Unfollow?", $"Stop following {feed.Title}? Its posts are removed (starred ones too).", "Unfollow", danger: true)) return;
        if (Podcast.NowPlaying?.FeedId == feed.Id) Podcast.Stop();
        await _store.DeleteAsync(feed);
        SelectedFeed = null;
        await LoadAsync();
        await _feeds.PublishUnreadAsync();
    }

    [RelayCommand]
    private async Task MarkAllRead()
    {
        var targets = Items.Where(i => !i.IsRead).ToList();
        if (targets.Count == 0) return;
        foreach (var i in targets) i.IsRead = true;
        await _store.UpdateRangeAsync(targets);
        RefreshCounts();
        if (SelectedFilter == UnreadFilter && SelectedFeed == null) Apply();
        await _feeds.PublishUnreadAsync();
    }

    [RelayCommand]
    private async Task ToggleNotify(Feed? feed)
    {
        if (feed == null) return;
        feed.Notify = !feed.Notify;
        await _store.UpdateAsync(feed);
        _toasts.Info(feed.Notify ? $"You'll be told about new posts from {feed.Title}" : $"No more notifications for {feed.Title}");
    }

    // ----- items -----

    private async Task MarkReadAsync(FeedItem item, bool read)
    {
        if (item.IsRead == read) return;
        item.IsRead = read;
        await _store.UpdateAsync(item);
        RefreshCounts();
    }

    [RelayCommand] private Task ToggleRead(FeedItem? item) => item == null ? Task.CompletedTask : MarkReadAsync(item, !item.IsRead);

    [RelayCommand]
    private async Task ToggleStar(FeedItem? item)
    {
        item ??= Selected;
        if (item == null) return;
        item.IsStarred = !item.IsStarred;
        await _store.UpdateAsync(item);
        RefreshCounts();
    }

    [RelayCommand]
    private void OpenInBrowser(FeedItem? item)
    {
        item ??= Selected;
        if (item is { Link.Length: > 0 }) UiCommands.OpenUrl.Execute(item.Link);
    }

    [RelayCommand]
    private async Task SaveToBookmarks()
    {
        if (Selected is not { Link.Length: > 0 } item) return;
        await _store.AddAsync(new Bookmark { Url = item.Link, Title = item.Title, Description = item.Summary.Length > 300 ? item.Summary[..300] + "…" : item.Summary, IsReadLater = true, Folder = "From feeds" });
        _toasts.Success("Saved to Bookmarks (read later)");
    }

    // ----- podcast player (playback lives in PodcastService) -----

    [RelayCommand]
    private void PlayEpisode(FeedItem? item)
    {
        item ??= Selected;
        if (item is not { IsAudio: true }) return;
        if (!item.IsRead) _ = MarkReadAsync(item, true);
        Podcast.Play(item);
    }

    public void SeekFraction(double fraction) => Podcast.SeekFraction(fraction);

    /// <summary>Keeps the list's copy in step when the episode that finished came from an older load.</summary>
    private void OnEpisodeFinished(FeedItem item)
    {
        if (_items.FirstOrDefault(i => i.Id == item.Id) is { } same && !ReferenceEquals(same, item))
        {
            same.IsPlayed = true;
            same.Position = 0;
        }
    }

    // ----- OPML -----

    [RelayCommand]
    private async Task ImportOpml()
    {
        var dialog = new OpenFileDialog { Title = "Import feeds", Filter = "OPML (*.opml;*.xml)|*.opml;*.xml|All files|*.*" };
        if (dialog.ShowDialog() != true) return;
        var list = FeedService.ParseOpml(await File.ReadAllTextAsync(dialog.FileName));
        if (list.Count == 0) { _toasts.Info("No feeds found in that file"); return; }
        if (!_web.IsAvailable) { _toasts.Info("You need to be online to add feeds"); return; }
        int added = 0;
        foreach (var (_, url) in list)
            if (await SubscribeUrlAsync(url, quiet: true)) added++;
        _toasts.Success($"Added {added} of {list.Count} feeds");
    }

    [RelayCommand]
    private async Task ExportOpml()
    {
        var dialog = new SaveFileDialog { Title = "Export feeds", Filter = "OPML (*.opml)|*.opml", FileName = "satchel-feeds.opml" };
        if (dialog.ShowDialog() != true) return;
        await File.WriteAllTextAsync(dialog.FileName, FeedService.ToOpml(Feeds), Encoding.UTF8);
        _toasts.Success($"Exported {Feeds.Count} feeds");
    }

    public void Dispose()
    {
        _feeds.Updated -= OnFeedsUpdated;
        Podcast.Finished -= OnEpisodeFinished;
    }
}
