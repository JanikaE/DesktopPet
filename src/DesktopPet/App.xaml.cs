using DesktopPet.Core;
using DesktopPet.Core.Sync;
using DesktopPet.Data;
using DesktopPet.Pet;
using DesktopPet.UI;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace DesktopPet;

public partial class App : System.Windows.Application
{
    private const string MutexName = "Local\\DesktopPet.SingleInstance";
    private const string ActivateEventName = "Local\\DesktopPet.Activate";
    private const int VisibilityHotkeyId = 0x4450;
    private const int KeyboardStatisticsHotkeyId = 0x4451;
    private const int MouseStatisticsHotkeyId = 0x4452;
    private Forms.NotifyIcon? _trayIcon;
    private MainWindow? _petWindow;
    private SettingsWindow? _settingsWindow;
    private QuickAccessWindow? _quickAccessWindow;
    private KeyboardStatisticsWindow? _keyboardStatisticsWindow;
    private MouseStatisticsWindow? _mouseStatisticsWindow;
    private double _quickAccessOffsetLeft;
    private double _quickAccessOffsetTop;
    private DesktopPetRepository? _repository;
    private OneDriveSyncService? _oneDriveSyncService;
    private System.Threading.Mutex? _instanceMutex;
    private System.Threading.EventWaitHandle? _activateEvent;
    private bool _isPrimaryInstance;
    private bool _restoreQuickAccessAfterTrayShow;
    private bool _restoreKeyboardStatisticsAfterTrayShow;
    private bool _restoreMouseStatisticsAfterTrayShow;
    private GlobalHotkey? _visibilityHotkey;
    private GlobalHotkey? _keyboardStatisticsHotkey;
    private GlobalHotkey? _mouseStatisticsHotkey;
    private KeyboardStatisticsService? _keyboardStatisticsService;
    private MouseStatisticsService? _mouseStatisticsService;

    protected override void OnStartup(StartupEventArgs e)
    {
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
        _oneDriveSyncService = new OneDriveSyncService(_repository);
        _oneDriveSyncService.Start();
        _keyboardStatisticsService = new KeyboardStatisticsService(_repository);
        _mouseStatisticsService = new MouseStatisticsService(_repository);

        _petWindow = new MainWindow(
            new PetStateMachine(),
            new PetImageStatePresenter(Path.Combine(AppContext.BaseDirectory, "Assets", "Pet")),
            new PetWindowPlacementStore(AppPaths.WindowPlacementPath));
        _petWindow.Closed += (_, _) => Shutdown();
        _petWindow.LocationChanged += (_, _) => MoveCompanionWindowsWithPet();
        _petWindow.MonitorDpiChanged += (_, _) => Dispatcher.BeginInvoke(MoveCompanionWindowsWithPet);
        _petWindow.SettingsRequested += (_, _) => ShowSettings();
        _petWindow.KeyboardStatisticsRequested += (_, _) => ShowKeyboardStatistics();
        _petWindow.MouseStatisticsRequested += (_, _) => ShowMouseStatistics();
        _petWindow.FeatureFlyoutRequested += ToggleFeatureFlyout;
        _petWindow.Show();

        var petHandle = new WindowInteropHelper(_petWindow).Handle;
        _visibilityHotkey = new GlobalHotkey(petHandle, VisibilityHotkeyId, ToggleAppVisibility);
        if (_petWindow.ToggleVisibilityHotkey is { } visibilityHotkey && !_visibilityHotkey.TrySet(visibilityHotkey))
            MessageBox.Show("已保存的显示 / 隐藏快捷键被其他程序占用，请在设置中重新配置。", "DesktopPet");

        _keyboardStatisticsHotkey = new GlobalHotkey(petHandle, KeyboardStatisticsHotkeyId, ToggleKeyboardStatistics);
        if (_petWindow.KeyboardStatisticsHotkey is { } keyboardHotkey && !_keyboardStatisticsHotkey.TrySet(keyboardHotkey))
            MessageBox.Show("已保存的打开键盘统计快捷键被其他程序占用，请在设置中重新配置。", "DesktopPet");

        _mouseStatisticsHotkey = new GlobalHotkey(petHandle, MouseStatisticsHotkeyId, ToggleMouseStatistics);
        if (_petWindow.MouseStatisticsHotkey is { } mouseHotkey && !_mouseStatisticsHotkey.TrySet(mouseHotkey))
            MessageBox.Show("已保存的打开鼠标统计快捷键被其他程序占用，请在设置中重新配置。", "DesktopPet");

        _trayIcon = TrayIconFactory.Create(_petWindow, ToggleAppVisibility, ShowSettings, Shutdown);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _visibilityHotkey?.Dispose();
        _keyboardStatisticsHotkey?.Dispose();
        _mouseStatisticsHotkey?.Dispose();
        _keyboardStatisticsService?.Dispose();
        _mouseStatisticsService?.Dispose();
        _oneDriveSyncService?.Dispose();
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
            _restoreKeyboardStatisticsAfterTrayShow = _keyboardStatisticsWindow is { IsVisible: true };
            _restoreMouseStatisticsAfterTrayShow = _mouseStatisticsWindow is { IsVisible: true };
            _quickAccessWindow?.Hide();
            _keyboardStatisticsWindow?.Hide();
            _mouseStatisticsWindow?.Hide();
            _petWindow.TogglePetVisibility();
            return;
        }

