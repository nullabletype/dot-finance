namespace DesktopShell;

public sealed class PrototypeState : IEquatable<PrototypeState>
{
    public required DateTimeOffset ImportedAt { get; init; }

    public required string AllocationStatus { get; init; }

    public required IReadOnlyList<PrototypeTransaction> Transactions { get; init; }

    public bool Equals(PrototypeState? other) => other is not null
        && ImportedAt.Equals(other.ImportedAt)
        && string.Equals(AllocationStatus, other.AllocationStatus, StringComparison.Ordinal)
        && Transactions.SequenceEqual(other.Transactions);

    public override bool Equals(object? obj) => Equals(obj as PrototypeState);

    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(ImportedAt);
        hash.Add(AllocationStatus, StringComparer.Ordinal);
        foreach (PrototypeTransaction transaction in Transactions)
        {
            hash.Add(transaction);
        }

        return hash.ToHashCode();
    }
}

public sealed record PrototypeTransaction(
    string Id,
    DateOnly Date,
    decimal Amount,
    string Account,
    string Bank,
    string Currency,
    string Category,
    string Subcategory,
    string Type,
    string Tags,
    string Counterparty,
    string CustomName,
    string Merchant,
    string AdditionalDetails,
    string Notes,
    string LinkedTransactionId,
    string FundingSource,
    bool Settled);
