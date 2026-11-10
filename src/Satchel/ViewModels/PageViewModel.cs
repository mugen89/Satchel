using CommunityToolkit.Mvvm.ComponentModel;

namespace Satchel.ViewModels;

public interface INavigator
{
    void NavigateTo(string pageKey, object? parameter = null);
    void ShowPalette();
}

/// <summary>Base for top-level pages hosted by the shell.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    public abstract string Title { get; }

    [ObservableProperty] private bool _isLoading;

    /// <summary>Called every time the page becomes visible. Parameter may identify an item to open.</summary>
    public virtual Task OnNavigatedToAsync(object? parameter) => Task.CompletedTask;

    /// <summary>Called when leaving the page (save pending edits).</summary>
    public virtual Task OnNavigatedFromAsync() => FlushAsync();

    /// <summary>Persist any pending edits immediately (before lock / exit).</summary>
    public virtual Task FlushAsync() => Task.CompletedTask;

    /// <summary>Ctrl+N on this page.</summary>
    public virtual void OnNewShortcut() { }

    /// <summary>Ctrl+F on this page.</summary>
    public virtual void OnSearchShortcut() => SearchFocusRequested?.Invoke();

    /// <summary>Esc with no dialog or palette open. Return true when the page used it (e.g. to leave an editing mode).</summary>
    public virtual bool OnEscape() => false;

    public event Action? SearchFocusRequested;
}

public sealed partial class NavItem : ObservableObject
{
    public NavItem(string key, string title, string glyph, Type pageType, string section = "", string shortcut = "")
    {
        Key = key;
        Title = title;
        Glyph = glyph;
        PageType = pageType;
        Section = section;
        _shortcut = shortcut;
    }

    public string Key { get; }
    public string Title { get; }
    public string Glyph { get; }
    public Type PageType { get; }
    public string Section { get; }
    public string Tooltip
    {
        get
        {
            var keys = string.Join(" · ", new[] { CustomKeys, Shortcut }.Where(k => !string.IsNullOrEmpty(k)));
            return keys.Length == 0 ? Title : $"{Title}  ({keys})";
        }
    }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _badge;
    [ObservableProperty] private string _shortcut;
    /// <summary>Your own key for this tab (Settings → Key bindings), if any.</summary>
    [ObservableProperty] private string? _customKeys;
    /// <summary>Hidden because its sidebar section is collapsed (the open page always stays visible).</summary>
    [ObservableProperty] private bool _isFoldedAway;

    partial void OnShortcutChanged(string value) => OnPropertyChanged(nameof(Tooltip));
    partial void OnCustomKeysChanged(string? value) => OnPropertyChanged(nameof(Tooltip));
}

/// <summary>Section header row in the sidebar; click it to collapse or expand its tabs.</summary>
public sealed partial class NavSection(string title) : ObservableObject
{
    public string Title { get; } = title;
    [ObservableProperty] private bool _isCollapsed;
    /// <summary>Tabs hidden while collapsed.</summary>
    [ObservableProperty] private int _hiddenCount;
    /// <summary>A hidden tab has a badge (new videos, unread posts…).</summary>
    [ObservableProperty] private bool _hasHiddenBadge;

    public string Tooltip => IsCollapsed ? $"{Title}: {HiddenCount} tab{(HiddenCount == 1 ? "" : "s")} hidden — click to show" : $"{Title} — click to collapse";

    partial void OnIsCollapsedChanged(bool value) => OnPropertyChanged(nameof(Tooltip));
    partial void OnHiddenCountChanged(int value) => OnPropertyChanged(nameof(Tooltip));
}
