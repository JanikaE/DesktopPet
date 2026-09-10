using Microsoft.Data.Sqlite;

namespace DesktopPet.Data;

public sealed class DesktopPetRepository(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
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
    }

    public IReadOnlyList<TodoItem> GetTodos()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, is_completed FROM todos ORDER BY is_completed, id DESC;";
        using var reader = command.ExecuteReader();
        var results = new List<TodoItem>();
        while (reader.Read()) results.Add(new TodoItem(reader.GetInt64(0), reader.GetString(1), reader.GetBoolean(2)));
        return results;
    }

    public TodoItem AddTodo(string title)
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO todos (title, is_completed, created_at) VALUES ($title, 0, $createdAt); SELECT last_insert_rowid();";
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$createdAt", DateTimeOffset.UtcNow.ToString("O"));
        return new TodoItem((long)command.ExecuteScalar()!, title, false);
    }

    public void SetTodoCompleted(long id, bool isCompleted) => Execute("UPDATE todos SET is_completed = $completed WHERE id = $id;", ("$id", id), ("$completed", isCompleted));
    public void DeleteTodo(long id) => Execute("DELETE FROM todos WHERE id = $id;", ("$id", id));

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

public sealed record TodoItem(long Id, string Title, bool IsCompleted);
public sealed record ClipboardItem(long Id, string Content);
public sealed record LauncherItem(long Id, string Name, string TargetPath);
