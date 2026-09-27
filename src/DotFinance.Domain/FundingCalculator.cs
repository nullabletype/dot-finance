using System.Collections.Immutable;

namespace DotFinance.Domain;

public static class FundingCalculator
{
    public static ImmutableArray<AllocatedTransaction> Allocate(FundingConfiguration configuration, AllocationRules rules,
        IEnumerable<ImportedTransaction> transactions, IReadOnlyDictionary<string, FundingSource>? overrides = null,
        IReadOnlyDictionary<string, FundingSource>? historicalSources = null)
    {
        ImportedTransaction[] input = [.. transactions.OrderBy(x => x.Id, StringComparer.Ordinal)];
        foreach (ImportedTransaction transaction in input) DomainGuard.Transaction(transaction);
        if (input.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != input.Length)
            throw new ArgumentException("Resolve duplicate transaction identities before allocation.");
        Dictionary<string, FundingSource> sources = historicalSources?.ToDictionary() ?? new(StringComparer.Ordinal);
        foreach (ImportedTransaction transaction in input.Where(x => x.Kind == TransactionKind.Purchase))
            if (Resolve(transaction) is { } source) sources[transaction.Id] = source;
        return [.. input.Select(transaction =>
        {
            if (transaction.Kind is TransactionKind.Transfer or TransactionKind.CardRepayment)
                return new AllocatedTransaction(transaction, null, 0m, 0m, false, []);
            List<ReviewItem> reviews = [];
            FundingSource? source = Resolve(transaction);
            if (transaction.Kind == TransactionKind.Refund && overrides?.ContainsKey(transaction.Id) != true
                && sources.TryGetValue(transaction.LinkedTransactionId, out FundingSource original)) source = original;
            if (source is null) reviews.Add(new(transaction.Id, ReviewReason.Uncategorized));
            bool card = transaction.AccountId == configuration.CardAccountId;
            FundingAccount? payment = configuration.Accounts.FirstOrDefault(x => x.Id == transaction.AccountId);
            if (!card && payment is null) reviews.Add(new(transaction.Id, ReviewReason.UnknownPaymentAccount));
            bool personalPayment = payment?.Role is AccountRole.Intermediate or AccountRole.PrimaryPersonal or AccountRole.PersonalSavings;
            bool eligible = card || personalPayment && source != FundingSource.Personal;
            decimal? activity = transaction.Currency.Equals("GBP", StringComparison.OrdinalIgnoreCase) ? transaction.Amount : transaction.ActualGbpAmount;
            if (eligible && activity is null) reviews.Add(new(transaction.Id, ReviewReason.ActualGbpRequired));
            if (!eligible && payment is not null) reviews.Clear(); // Purchases already funded by Joint/Shared are out of scope.
            decimal cost = eligible ? -(activity ?? 0m) : 0m;
            return new AllocatedTransaction(transaction, source, cost, card ? cost : 0m, eligible, [.. reviews]);
        })];

        FundingSource? Resolve(ImportedTransaction transaction)
        {
            if (overrides?.TryGetValue(transaction.Id, out FundingSource explicitSource) == true)
            { DomainGuard.Defined(explicitSource); return explicitSource; }
            SubcategoryRule? subcategory = rules.Subcategories.FirstOrDefault(x => Same(x.Category, transaction.Category) && Same(x.Subcategory, transaction.Subcategory));
            if (subcategory is not null) return subcategory.Source;
            CategoryRule? category = rules.Categories.FirstOrDefault(x => Same(x.Category, transaction.Category));
            return category?.Source ?? (string.IsNullOrWhiteSpace(transaction.Category) ? null : FundingSource.Personal);
        }
    }

