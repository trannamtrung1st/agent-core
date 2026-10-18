using AgentCore.Application.Agents;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed class TenantBrowserOperationTarget(ITenantSessionStore sessions, TenantRunConfigurationResolver configuration,
    IIamStore security, IamAuthorizer authorizer, IBrowser browser)
{
    public async ValueTask<(TenantAgent Agent, PreparedBrowserCommand Command, TenantBrowserCommandWitness Witness)> PrepareAsync(AgentRun run, string tool, string json, CancellationToken ct)
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
        await TenantBrowserOperationRules.RequirePermissionsAsync(state, run.AgentInstanceId, command, run.Owner.ExecutorPrincipalId!.Value, authorizer, ct).ConfigureAwait(false);
        var target = browser as ITenantBrowserCommandTarget ?? throw new AccessDeniedException("tenant_effect_unavailable");
        var prepared = await target.PrepareTenantCommandAsync(TenantBrowserOperationRules.Owner(session.Owner), intent.Command, ct).ConfigureAwait(false);
        if (prepared.ErrorCode is { } error) throw new AccessDeniedException(error);
        var witness = prepared.Witness ?? throw new AccessDeniedException("browser_target_required");
        if (witness.Owner != TenantBrowserOperationRules.Owner(session.Owner) || witness.CommandHash != command.CommandHash)
            throw new SecurityConflictException("browser_target_changed");
        var current = InstanceSettingsResolver.Resolve(baseline, agent.Instance.SettingsOverrides);
        var mode = TenantBrowserOperationRules.Isolation(run.Admission.Configuration!.Definition, current) switch {
            BrowserIsolation.EphemeralSession => BrowserProfileMode.EphemeralSession,
            BrowserIsolation.PersistentAgent => BrowserProfileMode.PersistentAgent, _ => browser.HostPolicy.ProfileMode };
        if (witness.ProfileMode != mode) throw new AccessDeniedException("policy_transition_pending");
        command = command with { TargetJson = TenantApprovalRules.BrowserTarget(witness, prepared.Origin) };
        TenantBrowserOperationRules.Require(command, run, agent, baseline, session);
        return (agent, command, witness);
    }
    public async ValueTask RequireAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct) => _ = await ResolveApprovedAsync(request, run, ct).ConfigureAwait(false);
    public async ValueTask<TenantBrowserCommandWitness> ResolveApprovedAsync(TenantApprovalRequest request, AgentRun run, CancellationToken ct)
    {
        var saved = request.Operation.BrowserCommand ?? throw new ArgumentException("Browser operation required.");
        var current = await PrepareAsync(run, saved.ToolName, saved.ArgumentsJson, ct).ConfigureAwait(false);
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
        return agent;
    }
}
