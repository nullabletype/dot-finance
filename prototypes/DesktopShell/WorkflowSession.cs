namespace DesktopShell;

/// <summary>
/// Mutable workflow state that drives the guided reconciliation UI. It reuses the pure
/// <see cref="ReconciliationEngine"/> to recompute allocations and transfers after each change.
/// </summary>
public sealed class WorkflowSession
{
    public const string CardAccountName = "Fictional Amex";

    private readonly List<Transaction> _imported = [];
    private readonly List<Transaction> _pending = [];
    private readonly List<ReconciliationDraft> _completed = [];
    private readonly List<PendingTransactionCorrection> _pendingCorrections = [];
    private readonly Dictionary<string, FundingSource> _overrides = new(StringComparer.Ordinal);

    public WorkflowSession()
    {
        Accounts =
        [
            new Account("Intermediate", AccountRole.Intermediate, 180m, 80m),
            new Account("Joint", AccountRole.Joint, 1000m, 0m),
            new Account("Shared Savings", AccountRole.SharedSavings, 1000m, 0m),
            new Account("Primary", AccountRole.PrimaryPersonal, 320m, 120m),
            new Account("Personal Savings", AccountRole.PersonalSavings, 1000m, 0m),
        ];
        CategoryRules =
        [
            new CategoryRule("Groceries", FundingSource.Joint),
            new CategoryRule("Dining", FundingSource.Joint),
            new CategoryRule("Travel", FundingSource.SharedSavings),
        ];
        SubcategoryRules = [];
        ReportingCategories = [new ReportingCategory("Holidays", FundingSource.SharedSavings)];
        CardPaymentTarget = 1200m;
        Recompute();
    }

    public IReadOnlyList<Account> Accounts { get; private set; }

    public IReadOnlyList<CategoryRule> CategoryRules { get; private set; }

    public IReadOnlyList<SubcategoryRule> SubcategoryRules { get; private set; }

    public IReadOnlyList<ReportingCategory> ReportingCategories { get; private set; }

    public decimal CardPaymentTarget { get; private set; }

    public IReadOnlyList<Transaction> Imported => _imported;

    public int PendingTransactionCount => _pending.Count;

    public IReadOnlyList<PendingTransactionCorrection> PendingCorrections => _pendingCorrections;

    public IReadOnlyList<ReconciliationDraft> CompletedReconciliations => _completed;

    public IReadOnlyList<Transaction> Allocated { get; private set; } = [];

    public TransferPlan Plan { get; private set; } = null!;

    public ReconciliationDraft? Draft { get; private set; }

    /// <summary>Imports new transactions, updates editable matches, and queues locked or settled corrections.</summary>
    public ImportResult Import(string path)
    {
        IReadOnlyList<Transaction> incoming = ReconciliationImport.Read(path);
        int added = 0;
        int skipped = 0;
        int updated = 0;
        int correctionsQueued = 0;
        foreach (Transaction transaction in ReconciliationEngine.Dedupe(incoming))
        {
            int currentIndex = FindById(_imported, transaction.Id);
            if (currentIndex >= 0)
            {
                Transaction current = _imported[currentIndex];
                if (SameImportedFields(current, transaction))
                {
                    skipped++;
                }
                else if (Draft?.IsLocked == true)
                {
                    if (QueueCorrection(current, transaction))
                    {
                        correctionsQueued++;
                    }
                    else
                    {
                        skipped++;
                    }
                }
                else
                {
                    _imported[currentIndex] = transaction;
                    updated++;
                }

                continue;
            }

            int pendingIndex = FindById(_pending, transaction.Id);
            if (pendingIndex >= 0)
            {
                if (SameImportedFields(_pending[pendingIndex], transaction))
                {
                    skipped++;
                }
                else
                {
                    _pending[pendingIndex] = transaction;
                    updated++;
                }

                continue;
            }

            Transaction? historical = _completed
                .AsEnumerable()
                .Reverse()
                .SelectMany(reconciliation => reconciliation.Transactions)
                .FirstOrDefault(candidate => string.Equals(candidate.Id, transaction.Id, StringComparison.Ordinal));
            if (historical is not null)
            {
                if (SameImportedFields(historical, transaction))
                {
                    skipped++;
                }
                else
                {
                    if (QueueCorrection(historical, transaction))
                    {
                        correctionsQueued++;
                    }
                    else
                    {
                        skipped++;
                    }
                }

                continue;
            }

            if (Draft?.IsLocked == true)
            {
                _pending.Add(transaction);
            }
            else
            {
                _imported.Add(transaction);
            }

            added++;
        }

        Recompute();
        return new ImportResult(added, skipped, updated, correctionsQueued);
    }

