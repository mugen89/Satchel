using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Satchel.Core;

namespace Satchel.Controls;

/// <summary>Indeterminate busy indicator: a rotating accent arc (static when animations are off).</summary>
public sealed class Spinner : Grid
{
    private readonly Ellipse _track = new();
    private readonly Ellipse _arc = new() { StrokeDashCap = PenLineCap.Round, RenderTransformOrigin = new Point(0.5, 0.5) };
    private readonly RotateTransform _rotate = new();

    public Spinner()
    {
        _track.SetResourceReference(Shape.StrokeProperty, "StrokeBrush");
        _arc.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        _arc.RenderTransform = _rotate;
        Children.Add(_track);
        Children.Add(_arc);
        SizeChanged += (_, _) => Layout();
        IsVisibleChanged += (_, _) => Animate();
        Loaded += (_, _) => Animate();
        Unloaded += (_, _) => _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }

    private void Layout()
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0) return;
        double thickness = Math.Max(2, size / 9);
        _track.StrokeThickness = _arc.StrokeThickness = thickness;
        double circumference = Math.PI * (size - thickness) / thickness;
        _arc.StrokeDashArray = [circumference * 0.28, circumference];
    }

    private void Animate()
    {
        if (IsVisible && Anim.Enabled)
            _rotate.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9))) { RepeatBehavior = RepeatBehavior.Forever });
        else
            _rotate.BeginAnimation(RotateTransform.AngleProperty, null);
    }
}
