using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Satchel.Core;

namespace Satchel.Behaviors;

public enum ReorderMode
{
    Hover,
    Vertical,
    Horizontal,
    /// <summary>The items panel (an <see cref="IReorderPanel"/>) picks the spot closest to where the card is held.</summary>
    Nearest
}

/// <summary>An items panel that can tell where a dragged child should go (used by <see cref="ReorderMode.Nearest"/>).</summary>
public interface IReorderPanel
{
    /// <summary>The index (among the panel's children) for the dragged child, given where it's held (panel coordinates); -1 = no opinion.</summary>
    int BestIndex(UIElement dragged, Rect held);
}

public sealed class ReorderEventArgs : EventArgs
{
    public required object Item { get; init; }
    public required IList SourceList { get; init; }
    public required IList TargetList { get; init; }
    public int OldIndex { get; init; }
    public int NewIndex { get; init; }
    public object? SourceTag { get; init; }
    public object? TargetTag { get; init; }
    public bool ChangedList => !ReferenceEquals(SourceList, TargetList);
}

public sealed class DropEventArgs : EventArgs
{
    public required object Data { get; init; }
    public object? Target { get; init; }
    public object? TargetTag { get; init; }
    public object? SourceTag { get; init; }
    /// <summary>Pointer position relative to the drop target element.</summary>
    public Point Position { get; init; }
    /// <summary>Where the dragged element was grabbed (relative to its top-left).</summary>
    public Point GrabOffset { get; init; }
}

/// <summary>
/// Mouse-capture based drag &amp; drop with live reordering:
///  • ItemsControl + IsReorderable: items move out of the way while dragging (pair with an animated panel).
///  • Lists sharing a Group exchange items (kanban columns, pinned/unpinned notes).
///  • DragData + DropCommand: free drags onto targets (event → calendar day, note → notebook).
/// </summary>
public static class DnD
{
    #region Attached properties

    public static readonly DependencyProperty IsReorderableProperty = DependencyProperty.RegisterAttached(
        "IsReorderable", typeof(bool), typeof(DnD), new PropertyMetadata(false, OnSourceChanged));
    public static bool GetIsReorderable(DependencyObject o) => (bool)o.GetValue(IsReorderableProperty);
    public static void SetIsReorderable(DependencyObject o, bool v) => o.SetValue(IsReorderableProperty, v);

