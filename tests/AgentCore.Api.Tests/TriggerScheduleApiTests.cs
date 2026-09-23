using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Triggers;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class TriggerScheduleApiTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public TriggerScheduleApiTests(AgentCoreApiFactory factory) => _factory = factory;

    [Fact]
    public async Task List_and_cancel_are_owner_scoped_and_hide_scheduler_internals()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var anonymous = _factory.CreateClient();
        var examiner = await CreateAsync(client, "examiner", 1);
        var other = await CreateAsync(client, "general-assistant", null);
        var unauthorized = await anonymous.GetAsync($"/api/v2/sessions/{examiner.SessionId}/triggers");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        var owner = await OwnerAsync(examiner.SessionId);
        var due = new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
        var created = await _factory.Services.GetRequiredService<ITriggerRegistrationService>().CreateAsync(
            new TriggerRegistrationDraft(
                owner,
                "Call John",
                new OneShotSchedule(due, "UTC", new DateOnly(2026, 9, 24), new TimeOnly(9, 0)),
                due,
                null,
                TriggerAuthorizationOrigin.CurrentUserTurn,
                Guid.Parse(examiner.SessionId),
                null));

        var listed = await client.GetAsync($"/api/v2/sessions/{examiner.SessionId}/triggers");
        listed.EnsureSuccessStatusCode();
        var body = await listed.Content.ReadAsStringAsync();
        Assert.DoesNotContain("provenance", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evidence", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("claim", body, StringComparison.OrdinalIgnoreCase);
        using var document = JsonDocument.Parse(body);
        var item = document.RootElement.GetProperty("items").EnumerateArray().Single();
        Assert.Equal("Call John", item.GetProperty("intent").GetString());
        Assert.Equal("active", item.GetProperty("status").GetString());
        Assert.Equal("UTC", item.GetProperty("timeZone").GetString());
        Assert.Equal("Once on 2026-09-24 at 09:00", item.GetProperty("schedule").GetString());
        Assert.Equal(created.Revision, item.GetProperty("revision").GetInt64());

        var hidden = await client.GetFromJsonAsync<TriggerScheduleListResponse>($"/api/v2/sessions/{other.SessionId}/triggers");
        Assert.Empty(hidden!.Items);
        var cross = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{other.SessionId}/triggers/{created.RegistrationId}/cancel",
            new CancelTriggerRequest(created.Revision));
        Assert.Equal(HttpStatusCode.NotFound, cross.StatusCode);

        var guessed = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{examiner.SessionId}/triggers/{Guid.NewGuid()}/cancel",
            new CancelTriggerRequest(created.Revision));
        Assert.Equal(HttpStatusCode.NotFound, guessed.StatusCode);

        var stale = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{examiner.SessionId}/triggers/{created.RegistrationId}/cancel",
            new CancelTriggerRequest(created.Revision + 5));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

        var sameOwner = await CreateAsync(client, "examiner", 1);
        var visible = await client.GetFromJsonAsync<TriggerScheduleListResponse>($"/api/v2/sessions/{sameOwner.SessionId}/triggers");
        Assert.Contains(visible!.Items, row => row.RegistrationId == created.RegistrationId.ToString());

        var cancelled = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{sameOwner.SessionId}/triggers/{created.RegistrationId}/cancel",
            new CancelTriggerRequest(created.Revision));
        cancelled.EnsureSuccessStatusCode();
        var saved = await cancelled.Content.ReadFromJsonAsync<TriggerScheduleResponse>();
        Assert.Equal("cancelled", saved!.Status);
        Assert.Equal(created.Revision + 1, saved.Revision);
    }

    [Fact]
    public async Task Schedule_remains_visible_after_sqlite_restart()
    {
        var db = Path.Combine(Path.GetTempPath(), $"agent-core-triggers-{Guid.NewGuid():N}.db");
        try
        {
            string sessionId;
            string registrationId;
            await using (var first = new DurableSqliteHostFactory(db))
            {
                var client = TestOwnerCapability.CreateOwnerClient(first);
                var session = await CreateAsync(client, "examiner", 1);
                sessionId = session.SessionId;
                var owner = await OwnerAsync(first.Services, sessionId);
                var due = new DateTimeOffset(2026, 9, 24, 9, 0, 0, TimeSpan.Zero);
                var created = await first.Services.GetRequiredService<ITriggerRegistrationService>().CreateAsync(
                    new TriggerRegistrationDraft(
                        owner,
                        "After restart",
                        new OneShotSchedule(due, "UTC"),
                        due,
                        null,
                        TriggerAuthorizationOrigin.CurrentUserTurn,
                        Guid.Parse(sessionId),
                        null));
                registrationId = created.RegistrationId.ToString();
                var before = await client.GetFromJsonAsync<TriggerScheduleListResponse>($"/api/v2/sessions/{sessionId}/triggers");
                Assert.Contains(before!.Items, item => item.RegistrationId == registrationId);
            }

            SqliteConnection.ClearAllPools();
            await using var second = new DurableSqliteHostFactory(db);
            var reopened = TestOwnerCapability.CreateOwnerClient(second);
            var listed = await reopened.GetFromJsonAsync<TriggerScheduleListResponse>($"/api/v2/sessions/{sessionId}/triggers");
            Assert.Contains(listed!.Items, item => item.RegistrationId == registrationId && item.Intent == "After restart");
        }
        finally
        {
            using var primary = new SqliteConnection($"Data Source={db}");
            SqliteConnection.ClearPool(primary);
            File.Delete(db);
        }
    }

    private async Task<TriggerOwner> OwnerAsync(string sessionId) =>
        await OwnerAsync(_factory.Services, sessionId);

    private static async Task<TriggerOwner> OwnerAsync(IServiceProvider services, string sessionId)
    {
        var snapshot = await services.GetRequiredService<SessionManager>().GetAsync(Guid.Parse(sessionId));
        return new TriggerOwner(snapshot.AgentInstanceId!.Value, snapshot.ProfileId!.Value);
    }

    private static async Task<SessionViewResponse> CreateAsync(HttpClient client, string agentId, int? version)
    {
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(agentId, version, "text"));
        created.EnsureSuccessStatusCode();
        return (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
    }
}
