namespace DesktopShell.Tests;

public sealed class ReconciliationEngineTests
{
    private const string Card = "Amex";

    [Fact]
    public void Base_fixture_produces_expected_transfers_and_balances()
    {
        TransferPlan plan = ComputePlan(BaseTransactions());

        SourceContribution joint = Plan(plan, FundingSource.Joint);
        SourceContribution shared = Plan(plan, FundingSource.SharedSavings);
        Assert.Equal(450m, joint.Total);
        Assert.Equal(250m, shared.Total);
        Assert.Equal(400m, plan.AdditionalPersonalTransfers);

        AssertTransfer(plan, "Joint", 450m);
        AssertTransfer(plan, "Shared Savings", 250m);
        AssertTransfer(plan, "Primary", 200m);
        AssertTransfer(plan, "Personal Savings", 200m);
        AssertTransfer(plan, "Intermediate", 1200m, TransferKind.CardPayment);

        AssertBalance(plan, "Intermediate", 80m);
        AssertBalance(plan, "Primary", 120m);

        Assert.Equal(150m, Reporting(plan, "Holidays"));
    }

    [Fact]
    public void Joint_bill_paid_from_personal_becomes_reimbursement_without_rededucting()
    {
        IReadOnlyList<Transaction> transactions = [.. BaseTransactions(), JointBillPaidFromPersonal()];

        TransferPlan plan = ComputePlan(transactions);

        SourceContribution joint = Plan(plan, FundingSource.Joint);
        Assert.Equal(510m, joint.Total);
        Assert.Equal(450m, joint.Purchases);
        Assert.Equal(60m, joint.Reimbursements);

        Assert.Equal(340m, plan.AdditionalPersonalTransfers);
        AssertTransfer(plan, "Joint", 510m);
        AssertTransfer(plan, "Primary", 200m);
        AssertTransfer(plan, "Personal Savings", 140m);
    }

    [Fact]
    public void Duplicate_import_ids_are_deduped()
    {
        IReadOnlyList<Transaction> transactions = [.. BaseTransactions(), BaseTransactions()[0]];

        IReadOnlyList<Transaction> deduped = ReconciliationEngine.Dedupe(transactions);

        Assert.Equal(BaseTransactions().Count, deduped.Count);
        Assert.Equal(1, deduped.Count(transaction => transaction.Id == "t1"));
    }

    [Fact]
    public void Refund_yields_an_outgoing_reimbursement()
    {
        IReadOnlyList<Transaction> transactions =
        [
            Tx("p1", -100m, "Groceries", linked: ""),
            Tx("r1", 150m, "Groceries", type: "Refund", linked: "p1"),
        ];
        IReadOnlyList<Transaction> allocated = ReconciliationEngine.Allocate(transactions, CategoryRules(), [], null);

        TransferPlan plan = ComputePlan(allocated);

        SourceContribution joint = Plan(plan, FundingSource.Joint);
        Assert.Equal(-50m, joint.Total);
        Transfer jointTransfer = plan.Transfers.Single(transfer => transfer.Source == "Joint");
        Assert.Equal(TransferKind.Refund, jointTransfer.Kind);
        Assert.Equal(-50m, jointTransfer.Amount);
    }

    [Fact]
    public void Joint_shortfall_is_reported_when_available_funds_are_insufficient()
    {
        IReadOnlyList<Account> accounts =
        [
            Account("Intermediate", AccountRole.Intermediate, 180m, 80m),
            Account("Joint", AccountRole.Joint, 500m, 0m),
            Account("Shared Savings", AccountRole.SharedSavings, 1000m, 0m),
            Account("Primary", AccountRole.PrimaryPersonal, 320m, 120m),
            Account("Personal Savings", AccountRole.PersonalSavings, 1000m, 0m),
        ];
        IReadOnlyList<Transaction> transactions = [Tx("j1", -600m, "Groceries")];

        IReadOnlyList<Transaction> allocated = ReconciliationEngine.Allocate(transactions, CategoryRules(), [], null);
        TransferPlan plan = ReconciliationEngine.ComputeTransfers(1200m, Card, accounts, allocated, Reporting());

        Shortfall shortfall = plan.Shortfalls.Single(shortfall => shortfall.AccountName == "Joint");
        Assert.Equal(100m, shortfall.Amount);
    }

    [Fact]
    public void Uncategorised_transaction_requires_review()
    {
        IReadOnlyList<Transaction> transactions = [Tx("u1", -25m, category: "")];

        IReadOnlyList<Transaction> allocated = ReconciliationEngine.Allocate(transactions, CategoryRules(), [], null);

        Transaction uncategorised = allocated.Single();
        Assert.True(uncategorised.RequiresReview);
        Assert.Null(uncategorised.FundingSource);
    }

    [Fact]
    public void Discrepancy_is_target_minus_net_imported_total()
    {
        IReadOnlyList<Transaction> allocated = ReconciliationEngine.Allocate(BaseTransactions(), CategoryRules(), [], null);

        decimal net = ReconciliationEngine.NetCardTotal(allocated, Card);
        decimal discrepancy = ReconciliationEngine.Discrepancy(1200m, ReconciliationEngine.NetImportedSpending(allocated, Card));

        Assert.Equal(-700m, net);
        Assert.Equal(500m, discrepancy);
    }

