namespace AuthenticatedSnapshots;

public sealed record SnapshotDocument(
    int SchemaVersion,
    DateTimeOffset SavedAt,
    IReadOnlyList<SnapshotTransaction> Transactions,
    IReadOnlyDictionary<string, string> Configuration,
    IReadOnlyList<ReconciliationRecord> Reconciliations);

public sealed record SnapshotTransaction(
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
    string ReconciliationId,
    string SettlementStatus);

public sealed record ReconciliationRecord(
    string Id,
    DateTimeOffset StartedAt,
    string Status,
    decimal CardPaymentTarget,
    IReadOnlyDictionary<string, decimal> AgreedTransfers,
    IReadOnlyList<string> Acknowledgements);