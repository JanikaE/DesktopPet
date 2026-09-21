using DesktopPet.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.Core.Sync;

public sealed class OneDriveSyncService : IDisposable
{
    private const long MaximumTodoReplicaFileSize = 16 * 1024 * 1024;
    private const long MaximumNoteReplicaFileSize = 32 * 1024 * 1024;
    private const int MaximumOperationCount = 100_000;
    private const int MaximumNoteCount = 100_000;
    private static readonly TimeSpan WatcherDebounce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LocalChangeDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PeriodicScanInterval = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly DesktopPetRepository _repository;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _timerGate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private Timer? _debounceTimer;
    private Timer? _periodicTimer;
    private int _syncPending;
    private bool _started;
    private bool _disposed;

    public OneDriveSyncService(DesktopPetRepository repository)
    {
        _repository = repository;
        CurrentState = new TodoSyncState(TodoSyncStateKind.Disabled, "OneDrive 同步未启用。", null);
    }

    public event EventHandler<TodoSyncState>? StateChanged;
    public TodoSyncState CurrentState { get; private set; }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        _repository.TodosChanged += RepositoryDataChanged;
        _repository.NotesChanged += RepositoryDataChanged;
        var configuration = _repository.GetTodoSyncConfiguration();
        if (!configuration.Enabled || string.IsNullOrWhiteSpace(configuration.Directory))
        {
            var legacy = _repository.GetLegacySyncDirectory();
            SetState(new TodoSyncState(
                legacy is null ? TodoSyncStateKind.Disabled : TodoSyncStateKind.DirectoryUnavailable,
                legacy is null
                    ? "OneDrive 同步未启用。"
                    : "检测到旧版自定义待办同步目录，请重新选择 TodoSync 上一级作为同步根目录。",
                configuration.Directory));
            return;
        }

