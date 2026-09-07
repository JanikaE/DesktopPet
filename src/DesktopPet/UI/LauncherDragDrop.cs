using DesktopPet.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopPet.UI;

internal sealed class LauncherDragDrop(ListBox listBox, DesktopPetRepository repository)
{
    private Point _mouseDownPoint;
    private LauncherItem? _draggedItem;

    public void PreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _mouseDownPoint = e.GetPosition(listBox);
        _draggedItem = FindItem(e.OriginalSource as DependencyObject);
    }

    public void PreviewMouseMove(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem is null) return;
        var point = e.GetPosition(listBox);
        if (Math.Abs(point.X - _mouseDownPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _mouseDownPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _draggedItem;
        _draggedItem = null;
        DragDrop.DoDragDrop(listBox, item, DragDropEffects.Move);
    }

    public void Drop(DragEventArgs e)
    {
        if (e.Data.GetData(typeof(LauncherItem)) is not LauncherItem source) return;
        var target = FindItem(e.OriginalSource as DependencyObject);
        if (target is null || target.Id == source.Id) return;

        var items = repository.GetLaunchers().ToList();
        var sourceIndex = items.FindIndex(item => item.Id == source.Id);
        var targetIndex = items.FindIndex(item => item.Id == target.Id);
        if (sourceIndex < 0 || targetIndex < 0) return;
        items.RemoveAt(sourceIndex);
        targetIndex = items.FindIndex(item => item.Id == target.Id);
        items.Insert(targetIndex, source);
        repository.ReorderLaunchers(items.Select(item => item.Id).ToArray());
        e.Handled = true;
    }

    private LauncherItem? FindItem(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
            source = System.Windows.Media.VisualTreeHelper.GetParent(source);
        return source is ListBoxItem container ? container.DataContext as LauncherItem : null;
    }
}
