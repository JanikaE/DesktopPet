using DesktopPet.Core.Sync;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Text.Json;

namespace DesktopPet.Data;

public sealed class DesktopPetRepository(string databasePath)
{
    private const int MouseGridSchemaVersion = 1;
    private const int CurrentSchemaVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    private readonly object _todoGate = new();
    public event EventHandler? TodosChanged;
    public event EventHandler? NotesChanged;
    public event EventHandler? LaunchersChanged;

    public void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS todos (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              title TEXT NOT NULL,
              is_completed INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS launchers (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              name TEXT NOT NULL,
              target_path TEXT NOT NULL UNIQUE,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS clipboard_items (
              id INTEGER PRIMARY KEY AUTOINCREMENT,
              content TEXT NOT NULL,
              created_at TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS keyboard_statistics (
              stat_date TEXT NOT NULL,
              key_code INTEGER NOT NULL,
              press_count INTEGER NOT NULL,
              PRIMARY KEY (stat_date, key_code)
            );
            CREATE TABLE IF NOT EXISTS mouse_heatmap (
              stat_date TEXT NOT NULL,
              grid_x INTEGER NOT NULL,
              grid_y INTEGER NOT NULL,
              move_count INTEGER NOT NULL,
              PRIMARY KEY (stat_date, grid_x, grid_y)
            );
            CREATE TABLE IF NOT EXISTS mouse_clicks (
              stat_date TEXT NOT NULL,
              grid_x INTEGER NOT NULL,
              grid_y INTEGER NOT NULL,
              button INTEGER NOT NULL,
              click_count INTEGER NOT NULL,
              PRIMARY KEY (stat_date, grid_x, grid_y, button)
            );
            CREATE TABLE IF NOT EXISTS mouse_daily (
              stat_date TEXT PRIMARY KEY,
              move_distance REAL NOT NULL,
              left_clicks INTEGER NOT NULL,
              right_clicks INTEGER NOT NULL,
              middle_clicks INTEGER NOT NULL,
              wheel_units INTEGER NOT NULL,
              double_clicks INTEGER NOT NULL
            );
            CREATE TABLE IF NOT EXISTS sync_metadata (
              key TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            CREATE TABLE IF NOT EXISTS todo_sync_operations (
              operation_id TEXT PRIMARY KEY,
              item_sync_id TEXT NOT NULL,
              device_id TEXT NOT NULL,
              counter INTEGER NOT NULL,
              kind TEXT NOT NULL,
              title TEXT NULL,
              is_completed INTEGER NULL,
              created_at TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_todo_sync_operations_item ON todo_sync_operations(item_sync_id);
            CREATE TABLE IF NOT EXISTS notes (
              sync_id TEXT PRIMARY KEY,
              content TEXT NOT NULL,
              is_conflict INTEGER NOT NULL DEFAULT 0,
              created_at TEXT NOT NULL,
              updated_at TEXT NOT NULL,
              deleted_at TEXT NULL,
              revision_id TEXT NOT NULL,
              version_device_id TEXT NOT NULL,
              version_counter INTEGER NOT NULL,
              version_vector TEXT NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_notes_updated_at ON notes(updated_at DESC);
            """;
        command.ExecuteNonQuery();

        using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = "SELECT COUNT(*) FROM pragma_table_info('launchers') WHERE name = 'sort_order';";
        if (Convert.ToInt32(schemaCommand.ExecuteScalar()) == 0)
        {
            using var migrationCommand = connection.CreateCommand();
            migrationCommand.CommandText = "ALTER TABLE launchers ADD COLUMN sort_order INTEGER NOT NULL DEFAULT 0; UPDATE launchers SET sort_order = id;";
            migrationCommand.ExecuteNonQuery();
        }

        using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        if (Convert.ToInt32(versionCommand.ExecuteScalar()) < MouseGridSchemaVersion)
        {
            using var clearCommand = connection.CreateCommand();
            clearCommand.CommandText = "DELETE FROM mouse_heatmap; DELETE FROM mouse_clicks;";
            clearCommand.ExecuteNonQuery();
            using var setVersionCommand = connection.CreateCommand();
            setVersionCommand.CommandText = $"PRAGMA user_version = {MouseGridSchemaVersion};";
            setVersionCommand.ExecuteNonQuery();
        }

        EnsureTodoSyncSchema(connection);
    }

    public IReadOnlyList<TodoItem> GetTodos()
    {
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT sync_id, title, is_completed FROM todos WHERE deleted_at IS NULL ORDER BY is_completed, created_at DESC, id DESC;";
            using var reader = command.ExecuteReader();
            var results = new List<TodoItem>();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var id)) continue;
                results.Add(new TodoItem(id, reader.GetString(1), reader.GetBoolean(2)));
            }
            return results;
        }
    }

    public TodoItem AddTodo(string title)
    {
        var itemId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var deviceId = GetDeviceId(connection, transaction);
            var counter = NextTodoClock(connection, transaction);

            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO todos (sync_id, title, is_completed, created_at, deleted_at) VALUES ($id, $title, 0, $createdAt, NULL);";
            command.Parameters.AddWithValue("$id", itemId.ToString("D"));
            command.Parameters.AddWithValue("$title", title);
            command.Parameters.AddWithValue("$createdAt", createdAt.ToString("O"));
            command.ExecuteNonQuery();
            InsertTodoOperation(connection, transaction, new TodoSyncOperation(Guid.NewGuid(), itemId, deviceId, counter, TodoSyncOperationKinds.Add, title, false, createdAt));
            transaction.Commit();
        }

        var item = new TodoItem(itemId, title, false);
        TodosChanged?.Invoke(this, EventArgs.Empty);
        return item;
    }

    public void SetTodoCompleted(Guid id, bool isCompleted)
    {
        var changed = false;
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var readCommand = connection.CreateCommand();
            readCommand.Transaction = transaction;
            readCommand.CommandText = "SELECT is_completed FROM todos WHERE sync_id = $id AND deleted_at IS NULL;";
            readCommand.Parameters.AddWithValue("$id", id.ToString("D"));
            var current = readCommand.ExecuteScalar();
            if (current is null || Convert.ToBoolean(current) == isCompleted) return;

            using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = "UPDATE todos SET is_completed = $completed WHERE sync_id = $id AND deleted_at IS NULL;";
            updateCommand.Parameters.AddWithValue("$id", id.ToString("D"));
            updateCommand.Parameters.AddWithValue("$completed", isCompleted);
            changed = updateCommand.ExecuteNonQuery() > 0;
            if (changed)
            {
                var operation = new TodoSyncOperation(Guid.NewGuid(), id, GetDeviceId(connection, transaction), NextTodoClock(connection, transaction), TodoSyncOperationKinds.SetCompleted, null, isCompleted, DateTimeOffset.UtcNow);
                InsertTodoOperation(connection, transaction, operation);
            }
            transaction.Commit();
        }
        if (changed) TodosChanged?.Invoke(this, EventArgs.Empty);
    }

    public void DeleteTodo(Guid id)
    {
        var changed = false;
        var deletedAt = DateTimeOffset.UtcNow;
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "UPDATE todos SET deleted_at = $deletedAt WHERE sync_id = $id AND deleted_at IS NULL;";
            command.Parameters.AddWithValue("$id", id.ToString("D"));
            command.Parameters.AddWithValue("$deletedAt", deletedAt.ToString("O"));
            changed = command.ExecuteNonQuery() > 0;
            if (changed)
            {
                var operation = new TodoSyncOperation(Guid.NewGuid(), id, GetDeviceId(connection, transaction), NextTodoClock(connection, transaction), TodoSyncOperationKinds.Delete, null, null, deletedAt);
                InsertTodoOperation(connection, transaction, operation);
            }
            transaction.Commit();
        }
        if (changed) TodosChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<TodoSyncOperation> GetTodoSyncOperations()
    {
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT operation_id, item_sync_id, device_id, counter, kind, title, is_completed, created_at
                FROM todo_sync_operations
                ORDER BY counter, device_id, operation_id;
                """;
            using var reader = command.ExecuteReader();
            var operations = new List<TodoSyncOperation>();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var operationId) ||
                    !Guid.TryParse(reader.GetString(1), out var itemId) ||
                    !Guid.TryParse(reader.GetString(2), out var deviceId) ||
                    !DateTimeOffset.TryParse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
                    continue;
                operations.Add(new TodoSyncOperation(
                    operationId,
                    itemId,
                    deviceId,
                    reader.GetInt64(3),
                    reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    reader.IsDBNull(6) ? null : reader.GetBoolean(6),
                    createdAt));
            }
            return operations;
        }
    }

    public int MergeTodoSyncOperations(IEnumerable<TodoSyncOperation> incomingOperations)
    {
        var operations = incomingOperations
            .Where(TodoMergeEngine.IsValid)
            .GroupBy(operation => operation.OperationId)
            .Select(group => group.First())
            .ToArray();
        if (operations.Length == 0) return 0;

        var inserted = 0;
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            foreach (var operation in operations)
                inserted += InsertTodoOperation(connection, transaction, operation, ignoreExisting: true);

            var observedCounter = operations.Max(operation => operation.Counter);
            var currentCounter = GetMetadataLong(connection, transaction, "todo_sync_clock");
            if (observedCounter > currentCounter)
                SetMetadata(connection, transaction, "todo_sync_clock", observedCounter.ToString(CultureInfo.InvariantCulture));

            if (inserted > 0) RebuildTodosFromOperations(connection, transaction);
            transaction.Commit();
        }

        if (inserted > 0) TodosChanged?.Invoke(this, EventArgs.Empty);
        return inserted;
    }

    public TodoSyncConfiguration GetTodoSyncConfiguration()
    {
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            MigrateSyncConfiguration(connection, transaction);
            var configuration = new TodoSyncConfiguration(
                string.Equals(GetMetadata(connection, transaction, "sync_enabled"), "1", StringComparison.Ordinal),
                GetMetadata(connection, transaction, "sync_directory"),
                GetDeviceId(connection, transaction));
            transaction.Commit();
            return configuration;
        }
    }

