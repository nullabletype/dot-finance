using Xunit;

using static DotFinance.Domain.Tests.Fixtures;

namespace DotFinance.Domain.Tests;

public sealed class ReconciliationTests
{
    [Fact]
    public void BaselinePreservesSearchableFieldsWithoutInventingReconciliationHistory()
    {
        ReconciliationBook book = Book();
        ImportedTransaction row = Tx("old", -100m, "Groceries");
        Assert.Throws<InvalidOperationException>(() => book.ImportSettledBaseline([row], false));
        book.ImportSettledBaseline([row], true);
        Assert.Equal(row, Assert.Single(book.Transactions));
        Assert.True(book.IsSettled("old"));
        Assert.Empty(book.History);
        Assert.Equal(1, book.Import([row]).Unchanged);
        book.Import([Tx("late", -20m)]);
        Start(book, 20m);
        Assert.Equal("late", Assert.Single(book.Current!.Transactions).Transaction.Id);
        Assert.Throws<InvalidOperationException>(() => book.ImportSettledBaseline([row], true));
    }

    [Fact]
    public void ConflictingDuplicatesBlockUntilExplicitSelectionAndReplayIsIdempotent()
    {
        ReconciliationBook book = Book();
        ImportedTransaction first = Tx("id", -10m), second = Tx("id", -20m);
        Assert.Equal(1, book.Import([first, second]).Conflicts);
        Start(book, 20m);
        Assert.Contains(book.Current!.Reviews, x => x.Reason == ReviewReason.Duplicate);
        Assert.Throws<InvalidOperationException>(book.AcknowledgeCardPaid);
        Assert.Throws<InvalidOperationException>(() => book.ResolveImportConflict("id", Tx("other", -1m)));
        book.ResolveImportConflict("id", second);
        Assert.Equal(20m, Assert.Single(book.Current!.Transactions).CostGbp);
        Assert.Empty(book.Current.Reviews);
        Assert.Equal(1, book.Import([second, second]).Unchanged);
        Assert.Single(book.Current.Transactions);
    }

    [Fact]
    public void EditableImportsUpdateAllRetainedFieldsAndInvalidateChangedForeignValuation()
    {
        ReconciliationBook book = Book();
        ImportedTransaction transaction = Tx("foreign", -10m, currency: "EUR");
        book.Import([transaction]); Start(book, 8m);
        Assert.Throws<InvalidOperationException>(book.AcknowledgeCardPaid);
        book.SetActualGbp("foreign", -8m);
        Assert.Empty(book.Current!.Reviews);
        Assert.Equal(1, book.Import([transaction]).Unchanged);
        book.Import([transaction with { Amount = -20m, Notes = "Corrected synthetic note", Tags = "revised", Merchant = "Other fictional shop" }]);
        Assert.Contains(book.Current.Reviews, x => x.Reason == ReviewReason.ActualGbpRequired);
        Assert.Equal("Corrected synthetic note", Assert.Single(book.Current.Transactions).Transaction.Notes);
        book.SetActualGbp("foreign", -16m);
        Assert.Equal(16m, book.Current.Plan.NetCardSpendingGbp);
        Assert.Throws<ArgumentException>(() => book.SetActualGbp("foreign", 16m));
    }

    [Fact]
    public void LiveTotalsIgnoreUnappliedRulePreviewAndPreserveOverrides()
    {
        ReconciliationBook book = Book();
        book.Import([Tx("first", -40m, "Groceries"), Tx("second", -60m, "Groceries")]); Start(book, 100m);
        book.Override("first", FundingSource.Personal);
        AllocationRules changed = new([new("Groceries", FundingSource.SharedSavings)], []);
        FundingPlan preview = book.PreviewRules(changed);
        Assert.Equal(60m, Contribution(preview, FundingSource.SharedSavings));
        Assert.Equal(60m, Contribution(book.Current!.Plan, FundingSource.Joint));
        book.ApplyRules(changed);
        Assert.Equal(40m, Contribution(book.Current.Plan, FundingSource.Personal));
        Assert.Equal(60m, Contribution(book.Current.Plan, FundingSource.SharedSavings));
        book.CorrectCategory("second", "", "");
        Assert.Contains(book.Current.Reviews, x => x.Reason == ReviewReason.Uncategorized);
        Assert.Equal(60m, book.Current.Plan.CategoryTotals.Single(x => x.Source is null).Gbp);
        Assert.Throws<InvalidOperationException>(() => book.AcknowledgeTransfer("shared"));
        book.Override("second", FundingSource.Joint);
        Assert.Empty(book.Current.Reviews);
    }

