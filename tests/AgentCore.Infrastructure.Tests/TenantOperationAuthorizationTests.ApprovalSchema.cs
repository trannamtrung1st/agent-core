using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Domain.Conversation;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Fact]
    public async Task Durable_wait_releases_the_Session_execution_slot_but_two_running_claims_remain_forbidden()
    {
        await using var f = await Fixture.Create(true); var setup = await Setup(f); var service = ApprovalService(f);
        var first = (await service.PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Agent.Instance.Persona with { Name = "First pending" }, 1)).Request!;
        var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, setup.Run.SessionId, setup.Owner, f.Ids.NewId(), "Second independent action",
            (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 2);
        var second = (await service.PreparePersonaAsync(new(setup.Tenant, receipt.RunId), setup.Agent.Instance.Persona with { Name = "Second pending" }, 1)).Request!;
        foreach (var request in new[] { first, second })
            await service.CancelAsync(setup.Tenant, request.RequestId, setup.Owner, request.Operation.ActionHash, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, 1);
        Assert.Equal(2, await service.DispatchPendingAsync());
        foreach (var runId in new[] { setup.Run.AgentRunId, receipt.RunId })
        {
            await using var db = await f.Contexts!.CreateDbContextAsync();
            var row = await db.AgentRuns.SingleAsync(r => r.AgentRunId == runId.ToString("D"));
            var run = AgentRunStoreMapping.ToDomain(row); var at = f.Clock.GetUtcNow();
            AgentRunStoreMapping.Apply(row, run.TakeClaim(f.Ids.NewId(), at, at.AddMinutes(1)));
            if (runId == setup.Run.AgentRunId) await db.SaveChangesAsync();
            else
            {
                var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
                Assert.Equal(2067, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);
            }
        }
    }

    [Fact]
    public async Task Durable_running_wait_requires_a_live_generation_and_resumes_the_same_attempt()
    {
        await using var f = await Fixture.Create(false); var setup = await Setup(f); var at = f.Clock.GetUtcNow(); var generation = f.Ids.NewId();
        var claimed = setup.Run.TakeClaim(generation, at, at.AddMinutes(1)); var requestId = f.Ids.NewId();
        Assert.Throws<AgentRunTransitionException>(() => claimed.WaitForDurableApproval(claimed.Revision, requestId, f.Ids.NewId(), at));
        Assert.Throws<AgentRunTransitionException>(() => claimed.WaitForDurableApproval(claimed.Revision, requestId, generation, at.AddMinutes(1)));
        var waiting = claimed.WaitForDurableApproval(claimed.Revision, requestId, generation, at);
        Assert.Null(waiting.Claim);
        var resumed = waiting.ResumeDurableApproval(waiting.Revision, requestId, at).TakeClaim(f.Ids.NewId(), at, at.AddMinutes(1));
        Assert.Equal(claimed.AttemptCount, resumed.AttemptCount);
        Assert.Throws<AgentRunTransitionException>(() => claimed.BeginApproval(claimed.Revision, generation, f.Ids.NewId(), "legacy", "{}", "hash", "Legacy", at.AddMinutes(1), at));
    }

    [Fact]
    public async Task Approval_schema_rejects_forged_lineage_and_grant_identity_and_detects_prepared_action_corruption()
    {
        await using var f = await Fixture.Create(true); var setup = await Setup(f);
        var request = (await ApprovalService(f).PreparePersonaAsync(new(setup.Tenant, setup.Run.AgentRunId), setup.Agent.Instance.Persona with { Name = "Constraint persona" }, 1)).Request!;
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            db.Add(new TenantOperationGrant(f.Ids.NewId(), request.RequestId, setup.Tenant, setup.Agent.PrincipalId, f.Ids.NewId(),
                request.Operation.ActionHash, "forged-effect", OperationGrantStatus.Available, 1, request.ExpiresAt));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(787, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);
        }
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            var original = await db.Set<TenantApprovalRecord>().AsNoTracking().SingleAsync();
            var forged = JsonSerializer.Deserialize<TenantApprovalRecord>(JsonSerializer.Serialize(original))!;
            forged.RequestId = f.Ids.NewId(); forged.OperationId = f.Ids.NewId(); forged.Fingerprint = new string('0', 64); forged.SessionId = f.Ids.NewId().ToString("D");
            db.Add(forged);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(787, Assert.IsType<SqliteException>(error.InnerException).SqliteExtendedErrorCode);
        }
        Assert.Null(await ((ITenantApprovalStore)f.Iam).FindOperationGrantAsync(setup.Tenant, request.RequestId));
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            var row = await db.Set<TenantApprovalRecord>().SingleAsync();
            row.PayloadJson = row.PayloadJson.Replace("Constraint persona", "Corrupted persona", StringComparison.Ordinal);
            await db.SaveChangesAsync();
        }
        await Assert.ThrowsAsync<InvalidOperationException>(() => ((ITenantApprovalStore)f.Iam).FindApprovalAsync(setup.Tenant, request.RequestId).AsTask());
    }
}
