using AgentCore.Api.Http;
using Microsoft.AspNetCore.Mvc;
using AgentCore.Application.Credentials;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;

namespace AgentCore.Api;

internal static class AdminCredentialEndpoints
{
    internal static void MapCredentials(this RouteGroupBuilder group)
    {
        group.MapGet("/credentials", ([FromServices] CredentialService service, CancellationToken ct) => Run(async () => Results.Json(new { items = (await service.ListAsync(ct)).Select(Map) })));
        group.MapGet("/credentials/{credentialId:guid}", (Guid credentialId, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => Results.Json(Map(await service.GetAsync(credentialId, ct)))));
        group.MapPost("/credentials", (AdminCreateCredentialRequest request, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => Results.Json(Map(await service.CreateAsync(request.DisplayName, request.Kind, request.Metadata, request.AllowedOrigins, request.ProtectedValue, ct)), statusCode: 201)));
        group.MapPatch("/credentials/{credentialId:guid}", (Guid credentialId, AdminUpdateCredentialRequest request, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => {
            Positive(request.ExpectedRevision); return Results.Json(Map(await service.UpdateAsync(credentialId, request.ExpectedRevision, request.DisplayName, request.Status, request.Metadata, request.AllowedOrigins, ct))); }));
        group.MapPut("/credentials/{credentialId:guid}/value", (Guid credentialId, AdminReplaceCredentialRequest request, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => {
            Positive(request.ExpectedRevision); return Results.Json(Map(await service.ReplaceAsync(credentialId, request.ExpectedRevision, request.ProtectedValue, ct))); }));
        group.MapDelete("/credentials/{credentialId:guid}", (Guid credentialId, long expectedRevision, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => {
            Positive(expectedRevision); await service.DeleteAsync(credentialId, expectedRevision, ct); return Results.NoContent(); }));
        group.MapGet("/agent-instances/{instanceId:guid}/credential-bindings", (Guid instanceId, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => Results.Json(new { items = (await service.BindingsAsync(instanceId, ct)).Select(Map) })));
        group.MapPost("/agent-instances/{instanceId:guid}/credential-bindings", (Guid instanceId, AdminBindCredentialRequest request, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => {
            Positive(request.ExpectedInstanceRevision); return Results.Json(Map(await service.BindAsync(instanceId, request.CredentialId, request.Reference, request.ExpectedInstanceRevision, ct)), statusCode: 201); }));
        group.MapDelete("/agent-instances/{instanceId:guid}/credential-bindings/{bindingId:guid}", (Guid instanceId, Guid bindingId, long expectedRevision, long expectedInstanceRevision, [FromServices] CredentialService service, CancellationToken ct) => Run(async () => {
            Positive(expectedRevision); Positive(expectedInstanceRevision); await service.UnbindAsync(instanceId, bindingId, expectedRevision, expectedInstanceRevision, ct); return Results.NoContent(); }));
        group.MapPost("/agent-instances/{instanceId:guid}/browser-profile/reset", (Guid instanceId, AdminResetBrowserProfileRequest request, AgentBrowserProfileService service, CancellationToken ct) => Run(async () => {
            Positive(request.ExpectedInstanceRevision); if (!request.Confirm) throw AgentCoreErrors.Validation("Explicit browser profile reset confirmation is required.");
            await service.ResetAsync(instanceId, request.ExpectedInstanceRevision, ct); return Results.NoContent(); }));
    }
    private static void Positive(long revision) { if (revision < 1) throw AgentCoreErrors.Validation("Expected revision must be positive."); }
    private static async Task<IResult> Run(Func<Task<IResult>> action) { try { return await action(); } catch (AgentCoreException ex) { return ProblemResults.From(ex); } }
    private static AdminCredentialResponse Map(CredentialView c) => new(c.CredentialId, c.DisplayName, c.Kind, c.Status, c.Metadata, c.AllowedOrigins, c.Revision, c.CreatedAtUtc, c.UpdatedAtUtc, c.BindingCount);
    private static AdminCredentialBindingResponse Map(CredentialBindingView b) => new(b.BindingId, b.CredentialId, b.Reference, b.Revision, Map(b.Credential));
}
