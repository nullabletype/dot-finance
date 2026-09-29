using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Xml;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;

namespace DotFinance.Import;

public static partial class EmmaXlsxImporter
{
    public const string ContractVersion = "emma-xlsx-v1";
    public const int MaximumInputBytes = 32 * 1024 * 1024;

    public const long MaximumUncompressedBytes = 128L * 1024 * 1024;
    public const int MaximumSharedStrings = 250_000;
    public const int MaximumCellFormats = 65_536;
    private const int MaximumPackageEntries = 2048;
    public const int MaximumRows = 25_000;

    private static readonly string[] ExpectedHeaders =
    [
        "ID", "Date", "Amount", "Account", "Bank", "Currency", "Category", "Subcategory", "Type", "Tags",
        "Counterparty", "Custom Name", "Merchant", "Additional details", "Notes", "Linked transaction ID"
    ];

    private static readonly HashSet<string> SpendingTypes = new(StringComparer.Ordinal)
    {
        "Purchase", "Direct Debit", "Standing Order", "Bill Payment"
    };

    public static EmmaImportResult Parse(ReadOnlyMemory<byte> input)
    {
        if (input.IsEmpty) return Invalid("EMPTY_INPUT", "The export is empty.");
        if (input.Length > MaximumInputBytes)
            return Invalid("INPUT_TOO_LARGE", "The export exceeds the 32 MiB import limit.");

        PackageCheck package = InspectPackage(input);
        if (package == PackageCheck.Unreadable)
            return Invalid("WORKBOOK_UNREADABLE", "The export is not a readable, unencrypted XLSX workbook.");
        if (package == PackageCheck.LimitExceeded)
            return Invalid("WORKBOOK_LIMIT_EXCEEDED", "The workbook package exceeds the safe import limits.");

        try
        {
            using MemoryStream stream = new(input.ToArray(), writable: false);
            OpenSettings settings = new() { AutoSave = false, MaxCharactersInPart = MaximumUncompressedBytes };
            using SpreadsheetDocument document = SpreadsheetDocument.Open(stream, false, settings);
            WorkbookPart? workbookPart = document.WorkbookPart;
            if (workbookPart?.Workbook?.Sheets is null)
                return Invalid("WORKBOOK_MALFORMED", "The workbook structure is incomplete.");

            List<ImportIssue> issues = [];
            ValidateWorkbook(document, workbookPart, issues);
            List<(Sheet Sheet, WorksheetPart Part)> worksheets = ReadWorksheets(workbookPart, issues);
            if (worksheets.Count != 1)
                issues.Add(new("WORKSHEET_AMBIGUOUS", "The workbook must contain exactly one visible worksheet."));
            if (issues.Count != 0) return Invalid(issues);

            Worksheet worksheet = worksheets[0].Part.Worksheet
                ?? throw new FormatException("Missing worksheet root.");
            string[] sharedStrings = ReadSharedStrings(workbookPart, issues);
            CellFormat[] cellFormats = ReadCellFormats(workbookPart, issues);
            if (issues.Count != 0) return Invalid(issues);
            SheetData? sheetData = worksheet.GetFirstChild<SheetData>();
            Row? headerRow = sheetData?.Elements<Row>().FirstOrDefault();
            if (headerRow is null) return Invalid("HEADER_MISSING", "The worksheet does not contain a header row.");

            string[] headers = ReadRow(headerRow, sharedStrings, issues, header: true);
            ValidateHeaders(headers, issues);
            if (issues.Count != 0) return Invalid(issues);

            List<EmmaTransactionRecord> records = [];
            int sourceRowCount = 0;
            foreach (Row row in sheetData!.Elements<Row>().Skip(1))
            {
                sourceRowCount++;
                if (sourceRowCount > MaximumRows)
                {
                    issues.Add(new("WORKBOOK_LIMIT_EXCEEDED", "The workbook contains more rows than the safe import limit."));
                    break;
                }

                string[] values = ReadRow(row, sharedStrings, issues, header: false);
                if (values.All(string.IsNullOrEmpty)) continue;
                int rowNumber = checked((int)(row.RowIndex?.Value ?? (uint)(records.Count + 2)));
                ParseRecord(values, row, cellFormats, rowNumber, issues, records);
            }

            return issues.Count == 0
                ? new(ContractVersion, [.. records], [])
                : Invalid(issues);
        }
        catch (Exception exception) when (exception is OpenXmlPackageException or InvalidDataException or IOException
            or FormatException or ArgumentOutOfRangeException or OverflowException or XmlException)
        {
            return Invalid("WORKBOOK_UNREADABLE", "The export is not a readable, unencrypted XLSX workbook.");
        }
    }

