using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Satchel.Behaviors;
using Satchel.Core;

namespace Satchel.Controls;

/// <summary>
/// Base panel that animates children from their previous position to the new one (FLIP),
/// so reordering, inserting and removing items glides instead of jumping.
/// </summary>
public abstract class AnimatedPanel : Panel
{
    private readonly Dictionary<UIElement, Point> _last = new();
    private bool _arrangedOnce;
    private int _entranceIndex;

    public static readonly DependencyProperty AnimateProperty = DependencyProperty.Register(
        nameof(Animate), typeof(bool), typeof(AnimatedPanel), new PropertyMetadata(true));
    public bool Animate { get => (bool)GetValue(AnimateProperty); set => SetValue(AnimateProperty, value); }

    public static readonly DependencyProperty EntranceProperty = DependencyProperty.Register(
        nameof(Entrance), typeof(bool), typeof(AnimatedPanel), new PropertyMetadata(true));
    public bool Entrance { get => (bool)GetValue(EntranceProperty); set => SetValue(EntranceProperty, value); }

    public static readonly DependencyProperty DurationProperty = DependencyProperty.Register(
        nameof(Duration), typeof(double), typeof(AnimatedPanel), new PropertyMetadata(340.0));
    public double Duration { get => (double)GetValue(DurationProperty); set => SetValue(DurationProperty, value); }

    protected abstract Size ArrangeItems(Size finalSize);

    protected sealed override Size ArrangeOverride(Size finalSize)
    {
        _entranceIndex = 0;
        var size = ArrangeItems(finalSize);
        if (_last.Count > InternalChildren.Count)
        {
            var alive = new HashSet<UIElement>(InternalChildren.Cast<UIElement>());
            foreach (var dead in _last.Keys.Where(k => !alive.Contains(k)).ToList()) _last.Remove(dead);
        }
        _arrangedOnce = true;
        return size;
    }

    protected void Place(UIElement child, Rect rect)
    {
        child.Arrange(rect);
        bool animate = Anim.Enabled && Animate;

        if (_last.TryGetValue(child, out var old))
        {
            if (animate && (Math.Abs(old.X - rect.X) > 0.5 || Math.Abs(old.Y - rect.Y) > 0.5))
            {
                var tt = Translate(child);
                double fromX = old.X + tt.X - rect.X;
                double fromY = old.Y + tt.Y - rect.Y;
                var ms = Duration;
                tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(fromX, 0, Anim.Ms(ms)) { EasingFunction = Anim.EaseOutQuint });
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, Anim.Ms(ms)) { EasingFunction = Anim.EaseOutQuint });
            }
        }
        else if (animate && Entrance && !DnD.IsDragging)
        {
            var tt = Translate(child);
            double delay = _arrangedOnce ? 0 : Math.Min(_entranceIndex++ * 28, 360);
            double distance = _arrangedOnce ? 8 : 16;
            child.Opacity = 0;
            child.BeginAnimation(OpacityProperty, Anim.To(1, 260, Anim.EaseOut, 0, delay));
            tt.BeginAnimation(TranslateTransform.YProperty, Anim.To(0, 420, Anim.EaseOutQuint, distance, delay));
        }
        _last[child] = rect.TopLeft;
    }

    private static TranslateTransform Translate(UIElement child)
    {
        if (child.RenderTransform is TranslateTransform t && !t.IsFrozen) return t;
        var tt = new TranslateTransform();
        child.RenderTransform = tt;
        return tt;
    }
}

/// <summary>Pinterest-style columns. Children can span columns via MasonryPanel.Span.</summary>
public sealed class MasonryPanel : AnimatedPanel, IReorderPanel
{
    /// <summary>Width of one column at the last layout (for resizing by whole columns).</summary>
    public double ColumnWidth { get; private set; }

