using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace DesktopPet.UI;

/// <summary>
/// Keeps a companion window beside the pet and allows it to move vertically
/// between top alignment and bottom alignment when the pet is taller.
/// </summary>
internal sealed class CompanionWindowDragController
{
    private const double CompanionGap = 12;
    private readonly Window _petWindow;
    private readonly Window _companionWindow;
    private double _alignment = 1;
    private double _pointerStartY;
    private double _windowStartTop;
    private bool _pointerCaptured;
    private bool _isDragging;

    public CompanionWindowDragController(Window petWindow, Window companionWindow)
    {
        _petWindow = petWindow;
        _companionWindow = companionWindow;
        _companionWindow.AddHandler(Mouse.PreviewMouseDownEvent, new MouseButtonEventHandler(OnPreviewMouseDown), true);
        _companionWindow.AddHandler(Mouse.PreviewMouseMoveEvent, new MouseEventHandler(OnPreviewMouseMove), true);
        _companionWindow.AddHandler(Mouse.PreviewMouseUpEvent, new MouseButtonEventHandler(OnPreviewMouseUp), true);
        _companionWindow.LostMouseCapture += OnLostMouseCapture;
    }

    public void PositionNextToPet()
    {
        var petHeight = WindowHeight(_petWindow);
        var companionHeight = WindowHeight(_companionWindow);
        _companionWindow.Left = CompanionLeft();
        _companionWindow.Top = petHeight > companionHeight
            ? _petWindow.Top + _alignment * (petHeight - companionHeight)
            : _petWindow.Top + petHeight - companionHeight;
    }

    private double CompanionLeft()
    {
        var companionWidth = WindowWidth(_companionWindow);
        var leftOfPet = _petWindow.Left - companionWidth - CompanionGap;
        var source = PresentationSource.FromVisual(_petWindow);
        var handle = new WindowInteropHelper(_petWindow).Handle;
        if (source?.CompositionTarget is null || handle == IntPtr.Zero) return leftOfPet;

        // Screen.WorkingArea is expressed in physical pixels while WPF window
        // positions use device-independent pixels. Compare relative to the pet's
        // on-screen position so this also works on per-monitor-DPI displays.
        var scaleX = source.CompositionTarget.TransformToDevice.M11;
        if (scaleX <= 0) return leftOfPet;

        var petScreenLeft = _petWindow.PointToScreen(new Point(0, 0)).X;
        var petWidthInPixels = WindowWidth(_petWindow) * scaleX;
        var companionWidthInPixels = companionWidth * scaleX;
        var gapInPixels = CompanionGap * scaleX;
        var workingArea = Forms.Screen.FromHandle(handle).WorkingArea;

        var targetLeft = petScreenLeft - companionWidthInPixels - gapInPixels;
        if (targetLeft < workingArea.Left)
            targetLeft = petScreenLeft + petWidthInPixels + gapInPixels;

        var rightmostLeft = workingArea.Right - companionWidthInPixels;
        targetLeft = rightmostLeft < workingArea.Left
            ? workingArea.Left
            : Math.Clamp(targetLeft, workingArea.Left, rightmostLeft);

        return _petWindow.Left + (targetLeft - petScreenLeft) / scaleX;
    }

    private void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left || !CanDrag() || IsInteractive(e.OriginalSource as DependencyObject)) return;

        _pointerStartY = PointerDesktopY();
        _windowStartTop = _companionWindow.Top;
        _pointerCaptured = Mouse.Capture(_companionWindow, CaptureMode.Element);
    }

    private void OnPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pointerCaptured) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            FinishDrag();
            return;
        }

        var delta = PointerDesktopY() - _pointerStartY;
        if (!_isDragging && Math.Abs(delta) < SystemParameters.MinimumVerticalDragDistance) return;
        _isDragging = true;
        _companionWindow.Cursor = Cursors.SizeNS;

        var range = DragRange();
        if (range <= 0)
        {
            FinishDrag();
            PositionNextToPet();
            return;
        }

        var offset = Math.Clamp(_windowStartTop + delta - _petWindow.Top, 0, range);
        _alignment = offset / range;
        _companionWindow.Top = _petWindow.Top + offset;
    }

    private void OnPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) FinishDrag();
    }

    private void OnLostMouseCapture(object sender, MouseEventArgs e)
    {
        _pointerCaptured = false;
        _isDragging = false;
        _companionWindow.ClearValue(FrameworkElement.CursorProperty);
    }

    private void FinishDrag()
    {
        _pointerCaptured = false;
        _isDragging = false;
        _companionWindow.ClearValue(FrameworkElement.CursorProperty);
        if (Mouse.Captured == _companionWindow) Mouse.Capture(null);
    }

    private bool CanDrag() => DragRange() > 0.5;

    private double DragRange() => WindowHeight(_petWindow) - WindowHeight(_companionWindow);

    private double PointerDesktopY()
    {
        var cursor = Forms.Cursor.Position;
        return _petWindow.Top + _petWindow.PointFromScreen(new Point(cursor.X, cursor.Y)).Y;
    }

    private static double WindowHeight(Window window) => window.ActualHeight > 0 ? window.ActualHeight : window.Height;

    private static double WindowWidth(Window window) => window.ActualWidth > 0 ? window.ActualWidth : window.Width;

    private static bool IsInteractive(DependencyObject? source)
    {
        for (var current = source; current is not null and not Window; current = ParentOf(current))
        {
            if (current is ButtonBase or TextBoxBase or ComboBox or ListBox or ScrollBar or Thumb or ScrollViewer or TabItem or Hyperlink)
                return true;
            if (current is FrameworkElement { Cursor: not null } element && element.Cursor == Cursors.Hand)
                return true;
        }
        return false;
    }

    private static DependencyObject? ParentOf(DependencyObject node) => node switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(node),
        FrameworkContentElement content => content.Parent,
        _ => LogicalTreeHelper.GetParent(node)
    };
}
