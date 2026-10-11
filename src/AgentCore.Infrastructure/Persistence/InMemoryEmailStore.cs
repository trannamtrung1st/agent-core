using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Persistence;

public sealed class InMemoryEmailStore : IEmailStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, EmailAccountSelection> _selections = [];
    public ValueTask CreateSelectionAsync(EmailAccountSelection request, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) { _selections.TryAdd(request.RequestId, request); return ValueTask.CompletedTask; } }
    public ValueTask<EmailAccountSelection?> GetSelectionAsync(Guid id, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_selections.GetValueOrDefault(id)); }
    public ValueTask<bool> UpdateSelectionAsync(EmailAccountSelection request, string expectedStatus, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (!_selections.TryGetValue(request.RequestId, out var current) || current.Status != expectedStatus) return ValueTask.FromResult(false);
            _selections[request.RequestId] = request; return ValueTask.FromResult(true);
        }
    }
    private readonly Dictionary<string, StoredEmailDraft> _drafts = [];
    private readonly Dictionary<string, EmailSendOperation> _sends = [];
    public ValueTask CreateDraftAsync(StoredEmailDraft draft, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (!_drafts.TryAdd(draft.Draft.DraftId, draft)) throw AgentCoreErrors.Conflict("Draft already exists.");
            return ValueTask.CompletedTask;
        }
    }
    public ValueTask<StoredEmailDraft?> GetDraftAsync(string id, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_drafts.GetValueOrDefault(id)); }
    public ValueTask<bool> TryClaimSendAsync(EmailSendOperation operation, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (!_drafts.TryGetValue(operation.DraftId, out var draft) || draft.Draft.AccountId != operation.AccountId
                || draft.Draft.Revision != operation.DraftRevision) throw AgentCoreErrors.Conflict("Send identity does not match the durable draft.");
            return ValueTask.FromResult(_sends.TryAdd(operation.DraftId, operation));
        }
    }
    public ValueTask<EmailSendOperation?> GetSendAsync(string id, CancellationToken ct = default)
    { ct.ThrowIfCancellationRequested(); lock (_gate) return ValueTask.FromResult(_sends.GetValueOrDefault(id)); }
    public ValueTask CompleteSendAsync(string id, EmailSendResult result, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); lock (_gate)
        {
            if (!_sends.TryGetValue(id, out var send)) throw AgentCoreErrors.NotFound("Send claim was not found.");
            if (send.Result is not null && send.Result != result) throw AgentCoreErrors.Conflict("Send receipt is immutable.");
            _sends[id] = send with { Result = result }; return ValueTask.CompletedTask;
        }
    }
}
