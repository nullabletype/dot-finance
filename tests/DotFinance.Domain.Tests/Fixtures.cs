using System.Collections.Immutable;

using DotFinance.Domain;

namespace DotFinance.Domain.Tests;

internal static class Fixtures
{
    internal static FundingConfiguration Configuration() => new("card",
    [
        new("intermediate", "Intermediate", AccountRole.Intermediate, 80m),
        new("joint", "Joint", AccountRole.Joint, 0m),
        new("shared", "Shared", AccountRole.SharedSavings, 0m),
        new("primary", "Primary", AccountRole.PrimaryPersonal, 120m),
        new("savings", "Savings", AccountRole.PersonalSavings, 0m, 1),
    ]);
    internal static ImmutableArray<AccountBalance> Balances(decimal intermediate = 180m, decimal joint = 1000m,
        decimal shared = 1000m, decimal primary = 320m, decimal savings = 1000m) =>
        [new("intermediate", intermediate), new("joint", joint), new("shared", shared), new("primary", primary), new("savings", savings)];
    internal static AllocationRules Rules() => new([new("Groceries", FundingSource.Joint), new("Travel", FundingSource.SharedSavings)], [], [new("Travel", "Holiday", FundingSource.SharedSavings)]);
    internal static ImportedTransaction Tx(string id, decimal activity, string category = "Personal", string account = "card",
        string linked = "", string currency = "GBP", decimal? actual = null) => new(id, new(2026, 1, 1), activity,
        "Fictional exported account", account, "Synthetic bank", currency, category, "", activity > 0 ? "Refund" : "Purchase", "fictional", "Fictional shop",
        "", "Fictional merchant", "Synthetic details", "Synthetic notes", linked,
        activity > 0 ? TransactionKind.Refund : TransactionKind.Purchase, actual);
    internal static ReconciliationBook Book() => new(Configuration(), Rules());
    internal static void Start(ReconciliationBook book, decimal target = 0m, string id = "r1", ImmutableArray<AccountBalance>? balances = null)
        => book.Start(id, new(2026, 2, 1), target, balances ?? Balances());
    internal static decimal Balance(FundingPlan plan, string id) => plan.ProjectedBalances.Single(x => x.AccountId == id).Gbp;
    internal static decimal Contribution(FundingPlan plan, FundingSource source) => plan.Contributions.Single(x => x.Source == source).TotalGbp;
    internal static void Complete(ReconciliationBook book)
    {
        if (!book.Current!.DiscrepancyAcknowledged) book.AcknowledgeDiscrepancy();
        foreach (FundingTransfer transfer in book.Current!.Transfers.Where(x => !x.Acknowledged).OrderByDescending(x => x.SignedGbp).ToArray())
            book.AcknowledgeTransfer(transfer.AccountId);
        book.AcknowledgeCardPaid();
    }
}