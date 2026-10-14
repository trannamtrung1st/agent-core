using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Api;

/// <summary>Host-level operational policy. Immutable processing guards remain independent.</summary>
public sealed class FilePreviewOptions
{
    public const string SectionName = "FilePreview";
    public PreviewByteLimits Limits { get; set; } = new();
    public PreviewTextLimits Text { get; set; } = new();
    public PreviewImageLimits Image { get; set; } = new();
    public PreviewSpreadsheetLimits Spreadsheet { get; set; } = new();
    public int WorkerTimeoutMs { get; set; } = 5000;

    public bool IsValid() => Limits is not null && Text is not null && Image is not null && Spreadsheet is not null
        && Limits.MaxBytes is > 0 and <= 64 * 1024 * 1024
        && new[] { Limits.TextBytes, Limits.DelimitedBytes, Limits.DocxBytes, Limits.XlsxBytes, Limits.ImageBytes, Limits.AudioBytes, Limits.VideoBytes }.All(v => v > 0 && v <= 64 * 1024 * 1024)
        && Text.MaxLines is > 0 and <= 100000 && Text.HighlightMaxBytes > 0 && Text.HighlightMaxBytes <= Limits.TextBytes
        && Text.HighlightMaxLines > 0 && Text.HighlightMaxLines <= Text.MaxLines
        && Image.MaxPixels is > 0 and <= 40_000_000 && Image.MaxDimension is > 0 and <= 32768
        && Spreadsheet.RowsPerWindow is > 0 and <= OfficeLimits.MaxRows && Spreadsheet.ColumnsPerWindow is > 0 and <= OfficeLimits.MaxColumns
        && Spreadsheet.MaxCellsPerWindow > 0 && Spreadsheet.MaxCellsPerWindow <= OfficeLimits.MaxRows * OfficeLimits.MaxColumns
        && Spreadsheet.RowsPerWindow * Spreadsheet.ColumnsPerWindow <= Spreadsheet.MaxCellsPerWindow
        && Spreadsheet.CsvMaxParsedRows is > 0 and <= 50000 && Spreadsheet.CsvMaxParsedColumns is > 0 and <= 256
        && Spreadsheet.MaxCellCharacters is > 0 and <= 4096 && Spreadsheet.MaxWindowCharacters is > 0 and <= 64000
        && WorkerTimeoutMs is > 0 and <= 10000;

    public FilePreviewOptions Effective() => new()
    {
        Limits = new()
        {
            MaxBytes = Limits.MaxBytes, TextBytes = Math.Min(Limits.MaxBytes, Limits.TextBytes),
            DelimitedBytes = Math.Min(Limits.MaxBytes, Limits.DelimitedBytes), ImageBytes = Math.Min(Limits.MaxBytes, Limits.ImageBytes),
            AudioBytes = Math.Min(Limits.MaxBytes, Limits.AudioBytes), VideoBytes = Math.Min(Limits.MaxBytes, Limits.VideoBytes),
            DocxBytes = Math.Min(OfficeLimits.MaxPackageBytes, Math.Min(Limits.MaxBytes, Limits.DocxBytes)),
            XlsxBytes = Math.Min(OfficeLimits.MaxPackageBytes, Math.Min(Limits.MaxBytes, Limits.XlsxBytes))
        }, Text = new() { MaxLines = Text.MaxLines, HighlightMaxBytes = Math.Min(Text.HighlightMaxBytes, Math.Min(Limits.TextBytes, Limits.MaxBytes)), HighlightMaxLines = Text.HighlightMaxLines }, Image = Image, Spreadsheet = Spreadsheet, WorkerTimeoutMs = WorkerTimeoutMs
    };

    internal int OfficeLimit(string name, string mime) => mime == OfficeLimits.DocumentMime && name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase)
        ? Effective().Limits.DocxBytes : Effective().Limits.XlsxBytes;
    internal void RequireOfficeSize(string name, string mime, long size)
    {
        var limit = OfficeLimit(name, mime);
        if (size > limit) throw new AgentCoreException("preview_too_large", $"This file is {size / 1048576d:0.##} MiB; its preview limit is {limit / 1048576d:0.##} MiB. Download the original.", 413);
    }
}
public sealed class PreviewByteLimits
{
    public int MaxBytes { get; set; } = 33554432;
    public int TextBytes { get; set; } = 2097152;
    public int DelimitedBytes { get; set; } = 5242880;
    // Standard environment alias for operators using FilePreview__Limits__CsvBytes.
    [System.Text.Json.Serialization.JsonIgnore]
    public int CsvBytes { get => DelimitedBytes; set => DelimitedBytes = value; }
    public int DocxBytes { get; set; } = 10485760;
    public int XlsxBytes { get; set; } = 10485760;
    public int ImageBytes { get; set; } = 15728640;
    public int AudioBytes { get; set; } = 33554432;
    public int VideoBytes { get; set; } = 33554432;
}
public sealed class PreviewTextLimits
{
    public int MaxLines { get; set; } = 10000;
    public int HighlightMaxBytes { get; set; } = 262144;
    public int HighlightMaxLines { get; set; } = 3000;
}
public sealed class PreviewImageLimits
{
    public long MaxPixels { get; set; } = 40000000;
    public int MaxDimension { get; set; } = 32768;
}
public sealed class PreviewSpreadsheetLimits
{
    public int RowsPerWindow { get; set; } = 200;
    public int ColumnsPerWindow { get; set; } = 64;
    public int MaxCellsPerWindow { get; set; } = 12800;
    public int CsvMaxParsedRows { get; set; } = 50000;
    public int CsvMaxParsedColumns { get; set; } = 256;
    public int MaxCellCharacters { get; set; } = 4096;
    public int MaxWindowCharacters { get; set; } = 64000;
}