    public static FundingPlan Plan(FundingConfiguration configuration, decimal cardTargetGbp, IEnumerable<AccountBalance> balances,
        IEnumerable<AllocatedTransaction> allocations, IEnumerable<Adjustment>? adjustments = null, IEnumerable<ReportingCategory>? reporting = null)
    {
        DomainGuard.Gbp(cardTargetGbp, nonNegative: true);
        ImmutableArray<AccountBalance> current = configuration.ValidateBalances(balances);
        AllocatedTransaction[] supplied = [.. allocations];
        Adjustment[] changes = [.. (adjustments ?? [])];
        if (supplied.Select(x => x.Transaction.Id).Distinct(StringComparer.Ordinal).Count() != supplied.Length
            || changes.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != changes.Length)
            throw new ArgumentException("Allocation and adjustment identities must be unique.");
        foreach (AllocatedTransaction row in supplied)
        {
            DomainGuard.Transaction(row.Transaction); DomainGuard.Gbp(row.CostGbp); DomainGuard.Gbp(row.CardCostGbp);
            if (row.Source is { } source) DomainGuard.Defined(source);
            decimal expected = -(row.Transaction.Currency.Equals("GBP", StringComparison.OrdinalIgnoreCase)
                ? row.Transaction.Amount : row.Transaction.ActualGbpAmount ?? 0m);
            if (row.CostGbp != (row.Eligible ? expected : 0m)
                || row.CardCostGbp != (row.Eligible && row.Transaction.AccountId == configuration.CardAccountId ? expected : 0m))
                throw new ArgumentException("Allocation money must match the valued transaction.");
        }
        foreach (Adjustment change in changes)
        {
            DomainGuard.Text(change.Id); DomainGuard.Text(change.TransactionId); DomainGuard.Defined(change.Source);
            DomainGuard.Gbp(change.CostGbp); DomainGuard.Gbp(change.CardCostGbp);
            if (change.Category is null || change.Subcategory is null) throw new ArgumentException("Adjustment reporting fields cannot be null.");
        }
        AllocatedTransaction[] rows = [.. supplied.Where(x => x.Eligible)];
        ImmutableArray<Contribution> contributions = [.. Enum.GetValues<FundingSource>().Select(source =>
        {
            decimal card = rows.Where(x => x.Source == source).Sum(x => x.CardCostGbp);
            decimal reimbursement = rows.Where(x => x.Source == source).Sum(x => x.CostGbp - x.CardCostGbp);
            decimal corrections = changes.Where(x => x.Source == source).Sum(x => x.CostGbp);
            return new Contribution(source, card, reimbursement, card + reimbursement + corrections);
        })];
        FundingAccount intermediate = configuration.Account(AccountRole.Intermediate);
        decimal balance = Current(intermediate);
        decimal joint = contributions.Single(x => x.Source == FundingSource.Joint).TotalGbp;
        decimal shared = contributions.Single(x => x.Source == FundingSource.SharedSavings).TotalGbp;
        decimal required = Math.Max(0m, cardTargetGbp + intermediate.ProtectedMinimum - balance - joint - shared);
        decimal remaining = required;
        List<FundingTransfer> transfers = [];
        Add(configuration.Account(AccountRole.Joint), joint);
        Add(configuration.Account(AccountRole.SharedSavings), shared);
        IEnumerable<FundingAccount> personal = configuration.Accounts.Where(x => x.Role is AccountRole.PrimaryPersonal or AccountRole.PersonalSavings)
            .OrderBy(x => x.Role == AccountRole.PrimaryPersonal ? 0 : 1).ThenBy(x => x.Priority).ThenBy(x => x.Id, StringComparer.Ordinal);
        foreach (FundingAccount account in personal)
        {
            decimal amount = Math.Min(remaining, Math.Max(0m, Current(account) - account.ProtectedMinimum));
            Add(account, amount); remaining -= amount;
        }
        ImmutableArray<AccountBalance> projected = Project(configuration, current, transfers, cardTargetGbp);
        ImmutableArray<Shortfall> shortfalls = FindShortfalls(configuration, current, projected);
        decimal netCard = rows.Sum(x => x.CardCostGbp) + changes.Sum(x => x.CardCostGbp);
        var reportingRows = rows.Select(x => (x.Source, x.Transaction.Category, x.Transaction.Subcategory, x.CostGbp))
            .Concat(changes.Select(x => (Source: (FundingSource?)x.Source, x.Category, x.Subcategory, x.CostGbp))).ToArray();
        ImmutableArray<CategoryTotal> totals = [.. reportingRows.GroupBy(x => (x.Source, x.Category))
            .Select(x => new CategoryTotal(x.Key.Source, x.Key.Category, x.Sum(row => row.CostGbp)))
            .OrderBy(x => x.Source).ThenBy(x => x.Category, StringComparer.Ordinal)];
        ImmutableArray<ReportingTotal> reportingTotals = [.. (reporting ?? []).Select(category =>
        {
            decimal selected = reportingRows.Where(x => x.Source == category.Source && Same(x.Category, category.Category)
                && (category.Subcategory is null || Same(x.Subcategory, category.Subcategory))).Sum(x => x.CostGbp);
            decimal total = contributions.Single(x => x.Source == category.Source).TotalGbp;
            return new ReportingTotal(category, selected, total - selected, total);
        })];
        return new(cardTargetGbp, netCard, cardTargetGbp - netCard, required,
            Math.Max(0m, intermediate.ProtectedMinimum - balance), contributions, [.. transfers], projected, shortfalls, totals, reportingTotals);
        decimal Current(FundingAccount account) => current.Single(x => x.AccountId == account.Id).Gbp;
        void Add(FundingAccount account, decimal amount) { if (amount != 0m) transfers.Add(new(account.Id, amount)); }
    }

