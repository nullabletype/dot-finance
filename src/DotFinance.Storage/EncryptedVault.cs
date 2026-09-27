using System.Diagnostics;
using System.Globalization;

using DotFinance.Application;
using DotFinance.Domain;

using Microsoft.Data.Sqlite;

namespace DotFinance.Storage;

public sealed class EncryptedVault : IDisposable, IReconciliationVault
{
    public const int CurrentSchemaVersion = 1;
    public const int KdfIterations = 600_000;
    private const string GenericAccessMessage = "The password is incorrect or the encrypted vault is damaged.";
    private readonly SqliteConnection _connection;
    private readonly char[] _password;
    private readonly IVaultFileSystem _fileSystem;
    private bool _disposed;

    static EncryptedVault() => SQLitePCL.Batteries_V2.Init();

    private EncryptedVault(SqliteConnection connection, ReadOnlySpan<char> password, IVaultFileSystem fileSystem)
    {
        _connection = connection;
        _password = password.ToArray();
        _fileSystem = fileSystem;
    }

    public static EncryptedVault Create(string databasePath, ReadOnlySpan<char> password)
        => Create(databasePath, password, VaultDependencies.Default);

    public static EncryptedVault Unlock(string databasePath, ReadOnlySpan<char> password)
        => Unlock(databasePath, password, VaultDependencies.Default);

    public static void Restore(string backupPath, string databasePath, ReadOnlySpan<char> password,
        IProgress<VaultStage>? progress = null, CancellationToken cancellationToken = default)
        => Restore(backupPath, databasePath, password, progress, VaultDependencies.Default, cancellationToken);

