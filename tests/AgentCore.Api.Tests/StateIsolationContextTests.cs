using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentCore.Application.Admin;
using AgentCore.Application.Context;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Tools;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Memory;
using AgentCore.Infrastructure.Providers.Synthetic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AgentCore.Api.Tests;

public sealed class StateIsolationContextTests
{
    [Fact]
    public async Task Live_profile_and_memory_restrictions_remove_the_prior_projection_before_the_next_model_call()
    {
        var model = new RecordingModel(inspect: true);
        await using var host = new FixtureHost(model);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var id = await CreateInstance(host.Services);
        var profiles = host.Services.GetRequiredService<ILocalUserProfileService>(); var profile = await profiles.GetLocalProfileAsync();
        await profiles.UpdateLocalProfileAsync(profile.Revision, new Dictionary<string, string?> { ["preferredName"] = "LIVE_PROFILE_SECRET" }, UserProfileValueSource.ApplicationProfile);
        var session = await host.Services.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
        var now = DateTimeOffset.UtcNow;
        await host.Services.GetRequiredService<IStructuredMemoryStore>().InsertAsync(new(Guid.NewGuid(), session.SessionId,
            MemoryKind.Preference, MemoryItemStatus.Active, "Historical preference", "LIVE_MEMORY_SECRET", "historical preference",
            new("user", [], null, now), now, now, MemoryScope.IdentityUser, id, LocalUserProfile.Id));
        host.Browser.BeforeObservation = async n => {
            if (n != 2) return;
            var planner = host.Services.GetRequiredService<StateIsolationConfigurationService>();
            var view = await planner.ReadAsync(id);
            var draft = new IsolationDraft([
                new("stateIsolationPolicy", new Dictionary<string, JsonElement> { ["profileEvidence"] = JsonSerializer.SerializeToElement(false) }, []),
                new("memoryPolicy", new Dictionary<string, JsonElement> { ["identityUserRetrieval"] = JsonSerializer.SerializeToElement(false), ["userRetrieval"] = JsonSerializer.SerializeToElement(false) }, [])
            ], view.ExperienceEnabled, view.AllowMaintenance);
            var plan = await planner.PreviewAsync(id, draft); await planner.ApplyAsync(id, plan.Expected, draft, plan.Fingerprint);
        };
        await using var runtime = host.Services.GetRequiredService<SessionRuntimeFactory>().Create(session, new SilentOutput());
        await runtime.AttachAsync(); Assert.True(await runtime.SubmitPersistedUserTextAsync("Inspect state now", Guid.NewGuid())); await runtime.WaitUntilIdleAsync();
        var requests = model.Requests.ToArray(); Assert.Equal(2, requests.Length);
        Assert.Contains("LIVE_PROFILE_SECRET", AllText(requests[0])); Assert.Contains("LIVE_MEMORY_SECRET", AllText(requests[0]));
        Assert.DoesNotContain("LIVE_PROFILE_SECRET", AllText(requests[1])); Assert.DoesNotContain("LIVE_MEMORY_SECRET", AllText(requests[1]));
        Assert.Single(await host.Services.GetRequiredService<IStructuredMemoryStore>().ListActiveIdentityUserAsync(id, LocalUserProfile.Id));
    }

