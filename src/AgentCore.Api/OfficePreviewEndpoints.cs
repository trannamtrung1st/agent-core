using AgentCore.Api.Http;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Contracts.Http;

namespace AgentCore.Api;

internal static class OfficePreviewEndpoints
{
    private sealed record Source(string Name, string Mime, byte[] Bytes);
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2").AddEndpointFilter<OwnerCapabilityFilter>()
            .AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "private, no-store"; context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff"; return await next(context); });
        group.MapGet("/sessions/{sessionId:guid}/attachments/{attachmentId:guid}/office-preview", (Guid sessionId, Guid attachmentId, string? sheet, string? range, SessionManager sessions, IAttachmentStore files, IOfficeProcessor office, CancellationToken ct) => Respond(async () =>
        {
            await sessions.EnsureAttachmentsReadableAsync(sessionId, ct);
            var metadata = await files.GetAsync(sessionId, attachmentId, ct) ?? throw AgentCoreErrors.NotFound("File was not found.");
            await using var stream = await files.OpenContentAsync(sessionId, attachmentId, ct);
            return new Source(metadata.DisplayName, metadata.ContentType, await Read(stream, ct));
        }, sheet, range, office, ct));
        group.MapGet("/sessions/{sessionId:guid}/artifacts/{artifactId:guid}/office-preview", (Guid sessionId, Guid artifactId, string? sheet, string? range, SessionManager sessions, IOfficeProcessor office, CancellationToken ct) => Respond(async () =>
        {
            var metadata = await sessions.GetArtifactAsync(sessionId, artifactId, ct);
            await using var stream = await sessions.OpenArtifactContentAsync(sessionId, artifactId, ct);
            return new Source(metadata.DisplayName, metadata.ContentType, await Read(stream, ct));
        }, sheet, range, office, ct));
        group.MapGet("/agent-instances/{instanceId:guid}/workspace/{itemId:guid}/office-preview", (Guid instanceId, Guid itemId, string? sheet, string? range, AgentInstanceWorkspaceService files, IOfficeProcessor office, CancellationToken ct) => Respond(async () =>
        {
            var source = await files.ReadAsync(instanceId, itemId, cancellationToken: ct);
            return new Source(Path.GetFileName(source.Item.LogicalPath), source.Item.ContentType, source.Bytes);
        }, sheet, range, office, ct));
        group.MapGet("/admin/agent-instances/{instanceId:guid}/resources/{key}/office-preview", (Guid instanceId, string key, string? sheet, string? range, AgentInstanceResourceService files, IOfficeProcessor office, CancellationToken ct) => Respond(async () =>
        {
            var source = await files.ReadContentAsync(instanceId, key, ct);
            return new Source(Path.GetFileName(source.Resource.LogicalPath), source.Resource.MediaType, source.Bytes);
        }, sheet, range, office, ct));
        group.MapGet("/admin/definition-drafts/{draftId:guid}/resources/{resourceId:guid}/office-preview", (Guid draftId, Guid resourceId, string? sheet, string? range, AgentDefinitionResourceService files, IOfficeProcessor office, CancellationToken ct) => Respond(async () =>
        {
            var item = (await files.ListDraftResourcesAsync(draftId, ct)).SingleOrDefault(item => item.ResourceId == resourceId) ?? throw AgentCoreErrors.NotFound("File was not found.");
            var bytes = await files.ReadDraftResourceContentAsync(draftId, resourceId, ct) ?? throw AgentCoreErrors.NotFound("File was not found.");
            return new Source(Path.GetFileName(item.LogicalPath), item.MediaType, bytes);
        }, sheet, range, office, ct));
    }
    private static async Task<byte[]> Read(Stream source, CancellationToken ct)
    {
        using var output = new MemoryStream(); var buffer = new byte[81920];
        while (true) { var read = await source.ReadAsync(buffer, ct); if (read == 0) break; if (output.Length + read > OfficeLimits.MaxPackageBytes) throw new AgentCoreException("preview_too_large", "The file exceeds 10 MiB. Download the original.", 413); output.Write(buffer, 0, read); }
        return output.ToArray();
    }
    private static async Task<IResult> Respond(Func<Task<Source>> read, string? sheet, string? range, IOfficeProcessor office, CancellationToken ct)
    {
        try
        {
            var source = await read();
            return Project(source.Name, source.Mime, source.Bytes, sheet, range, office, ct);
        }
        catch (AgentCoreException ex)
        {
            return ex.Message.StartsWith("invalid_office_package:", StringComparison.Ordinal) || ex.Message.StartsWith("office_operation_unsupported:", StringComparison.Ordinal)
                ? Results.Problem(statusCode: 400, title: ex.Message.Split(':')[0], detail: "The Office file or selected range is unsupported, unsafe or corrupt. Download the original or choose a smaller range.") : ProblemResults.From(ex);
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or System.Xml.XmlException)
        { return Results.Problem(statusCode: 400, title: "invalid_office_package", detail: "This Office package could not be interpreted safely. Download the original."); }
    }
    internal static IResult Project(string name, string mime, byte[] bytes, string? sheet, string? range, IOfficeProcessor office, CancellationToken ct)
    {
        if (bytes.Length > OfficeLimits.MaxPackageBytes) return Results.Problem(statusCode: 413, title: "preview_too_large", detail: "The file exceeds the 10 MiB Office preview limit. Download the original.");
        if (mime == OfficeLimits.DocumentMime && name.EndsWith(".docx", StringComparison.OrdinalIgnoreCase))
        {
            var result = office.InspectDocument(bytes, 0, 1, ct);
            return Results.Json(new OfficePreviewResponse("docx", result.Sha256, result.Warnings, EmptyBody: result.TotalElements == 0));
        }
        if (mime == OfficeLimits.SpreadsheetMime && name.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase))
        {
            var result = office.InspectWorkbook(bytes, sheet, range, ct);
            return Results.Json(new OfficePreviewResponse("xlsx", result.Sha256, result.Warnings, Sheets: result.Sheets, Sheet: result.Sheet,
                Range: result.Range, Rows: result.Rows.Select(row => (IReadOnlyList<OfficeCellResponse>)row.Select(cell => new OfficeCellResponse(cell.Address, cell.Type, cell.Value, cell.Formula, cell.ValueStatus, cell.NumberFormat)).ToArray()).ToArray()));
        }
        return Results.Problem(statusCode: 400, title: "preview_unsupported", detail: "The filename and stored type do not match a supported Office file. Download the original.");
    }

}
