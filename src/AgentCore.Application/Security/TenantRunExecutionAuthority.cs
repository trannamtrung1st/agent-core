using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Security;

public sealed partial class TenantRunExecutionAuthority(TenantOperationAuthorizer operations, IIamStore store, TimeProvider time,
    TenantRunConfigurationResolver configurations, TenantBrowserEffectService? browserEffects = null, TenantBrowserReadService? browserReads = null,
    TenantCredentialRuntimeReadService? credentialReads = null, TenantArtifactRuntimeReadService? artifactReads = null, TenantWorkspaceRuntimeReadService? workspaceReads = null, TenantAttachmentRuntimeContextService? attachmentContext = null,
    TenantWaitSourceAuthority? waitSources = null, TenantReportReadAuthority? reportReads = null, TenantAccountRuntimeReadService? accountReads = null, TenantAccountEffectService? accountEffects = null, TenantEventReadAuthority? eventReads = null, TenantRunSourceAuthority? sourceAuthority = null, AgentCore.Application.Ports.IModelCatalog? configurationModels = null, AgentCore.Application.Tools.IToolConfigurationGate? configurationTools = null, TenantPublicWebReadService? publicWebReads = null, TenantHttpEffectService? httpEffects = null,
    ILogger<TenantRunExecutionAuthority>? logger = null, TenantContextToolService? contextTools = null) : IAgentRunAuthority, ITenantRunContext
{
    public ValueTask RecordContextOutputFailureAsync(TenantRunReference reference, string reason, CancellationToken ct) =>
        operations.RecordFailureAsync(reference, "variables.resolve", reason, ExecutionAuthorizationPurpose.EffectEligibility, ct);
    public ValueTask RecordAccountOutputFailureAsync(TenantRunReference reference, string reason, CancellationToken ct) =>
        operations.RecordFailureAsync(reference, reason == "account_metadata_unavailable" ? "credentials.read" : "credentials.use", reason, ExecutionAuthorizationPurpose.EffectEligibility, ct);
    public ValueTask<AgentDefinition?> CurrentDefinitionAsync(AgentRun run, CancellationToken cancellationToken = default) => CurrentDefinitionCoreAsync(run, true, cancellationToken);
    public ValueTask<AgentDefinition?> CurrentProjectionDefinitionAsync(AgentRun run, CancellationToken ct = default) => CurrentDefinitionCoreAsync(run, false, ct);
    public ValueTask RecordProfileOutputFailureAsync(TenantRunReference reference, string reason, CancellationToken ct) =>
        operations.RecordFailureAsync(reference, "memory.read", reason, ExecutionAuthorizationPurpose.EffectEligibility, ct);
    public ValueTask RecordMemoryOutputFailureAsync(TenantRunReference reference, string reason, CancellationToken ct) =>
        operations.RecordFailureAsync(reference, reason == "memory_learning_changed" ? "memory.manage" : "memory.read", reason, ExecutionAuthorizationPurpose.EffectEligibility, ct);
    private async ValueTask<AgentDefinition?> CurrentDefinitionCoreAsync(AgentRun run, bool checkProfile, CancellationToken cancellationToken)
    {
        var phase = "resolve_before";
        try
        {
            // Discard a raced policy snapshot and make one fresh read. This never
            // retries a model call or effect, and every attempt rechecks live authority.
            for (var attempt = 0; attempt < 2; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                phase = "resolve_before";
                var before = await ResolveCurrentAsync(run, cancellationToken).ConfigureAwait(false);
                phase = "current_policy";
                var current = await configurations.CurrentPolicyAsync(before.State.Tenant.TenantId, run.AgentInstanceId, cancellationToken).ConfigureAwait(false);
                phase = "resolve_after";
                var after = await ResolveCurrentAsync(run, cancellationToken).ConfigureAwait(false);
                if (current.Agent.PrincipalId != run.Owner.ExecutorPrincipalId
                    || current.Agent.Instance.Lifecycle != AgentInstanceLifecycle.Active || current.Agent.Instance.DefinitionId != run.DefinitionId)
                    return null;
                if (before.State.Tenant.Revision == after.State.Tenant.Revision)
                {
                    phase = "admission";
                    if (after.Run.Admission.OutputContract is AgentRunOutputContract.BackgroundOutcome or AgentRunOutputContract.CompletionReport
                        || TenantAutomationConversationRules.IsConversationRoot(await TenantExecutionLineage.RootAsync(after.Run, (ITenantRunStore)store, cancellationToken).ConfigureAwait(false)))
                    {
                        var root = await TenantExecutionLineage.RootAsync(after.Run, (ITenantRunStore)store, cancellationToken).ConfigureAwait(false);
                        if (root.Admission.ExecutionContext!.Origin == ExecutionOrigin.UserMessage)
                        {
                            if (TenantExperienceReviewRules.Selected(root) is null
                                && !AgentCore.Application.Agents.RolePermissions.AllowsTool(current.Definition, AgentCore.Application.Tools.ToolCatalog.BackgroundStart)) return null;
                        }
                        else
                        {
                            var source = root.Admission.ExecutionContext.Origin switch {
                                ExecutionOrigin.ScheduledOccurrence => AgentCore.Domain.Triggers.TriggerSourceKind.Schedule,
                                ExecutionOrigin.ManualInvocation => AgentCore.Domain.Triggers.TriggerSourceKind.ManualInvocation,
                                ExecutionOrigin.ApplicationEvent => AgentCore.Domain.Triggers.TriggerSourceKind.ApplicationEvent,
                                _ => AgentCore.Domain.Triggers.TriggerSourceKind.CoreEvent };
                            if (!AgentCore.Application.Triggers.OccurrenceCompatibility.Allows(current.Definition, source)) return null;
                        }
                    }
                    if (after.Run.Admission.OutputContract == AgentRunOutputContract.CompletionReport)
                    {
                        phase = "report_source";
                        if (reportReads is null) throw new AccessDeniedException("completion_source_read_required");
                        await reportReads.RequireAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    }
                    phase = "event_source";
                    if (eventReads is not null) await eventReads.RequireAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    else if (TenantEventReadRules.IsEventRoot(await TenantExecutionLineage.RootAsync(after.Run, (ITenantRunStore)store, cancellationToken).ConfigureAwait(false)) || AgentCore.Application.Tools.TenantTaskExecutionContract.IsIndependent(after.Run))
                        throw new AccessDeniedException("event_source_unavailable");
                    phase = "browser_effect";
                    if (browserEffects is not null) await browserEffects.RequireStoredObservationAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    phase = "browser_read";
                    if (browserReads is not null) await browserReads.RequireStoredObservationsAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    phase = "account_read";
                    if (accountReads is not null) await accountReads.RequireStoredAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    phase = "account_effect";
                    if (accountEffects is not null) await accountEffects.RequireStoredAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    else if (TenantAccountActionReferences.Read(after.Run).Count > 0) throw new AccessDeniedException("account_source_changed");
                    phase = "http_effect";
                    if(httpEffects is not null)await httpEffects.RequireStoredAsync(after.Run,cancellationToken).ConfigureAwait(false);
                    else if(TenantHttpReferences.Read(after.Run).Count>0)throw new AccessDeniedException("http_source_changed");
                    phase = "credential_read";
                    if (contextTools is not null) await contextTools.RequireStoredAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    if (credentialReads is not null) await credentialReads.RequireStoredAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    phase = "artifact_read";
                    if (artifactReads is not null) await artifactReads.RequireStoredAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    phase = "public_web_read";
                    if (publicWebReads is not null) await publicWebReads.RequireStoredAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    else if (TenantPublicWebReadService.HasStored(after.Run)) throw new AccessDeniedException("public_web_unavailable");
                    phase = "workspace_read";
                    if (workspaceReads is not null) await workspaceReads.RequireStoredAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    phase = "wait_source";
                    if (waitSources is not null) await waitSources.RequireStoredAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    else if (TenantWaitSourceAuthority.HasStoredSources(after.Run)) throw new AccessDeniedException("wait_source_unavailable");
                    phase = "attachment_context";
                    if (attachmentContext is not null) await attachmentContext.RequireStoredAsync(after.Run, cancellationToken).ConfigureAwait(false);
                    else if (AgentCore.Application.Execution.AgentRunToolCallCheckpoint.ReadExecutionState(after.Run.Checkpoint).TenantAttachmentSources is { Count: > 0 }
                        || AgentCore.Application.Execution.AgentRunToolCallCheckpoint.TryRead(after.Run.Checkpoint, out var attachmentMessages)
                            && attachmentMessages!.Any(TenantAttachmentRuntimeContextService.HasReadObservation))
                        throw new AccessDeniedException("attachment_context_unavailable");
                    if (TenantRunSourceAuthority.HasSources(after.Run, checkProfile))
                    {
                        phase = "run_source";
                        if (sourceAuthority is null) throw new AccessDeniedException("source_context_unavailable");
                        await sourceAuthority.RequireAsync(after.Run, cancellationToken, checkProfile).ConfigureAwait(false);
                    }
                    return current.Definition;
                }
            }
            return null;
        }
        catch (AccessDeniedException error) { Unavailable("denied", error.ReasonCode); return null; }
        catch (SecurityConflictException error) { Unavailable("conflict", error.ReasonCode); return null; }

        void Unavailable(string failure, string reason)
        {
            // Machine codes only: never pass exception objects, messages or source data to the sink.
            var safeReason = reason is { Length: > 0 and <= 64 } && reason.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')
                ? reason : "unavailable";
            logger?.LogWarning("Tenant Run projection unavailable. Phase={Phase} Failure={Failure} ReasonCode={ReasonCode} RunId={RunId} CheckProfile={CheckProfile}",
                phase, failure, safeReason, run.AgentRunId, checkProfile);
        }
    }

    private async ValueTask<(AgentRun Run, TenantSecurityState State)> ResolveCurrentAsync(AgentRun run, CancellationToken ct)
    {
        if (run.Owner.TenantId is not { } tenant || run.Admission.ExecutionContext is null)
            throw new AccessDeniedException("tenant_context_required");
        var (current, state) = await operations.ResolveAsync(new(tenant, run.AgentRunId), ct).ConfigureAwait(false);
        if (current.Owner != run.Owner || current.SessionId != run.SessionId || current.Admission.ExecutionContext != run.Admission.ExecutionContext
            || current.Status != AgentRunStatus.Running || current.CancellationRequested || current.Claim is not { } claim
            || claim.Generation != run.Claim?.Generation || claim.LeaseExpiresAtUtc <= time.GetUtcNow())
            throw new AccessDeniedException("tenant_runtime_unavailable");
        return (current, state);
    }

    public async ValueTask<AgentRunContinuitySettings?> CurrentContinuitySettingsAsync(AgentRun run, CancellationToken ct = default)
    {
        _ = await ResolveCurrentAsync(run, ct).ConfigureAwait(false);
        return await configurations.CurrentContinuitySettingsAsync(run, ct).ConfigureAwait(false);
    }
    public async ValueTask<TenantRequesterContext> CurrentRequesterAsync(AgentRun run, CancellationToken ct = default)
    {
        var (current, state) = await ResolveCurrentAsync(run, ct).ConfigureAwait(false);
        var execution = current.Admission.ExecutionContext!;
        var chatAdmitted = false;
        Account? account = null; var hints = new List<TenantRequesterRole>();
        if (execution.OriginalInitiatorPrincipalId is { } requester)
        {
            var user = state.Users.SingleOrDefault(u => u.PrincipalId == requester && u.Status == IdentityStatus.Active)
                ?? throw new AccessDeniedException("requester_unavailable");
            account = await store.ReadAccountAsync(user.AccountId, ct).ConfigureAwait(false);
            if (account?.Status != IdentityStatus.Active) throw new AccessDeniedException("requester_unavailable");
            var agent = AccessScope.ForResource(execution.TenantId, "agent", current.AgentInstanceId.ToString("D"));
            var root = await operations.RootAsync(current, ct).ConfigureAwait(false);
            chatAdmitted = root.Admission.ExecutionContext?.Origin == ExecutionOrigin.UserMessage;
            var session = root.Admission.ExecutionContext?.Origin == ExecutionOrigin.ManualInvocation
                ? TenantAutomationRules.Scope(execution.TenantId, TenantAutomationRules.SourceAutomation(root)) : TenantSessionRules.Scope(execution.TenantId, root.SessionId);
            foreach (var assignment in state.Assignments.Where(a => a.PrincipalId == requester && a.TenantId == execution.TenantId
                && IamAuthorizer.ValidScope(a.Scope) && (a.Scope.Contains(agent) || a.Scope.Contains(session))))
            {
                var role = state.Roles.SingleOrDefault(r => r.RoleId == assignment.RoleId && r.TenantId == execution.TenantId);
                if (role is null || !role.Permissions.Any(key => PermissionCatalog.TryGet(key, out var p) && !p.SystemOnly
                    && (assignment.Scope.Contains(agent) && p.AppliesToResource("agent") || assignment.Scope.Contains(session) && p.AppliesToResource(session.ResourceType)))) continue;
                hints.Add(new(role.Name, assignment.Scope.Kind == AccessScopeKind.Tenant ? "organization" : assignment.Scope.ResourceType == "agent" ? "agent" : session.ResourceType == "automation" ? "automation" : "conversation"));
            }
        }
        var ordered = hints.Distinct().OrderBy(h => h.Scope, StringComparer.Ordinal).ThenBy(h => h.Name, StringComparer.Ordinal).ToArray();
        // Account status/name changes do not necessarily change the tenant security revision.
        var (_, after) = await ResolveCurrentAsync(run, ct).ConfigureAwait(false);
        if (after.Tenant.Revision != state.Tenant.Revision || account is not null && await store.ReadAccountAsync(account.AccountId, ct).ConfigureAwait(false) != account)
            throw new SecurityConflictException("requester_context_changed");
        return new(execution, state.Tenant.Name, account?.DisplayName, ordered.Take(TenantRequesterContext.MaxRoleHints),
            ordered.Length > TenantRequesterContext.MaxRoleHints, state.Tenant.Revision, time.GetUtcNow(), chatAdmitted);
    }
}
