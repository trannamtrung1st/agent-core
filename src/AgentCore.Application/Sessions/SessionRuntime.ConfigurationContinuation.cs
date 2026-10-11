using AgentCore.Application.Email;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private static string? ContinuationTarget(AgentRun? run) => run?.Admission.Activation.Kind == ActivationKind.ConfigurationContinuation
        ? ConfigurationPendingTask.ReadTarget(run.Admission.Activation.EvidenceJson) : null;

    private async Task<bool> TryStartConfigurationContinuationAsync(EventContext cause, CancellationToken ct)
    {
        // User input wins. A latest-Run check also prevents revival after later work,
        // terminal cancellation, or a previously admitted continuation, including reopen.
        if (_snapshot.ArchivedAt is not null || _snapshot.DurablyDeletedAt is not null || _deactivated
            || _headlessTransportDetached || _snapshot.Status is not (SessionStatus.Attached or SessionStatus.Created)) return false;
        var latest = (await _agentRuns.ListPageAsync(RunOwner, SessionId, null, 1, ct).ConfigureAwait(false)).Items.FirstOrDefault();
        var task = latest is null ? null : ConfigurationPendingTask.ReadSafe(latest);
        var email = latest is null || _tools.EmailStore is null ? null : await _tools.EmailStore.GetSelectionAsync(latest.AgentRunId, ct);
        var emailContinuation = latest is not null && email is { Status: "allowed", InstanceRevision: { } } && email.ExpiresAtUtc > _time.GetUtcNow()
            && EmailSelectionSafety.CanContinue(latest, email);
        if (emailContinuation)
        {
            var original = string.Join("\n", _snapshot.Entries.Where(e => latest!.Admission.Activation.SourceEntryIds.Contains(e.EntryId)).Select(e => e.Text));
            task = new(latest!.AgentRunId, email!.Target, original.Length <= 200 ? original : original[..200], email.InstanceRevision!.Value);
        }
        if (task is null || latest is null) return false;
        var sourceEntries = latest.Admission.Activation.SourceEntryIds;
        var entries = _snapshot.Entries.Where(e => sourceEntries.Contains(e.EntryId)).ToArray();
        if (entries.Length != sourceEntries.Count || entries.Length == 0) return false;
        var lastSource = entries.Max(e => e.Sequence);
        if (_snapshot.Entries.Any(e => e.Role == ConversationRole.User && e.Sequence > lastSource)) return false;
        var (projection, resolved) = await ResolveNewRunAsync(ct).ConfigureAwait(false);
        if (resolved.Configuration.InstanceRevision != task.InstanceRevision
            || !emailContinuation && (resolved.Configuration.Definition.SelfManagement is not { } policy || !ManagementTools.Enabled(policy))
            || emailContinuation && !AgentCore.Application.Agents.RolePermissions.AllowsTool(resolved.Configuration.Definition, ToolCatalog.EmailRequestAccount)
            || !AgentCore.Application.Agents.RolePermissions.AllowsTool(resolved.Configuration.Definition, task.Target)
            || !_tools.ConfigurationGate.IsConfigured(task.Target)) return false;
        var run = AgentRunAdmissionFactory.ForConfigurationContinuation(_ids.NewId(), _ids.NewId(), _ids.NewId(), projection,
            latest, task, _time.GetUtcNow(), resolved.Skills,
            ExecutionBudgetResolver.Resolve(resolved.Configuration.Definition, resolved.InstanceBudgets, true), resolved.Configuration, emailContinuation);
        _agentRunAdmissionPending = true;
        RequestPersist(_snapshot, admittedRun: run, onAdmitted: admitted => run = admitted, then: async token =>
        {
            _agentRunAdmissionPending = false;
            var current = await _agentRuns.GetAsync(RunOwner, run.AgentRunId, token).ConfigureAwait(false);
            if (current is not { Status: AgentRunStatus.Queued }) return;
            var now = _time.GetUtcNow();
            try { _boundAgentRun = await _agentRuns.ApplyAsync(RunOwner, run.AgentRunId,
                new AgentRunCommand.Claim(current.Revision, now, _ids.NewId(), now + AgentRunCoordinator.ClaimDuration), token).ConfigureAwait(false); }
            catch (AgentCoreException e) when (e.Code == "Conflict") { return; }
            await LaunchBoundAgentRunAsync(cause, _boundAgentRun, token).ConfigureAwait(false);
        });
        return true;
    }
}
