using DesktopPet.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace DesktopPet.UI;

internal sealed class LauncherDragDrop
{
    private readonly ListBox _listBox;
    private readonly DesktopPetRepository _repository;
    private Point _mouseDownPoint;
    private LauncherItem? _draggedItem;
    private AdornerLayer? _adornerLayer;
    private LauncherDropAdorner? _dropAdorner;
    private int _dropIndex = -1;

    public LauncherDragDrop(ListBox listBox, DesktopPetRepository repository)
    {
        _listBox = listBox;
        _repository = repository;
        _listBox.DragOver += DragOver;
        _listBox.DragLeave += DragLeave;
    }

    public void PreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        _mouseDownPoint = e.GetPosition(_listBox);
        _draggedItem = FindItem(e.OriginalSource as DependencyObject);
    }

    public void PreviewMouseMove(MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggedItem is null) return;
        var point = e.GetPosition(_listBox);
        if (Math.Abs(point.X - _mouseDownPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(point.Y - _mouseDownPoint.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        var item = _draggedItem;
        _draggedItem = null;
        try
        {
            DragDrop.DoDragDrop(_listBox, item, DragDropEffects.Move);
        }
        finally
        {
            HideDropCue();
        }
    }

    public void Drop(DragEventArgs e)
    {
        if (e.Data.GetData(typeof(LauncherItem)) is not LauncherItem source) return;
        var items = _repository.GetLaunchers().ToList();
        var sourceIndex = items.FindIndex(item => item.Id == source.Id);
        var dropIndex = GetDropIndex(e, sourceIndex, items.Count);
        HideDropCue();
        if (sourceIndex < 0 || dropIndex < 0) return;

        e.Handled = true;
        if (sourceIndex == dropIndex) return;

        items.RemoveAt(sourceIndex);
        items.Insert(dropIndex, source);
        _repository.ReorderLaunchers(items.Select(item => item.Id).ToArray());
    }

    private void DragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(LauncherItem)) is not LauncherItem source)
        {
            e.Effects = DragDropEffects.None;
            HideDropCue();
            return;
        }

        var sourceIndex = _listBox.Items.IndexOf(source);
        var dropIndex = GetDropIndex(e, sourceIndex, _listBox.Items.Count);
        if (dropIndex < 0)
        {
            e.Effects = DragDropEffects.None;
            HideDropCue();
            return;
        }

        e.Effects = DragDropEffects.Move;
        ShowDropCue(dropIndex);
        e.Handled = true;
    }

    private void DragLeave(object sender, DragEventArgs e)
    {
        if (!_listBox.IsMouseOver) HideDropCue();
    }

    private int GetDropIndex(DragEventArgs e, int sourceIndex, int itemCount)
    {
        if (sourceIndex < 0 || itemCount == 0) return -1;

        var targetContainer = FindContainer(e.OriginalSource as DependencyObject);
        int insertionIndex;
        if (targetContainer is not null)
        {
            var targetIndex = _listBox.ItemContainerGenerator.IndexFromContainer(targetContainer);
            var point = e.GetPosition(targetContainer);
            insertionIndex = targetIndex + (point.X >= targetContainer.ActualWidth / 2 ? 1 : 0);
        }
        else
        {
            insertionIndex = FindNearestInsertionIndex(e.GetPosition(_listBox), itemCount);
        }

        if (insertionIndex > sourceIndex) insertionIndex--;
        return Math.Clamp(insertionIndex, 0, itemCount - 1);
    }

    private int FindNearestInsertionIndex(Point point, int itemCount)
    {
        var nearestIndex = 0;
        var nearestDistance = double.MaxValue;
        var insertAfter = false;
        var top = double.MaxValue;
        var bottom = double.MinValue;

        for (var index = 0; index < itemCount; index++)
        {
            if (_listBox.ItemContainerGenerator.ContainerFromIndex(index) is not ListBoxItem container) continue;
            var topLeft = container.TranslatePoint(new Point(), _listBox);
            top = Math.Min(top, topLeft.Y);
            bottom = Math.Max(bottom, topLeft.Y + container.ActualHeight);
            var center = new Point(topLeft.X + container.ActualWidth / 2, topLeft.Y + container.ActualHeight / 2);
            var distance = Math.Pow(point.X - center.X, 2) + Math.Pow(point.Y - center.Y, 2);
            if (distance >= nearestDistance) continue;
            nearestDistance = distance;
            nearestIndex = index;
            insertAfter = point.X >= center.X;
        }

        if (point.Y < top) return 0;
        if (point.Y > bottom) return itemCount;
        return nearestIndex + (insertAfter ? 1 : 0);
    }

    private void ShowDropCue(int dropIndex)
    {
        if (_dropIndex == dropIndex && _dropAdorner is not null) return;
        HideDropCue();
        if (_listBox.ItemContainerGenerator.ContainerFromIndex(dropIndex) is not ListBoxItem container) return;

        _adornerLayer = AdornerLayer.GetAdornerLayer(container);
        if (_adornerLayer is null) return;
        _dropIndex = dropIndex;
        _dropAdorner = new LauncherDropAdorner(container, dropIndex + 1);
        _adornerLayer.Add(_dropAdorner);
    }

    private void HideDropCue()
    {
        if (_adornerLayer is not null && _dropAdorner is not null) _adornerLayer.Remove(_dropAdorner);
        _adornerLayer = null;
        _dropAdorner = null;
        _dropIndex = -1;
    }

    private LauncherItem? FindItem(DependencyObject? source) => FindContainer(source)?.DataContext as LauncherItem;

    private static ListBoxItem? FindContainer(DependencyObject? source)
    {
        while (source is not null && source is not ListBoxItem)
            source = VisualTreeHelper.GetParent(source);
        return source as ListBoxItem;
    }

    private sealed class LauncherDropAdorner : Adorner
    {
        private static readonly Brush Fill = new SolidColorBrush(Color.FromArgb(54, 125, 88, 166));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(125, 88, 166));
        private static readonly Pen Outline = new(Accent, 2) { DashStyle = DashStyles.Dash };
        private readonly int _position;

        public LauncherDropAdorner(UIElement adornedElement, int position) : base(adornedElement)
        {
            _position = position;
            IsHitTestVisible = false;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            var bounds = new Rect(3, 3, Math.Max(0, ActualWidth - 6), Math.Max(0, ActualHeight - 6));
            drawingContext.DrawRoundedRectangle(Fill, Outline, bounds, 10, 10);

            var label = new FormattedText(
                $"第 {_position} 位",
                System.Globalization.CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                new Typeface(SystemFonts.MessageFontFamily, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                11,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip);
            var labelBounds = new Rect(
                Math.Max(5, bounds.Right - label.Width - 13),
                bounds.Top + 6,
                label.Width + 8,
                label.Height + 4);
            drawingContext.DrawRoundedRectangle(Accent, null, labelBounds, 6, 6);
            drawingContext.DrawText(label, new Point(labelBounds.Left + 4, labelBounds.Top + 2));
        }
    }
}
