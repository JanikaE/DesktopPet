using DesktopPet.Core;
using DesktopPet.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DesktopPet.UI;

public partial class QuickAccessWindow : Window
{
    private readonly DesktopPetRepository _repository;
    private readonly LauncherDragDrop _launcherDragDrop;

    public QuickAccessWindow(DesktopPetRepository repository)
    {
        InitializeComponent();
        _repository = repository;
        _launcherDragDrop = new LauncherDragDrop(LauncherList, repository);
        _repository.LaunchersChanged += RepositoryLaunchersChanged;
        Closed += (_, _) => _repository.LaunchersChanged -= RepositoryLaunchersChanged;
        Refresh();
    }

    public void Refresh()
    {
        TodoList.ItemsSource = _repository.GetTodos();
        LauncherList.ItemsSource = _repository.GetLaunchers();
    }

    private void TodoTitleLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        var title = TodoTitleBox.Text.Trim();
        if (title.Length == 0) return;
        _repository.AddTodo(title);
        TodoTitleBox.Clear();
        Refresh();
    }

    private void TodoChanged(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TodoItem item)
        {
            _repository.SetTodoCompleted(item.Id, ((CheckBox)sender).IsChecked == true);
            Refresh();
        }
    }

    private void DeleteTodo(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is TodoItem item) { _repository.DeleteTodo(item.Id); Refresh(); }
    }

    private void Launch(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not LauncherItem item) return;
        if (!File.Exists(item.TargetPath) && !Directory.Exists(item.TargetPath)) { MessageBox.Show("目标路径已不存在。", "DesktopPet"); return; }
        LauncherProcess.Start(item.TargetPath);
    }

    private void RepositoryLaunchersChanged(object? sender, EventArgs e) => LauncherList.ItemsSource = _repository.GetLaunchers();
    private void LauncherPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _launcherDragDrop.PreviewMouseLeftButtonDown(e);
    private void LauncherPreviewMouseMove(object sender, MouseEventArgs e) => _launcherDragDrop.PreviewMouseMove(e);
    private void LauncherDrop(object sender, DragEventArgs e) => _launcherDragDrop.Drop(e);
}
