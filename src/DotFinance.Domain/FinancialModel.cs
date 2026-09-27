using System.Collections.Immutable;

namespace DotFinance.Domain;

public enum FundingSource { Personal, Joint, SharedSavings }
public enum AccountRole { Intermediate, PrimaryPersonal, PersonalSavings, Joint, SharedSavings }
public enum TransactionKind { Purchase, Refund, Transfer, CardRepayment }
public enum ReviewReason { Uncategorized, ActualGbpRequired, UnknownPaymentAccount, Duplicate, Correction }

// Amount is signed account activity: purchases negative, refunds positive. The import adapter
// must normalise its verified format into this contract; source Type is retained independently.
public sealed record ImportedTransaction(
    string Id, DateOnly Date, decimal Amount, string AccountId, string Bank, string Currency,
    string Category, string Subcategory, string Type, string Tags, string Counterparty,
    string CustomName, string Merchant, string AdditionalDetails, string Notes,
    string LinkedTransactionId, TransactionKind Kind, decimal? ActualGbpAmount = null);

public sealed record FundingAccount(string Id, string Name, AccountRole Role, decimal ProtectedMinimum, int Priority = 0);
public sealed record AccountBalance(string AccountId, decimal Gbp);
public sealed record CategoryRule(string Category, FundingSource Source);
public sealed record SubcategoryRule(string Category, string Subcategory, FundingSource Source);
public sealed record ReportingCategory(string Category, string? Subcategory, FundingSource Source);
public sealed record ReviewItem(string TransactionId, ReviewReason Reason);
public sealed record AllocatedTransaction(ImportedTransaction Transaction, FundingSource? Source,
    decimal CostGbp, decimal CardCostGbp, bool Eligible, ImmutableArray<ReviewItem> Reviews);
public sealed record Adjustment(string Id, string TransactionId, FundingSource Source, decimal CostGbp, decimal CardCostGbp, string Category, string Subcategory);
public sealed record Contribution(FundingSource Source, decimal CardPurchasesGbp, decimal ReimbursementsGbp, decimal TotalGbp);
public sealed record CategoryTotal(FundingSource? Source, string Category, decimal Gbp);
public sealed record ReportingTotal(ReportingCategory Category, decimal SelectedGbp, decimal OtherGbp, decimal CombinedGbp);
public sealed record FundingTransfer(string AccountId, decimal SignedGbp, bool Acknowledged = false);
public sealed record Shortfall(string AccountId, decimal Gbp);
public sealed record FundingPlan(decimal CardTargetGbp, decimal NetCardSpendingGbp,
    decimal DiscrepancyGbp, decimal AdditionalPersonalGbp, decimal IntermediateRestorationGbp,
    ImmutableArray<Contribution> Contributions, ImmutableArray<FundingTransfer> Transfers,
    ImmutableArray<AccountBalance> ProjectedBalances, ImmutableArray<Shortfall> Shortfalls,
    ImmutableArray<CategoryTotal> CategoryTotals, ImmutableArray<ReportingTotal> ReportingTotals);

public sealed class AllocationRules
{
    public AllocationRules(IEnumerable<CategoryRule> categories, IEnumerable<SubcategoryRule> subcategories,
        IEnumerable<ReportingCategory>? reporting = null)
    {
        Categories = [.. categories];
        Subcategories = [.. subcategories];
        Reporting = [.. (reporting ?? [])];
        foreach (CategoryRule rule in Categories)
        {
            DomainGuard.Text(rule.Category); DomainGuard.Defined(rule.Source);
        }
        foreach (SubcategoryRule rule in Subcategories)
        {
            DomainGuard.Text(rule.Category); DomainGuard.Text(rule.Subcategory); DomainGuard.Defined(rule.Source);
        }
        foreach (ReportingCategory rule in Reporting)
        {
            DomainGuard.Text(rule.Category); DomainGuard.Defined(rule.Source);
            if (rule.Subcategory is not null) DomainGuard.Text(rule.Subcategory);
        }
        if (Categories.GroupBy(x => x.Category, StringComparer.OrdinalIgnoreCase).Any(x => x.Count() > 1)
            || Subcategories.GroupBy(x => (x.Category.ToUpperInvariant(), x.Subcategory.ToUpperInvariant())).Any(x => x.Count() > 1)
            || Reporting.Distinct().Count() != Reporting.Length)
            throw new ArgumentException("Rules must be unambiguous and reporting categories unique.");
    }
    public ImmutableArray<CategoryRule> Categories { get; }
    public ImmutableArray<SubcategoryRule> Subcategories { get; }
    public ImmutableArray<ReportingCategory> Reporting { get; }
}

