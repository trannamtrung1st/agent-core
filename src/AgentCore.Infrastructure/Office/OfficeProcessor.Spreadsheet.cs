using System.Globalization;
using System.Text.RegularExpressions;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using ClosedXML.Excel;
using ExcelNumberFormat;

namespace AgentCore.Infrastructure.Office;

public sealed partial class OfficeProcessor
{
    private static readonly string[] WorkbookWarnings = ["Formula values are cached/available values, never recalculated for inspection. Charts, pivots and other Office layout may be omitted from preview."];
    private static string Format(string? preset) => preset switch { null or "general" => "General", "text" => "@", "currency" => "$#,##0.00", "date" => "yyyy-mm-dd", "percentage" => "0.00%", _ => throw AgentCoreErrors.Validation("office_operation_unsupported: Use general, text, currency, date or percentage format.") };
    private static void SheetName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 31 || name.IndexOfAny(['[', ']', ':', '*', '?', '/', '\\']) >= 0 || name.Any(char.IsControl))
            throw AgentCoreErrors.Validation("office_operation_unsupported: Invalid worksheet name.");
    }
    private static void SetCell(IXLCell cell, OfficeCell input)
    {
        TextLimit(input.Text); TextLimit(input.Formula);
        if (input.Formula is not null)
        {
            if (input.Formula.Length > 1024 || input.Formula.Contains('[') || input.Formula.Contains(']') || input.Formula.Contains('|') || input.Formula.Contains('!') && input.Formula.Contains("://", StringComparison.Ordinal)
                || Regex.IsMatch(input.Formula, @"\b(WEBSERVICE|HYPERLINK|RTD|DDE|IMAGE|IMPORTXML|IMPORTDATA|CALL|REGISTER(?:\.ID)?|EXEC|RUN|EVALUATE|GET\.(CELL|WORKSPACE)|PY|SQL\.REQUEST|STOCKHISTORY)\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                throw AgentCoreErrors.Validation("office_operation_unsupported: External or active formulas are unsupported.");
            cell.FormulaA1 = input.Formula.TrimStart('=');
        }
        else cell.Value = input.Type switch
        {
            "text" => input.Text ?? "",
            "number" when input.Number is double number && double.IsFinite(number) => number,
            "boolean" when input.Boolean is bool boolean => boolean,
            "date" when DateTime.TryParseExact(input.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) => date,
            "blank" => Blank.Value,
            _ => throw AgentCoreErrors.Validation("office_operation_unsupported: Cell must be text, number, boolean, date or blank.")
        };
        cell.Style.NumberFormat.Format = Format(input.Format ?? (input.Type == "date" ? "date" : null));
    }
    private static void SetRows(IXLWorksheet sheet, int firstRow, int firstColumn, IReadOnlyList<IReadOnlyList<OfficeCell>> rows, CancellationToken ct)
    {
        if (rows.Count > OfficeLimits.MaxRows || rows.Any(row => row.Count > OfficeLimits.MaxColumns) || rows.Sum(row => row.Count) > OfficeLimits.MaxRows * OfficeLimits.MaxColumns)
            throw AgentCoreErrors.Validation("office_operation_unsupported: At most 200 rows by 64 columns per operation.");
        for (var r = 0; r < rows.Count; r++) for (var c = 0; c < rows[r].Count; c++) { ct.ThrowIfCancellationRequested(); SetCell(sheet.Cell(firstRow + r, firstColumn + c), rows[r][c]); }
    }
    private OfficeProcessedFile CreateWorkbookCore(OfficeWorkbookSpec spec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (spec.Sheets.Count is < 1 or > OfficeLimits.MaxSheets || spec.Sheets.Select(sheet => sheet.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != spec.Sheets.Count
            || spec.Sheets.Sum(sheet => sheet.Rows.Sum(row => row.Count)) > OfficeLimits.MaxCells)
            throw AgentCoreErrors.Validation("office_operation_unsupported: Supply unique sheets within workbook limits.");
        using var workbook = new XLWorkbook();
        foreach (var input in spec.Sheets)
        {
            ct.ThrowIfCancellationRequested(); SheetName(input.Name);
            var sheet = workbook.Worksheets.Add(input.Name); SetRows(sheet, 1, 1, input.Rows, ct);
            sheet.Columns(1, Math.Max(1, input.Rows.Select(row => row.Count).DefaultIfEmpty(0).Max())).Width = 18;
        }
        using var output = new MemoryStream(); workbook.SaveAs(output, new SaveOptions { EvaluateFormulasBeforeSaving = false, ValidatePackage = true });
        var bytes = output.ToArray(); OfficePackageGuard.Validate(bytes, "xl", ct); return new(bytes, WorkbookWarnings);
    }
    private static (int Row, int Column, int Rows, int Columns) Range(string? range)
    {
        var match = Regex.Match(range ?? "A1:BL200", @"^([A-Za-z]{1,3})([1-9][0-9]{0,6})(?::([A-Za-z]{1,3})([1-9][0-9]{0,6}))?$", RegexOptions.CultureInvariant);
        if (!match.Success) throw AgentCoreErrors.Validation("office_operation_unsupported: Use a concrete A1 cell/range.");
        static int Column(string text) { var result = 0; foreach (var character in text.ToUpperInvariant()) result = result * 26 + character - 'A' + 1; return result; }
        var column = Column(match.Groups[1].Value); var row = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var lastColumn = match.Groups[3].Success ? Column(match.Groups[3].Value) : column;
        var lastRow = match.Groups[4].Success ? int.Parse(match.Groups[4].Value, CultureInfo.InvariantCulture) : row;
        var rows = lastRow - row + 1; var columns = lastColumn - column + 1;
        if (rows is < 1 or > OfficeLimits.MaxRows || columns is < 1 or > OfficeLimits.MaxColumns || lastRow > 1048576 || lastColumn > 16384)
            throw AgentCoreErrors.Validation("office_operation_unsupported: Range must fit Excel bounds and 200 rows by 64 columns.");
        return (row, column, rows, columns);
    }
    private OfficeWorkbookInspection InspectWorkbookCore(ReadOnlyMemory<byte> bytes, string? sheet, string? range, CancellationToken ct)
    {
        OfficePackageGuard.Validate(bytes, "xl", ct);
        var bounds = Range(range);
        using var stream = new MemoryStream(bytes.ToArray(), false); using var workbook = new XLWorkbook(stream);
        if (workbook.Worksheets.Count > OfficeLimits.MaxSheets) throw AgentCoreErrors.Validation("office_operation_unsupported: Too many worksheets.");
        var selected = sheet is null ? workbook.Worksheets.First() : workbook.Worksheets.FirstOrDefault(s => s.Name == sheet) ?? throw AgentCoreErrors.Validation("office_operation_unsupported: Worksheet was not found.");
        var rows = new List<IReadOnlyList<OfficeCellView>>(); var totalChars = 0;
        var used = selected.RangeUsed(); var actualRows = used is null ? 0 : Math.Min(bounds.Rows, Math.Max(0, used.RangeAddress.LastAddress.RowNumber - bounds.Row + 1));
        var actualColumns = used is null ? 0 : Math.Min(bounds.Columns, Math.Max(0, used.RangeAddress.LastAddress.ColumnNumber - bounds.Column + 1));
        for (var r = 0; r < actualRows; r++)
        {
            var cells = new List<OfficeCellView>();
            for (var c = 0; c < actualColumns; c++)
            {
                ct.ThrowIfCancellationRequested(); var cell = selected.Cell(bounds.Row + r, bounds.Column + c); var cached = cell.CachedValue;
                var formula = cell.HasFormula ? cell.FormulaA1 : null; var format = cell.Style.NumberFormat.Format;
                // CachedValue avoids ClosedXML's lazy recalculation in Value/GetFormattedString.
                object? value = cached.Type switch { XLDataType.Number => cached.GetNumber(), XLDataType.DateTime => cached.GetDateTime(), XLDataType.Boolean => cached.GetBoolean(), XLDataType.Text => cached.GetText(), _ => cached.ToString(CultureInfo.InvariantCulture) };
                string display;
                try { display = new NumberFormat(string.IsNullOrEmpty(format) ? "General" : format).Format(value, CultureInfo.InvariantCulture); }
                catch { display = Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""; }
                totalChars += display.Length + (formula?.Length ?? 0);
                if (totalChars > 64000) throw AgentCoreErrors.Validation("office_operation_unsupported: The excerpt is too large. Request a smaller range.");
                var state = formula is null ? "value" : cached.IsBlank ? "cached_value_unavailable" : cell.NeedsRecalculation ? "cached_value_stale" : "cached_value_available";
                cells.Add(new(cell.Address.ToString()!, cached.Type.ToString(), display, formula, state, string.IsNullOrEmpty(format) ? cell.Style.NumberFormat.NumberFormatId.ToString(CultureInfo.InvariantCulture) : format));
            }
            rows.Add(cells);
        }
        return new(Hash(bytes), workbook.Worksheets.Select(s => s.Name).ToArray(), selected.Name, range ?? "A1:BL200", rows, WorkbookWarnings);
    }
    private OfficeProcessedFile EditWorkbookCore(ReadOnlyMemory<byte> bytes, IReadOnlyList<OfficeSpreadsheetEdit> operations, CancellationToken ct)
    {
        OfficePackageGuard.Validate(bytes, "xl", ct);
        if (operations.Count is < 1 or > OfficeLimits.MaxOperations) throw AgentCoreErrors.Validation("office_operation_unsupported: Supply 1 to 20 operations.");
        OfficePackageGuard.RequireEditableWorkbook(bytes);
        using var stream = new MemoryStream(bytes.ToArray(), false); using var workbook = new XLWorkbook(stream);
        if (workbook.IsProtected) throw AgentCoreErrors.Validation("office_operation_unsupported: Protected workbooks cannot be edited.");
        foreach (var operation in operations)
        {
            ct.ThrowIfCancellationRequested();
            if (operation.Operation == "add_sheet") { SheetName(operation.Sheet); if (workbook.Worksheets.Count >= OfficeLimits.MaxSheets) throw AgentCoreErrors.Validation("office_operation_unsupported: Worksheet limit reached."); workbook.Worksheets.Add(operation.Sheet); continue; }
            var sheet = workbook.Worksheets.FirstOrDefault(s => s.Name == operation.Sheet) ?? throw AgentCoreErrors.Validation("office_operation_unsupported: Worksheet was not found.");
            if (sheet.IsProtected) throw AgentCoreErrors.Validation("office_operation_unsupported: Protected worksheets cannot be edited.");
            if (operation.Operation == "rename_sheet" && operation.Name is not null) { SheetName(operation.Name); sheet.Name = operation.Name; continue; }
            if (operation.Range is null) throw AgentCoreErrors.Validation("office_operation_unsupported: An exact range is required for this operation.");
            var bounds = Range(operation.Range);
            if (operation.Operation == "set_cells" && operation.Cells is not null)
            {
                if (operation.Cells.Count != bounds.Rows || operation.Cells.Any(row => row.Count != bounds.Columns)) throw AgentCoreErrors.Validation("office_operation_unsupported: Cell matrix must exactly match the range.");
                SetRows(sheet, bounds.Row, bounds.Column, operation.Cells, ct);
            }
            else if (operation.Operation == "set_formula" && operation.Formula is not null && bounds.Rows == 1 && bounds.Columns == 1) SetCell(sheet.Cell(bounds.Row, bounds.Column), new("blank", Formula: operation.Formula));
            else if (operation.Operation == "set_number_format") sheet.Range(operation.Range ?? "A1:BL200").Style.NumberFormat.Format = Format(operation.Format);
            else if (operation.Operation == "create_table") sheet.Range(operation.Range ?? "A1:BL200").CreateTable();
            else throw AgentCoreErrors.Validation("office_operation_unsupported: Unsupported worksheet operation.");
        }
        using var output = new MemoryStream(); workbook.SaveAs(output, new SaveOptions { EvaluateFormulasBeforeSaving = false, ValidatePackage = true });
        var updated = output.ToArray(); OfficePackageGuard.Validate(updated, "xl", ct); return new(updated, WorkbookWarnings);
    }
}
