using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Application.Agents;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
namespace AgentCore.Infrastructure.Security;

public sealed partial class InMemoryIamStore : ITenantManagementReadStore
{
    public ValueTask<TenantManagementReadObservation> ObserveTenantManagementReadAsync(TenantRunReference reference, Guid? generation, string tool, string arguments,
        PreparedManagementRead? expected = null, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(WithTenantEvents(() => {
            var run = sessionBacking.ReadRetainedTenantOperationRun(reference.TenantId, reference.RunId) ?? throw new AccessDeniedException("management_read_unavailable");
            if (generation is { } claim) _ = RequireMemoryFrame(new(run.Owner, run.SessionId, run.AgentRunId, claim));
            return ObserveManagementRead(run, tool, arguments, expected, inspectionHuman, ct);
        }));
    }
    private TenantManagementReader ManagementReader(AgentRun run, Guid? human, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("management_read_unavailable");
        var state = _tenants.GetValueOrDefault(tenant) ?? throw new AccessDeniedException("management_read_unavailable");
        var root = TenantCoreRoot(run);
        var agent = FindTenantAgentAsync(tenant, run.AgentInstanceId, ct).Result ?? throw new AccessDeniedException("management_read_unavailable");
        if (run.Admission.Configuration is null || agent.PrincipalId != run.Owner.ExecutorPrincipalId) throw new AccessDeniedException("management_read_unavailable");
        if (human is null) TenantExecutionLineage.RequireRequesterAsync(run, state, new IamAuthorizer(this), this, ct).AsTask().GetAwaiter().GetResult();
        var baseline = TenantAgentDefinitionBaseline(agent);
        return new(run, root, state, agent, baseline, InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides));
    }
    private TenantManagementReadObservation ObserveManagementRead(AgentRun run, string tool, string arguments, PreparedManagementRead? expected, Guid? human, CancellationToken ct)
    {
        var reader = ManagementReader(run, human, ct); var args = TenantManagementReadContract.Parse(tool, arguments); var iam = new IamAuthorizer(this);
        var baseline = reader.Baseline; if (human is null) TenantManagementReadRules.Eligible(reader, baseline, tool);
        if (human is not null) { if (expected is null || !reader.Security.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approval_unavailable"); ValidateAgentActor(reader.Security, human.Value); }
        if (human is null) TenantManagementReadRules.RawAsync(reader, iam, ct).AsTask().GetAwaiter().GetResult();
        if (human is not null) TenantManagementReadRules.RawAsync(reader, iam, ct, human).AsTask().GetAwaiter().GetResult();
        if (expected is not null)
        {
            if (expected.ToolName != tool || expected.ArgumentsJson != args.Json) throw new AccessDeniedException("management_source_changed");
            try { TenantManagementReadRules.ValidateCurrent(reader, baseline, expected, _operationReceipts.Values, id => _approvals.GetValueOrDefault(id)); }
            catch (AccessDeniedException error) when (human is not null && error.ReasonCode is "management_source_changed" or "management_read_unavailable") { return new(reader, expected, true); }
            return new(reader, expected);
        }
        return new(reader, TenantManagementReadRules.Build(reader, baseline, args));
    }
}
