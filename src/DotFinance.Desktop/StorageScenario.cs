using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using DotFinance.Domain;
using DotFinance.Storage;

namespace DotFinance.Desktop;

internal static class StorageScenario
{
    private const string PasswordEnvironmentVariable = "DOT_FINANCE_VAULT_PASSWORD";

    internal static int Run(string[] args)
    {
        string? supplied = Environment.GetEnvironmentVariable(PasswordEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(supplied))
            throw new InvalidOperationException($"{PasswordEnvironmentVariable} is required for the synthetic validation command.");
        Environment.SetEnvironmentVariable(PasswordEnvironmentVariable, null);
        char[] password = supplied.ToCharArray();
        try
        {
            return args switch
            {
                ["--vault-cycle", string mode, string workspace] => Cycle(mode, workspace, null, password),
                ["--vault-cycle", string mode, string workspace, string inputBackup] => Cycle(mode, workspace, inputBackup, password),
                ["--vault-interrupt", string operation, string workspace] => Interrupt(operation, workspace, password),
                ["--vault-recovery", string workspace] => Recovery(workspace, password),
                ["--vault-verify-restored", string workspace] => VerifyRestored(workspace, password),
                _ => Usage()
            };
        }
        finally
        {
            Array.Clear(password);
        }
    }

    private static int Cycle(string mode, string workspace, string? inputBackup, ReadOnlySpan<char> password)
    {
        if (mode is not ("initialize" or "continue" or "verify")) return Usage();
        Directory.CreateDirectory(workspace);
        string database = Path.Combine(workspace, "working.db");
        string backup = Path.Combine(workspace, "portable.dotfinance-backup");
        if (mode == "initialize")
        {
            DeleteManaged(database);
            using EncryptedVault vault = EncryptedVault.Create(database, password);
            RequireKdfBudget(vault.KeyDerivationElapsed);
            vault.Save(RepresentativeBook());
            vault.CreateBackup(backup);
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inputBackup);
            DeleteManaged(database);
            EncryptedVault.Restore(inputBackup, database, password);
            using EncryptedVault vault = EncryptedVault.Unlock(database, password);
            RequireKdfBudget(vault.KeyDerivationElapsed);
            AssertExpected(vault.Load());
            if (mode == "continue") vault.CreateBackup(backup);
        }

