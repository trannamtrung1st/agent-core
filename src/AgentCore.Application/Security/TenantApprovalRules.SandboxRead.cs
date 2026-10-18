using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record SandboxReadProfileReference(string Id, long Revision);
public sealed record SandboxReadProfileLabel(string Id, long Revision, string Name);

public static partial class TenantApprovalRules
{
    public static PreparedTenantOperation PrepareSandboxRead(Guid operationId, Guid instance, PreparedSandboxRead value, long instanceRevision, long personaRevision)
    {
        if (operationId == Guid.Empty || instance == Guid.Empty || instanceRevision < 1 || personaRevision < 1 || !Enum.IsDefined(value.Kind)
            || value.SessionId == Guid.Empty || value.Generation == Guid.Empty || value.ExecutionId == Guid.Empty
            || value.Kind == SandboxReadKind.Process && value.ExecutionId is null || value.Kind == SandboxReadKind.Profiles && value.ExecutionId is not null
            || value.TargetHash is not { Length: 64 } || value.TargetHash.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new ArgumentException("Invalid exact Sandbox observation read.");
        _ = ReadProfiles(value);
        var json = JsonSerializer.Serialize(new { operation = "sandbox.read", permission = "sandbox.read", resourceType = "agent",
            resourceId = instance.ToString("D"), expectedInstanceRevision = instanceRevision, expectedPersonaRevision = personaRevision, read = value });
        return new(operationId, instance, null, instanceRevision, personaRevision, json,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json))), SandboxRead: value);
    }
    public static IReadOnlyList<SandboxReadProfileReference> ReadProfiles(PreparedSandboxRead value)
    {
        if (value.ProfileReferencesJson is not { Length: <= 4096 }) throw new ArgumentException("Invalid bounded read scopes.");
        try
        {
            var references = JsonSerializer.Deserialize<SandboxReadProfileReference[]>(value.ProfileReferencesJson);
            if (references is null || references.Length > 18 || references.Any(p => p is null || !TenantSandboxProfileRules.ValidId(p.Id) || p.Revision < 1)
                || references.Select(p => (p.Id, p.Revision)).Distinct().Count() != references.Length
                || JsonSerializer.Serialize(references.OrderBy(p => p.Id, StringComparer.Ordinal).ThenBy(p => p.Revision)) != value.ProfileReferencesJson)
                throw new ArgumentException("Use exact canonical profile read scopes.");
            return references;
        }
        catch (JsonException e) { throw new ArgumentException("Invalid profile read scopes.", e); }
    }
    public static IReadOnlyList<SandboxReadProfileLabel> ReadSnapshotLabels(PreparedTenantOperation operation, TenantApprovalSandboxReadSnapshot snapshot)
    {
        var value = operation.SandboxRead ?? throw new ArgumentException("Exact read required."); var references = ReadProfiles(value);
        if (snapshot.ScopeSummary != TenantSandboxReadOperationRules.Summary(value.Kind) || snapshot.IncludesBoundedOutput != (value.Kind == SandboxReadKind.Process)
            || snapshot.ProfileLabelsJson is not { Length: <= 32768 }) throw new ArgumentException("Invalid stored read review evidence.");
        try
        {
            var labels = JsonSerializer.Deserialize<SandboxReadProfileLabel[]>(snapshot.ProfileLabelsJson);
            if (labels is null || labels.Length != references.Count || labels.Any(p => p is null || string.IsNullOrWhiteSpace(p.Name) || p.Name.Length > 120)
                || labels.Where((p, i) => p.Id != references[i].Id || p.Revision != references[i].Revision).Any()
                || JsonSerializer.Serialize(labels) != snapshot.ProfileLabelsJson) throw new ArgumentException("Read review scope evidence differs from the exact action.");
            return labels;
        }
        catch (JsonException e) { throw new ArgumentException("Invalid read review labels.", e); }
    }

}
