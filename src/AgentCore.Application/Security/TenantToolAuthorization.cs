using System.Text.Json;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;

namespace AgentCore.Application.Security;

public sealed partial class TenantToolAuthorization(ITenantRunStore runs, TenantOperationAuthorizer authorizer, TimeProvider time,
    TenantApprovalService? approvals = null, TenantRunConfigurationResolver? configuration = null,
    TenantPreferenceOperationService? preferences = null, TenantSandboxRuntimeReadService? sandboxReads = null, TenantBrowserReadService? browserReads = null,
    TenantCredentialRuntimeReadService? credentialReads = null, TenantArtifactOperationService? artifactPublications = null, TenantArtifactRuntimeReadService? artifactReads = null, TenantWorkspaceRuntimeReadService? workspaceReads = null, TenantWorkspaceMutationEffectService? workspaceMutations = null, TenantAttachmentRuntimeContextService? tenantAttachments = null, IOfficeProcessor? office = null, Func<TenantRuntimeStateService>? runtimeState = null, TenantMemoryOperationService? memoryOperations = null, TenantContinuityReadService? continuityReads = null, TenantExperienceOperationService? experienceOperations = null, TenantAutomationReadService? automationReads = null, TenantAutomationMutationService? automationMutations = null, TenantManagementOperationTarget? managementTargets = null, TenantPublicWebReadService? publicWebReads = null, TenantContextToolService? contextTools = null, AgentCore.Application.Goals.GoalToolService? goalTools = null, AgentCore.Application.Tasks.TenantTaskToolService? taskTools = null)
{
    internal async ValueTask<AgentCore.Application.Execution.ResolvedAgentRunConfiguration?> ResolveConfigurationAsync(AgentCore.Domain.Conversation.SessionSnapshot snapshot, CancellationToken ct,
        AgentCore.Domain.Conversation.AgentRun? childSource = null)
    {
        var owner = await runs.ResolveTenantSessionOwnerAsync(snapshot.SessionId, ct).ConfigureAwait(false);
        if (owner is null) return null;
        if (configuration is null || owner.AgentInstanceId != snapshot.AgentInstanceId || snapshot.ProfileId is not null)
            throw new AccessDeniedException("tenant_configuration_unavailable");
        if (childSource is not null && (childSource.Owner.TenantId != owner.TenantId || childSource.AgentInstanceId != owner.AgentInstanceId
            || childSource.SessionId != owner.SessionId || childSource.Owner.ExecutorPrincipalId != owner.ExecutorPrincipalId))
            throw new AccessDeniedException("tenant_context_mismatch");
        var resolved = childSource is not null
            ? await configuration.ResolveInCapturedSpaceAsync(childSource, ct, selectionRevision: 1).ConfigureAwait(false)
            : await configuration.ResolveInSpaceAsync(owner.TenantId, owner.AgentInstanceId,
                snapshot.ContextSelection ?? throw new AccessDeniedException("space_context_required"), ct).ConfigureAwait(false);
        if (resolved.Agent.PrincipalId != owner.ExecutorPrincipalId) throw new AccessDeniedException("tenant_configuration_unavailable");
        return resolved.Configuration;
    }
    internal async ValueTask<IReadOnlyList<string>> ContextToolsAsync(AgentCore.Domain.Conversation.AgentRun run, CancellationToken ct)
    {
        IReadOnlyList<string> offered = [];
        for (var attempt = 0; contextTools is not null; attempt++)
        {
            try { offered = await contextTools.OfferedAsync(run, ct).ConfigureAwait(false); break; }
            catch (SecurityConflictException) when (attempt < 2)
            {
                // Descriptor projection can race this same Runtime's checkpoint save.
                // Refresh only the read; never repeat a model call, tool or effect.
                var fresh = await runs.FindTenantRunAsync(run.Owner.TenantId!.Value, run.AgentRunId, ct).ConfigureAwait(false);
                if (fresh is not { Status: AgentCore.Domain.Conversation.AgentRunStatus.Running, CancellationRequested: false, Claim: { } claim }
                    || fresh.Owner != run.Owner || fresh.SessionId != run.SessionId || claim.Generation != run.Claim?.Generation)
                    throw new AccessDeniedException("tenant_runtime_unavailable");
                run = fresh;
            }
        }
        if (taskTools is not null) offered = offered.Concat(await taskTools.OfferedAsync(run, ct).ConfigureAwait(false)).ToArray();
        if (goalTools is not null) offered = offered.Concat(await goalTools.OfferedAsync(run, ct).ConfigureAwait(false)).ToArray();
        var boundary = await Resolve(run.SessionId, new(false, TriggerKind.UserTurn, AgentInstanceId: run.AgentInstanceId,
            AgentRunId: run.AgentRunId, OwnedSessionId: run.SessionId, TenantId: run.Owner.TenantId, AgentRunGeneration: run.Claim?.Generation), ToolCatalog.SessionComplete, ct).ConfigureAwait(false);
        return boundary.Allowed ? offered.Append(ToolCatalog.SessionComplete).ToArray() : offered;
    }
    private sealed record Boundary(bool Tenant, bool Allowed, TenantRunReference? Reference, string Reason);
    private async ValueTask<Boundary> Resolve(Guid session, ToolExecutionAdmission? admission, string tool, CancellationToken ct)
    {
        var owner = await runs.ResolveTenantSessionOwnerAsync(session, ct).ConfigureAwait(false);
        if (owner is null) return new(false, false, null, "tenant_context_required");
        if (admission is not { AgentRunId: { } runId, AgentRunGeneration: { } generation, SupportsTools: true }
            || runId == Guid.Empty || generation == Guid.Empty || admission.TenantId != owner.TenantId
            || admission.AgentInstanceId != owner.AgentInstanceId || admission.OwnedSessionId != owner.SessionId)
            return new(true, false, null, "tenant_context_required");
        var reference = new TenantRunReference(owner.TenantId, runId);
        try
        {
            var (run, _) = await authorizer.ResolveAsync(reference, ct).ConfigureAwait(false);
            if (run.SessionId != session || run.Owner.ExecutorPrincipalId != owner.ExecutorPrincipalId) return new(true, false, null, "tenant_context_mismatch");
            // A trusted task identity is not a live execution lease. Every model tool,
            // including IAM discovery, uses the current persisted generation/deadline.
            if (run.Status != AgentCore.Domain.Conversation.AgentRunStatus.Running || run.CancellationRequested
                || run.Claim is not { } claim || claim.Generation != generation)
                return new(true, false, reference, "tenant_runtime_unavailable");
            if (claim.LeaseExpiresAtUtc <= time.GetUtcNow())
                return new(true, false, reference, "execution_claim_expired");
            if (SessionCompletionIntent.Reason(run) is not null) return new(true, false, reference, "session_completion_pending");
            if (tool == ToolCatalog.SessionComplete)
            {
                var ownedChat = await ((ITenantSessionStore)runs).FindTenantSessionAsync(owner.TenantId, session, ct).ConfigureAwait(false);
                var completionAllowed = ownedChat is not null && SessionCompletionIntent.Eligible(run, ownedChat.Snapshot, time.GetUtcNow())
                    && admission is { Detached: false, TriggerKind: TriggerKind.UserTurn, ConfigurationContinuationTarget: null };
                return new(true, completionAllowed, reference, completionAllowed ? "allowed" : "session_completion_unavailable");
            }
            if (tool == ToolCatalog.AppMessageSend)
            {
                var allowedMessage = ApplicationMessageToolPolicy.AllowsTenantRun(run)
                    && admission is { Detached: false, TriggerKind: TriggerKind.UserTurn, IntermediateMessagingAllowed: true,
                        ConfigurationContinuationTarget: null }
                    && TenantConfigurationContinuationRules.Target(run) is null;
                return new(true, allowedMessage, reference, allowedMessage ? "allowed" : "application_message_unavailable");
            }
            if (TenantTaskToolContract.IsTool(tool)) return new(true, true, reference, "task_tool_unavailable");
            if (GoalToolContract.IsTool(tool)) return new(true, true, reference, "goal_tool_unavailable");
            if (TenantContextToolContract.IsTool(tool)) return new(true, TenantTaskExecutionContract.Allows(run), reference, "context_tool_unavailable");
            if (TenantManagementMutationContract.IsTool(tool))
            {
                var allowedChange = TenantManagementReadContract.Allows(run);
                return new(true, allowedChange, reference, allowedChange ? "allowed" : "management_unavailable");
            }
            if (TenantManagementReadContract.IsTool(tool))
            {
                var allowedRead = tool == ToolCatalog.SkillsLoad ? TenantSkillLoadContract.Allows(run) || TenantCoreProcedureContract.Allows(run) : TenantManagementReadContract.Allows(run);
                return new(true, allowedRead, reference, allowedRead ? "allowed" : "management_read_unavailable");
            }
            if (tool == ToolCatalog.CapabilitiesLoad)
            {
                var allowedDiscovery = TenantTaskExecutionContract.Allows(run) && run.Admission.Configuration?.Definition.ExecutionPolicy?.Capabilities is not null;
                return new(true, allowedDiscovery, reference, allowedDiscovery ? "allowed" : "capability_discovery_unavailable");
            }
            if (TenantBackgroundToolContract.IsTool(tool))
            {
                var background = TenantBackgroundToolContract.Allows(run, tool);
                return new(true, background, reference, background ? "allowed" : "tenant_effect_unavailable");
            }
            if (tool == ToolCatalog.ExecutionWait)
            {
                var allowedWait = TenantWaitRules.Allows(run);
                return new(true, allowedWait, reference, allowedWait ? "allowed" : "wait_unavailable");
            }
            if (!IamToolContracts.IsInspection(tool)
                && (run.Admission.ExecutionContext?.Origin == AgentCore.Domain.Conversation.ExecutionOrigin.CompletionReport
                    || TenantTaskExecutionContract.IsBackground(run) && !TenantTaskExecutionContract.IsTool(tool) && !TenantTaskExecutionContract.IsNativeInspection(tool) && tool != ToolCatalog.RequestAuthorization))
                return new(true, false, reference, "tenant_effect_unavailable");
            var task = TenantTaskExecutionContract.Allows(run);
            var allowed = IamToolContracts.IsInspection(tool)
                || task && (tool == ToolCatalog.HttpRequest || TenantPublicWebReadContract.IsRead(tool)
                    || TenantAutomationMutationContract.IsTool(tool) || TenantAutomationReadContract.IsTool(tool)
                    || TenantExperienceToolContract.IsTool(tool) || TenantMemoryToolContract.IsTool(tool))
                || TenantWorkspaceMutationContract.IsMutation(tool) || TenantWorkspaceReadContract.IsRead(tool)
                || tool is ToolCatalog.AttachmentsRead or ToolCatalog.ArtifactsCreate or ToolCatalog.ArtifactsCreateFromWorkspace or ToolCatalog.ArtifactsVerify
                || TenantCredentialDiscoveryContract.IsTool(tool) || tool == ToolCatalog.RequestAuthorization || tool == TenantPreferenceToolContract.Name
                || TenantSandboxInspectionContract.IsInspection(tool) || TenantBrowserInspectionContract.IsInspection(tool) || tool == ToolCatalog.AgentStateInspect;
            return new(true, allowed, reference, allowed ? "allowed" : "tenant_effect_unavailable");
        }
        catch (AccessDeniedException ex) { return new(true, false, reference, ex.ReasonCode); }
    }
    public async ValueTask<ToolPolicyDecision?> PolicyAsync(Guid session, ModelToolCall call, ToolExecutionAdmission? admission, CancellationToken ct)
    {
        var boundary = await Resolve(session, admission, call.Name, ct).ConfigureAwait(false);
        if (boundary.Tenant && !boundary.Allowed && boundary.Reference is { } failed)
            await authorizer.RecordFailureAsync(failed, "", boundary.Reason, AgentCore.Domain.Security.ExecutionAuthorizationPurpose.ToolBoundary, ct, session).ConfigureAwait(false);
        return boundary.Tenant || IsTenantTool(call.Name) || admission?.TenantId is not null
            ? boundary.Allowed ? ToolPolicyDecision.Allow : ToolPolicyDecision.Deny : null;
    }
    public async ValueTask<ToolExecutionResult?> ExecuteAttachmentAsync(Guid session, ModelToolCall call, ToolExecutionAdmission? admission, CancellationToken ct, int remainingBytes = ToolLimits.MaxOutputBytes)
    {
        if (call.Name != ToolCatalog.AttachmentsRead) return null;
        var boundary = await Resolve(session, admission, call.Name, ct).ConfigureAwait(false);
        if (!boundary.Tenant && admission?.TenantId is null) return null;
        if (!boundary.Allowed || tenantAttachments is null || admission?.AgentRunGeneration is not { } generation)
        {
            if (boundary.Reference is { } reference)
                await authorizer.RecordFailureAsync(reference, "attachments.read", boundary.Reason == "allowed" ? "tenant_effect_unavailable" : boundary.Reason,
                    AgentCore.Domain.Security.ExecutionAuthorizationPurpose.ToolBoundary, ct, session).ConfigureAwait(false);
            return new(Error("forbidden", boundary.Reason == "allowed" ? "tenant_effect_unavailable" : boundary.Reason));
        }
        try
        {
            var run = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
            return await tenantAttachments.InspectReadAsync(new(run.Owner, session, run.AgentRunId, generation), call.ArgumentsJson,
                admission.SupportsVision, remainingBytes, office, ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is JsonException or ArgumentException) { return new(Error("invalid", "invalid_arguments")); }
        catch (AccessDeniedException e) { return new(Error("forbidden", e.ReasonCode)); }
        catch (SecurityConflictException e) { return new(Error("conflict", e.ReasonCode)); }
    }
    public async ValueTask<string?> ExecuteAsync(Guid session, ModelToolCall call, ToolExecutionAdmission? admission, CancellationToken ct, int remainingBytes = ToolLimits.MaxOutputBytes)
    {
        if (call.Name == ToolCatalog.AttachmentsRead) return (await ExecuteAttachmentAsync(session, call, admission, ct, remainingBytes).ConfigureAwait(false))?.Text;
        var boundary = await Resolve(session, admission, call.Name, ct).ConfigureAwait(false);
        if (!boundary.Tenant && !IsTenantTool(call.Name) && admission?.TenantId is null) return null;
        if (!boundary.Allowed)
        {
            if (boundary.Tenant && boundary.Reference is { } failed)
                await authorizer.RecordFailureAsync(failed, "", boundary.Reason, AgentCore.Domain.Security.ExecutionAuthorizationPurpose.ToolBoundary, ct, session).ConfigureAwait(false);
            return Error("forbidden", boundary.Reason);
        }
        if (call.Name == ToolCatalog.RequestAuthorization) return Error("invalid", "authorization_requires_runtime_suspension");
        if (call.Name is ToolCatalog.SkillsLoad or ToolCatalog.CapabilitiesLoad) return Error("invalid", "context_load_requires_runtime");
        try
        {
            if (TenantTaskToolContract.IsTool(call.Name))
            {
                if (taskTools is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "task_tool_unavailable");
                var run = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await taskTools.ExecuteAsync(new(run.Owner, session, run.AgentRunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (GoalToolContract.IsTool(call.Name))
            {
                if (goalTools is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "goal_tool_unavailable");
                var run = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await goalTools.ExecuteAsync(new(run.Owner, session, run.AgentRunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (TenantContextToolContract.IsTool(call.Name))
            {
                if (contextTools is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "context_tool_unavailable");
                var run = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await contextTools.ExecuteAsync(new(run.Owner, session, run.AgentRunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (TenantManagementMutationContract.IsTool(call.Name))
            {
                if (managementTargets is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var run = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await managementTargets.ExecuteAsync(new(run.Owner, session, run.AgentRunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (TenantManagementReadContract.IsTool(call.Name))
            {
                if (managementTargets is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var run = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await managementTargets.Reads.ExecuteAsync(new(run.Owner, session, run.AgentRunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (TenantAutomationMutationContract.IsTool(call.Name))
            {
                if (automationMutations is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var run = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await automationMutations.ExecuteAsync(new(run.Owner, session, run.AgentRunId, generation), call, ct).ConfigureAwait(false);
            }
            if (TenantAutomationReadContract.IsTool(call.Name))
            {
                if (automationReads is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var run = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await automationReads.ExecuteAsync(new(run.Owner, session, run.AgentRunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (TenantExperienceToolContract.IsTool(call.Name))
            {
                if (experienceOperations is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var run = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await experienceOperations.ExecuteAsync(new(run.Owner, session, run.AgentRunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (TenantContinuityToolContract.IsTool(call.Name))
            {
                if (continuityReads is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var run = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await continuityReads.InspectAsync(new(run.Owner, session, run.AgentRunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (TenantMemoryToolContract.IsTool(call.Name))
            {
                if (memoryOperations is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var run = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await memoryOperations.InspectAsync(new(run.Owner, session, run.AgentRunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (call.Name == ToolCatalog.AgentStateInspect)
            {
                if (runtimeState is null || admission?.AgentRunGeneration is not { } generation) throw new AccessDeniedException("state_inspection_unavailable");
                var run = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await runtimeState().InspectForRunAsync(new(run.Owner, session, run.AgentRunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (call.Name is ToolCatalog.ArtifactsCreate or ToolCatalog.ArtifactsCreateFromWorkspace)
            {
                if (artifactPublications is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var result = call.Name == ToolCatalog.ArtifactsCreateFromWorkspace
                    ? await artifactPublications.ExecuteWorkspaceAsync(boundary.Reference!, generation, call.Id, call.ArgumentsJson, ct).ConfigureAwait(false)
                    : await artifactPublications.ExecuteAsync(boundary.Reference!, generation, call.Id, call.ArgumentsJson, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { decision = JsonNamingPolicy.CamelCase.ConvertName(result.Decision.ToString()),
                    reasonCode = result.ReasonCode, applied = result.Receipt is not null, operationId = result.Receipt?.OperationId });
            }
            if (TenantWorkspaceMutationContract.IsMutation(call.Name))
            {
                if (workspaceMutations is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var current = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                var result = await workspaceMutations.ExecuteAsync(new(current.Owner, session, current.AgentRunId, generation), call, ct).ConfigureAwait(false);
                var output = JsonSerializer.SerializeToNode(new { decision = JsonNamingPolicy.CamelCase.ConvertName(result.Decision.ToString()), reasonCode = result.ReasonCode,
                    outcome = result.IsUncertain ? "indeterminate" : result.Receipt is null ? "notDispatched" : JsonNamingPolicy.CamelCase.ConvertName(result.Receipt.Outcome.ToString()),
                    applied = result.IsUncertain ? (bool?)null : result.Receipt?.Outcome == AgentCore.Domain.Security.TenantOperationOutcome.Completed, operationId = result.OperationId, replayAllowed = false });
                if (result.Receipt?.PublicDownload is { } download) output!["download"] = JsonSerializer.SerializeToNode(TenantPublicDownloadRules.Project(download));
                return output!.ToJsonString();
            }
            if(call.Name==ToolCatalog.HttpRequest)
            {
                _=TenantHttpRequestContract.Parse(call.ArgumentsJson);
                return System.Text.Json.JsonSerializer.Serialize(new { requiresApproval=true,reasonCode="http_exact_review_required" });
            }
            if (TenantPublicWebReadContract.IsRead(call.Name))
            {
                if (publicWebReads is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var current = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await publicWebReads.InspectAsync(new(current.Owner, session, current.AgentRunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (TenantWorkspaceReadContract.IsRead(call.Name))
            {
                var args = TenantWorkspaceReadContract.Parse(call.Name, call.ArgumentsJson);
                if (TenantAttachmentVirtualReadContract.IsTool(call.Name) && TenantAttachmentVirtualReadContract.IsPath(args.Path))
                {
                    if (tenantAttachments is null || admission?.AgentRunGeneration is not { } attachmentGeneration) return Error("forbidden", "tenant_effect_unavailable");
                    var attachmentRun = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                    return await tenantAttachments.InspectVirtualAsync(new(attachmentRun.Owner, session, attachmentRun.AgentRunId, attachmentGeneration), call, remainingBytes, ct).ConfigureAwait(false);
                }
                if (workspaceReads is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var current = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await workspaceReads.InspectAsync(new(current.Owner, session, current.AgentRunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (call.Name == ToolCatalog.ArtifactsVerify)
            {
                if (artifactReads is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var current = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await artifactReads.InspectAsync(new(current.Owner, session, current.AgentRunId, generation), call.ArgumentsJson, remainingBytes, ct).ConfigureAwait(false);
            }
            if (TenantCredentialDiscoveryContract.IsTool(call.Name))
            {
                if (credentialReads is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var current = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await credentialReads.InspectAsync(new(current.Owner, session, current.AgentRunId, generation), call.ArgumentsJson, remainingBytes, ct, call.Name).ConfigureAwait(false);
            }
            if (TenantBrowserInspectionContract.IsInspection(call.Name))
            {
                if (browserReads is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var current = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await browserReads.InspectAsync(new(current.Owner, session, boundary.Reference.RunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (TenantSandboxInspectionContract.IsInspection(call.Name))
            {
                if (sandboxReads is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var current = await runs.FindTenantRunAsync(boundary.Reference!.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("run_unavailable");
                return await sandboxReads.InspectForRunAsync(new(current.Owner, session, boundary.Reference.RunId, generation), call, remainingBytes, ct).ConfigureAwait(false);
            }
            if (call.Name == TenantPreferenceToolContract.Name)
            {
                if (preferences is null || admission?.AgentRunGeneration is not { } generation) return Error("forbidden", "tenant_effect_unavailable");
                var result = await preferences.ExecuteAsync(boundary.Reference!, generation, call.Id, call.ArgumentsJson, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(new { decision = JsonNamingPolicy.CamelCase.ConvertName(result.Decision.ToString()),
                    reasonCode = result.ReasonCode, applied = result.Receipt is not null, operationId = result.Receipt?.OperationId,
                    instanceRevision = result.Receipt?.InstanceRevision });
            }
            // Runtime-owned commands must not fall through to IAM inspection merely
            // because they carry an object with resourceType/resourceId fields.
            if (!IamToolContracts.IsInspection(call.Name)) return Error("forbidden", "runtime_dispatch_required");
            if (string.IsNullOrWhiteSpace(call.ArgumentsJson) || Encoding.UTF8.GetByteCount(call.ArgumentsJson) > IamToolContracts.MaxArgumentBytes)
                return Error("invalid", "invalid_arguments");
            using var document = JsonDocument.Parse(call.ArgumentsJson); var input = document.RootElement;
            var check = call.Name == ToolCatalog.IamCheckAccess;
            if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any(p => p.Name is not ("resourceType" or "resourceId") && (!check || p.Name != "permission")))
                return Error("invalid", "invalid_arguments");
            if (input.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != input.EnumerateObject().Count()) return Error("invalid", "invalid_arguments");
            string Read(string name, int max) => input.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                && value.GetString() is { Length: > 0 } text && text.Length <= max ? text : throw new ArgumentException("Invalid IAM arguments.");
            var type = Read("resourceType", 32); var id = Read("resourceId", 200);
            if (check)
            {
                var decision = await authorizer.CheckAsync(boundary.Reference!, Read("permission", 128), type, id, ct).ConfigureAwait(false);
                await RequireInspectionBoundaryAsync(session, call.Name, admission!, boundary.Reference!, ct).ConfigureAwait(false);
                return JsonSerializer.Serialize(Project(decision));
            }
            var items = await authorizer.EffectiveAsync(boundary.Reference!, type, id, ct).ConfigureAwait(false);
            await RequireInspectionBoundaryAsync(session, call.Name, admission!, boundary.Reference!, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { operations = items.Select(Project).ToArray() });
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException) { return Error("invalid", "invalid_arguments"); }
        catch (AccessDeniedException ex) when (call.Name == ToolCatalog.ArtifactsCreateFromWorkspace && ex.ReasonCode == "artifact_source_review_required")
        { return JsonSerializer.Serialize(new { decision = "requiresApproval", reasonCode = ex.ReasonCode, applied = false, operationId = (Guid?)null }); }
        catch (AccessDeniedException ex) { return Error("forbidden", ex.ReasonCode); }
        catch (SecurityConflictException ex) { return Error("conflict", ex.ReasonCode); }
        catch (AgentCore.Application.Sessions.AgentCoreException ex) when (call.Name == ToolCatalog.AgentStateInspect) { return Error(ex.Code, ex.Code); }
    }
    private async ValueTask RequireInspectionBoundaryAsync(Guid session, string tool, ToolExecutionAdmission admission,
        TenantRunReference reference, CancellationToken ct)
    {
        var current = await Resolve(session, admission, tool, ct).ConfigureAwait(false);
        if (current.Allowed && current.Reference == reference) return;
        await authorizer.RecordFailureAsync(reference, "", current.Reason, AgentCore.Domain.Security.ExecutionAuthorizationPurpose.ToolBoundary, ct, session).ConfigureAwait(false);
        throw new AccessDeniedException(current.Reason);
    }
    public async ValueTask<TenantApprovalPreparationResult> PrepareAsync(Guid session, ModelToolCall call, ToolExecutionAdmission admission, CancellationToken ct)
    {
        var boundary = await Resolve(session, admission, call.Name, ct).ConfigureAwait(false);
        if (!boundary.Tenant || !boundary.Allowed || call.Name != ToolCatalog.RequestAuthorization || boundary.Reference is null || approvals is null || configuration is null)
            throw new AccessDeniedException("tenant_context_required");
        var intent = TenantApprovalToolContract.Parse(call);
        var run = (await runs.FindTenantRunAsync(boundary.Reference.TenantId, boundary.Reference.RunId, ct).ConfigureAwait(false))!;
        if (run.Status != AgentCore.Domain.Conversation.AgentRunStatus.Running || run.Claim?.Generation != admission.AgentRunGeneration)
            throw new AccessDeniedException("tenant_runtime_unavailable");
        if(AgentCore.Application.Execution.RunFinalization.InCleanup(run) && !AgentCore.Application.Execution.RunFinalization.CleanupCall(call))throw new AccessDeniedException("cleanup_only");
        if (TenantTaskExecutionContract.IsBackground(run) && intent.WorkspaceRead is null && intent.AttachmentArguments is null && intent.ArtifactId is null
            && intent.TaskArguments is null && intent.HttpArguments is null && intent.ManagementMutation is null && intent.ManagementRead is null && intent.AutomationMutation is null && intent.AutomationRead is null && intent.Experience is null && intent.Continuity is null && intent.Memory is null && intent.WorkspaceMutation is null && intent.ArtifactArguments is null
            && intent.MetadataArguments is null && intent.AccountAction is null && intent.AccountRead is null && intent.Browser is null && intent.Sandbox is null && intent.SandboxRead is null && intent.SandboxControl is null)
        {
            await authorizer.RecordFailureAsync(boundary.Reference, "", "tenant_effect_unavailable", AgentCore.Domain.Security.ExecutionAuthorizationPurpose.ApprovalPreparation, ct, session).ConfigureAwait(false);
            throw new AccessDeniedException("tenant_effect_unavailable");
        }
        if (intent.TaskArguments is { } task)
            return await approvals.PrepareTaskDelegationAsync(boundary.Reference, task, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.AutomationMutation is { } automationMutation)
            return await approvals.PrepareAutomationMutationAsync(boundary.Reference, new(call.Id, automationMutation.Tool, automationMutation.ArgumentsJson), run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.ManagementMutation is { } managementMutation)
        {
            if (TenantManagementMutationContract.Preference(managementMutation.Tool, managementMutation.ArgumentsJson) is { } patch)
            {
                _ = await (managementTargets ?? throw new AccessDeniedException("tenant_effect_unavailable")).PreferenceSourceAsync(boundary.Reference, run.Claim!.Generation, managementMutation.Tool, managementMutation.ArgumentsJson, ct).ConfigureAwait(false);
                return await approvals.PreparePreferencesAsync(boundary.Reference, TenantPreferenceToolContract.Parameters(patch), run.Revision, run.Claim.Generation, ct,
                    TenantManagementMutationContract.Parse(managementMutation.Tool, managementMutation.ArgumentsJson).ExpectedInstanceRevision).ConfigureAwait(false);
            }
            return await approvals.PrepareManagementMutationAsync(boundary.Reference, managementMutation.Tool, managementMutation.ArgumentsJson, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        }
        if (intent.ManagementRead is { } managementRead)
            return await approvals.PrepareManagementReadAsync(boundary.Reference, new(call.Id, managementRead.Tool, managementRead.ArgumentsJson), run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.AutomationRead is { } automationRead)
            return await approvals.PrepareAutomationReadAsync(boundary.Reference, new(call.Id, automationRead.Tool, automationRead.ArgumentsJson), run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.Experience is { } experience)
            return await approvals.PrepareExperienceAsync(boundary.Reference, new(call.Id, experience.Tool, experience.ArgumentsJson), run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.Continuity is { } continuity)
            return await approvals.PrepareContinuityAsync(boundary.Reference, new(call.Id, continuity.Tool, continuity.ArgumentsJson), run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.Memory is { } memory)
            return await approvals.PrepareMemoryAsync(boundary.Reference, new(call.Id, memory.Tool, memory.ArgumentsJson), run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.WorkspaceMutation is { } mutation)
            return await approvals.PrepareWorkspaceMutationAsync(boundary.Reference, new(call.Id, mutation.Tool, mutation.ArgumentsJson), run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.WorkspaceRead is { } readWorkspace)
            return await approvals.PrepareWorkspaceReadAsync(boundary.Reference, new(call.Id, readWorkspace.Tool, readWorkspace.ArgumentsJson), run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.AttachmentArguments is { } attachment)
            return await approvals.PrepareAttachmentReadAsync(boundary.Reference, attachment, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.ArtifactId is { } artifactId)
            return await approvals.PrepareArtifactVerificationAsync(boundary.Reference, artifactId, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.ArtifactArguments is { } artifact)
            return intent.ArtifactFromWorkspace
                ? await approvals.PrepareWorkspaceArtifactAsync(boundary.Reference, artifact, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false)
                : await approvals.PrepareArtifactAsync(boundary.Reference, artifact, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if(intent.HttpArguments is { } http)
            return await approvals.PrepareHttpRequestAsync(boundary.Reference,http,run.Revision,run.Claim!.Generation,ct).ConfigureAwait(false);
        if (intent.AccountAction is { } accountAction)
            return await approvals.PrepareAccountActionAsync(boundary.Reference, accountAction, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.AccountRead is { } accountRead)
            return await approvals.PrepareAccountReadAsync(boundary.Reference, accountRead, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.MetadataArguments is { } metadata)
            return await approvals.PrepareCredentialReadAsync(boundary.Reference, metadata, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.Browser is { } browser)
            return await approvals.PrepareBrowserAsync(boundary.Reference, browser.Tool, browser.ArgumentsJson, run.Revision, run.Claim!.Generation, ct, registerCold: true, downloadArtifacts: browser.DownloadArtifacts).ConfigureAwait(false);
        if (intent.SandboxRead is { } read)
            return await approvals.PrepareSandboxReadAsync(boundary.Reference, read.Kind, read.ExecutionId, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.SandboxControl is { } control)
            return await approvals.PrepareSandboxControlAsync(boundary.Reference, control.Kind, control.ExecutionId, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.Sandbox is { } sandbox)
            return await approvals.PrepareSandboxAsync(boundary.Reference, sandbox.ProfileId, sandbox.Command, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        if (intent.CredentialReference is { } reference)
            return await approvals.PrepareCredentialCheckAsync(boundary.Reference, reference, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
        var current = await configuration.ResolveAsync(boundary.Reference.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        if (TenantLocalEffectRules.Eligibility(run, current.Agent, current.Configuration.Configuration.Definition) is { } denied)
            throw new AccessDeniedException(denied);
        return intent.Preferences is { } preferences
            ? await approvals.PreparePreferencesAsync(boundary.Reference, preferences, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false)
            : await approvals.PreparePersonaAsync(boundary.Reference, intent.Persona!, run.Revision, run.Claim!.Generation, ct).ConfigureAwait(false);
    }
    private static bool IsTenantTool(string name) => TenantTaskToolContract.IsTool(name) || GoalToolContract.IsTool(name) || TenantAutomationReadContract.IsTool(name) || IamToolContracts.IsInspection(name) || name is ToolCatalog.SessionComplete or ToolCatalog.RequestAuthorization or TenantPreferenceToolContract.Name or ToolCatalog.AgentStateInspect;
    private static object Project(OperationAuthorizationDecision decision) => new { decision = JsonNamingPolicy.CamelCase.ConvertName(decision.Decision.ToString()),
        reasonCode = decision.ReasonCode, requiredPermission = decision.Permission, targetResource = new { type = decision.Resource.ResourceType, id = decision.Resource.ResourceId },
        approvalKind = decision.ApprovalKind is { } kind ? JsonNamingPolicy.CamelCase.ConvertName(kind.ToString()) : null };
    private static string Error(string error, string reason) => JsonSerializer.Serialize(new { error, reasonCode = reason });
}
