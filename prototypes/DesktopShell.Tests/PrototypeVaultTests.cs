namespace DesktopShell.Tests;

public sealed class PrototypeVaultTests : IDisposable
{
    private const string Password = "synthetic-prototype-password";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dot-finance-tests-{Guid.NewGuid():N}");

    [Fact]
    public void Imported_state_survives_save_and_restart_exactly()
    {
        Directory.CreateDirectory(_directory);
        PrototypeState expected = SyntheticEmmaImport.Read(FixturePath());
        string databasePath = Path.Combine(_directory, "working.db");

        using (PrototypeVault vault = PrototypeVault.Create(databasePath, Password))
        {
            vault.Save(expected);
        }

        using PrototypeVault reopened = PrototypeVault.Unlock(databasePath, Password);
        Assert.Equal(expected, reopened.Load());
    }

    [Fact]
    public void Backup_restores_the_exact_state_with_the_same_password()
    {
        Directory.CreateDirectory(_directory);
        PrototypeState expected = SyntheticEmmaImport.Read(FixturePath());
        string databasePath = Path.Combine(_directory, "working.db");
        string backupPath = Path.Combine(_directory, "portable.dotfinance-backup");
        using (PrototypeVault vault = PrototypeVault.Create(databasePath, Password))
        {
            vault.Save(expected);
            vault.CreateBackup(backupPath);
        }

        File.Delete(databasePath);
        PrototypeVault.Restore(backupPath, databasePath, Password);

        using PrototypeVault restored = PrototypeVault.Unlock(databasePath, Password);
        Assert.Equal(expected, restored.Load());
    }

    [Fact]
    public void Working_storage_and_backup_do_not_contain_plaintext_fixture_values_or_password()
    {
        Directory.CreateDirectory(_directory);
        PrototypeState expected = SyntheticEmmaImport.Read(FixturePath());
        string databasePath = Path.Combine(_directory, "working.db");
        string backupPath = Path.Combine(_directory, "portable.dotfinance-backup");
        using (PrototypeVault vault = PrototypeVault.Create(databasePath, Password))
        {
            vault.Save(expected);
            vault.CreateBackup(backupPath);
        }

        foreach (string path in new[] { databasePath, backupPath })
        {
            byte[] bytes = File.ReadAllBytes(path);
            Assert.DoesNotContain("SYNTH-0001"u8.ToArray(), bytes);
            Assert.DoesNotContain("Example Market"u8.ToArray(), bytes);
            Assert.DoesNotContain(System.Text.Encoding.UTF8.GetBytes(Password), bytes);
            Assert.DoesNotContain("SQLite format 3"u8.ToArray(), bytes);
        }
    }

    [Fact]
    public void Wrong_password_and_tampered_backup_do_not_replace_valid_working_state()
    {
        Directory.CreateDirectory(_directory);
        PrototypeState expected = SyntheticEmmaImport.Read(FixturePath());
        string databasePath = Path.Combine(_directory, "working.db");
        string backupPath = Path.Combine(_directory, "portable.dotfinance-backup");
        using (PrototypeVault vault = PrototypeVault.Create(databasePath, Password))
        {
            vault.Save(expected);
            vault.CreateBackup(backupPath);
        }

        Assert.Throws<VaultAuthenticationException>(() => PrototypeVault.Restore(backupPath, databasePath, "wrong-password"));
        Assert.Equal(expected, Read(databasePath));

        byte[] tampered = File.ReadAllBytes(backupPath);
        tampered[0] ^= 0x5A;
        File.WriteAllBytes(backupPath, tampered);
        Assert.Throws<VaultAuthenticationException>(() => PrototypeVault.Restore(backupPath, databasePath, Password));
        Assert.Equal(expected, Read(databasePath));
    }

    [Fact]
    public void Cancelled_save_and_backup_preserve_existing_valid_files()
    {
        Directory.CreateDirectory(_directory);
        PrototypeState expected = SyntheticEmmaImport.Read(FixturePath());
        PrototypeState changed = new()
        {
            ImportedAt = expected.ImportedAt,
            AllocationStatus = "Changed but uncommitted",
            Transactions = expected.Transactions
        };
        string databasePath = Path.Combine(_directory, "working.db");
        string backupPath = Path.Combine(_directory, "portable.dotfinance-backup");
        using PrototypeVault vault = PrototypeVault.Create(databasePath, Password);
        vault.Save(expected);
        vault.CreateBackup(backupPath);
        byte[] originalBackup = File.ReadAllBytes(backupPath);

        using CancellationTokenSource saveCancellation = new();
        InlineProgress<PersistenceStage> saveProgress = new(stage =>
        {
            if (stage == PersistenceStage.BeforeCommit)
            {
                saveCancellation.Cancel();
            }
        });
        Assert.Throws<OperationCanceledException>(() => vault.Save(changed, saveCancellation.Token, saveProgress));
        Assert.Equal(expected, vault.Load());

        using CancellationTokenSource backupCancellation = new();
        InlineProgress<PersistenceStage> backupProgress = new(stage =>
        {
            if (stage == PersistenceStage.BackupStaged)
            {
                backupCancellation.Cancel();
            }
        });
        Assert.Throws<OperationCanceledException>(() => vault.CreateBackup(backupPath, backupCancellation.Token, backupProgress));
        Assert.Equal(originalBackup, File.ReadAllBytes(backupPath));
        Assert.False(File.Exists(backupPath + ".partial"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static PrototypeState Read(string databasePath)
    {
        using PrototypeVault vault = PrototypeVault.Unlock(databasePath, Password);
        return vault.Load();
    }

    private static string FixturePath() => Path.Combine(AppContext.BaseDirectory, "Fixtures", "synthetic-emma.csv");

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}