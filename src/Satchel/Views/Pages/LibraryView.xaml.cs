using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Satchel.Behaviors;
using Satchel.Services;
using Satchel.ViewModels;

namespace Satchel.Views.Pages;

public partial class LibraryView : UserControl
{
    private LibraryViewModel? _vm;
    private ScrollViewer? _scroll;
    private readonly DispatcherTimer _previewTimer;
    private bool _restoring;
    private int _findIndex = -1;
    private string _lastFind = "";

    public LibraryView()
    {
        InitializeComponent();
        // The whole page takes dropped files — including over the reader, which used to refuse them.
        _ = new Controls.FileDropTarget(this, DropZone, Services.Import.DocumentImporter.IsSupported, files => _vm?.ImportFilesAsync(files) ?? Task.CompletedTask);
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); Render(restore: false); };
        DataContextChanged += (_, e) =>
        {
            if (_vm != null)
            {
                _vm.RenderRequested -= OnRenderRequested;
                _vm.FindRequested -= OnFindRequested;
                _vm.PropertyChanged -= OnVmChanged;
            }
            _vm = e.NewValue as LibraryViewModel;
            if (_vm == null) return;
            _vm.RenderRequested += OnRenderRequested;
            _vm.FindRequested += OnFindRequested;
            _vm.PropertyChanged += OnVmChanged;
            DropZone.Title = _vm.DropTitle;
            Render(restore: true);
            UpdateLayoutMode();
        };
        Reader.SizeChanged += (_, _) => UpdatePagePadding();
        // The folders take up to about 40% of the pane; the documents get the rest.
        ListPane.SizeChanged += (_, e) => FolderScroll.MaxHeight = Math.Max(140, e.NewSize.Height * 0.4);
        Pager.PositionChanged += p =>
        {
            if (_vm == null) return;
            _vm.ReportProgress(p);
            ProgressFill.Width = ProgressTrack.ActualWidth * p;
        };
        Reader.Loaded += (_, _) => HookScroll();
        Loaded += (_, _) => { HookScroll(); UpdateLayoutMode(); };
        ThemeState.Instance.PropertyChanged += OnThemeChanged;
        Unloaded += (_, _) => ThemeState.Instance.PropertyChanged -= OnThemeChanged;
    }

    private void OnThemeChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.BeginInvoke(() => Render(restore: false));

    private void OnRenderRequested() => Dispatcher.BeginInvoke(DispatcherPriority.Background, () => Render(restore: true));

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(LibraryViewModel.EditText) when _vm?.IsEditing == true:
                _previewTimer.Stop();
                _previewTimer.Interval = _vm.PreviewDelay;
                _previewTimer.Start();
                break;
            case nameof(LibraryViewModel.IsEditing):
            case nameof(LibraryViewModel.Settings):
                UpdateLayoutMode();
                break;
            case nameof(LibraryViewModel.IsFindOpen) when _vm?.IsFindOpen == true:
                Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { FindBox.Focus(); FindBox.SelectAll(); });
                break;
            case nameof(LibraryViewModel.FindText):
                _findIndex = -1;
                break;
            case nameof(LibraryViewModel.DropTitle):
                DropZone.Title = _vm!.DropTitle;
                break;
        }
    }

    // ----- folders: drag documents and folders onto a folder -----

    private const string DocFormat = "Satchel.LibraryDoc", FolderFormat = "Satchel.LibraryFolder";
    private Point _dragStart;
    private object? _dragItem;
    private LibraryFolderRow? _dropRow;

    private void OnDragSourceDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(this);
        _dragItem = (sender as FrameworkElement)?.DataContext;
    }

    private void OnDragSourceMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _dragItem == null || sender is not FrameworkElement source) return;
        var p = e.GetPosition(this);
        if (Math.Abs(p.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(p.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        var item = _dragItem;
        _dragItem = null;
        var data = new DataObject();
        if (item is Models.LibraryDoc doc) data.SetData(DocFormat, doc.Id);
        else if (item is LibraryFolderRow { Folder: { } folder }) data.SetData(FolderFormat, folder.Id);
        else return;
        // The row is a button holding the mouse: let go, or it stays pressed after the drop.
        source.ReleaseMouseCapture();
        try { DragDrop.DoDragDrop(source, data, DragDropEffects.Move); }
        finally { SetDropRow(null); }
    }

    /// <summary>Can what's being dragged go into this row? A folder can't go into itself or one of its own folders.</summary>
    private bool CanDrop(LibraryFolderRow row, IDataObject data)
    {
        if (_vm == null) return false;
        if (data.GetDataPresent(DocFormat) && data.GetData(DocFormat) is int docId && _vm.FindDoc(docId) is { } doc)
            return row.Kind switch
            {
                FolderRowKind.Favorites => !doc.IsFavorite,
                FolderRowKind.All => doc.FolderId != null,
                _ => doc.FolderId != row.Folder!.Id
            };
        if (data.GetDataPresent(FolderFormat) && data.GetData(FolderFormat) is int folderId && _vm.FindFolder(folderId) is { } folder)
        {
            if (row.Kind == FolderRowKind.All) return folder.ParentId != null;
            if (row.Kind != FolderRowKind.Folder || folder.ParentId == row.Folder!.Id) return false;
            for (int? id = row.Folder.Id; id is int at; id = _vm.FindFolder(at)?.ParentId)
                if (at == folder.Id) return false;
            return true;
        }
        return false;
    }

    private void OnFolderDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop)) return; // files: the page-wide drop imports them
        var row = (sender as FrameworkElement)?.DataContext as LibraryFolderRow;
        bool ok = row != null && CanDrop(row, e.Data);
        e.Effects = ok ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
        SetDropRow(ok ? row : null);
    }

    private void OnFolderDragLeave(object sender, DragEventArgs e)
    {
        if (sender is not FrameworkElement el || !ReferenceEquals(el.DataContext, _dropRow)) return;
        var p = e.GetPosition(el);
        if (p.X <= 0 || p.Y <= 0 || p.X >= el.ActualWidth || p.Y >= el.ActualHeight) SetDropRow(null);
    }

    private async void OnFolderDrop(object sender, DragEventArgs e)
    {
        SetDropRow(null);
        if (_vm == null || (sender as FrameworkElement)?.DataContext is not LibraryFolderRow row || !CanDrop(row, e.Data)) return;
        e.Handled = true;
        if (e.Data.GetData(DocFormat) is int docId && _vm.FindDoc(docId) is { } doc)
        {
            if (row.Kind == FolderRowKind.Favorites) await _vm.ToggleFavoriteCommand.ExecuteAsync(doc);
            else await _vm.MoveToFolderAsync(doc, row.Folder?.Id);
        }
        else if (e.Data.GetData(FolderFormat) is int folderId && _vm.FindFolder(folderId) is { } folder)
            await _vm.MoveFolderAsync(folder, row.Folder?.Id);
    }

    private void SetDropRow(LibraryFolderRow? row)
    {
        if (ReferenceEquals(row, _dropRow)) return;
        if (_dropRow != null) _dropRow.IsDropTarget = false;
        _dropRow = row;
        if (row != null) row.IsDropTarget = true;
    }

    /// <summary>⋯ → Move to folder → a folder (or New folder…).</summary>
    private async void OnMoveToFolder(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedDoc is not { } doc || (sender as FrameworkElement)?.DataContext is not FolderChoice choice) return;
        e.Handled = true;
        if (choice.FolderId == -1) await _vm.MoveToNewFolderAsync(doc);
        else await _vm.MoveToFolderAsync(doc, choice.FolderId);
    }

    private void UpdateLayoutMode()
    {
        if (_vm == null) return;
        bool editing = _vm.IsEditing;
        EditorColumn.Width = editing ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        TocPanel.Visibility = !editing && _vm.Settings.ReaderShowToc && _vm.Toc.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePagePadding();
        if (editing) Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Editor.Focus());
    }

    // ----- rendering -----

    private ReaderStyle BuildStyle()
    {
        var s = _vm!.Settings;
        Brush R(string key, string fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallback));
        SolidColorBrush B(string hex) => new((Color)ColorConverter.ConvertFromString(hex));
        var (bg, fg, muted, accent, code, rule, quote, dark) = s.ReaderTheme switch
        {
            "paper" => (B("#FFFDF8"), B("#1F2328"), B("#5F6872"), B("#0B6E99"), B("#F2EFE7"), B("#E4DFD3"), B("#C9A86A"), false),
            "sepia" => (B("#F4ECD8"), B("#4B3B2A"), B("#7A6650"), B("#99581A"), B("#EADFC6"), B("#DCCFB2"), B("#B08D57"), false),
            "night" => (B("#15171C"), B("#D0D4DA"), B("#8A919B"), B("#5CC3B3"), B("#1F232A"), B("#2C3139"), B("#3D8F84"), true),
            _ => ((Brush)R("SurfaceBrush", "#FFFFFF"), R("TextBrush", "#1F2328"), R("TextMutedBrush", "#5F6872"), R("AccentTextBrush", "#0F9488"),
                  new SolidColorBrush(Color.FromArgb(ThemeState.Instance.IsDark ? (byte)40 : (byte)22, 128, 128, 128)), R("StrokeBrush", "#E5E5E5"), R("AccentBrush", "#0F9488"),
                  ThemeState.Instance.IsDark)
        };
        var (font, heading) = s.ReaderFont switch
        {
            "book" => (new FontFamily("Palatino Linotype, Book Antiqua, Georgia"), new FontFamily("Palatino Linotype, Book Antiqua, Georgia")),
            "sans" => (new FontFamily("Segoe UI Variable Text, Segoe UI"), new FontFamily("Segoe UI Variable Display, Segoe UI")),
            "mono" => (new FontFamily("Cascadia Mono, Consolas"), new FontFamily("Cascadia Mono, Consolas")),
            _ => (new FontFamily("Georgia, Cambria, Times New Roman"), new FontFamily("Georgia, Cambria, Times New Roman"))
        };
        return new ReaderStyle(bg, fg, muted, accent, code, rule, quote, font, heading,
            (FontFamily)FindResource("MonoFont"), s.ReaderFontSize, s.ReaderLineHeight, s.ReaderJustify, dark);
    }

