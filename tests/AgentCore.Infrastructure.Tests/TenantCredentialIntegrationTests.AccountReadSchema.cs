using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Credentials;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Fact]
    public async Task Account_read_SQLite_guards_freeze_exact_query_identity_and_evidence_and_refuse_lossy_downgrade()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true); var s = await RuntimeCredential(f, true);
        var state = (await f.Iam.ReadTenantAsync(s.Tenant))!;
        var role = await f.Access.SaveRoleAsync(s.Owner.PrincipalId, s.Tenant, null, "Exact source", ["sessions.read", "drive.read"], state.Tenant.Revision);
        await f.Access.AssignRoleAsync(s.Owner.PrincipalId, s.Tenant, s.Agent.PrincipalId, role.RoleId, AgentCore.Domain.Security.AccessScope.ForTenant(s.Tenant), (await f.Iam.ReadTenantAsync(s.Tenant))!.Tenant.Revision);
        var http = new OAuthHttp(_ => throw new InvalidOperationException("No provider I/O is authorized by schema checks."));
        var reads = RuntimeAccountReads(f, http); var approvals = AccountApprovals(f, reads);
        var request = (await approvals.PrepareAccountReadAsync(new(s.Tenant, s.Run.AgentRunId), new(ToolCatalog.DriveList,
            TenantAccountReadContract.Parse(ToolCatalog.DriveList, """{"accountRef":"work-drive","query":"Exact","limit":1}""").Json), s.Run.Revision, s.Run.Claim!.Generation)).Request!;
        await using (var db = f.Contexts!.CreateDbContext())
        {
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Equal(19, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE type='trigger' AND (name LIKE 'TR_TenantApprovals_%' OR name LIKE 'TR_TenantReceipts_%')").SingleAsync());
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson = json_set(PayloadJson, '$.Operation.AccountRead.ArgumentsJson', '{{}}') WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET Kind=1, PayloadJson=json_set(PayloadJson,'$.Kind',1) WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Snapshot.Credential.DisplayName','Changed account') WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE TenantApprovalRequests SET PayloadJson=json_set(PayloadJson,'$.Operation.CanonicalActionJson','{{}}') WHERE RequestId={request.RequestId}"));
            await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync("20261020140000_TenantReportSourceReadReview"));
        }
        // Immutable consent still permits its normal status/revision decision after rejected tampering.
        var view = await approvals.ReadViewAsync(s.Tenant, request.RequestId, s.Owner);
        Assert.Equal(AgentCore.Domain.Security.TenantApprovalStatus.Rejected, (await approvals.DecideAsync(s.Tenant, request.RequestId, s.Owner, false,
            request.Operation.ActionHash, view.SecurityRevision, request.Revision)).Status);
    }
    [Fact]
    public async Task Account_read_SQLite_empty_down_up_preserves_predecessor_guards_and_current_model()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true);
        await using var db = f.Contexts!.CreateDbContext(); var migrations = db.GetService<IMigrator>();
        await migrations.MigrateAsync("20261020140000_TenantReportSourceReadReview");
        Assert.Equal(13, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE type='trigger' AND (name LIKE 'TR_TenantApprovals_%' OR name LIKE 'TR_TenantReceipts_%')").SingleAsync());
        await migrations.MigrateAsync(); Assert.False(db.Database.HasPendingModelChanges());
        Assert.Equal(19, await db.Database.SqlQueryRaw<int>("SELECT count(*) AS Value FROM sqlite_master WHERE type='trigger' AND (name LIKE 'TR_TenantApprovals_%' OR name LIKE 'TR_TenantReceipts_%')").SingleAsync());
    }
}
