using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    private void RequireSpaceResourceReadSources(AgentRun run, CancellationToken ct)
    {
        try
        {
            var sources = TenantSpaceResourceReadRules.Read(run); if (sources.Count == 0) return;
            var tenant = run.Owner.TenantId!.Value; var state = _tenants[tenant]; var iam = new IamAuthorizer(this);
            RequireSpaceExecutionSource(run, state, iam, ct).GetAwaiter().GetResult();
            var agent = FindTenantAgentAsync(tenant, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("resource_source_changed");
            foreach (var source in sources)
            {
                var request = source.RequestId is { } id ? _approvals.GetValueOrDefault(id) : null;
                TenantSpaceResourceReadRules.RequireAsync(run, source, state, agent, TenantAgentDefinitionBaseline(agent), iam,
                    (key, revision) => { RequireResourcePin(key, revision, run, agent, ct); return ValueTask.CompletedTask; }, request,
                    source.OperationId is { } operation ? _operationReceipts.GetValueOrDefault(operation) : null,
                    source.RequestId is { } decision ? _approvalDecisions.GetValueOrDefault(decision) : null,
                    source.RequestId is { } grant && _operationGrants.ContainsKey(grant), ct).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception e) when (e is AccessDeniedException or SecurityConflictException or ArgumentException or System.Text.Json.JsonException or InvalidOperationException or KeyNotFoundException)
        { throw new AccessDeniedException("resource_source_changed"); }
    }
}