        _petWindow.TogglePetVisibility();
        if (_restoreKeyboardStatisticsAfterTrayShow) ShowKeyboardStatistics();
        else if (_restoreMouseStatisticsAfterTrayShow) ShowMouseStatistics();
        else if (_restoreQuickAccessAfterTrayShow) ShowFeatureFlyout();
        _restoreQuickAccessAfterTrayShow = false;
        _restoreKeyboardStatisticsAfterTrayShow = false;
        _restoreMouseStatisticsAfterTrayShow = false;
    }

    private void ShowSettings()
    {
        ShowSettings(openLauncherTab: false);
    }

    private void ShowSettingsLauncherTab()
    {
        ShowSettings(openLauncherTab: true);
    }

    private void ShowSettings(bool openLauncherTab)
    {
        if (_petWindow is null) return;
        if (_settingsWindow is { IsVisible: true })
        {
            if (openLauncherTab) _settingsWindow.ActivateLauncherTab();
            else _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _petWindow,
            _repository!,
            _oneDriveSyncService!,
            SetVisibilityHotkey,
            SetKeyboardStatisticsHotkey,
            SetMouseStatisticsHotkey);
        if (openLauncherTab) _settingsWindow.ActivateLauncherTab();
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    private bool SetVisibilityHotkey(HotkeyGesture? hotkey)
    {
        if (_visibilityHotkey is null || _petWindow is null || !_visibilityHotkey.TrySet(hotkey)) return false;
        _petWindow.SetToggleVisibilityHotkey(hotkey);
        return true;
    }

    private bool SetKeyboardStatisticsHotkey(HotkeyGesture? hotkey)
    {
        if (_keyboardStatisticsHotkey is null || _petWindow is null || !_keyboardStatisticsHotkey.TrySet(hotkey)) return false;
        _petWindow.SetKeyboardStatisticsHotkey(hotkey);
        return true;
    }

    private bool SetMouseStatisticsHotkey(HotkeyGesture? hotkey)
    {
        if (_mouseStatisticsHotkey is null || _petWindow is null || !_mouseStatisticsHotkey.TrySet(hotkey)) return false;
        _petWindow.SetMouseStatisticsHotkey(hotkey);
        return true;
    }

    private void ToggleFeatureFlyout(bool show)
    {
        if (show) ShowFeatureFlyout();
        else
        {
            _quickAccessWindow?.Hide();
            _keyboardStatisticsWindow?.Hide();
            _mouseStatisticsWindow?.Hide();
        }
    }

    private void ShowFeatureFlyout()
    {
        if (_repository is null || _petWindow is null) return;
        _keyboardStatisticsWindow?.Hide();
        _mouseStatisticsWindow?.Hide();
        if (_quickAccessWindow is { IsVisible: true })
        {
            _quickAccessWindow.Activate();
            return;
        }

        if (_quickAccessWindow is null)
        {
            _quickAccessWindow = new QuickAccessWindow(_repository, ShowSettingsLauncherTab);
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

    private void ShowKeyboardStatistics()
    {
        if (_repository is null || _petWindow is null || _keyboardStatisticsService is null) return;
        _quickAccessWindow?.Hide();
        _mouseStatisticsWindow?.Hide();
        if (_keyboardStatisticsWindow is null)
        {
            _keyboardStatisticsWindow = new KeyboardStatisticsWindow(_repository, _keyboardStatisticsService) { Owner = _petWindow };
            _keyboardStatisticsWindow.Closed += (_, _) => _keyboardStatisticsWindow = null;
        }
        _keyboardStatisticsWindow.Left = _petWindow.Left - _keyboardStatisticsWindow.Width - 12;
        _keyboardStatisticsWindow.Top = _petWindow.Top;
        _keyboardStatisticsWindow.RefreshStatistics();
        _petWindow.SetCompanionWindowVisible(true);
        if (!_keyboardStatisticsWindow.IsVisible) _keyboardStatisticsWindow.Show();
        else _keyboardStatisticsWindow.Activate();
    }

    private void ShowMouseStatistics()
    {
        if (_repository is null || _petWindow is null || _mouseStatisticsService is null) return;
        _quickAccessWindow?.Hide();
        _keyboardStatisticsWindow?.Hide();
        if (_mouseStatisticsWindow is null)
        {
            _mouseStatisticsWindow = new MouseStatisticsWindow(_repository, _mouseStatisticsService, _petWindow.MouseLegendHidden, _petWindow.SetMouseLegendHidden) { Owner = _petWindow };
            _mouseStatisticsWindow.Closed += (_, _) => _mouseStatisticsWindow = null;
        }
        _mouseStatisticsWindow.TargetHeight = _petWindow.Height;
        _mouseStatisticsWindow.Top = _petWindow.Top;
        _mouseStatisticsWindow.RefreshStatistics();
        _petWindow.SetCompanionWindowVisible(true);
        if (!_mouseStatisticsWindow.IsVisible) _mouseStatisticsWindow.Show();
        else _mouseStatisticsWindow.Activate();
        _mouseStatisticsWindow.UpdateLayout();
        _mouseStatisticsWindow.Left = _petWindow.Left - _mouseStatisticsWindow.Width - 12;
    }

    private void ToggleKeyboardStatistics()
    {
        if (_keyboardStatisticsWindow is { IsVisible: true })
        {
            _keyboardStatisticsWindow.Hide();
            _petWindow?.SetCompanionWindowVisible(false);
            return;
        }
        ShowKeyboardStatistics();
    }

    private void ToggleMouseStatistics()
    {
        if (_mouseStatisticsWindow is { IsVisible: true })
        {
            _mouseStatisticsWindow.Hide();
            _petWindow?.SetCompanionWindowVisible(false);
            return;
        }
        ShowMouseStatistics();
    }

    private void MoveCompanionWindowsWithPet()
    {
        if (_petWindow is null) return;
        if (_quickAccessWindow is { IsVisible: true })
        {
            _quickAccessWindow.Left = _petWindow.Left + _quickAccessOffsetLeft;
            _quickAccessWindow.Top = _petWindow.Top + _quickAccessOffsetTop;
        }
        if (_keyboardStatisticsWindow is { IsVisible: true })
        {
            _keyboardStatisticsWindow.Left = _petWindow.Left - _keyboardStatisticsWindow.Width - 12;
            _keyboardStatisticsWindow.Top = _petWindow.Top;
        }
        if (_mouseStatisticsWindow is { IsVisible: true })
        {
            _mouseStatisticsWindow.Left = _petWindow.Left - _mouseStatisticsWindow.Width - 12;
            _mouseStatisticsWindow.Top = _petWindow.Top;
        }
    }
}