    public void SetTodoSyncConfiguration(bool enabled, string? directory)
    {
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            SetMetadata(connection, transaction, "sync_enabled", enabled ? "1" : "0");
            SetMetadata(connection, transaction, "todo_sync_enabled", enabled ? "1" : "0");
            if (string.IsNullOrWhiteSpace(directory))
            {
                DeleteMetadata(connection, transaction, "sync_directory");
            }
            else
            {
                var root = Path.GetFullPath(directory);
                SetMetadata(connection, transaction, "sync_directory", root);
                SetMetadata(connection, transaction, "todo_sync_directory", Path.Combine(root, "TodoSync"));
                DeleteMetadata(connection, transaction, "sync_legacy_directory");
            }
            transaction.Commit();
        }
    }

    public string? GetLegacySyncDirectory()
    {
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            MigrateSyncConfiguration(connection, transaction);
            var value = GetMetadata(connection, transaction, "sync_legacy_directory");
            transaction.Commit();
            return value;
        }
    }

    public IReadOnlyList<NoteItem> GetNotes()
    {
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            return ReadNoteRecords(connection)
                .Where(note => note.DeletedAtUtc is null)
                .OrderByDescending(note => note.UpdatedAtUtc)
                .ThenBy(note => note.NoteId)
                .Select(ToNoteItem)
                .ToArray();
        }
    }

    public NoteItem AddNote(string content)
    {
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("便签内容不能为空。", nameof(content));
        ValidateNoteContent(content);
        NoteSyncRecord record;
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var deviceId = GetDeviceId(connection, transaction);
            var counter = NextTodoClock(connection, transaction);
            var now = DateTimeOffset.UtcNow;
            record = new NoteSyncRecord(
                Guid.NewGuid(), content, false, now, now, null,
                new NoteVersion(Guid.NewGuid(), deviceId, counter),
                new Dictionary<string, long>(StringComparer.Ordinal) { [deviceId.ToString("D")] = counter });
            UpsertNoteRecords(connection, transaction, [record]);
            transaction.Commit();
        }
        NotesChanged?.Invoke(this, EventArgs.Empty);
        return ToNoteItem(record);
    }

    public NoteItem UpdateNote(NoteItem basis, string content)
    {
        ValidateNoteContent(content);
        NoteSyncRecord saved;
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var deviceId = GetDeviceId(connection, transaction);
            var counter = NextTodoClock(connection, transaction);
            var vector = new Dictionary<string, long>(basis.VersionVector, StringComparer.Ordinal)
            {
                [deviceId.ToString("D")] = counter
            };
            var candidate = new NoteSyncRecord(
                basis.Id, content, basis.IsConflict, basis.CreatedAtUtc, DateTimeOffset.UtcNow, null,
                new NoteVersion(Guid.NewGuid(), deviceId, counter), vector);
            var merged = NoteMergeEngine.Merge(ReadNoteRecords(connection, transaction), [candidate]);
            UpsertNoteRecords(connection, transaction, merged);
            saved = merged.FirstOrDefault(note => note.NoteId == basis.Id && note.Version.RevisionId == candidate.Version.RevisionId)
                ?? merged.FirstOrDefault(note => note.NoteId == NoteMergeEngine.ConflictNoteId(basis.Id, candidate.Version.RevisionId))
                ?? candidate;
            transaction.Commit();
        }
        NotesChanged?.Invoke(this, EventArgs.Empty);
        return ToNoteItem(saved);
    }

    public void DeleteNote(NoteItem basis)
    {
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var deviceId = GetDeviceId(connection, transaction);
            var counter = NextTodoClock(connection, transaction);
            var vector = new Dictionary<string, long>(basis.VersionVector, StringComparer.Ordinal)
            {
                [deviceId.ToString("D")] = counter
            };
            var now = DateTimeOffset.UtcNow;
            var deletion = new NoteSyncRecord(
                basis.Id, basis.Content, basis.IsConflict, basis.CreatedAtUtc, now, now,
                new NoteVersion(Guid.NewGuid(), deviceId, counter), vector);
            var merged = NoteMergeEngine.Merge(ReadNoteRecords(connection, transaction), [deletion]);
            UpsertNoteRecords(connection, transaction, merged);
            transaction.Commit();
        }
        NotesChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<NoteSyncRecord> GetNoteSyncRecords()
    {
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            return ReadNoteRecords(connection).OrderBy(note => note.NoteId).ToArray();
        }
    }

    public int MergeNoteSyncRecords(IEnumerable<NoteSyncRecord> incomingRecords)
    {
        var incoming = incomingRecords.Where(NoteMergeEngine.IsValid).ToArray();
        if (incoming.Length == 0) return 0;
        var changed = 0;
        lock (_todoGate)
        {
            using var connection = OpenConnection();
            using var transaction = connection.BeginTransaction();
            var existing = ReadNoteRecords(connection, transaction);
            var before = NoteFingerprint(existing);
            var merged = NoteMergeEngine.Merge(existing, incoming);
            var after = NoteFingerprint(merged);
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                UpsertNoteRecords(connection, transaction, merged);
                changed = 1;
            }

            var observedCounter = incoming.SelectMany(note => note.VersionVector.Values).DefaultIfEmpty(0).Max();
            var currentCounter = GetMetadataLong(connection, transaction, "sync_clock");
            if (observedCounter > currentCounter)
            {
                SetMetadata(connection, transaction, "sync_clock", observedCounter.ToString(CultureInfo.InvariantCulture));
                SetMetadata(connection, transaction, "todo_sync_clock", observedCounter.ToString(CultureInfo.InvariantCulture));
            }
            transaction.Commit();
        }
        if (changed > 0) NotesChanged?.Invoke(this, EventArgs.Empty);
        return changed;
    }

    public IReadOnlyList<ClipboardItem> GetClipboardItems()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, content FROM clipboard_items ORDER BY id DESC;";
        using var reader = command.ExecuteReader();
        var results = new List<ClipboardItem>();
        while (reader.Read()) results.Add(new ClipboardItem(reader.GetInt64(0), reader.GetString(1)));
        return results;
    }

    public ClipboardItem AddClipboardItem(string content)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO clipboard_items (content, created_at) VALUES ($content, $createdAt); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$content", content);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        return new ClipboardItem((long)command.ExecuteScalar()!, content);
    }

    public void DeleteClipboardItem(long id) => Execute("DELETE FROM clipboard_items WHERE id = $id;", ("$id", id));

    public void AddKeyboardStatistics(DateOnly date, IReadOnlyDictionary<int, long> counts)
    {
        if (counts.Count == 0) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO keyboard_statistics (stat_date, key_code, press_count)
            VALUES ($date, $keyCode, $count)
            ON CONFLICT(stat_date, key_code) DO UPDATE SET press_count = press_count + excluded.press_count;
            """;
        var dateParameter = command.Parameters.Add("$date", SqliteType.Text);
        var keyParameter = command.Parameters.Add("$keyCode", SqliteType.Integer);
        var countParameter = command.Parameters.Add("$count", SqliteType.Integer);
        dateParameter.Value = date.ToString("yyyy-MM-dd");
        foreach (var (keyCode, count) in counts)
        {
            keyParameter.Value = keyCode;
            countParameter.Value = count;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public IReadOnlyDictionary<int, long> GetKeyboardStatistics(DateOnly? startDate, DateOnly? endDate)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var filters = new List<string>();
        if (startDate is not null) { filters.Add("stat_date >= $startDate"); command.Parameters.AddWithValue("$startDate", startDate.Value.ToString("yyyy-MM-dd")); }
        if (endDate is not null) { filters.Add("stat_date <= $endDate"); command.Parameters.AddWithValue("$endDate", endDate.Value.ToString("yyyy-MM-dd")); }
        command.CommandText = $"SELECT key_code, SUM(press_count) FROM keyboard_statistics{(filters.Count == 0 ? "" : " WHERE " + string.Join(" AND ", filters))} GROUP BY key_code;";
        using var reader = command.ExecuteReader();
        var results = new Dictionary<int, long>();
        while (reader.Read()) results[reader.GetInt32(0)] = reader.GetInt64(1);
        return results;
    }

    public IReadOnlyList<DateOnly> GetKeyboardStatisticDates()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT DISTINCT stat_date FROM keyboard_statistics ORDER BY stat_date;";
        using var reader = command.ExecuteReader();
        var results = new List<DateOnly>();
        while (reader.Read() && DateOnly.TryParse(reader.GetString(0), out var date)) results.Add(date);
        return results;
    }

    public void AddMouseStatistics(
        DateOnly date,
        IReadOnlyDictionary<(int X, int Y), long> moveCounts,
        IReadOnlyDictionary<(int X, int Y, int Button), long> clickCounts,
        MouseDailyTotals daily)
    {
        if (moveCounts.Count == 0 && clickCounts.Count == 0 && daily.IsEmpty) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        if (moveCounts.Count > 0)
        {
            using var moveCommand = connection.CreateCommand();
            moveCommand.Transaction = transaction;
            moveCommand.CommandText = """
                INSERT INTO mouse_heatmap (stat_date, grid_x, grid_y, move_count)
                VALUES ($date, $x, $y, $count)
                ON CONFLICT(stat_date, grid_x, grid_y) DO UPDATE SET move_count = move_count + excluded.move_count;
                """;
            var dateParameter = moveCommand.Parameters.Add("$date", SqliteType.Text);
            var xParameter = moveCommand.Parameters.Add("$x", SqliteType.Integer);
            var yParameter = moveCommand.Parameters.Add("$y", SqliteType.Integer);
            var countParameter = moveCommand.Parameters.Add("$count", SqliteType.Integer);
            dateParameter.Value = date.ToString("yyyy-MM-dd");
            foreach (var ((x, y), count) in moveCounts)
            {
                xParameter.Value = x;
                yParameter.Value = y;
                countParameter.Value = count;
                moveCommand.ExecuteNonQuery();
            }
        }

        if (clickCounts.Count > 0)
        {
            using var clickCommand = connection.CreateCommand();
            clickCommand.Transaction = transaction;
            clickCommand.CommandText = """
                INSERT INTO mouse_clicks (stat_date, grid_x, grid_y, button, click_count)
                VALUES ($date, $x, $y, $button, $count)
                ON CONFLICT(stat_date, grid_x, grid_y, button) DO UPDATE SET click_count = click_count + excluded.click_count;
                """;
            var dateParameter = clickCommand.Parameters.Add("$date", SqliteType.Text);
            var xParameter = clickCommand.Parameters.Add("$x", SqliteType.Integer);
            var yParameter = clickCommand.Parameters.Add("$y", SqliteType.Integer);
            var buttonParameter = clickCommand.Parameters.Add("$button", SqliteType.Integer);
            var countParameter = clickCommand.Parameters.Add("$count", SqliteType.Integer);
            dateParameter.Value = date.ToString("yyyy-MM-dd");
            foreach (var ((x, y, button), count) in clickCounts)
            {
                xParameter.Value = x;
                yParameter.Value = y;
                buttonParameter.Value = button;
                countParameter.Value = count;
                clickCommand.ExecuteNonQuery();
            }
        }

        if (!daily.IsEmpty)
        {
            using var dailyCommand = connection.CreateCommand();
            dailyCommand.Transaction = transaction;
            dailyCommand.CommandText = """
                INSERT INTO mouse_daily (stat_date, move_distance, left_clicks, right_clicks, middle_clicks, wheel_units, double_clicks)
                VALUES ($date, $distance, $left, $right, $middle, $wheel, $double)
                ON CONFLICT(stat_date) DO UPDATE SET
                  move_distance = move_distance + excluded.move_distance,
                  left_clicks = left_clicks + excluded.left_clicks,
                  right_clicks = right_clicks + excluded.right_clicks,
                  middle_clicks = middle_clicks + excluded.middle_clicks,
                  wheel_units = wheel_units + excluded.wheel_units,
                  double_clicks = double_clicks + excluded.double_clicks;
                """;
            dailyCommand.Parameters.AddWithValue("$date", date.ToString("yyyy-MM-dd"));
            dailyCommand.Parameters.AddWithValue("$distance", daily.MoveDistance);
            dailyCommand.Parameters.AddWithValue("$left", daily.LeftClicks);
            dailyCommand.Parameters.AddWithValue("$right", daily.RightClicks);
            dailyCommand.Parameters.AddWithValue("$middle", daily.MiddleClicks);
            dailyCommand.Parameters.AddWithValue("$wheel", daily.WheelUnits);
            dailyCommand.Parameters.AddWithValue("$double", daily.DoubleClicks);
            dailyCommand.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    public IReadOnlyDictionary<(int X, int Y), long> GetMouseHeatmap(DateOnly? startDate, DateOnly? endDate)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT grid_x, grid_y, SUM(move_count) FROM mouse_heatmap{DateFilter(startDate, endDate, command)} GROUP BY grid_x, grid_y;";
        using var reader = command.ExecuteReader();
        var results = new Dictionary<(int X, int Y), long>();
        while (reader.Read()) results[(reader.GetInt32(0), reader.GetInt32(1))] = reader.GetInt64(2);
        return results;
    }

    public IReadOnlyDictionary<(int X, int Y, int Button), long> GetMouseClicks(DateOnly? startDate, DateOnly? endDate)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT grid_x, grid_y, button, SUM(click_count) FROM mouse_clicks{DateFilter(startDate, endDate, command)} GROUP BY grid_x, grid_y, button;";
        using var reader = command.ExecuteReader();
        var results = new Dictionary<(int X, int Y, int Button), long>();
        while (reader.Read()) results[(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2))] = reader.GetInt64(3);
        return results;
    }

    public MouseDailyTotals GetMouseDailyTotals(DateOnly? startDate, DateOnly? endDate)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COALESCE(SUM(move_distance), 0), COALESCE(SUM(left_clicks), 0), COALESCE(SUM(right_clicks), 0), COALESCE(SUM(middle_clicks), 0), COALESCE(SUM(wheel_units), 0), COALESCE(SUM(double_clicks), 0) FROM mouse_daily{DateFilter(startDate, endDate, command)};";
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return MouseDailyTotals.Empty;
        return new MouseDailyTotals(reader.GetDouble(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5));
    }

    public IReadOnlyList<DateOnly> GetMouseStatisticDates()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT stat_date FROM mouse_heatmap UNION SELECT stat_date FROM mouse_clicks UNION SELECT stat_date FROM mouse_daily ORDER BY stat_date;";
        using var reader = command.ExecuteReader();
        var results = new List<DateOnly>();
        while (reader.Read() && DateOnly.TryParse(reader.GetString(0), out var date)) results.Add(date);
        return results;
    }

    private static string DateFilter(DateOnly? startDate, DateOnly? endDate, SqliteCommand command)
    {
        var filters = new List<string>();
        if (startDate is not null) { filters.Add("stat_date >= $startDate"); command.Parameters.AddWithValue("$startDate", startDate.Value.ToString("yyyy-MM-dd")); }
        if (endDate is not null) { filters.Add("stat_date <= $endDate"); command.Parameters.AddWithValue("$endDate", endDate.Value.ToString("yyyy-MM-dd")); }
        return filters.Count == 0 ? string.Empty : " WHERE " + string.Join(" AND ", filters);
    }

    public IReadOnlyList<LauncherItem> GetLaunchers()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, target_path FROM launchers ORDER BY sort_order, id;";
        using var reader = command.ExecuteReader();
        var results = new List<LauncherItem>();
        while (reader.Read()) results.Add(new LauncherItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2)));
        return results;
    }

    public LauncherItem AddLauncher(string name, string targetPath)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO launchers (name, target_path, created_at, sort_order) VALUES ($name, $path, $createdAt, COALESCE((SELECT MAX(sort_order) + 1 FROM launchers), 0)); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$path", targetPath);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        var item = new LauncherItem((long)command.ExecuteScalar()!, name, targetPath);
        LaunchersChanged?.Invoke(this, EventArgs.Empty);
        return item;
    }

public void DeleteLauncher(long id)
{
    Execute("DELETE FROM launchers WHERE id = $id;", ("$id", id));
    LaunchersChanged?.Invoke(this, EventArgs.Empty);
}

public void RenameLauncher(long id, string name)
{
    using var connection = OpenConnection();
    using var command = connection.CreateCommand();
    command.CommandText = "UPDATE launchers SET name = $name WHERE id = $id;";
    command.Parameters.AddWithValue("$name", name);
    command.Parameters.AddWithValue("$id", id);
    command.ExecuteNonQuery();
    LaunchersChanged?.Invoke(this, EventArgs.Empty);
}

    public void ReorderLaunchers(IReadOnlyList<long> launcherIds)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE launchers SET sort_order = $sortOrder WHERE id = $id;";
        var sortOrderParameter = command.Parameters.Add("$sortOrder", SqliteType.Integer);
        var idParameter = command.Parameters.Add("$id", SqliteType.Integer);
        for (var index = 0; index < launcherIds.Count; index++)
        {
            sortOrderParameter.Value = index;
            idParameter.Value = launcherIds[index];
            command.ExecuteNonQuery();
        }
        transaction.Commit();
        LaunchersChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void EnsureTodoSyncSchema(SqliteConnection connection)
    {
        EnsureColumn(connection, "todos", "sync_id", "TEXT NULL");
        EnsureColumn(connection, "todos", "deleted_at", "TEXT NULL");

        using var transaction = connection.BeginTransaction();
        var deviceId = GetDeviceId(connection, transaction);
        var legacyTodos = new List<(long Id, string Title, bool IsCompleted, DateTimeOffset CreatedAt)>();
        using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText = "SELECT id, title, is_completed, created_at FROM todos WHERE sync_id IS NULL OR sync_id = '';";
            using var reader = readCommand.ExecuteReader();
            while (reader.Read())
            {
                var createdAt = DateTimeOffset.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                    ? parsed
                    : DateTimeOffset.UtcNow;
                legacyTodos.Add((reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2), createdAt));
            }
        }

        foreach (var legacy in legacyTodos)
        {
            var itemId = Guid.NewGuid();
            using var updateCommand = connection.CreateCommand();
            updateCommand.Transaction = transaction;
            updateCommand.CommandText = "UPDATE todos SET sync_id = $syncId WHERE id = $id;";
            updateCommand.Parameters.AddWithValue("$syncId", itemId.ToString("D"));
            updateCommand.Parameters.AddWithValue("$id", legacy.Id);
            updateCommand.ExecuteNonQuery();
            InsertTodoOperation(connection, transaction, new TodoSyncOperation(
                Guid.NewGuid(), itemId, deviceId, NextTodoClock(connection, transaction), TodoSyncOperationKinds.Add,
                legacy.Title, legacy.IsCompleted, legacy.CreatedAt));
        }

        using (var indexCommand = connection.CreateCommand())
        {
            indexCommand.Transaction = transaction;
            indexCommand.CommandText = "CREATE UNIQUE INDEX IF NOT EXISTS idx_todos_sync_id ON todos(sync_id);";
            indexCommand.ExecuteNonQuery();
        }

        using (var versionCommand = connection.CreateCommand())
        {
            versionCommand.Transaction = transaction;
            versionCommand.CommandText = "PRAGMA user_version;";
            var version = Convert.ToInt32(versionCommand.ExecuteScalar());
            if (version < CurrentSchemaVersion)
            {
                versionCommand.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
                versionCommand.ExecuteNonQuery();
            }
        }
        transaction.Commit();
    }

    private static void EnsureColumn(SqliteConnection connection, string table, string column, string declaration)
    {
        using var schemaCommand = connection.CreateCommand();
        schemaCommand.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = $column;";
        schemaCommand.Parameters.AddWithValue("$column", column);
        if (Convert.ToInt32(schemaCommand.ExecuteScalar()) != 0) return;
        using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration};";
        alterCommand.ExecuteNonQuery();
    }

    private static Guid GetDeviceId(SqliteConnection connection, SqliteTransaction transaction)
    {
        var existing = GetMetadata(connection, transaction, "sync_device_id") ??
                       GetMetadata(connection, transaction, "todo_sync_device_id");
        if (Guid.TryParse(existing, out var deviceId) && deviceId != Guid.Empty)
        {
            SetMetadata(connection, transaction, "sync_device_id", deviceId.ToString("D"));
            SetMetadata(connection, transaction, "todo_sync_device_id", deviceId.ToString("D"));
            return deviceId;
        }
        deviceId = Guid.NewGuid();
        SetMetadata(connection, transaction, "sync_device_id", deviceId.ToString("D"));
        SetMetadata(connection, transaction, "todo_sync_device_id", deviceId.ToString("D"));
        return deviceId;
    }

    private static long NextTodoClock(SqliteConnection connection, SqliteTransaction transaction)
    {
        var current = Math.Max(
            GetMetadataLong(connection, transaction, "sync_clock"),
            GetMetadataLong(connection, transaction, "todo_sync_clock"));
        var next = checked(current + 1);
        SetMetadata(connection, transaction, "sync_clock", next.ToString(CultureInfo.InvariantCulture));
        SetMetadata(connection, transaction, "todo_sync_clock", next.ToString(CultureInfo.InvariantCulture));
        return next;
    }

    private static void MigrateSyncConfiguration(SqliteConnection connection, SqliteTransaction transaction)
    {
        if (GetMetadata(connection, transaction, "sync_directory") is not null ||
            GetMetadata(connection, transaction, "sync_legacy_directory") is not null)
            return;

        var legacyDirectory = GetMetadata(connection, transaction, "todo_sync_directory");
        if (string.IsNullOrWhiteSpace(legacyDirectory)) return;
        var fullPath = Path.GetFullPath(legacyDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.Equals(Path.GetFileName(fullPath), "TodoSync", StringComparison.OrdinalIgnoreCase) &&
            Directory.GetParent(fullPath) is { } parent)
        {
            SetMetadata(connection, transaction, "sync_directory", parent.FullName);
            SetMetadata(connection, transaction, "sync_enabled",
                string.Equals(GetMetadata(connection, transaction, "todo_sync_enabled"), "1", StringComparison.Ordinal) ? "1" : "0");
        }
        else
        {
            SetMetadata(connection, transaction, "sync_legacy_directory", fullPath);
            SetMetadata(connection, transaction, "sync_enabled", "0");
        }
    }

    private static void ValidateNoteContent(string content)
    {
        if (content.Length > NoteMergeEngine.MaximumContentLength)
            throw new ArgumentOutOfRangeException(nameof(content), $"便签最多允许 {NoteMergeEngine.MaximumContentLength} 个字符。");
    }

    private static IReadOnlyList<NoteSyncRecord> ReadNoteRecords(
        SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sync_id, content, is_conflict, created_at, updated_at, deleted_at,
                   revision_id, version_device_id, version_counter, version_vector
            FROM notes;
            """;
        using var reader = command.ExecuteReader();
        var results = new List<NoteSyncRecord>();
        while (reader.Read())
        {
            if (!Guid.TryParse(reader.GetString(0), out var noteId) ||
                !DateTimeOffset.TryParse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt) ||
                !DateTimeOffset.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var updatedAt) ||
                !Guid.TryParse(reader.GetString(6), out var revisionId) ||
                !Guid.TryParse(reader.GetString(7), out var deviceId))
                continue;

            DateTimeOffset? deletedAt = null;
            if (!reader.IsDBNull(5) && DateTimeOffset.TryParse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedDeletedAt))
                deletedAt = parsedDeletedAt;
            Dictionary<string, long>? vector;
            try { vector = JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(9), JsonOptions); }
            catch (JsonException) { continue; }
            if (vector is null) continue;
            var record = new NoteSyncRecord(
                noteId, reader.GetString(1), reader.GetBoolean(2), createdAt, updatedAt, deletedAt,
                new NoteVersion(revisionId, deviceId, reader.GetInt64(8)), vector);
            if (NoteMergeEngine.IsValid(record)) results.Add(record);
        }
        return results;
    }

    private static void UpsertNoteRecords(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IEnumerable<NoteSyncRecord> records)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO notes
              (sync_id, content, is_conflict, created_at, updated_at, deleted_at,
               revision_id, version_device_id, version_counter, version_vector)
            VALUES
              ($id, $content, $conflict, $createdAt, $updatedAt, $deletedAt,
               $revisionId, $deviceId, $counter, $vector)
            ON CONFLICT(sync_id) DO UPDATE SET
              content = excluded.content,
              is_conflict = excluded.is_conflict,
              created_at = excluded.created_at,
              updated_at = excluded.updated_at,
              deleted_at = excluded.deleted_at,
              revision_id = excluded.revision_id,
              version_device_id = excluded.version_device_id,
              version_counter = excluded.version_counter,
              version_vector = excluded.version_vector;
            """;
        var id = command.Parameters.Add("$id", SqliteType.Text);
        var content = command.Parameters.Add("$content", SqliteType.Text);
        var conflict = command.Parameters.Add("$conflict", SqliteType.Integer);
        var createdAt = command.Parameters.Add("$createdAt", SqliteType.Text);
        var updatedAt = command.Parameters.Add("$updatedAt", SqliteType.Text);
        var deletedAt = command.Parameters.Add("$deletedAt", SqliteType.Text);
        var revisionId = command.Parameters.Add("$revisionId", SqliteType.Text);
        var deviceId = command.Parameters.Add("$deviceId", SqliteType.Text);
        var counter = command.Parameters.Add("$counter", SqliteType.Integer);
        var vector = command.Parameters.Add("$vector", SqliteType.Text);
        foreach (var record in records)
        {
            id.Value = record.NoteId.ToString("D");
            content.Value = record.Content;
            conflict.Value = record.IsConflict;
            createdAt.Value = record.CreatedAtUtc.ToString("O");
            updatedAt.Value = record.UpdatedAtUtc.ToString("O");
            deletedAt.Value = record.DeletedAtUtc is null ? DBNull.Value : record.DeletedAtUtc.Value.ToString("O");
            revisionId.Value = record.Version.RevisionId.ToString("D");
            deviceId.Value = record.Version.DeviceId.ToString("D");
            counter.Value = record.Version.Counter;
            vector.Value = JsonSerializer.Serialize(
                record.VersionVector.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
                JsonOptions);
            command.ExecuteNonQuery();
        }
    }

    private static string NoteFingerprint(IEnumerable<NoteSyncRecord> notes) =>
        JsonSerializer.Serialize(notes.OrderBy(note => note.NoteId).Select(note => new
        {
            note.NoteId,
            note.Content,
            note.IsConflict,
            note.CreatedAtUtc,
            note.UpdatedAtUtc,
            note.DeletedAtUtc,
            note.Version,
            VersionVector = note.VersionVector.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToArray()
        }), JsonOptions);

    private static NoteItem ToNoteItem(NoteSyncRecord note) => new(
        note.NoteId,
        note.Content,
        note.IsConflict,
        note.CreatedAtUtc,
        note.UpdatedAtUtc,
        note.Version,
        new Dictionary<string, long>(note.VersionVector, StringComparer.Ordinal));

    private static long GetMetadataLong(SqliteConnection connection, SqliteTransaction transaction, string key) =>
        long.TryParse(GetMetadata(connection, transaction, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;

    private static string? GetMetadata(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT value FROM sync_metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        return command.ExecuteScalar() as string;
    }

    private static void SetMetadata(SqliteConnection connection, SqliteTransaction transaction, string key, string value)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO sync_metadata (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", key);
        command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private static void DeleteMetadata(SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM sync_metadata WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);
        command.ExecuteNonQuery();
    }

    private static int InsertTodoOperation(SqliteConnection connection, SqliteTransaction transaction, TodoSyncOperation operation, bool ignoreExisting = false)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            INSERT {(ignoreExisting ? "OR IGNORE " : string.Empty)}INTO todo_sync_operations
              (operation_id, item_sync_id, device_id, counter, kind, title, is_completed, created_at)
            VALUES ($operationId, $itemId, $deviceId, $counter, $kind, $title, $completed, $createdAt);
            """;
        command.Parameters.AddWithValue("$operationId", operation.OperationId.ToString("D"));
        command.Parameters.AddWithValue("$itemId", operation.ItemId.ToString("D"));
        command.Parameters.AddWithValue("$deviceId", operation.DeviceId.ToString("D"));
        command.Parameters.AddWithValue("$counter", operation.Counter);
        command.Parameters.AddWithValue("$kind", operation.Kind);
        command.Parameters.AddWithValue("$title", (object?)operation.Title ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", operation.IsCompleted is null ? DBNull.Value : operation.IsCompleted.Value);
        command.Parameters.AddWithValue("$createdAt", operation.CreatedAtUtc.ToString("O"));
        return command.ExecuteNonQuery();
    }

    private static void RebuildTodosFromOperations(SqliteConnection connection, SqliteTransaction transaction)
    {
        var operations = new List<TodoSyncOperation>();
        using (var readCommand = connection.CreateCommand())
        {
            readCommand.Transaction = transaction;
            readCommand.CommandText = "SELECT operation_id, item_sync_id, device_id, counter, kind, title, is_completed, created_at FROM todo_sync_operations;";
            using var reader = readCommand.ExecuteReader();
            while (reader.Read())
            {
                if (!Guid.TryParse(reader.GetString(0), out var operationId) ||
                    !Guid.TryParse(reader.GetString(1), out var itemId) ||
                    !Guid.TryParse(reader.GetString(2), out var deviceId) ||
                    !DateTimeOffset.TryParse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var createdAt))
                    continue;
                operations.Add(new TodoSyncOperation(operationId, itemId, deviceId, reader.GetInt64(3), reader.GetString(4),
                    reader.IsDBNull(5) ? null : reader.GetString(5), reader.IsDBNull(6) ? null : reader.GetBoolean(6), createdAt));
            }
        }

        using (var clearCommand = connection.CreateCommand())
        {
            clearCommand.Transaction = transaction;
            clearCommand.CommandText = "DELETE FROM todos;";
            clearCommand.ExecuteNonQuery();
        }

        using var insertCommand = connection.CreateCommand();
        insertCommand.Transaction = transaction;
        insertCommand.CommandText = "INSERT INTO todos (sync_id, title, is_completed, created_at, deleted_at) VALUES ($id, $title, $completed, $createdAt, $deletedAt);";
        var idParameter = insertCommand.Parameters.Add("$id", SqliteType.Text);
        var titleParameter = insertCommand.Parameters.Add("$title", SqliteType.Text);
        var completedParameter = insertCommand.Parameters.Add("$completed", SqliteType.Integer);
        var createdAtParameter = insertCommand.Parameters.Add("$createdAt", SqliteType.Text);
        var deletedAtParameter = insertCommand.Parameters.Add("$deletedAt", SqliteType.Text);
        foreach (var item in TodoMergeEngine.Materialize(operations))
        {
            idParameter.Value = item.ItemId.ToString("D");
            titleParameter.Value = item.Title;
            completedParameter.Value = item.IsCompleted;
            createdAtParameter.Value = item.CreatedAtUtc.ToString("O");
            deletedAtParameter.Value = item.DeletedAtUtc is null ? DBNull.Value : item.DeletedAtUtc.Value.ToString("O");
            insertCommand.ExecuteNonQuery();
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Execute(string sql, params (string Name, object Value)[] parameters)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        command.ExecuteNonQuery();
    }
}

