using DesktopPet.Core;
using DesktopPet.Data;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Input;
using Forms = System.Windows.Forms;

namespace DesktopPet.UI;

public partial class SettingsWindow : Window
{
    private readonly MainWindow _pet;
    private readonly DesktopPetRepository _repository;
    private readonly LauncherDragDrop _launcherDragDrop;
    private readonly Func<HotkeyGesture?, bool> _setVisibilityHotkey;
    private bool _isLoading;

    public SettingsWindow(MainWindow pet, DesktopPetRepository repository, Func<HotkeyGesture?, bool> setVisibilityHotkey)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.EnableRoundedCorners(this);
        _pet = pet;
        _repository = repository;
        _setVisibilityHotkey = setVisibilityHotkey;
        _launcherDragDrop = new LauncherDragDrop(LauncherList, repository);
        _repository.LaunchersChanged += RepositoryLaunchersChanged;
        Closed += (_, _) => _repository.LaunchersChanged -= RepositoryLaunchersChanged;
        _isLoading = true;
        TopmostCheckBox.IsChecked = pet.IsPetTopmost;
        VisibilityHotkeyBox.Text = pet.ToggleVisibilityHotkey?.DisplayText ?? "未设置";
        _isLoading = false;
        RefreshLaunchers();
    }

    private void TopmostChanged(object sender, RoutedEventArgs e)
    {
        if (!_isLoading) _pet.SetPetTopmost(TopmostCheckBox.IsChecked == true);
    }

    private void CaptureVisibilityHotkey(object sender, KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;

        var modifiers = HotkeyModifiers.None;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= HotkeyModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= HotkeyModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= HotkeyModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= HotkeyModifiers.Windows;
        if (modifiers == HotkeyModifiers.None)
        {
            MessageBox.Show("快捷键至少需要包含 Ctrl、Alt、Shift 或 Win 中的一个。", "DesktopPet");
            return;
        }

        var gesture = new HotkeyGesture(modifiers, KeyInterop.VirtualKeyFromKey(key));
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

    private void DragSettingsWindow(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void CloseSettingsWindow(object sender, RoutedEventArgs e) => Close();

    private void BrowseApplication(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "程序文件 (*.exe)|*.exe|所有文件 (*.*)|*.*" };
        if (dialog.ShowDialog() == true) SetPath(dialog.FileName);
    }

    private void BrowseFolder(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog();
        if (dialog.ShowDialog() == Forms.DialogResult.OK) SetPath(dialog.SelectedPath);
    }

    private void SetPath(string path)
    {
        LauncherPathBox.Text = path;
        if (string.IsNullOrWhiteSpace(LauncherNameBox.Text))
            LauncherNameBox.Text = Directory.Exists(path) ? new DirectoryInfo(path).Name : Path.GetFileNameWithoutExtension(path);
    }

    private void AddLauncher(object sender, RoutedEventArgs e)
    {
        var path = LauncherPathBox.Text.Trim();
        if (!File.Exists(path) && !Directory.Exists(path)) { MessageBox.Show("请选择存在的程序或文件夹。", "DesktopPet"); return; }
        var name = LauncherNameBox.Text.Trim();
        if (name.Length == 0) name = Directory.Exists(path) ? new DirectoryInfo(path).Name : Path.GetFileNameWithoutExtension(path);
        try
        {
            _repository.AddLauncher(name, path);
            LauncherNameBox.Clear();
            LauncherPathBox.Clear();
            RefreshLaunchers();
        }
        catch (Microsoft.Data.Sqlite.SqliteException) { MessageBox.Show("这个路径已经在快捷启动列表中。", "DesktopPet"); }
    }

    private void LaunchSelected(object sender, MouseButtonEventArgs e)
    {
        if (LauncherList.SelectedItem is LauncherItem item) LauncherProcess.Start(item.TargetPath);
    }

    private void DeleteLauncher(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is LauncherItem item) { _repository.DeleteLauncher(item.Id); RefreshLaunchers(); }
    }

    private void RefreshLaunchers() => LauncherList.ItemsSource = _repository.GetLaunchers();

    private void RepositoryLaunchersChanged(object? sender, EventArgs e) => RefreshLaunchers();
    private void LauncherPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _launcherDragDrop.PreviewMouseLeftButtonDown(e);
    private void LauncherPreviewMouseMove(object sender, MouseEventArgs e) => _launcherDragDrop.PreviewMouseMove(e);
    private void LauncherDrop(object sender, DragEventArgs e) => _launcherDragDrop.Drop(e);
}
