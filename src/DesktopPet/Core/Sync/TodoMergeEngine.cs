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

            var title = MaxOperation(group
                .Where(operation => operation.Kind is TodoSyncOperationKinds.Add or TodoSyncOperationKinds.SetTitle));
            var state = MaxOperation(group
                .Where(operation => operation.Kind is TodoSyncOperationKinds.Add or TodoSyncOperationKinds.SetCompleted));
            var placement = MaxOperation(group.Where(operation => operation.Kind is TodoSyncOperationKinds.Add or TodoSyncOperationKinds.Move));
            var deletion = MaxOperation(group
                .Where(operation => operation.Kind == TodoSyncOperationKinds.Delete));

            results.Add(new TodoMaterializedItem(
                group.Key,
                title?.Title ?? add.Title!,
                state?.IsCompleted ?? false,
                add.CreatedAtUtc,
                deletion?.CreatedAtUtc,
                placement?.ParentItemId,
                placement?.SortKey ?? TodoOrderKey.Legacy(add.CreatedAtUtc, group.Key)));
        }

        // Resolve against the raw graph, so cycles and excessive depth converge independently of iteration order.
        var byId = results.ToDictionary(item => item.ItemId);
        results = results.Select(item => item.ParentItemId is { } parentId &&
            (parentId == item.ItemId || !byId.TryGetValue(parentId, out var parent) ||
             parent.DeletedAtUtc is not null || parent.ParentItemId is not null)
                ? item with { ParentItemId = null } : item).ToList();

        return results
            .OrderBy(item => item.IsCompleted)
            .ThenBy(item => item.SortKey, StringComparer.Ordinal)
            .ThenBy(item => item.ItemId)
            .ToArray();
    }

    public static bool IsValid(TodoSyncOperation operation)
    {
        if (operation.OperationId == Guid.Empty || operation.ItemId == Guid.Empty || operation.DeviceId == Guid.Empty || operation.Counter <= 0)
            return false;

        if (operation.ParentItemId == Guid.Empty || (operation.SortKey is not null && !TodoOrderKey.IsValid(operation.SortKey))) return false;

        return operation.Kind switch
        {
            TodoSyncOperationKinds.Add => !string.IsNullOrWhiteSpace(operation.Title) &&
                                          operation.IsCompleted is not null &&
                                          operation.ParentItemId != operation.ItemId,
            TodoSyncOperationKinds.Move => operation.ParentItemId != operation.ItemId && TodoOrderKey.IsValid(operation.SortKey),
            TodoSyncOperationKinds.SetTitle => !string.IsNullOrWhiteSpace(operation.Title) && operation.ParentItemId is null,
            TodoSyncOperationKinds.SetCompleted => operation.IsCompleted is not null && operation.ParentItemId is null,
            TodoSyncOperationKinds.Delete => operation.ParentItemId is null,
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
