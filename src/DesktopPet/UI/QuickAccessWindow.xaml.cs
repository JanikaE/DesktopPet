using DesktopPet.Core;
using DesktopPet.Core.Sync;
using DesktopPet.Data;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace DesktopPet.UI;

public partial class QuickAccessWindow : Window
{
    private readonly DesktopPetRepository _repository;
    private readonly Action _openSettingsLauncher;
    private readonly LauncherDragDrop _launcherDragDrop;
    private readonly TodoDragDrop _todoDragDrop;
    private readonly DispatcherTimer _noteSaveTimer;
    private readonly DispatcherTimer _clipboardToastTimer;
    private readonly HashSet<Guid> _collapsedTodoIds = [];
    private NoteItem? _editingNote;
    private Guid? _todoInputParentId;
    private bool _loadingNote;

    public QuickAccessWindow(DesktopPetRepository repository, Action openSettingsLauncher)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => WindowAppearance.EnableRoundedCorners(this);
        _repository = repository;
        _openSettingsLauncher = openSettingsLauncher;
        _launcherDragDrop = new LauncherDragDrop(LauncherList, repository);
        _todoDragDrop = new TodoDragDrop(TodoList, repository, TodoDragFinished);
        _noteSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _noteSaveTimer.Tick += (_, _) => CommitNote();
        _clipboardToastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        _clipboardToastTimer.Tick += (_, _) =>
        {
            _clipboardToastTimer.Stop();
            ClipboardToast.Visibility = Visibility.Collapsed;
        };
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
            _clipboardToastTimer.Stop();
            _repository.TodosChanged -= RepositoryTodosChanged;
            _repository.NotesChanged -= RepositoryNotesChanged;
            _repository.LaunchersChanged -= RepositoryLaunchersChanged;
        };
        Refresh();
    }

    public void Refresh()
    {
        RefreshTodos();
        ClipboardList.ItemsSource = _repository.GetClipboardItems();
        NoteList.ItemsSource = _repository.GetNotes();
        LauncherList.ItemsSource = _repository.GetLaunchers();
    }

    private void TodoDragFinished(Guid? parentId)
    {
        if (parentId is { } id) _collapsedTodoIds.Remove(id);
        if (!_todoDragDrop.IsDragging) RefreshTodos();
    }

    private void RefreshTodos()
    {
        if (_todoDragDrop.IsDragging) return;
        var scroll = TodoDragDrop.ScrollViewerOf(TodoList);
        var offset = scroll?.VerticalOffset ?? 0;
        var todos = _repository.GetTodos();
        var itemsById = todos.ToDictionary(item => item.Id);
        var childrenByParent = new Dictionary<Guid, List<TodoItem>>();
        var displayedAsChildren = new HashSet<Guid>();

        foreach (var item in todos)
        {
            if (item.ParentId is not { } parentId ||
                !itemsById.TryGetValue(parentId, out var parent) ||
                parent.ParentId is not null)
                continue;

            if (!childrenByParent.TryGetValue(parentId, out var children))
            {
                children = [];
                childrenByParent[parentId] = children;
            }
            children.Add(item);
            displayedAsChildren.Add(item.Id);
        }

        _collapsedTodoIds.IntersectWith(childrenByParent.Keys);
        var displayItems = new List<TodoDisplayItem>();
        foreach (var root in todos.Where(item => !displayedAsChildren.Contains(item.Id)))
        {
            childrenByParent.TryGetValue(root.Id, out var children);
            var hasChildren = children is { Count: > 0 };
            var isExpanded = hasChildren && !_collapsedTodoIds.Contains(root.Id);
            displayItems.Add(new TodoDisplayItem(
                root,
                false,
                hasChildren,
                isExpanded,
                children?.Count(child => child.IsCompleted) ?? 0,
                children?.Count ?? 0));
            if (!isExpanded) continue;
            displayItems.AddRange(children!.Select(child => new TodoDisplayItem(child, true, false, false, 0, 0)));
        }

        if (_todoInputParentId is { } inputParentId &&
            (!itemsById.TryGetValue(inputParentId, out var inputParent) || inputParent.ParentId is not null))
            ResetTodoInputMode(clearText: false);

        TodoList.ItemsSource = displayItems;
        TodoList.UpdateLayout();
        scroll?.ScrollToVerticalOffset(offset);
    }

    private void TodoTitleLostFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is DependencyObject newFocus && IsWithin(newFocus, TodoInputModePanel)) return;
        CommitTodoInput();
    }

    private void TodoTitleKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && _todoInputParentId is not null)
        {
            e.Handled = true;
            ResetTodoInputMode(clearText: true);
            return;
        }

        if (e.Key is not (Key.Enter or Key.Return)) return;
        e.Handled = true;
        CommitTodoInput();
    }

    private void CommitTodoInput()
    {
        var title = TodoTitleBox.Text.Trim();
        if (title.Length == 0) return;
        try
        {
            _repository.AddTodo(title, _todoInputParentId);
            ResetTodoInputMode(clearText: true);
        }
        catch (InvalidOperationException exception)
        {
            ResetTodoInputMode(clearText: false);
            MessageBox.Show(exception.Message, "DesktopPet");
        }
    }

    private void AddSubTodo(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuItem<TodoDisplayItem>(sender) is not { CanAddChild: true } displayItem) return;

        CommitTodoInput();
        _todoInputParentId = displayItem.Item.Id;
        _collapsedTodoIds.Remove(displayItem.Item.Id);
        TodoInputModeText.Text = $"添加到：{displayItem.Item.Title}";
        TodoInputModePanel.Visibility = Visibility.Visible;
        TodoTitleBox.ToolTip = "输入子待办，按回车或点击输入框外自动添加";
        RefreshTodos();
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => TodoTitleBox.Focus());
    }

    private void CancelSubTodoInput(object sender, RoutedEventArgs e) => ResetTodoInputMode(clearText: true);

    private void ResetTodoInputMode(bool clearText)
    {
        _todoInputParentId = null;
        TodoInputModeText.Text = string.Empty;
        TodoInputModePanel.Visibility = Visibility.Collapsed;
        TodoTitleBox.ToolTip = "输入待办，按回车或点击输入框外自动添加";
        if (clearText) TodoTitleBox.Clear();
    }

    private void ToggleTodoExpansion(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is not TodoDisplayItem { HasChildren: true } displayItem) return;
        if (!_collapsedTodoIds.Add(displayItem.Item.Id)) _collapsedTodoIds.Remove(displayItem.Item.Id);
        RefreshTodos();
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
        if (GetContextMenuItem<TodoDisplayItem>(sender) is not { } displayItem) return;
        var item = displayItem.Item;
        var childCount = _repository.GetTodos().Count(todo => todo.ParentId == item.Id);
        if (childCount > 0 && MessageBox.Show(
                $"删除此待办会同时删除 {childCount} 个子待办，确定继续吗？",
                "DesktopPet",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        if (_todoInputParentId == item.Id) ResetTodoInputMode(clearText: true);
        _repository.DeleteTodo(item.Id);
    }

    private void EditTodo(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            LogicalTreeHelper.GetParent(menuItem) is not ContextMenu
            {
                PlacementTarget: FrameworkElement { DataContext: TodoDisplayItem displayItem } target
            })
            return;

        var item = displayItem.Item;

        var display = FindVisualChild<Grid>(target, "TodoDisplay");
        var editor = FindVisualChild<TextBox>(target, "TodoEditor");
        if (display is null || editor is null) return;

        editor.Text = item.Title;
        display.Visibility = Visibility.Collapsed;
        editor.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            editor.Focus();
            editor.SelectAll();
        });
    }

    private void TodoEditorLostFocus(object sender, KeyboardFocusChangedEventArgs e) => CommitTodoEdit((TextBox)sender);

    private void TodoEditorKeyDown(object sender, KeyEventArgs e)
    {
        var editor = (TextBox)sender;
        if (e.Key is Key.Enter or Key.Return)
        {
            e.Handled = true;
            CommitTodoEdit(editor);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CancelTodoEdit(editor);
        }
    }

    private void CommitTodoEdit(TextBox editor)
    {
        if (editor.Visibility != Visibility.Visible || editor.Tag is not TodoItem item) return;
        var title = editor.Text.Trim();
        if (title.Length > 0) _repository.SetTodoTitle(item.Id, title);
        CancelTodoEdit(editor);
    }

    private static void CancelTodoEdit(TextBox editor)
    {
        editor.Visibility = Visibility.Collapsed;
        if (FindVisualChild<Grid>(editor.Parent, "TodoDisplay") is { } display)
            display.Visibility = Visibility.Visible;
    }

    private static T? FindVisualChild<T>(DependencyObject? root, string name) where T : FrameworkElement
    {
        if (root is null) return null;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T element && string.Equals(element.Name, name, StringComparison.Ordinal)) return element;
            if (FindVisualChild<T>(child, name) is { } descendant) return descendant;
        }
        return null;
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
        if (!IsWithin(source, TodoInputArea) && !IsWithin(source, TodoInputModePanel)) CommitTodoInput();
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

    private void AddClipboardItem(object sender, RoutedEventArgs e) => CommitClipboardInput();

    private void ClipboardItemClicked(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(ClipboardList, source) is not ListBoxItem
            {
                DataContext: ClipboardItem item
            })
            return;

        try
        {
            // The clipboard is a shared Windows resource and can be locked briefly by
            // another process. Let WinForms retry instead of failing on the first lock.
            Forms.Clipboard.SetDataObject(item.Content, true, 10, 100);
            ShowClipboardToast();
        }
        catch (ExternalException)
        {
            MessageBox.Show("暂时无法写入系统剪切板，请稍后重试。", "DesktopPet");
        }
    }

    private void DeleteClipboardItem(object sender, RoutedEventArgs e)
    {
        if (GetContextMenuItem<ClipboardItem>(sender) is not { } item) return;
        _repository.DeleteClipboardItem(item.Id);
        ClipboardList.ItemsSource = _repository.GetClipboardItems();
    }

    private void ShowClipboardToast()
    {
        ClipboardToast.Visibility = Visibility.Visible;
        _clipboardToastTimer.Stop();
        _clipboardToastTimer.Start();
    }

    private static T? GetContextMenuItem<T>(object sender) where T : class
    {
        if (sender is not MenuItem menuItem ||
            LogicalTreeHelper.GetParent(menuItem) is not ContextMenu
            {
                PlacementTarget: FrameworkElement { DataContext: T item }
            })
            return null;

        return item;
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

    private void OpenLauncherDirectory(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem ||
            LogicalTreeHelper.GetParent(menuItem) is not ContextMenu
            {
                PlacementTarget: FrameworkElement { DataContext: LauncherItem item }
            })
            return;

        try
        {
            LauncherProcess.OpenContainingDirectory(item.TargetPath);
        }
        catch (DirectoryNotFoundException)
        {
            MessageBox.Show("所在目录已不存在。", "DesktopPet");
        }
    }

    private void OpenSettingsLauncher(object sender, RoutedEventArgs e) => _openSettingsLauncher();

    private void RepositoryLaunchersChanged(object? sender, EventArgs e) => LauncherList.ItemsSource = _repository.GetLaunchers();
    private void RepositoryTodosChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(RefreshTodos);
    private void RepositoryNotesChanged(object? sender, EventArgs e) => Dispatcher.BeginInvoke(() => NoteList.ItemsSource = _repository.GetNotes());
    private void LauncherPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => _launcherDragDrop.PreviewMouseLeftButtonDown(e);
    private void LauncherPreviewMouseMove(object sender, MouseEventArgs e) => _launcherDragDrop.PreviewMouseMove(e);
    private void LauncherDrop(object sender, DragEventArgs e) => _launcherDragDrop.Drop(e);
}

public sealed record TodoDisplayItem(
    TodoItem Item,
    bool IsChild,
    bool HasChildren,
    bool IsExpanded,
    int CompletedChildCount,
    int ChildCount)
{
    public bool CanAddChild => !IsChild && Item.ParentId is null;
    public Thickness ContentMargin => new(IsChild ? 14 : 0, 0, 0, 0);
    public string ExpandGlyph => HasChildren ? IsExpanded ? "▾" : "▸" : string.Empty;
    public string ProgressText => HasChildren ? $"{CompletedChildCount} / {ChildCount}" : string.Empty;
}