    [Fact]
    public void FirstTransferLocksAndReservesUntilCardPaidSettlesEverything()
    {
        ReconciliationBook book = Book();
        ImportedTransaction purchase = Tx("purchase", -100m, "Groceries"), bill = Tx("bill", -30m, "Groceries", "primary");
        book.Import([purchase, bill]); Start(book, 100m);
        ReconciliationSnapshot editable = book.Current!;
        book.AcknowledgeTransfer("joint");
        Assert.True(book.Current!.Locked);
        Assert.True(book.IsReserved("bill"));
        Assert.False(book.IsSettled("purchase"));
        Assert.Equal(130m, Assert.Single(book.Current.Transfers).SignedGbp);
        Assert.Throws<InvalidOperationException>(() => book.SetCardTarget(50m));
        Assert.Throws<InvalidOperationException>(() => book.Override("purchase", FundingSource.Personal));
        Assert.Throws<InvalidOperationException>(() => Start(book, 100m, "another"));
        book.Import([Tx("late", -20m), purchase with { Amount = -120m }]);
        Assert.Single(book.PendingCorrections);
        Assert.Equal(2, book.Current.Transactions.Length);
        Assert.Throws<InvalidOperationException>(() => book.AcceptCorrection("purchase"));
        book.AcknowledgeCardPaid();
        Assert.True(book.IsSettled("bill"));
        Assert.True(book.IsSettled("purchase"));
        Assert.False(book.IsSettled("late"));
        Assert.False(editable.Locked);
        Assert.False(Assert.Single(editable.Transfers).Acknowledged);
        ReconciliationSnapshot completed = Assert.Single(book.History);
        Start(book, 20m, "next");
        Assert.Equal("late", Assert.Single(book.Current!.Transactions).Transaction.Id);
        Assert.Equal(2, completed.Transactions.Length);
        Assert.Equal(100m, completed.Plan.CardTargetGbp);
        Assert.Contains(book.Current.Reviews, x => x.Reason == ReviewReason.Correction);
        book.RejectCorrection("purchase");
        Assert.Empty(book.Current.Reviews);
    }

    [Fact]
    public void CorrectionUsesDeltaAndReportsSignedCategoryEffectsWithoutReplaying()
    {
        ReconciliationBook book = new(Configuration(), new([new("Groceries", FundingSource.Joint)], [], [new("Groceries", null, FundingSource.Joint)]));
        ImportedTransaction original = Tx("purchase", -100m, "Groceries");
        book.ImportSettledBaseline([original], true);
        book.Import([original with { Amount = -120m }]); Start(book, 20m);
        book.AcceptCorrection("purchase");
        Assert.Equal(20m, Contribution(book.Current!.Plan, FundingSource.Joint));
        Assert.Equal(20m, Assert.Single(book.Current.Plan.CategoryTotals).Gbp);
        Assert.Equal(20m, Assert.Single(book.Current.Plan.ReportingTotals).SelectedGbp);
        Assert.Equal(0m, Assert.Single(book.Current.Plan.ReportingTotals).OtherGbp);
        Assert.Equal(1, book.Import([original with { Amount = -120m }]).Unchanged);
        Assert.Equal(2, book.Current.Adjustments.Length);
        book.Import([original with { Amount = -130m }]);
        book.AcceptCorrection("purchase");
        Assert.Equal(30m, Contribution(book.Current.Plan, FundingSource.Joint));
        Assert.Equal(4, book.Current.Adjustments.Select(x => x.Id).Distinct().Count());
        Assert.Throws<InvalidOperationException>(() => book.AcceptCorrection("purchase"));
    }

