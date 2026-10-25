using AgentCore.Application.Agents;
using AgentCore.Application.Context;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Security;
using AgentCore.Domain.Spaces;
using AgentCore.Infrastructure.Providers;
using Microsoft.EntityFrameworkCore;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private sealed class StoreContextControl(ITenantSessionContextStore store, IIdGenerator ids) : ITenantSessionContextControl
    {
        public ValueTask<TenantContextSwitchResult> SwitchContextAsync(TenantContextSwitch request, CancellationToken ct = default) => store.SwitchTenantQuiescentContextAsync(request, ids.NewId(), ct);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_Sessions_capture_distinct_Spaces_and_Environment_epoch_prevents_silent_reactivation(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("context-owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var tenant = owner.TenantId; var actor = owner.OwnerPrincipalId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(actor, tenant, "Context identity", await Revision());
        var published = await f.Service.PublishAsync(actor, tenant, draft.DraftId, await Revision(), draft.Revision);
        var builtIns = new SessionNoBuiltinFallback(); var directory = new TenantAgentService(f.Iam, (ITenantAgentStore)f.Iam, builtIns, f.Authorizer, f.Ids, TimeProvider.System, ownedDefinitions: f.Definitions);
        var agent = await directory.CreateFromPublicationAsync(actor, tenant, Guid.Parse(published.DefinitionId), 1, await Revision());
        var role = await f.Access.SaveRoleAsync(actor, tenant, null, "Context reader", ["spaces.read", "environments.read", "environments.activate", "variables.read", "variables.resolve"], await Revision());
        await f.Access.AssignRoleAsync(actor, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
        var login = (await f.Identity.SignInAsync("context-owner@example.test", "strong-owner-password"))!.Session;
        var human = new TenantSessionInitiator(owner.AccountId, login.SessionId, actor);
        var sessionStore = (ITenantSessionStore)f.Iam; var runStore = (ITenantRunStore)f.Iam; var spaceStore = (ITenantSpaceStore)f.Iam;
        var sessions = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, sessionStore, runStore, f.Definitions, builtIns, f.Identity, f.Authorizer, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam), new(), f.Ids, TimeProvider.System);
        var first = await sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), 1);
        var second = await sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), 1);
        var own = (await spaceStore.FindPrivateSpaceAsync(tenant, agent.Instance.InstanceId))!;
        Assert.Equal(own.SpaceId, first.ContextSelection!.SpaceId); Assert.Null(first.ContextSelection.EnvironmentId);
        Assert.Equal(first.ContextSelection.SpaceId, second.ContextSelection!.SpaceId);
        var config = new TenantRunConfigurationResolver((ITenantAgentStore)f.Iam, f.Definitions, builtIns, new(f.Resources, builtIns, new NoBuiltinKnowledge(), f.Content), (ITenantCredentialBindingStore)f.Iam);
        var spaces = new TenantSpaceService(f.Iam, spaceStore, (ITenantAgentStore)f.Iam, f.Authorizer, config, (ITenantCredentialBindingStore)f.Iam, (ITenantCredentialStore)f.Iam, f.Ids, TimeProvider.System);
        var shared = await spaces.CreateAsync(actor, tenant, f.Ids.NewId(), "Shared review", "", "SPACE_INSTRUCTIONS", await Revision());
        await spaces.SetMemberAsync(actor, tenant, shared.SpaceId, agent.Instance.InstanceId, "Reviewer", "MEMBERSHIP_INSTRUCTIONS", [], [], await Revision(), 1, 0);
        shared = await spaces.ReadAsync(actor, tenant, shared.SpaceId);
        var contexts = new TenantSessionContextService(f.Iam, sessionStore, runStore, (ITenantAgentStore)f.Iam, spaceStore, (ITenantEnvironmentStore)f.Iam, f.Authorizer, f.Identity, new StoreContextControl((ITenantSessionContextStore)f.Iam, f.Ids));
        async Task<TenantContextSwitchResult> Switch(Guid session, Guid space, Guid? environment = null, Guid? transition = null)
        {
            var current = (await sessionStore.FindTenantSessionAsync(tenant, session))!.Snapshot;
            return await contexts.SwitchAsync(tenant, session, human, transition ?? f.Ids.NewId(), space, environment, await Revision(), current.Revision, current.ContextSelection!.Revision);
        }
        var switched = await Switch(first.Owner.SessionId, shared.SpaceId);
        Assert.Equal("applied", switched.State); Assert.Equal(shared.SpaceId, switched.Selection.SpaceId);
        Assert.Equal(own.SpaceId, (await contexts.ReadAsync(tenant, second.Owner.SessionId, human)).Selection.SpaceId);
        var runs = new TenantRunService(f.Iam, sessionStore, runStore, config, f.Identity, f.Authorizer, new AgentCore.Infrastructure.Providers.Synthetic.SyntheticTenantModelCatalogReader(f.Iam), f.Ids, TimeProvider.System);
        async Task<AgentRun> Submit(Guid session)
        {
            var saved = (await sessionStore.FindTenantSessionAsync(tenant, session))!.Snapshot;
            var receipt = await runs.SubmitTextAsync(tenant, session, human, f.Ids.NewId(), "Check context", await Revision(), saved.Revision);
            return (await runStore.FindTenantRunAsync(tenant, receipt.RunId))!;
        }
        var sharedRun = await Submit(first.Owner.SessionId); var privateRun = await Submit(second.Owner.SessionId);
        var beforeRemoval = await Revision();
        var sharedMember = Assert.Single(await spaceStore.ReadMembershipsAsync(tenant, shared.SpaceId));
        var busy = await Assert.ThrowsAsync<SecurityConflictException>(() => spaces.SetMemberAsync(actor, tenant, shared.SpaceId,
            agent.Instance.InstanceId, "", "", [], [], beforeRemoval, shared.Revision, sharedMember.Revision, remove: true).AsTask());
        Assert.Equal("space_membership_has_active_work", busy.ReasonCode);
        Assert.Equal(beforeRemoval, await Revision()); Assert.Equal(sharedMember, Assert.Single(await spaceStore.ReadMembershipsAsync(tenant, shared.SpaceId)));
        var sharedPin = sharedRun.Admission.Configuration!.SpaceContext!;
        Assert.Equal(shared.SpaceId, sharedPin.SpaceId); Assert.Equal(own.SpaceId, sharedPin.PrivateSpaceId);
        Assert.Equal(own.SpaceId, privateRun.Admission.Configuration!.SpaceContext!.SpaceId);
        Assert.NotEqual(sharedRun.Admission.Configuration.ConfigurationHash, privateRun.Admission.Configuration.ConfigurationHash);
        var action = TenantApprovalRules.Prepare(f.Ids.NewId(), agent.Instance.InstanceId, agent.Instance.Persona, agent.Instance.Revision, agent.Instance.PersonaRevision);
        var sharedAction = TenantApprovalRules.ForRun(action, sharedRun);
        var privateAction = TenantApprovalRules.ForRun(action, privateRun);
        Assert.NotEqual(sharedAction.ActionHash, privateAction.ActionHash);
        Assert.NotEqual(sharedAction.OperationFingerprint, privateAction.OperationFingerprint);
        Assert.Equal(sharedPin.Witness(), sharedAction.SpaceContext);
        Assert.Equal(sharedAction, TenantApprovalRules.Rebuild(sharedAction));
        var cannotRebind = Assert.Throws<AccessDeniedException>(() => TenantApprovalRules.ForRun(sharedAction, privateRun));
        Assert.Equal("operation_context_changed", cannotRebind.ReasonCode);
        var request = new PromptContextBuilder().BuildContext(new(sharedRun.Admission.Configuration.Definition, [], "", null, SessionMode.Text, null, false, null, new(Guid.NewGuid(), TriggerKind.UserTurn, "Check context")) { Configuration = sharedRun.Admission.Configuration });
        Assert.Equal("SPACE_INSTRUCTIONS", request.Text(ContextSectionId.SpaceInstructions));
        Assert.Contains("MEMBERSHIP_INSTRUCTIONS", request.Text(ContextSectionId.MembershipInstructions));
        Assert.DoesNotContain(request.Sections, s => s.Id == ContextSectionId.Environment); Assert.Null(request.Environment);
        var envService = new TenantEnvironmentService(f.Iam, spaceStore, (ITenantEnvironmentStore)f.Iam, (ITenantSecretStore)f.Iam, (ITenantCredentialStore)f.Iam, f.Authorizer, f.Identity, f.Ids, TimeProvider.System);
        var profile = await envService.SaveAsync(actor, tenant, shared.SpaceId, f.Ids.NewId(), "Development", "", await Revision(), shared.Revision, 0);
        shared = await spaces.UpdateAsync(actor, tenant, shared.SpaceId, shared.Name, "", shared.Instructions, true, await Revision(), shared.Revision);
        await Switch(first.Owner.SessionId, shared.SpaceId, profile.EnvironmentId);
        var withEnvironment = await Submit(first.Owner.SessionId);
        Assert.Equal(profile.EnvironmentId, withEnvironment.Admission.Configuration!.SpaceContext!.EnvironmentId);
        request = new PromptContextBuilder().BuildContext(new(withEnvironment.Admission.Configuration.Definition, [], "", null, SessionMode.Text, null, false, null, new(Guid.NewGuid(), TriggerKind.UserTurn, "Check context")) { Configuration = withEnvironment.Admission.Configuration });
        Assert.Contains("Development", request.Text(ContextSectionId.Environment)); Assert.NotNull(request.Environment);
        shared = await spaces.UpdateAsync(actor, tenant, shared.SpaceId, shared.Name, "", shared.Instructions, false, await Revision(), shared.Revision);
        shared = await spaces.UpdateAsync(actor, tenant, shared.SpaceId, shared.Name, "", shared.Instructions, true, await Revision(), shared.Revision);
        Assert.Equal(1, shared.EnvironmentSelectionEpoch);
        Assert.Equal("environment_selection_expired", (await contexts.ReadAsync(tenant, first.Owner.SessionId, human)).ReasonCode);
        var expired = await Assert.ThrowsAsync<AccessDeniedException>(() => Submit(first.Owner.SessionId)); Assert.Equal("environment_selection_expired", expired.ReasonCode);
        var transition = f.Ids.NewId(); var applied = await Switch(first.Owner.SessionId, own.SpaceId, transition: transition);
        Assert.Null(applied.Selection.EnvironmentId);
        var replay = await contexts.SwitchAsync(tenant, first.Owner.SessionId, human, transition, own.SpaceId, null, applied.SecurityRevision - 1, applied.SessionRevision - 1, applied.Selection.Revision - 1);
        Assert.Equal(applied.Selection, replay.Selection); Assert.Equal(applied.SecurityRevision, replay.SecurityRevision);
        Assert.Equal(sharedPin, (await runStore.FindTenantRunAsync(tenant, sharedRun.AgentRunId))!.Admission.Configuration!.SpaceContext);
        if (sqlite)
        {
            await using var db = f.Contexts!.CreateDbContext(); Assert.False(db.Database.HasPendingModelChanges());
            var reopened = new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, f.Content);
            Assert.Equal(applied.Selection, (await reopened.FindTenantSessionAsync(tenant, first.Owner.SessionId))!.Snapshot.ContextSelection);
        }
    }
}
