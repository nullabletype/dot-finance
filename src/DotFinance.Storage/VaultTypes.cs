namespace DotFinance.Storage;

public enum VaultStage
{
    BeforeSaveCommit,
    BackupStaged,
    RestoreStaged
}

public sealed record VaultSecurityProfile(string Cipher, int KdfIterations, bool LegacyMode,
    int PlaintextHeaderBytes, bool HmacCheck);

public sealed class VaultAccessException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed class VaultMigrationException(string message, Exception? innerException = null) : Exception(message, innerException);
public sealed class VaultStorageException(string message, Exception? innerException = null) : Exception(message, innerException);

internal interface IVaultFileSystem
{
    bool FileExists(string path);
    void CreateDirectory(string path);
    void CopyFile(string source, string destination);
    void DeleteFile(string path);
    void ReplaceFile(string source, string destination);
}

internal sealed class PhysicalVaultFileSystem : IVaultFileSystem
{
    internal static PhysicalVaultFileSystem Instance { get; } = new();
    public bool FileExists(string path) => File.Exists(path);
    public void CreateDirectory(string path) => Directory.CreateDirectory(path);
    public void CopyFile(string source, string destination) => File.Copy(source, destination);
    public void DeleteFile(string path) => File.Delete(path);
    public void ReplaceFile(string source, string destination) => File.Move(source, destination, true);
}

internal sealed record VaultDependencies(IVaultFileSystem FileSystem, Action<int>? BeforeMigrationCommit = null)
{
    internal static VaultDependencies Default { get; } = new(PhysicalVaultFileSystem.Instance);
}