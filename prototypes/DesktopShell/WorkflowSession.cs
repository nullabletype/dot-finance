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

    public IReadOnlyList<ReconciliationDraft> CompletedReconciliations => _completed;

    public IReadOnlyList<Transaction> Allocated { get; private set; } = [];

    public TransferPlan Plan { get; private set; } = null!;

    public ReconciliationDraft? Draft { get; private set; }

    /// <summary>Imports a fixture, skipping Ids already present, then recomputes. Returns (added, skipped).</summary>
    public (int Added, int Skipped) Import(string path)
    {
        IReadOnlyList<Transaction> incoming = ReconciliationImport.Read(path);
        HashSet<string> existing = _imported
            .Concat(_pending)
            .Select(transaction => transaction.Id)
            .ToHashSet(StringComparer.Ordinal);
        List<Transaction> destination = Draft?.IsLocked == true ? _pending : _imported;
        int added = 0;
        int skipped = 0;
        foreach (Transaction transaction in ReconciliationEngine.Dedupe(incoming))
        {
            if (existing.Add(transaction.Id))
            {
                destination.Add(transaction);
                added++;
            }
            else
            {
                skipped++;
            }
        }

        Recompute();
        return (added, skipped);
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

        Draft = ReconciliationEngine.AcknowledgeCardPaid(Draft);
        _completed.Add(Draft);
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
        ThemeSettings.Load());

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
}