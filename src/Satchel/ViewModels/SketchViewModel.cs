using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Satchel.Data;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>Sketchbook: freehand drawings (pen/tablet pressure), saved as ink strokes with a thumbnail.</summary>
public sealed partial class SketchViewModel : PageViewModel
{
    private readonly INavigator _nav;
    private readonly DataStore _store;
    private readonly DialogService _dialogs;
    private readonly ToastService _toasts;
    private readonly SettingsService _settings;
    private bool _loaded;

    public SketchViewModel(INavigator nav, DataStore store, DialogService dialogs, ToastService toasts, SettingsService settings)
    {
        _nav = nav;
        _store = store;
        _dialogs = dialogs;
        _toasts = toasts;
        _settings = settings;
        _color = settings.Current.SketchColor;
        _size = settings.Current.SketchSize;
    }

    public override string Title => "Sketch";
    public ObservableCollection<Sketch> Sketches { get; } = [];

    [ObservableProperty] private Sketch? _selected;
    [ObservableProperty] private string _tool = "pen";
    [ObservableProperty] private string _color;
    [ObservableProperty] private double _size;
    [ObservableProperty] private double _zoom = 1;

    public bool IsEditing => Selected != null;
    public bool IsEmpty => Sketches.Count == 0;

    public static IReadOnlyList<string> Palette { get; } =
    [
        "#1F2328", "#6B7280", "#FFFFFF", "#DC2626", "#EA580C", "#F59E0B", "#16A34A", "#0D9488", "#0EA5E9", "#2563EB", "#DB2777", "#92400E"
    ];

    public static IReadOnlyList<Choice<string>> Papers { get; } =
        [new("plain", "Plain"), new("grid", "Grid"), new("dots", "Dots"), new("lines", "Lined"), new("dark", "Dark")];

    /// <summary>Raised by the view model when the view must persist the current drawing now.</summary>
    public event Func<Task>? SaveRequested;

    partial void OnSelectedChanged(Sketch? value)
    {
        OnPropertyChanged(nameof(IsEditing));
        Zoom = 1;
    }

    partial void OnColorChanged(string value) => _settings.Current.SketchColor = value;
    partial void OnSizeChanged(double value) => _settings.Current.SketchSize = value;

    public override async Task OnNavigatedToAsync(object? parameter)
    {
        if (!_loaded) await LoadAsync();
        if (parameter is int id && Sketches.FirstOrDefault(s => s.Id == id) is { } sketch) Selected = sketch;
        else if (parameter is "new") await NewSketch();
    }

    public override async Task OnNavigatedFromAsync() => await FlushAsync();

    public override async Task FlushAsync()
    {
        if (SaveRequested != null && Selected != null) await SaveRequested.Invoke();
    }

