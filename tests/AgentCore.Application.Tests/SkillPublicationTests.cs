using System.Security.Cryptography;
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

public sealed class SkillPublicationTests
{
    [Fact]
    public async Task Publish_and_fork_keep_skills_without_granting_chat()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-30T12:00:00Z"));
        var ids = new SystemIdGenerator(clock);
        var admin = new InMemoryAgentDefinitionAdminStore(ids) { EventStore = new InMemoryAdminEventStore(ids) };
        var content = new InMemoryDefinitionResourceContentStore();
        var resourcesStore = new InMemoryAgentDefinitionResourceAdminStore(admin, content, ids);
        var builtIns = new EmptyBuiltIns();
        var lifecycle = new AgentDefinitionLifecycleService(
            builtIns,
            admin,
            SyntheticProviderAliases.Default,
            clock,
            ids);
        var resources = new AgentDefinitionResourceService(admin, resourcesStore, content, clock, lifecycle);
        var validation = new AgentDefinitionDraftValidationService(
            lifecycle,
            resources,
            SyntheticProviderAliases.Default,
            TestModelCatalogs.Synthetic(),
            ToolConfigurationGates.AllowAll);
        var evaluation = new AgentDefinitionDraftEvaluationService(
            lifecycle,
            resources,
            new InMemoryDefinitionDraftEvaluationStore(admin),
            content,
            AdminEvaluationTestSupport.CreateRunner(),
            clock);
        var diff = new AgentDefinitionDraftDiffService(lifecycle, resources, builtIns, admin);
        var publish = new AgentDefinitionDraftPublishService(lifecycle, validation, evaluation, diff, ids);
        var allowlist = new[] { ToolCatalog.WorkspaceRead };
        var skills = new[]
        {
            new SkillSpec(
                "refund.handle",
                "Refund handling",
                "Order refunds.",
                "Confirm the order before any refund.",
                ["refund"],
                [SkillCapabilities.ChatRespond, ToolCatalog.WorkspaceRead],
                ["notes/refund.md"]),
            new SkillSpec(
                "order.lookup",
                "Order lookup",
                "",
                "Read the order summary.",
                ["order"],
                [ToolCatalog.WorkspaceRead],
                [])
        };
        var candidate = AgentDefinitionCandidate.FromDefinition(SampleDefinitions.Support) with
        {
            DefinitionId = "skill-guide",
            Environment = RoleEnvironment.Empty with { ToolAllowlist = allowlist },
            Skills = skills
        };
        var draft = await admin.CreateDraftAsync(
            new AgentDefinitionDraftCreate(
                "skill-guide",
                candidate,
                DefinitionDraftSourceKind.New,
                null,
                clock.GetUtcNow()),
            CancellationToken.None);

        var blocked = await validation.ValidateDraftAsync(draft.DraftId, CancellationToken.None);
        Assert.Contains(blocked.Findings, finding => finding.Code == "missing_skill_resource");

        await BindAsync(resourcesStore, content, draft, clock);
        draft = (await admin.GetDraftAsync(draft.DraftId, CancellationToken.None))!;
        var ready = await validation.ValidateDraftAsync(draft.DraftId, CancellationToken.None);
        Assert.False(ready.HasBlockingFindings);

        var publication = await publish.PublishDraftAsync(draft.DraftId, draft.Revision, CancellationToken.None);
        Assert.Equal(allowlist, publication.Payload.Environment!.ToolList);
        Assert.Equal(["refund.handle", "order.lookup"], publication.Payload.SkillList.Select(skill => skill.Id).ToArray());
        Assert.False(ToolRegistry.TryGet(SkillCapabilities.ChatRespond, out _));

        var forked = await lifecycle.ForkDraftAsync(
            publication.DefinitionId,
            publication.Version,
            DefinitionDraftSourceKind.ForkDurable,
            CancellationToken.None);
        Assert.Equal(Shapes(publication.Payload.SkillList), Shapes(forked.Candidate.SkillList));
        Assert.Equal(allowlist, forked.Candidate.Environment!.ToolList);
    }

    private static string[] Shapes(IReadOnlyList<SkillSpec> skills) =>
        skills.Select(skill =>
                $"{skill.Id}|{skill.Name}|{skill.Description}|{skill.Procedure}|{string.Join(",", skill.ActivationKeywords)}|{string.Join(",", skill.RequiredCapabilities)}|{string.Join(",", skill.ResourcePaths)}")
            .ToArray();

    private static async Task BindAsync(
        InMemoryAgentDefinitionResourceAdminStore resources,
        InMemoryDefinitionResourceContentStore content,
        AgentDefinitionDraft draft,
        FakeTimeProvider clock)
    {
        var bytes = Encoding.UTF8.GetBytes("refund notes");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        await content.StoreVerifiedAsync(hash, bytes, CancellationToken.None);
        await resources.UpsertDraftResourceAsync(
            new AgentDefinitionDraftResourceUpsert(
                draft.DraftId,
                draft.Revision,
                null,
                "notes/refund.md",
                AgentDefinitionResourceKind.Reference,
                "text/markdown",
                hash,
                bytes.Length,
                clock.GetUtcNow()),
            CancellationToken.None);
    }

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