    /// <summary>Corrects imported category data in the editable draft and recomputes immediately.</summary>
    public void CorrectTransaction(string transactionId, string category, string subcategory)
    {
        EnsureEditable("Transaction corrections");
        int index = _imported.FindIndex(transaction => string.Equals(transaction.Id, transactionId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException($"Transaction {transactionId} is not in the current draft.");
        }

        _imported[index] = _imported[index] with
        {
            Category = category.Trim(),
            Subcategory = subcategory.Trim(),
        };
        Recompute();
    }

    /// <summary>Records the actual signed GBP amount for a non-GBP transaction.</summary>
    public void SetActualGbpAmount(string transactionId, decimal actualGbpAmount)
    {
        EnsureEditable("Actual GBP amounts");
        int index = FindById(_imported, transactionId);
        if (index < 0)
        {
            throw new InvalidOperationException($"Transaction {transactionId} is not in the current draft.");
        }

        if (_imported[index].Currency.Trim().Equals("GBP", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("An actual GBP amount is only required for a non-GBP transaction.");
        }

        _imported[index] = _imported[index] with { ActualGbpAmount = actualGbpAmount };
        Recompute();
    }

    /// <summary>Accepts a queued correction as reversing and replacement adjustments in the editable draft.</summary>
    public void AcceptPendingCorrection(string transactionId)
    {
        EnsureEditable("Pending corrections");
        int index = _pendingCorrections.FindIndex(correction =>
            string.Equals(correction.Original.Id, transactionId, StringComparison.Ordinal));
        if (index < 0)
        {
            throw new InvalidOperationException($"Transaction {transactionId} has no pending correction.");
        }

        PendingTransactionCorrection correction = _pendingCorrections[index];
        string reversalId = $"adjustment:{transactionId}:reversal";
        string replacementId = $"adjustment:{transactionId}:replacement";
        if (_imported.Any(transaction => transaction.Id == reversalId || transaction.Id == replacementId))
        {
            throw new InvalidOperationException($"Transaction {transactionId} already has an adjustment in this draft.");
        }

        Transaction reversal = correction.Original with
        {
            Id = reversalId,
            Amount = -correction.Original.Amount,
            Type = "Correction reversal",
            LinkedTransactionId = transactionId,
            FundingSource = null,
            Settled = false,
            RequiresReview = false,
            ActualGbpAmount = correction.Original.ActualGbpAmount is { } amount ? -amount : null,
        };
        Transaction replacement = correction.Updated with
        {
            Id = replacementId,
            Type = "Correction replacement",
            LinkedTransactionId = transactionId,
            FundingSource = null,
            Settled = false,
            RequiresReview = false,
        };
        _imported.Add(reversal);
        _imported.Add(replacement);
        if (correction.Original.FundingSource is { } originalSource)
        {
            _overrides[reversalId] = originalSource;
        }

        _pendingCorrections.RemoveAt(index);
        Recompute();
    }

    /// <summary>Sets the current balance of the named account and recomputes.</summary>
    public void SetBalance(string accountName, decimal balance)
    {
        Accounts = [.. Accounts
            .Select(account => string.Equals(account.Name, accountName, StringComparison.Ordinal)
                ? account with { CurrentBalance = balance }
                : account)];
        Recompute();
    }

    public void SetCardPaymentTarget(decimal target)
    {
        EnsureEditable("The card payment target");
        if (target < 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(target), "The card payment target cannot be negative.");
        }

        CardPaymentTarget = target;
        Recompute();
    }

    /// <summary>Sets or clears an allocation override for a transaction Id, then recomputes.</summary>
    public void SetOverride(string transactionId, FundingSource? source)
    {
        EnsureEditable("Allocation overrides");
        if (!_imported.Any(transaction => string.Equals(transaction.Id, transactionId, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException($"Transaction {transactionId} is not in the current draft.");
        }

        if (source is { } value)
        {
            _overrides[transactionId] = value;
        }
        else
        {
            _overrides.Remove(transactionId);
        }

        Recompute();
    }

    /// <summary>Applies a new category-rule mapping and recomputes from scratch.</summary>
    public void ApplyCategoryRule(string category, FundingSource source)
    {
        EnsureEditable("Category rules");
        CategoryRules =
        [
            .. CategoryRules
                .Where(rule => !string.Equals(rule.Category, category, StringComparison.OrdinalIgnoreCase)),
            new CategoryRule(category, source),
        ];
        Recompute();
    }

    /// <summary>Computes a would-be plan for a category-rule change without applying it.</summary>
    public TransferPlan PreviewRuleChange(string category, FundingSource source)
    {
        IReadOnlyList<CategoryRule> modified =
        [
            .. CategoryRules
                .Where(rule => !string.Equals(rule.Category, category, StringComparison.OrdinalIgnoreCase)),
            new CategoryRule(category, source),
        ];
        IReadOnlyList<Transaction> current = Draft?.IsLocked == true ? Draft.Transactions : _imported;
        IReadOnlyList<Transaction> preview = ReconciliationEngine.Allocate(current, modified, SubcategoryRules, _overrides);
        decimal target = Draft?.IsLocked == true ? Draft.CardPaymentTarget : CardPaymentTarget;
        return ReconciliationEngine.ComputeTransfers(target, CardAccountName, Accounts, preview, ReportingCategories);
    }

    /// <summary>Recomputes allocations and the funding plan from the current imported transactions.</summary>
    public void Recompute()
    {
        if (Draft?.IsLocked == true)
        {
            Allocated = Draft.Transactions;
            TransferPlan computed = ReconciliationEngine.ComputeTransfers(
                Draft.CardPaymentTarget,
                CardAccountName,
                Accounts,
                Draft.Transactions,
                ReportingCategories);
            Plan = computed with
            {
                Transfers = Draft.Transfers,
                ProjectedBalances = ReconciliationEngine.ProjectRemainingTransfers(Accounts, Draft.Transfers),
                Shortfalls = ReconciliationEngine.RemainingShortfalls(Accounts, Draft.Transfers),
            };
            return;
        }

        Allocated = ReconciliationEngine.Allocate(_imported, CategoryRules, SubcategoryRules, _overrides);
        Plan = ReconciliationEngine.ComputeTransfers(CardPaymentTarget, CardAccountName, Accounts, Allocated, ReportingCategories);
        decimal netImportedSpending = ReconciliationEngine.NetImportedSpending(Allocated, CardAccountName);
        decimal discrepancy = ReconciliationEngine.Discrepancy(CardPaymentTarget, netImportedSpending);
        bool discrepancyAcknowledged = discrepancy == 0m
            || Draft is { IsLocked: false, DiscrepancyAcknowledged: true }
                && Draft.Discrepancy == discrepancy;
        Draft = new ReconciliationDraft(
            CardPaymentTarget,
            Accounts,
            Allocated,
            Plan.Transfers,
            IsLocked: false,
            IsSettled: false,
            discrepancy,
            discrepancyAcknowledged);
    }

    public void AcknowledgeDiscrepancy()
    {
        if (Draft is null)
        {
            return;
        }

        Draft = ReconciliationEngine.AcknowledgeDiscrepancy(Draft);
    }

    public void AcknowledgeTransfer(string source, TransferKind kind)
    {
        if (Draft is null)
        {
            return;
        }

        if (!Draft.IsLocked && _pendingCorrections.Count > 0)
        {
            throw new InvalidOperationException("Review every pending transaction correction before confirming transfers.");
        }

        Account? sourceAccount = Accounts.FirstOrDefault(account => account.Name == source);
        bool isPersonal = sourceAccount?.Role is AccountRole.PrimaryPersonal or AccountRole.PersonalSavings;
        if (Plan.Shortfalls.Any(shortfall => shortfall.AccountName == source
            || isPersonal && shortfall.AccountName == "Personal"))
        {
            throw new InvalidOperationException($"The {source} transfer is blocked by a protected-balance shortfall.");
        }

        Draft = ReconciliationEngine.AcknowledgeTransfer(Draft, source, kind);
        Recompute();
    }

    public void UndoTransferAcknowledgement(string source, TransferKind kind)
    {
        if (Draft is null)
        {
            return;
        }

        Draft = ReconciliationEngine.UndoTransferAcknowledgement(Draft, source, kind);
        Recompute();
    }

    public void AcknowledgeCardPaid()
    {
        if (Draft is null)
        {
            return;
        }

        ReconciliationDraft completed = ReconciliationEngine.AcknowledgeCardPaid(Draft);
        _completed.Add(completed);
        HashSet<string> completedIds = completed.Transactions
            .Select(transaction => transaction.Id)
            .ToHashSet(StringComparer.Ordinal);
        _imported.Clear();
        _imported.AddRange(_pending);
        _pending.Clear();
        foreach (string id in completedIds)
        {
            _overrides.Remove(id);
        }

        Draft = null;
        Recompute();
    }

    /// <summary>Reopens the latest completion when no newer reconciliation activity exists.</summary>
    public void ReopenLatestCompleted()
    {
        if (_completed.Count == 0)
        {
            throw new InvalidOperationException("There is no completed reconciliation to reopen.");
        }

        if (_imported.Count != 0 || _pending.Count != 0 || _pendingCorrections.Count != 0)
        {
            throw new InvalidOperationException("The latest reconciliation cannot be reopened after a newer reconciliation has started.");
        }

        ReconciliationDraft completed = _completed[^1];
        _completed.RemoveAt(_completed.Count - 1);
        List<Transaction> reopenedTransactions = [.. completed.Transactions
            .Select(transaction => transaction with { Settled = false })];
        List<Transfer> reopenedTransfers = [.. completed.Transfers
            .Select(transfer => transfer.Kind == TransferKind.CardPayment
                ? transfer with { Acknowledged = false }
                : transfer)];
        _imported.AddRange(reopenedTransactions);
        CardPaymentTarget = completed.CardPaymentTarget;
        Draft = completed with
        {
            Transactions = reopenedTransactions,
            Transfers = reopenedTransfers,
            IsSettled = false,
            IsLocked = true,
        };
        Recompute();
    }

    /// <summary>Serialises the current workflow into the persistence shape.</summary>
    public ScenarioState ToState() => new(
        Accounts,
        CategoryRules,
        SubcategoryRules,
        ReportingCategories,
        _imported,
        _pending,
        _overrides,
        Draft,
        _completed,
        ThemeSettings.Load(),
        _pendingCorrections);

    /// <summary>Restores a previously saved workflow without re-deriving funding sources.</summary>
    public void FromState(ScenarioState state)
    {
        Accounts = state.Accounts;
        CategoryRules = state.CategoryRules;
        SubcategoryRules = state.SubcategoryRules;
        ReportingCategories = state.ReportingCategories;
        _imported.Clear();
        _imported.AddRange(state.Transactions);
        _pending.Clear();
        _pending.AddRange(state.PendingTransactions);
        _overrides.Clear();
        foreach ((string id, FundingSource source) in state.Overrides)
        {
            _overrides[id] = source;
        }

        _completed.Clear();
        _completed.AddRange(state.CompletedReconciliations);
        _pendingCorrections.Clear();
        _pendingCorrections.AddRange(state.PendingCorrections ?? []);
        Draft = state.Draft;

        if (Draft is not null)
        {
            CardPaymentTarget = Draft.CardPaymentTarget;
            Recompute();
        }
        else
        {
            Recompute();
        }
    }

    private void EnsureEditable(string subject)
    {
        if (Draft?.IsLocked == true)
        {
            throw new InvalidOperationException($"{subject} cannot change after the first transfer locks the reconciliation.");
        }
    }

    private static int FindById(List<Transaction> transactions, string id) =>
        transactions.FindIndex(transaction => string.Equals(transaction.Id, id, StringComparison.Ordinal));

    private bool QueueCorrection(Transaction original, Transaction updated)
    {
        int index = _pendingCorrections.FindIndex(correction =>
            string.Equals(correction.Original.Id, original.Id, StringComparison.Ordinal));
        if (index >= 0)
        {
            if (SameImportedFields(_pendingCorrections[index].Updated, updated))
            {
                return false;
            }

            _pendingCorrections[index] = new PendingTransactionCorrection(original, updated);
            return true;
        }

        _pendingCorrections.Add(new PendingTransactionCorrection(original, updated));
        return true;
    }

    private static bool SameImportedFields(Transaction left, Transaction right) =>
        left.Id == right.Id
        && left.Date == right.Date
        && left.Amount == right.Amount
        && left.Account == right.Account
        && left.Bank == right.Bank
        && left.Currency == right.Currency
        && left.Category == right.Category
        && left.Subcategory == right.Subcategory
        && left.Type == right.Type
        && left.Tags == right.Tags
        && left.Counterparty == right.Counterparty
        && left.CustomName == right.CustomName
        && left.Merchant == right.Merchant
        && left.AdditionalDetails == right.AdditionalDetails
        && left.Notes == right.Notes
        && left.LinkedTransactionId == right.LinkedTransactionId;
}
