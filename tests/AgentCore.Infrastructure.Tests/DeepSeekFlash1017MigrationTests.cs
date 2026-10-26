using AgentCore.Application.Execution;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Models;
using AgentCore.Domain.Security;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Security;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    private const string FlashPredecessor = "20261026080000_ChatRetention";
    private const string FlashSnapshot = "deepseek/deepseek-v4.1-flash-1017";

    [Fact]
    public async Task DeepSeekFlash1017_upgrade_preserves_owned_references_and_history_then_dispatches_only_the_new_route()
    {
        await using var f = await Fixture.Create(true, builtInAuthority: true);
        await using var db = f.Contexts!.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync(FlashPredecessor);
        var store = (ITenantModelStore)f.Iam;
        var oldPreset = (await store.ReadModelPresetsAsync()).Single(p => p.Kind == ModelProviderKind.OpenRouter);
        var setup = await ProviderSetup(f, ModelProviderKind.OpenRouter, oldPreset);
        var tenant = setup.Route.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var settings = await Models(f).SaveDefaultAsync(setup.Human, tenant, setup.Offering.OfferingId, "low", await Revision(), 0);
        // A second connection exercises a custom, retired offering without a preset link.
        var customConnection = await Models(f).SaveConnectionAsync(setup.Human, tenant, null,
            new(ModelProviderKind.OpenRouter, "Custom connection", null, setup.Connection.CredentialId, false, true), await Revision(), 0);
        var custom = await Models(f).SaveOfferingAsync(setup.Human, tenant, null,
            new(customConnection.ConnectionId, null, oldPreset.ExternalModelId, "Custom label", oldPreset.Transport, oldPreset.Capabilities, false, true), await Revision(), 0);
        var human = new TenantSessionInitiator(setup.Human.AccountId, setup.Human.AuthenticationSessionId, setup.Human.PrincipalId);
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var session = await f.Sessions.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var receipt = await f.RunService().SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(), "Keep this historical turn", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam;
        var run = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        Assert.Equal(setup.Route, run.PinnedModel.Binding!.Route);

        var busy = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
        Assert.Contains("DeepSeekFlash1017RequiresSettledRuns", busy.Message);
        Assert.Equal(FlashPredecessor, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equivalent(oldPreset, (await store.ReadModelPresetsAsync()).Single(p => p.Kind == ModelProviderKind.OpenRouter));
        var storage = (ITenantRuntimeStore)f.Iam;
        run = await storage.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.RequestCancellation(run.Revision, f.Clock.GetUtcNow(), null), f.Ids.NewId());
        Assert.Equal(AgentRunStatus.Cancelled, run.Status);
        var retainedRun = (await db.Set<AgentRunRecord>().AsNoTracking().SingleAsync(r => r.AgentRunId == run.AgentRunId.ToString("D"))).PayloadJson;
        var retainedSession = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, session.Owner.SessionId))!.Snapshot;
        var revision = await Revision();

        await db.Database.MigrateAsync();
        var migrated = await store.ReadTenantModelsAsync(tenant);
        var offering = migrated.Offerings.Single(o => o.OfferingId == setup.Offering.OfferingId);
        Assert.Equivalent(setup.Offering with { ExternalModelId = FlashSnapshot, Revision = setup.Offering.Revision + 1 }, offering);
        Assert.Equivalent(custom with { ExternalModelId = FlashSnapshot, Revision = custom.Revision + 1 }, migrated.Offerings.Single(o => o.OfferingId == custom.OfferingId));
        Assert.Equal(settings, migrated.Settings); Assert.Equal(revision + 1, await Revision());
        Assert.Equal(setup.Connection, migrated.Connections.Single(c => c.ConnectionId == setup.Connection.ConnectionId));
        var preset = (await store.ReadModelPresetsAsync()).Single(p => p.PresetId == oldPreset.PresetId);
        Assert.Equivalent(oldPreset with { ExternalModelId = FlashSnapshot, Label = "DeepSeek V4.1 Flash 1017", Revision = oldPreset.Revision + 1 }, preset);
        Assert.Equal(retainedRun, (await db.Set<AgentRunRecord>().AsNoTracking().SingleAsync(r => r.AgentRunId == run.AgentRunId.ToString("D"))).PayloadJson);
        var historical = (await ((ITenantSessionStore)f.Iam).FindTenantSessionAsync(tenant, session.Owner.SessionId))!.Snapshot;
        Assert.Equal(retainedSession.ModelSelection, historical.ModelSelection);
        Assert.Equal(retainedSession.Entries.Select(e => e.Text), historical.Entries.Select(e => e.Text));
        Assert.Equal(setup.Route, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.PinnedModel.Binding!.Route);

        var reader = new TenantModelCatalogReader(store, f.Iam);
        var selection = SessionModelBinder.PinDefault(await reader.ReadAsync((await f.Iam.ReadTenantAsync(tenant))!));
        Assert.Equivalent(setup.Offering.OfferingId.ToString("D"), selection.OfferingId);
        Assert.Equal(FlashSnapshot, selection.ExternalModelId); Assert.Equal("low", selection.ReasoningEffort);
        var route = selection.Binding!.Route;
        using var http = new ProviderHttpFixture(FinalWire(route));
        var factory = ProviderFactory(f, http);
        var stale = await ProviderEvents(factory.Create(setup.Route, setup.Offering.Capabilities, Invocation(setup.Route), ModelPurpose.Conversation), ProviderRequest());
        Assert.Single(stale.OfType<ModelFailed>()); Assert.Empty(http.Requests);
        var result = await ProviderEvents(factory.Create(route, offering.Capabilities, Invocation(route), ModelPurpose.Conversation), ProviderRequest() with { ReasoningEffort = selection.ReasoningEffort });
        Assert.Equal("Done ☃", Assert.Single(result.OfType<ModelSemanticResponseReady>()).Response.DisplayText);
        Assert.Equal(FlashSnapshot, Assert.Single(http.Requests).Body.GetProperty("model").GetString());

        await db.Database.MigrateAsync();
        Assert.Equivalent(offering, (await new SqliteIamStore(f.Contexts!).ReadTenantModelsAsync(tenant)).Offerings.Single(o => o.OfferingId == offering.OfferingId));
        Assert.Equal(revision + 1, await Revision()); Assert.False(db.Database.HasPendingModelChanges());
        var identity = await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE ModelOfferings SET ExternalModelId='forbidden'"));
        Assert.Contains("identity and retirement are immutable", identity.Message);
        var downgrade = await Assert.ThrowsAsync<SqliteException>(() => db.GetService<IMigrator>().MigrateAsync(FlashPredecessor));
        Assert.Contains("DeepSeekFlash1017DowngradeRequiresBackupRestore", downgrade.Message);
        Assert.Equal("20261026090000_DeepSeekFlash1017", (await db.Database.GetAppliedMigrationsAsync()).Last());
        await db.Database.OpenConnectionAsync();
        await using var integrity = db.Database.GetDbConnection().CreateCommand();
        integrity.CommandText = "SELECT count(*) FROM pragma_foreign_key_check";
        Assert.Equal(0L, await integrity.ExecuteScalarAsync());

        var next = await f.RunService().SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(),
            "Continue with the upgraded offering", await Revision(), historical.Revision);
        var admitted = (await runs.FindTenantRunAsync(tenant, next.RunId))!;
        Assert.Equal(route, admitted.PinnedModel.Binding!.Route);
        Assert.Equal(setup.Route, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.PinnedModel.Binding!.Route);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DeepSeekFlash1017_duplicate_targets_refuse_atomically_without_merging_identity(bool presetCollision)
    {
        await using var f = await Fixture.Create(true);
        await using var db = f.Contexts!.CreateDbContext();
        await db.GetService<IMigrator>().MigrateAsync(FlashPredecessor);
        var store = (ITenantModelStore)f.Iam;
        var oldPreset = (await store.ReadModelPresetsAsync()).Single(p => p.Kind == ModelProviderKind.OpenRouter);
        var setup = await ProviderSetup(f, ModelProviderKind.OpenRouter, oldPreset);
        if (presetCollision)
        {
            var row = await db.Set<ModelPresetRecord>().AsNoTracking().SingleAsync(p => p.PresetId == oldPreset.PresetId);
            db.Add(row with { PresetId = Guid.NewGuid(), ExternalModelId = FlashSnapshot });
            await db.SaveChangesAsync();
        }
        else
            await Models(f).SaveOfferingAsync(setup.Human, setup.Route.TenantId, null,
                new(setup.Connection.ConnectionId, null, FlashSnapshot, "Already registered", oldPreset.Transport, oldPreset.Capabilities, true, false),
                (await f.Iam.ReadTenantAsync(setup.Route.TenantId))!.Tenant.Revision, 0);
        var revision = (await f.Iam.ReadTenantAsync(setup.Route.TenantId))!.Tenant.Revision;
        var error = await Assert.ThrowsAsync<SqliteException>(() => db.Database.MigrateAsync());
        Assert.Contains("DeepSeekFlash1017RequiresIdentityReconciliation", error.Message);
        Assert.Equal(FlashPredecessor, (await db.Database.GetAppliedMigrationsAsync()).Last());
        Assert.Equivalent(oldPreset, (await store.ReadModelPresetsAsync()).Single(p => p.PresetId == oldPreset.PresetId));
        Assert.Equivalent(setup.Offering, (await store.ReadTenantModelsAsync(setup.Route.TenantId)).Offerings.Single(o => o.OfferingId == setup.Offering.OfferingId));
        Assert.Equal(revision, (await f.Iam.ReadTenantAsync(setup.Route.TenantId))!.Tenant.Revision);
    }
}
