using System.IO.Compression;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Office;

namespace AgentCore.Infrastructure.Tests;

public sealed class OfficeProcessorTests
{
    private readonly OfficeProcessor _processor = new();
    [Fact]
    public void Document_table_selector_edit_is_exact_and_bad_selectors_never_mutate_original()
    {
        var document = _processor.CreateDocument(new("Report", [new("Results", ["Repeated", "Repeated"], [["Region", "Value"], ["North", "123"]])]), default).Bytes;
        using (var package = WordprocessingDocument.Open(new MemoryStream(document), false)) { var errors = new OpenXmlValidator().Validate(package).ToArray(); Assert.True(errors.Length == 0, string.Join("\n", errors.Select(error => error.Description + " at " + error.Path?.XPath))); }
        var before = document.ToArray(); var inspection = _processor.InspectDocument(document, 0, 20, default);
        var table = inspection.Elements.Single(e => e.Kind == "Table");
        var edit = _processor.EditDocument(document, [new("replace_table_cell", table.Selector + ":1:1", "456")], default);
        Assert.Equal("456", _processor.InspectDocument(edit.Bytes, 0, 20, default).Elements.Single(e => e.Kind == "Table").Rows![1][1]);
        Assert.Equal(before, document);
        Assert.Throws<AgentCoreException>(() => _processor.EditDocument(document, [new("replace_paragraph", "Repeated", "ambiguous")], default));
        Assert.Equal(before, document);
        var paragraph = inspection.Elements.First(e => e.Text == "Repeated");
        Assert.Throws<AgentCoreException>(() => _processor.EditDocument(document, [new("replace_paragraph", paragraph.Selector, "one"), new("replace_paragraph", paragraph.Selector, "two")], default));
    }
    [Theory]
    [InlineData("external")]
    [InlineData("dtd")]
    [InlineData("traversal")]
    [InlineData("macro")]
    [InlineData("bomb")]
    [InlineData("paragraphs")]
    [InlineData("cells")]
    [InlineData("pages")]
    [InlineData("connection")]
    public void Unsafe_packages_fail_before_office_interpretation(string kind)
    {
        using var bytes = new MemoryStream();
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            void Entry(string name, string content) { using var writer = new StreamWriter(archive.CreateEntry(name).Open()); writer.Write(content); }
            Entry("[Content_Types].xml", kind == "macro" ? "<Types><Override ContentType=\"application/vnd.ms-word.document.macroEnabled.main+xml\" /></Types>" : "<Types />");
            Entry("word/document.xml", kind == "dtd" ? "<!DOCTYPE test [<!ENTITY foo SYSTEM 'https://invalid.test/leak'>]><test>&foo;</test>" : "<document />");
            if (kind == "connection") Entry("xl/connections.xml", "<connections />");
            if (kind == "external") Entry("word/_rels/document.xml.rels", "<Relationships><Relationship Id=\"x\" TargetMode=\"External\" Target=\"https://invalid.test/leak\" /></Relationships>");
            if (kind == "traversal") Entry("../secret.xml", "<x />");
            if (kind == "paragraphs") Entry("word/limits.xml", "<body>" + string.Concat(Enumerable.Repeat("<p />", OfficeLimits.MaxDocumentParagraphs + 1)) + "</body>");
            if (kind == "pages") Entry("word/limits.xml", "<body>" + string.Concat(Enumerable.Repeat("<lastRenderedPageBreak />", OfficeLimits.MaxDocumentPageBreaks + 1)) + "</body>");
            if (kind == "cells") Entry("word/limits.xml", "<body>" + string.Concat(Enumerable.Repeat("<tc />", OfficeLimits.MaxDocumentCells + 1)) + "</body>");
            if (kind == "bomb") Entry("word/huge.xml", new string('x', 5 * 1024 * 1024));
        }
        Assert.Throws<AgentCoreException>(() => OfficePackageGuard.Validate(bytes.ToArray(), "word", default));
    }
    [Fact]
    public void Formula_cache_is_not_evaluated_and_external_formulas_or_oversized_ranges_fail()
    {
        var file = _processor.CreateWorkbook(new([new("Sheet", [[new("text", Text: "0001"), new("number", Number: 125, Format: "currency")], [new("blank", Formula: "SUM(B1:B1)")]])]), default);
        var inspection = _processor.InspectWorkbook(file.Bytes, "Sheet", "A1:B2", default);
        Assert.Equal("0001", inspection.Rows[0][0].Value); Assert.Equal("$125.00", inspection.Rows[0][1].Value);
        Assert.Equal("SUM(B1:B1)", inspection.Rows[1][0].Formula); Assert.Equal("cached_value_unavailable", inspection.Rows[1][0].ValueStatus);
        Assert.Throws<AgentCoreException>(() => _processor.InspectWorkbook(file.Bytes, "Sheet", "A1:B201", default));
        Assert.Throws<AgentCoreException>(() => _processor.EditWorkbook(file.Bytes, [new("set_formula", "Sheet", "A1", Formula: "WEBSERVICE(\"https://invalid.test\")")], default));
        Assert.Equal("$125.00", _processor.InspectWorkbook(file.Bytes, "Sheet", "B1", default).Rows[0][0].Value);
    }
    [Fact]
    public void Valid_unsupported_and_processing_limit_packages_are_not_reported_as_corrupt()
    {
        var original = _processor.CreateWorkbook(new([new("Links", [[new("text", Text: "Vendor URL")]])]), default).Bytes;
        using var bytes = new MemoryStream();
        bytes.Write(original);
        using (var archive = new ZipArchive(bytes, ZipArchiveMode.Update, true))
        {
            using var writer = new StreamWriter(archive.CreateEntry("xl/_rels/unsupported.xml.rels").Open());
            writer.Write("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"link\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" TargetMode=\"External\" Target=\"https://invalid.test/vendor\" /></Relationships>");
        }
        var unsupported = Assert.Throws<AgentCoreException>(() => _processor.InspectWorkbook(bytes.ToArray(), null, "A1", default));
        Assert.Equal("office_feature_unsupported", unsupported.Code);
        Assert.Contains("does not establish corruption", unsupported.Message);
        var record = new AttachmentRecord(Guid.NewGuid(), Guid.NewGuid(), "blob", "links.xlsx", OfficeLimits.SpreadsheetMime,
            bytes.Length, "hash", AgentCore.Domain.Conversation.AttachmentState.Bound, true, Guid.NewGuid(), false, DateTimeOffset.UtcNow, null, DateTimeOffset.UtcNow);
        var processed = AgentCore.Infrastructure.Attachments.AttachmentProcessor.ProcessBytes(record, bytes.ToArray(), _processor);
        Assert.Equal("office_feature_unsupported", processed.FailureCode);
        Assert.Contains("External relationships", processed.Text);
        var limit = Assert.Throws<AgentCoreException>(() => OfficePackageGuard.Validate(new byte[OfficeLimits.MaxPackageBytes + 1], "xl", default));
        Assert.Equal("office_processing_limit", limit.Code);
        Assert.Contains("invalid_office_package", Assert.Throws<AgentCoreException>(() => _processor.InspectWorkbook("PKbad"u8.ToArray(), null, "A1", default)).Message);
    }

    [Fact]
    public void Inspection_text_budget_ends_on_a_complete_row_and_continues_to_the_last_row()
    {
        var rows = Enumerable.Range(1, 17).Select(i => (IReadOnlyList<OfficeCell>)new[] { new OfficeCell("text", Text: i.ToString() + new string('x', 3998)) }).ToArray();
        var bytes = _processor.CreateWorkbook(new([new("Long", rows)]), default).Bytes;
        var first = _processor.InspectWorkbook(bytes, "Long", "A1:A17", default);
        Assert.Equal(16, first.Rows.Count);
        using var json = System.Text.Json.JsonDocument.Parse(AgentCore.Application.Office.OfficeReadResults.Workbook(first, 256 * 1024));
        Assert.Equal("A17:A17", json.RootElement.GetProperty("nextRange").GetString());
        Assert.True(json.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(rows[16][0].Text, Assert.Single(Assert.Single(_processor.InspectWorkbook(bytes, "Long", "A17", default).Rows)).Value);
    }

    [Fact]
    public void Cancellation_does_not_process_or_commit_any_bytes()
    {
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => _processor.CreateDocument(new(null, [new(null, ["x"], null)]), cancelled.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => _processor.CreateWorkbook(new([new("Sheet", [])]), cancelled.Token));
    }
}
