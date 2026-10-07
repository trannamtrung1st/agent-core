using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Conversation;
using AgentCore.Infrastructure.Definitions;
using AgentCore.Infrastructure.Providers.Synthetic;

namespace AgentCore.Application.Tests;

public sealed class CapabilityProjectionTests
{
    [Theory]
    [InlineData("workspace.write")]
    [InlineData("Please use WORKSPACE.WRITE.")]
    public async Task Runtime_load_persists_ids_and_next_continuation_executes_then_next_turn_resets(string query)
    {
        var d = await Definition(ToolCatalog.CapabilitiesLoad, ToolCatalog.WorkspaceWrite, ToolCatalog.WorkspaceRead);
        d = d with { Environment = d.Environment! with { Workspace = new() } };
        var root = Path.Combine(Path.GetTempPath(), "capability-loop-" + Guid.NewGuid().ToString("N"));
        var time = TimeProvider.System;
        var ids = new AgentCore.Infrastructure.Identity.SystemIdGenerator(time);
        var now = time.GetUtcNow();
        var snapshot = new SessionSnapshot(1, Guid.NewGuid(), 1, d, SessionMode.Text, null, SessionStatus.Created, [], "", 0, null, null, now, now, ModelSelection: new SessionModelSelection("synthetic-offline/scripted", "primary-llm", "scripted", ModelSelectionSource.SystemDefault, null), AgentInstanceId: Guid.NewGuid());
        var memory = new AgentCore.Infrastructure.Persistence.InMemoryMemoryStore();
        var turns = new AgentCore.Infrastructure.Persistence.InMemoryConversationTurnExecutionStore();
        var workspace = new AgentCore.Infrastructure.Workspaces.FileSessionWorkspace(root, root, sessions: new WorkspaceTestSessions());
        await workspace.EnsureAsync(snapshot.SessionId, d);
        await memory.SaveAsync(snapshot, 0);
        var model = new LoadQueryModel(query);
        try
        {
            await using var runtime = new SessionRuntime(snapshot, model,
                new AgentCore.Application.Agents.DefaultAgentBrain(new AgentCore.Application.Agents.PromptContextBuilder()), memory,
                new AgentCore.Application.Testing.CapturingSessionOutput(), ids, time, Microsoft.Extensions.Logging.Abstractions.NullLogger<SessionRuntime>.Instance,
                tools: new SessionToolExecutor(workspace: workspace, agentWorkspace: OwnedWorkspaces.Create(workspace)), turnExecutions: turns);
            await runtime.AttachAsync();
            var source = Guid.NewGuid();
            Assert.True(await runtime.SubmitPersistedUserTextAsync("synthetic-capability-projection:write", source));
            await runtime.WaitUntilIdleAsync();
            var answer = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant);
            Assert.Contains("Capability projection results:", answer.Text);
            Assert.DoesNotContain("error", answer.Text);
            Assert.Equal("Loaded exact capability café\r\n", System.Text.Encoding.UTF8.GetString((await workspace.ReadAsync(snapshot.SessionId, d, "/workspace/working/loaded-capability.txt")).Bytes));
            var execution = await turns.GetBySourceEventAsync(snapshot.SessionId, source);
            Assert.Equal([ToolCatalog.WorkspaceWrite], execution!.LoadedCapabilityIds);
            Assert.Equal(1, execution.CapabilityLoadCount);
            Assert.True(await runtime.SubmitPersistedUserTextAsync("synthetic-capability-projection:inspect", Guid.NewGuid()));
            await runtime.WaitUntilIdleAsync();
            var next = runtime.Snapshot.Entries.Last(e => e.Role == ConversationRole.Assistant);
            Assert.Contains(ToolCatalog.CapabilitiesLoad, next.Text);
            Assert.DoesNotContain(ToolCatalog.WorkspaceWrite, next.Text);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private sealed class LoadQueryModel(string query) : ILanguageModel
    {
        private readonly ScriptedLanguageModel _inner = new();
        public ModelCapabilities Capabilities => _inner.Capabilities;
        public async IAsyncEnumerable<ModelGenerationEvent> GenerateAsync(ModelRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var item in _inner.GenerateAsync(request, cancellationToken))
                yield return item is ModelToolCallEvent { Call.Name: ToolCatalog.CapabilitiesLoad } load
                    ? load with { Call = load.Call with { ArgumentsJson = JsonSerializer.Serialize(new { query, limit = 1 }) } }
                    : item;
        }
    }

