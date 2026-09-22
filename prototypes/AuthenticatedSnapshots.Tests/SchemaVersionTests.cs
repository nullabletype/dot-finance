using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuthenticatedSnapshots.Tests;

public sealed class SchemaVersionTests : IDisposable
{
    private const int HeaderLength = 72;
    private const int TagLength = 16;
    private const int Iterations = 200_000;
    private const string Password = "synthetic test password";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dot-finance-schema-{Guid.NewGuid():N}");

    [Fact]
    public void Supported_schema_version_is_accepted()
    {
        CreateSavedVault();
        SnapshotDocument expected = SyntheticDocument();
        RewriteGeneration(Serialize(expected));

        using VaultSession reopened = VaultSession.Unlock(_directory, Password);
        Assert.Equivalent(expected, reopened.Load(), strict: true);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void Unsupported_schema_version_is_rejected(int schemaVersion)
    {
        CreateSavedVault();
        RewriteGeneration(Serialize(SyntheticDocument() with { SchemaVersion = schemaVersion }));

        VaultAuthenticationException exception = Assert.Throws<VaultAuthenticationException>(
            () => VaultSession.Unlock(_directory, Password));

        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Contains("schema", exception.InnerException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Missing_schema_version_is_rejected()
    {
        CreateSavedVault();
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SavedAt = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
            Transactions = Array.Empty<SnapshotTransaction>(),
            Configuration = new Dictionary<string, string>(),
            Reconciliations = Array.Empty<ReconciliationRecord>(),
        }, JsonOptions);
        RewriteGeneration(plaintext);

        VaultAuthenticationException exception = Assert.Throws<VaultAuthenticationException>(
            () => VaultSession.Unlock(_directory, Password));

        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Contains("schema", exception.InnerException.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    public void Save_rejects_an_unsupported_schema_version(int schemaVersion)
    {
        using VaultSession vault = VaultSession.Create(_directory, Password);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => vault.Save(SyntheticDocument() with { SchemaVersion = schemaVersion }));

        Assert.Contains("schema", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Null_transactions_collection_is_rejected_as_a_controlled_authentication_failure()
    {
        CreateSavedVault();
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(new
        {
            SchemaVersion = 1,
            SavedAt = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
            Transactions = (SnapshotTransaction[]?)null,
            Configuration = new Dictionary<string, string>(),
            Reconciliations = Array.Empty<ReconciliationRecord>(),
        }, JsonOptions);
        RewriteGeneration(plaintext);

        VaultAuthenticationException exception = Assert.Throws<VaultAuthenticationException>(
            () => VaultSession.Unlock(_directory, Password));

        Assert.IsType<InvalidDataException>(exception.InnerException);
        Assert.Contains("payload", exception.InnerException.Message, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private void CreateSavedVault()
    {
        using VaultSession vault = VaultSession.Create(_directory, Password);
        vault.Save(SyntheticDocument());
    }

    private static byte[] Serialize(SnapshotDocument document) => JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);

    private void RewriteGeneration(byte[] plaintext)
    {
        string generationPath = Assert.Single(Directory.GetFiles(_directory, "generation-*.dfsnap"));
        byte[] envelope = File.ReadAllBytes(generationPath);
        byte[] salt = envelope.AsSpan(16, 16).ToArray();
        byte[] nonce = envelope.AsSpan(52, 12).ToArray();
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(Password), salt, Iterations, HashAlgorithmName.SHA256, 32);

        byte[] header = new byte[HeaderLength];
        envelope.AsSpan(0, 64).CopyTo(header); // Reuse magic..nonce, keeping salt, lineage, generation and nonce.
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(64), (ulong)plaintext.Length);

        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagLength];
        using (AesGcm aes = new(key, TagLength))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag, header);
        }

        using FileStream stream = new(generationPath, FileMode.Create, FileAccess.Write, FileShare.None);
        stream.Write(header);
        stream.Write(ciphertext);
        stream.Write(tag);
    }

    private static SnapshotDocument SyntheticDocument() => new(
        1,
        new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
        [new SnapshotTransaction("SYNTH-0001", new DateOnly(2026, 9, 20), 42.50m, "Fictional Card", "Example Bank", "GBP", "Groceries", "Food", "Purchase", "", "Example Market", "", "Example Market", "Synthetic details", "Synthetic note", "", "Joint", "REC-001", "Unsettled")],
        new Dictionary<string, string> { ["theme"] = "System" },
        [new ReconciliationRecord("REC-001", new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero), "Draft", 1200m, new Dictionary<string, decimal> { ["Joint"] = 450m }, [])]);
}