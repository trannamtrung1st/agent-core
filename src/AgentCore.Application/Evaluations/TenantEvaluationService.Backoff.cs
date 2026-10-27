using AgentCore.Domain.Conversation;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
namespace AgentCore.Application.Evaluations;
public sealed partial class TenantEvaluationService
{
    private sealed record DeferredObservation(Guid TenantId, Guid? InitiatorId, string AuthorityHash, int Count, DateTimeOffset RetryAt, bool CatalogSensitive);
    private readonly Dictionary<Guid, DeferredObservation> deferred = [];
    private readonly object deferredGate = new();
    private DateTimeOffset nextAuthorityCheck;
    private DateTimeOffset scanPauseUntil;
    private async ValueTask<string> AuthorityHash(Guid tenant, Guid? initiator, bool catalogSensitive, CancellationToken ct)
    {
        var state = await security.ReadTenantAsync(tenant, ct);
        var accountId = state?.Principals.SingleOrDefault(p => p.PrincipalId == initiator)?.AccountId;
        var account = accountId is { } id ? await security.ReadAccountAsync(id, ct) : null;
        return EvaluationRules.Hash(new { state?.Tenant.Status, state?.Users, state?.Principals, state?.SystemIdentities, state?.Roles, state?.Assignments,
            CatalogRevision = catalogSensitive ? state?.Tenant.Revision : null, AccountStatus = account?.Status, AccountStamp = account?.SecurityStamp });
    }
    private async ValueTask<IReadOnlyList<Guid>> SuppressedRuns(CancellationToken ct)
    {
        var now = time.GetUtcNow(); KeyValuePair<Guid, DeferredObservation>[] entries;
        lock (deferredGate)
        {
            entries = deferred.ToArray();
            if (nextAuthorityCheck > now) return entries.Where(e => e.Value.RetryAt > now || e.Value.Count >= 5).Select(e => e.Key).ToArray();
            nextAuthorityCheck = now.AddSeconds(30);
        }
        foreach (var group in entries.GroupBy(e => (e.Value.TenantId, e.Value.InitiatorId, e.Value.CatalogSensitive)))
        {
            var hash = await AuthorityHash(group.Key.TenantId, group.Key.InitiatorId, group.Key.CatalogSensitive, ct);
            lock (deferredGate)
                foreach (var entry in group)
                    if (deferred.TryGetValue(entry.Key, out var current) && current == entry.Value && current.AuthorityHash != hash) deferred.Remove(entry.Key);
        }
        lock (deferredGate) return deferred.Where(e => e.Value.RetryAt > now || e.Value.Count >= 5).Select(e => e.Key).ToArray();
    }
    private async ValueTask DeferObservation(AgentRun run, bool authorityOnly, bool catalogSensitive, CancellationToken ct)
    {
        var tenant = run.Owner.TenantId!.Value; var initiator = run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId;
        var hash = await AuthorityHash(tenant, initiator, catalogSensitive, ct); var now = time.GetUtcNow();
        lock (deferredGate)
        {
            var count = deferred.TryGetValue(run.AgentRunId, out var prior) && prior.AuthorityHash == hash ? Math.Min(authorityOnly ? 5 : 4, prior.Count + 1) : 1;
            if (deferred.Count >= 512 && !deferred.ContainsKey(run.AgentRunId))
            {
                deferred.Remove(deferred.MinBy(e => e.Value.RetryAt).Key);
                scanPauseUntil = now.AddMinutes(1);
            }
            deferred[run.AgentRunId] = new(tenant, initiator, hash, count, now.AddSeconds(Math.Min(300, 30 * (1 << count))), catalogSensitive);
        }
    }
}
