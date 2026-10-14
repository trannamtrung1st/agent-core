using AgentCore.Api.Http;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Sandbox;
using AgentCore.Application.Admin;
using System.Text.Json;

namespace AgentCore.Api;

internal static class SandboxEndpoints
{
    internal static void MapSandboxes(this RouteGroupBuilder admin)
    {
        admin.MapGet("/sandboxes", (ISandboxReadiness readiness, ISandboxStore store, OpenSandboxOptions options, CancellationToken ct) => Safe(async () =>
            Results.Json(new SandboxPlatformResponse(readiness.State, readiness.SafeMessage, (await store.ListProfilesAsync(ct)).Select(Map).ToArray(),
                options.AllowedImagePrefixes, options.AllowLocalImageIds, "Trusted single-user local demo; own execd loopback remains reachable.",
                "250 MiB canonical /working ceiling; OCI writable-layer disk quota is not enforced by this Docker backend."))));
        admin.MapGet("/sandbox-profiles/{id}", (string id, long? revision, ISandboxStore store, CancellationToken ct) => Safe(async () =>
            Results.Json(Map(await store.GetProfileAsync(id, revision, ct) ?? throw AgentCoreErrors.NotFound("Sandbox profile was not found.")))));
        admin.MapPut("/sandbox-profiles/{id}", (string id, SaveSandboxProfileRequest request, ISandboxStore store, OpenSandboxOptions options, OpenSandboxReadiness readiness, CancellationToken ct) => Safe(async () =>
        {
            var profile = new SandboxProfile(id,checked(request.ExpectedRevision+1),request.Name,request.Description,request.Enabled,request.ImageReference,
                request.InitializationScript,request.Cpu,request.MemoryMiB,request.Pids,request.MaxCommandSeconds,request.IdleTtlSeconds,request.MaxLifetimeSeconds,request.StorageBytes);
            options.ValidateProfile(profile); await store.SaveProfileAsync(profile,request.ExpectedRevision,ct,History(profile));
            await readiness.RefreshAsync(ct); // Commands consult the saved disable immediately; the supervised pass owns cleanup.
            return Results.Json(Map(profile));
        }));
        admin.MapPost("/sandbox-profiles/{id}/verify", (string id, VerifySandboxProfileRequest request, ISandboxStore store, OpenSandboxService service, TimeProvider time, CancellationToken ct) => Safe(async () =>
        {
            var profile = await store.GetProfileAsync(id,ct:ct) ?? throw AgentCoreErrors.NotFound("Sandbox profile was not found.");
            if (profile.Revision != request.ExpectedRevision) throw AgentCoreErrors.Conflict("Profile changed. Reload before verifying.");
            var passed = await service.VerifyProfileAsync(profile,ct);
            profile = profile with { Revision = profile.Revision + 1, LastVerifiedAt = time.GetUtcNow(), VerificationReason = passed ? "Non-root Python image smoke passed." : "Image unavailable or smoke/cleanup failed. Check local server and image." };
            await store.SaveProfileAsync(profile,request.ExpectedRevision,ct,History(profile));
            return Results.Json(new SandboxVerificationResponse(passed,profile.VerificationReason,Map(profile)));
        }));
        admin.MapGet("/agent-instances/{instanceId:guid}/sandbox", (Guid instanceId, AgentRunConfigurationResolver configuration, ISandboxReadiness readiness, CancellationToken ct) => Safe(async () =>
        {
            var resolved = (await configuration.ResolveAsync(instanceId,ct,allowArchived:true)).Configuration.Definition;
            return Results.Json(new { policy = resolved.SandboxPolicy, profile = resolved.ExecutionSandboxProfile is { } p ? Map(p) : null, state = readiness.State, message = readiness.SafeMessage });
        }));
    }

    internal static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/sessions/{sessionId:guid}/sandbox").AddEndpointFilter<OwnerCapabilityFilter>();
        group.MapGet("", (Guid sessionId, IMemoryStore memory, ILocalUserProfileService owners, ISandboxService service, CancellationToken ct) => Safe(async () =>
        { await OwnedSessionAsync(sessionId,memory,owners,ct); return Results.Json(await service.InspectAsync(sessionId,ct)); }));
        group.MapGet("/executions/{executionId:guid}", (Guid sessionId, Guid executionId, IMemoryStore memory, ILocalUserProfileService owners, ISandboxService service, CancellationToken ct) => Safe(async () =>
        { await OwnedSessionAsync(sessionId,memory,owners,ct); return Results.Json(await service.InspectProcessAsync(sessionId,executionId,ct)); }));
        group.MapPost("/executions/{executionId:guid}/stop", (Guid sessionId, Guid executionId, IMemoryStore memory, ILocalUserProfileService owners, ISandboxService service, CancellationToken ct) => Safe(async () =>
        { await OwnedSessionAsync(sessionId,memory,owners,ct); return Results.Json(await service.StopProcessAsync(sessionId,executionId,ct)); }));
        group.MapPost("/reset", (Guid sessionId, ResetSandboxRequest request, IMemoryStore memory, ILocalUserProfileService owners, ISandboxService service, CancellationToken ct) => Safe(async () =>
        { await OwnedSessionAsync(sessionId,memory,owners,ct); await service.StopSessionAsync(sessionId,ct,request.ExpectedGeneration); return Results.Ok(new { reset = true }); }));
    }
    private static async Task OwnedSessionAsync(Guid id, IMemoryStore memory, ILocalUserProfileService owners, CancellationToken ct)
    {
        var session = await memory.LoadMetadataAsync(id,ct); var owner = await owners.GetLocalProfileAsync(ct);
        if (session is null || session.ProfileId != owner.ProfileId || session.DurablyDeletedAt is not null) throw AgentCoreErrors.NotFound("Owned sandbox Session was not found.");
    }
    private static SandboxProfileResponse Map(SandboxProfile p) => new(p.Id,p.Revision,p.Name,p.Description,p.Enabled,p.ImageReference,p.InitializationScript,p.Cpu,p.MemoryMiB,p.Pids,p.MaxCommandSeconds,p.IdleTtlSeconds,p.MaxLifetimeSeconds,p.StorageBytes,p.LastVerifiedAt,p.VerificationReason);
    private static AdminEventAppend History(SandboxProfile p) => new(Guid.CreateVersion7(),DateTimeOffset.UtcNow,AdminEventActorKind.LocalOwner,AdminEventOperationKind.SandboxProfileChanged,
        "sandbox.profile",p.Id,p.Revision,null,JsonSerializer.Serialize(new { profileId = p.Id, revision = p.Revision, enabled = p.Enabled }));
    private static async Task<IResult> Safe(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AgentCoreException e) { return ProblemResults.From(e); }
        catch (Exception e) when (e is ArgumentException or OverflowException) { return Results.Problem(statusCode:400,title:"ValidationError",detail:"Use a valid immutable image, profile revision and bounded limits."); }
    }
}
