using System.Collections.Concurrent;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Security;

public sealed record TenantRuntimeStateView(TenantAgent Agent, RuntimeStateObservation Observation, long SecurityRevision);

// Read-only, bounded observation. Every source receives only current, readable compound ownership.
public sealed partial class TenantRuntimeStateService(IIamStore security, ITenantAgentStore agents, ITenantSessionStore sessions,
    ITenantRuntimeStateStore storage, ITenantHostedRuntimeStateReader live, IdentityService identity,
    IamAuthorizer authorizer, ITenantBrowserStateReader browser, ISandboxReadiness sandboxReadiness, TimeProvider time,
    TenantOperationAuthorizer operations, TenantRunConfigurationResolver configuration)
{
    public async ValueTask<TenantRuntimeStateView> ReadAsync(Guid tenant, Guid instance, HumanSessionReference human, Guid? sessionId = null, CancellationToken ct = default)
    {
        var state = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        var agent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
        async ValueTask<bool> Can(string permission, AccessScope scope) =>
            (await authorizer.CheckTenantAsync(state, human.PrincipalId, PermissionCatalog.Get(permission), scope, ct).ConfigureAwait(false)).Allowed;
        if (!await Can("agents.read", AgentScope(tenant, instance)).ConfigureAwait(false)) throw new AccessDeniedException("permission_denied");
        TenantSessionRoot? selected = null;
        if (sessionId is { } id)
        {
            var session = await sessions.FindTenantSessionAsync(tenant, id, ct).ConfigureAwait(false) ?? throw new AccessDeniedException("resource_unavailable");
            if (session.Owner.AgentInstanceId != instance || session.Owner.ExecutorPrincipalId != agent.PrincipalId
                || !await Can("sessions.read", TenantSessionRules.Scope(tenant, id)).ConfigureAwait(false)) throw new AccessDeniedException("resource_unavailable");
            selected = session.Owner;
        }
        var observation = await Observe(state, agent, [human.PrincipalId], Can, sessionId, null, ct).ConfigureAwait(false);
        var currentAgent = await agents.FindTenantAgentAsync(tenant, instance, ct).ConfigureAwait(false);
        var current = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (!(await authorizer.CheckTenantAsync(current, human.PrincipalId, PermissionCatalog.Get("agents.read"), AgentScope(tenant, instance), ct).ConfigureAwait(false)).Allowed)
            throw new AccessDeniedException("permission_denied");
        if (selected is not null && (await sessions.FindTenantSessionAsync(tenant, selected.SessionId, ct).ConfigureAwait(false))?.Owner != selected)
            throw new AccessDeniedException("resource_unavailable");
        var final = await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct).ConfigureAwait(false);
        if (currentAgent?.PrincipalId != agent.PrincipalId || currentAgent.Instance.Revision != agent.Instance.Revision
            || current.Tenant.Revision != state.Tenant.Revision || final.Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision");
        return new(agent, observation, state.Tenant.Revision);
    }

    private async ValueTask<RuntimeStateObservation> Observe(TenantSecurityState state, TenantAgent agent, IReadOnlyList<Guid> principals,
        Func<string, AccessScope, ValueTask<bool>> can, Guid? sessionId, Guid? runId, CancellationToken ct)
    {
        var tenant = state.Tenant.TenantId; var instance = agent.Instance.InstanceId;
        var sessionIds = sessionId is { } selected
            ? await can("sessions.read", TenantSessionRules.Scope(tenant, selected)).ConfigureAwait(false) ? new[] { selected } : []
            : await ReadableIds("sessions.read", "session", []).ConfigureAwait(false);
        var runIds = await ReadableIds("agent-runs.read", "agent-run", runId is { } currentRun ? [currentRun] : []).ConfigureAwait(false);
        var profiles = await can("sandbox.read", AccessScope.ForTenant(tenant)).ConfigureAwait(false) ? null :
            await ReadableProfiles().ConfigureAwait(false);
        var owner = AgentRunOwner.ForTenant(tenant, instance, agent.PrincipalId);
        var query = new TenantRuntimeStateQuery(owner, sessionIds, runIds, profiles);
        var canSessions = sessionIds is null || sessionIds.Length > 0;
        var canRuns = canSessions && (runIds is null || runIds.Length > 0);
        var canBrowser = canSessions && await can("browser.read", AgentScope(tenant, instance)).ConfigureAwait(false);
        var canSandbox = canSessions && (profiles is null || profiles.Length > 0) && await can("sandbox.read", AgentScope(tenant, instance)).ConfigureAwait(false);
        var at = time.GetUtcNow();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2), time);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var issues = new ConcurrentBag<RuntimeStateIssue>();
        async Task<T?> Read<T>(string kind, bool allowed, Func<CancellationToken, ValueTask<T>> read) where T : class
        {
            if (!allowed) { issues.Add(new(kind, "read_restricted")); return null; }
            try { return await read(budget.Token).AsTask().WaitAsync(budget.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { issues.Add(new(kind, budget.IsCancellationRequested ? "observation_timeout" : "observation_unavailable")); return null; }
        }
        var durable = Read("sessions", canSessions, c => storage.ReadTenantSessionStateAsync(query, c));
        var runState = Read("runs", canRuns, c => storage.ReadTenantRunStateAsync(query, c));
        var hosted = Read("hostedSessions", canSessions, _ => ValueTask.FromResult(live.ObserveTenant(query)));
        var browserState = Read("browser", canBrowser, c => browser.ObserveTenantAgentAsync(new(tenant, instance, agent.PrincipalId), sessionIds, c));
        var sandboxState = Read("sandbox", canSandbox, async c => {
            var result = await storage.ReadTenantSandboxStateAsync(query, at, c).ConfigureAwait(false);
            return sandboxReadiness.IsAvailable ? result : result with { Availability = "unavailable", ActiveCommands = null, ActiveGenerations = null, Code = "provider_unavailable" };
        });
        await Task.WhenAll(durable, runState, hosted, browserState, sandboxState).ConfigureAwait(false);
        ResourceStateObservation Missing(string kind, bool allowed) => new(kind, allowed ? "unavailable" : "restricted", "unknown", at,
            Code: allowed ? "observation_unavailable" : "read_restricted");
        var resources = new[] { await browserState ?? Missing("browser", canBrowser), await sandboxState ?? Missing("sandbox", canSandbox) };
        foreach (var resource in resources) if (resource.Code is { } code) issues.Add(new(resource.Kind, code));
        var errors = issues.Distinct().OrderBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Code, StringComparer.Ordinal).ToArray();
        var any = await durable is not null || await runState is not null || await hosted is not null || await browserState is not null || await sandboxState is not null;
        return new(instance, at, at.AddSeconds(15), errors.Length == 0 ? "available" : any ? "partial" : "unavailable",
            await durable, await hosted, await runState, resources, errors, sessionId is null ? "readableInstance" : "currentSession");

        async ValueTask<Guid[]?> ReadableIds(string permission, string type, IReadOnlyList<Guid> known)
        {
            if (await can(permission, AccessScope.ForTenant(tenant)).ConfigureAwait(false)) return null;
            var ids = new List<Guid>();
            var candidates = state.Assignments.Where(a => principals.Contains(a.PrincipalId) && a.Scope.Kind == AccessScopeKind.Resource
                    && a.Scope.ResourceType == type).Select(a => a.Scope.ResourceId).Select(s => Guid.TryParseExact(s, "D", out var id) ? id : Guid.Empty)
                .Concat(known).Where(id => id != Guid.Empty).Distinct();
            foreach (var id in candidates) if (await can(permission, AccessScope.ForResource(tenant, type, id.ToString("D"))).ConfigureAwait(false)) ids.Add(id);
            return ids.ToArray();
        }
        async ValueTask<string[]> ReadableProfiles()
        {
            var result = new List<string>();
            foreach (var id in state.Assignments.Where(a => principals.Contains(a.PrincipalId) && TenantSandboxProfileRules.TryId(a.Scope, out _))
                .Select(a => a.Scope.ResourceId!).Distinct(StringComparer.Ordinal))
                if (await can("sandbox.read", TenantSandboxProfileRules.Scope(tenant, id)).ConfigureAwait(false)) result.Add(id);
            return result.ToArray();
        }
    }
    private static AccessScope AgentScope(Guid tenant, Guid instance) => AccessScope.ForResource(tenant, "agent", instance.ToString("D"));
}
