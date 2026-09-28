using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AdminDefinitionNewDraftTests
{
    [Fact]
    public async Task CreateNewDraftAsync_stores_a_persistence_valid_starter_on_an_empty_catalog()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids) { EventStore = new InMemoryAdminEventStore(ids) };
        var lifecycle = new AgentDefinitionLifecycleService(
            new EmptyBuiltInStore(),
            admin,
            SyntheticProviderAliases.Default,
            clock,
            ids);

        var draft = await lifecycle.CreateNewDraftAsync("p76-new-agent", CancellationToken.None);

        Assert.Empty(await admin.ListPublicationsAsync(cancellationToken: CancellationToken.None));
        Assert.Equal(DefinitionDraftSourceKind.New, draft.SourceKind);
        Assert.Null(draft.SourceVersion);
        Assert.Equal("p76-new-agent", draft.Candidate.DefinitionId);
        Assert.Equal("primary-llm", draft.Candidate.ProviderPreferences.LanguageModel);
        Assert.Null(draft.Candidate.ProviderPreferences.SpeechRecognizer);
        Assert.Null(draft.Candidate.ProviderPreferences.SpeechSynthesizer);
        Assert.DoesNotContain("OPENROUTER_API_KEY", draft.Candidate.SystemInstructions, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", draft.Candidate.SystemInstructions, StringComparison.Ordinal);

        var reopened = await lifecycle.GetDraftAsync(draft.DraftId, CancellationToken.None);
        Assert.Equal(draft.Candidate.SystemInstructions, reopened.Candidate.SystemInstructions);
        Assert.Equal(draft.Candidate.Identity, reopened.Candidate.Identity);
    }

    [Fact]
    public async Task CreateNewDraftAsync_rejects_an_invalid_definition_id()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids) { EventStore = new InMemoryAdminEventStore(ids) };
        var lifecycle = new AgentDefinitionLifecycleService(
            new EmptyBuiltInStore(),
            admin,
            SyntheticProviderAliases.Default,
            clock,
            ids);

        var error = await Assert.ThrowsAsync<AgentCoreException>(() =>
            lifecycle.CreateNewDraftAsync("Not Valid", CancellationToken.None).AsTask());

        Assert.Equal("ValidationError", error.Code);
        Assert.Empty(await admin.ListDraftsAsync(CancellationToken.None));
    }

    private sealed class EmptyBuiltInStore : IBuiltInAgentDefinitionStore
    {
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([]);

        public ValueTask<AgentDefinition?> GetAsync(
            string id,
            int? version = null,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<AgentDefinition?>(null);
    }
}
