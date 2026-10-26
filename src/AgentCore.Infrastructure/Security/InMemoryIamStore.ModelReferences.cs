using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;

namespace AgentCore.Infrastructure.Security;

internal sealed record ModelReferenceCandidate(ModelReferenceItem Item, Guid? AgentId = null, Guid? SessionId = null);

public sealed partial class InMemoryIamStore
{
    public ValueTask<ModelReferencePage> ReadModelReferencesAsync(ModelReferenceQuery query, CancellationToken ct = default)
    {
        ModelReferenceQueries.Validate(query); ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (!_tenants.TryGetValue(query.TenantId, out var owner) || owner.Tenant.Status != IdentityStatus.Active)
                throw new AccessDeniedException("tenant_unavailable");
            if (query.OfferingId is { } offering && modelOfferings.GetValueOrDefault(offering)?.TenantId != query.TenantId
                || query.ConnectionId is { } connection && modelConnections.GetValueOrDefault(connection)?.TenantId != query.TenantId)
                throw new AccessDeniedException("resource_unavailable");
            var selected = modelOfferings.Values.Where(o => o.TenantId == query.TenantId
                && (query.OfferingId is null || o.OfferingId == query.OfferingId) && (query.ConnectionId is null || o.ConnectionId == query.ConnectionId)
                && (query.Visible.Offerings is null || query.Visible.Offerings.Contains(o.OfferingId))).Select(o => o.OfferingId).ToHashSet();
            var rows = query.Kind switch
            {
                ModelReferenceKind.Default => modelSettings.GetValueOrDefault(query.TenantId)?.DefaultOfferingId is { } id
                    ? new[] { new ModelReferenceCandidate(new(query.TenantId, "default", id, "Organization default", "Organization default", modelSettings[query.TenantId].Revision, "Configured")) } : [],
                ModelReferenceKind.Definitions => DefinitionReferences(query.TenantId),
                ModelReferenceKind.Instances => InstanceReferences(owner, ct),
                ModelReferenceKind.Automations => automationBacking.ModelReferences(query.TenantId),
                _ => sessionBacking.ModelReferences(query.TenantId, query.Kind)
            };
            var visible = rows.Where(r => selected.Contains(r.Item.OfferingId)
                && (query.Visible.Resources is null || query.Visible.Resources.Contains(r.Item.ResourceId))
                && (query.Visible.Agents is null || r.AgentId is null || query.Visible.Agents.Contains(r.AgentId.Value))
                && (query.Visible.Sessions is null || r.SessionId is null || query.Visible.Sessions.Contains(r.SessionId.Value)))
                .OrderBy(r => r.Item.ResourceId.ToString("D"), StringComparer.Ordinal).ThenBy(r => r.Item.ReferenceId, StringComparer.Ordinal)
                .ThenBy(r => r.Item.OfferingId.ToString("D"), StringComparer.Ordinal).ToArray();
            return ValueTask.FromResult(new ModelReferencePage(visible.Skip(checked(query.Page * query.PageSize)).Take(query.PageSize).Select(r => r.Item).ToArray(),
                visible.Length, query.Page, query.PageSize, owner.Tenant.Revision));
        }
    }
    private ModelReferenceCandidate[] DefinitionReferences(Guid tenant)
    {
        lock (definitions.TenantGate)
        {
            var rows = new List<ModelReferenceCandidate>();
            foreach (var root in definitions.TenantDefinitions.Values.Where(d => d.TenantId == tenant && d.DeletedAt is null))
            {
                foreach (var draft in definitions.TenantDrafts(tenant, root.DefinitionId))
                    if (Guid.TryParse((draft.Candidate.ModelDefaults?.Mode == "auto" ? null : draft.Candidate.ModelDefaults?.OfferingId ?? modelSettings.GetValueOrDefault(tenant)?.DefaultOfferingId?.ToString("D")), out var id))
                        rows.Add(new(new(root.DefinitionId, draft.DraftId.ToString("D"), id, draft.Candidate.Identity.Name, draft.Candidate.ModelDefaults?.OfferingId is null ? "Draft inherits organization default" : "Definition draft", draft.Revision, "Draft")));
                foreach (var publication in definitions.TenantPublications(tenant, root.DefinitionId))
                    if (Guid.TryParse((publication.Payload.ModelDefaults?.Mode == "auto" ? null : publication.Payload.ModelDefaults?.OfferingId ?? modelSettings.GetValueOrDefault(tenant)?.DefaultOfferingId?.ToString("D")), out var id))
                        rows.Add(new(new(root.DefinitionId, "v" + publication.Version, id, publication.Payload.Identity.Name, "Published Definition v" + publication.Version + (publication.Payload.ModelDefaults?.OfferingId is null ? " inherits organization default" : ""),
                            publication.MetadataRevision, publication.Status == AgentCore.Domain.Definitions.DefinitionPublicationStatus.Active ? "Published" : "Deprecated")));
            }
            return rows.ToArray();
        }
    }
    private ModelReferenceCandidate[] InstanceReferences(TenantSecurityState owner, CancellationToken ct)
    {
        lock (instances.CredentialGate) lock (definitions.TenantGate)
        {
            var rows = new List<ModelReferenceCandidate>();
            foreach (var identity in owner.SystemIdentities.Where(i => i.Kind == SystemIdentityKind.Agent && i.Status != IdentityStatus.Removed && i.AgentInstanceId is not null))
            {
                var instance = instances.FindTenantBackingAsync(identity.AgentInstanceId!.Value, ct).Result
                    ?? throw new InvalidOperationException("An owned Agent lost its Instance.");
                var baseline = Guid.TryParse(instance.DefinitionId, out var definition)
                    ? definitions.TenantPublication(owner.Tenant.TenantId, definition, instance.ActiveVersion)?.Payload
                    : builtIns?.GetAsync(instance.DefinitionId, instance.ActiveVersion, ct).GetAwaiter().GetResult();
                var explicitModel = instance.SettingsOverrides?.ModelPreference;
                var preference = explicitModel is null ? baseline?.ModelDefaults : explicitModel.Value is null ? null
                    : AgentCore.Application.Models.ExecutionModelPolicy.MergePreference(baseline?.ModelDefaults, explicitModel.Value);
                var chosen = preference?.Mode == "auto" ? null : preference?.OfferingId
                    ?? modelSettings.GetValueOrDefault(owner.Tenant.TenantId)?.DefaultOfferingId?.ToString("D");
                if (Guid.TryParse(chosen, out var id)) rows.Add(new(new(instance.InstanceId, "conversation", id, instance.Persona.Name,
                    explicitModel?.Value?.OfferingId is not null ? "Instance conversation override" : explicitModel is null && baseline?.ModelDefaults?.OfferingId is not null
                        ? "Inherited Definition default" : "Inherited organization default", instance.Revision, instance.Lifecycle.ToString())));
                if (Guid.TryParse(instance.UnattendedModel?.OfferingId, out var unattended)) rows.Add(new(new(instance.InstanceId, "unattended", unattended,
                    instance.Persona.Name, "Unattended preference", instance.Revision, instance.Lifecycle.ToString())));
            }
            return rows.ToArray();
        }
    }
}