    /// <summary>Number of columns at the last layout.</summary>
    public int Columns { get; private set; } = 1;

    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(260.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double MinColumnWidth { get => (double)GetValue(MinColumnWidthProperty); set => SetValue(MinColumnWidthProperty, value); }

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(16.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public int MaxColumns { get => (int)GetValue(MaxColumnsProperty); set => SetValue(MaxColumnsProperty, value); }

    public static readonly DependencyProperty SpanProperty = DependencyProperty.RegisterAttached(
        "Span", typeof(int), typeof(MasonryPanel),
        new FrameworkPropertyMetadata(1, FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));
    public static int GetSpan(DependencyObject o) => (int)o.GetValue(SpanProperty);
    public static void SetSpan(DependencyObject o, int v) => o.SetValue(SpanProperty, v);

    private int ColumnCount(double width)
    {
        int cols = Math.Max(1, (int)((width + Spacing) / (MinColumnWidth + Spacing)));
        return MaxColumns > 0 ? Math.Min(cols, MaxColumns) : cols;
    }

    private IEnumerable<(UIElement Child, Rect Rect)> Layout(double width, bool measure)
    {
        int cols = ColumnCount(width);
        double colW = Math.Max(1, (width - Spacing * (cols - 1)) / cols);
        var heights = new double[cols];

        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            int span = Math.Clamp(SpanOf(child), 1, cols);
            double w = colW * span + Spacing * (span - 1);
            if (measure) child.Measure(new Size(w, double.PositiveInfinity));
            yield return (child, Place(heights, span, child.DesiredSize.Height, colW));
        }
    }

    /// <summary>Puts a card of the given span and height in the columns that let it sit highest.</summary>
    private Rect Place(double[] heights, int span, double height, double colW)
    {
        int cols = heights.Length;
        int bestCol = 0;
        double bestTop = double.MaxValue;
        for (int c = 0; c + span <= cols; c++)
        {
            double top = 0;
            for (int k = c; k < c + span; k++) top = Math.Max(top, heights[k]);
            if (top < bestTop - 0.5) { bestTop = top; bestCol = c; }
        }
        for (int k = bestCol; k < bestCol + span; k++) heights[k] = bestTop + height + Spacing;
        return new Rect(bestCol * (colW + Spacing), bestTop, colW * span + Spacing * (span - 1), height);
    }

    /// <summary>
    /// Where should a dragged card go? Tries every position in the order, works out where the card would land, and
    /// picks the spot closest to where it's being held. A new spot must be clearly closer than the current one, so
    /// cards don't flicker back and forth between two places.
    /// </summary>
    public int BestIndex(UIElement dragged, Rect held)
    {
        var children = InternalChildren.Cast<UIElement>().ToList();
        if (children.Any(c => c.Visibility == Visibility.Collapsed)) return -1;
        int from = children.IndexOf(dragged);
        if (from < 0) return -1;
        var others = children.Where(c => !ReferenceEquals(c, dragged)).ToList();
        double width = RenderSize.Width > 0 ? RenderSize.Width : _measuredWidth;
        if (!(width > 0)) return -1;
        int cols = ColumnCount(width);
        double colW = Math.Max(1, (width - Spacing * (cols - 1)) / cols);
        int span = Math.Clamp(SpanOf(dragged), 1, cols);
        double height = dragged.DesiredSize.Height;
        var target = new Point(held.X + held.Width / 2, held.Y + Math.Min(held.Height, height) / 2);

        int best = from;
        double bestScore = double.MaxValue, currentScore = double.MaxValue;
        for (int k = 0; k <= others.Count; k++)
        {
            var heights = new double[cols];
            for (int i = 0; i < k; i++) Place(heights, Math.Clamp(SpanOf(others[i]), 1, cols), others[i].DesiredSize.Height, colW);
            var rect = Place(heights, span, height, colW);
            var center = new Point(rect.X + rect.Width / 2, rect.Y + Math.Min(rect.Height, held.Height) / 2);
            double score = (center - target).Length;
            if (k == from) currentScore = score;
            if (score < bestScore) { bestScore = score; best = k; }
        }
        double margin = Math.Max(18, Math.Min(height, held.Height) * 0.12);
        return best != from && bestScore < currentScore - margin ? best : from;
    }


    private static int SpanOf(UIElement child)
    {
        int span = GetSpan(child);
        if (span == 1 && child is ContentPresenter { Content: DependencyObject content }) span = GetSpan(content);
        if (span == 1 && VisualTreeHelper.GetChildrenCount(child) > 0 && VisualTreeHelper.GetChild(child, 0) is DependencyObject first)
            span = Math.Max(span, GetSpan(first));
        return span;
    }

    private double _measuredWidth = double.NaN;

    protected override Size MeasureOverride(Size available)
    {
        double width = double.IsInfinity(available.Width) ? MinColumnWidth * 3 + Spacing * 2 : available.Width;
        _measuredWidth = width;
        double height = 0;
        foreach (var (_, rect) in Layout(width, true)) height = Math.Max(height, rect.Bottom);
        return new Size(width, height);
    }

    protected override Size ArrangeItems(Size finalSize)
    {
        double height = 0;
        Columns = ColumnCount(finalSize.Width);
        ColumnWidth = Math.Max(1, (finalSize.Width - Spacing * (Columns - 1)) / Columns);
        bool remeasure = Math.Abs(finalSize.Width - _measuredWidth) > 0.5;
        foreach (var (child, rect) in Layout(finalSize.Width, remeasure))
        {
            Place(child, rect);
            height = Math.Max(height, rect.Bottom);
        }
        return new Size(finalSize.Width, Math.Max(height, finalSize.Height));
    }
}

/// <summary>Stack panel with spacing and animated reflow.</summary>
public sealed class AnimatedStackPanel : AnimatedPanel
{
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(Orientation), typeof(AnimatedStackPanel),
        new FrameworkPropertyMetadata(Orientation.Vertical, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(AnimatedStackPanel),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    protected override Size MeasureOverride(Size available)
    {
        bool vertical = Orientation == Orientation.Vertical;
        var constraint = vertical ? new Size(available.Width, double.PositiveInfinity) : new Size(double.PositiveInfinity, available.Height);
        double main = 0, cross = 0;
        int count = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(constraint);
            var d = child.DesiredSize;
            main += vertical ? d.Height : d.Width;
            cross = Math.Max(cross, vertical ? d.Width : d.Height);
            count++;
        }
        main += Math.Max(0, count - 1) * Spacing;
        return vertical ? new Size(cross, main) : new Size(main, cross);
    }

