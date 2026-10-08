using System.Text.Json;
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
        catch (Exception)
        {
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

        var plan = SkillLoadAdmission.Plan(
            current.PinnedSkillCatalog,
            current.ActiveSkillKeys,
            current.SkillLoadCount,
            requestedIds);
        if (!plan.IncrementInvocation)
        {
            return new SkillLoadMailboxResult(plan.ToToolResultJson(), current.ActiveSkillKeys, plan.Outcome);
        }

        var updated = await _agentRuns.ApplyAsync(current.Owner, current.AgentRunId,
            new AgentRunCommand.LoadSkills(current.Revision, _time.GetUtcNow(), current.Claim!.Generation, plan.IdsToAppend),
            cancellationToken).ConfigureAwait(false);
        if (updated.AgentRunId == bound.AgentRunId)
        {
            _boundAgentRun = updated;
        }

        return new SkillLoadMailboxResult(plan.ToToolResultJson(), updated.ActiveSkillKeys, plan.Outcome);
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
