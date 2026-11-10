using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Satchel.Core;

namespace Satchel.Controls;

/// <summary>
/// Animated vector weather icon (no image files): the sun turns slowly, clouds drift, rain and snow fall.
/// Kinds: clear, night, partly, partly-night, cloudy, fog, drizzle, rain, snow, storm.
/// </summary>
public sealed class WeatherIcon : Viewbox
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(
        nameof(Kind), typeof(string), typeof(WeatherIcon), new PropertyMetadata("clear", (d, _) => ((WeatherIcon)d).Build()));
    public string? Kind { get => (string?)GetValue(KindProperty); set => SetValue(KindProperty, value); }

    private static readonly Brush Sun = Frozen("#F5B342");
    private static readonly Brush SunRay = Frozen("#F7C25F");
    private static readonly Brush Moon = Frozen("#E8D9A6");
    private static readonly Brush CloudLight = Frozen("#E7EBF0");
    private static readonly Brush CloudMid = Frozen("#C5CDD8");
    private static readonly Brush CloudDark = Frozen("#8E99A8");
    private static readonly Brush Rain = Frozen("#4FA3E0");
    private static readonly Brush Snow = Frozen("#DCEBFA");
    private static readonly Brush Bolt = Frozen("#F2A73B");

    private readonly Canvas _canvas = new() { Width = 48, Height = 48 };

    public WeatherIcon()
    {
        Stretch = Stretch.Uniform;
        Child = _canvas;
        Build();
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private void Build()
    {
        _canvas.Children.Clear();
        var kind = Kind ?? "clear";
        switch (kind)
        {
            case "clear":
                AddSun(24, 24, 9, rays: true);
                break;
            case "night":
                AddMoon(25, 23, 11);
                break;
            case "partly":
                AddSun(18, 17, 7, rays: true);
                AddCloud(26, 30, 1.0, CloudLight, drift: true);
                break;
            case "partly-night":
                AddMoon(18, 16, 8);
                AddCloud(26, 30, 1.0, CloudLight, drift: true);
                break;
            case "fog":
                AddCloud(24, 22, 1.0, CloudMid, drift: true);
                for (int i = 0; i < 3; i++) AddLine(10 + i * 3, 33 + i * 4.5, 38 - i * 2, 33 + i * 4.5, CloudMid, 2.2);
                break;
            case "drizzle":
            case "rain":
                AddCloud(24, 21, 1.0, kind == "rain" ? CloudMid : CloudLight, drift: false);
                for (int i = 0; i < (kind == "rain" ? 4 : 3); i++) AddDrop(15 + i * 6.5, 32, i * 0.22, kind == "rain");
                break;
            case "snow":
                AddCloud(24, 21, 1.0, CloudLight, drift: false);
                for (int i = 0; i < 4; i++) AddFlake(14 + i * 6.8, 32, i * 0.35);
                break;
            case "storm":
                AddCloud(24, 20, 1.0, CloudDark, drift: false);
                AddBolt();
                AddDrop(14, 31, 0.1, true);
                AddDrop(34, 31, 0.4, true);
                break;
            default:
                AddCloud(20, 20, 0.8, CloudMid, drift: false);
                AddCloud(27, 28, 1.0, CloudLight, drift: true);
                break;
        }
    }

    private void AddSun(double cx, double cy, double r, bool rays)
    {
        if (rays)
        {
            var group = new Canvas { Width = 48, Height = 48 };
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                group.Children.Add(new Line
                {
                    X1 = cx + Math.Cos(a) * (r + 3), Y1 = cy + Math.Sin(a) * (r + 3), X2 = cx + Math.Cos(a) * (r + 7.5), Y2 = cy + Math.Sin(a) * (r + 7.5),
                    Stroke = SunRay, StrokeThickness = 2.4, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
                });
            }
            var rotate = new RotateTransform(0, cx, cy);
            group.RenderTransform = rotate;
            _canvas.Children.Add(group);
            if (Anim.Enabled) rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(30)) { RepeatBehavior = RepeatBehavior.Forever });
        }
        var disc = new Ellipse { Width = r * 2, Height = r * 2, Fill = Sun };
        Canvas.SetLeft(disc, cx - r);
        Canvas.SetTop(disc, cy - r);
        _canvas.Children.Add(disc);
    }

    private void AddMoon(double cx, double cy, double r)
    {
        var moon = new Path
        {
            Fill = Moon,
            Data = new CombinedGeometry(GeometryCombineMode.Exclude, new EllipseGeometry(new Point(cx, cy), r, r), new EllipseGeometry(new Point(cx + r * 0.55, cy - r * 0.45), r * 0.85, r * 0.85))
        };
        _canvas.Children.Add(moon);
    }

    private void AddCloud(double cx, double cy, double scale, Brush fill, bool drift)
    {
        var g = new GeometryGroup { FillRule = FillRule.Nonzero };
        g.Children.Add(new EllipseGeometry(new Point(cx - 8 * scale, cy + 1 * scale), 7 * scale, 7 * scale));
        g.Children.Add(new EllipseGeometry(new Point(cx + 1 * scale, cy - 3 * scale), 9.5 * scale, 9.5 * scale));
        g.Children.Add(new EllipseGeometry(new Point(cx + 9.5 * scale, cy + 2 * scale), 6.5 * scale, 6.5 * scale));
        g.Children.Add(new RectangleGeometry(new Rect(cx - 14 * scale, cy + 1 * scale, 29 * scale, 7.5 * scale), 3.5 * scale, 3.5 * scale));
        var cloud = new Path { Fill = fill, Data = g, Stroke = Frozen("#22000000"), StrokeThickness = 0.6 };
        if (drift && Anim.Enabled)
        {
            var shift = new TranslateTransform();
            cloud.RenderTransform = shift;
            shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-1.6, 1.6, TimeSpan.FromSeconds(3.2))
            {
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
        }
        _canvas.Children.Add(cloud);
    }

    private void AddLine(double x1, double y1, double x2, double y2, Brush brush, double thickness) =>
        _canvas.Children.Add(new Line { X1 = x1, Y1 = y1, X2 = x2, Y2 = y2, Stroke = brush, StrokeThickness = thickness, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });

    private void AddDrop(double x, double y, double delay, bool heavy)
    {
        var drop = new Line { X1 = x, Y1 = y, X2 = x - 1.6, Y2 = y + (heavy ? 6 : 4), Stroke = Rain, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
        _canvas.Children.Add(drop);
        Fall(drop, delay, 7, 1.0);
    }

    private void AddFlake(double x, double y, double delay)
    {
        var flake = new Ellipse { Width = 3.6, Height = 3.6, Fill = Snow, Stroke = Frozen("#559DB8D6"), StrokeThickness = 0.6 };
        Canvas.SetLeft(flake, x);
        Canvas.SetTop(flake, y);
        _canvas.Children.Add(flake);
        Fall(flake, delay, 8, 1.8);
    }

    private static void Fall(UIElement element, double delay, double distance, double seconds)
    {
        if (!Anim.Enabled) return;
        var shift = new TranslateTransform();
        element.RenderTransform = shift;
        var begin = TimeSpan.FromSeconds(delay);
        shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, distance, TimeSpan.FromSeconds(seconds)) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever });
        element.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromSeconds(seconds)) { BeginTime = begin, RepeatBehavior = RepeatBehavior.Forever });
    }

    private void AddBolt()
    {
        var bolt = new Path
        {
            Fill = Bolt,
            Data = Geometry.Parse("M 25,27 L 19,37 L 24,37 L 21,45 L 30,33 L 25,33 L 28,27 Z")
        };
        _canvas.Children.Add(bolt);
        if (Anim.Enabled)
            bolt.BeginAnimation(OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(3.4), RepeatBehavior = RepeatBehavior.Forever,
                KeyFrames = { new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0)), new DiscreteDoubleKeyFrame(0.25, KeyTime.FromPercent(0.9)), new DiscreteDoubleKeyFrame(1, KeyTime.FromPercent(0.95)) }
            });
    }
}
