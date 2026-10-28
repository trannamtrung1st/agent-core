using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Application.Workflows;
using AgentCore.Domain.Workflows;
using AgentCore.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    private static TenantWorkflowCatalogService Workflows(Fixture f, IFormalWorkflowRuntime? runtime = null) => new(f.Iam, (ITenantWorkflowCatalogStore)f.Iam,
        (ITenantAgentStore)f.Iam, (ITenantSpaceStore)f.Iam, (ITenantEnvironmentStore)f.Iam, (ITenantEvaluationStore)f.Iam,
        f.Authorizer, f.Identity, f.Ids, f.Clock, runtime);
    private static WorkflowGraph HumanWorkflow(Guid reviewer) => new(1, [
        new("start", "Start", WorkflowNodeKind.Start),
        new("review", "Process sign-off", WorkflowNodeKind.HumanDecision, WorkflowRequirement.Global,
            Decision: new(reviewer, "Accept this process outcome?")),
        new("done", "Workflow completed", WorkflowNodeKind.EndSuccess),
        new("refused", "Process rejected", WorkflowNodeKind.EndFailure)
    ], [new("a", "start", "review"), new("b", "review", "done", "approve"), new("c", "review", "refused", "reject")],
        [], [WorkflowInvocationKind.HumanManual], []);

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Workflows_catalog_real_human_publish_pin_replay_stale_and_reopen(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Workflows", "workflows");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new HumanSessionReference(owner.AccountId, login.SessionId, owner.OwnerPrincipalId); var tenant = owner.TenantId;
        var service = Workflows(f); var id = Guid.NewGuid();
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var create = new WorkflowDraftWrite("Reviewed process", "A bounded human process", HumanWorkflow(human.PrincipalId), false, 0, await Revision(), "create-process");
        var created = await service.SaveAsync(tenant, id, human, create);
        Assert.Equal(1, created.Definition.Revision); Assert.Equal(created, await service.SaveAsync(tenant, id, human, create));
        Assert.Equal(created, await service.RecoverAsync(tenant, human, create.RequestKey));
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.SaveAsync(tenant, id, human, create with { Name = "Conflicting name" }).AsTask());
        Assert.Empty((await service.ValidateAsync(tenant, id, human)).Diagnostics);
        var publish = new WorkflowPublish(1, await Revision(), "publish-once");
        var published = await service.PublishAsync(tenant, id, human, publish);
        Assert.Equal(1, published.Version!.Version); Assert.Equal(published, await service.PublishAsync(tenant, id, human, publish));
        var updated = await service.SaveAsync(tenant, id, human, create with { Name = "New draft", ExpectedRevision = 2, ExpectedSecurityRevision = await Revision(), RequestKey = "edit-draft" });
        Assert.Equal(1, updated.Definition.PublishedVersion); Assert.Equal(published.Version, await service.VersionAsync(tenant, id, 1, human));
        var staleSecurity = await Revision();
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.SaveAsync(tenant, id, human, create with { ExpectedRevision = 2, ExpectedSecurityRevision = staleSecurity, RequestKey = "stale-draft" }).AsTask());
        var retired = await service.SaveAsync(tenant, id, human, create with { Retired = true, ExpectedRevision = 3, ExpectedSecurityRevision = await Revision(), RequestKey = "retire-process" });
        Assert.True(retired.Definition.Retired);
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.PublishAsync(tenant, id, human, new(4, retired.SecurityRevision, "publish-retired")).AsTask());
        Assert.Equal(1, (await service.ListAsync(tenant, human, 0, 20)).Total);
        if (sqlite) {
            var reopened = (ITenantWorkflowCatalogStore)new SqliteIamStore(f.Contexts!, clock: f.Clock);
            Assert.Equal(retired.Definition, await reopened.FindWorkflowAsync(tenant, id));
            Assert.Equal(published.Version, await reopened.FindWorkflowVersionAsync(tenant, id, 1));
            Assert.Equal(published, await reopened.FindWorkflowCatalogRequestAsync(tenant, human.PrincipalId, publish.RequestKey));
            await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
        }
        await f.Iam.RevokeSessionAsync(login.SessionId, f.Clock.GetUtcNow());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.RecoverAsync(tenant, human, publish.RequestKey).AsTask());
    }

    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task Workflows_catalog_scoped_visibility_and_publish_permission_are_independent(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Workflows", "workflow-scope");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!.Session;
        var human = new HumanSessionReference(owner.AccountId, login.SessionId, owner.OwnerPrincipalId); var tenant = owner.TenantId; var service = Workflows(f);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var id = Guid.NewGuid(); await service.SaveAsync(tenant, id, human, new("Private process", "", HumanWorkflow(human.PrincipalId), false, 0, await Revision(), "private"));
        var account = await f.Identity.RegisterAsync("workflow-author@example.test", "Author", "strong-author-password");
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Workflow author", ["workflows.create", "workflows.update", "workflows.start"], await Revision());
        var invitation = await f.Access.InviteAsync(human.PrincipalId, tenant, account.LoginKey, role.RoleId, await Revision());
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
        var signIn = (await f.Identity.SignInAsync(account.LoginKey, "strong-author-password"))!.Session;
        var author = new HumanSessionReference(account.AccountId, signIn.SessionId, user.PrincipalId);
        Assert.Equal(0, (await service.ListAsync(tenant, author, 0, 20)).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(tenant, id, author).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.PublishAsync(tenant, id, author, new(1, 0, "unauthorized-publish")).AsTask());
        Assert.Null(await service.RecoverAsync(tenant, author, "private"));
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(Guid.NewGuid(), id, human).AsTask());
        var bypass = HumanWorkflow(human.PrincipalId); bypass = bypass with { Edges = bypass.Edges.Select(e => e.Id == "a" ? e with { To = "done" } : e).ToArray() };
        await service.SaveAsync(tenant, id, human, new("Invalid draft", "", bypass, false, 1, await Revision(), "bad-draft"));
        Assert.Contains((await service.ValidateAsync(tenant, id, human)).Diagnostics, d => d.Code == "required_bypass");
        var security = await Revision(); await Assert.ThrowsAsync<ArgumentException>(() => service.PublishAsync(tenant, id, human, new(2, security, "bad-publish")).AsTask());
        Assert.Null(await ((ITenantWorkflowCatalogStore)f.Iam).FindWorkflowVersionAsync(tenant, id, 1));
    }
}
