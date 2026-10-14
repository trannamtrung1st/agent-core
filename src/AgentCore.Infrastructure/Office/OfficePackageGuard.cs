using System.IO.Compression;
using System.Xml;
using SkiaSharp;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Office;

internal static class OfficePackageGuard
{
    internal sealed record SparseSheetScan(int StoredCells, IReadOnlyList<string> TextAddresses, IReadOnlySet<string> MissingFormulaCaches, bool HasFormatting);
    public static void Validate(ReadOnlyMemory<byte> bytes, string family, CancellationToken ct) => ValidateCore(bytes, family, ct, false);
    internal static IReadOnlyDictionary<string, SparseSheetScan> ValidateSparseWorkbook(ReadOnlyMemory<byte> bytes, CancellationToken ct) => ValidateCore(bytes, "xl", ct, true);
    private static IReadOnlyDictionary<string, SparseSheetScan> ValidateCore(ReadOnlyMemory<byte> bytes, string family, CancellationToken ct, bool sparse)
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
            var sheetIds = new Dictionary<string, string>();
            var relationships = new Dictionary<string, string>();
            var scans = new Dictionary<string, SparseSheetScan>();
            var wordHyperlinkParts = new HashSet<string>(StringComparer.Ordinal);
            var populated = 0; long formulaCells = 0; var packageNodes = 0; var sharedStrings = 0;
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                var sparseXml = sparse && entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase);
                var worksheetPart = false;
                var partLimit = sparseXml ? OfficeLimits.MaxSparsePartBytes : 4 * 1024 * 1024;
                if (((entry.ExternalAttributes >> 16) & 0xF000) == 0xA000 || entry.FullName.Any(char.IsControl) || !names.Add(entry.FullName) || entry.FullName.StartsWith('/') || entry.FullName.Contains('\\') || entry.FullName.Split('/').Any(p => p is "." or "..")
                    || entry.FullName.Length > 256)
                    throw AgentCoreErrors.Validation("invalid_office_package: Unsafe package entry.");
                if (entry.FullName.Contains("vba", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".bin", StringComparison.OrdinalIgnoreCase)
                    || entry.FullName.Contains("macrosheets/", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("externalLinks/", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith("connections.xml", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("queryTables/", StringComparison.OrdinalIgnoreCase) || entry.FullName.Contains("embeddings/", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || entry.FullName.EndsWith(".odttf", StringComparison.OrdinalIgnoreCase))
                    throw new AgentCoreException("office_feature_unsupported", "This package contains a feature the Office reader does not support; this does not establish corruption.", 400);
                expanded = checked(expanded + entry.Length);
                if (expanded > OfficeLimits.MaxExpandedBytes || entry.Length > partLimit)
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
                using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = partLimit });
                var nodes = 0; var storedCells = 0; var cellDepth = -1; var formulaDepth = -1; System.Text.StringBuilder? formulaText = null; var valueDepth = -1; var cellContent = false; string? cellAddress = null;
                var rowNumber = 0; var lastColumn = 0; string? cellType = null; var textCell = false; var hasFormula = false; var hasCache = false; var hasFormatting = false;
                var textAddresses = new List<string>(); var missingCaches = new HashSet<string>(StringComparer.Ordinal);
                var standardRelationships = false;
                void FinishCell()
                {
                    if (hasFormula && !hasCache && cellAddress is not null) missingCaches.Add(cellAddress);
                    cellDepth = -1; cellAddress = null; hasFormula = false; hasCache = false;
                }
                void CountContent()
                {
                    if (cellContent) return;
                    cellContent = true;
                    if (++populated + formulaCells > OfficeLimits.MaxCells) throw new AgentCoreException("office_processing_limit", "Workbook exceeds 50,000 populated cells.", 400);
                    if (textCell && cellAddress is not null) textAddresses.Add(cellAddress);
                }
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    if (reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
                        standardRelationships = reader.LocalName == "Relationships" && reader.NamespaceURI == "http://schemas.openxmlformats.org/package/2006/relationships";
                    if (sparseXml && reader.NodeType == XmlNodeType.Element && reader.Depth == 0)
                    {
                        worksheetPart = reader.LocalName == "worksheet" && reader.NamespaceURI == "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
                        if (!worksheetPart && entry.Length > 4 * 1024 * 1024)
                            throw new AgentCoreException("office_processing_limit", "Non-worksheet part exceeds 4 MiB.", 400);
                    }
                    if (++nodes > (worksheetPart ? OfficeLimits.MaxSparsePartNodes : 250000) || reader.Depth > 64
                        || sparse && ++packageNodes > OfficeLimits.MaxSparsePackageNodes) throw new AgentCoreException("office_processing_limit", "XML exceeds structural limits. This is a processing limit, not evidence of corruption.", 400);
                    if (worksheetPart && formulaDepth >= 0 && reader.Depth > formulaDepth && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA)
                        formulaText!.Append(reader.Value);
                    if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == formulaDepth)
                    {
                        RequireLocalFormula(formulaText!.ToString()); formulaDepth = -1; formulaText = null;
                    }
                    if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == cellDepth) FinishCell();
                    if (worksheetPart && valueDepth >= 0 && reader.Depth > valueDepth
                        && reader.NodeType is XmlNodeType.Text or XmlNodeType.CDATA && !string.IsNullOrWhiteSpace(reader.Value)) CountContent();
                    if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == valueDepth) valueDepth = -1;
                    if (reader.NodeType != XmlNodeType.Element) continue;
                    if (sparse)
                    {
                        if (entry.FullName == "xl/workbook.xml" && reader.LocalName == "sheet")
                            sheetIds.Add(reader.GetAttribute("name") ?? "", reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships") ?? "");
                        if (entry.FullName == "xl/_rels/workbook.xml.rels" && reader.LocalName == "Relationship")
                        {
                            var target = reader.GetAttribute("Target") ?? "";
                            relationships.Add(reader.GetAttribute("Id") ?? "", new Uri(new Uri("http://package/xl/workbook.xml"), target).AbsolutePath.TrimStart('/'));
                        }
                        if (reader.LocalName == "si" && ++sharedStrings > OfficeLimits.MaxCells)
                            throw new AgentCoreException("office_processing_limit", "Shared string table exceeds 50,000 entries.", 400);
                        if (worksheetPart && reader.LocalName == "row")
                        {
                            rowNumber = reader.GetAttribute("r") is string row ? int.Parse(row, System.Globalization.CultureInfo.InvariantCulture) : rowNumber + 1;
                            lastColumn = 0;
                        }
                        if (worksheetPart && (reader.GetAttribute("s") is string style && style != "0"
                            || reader.LocalName == "col" && reader.GetAttribute("style") is string columnStyle && columnStyle != "0")) hasFormatting = true;
                        if (worksheetPart && reader.LocalName == "c")
                        {
                            storedCells++; cellDepth = reader.IsEmptyElement ? -1 : reader.Depth; cellContent = false;
                            hasFormula = false; hasCache = false; valueDepth = -1;
                            cellAddress = reader.GetAttribute("r");
                            if (cellAddress is not null) lastColumn = AddressColumn(cellAddress);
                            else cellAddress = AgentCore.Application.Office.OfficeReadResults.Address(rowNumber, ++lastColumn);
                            cellType = reader.GetAttribute("t");
                            textCell = cellType is "s" or "str" or "inlineStr";
                        }
                        if (worksheetPart && cellDepth >= 0 && reader.LocalName is "f" or "v" or "is")
                        {
                            if (reader.LocalName == "f")
                            {
                                hasFormula = true;
                                if (!reader.IsEmptyElement) { formulaDepth = reader.Depth; formulaText = new(); }
                            }
                            if (reader.LocalName == "v")
                            {
                                hasCache = true; valueDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                                // An explicit string value can intentionally be empty. Shared strings
                                // require an index; inline strings require an actual <is> representation.
                                if (cellType == "str") CountContent();
                            }
                            else CountContent();
                        }
                        // ClosedXML materializes array formula ranges, even when few <c> records exist.
                        if (worksheetPart && reader.LocalName == "f" && reader.GetAttribute("ref") is string formulaRange)
                        {
                            formulaCells += ValidateFormulaRange(formulaRange);
                            if (formulaCells + populated > OfficeLimits.MaxCells) throw new AgentCoreException("office_processing_limit", "Formula ranges exceed the workbook materialization budget.", 400);
                        }
                    }
                    if (reader.LocalName == "Relationship")
                    {
                        var target = reader.GetAttribute("Target") ?? "";
                        if (reader.GetAttribute("TargetMode") == "External" || (!target.StartsWith('/') && Uri.TryCreate(target, UriKind.Absolute, out _))
                            || target.StartsWith("//", StringComparison.Ordinal))
                        {
                            var inertWordHyperlink = family == "word" && standardRelationships && reader.Depth == 1
                                && reader.NamespaceURI == "http://schemas.openxmlformats.org/package/2006/relationships"
                                && reader.GetAttribute("TargetMode") == "External"
                                && (reader.GetAttribute("Type") is "http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink"
                                    or "http://purl.oclc.org/ooxml/officeDocument/relationships/hyperlink")
                                && !string.IsNullOrWhiteSpace(reader.GetAttribute("Id")) && SafeWordHyperlinkTarget(target)
                                && (wordHyperlinkParts.Contains(entry.FullName) || IsWordContentRelationshipPart(zip, entry.FullName, ct));
                            if (!inertWordHyperlink)
                                throw new AgentCoreException("office_feature_unsupported", "External relationships other than safe inert Word hyperlinks are unsupported. This does not establish corruption.", 400);
                            wordHyperlinkParts.Add(entry.FullName);
                        }
                    }
                    if (reader.LocalName == "Override" && (reader.GetAttribute("ContentType") ?? "").Contains("macro", StringComparison.OrdinalIgnoreCase))
                        throw new AgentCoreException("office_feature_unsupported", "Macro-enabled documents are unsupported. This does not establish corruption.", 400);
                    if (family == "xl" && reader.LocalName == "sheet" && ++sheets > OfficeLimits.MaxSheets) throw new AgentCoreException("office_processing_limit", "Too many worksheets. This is a processing limit, not evidence of corruption.", 400);
                    if (family == "xl" && reader.LocalName == "c" && ++cells > (sparse ? OfficeLimits.MaxSparseScannedCells : OfficeLimits.MaxCells))
                        throw new AgentCoreException("office_processing_limit", "Workbook exceeds the stored-cell scanning limit. This is a processing limit, not evidence of corruption.", 400);
                    if (family == "word" && (reader.LocalName is "lastRenderedPageBreak" or "pageBreakBefore" || reader.LocalName == "br" && reader.GetAttribute("type", "http://schemas.openxmlformats.org/wordprocessingml/2006/main") == "page") && ++pageBreaks > OfficeLimits.MaxDocumentPageBreaks)
                        throw new AgentCoreException("office_processing_limit", "Too many explicit document page breaks. This is a processing limit, not evidence of corruption.", 400);
                    if (family == "word" && reader.LocalName == "sectPr" && ++sections > 50) throw new AgentCoreException("office_processing_limit", "Too many document sections. This is a processing limit, not evidence of corruption.", 400);
                    if (family == "word" && (reader.LocalName == "p" && ++paragraphs > OfficeLimits.MaxDocumentParagraphs || reader.LocalName == "tc" && ++documentCells > OfficeLimits.MaxDocumentCells))
                        throw new AgentCoreException("office_processing_limit", "Document exceeds paragraph/table-cell rendering limits. This is a processing limit, not evidence of corruption.", 400);
                    if (family == "word" && reader.LocalName is "altChunk" or "object")
                        throw new AgentCoreException("office_feature_unsupported", "Embedded content is unsupported. This does not establish corruption.", 400);
                }
                if (worksheetPart) scans.Add(entry.FullName, new(storedCells, textAddresses, missingCaches, hasFormatting));
            }
            if (!names.Contains("[Content_Types].xml") || !names.Contains(family == "word" ? "word/document.xml" : "xl/workbook.xml"))
                throw AgentCoreErrors.Validation("invalid_office_package: The package does not match the requested Office type.");
            var result = new Dictionary<string, SparseSheetScan>();
            foreach (var (name, id) in sheetIds)
            {
                if (!relationships.TryGetValue(id, out var part) || !names.Contains(part))
                    throw AgentCoreErrors.Validation("invalid_office_package: Missing worksheet relationship.");
                if (!scans.TryGetValue(part, out var scan))
                    throw new AgentCoreException("office_feature_unsupported", "Non-worksheet sheet kinds are unsupported by sparse reading.", 400);
                result.Add(name, scan);
            }
            return result;
        }
        catch (Exception ex) when (ex is InvalidDataException or XmlException or IOException or OverflowException or ArgumentException or FormatException)
        { throw AgentCoreErrors.Validation("invalid_office_package: The package is corrupt or unsafe."); }
    }
    private static bool SafeWordHyperlinkTarget(string target)
    {
        if (target.Length is < 1 or > 2048 || target.Any(char.IsWhiteSpace) || target.Any(char.IsControl) || target.Contains('\\')
            || !Uri.TryCreate(target, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || Uri.UnescapeDataString(target).Any(char.IsControl)) return false;
        if (uri.Scheme is "http" or "https") return !string.IsNullOrEmpty(uri.Host) && string.IsNullOrEmpty(uri.UserInfo);
        if (uri.Scheme != "mailto") return false;
        var recipients = Uri.UnescapeDataString(target[(target.IndexOf(':') + 1)..].Split('?')[0]);
        return System.Net.Mail.MailAddress.TryCreate(recipients, out var parsed)
            && parsed.Address == recipients && recipients.Contains('@');
    }

    private static bool IsWordContentRelationshipPart(ZipArchive zip, string relationshipPart, CancellationToken ct)
    {
        var marker = relationshipPart.LastIndexOf("/_rels/", StringComparison.Ordinal);
        if (!relationshipPart.StartsWith("word/", StringComparison.Ordinal) || marker < 4 || !relationshipPart.EndsWith(".xml.rels", StringComparison.Ordinal)) return false;
        var name = relationshipPart[(marker + 7)..^5];
        if (name.Contains('/')) return false;
        var owner = zip.GetEntry(relationshipPart[..(marker + 1)] + name);
        if (owner is null) return false;
        if (owner.Length > 4 * 1024 * 1024) throw new AgentCoreException("office_processing_limit", "Word relationship owner exceeds part limits.", 400);
        using var input = owner.Open();
        using var reader = XmlReader.Create(input, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 4 * 1024 * 1024 });
        var nodes = 0;
        while (reader.Read())
        {
            ct.ThrowIfCancellationRequested();
            if (++nodes > 250000) throw new AgentCoreException("office_processing_limit", "Word relationship owner exceeds structural limits.", 400);
            if (reader.NodeType == XmlNodeType.Element)
                return reader.Depth == 0 && (reader.NamespaceURI is "http://schemas.openxmlformats.org/wordprocessingml/2006/main" or "http://purl.oclc.org/ooxml/wordprocessingml/main")
                    && (reader.LocalName is "document" or "hdr" or "ftr" or "footnotes" or "endnotes" or "comments");
        }
        return false;
    }

    internal static void RequireLocalFormula(string formula)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(formula, @"\[[^\]]+\][^!(),+\-*/<>=]*!", System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            throw new AgentCoreException("office_feature_unsupported", "External formula references are unsupported.", 400);
    }

    private static int AddressColumn(string address)
    {
        var column = 0;
        foreach (var letter in address)
        {
            if (!char.IsLetter(letter)) break;
            column = checked(column * 26 + char.ToUpperInvariant(letter) - 'A' + 1);
        }
        return column;
    }

    private static long ValidateFormulaRange(string range)
    {
        var pieces = range.Split(':');
        if (pieces.Length > 2) throw AgentCoreErrors.Validation("invalid_office_package: Invalid formula range.");
        static (int Row, int Column) Point(string text)
        {
            var match = System.Text.RegularExpressions.Regex.Match(text, @"^([A-Za-z]{1,3})([1-9][0-9]{0,6})$");
            if (!match.Success) throw AgentCoreErrors.Validation("invalid_office_package: Invalid formula range.");
            var column = 0; foreach (var c in match.Groups[1].Value.ToUpperInvariant()) column = column * 26 + c - 'A' + 1;
            var row = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
            if (row > 1048576 || column > 16384) throw AgentCoreErrors.Validation("invalid_office_package: Invalid formula range.");
            return (row, column);
        }
        var first = Point(pieces[0]); var last = Point(pieces[^1]);
        if (last.Row < first.Row || last.Column < first.Column) throw AgentCoreErrors.Validation("invalid_office_package: Invalid formula range.");
        var area = (long)(last.Row - first.Row + 1) * (last.Column - first.Column + 1);
        if (area > OfficeLimits.MaxCells)
            throw new AgentCoreException("office_processing_limit", "Formula range exceeds the populated-cell materialization budget.", 400);
        return area;
    }

    internal static void RequireEditableWorkbook(ReadOnlyMemory<byte> bytes)
    {
        using var stream = new MemoryStream(bytes.ToArray(), false); using var zip = new ZipArchive(stream, ZipArchiveMode.Read);
        if (zip.Entries.Any(entry => entry.FullName.StartsWith("xl/charts/", StringComparison.OrdinalIgnoreCase)
            || entry.FullName.StartsWith("xl/pivot", StringComparison.OrdinalIgnoreCase) || entry.FullName.StartsWith("xl/drawings/", StringComparison.OrdinalIgnoreCase)))
            throw AgentCoreErrors.Validation("office_operation_unsupported: This workbook has unsupported charts, pivots or drawings; editing would risk feature loss.");
    }

}
