using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Infrastructure.Persistence;

public sealed partial class InMemorySandboxStore
{
    internal object TenantGate => _gate;
    private readonly Dictionary<string, TenantSandboxProfileRoot> _tenantProfiles = new(StringComparer.Ordinal);
    internal bool OwnsTenantProfile(Guid tenant, string id) => _tenantProfiles.GetValueOrDefault(id)?.TenantId == tenant;
    internal SandboxProfile? TenantProfile(Guid tenant, string id, long? revision = null) => OwnsTenantProfile(tenant, id)
        ? _profiles.Values.Where(p => p.Id == id && p.TenantId == tenant && (revision is null || p.Revision == revision)).MaxBy(p => p.Revision) : null;
    internal TenantSandboxProfilePage TenantProfiles(Guid tenant, IReadOnlyList<string>? allowedIds, int page, int size, string search)
    {
        var allowed = allowedIds?.ToHashSet(StringComparer.Ordinal);
        var all = _profiles.Values.Where(p => p.TenantId == tenant && OwnsTenantProfile(tenant, p.Id) && (allowed is null || allowed.Contains(p.Id)))
            .GroupBy(p => p.Id).Select(g => g.MaxBy(p => p.Revision)!)
            .Where(p => TenantSandboxProfileRules.SearchText(p).Contains(TenantSandboxProfileRules.SearchTerm(search), StringComparison.Ordinal))
            .OrderBy(p => p.Id, StringComparer.Ordinal).ToArray();
        return new(all.Skip(page * size).Take(size).ToArray(), all.Length, page, size);
    }
    internal SandboxProfileVerification? TenantVerification(Guid tenant, string id, long revision) => OwnsTenantProfile(tenant, id)
        && _verifications.GetValueOrDefault((id, revision)) is { } value && value.TenantId == tenant ? value : null;
    internal void SaveTenantVerification(SandboxProfileVerification value) => _verifications[(value.ProfileId, value.ProfileRevision)] = value;
    internal bool SaveTenantProfile(TenantSandboxProfileRoot root, SandboxProfile value, long expected)
    {
        TenantSandboxProfileRules.Validate(root, value, expected);
        var owner = _tenantProfiles.GetValueOrDefault(value.Id);
        var old = _profiles.Values.Where(p => p.Id == value.Id).MaxBy(p => p.Revision);
        if (owner is null ? expected != 0 || old is not null : owner.TenantId != root.TenantId || old?.TenantId != root.TenantId) return false;
        if ((old?.Revision ?? 0) != expected) return false;
        if (owner is null) _tenantProfiles.Add(value.Id, root);
        _profiles.Add((value.Id, value.Revision), value); return true;
    }
}
