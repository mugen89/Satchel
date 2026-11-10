using System.Windows.Media;

namespace Satchel.Core;

public sealed record ColorOption(string Key, string Name, string Light, string Dark)
{
    public string Resolve(bool isDark) => isDark ? Dark : Light;
    public SolidColorBrush PreviewBrush => ColorUtil.Brush(Light);
}

/// <summary>
/// Shared color palettes. Deliberately no purple, violet or indigo anywhere.
/// </summary>
public static class Palettes
{
    /// <summary>Accent swatches: (light theme variant, dark theme variant).</summary>
    public static readonly IReadOnlyList<ColorOption> Accents =
    [
        new("teal", "Teal", "#0F9488", "#2DD4BF"),
        new("emerald", "Emerald", "#059669", "#34D399"),
        new("green", "Green", "#16A34A", "#4ADE80"),
        new("lime", "Lime", "#5B8C0A", "#A3E635"),
        new("amber", "Amber", "#C26A04", "#FBBF24"),
        new("orange", "Orange", "#E2560B", "#FB923C"),
        new("terracotta", "Terracotta", "#C2552D", "#E8835C"),
        new("red", "Red", "#DC2626", "#F87171"),
        new("rose", "Rose", "#E11D48", "#FB7185"),
        new("sky", "Sky", "#0284C7", "#38BDF8"),
        new("blue", "Blue", "#2563EB", "#60A5FA"),
        new("cyan", "Cyan", "#0891B2", "#22D3EE"),
        new("frost", "Frost", "#2F7F96", "#88C0D0"),
        new("slate", "Slate", "#475569", "#A5B4C8"),
    ];

    /// <summary>Colors for calendars, task lists, notebooks, profile tiles.</summary>
    public static readonly IReadOnlyList<ColorOption> Tags =
    [
        new("teal", "Teal", "#14B8A6", "#2DD4BF"),
        new("green", "Green", "#22C55E", "#4ADE80"),
        new("lime", "Lime", "#84CC16", "#A3E635"),
        new("amber", "Amber", "#F59E0B", "#FBBF24"),
        new("orange", "Orange", "#F97316", "#FB923C"),
        new("red", "Red", "#EF4444", "#F87171"),
        new("rose", "Rose", "#F43F5E", "#FB7185"),
        new("sky", "Sky", "#0EA5E9", "#38BDF8"),
        new("blue", "Blue", "#3B82F6", "#60A5FA"),
        new("cyan", "Cyan", "#06B6D4", "#22D3EE"),
        new("brown", "Brown", "#A16207", "#D6A35C"),
        new("slate", "Slate", "#64748B", "#94A3B8"),
    ];

    /// <summary>Note card backgrounds: pastel on light themes, muted deep tones on dark themes.</summary>
    public static readonly IReadOnlyList<ColorOption> Notes =
    [
        new("default", "Default", "", ""),
        new("coral", "Coral", "#FDE3DE", "#4A2A27"),
        new("peach", "Peach", "#FDEBD8", "#4A3424"),
        new("sand", "Sand", "#FAF1CF", "#443C22"),
        new("mint", "Mint", "#DBF4E8", "#1E4038"),
        new("sage", "Sage", "#E4EDD9", "#2D3A28"),
        new("sky", "Sky", "#DAEEFA", "#1D394C"),
        new("ocean", "Ocean", "#D6E4F5", "#1E2E47"),
        new("rose", "Rose", "#FADFE7", "#482532"),
        new("slate", "Slate", "#E5E9EE", "#2E3540"),
    ];

    /// <summary>Sticky-note colors. Always bright with dark ink, like real stickies.</summary>
    public static readonly IReadOnlyList<ColorOption> Stickies =
    [
        new("yellow", "Yellow", "#FFE681", "#FFE681"),
        new("orange", "Orange", "#FFC68C", "#FFC68C"),
        new("pink", "Pink", "#FFB8CB", "#FFB8CB"),
        new("green", "Green", "#BDECA6", "#BDECA6"),
        new("teal", "Teal", "#A3E7DB", "#A3E7DB"),
        new("blue", "Blue", "#AAD8FF", "#AAD8FF"),
        new("white", "White", "#F7F7F5", "#F7F7F5"),
        new("gray", "Gray", "#D6DADF", "#D6DADF"),
    ];

    public static ColorOption FindTag(string? key) =>
        Tags.FirstOrDefault(c => c.Key == key) ?? Tags[0];

    public static Color TagColor(string? key, bool isDark)
    {
        if (!string.IsNullOrEmpty(key) && key.StartsWith('#') && ColorUtil.TryParse(key, out var custom)) return custom;
        return ColorUtil.Parse(FindTag(key).Resolve(isDark));
    }

    public static ColorOption FindAccent(string? key) =>
        Accents.FirstOrDefault(c => c.Key == key) ?? Accents[0];
}
