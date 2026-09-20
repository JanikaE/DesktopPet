namespace DesktopPet.Core.Sync;

public static class TodoSyncOperationKinds
{
    public const string Add = "add";
    public const string SetCompleted = "setCompleted";
    public const string Delete = "delete";
}

public sealed record TodoSyncOperation(
    Guid OperationId,
    Guid ItemId,
    Guid DeviceId,
    long Counter,
    string Kind,
    string? Title,
    bool? IsCompleted,
    DateTimeOffset CreatedAtUtc);

public sealed record TodoMaterializedItem(
    Guid ItemId,
    string Title,
    bool IsCompleted,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset? DeletedAtUtc);

public sealed class TodoReplicaFile
{
    public const string ExpectedFormat = "desktop-pet.todo-replica";
    public const int CurrentSchemaVersion = 1;

    public string Format { get; init; } = ExpectedFormat;
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    public Guid ReplicaId { get; init; }
    public IReadOnlyList<TodoSyncOperation> Operations { get; init; } = [];
}

public sealed record TodoSyncConfiguration(bool Enabled, string? Directory, Guid DeviceId);

public enum TodoSyncStateKind
{
    Disabled,
    Ready,
    Syncing,
    DirectoryUnavailable,
    Warning,
    Error
}

public sealed record TodoSyncState(
    TodoSyncStateKind Kind,
    string Message,
    string? Directory,
    DateTimeOffset? LastSuccessfulMergeUtc = null);
