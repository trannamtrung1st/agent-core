using System.Text;
using System.Text.Json;
using AgentCore.Application.Connections;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Connections;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
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
    public void Payload_builder_keeps_only_allowlisted_fields()
    {
        var json = OrderPlacedPayload.Build("evt-1", "1001");
        Assert.True(OrderPlacedPayload.TryNormalize(Encoding.UTF8.GetBytes(json), out var evidence, out var eventId, out var error), error);
        Assert.Equal("evt-1", eventId);
        Assert.Equal(json, evidence);
        using var document = JsonDocument.Parse(evidence);
        Assert.Equal(["sourceEventId", "orderReference"], document.RootElement.EnumerateObject().Select(item => item.Name).ToArray());

        var stamped = OrderPlacedPayload.Build("evt-2", "1002", Now);
        Assert.True(OrderPlacedPayload.TryNormalize(Encoding.UTF8.GetBytes(stamped), out var stampedEvidence, out _, out error), error);
        Assert.Equal(stamped, stampedEvidence);

        Assert.False(OrderPlacedPayload.TryNormalize("{}"u8, out _, out _, out _));
        Assert.False(OrderPlacedPayload.TryNormalize("{"u8, out _, out _, out _));
        Assert.False(OrderPlacedPayload.TryNormalize(
            """{"sourceEventId":"evt-1","orderReference":"1001","instructions":"do this"}"""u8,
            out _,
            out _,
            out _));
        Assert.False(OrderPlacedPayload.TryNormalize(
            """{"sourceEventId":"evt-1","orderReference":"1001","eventType":"order.refunded"}"""u8,
            out _,
            out _,
            out _));
        Assert.False(OrderPlacedPayload.TryNormalize(
            """{"sourceEventId":"evt 1","orderReference":"1001"}"""u8,
            out _,
            out _,
            out _));
    }

    [Fact]
    public async Task Secretary_v2_admits_once_and_a_duplicate_returns_the_same_occurrence()
    {
        var fixture = await FixtureAsync("secretary", 2);
        var issued = await fixture.IssueAsync();
        var body = Encoding.UTF8.GetBytes(OrderPlacedPayload.Build("evt-1", "1001"));
        var first = await fixture.Webhook.AdmitAsync(issued.WebhookKey, issued.Token, body);
        Assert.Equal(OrderPlacedAdmissionKind.Admitted, first.Kind);
        var saved = await fixture.Triggers.GetOccurrenceAsync(fixture.Owner, first.OccurrenceId!.Value);
        Assert.Equal(OccurrenceRoutingDisposition.Pending, saved!.Disposition);
        Assert.Null(saved.DurableWorkItemId);
        Assert.NotNull(saved.ModelPin);
        using (var evidence = JsonDocument.Parse(saved.EvidenceJson))
        {
            Assert.Equal(["sourceEventId", "orderReference"], evidence.RootElement.EnumerateObject().Select(item => item.Name).ToArray());
            Assert.Equal("evt-1", evidence.RootElement.GetProperty("sourceEventId").GetString());
            Assert.Equal("1001", evidence.RootElement.GetProperty("orderReference").GetString());
        }

        var duplicate = await fixture.Webhook.AdmitAsync(issued.WebhookKey, issued.Token, body);
        Assert.Equal(OrderPlacedAdmissionKind.Duplicate, duplicate.Kind);
        Assert.Equal(first.OccurrenceId, duplicate.OccurrenceId);
        Assert.Single(await fixture.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));

        var text = string.Join('\n', fixture.Logs.Lines);
        Assert.DoesNotContain(issued.Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain(WebhookTokens.Hash(issued.Token), text, StringComparison.Ordinal);
        var stored = await fixture.Connections.GetByAgentAsync(fixture.InstanceId);
        Assert.Equal(WebhookCredentialStatus.Active, stored!.WebhookStatus);
        Assert.NotEqual(issued.Token, stored.WebhookTokenHash);
    }

    [Fact]
    public async Task Invalid_bearer_payload_and_disallowed_definitions_create_no_occurrence()
    {
        var secretary = await FixtureAsync("secretary", 2);
        var issued = await secretary.IssueAsync();
        var valid = Encoding.UTF8.GetBytes(OrderPlacedPayload.Build("evt-1", "1001"));
        Assert.Equal(OrderPlacedAdmissionKind.Unauthorized, (await secretary.Webhook.AdmitAsync(issued.WebhookKey, "wrong-token", valid)).Kind);
        Assert.Equal(OrderPlacedAdmissionKind.Unauthorized, (await secretary.Webhook.AdmitAsync(Guid.NewGuid(), issued.Token, valid)).Kind);
        Assert.Equal(
            OrderPlacedAdmissionKind.Invalid,
            (await secretary.Webhook.AdmitAsync(
                issued.WebhookKey,
                issued.Token,
                """{"sourceEventId":"evt-1","orderReference":"1001","tools":["browser.act"]}"""u8.ToArray())).Kind);
        Assert.Empty(await secretary.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));

        await secretary.Service.RevokeWebhookAsync(secretary.InstanceId);
        Assert.Equal(OrderPlacedAdmissionKind.Unauthorized, (await secretary.Webhook.AdmitAsync(issued.WebhookKey, issued.Token, valid)).Kind);
        var rotated = await secretary.IssueAsync();
        Assert.Equal(issued.WebhookKey, rotated.WebhookKey);
        Assert.NotEqual(issued.Token, rotated.Token);
        Assert.Equal(OrderPlacedAdmissionKind.Unauthorized, (await secretary.Webhook.AdmitAsync(issued.WebhookKey, issued.Token, valid)).Kind);
        Assert.Equal(OrderPlacedAdmissionKind.Admitted, (await secretary.Webhook.AdmitAsync(rotated.WebhookKey, rotated.Token, valid)).Kind);

        var v1 = await FixtureAsync("secretary", 1);
        var v1Token = await v1.IssueAsync();
        Assert.Equal(OrderPlacedAdmissionKind.NotAdmitted, (await v1.Webhook.AdmitAsync(v1Token.WebhookKey, v1Token.Token, valid)).Kind);
        Assert.Empty(await v1.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));

        var assistant = await FixtureAsync("general-assistant", 12);
        var assistantToken = await assistant.IssueAsync();
        Assert.Equal(
            OrderPlacedAdmissionKind.NotAdmitted,
            (await assistant.Webhook.AdmitAsync(assistantToken.WebhookKey, assistantToken.Token, valid)).Kind);
        Assert.Empty(await assistant.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));
    }

    [Fact]
    public async Task Pin_failure_creates_no_occurrence()
    {
        var missing = await FixtureAsync("secretary", 2, new MissingCatalog());
        var missingToken = await missing.IssueAsync();
        var body = Encoding.UTF8.GetBytes(OrderPlacedPayload.Build("evt-9", "1009"));
        var unavailable = await missing.Webhook.AdmitAsync(missingToken.WebhookKey, missingToken.Token, body);
        Assert.Equal(OrderPlacedAdmissionKind.ModelRejected, unavailable.Kind);
        Assert.Equal("model-unavailable", unavailable.Code);
        Assert.Empty(await missing.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));

        var textOnly = await FixtureAsync("secretary", 2, new ToolLessCatalog());
        var textToken = await textOnly.IssueAsync();
        var capability = await textOnly.Webhook.AdmitAsync(textToken.WebhookKey, textToken.Token, body);
        Assert.Equal(OrderPlacedAdmissionKind.ModelRejected, capability.Kind);
        Assert.Equal("model-capability-unsupported", capability.Code);
        Assert.Empty(await textOnly.Triggers.ListByDispositionAsync(OccurrenceRoutingDisposition.Pending, 10));
    }

    private static async Task<Fixture> FixtureAsync(string definitionId, int version, IModelCatalog? catalog = null)
    {
        var definitions = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        var definition = (await definitions.GetAsync(definitionId, version))!;
        var instances = new InMemoryAgentInstanceStore();
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
            false));
        var profiles = new LocalUserProfileService(memory, TimeProvider.System);
        var profile = await profiles.GetLocalProfileAsync();
        var connections = new InMemoryApplicationConnectionStore();
        await connections.SaveAsync(new ApplicationConnection(
            Guid.NewGuid(),
            instanceId,
            ApplicationConnectionKinds.NopCommerce,
            "Store",
            "http://127.0.0.1:5088",
            ["http://127.0.0.1:5088"],
            ApplicationConnectionStatus.Connected,
            instanceId,
            1,
            Now,
            Now,
            null), 0);
        var triggers = new InMemoryTriggerStore();
        var logs = new ListLogger();
        var webhook = new OrderPlacedWebhook(
            connections,
            triggers,
            new TriggerAdmissionGuard(instances, definitions, memory),
            new SystemIdGenerator(TimeProvider.System),
            TimeProvider.System,
            profiles,
            instances,
            definitions,
            catalog ?? ModelCatalogFactory.Synthetic(),
            logs);
        var service = new ApplicationConnectionService(
            connections,
            new SystemIdGenerator(TimeProvider.System),
            TimeProvider.System,
            instances);
        return new Fixture(instanceId, new TriggerOwner(instanceId, profile.ProfileId), connections, triggers, webhook, service, logs);
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

    private sealed record Fixture(
        Guid InstanceId,
        TriggerOwner Owner,
        InMemoryApplicationConnectionStore Connections,
        InMemoryTriggerStore Triggers,
        OrderPlacedWebhook Webhook,
        ApplicationConnectionService Service,
        ListLogger Logs)
    {
        public Task<WebhookCredential> IssueAsync() => Service.IssueWebhookAsync(InstanceId).AsTask();
    }

    private sealed class ListLogger : ILogger<OrderPlacedWebhook>
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

    private sealed class MissingCatalog : IModelCatalog
    {
        public string DefaultKey => "missing";

        public IReadOnlyList<ModelDescriptor> Models { get; } = [];

        public ModelDescriptor Default => throw new InvalidOperationException();

        public ModelDescriptor? Get(string key) => null;
    }

    private sealed class ToolLessCatalog : IModelCatalog
    {
        private readonly ModelDescriptor _model = new(
            "scripted-alpha",
            "Scripted Alpha",
            "primary-llm",
            "scripted-alpha",
            false,
            false,
            false,
            false,
            [],
            null);

        public string DefaultKey => _model.Key;

        public IReadOnlyList<ModelDescriptor> Models => [_model];

        public ModelDescriptor Default => _model;

        public ModelDescriptor? Get(string key) => key == _model.Key ? _model : null;
    }
}
