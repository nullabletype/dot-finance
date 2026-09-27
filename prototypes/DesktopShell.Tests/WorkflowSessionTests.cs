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
        session.CorrectTransaction("R-0005", "Groceries", string.Empty);
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
        Assert.Equal(475m, Contribution(session, FundingSource.Joint));
    }

    [Fact]
    public void Balance_updates_after_acknowledgement_project_only_remaining_steps()
    {
        WorkflowSession session = new();
        session.Import(Fixture("synthetic-reconciliation.csv"));
        session.Import(Fixture("synthetic-reconciliation-bill.csv"));
        session.CorrectTransaction("R-0005", "Personal", string.Empty);
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
        session.CorrectTransaction("R-0005", "Groceries", string.Empty);
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

        ReconciliationDraft history = Assert.Single(session.CompletedReconciliations);
        Assert.True(history.IsSettled);
        Assert.All(history.Transactions, transaction => Assert.True(transaction.Settled));
        Assert.True(history.Transfers.Single(transfer => transfer.Kind == TransferKind.CardPayment).Acknowledged);
        Assert.False(session.Draft!.IsSettled);
        Assert.Empty(session.Draft.Transactions);
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

            session.Import(Fixture("synthetic-refund.csv"));
            session.AcknowledgeCardPaid();
            session.Import(Fixture("synthetic-non-gbp.csv"));
            session.SetActualGbpAmount("R-0200", -275m);
            session.Import(Fixture("synthetic-reconciliation-changed.csv"));

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

            Assert.False(restored.Draft!.IsSettled);
            Assert.Equal(3, restored.Allocated.Count);
            Assert.Equal(-275m, restored.Allocated.Single(transaction => transaction.Id == "R-0200").ActualGbpAmount);
            Assert.Equal(0, restored.PendingTransactionCount);
            Assert.Single(restored.CompletedReconciliations);
            PendingTransactionCorrection correction = Assert.Single(restored.PendingCorrections);
            Assert.Equal("R-0001", correction.Original.Id);
            Assert.Equal(-320m, correction.Updated.Amount);
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
    public void Required_review_blocks_acknowledgement_until_the_transaction_is_resolved()
    {
        WorkflowSession session = new();
        session.Import(Fixture("synthetic-reconciliation.csv"));
        session.AcknowledgeDiscrepancy();

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(
            () => session.AcknowledgeTransfer("Joint", TransferKind.Funding));

        Assert.Contains("review", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(session.Draft!.IsLocked);

        session.CorrectTransaction("R-0005", "Groceries", string.Empty);
        session.AcknowledgeTransfer("Joint", TransferKind.Funding);
        Assert.True(session.Draft.IsLocked);
    }

    [Fact]
    public void Completion_promotes_pending_imports_and_keeps_history_immutable()
    {
        WorkflowSession session = CompletedSessionWithPendingRefund();

        ReconciliationDraft completed = Assert.Single(session.CompletedReconciliations);
        Assert.True(completed.IsSettled);
        Assert.All(completed.Transactions, transaction => Assert.True(transaction.Settled));
        Assert.Equal(2, session.Draft!.Transactions.Count);
        Assert.False(session.Draft.IsLocked);
        Assert.DoesNotContain(session.Draft.Transactions, transaction => transaction.Settled);
        Assert.Equal(0, session.PendingTransactionCount);
        Assert.DoesNotContain(completed.Transactions, transaction => transaction.Id.StartsWith("R-01", StringComparison.Ordinal));
    }

    [Fact]
    public void Latest_completion_can_reopen_only_before_newer_reconciliation_activity()
    {
        WorkflowSession session = new();
        CompleteBaseReconciliation(session);

        session.ReopenLatestCompleted();

        Assert.Empty(session.CompletedReconciliations);
        Assert.True(session.Draft!.IsLocked);
        Assert.False(session.Draft.IsSettled);
        Assert.All(session.Draft.Transactions, transaction => Assert.False(transaction.Settled));
        Assert.False(session.Draft.Transfers.Single(transfer => transfer.Kind == TransferKind.CardPayment).Acknowledged);
        Assert.All(session.Draft.Transfers.Where(transfer => transfer.Kind != TransferKind.CardPayment), transfer => Assert.True(transfer.Acknowledged));

        session.AcknowledgeCardPaid();
        session.Import(Fixture("synthetic-refund.csv"));
        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(session.ReopenLatestCompleted);
        Assert.Contains("newer reconciliation", exception.Message, StringComparison.OrdinalIgnoreCase);

        WorkflowSession changedTarget = new();
        CompleteBaseReconciliation(changedTarget);
        changedTarget.SetCardPaymentTarget(900m);
        Assert.False(changedTarget.CanReopenLatestCompleted);
        Assert.Throws<InvalidOperationException>(changedTarget.ReopenLatestCompleted);

        WorkflowSession changedBalance = new();
        CompleteBaseReconciliation(changedBalance);
        changedBalance.SetBalance("Joint", 900m);
        Assert.False(changedBalance.CanReopenLatestCompleted);
        Assert.Throws<InvalidOperationException>(changedBalance.ReopenLatestCompleted);

        WorkflowSession twoCompletions = CompletedSessionWithPendingRefund();
        CompleteCurrentDraft(twoCompletions);
        Assert.Equal(2, twoCompletions.CompletedReconciliations.Count);
        twoCompletions.ReopenLatestCompleted();
        Assert.False(twoCompletions.CanReopenLatestCompleted);
        Assert.Throws<InvalidOperationException>(twoCompletions.ReopenLatestCompleted);
    }

    [Fact]
    public void Reimport_updates_unlocked_transactions_and_queues_locked_or_settled_corrections()
    {
        WorkflowSession editable = new();
        editable.Import(Fixture("synthetic-reconciliation.csv"));

        ImportResult updated = editable.Import(Fixture("synthetic-reconciliation-changed.csv"));

        Assert.Equal(1, updated.Updated);
        Assert.Equal(-320m, editable.Imported.Single(transaction => transaction.Id == "R-0001").Amount);
        Assert.Empty(editable.PendingCorrections);

        WorkflowSession locked = new();
        locked.Import(Fixture("synthetic-reconciliation.csv"));
        locked.CorrectTransaction("R-0005", "Groceries", string.Empty);
        locked.AcknowledgeDiscrepancy();
        locked.AcknowledgeTransfer("Joint", TransferKind.Funding);

        ImportResult queued = locked.Import(Fixture("synthetic-reconciliation-changed.csv"));
        ImportResult replayed = locked.Import(Fixture("synthetic-reconciliation-changed.csv"));

        Assert.Equal(1, queued.CorrectionsQueued);
        Assert.Equal(1, replayed.Skipped);
        PendingTransactionCorrection correction = Assert.Single(locked.PendingCorrections);
        Assert.Equal(-300m, correction.Original.Amount);
        Assert.Equal(-320m, correction.Updated.Amount);
        Assert.Equal(-300m, locked.Draft!.Transactions.Single(transaction => transaction.Id == "R-0001").Amount);
        Assert.Throws<InvalidOperationException>(() => locked.AcceptPendingCorrection("R-0001"));
        foreach (Transfer transfer in locked.Draft.Transfers.Where(transfer =>
            transfer.Kind != TransferKind.CardPayment && !transfer.Acknowledged))
        {
            locked.AcknowledgeTransfer(transfer.Source, transfer.Kind);
        }

        locked.AcknowledgeCardPaid();
        Assert.Single(locked.CompletedReconciliations);
        Assert.Single(locked.PendingCorrections);

        WorkflowSession settled = new();
        CompleteBaseReconciliation(settled);
        ImportResult settledDuplicate = settled.Import(Fixture("synthetic-reconciliation.csv"));
        ImportResult settledCorrection = settled.Import(Fixture("synthetic-reconciliation-changed.csv"));
        Assert.Equal(6, settledDuplicate.Skipped);
        Assert.Equal(0, settledDuplicate.CorrectionsQueued);
        Assert.Equal(1, settledCorrection.CorrectionsQueued);
        Assert.Equal(-300m, Assert.Single(settled.CompletedReconciliations).Transactions.Single(transaction => transaction.Id == "R-0001").Amount);
        Assert.Throws<InvalidOperationException>(() => settled.AcknowledgeTransfer("Intermediate", TransferKind.CardPayment));

        settled.AcceptPendingCorrection("R-0001");

        Assert.Empty(settled.PendingCorrections);
        Assert.Equal(2, settled.Allocated.Count);
        Assert.Contains(settled.Allocated, transaction => transaction.Id == "adjustment:R-0001:1:reversal" && transaction.Amount == 300m);
        Assert.Contains(settled.Allocated, transaction => transaction.Id == "adjustment:R-0001:1:replacement" && transaction.Amount == -320m);
        Assert.Equal(20m, Contribution(settled, FundingSource.Joint));
        Assert.Equal(1180m, settled.Draft!.Discrepancy);
        Assert.Equal(-300m, Assert.Single(settled.CompletedReconciliations).Transactions.Single(transaction => transaction.Id == "R-0001").Amount);

        ImportResult acceptedReplay = settled.Import(Fixture("synthetic-reconciliation-changed.csv"));
        Assert.Equal(1, acceptedReplay.Skipped);
        Assert.Empty(settled.PendingCorrections);
        Assert.Equal(2, settled.Allocated.Count);

        WorkflowSession restored = new();
        restored.FromState(settled.ToState());
        ImportResult restoredReplay = restored.Import(Fixture("synthetic-reconciliation-changed.csv"));
        Assert.Equal(1, restoredReplay.Skipped);
        Assert.Empty(restored.PendingCorrections);
        Assert.Equal(20m, Contribution(restored, FundingSource.Joint));
    }

    [Fact]
    public void Settled_correction_reverses_the_funding_source_recorded_at_completion()
    {
        WorkflowSession session = new();
        session.Import(Fixture("synthetic-reconciliation.csv"));
        session.SetOverride("R-0001", FundingSource.Personal);
        session.SetOverride("R-0005", FundingSource.Joint);
        session.AcknowledgeDiscrepancy();
        foreach (Transfer transfer in session.Draft!.Transfers.Where(transfer => transfer.Kind != TransferKind.CardPayment))
        {
            session.AcknowledgeTransfer(transfer.Source, transfer.Kind);
        }

        session.AcknowledgeCardPaid();
        session.Import(Fixture("synthetic-reconciliation-changed.csv"));
        session.AcceptPendingCorrection("R-0001");

        Assert.Contains(session.Allocated, transaction => transaction.Type == "Correction reversal"
            && transaction.FundingSource == FundingSource.Personal
            && transaction.Amount == 300m);
        Assert.Contains(session.Allocated, transaction => transaction.Type == "Correction replacement"
            && transaction.FundingSource == FundingSource.Joint
            && transaction.Amount == -320m);
        Assert.Equal(320m, Contribution(session, FundingSource.Joint));
    }

    [Fact]
    public void Accepted_correction_baseline_survives_encrypted_round_trip()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"dot-finance-correction-{Guid.NewGuid():N}");
        string databasePath = Path.Combine(directory, "workflow.db");
        const string password = "fictional-test-password";
        try
        {
            WorkflowSession session = new();
            CompleteBaseReconciliation(session);
            session.Import(Fixture("synthetic-reconciliation-changed.csv"));
            session.AcceptPendingCorrection("R-0001");
            using (PrototypeVault vault = PrototypeVault.Create(databasePath, password))
            {
                vault.SaveScenario(session.ToState());
            }

            using PrototypeVault restoredVault = PrototypeVault.Unlock(databasePath, password);
            WorkflowSession restored = new();
            restored.FromState(restoredVault.LoadScenario());

            ImportResult replay = restored.Import(Fixture("synthetic-reconciliation-changed.csv"));
            Assert.Equal(1, replay.Skipped);
            Assert.Empty(restored.PendingCorrections);
            Assert.Equal(20m, Contribution(restored, FundingSource.Joint));
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
    public void Pre_baseline_state_reconstructs_an_accepted_correction_and_skips_its_replay()
    {
        WorkflowSession session = new();
        CompleteBaseReconciliation(session);
        session.Import(Fixture("synthetic-reconciliation-changed.csv"));
        session.AcceptPendingCorrection("R-0001");
        ScenarioState current = session.ToState();
        ScenarioState legacy = current with { AcceptedCorrectionBaselines = null };

        WorkflowSession restored = new();
        restored.FromState(legacy);
        ImportResult replay = restored.Import(Fixture("synthetic-reconciliation-changed.csv"));

        Assert.Equal(1, replay.Skipped);
        Assert.Empty(restored.PendingCorrections);
        Assert.Equal(20m, Contribution(restored, FundingSource.Joint));
    }

    [Fact]
    public void Locked_pre_baseline_state_reconstructs_the_accepted_replacement_source()
    {
        string secondCorrection = ChangedSingleRowFixture(2, "-330.00");
        try
        {
            WorkflowSession session = new();
            session.Import(Fixture("synthetic-reconciliation.csv"));
            session.SetOverride("R-0001", FundingSource.Personal);
            session.SetOverride("R-0005", FundingSource.Joint);
            CompleteCurrentDraft(session);
            session.Import(Fixture("synthetic-reconciliation-changed.csv"));
            session.AcceptPendingCorrection("R-0001");
            session.AcknowledgeDiscrepancy();
            Transfer first = session.Draft!.Transfers.First(transfer => transfer.Kind != TransferKind.CardPayment);
            session.AcknowledgeTransfer(first.Source, first.Kind);
            ScenarioState legacy = session.ToState() with { AcceptedCorrectionBaselines = null };

            WorkflowSession restored = new();
            restored.FromState(legacy);
            restored.Import(secondCorrection);
            foreach (Transfer transfer in restored.Draft!.Transfers.Where(transfer =>
                transfer.Kind != TransferKind.CardPayment && !transfer.Acknowledged))
            {
                restored.AcknowledgeTransfer(transfer.Source, transfer.Kind);
            }

            restored.AcknowledgeCardPaid();
            restored.ApplyCategoryRule("Groceries", FundingSource.Personal);
            restored.AcceptPendingCorrection("R-0001");

            Assert.Contains(restored.Allocated, transaction => transaction.Type == "Correction reversal"
                && transaction.FundingSource == FundingSource.Joint);
            Assert.Contains(restored.Allocated, transaction => transaction.Type == "Correction replacement"
                && transaction.FundingSource == FundingSource.Personal);
        }
        finally
        {
            File.Delete(secondCorrection);
        }
    }

    [Fact]
    public void Successive_settled_corrections_have_unique_adjustment_ids()
    {
        string secondCorrection = ChangedSingleRowFixture(2, "-330.00");
        try
        {
            WorkflowSession session = new();
            CompleteBaseReconciliation(session);
            session.Import(Fixture("synthetic-reconciliation-changed.csv"));
            session.AcceptPendingCorrection("R-0001");
            CompleteCurrentDraft(session);

            session.Import(secondCorrection);
            session.AcceptPendingCorrection("R-0001");

            string[] ids = [.. session.CompletedReconciliations
                .SelectMany(reconciliation => reconciliation.Transactions)
                .Concat(session.Allocated)
                .Select(transaction => transaction.Id)];
            Assert.Equal(ids.Length, ids.Distinct(StringComparer.Ordinal).Count());
            Assert.Contains("adjustment:R-0001:1:replacement", ids);
            Assert.Contains("adjustment:R-0001:2:replacement", ids);
        }
        finally
        {
            File.Delete(secondCorrection);
        }
    }

    [Fact]
    public void Changed_non_gbp_amount_requires_a_fresh_actual_gbp_value()
    {
        string firstChange = ChangedFixture("synthetic-non-gbp.csv", 2, "-350.00");
        string secondChange = ChangedFixture("synthetic-non-gbp.csv", 2, "-375.00");
        try
        {
            WorkflowSession session = new();
            session.Import(Fixture("synthetic-non-gbp.csv"));
            session.SetActualGbpAmount("R-0200", -275m);
            CompleteCurrentDraft(session);
            session.Import(firstChange);
            session.AcceptPendingCorrection("R-0200");
            session.SetActualGbpAmount("adjustment:R-0200:1:replacement", -320m);
            session.Import(secondChange);

            Transaction replacement = session.Allocated.Single(transaction => transaction.Type == "Correction replacement");
            Assert.Null(replacement.ActualGbpAmount);
            Assert.True(replacement.RequiresReview);
        }
        finally
        {
            File.Delete(firstChange);
            File.Delete(secondChange);
        }
    }

    [Fact]
    public void Every_changed_import_field_is_detected_for_a_locked_transaction()
    {
        (int Index, string Value)[] changes =
        [
            (1, "2026-09-03"),
            (2, "-301.00"),
            (3, "Other card"),
            (4, "Other bank"),
            (5, "EUR"),
            (6, "Dining"),
            (7, "Changed subcategory"),
            (8, "Bill"),
            (9, "changed-tag"),
            (10, "Changed counterparty"),
            (11, "Changed custom name"),
            (12, "Changed merchant"),
            (13, "Changed additional details"),
            (14, "Changed notes"),
            (15, "R-0999"),
        ];

        foreach ((int index, string value) in changes)
        {
            string path = ChangedSingleRowFixture(index, value);
            try
            {
                WorkflowSession session = LockedBaseSession();

                ImportResult result = session.Import(path);

                Assert.True(result.CorrectionsQueued == 1, $"CSV field index {index} was not detected as changed.");
                Assert.Single(session.PendingCorrections);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public void Non_gbp_transaction_requires_an_actual_gbp_amount_and_uses_it_for_the_plan()
    {
        WorkflowSession session = new();
        session.Import(Fixture("synthetic-non-gbp.csv"));

        Transaction before = Assert.Single(session.Allocated);
        Assert.True(before.RequiresReview);
        Assert.Null(before.ActualGbpAmount);
        session.AcknowledgeDiscrepancy();
        Assert.Throws<InvalidOperationException>(() => session.AcknowledgeTransfer("Primary", TransferKind.Funding));

        session.SetActualGbpAmount("R-0200", -275m);

        Transaction after = Assert.Single(session.Allocated);
        Assert.False(after.RequiresReview);
        Assert.Equal(-275m, after.ActualGbpAmount);
        Assert.Equal(275m, Contribution(session, FundingSource.Joint));
        Assert.Equal(925m, session.Draft!.Discrepancy);

        session.AcknowledgeDiscrepancy();
        session.AcknowledgeTransfer("Joint", TransferKind.Funding);
        Assert.Throws<InvalidOperationException>(() => session.SetActualGbpAmount("R-0200", -280m));

        WorkflowSession gbp = new();
        gbp.Import(Fixture("synthetic-reconciliation.csv"));
        Assert.Throws<InvalidOperationException>(() => gbp.SetActualGbpAmount("R-0001", -300m));
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

    private static WorkflowSession CompletedSessionWithPendingRefund()
    {
        WorkflowSession session = new();
        session.Import(Fixture("synthetic-reconciliation.csv"));
        session.SetOverride("R-0005", FundingSource.Joint);
        session.AcknowledgeDiscrepancy();
        session.AcknowledgeTransfer("Joint", TransferKind.Funding);
        session.Import(Fixture("synthetic-refund.csv"));
        foreach (Transfer transfer in session.Draft!.Transfers
            .Where(transfer => transfer.Kind != TransferKind.CardPayment && !transfer.Acknowledged))
        {
            session.AcknowledgeTransfer(transfer.Source, transfer.Kind);
        }

        session.AcknowledgeCardPaid();
        return session;
    }

    private static WorkflowSession LockedBaseSession()
    {
        WorkflowSession session = new();
        session.Import(Fixture("synthetic-reconciliation.csv"));
        session.SetOverride("R-0005", FundingSource.Joint);
        session.AcknowledgeDiscrepancy();
        session.AcknowledgeTransfer("Joint", TransferKind.Funding);
        return session;
    }

    private static string ChangedSingleRowFixture(int index, string value)
    {
        string[] fields =
        [
            "R-0001", "2026-09-02", "-300.00", "Fictional Amex", "Example Bank", "GBP", "Groceries", string.Empty,
            "Purchase", "weekly", "Example Market", string.Empty, "Example Market Ltd", "Synthetic purchase", string.Empty, string.Empty,
        ];
        fields[index] = value;
        string path = Path.Combine(Path.GetTempPath(), $"dot-finance-reimport-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path,
        [
            "ID,Date,Amount,Account,Bank,Currency,Category,Subcategory,Type,Tags,Counterparty,Custom Name,Merchant,Additional details,Notes,Linked transaction ID",
            string.Join(',', fields),
        ]);
        return path;
    }

    private static string ChangedFixture(string fixtureName, int index, string value)
    {
        string[] lines = File.ReadAllLines(Fixture(fixtureName));
        string[] fields = lines[1].Split(',');
        fields[index] = value;
        string path = Path.Combine(Path.GetTempPath(), $"dot-finance-reimport-{Guid.NewGuid():N}.csv");
        File.WriteAllLines(path, [lines[0], string.Join(',', fields)]);
        return path;
    }

    private static void CompleteBaseReconciliation(WorkflowSession session)
    {
        session.Import(Fixture("synthetic-reconciliation.csv"));
        session.SetOverride("R-0005", FundingSource.Joint);
        session.AcknowledgeDiscrepancy();
        foreach (Transfer transfer in session.Draft!.Transfers.Where(transfer => transfer.Kind != TransferKind.CardPayment))
        {
            session.AcknowledgeTransfer(transfer.Source, transfer.Kind);
        }

        session.AcknowledgeCardPaid();
    }

    private static void CompleteCurrentDraft(WorkflowSession session)
    {
        session.AcknowledgeDiscrepancy();
        foreach (Transfer transfer in session.Draft!.Transfers.Where(transfer =>
            transfer.Kind != TransferKind.CardPayment && !transfer.Acknowledged))
        {
            session.AcknowledgeTransfer(transfer.Source, transfer.Kind);
        }

        session.AcknowledgeCardPaid();
    }

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