    [Fact]
    public void CorrectionRebalancesNetAfterRefundAndFutureRefundUsesCorrectedSource()
    {
        ReconciliationBook book = Book();
        ImportedTransaction purchase = Tx("purchase", -100m, "Groceries");
        book.Import([purchase, Tx("refund", 20m, linked: "purchase")]); Start(book, 80m); Complete(book);
        ReconciliationSnapshot originalHistory = Assert.Single(book.History);
        book.Import([purchase with { Category = "Personal" }]); Start(book, 0m, "next");
        book.AcceptCorrection("purchase");
        Assert.Equal(-80m, Contribution(book.Current!.Plan, FundingSource.Joint));
        Assert.Equal(80m, Contribution(book.Current.Plan, FundingSource.Personal));
        Assert.Equal(0m, book.Current.Plan.NetCardSpendingGbp);
        book.Import([Tx("future-refund", 10m, "Groceries", linked: "purchase")]);
        Assert.Equal(FundingSource.Personal, Assert.Single(book.Current.Transactions).Source);
        Assert.Equal(70m, Contribution(book.Current.Plan, FundingSource.Personal));
        Assert.All(originalHistory.Transactions, x => Assert.Equal(FundingSource.Joint, x.Source));
        Assert.Equal(80m, Contribution(originalHistory.Plan, FundingSource.Joint));
    }

    [Fact]
    public void BelowMinimumSourceRequiresExternalTopUpEvenWithoutOutgoingTransfer()
    {
        ReconciliationBook book = Book(); Start(book, 0m, balances: Balances(primary: 100m));
        Assert.Contains(book.Current!.Plan.Shortfalls, x => x.AccountId == "primary" && x.Gbp == 20m);
        Assert.Empty(book.Current.Transfers);
        Assert.Throws<InvalidOperationException>(book.AcknowledgeCardPaid);
        book.SetBalances(Balances(primary: 120m));
        book.AcknowledgeCardPaid();
        Assert.True(Assert.Single(book.History).CardPaid);
    }

    [Fact]
    public void ShortfallBlocksAffectedAcknowledgementAndTopUpKeepsAgreedAmounts()
    {
        ReconciliationBook book = Book();
        book.Import([Tx("joint", -100m, "Groceries"), Tx("shared", -100m, "Travel")]);
        Start(book, 200m, balances: Balances(joint: 20m));
        Assert.Throws<InvalidOperationException>(() => book.AcknowledgeTransfer("joint"));
        book.AcknowledgeTransfer("shared");
        FundingTransfer[] agreed = [.. book.Current!.Transfers];
        Assert.Throws<InvalidOperationException>(book.AcknowledgeCardPaid);
        book.SetBalances(Balances(intermediate: 280m, joint: 100m, shared: 900m));
        Assert.Equal(agreed, book.Current.Transfers.ToArray());
        Assert.Equal(900m, Balance(book.Current.Plan, "shared"));
        Assert.Equal(180m, Balance(book.Current.Plan, "intermediate"));
        book.AcknowledgeTransfer("joint"); book.AcknowledgeCardPaid();
        Assert.True(Assert.Single(book.History).CardPaid);
    }

    [Fact]
    public void UndoKeepsLockAndCurrentBalancesWithoutInventingReverseMovement()
    {
        ReconciliationBook book = Book(); book.Import([Tx("j", -100m, "Groceries")]); Start(book, 100m);
        book.AcknowledgeTransfer("joint");
        AccountBalance[] balances = [.. book.Current!.CurrentBalances];
        book.UndoTransferAcknowledgement("joint");
        Assert.True(book.Current.Locked);
        Assert.False(Assert.Single(book.Current.Transfers).Acknowledged);
        Assert.Equal(balances, book.Current.CurrentBalances.ToArray());
        Assert.Equal(100m, Assert.Single(book.Current.Transfers).SignedGbp);
        Assert.Throws<InvalidOperationException>(book.AcknowledgeCardPaid);
        Assert.Throws<InvalidOperationException>(() => book.UndoTransferAcknowledgement("joint"));
    }

