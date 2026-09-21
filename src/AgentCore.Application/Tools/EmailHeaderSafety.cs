using System.Net.Mail;

namespace AgentCore.Application.Tools;

public static class EmailHeaderSafety
{
    public static bool ContainsHeaderUnsafeChars(ReadOnlySpan<char> value) =>
        value.IndexOfAny(['\r', '\n', '\0']) >= 0;

    public static bool TryValidateAddress(string value, out string address)
    {
        address = string.Empty;
        var trimmed = value.Trim();
        if (trimmed.Length == 0
            || trimmed.Length > EmailToolLimits.MaxRecipientLength
            || ContainsHeaderUnsafeChars(trimmed))
        {
            return false;
        }

        if (!MailAddress.TryCreate(trimmed, out var parsed)
            || string.IsNullOrWhiteSpace(parsed.Address)
            || ContainsHeaderUnsafeChars(parsed.Address))
        {
            return false;
        }

        address = trimmed;
        return true;
    }

    public static bool IsSafeSubject(string subject) =>
        !ContainsHeaderUnsafeChars(subject);

    public static bool IsSafeBody(string body) =>
        body.IndexOf('\0') < 0;
}
