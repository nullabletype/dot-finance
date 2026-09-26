using System.Globalization;

using CsvHelper;
using CsvHelper.Configuration.Attributes;

namespace DesktopShell;

public static class SyntheticEmmaImport
{
    public static PrototypeState Read(string path)
    {
        using StreamReader reader = File.OpenText(path);
        using CsvReader csv = new(reader, CultureInfo.InvariantCulture);
        List<EmmaRow> rows = [.. csv.GetRecords<EmmaRow>()];
        List<PrototypeTransaction> transactions = [.. rows.Select((row, index) => new PrototypeTransaction(
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
            index == 0 ? "Joint" : "Personal",
            index != 0))];

        return new PrototypeState
        {
            ImportedAt = new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero),
            AllocationStatus = "Synthetic fixture reviewed",
            Transactions = transactions
        };
    }

    private sealed class EmmaRow
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