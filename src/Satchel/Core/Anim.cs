using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Satchel.Core;

/// <summary>Small animation toolkit. Respects the "animations" setting globally.</summary>
public static class Anim
{
    public static bool Enabled { get; set; } = true;

    public static readonly IEasingFunction EaseOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseOutQuint = Freeze(new QuinticEase { EasingMode = EasingMode.EaseOut });
    public static readonly IEasingFunction EaseInOut = Freeze(new CubicEase { EasingMode = EasingMode.EaseInOut });
    public static readonly IEasingFunction Back = Freeze(new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.35 });

    private static IEasingFunction Freeze(EasingFunctionBase e) { e.Freeze(); return e; }

    public static Duration Ms(double ms) => new(TimeSpan.FromMilliseconds(Enabled ? ms : 0));

    public static DoubleAnimation To(double to, double ms, IEasingFunction? ease = null, double? from = null, double delayMs = 0)
    {
        var a = new DoubleAnimation
        {
            To = to,
            Duration = Ms(ms),
            EasingFunction = ease ?? EaseOut,
            BeginTime = TimeSpan.FromMilliseconds(Enabled ? delayMs : 0)
        };
        if (from.HasValue) a.From = from.Value;
        return a;
    }

    /// <summary>
    /// A bitmap cache sharp enough for the element's real on-screen size (interface size and display scaling included).
    /// While cached, moving or rotating the element only re-composites a picture instead of redrawing its text,
    /// shadows and images every frame.
    /// </summary>
    public static BitmapCache SharpCache(Visual element)
    {
        double scale = 1;
        try
        {
            if (PresentationSource.FromVisual(element)?.RootVisual is Visual root && root != element && element.IsDescendantOf(root))
            {
                var m = element.TransformToAncestor(root) is Transform t ? t.Value : Matrix.Identity;
                scale = Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12);
            }
            scale *= VisualTreeHelper.GetDpi(element).DpiScaleX;
        }
        catch (InvalidOperationException)
        {
            // not in a window yet
        }
        return new BitmapCache { RenderAtScale = Math.Clamp(scale, 1, 3), EnableClearType = false, SnapsToDevicePixels = false };
    }

    public static TranslateTransform EnsureTranslate(UIElement element)
    {
        switch (element.RenderTransform)
        {
            case TranslateTransform t when !t.IsFrozen:
                return t;
            case TransformGroup g when !g.IsFrozen:
                var existing = g.Children.OfType<TranslateTransform>().FirstOrDefault();
                if (existing != null) return existing;
                var added = new TranslateTransform();
                g.Children.Add(added);
                return added;
            default:
                var tt = new TranslateTransform();
                element.RenderTransform = tt;
                return tt;
        }
    }

    /// <summary>Fade + slide in from below.</summary>
    public static void EnterUp(UIElement element, double distance = 12, double ms = 280, double delayMs = 0)
    {
        if (!Enabled) { element.Opacity = 1; return; }
        var tt = EnsureTranslate(element);
        element.Opacity = 0;
        element.BeginAnimation(UIElement.OpacityProperty, To(1, ms, EaseOut, 0, delayMs));
        tt.BeginAnimation(TranslateTransform.YProperty, To(0, ms + 60, EaseOutQuint, distance, delayMs));
    }

    public static void FadeIn(UIElement element, double ms = 200)
    {
        if (!Enabled) { element.Opacity = 1; return; }
        element.BeginAnimation(UIElement.OpacityProperty, To(1, ms, EaseOut, 0));
    }

    public static void Shake(UIElement element)
    {
        if (!Enabled) return;
        var tt = EnsureTranslate(element);
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        double[] offsets = [0, -10, 9, -7, 5, -3, 1, 0];
        for (int i = 0; i < offsets.Length; i++)
            a.KeyFrames.Add(new EasingDoubleKeyFrame(offsets[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(i * 52))));
        tt.BeginAnimation(TranslateTransform.XProperty, a);
    }

    public static void Pop(UIElement element, double scale = 1.12)
    {
        if (!Enabled) return;
        var st = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = st;
        var a = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(260) };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(scale, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(110)), EaseOut));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(260)), EaseOut));
        st.BeginAnimation(ScaleTransform.ScaleXProperty, a);
        st.BeginAnimation(ScaleTransform.ScaleYProperty, a);
    }
}
