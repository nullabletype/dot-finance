using System.Diagnostics;
using System.Text.Json;

using AuthenticatedSnapshots;

const int TransactionCount = 100_000;
const double SearchLimitMilliseconds = 1_000;
const double UnlockLimitMilliseconds = 3_000;
const double SaveLimitMilliseconds = 3_000;
const double BackupLimitMilliseconds = 15_000;
const string Password = "fictional benchmark password — not user data";

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
    using (VaultSession vault = VaultSession.Create(vaultPath, Password))
    {
        saveMilliseconds = Measure(() => vault.Save(document));
    }

    double unlockMilliseconds;
    double backupMilliseconds;
    Dictionary<string, double> searchP95 = new(StringComparer.Ordinal);
    using (VaultSession vault = MeasureResult(() => VaultSession.Unlock(vaultPath, Password), out unlockMilliseconds))
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
        TransactionCount,
        saveMilliseconds,
        unlockMilliseconds,
        backupMilliseconds,
        searchP95,
        memoryAfter - memoryBefore,
        vaultBytes,
        backupBytes,
        searchP95.Values.All(value => value < SearchLimitMilliseconds)
            && unlockMilliseconds < UnlockLimitMilliseconds
            && saveMilliseconds < SaveLimitMilliseconds
            && backupMilliseconds < BackupLimitMilliseconds);
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
    SnapshotTransaction[] transactions = new SnapshotTransaction[TransactionCount];
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

    ReconciliationRecord[] reconciliations = Enumerable.Range(0, 200)
        .Select(index => new ReconciliationRecord(
            $"REC-{index:D4}",
            new DateTimeOffset(2006, 1, 1, 12, 0, 0, TimeSpan.Zero).AddDays(index * 36),
            "Completed",
            1200m + index,
            new Dictionary<string, decimal> { ["Joint"] = 450m, ["Shared Savings"] = 250m },
            ["Joint transferred", "Card paid"]))
        .ToArray();
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