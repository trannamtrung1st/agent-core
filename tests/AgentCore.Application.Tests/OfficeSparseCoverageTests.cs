using System.Text.Json;
using AgentCore.Application.Office;
using AgentCore.Application.Ports;
namespace AgentCore.Application.Tests;
public sealed class OfficeSparseCoverageTests
{
    [Theory]
    // first, middle, final continuation, complete selected sheet, empty selected,
    // workbook first/middle/final sheets, and a one-sheet workbook.
    [InlineData(3, 1, true, true, true, true, "partial", "partial", false)]
    [InlineData(3, 1, true, false, true, true, "partial", "partial", false)]
    [InlineData(3, 1, true, false, false, true, "partial", "partial", true)]
    [InlineData(3, 2, true, true, false, true, "complete", "complete", true)]
    [InlineData(3, 2, true, true, false, false, "complete", "complete", true)]
    [InlineData(3, 1, false, true, false, true, "partial", "complete", false)]
    [InlineData(3, 2, false, true, false, false, "partial", "complete", false)]
    [InlineData(3, 3, false, true, false, true, "partial", "complete", true)]
    [InlineData(3, 3, false, false, false, true, "partial", "partial", true)]
    [InlineData(1, 1, false, true, false, true, "complete", "complete", true)]
    public void Coverage_is_this_response_while_end_flags_are_positions_not_verified_analysis(int total, int ordinal, bool selected,
        bool beginning, bool moreCells, bool hasCells, string coverage, string sheetCoverage, bool traversalEnd)
    {
        var sheets = Enumerable.Range(1, total).Select(i => "Sheet" + i).ToArray(); var sheet = sheets[ordinal - 1];
        var source = new OfficeSparseWorkbookInspection(new string('a', 64), sheets, sheet, ordinal, selected ? sheet : null,
            hasCells ? [new("A1", "Text", "value", null, "value", "General")] : [], moreCells, hasCells, beginning, []);
        using var json = JsonDocument.Parse(OfficeReadResults.SparseWorkbook(source, 8192)); var page = json.RootElement;
        Assert.Equal(coverage, page.GetProperty("coverage").GetString()); Assert.Equal(sheetCoverage, page.GetProperty("sheetCoverage").GetString());
        Assert.Equal(!moreCells, page.GetProperty("worksheetEndReached").GetBoolean());
        Assert.Equal(traversalEnd, page.GetProperty("traversalComplete").GetBoolean());
        Assert.Equal("not_verified", page.GetProperty("analysisCoverage").GetString());
        Assert.Equal(sheetCoverage == "complete" ? "complete_sheet" : "partial_sheet", page.GetProperty("pageCoverage").GetString());
        Assert.Equal(!traversalEnd, page.GetProperty("hasMore").GetBoolean());
    }
}
