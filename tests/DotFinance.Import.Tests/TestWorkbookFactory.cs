using System.Globalization;
using System.IO.Compression;
using System.Text;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

namespace DotFinance.Import.Tests;

internal static class TestWorkbookFactory
{
    internal static readonly string[] Headers =
    [
        "ID", "Date", "Amount", "Account", "Bank", "Currency", "Category", "Subcategory", "Type", "Tags",
        "Counterparty", "Custom Name", "Merchant", "Additional details", "Notes", "Linked transaction ID"
    ];

    internal static object?[] Row(
        string id = "synthetic-1",
        string date = "2026-02-28",
        decimal amount = -12.34m,
        string currency = "GBP",
        string type = "Purchase",
        string linked = "") =>
    [
        id, date, amount, "Fictional card", "Synthetic bank", currency, "Groceries", "Food", type, "fictional-tag",
        "Fictional counterparty", "Fictional custom name", "Fictional merchant", "Synthetic details", "Synthetic notes", linked
    ];

    internal static byte[] Create(
        IReadOnlyList<string>? headers = null,
        IReadOnlyList<IReadOnlyList<object?>>? rows = null,
        bool date1904 = false,
        bool sharedStrings = false,
        SpreadsheetDocumentType documentType = SpreadsheetDocumentType.Workbook,
        Action<SpreadsheetDocument, WorkbookPart, WorksheetPart, Sheet>? configure = null)
    {
        headers ??= Headers;
        rows ??= [Row()];
        using MemoryStream stream = new();
        using (SpreadsheetDocument document = SpreadsheetDocument.Create(stream, documentType, true))
        {
            WorkbookPart workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook { WorkbookProperties = new WorkbookProperties { Date1904 = date1904 } };
            WorkbookStylesPart stylesPart = workbookPart.AddNewPart<WorkbookStylesPart>();
            stylesPart.Stylesheet = Styles();
            WorksheetPart worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            SheetData data = new();
            worksheetPart.Worksheet = new Worksheet(data);
            data.Append(BuildRow(1, [.. headers.Cast<object?>()]));
            uint index = 2;
            foreach (IReadOnlyList<object?> values in rows) data.Append(BuildRow(index++, values));
            Sheets sheets = workbookPart.Workbook.AppendChild(new Sheets());
            Sheet sheet = new() { Id = workbookPart.GetIdOfPart(worksheetPart), SheetId = 1, Name = "Transactions" };
            sheets.Append(sheet);
            if (sharedStrings) ConvertToSharedStrings(workbookPart, worksheetPart);
            configure?.Invoke(document, workbookPart, worksheetPart, sheet);
            workbookPart.Workbook.Save();
        }
        return stream.ToArray();
    }

    internal static FormulaValue Formula(string formula, decimal cached) => new(formula, cached);
    internal static TextAsNumber NumberText(string value) => new(value);
    internal static NumericLexeme Number(string value) => new(value);