public sealed class FundingConfiguration
{
    public FundingConfiguration(string cardAccountId, IEnumerable<FundingAccount> accounts)
    {
        DomainGuard.Text(cardAccountId);
        CardAccountId = cardAccountId;
        Accounts = [.. accounts.OrderBy(x => x.Role).ThenBy(x => x.Priority).ThenBy(x => x.Id, StringComparer.Ordinal)];
        foreach (FundingAccount account in Accounts)
        {
            DomainGuard.Text(account.Id); DomainGuard.Text(account.Name); DomainGuard.Defined(account.Role);
            DomainGuard.Gbp(account.ProtectedMinimum, nonNegative: true);
            if (account.Priority < 0) throw new ArgumentException("Savings priority cannot be negative.");
        }
        if (Accounts.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != Accounts.Length
            || Accounts.Any(x => x.Id == cardAccountId)) throw new ArgumentException("Account identities must be unique, including the card.");
        foreach (AccountRole role in Enum.GetValues<AccountRole>().Where(x => x != AccountRole.PersonalSavings))
            if (Accounts.Count(x => x.Role == role) != 1) throw new ArgumentException($"Exactly one {role} account is required.");
        if (!Accounts.Any(x => x.Role == AccountRole.PersonalSavings)) throw new ArgumentException("At least one personal savings source is required.");
        if (Accounts.Where(x => x.Role == AccountRole.PersonalSavings).GroupBy(x => x.Priority).Any(x => x.Count() > 1))
            throw new ArgumentException("Personal savings priorities must be unique.");
    }
    public string CardAccountId { get; }
    public ImmutableArray<FundingAccount> Accounts { get; }
    public FundingAccount Account(AccountRole role) => Accounts.Single(x => x.Role == role);
    internal ImmutableArray<AccountBalance> ValidateBalances(IEnumerable<AccountBalance> balances)
    {
        ImmutableArray<AccountBalance> result = [.. balances.OrderBy(x => x.AccountId, StringComparer.Ordinal)];
        if (result.Length != Accounts.Length || result.Select(x => x.AccountId).Distinct(StringComparer.Ordinal).Count() != result.Length
            || result.Any(x => !Accounts.Any(a => a.Id == x.AccountId))) throw new ArgumentException("Enter exactly one balance per configured funding account.");
        foreach (AccountBalance balance in result) DomainGuard.Gbp(balance.Gbp);
        return result;
    }
}

internal static class DomainGuard
{
    internal static void Text(string value) => ArgumentException.ThrowIfNullOrWhiteSpace(value);
    internal static void Defined<T>(T value) where T : struct, Enum
    {
        if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
    }
    internal static void Gbp(decimal value, bool nonNegative = false)
    {
        if ((nonNegative && value < 0m) || decimal.Round(value, 2) != value)
            throw new ArgumentOutOfRangeException(nameof(value), "GBP must have penny precision and the required sign.");
    }
    internal static void Transaction(ImportedTransaction transaction)
    {
        Text(transaction.Id); Text(transaction.AccountId); Text(transaction.Currency); Defined(transaction.Kind);
        if (transaction.Date == default) throw new ArgumentException("Supply a transaction date.");
        if (new[] { transaction.Bank, transaction.Category, transaction.Subcategory, transaction.Type,
            transaction.Tags, transaction.Counterparty, transaction.CustomName, transaction.Merchant,
            transaction.AdditionalDetails, transaction.Notes, transaction.LinkedTransactionId }.Any(x => x is null))
            throw new ArgumentException("Retained import fields cannot be null.");
        if (transaction.Kind == TransactionKind.Purchase && transaction.Amount > 0m
            || transaction.Kind == TransactionKind.Refund && transaction.Amount < 0m)
            throw new ArgumentException("Normalise purchase/refund amount signs at the import boundary.");
        bool gbp = transaction.Currency.Equals("GBP", StringComparison.OrdinalIgnoreCase);
        if (gbp) Gbp(transaction.Amount);
        if (transaction.ActualGbpAmount is { } actual)
        {
            Gbp(actual);
            if (gbp || Math.Sign(actual) != Math.Sign(transaction.Amount))
                throw new ArgumentException("Actual GBP is for foreign activity and must preserve its sign.");
        }
        if (transaction.LinkedTransactionId == transaction.Id) throw new ArgumentException("A transaction cannot link to itself.");
    }
}