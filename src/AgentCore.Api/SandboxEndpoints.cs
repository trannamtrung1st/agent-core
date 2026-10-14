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
        admin.MapGet("/sandboxes", (ISandboxReadiness readiness, ISandboxStore store, OpenSandboxOptions options, ISandboxImageAuthority authority, CancellationToken ct) => Safe(async () =>
            Results.Json(new SandboxPlatformResponse(readiness.State, readiness.SafeMessage, (await Task.WhenAll((await store.ListProfilesAsync(ct)).Select(p => MapObservedAsync(p, store, authority, readiness, ct)))).ToArray(),
                options.AllowedImagePrefixes, options.AllowLocalImageIds, "Trusted single-user local demo; own execd loopback remains reachable.",
                "250 MiB canonical /working ceiling; OCI writable-layer disk quota is not enforced by this Docker backend."))));
        admin.MapGet("/sandbox-profiles/{id}", (string id, long? revision, ISandboxStore store, ISandboxImageAuthority authority, ISandboxReadiness readiness, CancellationToken ct) => Safe(async () =>
            Results.Json(await MapObservedAsync(await store.GetProfileAsync(id, revision, ct) ?? throw AgentCoreErrors.NotFound("Sandbox profile was not found."), store, authority, readiness, ct))));
        admin.MapPut("/sandbox-profiles/{id}", (string id, JsonElement body, ISandboxStore store, OpenSandboxOptions options, OpenSandboxReadiness readiness, ISandboxImageAuthority authority, CancellationToken ct) => Safe(async () =>
        {
            var request = body.Deserialize<SaveSandboxProfileRequest>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new ArgumentException();
            var profile = new SandboxProfile(id,checked(request.ExpectedRevision+1),request.Name,request.Description,request.Enabled,request.ImageReference,
                request.InitializationScript,request.Cpu,request.MemoryMiB,request.Pids,request.MaxCommandSeconds,request.IdleTtlSeconds,request.MaxLifetimeSeconds,request.StorageBytes,RegistryCredentialId:request.RegistryCredentialId);
            var current = await store.GetProfileAsync(id,ct:ct);
            var disablingUnchangedSource = !profile.Enabled && current is not null && current.ImageReference == profile.ImageReference && current.RegistryCredentialId == profile.RegistryCredentialId;
            profile.Validate();
            if (!disablingUnchangedSource)
            {
                options.ValidateProfile(profile);
                var source = await authority.InspectAsync(profile, ct, requireVerification:false);
                if (source.Status != "SourceAuthorized") throw AgentCoreErrors.Validation(source.Message);
            }
            await store.SaveProfileAsync(profile,request.ExpectedRevision,ct,History(profile));
            await readiness.RefreshAsync(ct); // Commands consult the saved disable immediately; the supervised pass owns cleanup.
            return Results.Json(await MapObservedAsync(profile,store,authority,readiness,ct));
        }));
        admin.MapPost("/sandbox-profiles/{id}/verify", (string id, VerifySandboxProfileRequest request, ISandboxStore store, OpenSandboxService service, ISandboxImageAuthority authority, OpenSandboxReadiness readiness, CancellationToken ct) => Safe(async () =>
        {
            var profile = await store.GetProfileAsync(id,ct:ct) ?? throw AgentCoreErrors.NotFound("Sandbox profile was not found.");
            if (profile.Revision != request.ExpectedRevision) throw AgentCoreErrors.Conflict("Profile changed. Reload before verifying.");
            var observation = await service.VerifyProfileAsync(profile,ct);
            await store.SaveVerificationAsync(observation,ct);
            await readiness.RefreshAsync(ct);
            return Results.Json(new SandboxVerificationResponse(observation.Passed,observation.Message,await MapObservedAsync(profile,store,authority,readiness,ct), observation.ExitCode, observation.Stdout, observation.Stderr, observation.Truncated, observation.DurationMs, observation.DiagnosticId));
        }));
        admin.MapPost("/sandbox-profiles/{id}/check-image", (string id, VerifySandboxProfileRequest request, ISandboxStore store, OpenSandboxService service, CancellationToken ct) => Safe(async () =>
        {
            var profile = await store.GetProfileAsync(id,ct:ct) ?? throw AgentCoreErrors.NotFound("Sandbox profile was not found.");
            if (profile.Revision != request.ExpectedRevision) throw AgentCoreErrors.Conflict("Profile changed. Reload before checking readiness.");
            return Results.Json(MapVerification(await service.VerifyProfileAsync(profile,ct,imageOnly:true), null));
        }));
        admin.MapPost("/agent-instances/{instanceId:guid}/sandbox/verify", (Guid instanceId, VerifyInstanceSandboxRequest request, AgentRunConfigurationResolver configuration,
            ISandboxStore store, OpenSandboxService service, CancellationToken ct) => Safe(async () =>
        {
            var resolved = (await configuration.ResolveAsync(instanceId,ct,inspectSandbox:true)).Configuration;
            if (resolved.InstanceRevision != request.ExpectedInstanceRevision) throw AgentCoreErrors.Conflict("Instance changed. Reload before verifying setup.");
            var profile = resolved.Definition.ExecutionSandboxProfile ?? throw AgentCoreErrors.Conflict("An authorized Sandbox Profile is required.");
            var result = await service.VerifyProfileAsync(profile,ct);
            await store.SaveInstanceVerificationAsync(new(instanceId,resolved.InstanceRevision,result),ct);
            return Results.Json(MapVerification(result,resolved.InstanceRevision));
        }));
        admin.MapGet("/agent-instances/{instanceId:guid}/sandbox", (Guid instanceId, AgentRunConfigurationResolver configuration, ISandboxReadiness readiness, ISandboxStore store, ISandboxImageAuthority authority, CancellationToken ct) => Safe(async () =>
        {
            var config = (await configuration.ResolveAsync(instanceId,ct,allowArchived:true,inspectSandbox:true)).Configuration;
            var resolved = config.Definition;
            var verification = await store.GetInstanceVerificationAsync(instanceId,config.InstanceRevision,ct);
            if (verification?.Result.ProfileId != resolved.ExecutionSandboxProfile?.Id || verification?.Result.ProfileRevision != resolved.ExecutionSandboxProfile?.Revision) verification = null;
            return Results.Json(new { policy = resolved.SandboxPolicy, profile = resolved.ExecutionSandboxProfile is { } p ? await MapObservedAsync(p,store,authority,readiness,ct) : null,
                instanceRevision = config.InstanceRevision, instanceInitializationScript = resolved.ExecutionSandboxProfile?.InstanceInitializationScript,
                verification = verification is null ? null : new { instanceRevision = verification.InstanceRevision, result = MapVerification(verification.Result, verification.InstanceRevision) }, state = readiness.State, message = readiness.SafeMessage });
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
    private static SandboxSetupVerificationResponse MapVerification(SandboxProfileVerification result, long? instanceRevision) =>
        new(result.Passed,result.Message,result.ProfileId,result.ProfileRevision,instanceRevision,result.VerifiedAt,result.ExitCode,result.Stdout,result.Stderr,result.Truncated,result.DurationMs,result.DiagnosticId);
    private static async Task<SandboxProfileResponse> MapObservedAsync(SandboxProfile profile, ISandboxStore store, ISandboxImageAuthority authority, ISandboxReadiness readiness, CancellationToken ct)
    {
        var availability = await authority.InspectAsync(profile, ct);
        var observation = await store.GetVerificationAsync(profile.Id, profile.Revision, ct);
        return Map(observation is null ? profile : profile with { LastVerifiedAt = observation.VerifiedAt, VerificationReason = observation.Message })
            with { Verification = observation is null ? null : MapVerification(observation, null), Availability = availability.Eligible && readiness.State != "Ready" ? "ServerUnavailable" : availability.Status, AvailabilityMessage = availability.Eligible && readiness.State != "Ready" ? "Profile is verified, but the private server is unavailable or disabled. Restore server readiness before selecting or using it." : availability.Message };
    }
    private static SandboxProfileResponse Map(SandboxProfile p) => new(p.Id,p.Revision,p.Name,p.Description,p.Enabled,p.ImageReference,p.InitializationScript,p.Cpu,p.MemoryMiB,p.Pids,p.MaxCommandSeconds,p.IdleTtlSeconds,p.MaxLifetimeSeconds,p.StorageBytes,p.LastVerifiedAt,p.VerificationReason,RegistryCredentialId:p.RegistryCredentialId);
    private static AdminEventAppend History(SandboxProfile p) => new(Guid.CreateVersion7(),DateTimeOffset.UtcNow,AdminEventActorKind.LocalOwner,AdminEventOperationKind.SandboxProfileChanged,
        "sandbox.profile",p.Id,p.Revision,null,JsonSerializer.Serialize(new { profileId = p.Id, revision = p.Revision, enabled = p.Enabled }));
    private static async Task<IResult> Safe(Func<Task<IResult>> action)
    {
        try { return await action(); }
        catch (AgentCoreException e) { return ProblemResults.From(e); }
        catch (Exception e) when (e is ArgumentException or OverflowException or JsonException) { return Results.Problem(statusCode:400,title:"ValidationError",detail:"Use a valid immutable image, profile revision and bounded limits."); }
    }
}
