using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Satchel.Converters;
using Satchel.Services;

namespace Satchel.Controls;

/// <summary>Round/rounded avatar: image if available, otherwise an icon or initials on a tinted background.</summary>
public sealed class Avatar : Border
{
    private readonly TextBlock _text = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center,
        FontWeight = FontWeights.SemiBold
    };

    // Typed as object: XAML templates can't bind to array-typed dependency properties.
    public static readonly DependencyProperty ImageProperty = DependencyProperty.Register(
        nameof(Image), typeof(object), typeof(Avatar), new PropertyMetadata(null, (d, _) => ((Avatar)d).Refresh()));
    public object? Image { get => GetValue(ImageProperty); set => SetValue(ImageProperty, value); }

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(Avatar), new PropertyMetadata(null, (d, _) => ((Avatar)d).Refresh()));
    public string? Text { get => (string?)GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph), typeof(string), typeof(Avatar), new PropertyMetadata(null, (d, _) => ((Avatar)d).Refresh()));
    public string? Glyph { get => (string?)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public static readonly DependencyProperty ColorKeyProperty = DependencyProperty.Register(
        nameof(ColorKey), typeof(string), typeof(Avatar), new PropertyMetadata("teal", (d, _) => ((Avatar)d).Refresh()));
    public string? ColorKey { get => (string?)GetValue(ColorKeyProperty); set => SetValue(ColorKeyProperty, value); }

    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(Avatar), new PropertyMetadata(40.0, (d, _) => ((Avatar)d).Refresh()));
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    /// <summary>-1 = circle, otherwise a corner radius as a fraction of the size (0..0.5).</summary>
    public static readonly DependencyProperty RoundnessProperty = DependencyProperty.Register(
        nameof(Roundness), typeof(double), typeof(Avatar), new PropertyMetadata(0.28, (d, _) => ((Avatar)d).Refresh()));
    public double Roundness { get => (double)GetValue(RoundnessProperty); set => SetValue(RoundnessProperty, value); }

    /// <summary>Use a solid colored background (white glyph) instead of the subtle tint.</summary>
    public static readonly DependencyProperty SolidProperty = DependencyProperty.Register(
        nameof(Solid), typeof(bool), typeof(Avatar), new PropertyMetadata(false, (d, _) => ((Avatar)d).Refresh()));
    public bool Solid { get => (bool)GetValue(SolidProperty); set => SetValue(SolidProperty, value); }

    public Avatar()
    {
        Child = _text;
        SnapsToDevicePixels = true;
        Refresh();
    }

    private void Refresh()
    {
        double size = Size;
        Width = size;
        Height = size;
        CornerRadius = new CornerRadius(Roundness < 0 ? size / 2 : size * Math.Clamp(Roundness, 0, 0.5));

        var image = BytesToImageConverter.ToImage(Image as byte[]);
        if (image != null)
        {
            Background = new ImageBrush(image) { Stretch = Stretch.UniformToFill };
            _text.Visibility = Visibility.Collapsed;
            return;
        }

        _text.Visibility = Visibility.Visible;
        if (Solid)
        {
            Background = ThemeBrushes.Tag(ColorKey);
            _text.Foreground = Brushes.White;
        }
        else
        {
            Background = ThemeBrushes.TagSubtle(ColorKey);
            _text.Foreground = ThemeBrushes.Tag(ColorKey);
        }

        if (!string.IsNullOrEmpty(Glyph))
        {
            _text.Text = Glyph;
            _text.FontFamily = (FontFamily)Application.Current.FindResource("IconFont");
            _text.FontSize = Math.Max(10, size * 0.44);
            _text.FontWeight = FontWeights.Normal;
        }
        else
        {
            _text.Text = InitialsConverter.Initials(Text);
            _text.SetResourceReference(TextBlock.FontFamilyProperty, "UiFont");
            _text.FontSize = Math.Max(9, size * 0.38);
            _text.FontWeight = FontWeights.SemiBold;
        }
    }
}
