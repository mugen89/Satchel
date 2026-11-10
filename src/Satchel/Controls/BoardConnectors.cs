using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using Satchel.Core;
using Satchel.Models;
using Satchel.Services;

namespace Satchel.Controls;

public enum ConnectorLayer { Under, Over }

/// <summary>
/// Draws the connections between board notes. On clean boards they are gentle curves drawn under the notes;
/// on corkboards they are strings of yarn tied to push-pins, simulated with verlet physics so they sag and
/// swing when notes move. The "Over" layer also draws the pins and the live preview of a connection being dragged.
/// Everything is rendered in one pass (no element per link) and hit-testing is geometric.
/// </summary>
public sealed class BoardConnectors : FrameworkElement
{
    private const int Segments = 16;
    private const double Gravity = 1300;
    private const double Damping = 0.972;
    private const double HitTolerance = 7;
    public const double PinRadius = 7.5;

    public static readonly DependencyProperty StickiesProperty = DependencyProperty.Register(
        nameof(Stickies), typeof(IEnumerable<StickyNote>), typeof(BoardConnectors), new PropertyMetadata(null, OnStickiesChanged));
    public IEnumerable<StickyNote>? Stickies { get => (IEnumerable<StickyNote>?)GetValue(StickiesProperty); set => SetValue(StickiesProperty, value); }

    public static readonly DependencyProperty LinksProperty = DependencyProperty.Register(
        nameof(Links), typeof(IEnumerable<BoardLink>), typeof(BoardConnectors), new PropertyMetadata(null, OnLinksChanged));
    public IEnumerable<BoardLink>? Links { get => (IEnumerable<BoardLink>?)GetValue(LinksProperty); set => SetValue(LinksProperty, value); }

    public static readonly DependencyProperty IsCorkProperty = DependencyProperty.Register(
        nameof(IsCork), typeof(bool), typeof(BoardConnectors), new PropertyMetadata(false, (d, _) => ((BoardConnectors)d).ResetRopes(settled: true)));
    public bool IsCork { get => (bool)GetValue(IsCorkProperty); set => SetValue(IsCorkProperty, value); }

    public static readonly DependencyProperty LayerProperty = DependencyProperty.Register(
        nameof(Layer), typeof(ConnectorLayer), typeof(BoardConnectors), new PropertyMetadata(ConnectorLayer.Under, (d, _) => ((BoardConnectors)d).ResetRopes(settled: true)));
    public ConnectorLayer Layer { get => (ConnectorLayer)GetValue(LayerProperty); set => SetValue(LayerProperty, value); }

    public static readonly DependencyProperty SelectedLinkProperty = DependencyProperty.Register(
        nameof(SelectedLink), typeof(BoardLink), typeof(BoardConnectors), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public BoardLink? SelectedLink { get => (BoardLink?)GetValue(SelectedLinkProperty); set => SetValue(SelectedLinkProperty, value); }

    private readonly Dictionary<BoardLink, Rope> _ropes = new();
    private readonly Dictionary<BoardLink, Point[]> _hitPaths = new();
    private readonly Dictionary<int, StickyNote> _byId = new();
    private readonly HashSet<StickyNote> _watched = new();
    private readonly HashSet<BoardLink> _watchedLinks = new();
    private INotifyCollectionChanged? _stickySource, _linkSource;
    private bool _animating, _animateNext;
    private TimeSpan _lastTick;
    private double _accumulator;
    private Point? _pendingFrom, _pendingTo;

    private bool DrawsRopes => IsCork && Layer == ConnectorLayer.Over;
    private bool DrawsCurves => !IsCork && Layer == ConnectorLayer.Under;

    public BoardConnectors()
    {
        Loaded += (_, _) => { ThemeState.Instance.PropertyChanged += OnTheme; ResetRopes(settled: true); };
        Unloaded += (_, _) => { ThemeState.Instance.PropertyChanged -= OnTheme; StopAnimation(); };
    }

    private void OnTheme(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    /// <summary>The next link that appears starts taut and drops into place (a freshly tied string).</summary>
    public void AnimateNextLink() => _animateNext = true;

    /// <summary>Live preview while dragging out a new connection (null hides it).</summary>
    public void SetPending(Point? from, Point? to)
    {
        _pendingFrom = from;
        _pendingTo = to;
        InvalidateVisual();
    }

    // ----- source tracking -----

    private static void OnStickiesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (BoardConnectors)d;
        if (c._stickySource != null) c._stickySource.CollectionChanged -= c.OnStickyCollectionChanged;
        c._stickySource = e.NewValue as INotifyCollectionChanged;
        if (c._stickySource != null) c._stickySource.CollectionChanged += c.OnStickyCollectionChanged;
        c.SyncStickies();
    }

    private static void OnLinksChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var c = (BoardConnectors)d;
        if (c._linkSource != null) c._linkSource.CollectionChanged -= c.OnLinkCollectionChanged;
        c._linkSource = e.NewValue as INotifyCollectionChanged;
        if (c._linkSource != null) c._linkSource.CollectionChanged += c.OnLinkCollectionChanged;
        c.SyncLinks();
        c.ResetRopes(settled: true);
    }

