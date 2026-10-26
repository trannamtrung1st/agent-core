using System.Text.Json;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task DynamicModels_references_filter_before_paging_and_preserve_retired_pins(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ProviderSetup(f, ModelProviderKind.OpenAI);
        var tenant = setup.Route.TenantId; var human = new TenantSessionInitiator(setup.Human.AccountId, setup.Human.AuthenticationSessionId, setup.Human.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var service = Models(f); var store = (ITenantModelStore)f.Iam;
        var presetSummary = Assert.Single(await store.ReadModelPresetSummariesAsync(), p => p.Preset.PresetId == setup.Offering.PresetId);
        Assert.Equal(1, presetSummary.OfferingReferences);
        await service.SaveDefaultAsync(human, tenant, setup.Offering.OfferingId, "medium", await Revision(), 0);
        var visible = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, visible.PrincipalId);
        var hidden = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        async Task Configure(TenantAgent agent)
        {
            await f.AgentConfiguration().UpdateUnattendedModelAsync(human.PrincipalId, tenant, agent.Instance.InstanceId, await Revision(), agent.Instance.Revision,
                AgentCore.Domain.Models.ModelPreference.Fixed(setup.Offering.OfferingId.ToString("D"), "low"), initiator: human);
        }
        await Configure(visible); await Configure(hidden);
        var session = await f.Sessions.CreateAsync(tenant, visible.Instance.InstanceId, human, SessionMode.Text,
            await Revision(), 2, AgentCore.Domain.Models.ModelPreference.Fixed(setup.Offering.OfferingId.ToString("D"), "high"));
        var runReceipt = await f.RunService().SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(), "Read no provider; retain this admitted pin.", await Revision(), 1);
        var run = (await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, runReceipt.RunId))!;
        var pin = JsonSerializer.Serialize(run.PinnedModel);
        var all = new ModelReferenceVisibility(null, null, null, null);
        foreach (var kind in Enum.GetValues<ModelReferenceKind>())
        {
            var page = await service.ReferencesAsync(human, tenant, setup.Offering.OfferingId, false, kind, pageSize: 1);
            Assert.InRange(page.Items.Count, 0, 1); Assert.Equal(await Revision(), page.SecurityRevision);
            Assert.All(page.Items, item => Assert.Equal(setup.Offering.OfferingId, item.OfferingId));
        }
        Assert.Equal(4, (await service.ReferencesAsync(human, tenant, setup.Offering.OfferingId, false, ModelReferenceKind.Instances)).Total);
        var filter = new ModelReferenceVisibility([visible.Instance.InstanceId], null, null, [setup.Offering.OfferingId]);
        var query = new ModelReferenceQuery(tenant, setup.Offering.OfferingId, null, ModelReferenceKind.Instances, 0, 1, filter);
        var filtered = await store.ReadModelReferencesAsync(query);
        Assert.Equal(2, filtered.Total); Assert.Equal(visible.Instance.InstanceId, Assert.Single(filtered.Items).ResourceId);
        var beyond = await store.ReadModelReferencesAsync(query with { Page = 2 }); Assert.Equal(2, beyond.Total); Assert.Empty(beyond.Items);
        var noAgents = await store.ReadModelReferencesAsync(query with { Visible = filter with { Resources = [] } }); Assert.Equal(0, noAgents.Total);
        var runs = await store.ReadModelReferencesAsync(query with { Kind = ModelReferenceKind.Runs, Visible = all with { Sessions = [] } }); Assert.Equal(0, runs.Total);
        var exactRun = await store.ReadModelReferencesAsync(query with { Kind = ModelReferenceKind.Runs,
            Visible = all with { Resources = [run.AgentRunId], Agents = [visible.Instance.InstanceId], Sessions = [session.Owner.SessionId] } });
        Assert.Equal(1, exactRun.Total); Assert.Equal(run.AgentRunId, Assert.Single(exactRun.Items).ResourceId);
        Assert.Equal(1, (await service.ReferencesAsync(human, tenant, setup.Connection.ConnectionId, true, ModelReferenceKind.Runs)).Total);
        await service.SaveDefaultAsync(human, tenant, null, null, await Revision(), 1);
        await service.SaveOfferingAsync(human, tenant, setup.Offering.OfferingId, new(setup.Offering.ConnectionId, setup.Offering.PresetId, setup.Offering.ExternalModelId,
            setup.Offering.Label, setup.Offering.Transport, setup.Offering.Capabilities, false, true), await Revision(), setup.Offering.Revision);
        Assert.Equal(pin, JsonSerializer.Serialize((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, run.AgentRunId))!.PinnedModel));
        Assert.Equal(2, (await service.ReferencesAsync(human, tenant, setup.Offering.OfferingId, false, ModelReferenceKind.Instances)).Total);
        Assert.Equal(1, (await service.ReferencesAsync(human, tenant, setup.Offering.OfferingId, false, ModelReferenceKind.Runs)).Total);
        if (sqlite) Assert.Equal(2, (await new SqliteIamStore(f.Contexts!).ReadModelReferencesAsync(query with { Visible = all })).Total);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReferencesAsync(human, tenant, Guid.NewGuid(), false, ModelReferenceKind.Instances).AsTask());
        var account = await f.Identity.RegisterAsync("reference-reader@example.test", "Reference reader", "strong-reference-reader-password");
        var membership = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Membership only", ["sessions.create"], await Revision());
        var invitation = await f.Access.InviteAsync(human.PrincipalId, tenant, account.LoginKey, membership.RoleId, await Revision());
        var user = await f.Access.AcceptInvitationAsync(account.AccountId, tenant, invitation.Invitation.InvitationId, invitation.Token);
        var reader = await SandboxHuman(f, user.PrincipalId, "reference-reader@example.test", "strong-reference-reader-password");
        var modelRead = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Model metadata reader", ["models.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, reader.PrincipalId, modelRead.RoleId,
            TenantModelRules.Scope(tenant, setup.Offering.OfferingId), await Revision());
        Assert.Equal(0, (await service.ReferencesAsync(reader, tenant, setup.Offering.OfferingId, false, ModelReferenceKind.Instances)).Total);
        var agentRead = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "One visible Agent", ["agents.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, reader.PrincipalId, agentRead.RoleId,
            AccessScope.ForResource(tenant, "agent", visible.Instance.InstanceId.ToString("D")), await Revision());
        var scoped = await service.ReferencesAsync(reader, tenant, setup.Offering.OfferingId, false, ModelReferenceKind.Instances, pageSize: 1);
        Assert.Equal(1, scoped.Total); Assert.Equal(visible.Instance.InstanceId, Assert.Single(scoped.Items).ResourceId);
        Assert.Equal(0, (await service.ReferencesAsync(reader, tenant, setup.Offering.OfferingId, false, ModelReferenceKind.Runs)).Total);
        var runRead = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "One visible Run", ["agent-runs.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, reader.PrincipalId, runRead.RoleId,
            AccessScope.ForResource(tenant, "agent-run", run.AgentRunId.ToString("D")), await Revision());
        Assert.Equal(0, (await service.ReferencesAsync(reader, tenant, setup.Offering.OfferingId, false, ModelReferenceKind.Runs)).Total);
        var sessionRead = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "One visible Session", ["sessions.read"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, reader.PrincipalId, sessionRead.RoleId,
            AccessScope.ForResource(tenant, "session", session.Owner.SessionId.ToString("D")), await Revision());
        var visibleRun = await service.ReferencesAsync(reader, tenant, setup.Offering.OfferingId, false, ModelReferenceKind.Runs);
        Assert.Equal(1, visibleRun.Total); Assert.Equal(run.AgentRunId, Assert.Single(visibleRun.Items).ResourceId);
        Assert.DoesNotContain("factory-private-key", JsonSerializer.Serialize(scoped));
        await f.Service.UpdateAsync(human.PrincipalId, tenant, setup.Connection.CredentialId!.Value,
            new("Disabled inference key", AgentCore.Domain.Credentials.CredentialStatus.Disabled, null, []), await Revision(), 1, initiator: human);
        var disabledConnection = await service.SaveConnectionAsync(human, tenant, setup.Connection.ConnectionId,
            new(setup.Connection.Kind, setup.Connection.Label, null, setup.Connection.CredentialId, false, false), await Revision(), setup.Connection.Revision);
        Assert.False(disabledConnection.Enabled);
        var retainedSummary = Assert.Single(await store.ReadModelPresetSummariesAsync(), p => p.Preset.PresetId == setup.Offering.PresetId);
        Assert.Equal(1, retainedSummary.OfferingReferences);
        Assert.DoesNotContain(tenant.ToString("D"), JsonSerializer.Serialize(retainedSummary));
        Assert.DoesNotContain("factory-private-key", JsonSerializer.Serialize(retainedSummary));
        var beforeReenable = await Revision();
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SaveConnectionAsync(human, tenant, disabledConnection.ConnectionId,
            new(disabledConnection.Kind, disabledConnection.Label, null, disabledConnection.CredentialId, false, true), beforeReenable, disabledConnection.Revision).AsTask());
        Assert.Equal(beforeReenable, await Revision());
        Assert.Equal(pin, JsonSerializer.Serialize((await ((ITenantRunStore)f.Iam).FindTenantRunAsync(tenant, run.AgentRunId))!.PinnedModel));
        await f.Iam.RevokeSessionAsync(human.AuthenticationSessionId, f.Clock.GetUtcNow());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReferencesAsync(human, tenant, setup.Offering.OfferingId, false, ModelReferenceKind.Instances).AsTask());
    }
}
