using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Satchel.Services;

namespace Satchel.ViewModels;

/// <summary>An entry in a page's side panel (filter, notebook, list, category…).</summary>
public sealed partial class SideFilter : ObservableObject
{
    public SideFilter(string key, string title, string glyph, object? payload = null, string? colorKey = null)
    {
        Key = key;
        _title = title;
        Glyph = glyph;
        Payload = payload;
        _colorKey = colorKey;
    }

    public string Key { get; }
    public string Glyph { get; }
    public object? Payload { get; }

    [ObservableProperty] private string _title;
    [ObservableProperty] private bool _isSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge))]
    private int _count;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IconBrush))]
    private string? _colorKey;

    public string? Badge => Count > 0 ? (Count > 999 ? "999+" : Count.ToString()) : null;
    public Brush? IconBrush => ColorKey == null ? null : ThemeBrushes.Tag(ColorKey);
}

public sealed record Choice<T>(T Value, string Label, string? Glyph = null)
{
    public override string ToString() => Label;
}
