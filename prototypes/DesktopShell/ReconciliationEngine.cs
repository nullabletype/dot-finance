namespace DesktopShell;

/// <summary>A single funding source's contribution breakdown.</summary>
public sealed record SourceContribution(
    FundingSource Source,
    decimal Purchases,
    decimal Reimbursements,
    decimal Refunds,
    decimal Total);

/// <summary>The computed funding plan for a reconciliation draft.</summary>
public sealed record TransferPlan(
    decimal CardPaymentTarget,
    IReadOnlyList<SourceContribution> Contributions,
    decimal AdditionalPersonalTransfers,
    IReadOnlyList<Transfer> Transfers,
    IReadOnlyList<AccountBalance> ProjectedBalances,
    IReadOnlyList<Shortfall> Shortfalls,
    IReadOnlyList<ReportingSubtotal> ReportingSubtotals);

/// <summary>A projected account balance after transfers and the card payment.</summary>
public sealed record AccountBalance(string AccountName, decimal ProjectedBalance);

/// <summary>A funding shortfall that blocks confirmation of an affected transfer.</summary>
public sealed record Shortfall(string AccountName, decimal Amount);

/// <summary>A reporting-category subtotal shown separately within its funding source.</summary>
public sealed record ReportingSubtotal(string Name, FundingSource FundingSource, decimal Amount);

/// <summary>
/// Pure reconciliation calculations. No I/O and no UI: every method returns a new immutable value.
/// </summary>
public static class ReconciliationEngine
{
    /// <summary>Deduplicates imports by transaction Id; the first occurrence wins.</summary>
    public static IReadOnlyList<Transaction> Dedupe(IReadOnlyList<Transaction> transactions)
    {
        List<Transaction> result = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (Transaction transaction in transactions)
        {
            if (seen.Add(transaction.Id))
            {
                result.Add(transaction);
            }
        }

        return result;
    }

    /// <summary>
    /// Assigns a funding source to each transaction and flags uncategorised transactions for review.
    /// Refunds resolve to the funding source of the linked original purchase when identifiable.
    /// </summary>
    public static IReadOnlyList<Transaction> Allocate(
        IReadOnlyList<Transaction> transactions,
        IReadOnlyList<CategoryRule> categoryRules,
        IReadOnlyList<SubcategoryRule> subcategoryRules,
        IReadOnlyDictionary<string, FundingSource>? overrides = null)
    {
        Dictionary<string, FundingSource> sourceById = [];
        foreach (Transaction transaction in transactions)
        {
            if (IsExcluded(transaction) || IsRefund(transaction))
            {
                continue;
            }

            FundingSource? source = ResolveSource(transaction, categoryRules, subcategoryRules, overrides);
            if (source is { } resolved)
            {
                sourceById[transaction.Id] = resolved;
            }
        }

        List<Transaction> result = new(transactions.Count);
        foreach (Transaction transaction in transactions)
        {
            FundingSource? source = IsExcluded(transaction)
                ? null
                : IsRefund(transaction)
                && transaction.LinkedTransactionId is { Length: > 0 } linked
                && sourceById.TryGetValue(linked, out FundingSource original)
                    ? original
                    : ResolveSource(transaction, categoryRules, subcategoryRules, overrides);
            bool requiresReview = !IsExcluded(transaction)
                && (source is null || RequiresActualGbpAmount(transaction));
            result.Add(transaction with { FundingSource = source, RequiresReview = requiresReview });
        }

        return result;
    }

