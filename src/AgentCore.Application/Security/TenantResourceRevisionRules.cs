using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public static class TenantResourceRevisionRules
{
    public static void Require(string key, long? revision, AgentRun run, TenantAgent agent, InstanceResourceSnapshot resources, TenantResourcePinRetention? retained = null)
    {
        if (retained is not null)
        {
            if (retained.InstanceRevision != agent.Instance.Revision || retained.ResourceRevision != revision
                || run.Admission.Configuration!.Resources.SingleOrDefault(r => r.Key == key) is null) throw new SecurityConflictException("resource_source_changed");
            return;
        }
        if (key.StartsWith("instance:", StringComparison.Ordinal))
        {
            var current = resources.InstanceResources.SingleOrDefault(r => "instance:" + r.ResourceId.ToString("D") == key);
            var pinned = run.Admission.Configuration!.Resources.SingleOrDefault(r => r.Key == key);
            if (current is null || !current.Enabled || current.Revision != revision || pinned is null
                || current.ContentSha256 != pinned.ContentSha256 || current.ByteLength != pinned.ByteLength || current.MediaType != pinned.MediaType
                || current.LogicalPath != pinned.LogicalPath || current.Kind != pinned.Kind) throw new SecurityConflictException("resource_source_changed");
        }
        else
        {
            var current = resources.DefinitionStates.SingleOrDefault(r => "definition:" + r.ResourceId.ToString("D") == key);
            if ((current?.Revision ?? 0) != revision || current?.EnabledOverride == false
                || agent.Instance.DefinitionId != run.DefinitionId || agent.Instance.ActiveVersion != run.DefinitionVersion) throw new SecurityConflictException("resource_source_changed");
        }
    }
}
