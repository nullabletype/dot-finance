using System.Collections.Immutable;

namespace DotFinance.Domain;

public sealed record ImportOutcome(int Added, int Unchanged, int Updated, int Corrections, int Conflicts);
public sealed record PendingCorrection(string TransactionId, ImportedTransaction Original, ImportedTransaction Proposed);
public sealed record AcceptedCorrection(string ReconciliationId, int Sequence, string TransactionId,
    ImmutableArray<AllocatedTransaction> Before, ImmutableArray<AllocatedTransaction> After);
public sealed record ImportConflict(string TransactionId, ImmutableArray<ImportedTransaction> Candidates);
public sealed record ReconciliationSnapshot(string Id, DateOnly Date, bool Locked, bool CardPaid,
    ImmutableArray<AllocatedTransaction> Transactions, ImmutableArray<Adjustment> Adjustments,
    ImmutableArray<AccountBalance> InitialBalances, ImmutableArray<AccountBalance> CurrentBalances,
    FundingPlan Plan, ImmutableArray<FundingTransfer> Transfers, bool DiscrepancyAcknowledged,
    ImmutableArray<ReviewItem> Reviews);

/// <summary>Owns one unfinished reconciliation and immutable completed snapshots. No I/O or system clock.</summary>
public sealed class ReconciliationBook(FundingConfiguration configuration, AllocationRules rules)
{
    private readonly Dictionary<string, ImportedTransaction> _records = new(StringComparer.Ordinal);
    private readonly HashSet<string> _settled = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AllocatedTransaction> _effective = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingCorrection> _corrections = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ImportConflict> _conflicts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, FundingSource> _overrides = new(StringComparer.Ordinal);
    private readonly List<ReconciliationSnapshot> _history = [];
    private readonly List<AcceptedCorrection> _acceptedCorrections = [];
    private readonly HashSet<string> _usedIds = new(StringComparer.Ordinal);
    private readonly List<Adjustment> _adjustments = [];
    private readonly HashSet<string> _activeIds = new(StringComparer.Ordinal);
    private AllocationRules _rules = rules;
    private int _adjustmentSequence;

    public FundingConfiguration Configuration { get; } = configuration;
    public ReconciliationSnapshot? Current { get; private set; }
    public ImmutableArray<AllocatedTransaction> SettledBaseline { get; private set; } = [];
    public ImmutableArray<AcceptedCorrection> AcceptedCorrections => [.. _acceptedCorrections];
    public ImmutableArray<ReconciliationSnapshot> History => [.. _history];
    public ImmutableArray<ImportedTransaction> Transactions => [.. _records.Values.OrderBy(x => x.Id, StringComparer.Ordinal)];
    public ImmutableArray<PendingCorrection> PendingCorrections => [.. _corrections.Values.OrderBy(x => x.TransactionId, StringComparer.Ordinal)];
    public ImmutableArray<ImportConflict> ImportConflicts => [.. _conflicts.Values.OrderBy(x => x.TransactionId, StringComparer.Ordinal)];
    public bool CanReopenLatest => Current is null && _history.Count > 0;
    public bool IsSettled(string transactionId) => _settled.Contains(transactionId);
    public bool IsReserved(string transactionId) => Current?.Locked == true && _activeIds.Contains(transactionId);

    public void ImportSettledBaseline(IEnumerable<ImportedTransaction> transactions, bool confirmedAlreadySettled)
    {
        if (!confirmedAlreadySettled || _records.Count > 0 || Current is not null || _history.Count > 0)
            throw new InvalidOperationException("A confirmed settled baseline is permitted only before any activity.");
        ImportedTransaction[] input = [.. transactions];
        ImmutableArray<AllocatedTransaction> allocated = FundingCalculator.Allocate(Configuration, _rules, input);
        if (allocated.Any(x => x.Reviews.Length != 0)) throw new InvalidOperationException("Resolve baseline allocation and currency reviews before confirmation.");
        SettledBaseline = allocated;
        foreach (AllocatedTransaction row in allocated)
        { _records.Add(row.Transaction.Id, row.Transaction); _settled.Add(row.Transaction.Id); _effective.Add(row.Transaction.Id, row); }
    }

