using System.Globalization;
using System.IO.Compression;
using System.Xml;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;

namespace DotFinance.ImportInspector;

public static class ExportInspector
{
    public const int MaximumInputBytes = 32 * 1024 * 1024;

    private const long MaximumUncompressedBytes = 128L * 1024 * 1024;
    private const int MaximumPackageEntries = 2048;
    private const int MaximumCells = 250_000;
    private const int MaximumColumns = 512;

    private static readonly string[] ExpectedHeaders =
    [
        "ID", "Date", "Amount", "Account", "Bank", "Currency", "Category", "Subcategory", "Type", "Tags",
        "Counterparty", "Custom Name", "Merchant", "Additional details", "Notes", "Linked transaction ID"
    ];

    private static readonly string[] CellKindOrder = ["string", "number", "date", "boolean", "error"];
    private static readonly string[] NumberFormatOrder = ["general", "number", "date", "currency", "percentage", "custom"];
    private static readonly string[] DateTextSyntaxOrder =
    [
        "yyyy-MM-dd", "dd/MM/yyyy", "MM/dd/yyyy", "yyyy/MM/dd", "dd-MM-yyyy", "MM-dd-yyyy", "yyyyMMdd"
    ];

    public static InspectionResult Inspect(byte[] input)
    {
        ArgumentNullException.ThrowIfNull(input);
        List<Finding> findings = [];
        if (input.Length == 0)
        {
            findings.Add(new("error", "EMPTY_INPUT", "The export is empty."));
            return EmptyResult(findings);
        }

        if (input.Length > MaximumInputBytes)
        {
            findings.Add(new("error", "INPUT_TOO_LARGE", "The export exceeds the 32 MiB inspection limit."));
            return EmptyResult(findings);
        }

        PackageCheck packageCheck = InspectPackage(input);
        if (packageCheck == PackageCheck.Unreadable)
        {
            findings.Add(new("error", "WORKBOOK_UNREADABLE", "The export is not a readable, unencrypted XLSX workbook."));
            return EmptyResult(findings);
        }

        if (packageCheck == PackageCheck.LimitExceeded)
        {
            findings.Add(new("error", "WORKBOOK_LIMIT_EXCEEDED", "The workbook package exceeds the safe inspection limits."));
            return EmptyResult(findings);
        }

        try
        {
            using MemoryStream stream = new(input, writable: false);
            OpenSettings settings = new() { AutoSave = false, MaxCharactersInPart = MaximumUncompressedBytes };
            using SpreadsheetDocument document = SpreadsheetDocument.Open(stream, false, settings);
            WorkbookPart? workbookPart = document.WorkbookPart;
            if (workbookPart?.Workbook?.Sheets is null)
            {
                findings.Add(new("error", "WORKBOOK_MALFORMED", "The workbook structure is incomplete."));
                return EmptyResult(findings);
            }

            Workbook workbookRoot = workbookPart.Workbook;
            Sheets sheetsRoot = workbookRoot.Sheets;
            string dateSystem = workbookRoot.WorkbookProperties?.Date1904?.Value == true ? "1904" : "1900";
            WorkbookProfile workbook = new("xlsx", "none", dateSystem);
            List<(Sheet Sheet, WorksheetPart Part)> worksheets = [];
            foreach (Sheet sheet in sheetsRoot.Elements<Sheet>())
            {
                if (sheet.Id?.Value is null)
                {
                    findings.Add(new("error", "WORKBOOK_MALFORMED", "A worksheet relationship is missing."));
                    continue;
                }

                if (workbookPart.GetPartById(sheet.Id.Value) is not WorksheetPart worksheetPart)
                {
                    findings.Add(new("error", "WORKBOOK_MALFORMED", "A worksheet relationship is invalid."));
                    continue;
                }

                worksheets.Add((sheet, worksheetPart));
            }

            if (worksheets.Count != 1)
                findings.Add(new("error", "WORKSHEET_AMBIGUOUS", "The workbook must contain exactly one worksheet."));
            if (!CellCountIsSafe(worksheets.Select(item => item.Part)))
                findings.Add(new("error", "WORKBOOK_LIMIT_EXCEEDED", "The workbook contains more cells than the safe inspection limit."));
            if (HasErrors(findings))
                return new(new("4", "error", workbook, [], [], findings), null);

            InspectUnsafeFeatures(document, workbookPart, findings);
            if (new OpenXmlValidator().Validate(document).Any())
                AddOnce(findings, new("error", "WORKBOOK_SCHEMA_INVALID", "The workbook does not conform to the supported XLSX schema."));

            foreach ((Sheet sheet, WorksheetPart worksheetPart) in worksheets)
            {
                Worksheet worksheet = worksheetPart.Worksheet ?? throw new FormatException("Missing worksheet root.");
                SheetStateValues? sheetState = sheet.State?.Value;
                if ((sheetState.HasValue && (sheetState.Value == SheetStateValues.Hidden || sheetState.Value == SheetStateValues.VeryHidden))
                    || worksheet.Descendants<Row>().Any(row => row.Hidden?.Value == true)
                    || worksheet.Descendants<Column>().Any(column => column.Hidden?.Value == true))
                    AddOnce(findings, new("error", "HIDDEN_CONTENT_PRESENT", "The workbook contains hidden sheet, row, or column content."));
            }

            if (HasErrors(findings))
                return new(new("4", "error", workbook, [], [], findings), null);

            workbook = workbook with { WorksheetSelection = "single-visible" };
            WorksheetPart selectedPart = worksheets[0].Part;
            Worksheet selectedWorksheet = selectedPart.Worksheet ?? throw new FormatException("Missing worksheet root.");
            Row? headerRow = selectedWorksheet.GetFirstChild<SheetData>()?.Elements<Row>().FirstOrDefault();
            if (headerRow is null)
            {
                findings.Add(new("error", "HEADER_MISSING", "The worksheet does not contain a header row."));
                return new(new("4", "error", workbook, [], [], findings), null);
            }

            string[] headers = ReadHeaders(headerRow, workbookPart, findings);
            ValidateHeaders(headers, findings);
            List<HeaderProfile> publicHeaders = BuildHeaderProfiles(headers);
            List<ColumnProfile> columns = InspectColumns(selectedPart, workbookPart, headers, publicHeaders, findings);
            AddTextSyntaxWarnings(headers, columns, findings);
            string status = HasErrors(findings) ? "error" : findings.Any(x => x.Severity == "warning") ? "warning" : "valid";
            InspectionReport report = new("4", status, workbook, publicHeaders, columns, findings);
            byte[]? synthetic = status == "error" || headers.Length == 0
                ? null
                : BuildSyntheticWorkbook(headers, publicHeaders, columns, dateSystem == "1904");
            return new(report, synthetic);
        }
        catch (Exception exception) when (exception is OpenXmlPackageException or InvalidDataException or IOException or FormatException or ArgumentOutOfRangeException or XmlException)
        {
            findings.Clear();
            findings.Add(new("error", "WORKBOOK_UNREADABLE", "The export is not a readable, unencrypted XLSX workbook."));
            return EmptyResult(findings);
        }
    }