    [Fact]
    public void DiscrepancyRequiresAcknowledgementAndChangingTargetResetsIt()
    {
        ReconciliationBook book = Book(); book.Import([Tx("j", -100m, "Groceries")]); Start(book, 120m);
        Assert.Equal(20m, book.Current!.Plan.DiscrepancyGbp);
        Assert.Throws<InvalidOperationException>(() => book.AcknowledgeTransfer("joint"));
        book.AcknowledgeDiscrepancy(); book.SetCardTarget(110m);
        Assert.False(book.Current.DiscrepancyAcknowledged);
        book.AcknowledgeDiscrepancy(); book.AcknowledgeTransfer("joint");
        Assert.True(book.Current.Locked);
    }

    [Fact]
    public void OutgoingRefundMustBeAcknowledgedBeforeCardPaid()
    {
        ReconciliationBook book = Book(); book.Import([Tx("refund", 20m, "Groceries")]); Start(book);
        book.AcknowledgeDiscrepancy();
        Assert.Equal(-20m, Assert.Single(book.Current!.Transfers).SignedGbp);
        Assert.Throws<InvalidOperationException>(book.AcknowledgeCardPaid);
        book.AcknowledgeTransfer("joint"); book.AcknowledgeCardPaid();
        Assert.True(book.IsSettled("refund"));
    }

    [Fact]
    public void LatestCompletionCanReopenUntilNewDraftStartsAndDoesNotReversePayment()
    {
        ReconciliationBook book = Book(); book.Import([Tx("personal", -50m)]); Start(book, 50m); Complete(book);
        ReconciliationSnapshot history = Assert.Single(book.History);
        Assert.True(book.CanReopenLatest);
        book.ReopenLatest();
        Assert.True(book.Current!.Locked);
        Assert.False(book.Current.CardPaid);
        Assert.False(book.IsSettled("personal"));
        Assert.Equal(history.CurrentBalances.ToArray(), book.Current.CurrentBalances.ToArray());
        book.SetBalances(Balances()); book.AcknowledgeCardPaid();
        Assert.True(history.CardPaid);
        Start(book, 0m, "newer");
        Assert.False(book.CanReopenLatest);
        Assert.Throws<InvalidOperationException>(book.ReopenLatest);
        Assert.Equal(50m, book.History[0].Plan.CardTargetGbp);
    }

    [Fact]
    public void UnknownPaymentAndUncategorisedReviewsCannotBeBypassedByCompletion()
    {
        ReconciliationBook book = Book(); book.Import([Tx("uncat", -10m, ""), Tx("unknown", -10m, account: "unconfigured")]); Start(book, 10m);
        Assert.Equal(2, book.Current!.Reviews.Length);
        Assert.Throws<InvalidOperationException>(book.AcknowledgeCardPaid);
        book.CorrectCategory("uncat", "Personal", "");
        book.Import([Tx("unknown", -10m, account: "primary")]);
        Assert.Empty(book.Current.Reviews);
        book.AcknowledgeCardPaid();
        Assert.True(book.IsSettled("uncat"));
    }
    [Fact]
    public void InvalidImportsAreAtomicAndAllSourceFieldsParticipateInCorrectionDetection()
    {
        ReconciliationBook book = Book();
        Assert.Throws<ArgumentException>(() => book.Import([Tx("valid", -10m), Tx("bad", -10m) with { Id = "" }]));
        Assert.Empty(book.Transactions);
        ImportedTransaction original = Tx("original", -10m, "Groceries");
        book.ImportSettledBaseline([original], true);
        ImportedTransaction[] changes = [original with { Date = new(2026, 1, 2) }, original with { Bank = "Other fictional bank" },
            original with { Type = "Corrected type" }, original with { Tags = "changed" }, original with { Counterparty = "Changed" },
            original with { CustomName = "Changed" }, original with { Merchant = "Changed" }, original with { AdditionalDetails = "Changed" },
            original with { Notes = "Changed" }, original with { Account = "Renamed exported account" }, original with { AccountId = "primary" }, original with { Subcategory = "Changed" }];
        foreach (ImportedTransaction changed in changes)
        {
            Assert.Equal(1, book.Import([changed]).Corrections);
            Assert.Equal(changed, Assert.Single(book.PendingCorrections).Proposed);
            Assert.Equal(original, Assert.Single(book.Transactions));
            book.Import([original]);
            Assert.Empty(book.PendingCorrections);
        }
    }

