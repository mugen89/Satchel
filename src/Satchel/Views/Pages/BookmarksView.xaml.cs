using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Satchel.Models;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class BookmarksView : UserControl
{
    public BookmarksView() => InitializeComponent();

    private BookmarksViewModel? Vm => DataContext as BookmarksViewModel;

    private void OnCardDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Bookmark b) Vm?.OpenCommand.Execute(b);
        e.Handled = true;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ReadUrl(e.Data) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ReadUrl(e.Data) is { } url && Vm != null) await Vm.AddUrlAsync(url);
    }

    /// <summary>A link dragged from a browser (URL formats or plain text that is a web address).</summary>
    private static string? ReadUrl(IDataObject data)
    {
        try
        {
            if (data.GetDataPresent("UniformResourceLocatorW") && data.GetData("UniformResourceLocatorW") is MemoryStream ms)
                return System.Text.Encoding.Unicode.GetString(ms.ToArray()).TrimEnd('\0').Trim();
            if (data.GetDataPresent(DataFormats.UnicodeText) && data.GetData(DataFormats.UnicodeText) is string text)
            {
                text = text.Trim();
                if (BoardViewModel.LooksLikeUrl(text)) return text;
            }
        }
        catch { /* unsupported format */ }
        return null;
    }
}
