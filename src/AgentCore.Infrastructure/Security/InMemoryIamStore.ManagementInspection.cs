using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Definitions;

namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore
{
    private TenantManagementReader ManagementSkillReader(TenantManagementReader reader, CancellationToken ct)
    {
        var snapshot = ReadTenantAgentConfigurationAsync(reader.Security.Tenant.TenantId, reader.Run.AgentInstanceId, ct).Result
            ?? throw new AccessDeniedException("skill_unavailable");
        return reader with { Inspection = new(TenantManagementInspectionSources.SkillItems(reader, snapshot.Skills)) };
    }
    private TenantManagementReader ManagementInspectionReader(TenantManagementReader reader, CancellationToken ct)
    {
        var tenant = reader.Security.Tenant.TenantId; var owner = reader.Agent.Instance;
        var snapshot = ReadTenantAgentConfigurationAsync(tenant, owner.InstanceId, ct).Result ?? throw new AccessDeniedException("management_source_unavailable");
        var inherited = ManagementPublishedResources(reader, ct);
        var bindings = ReadTenantCredentialBindingsAsync(tenant, owner.InstanceId, ct).Result?.Bindings ?? [];
        var values = new List<TenantCredential>();
        for (var page = 0; ; page++)
        {
            var items = ListTenantCredentialsAsync(tenant, null, page, 100, ct).Result;
            values.AddRange(items.Items); if ((long)(page + 1) * 100 >= items.Total) break;
        }
        return reader with { Inspection = TenantManagementInspectionSources.Catalog(reader, snapshot, inherited, bindings, values) };
    }
}
