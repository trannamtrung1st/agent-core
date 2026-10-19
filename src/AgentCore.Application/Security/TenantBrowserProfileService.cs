using AgentCore.Application.Ports;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantBrowserProfileResetView(Guid OperationId, string Status, string ReasonCode, DateTimeOffset RequestedAt, DateTimeOffset? RecordedAt);
public sealed record TenantBrowserProfileView(Guid InstanceId, long InstanceRevision, long SecurityRevision, string Witness,
    bool ProfilePresent, int LiveViews, bool CanReset, bool Busy, TenantBrowserProfileResetView? LastReset);

public sealed class TenantBrowserProfileService(IIamStore security, ITenantAgentStore agents, ITenantBrowserProfileStore store,
    IdentityService identity, IamAuthorizer authorizer, IBrowser browser, TimeProvider time, IIdGenerator ids)
{
    private DateTimeOffset Now() => DateTimeOffset.FromUnixTimeMilliseconds(time.GetUtcNow().ToUnixTimeMilliseconds());
    private ITenantBrowserProfileControl Control => browser as ITenantBrowserProfileControl ?? throw new AccessDeniedException("provider_unavailable");
    private async ValueTask<(TenantSecurityState State, TenantAgent Agent, TenantBrowserAgentOwner Owner)> Require(Guid tenant, Guid instance,
        HumanSessionReference human, bool manage, CancellationToken ct)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        var owner = new TenantBrowserAgentOwner(tenant, instance, agent.PrincipalId);
        if (!TenantBrowserProfileRules.Matches(owner, agent) || !await TenantBrowserProfileRules.Allowed(state, owner, human.PrincipalId, authorizer, manage, ct))
            throw new AccessDeniedException("permission_denied");
        if (manage && agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) throw new AccessDeniedException("agent_archived");
        return (state, agent, owner);
    }
    private async ValueTask<TenantBrowserProfileObservation> Observe(TenantBrowserAgentOwner owner, CancellationToken ct)
    {
        try { return await Control.ReadTenantProfileAsync(owner, ct); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new AccessDeniedException("browser_profile_unavailable"); }
    }
    public async ValueTask<TenantBrowserProfileView> ReadAsync(Guid tenant, Guid instance, HumanSessionReference human, CancellationToken ct = default)
    {
        var before = await Require(tenant, instance, human, false, ct);
        var observed = await Observe(before.Owner, ct);
        if (!observed.Busy && before.Agent.Instance.BrowserProfileReset is { Status: "pending" } pending && observed.InFlightOperation != pending.OperationId)
        {
            var result = observed.LastResult?.OperationId == pending.OperationId ? observed.LastResult : new(pending.OperationId, "unknown", "browser_profile_reset_unknown");
            await store.TrySettleTenantProfileResetAsync(before.Owner, pending.OperationId, result.Status, result.ReasonCode, Now(), ids.NewId(), ct);
            // Recovery records facts only; it never repeats close or deletion. Obtain fresh current read witnesses.
            before = await Require(tenant, instance, human, false, ct); observed = await Observe(before.Owner, ct);
        }
        var after = await Require(tenant, instance, human, false, ct);
        if (before.State.Tenant.Revision != after.State.Tenant.Revision || before.Agent.Instance.Revision != after.Agent.Instance.Revision || before.Owner != after.Owner)
            throw new SecurityConflictException("stale_revision");
        var manage = agentActive(after.Agent) && await TenantBrowserProfileRules.Allowed(after.State, after.Owner, human.PrincipalId, authorizer, true, ct);
        var final = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
        if (final.Tenant.Revision != after.State.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return View(after.Agent, after.State.Tenant.Revision, observed, manage);
        static bool agentActive(TenantAgent agent) => agent.Instance.Lifecycle == AgentInstanceLifecycle.Active;
    }
    private static TenantBrowserProfileView View(TenantAgent agent, long revision, TenantBrowserProfileObservation observed, bool manage)
    {
        var saved = agent.Instance.BrowserProfileReset;
        return new(agent.Instance.InstanceId, agent.Instance.Revision, revision, observed.Witness, observed.ProfilePresent, observed.LiveViews,
            manage && !observed.Busy && observed.InFlightOperation is null && saved?.Status != "pending", observed.Busy || observed.InFlightOperation is not null,
            saved is null ? null : new(saved.OperationId, saved.Status, saved.ReasonCode, saved.RequestedAt, saved.RecordedAt));
    }
    public async ValueTask<TenantBrowserProfileResetView> ResetAsync(Guid tenant, Guid instance, HumanSessionReference human, Guid operation,
        long expectedSecurityRevision, long expectedInstanceRevision, string witness, CancellationToken ct = default)
    {
        if (operation == Guid.Empty || witness is not { Length: 64 } || witness.Any(c => !char.IsAsciiHexDigitLower(c))) throw new ArgumentException("Use a fresh exact Browser reset witness.");
        var before = await Require(tenant, instance, human, true, ct);
        if (before.Agent.Instance.BrowserProfileReset is { } existing && existing.OperationId == operation)
            return new(existing.OperationId, existing.Status, existing.ReasonCode, existing.RequestedAt, existing.RecordedAt);
        if (before.State.Tenant.Revision != expectedSecurityRevision || before.Agent.Instance.Revision != expectedInstanceRevision) throw new SecurityConflictException("stale_revision");
        TenantBrowserProfileCommit? started = null;
        TenantBrowserProfileResult result;
        try
        {
            result = await Control.ResetTenantProfileAsync(before.Owner, operation, witness, async (epoch, token) => {
                var now = Now(); var reset = new AgentBrowserProfileReset(operation, human.PrincipalId, epoch, witness, "pending", "browser_profile_reset_started", now);
                var audit = new AuthorizationAudit(operation, tenant, human.PrincipalId, "browser.profile.reset", instance.ToString("D"), "allowed", reset.ReasonCode, now);
                started = await store.TryBeginTenantProfileResetAsync(before.Owner, human, expectedSecurityRevision, expectedInstanceRevision, reset, audit, token);
                return started is not null;
            }, async token => {
                if (started is null) return false;
                try
                {
                    var current = await Require(tenant, instance, human, true, token);
                    return current.Owner == before.Owner && current.State.Tenant.Revision == started.SecurityRevision
                        && current.Agent.Instance.Revision == started.Agent.Instance.Revision
                        && current.Agent.Instance.BrowserProfileReset?.OperationId == operation && current.Agent.Instance.BrowserProfileReset.Status == "pending";
                }
                catch (AccessDeniedException) { return false; }
            }, ct);
        }
        catch (Exception) when (started is not null) { result = new(operation, "unknown", "browser_profile_reset_unknown"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new AccessDeniedException("browser_profile_unavailable"); }
        if (started is null) throw new SecurityConflictException(result.ReasonCode);
        // Factual completion must not be revoked by caller cancellation or later human access loss.
        using var settle = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        var saved = await store.TrySettleTenantProfileResetAsync(before.Owner, operation, result.Status, result.ReasonCode, Now(), ids.NewId(), settle.Token);
        var resetResult = saved?.Agent.Instance.BrowserProfileReset ?? started.Agent.Instance.BrowserProfileReset!;
        return new(resetResult.OperationId, resetResult.Status, resetResult.ReasonCode, resetResult.RequestedAt, resetResult.RecordedAt);
    }
}
