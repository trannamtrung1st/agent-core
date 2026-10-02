using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Memory;
using AgentCore.Application.Testing;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Domain.Triggers;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class SecretaryIdentityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly Guid InstanceId = Guid.Parse("019944af-00c1-7000-8000-0000000000a1");
    private static readonly Guid ProfileId = Guid.Parse("019944af-00c1-7000-8000-0000000000b1");
    private static readonly Guid SessionId = Guid.Parse("019944af-00c1-7000-8000-0000000000c1");

    private static readonly IReadOnlyDictionary<string, string> GeneralAssistantSha256 = new Dictionary<string, string>
    {
        ["general-assistant.json"] = "29de0311823e91ee6d81bc88bbb58e8f1fc8b914dd248df66d2d3c3e639ca4e4",
        ["general-assistant-v2.json"] = "6cf33455556c7a0f7d235bbe5b712c5300bfac6d026543b94d1f890632ddc327",
        ["general-assistant-v3.json"] = "aaafe3f3806e7677d5b66aeee75caba88227b2dcb59d1cf1b4e51d2f9804000d",
        ["general-assistant-v4.json"] = "7bcf853afa1db917e4dfa26bb5d1d51d9b4fd11c8857724734c84326b24b8090",
        ["general-assistant-v5.json"] = "37133c6679cf9287fe58d49a3bf4ec8a53e5ee4014e052434e9bb5a4ac745be4",
        ["general-assistant-v6.json"] = "fd158d99dbc55872b4e4436800bd25cbc19f86ac2aaeac4fe82b31c1355688ee",
        ["general-assistant-v7.json"] = "736b1cf4911be2ec8c35d5c05909a34eb0d76aa9f0af769907f7b67d6b564df0",
        ["general-assistant-v8.json"] = "f77defe3c81d3f97dcef5d8647621c44b76ffd46566734c83997944fecfc0e5d",
        ["general-assistant-v9.json"] = "027a255dfac0a9294cb95bb672eb43b9952451c75e17bf1b684faf80d66a382b",
        ["general-assistant-v10.json"] = "4d9078b18275895839acd9de6b4630fb8137984c199aaf17920129905a7fb599",
        ["general-assistant-v11.json"] = "b6fe78bdd2352c0a9cba2d396fbfd5afbed3ab0310ce8e386ba0aafb28d1b765",
        ["general-assistant-v12.json"] = "7e800eb89dca40c71ea97638f3ba882e7bac9179ca0c670abcaaa5716c5bc365"
    };

    [Fact]
    public async Task Secretary_definition_loads_with_broad_authority_and_procedural_skills()
    {
        var secretary = await LoadSecretaryAsync();
        var assistant = await LoadGeneralAssistantAsync(12);
        Assert.Equal("Morgan", secretary.Identity.Name);
        Assert.Equal("Secretary", secretary.Identity.Role);
        Assert.Contains("Loading a skill does not grant", secretary.SystemInstructions, StringComparison.Ordinal);
        Assert.Contains("not completion", secretary.SystemInstructions, StringComparison.Ordinal);
        Assert.Contains("Refuse passwords", secretary.SystemInstructions, StringComparison.Ordinal);
        Assert.True(secretary.MemoryPolicy?.SessionMemory);
        Assert.True(secretary.MemoryPolicy?.IdentityUserPromotion);
        Assert.True(secretary.MemoryPolicy?.IdentityUserRetrieval);
        Assert.False(secretary.MemoryPolicy?.UserPromotion);
        Assert.True(secretary.TriggerPolicy?.Enabled);
        Assert.True(secretary.TriggerPolicy?.AllowUserScheduling);
        Assert.Equal(RoleEnvironments.Of(assistant).ToolList, RoleEnvironments.Of(secretary).ToolList);
        Assert.Equal(
            ["store.product.manage", "store.order.review", "store.inventory.review", "store.promotion.manage", "store.daily.review"],
            secretary.SkillList.Select(skill => skill.Id).ToArray());
        Assert.All(secretary.SkillList, skill => Assert.NotEmpty(skill.RequiredCapabilities));
        Assert.DoesNotContain("demo.sensitive_action", RoleEnvironments.Of(secretary).ToolList);
    }

    [Fact]
    public void General_assistant_v1_through_v12_bytes_are_unchanged()
    {
        var agents = FindAgents();
        Assert.Equal(12, GeneralAssistantSha256.Count);
        foreach (var (name, expected) in GeneralAssistantSha256)
        {
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(agents, name)))).ToLowerInvariant();
            Assert.Equal(expected, hash);
        }
    }

    [Fact]
    public async Task Skill_load_does_not_add_browser_tools_to_a_definition_that_lacks_them()
    {
        var product = Assert.Single((await LoadSecretaryAsync()).SkillList, skill => skill.Id == "store.product.manage");
        Assert.Contains(ToolCatalog.BrowserNavigate, product.RequiredCapabilities);
        var bare = SampleDefinitions.Examiner with
        {
            Skills = [product],
            Environment = RoleEnvironment.Empty with { ToolAllowlist = [ToolCatalog.WorkspaceRead] }
        };
        using var args = JsonDocument.Parse("""{"ids":["store.product.manage"]}""");
        Assert.True(SkillLoadAdmission.TryParseIds(args.RootElement, out var requested, out _));
        var plan = SkillLoadAdmission.Plan(bare, [], 0, requested);
        Assert.Equal(["store.product.manage"], plan.Admitted);
        var offered = new PromptContextBuilder()
            .OfferTools(bare, Context(bare, plan.Admitted))
            .Select(tool => tool.Name)
            .ToArray();
        Assert.DoesNotContain(ToolCatalog.BrowserNavigate, offered);
        Assert.DoesNotContain(ToolCatalog.BrowserObserve, offered);
        Assert.DoesNotContain(ToolCatalog.BrowserAct, offered);
        Assert.DoesNotContain(ToolCatalog.BrowserClose, offered);
        Assert.Equal(
            ToolPolicyDecision.Deny,
            ToolPolicy.EvaluateExecution(
                bare,
                ToolCatalog.BrowserNavigate,
                ToolConfigurationGates.AllowAll,
                admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn)));
    }

    [Fact]
    public async Task Secretary_memory_admission_rejects_credentials_and_stores_an_ordinary_fact()
    {
        var secretary = await LoadSecretaryAsync();
        var memories = new StructuredMemoryService(
            new InMemoryStructuredMemoryStore(),
            new DeterministicIdGenerator(
                Enumerable.Range(1, 8).Select(index => Guid.Parse($"019944af-00c2-7000-8000-{index:D12}")),
                [SessionId]),
            new FakeTimeProvider(Now));
        var profile = new UserProfile(ProfileId, 1, new Dictionary<string, UserProfileValue>(), Now);
        var secret = new MemoryProposal(
            MemoryProposalOperation.Upsert,
            MemoryKind.Fact,
            "token",
            "sk-abcdefghijklmnopqrst",
            null,
            MemoryProposalSource.UserExplicit);
        Assert.Equal(
            MemoryAdmissionStatus.Rejected,
            await MemoryAdmission.AdmitOneAsync(
                memories,
                secretary,
                SessionId,
                InstanceId,
                profile,
                [],
                Guid.Parse("019944af-00c1-7000-8000-0000000000d1"),
                secret,
                NullLogger.Instance));
        Assert.Empty(await memories.SearchAsync(
            new TrustedMemoryOwner(SessionId),
            new MemorySearchQuery(null, null),
            new MemoryAdmissionContext("user_explicit", [], new HashSet<string>(StringComparer.Ordinal))));

        var fact = new MemoryProposal(
            MemoryProposalOperation.Upsert,
            MemoryKind.Fact,
            "project codename",
            "Atlas",
            null,
            MemoryProposalSource.UserExplicit);
        var stored = await MemoryAdmission.AdmitOneAsync(
            memories,
            secretary,
            SessionId,
            InstanceId,
            profile,
            [],
            Guid.Parse("019944af-00c1-7000-8000-0000000000d2"),
            fact,
            NullLogger.Instance);
        Assert.NotEqual(MemoryAdmissionStatus.Rejected, stored);
        var recalled = await memories.SearchAsync(
            new TrustedMemoryOwner(SessionId),
            new MemorySearchQuery("Atlas", null),
            new MemoryAdmissionContext("user_explicit", [], new HashSet<string>(StringComparer.Ordinal)));
        Assert.Equal("Atlas", Assert.Single(recalled).Content);
    }

    [Fact]
    public async Task Secretary_sensitive_tools_still_require_approval()
    {
        var secretary = await LoadSecretaryAsync();
        var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn);
        Assert.Equal(
            ToolPolicyDecision.RequireApproval,
            ToolPolicy.EvaluateExecution(secretary, ToolCatalog.EmailSend, ToolConfigurationGates.AllowAll, admission: admission));
        Assert.Equal(
            ToolPolicyDecision.RequireApproval,
            ToolPolicy.EvaluateExecution(secretary, ToolCatalog.HttpRequest, ToolConfigurationGates.AllowAll, admission: admission));
    }

    [Fact]
    public async Task Secretary_non_store_turns_create_an_artifact_and_a_schedule()
    {
        var secretary = await LoadSecretaryAsync();
        var time = new FakeTimeProvider(Now);
        var artifacts = new InMemoryArtifactStore(time);
        var knowledge = RoleKnowledgeService.FromApprovedCatalog(new FileApprovedKnowledgeCatalog(FindAgents()), time);
        var triggers = new InMemoryTriggerStore();
        var triggerIds = new DeterministicIdGenerator(
            Enumerable.Range(1, 8).Select(index => Guid.Parse($"019944af-00c3-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf21")]);
        var tools = new SessionToolExecutor(
            knowledge,
            artifacts: artifacts,
            triggerRegistrations: new TriggerRegistrationService(triggers, triggerIds, time));
        var sessionIds = new DeterministicIdGenerator(
            Enumerable.Range(1, 64).Select(index => Guid.Parse($"019944af-00c4-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940bf22")]);
        var snapshot = new SessionSnapshot(
            1,
            sessionIds.NewSessionId(),
            1,
            secretary,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            ProfileId,
            Now,
            Now,
            AgentInstanceId: InstanceId);
        var sessions = new InMemoryMemoryStore();
        await sessions.SaveAsync(snapshot, 0);
        await using var runtime = new SessionRuntime(
            snapshot,
            new ScriptedLanguageModel(),
            new DefaultAgentBrain(new PromptContextBuilder()),
            sessions,
            new CapturingSessionOutput(),
            sessionIds,
            time,
            NullLogger<SessionRuntime>.Instance,
            artifacts: new SessionArtifactAuthorizer(artifacts),
            tools: tools);
        await runtime.AttachAsync();
        await runtime.ApplyProfileAsync(new UserProfile(
            ProfileId,
            1,
            new Dictionary<string, UserProfileValue>
            {
                ["language"] = LocalUserProfile.ApplicationProfileValue("en", Now),
                ["timeZone"] = new UserProfileValue("UTC", UserProfileValueSource.UserSet, Now)
            },
            Now));

        Assert.True(await runtime.SubmitUserTextAsync("Run the support case for order 91."));
        await runtime.WaitUntilIdleAsync();
        var artifactAnswer = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, artifactAnswer.Status);
        Assert.Contains("delayed", artifactAnswer.Text, StringComparison.OrdinalIgnoreCase);
        var artifact = Assert.Single(artifactAnswer.Envelope!.Blocks, block => block.Kind == ResponseBlockKind.ArtifactReference);
        Assert.True(Guid.TryParse(artifact.ArtifactId, out var artifactId));
        Assert.NotNull(await artifacts.GetAsync(runtime.SessionId, artifactId));

        Assert.True(await runtime.SubmitUserTextAsync("Remind me tomorrow at 9 AM to call John."));
        await runtime.WaitUntilIdleAsync();
        var scheduleAnswer = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.Assistant);
        Assert.Equal(EntryStatus.Completed, scheduleAnswer.Status);
        Assert.Contains("Scheduled Call John.", scheduleAnswer.Text, StringComparison.Ordinal);
        var saved = Assert.Single(await triggers.ListAsync(new TriggerOwner(InstanceId, ProfileId), null));
        Assert.Equal("Call John", saved.Intent);
        Assert.Equal(TriggerRegistrationStatus.Active, saved.Status);
    }

    private static AgentContext Context(AgentDefinition definition, IReadOnlyList<string> activeIds) =>
        new(
            definition,
            [],
            string.Empty,
            null,
            SessionMode.Text,
            null,
            false,
            null,
            new AgentTrigger(Guid.NewGuid(), TriggerKind.UserTurn, "store product"),
            ActiveSkillIds: activeIds);

    private static async Task<AgentDefinition> LoadSecretaryAsync()
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return (await store.GetAsync("secretary", 1))!;
    }

    private static async Task<AgentDefinition> LoadGeneralAssistantAsync(int version)
    {
        var store = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return (await store.GetAsync("general-assistant", version))!;
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (File.Exists(Path.Combine(agents, "secretary-v1.json")))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }
}
