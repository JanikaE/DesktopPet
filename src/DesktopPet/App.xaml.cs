using DesktopPet.Core;
using DesktopPet.Data;
using DesktopPet.Pet;
using DesktopPet.UI;
using System.Windows;
using Forms = System.Windows.Forms;

namespace DesktopPet;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\DesktopPet.SingleInstance";
    private const string ActivateEventName = "Local\\DesktopPet.Activate";
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _petWindow;
    private SettingsWindow? _settingsWindow;
    private QuickAccessWindow? _quickAccessWindow;
    private double _quickAccessOffsetLeft;
    private double _quickAccessOffsetTop;
    private DesktopPetRepository? _repository;
    private System.Threading.Mutex? _instanceMutex;
    private System.Threading.EventWaitHandle? _activateEvent;
    private bool _isPrimaryInstance;
    private bool _restoreQuickAccessAfterTrayShow;

    protected override void OnStartup(StartupEventArgs e)
    {
        DpiAwareness.EnablePerMonitorV2();
        base.OnStartup(e);
        if (!AcquireSingleInstance())
        {
            Shutdown();
            return;
        }

        AppPaths.EnsureCreated();
        ApplicationShortcutService.EnsureDesktopAndStartupShortcuts();
        _repository = new DesktopPetRepository(AppPaths.DatabasePath);
        _repository.Initialize();

        _petWindow = new MainWindow(
            new PetStateMachine(),
            new PetImageStatePresenter(Path.Combine(AppContext.BaseDirectory, "Assets", "Pet")),
            new PetWindowPlacementStore(AppPaths.WindowPlacementPath));
        _petWindow.Closed += (_, _) => Shutdown();
        _petWindow.LocationChanged += (_, _) => MoveQuickAccessWithPet();
        _petWindow.MonitorDpiChanged += (_, _) => Dispatcher.BeginInvoke(MoveQuickAccessWithPet);
        _petWindow.SettingsRequested += (_, _) => ShowSettings();
        _petWindow.FeatureFlyoutRequested += ToggleFeatureFlyout;
        _petWindow.Show();

        _trayIcon = TrayIconFactory.Create(_petWindow, ToggleAppVisibility, ShowSettings, Shutdown);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _activateEvent?.Dispose();
        if (_isPrimaryInstance) _instanceMutex?.ReleaseMutex();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private bool AcquireSingleInstance()
    {
        _instanceMutex = new System.Threading.Mutex(initiallyOwned: true, MutexName, out var createdNew);
        if (!createdNew)
        {
            try { System.Threading.EventWaitHandle.OpenExisting(ActivateEventName).Set(); }
            catch (System.Threading.WaitHandleCannotBeOpenedException) { }
            return false;
        }

        _isPrimaryInstance = true;
        _activateEvent = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.AutoReset, ActivateEventName);
        var listener = new System.Threading.Thread(() =>
        {
            while (_activateEvent.WaitOne())
            {
                Dispatcher.BeginInvoke(ActivateExistingPet);
            }
        }) { IsBackground = true };
        listener.Start();
        return true;
    }

    private void ActivateExistingPet() => _petWindow?.ActivatePet();

    private void ToggleAppVisibility()
    {
        if (_petWindow is null) return;
        if (_petWindow.IsVisible)
        {
            _restoreQuickAccessAfterTrayShow = _quickAccessWindow is { IsVisible: true };
            _quickAccessWindow?.Hide();
            _petWindow.TogglePetVisibility();
            return;
        }

        _petWindow.TogglePetVisibility();
        if (_restoreQuickAccessAfterTrayShow) ShowFeatureFlyout();
        _restoreQuickAccessAfterTrayShow = false;
    }

    private void ShowSettings()
    {
        if (_petWindow is null) return;
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_petWindow, _repository!);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private void ToggleFeatureFlyout(bool show)
    {
        if (show) ShowFeatureFlyout();
        else _quickAccessWindow?.Hide();
    }

    private void ShowFeatureFlyout()
    {
        if (_repository is null || _petWindow is null) return;
        if (_quickAccessWindow is { IsVisible: true })
        {
            _quickAccessWindow.Activate();
            return;
        }

        if (_quickAccessWindow is null)
        {
            _quickAccessWindow = new QuickAccessWindow(_repository);
            _quickAccessWindow.Owner = _petWindow;
            _quickAccessWindow.Closed += (_, _) => _quickAccessWindow = null;
        }
        _quickAccessWindow.Height = _petWindow.Height;
        _quickAccessWindow.Left = _petWindow.Left - _quickAccessWindow.Width - 12;
        _quickAccessWindow.Top = _petWindow.Top;
        _quickAccessOffsetLeft = _quickAccessWindow.Left - _petWindow.Left;
        _quickAccessOffsetTop = _quickAccessWindow.Top - _petWindow.Top;
        _quickAccessWindow.Refresh();
        _quickAccessWindow.Show();
    }

    private void MoveQuickAccessWithPet()
    {
        if (_quickAccessWindow is not { IsVisible: true } || _petWindow is null) return;
        _quickAccessWindow.Left = _petWindow.Left + _quickAccessOffsetLeft;
        _quickAccessWindow.Top = _petWindow.Top + _quickAccessOffsetTop;
    }
}
