using AgentCore.Api.Http;
using AgentCore.Api.Mapping;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;

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
            ILocalUserProfileService profiles, IAgentInstanceStore instances, IAgentRunStore runs, IArtifactStore artifacts, ITriggerStore triggers, CancellationToken ct) => Safe(async () =>
        {
            var instance = await instances.FindAsync(instanceId, ct).ConfigureAwait(false) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
            var profile = await profiles.GetLocalProfileAsync(ct).ConfigureAwait(false);
            var owner = new AgentRunOwner(instanceId, profile.ProfileId);
            var page = await memory.ListBackgroundSessionsAsync(owner, cursor, limit ?? 50, includeArchived ?? false, ct).ConfigureAwait(false);
            var items = new List<BackgroundSessionResponse>();
            foreach (var session in page.Items)
            {
                var initial = await runs.GetAsync(owner, session.Origin.InitialBackgroundAgentRunId!.Value, ct).ConfigureAwait(false);
                items.Add(await ToSessionAsync(session, initial, instance.Lifecycle == AgentInstanceLifecycle.Active, artifacts, runs, triggers, ct));
            }
            return Results.Json(new BackgroundSessionPageResponse(items, page.NextCursor, page.HasMore));
        }));
        var instanceRuns = app.MapGroup("/api/v2/agent-instances/{instanceId:guid}/agent-runs").AddEndpointFilter<OwnerCapabilityFilter>();
        instanceRuns.MapGet("", (Guid instanceId, Guid? before, int? limit, ILocalUserProfileService profiles, IAgentInstanceStore instances, IAgentRunStore runs, IMemoryStore memory, ITriggerStore triggers, CancellationToken ct) => Safe(async () =>
        {
            if (await instances.FindAsync(instanceId, ct) is null) throw AgentCoreErrors.NotFound("Agent Instance was not found.");
            var profile = await profiles.GetLocalProfileAsync(ct);
            var page = await runs.ListPageAsync(new(instanceId, profile.ProfileId), null, before, limit ?? 50, ct);
            var items = new List<AgentRunResponse>();
            foreach (var run in page.Items)
                if (await TryToRunWithSourceAsync(run, memory, triggers, ct) is { } item) items.Add(item);
            return Results.Json(new AgentRunPageResponse(items, page.NextCursor?.ToString("D"), page.HasMore));
        }));
        instanceRuns.MapGet("/{runId:guid}", (Guid instanceId, Guid runId, ILocalUserProfileService profiles, IAgentRunStore runs, IMemoryStore memory, ITriggerStore triggers, CancellationToken ct) => Safe(async () =>
        {
            var profile = await profiles.GetLocalProfileAsync(ct);
            var run = await runs.GetAsync(new(instanceId, profile.ProfileId), runId, ct) ?? throw AgentCoreErrors.NotFound("AgentRun was not found.");
            return Results.Json(await ToRunWithSourceAsync(run, memory, triggers, ct));
        }));
        var sessions = app.MapGroup("/api/v2/sessions/{sessionId:guid}").AddEndpointFilter<OwnerCapabilityFilter>();
        sessions.MapGet("/background", (Guid sessionId, SessionManager manager, ILocalUserProfileService profiles,
            IAgentRunStore runs, IArtifactStore artifacts, IAgentInstanceStore instances, ITriggerStore triggers, CancellationToken ct) => Safe(async () =>
        {
            var session = await RequireSession(manager, profiles, sessionId, ct).ConfigureAwait(false);
            if (!session.Surfaces.HasFlag(SessionSurface.BackgroundWork)) throw AgentCoreErrors.NotFound("Background Session was not found.");
            var instance = await instances.FindAsync(session.AgentInstanceId, ct);
            return Results.Json(await ToSessionAsync(session, await runs.GetAsync(new(session.AgentInstanceId, session.ProfileId!.Value), session.Origin.InitialBackgroundAgentRunId!.Value, ct), instance?.Lifecycle == AgentInstanceLifecycle.Active, artifacts, runs, triggers, ct));
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
            ILocalUserProfileService profiles, IAgentRunStore runs, ITriggerStore triggers, CancellationToken ct) => Safe(async () =>
        {
            var session = await RequireSession(manager, profiles, sessionId, ct).ConfigureAwait(false);
            var page = await runs.ListPageAsync(new(session.AgentInstanceId, session.ProfileId!.Value), sessionId, before, limit ?? 50, ct).ConfigureAwait(false);
            var items = new List<AgentRunResponse>();
            foreach (var run in page.Items) items.Add(await WithTriggerOriginAsync(ToRun(run, session.Origin.AutomationId), run, triggers, ct));
            return Results.Json(new AgentRunPageResponse(items, page.NextCursor?.ToString("D"), page.HasMore));
        }));
        sessions.MapGet("/agent-runs/{runId:guid}", (Guid sessionId, Guid runId, SessionManager manager,
            ILocalUserProfileService profiles, IAgentRunStore runs, IMemoryStore memory, ITriggerStore triggers, CancellationToken ct) => Safe(async () =>
            Results.Json(await ToRunWithSourceAsync(await RequireRun(manager, profiles, runs, sessionId, runId, ct).ConfigureAwait(false), memory, triggers, ct))));
        sessions.MapPost("/agent-runs/{runId:guid}/cancel", (Guid sessionId, Guid runId, CancelAgentRunRequest body,
            SessionManager manager, ILocalUserProfileService profiles, IAgentRunStore runs, SessionHost host, IMemoryStore memory, ITriggerStore triggers, CancellationToken ct) => Safe(async () =>
        {
            await RequireRun(manager, profiles, runs, sessionId, runId, ct).ConfigureAwait(false);
            return Results.Json(await ToRunWithSourceAsync(await host.ControlAgentRunAsync(sessionId, runId, body.ExpectedRevision, ct: ct).ConfigureAwait(false), memory, triggers, ct));
        }));
        foreach (var action in new[] { "approve", "reject" })
        {
            var decision = action == "approve" ? AgentRunApprovalDecision.Approved : AgentRunApprovalDecision.Rejected;
            sessions.MapPost($"/agent-runs/{{runId:guid}}/approvals/{{approvalId:guid}}/{action}",
                (Guid sessionId, Guid runId, Guid approvalId, DecideAgentRunApprovalRequest body, SessionManager manager,
                    ILocalUserProfileService profiles, IAgentRunStore runs, SessionHost host, IMemoryStore memory, ITriggerStore triggers, CancellationToken ct) => Safe(async () =>
                {
                    await RequireRun(manager, profiles, runs, sessionId, runId, ct).ConfigureAwait(false);
                    return Results.Json(await ToRunWithSourceAsync(await host.ControlAgentRunAsync(sessionId, runId, body.ExpectedRevision,
                        approvalId, body.ExpectedApprovalRevision, body.ActionHash, decision, ct).ConfigureAwait(false), memory, triggers, ct));
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

    private static async Task<AgentRunResponse> ToRunWithSourceAsync(AgentRun run, IMemoryStore memory, ITriggerStore triggers, CancellationToken ct)
        => await TryToRunWithSourceAsync(run, memory, triggers, ct).ConfigureAwait(false)
            ?? throw AgentCoreErrors.NotFound("AgentRun was not found.");

    private static async Task<AgentRunResponse?> TryToRunWithSourceAsync(AgentRun run, IMemoryStore memory, ITriggerStore triggers, CancellationToken ct)
    {
        var session = await memory.LoadMetadataAsync(run.SessionId, ct).ConfigureAwait(false);
        if (session is null || session.AgentInstanceId != run.AgentInstanceId || session.ProfileId != run.ProfileId
            || session.DurablyDeletedAt is not null) return null;
        return await WithTriggerOriginAsync(ToRun(run, session.Origin.AutomationId), run, triggers, ct);
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
        run.Wait is { } wait ? new(wait.Mode.ToString(), wait.Until.ToString(), wait.BackgroundSessionIds.Select(id => id.ToString("D")).ToArray(), HttpMapping.Format(wait.DeadlineUtc)) : null, ToBudget(run),
        run.Admission.Configuration is { } config ? new(config.Definition.Id, config.Definition.Version, config.InstanceRevision,
            config.PersonaRevision, config.ConfigurationHash, config.Resources.Select(r => new AgentRunResourceResponse(r.Key, r.VirtualPath, r.ContentSha256, r.ByteLength)).ToArray()) : null, ToTriggerOrigin(run),
        run.Admission.ComposerInput is { } input ? new(input.ExplicitSkillKeys, "userExplicit", HttpMapping.Format(input.AdmittedAtUtc), input.Error,
            input.References.Select(r => new ComposerReferenceResponse(r.Reference.Kind, r.Reference.Locator, r.Label, r.Status, r.Revision, r.Sha256, r.Truncated)).ToArray()) : null);

    private static async Task<AgentRunResponse> WithTriggerOriginAsync(AgentRunResponse response, AgentRun run, ITriggerStore triggers, CancellationToken ct)
    {
        if (run.Admission.Activation.TriggerOccurrenceId is not { } occurrenceId) return response;
        var occurrence = await triggers.GetOccurrenceAsync(new(run.AgentInstanceId, run.ProfileId), occurrenceId, ct);
        if (occurrence is null) return response;
        var origin = ToTriggerOrigin(run, occurrence.EvidenceJson, occurrence.TriggerId, occurrence.SourceKind);
        return response with { AutomationId = occurrence.AutomationId?.ToString("D"), TriggerOrigin = origin is null ? null : origin with { TriggerRevision = occurrence.TriggerId is null ? null : occurrence.TriggerRevision } };
    }

    private static AutomationTriggerOriginResponse? ToTriggerOrigin(AgentRun run, string? occurrenceEvidence = null, Guid? capturedTriggerId = null, TriggerSourceKind? capturedSourceKind = null)
    {
        if (run.Admission.Activation.TriggerOccurrenceId is null) return null;
        if (run.Admission.Activation.Kind == ActivationKind.ManualBackground) return new(null, "manual", null, "Manual Run now");
        if ((occurrenceEvidence ?? run.Admission.Activation.EvidenceJson) is not { } evidence) return null;
        using var document = System.Text.Json.JsonDocument.Parse(evidence);
        var root = document.RootElement;
        var triggerId = root.TryGetProperty("triggerId", out var id) && id.ValueKind == System.Text.Json.JsonValueKind.String ? id.GetString() : capturedTriggerId?.ToString("D");
        if (triggerId is null) return null;
        var summary = root.TryGetProperty("triggerSummary", out var text) ? text.GetString() ?? "Automation" : "Automation";
        EventSourceReferenceDto? source = null;
        if (root.TryGetProperty("source", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.Object
            && value.TryGetProperty("kind", out var kind))
            source = new(kind.GetString()!, value.TryGetProperty("key", out var key) ? key.GetString() : null,
                value.TryGetProperty("eventId", out var eventId) ? eventId.GetString() : null);
        // Historical evidence at the original size ceiling cannot grow during migration.
        // Recover its safe source only from the owned immutable occurrence, never from today's parent.
        if (source is null && capturedSourceKind == TriggerSourceKind.CoreEvent
            && (summary.StartsWith("Core Event · ", StringComparison.Ordinal) || summary.StartsWith("Built-in · ", StringComparison.Ordinal)))
        {
            var key = summary[(summary.IndexOf(" · ", StringComparison.Ordinal) + 3)..];
            if (CoreEventCatalog.Keys.Contains(key, StringComparer.Ordinal)) source = new("builtin", key);
        }
        if (source is null && capturedSourceKind == TriggerSourceKind.ApplicationEvent)
        {
            var resource = root.TryGetProperty("triggerContext", out var context) && context.ValueKind == System.Text.Json.JsonValueKind.Object
                && context.TryGetProperty("eventId", out var archivedEventId) && archivedEventId.ValueKind == System.Text.Json.JsonValueKind.String ? archivedEventId.GetString() : null;
            if (resource is null && summary.StartsWith("Event ", StringComparison.Ordinal)) resource = summary[6..];
            if (Guid.TryParse(resource, out var resourceId) && resourceId != Guid.Empty) source = new("webhook", EventId: resourceId.ToString("D"));
        }
        return new(triggerId, source is null ? "schedule" : "event", source, summary.Length > 200 ? summary[..200] : summary);
    }

    private static AgentRunBudgetResponse? ToBudget(AgentRun run)
    {
        var d = AgentCore.Application.Execution.RunBudgetDiagnosticProjection.From(run);
        return d is null ? null : new(d.Class, d.Source, d.MaxSteps, d.DurationSeconds, d.PerToolSeconds,
            d.StepsConsumed, d.ActiveExecutionMs, d.Phase, d.TerminationReason, d.CleanupStatus, d.ClosureConfirmed, d.ClosureRequested, d.LogoutRequested, d.LogoutVerified, d.CleanupBlocked);
    }

    private static async Task<BackgroundSessionResponse> ToSessionAsync(SessionSnapshot session, AgentRun? initial,
        bool ownerActive, IArtifactStore artifacts, IAgentRunStore runs, ITriggerStore triggers, CancellationToken ct)
    {
        // Count only a bounded metadata page; the UI labels a truncated count with '+'.
        var files = await artifacts.ListPageAsync(session.SessionId, null, 50, ct, session.Origin.InitialBackgroundAgentRunId).ConfigureAwait(false);
        var delivery = await runs.GetCompletionDeliveryAsync(new(session.AgentInstanceId, session.ProfileId!.Value), session.Origin.InitialBackgroundAgentRunId!.Value, ct).ConfigureAwait(false);
        return new(HttpMapping.ToCatalogItem(session),
            new(session.Origin.Kind.ToString(), session.Origin.InitialBackgroundAgentRunId!.Value.ToString("D"), session.Origin.OriginatingSessionId?.ToString("D"),
                session.Origin.OriginatingAgentRunId?.ToString("D"), session.Origin.AutomationId?.ToString("D"), session.Origin.TriggerOccurrenceId?.ToString("D"), session.Origin.ReportCompletionToOrigin),
            new[] { SessionSurface.ChatList, SessionSurface.BackgroundWork }.Where(flag => session.Surfaces.HasFlag(flag)).Select(flag => flag.ToString()).ToArray(),
            initial is null || initial.SessionId != session.SessionId ? null : await WithTriggerOriginAsync(ToRun(initial, session.Origin.AutomationId), initial, triggers, ct), ownerActive && session.ArchivedAt is null && !SessionLifecycle.IsTerminal(session.LifecycleStatus)
                && session.Status is not (SessionStatus.Ended or SessionStatus.Ending), files.Items.Count, files.HasMore, new(delivery.Status, delivery.TargetSessionId?.ToString("D"), delivery.ParentAgentRunId?.ToString("D"), delivery.Reason), session.Origin.InitialTitle);
    }
}
