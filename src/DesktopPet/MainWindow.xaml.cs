using DesktopPet.Core;
using DesktopPet.Pet;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace DesktopPet;

public partial class MainWindow : Window
{
    private const double VisibleMargin = 72;
    private const double AspectRatio = 2.0 / 3.0;
    private readonly PetImageStatePresenter _imagePresenter;
    private readonly PetStateMachine _stateMachine;
    private readonly PetWindowPlacementStore _placementStore;
    private System.Windows.Point _mouseDownPosition;
    private bool _isDragging;
    private bool _wasDragging;
    private bool _isFeatureFlyoutVisible;
    private bool _isResizeMode;
    private double _resizeStartLeft;
    private double _resizeStartTop;
    private double _resizeStartWidth;
    private double _resizeStartHeight;
    private string? _activeResizeEdge;
    private NativePoint _resizeStartPointer;
    private bool _isResizing;

    public event EventHandler? SettingsRequested;
    public event EventHandler? MonitorDpiChanged;
    public event Action<bool>? FeatureFlyoutRequested;
    public event EventHandler? KeyboardStatisticsRequested;
    public event EventHandler? MouseStatisticsRequested;
    public bool IsPetTopmost => Topmost;
    public HotkeyGesture? ToggleVisibilityHotkey { get; private set; }
    public HotkeyGesture? KeyboardStatisticsHotkey { get; private set; }
    public HotkeyGesture? MouseStatisticsHotkey { get; private set; }
    public bool[] MouseLegendHidden { get; private set; } = [false, false, false, false];
    public string KeyboardLayoutId { get; private set; } = "full-size-104";

