using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

// Bound protected values stay inside the incumbent Credential protector/native sink boundary.
public sealed class TenantBrowserCredentialTarget(ITenantCredentialBindingStore bindings, TenantRunConfigurationResolver configuration,
    ICredentialProtector protector, ITenantRunStore runs)
{
    public async ValueTask<TenantCredentialBinding> ResolveAsync(AgentRun run, string reference, CancellationToken ct)
    {
        await TenantBrowserCredentialRules.RequireChatLineageAsync(run, (id, token) => runs.FindTenantRunAsync(run.Owner.TenantId!.Value, id, token), ct).ConfigureAwait(false);
        var snapshot = await configuration.CurrentCredentialBindingsAsync(run, ct, bindings).ConfigureAwait(false)
            ?? throw new AccessDeniedException("credential_unavailable");
        var bound = snapshot.Bindings.SingleOrDefault(b => b.Binding.Reference == CredentialRules.Reference(reference))
            ?? throw new AccessDeniedException("credential_unavailable");
        var (agent, baseline) = await configuration.BaselineAsync(run.Owner.TenantId!.Value, run.AgentInstanceId, ct).ConfigureAwait(false);
        if (snapshot.Agent.Instance.Revision != agent.Instance.Revision) throw new SecurityConflictException("stale_revision");
        TenantBrowserCredentialRules.RequireBinding(run, agent, bound, baseline);
        return bound;
    }
    public async ValueTask RequireAsync(AgentRun run, PreparedBrowserCredential saved, CancellationToken ct)
    {
        var bound = await ResolveAsync(run, saved.Reference, ct).ConfigureAwait(false);
        TenantBrowserCredentialRules.RequireWitness(saved, bound);
    }
    public async ValueTask<string> PasswordAsync(AgentRun run, PreparedBrowserCredential saved, string origin, CancellationToken ct)
    {
        if (origin != saved.Origin) throw new AccessDeniedException("credential_origin_changed");
        var bound = await ResolveAsync(run, saved.Reference, ct).ConfigureAwait(false);
        TenantBrowserCredentialRules.RequireWitness(saved, bound); ct.ThrowIfCancellationRequested();
        return protector.Unprotect(bound.Credential.Value.CredentialId, bound.Credential.Value.ProtectionVersion, bound.Credential.Value.ProtectedPayload);
    }
}

