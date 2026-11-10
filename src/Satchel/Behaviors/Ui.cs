using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.Input;
using Satchel.Core;

namespace Satchel.Behaviors;

/// <summary>General-purpose attached properties used by the control templates.</summary>
public static class Ui
{
    // Icon glyph shown by button / nav templates.
    public static readonly DependencyProperty IconProperty = DependencyProperty.RegisterAttached(
        "Icon", typeof(string), typeof(Ui), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));
    public static string? GetIcon(DependencyObject o) => (string?)o.GetValue(IconProperty);
    public static void SetIcon(DependencyObject o, string? v) => o.SetValue(IconProperty, v);

    // Placeholder text for TextBox / PasswordBox / ComboBox templates.
    public static readonly DependencyProperty PlaceholderProperty = DependencyProperty.RegisterAttached(
        "Placeholder", typeof(string), typeof(Ui), new PropertyMetadata(null));
    public static string? GetPlaceholder(DependencyObject o) => (string?)o.GetValue(PlaceholderProperty);
    public static void SetPlaceholder(DependencyObject o, string? v) => o.SetValue(PlaceholderProperty, v);

    // Per-element corner radius override for templates that support it.
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius", typeof(CornerRadius), typeof(Ui), new PropertyMetadata(new CornerRadius(8)));
    public static CornerRadius GetCornerRadius(DependencyObject o) => (CornerRadius)o.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject o, CornerRadius v) => o.SetValue(CornerRadiusProperty, v);

    // Optional icon color for templates (falls back to Foreground).
    public static readonly DependencyProperty IconBrushProperty = DependencyProperty.RegisterAttached(
        "IconBrush", typeof(System.Windows.Media.Brush), typeof(Ui), new PropertyMetadata(null));
    public static System.Windows.Media.Brush? GetIconBrush(DependencyObject o) => (System.Windows.Media.Brush?)o.GetValue(IconBrushProperty);
    public static void SetIconBrush(DependencyObject o, System.Windows.Media.Brush? v) => o.SetValue(IconBrushProperty, v);

    // Small badge text (e.g. counts in nav items).
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.RegisterAttached(
        "Badge", typeof(string), typeof(Ui), new PropertyMetadata(null));
    public static string? GetBadge(DependencyObject o) => (string?)o.GetValue(BadgeProperty);
    public static void SetBadge(DependencyObject o, string? v) => o.SetValue(BadgeProperty, v);

    // Focus the element once it's loaded.
    public static readonly DependencyProperty FocusOnLoadProperty = DependencyProperty.RegisterAttached(
        "FocusOnLoad", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnFocusOnLoadChanged));
    public static bool GetFocusOnLoad(DependencyObject o) => (bool)o.GetValue(FocusOnLoadProperty);
    public static void SetFocusOnLoad(DependencyObject o, bool v) => o.SetValue(FocusOnLoadProperty, v);

    private static void OnFocusOnLoadChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe || e.NewValue is not true) return;
        if (fe.IsLoaded) FocusLater(fe);
        else fe.Loaded += (_, _) => FocusLater(fe);
    }

    public static void FocusLater(FrameworkElement fe)
    {
        fe.Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            if (fe is PasswordInputHost host) { host.FocusInput(); return; }
            fe.Focus();
            Keyboard.Focus(fe);
            if (fe is TextBox tb) tb.CaretIndex = tb.Text.Length;
        });
    }

    // Select all text when a TextBox receives keyboard focus.
    public static readonly DependencyProperty SelectAllOnFocusProperty = DependencyProperty.RegisterAttached(
        "SelectAllOnFocus", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnSelectAllChanged));
    public static bool GetSelectAllOnFocus(DependencyObject o) => (bool)o.GetValue(SelectAllOnFocusProperty);
    public static void SetSelectAllOnFocus(DependencyObject o, bool v) => o.SetValue(SelectAllOnFocusProperty, v);

    private static void OnSelectAllChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is TextBox tb && e.NewValue is true)
            tb.GotKeyboardFocus += (_, _) => tb.Dispatcher.BeginInvoke(DispatcherPriority.Input, tb.SelectAll);
    }

    // Execute a command when Enter is pressed in a TextBox.
    public static readonly DependencyProperty EnterCommandProperty = DependencyProperty.RegisterAttached(
        "EnterCommand", typeof(ICommand), typeof(Ui), new PropertyMetadata(null, OnEnterCommandChanged));
    public static ICommand? GetEnterCommand(DependencyObject o) => (ICommand?)o.GetValue(EnterCommandProperty);
    public static void SetEnterCommand(DependencyObject o, ICommand? v) => o.SetValue(EnterCommandProperty, v);

    public static readonly DependencyProperty EnterCommandParameterProperty = DependencyProperty.RegisterAttached(
        "EnterCommandParameter", typeof(object), typeof(Ui), new PropertyMetadata(null));
    public static object? GetEnterCommandParameter(DependencyObject o) => o.GetValue(EnterCommandParameterProperty);
    public static void SetEnterCommandParameter(DependencyObject o, object? v) => o.SetValue(EnterCommandParameterProperty, v);

    private static void OnEnterCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.PreviewKeyDown -= OnEnterKeyDown;
        if (e.NewValue != null) el.PreviewKeyDown += OnEnterKeyDown;
    }

    private static void OnEnterKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        var d = (DependencyObject)sender;
        if (d is TextBox tb) tb.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
        var cmd = GetEnterCommand(d);
        var param = GetEnterCommandParameter(d);
        if (cmd?.CanExecute(param) == true)
        {
            cmd.Execute(param);
            e.Handled = true;
        }
    }

    // Execute a command when Escape is pressed.
    public static readonly DependencyProperty EscapeCommandProperty = DependencyProperty.RegisterAttached(
        "EscapeCommand", typeof(ICommand), typeof(Ui), new PropertyMetadata(null, OnEscapeCommandChanged));
    public static ICommand? GetEscapeCommand(DependencyObject o) => (ICommand?)o.GetValue(EscapeCommandProperty);
    public static void SetEscapeCommand(DependencyObject o, ICommand? v) => o.SetValue(EscapeCommandProperty, v);

    private static void OnEscapeCommandChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.PreviewKeyDown -= OnEscapeKeyDown;
        if (e.NewValue != null) el.PreviewKeyDown += OnEscapeKeyDown;
    }

    private static void OnEscapeKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        var cmd = GetEscapeCommand((DependencyObject)sender);
        if (cmd?.CanExecute(null) == true)
        {
            cmd.Execute(null);
            e.Handled = true;
        }
    }

    // An inner ScrollViewer that hands the mouse wheel to the page when it has nothing (more) to scroll itself.
    public static readonly DependencyProperty WheelPassThroughProperty = DependencyProperty.RegisterAttached(
        "WheelPassThrough", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnWheelPassThroughChanged));
    public static bool GetWheelPassThrough(DependencyObject o) => (bool)o.GetValue(WheelPassThroughProperty);
    public static void SetWheelPassThrough(DependencyObject o, bool v) => o.SetValue(WheelPassThroughProperty, v);

    private static void OnWheelPassThroughChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        sv.PreviewMouseWheel -= OnPassThroughWheel;
        if (e.NewValue is true) sv.PreviewMouseWheel += OnPassThroughWheel;
    }

    private static void OnPassThroughWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer sv || e.Handled) return;
        bool up = e.Delta > 0;
        bool canScroll = sv.ScrollableHeight > 0.5 && (up ? sv.VerticalOffset > 0.5 : sv.VerticalOffset < sv.ScrollableHeight - 0.5);
        if (canScroll) return;
        // Inner controls with their own scrolling (a text box, a list) still get the wheel.
        if (e.OriginalSource is DependencyObject origin && HasScrollableAncestorBelow(origin, sv)) return;
        e.Handled = true;
        if (System.Windows.Media.VisualTreeHelper.GetParent(sv) is UIElement parent)
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sv });
    }

    private static bool HasScrollableAncestorBelow(DependencyObject origin, ScrollViewer boundary)
    {
        for (var d = origin; d != null && !ReferenceEquals(d, boundary); d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d))
            if (d is ScrollViewer inner && inner.ScrollableHeight > 0.5) return true;
        return false;
    }

    // Open a context menu on left click (for "more" buttons).
    public static readonly DependencyProperty MenuOnClickProperty = DependencyProperty.RegisterAttached(
        "MenuOnClick", typeof(bool), typeof(Ui), new PropertyMetadata(false, OnMenuOnClickChanged));
    public static bool GetMenuOnClick(DependencyObject o) => (bool)o.GetValue(MenuOnClickProperty);
    public static void SetMenuOnClick(DependencyObject o, bool v) => o.SetValue(MenuOnClickProperty, v);

    private static void OnMenuOnClickChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ButtonBase b || e.NewValue is not true) return;
        b.Click += (_, _) =>
        {
            if (b.ContextMenu == null) return;
            b.ContextMenu.PlacementTarget = b;
            b.ContextMenu.Placement = PlacementMode.Bottom;
            b.ContextMenu.DataContext = b.DataContext;
            b.ContextMenu.IsOpen = true;
        };
    }
}

