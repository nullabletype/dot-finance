using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AuthenticatedSnapshots;

public sealed class VaultAuthenticationException(string message, Exception? innerException = null) : Exception(message, innerException);

public sealed record SnapshotBackupReceipt(Guid VaultId, uint Generation);

public sealed class VaultSession : IDisposable
{
    private const int FormatVersion = 1;
    private const int Iterations = 200_000;
    private const int SaltLength = 16;
    private const int NonceLength = 12;
    private const int TagLength = 16;
    private const int HeaderLength = 72;
    private const long MaximumPlaintextLength = 512L * 1024 * 1024;
    private const int SupportedSchemaVersion = 1;
    private static ReadOnlySpan<byte> Magic => "DFSNAP01"u8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.General);
    private readonly string _directory;
    private readonly FileStream _writerLock;
    private byte[] _key;
    private byte[] _salt;
    private readonly Guid _vaultId;
    private uint _generation;
    private SnapshotDocument? _document;
    private IReadOnlyList<IndexedTransaction> _searchIndex = [];
    private bool _disposed;

    // Disposable experiment instrumentation. Null in production; set by the trial to
    // receive per-phase save timings. Each callback carries a phase name and elapsed ms.
    public static Action<string, double>? SavePhaseTiming { get; set; }

    private VaultSession(string directory, FileStream writerLock, byte[] key, byte[] salt, Guid vaultId, uint generation, SnapshotDocument? document)
    {
        _directory = directory;
        _writerLock = writerLock;
        _key = key;
        _salt = salt;
        _vaultId = vaultId;
        _generation = generation;
        _document = document;
        _searchIndex = document is null ? [] : BuildSearchIndex(document.Transactions);
    }

    public static VaultSession Create(string directory, ReadOnlySpan<char> password)
    {
        ValidatePassword(password);
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        FileStream writerLock = AcquireWriterLock(directory);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltLength);
        try
        {
            return new VaultSession(directory, writerLock, DeriveKey(password, salt), salt, Guid.NewGuid(), 0, null);
        }
        catch
        {
            writerLock.Dispose();
            throw;
        }
    }

    public static VaultSession Unlock(string directory, ReadOnlySpan<char> password)
    {
        ValidatePassword(password);
        directory = Path.GetFullPath(directory);
        FileStream writerLock = AcquireWriterLock(directory);
        try
        {
            string activePath = Path.Combine(directory, "active-generation");
            string[] paths = File.Exists(activePath)
                ? [ReadActiveGenerationPath(directory)]
                : Directory.GetFiles(directory, "generation-*.dfsnap");
            if (paths.Length == 0)
            {
                throw new VaultAuthenticationException("The password is incorrect or the authenticated vault is damaged.");
            }

            ParsedSnapshot first = ReadHeader(paths[0]);
            byte[] key = DeriveKey(password, first.Salt);
            try
            {
                List<ParsedSnapshot> snapshots = [];
                HashSet<uint> generations = [];
                foreach (string path in paths)
                {
                    ParsedSnapshot snapshot = ReadAndAuthenticate(path, key);
                    if (snapshot.VaultId != first.VaultId || !snapshot.Salt.AsSpan().SequenceEqual(first.Salt) || !generations.Add(snapshot.Generation))
                    {
                        throw new VaultAuthenticationException("The password is incorrect or the authenticated vault is damaged.");
                    }

                    snapshots.Add(snapshot);
                }

                ParsedSnapshot latest = snapshots.MaxBy(snapshot => snapshot.Generation)!;
                VaultSession session = new(directory, writerLock, key, first.Salt, first.VaultId, latest.Generation, latest.Document);
                try
                {
                    session.PublishPendingBackups();
                }
                catch
                {
                    // Best-effort only: retrying a pending automatic backup must never
                    // block the vault from opening. Unexpected failures leave entries in
                    // the journal for a later session to retry.
                }

                return session;
            }
            catch
            {
                CryptographicOperations.ZeroMemory(key);
                throw;
            }
        }
        catch (VaultAuthenticationException)
        {
            writerLock.Dispose();
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException)
        {
            writerLock.Dispose();
            throw new VaultAuthenticationException("The password is incorrect or the authenticated vault is damaged.", exception);
        }
    }

    public SnapshotDocument Load()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _document ?? throw new InvalidOperationException("The authenticated vault does not contain state.");
    }

    public IReadOnlyList<SnapshotTransaction> Search(string query)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return [.. _searchIndex
            .Where(entry => entry.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Transaction)];
    }

    public void Save(SnapshotDocument document)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(document);
        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidOperationException("The document uses an unsupported schema version.");
        }

        EnsureGenerationUnchanged();
        uint nextGeneration = NextGeneration();
        byte[] plaintext = TimeSavePhase("save-serialize", () => JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions));
        if (plaintext.LongLength > MaximumPlaintextLength)
        {
            throw new InvalidOperationException("The snapshot exceeds the supported size.");
        }

        byte[] nonce = UniqueNonce();
        byte[] header = CreateHeader(nextGeneration, nonce, plaintext.LongLength);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagLength];
        try
        {
            TimeSavePhase("save-encrypt", () =>
            {
                using AesGcm aes = new(_key, TagLength);
                aes.Encrypt(nonce, plaintext, ciphertext, tag, header);
            });

            Publish(nextGeneration, header, ciphertext, tag);
            CrashIfRequested("save-before-pointer"); // Disposable fault-injection hook; no-op unless DFSNAP_CRASH_POINT is set.
            TimeSavePhase("save-pointer", () => PublishActiveGeneration(_directory, nextGeneration));
            _generation = nextGeneration;
            _document = document;
            _searchIndex = BuildSearchIndex(document.Transactions);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    public void ChangePassword(ReadOnlySpan<char> newPassword)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ValidatePassword(newPassword);
        SnapshotDocument document = Load();
        EnsureGenerationUnchanged();
        if (PendingBackupJournal.Read(_directory).Any(entry => entry.VaultId == _vaultId))
        {
            throw new InvalidOperationException("Publish pending automatic backups before changing the password.");
        }

        byte[] newSalt = RandomNumberGenerator.GetBytes(SaltLength);
        byte[] newKey = DeriveKey(newPassword, newSalt);
        byte[] plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
        uint nextGeneration = NextGeneration();
        byte[] header = CreateHeader(nextGeneration, nonce, plaintext.LongLength, newSalt);
        byte[] ciphertext = new byte[plaintext.Length];
        byte[] tag = new byte[TagLength];
        try
        {
            using (AesGcm aes = new(newKey, TagLength))
            {
                aes.Encrypt(nonce, plaintext, ciphertext, tag, header);
            }

            Publish(nextGeneration, header, ciphertext, tag);
            CrashIfRequested("password-change-before-pointer"); // Disposable fault-injection hook; no-op unless DFSNAP_CRASH_POINT is set.
            PublishActiveGeneration(_directory, nextGeneration);
            byte[] oldKey = _key;
            _key = newKey;
            newKey = [];
            _salt = newSalt;
            _generation = nextGeneration;
            CryptographicOperations.ZeroMemory(oldKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
            CryptographicOperations.ZeroMemory(newKey);
        }
    }

    public SnapshotBackupReceipt CreateBackup(string backupPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_generation == 0)
        {
            throw new InvalidOperationException("Save vault state before creating a backup.");
        }

        backupPath = Path.GetFullPath(backupPath);
        Directory.CreateDirectory(Path.GetDirectoryName(backupPath)!);
        if (File.Exists(backupPath))
        {
            ParsedSnapshot existing = ReadAndAuthenticate(backupPath, _key);
            return existing.VaultId == _vaultId && existing.Generation == _generation
                ? new SnapshotBackupReceipt(_vaultId, _generation)
                : throw new IOException("The backup path already contains a different authenticated generation.");
        }

        string temporaryPath = backupPath + $".{Guid.NewGuid():N}.partial";
        try
        {
            CopyAndFlush(GenerationPath(_generation), temporaryPath);
            ParsedSnapshot staged = ReadAndAuthenticate(temporaryPath, _key);
            if (staged.VaultId != _vaultId || staged.Generation != _generation)
            {
                throw new InvalidDataException("The staged backup is not the current vault generation.");
            }

            CrashIfRequested("backup-before-rename"); // Disposable fault-injection hook; no-op unless DFSNAP_CRASH_POINT is set.
            File.Move(temporaryPath, backupPath);
            return new SnapshotBackupReceipt(_vaultId, _generation);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public void QueueAutomaticBackup(string targetPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        if (_generation == 0)
        {
            throw new InvalidOperationException("Save vault state before queueing an automatic backup.");
        }

        targetPath = Path.GetFullPath(targetPath);
        List<PendingBackupEntry> entries = [.. PendingBackupJournal.Read(_directory)];
        entries.Add(new PendingBackupEntry(_vaultId, _generation, targetPath));
        PendingBackupJournal.Write(_directory, entries);
    }

    public IReadOnlyList<SnapshotBackupReceipt> PublishPendingBackups()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        IReadOnlyList<PendingBackupEntry> entries;
        try
        {
            entries = PendingBackupJournal.Read(_directory);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            // A corrupt or unreadable journal is preserved untouched (no write) so a
            // later session or manual inspection can recover it; publication is skipped
            // this session and the vault still opens.
            return [];
        }

        List<PendingBackupEntry> remaining = [];
        List<SnapshotBackupReceipt> receipts = [];
        foreach (PendingBackupEntry entry in entries)
        {
            if (entry.VaultId != _vaultId)
            {
                remaining.Add(entry);
                continue;
            }

            try
            {
                receipts.Add(PublishAutomaticBackup(entry.Generation, entry.TargetPath));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or VaultAuthenticationException)
            {
                // A failed backup must never block the vault. Keep the entry pending so
                // a later session retries it; it is never silently dropped.
                remaining.Add(entry);
            }
        }

        if (remaining.Count != entries.Count)
        {
            PendingBackupJournal.Write(_directory, remaining);
        }

        return receipts;
    }

    private SnapshotBackupReceipt PublishAutomaticBackup(uint generation, string targetPath)
    {
        if (File.Exists(targetPath))
        {
            ParsedSnapshot existing = ReadAndAuthenticate(targetPath, _key);
            return existing.VaultId == _vaultId && existing.Generation == generation
                ? new SnapshotBackupReceipt(_vaultId, generation)
                : throw new IOException("The backup path already contains a different authenticated generation.");
        }

        string temporaryPath = targetPath + $".{Guid.NewGuid():N}.partial";
        try
        {
            CopyAndFlush(GenerationPath(generation), temporaryPath);
            ParsedSnapshot staged = ReadAndAuthenticate(temporaryPath, _key);
            if (staged.VaultId != _vaultId || staged.Generation != generation)
            {
                throw new InvalidDataException("The staged backup is not the requested vault generation.");
            }

            File.Move(temporaryPath, targetPath);
            return new SnapshotBackupReceipt(_vaultId, generation);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    public static void Restore(string backupPath, string directory, ReadOnlySpan<char> password)
    {
        ValidatePassword(password);
        backupPath = Path.GetFullPath(backupPath);
        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        using FileStream writerLock = AcquireWriterLock(directory);
        ParsedSnapshot header = ReadHeader(backupPath);
        byte[] key = DeriveKey(password, header.Salt);
        try
        {
            ParsedSnapshot backup = ReadAndAuthenticate(backupPath, key);
            string targetPath = Path.Combine(directory, $"generation-{backup.Generation:D10}.dfsnap");
            string[] existing = Directory.GetFiles(directory, "generation-*.dfsnap");
            if (existing.Length != 0)
            {
                throw new InvalidOperationException("Restore requires an empty vault directory in this prototype.");
            }

            string temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.restore-partial");
            try
            {
                CopyAndFlush(backupPath, temporaryPath);
                _ = ReadAndAuthenticate(temporaryPath, key);
                CrashIfRequested("restore-before-rename"); // Disposable fault-injection hook; no-op unless DFSNAP_CRASH_POINT is set.
                File.Move(temporaryPath, targetPath);
                PublishActiveGeneration(directory, backup.Generation);
            }
            finally
            {
                File.Delete(temporaryPath);
            }
        }
        catch (VaultAuthenticationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or CryptographicException or JsonException or InvalidDataException)
        {
            throw new VaultAuthenticationException("The password is incorrect or the authenticated backup is damaged.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CryptographicOperations.ZeroMemory(_key);
        _writerLock.Dispose();
    }

    private static void ValidatePassword(ReadOnlySpan<char> password)
    {
        if (password.IsEmpty || password.IsWhiteSpace())
        {
            throw new ArgumentException("A non-empty vault password is required.", nameof(password));
        }
    }

    private static void ValidateSchemaVersion(SnapshotDocument document)
    {
        if (document.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException("The authenticated snapshot uses an unsupported schema version.");
        }
    }

    private static byte[] DeriveKey(ReadOnlySpan<char> password, ReadOnlySpan<byte> salt)
    {
        int byteCount = Encoding.UTF8.GetByteCount(password);
        byte[] passwordBytes = new byte[byteCount];
        try
        {
            Encoding.UTF8.GetBytes(password, passwordBytes);
            return Rfc2898DeriveBytes.Pbkdf2(passwordBytes, salt, Iterations, HashAlgorithmName.SHA256, 32);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    private static FileStream AcquireWriterLock(string directory)
    {
        try
        {
            return new FileStream(Path.Combine(directory, ".vault.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.WriteThrough);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("This vault is already open by another writer.", exception);
        }
    }

    private static void CopyAndFlush(string sourcePath, string destinationPath)
    {
        using FileStream source = File.OpenRead(sourcePath);
        using FileStream destination = new(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        source.CopyTo(destination);
        destination.Flush(flushToDisk: true);
    }

    private byte[] CreateHeader(uint generation, ReadOnlySpan<byte> nonce, long ciphertextLength) =>
        CreateHeader(generation, nonce, ciphertextLength, _salt);

    private byte[] CreateHeader(uint generation, ReadOnlySpan<byte> nonce, long ciphertextLength, ReadOnlySpan<byte> salt)
    {
        byte[] header = new byte[HeaderLength];
        Magic.CopyTo(header);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(8), FormatVersion);
        header[10] = 1; // PBKDF2-HMAC-SHA-256
        header[11] = 1; // AES-256-GCM
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(12), Iterations);
        salt.CopyTo(header.AsSpan(16, SaltLength));
        _vaultId.TryWriteBytes(header.AsSpan(32, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(48), generation);
        nonce.CopyTo(header.AsSpan(52, NonceLength));
        BinaryPrimitives.WriteUInt64BigEndian(header.AsSpan(64), checked((ulong)ciphertextLength));
        return header;
    }

    private byte[] UniqueNonce()
    {
        HashSet<string> used = new(StringComparer.Ordinal);
        foreach (string path in Directory.GetFiles(_directory, "generation-*.dfsnap"))
        {
            try
            {
                ParsedSnapshot snapshot = ReadHeader(path);
                if (snapshot.VaultId == _vaultId)
                {
                    used.Add(Convert.ToHexString(snapshot.Nonce));
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                // Ignore unreadable generation files; they are not valid generations and
                // cannot contribute a duplicate nonce.
            }
        }

        for (int attempt = 0; attempt < 8; attempt++)
        {
            byte[] nonce = RandomNumberGenerator.GetBytes(NonceLength);
            if (!used.Contains(Convert.ToHexString(nonce)))
            {
                return nonce;
            }
        }

        throw new CryptographicException("Could not allocate a unique snapshot nonce.");
    }

    private void Publish(uint generation, byte[] header, byte[] ciphertext, byte[] tag)
    {
        string finalPath = GenerationPath(generation);
        string temporaryPath = Path.Combine(_directory, $".{Guid.NewGuid():N}.partial");
        try
        {
            TimeSavePhase("save-write", () =>
            {
                using FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
                stream.Write(header);
                stream.Write(ciphertext);
                stream.Write(tag);
                stream.Flush(flushToDisk: true);
            });

            TimeSavePhase("save-validate", () => VerifyWrittenFile(temporaryPath, header, ciphertext, tag));
            CrashIfRequested("save-before-rename"); // Disposable fault-injection hook; no-op unless DFSNAP_CRASH_POINT is set.
            TimeSavePhase("save-rename", () => File.Move(temporaryPath, finalPath));
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private void EnsureGenerationUnchanged()
    {
        string activePath = Path.Combine(_directory, "active-generation");
        uint diskGeneration = File.Exists(activePath)
            ? ReadHeader(ReadActiveGenerationPath(_directory)).Generation
            : Directory.GetFiles(_directory, "generation-*.dfsnap")
                .Select(ReadHeader)
                .Where(snapshot => snapshot.VaultId == _vaultId)
                .Select(snapshot => snapshot.Generation)
                .DefaultIfEmpty(0u)
                .Max();
        if (diskGeneration != _generation)
        {
            throw new InvalidOperationException("The vault generation changed while this session was open.");
        }
    }

    private uint NextGeneration()
    {
        uint highest = _generation;
        foreach (string path in Directory.GetFiles(_directory, "generation-*.dfsnap"))
        {
            string name = Path.GetFileName(path);
            const string prefix = "generation-";
            const string suffix = ".dfsnap";
            if (name.Length == prefix.Length + 10 + suffix.Length
                && name.StartsWith(prefix, StringComparison.Ordinal)
                && name.EndsWith(suffix, StringComparison.Ordinal)
                && uint.TryParse(name.AsSpan(prefix.Length, 10), out uint generation))
            {
                highest = Math.Max(highest, generation);
            }
        }

        return highest == uint.MaxValue
            ? throw new InvalidOperationException("The snapshot generation limit has been reached; migrate to a new vault.")
            : highest + 1;
    }

    private string GenerationPath(uint generation) => Path.Combine(_directory, $"generation-{generation:D10}.dfsnap");

    private static string ReadActiveGenerationPath(string directory)
    {
        string name = File.ReadAllText(Path.Combine(directory, "active-generation")).Trim();
        const string prefix = "generation-";
        const string suffix = ".dfsnap";
        return !name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal)
            || !uint.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length), out uint generation)
            || !string.Equals(name, $"generation-{generation:D10}.dfsnap", StringComparison.Ordinal)
            ? throw new InvalidDataException("The active generation pointer is invalid.")
            : Path.Combine(directory, name);
    }

    private static void PublishActiveGeneration(string directory, uint generation)
    {
        string activePath = Path.Combine(directory, "active-generation");
        string temporaryPath = Path.Combine(directory, $".{Guid.NewGuid():N}.active-partial");
        try
        {
            using (FileStream stream = new(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            using (StreamWriter writer = new(stream, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write($"generation-{generation:D10}.dfsnap\n");
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, activePath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    // Disposable fault-injection hook for the abrupt-interruption recovery matrix. It is
    // a no-op unless running inside the AuthenticatedSnapshots.Trial executable AND
    // DFSNAP_CRASH_POINT and DFSNAP_CRASH_MARKER are both set; production code never
    // sets these variables, and inherited environment variables cannot trigger the hook
    // from any other assembly (for example the test host).
    private static void CrashIfRequested(string point)
    {
        if (Assembly.GetEntryAssembly()?.GetName().Name != "AuthenticatedSnapshots.Trial")
        {
            return;
        }

        if (string.Equals(Environment.GetEnvironmentVariable("DFSNAP_CRASH_POINT"), point, StringComparison.Ordinal))
        {
            string markerPath = Environment.GetEnvironmentVariable("DFSNAP_CRASH_MARKER")
                ?? throw new InvalidOperationException("DFSNAP_CRASH_MARKER is required when injecting an abrupt interruption.");
            File.WriteAllText(markerPath, point);
            Process.GetCurrentProcess().Kill();
            Thread.Sleep(Timeout.Infinite);
        }
    }

    private static T TimeSavePhase<T>(string name, Func<T> action)
    {
        if (SavePhaseTiming is null)
        {
            return action();
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            SavePhaseTiming(name, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static void TimeSavePhase(string name, Action action)
    {
        if (SavePhaseTiming is null)
        {
            action();
            return;
        }

        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            SavePhaseTiming(name, stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private static void VerifyWrittenFile(string path, byte[] header, byte[] ciphertext, byte[] tag)
    {
        long expectedLength = (long)header.Length + ciphertext.Length + tag.Length;
        using FileStream stream = File.OpenRead(path);
        if (stream.Length != expectedLength)
        {
            throw new InvalidDataException("The written snapshot is incomplete.");
        }

        byte[] buffer = new byte[64 * 1024];
        foreach (byte[] part in new[] { header, ciphertext, tag })
        {
            int offset = 0;
            while (offset < part.Length)
            {
                int count = Math.Min(buffer.Length, part.Length - offset);
                int read = stream.Read(buffer, 0, count);
                if (read != count || !buffer.AsSpan(0, read).SequenceEqual(part.AsSpan(offset, read)))
                {
                    throw new InvalidDataException("The written snapshot does not match the intended generation.");
                }

                offset += read;
            }
        }
    }

    private static ParsedSnapshot ReadHeader(string path)
    {
        using FileStream stream = File.OpenRead(path);
        byte[] header = new byte[HeaderLength];
        stream.ReadExactly(header);
        if (!header.AsSpan(0, Magic.Length).SequenceEqual(Magic)
            || BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(8)) != FormatVersion
            || header[10] != 1
            || header[11] != 1
            || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(12)) != Iterations)
        {
            throw new InvalidDataException("The snapshot header is unsupported.");
        }

        byte[] salt = header.AsSpan(16, SaltLength).ToArray();
        Guid vaultId = new(header.AsSpan(32, 16), bigEndian: true);
        uint generation = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(48));
        byte[] nonce = header.AsSpan(52, NonceLength).ToArray();
        ulong ciphertextLength = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(64));
        return ciphertextLength > MaximumPlaintextLength || checked((ulong)stream.Length) != (ulong)HeaderLength + ciphertextLength + TagLength
            ? throw new InvalidDataException("The snapshot length is invalid.")
            : new ParsedSnapshot(path, header, salt, vaultId, generation, nonce, checked((int)ciphertextLength), null);
    }

    private static ParsedSnapshot ReadAndAuthenticate(string path, byte[] key)
    {
        ParsedSnapshot parsed = ReadHeader(path);
        using FileStream stream = File.OpenRead(path);
        stream.Position = HeaderLength;
        byte[] ciphertext = new byte[parsed.CiphertextLength];
        byte[] tag = new byte[TagLength];
        byte[] plaintext = new byte[parsed.CiphertextLength];
        stream.ReadExactly(ciphertext);
        stream.ReadExactly(tag);
        try
        {
            using (AesGcm aes = new(key, TagLength))
            {
                aes.Decrypt(parsed.Nonce, ciphertext, tag, plaintext, parsed.Header);
            }

            SnapshotDocument document = JsonSerializer.Deserialize<SnapshotDocument>(plaintext, JsonOptions)
                ?? throw new InvalidDataException("The authenticated snapshot payload is invalid.");
            ValidateSchemaVersion(document);
            return document.Transactions is null || document.Configuration is null || document.Reconciliations is null
                ? throw new InvalidDataException("The authenticated snapshot payload is invalid.")
                : parsed with { Document = document };
        }
        catch (CryptographicException exception)
        {
            throw new VaultAuthenticationException("The password is incorrect or the authenticated vault is damaged.", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static IReadOnlyList<IndexedTransaction> BuildSearchIndex(IReadOnlyList<SnapshotTransaction> transactions) => [.. transactions
        .Select(transaction => new IndexedTransaction(transaction, string.Join('\u001F',
            transaction.Id,
            transaction.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            transaction.Amount.ToString("0.00", CultureInfo.InvariantCulture),
            transaction.Account,
            transaction.Bank,
            transaction.Category,
            transaction.Subcategory,
            transaction.Type,
            transaction.Counterparty,
            transaction.Merchant,
            transaction.Notes,
            transaction.ReconciliationId,
            transaction.SettlementStatus)))];

    private sealed record ParsedSnapshot(
        string Path,
        byte[] Header,
        byte[] Salt,
        Guid VaultId,
        uint Generation,
        byte[] Nonce,
        int CiphertextLength,
        SnapshotDocument? Document);

    private sealed record IndexedTransaction(SnapshotTransaction Transaction, string SearchText);
}