    [Fact]
    public void First_acknowledgement_locks_undo_keeps_lock_and_card_paid_settles()
    {
        IReadOnlyList<Transaction> allocated = ReconciliationEngine.Allocate(BaseTransactions(), CategoryRules(), [], null);
        ReconciliationDraft draft = ReconciliationEngine.BuildDraft(1200m, Card, BaseAccounts(), allocated, Reporting());

        Assert.False(draft.IsLocked);

        ReconciliationDraft locked = ReconciliationEngine.AcknowledgeTransfer(draft, "Joint", TransferKind.Funding);
        Assert.True(locked.IsLocked);
        Assert.True(locked.Transfers.Single(transfer => transfer.Source == "Joint").Acknowledged);

        ReconciliationDraft undone = ReconciliationEngine.UndoTransferAcknowledgement(locked, "Joint", TransferKind.Funding);
        Assert.True(undone.IsLocked);
        Assert.False(undone.Transfers.Single(transfer => transfer.Source == "Joint").Acknowledged);

        ReconciliationDraft acknowledged = AcknowledgeAllFunding(undone);
        ReconciliationDraft settled = ReconciliationEngine.AcknowledgeCardPaid(acknowledged);
        Assert.True(settled.IsSettled);
        Assert.All(settled.Transactions, transaction => Assert.True(transaction.Settled));
    }

    [Fact]
    public void Card_paid_requires_all_funding_transfers_acknowledged()
    {
        IReadOnlyList<Transaction> allocated = ReconciliationEngine.Allocate(BaseTransactions(), CategoryRules(), [], null);
        ReconciliationDraft draft = ReconciliationEngine.BuildDraft(1200m, Card, BaseAccounts(), allocated, Reporting());

        Assert.Throws<InvalidOperationException>(() => ReconciliationEngine.AcknowledgeCardPaid(draft));
    }

    private static ReconciliationDraft AcknowledgeAllFunding(ReconciliationDraft draft)
    {
        ReconciliationDraft current = draft;
        foreach (Transfer transfer in draft.Transfers.Where(transfer => transfer.Kind != TransferKind.CardPayment))
        {
            current = ReconciliationEngine.AcknowledgeTransfer(current, transfer.Source, transfer.Kind);
        }

        return current;
    }

    private static TransferPlan ComputePlan(IReadOnlyList<Transaction> transactions)
    {
        IReadOnlyList<Transaction> allocated = ReconciliationEngine.Allocate(transactions, CategoryRules(), [], null);
        return ReconciliationEngine.ComputeTransfers(1200m, Card, BaseAccounts(), allocated, Reporting());
    }

    private static IReadOnlyList<Transaction> BaseTransactions() =>
    [
        Tx("t1", -300m, "Groceries"),
        Tx("t2", -150m, "Dining"),
        Tx("t3", -150m, "Travel", subcategory: "Holidays"),
        Tx("t4", -100m, "Travel", subcategory: "Transport"),
    ];

    private static Transaction JointBillPaidFromPersonal() =>
        Tx("t5", -60m, "Groceries", account: "Primary");

    private static IReadOnlyList<Account> BaseAccounts() =>
    [
        Account("Intermediate", AccountRole.Intermediate, 180m, 80m),
        Account("Joint", AccountRole.Joint, 1000m, 0m),
        Account("Shared Savings", AccountRole.SharedSavings, 1000m, 0m),
        Account("Primary", AccountRole.PrimaryPersonal, 320m, 120m),
        Account("Personal Savings", AccountRole.PersonalSavings, 1000m, 0m),
    ];

    private static IReadOnlyList<CategoryRule> CategoryRules() =>
    [
        new("Groceries", FundingSource.Joint),
        new("Dining", FundingSource.Joint),
        new("Travel", FundingSource.SharedSavings),
    ];

    private static IReadOnlyList<ReportingCategory> Reporting() =>
    [
        new("Holidays", FundingSource.SharedSavings),
    ];

    private static Account Account(string name, AccountRole role, decimal balance, decimal minimum) =>
        new(name, role, balance, minimum);

    private static Transaction Tx(
        string id,
        decimal amount,
        string category,
        string subcategory = "",
        string type = "Purchase",
        string linked = "",
        string account = Card) =>
        new(
            id,
            new DateOnly(2026, 9, 1),
            amount,
            account,
            "Example Bank",
            "GBP",
            category,
            subcategory,
            type,
            "",
            "Counterparty",
            "",
            "Merchant",
            "",
            "",
            linked,
            null,
            false,
            false);

    private static SourceContribution Plan(TransferPlan plan, FundingSource source) =>
        plan.Contributions.Single(contribution => contribution.Source == source);

    private static void AssertTransfer(TransferPlan plan, string source, decimal amount, TransferKind kind = TransferKind.Funding)
    {
        Transfer transfer = plan.Transfers.Single(item => item.Source == source && item.Kind == kind);
        Assert.Equal(amount, transfer.Amount);
    }

    private static void AssertBalance(TransferPlan plan, string account, decimal amount)
    {
        AccountBalance balance = plan.ProjectedBalances.Single(item => item.AccountName == account);
        Assert.Equal(amount, balance.ProjectedBalance);
    }

    private static decimal Reporting(TransferPlan plan, string name) =>
        plan.ReportingSubtotals.Single(subtotal => subtotal.Name == name).Amount;
}