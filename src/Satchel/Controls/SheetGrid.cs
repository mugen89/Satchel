using System.Collections;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Satchel.Services;
using Satchel.Services.Sheets;
using Satchel.ViewModels;

namespace Satchel.Controls;

/// <summary>
/// The spreadsheet grid. It draws only the cells on screen (so big sheets scroll smoothly), edits in place, selects
/// cells, rows and columns, resizes columns, keeps frozen rows on top, and lets you click cells into a formula.
/// Put it in a ScrollViewer with CanContentScroll="True".
/// </summary>
public sealed class SheetGrid : FrameworkElement, IScrollInfo
{
    public const double RowHeight = 24, HeaderHeight = 24, Padding = 6;

    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(nameof(Document), typeof(SheetDocument), typeof(SheetGrid),
        new PropertyMetadata(null, (d, e) => ((SheetGrid)d).OnDocumentChanged(e.OldValue as SheetDocument, e.NewValue as SheetDocument)));

    public SheetDocument? Document
    {
        get => (SheetDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    /// <summary>The in-cell editor's text while it changes (for the formula bar).</summary>
    public event Action<string>? EditTextChanged;
    public event Action? EditEnded;

    private readonly TextBox _editor;
    private bool _editing, _editMode, _refDrag;
    private int _editRow, _editCol, _refStart, _refLength, _refAnchorRow, _refAnchorCol;
    private double[] _colX = new double[27];
    private int _rows = 200, _cols = 26, _frozen;
    private double _rowHeaderWidth = 46;
    private Size _viewport;
    private Vector _offset;
    private readonly DispatcherTimer _autoScroll;
    private Point _lastMouse;
    private enum Drag { None, Cells, Columns, Rows, Resize }
    private Drag _drag;
    private int _resizeCol;
    private double _resizeStart;
    private double? _resizeWidthBefore;
    private readonly Dictionary<(string, int, Brush), FormattedText> _textCache = [];

    public SheetGrid()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        RenderOptions.SetEdgeMode(this, EdgeMode.Aliased);
        _editor = new TextBox
        {
            Visibility = Visibility.Collapsed,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(Padding - 2, 2, 4, 2),
            VerticalContentAlignment = VerticalAlignment.Center,
            AcceptsReturn = true,
            AcceptsTab = false,
            FontSize = 13,
            FocusVisualStyle = null,
        };
        _editor.SetResourceReference(StyleProperty, "InlineTextBox");
        _editor.SetResourceReference(Control.BackgroundProperty, "ElevatedBrush");
        _editor.SetResourceReference(Control.ForegroundProperty, "TextBrush");
        _editor.SetResourceReference(Control.FontFamilyProperty, "UiFont");
        _editor.TextChanged += (_, _) =>
        {
            if (!_editing) return;
            EditTextChanged?.Invoke(_editor.Text);
            InvalidateArrange();
        };
        _editor.LostKeyboardFocus += (_, e) =>
        {
            if (_editing && !_refDrag && !ReferenceEquals(e.NewFocus, this) && e.NewFocus is not System.Windows.Controls.ContextMenu && e.NewFocus is not System.Windows.Controls.MenuItem) CommitEdit(refocus: false);
        };
        AddVisualChild(_editor);
        AddLogicalChild(_editor);
        _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
        _autoScroll.Tick += (_, _) => AutoScrollTick();
        Loaded += (_, _) => ThemeState.Instance.PropertyChanged += OnTheme;
        Unloaded += (_, _) => ThemeState.Instance.PropertyChanged -= OnTheme;
        ComputeColumns();
    }

    private void OnTheme(object? sender, PropertyChangedEventArgs e)
    {
        _textCache.Clear();
        InvalidateVisual();
    }

    private void OnDocumentChanged(SheetDocument? old, SheetDocument? doc)
    {
        if (old != null)
        {
            old.Changed -= OnChanged;
            old.SelectionChanged -= OnSelectionChanged;
            old.RevealRequested -= EnsureActiveVisible;
        }
        if (_editing) CancelEdit();
        _offset = default;
        _rows = 200;
        _cols = 26;
        if (doc != null)
        {
            doc.Changed += OnChanged;
            doc.SelectionChanged += OnSelectionChanged;
            doc.RevealRequested += EnsureActiveVisible;
        }
        OnChanged();
    }

    private void OnChanged()
    {
        _textCache.Clear();
        ComputeColumns();
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
        InvalidateArrange();
    }

    private void OnSelectionChanged()
    {
        if (Document is { } doc && (doc.ActiveRow + 30 > _rows || doc.ActiveCol + 6 > _cols)) ComputeColumns();
        InvalidateVisual();
    }

    // ===== layout =====

    private Sheet? Sheet => Document?.Sheet;

    private void ComputeColumns()
    {
        var sheet = Sheet;
        int usedRows = sheet?.UsedRows ?? 0, usedCols = sheet?.UsedCols ?? 0;
        _rows = Math.Max(_rows, Math.Max(200, Math.Max(usedRows + 60, (Document?.ActiveRow ?? 0) + 40)));
        if (sheet == null) _rows = 200;
        _cols = Math.Clamp(Math.Max(26, Math.Max(usedCols + 6, (Document?.ActiveCol ?? 0) + 6)), 26, CellRef.MaxCols);
        _frozen = Math.Min(sheet?.FrozenRows ?? 0, _rows);
        if (_colX.Length != _cols + 1) _colX = new double[_cols + 1];
        double x = 0;
        for (int c = 0; c < _cols; c++)
        {
            _colX[c] = x;
            x += sheet?.Width(c) ?? Sheet.DefaultWidth;
        }
        _colX[_cols] = x;
        int digits = Math.Max(2, ((int)Math.Log10(Math.Max(1, _rows)) + 1));
        _rowHeaderWidth = Math.Max(42, 16 + digits * 8);
    }

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _editor;
    protected override IEnumerator LogicalChildren => new object[] { _editor }.GetEnumerator();

    protected override Size MeasureOverride(Size available)
    {
        _editor.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return new Size(double.IsInfinity(available.Width) ? 800 : available.Width, double.IsInfinity(available.Height) ? 500 : available.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        if (_viewport != final)
        {
            _viewport = final;
            ClampOffset();
            ScrollOwner?.InvalidateScrollInfo();
        }
        _editor.Arrange(_editing ? EditorRect() : new Rect(0, 0, 0, 0));
        return final;
    }

    private Rect EditorRect()
    {
        var cell = CellRect(_editRow, _editCol);
        var ft = Text(_editor.Text.Length == 0 ? " " : _editor.Text, false, false, Brushes.Black);
        double width = Math.Max(cell.Width, Math.Min(ft.WidthIncludingTrailingWhitespace + Padding * 2 + 10, Math.Max(cell.Width, _viewport.Width - cell.X - 4)));
        int lines = Math.Max(1, _editor.LineCount);
        double height = Math.Max(cell.Height, lines * 18 + 6);
        return new Rect(cell.X + 1.5, cell.Y + 1.5, Math.Max(10, width - 3), Math.Max(10, height - 3));
    }

    private double FrozenBottom => HeaderHeight + _frozen * RowHeight;
    private double ColX(int c) => _rowHeaderWidth + _colX[Math.Clamp(c, 0, _cols)] - _offset.X;
    private double ColWidth(int c) => c < _cols ? _colX[c + 1] - _colX[c] : Sheet?.Width(c) ?? Sheet.DefaultWidth;
    private double RowY(int r) => r < _frozen ? HeaderHeight + r * RowHeight : HeaderHeight + r * RowHeight - _offset.Y;

    private Rect CellRect(int r, int c)
    {
        double x = c < _cols ? ColX(c) : ColX(_cols) + (c - _cols) * Sheet.DefaultWidth;
        return new Rect(x, RowY(r), ColWidth(c), RowHeight);
    }

    private int ColumnAt(double x)
    {
        double rel = x - _rowHeaderWidth + _offset.X;
        if (rel < 0) return 0;
        int lo = 0, hi = _cols - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_colX[mid] <= rel) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private int RowAt(double y)
    {
        if (y < FrozenBottom && _frozen > 0) return Math.Clamp((int)((y - HeaderHeight) / RowHeight), 0, _frozen - 1);
        return Math.Max(_frozen, (int)Math.Floor((y - HeaderHeight + _offset.Y) / RowHeight));
    }

    // ===== scrolling =====

    public bool CanVerticallyScroll { get; set; } = true;
    public bool CanHorizontallyScroll { get; set; } = true;
    public double ExtentWidth => _rowHeaderWidth + _colX[_cols] + 40;
    public double ExtentHeight => HeaderHeight + _rows * RowHeight + RowHeight;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset.Y - RowHeight);
    public void LineDown() => SetVerticalOffset(_offset.Y + RowHeight);
    public void LineLeft() => SetHorizontalOffset(_offset.X - 48);
    public void LineRight() => SetHorizontalOffset(_offset.X + 48);
    public void PageUp() => SetVerticalOffset(_offset.Y - (_viewport.Height - FrozenBottom - RowHeight));
    public void PageDown() => SetVerticalOffset(_offset.Y + (_viewport.Height - FrozenBottom - RowHeight));
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width * 0.8);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width * 0.8);
    public void MouseWheelUp() => SetVerticalOffset(_offset.Y - RowHeight * 3);
    public void MouseWheelDown() => SetVerticalOffset(_offset.Y + RowHeight * 3);
    public void MouseWheelLeft() => SetHorizontalOffset(_offset.X - 96);
    public void MouseWheelRight() => SetHorizontalOffset(_offset.X + 96);
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    public void SetHorizontalOffset(double offset)
    {
        var x = Math.Clamp(offset, 0, Math.Max(0, ExtentWidth - _viewport.Width));
        if (Math.Abs(x - _offset.X) < 0.01) return;
        // Near the right edge: more columns appear, so the sheet feels endless.
        if (x + _viewport.Width > ExtentWidth - 200 && _cols < CellRef.MaxCols) { _cols = Math.Min(CellRef.MaxCols, _cols + 10); _colX = new double[_cols + 1]; ComputeColumns(); }
        _offset.X = x;
        Scrolled();
    }

    public void SetVerticalOffset(double offset)
    {
        if (offset + _viewport.Height > ExtentHeight - RowHeight * 20 && _rows < CellRef.MaxRows) _rows = Math.Min(CellRef.MaxRows, _rows + 200);
        var y = Math.Clamp(Math.Round(offset / RowHeight) * RowHeight, 0, Math.Max(0, ExtentHeight - _viewport.Height));
        if (Math.Abs(y - _offset.Y) < 0.01) return;
        _offset.Y = y;
        Scrolled();
    }

    private void Scrolled()
    {
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
        InvalidateArrange();
    }

    private void ClampOffset()
    {
        _offset.X = Math.Clamp(_offset.X, 0, Math.Max(0, ExtentWidth - _viewport.Width));
        _offset.Y = Math.Clamp(_offset.Y, 0, Math.Max(0, ExtentHeight - _viewport.Height));
    }

    /// <summary>Scrolls so the active cell is fully in view.</summary>
    public void EnsureActiveVisible()
    {
        if (Document is not { } doc || _viewport.Width <= 0) return;
        int r = doc.ActiveRow, c = doc.ActiveCol;
        if (c + 2 > _cols || r + 20 > _rows) ComputeColumns();
        if (r >= _frozen)
        {
            double top = HeaderHeight + r * RowHeight;
            if (top - _offset.Y < FrozenBottom) SetVerticalOffset(top - FrozenBottom);
            else if (top + RowHeight - _offset.Y > _viewport.Height) SetVerticalOffset(top + RowHeight - _viewport.Height + 2);
        }
        double left = _rowHeaderWidth + _colX[Math.Min(c, _cols)];
        double right = left + ColWidth(c);
        if (left - _offset.X < _rowHeaderWidth) SetHorizontalOffset(left - _rowHeaderWidth);
        else if (right - _offset.X > _viewport.Width) SetHorizontalOffset(right - _viewport.Width + 2);
        InvalidateVisual();
    }

    // ===== drawing =====

    private Brush Res(string key, Color fallback) => TryFindResource(key) as Brush ?? new SolidColorBrush(fallback);

    private Typeface Face(bool bold, bool italic)
    {
        var family = TryFindResource("UiFont") as FontFamily ?? new FontFamily("Segoe UI");
        return new Typeface(family, italic ? FontStyles.Italic : FontStyles.Normal, bold ? FontWeights.SemiBold : FontWeights.Normal, FontStretches.Normal);
    }

    private FormattedText Text(string text, bool bold, bool italic, Brush brush, double size = 13)
    {
        var key = (text, (bold ? 1 : 0) | (italic ? 2 : 0) | ((int)size << 2), brush);
        if (_textCache.TryGetValue(key, out var cached)) return cached;
        var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Face(bold, italic), size, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        if (_textCache.Count > 6000) _textCache.Clear();
        _textCache[key] = ft;
        return ft;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = RenderSize;
        var surface = Res("SurfaceBrush", Colors.White);
        dc.DrawRectangle(surface, null, new Rect(size));
        if (Document is not { } doc) return;
        var sheet = doc.Sheet;
        if (_frozen != Math.Min(sheet.FrozenRows, _rows)) ComputeColumns();

        var textBrush = Res("TextBrush", Colors.Black);
        var muted = Res("TextMutedBrush", Colors.Gray);
        var stroke = Res("StrokeBrush", Colors.LightGray);
        var headerBg = Res("InputBrush", Color.FromRgb(0xF4, 0xF5, 0xF7));
        var accent = Res("AccentBrush", Colors.Teal);
        var accentText = Res("AccentTextBrush", Colors.Teal);
        var danger = Res("DangerBrush", Colors.IndianRed);
        var accentColor = accent is SolidColorBrush sb ? sb.Color : Colors.Teal;
        var fill = new SolidColorBrush(Color.FromArgb(ThemeState.Instance.IsDark ? (byte)46 : (byte)30, accentColor.R, accentColor.G, accentColor.B));
        var headerSel = new SolidColorBrush(Color.FromArgb(ThemeState.Instance.IsDark ? (byte)70 : (byte)44, accentColor.R, accentColor.G, accentColor.B));
        var gridPen = new Pen(stroke, 1);
        var frozenPen = new Pen(Res("StrokeStrongBrush", Colors.Gray), 2);

        int firstCol = ColumnAt(_rowHeaderWidth), lastCol = firstCol;
        while (lastCol < _cols - 1 && ColX(lastCol + 1) < size.Width) lastCol++;
        int firstRow = _frozen + (int)(_offset.Y / RowHeight), lastRow = firstRow;
        while (lastRow < _rows - 1 && RowY(lastRow + 1) < size.Height) lastRow++;

        var (s1, sc1, s2, sc2) = doc.Selection;
        var cellArea = new Rect(_rowHeaderWidth, HeaderHeight, Math.Max(0, size.Width - _rowHeaderWidth), Math.Max(0, size.Height - HeaderHeight));

        void Region(int r1, int r2, Rect clip)
        {
            if (r2 < r1 || clip.Height <= 0) return;
            dc.PushClip(new RectangleGeometry(clip));
            // selection fill (the active cell stays clear so its contents read well)
            int fr1 = Math.Max(s1, r1), fr2 = Math.Min(s2, r2);
            if (fr1 <= fr2 && doc.IsMultiple)
            {
                var rect = Union(CellRect(fr1, sc1), CellRect(fr2, sc2));
                dc.DrawRectangle(fill, null, rect);
                if (doc.ActiveRow >= r1 && doc.ActiveRow <= r2) dc.DrawRectangle(surface, null, CellRect(doc.ActiveRow, doc.ActiveCol));
            }
            // grid lines
            double bottom = Math.Min(clip.Bottom, RowY(r2) + RowHeight);
            for (int c = firstCol; c <= lastCol + 1 && c <= _cols; c++)
            {
                double x = Math.Floor(ColX(c)) + 0.5;
                dc.DrawLine(gridPen, new Point(x, clip.Top), new Point(x, bottom));
            }
            double right = Math.Min(clip.Right, ColX(lastCol + 1));
            for (int r = r1; r <= r2 + 1; r++)
            {
                double y = Math.Floor(RowY(r)) + 0.5;
                if (y < clip.Top - 1 || y > clip.Bottom + 1) continue;
                dc.DrawLine(gridPen, new Point(clip.Left, y), new Point(right, y));
            }
            // contents
            for (int r = r1; r <= r2; r++)
            {
                for (int c = firstCol; c <= lastCol; c++)
                {
                    if (sheet.Get(r, c) is not { } cell || cell.Display.Length == 0) continue;
                    if (_editing && r == _editRow && c == _editCol) continue;
                    DrawCell(dc, sheet, cell, r, c, lastCol, textBrush, danger, surface);
                }
            }
            // selection border, active cell, copy marquee
            if (fr1 <= fr2)
            {
                var rect = Union(CellRect(fr1, sc1), CellRect(fr2, sc2));
                if (doc.IsMultiple) dc.DrawRectangle(null, new Pen(accent, 1.5), Inflate(rect, -0.5));
            }
            if (!_editing && doc.ActiveRow >= r1 && doc.ActiveRow <= r2)
                dc.DrawRectangle(null, new Pen(accent, 2), Inflate(CellRect(doc.ActiveRow, doc.ActiveCol), -1));
            if (_editing && _editRow >= r1 && _editRow <= r2)
            {
                var er = EditorRect();
                dc.DrawRectangle(Res("ElevatedBrush", Colors.White), new Pen(accent, 2), new Rect(er.X - 1.5, er.Y - 1.5, er.Width + 3, er.Height + 3));
            }
            if (doc.CopyRange is { } cr && cr.R2 >= r1 && cr.R1 <= r2)
            {
                var rect = Union(CellRect(Math.Max(cr.R1, r1), cr.C1), CellRect(Math.Min(cr.R2, r2), cr.C2));
                dc.DrawRectangle(null, new Pen(accent, 1.5) { DashStyle = new DashStyle([3, 2], 0) }, Inflate(rect, -1.5));
            }
            dc.Pop();
        }

        Region(firstRow, lastRow, new Rect(cellArea.X, FrozenBottom, cellArea.Width, Math.Max(0, size.Height - FrozenBottom)));
        if (_frozen > 0)
        {
            Region(0, _frozen - 1, new Rect(cellArea.X, HeaderHeight, cellArea.Width, _frozen * RowHeight));
            dc.DrawLine(frozenPen, new Point(0, FrozenBottom), new Point(size.Width, FrozenBottom));
        }

        // column headers
        dc.PushClip(new RectangleGeometry(new Rect(_rowHeaderWidth, 0, Math.Max(0, size.Width - _rowHeaderWidth), HeaderHeight)));
        dc.DrawRectangle(headerBg, null, new Rect(0, 0, size.Width, HeaderHeight));
        bool wholeColumns = s1 == 0 && s2 >= 999;
        for (int c = firstCol; c <= lastCol; c++)
        {
            var rect = new Rect(ColX(c), 0, ColWidth(c), HeaderHeight);
            bool selected = c >= sc1 && c <= sc2;
            if (selected) dc.DrawRectangle(wholeColumns ? fill : headerSel, null, rect);
            var label = Text(CellRef.ColumnName(c), selected, false, selected ? accentText : muted, 11.5);
            dc.DrawText(label, new Point(rect.X + (rect.Width - label.Width) / 2, (HeaderHeight - label.Height) / 2));
            double x = Math.Floor(rect.Right) + 0.5;
            dc.DrawLine(gridPen, new Point(x, 0), new Point(x, HeaderHeight));
            if (selected && wholeColumns) dc.DrawLine(new Pen(accent, 2), new Point(rect.Left, HeaderHeight - 1), new Point(rect.Right, HeaderHeight - 1));
        }
        dc.Pop();
        dc.DrawLine(gridPen, new Point(0, HeaderHeight - 0.5), new Point(size.Width, HeaderHeight - 0.5));

        // row headers
        dc.PushClip(new RectangleGeometry(new Rect(0, HeaderHeight, _rowHeaderWidth, Math.Max(0, size.Height - HeaderHeight))));
        dc.DrawRectangle(headerBg, null, new Rect(0, HeaderHeight, _rowHeaderWidth, size.Height));
        bool wholeRows = sc1 == 0 && sc2 >= 25 && doc.IsMultiple && s2 - s1 < 999;
        void RowHeaders(int r1, int r2, double clipTop, double clipBottom)
        {
            for (int r = r1; r <= r2; r++)
            {
                double y = RowY(r);
                if (y + RowHeight < clipTop || y > clipBottom) continue;
                var rect = new Rect(0, y, _rowHeaderWidth, RowHeight);
                bool selected = r >= s1 && r <= s2;
                if (selected) dc.DrawRectangle(wholeRows ? fill : headerSel, null, rect);
                var label = Text((r + 1).ToString(CultureInfo.InvariantCulture), selected, false, selected ? accentText : muted, 11.5);
                dc.DrawText(label, new Point(rect.Right - label.Width - 8, y + (RowHeight - label.Height) / 2));
                double ly = Math.Floor(rect.Bottom) + 0.5;
                dc.DrawLine(gridPen, new Point(0, ly), new Point(_rowHeaderWidth, ly));
            }
        }
        dc.PushClip(new RectangleGeometry(new Rect(0, FrozenBottom, _rowHeaderWidth, Math.Max(0, size.Height - FrozenBottom))));
        RowHeaders(firstRow, lastRow, FrozenBottom, size.Height);
        dc.Pop();
        if (_frozen > 0) RowHeaders(0, _frozen - 1, HeaderHeight, FrozenBottom);
        dc.Pop();
        dc.DrawLine(gridPen, new Point(_rowHeaderWidth - 0.5, 0), new Point(_rowHeaderWidth - 0.5, size.Height));

        // corner (click: select everything)
        dc.DrawRectangle(headerBg, null, new Rect(0, 0, _rowHeaderWidth, HeaderHeight));
        var tri = new StreamGeometry();
        using (var ctx = tri.Open())
        {
            ctx.BeginFigure(new Point(_rowHeaderWidth - 5, HeaderHeight - 13), true, true);
            ctx.LineTo(new Point(_rowHeaderWidth - 5, HeaderHeight - 5), true, false);
            ctx.LineTo(new Point(_rowHeaderWidth - 13, HeaderHeight - 5), true, false);
        }
        dc.DrawGeometry(Res("TextFaintBrush", Colors.Gray), null, tri);
        dc.DrawLine(gridPen, new Point(0, HeaderHeight - 0.5), new Point(_rowHeaderWidth, HeaderHeight - 0.5));
    }

    private void DrawCell(DrawingContext dc, Sheet sheet, Cell cell, int r, int c, int lastCol, Brush textBrush, Brush danger, Brush surface)
    {
        var rect = CellRect(r, c);
        var value = cell.Value;
        bool bold = cell.Style.HasFlag(CellStyle.Bold), italic = cell.Style.HasFlag(CellStyle.Italic);
        var align = cell.Style & CellStyle.AlignMask;
        if (align == CellStyle.None)
            align = value.Kind switch { XKind.Number => CellStyle.Right, XKind.Bool or XKind.Error => CellStyle.Center, _ => CellStyle.Left };
        var text = cell.Display.Contains('\n') ? cell.Display.Replace("\r", "").Replace('\n', ' ') : cell.Display;
        var ft = Text(text, bold, italic, value.IsError ? danger : textBrush);
        double available = rect.Width - Padding * 2;
        var clip = rect;
        if (ft.WidthIncludingTrailingWhitespace > available)
        {
            if (value.IsNumber)
            {
                // A number that doesn't fit shows ####, like in Excel (widen the column to see it).
                int hashes = Math.Max(1, (int)(available / Text("#", false, false, textBrush).Width));
                ft = Text(new string('#', hashes), false, false, textBrush);
            }
            else if (align == CellStyle.Left)
            {
                // Text runs on over empty neighbours.
                int next = c + 1;
                double extra = 0;
                while (next <= lastCol + 1 && sheet.Get(r, next) is null or { IsBlank: true } && ft.WidthIncludingTrailingWhitespace > available + extra)
                {
                    extra += ColWidth(next);
                    next++;
                }
                if (extra > 0)
                {
                    clip = new Rect(rect.X, rect.Y, rect.Width + extra, rect.Height);
                    dc.DrawRectangle(surface, null, new Rect(rect.Right, rect.Y + 1, extra - 1, rect.Height - 1));
                }
            }
        }
        double x = align switch
        {
            CellStyle.Right => rect.Right - Padding - ft.WidthIncludingTrailingWhitespace,
            CellStyle.Center => rect.X + (rect.Width - ft.WidthIncludingTrailingWhitespace) / 2,
            _ => rect.X + Padding
        };
        double y = rect.Y + (RowHeight - ft.Height) / 2;
        dc.PushClip(new RectangleGeometry(new Rect(clip.X + 1, clip.Y + 1, Math.Max(0, clip.Width - 2), Math.Max(0, clip.Height - 1))));
        dc.DrawText(ft, new Point(x, y));
        if (cell.Style.HasFlag(CellStyle.Strike))
            dc.DrawLine(new Pen(ft.Height > 0 ? textBrush : textBrush, 1), new Point(x, y + ft.Height / 2 + 1), new Point(x + ft.WidthIncludingTrailingWhitespace, y + ft.Height / 2 + 1));
        dc.Pop();
    }

    private static Rect Union(Rect a, Rect b) => Rect.Union(a, b);
    private static Rect Inflate(Rect r, double d) => new(r.X - d, r.Y - d, Math.Max(0, r.Width + d * 2), Math.Max(0, r.Height + d * 2));

    // ===== mouse =====

    private enum Zone { Corner, ColumnHeader, RowHeader, Cell }

    private Zone ZoneAt(Point p) =>
        p.X < _rowHeaderWidth && p.Y < HeaderHeight ? Zone.Corner : p.Y < HeaderHeight ? Zone.ColumnHeader : p.X < _rowHeaderWidth ? Zone.RowHeader : Zone.Cell;

    /// <summary>The column whose right border is under the pointer in the header (to resize), or -1.</summary>
    private int ResizeEdgeAt(Point p)
    {
        if (p.Y >= HeaderHeight || p.X < _rowHeaderWidth) return -1;
        int c = ColumnAt(p.X);
        double right = ColX(c + 1), left = ColX(c);
        if (Math.Abs(p.X - right) <= 4) return c;
        if (Math.Abs(p.X - left) <= 4 && c > 0) return c - 1;
        return -1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Document is not { } doc) return;
        var p = e.GetPosition(this);
        _lastMouse = p;
        int edge = ResizeEdgeAt(p);
        if (edge >= 0)
        {
            if (e.ClickCount == 2) { AutoFit(edge); e.Handled = true; return; }
            _drag = Drag.Resize;
            _resizeCol = edge;
            _resizeStart = ColX(edge);
            _resizeWidthBefore = doc.Sheet.Widths.TryGetValue(edge, out var w) ? w : null;
            CaptureMouse();
            e.Handled = true;
            return;
        }
        var zone = ZoneAt(p);
        int row = RowAt(p.Y), col = ColumnAt(p.X);
        if (_editing)
        {
            if (zone == Zone.Cell && IsReferencePoint() && !(row == _editRow && col == _editCol))
            {
                // Building a formula: the clicked cell goes in at the caret.
                InsertReference(row, col, row, col, fresh: true);
                _refDrag = true;
                _refAnchorRow = row;
                _refAnchorCol = col;
                CaptureMouse();
                e.Handled = true;
                return;
            }
            if (zone == Zone.Cell && row == _editRow && col == _editCol) return;
            CommitEdit(refocus: true);
        }
        Focus();
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (zone)
        {
            case Zone.Corner:
                doc.SelectAll();
                break;
            case Zone.ColumnHeader:
                if (shift) doc.SelectRange(0, doc.AnchorCol, Math.Max(doc.Sheet.UsedRows - 1, 999), col);
                else doc.SelectColumns(col, col);
                _drag = Drag.Columns;
                break;
            case Zone.RowHeader:
                if (shift) doc.SelectRange(doc.AnchorRow, 0, row, Math.Max(doc.Sheet.UsedCols - 1, 25));
                else doc.SelectRows(row, row);
                _drag = Drag.Rows;
                break;
            default:
                if (e.ClickCount == 2)
                {
                    doc.Select(row, col);
                    BeginEdit(null, editMode: true);
                    e.Handled = true;
                    return;
                }
                doc.Select(row, col, shift);
                _drag = Drag.Cells;
                break;
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (Document is not { } doc) return;
        var p = e.GetPosition(this);
        _lastMouse = p;
        if (_drag == Drag.Resize)
        {
            doc.SetWidth(_resizeCol, Math.Max(Sheet.MinWidth, p.X - _resizeStart), record: false);
            ComputeColumns();
            InvalidateVisual();
            ScrollOwner?.InvalidateScrollInfo();
            return;
        }
        if (_refDrag && e.LeftButton == MouseButtonState.Pressed)
        {
            InsertReference(_refAnchorRow, _refAnchorCol, RowAt(p.Y), ColumnAt(p.X), fresh: false);
            UpdateAutoScroll(p);
            return;
        }
        if (_drag != Drag.None && e.LeftButton == MouseButtonState.Pressed)
        {
            ExtendDrag(p);
            UpdateAutoScroll(p);
            return;
        }
        Cursor = ResizeEdgeAt(p) >= 0 ? Cursors.SizeWE : null;
    }

    private void ExtendDrag(Point p)
    {
        if (Document is not { } doc) return;
        int row = RowAt(Math.Clamp(p.Y, HeaderHeight + 1, Math.Max(HeaderHeight + 1, _viewport.Height - 1)));
        int col = ColumnAt(Math.Clamp(p.X, _rowHeaderWidth + 1, Math.Max(_rowHeaderWidth + 1, _viewport.Width - 1)));
        switch (_drag)
        {
            case Drag.Cells: doc.Select(row, col, extend: true); break;
            case Drag.Columns: doc.SelectRange(0, doc.AnchorCol, Math.Max(doc.Sheet.UsedRows - 1, 999), col); break;
            case Drag.Rows: doc.SelectRange(doc.AnchorRow, 0, row, Math.Max(doc.Sheet.UsedCols - 1, 25)); break;
        }
    }

    private void UpdateAutoScroll(Point p)
    {
        bool outside = p.Y < FrozenBottom || p.Y > _viewport.Height || p.X < _rowHeaderWidth || p.X > _viewport.Width;
        if (outside && !_autoScroll.IsEnabled) _autoScroll.Start();
        else if (!outside) _autoScroll.Stop();
    }

    private void AutoScrollTick()
    {
        if (_drag == Drag.None && !_refDrag) { _autoScroll.Stop(); return; }
        var p = _lastMouse;
        if (p.Y > _viewport.Height) LineDown();
        else if (p.Y < FrozenBottom && _offset.Y > 0) LineUp();
        if (p.X > _viewport.Width) LineRight();
        else if (p.X < _rowHeaderWidth) LineLeft();
        if (_refDrag) InsertReference(_refAnchorRow, _refAnchorCol, RowAt(Math.Clamp(p.Y, FrozenBottom + 1, _viewport.Height - 1)), ColumnAt(Math.Clamp(p.X, _rowHeaderWidth + 1, _viewport.Width - 1)), fresh: false);
        else ExtendDrag(p);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _autoScroll.Stop();
        if (_drag == Drag.Resize && Document is { } doc) doc.CommitWidth(_resizeCol, _resizeWidthBefore);
        if (_refDrag)
        {
            _refDrag = false;
            _editor.Focus();
        }
        _drag = Drag.None;
        if (IsMouseCaptured) ReleaseMouseCapture();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (Document is not { } doc) return;
        if (_editing) CommitEdit(refocus: true);
        Focus();
        var p = e.GetPosition(this);
        if (ZoneAt(p) != Zone.Cell) return;
        int row = RowAt(p.Y), col = ColumnAt(p.X);
        var (r1, c1, r2, c2) = doc.Selection;
        if (row < r1 || row > r2 || col < c1 || col > c2) doc.Select(row, col);
    }

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        base.OnPreviewMouseWheel(e);
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        if (e.Delta > 0) MouseWheelLeft(); else MouseWheelRight();
        e.Handled = true;
    }

    /// <summary>Double-click on a column border: the column gets as wide as its longest value.</summary>
    public void AutoFit(int col)
    {
        if (Document is not { } doc) return;
        double width = 0;
        foreach (var (_, c, cell) in doc.Sheet.All())
        {
            if (c != col || cell.Display.Length == 0) continue;
            var ft = Text(cell.Display.Length > 200 ? cell.Display[..200] : cell.Display, cell.Style.HasFlag(CellStyle.Bold), cell.Style.HasFlag(CellStyle.Italic), Brushes.Black);
            width = Math.Max(width, ft.WidthIncludingTrailingWhitespace);
        }
        doc.SetWidth(col, width == 0 ? Sheet.DefaultWidth : width + Padding * 2 + 6);
    }

    // ===== keyboard =====

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (Document is not { } doc) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift), alt = mods.HasFlag(ModifierKeys.Alt);

        if (_editing)
        {
            switch (key)
            {
                case Key.Enter when alt:
                    int at = _editor.CaretIndex;
                    _editor.Text = _editor.Text.Insert(at, "\n");
                    _editor.CaretIndex = at + 1;
                    e.Handled = true;
                    return;
                case Key.Enter:
                    CommitEdit(refocus: true);
                    if (!ctrl) doc.Move(shift ? -1 : 1, 0, withinSelection: true);
                    e.Handled = true;
                    return;
                case Key.Tab:
                    CommitEdit(refocus: true);
                    doc.Move(0, shift ? -1 : 1, withinSelection: true);
                    e.Handled = true;
                    return;
                case Key.Escape:
                    CancelEdit();
                    e.Handled = true;
                    return;
                case Key.F2:
                    _editMode = !_editMode;
                    e.Handled = true;
                    return;
                case Key.Up or Key.Down or Key.Left or Key.Right when !_editMode && !ctrl && !shift:
                    // Typed straight in ("enter" mode): arrows finish the cell and move, like in Excel.
                    CommitEdit(refocus: true);
                    doc.Move(key == Key.Up ? -1 : key == Key.Down ? 1 : 0, key == Key.Left ? -1 : key == Key.Right ? 1 : 0);
                    e.Handled = true;
                    return;
            }
            return;
        }

        bool handled = true;
        switch (key)
        {
            case Key.Up: if (ctrl) doc.Jump(-1, 0, shift); else doc.Move(-1, 0, shift); break;
            case Key.Down: if (ctrl) doc.Jump(1, 0, shift); else doc.Move(1, 0, shift); break;
            case Key.Left: if (ctrl) doc.Jump(0, -1, shift); else doc.Move(0, -1, shift); break;
            case Key.Right: if (ctrl) doc.Jump(0, 1, shift); else doc.Move(0, 1, shift); break;
            case Key.Enter: doc.Move(shift ? -1 : 1, 0, withinSelection: true); break;
            case Key.Tab: doc.Move(0, shift ? -1 : 1, withinSelection: true); break;
            case Key.Home: doc.Select(ctrl ? 0 : doc.ActiveRow, 0, shift); break;
            case Key.End when ctrl: doc.Select(Math.Max(0, doc.Sheet.UsedRows - 1), Math.Max(0, doc.Sheet.UsedCols - 1), shift); break;
            case Key.PageDown: doc.Move(Math.Max(1, (int)((_viewport.Height - FrozenBottom) / RowHeight) - 1), 0, shift); break;
            case Key.PageUp: doc.Move(-Math.Max(1, (int)((_viewport.Height - FrozenBottom) / RowHeight) - 1), 0, shift); break;
            case Key.F2: BeginEdit(null, editMode: true); break;
            case Key.Delete: doc.ClearSelection(); break;
            case Key.Back: BeginEdit("", editMode: false); break;
            case Key.Escape when doc.CopyRange != null: doc.ClearCopy(); break;
            case Key.C when ctrl: doc.Copy(cut: false); break;
            case Key.X when ctrl: doc.Copy(cut: true); break;
            case Key.V when ctrl: doc.Paste(); break;
            case Key.Z when ctrl && shift: doc.Redo(); break;
            case Key.Z when ctrl: doc.Undo(); break;
            case Key.Y when ctrl: doc.Redo(); break;
            case Key.A when ctrl: doc.SelectAll(); break;
            case Key.B when ctrl: doc.ToggleStyle(CellStyle.Bold); break;
            case Key.I when ctrl: doc.ToggleStyle(CellStyle.Italic); break;
            case Key.D when ctrl && !shift: doc.Fill(down: true); break;
            case Key.R when ctrl && !shift: doc.Fill(down: false); break;
            case Key.OemSemicolon when ctrl:
                doc.Commit(doc.ActiveRow, doc.ActiveCol, shift ? DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture) : DateTime.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                break;
            case Key.Space when ctrl: doc.SelectColumns(doc.Selection.C1, doc.Selection.C2); break;
            case Key.Space when shift: doc.SelectRows(doc.Selection.R1, doc.Selection.R2); break;
            case Key.OemPlus when alt: doc.AutoSum(); break;
            default: handled = false; break;
        }
        if (handled) e.Handled = true;
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (_editing || Document == null || string.IsNullOrEmpty(e.Text) || char.IsControl(e.Text[0])) return;
        BeginEdit(e.Text, editMode: false);
        e.Handled = true;
    }

    // ===== editing =====

    public bool IsEditing => _editing;

    /// <summary>Starts editing the active cell: with its contents (F2, double-click) or with what you just typed.</summary>
    public void BeginEdit(string? initial, bool editMode)
    {
        if (Document is not { } doc) return;
        doc.ClearCopy();
        _editRow = doc.ActiveRow;
        _editCol = doc.ActiveCol;
        _editMode = editMode;
        _editing = true;
        _refLength = 0;
        _editor.Text = initial ?? doc.RawAt(_editRow, _editCol);
        _editor.FontWeight = doc.ActiveStyle.HasFlag(CellStyle.Bold) ? FontWeights.SemiBold : FontWeights.Normal;
        _editor.Visibility = Visibility.Visible;
        EnsureActiveVisible();
        InvalidateArrange();
        InvalidateVisual();
        _editor.Focus();
        Keyboard.Focus(_editor);
        _editor.CaretIndex = _editor.Text.Length;
        EditTextChanged?.Invoke(_editor.Text);
    }

    public void CommitEdit(bool refocus)
    {
        if (!_editing || Document is not { } doc) return;
        var text = _editor.Text;
        EndEdit(refocus);
        doc.Commit(_editRow, _editCol, text);
    }

    public void CancelEdit()
    {
        if (!_editing) return;
        EndEdit(refocus: true);
        Document?.Refresh();
    }

    private void EndEdit(bool refocus)
    {
        _editing = false;
        _refDrag = false;
        _editor.Visibility = Visibility.Collapsed;
        InvalidateArrange();
        InvalidateVisual();
        if (refocus) Focus();
        EditEnded?.Invoke();
    }

    /// <summary>Right after "=", "(", "," or an operator, a click on a cell puts its address into the formula.</summary>
    private bool IsReferencePoint()
    {
        var text = _editor.Text;
        if (!text.StartsWith('=')) return false;
        int caret = _editor.CaretIndex;
        if (_refLength > 0 && caret == _refStart + _refLength) return true; // still choosing the last reference
        var before = text[..caret].TrimEnd();
        return before.Length > 0 && before[^1] is '=' or '(' or ',' or ';' or '+' or '-' or '*' or '/' or '^' or '&' or '<' or '>' or ':';
    }

    private void InsertReference(int r1, int c1, int r2, int c2, bool fresh)
    {
        var text = _editor.Text;
        var reference = CellRef.RangeName(r1, c1, r2, c2);
        if (fresh)
        {
            int caret = _editor.CaretIndex;
            if (_refLength > 0 && caret == _refStart + _refLength) { text = text.Remove(_refStart, _refLength); caret = _refStart; }
            _refStart = caret;
        }
        else if (_refLength > 0 && _refStart + _refLength <= text.Length) text = text.Remove(_refStart, _refLength);
        _editor.Text = text.Insert(Math.Min(_refStart, text.Length), reference);
        _refLength = reference.Length;
        _editor.CaretIndex = _refStart + _refLength;
        _editor.Focus();
    }
}
