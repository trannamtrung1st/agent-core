using AgentCore.Tests.Shared;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Agents;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Testing;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Identity;
using AgentCore.Infrastructure.Persistence;
using AgentCore.Infrastructure.Providers.SemanticResponses;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Application.Tests;

public sealed class ProductPublishWorkflowTests
{
    private const string Origin = "http://127.0.0.1:5094";
    private const string Reference = "el_0123456789abcdefghijkl";

    [Fact]
    public async Task Storefront_observation_verifies_publish_and_a_click_does_not()
    {
        var verified = await RunAsync("Publish SKU AC-KBD-001.");
        Assert.Equal(ScriptedLanguageModel.ProductPublishVerifiedAnswer, verified.Answer);
        Assert.Equal(1, verified.Browser.ActCalls);
        Assert.Contains(verified.Browser.NavigatedUrls, url => url.EndsWith("/storefront", StringComparison.Ordinal));
        Assert.True(ProductPublishVerification.IsStorefrontVerified(verified.Browser.Steps));
        Assert.Contains(
            verified.Recording.Requests.SelectMany(request => request.Messages),
            message => message.Text.Contains("A Save or Publish click is not completion", StringComparison.Ordinal));
        AssertNoMessageSend(verified.Recording);

        var clicked = await RunAsync("Publish SKU AC-KBD-001 and stop after the click.");
        Assert.Equal(ScriptedLanguageModel.ProductPublishUnverifiedAnswer, clicked.Answer);
        Assert.DoesNotContain(ScriptedLanguageModel.ProductPublishVerifiedAnswer, clicked.Answer, StringComparison.Ordinal);
        Assert.Equal(1, clicked.Browser.ActCalls);
        Assert.DoesNotContain(clicked.Browser.NavigatedUrls, url => url.Contains("/storefront", StringComparison.Ordinal));
        Assert.False(ProductPublishVerification.IsStorefrontVerified(clicked.Browser.Steps));
        AssertNoMessageSend(clicked.Recording);
    }

    [Fact]
    public async Task Login_wall_stops_the_product_turn()
    {
        var login = await RunAsync("Publish SKU AC-KBD-001 on the login wall.");
        Assert.Equal(ScriptedLanguageModel.ProductPublishLoginAnswer, login.Answer);
        Assert.DoesNotContain(ScriptedLanguageModel.ProductPublishVerifiedAnswer, login.Answer, StringComparison.Ordinal);
        Assert.Equal(0, login.Browser.ActCalls);
        Assert.Equal([Origin + "/login"], login.Browser.NavigatedUrls);
        Assert.False(ProductPublishVerification.IsStorefrontVerified(login.Browser.Steps));
        Assert.Contains(
            login.Recording.Requests.SelectMany(request => request.Messages),
            message => message.Role == ModelRole.Tool
                && message.Text.Contains("user_intervention_required", StringComparison.Ordinal));
        AssertNoMessageSend(login.Recording);
    }

    [Fact]
    public async Task Upload_resolves_an_artifact_and_rejects_a_path_or_url()
    {
        var artifacts = new InMemoryArtifactStore(TimeProvider.System);
        var sessionId = Guid.NewGuid();
        var created = await artifacts.CreateAsync(
            sessionId,
            "../ac-keyboard.png",
            "image/png",
            new byte[] { 1, 2, 3, 4 },
            null,
            null);
        var browser = new UploadBrowser();
        var executor = new SessionToolExecutor(
            artifacts: artifacts,
            browser: browser,
            configurationGate: ToolConfigurationGates.AllowAll);
        var uploaded = await executor.ExecuteAsync(
            Definition(),
            sessionId,
            Call($$"""{"target":{"by":"role","value":"button","name":"{{Reference}}"},"artifactIds":["{{created.ArtifactId}}"]}"""),
            ToolLimits.MaxOutputBytes,
            admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn));

        Assert.DoesNotContain("invalid", uploaded.Text, StringComparison.Ordinal);
        var payload = Assert.IsType<BrowserUpload>(browser.LastUpload);
        Assert.Equal("ac-keyboard.png", payload.FileName);
        Assert.Equal("image/png", payload.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, payload.Content.ToArray());
        Assert.DoesNotContain("/", payload.FileName, StringComparison.Ordinal);

