using AgentCore.Application.Security;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Data.Sqlite;
using AgentCore.Infrastructure.Security;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_run_authorization_persists_the_review_and_current_executor_decisions_without_changing_security_or_run(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var reference = new TenantRunReference(setup.Tenant, setup.Run.AgentRunId); var operations = Operations(f);
        var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var review = await operations.CheckAsync(reference, "agents.read", "agent", setup.Agent.Instance.InstanceId.ToString("D"));
        Assert.Equal(OperationDecisionKind.RequiresApproval, review.Decision);
        var recorded = Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "authorization.operation");
        Assert.Equal(setup.Agent.PrincipalId, recorded.ActorPrincipalId); Assert.Equal("requiresApproval", recorded.Decision);
        Assert.Equal(review.ReasonCode, recorded.ReasonCode);
        var execution = Assert.IsType<ExecutionAuthorizationAudit>(recorded.Execution);
        Assert.Equal(setup.Run.Admission.ExecutionContext, execution.Context); Assert.Equal(setup.Run.Revision, execution.RunRevision);
        Assert.Equal(revision, execution.SecurityRevision); Assert.Null(execution.ClaimGeneration);
        Assert.Equal("agents.read", execution.Permission); Assert.Equal(ApprovalKind.ExceptionalGrant, execution.ApprovalKind);
        Assert.Equal(ExecutionAuthorizationPurpose.Inspection, execution.Purpose);
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Equal(setup.Run.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, setup.Run.AgentRunId))!.Revision);
        var role = await Grant(f, setup, setup.Agent.PrincipalId, "Own read", ["agents.read"]);
        Assert.Equal(OperationDecisionKind.Allowed, (await operations.CheckAsync(reference, "agents.read", "agent", setup.Agent.Instance.InstanceId.ToString("D"))).Decision);
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, role.AssignmentId, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Equal(OperationDecisionKind.RequiresApproval, (await operations.CheckAsync(reference, "agents.read", "agent", setup.Agent.Instance.InstanceId.ToString("D"))).Decision);
        Assert.Equal(OperationDecisionKind.Denied, (await operations.CheckAsync(reference, "iam.roles.assign", "agent", setup.Agent.Instance.InstanceId.ToString("D"))).Decision);
        IIamStore persisted = sqlite ? new SqliteIamStore(f.Contexts!, clock: f.Clock) : f.Iam;
        var events = (await persisted.ReadAuditAsync(setup.Tenant, 200)).Where(a => a.Action == "authorization.operation").ToArray();
        Assert.Equal(4, events.Length); Assert.Contains(events, a => a.Decision == "allowed" && a.Execution!.ApprovalKind is null);
        Assert.Contains(events, a => a.Decision == "denied" && a.ReasonCode == "human_only");
        Assert.All(events, a => Assert.Equal(setup.Run.Admission.ExecutionContext, a.Execution!.Context));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Execution_authorization_audit_never_copies_unknown_permission_or_malformed_resource_payloads(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var operations = Operations(f);
        var reference = new TenantRunReference(setup.Tenant, setup.Run.AgentRunId);
        const string secret = "untrusted-secret-bearing-input";
        var unknown = await operations.CheckAsync(reference, secret, "agent", setup.Agent.Instance.InstanceId.ToString("D"));
        Assert.Equal("unknown_permission", unknown.ReasonCode);
        var invalid = await operations.CheckAsync(reference, "agents.read", secret, "https://example.invalid/?token=" + secret);
        Assert.Equal("resource_unavailable", invalid.ReasonCode);
        var events = (await f.Iam.ReadAuditAsync(setup.Tenant, 200)).Where(a => a.Action == "authorization.operation").ToArray();
        Assert.Equal(2, events.Length); Assert.DoesNotContain(secret, JsonSerializer.Serialize(events));
        Assert.Null(Assert.Single(events, a => a.ReasonCode == "unknown_permission").Execution!.Permission);
        Assert.Equal(AccessScope.ForTenant(setup.Tenant), Assert.Single(events, a => a.ReasonCode == "resource_unavailable").Execution!.Resource);
    }

    [Theory]
    [InlineData(false, "run-revision")] [InlineData(true, "run-revision")]
    [InlineData(false, "security-revision")] [InlineData(true, "security-revision")]
    [InlineData(false, "generation")] [InlineData(true, "generation")]
    [InlineData(false, "lineage")] [InlineData(true, "lineage")]
    [InlineData(false, "actor")] [InlineData(true, "actor")]
    [InlineData(false, "requester")] [InlineData(true, "requester")]
    [InlineData(false, "tenant")] [InlineData(true, "tenant")]
    public async Task Execution_audit_store_fences_untrusted_lineage_and_stale_witnesses_without_mutating_the_actual_run(bool sqlite, string boundary)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        await Operations(f).CheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "agents.read", "agent", setup.Agent.Instance.InstanceId.ToString("D"));
        var before = Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Execution is not null); var e = before.Execution!; var c = e.Context;
        var tenant = boundary == "tenant" ? f.Ids.NewId() : c.TenantId;
        var agent = boundary == "actor" ? f.Ids.NewId() : c.AgentPrincipalId;
        var activation = boundary == "lineage" ? f.Ids.NewId() : c.ActivationId;
        var context = new TrustedExecutionContext(tenant, agent, boundary == "requester" ? f.Ids.NewId() : c.OriginalInitiatorPrincipalId,
            c.SessionId, c.RunId, activation, activation, null, null, c.Origin);
        var changed = before with { AuditId = f.Ids.NewId(), TenantId = tenant, ActorPrincipalId = agent, Execution = e with { Context = context, Resource = e.Resource with { TenantId = tenant },
            RunRevision = boundary == "run-revision" ? e.RunRevision + 1 : e.RunRevision,
            SecurityRevision = boundary == "security-revision" ? e.SecurityRevision + 1 : e.SecurityRevision,
            ClaimGeneration = boundary == "generation" ? f.Ids.NewId() : e.ClaimGeneration } };
        Assert.False(await ((ITenantAuthorizationAuditStore)f.Iam).TryRecordOperationAuthorizationAsync(changed));
        Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Execution is not null);
        Assert.Equal(e.SecurityRevision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Equal(e.RunRevision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, c.RunId))!.Revision);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Execution_audit_replay_is_idempotent_immutable_and_never_reuses_an_old_decision_after_security_changes(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var port = (ITenantAuthorizationAuditStore)f.Iam;
        await Operations(f).CheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "agents.read", "agent", setup.Agent.Instance.InstanceId.ToString("D"));
        var before = Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Execution is not null);
        Assert.True(await port.TryRecordOperationAuthorizationAsync(before));
        Assert.False(await port.TryRecordOperationAuthorizationAsync(before with { ReasonCode = "different_reason" }));
        await Grant(f, setup, setup.Agent.PrincipalId, "Changed security", ["agents.read"]);
        Assert.False(await port.TryRecordOperationAuthorizationAsync(before));
        Assert.Equal(before, Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Execution is not null));
    }

    [Fact]
    public async Task Audit_context_migration_preserves_legacy_rows_and_rejects_invalid_or_oversized_json()
    {
        await using var f = await Fixture.Create(true); var setup = await Setup(f);
        var legacy = await f.Iam.ReadAuditAsync(setup.Tenant, 200);
        await using (var db = await f.Contexts!.CreateDbContextAsync())
        {
            var migrator = db.GetService<IMigrator>();
            await migrator.MigrateAsync("20261017052609_TenantDirectLocalOperationReceipts");
            await migrator.MigrateAsync();
        }
        Assert.Equal(legacy, await f.Iam.ReadAuditAsync(setup.Tenant, 200));
        Assert.All(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => Assert.Null(a.Execution));
        await Operations(f).CheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "agents.read", "agent", setup.Agent.Instance.InstanceId.ToString("D"));
        var recorded = Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Execution is not null);
        await using (var db = await f.Contexts!.CreateDbContextAsync())
            foreach (var invalid in new[] { "{", JsonSerializer.Serialize(new string('x', 4096)) })
            {
                var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE AuthorizationAudit SET ExecutionJson = {invalid} WHERE AuditId = {recorded.AuditId}"));
                Assert.Equal(19, error.SqliteErrorCode);
            }
        Assert.Equal(recorded, Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Execution is not null));
    }


    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Empty_permission_inventory_and_unavailable_executor_keep_safe_inspection_denial_evidence(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f); var operations = Operations(f);
        var reference = new TenantRunReference(setup.Tenant, setup.Run.AgentRunId);
        Assert.Empty(await operations.EffectiveAsync(reference, "agent", setup.Agent.Instance.InstanceId.ToString("D")));
        var inventory = Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Execution is not null);
        Assert.Equal("effective_permissions_inspected", inventory.ReasonCode); Assert.Null(inventory.Execution!.Permission);
        await f.Agents.SetLifecycleAsync(setup.Owner.PrincipalId, setup.Tenant, setup.Agent.Instance.InstanceId,
            AgentCore.Domain.Definitions.AgentInstanceLifecycle.Archived, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, setup.Agent.Instance.Revision);
        var error = await Assert.ThrowsAsync<AccessDeniedException>(() => operations.CheckAsync(reference, "agents.read", "agent", setup.Agent.Instance.InstanceId.ToString("D")).AsTask());
        Assert.Equal("executor_unavailable", error.ReasonCode);
        var denied = Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Action == "authorization.operation" && a.Decision == "denied");
        Assert.Equal(error.ReasonCode, denied.ReasonCode); Assert.Equal(setup.Run.Admission.ExecutionContext, denied.Execution!.Context);
    }


    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unsupported_tenant_credential_tool_is_audited_without_copying_model_arguments_or_reading_a_secret(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var tools = new SessionToolExecutor(tenantAuthorization: new((ITenantRunStore)f.Iam, Operations(f)));
        var frame = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: setup.Agent.Instance.InstanceId,
            AgentRunId: setup.Run.AgentRunId, OwnedSessionId: setup.Run.SessionId, TenantId: setup.Tenant);
        const string secret = "untrusted-credential-content";
        var result = await tools.ExecuteAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId,
            new("blocked-use", ToolCatalog.CredentialsList, secret), 16000, admission: frame);
        using var body = JsonDocument.Parse(result.Text); Assert.Equal("tenant_effect_unavailable", body.RootElement.GetProperty("reasonCode").GetString());
        var entry = Assert.Single(await f.Iam.ReadAuditAsync(setup.Tenant, 200), a => a.Execution is not null);
        Assert.Equal("denied", entry.Decision); Assert.Equal("tenant_effect_unavailable", entry.ReasonCode);
        Assert.Equal(ExecutionAuthorizationPurpose.ToolBoundary, entry.Execution!.Purpose); Assert.Null(entry.Execution.Permission);
        Assert.Equal(setup.Run.Admission.ExecutionContext, entry.Execution.Context); Assert.DoesNotContain(secret, JsonSerializer.Serialize(entry));
    }

}
