using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentCore.Api.Realtime;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AgentCore.Api.Tests;

public sealed class HttpDiagnosticTests
{
    [Fact]
    public async Task Expected_http_failures_omit_diagnostic_id()
    {
        await using var factory = new AgentCoreApiFactory();
        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var validation = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(Guid.Empty, "text"));
        Assert.Equal(HttpStatusCode.BadRequest, validation.StatusCode);
        await AssertNoDiagnosticIdAsync(validation);

        var missing = await client.GetAsync($"/api/v1/sessions/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await AssertNoDiagnosticIdAsync(missing);

        var host = factory.Services.GetRequiredService<SessionHost>();
        await host.DrainAsync();
        var shutdown = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, shutdown.StatusCode);
        await AssertNoDiagnosticIdAsync(shutdown);
    }

    [Fact]
    public async Task Persistence_503_returns_one_logged_diagnostic_id()
    {
        await using var factory = new SaveFailureApiFactory(AgentCoreErrors.Persistence("forced session save failure"));
        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, created.StatusCode);
        var body = await created.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("SessionPersistenceUnavailable", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("forced session save failure", json.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
        var diagnosticId = json.RootElement.GetProperty("diagnosticId").GetString();
        Assert.True(Guid.TryParse(diagnosticId, out _));
        var logged = factory.Logs.Where(line => line.Contains(diagnosticId!, StringComparison.Ordinal)).ToArray();
        var line = Assert.Single(logged);
        Assert.Contains("Persistent save failed.", line, StringComparison.Ordinal);
        Assert.Contains("AgentCoreException", line, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Persistence_503_keeps_a_runtime_diagnostic_id_without_a_second_log()
    {
        var known = Guid.Parse("019944af-0008-7000-8000-0000000000e1");
        await using var factory = new SaveFailureApiFactory(
            AgentCoreErrors.Persistence("forced session save failure", known));
        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, created.StatusCode);
        using var json = JsonDocument.Parse(await created.Content.ReadAsStringAsync());
        Assert.Equal(known.ToString("D"), json.RootElement.GetProperty("diagnosticId").GetString());
        Assert.DoesNotContain(factory.Logs, line => line.Contains(known.ToString("D"), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Unhandled_http_failure_returns_a_safe_500()
    {
        await using var factory = new SaveFailureApiFactory(new InvalidOperationException("secret-provider-body"));
        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        Assert.Equal(HttpStatusCode.InternalServerError, created.StatusCode);
        var body = await created.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        Assert.Equal("InternalError", json.RootElement.GetProperty("code").GetString());
        Assert.Equal("The request could not be completed.", json.RootElement.GetProperty("detail").GetString());
        Assert.DoesNotContain("secret-provider-body", body, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", body, StringComparison.OrdinalIgnoreCase);
        var diagnosticId = json.RootElement.GetProperty("diagnosticId").GetString();
        Assert.True(Guid.TryParse(diagnosticId, out _));
        var logged = factory.Logs.Where(line => line.Contains(diagnosticId!, StringComparison.Ordinal)).ToArray();
        var line = Assert.Single(logged);
        Assert.Contains("HTTP request failed.", line, StringComparison.Ordinal);
        Assert.Contains("InvalidOperationException", line, StringComparison.Ordinal);
    }

    private static async Task AssertNoDiagnosticIdAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("diagnosticId", body, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class SaveFailureApiFactory(Exception failure) : AgentCoreApiFactory
{
    private readonly DiagnosticLineLoggerProvider _logs = new();

    public IReadOnlyList<string> Logs => _logs.Lines;

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            var original = services.Single(item => item.ServiceType == typeof(IMemoryStore));
            services.Remove(original);
            services.AddSingleton<IMemoryStore>(provider =>
            {
                var inner = original.ImplementationFactory is not null
                    ? (IMemoryStore)original.ImplementationFactory(provider)
                    : (IMemoryStore)ActivatorUtilities.CreateInstance(provider, original.ImplementationType!);
                return new SaveFailureStore(inner, failure);
            });
        });
        builder.ConfigureLogging(logging => logging.AddProvider(_logs));
    }
}

internal sealed class SaveFailureStore(IMemoryStore inner, Exception failure) : IMemoryStore
{
    public ValueTask<SessionSnapshot?> LoadAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
        inner.LoadAsync(sessionId, cancellationToken);

    public ValueTask SaveAsync(SessionSnapshot snapshot, long expectedRevision, CancellationToken cancellationToken = default) =>
        throw failure;

    public ValueTask<IReadOnlyList<ConversationEntry>> ReadHistoryAsync(
        Guid sessionId,
        long afterEntrySequence,
        int limit,
        CancellationToken cancellationToken = default) =>
        inner.ReadHistoryAsync(sessionId, afterEntrySequence, limit, cancellationToken);

    public ValueTask<UserProfile?> LoadProfileAsync(Guid profileId, CancellationToken cancellationToken = default) =>
        inner.LoadProfileAsync(profileId, cancellationToken);

    public ValueTask SaveProfileAsync(UserProfile profile, long expectedRevision, CancellationToken cancellationToken = default) =>
        inner.SaveProfileAsync(profile, expectedRevision, cancellationToken);

    public ValueTask RecoverCrashedSessionsAsync(CancellationToken cancellationToken = default) =>
        inner.RecoverCrashedSessionsAsync(cancellationToken);
}