    public static void DiscardInterruptedRestore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        DeleteManagedSet(PhysicalVaultFileSystem.Instance, Path.GetFullPath(databasePath) + ".restore-partial");
    }

    internal static EncryptedVault Create(string databasePath, ReadOnlySpan<char> password, VaultDependencies dependencies)
    {
        ValidatePathAndPassword(databasePath, password);
        string fullPath = Path.GetFullPath(databasePath);
        if (dependencies.FileSystem.FileExists(fullPath))
            throw new VaultStorageException("A vault already exists at the selected location.");
        dependencies.FileSystem.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        SqliteConnection? connection = null;
        try
        {
            connection = OpenConnection(fullPath, password, SqliteOpenMode.ReadWriteCreate);
            ApplyMigrations(connection, dependencies.BeforeMigrationCommit);
            ConfigureDurability(connection);
            VerifySecurityProfile(connection);
            return new EncryptedVault(connection, password, dependencies.FileSystem);
        }
        catch
        {
            connection?.Dispose();
            TryDeleteManagedFile(dependencies.FileSystem, fullPath);
            throw;
        }
    }

    internal static EncryptedVault Unlock(string databasePath, ReadOnlySpan<char> password, VaultDependencies dependencies)
    {
        ValidatePathAndPassword(databasePath, password);
        string fullPath = Path.GetFullPath(databasePath);
        if (!dependencies.FileSystem.FileExists(fullPath)) throw new VaultAccessException(GenericAccessMessage);
        SqliteConnection? connection = null;
        try
        {
            connection = OpenConnection(fullPath, password, SqliteOpenMode.ReadWrite);
            ValidateIntegrity(connection);
            ApplyMigrations(connection, dependencies.BeforeMigrationCommit);
            ConfigureDurability(connection);
            VerifySecurityProfile(connection);
            return new EncryptedVault(connection, password, dependencies.FileSystem);
        }
        catch (VaultMigrationException)
        {
            connection?.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidDataException)
        {
            connection?.Dispose();
            throw new VaultAccessException(GenericAccessMessage, exception);
        }
    }

    internal static void Restore(string backupPath, string databasePath, ReadOnlySpan<char> password,
        IProgress<VaultStage>? progress, VaultDependencies dependencies, CancellationToken cancellationToken)
    {
        ValidatePathAndPassword(backupPath, password);
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        cancellationToken.ThrowIfCancellationRequested();
        string source = Path.GetFullPath(backupPath);
        string destination = Path.GetFullPath(databasePath);
        if (string.Equals(source, destination, StringComparison.Ordinal))
            throw new ArgumentException("The backup and working vault must use different files.");
        bool damagedDestination = false;
        if (dependencies.FileSystem.FileExists(destination))
        {
            try
            {
                using EncryptedVault current = Unlock(destination, password, dependencies);
                _ = current.Load();
            }
            catch (VaultAccessException)
            {
                damagedDestination = true;
            }
        }

        dependencies.FileSystem.CreateDirectory(Path.GetDirectoryName(destination)!);
        string candidate = destination + ".restore-partial";
        DeleteManagedSet(dependencies.FileSystem, candidate);
        try
        {
            dependencies.FileSystem.CopyFile(source, candidate);
            using (EncryptedVault staged = Unlock(candidate, password, dependencies)) _ = staged.Load();
            progress?.Report(VaultStage.RestoreStaged);
            cancellationToken.ThrowIfCancellationRequested();
            if (damagedDestination) DeleteSidecars(dependencies.FileSystem, destination);
            dependencies.FileSystem.ReplaceFile(candidate, destination);
        }
        catch (VaultAccessException)
        {
            throw;
        }
        catch (VaultMigrationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new VaultStorageException("The validated backup could not replace the working vault.", exception);
        }
        finally
        {
            DeleteManagedSet(dependencies.FileSystem, candidate);
        }
    }

    public ReconciliationBook Load()
    {
        ThrowIfDisposed();
        try
        {
            return VaultStateStore.Read(_connection);
        }
        catch (Exception exception) when (exception is SqliteException or System.Text.Json.JsonException or InvalidDataException or ArgumentException)
        {
            throw new VaultAccessException(GenericAccessMessage, exception);
        }
    }

    public void Save(ReconciliationBook book, IProgress<VaultStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ThrowIfDisposed();
        cancellationToken.ThrowIfCancellationRequested();
        using SqliteTransaction transaction = _connection.BeginTransaction();
        VaultStateStore.Write(_connection, transaction, book.ExportState());
        progress?.Report(VaultStage.BeforeSaveCommit);
        cancellationToken.ThrowIfCancellationRequested();
        transaction.Commit();
    }

    public void CreateBackup(string backupPath, IProgress<VaultStage>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(backupPath);
        cancellationToken.ThrowIfCancellationRequested();
        string destination = Path.GetFullPath(backupPath);
        _fileSystem.CreateDirectory(Path.GetDirectoryName(destination)!);
        string partial = destination + ".partial";
        DeleteManagedSet(_fileSystem, partial);
        try
        {
            using (SqliteConnection staged = OpenConnection(partial, _password, SqliteOpenMode.ReadWriteCreate))
            {
                _connection.BackupDatabase(staged);
                ValidateIntegrity(staged);
                VerifySecurityProfile(staged);
                VerifyPersistedState(staged);
            }
            progress?.Report(VaultStage.BackupStaged);
            cancellationToken.ThrowIfCancellationRequested();
            _fileSystem.ReplaceFile(partial, destination);
        }
        catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException or ArgumentException)
        {
            throw new VaultStorageException("The encrypted backup could not be validated.", exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new VaultStorageException("The encrypted backup could not be replaced.", exception);
        }
        finally
        {
            DeleteManagedSet(_fileSystem, partial);
        }
    }

    public VaultSecurityProfile SecurityProfile()
    {
        ThrowIfDisposed();
        return ReadSecurityProfile(_connection);
    }

    void IReconciliationVault.Save(ReconciliationBook book, CancellationToken cancellationToken)
        => Save(book, cancellationToken: cancellationToken);

    void IReconciliationVault.CreateBackup(string backupPath, CancellationToken cancellationToken)
        => CreateBackup(backupPath, cancellationToken: cancellationToken);

    public void Dispose()
    {
        if (_disposed) return;
        _connection.Dispose();
        Array.Clear(_password);
        _disposed = true;
    }

    private static SqliteConnection OpenConnection(string path, ReadOnlySpan<char> password, SqliteOpenMode mode)
    {
        string uri = new Uri(Path.GetFullPath(path)).AbsoluteUri
            + $"?cipher=chacha20&legacy=0&kdf_iter={KdfIterations.ToString(CultureInfo.InvariantCulture)}&plaintext_header_size=0&hmac_check=1";
        SqliteConnectionStringBuilder builder = new()
        {
            DataSource = uri,
            Mode = mode,
            Pooling = false,
            Password = new string(password),
            ForeignKeys = true,
            DefaultTimeout = 5
        };
        SqliteConnection connection = new(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    private static void ApplyMigrations(SqliteConnection connection, Action<int>? beforeCommit)
    {
        int version = ScalarInt(connection, "PRAGMA user_version;");
        if (version > CurrentSchemaVersion)
            throw new VaultMigrationException("The encrypted vault was created by a newer unsupported schema version.");
        if (version == CurrentSchemaVersion) return;
        using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                CREATE TABLE schema_history (
                    version INTEGER PRIMARY KEY,
                    description TEXT NOT NULL
                ) STRICT;
                CREATE TABLE book_state (
                    id INTEGER PRIMARY KEY CHECK (id = 1),
                    state_version INTEGER NOT NULL,
                    adjustment_sequence INTEGER NOT NULL,
                    card_account_id TEXT NOT NULL,
                    current BLOB
                ) STRICT;
                CREATE TABLE funding_accounts (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL, role INTEGER, protected_minimum_minor INTEGER, priority INTEGER) STRICT;
                CREATE TABLE category_rules (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL, funding_source INTEGER) STRICT;
                CREATE TABLE subcategory_rules (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL, category TEXT, subcategory TEXT, funding_source INTEGER) STRICT;
                CREATE TABLE reporting_categories (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL, category TEXT, subcategory TEXT, funding_source INTEGER) STRICT;
                CREATE TABLE imported_transactions (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL, transaction_date TEXT, amount_decimal TEXT, currency TEXT, category TEXT, subcategory TEXT, transaction_kind INTEGER) STRICT;
                CREATE TABLE settled_transaction_ids (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL) STRICT;
                CREATE TABLE effective_transactions (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL, funding_source INTEGER, cost_minor INTEGER, card_cost_minor INTEGER, eligible INTEGER) STRICT;
                CREATE TABLE pending_corrections (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL) STRICT;
                CREATE TABLE import_conflicts (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL) STRICT;
                CREATE TABLE funding_overrides (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL) STRICT;
                CREATE TABLE reconciliation_history (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL, reconciliation_date TEXT, locked INTEGER, card_paid INTEGER) STRICT;
                CREATE TABLE accepted_corrections (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL, reconciliation_id TEXT, sequence INTEGER, transaction_id TEXT) STRICT;
                CREATE TABLE used_reconciliation_ids (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL) STRICT;
                CREATE TABLE current_adjustments (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL) STRICT;
                CREATE TABLE active_transaction_ids (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL) STRICT;
                CREATE TABLE settled_baseline (item_key TEXT PRIMARY KEY, sort_index INTEGER NOT NULL, payload BLOB NOT NULL) STRICT;
                CREATE INDEX imported_transactions_date_idx ON imported_transactions(transaction_date);
                CREATE INDEX imported_transactions_category_idx ON imported_transactions(category, subcategory);
                CREATE INDEX effective_transactions_source_idx ON effective_transactions(funding_source);
                CREATE INDEX reconciliation_history_date_idx ON reconciliation_history(reconciliation_date);
                CREATE INDEX accepted_corrections_transaction_idx ON accepted_corrections(transaction_id);
                INSERT INTO schema_history (version, description) VALUES (1, 'Relational encrypted reconciliation state');
                PRAGMA user_version = 1;
                """;
            command.ExecuteNonQuery();
            beforeCommit?.Invoke(1);
            transaction.Commit();
        }
        catch (Exception exception) when (exception is not VaultMigrationException)
        {
            throw new VaultMigrationException("The encrypted vault schema could not be migrated.", exception);
        }
    }

    private static void ConfigureDurability(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=DELETE; PRAGMA synchronous=FULL; PRAGMA secure_delete=ON; PRAGMA memory_security=ON;";
        command.ExecuteNonQuery();
    }

    private static void ValidateIntegrity(SqliteConnection connection)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check;";
        if (!string.Equals(command.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new InvalidDataException("Encrypted vault integrity check failed.");
    }

    private static void VerifyPersistedState(SqliteConnection connection)
    {
        _ = VaultStateStore.Read(connection);
    }

    private static VaultSecurityProfile ReadSecurityProfile(SqliteConnection connection)
        => new(ScalarText(connection, "PRAGMA cipher;"), ScalarInt(connection, "PRAGMA kdf_iter;"),
            ScalarInt(connection, "PRAGMA legacy;") != 0, ScalarInt(connection, "PRAGMA plaintext_header_size;"),
            ScalarInt(connection, "PRAGMA hmac_check;") != 0, ScalarText(connection, "SELECT sqlite3mc_version();"),
            ScalarText(connection, "SELECT sqlite_version();"));

    private static void VerifySecurityProfile(SqliteConnection connection)
    {
        VaultSecurityProfile profile = ReadSecurityProfile(connection);
        if (!string.Equals(profile.Cipher, "chacha20", StringComparison.OrdinalIgnoreCase)
            || profile.KdfIterations != KdfIterations || profile.LegacyMode || profile.PlaintextHeaderBytes != 0
            || !profile.HmacCheck || !profile.NativeVersion.Contains("2.4.0", StringComparison.Ordinal)
            || !string.Equals(profile.SQLiteVersion, "3.53.4", StringComparison.Ordinal))
            throw new InvalidDataException("The encrypted vault security profile differs from the production policy.");
    }

    private static int ScalarInt(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static string ScalarText(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void ValidatePathAndPassword(string path, ReadOnlySpan<char> password)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (password.IsEmpty || password.Trim().IsEmpty) throw new ArgumentException("A password is required.", nameof(password));
    }

    private static void DeleteManagedSet(IVaultFileSystem fileSystem, string path)
    {
        TryDeleteManagedFile(fileSystem, path);
        DeleteSidecars(fileSystem, path);
    }

    private static void DeleteSidecars(IVaultFileSystem fileSystem, string path)
    {
        TryDeleteManagedFile(fileSystem, path + "-journal");
        TryDeleteManagedFile(fileSystem, path + "-wal");
        TryDeleteManagedFile(fileSystem, path + "-shm");
    }

    private static void TryDeleteManagedFile(IVaultFileSystem fileSystem, string path)
    {
        try { fileSystem.DeleteFile(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Debug.WriteLine(exception.GetType().Name); }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}