    protected override Size ArrangeItems(Size finalSize)
    {
        bool vertical = Orientation == Orientation.Vertical;
        double offset = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            var d = child.DesiredSize;
            var rect = vertical
                ? new Rect(0, offset, finalSize.Width, d.Height)
                : new Rect(offset, 0, d.Width, finalSize.Height);
            Place(child, rect);
            offset += (vertical ? d.Height : d.Width) + Spacing;
        }
        return finalSize;
    }
}

/// <summary>A page whose view is slow to build (a long document, a big sheet): its view is kept while you're elsewhere.</summary>
public interface IKeepAlivePage
{
    /// <summary>The page was replaced (e.g. after showing private items) — its kept view goes too.</summary>
    bool IsDisposed { get; }
}

/// <summary>
/// Shows the current page with a slide-in. Pages marked <see cref="IKeepAlivePage"/> keep their view (just hidden) while
/// another page is open, so coming back is instant instead of laying out a long document again.
/// </summary>
public sealed class PageHost : Grid
{
    public static readonly DependencyProperty PageProperty = DependencyProperty.Register(nameof(Page), typeof(object), typeof(PageHost),
        new PropertyMetadata(null, (d, e) => ((PageHost)d).Show(e.NewValue)));

    public object? Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    private readonly ContentPresenter _presenter = new();
    private readonly Dictionary<object, FrameworkElement> _kept = [];

    public PageHost() => Children.Add(_presenter);

    private void Show(object? page)
    {
        // Views of pages that were replaced or closed go.
        foreach (var stale in _kept.Keys.Where(k => (k as IKeepAlivePage)?.IsDisposed == true || (page != null && k.GetType() == page.GetType() && !ReferenceEquals(k, page))).ToList())
        {
            Children.Remove(_kept[stale]);
            _kept.Remove(stale);
        }
        FrameworkElement shown;
        if (page is IKeepAlivePage)
        {
            if (!_kept.TryGetValue(page, out var view))
            {
                view = CreateView(page);
                _kept[page] = view;
                Children.Add(view);
            }
            _presenter.Content = null;
            _presenter.Visibility = Visibility.Collapsed;
            shown = view;
        }
        else
        {
            _presenter.Content = page;
            _presenter.Visibility = Visibility.Visible;
            shown = _presenter;
        }
        foreach (var v in _kept.Values)
        {
            bool visible = ReferenceEquals(v, shown);
            // A hidden page must not keep the keyboard (typing would go into a sheet or editor you can't see).
            if (!visible && v.IsKeyboardFocusWithin) System.Windows.Input.Keyboard.ClearFocus();
            v.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        }
        if (page != null) Anim.EnterUp(shown, 16, 320);
    }

    private FrameworkElement CreateView(object page)
    {
        if (TryFindResource(new DataTemplateKey(page.GetType())) is DataTemplate template && template.LoadContent() is FrameworkElement view)
        {
            view.DataContext = page;
            return view;
        }
        return new ContentPresenter { Content = page };
    }
}
