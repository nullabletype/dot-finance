using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopShell;

internal static class PersistenceScenario
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private enum CycleMode
    {
        Initialize,
        Continue,
        Verify
    }

    public static int RunCommand(string[] args)
    {
        if (args.Length is < 4 or > 5)
        {
            Console.Error.WriteLine("Usage: --persistence-cycle <initialize|continue|verify> <workspace> <fixture> [input-backup]");
            return 2;
        }

        string password = Environment.GetEnvironmentVariable("DOT_FINANCE_PROTOTYPE_PASSWORD")
            ?? throw new InvalidOperationException("DOT_FINANCE_PROTOTYPE_PASSWORD is required and is used only for this process.");
        string? inputBackup = args.Length == 5 ? args[4] : null;
        if (!Enum.TryParse(args[1], true, out CycleMode mode))
        {
            throw new ArgumentException($"Unknown persistence cycle mode: {args[1]}");
        }

        Run(mode, args[2], args[3], inputBackup, password);
        return 0;
    }

    public static int RunInterruptionCommand(string[] args)
    {
        if (args.Length != 4 || args[1] is not ("save" or "backup"))
        {
            Console.Error.WriteLine("Usage: --persistence-interrupt <save|backup> <workspace> <fixture>");
            return 2;
        }

        string password = RequiredPassword();
        string databasePath = Path.Combine(args[2], "working.db");
        using PrototypeVault vault = PrototypeVault.Unlock(databasePath, password);
        if (args[1] == "save")
        {
            PrototypeState expected = SyntheticEmmaImport.Read(args[3]);
            vault.Save(new PrototypeState
            {
                ImportedAt = expected.ImportedAt,
                AllocationStatus = "This interrupted value must not commit",
                Transactions = expected.Transactions
            });
        }
        else
        {
            vault.CreateBackup(Path.Combine(args[2], "portable.dotfinance-backup"));
        }

        throw new InvalidOperationException("The requested abrupt interruption did not occur.");
    }

    public static int RunRecoveryCheckCommand(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine("Usage: --persistence-check-recovery <workspace> <fixture>");
            return 2;
        }

        string password = RequiredPassword();
        string databasePath = Path.Combine(args[1], "working.db");
        string backupPath = Path.Combine(args[1], "portable.dotfinance-backup");
        PrototypeState expected = SyntheticEmmaImport.Read(args[2]);
        using (PrototypeVault vault = PrototypeVault.Unlock(databasePath, password))
        {
            AssertExpected(expected, vault.Load());
        }

        string restoredPath = Path.Combine(args[1], "recovery-check.db");
        PrototypeVault.Restore(backupPath, restoredPath, password);
        using (PrototypeVault restored = PrototypeVault.Unlock(restoredPath, password))
        {
            AssertExpected(expected, restored.Load());
        }

        InspectManagedFiles(args[1], expected, password);
        return 0;
    }

    private static void Run(CycleMode mode, string workspace, string fixturePath, string? inputBackup, string password)
    {
        Directory.CreateDirectory(workspace);
        string databasePath = Path.Combine(workspace, "working.db");
        string backupPath = Path.Combine(workspace, "portable.dotfinance-backup");
        PrototypeState expected = SyntheticEmmaImport.Read(fixturePath);

        if (mode == CycleMode.Initialize)
        {
            File.Delete(databasePath);
            using PrototypeVault vault = PrototypeVault.Create(databasePath, password);
            vault.Save(expected);
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inputBackup);
            PrototypeVault.Restore(inputBackup, databasePath, password);
        }

        using (PrototypeVault reopened = PrototypeVault.Unlock(databasePath, password))
        {
            AssertExpected(expected, reopened.Load());

            if (mode != CycleMode.Verify)
            {
                reopened.CreateBackup(backupPath);
            }
        }

        int inspectedFileCount = InspectManagedFiles(workspace, expected, password);

        string stateJson = JsonSerializer.Serialize(expected);
        File.WriteAllText(Path.Combine(workspace, "persistence-evidence.json"), JsonSerializer.Serialize(new
        {
            mode = mode.ToString(),
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            transactionCount = expected.Transactions.Count,
            stateSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stateJson))).ToLowerInvariant(),
            appManagedFilesInspectedForPlaintext = inspectedFileCount,
            passwordPersisted = false,
            timestampUtc = DateTimeOffset.UtcNow,
            scope = "Synthetic fixture round trip and byte-pattern inspection; not a forensic memory, swap, filesystem or cryptographic audit."
        }, JsonOptions));
    }

    public static string RunLocalRoundTrip(string workspace, string fixturePath, string password)
    {
        Run(CycleMode.Initialize, workspace, fixturePath, null, password);
        string backupPath = Path.Combine(workspace, "portable.dotfinance-backup");
        string restoredPath = Path.Combine(workspace, "restored.db");
        PrototypeVault.Restore(backupPath, restoredPath, password);
        using PrototypeVault restored = PrototypeVault.Unlock(restoredPath, password);
        PrototypeState state = restored.Load();
        return $"Encrypted reopen and backup restore passed for {state.Transactions.Count} synthetic transactions.";
    }

    private static int InspectManagedFiles(string workspace, PrototypeState state, string password)
    {
        string[] paths = Directory.GetFiles(workspace, "*", SearchOption.AllDirectories);
        foreach (string path in paths)
        {
            AssertNoPlaintext(path, state, password);
        }

        return paths.Length;
    }

    private static void AssertNoPlaintext(string path, PrototypeState state, string password)
    {
        byte[] bytes = File.ReadAllBytes(path);
        IEnumerable<string> forbidden = state.Transactions.SelectMany(transaction => new[] { transaction.Id, transaction.Merchant })
            .Append(password)
            .Append("SQLite format 3");
        foreach (string value in forbidden.Where(value => !string.IsNullOrEmpty(value)))
        {
            if (bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(value)) >= 0)
            {
                throw new InvalidDataException($"Plaintext marker found in app-managed file {Path.GetFileName(path)}.");
            }
        }
    }

    private static void AssertExpected(PrototypeState expected, PrototypeState actual)
    {
        if (!expected.Equals(actual))
        {
            throw new InvalidDataException("Restored state differs from the synthetic imported state.");
        }
    }

    private static string RequiredPassword() => Environment.GetEnvironmentVariable("DOT_FINANCE_PROTOTYPE_PASSWORD")
        ?? throw new InvalidOperationException("DOT_FINANCE_PROTOTYPE_PASSWORD is required and is used only for this process.");
}