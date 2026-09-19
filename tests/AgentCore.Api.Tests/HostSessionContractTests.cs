using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Contracts.Http;
using Microsoft.Data.Sqlite;

namespace AgentCore.Api.Tests;

public sealed class HostSessionContractTests : IClassFixture<AgentCoreApiFactory>
{
    private readonly AgentCoreApiFactory _factory;

    public HostSessionContractTests(AgentCoreApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Host_create_persists_purpose_and_policy_without_public_leak()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync(
            "/api/v2/host/sessions",
            new HostCreateSessionRequest(
                "examiner",
                1,
                "text",
                Purpose: new HostSessionPurposeRequest("goal", "Finish the spoken sample", null),
                CompletionPolicy: new HostSessionCompletionPolicyRequest("advisory", false, true),
                MaxDurationSeconds: 3600));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        using var hostDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal("goal", hostDoc.RootElement.GetProperty("purpose").GetProperty("kind").GetString());
        Assert.Equal("Finish the spoken sample", hostDoc.RootElement.GetProperty("purpose").GetProperty("description").GetString());
        Assert.False(string.IsNullOrWhiteSpace(hostDoc.RootElement.GetProperty("purpose").GetProperty("deadlineAt").GetString()));
        Assert.False(hostDoc.RootElement.GetProperty("purpose").TryGetProperty("metadata", out _));
        Assert.Equal("advisory", hostDoc.RootElement.GetProperty("completionPolicy").GetProperty("agentCompletion").GetString());
        Assert.False(hostDoc.RootElement.GetProperty("completionPolicy").GetProperty("userCompletionAllowed").GetBoolean());
        var sessionId = hostDoc.RootElement.GetProperty("sessionId").GetString();

        using var publicDoc = JsonDocument.Parse(
            await (await client.GetAsync($"/api/v2/sessions/{sessionId}")).Content.ReadAsStringAsync());
        Assert.Equal("active", publicDoc.RootElement.GetProperty("lifecycleStatus").GetString());
        Assert.False(publicDoc.RootElement.TryGetProperty("purpose", out _));
        Assert.False(publicDoc.RootElement.TryGetProperty("completionPolicy", out _));
        Assert.False(publicDoc.RootElement.TryGetProperty("userCompletionAllowed", out _));
        Assert.False(publicDoc.RootElement.TryGetProperty("deadlineAt", out _));

        var catalog = await client.GetFromJsonAsync<SessionCatalogPageResponse>("/api/v2/sessions");
        var row = Assert.Single(catalog!.Items, item => item.SessionId == sessionId);
        using var catalogDoc = JsonDocument.Parse(JsonSerializer.Serialize(row));
        Assert.False(catalogDoc.RootElement.TryGetProperty("purpose", out _));
        Assert.False(catalogDoc.RootElement.TryGetProperty("completionPolicy", out _));

        using var hostGet = JsonDocument.Parse(
            await (await client.GetAsync($"/api/v2/host/sessions/{sessionId}")).Content.ReadAsStringAsync());
        Assert.Equal("advisory", hostGet.RootElement.GetProperty("completionPolicy").GetProperty("agentCompletion").GetString());
        Assert.Equal(
            hostDoc.RootElement.GetProperty("purpose").GetProperty("deadlineAt").GetString(),
            hostGet.RootElement.GetProperty("purpose").GetProperty("deadlineAt").GetString());
    }

    [Fact]
    public async Task Public_lifecycle_cannot_spoof_host_to_bypass_user_completion_policy()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var created = await client.PostAsJsonAsync(
            "/api/v2/host/sessions",
            new HostCreateSessionRequest(
                "examiner",
                1,
                "text",
                Purpose: new HostSessionPurposeRequest("goal", "Exam"),
                CompletionPolicy: new HostSessionCompletionPolicyRequest("advisory", false, true)));
        created.EnsureSuccessStatusCode();
        var host = await created.Content.ReadFromJsonAsync<HostSessionViewResponse>();