    public static ImmutableArray<AccountBalance> Project(FundingConfiguration configuration, IEnumerable<AccountBalance> balances,
        IEnumerable<FundingTransfer> transfers, decimal cardTargetGbp, bool cardPaid = false)
    {
        DomainGuard.Gbp(cardTargetGbp, nonNegative: true);
        Dictionary<string, decimal> projected = configuration.ValidateBalances(balances).ToDictionary(x => x.AccountId, x => x.Gbp, StringComparer.Ordinal);
        string intermediate = configuration.Account(AccountRole.Intermediate).Id;
        FundingTransfer[] steps = [.. transfers];
        if (steps.Select(x => x.AccountId).Distinct(StringComparer.Ordinal).Count() != steps.Length)
            throw new ArgumentException("Only one combined transfer per source is supported.");
        foreach (FundingTransfer transfer in steps)
        {
            DomainGuard.Gbp(transfer.SignedGbp);
            if (!projected.ContainsKey(transfer.AccountId) || transfer.AccountId == intermediate) throw new ArgumentException("Invalid transfer source.");
            if (transfer.Acknowledged) continue;
            projected[transfer.AccountId] -= transfer.SignedGbp;
            projected[intermediate] += transfer.SignedGbp;
        }
        if (!cardPaid) projected[intermediate] -= cardTargetGbp;
        return [.. projected.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new AccountBalance(x.Key, x.Value))];
    }

    internal static ImmutableArray<Shortfall> FindShortfalls(FundingConfiguration configuration,
        ImmutableArray<AccountBalance> current, ImmutableArray<AccountBalance> projected) => [.. configuration.Accounts.Select(account =>
        {
            decimal final = projected.Single(x => x.AccountId == account.Id).Gbp;
            // An incoming refund must not disguise a required external top-up of a source already below its minimum.
            decimal relevant = account.Role == AccountRole.Intermediate ? final : Math.Min(final, current.Single(x => x.AccountId == account.Id).Gbp);
            return new Shortfall(account.Id, Math.Max(0m, account.ProtectedMinimum - relevant));
        }).Where(x => x.Gbp > 0m)];

    private static bool Same(string left, string right) => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}