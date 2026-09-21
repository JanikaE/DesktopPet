using DesktopPet.Core;
using DesktopPet.Core.Sync;
using DesktopPet.Data;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace DesktopPet.UI;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _pet;
    private readonly DesktopPetRepository _repository;
    private readonly OneDriveSyncService _oneDriveSyncService;
    private readonly LauncherDragDrop _launcherDragDrop;
    private readonly Func<HotkeyGesture?, bool> _setVisibilityHotkey;
    private readonly Func<HotkeyGesture?, bool> _setKeyboardStatisticsHotkey;
    private readonly Func<HotkeyGesture?, bool> _setMouseStatisticsHotkey;
    private bool _isLoading;
    private bool _launcherTabRequested;

    public SettingsWindow(
        MainWindow pet,
        DesktopPetRepository repository,
        OneDriveSyncService oneDriveSyncService,
        Func<HotkeyGesture?, bool> setVisibilityHotkey,
        Func<HotkeyGesture?, bool> setKeyboardStatisticsHotkey,
        Func<HotkeyGesture?, bool> setMouseStatisticsHotkey)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.EnableRoundedCorners(this);
        Loaded += (_, _) =>
        {
            if (_launcherTabRequested)
            {
                _launcherTabRequested = false;
                Activate();
            }
        };
        _pet = pet;
        _repository = repository;
        _oneDriveSyncService = oneDriveSyncService;
        _setVisibilityHotkey = setVisibilityHotkey;
        _setKeyboardStatisticsHotkey = setKeyboardStatisticsHotkey;
        _setMouseStatisticsHotkey = setMouseStatisticsHotkey;
        _launcherDragDrop = new LauncherDragDrop(LauncherList, repository);
        _repository.LaunchersChanged += RepositoryLaunchersChanged;
        _oneDriveSyncService.StateChanged += TodoSyncStateChanged;
        Closed += (_, _) =>
        {
            _repository.LaunchersChanged -= RepositoryLaunchersChanged;
            _oneDriveSyncService.StateChanged -= TodoSyncStateChanged;
        };
        _isLoading = true;
        TopmostCheckBox.IsChecked = pet.IsPetTopmost;
        VisibilityHotkeyBox.Text = pet.ToggleVisibilityHotkey?.DisplayText ?? "未设置";
        KeyboardStatisticsHotkeyBox.Text = pet.KeyboardStatisticsHotkey?.DisplayText ?? "未设置";
        MouseStatisticsHotkeyBox.Text = pet.MouseStatisticsHotkey?.DisplayText ?? "未设置";
        _isLoading = false;
        RefreshLaunchers();
        RefreshTodoSyncState(_oneDriveSyncService.CurrentState);
    }

    private void TopmostChanged(object sender, RoutedEventArgs e)
    {
        if (!_isLoading) _pet.SetPetTopmost(TopmostCheckBox.IsChecked == true);
    }

    private void CaptureVisibilityHotkey(object sender, KeyEventArgs e)
    {
        if (TryCaptureGesture(e) is not { } gesture) return;
        if (!_setVisibilityHotkey(gesture))
        {
            MessageBox.Show("这个组合键已被其他程序占用，请换一个组合键。", "DesktopPet");
            return;
        }
        VisibilityHotkeyBox.Text = gesture.DisplayText;
    }

    private void ClearVisibilityHotkey(object sender, RoutedEventArgs e)
    {
        _setVisibilityHotkey(null);
        VisibilityHotkeyBox.Text = "未设置";
    }

    private void CaptureKeyboardStatisticsHotkey(object sender, KeyEventArgs e)
    {
        if (TryCaptureGesture(e) is not { } gesture) return;
        if (!_setKeyboardStatisticsHotkey(gesture))
        {
            MessageBox.Show("这个组合键已被其他程序占用，请换一个组合键。", "DesktopPet");
            return;
        }
        KeyboardStatisticsHotkeyBox.Text = gesture.DisplayText;
    }

    private void ClearKeyboardStatisticsHotkey(object sender, RoutedEventArgs e)
    {
        _setKeyboardStatisticsHotkey(null);
        KeyboardStatisticsHotkeyBox.Text = "未设置";
    }

    private void CaptureMouseStatisticsHotkey(object sender, KeyEventArgs e)
    {
        if (TryCaptureGesture(e) is not { } gesture) return;
        if (!_setMouseStatisticsHotkey(gesture))
        {
            MessageBox.Show("这个组合键已被其他程序占用，请换一个组合键。", "DesktopPet");
            return;
        }
        MouseStatisticsHotkeyBox.Text = gesture.DisplayText;
    }

    private void ClearMouseStatisticsHotkey(object sender, RoutedEventArgs e)
    {
        _setMouseStatisticsHotkey(null);
        MouseStatisticsHotkeyBox.Text = "未设置";
    }

    private static HotkeyGesture? TryCaptureGesture(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return null;

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Windows;
        if (modifiers == HotkeyModifiers.None)
        {
            MessageBox.Show("快捷键至少需要包含 Ctrl、Alt、Shift 或 Win 中的一个。", "DesktopPet");
            return null;
        }

        return new HotkeyGesture(modifiers, KeyInterop.VirtualKeyFromKey(key));
    }

    private void DragSettingsWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CloseSettingsWindow(object sender, RoutedEventArgs e) => Close();

    private void BrowseFile(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog() == true) AddLauncher(dialog.FileName);
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == Forms.DialogResult.OK) AddLauncher(dialog.SelectedPath);
    }

    private void AddLauncher(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path)) { MessageBox.Show("请选择存在的文件或文件夹。", "DesktopPet"); return; }
        var name = Directory.Exists(path) ? new DirectoryInfo(path).Name : Path.GetFileNameWithoutExtension(path);
        try
        {
            _repository.AddLauncher(name, path);
            RefreshLaunchers();
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { MessageBox.Show("这个路径已经在快捷启动列表中。", "DesktopPet"); }
    }

    private TextBox? _editingBox;

    private void CardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2 || ((FrameworkElement)sender).DataContext is not LauncherItem item) return;
        item.IsEditing = true;
        if (((FrameworkElement)sender).IsLoaded) Dispatcher.BeginInvoke(() =>
        {
            if (FindDescendantTextBox((System.Windows.DependencyObject)sender) is { } box)
            {
                _editingBox = box;
                box.Focus();
                box.SelectAll();
            }
        });
    }

    private void LauncherNameKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        CommitLauncherName(sender as TextBox);
    }

    private void LauncherNameLostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) CommitLauncherName(box);
    }

    private void WindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_editingBox is null) return;
        var source = e.OriginalSource as DependencyObject;
        if (IsWithin(source, _editingBox)) return;
        CommitLauncherName(_editingBox);
    }

    private void CommitLauncherName(TextBox? box)
    {
        if (box is null || box.DataContext is not LauncherItem item) return;
        var trimmed = box.Text.Trim();
        if (trimmed.Length == 0) trimmed = Directory.Exists(item.TargetPath) ? new DirectoryInfo(item.TargetPath).Name : Path.GetFileNameWithoutExtension(item.TargetPath);
        if (!string.Equals(trimmed, item.Name, StringComparison.Ordinal))
        {
            box.Text = trimmed;
            _repository.RenameLauncher(item.Id, trimmed);
        }
        item.IsEditing = false;
        if (ReferenceEquals(_editingBox, box)) _editingBox = null;
    }

    private static TextBox? FindDescendantTextBox(System.Windows.DependencyObject root)
    {
        var stack = new Stack<System.Windows.DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            if (current is TextBox textBox) return textBox;
            var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(current);
            for (var index = 0; index < count; index++) stack.Push(System.Windows.Media.VisualTreeHelper.GetChild(current, index));
        }
        return null;
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = current is Visual
            ? System.Windows.Media.VisualTreeHelper.GetParent(current)
            : LogicalTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private void DeleteLauncher(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuLauncherItem(sender) is not LauncherItem item) return;
        _repository.DeleteLauncher(item.Id);
        RefreshLaunchers();
    }

    private static LauncherItem? GetContextMenuLauncherItem(object sender)
    {
        if (sender is not MenuItem menuItem) return null;
        var contextMenu = LogicalTreeHelper.GetParent(menuItem) as ContextMenu;
        return contextMenu?.PlacementTarget is FrameworkElement { DataContext: LauncherItem item } ? item : null;
    }

    public void ActivateLauncherTab()
    {
        LauncherTab.IsSelected = true;
        if (IsVisible) Activate();
        else _launcherTabRequested = true;
    }

    private void RefreshLaunchers() => LauncherList.ItemsSource = _repository.GetLaunchers();

    private void RepositoryLaunchersChanged(object? sender, EventArgs e) => RefreshLaunchers();
    private void TodoSyncStateChanged(object? sender, TodoSyncState state) => Dispatcher.BeginInvoke(() => RefreshTodoSyncState(state));

    private void RefreshTodoSyncState(TodoSyncState state)
    {
        var configuration = _repository.GetTodoSyncConfiguration();
        TodoSyncDirectoryBox.Text = state.Directory ?? configuration.Directory ?? "尚未选择";
        TodoSyncStatusText.Text = state.Message;
        TodoSyncLastTimeText.Text = state.LastSuccessfulMergeUtc is null
            ? "尚无成功合并记录"
            : $"上次本地合并：{state.LastSuccessfulMergeUtc.Value.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        EnableTodoSyncButton.IsEnabled = !configuration.Enabled;
        SyncTodosNowButton.IsEnabled = configuration.Enabled;
        DisableTodoSyncButton.IsEnabled = configuration.Enabled;
        OpenTodoSyncDirectoryButton.IsEnabled = !string.IsNullOrWhiteSpace(configuration.Directory) && Directory.Exists(configuration.Directory);
    }

    private async void EnableTodoSync(object sender, RoutedEventArgs e)
    {
        if (!await _oneDriveSyncService.EnableDefaultAsync())
            MessageBox.Show("没有找到可用的主要 OneDrive 目录，请点击“选择其他目录”并选择 TodoSync 的上一级同步根目录。", "DesktopPet");
    }

    private async void ChooseTodoSyncDirectory(object sender, RoutedEventArgs e)
    {
        var configuration = _repository.GetTodoSyncConfiguration();
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "选择一个由 OneDrive 同步的根目录；程序会在其中创建 TodoSync 和 NoteSync。",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            SelectedPath = configuration.Directory ?? string.Empty
        };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        if (!await _oneDriveSyncService.ConfigureDirectoryAsync(dialog.SelectedPath))
            MessageBox.Show("无法使用所选目录，请确认目录存在且当前用户具有写入权限。", "DesktopPet");
    }

    private async void SyncTodosNow(object sender, RoutedEventArgs e) => await _oneDriveSyncService.SynchronizeAsync();

    private void OpenTodoSyncDirectory(object sender, RoutedEventArgs e)
    {
        var directory = _repository.GetTodoSyncConfiguration().Directory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;
        Process.Start(new ProcessStartInfo { FileName = directory, UseShellExecute = true });
    }

    private void DisableTodoSync(object sender, RoutedEventArgs e) => _oneDriveSyncService.Disable();

    private void LauncherPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _launcherDragDrop.PreviewMouseLeftButtonDown(e);
    private void LauncherPreviewMouseMove(object sender, MouseEventArgs e) => _launcherDragDrop.PreviewMouseMove(e);
    private void LauncherDrop(object sender, DragEventArgs e) => _launcherDragDrop.Drop(e);
}
