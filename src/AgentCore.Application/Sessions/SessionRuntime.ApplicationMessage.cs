using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Execution;
using AgentCore.Application.Events;
using AgentCore.Application.Observability;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private async Task<ApplicationMessageMailboxResult> RequestApplicationMessageAsync(
        EventContext cause,
        Guid responseId,
        string toolCallId,
        string argumentsJson,
        CancellationToken cancellationToken)
    {
        var completed = new TaskCompletionSource<ApplicationMessageMailboxResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!TryMailbox(new ApplicationMessageRequested(
                WorkerContext(cause),
                responseId,
                cause.Epoch,
                toolCallId,
                argumentsJson,
                completed)))
        {
            EndWork();
            return ApplicationMessageMailboxResult.Failed(
                ApplicationMessageAdmission.Error("stale", "Application message is no longer owned by this execution."),
                "stale");
        }

        return await completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleApplicationMessageAsync(
        ApplicationMessageRequested input,
        CancellationToken cancellationToken)
    {
        ApplicationMessageMailboxResult result;
        try
        {
            result = await AdmitApplicationMessageAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            result = ApplicationMessageMailboxResult.Failed(
                ApplicationMessageAdmission.Error("stale", "Application message is no longer owned by this execution."),
                "stale");
        }

        RuntimeTelemetry.RecordApplicationMessage(result.Outcome);
        input.Completed.TrySetResult(result);
    }

    private async Task<ApplicationMessageMailboxResult> AdmitApplicationMessageAsync(
        ApplicationMessageRequested input,
        CancellationToken cancellationToken)
    {
        var fence = ApplicationMessageFence(input);
        if (fence is not null || !await OwnsWorkerAsync(input.Context, input.ResponseId, cancellationToken).ConfigureAwait(false))
        {
            return fence ?? ApplicationMessageMailboxResult.Failed(
                ApplicationMessageAdmission.Error("stale", "Application message is no longer owned by this execution."), "stale");
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
            var code = current?.CancellationRequested == true ? "cancelled" : "stale";
            var message = code == "cancelled"
                ? "Application message was cancelled."
                : "Application message is no longer owned by this execution.";
            return ApplicationMessageMailboxResult.Failed(ApplicationMessageAdmission.Error(code, message), "stale");
        }

        JsonElement args;
        try
        {
            args = JsonSerializer.Deserialize<JsonElement>(
                string.IsNullOrWhiteSpace(input.ArgumentsJson) ? "{}" : input.ArgumentsJson);
        }
        catch (JsonException)
        {
            return ApplicationMessageMailboxResult.Failed(
                ApplicationMessageAdmission.Error("invalid", "Tool arguments were malformed."),
                "denied");
        }

        if (!ApplicationMessageAdmission.TryParseText(args, out var text, out var errorJson))
        {
            return ApplicationMessageMailboxResult.Failed(errorJson, "denied");
        }

        if (!ApplicationMessageAdmission.TryCreateEffectKey(current.AgentRunId, input.ToolCallId, out var effectKey))
        {
            return ApplicationMessageMailboxResult.Failed(
                ApplicationMessageAdmission.Error("invalid", "Application message effect identity is too long."),
                "denied");
        }

        var existing = _snapshot.Entries
            .Where(entry => entry.Role == ConversationRole.ApplicationMessage && entry.ResponseId == input.ResponseId)
            .ToArray();
        var duplicate = existing.FirstOrDefault(entry =>
            string.Equals(entry.ApplicationMessageEffectKey, effectKey, StringComparison.Ordinal)
            || string.Equals(entry.Text, text, StringComparison.Ordinal));
        var budget = ApplicationMessageBudget.FromSnapshot(
            _snapshot.Entries,
            input.ResponseId,
            _applicationMessagePolicy);
        if (duplicate is not null)
        {
            return new ApplicationMessageMailboxResult(
                ApplicationMessageAdmission.Duplicate(duplicate.ApplicationMessageEffectKey!, budget),
                "duplicate");
        }

        if (!budget.CanAdmit(text.Length))
        {
            return ApplicationMessageMailboxResult.Failed(
                ApplicationMessageAdmission.OverBudget(budget),
                "over_budget");
        }

        var now = _time.GetUtcNow();
        var entry = new ConversationEntry(
            _ids.NewId(),
            NextSequence(),
            null,
            ConversationRole.ApplicationMessage,
            text,
            input.ResponseId,
            EntryStatus.Completed,
            _snapshot.Mode,
            0,
            text.Length,
            now,
            ApplicationMessageEffectKey: effectKey);
        _snapshot = Append(entry);
        RequestPersist(_snapshot);
        await PublishAsync(
                new SessionOutput(
                    input.Context,
                    input.ResponseId,
                    new HistoryEntryUpsertOutput(PublicHistory.FromEntry(entry))),
                cancellationToken)
            .ConfigureAwait(false);
        await PublishStateAsync(input.Context, cancellationToken).ConfigureAwait(false);
        var budgetAfter = ApplicationMessageBudget.FromSnapshot(
            _snapshot.Entries,
            input.ResponseId,
            _applicationMessagePolicy);
        return new ApplicationMessageMailboxResult(
            ApplicationMessageAdmission.Success(effectKey, budgetAfter),
            "admitted");
    }

    private ApplicationMessageMailboxResult? ApplicationMessageFence(ApplicationMessageRequested input)
    {
        if (_deactivated
            || _responseTerminal
            || _activeResponseId != input.ResponseId
            || _epoch != input.Epoch
            || input.Context.Epoch != _epoch)
        {
            return ApplicationMessageMailboxResult.Failed(
                ApplicationMessageAdmission.Error("stale", "Application message is no longer owned by this execution."),
                "stale");
        }

        if (_boundAgentRun is not
            {
                Status: AgentRunStatus.Running,
                Claim: not null
            } bound
            || bound.ResponseId != input.ResponseId)
        {
            return ApplicationMessageMailboxResult.Failed(
                ApplicationMessageAdmission.Error("stale", "Application message is no longer owned by this execution."),
                "stale");
        }

        if (bound.CancellationRequested)
        {
            return ApplicationMessageMailboxResult.Failed(
                ApplicationMessageAdmission.Error("cancelled", "Application message was cancelled."),
                "stale");
        }

        return null;
    }
}
