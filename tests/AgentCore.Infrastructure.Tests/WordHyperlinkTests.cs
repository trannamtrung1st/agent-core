using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Xml.Linq;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Office;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AgentCore.Infrastructure.Tests;

public sealed class WordHyperlinkTests
{
    private const string HyperlinkType = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink";
    private readonly OfficeProcessor processor = new();

    [Theory]
    [InlineData("https://example.test/report?q=hello%20world#section")]
    [InlineData("http://example.test/report")]
    [InlineData("mailto:office@example.test?subject=Review%20report")]
    public void Safe_hyperlinks_allow_inspection_and_preserve_unrelated_links_styles_and_relationships_on_edit(string target)
    {
        var bytes = Fixture(target); var original = bytes.ToArray();
        using (var valid = WordprocessingDocument.Open(new MemoryStream(bytes), false))
            Assert.Empty(new DocumentFormat.OpenXml.Validation.OpenXmlValidator().Validate(valid));
        var inspection = processor.InspectDocument(bytes, 0, 100, default);
        Assert.Contains(inspection.Elements, e => e.Text == "Business hyperlink");
        var selector = inspection.Elements.Single(e => e.Text == "Editable paragraph.").Selector;
        var before = Parts(bytes);
        var edited = processor.EditDocument(bytes, [new("replace_paragraph", selector, "Updated paragraph.")], default);
        var after = Parts(edited.Bytes);
        Assert.Contains(processor.InspectDocument(edited.Bytes, 0, 100, default).Elements, e => e.Text == "Updated paragraph.");
        Assert.Equal(before.Relationships, after.Relationships);
        Assert.Equal(before.Hyperlink, after.Hyperlink);
        Assert.Equal(before.StyledParagraph, after.StyledParagraph);
        Assert.Equal(before.Styles, after.Styles);
        Assert.Equal(original, bytes);
        var hyperlinkSelector = inspection.Elements.Single(e => e.Text == "Business hyperlink").Selector;
        var error = Assert.Throws<AgentCoreException>(() => processor.EditDocument(bytes, [new("replace_paragraph", hyperlinkSelector, "Must not replace link.")], default));
        Assert.StartsWith("office_operation_unsupported:", error.Message);
        Assert.Equal(original, bytes);
    }