        browser = new UploadBrowser();
        executor = new SessionToolExecutor(
            artifacts: artifacts,
            browser: browser,
            configurationGate: ToolConfigurationGates.AllowAll);
        var path = await executor.ExecuteAsync(
            Definition(),
            sessionId,
            Call($$"""{"target":{"by":"role","value":"button","name":"{{Reference}}"},"artifactIds":["/tmp/ac-keyboard.png"]}"""),
            ToolLimits.MaxOutputBytes,
            admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn));
        var url = await executor.ExecuteAsync(
            Definition(),
            sessionId,
            Call($$"""{"target":{"by":"role","value":"button","name":"{{Reference}}"},"artifactIds":["https://files.example/ac-keyboard.png"]}"""),
            ToolLimits.MaxOutputBytes,
            admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn));
        Assert.Contains("invalid", path.Text, StringComparison.Ordinal);
        Assert.Contains("invalid", url.Text, StringComparison.Ordinal);
        Assert.Equal(0, browser.ActCalls);
    }

    [Fact]
    public async Task Upload_resolves_a_definition_resource()
    {
        var resourceId = Guid.NewGuid();
        var browser = new UploadBrowser();
        var executor = new SessionToolExecutor(
            browser: browser,
            configurationGate: ToolConfigurationGates.AllowAll,
            definitionResources: new PublicationResource(resourceId, [9, 8, 7]));
        var uploaded = await executor.ExecuteAsync(
            Definition(),
            Guid.NewGuid(),
            Call($$"""{"target":{"by":"role","value":"button","name":"{{Reference}}"},"artifactIds":["{{resourceId}}"]}"""),
            ToolLimits.MaxOutputBytes,
            admission: new ToolExecutionAdmission(false, TriggerKind.UserTurn));

        Assert.DoesNotContain("invalid", uploaded.Text, StringComparison.Ordinal);
        var payload = Assert.IsType<BrowserUpload>(browser.LastUpload);
        Assert.Equal("ac-keyboard.png", payload.FileName);
        Assert.Equal(new byte[] { 9, 8, 7 }, payload.Content.ToArray());
    }

    private static void AssertNoMessageSend(RecordingModel recording) =>
        Assert.DoesNotContain(
            recording.Requests.SelectMany(request => request.Messages),
            message => message.Name == ToolCatalog.AppMessageSend
                || message.ToolCalls?.Any(call => call.Name == ToolCatalog.AppMessageSend) == true);

    private static ModelToolCall Call(string arguments) => new("c1", ToolCatalog.BrowserUpload, arguments);

    private static async Task<PublishRun> RunAsync(string userText)
    {
        var browser = new ProductBrowser();
        var recording = new RecordingModel(new ScriptedLanguageModel());
        var output = new CapturingSessionOutput();
        var turns = new RuntimeAgentRunStore();
        await using var runtime = Create(
            new SemanticResponseLanguageModel(recording),
            output,
            Definition(),
            browser,
            turns);
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitUserTextAsync(userText));
        await runtime.WaitUntilIdleAsync();
        Assert.DoesNotContain(runtime.Snapshot.Entries, entry => entry.Status == EntryStatus.Failed);
        Assert.DoesNotContain(output.Items, item => item.Payload is ErrorOutput);
        var assistant = Assert.Single(
            runtime.Snapshot.Entries,
            entry => entry.Role == ConversationRole.Assistant && entry.Status == EntryStatus.Completed);
        return new PublishRun(assistant.Text, browser, recording);
    }

    private static AgentDefinition Definition() =>
        new(
            1,
            "general-assistant",
            11,
            new AgentIdentity("Test", "Role", "desc", "Tone"),
            [],
            "instructions",
            new BehaviorPolicy("answerNewTurn", true, true),
            new ConversationPolicy("balanced", false, "en", 2048),
            new InitiativePolicy(false, 60_000, 120_000, 1, ["longSilence"], 0),
            new VoiceConfiguration(false, "default", 1.0),
            new ProviderPreferences("primary-llm", "primary-stt", "primary-tts"),
            new Dictionary<string, string>(StringComparer.Ordinal),
            new RoleEnvironment(ToolAllowlist:
            [
                ToolCatalog.BrowserNavigate,
                ToolCatalog.BrowserSnapshot,
                ToolCatalog.BrowserFind,
                ToolCatalog.BrowserClick,
                ToolCatalog.BrowserUpload
            ]),
            Skills:
            [
                new SkillSpec(
                    ScriptedLanguageModel.ProductPublishSkillId,
                    "Manage a store product",
                    "Create or update a product, then confirm the public storefront.",
                    "Search SKU AC-KBD-001, upload ac-keyboard.png, and check the storefront. A Save or Publish click is not completion.", SkillProjection.OnDemand, true,
                    [ToolCatalog.BrowserNavigate],
                    [])
            ]);

    private static SessionRuntime Create(
        ILanguageModel model,
        CapturingSessionOutput output,
        AgentDefinition definition,
        ProductBrowser browser,
        RuntimeAgentRunStore turns)
    {
        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-10-01T12:00:00Z"));
        var ids = new DeterministicIdGenerator(
            Enumerable.Range(1, 400).Select(index => Guid.Parse($"019944af-00d2-7000-8000-{index:D12}")),
            [Guid.Parse("873f07d1-e264-4c81-a31b-7e59e940d201")]);
        var now = time.GetUtcNow();
        var instanceId = Guid.NewGuid();
        var snapshot = new SessionSnapshot(
            1,
            ids.NewSessionId(),
            1,
            definition,
            SessionMode.Text,
            null,
            SessionStatus.Created,
            [],
            string.Empty,
            0,
            null,
            null,
            now,
            now,
            ModelSelection: new SessionModelSelection(
                "synthetic-offline/scripted",
                "primary-llm",
                "scripted",
                ModelSelectionSource.SystemDefault,
                null), AgentInstanceId: instanceId);
        var store = new InMemoryMemoryStore();
        snapshot = RuntimeAgentRunStore.WithPins(snapshot);
        turns.Bind(store);
        store.SaveAsync(snapshot, 0).AsTask().GetAwaiter().GetResult();
        var agents = new InMemoryAgentInstanceStore();
        agents.InsertAsync(new AgentInstance(instanceId, definition.Id, definition.Version, definition.Identity,
            AgentInstanceLifecycle.Active, now, now), initialSkills: definition.SkillList).AsTask().GetAwaiter().GetResult();
        return SessionRuntimeFixture.Create(
            snapshot,
            model,
            new DefaultAgentBrain(new PromptContextBuilder(ToolConfigurationGates.AllowAll, browser)),
            store,
            output,
            ids,
            time,
            NullLogger<SessionRuntime>.Instance,
            tools: new SessionToolExecutor(browser: browser, agentInstances: agents, configurationGate: ToolConfigurationGates.AllowAll),
            agentRuns: turns);
    }

    private sealed record PublishRun(string Answer, ProductBrowser Browser, RecordingModel Recording);

    private sealed class RecordingModel(ILanguageModel inner) : ILanguageModel
    {
        public List<ModelRequest> Requests { get; } = [];

        public ModelCapabilities Capabilities => inner.Capabilities;

        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(
            ModelRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            await foreach (var item in inner.GenerateAsync(request, cancellationToken).ConfigureAwait(false))
            {
                yield return item;
            }
        }
    }

    private sealed class ProductBrowser : IBrowser
    {
        public BrowserProviderDescriptor Provider { get; } = new("fixture", "Test browser", new HashSet<BrowserFeature> { BrowserFeature.Navigate, BrowserFeature.Snapshot, BrowserFeature.Find, BrowserFeature.Click, BrowserFeature.Type, BrowserFeature.Hover, BrowserFeature.Drag, BrowserFeature.FillForm, BrowserFeature.SelectOption, BrowserFeature.PressKey, BrowserFeature.Upload, BrowserFeature.FillCredential, BrowserFeature.Wait, BrowserFeature.Tabs, BrowserFeature.Screenshot, BrowserFeature.Close });
        private string _page = "none";

        public int ActCalls { get; private set; }

        public List<string> NavigatedUrls { get; } = [];

        public List<ProductPublishStep> Steps { get; } = [];

        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            [Origin]);

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new(_page == "none" ? null : PageUri());

        public ValueTask<BrowserResult> NavigateAsync(
            BrowserRequest request,
            CancellationToken cancellationToken = default)
        {
            NavigatedUrls.Add(((BrowserNavigate)request.Command).Url!);
            Steps.Add(new ProductPublishStep(ToolCatalog.BrowserNavigate, ((BrowserNavigate)request.Command).Url!));
            if (!string.Equals(new Uri(((BrowserNavigate)request.Command).Url!)!.GetLeftPart(UriPartial.Authority), Origin, StringComparison.Ordinal)
                || new Uri(((BrowserNavigate)request.Command).Url!).AbsolutePath is not ("/" or "" or "/storefront" or "/login"))
            {
                return new(new BrowserResult("target_denied", null));
            }

            _page = new Uri(((BrowserNavigate)request.Command).Url!)!.AbsolutePath switch
            {
                "/storefront" => "storefront",
                "/login" => "login",
                _ => "admin"
            };
            return new(Ok(Capture()));
        }

        public ValueTask<BrowserResult> SnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            if (_page == "none")
            {
                return new(new BrowserResult("provider_unavailable", null));
            }

            var observation = Capture();
            Steps.Add(new ProductPublishStep(ToolCatalog.BrowserSnapshot, observation.Content));
            return new(Ok(observation));
        }

        public ValueTask<BrowserResult> InteractAsync(
            BrowserRequest request,
            CancellationToken cancellationToken = default)
        {
            ActCalls++;
            Steps.Add(new ProductPublishStep(ToolCatalog.BrowserClick, AgentCore.Application.Tools.BrowserToolArguments.ToolName(request.Operation)[8..]));
            if (Capture().Targets.FirstOrDefault(e => e.Target == (request.Command switch { BrowserClick click => click.Target, BrowserTypeText type => type.Target, _ => null })) is not { Name: var name } || name != "Publish" || AgentCore.Application.Tools.BrowserToolArguments.ToolName(request.Operation)[8..] != "click")
            {
                return new(new BrowserResult("unsupported_operation", null));
            }

            return new(Ok(Capture()));
        }

        private Uri PageUri() => _page switch
        {
            "storefront" => new Uri(Origin + "/storefront"),
            "login" => new Uri(Origin + "/login"),
            _ => new Uri(Origin + "/")
        };

        private BrowserResult Ok(BrowserSnapshot observation) => new(null, observation);

        private BrowserSnapshot Capture()
        {
            return _page switch
            {
                "login" => new BrowserSnapshot(
                    Origin + "/login",
                    "Sign in",
                    "Sign in",
                    false,
                    [],
                    BrowserInterventionKind.AuthenticationRequired),
                "storefront" => new BrowserSnapshot(
                    Origin + "/storefront",
                    "AC Keyboard",
                    "AC Keyboard $99 Published ac-keyboard.png",
                    false,
                    []),
                _ => ProductPage()
            };
        }

        private BrowserSnapshot ProductPage()
        {
            return new BrowserSnapshot(
                Origin + "/",
                "Product",
                "Saved",
                false,
                [new BrowserElement(new BrowserTarget("role", "button", Name: "Publish"), "button", "Publish", ["click"])]);
        }

        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) => request.Operation switch
        {
            BrowserOperation.Find => new(BrowserFixtureResults.Find(request, Capture())),
            BrowserOperation.Navigate => NavigateAsync(request, ct),
            BrowserOperation.Snapshot or BrowserOperation.WaitFor => SnapshotAsync(request.SessionId, ct),
            BrowserOperation.Click or BrowserOperation.Type or BrowserOperation.Hover or BrowserOperation.Drag or BrowserOperation.Upload or BrowserOperation.FillForm => InteractAsync(request, ct),
            _ => new(new BrowserResult("unsupported_operation")),
        };
}

    private sealed class UploadBrowser : IBrowser
    {
        public BrowserProviderDescriptor Provider { get; } = new("fixture", "Test browser", new HashSet<BrowserFeature> { BrowserFeature.Navigate, BrowserFeature.Snapshot, BrowserFeature.Find, BrowserFeature.Click, BrowserFeature.Type, BrowserFeature.Hover, BrowserFeature.Drag, BrowserFeature.FillForm, BrowserFeature.SelectOption, BrowserFeature.PressKey, BrowserFeature.Upload, BrowserFeature.FillCredential, BrowserFeature.Wait, BrowserFeature.Tabs, BrowserFeature.Screenshot, BrowserFeature.Close });
        public int ActCalls { get; private set; }

        public BrowserUpload? LastUpload { get; private set; }

        public bool IsAvailable => true;

        public BrowserHostPolicy HostPolicy { get; } = new(
            true,
            true,
            BrowserInteractionMode.InteractiveDemo,
            ["http://127.0.0.1:5091"]);

        public ValueTask<Uri?> GetCurrentUrlAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new(new Uri("http://127.0.0.1:5091/"));

        public ValueTask<BrowserResult> NavigateAsync(
            BrowserRequest request,
            CancellationToken cancellationToken = default) =>
            new(new BrowserResult(null, Page()));

        public ValueTask<BrowserResult> SnapshotAsync(Guid sessionId, CancellationToken cancellationToken = default) =>
            new(new BrowserResult(null, Page()));

        public ValueTask<BrowserResult> InteractAsync(
            BrowserRequest request,
            CancellationToken cancellationToken = default)
        {
            ActCalls++;
            LastUpload = ((BrowserUploadCommand)request.Command).Uploads?.FirstOrDefault() ?? ((BrowserUploadCommand)request.Command).Uploads?.FirstOrDefault();
            return new(new BrowserResult(null, Page()));
        }

        private static BrowserSnapshot Page() =>
            new("http://127.0.0.1:5091/", "Upload", "chosen", false, []);

        public ValueTask<BrowserResult> ExecuteAsync(BrowserRequest request, CancellationToken ct = default) => request.Operation switch
        {
            BrowserOperation.Find => new(BrowserFixtureResults.Find(request, Page())),
            BrowserOperation.Navigate => NavigateAsync(request, ct),
            BrowserOperation.Snapshot or BrowserOperation.WaitFor => SnapshotAsync(request.SessionId, ct),
            BrowserOperation.Click or BrowserOperation.Type or BrowserOperation.Hover or BrowserOperation.Drag or BrowserOperation.Upload or BrowserOperation.FillForm => InteractAsync(request, ct),
            _ => new(new BrowserResult("unsupported_operation")),
        };
}

    private sealed class PublicationResource(Guid resourceId, byte[] content) : IAgentDefinitionResourceAdminStore
    {
        public ValueTask<IReadOnlyList<AgentDefinitionDraftResource>> ListDraftResourcesAsync(
            Guid draftId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<AgentDefinitionDraftResource> UpsertDraftResourceAsync(
            AgentDefinitionDraftResourceUpsert upsert,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<AgentDefinitionDraftResourceBatchBound> BindDraftResourcesAsync(
            AgentDefinitionDraftResourceBatchBind bind,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<AgentDefinitionDraftResource> RemoveDraftResourceAsync(
            AgentDefinitionDraftResourceRemove remove,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<byte[]?> ReadDraftResourceContentAsync(
            Guid draftId,
            Guid resourceId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AgentDefinitionPublicationResource>> ListPublicationResourcesAsync(
            string definitionId,
            int version,
            CancellationToken cancellationToken = default) =>
            new([
                new AgentDefinitionPublicationResource(
                    definitionId,
                    version,
                    resourceId,
                    "assets/ac-keyboard.png",
                    AgentDefinitionResourceKind.StaticAsset,
                    "image/png",
                    "abc",
                    content.Length)
            ]);

        public ValueTask<byte[]?> ReadPublicationResourceContentAsync(
            string definitionId,
            int version,
            Guid id,
            CancellationToken cancellationToken = default) =>
            new(id == resourceId ? content : null);
    }
}
