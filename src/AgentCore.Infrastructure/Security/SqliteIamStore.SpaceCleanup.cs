using AgentCore.Application.Ports;
using AgentCore.Application.Workspaces;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Spaces;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Security;

public sealed partial class SqliteIamStore
{
    public async ValueTask<SpaceWorkspaceCleanupPage> ReadDeletedSpaceWorkspacePageAsync(string? cursor, int limit, CancellationToken ct = default)
    {
        SpaceWorkspaceCleanupCursor.Validate(cursor, limit);
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        var after = cursor ?? "";
        var rows = await db.Set<SpaceRecord>().FromSqlInterpolated($"SELECT * FROM Spaces WHERE Kind = {(int)SpaceKind.Shared} AND DeletedAt IS NOT NULL AND lower(TenantId) || '/' || lower(SpaceId) > {after} ORDER BY lower(TenantId), lower(SpaceId) LIMIT {limit + 1}")
            .AsNoTracking().ToArrayAsync(ct).ConfigureAwait(false);
        return new(rows.Take(limit).Select(s => new TenantWorkspaceReference.SpaceHome(s.TenantId, s.SpaceId)).ToArray(),
            rows.Length > limit ? SpaceWorkspaceCleanupCursor.Of(rows[limit - 1].TenantId, rows[limit - 1].SpaceId) : null);
    }
    public async ValueTask<bool> CanPurgeDeletedSpaceWorkspaceAsync(TenantWorkspaceReference.SpaceHome owner, CancellationToken ct = default)
    {
        if (owner.PrivateAlias) return false;
        await using var db = await contexts.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        if (!await db.Set<SpaceRecord>().AnyAsync(s => s.TenantId == owner.TenantId && s.SpaceId == owner.SpaceId && s.Kind == SpaceKind.Shared && s.DeletedAt != null, ct).ConfigureAwait(false)
            || await db.Set<SpaceMembershipRecord>().AnyAsync(s => s.TenantId == owner.TenantId && s.SpaceId == owner.SpaceId, ct).ConfigureAwait(false)) return false;
        if (await HasSpaceWork(db, owner.TenantId, owner.SpaceId, null, ct).ConfigureAwait(false)) return false;
        var tenant = owner.TenantId.ToString("D"); var space = owner.SpaceId.ToString("D");
        // Missing/legacy mount provenance cannot establish which root remains mounted. Retain
        // files conservatively until that generation's existing retirement owner confirms cleanup.
        var blocked = await db.Database.SqlQuery<int>($"SELECT EXISTS(SELECT 1 FROM SandboxSessionLeases WHERE State <> {(int)SandboxLeaseState.Terminated} AND (TenantId IS NULL OR (lower(TenantId) = {tenant} AND (json_type(Json, '$.mountAuthority.workspaceAccess.context') IS NOT 'object' OR lower(json_extract(Json, '$.mountAuthority.workspaceAccess.context.spaceId')) = {space} OR lower(json_extract(Json, '$.mountAuthority.workspaceAccess.context.privateSpaceId')) = {space})))) AS Value")
            .SingleAsync(ct).ConfigureAwait(false);
        return blocked == 0;
    }
}
