using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Admin;

public sealed record RuntimeStateIssue(string Kind, string Code);
public sealed record RuntimeStateObservation(Guid AgentInstanceId, DateTimeOffset ObservedAtUtc,
    DateTimeOffset FreshThroughUtc, string Availability, DurableSessionState? Sessions,
    HostedSessionState? HostedSessions, DurableRunState? Runs,
    IReadOnlyList<ResourceStateObservation> Resources, IReadOnlyList<RuntimeStateIssue> Issues, string Scope = "adminInstance");

/// <summary>Fixed, bounded read fan-out over existing owners. No cached authorization or provider actions.</summary>
public sealed class RuntimeStateService(IAgentInstanceStore instances, ILocalUserProfileService profiles,
    IMemoryStore sessions, IAgentRunStore runs, IHostedSessionStateReader live,
    IBrowserStateReader browser, ISandboxStore sandbox, ISandboxReadiness readiness, TimeProvider time)
{
    public ValueTask<RuntimeStateObservation> ObserveAsync(Guid instanceId, CancellationToken ct = default) => ObserveCoreAsync(instanceId, null, ct);

    public async ValueTask<RuntimeStateObservation> ObserveOwnedSessionAsync(Guid trustedInstanceId, Guid trustedSessionId, CancellationToken ct = default) =>
        await ObserveCoreAsync(trustedInstanceId, trustedSessionId, ct);

    private async ValueTask<RuntimeStateObservation> ObserveCoreAsync(Guid instanceId, Guid? sessionId, CancellationToken ct)
    {
        _ = await instances.FindAsync(instanceId, ct) ?? throw AgentCoreErrors.NotFound("Agent Instance was not found.");
        var profile = await profiles.GetLocalProfileAsync(ct);
        var owner = new AgentRunOwner(instanceId, profile.ProfileId);
        SessionSnapshot? current = null;
        if (sessionId is { } selected)
        {
            current = await sessions.LoadMetadataAsync(selected, ct);
            if (current is null || current.AgentInstanceId != instanceId || current.ProfileId != profile.ProfileId
                || current.DurablyDeletedAt is not null || current.ArchivedAt is not null)
                throw AgentCoreErrors.NotFound("Owned Session state was not found.");
        }
        var observedAt = time.GetUtcNow();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2), time);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        var issues = new System.Collections.Concurrent.ConcurrentBag<RuntimeStateIssue>();
        async Task<T?> Read<T>(string kind, Func<CancellationToken, ValueTask<T>> read) where T : class
        {
            try { return await read(budget.Token).AsTask().WaitAsync(budget.Token); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception)
            {
                issues.Add(new(kind, budget.IsCancellationRequested ? "observation_timeout" : "observation_unavailable"));
                return null;
            }
        }
        var durable = Read("sessions", c => current is null ? sessions.ReadStateAsync(owner, c)
            : ValueTask.FromResult(new DurableSessionState(current.LifecycleStatus == SessionLifecycleStatus.Active ? 1 : 0,
                current.LifecycleStatus == SessionLifecycleStatus.Paused ? 1 : 0, SessionLifecycle.IsTerminal(current.LifecycleStatus) ? 1 : 0, current.UpdatedAt)));
        var runState = Read("runs", c => sessionId is { } id ? runs.ReadSessionStateAsync(owner, id, c) : runs.ReadStateAsync(owner, c));
        var hosted = Read("hostedSessions", _ => ValueTask.FromResult(sessionId is { } id ? live.ObserveSession(owner, id) : live.Observe(owner)));
        var browsers = Read("browser", c => sessionId is { } id ? browser.ObserveSessionAsync(instanceId, id, c) : browser.ObserveAsync(instanceId, c));
        var sandboxes = Read("sandbox", async c => {
            var observed = sessionId is { } id ? await sandbox.ReadSessionStateAsync(owner, id, observedAt, c) : await sandbox.ReadStateAsync(owner, observedAt, c);
            // Durable leases remain observable during an outage, but do not prove live provider state.
            return readiness.IsAvailable ? observed : observed with { Availability = "unavailable", ActiveGenerations = null,
                ActiveCommands = null, Code = "provider_unavailable" };
        });
        await Task.WhenAll(durable, runState, hosted, browsers, sandboxes);
        var browserState = await browsers ?? new("browser", "unavailable", "unknown", observedAt, Code: "observation_unavailable");
        var sandboxState = await sandboxes ?? new("sandbox", "unavailable", "isolated", observedAt, Code: "observation_unavailable");
        foreach (var resource in new[] { browserState, sandboxState })
            if (resource.Code is { } code) issues.Add(new(resource.Kind, code));
        var errors = issues.Distinct().OrderBy(i => i.Kind, StringComparer.Ordinal).ThenBy(i => i.Code, StringComparer.Ordinal).ToArray();
        // Freshness starts before collection: a slow observation cannot extend an old sample's lifetime.
        var anySource = await durable is not null || await hosted is not null || await runState is not null || await browsers is not null || await sandboxes is not null;
        return new(instanceId, observedAt, observedAt.AddSeconds(15), errors.Length == 0 ? "available" : anySource ? "partial" : "unavailable",
            await durable, await hosted, await runState, [browserState, sandboxState], errors, sessionId is null ? "adminInstance" : "currentSession");
    }
}
