using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantBrowserOperationTarget(ITenantSessionStore sessions, TenantRunConfigurationResolver configuration,
    IIamStore security, IamAuthorizer authorizer, IBrowser browser, TenantBrowserCredentialTarget? passwords = null)
{
    public async ValueTask<(TenantAgent Agent, PreparedBrowserCommand Command, TenantBrowserCommandWitness Witness)> PrepareAsync(AgentRun run, string tool, string json, CancellationToken ct, bool registerCold = false)
    {
        var intent = TenantApprovalRules.BrowserIntent(run.SessionId, tool, json);
        var destinationUrl = intent.Command switch { BrowserNavigate { Operation: "goto" } navigation => navigation.Url,
            BrowserTabs { Operation: "new" } tab => tab.Url, _ => null };
        if (destinationUrl is not null)
        {
            var destination = BrowserTargetPolicy.EvaluateDestination(destinationUrl, browser.HostPolicy.NavigationOrigins, browser.HostPolicy.PolicyMode);
            if (!destination.Allowed) throw new AccessDeniedException(destination.Code ?? "target_denied");
        }
        var tenant = run.Owner.TenantId ?? throw new AccessDeniedException("tenant_context_required");
        var (agent, baseline) = await configuration.BaselineAsync(tenant, run.AgentInstanceId, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(tenant, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var state = await security.ReadTenantAsync(tenant, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("tenant_unavailable");
        // Read/primary IAM and both capability ceilings precede the provider target observation.
        var command = new PreparedBrowserCommand(run.SessionId, tool, intent.Arguments, BrowserCommandIdentity.Hash(intent.Command),
            "", intent.ReadOnly);
        TenantBrowserOperationRules.RequireSource(run.SessionId, tool, run, agent, baseline, session);
        var credential = intent.Command is BrowserFillCredential password
            ? await (passwords ?? throw new AccessDeniedException("tenant_effect_unavailable")).ResolveAsync(run, password.CredentialRef, ct).ConfigureAwait(false) : null;
        if (credential is not null) command = command with { Credential = TenantBrowserCredentialRules.Parameters(credential, "") };
        await TenantBrowserOperationRules.RequirePermissionsAsync(state, run.AgentInstanceId, command, run.Owner.ExecutorPrincipalId!.Value, authorizer, ct).ConfigureAwait(false);
        var current = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides);
        var mode = TenantBrowserOperationRules.Isolation(run.Admission.Configuration!.Definition, current) switch {
            BrowserIsolation.EphemeralSession => BrowserProfileMode.EphemeralSession,
            BrowserIsolation.PersistentAgent => BrowserProfileMode.PersistentAgent, _ => browser.HostPolicy.ProfileMode };
        if (registerCold)
        {
            if (browser is not ITenantBrowserBinding binding) throw new AccessDeniedException("tenant_effect_unavailable");
            if (await binding.EnsureTenantIsolationAsync(TenantBrowserOperationRules.Owner(session.Owner), mode, ct).ConfigureAwait(false) is { } unavailable)
                throw new AccessDeniedException(unavailable);
        }
        var target = browser as ITenantBrowserCommandTarget ?? throw new AccessDeniedException("tenant_effect_unavailable");
        var prepared = await target.PrepareTenantCommandAsync(TenantBrowserOperationRules.Owner(session.Owner), intent.Command, ct).ConfigureAwait(false);
        if (prepared.ErrorCode is { } error) throw new AccessDeniedException(error);
        var witness = prepared.Witness ?? throw new AccessDeniedException("browser_target_required");
        if (witness.Owner != TenantBrowserOperationRules.Owner(session.Owner) || witness.CommandHash != command.CommandHash)
            throw new SecurityConflictException("browser_target_changed");
        if (witness.ProfileMode != mode) throw new AccessDeniedException("policy_transition_pending");
        command = command with { TargetJson = TenantApprovalRules.BrowserTarget(witness, prepared.Origin) };
        if (credential is not null)
        {
            command = command with { Credential = TenantBrowserCredentialRules.Parameters(credential, witness.CredentialOrigin ?? throw new AccessDeniedException("credential_origin_changed")) };
            TenantBrowserCredentialRules.RequireWitness(command.Credential, credential);
        }
        TenantBrowserOperationRules.Require(command, run, agent, baseline, session);
        return (agent, command, witness);
    }
    public async ValueTask RequireAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct) => _ = await ResolveApprovedAsync(request, run, ct).ConfigureAwait(false);
    public async ValueTask<TenantBrowserCommandWitness> ResolveApprovedAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct)
    {
        var saved = request.Operation.BrowserCommand ?? throw new ArgumentException("Browser operation required.");
        // Restore trusted metadata after host loss, then compare the actual target. Never restore old native handles or transition an existing view.
        var current = await PrepareAsync(run, saved.ToolName, saved.ArgumentsJson, ct, registerCold: browser is ITenantBrowserBinding).ConfigureAwait(false);
        if (saved.Credential != current.Command.Credential)
            throw new SecurityConflictException(saved.Credential?.Origin != current.Command.Credential?.Origin ? "credential_origin_changed" : "credential_binding_changed");
        if (current.Command != saved || current.Agent.Instance.Revision != request.Operation.ExpectedInstanceRevision
            || current.Agent.Instance.PersonaRevision != request.Operation.ExpectedPersonaRevision
            || request.Snapshot != TenantBrowserOperationRules.Snapshot(current.Agent, current.Command))
            throw new SecurityConflictException("browser_target_changed");
        return current.Witness;
    }
    // Used inside the native provider gate. No provider reentry, binding or handle observation.
    public async ValueTask<TenantAgent> RequireSourceAsync(TenantApprovalRequest request, AgentRun run, AgentRun? source, CancellationToken ct)
    {
        var value = request.Operation.BrowserCommand ?? throw new ArgumentException("Browser operation required.");
        var (agent, baseline) = await configuration.BaselineAsync(request.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(request.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantApprovalRules.Validate(request, run, agent, source: source);
        TenantBrowserOperationRules.Require(value, run, agent, baseline, session);
        if (value.Credential is { } credential)
            await (passwords ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(run, credential, ct).ConfigureAwait(false);
        return agent;
    }
    // Historical output can outlive one approval hop; it never supplies dispatch authority.
    public async ValueTask<TenantAgent> RequireObservationSourceAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct)
    {
        var value = request.Operation.BrowserCommand ?? throw new AccessDeniedException("browser_source_changed");
        var context = run.Admission.ExecutionContext ?? throw new AccessDeniedException("tenant_context_required");
        if (request.TenantId != run.Owner.TenantId || request.SessionId != run.SessionId || request.Operation.InstanceId != run.AgentInstanceId
            || request.ExecutorPrincipalId != run.Owner.ExecutorPrincipalId || request.OriginalInitiatorPrincipalId != context.OriginalInitiatorPrincipalId
            || request.RootActivationId != context.RootActivationId || request.Status != TenantApprovalStatus.Approved)
            throw new AccessDeniedException("browser_source_changed");
        var (agent, baseline) = await configuration.BaselineAsync(request.TenantId, run.AgentInstanceId, ct).ConfigureAwait(false);
        var session = await sessions.FindTenantSessionAsync(request.TenantId, run.SessionId, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        TenantBrowserOperationRules.Require(value, run, agent, baseline, session);
        if (value.Credential is { } credential)
            await (passwords ?? throw new AccessDeniedException("tenant_effect_unavailable")).RequireAsync(run, credential, ct).ConfigureAwait(false);
        return agent;
    }

    public ValueTask<string> PasswordAsync(TenantApprovalRequest request, AgentRun run, string origin, CancellationToken ct) =>
        (passwords ?? throw new AccessDeniedException("tenant_effect_unavailable")).PasswordAsync(run,
            request.Operation.BrowserCommand?.Credential ?? throw new AccessDeniedException("credential_unavailable"), origin, ct);

}
