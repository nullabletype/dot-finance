using System.Text.Json;

namespace AuthenticatedSnapshots;

public sealed record PendingBackupEntry(Guid VaultId, uint Generation, string TargetPath);

// A corrupt or unreadable pending-backups journal is preserved (not deleted) so it can
// be inspected, and Read throws for such a journal so that queueing fails loudly instead
// of silently discarding pending backups. Only a missing or empty journal is treated as
// empty.
public static class PendingBackupJournal
{
    public const string FileName = "pending-backups.json";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    public static IReadOnlyList<PendingBackupEntry> Read(string directory)
    {
        string path = Path.Combine(directory, FileName);
        if (!File.Exists(path))
        {
            return [];
        }

        byte[] json = File.ReadAllBytes(path);
        return json.Length == 0
            ? []
            : JsonSerializer.Deserialize<List<PendingBackupEntry>>(json, JsonOptions) ?? [];
    }

    public static void Write(string directory, IReadOnlyList<PendingBackupEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        string path = Path.Combine(directory, FileName);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(entries, JsonOptions);
        string temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.journal-partial");
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(json);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }
}