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
public sealed record LauncherItem(long Id, string Name, string TargetPath);
