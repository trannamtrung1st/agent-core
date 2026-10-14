using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Office;

/// <summary>Preserve complete cells and continuation evidence when a read must fit a context budget.</summary>
public static class OfficeReadResults
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string Document(OfficeDocumentInspection source, int offset, int budget, Func<object, string>? envelope = null)
    {
        envelope ??= page => JsonSerializer.Serialize(page, Json);
        var count = source.Elements.Count;
        while (true)
        {
            var truncated = offset + count < source.TotalElements;
            if (count == 0 && truncated) return OutputLimit(budget, "A complete document element exceeds the inspection text budget.");
            var page = new
            {
                source.Sha256, offset, source.TotalElements, truncated,
                nextOffset = truncated ? (int?)(offset + count) : null, source.Warnings,
                elements = source.Elements.Take(count).ToArray()
            };
            var json = envelope(page);
            if (Encoding.UTF8.GetByteCount(json) <= budget) return json;
            if (count <= 1) return OutputLimit(budget, "Complete document elements or metadata exceed the read budget. Request a smaller element with a larger output budget.");
            count = Math.Max(1, count / 2);
        }
    }

    public static string Workbook(OfficeWorkbookInspection source, int budget, Func<object, string>? envelope = null)
    {
        envelope ??= page => JsonSerializer.Serialize(page, Json);
        var count = source.Rows.Count;
        while (true)
        {
            var rows = source.Rows.Take(count).ToArray();
            var extent = source.SheetExtents?.FirstOrDefault(s => s.Name == source.Sheet);
            var first = rows.FirstOrDefault()?.FirstOrDefault()?.Address;
            var last = rows.LastOrDefault()?.LastOrDefault()?.Address;
            var usedRange = extent is { LastRow: > 0, LastColumn: > 0 }
                ? $"{Address(extent.FirstRow, extent.FirstColumn)}:{Address(extent.LastRow, extent.LastColumn)}" : null;
            var returnedRange = first is null ? null : $"{first}:{last}";
            string? nextRange = null;
            if (extent is not null && first is not null && last is not null)
            {
                var lastRow = Row(last);
                var firstColumn = Column(first);
                var lastColumn = Column(last);
                if (lastRow < extent.LastRow)
                    nextRange = $"{Address(lastRow + 1, firstColumn)}:{Address(Math.Min(extent.LastRow, lastRow + OfficeLimits.MaxRows), lastColumn)}";
                else if (lastColumn < extent.LastColumn)
                    nextRange = $"{Address(extent.FirstRow, lastColumn + 1)}:{Address(Math.Min(extent.LastRow, extent.FirstRow + OfficeLimits.MaxRows - 1), Math.Min(extent.LastColumn, lastColumn + OfficeLimits.MaxColumns))}";
            }
            var page = new
            {
                mode = "range", source.Sha256, source.Sheets, source.Sheet, source.SheetExtents,
                source.Range, usedRange, returnedRange,
                truncated = count < source.Rows.Count || extent is { LastRow: > 0 } &&
                    (first is null || Row(first) > extent.FirstRow || Column(first) > extent.FirstColumn || Row(last!) < extent.LastRow || Column(last!) < extent.LastColumn),
                nextRange, source.Warnings, rows
            };
            var json = envelope(page);
            if (Encoding.UTF8.GetByteCount(json) <= budget) return json;
            // No cell or metadata strings are sliced; callers can request a narrower column range.
            if (count <= 1) return OutputLimit(budget, "Complete rows or metadata exceed the read budget. Request fewer columns or individual cells with a larger output budget.");
            count = Math.Max(1, count / 2);
        }
    }

    public static string SparseWorkbook(OfficeSparseWorkbookInspection source, int budget, Func<object, string>? envelope = null)
    {
        envelope ??= page => JsonSerializer.Serialize(page, Json);
        var count = source.Cells.Count;
        while (true)
        {
            var cells = source.Cells.Take(count).ToArray();
            var sheetHasMore = count < source.Cells.Count || source.SheetHasMore;
            var hasMore = sheetHasMore || source.SelectedSheet is null && source.SheetOrdinal < source.Sheets.Count;
            var nextCursor = sheetHasMore && count > 0
                ? OfficeSparseCursor.Encode(source.Sha256, source.SelectedSheet, source.SheetOrdinal, source.Sheet, cells[^1].Address)
                : hasMore && !sheetHasMore
                    ? OfficeSparseCursor.Encode(source.Sha256, source.SelectedSheet, source.SheetOrdinal + 1, source.Sheets[source.SheetOrdinal], null)
                    : null;
            var sheetCoverage = !sheetHasMore ? "complete" : "partial";
            var coverage = !hasMore ? "complete" : "partial";
            var page = new
            {
                mode = "sparse", source.Sha256, source.Sheets, source.Sheet, source.SheetOrdinal, source.SelectedSheet,
                populatedCellsReturned = count, cells, sheetHasMore, hasMore, nextCursor,
                coverage, sheetCoverage, coverageScope = source.SelectedSheet is null ? "workbook_traversal" : "selected_sheet_traversal",
                pageCoverage = !sheetHasMore && source.StartedAtBeginning ? "complete_sheet" : "partial_sheet", traversalComplete = !hasMore, source.StartedAtBeginning,
                sheetState = source.Cells.Count > 0 || !source.StartedAtBeginning ? "populated" : source.HasStoredCells || source.HasFormatting ? "formatting_only" : "empty",
                source.HasStoredCells, source.HasFormatting, source.Warnings
            };
            var json = envelope(page);
            if (Encoding.UTF8.GetByteCount(json) <= budget) return json;
            if (count <= 1) return OutputLimit(budget, "A complete populated cell or worksheet metadata exceeds the read budget. No continuation was issued; retry with a larger output budget.");
            count = Math.Max(1, count / 2);
        }
    }

    private static string OutputLimit(int budget, string message) => ToolJsonResults.FitToBudget(budget,
        JsonSerializer.Serialize(new { error = "office_output_limit", message }, Json));

    public static string Address(int row, int column)
    {
        var letters = "";
        while (column > 0) { column--; letters = (char)('A' + column % 26) + letters; column /= 26; }
        return letters + row;
    }
    private static int Row(string address) => int.Parse(new string(address.Where(char.IsDigit).ToArray()), System.Globalization.CultureInfo.InvariantCulture);
    private static int Column(string address)
    {
        var column = 0;
        foreach (var letter in address.TakeWhile(char.IsLetter)) column = column * 26 + char.ToUpperInvariant(letter) - 'A' + 1;
        return column;
    }
}
