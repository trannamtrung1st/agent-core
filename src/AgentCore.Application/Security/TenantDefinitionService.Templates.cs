using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed partial class TenantDefinitionService
{
    public async ValueTask<IReadOnlyList<AgentDefinition>> ListTemplatesAsync(Guid actor, Guid tenant, CancellationToken ct = default)
    {
        await authorizer.RequireAsync(actor, "agent-definitions.create", AccessScope.ForTenant(tenant), ct).ConfigureAwait(false);
        return (await templates.ListAsync(ct).ConfigureAwait(false)).OrderBy(d => d.Id, StringComparer.Ordinal).ThenByDescending(d => d.Version).ToArray();
    }

    public async ValueTask<AgentDefinitionDraft> ForkTemplateAsync(Guid actor, Guid tenant, string templateId, int version,
        string? name, long expectedRevision, CancellationToken ct = default)
    {
        var state = await ForChange(actor, tenant, "create", AccessScope.ForTenant(tenant), expectedRevision, ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(templateId) || version < 1) throw new ArgumentException("Choose an exact shipped template version.");
        var source = await templates.ReadAsync(templateId, version, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("definition_unavailable");
        if (source.Definition.Id != templateId || source.Definition.Version != version) throw new AccessDeniedException("definition_unavailable");
        var id = ids.NewId(); var now = time.GetUtcNow();
        var candidate = AgentDefinitionCandidate.FromDefinition(source.Definition) with { DefinitionId = id.ToString("D") };
        if (name is not null) candidate = candidate with { Identity = candidate.Identity with { Name = name } };
        candidate = Validate(candidate, false);
        var draft = new AgentDefinitionDraft(ids.NewId(), candidate.DefinitionId, 1, candidate, DefinitionDraftSourceKind.ForkBuiltIn, version, now, now, templateId);
        var payloads = source.Resources.Select(r => r with { Content = r.Content.ToArray() }).ToArray();
        var resources = payloads.Select(r => new AgentDefinitionDraftResource(ids.NewId(), draft.DraftId,
            r.LogicalPath, r.Kind, r.MediaType, DefinitionResourceContentHasher.ComputeSha256Hex(r.Content), r.Content.LongLength, now)).ToArray();
        if (resources.Length > 0) TenantDefinitionChildRules.ValidateResourceBatch(draft.DraftId, [], resources);
        foreach (var r in payloads) DefinitionResourcePolicies.RejectSecretsInTextualContent(r.MediaType, r.Content);
        // Fail before creating anything if the selected source has incomplete dependencies.
        TenantDefinitionPublicationRules.RequireReady(draft, new(resources, [], []));
        for (var i = 0; i < resources.Length; i++) await content.StoreVerifiedAsync(resources[i].ContentSha256, payloads[i].Content, ct).ConfigureAwait(false);
        if (!await definitions.TryCreateDefinitionAsync(Advance(state), expectedRevision, new(tenant, id, now), draft, resources,
            Audit(tenant, actor, "fork-template", id, now), ct).ConfigureAwait(false)) throw new SecurityConflictException("stale_revision");
        return draft;
    }
}