    private void OnStickyCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncStickies();
        InvalidateVisual();
    }

    private void OnLinkCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SyncLinks();
        if (e.Action == NotifyCollectionChangedAction.Add && e.NewItems != null)
        {
            foreach (BoardLink link in e.NewItems) CreateRope(link, settled: !_animateNext);
            _animateNext = false;
            StartAnimation();
        }
        else if (e.Action is NotifyCollectionChangedAction.Remove && e.OldItems != null)
        {
            foreach (BoardLink link in e.OldItems) _ropes.Remove(link);
        }
        else ResetRopes(settled: true);
        InvalidateVisual();
    }

    private void SyncStickies()
    {
        var current = Stickies?.ToList() ?? [];
        foreach (var s in _watched.Except(current).ToList())
        {
            s.PropertyChanged -= OnStickyChanged;
            _watched.Remove(s);
        }
        foreach (var s in current.Where(s => _watched.Add(s))) s.PropertyChanged += OnStickyChanged;
        _byId.Clear();
        foreach (var s in current) _byId[s.Id] = s;
    }

    private void SyncLinks()
    {
        var current = Links?.ToList() ?? [];
        foreach (var l in _watchedLinks.Except(current).ToList())
        {
            l.PropertyChanged -= OnLinkChanged;
            _watchedLinks.Remove(l);
        }
        foreach (var l in current.Where(l => _watchedLinks.Add(l))) l.PropertyChanged += OnLinkChanged;
        foreach (var gone in _ropes.Keys.Except(current).ToList()) _ropes.Remove(gone);
    }

    private void OnLinkChanged(object? sender, PropertyChangedEventArgs e) => InvalidateVisual();

    private void OnStickyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not StickyNote s) return;
        switch (e.PropertyName)
        {
            case nameof(StickyNote.X) or nameof(StickyNote.Y) or nameof(StickyNote.Width) or nameof(StickyNote.Height) or nameof(StickyNote.Rotation):
                if (DrawsRopes)
                {
                    foreach (var (link, rope) in _ropes)
                        if (link.FromId == s.Id || link.ToId == s.Id) rope.Wake();
                    StartAnimation();
                }
                InvalidateVisual();
                break;
            case nameof(StickyNote.Pin) or nameof(StickyNote.Id):
                if (e.PropertyName == nameof(StickyNote.Id)) SyncStickies();
                InvalidateVisual();
                break;
        }
    }

    // ----- physics -----

    private void ResetRopes(bool settled)
    {
        _ropes.Clear();
        if (!DrawsRopes || Links == null) { InvalidateVisual(); return; }
        foreach (var link in Links) CreateRope(link, settled);
        InvalidateVisual();
    }

    private void CreateRope(BoardLink link, bool settled)
    {
        if (!DrawsRopes) return;
        if (!_byId.TryGetValue(link.FromId, out var a) || !_byId.TryGetValue(link.ToId, out var b)) return;
        var rope = new Rope();
        rope.Init(a.PinPoint, b.PinPoint, settled);
        _ropes[link] = rope;
    }

    private void StartAnimation()
    {
        if (_animating || !DrawsRopes) return;
        if (!Anim.Enabled)
        {
            // No animation: jump straight to the resting shape.
            foreach (var (link, rope) in _ropes)
                if (_byId.TryGetValue(link.FromId, out var a) && _byId.TryGetValue(link.ToId, out var b)) rope.Settle(a.PinPoint, b.PinPoint);
            InvalidateVisual();
            return;
        }
        _animating = true;
        _lastTick = TimeSpan.Zero;
        _accumulator = 0;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = (e as RenderingEventArgs)?.RenderingTime ?? TimeSpan.Zero;
        if (_lastTick == TimeSpan.Zero) { _lastTick = now; return; }
        var dt = Math.Min(0.05, (now - _lastTick).TotalSeconds);
        _lastTick = now;
        if (dt <= 0) return;
        _accumulator += dt;
        const double step = 1 / 60.0;
        bool any = false;
        int steps = 0;
        while (_accumulator >= step && steps++ < 4)
        {
            _accumulator -= step;
            foreach (var (link, rope) in _ropes)
            {
                if (rope.Asleep) continue;
                if (!_byId.TryGetValue(link.FromId, out var a) || !_byId.TryGetValue(link.ToId, out var b)) continue;
                rope.Step(a.PinPoint, b.PinPoint, step);
                any = true;
            }
        }
        if (_ropes.Values.All(r => r.Asleep) && !any) StopAnimation();
        InvalidateVisual();
    }

    private sealed class Rope
    {
        public readonly Point[] P = new Point[Segments + 1];
        private readonly Point[] _prev = new Point[Segments + 1];
        private double _length;
        private int _quiet;
        public bool Asleep { get; private set; }

        private static double TargetLength(Point a, Point b) => (b - a).Length * 1.028 + 6;

        public void Wake()
        {
            Asleep = false;
            _quiet = 0;
        }

        public void Init(Point a, Point b, bool settled)
        {
            _length = TargetLength(a, b);
            for (int i = 0; i <= Segments; i++)
            {
                double t = (double)i / Segments;
                P[i] = _prev[i] = a + (b - a) * t;
            }
            Wake();
            if (settled) Settle(a, b);
        }

        public void Settle(Point a, Point b)
        {
            for (int k = 0; k < 300; k++) Step(a, b, 1 / 60.0);
            Array.Copy(P, _prev, P.Length);
            Asleep = true;
        }

        public void Step(Point a, Point b, double dt)
        {
            _length += (TargetLength(a, b) - _length) * 0.12;
            double seg = _length / Segments;
            var gravity = new Vector(0, Gravity * dt * dt);
            for (int i = 1; i < Segments; i++)
            {
                var velocity = (P[i] - _prev[i]) * Damping;
                _prev[i] = P[i];
                P[i] = P[i] + velocity + gravity;
            }
            P[0] = _prev[0] = a;
            P[Segments] = _prev[Segments] = b;
            for (int k = 0; k < 12; k++)
            {
                for (int i = 0; i < Segments; i++)
                {
                    var d = P[i + 1] - P[i];
                    double len = d.Length;
                    if (len < 1e-6) continue;
                    var correction = d * ((len - seg) / len);
                    if (i == 0) P[1] -= correction;
                    else if (i + 1 == Segments) P[i] += correction;
                    else
                    {
                        P[i] += correction * 0.5;
                        P[i + 1] -= correction * 0.5;
                    }
                }
            }
            double moved = 0;
            for (int i = 1; i < Segments; i++) moved = Math.Max(moved, (P[i] - _prev[i]).LengthSquared);
            if (moved < 0.0009) { if (++_quiet > 24) Asleep = true; }
            else _quiet = 0;
        }
    }

    // ----- rendering -----

    protected override void OnRender(DrawingContext dc)
    {
        _hitPaths.Clear();
        var links = Links?.ToList() ?? [];
        if (DrawsRopes)
        {
            foreach (var link in links)
            {
                if (!_ropes.TryGetValue(link, out var rope))
                {
                    CreateRope(link, settled: true);
                    if (!_ropes.TryGetValue(link, out rope)) continue;
                }
                DrawRope(dc, link, rope.P, ReferenceEquals(link, SelectedLink));
            }
            foreach (var s in Stickies ?? []) DrawPin(dc, s.PinPoint, PinColor(s));
        }
        else if (DrawsCurves)
        {
            foreach (var link in links)
                if (_byId.TryGetValue(link.FromId, out var a) && _byId.TryGetValue(link.ToId, out var b))
                    DrawCurve(dc, link, a, b, ReferenceEquals(link, SelectedLink));
        }

        if (Layer == ConnectorLayer.Over && _pendingFrom is { } from && _pendingTo is { } to)
        {
            if (IsCork)
            {
                var pen = YarnPen(RopeColor(""));
                dc.DrawLine(pen, from, to);
                DrawPin(dc, to, PinColors[0].Color);
            }
            else
            {
                var accent = Accent();
                var pen = new Pen(new SolidColorBrush(accent), 2) { DashStyle = new DashStyle([3, 2], 0), DashCap = PenLineCap.Round };
                dc.DrawLine(pen, from, to);
                dc.DrawEllipse(new SolidColorBrush(accent), null, to, 5, 5);
            }
        }
    }

    private void DrawRope(DrawingContext dc, BoardLink link, Point[] points, bool selected)
    {
        var geometry = Smooth(points);
        var color = RopeColor(link.Color);
        if (selected) dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(110, Accent().R, Accent().G, Accent().B)), 9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        dc.PushTransform(new TranslateTransform(1.8, 4.5));
        dc.DrawGeometry(null, RopeShadowPen, geometry);
        dc.Pop();
        dc.DrawGeometry(null, YarnPen(color), geometry);
        dc.DrawGeometry(null, YarnHighlightPen(color), geometry);
        _hitPaths[link] = points.ToArray();
        if (!string.IsNullOrWhiteSpace(link.Label)) DrawLabel(dc, points[Segments / 2], link.Label, cork: true, color);
    }

    private void DrawCurve(DrawingContext dc, BoardLink link, StickyNote a, StickyNote b, bool selected)
    {
        var ra = new Rect(a.X, a.Y, a.Width, a.Height);
        var rb = new Rect(b.X, b.Y, b.Width, b.Height);
        var ca = new Point(ra.X + ra.Width / 2, ra.Y + ra.Height / 2);
        var cb = new Point(rb.X + rb.Width / 2, rb.Y + rb.Height / 2);
        var start = EdgePoint(ra, cb, 5);
        var end = EdgePoint(rb, ca, link.Arrow ? 9 : 5);
        var d = end - start;
        double len = d.Length;
        if (len < 6) return;
        var normal = new Vector(-d.Y, d.X) / len;
        double bend = Math.Min(48, len * 0.1);
        var c1 = start + d / 3 + normal * bend;
        var c2 = start + d * 2 / 3 + normal * bend;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(start, false, false);
            ctx.BezierTo(c1, c2, end, true, true);
        }
        geometry.Freeze();

        var color = CurveColor(link.Color);
        var brush = new SolidColorBrush(color);
        if (selected) dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(70, color.R, color.G, color.B)), 9) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        dc.DrawGeometry(null, new Pen(brush, selected ? 2.6 : 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geometry);
        dc.DrawEllipse(brush, null, start, 3.2, 3.2);

        if (link.Arrow)
        {
            var dir = end - c2;
            if (dir.Length > 0.01)
            {
                dir.Normalize();
                var side = new Vector(-dir.Y, dir.X);
                var tip = end + dir * 4;
                var arrow = new StreamGeometry();
                using (var ctx = arrow.Open())
                {
                    ctx.BeginFigure(tip, true, true);
                    ctx.LineTo(tip - dir * 11 + side * 5.5, true, true);
                    ctx.LineTo(tip - dir * 11 - side * 5.5, true, true);
                }
                arrow.Freeze();
                dc.DrawGeometry(brush, null, arrow);
            }
        }
        else dc.DrawEllipse(brush, null, end, 3.2, 3.2);

        var samples = new Point[25];
        for (int i = 0; i < samples.Length; i++) samples[i] = Bezier(start, c1, c2, end, i / 24.0);
        _hitPaths[link] = samples;
        if (!string.IsNullOrWhiteSpace(link.Label)) DrawLabel(dc, Bezier(start, c1, c2, end, 0.5), link.Label, cork: false, color);
    }

    private void DrawLabel(DrawingContext dc, Point at, string text, bool cork, Color color)
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = new Typeface(cork ? new FontFamily("Segoe Print, Segoe UI") : new FontFamily("Segoe UI Variable Text, Segoe UI"),
            FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
        var textBrush = cork ? new SolidColorBrush(Color.FromRgb(0x33, 0x2A, 0x22)) : (TryFindResource("TextBrush") as Brush ?? Brushes.Black);
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, cork ? 12 : 11.5, textBrush, dpi)
        {
            MaxTextWidth = 220,
            Trimming = TextTrimming.CharacterEllipsis,
            MaxLineCount = 2
        };
        var rect = new Rect(at.X - ft.Width / 2 - 8, at.Y - ft.Height / 2 - 3, ft.Width + 16, ft.Height + 6);
        if (cork)
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), null, new Rect(rect.X + 1.5, rect.Y + 2.5, rect.Width, rect.Height));
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xFB, 0xF6, 0xE9)), new Pen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)), 0.6), rect);
        }
        else
        {
            var bg = TryFindResource("ElevatedBrush") as Brush ?? Brushes.White;
            dc.DrawRoundedRectangle(bg, new Pen(new SolidColorBrush(color), 1.2), rect, rect.Height / 2, rect.Height / 2);
        }
        dc.DrawText(ft, new Point(rect.X + 8, rect.Y + 3));
    }


    private static void DrawPin(DrawingContext dc, Point c, Color color)
    {
        dc.DrawEllipse(PinShadowOuter, null, c + new Vector(3.6, 4.8), PinRadius + 1.6, PinRadius + 0.8);
        dc.DrawEllipse(PinShadowInner, null, c + new Vector(2.6, 3.6), PinRadius - 0.4, PinRadius - 1);
        dc.DrawEllipse(HeadBrush(color), HeadPen(color), c, PinRadius, PinRadius);
        dc.DrawEllipse(Specular, null, c + new Vector(-2.5, -2.7), 2.6, 1.9);
    }

    // ----- hit testing -----

    protected override HitTestResult? HitTestCore(PointHitTestParameters hitTestParameters)
    {
        var p = hitTestParameters.HitPoint;
        if (LinkAt(p) != null || (DrawsRopes && PinAt(p) != null)) return new PointHitTestResult(this, p);
        return null;
    }

    public BoardLink? LinkAt(Point p)
    {
        if (!DrawsRopes && !DrawsCurves) return null;
        BoardLink? best = null;
        double bestDistance = HitTolerance;
        foreach (var (link, path) in _hitPaths)
        {
            for (int i = 0; i < path.Length - 1; i++)
            {
                var dist = DistanceToSegment(p, path[i], path[i + 1]);
                if (dist < bestDistance) { bestDistance = dist; best = link; }
            }
        }
        return best;
    }

    /// <summary>The note whose push-pin is under the point (corkboards only).</summary>
    public StickyNote? PinAt(Point p)
    {
        if (!DrawsRopes || Stickies == null) return null;
        return Stickies.OrderByDescending(s => s.ZIndex).FirstOrDefault(s => (s.PinPoint - p).Length <= PinRadius + 3);
    }

    /// <summary>Midpoint of a drawn link (board coordinates), for placing its toolbar.</summary>
    public Point? Midpoint(BoardLink link) => _hitPaths.TryGetValue(link, out var path) && path.Length > 0 ? path[path.Length / 2] : null;

    private static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double lengthSq = ab.LengthSquared;
        if (lengthSq < 1e-9) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / lengthSq, 0, 1);
        return (p - (a + ab * t)).Length;
    }

    // ----- geometry helpers -----

    private static Point Bezier(Point p0, Point p1, Point p2, Point p3, double t)
    {
        double u = 1 - t;
        return new Point(
            u * u * u * p0.X + 3 * u * u * t * p1.X + 3 * u * t * t * p2.X + t * t * t * p3.X,
            u * u * u * p0.Y + 3 * u * u * t * p1.Y + 3 * u * t * t * p2.Y + t * t * t * p3.Y);
    }

    private static Point EdgePoint(Rect r, Point toward, double pad)
    {
        r.Inflate(pad, pad);
        var c = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        var d = toward - c;
        if (Math.Abs(d.X) < 1e-6 && Math.Abs(d.Y) < 1e-6) return c;
        double tx = Math.Abs(d.X) > 1e-6 ? r.Width / 2 / Math.Abs(d.X) : double.MaxValue;
        double ty = Math.Abs(d.Y) > 1e-6 ? r.Height / 2 / Math.Abs(d.Y) : double.MaxValue;
        return c + d * Math.Min(Math.Min(tx, ty), 1);
    }

    private static StreamGeometry Smooth(IReadOnlyList<Point> p)
    {
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(p[0], false, false);
            for (int i = 0; i < p.Count - 1; i++)
            {
                var p0 = i > 0 ? p[i - 1] : p[i];
                var p1 = p[i];
                var p2 = p[i + 1];
                var p3 = i + 2 < p.Count ? p[i + 2] : p2;
                ctx.BezierTo(p1 + (p2 - p0) / 6, p2 - (p3 - p1) / 6, p2, true, true);
            }
        }
        g.Freeze();
        return g;
    }

    // ----- colours, brushes, pens (cached) -----

    public static readonly IReadOnlyList<(string Key, string Name, Color Color)> PinColors =
    [
        ("red", "Red", Color.FromRgb(0xE5, 0x39, 0x35)),
        ("blue", "Blue", Color.FromRgb(0x1E, 0x88, 0xE5)),
        ("green", "Green", Color.FromRgb(0x43, 0xA0, 0x47)),
        ("yellow", "Yellow", Color.FromRgb(0xFD, 0xD8, 0x35)),
        ("orange", "Orange", Color.FromRgb(0xFB, 0x8C, 0x00)),
        ("white", "White", Color.FromRgb(0xF5, 0xF5, 0xF2)),
        ("black", "Black", Color.FromRgb(0x37, 0x47, 0x4F))
    ];

    public static readonly IReadOnlyList<(string Key, string Name, Color Color)> StringColors =
    [
        ("", "Red yarn", Color.FromRgb(0xC6, 0x28, 0x28)),
        ("blue", "Blue", Color.FromRgb(0x15, 0x65, 0xC0)),
        ("green", "Green", Color.FromRgb(0x2E, 0x7D, 0x32)),
        ("yellow", "Yellow", Color.FromRgb(0xF9, 0xA8, 0x25)),
        ("black", "Black", Color.FromRgb(0x26, 0x32, 0x38)),
        ("white", "White", Color.FromRgb(0xEC, 0xEF, 0xF1))
    ];

    public static Color PinColor(StickyNote s)
    {
        var match = PinColors.FirstOrDefault(p => p.Key == s.Pin);
        if (match.Key != null) return match.Color;
        int index = (int)((uint)(s.Id * 2654435761u) % 5); // stable "random" pick among the bright colours
        return PinColors[index].Color;
    }

    public static Color RopeColor(string key)
    {
        var match = StringColors.FirstOrDefault(c => c.Key == key);
        return match.Name != null ? match.Color : StringColors[0].Color;
    }

    private Color CurveColor(string key)
    {
        if (string.IsNullOrEmpty(key)) return Accent();
        var option = Palettes.Tags.FirstOrDefault(t => t.Key == key);
        if (option != null && ColorUtil.TryParse(option.Resolve(ThemeState.Instance.IsDark), out var c)) return c;
        return Accent();
    }

    private Color Accent() => TryFindResource("AccentColor") is Color c ? c : Color.FromRgb(0x0F, 0x94, 0x88);

    private static readonly Dictionary<Color, Brush> HeadBrushes = new();
    private static readonly Dictionary<Color, Pen> HeadPens = new();
    private static readonly Dictionary<Color, Pen> YarnPens = new();
    private static readonly Dictionary<Color, Pen> YarnHighlightPens = new();

    private static readonly Brush PinShadowOuter = Frozen(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)));
    private static readonly Brush PinShadowInner = Frozen(new SolidColorBrush(Color.FromArgb(70, 0, 0, 0)));
    private static readonly Brush Specular = Frozen(new SolidColorBrush(Color.FromArgb(185, 255, 255, 255)));
    private static readonly Pen RopeShadowPen = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(55, 0, 0, 0)), 2.8) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });

    private static Brush HeadBrush(Color c)
    {
        if (HeadBrushes.TryGetValue(c, out var b)) return b;
        var brush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.32, 0.28),
            Center = new Point(0.42, 0.38),
            RadiusX = 0.72,
            RadiusY = 0.72,
            GradientStops =
            {
                new GradientStop(ColorUtil.Blend(c, Colors.White, 0.45), 0),
                new GradientStop(c, 0.55),
                new GradientStop(ColorUtil.Blend(c, Colors.Black, 0.3), 1)
            }
        };
        brush.Freeze();
        return HeadBrushes[c] = brush;
    }

    private static Pen HeadPen(Color c)
    {
        if (HeadPens.TryGetValue(c, out var p)) return p;
        return HeadPens[c] = Frozen(new Pen(new SolidColorBrush(ColorUtil.Blend(c, Colors.Black, 0.45)), 0.7));
    }

    private static Pen YarnPen(Color c)
    {
        if (YarnPens.TryGetValue(c, out var p)) return p;
        return YarnPens[c] = Frozen(new Pen(new SolidColorBrush(c), 2.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round });
    }

    private static Pen YarnHighlightPen(Color c)
    {
        if (YarnHighlightPens.TryGetValue(c, out var p)) return p;
        var light = ColorUtil.Blend(c, Colors.White, 0.5);
        return YarnHighlightPens[c] = Frozen(new Pen(new SolidColorBrush(Color.FromArgb(200, light.R, light.G, light.B)), 0.9)
        {
            DashStyle = new DashStyle([1.3, 2.1], 0),
            DashCap = PenLineCap.Round
        });
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    protected override Size MeasureOverride(Size availableSize) => new(0, 0);
}