public static class TenantBrowserCredentialRules
{
    public static async ValueTask RequireChatLineageAsync(AgentRun leaf, Func<Guid, CancellationToken, ValueTask<AgentRun?>> read, CancellationToken ct)
    {
        var current = leaf; var visited = new HashSet<Guid>();
        var maximum = leaf.Admission.ExecutionBudget?.Profile.MaxSteps ?? 144;
        while (current.Admission.Activation.Kind == ActivationKind.ApprovalContinuation)
        {
            var resume = current.Admission.ApprovalResume ?? throw new AccessDeniedException("credential_direct_chat_required");
            if (!visited.Add(current.AgentRunId) || visited.Count > maximum) throw new AccessDeniedException("credential_direct_chat_required");
            var source = await read(resume.SourceRunId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("credential_direct_chat_required");
            if (source.AgentRunId != resume.SourceRunId || current.Admission.Activation.SourceAgentRunId != source.AgentRunId
                || source.Owner != leaf.Owner || source.SessionId != leaf.SessionId || source.ResponseId != leaf.ResponseId
                || source.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
                || source.Status != AgentRunStatus.Completed || source.Result?.OutcomeKind != AgentRunOutcomeKind.Continued
                || source.Result.ContinuationRunId != current.AgentRunId || source.ApprovalRequestId != resume.RequestId
                || current.Admission.Activation.ParentActivationId != source.ActivationId || current.Admission.Activation.SourceSessionId != leaf.SessionId
                || current.Admission.ExecutionContext?.ParentRunId != source.AgentRunId
                || current.Admission.ExecutionContext?.ParentActivationId != source.ActivationId
                || source.Admission.Activation.RootActivationId != leaf.Admission.Activation.RootActivationId
                || source.Admission.ExecutionContext?.OriginalInitiatorPrincipalId != leaf.Admission.ExecutionContext?.OriginalInitiatorPrincipalId
                || !resume.SourceInputEntryIds.SequenceEqual(TenantApprovalContinuationRules.Inputs(source)))
                throw new AccessDeniedException("credential_direct_chat_required");
            current = source;
        }
        if (current.Admission.Activation.Kind != ActivationKind.UserTurn || current.ActivationId != leaf.Admission.Activation.RootActivationId
            || current.SessionId != leaf.SessionId || current.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is null
            || current.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
            || !current.Admission.Activation.SourceEntryIds.SequenceEqual(TenantApprovalContinuationRules.Inputs(leaf)))
            throw new AccessDeniedException("credential_direct_chat_required");
    }
    public static PreparedBrowserCredential Parameters(TenantCredentialBinding bound, string origin) => new(bound.Credential.Value.CredentialId,
        bound.Binding.BindingId, bound.Binding.Reference, bound.Binding.Revision, bound.Credential.Value.Revision,
        bound.Credential.Value.SecretRevision, origin, bound.Credential.Value.DisplayName);
    public static void RequireBinding(AgentRun run, TenantAgent agent, TenantCredentialBinding bound, AgentDefinition baseline)
    {
        var current = InstanceSettingsResolver.ResolveForAgent(baseline, agent);
        var b = bound.Binding; var c = bound.Credential;
        if (run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is null || run.Admission.OutputContract != AgentRunOutputContract.ConversationResponse
            || run.Admission.Activation.Kind is not (ActivationKind.UserTurn or ActivationKind.ApprovalContinuation))
            throw new AccessDeniedException("credential_direct_chat_required");
        if (run.Owner.TenantId != agent.TenantId || c.Owner.TenantId != agent.TenantId || c.Owner.DeletedAt is not null
            || c.Owner.CredentialId != c.Value.CredentialId || b.CredentialId != c.Value.CredentialId || b.AgentInstanceId != run.AgentInstanceId
            || agent.Instance.InstanceId != run.AgentInstanceId || agent.PrincipalId != run.Owner.ExecutorPrincipalId || agent.Instance.Lifecycle != AgentInstanceLifecycle.Active
            || !run.Admission.Configuration!.CredentialBindings.Any(p => p == b)
            || !RolePermissions.AllowsTool(run.Admission.Configuration.Definition, ToolCatalog.BrowserFillCredential)
            || !RolePermissions.AllowsTool(current, ToolCatalog.BrowserFillCredential)) throw new AccessDeniedException("credential_binding_changed");
        if (c.Value.Kind != CredentialKind.Password || c.Value.Purpose != CredentialPurpose.General || c.Value.Status != CredentialStatus.Active
            || string.IsNullOrEmpty(c.Value.ProtectedPayload)) throw new AccessDeniedException("credential_unavailable");
    }
    public static void RequireWitness(PreparedBrowserCredential saved, TenantCredentialBinding bound)
    {
        if (saved != Parameters(bound, saved.Origin)) throw new SecurityConflictException("credential_binding_changed");
        if (!bound.Credential.Value.AllowedOrigins.Contains(saved.Origin, StringComparer.Ordinal)) throw new AccessDeniedException("credential_origin_changed");
    }
    public static void Shape(PreparedBrowserCommand value, BrowserCommand command)
    {
        if (command is not BrowserFillCredential)
        {
            if (value.Credential is not null) throw new ArgumentException("Unexpected protected Credential witness.");
            return;
        }
        var c = value.Credential;
        if (c is null || c.CredentialId == Guid.Empty || c.BindingId == Guid.Empty || c.BindingRevision < 1 || c.CredentialRevision < 1 || c.SecretRevision < 0
            || c.Reference != CredentialRules.Reference(c.Reference) || ((BrowserFillCredential)command).CredentialRef != c.Reference
            || c.Origin != CredentialRules.Origin(c.Origin) || c.DisplayName != CredentialRules.DisplayName(c.DisplayName) || value.ReadOnly
            || TenantApprovalRules.BrowserTarget(value).ContextId is null)
            throw new ArgumentException("An exact protected Credential and existing Browser target are required.");
    }
}
