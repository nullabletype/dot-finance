namespace AuthenticatedSnapshots.Tests;

public sealed class VaultSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dot-finance-snapshot-{Guid.NewGuid():N}");

    [Fact]
    public void Saved_state_can_be_unlocked_and_loaded_exactly()
    {
        SnapshotDocument expected = SyntheticDocument();

        using (VaultSession vault = VaultSession.Create(_directory, "synthetic test password"))
        {
            vault.Save(expected);
        }

        using VaultSession reopened = VaultSession.Unlock(_directory, "synthetic test password");
        Assert.Equivalent(expected, reopened.Load(), strict: true);
    }

    [Theory]
    [InlineData("example bank")]
    [InlineData("2026-09-20")]
    [InlineData("42.50")]
    [InlineData("unsettled")]
    [InlineData("rec-001")]
    public void Search_finds_transactions_across_displayed_and_status_fields(string query)
    {
        using VaultSession vault = VaultSession.Create(_directory, "synthetic test password");
        vault.Save(SyntheticDocument());

        SnapshotTransaction transaction = Assert.Single(vault.Search(query));

        Assert.Equal("SYNTH-0001", transaction.Id);
    }

    [Fact]
    public void Backup_restores_the_exact_authenticated_generation()
    {
        string backupPath = Path.Combine(Path.GetTempPath(), $"dot-finance-backup-{Guid.NewGuid():N}.dfsnap");
        string restoredDirectory = Path.Combine(Path.GetTempPath(), $"dot-finance-restored-{Guid.NewGuid():N}");
        try
        {
            SnapshotDocument expected = SyntheticDocument();
            using (VaultSession vault = VaultSession.Create(_directory, "synthetic test password"))
            {
                vault.Save(expected);
                SnapshotBackupReceipt receipt = vault.CreateBackup(backupPath);
                Assert.Equal(1u, receipt.Generation);
            }

            VaultSession.Restore(backupPath, restoredDirectory, "synthetic test password");
            using VaultSession restored = VaultSession.Unlock(restoredDirectory, "synthetic test password");
            Assert.Equivalent(expected, restored.Load(), strict: true);
        }
        finally
        {
            File.Delete(backupPath);
            if (Directory.Exists(restoredDirectory))
            {
                Directory.Delete(restoredDirectory, true);
            }
        }
    }

    [Fact]
    public void Wrong_password_or_modified_ciphertext_cannot_unlock_the_vault()
    {
        using (VaultSession vault = VaultSession.Create(_directory, "synthetic test password"))
        {
            vault.Save(SyntheticDocument());
        }

        Assert.Throws<VaultAuthenticationException>(() => VaultSession.Unlock(_directory, "wrong synthetic password"));

        string generationPath = Assert.Single(Directory.GetFiles(_directory, "generation-*.dfsnap"));
        byte[] bytes = File.ReadAllBytes(generationPath);
        bytes[bytes.Length / 2] ^= 0x5A;
        File.WriteAllBytes(generationPath, bytes);

        Assert.Throws<VaultAuthenticationException>(() => VaultSession.Unlock(_directory, "synthetic test password"));
    }

    [Fact]
    public void Every_envelope_region_and_length_change_is_authenticated_or_rejected()
    {
        using (VaultSession vault = VaultSession.Create(_directory, "synthetic test password"))
        {
            vault.Save(SyntheticDocument());
        }

        string generationPath = Assert.Single(Directory.GetFiles(_directory, "generation-*.dfsnap"));
        byte[] original = File.ReadAllBytes(generationPath);
        int[] offsets = [0, 8, 10, 11, 12, 16, 32, 48, 52, 64, 72, original.Length / 2, original.Length - 1];
        foreach (int offset in offsets)
        {
            byte[] modified = (byte[])original.Clone();
            modified[offset] ^= 0x5A;
            File.WriteAllBytes(generationPath, modified);
            Assert.Throws<VaultAuthenticationException>(() => VaultSession.Unlock(_directory, "synthetic test password"));
        }

        File.WriteAllBytes(generationPath, original[..^1]);
        Assert.Throws<VaultAuthenticationException>(() => VaultSession.Unlock(_directory, "synthetic test password"));
        File.WriteAllBytes(generationPath, [.. original, 0x5A]);
        Assert.Throws<VaultAuthenticationException>(() => VaultSession.Unlock(_directory, "synthetic test password"));
        File.WriteAllBytes(generationPath, original);
    }

    [Fact]
    public void A_second_writer_cannot_open_the_same_vault()
    {
        using VaultSession first = VaultSession.Create(_directory, "synthetic test password");

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => VaultSession.Create(_directory, "another synthetic password"));

        Assert.Contains("already open", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Password_change_commits_as_one_new_generation_and_old_backups_keep_the_old_password()
    {
        string backupPath = Path.Combine(Path.GetTempPath(), $"dot-finance-old-password-{Guid.NewGuid():N}.dfsnap");
        try
        {
            SnapshotDocument expected = SyntheticDocument();
            using (VaultSession vault = VaultSession.Create(_directory, "old synthetic password"))
            {
                vault.Save(expected);
                vault.CreateBackup(backupPath);
                vault.ChangePassword("new synthetic password");
            }

            Assert.Throws<VaultAuthenticationException>(() => VaultSession.Unlock(_directory, "old synthetic password"));
            using VaultSession reopened = VaultSession.Unlock(_directory, "new synthetic password");
            Assert.Equivalent(expected, reopened.Load(), strict: true);

            string restoredDirectory = Path.Combine(Path.GetTempPath(), $"dot-finance-old-backup-{Guid.NewGuid():N}");
            try
            {
                VaultSession.Restore(backupPath, restoredDirectory, "old synthetic password");
                using VaultSession restored = VaultSession.Unlock(restoredDirectory, "old synthetic password");
                Assert.Equivalent(expected, restored.Load(), strict: true);
            }
            finally
            {
                if (Directory.Exists(restoredDirectory))
                {
                    Directory.Delete(restoredDirectory, true);
                }
            }
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