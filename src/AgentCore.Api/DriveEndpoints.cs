using System.Security.Cryptography;
using AgentCore.Api.Http;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Workspaces;
using AgentCore.Infrastructure.Drive;
namespace AgentCore.Api;

internal static class DriveEndpoints
{
    private sealed record SaveRequest(string AccountRef, long AuthEpoch, string ExpectedSha256, Guid OperationId, Guid BindingId, string FolderId = "root", string? Name = null);
    private sealed record SaveSource(Guid InstanceId, string Name, string Mime, byte[] Bytes);
    internal static void Map(WebApplication app)
    {
        var root = app.MapGroup("/api/v2").AddEndpointFilter<OwnerCapabilityFilter>()
            .AddEndpointFilter(async (context, next) => { context.HttpContext.Response.Headers.CacheControl = "private, no-store"; context.HttpContext.Response.Headers.XContentTypeOptions = "nosniff"; return await next(context); });
        root.MapGet("/integrations/google-drive/options", (GoogleDriveOptions options, IGoogleDriveOAuthService oauth) => Results.Json(new { options.Enabled, configured = oauth.IsConfigured, synthetic = oauth.IsSynthetic, scope = AgentCore.Domain.Credentials.DriveAccountProfile.Scope }));
        var g = root.MapGroup("/agent-instances/{instanceId:guid}/drive");
        g.MapGet("/accounts", (Guid instanceId, DriveService service, CancellationToken ct) => Respond(async () => Results.Json(await service.AccountsAsync(instanceId, null, ct))));
        g.MapGet("/accounts/{accountRef}/files", (Guid instanceId, string accountRef, string? folderId, string? query, string? cursor, int? limit, DriveService service, CancellationToken ct) => Respond(async () =>
            Results.Json(await service.ListAsync(await service.ResolveAsync(instanceId, accountRef, null, false, ct), folderId, query, cursor, limit ?? 20, ct))));
        g.MapGet("/accounts/{accountRef}/files/{fileId}", (Guid instanceId, string accountRef, string fileId, DriveService service, CancellationToken ct) => Respond(async () => Results.Json(await service.GetAsync(await service.ResolveAsync(instanceId, accountRef, null, false, ct), fileId, ct))));
        g.MapGet("/accounts/{accountRef}/files/{fileId}/content", (Guid instanceId, string accountRef, string fileId, string? expectedVersion, long? authEpoch, Guid? bindingId, DriveService service, HttpContext http, CancellationToken ct) => Respond(async () =>
        {
            var access = await ReadAccess(service, instanceId, accountRef, authEpoch, bindingId, ct); var source = await service.ReadAsync(access, fileId, expectedVersion, ct);
            http.Response.Headers.CacheControl = "private, no-store"; http.Response.Headers.XContentTypeOptions = "nosniff"; return Results.Bytes(source.Bytes, source.File.MimeType, source.File.Name);
        }));
        g.MapGet("/accounts/{accountRef}/files/{fileId}/office-preview", (Guid instanceId, string accountRef, string fileId, string? expectedVersion, long? authEpoch, Guid? bindingId, string? sheet, string? range, DriveService service, IOfficeProcessor office, CancellationToken ct) => Respond(async () =>
        {
            var access = await ReadAccess(service, instanceId, accountRef, authEpoch, bindingId, ct); var metadata = await service.GetAsync(access, fileId, ct);
            if (metadata.ByteSize > OfficeLimits.MaxPackageBytes) return Results.Problem(statusCode: 413, title: "preview_too_large", detail: "The file exceeds 10 MiB. Download the original.");
            var source = await service.ReadAsync(access, fileId, expectedVersion, ct); return OfficePreviewEndpoints.Project(source.File.Name, source.File.MimeType, source.Bytes, sheet, range, office, ct);
        }));
        g.MapGet("/accounts/{accountRef}/operations/{operationId:guid}", (Guid instanceId, string accountRef, Guid operationId, DriveService service, CancellationToken ct) => Respond(async () => Results.Json(await service.InspectOperationAsync(await service.ResolveAsync(instanceId, accountRef, null, false, ct), operationId, ct))));
        var home = root.MapGroup("/agent-instances/{instanceId:guid}/workspace/{itemId:guid}");
        home.MapGet("/drive-accounts", (Guid instanceId, Guid itemId, AgentInstanceWorkspaceService files, DriveService drive, CancellationToken ct) => Respond(async () => { await files.InspectAsync(instanceId, itemId, cancellationToken: ct); return Results.Json(new { instanceId, accounts = await drive.AccountsAsync(instanceId, null, ct) }); }));
        home.MapPost("/drive-upload", (Guid instanceId, Guid itemId, SaveRequest body, AgentInstanceWorkspaceService files, DriveService drive, CancellationToken ct) => Respond(async () =>
        { var source = await files.ReadAsync(instanceId, itemId, cancellationToken: ct); return await Save(new(instanceId, Path.GetFileName(source.Item.LogicalPath), source.Item.ContentType, source.Bytes), body, drive, ct); }));
        var artifacts = root.MapGroup("/sessions/{sessionId:guid}/artifacts/{artifactId:guid}");
        artifacts.MapGet("/drive-accounts", (Guid sessionId, Guid artifactId, SessionManager sessions, DriveService drive, CancellationToken ct) => Respond(async () =>
        { await sessions.GetArtifactAsync(sessionId, artifactId, ct); var session = await sessions.GetAsync(sessionId, ct); return Results.Json(new { instanceId = session.AgentInstanceId, accounts = await drive.AccountsAsync(session.AgentInstanceId, null, ct) }); }));
        artifacts.MapPost("/drive-upload", (Guid sessionId, Guid artifactId, SaveRequest body, SessionManager sessions, DriveService drive, CancellationToken ct) => Respond(async () =>
        {
            var file = await sessions.GetArtifactAsync(sessionId, artifactId, ct); var session = await sessions.GetAsync(sessionId, ct); if (file.ByteSize > DriveService.MaxTransferBytes) throw new AgentCoreException("drive_transfer_too_large", "The file exceeds 50 MiB.", 413);
            await using var stream = await sessions.OpenArtifactContentAsync(sessionId, artifactId, ct); using var bytes = new MemoryStream(); var buffer = new byte[81920]; while (true) { var read = await stream.ReadAsync(buffer, ct); if (read == 0) break; if (bytes.Length + read > DriveService.MaxTransferBytes) throw new AgentCoreException("drive_transfer_too_large", "The file exceeds 50 MiB.", 413); bytes.Write(buffer, 0, read); }
            return await Save(new(session.AgentInstanceId, file.DisplayName, file.ContentType, bytes.ToArray()), body, drive, ct);
        }));
    }
    private static async ValueTask<DriveAccess> ReadAccess(DriveService service, Guid instance, string reference, long? epoch, Guid? binding, CancellationToken ct)
    {
        var access = await service.ResolveAsync(instance, reference, null, false, ct); if (epoch is not null) DriveService.Epoch(access, epoch.Value); if (binding is not null && access.Binding.BindingId != binding) throw new AgentCoreException("drive_access_changed", "The Drive binding changed. Reload its source list.", 409); return access;
    }
    private static async Task<IResult> Save(SaveSource source, SaveRequest body, DriveService drive, CancellationToken ct)
    {
        var hash = Convert.ToHexStringLower(SHA256.HashData(source.Bytes)); if (!string.Equals(hash, body.ExpectedSha256, StringComparison.OrdinalIgnoreCase)) throw new AgentCoreException("drive_source_revision_conflict", "Source file changed. Reload before saving to Drive.", 409);
        if (body.OperationId == Guid.Empty) throw AgentCoreErrors.Validation("operationId is required."); var access = await drive.ResolveAsync(source.InstanceId, body.AccountRef, null, true, ct); if (access.Binding.BindingId != body.BindingId) throw new AgentCoreException("drive_access_changed", "Drive binding changed. Reload the destination list before saving.", 409);
        return Results.Json(await drive.EffectAsync(access, body.OperationId, "drive.upload", body.AuthEpoch, null, new(body.Name ?? source.Name, body.FolderId, hash, source.Bytes.Length, source.Mime), source.Bytes, ct));
    }
    private static async Task<IResult> Respond(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (DriveIndeterminateException ex) { return Results.Json(new { error = "drive_effect_indeterminate", message = ex.Message, operationId = ex.OperationId, fileId = ex.FileId, causeCode = ex.CauseCode, retryAllowed = false, resumeAllowed = ex.ResumeAllowed }, statusCode: 409); }
        catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { return Results.Problem(statusCode: 400, title: "drive_invalid", detail: "Drive request or provider data could not be interpreted safely."); }
    }
}
