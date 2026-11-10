using System.Collections;
using System.Collections.Specialized;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Satchel.Services;

namespace Satchel.Controls;

/// <summary>A value on a chart. <see cref="Detail"/> is shown in the hover tooltip.</summary>
public sealed record ChartPoint(string Label, double Value, string Detail = "");

/// <summary>A donut slice; <see cref="Slot"/> picks the categorical colour (fixed order, never cycled).</summary>
public sealed record ChartSlice(string Label, double Value, int Slot, string ValueText)
{
    public Brush Brush => ChartPalette.Series(Slot);
}

/// <summary>
/// Chart colours. Categorical slots in a fixed order (no purple/indigo), validated for colour-vision deficiency and
/// contrast: light steps pass with direct labels/legend relief, dark steps pass every check.
/// </summary>
public static class ChartPalette
{
    private static readonly string[] Light = ["#2A78D6", "#EB6834", "#1BAF7A", "#EDA100", "#E87BA4", "#008300", "#E34948"];
    private static readonly string[] Dark = ["#3987E5", "#D95926", "#199E70", "#C98500", "#D55181", "#008300", "#E66767"];
    public const int Slots = 7;

    public static Brush Series(int slot)
    {
        var set = ThemeState.Instance.IsDark ? Dark : Light;
        if (slot < 0 || slot >= set.Length) return Resource("TextFaintBrush") ?? Brushes.Gray; // "Other"
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(set[slot]));
        b.Freeze();
        return b;
    }

    public static Brush? Resource(string key) => Application.Current?.TryFindResource(key) as Brush;
}

/// <summary>Shared drawing helpers: theme text, hairlines, tooltip bubble.</summary>
public abstract class ChartBase : FrameworkElement
{
    protected Point? Hover;

    protected ChartBase()
    {
        MouseMove += (_, e) => { Hover = e.GetPosition(this); InvalidateVisual(); };
        MouseLeave += (_, _) => { Hover = null; InvalidateVisual(); };
        ThemeState.Instance.PropertyChanged += (_, _) => InvalidateVisual();
        SnapsToDevicePixels = true;
    }

    protected static Brush TextBrush => ChartPalette.Resource("TextBrush") ?? Brushes.Black;
    protected static Brush MutedBrush => ChartPalette.Resource("TextMutedBrush") ?? Brushes.Gray;
    protected static Brush SurfaceBrush => ChartPalette.Resource("SurfaceBrush") ?? Brushes.White;
    protected static Brush GridBrush => ChartPalette.Resource("StrokeBrush") ?? Brushes.LightGray;

    protected FormattedText Text(string text, double size, Brush brush, bool bold = false) =>
        new(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal),
            size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>A small rounded bubble with one or two lines, kept inside the chart.</summary>
    protected void DrawTooltip(DrawingContext dc, Point anchor, string title, string value)
    {
        var t1 = Text(title, 11.5, MutedBrush);
        var t2 = Text(value, 13, TextBrush, bold: true);
        double w = Math.Max(t1.Width, t2.Width) + 20, h = t1.Height + t2.Height + 14;
        double x = Math.Clamp(anchor.X - w / 2, 0, Math.Max(0, ActualWidth - w));
        double y = anchor.Y - h - 10;
        if (y < 0) y = anchor.Y + 14;
        var bg = ChartPalette.Resource("ElevatedBrush") ?? SurfaceBrush;
        dc.DrawRoundedRectangle(bg, new Pen(GridBrush, 1), new Rect(x, y, w, h), 7, 7);
        dc.DrawText(t1, new Point(x + 10, y + 6));
        dc.DrawText(t2, new Point(x + 10, y + 6 + t1.Height));
    }

    protected static double NiceMax(double max)
    {
        if (max <= 0) return 1;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(max)));
        foreach (var step in new[] { 1, 2, 2.5, 5, 10 })
            if (step * magnitude >= max) return step * magnitude;
        return 10 * magnitude;
    }

    protected static string Compact(double v) => v >= 10000 ? (v / 1000).ToString("0.#", CultureInfo.CurrentCulture) + "K" : v.ToString(v == Math.Round(v) ? "#,0" : "#,0.#", CultureInfo.CurrentCulture);

    /// <summary>Re-draws when the bound collection changes.</summary>
    protected static void OnItemsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chart = (ChartBase)d;
        if (e.OldValue is INotifyCollectionChanged oldC) oldC.CollectionChanged -= chart.OnCollection;
        if (e.NewValue is INotifyCollectionChanged newC) newC.CollectionChanged += chart.OnCollection;
        chart.InvalidateVisual();
    }

    private void OnCollection(object? sender, NotifyCollectionChangedEventArgs e) => InvalidateVisual();
}

