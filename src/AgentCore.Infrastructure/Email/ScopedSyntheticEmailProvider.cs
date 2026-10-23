using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Email;

internal sealed class ScopedSyntheticEmailProvider(EmailAccountContext account, SyntheticEmailProvider inner,
    Func<EmailAccountContext, CancellationToken, ValueTask> recheck) : IEmailProvider
{
    public bool IsAvailable => true;
    private string Prefix => "syn-" + account.AccountId.ToString("N") + ":";
    public async ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken cancellationToken = default)
    {
        await recheck(account, cancellationToken);
        var result = await inner.SearchAsync(request, cancellationToken);
        return result with { Results = result.Results.Select(m => m with { MessageId = Prefix + m.MessageId }).ToArray() };
    }
    public async ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default)
    {
        await recheck(account, cancellationToken);
        if (!request.MessageId.StartsWith(Prefix, StringComparison.Ordinal)) throw AgentCoreErrors.NotFound("Message reference is not in this account.");
        var message = await inner.ReadAsync(new(request.MessageId[Prefix.Length..]), cancellationToken);
        return message with { MessageId = Prefix + message.MessageId };
    }
    public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException("Core owns drafts.");
    public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default) => throw new NotSupportedException("Core owns drafts.");
    public async ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken cancellationToken = default)
    {
        await recheck(account, cancellationToken);
        if (request.ApprovedDraft.AccountId != account.AccountId) throw AgentCoreErrors.Forbidden("Draft account mismatch.");
        return new(EmailSendOutcome.Sent, "syn-sent-" + request.DraftId, null, null);
    }
}
