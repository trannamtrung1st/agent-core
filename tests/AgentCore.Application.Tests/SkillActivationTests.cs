using AgentCore.Application.Agents;
using AgentCore.Application.Admin;
using AgentCore.Application.Identity;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Admin;
using Microsoft.Extensions.Time.Testing;
namespace AgentCore.Application.Tests;

public sealed class SkillActivationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-07T00:00:00Z");
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Older_session_context_cannot_overfill_the_current_instance_Always_budget(bool sqlite)
    {
        await using var db = sqlite ? await SqliteTestHarness.CreateMigratedAsync() : null;
        var ids = new SystemIdGenerator(TimeProvider.System); var clock = new FakeTimeProvider(Now);
        IAgentInstanceStore store = sqlite ? new SqliteAgentInstanceStore(db!.Factory, ids) : new InMemoryAgentInstanceStore { EventStore = new InMemoryAdminEventStore(new SystemIdGenerator(TimeProvider.System)) };
        var original = Definition(1, new SkillSpec("review", "Review", "Review", "OLD", SkillProjection.Always, true, [], []));
        var current = Definition(2,
            new SkillSpec("review", "Review", "Review", new string('a', 4000), SkillProjection.Always, true, [], []),
            new SkillSpec("added", "Added", "Added", new string('b', 4000), SkillProjection.Always, true, [], []));
        var defs = new Definitions(original, current); var owner = await NativeInstanceSeed.CreateAsync(store, defs, ids, clock, original.Id, 1);
        var pin = await NativeSkillSeed.ResolveAsync(store, owner.InstanceId, original);
        await NativeInstanceSeed.UpgradeAsync(store, defs, clock, owner.InstanceId, 2, owner.Revision);
        owner = (await store.FindAsync(owner.InstanceId))!;
        var before = await store.ReadSkillsAsync(owner.InstanceId);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "create",
            input: new("Local", "Local", "ONE", SkillProjection.Always, true, []), actor: SkillAuthor.Agent, context: original).AsTask());
        Assert.Contains("8000-character", error.Message);
        Assert.Equal(owner, await store.FindAsync(owner.InstanceId));
        var after = await store.ReadSkillsAsync(owner.InstanceId);
        Assert.Equal(before.DefinitionStates, after.DefinitionStates); Assert.Equal(before.InstanceSkills, after.InstanceSkills);
        Assert.Equal("OLD", Assert.Single(pin).Procedure);
        Assert.Equal(8000, (await NativeSkillSeed.ResolveAsync(store, owner.InstanceId, current)).Sum(s => s.Procedure.Length));
        var allowed = await NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "create", input: new("Local", "Local", "DEFERRED", SkillProjection.OnDemand, true, []), actor: SkillAuthor.Agent, context: original);
        Assert.Contains(await NativeSkillSeed.ResolveAsync(store, owner.InstanceId, current), s => s.Key == allowed.Key && s.Projection == SkillProjection.OnDemand);
        Assert.Equal("OLD", Assert.Single(pin).Procedure);
        var fresh = await NativeSkillSeed.ResolveAsync(store, owner.InstanceId, current);
        var explicitBudgetError = Assert.Throws<AgentCoreException>(() => AgentCore.Application.Composer.ComposerInputRules.ActiveSkills(current, fresh, [allowed.Key]));
        Assert.Contains("8000-character", explicitBudgetError.Message);
        Assert.Equal(8000, fresh.Where(s => s.Projection == SkillProjection.Always).Sum(s => s.Procedure.Length));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_version_reconciliation_leaves_owner_and_skill_rows_unchanged(bool sqlite)
    {
        await using var db = sqlite ? await SqliteTestHarness.CreateMigratedAsync() : null;
        var ids = new SystemIdGenerator(TimeProvider.System); var clock = new FakeTimeProvider(Now);
        IAgentInstanceStore store = sqlite ? new SqliteAgentInstanceStore(db!.Factory, ids) : new InMemoryAgentInstanceStore { EventStore = new InMemoryAdminEventStore(new SystemIdGenerator(TimeProvider.System)) };
        var original = Definition(1, new SkillSpec("review", "Review", "Review", "SMALL", SkillProjection.Always, true, [], []));
        var upgraded = Definition(2,
            new SkillSpec("review", "Review", "Review", new string('a', 4000), SkillProjection.Always, true, [], []),
            new SkillSpec("added", "Added", "Added", new string('b', 4000), SkillProjection.Always, true, [], []));
        var defs = new Definitions(original, upgraded); var owner = await NativeInstanceSeed.CreateAsync(store, defs, ids, clock, original.Id, 1);
        await NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "create", input: new("Local", "Local", "LOCAL", SkillProjection.Always, true, []), actor: SkillAuthor.Agent);
        owner = (await store.FindAsync(owner.InstanceId))!;
        var before = await store.ReadSkillsAsync(owner.InstanceId);
        await Assert.ThrowsAsync<AgentCoreException>(() => NativeInstanceSeed.UpgradeAsync(store, defs, clock, owner.InstanceId, 2, owner.Revision).AsTask());
        Assert.Equal(owner, await store.FindAsync(owner.InstanceId));
        var after = await store.ReadSkillsAsync(owner.InstanceId);
        Assert.Equal(before.DefinitionStates, after.DefinitionStates); Assert.Equal(before.InstanceSkills, after.InstanceSkills);
        Assert.DoesNotContain(after.DefinitionStates, s => s.DefinitionSkillId == "added");
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Durable_ownership_customization_revision_and_upgrade_are_consistent(bool sqlite)
    {
        await using var db = sqlite ? await SqliteTestHarness.CreateMigratedAsync() : null;
        var ids = new SystemIdGenerator(TimeProvider.System); var clock = new FakeTimeProvider(Now);
        IAgentInstanceStore store = sqlite ? new SqliteAgentInstanceStore(db!.Factory, ids) : new InMemoryAgentInstanceStore { EventStore = new InMemoryAdminEventStore(new SystemIdGenerator(TimeProvider.System)) };
        var original = Definition(1, new SkillSpec("review", "Review", "Review orders", "ORIGINAL", SkillProjection.OnDemand, true, [], []));
        var upgraded = Definition(2, new SkillSpec("review", "Review", "New review", "UPSTREAM", SkillProjection.Always, false, [], []), new SkillSpec("added", "Added", "New default", "ADDED", SkillProjection.OnDemand, false, [], []));
        var removed = Definition(3);
        var defs = new Definitions(original, upgraded, removed);
        var owner = await NativeInstanceSeed.CreateAsync(store, defs, ids, clock, original.Id, 1);
        var initial = Assert.Single(await NativeSkillSeed.ViewsAsync(store, defs, owner.InstanceId), s => s.Origin != SkillOrigin.BuiltIn);
        Assert.True(initial.Enabled); Assert.Equal("definition:review", initial.Key);
        var customized = await NativeSkillSeed.CustomizeAsync(store, defs, clock, owner.InstanceId, initial.Key, initial.Revision, actor: SkillAuthor.Agent);
        var copied = customized.InstanceSkill;
        Assert.Equal(initial.Key, customized.DefinitionSkill.Key);
        Assert.False(customized.DefinitionSkill.Enabled);
        Assert.Equal(initial.Revision + 1, customized.DefinitionSkill.Revision);
        Assert.Equal(SkillOrigin.Instance, copied.Origin); Assert.Equal("review", copied.SourceDefinitionSkillId);
        Assert.False((await NativeSkillSeed.InspectAsync(store, defs, owner.InstanceId, initial.Key)).Enabled);
        Assert.Equal("ORIGINAL", copied.Procedure);
        var updated = await NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "update", copied.Key, copied.Revision,
            new("Review", "Local review", "LOCAL", SkillProjection.Always, true, []), actor: SkillAuthor.Agent);
        Assert.Equal(copied.Revision + 1, updated.Revision);
        await Assert.ThrowsAsync<AgentCoreException>(() => NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "delete", copied.Key, copied.Revision, actor: SkillAuthor.Agent).AsTask());
        await Assert.ThrowsAsync<AgentCoreException>(() => NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "delete", initial.Key, 2, actor: SkillAuthor.Agent).AsTask());
        owner = (await store.FindAsync(owner.InstanceId))!;
        await NativeInstanceSeed.UpgradeAsync(store, defs, clock, owner.InstanceId, 2, owner.Revision);
        var afterUpgrade = await NativeSkillSeed.ViewsAsync(store, defs, owner.InstanceId);
        Assert.False(afterUpgrade.Single(s => s.Key == initial.Key).Enabled);
        Assert.Equal("UPSTREAM", afterUpgrade.Single(s => s.Key == initial.Key).Procedure);
        Assert.False(afterUpgrade.Single(s => s.Key == "definition:added").Enabled);
        Assert.Equal("LOCAL", afterUpgrade.Single(s => s.Key == copied.Key).Procedure);
        owner = (await store.FindAsync(owner.InstanceId))!;
        await NativeInstanceSeed.UpgradeAsync(store, defs, clock, owner.InstanceId, 3, owner.Revision);
        Assert.Single(await NativeSkillSeed.ViewsAsync(store, defs, owner.InstanceId), s => s.Origin != SkillOrigin.BuiltIn);
        Assert.Equal(2, (await store.ReadSkillsAsync(owner.InstanceId)).DefinitionStates.Count);
        owner = (await store.FindAsync(owner.InstanceId))!;
        await NativeInstanceSeed.UpgradeAsync(store, defs, clock, owner.InstanceId, 1, owner.Revision);
        Assert.False((await NativeSkillSeed.InspectAsync(store, defs, owner.InstanceId, initial.Key)).Enabled);
        var readStore = sqlite ? new SqliteAgentInstanceStore(db!.Factory, ids) : store;
        Assert.Equal("LOCAL", Assert.Single((await readStore.ReadSkillsAsync(owner.InstanceId)).InstanceSkills).Procedure);
        await NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "delete", copied.Key, updated.Revision, actor: SkillAuthor.Agent);
        Assert.Empty((await store.ReadSkillsAsync(owner.InstanceId)).InstanceSkills);
    }
    [Fact]
    public async Task Current_execution_keeps_old_content_and_next_execution_sees_durable_changes_without_shadowing()
    {
        var store = new InMemoryAgentInstanceStore { EventStore = new InMemoryAdminEventStore(new SystemIdGenerator(TimeProvider.System)) }; var ids = new SystemIdGenerator(TimeProvider.System); var clock = new FakeTimeProvider(Now);
        var d = Definition(1, new SkillSpec("review", "Review", "Definition review", "DEFINITION", SkillProjection.Always, true, [], []));
        var defs = new Definitions(d); var owner = await NativeInstanceSeed.CreateAsync(store, defs, ids, clock, d.Id);
        var local = await NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "create", input: new("Review", "Local", "OLD", SkillProjection.OnDemand, true, []), actor: SkillAuthor.Agent);
        var pin = await NativeSkillSeed.ResolveAsync(store, owner.InstanceId, d);
        Assert.Equal(2, pin.Count); Assert.Equal(2, pin.Select(s => s.Key).Distinct().Count());
        await NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "update", local.Key, local.Revision, new("Review", "Local", "NEW", SkillProjection.OnDemand, true, []), actor: SkillAuthor.Agent);
        var later = await NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "create", input: new("Later", "Later", "LATER", SkillProjection.OnDemand, true, []), actor: SkillAuthor.Agent);
        var loaded = SkillLoadAdmission.Plan(pin, ["definition:review"], 0, [local.Key, later.Key]);
        Assert.Equal([local.Key], loaded.Admitted); Assert.Equal("unknown", Assert.Single(loaded.Rejected).Reason);
        Assert.Contains("OLD", PromptContextBuilder.BuildActiveSkillContext(pin, loaded.Admitted));
        Assert.DoesNotContain("NEW", PromptContextBuilder.BuildActiveSkillContext(pin, loaded.Admitted));
        Assert.Contains("NEW", PromptContextBuilder.BuildActiveSkillContext(await NativeSkillSeed.ResolveAsync(store, owner.InstanceId, d), [local.Key]));
    }
    [Fact]
    public async Task Resources_missing_authority_and_archived_lifecycle_fail_without_mutation()
    {
        var store = new InMemoryAgentInstanceStore { EventStore = new InMemoryAdminEventStore(new SystemIdGenerator(TimeProvider.System)) }; var ids = new SystemIdGenerator(TimeProvider.System); var clock = new FakeTimeProvider(Now);
        var d = Definition(1, new SkillSpec("bound", "Bound", "Bound resource", "Read reference", SkillProjection.OnDemand, true, [], ["knowledge/reference"]));
        var defs = new Definitions(d); var owner = await NativeInstanceSeed.CreateAsync(store, defs, ids, clock, d.Id);
        await Assert.ThrowsAsync<AgentCoreException>(() => NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "customize", "definition:bound", 1, actor: SkillAuthor.Agent).AsTask());
        Assert.Null(Assert.Single((await store.ReadSkillsAsync(owner.InstanceId)).DefinitionStates).EnabledOverride);
        Assert.Empty((await store.ReadSkillsAsync(owner.InstanceId)).InstanceSkills);
        await Assert.ThrowsAsync<AgentCoreException>(() => NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "create", input: new("Bad", "Bad", "No grant", SkillProjection.Always, true, ["shell"]), actor: SkillAuthor.Agent).AsTask());
        await NativeInstanceSeed.LifecycleAsync(store, clock, owner.InstanceId, AgentInstanceLifecycle.Archived, owner.Revision);
        await Assert.ThrowsAsync<AgentCoreException>(() => NativeSkillSeed.WriteAsync(store, defs, clock, owner.InstanceId, "create", input: new("Local", "Local", "Local", SkillProjection.OnDemand, true, []), actor: SkillAuthor.Agent).AsTask());
    }
    internal static AgentDefinition Definition(int version, params SkillSpec[] skills) => SampleDefinitions.Examiner with { Version = version, Skills = skills, Environment = RoleEnvironment.Empty, SelfManagement = new(HarnessManagementMode.Assisted, [HarnessManagementScope.Skills], [], [], BuiltInSkills: new(false, false)) };
    internal sealed class Definitions(params AgentDefinition[] versions) : IAgentDefinitionStore
    {
        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken cancellationToken = default) => ValueTask.FromResult(versions.Where(d => d.Id == id && (version is null || d.Version == version)).OrderByDescending(d => d.Version).FirstOrDefault());
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<AgentDefinition>>(versions);
    }
}
