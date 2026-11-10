using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Satchel.Behaviors;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class MusicView : UserControl
{
    private MusicViewModel? _vm;
    private Rect _lastSlot = Rect.Empty;

    public MusicView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.VideoModeChanged -= UpdateVideo;
                _vm.Player.PropertyChanged -= OnPlayerChanged;
            }
            _vm = e.NewValue as MusicViewModel;
            if (_vm == null) return;
            _vm.VideoModeChanged += UpdateVideo;
            _vm.Player.PropertyChanged += OnPlayerChanged;
            UpdateVideo();
        };
        Loaded += (_, _) => UpdateVideo();
        Unloaded += (_, _) =>
        {
            // Leaving the page: the player goes back to being invisible (music keeps playing).
            if (MainWindow.MusicHost is { } host) host.IsShown = false;
            _lastSlot = Rect.Empty;
        };
        VideoSlot.LayoutUpdated += (_, _) => PlaceVideo();
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Services.MusicService.HasTrack)) Dispatcher.BeginInvoke(UpdateVideo);
    }

    private void UpdateVideo()
    {
        _lastSlot = Rect.Empty;
        PlaceVideo();
    }

    /// <summary>Moves the window-level music player over the video slot (it can't live inside the page: it must survive page changes).</summary>
    private void PlaceVideo()
    {
        if (MainWindow.MusicHost is not { } host || DnD.Root is not { } root) return;
        bool show = _vm is { ShowVideo: true } && _vm.Player.HasTrack && IsLoaded && VideoSlot.IsVisible && VideoSlot.ActualWidth > 0;
        if (!show)
        {
            if (host.IsShown) host.IsShown = false;
            return;
        }
        Point origin;
        try { origin = VideoSlot.TranslatePoint(new Point(0, 0), root); }
        catch (InvalidOperationException) { return; }
        var rect = new Rect(origin, new Size(VideoSlot.ActualWidth, VideoSlot.ActualHeight));
        if (rect == _lastSlot && host.IsShown) return;
        _lastSlot = rect;
        host.Margin = new Thickness(rect.X, rect.Y, 0, 0);
        host.Width = rect.Width;
        host.Height = rect.Height;
        host.IsShown = true;
    }

    private void OnSeek(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null || SeekTrack.ActualWidth <= 0) return;
        _vm.SeekToCommand.Execute(e.GetPosition(SeekTrack).X / SeekTrack.ActualWidth);
    }
}
