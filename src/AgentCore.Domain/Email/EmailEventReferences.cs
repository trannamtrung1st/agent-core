using System.Text.Json;
using AgentCore.Domain.Credentials;

namespace AgentCore.Domain.Email;

// Closed factual lookup metadata only. A reference grants no mailbox read.
public static class EmailEventReferences
{
    public static IReadOnlyList<Guid> AccountIdsFromEvidence(string evidence, bool isBucket)
    {
        using var document = JsonDocument.Parse(evidence); var context = document.RootElement.GetProperty("triggerContext");
        var data = isBucket ? context.GetProperty("sources").EnumerateArray().Select(e => e.GetProperty("data")).ToArray()
            : [context.GetProperty("source")];
        return Array.AsReadOnly(data.Select(e => AccountId(e.GetRawText())).Distinct().OrderBy(id => id.ToString("D"), StringComparer.Ordinal).ToArray());
    }
    public static Guid AccountId(string json)
    {
        using var document = JsonDocument.Parse(json); var value = document.RootElement;
        var keys = value.ValueKind == JsonValueKind.Object ? value.EnumerateObject().Select(p => p.Name).ToArray() : [];
        if (keys.Length != 4 || keys.Distinct(StringComparer.Ordinal).Count() != 4
            || keys.Any(k => k is not ("accountId" or "accountRef" or "messageRef" or "provider"))
            || value.GetProperty("accountId").ValueKind != JsonValueKind.String
            || !Guid.TryParseExact(value.GetProperty("accountId").GetString(), "D", out var id) || id == Guid.Empty
            || value.GetProperty("accountId").GetString() != id.ToString("D")
            || value.GetProperty("accountRef").ValueKind != JsonValueKind.String
            || value.GetProperty("messageRef").ValueKind != JsonValueKind.String
            || value.GetProperty("messageRef").GetString() is not { Length: > 0 and <= 512 } message || message.Any(char.IsControl)
            || value.GetProperty("provider").ValueKind != JsonValueKind.String
            || value.GetProperty("provider").GetString() is not ("Gmail" or "Custom"))
            throw new ArgumentException("A closed factual email reference is required.");
        var reference = value.GetProperty("accountRef").GetString()!;
        if (CredentialRules.Reference(reference) != reference) throw new ArgumentException("A canonical bound account reference is required.");
        return id;
    }
}
