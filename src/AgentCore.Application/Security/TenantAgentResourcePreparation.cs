using AgentCore.Application.Admin;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;

namespace AgentCore.Application.Security;

// Semantic preparation for existing tenant Resource commands; authority and effects stay with their owners.
internal sealed record TenantPreparedResourceChange(TenantAgentConfigurationChange? Change,
    InstanceResourceSnapshot Next, InstanceResourceView? Resource);

internal static class TenantAgentResourcePreparation
{
    private static AgentDefinition Definition(AgentInstance owner, AgentDefinition baseline, SkillAuthor actor, HarnessManagementScope scope)
    {
        if (owner.Lifecycle != AgentInstanceLifecycle.Active)
            throw AgentCoreErrors.Validation("Archived instances are read-only.");
        var definition = InstanceSettingsResolver.Resolve(baseline, owner.SettingsOverrides) with {
            SelfManagement = ManagementPolicyResolver.Resolve(baseline, owner)
        };
        if (actor == SkillAuthor.Agent && !definition.SelfManagement!.Allows(scope))
            throw AgentCoreErrors.Forbidden("Resource administration is outside the effective self-management grant.");
        return definition;
    }

    internal static TenantPreparedResourceChange Upsert(AgentInstance owner, AgentDefinition baseline,
        InstanceResourceSnapshot snapshot, InstanceSkillSnapshot skills, IReadOnlyList<AgentDefinitionPublicationResource> published,
        TenantAgentResourceInput input, byte[] bytes, SkillAuthor actor, Guid allocatedId, DateTimeOffset at,
        AgentDefinitionPublicationResource? source = null, HarnessManagementScope scope = HarnessManagementScope.Resources)
    {
        var definition = Definition(owner, baseline, actor, scope);
        if (source is not null)
        {
            // Copy provenance denotes the actual immutable published bytes, never a model-supplied label.
            if (input.ResourceId is not null || input.ExpectedResourceRevision is not null
                || source.DefinitionId != baseline.Id || source.Version != baseline.Version || !published.Contains(source)
                || input.Kind != source.Kind || input.MediaType != source.MediaType)
                throw AgentCoreErrors.Validation("Copy requires the current Definition resource and its immutable content.");
            InstanceResourceRules.Verify(source.ContentSha256, source.ByteLength, bytes);
        }
        var previous = snapshot.InstanceResources.SingleOrDefault(r => r.ResourceId == input.ResourceId);
        var resource = InstanceResourceRules.PrepareUpsert(owner, snapshot, input.ResourceId, input.ExpectedResourceRevision,
            input.LogicalPath, input.Kind, input.MediaType, bytes, input.Enabled ?? previous?.Enabled ?? true, actor, allocatedId, at, source);
        if (previous is not null && (resource with { Revision = previous.Revision, UpdatedAt = previous.UpdatedAt }) == previous)
            return new(null, snapshot, View(definition, published, snapshot, skills, "instance:" + previous.ResourceId.ToString("D")));
        return Finish(owner, definition, snapshot, skills, published,
            new(owner.InstanceId, owner.Revision, Resource: resource, ExpectedResourceRevision: input.ExpectedResourceRevision));
    }

    internal static TenantPreparedResourceChange Choice(AgentInstance owner, AgentDefinition baseline,
        InstanceResourceSnapshot snapshot, InstanceSkillSnapshot skills, IReadOnlyList<AgentDefinitionPublicationResource> published,
        string key, bool delete, bool? enabled, long expectedRevision, SkillAuthor actor, DateTimeOffset at, HarnessManagementScope scope = HarnessManagementScope.Resources)
    {
        var definition = Definition(owner, baseline, actor, scope);
        var view = View(definition, published, snapshot, skills, key);
        if (view.Revision != expectedRevision) throw new SecurityConflictException("stale_revision");
        var parsed = InstanceResourceRules.ParseKey(key); InstanceResourceMutation mutation;
        if (delete)
        {
            if (parsed.Origin != "instance") throw AgentCoreErrors.Validation("Inherited resource bytes are read-only.");
            mutation = new(owner.InstanceId, owner.Revision, DeleteResourceId: parsed.Id, ExpectedResourceRevision: expectedRevision);
        }
        else if (parsed.Origin == "instance")
        {
            if (enabled is null) throw AgentCoreErrors.Validation("Instance resources do not inherit enabled state.");
            var previous = snapshot.InstanceResources.Single(r => r.ResourceId == parsed.Id);
            if (previous.Enabled == enabled.Value) return new(null, snapshot, view);
            mutation = new(owner.InstanceId, owner.Revision, Resource: previous with {
                Enabled = enabled.Value, Revision = checked(previous.Revision + 1), UpdatedAt = at
            }, ExpectedResourceRevision: previous.Revision);
        }
        else
        {
            var previous = snapshot.DefinitionStates.SingleOrDefault(r => r.ResourceId == parsed.Id);
            if (previous?.EnabledOverride == enabled) return new(null, snapshot, view);
            mutation = new(owner.InstanceId, owner.Revision, DefinitionState: new(owner.InstanceId, parsed.Id, enabled,
                checked((previous?.Revision ?? 0) + 1), at), ExpectedStateRevision: previous?.Revision);
        }
        return Finish(owner, definition, snapshot, skills, published, mutation);
    }

    private static InstanceResourceSnapshot After(InstanceResourceSnapshot current, InstanceResourceMutation mutation) => new(
        current.DefinitionStates.Where(r => r.ResourceId != mutation.DefinitionState?.ResourceId).Concat(mutation.DefinitionState is null ? [] : new[] { mutation.DefinitionState }).ToArray(),
        current.InstanceResources.Where(r => r.ResourceId != mutation.DeleteResourceId && r.ResourceId != mutation.Resource?.ResourceId).Concat(mutation.Resource is null ? [] : new[] { mutation.Resource }).ToArray());

    private static InstanceResourceView View(AgentDefinition definition, IReadOnlyList<AgentDefinitionPublicationResource> published,
        InstanceResourceSnapshot snapshot, InstanceSkillSnapshot skills, string key) =>
        InstanceResourceRules.Views(definition, published, snapshot, skills).SingleOrDefault(r => r.Key == key)
            ?? throw new AccessDeniedException("resource_unavailable");

    private static TenantPreparedResourceChange Finish(AgentInstance owner, AgentDefinition definition, InstanceResourceSnapshot snapshot,
        InstanceSkillSnapshot skills, IReadOnlyList<AgentDefinitionPublicationResource> published, InstanceResourceMutation mutation)
    {
        var next = After(snapshot, mutation);
        InstanceResourceRules.ValidateCollection(next.InstanceResources);
        _ = InstanceResourceRules.Resolve(definition, published, next, skills);
        var deleted = mutation.DeleteResourceId is { } id ? "instance:" + id.ToString("D") : null;
        var key = mutation.Resource is { } local ? "instance:" + local.ResourceId.ToString("D")
            : mutation.DefinitionState is { } inherited ? "definition:" + inherited.ResourceId.ToString("D") : deleted!;
        return new(new(owner.InstanceId, owner.Revision, Resource: mutation), next,
            deleted is null ? View(definition, published, next, skills, key) : null);
    }
}
