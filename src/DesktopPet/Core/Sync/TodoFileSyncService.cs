using DesktopPet.Data;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DesktopPet.Core.Sync;

public sealed class TodoFileSyncService : IDisposable
{
    private const long MaximumReplicaFileSize = 16 * 1024 * 1024;
    private const int MaximumOperationCount = 100_000;
    private static readonly TimeSpan WatcherDebounce = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan LocalChangeDebounce = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PeriodicScanInterval = TimeSpan.FromSeconds(60);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly DesktopPetRepository _repository;
    private readonly SemaphoreSlim _syncGate = new(1, 1);
    private readonly object _timerGate = new();
    private FileSystemWatcher? _watcher;
    private Timer? _debounceTimer;
    private Timer? _periodicTimer;
    private int _syncPending;
    private bool _started;
    private bool _disposed;

    public TodoFileSyncService(DesktopPetRepository repository)
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
        _repository.TodosChanged += RepositoryTodosChanged;
        var configuration = _repository.GetTodoSyncConfiguration();
        if (!configuration.Enabled || string.IsNullOrWhiteSpace(configuration.Directory))
        {
            SetState(new TodoSyncState(TodoSyncStateKind.Disabled, "OneDrive 同步未启用。", configuration.Directory));
            return;
        }

        StartMonitoring(configuration.Directory);
        _ = SynchronizeAsync();
    }

    public async Task<bool> EnableDefaultAsync()
    {
        var directory = OneDriveFolderLocator.TryGetDefaultSyncDirectory();
        if (directory is null)
        {
            SetState(new TodoSyncState(TodoSyncStateKind.DirectoryUnavailable, "未找到主要 OneDrive 目录，请手动选择同步文件夹。", null));
            return false;
        }
        return await ConfigureDirectoryAsync(directory);
    }

    public async Task<bool> ConfigureDirectoryAsync(string directory)
    {
        if (_disposed || string.IsNullOrWhiteSpace(directory)) return false;
        try
        {
            var fullPath = Path.GetFullPath(directory);
            Directory.CreateDirectory(Path.Combine(fullPath, "replicas"));
            _repository.SetTodoSyncConfiguration(true, fullPath);
            StartMonitoring(fullPath);
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
        SetState(new TodoSyncState(TodoSyncStateKind.Disabled, "OneDrive 同步已停用，本地待办仍会保留。", configuration.Directory));
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
            SetState(new TodoSyncState(TodoSyncStateKind.DirectoryUnavailable, "OneDrive 同步目录当前不可用，本地待办仍可正常使用。", root));
            return;
        }

        SetState(new TodoSyncState(TodoSyncStateKind.Syncing, "正在合并 OneDrive 文件夹中的待办…", root, CurrentState.LastSuccessfulMergeUtc));
        try
        {
            var replicasPath = Path.Combine(root, "replicas");
            Directory.CreateDirectory(replicasPath);
            EnsureWatcher(replicasPath);

            var incoming = new List<TodoSyncOperation>();
            var invalidFiles = new List<string>();
            foreach (var file in Directory.EnumerateFiles(replicasPath, "*.json", SearchOption.TopDirectoryOnly))
            {
                var replica = await TryReadReplicaAsync(file);
                if (replica is null)
                {
                    invalidFiles.Add(Path.GetFileName(file));
                    continue;
                }
                incoming.AddRange(replica.Operations);
            }

            _repository.MergeTodoSyncOperations(incoming);
            await WriteOwnReplicaIfChangedAsync(replicasPath, configuration.DeviceId, _repository.GetTodoSyncOperations());
            var completedAt = DateTimeOffset.UtcNow;
            SetState(invalidFiles.Count == 0
                ? new TodoSyncState(TodoSyncStateKind.Ready, "已写入 OneDrive 文件夹。", root, completedAt)
                : new TodoSyncState(TodoSyncStateKind.Warning, $"同步已完成，但忽略了 {invalidFiles.Count} 个无效副本文件。", root, completedAt));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            SetState(new TodoSyncState(TodoSyncStateKind.Error, $"同步失败：{exception.Message}", root, CurrentState.LastSuccessfulMergeUtc));
        }
    }

    private static async Task<TodoReplicaFile?> TryReadReplicaAsync(string path)
    {
        try
        {
            var fileInfo = new FileInfo(path);
            if (!fileInfo.Exists || fileInfo.Length <= 0 || fileInfo.Length > MaximumReplicaFileSize) return null;
            for (var attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 32 * 1024, useAsync: true);
                    var replica = await JsonSerializer.DeserializeAsync<TodoReplicaFile>(stream, JsonOptions);
                    if (replica is null || replica.Format != TodoReplicaFile.ExpectedFormat ||
                        replica.SchemaVersion != TodoReplicaFile.CurrentSchemaVersion || replica.ReplicaId == Guid.Empty ||
                        replica.Operations.Count > MaximumOperationCount || replica.Operations.Any(operation => !TodoMergeEngine.IsValid(operation)))
                        return null;
                    return replica;
                }
                catch (IOException) when (attempt < 4)
                {
                    await Task.Delay(200);
                }
                catch (JsonException) when (attempt < 4)
                {
                    await Task.Delay(200);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
        return null;
    }

    private static async Task WriteOwnReplicaIfChangedAsync(string replicasPath, Guid deviceId, IReadOnlyList<TodoSyncOperation> operations)
    {
        var replica = new TodoReplicaFile { ReplicaId = deviceId, Operations = operations };
        var contents = JsonSerializer.SerializeToUtf8Bytes(replica, JsonOptions);
        if (contents.Length > MaximumReplicaFileSize) throw new IOException("待办同步副本超过 16 MB 限制。");
        var targetPath = Path.Combine(replicasPath, $"{deviceId:D}.json");
        if (File.Exists(targetPath))
        {
            try
            {
                var existing = await File.ReadAllBytesAsync(targetPath);
                if (existing.AsSpan().SequenceEqual(contents)) return;
            }
            catch (IOException)
            {
                // The atomic replacement below will retry on the next synchronization if OneDrive still owns the file.
            }
        }

        var temporaryPath = Path.Combine(replicasPath, $"{deviceId:D}.{Guid.NewGuid():N}.partial");
        try
        {
            await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 32 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(contents);
                await stream.FlushAsync();
            }
            File.Move(temporaryPath, targetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private void StartMonitoring(string directory)
    {
        StopMonitoring();
        var replicasPath = Path.Combine(directory, "replicas");
        if (Directory.Exists(replicasPath)) EnsureWatcher(replicasPath);
        lock (_timerGate)
        {
            _debounceTimer = new Timer(_ => _ = SynchronizeAsync(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _periodicTimer = new Timer(_ => _ = SynchronizeAsync(), null, PeriodicScanInterval, PeriodicScanInterval);
        }
    }

    private void EnsureWatcher(string replicasPath)
    {
        if (_watcher is not null && string.Equals(_watcher.Path, replicasPath, StringComparison.OrdinalIgnoreCase)) return;
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(replicasPath, "*.json")
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        _watcher.Changed += ReplicaChanged;
        _watcher.Created += ReplicaChanged;
        _watcher.Deleted += ReplicaChanged;
        _watcher.Renamed += ReplicaRenamed;
        _watcher.Error += WatcherError;
    }

    private void StopMonitoring()
    {
        _watcher?.Dispose();
        _watcher = null;
        lock (_timerGate)
        {
            _debounceTimer?.Dispose();
            _periodicTimer?.Dispose();
            _debounceTimer = null;
            _periodicTimer = null;
        }
    }

    private void RepositoryTodosChanged(object? sender, EventArgs e) => ScheduleSync(LocalChangeDebounce);
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
        _repository.TodosChanged -= RepositoryTodosChanged;
        StopMonitoring();
    }
}
