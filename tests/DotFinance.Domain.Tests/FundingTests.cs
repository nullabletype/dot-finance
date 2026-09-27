using System.Collections.Immutable;

using Xunit;

using static DotFinance.Domain.Tests.Fixtures;

namespace DotFinance.Domain.Tests;

public sealed class FundingTests
{
    [Fact]
    public void WorkedExampleUsesSignedResponsibilitiesAndPersonalPriority()
    {
        ImportedTransaction[] rows = [Tx("joint", -450m, "Groceries"), Tx("shared", -100m, "Travel"),
            Tx("holiday", -150m, "Travel") with { Subcategory = "Holiday" }, Tx("personal", -500m)];
        FundingPlan plan = FundingCalculator.Plan(Configuration(), 1200m, Balances(), FundingCalculator.Allocate(Configuration(), Rules(), rows), reporting: Rules().Reporting);
        Assert.Equal(450m, Contribution(plan, FundingSource.Joint));
        Assert.Equal(250m, Contribution(plan, FundingSource.SharedSavings));
        Assert.Equal(400m, plan.AdditionalPersonalGbp);
        Assert.Equal(200m, plan.Transfers.Single(x => x.AccountId == "primary").SignedGbp);
        Assert.Equal(200m, plan.Transfers.Single(x => x.AccountId == "savings").SignedGbp);
        Assert.Equal(80m, Balance(plan, "intermediate"));
        Assert.Equal(120m, Balance(plan, "primary"));
        Assert.Equal(150m, Assert.Single(plan.ReportingTotals).SelectedGbp);
        Assert.Equal(100m, Assert.Single(plan.ReportingTotals).OtherGbp);
        Assert.Empty(plan.Shortfalls);
    }

    [Fact]
    public void PersonalPaidBillReimbursesWithoutDoubleDeductionOrCardTargetChange()
    {
        ImportedTransaction[] rows = [Tx("j", -450m, "Groceries"), Tx("s", -250m, "Travel"), Tx("p", -500m), Tx("bill", -60m, "Groceries", "primary")];
        FundingPlan plan = FundingCalculator.Plan(Configuration(), 1200m, Balances(), FundingCalculator.Allocate(Configuration(), Rules(), rows));
        Assert.Equal(510m, Contribution(plan, FundingSource.Joint));
        Assert.Equal(60m, plan.Contributions.Single(x => x.Source == FundingSource.Joint).ReimbursementsGbp);
        Assert.Equal(340m, plan.AdditionalPersonalGbp);
        Assert.Equal(1200m, plan.NetCardSpendingGbp);
        Assert.Equal(120m, Balance(plan, "primary"));
        Assert.Equal(140m, plan.Transfers.Single(x => x.AccountId == "savings").SignedGbp);
    }

    [Fact]
    public void RefundsProduceOutgoingContributionAndSurplusStaysIntermediate()
    {
        ImmutableArray<AllocatedTransaction> rows = FundingCalculator.Allocate(Configuration(), Rules(),
            [Tx("p", -100m, "Groceries"), Tx("refund", 150m, linked: "p")]);
        FundingPlan plan = FundingCalculator.Plan(Configuration(), 100m, Balances(), rows);
        Assert.Equal(-50m, Contribution(plan, FundingSource.Joint));
        Assert.Equal(-50m, plan.Transfers.Single(x => x.AccountId == "joint").SignedGbp);
        Assert.Equal(50m, plan.AdditionalPersonalGbp);
        Assert.Equal(80m, Balance(plan, "intermediate"));
        rows = FundingCalculator.Allocate(Configuration(), Rules(), [Tx("bill", -300m, "Groceries", "primary")]);
        plan = FundingCalculator.Plan(Configuration(), 100m, Balances(), rows);
        Assert.Equal(0m, plan.AdditionalPersonalGbp);
        Assert.Equal(380m, Balance(plan, "intermediate"));
        Assert.Equal(320m, Balance(plan, "primary"));
    }

