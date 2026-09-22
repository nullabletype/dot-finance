namespace AuthenticatedSnapshots.Tests;

using System.Text.Json;

public sealed class AutomaticBackupJournalTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dot-finance-auto-backup-{Guid.NewGuid():N}");

    [Fact]
    public void Queued_backup_publishes_on_reopen_and_clears_the_journal()
    {
        string backupPath = Path.Combine(Path.GetTempPath(), $"dot-finance-auto-backup-{Guid.NewGuid():N}.dfsnap");
        try
        {
            using (VaultSession vault = VaultSession.Create(_directory, "synthetic test password"))
            {
                vault.Save(SyntheticDocument());
                vault.QueueAutomaticBackup(backupPath);
            }

            Assert.True(File.Exists(Path.Combine(_directory, PendingBackupJournal.FileName)));

            using (VaultSession reopened = VaultSession.Unlock(_directory, "synthetic test password"))
            {
                Assert.NotEmpty(reopened.Load().Transactions);
            }

            Assert.True(File.Exists(backupPath));
            string generationPath = Assert.Single(Directory.GetFiles(_directory, "generation-*.dfsnap"));
            Assert.Equal(File.ReadAllBytes(generationPath), File.ReadAllBytes(backupPath));
            Assert.Empty(PendingBackupJournal.Read(_directory));
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    [Fact]
    public void Failed_publication_keeps_the_entry_pending_and_the_vault_still_opens()
    {
        string invalidBackupPath = Path.Combine(Path.GetTempPath(), $"dot-finance-auto-backup-missing-{Guid.NewGuid():N}", "auto.dfsnap");
        try
        {
            using (VaultSession vault = VaultSession.Create(_directory, "synthetic test password"))
            {
                vault.Save(SyntheticDocument());
                vault.QueueAutomaticBackup(invalidBackupPath);
            }

            using (VaultSession reopened = VaultSession.Unlock(_directory, "synthetic test password"))
            {
                Assert.NotEmpty(reopened.Load().Transactions);
            }

            Assert.False(File.Exists(invalidBackupPath));
            PendingBackupEntry entry = Assert.Single(PendingBackupJournal.Read(_directory));
            Assert.Equal(invalidBackupPath, entry.TargetPath);
        }
        finally
        {
            string? parent = Path.GetDirectoryName(invalidBackupPath);
            if (parent is not null && Directory.Exists(parent))
            {
                Directory.Delete(parent, true);
            }
        }
    }

    [Fact]
    public void Republishing_an_already_published_backup_is_idempotent()
    {
        string backupPath = Path.Combine(Path.GetTempPath(), $"dot-finance-auto-backup-idem-{Guid.NewGuid():N}.dfsnap");
        try
        {
            using (VaultSession vault = VaultSession.Create(_directory, "synthetic test password"))
            {
                vault.Save(SyntheticDocument());
                vault.QueueAutomaticBackup(backupPath);

                IReadOnlyList<SnapshotBackupReceipt> first = vault.PublishPendingBackups();
                Assert.Single(first);

                vault.QueueAutomaticBackup(backupPath);
                IReadOnlyList<SnapshotBackupReceipt> second = vault.PublishPendingBackups();
                Assert.Single(second);
                Assert.Empty(PendingBackupJournal.Read(_directory));
            }

            Assert.True(File.Exists(backupPath));
            string backup = Assert.Single(Directory.GetFiles(Path.GetDirectoryName(backupPath)!, "dot-finance-auto-backup-idem-*.dfsnap"));
            Assert.Equal(backupPath, backup);
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    [Fact]
    public void Journal_contains_only_vault_metadata()
    {
        string backupPath = Path.Combine(Path.GetTempPath(), $"dot-finance-auto-backup-meta-{Guid.NewGuid():N}.dfsnap");
        try
        {
            using (VaultSession vault = VaultSession.Create(_directory, "synthetic test password"))
            {
                vault.Save(SyntheticDocument());
                vault.QueueAutomaticBackup(backupPath);
            }

            string journal = File.ReadAllText(Path.Combine(_directory, PendingBackupJournal.FileName));
            Assert.DoesNotContain("synthetic test password", journal, StringComparison.Ordinal);
            Assert.DoesNotContain("SYNTH-0001", journal, StringComparison.Ordinal);
            Assert.DoesNotContain("Example Bank", journal, StringComparison.Ordinal);
            Assert.DoesNotContain("Fictional Card", journal, StringComparison.Ordinal);
            Assert.DoesNotContain("Example Market", journal, StringComparison.Ordinal);
            Assert.DoesNotContain("42.50", journal, StringComparison.Ordinal);
            Assert.DoesNotContain("password", journal, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    [Fact]
    public void Queueing_before_any_save_is_rejected()
    {
        using VaultSession vault = VaultSession.Create(_directory, "synthetic test password");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => vault.QueueAutomaticBackup(Path.Combine(Path.GetTempPath(), "unused.dfsnap")));

        Assert.Contains("Save", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Corrupt_journal_fails_queueing_loudly_and_publish_returns_empty_and_the_vault_still_opens()
    {
        const string password = "synthetic test password";
        using (VaultSession vault = VaultSession.Create(_directory, password))
        {
            vault.Save(SyntheticDocument());
        }

        string journalPath = Path.Combine(_directory, PendingBackupJournal.FileName);
        File.WriteAllText(journalPath, "{ not valid json");
        byte[] corrupt = File.ReadAllBytes(journalPath);

        using (VaultSession vault = VaultSession.Unlock(_directory, password))
        {
            // Queueing fails loudly on a corrupt journal instead of silently discarding it.
            Assert.Throws<JsonException>(() => vault.QueueAutomaticBackup(Path.Combine(Path.GetTempPath(), $"dot-finance-corrupt-{Guid.NewGuid():N}.dfsnap")));

            // Publication returns empty and leaves the corrupt journal byte-for-byte unchanged.
            Assert.Empty(vault.PublishPendingBackups());
            Assert.Equal(corrupt, File.ReadAllBytes(journalPath));
            Assert.NotEmpty(vault.Load().Transactions);
        }

        // The vault still opens.
        using VaultSession reopened = VaultSession.Unlock(_directory, password);
        Assert.NotEmpty(reopened.Load().Transactions);
    }

    [Fact]
    public void Password_change_with_a_pending_backup_throws_and_does_not_change_the_password()
    {
        const string password = "synthetic test password";
        string backupPath = Path.Combine(Path.GetTempPath(), $"dot-finance-pending-password-{Guid.NewGuid():N}.dfsnap");
        try
        {
            SnapshotDocument expected = SyntheticDocument();
            using (VaultSession vault = VaultSession.Create(_directory, password))
            {
                vault.Save(expected);
                vault.QueueAutomaticBackup(backupPath);

                InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                    () => vault.ChangePassword("synthetic new password"));

                Assert.Contains("Publish pending", exception.Message, StringComparison.Ordinal);
            }

            using (VaultSession reopened = VaultSession.Unlock(_directory, password))
            {
                Assert.Equivalent(expected, reopened.Load(), strict: true);
            }

            Assert.Throws<VaultAuthenticationException>(() => VaultSession.Unlock(_directory, "synthetic new password"));
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    [Fact]
    public void A_json_null_journal_is_corrupt_and_is_not_silently_overwritten()
    {
        string backupPath = Path.Combine(Path.GetTempPath(), $"dot-finance-auto-backup-null-{Guid.NewGuid():N}.dfsnap");
        string journalPath = Path.Combine(_directory, PendingBackupJournal.FileName);
        try
        {
            using (VaultSession vault = VaultSession.Create(_directory, "synthetic test password"))
            {
                vault.Save(SyntheticDocument());
            }

            File.WriteAllText(journalPath, "null");

            using (VaultSession vault = VaultSession.Unlock(_directory, "synthetic test password"))
            {
                Assert.Empty(vault.PublishPendingBackups());
                Assert.Throws<InvalidDataException>(() => vault.QueueAutomaticBackup(backupPath));
            }

            Assert.Equal("null", File.ReadAllText(journalPath));
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static SnapshotDocument SyntheticDocument() => new(
        1,
        new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
        [new SnapshotTransaction("SYNTH-0001", new DateOnly(2026, 9, 20), 42.50m, "Fictional Card", "Example Bank", "GBP", "Groceries", "Food", "Purchase", "", "Example Market", "", "Example Market", "Synthetic details", "Synthetic note", "", "Joint", "REC-001", "Unsettled")],
        new Dictionary<string, string> { ["theme"] = "System" },
        [new ReconciliationRecord("REC-001", new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero), "Draft", 1200m, new Dictionary<string, decimal> { ["Joint"] = 450m }, [])]);
}