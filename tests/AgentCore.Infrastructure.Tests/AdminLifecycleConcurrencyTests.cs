using AgentCore.Application.Admin;
using AgentCore.Application.Identity;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;

namespace AgentCore.Infrastructure.Tests;

public sealed class AdminLifecycleConcurrencyTests
{
    [Fact]
    public async Task Instance_delete_conflicts_when_a_gated_session_reference_lands_first()
    {
        var fixture = CreateFixture();
        var instanceId = Guid.Parse("019944af-00d1-7000-8000-0000000000c1");
        var now = DateTimeOffset.Parse("2026-09-29T03:00:00Z");
        await fixture.Instances.InsertAsync(Instance(instanceId, now));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reference = fixture.Gate.WithInstanceAsync(instanceId, async cancellationToken =>
        {
            entered.TrySetResult();
            var sawDeleteWaiting = await WaitUntilAsync(() => fixture.Gate.Waiters > 0);
            Assert.True(sawDeleteWaiting);
            await fixture.Sessions.SaveAsync(Snapshot(instanceId, now), 0, cancellationToken);
        });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var delete = fixture.InstancesService.DeleteAsync(
            new AdminInstanceDeleteCommand(instanceId, 1, fixture.Ids.NewId(), now));
        await reference;
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => delete.AsTask());
        Assert.Equal("Conflict", error.Code);
        Assert.Contains("1 session", error.Message, StringComparison.Ordinal);
        Assert.NotNull(await fixture.Instances.FindAsync(instanceId));
        Assert.NotNull(await fixture.Sessions.LoadAsync(Snapshot(instanceId, now).SessionId));
    }

    [Fact]
    public async Task Instance_delete_wins_and_the_waiting_reference_does_not_save()
    {
        var fixture = CreateFixture();
        var instanceId = Guid.Parse("019944af-00d1-7000-8000-0000000000c2");
        var now = DateTimeOffset.Parse("2026-09-29T03:10:00Z");
        await fixture.Instances.InsertAsync(Instance(instanceId, now));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Deletion.BeforeCommit = _ =>
        {
            entered.TrySetResult();
            return new ValueTask(release.Task);
        };
        var delete = fixture.InstancesService.DeleteAsync(
            new AdminInstanceDeleteCommand(instanceId, 1, fixture.Ids.NewId(), now));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reference = fixture.Gate.WithInstanceAsync(instanceId, async cancellationToken =>
        {
            var current = await fixture.Instances.FindAsync(instanceId, cancellationToken).ConfigureAwait(false)
                ?? throw AgentCoreErrors.NotFound("Agent instance was not found.");
            if (current.Lifecycle == AgentInstanceLifecycle.Archived)
            {
                throw AgentCoreErrors.Validation("Archived agent instances cannot start new sessions.");
            }

            await fixture.Sessions.SaveAsync(Snapshot(instanceId, now), 0, cancellationToken);
        });
        var sawReferenceWaiting = await WaitUntilAsync(() => fixture.Gate.Waiters > 0);
        Assert.True(sawReferenceWaiting);
        release.TrySetResult();
        await delete;
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => reference.AsTask());
        Assert.Equal("NotFound", error.Code);
        Assert.Null(await fixture.Instances.FindAsync(instanceId));
        Assert.Null(await fixture.Sessions.LoadAsync(Snapshot(instanceId, now).SessionId));
    }

    [Fact]
    public async Task Definition_delete_and_fork_cannot_both_succeed()
    {
        var fixture = CreateFixture();
        var now = DateTimeOffset.Parse("2026-09-29T03:20:00Z");
        var draft = await fixture.Definitions.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "field-guide",
                AdminPublicationHistoryTests.SampleCandidateStatic("field-guide"),
                DefinitionDraftSourceKind.New,
                null,
                now,
                fixture.Ids.NewId()));
        var published = await fixture.Definitions.PublishDraftAsync(
            new AgentDefinitionDraftPublish(
                draft.DraftId,
                draft.Revision,
                [],
                now.AddMinutes(1),
                fixture.Ids.NewId(),
                ChangedSectionIds: ["instructions"]));
        var reloaded = await fixture.Definitions.GetDraftAsync(draft.DraftId);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Deletion.BeforeCommit = _ =>
        {
            entered.TrySetResult();
            return new ValueTask(release.Task);
        };
        var delete = fixture.Lifecycle.DeleteLogicalDefinitionAsync(
            new AdminDefinitionDeleteCommand(
                "field-guide",
                new AdminDefinitionDeleteWitness(
                    [new AdminDraftRevisionWitness(reloaded!.DraftId, reloaded.Revision)],
                    [new AdminPublicationRevisionWitness(published.Version, published.MetadataRevision)]),
                fixture.Ids.NewId(),
                now.AddMinutes(2)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var fork = fixture.Lifecycle.ForkDraftAsync(
            "field-guide",
            published.Version,
            DefinitionDraftSourceKind.ForkDurable);
        var sawForkWaiting = await WaitUntilAsync(() => fixture.Gate.Waiters > 0);
        Assert.True(sawForkWaiting);
        release.TrySetResult();
        await delete;
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => fork.AsTask());
        Assert.Equal("NotFound", error.Code);
        Assert.Empty(await fixture.Definitions.ListDraftsAsync());
        Assert.Null(await fixture.Definitions.GetPublicationAsync("field-guide", published.Version));
    }

    [Fact]
    public async Task Definition_delete_conflicts_when_managed_instance_creation_holds_gate_first()
    {
        var fixture = CreateFixture();
        var now = DateTimeOffset.Parse("2026-09-29T03:30:00Z");
        var (_, published, reloaded) = await PublishFieldGuideAsync(fixture, now);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reference = fixture.Gate.WithDefinitionAsync(
            "field-guide",
            async cancellationToken =>
            {
                entered.TrySetResult();
                var sawDeleteWaiting = await WaitUntilAsync(() => fixture.Gate.Waiters > 0);
                Assert.True(sawDeleteWaiting);
                var definition = await fixture.DefinitionCatalog.GetAsync("field-guide", null, cancellationToken)
                    .ConfigureAwait(false)
                    ?? throw AgentCoreErrors.NotFound("Agent was not found.");
                var instance = await fixture.InstanceRuntime.CreateAsync(definition.Id, definition.Version, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                await fixture.Sessions.SaveAsync(
                    Snapshot(instance.InstanceId, definition, now),
                    0,
                    cancellationToken).ConfigureAwait(false);
            });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var delete = fixture.Lifecycle.DeleteLogicalDefinitionAsync(
            new AdminDefinitionDeleteCommand(
                "field-guide",
                new AdminDefinitionDeleteWitness(
                    [new AdminDraftRevisionWitness(reloaded.DraftId, reloaded.Revision)],
                    [new AdminPublicationRevisionWitness(published.Version, published.MetadataRevision)]),
                fixture.Ids.NewId(),
                now.AddMinutes(3)));
        await reference;
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => delete.AsTask());
        Assert.Equal("Conflict", error.Code);
        Assert.Contains("instance", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(await fixture.Definitions.GetPublicationAsync("field-guide", published.Version));
    }

    [Fact]
    public async Task Definition_delete_wins_and_managed_instance_creation_fails()
    {
        var fixture = CreateFixture();
        var now = DateTimeOffset.Parse("2026-09-29T03:40:00Z");
        var (_, published, reloaded) = await PublishFieldGuideAsync(fixture, now);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Deletion.BeforeCommit = _ =>
        {
            entered.TrySetResult();
            return new ValueTask(release.Task);
        };
        var delete = fixture.Lifecycle.DeleteLogicalDefinitionAsync(
            new AdminDefinitionDeleteCommand(
                "field-guide",
                new AdminDefinitionDeleteWitness(
                    [new AdminDraftRevisionWitness(reloaded.DraftId, reloaded.Revision)],
                    [new AdminPublicationRevisionWitness(published.Version, published.MetadataRevision)]),
                fixture.Ids.NewId(),
                now.AddMinutes(3)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var reference = fixture.InstancesService.CreateManagedAsync("field-guide", published.Version).AsTask();
        var sawReferenceWaiting = await WaitUntilAsync(() => fixture.Gate.Waiters > 0);
        Assert.True(sawReferenceWaiting);
        release.TrySetResult();
        await delete;
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => reference);
        Assert.Equal("NotFound", error.Code);
        Assert.Null(await fixture.Definitions.GetPublicationAsync("field-guide", published.Version));
    }

    [Fact]
    public async Task Definition_delete_wins_and_concurrent_draft_update_fails()
    {
        var fixture = CreateFixture();
        var now = DateTimeOffset.Parse("2026-09-29T03:50:00Z");
        var (candidate, published, reloaded) = await PublishFieldGuideAsync(fixture, now);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Deletion.BeforeCommit = _ =>
        {
            entered.TrySetResult();
            return new ValueTask(release.Task);
        };
        var delete = fixture.Lifecycle.DeleteLogicalDefinitionAsync(
            new AdminDefinitionDeleteCommand(
                "field-guide",
                new AdminDefinitionDeleteWitness(
                    [new AdminDraftRevisionWitness(reloaded.DraftId, reloaded.Revision)],
                    [new AdminPublicationRevisionWitness(published.Version, published.MetadataRevision)]),
                fixture.Ids.NewId(),
                now.AddMinutes(3)));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var update = fixture.Lifecycle.UpdateDraftAsync(
            reloaded.DraftId,
            reloaded.Revision,
            candidate with { SystemInstructions = "Updated after delete started." });
        var sawUpdateWaiting = await WaitUntilAsync(() => fixture.Gate.Waiters > 0);
        Assert.True(sawUpdateWaiting);
        release.TrySetResult();
        await delete;
        var error = await Assert.ThrowsAsync<AgentCoreException>(() => update.AsTask());
        Assert.Equal("NotFound", error.Code);
        Assert.Empty(await fixture.Definitions.ListDraftsAsync());
    }

    [Fact]
    public async Task Draft_update_wins_and_definition_delete_conflicts_on_stale_witness()
    {
        var fixture = CreateFixture();
        var now = DateTimeOffset.Parse("2026-09-29T04:00:00Z");
        var (candidate, published, reloaded) = await PublishFieldGuideAsync(fixture, now);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var update = fixture.Gate.WithDefinitionAsync(
            "field-guide",
            async cancellationToken =>
            {
                entered.TrySetResult();
                var sawDeleteWaiting = await WaitUntilAsync(() => fixture.Gate.Waiters > 0);
                Assert.True(sawDeleteWaiting);
                await fixture.Definitions.UpdateDraftAsync(
                    new AgentDefinitionDraftUpdate(
                        reloaded.DraftId,
                        reloaded.Revision,
                        candidate with { SystemInstructions = "Updated under gate." },
                        now.AddMinutes(1)),
                    cancellationToken);
                release.TrySetResult();
            });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var delete = fixture.Lifecycle.DeleteLogicalDefinitionAsync(
            new AdminDefinitionDeleteCommand(
                "field-guide",
                new AdminDefinitionDeleteWitness(
                    [new AdminDraftRevisionWitness(reloaded.DraftId, reloaded.Revision)],
                    [new AdminPublicationRevisionWitness(published.Version, published.MetadataRevision)]),
                fixture.Ids.NewId(),
                now.AddMinutes(3))).AsTask();
        // The holder observes the queued delete before releasing the gate. A second
        // observer can miss that transient state after the update has completed.
        await update;
        var error = await Assert.ThrowsAsync<AgentCoreException>(async () => await delete);
        Assert.Equal("Conflict", error.Code);
        Assert.NotNull(await fixture.Definitions.GetDraftAsync(reloaded.DraftId));
    }

    [Fact]
    public async Task Archived_managed_instance_blocks_new_scheduled_trigger_admission()
    {
        var fixture = CreateFixture();
        var instanceId = Guid.Parse("019944af-00d1-7000-8000-0000000000d3");
        var profileId = Guid.Parse("019944af-00d1-7000-8000-0000000000d4");
        var now = DateTimeOffset.Parse("2026-09-29T04:10:00Z");
        await fixture.Instances.InsertAsync(Instance(instanceId, now));
        await fixture.Sessions.SaveProfileAsync(
            new UserProfile(profileId, 1, new Dictionary<string, UserProfileValue>
            {
                ["timeZone"] = new("UTC", UserProfileValueSource.UserSet, now)
            }, now),
            0);
        var admission = await TriggerDurableSchedulingPolicy.EvaluateScheduledOccurrenceEligibilityAsync(
            new TriggerOwner(instanceId, profileId),
            fixture.Instances,
            fixture.DefinitionCatalog,
            fixture.Sessions,
            CancellationToken.None);
        Assert.False(admission.Allowed);
        Assert.Equal(UserSchedulingAdmissionDenialReason.InstanceInactive, admission.DenialReason);
    }

    private static async Task<(AgentDefinitionCandidate Candidate, AgentDefinitionPublication Published, AgentDefinitionDraft Reloaded)> PublishFieldGuideAsync(
        Fixture fixture,
        DateTimeOffset now)
    {
        var candidate = AdminPublicationHistoryTests.SampleCandidateStatic("field-guide");
        var draft = await fixture.Definitions.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "field-guide",
                candidate,
                DefinitionDraftSourceKind.New,
                null,
                now,
                fixture.Ids.NewId()));
        var published = await fixture.Definitions.PublishDraftAsync(
            new AgentDefinitionDraftPublish(
                draft.DraftId,
                draft.Revision,
                [],
                now.AddMinutes(1),
                fixture.Ids.NewId(),
                ChangedSectionIds: ["instructions"]));
        var reloaded = await fixture.Definitions.GetDraftAsync(draft.DraftId);
        return (candidate, published, reloaded!);
    }

    private static async Task<bool> WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Yield();
        }

        return condition();
    }

    private static Fixture CreateFixture()
    {
        var clock = TimeProvider.System;
        var ids = new SystemIdGenerator(clock);
        var events = new InMemoryAdminEventStore(ids);
        var state = new InMemoryDurableState();
        var instances = new InMemoryAgentInstanceStore { EventStore = events };
        var sessions = new InMemoryMemoryStore();
        var definitions = new InMemoryAgentDefinitionAdminStore(ids) { EventStore = events };
        definitions.ResourceStore = new InMemoryAgentDefinitionResourceAdminStore(
            definitions,
            new InMemoryDefinitionResourceContentStore(),
            ids);
        _ = new InMemoryDefinitionDraftEvaluationStore(definitions);
        var deletion = new InMemoryAdminLifecycleDeletion(
            instances,
            sessions,
            new InMemoryStructuredMemoryStore(),
            new InMemoryTriggerStore(state),
            new InMemoryWorkItemStore(state),
            new InMemoryConversationTurnExecutionStore(state),
            definitions,
            events);
        var gate = new AdminLifecycleCoordinator();
        var builtIns = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var definitionCatalog = new CompositeAgentDefinitionStore(builtIns, definitions);
        var lifecycle = new AgentDefinitionLifecycleService(
            builtIns,
            definitions,
            SyntheticProviderAliases.Default,
            clock,
            ids,
            deletion,
            gate);
        var instanceRuntime = new AgentInstanceService(instances, definitionCatalog, ids, clock);
        var sessionsManager = new SessionManager(
            definitionCatalog,
            sessions,
            ids,
            clock,
            new VoiceAvailability { SpeechAdaptersResolved = true },
            instances: instanceRuntime,
            lifecycleGate: gate);
        var instanceService = new AdminAgentInstanceService(
            definitionCatalog,
            instances,
            events,
            ids,
            clock,
            deletion: deletion,
            lifecycleGate: gate);
        return new Fixture(
            ids,
            instances,
            sessions,
            definitions,
            deletion,
            gate,
            lifecycle,
            instanceService,
            sessionsManager,
            definitionCatalog,
            instanceRuntime);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents) && File.Exists(Path.Combine(agents, "examiner.json")))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }

    private static AgentInstance Instance(Guid instanceId, DateTimeOffset now) =>
        new(
            instanceId,
            "field-guide",
            1,
            new AgentIdentity("Guide", "role", "desc", "tone"),
            AgentInstanceLifecycle.Archived,
            now,
            now);

    private static SessionSnapshot Snapshot(
        Guid instanceId,
        AgentDefinition definition,
        DateTimeOffset now) =>
        new SessionSnapshot(
            1,
            Guid.Parse("019944af-00d1-7000-8000-0000000000c9"),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now, AgentInstanceId: instanceId) with
        {
            AgentInstanceId = instanceId
        };

    private static SessionSnapshot Snapshot(Guid instanceId, DateTimeOffset now)
    {
        var definition = new AgentDefinition(
            1,
            "field-guide",
            1,
            new AgentIdentity("Guide", "role", "desc", "tone"),
            ["Help"],
            "instructions",
            new BehaviorPolicy("acknowledgeThenContinue", true, true),
            new ConversationPolicy("concise", true, "en", 256),
            new InitiativePolicy(false, 30000, 60000, 1, []),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", null, null),
            new Dictionary<string, string>());
        return new SessionSnapshot(
            1,
            Guid.Parse("019944af-00d1-7000-8000-0000000000c9"),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now, AgentInstanceId: instanceId) with
        {
            AgentInstanceId = instanceId
        };
    }

    private sealed record Fixture(
        IIdGenerator Ids,
        InMemoryAgentInstanceStore Instances,
        InMemoryMemoryStore Sessions,
        InMemoryAgentDefinitionAdminStore Definitions,
        InMemoryAdminLifecycleDeletion Deletion,
        AdminLifecycleCoordinator Gate,
        AgentDefinitionLifecycleService Lifecycle,
        AdminAgentInstanceService InstancesService,
        SessionManager SessionsManager,
        IAgentDefinitionStore DefinitionCatalog,
        AgentInstanceService InstanceRuntime);
}
