using DotFinance.Domain;
using DotFinance.Import;

using Xunit;

namespace DotFinance.Import.Tests;

public sealed class ImportDomainCompatibilityTests
{
    [Fact]
    public void ParsedDuplicateIds_RemainSeparateForDomainConflictResolution()
    {
        object?[] first = TestWorkbookFactory.Row(id: "same-id", amount: -10m);
        object?[] second = TestWorkbookFactory.Row(id: "same-id", amount: -20m);
        EmmaImportResult import = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(rows: [first, second]));
        ReconciliationBook book = Book();

        ImportOutcome outcome = book.Import(import.Records.Select(Map));

        Assert.True(import.IsValid);
        Assert.Equal(1, outcome.Conflicts);
        ImportConflict conflict = Assert.Single(book.ImportConflicts);
        Assert.Equal("same-id", conflict.TransactionId);
        Assert.Equal([-10m, -20m], [.. conflict.Candidates.Select(candidate => candidate.Amount)]);
    }

    [Fact]
    public void ChangedParsedRow_UpdatesUnlockedRecordButQueuesSettledCorrection()
    {
        EmmaTransactionRecord initial = Parse(TestWorkbookFactory.Row(id: "changed-id", amount: -10m));
        EmmaTransactionRecord changed = Parse(TestWorkbookFactory.Row(id: "changed-id", amount: -20m));
        ReconciliationBook unlocked = Book();
        unlocked.Import([Map(initial)]);

        ImportOutcome unlockedOutcome = unlocked.Import([Map(changed)]);

        Assert.Equal(1, unlockedOutcome.Updated);
        Assert.Equal(-20m, Assert.Single(unlocked.Transactions).Amount);

        ReconciliationBook settled = Book();
        settled.ImportSettledBaseline([Map(initial)], confirmedAlreadySettled: true);

        ImportOutcome settledOutcome = settled.Import([Map(changed)]);

        Assert.Equal(1, settledOutcome.Corrections);
        PendingCorrection correction = Assert.Single(settled.PendingCorrections);
        Assert.Equal(-10m, correction.Original.Amount);
        Assert.Equal(-20m, correction.Proposed.Amount);
    }

    [Fact]
    public void LateParsedRow_RetainsItsSourceDateAndImportsAfterNewerActivity()
    {
        ReconciliationBook book = Book();
        book.Import([Map(Parse(TestWorkbookFactory.Row(id: "newer", date: "2026-09-01")))]);
        EmmaTransactionRecord late = Parse(TestWorkbookFactory.Row(id: "late", date: "2025-12-31"));

        ImportOutcome outcome = book.Import([Map(late)]);

        Assert.Equal(1, outcome.Added);
        Assert.Equal(new DateOnly(2025, 12, 31), book.Transactions.Single(transaction => transaction.Id == "late").Date);
    }

    private static EmmaTransactionRecord Parse(IReadOnlyList<object?> row)
    {
        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(rows: [row]));
        Assert.True(result.IsValid);
        return Assert.Single(result.Records);
    }

    private static ImportedTransaction Map(EmmaTransactionRecord source) => new(
        source.Id, source.Date, source.Amount, source.Account, "card", source.Bank, source.Currency,
        source.Category, source.Subcategory, source.Type, source.Tags, source.Counterparty, source.CustomName,
        source.Merchant, source.AdditionalDetails, source.Notes, source.LinkedTransactionId,
        source.Classification == ImportClassification.RefundCandidate ? TransactionKind.Refund : TransactionKind.Purchase);

    private static ReconciliationBook Book() => new(new FundingConfiguration("card",
    [
        new("intermediate", "Intermediate", AccountRole.Intermediate, 0m),
        new("primary", "Primary", AccountRole.PrimaryPersonal, 0m),
        new("savings", "Savings", AccountRole.PersonalSavings, 0m, 1),
        new("joint", "Joint", AccountRole.Joint, 0m),
        new("shared", "Shared", AccountRole.SharedSavings, 0m)
    ]), new AllocationRules([new("Groceries", FundingSource.Joint)], []));
}