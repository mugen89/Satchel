using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Satchel.Core;

namespace Satchel.Controls;

/// <summary>Circular progress ring (dash-offset based, so it animates smoothly).</summary>
public sealed class ProgressRing : Grid
{
    private readonly Ellipse _track = new();
    private readonly Ellipse _arc = new() { StrokeDashCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5) };

    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(ProgressRing), new PropertyMetadata(0.0, (d, e) => ((ProgressRing)d).Animate((double)e.OldValue)));
    public double Progress { get => (double)GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(ProgressRing), new PropertyMetadata(4.0, (d, _) => ((ProgressRing)d).Layout()));
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    public static readonly DependencyProperty RingBrushProperty = DependencyProperty.Register(
        nameof(RingBrush), typeof(Brush), typeof(ProgressRing), new PropertyMetadata(null, (d, e) => ((ProgressRing)d)._arc.Stroke = (Brush?)e.NewValue));
    public Brush? RingBrush { get => (Brush?)GetValue(RingBrushProperty); set => SetValue(RingBrushProperty, value); }

    private double _circumferenceUnits = 1;

    public ProgressRing()
    {
        _track.SetResourceReference(Shape.StrokeProperty, "StrokeBrush");
        _arc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        _arc.RenderTransform = new RotateTransform(-90);
        Children.Add(_track);
        Children.Add(_arc);
        SizeChanged += (_, _) => Layout();
    }

    private void Layout()
    {
        double d = Math.Min(ActualWidth, ActualHeight);
        if (d <= 0) return;
        double t = Thickness;
        _track.StrokeThickness = t;
        _arc.StrokeThickness = t;
        _circumferenceUnits = Math.PI * (d - t) / t;
        _arc.StrokeDashArray = new DoubleCollection { _circumferenceUnits, _circumferenceUnits };
        _arc.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
        _arc.StrokeDashOffset = Offset(Progress);
    }

    private double Offset(double progress) => _circumferenceUnits * (1 - Math.Clamp(progress, 0, 1));

    private void Animate(double old)
    {
        if (!Anim.Enabled || !IsLoaded) { _arc.StrokeDashOffset = Offset(Progress); return; }
        _arc.BeginAnimation(Shape.StrokeDashOffsetProperty, new DoubleAnimation(Offset(old), Offset(Progress), Anim.Ms(500)) { EasingFunction = Anim.EaseOut });
    }

    /// <summary>Linear countdown from the given fraction to zero over the duration (TOTP).</summary>
    public void Countdown(double fromFraction, TimeSpan duration)
    {
        _arc.BeginAnimation(Shape.StrokeDashOffsetProperty,
            new DoubleAnimation(Offset(fromFraction), Offset(0), new Duration(duration)));
    }
}
