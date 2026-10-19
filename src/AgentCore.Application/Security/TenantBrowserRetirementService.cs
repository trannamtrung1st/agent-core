using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Security;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Security;

/// <summary>Technical cleanup follows canonical lifecycle; it never impersonates a human or grants Browser use.</summary>
public sealed class TenantBrowserRetirementService(IIamStore security, ITenantSessionStore sessions, ITenantAgentStore agents,
    IBrowser browser, TimeProvider time, ILogger<TenantBrowserRetirementService>? logger = null, IDiagnosticIdSource? diagnosticIds = null)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Guid? _cursor;

    public async ValueTask<int> RecoverAsync(CancellationToken ct = default)
    {
        if (browser is not ITenantBrowserRetirement retirement) return 0;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var page = await retirement.ListTenantRetirementCandidatesAsync(_cursor, 100, ct).ConfigureAwait(false);
            var closed = 0;
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
            foreach (var owner in page.Items)
            {
                // Advance even on a timed-out owner so one stuck context cannot starve the remaining page.
                _cursor = owner.SessionId;
                try { if (await RetireAsync(retirement, owner, linked.Token).ConfigureAwait(false)) closed++; }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested) { return closed; }
            }
            _cursor = page.NextCursor;
            return closed;
        }
        finally { _gate.Release(); }
    }

    public async ValueTask ReconcileAsync(Guid tenant, Guid? session = null, Guid? instance = null, CancellationToken ct = default)
    {
        if (browser is not ITenantBrowserRetirement retirement) return;
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        Guid? cursor = null;
        try
        {
            do
            {
                var page = await retirement.ListTenantRetirementCandidatesAsync(cursor, 100, linked.Token).ConfigureAwait(false);
                foreach (var owner in page.Items.Where(o => o.TenantId == tenant && (session is null || o.SessionId == session)
                    && (instance is null || o.AgentInstanceId == instance))) await RetireAsync(retirement, owner, linked.Token).ConfigureAwait(false);
                cursor = page.NextCursor;
            } while (cursor is not null);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && budget.IsCancellationRequested) { }
    }

    private async ValueTask<bool> RetireAsync(ITenantBrowserRetirement retirement, TenantBrowserOwner owner, CancellationToken ct)
    {
        try { return await retirement.RetireTenantAsync(owner, token => IsRetiredAsync(owner, token), ct).ConfigureAwait(false) == "closed"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            HandledFailureLog.Warning(logger, error, "browser.cleanup", "browser_retirement_failed",
                new(SessionId: owner.SessionId, AgentInstanceId: owner.AgentInstanceId), diagnostics: diagnosticIds);
            return false; // Existing native handles and canonical lifecycle remain discoverable on the next sweep.
        }
    }

    private async ValueTask<bool> IsRetiredAsync(TenantBrowserOwner owner, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(owner.TenantId, ct).ConfigureAwait(false);
        if (state is null) return true;
        if (state.Tenant.TenantId != owner.TenantId) throw new AccessDeniedException("browser_owner_mismatch");
        if (state.Tenant.Status != IdentityStatus.Active) return true;
        var agent = await agents.FindTenantAgentAsync(owner.TenantId, owner.AgentInstanceId, ct).ConfigureAwait(false);
        if (agent is null) return true;
        if (agent.TenantId != owner.TenantId || agent.Instance.InstanceId != owner.AgentInstanceId || agent.PrincipalId != owner.ExecutorPrincipalId) throw new AccessDeniedException("browser_owner_mismatch");
        if (agent.Instance.Lifecycle != AgentInstanceLifecycle.Active) return true;
        try { TenantSessionRules.RequireExecutor(state, agent); }
        catch (AccessDeniedException) { return true; }
        var session = await sessions.FindTenantSessionAsync(owner.TenantId, owner.SessionId, ct).ConfigureAwait(false);
        if (session is null) return true; // Native ownership was sealed before canonical Session deletion.
        if (session.Owner.TenantId != owner.TenantId || session.Owner.SessionId != owner.SessionId
            || session.Owner.AgentInstanceId != owner.AgentInstanceId || session.Owner.ExecutorPrincipalId != owner.ExecutorPrincipalId)
            throw new AccessDeniedException("browser_owner_mismatch");
        return session.Snapshot.DurablyDeletedAt is not null || session.Snapshot.ArchivedAt is not null
            || session.Snapshot.LifecycleStatus != SessionLifecycleStatus.Active || session.Snapshot.Status is SessionStatus.Ending or SessionStatus.Ended;
    }
}
