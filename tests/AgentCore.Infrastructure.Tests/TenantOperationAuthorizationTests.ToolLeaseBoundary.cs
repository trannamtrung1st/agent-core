using System.Text.Json;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using Microsoft.Extensions.Time.Testing;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false, "queued")] [InlineData(true, "queued")]
    [InlineData(false, "missing-generation")] [InlineData(true, "missing-generation")]
    [InlineData(false, "stale-generation")] [InlineData(true, "stale-generation")]
    [InlineData(false, "expired")] [InlineData(true, "expired")]
    [InlineData(false, "cancellation")] [InlineData(true, "cancellation")]
    [InlineData(false, "waiting-approval")] [InlineData(true, "waiting-approval")]
    [InlineData(false, "other-session-run")] [InlineData(true, "other-session-run")]
    [InlineData(false, "other-tenant")] [InlineData(true, "other-tenant")]
    public async Task Registered_model_tools_require_current_claim_before_policy_arguments_or_dispatch(bool sqlite, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f, "general-assistant", 32);
        var storage = (ITenantRuntimeStore)f.Iam; var runs = (ITenantRunStore)f.Iam; var run = setup.Run;
        if (boundary != "queued") run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(1)), f.Ids.NewId());
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: run.AgentInstanceId, AgentRunId: run.AgentRunId,
            OwnedSessionId: run.SessionId, TenantId: setup.Tenant, AgentRunGeneration: run.Claim?.Generation);
        if (boundary == "missing-generation") admission = admission with { AgentRunGeneration = null };
        if (boundary == "stale-generation") admission = admission with { AgentRunGeneration = f.Ids.NewId() };
        if (boundary == "expired") clock.Advance(TimeSpan.FromMinutes(2));
        if (boundary == "cancellation") run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.RequestCancellation(run.Revision, clock.GetUtcNow(), null), f.Ids.NewId());
        if (boundary == "waiting-approval")
        {
            var pending = await ApprovalService(f).PreparePersonaAsync(new(setup.Tenant, run.AgentRunId), setup.Agent.Instance.Persona with { Name = "Exact pending persona" }, run.Revision, run.Claim!.Generation);
            Assert.NotNull(pending.Request); run = (await runs.FindTenantRunAsync(setup.Tenant, run.AgentRunId))!;
            Assert.Equal(AgentRunStatus.WaitingForApproval, run.Status); Assert.Null(run.Claim);
        }
        if (boundary == "other-session-run")
        {
            var session = await f.Sessions.CreateAsync(setup.Tenant, run.AgentInstanceId, setup.Owner, SessionMode.Text,
                (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, setup.Agent.Instance.Revision);
            var receipt = await f.RunService().SubmitTextAsync(setup.Tenant, session.Owner.SessionId, setup.Owner, f.Ids.NewId(), "Separate owned task",
                (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision, session.Revision);
            var other = (await runs.FindTenantRunAsync(setup.Tenant, receipt.RunId))!;
            other = await storage.ApplyTenantRuntimeCommandAsync(other.Owner, other.AgentRunId,
                new AgentRunCommand.Claim(other.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(1)), f.Ids.NewId());
            admission = admission with { AgentRunId = other.AgentRunId, AgentRunGeneration = other.Claim!.Generation };
        }
        if (boundary == "other-tenant")
        {
            var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == setup.Owner.AccountId);
            var other = await f.Access.CreateTenantAsync(system.PrincipalId, setup.Owner.AccountId, "Other tool tenant", "other-tools");
            Assert.NotEqual(setup.Owner.PrincipalId, other.Users.Single().PrincipalId);
            admission = admission with { TenantId = other.Tenant.TenantId };
        }
        var current = (await runs.FindTenantRunAsync(setup.Tenant, run.AgentRunId))!;
        var revision = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        var pendingCount = (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(setup.Tenant, null, null, new(0, 100))).Total;
        var executor = new SessionToolExecutor(tenantAuthorization: new((ITenantRunStore)f.Iam, Operations(f), f.Clock));
        var names = ToolRegistry.All.Select(t => t.Name).Distinct(StringComparer.Ordinal).Append("unregistered.effect").Order(StringComparer.Ordinal).ToArray();
        Assert.Contains(ToolCatalog.IamCheckAccess, names); Assert.Contains(ToolCatalog.AutomationCreate, names);
        foreach (var name in names)
        {
            var call = new ModelToolCall("stale-" + name, name, "{\"approved\":true,\"principalId\":\"forged\",invalid}");
            var policy = await executor.EvaluateExecutionPolicyAsync(run.Admission.Configuration!.Definition, run.SessionId, call, default, admission, default);
            Assert.True(policy == ToolPolicyDecision.Deny, $"{boundary}: {name} admitted stale policy.");
            using var result = JsonDocument.Parse((await executor.ExecuteAsync(run.Admission.Configuration.Definition, run.SessionId, call, 4096, admission: admission)).Text);
            Assert.True(result.RootElement.TryGetProperty("error", out var error) && error.GetString() == "forbidden", $"{boundary}: {name}: {result.RootElement}");
        }
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        Assert.Equal(current.Revision, (await runs.FindTenantRunAsync(setup.Tenant, current.AgentRunId))!.Revision);
        Assert.Equal(pendingCount, (await ((ITenantApprovalStore)f.Iam).ListApprovalsAsync(setup.Tenant, null, null, new(0, 100))).Total);
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Model_IAM_inspection_uses_the_live_renewed_claim_deadline_without_blocking_valid_execution(bool sqlite)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f); var storage = (ITenantRuntimeStore)f.Iam;
        var run = await storage.ApplyTenantRuntimeCommandAsync(setup.Run.Owner, setup.Run.AgentRunId,
            new AgentRunCommand.Claim(setup.Run.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(1)), f.Ids.NewId());
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: run.AgentInstanceId, AgentRunId: run.AgentRunId,
            OwnedSessionId: run.SessionId, TenantId: setup.Tenant, AgentRunGeneration: run.Claim!.Generation);
        var executor = new SessionToolExecutor(tenantAuthorization: new((ITenantRunStore)f.Iam, Operations(f), f.Clock));
        var call = new ModelToolCall("inspect-own-access", ToolCatalog.IamCheckAccess,
            JsonSerializer.Serialize(new { permission = "agents.read", resourceType = "agent", resourceId = run.AgentInstanceId }));
        async Task Allowed()
        {
            Assert.Equal(ToolPolicyDecision.Allow, await executor.EvaluateExecutionPolicyAsync(run.Admission.Configuration!.Definition, run.SessionId, call, default, admission, default));
            using var result = JsonDocument.Parse((await executor.ExecuteAsync(run.Admission.Configuration.Definition, run.SessionId, call, 4096, admission: admission)).Text);
            Assert.False(result.RootElement.TryGetProperty("error", out _)); Assert.True(result.RootElement.TryGetProperty("decision", out _));
        }
        await Allowed(); clock.Advance(TimeSpan.FromSeconds(59));
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Renew(run.Revision, clock.GetUtcNow(), run.Claim!.Generation, clock.GetUtcNow().AddMinutes(3)), f.Ids.NewId());
        clock.Advance(TimeSpan.FromSeconds(2)); await Allowed();
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(ToolPolicyDecision.Deny, await executor.EvaluateExecutionPolicyAsync(run.Admission.Configuration!.Definition, run.SessionId, call, default, admission, default));
        using var denied = JsonDocument.Parse((await executor.ExecuteAsync(run.Admission.Configuration.Definition, run.SessionId, call, 4096, admission: admission)).Text);
        Assert.Equal("forbidden", denied.RootElement.GetProperty("error").GetString());
    }
    private sealed class InspectionLeaseHook(ITenantAuthorizationAuditStore inner, Func<Task> afterInspection) : ITenantAuthorizationAuditStore
    {
        private int used;
        public async ValueTask<bool> TryRecordOperationAuthorizationAsync(AgentCore.Domain.Security.AuthorizationAudit audit, CancellationToken ct = default)
        {
            var saved = await inner.TryRecordOperationAuthorizationAsync(audit, ct);
            if (saved && audit.Execution?.Purpose == AgentCore.Domain.Security.ExecutionAuthorizationPurpose.Inspection && Interlocked.Exchange(ref used, 1) == 0)
                await afterInspection();
            return saved;
        }
        public ValueTask<bool> TryRecordWorkspaceAuthorizationAsync(TenantWorkspaceHumanAuthorization proof, CancellationToken ct = default) => inner.TryRecordWorkspaceAuthorizationAsync(proof, ct);
    }
    [Theory]
    [InlineData(false, false, "expired")] [InlineData(true, false, "expired")]
    [InlineData(false, true, "expired")] [InlineData(true, true, "expired")]
    [InlineData(false, false, "cancellation")] [InlineData(true, false, "cancellation")]
    [InlineData(false, true, "cancellation")] [InlineData(true, true, "cancellation")]
    [InlineData(false, false, "waiting")] [InlineData(true, false, "waiting")]
    [InlineData(false, true, "waiting")] [InlineData(true, true, "waiting")]
    [InlineData(false, false, "reclaimed")] [InlineData(true, false, "reclaimed")]
    [InlineData(false, true, "reclaimed")] [InlineData(true, true, "reclaimed")]
    public async Task IAM_results_cannot_leave_the_shared_tool_boundary_after_lease_loss_during_inspection(bool sqlite, bool effective, string boundary)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var f = await Fixture.Create(sqlite, clock); var setup = await Setup(f);
        var runtime = (ITenantRuntimeStore)f.Iam;
        var run = await runtime.ApplyTenantRuntimeCommandAsync(setup.Run.Owner, setup.Run.AgentRunId,
            new AgentRunCommand.Claim(setup.Run.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(1)), f.Ids.NewId());
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: run.AgentInstanceId, AgentRunId: run.AgentRunId,
            OwnedSessionId: run.SessionId, TenantId: setup.Tenant, AgentRunGeneration: run.Claim!.Generation);
        async Task Change()
        {
            if (boundary is "expired" or "reclaimed") clock.Advance(TimeSpan.FromMinutes(2));
            if (boundary == "cancellation") run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
                new AgentRunCommand.RequestCancellation(run.Revision, clock.GetUtcNow(), null), f.Ids.NewId());
            if (boundary == "waiting") Assert.NotNull((await ApprovalService(f).PreparePersonaAsync(new(setup.Tenant, run.AgentRunId),
                setup.Agent.Instance.Persona with { Name = "Wait during inspection" }, run.Revision, run.Claim!.Generation)).Request);
            if (boundary == "reclaimed")
            {
                run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Recover(run.Revision, clock.GetUtcNow()), f.Ids.NewId());
                run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
                    new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(1)), f.Ids.NewId());
            }
        }
        var operations = new TenantOperationAuthorizer(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer,
            new(new InspectionLeaseHook((ITenantAuthorizationAuditStore)f.Iam, Change), f.Ids, clock));
        var executor = new SessionToolExecutor(tenantAuthorization: new((ITenantRunStore)f.Iam, operations, clock));
        var call = new ModelToolCall("inspection-race", effective ? ToolCatalog.IamEffectivePermissions : ToolCatalog.IamCheckAccess,
            effective ? JsonSerializer.Serialize(new { resourceType = "agent", resourceId = run.AgentInstanceId })
                : JsonSerializer.Serialize(new { permission = "agents.read", resourceType = "agent", resourceId = run.AgentInstanceId }));
        using var result = JsonDocument.Parse((await executor.ExecuteAsync(run.Admission.Configuration!.Definition, run.SessionId, call, 16000, admission: admission)).Text);
        Assert.Equal("forbidden", result.RootElement.GetProperty("error").GetString());
        Assert.False(result.RootElement.TryGetProperty("decision", out _)); Assert.False(result.RootElement.TryGetProperty("operations", out _));
        if (boundary == "reclaimed")
        {
            Assert.NotEqual(admission.AgentRunGeneration, run.Claim!.Generation);
            using var current = JsonDocument.Parse((await executor.ExecuteAsync(run.Admission.Configuration.Definition, run.SessionId, call, 16000,
                admission: admission with { AgentRunGeneration = run.Claim.Generation })).Text);
            Assert.False(current.RootElement.TryGetProperty("error", out _));
        }
        Assert.Contains(await f.Iam.ReadAuditAsync(setup.Tenant, 100), a => a.Execution?.Purpose == AgentCore.Domain.Security.ExecutionAuthorizationPurpose.ToolBoundary && a.Decision == "denied");
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Runtime_owned_commands_cannot_borrow_IAM_inspection_through_the_generic_tool_adapter(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f, "general-assistant", 32);
        var runtime = (ITenantRuntimeStore)f.Iam;
        var run = await runtime.ApplyTenantRuntimeCommandAsync(setup.Run.Owner, setup.Run.AgentRunId,
            new AgentRunCommand.Claim(setup.Run.Revision, f.Clock.GetUtcNow(), f.Ids.NewId(), f.Clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        var executor = new SessionToolExecutor(tenantAuthorization: new((ITenantRunStore)f.Iam, Operations(f), f.Clock));
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: run.AgentInstanceId, AgentRunId: run.AgentRunId,
            OwnedSessionId: run.SessionId, TenantId: setup.Tenant, AgentRunGeneration: run.Claim!.Generation);
        foreach (var name in new[] { ToolCatalog.BackgroundStart, ToolCatalog.ExecutionWait })
        {
            using var result = JsonDocument.Parse((await executor.ExecuteAsync(run.Admission.Configuration!.Definition, run.SessionId,
                new("wrong-owner", name, JsonSerializer.Serialize(new { resourceType = "agent", resourceId = run.AgentInstanceId })), 4096, admission: admission)).Text);
            Assert.Equal("forbidden", result.RootElement.GetProperty("error").GetString()); Assert.False(result.RootElement.TryGetProperty("operations", out _));
        }
        Assert.Equal(run.Revision, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(setup.Tenant, run.AgentRunId))!.Revision);
    }
}