    [Fact]
    public async Task Stateless_request_completes_an_ordinary_session_and_a_fresh_request_cannot_reuse_context_or_scratch()
    {
        var model = new RecordingModel();
        await using var host = new FixtureHost(model);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var id = await CreateInstance(host.Services);
        var planner = host.Services.GetRequiredService<StateIsolationConfigurationService>();
        var plan = await planner.PresetAsync(id, "Stateless");
        await planner.ApplyAsync(id, plan.Expected, plan.Draft, plan.Fingerprint);
        var manager = host.Services.GetRequiredService<SessionManager>();
        var first = await manager.CreateForInstanceAsync(id, SessionMode.Text);
        var workspace = host.Services.GetRequiredService<ISessionWorkspace>();
        await workspace.EnsureAsync(first.SessionId, first.Definition);
        await workspace.WriteAsync(first.SessionId, "/workspace/working/request.txt", "FIRST_REQUEST_SCRATCH"u8.ToArray());
        await using var a = host.Services.GetRequiredService<SessionRuntimeFactory>().Create(first, new SilentOutput());
        await a.AttachAsync(); var eventId = Guid.NewGuid();
        Assert.True(await a.SubmitPersistedUserTextAsync("FIRST_REQUEST_PRIVATE", eventId)); await a.WaitUntilIdleAsync();
        Assert.Equal(SessionLifecycleStatus.Completed, a.Snapshot.LifecycleStatus);
        Assert.True(await a.SubmitPersistedUserTextAsync("FIRST_REQUEST_PRIVATE", eventId));
        Assert.Equal("fresh_session_required", (await Assert.ThrowsAsync<AgentCoreException>(() =>
            a.SubmitPersistedUserTextAsync("Another request", Guid.NewGuid()))).Code);
        var second = await manager.CreateForInstanceAsync(id, SessionMode.Text);
        Assert.NotEqual(first.SessionId, second.SessionId);
        await using var b = host.Services.GetRequiredService<SessionRuntimeFactory>().Create(second, new SilentOutput());
        await b.AttachAsync(); Assert.True(await b.SubmitPersistedUserTextAsync("SECOND_REQUEST", Guid.NewGuid())); await b.WaitUntilIdleAsync();
        Assert.Equal(SessionLifecycleStatus.Completed, b.Snapshot.LifecycleStatus);
        Assert.Equal(2, model.Requests.Count);
        Assert.DoesNotContain("FIRST_REQUEST_PRIVATE", AllText(model.Requests.Last()));
        Assert.DoesNotContain("FIRST_REQUEST_SCRATCH", AllText(model.Requests.Last()));
        Assert.NotEmpty(await host.Services.GetRequiredService<IMemoryStore>().ReadHistoryAsync(first.SessionId, 0, 50));
        Assert.Equal("FIRST_REQUEST_SCRATCH"u8.ToArray(), (await workspace.ReadAsync(first.SessionId, first.Definition,
            "/workspace/working/request.txt")).Bytes);
        Assert.Equal("NotFound", (await Assert.ThrowsAsync<AgentCoreException>(() => workspace.ReadAsync(second.SessionId,
            second.Definition, "/workspace/working/request.txt").AsTask())).Code);
    }

