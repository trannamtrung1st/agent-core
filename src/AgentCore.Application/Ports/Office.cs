namespace AgentCore.Application.Ports;

public static class OfficeLimits
{
    public const int MaxPackageBytes = 10 * 1024 * 1024;
    public const int MaxExpandedBytes = 32 * 1024 * 1024;
    public const int MaxEntries = 512;
    public const int MaxOperations = 20;
    public const int MaxSheets = 50;
    public const int MaxRows = 200;
    public const int MaxColumns = 64;
    public const int MaxCells = 50000;
    public const int MaxDocumentParagraphs = 2000;
    public const int MaxDocumentCells = 10000;
    public const int MaxDocumentImages = 32;
    public const int MaxDocumentPageBreaks = 200;
    public const long MaxDocumentImagePixels = 40_000_000;
    public const string DocumentMime = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const string SpreadsheetMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
}
public sealed record OfficeSection(string? Heading, IReadOnlyList<string> Paragraphs, IReadOnlyList<IReadOnlyList<string>>? Table, bool PageBreakBefore = false);
public sealed record OfficeDocumentSpec(string? Title, IReadOnlyList<OfficeSection> Sections);
public sealed record OfficeCell(string Type, string? Text = null, double? Number = null, bool? Boolean = null, string? Formula = null, string? Format = null);
public sealed record OfficeSheet(string Name, IReadOnlyList<IReadOnlyList<OfficeCell>> Rows);
public sealed record OfficeWorkbookSpec(IReadOnlyList<OfficeSheet> Sheets);
public sealed record OfficeDocumentElement(string Selector, string Kind, string Text, IReadOnlyList<IReadOnlyList<string>>? Rows = null);
public sealed record OfficeDocumentInspection(string Sha256, IReadOnlyList<OfficeDocumentElement> Elements, int TotalElements, bool Truncated, IReadOnlyList<string> Warnings);
public sealed record OfficeDocumentEdit(string Operation, string Selector, string? Text = null, string? Style = null);
public sealed record OfficeSpreadsheetEdit(string Operation, string Sheet, string? Range = null, IReadOnlyList<IReadOnlyList<OfficeCell>>? Cells = null, string? Name = null, string? Formula = null, string? Format = null);
public sealed record OfficeCellView(string Address, string Type, string Value, string? Formula, string ValueStatus, string NumberFormat);
public sealed record OfficeWorkbookInspection(string Sha256, IReadOnlyList<string> Sheets, string Sheet, string Range, IReadOnlyList<IReadOnlyList<OfficeCellView>> Rows, IReadOnlyList<string> Warnings);
public sealed record OfficeProcessedFile(byte[] Bytes, IReadOnlyList<string> Warnings);

/// <summary>Bounded semantic OOXML processing. Caller owns all source authorization and guarded storage commits.</summary>
public interface IOfficeProcessor
{
    OfficeProcessedFile CreateDocument(OfficeDocumentSpec spec, CancellationToken ct);
    OfficeDocumentInspection InspectDocument(ReadOnlyMemory<byte> bytes, int offset, int limit, CancellationToken ct);
    OfficeProcessedFile EditDocument(ReadOnlyMemory<byte> bytes, IReadOnlyList<OfficeDocumentEdit> operations, CancellationToken ct);
    OfficeProcessedFile CreateWorkbook(OfficeWorkbookSpec spec, CancellationToken ct);
    OfficeWorkbookInspection InspectWorkbook(ReadOnlyMemory<byte> bytes, string? sheet, string? range, CancellationToken ct);
    OfficeProcessedFile EditWorkbook(ReadOnlyMemory<byte> bytes, IReadOnlyList<OfficeSpreadsheetEdit> operations, CancellationToken ct);
}
