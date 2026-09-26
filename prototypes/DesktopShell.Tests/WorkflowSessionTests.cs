namespace DesktopShell.Tests;

public sealed class WorkflowSessionTests
{
    [Fact]
    public void Base_fixture_and_joint_bill_match_the_walkthrough_totals()
    {
        WorkflowSession session = new();

        session.Import(Fixture("synthetic-reconciliation.csv"));

        Assert.Equal(450m, Contribution(session, FundingSource.Joint));
        Assert.Equal(250m, Contribution(session, FundingSource.SharedSavings));
        Assert.Equal(400m, session.Plan.AdditionalPersonalTransfers);
        AssertTransfer(session.Plan, "Primary", 200m);
        AssertTransfer(session.Plan, "Personal Savings", 200m);
        AssertBalance(session.Plan, "Intermediate", 80m);
        AssertBalance(session.Plan, "Primary", 120m);
        Assert.Equal(150m, session.Plan.ReportingSubtotals.Single(subtotal => subtotal.Name == "Holidays").Amount);
        Assert.Equal(25m, session.Allocated.Single(transaction => transaction.Id == "R-0005" && transaction.RequiresReview).Amount * -1m);

        session.Import(Fixture("synthetic-reconciliation-bill.csv"));

        Assert.Equal(510m, Contribution(session, FundingSource.Joint));
        Assert.Equal(340m, session.Plan.AdditionalPersonalTransfers);
        AssertTransfer(session.Plan, "Primary", 200m);
        AssertTransfer(session.Plan, "Personal Savings", 140m);
        Assert.Equal(320m, session.Accounts.Single(account => account.Name == "Primary").CurrentBalance);
    }

    [Fact]
    public void Preview_does_not_replace_current_totals_but_accepted_edits_do()
    {
        WorkflowSession session = new();
        session.Import(Fixture("synthetic-reconciliation.csv"));

        TransferPlan preview = session.PreviewRuleChange("Groceries", FundingSource.Personal);

        Assert.Equal(150m, Contribution(preview, FundingSource.Joint));
        Assert.Equal(450m, Contribution(session, FundingSource.Joint));

        session.ApplyCategoryRule("Groceries", FundingSource.Personal);
        Assert.Equal(150m, Contribution(session, FundingSource.Joint));

        session.SetOverride("R-0001", FundingSource.Joint);
        Assert.Equal(450m, Contribution(session, FundingSource.Joint));

        session.CorrectTransaction("R-0005", "Dining", string.Empty);
        Assert.Equal(475m, Contribution(session, FundingSource.Joint));
        Assert.DoesNotContain(session.Allocated, transaction => transaction.RequiresReview);
    }

    [Fact]
    public void Duplicate_and_post_lock_imports_are_not_added_to_the_locked_draft()
    {
        WorkflowSession session = new();
        session.Import(Fixture("synthetic-reconciliation.csv"));
        (int added, int skipped) = session.Import(Fixture("synthetic-reconciliation.csv"));
        session.AcknowledgeDiscrepancy();
        session.AcknowledgeTransfer("Joint", TransferKind.Funding);
        IReadOnlyList<string> lockedIds = [.. session.Allocated.Select(transaction => transaction.Id)];

        (int queued, int queueSkipped) = session.Import(Fixture("synthetic-refund.csv"));

        Assert.Equal(0, added);
        Assert.Equal(6, skipped);
        Assert.Equal(2, queued);
        Assert.Equal(0, queueSkipped);
        Assert.Equal(2, session.PendingTransactionCount);
        Assert.Equal(lockedIds, session.Allocated.Select(transaction => transaction.Id));
        Assert.Throws<InvalidOperationException>(() => session.SetOverride("R-0001", FundingSource.Personal));
        Assert.Equal(450m, Contribution(session, FundingSource.Joint));
    }

    [Fact]
    public void Balance_updates_after_acknowledgement_project_only_remaining_steps()
    {
        WorkflowSession session = new();
        session.Import(Fixture("synthetic-reconciliation.csv"));
        session.Import(Fixture("synthetic-reconciliation-bill.csv"));
        session.AcknowledgeDiscrepancy();
        session.AcknowledgeTransfer("Joint", TransferKind.Funding);

        session.SetBalance("Joint", 490m);
        session.SetBalance("Intermediate", 690m);

        Assert.True(session.Draft!.IsLocked);
        AssertBalance(session.Plan, "Joint", 490m);
        AssertBalance(session.Plan, "Intermediate", 80m);
        Assert.Equal(510m, session.Draft.Transfers.Single(transfer => transfer.Source == "Joint").Amount);
    }

    [Fact]
    public void Insufficient_funds_block_the_affected_transfer_acknowledgement()
    {
        WorkflowSession session = new();
        session.Import(Fixture("synthetic-reconciliation.csv"));
        session.SetBalance("Joint", 400m);
        session.AcknowledgeDiscrepancy();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => session.AcknowledgeTransfer("Joint", TransferKind.Funding));

        Assert.Contains("shortfall", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(session.Draft!.IsLocked);
        Assert.Equal(50m, session.Plan.Shortfalls.Single(shortfall => shortfall.AccountName == "Joint").Amount);
    }

