using System.Text;
using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
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
    public void Envelope_preserves_bounded_untrusted_data_and_rejects_ambiguous_shapes()
    {
        var json = ExternalEventEnvelope.Build("evt-1", "1001");
        Assert.True(ExternalEventEnvelope.TryNormalize(Encoding.UTF8.GetBytes(json), out var evidence, out var eventId, out _, out var error), error);
        Assert.Equal("evt-1", eventId);
        using var document = JsonDocument.Parse(evidence);
        Assert.Equal("1001", document.RootElement.GetProperty("data").GetProperty("orderReference").GetString());
        Assert.False(ExternalEventEnvelope.TryNormalize("{}"u8, out _, out _, out _, out _));
        Assert.False(ExternalEventEnvelope.TryNormalize("{"u8, out _, out _, out _, out _));
        Assert.False(ExternalEventEnvelope.TryNormalize("""{"eventId":"evt-1","data":{},"instructions":"do this"}"""u8, out _, out _, out _, out _));
        Assert.False(ExternalEventEnvelope.TryNormalize("""{"eventId":"evt-1","data":{"x":1,"x":2}}"""u8, out _, out _, out _, out _));
        Assert.True(ExternalEventEnvelope.TryNormalize("""{"eventId":"invoice-1","data":{"invoice":{"amount":19,"currency":"USD"},"instructions":"untrusted"}}"""u8, out _, out _, out _, out _));
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
        var issued = await secretary.Sources.CreateAsync("Demo Store", "event.demo-store");
        await secretary.SubscribeAsync(secretary.InstanceId, issued.ResourceId);
        await secretary.SubscribeAsync(monitor.InstanceId, issued.ResourceId);
        var body = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("order-105-created", "105"));
        var first = await secretary.Ingress.AdmitAsync(issued.EventKey, issued.Token, body);
        Assert.Equal(ExternalEventIngressKind.Admitted, first.Kind);
        var pending = await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10);
        Assert.Equal(2, pending.Count);
        Assert.Equal(
            new[] { secretary.InstanceId, monitor.InstanceId }.Order(),
            pending.Select(item => item.Owner.AgentInstanceId).Order());
        Assert.All(pending, item => Assert.NotNull(item.ModelPin));
        Assert.All(pending, item => Assert.Equal(1, item.TriggerRevision));
        Assert.All(
            pending,
            item => Assert.Equal(
                ExternalEventIngress.OccurrenceDedupeKey(issued.ResourceId, "order-105-created") + ":" + item.AutomationId,
                item.DedupeKey));
        Assert.All(pending, item => Assert.Null(item.AcceptedAgentRunId));
        var saved = await secretary.Events.GetEventAsync(issued.ResourceId, "order-105-created");
        Assert.Equal(first.EventId, saved!.EventId);

        var duplicate = await secretary.Ingress.AdmitAsync(issued.EventKey, issued.Token, body);
        Assert.Equal(ExternalEventIngressKind.Duplicate, duplicate.Kind);
        Assert.Equal(first.EventId, duplicate.EventId);
        Assert.Equal(2, (await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10)).Count);

        var text = string.Join('\n', secretary.Logs.Lines);
        Assert.DoesNotContain(issued.Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookTokens.Hash(issued.Token), text, StringComparison.Ordinal);
        var stored = await secretary.Events.GetAsync(issued.ResourceId);
        Assert.Equal(WebhookEventStatus.Active, stored!.Status);
        Assert.NotEqual(issued.Token, stored.CredentialHash);
    }

    [Fact]
    public async Task Invalid_credentials_and_ineligible_subscribers_do_not_create_occurrences()
    {
        var secretary = await FixtureAsync("secretary", 2);
        var issued = await secretary.Sources.CreateAsync("Demo Store", "event.demo-store");
        await secretary.SubscribeAsync(secretary.InstanceId, issued.ResourceId);
        var valid = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("evt-1", "1001"));
        Assert.Equal(ExternalEventIngressKind.Unauthorized, (await secretary.Ingress.AdmitAsync(issued.EventKey, "wrong-token", valid)).Kind);
        Assert.Equal(ExternalEventIngressKind.Unauthorized, (await secretary.Ingress.AdmitAsync("missing.event", issued.Token, valid)).Kind);
        Assert.Equal(
            ExternalEventIngressKind.Invalid,
            (await secretary.Ingress.AdmitAsync(issued.EventKey, issued.Token, """{"eventId":"evt-1"}"""u8.ToArray())).Kind);
        Assert.Null(await secretary.Events.GetEventAsync(issued.ResourceId, "evt-1"));

        await secretary.Sources.RevokeAsync(issued.ResourceId);
        Assert.Equal(ExternalEventIngressKind.Unauthorized, (await secretary.Ingress.AdmitAsync(issued.EventKey, issued.Token, valid)).Kind);
        Assert.NotNull(await secretary.Events.GetAsync(issued.ResourceId));
        var rotated = await secretary.Sources.RotateAsync(issued.ResourceId);
        Assert.Equal(issued.EventKey, rotated.EventKey);
        Assert.NotEqual(issued.Token, rotated.Token);
        Assert.Equal(ExternalEventIngressKind.Unauthorized, (await secretary.Ingress.AdmitAsync(issued.EventKey, issued.Token, valid)).Kind);
        var admitted = await secretary.Ingress.AdmitAsync(rotated.EventKey, rotated.Token, valid);
        Assert.Equal(ExternalEventIngressKind.Admitted, admitted.Kind);
        Assert.Single(await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));

        var v1 = await FixtureAsync("secretary", 1, secretary.Events, secretary.Logs);
        await Assert.ThrowsAsync<AgentCoreException>(() =>
            v1.SubscribeAsync(v1.InstanceId, issued.ResourceId).AsTask());
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
        var issued = await blocked.Sources.CreateAsync("Demo Store", "event.demo-store");
        await blocked.SubscribeAsync(blocked.InstanceId, issued.ResourceId);
        await blocked.SubscribeAsync(ready.InstanceId, issued.ResourceId);
        var body = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("evt-9", "1009"));
        var result = await blocked.Ingress.AdmitAsync(issued.EventKey, issued.Token, body);
        Assert.Equal(ExternalEventIngressKind.Admitted, result.Kind);
        var pending = Assert.Single(await blocked.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));
        Assert.Equal(ready.InstanceId, pending.Owner.AgentInstanceId);
        Assert.NotNull(await blocked.Events.GetEventAsync(issued.ResourceId, "evt-9"));
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
        var issued = await secretary.Sources.CreateAsync("Demo Store", "event.demo-store");
        var body = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("evt-empty", "1002"));
        var alone = await secretary.Ingress.AdmitAsync(issued.EventKey, issued.Token, body);
        Assert.Equal(ExternalEventIngressKind.Admitted, alone.Kind);
        Assert.Empty(await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));

        var otherSource = await secretary.Sources.CreateAsync("Other Store", "event.other-store");
        await other.SubscribeAsync(other.InstanceId, otherSource.ResourceId);
        var subscribed = await secretary.SubscribeAsync(secretary.InstanceId, issued.ResourceId);
        await secretary.Triggers.CancelAsync(
            subscribed.Owner,
            subscribed.AutomationId,
            subscribed.Revision,
            DateTimeOffset.UtcNow);
        var unmatched = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("evt-unmatched", "1003"));
        var admitted = await secretary.Ingress.AdmitAsync(issued.EventKey, issued.Token, unmatched);
        Assert.Equal(ExternalEventIngressKind.Admitted, admitted.Kind);
        Assert.Empty(await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));
        Assert.NotNull(await secretary.Events.GetEventAsync(issued.ResourceId, "evt-unmatched"));
    }

    [Fact]
    public async Task The_same_source_event_id_from_two_sources_wakes_one_agent_twice()
    {
        var secretary = await FixtureAsync("secretary", 2);
        var first = await secretary.Sources.CreateAsync("Store A", "event.store-a");
        var second = await secretary.Sources.CreateAsync("Store B", "event.store-b");
        await secretary.SubscribeAsync(secretary.InstanceId, first.ResourceId);
        await secretary.SubscribeAsync(secretary.InstanceId, second.ResourceId);
        var body = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("order-123-placed", "123"));
        Assert.Equal(ExternalEventIngressKind.Admitted, (await secretary.Ingress.AdmitAsync(first.EventKey, first.Token, body)).Kind);
        Assert.Equal(ExternalEventIngressKind.Admitted, (await secretary.Ingress.AdmitAsync(second.EventKey, second.Token, body)).Kind);
        var pending = await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10);
        Assert.Equal(2, pending.Count);
        Assert.Equal(
            new[]
            {
                ExternalEventIngress.OccurrenceDedupeKey(first.ResourceId, "order-123-placed"),
                ExternalEventIngress.OccurrenceDedupeKey(second.ResourceId, "order-123-placed")
            }.Order(),
            pending.Select(item => item.DedupeKey[..item.DedupeKey.LastIndexOf(':')]).Order());
    }

    [Fact]
    public async Task A_duplicate_delivery_finishes_fan_out_that_stopped_early()
    {
        var ids = new FailOnceIdGenerator(4);
        var secretary = await FixtureAsync("secretary", 2, ids: ids);
        var monitor = await FixtureAsync(
            "secretary",
            2,
            secretary.Events,
            secretary.Logs,
            instances: secretary.Instances,
            triggers: secretary.Triggers,
            ids: ids);
        var issued = await secretary.Sources.CreateAsync("Demo Store", "event.demo-store");
        await secretary.SubscribeAsync(secretary.InstanceId, issued.ResourceId);
        await monitor.SubscribeAsync(monitor.InstanceId, issued.ResourceId);
        var body = Encoding.UTF8.GetBytes(ExternalEventEnvelope.Build("order-106-created", "106"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => secretary.Ingress.AdmitAsync(issued.EventKey, issued.Token, body).AsTask());
        Assert.NotNull(await secretary.Events.GetEventAsync(issued.ResourceId, "order-106-created"));
        Assert.Single(await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));

        var late = await FixtureAsync(
            "secretary",
            2,
            secretary.Events,
            secretary.Logs,
            instances: secretary.Instances,
            triggers: secretary.Triggers,
            ids: ids);
        await late.SubscribeAsync(late.InstanceId, issued.ResourceId);
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
        var issued = await secretary.Sources.CreateAsync("Demo Store", "event.demo-store");
        await secretary.Sources.RevokeAsync(issued.ResourceId);
        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            secretary.SubscribeAsync(secretary.InstanceId, issued.ResourceId).AsTask());
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
        var definitions = new ScenarioDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
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
            UnattendedModelCatalogKey: unattendedModel));
        var profiles = new LocalUserProfileService(memory, TimeProvider.System);
        var profile = await profiles.GetLocalProfileAsync();
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
        var sources = new WebhookEventService(
            events, ids, TimeProvider.System, logs);
        return new Fixture(instanceId, instances, triggers, events, ingress, sources, logs, guard);
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
        InMemoryTriggerStore Triggers,
        InMemoryExternalEventStore Events,
        ExternalEventIngress Ingress,
        WebhookEventService Sources,
        ListLogger Logs, ITriggerAdmissionGuard Guard)
    {
        public async ValueTask<Automation> SubscribeAsync(Guid instanceId, Guid sourceId)
        {
            var source = await Events.GetAsync(sourceId) ?? throw AgentCoreErrors.NotFound("Event Source not found.");
            if (source.Status != WebhookEventStatus.Active) throw AgentCoreErrors.Validation("Event Source is not active.");
            var owner = new TriggerOwner(instanceId, LocalUserProfile.Id);
            if ((await Guard.EvaluateAsync(owner, TriggerSourceKind.ApplicationEvent)).Kind != TriggerAdmissionDecisionKind.Allow)
                throw AgentCoreErrors.Validation("Event admission is disabled.");
            var now = DateTimeOffset.UtcNow;
            return await Triggers.CreateAsync(new(Guid.NewGuid(), owner, AutomationStatus.Active, "Review the order and report what needs attention.",
                new EventTrigger(sourceId), null, null, 0, 1, 1, new(TriggerAuthorizationOrigin.AdminOwner, null, null, now, now), null));
        }
    }

    private sealed class ListLogger : ILogger<ExternalEventIngress>, ILogger<WebhookEventService>
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
