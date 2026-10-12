using Microsoft.AspNetCore.Mvc;
using AgentCore.Api.Http;
using AgentCore.Api.Realtime;
using AgentCore.Application.Credentials;
using AgentCore.Application.Email;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api;

public static class EmailSelectionEndpoints
{
    public static void Map(WebApplication app)
    {
        var group = app.MapGroup("/api/v2/sessions/{sessionId:guid}/email-account-request").AddEndpointFilter<OwnerCapabilityFilter>();
        group.MapGet("", (Guid sessionId, SessionManager sessions, ILocalUserProfileService profiles, IAgentRunStore runs,
            IAgentInstanceStore instances, IEmailStore store, IEmailProviderFactory providers, [FromServices] CredentialService credentials, TimeProvider time, CancellationToken ct) => Safe(async () =>
        {
            var session = await RequireSession(sessionId, sessions, profiles, ct);
            var source = (await runs.ListPageAsync(new(session.AgentInstanceId, session.ProfileId!.Value), sessionId, null, 1, ct)).Items.FirstOrDefault();
            var request = source is null ? null : await store.GetSelectionAsync(source.AgentRunId, ct);
            if (source is null || request is not { Status: "pending" } || request.ExpiresAtUtc <= time.GetUtcNow()
                || !EmailSelectionSafety.CanContinue(source, request)
                || session.Entries.Any(e => e.Role == ConversationRole.User && e.Sequence > session.Entries
                    .Where(e => source.Admission.Activation.SourceEntryIds.Contains(e.EntryId)).Select(e => e.Sequence).DefaultIfEmpty(-1).Max())) return Results.NoContent();
            var instance = await instances.FindAsync(session.AgentInstanceId, ct);
            if (instance is not { Lifecycle: AgentInstanceLifecycle.Active }) return Results.NoContent();
            var bound = await credentials.BindingsAsync(session.AgentInstanceId, ct);
            var choices = (await credentials.ListAsync(ct)).Where(c => c.Purpose == "EmailAccount" && c.Status == "Active" && c.EmailAccount is { IsUsable: true } && providers.CanUse(c.EmailAccount) && (request.Target != "email.send" || c.EmailAccount.CanSend))
                .Select(c => new { accountId = c.CredentialId, c.DisplayName, emailAddress = c.EmailAccount!.EmailAddress,
                    preset = c.EmailAccount.Preset.ToString(), accountRef = bound.FirstOrDefault(b => b.CredentialId == c.CredentialId)?.Reference });
            return Results.Json(new { request.RequestId, request.ExpiresAtUtc, instanceId = instance.InstanceId, agentName = instance.Persona.Name,
                expectedInstanceRevision = instance.Revision, choices });
        }));
        group.MapPost("/decision", (Guid sessionId, DecideEmailAccountRequest request, SessionManager sessions,
            ILocalUserProfileService profiles, SessionHost host, CancellationToken ct) => Safe(async () =>
        {
            await RequireSession(sessionId, sessions, profiles, ct);
            if (!await host.DecideEmailAccountAsync(sessionId, request.RequestId, request.AccountId, request.Reference, request.ExpectedInstanceRevision, ct))
                throw AgentCoreErrors.Conflict("This Chat cannot decide the account request. Reconnect and retry.");
            return Results.Json(new { status = request.AccountId is null ? "cancelled" : "allowed", message = request.AccountId is null
                ? "Account selection cancelled. Send a new user turn to retry." : "Account access saved. A safe fresh continuation is admitted only while the source task remains current." });
        }));
    }
    private static async Task<SessionSnapshot> RequireSession(Guid id, SessionManager sessions, ILocalUserProfileService profiles, CancellationToken ct)
    {
        var session = await sessions.LoadRuntimeAsync(id, ct); var profile = await profiles.GetLocalProfileAsync(ct);
        if (session.ProfileId != profile.ProfileId || session.ArchivedAt is not null || session.DurablyDeletedAt is not null
            || !session.Surfaces.HasFlag(SessionSurface.ChatList)) throw AgentCoreErrors.NotFound("Chat Session was not found.");
        return session;
    }
    private static async Task<IResult> Safe(Func<Task<IResult>> action)
    { try { return await action(); } catch (AgentCoreException ex) { return ProblemResults.From(ex); } }
}
