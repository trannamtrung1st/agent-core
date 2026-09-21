using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;

namespace AgentCore.Infrastructure.Email;

public sealed class SyntheticEmailProvider : IEmailProvider
{
    private readonly ConcurrentDictionary<string, EmailDraftSnapshot> _drafts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, EmailMessageResult> _messages = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _sentByDraft = new(StringComparer.Ordinal);

    public SyntheticEmailProvider()
    {
        SeedMessages();
    }

    public bool IsAvailable => true;

    public ValueTask<EmailSearchResult> SearchAsync(EmailSearchRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalizedQuery = request.Query.Trim();
        var matches = _messages.Values
            .Where(message =>
                message.Subject.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                || message.From.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase)
                || message.Body.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(message => message.Date)
            .Take(Math.Clamp(request.Limit, 1, 25))
            .Select(message => new EmailSearchItem(
                message.MessageId,
                message.ThreadId,
                message.From,
                message.Subject,
                message.Date,
                message.Body.Length > 120 ? message.Body[..120] + "…" : message.Body))
            .ToArray();
        if (matches.Length == 0 && normalizedQuery.Length > 0)
        {
            matches =
            [
                new EmailSearchItem(
                    "syn-msg-harness",
                    "syn-thread-harness",
                    "harness@example.test",
                    "Synthetic harness inbox",
                    DateTimeOffset.UtcNow,
                    "Deterministic synthetic email harness message.")
            ];
        }

        return ValueTask.FromResult(new EmailSearchResult(matches, false));
    }

    public ValueTask<EmailMessageResult> ReadAsync(EmailReadRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_messages.TryGetValue(request.MessageId, out var message))
        {
            return ValueTask.FromResult(message);
        }

        if (string.Equals(request.MessageId, "syn-msg-harness", StringComparison.Ordinal))
        {
            var harness = BuildHarnessMessage();
            _messages[harness.MessageId] = harness;
            return ValueTask.FromResult(harness);
        }

        throw AgentCoreErrors.NotFound("Message was not found.");
    }

    public ValueTask<EmailDraftResult> CreateDraftAsync(EmailCreateDraftRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var draftId = "syn-draft-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('|', request.To) + request.Subject + request.Body + _drafts.Count)))[..16];
        var snapshot = new EmailDraftSnapshot(
            draftId,
            request.To,
            request.Cc,
            request.Bcc,
            request.Subject,
            request.Body);
        _drafts[draftId] = snapshot;
        return ValueTask.FromResult(new EmailDraftResult(snapshot));
    }

    public ValueTask<EmailDraftSnapshot?> GetDraftAsync(string draftId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_drafts.TryGetValue(draftId, out var draft) ? draft : null);
    }

    public ValueTask<EmailSendResult> SendDraftAsync(EmailSendDraftRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_drafts.ContainsKey(request.DraftId))
        {
            return ValueTask.FromResult(new EmailSendResult(EmailSendOutcome.Failed, null, "notFound", "Draft was not found."));
        }

        if (!string.Equals(request.DraftId, request.ApprovedDraft.DraftId, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(new EmailSendResult(EmailSendOutcome.Failed, null, "invalid", "Approved draft id does not match."));
        }

        if (_sentByDraft.TryGetValue(request.DraftId, out var existing))
        {
            return ValueTask.FromResult(new EmailSendResult(EmailSendOutcome.Sent, existing, null, null));
        }

        var messageId = "syn-sent-" + request.DraftId;
        _sentByDraft[request.DraftId] = messageId;
        return ValueTask.FromResult(new EmailSendResult(EmailSendOutcome.Sent, messageId, null, null));
    }

    private void SeedMessages()
    {
        var harness = BuildHarnessMessage();
        _messages[harness.MessageId] = harness;
    }

    private static EmailMessageResult BuildHarnessMessage() =>
        new(
            "syn-msg-harness",
            "syn-thread-harness",
            "harness@example.test",
            ["assistant@example.test"],
            [],
            new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero),
            "Synthetic harness inbox",
            "This is the deterministic synthetic email harness body.",
            false,
            []);
}
