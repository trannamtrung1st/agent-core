using System.Text;
using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Admin;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class AdminKnowledgeBindingTests
{
    [Fact]
    public async Task ValidateDraft_accepts_a_legacy_knowledge_path()
    {
        var fixture = await CreateFixtureAsync(resourcePath: null);
        await BindAsync(fixture, "knowledge/policy", AgentDefinitionResourceKind.Knowledge, "text/markdown", "policy");

        var result = await fixture.Validation.ValidateDraftAsync(fixture.Draft.DraftId, CancellationToken.None);

        Assert.DoesNotContain(result.Findings, finding => finding.Code.Contains("knowledge", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidateDraft_accepts_an_explicit_knowledge_path()
    {
        var fixture = await CreateFixtureAsync("knowledge/refund-policy.md");
        await BindAsync(
            fixture,
            "knowledge/refund-policy.md",
            AgentDefinitionResourceKind.Knowledge,
            "text/markdown",
            "refunds");

        var result = await fixture.Validation.ValidateDraftAsync(fixture.Draft.DraftId, CancellationToken.None);

        Assert.DoesNotContain(result.Findings, finding => finding.Code.Contains("knowledge", StringComparison.Ordinal));
        var stored = await fixture.Admin.GetDraftAsync(fixture.Draft.DraftId, CancellationToken.None);
        Assert.Equal("knowledge/refund-policy.md", stored!.Candidate.Environment!.KnowledgeList[0].ResourcePath);
    }

    [Fact]
    public async Task ValidateDraft_reports_a_missing_explicit_path_without_rewriting_the_source()
    {
        var fixture = await CreateFixtureAsync("knowledge/missing.md");

        var result = await fixture.Validation.ValidateDraftAsync(fixture.Draft.DraftId, CancellationToken.None);

        var finding = Assert.Single(result.Findings, item => item.Code == "missing_knowledge_resource");
        Assert.Equal("environment.knowledgeSources[0].resourcePath", finding.Field);
        var stored = await fixture.Admin.GetDraftAsync(fixture.Draft.DraftId, CancellationToken.None);
        Assert.Equal("knowledge/missing.md", stored!.Candidate.Environment!.KnowledgeList[0].ResourcePath);
        Assert.Equal("policy", stored.Candidate.Environment.KnowledgeList[0].Identity);
    }

    [Fact]
    public async Task ValidateDraft_rejects_a_binding_to_the_wrong_kind()
    {
        var fixture = await CreateFixtureAsync("references/policy.md");
        await BindAsync(fixture, "references/policy.md", AgentDefinitionResourceKind.Reference, "text/plain", "notes");

        var result = await fixture.Validation.ValidateDraftAsync(fixture.Draft.DraftId, CancellationToken.None);

        var finding = Assert.Single(result.Findings, item => item.Code == "wrong_knowledge_resource_kind");
        Assert.Equal("environment.knowledgeSources[0].resourcePath", finding.Field);
    }

    [Fact]
    public async Task ValidateDraft_rejects_non_textual_knowledge_media()
    {
        var fixture = await CreateFixtureAsync("knowledge/policy.pdf");
        await BindAsync(
            fixture,
            "knowledge/policy.pdf",
            AgentDefinitionResourceKind.Knowledge,
            "application/pdf",
            "%PDF");

        var result = await fixture.Validation.ValidateDraftAsync(fixture.Draft.DraftId, CancellationToken.None);

        Assert.Contains(result.Findings, finding => finding.Code == "non_textual_knowledge_resource");
    }

    private static async Task<Fixture> CreateFixtureAsync(string? resourcePath)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-28T00:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids) { EventStore = new InMemoryAdminEventStore(ids) };
        var content = new InMemoryDefinitionResourceContentStore();
        var resources = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        var candidate = AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Examiner) with
        {
            Environment = new RoleEnvironment(
                KnowledgeSources: [new KnowledgeSourceRef("policy", "Policy", "policy@demo", resourcePath)])
        };
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "examiner",
                candidate,
                DefinitionDraftSourceKind.New,
                null,
                clock.GetUtcNow()),
            CancellationToken.None);
        var lifecycle = new AgentDefinitionLifecycleService(
            new EmptyBuiltIns(),
            admin,
            SyntheticProviderAliases.Default,
            clock,
            ids);
        var validation = new AgentDefinitionDraftValidationService(
            lifecycle,
            new AgentDefinitionResourceService(admin, resources, content, clock),
            SyntheticProviderAliases.Default,
            TestModelCatalogs.Synthetic(),
            ToolConfigurationGates.AllowAll);
        return new Fixture(admin, resources, content, validation, draft, clock);
    }

    private static async Task BindAsync(
        Fixture fixture,
        string path,
        AgentDefinitionResourceKind kind,
        string mediaType,
        string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var hash = DefinitionResourceContentHasher.ComputeSha256Hex(bytes);
        await fixture.Content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
        var current = await fixture.Admin.GetDraftAsync(fixture.Draft.DraftId, CancellationToken.None);
        await fixture.Resources.UpsertDraftResourceAsync(
            new AgentDefinitionDraftResourceUpsert(
                fixture.Draft.DraftId,
                current!.Revision,
                null,
                path,
                kind,
                mediaType,
                hash,
                bytes.Length,
                fixture.Clock.GetUtcNow()),
            CancellationToken.None);
    }

    private sealed record Fixture(
        InMemoryAgentDefinitionAdminStore Admin,
        InMemoryAgentDefinitionResourceAdminStore Resources,
        InMemoryDefinitionResourceContentStore Content,
        AgentDefinitionDraftValidationService Validation,
        AgentDefinitionDraft Draft,
        FakeTimeProvider Clock);

    private sealed class EmptyBuiltIns : IBuiltInAgentDefinitionStore
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