/// <summary>Implemented by controls that host an inner input (so FocusOnLoad focuses the right thing).</summary>
public interface PasswordInputHost
{
    void FocusInput();
}

/// <summary>App-wide commands usable from any template.</summary>
public static class UiCommands
{
    public static IRelayCommand<TextBox> ClearText { get; } = new RelayCommand<TextBox>(tb =>
    {
        if (tb == null) return;
        tb.Clear();
        tb.Focus();
    });

    public static IRelayCommand<string> OpenUrl { get; } = new RelayCommand<string>(OpenLink);

    public static IRelayCommand<string> CopyText { get; } = new RelayCommand<string>(text =>
        Security.SecureClipboard.Copy(text, sensitive: false, "Copied to clipboard"));

    public static IRelayCommand<string> CopySecret { get; } = new RelayCommand<string>(text =>
        Security.SecureClipboard.Copy(text, sensitive: true, "Copied — clears automatically"));

    public static void OpenLink(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        var target = url.Trim();
        if (!target.Contains("://", StringComparison.Ordinal) && !target.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            target = "https://" + target;
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) return;
        if (uri.Scheme is not ("http" or "https" or "mailto" or "ftp")) return;
        try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
        catch { /* no browser registered */ }
    }

    public static void OpenFolder(string path)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true }); }
        catch { }
    }
}