    [Fact]
    public void Word_processing_does_not_connect_to_hyperlink_targets()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var bytes = Fixture($"http://127.0.0.1:{port}/must-not-fetch");
        var inspection = processor.InspectDocument(bytes, 0, 100, default);
        processor.EditDocument(bytes, [new("replace_paragraph", inspection.Elements.Single(e => e.Text == "Editable paragraph.").Selector, "Edited offline")], default);
        Assert.False(listener.Pending());
    }

    [Fact]
    public void Header_hyperlinks_are_preserved_and_strict_standard_relationship_type_is_accepted()
    {
        var bytes = Fixture("https://example.test/header", header: true);
        var inspection = processor.InspectDocument(bytes, 0, 100, default);
        var edited = processor.EditDocument(bytes, [new("replace_paragraph", inspection.Elements.Single(e => e.Text == "Editable paragraph.").Selector, "Edit body only")], default);
        using var before = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        using var after = WordprocessingDocument.Open(new MemoryStream(edited.Bytes), false);
        Assert.Equal(before.MainDocumentPart!.HeaderParts.Single().Header!.OuterXml, after.MainDocumentPart!.HeaderParts.Single().Header!.OuterXml);
        Assert.Equal(before.MainDocumentPart.HeaderParts.Single().HyperlinkRelationships.Single().Uri,
            after.MainDocumentPart.HeaderParts.Single().HyperlinkRelationships.Single().Uri);
        var strict = ChangeRelationship(Fixture("https://example.test/strict"), type: "http://purl.oclc.org/ooxml/officeDocument/relationships/hyperlink");
        OfficePackageGuard.Validate(strict, "word", default);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,evil")]
    [InlineData("file:///tmp/secret")]
    [InlineData("ftp://example.test/file")]
    [InlineData("//example.test/file")]
    [InlineData("relative/path")]
    [InlineData("https://user:password@example.test/")]
    [InlineData("https://example.test\\evil")]
    [InlineData("https://example.test/%0d%0aheader")]
    [InlineData("https://example.test/unsafe path")]
    [InlineData("mailto:")]
    [InlineData("mailto:not-an-email")]
    [InlineData("mailto:office@example.test,review@example.test")]
    [InlineData("mailto:office@example.test?subject=x%0d%0aBcc:other@example.test")]
    public void Unsafe_hyperlink_targets_remain_unsupported(string target)
    {
        var bytes = ChangeRelationship(Fixture("https://example.test/"), target: target);
        Assert.Equal("office_feature_unsupported", Assert.Throws<AgentCoreException>(() => processor.InspectDocument(bytes, 0, 10, default)).Code);
    }

    [Theory]
    [InlineData("attachedTemplate")]
    [InlineData("image")]
    [InlineData("oleObject")]
    [InlineData("aFChunk")]
    [InlineData("package")]
    [InlineData("notHyperlink")]
    public void Other_external_relationship_types_remain_unsupported(string type)
    {
        var bytes = ChangeRelationship(Fixture("https://example.test/"), type: "http://schemas.openxmlformats.org/officeDocument/2006/relationships/" + type);
        Assert.Equal("office_feature_unsupported", Assert.Throws<AgentCoreException>(() => processor.InspectDocument(bytes, 0, 10, default)).Code);
    }

    [Theory]
    [InlineData("_rels/.rels")]
    [InlineData("customXml/_rels/item.xml.rels")]
    [InlineData("word/_rels/missing.xml.rels")]
    [InlineData("word/_rels/styles.xml.rels")]
    [InlineData("word/invalid.rels")]
    public void Hyperlinks_are_not_admitted_from_invalid_or_non_Word_relationship_parts(string part)
    {
        var bytes = Fixture("https://example.test/");
        using var stream = new MemoryStream(); stream.Write(bytes);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, true))
        {
            zip.GetEntry(part)?.Delete();
            using var writer = new StreamWriter(zip.CreateEntry(part).Open());
            writer.Write($"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"unsafe\" Type=\"{HyperlinkType}\" TargetMode=\"External\" Target=\"https://example.test/\" /></Relationships>");
        }
        Assert.Equal("office_feature_unsupported", Assert.Throws<AgentCoreException>(() => OfficePackageGuard.Validate(stream.ToArray(), "word", default)).Code);
    }

    [Fact]
    public void Invalid_namespace_missing_mode_and_oversized_targets_do_not_open_an_external_exception()
    {
        foreach (var bytes in new[] {
            ChangeRelationship(Fixture("https://example.test/"), relationshipNamespace: "urn:invalid"),
            ChangeRelationship(Fixture("https://example.test/"), externalMode: false),
            ChangeRelationship(Fixture("https://example.test/"), target: "https://example.test/" + new string('a', 2048)) })
            Assert.Equal("office_feature_unsupported", Assert.Throws<AgentCoreException>(() => OfficePackageGuard.Validate(bytes, "word", default)).Code);
    }

    [Fact]
    public void Spreadsheet_hyperlinks_are_still_blocked_by_strict_and_sparse_profiles()
    {
        using var stream = new MemoryStream(); stream.Write(processor.CreateWorkbook(new([new("Sheet", [[new("number", Number: 0)]])]), default).Bytes);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("xl/worksheets/_rels/sheet1.xml.rels").Open());
            writer.Write($"<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"link\" Type=\"{HyperlinkType}\" TargetMode=\"External\" Target=\"https://example.test/\" /></Relationships>");
        }
        Assert.Equal("office_feature_unsupported", Assert.Throws<AgentCoreException>(() => OfficePackageGuard.Validate(stream.ToArray(), "xl", default)).Code);
        Assert.Equal("office_feature_unsupported", Assert.Throws<AgentCoreException>(() => OfficePackageGuard.ValidateSparseWorkbook(stream.ToArray(), default)).Code);
    }

    private byte[] Fixture(string target, bool header = false)
    {
        using var stream = new MemoryStream(); stream.Write(processor.CreateDocument(new(null, [new(null, ["Editable paragraph.", "Styled paragraph."], null)]), default).Bytes);
        using (var document = WordprocessingDocument.Open(stream, true))
        {
            var main = document.MainDocumentPart!;
            var styled = main.Document!.Body!.Elements<Paragraph>().Last();
            styled.ParagraphProperties = new ParagraphProperties(new ParagraphStyleId { Val = "Heading2" });
            styled.GetFirstChild<Run>()!.RunProperties = new RunProperties(new Italic(), new Color { Val = "123456" });
            var paragraph = new Paragraph(new Hyperlink(new Run(new Text("Business hyperlink"))) { Id = "inert-link" });
            if (header)
            {
                var part = main.AddNewPart<HeaderPart>();
                part.AddHyperlinkRelationship(new Uri(target), true, "inert-link"); part.Header = new Header(paragraph);
                main.Document!.Body.GetFirstChild<SectionProperties>()!.PrependChild(new HeaderReference { Type = HeaderFooterValues.Default, Id = main.GetIdOfPart(part) });
                part.Header.Save();
            }
            else
            {
                main.AddHyperlinkRelationship(new Uri(target), true, "inert-link");
                main.Document!.Body.InsertBefore(paragraph, main.Document!.Body.GetFirstChild<SectionProperties>());
            }
            main.Document!.Save();
        }
        return stream.ToArray();
    }

    private static byte[] ChangeRelationship(byte[] bytes, string? target = null, string? type = null, string? relationshipNamespace = null, bool externalMode = true)
    {
        using var stream = new MemoryStream(); stream.Write(bytes);
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Update, true))
        {
            var entry = zip.GetEntry("word/_rels/document.xml.rels")!; XDocument xml;
            using (var input = entry.Open()) xml = XDocument.Load(input);
            var relationship = xml.Root!.Elements().Single(e => e.Attribute("Id")?.Value == "inert-link");
            if (target is not null) relationship.SetAttributeValue("Target", target);
            if (type is not null) relationship.SetAttributeValue("Type", type);
            if (!externalMode) relationship.SetAttributeValue("TargetMode", null);
            if (relationshipNamespace is not null)
            {
                xml.Root.Name = XName.Get("Relationships", relationshipNamespace);
                xml.Root.SetAttributeValue("xmlns", relationshipNamespace);
                foreach (var child in xml.Root.Elements()) child.Name = XName.Get("Relationship", relationshipNamespace);
            }
            entry.Delete(); using var output = zip.CreateEntry("word/_rels/document.xml.rels").Open(); xml.Save(output);
        }
        return stream.ToArray();
    }

    private static (string Relationships, string Hyperlink, string StyledParagraph, string Styles) Parts(byte[] bytes)
    {
        using var document = WordprocessingDocument.Open(new MemoryStream(bytes), false);
        var main = document.MainDocumentPart!;
        var links = string.Join("\n", main.HyperlinkRelationships.Select(r => $"{r.Id}|{r.Uri}|{r.IsExternal}|{r.RelationshipType}"));
        return (links, main.Document!.Descendants<Hyperlink>().Single().OuterXml,
            main.Document!.Body!.Elements<Paragraph>().Single(p => p.InnerText == "Styled paragraph.").OuterXml, main.StyleDefinitionsPart!.Styles!.OuterXml);
    }
}
