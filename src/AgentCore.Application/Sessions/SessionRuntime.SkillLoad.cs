using System.Text.Json;
using AgentCore.Application.Agents;
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
                NewContext(cause.EventId),
                responseId,
                _epoch,
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
        if (fence is not null)
        {
            return fence;
        }

        var bound = _boundConversationExecution!;
        if (_turnExecutions is null)
        {
            return SkillLoadMailboxResult.Failed(
                SkillLoadAdmission.Error("forbidden", "Skill load requires a conversation execution."),
                "denied");
        }

        var current = await _turnExecutions.GetAsync(bound.ExecutionId, cancellationToken).ConfigureAwait(false);
        if (current is null
            || current.Revision != bound.Revision
            || current.Claim?.Generation != bound.Claim!.Generation
            || current.Status != ConversationTurnExecutionStatus.Running
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

        var updated = await _turnExecutions.AdmitActiveSkillsAsync(
                current.ExecutionId,
                current.Revision,
                current.Claim!.Generation,
                plan.IdsToAppend,
                _time.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);
        if (updated.ExecutionId == bound.ExecutionId)
        {
            _boundConversationExecution = updated;
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

        if (_boundConversationExecution is not
            {
                Status: ConversationTurnExecutionStatus.Running,
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