    [Fact]
    public void ShortfallsNeverSubsidiseJointAndProjectionsUseOnlyActualFunding()
    {
        FundingPlan plan = FundingCalculator.Plan(Configuration(), 600m, Balances(joint: 10m, primary: 120m, savings: 0m),
            FundingCalculator.Allocate(Configuration(), Rules(), [Tx("j", -300m, "Groceries"), Tx("p", -300m)]));
        Assert.Equal(300m, plan.Transfers.Single().SignedGbp);
        Assert.Equal(200m, plan.AdditionalPersonalGbp);
        Assert.Contains(plan.Shortfalls, x => x.AccountId == "joint" && x.Gbp == 290m);
        Assert.Contains(plan.Shortfalls, x => x.AccountId == "intermediate" && x.Gbp == 200m);
        Assert.Equal(-120m, Balance(plan, "intermediate"));
    }

    [Theory]
    [InlineData(119.99, 0)]
    [InlineData(120, 0)]
    [InlineData(120.01, 0.01)]
    public void MinimumBoundaryDoesNotSpendProtectedMoney(decimal primary, decimal expected)
    {
        FundingPlan plan = FundingCalculator.Plan(Configuration(), 200m, Balances(primary: primary), []);
        Assert.Equal(expected, plan.Transfers.Where(x => x.AccountId == "primary").Sum(x => x.SignedGbp));
    }

    [Fact]
    public void IntermediateDeficitIsRestoredAndSavingsFollowConfiguredPriority()
    {
        FundingConfiguration configuration = new("card", Configuration().Accounts.Where(x => x.Id != "savings").Concat(
            [new("z-first", "First", AccountRole.PersonalSavings, 10m, 1), new("a-last", "Last", AccountRole.PersonalSavings, 0m, 2)]));
        AccountBalance[] balances = [.. Balances(intermediate: 50m, primary: 120m).Where(x => x.AccountId != "savings"), new("z-first", 50m), new("a-last", 100m)];
        FundingPlan plan = FundingCalculator.Plan(configuration, 100m, balances, []);
        Assert.Equal(30m, plan.IntermediateRestorationGbp);
        Assert.Equal(130m, plan.AdditionalPersonalGbp);
        Assert.Equal(new[] { new FundingTransfer("z-first", 40m), new FundingTransfer("a-last", 90m) }, plan.Transfers);
        Assert.Equal(80m, Balance(plan, "intermediate"));
    }

    [Fact]
    public void ProjectionIgnoresAcknowledgedStepsFromCurrentBalances()
    {
        ImmutableArray<AccountBalance> result = FundingCalculator.Project(Configuration(), Balances(intermediate: 280m, joint: 900m),
            [new("joint", 100m, true), new("primary", 50m)], 100m);
        Assert.Equal(230m, result.Single(x => x.AccountId == "intermediate").Gbp);
        Assert.Equal(900m, result.Single(x => x.AccountId == "joint").Gbp);
        Assert.Equal(270m, result.Single(x => x.AccountId == "primary").Gbp);
    }

