using AgentCore.Application.Email;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private sealed record EmailSelectionDecisionReceived(EventContext Context, Guid RequestId, Guid? AccountId,
        string? Reference, long InstanceRevision, TaskCompletionSource<bool> Completed) : SessionInput(Context);
    public async Task<bool> DecideEmailAccountAsync(Guid requestId, Guid? accountId, string? reference, long instanceRevision, CancellationToken ct)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var context = NewContext();
        BeginWork();
        if (!Enqueue(new EmailSelectionDecisionReceived(context, requestId, accountId, reference, instanceRevision, completed), urgent: false)) return false;
        return await completed.Task.WaitAsync(ct);
    }
    private async Task HandleEmailSelectionDecisionAsync(EmailSelectionDecisionReceived input, CancellationToken ct)
    {
        try
        {
            var store = _tools.EmailStore; var credentials = _tools.EmailCredentials;
            if (store is null || credentials is null || _headlessTransportDetached || _deactivated || _activeResponseId is not null
                || _snapshot.ArchivedAt is not null || _snapshot.DurablyDeletedAt is not null || _snapshot.Status != SessionStatus.Attached)
                throw AgentCoreErrors.Conflict("Selection requires this attached idle Chat. Retry after the current turn finishes.");
            var request = await store.GetSelectionAsync(input.RequestId, ct);
            var source = (await _agentRuns.ListPageAsync(RunOwner, SessionId, null, 1, ct)).Items.FirstOrDefault();
            if (request is null || request.Status != "pending" || request.ExpiresAtUtc <= _time.GetUtcNow()
                || source is null || !EmailSelectionSafety.CanContinue(source, request)
                || _snapshot.Entries.Any(e => e.Role == ConversationRole.User && e.Sequence > _snapshot.Entries
                    .Where(e => source.Admission.Activation.SourceEntryIds.Contains(e.EntryId)).Select(e => e.Sequence).DefaultIfEmpty(-1).Max()))
                throw AgentCoreErrors.Conflict("This account request expired or newer input superseded it. Send a new user turn.");
            var (_, before) = await ResolveNewRunAsync(ct);
            if (input.AccountId is not null && before.Configuration.InstanceRevision != input.InstanceRevision)
                throw AgentCoreErrors.Conflict("Instance revision changed. Refresh the request before granting account access.");
            if (!AgentCore.Application.Agents.RolePermissions.AllowsTool(before.Configuration.Definition, request.Target)
                || !AgentCore.Application.Agents.RolePermissions.AllowsTool(before.Configuration.Definition, ToolCatalog.EmailRequestAccount))
                throw AgentCoreErrors.Forbidden("Email operation is no longer authorized.");
            var next = request with { Status = input.AccountId is null ? "cancelled" : "deciding" };
            if (!await store.UpdateSelectionAsync(next, "pending", ct)) throw AgentCoreErrors.Conflict("Account request already decided.");
            if (input.AccountId is Guid accountId)
            {
                try
                {
                    var account = await credentials.GetAsync(accountId, ct);
                    if (account.Purpose != "EmailAccount" || account.Status != "Active") throw AgentCoreErrors.Forbidden("Choose an active Email Account.");
                    var binding = (await credentials.BindingsAsync(_snapshot.AgentInstanceId, ct)).FirstOrDefault(b => b.CredentialId == accountId);
                    if (binding is null) await credentials.BindAsync(_snapshot.AgentInstanceId, accountId, input.Reference ?? "email", input.InstanceRevision, ct);
                    var (_, resolved) = await ResolveNewRunAsync(ct);
                    if (!AgentCore.Application.Agents.RolePermissions.AllowsTool(resolved.Configuration.Definition, request.Target)
                        || !AgentCore.Application.Agents.RolePermissions.AllowsTool(resolved.Configuration.Definition, ToolCatalog.EmailRequestAccount))
                        throw AgentCoreErrors.Forbidden("Email capability is no longer authorized. Binding grants no tools.");
                    await store.UpdateSelectionAsync(next with { Status = "allowed", InstanceRevision = resolved.Configuration.InstanceRevision }, "deciding", ct);
                    await TryStartConfigurationContinuationAsync(input.Context, ct);
                }
                catch
                {
                    await store.UpdateSelectionAsync(next with { Status = "failed" }, "deciding", CancellationToken.None);
                    throw;
                }
            }
            input.Completed.TrySetResult(true);
        }
        catch (Exception ex) { input.Completed.TrySetException(ex); }
    }
}
