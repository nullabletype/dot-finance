using System.Globalization;

using CsvHelper;
using CsvHelper.Configuration.Attributes;

namespace DesktopShell;

/// <summary>Reads the fictional reconciliation fixtures into unallocated transactions.</summary>
public static class ReconciliationImport
{
    public static IReadOnlyList<Transaction> Read(string path)
    {
        using StreamReader reader = File.OpenText(path);
        using CsvReader csv = new(reader, CultureInfo.InvariantCulture);
        return [.. csv.GetRecords<ImportRow>()
            .Select(row => new Transaction(
                row.Id,
                DateOnly.ParseExact(row.Date, "yyyy-MM-dd", CultureInfo.InvariantCulture),
                decimal.Parse(row.Amount, NumberStyles.Number | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture),
                row.Account,
                row.Bank,
                row.Currency,
                row.Category,
                row.Subcategory,
                row.Type,
                row.Tags,
                row.Counterparty,
                row.CustomName,
                row.Merchant,
                row.AdditionalDetails,
                row.Notes,
                row.LinkedTransactionId,
                null,
                false,
                false))];
    }

    private sealed class ImportRow
    {
        [Name("ID")]
        public string Id { get; init; } = string.Empty;

        public string Date { get; init; } = string.Empty;

        public string Amount { get; init; } = string.Empty;

        public string Account { get; init; } = string.Empty;

        public string Bank { get; init; } = string.Empty;

        public string Currency { get; init; } = string.Empty;

        public string Category { get; init; } = string.Empty;

        public string Subcategory { get; init; } = string.Empty;

        public string Type { get; init; } = string.Empty;

        public string Tags { get; init; } = string.Empty;

        public string Counterparty { get; init; } = string.Empty;

        [Name("Custom Name")]
        public string CustomName { get; init; } = string.Empty;

        public string Merchant { get; init; } = string.Empty;

        [Name("Additional details")]
        public string AdditionalDetails { get; init; } = string.Empty;

        public string Notes { get; init; } = string.Empty;

        [Name("Linked transaction ID")]
        public string LinkedTransactionId { get; init; } = string.Empty;
    }
}