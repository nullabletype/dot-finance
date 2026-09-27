using DotFinance.Domain;

namespace DotFinance.Application;

public interface IReconciliationVault
{
    void Save(ReconciliationBook book, CancellationToken cancellationToken);
    void CreateBackup(string backupPath, CancellationToken cancellationToken);
}

/// <summary>Commits a completed reconciliation and then refreshes its configured encrypted backup.</summary>
public sealed class ReconciliationCompletion(IReconciliationVault vault, string automaticBackupPath)
{
    public void Complete(ReconciliationBook book, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentException.ThrowIfNullOrWhiteSpace(automaticBackupPath);
        cancellationToken.ThrowIfCancellationRequested();
        book.AcknowledgeCardPaid();
        vault.Save(book, cancellationToken);
        vault.CreateBackup(automaticBackupPath, cancellationToken);
    }
}