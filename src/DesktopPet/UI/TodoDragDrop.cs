using DesktopPet.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DesktopPet.UI;

internal sealed class TodoDragDrop
{
    private readonly ListBox _list;
    private readonly DesktopPetRepository _repository;
    private readonly Action<Guid?> _finished;
    private readonly DispatcherTimer _scrollTimer;
    private Point _start;
    private Point _pointer;
    private Guid? _pending;
    private Guid? _source;
    private AdornerLayer? _layer;
    private Cue? _cue;
    public bool IsDragging => _source is not null;

    public TodoDragDrop(ListBox list, DesktopPetRepository repository, Action<Guid?> finished)
    {
        _list = list;
        _repository = repository;
        _finished = finished;
        _scrollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        list.AllowDrop = true;
        list.PreviewMouseLeftButtonDown += MouseDown;
        list.PreviewMouseMove += MouseMove;
        list.DragOver += DragOver;
        list.Drop += Drop;
        list.DragLeave += (_, _) => { if (!list.IsMouseOver) { ClearCue(); _scrollTimer.Stop(); } };
        _scrollTimer.Tick += (_, _) =>
        {
            var scroll = ScrollViewerOf(_list);
            if (scroll is null) return;
            if (_pointer.Y < 24) scroll.LineUp();
            else if (_pointer.Y > _list.ActualHeight - 24) scroll.LineDown();
            else return;
            UpdateCue();
        };
    }

    private void MouseDown(object sender, MouseButtonEventArgs e)
    {
        _pending = null;
        for (var node = e.OriginalSource as DependencyObject; node is not null && node != _list; node = Parent(node))
            if (node is ButtonBase or TextBoxBase or ScrollBar or Thumb) return;
        if (Container(e.OriginalSource as DependencyObject)?.DataContext is TodoDisplayItem row)
        {
            _pending = row.Item.Id;
            _start = e.GetPosition(_list);
        }
    }

