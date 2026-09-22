namespace AuthenticatedSnapshots.Tests;

public sealed class FilesystemFaultTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dot-finance-filesystem-fault-{Guid.NewGuid():N}");

    [Fact]
    public void A_subsequent_save_succeeds_after_a_save_orphans_a_generation_before_the_pointer_update()
    {
        const string password = "synthetic test password";
        SnapshotDocument first = SyntheticDocument();
        using (VaultSession vault = VaultSession.Create(_directory, password))
        {
            vault.Save(first);
            vault.Save(SyntheticDocument("SYNTH-0002"));
        }

        // Simulate interruption after publishing generation-2 but before updating the
        // active pointer: the pointer is rolled back to generation-1 while generation-2
        // remains on disk as a complete orphan.
        File.WriteAllText(Path.Combine(_directory, "active-generation"), "generation-0000000001.dfsnap\n");

        SnapshotDocument third = SyntheticDocument("SYNTH-0003");
        using (VaultSession vault = VaultSession.Unlock(_directory, password))
        {
            Assert.Equivalent(first, vault.Load(), strict: true);
            vault.Save(third);
        }

        using VaultSession reopened = VaultSession.Unlock(_directory, password);
        Assert.Equivalent(third, reopened.Load(), strict: true);
    }

    [Fact]
    public void Stale_partial_files_are_ignored_and_never_selected_as_the_active_generation()
    {
        const string password = "synthetic test password";
        SnapshotDocument first = SyntheticDocument();
        using (VaultSession vault = VaultSession.Create(_directory, password))
        {
            vault.Save(first);
        }

        // Leftover partial files from an interrupted earlier run must be ignored.
        string[] staleFiles =
        [
            Path.Combine(_directory, ".stale.partial"),
            Path.Combine(_directory, ".stale.active-partial"),
            Path.Combine(_directory, ".stale.restore-partial"),
            Path.Combine(_directory, ".stale.journal-partial"),
        ];
        foreach (string stale in staleFiles)
        {
            File.WriteAllText(stale, "synthetic leftover bytes");
        }

        SnapshotDocument second = SyntheticDocument("SYNTH-0002");
        using (VaultSession vault = VaultSession.Unlock(_directory, password))
        {
            Assert.Equivalent(first, vault.Load(), strict: true);
            vault.Save(second);
        }

        using VaultSession reopened = VaultSession.Unlock(_directory, password);
        Assert.Equivalent(second, reopened.Load(), strict: true);

        string active = File.ReadAllText(Path.Combine(_directory, "active-generation")).Trim();
        Assert.StartsWith("generation-", active, StringComparison.Ordinal);
        Assert.EndsWith(".dfsnap", active, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(_directory, active)));
        foreach (string stale in staleFiles)
        {
            Assert.Equal("synthetic leftover bytes", File.ReadAllText(stale));
        }
    }

    [Fact]
    public void A_second_writer_cannot_unlock_the_same_vault()
    {
        using (VaultSession vault = VaultSession.Create(_directory, "synthetic test password"))
        {
            vault.Save(SyntheticDocument());
        }

        using VaultSession first = VaultSession.Unlock(_directory, "synthetic test password");

        // The exclusive lock is refused before authentication, so the password need not be correct.
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => VaultSession.Unlock(_directory, "deliberately wrong password"));

        Assert.Contains("already open", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Restore_into_a_non_empty_vault_directory_is_refused()
    {
        const string password = "synthetic test password";
        string backupPath = Path.Combine(Path.GetTempPath(), $"dot-finance-restore-refusal-{Guid.NewGuid():N}.dfsnap");
        string targetDirectory = Path.Combine(Path.GetTempPath(), $"dot-finance-restore-refusal-{Guid.NewGuid():N}");
        try
        {
            using (VaultSession vault = VaultSession.Create(_directory, password))
            {
                vault.Save(SyntheticDocument());
                vault.CreateBackup(backupPath);
            }

            SnapshotDocument existing = SyntheticDocument("SYNTH-0002");
            using (VaultSession target = VaultSession.Create(targetDirectory, password))
            {
                target.Save(existing);
            }

            InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
                () => VaultSession.Restore(backupPath, targetDirectory, password));

            Assert.Contains("empty", exception.Message, StringComparison.Ordinal);

            using VaultSession unchanged = VaultSession.Unlock(targetDirectory, password);
            Assert.Equivalent(existing, unchanged.Load(), strict: true);
        }
        finally
        {
            File.Delete(backupPath);
            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, true);
            }
        }
    }

    [Fact]
    public void Backup_over_a_different_generation_is_refused_and_preserves_the_existing_backup()
    {
        const string password = "synthetic test password";
        string backupPath = Path.Combine(Path.GetTempPath(), $"dot-finance-backup-refusal-{Guid.NewGuid():N}.dfsnap");
        try
        {
            using VaultSession vault = VaultSession.Create(_directory, password);
            vault.Save(SyntheticDocument());
            vault.CreateBackup(backupPath);
            byte[] original = File.ReadAllBytes(backupPath);

            vault.Save(SyntheticDocument("SYNTH-0002"));

            IOException exception = Assert.Throws<IOException>(
                () => vault.CreateBackup(backupPath));

            Assert.Contains("different", exception.Message, StringComparison.Ordinal);
            Assert.Equal(original, File.ReadAllBytes(backupPath));
        }
        finally
        {
            File.Delete(backupPath);
        }
    }

    [Fact]
    public void Automatic_backup_with_a_missing_parent_directory_stays_pending()
    {
        const string password = "synthetic test password";
        string targetPath = Path.Combine(Path.GetTempPath(), $"dot-finance-missing-parent-{Guid.NewGuid():N}", "auto.dfsnap");
        try
        {
            SnapshotDocument expected = SyntheticDocument();
            using (VaultSession vault = VaultSession.Create(_directory, password))
            {
                vault.Save(expected);
                vault.QueueAutomaticBackup(targetPath);

                IReadOnlyList<SnapshotBackupReceipt> published = vault.PublishPendingBackups();
                Assert.Empty(published);

                PendingBackupEntry entry = Assert.Single(PendingBackupJournal.Read(_directory));
                Assert.Equal(targetPath, entry.TargetPath);
            }

            Assert.False(File.Exists(targetPath));

            using VaultSession reopened = VaultSession.Unlock(_directory, password);
            Assert.Equivalent(expected, reopened.Load(), strict: true);
        }
        finally
        {
            string? parent = Path.GetDirectoryName(targetPath);
            if (parent is not null && Directory.Exists(parent))
            {
                Directory.Delete(parent, true);
            }
        }
    }

    [Fact]
    public void Read_only_vault_directory_fails_save_cleanly_and_preserves_the_previous_generation()
    {
        // Root bypasses permission checks, so a read-only directory cannot exercise the
        // failure path under a privileged account (this container runs as root); the
        // scenario is meaningful on the non-root GitHub Actions runner.
        if (Environment.IsPrivilegedProcess)
        {
            return;
        }

        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        const string password = "synthetic test password";
        SnapshotDocument first = SyntheticDocument();
        using (VaultSession vault = VaultSession.Create(_directory, password))
        {
            vault.Save(first);

            File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserExecute);
            try
            {
                try
                {
                    vault.Save(SyntheticDocument("SYNTH-0002"));
                    Assert.Fail("Expected the second save to fail against a read-only vault directory.");
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // Expected: the save fails cleanly without replacing the previous generation.
                }
            }
            finally
            {
                File.SetUnixFileMode(_directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        using VaultSession reopened = VaultSession.Unlock(_directory, password);
        Assert.Equivalent(first, reopened.Load(), strict: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private static SnapshotDocument SyntheticDocument(string transactionId = "SYNTH-0001") => new(
        1,
        new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
        [new SnapshotTransaction(transactionId, new DateOnly(2026, 9, 20), 42.50m, "Fictional Card", "Example Bank", "GBP", "Groceries", "Food", "Purchase", "", "Example Market", "", "Example Market", "Synthetic details", "Synthetic note", "", "Joint", "REC-001", "Unsettled")],
        new Dictionary<string, string> { ["theme"] = "System" },
        [new ReconciliationRecord("REC-001", new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero), "Draft", 1200m, new Dictionary<string, decimal> { ["Joint"] = 450m }, [])]);
}