    public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
        "Group", typeof(string), typeof(DnD), new PropertyMetadata(null));
    public static string? GetGroup(DependencyObject o) => (string?)o.GetValue(GroupProperty);
    public static void SetGroup(DependencyObject o, string? v) => o.SetValue(GroupProperty, v);

    public static readonly DependencyProperty ListTagProperty = DependencyProperty.RegisterAttached(
        "ListTag", typeof(object), typeof(DnD), new PropertyMetadata(null));
    public static object? GetListTag(DependencyObject o) => o.GetValue(ListTagProperty);
    public static void SetListTag(DependencyObject o, object? v) => o.SetValue(ListTagProperty, v);

    public static readonly DependencyProperty ReorderCommandProperty = DependencyProperty.RegisterAttached(
        "ReorderCommand", typeof(ICommand), typeof(DnD), new PropertyMetadata(null));
    public static ICommand? GetReorderCommand(DependencyObject o) => (ICommand?)o.GetValue(ReorderCommandProperty);
    public static void SetReorderCommand(DependencyObject o, ICommand? v) => o.SetValue(ReorderCommandProperty, v);

    public static readonly DependencyProperty HandleOnlyProperty = DependencyProperty.RegisterAttached(
        "HandleOnly", typeof(bool), typeof(DnD), new PropertyMetadata(false));
    public static bool GetHandleOnly(DependencyObject o) => (bool)o.GetValue(HandleOnlyProperty);
    public static void SetHandleOnly(DependencyObject o, bool v) => o.SetValue(HandleOnlyProperty, v);

    /// <summary>Vertical/Horizontal = insertion by midpoints (lists). Hover = swap with hovered item (grids).</summary>
    public static readonly DependencyProperty ModeProperty = DependencyProperty.RegisterAttached(
        "Mode", typeof(ReorderMode), typeof(DnD), new PropertyMetadata(ReorderMode.Hover));
    public static ReorderMode GetMode(DependencyObject o) => (ReorderMode)o.GetValue(ModeProperty);
    public static void SetMode(DependencyObject o, ReorderMode v) => o.SetValue(ModeProperty, v);

    public static readonly DependencyProperty IsHandleProperty = DependencyProperty.RegisterAttached(
        "IsHandle", typeof(bool), typeof(DnD), new PropertyMetadata(false));
    public static bool GetIsHandle(DependencyObject o) => (bool)o.GetValue(IsHandleProperty);
    public static void SetIsHandle(DependencyObject o, bool v) => o.SetValue(IsHandleProperty, v);

    public static readonly DependencyProperty NoDragProperty = DependencyProperty.RegisterAttached(
        "NoDrag", typeof(bool), typeof(DnD), new PropertyMetadata(false));
    public static bool GetNoDrag(DependencyObject o) => (bool)o.GetValue(NoDragProperty);
    public static void SetNoDrag(DependencyObject o, bool v) => o.SetValue(NoDragProperty, v);

    public static readonly DependencyProperty DragDataProperty = DependencyProperty.RegisterAttached(
        "DragData", typeof(object), typeof(DnD), new PropertyMetadata(null, OnSourceChanged));
    public static object? GetDragData(DependencyObject o) => o.GetValue(DragDataProperty);
    public static void SetDragData(DependencyObject o, object? v) => o.SetValue(DragDataProperty, v);

    public static readonly DependencyProperty DropCommandProperty = DependencyProperty.RegisterAttached(
        "DropCommand", typeof(ICommand), typeof(DnD), new PropertyMetadata(null));
    public static ICommand? GetDropCommand(DependencyObject o) => (ICommand?)o.GetValue(DropCommandProperty);
    public static void SetDropCommand(DependencyObject o, ICommand? v) => o.SetValue(DropCommandProperty, v);

    public static readonly DependencyProperty DropTagProperty = DependencyProperty.RegisterAttached(
        "DropTag", typeof(object), typeof(DnD), new PropertyMetadata(null));
    public static object? GetDropTag(DependencyObject o) => o.GetValue(DropTagProperty);
    public static void SetDropTag(DependencyObject o, object? v) => o.SetValue(DropTagProperty, v);

    public static readonly DependencyProperty IsDragOverProperty = DependencyProperty.RegisterAttached(
        "IsDragOver", typeof(bool), typeof(DnD), new PropertyMetadata(false));
    public static bool GetIsDragOver(DependencyObject o) => (bool)o.GetValue(IsDragOverProperty);
    public static void SetIsDragOver(DependencyObject o, bool v) => o.SetValue(IsDragOverProperty, v);

    #endregion

    /// <summary>Top-level element inside the window's AdornerDecorator (set by MainWindow).</summary>
    public static FrameworkElement? Root { get; set; }

    public static bool IsDragging => _session != null;

    /// <summary>Diagnostics hook (debug automation only).</summary>
    public static Action<string>? Trace { get; set; }

    private const double Threshold = 5;
    private static Pending? _pending;
    private static Session? _session;
    private static readonly ConcurrentDictionary<Type, MethodInfo?> MoveMethods = new();

    private sealed class Pending
    {
        public required FrameworkElement Source;
        public required FrameworkElement Visual;
        public required object Data;
        public ItemsControl? List;
        public Point Start;
        public Point Grab;
    }

    private sealed class Session
    {
        public required FrameworkElement Root;
        public required object Data;
        public required string? Group;
        public required FrameworkElement Source;
        public ItemsControl? OriginList;
        public IList? OriginItems;
        public int OriginIndex = -1;
        public ItemsControl? CurrentList;
        public Point Grab;
        public DragAdorner? Adorner;
        public AdornerLayer? Layer;
        public FrameworkElement? Target;
        public object? LastHover;
        public readonly List<UIElement> Ghosted = [];
        public DispatcherTimer? ScrollTimer;
        public ScrollViewer? ScrollViewer;
        public Vector ScrollSpeed;
        public Window? Window;
        public bool Ending;
        /// <summary>A drop command handled the drop (the card doesn't fly back into a list slot).</summary>
        public bool Dropped;
        public bool IsList => OriginItems != null;
    }

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement el) return;
        el.PreviewMouseLeftButtonDown -= OnPreviewDown;
        el.PreviewMouseMove -= OnPreviewMove;
        el.PreviewMouseLeftButtonUp -= OnPreviewUp;
        bool enable = e.Property == IsReorderableProperty ? e.NewValue is true : e.NewValue != null;
        if (!enable && e.Property == DragDataProperty && GetIsReorderable(d)) enable = true;
        if (!enable && e.Property == IsReorderableProperty && GetDragData(d) != null) enable = true;
        if (enable)
        {
            el.PreviewMouseLeftButtonDown += OnPreviewDown;
            el.PreviewMouseMove += OnPreviewMove;
            el.PreviewMouseLeftButtonUp += OnPreviewUp;
        }
    }

    private static void OnPreviewDown(object sender, MouseButtonEventArgs e)
    {
        if (_session != null || sender is not FrameworkElement fe) return;
        var origin = e.OriginalSource as DependencyObject;
        if (origin == null) return;

        if (fe is ItemsControl ic && GetIsReorderable(ic))
        {
            bool onHandle = IsWithinHandle(origin, ic);
            if (GetHandleOnly(ic) && !onHandle) return;
            if (!onHandle && IsInteractive(origin, ic)) return;
            if (ItemsControl.ContainerFromElement(ic, origin) is not FrameworkElement container) return;
            var item = ic.ItemContainerGenerator.ItemFromContainer(container);
            if (item == DependencyProperty.UnsetValue || item == null) return;
            _pending = new Pending { Source = ic, Visual = container, Data = item, List = ic, Start = e.GetPosition(null), Grab = e.GetPosition(container) };
            Trace?.Invoke($"pending list item {item}");
        }
        else if (GetDragData(fe) is { } data)
        {
            if (IsInteractive(origin, fe)) return;
            _pending = new Pending { Source = fe, Visual = fe, Data = data, List = null, Start = e.GetPosition(null), Grab = e.GetPosition(fe) };
        }
    }

    private static void OnPreviewMove(object sender, MouseEventArgs e)
    {
        if (_pending == null || _session != null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { _pending = null; return; }
        var pos = e.GetPosition(null);
        if (Math.Abs(pos.X - _pending.Start.X) < Threshold && Math.Abs(pos.Y - _pending.Start.Y) < Threshold) return;
        var pending = _pending;
        _pending = null;
        Trace?.Invoke("start drag");
        Start(pending);
    }

    private static void OnPreviewUp(object sender, MouseButtonEventArgs e) => _pending = null;

    private static void Start(Pending p)
    {
        var root = Root ?? (Window.GetWindow(p.Source)?.Content as FrameworkElement);
        if (root == null || !p.Visual.IsVisible) return;
        var layer = AdornerLayer.GetAdornerLayer(root);

        var s = new Session
        {
            Root = root,
            Data = p.Data,
            Source = p.Source,
            Group = GetGroup(p.Source),
            Grab = p.Grab,
            Layer = layer,
            Window = Window.GetWindow(root)
        };

        if (p.List is { ItemsSource: IList items } list && !items.IsFixedSize && !items.IsReadOnly)
        {
            s.OriginList = list;
            s.CurrentList = list;
            s.OriginItems = items;
            s.OriginIndex = items.IndexOf(p.Data);
        }

        if (layer != null)
        {
            var image = Snapshot(p.Visual, out var size);
            if (image != null)
            {
                double tilt = s.IsList && GetMode(p.List!) is ReorderMode.Hover or ReorderMode.Nearest ? 1.6 : 0.8;
                // Big cards tilt less, so their corners don't swing far.
                tilt = Math.Min(tilt, 160 / Math.Max(1, Math.Max(size.Width, size.Height)));
                s.Adorner = new DragAdorner(root, image, size, tilt);
                layer.Add(s.Adorner);
                s.Adorner.MoveTo(Mouse.GetPosition(root) - (Vector)s.Grab);
                s.Adorner.PickUp();
            }
        }

        _session = s;
        Ghost(s, p.Visual);

        root.MouseMove += OnSessionMove;
        root.MouseLeftButtonUp += OnSessionUp;
        root.LostMouseCapture += OnLostCapture;
        if (s.Window != null) s.Window.PreviewKeyDown += OnSessionKey;
        Mouse.OverrideCursor = Cursors.SizeAll;
        if (!root.CaptureMouse()) { Trace?.Invoke("capture failed"); End(false); return; }

        s.ScrollTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };
        s.ScrollTimer.Tick += OnScrollTick;
        s.ScrollTimer.Start();
    }

    private static void OnSessionMove(object sender, MouseEventArgs e)
    {
        var s = _session;
        if (s == null) return;
        var p = e.GetPosition(s.Root);
        s.Adorner?.MoveTo(p - (Vector)s.Grab);

        var hit = s.Root.InputHitTest(p) as DependencyObject;
        FindTargets(s, hit, out var list, out var dropTarget);
        Trace?.Invoke($"move {p} hit={hit?.GetType().Name} list={(list == null ? "-" : GetListTag(list) ?? "?")} target={(dropTarget == null ? "-" : "yes")}");
        SetTarget(s, dropTarget);
        if (list != null) Hover(s, list, p);
        UpdateAutoScroll(s, hit);
    }

    private static void FindTargets(Session s, DependencyObject? hit, out ItemsControl? list, out FrameworkElement? dropTarget)
    {
        list = null;
        dropTarget = null;
        for (var d = hit; d != null; d = Parent(d))
        {
            if (s.IsList && d is ItemsControl ic && GetIsReorderable(ic) && GetGroup(ic) == s.Group && ic.ItemsSource is IList)
            {
                list = ic;
                return;
            }
            if (d is FrameworkElement fe && GetDropCommand(fe) != null && GetGroup(fe) == s.Group)
            {
                dropTarget = fe;
                return;
            }
        }
    }

    private static void SetTarget(Session s, FrameworkElement? target)
    {
        if (ReferenceEquals(s.Target, target)) return;
        if (s.Target != null) SetIsDragOver(s.Target, false);
        s.Target = target;
        if (target != null) SetIsDragOver(target, true);
    }

    private static void Hover(Session s, ItemsControl list, Point p)
    {
        var items = (IList)list.ItemsSource;
        int current = items.IndexOf(s.Data);
        var mode = GetMode(list);

        if (mode == ReorderMode.Nearest && current >= 0 && s.Adorner != null
            && list.ItemContainerGenerator.ContainerFromItem(s.Data) is FrameworkElement self
            && VisualTreeHelper.GetParent(self) is Panel host and IReorderPanel panel)
        {
            var topLeft = s.Root.TranslatePoint(p - (Vector)s.Grab, host);
            int target = panel.BestIndex(self, new Rect(topLeft, s.Adorner.Size));
            if (target >= 0 && target != current && target < items.Count) MoveWithin(items, current, target);
            return;
        }

        if (mode is ReorderMode.Vertical or ReorderMode.Horizontal)
        {
            int insertion = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (ReferenceEquals(items[i], s.Data)) continue;
                if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c || !c.IsVisible) continue;
                var r = SlotRect(c, s.Root);
                if (r.IsEmpty) continue;
                bool after = mode == ReorderMode.Vertical ? p.Y > r.Top + r.Height / 2 : p.X > r.Left + r.Width / 2;
                if (after) insertion++;
            }
            if (current >= 0)
            {
                if (insertion != current) MoveWithin(items, current, insertion);
            }
            else
            {
                MoveAcross(s, list, items, insertion);
            }
            return;
        }

        // Hover-swap mode for grids / masonry.
        object? hovered = null;
        int hoveredIndex = -1;
        bool overSelf = false;
        for (int i = 0; i < items.Count; i++)
        {
            if (list.ItemContainerGenerator.ContainerFromIndex(i) is not FrameworkElement c || !c.IsVisible) continue;
            var r = SlotRect(c, s.Root);
            if (!r.Contains(p)) continue;
            if (ReferenceEquals(items[i], s.Data)) { overSelf = true; break; }
            hovered = items[i];
            hoveredIndex = i;
            break;
        }

        if (overSelf) { s.LastHover = null; return; }
        if (hovered == null)
        {
            if (current < 0) MoveAcross(s, list, items, items.Count);
            return;
        }
        if (ReferenceEquals(hovered, s.LastHover)) return;
        s.LastHover = hovered;
        if (current >= 0) MoveWithin(items, current, hoveredIndex);
        else MoveAcross(s, list, items, hoveredIndex);
    }

    private static void MoveWithin(IList items, int from, int to)
    {
        if (from == to || from < 0 || to < 0 || to >= items.Count) return;
        var move = MoveMethods.GetOrAdd(items.GetType(), t => t.GetMethod("Move", [typeof(int), typeof(int)]));
        if (move != null)
        {
            move.Invoke(items, [from, to]);
        }
        else
        {
            var item = items[from];
            items.RemoveAt(from);
            items.Insert(to, item);
        }
        RefreshGhost();
    }

    private static void MoveAcross(Session s, ItemsControl target, IList targetItems, int index)
    {
        if (s.CurrentList?.ItemsSource is IList from) from.Remove(s.Data);
        targetItems.Insert(Math.Clamp(index, 0, targetItems.Count), s.Data);
        s.CurrentList = target;
        s.LastHover = null;
        RefreshGhost();
    }

    private static void RefreshGhost()
    {
        var s = _session;
        if (s?.CurrentList == null) return;
        s.CurrentList.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_session != s || s.CurrentList == null) return;
            if (s.CurrentList.ItemContainerGenerator.ContainerFromItem(s.Data) is UIElement c && !s.Ghosted.Contains(c))
                Ghost(s, c);
        });
    }

    private static void Ghost(Session s, UIElement element)
    {
        element.BeginAnimation(UIElement.OpacityProperty, null);
        element.Opacity = s.IsList ? 0.22 : 0.45;
        s.Ghosted.Add(element);
    }

    private static void OnSessionUp(object sender, MouseButtonEventArgs e)
    {
        Trace?.Invoke("mouse up");
        End(true);
    }

    private static void OnLostCapture(object sender, MouseEventArgs e)
    {
        // LostMouseCapture bubbles: a button inside the dragged card releasing its own capture
        // must not cancel the drag — only the root losing capture does.
        if (_session is not { Ending: false } s || !ReferenceEquals(e.OriginalSource, s.Root)) return;
        Trace?.Invoke("lost capture");
        End(false);
    }

    private static void OnSessionKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _session != null)
        {
            e.Handled = true;
            End(false);
        }
    }

    private static void End(bool commit)
    {
        var s = _session;
        if (s == null || s.Ending) return;
        s.Ending = true;

        try
        {
            if (commit)
            {
                var p = Mouse.GetPosition(s.Root);
                FindTargets(s, s.Root.InputHitTest(p) as DependencyObject, out _, out var dropTarget);
                if (dropTarget != null && GetDropCommand(dropTarget) is { } drop)
                {
                    s.Dropped = true;
                    Revert(s);
                    var args = new DropEventArgs
                    {
                        Data = s.Data,
                        Target = dropTarget.DataContext,
                        TargetTag = GetDropTag(dropTarget),
                        SourceTag = s.OriginList != null ? GetListTag(s.OriginList) : GetDropTag(s.Source),
                        Position = Mouse.GetPosition(dropTarget),
                        GrabOffset = s.Grab
                    };
                    if (drop.CanExecute(args)) drop.Execute(args);
                }
                else if (s.IsList && s.CurrentList?.ItemsSource is IList items)
                {
                    int newIndex = items.IndexOf(s.Data);
                    bool changed = !ReferenceEquals(s.CurrentList, s.OriginList) || newIndex != s.OriginIndex;
                    if (changed && newIndex >= 0)
                    {
                        var args = new ReorderEventArgs
                        {
                            Item = s.Data,
                            SourceList = s.OriginItems!,
                            TargetList = items,
                            OldIndex = s.OriginIndex,
                            NewIndex = newIndex,
                            SourceTag = s.OriginList != null ? GetListTag(s.OriginList) : null,
                            TargetTag = GetListTag(s.CurrentList)
                        };
                        var cmd = GetReorderCommand(s.CurrentList) ?? (s.OriginList != null ? GetReorderCommand(s.OriginList) : null);
                        if (cmd?.CanExecute(args) == true) cmd.Execute(args);
                    }
                }
            }
            else
            {
                Revert(s);
            }
        }
        finally
        {
            Cleanup(s);
        }
    }

    private static void Revert(Session s)
    {
        if (!s.IsList || s.OriginItems == null || s.CurrentList?.ItemsSource is not IList current) return;
        int idx = current.IndexOf(s.Data);
        if (ReferenceEquals(current, s.OriginItems))
        {
            if (idx >= 0 && idx != s.OriginIndex) MoveWithin(current, idx, Math.Clamp(s.OriginIndex, 0, current.Count - 1));
        }
        else
        {
            if (idx >= 0) current.RemoveAt(idx);
            s.OriginItems.Insert(Math.Clamp(s.OriginIndex, 0, s.OriginItems.Count), s.Data);
        }
        s.CurrentList = s.OriginList;
    }

    private static void Cleanup(Session s)
    {
        s.ScrollTimer?.Stop();
        s.Root.MouseMove -= OnSessionMove;
        s.Root.MouseLeftButtonUp -= OnSessionUp;
        s.Root.LostMouseCapture -= OnLostCapture;
        if (s.Window != null) s.Window.PreviewKeyDown -= OnSessionKey;
        if (s.Target != null) SetIsDragOver(s.Target, false);
        Mouse.OverrideCursor = null;
        _session = null;
        if (s.Root.IsMouseCaptured) s.Root.ReleaseMouseCapture();

        // Reordered (or cancelled) list items: the picture glides into the card's slot before the real card shows again.
        if (s.Adorner is { } adorner && !s.Dropped && Anim.Enabled && SettleTarget(s) is { } settle)
        {
            var card = settle.Card;
            foreach (var el in s.Ghosted.Where(el => !ReferenceEquals(el, card))) Unghost(el);
            card.BeginAnimation(UIElement.OpacityProperty, null);
            card.Opacity = 0;
            adorner.SettleTo(settle.TopLeft, () =>
            {
                s.Layer?.Remove(adorner);
                card.Opacity = 1;
            });
            return;
        }

        if (s.Adorner != null) s.Layer?.Remove(s.Adorner);
        foreach (var el in s.Ghosted) Unghost(el);
        // The final container may have been regenerated after a cross-list move.
        s.Root.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (s.CurrentList?.ItemContainerGenerator.ContainerFromItem(s.Data) is UIElement c) Unghost(c);
            if (s.OriginList?.ItemContainerGenerator.ContainerFromItem(s.Data) is UIElement o) Unghost(o);
        });
    }

    /// <summary>The dragged item's card in its final list and where it sits (root coordinates), once layout has caught up.</summary>
    private static (FrameworkElement Card, Point TopLeft)? SettleTarget(Session s)
    {
        if (!s.IsList || s.CurrentList == null) return null;
        try
        {
            s.CurrentList.UpdateLayout();
            if (s.CurrentList.ItemContainerGenerator.ContainerFromItem(s.Data) is not FrameworkElement card || !card.IsVisible) return null;
            // Skip the panel's glide: the card is hidden while the picture lands, so it can sit in its slot already.
            if (card.RenderTransform is TranslateTransform { IsFrozen: false } shift)
            {
                shift.BeginAnimation(TranslateTransform.XProperty, null);
                shift.BeginAnimation(TranslateTransform.YProperty, null);
                shift.X = shift.Y = 0;
            }
            return (card, card.TranslatePoint(new Point(0, 0), s.Root));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static void Unghost(UIElement el)
    {
        if (el.Opacity >= 1 && !el.HasAnimatedProperties) return;
        el.BeginAnimation(UIElement.OpacityProperty, Anim.To(1, 220, Anim.EaseOut, el.Opacity));
    }

    private static void UpdateAutoScroll(Session s, DependencyObject? hit)
    {
        s.ScrollViewer = null;
        s.ScrollSpeed = default;
        for (var d = hit; d != null; d = Parent(d))
        {
            if (d is ScrollViewer sv && (sv.ScrollableHeight > 0 || sv.ScrollableWidth > 0) && sv.TemplatedParent is not TextBoxBase)
            {
                var pos = Mouse.GetPosition(sv);
                const double edge = 56;
                double vy = 0, vx = 0;
                if (sv.ScrollableHeight > 0)
                {
                    if (pos.Y < edge) vy = -(edge - pos.Y);
                    else if (pos.Y > sv.ActualHeight - edge) vy = pos.Y - (sv.ActualHeight - edge);
                }
                if (sv.ScrollableWidth > 0)
                {
                    if (pos.X < edge) vx = -(edge - pos.X);
                    else if (pos.X > sv.ActualWidth - edge) vx = pos.X - (sv.ActualWidth - edge);
                }
                if (vx != 0 || vy != 0)
                {
                    s.ScrollViewer = sv;
                    s.ScrollSpeed = new Vector(vx * 0.35, vy * 0.35);
                    return;
                }
            }
        }
    }

    private static void OnScrollTick(object? sender, EventArgs e)
    {
        var s = _session;
        if (s?.ScrollViewer is not { } sv || s.ScrollSpeed == default) return;
        if (s.ScrollSpeed.Y != 0) sv.ScrollToVerticalOffset(sv.VerticalOffset + s.ScrollSpeed.Y);
        if (s.ScrollSpeed.X != 0) sv.ScrollToHorizontalOffset(sv.HorizontalOffset + s.ScrollSpeed.X);
    }

    #region Helpers

    private static Rect SlotRect(FrameworkElement container, Visual root)
    {
        if (VisualTreeHelper.GetParent(container) is not Visual parent) return Rect.Empty;
        try
        {
            var slot = LayoutInformation.GetLayoutSlot(container);
            return parent.TransformToAncestor(root).TransformBounds(slot);
        }
        catch (InvalidOperationException)
        {
            return Rect.Empty;
        }
    }

    private static DependencyObject? Parent(DependencyObject d)
    {
        if (d is Visual or System.Windows.Media.Media3D.Visual3D)
            return VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d);
        return LogicalTreeHelper.GetParent(d);
    }

    private static bool IsWithinHandle(DependencyObject origin, DependencyObject boundary)
    {
        for (var d = origin; d != null && d != boundary; d = Parent(d))
            if (GetIsHandle(d)) return true;
        return false;
    }

    private static bool IsInteractive(DependencyObject origin, DependencyObject boundary)
    {
        for (var d = origin; d != null && d != boundary; d = Parent(d))
        {
            if (GetNoDrag(d)) return true;
            if (d is TextBoxBase or PasswordBox or Thumb or ScrollBar or Slider or ComboBox) return true;
            if (d is TextBox) return true;
        }
        return false;
    }

    private static ImageSource? Snapshot(FrameworkElement element, out Size size)
    {
        size = new Size(element.ActualWidth, element.ActualHeight);
        if (size.Width < 1 || size.Height < 1) return null;
        try
        {
            var dpi = VisualTreeHelper.GetDpi(element);
            double scale = 1;
            if (Window.GetWindow(element) is { } w)
            {
                var m = (element.TransformToAncestor(w) as Transform)?.Value ?? Matrix.Identity;
                scale = Math.Max(0.5, Math.Sqrt(m.M11 * m.M11 + m.M12 * m.M12));
            }
            int pw = (int)Math.Ceiling(size.Width * dpi.DpiScaleX * scale);
            int ph = (int)Math.Ceiling(size.Height * dpi.DpiScaleY * scale);
            var rtb = new RenderTargetBitmap(pw, ph, 96 * dpi.DpiScaleX * scale, 96 * dpi.DpiScaleY * scale, PixelFormats.Pbgra32);
            var dv = new DrawingVisual();
            using (var ctx = dv.RenderOpen())
                ctx.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(size));
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
        catch
        {
            return null;
        }
    }

    #endregion

    private sealed class DragAdorner : Adorner
    {
        private readonly ImageSource _image;
        private readonly Size _size;
        private readonly TranslateTransform _translate = new();
        private readonly ScaleTransform _scale = new(1, 1);
        private readonly RotateTransform _rotate;

        public DragAdorner(UIElement adorned, ImageSource image, Size size, double angle) : base(adorned)
        {
            _image = image;
            _size = size;
            _rotate = new RotateTransform(0, size.Width / 2, size.Height / 2);
            IsHitTestVisible = false;
            Opacity = 0.95;
            var group = new TransformGroup();
            group.Children.Add(_scale);
            group.Children.Add(_rotate);
            group.Children.Add(_translate);
            RenderTransform = group;
            _scale.CenterX = size.Width / 2;
            _scale.CenterY = size.Height / 2;
            if (Application.Current.TryFindResource("LiftShadow") is Effect fx) Effect = fx;
            Tag = angle;
        }

        public Size Size => _size;

        public void MoveTo(Point topLeft)
        {
            _translate.X = topLeft.X;
            _translate.Y = topLeft.Y;
        }

        public void PickUp()
        {
            var angle = (double)Tag;
            // A lift of about 14 px at most: small cards grow 3.5 %, big ones less.
            double lift = 1 + Math.Min(0.035, 14 / Math.Max(1, _size.Width));
            if (!Anim.Enabled) { _scale.ScaleX = _scale.ScaleY = lift; _rotate.Angle = angle; return; }
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim.To(lift, 160, Anim.Back));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim.To(lift, 160, Anim.Back));
            _rotate.BeginAnimation(RotateTransform.AngleProperty, Anim.To(angle, 200, Anim.EaseOut));
        }

        /// <summary>Glides into place (no tilt, normal size), then calls <paramref name="done"/>.</summary>
        public void SettleTo(Point topLeft, Action done)
        {
            const double ms = 190;
            var x = Anim.To(topLeft.X, ms, Anim.EaseOut);
            bool finished = false;
            void Finish()
            {
                if (finished) return;
                finished = true;
                done();
            }
            x.Completed += (_, _) => Finish();
            _translate.BeginAnimation(TranslateTransform.XProperty, x);
            _translate.BeginAnimation(TranslateTransform.YProperty, Anim.To(topLeft.Y, ms, Anim.EaseOut));
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, Anim.To(1, ms, Anim.EaseOut));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, Anim.To(1, ms, Anim.EaseOut));
            _rotate.BeginAnimation(RotateTransform.AngleProperty, Anim.To(0, ms, Anim.EaseOut));
            BeginAnimation(OpacityProperty, Anim.To(1, ms, Anim.EaseOut));
            // Safety net: never leave a picture floating if the animation clock doesn't complete.
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms + 250) };
            timer.Tick += (_, _) => { timer.Stop(); Finish(); };
            timer.Start();
        }

        protected override Size MeasureOverride(Size constraint) => _size;

        protected override void OnRender(DrawingContext dc) => dc.DrawImage(_image, new Rect(_size));
    }
}