    internal static async Task<AgentDefinition> Definition(params string[] names)
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../agents"));
        var baseline = (await new FileAgentDefinitionStore(path, SyntheticProviderAliases.Default).GetAsync("general-assistant", 16))!;
        return baseline with { Environment = baseline.Environment! with { ToolAllowlist = null,
            Capabilities = new("Selected", names), Projection = new([]) } };
    }
    internal static AgentContext Context(AgentDefinition d) => new(d, [], "", null, SessionMode.Text, null, false, null, new(Guid.NewGuid(), TriggerKind.UserTurn, "email"), AgentWorkspaceAvailable: true);

    [Fact]
    public async Task All_resolves_exact_snapshot_without_runtime_catalog_expansion()
    {
        var d = await Definition();
        var draft = AgentDefinitionCandidate.FromDefinition(d) with { Environment = d.Environment! with { Capabilities = new("All", []) } };
        var resolved = CapabilityAuthorizationResolver.ResolveCandidate(draft);
        Assert.True(resolved.Environment!.ToolList.Count > 32);
        Assert.Equal(64, resolved.Environment.Capabilities!.AuthorizationFingerprint!.Length);
        var published = resolved.ToPublished(16);
        Assert.Equal(resolved.Environment.ToolList, published.Environment!.ToolList);
        Assert.DoesNotContain("workspace.retain", published.Environment.ToolList);
        Assert.DoesNotContain("future.capability", published.Environment.ToolList);
        AgentDefinitionValidator.Validate(published);
    }
    [Fact]
    public async Task Frozen_All_snapshot_does_not_expand_to_other_current_registered_names()
    {
        var d = await Definition(ToolCatalog.CapabilitiesLoad, ToolCatalog.WorkspaceRead);
        d = d with { Environment = d.Environment! with { Capabilities = new("All", d.Environment.ToolList), Projection = new([ToolCatalog.CapabilitiesLoad, ToolCatalog.WorkspaceRead]) } };
        Assert.Equal([ToolCatalog.CapabilitiesLoad, ToolCatalog.WorkspaceRead], ToolCatalog.For(d, Context(d), ToolConfigurationGates.AllowAll).Select(t => t.Name));
        Assert.False(AgentCore.Application.Agents.RolePermissions.AllowsTool(d, ToolCatalog.WorkspaceWrite));
        Assert.Equal(ToolPolicyDecision.Deny, ToolPolicy.EvaluateExecution(d, ToolCatalog.WorkspaceWrite, ToolConfigurationGates.AllowAll));
    }

    [Fact]
    public async Task Projection_metrics_measure_actual_schemas_and_never_emit_search_content()
    {
        var d = await Definition(ToolCatalog.CapabilitiesLoad, ToolCatalog.WorkspaceRead);
        var measurements = new System.Collections.Concurrent.ConcurrentQueue<(long Value, Dictionary<string, object?> Tags)>();
        using var listener = new System.Diagnostics.Metrics.MeterListener();
        listener.InstrumentPublished = (instrument, meter) =>
        {
            if (instrument.Meter.Name == AgentCore.Application.Observability.RuntimeTelemetry.Name && instrument.Name == "capability_projection") meter.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => measurements.Enqueue((value, tags.ToArray().ToDictionary(t => t.Key, t => t.Value))));
        listener.Start();
        var context = Context(d) with { LoadedCapabilityIds = [ToolCatalog.WorkspaceRead] };
        var tools = ToolCatalog.For(d, context, ToolConfigurationGates.AllowAll);
        CapabilityProjectionTelemetry.Record(d, context, ToolConfigurationGates.AllowAll, tools, "capability-test-model", 1);
        var selected = measurements.Where(m => m.Tags.GetValueOrDefault("model") as string == "capability-test-model").ToArray();
        long Read(string kind) => Assert.Single(selected, m => m.Tags["kind"] as string == kind).Value;
        Assert.Equal(2, Read("authorizedCapabilityCount"));
        Assert.Equal(2, Read("eligibleCapabilityCount"));
        Assert.Equal(2, Read("projectedCapabilityCount"));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(tools).Length, Read("projectedToolSchemaBytes"));
        Assert.Equal(1, Read("loadedProjectedCount"));
        Assert.Equal(1, Read("coreBootstrapCount"));
        Assert.Equal(1, Read("capabilityLoadInvocationCount"));
        Assert.All(selected, m => Assert.Equal(["kind", "model", "provider"], m.Tags.Keys.Order().ToArray()));
    }

    [Fact]
    public async Task Initial_projection_does_not_flood_model_and_loaded_and_skill_sources_dedupe()
    {
        var d = await Definition(ToolCatalog.CapabilitiesLoad, ToolCatalog.WorkspaceRead, ToolCatalog.WorkspaceWrite);
        var c = Context(d);
        Assert.Equal([ToolCatalog.CapabilitiesLoad], ToolCatalog.For(d, c, ToolConfigurationGates.AllowAll).Select(t => t.Name));
        d = d with { Environment = d.Environment! with { Projection = new([ToolCatalog.WorkspaceRead]) },
            Skills = [new("write", "Write", "Write a file", "Use tools", [], [ToolCatalog.WorkspaceWrite], [])] };
        c = c with { Definition = d, ActiveSkillIds = ["write"], LoadedCapabilityIds = [ToolCatalog.WorkspaceRead, ToolCatalog.WorkspaceWrite] };
        Assert.Equal(3, ToolCatalog.For(d, c, ToolConfigurationGates.AllowAll).Count);
        Assert.Empty(ToolCatalog.For(d, c with { ModelSupportsTools = false }, ToolConfigurationGates.AllowAll));
        Assert.DoesNotContain(ToolCatalog.WorkspaceWrite, ToolCatalog.For(d, Context(d), ToolConfigurationGates.AllowAll).Select(t => t.Name));
    }
    [Theory]
    [InlineData("email.search")]
    [InlineData("please use email.search")]
    [InlineData("Please use EMAIL.SEARCH.")]
    [InlineData("email")]
    [InlineData("mail messages")]
    public async Task Discovery_is_deterministic_and_only_authorized_configured_and_discoverable(string query)
    {
        var d = await Definition(ToolCatalog.CapabilitiesLoad, ToolCatalog.EmailSearch, ToolCatalog.WorkComplete);
        var c = Context(d);
        using var j = JsonDocument.Parse(JsonSerializer.Serialize(new { query }));
        Assert.Equal([ToolCatalog.EmailSearch], CapabilityDiscoveryMatcher.Load(d, c, ToolConfigurationGates.AllowAll, j.RootElement, 0).Loaded);
        Assert.Empty(CapabilityDiscoveryMatcher.Load(d, c, ToolConfigurationGates.Unconfigured, j.RootElement, 0).Loaded);
        Assert.Equal("load_over_budget", CapabilityDiscoveryMatcher.Load(d, c, ToolConfigurationGates.AllowAll, j.RootElement, 8).Outcome);
        Assert.Equal("load_already_projected", CapabilityDiscoveryMatcher.Load(d, c with { LoadedCapabilityIds = [ToolCatalog.EmailSearch] }, ToolConfigurationGates.AllowAll, j.RootElement, 1).Outcome);
    }
    [Fact]
    public async Task No_match_and_configuration_and_context_cannot_escalate_authority()
    {
        var d = await Definition(ToolCatalog.CapabilitiesLoad, ToolCatalog.WorkspaceWrite);
        using var j = JsonDocument.Parse("{\"query\":\"send an email\"}");
        Assert.Empty(CapabilityDiscoveryMatcher.Load(d, Context(d), ToolConfigurationGates.AllowAll, j.RootElement, 0).Loaded);
        Assert.Equal(ToolPolicyDecision.Deny, ToolPolicy.EvaluateExecution(d, ToolCatalog.EmailSend, ToolConfigurationGates.AllowAll));
        Assert.False(ToolPolicy.IsOffered(d, Context(d) with { DetachedExecution = true }, ToolCatalog.CapabilitiesLoad, ToolConfigurationGates.AllowAll));
        foreach (var kind in new[] { TriggerKind.ApplicationEvent, TriggerKind.ScheduledOccurrence, TriggerKind.ThoughtActivation })
        {
            var c = Context(d) with { DetachedExecution = true, Trigger = new(Guid.NewGuid(), kind, null) };
            Assert.False(ToolPolicy.IsOffered(d, c, ToolCatalog.CapabilitiesLoad, ToolConfigurationGates.AllowAll));
            Assert.True(ToolPolicy.IsOffered(d, c with { AgentInstanceId = Guid.NewGuid() }, ToolCatalog.CapabilitiesLoad, ToolConfigurationGates.AllowAll));
        }
    }
    [Fact]
    public async Task Attachment_and_browser_context_project_only_exact_authority()
    {
        var d = await Definition(ToolCatalog.CapabilitiesLoad, ToolCatalog.AttachmentsRead, ToolCatalog.BrowserObserve);
        var c = Context(d) with { SessionAttachments = [new(Guid.NewGuid(), "invoice.txt", "text/plain", 1)], AgentInstanceId = Guid.NewGuid() };
        var projected = ToolCatalog.For(d, c, ToolConfigurationGates.AllowAll).Select(t => t.Name).ToArray();
        Assert.Contains(ToolCatalog.AttachmentsRead, projected);
        Assert.DoesNotContain(ToolCatalog.BrowserObserve, projected);
        Assert.Contains(ToolCatalog.BrowserObserve, ToolCatalog.For(d, c with { LoadedCapabilityIds = [ToolCatalog.BrowserObserve] }, ToolConfigurationGates.AllowAll).Select(t => t.Name));
        Assert.DoesNotContain(ToolCatalog.BrowserNavigate, projected);
        Assert.DoesNotContain(ToolCatalog.AttachmentsRead, ToolCatalog.For(d, Context(d), ToolConfigurationGates.AllowAll).Select(t => t.Name));
    }

    [Fact]
    public async Task Always_and_loaded_interfaces_still_obey_configuration_context_and_approval()
    {
        var d = await Definition(ToolCatalog.CapabilitiesLoad, ToolCatalog.EmailSend, ToolCatalog.WorkspaceWrite, ToolCatalog.TriggerScheduleOnce);
        d = d with { Environment = d.Environment! with { Projection = new([ToolCatalog.EmailSend, ToolCatalog.WorkspaceWrite, ToolCatalog.TriggerScheduleOnce]) } };
        var c = Context(d) with { LoadedCapabilityIds = d.Environment.ToolList };
        Assert.DoesNotContain(ToolCatalog.EmailSend, ToolCatalog.For(d, c, ToolConfigurationGates.Unconfigured).Select(t => t.Name));
        Assert.Equal(ToolPolicyDecision.Deny, ToolPolicy.EvaluateExecution(d, ToolCatalog.EmailSend, ToolConfigurationGates.Unconfigured));
        Assert.Equal(ToolPolicyDecision.RequireApproval, ToolPolicy.EvaluateExecution(d, ToolCatalog.EmailSend, ToolConfigurationGates.AllowAll));
        var occurrence = c with { DetachedExecution = true, AgentInstanceId = Guid.NewGuid(), Trigger = new(Guid.NewGuid(), TriggerKind.ScheduledOccurrence, null) };
        Assert.DoesNotContain(ToolCatalog.WorkspaceWrite, ToolCatalog.For(d, occurrence, ToolConfigurationGates.AllowAll).Select(t => t.Name));
        Assert.DoesNotContain(ToolCatalog.TriggerScheduleOnce, ToolCatalog.For(d, occurrence, ToolConfigurationGates.AllowAll).Select(t => t.Name));
    }

    [Fact]
    public async Task Description_matching_is_bounded_deterministic_and_never_returns_context_only_tools()
    {
        var d = await Definition(ToolRegistry.All.Select(t => t.Name).ToArray());
        using var broad = JsonDocument.Parse("{\"query\":\"workspace browser email\",\"limit\":2}");
        var first = CapabilityDiscoveryMatcher.Load(d, Context(d), ToolConfigurationGates.AllowAll, broad.RootElement, 0);
        Assert.Equal(2, first.Loaded.Count);
        Assert.Equal(first, CapabilityDiscoveryMatcher.Load(d, Context(d), ToolConfigurationGates.AllowAll, broad.RootElement, 0), new LoadComparer());
        Assert.All(first.Loaded, n => Assert.True(ToolRegistry.Get(n).Discoverable));
        using var longQuery = JsonDocument.Parse(JsonSerializer.Serialize(new { query = new string('x', 201) }));
        Assert.Throws<AgentCoreException>(() => CapabilityDiscoveryMatcher.Load(d, Context(d), ToolConfigurationGates.AllowAll, longQuery.RootElement, 0));
        var descriptor = ToolRegistry.Get(ToolCatalog.WorkspaceRead);
        var word = System.Text.RegularExpressions.Regex.Matches(descriptor.Summary.ToLowerInvariant(), "[a-z]+").Select(m => m.Value)
            .First(w => w.Length > 3 && !descriptor.Name.Contains(w) && w != descriptor.Category && !descriptor.Tags.Contains(w));
        var only = await Definition(ToolCatalog.CapabilitiesLoad, descriptor.Name);
        using var description = JsonDocument.Parse(JsonSerializer.Serialize(new { query = word }));
        Assert.Equal([descriptor.Name], CapabilityDiscoveryMatcher.Load(only, Context(only), ToolConfigurationGates.AllowAll, description.RootElement, 0).Loaded);
    }
    [Fact]
    public async Task Discovery_ranks_embedded_exact_names_before_family_matches_and_keeps_authority_filtering()
    {
        var d = await Definition(ToolCatalog.CapabilitiesLoad, ToolCatalog.WorkspaceRead, ToolCatalog.WorkspaceWrite);
        using var query = JsonDocument.Parse("{\"query\":\"workspace: use workspace.write and workspace.read, not email.send\",\"limit\":2}");
        var result = CapabilityDiscoveryMatcher.Load(d, Context(d), ToolConfigurationGates.AllowAll, query.RootElement, 0);
        Assert.Equal([ToolCatalog.WorkspaceRead, ToolCatalog.WorkspaceWrite], result.Loaded);
        Assert.Equal("load_matched", result.Outcome);
        Assert.DoesNotContain(ToolCatalog.EmailSend, result.Loaded);
        using var limited = JsonDocument.Parse("{\"query\":\"workspace please use workspace.write\",\"limit\":1}");
        Assert.Equal([ToolCatalog.WorkspaceWrite], CapabilityDiscoveryMatcher.Load(d, Context(d), ToolConfigurationGates.AllowAll, limited.RootElement, 0).Loaded);
    }

    [Theory]
    [InlineData("workspace.copy", ToolCatalog.WorkspaceCopy)]
    [InlineData("rename", ToolCatalog.WorkspaceMove)]
    public async Task Workspace_discovery_loads_current_canonical_cross_root_and_tree_descriptors(string query, string expected)
    {
        var d = await Definition(ToolCatalog.CapabilitiesLoad, ToolCatalog.WorkspaceCopy, ToolCatalog.WorkspaceMove);
        using var args = JsonDocument.Parse(JsonSerializer.Serialize(new { query, limit = 1 }));
        var context = Context(d);
        var loaded = CapabilityDiscoveryMatcher.Load(d, context, ToolConfigurationGates.AllowAll, args.RootElement, 0);
        Assert.Equal([expected], loaded.Loaded);
        var projected = ToolCatalog.For(d, context with { LoadedCapabilityIds = loaded.Loaded }, ToolConfigurationGates.AllowAll);
        var tool = Assert.Single(projected, t => t.Name == expected);
        Assert.Equal(ToolRegistry.Get(expected).ModelDefinition, tool);
        Assert.Contains("/home", tool.Description);
        Assert.Contains("/working", tool.Description);
        Assert.Contains("tree", tool.Description);
        Assert.DoesNotContain("retain", tool.Description);
        Assert.DoesNotContain("checkout", tool.Description);
        Assert.DoesNotContain("workspace.retain", ToolRegistry.AllKnownNames());
        Assert.DoesNotContain("workspace.checkout", ToolRegistry.AllKnownNames());
    }

    private sealed class LoadComparer : IEqualityComparer<CapabilityLoadResult>
    {
        public bool Equals(CapabilityLoadResult? a, CapabilityLoadResult? b) => a!.Outcome == b!.Outcome && a.Loaded.SequenceEqual(b.Loaded) && a.AlreadyProjected.SequenceEqual(b.AlreadyProjected);
        public int GetHashCode(CapabilityLoadResult value) => value.Outcome.GetHashCode();
    }

    [Theory]
    [InlineData("{\"query\":\"email\",\"limit\":\"4\"}")]
    [InlineData("{\"query\":\"email\",\"limit\":0}")]
    [InlineData("{}")]
    [InlineData("{\"query\":\"*\"}")]
    [InlineData("{\"query\":\"email\",\"owner\":\"other\"}")]
    [InlineData("{\"query\":\"email\",\"query\":\"browser\"}")]
    [InlineData("{\"query\":\"email\",\"limit\":9}")]
    public async Task Invalid_load_requests_fail_closed(string args)
    {
        var d = await Definition(ToolCatalog.CapabilitiesLoad);
        using var j = JsonDocument.Parse(args);
        Assert.Throws<AgentCoreException>(() => CapabilityDiscoveryMatcher.Load(d, Context(d), ToolConfigurationGates.AllowAll, j.RootElement, 0));
    }
}
