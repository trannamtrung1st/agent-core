using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Review binds intent/ownership, not stdout, polling revisions or a late provider reply.
public static class TenantSandboxControlOperationRules
{
    public static string Tool(SandboxControlKind kind) => kind == SandboxControlKind.StopProcess ? ToolCatalog.SandboxProcessStop : ToolCatalog.SandboxReset;
    public static string TargetHash(TenantSessionRoot owner, SandboxLease lease, SandboxExecution? execution) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            owner.TenantId, owner.SessionId, owner.AgentInstanceId, owner.ExecutorPrincipalId, lease.Generation,
            ProfileId = lease.Profile.Id, ProfileRevision = lease.Profile.Revision, lease.Profile.InstanceRevision,
            lease.SelectedProfileId, lease.TransitionProfileId, lease.TransitionProfileRevision,
            SelectionRequest = lease.SelectionWitness?.RequestId, lease.CleanupReason,
            Execution = lease.ActiveExecutionId ?? lease.LastExecutionId, StopRequest = execution?.ProcessStop?.RequestId }))));
    public static PreparedSandboxControl Parameters(SandboxControlKind kind, TenantSession session, SandboxLease lease, SandboxExecution? execution) =>
        new(kind, session.Owner.SessionId, lease.Profile.Id, lease.Profile.Revision, lease.Generation, execution?.Id,
            session.Snapshot.Revision, lease.Revision, execution?.Revision, TargetHash(session.Owner, lease, execution));
    public static TenantApprovalSnapshot Snapshot(TenantAgent agent, SandboxLease lease, SandboxExecution? execution) =>
        new(agent.Instance.Persona.Name, SandboxControl: new(lease.Profile.Name, lease.Profile.ImageReference, lease.State.ToString(), execution?.State.ToString()));
    public static async ValueTask RequirePermissionsAsync(TenantSecurityState state, TenantSessionRoot owner, string profile, Guid principal,
        IamAuthorizer authorizer, CancellationToken ct)
    {
        foreach (var (key, scope) in new[] {
            ("sessions.read", TenantSessionRules.Scope(owner.TenantId, owner.SessionId)),
            ("sessions.manage", TenantSessionRules.Scope(owner.TenantId, owner.SessionId)),
            ("sandbox.read", AccessScope.ForResource(owner.TenantId, "agent", owner.AgentInstanceId.ToString("D"))),
            ("sandbox.manage", AccessScope.ForResource(owner.TenantId, "agent", owner.AgentInstanceId.ToString("D"))),
            ("sandbox.read", TenantSandboxProfileRules.Scope(owner.TenantId, profile)) })
            if (!(await authorizer.CheckTenantAsync(state, principal, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("permission_denied");
    }
    public static void Require(PreparedSandboxControl value, AgentRun run, TenantAgent agent, AgentDefinition baseline,
        TenantSession session, SandboxLease lease, SandboxExecution? execution)
    {
        var current = InstanceSettingsResolver.ResolveForAgent(baseline, agent);
        if (run.Admission.Configuration is null || !TenantTaskExecutionContract.Allows(run, current) || !RolePermissions.AllowsTool(run.Admission.Configuration.Definition, Tool(value.Kind))
            || !RolePermissions.AllowsTool(current, Tool(value.Kind))) throw new AccessDeniedException("sandbox_policy_changed");
        if (session.Owner.TenantId != run.Owner.TenantId || session.Owner.SessionId != run.SessionId || session.Owner.AgentInstanceId != run.AgentInstanceId
            || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId || session.Snapshot.ArchivedAt is not null
            || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ended or SessionStatus.Ending)
            throw new AccessDeniedException("resource_unavailable");
        TenantSandboxControlRules.Require(new(value.SessionId, session.Snapshot.Revision, value.Generation, lease.Revision, value.ExecutionId), session, lease);
        if (value.ProfileId != lease.Profile.Id || value.ProfileRevision != lease.Profile.Revision
            || session.Snapshot.Revision < value.ObservedSessionRevision || lease.Revision < value.ObservedLeaseRevision
            || execution?.Id != value.ExecutionId || execution?.Revision < value.ObservedExecutionRevision
            || value.TargetHash != TargetHash(session.Owner, lease, execution)) throw new SecurityConflictException("sandbox_operation_witness_changed");
        if (execution is not null && (execution.SessionId != session.Owner.SessionId || execution.TenantId != lease.TenantId
            || execution.Generation != lease.Generation || execution.AgentInstanceId != lease.AgentInstanceId || execution.ExecutorPrincipalId != lease.ExecutorPrincipalId))
            throw new AccessDeniedException("resource_unavailable");
        if (value.Kind == SandboxControlKind.StopProcess)
            TenantSandboxControlRules.Require(new TenantSandboxProcessStop(value.SessionId, session.Snapshot.Revision, value.Generation, lease.Revision,
                value.ExecutionId!.Value, execution!.Revision), session, lease, execution);
    }
    public static string Reason(PreparedSandboxControl control) => control.Kind == SandboxControlKind.StopProcess ? "sandbox_stop_requested" : "sandbox_reset_requested";
    public static string AuditAction => "operations.sandbox.control";
}