    [Fact]
    public async Task Session_isolation_keeps_own_followup_but_excludes_other_customer_profile_and_learned_memory()
    {
        var model = new RecordingModel();
        await using var host = new FixtureHost(model);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var id = await CreateInstance(host.Services);
        var profiles = host.Services.GetRequiredService<ILocalUserProfileService>();
        var profile = await profiles.GetLocalProfileAsync();
        await profiles.UpdateLocalProfileAsync(profile.Revision, new Dictionary<string, string?> { ["preferredName"] = "PROFILE_CUSTOMER_SECRET" }, UserProfileValueSource.ApplicationProfile);
        var isolation = host.Services.GetRequiredService<StateIsolationConfigurationService>();
        var plan = await isolation.PresetAsync(id, "SessionIsolated");
        await isolation.ApplyAsync(id, plan.Expected, plan.Draft, plan.Fingerprint);
        var manager = host.Services.GetRequiredService<SessionManager>();
        var a = await manager.CreateForInstanceAsync(id, SessionMode.Text);
        var b = await manager.CreateForInstanceAsync(id, SessionMode.Text);
        var now = DateTimeOffset.UtcNow;
        await host.Services.GetRequiredService<IStructuredMemoryStore>().InsertAsync(new(Guid.NewGuid(), a.SessionId,
            MemoryKind.Preference, MemoryItemStatus.Active, "Historical customer", "LEARNED_CUSTOMER_SECRET", "historical customer",
            new("user", [], null, now), now, now, MemoryScope.IdentityUser, id, LocalUserProfile.Id));
        var working = host.Services.GetRequiredService<ISessionWorkspace>();
        await working.EnsureAsync(a.SessionId, a.Definition); await working.EnsureAsync(b.SessionId, b.Definition);
        await working.WriteAsync(a.SessionId, "/workspace/working/customer.txt", "SCRATCH_CUSTOMER_A"u8.ToArray());
        await using var ra = host.Services.GetRequiredService<SessionRuntimeFactory>().Create(a, new SilentOutput());
        await using var rb = host.Services.GetRequiredService<SessionRuntimeFactory>().Create(b, new SilentOutput());
        await ra.AttachAsync(); await rb.AttachAsync();
        Assert.True(await ra.SubmitPersistedUserTextAsync("CUSTOMER_A_REQUEST_SECRET", Guid.NewGuid())); await ra.WaitUntilIdleAsync();
        Assert.True(await rb.SubmitPersistedUserTextAsync("CUSTOMER_B_REQUEST", Guid.NewGuid())); await rb.WaitUntilIdleAsync();
        Assert.True(await ra.SubmitPersistedUserTextAsync("Follow up A", Guid.NewGuid())); await ra.WaitUntilIdleAsync();
        var requests = model.Requests.ToArray(); Assert.Equal(3, requests.Length);
        Assert.DoesNotContain("CUSTOMER_A_REQUEST_SECRET", AllText(requests[1]));
        Assert.Contains("CUSTOMER_A_REQUEST_SECRET", AllText(requests[2]));
        Assert.All(requests, request => {
            Assert.DoesNotContain("PROFILE_CUSTOMER_SECRET", AllText(request));
            Assert.DoesNotContain("LEARNED_CUSTOMER_SECRET", AllText(request));
            Assert.DoesNotContain(request.Context!.Sections, s => s.Id == ContextSectionId.Profile);
            using var facts = Observation(request);
            Assert.Equal(1, facts.RootElement.GetProperty("sessions").GetProperty("open").GetInt32());
        });
        Assert.Equal("NotFound", (await Assert.ThrowsAsync<AgentCoreException>(() => working.ReadAsync(b.SessionId,
            b.Definition, "/workspace/working/customer.txt").AsTask())).Code);
        Assert.Single(await host.Services.GetRequiredService<IStructuredMemoryStore>().ListActiveIdentityUserAsync(id, LocalUserProfile.Id));
    }

