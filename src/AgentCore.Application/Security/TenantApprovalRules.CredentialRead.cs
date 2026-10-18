using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Tools;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record CredentialReadReference(Guid CredentialId, Guid BindingId, string Reference, long BindingRevision,
    long CredentialRevision, string DisplayName, string Kind);

public static partial class TenantApprovalRules
{
    public static string CredentialReadArguments(string arguments)
    {
        if (Encoding.UTF8.GetByteCount(arguments) > 4096) throw new ArgumentException("Use bounded Credential discovery arguments.");
        try
        {
            using var doc = JsonDocument.Parse(arguments); var value = CredentialDiscovery.Parse(doc.RootElement);
            return JsonSerializer.Serialize(new { cursor = value.Cursor, limit = value.Limit });
        }
        catch (AgentCore.Application.Sessions.AgentCoreException e) { throw new ArgumentException("Use only bounded cursor and limit discovery arguments.", e); }
    }
    public static IReadOnlyList<CredentialReadReference> CredentialReadReferences(PreparedCredentialRead read)
    {
        if (read.ReferencesJson is not { Length: <= 65536 } || read.ArgumentsJson != CredentialReadArguments(read.ArgumentsJson))
            throw new ArgumentException("Use canonical metadata read scopes.");
        using var args = JsonDocument.Parse(read.ArgumentsJson); var request = CredentialDiscovery.Parse(args.RootElement);
        var refs = JsonSerializer.Deserialize<CredentialReadReference[]>(read.ReferencesJson);
        if (refs is null || refs.Length > 101 || refs.Length > request.Limit + 1
            || refs.Any(r => r is null || r.CredentialId == Guid.Empty || r.BindingId == Guid.Empty || r.BindingRevision < 1 || r.CredentialRevision < 1
                || r.Reference != CredentialRules.Reference(r.Reference) || r.DisplayName != CredentialRules.DisplayName(r.DisplayName)
                || r.Kind is not ("Password" or "Token") || request.Cursor is { } cursor && string.CompareOrdinal(r.Reference, cursor) <= 0)
            || refs.Select(r => r.Reference).Distinct(StringComparer.Ordinal).Count() != refs.Length
            || refs.Select(r => r.BindingId).Distinct().Count() != refs.Length
            || JsonSerializer.Serialize(refs.OrderBy(r => r.Reference, StringComparer.Ordinal)) != read.ReferencesJson
            || (refs.Length == 0 ? read.CredentialId is not null : refs[0].CredentialId != read.CredentialId))
            throw new ArgumentException("Invalid exact metadata page.");
        return refs;
    }
    public static PreparedTenantOperation PrepareCredentialRead(Guid id, Guid instance, PreparedCredentialRead value, long instanceRevision, long personaRevision)
    {
        if (id == Guid.Empty || instance == Guid.Empty || value.SessionId == Guid.Empty || instanceRevision < 1 || personaRevision < 1)
            throw new ArgumentException("Invalid exact metadata read.");
        _ = CredentialReadReferences(value);
        var json = JsonSerializer.Serialize(new { operation = "credentials.metadata.read", permission = value.CredentialId is null ? "sessions.read" : "credentials.read", resourceType = value.CredentialId is null ? "session" : "credential",
            resourceId = (value.CredentialId ?? value.SessionId).ToString("D"), expectedInstanceRevision = instanceRevision, expectedPersonaRevision = personaRevision, read = value });
        return new(id, instance, null, instanceRevision, personaRevision, json, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), CredentialRead: value);
    }
}
