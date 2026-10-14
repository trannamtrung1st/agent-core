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
        if (bytes.Length < 4) throw AgentCoreErrors.Validation("invalid_office_package: The package is incomplete.");
        if (bytes.Length > OfficeLimits.MaxPackageBytes) throw new AgentCoreException("office_processing_limit", "Package size is outside processing limits. This is a processing limit, not evidence of corruption.", 400);
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), false);
            using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
            if (zip.Entries.Count > OfficeLimits.MaxEntries) throw new AgentCoreException("office_processing_limit", "Too many package entries. This is a processing limit, not evidence of corruption.", 400);
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long expanded = 0; var cells = 0; var sheets = 0; var paragraphs = 0; var documentCells = 0; var images = 0; var pageBreaks = 0; var sections = 0; long imagePixels = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || entry.FullName.Any(char.IsControl) || !names.Add(entry.FullName) || entry.FullName.StartsWith('/') || entry.FullName.Contains('\\') || entry.FullName.Split('/').Any(p => p is "." or "..")
                    || entry.FullName.Length > 256)
                    throw AgentCoreErrors.Validation("invalid_office_package: Unsafe package entry.");
                if (entry.FullName.Contains("vba", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.Contains("macrosheets/", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("externalLinks/", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith("connections.xml", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("queryTables/", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("embeddings/", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".odttf", StringComparison.OrdinalIgnoreCase))
                    throw new AgentCoreException("office_feature_unsupported", "This package contains a feature the Office reader does not support; this does not establish corruption.", 400);
                expanded = checked(expanded + entry.Length);
                if (expanded > OfficeLimits.MaxExpandedBytes || entry.Length > 4 * 1024 * 1024)
                    throw new AgentCoreException("office_processing_limit", "Expanded package exceeds limits. This is a processing limit, not evidence of corruption.", 400);
                if (entry.FullName.StartsWith("word/media/", StringComparison.OrdinalIgnoreCase))
                {
                    using var imageStream = entry.Open(); using var codec = SKCodec.Create(imageStream);
                    if (++images > OfficeLimits.MaxDocumentImages || codec is null || codec.Info.Width <= 0 || codec.Info.Height <= 0 || (long)codec.Info.Width * codec.Info.Height > 40_000_000 || codec.Info.Width > 32768 || codec.Info.Height > 32768)
                        throw AgentCoreErrors.Validation("invalid_office_package: Embedded image dimensions/type are unsafe or unsupported.");
                    imagePixels += (long)codec.Info.Width * codec.Info.Height;
                    if (imagePixels > OfficeLimits.MaxDocumentImagePixels) throw new AgentCoreException("office_processing_limit", "Total embedded image pixels exceed document limits. This is a processing limit, not evidence of corruption.", 400);
                }
                if (!(entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".rels", StringComparison.OrdinalIgnoreCase))) continue;
                using var input = entry.Open();
                using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
                var nodes = 0;
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (++nodes > 250000 || reader.Depth > 64) throw new AgentCoreException("office_processing_limit", "XML exceeds structural limits. This is a processing limit, not evidence of corruption.", 400);
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (reader.LocalName == "Relationship")
                    {
                        var target = reader.GetAttribute("Target") ?? "";
                        if (reader.GetAttribute("TargetMode") == "External" || (!target.StartsWith('/') && Uri.TryCreate(target, UriKind.Absolute, out _))
                            || target.StartsWith("//", StringComparison.Ordinal))
                            throw new AgentCoreException("office_feature_unsupported", "External relationships are unsupported. This does not establish corruption.", 400);
                    }
                    if (reader.LocalName == "Override" && (reader.GetAttribute("ContentType") ?? "").Contains("macro", StringComparison.OrdinalIgnoreCase))
                        throw new AgentCoreException("office_feature_unsupported", "Macro-enabled documents are unsupported. This does not establish corruption.", 400);
                    if (family == "xl" && reader.LocalName == "sheet" && ++sheets > OfficeLimits.MaxSheets) throw new AgentCoreException("office_processing_limit", "Too many worksheets. This is a processing limit, not evidence of corruption.", 400);
                    if (family == "xl" && reader.LocalName == "c" && ++cells > OfficeLimits.MaxCells)
                        throw new AgentCoreException("office_processing_limit", "Workbook exceeds 50,000 cells. This is a processing limit, not evidence of corruption.", 400);
                    if (family == "word" && (reader.LocalName is "lastRenderedPageBreak" or "pageBreakBefore" || reader.LocalName == "br" && reader.GetAttribute("type", "http://schemas.openxmlformats.org/wordprocessingml/2006/main") == "page") && ++pageBreaks > OfficeLimits.MaxDocumentPageBreaks)
                        throw new AgentCoreException("office_processing_limit", "Too many explicit document page breaks. This is a processing limit, not evidence of corruption.", 400);
                    if (family == "word" && reader.LocalName == "sectPr" && ++sections > 50) throw new AgentCoreException("office_processing_limit", "Too many document sections. This is a processing limit, not evidence of corruption.", 400);
                    if (family == "word" && (reader.LocalName == "p" && ++paragraphs > OfficeLimits.MaxDocumentParagraphs || reader.LocalName == "tc" && ++documentCells > OfficeLimits.MaxDocumentCells))
                        throw new AgentCoreException("office_processing_limit", "Document exceeds paragraph/table-cell rendering limits. This is a processing limit, not evidence of corruption.", 400);
                    if (family == "word" && reader.LocalName is "altChunk" or "object")
                        throw new AgentCoreException("office_feature_unsupported", "Embedded content is unsupported. This does not establish corruption.", 400);
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