    [Fact]
    public async Task State_refresh_replaces_one_trusted_runtime_section_while_tool_receipt_stays_evidence()
    {
        var model = new RecordingModel(inspect: true);
        await using var host = new FixtureHost(model);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var id = await CreateInstance(host.Services);
        var session = await host.Services.GetRequiredService<SessionManager>().CreateForInstanceAsync(id, SessionMode.Text);
        await using var runtime = host.Services.GetRequiredService<SessionRuntimeFactory>().Create(session, new SilentOutput());
        await runtime.AttachAsync();
        Assert.True(await runtime.SubmitPersistedUserTextAsync("Inspect state now", Guid.NewGuid())); await runtime.WaitUntilIdleAsync();
        var requests = model.Requests.ToArray(); Assert.Equal(2, requests.Length);
        using var first = Observation(requests[0]); using var next = Observation(requests[1]);
        Assert.True(next.RootElement.GetProperty("resources")[0].GetProperty("openPages").GetInt32()
            > first.RootElement.GetProperty("resources")[0].GetProperty("openPages").GetInt32());
        Assert.All(requests, request => {
            var fact = Assert.Single(request.Context!.Sections, s => s.Id == ContextSectionId.ExecutionFacts);
            Assert.Equal(ContextLayer.Runtime, fact.Layer); Assert.Equal(ContextSemantics.TrustedFact, fact.Semantics);
            Assert.Single(request.Messages, m => m.Role == ModelRole.User && m.Text == "Inspect state now");
        });
        Assert.Equal(requests[0].Messages.Where(m => m.Role == ModelRole.System).Select(m => m.Text),
            requests[1].Messages.Where(m => m.Role == ModelRole.System).Select(m => m.Text));
        var result = Assert.Single(requests[1].Messages, m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AgentStateInspect);
        Assert.Contains("currentSession", result.Text);
        Assert.DoesNotContain(requests[1].Context!.Sections, s => s.Layer == ContextLayer.Core && s.Message.Text.Contains("openPages"));
        var run = Assert.Single(await host.Services.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(id, LocalUserProfile.Id), session.SessionId));
        var manifest = AgentRunToolCallCheckpoint.ReadExecutionState(run.Checkpoint).ContextManifest;
        Assert.NotNull(manifest);
        Assert.DoesNotContain("Inspect state now", JsonSerializer.Serialize(manifest));
    }

    [Theory]
    [InlineData(AgentStateVisibility.CurrentSession)] [InlineData(AgentStateVisibility.InstanceSummary)]
    public async Task State_tool_rejects_forged_ids_limits_refresh_and_rechecks_live_visibility(AgentStateVisibility visibility)
    {
        var model = new RecordingModel(hold: true);
        await using var host = new FixtureHost(model, visibility);
        using var client = TestOwnerCapability.CreateOwnerClient(host);
        var id = await CreateInstance(host.Services);
        var manager = host.Services.GetRequiredService<SessionManager>();
        var session = await manager.CreateForInstanceAsync(id, SessionMode.Text);
        var sibling = await manager.CreateForInstanceAsync(id, SessionMode.Text);
        await using var runtime = host.Services.GetRequiredService<SessionRuntimeFactory>().Create(session, new SilentOutput());
        try
        {
            await runtime.AttachAsync(); Assert.True(await runtime.SubmitPersistedUserTextAsync("Hold request", Guid.NewGuid()));
            await model.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var run = Assert.Single(await host.Services.GetRequiredService<IAgentRunStore>().ListForSessionAsync(new(id, LocalUserProfile.Id), session.SessionId));
            var executor = host.Services.GetRequiredService<SessionToolExecutor>();
            var admission = new ToolExecutionAdmission(false, TriggerKind.UserTurn, AgentInstanceId: id,
                AgentRunId: run.AgentRunId, OwnedSessionId: session.SessionId);
            async Task<string> Read(string args, ToolExecutionAdmission? context = null) => (await executor.ExecuteAsync(run.Admission.Configuration!.Definition,
                session.SessionId, new(Guid.NewGuid().ToString("N"), ToolCatalog.AgentStateInspect, args), 4096, admission: context ?? admission)).Text;
            var before = host.Browser.Observations;
            Assert.Contains("invalid", await Read("{\"sessionId\":\"" + sibling.SessionId + "\"}"));
            Assert.Contains("invalid", await Read("{\"agentInstanceId\":\"" + Guid.NewGuid() + "\"}"));
            Assert.Contains("forbidden", await Read("{}", admission with { AgentRunId = Guid.NewGuid() }), StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, host.Browser.Observations);
            var current = await Read("{}"); using var own = JsonDocument.Parse(current);
            Assert.Equal(1, own.RootElement.GetProperty("sessions").GetProperty("open").GetInt32());
            Assert.DoesNotContain(sibling.SessionId.ToString(), current);
            Assert.Contains("state_refresh_limited", await Read("{}"));
            if (visibility == AgentStateVisibility.InstanceSummary)
            {
                await Task.Delay(1050);
                using var summary = JsonDocument.Parse(await Read("{\"scope\":\"instanceSummary\"}"));
                Assert.Equal(2, summary.RootElement.GetProperty("sessions").GetProperty("open").GetInt32());
                Assert.Equal("instanceSummary", summary.RootElement.GetProperty("scope").GetString());
            }
            else Assert.Contains("forbidden", await Read("{\"scope\":\"instanceSummary\"}"), StringComparison.OrdinalIgnoreCase);
            var root = $"/api/v2/admin/agent-instances/{id}/settings/stateIsolationPolicy";
            var state = (await client.GetFromJsonAsync<InstanceSettingsSection>(root))!;
            (await client.PatchAsJsonAsync(root, new { expectedInstanceRevision = state.InstanceRevision,
                set = new { agentStateVisibility = "Off" }, clear = Array.Empty<string>() })).EnsureSuccessStatusCode();
            Assert.Contains("forbidden", await Read("{}"), StringComparison.OrdinalIgnoreCase);
        }
        finally { model.Release.TrySetResult(); await runtime.WaitUntilIdleAsync(); }
    }

    private static string AllText(ModelRequest request) => string.Join('\n', request.Messages.Select(m => m.Text));
    private static JsonDocument Observation(ModelRequest request) => JsonDocument.Parse(request.Context!.Text(ContextSectionId.ExecutionFacts)
        .Split("\nTrusted runtime observation: ", StringSplitOptions.None)[1]);
    private static async Task<Guid> CreateInstance(IServiceProvider services)
    {
        var definition = await services.GetRequiredService<IAgentDefinitionStore>().GetAsync("state-fixture", 1);
        var now = DateTimeOffset.UtcNow; var id = Guid.NewGuid();
        await services.GetRequiredService<IAgentInstanceStore>().InsertAsync(new(id, definition!.Id, 1, definition.Identity, AgentInstanceLifecycle.Active, now, now));
        return id;
    }
    private sealed class FixtureHost(RecordingModel model, AgentStateVisibility visibility = AgentStateVisibility.CurrentSession) : AgentCoreApiFactory
    {
        public StateBrowser Browser { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.ConfigureTestServices(services => {
                services.RemoveAll<IAgentDefinitionStore>(); services.AddSingleton<IAgentDefinitionStore>(new Definitions(visibility));
                services.RemoveAll<ILanguageModel>(); services.AddSingleton<ILanguageModel>(model);
                services.RemoveAll<IBrowserStateReader>(); services.AddSingleton<IBrowserStateReader>(Browser);
            });
        }
    }
    private sealed class Definitions(AgentStateVisibility visibility) : IAgentDefinitionStore
    {
        private readonly AgentDefinition _definition = new(2, "state-fixture", 1, new("Fixture", "Support", "Support fixture", "Clear"), ["Support the current customer"], "Answer the current request.",
            new("answerNewTurn", true, true), new("balanced", false, "en", 512), new(false, 60000, 120000, 1, ["longSilence"]),
            new(false, "default", 1), new("primary-llm", "primary-stt", "primary-tts"), new Dictionary<string, string>(),
            Environment: new RoleEnvironment(ToolAllowlist: [ToolCatalog.AgentStateInspect]), MemoryPolicy: new(true, true, true, true, true),
            StateIsolationPolicy: new(AgentStateVisibility: visibility));
        public ValueTask<AgentDefinition?> GetAsync(string id, int? version = null, CancellationToken ct = default) => ValueTask.FromResult<AgentDefinition?>(id == _definition.Id && (version is null or 1) ? _definition : null);
        public ValueTask<IReadOnlyList<AgentDefinition>> ListAsync(CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<AgentDefinition>>([_definition]);
    }
    private sealed class StateBrowser : IBrowserStateReader
    {
        public int Observations;
        public Func<int, Task>? BeforeObservation { get; set; }
        public ValueTask<ResourceStateObservation> ObserveAsync(Guid id, CancellationToken ct = default) => Read();
        public ValueTask<ResourceStateObservation> ObserveSessionAsync(Guid id, Guid sid, CancellationToken ct = default) => Read();
        private async ValueTask<ResourceStateObservation> Read()
        {
            var n = Interlocked.Increment(ref Observations);
            if (BeforeObservation is { } before) await before(n);
            return new ResourceStateObservation("browser", "available", "isolated", DateTimeOffset.UtcNow, LiveContexts: 1, OpenPages: n);
        }
    }
    private sealed class RecordingModel(bool inspect = false, bool hold = false) : ILanguageModel
    {
        private readonly ScriptedLanguageModel _inner = new(["Acknowledged"]);
        public ModelCapabilities Capabilities => _inner.Capabilities;
        public ConcurrentQueue<ModelRequest> Requests { get; } = new();
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request, [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request); Started.TrySetResult(); if (hold) await Release.Task.WaitAsync(ct);
            if (inspect && !request.Messages.Any(m => m.Role == ModelRole.Tool && m.Name == ToolCatalog.AgentStateInspect))
            {
                yield return new ModelToolCallEvent(new("inspect-state", ToolCatalog.AgentStateInspect, "{}"));
                yield return new ModelCompleted(ModelStopReason.ToolCalls); yield break;
            }
            await foreach (var e in _inner.GenerateAsync(request, ct)) yield return e;
        }
    }
    private sealed class SilentOutput : ISessionOutput
    { public ValueTask PublishAsync(SessionOutput output, CancellationToken cancellationToken = default) => ValueTask.CompletedTask; }
}