    internal static byte[] CreatePackageWithEntries(int count)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            for (int index = 0; index < count; index++) archive.CreateEntry($"synthetic/{index}.xml");
        }
        return stream.ToArray();
    }

    internal static byte[] CreatePackageWithUncompressedEntry(long length)
    {
        using MemoryStream stream = new();
        using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("synthetic/oversized.xml", CompressionLevel.SmallestSize);
            using Stream destination = entry.Open();
            byte[] buffer = new byte[64 * 1024];
            for (long remaining = length; remaining > 0; remaining -= Math.Min(remaining, buffer.Length))
                destination.Write(buffer, 0, (int)Math.Min(remaining, buffer.Length));
        }
        return stream.ToArray();
    }

    internal static byte[] ConvertWorksheetToUtf16(byte[] workbook)
    {
        using MemoryStream source = new(workbook, writable: false);
        using ZipArchive input = new(source, ZipArchiveMode.Read, leaveOpen: true);
        using MemoryStream target = new();
        using (ZipArchive output = new(target, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (ZipArchiveEntry entry in input.Entries)
            {
                ZipArchiveEntry copy = output.CreateEntry(entry.FullName, CompressionLevel.SmallestSize);
                using Stream inputStream = entry.Open();
                using Stream outputStream = copy.Open();
                if (entry.FullName == "xl/worksheets/sheet1.xml")
                {
                    using StreamReader reader = new(inputStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                    string xml = reader.ReadToEnd().Replace("encoding=\"utf-8\"", "encoding=\"utf-16\"", StringComparison.OrdinalIgnoreCase);
                    using StreamWriter writer = new(outputStream, Encoding.Unicode, leaveOpen: true);
                    writer.Write(xml);
                }
                else
                {
                    inputStream.CopyTo(outputStream);
                }
            }
        }
        return target.ToArray();
    }

    private static Row BuildRow(uint index, IReadOnlyList<object?> values)
    {
        Row row = new() { RowIndex = index };
        for (int column = 0; column < values.Count; column++)
        {
            Cell cell = Cell(values[column]);
            cell.CellReference = $"{ColumnName(column)}{index}";
            row.Append(cell);
        }
        return row;
    }

    private static Cell Cell(object? value) => value switch
    {
        null => new Cell(),
        decimal number => new Cell { CellValue = new CellValue(number.ToString(CultureInfo.InvariantCulture)) },
        NumericLexeme number => new Cell { CellValue = new CellValue(number.Value) },
        TextAsNumber text => StringCell(text.Value),
        FormulaValue formula => new Cell
        {
            CellFormula = new CellFormula(formula.Formula),
            CellValue = new CellValue(formula.Cached.ToString(CultureInfo.InvariantCulture))
        },
        _ => StringCell(Convert.ToString(value, CultureInfo.InvariantCulture)!)
    };

    private static Cell StringCell(string value) => new()
    {
        DataType = CellValues.InlineString,
        InlineString = new InlineString(new Text(value) { Space = SpaceProcessingModeValues.Preserve })
    };

    private static void ConvertToSharedStrings(WorkbookPart workbookPart, WorksheetPart worksheetPart)
    {
        SharedStringTablePart part = workbookPart.AddNewPart<SharedStringTablePart>();
        part.SharedStringTable = new SharedStringTable();
        foreach (Cell cell in worksheetPart.Worksheet!.Descendants<Cell>().Where(cell => cell.DataType?.Value == CellValues.InlineString))
        {
            int index = part.SharedStringTable.Elements<SharedStringItem>().Count();
            part.SharedStringTable.Append(new SharedStringItem(new Text(cell.InlineString?.InnerText ?? string.Empty)
            {
                Space = SpaceProcessingModeValues.Preserve
            }));
            cell.InlineString = null;
            cell.DataType = CellValues.SharedString;
            cell.CellValue = new CellValue(index.ToString(CultureInfo.InvariantCulture));
        }
        part.SharedStringTable.Save();
    }

    private static string ColumnName(int index)
    {
        string value = string.Empty;
        for (int column = index + 1; column > 0; column = (column - 1) / 26)
            value = (char)('A' + (column - 1) % 26) + value;
        return value;
    }

    private static Stylesheet Styles()
    {
        Fonts fonts = new(new Font()) { Count = 1 };
        Fills fills = new(
            new Fill(new PatternFill { PatternType = PatternValues.None }),
            new Fill(new PatternFill { PatternType = PatternValues.Gray125 }))
        { Count = 2 };
        Borders borders = new(new Border()) { Count = 1 };
        CellStyleFormats styleFormats = new(new CellFormat()) { Count = 1 };
        CellFormats formats = new(new CellFormat()) { Count = 1 };
        CellStyles styles = new(new CellStyle { Name = "Normal", FormatId = 0, BuiltinId = 0 }) { Count = 1 };
        Stylesheet stylesheet = new();
        stylesheet.Append(fonts, fills, borders, styleFormats, formats, styles);
        return stylesheet;
    }

    internal sealed record FormulaValue(string Formula, decimal Cached);
    internal sealed record TextAsNumber(string Value);
    internal sealed record NumericLexeme(string Value);
}