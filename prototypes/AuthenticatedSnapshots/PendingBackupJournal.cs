using System.Text.Json;

namespace AuthenticatedSnapshots;

public sealed record PendingBackupEntry(Guid VaultId, uint Generation, string TargetPath);

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

        try
        {
            byte[] json = File.ReadAllBytes(path);
            return json.Length == 0
                ? []
                : JsonSerializer.Deserialize<List<PendingBackupEntry>>(json, JsonOptions) ?? [];
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            // A corrupt or unparseable journal is treated as empty so that it never
            // blocks the vault from opening. The damaged file is deliberately left in
            // place (not deleted) for later inspection; the next successful write
            // atomically replaces it.
            return [];
        }
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