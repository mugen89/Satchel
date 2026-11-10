using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Satchel.Controls;
using Satchel.Core;
using Satchel.Models;
using Satchel.Services;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

/// <summary>Picks the note template by kind (text, image, link).</summary>
public sealed class StickyTemplateSelector : DataTemplateSelector
{
    public DataTemplate? TextTemplate { get; set; }
    public DataTemplate? ImageTemplate { get; set; }
    public DataTemplate? LinkTemplate { get; set; }
    public DataTemplate? LiveTemplate { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container) => (item as StickyNote)?.Kind switch
    {
        StickyKind.Image => ImageTemplate,
        StickyKind.Link => LinkTemplate,
        StickyKind.Live => LiveTemplate,
        _ => TextTemplate
    };
}

public partial class BoardView : UserControl
{
    private static readonly (string Name, string? Hex)[] TextColors =
    [
        ("Default", null), ("Red", "#D32F2F"), ("Orange", "#E65100"), ("Green", "#2E7D32"), ("Teal", "#00796B"), ("Blue", "#1565C0")
    ];

    private static readonly double[] FontSteps = [12, 13, 15, 18, 22, 28, 36];

    private BoardViewModel? _vm;
    private StickyNote? _menuTarget;
    private RichTextEditor? _activeEditor;
    private StickyNote? _activeSticky;
    private StickyNote? _connectFrom;
    private Point _connectStart;