    /// <summary>
    /// Computes the funding plan using the spec formula and personal funding order.
    /// Joint and Shared Savings contributions are signed: positive means incoming to the
    /// intermediate account, negative means outgoing (for example, a net refund).
    /// </summary>
    public static TransferPlan ComputeTransfers(
        decimal cardPaymentTarget,
        string cardAccountName,
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Transaction> allocated,
        IReadOnlyList<ReportingCategory> reportingCategories)
    {
        Account intermediate = RequireRole(accounts, AccountRole.Intermediate);
        Account primary = RequireRole(accounts, AccountRole.PrimaryPersonal);
        Account joint = RequireRole(accounts, AccountRole.Joint);
        Account sharedSavings = RequireRole(accounts, AccountRole.SharedSavings);
        List<Account> savings = [.. accounts
            .Where(account => account.Role == AccountRole.PersonalSavings)
            .OrderBy(account => account.Name, StringComparer.Ordinal)];

        SourceContribution jointContribution = ComputeContribution(allocated, cardAccountName, FundingSource.Joint);
        SourceContribution sharedContribution = ComputeContribution(allocated, cardAccountName, FundingSource.SharedSavings);

        decimal additionalPersonal = Math.Max(
            0m,
            cardPaymentTarget + intermediate.ProtectedMinimum
            - intermediate.CurrentBalance
            - jointContribution.Total
            - sharedContribution.Total);

        decimal primaryAvailable = Math.Max(0m, primary.CurrentBalance - primary.ProtectedMinimum);
        decimal primaryTransfer = Math.Min(additionalPersonal, primaryAvailable);
        decimal remaining = additionalPersonal - primaryTransfer;

        List<Transfer> transfers = [];
        if (jointContribution.Total != 0m)
        {
            transfers.Add(new Transfer(joint.Name, KindFor(jointContribution), jointContribution.Total, false));
        }

        if (sharedContribution.Total != 0m)
        {
            transfers.Add(new Transfer(sharedSavings.Name, KindFor(sharedContribution), sharedContribution.Total, false));
        }

        if (primaryTransfer != 0m)
        {
            transfers.Add(new Transfer(primary.Name, TransferKind.Funding, primaryTransfer, false));
        }

        foreach (Account savingsAccount in savings)
        {
            decimal available = Math.Max(0m, savingsAccount.CurrentBalance - savingsAccount.ProtectedMinimum);
            decimal amount = Math.Min(remaining, available);
            if (amount != 0m)
            {
                transfers.Add(new Transfer(savingsAccount.Name, TransferKind.Funding, amount, false));
                remaining -= amount;
            }
        }

        transfers.Add(new Transfer(intermediate.Name, TransferKind.CardPayment, cardPaymentTarget, false));

        List<AccountBalance> balances =
        [
            new AccountBalance(intermediate.Name,
                intermediate.CurrentBalance + jointContribution.Total + sharedContribution.Total
                + additionalPersonal - cardPaymentTarget),
            new AccountBalance(joint.Name, joint.CurrentBalance - jointContribution.Total),
            new AccountBalance(sharedSavings.Name, sharedSavings.CurrentBalance - sharedContribution.Total),
            new AccountBalance(primary.Name, primary.CurrentBalance - primaryTransfer),
        ];
        balances.AddRange(savings.Select(savingsAccount =>
            new AccountBalance(savingsAccount.Name,
                savingsAccount.CurrentBalance - TransferFor(transfers, savingsAccount.Name))));

        List<Shortfall> shortfalls = [];
        AddShortfallIfExceeded(shortfalls, joint, jointContribution.Total);
        AddShortfallIfExceeded(shortfalls, sharedSavings, sharedContribution.Total);
        if (remaining > 0m)
        {
            shortfalls.Add(new Shortfall("Personal", remaining));
        }

        List<ReportingSubtotal> reporting = [.. reportingCategories
            .Select(category => new ReportingSubtotal(
                category.Name,
                category.FundingSource,
                allocated
                    .Where(transaction => transaction.FundingSource == category.FundingSource
                        && (string.Equals(transaction.Subcategory, category.Name, StringComparison.OrdinalIgnoreCase)
                            || string.Equals(transaction.Category, category.Name, StringComparison.OrdinalIgnoreCase)))
                    .Sum(transaction => -EffectiveGbpAmount(transaction))))
            .OrderBy(subtotal => subtotal.Name, StringComparer.Ordinal)];

        return new TransferPlan(
            cardPaymentTarget,
            [jointContribution, sharedContribution],
            additionalPersonal,
            transfers,
            balances,
            shortfalls,
            reporting);
    }

    /// <summary>The signed net card activity (purchases plus refunds) on the named card account.</summary>
    public static decimal NetCardTotal(IReadOnlyList<Transaction> allocated, string cardAccountName) =>
        allocated
            .Where(transaction => !IsExcluded(transaction)
                && string.Equals(transaction.Account, cardAccountName, StringComparison.Ordinal))
            .Sum(EffectiveGbpAmount);

    /// <summary>Net new card spending imported in this reconciliation (positive for spending).</summary>
    public static decimal NetImportedSpending(IReadOnlyList<Transaction> allocated, string cardAccountName) =>
        -NetCardTotal(allocated, cardAccountName);

    /// <summary>Difference between the entered target and the net imported card spending.</summary>
    public static decimal Discrepancy(decimal cardPaymentTarget, decimal netImportedSpending) =>
        cardPaymentTarget - netImportedSpending;

    /// <summary>Creates an unlocked draft whose transfers reflect the computed plan.</summary>
    public static ReconciliationDraft BuildDraft(
        decimal cardPaymentTarget,
        string cardAccountName,
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Transaction> allocated,
        IReadOnlyList<ReportingCategory> reportingCategories)
    {
        decimal netImportedSpending = NetImportedSpending(allocated, cardAccountName);
        decimal discrepancy = Discrepancy(cardPaymentTarget, netImportedSpending);
        return new ReconciliationDraft(
            cardPaymentTarget,
            accounts,
            allocated,
            ComputeTransfers(cardPaymentTarget, cardAccountName, accounts, allocated, reportingCategories).Transfers,
            IsLocked: false,
            IsSettled: false,
            discrepancy,
            DiscrepancyAcknowledged: discrepancy == 0m);
    }

