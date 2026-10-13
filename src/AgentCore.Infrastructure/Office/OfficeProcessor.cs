using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using DocumentFormat.OpenXml.Packaging;

namespace AgentCore.Infrastructure.Office;

public sealed partial class OfficeProcessor
{
    public OfficeProcessedFile CreateDocument(OfficeDocumentSpec spec, CancellationToken ct) => Process(token => CreateDocumentCore(spec, token), ct);
    public OfficeDocumentInspection InspectDocument(ReadOnlyMemory<byte> bytes, int offset, int limit, CancellationToken ct) => Process(token => InspectDocumentCore(bytes, offset, limit, token), ct);
    public OfficeProcessedFile EditDocument(ReadOnlyMemory<byte> bytes, IReadOnlyList<OfficeDocumentEdit> operations, CancellationToken ct) => Process(token => EditDocumentCore(bytes, operations, token), ct);
    public OfficeProcessedFile CreateWorkbook(OfficeWorkbookSpec spec, CancellationToken ct) => Process(token => CreateWorkbookCore(spec, token), ct);
    public OfficeWorkbookInspection InspectWorkbook(ReadOnlyMemory<byte> bytes, string? sheet, string? range, CancellationToken ct) => Process(token => InspectWorkbookCore(bytes, sheet, range, token), ct);
    public OfficeProcessedFile EditWorkbook(ReadOnlyMemory<byte> bytes, IReadOnlyList<OfficeSpreadsheetEdit> operations, CancellationToken ct) => Process(token => EditWorkbookCore(bytes, operations, token), ct);
    private static T Process<T>(Func<CancellationToken, T> operation, CancellationToken caller)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller); deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try { var result = operation(deadline.Token); deadline.Token.ThrowIfCancellationRequested(); return result; }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested) { throw AgentCoreErrors.Validation("office_operation_unsupported: Processing exceeded its cooperative deadline."); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException or FormatException or System.Xml.XmlException or OpenXmlPackageException)
        { throw AgentCoreErrors.Validation("invalid_office_package: The package or operation is unsupported or corrupt."); }
    }
}
