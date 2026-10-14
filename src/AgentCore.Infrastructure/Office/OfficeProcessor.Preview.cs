using AgentCore.Application.Ports;
using ClosedXML.Excel;

namespace AgentCore.Infrastructure.Office;

public sealed partial class OfficeProcessor
{
    private static (int Rows, int Columns, bool Stale) PreviewFrozenPane(ReadOnlyMemory<byte> bytes, string name)
    {
        using var stream = new MemoryStream(bytes.ToArray(), false);
        using var package = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(stream, false);
        var part = package.WorkbookPart!;
        var selected = part.Workbook!.Sheets!.Elements<DocumentFormat.OpenXml.Spreadsheet.Sheet>().First(s => s.Name?.Value == name);
        var worksheet = (DocumentFormat.OpenXml.Packaging.WorksheetPart)part.GetPartById(selected.Id?.Value ?? throw new InvalidDataException("Missing worksheet identity."));
        var calculation = part.Workbook?.CalculationProperties;
        var stale = calculation?.FullCalculationOnLoad?.Value == true || calculation?.ForceFullCalculation?.Value == true;
        var pane = worksheet.Worksheet?.SheetViews?.Elements<DocumentFormat.OpenXml.Spreadsheet.SheetView>().FirstOrDefault()?.Pane;
        if (pane is null) return (0, 0, stale);
        return pane.State?.Value == DocumentFormat.OpenXml.Spreadsheet.PaneStateValues.Frozen || pane.State?.Value == DocumentFormat.OpenXml.Spreadsheet.PaneStateValues.FrozenSplit
            ? ((int)Math.Clamp(pane.VerticalSplit?.Value ?? 0, 0, OfficeLimits.MaxRows), (int)Math.Clamp(pane.HorizontalSplit?.Value ?? 0, 0, OfficeLimits.MaxColumns), stale) : (0, 0, stale);
    }
    private static string PreviewNumberFormat(int id) => id switch
    {
        1 => "0", 2 => "0.00", 3 => "#,##0", 4 => "#,##0.00", 5 or 6 => "$#,##0", 7 or 8 => "$#,##0.00",
        9 => "0%", 10 => "0.00%", 11 => "0.00E+00", 12 => "# ?/?", 13 => "# ??/??", 14 => "M/d/yyyy", 15 => "d-MMM-yy", 16 => "d-MMM", 17 => "MMM-yy",
        18 => "h:mm AM/PM", 19 => "h:mm:ss AM/PM", 20 => "H:mm", 21 => "H:mm:ss", 22 => "M/d/yyyy H:mm", 37 or 38 => "#,##0;(#,##0)",
        39 or 40 => "#,##0.00;(#,##0.00)", 45 => "mm:ss", 46 => "[h]:mm:ss", 47 => "mm:ss.0", 48 => "##0.0E+0", 49 => "@", _ => "General"
    };
    private static string? PreviewColor(XLColor color, XLWorkbook workbook)
    {
        if (!color.HasValue) return null;
        try
        {
            var rgb = color.ColorType == XLColorType.Theme ? workbook.Theme.ResolveThemeColor(color.ThemeColor).Color : color.Color;
            var tint = color.ColorType == XLColorType.Theme ? color.ThemeTint : 0;
            if (rgb.A == 0) return null;
            if (tint == 0) return $"#{rgb.R:X2}{rgb.G:X2}{rgb.B:X2}";
            // OOXML tint adjusts HLS luminance, preserving hue and saturation.
            var maximum = (double)Math.Max(rgb.R, Math.Max(rgb.G, rgb.B));
            var minimum = (double)Math.Min(rgb.R, Math.Min(rgb.G, rgb.B));
            var difference = maximum - minimum;
            var luminance = (maximum + minimum) / 2;
            var saturation = difference == 0 ? 0 : difference / (255 - Math.Abs(2 * luminance - 255));
            var hue = difference == 0 ? 0 : maximum == rgb.R ? ((rgb.G - rgb.B) / difference + 6) % 6
                : maximum == rgb.G ? (rgb.B - rgb.R) / difference + 2 : (rgb.R - rgb.G) / difference + 4;
            luminance = tint < 0 ? luminance * (1 + tint) : luminance * (1 - tint) + 255 * tint;
            var chroma = (255 - Math.Abs(2 * luminance - 255)) * saturation;
            var intermediate = chroma * (1 - Math.Abs(hue % 2 - 1));
            var offset = luminance - chroma / 2;
            var channels = hue switch
            {
                < 1 => (chroma, intermediate, 0d), < 2 => (intermediate, chroma, 0d),
                < 3 => (0d, chroma, intermediate), < 4 => (0d, intermediate, chroma),
                < 5 => (intermediate, 0d, chroma), _ => (chroma, 0d, intermediate)
            };
            int Channel(double value) => (int)Math.Clamp(Math.Round(value + offset), 0, 255);
            return $"#{Channel(channels.Item1):X2}{Channel(channels.Item2):X2}{Channel(channels.Item3):X2}";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException) { return null; }
    }
    private static OfficeCellStyle PreviewStyle(IXLStyle style, XLWorkbook workbook)
    {
        var b = style.Border;
        OfficeBorder Border(XLBorderStyleValues value, XLColor color) => new(value.ToString(), PreviewColor(color, workbook));
        return new(style.Font.FontName, Math.Clamp(style.Font.FontSize, 1, 72), style.Font.Bold, style.Font.Italic,
            PreviewColor(style.Font.FontColor, workbook), style.Fill.PatternType == XLFillPatternValues.Solid ? PreviewColor(style.Fill.BackgroundColor, workbook) : null,
            style.Alignment.Horizontal.ToString(), style.Alignment.Vertical.ToString(), style.Alignment.WrapText,
            Border(b.TopBorder, b.TopBorderColor), Border(b.RightBorder, b.RightBorderColor), Border(b.BottomBorder, b.BottomBorderColor), Border(b.LeftBorder, b.LeftBorderColor));
    }
    private static OfficeWorksheetView PreviewPresentation(IXLWorksheet sheet, (int Row, int Column, int Rows, int Columns) bounds,
        int rows, int columns, IXLRange? used, (int Rows, int Columns) frozen)
    {
        var merges = new List<OfficeMerge>();
        foreach (var merge in sheet.MergedRanges)
        {
            var a = merge.RangeAddress;
            var firstRow = Math.Max(a.FirstAddress.RowNumber, bounds.Row);
            var firstColumn = Math.Max(a.FirstAddress.ColumnNumber, bounds.Column);
            var lastRow = Math.Min(a.LastAddress.RowNumber, bounds.Row + rows - 1);
            var lastColumn = Math.Min(a.LastAddress.ColumnNumber, bounds.Column + columns - 1);
            if (firstRow > lastRow || firstColumn > lastColumn) continue;
            var clipped = firstRow != a.FirstAddress.RowNumber || firstColumn != a.FirstAddress.ColumnNumber || lastRow != a.LastAddress.RowNumber || lastColumn != a.LastAddress.ColumnNumber;
            // Clipped merges are disclosed but not merged locally: never invent a new anchor/value.
            merges.Add(new(firstRow - bounds.Row, firstColumn - bounds.Column, lastRow - firstRow + 1, lastColumn - firstColumn + 1, clipped));
        }
        return new(bounds.Row, bounds.Column, used?.RangeAddress.LastAddress.RowNumber ?? 0, used?.RangeAddress.LastAddress.ColumnNumber ?? 0,
            Enumerable.Range(bounds.Row, rows).Select(r => new OfficeDimension(Math.Clamp(sheet.Row(r).Height * 96 / 72, 1, 512), sheet.Row(r).IsHidden)).ToArray(),
            Enumerable.Range(bounds.Column, columns).Select(c => new OfficeDimension(Math.Clamp(sheet.Column(c).Width * 7 + 5, 1, 1024), sheet.Column(c).IsHidden)).ToArray(),
            merges, bounds.Row == 1 ? Math.Min(frozen.Rows, rows) : 0,
            bounds.Column == 1 ? Math.Min(frozen.Columns, columns) : 0);
    }
}
