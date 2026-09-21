using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using MimeKit;

namespace AgentCore.Infrastructure.Email;

internal static class GmailMime
{
    public static string BuildRawMessage(
        IReadOnlyList<string> to,
        IReadOnlyList<string> cc,
        IReadOnlyList<string> bcc,
        string subject,
        string body)
    {
        using var message = BuildMessage(to, cc, bcc, subject, body);
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return EncodeBase64Url(stream.ToArray());
    }

    public static MimeMessage BuildMessage(
        IReadOnlyList<string> to,
        IReadOnlyList<string> cc,
        IReadOnlyList<string> bcc,
        string subject,
        string body)
    {
        if (!EmailHeaderSafety.IsSafeSubject(subject))
        {
            throw AgentCoreErrors.Validation("Email subject contains control characters.");
        }

        if (!EmailHeaderSafety.IsSafeBody(body))
        {
            throw AgentCoreErrors.Validation("Email body contains control characters.");
        }

        var message = new MimeMessage();
        try
        {
            foreach (var address in to)
            {
                message.To.Add(ParseMailbox(address));
            }

            foreach (var address in cc)
            {
                message.Cc.Add(ParseMailbox(address));
            }

            foreach (var address in bcc)
            {
                message.Bcc.Add(ParseMailbox(address));
            }

            if (message.To.Count == 0)
            {
                throw AgentCoreErrors.Validation("Email requires at least one To recipient.");
            }

            message.Subject = subject;
            message.Body = new TextPart("plain") { Text = body };
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    public static EmailDraftSnapshot ParseDraft(string draftId, byte[] rfc822)
    {
        using var stream = new MemoryStream(rfc822);
        using var message = MimeMessage.Load(stream);
        return new EmailDraftSnapshot(
            draftId,
            Addresses(message.To),
            Addresses(message.Cc),
            Addresses(message.Bcc),
            message.Subject ?? string.Empty,
            (message.TextBody ?? message.HtmlBody ?? string.Empty).TrimEnd('\r', '\n'));
    }

    public static byte[] DecodeBase64Url(string data)
    {
        var padded = data.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }

        return Convert.FromBase64String(padded);
    }

    public static string EncodeBase64Url(byte[] data) =>
        Convert.ToBase64String(data)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

    private static MailboxAddress ParseMailbox(string value)
    {
        if (!EmailHeaderSafety.TryValidateAddress(value, out _))
        {
            throw AgentCoreErrors.Validation("Email address is invalid.");
        }

        try
        {
            return MailboxAddress.Parse(value);
        }
        catch (FormatException)
        {
            throw AgentCoreErrors.Validation("Email address is invalid.");
        }
    }

    private static IReadOnlyList<string> Addresses(InternetAddressList list)
    {
        var addresses = new List<string>();
        foreach (var mailbox in list.Mailboxes)
        {
            if (!string.IsNullOrWhiteSpace(mailbox.Address))
            {
                addresses.Add(mailbox.Address);
            }
        }

        return addresses;
    }
}