    [Fact]
    public void AllocationPrecedenceHistoryAndReviewsAreExplicit()
    {
        AllocationRules rules = new([new("Groceries", FundingSource.Joint)], [new("Groceries", "Special", FundingSource.SharedSavings)]);
        ImmutableArray<AllocatedTransaction> rows = FundingCalculator.Allocate(Configuration(), rules,
            [Tx("category", -10m, "Groceries"), Tx("sub", -10m, "Groceries") with { Subcategory = "Special" },
                Tx("override", -10m, "Groceries") with { Subcategory = "Special" }, Tx("unmapped", -10m),
                Tx("missing", -10m, ""), Tx("foreign", -10m, currency: "EUR"), Tx("unknown", -10m, account: "unknown"),
                Tx("refund", 10m, "Travel", linked: "historical"), Tx("excluded", -10m, "") with { Kind = TransactionKind.Transfer }],
            new Dictionary<string, FundingSource> { ["override"] = FundingSource.Personal },
            new Dictionary<string, FundingSource> { ["historical"] = FundingSource.Joint });
        Assert.Equal(FundingSource.Joint, Row("category").Source);
        Assert.Equal(FundingSource.SharedSavings, Row("sub").Source);
        Assert.Equal(FundingSource.Personal, Row("override").Source);
        Assert.Equal(FundingSource.Personal, Row("unmapped").Source);
        Assert.Equal(FundingSource.Joint, Row("refund").Source);
        Assert.Contains(Row("missing").Reviews, x => x.Reason == ReviewReason.Uncategorized);
        Assert.Contains(Row("foreign").Reviews, x => x.Reason == ReviewReason.ActualGbpRequired);
        Assert.Contains(Row("unknown").Reviews, x => x.Reason == ReviewReason.UnknownPaymentAccount);
        Assert.False(Row("excluded").Eligible);
        Assert.Empty(Row("excluded").Reviews);
        AllocatedTransaction Row(string id) => rows.Single(x => x.Transaction.Id == id);
    }

    [Fact]
    public void RefundOverrideWinsAndUnlinkedRefundFallsBackToItsCategory()
    {
        ImmutableArray<AllocatedTransaction> rows = FundingCalculator.Allocate(Configuration(), Rules(),
            [Tx("p", -20m, "Groceries"), Tx("r", 5m, "Travel", linked: "p"), Tx("fallback", 5m, "Travel", linked: "absent")],
            new Dictionary<string, FundingSource> { ["r"] = FundingSource.Personal });
        Assert.Equal(FundingSource.Personal, rows.Single(x => x.Transaction.Id == "r").Source);
        Assert.Equal(FundingSource.SharedSavings, rows.Single(x => x.Transaction.Id == "fallback").Source);
    }

