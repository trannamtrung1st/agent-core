using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantApprovedOperationStore
{
    private readonly Dictionary<Guid, TenantOperationReceipt> _operationReceipts = [];
    public ValueTask<TenantOperationReceipt?> FindDirectOperationReceiptAsync(Guid tenantId, Guid runId, string sourceCallId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_operationReceipts.Values.SingleOrDefault(r =>
            r.TenantId == tenantId && r.RunId == runId && r.SourceCallId == sourceCallId));
    }
    public ValueTask<TenantApprovalDecision?> FindApprovalDecisionAsync(Guid tenantId, Guid requestId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_approvalDecisions.GetValueOrDefault(requestId) is { } d && d.TenantId == tenantId ? d : null); }
    public ValueTask<TenantOperationReceipt?> FindOperationReceiptAsync(Guid tenantId, Guid operationId, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_operationReceipts.GetValueOrDefault(operationId) is { } r && r.TenantId == tenantId ? r : null); }

    public ValueTask<TenantOperationReceipt?> TryCommitLocalEffectAsync(TenantSecurityState next, long expectedSecurityRevision,
        TenantOperationCommit commit, AuthorizationAudit audit, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); next = IamStateRules.Freeze(next);
        lock (_gate) lock (sessionBacking.AdmissionGate) lock (instances.CredentialGate) lock (definitions.TenantGate) lock (sandboxBacking.TenantGate) lock (isolationBacking.IsolationGate)
        {
            if (_operationReceipts.GetValueOrDefault(commit.OperationId) is { } previous)
                return ValueTask.FromResult<TenantOperationReceipt?>(TenantLocalEffectRules.MatchingReceipt(previous, next.Tenant.TenantId, commit));
            if (!_tenants.TryGetValue(next.Tenant.TenantId, out var before) || before.Tenant.Revision != expectedSecurityRevision) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var request = _approvals.GetValueOrDefault(commit.RequestId); var decision = _approvalDecisions.GetValueOrDefault(commit.RequestId);
            var run = sessionBacking.ReadTenantRun(next.Tenant.TenantId, commit.RunId);
            if (request is null || decision is null || run is null || run.Revision != commit.ExpectedRunRevision) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            var source = request.RunId != run.AgentRunId ? sessionBacking.ReadTenantRun(request.TenantId, request.RunId) : null;
            var grant = _operationGrants.GetValueOrDefault(commit.RequestId);
            TenantLocalEffectRules.Validate(request, decision, grant, run, commit, source);
            if (commit.Outcome == TenantOperationOutcome.Completed) RequireTenantEventRead(run, ct);
            var target = FindTenantAgentAsync(next.Tenant.TenantId, request.Operation.InstanceId, ct).Result;
            if (target is null) return ValueTask.FromResult<TenantOperationReceipt?>(null);
            TenantSessionRules.RequireExecutor(before, target); ValidateAgentActor(before, request.ExecutorPrincipalId);
            var at = (clock ?? TimeProvider.System).GetUtcNow();
            commit = TenantLocalEffectRules.AtCommit(commit, request, grant, run, target, at, source);
            var correlated = RootApproval(request.TenantId, request.RootActivationId, request.ExecutorPrincipalId, request.Fingerprint);
            if (correlated is not null && correlated.RequestId != request.RequestId)
                commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "root_operation_conflict" };
            if (commit.Outcome == TenantOperationOutcome.Completed)
            {
                try { ValidateAgentActor(before, decision.HumanPrincipalId); }
                catch (AccessDeniedException) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "grantor_authority_lost" }; }
                if (commit.Outcome == TenantOperationOutcome.Completed && request.OriginalInitiatorPrincipalId is { } requester)
                    try { ValidateAgentActor(before, requester); }
                    catch (AccessDeniedException) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = "requester_unavailable" }; }
            }
            audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
            TenantLocalEffectRules.ValidateTransaction(next, expectedSecurityRevision, request, audit);
            IamStateRules.ValidateAgentContinuity(before.SystemIdentities, next); EnsureReferences(next, audit, ct: ct);
            at = (clock ?? TimeProvider.System).GetUtcNow();
            commit = TenantLocalEffectRules.AtCommit(commit, request, grant, run, target, at, source);
            audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
            (SandboxLease Lease, SandboxExecution? Execution)? control = null;
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.SandboxControl is not null)
            {
                control = RequireSandboxControlOperation(request, run, target);
                TenantSandboxControlOperationRules.RequirePermissionsAsync(before, sessionBacking.ReadTenantSession(request.TenantId, request.SessionId)!.Owner,
                    request.Operation.SandboxControl.ProfileId, decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            }
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.SandboxRead is not null)
            {
                var owner = RequireSandboxReadOperation(request, run, target);
                TenantSandboxReadOperationRules.RequireReviewerAsync(before, owner, request.Operation.SandboxRead, request.OriginalInitiatorPrincipalId,
                    decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            }
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.WorkspaceRead is not null)
            {
                RequireWorkspaceReadOperation(request, run, target);
                TenantWorkspaceReadOperationRules.ReviewerAsync(before, target.Instance.InstanceId, request.Operation.WorkspaceRead, TenantWorkspaceRules.Context(request.Operation), request.OriginalInitiatorPrincipalId,
                    decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            }
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.AttachmentRead is not null)
            {
                RequireAttachmentReadOperation(request, run, target);
                TenantAttachmentReadRules.ReviewerAsync(before, request.Operation.AttachmentRead, request.OriginalInitiatorPrincipalId,
                    decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            }
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.CompletionRead is not null)
            {
                RequireCompletionReadOperation(request, run, target);
                TenantCompletionReadRules.ReviewerAsync(before, request.Operation.CompletionRead, request.OriginalInitiatorPrincipalId,
                    decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            }
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.ArtifactVerification is not null)
            {
                RequireArtifactVerificationOperation(request, run, target);
                TenantArtifactVerificationRules.ReviewerAsync(before, request.Operation.ArtifactVerification, request.OriginalInitiatorPrincipalId,
                    decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            }
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.AccountRead is not null)
            {
                RequireAccountReadOperation(request, run, target);
                TenantAccountReadRules.ReviewerAsync(before, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            }
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.CredentialRead is not null)
            {
                RequireCredentialReadOperation(request, run, target);
                TenantCredentialReadOperationRules.ReviewerAsync(before, request.Operation.CredentialRead, request.OriginalInitiatorPrincipalId,
                    decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
            }
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.ContinuityRead is not null)
            {
                try
                {
                    var observed = RequireContinuityOperation(request, run, ct);
                    TenantContinuityReadRules.ReviewerAsync(request, observed, decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
                }
                catch (AccessDeniedException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                catch (SecurityConflictException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode };
            }
            TenantAgentConfigurationChange? managementChange = null;
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.ManagementMutation is not null)
            {
                try {
                    var managementState = RequireManagementMutation(request, run, ct, requireStagedContent: true);
                    TenantManagementMutationRules.ReviewerAsync(request, managementState.Reader, request.Operation.ManagementMutation, decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
                    managementChange = managementState.Change;
                } catch (AccessDeniedException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                catch (SecurityConflictException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode };
            }
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.ManagementRead is { } managementRead)
            {
                try {
                    var observed = ObserveManagementRead(run, managementRead.ToolName, managementRead.ArgumentsJson, managementRead, null, ct);
                    TenantManagementReadService.ReviewerAsync(request, observed, decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
                } catch (AccessDeniedException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                catch (SecurityConflictException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode };
            }
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.AutomationRead is { } automationRead)
            {
                try {
                    var observed = ObserveAutomationRead(run, automationRead.ToolName, automationRead.ArgumentsJson, automationRead, null, ct);
                    TenantAutomationReadService.ReviewerAsync(request, observed, decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
                } catch (AccessDeniedException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                catch (SecurityConflictException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode };
            }
            TenantAutomationMutationObservation? automationMutation = null;
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.AutomationMutation is not null)
            {
                try {
                    automationMutation = RequireAutomationMutationApproval(request, run, ct);
                    RequireAutomationInvocation(automationMutation, request.Operation.AutomationMutation!, commit.AutomationInvocationAdmission, ct);
                    TenantAutomationMutationRules.ReviewerAsync(request, automationMutation, decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
                } catch (AccessDeniedException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                catch (SecurityConflictException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode };
            }
            IReadOnlyList<AgentCore.Domain.Experience.TenantExperience> experienceValues = [];
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.ExperienceOperation is not null)
            {
                try
                {
                    experienceValues = RequireExperienceEffect(request, run, decision.HumanPrincipalId, at, ct);
                }
                catch (AccessDeniedException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                catch (SecurityConflictException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode };
            }
            IReadOnlyList<AgentCore.Domain.Memory.TenantLearnedMemory> memoryValues = [];
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.MemoryOperation is not null)
            {
                try
                {
                    var memory = RequireMemoryOperation(request, run, ct);
                    TenantMemoryOperationReview.ReviewerAsync(request, memory, decision.HumanPrincipalId, new IamAuthorizer(this), ct).AsTask().GetAwaiter().GetResult();
                    memoryValues = MemoryEffectValues(memory, at);
                }
                catch (AccessDeniedException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                catch (SecurityConflictException error) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = error.ReasonCode }; }
                audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode };
            }
            TenantArtifactMaterialization? artifact = null;
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.ArtifactPublication is not null)
            {
                try
                {
                    _ = RequireArtifactOperation(request, run, target, source);
                    try { TenantArtifactReviewRules.ReviewerAsync(before, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult(); }
                    catch (AccessDeniedException) { throw new AccessDeniedException("grantor_authority_lost"); }
                    artifact = TenantArtifactSourceRules.MaterializeAsync(run, request.Operation.ArtifactPublication, commit.ArtifactSourceContent, ct).GetAwaiter().GetResult();
                }
                catch (AccessDeniedException e) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = e.ReasonCode }; }
                catch (SecurityConflictException e) { commit = commit with { Outcome = TenantOperationOutcome.Denied, ReasonCode = e.ReasonCode }; }
                at = (clock ?? TimeProvider.System).GetUtcNow(); commit = TenantLocalEffectRules.AtCommit(commit, request, grant, run, target, at, source);
                audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
            }
            if (request.Operation.ManagementMutation?.Resource is not null)
            {
                at = (clock ?? TimeProvider.System).GetUtcNow(); commit = TenantLocalEffectRules.AtCommit(commit, request, grant, run, target, at, source);
                audit = audit with { Decision = commit.Outcome == TenantOperationOutcome.Completed ? "allowed" : "denied", ReasonCode = commit.ReasonCode, At = at };
            }
            TenantSessionRoot? childRoot = null;
            if (commit.Outcome == TenantOperationOutcome.Completed && request.Operation.BackgroundStart is not null)
            {
                var parent = RequireBackgroundCreationOperation(request, run, target);
                TenantBackgroundCreationRules.ReviewerAsync(before, request, decision.HumanPrincipalId, new IamAuthorizer(this), ct).GetAwaiter().GetResult();
                childRoot = TenantBackgroundCreationRules.Admission(request, run, parent, target, commit.BackgroundAdmission!, at);
            }
            audit = TenantOperationAuditRules.WithEffectResult(audit, before, run, request, grant?.GrantId);
            var complete = commit.Outcome == TenantOperationOutcome.Completed;
            var savedRun = run.RecordApprovedLocalEffect(run.Revision, commit.Generation, request.RequestId, commit.OperationId, commit.ActionHash, complete, at);
            var configure = complete && (request.Operation.IsLocalConfiguration || request.Operation.ManagementMutation is not null);
            var change = configure ? managementChange ?? TenantLocalEffectRules.Change(request, target.Instance) : null;
            var preparedConfiguration = configure ? TenantAgentConfigurationRules.Apply(target.Instance, change!, at) : target.Instance;
            Action? applyBinding = null;
            if (configure && change!.Binding is { } bindingPlan)
            {
                if (!bindingPlan.Remove) RequireSpaceCredentialAlias(request.TenantId, bindingPlan.Value);
                applyBinding = bindingPlan.Remove ? CredentialBacking().PrepareTenantUnbind(request.TenantId, run.AgentInstanceId, bindingPlan.Value.BindingId, bindingPlan.Value.Revision)
                    : CredentialBacking().PrepareTenantBind(request.TenantId, bindingPlan.Value, bindingPlan.CredentialRevision);
            }
            if (configure && change!.Binding is not null && applyBinding is null) throw new SecurityConflictException("management_source_changed");
            var reconcileBinding = configure && change!.Binding is { Remove: true } removed
                ? PrepareSpaceExclusionPrune(request.TenantId, run.AgentInstanceId, new HashSet<string>(StringComparer.Ordinal) { "Credential:" + removed.Value.CredentialId.ToString("D") }, audit) : null;
            var reconcileSpaces = configure ? PrepareResourceSpaceReconciliation(request.TenantId, change!, audit, ct) : null;
            ct.ThrowIfCancellationRequested();
            AgentInstance updated;
            if (configure && change!.Skill is { } skillChange)
            {
                next = TenantAgentChildScopes.RetainAvailable(next, run.AgentInstanceId, ChildCatalog(next.Tenant.TenantId, run.AgentInstanceId, change, ct));
                instances.MutateTenantSkillsAsync(skillChange, at, ct).GetAwaiter().GetResult();
                updated = instances.FindTenantBackingAsync(run.AgentInstanceId, ct).Result!;
            }
            else if (configure && change!.Resource is { } resourceChange)
            {
                next = TenantAgentChildScopes.RetainAvailable(next, run.AgentInstanceId, ChildCatalog(next.Tenant.TenantId, run.AgentInstanceId, change, ct));
                instances.MutateTenantResourcesAsync(resourceChange with { HarnessManagement = change.HarnessAssessment is null ? null : change.HarnessAssessment }, at, ct).GetAwaiter().GetResult();
                updated = instances.FindTenantBackingAsync(run.AgentInstanceId, ct).Result!;
            }
            else
            {
                updated = configure ? instances.ChangeTenantConfigurationAsync(new(target.Instance.InstanceId, target.Instance.Revision,
                Persona: change!.Persona, ExpectedPersonaRevision: change.ExpectedPersonaRevision,
                SetSettingsOverrides: change.SetSettingsOverrides, SettingsOverrides: change.SettingsOverrides, AutomationPolicyChanges: change.AutomationPolicyChanges,
                HarnessManagement: preparedConfiguration.HarnessManagement, SetUnattendedModel: change.SetUnattendedModel, UnattendedModel: change.UnattendedModel), at, ct).Result : target.Instance;
            }
            applyBinding?.Invoke(); reconcileBinding?.Invoke(); reconcileSpaces?.Invoke();
            if (complete && automationMutation is not null)
            {
                if (commit.AutomationInvocationAdmission is { } admitted) { StageTenantAutomationGraph(admitted); automationBacking.CommitTenantOccurrence(admitted.Next, admitted.Occurrence); }
                else automationBacking.SaveTenantAutomation(request.Operation.AutomationMutation!.Proposed);
            }
            var receipt = new TenantOperationReceipt(commit.OperationId, request.RequestId, request.TenantId, run.AgentRunId, run.SessionId,
                run.Admission.Activation.RootActivationId, request.ExecutorPrincipalId, target.Instance.InstanceId, commit.ActionHash, commit.Outcome, commit.ReasonCode,
                updated.Revision, updated.PersonaRevision, DateTimeOffset.FromUnixTimeMilliseconds(at.ToUnixTimeMilliseconds()),
                ManagementResource: complete && request.Operation.ManagementMutation is { Resource: not null } resourceEvidence ? TenantManagementResourceRules.Evidence(resourceEvidence, target.Instance.Revision) : null,
                ManagementSkill: complete && request.Operation.ManagementMutation is { Skill: not null } skillEvidence ? TenantManagementSkillRules.Evidence(skillEvidence, target.Instance.Revision) : null,
                AutomationMutation: complete && request.Operation.AutomationMutation is { } mutationEvidence ? TenantAutomationMutationCommitRules.Evidence(mutationEvidence) : null);
            SandboxLease? pending = null; SandboxExecution? stopped = null;
            if (complete && control is { } current)
            {
                pending = request.Operation.SandboxControl!.Kind == SandboxControlKind.ResetEnvironment ? TenantSandboxControlRules.Apply(current.Lease)
                    : current.Lease with { Revision = checked(current.Lease.Revision + 1), SafeMessage = "Stop requested. Process cleanup is pending; canonical files are retained." };
                if (request.Operation.SandboxControl.Kind == SandboxControlKind.StopProcess)
                    stopped = TenantSandboxControlRules.ApplyStop(current.Execution!, audit with { AuditId = request.RequestId, ActorPrincipalId = decision.HumanPrincipalId });
            }
            if (complete && artifact is not null)
            {
                var frame = new TenantRuntimeFrame(run.Owner, run.SessionId, run.AgentRunId, commit.Generation); var bytes = artifact.Bytes;
                (artifactBacking ?? throw new AccessDeniedException("tenant_effect_unavailable")).PublishTenantLocal(frame,
                    TenantArtifactRules.Record(frame, commit.OperationId, artifact.DisplayName, artifact.ContentType, bytes, at), bytes, false, ct);
            }
            if (complete && childRoot is not null)
            {
                var child = commit.BackgroundAdmission!;
                StageTenantBackgroundGraph(childRoot, child.Session, child.Run);
            }
            if (configure) foreach (var fact in AgentCore.Infrastructure.Persistence.CoreEventPersistence.Instance(target.Instance, updated,
                AgentCore.Domain.Triggers.TriggerOwner.ForTenant(request.TenantId, updated.InstanceId, target.PrincipalId))) AppendTenantCore(fact);
            if (commit.Outcome == TenantOperationOutcome.Completed) foreach (var value in experienceValues) SaveExperience(value);
            foreach (var memory in memoryValues) SetLearnedMemory(memory);
            sessionBacking.SaveTenantOperationRun(savedRun, run.Revision);
            if (pending is not null) sandboxBacking.SaveTenantLease(pending);
            if (stopped is not null) sandboxBacking.SaveTenantExecution(stopped);
            if (grant is not null) _operationGrants[request.RequestId] = grant with { Status = complete ? OperationGrantStatus.Consumed : OperationGrantStatus.Revoked, Revision = checked(grant.Revision + 1) };
            _operationReceipts.Add(receipt.OperationId, receipt); _tenants[request.TenantId] = next; _audit.Add(audit);
            return ValueTask.FromResult<TenantOperationReceipt?>(receipt);
        }
    }

}