#if DEBUG
    /// <summary>Debug counters for the speed test: renders, time spent building documents, page padding changes.</summary>
    public static int RenderCount, PaddingChanges;
    public static long RenderMs;
#endif

    private void Render(bool restore)
    {
#if DEBUG
        RenderCount++;
        var perf = System.Diagnostics.Stopwatch.StartNew();
        try { RenderCore(restore); } finally { RenderMs += perf.ElapsedMilliseconds; }
    }

    private void RenderCore(bool restore)
    {
#endif
        if (_vm?.SelectedDoc is not { } doc)
        {
            Reader.Document = null;
            Pager.Document = null;
            return;
        }
        var style = BuildStyle();
        Paper.Background = style.Background;
        var markdown = _vm.IsEditing ? _vm.EditText : doc.Markdown;
        var flow = MarkdownRenderer.Render(markdown, style, _vm.ResolveImage, url => UiCommands.OpenUrl.Execute(url), out var toc);
        if (_vm.Settings.ReaderLayout == "pages" && !_vm.IsEditing)
        {
            // Page view: pages on a slightly darker "desk".
            var s = _vm.Settings;
            double previous = Pager.Document != null ? Pager.Position : doc.Progress;
            Reader.Document = null;
            Reader.Visibility = Visibility.Collapsed;
            Pager.Visibility = Visibility.Visible;
            Pager.PageSize = s.ReaderPageSize;
            Pager.Margins = s.ReaderMargins;
            Pager.TwoPages = s.ReaderTwoPages;
            Pager.PageBackground = style.Background;
            Paper.Background = DeskBrush(style);
            Pager.ShowPosition(restore ? doc.Progress : previous);
            Pager.Document = flow;
            Pager.Refresh();
            _vm.SetToc(toc);
            UpdateLayoutMode();
            ProgressFill.Width = ProgressTrack.ActualWidth * Pager.Position;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () => Pager.Focus());
            return;
        }
        Pager.Document = null;
        Pager.Visibility = Visibility.Collapsed;
        Reader.Visibility = Visibility.Visible;
        double previousOffset = _scroll?.VerticalOffset ?? 0;
        Reader.Document = flow;
        _vm.SetToc(toc);
        UpdatePagePadding();
        UpdateLayoutMode();
        _findIndex = -1;
        // Restore the saved reading position (or keep the current one while editing).
        _restoring = true;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, () =>
        {
            HookScroll();
            if (_scroll != null)
            {
                double scrollable = Math.Max(0, _scroll.ExtentHeight - _scroll.ViewportHeight);
                _scroll.ScrollToVerticalOffset(restore && !_vm.IsEditing ? doc.Progress * scrollable : previousOffset);
            }
            UpdateProgressBar();
            // Late layout passes (images, fonts) can still move the offset; ignore them briefly.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _restoring = false);
        });
    }

    private void UpdatePagePadding()
    {
        if (Reader.Document is not { } doc || _vm == null) return;
        double available = Reader.ActualWidth > 0 ? Reader.ActualWidth : 900;
        double measure = Math.Min(_vm.Settings.ReaderWidth, Math.Max(320, available - 64));
        double side = Math.Max(28, (available - measure) / 2 - 8);
#if DEBUG
        if (doc.PagePadding != new Thickness(side, 30, side, 120)) PaddingChanges++;
#endif
        doc.PagePadding = new Thickness(side, 30, side, 120);
    }

    private void HookScroll()
    {
        var scroll = FindChild<ScrollViewer>(Reader);
        if (scroll == null || ReferenceEquals(scroll, _scroll)) return;
        if (_scroll != null) _scroll.ScrollChanged -= OnReaderScroll;
        _scroll = scroll;
        _scroll.ScrollChanged += OnReaderScroll;
    }

    private void OnReaderScroll(object sender, ScrollChangedEventArgs e)
    {
        UpdateProgressBar();
        if (_restoring || _scroll == null || _vm == null) return;
        double scrollable = _scroll.ExtentHeight - _scroll.ViewportHeight;
        // Only real scrolling counts as reading progress (layout passes report a 0-height extent).
        if (scrollable <= 1 || e.VerticalChange == 0) return;
        _vm.ReportProgress(_scroll.VerticalOffset / scrollable);
    }

    private void UpdateProgressBar()
    {
        if (_scroll == null) return;
        double scrollable = _scroll.ExtentHeight - _scroll.ViewportHeight;
        double p = scrollable <= 1 ? 1 : Math.Clamp(_scroll.VerticalOffset / scrollable, 0, 1);
        ProgressFill.Width = ProgressTrack.ActualWidth * p;
    }

    /// <summary>The surface around the pages: a touch darker (or lighter on night themes) than the paper.</summary>
    private static Brush DeskBrush(ReaderStyle style)
    {
        var paper = (style.Background as SolidColorBrush)?.Color ?? Colors.White;
        var desk = style.IsDark ? Core.ColorUtil.Darken(paper, 0.35) : Core.ColorUtil.Darken(paper, 0.07);
        return new SolidColorBrush(desk);
    }

    private void OnTocClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not TocEntry entry) return;
        if (Pager.Visibility == Visibility.Visible)
        {
            Pager.ShowContent(entry.Target.ContentStart);
            return;
        }
        var rect = entry.Target.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        if (_scroll != null && !rect.IsEmpty)
            _scroll.ScrollToVerticalOffset(Math.Max(0, _scroll.VerticalOffset + rect.Top - 24));
        else entry.Target.BringIntoView();
    }

    // ----- find -----

    private void OnFindRequested(string query, bool backwards)
    {
        bool paged = Pager.Visibility == Visibility.Visible;
        if (string.IsNullOrWhiteSpace(query) || (paged ? Pager.Document : Reader.Document) is not { } doc)
        {
            FindStatus.Text = "";
            return;
        }
        var matches = new List<(TextPointer Start, TextPointer End)>();
        foreach (var run in Runs(doc.Blocks))
        {
            var text = run.Text;
            int at = 0;
            while ((at = text.IndexOf(query, at, StringComparison.CurrentCultureIgnoreCase)) >= 0)
            {
                var start = run.ContentStart.GetPositionAtOffset(at);
                var end = start?.GetPositionAtOffset(query.Length);
                if (start != null && end != null) matches.Add((start, end));
                at += query.Length;
            }
        }
        if (matches.Count == 0)
        {
            FindStatus.Text = "No matches";
            return;
        }
        if (!string.Equals(query, _lastFind, StringComparison.CurrentCultureIgnoreCase)) _findIndex = -1;
        _lastFind = query;
        _findIndex = backwards ? (_findIndex <= 0 ? matches.Count - 1 : _findIndex - 1) : (_findIndex + 1) % matches.Count;
        var (s, e2) = matches[_findIndex];
        if (paged)
        {
            Pager.ShowContent(s);
            FindStatus.Text = $"{_findIndex + 1} of {matches.Count}";
            return;
        }
        Reader.Selection?.Select(s, e2);
        var rect = s.GetCharacterRect(LogicalDirection.Forward);
        if (_scroll != null && !rect.IsEmpty)
            _scroll.ScrollToVerticalOffset(Math.Max(0, _scroll.VerticalOffset + rect.Top - _scroll.ViewportHeight / 3));
        FindStatus.Text = $"{_findIndex + 1} of {matches.Count}";
    }

    private static IEnumerable<Run> Runs(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case Paragraph p:
                    foreach (var r in InlineRuns(p.Inlines)) yield return r;
                    break;
                case Section s:
                    foreach (var r in Runs(s.Blocks)) yield return r;
                    break;
                case List l:
                    foreach (var item in l.ListItems)
                        foreach (var r in Runs(item.Blocks)) yield return r;
                    break;
                case Table t:
                    foreach (var row in t.RowGroups.SelectMany(g => g.Rows))
                        foreach (var cell in row.Cells)
                            foreach (var r in Runs(cell.Blocks)) yield return r;
                    break;
            }
        }
    }

    private static IEnumerable<Run> InlineRuns(InlineCollection inlines)
    {
        foreach (var inline in inlines)
        {
            if (inline is Run run) yield return run;
            else if (inline is Span span)
                foreach (var r in InlineRuns(span.Inlines)) yield return r;
        }
    }

    private void OnFindKeyDown(object sender, KeyEventArgs e)
    {
        if (_vm == null) return;
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) _vm.FindPreviousCommand.Execute(null);
            else _vm.FindNextCommand.Execute(null);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _vm.CloseFindCommand.Execute(null);
            Reader.Focus();
        }
    }

    // ----- options, print, drop -----

    private void OnStyleMenu(object sender, RoutedEventArgs e) => StylePopup.IsOpen = true;

    private void OnPrint(object sender, RoutedEventArgs e)
    {
        if (_vm?.SelectedDoc is not { } doc) return;
        var dialog = new PrintDialog();
        if (dialog.ShowDialog() != true) return;
        var s = _vm.Settings;
        var black = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        var style = new ReaderStyle(Brushes.White, black, new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55)), new SolidColorBrush(Color.FromRgb(0x0B, 0x5C, 0x8A)),
            new SolidColorBrush(Color.FromRgb(0xF1, 0xF1, 0xF1)), new SolidColorBrush(Color.FromRgb(0xD5, 0xD5, 0xD5)), new SolidColorBrush(Color.FromRgb(0xB0, 0xB0, 0xB0)),
            new FontFamily("Georgia"), new FontFamily("Georgia"), (FontFamily)FindResource("MonoFont"), 11.5, Math.Min(1.5, s.ReaderLineHeight), s.ReaderJustify, false);
        var flow = MarkdownRenderer.Render(doc.Markdown, style, _vm.ResolveImage, _ => { }, out _);
        flow.PageHeight = dialog.PrintableAreaHeight;
        flow.PageWidth = dialog.PrintableAreaWidth;
        flow.PagePadding = new Thickness(64, 56, 64, 56);
        flow.ColumnWidth = dialog.PrintableAreaWidth;
        flow.ColumnGap = 0;
        dialog.PrintDocument(((IDocumentPaginatorSource)flow).DocumentPaginator, doc.Title);
    }

    private static T? FindChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) return t;
            if (FindChild<T>(child) is { } nested) return nested;
        }
        return null;
    }
}