/// <summary>Columns over time or categories: single series, one hue (the accent), clean ticks, value on the tallest.</summary>
public sealed class ColumnChart : ChartBase
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IEnumerable), typeof(ColumnChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));
    public IEnumerable? Points { get => (IEnumerable?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    /// <summary>Suffix for values in tooltips and the top label ("h", "pages"…).</summary>
    public string Unit { get; set; } = "";
    /// <summary>Show every n-th category label on the axis (0 = automatic).</summary>
    public int LabelEvery { get; set; }

    protected override void OnRender(DrawingContext dc)
    {
        var points = Points?.OfType<ChartPoint>().ToList() ?? [];
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (points.Count == 0 || w < 40 || h < 40) return;
        const double left = 34, bottom = 22, top = 18;
        double plotW = w - left - 4, plotH = h - bottom - top;
        double max = NiceMax(points.Max(p => p.Value));
        var grid = new Pen(GridBrush, 1);
        // three hairline gridlines with clean ticks
        for (int i = 0; i <= 2; i++)
        {
            double v = max * i / 2, y = top + plotH - plotH * v / max;
            dc.DrawLine(grid, new Point(left, Math.Round(y) + 0.5), new Point(w - 4, Math.Round(y) + 0.5));
            var tick = Text(Compact(v), 10.5, MutedBrush);
            dc.DrawText(tick, new Point(left - tick.Width - 6, y - tick.Height / 2));
        }
        double slot = plotW / points.Count;
        double bar = Math.Min(24, Math.Max(3, slot * 0.62));
        int every = LabelEvery > 0 ? LabelEvery : Math.Max(1, (int)Math.Ceiling(points.Count / Math.Max(1, plotW / 46)));
        var accent = ChartPalette.Resource("AccentBrush") ?? Brushes.Teal;
        int hovered = Hover is { } hp && hp.X >= left ? Math.Clamp((int)((hp.X - left) / slot), 0, points.Count - 1) : -1;
        int tallest = points.IndexOf(points.MaxBy(p => p.Value)!);
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            double bh = max <= 0 ? 0 : plotH * p.Value / max;
            double x = left + slot * i + (slot - bar) / 2, y = top + plotH - bh;
            if (bh > 0.5)
            {
                var brush = hovered >= 0 && hovered != i ? WithOpacity(accent, 0.55) : accent;
                dc.DrawGeometry(brush, null, TopRounded(new Rect(x, y, bar, bh), Math.Min(4, bar / 2)));
            }
            if (i % every == 0 || i == points.Count - 1)
            {
                var label = Text(p.Label, 10.5, MutedBrush);
                double lx = Math.Clamp(x + bar / 2 - label.Width / 2, 0, w - label.Width);
                if (i == points.Count - 1 || lx + label.Width < left + slot * (i + every) + (slot - bar) / 2 + bar / 2 - 2)
                    dc.DrawText(label, new Point(lx, h - bottom + 5));
            }
        }
        // selective direct label: the tallest column only
        if (hovered < 0 && points[tallest].Value > 0)
        {
            var p = points[tallest];
            var t = Text(Compact(p.Value), 11, TextBrush, bold: true);
            double x = left + slot * tallest + slot / 2 - t.Width / 2, y = top + plotH - plotH * p.Value / max - t.Height - 2;
            dc.DrawText(t, new Point(Math.Clamp(x, 0, w - t.Width), Math.Max(0, y)));
        }
        if (hovered >= 0)
        {
            var p = points[hovered];
            double bh = plotH * p.Value / max;
            DrawTooltip(dc, new Point(left + slot * hovered + slot / 2, top + plotH - bh), p.Detail.Length > 0 ? p.Detail : p.Label, $"{Compact(p.Value)}{(Unit.Length > 0 ? " " + Unit : "")}");
        }
    }

    private static Brush WithOpacity(Brush b, double o)
    {
        var c = b.Clone();
        c.Opacity = o;
        c.Freeze();
        return c;
    }

    /// <summary>A bar with rounded top (data end) and a square base.</summary>
    private static Geometry TopRounded(Rect r, double radius)
    {
        radius = Math.Min(radius, r.Height);
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(new Point(r.Left, r.Bottom), true, true);
            c.LineTo(new Point(r.Left, r.Top + radius), false, false);
            c.ArcTo(new Point(r.Left + radius, r.Top), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(r.Right - radius, r.Top), false, false);
            c.ArcTo(new Point(r.Right, r.Top + radius), new Size(radius, radius), 0, false, SweepDirection.Clockwise, false, false);
            c.LineTo(new Point(r.Right, r.Bottom), false, false);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>A single line over time with a light wash, an end dot (surface ring) and a hover crosshair.</summary>
public sealed class LineChart : ChartBase
{
    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(nameof(Points), typeof(IEnumerable), typeof(LineChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));
    public IEnumerable? Points { get => (IEnumerable?)GetValue(PointsProperty); set => SetValue(PointsProperty, value); }

    /// <summary>Fixed y range (e.g. 1–5 for mood). 0 = automatic from 0.</summary>
    public double MinValue { get; set; }
    public double MaxValue { get; set; }
    public string Unit { get; set; } = "";
    /// <summary>Skip points with this value (no data). NaN = none.</summary>
    public double Missing { get; set; } = double.NaN;

    protected override void OnRender(DrawingContext dc)
    {
        var points = Points?.OfType<ChartPoint>().ToList() ?? [];
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (points.Count < 2 || w < 40 || h < 40) return;
        const double left = 34, bottom = 22, top = 16;
        double plotW = w - left - 10, plotH = h - bottom - top;
        double min = MinValue, max = MaxValue > MinValue ? MaxValue : NiceMax(points.Max(p => p.Value));
        var grid = new Pen(GridBrush, 1);
        for (int i = 0; i <= 2; i++)
        {
            double v = min + (max - min) * i / 2, y = top + plotH - plotH * (v - min) / (max - min);
            dc.DrawLine(grid, new Point(left, Math.Round(y) + 0.5), new Point(w - 4, Math.Round(y) + 0.5));
            var tick = Text(Compact(v), 10.5, MutedBrush);
            dc.DrawText(tick, new Point(left - tick.Width - 6, y - tick.Height / 2));
        }
        Point At(int i) => new(left + plotW * i / (points.Count - 1), top + plotH - plotH * (Math.Clamp(points[i].Value, min, max) - min) / (max - min));
        var accent = ChartPalette.Resource("AccentBrush") ?? Brushes.Teal;
        var valid = Enumerable.Range(0, points.Count).Where(i => double.IsNaN(Missing) || points[i].Value != Missing).ToList();
        if (valid.Count >= 2)
        {
            var line = new StreamGeometry();
            var area = new StreamGeometry();
            using (var c = line.Open())
            using (var a = area.Open())
            {
                c.BeginFigure(At(valid[0]), false, false);
                a.BeginFigure(new Point(At(valid[0]).X, top + plotH), true, true);
                a.LineTo(At(valid[0]), false, false);
                foreach (var i in valid.Skip(1)) { c.LineTo(At(i), true, true); a.LineTo(At(i), false, false); }
                a.LineTo(new Point(At(valid[^1]).X, top + plotH), false, false);
            }
            var wash = accent.Clone();
            wash.Opacity = 0.10;
            dc.DrawGeometry(wash, null, area);
            dc.DrawGeometry(null, new Pen(accent, 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, line);
            var end = At(valid[^1]);
            dc.DrawEllipse(accent, new Pen(SurfaceBrush, 2), end, 4.5, 4.5);
        }
        int every = Math.Max(1, (int)Math.Ceiling(points.Count / Math.Max(1, plotW / 54)));
        for (int i = 0; i < points.Count; i += every)
        {
            var label = Text(points[i].Label, 10.5, MutedBrush);
            dc.DrawText(label, new Point(Math.Clamp(At(i).X - label.Width / 2, 0, w - label.Width), h - bottom + 5));
        }
        if (Hover is { } hp && hp.X >= left - 4 && valid.Count > 0)
        {
            int nearest = valid.MinBy(i => Math.Abs(At(i).X - hp.X));
            var pt = At(nearest);
            dc.DrawLine(grid, new Point(pt.X, top), new Point(pt.X, top + plotH));
            dc.DrawEllipse(accent, new Pen(SurfaceBrush, 2), pt, 4.5, 4.5);
            var p = points[nearest];
            DrawTooltip(dc, pt, p.Detail.Length > 0 ? p.Detail : p.Label, $"{p.Value.ToString("0.#", CultureInfo.CurrentCulture)}{(Unit.Length > 0 ? " " + Unit : "")}");
        }
    }
}

/// <summary>Share of a whole: slices separated by a 2px surface gap, the total in the middle. Pair it with a legend list.</summary>
public sealed class DonutChart : ChartBase
{
    public static readonly DependencyProperty SlicesProperty = DependencyProperty.Register(nameof(Slices), typeof(IEnumerable), typeof(DonutChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnItemsChanged));
    public IEnumerable? Slices { get => (IEnumerable?)GetValue(SlicesProperty); set => SetValue(SlicesProperty, value); }

    public static readonly DependencyProperty CenterTextProperty = DependencyProperty.Register(nameof(CenterText), typeof(string), typeof(DonutChart),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public string CenterText { get => (string)GetValue(CenterTextProperty); set => SetValue(CenterTextProperty, value); }

    public string CenterCaption { get; set; } = "";

    protected override void OnRender(DrawingContext dc)
    {
        var slices = Slices?.OfType<ChartSlice>().Where(s => s.Value > 0).ToList() ?? [];
        double size = Math.Min(ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (size < 40) return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double outer = size / 2 - 2, thickness = Math.Max(10, size * 0.16), inner = outer - thickness;
        double total = slices.Sum(s => s.Value);
        if (total <= 0)
        {
            dc.DrawEllipse(null, new Pen(GridBrush, thickness), center, outer - thickness / 2, outer - thickness / 2);
        }
        else
        {
            double angle = -90;
            int hovered = -1;
            if (Hover is { } hp)
            {
                var v = hp - center;
                double dist = v.Length;
                if (dist >= inner - 4 && dist <= outer + 4)
                {
                    double a = Math.Atan2(v.Y, v.X) * 180 / Math.PI;
                    double rel = (a + 90 + 360) % 360, acc = 0;
                    for (int i = 0; i < slices.Count; i++)
                    {
                        acc += slices[i].Value / total * 360;
                        if (rel <= acc) { hovered = i; break; }
                    }
                }
            }
            var gap = new Pen(SurfaceBrush, 2);
            for (int i = 0; i < slices.Count; i++)
            {
                double sweep = slices[i].Value / total * 360;
                double grow = i == hovered ? 3 : 0;
                dc.DrawGeometry(slices[i].Brush, slices.Count > 1 ? gap : null, Segment(center, inner, outer + grow, angle, sweep));
                angle += sweep;
            }
            if (hovered >= 0)
            {
                var s = slices[hovered];
                DrawTooltip(dc, Hover!.Value, s.Label, $"{s.ValueText} · {s.Value / total:P0}");
            }
        }
        var main = Text(CenterText, Math.Max(13, size * 0.12), TextBrush, bold: true);
        var cap = Text(CenterCaption, 11, MutedBrush);
        double blockH = main.Height + (CenterCaption.Length > 0 ? cap.Height : 0);
        dc.DrawText(main, new Point(center.X - main.Width / 2, center.Y - blockH / 2));
        if (CenterCaption.Length > 0) dc.DrawText(cap, new Point(center.X - cap.Width / 2, center.Y - blockH / 2 + main.Height));
    }

    private static Geometry Segment(Point c, double inner, double outer, double startDeg, double sweepDeg)
    {
        if (sweepDeg >= 359.99)
        {
            var ring = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(c, outer, outer), new EllipseGeometry(c, inner, inner));
            ring.Freeze();
            return ring;
        }
        Point P(double r, double deg) => new(c.X + r * Math.Cos(deg * Math.PI / 180), c.Y + r * Math.Sin(deg * Math.PI / 180));
        bool large = sweepDeg > 180;
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(outer, startDeg), true, true);
            ctx.ArcTo(P(outer, startDeg + sweepDeg), new Size(outer, outer), 0, large, SweepDirection.Clockwise, true, true);
            ctx.LineTo(P(inner, startDeg + sweepDeg), true, true);
            ctx.ArcTo(P(inner, startDeg), new Size(inner, inner), 0, large, SweepDirection.Counterclockwise, true, true);
        }
        g.Freeze();
        return g;
    }
}

/// <summary>A day-by-day activity grid (weeks as columns), one hue light → dark.</summary>
public sealed class ActivityGrid : ChartBase
{
    public static readonly DependencyProperty DaysProperty = DependencyProperty.Register(nameof(Days), typeof(IEnumerable), typeof(ActivityGrid),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.AffectsMeasure, OnItemsChanged));
    /// <summary>One <see cref="ChartPoint"/> per day, oldest first, the first one on the first day of a week.</summary>
    public IEnumerable? Days { get => (IEnumerable?)GetValue(DaysProperty); set => SetValue(DaysProperty, value); }

    public event Action<int>? DayClicked;

    private const double Cell = 12, Gap = 3, TopLabels = 16, LeftLabels = 26;

    public ActivityGrid()
    {
        Cursor = Cursors.Hand;
        MouseLeftButtonUp += (_, e) =>
        {
            var p = e.GetPosition(this);
            int idx = IndexAt(p);
            if (idx >= 0) DayClicked?.Invoke(idx);
        };
    }

    private int IndexAt(Point p)
    {
        int col = (int)((p.X - LeftLabels) / (Cell + Gap)), row = (int)((p.Y - TopLabels) / (Cell + Gap));
        if (p.X < LeftLabels || p.Y < TopLabels || row > 6 || col < 0) return -1;
        int idx = col * 7 + row;
        int count = Days?.OfType<ChartPoint>().Count() ?? 0;
        return idx < count ? idx : -1;
    }

    protected override Size MeasureOverride(Size available)
    {
        int count = Days?.OfType<ChartPoint>().Count() ?? 0;
        int weeks = (count + 6) / 7;
        return new Size(LeftLabels + weeks * (Cell + Gap), TopLabels + 7 * (Cell + Gap));
    }

    protected override void OnRender(DrawingContext dc)
    {
        var days = Days?.OfType<ChartPoint>().ToList() ?? [];
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (days.Count == 0) return;
        double max = Math.Max(1, days.Max(d => d.Value));
        var accent = (ChartPalette.Resource("AccentBrush") as SolidColorBrush)?.Color ?? Colors.Teal;
        var empty = ChartPalette.Resource("SegmentBrush") ?? Brushes.LightGray;
        var dayNames = Core.Format.WeekdayInitials().ToList();
        for (int r = 1; r < 7; r += 2)
        {
            var t = Text(dayNames.ElementAtOrDefault(r) ?? "", 10, MutedBrush);
            dc.DrawText(t, new Point(0, TopLabels + r * (Cell + Gap) + (Cell - t.Height) / 2));
        }
        string lastMonth = "";
        int hovered = Hover is { } hp ? IndexAt(hp) : -1;
        for (int i = 0; i < days.Count; i++)
        {
            int col = i / 7, row = i % 7;
            double x = LeftLabels + col * (Cell + Gap), y = TopLabels + row * (Cell + Gap);
            var d = days[i];
            Brush fill;
            if (d.Value <= 0) fill = empty;
            else
            {
                // five steps of the accent from light to full
                double level = Math.Ceiling(4 * d.Value / max) / 4;
                fill = new SolidColorBrush(Color.FromArgb((byte)(70 + 185 * level), accent.R, accent.G, accent.B));
            }
            dc.DrawRoundedRectangle(fill, i == hovered ? new Pen(TextBrush, 1.2) : null, new Rect(x, y, Cell, Cell), 3, 3);
            if (row == 0 && d.Label != lastMonth)
            {
                lastMonth = d.Label;
                dc.DrawText(Text(d.Label, 10, MutedBrush), new Point(x, 0));
            }
        }
        if (hovered >= 0)
        {
            var d = days[hovered];
            int col = hovered / 7, row = hovered % 7;
            DrawTooltip(dc, new Point(LeftLabels + col * (Cell + Gap) + Cell / 2, TopLabels + row * (Cell + Gap)), d.Detail, d.Value == 0 ? "Nothing logged" : $"{d.Value:0} thing{(d.Value == 1 ? "" : "s")}");
        }
    }
}
