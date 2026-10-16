using System.Globalization;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record DefinitionResourceReference(Guid DefinitionId, Guid? DraftId, int? Version, Guid ResourceId);

public static class TenantDefinitionResourceScopes
{
    public static AccessScope Draft(Guid tenant, Guid definition, Guid draft, Guid resource) =>
        Create(tenant, new(definition, draft, null, resource));
    public static AccessScope Publication(Guid tenant, Guid definition, int version, Guid resource) =>
        Create(tenant, new(definition, null, version, resource));
    private static AccessScope Create(Guid tenant, DefinitionResourceReference resource)
    {
        var context = resource.DraftId is { } draft ? "draft:" + draft.ToString("D") : "publication:" + resource.Version?.ToString(CultureInfo.InvariantCulture);
        var scope = AccessScope.ForResource(tenant, "resource", resource.DefinitionId.ToString("D") + ":" + context + ":" + resource.ResourceId.ToString("D"));
        if (!TryParse(scope, out _)) throw new AccessDeniedException("resource_unavailable");
        return scope;
    }
    public static bool TryParse(AccessScope scope, out DefinitionResourceReference reference)
    {
        reference = null!;
        if (scope.Kind != AccessScopeKind.Resource || scope.ResourceType != "resource" || scope.TenantId is not { } tenant || tenant == Guid.Empty) return false;
        var parts = scope.ResourceId?.Split(':');
        if (parts is not { Length: 4 } || !CanonicalGuid(parts[0], out var definition) || !CanonicalGuid(parts[3], out var resource)) return false;
        if (parts[1] == "draft" && CanonicalGuid(parts[2], out var draft)) reference = new(definition, draft, null, resource);
        else if (parts[1] == "publication" && int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var version)
            && version > 0 && parts[2] == version.ToString(CultureInfo.InvariantCulture)) reference = new(definition, null, version, resource);
        return reference is not null;
    }
    private static bool CanonicalGuid(string value, out Guid id) => Guid.TryParseExact(value, "D", out id) && id != Guid.Empty && value == id.ToString("D");
    public static bool BelongsTo(AccessScope scope, Guid definition) => TryParse(scope, out var reference) && reference.DefinitionId == definition;
    public static TenantSecurityState WithoutDraftGrants(TenantSecurityState state, Guid draft) => state with
    {
        Assignments = state.Assignments.Where(a => !TryParse(a.Scope, out var reference) || reference.DraftId != draft).ToArray()
    };
    public static void RequireNoDraftGrants(TenantSecurityState state, Guid draft)
    {
        if (state.Assignments.Any(a => TryParse(a.Scope, out var reference) && reference.DraftId == draft))
            throw new ArgumentException("Consumed/deleted drafts cannot retain resource grants.");
    }
    public static void RequireNoGrant(TenantSecurityState state, AccessScope scope)
    {
        if (state.Assignments.Any(a => a.Scope == scope)) throw new ArgumentException("Deleted resources cannot retain grants.");
    }
}
