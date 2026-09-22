using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

using AuthenticatedSnapshots;

const int transactionCount = 100_000;
const double searchLimitMilliseconds = 1_000;
const double unlockLimitMilliseconds = 3_000;
const double saveLimitMilliseconds = 3_000;
const double backupLimitMilliseconds = 15_000;
const string password = "fictional benchmark password — not user data";

if (args.Length > 0 && args[0] == "--fault")
{
    Environment.ExitCode = RunFaultCommand(args[1..]);
    return;
}

string outputPath = args.Length == 2 && args[0] == "--output"
    ? Path.GetFullPath(args[1])
    : Path.GetFullPath(Path.Combine("artifacts", "authenticated-snapshots", $"{Environment.OSVersion.Platform}-{Environment.ProcessId}.json"));
string root = Path.Combine(Path.GetTempPath(), $"dot-finance-snapshot-trial-{Guid.NewGuid():N}");
string vaultPath = Path.Combine(root, "vault");
string backupPath = Path.Combine(root, "portable.dfsnap");
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
Directory.CreateDirectory(root);
try
{
    SnapshotDocument document = GenerateDocument();
    long memoryBefore = GC.GetTotalMemory(forceFullCollection: true);
    double saveMilliseconds;
    using (VaultSession vault = VaultSession.Create(vaultPath, password))
    {
        saveMilliseconds = Measure(() => vault.Save(document));
    }

    double unlockMilliseconds;
    double backupMilliseconds;
    Dictionary<string, double> searchP95 = new(StringComparer.Ordinal);
    using (VaultSession vault = MeasureResult(() => VaultSession.Unlock(vaultPath, password), out unlockMilliseconds))
    {
        string[] searches =
        [
            "2099-12-31", "99999.99", "Needle Account", "Needle Bank", "Needle Category", "Needle Type",
            "Needle Counterparty", "Needle Merchant", "Needle Notes", "REC-NEEDLE", "Completed"
        ];
        foreach (string query in searches)
        {
            double[] samples = new double[12];
            for (int iteration = 0; iteration < samples.Length; iteration++)
            {
                samples[iteration] = Measure(() =>
                {
                    SnapshotTransaction result = vault.Search(query).Single();
                    if (!string.Equals(result.Id, "SYNTH-099999", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Search '{query}' returned the wrong transaction.");
                    }
                });
            }

            Array.Sort(samples);
            searchP95[query] = samples[(int)Math.Ceiling(samples.Length * 0.95) - 1];
        }

        backupMilliseconds = Measure(() => vault.CreateBackup(backupPath));
    }

    long memoryAfter = GC.GetTotalMemory(forceFullCollection: true);
    long vaultBytes = Directory.GetFiles(vaultPath, "generation-*.dfsnap").Sum(path => new FileInfo(path).Length);
    long backupBytes = new FileInfo(backupPath).Length;
    TrialEvidence evidence = new(
        DateTimeOffset.UtcNow,
        Environment.OSVersion.ToString(),
        System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString(),
        Environment.Version.ToString(),
        transactionCount,
        saveMilliseconds,
        unlockMilliseconds,
        backupMilliseconds,
        searchP95,
        memoryAfter - memoryBefore,
        vaultBytes,
        backupBytes,
        searchP95.Values.All(value => value < searchLimitMilliseconds)
            && unlockMilliseconds < unlockLimitMilliseconds
            && saveMilliseconds < saveLimitMilliseconds
            && backupMilliseconds < backupLimitMilliseconds);
    File.WriteAllText(outputPath, JsonSerializer.Serialize(evidence, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Snapshot trial {(evidence.Passed ? "passed" : "failed")}: {outputPath}");
    if (!evidence.Passed)
    {
        Environment.ExitCode = 1;
    }
}
finally
{
    Directory.Delete(root, recursive: true);
}

static SnapshotDocument GenerateDocument()
{
    SnapshotTransaction[] transactions = new SnapshotTransaction[transactionCount];
    DateOnly start = new(2006, 1, 1);
    for (int index = 0; index < transactions.Length; index++)
    {
        bool needle = index == transactions.Length - 1;
        transactions[index] = new SnapshotTransaction(
            $"SYNTH-{index:D6}",
            needle ? new DateOnly(2099, 12, 31) : start.AddDays(index % 7305),
            needle ? 99999.99m : (index % 25_000) / 100m,
            needle ? "Needle Account" : $"Fictional Account {index % 4}",
            needle ? "Needle Bank" : $"Example Bank {index % 3}",
            "GBP",
            needle ? "Needle Category" : $"Category {index % 20}",
            $"Subcategory {index % 50}",
            needle ? "Needle Type" : "Purchase",
            $"synthetic-tag-{index % 10}",
            needle ? "Needle Counterparty" : $"Counterparty {index % 2_000}",
            $"Custom {index % 1_000}",
            needle ? "Needle Merchant" : $"Merchant {index % 5_000}",
            $"Fictional details {index}",
            needle ? "Needle Notes" : $"Synthetic note {index % 300}",
            string.Empty,
            (index % 3) switch { 0 => "Joint", 1 => "Shared Savings", _ => "Personal" },
            needle ? "REC-NEEDLE" : $"REC-{index / 500:D4}",
            needle ? "Completed" : index % 2 == 0 ? "Settled" : "Unsettled");
    }

    ReconciliationRecord[] reconciliations =
    [
        .. Enumerable.Range(0, 200)
            .Select(index => new ReconciliationRecord(
                $"REC-{index:D4}",
                new DateTimeOffset(2006, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(index * 36),
                "Completed",
                1200m + index,
                new Dictionary<string, decimal> { ["Joint"] = 450m, ["Shared Savings"] = 250m },
                ["Joint transferred", "Card paid"]))
    ];
    return new SnapshotDocument(
        1,
        DateTimeOffset.UtcNow,
        transactions,
        new Dictionary<string, string> { ["theme"] = "System", ["fixture"] = "100k synthetic transactions" },
        reconciliations);
}

static double Measure(Action action)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    action();
    return stopwatch.Elapsed.TotalMilliseconds;
}

static T MeasureResult<T>(Func<T> action, out double milliseconds)
{
    Stopwatch stopwatch = Stopwatch.StartNew();
    T result = action();
    milliseconds = stopwatch.Elapsed.TotalMilliseconds;
    return result;
}

// Deterministic synthetic constants for the abrupt-interruption fault matrix. These are
// shared with scripts/snapshots-fault-smoke.mjs and never contain user data.
const string faultPassword = "synthetic fault-injection password";
const string faultNewPassword = "synthetic new password";

static int RunFaultCommand(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("Usage: --fault <init|save|backup|restore|password-change|verify> ...");
        return 2;
    }

    switch (args[0])
    {
        case "init" when args.Length == 2:
            return FaultInit(args[1]);
        case "save" when args.Length == 2:
            return FaultSave(args[1]);
        case "backup" when args.Length == 3:
            return FaultBackup(args[1], args[2]);
        case "restore" when args.Length == 3:
            return FaultRestore(args[1], args[2]);
        case "password-change" when args.Length == 2:
            return FaultPasswordChange(args[1]);
        case "verify" when args.Length == 3:
            return FaultVerify(args[1], args[2]);
        default:
            Console.Error.WriteLine($"Unknown fault subcommand or argument count: {string.Join(' ', args)}");
            return 2;
    }
}

static int FaultInit(string vaultDirectory)
{
    using VaultSession vault = VaultSession.Create(vaultDirectory, faultPassword);
    vault.Save(FaultDocument());
    return 0;
}

static int FaultSave(string vaultDirectory)
{
    using VaultSession vault = VaultSession.Unlock(vaultDirectory, faultPassword);
    vault.Save(FaultDocument());
    return 0;
}

static int FaultBackup(string vaultDirectory, string backupPath)
{
    using VaultSession vault = VaultSession.Unlock(vaultDirectory, faultPassword);
    vault.CreateBackup(backupPath);
    return 0;
}

static int FaultRestore(string backupPath, string vaultDirectory)
{
    VaultSession.Restore(backupPath, vaultDirectory, faultPassword);
    return 0;
}

static int FaultPasswordChange(string vaultDirectory)
{
    using VaultSession vault = VaultSession.Unlock(vaultDirectory, faultPassword);
    vault.ChangePassword(faultNewPassword);
    return 0;
}

static int FaultVerify(string vaultDirectory, string password)
{
    using VaultSession vault = VaultSession.Unlock(vaultDirectory, password);
    SnapshotDocument document = vault.Load();
    SnapshotTransaction first = document.Transactions[0];
    FaultVerifyReceipt receipt = new(
        document.Transactions.Count,
        document.Reconciliations.Count,
        first.Id,
        first.Amount,
        first.Account,
        document.Configuration["theme"],
        FaultDocumentChecksum(document));
    Console.WriteLine(JsonSerializer.Serialize(receipt));
    return 0;
}

static string FaultDocumentChecksum(SnapshotDocument document)
{
    byte[] json = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions(JsonSerializerDefaults.General));
    return Convert.ToHexString(SHA256.HashData(json));
}

static SnapshotDocument FaultDocument() => new(
    1,
    new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
    [
        new SnapshotTransaction(
            "FAULT-0001",
            new DateOnly(2026, 9, 20),
            42.50m,
            "Fictional Card",
            "Example Bank",
            "GBP",
            "Groceries",
            "Food",
            "Purchase",
            string.Empty,
            "Example Market",
            string.Empty,
            "Example Market",
            "Synthetic details",
            "Synthetic note",
            string.Empty,
            "Joint",
            "REC-001",
            "Unsettled")
    ],
    new Dictionary<string, string> { ["theme"] = "System" },
    [
        new ReconciliationRecord(
            "REC-001",
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
            "Draft",
            1200m,
            new Dictionary<string, decimal> { ["Joint"] = 450m },
            [])
    ]);

internal sealed record FaultVerifyReceipt(
    int Transactions,
    int Reconciliations,
    string FirstTransactionId,
    decimal FirstTransactionAmount,
    string FirstTransactionAccount,
    string ConfigurationTheme,
    string DocumentChecksum);

internal sealed record TrialEvidence(
    DateTimeOffset RecordedAt,
    string OperatingSystem,
    string Architecture,
    string DotNet,
    int Transactions,
    double SaveMilliseconds,
    double UnlockMilliseconds,
    double BackupMilliseconds,
    IReadOnlyDictionary<string, double> SearchP95Milliseconds,
    long RetainedManagedMemoryBytes,
    long VaultBytes,
    long BackupBytes,
    bool Passed);