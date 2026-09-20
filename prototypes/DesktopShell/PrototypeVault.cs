using System.Text.Json;
using System.Diagnostics;

using Microsoft.Data.Sqlite;

namespace DesktopShell;

public enum PersistenceStage
{
    BeforeCommit,
    BackupStaged
}

public sealed class VaultAuthenticationException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed class PrototypeVault : IDisposable
{
    private const string StateKey = "prototype-state";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);
    private readonly SqliteConnection _connection;
    private readonly string _password;

    static PrototypeVault()
    {
        SQLitePCL.Batteries_V2.Init();
    }

    private PrototypeVault(SqliteConnection connection, string password)
    {
        _connection = connection;
        _password = password;
    }

    public static PrototypeVault Create(string databasePath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        SqliteConnection connection = Open(databasePath, password, SqliteOpenMode.ReadWriteCreate);
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "CREATE TABLE IF NOT EXISTS prototype_state (key TEXT PRIMARY KEY, value TEXT NOT NULL);";
            command.ExecuteNonQuery();
            return new PrototypeVault(connection, password);
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public static PrototypeVault Unlock(string databasePath, string password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(password);
        SqliteConnection connection;
        try
        {
            connection = Open(databasePath, password, SqliteOpenMode.ReadWrite);
            Validate(connection);
            return new PrototypeVault(connection, password);
        }
        catch (SqliteException exception)
        {
            throw new VaultAuthenticationException("The password is incorrect or the encrypted vault is damaged.", exception);
        }
    }

    public static void Restore(string backupPath, string databasePath, string password)
    {
        string candidatePath = databasePath + ".restore-partial";
        File.Delete(candidatePath);
        try
        {
            File.Copy(backupPath, candidatePath);
            using (PrototypeVault candidate = Unlock(candidatePath, password))
            {
                _ = candidate.Load();
            }

            File.Move(candidatePath, databasePath, true);
        }
        catch (VaultAuthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new VaultAuthenticationException("The backup could not be validated and was not restored.", exception);
        }
        finally
        {
            File.Delete(candidatePath);
        }
    }

    public PrototypeState Load()
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT value FROM prototype_state WHERE key = $key;";
        command.Parameters.AddWithValue("$key", StateKey);
        string? json = command.ExecuteScalar() as string;
        return json is null
            ? throw new InvalidOperationException("The prototype vault does not contain imported state.")
            : JsonSerializer.Deserialize<PrototypeState>(json, JsonOptions)
                ?? throw new InvalidDataException("The stored prototype state is invalid.");
    }

    public void Save(PrototypeState state) => Save(state, CancellationToken.None, null);

    public void Save(PrototypeState state, CancellationToken cancellationToken, IProgress<PersistenceStage>? progress)
    {
        ArgumentNullException.ThrowIfNull(state);
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteTransaction transaction = _connection.BeginTransaction();
        using SqliteCommand command = _connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO prototype_state (key, value) VALUES ($key, $value) ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("$key", StateKey);
        command.Parameters.AddWithValue("$value", JsonSerializer.Serialize(state, JsonOptions));
        command.ExecuteNonQuery();
        progress?.Report(PersistenceStage.BeforeCommit);
        CrashIfRequested("save-before-commit");
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public void CreateBackup(string backupPath) => CreateBackup(backupPath, CancellationToken.None, null);

    public void CreateBackup(string backupPath, CancellationToken cancellationToken, IProgress<PersistenceStage>? progress)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string partialPath = backupPath + ".partial";
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(backupPath))!);
        File.Delete(partialPath);
        try
        {
            using SqliteConnection destination = Open(partialPath, _password, SqliteOpenMode.ReadWriteCreate);
            _connection.BackupDatabase(destination);
            Validate(destination);
            progress?.Report(PersistenceStage.BackupStaged);
            CrashIfRequested("backup-staged");
            cancellationToken.ThrowIfCancellationRequested();
            destination.Close();
            File.Move(partialPath, backupPath, true);
        }
        finally
        {
            File.Delete(partialPath);
        }
    }

    public void Dispose() => _connection.Dispose();

    private static SqliteConnection Open(string path, string password, SqliteOpenMode mode)
    {
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = path,
            Mode = mode,
            Pooling = false,
            Password = password
        };
        SqliteConnection connection = new(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static void Validate(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM prototype_state; PRAGMA integrity_check;";
        using SqliteDataReader reader = command.ExecuteReader();
        _ = reader.Read();
        _ = reader.NextResult();
        if (!reader.Read() || !string.Equals(reader.GetString(0), "ok", StringComparison.Ordinal))
        {
            throw new SqliteException("Encrypted vault integrity check failed.", 11);
        }
    }

    private static void CrashIfRequested(string point)
    {
        if (string.Equals(Environment.GetEnvironmentVariable("DOT_FINANCE_CRASH_POINT"), point, StringComparison.Ordinal))
        {
            string markerPath = Environment.GetEnvironmentVariable("DOT_FINANCE_CRASH_MARKER")
                ?? throw new InvalidOperationException("DOT_FINANCE_CRASH_MARKER is required when injecting an abrupt interruption.");
            File.WriteAllText(markerPath, point);
            Process.GetCurrentProcess().Kill();
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