    private static void ValidateWorkbook(SpreadsheetDocument document, WorkbookPart workbookPart, List<ImportIssue> issues)
    {
        Workbook workbook = workbookPart.Workbook ?? throw new FormatException("Missing workbook root.");
        if (document.DocumentType != SpreadsheetDocumentType.Workbook)
            issues.Add(new("WORKBOOK_TYPE_UNSUPPORTED", "Only unencrypted .xlsx workbooks are supported."));
        if (workbook.WorkbookProperties?.Date1904?.Value == true)
            issues.Add(new("DATE_SYSTEM_UNSUPPORTED", "The workbook must use the Excel 1900 date system."));
        if (new OpenXmlValidator().Validate(document).Any())
            issues.Add(new("WORKBOOK_SCHEMA_INVALID", "The workbook does not conform to the supported XLSX schema."));

        OpenXmlPart[] parts = [.. EnumerateParts(document)];
        if (parts.Any(part => part is VbaProjectPart))
            issues.Add(new("MACROS_NOT_SUPPORTED", "Macro-enabled workbooks are not supported."));
        if (document.ExternalRelationships.Any() || document.HyperlinkRelationships.Any()
            || parts.Any(part => part.ExternalRelationships.Any() || part.HyperlinkRelationships.Any()))
            issues.Add(new("EXTERNAL_RELATIONSHIP_PRESENT", "External workbook relationships are not supported."));
        if (parts.Any(part => part is EmbeddedObjectPart or EmbeddedPackagePart))
            issues.Add(new("EMBEDDED_CONTENT_PRESENT", "Embedded objects or packages are not supported."));
        if (parts.Any(part => !IsAllowedPart(part)))
            issues.Add(new("WORKBOOK_FEATURE_UNSUPPORTED", "The workbook contains unsupported active or presentation content."));
        if (workbookPart.CalculationChainPart is not null || workbook.DefinedNames?.Elements<DefinedName>().Any() == true)
            issues.Add(new("WORKBOOK_FEATURE_UNSUPPORTED", "Calculated or named workbook content is not supported."));
    }

    private static List<(Sheet Sheet, WorksheetPart Part)> ReadWorksheets(WorkbookPart workbookPart, List<ImportIssue> issues)
    {
        List<(Sheet, WorksheetPart)> worksheets = [];
        Sheets sheets = workbookPart.Workbook?.Sheets ?? throw new FormatException("Missing worksheet collection.");
        foreach (Sheet sheet in sheets.Elements<Sheet>())
        {
            SheetStateValues? state = sheet.State?.Value;
            if (state.HasValue && (state.Value == SheetStateValues.Hidden || state.Value == SheetStateValues.VeryHidden))
            {
                issues.Add(new("HIDDEN_CONTENT_PRESENT", "The workbook contains hidden sheet, row, or column content."));
                continue;
            }
            if (sheet.Id?.Value is null || workbookPart.GetPartById(sheet.Id.Value) is not WorksheetPart part)
            {
                issues.Add(new("WORKBOOK_MALFORMED", "A worksheet relationship is missing or invalid."));
                continue;
            }
            Worksheet? worksheet = part.Worksheet;
            if (worksheet is null)
            {
                issues.Add(new("WORKBOOK_MALFORMED", "A worksheet is incomplete."));
                continue;
            }
            if (worksheet.Descendants<Row>().Any(row => row.Hidden?.Value == true)
                || worksheet.Descendants<Column>().Any(column => column.Hidden?.Value == true))
                issues.Add(new("HIDDEN_CONTENT_PRESENT", "The workbook contains hidden sheet, row, or column content."));
            if (worksheet.Descendants<Cell>().Any(cell => cell.CellFormula is not null))
                issues.Add(new("FORMULA_NOT_SUPPORTED", "Formula cells are not supported."));
            worksheets.Add((sheet, part));
        }
        return worksheets;
    }

