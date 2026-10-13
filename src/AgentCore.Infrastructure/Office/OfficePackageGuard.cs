using System.IO.Compression;
using System.Xml;
using SkiaSharp;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Office;

internal static class OfficePackageGuard
{
    public static void Validate(ReadOnlyMemory<byte> bytes, string family, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (bytes.Length > OfficeLimits.MaxPackageBytes || bytes.Length < 4) throw AgentCoreErrors.Validation("invalid_office_package: Package size is outside processing limits.");
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count > OfficeLimits.MaxEntries) throw AgentCoreErrors.Validation("invalid_office_package: Too many package entries.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0; var cells = 0; var sheets = 0; var paragraphs = 0; var documentCells = 0; var images = 0; var pageBreaks = 0; var sections = 0; long imagePixels = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || entry.FullName.Any(char.IsControl) || !names.Add(entry.FullName) || entry.FullName.StartsWith('/') || entry.FullName.Contains('\\') || entry.FullName.Split('/').Any(p => p is "." or "..")
                    || entry.FullName.Length > 256 || entry.FullName.Contains("vba", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.Contains("macrosheets/", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("externalLinks/", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith("connections.xml", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("queryTables/", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("embeddings/", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".odttf", StringComparison.OrdinalIgnoreCase))
                    throw AgentCoreErrors.Validation("invalid_office_package: Unsafe or unsupported package entry.");
                expanded = checked(expanded + entry.Length);
                if (expanded > OfficeLimits.MaxExpandedBytes || entry.Length > 4 * 1024 * 1024)
                    throw AgentCoreErrors.Validation("invalid_office_package: Expanded package exceeds limits.");
                if (entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase))
                {
                    using var imageStream = entry.Open(); using var codec = SKCodec.Create(imageStream);
                    if (++images > OfficeLimits.MaxDocumentImages || codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 40_000_000 || codec.Info.Width > 32768 || codec.Info.Height > 32768)
                        throw AgentCoreErrors.Validation("invalid_office_package: Embedded image dimensions/type are unsafe or unsupported.");
                    imagePixels += (long)codec.Info.Width * codec.Info.Height;
                    if (imagePixels > OfficeLimits.MaxDocumentImagePixels) throw AgentCoreErrors.Validation("invalid_office_package: Total embedded image pixels exceed document limits.");
                }
                if (!(entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))) continue;
                using var input = entry.Open();
                using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                var nodes = 0;
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (++nodes > 250000 || reader.Depth > 64) throw AgentCoreErrors.Validation("invalid_office_package: XML exceeds structural limits.");
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.LocalName == "Relationship")
                    {
                        var target = reader.GetAttribute("Target") ?? "";
                        if (reader.GetAttribute("TargetMode") == "External" || (!target.StartsWith('/') && Uri.TryCreate(target, UriKind.Absolute, out _))
                            || target.StartsWith("//", StringComparison.Ordinal))
                            throw AgentCoreErrors.Validation("invalid_office_package: External relationships are unsupported.");
                    }
                    if (reader.LocalName == "Override" && (reader.GetAttribute("ContentType") ?? "").Contains("macro", StringComparison.OrdinalIgnoreCase))
                        throw AgentCoreErrors.Validation("invalid_office_package: Macro-enabled documents are unsupported.");
                    if (family == "xl" && reader.LocalName == "sheet" && ++sheets > OfficeLimits.MaxSheets) throw AgentCoreErrors.Validation("invalid_office_package: Too many worksheets.");
                    if (family == "xl" && reader.LocalName == "c" && ++cells > OfficeLimits.MaxCells)
                        throw AgentCoreErrors.Validation("invalid_office_package: Workbook exceeds 50,000 cells.");
                    if (family == "word" && (reader.LocalName is "lastRenderedPageBreak" or "pageBreakBefore" || reader.LocalName == "br" && reader.GetAttribute("type", "http://schemas.openxmlformats.org/wordprocessingml/2006/main") == "page") && ++pageBreaks > OfficeLimits.MaxDocumentPageBreaks)
                        throw AgentCoreErrors.Validation("invalid_office_package: Too many explicit document page breaks.");
                    if (family == "word" && reader.LocalName == "sectPr" && ++sections > 50) throw AgentCoreErrors.Validation("invalid_office_package: Too many document sections.");
                    if (family == "word" && (reader.LocalName == "p" && ++paragraphs > OfficeLimits.MaxDocumentParagraphs || reader.LocalName == "tc" && ++documentCells > OfficeLimits.MaxDocumentCells))
                        throw AgentCoreErrors.Validation("invalid_office_package: Document exceeds paragraph/table-cell rendering limits.");
                    if (family == "word" && reader.LocalName is "altChunk" or "object")
                        throw AgentCoreErrors.Validation("invalid_office_package: Embedded content is unsupported.");
                }
            }
            if (!names.Contains("[Content_Types].xml") || !names.Contains(family == "word" ? "word/document.xml" : "xl/workbook.xml"))
                throw AgentCoreErrors.Validation("invalid_office_package: The package does not match the requested Office type.");
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or OverflowException)
        { throw AgentCoreErrors.Validation("invalid_office_package: The package is corrupt or unsafe."); }
    }
    internal static void RequireEditableWorkbook(ReadOnlyMemory<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), false); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Any(entry => entry.FullName.StartsWith("xl/charts/", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.StartsWith("xl/pivot", StringComparison.OrdinalIgnoreCase) || entry.FullName.StartsWith("xl/drawings/", StringComparison.OrdinalIgnoreCase)))
            throw AgentCoreErrors.Validation("office_operation_unsupported: This workbook has unsupported charts, pivots or drawings; editing would risk feature loss.");
    }

}