    private void MouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed) { _pending = null; return; }
        if (_pending is not { } id) return;
        var point = e.GetPosition(_list);
        if (Math.Abs(point.X - _start.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _start.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        _pending = null;
        _source = id;
        try { DragDrop.DoDragDrop(_list, new DataObject("DesktopPet.Todo", id), DragDropEffects.Move); }
        finally
        {
            _source = null;
            _scrollTimer.Stop();
            ClearCue();
            _finished(null);
        }
        e.Handled = true;
    }

    private void DragOver(object sender, DragEventArgs e)
    {
        if (_source is null || e.Data.GetData("DesktopPet.Todo") is not Guid id || id != _source)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        _pointer = e.GetPosition(_list);
        e.Effects = UpdateCue() is null ? DragDropEffects.None : DragDropEffects.Move;
        _scrollTimer.Start();
        e.Handled = true;
    }

    private void Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        _scrollTimer.Stop();
        if (_source is not { } id || e.Data.GetData("DesktopPet.Todo") is not Guid payload || payload != id) return;
        _pointer = e.GetPosition(_list);
        var placement = Resolve();
        ClearCue();
        if (placement is null) return;
        try
        {
            if (_repository.MoveTodo(id, placement.ParentId, placement.AnchorId, placement.After))
                _finished(placement.ParentId);
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(Window.GetWindow(_list), exception.Message, "移动待办", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private Placement? Resolve()
    {
        if (_source is not { } id) return null;
        var items = _repository.GetTodos();
        var source = items.FirstOrDefault(item => item.Id == id);
        if (source is null) return null;
        var hit = _list.InputHitTest(_pointer) as DependencyObject;
        for (var node = hit; node is not null && node != _list; node = Parent(node))
            if (node is ScrollBar or Thumb) return null;
        var container = Container(hit);
        Guid? parentId = null;
        Guid? anchorId = null;
        var after = true;
        var into = false;
        if (container?.DataContext is TodoDisplayItem row)
        {
            var target = items.FirstOrDefault(item => item.Id == row.Item.Id);
            if (target is null || target.Id == id) return null;
            var y = _list.TranslatePoint(_pointer, container).Y;
            var edge = Math.Min(10, container.ActualHeight * 0.25);
            into = target.ParentId is null && y >= edge && y <= container.ActualHeight - edge;
            if (into) parentId = target.Id;
            else
            {
                parentId = target.ParentId;
                anchorId = target.Id;
                after = y >= container.ActualHeight / 2;
            }
        }
        else if (_pointer.Y < 0 || _pointer.Y > _list.ActualHeight) return null;
        // Reject parents with children, self-parenting and stale targets.
        if (parentId == id || (parentId is not null &&
            (!items.Any(item => item.Id == parentId && item.ParentId is null) || items.Any(item => item.ParentId == id)))) return null;

        var siblings = items.Where(item => item.Id != id && item.ParentId == parentId && item.IsCompleted == source.IsCompleted).ToList();
        var index = siblings.Count;
        if (anchorId is { } anchor)
        {
            var target = items.First(item => item.Id == anchor);
            index = target.IsCompleted != source.IsCompleted ? (source.IsCompleted ? 0 : siblings.Count)
                : siblings.FindIndex(item => item.Id == anchor) + (after ? 1 : 0);
        }
        var original = items.Where(item => item.ParentId == parentId && item.IsCompleted == source.IsCompleted).ToList();
        if (source.ParentId == parentId && original.FindIndex(item => item.Id == id) == index) return null;
        return new Placement(parentId, anchorId, after, into, index);
    }

    private Placement? UpdateCue()
    {
        ClearCue();
        var placement = Resolve();
        if (placement is null) return null;
        var rows = _list.Items.Cast<TodoDisplayItem>().ToList();
        FrameworkElement? target = null;
        var bottom = false;
        var into = placement.Into;
        if (into)
            target = ContainerFor(rows.FindIndex(row => row.Item.Id == placement.ParentId));
        else
        {
            var source = _repository.GetTodos().FirstOrDefault(item => item.Id == _source);
            if (source is null) return null;
            var siblings = rows.Where(row => row.Item.Id != _source && row.Item.ParentId == placement.ParentId &&
                row.Item.IsCompleted == source.IsCompleted).ToList();
            if (placement.Index < siblings.Count)
                target = ContainerFor(rows.IndexOf(siblings[placement.Index]));
            else if (siblings.Count > 0)
            {
                var last = siblings[^1];
                var index = rows.IndexOf(last);
                if (placement.ParentId is null)
                    while (index + 1 < rows.Count && rows[index + 1].IsChild) index++;
                target = ContainerFor(index);
                bottom = true;
            }
            else if (placement.ParentId is { } parent)
            {
                var index = rows.FindIndex(row => row.Item.Id == parent);
                if (source.IsCompleted)
                    while (index + 1 < rows.Count && rows[index + 1].Item.ParentId == parent) index++;
                target = ContainerFor(index);
                bottom = true;
            }
            else
            {
                // The requested status group is empty: completed roots follow all unfinished groups.
                var index = source.IsCompleted ? rows.Count - 1 : 0;
                target = ContainerFor(index);
                bottom = source.IsCompleted;
            }
        }
        target ??= _list;
        _layer = AdornerLayer.GetAdornerLayer(target);
        if (_layer is not null)
        {
            _cue = new Cue(target, into, bottom, placement.ParentId is not null && !into);
            _layer.Add(_cue);
        }
        return placement;
    }

    private FrameworkElement? ContainerFor(int index) =>
        index < 0 ? null : _list.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;

    private void ClearCue()
    {
        if (_layer is not null && _cue is not null) _layer.Remove(_cue);
        _layer = null;
        _cue = null;
    }

    private ListBoxItem? Container(DependencyObject? node)
    {
        while (node is not null && node != _list)
        {
            if (node is ListBoxItem item && ItemsControl.ItemsControlFromItemContainer(item) == _list) return item;
            node = Parent(node);
        }
        return null;
    }

    private static DependencyObject? Parent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    internal static ScrollViewer? ScrollViewerOf(DependencyObject root)
    {
        if (root is ScrollViewer scroll) return scroll;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (ScrollViewerOf(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    private sealed record Placement(Guid? ParentId, Guid? AnchorId, bool After, bool Into, int Index);

    private sealed class Cue : Adorner
    {
        private readonly bool into;
        private readonly bool bottom;
        private readonly bool child;
        public Cue(UIElement element, bool into, bool bottom, bool child) : base(element)
        {
            this.into = into; this.bottom = bottom; this.child = child;
            IsHitTestVisible = false;
        }
        protected override void OnRender(DrawingContext context)
        {
            var accent = new SolidColorBrush(Color.FromRgb(125, 88, 166));
            if (into)
            {
                context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(38, 125, 88, 166)),
                    new Pen(accent, 1), new Rect(1, 1, Math.Max(0, ActualWidth - 2), Math.Max(0, ActualHeight - 2)), 5, 5);
                var text = new FormattedText("设为子待办", System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight, new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal), 10, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var rect = new Rect(Math.Max(0, ActualWidth - text.Width - 8), 0, text.Width + 8, text.Height + 4);
                context.DrawRoundedRectangle(accent, null, rect, 3, 3);
                context.DrawText(text, new Point(rect.X + 4, 2));
            }
            else
            {
                var y = bottom ? Math.Max(1, ActualHeight - 1) : 1;
                context.DrawLine(new Pen(accent, 2), new Point(child ? 24 : 3, y), new Point(Math.Max(3, ActualWidth - 3), y));
            }
        }
    }
}
