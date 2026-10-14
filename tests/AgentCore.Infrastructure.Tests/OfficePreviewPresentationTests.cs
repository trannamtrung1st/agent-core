using AgentCore.Infrastructure.Office;
using ClosedXML.Excel;
using AgentCore.Application.Ports;

namespace AgentCore.Infrastructure.Tests;
public sealed class OfficePreviewPresentationTests
{
    [Theory]
    [InlineData(0.25, "#C66332")]
    [InlineData(-0.25, "#603018")]
    public void Theme_tint_uses_luminance_and_indexed_colors_resolve_to_inert_hex(double tint, string expected)
    {
        using var workbook = new XLWorkbook();
        workbook.Theme.Accent1 = XLColor.FromArgb(128, 64, 32);
        var sheet = workbook.AddWorksheet("Colors");
        sheet.Cell("A1").Value = "Theme"; sheet.Cell("B1").Value = "Indexed";
        sheet.Cell("A1").Style.Font.FontColor = XLColor.FromTheme(XLThemeColor.Accent1, tint);
        sheet.Cell("B1").Style.Font.FontColor = XLColor.FromIndex(4);
        using var stream = new MemoryStream(); workbook.SaveAs(stream);
        var result = new OfficeProcessor().PreviewWorkbook(stream.ToArray(), "Colors", "A1:B1", default);
        Assert.Equal(expected, result.Rows[0][0].Style!.FontColor);
        Assert.Equal("#0000FF", result.Rows[0][1].Style!.FontColor);
    }
    [Fact]
    public void Preview_projects_formatting_geometry_merges_and_cached_values_without_changing_tool_inspection()
    {
        using var workbook = new XLWorkbook(); var sheet = workbook.AddWorksheet("Presentation"); workbook.AddWorksheet("Second");
        sheet.Cell("B2").Value = "Merged title"; sheet.Range("B2:D2").Merge();
        var style = sheet.Cell("B2").Style; style.Font.FontName = "Arial"; style.Font.FontSize = 18; style.Font.Bold = true; style.Font.Italic = true;
        style.Font.FontColor = XLColor.FromTheme(XLThemeColor.Accent1, 0.25); style.Fill.BackgroundColor = XLColor.Red;
        style.Border.BottomBorder = XLBorderStyleValues.Double; style.Border.BottomBorderColor = XLColor.Blue;
        style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center; style.Alignment.Vertical = XLAlignmentVerticalValues.Top; style.Alignment.WrapText = true;
        sheet.Row(2).Height = 36; sheet.Column(2).Width = 24; sheet.Row(3).Hide(); sheet.Column(3).Hide(); sheet.SheetView.Freeze(2, 1);
        sheet.Cell("B4").Value = 1234.5; sheet.Cell("B4").Style.NumberFormat.Format = "$#,##0.00";
        sheet.Cell("C4").Value = 0.125; sheet.Cell("C4").Style.NumberFormat.NumberFormatId = 10;
        sheet.Cell("D4").Value = new DateTime(2026, 10, 14); sheet.Cell("D4").Style.NumberFormat.NumberFormatId = 14;
        sheet.Cell("B5").Value = "00123"; sheet.Cell("C5").FormulaA1 = "SUM(B4:B4)";
        using var stream = new MemoryStream(); workbook.SaveAs(stream, new SaveOptions { EvaluateFormulasBeforeSaving = false }); var bytes = stream.ToArray();
        if (Environment.GetEnvironmentVariable("AGENTCORE_PREVIEW_FIXTURE_PATH") is { } fixturePath) File.WriteAllBytes(fixturePath, bytes);
        var processor = new OfficeProcessor(); var result = processor.PreviewWorkbook(bytes, "Presentation", "A1:D5", default);
        Assert.Equal(2, result.Sheets.Count); var s = result.Rows[1][1].Style!;
        Assert.True(s.Bold); Assert.True(s.Italic); Assert.Equal(18, s.FontSize); Assert.Equal("#FF0000", s.FillColor); Assert.Matches("^#[0-9A-F]{6}$", s.FontColor!);
        Assert.Equal("Double", s.Bottom.Style); Assert.Equal("Center", s.Horizontal); Assert.True(s.Wrap);
        var view = result.Presentation!; Assert.Equal(2, view.FrozenRows); Assert.Equal(1, view.FrozenColumns);
        Assert.Equal(48, view.RowDimensions[1].Pixels); Assert.True(view.RowDimensions[2].Hidden); Assert.True(view.ColumnDimensions[2].Hidden);
        Assert.Equal(new OfficeMerge(1, 1, 1, 3, false), Assert.Single(view.Merges));
        Assert.Equal("$1,234.50", result.Rows[3][1].Value); Assert.Equal("12.50%", result.Rows[3][2].Value); Assert.Contains("2026", result.Rows[3][3].Value); Assert.Equal("00123", result.Rows[4][1].Value);
        Assert.Equal("cached_value_unavailable", result.Rows[4][2].ValueStatus);
        var clipped = processor.PreviewWorkbook(bytes, "Presentation", "C2:D5", default);
        Assert.True(Assert.Single(clipped.Presentation!.Merges).Clipped); Assert.Equal(3, clipped.Presentation.FirstColumn); Assert.Equal(2, clipped.Presentation.FirstRow);
        Assert.Equal(0, clipped.Presentation.FrozenRows);
        Assert.Null(processor.InspectWorkbook(bytes, "Presentation", "A1:D5", default).Presentation);
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Cached_results_are_read_without_calculation_and_explicit_stale_flags_are_disclosed(bool stale)
    {
        var processor = new OfficeProcessor();
        var original = processor.CreateWorkbook(new([new("Formula", [[new("blank", Formula: "1+1")]])]), default).Bytes;
        using var stream = new MemoryStream(); stream.Write(original); stream.Position = 0;
        using (var package = DocumentFormat.OpenXml.Packaging.SpreadsheetDocument.Open(stream, true))
        {
            var part = package.WorkbookPart!; var worksheet = part.WorksheetParts.Single();
            var cell = worksheet.Worksheet!.Descendants<DocumentFormat.OpenXml.Spreadsheet.Cell>().Single(c => c.CellReference?.Value == "A1");
            cell.CellValue = new DocumentFormat.OpenXml.Spreadsheet.CellValue("99");
            cell.DataType = DocumentFormat.OpenXml.Spreadsheet.CellValues.Number;
            part.Workbook!.CalculationProperties = new DocumentFormat.OpenXml.Spreadsheet.CalculationProperties { FullCalculationOnLoad = stale, ForceFullCalculation = stale };
            worksheet.Worksheet.Save(); part.Workbook.Save();
        }
        var result = processor.PreviewWorkbook(stream.ToArray(), "Formula", "A1", default).Rows[0][0];
        Assert.Equal("99", result.Value); Assert.Equal("1+1", result.Formula);
        Assert.Equal(stale ? "cached_value_stale" : "cached_value_available", result.ValueStatus);
        Assert.Equal("cached_value_unavailable", processor.PreviewWorkbook(original, "Formula", "A1", default).Rows[0][0].ValueStatus);
    }

}
