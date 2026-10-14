using AgentCore.Api.Http;
using AgentCore.Application.Drive;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Infrastructure.Drive;
namespace AgentCore.Api;

internal static class DriveEndpoints
{
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

    }
    private static async ValueTask<DriveAccess> ReadAccess(DriveService service, Guid instance, string reference, long? epoch, Guid? binding, CancellationToken ct)
    {
        var access = await service.ResolveAsync(instance, reference, null, false, ct); if (epoch is not null) DriveService.Epoch(access, epoch.Value); if (binding is not null && access.Binding.BindingId != binding) throw new AgentCoreException("drive_access_changed", "The Drive binding changed. Reload its source list.", 409); return access;
    }
    private static async Task<IResult> Respond(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (DriveIndeterminateException ex) { return Results.Json(new { error = "drive_effect_indeterminate", message = ex.Message, operationId = ex.OperationId, fileId = ex.FileId, causeCode = ex.CauseCode, retryAllowed = false, resumeAllowed = ex.ResumeAllowed }, statusCode: 409); }
        catch (AgentCoreException ex) { return ProblemResults.From(ex); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.Text.Json.JsonException) { return Results.Problem(statusCode: 400, title: "drive_invalid", detail: "Drive request or provider data could not be interpreted safely."); }
    }
}
