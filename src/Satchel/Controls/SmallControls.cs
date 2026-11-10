using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Satchel.Core;
using Satchel.Services;

namespace Satchel.Controls;

/// <summary>Shows a password with digits and symbols tinted, like most password managers.</summary>
public sealed class ColoredPasswordText : TextBlock
{
    public static readonly DependencyProperty PasswordProperty = DependencyProperty.Register(
        nameof(Password), typeof(string), typeof(ColoredPasswordText), new PropertyMetadata("", (d, _) => ((ColoredPasswordText)d).Rebuild()));
    public string Password { get => (string)GetValue(PasswordProperty); set => SetValue(PasswordProperty, value); }

    private void Rebuild()
    {
        Inlines.Clear();
        var text = Password ?? "";
        int i = 0;
        while (i < text.Length)
        {
            int kind = Kind(text[i]);
            int start = i;
            while (i < text.Length && Kind(text[i]) == kind) i++;
            var run = new Run(text[start..i]);
            run.SetResourceReference(TextElement.ForegroundProperty, kind switch
            {
                1 => "AccentTextBrush",
                2 => "WarningBrush",
                _ => "TextBrush"
            });
            Inlines.Add(run);
        }
    }

    private static int Kind(char c) => char.IsDigit(c) ? 1 : char.IsLetter(c) ? 0 : 2;
}

public sealed class SwatchItem : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    private bool _isSelected;
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required Brush Brush { get; init; }
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

/// <summary>Row of round color swatches bound to a palette key.</summary>
public sealed class ColorSwatches : ItemsControl
{
    public static readonly DependencyProperty SelectedKeyProperty = DependencyProperty.Register(
        nameof(SelectedKey), typeof(string), typeof(ColorSwatches),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((ColorSwatches)d).SyncSelection()));
    public string? SelectedKey { get => (string?)GetValue(SelectedKeyProperty); set => SetValue(SelectedKeyProperty, value); }

    /// <summary>"tag" (default), "note", "sticky" or "accent".</summary>
    public static readonly DependencyProperty PaletteProperty = DependencyProperty.Register(
        nameof(Palette), typeof(string), typeof(ColorSwatches), new PropertyMetadata("tag", (d, _) => ((ColorSwatches)d).Build()));
    public string Palette { get => (string)GetValue(PaletteProperty); set => SetValue(PaletteProperty, value); }

    public static readonly DependencyProperty SwatchSizeProperty = DependencyProperty.Register(
        nameof(SwatchSize), typeof(double), typeof(ColorSwatches), new PropertyMetadata(24.0));
    public double SwatchSize { get => (double)GetValue(SwatchSizeProperty); set => SetValue(SwatchSizeProperty, value); }

    private readonly List<SwatchItem> _items = [];

    public ColorSwatches()
    {
        ItemsPanel = new ItemsPanelTemplate(new FrameworkElementFactory(typeof(WrapPanel)));
        ItemTemplate = BuildTemplate();
        Build();
    }

    private void Build()
    {
        _items.Clear();
        IEnumerable<ColorOption> options = Palette switch
        {
            "note" => Palettes.Notes,
            "sticky" => Palettes.Stickies,
            "accent" => Palettes.Accents,
            _ => Palettes.Tags
        };
        foreach (var o in options)
        {
            Brush brush = Palette switch
            {
                "note" => ThemeBrushes.Note(o.Key),
                "sticky" => ThemeBrushes.Sticky(o.Key),
                "accent" => ColorUtil.Brush(o.Resolve(ThemeState.Instance.IsDark)),
                _ => ThemeBrushes.Tag(o.Key)
            };
            _items.Add(new SwatchItem { Key = o.Key, Name = o.Name, Brush = brush });
        }
        ItemsSource = _items.ToList();
        SyncSelection();
    }

    private void SyncSelection()
    {
        foreach (var i in _items) i.IsSelected = string.Equals(i.Key, SelectedKey, StringComparison.OrdinalIgnoreCase);
    }

    private DataTemplate BuildTemplate()
    {
        var template = new DataTemplate(typeof(SwatchItem));
        var grid = new FrameworkElementFactory(typeof(ColorSwatchView));
        template.VisualTree = grid;
        return template;
    }

    internal void Select(string key)
    {
        SelectedKey = key;
        SyncSelection();
    }

}

public sealed class ColorSwatchView : Grid
{
    private readonly Ellipse _fill = new();
    private readonly Ellipse _ring = new() { StrokeThickness = 2, Opacity = 0 };
    private readonly TextBlock _check = new()
    {
        Text = Glyphs.Check,
        FontSize = 11,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        Opacity = 0
    };

    public ColorSwatchView()
    {
        Margin = new Thickness(0, 0, 6, 6);
        Cursor = Cursors.Hand;
        Background = Brushes.Transparent;
        _check.FontFamily = (FontFamily)Application.Current.FindResource("IconFont");
        _fill.SetResourceReference(Shape.StrokeProperty, "StrokeStrongBrush");
        _fill.StrokeThickness = 1;
        _ring.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        Children.Add(_fill);
        Children.Add(_ring);
        Children.Add(_check);
        DataContextChanged += (_, _) => Bind();
        Loaded += (_, _) => Bind();
        MouseLeftButtonUp += (_, _) =>
        {
            if (DataContext is SwatchItem item && FindOwner() is { } owner)
            {
                owner.Select(item.Key);
                Anim.Pop(this, 1.15);
            }
        };
    }