    public ImportOutcome Import(IEnumerable<ImportedTransaction> transactions)
    {
        ImportedTransaction[] input = [.. transactions];
        foreach (ImportedTransaction transaction in input) DomainGuard.Transaction(transaction);
        int added = 0, unchanged = 0, updated = 0, corrections = 0, conflicts = 0;
        foreach (IGrouping<string, ImportedTransaction> group in input.GroupBy(x => x.Id).OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            ImmutableArray<ImportedTransaction> candidates = [.. group.Distinct()];
            if (candidates.Length > 1)
            { _conflicts[group.Key] = new(group.Key, candidates); conflicts++; continue; }
            if (_conflicts.ContainsKey(group.Key)) { conflicts++; continue; }
            Apply(candidates[0]);
        }
        Recompute();
        return new(added, unchanged, updated, corrections, conflicts);

        void Apply(ImportedTransaction incoming)
        {
            if (!_records.TryGetValue(incoming.Id, out ImportedTransaction? old))
            {
                _records.Add(incoming.Id, incoming); added++;
                if (Current is { Locked: false }) _activeIds.Add(incoming.Id);
                return;
            }
            incoming = PreserveValuation(old, incoming);
            if (old == incoming)
            {
                // A later export reverting an unaccepted change cancels that pending proposal.
                _corrections.Remove(incoming.Id); unchanged++; return;
            }
            if (_settled.Contains(incoming.Id) || IsReserved(incoming.Id))
            {
                if (_corrections.TryGetValue(incoming.Id, out PendingCorrection? pending) && pending.Proposed == incoming) unchanged++;
                else { _corrections[incoming.Id] = new(incoming.Id, old, incoming); corrections++; }
            }
            else { _records[incoming.Id] = incoming; updated++; }
        }
    }

    public void ResolveImportConflict(string transactionId, ImportedTransaction selected)
    {
        if (!_conflicts.TryGetValue(transactionId, out ImportConflict? conflict) || !conflict.Candidates.Contains(selected))
            throw new InvalidOperationException("Select one of the conflicting imported versions.");
        _conflicts.Remove(transactionId);
        Import([selected]);
    }

    public void Start(string reconciliationId, DateOnly date, decimal cardTargetGbp, IEnumerable<AccountBalance> balances)
    {
        DomainGuard.Text(reconciliationId);
        if (Current is not null || _usedIds.Contains(reconciliationId) || date == default)
            throw new InvalidOperationException("Supply a new dated identity with no unfinished reconciliation.");
        DomainGuard.Gbp(cardTargetGbp, nonNegative: true);
        ImmutableArray<AccountBalance> currentBalances = Configuration.ValidateBalances(balances);
        _usedIds.Add(reconciliationId);
        _activeIds.Clear(); _activeIds.UnionWith(_records.Keys.Where(x => !_settled.Contains(x)));
        _adjustments.Clear();
        FundingPlan plan = FundingCalculator.Plan(Configuration, cardTargetGbp, currentBalances, []);
        Current = new(reconciliationId, date, false, false, [], [], currentBalances, currentBalances, plan, [], false, []);
        Recompute();
    }

    public FundingPlan PreviewRules(AllocationRules rules)
    {
        ReconciliationSnapshot draft = Editable();
        return Calculate(rules, draft.Plan.CardTargetGbp, draft.CurrentBalances).Plan;
    }

    public void ApplyRules(AllocationRules rules)
    { Editable(); _rules = rules; Recompute(); }

    public void Override(string transactionId, FundingSource source)
    {
        Editable(); DomainGuard.Defined(source);
        if (!_activeIds.Contains(transactionId)) throw new InvalidOperationException("Only an included editable transaction may be overridden.");
        _overrides[transactionId] = source; Recompute();
    }

