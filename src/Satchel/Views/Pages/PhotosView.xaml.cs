using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Satchel.Services;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class PhotosView : UserControl
{
    private PhotosViewModel? _vm;
    private Point? _panStart;
    private Point _panOrigin;

    public PhotosView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null) _vm.PropertyChanged -= OnVmChanged;
            _vm = e.NewValue as PhotosViewModel;
            if (_vm != null) _vm.PropertyChanged += OnVmChanged;
        };
        Loaded += (_, _) => Focus();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PhotosViewModel.Viewing))
        {
            ResetZoom();
            if (_vm?.IsViewing == true) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Focus());
        }
    }

    private void ResetZoom()
    {
        ZoomScale.ScaleX = ZoomScale.ScaleY = 1;
        ZoomPan.X = ZoomPan.Y = 0;
    }

    // ----- keyboard: viewer arrows, favorite, slideshow; paste in the grid -----

    private async void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null || Keyboard.FocusedElement is TextBoxBase) return;
        if (_vm.IsViewing)
        {
            switch (e.Key)
            {
                case Key.Left: _vm.PreviousCommand.Execute(null); e.Handled = true; break;
                case Key.Right: _vm.NextCommand.Execute(null); e.Handled = true; break;
                case Key.Escape: _vm.CloseViewerCommand.Execute(null); e.Handled = true; break;
                case Key.F: _vm.ToggleFavoriteCommand.Execute(null); e.Handled = true; break;
                case Key.Space: _vm.ToggleSlideshowCommand.Execute(null); e.Handled = true; break;
                case Key.I: _vm.ToggleInfoCommand.Execute(null); e.Handled = true; break;
                case Key.D0 or Key.NumPad0: ResetZoom(); e.Handled = true; break;
            }
            return;
        }
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            var images = ImageService.FromDataObject(Clipboard.GetDataObject(), int.MaxValue);
            if (images.Count == 0 && Clipboard.ContainsFileDropList())
            {
                e.Handled = true;
                await _vm.ImportPathsAsync(Clipboard.GetFileDropList().Cast<string>().Where(ImageService.IsImageFile).ToList());
                return;
            }
            if (images.Count == 0) return;
            e.Handled = true;
            await _vm.ImportImagesAsync(images);
        }
        else if (e.Key == Key.Escape && _vm.SelectMode)
        {
            _vm.SelectMode = false;
            e.Handled = true;
        }
    }

    // ----- drop files / folders -----

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || ImageService.HasImage(e.Data) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_vm == null) return;
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            var files = new List<string>();
            foreach (var p in paths)
            {
                if (Directory.Exists(p)) files.AddRange(Directory.EnumerateFiles(p, "*.*", SearchOption.AllDirectories).Where(ImageService.IsImageFile));
                else if (ImageService.IsImageFile(p)) files.Add(p);
            }
            await _vm.ImportPathsAsync(files);
            return;
        }
        var images = ImageService.FromDataObject(e.Data, int.MaxValue);
        if (images.Count > 0) await _vm.ImportImagesAsync(images);
    }

    // ----- zoom (wheel) and pan (drag) in the viewer -----

    private void OnStageWheel(object sender, MouseWheelEventArgs e)
    {
        double factor = e.Delta > 0 ? 1.15 : 1 / 1.15;
        double scale = Math.Clamp(ZoomScale.ScaleX * factor, 1, 8);
        ZoomScale.ScaleX = ZoomScale.ScaleY = scale;
        if (scale <= 1.001) ZoomPan.X = ZoomPan.Y = 0;
        e.Handled = true;
    }

    private void OnStageDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            if (ZoomScale.ScaleX > 1) ResetZoom();
            else ZoomScale.ScaleX = ZoomScale.ScaleY = 2.5;
            return;
        }
        if (ZoomScale.ScaleX <= 1) return;
        _panStart = e.GetPosition(Stage);
        _panOrigin = new Point(ZoomPan.X, ZoomPan.Y);
        Stage.CaptureMouse();
    }

    private void OnStageMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } start) return;
        var p = e.GetPosition(Stage);
        ZoomPan.X = _panOrigin.X + (p.X - start.X);
        ZoomPan.Y = _panOrigin.Y + (p.Y - start.Y);
    }

    private void OnStageUp(object sender, MouseButtonEventArgs e)
    {
        _panStart = null;
        Stage.ReleaseMouseCapture();
    }

    private async void OnCaptionBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_vm != null) await _vm.SaveCaptionAsync();
    }
}
