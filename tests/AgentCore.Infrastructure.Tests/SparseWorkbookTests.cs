using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Office;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Attachments;
using AgentCore.Infrastructure.Office;
using ClosedXML.Excel;
using Xunit.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class SparseWorkbookTests(ITestOutputHelper output)
{
    private readonly OfficeProcessor processor = new();

    [Theory]
    [InlineData(3)]
    [InlineData(50010)]
    public void Typed_style_only_records_do_not_count_or_appear_as_empty_text(int records)
    {
        using var stream = new MemoryStream(); stream.Write(SparseWorkbookFixture.Create());
        var rows = string.Concat(Enumerable.Range(2, records).Select(row => $"<row r=\"{row}\"><c r=\"A{row}\" s=\"1\" t=\"{(row % 3 == 0 ? "s" : row % 3 == 1 ? "str" : "inlineStr")}\" /></row>"));
        SparseWorkbookFixture.Replace(stream, "xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{rows}</sheetData></worksheet>");
        var source = processor.ReadWorkbookContent(stream.ToArray(), "Content", null, default);
        Assert.Empty(source.Cells);
        Assert.True(source.HasFormatting);
    }

    [Fact]
    public void Stored_text_representation_and_formula_distinguish_empty_values_from_type_only_records()
    {
        using var stream = new MemoryStream(); stream.Write(SparseWorkbookFixture.Create());
        SparseWorkbookFixture.Replace(stream, "xl/worksheets/sheet1.xml", """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData><row r="1">
            <c r="A1" t="s"><v /></c><c r="B1" t="inlineStr"><v /></c><c r="C1"><v /></c>
            <c r="D1" t="str"><v /></c><c r="E1" t="inlineStr"><is><t /></is></c>
            <c r="F1" t="s"><v>1</v></c><c r="G1"><f>1+1</f></c>
            <c r="H1"><v>0</v></c><c r="I1" t="b"><v>0</v></c><c r="J1" t="e"><v>#N/A</v></c>
            </row></sheetData></worksheet>
            """);
        var source = processor.ReadWorkbookContent(stream.ToArray(), "Content", null, default);
        Assert.Equal(["D1", "E1", "F1", "G1", "H1", "I1", "J1"], source.Cells.Select(c => c.Address));
        Assert.All(source.Cells.Take(3), c => Assert.Equal("", c.Value));
        Assert.Equal("cached_value_unavailable", source.Cells[3].ValueStatus);
    }

    [Fact]
    public void Final_continuation_page_does_not_claim_prior_page_coverage()
    {
        var bytes = SparseWorkbookFixture.Create(populatedCells: 1101);
        var initial = processor.ReadWorkbookContent(bytes, "Content", null, default);
        var cursor = OfficeSparseCursor.Encode(initial.Sha256, "Content", 1, "Content", "A1000");
        using var final = JsonDocument.Parse(OfficeReadResults.SparseWorkbook(processor.ReadWorkbookContent(bytes, "Content", cursor, default), 128 * 1024));
        Assert.Equal("partial", final.RootElement.GetProperty("coverage").GetString());
        Assert.Equal("partial", final.RootElement.GetProperty("sheetCoverage").GetString());
        Assert.True(final.RootElement.GetProperty("traversalComplete").GetBoolean());
    }

    [Fact]
    public void Large_formatted_template_reads_content_with_original_coordinates_and_keeps_strict_operations_bounded()
    {
        var bytes = SparseWorkbookFixture.Create(SparseWorkbookFixture.FormattingCells);
        using (var zip = new ZipArchive(new MemoryStream(bytes)))
            Assert.True(zip.GetEntry("xl/worksheets/sheet1.xml")!.Length > 4 * 1024 * 1024);
        var page = processor.ReadWorkbookContent(bytes, null, null, default);
        Assert.Equal(13, page.Cells.Count);
        Assert.Equal("A1", page.Cells[0].Address);
        Assert.Equal("0", page.Cells[0].Value);
        Assert.Equal("Boolean", page.Cells[1].Type);
        Assert.Equal("False", page.Cells[1].Value);
        Assert.Equal("#DIV/0!", page.Cells[2].Value);
        Assert.Equal("cached_value_unavailable", page.Cells[3].ValueStatus);
        Assert.Equal("UNSUPPORTED_FUNCTION(1)", page.Cells[3].Formula);
        Assert.Equal("", page.Cells.Single(c => c.Address == "E1").Value);
        Assert.Equal("cached_value_available", page.Cells.Single(c => c.Address == "E1").ValueStatus);
        Assert.Equal("Inline Việt 日本語 😀", page.Cells.Single(c => c.Address == "F1").Value);
        Assert.Equal("Text", page.Cells.Single(c => c.Address == "G1").Type);
        Assert.Equal("", page.Cells.Single(c => c.Address == "G1").Value);
        Assert.Equal("Shared Việt 日本語", page.Cells.Single(c => c.Address == "H1").Value);
        Assert.Equal("", page.Cells.Single(c => c.Address == "I1").Value);
        Assert.Equal(" ", page.Cells.Single(c => c.Address == "J1").Value);
        Assert.Equal("0", page.Cells.Single(c => c.Address == "K1").Value);
        Assert.Equal("B500000", page.Cells[^2].Address);
        Assert.Equal("XFD1048576", page.Cells[^1].Address);
        Assert.Equal("office_processing_limit", Assert.Throws<AgentCoreException>(() => processor.PreviewWorkbook(bytes, null, "A1", default)).Code);
        Assert.Equal("office_processing_limit", Assert.Throws<AgentCoreException>(() => processor.EditWorkbook(bytes, [new("set_formula", "Content", "A1", Formula: "1")], default)).Code);
        Assert.Equal("office_processing_limit", Assert.Throws<AgentCoreException>(() => processor.InspectWorkbook(bytes, null, "A1", default)).Code);
    }

    [Fact]
    public void Inferred_coordinates_empty_text_and_missing_string_formula_cache_are_preserved()
    {
        using var stream = new MemoryStream(); stream.Write(SparseWorkbookFixture.Create());
        SparseWorkbookFixture.Replace(stream, "xl/worksheets/sheet1.xml", """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
            <row r="5"><c t="inlineStr"><is><t /></is></c><c t="s"><v>1</v></c><c t="str"><f>""</f></c></row>
            </sheetData></worksheet>
            """);
        var source = processor.ReadWorkbookContent(stream.ToArray(), "Content", null, default);
        Assert.Equal(["A5", "B5", "C5"], source.Cells.Select(c => c.Address));
        Assert.All(source.Cells, c => Assert.Equal("", c.Value));
        Assert.Equal("cached_value_unavailable", source.Cells[2].ValueStatus);
        SparseWorkbookFixture.Replace(stream, "xl/worksheets/sheet2.xml", """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><cols><col min="1" max="1" style="1" /></cols><sheetData /></worksheet>
            """);
        using var page = JsonDocument.Parse(OfficeReadResults.SparseWorkbook(processor.ReadWorkbookContent(stream.ToArray(), "Formatted", null, default), 8192));
        Assert.Equal("formatting_only", page.RootElement.GetProperty("sheetState").GetString());
        Assert.Empty(page.RootElement.GetProperty("cells").EnumerateArray());
    }

    [Fact]
    public void Whole_cell_output_pagination_visits_each_sheet_and_each_cell_exactly_once()
    {
        var bytes = SparseWorkbookFixture.Create(populatedCells: 1101);
        var seen = new List<string>(); var sheets = new List<string>(); string? cursor = null;
        for (var call = 0; call < 100; call++)
        {
            var source = processor.ReadWorkbookContent(bytes, null, cursor, default);
            Assert.InRange(source.Cells.Count, 0, 500);
            var text = OfficeReadResults.SparseWorkbook(source, 8192);
            Assert.True(Encoding.UTF8.GetByteCount(text) <= 8192);
            using var json = JsonDocument.Parse(text); var page = json.RootElement;
            Assert.False(page.TryGetProperty("error", out _), text);
            sheets.Add(page.GetProperty("sheet").GetString()!);
            foreach (var cell in page.GetProperty("cells").EnumerateArray()) seen.Add(source.Sheet + "!" + cell.GetProperty("address").GetString());
            cursor = page.GetProperty("nextCursor").GetString();
            Assert.Equal(cursor is not null, page.GetProperty("hasMore").GetBoolean());
            if (cursor is null)
            {
                Assert.Equal("partial", page.GetProperty("coverage").GetString());
                Assert.True(page.GetProperty("traversalComplete").GetBoolean());
                break;
            }
        }
        Assert.Null(cursor);
        Assert.Equal(Enumerable.Range(1, 1101).Select(i => "Content!A" + i).Append("Last!Z9999"), seen);
        Assert.Equal(["Content", "Formatted", "Empty", "Last"], sheets.Distinct());
        foreach (var (sheet, state) in new[] { ("Formatted", "formatting_only"), ("Empty", "empty") })
        {
            using var page = JsonDocument.Parse(OfficeReadResults.SparseWorkbook(processor.ReadWorkbookContent(bytes, sheet, null, default), 8192));
            Assert.Empty(page.RootElement.GetProperty("cells").EnumerateArray());
            Assert.Equal(state, page.RootElement.GetProperty("sheetState").GetString());
            Assert.Equal("complete", page.RootElement.GetProperty("coverage").GetString());
            Assert.False(page.RootElement.GetProperty("hasMore").GetBoolean());
        }
    }

    [Fact]
    public void Cursor_rejects_changed_source_selection_identity_and_malformed_input()
    {
        var bytes = SparseWorkbookFixture.Create(populatedCells: 501);
        using var page = JsonDocument.Parse(OfficeReadResults.SparseWorkbook(processor.ReadWorkbookContent(bytes, "Content", null, default), 128 * 1024));
        var cursor = page.RootElement.GetProperty("nextCursor").GetString();
        Assert.Equal("office_revision_conflict", Assert.Throws<AgentCoreException>(() => processor.ReadWorkbookContent(SparseWorkbookFixture.Create(populatedCells: 502), "Content", cursor, default)).Code);
        Assert.Equal("office_cursor_invalid", Assert.Throws<AgentCoreException>(() => processor.ReadWorkbookContent(bytes, null, cursor, default)).Code);
        Assert.Equal("office_cursor_invalid", Assert.Throws<AgentCoreException>(() => processor.ReadWorkbookContent(bytes, "Content", "broken", default)).Code);
        var source = processor.ReadWorkbookContent(bytes, "Content", null, default);
        var malformedHash = OfficeSparseCursor.Encode("invalid", "Content", 1, "Content", "A1");
        Assert.Equal("office_cursor_invalid", Assert.Throws<AgentCoreException>(() => processor.ReadWorkbookContent(bytes, "Content", malformedHash, default)).Code);
        var forged = OfficeSparseCursor.Encode(source.Sha256, "Content", 2, "Content", "A1");
        Assert.Equal("office_cursor_invalid", Assert.Throws<AgentCoreException>(() => processor.ReadWorkbookContent(bytes, "Content", forged, default)).Code);
        forged = OfficeSparseCursor.Encode(source.Sha256, "Content", 1, "Content", "Z123");
        Assert.Equal("office_cursor_invalid", Assert.Throws<AgentCoreException>(() => processor.ReadWorkbookContent(bytes, "Content", forged, default)).Code);
        Assert.Single(processor.ReadWorkbookContent(bytes, "Content", cursor, default).Cells);
    }

    [Fact]
    public void Oversized_cell_returns_a_truthful_output_error_and_prompt_refitting_keeps_valid_json_and_continuation()
    {
        var bytes = processor.CreateWorkbook(new([new("Long", [[new("text", Text: new string('x', 16000))]])]), default).Bytes;
        var source = processor.ReadWorkbookContent(bytes, null, null, default);
        using var limited = JsonDocument.Parse(OfficeReadResults.SparseWorkbook(source, 2048));
        Assert.Equal("office_output_limit", limited.RootElement.GetProperty("error").GetString());
        Assert.False(limited.RootElement.TryGetProperty("nextCursor", out _));
        var record = Record(SparseWorkbookFixture.Create(populatedCells: 60));
        var processed = AttachmentProcessor.ProcessBytes(record.Record, record.Bytes, processor);
        var prompt = PromptContextBuilder.BuildCurrentUserMessage("Summarize", [processed], true, new Dictionary<Guid, int> { [processed.AttachmentId] = 4096 });
        using var fitted = JsonDocument.Parse(prompt.Text.Split("\"\"\"")[1].Trim());
        Assert.Equal("sparse", fitted.RootElement.GetProperty("mode").GetString());
        Assert.True(fitted.RootElement.GetProperty("hasMore").GetBoolean());
        var last = fitted.RootElement.GetProperty("cells").EnumerateArray().Last().GetProperty("address").GetString();
        var next = processor.ReadWorkbookContent(record.Bytes, null, fitted.RootElement.GetProperty("nextCursor").GetString(), default);
        Assert.Equal("A" + (int.Parse(last![1..]) + 1), next.Cells[0].Address);
        Assert.Contains("attachments.read", prompt.Text);
        Assert.Equal("partial", fitted.RootElement.GetProperty("coverage").GetString());
    }

    [Fact]
    public void Initial_excerpt_finds_distant_content_and_retains_original_attachment_bytes()
    {
        var fixture = Record(SparseWorkbookFixture.Create(SparseWorkbookFixture.FormattingCells));
        var original = fixture.Bytes.ToArray();
        var processed = AttachmentProcessor.ProcessBytes(fixture.Record, fixture.Bytes, processor);
        Assert.Equal(AttachmentProcessKind.ExtractedText, processed.Kind);
        using var json = JsonDocument.Parse(processed.Text);
        Assert.Equal("sparse", json.RootElement.GetProperty("mode").GetString());
        Assert.Contains("XFD1048576", processed.Text);
        Assert.True(json.RootElement.GetProperty("hasMore").GetBoolean());
        Assert.Equal("partial", json.RootElement.GetProperty("coverage").GetString());
        Assert.Equal(original, fixture.Bytes);
    }

    [Theory]
    [InlineData("scan", "office_processing_limit")]
    [InlineData("populated", "office_processing_limit")]
    [InlineData("part", "office_processing_limit")]
    [InlineData("nodes", "office_processing_limit")]
    [InlineData("depth", "office_processing_limit")]
    [InlineData("shared", "office_processing_limit")]
    [InlineData("formula", "office_processing_limit")]
    [InlineData("dtd", "invalid_office_package")]
    [InlineData("xml", "invalid_office_package")]
    [InlineData("traversal", "invalid_office_package")]
    [InlineData("duplicate", "invalid_office_package")]
    [InlineData("external", "office_feature_unsupported")]
    [InlineData("macro", "office_feature_unsupported")]
    [InlineData("external_formula", "office_feature_unsupported")]
    [InlineData("expanded", "office_processing_limit")]
    [InlineData("package_nodes", "office_processing_limit")]
    [InlineData("sheets", "office_processing_limit")]
    public void Sparse_guard_classifies_security_and_resource_boundaries(string kind, string expected)
    {
        var bytes = SparseWorkbookFixture.Create();
        using var stream = new MemoryStream(); stream.Write(bytes);
        var ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        var body = kind switch
        {
            "scan" => string.Concat(Enumerable.Repeat("<c />", OfficeLimits.MaxSparseScannedCells + 1)),
            "populated" => string.Concat(Enumerable.Repeat("<c><v>0</v></c>", OfficeLimits.MaxCells + 1)),
            "part" => new string(' ', OfficeLimits.MaxSparsePartBytes),
            "nodes" => string.Concat(Enumerable.Repeat("<x />", OfficeLimits.MaxSparsePartNodes + 1)),
            "depth" => string.Concat(Enumerable.Repeat("<x>", 65)) + string.Concat(Enumerable.Repeat("</x>", 65)),
            "formula" => "<c r=\"A1\"><f t=\"array\" ref=\"A1:XFD1048576\">0</f></c>",
            "external_formula" => "<c r=\"A1\"><f>'[other.xlsx]Sheet1'!A1</f></c>",
            _ => ""
        };
        SparseWorkbookFixture.Replace(stream, "xl/worksheets/sheet1.xml", kind switch
        {
            "dtd" => "<!DOCTYPE worksheet [<!ENTITY x 'bad'>]><worksheet />",
            "xml" => "<worksheet>",
            _ => $"<worksheet xmlns=\"{ns}\"><sheetData>{body}</sheetData></worksheet>"
        });
        if (kind == "shared") SparseWorkbookFixture.Replace(stream, "xl/sharedStrings.xml", $"<sst xmlns=\"{ns}\">{string.Concat(Enumerable.Repeat("<si><t /></si>", OfficeLimits.MaxCells + 1))}</sst>");
        if (kind is "traversal" or "external" or "macro") SparseWorkbookFixture.Replace(stream, kind switch { "traversal" => "../evil.xml", "external" => "_rels/external.rels", _ => "xl/vba.bin" }, kind == "external" ? "<Relationships><Relationship TargetMode=\"External\" Target=\"https://example.test/\" /></Relationships>" : "<x />");
        if (kind == "duplicate")
        {
            stream.Position = 0; using var zip = new ZipArchive(stream, ZipArchiveMode.Update, true);
            using var writer = new StreamWriter(zip.CreateEntry("xl/workbook.xml").Open()); writer.Write("<x />");
        }
        if (kind == "sheets") SparseWorkbookFixture.Replace(stream, "xl/workbook.xml", "<workbook><sheets>" + string.Concat(Enumerable.Range(1, 51).Select(i => $"<sheet name=\"S{i}\" />")) + "</sheets></workbook>");
        if (kind == "expanded")
            for (var i = 0; i < 9; i++) SparseWorkbookFixture.Replace(stream, $"parts/padding{i}.xml", "<x>" + new string(' ', 4 * 1024 * 1024 - 7) + "</x>");
        if (kind == "package_nodes")
            for (var i = 0; i < 3; i++) SparseWorkbookFixture.Replace(stream, $"xl/worksheets/extra{i}.xml", $"<worksheet xmlns=\"{ns}\">" + string.Concat(Enumerable.Repeat("<x />", 1000001)) + "</worksheet>");
        var error = Assert.Throws<AgentCoreException>(() => OfficePackageGuard.ValidateSparseWorkbook(stream.ToArray(), default));
        if (expected == "invalid_office_package") Assert.StartsWith("invalid_office_package:", error.Message);
        else Assert.Equal(expected, error.Code);
    }

    [Fact]
    public void Worksheet_identity_follows_relationships_instead_of_assuming_part_filenames()
    {
        using var stream = new MemoryStream(); stream.Write(SparseWorkbookFixture.Create());
        var ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        {
            string relationships;
            string types;
            stream.Position = 0;
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Read, true))
            {
                using var reader = new StreamReader(zip.GetEntry("xl/_rels/workbook.xml.rels")!.Open());
                relationships = reader.ReadToEnd().Replace("worksheets/sheet1.xml", "nonstandard.xml", StringComparison.Ordinal);
                using var typeReader = new StreamReader(zip.GetEntry("[Content_Types].xml")!.Open());
                types = typeReader.ReadToEnd().Replace("worksheets/sheet1.xml", "nonstandard.xml", StringComparison.Ordinal);
            }
            SparseWorkbookFixture.Replace(stream, "xl/_rels/workbook.xml.rels", relationships);
            SparseWorkbookFixture.Replace(stream, "[Content_Types].xml", types);
            SparseWorkbookFixture.Replace(stream, "xl/nonstandard.xml", $"<worksheet xmlns=\"{ns}\"><sheetData /></worksheet>");
        }
        var source = processor.ReadWorkbookContent(stream.ToArray(), "Content", null, default);
        Assert.Equal("Content", source.Sheet);
        Assert.Empty(source.Cells);
    }

    [Fact]
    public void Cancellation_stops_scan_and_read()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var bytes = SparseWorkbookFixture.Create();
        Assert.ThrowsAny<OperationCanceledException>(() => OfficePackageGuard.ValidateSparseWorkbook(bytes, cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => processor.ReadWorkbookContent(bytes, null, null, cancelled.Token));
    }

    [Fact]
    public void Empty_numeric_value_records_do_not_consume_populated_budget()
    {
        using var stream = new MemoryStream(); stream.Write(SparseWorkbookFixture.Create());
        var rows = new StringBuilder();
        for (var row = 1; row <= OfficeLimits.MaxCells + 1; row++)
            rows.Append($"<row r=\"{row}\"><c r=\"A{row}\" s=\"1\"><v /></c></row>");
        SparseWorkbookFixture.Replace(stream, "xl/worksheets/sheet1.xml", $"<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>{rows}</sheetData></worksheet>");
        var page = processor.ReadWorkbookContent(stream.ToArray(), "Content", null, default);
        Assert.Empty(page.Cells);
        Assert.True(page.HasStoredCells);
        Assert.False(page.SheetHasMore);
    }

    [Fact]
    public void Measure_generated_template_open_enumeration_allocations_and_stateless_pagination()
    {
        var bytes = SparseWorkbookFixture.Create(SparseWorkbookFixture.FormattingCells);
        // Warm dependency initialization; measurements are evidence, never fragile timing assertions.
        processor.ReadWorkbookContent(SparseWorkbookFixture.Create(), null, null, default);
        Measure("security scan", () => OfficePackageGuard.ValidateSparseWorkbook(bytes, default));
        using var stream = new MemoryStream(bytes);
        XLWorkbook? workbook = null;
        Measure("ClosedXML open", () => workbook = new XLWorkbook(stream));
        using (workbook!)
            Measure("Contents enumeration", () => Assert.Equal(11, workbook!.Worksheet("Content").CellsUsed(XLCellsUsedOptions.Contents).Count()));
        OfficeSparseWorkbookInspection? first = null;
        Measure("full first read", () => first = processor.ReadWorkbookContent(bytes, null, null, default));
        using var page = JsonDocument.Parse(OfficeReadResults.SparseWorkbook(first!, 8192));
        Measure("next worksheet read (reopen)", () => Assert.Empty(processor.ReadWorkbookContent(bytes, null, page.RootElement.GetProperty("nextCursor").GetString(), default).Cells));
        using var smaller = JsonDocument.Parse(OfficeReadResults.SparseWorkbook(first!, 2048));
        Assert.False(smaller.RootElement.TryGetProperty("error", out _), smaller.RootElement.ToString());
        var sameSheetCursor = smaller.RootElement.GetProperty("nextCursor").GetString();
        Assert.Equal(1, OfficeSparseCursor.Decode(sameSheetCursor, first!.Sha256, null)!.SheetOrdinal);
        Measure("same worksheet continuation (reopen)", () => Assert.Equal("Content", processor.ReadWorkbookContent(bytes, null, sameSheetCursor, default).Sheet));
        output.WriteLine($"fixture: compressed={bytes.Length} bytes, formatting-only cells={SparseWorkbookFixture.FormattingCells}, populated first-sheet cells={first!.Cells.Count}");
    }

    private void Measure(string name, Action action)
    {
        var allocated = GC.GetAllocatedBytesForCurrentThread(); var watch = Stopwatch.StartNew();
        action(); watch.Stop();
        output.WriteLine($"{name}: {watch.Elapsed.TotalMilliseconds:F1} ms; {(GC.GetAllocatedBytesForCurrentThread() - allocated) / 1048576.0:F2} MiB allocated");
    }

    private static (AttachmentRecord Record, byte[] Bytes) Record(byte[] bytes) => (new(Guid.NewGuid(), Guid.NewGuid(), "blob", "sparse.xlsx", OfficeLimits.SpreadsheetMime,
        bytes.Length, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), AgentCore.Domain.Conversation.AttachmentState.Bound,
        true, Guid.NewGuid(), false, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow), bytes);
}
