using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using Fixture = AgentCore.Infrastructure.Tests.TenantCredentialIntegrationTests.Fixture;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantOperationAuthorizationTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Shared_tool_boundary_uses_actual_executor_roles_and_rechecks_revoke_without_borrowing_requester_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await Setup(f);
        var operations = new TenantOperationAuthorizer(f.Iam, (ITenantRunStore)f.Iam, (ITenantAgentStore)f.Iam, f.Authorizer, new((ITenantAuthorizationAuditStore)f.Iam, f.Ids, f.Clock));
        var tools = new SessionToolExecutor(tenantAuthorization: new((ITenantRunStore)f.Iam, operations));
        var frame = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: setup.Agent.Instance.InstanceId, AgentRunId: setup.Run.AgentRunId,
            OwnedSessionId: setup.Run.SessionId, TenantId: setup.Tenant);
        var args = JsonSerializer.Serialize(new { permission = "agents.read", resourceType = "agent", resourceId = setup.Agent.Instance.InstanceId });
        var call = new ModelToolCall("inspect", ToolCatalog.IamCheckAccess, args);
        using (var result = JsonDocument.Parse((await tools.ExecuteAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId, call, 16_000, admission: frame)).Text))
        {
            Assert.Equal("requiresApproval", result.RootElement.GetProperty("decision").GetString());
            Assert.Equal("exceptionalGrant", result.RootElement.GetProperty("approvalKind").GetString());
        }
        var assignment = await Grant(f, setup, setup.Agent.PrincipalId, "Executor read", ["agents.read"]);
        using (var result = JsonDocument.Parse((await tools.ExecuteAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId, call, 16_000, admission: frame)).Text))
            Assert.Equal("allowed", result.RootElement.GetProperty("decision").GetString());
        var effective = new ModelToolCall("effective", ToolCatalog.IamEffectivePermissions, JsonSerializer.Serialize(new { resourceType = "agent", resourceId = setup.Agent.Instance.InstanceId }));
        using (var result = JsonDocument.Parse((await tools.ExecuteAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId, effective, 16_000, admission: frame)).Text))
            Assert.Equal("agents.read", Assert.Single(result.RootElement.GetProperty("operations").EnumerateArray()).GetProperty("requiredPermission").GetString());
        var witness = (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision;
        await f.Access.RevokeAssignmentAsync(setup.Owner.PrincipalId, setup.Tenant, assignment.AssignmentId, witness);
        using (var result = JsonDocument.Parse((await tools.ExecuteAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId, call, 16_000, admission: frame)).Text))
            Assert.Equal("exceptionalGrant", result.RootElement.GetProperty("approvalKind").GetString());
        using (var result = JsonDocument.Parse((await tools.ExecuteAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId,
            call with { ArgumentsJson = JsonSerializer.Serialize(new { permission = "agents.read", resourceType = "agent", resourceId = setup.Agent.Instance.InstanceId, principalId = setup.Owner.PrincipalId }) }, 16_000, admission: frame)).Text))
            Assert.Equal("invalid", result.RootElement.GetProperty("error").GetString());
        using (var result = JsonDocument.Parse((await tools.ExecuteAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId,
            call with { ArgumentsJson = new string('x', IamToolContracts.MaxArgumentBytes + 1) }, 16_000, admission: frame)).Text))
            Assert.Equal("invalid", result.RootElement.GetProperty("error").GetString());
        using (var result = JsonDocument.Parse((await tools.ExecuteAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId,
            new("legacy", ToolCatalog.CredentialsList, "{}"), 16_000, admission: frame)).Text))
            Assert.Equal("tenant_effect_unavailable", result.RootElement.GetProperty("reasonCode").GetString());
        using (var empty = JsonDocument.Parse("{}"))
        {
            var legacyGrant = new ToolApprovalGrant(Guid.NewGuid(), ToolCatalog.CredentialsList, ToolActionHash.Compute(ToolCatalog.CredentialsList, empty.RootElement),
                setup.Run.ResponseId!.Value, Guid.NewGuid(), Guid.NewGuid());
            using var denied = JsonDocument.Parse((await tools.ExecuteAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId,
                new("legacy-grant", ToolCatalog.CredentialsList, "{}"), 16_000, approvalGrant: legacyGrant, admission: frame)).Text);
            Assert.Equal("tenant_effect_unavailable", denied.RootElement.GetProperty("reasonCode").GetString());
        }
        Assert.Equal(ToolPolicyDecision.Deny, await tools.EvaluateExecutionPolicyAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId, call, default, frame with { TenantId = Guid.NewGuid() }, default));
        Assert.Equal(ToolPolicyDecision.Deny, await tools.EvaluateExecutionPolicyAsync(setup.Run.Admission.Configuration!.Definition, setup.Run.SessionId, call, default, null, default));
        var reserved = await operations.CheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "iam.roles.assign", "agent", setup.Agent.Instance.InstanceId.ToString("D"));
        Assert.Equal("human_only", reserved.ReasonCode); Assert.Equal(OperationDecisionKind.Denied, reserved.Decision);
        var coarse = await operations.CheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "browser.use", "agent", setup.Agent.Instance.InstanceId.ToString("D"));
        Assert.Equal("nondelegatable", coarse.ReasonCode);
        await Grant(f, setup, setup.Agent.PrincipalId, "Executor browser", ["browser.use"]);
        var sensitive = await operations.CheckAsync(new(setup.Tenant, setup.Run.AgentRunId), "browser.use", "agent", setup.Agent.Instance.InstanceId.ToString("D"));
        Assert.Equal(OperationDecisionKind.RequiresApproval, sensitive.Decision); Assert.Equal(ApprovalKind.Review, sensitive.ApprovalKind);
    }

    private sealed record Environment(Guid Tenant, TenantSessionInitiator Owner, TenantAgent Agent, AgentRun Run);
    private static async Task<Environment> Setup(Fixture f)
    {
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var actor = new TenantSessionInitiator(owner.AccountId, login.SessionId, owner.OwnerPrincipalId);
        var agent = await f.Agents.CreateAsync(actor.PrincipalId, owner.TenantId, "examiner", 1, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision);
        var session = await f.Sessions.CreateAsync(owner.TenantId, agent.Instance.InstanceId, actor, SessionMode.Text, (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision, 1);
        var receipt = await f.RunService().SubmitTextAsync(owner.TenantId, session.Owner.SessionId, actor, f.Ids.NewId(), "Inspect access", (await f.Iam.ReadTenantAsync(owner.TenantId))!.Tenant.Revision, 1);
        return new(owner.TenantId, actor, agent, (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(owner.TenantId, receipt.RunId))!);
    }
    private static async Task<RoleAssignment> Grant(Fixture f, Environment setup, Guid principal, string name, string[] permissions)
    {
        var role = await f.Access.SaveRoleAsync(setup.Owner.PrincipalId, setup.Tenant, null, name, permissions, (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
        return await f.Access.AssignRoleAsync(setup.Owner.PrincipalId, setup.Tenant, principal, role.RoleId,
            AccessScope.ForResource(setup.Tenant, "agent", setup.Agent.Instance.InstanceId.ToString("D")), (await f.Iam.ReadTenantAsync(setup.Tenant))!.Tenant.Revision);
    }
}
