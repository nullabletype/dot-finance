using System.Collections.Immutable;
using System.Text.Json;

using DotFinance.Domain;

namespace DotFinance.Storage.Tests;

internal static class StorageFixtures
{
    internal const string Password = "synthetic-storage-password";

    internal static ReconciliationBook RepresentativeBook()
    {
        ReconciliationBook book = new(Configuration(), Rules());
        ImportedTransaction baselineOne = Transaction("baseline-1", -100m, "Groceries");
        ImportedTransaction baselineTwo = Transaction("baseline-2", -30m, "Travel");
        book.ImportSettledBaseline([baselineOne, baselineTwo], confirmedAlreadySettled: true);

        book.Import([Transaction("completed-1", -10m, "Groceries")]);
        book.Start("reconciliation-1", new DateOnly(2026, 9, 1), 10m, Balances());
        foreach (FundingTransfer transfer in book.Current!.Transfers) book.AcknowledgeTransfer(transfer.AccountId);
        book.AcknowledgeCardPaid();

        book.Import([baselineOne with { Category = "Personal" }]);
        book.Import([baselineTwo with { Notes = "Synthetic corrected note" }]);
        book.Import([
            Transaction("duplicate-1", -4m, "Personal") with { Merchant = "Fictional duplicate A" },
            Transaction("duplicate-1", -5m, "Personal") with { Merchant = "Fictional duplicate B" }
        ]);
        book.Import([Transaction("active-1", -25m, "Personal")]);
        book.Start("reconciliation-2", new DateOnly(2026, 9, 27), 25m, Balances());
        book.Override("active-1", FundingSource.SharedSavings);
        book.AcceptCorrection("baseline-1", FundingSource.Personal);
        return book;
    }

    internal static FundingConfiguration Configuration() => new("card",
    [
        new("intermediate", "Intermediate", AccountRole.Intermediate, 80m),
        new("joint", "Joint", AccountRole.Joint, 0m),
        new("shared", "Shared", AccountRole.SharedSavings, 0m),
        new("primary", "Primary", AccountRole.PrimaryPersonal, 120m),
        new("savings", "Savings", AccountRole.PersonalSavings, 0m, 1)
    ]);

    internal static AllocationRules Rules() => new(
        [new("Groceries", FundingSource.Joint), new("Travel", FundingSource.SharedSavings)],
        [], [new("Travel", "Holiday", FundingSource.SharedSavings)]);

    internal static ImmutableArray<AccountBalance> Balances() =>
        [new("intermediate", 500m), new("joint", 1000m), new("shared", 1000m), new("primary", 1000m), new("savings", 1000m)];

    internal static ImportedTransaction Transaction(string id, decimal amount, string category) => new(
        id, new DateOnly(2026, 8, 1), amount, "Fictional card", "card", "Synthetic bank", "GBP",
        category, "", amount > 0 ? "Refund" : "Purchase", "fictional", "Fictional counterparty", "",
        $"Synthetic merchant {id}", "Synthetic details", "Synthetic notes", "",
        amount > 0 ? TransactionKind.Refund : TransactionKind.Purchase);

    internal static string CanonicalState(ReconciliationBook book) => JsonSerializer.Serialize(book.ExportState());
}