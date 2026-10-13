using System.Globalization;
using System.Security.Cryptography;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Office;

public sealed partial class OfficeProcessor : IOfficeProcessor
{
    internal static string Hash(ReadOnlyMemory<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes.Span));
    private static string TextHash(string text) => Hash(System.Text.Encoding.UTF8.GetBytes(text))[..16];
    private static readonly string[] DocumentWarnings = ["Read-only preview is best effort. Additional Office layout/features may be present in the original file."];
    private static void TextLimit(string? value) { if (value?.Length > 16000 || value?.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')) == true) throw AgentCoreErrors.Validation("office_operation_unsupported: Text exceeds supported limits."); }
    private static Paragraph Paragraph(string text, string? style = null)
    {
        TextLimit(text);
        var paragraph = new Paragraph(new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve }));
        if (style is not null) paragraph.ParagraphProperties = new ParagraphProperties(new ParagraphStyleId { Val = style });
        return paragraph;
    }
    private static void AddSection(Body body, OfficeSection section)
    {
        if (section.PageBreakBefore) body.Append(new Paragraph(new Run(new Break { Type = BreakValues.Page })));
        if (section.Heading is not null) body.Append(Paragraph(section.Heading, "Heading1"));
        if (section.Paragraphs.Count > 100) throw AgentCoreErrors.Validation("office_operation_unsupported: Too many paragraphs.");
        foreach (var text in section.Paragraphs) body.Append(Paragraph(text));
        if (section.Table is null || section.Table.Count == 0) return;
        if (section.Table.Count > 200 || section.Table.Any(row => row.Count > 20)) throw AgentCoreErrors.Validation("office_operation_unsupported: Table exceeds supported limits.");
        var table = new Table(new TableProperties(new TableBorders(new TopBorder { Val = BorderValues.Single, Size = 4 }, new LeftBorder { Val = BorderValues.Single, Size = 4 }, new BottomBorder { Val = BorderValues.Single, Size = 4 }, new RightBorder { Val = BorderValues.Single, Size = 4 }, new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4 }, new InsideVerticalBorder { Val = BorderValues.Single, Size = 4 })));
        var columns = section.Table.Max(row => row.Count);
        table.Append(new TableGrid(Enumerable.Range(0, columns).Select(_ => new GridColumn { Width = (8000 / Math.Max(1, columns)).ToString(CultureInfo.InvariantCulture) })));
        foreach (var row in section.Table) table.Append(new TableRow(row.Select(text => new TableCell(Paragraph(text))).ToArray()));
        body.Append(table);
    }
    private OfficeProcessedFile CreateDocumentCore(OfficeDocumentSpec spec, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (spec.Sections.Count is < 1 or > 20) throw AgentCoreErrors.Validation("office_operation_unsupported: Supply 1 to 20 sections.");
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, true))
        {
            var main = document.AddMainDocumentPart(); main.Document = new Document(new Body());
            var styles = main.AddNewPart<StyleDefinitionsPart>();
            styles.Styles = new Styles(new Style(new StyleName { Val = "Normal" }) { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true });
            foreach (var (id, size) in new[] { ("Title", "36"), ("Heading1", "28"), ("Heading2", "24"), ("Heading3", "22") })
                styles.Styles.Append(new Style(new StyleName { Val = id }, new BasedOn { Val = "Normal" }, new StyleRunProperties(new Bold(), new FontSize { Val = size })) { Type = StyleValues.Paragraph, StyleId = id });
            var body = main.Document.Body!;
            if (spec.Title is not null) body.Append(Paragraph(spec.Title, "Title"));
            foreach (var section in spec.Sections) { ct.ThrowIfCancellationRequested(); AddSection(body, section); }
            body.Append(new SectionProperties(new PageSize { Width = 11906, Height = 16838 }, new PageMargin { Top = 1134, Bottom = 1134, Left = 1134, Right = 1134, Header = 567, Footer = 567, Gutter = 0 }));
            main.Document.Save();
        }
        var bytes = stream.ToArray(); OfficePackageGuard.Validate(bytes, "word", ct); return new(bytes, []);
    }
    private OfficeDocumentInspection InspectDocumentCore(ReadOnlyMemory<byte> bytes, int offset, int limit, CancellationToken ct)
    {
        OfficePackageGuard.Validate(bytes, "word", ct);
        if (offset < 0 || limit is < 1 or > 100) throw AgentCoreErrors.Validation("office_operation_unsupported: Invalid document page.");
        using var stream = new MemoryStream(bytes.ToArray(), false); using var document = WordprocessingDocument.Open(stream, false);
        var body = document.MainDocumentPart?.Document?.Body ?? throw AgentCoreErrors.Validation("invalid_office_package: Document body is absent.");
        var elements = new List<OfficeDocumentElement>(); var paragraphIndex = 0; var tableIndex = 0;
        foreach (var node in body.ChildElements)
        {
            ct.ThrowIfCancellationRequested();
            if (node is Paragraph paragraph)
            {
                var text = paragraph.InnerText; TextLimit(text);
                elements.Add(new($"p:{paragraphIndex++}:{TextHash(text)}", paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value ?? "Paragraph", text));
            }
            else if (node is Table table)
            {
                var rows = table.Elements<TableRow>().Select(row => (IReadOnlyList<string>)row.Elements<TableCell>().Select(cell => cell.InnerText).ToArray()).ToArray();
                if (rows.Length > 200 || rows.Any(row => row.Count > 20)) throw AgentCoreErrors.Validation("office_operation_unsupported: Table exceeds inspection limits.");
                foreach (var row in rows) foreach (var cell in row) TextLimit(cell);
                elements.Add(new($"t:{tableIndex++}:{TextHash(table.InnerText)}", "Table", "", rows));
            }
            if (elements.Count > 2000) throw AgentCoreErrors.Validation("office_operation_unsupported: Document exceeds structural limits.");
        }
        // Bound the excerpt independently of package limits and execution checkpoint budgets.
        var page = new List<OfficeDocumentElement>(); var chars = 0;
        foreach (var element in elements.Skip(offset).Take(limit))
        {
            var size = element.Text.Length + (element.Rows?.Sum(row => row.Sum(cell => cell.Length)) ?? 0);
            if (chars + size > 64000) break;
            page.Add(element); chars += size;
        }
        return new(Hash(bytes), page, elements.Count, offset + page.Count < elements.Count, DocumentWarnings);
    }
    private OfficeProcessedFile EditDocumentCore(ReadOnlyMemory<byte> bytes, IReadOnlyList<OfficeDocumentEdit> operations, CancellationToken ct)
    {
        OfficePackageGuard.Validate(bytes, "word", ct);
        if (operations.Count is < 1 or > OfficeLimits.MaxOperations) throw AgentCoreErrors.Validation("office_operation_unsupported: Supply 1 to 20 operations.");
        using var stream = new MemoryStream(); stream.Write(bytes.Span); stream.Position = 0;
        var warnings = new List<string>();
        using (var document = WordprocessingDocument.Open(stream, true))
        {
            var body = document.MainDocumentPart?.Document?.Body ?? throw AgentCoreErrors.Validation("invalid_office_package: Document body is absent.");
            if (document.MainDocumentPart?.DocumentSettingsPart?.Settings?.GetFirstChild<DocumentProtection>()?.Enforcement?.Value == true) throw AgentCoreErrors.Validation("office_operation_unsupported: Protected documents cannot be edited.");
            // Resolve every inspected selector against the original body before any mutation.
            var targets = operations.Select(operation => (Operation: operation, Target: Resolve(body, operation.Selector))).ToArray();
            if (targets.Select(t => t.Target).Distinct().Count() != targets.Length) throw AgentCoreErrors.Validation("office_operation_unsupported: Repeated or ambiguous selectors.");
            foreach (var (operation, target) in targets)
            {
                ct.ThrowIfCancellationRequested();
                if (target is Paragraph paragraph)
                {
                    if (paragraph.Descendants().Any(node => node is Hyperlink or FieldCode or Drawing or SimpleField || node.LocalName is "pict" or "fldChar" or "sdt" or "ins" or "del" or "oMath" or "bookmarkStart" or "commentRangeStart")) throw AgentCoreErrors.Validation("office_operation_unsupported: Complex paragraphs cannot be edited.");
                    if (operation.Operation == "set_heading_style" && operation.Style is "Normal" or "Title" or "Heading1" or "Heading2" or "Heading3")
                    { paragraph.ParagraphProperties ??= new ParagraphProperties(); paragraph.ParagraphProperties.ParagraphStyleId = new ParagraphStyleId { Val = operation.Style }; }
                    else if (operation.Operation == "replace_paragraph" && operation.Text is not null)
                    {
                        TextLimit(operation.Text); var properties = paragraph.ParagraphProperties?.CloneNode(true);
                        paragraph.RemoveAllChildren(); if (properties is not null) paragraph.Append(properties);
                        paragraph.Append(new Run(new Text(operation.Text) { Space = SpaceProcessingModeValues.Preserve }));
                        warnings.Add("Replacement preserves paragraph properties; individual run formatting is normalized.");
                    }
                    else if (operation.Operation == "insert_paragraph_after" && operation.Text is not null) paragraph.InsertAfterSelf(Paragraph(operation.Text));
                    else throw AgentCoreErrors.Validation("office_operation_unsupported: Unsupported paragraph edit.");
                }
                else if (target is TableCell cell && operation.Operation == "replace_table_cell" && operation.Text is not null)
                {
                    if (cell.Descendants<Table>().Any() || cell.Descendants().Any(node => node is Drawing or FieldCode or Hyperlink || node.LocalName is "pict" or "fldChar" or "sdt" or "ins" or "del" or "oMath")) throw AgentCoreErrors.Validation("office_operation_unsupported: Complex cells cannot be edited.");
                    foreach (var childParagraph in cell.Elements<Paragraph>().ToArray()) childParagraph.Remove(); cell.Append(Paragraph(operation.Text));
                    warnings.Add("Cell text replacement normalizes paragraph/run formatting.");
                }
                else throw AgentCoreErrors.Validation("office_operation_unsupported: Unsupported document operation.");
            }
            document.MainDocumentPart!.Document.Save();
        }
        var updated = stream.ToArray(); OfficePackageGuard.Validate(updated, "word", ct); return new(updated, warnings.Distinct().ToArray());
    }
    private static OpenXmlElement Resolve(Body body, string selector)
    {
        var parts = selector.Split(':');
        if (parts.Length is not (3 or 5) || !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var index) || index < 0)
            throw AgentCoreErrors.Validation("office_operation_unsupported: Use a unique inspected selector.");
        if (parts[0] == "p" && parts.Length == 3)
        {
            var paragraph = body.Elements<Paragraph>().ElementAtOrDefault(index);
            if (paragraph is not null && TextHash(paragraph.InnerText) == parts[2]) return paragraph;
        }
        if (parts[0] == "t" && parts.Length == 5 && int.TryParse(parts[3], out var row) && int.TryParse(parts[4], out var column) && row >= 0 && column >= 0)
        {
            var table = body.Elements<Table>().ElementAtOrDefault(index);
            if (table is not null && TextHash(table.InnerText) == parts[2])
            { var cell = table.Elements<TableRow>().ElementAtOrDefault(row)?.Elements<TableCell>().ElementAtOrDefault(column); if (cell is not null) return cell; }
        }
        throw AgentCoreErrors.Validation("office_operation_unsupported: Selector is absent or stale; inspect the file again.");
    }
}