    /// <summary>Records the user's decision to proceed despite a non-zero target discrepancy.</summary>
    public static ReconciliationDraft AcknowledgeDiscrepancy(ReconciliationDraft draft)
    {
        return draft.IsLocked || draft.IsSettled
            ? throw new InvalidOperationException("The discrepancy can only be acknowledged while the reconciliation is editable.")
            : (draft with { DiscrepancyAcknowledged = true });
    }

    /// <summary>
    /// Marks a transfer acknowledged. The first acknowledgement locks the draft and freezes amounts.
    /// </summary>
    public static ReconciliationDraft AcknowledgeTransfer(ReconciliationDraft draft, string source, TransferKind kind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (draft.IsSettled)
        {
            throw new InvalidOperationException("A settled reconciliation cannot change transfer acknowledgements.");
        }

        if (draft.Discrepancy != 0m && !draft.DiscrepancyAcknowledged)
        {
            throw new InvalidOperationException("Acknowledge the payment-target discrepancy before confirming transfers.");
        }

        if (draft.Transactions.Any(transaction => transaction.RequiresReview))
        {
            throw new InvalidOperationException("Resolve every transaction requiring review before confirming transfers.");
        }

        if (!draft.Transfers.Any(transfer => transfer.Source == source && transfer.Kind == kind))
        {
            throw new InvalidOperationException($"No {kind} transfer exists for {source}.");
        }

        List<Transfer> transfers = [.. draft.Transfers
            .Select(transfer => transfer.Source == source && transfer.Kind == kind
                ? transfer with { Acknowledged = true }
                : transfer)];

        return draft with { Transfers = transfers, IsLocked = true };
    }

    /// <summary>
    /// Reverts an accidental acknowledgement. The draft stays locked and agreed amounts stay fixed.
    /// </summary>
    public static ReconciliationDraft UndoTransferAcknowledgement(ReconciliationDraft draft, string source, TransferKind kind)
    {
        if (!draft.IsLocked)
        {
            throw new InvalidOperationException("There is no transfer acknowledgement to undo before locking.");
        }

        if (draft.IsSettled)
        {
            throw new InvalidOperationException("A settled reconciliation cannot change transfer acknowledgements.");
        }

        List<Transfer> transfers = [.. draft.Transfers
            .Select(transfer => transfer.Source == source && transfer.Kind == kind
                ? transfer with { Acknowledged = false }
                : transfer)];

        return draft with { Transfers = transfers };
    }

    /// <summary>Settles every included transaction together on the final acknowledgement.</summary>
    public static ReconciliationDraft AcknowledgeCardPaid(ReconciliationDraft draft)
    {
        if (draft.IsSettled)
        {
            throw new InvalidOperationException("The reconciliation is already settled.");
        }

        if (draft.Transactions.Any(transaction => transaction.RequiresReview))
        {
            throw new InvalidOperationException("Resolve every transaction requiring review before confirming the card payment.");
        }

        if (!draft.IsLocked || draft.Transfers.Any(transfer => transfer.Kind != TransferKind.CardPayment && !transfer.Acknowledged))
        {
            throw new InvalidOperationException("All funding transfers must be acknowledged before the card payment.");
        }

        List<Transaction> settled = [.. draft.Transactions.Select(transaction => transaction with { Settled = true })];
        List<Transfer> transfers = [.. draft.Transfers.Select(transfer =>
            transfer.Kind == TransferKind.CardPayment ? transfer with { Acknowledged = true } : transfer)];

        return draft with { Transactions = settled, Transfers = transfers, IsSettled = true };
    }

    /// <summary>
    /// Projects only the outstanding agreed transfers from balances that describe the current state.
    /// Acknowledged transfers are not applied a second time.
    /// </summary>
    public static IReadOnlyList<AccountBalance> ProjectRemainingTransfers(
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Transfer> agreedTransfers)
    {
        Account intermediate = RequireRole(accounts, AccountRole.Intermediate);
        Dictionary<string, decimal> balances = accounts.ToDictionary(
            account => account.Name,
            account => account.CurrentBalance,
            StringComparer.Ordinal);

        foreach (Transfer transfer in agreedTransfers.Where(transfer => !transfer.Acknowledged))
        {
            if (transfer.Kind == TransferKind.CardPayment)
            {
                balances[intermediate.Name] -= transfer.Amount;
                continue;
            }

            if (!balances.ContainsKey(transfer.Source))
            {
                throw new InvalidOperationException($"The account set is missing the transfer source {transfer.Source}.");
            }

            balances[transfer.Source] -= transfer.Amount;
            balances[intermediate.Name] += transfer.Amount;
        }

        return [.. accounts.Select(account => new AccountBalance(account.Name, balances[account.Name]))];
    }