    public void CorrectCategory(string transactionId, string category, string subcategory)
    {
        Editable();
        if (!_activeIds.Contains(transactionId)) throw new InvalidOperationException("Transaction is not in this draft.");
        ImportedTransaction corrected = _records[transactionId] with { Category = category, Subcategory = subcategory };
        DomainGuard.Transaction(corrected); _records[transactionId] = corrected; Recompute();
    }

    public void SetActualGbp(string transactionId, decimal signedGbp)
    {
        Editable();
        if (!_activeIds.Contains(transactionId)) throw new InvalidOperationException("Transaction is not in this draft.");
        ImportedTransaction corrected = _records[transactionId] with { ActualGbpAmount = signedGbp };
        DomainGuard.Transaction(corrected); _records[transactionId] = corrected; Recompute();
    }

    public void SetCardTarget(decimal gbp)
    {
        ReconciliationSnapshot draft = Editable(); DomainGuard.Gbp(gbp, nonNegative: true);
        Current = draft with { Plan = draft.Plan with { CardTargetGbp = gbp }, DiscrepancyAcknowledged = false };
        Recompute();
    }

    public void SetBalances(IEnumerable<AccountBalance> balances)
    {
        ReconciliationSnapshot draft = RequireCurrent();
        ImmutableArray<AccountBalance> entered = Configuration.ValidateBalances(balances);
        Current = draft with { CurrentBalances = entered };
        if (!draft.Locked) Recompute();
        else RefreshProjection();
    }

    public void AcceptCorrection(string transactionId, FundingSource? responsibility = null, decimal? actualGbp = null)
    {
        Editable();
        if (!_corrections.TryGetValue(transactionId, out PendingCorrection? correction) || !_settled.Contains(transactionId))
            throw new InvalidOperationException("A correction can be accepted only after its original reconciliation settles, into an editable next draft.");
        ImportedTransaction proposed = correction.Proposed;
        if (actualGbp is not null) proposed = proposed with { ActualGbpAmount = actualGbp };
        DomainGuard.Transaction(proposed);
        Dictionary<string, FundingSource> temporaryOverrides = new(_overrides, StringComparer.Ordinal);
        if (responsibility is { } source) { DomainGuard.Defined(source); temporaryOverrides[transactionId] = source; }
        // Reallocate the root and every settled linked refund as one responsibility family.
        string root = proposed.Kind == TransactionKind.Refund ? proposed.LinkedTransactionId : proposed.Id;
        List<ImportedTransaction> family = [proposed];
        if (proposed.Kind == TransactionKind.Purchase)
            family.AddRange(_effective.Values.Where(x => x.Transaction.Kind == TransactionKind.Refund
                && x.Transaction.LinkedTransactionId == root).Select(x => x.Transaction));
        Dictionary<string, FundingSource> known = HistoricalSources();
        ImmutableArray<AllocatedTransaction> replacement = FundingCalculator.Allocate(Configuration, _rules, family, temporaryOverrides, known);
        if (replacement.Any(x => x.Reviews.Length != 0)) throw new InvalidOperationException("Resolve every correction allocation and actual GBP review.");
        int sequence = checked(_adjustmentSequence + 1);
        List<Adjustment> changes = [];
        foreach (AllocatedTransaction after in replacement)
        {
            AllocatedTransaction before = _effective[after.Transaction.Id];
            if (before.Source is { } oldSource && (before.CostGbp != 0m || before.CardCostGbp != 0m))
                changes.Add(new($"{sequence}:{after.Transaction.Id}:reverse", after.Transaction.Id, oldSource, -before.CostGbp, -before.CardCostGbp, before.Transaction.Category, before.Transaction.Subcategory));
            if (after.Source is { } newSource && (after.CostGbp != 0m || after.CardCostGbp != 0m))
                changes.Add(new($"{sequence}:{after.Transaction.Id}:replace", after.Transaction.Id, newSource, after.CostGbp, after.CardCostGbp, after.Transaction.Category, after.Transaction.Subcategory));
        }
        _acceptedCorrections.Add(new(Current!.Id, sequence, transactionId,
            [.. replacement.Select(x => _effective[x.Transaction.Id])], replacement));
        _adjustmentSequence = sequence;
        foreach (AllocatedTransaction after in replacement) { _effective[after.Transaction.Id] = after; _records[after.Transaction.Id] = after.Transaction; }
        if (responsibility is { } accepted) _overrides[transactionId] = accepted;
        _adjustments.AddRange(changes); _corrections.Remove(transactionId); Recompute();
    }

