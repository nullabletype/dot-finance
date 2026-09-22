using System.Buffers.Binary;
using System.Security.Cryptography;

namespace AuthenticatedSnapshots.Tests;

public sealed class HostileInputTests : IDisposable
{
    private const int HeaderLength = 72;
    private const int TagLength = 16;
    private const string Password = "synthetic test password";
    private const ulong MaximumPlaintextLength = 512UL * 1024 * 1024;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"dot-finance-hostile-{Guid.NewGuid():N}");

    [Fact]
    public void Very_long_password_round_trips()
    {
        string password = new('x', 100_000);
        SnapshotDocument expected = SyntheticDocument();

        using (VaultSession vault = VaultSession.Create(_directory, password))
        {
            vault.Save(expected);
        }

        using VaultSession reopened = VaultSession.Unlock(_directory, password);
        Assert.Equivalent(expected, reopened.Load(), strict: true);
    }

    [Fact]
    public void Unicode_password_with_emoji_combining_marks_and_surrogate_pairs_round_trips()
    {
        string password = "Pässwörð 🔒 e\u0301 𐍈 ✨";
        SnapshotDocument expected = SyntheticDocument();

        using (VaultSession vault = VaultSession.Create(_directory, password))
        {
            vault.Save(expected);
        }

        using VaultSession reopened = VaultSession.Unlock(_directory, password);
        Assert.Equivalent(expected, reopened.Load(), strict: true);
    }

    [Fact]
    public void Passwords_are_not_unicode_normalized()
    {
        // U+00E9 (precomposed é) and U+0065 U+0301 (e + combining acute) are
        // normalization-equivalent but must be treated as different passwords.
        const string composed = "caf\u00E9";
        const string decomposed = "cafe\u0301";

        using (VaultSession vault = VaultSession.Create(_directory, composed))
        {
            vault.Save(SyntheticDocument());
        }

        using (VaultSession reopened = VaultSession.Unlock(_directory, composed))
        {
            _ = reopened.Load();
        }

        Assert.Throws<VaultAuthenticationException>(() => VaultSession.Unlock(_directory, decomposed));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData("\r\n")]
    public void Empty_or_whitespace_password_is_rejected_on_create(string password)
    {
        ArgumentException exception = Assert.Throws<ArgumentException>(() => VaultSession.Create(_directory, password));

        Assert.Equal("password", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Empty_or_whitespace_password_is_rejected_on_unlock(string password)
    {
        using (VaultSession vault = VaultSession.Create(_directory, Password))
        {
            vault.Save(SyntheticDocument());
        }

        ArgumentException exception = Assert.Throws<ArgumentException>(() => VaultSession.Unlock(_directory, password));

        Assert.Equal("password", exception.ParamName);
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(uint.MaxValue)]
    public void Hostile_iteration_count_is_rejected_before_key_derivation(uint hostileIterations)
    {
        string generationPath = SavedGenerationPath();
        byte[] bytes = File.ReadAllBytes(generationPath);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(12), hostileIterations);
        File.WriteAllBytes(generationPath, bytes);

        VaultAuthenticationException exception = Assert.Throws<VaultAuthenticationException>(
            () => VaultSession.Unlock(_directory, Password));

        // Rejected by the header check (InvalidDataException), not after PBKDF2 and AES-GCM.
        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public void Ciphertext_length_larger_than_the_real_file_is_rejected()
    {
        string generationPath = SavedGenerationPath();
        byte[] bytes = File.ReadAllBytes(generationPath);
        ulong actualLength = (ulong)(bytes.Length - HeaderLength - TagLength);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(64), actualLength + 1);
        File.WriteAllBytes(generationPath, bytes);

        VaultAuthenticationException exception = Assert.Throws<VaultAuthenticationException>(
            () => VaultSession.Unlock(_directory, Password));

        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public void Ciphertext_length_beyond_the_maximum_plaintext_is_rejected()
    {
        string generationPath = SavedGenerationPath();
        byte[] bytes = File.ReadAllBytes(generationPath);
        BinaryPrimitives.WriteUInt64BigEndian(bytes.AsSpan(64), MaximumPlaintextLength + 1);
        File.WriteAllBytes(generationPath, bytes);

        VaultAuthenticationException exception = Assert.Throws<VaultAuthenticationException>(
            () => VaultSession.Unlock(_directory, Password));

        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public void Truncated_snapshot_is_rejected()
    {
        string generationPath = SavedGenerationPath();
        byte[] bytes = File.ReadAllBytes(generationPath);
        File.WriteAllBytes(generationPath, bytes[..^TagLength]);

        VaultAuthenticationException exception = Assert.Throws<VaultAuthenticationException>(
            () => VaultSession.Unlock(_directory, Password));

        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public void Appended_trailing_bytes_are_rejected()
    {
        string generationPath = SavedGenerationPath();
        byte[] bytes = File.ReadAllBytes(generationPath);
        File.WriteAllBytes(generationPath, [.. bytes, 0x5A]);

        VaultAuthenticationException exception = Assert.Throws<VaultAuthenticationException>(
            () => VaultSession.Unlock(_directory, Password));

        Assert.IsType<InvalidDataException>(exception.InnerException);
    }

    [Fact]
    public void Authentication_tag_corruption_is_rejected_as_an_authentication_failure()
    {
        string generationPath = SavedGenerationPath();
        byte[] bytes = File.ReadAllBytes(generationPath);
        bytes[^8] ^= 0xFF; // Corrupt a byte inside the 16-byte GCM tag region.
        File.WriteAllBytes(generationPath, bytes);

        VaultAuthenticationException exception = Assert.Throws<VaultAuthenticationException>(
            () => VaultSession.Unlock(_directory, Password));

        // Unlike header-level rejects, this reaches AES-GCM and fails authentication.
        Assert.IsType<CryptographicException>(exception.InnerException, exactMatch: false);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private string SavedGenerationPath()
    {
        using (VaultSession vault = VaultSession.Create(_directory, Password))
        {
            vault.Save(SyntheticDocument());
        }

        return Assert.Single(Directory.GetFiles(_directory, "generation-*.dfsnap"));
    }

    private static SnapshotDocument SyntheticDocument() => new(
        1,
        new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
        [new SnapshotTransaction("SYNTH-0001", new DateOnly(2026, 9, 20), 42.50m, "Fictional Card", "Example Bank", "GBP", "Groceries", "Food", "Purchase", "", "Example Market", "", "Example Market", "Synthetic details", "Synthetic note", "", "Joint", "REC-001", "Unsettled")],
        new Dictionary<string, string> { ["theme"] = "System" },
        [new ReconciliationRecord("REC-001", new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero), "Draft", 1200m, new Dictionary<string, decimal> { ["Joint"] = 450m }, [])]);
}