        var spoof = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{host!.SessionId}/lifecycle",
            new TransitionLifecycleRequest("completed", "host", "self-promote"));
        Assert.Equal(HttpStatusCode.Forbidden, spoof.StatusCode);
        using var spoofDoc = JsonDocument.Parse(await spoof.Content.ReadAsStringAsync());
        Assert.Equal("Forbidden", spoofDoc.RootElement.GetProperty("code").GetString());

        var systemSpoof = await client.PostAsJsonAsync(
            $"/api/v2/sessions/{host.SessionId}/lifecycle",
            new TransitionLifecycleRequest("expired", "system", "self-expire"));
        Assert.Equal(HttpStatusCode.Forbidden, systemSpoof.StatusCode);

        var hostComplete = await client.PostAsJsonAsync(
            $"/api/v2/host/sessions/{host.SessionId}/lifecycle",
            new TransitionLifecycleRequest("completed", "user", "validated submission"));
        hostComplete.EnsureSuccessStatusCode();
        var completed = await hostComplete.Content.ReadFromJsonAsync<HostSessionViewResponse>();
        Assert.Equal("completed", completed!.LifecycleStatus);
        Assert.Equal("ended", completed.Status);
        Assert.Equal("host", completed.LifecycleSource);
        Assert.Equal("validated submission", completed.LifecycleReason);

        using var publicDoc = JsonDocument.Parse(
            await (await client.GetAsync($"/api/v2/sessions/{host.SessionId}")).Content.ReadAsStringAsync());
        Assert.Equal("completed", publicDoc.RootElement.GetProperty("lifecycleStatus").GetString());
        Assert.False(publicDoc.RootElement.TryGetProperty("completionPolicy", out _));
    }

    [Fact]
    public async Task Host_rejects_ambiguous_deadline_and_raw_timespan_is_not_the_wire_unit()
    {
        var client = TestOwnerCapability.CreateOwnerClient(_factory);
        var both = await client.PostAsJsonAsync(
            "/api/v2/host/sessions",
            new HostCreateSessionRequest(
                "examiner",
                1,
                "text",
                Purpose: new HostSessionPurposeRequest(
                    "goal",
                    "Exam",
                    "2026-09-19T12:00:00.000Z"),
                MaxDurationSeconds: 60));
        Assert.Equal(HttpStatusCode.BadRequest, both.StatusCode);

        var tooLong = await client.PostAsJsonAsync(
            "/api/v2/host/sessions",
            new HostCreateSessionRequest(
                "examiner",
                1,
                "text",
                Purpose: new HostSessionPurposeRequest("goal"),
                MaxDurationSeconds: (long)TimeSpan.FromDays(31).TotalSeconds));
        Assert.Equal(HttpStatusCode.BadRequest, tooLong.StatusCode);

        var noTimezone = await client.PostAsJsonAsync(
            "/api/v2/host/sessions",
            new HostCreateSessionRequest(
                "examiner",
                1,
                "text",
                Purpose: new HostSessionPurposeRequest("goal", "Exam", "2026-09-19T12:00:00")));
        Assert.Equal(HttpStatusCode.BadRequest, noTimezone.StatusCode);
    }

    [Fact]
    public async Task Host_purpose_survives_sqlite_restart_and_stays_off_public_projections()
    {
        var db = Path.Combine(Path.GetTempPath(), $"agent-core-host-purpose-{Guid.NewGuid():N}.db");
        string sessionId;
        string deadline;
        try
        {
            await using (var first = new DurableSqliteHostFactory(db))
            {
                var client = TestOwnerCapability.CreateOwnerClient(first);
                var created = await client.PostAsJsonAsync(
                    "/api/v2/host/sessions",
                    new HostCreateSessionRequest(
                        "examiner",
                        1,
                        "text",
                        Purpose: new HostSessionPurposeRequest("goal", "Onboarding task"),
                        CompletionPolicy: new HostSessionCompletionPolicyRequest("advisory", false, true),
                        MaxDurationSeconds: 120));
                created.EnsureSuccessStatusCode();
                using var createdDoc = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
                sessionId = createdDoc.RootElement.GetProperty("sessionId").GetString()!;
                deadline = createdDoc.RootElement.GetProperty("purpose").GetProperty("deadlineAt").GetString()!;
            }

            await using var restarted = new DurableSqliteHostFactory(db);
            var after = TestOwnerCapability.CreateOwnerClient(restarted);
            using var hostDoc = JsonDocument.Parse(
                await (await after.GetAsync($"/api/v2/host/sessions/{sessionId}")).Content.ReadAsStringAsync());
            Assert.Equal("goal", hostDoc.RootElement.GetProperty("purpose").GetProperty("kind").GetString());
            Assert.Equal("Onboarding task", hostDoc.RootElement.GetProperty("purpose").GetProperty("description").GetString());
            Assert.Equal(deadline, hostDoc.RootElement.GetProperty("purpose").GetProperty("deadlineAt").GetString());
            Assert.Equal("advisory", hostDoc.RootElement.GetProperty("completionPolicy").GetProperty("agentCompletion").GetString());
            Assert.False(hostDoc.RootElement.GetProperty("completionPolicy").GetProperty("userCompletionAllowed").GetBoolean());

            using var publicDoc = JsonDocument.Parse(
                await (await after.GetAsync($"/api/v2/sessions/{sessionId}")).Content.ReadAsStringAsync());
            Assert.Equal("active", publicDoc.RootElement.GetProperty("lifecycleStatus").GetString());
            Assert.False(publicDoc.RootElement.TryGetProperty("purpose", out _));
            Assert.False(publicDoc.RootElement.TryGetProperty("completionPolicy", out _));
            Assert.False(publicDoc.RootElement.TryGetProperty("deadlineAt", out _));
        }
        finally
        {
            using var connection = new SqliteConnection($"Data Source={db}");
            SqliteConnection.ClearPool(connection);
            if (File.Exists(db))
            {
                File.Delete(db);
            }
        }
    }
}