    public void RejectCorrection(string transactionId)
    {
        Editable();
        if (!_corrections.Remove(transactionId)) throw new InvalidOperationException("No correction awaits review.");
        Recompute();
    }

    public void AcknowledgeDiscrepancy()
    { ReconciliationSnapshot draft = Editable(); Current = draft with { DiscrepancyAcknowledged = true }; }

    public void AcknowledgeTransfer(string accountId)
    {
        ReconciliationSnapshot draft = RequireCurrent();
        EnsureReviewsResolved(draft);
        FundingTransfer transfer = draft.Transfers.SingleOrDefault(x => x.AccountId == accountId)
            ?? throw new InvalidOperationException("No transfer exists for that source.");
        if (transfer.Acknowledged) throw new InvalidOperationException("Transfer is already acknowledged.");
        FundingAccount source = Configuration.Accounts.Single(x => x.Id == accountId);
        FundingAccount intermediate = Configuration.Account(AccountRole.Intermediate);
        decimal sourceBalance = draft.CurrentBalances.Single(x => x.AccountId == accountId).Gbp;
        decimal intermediateBalance = draft.CurrentBalances.Single(x => x.AccountId == intermediate.Id).Gbp;
        if (sourceBalance < source.ProtectedMinimum || sourceBalance - transfer.SignedGbp < source.ProtectedMinimum
            || transfer.SignedGbp < 0m && intermediateBalance + transfer.SignedGbp < intermediate.ProtectedMinimum)
            throw new InvalidOperationException("Top up the affected account before acknowledging this transfer.");
        ImmutableArray<AccountBalance> balances = [.. draft.CurrentBalances.Select(x =>
            x.AccountId == accountId ? x with { Gbp = x.Gbp - transfer.SignedGbp }
            : x.AccountId == intermediate.Id ? x with { Gbp = x.Gbp + transfer.SignedGbp } : x)];
        Current = draft with
        {
            Locked = true,
            CurrentBalances = balances,
            Transfers = [.. draft.Transfers.Select(x => x.AccountId == accountId ? x with { Acknowledged = true } : x)],
        };
        RefreshProjection();
    }

    public void UndoTransferAcknowledgement(string accountId)
    {
        ReconciliationSnapshot draft = RequireCurrent();
        if (!draft.Locked || !draft.Transfers.Any(x => x.AccountId == accountId && x.Acknowledged))
            throw new InvalidOperationException("No acknowledged transfer can be undone.");
        Current = draft with { Transfers = [.. draft.Transfers.Select(x => x.AccountId == accountId ? x with { Acknowledged = false } : x)] };
        RefreshProjection(); // This records an outstanding step, never an imaginary reverse bank transfer.
    }

    public void AcknowledgeCardPaid()
    {
        ReconciliationSnapshot draft = RequireCurrent();
        EnsureReviewsResolved(draft);
        if (draft.Transfers.Any(x => !x.Acknowledged) || draft.Plan.Shortfalls.Length > 0)
            throw new InvalidOperationException("Resolve all funding and protected-minimum shortfalls before Card paid.");
        string intermediate = Configuration.Account(AccountRole.Intermediate).Id;
        ReconciliationSnapshot completed = draft with
        {
            Locked = true,
            CardPaid = true,
            CurrentBalances = [.. draft.CurrentBalances.Select(x => x.AccountId == intermediate ? x with { Gbp = x.Gbp - draft.Plan.CardTargetGbp } : x)],
        };
        foreach (AllocatedTransaction row in completed.Transactions)
        { _settled.Add(row.Transaction.Id); _effective[row.Transaction.Id] = row; }
        _history.Add(completed); Current = null;
        _activeIds.Clear(); _adjustments.Clear();
    }

