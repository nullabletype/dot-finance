using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;

using DotFinance.Import;

using Xunit;

namespace DotFinance.Import.Tests;

public sealed class EmmaXlsxImporterTests
{
    [Fact]
    public void Parse_CommittedVerifiedVariantFixture_RemainsCompatible()
    {
        using Stream fixture = typeof(EmmaXlsxImporterTests).Assembly.GetManifestResourceStream(
            "DotFinance.Import.Tests.Fixtures.verified-emma-xlsx-v1.xlsx.base64")
            ?? throw new InvalidOperationException("The committed XLSX fixture is missing.");
        using StreamReader reader = new(fixture);
        byte[] workbook = Convert.FromBase64String(reader.ReadToEnd().Trim());

        EmmaImportResult result = EmmaXlsxImporter.Parse(workbook);

        Assert.True(result.IsValid);
        EmmaTransactionRecord record = Assert.Single(result.Records);
        Assert.Equal("synthetic-1", record.Id);
        Assert.Equal(-12.34m, record.Amount);
    }

    [Fact]
    public void Parse_VerifiedVariant_PreservesEverySourceFieldAndRow()
    {
        object?[] first = TestWorkbookFactory.Row(id: " opaque-001 ", date: "2025-12-31", amount: -1234.56m);
        first[3] = "Card export name";
        first[4] = "Fictional bank";
        first[6] = "Groceries";
        first[7] = "Food";
        first[9] = "tag one,tag two";
        first[10] = "Counterparty value";
        first[11] = "Custom value";
        first[12] = "Merchant value";
        first[13] = "Additional value";
        first[14] = "Notes value";
        object?[] changedDuplicate = [.. first];
        changedDuplicate[2] = -1200.01m;

        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(rows: [first, changedDuplicate]));

