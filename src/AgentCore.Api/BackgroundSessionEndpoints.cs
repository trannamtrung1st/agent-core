using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Api;

public static class BackgroundSessionEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/v2/agent-instances/{instanceId:guid}/sessions", (Guid instanceId, string? cursor, int? limit,
            IMemoryStore memory, ILocalUserProfileService profiles, IAgentInstanceStore instances, CancellationToken ct) => Safe(async () =>
        {
            if (await instances.FindAsync(instanceId, ct).ConfigureAwait(false) is null) throw AgentCoreErrors.NotFound("Agent Instance was not found.");
            var profile = await profiles.GetLocalProfileAsync(ct).ConfigureAwait(false);
            var page = await memory.ListInstanceSessionsAsync(new(instanceId, profile.ProfileId), cursor, limit ?? 20, ct).ConfigureAwait(false);
            return Results.Json(new InstanceActivitySessionPageResponse(page.Items.Select(session => new InstanceActivitySessionResponse(
                HttpMapping.ToCatalogItem(session), session.Origin.Kind.ToString(),
                new[] { SessionSurface.ChatList, SessionSurface.BackgroundWork }.Where(flag => session.Surfaces.HasFlag(flag)).Select(flag => flag.ToString()).ToArray())).ToArray(),
                page.NextCursor, page.HasMore));
        })).AddEndpointFilter<OwnerCapabilityFilter>();
        app.MapGet("/api/v2/agent-instances/{instanceId:guid}/sessions/{sessionId:guid}", (Guid instanceId, Guid sessionId,
            IMemoryStore memory, ILocalUserProfileService profiles, CancellationToken ct) => Safe(async () =>
        {
            var profile = await profiles.GetLocalProfileAsync(ct).ConfigureAwait(false);
            var session = await memory.LoadMetadataAsync(sessionId, ct).ConfigureAwait(false);
            if (session is null || session.AgentInstanceId != instanceId || session.ProfileId != profile.ProfileId || session.DurablyDeletedAt is not null)
                throw AgentCoreErrors.NotFound("Session was not found.");
            return Results.Json(new InstanceActivitySessionResponse(HttpMapping.ToCatalogItem(session), session.Origin.Kind.ToString(),
                new[] { SessionSurface.ChatList, SessionSurface.BackgroundWork }.Where(flag => session.Surfaces.HasFlag(flag)).Select(flag => flag.ToString()).ToArray()));
        })).AddEndpointFilter<OwnerCapabilityFilter>();
        var owned = app.MapGroup("/api/v2/agent-instances/{instanceId:guid}/background-sessions").AddEndpointFilter<OwnerCapabilityFilter>();
        owned.MapGet("", (Guid instanceId, string? cursor, int? limit, bool? includeArchived, IMemoryStore memory,
            ILocalUserProfileService profiles, IAgentInstanceStore instances, IAgentRunStore runs, IArtifactStore artifacts, CancellationToken ct) => Safe(async () =>
        {
            var instance = await instances.FindAsync(instanceId, ct).ConfigureAwait(false) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
            var profile = await profiles.GetLocalProfileAsync(ct).ConfigureAwait(false);
            var owner = new AgentRunOwner(instanceId, profile.ProfileId);
            var page = await memory.ListBackgroundSessionsAsync(owner, cursor, limit ?? 50, includeArchived ?? false, ct).ConfigureAwait(false);
            var items = new List<BackgroundSessionResponse>();
            foreach (var session in page.Items)
            {
                var initial = await runs.GetAsync(owner, session.Origin.InitialBackgroundAgentRunId!.Value, ct).ConfigureAwait(false);
                items.Add(await ToSessionAsync(session, initial, instance.Lifecycle == AgentInstanceLifecycle.Active, artifacts, runs, ct));
            }
            return Results.Json(new BackgroundSessionPageResponse(items, page.NextCursor, page.HasMore));
        }));
        var instanceRuns = app.MapGroup("/api/v2/agent-instances/{instanceId:guid}/agent-runs").AddEndpointFilter<OwnerCapabilityFilter>();
        instanceRuns.MapGet("", (Guid instanceId, Guid? before, int? limit, ILocalUserProfileService profiles, IAgentInstanceStore instances, IAgentRunStore runs, IMemoryStore memory, CancellationToken ct) => Safe(async () =>
        {
            if (await instances.FindAsync(instanceId, ct) is null) throw AgentCoreErrors.NotFound("Agent Instance was not found.");
            var profile = await profiles.GetLocalProfileAsync(ct);
            var page = await runs.ListPageAsync(new(instanceId, profile.ProfileId), null, before, limit ?? 50, ct);
            var items = new List<AgentRunResponse>();
            foreach (var run in page.Items) items.Add(await ToRunWithSourceAsync(run, memory, ct));
            return Results.Json(new AgentRunPageResponse(items, page.NextCursor?.ToString("D"), page.HasMore));
        }));
        instanceRuns.MapGet("/{runId:guid}", (Guid instanceId, Guid runId, ILocalUserProfileService profiles, IAgentRunStore runs, IMemoryStore memory, CancellationToken ct) => Safe(async () =>
        {
            var profile = await profiles.GetLocalProfileAsync(ct);
            var run = await runs.GetAsync(new(instanceId, profile.ProfileId), runId, ct) ?? throw AgentCoreErrors.NotFound("AgentRun was not found.");
            return Results.Json(await ToRunWithSourceAsync(run, memory, ct));
        }));
        var sessions = app.MapGroup("/api/v2/sessions/{sessionId:guid}").AddEndpointFilter<OwnerCapabilityFilter>();
        sessions.MapGet("/background", (Guid sessionId, SessionManager manager, ILocalUserProfileService profiles,
            IAgentRunStore runs, IArtifactStore artifacts, IAgentInstanceStore instances, CancellationToken ct) => Safe(async () =>
        {
            var session = await RequireSession(manager, profiles, sessionId, ct).ConfigureAwait(false);
            if (!session.Surfaces.HasFlag(SessionSurface.BackgroundWork)) throw AgentCoreErrors.NotFound("Background Session was not found.");
            var instance = await instances.FindAsync(session.AgentInstanceId, ct);
            return Results.Json(await ToSessionAsync(session, await runs.GetAsync(new(session.AgentInstanceId, session.ProfileId!.Value), session.Origin.InitialBackgroundAgentRunId!.Value, ct), instance?.Lifecycle == AgentInstanceLifecycle.Active, artifacts, runs, ct));
        }));
        sessions.MapPost("/continue-in-chat", (Guid sessionId, SessionManager manager, ILocalUserProfileService profiles,
            SessionHost host, IAgentInstanceStore instances, CancellationToken ct) => Safe(async () =>
        {
            var session = await RequireSession(manager, profiles, sessionId, ct).ConfigureAwait(false);
            if ((await instances.FindAsync(session.AgentInstanceId, ct))?.Lifecycle != AgentInstanceLifecycle.Active)
                throw AgentCoreErrors.Conflict("The Agent Instance is unavailable for continuation.");
            if (!await host.ContinueInChatAsync(sessionId, ct).ConfigureAwait(false)) throw AgentCoreErrors.Conflict("Session cannot be opened in chat.");
            return Results.Json(new ContinueInChatResponse(sessionId.ToString("D")));
        }));
        sessions.MapGet("/agent-runs", (Guid sessionId, int? limit, Guid? before, SessionManager manager,
            ILocalUserProfileService profiles, IAgentRunStore runs, CancellationToken ct) => Safe(async () =>
        {
            var session = await RequireSession(manager, profiles, sessionId, ct).ConfigureAwait(false);
            var page = await runs.ListPageAsync(new(session.AgentInstanceId, session.ProfileId!.Value), sessionId, before, limit ?? 50, ct).ConfigureAwait(false);
            return Results.Json(ToPage(page, session.Origin.AutomationId));
        }));
        sessions.MapGet("/agent-runs/{runId:guid}", (Guid sessionId, Guid runId, SessionManager manager,
            ILocalUserProfileService profiles, IAgentRunStore runs, IMemoryStore memory, CancellationToken ct) => Safe(async () =>
            Results.Json(await ToRunWithSourceAsync(await RequireRun(manager, profiles, runs, sessionId, runId, ct).ConfigureAwait(false), memory, ct))));
        sessions.MapPost("/agent-runs/{runId:guid}/cancel", (Guid sessionId, Guid runId, CancelAgentRunRequest body,
            SessionManager manager, ILocalUserProfileService profiles, IAgentRunStore runs, SessionHost host, IMemoryStore memory, CancellationToken ct) => Safe(async () =>
        {
            await RequireRun(manager, profiles, runs, sessionId, runId, ct).ConfigureAwait(false);
            return Results.Json(await ToRunWithSourceAsync(await host.ControlAgentRunAsync(sessionId, runId, body.ExpectedRevision, ct: ct).ConfigureAwait(false), memory, ct));
        }));
        foreach (var action in new[] { "approve", "reject" })
        {
            var decision = action == "approve" ? AgentRunApprovalDecision.Approved : AgentRunApprovalDecision.Rejected;
            sessions.MapPost($"/agent-runs/{{runId:guid}}/approvals/{{approvalId:guid}}/{action}",
                (Guid sessionId, Guid runId, Guid approvalId, DecideAgentRunApprovalRequest body, SessionManager manager,
                    ILocalUserProfileService profiles, IAgentRunStore runs, SessionHost host, IMemoryStore memory, CancellationToken ct) => Safe(async () =>
                {
                    await RequireRun(manager, profiles, runs, sessionId, runId, ct).ConfigureAwait(false);
                    return Results.Json(await ToRunWithSourceAsync(await host.ControlAgentRunAsync(sessionId, runId, body.ExpectedRevision,
                        approvalId, body.ExpectedApprovalRevision, body.ActionHash, decision, ct).ConfigureAwait(false), memory, ct));
                }));
        }
    }

    internal static async Task<SessionSnapshot> RequireSession(SessionManager manager, ILocalUserProfileService profiles, Guid id, CancellationToken ct)
    {
        var session = await manager.GetAsync(id, ct).ConfigureAwait(false);
        if (session.ProfileId != (await profiles.GetLocalProfileAsync(ct).ConfigureAwait(false)).ProfileId) throw AgentCoreErrors.NotFound("Session was not found.");
        return session;
    }
    private static async Task<AgentRun> RequireRun(SessionManager manager, ILocalUserProfileService profiles, IAgentRunStore runs, Guid sessionId, Guid runId, CancellationToken ct)
    {
        var session = await RequireSession(manager, profiles, sessionId, ct).ConfigureAwait(false);
        var run = await runs.GetAsync(new(session.AgentInstanceId, session.ProfileId!.Value), runId, ct).ConfigureAwait(false);
        return run is not null && run.SessionId == sessionId ? run : throw AgentCoreErrors.NotFound("AgentRun was not found.");
    }
    private static async Task<IResult> Safe(Func<Task<IResult>> operation)
    { try { return await operation().ConfigureAwait(false); } catch (AgentCoreException exception) { return ProblemResults.From(exception); } }

    private static AgentRunPageResponse ToPage(AgentRunPage page, Guid? automationId = null) => new(page.Items.Select(run => ToRun(run, automationId)).ToArray(), page.NextCursor?.ToString("D"), page.HasMore);

    private static async Task<AgentRunResponse> ToRunWithSourceAsync(AgentRun run, IMemoryStore memory, CancellationToken ct)
    {
        var session = await memory.LoadMetadataAsync(run.SessionId, ct).ConfigureAwait(false);
        if (session is null || session.AgentInstanceId != run.AgentInstanceId || session.ProfileId != run.ProfileId
            || session.DurablyDeletedAt is not null) throw AgentCoreErrors.NotFound("AgentRun was not found.");
        return ToRun(run, session.Origin.AutomationId);
    }

    internal static AgentRunResponse ToRun(AgentRun run, Guid? automationId = null) => new(run.AgentRunId.ToString("D"), run.SessionId.ToString("D"), run.ActivationId.ToString("D"),
        run.Admission.Activation.Kind.ToString(), run.Status switch { AgentRunStatus.WaitingForApproval => "needsApproval", AgentRunStatus.WaitingToRetry => "retrying", _ => char.ToLowerInvariant(run.Status.ToString()[0]) + run.Status.ToString()[1..] },
        run.Revision, run.AttemptCount, run.MaxAttempts, run.CancellationRequested, !run.IsTerminal && !run.CancellationRequested, run.Progress?.Summary,
        run.NextRetryAtUtc is { } retry ? HttpMapping.Format(retry) : null, HttpMapping.Format(run.CreatedAtUtc), HttpMapping.Format(run.UpdatedAtUtc),
        run.Status == AgentRunStatus.WaitingForApproval && run.Approval is { } approval ? new(approval.ApprovalId.ToString("D"), approval.Revision, approval.ActionHash, approval.ToolName, approval.Preview, HttpMapping.Format(approval.ExpiresAtUtc)) : null,
        run.Result is { } result ? new(result.OutcomeKind.ToString(), result.Text, result.OutcomeEntryId?.ToString("D"), result.AttentionRequired) : null,
        run.Failure?.Code, run.Failure?.Summary, run.Failure?.DiagnosticId?.ToString("D"), run.KnownEffectSummary, run.PinnedModel.CatalogKey, run.ResponseId?.ToString("D"), automationId?.ToString("D"),
        run.Admission.Activation.DedupeKey.StartsWith("experience:", StringComparison.Ordinal) ? run.AgentRunId.ToString("D") : null,
        run.Admission.Activation.TriggerOccurrenceId?.ToString("D"),
        run.Admission.Activation.Kind == ActivationKind.BackgroundCompleted ? run.Admission.Activation.SourceSessionId?.ToString("D") : null,
        run.Wait is { } wait ? new(wait.Mode.ToString(), wait.Until.ToString(), wait.BackgroundSessionIds.Select(id => id.ToString("D")).ToArray(), HttpMapping.Format(wait.DeadlineUtc)) : null);

    private static async Task<BackgroundSessionResponse> ToSessionAsync(SessionSnapshot session, AgentRun? initial,
        bool ownerActive, IArtifactStore artifacts, IAgentRunStore runs, CancellationToken ct)
    {
        // Count only a bounded metadata page; the UI labels a truncated count with '+'.
        var files = await artifacts.ListPageAsync(session.SessionId, null, 50, ct, session.Origin.InitialBackgroundAgentRunId).ConfigureAwait(false);
        var delivery = await runs.GetCompletionDeliveryAsync(new(session.AgentInstanceId, session.ProfileId!.Value), session.Origin.InitialBackgroundAgentRunId!.Value, ct).ConfigureAwait(false);
        return new(HttpMapping.ToCatalogItem(session),
            new(session.Origin.Kind.ToString(), session.Origin.InitialBackgroundAgentRunId!.Value.ToString("D"), session.Origin.OriginatingSessionId?.ToString("D"),
                session.Origin.OriginatingAgentRunId?.ToString("D"), session.Origin.AutomationId?.ToString("D"), session.Origin.TriggerOccurrenceId?.ToString("D"), session.Origin.ReportCompletionToOrigin),
            new[] { SessionSurface.ChatList, SessionSurface.BackgroundWork }.Where(flag => session.Surfaces.HasFlag(flag)).Select(flag => flag.ToString()).ToArray(),
            initial is null || initial.SessionId != session.SessionId ? null : ToRun(initial, session.Origin.AutomationId), ownerActive && session.ArchivedAt is null && !SessionLifecycle.IsTerminal(session.LifecycleStatus)
                && session.Status is not (SessionStatus.Ended or SessionStatus.Ending), files.Items.Count, files.HasMore, new(delivery.Status, delivery.TargetSessionId?.ToString("D"), delivery.ParentAgentRunId?.ToString("D"), delivery.Reason), session.Origin.InitialTitle);
    }
}
