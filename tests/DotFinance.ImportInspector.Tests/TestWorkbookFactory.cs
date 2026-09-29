using System.Globalization;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace DotFinance.ImportInspector.Tests;

internal static class TestWorkbookFactory
{
    public static readonly string[] ExpectedHeaders =
    [
        "ID", "Date", "Amount", "Account", "Bank", "Currency", "Category", "Subcategory", "Type", "Tags",
        "Counterparty", "Custom Name", "Merchant", "Additional details", "Notes", "Linked transaction ID"
    ];

    public static byte[] Create(
        IReadOnlyList<string>? headers = null,
        IReadOnlyList<IReadOnlyList<object?>>? rows = null,
        string sheetName = "Transactions",
        string? creator = null,
        SpreadsheetDocumentType documentType = SpreadsheetDocumentType.Workbook,
        Action<SpreadsheetDocument, WorkbookPart, WorksheetPart, Sheet>? configure = null)
    {
        headers ??= ExpectedHeaders;
        rows ??= [RepresentativeRow("private-id-1")];
        using MemoryStream stream = new();
        using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, documentType, true))
        {
            if (creator is not null) document.PackageProperties.Creator = creator;
            WorkbookPart workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            WorkbookStylesPart stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = Styles();
            WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            SheetData sheetData = new();
            worksheetPart.Worksheet = new Worksheet(sheetData);
            Row headerRow = new() { RowIndex = 1 };
            foreach (string header in headers) headerRow.Append(StringCell(header));
            sheetData.Append(headerRow);
            uint rowIndex = 2;
            foreach (IReadOnlyList<object?> values in rows)
            {
                Row row = new() { RowIndex = rowIndex++ };
                foreach (object? value in values) row.Append(ValueCell(value));
                sheetData.Append(row);
            }

            Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
            Sheet sheet = new() { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = sheetName };
            sheets.Append(sheet);
            configure?.Invoke(document, workbookPart, worksheetPart, sheet);
            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }

    public static IReadOnlyList<object?> RepresentativeRow(string id) =>
    [
        id, new DateTime(2026, 9, 1), -12.34m, "Private card", "Private bank", "GBP", "Private category", null,
        "Private purchase", null, "Private counterparty", null, "Private merchant", "Private details", "Private notes", null
    ];

    public static string ReadAllText(byte[] workbook)
    {
        using MemoryStream stream = new(workbook, writable: false);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(stream, false);
        return string.Join('|', document.WorkbookPart!.WorksheetParts
            .SelectMany(part => part.Worksheet!.Descendants<Cell>())
            .Select(cell => cell.InnerText));
    }

    public static int DataRowCount(byte[] workbook)
    {
        using MemoryStream stream = new(workbook, writable: false);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(stream, false);
        return document.WorkbookPart!.WorksheetParts.Single().Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().Count() - 1;
    }

    public static string ReadSemanticSnapshot(byte[] workbook)
    {
        using MemoryStream stream = new(workbook, writable: false);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(stream, false);
        WorkbookPart workbookPart = document.WorkbookPart!;
        return string.Join('|', workbookPart.WorksheetParts.Single().Worksheet!.Descendants<Cell>().Select(cell =>
            $"{cell.DataType?.Value}:{cell.StyleIndex?.Value}:{cell.InnerText}"));
    }

    public static bool Uses1904DateSystem(byte[] workbook)
    {
        using MemoryStream stream = new(workbook, writable: false);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(stream, false);
        return document.WorkbookPart!.Workbook!.WorkbookProperties?.Date1904?.Value == true;
    }

    public static DateTime ReadFirstSyntheticDate(byte[] workbook)
    {
        using MemoryStream stream = new(workbook, writable: false);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(stream, false);
        Workbook workbookRoot = document.WorkbookPart!.Workbook!;
        SheetData sheetData = document.WorkbookPart.WorksheetParts.Single().Worksheet!.GetFirstChild<SheetData>()!;
        int dateColumn = sheetData.Elements<Row>().First().Elements<Cell>().Select((cell, index) => (cell, index))
            .Single(item => item.cell.InnerText == "Date").index;
        string serialText = sheetData.Elements<Row>().Skip(1).First().Elements<Cell>().ElementAt(dateColumn).CellValue!.Text;
        double serial = double.Parse(serialText, CultureInfo.InvariantCulture);
        if (workbookRoot.WorkbookProperties?.Date1904?.Value == true) serial += 1462;
        return DateTime.FromOADate(serial);
    }

    private static Cell ValueCell(object? value) => value switch
    {
        null => new Cell(),
        DateTime date => new Cell
        {
            CellValue = new CellValue(date.ToOADate().ToString(CultureInfo.InvariantCulture)),
            StyleIndex = 1
        },
        decimal number => new Cell
        {
            CellValue = new CellValue(number.ToString(CultureInfo.InvariantCulture)),
            StyleIndex = 2
        },
        bool boolean => new Cell
        {
            DataType = CellValues.Boolean,
            CellValue = new CellValue(boolean ? "1" : "0")
        },
        _ => StringCell(Convert.ToString(value, CultureInfo.InvariantCulture)!)
    };

    private static Cell StringCell(string value) => new()
    {
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
    };

    private static Stylesheet Styles()
    {
        NumberingFormats numberingFormats = new(new NumberingFormat { NumberFormatId = 165, FormatCode = "£#,##0.00" }) { Count = 1 };
        Fonts fonts = new(new Font()) { Count = 1 };
        Fills fills = new(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
        { Count = 2 };
        Borders borders = new(new Border()) { Count = 1 };
        CellStyleFormats styleFormats = new(new CellFormat()) { Count = 1 };
        CellFormats cellFormats = new(
            new CellFormat(),
            new CellFormat { NumberFormatId = 14, ApplyNumberFormat = true },
            new CellFormat { NumberFormatId = 165, ApplyNumberFormat = true })
        { Count = 3 };
        CellStyles cellStyles = new(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 };
        Stylesheet stylesheet = new();
        stylesheet.Append(numberingFormats, fonts, fills, borders, styleFormats, cellFormats, cellStyles);
        return stylesheet;
    }
}