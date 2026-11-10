using System.Windows.Media;
using Satchel.Core;

namespace Satchel.Services;

/// <summary>
/// Shared, mutable brushes for palette keys. Bindings keep the same brush instance and the
/// ThemeService recolors them in place, so colored items update instantly on theme change.
/// </summary>
public static class ThemeBrushes
{
    private static readonly Dictionary<string, SolidColorBrush> TagBrushes = new();
    private static readonly Dictionary<string, SolidColorBrush> TagSubtleBrushes = new();
    private static readonly Dictionary<string, SolidColorBrush> NoteBrushes = new();
    private static readonly Dictionary<string, SolidColorBrush> StickyBrushes = new();
    private static bool _isDark;
    private static Color _surface = Colors.White;

    public static void Refresh(bool isDark, Color surface)
    {
        _isDark = isDark;
        _surface = surface;
        foreach (var (key, brush) in TagBrushes) brush.Color = TagColor(key);
        foreach (var (key, brush) in TagSubtleBrushes) brush.Color = SubtleColor(key);
        foreach (var (key, brush) in NoteBrushes) brush.Color = NoteColor(key);
    }

    public static SolidColorBrush Tag(string? key)
    {
        key ??= "teal";
        if (!TagBrushes.TryGetValue(key, out var b)) TagBrushes[key] = b = new SolidColorBrush(TagColor(key));
        return b;
    }

    public static SolidColorBrush TagSubtle(string? key)
    {
        key ??= "teal";
        if (!TagSubtleBrushes.TryGetValue(key, out var b)) TagSubtleBrushes[key] = b = new SolidColorBrush(SubtleColor(key));
        return b;
    }

    public static SolidColorBrush Note(string? key)
    {
        key = string.IsNullOrEmpty(key) ? "default" : key;
        if (!NoteBrushes.TryGetValue(key, out var b)) NoteBrushes[key] = b = new SolidColorBrush(NoteColor(key));
        return b;
    }

    public static SolidColorBrush Sticky(string? key)
    {
        key ??= "yellow";
        if (!StickyBrushes.TryGetValue(key, out var b))
        {
            var option = Palettes.Stickies.FirstOrDefault(s => s.Key == key) ?? Palettes.Stickies[0];
            StickyBrushes[key] = b = new SolidColorBrush(ColorUtil.Parse(option.Light));
        }
        return b;
    }

    private static Color TagColor(string key) => Palettes.TagColor(key, _isDark);

    private static Color SubtleColor(string key) => ColorUtil.Blend(_surface, TagColor(key), _isDark ? 0.2 : 0.14);

    private static Color NoteColor(string key)
    {
        var option = Palettes.Notes.FirstOrDefault(n => n.Key == key);
        if (option == null || key == "default") return _surface;
        return ColorUtil.Parse(option.Resolve(_isDark));
    }
}
