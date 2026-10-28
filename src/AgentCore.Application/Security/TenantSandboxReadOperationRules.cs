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

public static class TenantSandboxReadOperationRules
{
    public const string Reason = "sandbox_read_authorized";
    public const string AuditAction = "operations.sandbox.read";
    public static string Tool(SandboxReadKind kind) => kind switch { SandboxReadKind.Profiles => ToolCatalog.SandboxProfiles,
        SandboxReadKind.Status => ToolCatalog.SandboxStatus, SandboxReadKind.Process => ToolCatalog.SandboxProcessStatus, _ => throw new ArgumentException("Invalid Sandbox read.") };
    public static string Summary(SandboxReadKind kind) => kind switch { SandboxReadKind.Profiles => "Inspect readable Sandbox profiles",
        SandboxReadKind.Status => "Inspect stored Sandbox status", SandboxReadKind.Process => "Inspect exact stored process output", _ => throw new ArgumentException("Invalid Sandbox read.") };
    public static string TargetHash(SandboxReadKind kind, TenantSessionRoot owner, TenantSandboxRuntimeSnapshot snapshot) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            kind, owner.TenantId, owner.SessionId, owner.AgentInstanceId, owner.ExecutorPrincipalId, snapshot.Lease?.Generation,
            ProfileId = snapshot.Lease?.Profile.Id, ProfileRevision = snapshot.Lease?.Profile.Revision,
            ExecutionId = kind == SandboxReadKind.Profiles ? null : snapshot.Execution?.Id,
            CurrentExecutionId = kind == SandboxReadKind.Profiles ? null : snapshot.Lease?.ActiveExecutionId ?? snapshot.Lease?.LastExecutionId,
            ExecutionGeneration = kind == SandboxReadKind.Profiles ? null : snapshot.Execution?.Generation,
            snapshot.Lease?.SelectedProfileId, snapshot.Lease?.TransitionProfileId, snapshot.Lease?.TransitionProfileRevision,
            SelectionRequest = snapshot.Lease?.SelectionWitness?.RequestId }))));
    public static IReadOnlyList<(string Permission, AccessScope Scope)> Targets(TenantSessionRoot owner, IEnumerable<string> profiles) =>
        new[] { ("sessions.read", TenantSessionRules.Scope(owner.TenantId, owner.SessionId)),
            ("sandbox.read", AccessScope.ForResource(owner.TenantId, "agent", owner.AgentInstanceId.ToString("D"))) }
            .Concat(profiles.Distinct(StringComparer.Ordinal).Select(id => ("sandbox.read", TenantSandboxProfileRules.Scope(owner.TenantId, id)))).ToArray();
    public static async ValueTask<bool> NeedsReviewAsync(TenantSecurityState state, Guid? requester, string permission, AccessScope scope,
        IamAuthorizer authorizer, CancellationToken ct)
    {
        var policy = TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission));
        return policy.Mode == OperationApprovalMode.Always || policy.Mode == OperationApprovalMode.WhenRequesterLacksPermission && requester is { } human
            && !(await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed;
    }
    public static async ValueTask RequirePermissionsAsync(TenantSecurityState state, TenantSessionRoot owner, PreparedSandboxRead value, Guid principal,
        IamAuthorizer authorizer, CancellationToken ct)
    {
        foreach (var (permission, scope) in Targets(owner, TenantApprovalRules.ReadProfiles(value).Select(p => p.Id)))
            if (!(await authorizer.CheckTenantAsync(state, principal, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("permission_denied");
    }
    public static async ValueTask<bool> IndependentAsync(TenantSecurityState state, TenantSessionRoot owner, PreparedSandboxRead value, Guid? requester,
        IamAuthorizer authorizer, CancellationToken ct)
    {
        foreach (var (permission, scope) in Targets(owner, TenantApprovalRules.ReadProfiles(value).Select(p => p.Id)))
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).IndependentReview
                && (scope.ResourceType == "agent" || await NeedsReviewAsync(state, requester, permission, scope, authorizer, ct).ConfigureAwait(false))) return true;
        return false;
    }
    public static async ValueTask RequireReviewerAsync(TenantSecurityState state, TenantSessionRoot owner, PreparedSandboxRead value, Guid? requester, Guid human,
        IamAuthorizer authorizer, CancellationToken ct)
    {
        if (!state.Users.Any(u => u.PrincipalId == human && u.Status == IdentityStatus.Active)) throw new AccessDeniedException("approver_unqualified");
        await RequirePermissionsAsync(state, owner, value, human, authorizer, ct).ConfigureAwait(false);
        foreach (var (permission, scope) in Targets(owner, TenantApprovalRules.ReadProfiles(value).Select(p => p.Id)))
        {
            if (scope.ResourceType != "agent" && !await NeedsReviewAsync(state, requester, permission, scope, authorizer, ct).ConfigureAwait(false)) continue;
            if (TenantOperationPolicyService.Effective(state, PermissionCatalog.Get(permission)).IndependentReview && requester == human)
                throw new AccessDeniedException("independent_reviewer_required");
            if (!(await authorizer.CheckTenantAsync(state, human, PermissionCatalog.Get("approvals.approve"), scope, ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("approver_unqualified");
        }
    }
    public static void Require(PreparedSandboxRead value, AgentRun run, TenantAgent agent, AgentDefinition baseline, TenantSession session,
        TenantSandboxRuntimeSnapshot snapshot, IReadOnlyList<SandboxProfile> sources)
    {
        var current = InstanceSettingsResolver.ResolveForAgent(baseline, agent);
        if (run.Admission.Configuration is null || !TenantTaskExecutionContract.Allows(run, current) || !TenantSandboxInspectionContract.Allows(run.Admission.Configuration.Definition, Tool(value.Kind))
            || !TenantSandboxInspectionContract.Allows(current, Tool(value.Kind))) throw new AccessDeniedException("sandbox_policy_changed");
        if (session.Owner.SessionId != value.SessionId || value.SessionId != run.SessionId || session.Owner.TenantId != run.Owner.TenantId
            || session.Owner.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId || session.Owner.AgentInstanceId != run.AgentInstanceId
            || snapshot.Owner != session.Owner || snapshot.SessionRevision != session.Snapshot.Revision || session.Snapshot.DurablyDeletedAt is not null
            || session.Snapshot.ArchivedAt is not null || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active
            || session.Snapshot.Status is SessionStatus.Ended or SessionStatus.Ending) throw new AccessDeniedException("resource_unavailable");
        if (value.Generation != snapshot.Lease?.Generation || value.ExecutionId != (value.Kind == SandboxReadKind.Profiles ? null : snapshot.Execution?.Id)
            || value.TargetHash != TargetHash(value.Kind, session.Owner, snapshot)) throw new SecurityConflictException("sandbox_read_target_changed");
        var refs = TenantApprovalRules.ReadProfiles(value);
        foreach (var profile in refs)
        {
            var source = sources.SingleOrDefault(p => p.Id == profile.Id && p.Revision == profile.Revision);
            if (source is null || source.TenantId != session.Owner.TenantId)
                throw new AccessDeniedException("resource_unavailable");
            if (value.Kind == SandboxReadKind.Profiles && (run.Admission.Configuration.Definition.ExecutionSandboxProfiles?.Any(p => p.Id == profile.Id && p.Revision == profile.Revision) != true
                || run.Admission.Configuration.Definition.SandboxPolicy?.AllowedProfileIds.Contains(profile.Id, StringComparer.Ordinal) != true
                || current.SandboxPolicy?.AllowedProfileIds.Contains(profile.Id, StringComparer.Ordinal) != true)) throw new AccessDeniedException("sandbox_policy_changed");
        }
        if (value.Kind != SandboxReadKind.Profiles)
        {
            foreach (var (id, revision) in new[] { (snapshot.Lease?.Profile.Id, snapshot.Lease?.Profile.Revision), (snapshot.Execution?.ProfileId, snapshot.Execution?.ProfileRevision) })
                if (id is not null && !refs.Any(p => p.Id == id && p.Revision == revision)) throw new SecurityConflictException("sandbox_read_target_changed");
        }
        var execution = snapshot.Execution;
        if (value.Kind == SandboxReadKind.Process && execution is null || execution is not null && value.Kind != SandboxReadKind.Profiles
            && (execution.TenantId != session.Owner.TenantId || execution.SessionId != value.SessionId || execution.AgentInstanceId != run.AgentInstanceId
                || execution.ExecutorPrincipalId != session.Owner.ExecutorPrincipalId)) throw new AccessDeniedException("resource_unavailable");
    }
}
