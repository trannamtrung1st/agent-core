using System.Globalization;
using AgentCore.Application.Office;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using ClosedXML.Excel;
using ExcelNumberFormat;

namespace AgentCore.Infrastructure.Office;

public sealed partial class OfficeProcessor
{
    public OfficeSparseWorkbookInspection ReadWorkbookContent(ReadOnlyMemory<byte> bytes, string? sheet, string? cursor, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            var result = ReadWorkbookContentCore(bytes, sheet, cursor, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            return result;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new AgentCoreException("office_processing_limit", "Sparse reading exceeded its cooperative processing deadline. No reliable continuation is available.", 400); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException or FormatException or System.Xml.XmlException or DocumentFormat.OpenXml.Packaging.OpenXmlPackageException)
        { throw AgentCoreErrors.Validation("invalid_office_package: The workbook is malformed or invalid."); }
    }

    private static OfficeSparseWorkbookInspection ReadWorkbookContentCore(ReadOnlyMemory<byte> bytes, string? sheet, string? cursor, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var sha256 = Hash(bytes);
        var continuation = OfficeSparseCursor.Decode(cursor, sha256, sheet);
        var scans = OfficePackageGuard.ValidateSparseWorkbook(bytes, ct);
        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var workbook = new XLWorkbook(stream);
        ct.ThrowIfCancellationRequested(); // ClosedXML's synchronous load is bounded by the package scan, but cannot be interrupted.
        var sheets = workbook.Worksheets.OrderBy(s => s.Position).ToArray();
        var ordinal = continuation?.SheetOrdinal ?? (sheet is null ? 1 : Array.FindIndex(sheets, s => s.Name == sheet) + 1);
        if (ordinal < 1 || ordinal > sheets.Length)
            throw AgentCoreErrors.Validation("office_operation_unsupported: Worksheet was not found.");
        var selected = sheets[ordinal - 1];
        if (continuation is not null && (continuation.Sheet != selected.Name || sheet is not null && sheet != selected.Name))
            throw new AgentCoreException("office_cursor_invalid", "Cursor worksheet identity does not match the source.", 400);
        if (!scans.TryGetValue(selected.Name, out var scan))
            throw AgentCoreErrors.Validation("invalid_office_package: Worksheet scan identity was not found.");

        // Contents excludes style-only cells and preserves formulas. ClosedXML considers explicit empty text empty;
        // the existing safety scan supplies bounded text-address hints, while ClosedXML still interprets all values.
        var content = new Dictionary<string, IXLCell>(StringComparer.Ordinal);
        foreach (var cell in selected.CellsUsed(XLCellsUsedOptions.Contents))
        {
            ct.ThrowIfCancellationRequested();
            content.Add(cell.Address.ToString()!, cell);
            if (content.Count > OfficeLimits.MaxCells) throw new AgentCoreException("office_processing_limit", "Too many materialized populated cells.", 400);
        }
        foreach (var address in scan.TextAddresses)
        {
            ct.ThrowIfCancellationRequested();
            // Validate a single coordinate before passing untrusted source text to ClosedXML.
            Range(address);
            var cell = selected.Cell(address);
            content.TryAdd(cell.Address.ToString()!, cell);
            if (content.Count > OfficeLimits.MaxCells) throw new AgentCoreException("office_processing_limit", "Too many materialized populated cells.", 400);
        }
        var ordered = content.Values.OrderBy(c => c.Address.RowNumber).ThenBy(c => c.Address.ColumnNumber).ToArray();
        var offset = 0;
        if (continuation?.After is string after)
        {
            var index = Array.FindIndex(ordered, c => c.Address.ToString() == after);
            if (index < 0) throw new AgentCoreException("office_cursor_invalid", "Cursor cell was not found in this worksheet.", 400);
            offset = index + 1;
        }
        var cells = new List<OfficeCellView>();
        foreach (var cell in ordered.Skip(offset).Take(OfficeLimits.MaxSparsePageCells))
        {
            ct.ThrowIfCancellationRequested();
            var cached = cell.CachedValue;
            var formula = cell.HasFormula ? cell.FormulaA1 : null;
            if (formula is not null) OfficePackageGuard.RequireLocalFormula(formula);
            var format = cell.Style.NumberFormat.Format;
            object? value = cached.Type switch
            {
                XLDataType.Number => cached.GetNumber(), XLDataType.DateTime => cached.GetDateTime(),
                XLDataType.Boolean => cached.GetBoolean(), XLDataType.Text => cached.GetText(),
                _ => cached.ToString(CultureInfo.InvariantCulture)
            };
            string display;
            try { display = new NumberFormat(string.IsNullOrEmpty(format) ? PreviewNumberFormat(cell.Style.NumberFormat.NumberFormatId) : format).Format(value, CultureInfo.InvariantCulture); }
            catch { display = Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; }
            var state = formula is null ? "value" : cached.IsBlank || scan.MissingFormulaCaches.Contains(cell.Address.ToString()!) ? "cached_value_unavailable" : cell.NeedsRecalculation ? "cached_value_stale" : "cached_value_available";
            cells.Add(new(cell.Address.ToString()!, formula is null && cached.IsBlank ? "Text" : cached.Type.ToString(), display, formula, state,
                string.IsNullOrEmpty(format) ? cell.Style.NumberFormat.NumberFormatId.ToString(CultureInfo.InvariantCulture) : format));
        }
        return new(sha256, sheets.Select(s => s.Name).ToArray(), selected.Name, ordinal, sheet, cells,
            offset + cells.Count < ordered.Length, scan.StoredCells > 0, offset == 0, WorkbookWarnings, scan.HasFormatting);
    }
}
