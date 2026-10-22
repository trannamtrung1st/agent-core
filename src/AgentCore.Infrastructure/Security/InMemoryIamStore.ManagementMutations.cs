using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Application.Tools;
using System.Text.Json;
namespace AgentCore.Infrastructure.Security;
public sealed partial class InMemoryIamStore
{
    public ValueTask RequireTenantManagementSkillSourcesAsync(TenantRunReference reference, Guid? generation, string tool, string arguments, Guid? inspectionHuman = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested(); WithTenantEvents(() => {
            var source = ReadTenantManagementSourceAsync(reference, generation, inspectionHuman, ct).Result;
            RequireManagementSkillSources(source.Reader, TenantManagementMutationContract.Parse(tool, arguments), inspectionHuman, ct); return true;
        }); return ValueTask.CompletedTask;
    }
    private void RequireManagementSkillSources(TenantManagementReader reader, TenantManagementMutationArguments args, Guid? human, CancellationToken ct)
    {
        var iam = new IamAuthorizer(this);
        TenantManagementSkillRules.RawAsync(reader, args, iam, human ?? reader.Agent.PrincipalId, ct).AsTask().GetAwaiter().GetResult();
        if (human is not null) return;
        var key = args.Value.TryGetProperty("key", out var selection) ? selection.GetString() : null;
        var need = TenantManagementReadRules.NeedsReviewOwnedAsync(reader,
            "agents.read", TenantManagementReadRules.Scope(reader), iam, ct).AsTask().GetAwaiter().GetResult();
        if (key is not null) need |= TenantManagementReadRules.NeedsReviewOwnedAsync(reader,
            "skills.read", TenantAgentChildScopes.Scope(reader.Security.Tenant.TenantId, reader.Agent.Instance.InstanceId, "skill", key), iam, ct).AsTask().GetAwaiter().GetResult();
        if (!need) return;
        var observed = ObserveManagementRead(reader.Run, key is null ? "management.inspect" : "skills.inspect", key is null ? "{}" : JsonSerializer.Serialize(new { key }), null, null, ct);
        if (!ManagementReadConsented(reader, TenantManagementReadService.Reference(observed.Value, reader.Security.Tenant.TenantId), ct))
            throw new AccessDeniedException("skill_source_read_review_required");
    }
    private (TenantManagementReader Reader, TenantAgentConfigurationChange Change) RequireManagementMutation(TenantApprovalRequest request, AgentRun run, CancellationToken ct)
    {
        var value = request.Operation.ManagementMutation ?? throw new AccessDeniedException("management_unavailable");
        var reader = ManagementReader(run, null, ct); var iam = new IamAuthorizer(this);
        TenantManagementReadRules.RawAsync(reader, iam, ct).AsTask().GetAwaiter().GetResult();
        TenantManagementMutationRules.SecondaryAsync(reader, value, iam, reader.Agent.PrincipalId, ct).AsTask().GetAwaiter().GetResult();
        TenantManagementMutationRules.PrimaryAsync(request, reader, iam, ct).AsTask().GetAwaiter().GetResult();
        if (value.Skill is not null) RequireManagementSkillSources(reader, TenantManagementMutationContract.Parse(value.ToolName, value.ArgumentsJson), null, ct);
        return (reader, TenantManagementMutationRules.Change(reader, value, value.Skill is null ? null : ReadTenantAgentConfigurationAsync(request.TenantId, run.AgentInstanceId, ct).Result));
    }
}