    private static string[] ReadRow(Row row, IReadOnlyList<string> sharedStrings, List<ImportIssue> issues, bool header)
    {
        string[] values = new string[ExpectedHeaders.Length];
        Cell[] cells = [.. row.Elements<Cell>().Take(ExpectedHeaders.Length + 1)];
        if (cells.Length > ExpectedHeaders.Length)
        {
            issues.Add(new("COLUMN_OUT_OF_RANGE", "The worksheet contains more cells in a row than the supported contract.", RowNumber(row)));
            return values;
        }
        HashSet<int> seen = [];
        int sequential = 0;
        foreach (Cell cell in cells)
        {
            int column = cell.CellReference?.Value is { Length: > 0 } reference
                ? ColumnIndex(reference)
                : sequential;
            sequential = column + 1;
            if (column < 0 || column >= ExpectedHeaders.Length)
            {
                issues.Add(new("COLUMN_OUT_OF_RANGE", "The worksheet contains data outside the supported columns.", RowNumber(row)));
                continue;
            }
            if (!seen.Add(column))
            {
                issues.Add(new("CELL_DUPLICATE", "The worksheet contains duplicate cells in a row.", RowNumber(row), ExpectedHeaders[column]));
                continue;
            }
            values[column] = !header && column == 2
                ? cell.CellValue?.Text ?? string.Empty
                : ReadText(cell, sharedStrings, header, RowNumber(row), ExpectedHeaders[column], issues);
        }
        return values;
    }

    private static void ValidateHeaders(string[] headers, List<ImportIssue> issues)
    {
        for (int index = 0; index < ExpectedHeaders.Length; index++)
        {
            if (headers[index] != ExpectedHeaders[index])
                issues.Add(new("HEADER_MISMATCH", $"Column {index + 1} must be '{ExpectedHeaders[index]}'.", 1, ExpectedHeaders[index]));
        }
    }

    private static void ParseRecord(string[] values, Row row, IReadOnlyList<CellFormat> cellFormats, int rowNumber,
        List<ImportIssue> issues, List<EmmaTransactionRecord> records)
    {
        string id = Required(values[0], "ID", rowNumber, issues);
        string dateText = Required(values[1], "Date", rowNumber, issues);
        string account = Required(values[3], "Account", rowNumber, issues);
        string currency = Required(values[5], "Currency", rowNumber, issues);
        string type = Required(values[8], "Type", rowNumber, issues);

        if (!DateOnly.TryParseExact(dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly date))
            issues.Add(new("DATE_INVALID", "Date must be text in yyyy-MM-dd format.", rowNumber, "Date"));
        if (!CurrencyPattern().IsMatch(currency))
            issues.Add(new("CURRENCY_INVALID", "Currency must be a three-letter uppercase code.", rowNumber, "Currency"));

        Cell? amountCell = CellAt(row, 2);
        decimal amount = 0m;
        if (amountCell is null || !IsGeneralNumber(amountCell, cellFormats)
            || !TryParseExactAmount(amountCell.CellValue?.Text, out amount))
            issues.Add(new("AMOUNT_INVALID", "Amount must be an exactly representable decimal in a numeric General cell.", rowNumber, "Amount"));

        if (issues.Any(issue => issue.Row == rowNumber)) return;
        (ImportClassification classification, ImmutableArray<ImportReviewReason> reviews) = Classify(type, amount, values[15], currency);
        records.Add(new(id, date, amount, account, values[4], currency, values[6], values[7], type, values[9],
            values[10], values[11], values[12], values[13], values[14], values[15], classification, reviews));
    }