    public void ReopenLatest()
    {
        if (!CanReopenLatest) throw new InvalidOperationException("Only the latest completion before any newer reconciliation can be reopened.");
        ReconciliationSnapshot completed = _history[^1];
        _history.RemoveAt(_history.Count - 1);
        Current = completed with { CardPaid = false };
        _activeIds.UnionWith(completed.Transactions.Select(x => x.Transaction.Id));
        foreach (string id in _activeIds) _settled.Remove(id);
        _adjustments.AddRange(completed.Adjustments);
        RefreshProjection(); // Balances remain current: reopening never reverses a real payment.
    }

    private ReconciliationSnapshot RequireCurrent() => Current ?? throw new InvalidOperationException("Start a reconciliation first.");
    private ReconciliationSnapshot Editable()
    {
        ReconciliationSnapshot draft = RequireCurrent();
        return !draft.Locked ? draft : throw new InvalidOperationException("Agreed transactions and amounts are locked.");
    }
    private void Recompute()
    {
        if (Current is not { Locked: false } draft) return;
        (ImmutableArray<AllocatedTransaction> rows, FundingPlan plan) = Calculate(_rules, draft.Plan.CardTargetGbp, draft.CurrentBalances);
        ImmutableArray<ReviewItem> reviews = [.. rows.SelectMany(x => x.Reviews)
            .Concat(_conflicts.Keys.Select(x => new ReviewItem(x, ReviewReason.Duplicate)))
            .Concat(_corrections.Keys.Select(x => new ReviewItem(x, ReviewReason.Correction)))
            .OrderBy(x => x.TransactionId, StringComparer.Ordinal).ThenBy(x => x.Reason)];
        Current = draft with
        {
            Transactions = rows,
            Adjustments = [.. _adjustments],
            Plan = plan,
            Transfers = plan.Transfers,
            Reviews = reviews,
            DiscrepancyAcknowledged = plan.DiscrepancyGbp == 0m || draft.DiscrepancyAcknowledged && plan.DiscrepancyGbp == draft.Plan.DiscrepancyGbp
        };
    }
    private (ImmutableArray<AllocatedTransaction> Rows, FundingPlan Plan) Calculate(AllocationRules rules, decimal target, ImmutableArray<AccountBalance> balances)
    {
        ImmutableArray<AllocatedTransaction> rows = FundingCalculator.Allocate(Configuration, rules,
            _activeIds.Select(x => _records[x]), _overrides, HistoricalSources());
        return (rows, FundingCalculator.Plan(Configuration, target, balances, rows, _adjustments, rules.Reporting));
    }
    private Dictionary<string, FundingSource> HistoricalSources() => _effective.Values.Where(x => x.Source is not null && x.Transaction.Kind == TransactionKind.Purchase)
        .ToDictionary(x => x.Transaction.Id, x => x.Source!.Value, StringComparer.Ordinal);
    private void RefreshProjection()
    {
        ReconciliationSnapshot draft = RequireCurrent();
        ImmutableArray<AccountBalance> projected = FundingCalculator.Project(Configuration, draft.CurrentBalances, draft.Transfers, draft.Plan.CardTargetGbp);
        Current = draft with
        {
            Plan = draft.Plan with
            {
                Transfers = draft.Transfers,
                ProjectedBalances = projected,
                Shortfalls = FundingCalculator.FindShortfalls(Configuration, draft.CurrentBalances, projected)
            }
        };
    }
    private static void EnsureReviewsResolved(ReconciliationSnapshot draft)
    {
        if (draft.Reviews.Length != 0 || !draft.DiscrepancyAcknowledged)
            throw new InvalidOperationException("Resolve required reviews and acknowledge the card-target discrepancy first.");
    }
    private static ImportedTransaction PreserveValuation(ImportedTransaction old, ImportedTransaction incoming)
    {
        return incoming.Currency.Equals("GBP", StringComparison.OrdinalIgnoreCase)
            ? incoming
            : (incoming with { ActualGbpAmount = old.Amount == incoming.Amount && old.Currency == incoming.Currency ? old.ActualGbpAmount : null });
    }
}