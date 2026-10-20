using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore : ITenantConnectedAccountReadStore
{
    public async ValueTask<bool> TryRecordConnectedAccountReadAsync(TenantConnectedAccountReadProof proof, CancellationToken ct = default)
    {
        TenantConnectedAccountRules.Proof(proof);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var state = await ReadTenantAsync(db, proof.TenantId, ct).ConfigureAwait(false);
        if (state is null || state.Tenant.Revision != proof.SecurityRevision) return false;
        try { await RequireHumanSessionAsync(db, proof.TenantId, proof.Human, ct).ConfigureAwait(false); }
        catch (AccessDeniedException) { return false; }
        var parent = await OwnedAgents(db, proof.TenantId).AsNoTracking().SingleOrDefaultAsync(a => a.Instance.InstanceId == proof.InstanceId.ToString("D"), ct).ConfigureAwait(false);
        var binding = await OwnedCredentialBindings(db, proof.TenantId, proof.InstanceId).AsNoTracking().SingleOrDefaultAsync(b => b.BindingId == proof.BindingId.ToString("D"), ct).ConfigureAwait(false);
        var row = await OwnedCredentials(db, proof.TenantId).Include(c => c.EmailAccount).Include(c => c.DriveAccount).AsNoTracking()
            .SingleOrDefaultAsync(c => c.CredentialId == proof.CredentialId.ToString("D"), ct).ConfigureAwait(false);
        if (parent is null || parent.Instance.Revision != proof.InstanceRevision || binding is null || binding.Revision != proof.BindingRevision
            || binding.CredentialId != proof.CredentialId.ToString("D") || row is null || row.Revision != proof.CredentialRevision || row.SecretRevision != proof.SecretRevision) return false;
        var agent = Owned(parent); var value = SqliteCredentialStore.Map(row); var iam = new IamAuthorizer(this);
        if (value.Purpose != proof.Purpose || value.Status != Domain.Credentials.CredentialStatus.Active
            || agent.Instance.Lifecycle != Domain.Definitions.AgentInstanceLifecycle.Active) return false;
        try { TenantSessionRules.RequireExecutor(state, agent); }
        catch (AccessDeniedException) { return false; }
        if (!await TenantConnectedAccountRules.AgentReadAsync(state, proof.InstanceId, proof.Human.PrincipalId, iam, ct).ConfigureAwait(false)
            || !await TenantConnectedAccountRules.AllowedAsync(state, value, proof.Human.PrincipalId, iam, true, ct).ConfigureAwait(false)) return false;
        var prior = await db.Set<AuthorizationAudit>().AsNoTracking().SingleOrDefaultAsync(a => a.AuditId == proof.Audit.AuditId, ct).ConfigureAwait(false);
        // SQLite persists audit time at millisecond precision; compare the same canonical value on replay.
        if (prior is not null) return prior == proof.Audit with { At = DateTimeOffset.FromUnixTimeMilliseconds(proof.Audit.At.ToUnixTimeMilliseconds()) };
        db.Add(proof.Audit);
        try { await db.SaveChangesAsync(ct).ConfigureAwait(false); await tx.CommitAsync(ct).ConfigureAwait(false); return true; }
        catch (DbUpdateConcurrencyException) { return false; }
        catch (DbUpdateException e) when (IsConstraint(e)) { return false; }
        catch (SqliteException e) when (e.SqliteErrorCode is 5 or 6) { return false; }
    }
}