    private static (ImportClassification Classification, ImmutableArray<ImportReviewReason> Reviews) Classify(
        string type, decimal amount, string linkedTransactionId, string currency)
    {
        ImportClassification classification;
        List<ImportReviewReason> reviews = [];
        if (SpendingTypes.Contains(type) && amount < 0m)
            classification = ImportClassification.PurchaseCandidate;
        else if (type == "Credit" && amount > 0m && !string.IsNullOrWhiteSpace(linkedTransactionId))
            classification = ImportClassification.RefundCandidate;
        else if (type == "Transfer")
            classification = ImportClassification.ExcludedTransfer;
        else
        {
            classification = ImportClassification.ReviewRequired;
            if (type == "Credit" && amount > 0m)
                reviews.Add(ImportReviewReason.UnlinkedCredit);
            else if (SpendingTypes.Contains(type) || type == "Credit")
                reviews.Add(ImportReviewReason.ContradictoryAmountDirection);
            else
                reviews.Add(ImportReviewReason.SourceTypeRequiresReview);
        }
        if (classification != ImportClassification.ExcludedTransfer && currency != "GBP")
            reviews.Add(ImportReviewReason.ActualGbpRequired);
        return (classification, [.. reviews]);
    }

    private static string Required(string value, string field, int row, List<ImportIssue> issues)
    {
        if (string.IsNullOrWhiteSpace(value))
            issues.Add(new("REQUIRED_VALUE_MISSING", $"{field} must contain text.", row, field));
        return value;
    }

    private static string ReadText(Cell cell, IReadOnlyList<string> sharedStrings, bool header, int row, string field,
        List<ImportIssue> issues)
    {
        if (cell.CellFormula is not null)
        {
            issues.Add(new("FORMULA_NOT_SUPPORTED", "Formula cells are not supported.", row, field));
            return string.Empty;
        }
        if (cell.DataType is null && cell.CellValue is null && cell.InlineString is null) return string.Empty;
        CellValues? dataType = cell.DataType?.Value;
        string? value = dataType.HasValue && dataType.Value == CellValues.InlineString
            ? cell.InlineString?.InnerText
            : dataType.HasValue && dataType.Value == CellValues.SharedString
                ? ReadSharedString(cell, sharedStrings)
                : dataType.HasValue && dataType.Value == CellValues.String
                    ? cell.CellValue?.Text
                    : null;
        if (value is null)
        {
            issues.Add(new(header ? "HEADER_CELL_INVALID" : "TEXT_CELL_INVALID",
                header ? "Headers must be text cells." : $"{field} must be a text cell when populated.", row, field));
            return string.Empty;
        }
        return value;
    }

    private static string? ReadSharedString(Cell cell, IReadOnlyList<string> sharedStrings)
    {
        return !int.TryParse(cell.CellValue?.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int index)
            ? null
            : index >= 0 && index < sharedStrings.Count ? sharedStrings[index] : null;
    }

    private static string[] ReadSharedStrings(WorkbookPart workbookPart, List<ImportIssue> issues)
    {
        IEnumerable<SharedStringItem> items = workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>()
            ?? [];
        SharedStringItem[] bounded = [.. items.Take(MaximumSharedStrings + 1)];
        if (bounded.Length > MaximumSharedStrings)
        {
            issues.Add(new("WORKBOOK_LIMIT_EXCEEDED", "The workbook contains more shared strings than the safe import limit."));
            return [];
        }
        return [.. bounded.Select(item => item.InnerText)];
    }

    private static CellFormat[] ReadCellFormats(WorkbookPart workbookPart, List<ImportIssue> issues)
    {
        IEnumerable<CellFormat> formats = workbookPart.WorkbookStylesPart?.Stylesheet?.CellFormats?.Elements<CellFormat>()
            ?? [];
        CellFormat[] bounded = [.. formats.Take(MaximumCellFormats + 1)];
        if (bounded.Length > MaximumCellFormats)
        {
            issues.Add(new("WORKBOOK_LIMIT_EXCEEDED", "The workbook contains more cell formats than the safe import limit."));
            return [];
        }
        return bounded;
    }

    private static bool IsGeneralNumber(Cell cell, IReadOnlyList<CellFormat> cellFormats)
    {
        CellValues? dataType = cell.DataType?.Value;
        if (dataType.HasValue && dataType.Value != CellValues.Number) return false;
        uint styleIndex = cell.StyleIndex?.Value ?? 0;
        return styleIndex >= cellFormats.Count
            ? styleIndex == 0 && cellFormats.Count == 0
            : (cellFormats[(int)styleIndex].NumberFormatId?.Value ?? 0) == 0;
    }

