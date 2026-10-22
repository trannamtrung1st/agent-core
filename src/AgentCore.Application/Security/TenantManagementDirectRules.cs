using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// The existing local-effect transaction serves the two explicitly delegated management choices.
public static class TenantManagementDirectRules
{
    public static bool AutoApplies(TenantManagementReader reader, PreparedManagementMutation value) => TenantHarnessCommandRules.AutoApplies(reader, value) || (value.Skill is not null
        ? TenantManagementSkillRules.AutoApplies(reader, value.ToolName) : TenantManagementResourceRules.AutoApplies(reader, value));
    public static string ParametersHash(string tool, string arguments) => TenantMemoryOperationRules.Hash(JsonSerializer.Serialize(new { tool, arguments }));
    public static void Transaction(TenantSecurityState next, long expected, TenantManagementReader reader, TenantManagementCommandCommit commit, AuthorizationAudit audit, DateTimeOffset at)
    {
        var run = reader.Run; var value = commit.Operation.ManagementMutation ?? throw new SecurityConflictException("operation_mismatch");
        TenantConfigurationContinuationRules.RequireDirectWrite(run);
        TenantLocalEffectRules.RequireDirectRun(run, commit.Generation, at);
        if (run.AgentRunId != commit.RunId || run.Revision != commit.ExpectedRunRevision || commit.Operation.InstanceId != run.AgentInstanceId
            || commit.Operation.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(commit.SourceCallId) || commit.SourceCallId.Length > 200
            || !(value.Skill is { Changed: true } || value.Resource is { Changed: true }) || !AutoApplies(reader, value)
            || commit.ParametersHash != ParametersHash(value.Harness?.ToolName ?? value.ToolName, value.Harness?.ArgumentsJson ?? value.ArgumentsJson)
            || next.Tenant.TenantId != run.Owner.TenantId || next.Tenant.Revision != checked(expected + 1)
            || audit.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != run.Owner.ExecutorPrincipalId
            || audit.Action != (value.Resource is null ? "operations.skills.apply" : "operations.resources.apply") || audit.Target != run.AgentInstanceId.ToString("D")
            || audit.Decision != "allowed" || audit.ReasonCode != TenantManagementMutationRules.Completed)
            throw new SecurityConflictException("operation_mismatch");
        var rebuilt = TenantApprovalRules.PrepareManagementMutation(commit.Operation.OperationId, run.AgentInstanceId, value, reader.Agent.Instance.Revision, reader.Agent.Instance.PersonaRevision);
        if (rebuilt.ActionHash != commit.Operation.ActionHash) throw new SecurityConflictException("operation_mismatch");
    }
    public static async ValueTask AuthorityAsync(TenantManagementReader reader, PreparedManagementMutation value, IamAuthorizer iam, CancellationToken ct)
    {
        var permission = value.Resource is null ? "skills.manage" : TenantManagementResourceRules.Permission(value.ToolName);
        var scope = value.Resource is null ? TenantManagementSkillRules.Scope(reader, value) : TenantManagementResourceRules.Scope(reader, value);
        if (!(await iam.CheckOwnedTenantAsync(reader.Security, reader.Agent.PrincipalId, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed
            || await TenantManagementReadRules.NeedsReviewOwnedAsync(reader, permission, scope, iam, ct).ConfigureAwait(false))
            throw new AccessDeniedException("operation_authority_changed");
    }
}