    /// <summary>Reports protected-minimum breaches caused by the remaining agreed transfers.</summary>
    public static IReadOnlyList<Shortfall> RemainingShortfalls(
        IReadOnlyList<Account> accounts,
        IReadOnlyList<Transfer> agreedTransfers)
    {
        IReadOnlyList<AccountBalance> projected = ProjectRemainingTransfers(accounts, agreedTransfers);
        return [.. accounts
            .Join(projected, account => account.Name, balance => balance.AccountName, (account, balance) => new { account, balance })
            .Where(item => item.balance.ProjectedBalance < item.account.ProtectedMinimum)
            .Select(item => new Shortfall(
                item.account.Name,
                item.account.ProtectedMinimum - item.balance.ProjectedBalance))];
    }

    private static SourceContribution ComputeContribution(
        IReadOnlyList<Transaction> allocated,
        string cardAccountName,
        FundingSource source)
    {
        IReadOnlyList<Transaction> matches = [.. allocated.Where(transaction => transaction.FundingSource == source)];

        decimal purchases = matches
            .Where(transaction => !IsRefund(transaction)
                && string.Equals(transaction.Account, cardAccountName, StringComparison.Ordinal))
            .Sum(transaction => -EffectiveGbpAmount(transaction));

        decimal reimbursements = matches
            .Where(transaction => !IsRefund(transaction)
                && !string.Equals(transaction.Account, cardAccountName, StringComparison.Ordinal))
            .Sum(transaction => -EffectiveGbpAmount(transaction));

        decimal refunds = matches
            .Where(IsRefund)
            .Sum(transaction => -EffectiveGbpAmount(transaction));

        return new SourceContribution(source, purchases, reimbursements, refunds, purchases + reimbursements + refunds);
    }

    private static TransferKind KindFor(SourceContribution contribution) =>
        contribution.Total < 0m ? TransferKind.Refund : TransferKind.Funding;

    private static FundingSource? ResolveSource(
        Transaction transaction,
        IReadOnlyList<CategoryRule> categoryRules,
        IReadOnlyList<SubcategoryRule> subcategoryRules,
        IReadOnlyDictionary<string, FundingSource>? overrides)
    {
        if (overrides is not null && overrides.TryGetValue(transaction.Id, out FundingSource overrideSource))
        {
            return overrideSource;
        }

        SubcategoryRule? subcategory = subcategoryRules.FirstOrDefault(rule =>
            string.Equals(rule.Subcategory, transaction.Subcategory, StringComparison.OrdinalIgnoreCase));
        if (subcategory is not null)
        {
            return subcategory.FundingSource;
        }

        CategoryRule? category = categoryRules.FirstOrDefault(rule =>
            string.Equals(rule.Category, transaction.Category, StringComparison.OrdinalIgnoreCase));
        return category is not null
            ? category.FundingSource
            : string.IsNullOrWhiteSpace(transaction.Category) ? null : FundingSource.Personal;
    }

    private static Account RequireRole(IReadOnlyList<Account> accounts, AccountRole role) =>
        accounts.FirstOrDefault(account => account.Role == role)
        ?? throw new InvalidOperationException($"The account set is missing a {role} account.");

    private static void AddShortfallIfExceeded(List<Shortfall> shortfalls, Account account, decimal required)
    {
        decimal available = Math.Max(0m, account.CurrentBalance - account.ProtectedMinimum);
        if (required > available)
        {
            shortfalls.Add(new Shortfall(account.Name, required - available));
        }
    }

    private static decimal TransferFor(IReadOnlyList<Transfer> transfers, string source) =>
        transfers.Where(transfer => transfer.Source == source && transfer.Kind == TransferKind.Funding)
            .Sum(transfer => transfer.Amount);

    private static bool IsExcluded(Transaction transaction)
    {
        string type = transaction.Type.Trim();
        return type.Equals("Transfer", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Card repayment", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Card payment", StringComparison.OrdinalIgnoreCase)
            || type.Equals("Repayment", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRefund(Transaction transaction) =>
        transaction.Amount > 0m
        || transaction.Type.Trim().Equals("Refund", StringComparison.OrdinalIgnoreCase);

    private static bool RequiresActualGbpAmount(Transaction transaction) =>
        !transaction.Currency.Trim().Equals("GBP", StringComparison.OrdinalIgnoreCase)
        && transaction.ActualGbpAmount is null;

    public static decimal EffectiveGbpAmount(Transaction transaction) =>
        transaction.Currency.Trim().Equals("GBP", StringComparison.OrdinalIgnoreCase)
            ? transaction.Amount
            : transaction.ActualGbpAmount ?? 0m;
}
