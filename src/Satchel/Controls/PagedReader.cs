using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Satchel.Core;

namespace Satchel.Controls;

/// <summary>
/// A PDF-like reading view for a FlowDocument: real pages with paper proportions and margins, one page or a two-page
/// spread, turned with the arrow keys, Page Up/Down, the mouse wheel or the buttons. Pages are sized to the window so
/// the text keeps its chosen size; resizing re-flows the pages and keeps your place.
/// </summary>
public sealed class PagedReader : Grid
{
    private const double Gap = 28;
    private const double NavHeight = 44;

    private readonly StackPanel _spread = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border[] _paper = new Border[2];
    private readonly DocumentPageView[] _views = new DocumentPageView[2];
    private readonly TextBlock[] _numbers = new TextBlock[2];
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 12, 0), FontSize = 12.5 };
    private readonly Slider _slider = new() { Minimum = 0, VerticalAlignment = VerticalAlignment.Center, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 2 };
    private readonly DispatcherTimer _relayout;
    private DynamicDocumentPaginator? _paginator;
    private bool _spreadMode;
    private bool _syncingSlider;
    private double _pendingFraction = -1;

    public PagedReader()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(NavHeight) });

        for (int i = 0; i < 2; i++)
        {
            _views[i] = new DocumentPageView { Stretch = Stretch.Fill };
            _numbers[i] = new TextBlock { FontSize = 11, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), Opacity = 0.6 };
            _numbers[i].SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
            _paper[i] = new Border
            {
                Child = _views[i], CornerRadius = new CornerRadius(2),
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Direction = 270, Opacity = 0.22, Color = Colors.Black }
            };
            var column = new StackPanel { Margin = new Thickness(i == 0 ? 0 : Gap, 0, 0, 0) };
            column.Children.Add(_paper[i]);
            column.Children.Add(_numbers[i]);
            _spread.Children.Add(column);
        }
        Children.Add(_spread);

        var prev = NavButton("", "Previous page (←)", () => Turn(-1));
        var next = NavButton("", "Next page (→)", () => Turn(+1));
        _status.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        var nav = new DockPanel { Margin = new Thickness(24, 0, 24, 6), MaxWidth = 720 };
        DockPanel.SetDock(prev, Dock.Left);
        DockPanel.SetDock(next, Dock.Right);
        DockPanel.SetDock(_status, Dock.Right);
        nav.Children.Add(prev);
        nav.Children.Add(next);
        nav.Children.Add(_status);
        nav.Children.Add(_slider);
        SetRow(nav, 1);
        Children.Add(nav);
        _slider.ValueChanged += (_, e) =>
        {
            if (_syncingSlider) return;
            GoTo((int)Math.Round(e.NewValue));
        };

        _relayout = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _relayout.Tick += (_, _) => { _relayout.Stop(); Paginate(); };
        SizeChanged += (_, _) => { _relayout.Stop(); _relayout.Start(); };
        PreviewKeyDown += OnKey;
        PreviewMouseWheel += (_, e) =>
        {
            Turn(e.Delta < 0 ? +1 : -1);
            e.Handled = true;
        };
        MouseLeftButtonDown += (_, _) => Focus();
    }

    private Button NavButton(string glyph, string tip, Action click)
    {
        var b = new Button { Content = glyph, ToolTip = tip, VerticalAlignment = VerticalAlignment.Center };
        b.SetResourceReference(StyleProperty, "IconButton");
        b.Click += (_, _) => click();
        return b;
    }

    // ----- properties -----

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(nameof(Document), typeof(FlowDocument), typeof(PagedReader),
        new PropertyMetadata(null, (d, _) => ((PagedReader)d).Paginate()));
    public FlowDocument? Document { get => (FlowDocument?)GetValue(DocumentProperty); set => SetValue(DocumentProperty, value); }

    /// <summary>"A4", "Letter", "Book" or "Fill".</summary>
    public string PageSize { get; set; } = "A4";
    /// <summary>"narrow", "normal" or "wide".</summary>
    public string Margins { get; set; } = "normal";
    public bool TwoPages { get; set; } = true;
    public Brush? PageBackground { get; set; }

    public int PageIndex { get; private set; }
    public int PageCount { get; private set; }

    /// <summary>Reading position 0..1 changed (page turned).</summary>
    public event Action<double>? PositionChanged;

    public double Position => PageCount <= 1 ? 1 : (double)PageIndex / (PageCount - 1);

    /// <summary>Opens at a reading position (0..1) once the pages are laid out.</summary>
    public void ShowPosition(double fraction)
    {
        _pendingFraction = Math.Clamp(fraction, 0, 1);
        if (PageCount > 0) ApplyPending();
    }

    /// <summary>Re-flows the pages (after settings or the document changed).</summary>
    public void Refresh() => Paginate();

    // ----- layout -----

    private double AspectRatio => PageSize switch
    {
        "Letter" => 11.0 / 8.5,
        "Book" => 1.5,
        "Fill" => 0,
        _ => Math.Sqrt(2)
    };

    private void Paginate()
    {
        var doc = Document;
        if (doc == null || ActualWidth < 120 || ActualHeight < 160)
        {
            _paginator = null;
            PageCount = 0;
            Show();
            return;
        }
        double keep = _pendingFraction >= 0 ? _pendingFraction : Position;
        var area = new Size(Math.Max(100, ActualWidth - 48), Math.Max(100, ActualHeight - NavHeight - 52));
        double ratio = AspectRatio;
        double pageW, pageH;
        if (ratio <= 0)
        {
            _spreadMode = TwoPages && area.Width > 1100;
            pageW = Math.Min(860, _spreadMode ? (area.Width - Gap) / 2 : area.Width);
            pageH = area.Height;
        }
        else
        {
            pageH = area.Height;
            pageW = pageH / ratio;
            _spreadMode = TwoPages && pageW * 2 + Gap <= area.Width;
            if (!_spreadMode && pageW > area.Width)
            {
                pageW = area.Width;
                pageH = pageW * ratio;
            }
        }
        double margin = pageW * (Margins switch { "narrow" => 0.07, "wide" => 0.15, _ => 0.105 });
        doc.PagePadding = new Thickness(margin, margin * 0.9, margin, margin * 1.1);
        doc.ColumnWidth = double.PositiveInfinity;
        doc.ColumnGap = 0;
        doc.PageWidth = pageW;
        doc.PageHeight = pageH;
        if (PageBackground != null) doc.Background = PageBackground;
        _paginator = (DynamicDocumentPaginator)((IDocumentPaginatorSource)doc).DocumentPaginator;
        _paginator.PageSize = new Size(pageW, pageH);
        _paginator.ComputePageCount();
        PageCount = Math.Max(1, _paginator.PageCount);
        foreach (var p in _paper)
        {
            p.Width = pageW;
            p.Height = pageH;
            p.Background = PageBackground ?? Brushes.White;
        }
        foreach (var v in _views) v.DocumentPaginator = _paginator;
        _pendingFraction = keep;
        ApplyPending();
    }

    private void ApplyPending()
    {
        if (_pendingFraction < 0) { Show(); return; }
        int index = (int)Math.Round(_pendingFraction * (PageCount - 1));
        _pendingFraction = -1;
        PageIndex = Normalize(index);
        Show();
    }

    private int Normalize(int index)
    {
        index = Math.Clamp(index, 0, Math.Max(0, PageCount - 1));
        return _spreadMode ? index - index % 2 : index;
    }

    private void Show()
    {
        bool has = _paginator != null && PageCount > 0;
        _views[0].PageNumber = PageIndex;
        _paper[0].Visibility = has ? Visibility.Visible : Visibility.Hidden;
        _numbers[0].Text = has ? (PageIndex + 1).ToString() : "";
        bool second = has && _spreadMode && PageIndex + 1 < PageCount;
        ((FrameworkElement)_spread.Children[1]).Visibility = _spreadMode ? Visibility.Visible : Visibility.Collapsed;
        _paper[1].Visibility = second ? Visibility.Visible : Visibility.Hidden;
        if (second) _views[1].PageNumber = PageIndex + 1;
        _numbers[1].Text = second ? (PageIndex + 2).ToString() : "";
        _status.Text = has ? (second ? $"Pages {PageIndex + 1}–{PageIndex + 2} of {PageCount}" : $"Page {PageIndex + 1} of {PageCount}") : "";
        _syncingSlider = true;
        _slider.Maximum = Math.Max(0, PageCount - 1);
        _slider.Value = PageIndex;
        _slider.Visibility = PageCount > 1 ? Visibility.Visible : Visibility.Hidden;
        _syncingSlider = false;
    }

    // ----- turning pages -----

    public void Turn(int direction)
    {
        if (PageCount == 0) return;
        int step = _spreadMode ? 2 : 1;
        GoTo(PageIndex + direction * step, direction);
    }

    public void GoTo(int index, int direction = 0)
    {
        if (PageCount == 0) return;
        int target = Normalize(index);
        if (target == PageIndex) return;
        if (direction == 0) direction = Math.Sign(target - PageIndex);
        PageIndex = target;
        Show();
        if (Anim.Enabled)
        {
            var shift = Anim.EnsureTranslate(_spread);
            shift.BeginAnimation(TranslateTransform.XProperty, Anim.To(0, 220, Anim.EaseOut, from: 26 * direction));
            _spread.BeginAnimation(OpacityProperty, Anim.To(1, 200, null, from: 0.55));
        }
        PositionChanged?.Invoke(Position);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;
        switch (e.Key)
        {
            case Key.Right or Key.PageDown or Key.Space or Key.Down:
                Turn(+1);
                e.Handled = true;
                break;
            case Key.Left or Key.PageUp or Key.Up:
                Turn(-1);
                e.Handled = true;
                break;
            case Key.Home:
                GoTo(0);
                e.Handled = true;
                break;
            case Key.End:
                GoTo(PageCount - 1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Shows the page that contains a text position (for the table of contents and find).</summary>
    public void ShowContent(TextPointer pointer)
    {
        if (_paginator == null) return;
        var position = _paginator.GetPagePosition(_paginator.GetPage(PageIndex));
        // Search forward/backward for the page whose content range contains the pointer.
        for (int i = 0; i < PageCount; i++)
        {
            var page = _paginator.GetPage(i);
            if (page is not DocumentPage dp || dp == DocumentPage.Missing) continue;
            if (_paginator.GetPagePosition(dp) is TextPointer start && start.CompareTo(pointer) > 0)
            {
                GoTo(Math.Max(0, i - 1));
                return;
            }
        }
        GoTo(PageCount - 1);
        _ = position;
    }
}
