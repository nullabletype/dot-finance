using System.Text;
using System.Text.Json;

using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Validation;

using DotFinance.ImportInspector;

using Xunit;

namespace DotFinance.ImportInspector.Tests;

public sealed class ExportInspectorTests
{
    [Theory]
    [InlineData("2026-09-13", "yyyy-MM-dd", "2001-02-03")]
    [InlineData("13/09/2026", "dd/MM/yyyy", "03/02/2001")]
    [InlineData("09/13/2026", "MM/dd/yyyy", "02/03/2001")]
    public void Inspect_TextDate_ReportsOnlyFixedSyntaxAndGeneratesMatchingSyntheticDate(
        string privateDate,
        string expectedSyntax,
        string expectedSyntheticDate)
    {
        object?[] row = [.. TestWorkbookFactory.RepresentativeRow("private-id")];
        row[1] = privateDate;

        InspectionResult result = ExportInspector.Inspect(TestWorkbookFactory.Create(rows: [row]));

        ColumnProfile date = Assert.Single(result.Report.Columns, column => column.HeaderToken == "Date");
        Assert.Equal([expectedSyntax], date.TextSyntaxes);
        Assert.DoesNotContain(result.Report.Findings, finding => finding.Code.StartsWith("DATE_TEXT_SYNTAX_", StringComparison.Ordinal));
        Assert.DoesNotContain(privateDate, JsonSerializer.Serialize(result.Report), StringComparison.Ordinal);
        Assert.Contains(expectedSyntheticDate, TestWorkbookFactory.ReadAllText(result.SyntheticWorkbook!), StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_AmbiguousTextDate_ReportsCandidatesWithoutChoosingOrCopyingSourceValue()
    {
        object?[] row = [.. TestWorkbookFactory.RepresentativeRow("private-id")];
        row[1] = "01/02/2026";

        InspectionResult result = ExportInspector.Inspect(TestWorkbookFactory.Create(rows: [row]));

        ColumnProfile date = Assert.Single(result.Report.Columns, column => column.HeaderToken == "Date");
        Assert.Equal(["dd/MM/yyyy", "MM/dd/yyyy"], date.TextSyntaxes);
        Assert.Contains(result.Report.Findings, finding => finding.Code == "DATE_TEXT_SYNTAX_AMBIGUOUS");
        string combined = JsonSerializer.Serialize(result.Report) + TestWorkbookFactory.ReadAllText(result.SyntheticWorkbook!);
        Assert.DoesNotContain("01/02/2026", combined, StringComparison.Ordinal);
        Assert.Contains("DATE_1", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_RepresentativeWorkbook_ReportsOnlyStructuralContract()
    {
        InspectionResult result = ExportInspector.Inspect(TestWorkbookFactory.Create());

        Assert.Equal("valid", result.Report.Status);
        Assert.Equal("4", result.Report.SchemaVersion);
        Assert.Equal(new WorkbookProfile("xlsx", "single-visible", "1900"), result.Report.Workbook);
        Assert.Equal(TestWorkbookFactory.ExpectedHeaders, result.Report.Headers.Select(header => header.Token));
        ColumnProfile date = Assert.Single(result.Report.Columns, column => column.HeaderToken == "Date");
        Assert.Equal(["date"], date.CellKinds);
        Assert.Equal(["date"], date.NumberFormats);
        ColumnProfile amount = Assert.Single(result.Report.Columns, column => column.HeaderToken == "Amount");
        Assert.Equal(["number"], amount.CellKinds);
        Assert.Equal(["currency"], amount.NumberFormats);
        Assert.NotNull(result.SyntheticWorkbook);
        Assert.Equal(8, TestWorkbookFactory.DataRowCount(result.SyntheticWorkbook));
        InspectionResult reparsed = ExportInspector.Inspect(result.SyntheticWorkbook);
        Assert.Equal("valid", reparsed.Report.Status);
    }

    [Fact]
    public void Inspect_GeneratedSyntheticWorkbook_IsSchemaValid()
    {
        InspectionResult result = ExportInspector.Inspect(TestWorkbookFactory.Create());
        using MemoryStream stream = new(result.SyntheticWorkbook!, writable: false);
        using SpreadsheetDocument document = SpreadsheetDocument.Open(stream, false);

        Assert.Empty(new OpenXmlValidator().Validate(document, TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Inspect_PrivateValuesWorkbookMetadataAndSheetName_NeverAppearInShareableOutputs()
    {
        byte[] input = TestWorkbookFactory.Create(
            sheetName: "SECRET-SHEET",
            creator: "SECRET-CREATOR",
            rows: [TestWorkbookFactory.RepresentativeRow("SECRET-ID")]);

        InspectionResult result = ExportInspector.Inspect(input);
        string report = JsonSerializer.Serialize(result.Report);
        string syntheticText = TestWorkbookFactory.ReadAllText(result.SyntheticWorkbook!);

        foreach (string secret in new[] { "SECRET-SHEET", "SECRET-CREATOR", "SECRET-ID", "Private card", "Private bank", "Private category", "Private notes" })
        {
            Assert.DoesNotContain(secret, report, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, syntheticText, StringComparison.Ordinal);
        }
        Assert.Contains("ID-0001", syntheticText, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_PrivateOrSpreadsheetActiveHeaders_EmitsOnlyPublicHeaderTokens()
    {
        string[] headers = ["Alice current account", "=WEBSERVICE(\"https://private.example\")", "Date", "Amount"];
        byte[] input = TestWorkbookFactory.Create(headers, [["PRIVATE-ACCOUNT", "PRIVATE-FORMULA", new DateTime(2026, 9, 1), -12.34m]]);

        InspectionResult result = ExportInspector.Inspect(input);
        string combined = JsonSerializer.Serialize(result.Report) + TestWorkbookFactory.ReadAllText(result.SyntheticWorkbook!);

        Assert.DoesNotContain("Alice current account", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("WEBSERVICE", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("private.example", combined, StringComparison.Ordinal);
        Assert.Contains("UNEXPECTED_COLUMN_1", combined, StringComparison.Ordinal);
        Assert.Contains("UNEXPECTED_COLUMN_2", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_EquivalentStructureWithDifferentVolumeAndMetadata_EmitsTheSameEvidence()
    {
        IReadOnlyList<object?> row = TestWorkbookFactory.RepresentativeRow("PRIVATE-ID-A");
        byte[] first = TestWorkbookFactory.Create(rows: [row], sheetName: "PRIVATE-SHEET-A", creator: "PRIVATE-CREATOR-A");
        byte[] second = TestWorkbookFactory.Create(
            rows: [.. Enumerable.Repeat(TestWorkbookFactory.RepresentativeRow("PRIVATE-ID-B"), 7)],
            sheetName: "PRIVATE-SHEET-B",
            creator: "PRIVATE-CREATOR-B");

        InspectionResult firstResult = ExportInspector.Inspect(first);
        InspectionResult secondResult = ExportInspector.Inspect(second);
        string serialized = JsonSerializer.Serialize(firstResult.Report);

        Assert.Equal(serialized, JsonSerializer.Serialize(secondResult.Report));
        Assert.Equal(
            TestWorkbookFactory.ReadSemanticSnapshot(firstResult.SyntheticWorkbook!),
            TestWorkbookFactory.ReadSemanticSnapshot(secondResult.SyntheticWorkbook!));
        foreach (string privateAggregate in new[] { "RecordCount", "RowCount", "SheetCount", "DistinctCount", "NegativeCount", "PositiveCount", "Creator", "SheetName" })
            Assert.DoesNotContain($"\"{privateAggregate}\"", serialized, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("formula", "FORMULA_PRESENT")]
    [InlineData("external", "EXTERNAL_RELATIONSHIP_PRESENT")]
    [InlineData("hidden", "HIDDEN_CONTENT_PRESENT")]
    [InlineData("embedded", "EMBEDDED_CONTENT_PRESENT")]
    [InlineData("multiple", "WORKSHEET_AMBIGUOUS")]
    [InlineData("macro", "MACROS_NOT_SUPPORTED")]
    [InlineData("connections", "UNSUPPORTED_WORKBOOK_PART")]
    public void Inspect_ActiveOrAmbiguousWorkbookFeature_RejectsWithoutSyntheticOutput(string feature, string expectedCode)
    {
        SpreadsheetDocumentType documentType = feature == "macro"
            ? SpreadsheetDocumentType.MacroEnabledWorkbook
            : SpreadsheetDocumentType.Workbook;
        byte[] input = TestWorkbookFactory.Create(
            documentType: documentType,
            configure: (_, workbookPart, worksheetPart, _) => ConfigureRisk(feature, workbookPart, worksheetPart));

        InspectionResult result = ExportInspector.Inspect(input);

        Assert.Equal("error", result.Report.Status);
        Assert.Contains(result.Report.Findings, finding => finding.Code == expectedCode);
        Assert.Null(result.SyntheticWorkbook);
    }

    [Fact]
    public void Inspect_DefinedName_ReportsOnlySafeWarningAndDoesNotCopyDefinition()
    {
        byte[] input = TestWorkbookFactory.Create(configure: (_, workbookPart, _, _) => workbookPart.Workbook!.Append(new DefinedNames(new DefinedName("'Transactions'!$A$2") { Name = "SECRET-NAME" })));

        InspectionResult result = ExportInspector.Inspect(input);
        string combined = JsonSerializer.Serialize(result.Report) + TestWorkbookFactory.ReadAllText(result.SyntheticWorkbook!);

        Assert.Equal("warning", result.Report.Status);
        Assert.Contains(result.Report.Findings, finding => finding.Code == "DEFINED_NAMES_PRESENT");
        Assert.DoesNotContain("SECRET-NAME", combined, StringComparison.Ordinal);
        Assert.DoesNotContain("Transactions", combined, StringComparison.Ordinal);
    }

    [Fact]
    public void Inspect_1904DateSystem_PreservesTheStructuralDateContract()
    {
        byte[] input = TestWorkbookFactory.Create(configure: (_, workbookPart, _, _) =>
            workbookPart.Workbook!.WorkbookProperties = new WorkbookProperties { Date1904 = true });

        InspectionResult result = ExportInspector.Inspect(input);

        Assert.Equal("1904", result.Report.Workbook.DateSystem);
        Assert.True(TestWorkbookFactory.Uses1904DateSystem(result.SyntheticWorkbook!));
        Assert.Equal(new DateTime(2001, 2, 3), TestWorkbookFactory.ReadFirstSyntheticDate(result.SyntheticWorkbook!));
    }

    [Fact]
    public void Inspect_UnreadableOrEncryptedContainer_ReturnsFixedFinding()
    {
        byte[] input = Encoding.UTF8.GetBytes("PRIVATE-NOT-A-WORKBOOK");

        InspectionResult result = ExportInspector.Inspect(input);

        Assert.Equal("error", result.Report.Status);
        Assert.Contains(result.Report.Findings, finding => finding.Code == "WORKBOOK_UNREADABLE");
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result.Report), StringComparison.Ordinal);
        Assert.Null(result.SyntheticWorkbook);
    }

    [Fact]
    public void Inspect_DuplicateAndMissingHeaders_ReturnsStableFindings()
    {
        string[] headers = [.. TestWorkbookFactory.ExpectedHeaders];
        headers[^1] = "Notes";

        InspectionResult result = ExportInspector.Inspect(TestWorkbookFactory.Create(headers));

        Assert.Equal("error", result.Report.Status);
        Assert.Contains(result.Report.Findings, finding => finding.Code == "HEADER_DUPLICATE");
        Assert.Contains(result.Report.Findings, finding => finding.Code == "EXPECTED_HEADERS_MISSING");
        Assert.Null(result.SyntheticWorkbook);
    }

    [Fact]
    public void Inspect_PopulatedCellOutsideHeader_RejectsWithoutDroppingTheField()
    {
        byte[] input = TestWorkbookFactory.Create(configure: (_, _, worksheetPart, _) =>
        {
            Row dataRow = worksheetPart.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().Skip(1).First();
            dataRow.Append(new Cell
            {
                CellReference = "Q2",
                DataType = CellValues.InlineString,
                InlineString = new InlineString(new Text("PRIVATE-EXTRA"))
            });
        });

        InspectionResult result = ExportInspector.Inspect(input);

        Assert.Equal("error", result.Report.Status);
        Assert.Contains(result.Report.Findings, finding => finding.Code == "CELL_OUTSIDE_HEADER");
        Assert.DoesNotContain("PRIVATE-EXTRA", JsonSerializer.Serialize(result.Report), StringComparison.Ordinal);
        Assert.Null(result.SyntheticWorkbook);
    }

    [Fact]
    public void Inspect_SchemaInvalidSource_RejectsWithFixedFinding()
    {
        byte[] input = TestWorkbookFactory.Create(configure: (_, _, worksheetPart, _) =>
        {
            Cell cell = worksheetPart.Worksheet!.Descendants<Cell>().Last();
            cell.DataType = CellValues.Boolean;
            cell.CellValue = new CellValue("PRIVATE-NOT-A-BOOLEAN");
            cell.InlineString = null;
        });

        InspectionResult result = ExportInspector.Inspect(input);

        Assert.Equal("error", result.Report.Status);
        Assert.Contains(result.Report.Findings, finding => finding.Code == "WORKBOOK_SCHEMA_INVALID");
        Assert.DoesNotContain("PRIVATE-NOT-A-BOOLEAN", JsonSerializer.Serialize(result.Report), StringComparison.Ordinal);
        Assert.Null(result.SyntheticWorkbook);
    }

    [Fact]
    public void Inspect_MissingSharedString_RejectsWithFixedFinding()
    {
        byte[] input = TestWorkbookFactory.Create(configure: (_, _, worksheetPart, _) =>
        {
            Cell cell = worksheetPart.Worksheet!.Descendants<Cell>().Last();
            cell.DataType = CellValues.SharedString;
            cell.CellValue = new CellValue("999");
            cell.InlineString = null;
        });

        InspectionResult result = ExportInspector.Inspect(input);

        Assert.Equal("error", result.Report.Status);
        Assert.Contains(result.Report.Findings, finding => finding.Code == "CELL_VALUE_INVALID");
        Assert.Null(result.SyntheticWorkbook);
    }

    private static void ConfigureRisk(string feature, WorkbookPart workbookPart, WorksheetPart worksheetPart)
    {
        switch (feature)
        {
            case "formula":
                worksheetPart.Worksheet!.Descendants<Cell>().Last().CellFormula = new CellFormula("WEBSERVICE(\"https://synthetic.invalid\")");
                break;
            case "external":
                worksheetPart.AddHyperlinkRelationship(new Uri("https://synthetic.invalid"), true, "rExternal");
                break;
            case "hidden":
                worksheetPart.Worksheet!.GetFirstChild<SheetData>()!.Elements<Row>().Last().Hidden = true;
                break;
            case "embedded":
                EmbeddedObjectPart embedded = worksheetPart.AddEmbeddedObjectPart("application/octet-stream");
                using (Stream stream = embedded.GetStream(FileMode.Create, FileAccess.Write)) stream.Write([1, 2, 3]);
                break;
            case "multiple":
                WorksheetPart secondPart = workbookPart.AddNewPart<WorksheetPart>();
                secondPart.Worksheet = new Worksheet(new SheetData(new Row(new Cell { DataType = CellValues.InlineString, InlineString = new InlineString(new Text("SECOND")) })));
                workbookPart.Workbook!.Sheets!.Append(new Sheet { Id = workbookPart.GetIdOfPart(secondPart), SheetId = 2, Name = "Second" });
                break;
            case "macro":
                break;
            case "connections":
                ConnectionsPart connectionsPart = workbookPart.AddNewPart<ConnectionsPart>();
                connectionsPart.Connections = new Connections();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(feature));
        }
    }
}