    private static bool TryParseExactAmount(string? text, out decimal amount)
    {
        amount = 0m;
        if (text is null || !AmountPattern().IsMatch(text)
            || !decimal.TryParse(text, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out amount))
            return false;

        string source = CanonicalDecimal(text);
        string parsed = CanonicalDecimal(amount.ToString("0.#############################", CultureInfo.InvariantCulture));
        return source == parsed;
    }

    private static string CanonicalDecimal(string value)
    {
        bool negative = value[0] == '-';
        ReadOnlySpan<char> unsigned = value.AsSpan(negative || value[0] == '+' ? 1 : 0);
        int dot = unsigned.IndexOf('.');
        ReadOnlySpan<char> integer = dot < 0 ? unsigned : unsigned[..dot];
        ReadOnlySpan<char> fraction = dot < 0 ? [] : unsigned[(dot + 1)..];
        integer = integer.TrimStart('0');
        fraction = fraction.TrimEnd('0');
        string magnitude = $"{(integer.IsEmpty ? "0" : integer.ToString())}{(fraction.IsEmpty ? string.Empty : $".{fraction}")}";
        return negative && magnitude != "0" ? $"-{magnitude}" : magnitude;
    }

    private static Cell? CellAt(Row row, int requestedColumn)
    {
        int sequential = 0;
        foreach (Cell cell in row.Elements<Cell>())
        {
            int column = cell.CellReference?.Value is { Length: > 0 } reference ? ColumnIndex(reference) : sequential;
            sequential = column + 1;
            if (column == requestedColumn) return cell;
        }
        return null;
    }

    private static int ColumnIndex(string reference)
    {
        int value = 0;
        int letters = 0;
        foreach (char character in reference)
        {
            if (!char.IsAsciiLetterUpper(character)) break;
            value = checked(value * 26 + character - 'A' + 1);
            letters++;
        }
        return letters == 0 ? throw new FormatException("Invalid cell reference.") : value - 1;
    }

    private static int RowNumber(Row row) => checked((int)(row.RowIndex?.Value ?? 0));

    private static PackageCheck InspectPackage(ReadOnlyMemory<byte> input)
    {
        try
        {
            using MemoryStream stream = new(input.ToArray(), writable: false);
            using ZipArchive archive = new(stream, ZipArchiveMode.Read, leaveOpen: false);
            if (archive.Entries.Count == 0) return PackageCheck.Unreadable;
            if (archive.Entries.Count > MaximumPackageEntries) return PackageCheck.LimitExceeded;
            long total = 0;
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                if (entry.Length > MaximumUncompressedBytes) return PackageCheck.LimitExceeded;
                total = checked(total + entry.Length);
                if (total > MaximumUncompressedBytes) return PackageCheck.LimitExceeded;
            }
            return PackageCheck.Safe;
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or OverflowException)
        {
            return PackageCheck.Unreadable;
        }
    }

    private static IEnumerable<OpenXmlPart> EnumerateParts(OpenXmlPartContainer root)
    {
        HashSet<OpenXmlPart> seen = [];
        Stack<OpenXmlPart> pending = new(root.Parts.Select(pair => pair.OpenXmlPart));
        while (pending.Count > 0)
        {
            OpenXmlPart part = pending.Pop();
            if (!seen.Add(part)) continue;
            yield return part;
            foreach (IdPartPair child in part.Parts) pending.Push(child.OpenXmlPart);
        }
    }

    private static bool IsAllowedPart(OpenXmlPart part) => part is WorkbookPart
        or WorksheetPart
        or WorkbookStylesPart
        or SharedStringTablePart
        or ThemePart
        or CoreFilePropertiesPart
        or ExtendedFilePropertiesPart
        or CustomFilePropertiesPart;

    private static EmmaImportResult Invalid(string code, string message) => Invalid([new(code, message)]);

    private static EmmaImportResult Invalid(IEnumerable<ImportIssue> issues) => new(ContractVersion, [], [.. issues]);

    [GeneratedRegex("^[A-Z]{3}$", RegexOptions.CultureInvariant)]
    private static partial Regex CurrencyPattern();

    [GeneratedRegex("^[+-]?[0-9]+(?:\\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex AmountPattern();

    private enum PackageCheck { Safe, Unreadable, LimitExceeded }
}