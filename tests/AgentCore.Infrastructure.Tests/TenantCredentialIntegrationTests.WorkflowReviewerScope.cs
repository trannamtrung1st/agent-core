using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Providers.Synthetic;
using AgentCore.Infrastructure.Workflows;
using Microsoft.Extensions.DependencyInjection;
namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Workflows_assigned_reviewer_requires_current_process_source_read_and_decide_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Reviewer scope", "reviewer-scope");
        var human = await SandboxHuman(f, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var account = await f.Identity.RegisterAsync("process-reviewer@example.test", "Reviewer", "strong-reviewer-password");
        var read = new[] { "workflows.read", "workflows.monitor" };
        var permissions = read.Append("workflows.decide").ToArray();
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Process reviewer", permissions, await Revision());
        var invitation = await f.Access.InviteAsync(human.PrincipalId, tenant, account.LoginKey, role.RoleId, await Revision());
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
        var login = (await f.Identity.SignInAsync(account.LoginKey, "strong-reviewer-password"))!.Session;
        var reviewer = new HumanSessionReference(account.AccountId, login.SessionId, user.PrincipalId);
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        var initiator = new TenantSessionInitiator(human.AccountId, human.AuthenticationSessionId, human.PrincipalId);
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, initiator, SessionMode.Text, await Revision(), agent.Instance.Revision);
        await using var vendor = WorkflowRuntimeProvider(f, sqlite);
        if (sqlite) await vendor.MigrateFormalWorkflowRuntimeAsync(); await vendor.PopulateFormalWorkflowActivitiesAsync();
        var engine = vendor.GetRequiredService<IFormalWorkflowRuntime>(); var catalog = Workflows(f, engine); var id = Guid.NewGuid();
        await catalog.SaveAsync(tenant, id, human, new("Assigned process", "", HumanWorkflow(reviewer.PrincipalId), false, 0, await Revision(), "draft"));
        await catalog.PublishAsync(tenant, id, human, new(1, await Revision(), "publish"));
        var bridge = new TenantWorkflowExecutionService(f.Iam, (ITenantWorkflowCatalogStore)f.Iam, (ITenantWorkflowExecutionStore)f.Iam,
            (ITenantWorkflowTaskStore)f.Iam, (ITenantTaskStore)f.Iam, (ITenantSessionStore)f.Iam, (ITenantRunStore)f.Iam, (ITenantSpaceStore)f.Iam,
            catalog, f.RunConfiguration(), new SyntheticTenantModelCatalogReader(f.Iam), engine, f.Identity, f.Authorizer, f.Ids, f.Clock);
        using var input = JsonDocument.Parse("{}");
        var execution = await bridge.StartHumanAsync(tenant, id, human, new(1, input.RootElement, "start", await Revision(), SourceSessionId: session.Owner.SessionId));
        await bridge.ReconcileAsync(4, default);
        var store = (ITenantWorkflowExecutionStore)f.Iam; execution = (await store.FindWorkflowExecutionAsync(tenant, execution.ExecutionId))!;
        var step = Assert.Single(await store.ReadWorkflowStepsAsync(tenant, execution.ExecutionId));
        var command = new WorkflowDecisionCommand(tenant, execution.ExecutionId, "review", reviewer, true, "Exact source review", "review", execution.Revision, step.Revision, await Revision(), Guid.NewGuid());
        await Assert.ThrowsAsync<AccessDeniedException>(() => bridge.DecideHumanAsync(command).AsTask());
        Assert.Null(await store.FindWorkflowDecisionAsync(tenant, execution.ExecutionId, "review"));
        role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, role.RoleId, role.Name, permissions.Append("sessions.read").ToArray(), await Revision());
        Assert.True((await bridge.ReadHumanAsync(tenant, execution.ExecutionId, reviewer)).Steps.Single(s => s.NodeId == "review").CanDecide);
        role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, role.RoleId, role.Name, read.Append("sessions.read").ToArray(), await Revision());
        var revoked = command with { ExpectedSecurityRevision = await Revision() };
        await Assert.ThrowsAsync<AccessDeniedException>(() => bridge.DecideHumanAsync(revoked).AsTask());
        Assert.Null(await store.FindWorkflowDecisionAsync(tenant, execution.ExecutionId, "review"));
        role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, role.RoleId, role.Name, permissions.Append("sessions.read").ToArray(), await Revision());
        await bridge.DecideHumanAsync(command with { ExpectedSecurityRevision = await Revision() });
        for (var pass = 0; pass < 6; pass++) await bridge.ReconcileAsync(4, default);
        Assert.Equal(WorkflowExecutionStatus.Completed, (await store.FindWorkflowExecutionAsync(tenant, execution.ExecutionId))!.Status);
    }
}
