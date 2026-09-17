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

public sealed record MouseDailyTotals(double MoveDistance, long LeftClicks, long RightClicks, long MiddleClicks, long WheelUnits, long DoubleClicks)
{
    public static readonly MouseDailyTotals Empty = new(0, 0, 0, 0, 0, 0);
    public bool IsEmpty => MoveDistance == 0 && LeftClicks == 0 && RightClicks == 0 && MiddleClicks == 0 && WheelUnits == 0 && DoubleClicks == 0;
    public MouseDailyTotals Add(double distance, long left, long right, long middle, long wheel, long doubleClicks) =>
        new(MoveDistance + distance, LeftClicks + left, RightClicks + right, MiddleClicks + middle, WheelUnits + wheel, DoubleClicks + doubleClicks);
}
