using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Credentials;
using AgentCore.Domain.Email;
using AgentCore.Domain.Events;
using AgentCore.Domain.Security;
using AgentCore.Domain.Triggers;

namespace AgentCore.Application.Security;

public static class TenantEmailWatchRules
{
    public static readonly string[] SourcePermissions = ["credentials.read", "credentials.use", "email.read"];
    public static async ValueTask<IReadOnlyList<Guid>?> VisibleAccountsAsync(TenantSecurityState state, Guid actor, IamAuthorizer iam, CancellationToken ct)
    {
        var tenant = state.Tenant.TenantId; var all = true;
        foreach (var key in SourcePermissions)
            all &= (await iam.CheckTenantAsync(state, actor, PermissionCatalog.Get(key), AccessScope.ForTenant(tenant), ct).ConfigureAwait(false)).Allowed;
        if (all) return null;
        var result = new List<Guid>();
        foreach (var scope in state.Assignments.Where(a => a.PrincipalId == actor && a.TenantId == tenant && a.Scope.ResourceType == "credential").Select(a => a.Scope).Distinct())
            if (Guid.TryParseExact(scope.ResourceId, "D", out var id))
            {
                var allowed = true;
                foreach (var key in SourcePermissions) allowed &= (await iam.CheckOwnedTenantAsync(state, actor, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false)).Allowed;
                if (allowed) result.Add(id);
            }
        return result;
    }
    public static async ValueTask RequireHumanAsync(TenantSecurityState state, Guid human, Guid account, bool manage, bool poll,
        IamAuthorizer iam, CancellationToken ct)
    {
        var scope = TenantCredentialRules.Scope(state.Tenant.TenantId, account);
        foreach (var key in new[] { "credentials.read", "email.read" }.Concat(manage ? ["credentials.manage"] : []).Concat(poll ? ["credentials.use"] : []))
        {
            var result = await iam.CheckOwnedTenantAsync(state, human, PermissionCatalog.Get(key), scope, ct).ConfigureAwait(false);
            if (!result.Allowed) throw new AccessDeniedException(result.ReasonCode);
        }
    }
    public static async ValueTask RequireExecutorAsync(TenantSecurityState state, Guid executor, Guid account, IamAuthorizer iam, CancellationToken ct)
    {
        foreach (var key in SourcePermissions)
            if (!(await iam.CheckOwnedTenantAsync(state, executor, PermissionCatalog.Get(key), TenantCredentialRules.Scope(state.Tenant.TenantId, account), ct).ConfigureAwait(false)).Allowed)
                throw new AccessDeniedException("email_event_source_read_required");
    }
    public static void Failure(SafeEmailWatchFailure failure)
    {
        if (failure.Code is not ("email_watch_fanout_limit" or "email_watch_host_unavailable" or "email_watch_access_required" or "email_watch_temporary_failure")
            || failure.Permanent != (failure.Code == "email_watch_access_required") || failure.DiagnosticId == Guid.Empty)
            throw new ArgumentException("A normalized source failure is required.");
    }
    public static void Batch(EmailWatchBatch batch)
    {
        if (batch.MessageRefs.Count > 25 || batch.MessageRefs.Any(r => r.Length is < 1 or > 512 || r.Any(char.IsControl))
            || batch.CursorGap && batch.MessageRefs.Count != 0 || !Enum.IsDefined(batch.NextCursor.Kind)
            || batch.NextCursor.Offset < 0 || batch.NextCursor.HistoryId?.Length > 32 || batch.NextCursor.PageToken?.Length > 2048
            || Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(batch.NextCursor)) > 4096)
            throw new ArgumentException("A bounded mailbox cursor and reference batch is required.");
    }
    public static CoreEventOccurrence Fact(Guid tenant, Guid executor, AgentCredentialBinding binding, Credential account,
        EmailWatchState watch, string message, DateTimeOffset now)
    {
        if (watch.TenantId != tenant || tenant == Guid.Empty || executor == Guid.Empty || binding.CredentialId != account.CredentialId
            || watch.AccountId != account.CredentialId || account.Purpose != CredentialPurpose.EmailAccount || account.EmailAccount is null)
            throw new ArgumentException("An actual owned account, watch and binding are required.");
        now = DateTimeOffset.FromUnixTimeMilliseconds(now.ToUnixTimeMilliseconds());
        var key = $"email.message.received:{tenant:N}:{account.CredentialId:N}:{watch.Generation}:{message}:{binding.AgentInstanceId:N}";
        return new(new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0,16)), key,
            TriggerOwner.ForTenant(tenant, binding.AgentInstanceId, executor), "email.message.received", now,
            JsonSerializer.Serialize(new { accountId = account.CredentialId, accountRef = binding.Reference, messageRef = message, provider = account.EmailAccount.Preset.ToString() }), VisitedAutomationIds: []);
    }
    public static void Change(TenantSecurityState next, long expected, Guid account, TenantEmailWatchChange change, AuthorizationAudit audit)
    {
        if (next.Tenant.Revision != checked(expected + 1) || change.CredentialRevision < 1 || change.WatchRevision < 0
            || audit.TenantId != next.Tenant.TenantId || audit.ActorPrincipalId != change.Human.PrincipalId
            || audit.Target != account.ToString("D") || audit.Decision != "allowed"
            || audit.Action != (change.CheckNow ? "email.monitoring.check" : "email.monitoring.configure"))
            throw new ArgumentException("An exact human account-source mutation is required.");
    }
}
