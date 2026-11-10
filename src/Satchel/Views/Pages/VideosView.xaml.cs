using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Satchel.Controls;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

/// <summary>Hosts the shared in-app YouTube player (see <see cref="YouTubePlayer"/>) for the Videos page.</summary>
public partial class VideosView : UserControl
{
    private VideosViewModel? _vm;

    public VideosView()
    {
        InitializeComponent();
        Player.TimeChanged += (time, duration) => _vm?.ReportPlayback(time, duration);
        Player.StateChanged += (_, time, duration) => _vm?.ReportPlayback(time, duration);
        Player.ErrorOccurred += code => ShowFallback(YouTubePlayer.ErrorText(code));
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.PlayRequested -= OnPlayRequested;
                _vm.SeekRequested -= OnSeekRequested;
                _vm.PropertyChanged -= OnVmChanged;
            }
            _vm = e.NewValue as VideosViewModel;
            if (_vm == null) return;
            _vm.PlayRequested += OnPlayRequested;
            _vm.SeekRequested += OnSeekRequested;
            _vm.PropertyChanged += OnVmChanged;
            if (_vm.Playing is { } v) OnPlayRequested(v.YoutubeId, _vm.CurrentTime);
        };
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VideosViewModel.Playing) && _vm?.Playing == null) Player.Stop();
    }

    private async void OnPlayRequested(string id, double start)
    {
        Fallback.Visibility = Visibility.Collapsed;
        Player.IsShown = true;
        Player.Load(id, start);
        if (!await Player.EnsureAsync()) ShowFallback("The in-app player isn't available on this PC.");
    }

    private void OnSeekRequested(double seconds) => Player.Seek(seconds);

    private void ShowFallback(string message)
    {
        FallbackText.Text = message;
        Fallback.Visibility = Visibility.Visible;
        Player.IsShown = false;
    }
}