    private async Task LoadAsync()
    {
        var sketches = await _store.ListAsync<Sketch>(q => q.OrderByDescending(s => s.UpdatedAt));
        Sketches.Clear();
        foreach (var s in sketches) Sketches.Add(s);
        _loaded = true;
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Stores strokes and thumbnail produced by the view.</summary>
    public async Task SaveAsync(Sketch sketch, byte[] strokes, byte[]? thumbnail)
    {
        sketch.Strokes = strokes;
        if (thumbnail != null) sketch.Thumbnail = thumbnail;
        sketch.UpdatedAt = DateTime.Now;
        if (sketch.Id == 0) await _store.AddAsync(sketch);
        else await _store.UpdateAsync(sketch);
    }

    public override void OnNewShortcut()
    {
        if (Selected == null) _ = NewSketch();
    }

    [RelayCommand]
    private async Task NewSketch()
    {
        await FlushAsync();
        var sketch = new Sketch { Title = $"Sketch {DateTime.Now:d MMM, HH:mm}", Paper = "dots" };
        await _store.AddAsync(sketch);
        Sketches.Insert(0, sketch);
        OnPropertyChanged(nameof(IsEmpty));
        Tool = "pen";
        Selected = sketch;
    }

    [RelayCommand]
    private async Task Open(Sketch? sketch)
    {
        if (sketch == null) return;
        await FlushAsync();
        Selected = sketch;
    }

    [RelayCommand]
    private async Task Back()
    {
        await FlushAsync();
        if (Selected is { } s && s.Id != 0)
        {
            // move to the front of the gallery
            Sketches.Remove(s);
            Sketches.Insert(0, s);
        }
        Selected = null;
    }

    [RelayCommand]
    private void SetTool(string? tool) => Tool = tool ?? "pen";

    [RelayCommand]
    private void SetColor(string? color)
    {
        if (color == null) return;
        Color = color;
        if (Tool is "eraser" or "select") Tool = "pen";
    }

    [RelayCommand]
    private void SetPaper(string? paper)
    {
        if (Selected == null || paper == null) return;
        Selected.Paper = paper;
        if (paper == "dark" && Color == "#1F2328") Color = "#FFFFFF";
        if (paper != "dark" && Color == "#FFFFFF") Color = "#1F2328";
        _ = _store.UpdateAsync(Selected);
    }

    [RelayCommand]
    private async Task PickReference()
    {
        if (Selected == null) return;
        var img = ImageService.PickImage(1800, photo: true);
        if (img == null) return;
        Selected.Reference = img;
        await _store.UpdateAsync(Selected);
    }

    [RelayCommand]
    private async Task RemoveReference()
    {
        if (Selected?.Reference == null) return;
        Selected.Reference = null;
        await _store.UpdateAsync(Selected);
    }

    public async Task SaveReferenceOpacityAsync()
    {
        if (Selected != null) await _store.UpdateAsync(Selected);
    }

    [RelayCommand]
    private async Task Rename()
    {
        if (Selected == null) return;
        var name = await _dialogs.PromptAsync("Rename sketch", null, Selected.Title, "Title", "Rename");
        if (string.IsNullOrWhiteSpace(name)) return;
        Selected.Title = name.Trim();
        await _store.UpdateAsync(Selected);
    }

    [RelayCommand]
    private async Task Delete(Sketch? sketch)
    {
        sketch ??= Selected;
        if (sketch == null) return;
        if (_settings.Current.ConfirmDeletes && !await _dialogs.ConfirmAsync("Delete sketch?", $"“{sketch.Title}” will be deleted.", "Delete", danger: true)) return;
        if (ReferenceEquals(Selected, sketch)) Selected = null;
        Sketches.Remove(sketch);
        await _store.DeleteAsync(sketch);
        OnPropertyChanged(nameof(IsEmpty));
        _toasts.Show("Sketch deleted", ToastKind.Info, "Undo", async () =>
        {
            sketch.Id = 0;
            await _store.AddAsync(sketch);
            Sketches.Insert(0, sketch);
            OnPropertyChanged(nameof(IsEmpty));
        });
    }

    /// <summary>Puts a rendered sketch on a board as a photo note.</summary>
    public async Task SendToBoardAsync(byte[] png, string title)
    {
        var board = (await _store.ListAsync<Board>(q => q.OrderBy(b => b.SortOrder))).FirstOrDefault();
        if (board == null)
        {
            board = new Board { Name = "My board" };
            await _store.AddAsync(board);
        }
        var size = ImageService.PixelSize(png) ?? new System.Windows.Size(4, 3);
        double w = 280, h = Math.Clamp(w * size.Height / Math.Max(1, size.Width), 110, 380);
        var maxZ = await _store.RunAsync(db => db.Stickies.Where(s => s.BoardId == board.Id).Select(s => (int?)s.ZIndex).Max() ?? 0);
        var sticky = new StickyNote
        {
            BoardId = board.Id, Kind = StickyKind.Image, Image = png, Caption = title, Color = "white",
            X = 80 + Random.Shared.Next(0, 240), Y = 80 + Random.Shared.Next(0, 160), Width = w + 20, Height = h + 50,
            Rotation = Math.Round(Random.Shared.NextDouble() * 4 - 2, 1), ZIndex = maxZ + 1
        };
        await _store.AddAsync(sticky);
        if ((_nav as ShellViewModel)?.GetPage<BoardViewModel>("Board") is { } boards) await boards.ReloadBoardsAsync(board.Id);
        var id = board.Id;
        _toasts.Show($"Added to board “{board.Name}”", ToastKind.Success, "Open", () => _nav.NavigateTo("Board", id));
    }
}
