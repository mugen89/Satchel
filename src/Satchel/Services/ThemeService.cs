using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Effects;
using CommunityToolkit.Mvvm.ComponentModel;
using Satchel.Core;

namespace Satchel.Services;

public sealed record ThemeDefinition(
    string Key,
    string Name,
    bool IsDark,
    string Bg,
    string Sidebar,
    string Surface,
    string Input,
    string Stroke,
    string Text,
    string Muted,
    string DefaultAccent)
{
    public SolidColorBrush BgPreview => ColorUtil.Brush(Bg);
    public SolidColorBrush SidebarPreview => ColorUtil.Brush(Sidebar);
    public SolidColorBrush SurfacePreview => ColorUtil.Brush(Surface);
    public SolidColorBrush StrokePreview => ColorUtil.Brush(Stroke);
    public SolidColorBrush TextPreview => ColorUtil.Brush(Text);
    public SolidColorBrush MutedPreview => ColorUtil.Brush(Muted);
    public SolidColorBrush AccentPreview => ColorUtil.Brush(Palettes.FindAccent(DefaultAccent).Resolve(IsDark));
}

/// <summary>Observable theme state so bindings (e.g. note colors) can refresh when the theme changes.</summary>
public sealed partial class ThemeState : ObservableObject
{
    public static ThemeState Instance { get; } = new();
    [ObservableProperty] private bool _isDark;
    [ObservableProperty] private int _version;
}

public sealed class ThemeService
{
    public static readonly IReadOnlyList<ThemeDefinition> Themes =
    [
        new("daylight", "Daylight", false, "#F3F5F8", "#ECEFF3", "#FFFFFF", "#F5F7FA", "#E1E6EC", "#17202A", "#5C6773", "teal"),
        new("arctic", "Arctic", false, "#EEF3F8", "#E4EBF3", "#FFFFFF", "#F3F7FB", "#D8E1EB", "#132033", "#56667B", "sky"),
        new("sand", "Sand", false, "#F5F0E8", "#EDE6DB", "#FFFDF9", "#F8F3EB", "#E5DBCC", "#2A231C", "#786A5B", "terracotta"),
        new("mono", "Paper", false, "#F4F4F3", "#EDEDEB", "#FFFFFF", "#F6F6F5", "#E3E3E0", "#1C1C1A", "#66665F", "slate"),
        new("graphite", "Graphite", true, "#15181C", "#111316", "#1D2126", "#23282E", "#2D333A", "#E7EAEE", "#98A1AC", "teal"),
        new("nord", "Nord", true, "#232831", "#1E222A", "#2B313C", "#323946", "#3B4351", "#ECEFF4", "#A6B0C0", "frost"),
        new("forest", "Forest", true, "#0F1613", "#0B110E", "#16201B", "#1C2721", "#26332B", "#E2ECE5", "#8EA295", "emerald"),
        new("ocean", "Ocean", true, "#0C1420", "#09101A", "#121C2B", "#172335", "#213046", "#E1E9F4", "#8B9CB4", "sky"),
        new("midnight", "Midnight", true, "#000000", "#060606", "#0E0E0F", "#151517", "#222224", "#F0F0F0", "#9B9B9F", "amber"),
    ];

    public ThemeDefinition Current { get; private set; } = Themes[0];
    public bool IsDark => Current.IsDark;
    public Color Accent { get; private set; }

    public event Action? Changed;

    public static ThemeDefinition Find(string? key) => Themes.FirstOrDefault(t => t.Key == key) ?? Themes[0];

