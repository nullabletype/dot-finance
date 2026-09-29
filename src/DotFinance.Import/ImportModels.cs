using System.Collections.Immutable;

namespace DotFinance.Import;

public enum ImportClassification
{
    PurchaseCandidate,
    RefundCandidate,
    ExcludedTransfer,
    ReviewRequired
}

public enum ImportReviewReason
{
    SourceTypeRequiresReview,
    ContradictoryAmountDirection,
    UnlinkedCredit,
    ActualGbpRequired
}

public sealed record ImportIssue(string Code, string Message, int? Row = null, string? Field = null);

public sealed record EmmaTransactionRecord(
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
    ImportClassification Classification,
    ImmutableArray<ImportReviewReason> Reviews);

public sealed record EmmaImportResult(
    string ContractVersion,
    ImmutableArray<EmmaTransactionRecord> Records,
    ImmutableArray<ImportIssue> Issues)
{
    public bool IsValid => Issues.IsDefaultOrEmpty;
}