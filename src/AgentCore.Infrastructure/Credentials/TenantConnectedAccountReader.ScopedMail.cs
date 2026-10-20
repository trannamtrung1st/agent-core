using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Credentials;

namespace AgentCore.Infrastructure.Credentials;

public sealed partial class TenantConnectedAccountReader
{
    private sealed class ScopedMail(IEmailProvider inner, Func<CancellationToken, ValueTask<Credential>> current,
        Func<bool> sending) : IEmailProvider, IEmailSendPreflight
    {
        public bool IsAvailable => inner.IsAvailable;
        private async ValueTask<T> Read<T>(Func<ValueTask<T>> action, CancellationToken ct)
        {
            await current(ct).ConfigureAwait(false); var result = await action().ConfigureAwait(false);
            await current(ct).ConfigureAwait(false); return result;
        }
        public ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken ct = default) => Read(() => inner.SearchAsync(request, ct), ct);
        public ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken ct = default) => Read(() => inner.ReadAsync(request, ct), ct);
        public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken ct = default) => throw new NotSupportedException("Core owns drafts.");
        public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string id, CancellationToken ct = default) => throw new NotSupportedException("Core owns drafts.");
        public async ValueTask PrepareSendAsync(CancellationToken ct)
        {
            if (!sending()) throw new AccessDeniedException("account_send_unavailable");
            await current(ct).ConfigureAwait(false);
            if (inner is IEmailSendPreflight prepare) await prepare.PrepareSendAsync(ct).ConfigureAwait(false);
            await current(ct).ConfigureAwait(false);
        }
        public async ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken ct = default)
        {
            if (!sending()) throw new AccessDeniedException("account_send_unavailable");
            await current(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            return await inner.SendDraftAsync(request, ct).ConfigureAwait(false);
        }
    }
}
