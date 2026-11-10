using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Satchel.Core;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class SketchView : UserControl
{
    private const int UndoLimit = 60;
    private SketchViewModel? _vm;
    private Sketch? _loadedSketch;
    private readonly Stack<StrokeCollection> _undo = new();
    private readonly Stack<StrokeCollection> _redo = new();
    private readonly DispatcherTimer _saveTimer;
    private bool _dirty;
    private bool _suspend;

    public SketchView()
    {
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _saveTimer.Tick += async (_, _) => await SaveAsync();
        Ink.StrokeCollected += (_, _) => Changed();
        Ink.StrokeErased += (_, _) => Changed();
        Ink.SelectionMoved += (_, _) => Changed();
        Ink.SelectionResized += (_, _) => Changed();
        // One undo step per gesture: remember the strokes when a gesture starts, push them on its first change.
        Ink.PreviewMouseDown += (_, _) => BeginGesture();
        Ink.PreviewStylusDown += (_, _) => BeginGesture();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.PropertyChanged -= OnVmChanged;
                _vm.SaveRequested -= SaveAsync;
            }
            _vm = e.NewValue as SketchViewModel;
            if (_vm == null) return;
            _vm.PropertyChanged += OnVmChanged;
            _vm.SaveRequested += SaveAsync;
            LoadSketch();
            ApplyTool();
        };
        Unloaded += async (_, _) => await SaveAsync();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(SketchViewModel.Selected):
                LoadSketch();
                break;
            case nameof(SketchViewModel.Tool):
            case nameof(SketchViewModel.Color):
            case nameof(SketchViewModel.Size):
                ApplyTool();
                break;
        }
    }

    // ----- loading & saving -----

    private void LoadSketch()
    {
        var sketch = _vm?.Selected;
        if (ReferenceEquals(sketch, _loadedSketch)) return;
        _loadedSketch = sketch;
        _undo.Clear();
        _redo.Clear();
        _dirty = false;
        _suspend = true;
        Ink.Strokes = new StrokeCollection();
        if (sketch == null) { _suspend = false; return; }
        Page.Width = sketch.CanvasWidth;
        Page.Height = sketch.CanvasHeight;
        if (sketch.Strokes is { Length: > 0 } data)
        {
            try
            {
                using var ms = new MemoryStream(data);
                Ink.Strokes = new StrokeCollection(ms);
            }
            catch (Exception ex) { App.Log("Sketch load", ex); }
        }
        ApplyPaper(sketch.Paper);
        _suspend = false;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, ZoomToFit);
    }

    private StrokeCollection? _gestureStart;
    private bool _gesturePushed;

    private void BeginGesture()
    {
        _gestureStart = Ink.Strokes.Clone();
        _gesturePushed = false;
    }

    private void Changed()
    {
        if (_suspend) return;
        if (_gestureStart != null && !_gesturePushed)
        {
            _undo.Push(_gestureStart);
            _redo.Clear();
            _gesturePushed = true;
        }
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private async Task SaveAsync()
    {
        _saveTimer.Stop();
        if (!_dirty || _loadedSketch is not { } sketch || _vm == null) return;
        _dirty = false;
        byte[] isf;
        using (var ms = new MemoryStream())
        {
            Ink.Strokes.Save(ms, compress: true);
            isf = ms.ToArray();
        }
        var thumb = RenderPng(360, includeReference: false);
        await _vm.SaveAsync(sketch, isf, thumb);
    }

    // ----- tools -----

    private void ApplyTool()
    {
        if (_vm == null) return;
        var color = ColorUtil.TryParse(_vm.Color, out var c) ? c : Colors.Black;
        double size = Math.Max(1, _vm.Size);
        switch (_vm.Tool)
        {
            case "marker":
                Ink.EditingMode = InkCanvasEditingMode.Ink;
                Ink.DefaultDrawingAttributes = new DrawingAttributes
                {
                    Color = Color.FromArgb(150, color.R, color.G, color.B), Width = size * 3.2, Height = size * 3.2,
                    IsHighlighter = true, StylusTip = StylusTip.Rectangle, FitToCurve = true, IgnorePressure = true
                };
                break;
            case "eraser":
                Ink.EditingMode = InkCanvasEditingMode.EraseByPoint;
                Ink.EraserShape = new EllipseStylusShape(size * 3 + 6, size * 3 + 6);
                break;
            case "strokes":
                Ink.EditingMode = InkCanvasEditingMode.EraseByStroke;
                break;
            case "select":
                Ink.EditingMode = InkCanvasEditingMode.Select;
                break;
            default:
                Ink.EditingMode = InkCanvasEditingMode.Ink;
                Ink.DefaultDrawingAttributes = new DrawingAttributes
                {
                    Color = color, Width = size, Height = size, FitToCurve = true, IgnorePressure = false, StylusTip = StylusTip.Ellipse
                };
                break;
        }
    }

    private void OnColorPicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is string hex) _vm?.SetColorCommand.Execute(hex);
    }

    private void OnPaperChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_vm?.Selected == null || (sender as ComboBox)?.SelectedValue is not string paper || paper == _vm.Selected.Paper) return;
        _vm.SetPaperCommand.Execute(paper);
        ApplyPaper(paper);
        _dirty = true;
        Changed();
    }

    private void ApplyPaper(string paper)
    {
        bool dark = paper == "dark";
        var bg = dark ? Color.FromRgb(0x1E, 0x22, 0x28) : Color.FromRgb(0xFF, 0xFF, 0xFF);
        var line = dark ? Color.FromArgb(40, 255, 255, 255) : Color.FromArgb(34, 40, 60, 90);
        Brush pattern = paper switch
        {
            "grid" => Pattern(24, (dc, s) =>
            {
                var pen = new Pen(new SolidColorBrush(line), 1);
                dc.DrawLine(pen, new Point(0, 0), new Point(s, 0));
                dc.DrawLine(pen, new Point(0, 0), new Point(0, s));
            }, bg),
            "dots" => Pattern(24, (dc, s) => dc.DrawEllipse(new SolidColorBrush(Color.FromArgb((byte)(line.A * 2.2), line.R, line.G, line.B)), null, new Point(s / 2, s / 2), 1.2, 1.2), bg),
            "lines" => Pattern(30, (dc, s) => dc.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(60, 70, 130, 200)), 1), new Point(0, s - 0.5), new Point(s, s - 0.5)), bg),
            _ => new SolidColorBrush(bg)
        };
        Paper.Background = pattern;
    }

    private static Brush Pattern(double size, Action<DrawingContext, double> draw, Color background)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, size, size));
            draw(dc, size);
        }
        var brush = new DrawingBrush(dv.Drawing) { TileMode = TileMode.Tile, Viewport = new Rect(0, 0, size, size), ViewportUnits = BrushMappingMode.Absolute, Stretch = Stretch.None };
        brush.Freeze();
        return brush;
    }

    // ----- undo / redo -----

    private void PushUndo()
    {
        if (_suspend) return;
        _undo.Push(Ink.Strokes.Clone());
        while (_undo.Count > UndoLimit)
        {
            var keep = _undo.Take(UndoLimit).Reverse().ToList();
            _undo.Clear();
            foreach (var k in keep) _undo.Push(k);
        }
        _redo.Clear();
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();
    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Push(Ink.Strokes.Clone());
        Restore(_undo.Pop());
    }

    private void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Push(Ink.Strokes.Clone());
        Restore(_redo.Pop());
    }

    private void Restore(StrokeCollection strokes)
    {
        _suspend = true;
        Ink.Strokes = strokes;
        _suspend = false;
        _gestureStart = null;
        _dirty = true;
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void OnClear(object sender, RoutedEventArgs e)
    {
        if (Ink.Strokes.Count == 0) return;
        PushUndo();
        Restore(new StrokeCollection());
    }

    // ----- keyboard & zoom -----

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm?.Selected == null || Keyboard.FocusedElement is TextBoxBase) return;
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y) { Redo(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.P: _vm.Tool = "pen"; break;
                case Key.M: _vm.Tool = "marker"; break;
                case Key.E: _vm.Tool = "eraser"; break;
                case Key.S: _vm.Tool = "select"; break;
                case Key.Delete when Ink.GetSelectedStrokes().Count > 0:
                    PushUndo();
                    Ink.Strokes.Remove(Ink.GetSelectedStrokes());
                    Changed();
                    break;
                default: return;
            }
            e.Handled = true;
        }
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm == null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        _vm.Zoom = Math.Clamp(Math.Round(_vm.Zoom + (e.Delta > 0 ? 0.1 : -0.1), 2), 0.2, 3);
        e.Handled = true;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) { if (_vm != null) _vm.Zoom = Math.Min(3, Math.Round(_vm.Zoom + 0.1, 2)); }
    private void OnZoomOut(object sender, RoutedEventArgs e) { if (_vm != null) _vm.Zoom = Math.Max(0.2, Math.Round(_vm.Zoom - 0.1, 2)); }
    private void OnZoomFit(object sender, RoutedEventArgs e) => ZoomToFit();

    private void ZoomToFit()
    {
        if (_vm?.Selected is not { } s || Scroll.ActualWidth <= 0) return;
        double fit = Math.Min((Scroll.ActualWidth - 60) / s.CanvasWidth, (Scroll.ActualHeight - 60) / s.CanvasHeight);
        _vm.Zoom = Math.Round(Math.Clamp(fit, 0.2, 1.5), 2);
    }

    private async void OnOpacityDone(object sender, DragCompletedEventArgs e)
    {
        if (_vm != null) await _vm.SaveReferenceOpacityAsync();
    }

    // ----- export -----

    /// <summary>Renders paper + (optionally) reference + ink to PNG, scaled to <paramref name="width"/> px.</summary>
    private byte[] RenderPng(int width, bool includeReference = true)
    {
        var sketch = _loadedSketch!;
        double w = sketch.CanvasWidth, h = sketch.CanvasHeight;
        double scale = width / w;
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.DrawRectangle(Paper.Background, null, new Rect(0, 0, w, h));
            if (includeReference && Reference.Source is ImageSource img)
            {
                dc.PushOpacity(sketch.ReferenceOpacity);
                double r = Math.Min(w / img.Width, h / img.Height);
                double iw = img.Width * r, ih = img.Height * r;
                dc.DrawImage(img, new Rect((w - iw) / 2, (h - ih) / 2, iw, ih));
                dc.Pop();
            }
            Ink.Strokes.Draw(dc);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(Math.Max(1, (int)(w * scale)), Math.Max(1, (int)(h * scale)), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(rtb));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        return ms.ToArray();
    }

    private void OnExport(object sender, RoutedEventArgs e)
    {
        if (_loadedSketch == null) return;
        var dialog = new SaveFileDialog { Title = "Export sketch", Filter = "PNG image|*.png", FileName = SafeName(_loadedSketch.Title) + ".png" };
        if (dialog.ShowDialog() != true) return;
        File.WriteAllBytes(dialog.FileName, RenderPng((int)_loadedSketch.CanvasWidth));
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if (_loadedSketch == null) return;
        var img = ImageService.ToBitmap(RenderPng((int)_loadedSketch.CanvasWidth));
        if (img != null) Clipboard.SetImage(img);
    }

    private async void OnSendToBoard(object sender, RoutedEventArgs e)
    {
        if (_loadedSketch == null || _vm == null) return;
        await SaveAsync();
        await _vm.SendToBoardAsync(RenderPng(1200), _loadedSketch.Title);
    }

    private static string SafeName(string name) => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
