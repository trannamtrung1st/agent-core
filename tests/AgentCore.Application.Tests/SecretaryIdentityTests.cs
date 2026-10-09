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
            ["store.signin", "store.product.manage", "store.order.review", "store.inventory.review", "store.promotion.manage", "store.daily.review"],
            secretary.SkillList.Select(skill => skill.Id).ToArray());
        var signin = secretary.SkillList.Single(skill => skill.Id == "store.signin");
        Assert.Contains("credentials.list", signin.Procedure, StringComparison.Ordinal);
        Assert.Contains("fill_credential", signin.Procedure, StringComparison.Ordinal);
        Assert.Contains("bound credentialRef", signin.Procedure, StringComparison.Ordinal);
        Assert.Contains("location supplied by the owner or approved knowledge", signin.Procedure, StringComparison.Ordinal);
        Assert.Contains("browser.find", signin.Procedure, StringComparison.Ordinal);
        Assert.DoesNotContain("browser.click fill_credential", signin.Procedure, StringComparison.Ordinal);
        Assert.Contains("Detached work cannot fill credentials", signin.Procedure, StringComparison.Ordinal);
        Assert.DoesNotContain(ToolCatalog.CredentialsList, RoleEnvironments.Of(secretary).ToolList);
        var daily = secretary.SkillList.Single(skill => skill.Id == "store.daily.review");
        Assert.Contains("Visit each required review area at most once", daily.Procedure, StringComparison.Ordinal);
        Assert.Contains("Do not restart the whole review", daily.Procedure, StringComparison.Ordinal);
        Assert.Contains("not evidence that filtering succeeded", daily.Procedure, StringComparison.Ordinal);
        Assert.Contains("Count pending orders only when the observed order status is Pending", daily.Procedure, StringComparison.Ordinal);
        Assert.Contains("whole-grid or page summary", daily.Procedure, StringComparison.Ordinal);
        Assert.Contains("unpublished-product review as unknown or unverified", daily.Procedure, StringComparison.Ordinal);
        Assert.Contains("Do not invent a threshold", daily.Procedure, StringComparison.Ordinal);
        Assert.Contains("no changes were made", daily.Procedure, StringComparison.Ordinal);
        var orders = secretary.SkillList.Single(skill => skill.Id == "store.order.review");
        Assert.Contains("not evidence that filtering succeeded", orders.Procedure, StringComparison.Ordinal);
        Assert.Contains("Count only rows whose observed order status is Pending", orders.Procedure, StringComparison.Ordinal);
        Assert.Contains("whole-grid or page summary", orders.Procedure, StringComparison.Ordinal);
        var inventory = secretary.SkillList.Single(skill => skill.Id == "store.inventory.review");
        Assert.Contains("confirmed low-stock subset", inventory.Procedure, StringComparison.Ordinal);
        Assert.Contains("Do not invent a threshold", inventory.Procedure, StringComparison.Ordinal);





        Assert.All(secretary.SkillList, skill => Assert.NotEmpty(skill.RequiredCapabilities));
        Assert.DoesNotContain("demo.sensitive_action", RoleEnvironments.Of(secretary).ToolList);
    }

    [Fact]
    public async Task Retired_general_definitions_are_absent_from_the_runtime_catalog()
    {
        var current = new FileAgentDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        for (var version = 1; version <= 20; version++)
            Assert.Null(await current.GetAsync("general-assistant", version));
        Assert.Equal(21, (await current.GetAsync("general-assistant"))!.Version);
    }

    [Fact]
    public async Task Skill_load_does_not_add_browser_tools_to_a_definition_that_lacks_them()
    {
        var product = Assert.Single((await LoadSecretaryAsync()).SkillList, skill => skill.Id == "store.product.manage");
        Assert.Contains("direct semantic targets", product.Procedure, StringComparison.Ordinal);
        Assert.Contains("scope duplicate labels with one unique within row/group", product.Procedure, StringComparison.Ordinal);
        Assert.Contains("Verify required values and checked states before saving", product.Procedure, StringComparison.Ordinal);
        Assert.Contains("never blindly create a duplicate", product.Procedure, StringComparison.Ordinal);
        Assert.Contains("browser.upload with the returned artifactId", product.Procedure, StringComparison.Ordinal);
        Assert.Contains("A filename is not an artifactId", product.Procedure, StringComparison.Ordinal);
        Assert.Contains("observe the current page after navigation", product.Procedure, StringComparison.Ordinal);
        Assert.Contains("Independently inspect the public product", product.Procedure, StringComparison.Ordinal);
        Assert.Contains("A Save click is not completion", product.Procedure, StringComparison.Ordinal);
        Assert.DoesNotContain("browser.click with that artifact id", product.Procedure, StringComparison.Ordinal);
        Assert.Equal(SkillProjection.OnDemand, product.Projection);
        Assert.Contains(ToolCatalog.ArtifactsCreateFromWorkspace, product.RequiredCapabilities);
        Assert.Contains(ToolCatalog.BrowserNavigate, product.RequiredCapabilities);
        Assert.Contains(ToolCatalog.BrowserFind, product.RequiredCapabilities);
        Assert.Contains(ToolCatalog.BrowserUpload, product.RequiredCapabilities);
        var bare = SampleDefinitions.Examiner with
        {
            Skills = [product],
            Environment = RoleEnvironment.Empty with { ToolAllowlist = [ToolCatalog.WorkspaceRead] }
        };
        using var args = JsonDocument.Parse("""{"ids":["definition:store.product.manage"]}""");
        Assert.True(SkillLoadAdmission.TryParseIds(args.RootElement, out var requested, out _));
        var plan = SkillLoadAdmission.Plan(Catalog(bare), [], 0, requested);
        Assert.Equal(["definition:store.product.manage"], plan.Admitted);
        var offered = new PromptContextBuilder()
            .OfferTools(bare, Context(bare, plan.Admitted))
            .Select(tool => tool.Name)
            .ToArray();
        Assert.DoesNotContain(ToolCatalog.BrowserNavigate, offered);
        Assert.DoesNotContain(ToolCatalog.BrowserSnapshot, offered);
        Assert.DoesNotContain(ToolCatalog.BrowserClick, offered);
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
    public async Task OnDemand_skills_are_pinned_without_keyword_activation_across_turns()
    {
        var secretary = await LoadSecretaryAsync();
        var agents = new InMemoryAgentInstanceStore();
        await agents.InsertAsync(new AgentInstance(InstanceId, secretary.Id, secretary.Version, secretary.Identity,
            AgentInstanceLifecycle.Active, Now, Now), initialSkills: secretary.SkillList);
        var pending = secretary.SkillList.Select(s => s.Id).ToArray();
        Assert.Contains("store.product.manage", pending);
        Assert.Contains("store.order.review", pending);
        var turns = new RuntimeAgentRunStore();
        var model = new CompletingLanguageModel();
        var sessionIds = new DeterministicIdGenerator(
            Enumerable.Range(1, 32).Select(index => Guid.Parse($"019944af-00c5-7000-8000-{index:D12}")),
            [SessionId]);
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
            AgentInstanceId: InstanceId,
            ModelSelection: new SessionModelSelection(
                "synthetic-offline/scripted",
                "primary-llm",
                "scripted",
                ModelSelectionSource.SystemDefault,
                null));
        var sessions = new InMemoryMemoryStore();
        snapshot = RuntimeAgentRunStore.WithPins(snapshot);
        turns.Bind(sessions);
        await sessions.SaveAsync(snapshot, 0);
        await using var runtime = SessionRuntimeFixture.Create(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder()),
            sessions,
            new CapturingSessionOutput(),
            sessionIds,
            new FakeTimeProvider(Now),
            NullLogger<SessionRuntime>.Instance,
            tools: new SessionToolExecutor(agentInstances: agents),
            agentRuns: turns);
        await runtime.AttachAsync();

        const string journey = "Publish AC Keyboard with SKU AC-KBD-001 at $99 using ac-keyboard.png and verify it on the storefront.";
        Assert.True(await runtime.SubmitUserTextAsync(journey));
        await runtime.WaitUntilIdleAsync();
        var user = Assert.Single(runtime.Snapshot.Entries, entry => entry.Role == ConversationRole.User);
        var pinned = await turns.ForSourceAsync(runtime.SessionId, user.SourceEventId ?? user.EntryId);
        Assert.Empty(pinned!.ActiveSkillKeys);
        Assert.Contains(pinned.PinnedSkillCatalog, s => s.Key == "definition:store.product.manage");
        Assert.Contains(
            "key: definition:store.product.manage",
            string.Join('\n', model.Requests[0].Messages.Select(message => message.Text)),
            StringComparison.Ordinal);

        Assert.True(await runtime.SubmitUserTextAsync("Remind me tomorrow at 9 AM to call John."));
        await runtime.WaitUntilIdleAsync();
        var reminder = runtime.Snapshot.Entries.Last(entry => entry.Role == ConversationRole.User && entry.Text.Contains("Remind me", StringComparison.Ordinal));
        var reminderPin = await turns.ForSourceAsync(runtime.SessionId, reminder.SourceEventId ?? reminder.EntryId);
        Assert.DoesNotContain("store.product.manage", reminderPin!.ActiveSkillKeys);

        Assert.True(await runtime.SubmitUserTextAsync("Review pending orders."));
        await runtime.WaitUntilIdleAsync();
        var orders = runtime.Snapshot.Entries.Last(entry => entry.Text == "Review pending orders.");
        var orderPin = await turns.ForSourceAsync(runtime.SessionId, orders.SourceEventId ?? orders.EntryId);
        Assert.DoesNotContain("store.product.manage", orderPin!.ActiveSkillKeys);
        Assert.Empty(orderPin.ActiveSkillKeys);
        Assert.Contains(orderPin.PinnedSkillCatalog, s => s.Key == "definition:store.order.review");
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
        var agents = new InMemoryAgentInstanceStore();
        await agents.InsertAsync(new AgentInstance(InstanceId, secretary.Id, secretary.Version, secretary.Identity,
            AgentInstanceLifecycle.Active, Now, Now), initialSkills: secretary.SkillList);
        var tools = new SessionToolExecutor(
            knowledge, agentInstances: agents,
            artifacts: artifacts,
            triggerRegistrations: new AutomationService(triggers, triggerIds, time));
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
        await using var runtime = SessionRuntimeFixture.Create(
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
        Assert.Equal("Call John", saved.Instructions);
        Assert.Equal(AutomationStatus.Active, saved.Status);
    }

    private sealed class CompletingLanguageModel : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities { get; } = new(true, true, Tools: true);

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await Task.Yield();
            yield return new ModelDisplayDelta("Shown");
            yield return new ModelSemanticResponseReady(
                new ModelSemanticResponse("Shown", new ModelSpeechProjection(ModelSpeechMode.Same, null), []));
            yield return new ModelCompleted(ModelStopReason.Completed);
        }
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
            ActiveSkillKeys: activeIds);

    private static async Task<AgentDefinition> LoadSecretaryAsync()
    {
        var store = new ScenarioDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return (await store.GetAsync("secretary", 1))!;
    }

    private static async Task<AgentDefinition> LoadGeneralAssistantAsync(int version)
    {
        var store = new ScenarioDefinitionStore(FindAgents(), SyntheticProviderAliases.Default);
        return (await store.GetAsync("general-assistant", version))!;
    }

    private static string FindAgents()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var agents = Path.Combine(dir.FullName, "agents");
            if (File.Exists(Path.Combine(agents, "secretary-v8.json")))
            {
                return agents;
            }

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("agents/");
    }
    private static IReadOnlyList<EffectiveSkill> Catalog(AgentDefinition d) => d.SkillList.Select(s => new EffectiveSkill("definition:" + s.Id,
        SkillOrigin.Definition, s.Id, s.Name, s.Description, s.Procedure, s.Projection, s.RequiredCapabilities, s.ResourcePaths)).ToArray();

}
