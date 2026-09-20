namespace DesktopPet.Core.Sync;

public static class TodoMergeEngine
{
    public static IReadOnlyList<TodoMaterializedItem> Materialize(IEnumerable<TodoSyncOperation> operations)
    {
        var results = new List<TodoMaterializedItem>();
        foreach (var group in operations.GroupBy(operation => operation.ItemId))
        {
            var add = MaxOperation(group
                .Where(operation => operation.Kind == TodoSyncOperationKinds.Add && !string.IsNullOrWhiteSpace(operation.Title)));
            if (add is null) continue;

            var state = MaxOperation(group
                .Where(operation => operation.Kind is TodoSyncOperationKinds.Add or TodoSyncOperationKinds.SetCompleted));
            var deletion = MaxOperation(group
                .Where(operation => operation.Kind == TodoSyncOperationKinds.Delete));

            results.Add(new TodoMaterializedItem(
                group.Key,
                add.Title!,
                state?.IsCompleted ?? false,
                add.CreatedAtUtc,
                deletion?.CreatedAtUtc));
        }

        return results
            .OrderBy(item => item.IsCompleted)
            .ThenByDescending(item => item.CreatedAtUtc)
            .ThenBy(item => item.ItemId)
            .ToArray();
    }

    public static bool IsValid(TodoSyncOperation operation)
    {
        if (operation.OperationId == Guid.Empty || operation.ItemId == Guid.Empty || operation.DeviceId == Guid.Empty || operation.Counter <= 0)
            return false;

        return operation.Kind switch
        {
            TodoSyncOperationKinds.Add => !string.IsNullOrWhiteSpace(operation.Title) && operation.IsCompleted is not null,
            TodoSyncOperationKinds.SetCompleted => operation.IsCompleted is not null,
            TodoSyncOperationKinds.Delete => true,
            _ => false
        };
    }

    private static TodoSyncOperation? MaxOperation(IEnumerable<TodoSyncOperation> operations) =>
        operations.OrderBy(operation => operation, OperationVersionComparer.Instance).LastOrDefault();

    private sealed class OperationVersionComparer : IComparer<TodoSyncOperation>
    {
        public static readonly OperationVersionComparer Instance = new();

        public int Compare(TodoSyncOperation? x, TodoSyncOperation? y)
        {
            if (ReferenceEquals(x, y)) return 0;
            if (x is null) return -1;
            if (y is null) return 1;
            var counter = x.Counter.CompareTo(y.Counter);
            if (counter != 0) return counter;
            var device = string.CompareOrdinal(x.DeviceId.ToString("D"), y.DeviceId.ToString("D"));
            return device != 0 ? device : string.CompareOrdinal(x.OperationId.ToString("D"), y.OperationId.ToString("D"));
        }
    }
}