    private ColorSwatches? FindOwner()
    {
        DependencyObject? d = this;
        while (d != null && d is not ColorSwatches) d = VisualTreeHelper.GetParent(d);
        return d as ColorSwatches;
    }

    private void Bind()
    {
        if (DataContext is not SwatchItem item) return;
        var size = FindOwner()?.SwatchSize ?? 24;
        Width = Height = size + 6;
        _fill.Width = _fill.Height = size;
        _ring.Width = _ring.Height = size + 6;
        _fill.Fill = item.Brush;
        ToolTip = item.Name;
        _check.Foreground = item.Key is "default" or "white" or "sand" ? (Brush)Application.Current.FindResource("TextBrush") : Brushes.White;
        if (item.Key == "default") _check.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        item.PropertyChanged -= OnItemChanged;
        item.PropertyChanged += OnItemChanged;
        Apply(item.IsSelected);
    }

    private void OnItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is SwatchItem item) Apply(item.IsSelected);
    }

    private void Apply(bool selected)
    {
        _ring.BeginAnimation(OpacityProperty, Anim.To(selected ? 1 : 0, 150));
        _check.BeginAnimation(OpacityProperty, Anim.To(selected ? 1 : 0, 150));
    }
}

/// <summary>Slowly drifting accent glows used behind the lock screen and dashboard header.</summary>
public sealed class AmbientBackground : Canvas
{
    private readonly List<Ellipse> _blobs = [];

    public AmbientBackground()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
        for (int i = 0; i < 3; i++)
        {
            var e = new Ellipse { Width = 520, Height = 520, RenderTransform = new TranslateTransform() };
            _blobs.Add(e);
            Children.Add(e);
        }
        ApplyColors();
        SizeChanged += (_, _) => Position();
        Loaded += (_, _) => { ThemeState.Instance.PropertyChanged += OnThemeChanged; ApplyColors(); Start(); };
        Unloaded += (_, _) => { ThemeState.Instance.PropertyChanged -= OnThemeChanged; Stop(); };
        IsVisibleChanged += (_, _) => { if (IsVisible) Start(); else Stop(); };
    }

    private void OnThemeChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => ApplyColors();

    private void ApplyColors()
    {
        for (int i = 0; i < _blobs.Count; i++)
        {
            var key = i == 1 ? "AccentGlow2Color" : "AccentGlowColor";
            var color = Application.Current.TryFindResource(key) is Color c ? c : Color.FromArgb(0x30, 0x2D, 0xD4, 0xBF);
            var brush = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B));
            brush.Freeze();
            _blobs[i].Fill = brush;
        }
    }

    private void Position()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0) return;
        SetLeft(_blobs[0], -160); SetTop(_blobs[0], -200);
        SetLeft(_blobs[1], w - 380); SetTop(_blobs[1], h * 0.25 - 200);
        SetLeft(_blobs[2], w * 0.35 - 260); SetTop(_blobs[2], h - 300);
    }

    private void Start()
    {
        Position();
        if (!Anim.Enabled) return;
        double[] dx = [140, -120, 90], dy = [90, 140, -110], secs = [17, 21, 25];
        for (int i = 0; i < _blobs.Count; i++)
        {
            var tt = (TranslateTransform)_blobs[i].RenderTransform;
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, dx[i], TimeSpan.FromSeconds(secs[i])) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Anim.EaseInOut });
            tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, dy[i], TimeSpan.FromSeconds(secs[i] * 0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = Anim.EaseInOut });
        }
    }

    private void Stop()
    {
        foreach (var b in _blobs)
        {
            var tt = (TranslateTransform)b.RenderTransform;
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.BeginAnimation(TranslateTransform.YProperty, null);
        }
    }
}

/// <summary>The Satchel logo mark drawn as vector paths.</summary>
public sealed class LogoMark : Grid
{
    public static readonly DependencyProperty SizeProperty = DependencyProperty.Register(
        nameof(Size), typeof(double), typeof(LogoMark), new PropertyMetadata(36.0, (d, e) => ((LogoMark)d).Apply()));
    public double Size { get => (double)GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    private readonly Border _tile = new();
    private readonly Viewbox _box = new();

    public LogoMark()
    {
        _tile.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
        var canvas = new Canvas { Width = 64, Height = 64 };
        var white = Brushes.White;
        // handle
        canvas.Children.Add(new Path
        {
            Data = Geometry.Parse("M 24,22 L 24,17 A 8,8 0 0 1 40,17 L 40,22"),
            Stroke = white, StrokeThickness = 4.2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
        });
        // body
        canvas.Children.Add(new Path { Data = Geometry.Parse("M 14,26 Q 14,21 19,21 L 45,21 Q 50,21 50,26 L 50,47 Q 50,52 45,52 L 19,52 Q 14,52 14,47 Z"), Fill = white });
        // flap line
        var flap = new Path { Data = Geometry.Parse("M 14,31 Q 32,39 50,31"), StrokeThickness = 3, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round };
        flap.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        canvas.Children.Add(flap);
        // clasp
        var clasp = new Rectangle { Width = 8, Height = 9, RadiusX = 2, RadiusY = 2 };
        clasp.SetResourceReference(Shape.FillProperty, "AccentBrush");
        Canvas.SetLeft(clasp, 28); Canvas.SetTop(clasp, 32);
        canvas.Children.Add(clasp);
        _box.Child = canvas;
        _tile.Child = _box;
        Children.Add(_tile);
        Apply();
    }

    private void Apply()
    {
        Width = Height = Size;
        _tile.CornerRadius = new CornerRadius(Size * 0.28);
        _box.Margin = new Thickness(Size * 0.12);
    }
}
