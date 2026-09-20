using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DesktopShell;

internal static class PersistenceScenario
{
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
        Run(args[1], args[2], args[3], inputBackup, password);
        return 0;
    }

    public static void Run(string mode, string workspace, string fixturePath, string? inputBackup, string password)
    {
        Directory.CreateDirectory(workspace);
        string databasePath = Path.Combine(workspace, "working.db");
        string backupPath = Path.Combine(workspace, "portable.dotfinance-backup");
        PrototypeState expected = SyntheticEmmaImport.Read(fixturePath);

        if (string.Equals(mode, "initialize", StringComparison.Ordinal))
        {
            File.Delete(databasePath);
            using (PrototypeVault vault = PrototypeVault.Create(databasePath, password))
            {
                vault.Save(expected);
            }
        }
        else if (string.Equals(mode, "continue", StringComparison.Ordinal) || string.Equals(mode, "verify", StringComparison.Ordinal))
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(inputBackup);
            PrototypeVault.Restore(inputBackup, databasePath, password);
        }
        else
        {
            throw new ArgumentException($"Unknown persistence cycle mode: {mode}", nameof(mode));
        }

        using (PrototypeVault reopened = PrototypeVault.Unlock(databasePath, password))
        {
            if (!expected.Equals(reopened.Load()))
            {
                throw new InvalidDataException("Restored state differs from the synthetic imported state.");
            }

            if (!string.Equals(mode, "verify", StringComparison.Ordinal))
            {
                reopened.CreateBackup(backupPath);
            }
        }

        AssertNoPlaintext(databasePath, expected, password);
        if (File.Exists(backupPath))
        {
            AssertNoPlaintext(backupPath, expected, password);
        }

        string stateJson = JsonSerializer.Serialize(expected);
        File.WriteAllText(Path.Combine(workspace, "persistence-evidence.json"), JsonSerializer.Serialize(new
        {
            mode,
            os = RuntimeInformation.OSDescription,
            architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            framework = RuntimeInformation.FrameworkDescription,
            transactionCount = expected.Transactions.Count,
            stateSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(stateJson))).ToLowerInvariant(),
            encryptedFilesInspectedForPlaintext = File.Exists(backupPath) ? 2 : 1,
            passwordPersisted = false,
            timestampUtc = DateTimeOffset.UtcNow,
            scope = "Synthetic fixture round trip and byte-pattern inspection; not a forensic memory, swap, filesystem or cryptographic audit."
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string RunLocalRoundTrip(string workspace, string fixturePath, string password)
    {
        Run("initialize", workspace, fixturePath, null, password);
        string backupPath = Path.Combine(workspace, "portable.dotfinance-backup");
        string restoredPath = Path.Combine(workspace, "restored.db");
        PrototypeVault.Restore(backupPath, restoredPath, password);
        using PrototypeVault restored = PrototypeVault.Unlock(restoredPath, password);
        PrototypeState state = restored.Load();
        return $"Encrypted restart and backup restore passed for {state.Transactions.Count} synthetic transactions.";
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
                throw new InvalidDataException($"Plaintext marker found in app-managed encrypted file {Path.GetFileName(path)}.");
            }
        }
    }
}
