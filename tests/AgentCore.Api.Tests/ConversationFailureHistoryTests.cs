using System.Net.Http.Json;
using AgentCore.Application.Ports;
using AgentCore.Contracts.Http;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace AgentCore.Api.Tests;

public sealed class ConversationFailureHistoryTests
{
    [Fact]
    public async Task History_returns_the_safe_reference_and_hides_exception_text()
    {
        await using var factory = new AgentCoreApiFactory();
        var client = TestOwnerCapability.CreateOwnerClient(factory);
        var created = await client.PostAsJsonAsync("/api/v2/sessions", new CreateSessionRequest(TestInstances.Create(client, "examiner", 1), "text"));
        created.EnsureSuccessStatusCode();
        var session = (await created.Content.ReadFromJsonAsync<SessionViewResponse>())!;
        var sessionId = Guid.Parse(session.SessionId);
        var store = factory.Services.GetRequiredService<IMemoryStore>();
        var snapshot = await store.LoadAsync(sessionId);
        Assert.NotNull(snapshot);
        var diagnosticId = Guid.Parse("019944af-00d7-7000-8000-0000000000d1");
        var correlationId = Guid.Parse("019944af-00d7-7000-8000-0000000000c1");
        var reference = new FailureReference(diagnosticId, "provider", "Unavailable", correlationId);
        var now = snapshot.UpdatedAt;
        var failed = new ConversationEntry(
            Guid.Parse("019944af-00d7-7000-8000-0000000000e1"),
            snapshot.Entries.Count + 1,
            null,
            ConversationRole.Assistant,
            "The response failed.",
            Guid.Parse("019944af-00d7-7000-8000-0000000000e2"),
            EntryStatus.Failed,
            SessionMode.Text,
            0,
            0,
            now,
            Failure: reference);
        var next = snapshot with
        {
            Revision = snapshot.Revision + 1,
            Entries = [.. snapshot.Entries, failed],
            UpdatedAt = now
        };
        await store.SaveAsync(next, snapshot.Revision);

        var raw = await client.GetStringAsync($"/api/v1/sessions/{session.SessionId}/messages?limit=20");
        Assert.Contains(diagnosticId.ToString("D"), raw, StringComparison.Ordinal);
        Assert.Contains("Unavailable", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-provider-body", raw, StringComparison.Ordinal);
        Assert.DoesNotContain("stack", raw, StringComparison.OrdinalIgnoreCase);
        var page = await client.GetFromJsonAsync<HistoryPageResponse>($"/api/v1/sessions/{session.SessionId}/messages?limit=20");
        var item = Assert.Single(page!.Items, entry => entry.Status == "failed");
        Assert.Equal(diagnosticId.ToString("D"), item.Failure!.DiagnosticId);
        Assert.Equal(correlationId.ToString("D"), item.Failure.CorrelationId);
        Assert.Equal("provider", item.Failure.Category);
        Assert.Equal("Unavailable", item.Failure.Code);
    }
}
