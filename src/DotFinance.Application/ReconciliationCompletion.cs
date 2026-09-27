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
    public ReconciliationBook Complete(ReconciliationBook book, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(book);
        ArgumentException.ThrowIfNullOrWhiteSpace(automaticBackupPath);
        cancellationToken.ThrowIfCancellationRequested();
        ReconciliationBook completed = ReconciliationBook.Restore(book.ExportState());
        completed.AcknowledgeCardPaid();
        vault.Save(completed, cancellationToken);
        try
        {
            vault.CreateBackup(automaticBackupPath, cancellationToken);
        }
        catch (Exception exception)
        {
            throw new AutomaticBackupException(completed, exception);
        }
        return completed;
    }

    public void RetryAutomaticBackup(CancellationToken cancellationToken = default)
        => vault.CreateBackup(automaticBackupPath, cancellationToken);
}

public sealed class AutomaticBackupException(ReconciliationBook completedBook, Exception innerException)
    : Exception("The reconciliation was saved, but its automatic backup could not be refreshed.", innerException)
{
    public ReconciliationBook CompletedBook { get; } = completedBook;
}