    [Fact]
    public void ConfigurationAndCurrencyRejectAmbiguousInputs()
    {
        Assert.Throws<ArgumentException>(() => new FundingConfiguration("card", Configuration().Accounts.Add(Configuration().Accounts[0])));
        Assert.Throws<ArgumentException>(() => new FundingConfiguration("card", Configuration().Accounts.Where(x => x.Role != AccountRole.PersonalSavings)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FundingConfiguration("card", Configuration().Accounts.Select(x => x with { ProtectedMinimum = -1m })));
        Assert.Throws<ArgumentException>(() => new AllocationRules([new("food", FundingSource.Joint), new("FOOD", FundingSource.Personal)], []));
        Assert.Throws<ArgumentOutOfRangeException>(() => FundingCalculator.Allocate(Configuration(), Rules(), [Tx("p", -1.001m)]));
        Assert.Throws<ArgumentException>(() => FundingCalculator.Allocate(Configuration(), Rules(), [Tx("p", -1m, currency: "EUR", actual: 1m)]));
        Assert.Throws<ArgumentException>(() => FundingCalculator.Plan(Configuration(), 0m, Balances().RemoveAt(0), []));
    }

    [Fact]
    public void InputOrderingAndMutableCallerCollectionsCannotChangeResults()
    {
        List<FundingAccount> accounts = [.. Configuration().Accounts];
        FundingConfiguration configuration = new("card", accounts);
        accounts.Clear();
        ImportedTransaction[] input = [Tx("z", -40m), Tx("a", -60m, "Groceries")];
        ImmutableArray<AllocatedTransaction> first = FundingCalculator.Allocate(configuration, Rules(), input);
        ImmutableArray<AllocatedTransaction> second = FundingCalculator.Allocate(configuration, Rules(), input.Reverse());
        Assert.Equal(["a", "z"], first.Select(x => x.Transaction.Id));
        Assert.Equal(first.ToArray(), second.ToArray());
        Assert.Equal(5, configuration.Accounts.Length);
    }
    [Fact]
    public void PlanningRejectsDuplicateAllocationsAndInvalidAdjustmentMoney()
    {
        AllocatedTransaction row = Assert.Single(FundingCalculator.Allocate(Configuration(), Rules(), [Tx("p", -10m)]));
        Assert.Throws<ArgumentException>(() => FundingCalculator.Plan(Configuration(), 10m, Balances(), [row, row]));
        Assert.Throws<ArgumentException>(() => FundingCalculator.Plan(Configuration(), 10m, Balances(), [row with { CostGbp = 20m }]));
        Assert.Throws<ArgumentOutOfRangeException>(() => FundingCalculator.Plan(Configuration(), 10m, Balances(), [],
            [new("a", "p", (FundingSource)99, 10m, 10m, "Personal", "")]));
        Assert.Throws<ArgumentOutOfRangeException>(() => FundingCalculator.Plan(Configuration(), 10m, Balances(), [],
            [new("a", "p", FundingSource.Personal, 10.001m, 10m, "Personal", "")]));
    }

    [Fact]
    public void OnlyCardAndQualifyingPersonalPaidBillsEnterAllocations()
    {
        var rows = FundingCalculator.Allocate(Configuration(), Rules(),
            [Tx("own-bill", -40m, account: "primary"), Tx("joint-paid", -50m, "Groceries", "joint"),
                Tx("shared-paid", -60m, "Travel", "shared"), Tx("repayment", -70m) with { Kind = TransactionKind.CardRepayment },
                Tx("reimburse", -30m, "Groceries", "savings")]);
        Assert.Equal("reimburse", Assert.Single(rows.Where(x => x.Eligible)).Transaction.Id);
        var plan = FundingCalculator.Plan(Configuration(), 0m, Balances(), rows);
        Assert.Equal(30m, Contribution(plan, FundingSource.Joint));
        Assert.Equal(0m, plan.NetCardSpendingGbp);
        Assert.Equal(30m, Assert.Single(plan.CategoryTotals).Gbp);
    }

    [Fact]
    public void ProjectionConservesMoneyAcrossSignedContributionsAndTargets()
    {
        foreach (decimal joint in new[] { -100m, 0m, 100m })
            foreach (decimal shared in new[] { -100m, 0m, 100m })
                foreach (decimal target in new[] { 0m, 100m, 500m })
                {
                    var balances = Balances();
                    var rows = FundingCalculator.Allocate(Configuration(), Rules(),
                        [Tx("joint", -joint, "Groceries"), Tx("shared", -shared, "Travel")]);
                    var plan = FundingCalculator.Plan(Configuration(), target, balances, rows);
                    Assert.Equal(balances.Sum(x => x.Gbp) - target, plan.ProjectedBalances.Sum(x => x.Gbp));
                    Assert.All(plan.ProjectedBalances, x => Assert.True(x.Gbp >= Configuration().Accounts.Single(a => a.Id == x.AccountId).ProtectedMinimum));
                    Assert.Equal(joint, Contribution(plan, FundingSource.Joint));
                    Assert.Equal(shared, Contribution(plan, FundingSource.SharedSavings));
                }
    }

    [Fact]
    public void IncomingRefundDoesNotReplaceRequiredExternalTopUp()
    {
        var configuration = new FundingConfiguration("card", Configuration().Accounts.Select(x => x.Id == "joint" ? x with { ProtectedMinimum = 100m } : x));
        var rows = FundingCalculator.Allocate(configuration, Rules(), [Tx("refund", 20m, "Groceries")]);
        var plan = FundingCalculator.Plan(configuration, 0m, Balances(joint: 90m), rows);
        Assert.Equal(110m, Balance(plan, "joint"));
        Assert.Contains(plan.Shortfalls, x => x.AccountId == "joint" && x.Gbp == 10m);
    }

    [Fact]
    public void CompletedCardPaymentIsNotProjectedAgain()
    {
        var result = FundingCalculator.Project(Configuration(), Balances(), [], 100m, cardPaid: true);
        Assert.Equal(180m, result.Single(x => x.AccountId == "intermediate").Gbp);
    }

}