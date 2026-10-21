using AgentCore.Api.Http;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Contracts.Http;

namespace AgentCore.Api.Mapping;

internal static class OfficePreviewMapping
{
    internal static IResult Problem(AgentCoreException ex) =>
        ex.Message.StartsWith("invalid_office_package:", StringComparison.Ordinal) || ex.Message.StartsWith("office_operation_unsupported:", StringComparison.Ordinal)
            ? Results.Problem(statusCode: 400, title: ex.Message.Split(':')[0], detail: "This Office file or operation could not be interpreted safely. Download the original or choose a smaller range where applicable.")
            : ProblemResults.From(ex);

    internal static IResult Project(string name, string mime, byte[] bytes, string? sheet, string? range, IOfficeProcessor office, FilePreviewOptions policy, CancellationToken ct, bool includeDocumentSource = false)
    {
        policy.RequireOfficeSize(name, mime, bytes.Length);
        if (mime == OfficeLimits.DocumentMime && name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            var result = office.InspectDocument(bytes, 0, 1, ct);
            return Results.Json(new OfficePreviewResponse("docx", result.Sha256, result.Warnings, EmptyBody: result.TotalElements == 0, SourceBase64: includeDocumentSource ? Convert.ToBase64String(bytes) : null));
        }
        if (mime == OfficeLimits.SpreadsheetMime && name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            range ??= $"A1:{ColumnName(policy.Spreadsheet.ColumnsPerWindow)}{policy.Spreadsheet.RowsPerWindow}";
            ValidateRange(range, policy.Spreadsheet);
            var result = office.PreviewWorkbook(bytes, sheet, range, ct);
            if (result.Rows.Sum(row => row.Sum(cell => cell.Value.Length + (cell.Formula?.Length ?? 0))) > policy.Spreadsheet.MaxWindowCharacters
                || result.Rows.Any(row => row.Any(cell => cell.Value.Length > policy.Spreadsheet.MaxCellCharacters)))
                throw new AgentCoreException("preview_structure_limit", "This range exceeds the preview text budget. Select a smaller range or download the original.", 413);
            return Results.Json(new OfficePreviewResponse("xlsx", result.Sha256, result.Warnings, Sheets: result.Sheets, Sheet: result.Sheet,
                Range: result.Range, Rows: result.Rows.Select(row => (IReadOnlyList<OfficeCellResponse>)row.Select(cell => new OfficeCellResponse(cell.Address, cell.Type, cell.Value, cell.Formula, cell.ValueStatus, cell.NumberFormat, MapStyle(cell.Style))).ToArray()).ToArray(), Presentation: MapPresentation(result.Presentation)));
        }
        return Results.Problem(statusCode: 400, title: "preview_unsupported", detail: "The filename and stored type do not match a supported Office file. Download the original.");
    }

    private static OfficeCellStyleResponse? MapStyle(OfficeCellStyle? s) => s is null ? null : new(s.FontFamily, s.FontSize, s.Bold, s.Italic, s.FontColor, s.FillColor,
        s.Horizontal, s.Vertical, s.Wrap, new(s.Top.Style, s.Top.Color), new(s.Right.Style, s.Right.Color), new(s.Bottom.Style, s.Bottom.Color), new(s.Left.Style, s.Left.Color));
    private static OfficeWorksheetResponse? MapPresentation(OfficeWorksheetView? s) => s is null ? null : new(s.FirstRow, s.FirstColumn, s.TotalRows, s.TotalColumns,
        s.RowDimensions.Select(d => new OfficeDimensionResponse(d.Pixels, d.Hidden)).ToArray(), s.ColumnDimensions.Select(d => new OfficeDimensionResponse(d.Pixels, d.Hidden)).ToArray(),
        s.Merges.Select(m => new OfficeMergeResponse(m.Row, m.Column, m.Rows, m.Columns, m.Clipped)).ToArray(), s.FrozenRows, s.FrozenColumns);
    internal static string ColumnName(int column)
    {
        var name = "";
        while (column > 0) { column--; name = (char)('A' + column % 26) + name; column /= 26; }
        return name;
    }
    private static void ValidateRange(string range, PreviewSpreadsheetLimits policy)
    {
        var match = System.Text.RegularExpressions.Regex.Match(range, @"^([A-Za-z]{1,3})([1-9][0-9]{0,6})(?::([A-Za-z]{1,3})([1-9][0-9]{0,6}))?$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (!match.Success) throw AgentCoreErrors.Validation("office_operation_unsupported: Use a concrete A1 cell/range.");
        static int Column(string name) => name.ToUpperInvariant().Aggregate(0, (value, ch) => value * 26 + ch - 'A' + 1);
        var rows = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) - int.Parse(match.Groups[2].Value) + 1 : 1;
        var columns = match.Groups[3].Success ? Column(match.Groups[3].Value) - Column(match.Groups[1].Value) + 1 : 1;
        if (rows < 1 || columns < 1 || rows > policy.RowsPerWindow || columns > policy.ColumnsPerWindow || (long)rows * columns > policy.MaxCellsPerWindow)
            throw new AgentCoreException("preview_structure_limit", "The selected range exceeds the configured preview window. Choose a smaller range or download the original.", 413);
    }
}