        VaultSecurityProfile profile;
        using (EncryptedVault reopened = EncryptedVault.Unlock(database, password))
        {
            AssertExpected(reopened.Load());
            profile = reopened.SecurityProfile();
        }
        InspectManagedFiles(workspace, password);
        WriteReceipt(workspace, mode, database, profile);
        return 0;
    }

    private static int Interrupt(string operation, string workspace, ReadOnlySpan<char> password)
    {
        InlineProgress progress = new(stage =>
        {
            bool requested = operation == "save" && stage == VaultStage.BeforeSaveCommit
                || operation == "backup" && stage == VaultStage.BackupStaged
                || operation == "restore" && stage == VaultStage.RestoreStaged;
            if (!requested) return;
            Process.GetCurrentProcess().Kill();
            Thread.Sleep(Timeout.Infinite);
        });
        string database = Path.Combine(workspace, "working.db");
        if (operation == "restore")
        {
            EncryptedVault.Restore(Path.Combine(workspace, "portable.dotfinance-backup"),
                Path.Combine(workspace, "restored.db"), password, progress);
            throw new InvalidOperationException("The requested abrupt interruption did not occur.");
        }

        using EncryptedVault vault = EncryptedVault.Unlock(database, password);
        if (operation == "save")
        {
            ReconciliationBook changed = vault.Load();
            changed.Import([Transaction("interrupted-value", -1m, "Personal")]);
            vault.Save(changed, progress: progress);
        }
        else if (operation == "backup")
        {
            vault.CreateBackup(Path.Combine(workspace, "portable.dotfinance-backup"), progress: progress);
        }
        else
        {
            return Usage();
        }
        throw new InvalidOperationException("The requested abrupt interruption did not occur.");
    }

    private static int Recovery(string workspace, ReadOnlySpan<char> password)
    {
        string database = Path.Combine(workspace, "working.db");
        string backup = Path.Combine(workspace, "portable.dotfinance-backup");
        VaultSecurityProfile profile;
        using (EncryptedVault vault = EncryptedVault.Unlock(database, password))
        {
            AssertExpected(vault.Load());
            profile = vault.SecurityProfile();
        }
        string restored = Path.Combine(workspace, "restored.db");
        DeleteManaged(restored);
        EncryptedVault.Restore(backup, restored, password);
        using (EncryptedVault vault = EncryptedVault.Unlock(restored, password)) AssertExpected(vault.Load());
        InspectManagedFiles(workspace, password);
        WriteReceipt(workspace, "recovery", database, profile);
        return 0;
    }

    private static int VerifyRestored(string workspace, ReadOnlySpan<char> password)
    {
        string restored = Path.Combine(workspace, "restored.db");
        using (EncryptedVault vault = EncryptedVault.Unlock(restored, password)) AssertExpected(vault.Load());
        EncryptedVault.DiscardInterruptedRestore(restored);
        return File.Exists(restored + ".restore-partial")
            ? throw new InvalidDataException("An interrupted restore candidate could not be discarded.")
            : 0;
    }

    private static ReconciliationBook RepresentativeBook()
    {
        FundingConfiguration configuration = new("card",
        [
            new("intermediate", "Intermediate", AccountRole.Intermediate, 80m),
            new("joint", "Joint", AccountRole.Joint, 0m),
            new("shared", "Shared", AccountRole.SharedSavings, 0m),
            new("primary", "Primary", AccountRole.PrimaryPersonal, 120m),
            new("savings", "Savings", AccountRole.PersonalSavings, 0m, 1)
        ]);
        AllocationRules rules = new(
            [new("Groceries", FundingSource.Joint), new("Travel", FundingSource.SharedSavings)],
            [], [new("Travel", "Holiday", FundingSource.SharedSavings)]);
        AccountBalance[] balances = [new("intermediate", 500m), new("joint", 1000m), new("shared", 1000m), new("primary", 1000m), new("savings", 1000m)];
        ReconciliationBook book = new(configuration, rules);
        ImportedTransaction baselineOne = Transaction("baseline-1", -100m, "Groceries");
        ImportedTransaction baselineTwo = Transaction("baseline-2", -30m, "Travel");
        book.ImportSettledBaseline([baselineOne, baselineTwo], confirmedAlreadySettled: true);
        book.Import([Transaction("completed-1", -10m, "Groceries")]);
        book.Start("reconciliation-1", new DateOnly(2026, 9, 1), 10m, balances);
        foreach (FundingTransfer transfer in book.Current!.Transfers) book.AcknowledgeTransfer(transfer.AccountId);
        book.AcknowledgeCardPaid();
        book.Import([baselineOne with { Category = "Personal" }]);
        book.Import([baselineTwo with { Notes = "Synthetic corrected note" }]);
        book.Import([
            Transaction("duplicate-1", -4m, "Personal") with { Merchant = "Fictional duplicate A" },
            Transaction("duplicate-1", -5m, "Personal") with { Merchant = "Fictional duplicate B" }
        ]);
        book.Import([Transaction("active-1", -25m, "Personal")]);
        book.Start("reconciliation-2", new DateOnly(2026, 9, 27), 25m, balances);
        book.Override("active-1", FundingSource.SharedSavings);
        book.AcceptCorrection("baseline-1", FundingSource.Personal);
        return book;
    }

    private static ImportedTransaction Transaction(string id, decimal amount, string category) => new(
        id, new DateOnly(2026, 8, 1), amount, "Fictional card", "card", "Synthetic bank", "GBP",
        category, "", amount > 0 ? "Refund" : "Purchase", "fictional", "Fictional counterparty", "",
        $"Synthetic merchant {id}", "Synthetic details", "Synthetic notes", "",
        amount > 0 ? TransactionKind.Refund : TransactionKind.Purchase);

    private static void AssertExpected(ReconciliationBook actual)
    {
        byte[] expected = JsonSerializer.SerializeToUtf8Bytes(RepresentativeBook().ExportState(),
            StorageScenarioJsonContext.Default.ReconciliationBookState);
        byte[] restored = JsonSerializer.SerializeToUtf8Bytes(actual.ExportState(),
            StorageScenarioJsonContext.Default.ReconciliationBookState);
        if (!expected.AsSpan().SequenceEqual(restored))
            throw new InvalidDataException("Restored synthetic reconciliation state differs from the expected state.");
    }

    private static void InspectManagedFiles(string workspace, ReadOnlySpan<char> password)
    {
        byte[] passwordBytes = new byte[Encoding.UTF8.GetByteCount(password)];
        Encoding.UTF8.GetBytes(password, passwordBytes);
        try
        {
            foreach (string path in Directory.GetFiles(workspace, "*", SearchOption.AllDirectories))
            {
                byte[] bytes = File.ReadAllBytes(path);
                foreach (byte[] marker in new[] { "baseline-1"u8.ToArray(), "Synthetic merchant"u8.ToArray(), passwordBytes, "SQLite format 3"u8.ToArray() })
                    if (bytes.AsSpan().IndexOf(marker) >= 0) throw new InvalidDataException("A plaintext marker was found in an app-managed file.");
            }
        }
        finally
        {
            Array.Clear(passwordBytes);
        }
    }

    private static void WriteReceipt(string workspace, string mode, string database, VaultSecurityProfile profile)
    {
        byte[] state = JsonSerializer.SerializeToUtf8Bytes(RepresentativeBook().ExportState(),
            StorageScenarioJsonContext.Default.ReconciliationBookState);
        VaultReceipt receipt = new(mode, EncryptedVault.CurrentSchemaVersion, profile.Cipher, profile.KdfIterations,
            profile.NativeVersion, profile.SQLiteVersion,
            Convert.ToHexString(SHA256.HashData(state)).ToLowerInvariant(), new FileInfo(database).Length,
            Environment.OSVersion.Platform.ToString());
        File.WriteAllText(Path.Combine(workspace, "vault-receipt.json"),
            JsonSerializer.Serialize(receipt, StorageScenarioJsonContext.Default.VaultReceipt));
    }

    private static void RequireKdfBudget(TimeSpan elapsed)
    {
        if (elapsed >= TimeSpan.FromSeconds(1))
            throw new InvalidOperationException("The production KDF exceeded the one-second native-runner budget.");
    }

    private static void DeleteManaged(string path)
    {
        File.Delete(path);
        File.Delete(path + "-journal");
        File.Delete(path + "-wal");
        File.Delete(path + "-shm");
    }

    private static int Usage()
    {
        Console.Error.WriteLine("Invalid synthetic vault validation command.");
        return 2;
    }

    private sealed class InlineProgress(Action<VaultStage> report) : IProgress<VaultStage>
    {
        public void Report(VaultStage value) => report(value);
    }
}