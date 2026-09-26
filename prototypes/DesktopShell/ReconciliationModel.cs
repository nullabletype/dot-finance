namespace DesktopShell;

/// <summary>The role an account plays in reconciliation funding.</summary>
public enum AccountRole
{
    Intermediate,
    PrimaryPersonal,
    PersonalSavings,
    Joint,
    SharedSavings
}

/// <summary>The funding source responsible for a purchase.</summary>
public enum FundingSource
{
    Joint,
    SharedSavings,
    Personal
}

/// <summary>The purpose and direction of a planned transfer.</summary>
public enum TransferKind
{
    Funding,
    Reimbursement,
    Refund,
    CardPayment
}

/// <summary>A configured funding account with its entered balance and protected minimum.</summary>
public sealed record Account(string Name, AccountRole Role, decimal CurrentBalance, decimal ProtectedMinimum);

/// <summary>Maps a category to a funding source.</summary>
public sealed record CategoryRule(string Category, FundingSource FundingSource);

/// <summary>Maps a subcategory to a funding source, taking precedence over its category rule.</summary>
public sealed record SubcategoryRule(string Subcategory, FundingSource FundingSource);

/// <summary>A subcategory reported separately within a funding source.</summary>
public sealed record ReportingCategory(string Name, FundingSource FundingSource);

/// <summary>An imported transaction with all CSV fields plus derived allocation state.</summary>
public sealed record Transaction(
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
    FundingSource? FundingSource,
    bool Settled,
    bool RequiresReview);

/// <summary>A planned transfer to be performed outside the app and acknowledged manually.</summary>
public sealed record Transfer(string Source, TransferKind Kind, decimal Amount, bool Acknowledged);

/// <summary>An in-progress reconciliation draft.</summary>
public sealed record ReconciliationDraft(
    decimal CardPaymentTarget,
    IReadOnlyList<Account> Accounts,
    IReadOnlyList<Transaction> Transactions,
    IReadOnlyList<Transfer> Transfers,
    bool IsLocked,
    bool IsSettled,
    decimal Discrepancy,
    bool DiscrepancyAcknowledged = false);

/// <summary>The full persistence shape for the guided workflow experiment.</summary>
public sealed record ScenarioState(
    IReadOnlyList<Account> Accounts,
    IReadOnlyList<CategoryRule> CategoryRules,
    IReadOnlyList<SubcategoryRule> SubcategoryRules,
    IReadOnlyList<ReportingCategory> ReportingCategories,
    IReadOnlyList<Transaction> Transactions,
    IReadOnlyList<Transaction> PendingTransactions,
    IReadOnlyDictionary<string, FundingSource> Overrides,
    ReconciliationDraft? Draft,
    IReadOnlyList<ReconciliationDraft> CompletedReconciliations,
    string ThemePreference);