    public MainWindow(PetStateMachine stateMachine, PetImageStatePresenter imagePresenter, PetWindowPlacementStore placementStore)
    {
        InitializeComponent();
        _stateMachine = stateMachine;
        _imagePresenter = imagePresenter;
        _placementStore = placementStore;
        _imagePresenter.ImageChanged += (_, image) => PetImage.Source = image;
        _stateMachine.StateChanged += (_, state) => _imagePresenter.Show(state);
        Loaded += OnLoaded;
        Closed += OnClosed;
        Deactivated += (_, _) =>
        {
            if (!_isResizing) ExitResizeMode();
        };
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessageHook);
    }

    private void PetPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizeMode || FindAncestor<Thumb>(e.OriginalSource as DependencyObject) is not null) return;
        ExitResizeMode();
        e.Handled = true;
    }

    private void PetMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _mouseDownPosition = e.GetPosition(this);
        _isDragging = false;
        _wasDragging = false;
        Mouse.Capture((IInputElement)sender);
    }

    private void PetMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _isDragging) return;
        var currentPosition = e.GetPosition(this);
        if (Math.Abs(currentPosition.X - _mouseDownPosition.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(currentPosition.Y - _mouseDownPosition.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _isDragging = true;
        _wasDragging = true;
        _stateMachine.BeginDrag();
        try
        {
            DragMove();
        }
        finally
        {
            if (_isFeatureFlyoutVisible) _stateMachine.PlayClick();
            else _stateMachine.EndDrag();
            SavePlacement();
            _isDragging = false;
        }
    }

    private void PetMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        Mouse.Capture(null);
        if (_wasDragging) return;
        ToggleFeatureFlyout();
    }

    private void PetMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        var menu = ((FrameworkElement)sender).ContextMenu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private void ToggleFeatureFlyout()
    {
        var shouldShow = _stateMachine.Current != PetState.Click;
        _isFeatureFlyoutVisible = shouldShow;
        if (shouldShow) _stateMachine.PlayClick();
        else _stateMachine.FinishAnimation();
        FeatureFlyoutRequested?.Invoke(shouldShow);
    }

    private void ToggleTopmost(object sender, RoutedEventArgs e) => SetPetTopmost(((System.Windows.Controls.MenuItem)sender).IsChecked);
    private void ToggleResizeMode(object sender, RoutedEventArgs e)
    {
        if (_isResizeMode) ExitResizeMode();
        else Dispatcher.BeginInvoke(EnterResizeMode, System.Windows.Threading.DispatcherPriority.ContextIdle);
    }
    private void OpenSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void OpenKeyboardStatistics(object sender, RoutedEventArgs e) => KeyboardStatisticsRequested?.Invoke(this, EventArgs.Empty);
    private void OpenMouseStatistics(object sender, RoutedEventArgs e) => MouseStatisticsRequested?.Invoke(this, EventArgs.Empty);
    private void ExitApplication(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void EnterResizeMode()
    {
        _isResizeMode = true;
        ResizeAdorner.Visibility = Visibility.Visible;
        ResizeMenuItem.IsChecked = true;
        Activate();
    }

    private void ExitResizeMode()
    {
        if (!_isResizeMode) return;
        _isResizeMode = false;
        ResizeAdorner.Visibility = Visibility.Collapsed;
        ResizeMenuItem.IsChecked = false;
        SavePlacement();
    }

    private void ResizeStarted(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Thumb { Tag: string edge } || !_isResizeMode) return;
        _resizeStartLeft = Left;
        _resizeStartTop = Top;
        _resizeStartWidth = ActualWidth;
        _resizeStartHeight = ActualHeight;
        _activeResizeEdge = edge;
        GetCursorPos(out _resizeStartPointer);
        _isResizing = true;
        Mouse.Capture(PetRoot, CaptureMode.Element);
        e.Handled = true;
    }

    private void ResizeMouseMoved(object sender, MouseEventArgs e)
    {
        if (!_isResizing || _activeResizeEdge is not { } edge) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CompleteResize();
            return;
        }

        GetCursorPos(out var pointer);
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null) return;
        var change = source.CompositionTarget.TransformFromDevice.Transform(
            new Vector(pointer.X - _resizeStartPointer.X, pointer.Y - _resizeStartPointer.Y));

        var horizontalDirection = edge.Contains("Left", StringComparison.Ordinal) ? -1.0 : 1.0;
        var verticalDirection = edge.Contains("Top", StringComparison.Ordinal) ? -1.0 : 1.0;
        var changesWidth = edge is "Left" or "Right" || edge.Contains("Left", StringComparison.Ordinal) || edge.Contains("Right", StringComparison.Ordinal);
        var changesHeight = edge is "Top" or "Bottom" || edge.Contains("Top", StringComparison.Ordinal) || edge.Contains("Bottom", StringComparison.Ordinal);
        var widthFromHorizontal = _resizeStartWidth + horizontalDirection * change.X;
        var widthFromVertical = (_resizeStartHeight + verticalDirection * change.Y) * AspectRatio;
        var requestedWidth = changesWidth && changesHeight
            ? (Math.Abs(widthFromHorizontal - _resizeStartWidth) >= Math.Abs(widthFromVertical - _resizeStartWidth)
                ? widthFromHorizontal
                : widthFromVertical)
            : changesWidth ? widthFromHorizontal : widthFromVertical;
        var newWidth = Math.Max(MinWidth, requestedWidth);
        var newHeight = newWidth / AspectRatio;

        if (edge.Contains("Left", StringComparison.Ordinal)) Left = _resizeStartLeft + _resizeStartWidth - newWidth;
        if (edge.Contains("Top", StringComparison.Ordinal)) Top = _resizeStartTop + _resizeStartHeight - newHeight;
        Width = newWidth;
        Height = newHeight;
        e.Handled = true;
    }

    private void ResizeMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizing) return;
        CompleteResize();
        e.Handled = true;
    }

    private void ResizeMouseCaptureLost(object sender, MouseEventArgs e)
    {
        if (_isResizing) CompleteResize();
    }

    private void CompleteResize()
    {
        var exitAfterResize = !IsActive;
        _isResizing = false;
        _activeResizeEdge = null;
        if (Mouse.Captured == PetRoot) Mouse.Capture(null);
        KeepWindowVisible();
        SavePlacement();
        if (exitAfterResize) ExitResizeMode();
    }

    private static T? FindAncestor<T>(DependencyObject? source) where T : DependencyObject
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is T match) return match;
        }
        return null;
    }

    public void TogglePetTopmost() => SetPetTopmost(!Topmost);

    public void SetPetTopmost(bool value)
    {
        Topmost = value;
        TopmostMenuItem.IsChecked = value;
        SavePlacement();
    }

    public void TogglePetVisibility()
    {
        if (IsVisible)
        {
            Hide();
            return;
        }

        Show();
        Activate();
    }

    public void ActivatePet()
    {
        if (!IsVisible) Show();
        WindowState = WindowState.Normal;
        var wasTopmost = Topmost;
        Activate();
        Topmost = true;
        Topmost = wasTopmost;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var placement = _placementStore.Load();
        if (placement is null)
        {
            Left = SystemParameters.WorkArea.Right - Width - VisibleMargin;
            Top = SystemParameters.WorkArea.Bottom - Height - VisibleMargin;
        }
        else
        {
            Left = placement.Left;
            Top = placement.Top;
            Width = Math.Max(MinWidth, placement.Width);
            Height = Width / AspectRatio;
            ToggleVisibilityHotkey = placement.Hotkey;
            KeyboardStatisticsHotkey = placement.KeyboardStatisticsHotkey;
            MouseStatisticsHotkey = placement.MouseStatisticsHotkey;
            if (placement.MouseLegendHidden is { Length: 4 } legendHidden) MouseLegendHidden = legendHidden;
            KeyboardLayoutId = placement.KeyboardLayoutId;
            SetPetTopmost(placement.Topmost);
        }

        KeepWindowVisible();
        _imagePresenter.Show(PetState.Idle);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SavePlacement();
    }

    public void SetToggleVisibilityHotkey(HotkeyGesture? hotkey)
    {
        ToggleVisibilityHotkey = hotkey;
        SavePlacement();
    }

    public void SetKeyboardStatisticsHotkey(HotkeyGesture? hotkey)
    {
        KeyboardStatisticsHotkey = hotkey;
        SavePlacement();
    }

    public void SetMouseStatisticsHotkey(HotkeyGesture? hotkey)
    {
        MouseStatisticsHotkey = hotkey;
        SavePlacement();
    }

    public void SetMouseLegendHidden(bool[] hidden)
    {
        MouseLegendHidden = hidden;
        SavePlacement();
    }

    public void SetKeyboardLayout(string layoutId)
    {
        KeyboardLayoutId = layoutId;
        SavePlacement();
    }

    public void SetCompanionWindowVisible(bool visible)
    {
        _isFeatureFlyoutVisible = visible;
        if (visible) _stateMachine.PlayClick();
        else _stateMachine.FinishAnimation();
    }

    private void SavePlacement() => _placementStore.Save(
        Left,
        Top,
        Width,
        Topmost,
        ToggleVisibilityHotkey,
        KeyboardStatisticsHotkey,
        MouseStatisticsHotkey,
        MouseLegendHidden,
        KeyboardLayoutId);

    private void KeepWindowVisible()
    {
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;
        var virtualRight = virtualLeft + SystemParameters.VirtualScreenWidth;
        var virtualBottom = virtualTop + SystemParameters.VirtualScreenHeight;
        Left = Math.Clamp(Left, virtualLeft - Width + VisibleMargin, virtualRight - VisibleMargin);
        Top = Math.Clamp(Top, virtualTop - Height + VisibleMargin, virtualBottom - VisibleMargin);
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WindowMessageDpiChanged = 0x02E0;
        if (message == WindowMessageDpiChanged) Dispatcher.BeginInvoke(() => MonitorDpiChanged?.Invoke(this, EventArgs.Empty));
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }
}
