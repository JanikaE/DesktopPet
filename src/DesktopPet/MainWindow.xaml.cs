using DesktopPet.Core;
using DesktopPet.Pet;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace DesktopPet;

public partial class MainWindow : Window
{
    private const double VisibleMargin = 72;
    private readonly PetImageStatePresenter _imagePresenter;
    private readonly PetStateMachine _stateMachine;
    private readonly PetWindowPlacementStore _placementStore;
    private System.Windows.Point _mouseDownPosition;
    private bool _isDragging;
    private bool _wasDragging;
    private bool _isFeatureFlyoutVisible;

    public event EventHandler? SettingsRequested;
    public event EventHandler? MonitorDpiChanged;
    public event Action<bool>? FeatureFlyoutRequested;
    public bool IsPetTopmost => Topmost;

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
        SourceInitialized += (_, _) => HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WindowMessageHook);
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
    private void OpenSettings(object sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void ExitApplication(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

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
            SetPetTopmost(placement.Topmost);
        }

        KeepWindowVisible();
        _imagePresenter.Show(PetState.Idle);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SavePlacement();
    }

    private void SavePlacement() => _placementStore.Save(Left, Top, Topmost);

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
}