    [Fact]
    public void ForeignSettledCorrectionRequiresFreshValuationBeforeDeltaAcceptance()
    {
        ReconciliationBook book = Book();
        ImportedTransaction original = Tx("foreign", -100m, "Groceries", currency: "EUR", actual: -80m);
        book.ImportSettledBaseline([original], true);
        book.Import([original with { Amount = -120m, ActualGbpAmount = null }]); Start(book, 16m);
        Assert.Throws<InvalidOperationException>(() => book.AcceptCorrection("foreign"));
        Assert.Empty(book.Current!.Adjustments);
        book.AcceptCorrection("foreign", actualGbp: -96m);
        Assert.Equal(16m, Contribution(book.Current.Plan, FundingSource.Joint));
        Assert.Equal(16m, book.Current.Plan.NetCardSpendingGbp);
    }

    [Fact]
    public void LockedDuplicateResolutionQueuesCorrectionWithoutChangingAgreedAmounts()
    {
        ReconciliationBook book = Book(); ImportedTransaction original = Tx("j", -100m, "Groceries");
        book.Import([original]); Start(book, 100m); book.AcknowledgeTransfer("joint");
        var before = book.Current;
        var changed = original with { Amount = -120m };
        book.Import([original, changed]); book.ResolveImportConflict("j", changed);
        Assert.Single(book.PendingCorrections);
        Assert.Equal(before, book.Current);
        book.AcknowledgeCardPaid();
        Start(book, 20m, "next"); book.AcceptCorrection("j");
        Assert.Equal(20m, Contribution(book.Current!.Plan, FundingSource.Joint));
        Assert.Equal(100m, Contribution(book.History[0].Plan, FundingSource.Joint));
    }

    [Fact]
    public void AcceptedCorrectionsPreserveBaselineAndEveryBeforeAfterVersion()
    {
        ReconciliationBook book = Book();
        var original = Tx("baseline", -100m, "Groceries") with { Notes = "Original note", Merchant = "Original fictional merchant" };
        book.ImportSettledBaseline([original], true);
        var corrected = original with { Amount = -120m, Notes = "First correction", Merchant = "Revised fictional merchant", Date = new(2026, 1, 2) };
        book.Import([corrected]); Start(book, 20m); book.AcceptCorrection("baseline");
        AcceptedCorrection first = Assert.Single(book.AcceptedCorrections);
        Assert.Equal("r1", first.ReconciliationId);
        Assert.Equal("Original note", Assert.Single(first.Before).Transaction.Notes);
        Assert.Equal("Original fictional merchant", Assert.Single(book.SettledBaseline).Transaction.Merchant);
        Assert.Equal(new DateOnly(2026, 1, 2), Assert.Single(first.After).Transaction.Date);
        book.Import([corrected with { Notes = "Second correction" }]); book.AcceptCorrection("baseline");
        Assert.Equal(2, book.AcceptedCorrections.Length);
        Assert.Equal("First correction", Assert.Single(book.AcceptedCorrections[1].Before).Transaction.Notes);
        Assert.Equal("Second correction", Assert.Single(book.AcceptedCorrections[1].After).Transaction.Notes);
        Assert.Equal("First correction", Assert.Single(first.After).Transaction.Notes);
        Assert.Equal("Original note", Assert.Single(book.SettledBaseline).Transaction.Notes);
    }

    [Fact]
    public void OutgoingRefundWaitsForIncomingFundsWithoutChangingBalancesOnRejection()
    {
        ReconciliationBook book = Book(); book.Import([Tx("refund", 150m, "Groceries")]); Start(book);
        book.AcknowledgeDiscrepancy();
        var balances = book.Current!.CurrentBalances;
        Assert.Equal(50m, book.Current.Transfers.Single(x => x.AccountId == "primary").SignedGbp);
        Assert.Throws<InvalidOperationException>(() => book.AcknowledgeTransfer("joint"));
        Assert.Equal(balances, book.Current.CurrentBalances);
        Assert.False(book.Current.Locked);
        book.AcknowledgeTransfer("primary"); book.AcknowledgeTransfer("joint"); book.AcknowledgeCardPaid();
        Assert.Equal(80m, book.History[0].CurrentBalances.Single(x => x.AccountId == "intermediate").Gbp);
    }

}