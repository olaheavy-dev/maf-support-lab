using Microsoft.Data.Sqlite;
using Support.Api.Common.Application;

namespace Support.Api.Common.Infrastructure.Persistence;

/// <summary>
/// The local durable store (L11): one SQLite file per backend and per run (<c>Storage:Path</c>), so the .NET backend,
/// the Python backend, and each evaluation run never share mutable state. Writes inside
/// <see cref="AtomicallyAsync{T}"/> share one IMMEDIATE transaction and commit together or not at all; other calls use
/// their own short-lived connection. Single process only: there is no cross-process locking beyond SQLite's own.
/// </summary>
public sealed class SupportDatabase : IUnitOfWork
{
    private static readonly AsyncLocal<Ambient?> Current = new();
    private readonly string connectionString;

    public SupportDatabase(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        connectionString = new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString();
        CreateSchema();
    }

    public string Path { get; }

    /// <summary>Runs a command on the ambient transaction if there is one, otherwise on a new connection.</summary>
    public T Run<T>(Func<SqliteCommand, T> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (Current.Value is { } ambient)
        {
            using var command = ambient.Connection.CreateCommand();
            command.Transaction = ambient.Transaction;
            return work(command);
        }

        using var connection = Open();
        using var own = connection.CreateCommand();
        return work(own);
    }

    /// <summary>Runs a statement and returns the number of rows it changed.</summary>
    public int Execute(string sql, params (string Name, object? Value)[] parameters) =>
        Run(command =>
        {
            command.CommandText = sql;
            Bind(command, parameters);
            return command.ExecuteNonQuery();
        });

    public IReadOnlyList<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] parameters) =>
        Run(command =>
        {
            command.CommandText = sql;
            Bind(command, parameters);
            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read())
            {
                rows.Add(map(reader));
            }

            return (IReadOnlyList<T>)rows;
        });

    /// <summary>Takes and increments a named counter (refund, payment, ticket numbers) inside the current transaction.</summary>
    public long NextValue(string name, long start)
    {
        Execute("INSERT INTO counters(name, value) VALUES (@n, @s) ON CONFLICT(name) DO NOTHING", ("@n", name), ("@s", start));
        Execute("UPDATE counters SET value = value + 1 WHERE name = @n", ("@n", name));
        return Query("SELECT value - 1 FROM counters WHERE name = @n", reader => reader.GetInt64(0), ("@n", name))[0];
    }

    /// <summary>Synchronous form of <see cref="AtomicallyAsync{T}"/>, for a few writes that must land together.</summary>
    public T Atomically<T>(Func<T> work) =>
        AtomicallyAsync(() => Task.FromResult(work()), CancellationToken.None).GetAwaiter().GetResult();

    public async Task<T> AtomicallyAsync<T>(Func<Task<T>> work, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(work);
        cancellationToken.ThrowIfCancellationRequested();
        if (Current.Value is not null)
        {
            return await work().ConfigureAwait(false); // already inside one: join it
        }

        using var connection = Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Current.Value = new Ambient(connection, transaction);
        try
        {
            var result = await work().ConfigureAwait(false);
            transaction.Commit(); // once the work has finished, commit even if the caller has since cancelled
            return result;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
        finally
        {
            Current.Value = null;
        }
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode = WAL; PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void Bind(SqliteCommand command, (string Name, object? Value)[] parameters)
    {
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }
    }

    private void CreateSchema() => Execute("""
        CREATE TABLE IF NOT EXISTS counters (name TEXT PRIMARY KEY, value INTEGER NOT NULL);
        CREATE TABLE IF NOT EXISTS changed_orders (order_id TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS payments (payment_id TEXT PRIMARY KEY, customer_id TEXT NOT NULL, json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS tickets (ticket_id TEXT PRIMARY KEY, customer_id TEXT NOT NULL, json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS conversations (conversation_id TEXT PRIMARY KEY, owner_customer_id TEXT NOT NULL, created_at TEXT NOT NULL, context_json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS turns (turn_id TEXT PRIMARY KEY, conversation_id TEXT NOT NULL REFERENCES conversations(conversation_id), client_key TEXT, message TEXT, json TEXT NOT NULL);
        CREATE UNIQUE INDEX IF NOT EXISTS turns_client_key ON turns(conversation_id, client_key) WHERE client_key IS NOT NULL;
        CREATE TABLE IF NOT EXISTS operations (operation_id TEXT PRIMARY KEY, approval_id TEXT NOT NULL UNIQUE, conversation_id TEXT NOT NULL, owner_customer_id TEXT NOT NULL, status TEXT NOT NULL, json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS native_approval_requests (operation_id TEXT PRIMARY KEY, json TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS agent_sessions (conversation_id TEXT NOT NULL, agent TEXT NOT NULL, json TEXT NOT NULL, PRIMARY KEY (conversation_id, agent));
        """);

    private sealed record Ambient(SqliteConnection Connection, SqliteTransaction Transaction);
}
