using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Satchel.Core;

namespace Satchel.Controls;

/// <summary>
/// "Drop files here" over a page while files are dragged onto it: a tinted panel with a dashed outline. Use
/// <see cref="FileDropTarget"/> to wire it to the page.
/// </summary>
public sealed class DropOverlay : Grid
{
    private readonly TextBlock _title, _hint, _count;

    public DropOverlay()
    {
        IsHitTestVisible = false;
        Visibility = Visibility.Collapsed;
        Opacity = 0;
        Margin = new Thickness(12);
        var backdrop = new Border { CornerRadius = new CornerRadius(14) };
        backdrop.SetResourceReference(Border.BackgroundProperty, "AccentSubtleBrush");
        backdrop.Opacity = 0.93;
        var outline = new Rectangle { RadiusX = 14, RadiusY = 14, StrokeThickness = 2, StrokeDashArray = [6, 4], SnapsToDevicePixels = true };
        outline.SetResourceReference(Shape.StrokeProperty, "AccentBrush");
        var icon = new TextBlock { Text = Glyphs.Import, FontSize = 34, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 14) };
        icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
        icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        _title = new TextBlock { FontSize = 20, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center };
        _title.SetResourceReference(TextBlock.ForegroundProperty, "TextBrush");
        _hint = new TextBlock { FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Margin = new Thickness(0, 6, 0, 0) };
        _hint.SetResourceReference(TextBlock.ForegroundProperty, "TextMutedBrush");
        _count = new TextBlock { FontSize = 12.5, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 12, 0, 0) };
        _count.SetResourceReference(TextBlock.ForegroundProperty, "AccentTextBrush");
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        stack.Children.Add(icon);
        stack.Children.Add(_title);
        stack.Children.Add(_hint);
        stack.Children.Add(_count);
        Children.Add(backdrop);
        Children.Add(outline);
        Children.Add(stack);
    }

    public string Title { get => _title.Text; set => _title.Text = value; }
    public string Hint { get => _hint.Text; set => _hint.Text = value; }

    public void Show(int files, int unsupported)
    {
        _count.Text = (files == 1 ? "1 file" : $"{files} files") + (unsupported > 0 ? $" · {unsupported} can't be read and will be skipped" : "");
        if (Visibility == Visibility.Visible) return;
        Visibility = Visibility.Visible;
        BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(Anim.Enabled ? 120 : 0)));
    }

    public void Hide()
    {
        if (Visibility != Visibility.Visible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(Anim.Enabled ? 120 : 0));
        fade.Completed += (_, _) => { if (Opacity == 0) Visibility = Visibility.Collapsed; };
        BeginAnimation(OpacityProperty, fade);
    }
}

/// <summary>
/// Makes a whole page a drop target for files. It listens to the tunnelling (Preview) drag events, so text viewers and
/// boxes inside the page can't swallow the drop, and shows a <see cref="DropOverlay"/> while files hover.
/// </summary>
public sealed class FileDropTarget
{
    private readonly FrameworkElement _host;
    private readonly DropOverlay _overlay;
    private readonly Func<string, bool> _accepts;
    private readonly Func<string[], Task> _drop;

    public FileDropTarget(FrameworkElement host, DropOverlay overlay, Func<string, bool> accepts, Func<string[], Task> drop)
    {
        _host = host;
        _overlay = overlay;
        _accepts = accepts;
        _drop = drop;
        host.AllowDrop = true;
        host.PreviewDragEnter += OnDragOver;
        host.PreviewDragOver += OnDragOver;
        host.PreviewDragLeave += OnDragLeave;
        host.PreviewDrop += OnDrop;
    }

    private static string[] Files(IDataObject data)
    {
        try
        {
            return data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files ? files : [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var files = Files(e.Data);
        if (files.Length == 0) return; // text and other drags go to whatever is under the pointer
        int good = files.Count(f => File.Exists(f) && _accepts(f));
        e.Effects = good > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (good > 0) _overlay.Show(good, files.Length - good);
        else _overlay.Hide();
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        // DragLeave also fires when moving between child elements: only hide once the pointer is really outside.
        var p = e.GetPosition(_host);
        if (p.X <= 0 || p.Y <= 0 || p.X >= _host.ActualWidth || p.Y >= _host.ActualHeight) _overlay.Hide();
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var files = Files(e.Data);
        _overlay.Hide();
        if (files.Length == 0) return;
        e.Handled = true;
        var good = files.Where(f => File.Exists(f) && _accepts(f)).ToArray();
        if (good.Length > 0) await _drop(good);
    }
}