    [Fact]
    public void Reconciliation_import_retains_all_fields_and_signed_values()
    {
        IReadOnlyList<Transaction> transactions = ReconciliationImport.Read(Fixture("synthetic-refund.csv"));

        Transaction refund = transactions.Single(transaction => transaction.Id == "R-0101");
        Assert.Equal(new DateOnly(2026, 9, 12), refund.Date);
        Assert.Equal(150m, refund.Amount);
        Assert.Equal("Fictional Amex", refund.Account);
        Assert.Equal("Example Bank", refund.Bank);
        Assert.Equal("GBP", refund.Currency);
        Assert.Equal("Groceries", refund.Category);
        Assert.Equal(string.Empty, refund.Subcategory);
        Assert.Equal("Refund", refund.Type);
        Assert.Equal(string.Empty, refund.Tags);
        Assert.Equal("Example Market", refund.Counterparty);
        Assert.Equal(string.Empty, refund.CustomName);
        Assert.Equal("Example Market Ltd", refund.Merchant);
        Assert.Equal("Synthetic refund", refund.AdditionalDetails);
        Assert.Equal("Synthetic refund larger than purchase", refund.Notes);
        Assert.Equal("R-0100", refund.LinkedTransactionId);
        Assert.Null(refund.FundingSource);
        Assert.False(refund.Settled);
        Assert.False(refund.RequiresReview);
    }

    [Fact]
    public void Discrepancy_lock_undo_and_card_paid_preserve_state_and_history()
    {
        WorkflowSession session = new();
        session.Import(Fixture("synthetic-reconciliation.csv"));
        Assert.NotEqual(0m, session.Draft!.Discrepancy);

        Assert.Throws<InvalidOperationException>(() => session.AcknowledgeTransfer("Joint", TransferKind.Funding));
        session.AcknowledgeDiscrepancy();
        session.AcknowledgeTransfer("Joint", TransferKind.Funding);
        session.UndoTransferAcknowledgement("Joint", TransferKind.Funding);

        Assert.True(session.Draft.IsLocked);
        Assert.False(session.Draft.Transfers.Single(transfer => transfer.Source == "Joint").Acknowledged);

        foreach (Transfer transfer in session.Draft.Transfers.Where(transfer => transfer.Kind != TransferKind.CardPayment))
        {
            session.AcknowledgeTransfer(transfer.Source, transfer.Kind);
        }

        session.AcknowledgeCardPaid();

        Assert.True(session.Draft.IsSettled);
        Assert.All(session.Draft.Transactions, transaction => Assert.True(transaction.Settled));
        Assert.True(session.Draft.Transfers.Single(transfer => transfer.Kind == TransferKind.CardPayment).Acknowledged);
        ReconciliationDraft history = Assert.Single(session.CompletedReconciliations);
        Assert.Equal(session.Draft, history);
    }

    [Fact]
    public void Scenario_round_trip_preserves_locked_state_overrides_pending_imports_and_history()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dot-finance-workflow-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "workflow.db");
        const string password = "fictional-test-password";
        try
        {
            WorkflowSession session = new();
            session.Import(Fixture("synthetic-reconciliation.csv"));
            session.SetOverride("R-0005", FundingSource.Joint);
            session.AcknowledgeDiscrepancy();
            foreach (Transfer transfer in session.Draft!.Transfers.Where(transfer => transfer.Kind != TransferKind.CardPayment))
            {
                session.AcknowledgeTransfer(transfer.Source, transfer.Kind);
            }

            session.AcknowledgeCardPaid();
            session.Import(Fixture("synthetic-refund.csv"));

            using (PrototypeVault vault = PrototypeVault.Create(databasePath, password))
            {
                vault.SaveScenario(session.ToState());
            }

            ScenarioState restoredState;
            using (PrototypeVault vault = PrototypeVault.Unlock(databasePath, password))
            {
                restoredState = vault.LoadScenario();
            }

            WorkflowSession restored = new();
            restored.FromState(restoredState);

            Assert.True(restored.Draft!.IsSettled);
            Assert.True(restored.Draft.DiscrepancyAcknowledged);
            Assert.Equal(FundingSource.Joint, restored.Allocated.Single(transaction => transaction.Id == "R-0005").FundingSource);
            Assert.Equal(2, restored.PendingTransactionCount);
            Assert.Equal(475m, Contribution(restored, FundingSource.Joint));
            Assert.Single(restored.CompletedReconciliations);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void Theme_setting_round_trip_and_invalid_content_fall_back_safely()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dot-finance-theme-{Guid.NewGuid():N}");
        string path = Path.Combine(directory, "theme.json");
        try
        {
            ThemeSettings.Save(path, "Dark");
            Assert.Equal("Dark", ThemeSettings.Load(path));

            File.WriteAllText(path, "{ not-json }");
            Assert.Equal("System", ThemeSettings.Load(path));
            Assert.Throws<ArgumentOutOfRangeException>(() => ThemeSettings.Save(path, "Sepia"));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static decimal Contribution(WorkflowSession session, FundingSource source) => Contribution(session.Plan, source);

    private static decimal Contribution(TransferPlan plan, FundingSource source) =>
        plan.Contributions.Single(contribution => contribution.Source == source).Total;

    private static void AssertTransfer(TransferPlan plan, string source, decimal amount)
    {
        Transfer transfer = plan.Transfers.Single(transfer => transfer.Source == source && transfer.Kind == TransferKind.Funding);
        Assert.Equal(amount, transfer.Amount);
    }

    private static void AssertBalance(TransferPlan plan, string account, decimal amount)
    {
        AccountBalance balance = plan.ProjectedBalances.Single(balance => balance.AccountName == account);
        Assert.Equal(amount, balance.ProjectedBalance);
    }
}