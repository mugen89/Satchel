using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Satchel.Core;

namespace Satchel.Controls;

/// <summary>
/// Rich text note editor. Content is stored as base64 XamlPackage (keeps images).
/// Default text color/font/size are written as sentinels and stripped on load, so notes follow
/// the current theme instead of freezing the colors that were active when they were saved.
/// </summary>
public partial class RichTextEditor : UserControl
{
    private static readonly Color SentinelColor = Color.FromArgb(255, 1, 2, 3);
    private static readonly Color SentinelBackground = Color.FromArgb(0, 1, 2, 3);
    private const string SentinelFont = "Satchel Default Font";
    private const double SentinelSize = 13.37;

    private readonly DispatcherTimer _debounce;
    private bool _loading;
    private bool _dirty;
    private string? _lastValue;

    public static readonly DependencyProperty ContentTextProperty = DependencyProperty.Register(
        nameof(ContentText), typeof(string), typeof(RichTextEditor),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnContentTextChanged));
    public string ContentText { get => (string)GetValue(ContentTextProperty); set => SetValue(ContentTextProperty, value); }

    public static readonly DependencyProperty PlainTextProperty = DependencyProperty.Register(
        nameof(PlainText), typeof(string), typeof(RichTextEditor),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));
    public string PlainText { get => (string)GetValue(PlainTextProperty); set => SetValue(PlainTextProperty, value); }

    public static readonly DependencyProperty ShowToolbarProperty = DependencyProperty.Register(
        nameof(ShowToolbar), typeof(bool), typeof(RichTextEditor), new PropertyMetadata(true, (d, e) =>
            ((RichTextEditor)d).Toolbar.Visibility = e.NewValue is true ? Visibility.Visible : Visibility.Collapsed));
    public bool ShowToolbar { get => (bool)GetValue(ShowToolbarProperty); set => SetValue(ShowToolbarProperty, value); }

    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(RichTextEditor), new PropertyMetadata(false, (d, e) => ((RichTextEditor)d).Rtb.IsReadOnly = e.NewValue is true));
    public bool IsReadOnly { get => (bool)GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }

    public static readonly DependencyProperty EditorFontSizeProperty = DependencyProperty.Register(
        nameof(EditorFontSize), typeof(double), typeof(RichTextEditor), new PropertyMetadata(15.0, (d, e) => ((RichTextEditor)d).Rtb.FontSize = (double)e.NewValue));
    public double EditorFontSize { get => (double)GetValue(EditorFontSizeProperty); set => SetValue(EditorFontSizeProperty, value); }

    /// <summary>Fixed text colour (e.g. dark text on pastel sticky notes); null follows the theme.</summary>
    public static readonly DependencyProperty EditorForegroundProperty = DependencyProperty.Register(
        nameof(EditorForeground), typeof(Brush), typeof(RichTextEditor), new PropertyMetadata(null, (d, e) =>
        {
            var editor = (RichTextEditor)d;
            if (e.NewValue is Brush b)
            {
                editor.Rtb.Foreground = b;
                editor.Rtb.CaretBrush = b;
            }
            else
            {
                editor.Rtb.SetResourceReference(ForegroundProperty, "TextBrush");
                editor.Rtb.SetResourceReference(TextBoxBase.CaretBrushProperty, "TextBrush");
            }
        }));
    public Brush? EditorForeground { get => (Brush?)GetValue(EditorForegroundProperty); set => SetValue(EditorForegroundProperty, value); }

    public static readonly DependencyProperty EditorPaddingProperty = DependencyProperty.Register(
        nameof(EditorPadding), typeof(Thickness), typeof(RichTextEditor), new PropertyMetadata(new Thickness(0), (d, e) => ((RichTextEditor)d).Rtb.Padding = (Thickness)e.NewValue));
    public Thickness EditorPadding { get => (Thickness)GetValue(EditorPaddingProperty); set => SetValue(EditorPaddingProperty, value); }

    /// <summary>Raised when the caret/selection moves, so external toolbars can refresh their state.</summary>
    public event Action? FormattingChanged;

    public RichTextBox Editor => Rtb;

    public RichTextEditor()
    {
        InitializeComponent();
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _debounce.Tick += (_, _) => Flush();
        Unloaded += (_, _) => Flush();
        BuildColorButtons();
        Load(ContentText);
        Rtb.PreviewKeyDown += OnEditorKeyDown;
        Rtb.PreviewTextInput += OnEditorTextInput;
        Rtb.PreviewMouseLeftButtonDown += OnEditorMouseDown;
        Rtb.MouseMove += OnEditorMouseMove;
        DataObject.AddPastingHandler(Rtb, OnPasting);
        Rtb.PreviewDragOver += OnPreviewDragOver;
        Rtb.PreviewDrop += OnPreviewDrop;
        Loaded += (_, _) => { if (AutoFocus) Dispatcher.BeginInvoke(DispatcherPriority.Input, FocusEditor); };
    }

    /// <summary>Puts the caret at the end once the editor is shown (e.g. in a dialog).</summary>
    public static readonly DependencyProperty AutoFocusProperty = DependencyProperty.Register(
        nameof(AutoFocus), typeof(bool), typeof(RichTextEditor), new PropertyMetadata(false));
    public bool AutoFocus { get => (bool)GetValue(AutoFocusProperty); set => SetValue(AutoFocusProperty, value); }

    // ===== checklists: a line that starts with ☐ (open) or ☑ (done) =====
    // Kept as ordinary text, so checklists survive saving, copy & paste, search and previews.

    public const char OpenBox = '☐';
    public const char DoneBox = '☑';
    private static readonly Color DoneTextColor = Color.FromRgb(0x8A, 0x8F, 0x98);
    private static readonly Color DoneBoxColor = Color.FromRgb(0x1E, 0x9E, 0x5A);
    private const double IndentStep = 24;

    /// <summary>The position just before the box of a checklist line, or null when the paragraph isn't one.</summary>
    private static TextPointer? BoxStart(Paragraph? p)
    {
        if (p == null) return null;
        var start = p.ContentStart.GetInsertionPosition(LogicalDirection.Forward);
        if (start.Paragraph != p) return null;
        var text = start.GetTextInRun(LogicalDirection.Forward);
        return text.Length > 0 && (text[0] == OpenBox || text[0] == DoneBox) ? start : null;
    }

    private static bool IsDone(TextPointer box) => box.GetTextInRun(LogicalDirection.Forward) is { Length: > 0 } t && t[0] == DoneBox;

    /// <summary>Where the item's own text starts (after the box and its space).</summary>
    private static TextPointer? ItemTextStart(Paragraph p)
    {
        if (BoxStart(p) is not { } box) return null;
        var after = box.GetNextInsertionPosition(LogicalDirection.Forward);
        if (after == null || after.Paragraph != p) return p.ContentEnd;
        if (after.GetTextInRun(LogicalDirection.Forward).StartsWith(' ') && after.GetNextInsertionPosition(LogicalDirection.Forward) is { } past && past.Paragraph == p)
            after = past;
        return after;
    }

    /// <summary>A checklist line for building documents in code (e.g. a checklist note copied to a board).</summary>
    public static Paragraph CheckItem(string text, bool done, int indent = 0)
    {
        var p = new Paragraph { Margin = new Thickness(Math.Clamp(indent, 0, 6) * IndentStep, 0, 0, 3) };
        var box = new Run((done ? DoneBox : OpenBox) + " ");
        var body = new Run(text);
        if (done)
        {
            box.Foreground = new SolidColorBrush(DoneBoxColor);
            body.TextDecorations = TextDecorations.Strikethrough;
            body.Foreground = new SolidColorBrush(DoneTextColor);
        }
        p.Inlines.Add(box);
        p.Inlines.Add(body);
        return p;
    }

    /// <summary>Checklist button / Ctrl+Shift+C: turns the selected lines into checklist items (or back into plain lines).</summary>
    public void ToggleChecklist()
    {
        var paragraphs = SelectedParagraphs().ToList();
        if (paragraphs.Count == 0)
        {
            var empty = new Paragraph();
            Rtb.Document.Blocks.Add(empty);
            paragraphs.Add(empty);
        }
        bool allItems = paragraphs.All(p => BoxStart(p) != null);
        foreach (var p in paragraphs)
        {
            if (allItems) RemoveBox(p);
            else if (BoxStart(p) == null) AddBox(p);
        }
        // Keep the caret after the box, never in front of it.
        if (Rtb.Selection.IsEmpty && Rtb.CaretPosition.Paragraph is { } current && ItemTextStart(current) is { } textStart
            && Rtb.CaretPosition.CompareTo(textStart) < 0)
            Rtb.CaretPosition = textStart;
        CleanSentinels(Rtb.Document);
        MarkDirty();
    }

    private IEnumerable<Paragraph> SelectedParagraphs()
    {
        var start = Rtb.Selection.Start.Paragraph;
        var end = Rtb.Selection.End.Paragraph ?? start;
        for (Block? block = start; block != null; block = block.NextBlock)
        {
            if (block is Paragraph p) yield return p;
            if (ReferenceEquals(block, end)) yield break;
        }
    }

    private static void AddBox(Paragraph p)
    {
        p.ContentStart.GetInsertionPosition(LogicalDirection.Forward).InsertTextInRun(OpenBox + " ");
        var m = p.Margin;
        p.Margin = new Thickness(m.Left, 0, 0, 3);
    }

    private static void RemoveBox(Paragraph p)
    {
        if (BoxStart(p) is not { } box || ItemTextStart(p) is not { } textStart) return;
        new TextRange(box, textStart).Text = "";
        var all = new TextRange(p.ContentStart, p.ContentEnd);
        all.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
        all.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(SentinelColor));
        p.ClearValue(Block.MarginProperty);
    }

    /// <summary>Ticks or unticks a checklist line: the box changes and the text is struck through.</summary>
    public void ToggleCheck(Paragraph p)
    {
        if (BoxStart(p) is not { } box || box.GetNextInsertionPosition(LogicalDirection.Forward) is not { } boxEnd) return;
        bool done = IsDone(box);
        var boxRange = new TextRange(box, boxEnd) { Text = (done ? OpenBox : DoneBox).ToString() };
        boxRange.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(done ? SentinelColor : DoneBoxColor));
        if (ItemTextStart(p) is { } textStart && textStart.CompareTo(p.ContentEnd) < 0)
        {
            var rest = new TextRange(textStart, p.ContentEnd);
            rest.ApplyPropertyValue(Inline.TextDecorationsProperty, done ? new TextDecorationCollection() : TextDecorations.Strikethrough);
            rest.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(done ? SentinelColor : DoneTextColor));
        }
        CleanSentinels(Rtb.Document);
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Checklist lines in this editor (for tests and counts).</summary>
    public IReadOnlyList<(string Text, bool Done)> ChecklistItems() =>
        Rtb.Document.Blocks.OfType<Paragraph>().Select(p => (p, box: BoxStart(p))).Where(x => x.box != null)
            .Select(x => (new TextRange(ItemTextStart(x.p)!, x.p.ContentEnd).Text.Trim(), IsDone(x.box!))).ToList();

    /// <summary>The checklist line whose box is under the point (editor coordinates), if any.</summary>
    private Paragraph? BoxAt(Point point)
    {
        var hit = Rtb.GetPositionFromPoint(point, snapToText: false);
        if (hit?.Paragraph is not { } p || BoxStart(p) is not { } box || box.GetNextInsertionPosition(LogicalDirection.Forward) is not { } end) return null;
        return hit.CompareTo(box) >= 0 && hit.CompareTo(end) <= 0 ? p : null;
    }

    private void OnEditorMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Rtb.IsReadOnly || e.ClickCount > 1 || BoxAt(e.GetPosition(Rtb)) is not { } p) return;
        ToggleCheck(p);
        e.Handled = true;
    }

    private void OnEditorMouseMove(object sender, MouseEventArgs e)
    {
        bool onBox = !Rtb.IsReadOnly && BoxAt(e.GetPosition(Rtb)) != null;
        if (onBox) Rtb.Cursor = Cursors.Hand;
        else if (Rtb.ReadLocalValue(CursorProperty) != DependencyProperty.UnsetValue) Rtb.ClearValue(CursorProperty);
    }

    /// <summary>Typing never lands in front of (or inside) a box.</summary>
    private void OnEditorTextInput(object sender, TextCompositionEventArgs e)
    {
        if (!Rtb.Selection.IsEmpty || Rtb.CaretPosition.Paragraph is not { } p || ItemTextStart(p) is not { } textStart) return;
        if (Rtb.CaretPosition.CompareTo(textStart) < 0) Rtb.CaretPosition = textStart;
    }

    /// <summary>Enter, Backspace, Tab and Home inside checklist lines. Returns true when handled.</summary>
    private bool HandleChecklistKey(KeyEventArgs e)
    {
        if (!Rtb.Selection.IsEmpty || Rtb.CaretPosition.Paragraph is not { } p || ItemTextStart(p) is not { } textStart) return false;
        var mods = Keyboard.Modifiers;
        bool atTextStart = Rtb.CaretPosition.CompareTo(textStart) <= 0;
        switch (e.Key)
        {
            case Key.Enter when mods == ModifierKeys.None:
                if (new TextRange(textStart, p.ContentEnd).Text.Trim().Length == 0) RemoveBox(p);   // Enter on an empty item ends the list
                else NewCheckItem();
                break;
            case Key.Back when mods == ModifierKeys.None && atTextStart:
                RemoveBox(p);
                break;
            case Key.Tab when mods is ModifierKeys.None or ModifierKeys.Shift:
                var m = p.Margin;
                p.Margin = new Thickness(Math.Clamp(m.Left + (mods == ModifierKeys.Shift ? -IndentStep : IndentStep), 0, 6 * IndentStep), m.Top, m.Right, m.Bottom);
                break;
            case Key.Home when mods == ModifierKeys.None && Rtb.CaretPosition.GetLineStartPosition(0) is { } lineStart && lineStart.CompareTo(textStart) <= 0:
                Rtb.CaretPosition = textStart;
                return true;
            case Key.Left when mods == ModifierKeys.None && atTextStart:
                if (p.ContentStart.GetNextInsertionPosition(LogicalDirection.Backward) is { } before) Rtb.CaretPosition = before;
                return true;
            default:
                return false;
        }
        CleanSentinels(Rtb.Document);
        MarkDirty();
        return true;
    }

    private void NewCheckItem()
    {
        EditingCommands.EnterParagraphBreak.Execute(null, Rtb);
        if (Rtb.CaretPosition.Paragraph is not { } next) return;
        // The new line starts open, whatever the line above was.
        var all = new TextRange(next.ContentStart, next.ContentEnd);
        all.ApplyPropertyValue(Inline.TextDecorationsProperty, new TextDecorationCollection());
        all.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(SentinelColor));
        AddBox(next);
        Rtb.CaretPosition = ItemTextStart(next) ?? next.ContentEnd;
    }

    private void OnChecklist(object sender, RoutedEventArgs e) => ToggleChecklist();

    // ----- public formatting API (used by floating toolbars, e.g. on board notes) -----

    public bool IsBold => Equals(Rtb.Selection.GetPropertyValue(TextElement.FontWeightProperty), FontWeights.Bold);
    public bool IsItalic => Equals(Rtb.Selection.GetPropertyValue(TextElement.FontStyleProperty), FontStyles.Italic);
    public bool IsUnderline => (Rtb.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection)?.Any(d => d.Location == TextDecorationLocation.Underline) == true;
    public bool IsStrike => (Rtb.Selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection)?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;

    public void ToggleBold() => Exec(EditingCommands.ToggleBold);
    public void ToggleItalic() => Exec(EditingCommands.ToggleItalic);
    public void ToggleUnderline() => Exec(EditingCommands.ToggleUnderline);
    public void ToggleStrike() => OnStrike(this, new RoutedEventArgs());
    public void ToggleBullets() => Exec(EditingCommands.ToggleBullets);
    public void ToggleNumbering() => Exec(EditingCommands.ToggleNumbering);
    public void SetHeading(int level) => ApplyHeading(level);
    public void ClearFormatting() => OnClearFormat(this, new RoutedEventArgs());
    public void ToggleCode() => OnCode(this, new RoutedEventArgs());

    public void SetTextColor(string? hex)
    {
        var sel = Rtb.Selection;
        if (hex == null)
        {
            sel.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(SentinelColor));
            CleanSentinels(Rtb.Document);
        }
        else sel.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(ColorUtil.Parse(hex)));
        MarkDirty();
    }

    public void SetHighlight(string? hex)
    {
        var sel = Rtb.Selection;
        if (hex == null)
        {
            sel.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(SentinelBackground));
            CleanSentinels(Rtb.Document);
        }
        else sel.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(ColorUtil.Parse(hex)));
        MarkDirty();
    }

    /// <summary>Inserts an image at the caret, scaled to fit the editor width.</summary>
    public void InsertImage(byte[] data)
    {
        var bitmap = Services.ImageService.ToBitmap(data);
        if (bitmap == null) return;
        double available = Math.Max(80, (Rtb.ActualWidth > 0 ? Rtb.ActualWidth : 600) - Rtb.Padding.Left - Rtb.Padding.Right - 16);
        var image = new Image { Source = bitmap, Stretch = Stretch.Uniform, Width = Math.Min(bitmap.Width, available) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        var position = Rtb.Selection.IsEmpty ? Rtb.CaretPosition : Rtb.Selection.Start;
        if (!Rtb.Selection.IsEmpty) Rtb.Selection.Text = "";
        var container = new InlineUIContainer(image, position.GetInsertionPosition(LogicalDirection.Forward));
        Rtb.CaretPosition = container.ElementEnd;
        Rtb.Focus();
        MarkDirty();
    }

    private void MarkDirty()
    {
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
        Rtb.Focus();
        UpdateToolbar();
    }

    private void OnPasting(object sender, DataObjectPastingEventArgs e)
    {
        var data = e.DataObject;
        // Rich formats (Word, web pages) paste normally; plain bitmaps and image files become inline images.
        if (data.GetDataPresent(DataFormats.Rtf) || data.GetDataPresent(DataFormats.Xaml) || data.GetDataPresent(DataFormats.XamlPackage)) return;
        if (!Services.ImageService.HasImage(data)) return;
        var images = Services.ImageService.FromDataObject(data, Services.ImageService.PhotoSize);
        if (images.Count == 0) return;
        e.CancelCommand();
        foreach (var img in images) InsertImage(img);
    }

    private void OnPreviewDragOver(object sender, DragEventArgs e)
    {
        if (!Rtb.IsReadOnly && Services.ImageService.HasImage(e.Data))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnPreviewDrop(object sender, DragEventArgs e)
    {
        if (Rtb.IsReadOnly || !Services.ImageService.HasImage(e.Data)) return;
        var images = Services.ImageService.FromDataObject(e.Data, Services.ImageService.PhotoSize);
        if (images.Count == 0) return;
        e.Handled = true;
        var pos = Rtb.GetPositionFromPoint(e.GetPosition(Rtb), true);
        if (pos != null) Rtb.CaretPosition = pos;
        foreach (var img in images) InsertImage(img);
    }

    public void FocusEditor()
    {
        Rtb.Focus();
        Rtb.CaretPosition = Rtb.Document.ContentEnd;
    }

    /// <summary>Writes pending edits to the bound properties immediately.</summary>
    public void Flush()
    {
        _debounce.Stop();
        if (!_dirty) return;
        _dirty = false;
        var (content, plain) = Serialize();
        _lastValue = content;
        PlainText = plain;
        ContentText = content;
    }

    private static void OnContentTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var editor = (RichTextEditor)d;
        var value = e.NewValue as string ?? "";
        if (value == editor._lastValue) return;
        editor._dirty = false;
        editor.Load(value);
    }

    private void Load(string? content)
    {
        _loading = true;
        try
        {
            var doc = new FlowDocument { PagePadding = new Thickness(0), TextAlignment = TextAlignment.Left };
            Rtb.Document = doc;
            if (!string.IsNullOrEmpty(content))
            {
                try
                {
                    var bytes = Convert.FromBase64String(content);
                    using var ms = new MemoryStream(bytes);
                    new TextRange(doc.ContentStart, doc.ContentEnd).Load(ms, DataFormats.XamlPackage);
                    CleanSentinels(doc);
                }
                catch
                {
                    doc.Blocks.Clear();
                    foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
                        doc.Blocks.Add(new Paragraph(new Run(line)));
                }
            }
            _lastValue = content ?? "";
        }
        finally
        {
            _loading = false;
        }
    }

    private (string Content, string Plain) Serialize() => SerializeDocument(Rtb.Document);

    /// <summary>Loads stored rich text into a new document (theme-neutral defaults removed), the same way the editor does.</summary>
    public static FlowDocument LoadDocument(string? content)
    {
        var doc = new FlowDocument();
        if (string.IsNullOrEmpty(content)) return doc;
        try
        {
            using var ms = new MemoryStream(Convert.FromBase64String(content));
            new TextRange(doc.ContentStart, doc.ContentEnd).Load(ms, DataFormats.XamlPackage);
            CleanSentinels(doc);
        }
        catch
        {
            doc.Blocks.Clear();
            foreach (var line in content.Replace("\r\n", "\n").Split('\n'))
                doc.Blocks.Add(new Paragraph(new Run(line)));
        }
        return doc;
    }

    /// <summary>Serializes a FlowDocument the same way the editor does (theme-neutral defaults).</summary>
    public static (string Content, string Plain) SerializeDocument(FlowDocument doc)
    {
        var plain = CleanPlain(new TextRange(doc.ContentStart, doc.ContentEnd).Text);
        if (plain.Length == 0 && !HasEmbeddedObjects(doc)) return ("", "");
        doc.TextAlignment = TextAlignment.Left;

        doc.Foreground = new SolidColorBrush(SentinelColor);
        doc.FontFamily = new FontFamily(SentinelFont);
        doc.FontSize = SentinelSize;
        try
        {
            using var ms = new MemoryStream();
            new TextRange(doc.ContentStart, doc.ContentEnd).Save(ms, DataFormats.XamlPackage);
            return (Convert.ToBase64String(ms.ToArray()), plain);
        }
        finally
        {
            doc.ClearValue(TextElement.ForegroundProperty);
            doc.ClearValue(TextElement.FontFamilyProperty);
            doc.ClearValue(TextElement.FontSizeProperty);
        }
    }

    /// <summary>List markers come through as a bullet plus tabs — tidy them for previews and search.</summary>
    private static string CleanPlain(string text)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"[ \t]*\t+[ \t]*", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"(\r?\n){3,}", "\n\n");
        return cleaned.Trim();
    }

    private static bool HasEmbeddedObjects(FlowDocument doc) =>
        AllElements(doc).Any(e => e is InlineUIContainer or BlockUIContainer);

    private static void CleanSentinels(FlowDocument doc)
    {
        Clean(doc);
        foreach (var e in AllElements(doc).ToList()) Clean(e);
    }

    private static void Clean(DependencyObject e)
    {
        if (e.ReadLocalValue(TextElement.ForegroundProperty) is SolidColorBrush fg && fg.Color == SentinelColor) e.ClearValue(TextElement.ForegroundProperty);
        if (e.ReadLocalValue(TextElement.BackgroundProperty) is SolidColorBrush bg && (bg.Color == SentinelBackground || bg.Color.A == 0)) e.ClearValue(TextElement.BackgroundProperty);
        if (e.ReadLocalValue(TextElement.FontFamilyProperty) is FontFamily ff && ff.Source == SentinelFont) e.ClearValue(TextElement.FontFamilyProperty);
        if (e.ReadLocalValue(TextElement.FontSizeProperty) is double size && Math.Abs(size - SentinelSize) < 0.01) e.ClearValue(TextElement.FontSizeProperty);
        if (e is Block or FlowDocument && e.ReadLocalValue(Block.TextAlignmentProperty) is TextAlignment.Justify) e.ClearValue(Block.TextAlignmentProperty);
        if (e is FlowDocument d)
        {
            if (d.ReadLocalValue(FlowDocument.BackgroundProperty) is SolidColorBrush db && db.Color.A == 0) d.ClearValue(FlowDocument.BackgroundProperty);
        }
    }

    private static IEnumerable<TextElement> AllElements(FlowDocument doc)
    {
        foreach (var block in doc.Blocks.ToList())
            foreach (var e in Walk(block))
                yield return e;
    }

    private static IEnumerable<TextElement> Walk(TextElement element)
    {
        yield return element;
        IEnumerable<TextElement> children = element switch
        {
            Paragraph p => p.Inlines,
            Span s => s.Inlines,
            List l => l.ListItems,
            ListItem li => li.Blocks,
            Section sec => sec.Blocks,
            Table t => t.RowGroups.SelectMany(g => g.Rows).SelectMany(r => r.Cells).SelectMany(c => c.Blocks),
            _ => []
        };
        foreach (var child in children.ToList())
            foreach (var e in Walk(child))
                yield return e;
    }

    private void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        UpdateToolbar();
        FormattingChanged?.Invoke();
    }

    private void UpdateToolbar()
    {
        var sel = Rtb.Selection;
        BoldBtn.IsChecked = Equals(sel.GetPropertyValue(TextElement.FontWeightProperty), FontWeights.Bold);
        ItalicBtn.IsChecked = Equals(sel.GetPropertyValue(TextElement.FontStyleProperty), FontStyles.Italic);
        var deco = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        UnderlineBtn.IsChecked = deco?.Any(d => d.Location == TextDecorationLocation.Underline) == true;
        StrikeBtn.IsChecked = deco?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
        CodeBtn.IsChecked = sel.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily f && IsMono(f);
    }

    private static bool IsMono(FontFamily f) => f.Source.Contains("Mono", StringComparison.OrdinalIgnoreCase) || f.Source.Contains("Consolas", StringComparison.OrdinalIgnoreCase);

    private void OnEditorKeyDown(object sender, KeyEventArgs e)
    {
        if (Rtb.IsReadOnly) return;
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C) { ToggleChecklist(); e.Handled = true; }
        else if (HandleChecklistKey(e)) e.Handled = true;
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.X) { OnStrike(this, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D1) { ApplyHeading(1); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D2) { ApplyHeading(2); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D0) { ApplyHeading(0); e.Handled = true; }
    }

    private void Exec(RoutedUICommand command)
    {
        command.Execute(null, Rtb);
        Rtb.Focus();
        UpdateToolbar();
    }

    private void OnBold(object sender, RoutedEventArgs e) => Exec(EditingCommands.ToggleBold);
    private void OnItalic(object sender, RoutedEventArgs e) => Exec(EditingCommands.ToggleItalic);
    private void OnUnderline(object sender, RoutedEventArgs e) => Exec(EditingCommands.ToggleUnderline);
    private void OnBullets(object sender, RoutedEventArgs e) => Exec(EditingCommands.ToggleBullets);
    private void OnNumbering(object sender, RoutedEventArgs e) => Exec(EditingCommands.ToggleNumbering);

    private void OnStrike(object sender, RoutedEventArgs e)
    {
        var sel = Rtb.Selection;
        var deco = sel.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
        bool has = deco?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;
        sel.ApplyPropertyValue(Inline.TextDecorationsProperty, has ? new TextDecorationCollection() : TextDecorations.Strikethrough);
        Rtb.Focus();
        UpdateToolbar();
    }

    private void OnHeading(object sender, RoutedEventArgs e) =>
        ApplyHeading(int.Parse((string)((FrameworkElement)sender).Tag, System.Globalization.CultureInfo.InvariantCulture));

    private void ApplyHeading(int level)
    {
        var start = Rtb.Selection.Start.Paragraph;
        var end = Rtb.Selection.End.Paragraph;
        Block? block = start;
        while (block != null)
        {
            if (block is Paragraph p)
            {
                foreach (var inline in p.Inlines.ToList())
                {
                    inline.ClearValue(TextElement.FontSizeProperty);
                    inline.ClearValue(TextElement.FontWeightProperty);
                }
                switch (level)
                {
                    case 1: p.FontSize = 24; p.FontWeight = FontWeights.Bold; break;
                    case 2: p.FontSize = 19; p.FontWeight = FontWeights.SemiBold; break;
                    default: p.ClearValue(TextElement.FontSizeProperty); p.ClearValue(TextElement.FontWeightProperty); break;
                }
            }
            if (block == end) break;
            block = block.NextBlock;
        }
        Rtb.Focus();
        _dirty = true;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnCode(object sender, RoutedEventArgs e)
    {
        var sel = Rtb.Selection;
        bool isMono = sel.GetPropertyValue(TextElement.FontFamilyProperty) is FontFamily f && IsMono(f);
        if (isMono)
        {
            sel.ApplyPropertyValue(TextElement.FontFamilyProperty, new FontFamily(SentinelFont));
            sel.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(SentinelBackground));
            CleanSentinels(Rtb.Document);
        }
        else
        {
            sel.ApplyPropertyValue(TextElement.FontFamilyProperty, (FontFamily)FindResource("MonoFont"));
            sel.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(Color.FromArgb(0x30, 0x80, 0x80, 0x80)));
        }
        Rtb.Focus();
        UpdateToolbar();
    }

    private void OnClearFormat(object sender, RoutedEventArgs e)
    {
        Rtb.Selection.ClearAllProperties();
        Rtb.Focus();
        UpdateToolbar();
    }

    private void OnColorMenu(object sender, RoutedEventArgs e) => ColorPopup.IsOpen = true;

    private static readonly (string Name, string? Hex)[] TextPalette =
    [
        ("Default", null), ("Red", "#E5484D"), ("Orange", "#F76B15"), ("Amber", "#E2A400"), ("Green", "#30A46C"),
        ("Teal", "#12A594"), ("Sky", "#0091E6"), ("Blue", "#2F80ED"), ("Rose", "#E54666"), ("Gray", "#8B8D98")
    ];

    private static readonly (string Name, string? Hex)[] HighlightPalette =
    [
        ("None", null), ("Yellow", "#66FFD84D"), ("Green", "#5577DD88"), ("Blue", "#5566B8FF"), ("Pink", "#55FF7AA8"), ("Orange", "#55FFA94D")
    ];

    private void BuildColorButtons()
    {
        foreach (var (name, hex) in TextPalette)
            TextColors.Children.Add(MakeSwatch(name, hex, foreground: true));
        foreach (var (name, hex) in HighlightPalette)
            HighlightColors.Children.Add(MakeSwatch(name, hex, foreground: false));
    }

    private FrameworkElement MakeSwatch(string name, string? hex, bool foreground)
    {
        var grid = new Grid { Width = 26, Height = 26, Margin = new Thickness(0, 0, 6, 6), Cursor = Cursors.Hand, ToolTip = name, Background = Brushes.Transparent };
        var ellipse = new Ellipse { StrokeThickness = 1 };
        ellipse.SetResourceReference(Shape.StrokeProperty, "StrokeStrongBrush");
        if (hex != null && ColorUtil.TryParse(hex, out var c)) ellipse.Fill = new SolidColorBrush(c);
        else
        {
            ellipse.SetResourceReference(Shape.FillProperty, foreground ? "TextBrush" : "SurfaceBrush");
            if (!foreground)
                grid.Children.Add(new Line { X1 = 7, Y1 = 19, X2 = 19, Y2 = 7, StrokeThickness = 1.5, Stroke = Brushes.IndianRed });
        }
        grid.Children.Insert(0, ellipse);
        grid.MouseLeftButtonUp += (_, _) =>
        {
            var sel = Rtb.Selection;
            if (foreground)
            {
                if (hex == null)
                {
                    sel.ApplyPropertyValue(TextElement.ForegroundProperty, new SolidColorBrush(SentinelColor));
                    CleanSentinels(Rtb.Document);
                    ColorBar.SetResourceReference(Border.BackgroundProperty, "TextBrush");
                }
                else
                {
                    var brush = new SolidColorBrush(ColorUtil.Parse(hex));
                    sel.ApplyPropertyValue(TextElement.ForegroundProperty, brush);
                    ColorBar.Background = brush;
                }
            }
            else
            {
                if (hex == null)
                {
                    sel.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(SentinelBackground));
                    CleanSentinels(Rtb.Document);
                }
                else sel.ApplyPropertyValue(TextElement.BackgroundProperty, new SolidColorBrush(ColorUtil.Parse(hex)));
            }
            ColorPopup.IsOpen = false;
            Rtb.Focus();
            _dirty = true;
            _debounce.Stop();
            _debounce.Start();
        };
        return grid;
    }
}
