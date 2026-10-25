using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantCredentialOperationTarget(ITenantCredentialBindingStore bindings, TenantRunConfigurationResolver configuration)
{
    public async ValueTask<(TenantAgent Agent, TenantCredentialBinding Bound)> ResolveAsync(AgentRun run, string reference, CancellationToken ct)
    {
        var alias = CredentialRules.Reference(reference);
        var snapshot = await configuration.CurrentCredentialBindingsAsync(run, ct, bindings).ConfigureAwait(false)
            ?? throw new AccessDeniedException("executor_unavailable");
        var bound = snapshot.Bindings.SingleOrDefault(b => b.Binding.Reference == alias) ?? throw new AccessDeniedException("credential_unavailable");
        var current = await configuration.CurrentPolicyAsync(run.Owner.TenantId!.Value, run.AgentInstanceId, ct).ConfigureAwait(false);
        if (current.Agent.Instance.Revision != snapshot.Agent.Instance.Revision) throw new SecurityConflictException("stale_revision");
        TenantCredentialOperationRules.RequireBinding(run, snapshot.Agent, bound, current.Definition);
        return (snapshot.Agent, bound);
    }
}

public static class TenantCredentialOperationRules
{
    public static bool Allows(AgentDefinition definition, CredentialPurpose purpose) => purpose switch {
        CredentialPurpose.EmailAccount => RolePermissions.AllowsTool(definition, ToolCatalog.EmailAccounts),
        CredentialPurpose.GoogleDriveAccount => RolePermissions.AllowsTool(definition, ToolCatalog.DriveAccounts),
        _ => false };

    public static void RequireBinding(AgentRun run, TenantAgent agent, TenantCredentialBinding bound, AgentDefinition current)
    {
        var b = bound.Binding; var c = bound.Credential;
        if (run.Owner.TenantId != agent.TenantId || c.Owner.TenantId != agent.TenantId || c.Owner.DeletedAt is not null
            || b.AgentInstanceId != agent.Instance.InstanceId || b.AgentInstanceId != run.AgentInstanceId || b.CredentialId != c.Value.CredentialId
            || agent.PrincipalId != run.Admission.ExecutionContext?.AgentPrincipalId || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active
            || !run.Admission.Configuration!.CredentialBindings.Any(p => p == b)
            || !Allows(run.Admission.Configuration.Definition, c.Value.Purpose) || !Allows(current, c.Value.Purpose))
            throw new AccessDeniedException("credential_binding_changed");
        Connected(c.Value);
    }
    public static void Connected(Credential c)
    {
        if (!TenantCredentialOAuthRules.IsOAuth(c) || c.Status != CredentialStatus.Active || c.ProtectedPayload.Length == 0
            || !(c.EmailAccount?.ConnectionStatus == EmailConnectionStatus.Connected || c.DriveAccount?.ConnectionStatus == DriveConnectionStatus.Connected))
            throw new AccessDeniedException("oauth_reconnect_required");
    }
    public static PreparedCredentialConnectionCheck Parameters(TenantCredentialBinding bound) => new(bound.Credential.Value.CredentialId,
        bound.Binding.BindingId, bound.Binding.Reference, bound.Binding.Revision, bound.Credential.Value.Revision,
        bound.Credential.Value.SecretRevision, TenantCredentialOAuthRules.Epoch(bound.Credential.Value));
    public static TenantApprovalSnapshot Snapshot(TenantAgent agent, TenantCredentialBinding bound) => new(agent.Instance.Persona.Name,
        Credential: new(bound.Credential.Value.DisplayName, bound.Credential.Value.Purpose.ToString(), "Connected"));
    public static void RequireWitness(PreparedTenantOperation op, TenantCredentialBinding bound)
    {
        if (op.CredentialCheck != Parameters(bound)) throw new SecurityConflictException("operation_witness_changed");
    }
    public static void RequireRequest(TenantApprovalRequest request, AgentRun run, TenantAgent agent, TenantCredentialBinding bound)
    {
        RequireBinding(run, agent, bound, run.Admission.Configuration!.Definition);
        RequireWitness(request.Operation, bound);
        if (request.Snapshot?.Credential != Snapshot(agent, bound).Credential) throw new ArgumentException("Credential review evidence does not match the target.");
    }
}
