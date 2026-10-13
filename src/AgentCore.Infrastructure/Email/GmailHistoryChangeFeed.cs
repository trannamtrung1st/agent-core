using System.Net;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Email;

namespace AgentCore.Infrastructure.Email;

// Cursor includes a bounded page position so no message is skipped at the 25-ref boundary.
internal sealed class GmailHistoryChangeFeed(Guid accountId, GmailEmailProvider provider)
{
    public async ValueTask<EmailWatchCursor> BaselineAsync(CancellationToken ct)
    {
        using var profile = await provider.Get("profile", ct);
        var id = profile.RootElement.GetProperty("historyId").GetString();
        if (id is not { Length: > 0 and <= 32 } || !id.All(char.IsAsciiDigit)) throw new InvalidDataException("Invalid mailbox position.");
        return new(EmailWatchCursorKind.GmailHistory, HistoryId: id);
    }
    public async ValueTask<EmailWatchBatch> PollAsync(EmailWatchCursor cursor, CancellationToken ct)
    {
        if (cursor.Kind != EmailWatchCursorKind.GmailHistory) return new([], await BaselineAsync(ct), CursorGap: true);
        JsonDocument page;
        try { page = await provider.Get("history?maxResults=100&historyTypes=messageAdded&startHistoryId=" + Uri.EscapeDataString(cursor.HistoryId!)
            + (cursor.PageToken is null ? "" : "&pageToken=" + Uri.EscapeDataString(cursor.PageToken)), ct); }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { return new([], await BaselineAsync(ct), CursorGap: true); }
        using (page)
        {
            var root = page.RootElement;
            var entries = root.TryGetProperty("history", out var history)
                ? history.EnumerateArray().SelectMany(h => h.TryGetProperty("messagesAdded", out var added) ? added.EnumerateArray().Select(a => a.GetProperty("message")).ToArray() : []).ToArray() : [];
            var refs = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal); var index = cursor.Offset;
            // Bound metadata requests independently from number of inbox messages.
            var examined = 0;
            for (; index < entries.Length && refs.Count < 25 && examined < 25; index++, examined++)
            {
                var message = entries[index]; var id = message.GetProperty("id").GetString()!;
                if (id.Length is < 1 or > 128 || !id.All(char.IsAsciiLetterOrDigit)) throw new InvalidDataException("Invalid mailbox reference.");
                if (!seen.Add(id) || !message.TryGetProperty("labelIds", out var labels) || !labels.EnumerateArray().Any(l => l.GetString() == "INBOX")) continue;
                try
                {
                    using var metadata = await provider.Get("messages/" + Uri.EscapeDataString(id) + "?format=minimal&fields=id,labelIds", ct);
                    if (metadata.RootElement.TryGetProperty("labelIds", out var currentLabels) && currentLabels.EnumerateArray().Any(l => l.GetString() == "INBOX")) refs.Add(EmailMessageReferences.Gmail(accountId, id));
                }
                catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound) { /* Gone before observation. */ }
            }
            if (index < entries.Length) return new(refs, cursor with { Offset = index }, HasMore: true);
            if (root.TryGetProperty("nextPageToken", out var next))
            {
                var token = next.GetString(); if (token is not { Length: > 0 and <= 2048 }) throw new InvalidDataException("Invalid mailbox continuation.");
                return new(refs, cursor with { PageToken = token, Offset = 0 }, HasMore: true);
            }
            var final = root.GetProperty("historyId").GetString();
            if (final is not { Length: > 0 and <= 32 } || !final.All(char.IsAsciiDigit)) throw new InvalidDataException("Invalid mailbox position.");
            return new(refs, new(EmailWatchCursorKind.GmailHistory, HistoryId: final));
        }
    }
}
