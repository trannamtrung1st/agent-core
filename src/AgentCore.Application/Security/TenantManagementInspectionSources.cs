using System.Text;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantManagementInspectionSources
{
    public static bool IsInspection(string tool) => tool is "harness.inspect" or "configuration.inspect";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static TenantManagementInspectionCatalog Catalog(TenantManagementReader reader, TenantAgentConfigurationSnapshot snapshot,
        IReadOnlyList<AgentDefinitionPublicationResource> inherited, IReadOnlyList<TenantCredentialBinding> bindings, IReadOnlyList<TenantCredential> credentials)
    {
        var owner = reader.Agent.Instance; var tenant = reader.Security.Tenant.TenantId;
        var definition = reader.Current with { SelfManagement = ManagementPolicyResolver.Resolve(reader.Baseline, owner) };
        var items = new List<TenantManagementInspectionItem>();
        void Add(string kind, string key, AccessScope scope, object identity, object metadata) => items.Add(new(kind, key,
            TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(identity)), scope, JsonSerializer.SerializeToElement(metadata, Json)));
        foreach (var skill in AgentInstanceSkillService.Views(definition, snapshot.Skills).Concat(AgentInstanceSkillService.BuiltIns(definition, definition.SelfManagement!, owner)))
        {
            object identity = skill.Origin switch {
                SkillOrigin.Instance => new { skill.Key, skill.Name, skill.Description, skill.Projection, skill.EnabledChoice, skill.Revision },
                SkillOrigin.Definition => new { owner.DefinitionId, owner.ActiveVersion, skill.Key, skill.Name, skill.Description, skill.Projection,
                    state = snapshot.Skills.DefinitionStates.Single(s => "definition:" + s.DefinitionSkillId == skill.Key) },
                _ => new { skill.Key, skill.Name, skill.Description, skill.Projection, version = ManagementBuiltInSkills.Catalog.Single(s => s.Key == skill.Key).SourceRevision } };
            Add("skill", skill.Key, TenantAgentChildScopes.Scope(tenant, owner.InstanceId, "skill", skill.Key), identity,
                new { skill.Key, skill.Name, skill.Description, origin = skill.Origin.ToString(), projection = skill.Projection.ToString(), skill.Enabled, skill.EnabledChoice, skill.Revision, skill.MissingCapabilities });
        }
        foreach (var resource in AgentInstanceResourceService.Views(definition, inherited, snapshot.Resources, snapshot.Skills))
            Add("resource", resource.Key, TenantAgentChildScopes.Scope(tenant, owner.InstanceId, "resource", resource.Key),
                new { resource.Key, resource.Origin, resource.LogicalPath, resource.Kind, resource.MediaType, resource.ContentSha256, resource.ByteLength, resource.EnabledOverride, resource.Revision },
                new { resource.Key, resource.Origin, resource.LogicalPath, kind = resource.Kind.ToString(), resource.MediaType, resource.ContentSha256, resource.ByteLength, resource.Enabled, resource.Revision, resource.VirtualPath });
        foreach (var credential in credentials.Where(c => c.Value.Status == CredentialStatus.Active))
            Add("credential", credential.Value.CredentialId.ToString("D"), TenantCredentialRules.Scope(tenant, credential.Value.CredentialId),
                new { credential.Value.CredentialId, credential.Value.Revision, credential.Value.DisplayName, credential.Value.Kind, credential.Value.Status, credential.Value.Purpose },
                new { credential.Value.CredentialId, credential.Value.DisplayName, kind = credential.Value.Kind.ToString(), purpose = credential.Value.Purpose.ToString(), credential.Value.Revision });
        foreach (var binding in bindings.Where(b => b.Credential.Value.Status == CredentialStatus.Active))
            Add("binding", binding.Binding.BindingId.ToString("D"), TenantCredentialRules.Scope(tenant, binding.Credential.Value.CredentialId),
                new { binding.Binding, binding.Credential.Value.CredentialId, binding.Credential.Value.Revision, binding.Credential.Value.DisplayName, binding.Credential.Value.Kind, binding.Credential.Value.Status },
                new { binding.Binding.BindingId, binding.Binding.Reference, binding.Binding.CredentialId, binding.Binding.Revision, binding.Credential.Value.DisplayName, kind = binding.Credential.Value.Kind.ToString(), purpose = binding.Credential.Value.Purpose.ToString() });
        return new(items.OrderBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Key, StringComparer.Ordinal).ToArray());
    }
    public static string Permission(string kind) => kind switch { "skill" => "skills.read", "resource" => "resources.read", "credential" or "binding" => "credentials.read", _ => throw new AccessDeniedException("management_source_unavailable") };
    public static IReadOnlyList<TenantManagementChildRead> Read(string? json)
    {
        if (json is null) return [];
        if (Encoding.UTF8.GetByteCount(json) > 24000) throw new AccessDeniedException("management_source_budget");
        TenantManagementChildRead[] items;
        try { items = JsonSerializer.Deserialize<TenantManagementChildRead[]>(json) ?? throw new AccessDeniedException("management_source_unavailable"); }
        catch (JsonException) { throw new AccessDeniedException("management_source_unavailable"); }
        if (items.Any(i => i is null)) throw new AccessDeniedException("management_source_unavailable");
        if (JsonSerializer.Serialize(items) != json || items.Length > 64 || items.Select(i => (i.Kind, i.Key)).Distinct().Count() != items.Length) throw new AccessDeniedException("management_source_budget");
        foreach (var item in items)
            if (!TenantMemoryOperationRules.ValidHash(item.Fingerprint) || item.Key is not { Length: >= 1 and <= 100 } || item.ResourceId is not { Length: >= 1 and <= 160 }
                || item.Kind is not ("skill" or "resource" or "credential" or "binding")) throw new AccessDeniedException("management_source_unavailable");
        return items;
    }
    public static AccessScope Scope(TenantManagementReader reader, TenantManagementChildRead value) => AccessScope.ForResource(reader.Security.Tenant.TenantId,
        value.Kind is "credential" or "binding" ? "credential" : value.Kind, value.ResourceId);
    public static async ValueTask RequireAsync(TenantManagementReader reader, string? sources, IamAuthorizer iam, CancellationToken ct, Guid? human = null)
    {
        foreach (var value in Read(sources))
        {
            var scope = Scope(reader, value);
            if (!(await iam.CheckOwnedTenantAsync(reader.Security, human ?? reader.Agent.PrincipalId, PermissionCatalog.Get(Permission(value.Kind)), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException(human is null ? "management_source_changed" : "approval_unavailable");
            var current = reader.Inspection?.Items.SingleOrDefault(i => i.Kind == value.Kind && i.Key == value.Key);
            if (current is null || current.Fingerprint != value.Fingerprint || current.Scope != scope) throw new AccessDeniedException("management_source_changed");
        }
    }
    public static IEnumerable<(string Permission, AccessScope Scope)> Targets(TenantManagementReader reader, string? sources)
    {
        yield return ("agents.read", TenantManagementReadRules.Scope(reader));
        foreach (var value in Read(sources)) yield return (Permission(value.Kind), Scope(reader, value));
    }
    public static async ValueTask<bool> ReviewAsync(TenantManagementReader reader, string? sources, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var (permission, scope) in Targets(reader, sources))
            if (await TenantSandboxReadOperationRules.NeedsReviewAsync(reader.Security, reader.Run.Admission.ExecutionContext!.OriginalInitiatorPrincipalId, permission, scope, iam, ct).ConfigureAwait(false)) return true;
        return false;
    }
    public static bool Independent(TenantManagementReader reader, string? sources) => Targets(reader, sources).Any(t => TenantOperationPolicyService.Effective(reader.Security, PermissionCatalog.Get(t.Permission)).IndependentReview);
    public static bool Visible(PreparedManagementRead? value, TenantManagementApprovalVisibility? visibility)
    {
        foreach (var source in Read(value?.SourcesJson))
        {
            if (visibility is null) return false;
            if (source.Kind is "credential" or "binding")
            {
                if (visibility.Credentials is { } credentials && (!Guid.TryParse(source.ResourceId, out var id) || !credentials.Contains(id))) return false;
            }
            else
            {
                var allowed = source.Kind == "skill" ? visibility.Skills : visibility.Resources;
                if (allowed is null || allowed.Agents is { } agents && (!Guid.TryParse(source.ResourceId.AsSpan(0, Math.Min(36, source.ResourceId.Length)), out var parent) || !agents.Contains(parent))
                    && allowed.Children?.Contains(source.ResourceId) != true) return false;
            }
        }
        return true;
    }

}
