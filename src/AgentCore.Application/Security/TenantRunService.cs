using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantRunReceipt(bool Created, Guid RunId, Guid EntryId, Guid ResponseId, Guid ActivationId, Guid RootActivationId, long SessionRevision, long SecurityRevision);
public sealed record TenantRunMetadata(TrustedExecutionContext Context, Guid AgentInstanceId, AgentRunStatus Status, long Revision, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, Guid? ContinuationRunId = null, AgentRunOutcomeKind? OutcomeKind = null, bool CanCancel = false);

public sealed class TenantRunService(IIamStore security, ITenantSessionStore sessions, ITenantRunStore runs,
    TenantRunConfigurationResolver configurations, IdentityService identity, IamAuthorizer authorizer, IModelCatalog models, IIdGenerator ids, TimeProvider time, TenantRunCancellationService? cancellations = null, ITenantRunControl? control = null)
{
    public async ValueTask<TenantRunReceipt> SubmitTextAsync(Guid tenant, Guid sessionId, TenantSessionInitiator initiator,
        Guid messageId, string text, long expectedSecurityRevision, long expectedSessionRevision, CancellationToken ct = default)
    {
        var session = await sessions.FindTenantSessionAsync(tenant, sessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
        await Require(state, initiator.PrincipalId, "sessions.read", TenantSessionRules.Scope(tenant, sessionId), ct).ConfigureAwait(false);
        await Require(state, initiator.PrincipalId, "agents.chat", AccessScope.ForResource(tenant, "agent", session.Owner.AgentInstanceId.ToString("D")), ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedSecurityRevision || session.Snapshot.Revision != expectedSessionRevision) throw new SecurityConflictException("stale_revision");
        if (messageId == Guid.Empty || string.IsNullOrWhiteSpace(text) || text.Length > 8000) throw AgentCoreErrors.Validation("A message ID and 1–8000 characters of text are required.");
        var (agent, resolved) = await configurations.ResolveAsync(tenant, session.Owner.AgentInstanceId, ct).ConfigureAwait(false);
        if (agent.PrincipalId != session.Owner.ExecutorPrincipalId) throw new AccessDeniedException("executor_unavailable");
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        TenantSessionRules.RequireExecutor(state, agent);
        var now = DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
        var activationId = ids.NewId(); var runId = ids.NewId(); var entryId = ids.NewId(); var responseId = ids.NewId();
        var input = new ConversationEntry(entryId, session.Snapshot.DurableLastEntrySequence + 1, messageId, ConversationRole.User,
            text, null, EntryStatus.Completed, SessionMode.Text, 0, text.Length, now);
        var activation = Activation.Start(activationId, sessionId, ActivationKind.UserTurn, [entryId], messageId, null, null, null,
            $"human:{initiator.PrincipalId:D}:{messageId:D}", now);
        var context = new TrustedExecutionContext(tenant, agent.PrincipalId, initiator.PrincipalId, sessionId, runId, activationId, activationId, null, null, ExecutionOrigin.UserMessage);
        var definition = resolved.Configuration.Definition;
        var selected = SessionModelBinder.RefreshDefault(models, definition, session.Snapshot.ModelSelection)
            ?? throw AgentCoreErrors.Validation("A resolved model is required.");
        var run = AgentRun.Create(runId, AgentRunOwner.ForTenant(tenant, agent.Instance.InstanceId, agent.PrincipalId),
            new(activation, definition.Id, definition.Version, resolved.Persona, responseId, AgentRunOutputContract.ConversationResponse,
                ExecutionBudgetResolver.Resolve(definition, resolved.InstanceBudgets, false), resolved.Configuration, executionContext: context),
            new(selected.CatalogKey, selected.ProviderAlias, selected.ModelId, selected.ReasoningEffort), AgentRunLimits.DefaultMaxAttempts, now,
            resolved.Skills, resolved.Skills.Where(s => s.Projection == SkillProjection.Always).Select(s => s.Key).ToArray());
        var admission = new TenantHumanRunAdmission(run, input, expectedSessionRevision, agent.Instance.Revision);
        _ = TenantRunRules.Admit(session, admission, agent, initiator);
        var next = state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
        var audit = new AuthorizationAudit(ids.NewId(), tenant, initiator.PrincipalId, "agent-runs.admit", runId.ToString("D"), "allowed", "human_message_admitted", now);
        var result = await runs.TryAdmitTenantHumanRunAsync(next, state.Tenant.Revision, admission, initiator, audit, ct).ConfigureAwait(false)
            ?? throw new SecurityConflictException("stale_revision");
        var saved = result.Run;
        return new(result.Created, saved.AgentRunId, result.EntryId, saved.ResponseId!.Value, saved.ActivationId, saved.Admission.Activation.RootActivationId,
            result.SessionRevision, result.Created ? next.Tenant.Revision : state.Tenant.Revision);
    }
    public async ValueTask<TenantRunMetadata> ReadAsync(Guid tenant, Guid runId, TenantSessionInitiator initiator, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
        var run = await runs.FindTenantRunAsync(tenant, runId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        await Require(state, initiator.PrincipalId, "agent-runs.read", TenantRunRules.Scope(tenant, runId), ct).ConfigureAwait(false);
        await Require(state, initiator.PrincipalId, "sessions.read", TenantSessionRules.Scope(tenant, run.SessionId), ct).ConfigureAwait(false);
        var canCancel = !run.IsTerminal && (await authorizer.CheckTenantAsync(state, initiator.PrincipalId, PermissionCatalog.Get("agent-runs.cancel"), TenantRunRules.Scope(tenant, runId), ct).ConfigureAwait(false)).Allowed;
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, initiator, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return new(run.Admission.ExecutionContext!, run.AgentInstanceId, run.Status, run.Revision, run.CreatedAtUtc, run.UpdatedAtUtc, run.Result?.ContinuationRunId, run.Result?.OutcomeKind, canCancel);
    }
    public async ValueTask<TenantRunMetadata> CancelAsync(Guid tenant, Guid runId, TenantSessionInitiator initiator, long expectedSecurityRevision, long expectedRunRevision, CancellationToken ct = default)
    {
        var service = cancellations ?? new TenantRunCancellationService(security, runs, identity, authorizer, ids, time);
        var request = await service.PrepareAsync(tenant, runId, initiator, expectedSecurityRevision, expectedRunRevision, ct).ConfigureAwait(false);
        var saved = control is null ? await service.CommitAsync(request, ct).ConfigureAwait(false)
            : await control.CancelAsync(request, ct).ConfigureAwait(false);
        return new(saved.Admission.ExecutionContext!, saved.AgentInstanceId, saved.Status, saved.Revision, saved.CreatedAtUtc, saved.UpdatedAtUtc);
    }
    private async ValueTask Require(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct)
    { var result = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false); if (!result.Allowed) throw new AccessDeniedException(result.ReasonCode); }
}
