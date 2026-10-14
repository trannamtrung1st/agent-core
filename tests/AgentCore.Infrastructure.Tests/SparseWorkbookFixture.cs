using System.IO.Compression;
using System.Text;
using ClosedXML.Excel;

namespace AgentCore.Infrastructure.Tests;

internal static class SparseWorkbookFixture
{
    internal const int FormattingCells = 120024;
    internal static byte[] Create(int formattingCells = 0, int populatedCells = 0)
    {
        using var workbook = new XLWorkbook();
        var first = workbook.AddWorksheet("Content");
        first.Cell("A1").Value = "Shared Việt 日本語";
        first.Cell("A1").Style.Font.Bold = true;
        workbook.AddWorksheet("Formatted").Cell("C50").Style.Font.Bold = true;
        workbook.AddWorksheet("Empty");
        workbook.AddWorksheet("Last").Cell("Z9999").Value = "Final sheet";
        using var output = new MemoryStream();
        workbook.SaveAs(output, new SaveOptions { EvaluateFormulasBeforeSaving = false });
        var rows = new StringBuilder();
        if (populatedCells > 0)
        {
            for (var row = 1; row <= populatedCells; row++)
                rows.Append($"<row r=\"{row}\"><c r=\"A{row}\"><v>{row}</v></c></row>");
        }
        else
        {
            rows.Append("""
                <row r="1"><c r="A1"><v>0</v></c><c r="B1" t="b"><v>0</v></c><c r="C1" t="e"><v>#DIV/0!</v></c><c r="D1"><f>UNSUPPORTED_FUNCTION(1)</f></c><c r="E1" t="str"><f>""</f><v /></c><c r="F1" t="inlineStr"><is><t>Inline Việt 日本語 😀</t></is></c><c r="G1" t="inlineStr"><is><t /></is></c><c r="H1" t="s"><v>0</v></c><c r="I1" t="s"><v>1</v></c><c r="J1" t="inlineStr"><is><t xml:space="preserve"> </t></is></c><c r="K1"><f>0+0</f><v>0</v></c></row>
                """);
            for (var i = 0; i < formattingCells; i += 24)
            {
                var row = 100 + i / 24;
                rows.Append($"<row r=\"{row}\">");
                for (var column = 3; column < 27 && i + column - 3 < formattingCells; column++)
                    rows.Append($"<c r=\"{AgentCore.Application.Office.OfficeReadResults.Address(row, column)}\" s=\"1\">                    </c>");
                rows.Append("</row>");
            }
            rows.Append("<row r=\"500000\"><c r=\"B500000\" t=\"s\"><v>0</v></c></row><row r=\"1048576\"><c r=\"XFD1048576\" t=\"inlineStr\"><is><t>Far corner</t></is></c></row>");
        }
        Replace(output, "xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{rows}</sheetData></worksheet>");
        Replace(output, "xl/sharedStrings.xml", "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" count=\"3\" uniqueCount=\"3\"><si><t>Shared Việt 日本語</t></si><si><t /></si><si><t>Final sheet</t></si></sst>");
        // The last sheet's seed references SST index 1; use index 2 after introducing the explicit empty string.
        Replace(output, "xl/worksheets/sheet4.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData><row r=\"9999\"><c r=\"Z9999\" t=\"s\"><v>2</v></c></row></sheetData></worksheet>");
        return output.ToArray();
    }

    internal static void Replace(MemoryStream bytes, string name, string content)
    {
        bytes.Position = 0;
        using var zip = new ZipArchive(bytes, ZipArchiveMode.Update, true);
        zip.GetEntry(name)?.Delete();
        using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
        writer.Write(content);
    }
}
