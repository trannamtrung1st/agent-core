using AgentCore.Domain.Conversation;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantApprovalPreparationResult(OperationDecisionKind Decision, string ReasonCode,
    PreparedTenantOperation Operation, TenantApprovalRequest? Request);
public sealed record TenantApprovalViewPage(IReadOnlyList<TenantApprovalView> Items, int Total, int Page, int PageSize);
public sealed record TenantApprovalView(TenantApprovalRequest Request, long SecurityRevision,
    TenantOperationReceipt? OperationResult = null, TenantApprovalDecision? Decision = null, TenantApprovalReview? Review = null, TenantAccountActionResult? AccountResult = null);

public sealed partial class TenantApprovalService(IIamStore security,
    ITenantApprovalStore approvals,
    ITenantAgentStore agents,
    TenantOperationAuthorizer operations, IamAuthorizer authorizer, IdentityService identity, IIdGenerator ids, TimeProvider time,
    TenantRunConfigurationResolver configuration, ITenantModelCatalogReader modelCatalogs, ProviderAliasSet aliases,
    ITenantApprovedOperationStore effects,
    TenantCredentialOperationTarget? credentialTargets = null,
    TenantSandboxOperationTarget? sandboxTargets = null,
    TenantSandboxControlOperationTarget? controlTargets = null,
    TenantSandboxRuntimeReadService? sandboxReads = null,
    TenantBrowserOperationTarget? browserTargets = null,
    TenantCredentialRuntimeReadService? credentialReads = null,
    TenantArtifactRuntimeReadService? artifactReads = null,
    TenantWorkspaceRuntimeReadService? workspaceReads = null,
    TenantWorkspaceMutationTarget? workspaceMutations = null,
    TenantArtifactSourceTarget? artifactSources = null,
    TenantAttachmentRuntimeContextService? attachmentReads = null,
    TenantBackgroundCreationTarget? backgroundCreations = null,
    TenantBackgroundReportTarget? backgroundReports = null,
    ITenantRuntimeStore? reportRuntime = null,
    TenantCompletionReadService? completionReads = null,
    TenantReportSourceReadService? reportSourceReads = null,
    TenantAccountRuntimeReadService? accountReads = null,
    TenantAccountActionTarget? accountActions = null,
    TenantAutomationOperationTarget? automationAdmissions = null,
    TenantMemoryOperationService? memoryOperations = null,
    TenantContinuityReadService? continuityReads = null,
    TenantExperienceOperationService? experienceOperations = null,
    TenantManagementOperationTarget? managementTargets = null,
    TenantPublicWebOperationTarget? webTargets = null,
    AgentCore.Application.Evaluations.EvaluationSandboxAuthority? evaluationSandbox = null)
{
    // Trusted internal typed preparation. No model-supplied tenant, principal, permission, grant or arbitrary action JSON.
    public ValueTask<TenantApprovalPreparationResult> PreparePersonaAsync(TenantRunReference reference, AgentIdentity persona,
        long expectedRunRevision, Guid? generation = null, CancellationToken ct = default) => PrepareAsync(reference, persona, null, expectedRunRevision, generation, ct);

    public ValueTask<TenantApprovalPreparationResult> PreparePreferencesAsync(TenantRunReference reference, PreparedConversationPreferences preferences,
        long expectedRunRevision, Guid? generation = null, CancellationToken ct = default, long? expectedInstanceRevision = null) => PrepareAsync(reference, null, preferences, expectedRunRevision, generation, ct, expectedInstanceRevision);

    private async ValueTask<TenantApprovalPreparationResult> PrepareAsync(TenantRunReference reference, AgentIdentity? persona, PreparedConversationPreferences? preferences,
        long expectedRunRevision, Guid? generation, CancellationToken ct, long? expectedInstanceRevision = null)
    {
        var (run, state) = await operations.ResolveForDecisionAsync(reference, "agents.update", ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
        var agent = await agents.FindTenantAgentAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false)
            ?? throw new AccessDeniedException("executor_unavailable");
        if (expectedInstanceRevision is { } expected && agent.Instance.Revision != expected) throw new SecurityConflictException("stale_revision");
        var operation = preferences is null ? TenantApprovalRules.Prepare(ids.NewId(), agent.Instance.InstanceId, persona ?? throw new ArgumentException("Missing operation."), agent.Instance.Revision, agent.Instance.PersonaRevision)
            : TenantApprovalRules.PreparePreferences(ids.NewId(), agent.Instance.InstanceId, preferences, agent.Instance.Revision, agent.Instance.PersonaRevision);
        operation = TenantApprovalRules.ForRun(operation, run);
        var pinned = run.Admission.Configuration!.Definition;
        var snapshot = preferences is null ? TenantApprovalSnapshotRules.Persona(agent) : null;
        var managementReview = pinned.SelfManagement?.Allows(HarnessManagementScope.Settings) == true;
        if (preferences is not null)
        {
            var (current, baseline) = await configuration.BaselineAsync(reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
            if (current.Instance.Revision != agent.Instance.Revision) throw new SecurityConflictException("stale_revision");
            var effective = InstanceSettingsResolver.Resolve(baseline, current.Instance.SettingsOverrides) with { SelfManagement = ManagementPolicyResolver.Resolve(baseline, current.Instance) };
            snapshot = TenantApprovalSnapshotRules.Preferences(current, baseline);
            if (TenantLocalEffectRules.Eligibility(run, current, effective) is { } unavailable) return await PreparationOutcome(state, run, operation, OperationDecisionKind.Denied, unavailable, null, ct).ConfigureAwait(false);
            var patch = TenantPreferenceToolContract.Patch(preferences);
            var models = await modelCatalogs.ReadAsync(state, ct).ConfigureAwait(false);
            _ = InstanceSettingsRules.ResolvePatch(baseline, current.Instance.SettingsOverrides ?? new(), "conversationPolicy", patch.Set, patch.Clear, models, aliases);
            managementReview = !TenantPreferenceToolContract.Allows(pinned) || !TenantPreferenceToolContract.Allows(effective);
        }
        return await PersistAsync(reference, run, state, agent, operation, snapshot, managementReview, expectedRunRevision, generation, ct).ConfigureAwait(false);
    }

    public async ValueTask<TenantApprovalPreparationResult> PrepareCredentialCheckAsync(TenantRunReference reference, string alias,
        long expectedRunRevision, Guid generation, CancellationToken ct = default)
    {
        try { return await PrepareCredentialCheckCoreAsync(reference, alias, expectedRunRevision, generation, ct).ConfigureAwait(false); }
        catch (Exception ex) when (ex is AccessDeniedException or SecurityConflictException)
        {
            var reason = ex is AccessDeniedException denied ? denied.ReasonCode : ((SecurityConflictException)ex).ReasonCode;
            await operations.RecordFailureAsync(reference, "credentials.use", reason, ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async ValueTask<TenantApprovalPreparationResult> PrepareCredentialCheckCoreAsync(TenantRunReference reference, string alias,
        long expectedRunRevision, Guid generation, CancellationToken ct)
    {
        if (credentialTargets is null) throw new AccessDeniedException("tenant_effect_unavailable");
        var (run, state) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
        if (run.Status != AgentCore.Domain.Conversation.AgentRunStatus.Running || run.Claim?.Generation != generation) throw new AccessDeniedException("tenant_runtime_unavailable");
        var (agent, bound) = await credentialTargets.ResolveAsync(run, alias, ct).ConfigureAwait(false);
        var operation = TenantApprovalRules.PrepareCredentialCheck(ids.NewId(), run.AgentInstanceId, TenantCredentialOperationRules.Parameters(bound), agent.Instance.Revision, agent.Instance.PersonaRevision);
        operation = TenantApprovalRules.ForRun(operation, run);
        var read = await authorizer.CheckTenantAsync(state, run.Admission.ExecutionContext!.AgentPrincipalId, PermissionCatalog.Get("credentials.read"),
            AccessScope.ForResource(reference.TenantId, "credential", operation.ResourceId), ct).ConfigureAwait(false);
        if (!read.Allowed) return await PreparationOutcome(state, run, operation, OperationDecisionKind.Denied, "executor_read_required", null, ct).ConfigureAwait(false);
        return await PersistAsync(reference, run, state, agent, operation, TenantCredentialOperationRules.Snapshot(agent, bound), false, expectedRunRevision, generation, ct).ConfigureAwait(false);
    }

    private async ValueTask<TenantApprovalPreparationResult> PersistAsync(TenantRunReference reference, AgentCore.Domain.Conversation.AgentRun run,
        TenantSecurityState state, TenantAgent agent, PreparedTenantOperation operation, TenantApprovalSnapshot? snapshot, bool managementReview,
        long expectedRunRevision, Guid? generation, CancellationToken ct)
    {
        var existing = await approvals.FindRootApprovalAsync(reference.TenantId, run.Admission.Activation.RootActivationId, agent.PrincipalId, operation, ct).ConfigureAwait(false);
        if (existing is not null) return await DuplicateOutcome(state, run, existing, ct).ConfigureAwait(false);
        OperationAuthorizationDecision decision;
        try
        {
            decision = await operations.Check(state, run, operation.Permission,
                TenantApprovalRules.Scope(reference.TenantId, operation), ct, managementReview, ExecutionAuthorizationPurpose.ApprovalPreparation).ConfigureAwait(false);
        }
        catch (SecurityConflictException ex) when (ex.ReasonCode == "stale_revision")
        {
            // A concurrent preparer can have committed the same root while this gate was being audited.
            var (currentRun, currentState) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
            var winner = await approvals.FindRootApprovalAsync(reference.TenantId, currentRun.Admission.Activation.RootActivationId, agent.PrincipalId, operation, ct).ConfigureAwait(false);
            if (winner is not null) return await DuplicateOutcome(currentState, currentRun, winner, ct).ConfigureAwait(false);
            throw;
        }
        if (decision.Decision != OperationDecisionKind.RequiresApproval) return new(decision.Decision, decision.ReasonCode, operation, null);
        var at = time.GetUtcNow();
        var request = new TenantApprovalRequest(ids.NewId(), reference.TenantId, run.AgentRunId, run.SessionId, run.Admission.Activation.RootActivationId,
            agent.PrincipalId, run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, decision.ApprovalKind!.Value,
            operation, TenantApprovalStatus.Pending, 1, at, at.AddHours(1), snapshot);
        if (operation.AccountAction is not null)
        {
            var qualified = false;
            foreach (var human in state.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try { await TenantAccountActionRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false); qualified = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!qualified) return await PreparationOutcome(state, run, operation, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
        }
        if (operation.AccountRead is not null)
        {
            var qualified = false;
            foreach (var human in state.Users.Where(u => u.Status == IdentityStatus.Active))
            {
                try { await TenantAccountReadRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false); qualified = true; break; }
                catch (AccessDeniedException) { }
            }
            if (!qualified) return await PreparationOutcome(state, run, operation, OperationDecisionKind.Denied, "no_eligible_approver", null, ct).ConfigureAwait(false);
        }
        TenantApprovalRules.Validate(request, run, agent);
        var next = Next(state);
        var audit = new AuthorizationAudit(ids.NewId(), reference.TenantId, agent.PrincipalId, "approvals.request", request.RequestId.ToString("D"), "allowed", "approval_pending", at);
        TenantApprovalRequest? saved;
        try { saved = await approvals.TryCreateApprovalAsync(next, state.Tenant.Revision, request, expectedRunRevision, generation, audit, ct).ConfigureAwait(false); }
        catch (SecurityConflictException ex) when (ex.ReasonCode is "approval_rate_limited" or "approval_root_limit")
        { return await PreparationOutcome(state, run, operation, OperationDecisionKind.Denied, ex.ReasonCode, null, ct).ConfigureAwait(false); }
        if (saved is null)
        {
            // A concurrent same-root preparer may have won security/Run CAS. Return its durable request after fresh identity checks.
            var (current, _) = await operations.ResolveAsync(reference, ct).ConfigureAwait(false);
            saved = await approvals.FindRootApprovalAsync(reference.TenantId, current.Admission.Activation.RootActivationId,
                current.Admission.ExecutionContext!.AgentPrincipalId, operation, ct).ConfigureAwait(false)
                ?? throw new SecurityConflictException("stale_revision");
        }
        return new(saved.Status == TenantApprovalStatus.Pending ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied,
            saved.RequestId == request.RequestId ? "approval_pending" : TenantApprovalRules.DuplicateReason(saved), saved.Operation, saved);
    }

    private ValueTask<TenantApprovalPreparationResult> DuplicateOutcome(TenantSecurityState state, AgentCore.Domain.Conversation.AgentRun run,
        TenantApprovalRequest request, CancellationToken ct) => PreparationOutcome(state, run, request.Operation,
            request.Status == TenantApprovalStatus.Pending ? OperationDecisionKind.RequiresApproval : OperationDecisionKind.Denied,
            TenantApprovalRules.DuplicateReason(request), request, ct);

    private async ValueTask<TenantApprovalPreparationResult> PreparationOutcome(TenantSecurityState state, AgentCore.Domain.Conversation.AgentRun run,
        PreparedTenantOperation operation, OperationDecisionKind decision, string reason, TenantApprovalRequest? request, CancellationToken ct)
    {
        await operations.RecordAsync(state, run, new(decision, reason, operation.Permission,
            TenantApprovalRules.Scope(state.Tenant.TenantId, operation),
            decision == OperationDecisionKind.RequiresApproval ? request?.Kind : null), ExecutionAuthorizationPurpose.ApprovalPreparation, ct).ConfigureAwait(false);
        return new(decision, reason, operation, request);
    }

    public async ValueTask<TenantApprovalRequest> ReadAsync(Guid tenant, Guid requestId, TenantSessionInitiator human, CancellationToken ct = default)
        => (await ReadViewAsync(tenant, requestId, human, ct).ConfigureAwait(false)).Request;

    public async ValueTask<TenantApprovalView> ReadViewAsync(Guid tenant, Guid requestId, TenantSessionInitiator human, CancellationToken ct = default)
    {
        await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var metadata = await Metadata(request, ct).ConfigureAwait(false); request = metadata.Request;
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        await RequireRead(state, human.PrincipalId, request, ct, metadata.OperationResult).ConfigureAwait(false);
        var review = await ReviewAsync(metadata, state, human, ct).ConfigureAwait(false);
        await RequireUnchangedAuthority(state, human, ct).ConfigureAwait(false);
        return metadata with { SecurityRevision = state.Tenant.Revision, Review = review };
    }

    public async ValueTask<TenantApprovalViewPage> ListAsync(Guid tenant, TenantSessionInitiator human, TenantApprovalQuery query, CancellationToken ct = default)
    {
        query.Validate();
        var before = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var requestIds = await VisibleIds(before, human.PrincipalId, "approvals.read", "approval", ct).ConfigureAwait(false);
        var instanceIds = await VisibleIds(before, human.PrincipalId, "agents.update", "agent", ct).ConfigureAwait(false);
        var credentialIds = await VisibleIds(before, human.PrincipalId, "credentials.use", "credential", ct).ConfigureAwait(false);
        var emailRead = await VisibleIds(before, human.PrincipalId, "email.read", "credential", ct).ConfigureAwait(false);
        var driveRead = await VisibleIds(before, human.PrincipalId, "drive.read", "credential", ct).ConfigureAwait(false);
        var credentialRead = await VisibleIds(before, human.PrincipalId, "credentials.read", "credential", ct).ConfigureAwait(false);
        var emailDraft = await VisibleIds(before, human.PrincipalId, "email.draft", "credential", ct).ConfigureAwait(false);
        var emailSend = await VisibleIds(before, human.PrincipalId, "email.send", "credential", ct).ConfigureAwait(false);
        var driveWrite = await VisibleIds(before, human.PrincipalId, "drive.write", "credential", ct).ConfigureAwait(false);
        var driveDelete = await VisibleIds(before, human.PrincipalId, "drive.delete", "credential", ct).ConfigureAwait(false);
        credentialIds = credentialIds is null ? credentialRead : credentialRead is null ? credentialIds : credentialIds.Intersect(credentialRead).ToArray();
        var sandboxIds = await VisibleIds(before, human.PrincipalId, "sandbox.execute", "agent", ct).ConfigureAwait(false);
        var controlIds = await VisibleIds(before, human.PrincipalId, "sandbox.manage", "agent", ct).ConfigureAwait(false);
        var controlReadIds = await VisibleIds(before, human.PrincipalId, "sandbox.read", "agent", ct).ConfigureAwait(false);
        controlIds = controlIds is null ? controlReadIds : controlReadIds is null ? controlIds : controlIds.Intersect(controlReadIds).ToArray();
        var sandboxSessions = await VisibleIds(before, human.PrincipalId, "sessions.read", "session", ct).ConfigureAwait(false);
        var sandboxProfiles = await VisibleSandboxProfiles(before, human.PrincipalId, ct).ConfigureAwait(false);
        var browserRead = await VisibleIds(before, human.PrincipalId, "browser.read", "agent", ct).ConfigureAwait(false);
        var browserUse = await VisibleIds(before, human.PrincipalId, "browser.use", "agent", ct).ConfigureAwait(false);
        var artifactCreate = await VisibleIds(before, human.PrincipalId, "artifacts.create", "session", ct).ConfigureAwait(false);
        var attachmentReadIds = await VisibleIds(before, human.PrincipalId, "attachments.read", "attachment", ct).ConfigureAwait(false);
        var artifactReadIds = await VisibleIds(before, human.PrincipalId, "artifacts.read", "artifact", ct).ConfigureAwait(false);
        var workspaceScopes = await VisibleWorkspaceScopes(before, human.PrincipalId, ct).ConfigureAwait(false);
        var homeSpaces = await VisibleIds(before, human.PrincipalId, "spaces.read", "space", ct).ConfigureAwait(false);
        var homeAgents = await VisibleIds(before, human.PrincipalId, "agents.read", "agent", ct).ConfigureAwait(false);
        var resourceScopes = await VisibleResourceReadScopes(before, human.PrincipalId, ct).ConfigureAwait(false);
        var workspaceWrites = await VisibleWorkspaceScopes(before, human.PrincipalId, ct, "workspaces.write").ConfigureAwait(false);
        var workspaceDeletes = await VisibleWorkspaceScopes(before, human.PrincipalId, ct, "workspaces.delete").ConfigureAwait(false);
        var creationRead = true;
        foreach (var key in TenantBackgroundCreationRules.Permissions)
            creationRead &= (await authorizer.CheckTenantAsync(before, human.PrincipalId, PermissionCatalog.Get(key), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed;
        var reportRuns = await VisibleIds(before, human.PrincipalId, "agent-runs.read", "agent-run", ct).ConfigureAwait(false);
        var reportCreate = (await authorizer.CheckTenantAsync(before, human.PrincipalId, PermissionCatalog.Get("agent-runs.create"), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed;
        var automationChatAgents = await VisibleIds(before, human.PrincipalId, "agents.chat", "agent", ct).ConfigureAwait(false);
        var automationReads = await VisibleIds(before, human.PrincipalId, "automations.read", "automation", ct).ConfigureAwait(false);
        var automationRuns = await VisibleIds(before, human.PrincipalId, "automations.run", "automation", ct).ConfigureAwait(false);
        var webhookSources = await VisibleIds(before, human.PrincipalId, "webhooks.read", "webhook", ct).ConfigureAwait(false);
        var coreEvents = await VisibleIds(before, human.PrincipalId, "core-events.read", "event", ct).ConfigureAwait(false);
        var emailSources = new[] { credentialIds, credentialRead, emailRead }.Where(ids => ids is not null).Select(ids => ids!).ToArray();
        IReadOnlyList<Guid>? emailSourceIds = emailSources.Length == 0 ? null : emailSources.Skip(1).Aggregate(emailSources[0].AsEnumerable(), (ids, next) => ids.Intersect(next)).ToArray();
        var memoryAgents = await VisibleIds(before, human.PrincipalId, "memory.read", "agent", ct).ConfigureAwait(false);
        memoryAgents = memoryAgents is null ? homeAgents : homeAgents is null ? memoryAgents : memoryAgents.Intersect(homeAgents).ToArray();
        var httpAgents=await VisibleIds(before,human.PrincipalId,"http.request","agent",ct).ConfigureAwait(false);
        httpAgents=httpAgents is null?homeAgents:homeAgents is null?httpAgents:httpAgents.Intersect(homeAgents).ToArray();
        var requests = await approvals.ListApprovalsAsync(tenant, requestIds, instanceIds, query, ct, credentialIds, new(sandboxIds, sandboxProfiles, sandboxSessions, controlIds, controlReadIds), new(browserRead, browserUse, sandboxSessions), new(credentialRead, sandboxSessions, emailRead, driveRead, emailDraft, emailSend, driveWrite, driveDelete), artifactCreate, new(artifactReadIds, sandboxSessions), new(workspaceScopes, homeSpaces, sandboxSessions, resourceScopes, homeAgents), new(workspaceScopes, workspaceWrites, workspaceDeletes, homeSpaces, sandboxSessions), new(attachmentReadIds, sandboxSessions), creationRead, new(reportCreate, sandboxSessions, reportRuns), new(creationRead, homeAgents, automationReads, automationRuns, webhookSources, coreEvents, emailSourceIds, reportCreate, sandboxSessions, automationChatAgents), new(human.PrincipalId, memoryAgents, sandboxSessions), new(human.PrincipalId, homeAgents, automationChatAgents, sandboxSessions, memoryAgents), await ManagementVisibility(before, human.PrincipalId, ct).ConfigureAwait(false), new(httpAgents,sandboxSessions)).ConfigureAwait(false);
        var metadata = new List<TenantApprovalView>();
        foreach (var request in requests.Items) metadata.Add(await Metadata(request, ct).ConfigureAwait(false));
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != before.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        var result = new List<TenantApprovalView>();
        foreach (var view in metadata)
        {
            try
            {
                await RequireRead(state, human.PrincipalId, view.Request, ct, view.OperationResult).ConfigureAwait(false);
                result.Add(view with { SecurityRevision = state.Tenant.Revision, Review = await ReviewAsync(view, state, human, ct).ConfigureAwait(false) });
            }
            catch (AccessDeniedException) { }
        }
        await RequireUnchangedAuthority(state, human, ct).ConfigureAwait(false);
        return new(result, requests.Total, requests.Page, requests.PageSize);
    }

    private async ValueTask RequireUnchangedAuthority(TenantSecurityState state, TenantSessionInitiator human, CancellationToken ct)
    {
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, state.Tenant.TenantId, human, ct).ConfigureAwait(false);
        if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
    }

    private async ValueTask<TenantApprovalView> Metadata(TenantApprovalRequest request, CancellationToken ct)
    {
        var receipt = await effects.FindOperationReceiptAsync(request.TenantId, request.Operation.OperationId, ct).ConfigureAwait(false);
        var decision = await effects.FindApprovalDecisionAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false);
        if (request.Status == TenantApprovalStatus.Pending && (receipt is not null || decision is not null))
            request = await approvals.FindApprovalAsync(request.TenantId, request.RequestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        var accountResult = request.Operation.AccountAction is not null && receipt is not null
            ? TenantAccountActionResults.Read(request, receipt, await operations.FindRunAsync(new(request.TenantId, receipt.RunId), ct).ConfigureAwait(false)) : null;
        return new(request, 0, receipt, decision, AccountResult: accountResult);
    }

    private async ValueTask<IReadOnlyList<string>?> VisibleResourceReadScopes(TenantSecurityState state, Guid human, CancellationToken ct, string permissionKey = "resources.read", string resourceType = "resource")
    {
        var permission = PermissionCatalog.Get(permissionKey);
        var inherited = await authorizer.CheckTenantAsync(state, human, permission, AccessScope.ForTenant(state.Tenant.TenantId), ct).ConfigureAwait(false);
        if (inherited.Allowed) return null;
        if (inherited.ReasonCode != "permission_denied") throw new AccessDeniedException(inherited.ReasonCode);
        var result = new List<string>();
        foreach (var scope in state.Assignments.Where(a => a.PrincipalId == human && a.Scope.ResourceType == resourceType).Select(a => a.Scope).Distinct())
            if ((TenantAgentChildScopes.TryParse(scope, out _, out _) || TenantResourceScopes.TrySpace(scope, out _, out _)) && (await authorizer.CheckOwnedTenantAsync(state, human, permission, scope, ct).ConfigureAwait(false)).Allowed) result.Add(scope.ResourceId!);
        return result;
    }
    private async ValueTask<IReadOnlyList<string>?> VisibleWorkspaceScopes(TenantSecurityState state, Guid human, CancellationToken ct, string permissionKey = "workspaces.read")
    {
        var permission = PermissionCatalog.Get(permissionKey);
        var inherited = await authorizer.CheckTenantAsync(state, human, permission, AccessScope.ForTenant(state.Tenant.TenantId), ct).ConfigureAwait(false);
        if (inherited.Allowed) return null;
        if (inherited.ReasonCode != "permission_denied") throw new AccessDeniedException(inherited.ReasonCode);
        var scopes = state.Assignments.Where(a => a.PrincipalId == human && a.Scope.ResourceType == "workspace"
            && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains(permissionKey, StringComparer.Ordinal))).Select(a => a.Scope).Distinct();
        var result = new List<string>();
        foreach (var scope in scopes)
            if (TenantWorkspaceRules.TryParse(scope, out _, out _) && (await authorizer.CheckOwnedTenantAsync(state, human, permission, scope, ct).ConfigureAwait(false)).Allowed) result.Add(scope.ResourceId!);
        return result;
    }

    private async ValueTask<IReadOnlyList<Guid>?> VisibleIds(TenantSecurityState state, Guid human, string permission, string type, CancellationToken ct)
    {
        var metadata = PermissionCatalog.Get(permission);
        var inherited = await authorizer.CheckTenantAsync(state, human, metadata, AccessScope.ForTenant(state.Tenant.TenantId), ct).ConfigureAwait(false);
        if (inherited.Allowed) return null;
        if (inherited.ReasonCode != "permission_denied") throw new AccessDeniedException(inherited.ReasonCode);
        var candidates = state.Assignments.Where(a => a.PrincipalId == human && a.Scope.ResourceType == type
            && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains(permission, StringComparer.Ordinal))).Select(a => a.Scope).Distinct();
        var visible = new List<Guid>();
        foreach (var scope in candidates)
            if ((await authorizer.CheckTenantAsync(state, human, metadata, scope, ct).ConfigureAwait(false)).Allowed && Guid.TryParse(scope.ResourceId, out var id)) visible.Add(id);
        return visible;
    }

    private async ValueTask<IReadOnlyList<string>?> VisibleSandboxProfiles(TenantSecurityState state, Guid human, CancellationToken ct)
    {
        var metadata = PermissionCatalog.Get("sandbox.read");
        var inherited = await authorizer.CheckTenantAsync(state, human, metadata, AccessScope.ForTenant(state.Tenant.TenantId), ct).ConfigureAwait(false);
        if (inherited.Allowed) return null;
        if (inherited.ReasonCode != "permission_denied") throw new AccessDeniedException(inherited.ReasonCode);
        var scopes = state.Assignments.Where(a => a.PrincipalId == human && a.Scope.ResourceType == "sandbox-profile"
            && state.Roles.Any(r => r.RoleId == a.RoleId && r.Permissions.Contains("sandbox.read", StringComparer.Ordinal))).Select(a => a.Scope).Distinct();
        var visible = new List<string>();
        foreach (var scope in scopes)
            if ((await authorizer.CheckTenantAsync(state, human, metadata, scope, ct).ConfigureAwait(false)).Allowed) visible.Add(scope.ResourceId!);
        return visible;
    }

    public ValueTask<TenantApprovalRequest> DecideAsync(Guid tenant, Guid requestId, TenantSessionInitiator human,
        bool approve, string actionHash, long expectedSecurityRevision, long expectedRequestRevision, CancellationToken ct = default)
        => ChangeAsync(tenant, requestId, human, approve ? TenantApprovalStatus.Approved : TenantApprovalStatus.Rejected, actionHash, expectedSecurityRevision, expectedRequestRevision, ct);

    public ValueTask<TenantApprovalRequest> CancelAsync(Guid tenant, Guid requestId, TenantSessionInitiator human,
        string actionHash, long expectedSecurityRevision, long expectedRequestRevision, CancellationToken ct = default)
        => ChangeAsync(tenant, requestId, human, TenantApprovalStatus.Cancelled, actionHash, expectedSecurityRevision, expectedRequestRevision, ct);

    private async ValueTask<TenantApprovalRequest> ChangeAsync(Guid tenant, Guid requestId, TenantSessionInitiator human,
        TenantApprovalStatus outcome, string actionHash, long expectedSecurityRevision, long expectedRequestRevision, CancellationToken ct)
    {
        if (actionHash is not { Length: 64 } || actionHash.Any(c => !Uri.IsHexDigit(c))) throw new ArgumentException("An exact bounded action hash is required.");
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var request = await approvals.FindApprovalAsync(tenant, requestId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("approval_unavailable");
        if (outcome == TenantApprovalStatus.Cancelled)
        {
            var allowed = await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get("approvals.cancel"),
                AccessScope.ForResource(tenant, "approval", requestId.ToString("D")), ct).ConfigureAwait(false);
            if (!allowed.Allowed) throw new AccessDeniedException("approval_unavailable");
        }
        else await RequireRead(state, human.PrincipalId, request, ct).ConfigureAwait(false);
        if (state.Tenant.Revision != expectedSecurityRevision || request.Revision != expectedRequestRevision || request.Status != TenantApprovalStatus.Pending)
            throw new SecurityConflictException("stale_revision");
        if (!string.Equals(request.Operation.ActionHash, actionHash, StringComparison.Ordinal)) throw new SecurityConflictException("operation_mismatch");
        var at = time.GetUtcNow();
        if (at >= request.ExpiresAt) throw new SecurityConflictException("approval_expired");
        if (request.Operation.SandboxCommand?.Evaluation is not null)
            return await ChangeEvaluationSandboxAsync(state, request, human, outcome, ct);
        if (request.Operation.AutomationAdmission is not null) return await ChangeAutomationAdmissionAsync(state, request, human, outcome, ct).ConfigureAwait(false);
        if (outcome != TenantApprovalStatus.Cancelled)
        {
            AgentRun run; TenantSecurityState current;
            if (request.Operation.ReportSourceRead is not null)
            {
                var reads = reportSourceReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                await reads.RequireAsync(request, ct, pending: true).ConfigureAwait(false);
                run = (await operations.FindRunAsync(new(tenant, request.RunId), ct).ConfigureAwait(false))!; current = state;
                await TenantReportSourceReadRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
            }
            else if (request.Operation.BackgroundReport is not null)
            {
                var reportState = await (backgroundReports ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(request, ct).ConfigureAwait(false);
                (run, current) = (reportState.Source.Child, reportState.State);
                await TenantBackgroundReportRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
            }
            else (run, current) = await operations.ResolveForDecisionAsync(new(tenant, request.RunId), request.Permission, ExecutionAuthorizationPurpose.ApprovalDecision, ct).ConfigureAwait(false);
            if (current.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
            var agent = await agents.FindTenantAgentAsync(tenant, request.Operation.InstanceId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("executor_unavailable");
            TenantApprovalRules.Validate(request, run, agent);
            if (request.Operation.ArtifactPublication is not null)
            {
                await RequireArtifactAsync(request, run, state, ct).ConfigureAwait(false);
                await TenantArtifactReviewRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
            }
            if(request.Operation.HttpRequest is not null)
            {
                await (webTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireSavedAsync(request,run,ct).ConfigureAwait(false);
                await TenantHttpOperationRules.ReviewerAsync(state,request,human.PrincipalId,authorizer,ct).ConfigureAwait(false);
            }
            if (request.Operation.AccountAction is not null)
            {
                await (accountActions ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireSavedAsync(request, run, ct).ConfigureAwait(false);
                await TenantAccountActionRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
            }
            if (request.Operation.AccountRead is not null)
            {
                await (accountReads ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireInspectionAsync(request, run, ct).ConfigureAwait(false);
                await TenantAccountReadRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
            }
            if (request.Operation.CredentialCheck is { } check)
            {
                if (credentialTargets is null) throw new AccessDeniedException("tenant_effect_unavailable");
                var (_, bound) = await credentialTargets.ResolveAsync(run, check.Reference, ct).ConfigureAwait(false);
                TenantCredentialOperationRules.RequireWitness(request.Operation, bound);
                var read = await authorizer.CheckTenantAsync(state, run.Admission.ExecutionContext!.AgentPrincipalId, PermissionCatalog.Get("credentials.read"),
                    AccessScope.ForResource(tenant, "credential", request.ResourceId), ct).ConfigureAwait(false);
                if (!read.Allowed) throw new AccessDeniedException("executor_read_required");
            }
            if (request.Operation.SandboxCommand is not null)
                await (sandboxTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(request, run, ct).ConfigureAwait(false);
            if (request.Operation.SandboxControl is not null)
            {
                var controls = controlTargets ?? throw new AccessDeniedException("tenant_effect_unavailable");
                await controls.RequireAsync(request, run, ct).ConfigureAwait(false);
                await controls.RequireReviewerAsync(request, state, human.PrincipalId, ct).ConfigureAwait(false);
            }
            if (request.Operation.SandboxRead is not null)
            {
                var reads = sandboxReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                await reads.RequireInspectionAsync(request, run, ct).ConfigureAwait(false);
                await reads.RequireInspectionReviewerAsync(request, state, human.PrincipalId, ct).ConfigureAwait(false);
            }
            if (request.Operation.WorkspaceMutation is not null)
            {
                await (workspaceMutations ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireSavedAsync(request, run, ct).ConfigureAwait(false);
                await TenantWorkspaceMutationOperationRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
            }
            if (request.Operation.AutomationMutation is { } automationMutation)
            {
                var observed = await (automationAdmissions ?? throw new AccessDeniedException("tenant_effect_unavailable")).Mutations.PrepareAsync(new(tenant, run.AgentRunId), null, automationMutation.ToolName, automationMutation.ArgumentsJson, ct, automationMutation).ConfigureAwait(false);
                await TenantAutomationMutationRules.ReviewerAsync(request, observed.Source, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
            }
            if (request.Operation.ManagementMutation is { } managementMutation)
            {
                var observed = await (managementTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).PrepareAsync(new(tenant, run.AgentRunId), null, managementMutation.ToolName, managementMutation.ArgumentsJson, ct, managementMutation).ConfigureAwait(false);
                await TenantManagementOperationTarget.ReviewerAsync(request, observed, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
            }
            if (request.Operation.ManagementRead is { } managementRead)
            {
                var observed = await (managementTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).Reads.PrepareAsync(new(tenant, run.AgentRunId), null, managementRead.ToolName, managementRead.ArgumentsJson, ct, managementRead).ConfigureAwait(false);
                await TenantManagementReadService.ReviewerAsync(request, observed, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
            }
            if (request.Operation.AutomationRead is { } automationRead)
            {
                var observed = await (automationAdmissions ?? throw new AccessDeniedException("tenant_effect_unavailable")).Reads.PrepareAsync(new(tenant, run.AgentRunId), null, automationRead.ToolName, automationRead.ArgumentsJson, ct, automationRead).ConfigureAwait(false);
                await TenantAutomationReadService.ReviewerAsync(request, observed, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
            }
            if (request.Operation.ExperienceOperation is { } experience)
            {
                var observed = await (experienceOperations ?? throw new AccessDeniedException("tenant_effect_unavailable")).PrepareAsync(new(tenant, run.AgentRunId), null, experience.ToolName, experience.ArgumentsJson, ct, experience).ConfigureAwait(false);
                await TenantExperienceConsentRules.ExecutorAsync(request, observed, authorizer, ct).ConfigureAwait(false);
                await experienceOperations.ReviewerAsync(request, observed, human.PrincipalId, ct).ConfigureAwait(false);
            }
            if (request.Operation.ContinuityRead is { } continuity)
            {
                var observed = await (continuityReads ?? throw new AccessDeniedException("tenant_effect_unavailable")).PrepareAsync(new(tenant, run.AgentRunId), null, continuity.ToolName, continuity.ArgumentsJson, ct, continuity).ConfigureAwait(false);
                await continuityReads.ReviewerAsync(request, observed, human.PrincipalId, ct).ConfigureAwait(false);
            }
            if (request.Operation.MemoryOperation is { } memory)
            {
                var observed = await (memoryOperations ?? throw new AccessDeniedException("tenant_effect_unavailable")).PrepareAsync(new(tenant, run.AgentRunId), null, memory.ToolName, memory.ArgumentsJson, ct, memory).ConfigureAwait(false);
                await memoryOperations.ReviewerAsync(request, observed, human.PrincipalId, ct).ConfigureAwait(false);
            }
            if (request.Operation.WorkspaceRead is not null)
            {
                var reads = workspaceReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                await reads.RequireInspectionAsync(request, run, ct).ConfigureAwait(false);
                await reads.RequireInspectionReviewerAsync(request, state, human.PrincipalId, ct).ConfigureAwait(false);
            }
            if (request.Operation.AttachmentRead is not null)
            {
                var reads = attachmentReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                await reads.RequireInspectionAsync(request, run, ct).ConfigureAwait(false);
                await reads.RequireInspectionReviewerAsync(request, state, human.PrincipalId, ct).ConfigureAwait(false);
            }
            if (request.Operation.CompletionRead is not null)
            {
                var reads = completionReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                await reads.RequireInspectionAsync(request, run, ct).ConfigureAwait(false);
                await reads.RequireInspectionReviewerAsync(request, state, human.PrincipalId, ct).ConfigureAwait(false);
            }
            if (request.Operation.ArtifactVerification is not null)
            {
                var reads = artifactReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                await reads.RequireInspectionAsync(request, run, ct).ConfigureAwait(false);
                await reads.RequireInspectionReviewerAsync(request, state, human.PrincipalId, ct).ConfigureAwait(false);
            }
            if (request.Operation.CredentialRead is not null)
            {
                var reads = credentialReads ?? throw new AccessDeniedException("tenant_effect_unavailable");
                await reads.RequireInspectionAsync(request, run, ct).ConfigureAwait(false);
                await reads.RequireInspectionReviewerAsync(request, state, human.PrincipalId, ct).ConfigureAwait(false);
            }
            if (request.Operation.BackgroundStart is not null)
            {
                await (backgroundCreations ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(request, run, state, ct).ConfigureAwait(false);
                await TenantBackgroundCreationRules.ReviewerAsync(state, request, human.PrincipalId, authorizer, ct).ConfigureAwait(false);
            }
            if (request.Operation.BrowserCommand is { } browser)
            {
                await (browserTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(request, run, ct).ConfigureAwait(false);
                await TenantBrowserOperationRules.RequireReviewerAsync(state, request.Operation.InstanceId, browser, request.OriginalInitiatorPrincipalId,
                    human.PrincipalId, authorizer, ct, request.Kind).ConfigureAwait(false);
            }
            var target = TenantApprovalRules.Scope(tenant, request.Operation);
            var eligibility = await operations.Check(state, run, request.Permission, target, ct, purpose: ExecutionAuthorizationPurpose.ApprovalDecision).ConfigureAwait(false);
            if (eligibility.Decision == OperationDecisionKind.Denied || request.Kind == ApprovalKind.Review && eligibility.ApprovalKind == ApprovalKind.ExceptionalGrant)
                throw new AccessDeniedException("approval_authority_changed");
            if (!await operations.QualifiesAsync(state, request.OriginalInitiatorPrincipalId, human.PrincipalId,
                PermissionCatalog.Get(request.Permission), target, request.Kind, ct).ConfigureAwait(false)) throw new AccessDeniedException("approver_unqualified");
        }
        var nextRequest = request.Decide(outcome, expectedRequestRevision, at);
        var decision = new TenantApprovalDecision(ids.NewId(), requestId, tenant, human.PrincipalId, nextRequest.Status, actionHash, nextRequest.Revision, at);
        var grant = outcome == TenantApprovalStatus.Approved && request.Kind == ApprovalKind.ExceptionalGrant
            ? new TenantOperationGrant(ids.NewId(), requestId, tenant, request.ExecutorPrincipalId, request.Operation.OperationId, actionHash,
                "operation:" + request.Operation.OperationId.ToString("D"), OperationGrantStatus.Available, 1, request.ExpiresAt) : null;
        var signal = new ApprovalProcessedSignal(ids.NewId(), tenant, requestId, request.RunId, nextRequest.Revision, 1, at, null);
        var audit = new AuthorizationAudit(ids.NewId(), tenant, human.PrincipalId, "approvals.decide", requestId.ToString("D"), "allowed",
            outcome switch { TenantApprovalStatus.Approved => "approval_approved", TenantApprovalStatus.Cancelled => "approval_cancelled", _ => "approval_rejected" }, at);
        if (!await approvals.TryDecideApprovalAsync(Next(state), state.Tenant.Revision, nextRequest, expectedRequestRevision, decision, grant, signal, human, audit, ct).ConfigureAwait(false))
            throw new SecurityConflictException("stale_revision");
        return nextRequest;
    }

    public async ValueTask<int> DispatchPendingAsync(CancellationToken ct = default)
    {
        var at = time.GetUtcNow();
        foreach (var request in await approvals.DueApprovalsAsync(at, 100, ct).ConfigureAwait(false))
            await approvals.TryExpireApprovalAsync(request.TenantId, request.RequestId, request.Revision,
                new(ids.NewId(), request.TenantId, request.RequestId, request.RunId, checked(request.Revision + 1), 1, at, null), at, ct).ConfigureAwait(false);
        var count = 0;
        foreach (var signal in await approvals.PendingApprovalSignalsAsync(100, ct).ConfigureAwait(false))
        {
            var request = await approvals.FindApprovalAsync(signal.TenantId, signal.RequestId, ct).ConfigureAwait(false);
            if (request?.Operation.AutomationAdmission is not null)
            { if (await ProcessAutomationAdmissionAsync(signal, request, ct).ConfigureAwait(false)) count++; }
            else if (request?.Operation.ReportSourceRead is not null)
            { if (await (reportRuntime ?? throw new AccessDeniedException("tenant_effect_unavailable")).SettleTenantReportSourceReadAsync(signal, ids.NewId(), ct).ConfigureAwait(false)) count++; }
            else if (request?.Operation.BackgroundReport is not null)
            { if (await ProcessBackgroundReportAsync(signal, request, ct).ConfigureAwait(false)) count++; }
            else if (await approvals.TryResumeApprovalAsync(signal, time.GetUtcNow(), ct, new(ids.NewId(), ids.NewId())).ConfigureAwait(false)) count++;
        }
        return count;
    }

    private async ValueTask RequireRead(TenantSecurityState state, Guid human, TenantApprovalRequest request, CancellationToken ct, TenantOperationReceipt? recorded = null)
    {
        if (request.Operation.SandboxCommand?.Evaluation is not null)
        {
            await (evaluationSandbox ?? throw new AccessDeniedException("approval_unavailable")).ReadAsync(state, request, human, ct);
            return;
        }
        if (request.Operation.ArtifactPublication is not null)
            await TenantArtifactReviewRules.ReadAsync(state, request, human, authorizer, ct).ConfigureAwait(false);
        if (request.Operation.WorkspaceMutation is not null)
        {
            try { await TenantWorkspaceMutationOperationRules.ReadAsync(state, request, human, authorizer, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { throw new AccessDeniedException("approval_unavailable"); }
        }
        if (request.Operation.AutomationMutation is not null)
            _ = await (automationAdmissions ?? throw new AccessDeniedException("approval_unavailable")).Mutations.InspectionAsync(request, human, ct).ConfigureAwait(false);
        if (request.Operation.ManagementMutation is not null)
        {
            if (request.Status == TenantApprovalStatus.Approved && (request.Operation.ManagementMutation.Skill is not null || request.Operation.ManagementMutation.Resource is not null) && recorded is null)
                recorded = await effects.FindOperationReceiptAsync(request.TenantId, request.Operation.OperationId, ct).ConfigureAwait(false);
            _ = await (managementTargets ?? throw new AccessDeniedException("approval_unavailable")).InspectionAsync(request, human, ct, recorded).ConfigureAwait(false);
        }
        if (request.Operation.ManagementRead is not null)
            _ = await (managementTargets ?? throw new AccessDeniedException("approval_unavailable")).Reads.InspectionAsync(request, human, ct).ConfigureAwait(false);
        if (request.Operation.AutomationRead is not null)
            _ = await (automationAdmissions ?? throw new AccessDeniedException("approval_unavailable")).Reads.InspectionAsync(request, human, ct).ConfigureAwait(false);
        if (request.Operation.ExperienceOperation is not null)
            _ = await (experienceOperations ?? throw new AccessDeniedException("approval_unavailable")).InspectionAsync(request, human, ct).ConfigureAwait(false);
        if (request.Operation.ContinuityRead is not null)
            _ = await (continuityReads ?? throw new AccessDeniedException("tenant_effect_unavailable")).InspectionAsync(request, human, ct).ConfigureAwait(false);
        if (request.Operation.MemoryOperation is not null)
            _ = await (memoryOperations ?? throw new AccessDeniedException("approval_unavailable")).InspectionAsync(request, human, ct).ConfigureAwait(false);
        if (request.Operation.WorkspaceRead is not null)
        {
            try { await (workspaceReads ?? throw new AccessDeniedException("approval_unavailable")).RequireInspectionReadAsync(request, state, human, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { throw new AccessDeniedException("approval_unavailable"); }
        }
        if (request.Operation.AttachmentRead is not null)
        {
            try { await (attachmentReads ?? throw new AccessDeniedException("approval_unavailable")).RequireInspectionReadAsync(request, state, human, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { throw new AccessDeniedException("approval_unavailable"); }
        }
        if (request.Operation.ReportSourceRead is not null)
            await (reportSourceReads ?? throw new AccessDeniedException("approval_unavailable")).RequireInspectionAsync(request, state, human, ct).ConfigureAwait(false);
        if (request.Operation.CompletionRead is not null)
        {
            try { await (completionReads ?? throw new AccessDeniedException("approval_unavailable")).RequireInspectionReadAsync(request, state, human, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { throw new AccessDeniedException("approval_unavailable"); }
        }
        if (request.Operation.ArtifactVerification is not null)
        {
            try { await (artifactReads ?? throw new AccessDeniedException("approval_unavailable")).RequireInspectionReadAsync(request, state, human, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { throw new AccessDeniedException("approval_unavailable"); }
        }
        if (request.Operation.CredentialRead is not null)
        {
            try { await (credentialReads ?? throw new AccessDeniedException("approval_unavailable")).RequireInspectionReadAsync(request, state, human, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { throw new AccessDeniedException("approval_unavailable"); }
        }
        if (request.Operation.BrowserCommand is { } browser)
        {
            try { await TenantBrowserOperationRules.RequirePermissionsAsync(state, request.Operation.InstanceId, browser, human, authorizer, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { throw new AccessDeniedException("approval_unavailable"); }
        }
        if (request.Operation.SandboxRead is not null)
        {
            try { await (sandboxReads ?? throw new AccessDeniedException("approval_unavailable")).RequireInspectionReadAsync(request, state, human, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { throw new AccessDeniedException("approval_unavailable"); }
        }
        if(request.Operation.HttpRequest is not null)
            foreach(var (key,scope) in TenantHttpOperationRules.Targets(request.TenantId,request.Operation.InstanceId,request.SessionId))
                if(!(await authorizer.CheckTenantAsync(state,human,PermissionCatalog.Get(key),scope,ct).ConfigureAwait(false)).Allowed)throw new AccessDeniedException("approval_unavailable");
        if (request.Operation.AccountAction is { } accountAction)
        {
            try { await TenantAccountActionRules.PermissionsAsync(state, request.Operation.InstanceId, accountAction, TenantWorkspaceRules.Context(request.Operation), human, authorizer, true, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { throw new AccessDeniedException("approval_unavailable"); }
        }
        if (request.Operation.AccountRead is { } accountRead)
        {
            try { await TenantAccountReadRules.PermissionsAsync(state, accountRead, human, authorizer, true, ct).ConfigureAwait(false); }
            catch (AccessDeniedException) { throw new AccessDeniedException("approval_unavailable"); }
        }
        if (request.Operation.CredentialCheck is not null)
        {
            var read = await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get("credentials.read"),
                AccessScope.ForResource(request.TenantId, "credential", request.ResourceId), ct).ConfigureAwait(false);
            if (!read.Allowed) throw new AccessDeniedException("approval_unavailable");
        }
        if ((request.Operation.SandboxCommand?.ProfileId ?? request.Operation.SandboxControl?.ProfileId) is { } profileId)
        {
            foreach (var target in new[] { ("sessions.read", TenantSessionRules.Scope(request.TenantId, request.SessionId)),
                ("sandbox.read", TenantSandboxProfileRules.Scope(request.TenantId, profileId)) })
                if (!(await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get(target.Item1), target.Item2, ct).ConfigureAwait(false)).Allowed)
                    throw new AccessDeniedException("approval_unavailable");
        }
        if (request.Operation.SandboxControl is not null && !(await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get("sandbox.read"),
            AccessScope.ForResource(request.TenantId, "agent", request.ResourceId), ct).ConfigureAwait(false)).Allowed) throw new AccessDeniedException("approval_unavailable");
        if (request.Operation.AutomationAdmission is not null) await TenantAutomationAdmissionApprovalRules.ReadAsync(state, request, human, authorizer, ct).ConfigureAwait(false);
        if (request.Operation.BackgroundReport is not null) await TenantBackgroundReportRules.ReadAsync(state, request, human, authorizer, ct).ConfigureAwait(false);
        if (request.Operation.BackgroundStart is not null) await TenantBackgroundCreationRules.ReadAsync(state, request, human, authorizer, ct).ConfigureAwait(false);
        var actionScope = TenantApprovalRules.Scope(request.TenantId, request.Operation);
        if (request is { Status: TenantApprovalStatus.Approved, Operation.ManagementMutation.Skill.Result.Deleted: true })
        {
            recorded ??= await effects.FindOperationReceiptAsync(request.TenantId, request.Operation.OperationId, ct).ConfigureAwait(false);
            if (TenantManagementSkillRules.CompletedDeletion(request, recorded))
            {
                // The immutable receipt proves the old child address. Only current
                // tenant-wide readers/managers may inspect it after its grant was retired.
                actionScope = AccessScope.ForTenant(request.TenantId);
                if (!(await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get("skills.read"), actionScope, ct).ConfigureAwait(false)).Allowed)
                    throw new AccessDeniedException("approval_unavailable");
            }
        }
        if (request is { Status: TenantApprovalStatus.Approved, Operation.ManagementMutation.Resource.Result.Deleted: true })
        {
            recorded ??= await effects.FindOperationReceiptAsync(request.TenantId, request.Operation.OperationId, ct).ConfigureAwait(false);
            if (TenantManagementResourceRules.CompletedDeletion(request, recorded))
            {
                actionScope = AccessScope.ForTenant(request.TenantId);
                if (!(await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get("resources.read"), actionScope, ct).ConfigureAwait(false)).Allowed)
                    throw new AccessDeniedException("approval_unavailable");
            }
        }
        foreach (var pair in new[] { ("approvals.read", AccessScope.ForResource(request.TenantId, "approval", request.RequestId.ToString("D"))),
            (request.Permission, actionScope) })
        {
            var decision = await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get(pair.Item1), pair.Item2, ct).ConfigureAwait(false);
            if (!decision.Allowed) throw new AccessDeniedException("approval_unavailable");
        }
    }
    private static TenantSecurityState Next(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = checked(state.Tenant.Revision + 1) } };
}
