namespace AgentCore.Contracts.Http;

public sealed record OfficeCellResponse(string Address, string Type, string Value, string? Formula, string ValueStatus, string NumberFormat, OfficeCellStyleResponse? Style = null);
public sealed record OfficePreviewResponse(string Format, string Sha256, IReadOnlyList<string> Warnings,
    IReadOnlyList<string>? Sheets = null, string? Sheet = null,
    string? Range = null, IReadOnlyList<IReadOnlyList<OfficeCellResponse>>? Rows = null, bool EmptyBody = false, OfficeWorksheetResponse? Presentation = null, string? SourceBase64 = null);

public sealed record OfficeBorderResponse(string Style, string? Color);
public sealed record OfficeCellStyleResponse(string FontFamily, double FontSize, bool Bold, bool Italic, string? FontColor, string? FillColor,
    string Horizontal, string Vertical, bool Wrap, OfficeBorderResponse Top, OfficeBorderResponse Right, OfficeBorderResponse Bottom, OfficeBorderResponse Left);
public sealed record OfficeMergeResponse(int Row, int Column, int Rows, int Columns, bool Clipped);
public sealed record OfficeDimensionResponse(double Pixels, bool Hidden);
public sealed record OfficeWorksheetResponse(int FirstRow, int FirstColumn, int TotalRows, int TotalColumns,
    IReadOnlyList<OfficeDimensionResponse> RowDimensions, IReadOnlyList<OfficeDimensionResponse> ColumnDimensions,
    IReadOnlyList<OfficeMergeResponse> Merges, int FrozenRows, int FrozenColumns);
