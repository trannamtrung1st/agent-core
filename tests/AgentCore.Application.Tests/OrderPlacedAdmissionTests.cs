using System.Text;
using System.Text.Json;
using AgentCore.Application.Connections;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Connections;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Events;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers;
using Microsoft.Extensions.Logging;

namespace AgentCore.Application.Tests;

public sealed class OrderPlacedAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Envelope_keeps_only_allowlisted_fields()
    {
        var json = ExternalEventEnvelope.Build("evt-1", "1001");
        Assert.True(
            ExternalEventEnvelope.TryNormalize(Encoding.UTF8.GetBytes(json), out var evidence, out var eventId, out var eventType, out _, out var error),
            error);
        Assert.Equal("evt-1", eventId);
        Assert.Equal(ExternalEventTypes.OrderPlaced, eventType);
        using var document = JsonDocument.Parse(evidence);
        Assert.Equal(["sourceEventId", "orderReference"], document.RootElement.EnumerateObject().Select(item => item.Name).ToArray());

        Assert.False(ExternalEventEnvelope.TryNormalize("{}"u8, out _, out _, out _, out _, out _));
        Assert.False(ExternalEventEnvelope.TryNormalize("{"u8, out _, out _, out _, out _, out _));
        Assert.False(ExternalEventEnvelope.TryNormalize(
            """{"eventId":"evt-1","type":"order.placed","data":{"orderReference":"1001"},"instructions":"do this"}"""u8,
            out _,
            out _,
            out _,
            out _,
            out _));
        var unsupported = ExternalEventEnvelope.TryNormalize(
            """{"eventId":"evt-1","type":"order.refunded","data":{"orderReference":"1001"}}"""u8,
            out _,
            out _,
            out _,
            out _,
            out error);
        Assert.False(unsupported);
        Assert.Equal("unsupported_event", error);
    }

    [Fact]
    public async Task One_source_event_fans_out_once_per_subscriber()
    {
        var secretary = await FixtureAsync("secretary", 2);
        var monitor = await FixtureAsync(
            "secretary",
            2,
            secretary.Events,
            secretary.Logs,
            instances: secretary.Instances,
            triggers: secretary.Triggers);
        var issued = await secretary.Sources.CreateAsync("Demo Store");
        await secretary.Sources.SubscribeAsync(secretary.InstanceId, issued.SourceId, ExternalEventTypes.OrderPlaced);
        await secretary.Sources.SubscribeAsync(monitor.InstanceId, issued.SourceId, ExternalEventTypes.OrderPlaced);
        var body = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("order-105-created", "105"));
        var first = await secretary.Ingress.AdmitAsync(issued.SourceKey, issued.Token, body);
        Assert.Equal(ExternalEventIngressKind.Admitted, first.Kind);
        var pending = await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10);
        Assert.Equal(2, pending.Count);
        Assert.Equal(
            new[] { secretary.InstanceId, monitor.InstanceId }.Order(),
            pending.Select(item => item.Owner.AgentInstanceId).Order());
        Assert.All(pending, item => Assert.NotNull(item.ModelPin));
        Assert.All(pending, item => Assert.Equal(1, item.ScheduleRevision));
        Assert.All(
            pending,
            item => Assert.Equal(
                ExternalEventIngress.OccurrenceDedupeKey(issued.SourceId, "order-105-created"),
                item.DedupeKey));
        Assert.All(pending, item => Assert.Null(item.DurableWorkItemId));
        var saved = await secretary.Events.GetEventAsync(issued.SourceId, "order-105-created");
        Assert.Equal(first.EventId, saved!.EventId);

        var duplicate = await secretary.Ingress.AdmitAsync(issued.SourceKey, issued.Token, body);
        Assert.Equal(ExternalEventIngressKind.Duplicate, duplicate.Kind);
        Assert.Equal(first.EventId, duplicate.EventId);
        Assert.Equal(2, (await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10)).Count);

        var text = string.Join('\n', secretary.Logs.Lines);
        Assert.DoesNotContain(issued.Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookTokens.Hash(issued.Token), text, StringComparison.Ordinal);
        Assert.Null(await secretary.Connections.GetByAgentAsync(secretary.InstanceId));
        var stored = await secretary.Events.GetAsync(issued.SourceId);
        Assert.Equal(ExternalEventSourceStatus.Active, stored!.Status);
        Assert.NotEqual(issued.Token, stored.CredentialHash);
    }

    [Fact]
    public async Task Invalid_credentials_and_ineligible_subscribers_do_not_create_occurrences()
    {
        var secretary = await FixtureAsync("secretary", 2);
        var issued = await secretary.Sources.CreateAsync("Demo Store");
        await secretary.Sources.SubscribeAsync(secretary.InstanceId, issued.SourceId, ExternalEventTypes.OrderPlaced);
        var valid = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("evt-1", "1001"));
        Assert.Equal(ExternalEventIngressKind.Unauthorized, (await secretary.Ingress.AdmitAsync(issued.SourceKey, "wrong-token", valid)).Kind);
        Assert.Equal(ExternalEventIngressKind.Unauthorized, (await secretary.Ingress.AdmitAsync(Guid.NewGuid(), issued.Token, valid)).Kind);
        Assert.Equal(
            ExternalEventIngressKind.Invalid,
            (await secretary.Ingress.AdmitAsync(issued.SourceKey, issued.Token, """{"eventId":"evt-1"}"""u8.ToArray())).Kind);
        Assert.Null(await secretary.Events.GetEventAsync(issued.SourceId, "evt-1"));

        await secretary.Sources.RevokeAsync(issued.SourceId);
        Assert.Equal(ExternalEventIngressKind.Unauthorized, (await secretary.Ingress.AdmitAsync(issued.SourceKey, issued.Token, valid)).Kind);
        Assert.NotNull(await secretary.Events.GetAsync(issued.SourceId));
        var rotated = await secretary.Sources.RotateAsync(issued.SourceId);
        Assert.Equal(issued.SourceKey, rotated.SourceKey);
        Assert.NotEqual(issued.Token, rotated.Token);
        Assert.Equal(ExternalEventIngressKind.Unauthorized, (await secretary.Ingress.AdmitAsync(issued.SourceKey, issued.Token, valid)).Kind);
        var admitted = await secretary.Ingress.AdmitAsync(rotated.SourceKey, rotated.Token, valid);
        Assert.Equal(ExternalEventIngressKind.Admitted, admitted.Kind);
        Assert.Single(await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));

        var v1 = await FixtureAsync("secretary", 1, secretary.Events, secretary.Logs);
        await Assert.ThrowsAsync<AgentCoreException>(() =>
            v1.Sources.SubscribeAsync(v1.InstanceId, issued.SourceId, ExternalEventTypes.OrderPlaced).AsTask());
        Assert.Empty(await v1.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));
    }

    [Fact]
    public async Task A_subscriber_without_a_usable_model_does_not_block_another_subscriber()
    {
        var blocked = await FixtureAsync("secretary", 2, unattendedModel: "missing-model");
        var ready = await FixtureAsync(
            "secretary",
            2,
            blocked.Events,
            blocked.Logs,
            instances: blocked.Instances,
            triggers: blocked.Triggers);
        var issued = await blocked.Sources.CreateAsync("Demo Store");
        await blocked.Sources.SubscribeAsync(blocked.InstanceId, issued.SourceId, ExternalEventTypes.OrderPlaced);
        await blocked.Sources.SubscribeAsync(ready.InstanceId, issued.SourceId, ExternalEventTypes.OrderPlaced);
        var body = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("evt-9", "1009"));
        var result = await blocked.Ingress.AdmitAsync(issued.SourceKey, issued.Token, body);
        Assert.Equal(ExternalEventIngressKind.Admitted, result.Kind);
        var pending = Assert.Single(await blocked.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));
        Assert.Equal(ready.InstanceId, pending.Owner.AgentInstanceId);
        Assert.NotNull(await blocked.Events.GetEventAsync(issued.SourceId, "evt-9"));
    }

    [Fact]
    public async Task An_event_with_no_eligible_subscriber_is_still_admitted()
    {
        var secretary = await FixtureAsync("secretary", 2);
        var other = await FixtureAsync(
            "secretary",
            2,
            secretary.Events,
            secretary.Logs,
            instances: secretary.Instances,
            triggers: secretary.Triggers);
        var issued = await secretary.Sources.CreateAsync("Demo Store");
        var body = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("evt-empty", "1002"));
        var alone = await secretary.Ingress.AdmitAsync(issued.SourceKey, issued.Token, body);
        Assert.Equal(ExternalEventIngressKind.Admitted, alone.Kind);
        Assert.Empty(await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));

        var otherSource = await secretary.Sources.CreateAsync("Other Store");
        await other.Sources.SubscribeAsync(other.InstanceId, otherSource.SourceId, ExternalEventTypes.OrderPlaced);
        var subscribed = await secretary.Sources.SubscribeAsync(secretary.InstanceId, issued.SourceId, ExternalEventTypes.OrderPlaced);
        await secretary.Triggers.CancelAsync(
            subscribed.Owner,
            subscribed.RegistrationId,
            subscribed.Revision,
            DateTimeOffset.UtcNow);
        var unmatched = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("evt-unmatched", "1003"));
        var admitted = await secretary.Ingress.AdmitAsync(issued.SourceKey, issued.Token, unmatched);
        Assert.Equal(ExternalEventIngressKind.Admitted, admitted.Kind);
        Assert.Empty(await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));
        Assert.NotNull(await secretary.Events.GetEventAsync(issued.SourceId, "evt-unmatched"));
    }

    [Fact]
    public async Task The_same_source_event_id_from_two_sources_wakes_one_agent_twice()
    {
        var secretary = await FixtureAsync("secretary", 2);
        var first = await secretary.Sources.CreateAsync("Store A");
        var second = await secretary.Sources.CreateAsync("Store B");
        await secretary.Sources.SubscribeAsync(secretary.InstanceId, first.SourceId, ExternalEventTypes.OrderPlaced);
        await secretary.Sources.SubscribeAsync(secretary.InstanceId, second.SourceId, ExternalEventTypes.OrderPlaced);
        var body = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("order-123-placed", "123"));
        Assert.Equal(ExternalEventIngressKind.Admitted, (await secretary.Ingress.AdmitAsync(first.SourceKey, first.Token, body)).Kind);
        Assert.Equal(ExternalEventIngressKind.Admitted, (await secretary.Ingress.AdmitAsync(second.SourceKey, second.Token, body)).Kind);
        var pending = await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10);
        Assert.Equal(2, pending.Count);
        Assert.Equal(
            new[]
            {
                ExternalEventIngress.OccurrenceDedupeKey(first.SourceId, "order-123-placed"),
                ExternalEventIngress.OccurrenceDedupeKey(second.SourceId, "order-123-placed")
            }.Order(),
            pending.Select(item => item.DedupeKey).Order());
    }

    [Fact]
    public async Task A_duplicate_delivery_finishes_fan_out_that_stopped_early()
    {
        var ids = new FailOnceIdGenerator(7);
        var secretary = await FixtureAsync("secretary", 2, ids: ids);
        var monitor = await FixtureAsync(
            "secretary",
            2,
            secretary.Events,
            secretary.Logs,
            instances: secretary.Instances,
            triggers: secretary.Triggers,
            ids: ids);
        var issued = await secretary.Sources.CreateAsync("Demo Store");
        await secretary.Sources.SubscribeAsync(secretary.InstanceId, issued.SourceId, ExternalEventTypes.OrderPlaced);
        await monitor.Sources.SubscribeAsync(monitor.InstanceId, issued.SourceId, ExternalEventTypes.OrderPlaced);
        var body = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("order-106-created", "106"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => secretary.Ingress.AdmitAsync(issued.SourceKey, issued.Token, body).AsTask());
        Assert.NotNull(await secretary.Events.GetEventAsync(issued.SourceId, "order-106-created"));
        Assert.Single(await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));

        var late = await FixtureAsync(
            "secretary",
            2,
            secretary.Events,
            secretary.Logs,
            instances: secretary.Instances,
            triggers: secretary.Triggers,
            ids: ids);
        await late.Sources.SubscribeAsync(late.InstanceId, issued.SourceId, ExternalEventTypes.OrderPlaced);
        Assert.Equal(1, await secretary.Ingress.ResumePendingAsync());
        var pending = await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10);
        Assert.Equal(2, pending.Count);
        Assert.Equal(
            new[] { secretary.InstanceId, monitor.InstanceId }.Order(),
            pending.Select(item => item.Owner.AgentInstanceId).Order());
        Assert.DoesNotContain(pending, item => item.Owner.AgentInstanceId == late.InstanceId);
    }

    [Fact]
    public async Task A_revoked_source_cannot_gain_a_new_subscription()
    {
        var secretary = await FixtureAsync("secretary", 2);
        var issued = await secretary.Sources.CreateAsync("Demo Store");
        await secretary.Sources.RevokeAsync(issued.SourceId);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            secretary.Sources.SubscribeAsync(secretary.InstanceId, issued.SourceId, ExternalEventTypes.OrderPlaced).AsTask());
        Assert.Contains("active", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));
    }

    private static async Task<Fixture> FixtureAsync(
        string definitionId,
        int version,
        InMemoryExternalEventStore? events = null,
        ListLogger? logs = null,
        IModelCatalog? catalog = null,
        InMemoryAgentInstanceStore? instances = null,
        InMemoryTriggerStore? triggers = null,
        string? unattendedModel = null,
        IIdGenerator? ids = null)
    {
        var definitions = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var definition = (await definitions.GetAsync(definitionId, version))!;
        instances ??= new InMemoryAgentInstanceStore();
        var memory = new InMemoryMemoryStore();
        var instanceId = Guid.NewGuid();
        await instances.InsertAsync(new AgentInstance(
            instanceId,
            definition.Id,
            definition.Version,
            definition.Identity,
            AgentInstanceLifecycle.Active,
            Now,
            Now,
            false,
            UnattendedModelCatalogKey: unattendedModel));
        var profiles = new LocalUserProfileService(memory, TimeProvider.System);
        var profile = await profiles.GetLocalProfileAsync();
        var connections = new InMemoryApplicationConnectionStore();
        triggers ??= new InMemoryTriggerStore();
        events ??= new InMemoryExternalEventStore();
        logs ??= new ListLogger();
        ids ??= new SystemIdGenerator(TimeProvider.System);
        var guard = new TriggerAdmissionGuard(instances, definitions, memory);
        var ingress = new ExternalEventIngress(
            events,
            triggers,
            guard,
            ids,
            TimeProvider.System,
            instances,
            definitions,
            catalog ?? ModelCatalogFactory.Synthetic(),
            logs);
        var sources = new ExternalEventSourceService(
            events,
            triggers,
            guard,
            ids,
            TimeProvider.System,
            profiles,
            instances,
            logs);
        return new Fixture(instanceId, instances, connections, triggers, events, ingress, sources, logs);
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (Directory.Exists(agents))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }

    private sealed class FailOnceIdGenerator(int failOnCall) : IIdGenerator
    {
        private int _calls;

        public Guid NewId()
        {
            var call = Interlocked.Increment(ref _calls);
            if (call == failOnCall)
            {
                throw new InvalidOperationException("fan-out stopped");
            }

            return Guid.CreateVersion7();
        }

        public Guid NewSessionId() => Guid.CreateVersion7();
    }

    private sealed record Fixture(
        Guid InstanceId,
        InMemoryAgentInstanceStore Instances,
        InMemoryApplicationConnectionStore Connections,
        InMemoryTriggerStore Triggers,
        InMemoryExternalEventStore Events,
        ExternalEventIngress Ingress,
        ExternalEventSourceService Sources,
        ListLogger Logs);

    private sealed class ListLogger : ILogger<ExternalEventIngress>, ILogger<ExternalEventSourceService>
    {
        public List<string> Lines { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }

}