    private static bool CellCountIsSafe(IEnumerable<WorksheetPart> worksheetParts)
    {
        int cells = 0;
        XmlReaderSettings settings = new()
        {
            DtdProcessing = DtdProcessing.Prohibit,
            IgnoreComments = true,
            IgnoreProcessingInstructions = true,
            IgnoreWhitespace = true,
            MaxCharactersInDocument = MaximumUncompressedBytes,
            XmlResolver = null
        };
        foreach (WorksheetPart worksheetPart in worksheetParts)
        {
            using Stream stream = worksheetPart.GetStream(FileMode.Open, FileAccess.Read);
            using XmlReader reader = XmlReader.Create(stream, settings);
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element || reader.LocalName != "c") continue;
                cells++;
                if (cells > MaximumCells) return false;
            }
        }

        return true;
    }

    private static InspectionResult EmptyResult(IReadOnlyList<Finding> findings) => new(
        new("4", "error", new("unknown", "none", "unknown"), [], [], findings), null);

    private static PackageCheck InspectPackage(byte[] input)
    {
        try
        {
            using MemoryStream stream = new(input, writable: false);
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

    private static void InspectUnsafeFeatures(SpreadsheetDocument document, WorkbookPart workbookPart, List<Finding> findings)
    {
        OpenXmlPart[] parts = [.. EnumerateParts(document)];
        if (document.DocumentType != SpreadsheetDocumentType.Workbook || parts.Any(part => part is VbaProjectPart))
            AddOnce(findings, new("error", "MACROS_NOT_SUPPORTED", "Macro-enabled workbooks are not supported."));
        if (document.ExternalRelationships.Any()
            || document.HyperlinkRelationships.Any()
            || parts.Any(part => part.ExternalRelationships.Any() || part.HyperlinkRelationships.Any()))
            AddOnce(findings, new("error", "EXTERNAL_RELATIONSHIP_PRESENT", "External workbook relationships are not supported."));
        if (parts.Any(part => part is EmbeddedObjectPart or EmbeddedPackagePart))
            AddOnce(findings, new("error", "EMBEDDED_CONTENT_PRESENT", "Embedded objects or packages are not supported."));
        if (parts.Any(part => part is DrawingsPart or ChartPart or ImagePart or WorksheetCommentsPart))
            AddOnce(findings, new("error", "AUXILIARY_CONTENT_PRESENT", "Drawings, charts, images, or comments are not supported."));
        if (parts.Any(part => !IsAllowedPart(part)))
            AddOnce(findings, new("error", "UNSUPPORTED_WORKBOOK_PART", "The workbook contains an unsupported data, control, or auxiliary part."));
        if (workbookPart.WorksheetParts.SelectMany(part => (part.Worksheet ?? throw new FormatException("Missing worksheet root."))
            .Descendants<Cell>()).Any(cell => cell.CellFormula is not null))
            AddOnce(findings, new("error", "FORMULA_PRESENT", "Formula cells are not supported."));
        if (workbookPart.Workbook?.DefinedNames?.Elements<DefinedName>().Any() == true)
            AddOnce(findings, new("warning", "DEFINED_NAMES_PRESENT", "Workbook-defined names were detected and are not copied."));
    }

    private static bool IsAllowedPart(OpenXmlPart part) => part is WorkbookPart
        or WorksheetPart
        or WorkbookStylesPart
        or SharedStringTablePart
        or ThemePart
        or CoreFilePropertiesPart
        or ExtendedFilePropertiesPart
        or CustomFilePropertiesPart;

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

    private static string[] ReadHeaders(Row headerRow, WorkbookPart workbookPart, List<Finding> findings)
    {
        List<(int Column, Cell Cell)> cells = IndexedCells(headerRow, findings);
        if (cells.Count == 0) return [];
        int width = cells.Max(item => item.Column) + 1;
        if (width > MaximumColumns)
        {
            findings.Add(new("error", "WORKBOOK_LIMIT_EXCEEDED", "The header exceeds the safe column limit."));
            return [];
        }

        string[] headers = new string[width];
        foreach ((int column, Cell cell) in cells)
            headers[column] = ReadCellText(cell, workbookPart) ?? string.Empty;
        return headers;
    }

    private static List<(int Column, Cell Cell)> IndexedCells(Row row, List<Finding> findings)
    {
        List<(int Column, Cell Cell)> result = [];
        int nextColumn = 0;
        HashSet<int> used = [];
        foreach (Cell cell in row.Elements<Cell>())
        {
            int column = cell.CellReference?.Value is { } reference ? ParseColumnIndex(reference) : nextColumn;
            if (column < 0 || column >= MaximumColumns || !used.Add(column))
            {
                AddOnce(findings, new("error", "WORKBOOK_MALFORMED", "The worksheet contains invalid or duplicate cell positions."));
                continue;
            }

            result.Add((column, cell));
            nextColumn = column + 1;
        }

        return result;
    }

    private static int ParseColumnIndex(string cellReference)
    {
        int column = 0;
        int letters = 0;
        foreach (char character in cellReference)
        {
            if (character is >= 'A' and <= 'Z')
            {
                column = checked(column * 26 + character - 'A' + 1);
                letters++;
            }
            else if (character is >= 'a' and <= 'z')
            {
                column = checked(column * 26 + character - 'a' + 1);
                letters++;
            }
            else
            {
                break;
            }
        }

        return letters == 0 ? -1 : column - 1;
    }

    private static string? ReadCellText(Cell cell, WorkbookPart workbookPart)
    {
        if (cell.DataType?.Value == CellValues.SharedString)
        {
            if (!int.TryParse(cell.CellValue?.Text, NumberStyles.None, CultureInfo.InvariantCulture, out int index) || index < 0)
                throw new FormatException("Invalid shared string index.");
            SharedStringItem? item = workbookPart.SharedStringTablePart?.SharedStringTable?.Elements<SharedStringItem>().ElementAtOrDefault(index);
            return item?.InnerText ?? throw new FormatException("Missing shared string.");
        }

        return cell.DataType?.Value == CellValues.InlineString ? cell.InlineString?.InnerText : cell.CellValue?.Text;
    }

    private static void ValidateHeaders(string[] headers, List<Finding> findings)
    {
        if (headers.Length == 0)
        {
            findings.Add(new("error", "HEADER_MISSING", "The worksheet does not contain a usable header row."));
            return;
        }

        for (int index = 0; index < headers.Length; index++)
            if (string.IsNullOrWhiteSpace(headers[index]))
                findings.Add(new("error", "HEADER_EMPTY", "A header is empty.", index + 1));
        if (headers.Distinct(StringComparer.Ordinal).Count() != headers.Length)
            findings.Add(new("error", "HEADER_DUPLICATE", "The header contains duplicate names."));
        if (ExpectedHeaders.Except(headers, StringComparer.Ordinal).Any())
            findings.Add(new("warning", "EXPECTED_HEADERS_MISSING", "One or more specification headers are absent."));
        if (headers.Except(ExpectedHeaders, StringComparer.Ordinal).Any())
            findings.Add(new("warning", "UNEXPECTED_HEADERS_PRESENT", "One or more headers are not in the current specification list."));
        if (headers.Length == ExpectedHeaders.Length && !headers.SequenceEqual(ExpectedHeaders, StringComparer.Ordinal))
            findings.Add(new("warning", "HEADER_ORDER_DIFFERS", "The known headers are not in the current specification order."));
    }

    private static List<HeaderProfile> BuildHeaderProfiles(string[] headers)
    {
        List<HeaderProfile> profiles = [];
        int empty = 0;
        int unexpected = 0;
        for (int index = 0; index < headers.Length; index++)
        {
            string header = headers[index];
            profiles.Add(ExpectedHeaders.Contains(header, StringComparer.Ordinal)
                ? new(header, index + 1, "expected")
                : string.IsNullOrWhiteSpace(header)
                    ? new($"EMPTY_COLUMN_{++empty}", index + 1, "empty")
                    : new($"UNEXPECTED_COLUMN_{++unexpected}", index + 1, "unexpected"));
        }

        return profiles;
    }

    private static List<ColumnProfile> InspectColumns(
        WorksheetPart worksheetPart,
        WorkbookPart workbookPart,
        string[] headers,
        IReadOnlyList<HeaderProfile> publicHeaders,
        List<Finding> findings)
    {
        HashSet<string>[] kinds = [.. Enumerable.Range(0, headers.Length).Select(_ => new HashSet<string>(StringComparer.Ordinal))];
        HashSet<string>[] formats = [.. Enumerable.Range(0, headers.Length).Select(_ => new HashSet<string>(StringComparer.Ordinal))];
        int dateColumn = Array.IndexOf(headers, "Date");
        HashSet<string>? dateTextSyntaxes = null;
        SheetData? sheetData = (worksheetPart.Worksheet ?? throw new FormatException("Missing worksheet root.")).GetFirstChild<SheetData>();
        if (sheetData is not null)
        {
            foreach (Row row in sheetData.Elements<Row>().Skip(1))
            {
                foreach ((int column, Cell cell) in IndexedCells(row, findings))
                {
                    if (!CellHasValue(cell)) continue;
                    if (column >= headers.Length)
                    {
                        AddOnce(findings, new("error", "CELL_OUTSIDE_HEADER", "A populated cell lies outside the declared header columns."));
                        continue;
                    }

                    if (!CellValueIsValid(cell, workbookPart))
                    {
                        AddOnce(findings, new("error", "CELL_VALUE_INVALID", "A cell value does not match its declared XLSX type."));
                        continue;
                    }

                    string format = ClassifyNumberFormat(cell, workbookPart);
                    string kind = ClassifyCellKind(cell, format);
                    kinds[column].Add(kind);
                    formats[column].Add(format);
                    if (column == dateColumn && kind == "string")
                    {
                        HashSet<string> candidates = ClassifyDateTextSyntaxes(ReadCellText(cell, workbookPart));
                        if (dateTextSyntaxes is null)
                            dateTextSyntaxes = candidates;
                        else
                            dateTextSyntaxes.IntersectWith(candidates);
                    }
                }
            }
        }

        return [.. publicHeaders.Select((header, index) => new ColumnProfile(
            header.Token,
            header.Position,
            [.. CellKindOrder.Where(kinds[index].Contains)],
            [.. NumberFormatOrder.Where(formats[index].Contains)],
            index == dateColumn && dateTextSyntaxes is not null
                ? [.. DateTextSyntaxOrder.Where(dateTextSyntaxes.Contains)]
                : []))];
    }

    private static HashSet<string> ClassifyDateTextSyntaxes(string? value)
    {
        HashSet<string> candidates = new(StringComparer.Ordinal);
        if (value is null) return candidates;
        foreach (string syntax in DateTextSyntaxOrder)
            if (DateOnly.TryParseExact(value, syntax, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                candidates.Add(syntax);
        return candidates;
    }

    private static bool CellValueIsValid(Cell cell, WorkbookPart workbookPart)
    {
        CellValues? dataType = cell.DataType?.Value;
        if (dataType.HasValue && dataType.Value == CellValues.SharedString)
        {
            try
            {
                _ = ReadCellText(cell, workbookPart);
                return true;
            }
            catch (FormatException)
            {
                return false;
            }
        }

        string? value = cell.CellValue?.Text;
        return dataType.HasValue && (dataType.Value == CellValues.InlineString
            || dataType.Value == CellValues.String
            || dataType.Value == CellValues.Error)
            || (dataType.HasValue && dataType.Value == CellValues.Boolean
            ? value is "0" or "1" or "true" or "false"
            : dataType.HasValue && dataType.Value == CellValues.Date
            ? DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _)
            : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _));
    }

    private static void AddTextSyntaxWarnings(string[] headers, List<ColumnProfile> columns, List<Finding> findings)
    {
        int dateColumn = Array.IndexOf(headers, "Date");
        if (dateColumn >= 0 && columns[dateColumn].CellKinds.Contains("string", StringComparer.Ordinal))
        {
            if (columns[dateColumn].TextSyntaxes.Count == 0)
                findings.Add(new("warning", "DATE_TEXT_SYNTAX_NOT_CAPTURED", "The text date syntax is unsupported or inconsistent.", dateColumn + 1));
            else if (columns[dateColumn].TextSyntaxes.Count > 1)
                findings.Add(new("warning", "DATE_TEXT_SYNTAX_AMBIGUOUS", "The text date order is ambiguous from the available values.", dateColumn + 1));
        }
        int amountColumn = Array.IndexOf(headers, "Amount");
        if (amountColumn >= 0 && columns[amountColumn].CellKinds.Contains("string", StringComparer.Ordinal))
            findings.Add(new("warning", "AMOUNT_TEXT_SYNTAX_NOT_CAPTURED", "Amount cells stored as text require a separate synthetic format description.", amountColumn + 1));
    }

    private static string ClassifyCellKind(Cell cell, string numberFormat)
    {
        CellValues? dataType = cell.DataType?.Value;
        return dataType.HasValue && dataType.Value == CellValues.Boolean
            ? "boolean"
            : dataType.HasValue && dataType.Value == CellValues.Error
            ? "error"
            : dataType.HasValue && dataType.Value == CellValues.Date
            ? "date"
            : dataType.HasValue && (dataType.Value == CellValues.SharedString
            || dataType.Value == CellValues.InlineString
            || dataType.Value == CellValues.String)
            ? "string"
            : numberFormat == "date" ? "date" : "number";
    }

    private static string ClassifyNumberFormat(Cell cell, WorkbookPart workbookPart)
    {
        if (cell.StyleIndex?.Value is not uint styleIndex || workbookPart.WorkbookStylesPart?.Stylesheet?.CellFormats is not { } cellFormats)
            return "general";
        CellFormat? cellFormat = cellFormats.Elements<CellFormat>().ElementAtOrDefault(checked((int)styleIndex));
        if (cellFormat?.NumberFormatId?.Value is not uint formatId || formatId == 0) return "general";
        if (IsBuiltInDateFormat(formatId)) return "date";
        if (formatId is 5 or 6 or 7 or 8 or >= 37 and <= 44) return "currency";
        if (formatId is 9 or 10) return "percentage";
        string? formatCode = workbookPart.WorkbookStylesPart.Stylesheet.NumberingFormats?
            .Elements<NumberingFormat>()
            .FirstOrDefault(format => format.NumberFormatId?.Value == formatId)?.FormatCode?.Value;
        if (formatCode is null) return "number";
        string visibleCode = RemoveQuotedAndEscapedText(formatCode).ToLowerInvariant();
        return visibleCode.IndexOfAny(['y', 'd', 'h', 's']) >= 0
            ? "date"
            : visibleCode.Contains('%')
            ? "percentage"
            : visibleCode.IndexOfAny(['£', '$', '€', '¥']) >= 0 || visibleCode.Contains("[$", StringComparison.Ordinal)
            ? "currency"
            : visibleCode.IndexOfAny(['0', '#', '?']) >= 0 ? "number" : "custom";
    }

    private static bool IsBuiltInDateFormat(uint formatId) => formatId is >= 14 and <= 22
        or >= 27 and <= 36
        or >= 45 and <= 47
        or >= 50 and <= 58;

    private static string RemoveQuotedAndEscapedText(string formatCode)
    {
        System.Text.StringBuilder result = new();
        bool quoted = false;
        for (int index = 0; index < formatCode.Length; index++)
        {
            char character = formatCode[index];
            if (character == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (character == '\\' && index + 1 < formatCode.Length)
            {
                index++;
                continue;
            }

            if (!quoted) result.Append(character);
        }

        return result.ToString();
    }

    private static byte[] BuildSyntheticWorkbook(
        string[] sourceHeaders,
        List<HeaderProfile> publicHeaders,
        List<ColumnProfile> columns,
        bool date1904)
    {
        using MemoryStream stream = new();
        using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, true))
        {
            WorkbookPart workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook(new WorkbookProperties { Date1904 = date1904 });
            WorkbookStylesPart stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = CreateStylesheet();
            WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            SheetData sheetData = new();
            worksheetPart.Worksheet = new Worksheet(sheetData);
            Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
            sheets.Append(new Sheet { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Synthetic export" });

            Row header = new() { RowIndex = 1 };
            foreach (HeaderProfile profile in publicHeaders) header.Append(CreateStringCell(profile.Token));
            sheetData.Append(header);
            for (int rowIndex = 0; rowIndex < 8; rowIndex++)
            {
                Row row = new() { RowIndex = checked((uint)rowIndex + 2) };
                for (int column = 0; column < sourceHeaders.Length; column++)
                    row.Append(CreateSyntheticCell(sourceHeaders[column], publicHeaders[column].Token, columns[column], rowIndex, date1904));
                sheetData.Append(row);
            }

            workbookPart.Workbook.Save();
        }

        return NormalisePackageTimestamps(stream.ToArray());
    }

    private static Stylesheet CreateStylesheet()
    {
        NumberingFormats numberingFormats = new(
            new NumberingFormat { NumberFormatId = 164, FormatCode = "yyyy-mm-dd" },
            new NumberingFormat { NumberFormatId = 165, FormatCode = "#,##0.00" },
            new NumberingFormat { NumberFormatId = 166, FormatCode = "£#,##0.00" },
            new NumberingFormat { NumberFormatId = 167, FormatCode = "0.00%" })
        { Count = 4 };
        Fonts fonts = new(new Font()) { Count = 1 };
        Fills fills = new(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
        { Count = 2 };
        Borders borders = new(new Border()) { Count = 1 };
        CellStyleFormats styleFormats = new(new CellFormat()) { Count = 1 };
        CellFormats cellFormats = new(
            new CellFormat(),
            new CellFormat { NumberFormatId = 164, ApplyNumberFormat = true },
            new CellFormat { NumberFormatId = 165, ApplyNumberFormat = true },
            new CellFormat { NumberFormatId = 166, ApplyNumberFormat = true },
            new CellFormat { NumberFormatId = 167, ApplyNumberFormat = true })
        { Count = 5 };
        CellStyles cellStyles = new(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 };
        Stylesheet stylesheet = new();
        stylesheet.Append(numberingFormats, fonts, fills, borders, styleFormats, cellFormats, cellStyles);
        return stylesheet;
    }

    private static Cell CreateSyntheticCell(string sourceHeader, string publicHeader, ColumnProfile profile, int row, bool date1904)
    {
        string kind = profile.CellKinds.Count > 0
            ? profile.CellKinds[row % profile.CellKinds.Count]
            : string.Equals(sourceHeader, "Date", StringComparison.Ordinal) ? "date"
            : string.Equals(sourceHeader, "Amount", StringComparison.Ordinal) ? "number"
            : "string";
        return kind switch
        {
            "date" => new Cell { CellValue = new CellValue(SyntheticDateSerial(row, date1904)), StyleIndex = 1 },
            "number" => new Cell { CellValue = new CellValue((row % 2 == 0 ? -12.34m : 56.78m).ToString(CultureInfo.InvariantCulture)), StyleIndex = NumberStyle(profile, row) },
            "boolean" => new Cell { DataType = CellValues.Boolean, CellValue = new CellValue(row % 2 == 0 ? "1" : "0") },
            "error" => new Cell { DataType = CellValues.Error, CellValue = new CellValue("#N/A") },
            _ => CreateStringCell(SyntheticString(sourceHeader, publicHeader, profile, row))
        };
    }

    private static string SyntheticDateSerial(int row, bool date1904)
    {
        double serial = new DateTime(2001, 2, 3).AddDays(row).ToOADate();
        if (date1904) serial -= 1462;
        return serial.ToString(CultureInfo.InvariantCulture);
    }

    private static uint NumberStyle(ColumnProfile profile, int row)
    {
        string format = profile.NumberFormats.Count == 0 ? "number" : profile.NumberFormats[row % profile.NumberFormats.Count];
        return format switch
        {
            "date" => 1U,
            "currency" => 3U,
            "percentage" => 4U,
            _ => 2U
        };
    }

    private static string SyntheticString(string sourceHeader, string publicHeader, ColumnProfile profile, int row)
    {
        return sourceHeader switch
        {
            "ID" => $"ID-{row + 1:0000}",
            "Linked transaction ID" => row == 0 ? string.Empty : "ID-0001",
            "Date" => profile.TextSyntaxes.Count == 1
                ? new DateOnly(2001, 2, 3).AddDays(row).ToString(profile.TextSyntaxes[0], CultureInfo.InvariantCulture)
                : $"DATE_{(row % 2) + 1}",
            "Amount" => $"AMOUNT_{(row % 2) + 1}",
            "Currency" => row % 2 == 0 ? "AAA" : "BBB",
            "Type" => $"TYPE_{(row % 2) + 1}",
            _ => $"{publicHeader}_{(row % 2) + 1}"
        };
    }

    private static Cell CreateStringCell(string value) => new()
    {
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
    };

    private static byte[] NormalisePackageTimestamps(byte[] package)
    {
        using MemoryStream stream = new();
        stream.Write(package);
        Array.Clear(package);
        stream.Position = 0;
        using (ZipArchive archive = new(stream, ZipArchiveMode.Update, leaveOpen: true))
            foreach (ZipArchiveEntry entry in archive.Entries)
                entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        return stream.ToArray();
    }

    private static bool CellHasValue(Cell cell) => cell.CellValue is not null || cell.InlineString is not null || cell.CellFormula is not null;

    private static bool HasErrors(IEnumerable<Finding> findings) => findings.Any(finding => finding.Severity == "error");

    private static void AddOnce(List<Finding> findings, Finding finding)
    {
        if (!findings.Any(existing => existing.Code == finding.Code)) findings.Add(finding);
    }

    private enum PackageCheck { Safe, Unreadable, LimitExceeded }
}