    public void Apply(AppSettings s)
    {
        var t = Find(s.Theme);
        Current = t;
        var r = Application.Current.Resources;
        bool dark = t.IsDark;

        Color bg = ColorUtil.Parse(t.Bg), sidebar = ColorUtil.Parse(t.Sidebar), surface = ColorUtil.Parse(t.Surface);
        Color input = ColorUtil.Parse(t.Input), stroke = ColorUtil.Parse(t.Stroke), text = ColorUtil.Parse(t.Text), muted = ColorUtil.Parse(t.Muted);

        Color accent = ResolveAccent(s.Accent, t);
        Accent = accent;
        Color elevated = dark ? ColorUtil.Blend(surface, Colors.White, 0.035) : surface;

        Set(r, "BgBrush", bg);
        Set(r, "SidebarBrush", sidebar);
        Set(r, "SurfaceBrush", surface);
        Set(r, "ElevatedBrush", elevated);
        Set(r, "InputBrush", input);
        Set(r, "SurfaceHoverBrush", ColorUtil.Blend(surface, text, dark ? 0.06 : 0.045));
        Set(r, "SurfacePressedBrush", ColorUtil.Blend(surface, text, dark ? 0.10 : 0.08));
        Set(r, "SidebarHoverBrush", ColorUtil.Blend(sidebar, text, dark ? 0.06 : 0.05));
        Set(r, "HoverOverlayBrush", ColorUtil.WithAlpha(text, dark ? 0.08 : 0.055));
        Set(r, "PressedOverlayBrush", ColorUtil.WithAlpha(text, dark ? 0.14 : 0.1));
        Set(r, "SegmentBrush", dark ? ColorUtil.Blend(input, text, 0.12) : surface);
        Set(r, "StrokeBrush", stroke);
        Set(r, "StrokeStrongBrush", ColorUtil.Blend(stroke, text, 0.2));
        Set(r, "TextBrush", text);
        Set(r, "TextMutedBrush", muted);
        Set(r, "TextFaintBrush", ColorUtil.Blend(muted, surface, 0.4));

        Set(r, "AccentBrush", accent);
        Set(r, "AccentHoverBrush", dark ? ColorUtil.Lighten(accent, 0.1) : ColorUtil.Darken(accent, 0.08));
        Set(r, "AccentPressedBrush", dark ? ColorUtil.Lighten(accent, 0.18) : ColorUtil.Darken(accent, 0.16));
        Set(r, "AccentSubtleBrush", ColorUtil.Blend(surface, accent, dark ? 0.16 : 0.10));
        Set(r, "AccentSubtleStrongBrush", ColorUtil.Blend(surface, accent, dark ? 0.26 : 0.18));
        Set(r, "AccentTextBrush", ReadableOn(accent, surface, dark));
        Set(r, "OnAccentBrush", ColorUtil.OnColor(accent));
        Set(r, "SelectionBrush", ColorUtil.WithAlpha(accent, 0.35));

        SetSemantic(r, "Success", dark ? "#3DD68C" : "#148F55", surface, dark);
        SetSemantic(r, "Warning", dark ? "#F5B941" : "#B86E00", surface, dark);
        SetSemantic(r, "Danger", dark ? "#FF6B6B" : "#D33A3A", surface, dark);
        SetSemantic(r, "Info", dark ? "#5AA9FF" : "#2B75D0", surface, dark);

        Set(r, "OverlayBrush", dark ? ColorUtil.WithAlpha(Colors.Black, 0.58) : ColorUtil.WithAlpha(ColorUtil.Parse("#0B1218"), 0.36));
        Set(r, "ScrollThumbBrush", ColorUtil.Blend(surface, text, dark ? 0.24 : 0.2));
        Set(r, "ScrollThumbHoverBrush", ColorUtil.Blend(surface, text, dark ? 0.4 : 0.36));

        r["AccentColor"] = accent;
        r["BgColor"] = bg;
        r["SurfaceColor"] = surface;
        var shadow = dark ? Colors.Black : ColorUtil.Parse("#1B2836");
        r["ShadowColor"] = shadow;
        r["AccentGlowColor"] = ColorUtil.WithAlpha(accent, dark ? 0.32 : 0.22);
        r["AccentGlow2Color"] = ColorUtil.WithAlpha(ColorUtil.Blend(accent, dark ? Colors.White : ColorUtil.Parse("#0EA5E9"), 0.35), dark ? 0.2 : 0.16);

        r["CardShadow"] = s.CardShadows ? Shadow(shadow, 16, 2, dark ? 0.32 : 0.06) : null;
        r["LiftShadow"] = Shadow(shadow, 26, 8, dark ? 0.5 : 0.15);
        r["PopupShadow"] = Shadow(shadow, 22, 6, dark ? 0.5 : 0.14);
        r["DialogShadow"] = Shadow(shadow, 44, 14, dark ? 0.6 : 0.22);

        double radius = Math.Clamp(s.CornerRadius, 0, 24);
        r["RadiusSmall"] = new CornerRadius(Math.Round(radius * 0.7));
        r["Radius"] = new CornerRadius(radius);
        r["RadiusLarge"] = new CornerRadius(Math.Round(radius * 1.4));
        r["RadiusTiny"] = new CornerRadius(Math.Round(radius * 0.4));
        r["RadiusValue"] = radius;

        try { r["UiFont"] = new FontFamily(string.IsNullOrWhiteSpace(s.FontFamily) ? "Segoe UI" : s.FontFamily); }
        catch { r["UiFont"] = new FontFamily("Segoe UI"); }

        Anim.Enabled = s.AnimationsEnabled;
        ThemeBrushes.Refresh(dark, surface);
        ThemeState.Instance.IsDark = dark;
        ThemeState.Instance.Version++;
        Changed?.Invoke();
    }

    public static Color ResolveAccent(string? accent, ThemeDefinition t)
    {
        if (!string.IsNullOrWhiteSpace(accent) && accent.StartsWith('#') && ColorUtil.TryParse(accent, out var custom))
            return custom;
        var option = Palettes.FindAccent(string.IsNullOrWhiteSpace(accent) ? t.DefaultAccent : accent);
        return ColorUtil.Parse(option.Resolve(t.IsDark));
    }

    /// <summary>Background for a note card color key in the current theme (null = default surface).</summary>
    public Brush? NoteBrush(string? key)
    {
        if (string.IsNullOrEmpty(key) || key == "default") return null;
        var option = Palettes.Notes.FirstOrDefault(n => n.Key == key);
        return option == null ? null : ColorUtil.Brush(option.Resolve(IsDark));
    }

    private static Color ReadableOn(Color color, Color surface, bool dark)
    {
        var c = color;
        for (int i = 0; i < 8 && ColorUtil.Contrast(c, surface) < 4.2; i++)
            c = dark ? ColorUtil.Lighten(c, 0.12) : ColorUtil.Darken(c, 0.1);
        return c;
    }

    private static void SetSemantic(ResourceDictionary r, string name, string hex, Color surface, bool dark)
    {
        var c = ColorUtil.Parse(hex);
        Set(r, name + "Brush", c);
        Set(r, name + "SubtleBrush", ColorUtil.Blend(surface, c, dark ? 0.16 : 0.11));
        r[name + "Color"] = c;
    }

    private static void Set(ResourceDictionary r, string key, Color c) => r[key] = ColorUtil.Brush(c);

    private static DropShadowEffect Shadow(Color color, double blur, double depth, double opacity)
    {
        var e = new DropShadowEffect
        {
            Color = color,
            BlurRadius = blur,
            ShadowDepth = depth,
            Direction = 270,
            Opacity = opacity,
            RenderingBias = RenderingBias.Performance
        };
        e.Freeze();
        return e;
    }
}
