using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;

namespace AgentCore.Application.Tools;

public static class EmailDraftNormalizer
{
    private static readonly JsonSerializerOptions CanonicalOptions = new() { WriteIndented = false };

    public static EmailDraftSnapshot Normalize(EmailDraftSnapshot draft) =>
        new(
            draft.DraftId.Trim(),
            NormalizeAddresses(draft.To),
            NormalizeAddresses(draft.Cc),
            NormalizeAddresses(draft.Bcc),
            NormalizeText(draft.Subject),
            NormalizeBody(draft.Body));

    public static string ComputeSendActionHash(EmailDraftSnapshot draft)
    {
        var normalized = Normalize(draft);
        var payload = string.Join(
            '\n',
            [
                ToolCatalog.EmailSend,
                normalized.DraftId,
                SerializeAddresses(normalized.To),
                SerializeAddresses(normalized.Cc),
                SerializeAddresses(normalized.Bcc),
                normalized.Subject,
                normalized.Body
            ]);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(payload));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static IReadOnlyList<string> NormalizeAddresses(IReadOnlyList<string> addresses)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var address in addresses)
        {
            var trimmed = address.Trim();
            if (trimmed.Length == 0 || !seen.Add(trimmed))
            {
                continue;
            }

            list.Add(trimmed.ToLowerInvariant());
        }

        list.Sort(StringComparer.Ordinal);
        return list;
    }

    private static string NormalizeText(string value) =>
        value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private static string NormalizeBody(string value) => NormalizeText(value);

    private static string SerializeAddresses(IReadOnlyList<string> addresses) =>
        JsonSerializer.Serialize(addresses, CanonicalOptions);
}
