namespace DotFinance.ImportInspector;

public sealed record InspectionReport(
    string SchemaVersion,
    string Status,
    WorkbookProfile Workbook,
    IReadOnlyList<HeaderProfile> Headers,
    IReadOnlyList<ColumnProfile> Columns,
    IReadOnlyList<Finding> Findings);

public sealed record WorkbookProfile(string Format, string WorksheetSelection, string DateSystem);

public sealed record HeaderProfile(string Token, int Position, string Classification);

public sealed record ColumnProfile(
    string HeaderToken,
    int Position,
    IReadOnlyList<string> CellKinds,
    IReadOnlyList<string> NumberFormats,
    IReadOnlyList<string> TextSyntaxes);

public sealed record Finding(string Severity, string Code, string Message, int? Column = null);

public sealed record InspectionResult(InspectionReport Report, byte[]? SyntheticWorkbook);