        try
        {
            StartMonitoring(configuration.Directory);
            _ = SynchronizeAsync();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetState(new TodoSyncState(
                TodoSyncStateKind.DirectoryUnavailable,
                $"OneDrive 同步根目录当前不可用，本地数据仍可正常使用：{exception.Message}",
                configuration.Directory));
        }
    }

    public async Task<bool> EnableDefaultAsync()
    {
        var directory = OneDriveFolderLocator.TryGetDefaultSyncDirectory();
        if (directory is null)
        {
            SetState(new TodoSyncState(TodoSyncStateKind.DirectoryUnavailable, "未找到主要 OneDrive 目录，请手动选择同步根目录。", null));
            return false;
        }
        return await ConfigureDirectoryAsync(directory);
    }

    public async Task<bool> ConfigureDirectoryAsync(string directory)
    {
        if (_disposed || string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var root = Path.GetFullPath(directory);
            EnsureReplicaDirectories(root);
            _repository.SetTodoSyncConfiguration(true, root);
            StartMonitoring(root);
            await SynchronizeAsync();
            return CurrentState.Kind is TodoSyncStateKind.Ready or TodoSyncStateKind.Warning;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SetState(new TodoSyncState(TodoSyncStateKind.DirectoryUnavailable, $"无法使用所选目录：{exception.Message}", directory));
            return false;
        }
    }

    public void Disable()
    {
        var configuration = _repository.GetTodoSyncConfiguration();
        _repository.SetTodoSyncConfiguration(false, configuration.Directory);
        StopMonitoring();
        SetState(new TodoSyncState(TodoSyncStateKind.Disabled, "OneDrive 同步已停用，本地待办和便签仍会保留。", configuration.Directory));
    }

    public async Task SynchronizeAsync()
    {
        if (_disposed) return;
        if (!await _syncGate.WaitAsync(0))
        {
            Interlocked.Exchange(ref _syncPending, 1);
            return;
        }

        try
        {
            do
            {
                Interlocked.Exchange(ref _syncPending, 0);
                await SynchronizeCoreAsync();
            }
            while (Interlocked.Exchange(ref _syncPending, 0) == 1 && !_disposed);
        }
        catch (Exception exception)
        {
            var configuration = _repository.GetTodoSyncConfiguration();
            SetState(new TodoSyncState(TodoSyncStateKind.Error, $"同步失败：{exception.Message}", configuration.Directory, CurrentState.LastSuccessfulMergeUtc));
        }
        finally
        {
            _syncGate.Release();
        }
    }

    private async Task SynchronizeCoreAsync()
    {
        var configuration = _repository.GetTodoSyncConfiguration();
        if (!configuration.Enabled || string.IsNullOrWhiteSpace(configuration.Directory))
        {
            SetState(new TodoSyncState(TodoSyncStateKind.Disabled, "OneDrive 同步未启用。", configuration.Directory));
            return;
        }

        var root = configuration.Directory;
        if (!Directory.Exists(root))
        {
            SetState(new TodoSyncState(TodoSyncStateKind.DirectoryUnavailable, "OneDrive 同步根目录当前不可用，本地数据仍可正常使用。", root));
            return;
        }

        SetState(new TodoSyncState(TodoSyncStateKind.Syncing, "正在合并 OneDrive 文件夹中的待办和便签…", root, CurrentState.LastSuccessfulMergeUtc));
        try
        {
            EnsureReplicaDirectories(root);
            EnsureWatchers(root);
            var invalidTodoFiles = await SynchronizeTodosAsync(Path.Combine(root, "TodoSync", "replicas"), configuration.DeviceId);
            var invalidNoteFiles = await SynchronizeNotesAsync(Path.Combine(root, "NoteSync", "replicas"), configuration.DeviceId);
            var completedAt = DateTimeOffset.UtcNow;
            var invalidCount = invalidTodoFiles + invalidNoteFiles;
            SetState(invalidCount == 0
                ? new TodoSyncState(TodoSyncStateKind.Ready, "待办和便签已写入 OneDrive 文件夹。", root, completedAt)
                : new TodoSyncState(TodoSyncStateKind.Warning,
                    $"同步已完成，但忽略了 {invalidTodoFiles} 个待办副本和 {invalidNoteFiles} 个便签副本。", root, completedAt));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            SetState(new TodoSyncState(TodoSyncStateKind.Error, $"同步失败：{exception.Message}", root, CurrentState.LastSuccessfulMergeUtc));
        }
    }

    private async Task<int> SynchronizeTodosAsync(string replicasPath, Guid deviceId)
    {
        var incoming = new List<TodoSyncOperation>();
        var invalid = 0;
        foreach (var file in Directory.EnumerateFiles(replicasPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            var replica = await TryReadTodoReplicaAsync(file);
            if (replica is null) { invalid++; continue; }
            incoming.AddRange(replica.Operations);
        }
        _repository.MergeTodoSyncOperations(incoming);
        await WriteReplicaIfChangedAsync(
            Path.Combine(replicasPath, $"{deviceId:D}.json"),
            new TodoReplicaFile { ReplicaId = deviceId, Operations = _repository.GetTodoSyncOperations() },
            MaximumTodoReplicaFileSize,
            "待办");
        return invalid;
    }

    private async Task<int> SynchronizeNotesAsync(string replicasPath, Guid deviceId)
    {
        var incoming = new List<NoteSyncRecord>();
        var invalid = 0;
        foreach (var file in Directory.EnumerateFiles(replicasPath, "*.json", SearchOption.TopDirectoryOnly))
        {
            var replica = await TryReadNoteReplicaAsync(file);
            if (replica is null) { invalid++; continue; }
            incoming.AddRange(replica.Notes);
        }
        _repository.MergeNoteSyncRecords(incoming);
        await WriteReplicaIfChangedAsync(
            Path.Combine(replicasPath, $"{deviceId:D}.json"),
            new NoteReplicaFile { ReplicaId = deviceId, Notes = _repository.GetNoteSyncRecords() },
            MaximumNoteReplicaFileSize,
            "便签");
        return invalid;
    }

    private static async Task<TodoReplicaFile?> TryReadTodoReplicaAsync(string path)
    {
        var replica = await TryReadReplicaAsync<TodoReplicaFile>(path, MaximumTodoReplicaFileSize);
        return replica is null || replica.Format != TodoReplicaFile.ExpectedFormat ||
               replica.SchemaVersion != TodoReplicaFile.CurrentSchemaVersion || replica.ReplicaId == Guid.Empty ||
               replica.Operations is null || replica.Operations.Count > MaximumOperationCount ||
               replica.Operations.Any(operation => operation is null || !TodoMergeEngine.IsValid(operation))
            ? null : replica;
    }

    private static async Task<NoteReplicaFile?> TryReadNoteReplicaAsync(string path)
    {
        var replica = await TryReadReplicaAsync<NoteReplicaFile>(path, MaximumNoteReplicaFileSize);
        return replica is null || replica.Format != NoteReplicaFile.ExpectedFormat ||
               replica.SchemaVersion != NoteReplicaFile.CurrentSchemaVersion || replica.ReplicaId == Guid.Empty ||
               replica.Notes is null || replica.Notes.Count > MaximumNoteCount ||
               replica.Notes.Any(note => note is null || !NoteMergeEngine.IsValid(note))
            ? null : replica;
    }

    private static async Task<T?> TryReadReplicaAsync<T>(string path, long maximumSize) where T : class
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > maximumSize) return null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 32 * 1024, true);
                    return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions);
                }
                catch (IOException) when (attempt < 4) { await Task.Delay(200); }
                catch (JsonException) when (attempt < 4) { await Task.Delay(200); }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { }
        return null;
    }

    private static async Task WriteReplicaIfChangedAsync<T>(string targetPath, T replica, long maximumSize, string dataName)
    {
        var contents = JsonSerializer.SerializeToUtf8Bytes(replica, JsonOptions);
        if (contents.Length > maximumSize) throw new IOException($"{dataName}同步副本超过 {maximumSize / 1024 / 1024} MB 限制。");
        if (File.Exists(targetPath))
        {
            try
            {
                var existing = await File.ReadAllBytesAsync(targetPath);
                if (existing.AsSpan().SequenceEqual(contents)) return;
            }
            catch (IOException) { }
        }

        var directory = Path.GetDirectoryName(targetPath)!;
        var temporaryPath = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(targetPath)}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents);
                await stream.FlushAsync();
            }
            File.Move(temporaryPath, targetPath, true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private static void EnsureReplicaDirectories(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "TodoSync", "replicas"));
        Directory.CreateDirectory(Path.Combine(root, "NoteSync", "replicas"));
    }

    private void StartMonitoring(string root)
    {
        StopMonitoring();
        EnsureReplicaDirectories(root);
        EnsureWatchers(root);
        lock (_timerGate)
        {
            _debounceTimer = new Timer(_ => _ = SynchronizeAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _periodicTimer = new Timer(_ => _ = SynchronizeAsync(), null, PeriodicScanInterval, PeriodicScanInterval);
        }
    }

    private void EnsureWatchers(string root)
    {
        var paths = new[] { Path.Combine(root, "TodoSync", "replicas"), Path.Combine(root, "NoteSync", "replicas") };
        if (_watchers.Count == paths.Length && _watchers.Select(watcher => watcher.Path).SequenceEqual(paths, StringComparer.OrdinalIgnoreCase)) return;
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        foreach (var path in paths)
        {
            var watcher = new FileSystemWatcher(path, "*.json")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            watcher.Changed += ReplicaChanged;
            watcher.Created += ReplicaChanged;
            watcher.Deleted += ReplicaChanged;
            watcher.Renamed += ReplicaRenamed;
            watcher.Error += WatcherError;
            _watchers.Add(watcher);
        }
    }

    private void StopMonitoring()
    {
        foreach (var watcher in _watchers) watcher.Dispose();
        _watchers.Clear();
        lock (_timerGate)
        {
            _debounceTimer?.Dispose();
            _periodicTimer?.Dispose();
            _debounceTimer = null;
            _periodicTimer = null;
        }
    }

    private void RepositoryDataChanged(object? sender, EventArgs e) => ScheduleSync(LocalChangeDebounce);
    private void ReplicaChanged(object sender, FileSystemEventArgs e) => ScheduleSync(WatcherDebounce);
    private void ReplicaRenamed(object sender, RenamedEventArgs e) => ScheduleSync(WatcherDebounce);
    private void WatcherError(object sender, ErrorEventArgs e) => ScheduleSync(WatcherDebounce);

    private void ScheduleSync(TimeSpan delay)
    {
        if (_disposed || !_repository.GetTodoSyncConfiguration().Enabled) return;
        lock (_timerGate) _debounceTimer?.Change(delay, Timeout.InfiniteTimeSpan);
    }

    private void SetState(TodoSyncState state)
    {
        CurrentState = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _repository.TodosChanged -= RepositoryDataChanged;
        _repository.NotesChanged -= RepositoryDataChanged;
        StopMonitoring();
    }
}