    public BoardView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.StickyAdded -= OnStickyAdded;
                _vm.PropertyChanged -= OnVmChanged;
                _vm.StudyLoadRequested -= OnStudyLoad;
                _vm.StudySeekRequested -= OnStudySeek;
                _vm.RevealRequested -= OnReveal;
            }
            _vm = e.NewValue as BoardViewModel;
            if (_vm == null) return;
            _vm.StickyAdded += OnStickyAdded;
            _vm.PropertyChanged += OnVmChanged;
            _vm.StudyLoadRequested += OnStudyLoad;
            _vm.StudySeekRequested += OnStudySeek;
            _vm.RevealRequested += OnReveal;
            ApplyBoardStyle();
        };
        BuildFormatColors();
        BuildPinList();
        StudyPlayer.TimeChanged += (time, _) => _vm?.ReportStudyTime(time);
        StudyPlayer.StateChanged += (_, time, _) => _vm?.ReportStudyTime(time);
        StudyPlayer.ErrorOccurred += code => StudyPlayer.ShowMessage(YouTubePlayer.ErrorText(code));
        Loaded += (_, _) => ApplyBoardStyle();
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(BoardViewModel.IsCork):
                ApplyBoardStyle();
                HideFormatBar();
                break;
            case nameof(BoardViewModel.SelectedLink):
                UpdateLinkBar();
                break;
            case nameof(BoardViewModel.Zoom):
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => { PositionFormatBar(); UpdateLinkBar(); });
                break;
            case nameof(BoardViewModel.SelectedBoard):
                HideFormatBar();
                break;
            case nameof(BoardViewModel.IsStudyOpen) when _vm?.IsStudyOpen == false:
                StudyPlayer.Stop();
                break;
        }
    }

    private void ApplyBoardStyle()
    {
        if (_vm == null) return;
        Surface.Background = _vm.IsCork ? CorkTexture.Cork : (Brush)FindResource("DotsBrush");
        BuildLinkColors();
    }

    // ----- tree helpers -----

    private static Grid? StickyRoot(DependencyObject? d)
    {
        while (d != null && d is not Grid { Name: "sticky" }) d = VisualTreeHelper.GetParent(d);
        return d as Grid;
    }

    private static FrameworkElement? FindByName(DependencyObject parent, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindByName(child, name) is { } found) return found;
        }
        return null;
    }

    private static ScaleTransform? ScaleOf(Grid root) =>
        (root.RenderTransform as TransformGroup)?.Children.OfType<ScaleTransform>().FirstOrDefault();

    private Grid? RootOf(StickyNote sticky)
    {
        if (StickyHost.ItemContainerGenerator.ContainerFromItem(sticky) is not FrameworkElement container) return null;
        return VisualTreeHelper.GetChildrenCount(container) > 0 ? VisualTreeHelper.GetChild(container, 0) as Grid : null;
    }

    // ----- moving & resizing -----

    private void OnStickyMouseDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StickyNote s) _vm?.BringToFront(s);
        if (_vm?.SelectedLink != null) _vm.SelectedLink = null;
    }

    private void Lift(Grid root, bool up)
    {
        if (ScaleOf(root) is { } scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim.To(up ? 1.04 : 1, 180, Anim.EaseOut));
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim.To(up ? 1.04 : 1, 180, Anim.EaseOut));
        }
        if (FindByName(root, "shadow") is UIElement shadow)
            shadow.BeginAnimation(OpacityProperty, Anim.To(up ? 1 : 0.55, 180));
    }

    private void OnDragStarted(object sender, DragStartedEventArgs e)
    {
        if (StickyRoot((DependencyObject)sender) is { } root) Lift(root, true);
        HideFormatBar();
    }

    private void OnDragDelta(object sender, DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StickyNote s) return;
        s.X = Math.Clamp(s.X + e.HorizontalChange, 0, BoardViewModel.CanvasWidth - s.Width);
        s.Y = Math.Clamp(s.Y + e.VerticalChange, 0, BoardViewModel.CanvasHeight - s.Height);
    }

    private void OnDragCompleted(object sender, DragCompletedEventArgs e)
    {
        if (StickyRoot((DependencyObject)sender) is { } root) Lift(root, false);
    }

    private void OnResizeDelta(object sender, DragDeltaEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StickyNote s) return;
        double minW = s.IsText ? 150 : 180, minH = s.IsText ? 110 : 100;
        s.Width = Math.Clamp(s.Width + e.HorizontalChange, minW, 900);
        s.Height = Math.Clamp(s.Height + e.VerticalChange, minH, 900);
        PositionFormatBar();
    }

    // ----- note menu -----

    private void OnStickyMenu(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: StickyNote s } button || _vm == null) return;
        _menuTarget = s;
        MenuColorSection.Visibility = s.IsText ? Visibility.Visible : Visibility.Collapsed;
        MenuSizeSection.Visibility = s.IsText ? Visibility.Visible : Visibility.Collapsed;
        MenuPinSection.Visibility = _vm.IsCork ? Visibility.Visible : Visibility.Collapsed;
        MenuRefresh.Visibility = s.IsLink ? Visibility.Visible : Visibility.Collapsed;
        MenuPrivateText.Text = s.IsPrivate ? "Remove private flag" : "Mark private";
        StickyMenu.PlacementTarget = button;
        StickyMenu.IsOpen = true;
    }

    /// <summary>A private note disappears (with its connections) while private items are hidden.</summary>
    private async void OnTogglePrivate(object sender, RoutedEventArgs e)
    {
        StickyMenu.IsOpen = false;
        if (_menuTarget is not { } s) return;
        if (RootOf(s) is { } root && FindByName(root, "editor") is RichTextEditor editor) editor.Flush();
        if (_vm != null) await _vm.FlushAsync();
        await PrivacyCommands.Toggle.ExecuteAsync(s);
    }

    private void OnColorPicked(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ColorOption option && _menuTarget != null)
            _vm?.SetColor(_menuTarget, option.Key);
        StickyMenu.IsOpen = false;
    }

    private void BuildPinList()
    {
        foreach (var (key, name, color) in BoardConnectors.PinColors)
        {
            var dot = new Ellipse
            {
                Width = 22, Height = 22, Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, ToolTip = name,
                Fill = new RadialGradientBrush(ColorUtil.Lighten(color, 0.35), color) { GradientOrigin = new Point(0.35, 0.3) },
                Stroke = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), StrokeThickness = 1
            };
            dot.MouseLeftButtonUp += (_, _) =>
            {
                if (_menuTarget != null) _vm?.SetPin(_menuTarget, key);
                StickyMenu.IsOpen = false;
            };
            PinList.Children.Add(dot);
        }
    }

    private void OnSizePicked(object sender, RoutedEventArgs e)
    {
        if (_menuTarget != null && sender is FrameworkElement { Tag: string tag })
            _vm?.SetFontSize(_menuTarget, tag == "15" ? 0 : double.Parse(tag, CultureInfo.InvariantCulture));
        StickyMenu.IsOpen = false;
    }

    private void OnDuplicate(object sender, RoutedEventArgs e)
    {
        StickyMenu.IsOpen = false;
        _vm?.DuplicateStickyCommand.Execute(_menuTarget);
    }

    private void OnRefreshPreview(object sender, RoutedEventArgs e)
    {
        StickyMenu.IsOpen = false;
        _vm?.RefreshPreviewCommand.Execute(_menuTarget);
    }

    // ----- rich text editing & the floating format bar -----

    private void OnEditorFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (sender is not RichTextEditor { DataContext: StickyNote s } editor) return;
        if (!ReferenceEquals(_activeEditor, editor))
        {
            if (_activeEditor != null) _activeEditor.FormattingChanged -= RefreshFormatState;
            _activeEditor = editor;
            _activeEditor.FormattingChanged += RefreshFormatState;
        }
        _activeSticky = s;
        _vm?.BringToFront(s);
        FormatBar.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, PositionFormatBar);
        RefreshFormatState();
    }

    private void OnEditorBlur(object sender, KeyboardFocusChangedEventArgs e)
    {
        // Keep the bar while focus stays inside the same editor (e.g. context menu) — hide otherwise.
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (_activeEditor != null && _activeEditor.IsKeyboardFocusWithin) return;
            if (StickyMenu.IsOpen) return;
            HideFormatBar();
        });
    }

    private void HideFormatBar()
    {
        FormatBar.Visibility = Visibility.Collapsed;
        if (_activeEditor != null) _activeEditor.FormattingChanged -= RefreshFormatState;
        _activeEditor = null;
        _activeSticky = null;
    }

    private void PositionFormatBar()
    {
        if (_activeSticky is not { } s || FormatBar.Visibility != Visibility.Visible) return;
        FormatBar.UpdateLayout();
        var top = Surface.TranslatePoint(new Point(s.X, s.Y), Overlay);
        var bottom = Surface.TranslatePoint(new Point(s.X, s.Y + s.Height), Overlay);
        double y = top.Y - FormatBar.ActualHeight - 8;
        if (y < 6) y = bottom.Y + 8;
        double x = Math.Clamp(top.X, 6, Math.Max(6, Overlay.ActualWidth - FormatBar.ActualWidth - 6));
        y = Math.Clamp(y, 6, Math.Max(6, Overlay.ActualHeight - FormatBar.ActualHeight - 6));
        FormatBarShift.X = x;
        FormatBarShift.Y = y;
    }

    private void RefreshFormatState()
    {
        if (_activeEditor == null) return;
        FmtBold.IsChecked = _activeEditor.IsBold;
        FmtItalic.IsChecked = _activeEditor.IsItalic;
        FmtUnderline.IsChecked = _activeEditor.IsUnderline;
        FmtStrike.IsChecked = _activeEditor.IsStrike;
    }

    private void BuildFormatColors()
    {
        foreach (var (name, hex) in TextColors)
        {
            var dot = new Ellipse { Width = 16, Height = 16, Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand, ToolTip = name, StrokeThickness = 1 };
            dot.SetResourceReference(Shape.StrokeProperty, "StrokeStrongBrush");
            dot.Fill = hex == null ? new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x26)) : new SolidColorBrush(ColorUtil.Parse(hex));
            dot.MouseLeftButtonDown += (_, e) =>
            {
                _activeEditor?.SetTextColor(hex);
                e.Handled = true;
            };
            FmtColors.Children.Add(dot);
        }
    }

    private void OnFormat(object sender, RoutedEventArgs e)
    {
        if (_activeEditor == null || sender is not FrameworkElement { Tag: string command }) return;
        switch (command)
        {
            case "bold": _activeEditor.ToggleBold(); break;
            case "italic": _activeEditor.ToggleItalic(); break;
            case "underline": _activeEditor.ToggleUnderline(); break;
            case "strike": _activeEditor.ToggleStrike(); break;
            case "bullets": _activeEditor.ToggleBullets(); break;
            case "numbers": _activeEditor.ToggleNumbering(); break;
            case "h1": _activeEditor.SetHeading(1); break;
            case "h2": _activeEditor.SetHeading(2); break;
            case "normal": _activeEditor.SetHeading(0); break;
            case "checklist": _activeEditor.ToggleChecklist(); break;
            case "code": _activeEditor.ToggleCode(); break;
            case "clear": _activeEditor.ClearFormatting(); break;
            case "expand":
                if (_activeSticky is { } editing) OpenLarge(editing);
                return;
            case "highlight":
                bool has = _activeEditor.Editor.Selection.GetPropertyValue(System.Windows.Documents.TextElement.BackgroundProperty) is SolidColorBrush { Color.A: > 0 };
                _activeEditor.SetHighlight(has ? null : "#80FFE14D");
                break;
            case "smaller":
            case "bigger":
                if (_activeSticky != null)
                {
                    double current = _activeSticky.EffectiveFontSize;
                    double next = command == "bigger"
                        ? FontSteps.FirstOrDefault(f => f > current + 0.1, FontSteps[^1])
                        : FontSteps.LastOrDefault(f => f < current - 0.1, FontSteps[0]);
                    _activeSticky.FontSize = Math.Abs(next - 15) < 0.1 ? 0 : next;
                }
                _activeEditor.Editor.Focus();
                break;
            case "image":
                var editor = _activeEditor;
                var image = ImageService.PickImage(ImageService.PhotoSize, photo: true);
                if (image != null) editor.InsertImage(image);
                break;
        }
        RefreshFormatState();
    }

    // ----- connections -----

    private void BeginConnect(StickyNote from, Point start)
    {
        _connectFrom = from;
        _connectStart = start;
        Surface.CaptureMouse();
        OverLinks.SetPending(start, start);
        HideFormatBar();
        if (_vm != null) _vm.SelectedLink = null;
    }

    private void OnConnectHandleDown(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StickyNote s) return;
        e.Handled = true;
        BeginConnect(s, new Point(s.X + s.Width, s.Y + s.Height / 2));
    }

    private void OnConnectorMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null || sender is not BoardConnectors layer) return;
        var p = e.GetPosition(Surface);
        if (layer.PinAt(p) is { } pinned)
        {
            e.Handled = true;
            BeginConnect(pinned, pinned.PinPoint);
            return;
        }
        if (layer.LinkAt(p) is { } link)
        {
            e.Handled = true;
            _vm.SelectedLink = link;
            Focus();
        }
    }

    private void OnSurfaceMouseMove(object sender, MouseEventArgs e)
    {
        if (_connectFrom == null) return;
        var p = e.GetPosition(Surface);
        // Snap the preview to the pin of a note under the cursor on corkboards.
        if (_vm?.IsCork == true && StickyAt(p, _connectFrom) is { } target) p = target.PinPoint;
        OverLinks.SetPending(_connectStart, p);
    }

    private async void OnSurfaceMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_connectFrom == null || _vm == null) return;
        var from = _connectFrom;
        _connectFrom = null;
        var p = e.GetPosition(Surface);
        Surface.ReleaseMouseCapture();
        OverLinks.SetPending(null, null);
        if (StickyAt(p, from) is { } target)
        {
            OverLinks.AnimateNextLink();
            await _vm.ConnectAsync(from, target);
        }
    }

    private void OnSurfaceLostCapture(object sender, MouseEventArgs e)
    {
        if (_connectFrom == null) return;
        _connectFrom = null;
        OverLinks.SetPending(null, null);
    }

    private StickyNote? StickyAt(Point p, StickyNote? exclude) =>
        _vm?.Stickies.Where(s => !ReferenceEquals(s, exclude) && new Rect(s.X - 6, s.Y - 6, s.Width + 12, s.Height + 12).Contains(p))
            .OrderByDescending(s => s.ZIndex).FirstOrDefault();

    private void UpdateLinkBar()
    {
        if (_vm?.SelectedLink is not { } link)
        {
            LinkBar.Visibility = Visibility.Collapsed;
            return;
        }
        var layer = _vm.IsCork ? OverLinks : UnderLinks;
        LinkBar.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (layer.Midpoint(link) is not { } mid || !ReferenceEquals(_vm?.SelectedLink, link)) return;
            LinkBar.UpdateLayout();
            var p = Surface.TranslatePoint(mid, Overlay);
            LinkBarShift.X = Math.Clamp(p.X - LinkBar.ActualWidth / 2, 6, Math.Max(6, Overlay.ActualWidth - LinkBar.ActualWidth - 6));
            LinkBarShift.Y = Math.Clamp(p.Y - LinkBar.ActualHeight - 16, 6, Math.Max(6, Overlay.ActualHeight - LinkBar.ActualHeight - 6));
        });
    }

    private void BuildLinkColors()
    {
        if (_vm == null) return;
        LinkColorPanel.Children.Clear();
        IEnumerable<(string Key, string Name, Color Color)> options = _vm.IsCork
            ? BoardConnectors.StringColors
            : new[] { ("", "Accent", TryFindResource("AccentColor") is Color a ? a : Colors.Teal) }
                .Concat(_vm.LinkColors.Take(7).Select(t => (t.Key, t.Name, ColorUtil.Parse(t.Resolve(ThemeState.Instance.IsDark)))));
        foreach (var (key, name, color) in options)
        {
            var dot = new Ellipse { Width = 18, Height = 18, Margin = new Thickness(2, 0, 2, 0), Cursor = Cursors.Hand, ToolTip = name, Fill = new SolidColorBrush(color), StrokeThickness = 1 };
            dot.SetResourceReference(Shape.StrokeProperty, "StrokeStrongBrush");
            dot.MouseLeftButtonDown += async (_, e) =>
            {
                e.Handled = true;
                if (_vm?.SelectedLink is { } link) await _vm.SetLinkColorAsync(link, key);
            };
            LinkColorPanel.Children.Add(dot);
        }
    }

    // ----- canvas, keyboard, clipboard, drag & drop -----

    private async void OnCanvasMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_vm == null) return;
        if (e.OriginalSource is not Canvas && e.OriginalSource != StickyHost) return;
        _vm.SelectedLink = null;
        Focus();
        if (e.ClickCount != 2) return;
        await _vm.AddStickyAt(e.GetPosition(Surface));
        e.Handled = true;
    }

    private async void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        bool typing = Keyboard.FocusedElement is TextBoxBase;
        if (e.Key == Key.Escape)
        {
            if (ImagePreview.Visibility == Visibility.Visible) { ImagePreview.Visibility = Visibility.Collapsed; e.Handled = true; return; }
            if (_connectFrom != null) { Surface.ReleaseMouseCapture(); e.Handled = true; return; }
            if (_vm.SelectedLink != null) { _vm.SelectedLink = null; e.Handled = true; return; }
        }
        if (typing) return;
        if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control)
        {
            e.Handled = true;
            await _vm.AddFromDataAsync(Clipboard.GetDataObject(), _vm.ViewCenter);
        }
        else if (e.Key is Key.Delete or Key.Back && _vm.SelectedLink != null)
        {
            e.Handled = true;
            _vm.DeleteLinkCommand.Execute(null);
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        bool ok = ImageService.HasImage(e.Data) || e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent("UniformResourceLocatorW");
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_vm == null) return;
        e.Handled = true;
        await _vm.AddFromDataAsync(e.Data, e.GetPosition(Surface));
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_vm == null || !Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        if (e.Delta > 0) _vm.ZoomInCommand.Execute(null);
        else _vm.ZoomOutCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>Mouse wheel scrolls the board chips sideways when there are many boards.</summary>
    private void OnChipsWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || sv.ScrollableWidth <= 0) return;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta / 2.0);
        e.Handled = true;
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_vm == null || _vm.Zoom <= 0) return;
        _vm.ViewCenter = new Point((Scroll.HorizontalOffset + Scroll.ViewportWidth / 2) / _vm.Zoom,
                                   (Scroll.VerticalOffset + Scroll.ViewportHeight / 2) / _vm.Zoom);
        PositionFormatBar();
        if (_vm.SelectedLink != null) UpdateLinkBar();
    }

    /// <summary>Scrolls so the area (canvas coordinates) is in view — e.g. cards just added from other tabs.</summary>
    private void OnReveal(Rect area)
    {
        if (_vm == null || _vm.Zoom <= 0) return;
        double z = _vm.Zoom;
        var view = new Rect(Scroll.HorizontalOffset / z, Scroll.VerticalOffset / z, Scroll.ViewportWidth / z, Scroll.ViewportHeight / z);
        if (view.Contains(area)) return;
        Scroll.ScrollToHorizontalOffset(Math.Max(0, (area.X + area.Width / 2) * z - Scroll.ViewportWidth / 2));
        Scroll.ScrollToVerticalOffset(Math.Max(0, (area.Y + area.Height / 2) * z - Scroll.ViewportHeight / 2));
    }

    private void OnStickyAdded(StickyNote sticky)
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (RootOf(sticky) is not { } root) return;
            if (ScaleOf(root) is { } scale && Anim.Enabled)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim.To(1, 360, Anim.Back, 0.6));
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim.To(1, 360, Anim.Back, 0.6));
            }
            if (sticky.IsText && sticky.Text.Length == 0 && FindByName(root, "editor") is RichTextEditor editor) editor.FocusEditor();
        });
    }

    // ----- image & link actions -----

    private void OnImageDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not StickyNote { Image: { } image }) return;
        PreviewImage.Source = ImageService.ToBitmap(image);
        ImagePreview.Visibility = Visibility.Visible;
        Focus();
        e.Handled = true;
    }

    private void OnClosePreview(object sender, MouseButtonEventArgs e) => ImagePreview.Visibility = Visibility.Collapsed;

    private void OnLinkDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StickyNote s)
        {
            // Videos open in the study panel next to the board; other links in the browser.
            if (s.IsVideo && App.Services.GetService(typeof(Services.SettingsService)) is Services.SettingsService { Current.VideoPlayInApp: true })
                _vm?.StudyVideoCommand.Execute(s);
            else _vm?.OpenLinkCommand.Execute(s);
        }
        e.Handled = true;
    }

    // ----- the large editor (full toolbar) -----

    private void OnTextHeaderDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StickyNote s) OpenLarge(s);
        e.Handled = true;
    }

    private void OnOpenLarge(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StickyNote s) OpenLarge(s);
    }

    /// <summary>Saves what's typed on the board first, so the large editor starts from the latest text.</summary>
    private void OpenLarge(StickyNote sticky)
    {
        if (RootOf(sticky) is { } root && FindByName(root, "editor") is RichTextEditor editor) editor.Flush();
        HideFormatBar();
        _vm?.OpenStickyEditorCommand.Execute(sticky);
    }

    private void OnLiveDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is StickyNote s) _vm?.OpenLiveCommand.Execute(s);
        e.Handled = true;
    }

    // ----- study panel -----

    private void OnStudyLoad(string id, double start)
    {
        StudyPlayer.IsShown = true;
        StudyPlayer.Load(id, start);
    }

    private void OnStudySeek(double seconds) => StudyPlayer.Seek(seconds);

    /// <summary>Enter pins the note; Shift+Enter is a new line.</summary>
    private void OnStudyNoteKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        _vm?.AddStudyNoteCommand.Execute(null);
    }
}
