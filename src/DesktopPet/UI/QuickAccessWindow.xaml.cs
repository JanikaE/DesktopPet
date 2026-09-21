using DesktopPet.Core;
using DesktopPet.Core.Sync;
using DesktopPet.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace DesktopPet.UI;

public partial class QuickAccessWindow : Window
{
    private readonly DesktopPetRepository _repository;
    private readonly Action _openSettingsLauncher;
    private readonly LauncherDragDrop _launcherDragDrop;
    private readonly DispatcherTimer _noteSaveTimer;
    private NoteItem? _editingNote;
    private bool _loadingNote;

    public QuickAccessWindow(DesktopPetRepository repository, Action openSettingsLauncher)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.EnableRoundedCorners(this);
        _repository = repository;
        _openSettingsLauncher = openSettingsLauncher;
        _launcherDragDrop = new LauncherDragDrop(LauncherList, repository);
        _noteSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _noteSaveTimer.Tick += (_, _) => CommitNote();
        _repository.TodosChanged += RepositoryTodosChanged;
        _repository.NotesChanged += RepositoryNotesChanged;
        _repository.LaunchersChanged += RepositoryLaunchersChanged;
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible) CommitNote();
        };
        Closed += (_, _) =>
        {
            CommitNote();
            _noteSaveTimer.Stop();
            _repository.TodosChanged -= RepositoryTodosChanged;
            _repository.NotesChanged -= RepositoryNotesChanged;
            _repository.LaunchersChanged -= RepositoryLaunchersChanged;
        };
        Refresh();
    }

    public void Refresh()
    {
        TodoList.ItemsSource = _repository.GetTodos();
        ClipboardList.ItemsSource = _repository.GetClipboardItems();
        NoteList.ItemsSource = _repository.GetNotes();
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

    private void NewNote(object sender, RoutedEventArgs e)
    {
        _editingNote = null;
        ShowNoteEditor(string.Empty);
    }

    private void EditNote(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not NoteItem item) return;
        _editingNote = item;
        ShowNoteEditor(item.Content);
    }

    private void ShowNoteEditor(string content)
    {
        _noteSaveTimer.Stop();
        _loadingNote = true;
        NoteContentBox.Text = content;
        NoteContentBox.CaretIndex = content.Length;
        NoteCharacterCountText.Text = $"{content.Length} / {NoteMergeEngine.MaximumContentLength}";
        NoteSaveStatusText.Text = _editingNote is null ? "输入内容后自动保存" : "已保存";
        NoteListView.Visibility = Visibility.Collapsed;
        NoteEditorView.Visibility = Visibility.Visible;
        _loadingNote = false;
        NoteContentBox.Focus();
    }

    private void NoteContentChanged(object sender, TextChangedEventArgs e)
    {
        NoteCharacterCountText.Text = $"{NoteContentBox.Text.Length} / {NoteMergeEngine.MaximumContentLength}";
        if (_loadingNote) return;
        NoteSaveStatusText.Text = "等待保存…";
        _noteSaveTimer.Stop();
        _noteSaveTimer.Start();
    }

    private void CommitNote()
    {
        _noteSaveTimer.Stop();
        if (NoteEditorView.Visibility != Visibility.Visible) return;
        var content = NoteContentBox.Text;
        if (_editingNote is null)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                NoteSaveStatusText.Text = "输入内容后自动保存";
                return;
            }
            _editingNote = _repository.AddNote(content);
        }
        else if (!string.Equals(content, _editingNote.Content, StringComparison.Ordinal))
        {
            _editingNote = _repository.UpdateNote(_editingNote, content);
        }
        NoteSaveStatusText.Text = $"已保存 {DateTime.Now:HH:mm:ss}";
    }

    private void BackFromNote(object sender, RoutedEventArgs e)
    {
        CommitNote();
        CloseNoteEditor();
    }

    private void DeleteNote(object sender, RoutedEventArgs e)
    {
        _noteSaveTimer.Stop();
        if (_editingNote is null)
        {
            CloseNoteEditor();
            return;
        }

        if (!string.IsNullOrWhiteSpace(NoteContentBox.Text) &&
            MessageBox.Show("确定删除这条便签吗？删除会同步到其他设备。", "DesktopPet",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _repository.DeleteNote(_editingNote);
        CloseNoteEditor();
    }

    private void CloseNoteEditor()
    {
        _noteSaveTimer.Stop();
        _editingNote = null;
        _loadingNote = true;
        NoteContentBox.Clear();
        _loadingNote = false;
        NoteEditorView.Visibility = Visibility.Collapsed;
        NoteListView.Visibility = Visibility.Visible;
        NoteList.ItemsSource = _repository.GetNotes();
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
    private void RepositoryNotesChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => NoteList.ItemsSource = _repository.GetNotes());
    private void LauncherPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _launcherDragDrop.PreviewMouseLeftButtonDown(e);
    private void LauncherPreviewMouseMove(object sender, MouseEventArgs e) => _launcherDragDrop.PreviewMouseMove(e);
    private void LauncherDrop(object sender, DragEventArgs e) => _launcherDragDrop.Drop(e);
}
