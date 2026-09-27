using System.Text;

using DotFinance.Application;
using DotFinance.Domain;

using Microsoft.Data.Sqlite;

using Xunit;

namespace DotFinance.Storage.Tests;

public sealed class EncryptedVaultTests : IDisposable
{
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"dot-finance-storage-{Guid.NewGuid():N}");

    [Fact]
    public void CompleteDomainStateRoundTripsAndCanContinue()
    {
        ReconciliationBook expected = StorageFixtures.RepresentativeBook();
        string database = FilePath("working.db");
        using (EncryptedVault vault = EncryptedVault.Create(database, StorageFixtures.Password))
            vault.Save(expected, cancellationToken: TestContext.Current.CancellationToken);

        using EncryptedVault reopened = EncryptedVault.Unlock(database, StorageFixtures.Password);
        ReconciliationBook actual = reopened.Load();
        Assert.Equal(StorageFixtures.CanonicalState(expected), StorageFixtures.CanonicalState(actual));

        actual.RejectCorrection("baseline-2");
        ImportedTransaction selected = actual.ImportConflicts.Single().Candidates[0];
        actual.ResolveImportConflict("duplicate-1", selected);
        if (!actual.Current!.DiscrepancyAcknowledged) actual.AcknowledgeDiscrepancy();
        foreach (FundingTransfer transfer in actual.Current!.Transfers.Where(x => !x.Acknowledged).ToArray())
            actual.AcknowledgeTransfer(transfer.AccountId);
        actual.AcknowledgeCardPaid();
        Assert.Equal(2, actual.History.Length);
        Assert.All(actual.History.SelectMany(x => x.Transactions), row => Assert.True(actual.IsSettled(row.Transaction.Id)));
    }

    [Fact]
    public void NewVaultRecordsSchemaAndConfiguredCipher()
    {
        using EncryptedVault vault = EncryptedVault.Create(FilePath("working.db"), StorageFixtures.Password);
        VaultSecurityProfile profile = vault.SecurityProfile();
        Assert.Equal("chacha20", profile.Cipher, ignoreCase: true);
        Assert.Equal(600_000, profile.KdfIterations);
        Assert.False(profile.LegacyMode);
        Assert.Equal(0, profile.PlaintextHeaderBytes);
        Assert.True(profile.HmacCheck);
        Assert.Contains("2.4.0", profile.NativeVersion, StringComparison.Ordinal);
        Assert.Equal("3.53.4", profile.SQLiteVersion);

        using SqliteConnection connection = OpenRaw(FilePath("working.db"));
        Assert.Equal(1L, Scalar(connection, "PRAGMA user_version;"));
        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM schema_history WHERE version = 1;"));
        Assert.Equal(1L, Scalar(connection, "SELECT count(*) FROM sqlite_schema WHERE name = 'book_state' AND type = 'table';"));
        Assert.Equal(16L, Scalar(connection, "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name IN ('funding_accounts', 'category_rules', 'subcategory_rules', 'reporting_categories', 'imported_transactions', 'settled_transaction_ids', 'effective_transactions', 'pending_corrections', 'import_conflicts', 'funding_overrides', 'reconciliation_history', 'accepted_corrections', 'used_reconciliation_ids', 'current_adjustments', 'active_transaction_ids', 'settled_baseline');"));
        Assert.Equal(5L, Scalar(connection, "SELECT count(*) FROM sqlite_schema WHERE type = 'index' AND name LIKE '%_idx';"));
    }

    [Fact]
    public void CompletionSavesWorkingStateAndRefreshesConfiguredBackup()
    {
        string database = FilePath("working.db");
        string backup = FilePath("automatic.dotfinance-backup");
        ReconciliationBook book = ReadyToCompleteBook();

        using EncryptedVault vault = EncryptedVault.Create(database, StorageFixtures.Password);
        book = new ReconciliationCompletion(vault, backup).Complete(book, TestContext.Current.CancellationToken);

        Assert.Null(book.Current);
        Assert.True(File.Exists(backup));
        Assert.Equal(StorageFixtures.CanonicalState(book), ReadState(database));
        string restored = FilePath("restored.db");
        EncryptedVault.Restore(backup, restored, StorageFixtures.Password,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(StorageFixtures.CanonicalState(book), ReadState(restored));

        using SqliteConnection relational = OpenRaw(database);
        Assert.Equal(1L, Scalar(relational, "SELECT count(*) FROM imported_transactions WHERE item_key = 'baseline-1' AND transaction_date = '2026-08-01' AND category = 'Personal';"));
        Assert.Equal(1L, Scalar(relational, "SELECT count(*) FROM effective_transactions WHERE item_key = 'active-1' AND funding_source = 2 AND eligible = 1;"));
        Assert.Equal(2L, Scalar(relational, "SELECT count(*) FROM reconciliation_history WHERE locked = 1 AND card_paid = 1;"));
        Assert.Equal(1L, Scalar(relational, "SELECT count(*) FROM accepted_corrections WHERE transaction_id = 'baseline-1';"));
    }

    [Fact]
    public void CompletionFailureKeepsCallerDraftAndExposesBackupRetry()
    {
        ReconciliationBook draft = ReadyToCompleteBook();
        string expectedDraft = StorageFixtures.CanonicalState(draft);
        RecordingVault saveFailure = new() { SaveError = new IOException("Synthetic save fault") };
        ReconciliationCompletion failedSave = new(saveFailure, FilePath("automatic.backup"));

        Assert.Throws<IOException>(() => failedSave.Complete(draft, TestContext.Current.CancellationToken));
        Assert.Equal(expectedDraft, StorageFixtures.CanonicalState(draft));
        Assert.NotNull(draft.Current);
        Assert.Null(saveFailure.Saved);

        RecordingVault backupFailure = new() { BackupError = new IOException("Synthetic backup fault") };
        ReconciliationCompletion completion = new(backupFailure, FilePath("automatic.backup"));
        AutomaticBackupException exception = Assert.Throws<AutomaticBackupException>(() =>
            completion.Complete(draft, TestContext.Current.CancellationToken));
        Assert.Equal(expectedDraft, StorageFixtures.CanonicalState(draft));
        Assert.Null(exception.CompletedBook.Current);
        Assert.Equal(StorageFixtures.CanonicalState(exception.CompletedBook),
            StorageFixtures.CanonicalState(backupFailure.Saved!));

        backupFailure.BackupError = null;
        completion.RetryAutomaticBackup(TestContext.Current.CancellationToken);
        Assert.Equal(2, backupFailure.BackupAttempts);
    }

    [Fact]
    public void CancelledCompletionDoesNotMutateOrPersistDraft()
    {
        ReconciliationBook draft = ReadyToCompleteBook();
        string expected = StorageFixtures.CanonicalState(draft);
        RecordingVault vault = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new ReconciliationCompletion(vault, FilePath("automatic.backup")).Complete(draft, cancellation.Token));
        Assert.Equal(expected, StorageFixtures.CanonicalState(draft));
        Assert.Null(vault.Saved);
        Assert.Equal(0, vault.BackupAttempts);
    }

    [Fact]
    public void WrongPasswordAndTamperedBackupDoNotReplaceWorkingVault()
    {
        string database = FilePath("working.db");
        string backup = FilePath("portable.dotfinance-backup");
        ReconciliationBook expected = StorageFixtures.RepresentativeBook();
        using (EncryptedVault vault = EncryptedVault.Create(database, StorageFixtures.Password))
        {
            vault.Save(expected, cancellationToken: TestContext.Current.CancellationToken);
            vault.CreateBackup(backup, cancellationToken: TestContext.Current.CancellationToken);
        }

        VaultAccessException wrong = Assert.Throws<VaultAccessException>(() =>
            EncryptedVault.Restore(backup, database, "wrong-synthetic-password", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal("The password is incorrect or the encrypted vault is damaged.", wrong.Message);
        Assert.Equal(StorageFixtures.CanonicalState(expected), ReadState(database));

        byte[] damaged = File.ReadAllBytes(backup);
        damaged[damaged.Length / 2] ^= 0x5A;
        File.WriteAllBytes(backup, damaged);
        VaultAccessException tampered = Assert.Throws<VaultAccessException>(() =>
            EncryptedVault.Restore(backup, database, StorageFixtures.Password, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(wrong.Message, tampered.Message);
        Assert.Equal(StorageFixtures.CanonicalState(expected), ReadState(database));
    }

    [Fact]
    public void ValidBackupRecoversDamagedWorkingVault()
    {
        string database = FilePath("working.db");
        string backup = FilePath("portable.dotfinance-backup");
        ReconciliationBook expected = StorageFixtures.RepresentativeBook();
        using (EncryptedVault vault = EncryptedVault.Create(database, StorageFixtures.Password))
        {
            vault.Save(expected, cancellationToken: TestContext.Current.CancellationToken);
            vault.CreateBackup(backup, cancellationToken: TestContext.Current.CancellationToken);
        }

        byte[] damaged = File.ReadAllBytes(database);
        damaged[damaged.Length / 2] ^= 0x5A;
        File.WriteAllBytes(database, damaged);
        EncryptedVault.Restore(backup, database, StorageFixtures.Password,
            cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(StorageFixtures.CanonicalState(expected), ReadState(database));
    }

    [Fact]
    public void CancelledSaveBackupAndRestorePreserveLastValidState()
    {
        string database = FilePath("working.db");
        string backup = FilePath("portable.dotfinance-backup");
        ReconciliationBook expected = StorageFixtures.RepresentativeBook();
        using EncryptedVault vault = EncryptedVault.Create(database, StorageFixtures.Password);
        vault.Save(expected, cancellationToken: TestContext.Current.CancellationToken);
        vault.CreateBackup(backup, cancellationToken: TestContext.Current.CancellationToken);
        byte[] originalBackup = File.ReadAllBytes(backup);

        ReconciliationBook changed = StorageFixtures.RepresentativeBook();
        changed.Import([StorageFixtures.Transaction("later-1", -1m, "Personal")]);
        using CancellationTokenSource saveCancellation = new();
        Assert.Throws<OperationCanceledException>(() => vault.Save(changed,
            new InlineProgress(stage => { if (stage == VaultStage.BeforeSaveCommit) saveCancellation.Cancel(); }), saveCancellation.Token));
        Assert.Equal(StorageFixtures.CanonicalState(expected), StorageFixtures.CanonicalState(vault.Load()));

        using CancellationTokenSource backupCancellation = new();
        Assert.Throws<OperationCanceledException>(() => vault.CreateBackup(backup,
            new InlineProgress(stage => { if (stage == VaultStage.BackupStaged) backupCancellation.Cancel(); }), backupCancellation.Token));
        Assert.Equal(originalBackup, File.ReadAllBytes(backup));
        Assert.False(File.Exists(backup + ".partial"));

        using CancellationTokenSource restoreCancellation = new();
        Assert.Throws<OperationCanceledException>(() => EncryptedVault.Restore(backup, database, StorageFixtures.Password,
            new InlineProgress(stage => { if (stage == VaultStage.RestoreStaged) restoreCancellation.Cancel(); }), restoreCancellation.Token));
        Assert.Equal(StorageFixtures.CanonicalState(expected), StorageFixtures.CanonicalState(vault.Load()));
        Assert.False(File.Exists(database + ".restore-partial"));
    }

    [Fact]
    public void ReplacementFailuresPreserveWorkingVaultAndPreviousBackup()
    {
        string database = FilePath("working.db");
        string backup = FilePath("portable.dotfinance-backup");
        ReconciliationBook expected = StorageFixtures.RepresentativeBook();
        using (EncryptedVault vault = EncryptedVault.Create(database, StorageFixtures.Password))
        {
            vault.Save(expected, cancellationToken: TestContext.Current.CancellationToken);
            vault.CreateBackup(backup, cancellationToken: TestContext.Current.CancellationToken);
        }
        byte[] originalBackup = File.ReadAllBytes(backup);
        VaultDependencies failing = new(new ReplaceFailingFileSystem());

        using (EncryptedVault vault = EncryptedVault.Unlock(database, StorageFixtures.Password, failing))
            Assert.Throws<VaultStorageException>(() => vault.CreateBackup(backup, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(originalBackup, File.ReadAllBytes(backup));

        Assert.Throws<VaultStorageException>(() => EncryptedVault.Restore(
            backup, database, StorageFixtures.Password, null, failing, TestContext.Current.CancellationToken));
        Assert.Equal(StorageFixtures.CanonicalState(expected), ReadState(database));
    }

    [Fact]
    public void BackupWithoutSavedStateDoesNotReplacePreviousValidBackup()
    {
        string populated = FilePath("populated.db");
        string empty = FilePath("empty.db");
        string backup = FilePath("portable.dotfinance-backup");
        using (EncryptedVault vault = EncryptedVault.Create(populated, StorageFixtures.Password))
        {
            vault.Save(StorageFixtures.RepresentativeBook(), cancellationToken: TestContext.Current.CancellationToken);
            vault.CreateBackup(backup, cancellationToken: TestContext.Current.CancellationToken);
        }
        byte[] validBackup = File.ReadAllBytes(backup);

        using EncryptedVault emptyVault = EncryptedVault.Create(empty, StorageFixtures.Password);
        Assert.Throws<VaultStorageException>(() => emptyVault.CreateBackup(
            backup, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(validBackup, File.ReadAllBytes(backup));
        Assert.False(File.Exists(backup + ".partial"));
    }

    [Fact]
    public void MigrationFailureRollsBackAndFutureSchemaIsRejected()
    {
        string migrationCandidate = FilePath("migration.db");
        CreateEmptyEncryptedDatabase(migrationCandidate);
        VaultDependencies failingMigration = new(PhysicalVaultFileSystem.Instance,
            _ => throw new IOException("Synthetic migration fault"));
        Assert.Throws<VaultMigrationException>(() => EncryptedVault.Unlock(
            migrationCandidate, StorageFixtures.Password, failingMigration));
        using (SqliteConnection raw = OpenRaw(migrationCandidate))
        {
            Assert.Equal(0L, Scalar(raw, "PRAGMA user_version;"));
            Assert.Equal(0L, Scalar(raw, "SELECT count(*) FROM sqlite_schema WHERE name IN ('schema_history', 'book_state');"));
        }

        string future = FilePath("future.db");
        using (EncryptedVault vault = EncryptedVault.Create(future, StorageFixtures.Password))
            vault.Save(StorageFixtures.RepresentativeBook(), cancellationToken: TestContext.Current.CancellationToken);
        using (SqliteConnection raw = OpenRaw(future))
        {
            using SqliteCommand command = raw.CreateCommand();
            command.CommandText = "PRAGMA user_version = 2;";
            command.ExecuteNonQuery();
        }
        Assert.Throws<VaultMigrationException>(() => EncryptedVault.Unlock(future, StorageFixtures.Password));
    }

    [Fact]
    public void VaultFilesExcludeSyntheticMarkersPasswordAndPlaintextHeader()
    {
        string database = FilePath("working.db");
        string backup = FilePath("portable.dotfinance-backup");
        using (EncryptedVault vault = EncryptedVault.Create(database, StorageFixtures.Password))
        {
            vault.Save(StorageFixtures.RepresentativeBook(), cancellationToken: TestContext.Current.CancellationToken);
            vault.CreateBackup(backup, cancellationToken: TestContext.Current.CancellationToken);
        }

        foreach (string file in Directory.GetFiles(_directory, "*", SearchOption.AllDirectories))
        {
            byte[] bytes = File.ReadAllBytes(file);
            Assert.DoesNotContain("baseline-1"u8.ToArray(), bytes);
            Assert.DoesNotContain("Synthetic merchant"u8.ToArray(), bytes);
            Assert.DoesNotContain(Encoding.UTF8.GetBytes(StorageFixtures.Password), bytes);
            Assert.DoesNotContain("SQLite format 3"u8.ToArray(), bytes);
        }
    }

    [Fact]
    public void InvalidPathsExistingFilesAndDisposedSessionsFailWithoutMutation()
    {
        string database = FilePath("working.db");
        Assert.Throws<ArgumentException>(() => EncryptedVault.Create(FilePath("blank-password.db"), "   "));
        Assert.False(File.Exists(FilePath("blank-password.db")));
        VaultAccessException missing = Assert.Throws<VaultAccessException>(() =>
            EncryptedVault.Unlock(FilePath("missing.db"), StorageFixtures.Password));
        Assert.Equal("The password is incorrect or the encrypted vault is damaged.", missing.Message);

        EncryptedVault vault = EncryptedVault.Create(database, StorageFixtures.Password);
        vault.Save(StorageFixtures.RepresentativeBook(), cancellationToken: TestContext.Current.CancellationToken);
        byte[] original = File.ReadAllBytes(database);
        Assert.Throws<VaultStorageException>(() => EncryptedVault.Create(database, StorageFixtures.Password));
        Assert.Equal(original, File.ReadAllBytes(database));
        Assert.Throws<ArgumentException>(() => EncryptedVault.Restore(
            database, database, StorageFixtures.Password, cancellationToken: TestContext.Current.CancellationToken));

        vault.Dispose();
        Assert.Throws<ObjectDisposedException>(() => vault.Load());
        Assert.Throws<ObjectDisposedException>(() => vault.Save(StorageFixtures.RepresentativeBook(),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => vault.CreateBackup(FilePath("disposed.backup"),
            cancellationToken: TestContext.Current.CancellationToken));
        Assert.Throws<ObjectDisposedException>(() => vault.SecurityProfile());
    }

    [Fact]
    public void UnsupportedDomainStateVersionFailsWithGenericAccessError()
    {
        string database = FilePath("working.db");
        using (EncryptedVault vault = EncryptedVault.Create(database, StorageFixtures.Password))
            vault.Save(StorageFixtures.RepresentativeBook(), cancellationToken: TestContext.Current.CancellationToken);
        using (SqliteConnection raw = OpenRaw(database))
        {
            using SqliteCommand command = raw.CreateCommand();
            command.CommandText = "UPDATE book_state SET state_version = 2 WHERE id = 1;";
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        using EncryptedVault reopened = EncryptedVault.Unlock(database, StorageFixtures.Password);
        VaultAccessException exception = Assert.Throws<VaultAccessException>(() => reopened.Load());
        Assert.Equal("The password is incorrect or the encrypted vault is damaged.", exception.Message);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    private string FilePath(string name)
    {
        Directory.CreateDirectory(_directory);
        return System.IO.Path.Combine(_directory, name);
    }

    private static string ReadState(string database)
    {
        using EncryptedVault vault = EncryptedVault.Unlock(database, StorageFixtures.Password);
        return StorageFixtures.CanonicalState(vault.Load());
    }

    private static ReconciliationBook ReadyToCompleteBook()
    {
        ReconciliationBook book = StorageFixtures.RepresentativeBook();
        book.RejectCorrection("baseline-2");
        book.ResolveImportConflict("duplicate-1", book.ImportConflicts.Single().Candidates[0]);
        if (!book.Current!.DiscrepancyAcknowledged) book.AcknowledgeDiscrepancy();
        foreach (FundingTransfer transfer in book.Current.Transfers.Where(value => !value.Acknowledged).ToArray())
            book.AcknowledgeTransfer(transfer.AccountId);
        return book;
    }

    private static SqliteConnection OpenRaw(string path)
    {
        string dataSource = new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri
            + "?cipher=chacha20&legacy=0&kdf_iter=600000&plaintext_header_size=0&hmac_check=1";
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
            Password = StorageFixtures.Password
        }.ConnectionString);
        connection.Open();
        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void CreateEmptyEncryptedDatabase(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string dataSource = new Uri(System.IO.Path.GetFullPath(path)).AbsoluteUri
            + "?cipher=chacha20&legacy=0&kdf_iter=600000&plaintext_header_size=0&hmac_check=1";
        using SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            Password = StorageFixtures.Password
        }.ConnectionString);
        connection.Open();
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE retained_synthetic_state (id INTEGER PRIMARY KEY);";
        command.ExecuteNonQuery();
    }

    private sealed class InlineProgress(Action<VaultStage> report) : IProgress<VaultStage>
    {
        public void Report(VaultStage value) => report(value);
    }

    private sealed class ReplaceFailingFileSystem : IVaultFileSystem
    {
        private readonly PhysicalVaultFileSystem _inner = PhysicalVaultFileSystem.Instance;
        public bool FileExists(string path) => _inner.FileExists(path);
        public void CreateDirectory(string path) => _inner.CreateDirectory(path);
        public void CopyFile(string source, string destination) => _inner.CopyFile(source, destination);
        public void DeleteFile(string path) => _inner.DeleteFile(path);
        public void ReplaceFile(string source, string destination) => throw new IOException("Synthetic replacement fault");
    }

    private sealed class RecordingVault : IReconciliationVault
    {
        internal Exception? SaveError { get; init; }
        internal Exception? BackupError { get; set; }
        internal ReconciliationBook? Saved { get; private set; }
        internal int BackupAttempts { get; private set; }

        public void Save(ReconciliationBook book, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SaveError is not null) throw SaveError;
            Saved = book;
        }

        public void CreateBackup(string backupPath, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BackupAttempts++;
            if (BackupError is not null) throw BackupError;
        }
    }
}