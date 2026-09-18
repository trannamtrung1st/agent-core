using System.Net;
using System.Net.Http.Json;
using AgentCore.Api.Realtime;
using AgentCore.Contracts.Http;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class SessionCatalogApiTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public SessionCatalogApiTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Owner_capability_is_required_for_catalog_and_survives_as_hashed_grant()
    {
        var client = _factory.CreateClient();
        var denied = await client.GetAsync("/api/v2/sessions");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);

        var issued = await client.PostAsync("/api/v1/local/owner-capability", null);
        issued.EnsureSuccessStatusCode();
        var capability = await issued.Content.ReadFromJsonAsync<OwnerCapabilityResponse>();
        Assert.False(string.IsNullOrWhiteSpace(capability!.Token));

        client.DefaultRequestHeaders.TryAddWithoutValidation(OwnerCapabilityHeaders.Name, capability.Token);
        var empty = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions");
        Assert.NotNull(empty);
    }

    [Fact]
    public async Task V2_create_list_rename_archive_reopen_and_durable_delete()
    {
        var client = OwnerClient();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.Equal("examiner", view!.AgentId);
        Assert.Equal(1, view.AgentVersion);

        var renamed = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{view.SessionId}/rename",
            new RenameSessionRequest("Planning notes"));
        renamed.EnsureSuccessStatusCode();
        var item = await renamed.Content.ReadFromJsonAsync<SessionCatalogItemResponse>();
        Assert.Equal("Planning notes", item!.Title);
        Assert.True(item.WorkspaceOwned);

        var listed = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions");
        Assert.Contains(listed!.Items, row => row.SessionId == view.SessionId && row.Title == "Planning notes");

        var archived = await client.PostAsync($"/api/v2/sessions/{view.SessionId}/archive", null);
        archived.EnsureSuccessStatusCode();
        var hidden = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions");
        Assert.DoesNotContain(hidden!.Items, row => row.SessionId == view.SessionId);
        var shown = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions?includeArchived=true");
        Assert.Contains(shown!.Items, row => row.SessionId == view.SessionId && row.Archived);

        var unarchived = await client.PostAsync($"/api/v2/sessions/{view.SessionId}/unarchive", null);
        unarchived.EnsureSuccessStatusCode();
        var reopened = await client.PostAsync($"/api/v2/sessions/{view.SessionId}/reopen", null);
        reopened.EnsureSuccessStatusCode();
        var epoch = await reopened.Content.ReadFromJsonAsync<SessionCatalogItemResponse>();
        Assert.Equal(0, epoch!.RuntimeEpoch);

        var deleted = await client.DeleteAsync($"/api/v2/sessions/{view.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        var again = await client.DeleteAsync($"/api/v2/sessions/{view.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        var missing = await client.GetAsync($"/api/v2/sessions/{view.SessionId}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Support_and_compliance_sessions_are_distinct_catalog_rows()
    {
        var client = OwnerClient();
        var support = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("customer-support", 1, "text"));
        var compliance = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("compliance", 1, "text"));
        support.EnsureSuccessStatusCode();
        compliance.EnsureSuccessStatusCode();
        var supportView = await support.Content.ReadFromJsonAsync<SessionViewResponse>();
        var complianceView = await compliance.Content.ReadFromJsonAsync<SessionViewResponse>();
        Assert.Equal("customer-support", supportView!.AgentId);
        Assert.Equal("compliance", complianceView!.AgentId);
        var listed = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions");
        Assert.Contains(listed!.Items, row => row.SessionId == supportView.SessionId && row.AgentId == "customer-support");
        Assert.Contains(listed.Items, row => row.SessionId == complianceView.SessionId && row.AgentId == "compliance");
    }

    [Fact]
    public async Task V1_delete_leaves_labeled_ended_catalog_row()
    {
        var client = OwnerClient();
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var ended = await client.DeleteAsync($"/api/v1/sessions/{view!.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);
        var listed = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions");
        Assert.Contains(listed!.Items, row => row.SessionId == view.SessionId && row.Ended && row.Status == "ended");
    }

    [Fact]
    public async Task V2_durable_delete_removes_ended_catalog_row()
    {
        var client = OwnerClient();
        var created = await client.PostAsJsonAsync("/api/v1/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var ended = await client.DeleteAsync($"/api/v1/sessions/{view!.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, ended.StatusCode);

        var listed = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions");
        var row = Assert.Single(listed!.Items, item => item.SessionId == view.SessionId);
        Assert.True(row.Ended);

        var deleted = await client.DeleteAsync($"/api/v2/sessions/{view.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        var after = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions");
        Assert.DoesNotContain(after!.Items, item => item.SessionId == view.SessionId);
    }

    [Fact]
    public async Task Archive_cancels_live_runtime()
    {
        var client = OwnerClient();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress!, "/hubs/session"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    TestOwnerCapability.Apply(options, _factory.Services);
                })
            .AddMessagePackProtocol()
            .Build();
        await hub.StartAsync();
        var attached = await hub.InvokeAsync<CommandAck>(
            "Attach",
            new ClientCommand<AttachPayload>
            {
                ProtocolVersion = 1,
                SessionId = view!.SessionId,
                EventId = Guid.NewGuid().ToString(),
                Sequence = 0,
                Type = "session.attach",
                Payload = new AttachPayload()
            });
        Assert.True(attached.Accepted, attached.Error?.Message);
        Assert.NotNull(_factory.Services.GetRequiredService<SessionHost>().LiveSnapshot(Guid.Parse(view.SessionId)));
        var archived = await client.PostAsync($"/api/v2/sessions/{view.SessionId}/archive", null);
        archived.EnsureSuccessStatusCode();
        Assert.Null(_factory.Services.GetRequiredService<SessionHost>().LiveSnapshot(Guid.Parse(view.SessionId)));
    }

    [Fact]
    public async Task V2_delete_removes_attached_session_without_stale_revision()
    {
        var client = OwnerClient();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        await using var hub = CreateHubConnection();
        await hub.StartAsync();
        var attached = await AttachAsync(hub, view!.SessionId);
        Assert.True(attached.Accepted, attached.Error?.Message);
        var host = _factory.Services.GetRequiredService<SessionHost>();
        var sessionId = Guid.Parse(view.SessionId);
        Assert.NotNull(host.LiveSnapshot(sessionId));

        var deleted = await client.DeleteAsync($"/api/v2/sessions/{view.SessionId}");
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(host.LiveSnapshot(sessionId));
        var missing = await client.GetAsync($"/api/v2/sessions/{view.SessionId}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Attach_without_owner_capability_is_rejected()
    {
        var client = OwnerClient();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        await using var hub = new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress!, "/hubs/session"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                })
            .AddMessagePackProtocol()
            .Build();
        await hub.StartAsync();
        var ack = await hub.InvokeAsync<CommandAck>(
            "Attach",
            new ClientCommand<AttachPayload>
            {
                ProtocolVersion = 1,
                SessionId = view!.SessionId,
                EventId = Guid.NewGuid().ToString(),
                Sequence = 0,
                Type = "session.attach",
                Payload = new AttachPayload()
            });
        Assert.False(ack.Accepted);
        Assert.Equal("Unauthorized", ack.Error?.Code);
    }

    [Fact]
    public async Task Switching_hub_attachments_preserves_catalog_order()
    {
        var client = OwnerClient();
        var firstCreated = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var first = await firstCreated.Content.ReadFromJsonAsync<SessionViewResponse>();
        var secondCreated = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var second = await secondCreated.Content.ReadFromJsonAsync<SessionViewResponse>();

        await client.PostAsJsonAsync($"/api/v2/sessions/{first!.SessionId}/rename", new RenameSessionRequest("First session"));
        await client.PostAsJsonAsync($"/api/v2/sessions/{second!.SessionId}/rename", new RenameSessionRequest("Second session"));

        var before = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions");
        var orderBefore = before!.Items.Select(item => item.SessionId).ToArray();
        Assert.Equal(second.SessionId, orderBefore[0]);
        Assert.Equal(first.SessionId, orderBefore[1]);

        await using var hub = CreateHubConnection();
        await hub.StartAsync();
        var attachedFirst = await AttachAsync(hub, first.SessionId);
        Assert.True(attachedFirst.Accepted, attachedFirst.Error?.Message);

        var duringFirst = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions");
        Assert.Equal(orderBefore, duringFirst!.Items.Select(item => item.SessionId).ToArray());

        await hub.StopAsync();
        var reopened = await client.PostAsync($"/api/v2/sessions/{second.SessionId}/reopen", null);
        reopened.EnsureSuccessStatusCode();

        await hub.StartAsync();
        var attachedSecond = await AttachAsync(hub, second.SessionId);
        Assert.True(attachedSecond.Accepted, attachedSecond.Error?.Message);

        var afterSwitch = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions");
        Assert.Equal(orderBefore, afterSwitch!.Items.Select(item => item.SessionId).ToArray());
    }

    [Fact]
    public async Task Deactivate_pauses_without_archive_and_is_idempotent()
    {
        var client = OwnerClient();
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var view = await created.Content.ReadFromJsonAsync<SessionViewResponse>();
        var first = await client.PostAsync($"/api/v2/sessions/{view!.SessionId}/deactivate", null);
        first.EnsureSuccessStatusCode();
        var item = await first.Content.ReadFromJsonAsync<SessionCatalogItemResponse>();
        Assert.Equal("paused", item!.Status);
        Assert.Equal("manual", item.PauseReason);
        Assert.False(item.Archived);
        Assert.False(item.Ended);
        Assert.Equal(1, item.RuntimeEpoch);
        var detail = await client.GetFromJsonAsync<SessionViewResponse>($"/api/v2/sessions/{view.SessionId}");
        Assert.Equal("paused", detail!.Status);
        Assert.Equal("manual", detail.PauseReason);
        await using (var hub = CreateHubConnection())
        {
            await hub.StartAsync();
            var blocked = await AttachAsync(hub, view.SessionId);
            Assert.False(blocked.Accepted);
            Assert.Equal("SessionPaused", blocked.Error?.Code);
        }

        var second = await client.PostAsync($"/api/v2/sessions/{view.SessionId}/deactivate", null);
        second.EnsureSuccessStatusCode();
        var again = await second.Content.ReadFromJsonAsync<SessionCatalogItemResponse>();
        Assert.Equal("paused", again!.Status);
        Assert.Equal("manual", again.PauseReason);
        Assert.Equal(1, again.RuntimeEpoch);
        var listed = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions");
        Assert.Contains(listed!.Items, row => row.SessionId == view.SessionId && row.Status == "paused" && row.PauseReason == "manual");

        var reopen = await client.PostAsync($"/api/v2/sessions/{view.SessionId}/reopen", null);
        reopen.EnsureSuccessStatusCode();
        var resumed = await reopen.Content.ReadFromJsonAsync<SessionCatalogItemResponse>();
        Assert.Null(resumed!.PauseReason);
        Assert.False(resumed.Ended);
        Assert.NotEqual("ended", resumed.Status);
    }

    [Fact]
    public async Task Knowledge_retrieve_is_role_scoped_and_includes_citation()
    {
        var client = OwnerClient();
        var support = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("customer-support", 1, "text"));
        var view = await support.Content.ReadFromJsonAsync<SessionViewResponse>();
        var ok = await client.GetFromJsonAsync<KnowledgeDocumentResponse>(
            $"/api/v2/sessions/{view!.SessionId}/knowledge/support-order-policy");
        Assert.Equal("support-order-policy@demo", ok!.Citation);
        Assert.False(string.IsNullOrWhiteSpace(ok.Content));

        var examiner = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest("examiner", 1, "text"));
        var other = await examiner.Content.ReadFromJsonAsync<SessionViewResponse>();
        var denied = await client.GetAsync($"/api/v2/sessions/{other!.SessionId}/knowledge/support-order-policy");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);

        var processDenied = await client.GetAsync($"/api/v2/sessions/{view.SessionId}/knowledge/..%2Fsecrets");
        Assert.True(processDenied.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound);
    }

    private HttpClient OwnerClient()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            OwnerCapabilityHeaders.Name,
            TestOwnerCapability.Token(_factory.Services));
        return client;
    }

    private HubConnection CreateHubConnection() =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(_factory.Server.BaseAddress!, "/hubs/session"),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler();
                    options.Transports = HttpTransportType.LongPolling;
                    TestOwnerCapability.Apply(options, _factory.Services);
                })
            .AddMessagePackProtocol()
            .Build();

    private static Task<CommandAck> AttachAsync(HubConnection hub, string sessionId) =>
        hub.InvokeAsync<CommandAck>(
            "Attach",
            new ClientCommand<AttachPayload>
            {
                ProtocolVersion = 1,
                SessionId = sessionId,
                EventId = Guid.NewGuid().ToString(),
                Sequence = 0,
                Type = "session.attach",
                Payload = new AttachPayload()
            });
}
