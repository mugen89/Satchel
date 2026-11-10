using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using Microsoft.Win32;
using Satchel.Core;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels.Dialogs;

namespace Satchel.ViewModels;

public sealed record PhotoGroup(string Title, IReadOnlyList<Photo> Photos);

/// <summary>Photos: a gallery for photos and screenshots — albums, favorites, a full-screen viewer with zoom and slideshow.</summary>
public sealed partial class PhotosViewModel : PageViewModel, IDisposable
{
    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private readonly DispatcherTimer _slideshow;
    private List<Photo> _all = [];
    private bool _loaded;
    private int _limit = 400;

    public PhotosViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings)
    {
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        AllFilter = new SideFilter("all", "All photos", Glyphs.Photo);
        FavoritesFilter = new SideFilter("fav", "Favorites", Glyphs.Heart);
        ScreenshotsFilter = new SideFilter("shots", "Screenshots", Glyphs.Monitor);
        RecentFilter = new SideFilter("recent", "Added lately", Glyphs.History);
        _selectedFilter = AllFilter;
        AllFilter.IsSelected = true;
        _slideshow = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
        _slideshow.Tick += (_, _) => Step(+1, wrap: true);
    }

    public override string Title => "Photos";
    public SideFilter AllFilter { get; }
    public SideFilter FavoritesFilter { get; }
    public SideFilter ScreenshotsFilter { get; }
    public SideFilter RecentFilter { get; }
    public IReadOnlyList<SideFilter> MainFilters => [AllFilter, FavoritesFilter, ScreenshotsFilter, RecentFilter];
    public ObservableCollection<Album> Albums { get; } = [];
    public ObservableCollection<SideFilter> AlbumFilters { get; } = [];
    public ObservableCollection<PhotoGroup> Groups { get; } = [];
    public List<Photo> Visible { get; private set; } = [];

    [ObservableProperty] private SideFilter _selectedFilter;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private string _importText = "";
    [ObservableProperty] private bool _selectMode;

    // viewer
    [ObservableProperty] private Photo? _viewing;
    [ObservableProperty] private BitmapSource? _viewImage;
    [ObservableProperty] private bool _isSlideshow;
    [ObservableProperty] private bool _showInfo = true;

    public bool IsViewing => Viewing != null;
    public bool IsEmpty => Visible.Count == 0;
    public bool HasMore => _moreCount > 0;
    private int _moreCount;
    public string MoreText => $"Show {Math.Min(_moreCount, 400)} more";
    public int CheckedCount => Visible.Count(p => p.IsChecked);
    public string ViewPosition => Viewing == null ? "" : $"{Visible.IndexOf(Viewing) + 1} of {Visible.Count}";
    public string ViewAlbum => Viewing?.AlbumId is int id ? Albums.FirstOrDefault(a => a.Id == id)?.Name ?? "" : "";

    public double ThumbSize => _settings.Current.PhotosThumbSize switch { "S" => 120, "L" => 250, _ => 170 };
    public string ThumbSizeKey
    {
        get => _settings.Current.PhotosThumbSize;
        set { _settings.Current.PhotosThumbSize = value; OnPropertyChanged(); OnPropertyChanged(nameof(ThumbSize)); }
    }

    partial void OnSelectedFilterChanged(SideFilter? oldValue, SideFilter newValue)
    {
        if (oldValue != null) oldValue.IsSelected = false;
        newValue.IsSelected = true;
        _limit = 400;
        Apply();
    }

    partial void OnSearchChanged(string value) => Apply();

    partial void OnViewingChanged(Photo? value)
    {
        OnPropertyChanged(nameof(IsViewing));
        OnPropertyChanged(nameof(ViewPosition));
        OnPropertyChanged(nameof(ViewAlbum));
        _ = LoadFullAsync(value);
        if (value == null) StopSlideshow();
    }

    partial void OnSelectModeChanged(bool value)
    {
        if (!value) foreach (var p in _all) p.IsChecked = false;
        OnPropertyChanged(nameof(CheckedCount));
    }

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is int id && _all.FirstOrDefault(p => p.Id == id) is { } photo) Viewing = photo;
        else if (parameter is "import") await ImportFiles();
    }

    public override void OnNewShortcut() => _ = ImportFiles();

    private async Task LoadAsync()
    {
        // The full images stay in the database until a photo is opened; the grid only needs thumbnails.
        var (photos, albums) = await _store.RunAsync(db => (
            db.Photos.Select(p => new Photo
            {
                Id = p.Id, AlbumId = p.AlbumId, Caption = p.Caption, FileName = p.FileName, Tags = p.Tags, Thumb = p.Thumb, Width = p.Width, Height = p.Height, Size = p.Size,
                TakenAt = p.TakenAt, IsFavorite = p.IsFavorite, IsScreenshot = p.IsScreenshot, SortOrder = p.SortOrder, CreatedAt = p.CreatedAt, UpdatedAt = p.UpdatedAt, IsPrivate = p.IsPrivate
            }).ToList(),
            db.Albums.OrderBy(a => a.SortOrder).ToList()));
        _all = photos;
        foreach (var p in _all) p.PropertyChanged += OnPhotoChecked;
        Albums.Clear();
        foreach (var a in albums) Albums.Add(a);
        _loaded = true;
        RefreshSide();
        Apply();
    }

    private void OnPhotoChecked(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Photo.IsChecked)) OnPropertyChanged(nameof(CheckedCount));
    }

    private void RefreshSide()
    {
        AllFilter.Count = _all.Count;
        FavoritesFilter.Count = _all.Count(p => p.IsFavorite);
        ScreenshotsFilter.Count = _all.Count(p => p.IsScreenshot);
        var key = SelectedFilter.Key;
        AlbumFilters.Clear();
        foreach (var a in Albums)
        {
            a.PhotoCount = _all.Count(p => p.AlbumId == a.Id);
            a.CoverThumb = _all.FirstOrDefault(p => p.Id == a.CoverPhotoId)?.Thumb ?? _all.Where(p => p.AlbumId == a.Id).OrderByDescending(p => p.TakenAt).FirstOrDefault()?.Thumb;
            var f = new SideFilter("album:" + a.Id, a.Name, Glyphs.Folder, a, a.Color) { Count = a.PhotoCount };
            AlbumFilters.Add(f);
#pragma warning disable MVVMTK0034 // same album after a rebuild: swap the instance without re-filtering
            if (f.Key == key) { _selectedFilter = f; f.IsSelected = true; }
#pragma warning restore MVVMTK0034
        }
    }

    private void Apply()
    {
        IEnumerable<Photo> items = _all;
        var f = SelectedFilter;
        if (f == FavoritesFilter) items = items.Where(p => p.IsFavorite);
        else if (f == ScreenshotsFilter) items = items.Where(p => p.IsScreenshot);
        else if (f == RecentFilter) items = items.Where(p => p.CreatedAt > DateTime.Now.AddDays(-30));
        else if (f.Payload is Album album) items = items.Where(p => p.AlbumId == album.Id);
        var q = Search.Trim();
        if (q.Length > 0) items = items.Where(p => p.Caption.Contains(q, StringComparison.CurrentCultureIgnoreCase) || p.FileName.Contains(q, StringComparison.CurrentCultureIgnoreCase) ||
                                                   p.Tags.Contains(q, StringComparison.CurrentCultureIgnoreCase));
        var ordered = f == RecentFilter ? items.OrderByDescending(p => p.CreatedAt).ToList() : items.OrderByDescending(p => p.TakenAt).ToList();
        _moreCount = Math.Max(0, ordered.Count - _limit);
        Visible = ordered.Take(_limit).ToList();
        // Grouped by month, like a phone gallery.
        Groups.Clear();
        foreach (var g in Visible.GroupBy(p => f == RecentFilter ? new DateTime(p.CreatedAt.Year, p.CreatedAt.Month, 1) : new DateTime(p.TakenAt.Year, p.TakenAt.Month, 1)))
            Groups.Add(new PhotoGroup(g.Key.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture), g.ToList()));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(MoreText));
        OnPropertyChanged(nameof(CheckedCount));
    }

    [RelayCommand]
    private void ShowMore()
    {
        _limit += 400;
        Apply();
    }

    // ----- navigation -----

    [RelayCommand] private void SelectFilter(SideFilter? filter) { if (filter != null) SelectedFilter = filter; }
    [RelayCommand] private void SetThumbSize(string? size) => ThumbSizeKey = size ?? "M";
    [RelayCommand] private void ClearSearch() => Search = "";
    [RelayCommand] private void ToggleSelectMode() => SelectMode = !SelectMode;

    [RelayCommand]
    private void Open(Photo? photo)
    {
        if (photo == null) return;
        if (SelectMode)
        {
            photo.IsChecked = !photo.IsChecked;
            return;
        }
        Viewing = photo;
    }

    // ----- importing -----

    [RelayCommand]
    private async Task ImportFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add photos", Multiselect = true,
            Filter = "Images|*.jpg;*.jpeg;*.jfif;*.png;*.gif;*.bmp;*.tif;*.tiff;*.webp;*.heic|All files|*.*"
        };
        if (dialog.ShowDialog() != true) return;
        await ImportPathsAsync(dialog.FileNames);
    }

    [RelayCommand]
    private async Task ImportFolder()
    {
        var dialog = new OpenFolderDialog { Title = "Add a folder of photos (subfolders too)" };
        if (dialog.ShowDialog() != true) return;
        var files = Directory.EnumerateFiles(dialog.FolderName, "*.*", SearchOption.AllDirectories).Where(ImageService.IsImageFile).ToList();
        if (files.Count == 0) { _toasts.Info("No pictures in that folder"); return; }
        if (files.Count > 50 && !await _dialogs.ConfirmAsync($"Add {files.Count} pictures?", "They're copied into Satchel's encrypted database (up to 2048 px each), so the drive needs room for them.", "Add them")) return;
        await ImportPathsAsync(files, albumName: Path.GetFileName(dialog.FolderName));
    }

    public async Task ImportPathsAsync(IReadOnlyList<string> files, string? albumName = null)
    {
        if (IsImporting || files.Count == 0) return;
        IsImporting = true;
        int added = 0, failed = 0;
        try
        {
            int? albumId = SelectedFilter.Payload is Album current ? current.Id : null;
            if (albumName != null && albumId == null)
            {
                var album = Albums.FirstOrDefault(a => a.Name.Equals(albumName, StringComparison.CurrentCultureIgnoreCase)) ?? await CreateAlbumAsync(albumName);
                albumId = album.Id;
            }
            var batch = new List<Photo>();
            for (int i = 0; i < files.Count; i++)
            {
                ImportText = $"Adding {i + 1} of {files.Count}…";
                var path = files[i];
                var photo = await Task.Run(() => PhotoImporter.FromFile(path));
                if (photo == null) { failed++; continue; }
                photo.AlbumId = albumId;
                if (SelectedFilter == FavoritesFilter) photo.IsFavorite = true;
                batch.Add(photo);
                if (batch.Count >= 8 || i == files.Count - 1)
                {
                    await SaveBatchAsync(batch);
                    added += batch.Count;
                    batch.Clear();
                }
            }
            if (batch.Count > 0) { await SaveBatchAsync(batch); added += batch.Count; }
        }
        finally
        {
            IsImporting = false;
            ImportText = "";
        }
        RefreshSide();
        Apply();
        _toasts.Success(failed == 0 ? $"Added {added} photo{(added == 1 ? "" : "s")}" : $"Added {added} — {failed} couldn't be read");
    }

    private async Task SaveBatchAsync(List<Photo> batch)
    {
        await _store.AddRangeAsync(batch);
        foreach (var p in batch)
        {
            p.Data = null; // keep only the thumbnail in memory
            p.PropertyChanged += OnPhotoChecked;
            _all.Add(p);
        }
    }

    public async Task ImportImagesAsync(IEnumerable<byte[]> images)
    {
        var list = new List<Photo>();
        foreach (var bytes in images)
        {
            var photo = await Task.Run(() => PhotoImporter.FromBytes(bytes, $"Pasted {DateTime.Now:yyyy-MM-dd HHmm}", DateTime.Now));
            if (photo == null) continue;
            photo.AlbumId = SelectedFilter.Payload is Album a ? a.Id : null;
            list.Add(photo);
        }
        if (list.Count == 0) return;
        await SaveBatchAsync(list);
        RefreshSide();
        Apply();
        _toasts.Success(list.Count == 1 ? "Photo added" : $"{list.Count} photos added");
    }

    // ----- albums -----

    [RelayCommand]
    private async Task NewAlbum()
    {
        var name = await _dialogs.PromptAsync("New album", null, "", "e.g. Summer 2026", "Create");
        if (string.IsNullOrWhiteSpace(name)) return;
        var album = await CreateAlbumAsync(name.Trim());
        RefreshSide();
        SelectedFilter = AlbumFilters.First(f => f.Payload == album);
    }

    private async Task<Album> CreateAlbumAsync(string name)
    {
        var album = new Album { Name = name, SortOrder = Albums.Count, Color = Palettes.Tags[Albums.Count % 10].Key };
        await _store.AddAsync(album);
        Albums.Add(album);
        return album;
    }

    [RelayCommand]
    private async Task RenameAlbum(SideFilter? filter)
    {
        if (filter?.Payload is not Album album) return;
        var name = await _dialogs.PromptAsync("Rename album", null, album.Name, "Name", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        album.Name = name.Trim();
        await _store.UpdateAsync(album);
        RefreshSide();
    }

    [RelayCommand]
    private async Task DeleteAlbum(SideFilter? filter)
    {
        if (filter?.Payload is not Album album) return;
        if (!await _dialogs.ConfirmAsync("Delete album?", $"“{album.Name}” goes away; its {album.PhotoCount} photo(s) stay in All photos.", "Delete album", danger: true)) return;
        await _store.DeleteAsync(album);
        foreach (var p in _all.Where(p => p.AlbumId == album.Id)) p.AlbumId = null;
        Albums.Remove(album);
        SelectedFilter = AllFilter;
        RefreshSide();
    }

    [RelayCommand]
    private async Task TogglePrivateAlbum(SideFilter? filter)
    {
        if (filter?.Payload is Album album) await PrivacyCommands.Toggle.ExecuteAsync(album);
    }

    // ----- photo actions (viewer and selection) -----

    /// <summary>Updates only the given columns (the full image isn't loaded in the grid).</summary>
    private Task SaveAsync(IEnumerable<Photo> photos, params string[] properties)
    {
        var list = photos.Where(p => p.Id > 0).ToList();
        if (list.Count == 0) return Task.CompletedTask;
        return _store.RunAsync(db =>
        {
            foreach (var p in list)
            {
                db.Attach(p);
                foreach (var prop in properties) db.Entry(p).Property(prop).IsModified = true;
            }
            db.SaveChanges();
        });
    }

    [RelayCommand]
    private async Task ToggleFavorite(Photo? photo)
    {
        photo ??= Viewing;
        if (photo == null) return;
        photo.IsFavorite = !photo.IsFavorite;
        await SaveAsync([photo], nameof(Photo.IsFavorite));
        RefreshSide();
        if (SelectedFilter == FavoritesFilter && !IsViewing) Apply();
    }

    public async Task SaveCaptionAsync()
    {
        if (Viewing is { } p) await SaveAsync([p], nameof(Photo.Caption));
    }

    [RelayCommand]
    private async Task MoveToAlbum(Photo? photo)
    {
        List<Photo> targets = photo != null ? [photo] : SelectMode ? Visible.Where(p => p.IsChecked).ToList() : Viewing != null ? [Viewing] : new List<Photo>();
        if (targets.Count == 0) return;
        var options = new List<PickOption> { new("No album", "Only in All photos", Glyphs.Photo, null) };
        options.AddRange(Albums.Select(a => new PickOption(a.Name, $"{a.PhotoCount} photos", Glyphs.Folder, a, a.Color)));
        options.Add(new PickOption("New album…", "", Glyphs.Add, "new"));
        if (await _dialogs.ShowAsync(new PickDialogViewModel(targets.Count == 1 ? "Move to album" : $"Move {targets.Count} photos", null, options)) is not PickOption picked) return;
        Album? album = picked.Value as Album;
        if (picked.Value is "new")
        {
            var name = await _dialogs.PromptAsync("New album", null, "", "Album name", "Create");
            if (string.IsNullOrWhiteSpace(name)) return;
            album = await CreateAlbumAsync(name.Trim());
        }
        foreach (var p in targets) p.AlbumId = album?.Id;
        await SaveAsync(targets, nameof(Photo.AlbumId));
        RefreshSide();
        Apply();
        OnPropertyChanged(nameof(ViewAlbum));
        _toasts.Success(album == null ? "Removed from the album" : $"Moved to {album.Name}");
    }

    [RelayCommand]
    private async Task SetAsCover()
    {
        if (Viewing is not { AlbumId: int id } p || Albums.FirstOrDefault(a => a.Id == id) is not { } album) { _toasts.Info("Put the photo in an album first"); return; }
        album.CoverPhotoId = p.Id;
        await _store.UpdateAsync(album);
        RefreshSide();
        _toasts.Success($"Cover of {album.Name}");
    }

    [RelayCommand]
    private async Task DeletePhotos(Photo? photo)
    {
        List<Photo> targets = photo != null ? [photo] : SelectMode ? Visible.Where(p => p.IsChecked).ToList() : Viewing != null ? [Viewing] : new List<Photo>();
        if (targets.Count == 0) return;
        if (!await _dialogs.ConfirmAsync(targets.Count == 1 ? "Delete photo?" : $"Delete {targets.Count} photos?", "They're removed from Satchel (the original files on your PC are not touched).", "Delete", danger: true)) return;
        if (Viewing != null && targets.Contains(Viewing))
        {
            var next = Visible.SkipWhile(p => !ReferenceEquals(p, Viewing)).Skip(1).FirstOrDefault(p => !targets.Contains(p));
            Viewing = next;
        }
        await _store.DeleteRangeAsync(targets);
        foreach (var p in targets) _all.Remove(p);
        SelectMode = false;
        RefreshSide();
        Apply();
    }

    [RelayCommand]
    private async Task CopyImage()
    {
        if (Viewing == null) return;
        var data = await LoadDataAsync(Viewing.Id);
        if (ImageService.ToBitmap(data) is { } bmp) { Clipboard.SetImage(bmp); _toasts.Success("Copied"); }
    }

    [RelayCommand]
    private async Task SaveAs()
    {
        if (Viewing is not { } p) return;
        var data = await LoadDataAsync(p.Id);
        if (data == null) return;
        bool png = data.Length > 8 && data[0] == 0x89;
        var dialog = new SaveFileDialog { Title = "Save a copy", FileName = (p.FileName.Length > 0 ? p.FileName : "photo") + (png ? ".png" : ".jpg"), Filter = png ? "PNG|*.png" : "JPEG|*.jpg" };
        if (dialog.ShowDialog() != true) return;
        await File.WriteAllBytesAsync(dialog.FileName, data);
        _toasts.Success("Saved (not encrypted)");
    }

    /// <summary>Pins the photo to a board as a polaroid.</summary>
    [RelayCommand]
    private async Task SendToBoard()
    {
        if (Viewing is not { } p) return;
        var boards = await _store.ListAsync<Board>(q => q.OrderBy(b => b.SortOrder));
        if (boards.Count == 0) { _toasts.Info("Create a board first"); return; }
        if (await _dialogs.ShowAsync(new PickDialogViewModel("Pin to a board", null, boards.Select(b => new PickOption(b.Name, b.IsCork ? "Corkboard" : "Board", Glyphs.Tiles, b)))) is not PickOption { Value: Board board }) return;
        var data = await LoadDataAsync(p.Id);
        if (data == null) return;
        var image = ImageService.Resize(data, ImageService.PhotoSize, photo: true);
        double w = p.Width >= p.Height ? 280 : 220, h = Math.Clamp(w * p.Height / Math.Max(1, p.Width), 110, 380);
        await _store.RunAsync(db =>
        {
            int z = db.Stickies.Where(s => s.BoardId == board.Id).Select(s => (int?)s.ZIndex).Max() ?? 0;
            db.Stickies.Add(new StickyNote { BoardId = board.Id, Kind = StickyKind.Image, Image = image, Caption = p.Caption, Color = "white", X = 120 + Random.Shared.Next(200), Y = 100 + Random.Shared.Next(120),
                                              Width = w + 20, Height = h + 50, Rotation = Math.Round(Random.Shared.NextDouble() * 4 - 2, 1), ZIndex = z + 1 });
            db.SaveChanges();
        });
        (_nav as ShellViewModel)?.GetPage<BoardViewModel>("Board")?.MarkChanged(board.Id);
        _toasts.Show($"Pinned to {board.Name}", ToastKind.Success, "Open", () => _nav.NavigateTo("Board", board.Id));
    }

    // ----- viewer -----

    private Task<byte[]?> LoadDataAsync(int id) => _store.RunAsync(db => db.Photos.Where(x => x.Id == id).Select(x => x.Data).FirstOrDefault());

    private async Task LoadFullAsync(Photo? photo)
    {
        ViewImage = photo?.Thumb is { } thumb ? ImageService.ToBitmap(thumb) : null;
        if (photo == null) return;
        var data = await LoadDataAsync(photo.Id);
        if (!ReferenceEquals(Viewing, photo)) return;
        if (data != null) ViewImage = await Task.Run(() => ImageService.ToBitmap(data));
    }

    [RelayCommand] private void CloseViewer() => Viewing = null;
    [RelayCommand] private void Previous() => Step(-1, wrap: false);
    [RelayCommand] private void Next() => Step(+1, wrap: false);
    [RelayCommand] private void ToggleInfo() => ShowInfo = !ShowInfo;

    private void Step(int delta, bool wrap)
    {
        if (Viewing == null || Visible.Count == 0) return;
        int i = Visible.IndexOf(Viewing) + delta;
        if (i >= Visible.Count) { if (!wrap) return; i = 0; }
        if (i < 0) { if (!wrap) return; i = Visible.Count - 1; }
        Viewing = Visible[i];
    }

    [RelayCommand]
    private void ToggleSlideshow()
    {
        if (IsSlideshow) StopSlideshow();
        else
        {
            IsSlideshow = true;
            ShowInfo = false;
            _slideshow.Start();
        }
    }

    private void StopSlideshow()
    {
        IsSlideshow = false;
        _slideshow.Stop();
    }

    public void Dispose()
    {
        _slideshow.Stop();
        foreach (var p in _all) p.PropertyChanged -= OnPhotoChecked;
    }
}