public sealed record TodoItem(Guid Id, string Title, bool IsCompleted);
public sealed record ClipboardItem(long Id, string Content);
public sealed record NoteItem(
    Guid Id,
    string Content,
    bool IsConflict,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    NoteVersion Version,
    Dictionary<string, long> VersionVector)
{
    public string Preview => string.IsNullOrEmpty(Content) ? "（空便签）" : Content;
    public string UpdatedAtText => UpdatedAtUtc.ToLocalTime().ToString("MM-dd HH:mm");
}

public sealed class LauncherItem(long id, string name, string targetPath) : System.ComponentModel.INotifyPropertyChanged
{
    public long Id { get; } = id;
    public string TargetPath { get; } = targetPath;
    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Name)));
        }
    }
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsEditing)));
        }
    }
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    private string _name = name;
    private bool _isEditing;
}

public sealed record MouseDailyTotals(double MoveDistance, long LeftClicks, long RightClicks, long MiddleClicks, long WheelUnits, long DoubleClicks)
{
    public static readonly MouseDailyTotals Empty = new(0, 0, 0, 0, 0, 0);
    public bool IsEmpty => MoveDistance == 0 && LeftClicks == 0 && RightClicks == 0 && MiddleClicks == 0 && WheelUnits == 0 && DoubleClicks == 0;
    public MouseDailyTotals Add(double distance, long left, long right, long middle, long wheel, long doubleClicks) =>
        new(MoveDistance + distance, LeftClicks + left, RightClicks + right, MiddleClicks + middle, WheelUnits + wheel, DoubleClicks + doubleClicks);
}