        Assert.True(result.IsValid);
        Assert.Equal(EmmaXlsxImporter.ContractVersion, result.ContractVersion);
        Assert.Empty(result.Issues);
        Assert.Equal(2, result.Records.Length);
        EmmaTransactionRecord record = result.Records[0];
        Assert.Equal(" opaque-001 ", record.Id);
        Assert.Equal(new DateOnly(2025, 12, 31), record.Date);
        Assert.Equal(-1234.56m, record.Amount);
        Assert.Equal("Card export name", record.Account);
        Assert.Equal("Fictional bank", record.Bank);
        Assert.Equal("GBP", record.Currency);
        Assert.Equal("Groceries", record.Category);
        Assert.Equal("Food", record.Subcategory);
        Assert.Equal("Purchase", record.Type);
        Assert.Equal("tag one,tag two", record.Tags);
        Assert.Equal("Counterparty value", record.Counterparty);
        Assert.Equal("Custom value", record.CustomName);
        Assert.Equal("Merchant value", record.Merchant);
        Assert.Equal("Additional value", record.AdditionalDetails);
        Assert.Equal("Notes value", record.Notes);
        Assert.Equal(string.Empty, record.LinkedTransactionId);
        Assert.Equal(-1200.01m, result.Records[1].Amount);
        Assert.Equal(record.Id, result.Records[1].Id);
    }

    [Fact]
    public void Parse_SharedStringRepresentation_PreservesTextExactly()
    {
        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(sharedStrings: true));

        EmmaTransactionRecord record = Assert.Single(result.Records);
        Assert.True(result.IsValid);
        Assert.Equal("synthetic-1", record.Id);
        Assert.Equal("Fictional custom name", record.CustomName);
        Assert.Equal("Synthetic notes", record.Notes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Parse_DeclaredUnicodeXmlEncoding_PreservesNonAsciiText(bool utf16)
    {
        object?[] row = TestWorkbookFactory.Row(id: "synthetic-£-é-東京");
        row[14] = "Fictional café — £12.34";
        byte[] workbook = TestWorkbookFactory.Create(rows: [row], sharedStrings: true);
        if (utf16) workbook = TestWorkbookFactory.ConvertWorksheetToUtf16(workbook);

        EmmaImportResult result = EmmaXlsxImporter.Parse(workbook);

        EmmaTransactionRecord record = Assert.Single(result.Records);
        Assert.True(result.IsValid);
        Assert.Equal("synthetic-£-é-東京", record.Id);
        Assert.Equal("Fictional café — £12.34", record.Notes);
    }

    [Theory]
    [InlineData("Purchase")]
    [InlineData("Direct Debit")]
    [InlineData("Standing Order")]
    [InlineData("Bill Payment")]
    public void Parse_RecognisedNegativeSpendingType_ClassifiesPurchaseCandidate(string type)
    {
        EmmaTransactionRecord record = Parse(TestWorkbookFactory.Row(type: type, amount: -1.23m));

        Assert.Equal(ImportClassification.PurchaseCandidate, record.Classification);
        Assert.Empty(record.Reviews);
        Assert.Equal(type, record.Type);
    }

    [Fact]
    public void Parse_PositiveLinkedCredit_ClassifiesRefundCandidate()
    {
        EmmaTransactionRecord record = Parse(TestWorkbookFactory.Row(type: "Credit", amount: 12.34m, linked: "purchase-1"));

        Assert.Equal(ImportClassification.RefundCandidate, record.Classification);
        Assert.Empty(record.Reviews);
        Assert.Equal("purchase-1", record.LinkedTransactionId);
    }

    [Theory]
    [InlineData(-10, "")]
    [InlineData(10, "purchase-1")]
    public void Parse_Transfer_IsExcludedFromPurchaseAllocation(decimal amount, string linked)
    {
        EmmaTransactionRecord record = Parse(TestWorkbookFactory.Row(type: "Transfer", amount: amount, linked: linked));

        Assert.Equal(ImportClassification.ExcludedTransfer, record.Classification);
        Assert.Empty(record.Reviews);
    }

    [Theory]
    [InlineData("Unknown", -10, "", ImportReviewReason.SourceTypeRequiresReview)]
    [InlineData("Cash Withdrawal", -10, "", ImportReviewReason.SourceTypeRequiresReview)]
    [InlineData("Purchase", 10, "", ImportReviewReason.ContradictoryAmountDirection)]
    [InlineData("Purchase", 0, "", ImportReviewReason.ContradictoryAmountDirection)]
    [InlineData("Credit", -10, "purchase-1", ImportReviewReason.ContradictoryAmountDirection)]
    [InlineData("Credit", 10, "", ImportReviewReason.UnlinkedCredit)]
    [InlineData("purchase", -10, "", ImportReviewReason.SourceTypeRequiresReview)]
    public void Parse_AmbiguousOrUnfamiliarRow_PreservesValuesAndRequiresReview(
        string type, decimal amount, string linked, ImportReviewReason reason)
    {
        EmmaTransactionRecord record = Parse(TestWorkbookFactory.Row(type: type, amount: amount, linked: linked));

        Assert.Equal(type, record.Type);
        Assert.Equal(amount, record.Amount);
        Assert.Equal(ImportClassification.ReviewRequired, record.Classification);
        Assert.Equal([reason], record.Reviews);
    }

    [Fact]
    public void Parse_NonGbpRow_RequiresActualGbpWithoutConversion()
    {
        EmmaTransactionRecord record = Parse(TestWorkbookFactory.Row(currency: "EUR", amount: -9.99m));

        Assert.Equal("EUR", record.Currency);
        Assert.Equal(-9.99m, record.Amount);
        Assert.Equal(ImportClassification.PurchaseCandidate, record.Classification);
        Assert.Equal([ImportReviewReason.ActualGbpRequired], record.Reviews);
    }

    [Fact]
    public void Parse_AmbiguousNonGbpRow_RequiresBothSemanticAndValuationReview()
    {
        EmmaTransactionRecord record = Parse(TestWorkbookFactory.Row(type: "Future Type", currency: "EUR", amount: -9.99m));

        Assert.Equal(ImportClassification.ReviewRequired, record.Classification);
        Assert.Equal([ImportReviewReason.SourceTypeRequiresReview, ImportReviewReason.ActualGbpRequired], record.Reviews);
    }

    [Fact]
    public void Parse_NonGbpTransfer_RemainsExcludedWithoutValuationReview()
    {
        EmmaTransactionRecord record = Parse(TestWorkbookFactory.Row(type: "Transfer", currency: "EUR", amount: -9.99m));

        Assert.Equal(ImportClassification.ExcludedTransfer, record.Classification);
        Assert.Empty(record.Reviews);
    }

    [Fact]
    public void Parse_InvalidHeaders_ReturnsActionableErrorsAndNoPartialRecords()
    {
        string[] swapped = [.. TestWorkbookFactory.Headers];
        (swapped[0], swapped[1]) = (swapped[1], swapped[0]);

        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(headers: swapped));

        Assert.False(result.IsValid);
        Assert.Empty(result.Records);
        Assert.Contains(result.Issues, issue => issue.Code == "HEADER_MISMATCH" && issue.Row == 1 && issue.Field == "ID");
        Assert.Contains(result.Issues, issue => issue.Code == "HEADER_MISMATCH" && issue.Field == "Date");
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(1, "28/02/2026")]
    [InlineData(3, " ")]
    [InlineData(5, "gbp")]
    [InlineData(8, "")]
    public void Parse_InvalidRequiredFields_ReturnsActionableErrors(int column, string value)
    {
        object?[] row = TestWorkbookFactory.Row();
        row[column] = value;

        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(rows: [row]));

        Assert.False(result.IsValid);
        Assert.Empty(result.Records);
        Assert.Contains(result.Issues, issue => issue.Row == 2);
    }

    [Fact]
    public void Parse_TextAmountAndNumericDate_ReturnsCellRepresentationErrors()
    {
        object?[] row = TestWorkbookFactory.Row();
        row[1] = 20260228m;
        row[2] = TestWorkbookFactory.NumberText("-12.34");

        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(rows: [row]));

        Assert.False(result.IsValid);
        Assert.Empty(result.Records);
        Assert.Contains(result.Issues, issue => issue.Code == "TEXT_CELL_INVALID" && issue.Field == "Date");
        Assert.Contains(result.Issues, issue => issue.Code == "AMOUNT_INVALID" && issue.Field == "Amount");
    }

    [Theory]
    [InlineData("0.12345678901234567890123456789")]
    [InlineData("79228162514264337593543950335.1")]
    [InlineData("1e2")]
    [InlineData("12,34")]
    public void Parse_AmountThatCannotBeRepresentedExactly_IsRejected(string sourceAmount)
    {
        object?[] row = TestWorkbookFactory.Row();
        row[2] = TestWorkbookFactory.Number(sourceAmount);

        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(rows: [row]));

        Assert.False(result.IsValid);
        Assert.Empty(result.Records);
        Assert.Contains(result.Issues, issue => issue.Code == "AMOUNT_INVALID" && issue.Field == "Amount");
    }

    [Theory]
    [InlineData("+0012.3400", "12.34")]
    [InlineData("-0.000", "0")]
    [InlineData("79228162514264337593543950335", "79228162514264337593543950335")]
    public void Parse_ExactlyRepresentableInvariantAmount_IsAccepted(string sourceAmount, string expectedText)
    {
        object?[] row = TestWorkbookFactory.Row(type: "Unknown");
        row[2] = TestWorkbookFactory.Number(sourceAmount);

        EmmaTransactionRecord record = Parse(row);

        Assert.Equal(decimal.Parse(expectedText, System.Globalization.CultureInfo.InvariantCulture), record.Amount);
    }

    [Fact]
    public void Parse_OneInvalidDataRow_RejectsAllRowsAtomically()
    {
        object?[] invalid = TestWorkbookFactory.Row(id: "bad-row");
        invalid[2] = TestWorkbookFactory.NumberText("-4.50");

        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(rows: [TestWorkbookFactory.Row(), invalid]));

        Assert.False(result.IsValid);
        Assert.Empty(result.Records);
        Assert.Contains(result.Issues, issue => issue.Code == "AMOUNT_INVALID" && issue.Row == 3 && issue.Field == "Amount");
    }

    [Fact]
    public void Parse_FormulaCell_ReturnsUnsupportedFeatureError()
    {
        object?[] row = TestWorkbookFactory.Row();
        row[2] = TestWorkbookFactory.Formula("1+1", 2m);

        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(rows: [row]));

        Assert.False(result.IsValid);
        Assert.Empty(result.Records);
        Assert.Contains(result.Issues, issue => issue.Code == "FORMULA_NOT_SUPPORTED");
    }

    [Fact]
    public void Parse_HiddenOrAdditionalWorksheet_ReturnsUnsupportedStructureErrors()
    {
        byte[] hidden = TestWorkbookFactory.Create(configure: (_, _, _, sheet) => sheet.State = SheetStateValues.Hidden);
        byte[] additional = TestWorkbookFactory.Create(configure: (_, workbook, _, _) =>
        {
            WorksheetPart second = workbook.AddNewPart<WorksheetPart>();
            second.Worksheet = new Worksheet(new SheetData());
            Sheets sheets = workbook.Workbook?.Sheets ?? throw new InvalidOperationException("Missing test worksheet collection.");
            sheets.Append(new Sheet
            {
                Id = workbook.GetIdOfPart(second),
                SheetId = 2,
                Name = "Other"
            });
        });

        EmmaImportResult hiddenResult = EmmaXlsxImporter.Parse(hidden);
        EmmaImportResult additionalResult = EmmaXlsxImporter.Parse(additional);

        Assert.Contains(hiddenResult.Issues, issue => issue.Code == "HIDDEN_CONTENT_PRESENT");
        Assert.Contains(additionalResult.Issues, issue => issue.Code == "WORKSHEET_AMBIGUOUS");
        Assert.Empty(hiddenResult.Records);
        Assert.Empty(additionalResult.Records);
    }

    [Fact]
    public void Parse_1904DateSystem_ReturnsUnsupportedDateSystemError()
    {
        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(date1904: true));

        Assert.False(result.IsValid);
        Assert.Empty(result.Records);
        Assert.Contains(result.Issues, issue => issue.Code == "DATE_SYSTEM_UNSUPPORTED");
    }

    [Fact]
    public void Parse_SourceRowLimitCountsEmptyAndInvalidRows()
    {
        IReadOnlyList<object?> empty = [];
        IReadOnlyList<IReadOnlyList<object?>> rows = [.. Enumerable.Repeat(empty, EmmaXlsxImporter.MaximumRows + 1)];

        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(rows: rows));

        Assert.False(result.IsValid);
        Assert.Empty(result.Records);
        Assert.Contains(result.Issues, issue => issue.Code == "WORKBOOK_LIMIT_EXCEEDED");
    }

    [Fact]
    public void Parse_PackageEntryLimitRejectsBeforeWorkbookTraversal()
    {
        byte[] package = TestWorkbookFactory.CreatePackageWithEntries(2_049);

        EmmaImportResult result = EmmaXlsxImporter.Parse(package);

        Assert.Equal("WORKBOOK_LIMIT_EXCEEDED", Assert.Single(result.Issues).Code);
        Assert.Empty(result.Records);
    }

    [Fact]
    public void Parse_DefinedNameExternalRelationshipOrImagePart_IsRejected()
    {
        byte[] definedName = TestWorkbookFactory.Create(configure: (_, workbook, _, _) =>
            workbook.Workbook?.Append(new DefinedNames(new DefinedName { Name = "SyntheticName", Text = "Transactions!$A$1" })));
        byte[] external = TestWorkbookFactory.Create(configure: (_, workbook, _, _) =>
            workbook.AddExternalRelationship(
                "http://schemas.openxmlformats.org/officeDocument/2006/relationships/externalLink",
                new Uri("https://example.invalid/synthetic", UriKind.Absolute)));
        byte[] image = TestWorkbookFactory.Create(configure: (_, _, worksheet, _) =>
        {
            ImagePart part = worksheet.AddImagePart(ImagePartType.Png);
            part.FeedData(new MemoryStream([0x89, 0x50, 0x4e, 0x47]));
        });

        EmmaImportResult namedResult = EmmaXlsxImporter.Parse(definedName);
        EmmaImportResult externalResult = EmmaXlsxImporter.Parse(external);
        EmmaImportResult imageResult = EmmaXlsxImporter.Parse(image);

        Assert.Contains(namedResult.Issues, issue => issue.Code == "WORKBOOK_FEATURE_UNSUPPORTED");
        Assert.Contains(externalResult.Issues, issue => issue.Code == "EXTERNAL_RELATIONSHIP_PRESENT");
        Assert.Contains(imageResult.Issues, issue => issue.Code == "WORKBOOK_FEATURE_UNSUPPORTED");
        Assert.Empty(namedResult.Records);
        Assert.Empty(externalResult.Records);
        Assert.Empty(imageResult.Records);
    }

    [Fact]
    public void Parse_InvalidSharedStringIndexOrStyleIndex_IsRejected()
    {
        byte[] sharedString = TestWorkbookFactory.Create(sharedStrings: true, configure: (_, _, worksheet, _) =>
            worksheet.Worksheet?.Descendants<Cell>().First().CellValue = new CellValue("999999"));
        byte[] style = TestWorkbookFactory.Create(configure: (_, _, worksheet, _) =>
            worksheet.Worksheet?.Descendants<Cell>().Single(cell => cell.CellReference?.Value == "C2").StyleIndex = 999999);

        EmmaImportResult sharedStringResult = EmmaXlsxImporter.Parse(sharedString);
        EmmaImportResult styleResult = EmmaXlsxImporter.Parse(style);

        Assert.False(sharedStringResult.IsValid);
        Assert.False(styleResult.IsValid);
        Assert.Empty(sharedStringResult.Records);
        Assert.Empty(styleResult.Records);
    }

    [Fact]
    public void Parse_LargeValidCellFormatIndex_IsResolvedFromBoundedIndex()
    {
        const uint formatIndex = 4_095;
        byte[] workbook = TestWorkbookFactory.Create(configure: (_, workbookPart, worksheet, _) =>
        {
            CellFormats formats = new();
            for (uint index = 0; index <= formatIndex; index++) formats.Append(new CellFormat { NumberFormatId = 0 });
            formats.Count = formatIndex + 1;
            workbookPart.WorkbookStylesPart!.Stylesheet!.CellFormats = formats;
            worksheet.Worksheet?.Descendants<Cell>().Single(cell => cell.CellReference?.Value == "C2").StyleIndex = formatIndex;
        });

        EmmaImportResult result = EmmaXlsxImporter.Parse(workbook);

        Assert.True(result.IsValid);
        Assert.Equal(-12.34m, Assert.Single(result.Records).Amount);
    }

    [Fact]
    public void Parse_MoreThanContractColumnsProducesOneBoundedDiagnostic()
    {
        object?[] row = [.. TestWorkbookFactory.Row(), "synthetic extra cell"];

        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(rows: [row]));

        ImportIssue issue = Assert.Single(result.Issues);
        Assert.Equal("COLUMN_OUT_OF_RANGE", issue.Code);
        Assert.Empty(result.Records);
    }

    [Fact]
    public void Parse_DecompressionLimitRejectsBeforeWorkbookTraversal()
    {
        byte[] package = TestWorkbookFactory.CreatePackageWithUncompressedEntry(EmmaXlsxImporter.MaximumUncompressedBytes + 1);

        EmmaImportResult result = EmmaXlsxImporter.Parse(package);

        Assert.Equal("WORKBOOK_LIMIT_EXCEEDED", Assert.Single(result.Issues).Code);
        Assert.Empty(result.Records);
    }

    [Fact]
    public void Parse_CalculationChainEmbeddedPackageOrMacro_IsRejected()
    {
        byte[] calculationChain = TestWorkbookFactory.Create(configure: (_, workbook, _, _) =>
        {
            CalculationChainPart part = workbook.AddNewPart<CalculationChainPart>();
            part.CalculationChain = new CalculationChain();
        });
        byte[] embedded = TestWorkbookFactory.Create(configure: (_, _, worksheet, _) =>
        {
            EmbeddedPackagePart part = worksheet.AddEmbeddedPackagePart("application/octet-stream");
            part.FeedData(new MemoryStream([1, 2, 3, 4]));
        });
        byte[] macro = TestWorkbookFactory.Create(documentType: SpreadsheetDocumentType.MacroEnabledWorkbook,
            configure: (_, workbook, _, _) =>
            {
                VbaProjectPart part = workbook.AddNewPart<VbaProjectPart>();
                part.FeedData(new MemoryStream([1, 2, 3, 4]));
            });

        EmmaImportResult chainResult = EmmaXlsxImporter.Parse(calculationChain);
        EmmaImportResult embeddedResult = EmmaXlsxImporter.Parse(embedded);
        EmmaImportResult macroResult = EmmaXlsxImporter.Parse(macro);

        Assert.Contains(chainResult.Issues, issue => issue.Code == "WORKBOOK_FEATURE_UNSUPPORTED");
        Assert.Contains(embeddedResult.Issues, issue => issue.Code == "EMBEDDED_CONTENT_PRESENT");
        Assert.Contains(macroResult.Issues, issue => issue.Code is "MACROS_NOT_SUPPORTED" or "WORKBOOK_TYPE_UNSUPPORTED");
        Assert.Empty(chainResult.Records);
        Assert.Empty(embeddedResult.Records);
        Assert.Empty(macroResult.Records);
    }

    [Fact]
    public void Parse_MalformedOrOversizedInput_ReturnsErrorsWithoutRecords()
    {
        EmmaImportResult malformed = EmmaXlsxImporter.Parse("not an xlsx"u8.ToArray());
        EmmaImportResult oversized = EmmaXlsxImporter.Parse(new byte[EmmaXlsxImporter.MaximumInputBytes + 1]);

        Assert.Equal("WORKBOOK_UNREADABLE", Assert.Single(malformed.Issues).Code);
        Assert.Equal("INPUT_TOO_LARGE", Assert.Single(oversized.Issues).Code);
        Assert.Empty(malformed.Records);
        Assert.Empty(oversized.Records);
    }

    private static EmmaTransactionRecord Parse(IReadOnlyList<object?> row)
    {
        EmmaImportResult result = EmmaXlsxImporter.Parse(TestWorkbookFactory.Create(rows: [row]));
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Issues.Select(issue => $"{issue.Code}: {issue.Message}")));
        return Assert.Single(result.Records);
    }
}