using System.Text.Json;
using System.Text.Json.Nodes;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Security;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private async Task<SkillLoadMailboxResult> RequestSkillLoadAsync(
        EventContext cause,
        Guid responseId,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource<SkillLoadMailboxResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!TryMailbox(new SkillLoadRequested(
                WorkerContext(cause),
                responseId,
                cause.Epoch,
                argumentsJson,
                completed)))
        {
            EndWork();
            return SkillLoadMailboxResult.Failed(
                SkillLoadAdmission.Error("stale", "Skill load is no longer owned by this execution."),
                "stale");
        }

        return await completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleSkillLoadAsync(SkillLoadRequested input, CancellationToken cancellationToken)
    {
        SkillLoadMailboxResult result;
        try
        {
            result = await AdmitSkillLoadAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogHandledRuntimeFailure(ex, "skills.load", "skill_load_failed");
            result = SkillLoadMailboxResult.Failed(
                SkillLoadAdmission.Error("stale", "Skill load is no longer owned by this execution."),
                "stale");
        }

        RuntimeTelemetry.RecordSkillLoad(result.Outcome);
        input.Completed.TrySetResult(result);
    }

    private async Task<SkillLoadMailboxResult> AdmitSkillLoadAsync(
        SkillLoadRequested input,
        CancellationToken cancellationToken)
    {
        var fence = SkillLoadFence(input);
        if (fence is not null || !await OwnsWorkerAsync(input.Context, input.ResponseId, cancellationToken).ConfigureAwait(false))
        {
            return fence ?? SkillLoadMailboxResult.Failed(SkillLoadAdmission.Error("stale", "Skill load is no longer owned by this execution."), "stale");
        }

        var bound = _boundAgentRun!;
        var current = await _agentRuns.GetAsync(bound.Owner, bound.AgentRunId, cancellationToken).ConfigureAwait(false);
        if (current is null
            || current.Revision != bound.Revision
            || current.Claim?.Generation != bound.Claim!.Generation
            || current.Status != AgentRunStatus.Running
            || current.CancellationRequested
            || current.ResponseId != input.ResponseId)
        {
            return SkillLoadMailboxResult.Failed(
                SkillLoadAdmission.Error("stale", "Skill load is no longer owned by this execution."),
                "stale");
        }

        JsonElement args;
        try
        {
            args = JsonSerializer.Deserialize<JsonElement>(
                string.IsNullOrWhiteSpace(input.ArgumentsJson) ? "{}" : input.ArgumentsJson);
        }
        catch (JsonException)
        {
            return SkillLoadMailboxResult.Failed(
                SkillLoadAdmission.Error("invalid", "Tool arguments were malformed."),
                "denied");
        }

        if (!SkillLoadAdmission.TryParseIds(args, out var requestedIds, out var errorJson))
        {
            return SkillLoadMailboxResult.Failed(errorJson, "denied");
        }

        var management = await HarnessContextAsync(current, cancellationToken);
        var visibleCatalog = ManagementBuiltInSkills.Visible(current.PinnedSkillCatalog, management, initial: false);
        // The invocation ceiling depends only on this Run's public accounting.
        // Procedure-dependent rejections still require the selected private reads.
        if (current.SkillLoadCount >= SkillActivationLimits.MaxLoadInvocations)
        {
            var exhausted = SkillLoadAdmission.Plan(current.PinnedSkillCatalog, current.ActiveSkillKeys, current.SkillLoadCount, requestedIds);
            return new(exhausted.ToToolResultJson(), current.ActiveSkillKeys, exhausted.Outcome);
        }
        TenantManagementReadObservation? privateRead = null;
        if (_runtimeOwner is not null)
        {
            if (requestedIds.Distinct(StringComparer.Ordinal).Count() != requestedIds.Count
                || requestedIds.Any(k => !visibleCatalog.Any(s => s.Key == k)))
                return SkillLoadMailboxResult.Failed(SkillLoadAdmission.Error("forbidden", "Procedure is unavailable in this execution."), "denied");
            var privateIds = requestedIds.Where(k => !k.StartsWith("builtin:", StringComparison.Ordinal)).ToArray();
            if (requestedIds.Any(k => k.StartsWith("builtin:", StringComparison.Ordinal)) && !TenantCoreProcedureContract.Allows(current))
                return SkillLoadMailboxResult.Failed(SkillLoadAdmission.Error("forbidden", "Core procedure delegation is unavailable."), "denied");
            if (privateIds.Length > 0)
            {
                if (_runAuthority is not ITenantRunContext authority || !TenantSkillLoadContract.Allows(current))
                    return SkillLoadMailboxResult.Failed(SkillLoadAdmission.Error("forbidden", "Private procedure loading is unavailable."), "denied");
                (TenantManagementReadObservation Observation, OperationAuthorizationDecision Decision) prepared;
                try { prepared = await authority.PrepareSkillLoadAsync(current, privateIds, cancellationToken).ConfigureAwait(false); }
                catch (Exception error) when (error is AccessDeniedException or SecurityConflictException)
                { return SkillLoadMailboxResult.Failed(SkillLoadAdmission.Error("forbidden", "Procedure is unavailable in this execution."), "denied"); }
                privateRead = prepared.Observation; var decision = prepared.Decision;
                if (decision.Decision != OperationDecisionKind.Allowed)
                    return SkillLoadMailboxResult.Failed(JsonSerializer.Serialize(new {
                        decision = decision.Decision == OperationDecisionKind.RequiresApproval ? "requiresApproval" : "denied", reasonCode = decision.ReasonCode,
                        activated = false, read = new { tool = ToolCatalog.SkillsLoad, arguments = new { ids = privateIds } }, replayAllowed = false }), "denied");
            }
        }
        string Result(SkillLoadPlan value)
        {
            if (privateRead is null) return value.ToToolResultJson();
            var body = JsonNode.Parse(value.ToToolResultJson())!.AsObject();
            body["tenantManagementRead"] = true;
            body["managementReadSource"] = JsonSerializer.SerializeToNode(TenantManagementReadService.Reference(privateRead.Value, current.Owner.TenantId!.Value));
            return body.ToJsonString();
        }
        if (requestedIds.Any(k => k.StartsWith("builtin:", StringComparison.Ordinal) && !visibleCatalog.Any(s => s.Key == k)))
            return SkillLoadMailboxResult.Failed(SkillLoadAdmission.Error("forbidden", "Procedure is unavailable in this execution."), "denied");
        var plan = SkillLoadAdmission.Plan(current.PinnedSkillCatalog, current.ActiveSkillKeys, current.SkillLoadCount, requestedIds);
        var updated = await _agentRuns.ApplyAsync(current.Owner, current.AgentRunId,
            new AgentRunCommand.LoadSkills(current.Revision, _time.GetUtcNow(), current.Claim!.Generation, plan.IdsToAppend),
            cancellationToken).ConfigureAwait(false);
        if (updated.AgentRunId == bound.AgentRunId)
        {
            _boundAgentRun = updated;
        }

        return new SkillLoadMailboxResult(Result(plan), updated.ActiveSkillKeys, plan.Outcome);
    }

    private SkillLoadMailboxResult? SkillLoadFence(SkillLoadRequested input)
    {
        if (_deactivated
            || _responseTerminal
            || _activeResponseId != input.ResponseId
            || _epoch != input.Epoch
            || input.Context.Epoch != _epoch)
        {
            return SkillLoadMailboxResult.Failed(
                SkillLoadAdmission.Error("stale", "Skill load is no longer owned by this execution."),
                "stale");
        }

        if (_boundAgentRun is not
            {
                Status: AgentRunStatus.Running,
                Claim: not null
            } bound
            || bound.ResponseId != input.ResponseId)
        {
            return SkillLoadMailboxResult.Failed(
                SkillLoadAdmission.Error("stale", "Skill load is no longer owned by this execution."),
                "stale");
        }

        if (bound.CancellationRequested)
        {
            return SkillLoadMailboxResult.Failed(
                SkillLoadAdmission.Error("cancelled", "Skill load was cancelled."),
                "stale");
        }

        return null;
    }
}
