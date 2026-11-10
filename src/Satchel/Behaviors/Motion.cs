using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Satchel.Core;

namespace Satchel.Behaviors;

/// <summary>Declarative entrance / hover motion for any element.</summary>
public static class Motion
{
    // Fade + slide up when the element is loaded.
    public static readonly DependencyProperty EnterProperty = DependencyProperty.RegisterAttached(
        "Enter", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnEnterChanged));
    public static bool GetEnter(DependencyObject o) => (bool)o.GetValue(EnterProperty);
    public static void SetEnter(DependencyObject o, bool v) => o.SetValue(EnterProperty, v);

    public static readonly DependencyProperty DelayProperty = DependencyProperty.RegisterAttached(
        "Delay", typeof(double), typeof(Motion), new PropertyMetadata(0.0));
    public static double GetDelay(DependencyObject o) => (double)o.GetValue(DelayProperty);
    public static void SetDelay(DependencyObject o, double v) => o.SetValue(DelayProperty, v);

    public static readonly DependencyProperty DistanceProperty = DependencyProperty.RegisterAttached(
        "Distance", typeof(double), typeof(Motion), new PropertyMetadata(14.0));
    public static double GetDistance(DependencyObject o) => (double)o.GetValue(DistanceProperty);
    public static void SetDistance(DependencyObject o, double v) => o.SetValue(DistanceProperty, v);

    private static void OnEnterChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is not true) return;
        fe.Loaded += (_, _) => Anim.EnterUp(fe, GetDistance(fe), 320, GetDelay(fe));
    }

    // Lift the element slightly on hover (cards).
    public static readonly DependencyProperty LiftProperty = DependencyProperty.RegisterAttached(
        "Lift", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnLiftChanged));
    public static bool GetLift(DependencyObject o) => (bool)o.GetValue(LiftProperty);
    public static void SetLift(DependencyObject o, bool v) => o.SetValue(LiftProperty, v);

    private static void OnLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is not true) return;
        fe.MouseEnter += (_, _) => LiftTo(fe, -3);
        fe.MouseLeave += (_, _) => LiftTo(fe, 0);
    }

    private static void LiftTo(FrameworkElement fe, double y)
    {
        if (!Anim.Enabled) return;
        if (fe.RenderTransform is not TranslateTransform tt || tt.IsFrozen)
        {
            tt = new TranslateTransform();
            fe.RenderTransform = tt;
        }
        tt.BeginAnimation(TranslateTransform.YProperty, Anim.To(y, 180, Anim.EaseOut));
    }

    // Continuous gentle wiggle (dashboard customize mode).
    public static readonly DependencyProperty WiggleProperty = DependencyProperty.RegisterAttached(
        "Wiggle", typeof(bool), typeof(Motion), new PropertyMetadata(false, OnWiggleChanged));
    public static bool GetWiggle(DependencyObject o) => (bool)o.GetValue(WiggleProperty);
    public static void SetWiggle(DependencyObject o, bool v) => o.SetValue(WiggleProperty, v);

    private static readonly DependencyProperty WiggleRotateProperty = DependencyProperty.RegisterAttached(
        "WiggleRotate", typeof(RotateTransform), typeof(Motion), new PropertyMetadata(null));

    private static readonly DependencyProperty WiggleClockProperty = DependencyProperty.RegisterAttached(
        "WiggleClock", typeof(AnimationClock), typeof(Motion), new PropertyMetadata(null));

    private static void OnWiggleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        fe.Loaded -= OnWiggleHostLoaded;
        fe.Unloaded -= OnWiggleHostUnloaded;
        if (e.NewValue is true)
        {
            fe.Loaded += OnWiggleHostLoaded;
            fe.Unloaded += OnWiggleHostUnloaded;
            if (fe.IsLoaded) StartWiggle(fe);
        }
        else
        {
            StopWiggle(fe);
        }
    }

    private static void OnWiggleHostLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && GetWiggle(fe)) StartWiggle(fe);
    }

    private static void OnWiggleHostUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) StopWiggle(fe);
    }

    /// <summary>
    /// The element is drawn once into a bitmap and only that picture is tilted, so dozens of cards (with shadows, text
    /// and images) wiggle smoothly. Every card moves its corners by about the same distance, so big cards tilt less
    /// than small ones, and each starts at a random point of its swing so they don't move in step.
    /// </summary>
    private static void StartWiggle(FrameworkElement fe)
    {
        if (!Anim.Enabled || fe.GetValue(WiggleClockProperty) != null) return;
        if (fe.GetValue(WiggleRotateProperty) is not RotateTransform rotate)
        {
            rotate = new RotateTransform();
            fe.SetValue(WiggleRotateProperty, rotate);
        }
        fe.RenderTransformOrigin = new Point(0.5, 0.5);
        fe.RenderTransform = rotate;
        fe.CacheMode = Anim.SharpCache(fe);

        double halfDiagonal = Math.Max(60, Math.Sqrt(fe.ActualWidth * fe.ActualWidth + fe.ActualHeight * fe.ActualHeight) / 2);
        double angle = Math.Clamp(1.4 / halfDiagonal * 180 / Math.PI, 0.15, 0.7);
        if (Random.Shared.Next(2) == 0) angle = -angle;
        int half = 210 + Random.Shared.Next(70);
        var swing = new DoubleAnimation(-angle, angle, TimeSpan.FromMilliseconds(half))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = Anim.EaseInOut
        };
        var clock = swing.CreateClock();
        rotate.ApplyAnimationClock(RotateTransform.AngleProperty, clock);
        clock.Controller?.Seek(TimeSpan.FromMilliseconds(Random.Shared.Next(half * 2)), TimeSeekOrigin.BeginTime);
        fe.SetValue(WiggleClockProperty, clock);
    }

    private static void StopWiggle(FrameworkElement fe)
    {
        if (fe.GetValue(WiggleClockProperty) is AnimationClock clock)
        {
            clock.Controller?.Stop();
            fe.ClearValue(WiggleClockProperty);
        }
        if (fe.GetValue(WiggleRotateProperty) is RotateTransform rotate)
        {
            rotate.ApplyAnimationClock(RotateTransform.AngleProperty, null);
            rotate.Angle = 0;
        }
        if (fe.CacheMode is BitmapCache) fe.CacheMode = null;
    }
}
