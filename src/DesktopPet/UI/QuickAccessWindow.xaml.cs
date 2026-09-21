using DesktopPet.Core;
using DesktopPet.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace DesktopPet.UI;

public partial class QuickAccessWindow : Window
{
    private readonly DesktopPetRepository _repository;
    private readonly Action _openSettingsLauncher;
    private readonly LauncherDragDrop _launcherDragDrop;

    public QuickAccessWindow(DesktopPetRepository repository, Action openSettingsLauncher)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.EnableRoundedCorners(this);
        _repository = repository;
        _openSettingsLauncher = openSettingsLauncher;
        _launcherDragDrop = new LauncherDragDrop(LauncherList, repository);
        _repository.TodosChanged += RepositoryTodosChanged;
        _repository.LaunchersChanged += RepositoryLaunchersChanged;
        Closed += (_, _) =>
        {
            _repository.TodosChanged -= RepositoryTodosChanged;
            _repository.LaunchersChanged -= RepositoryLaunchersChanged;
        };
        Refresh();
    }

    public void Refresh()
    {
        TodoList.ItemsSource = _repository.GetTodos();
        ClipboardList.ItemsSource = _repository.GetClipboardItems();
        LauncherList.ItemsSource = _repository.GetLaunchers();
    }

    private void TodoTitleLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTodoInput();

    private void TodoTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        CommitTodoInput();
    }

    private void CommitTodoInput()
    {
        var title = TodoTitleBox.Text.Trim();
        if (title.Length == 0) return;
        _repository.AddTodo(title);
        TodoTitleBox.Clear();
    }

    private void TodoChanged(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TodoItem item)
        {
            _repository.SetTodoCompleted(item.Id, ((CheckBox)sender).IsChecked == true);
        }
    }

    private void DeleteTodo(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TodoItem item) _repository.DeleteTodo(item.Id);
    }

    private void ClipboardContentLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitClipboardInput();

    private void CommitClipboardInput()
    {
        var content = ClipboardContentBox.Text;
        if (string.IsNullOrWhiteSpace(content)) return;
        _repository.AddClipboardItem(content);
        ClipboardContentBox.Clear();
        ClipboardList.ItemsSource = _repository.GetClipboardItems();
    }

    private void WindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        if (!IsWithin(source, TodoTitleBox)) CommitTodoInput();
        if (!IsWithin(source, ClipboardContentBox)) CommitClipboardInput();
    }

    private static bool IsWithin(DependencyObject? source, DependencyObject ancestor)
    {
        for (var current = source; current is not null; current = GetParent(current))
        {
            if (ReferenceEquals(current, ancestor)) return true;
        }
        return false;
    }

    private static DependencyObject? GetParent(DependencyObject node) =>
        node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private void ReadClipboard(object sender, RoutedEventArgs e)
    {
        try
        {
            ClipboardContentBox.Text = Clipboard.ContainsText() ? Clipboard.GetText() : string.Empty;
            ClipboardContentBox.Focus();
            ClipboardContentBox.CaretIndex = ClipboardContentBox.Text.Length;
        }
        catch (ExternalException)
        {
            MessageBox.Show("暂时无法读取系统剪切板，请稍后重试。", "DesktopPet");
        }
    }

    private void CopyClipboardItem(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ClipboardItem item) return;
        try
        {
            Clipboard.SetText(item.Content);
        }
        catch (ExternalException)
        {
            MessageBox.Show("暂时无法写入系统剪切板，请稍后重试。", "DesktopPet");
        }
    }

    private void DeleteClipboardItem(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not ClipboardItem item) return;
        _repository.DeleteClipboardItem(item.Id);
        ClipboardList.ItemsSource = _repository.GetClipboardItems();
    }

    private void Launch(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not LauncherItem item) return;
        if (!File.Exists(item.TargetPath) && !Directory.Exists(item.TargetPath)) { MessageBox.Show("目标路径已不存在。", "DesktopPet"); return; }
        LauncherProcess.Start(item.TargetPath);
    }

    private void OpenSettingsLauncher(object sender, RoutedEventArgs e) => _openSettingsLauncher();

    private void RepositoryLaunchersChanged(object? sender, EventArgs e) => LauncherList.ItemsSource = _repository.GetLaunchers();
    private void RepositoryTodosChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => TodoList.ItemsSource = _repository.GetTodos());
    private void LauncherPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _launcherDragDrop.PreviewMouseLeftButtonDown(e);
    private void LauncherPreviewMouseMove(object sender, MouseEventArgs e) => _launcherDragDrop.PreviewMouseMove(e);
    private void LauncherDrop(object sender, DragEventArgs e) => _launcherDragDrop.Drop(e);
}
