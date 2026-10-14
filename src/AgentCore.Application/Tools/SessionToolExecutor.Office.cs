using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentCore.Application.Ports;
using AgentCore.Application.Office;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Tools;

public sealed partial class SessionToolExecutor
{
    private static readonly JsonSerializerOptions OfficeJson = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    private sealed record DocumentCreate(string Path, string? Title, IReadOnlyList<OfficeSection>? Sections);
    private sealed record WorkbookCreate(string Path, IReadOnlyList<OfficeSheet>? Sheets);
    private sealed record DocumentInspect(string Path, int Offset = 0, int Limit = 20);
    private sealed record WorkbookInspect(string Path, string? Sheet = null, string? Range = null, string? Cursor = null);
    private sealed record OfficeAttachmentRead(string AttachmentId, string? Sheet = null, string? Range = null, int Offset = 0, int Limit = 100, string? Cursor = null);
    private sealed record DocumentEdit(string Path, string ExpectedSha256, IReadOnlyList<OfficeDocumentEdit>? Operations);
    private sealed record WorkbookEdit(string Path, string ExpectedSha256, IReadOnlyList<OfficeSpreadsheetEdit>? Operations);
    private static T OfficeArgs<T>(JsonElement args) => args.Deserialize<T>(OfficeJson) ?? throw AgentCoreErrors.Validation("office_operation_unsupported: Arguments are required.");
    private async Task<string> ExecuteOfficeAsync(string name, AgentDefinition definition, Guid sessionId, JsonElement args, string cwd, int outputBudget, CancellationToken ct)
    {
        if (officeProcessor is null || agentWorkspace is null || workspace is null || !await AgentWorkspaceAvailableAsync(sessionId, ct))
            return Error("unavailable", "Office processing requires an owned Agent Instance workspace.");
        try
        {
            if (args.GetRawText().Length > 256 * 1024) return Error("office_operation_unsupported", "Office command exceeds 256 KiB. Use smaller operations.");
            if (!TryString(args, "path", out var raw)) return Error("invalid", "path is required.");
            var path = AgentWorkspacePaths.Resolve(raw, sessionId, cwd, structural: true);
            if (!path.StartsWith("/home/", StringComparison.Ordinal) && !path.StartsWith(WorkspaceLogicalPath.WorkingDirectory + "/", StringComparison.Ordinal))
                return Error("forbidden", "Office processing uses files in /home or this Session's /working.");
            var document = name.StartsWith("office.document.", StringComparison.Ordinal); var action = name.Split('.')[2];
            if (!path.EndsWith(document ? ".docx" : ".xlsx", StringComparison.OrdinalIgnoreCase)) return Error("invalid_office_package", "Use the matching .docx or .xlsx extension.");
            OfficeProcessedFile? result = null; string? expectedHash = null;
            if (action == "create")
            {
                if (document)
                { var input = OfficeArgs<DocumentCreate>(args); if (input.Sections is null || input.Sections.Any(s => s is null)) return Error("invalid", "sections are required."); result = officeProcessor.CreateDocument(new(input.Title, input.Sections.Select(s => s with { Paragraphs = s.Paragraphs ?? [] }).ToArray()), ct); }
                else
                { var input = OfficeArgs<WorkbookCreate>(args); if (input.Sheets is null || input.Sheets.Any(s => s is null || s.Rows is null || s.Rows.Any(r => r is null || r.Any(c => c is null)))) return Error("invalid", "sheets and typed rows are required."); result = officeProcessor.CreateWorkbook(new(input.Sheets), ct); }
            }
            else
            {
                await workspace.EnsureAsync(sessionId, definition, ct);
                var source = await ReadExecutionWorkspaceAsync(sessionId, definition, path, ct);
                if (source.Bytes.Length > OfficeLimits.MaxPackageBytes) return Error("office_processing_limit", "The Office file exceeds the 10 MiB processing limit; this does not establish corruption.");
                if (action == "inspect")
                {
                    if (document)
                    {
                        var input = OfficeArgs<DocumentInspect>(args);
                        return OfficeReadResults.Document(officeProcessor.InspectDocument(source.Bytes, input.Offset, input.Limit, ct), input.Offset, Math.Min(outputBudget, 128 * 1024),
                            page => JsonSerializer.Serialize(new { untrusted = true, path = AgentWorkspacePaths.Public(path), inspection = page }, OfficeJson));
                    }
                    else
                    {
                        var input = OfficeArgs<WorkbookInspect>(args);
                        if (input.Cursor is not null && input.Range is not null) return Error("invalid", "A sparse cursor cannot be combined with an explicit range.");
                        if (input.Range is null)
                            return OfficeReadResults.SparseWorkbook(officeProcessor.ReadWorkbookContent(source.Bytes, input.Sheet, input.Cursor, ct), Math.Min(outputBudget, 128 * 1024),
                                page => JsonSerializer.Serialize(new { untrusted = true, path = AgentWorkspacePaths.Public(path), inspection = page }, OfficeJson));
                        return OfficeReadResults.Workbook(officeProcessor.InspectWorkbook(source.Bytes, input.Sheet, input.Range, ct), Math.Min(outputBudget, 128 * 1024),
                            page => JsonSerializer.Serialize(new { untrusted = true, path = AgentWorkspacePaths.Public(path), inspection = page }, OfficeJson));
                    }
                }
                if (!TryString(args, "expectedSha256", out expectedHash) || expectedHash.Length != 64 || !expectedHash.All(Uri.IsHexDigit)) return Error("invalid", "expectedSha256 is required for edits.");
                expectedHash = expectedHash.ToLowerInvariant();
                var actual = Convert.ToHexStringLower(SHA256.HashData(source.Bytes));
                if (!string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase)) return Error("office_revision_conflict", "The file changed. Inspect it again before editing; original bytes are unchanged.");
                if (document) { var input = OfficeArgs<DocumentEdit>(args); if (input.Operations is null || input.Operations.Any(o => o is null)) return Error("invalid", "operations are required."); result = officeProcessor.EditDocument(source.Bytes, input.Operations, ct); }
                else { var input = OfficeArgs<WorkbookEdit>(args); if (input.Operations is null || input.Operations.Any(o => o is null || o.Cells?.Any(row => row is null || row.Any(c => c is null)) == true)) return Error("invalid", "operations are required."); result = officeProcessor.EditWorkbook(source.Bytes, input.Operations, ct); }
            }
            ct.ThrowIfCancellationRequested();
            await agentWorkspace.CommitBinaryAsync(sessionId, path, document ? OfficeLimits.DocumentMime : OfficeLimits.SpreadsheetMime, result.Bytes, expectedHash, ct);
            return JsonSerializer.Serialize(new { path = AgentWorkspacePaths.Public(path), sha256 = Convert.ToHexStringLower(SHA256.HashData(result.Bytes)), byteSize = result.Bytes.Length, warnings = result.Warnings, published = false }, OfficeJson);
        }
        catch (OperationCanceledException) { throw; }
        catch (AgentCoreException ex)
        {
            var code = ex.Code == "Conflict" ? "office_revision_conflict" : ex.Message.StartsWith("invalid_office_package:", StringComparison.Ordinal) ? "invalid_office_package"
                : ex.Message.StartsWith("office_operation_unsupported:", StringComparison.Ordinal) ? "office_operation_unsupported" : ex.Code;
            return Error(code, ex.Message);
        }
        catch (JsonException) { return Error("invalid", "Office arguments do not match the closed tool schema."); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or FormatException or System.Xml.XmlException or NullReferenceException)
        { return Error("invalid_office_package", "The file or operation is unsupported or corrupt; source bytes are unchanged."); }
    }

    private async Task<string> ReadOfficeAttachmentAsync(Guid sessionId, AttachmentRecord record, JsonElement args, int budget, CancellationToken ct)
    {
        try
        {
            var input = OfficeArgs<OfficeAttachmentRead>(args);
            var spreadsheet = AttachmentMedia.NormalizeContentType(record.ContentType) == OfficeLimits.SpreadsheetMime;
            if (spreadsheet && (args.TryGetProperty("offset", out _) || args.TryGetProperty("limit", out _)))
                return Error("invalid", "Use sheet/range or cursor for XLSX; offset/limit apply to DOCX.");
            if (!spreadsheet && (args.TryGetProperty("sheet", out _) || args.TryGetProperty("range", out _) || args.TryGetProperty("cursor", out _)))
                return Error("invalid", "Use offset/limit for DOCX; sheet/range/cursor apply to XLSX.");
            if (record.ByteSize > OfficeLimits.MaxPackageBytes)
                return Error("office_processing_limit", "The Office attachment exceeds the 10 MiB processing limit; this does not establish corruption.");
            await using var stream = await attachments!.OpenContentAsync(sessionId, record.AttachmentId, ct);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, ct);
                if (read == 0) break;
                if (bytes.Length + read > OfficeLimits.MaxPackageBytes) return Error("office_processing_limit", "The Office attachment exceeds the processing byte limit.");
                bytes.Write(buffer, 0, read);
            }
            string Envelope(object inspection) => JsonSerializer.Serialize(new
            {
                untrusted = true, record.AttachmentId, record.DisplayName, record.ContentType, kind = "office", inspection
            }, OfficeJson);
            if (spreadsheet)
            {
                if (input.Cursor is not null && input.Range is not null) return Error("invalid", "A sparse cursor cannot be combined with an explicit range.");
                if (input.Range is null) return OfficeReadResults.SparseWorkbook(officeProcessor!.ReadWorkbookContent(bytes.ToArray(), input.Sheet, input.Cursor, ct), budget, Envelope);
                return OfficeReadResults.Workbook(officeProcessor!.InspectWorkbook(bytes.ToArray(), input.Sheet, input.Range, ct), budget, Envelope);
            }
            return OfficeReadResults.Document(officeProcessor!.InspectDocument(bytes.ToArray(), input.Offset, input.Limit, ct), input.Offset, budget, Envelope);
        }
        catch (JsonException) { return Error("invalid", "Office attachment arguments do not match the closed tool schema."); }
        catch (AgentCoreException ex)
        {
            var code = ex.Message.StartsWith("invalid_office_package:", StringComparison.Ordinal) ? "invalid_office_package"
                : ex.Message.StartsWith("office_operation_unsupported:", StringComparison.Ordinal) ? "office_operation_unsupported" : ex.Code;
            return Error(code, ex.Message);
        }
    }
}
