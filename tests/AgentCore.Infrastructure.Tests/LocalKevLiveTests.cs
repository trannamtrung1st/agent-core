using System.Diagnostics;
using System.Text.Json;
using AgentCore.Application.Decisions;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Decisions;
using Xunit.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class LocalKevLiveFactAttribute : FactAttribute
{
    public LocalKevLiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("AGENTCORE_LIVE_KEV") != "1")
            Skip = "Explicit AGENTCORE_LIVE_KEV=1 is required; default tests never contact Kev.";
        else if (!File.Exists(Environment.GetEnvironmentVariable("AGENTCORE_KEV_KEY_FILE")))
            Skip = "A private AGENTCORE_KEV_KEY_FILE is required for the loopback service.";
    }
}

// Real checkpoint only. No service startup/download, tenant data, credentials or remote endpoints.
public sealed class LocalKevLiveTests(ITestOutputHelper output)
{
    [LocalKevLiveFact(Timeout = 90000)]
    public async Task Shared_router_refuses_uncertain_live_results_and_uses_only_the_explicit_eligible_default()
    {
        var options = new DecisionIntelligenceOptions { Endpoint = Environment.GetEnvironmentVariable("AGENTCORE_KEV_ENDPOINT") ?? "http://127.0.0.1:8009", Enabled = true, Backend = DecisionBackendKind.LocalKev, ShadowMode = false,
            ApiKey = (await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("AGENTCORE_KEV_KEY_FILE")!)).Trim(),
            RequestTimeoutMs = 2000, MaxConcurrentRequests = 1 };
        using var client = new HttpClient(LocalKevDecisionAdapter.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var backend = new LocalKevDecisionAdapter(client, options);
        var catalog = new LiveCatalog();
        var features = new Dictionary<string,string> { ["inputCharacters"] = "128", ["attachmentCount"] = "0", ["requiresVision"] = "False", ["inputMode"] = "text" };
        // Explicit public-fixture warm-up is outside Run admission; readiness alone is not a warm-up.
        using var warmupDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var warmup = await backend.EvaluateAsync(new(DecisionPurpose.ModelRouting, DecisionKind.Choice,
            catalog.Models.Select(m => new DecisionCandidate(m.OfferingId, m.DisplayName,
                $"Tools={m.Tools}; Vision={m.Vision}; Reasoning={m.Reasoning}; Cost=unknown; Latency=unknown; Context=unknown")).ToArray(),
            features, new(), "live-contract-v1"), warmupDeadline.Token);
        Assert.Equal(DecisionStatus.Ready, warmup.Status);
        using var engine = new DecisionEngine(backend, options);
        var router = new AutoModelRouter(engine);
        var intent = new ModelPreference("auto", Reasoning: ReasoningPreference.Default);
        var watch = Stopwatch.StartNew();
        var refused = await Assert.ThrowsAsync<AgentCoreException>(() => router.ResolveAsync(catalog, intent, null, false, true, "session", features).AsTask());
        Assert.Equal("ModelRoutingUnavailable", refused.Code);
        Assert.Contains("LowConfidence", refused.Message);
        output.WriteLine($"Uncertain live Auto refused in {watch.Elapsed.TotalMilliseconds:F1} ms.");
        catalog.RoutingPolicy = new(true, true, .7);
        var selected = await router.ResolveAsync(catalog, intent, null, false, true, "session", features);
        Assert.Equal(catalog.DefaultOfferingId, selected.OfferingId);
        Assert.Equal("configuredDefault", selected.Decision!.Basis);
        Assert.Equal("LowConfidence", selected.Decision.DiagnosticCode);
        output.WriteLine("Explicit eligible default selected with configuredDefault / LowConfidence evidence.");
        options.RequestTimeoutMs = 500;
        catalog.RoutingPolicy = new(true, false, .7);
        watch.Restart();
        refused = await Assert.ThrowsAsync<AgentCoreException>(() => router.ResolveAsync(catalog, intent, null, false, true, "session", features).AsTask());
        Assert.Equal("ModelRoutingUnavailable", refused.Code);
        Assert.True(refused.Message.Contains("Timeout", StringComparison.Ordinal) || refused.Message.Contains("LowConfidence", StringComparison.Ordinal));
        output.WriteLine($"500 ms admission budget: {watch.Elapsed.TotalMilliseconds:F1} ms; {refused.Message}");
    }

    private sealed class LiveCatalog : IModelCatalog
    {
        public string DefaultOfferingId => "dca30000-0000-4000-8000-000000000001";
        public AutoRoutingPolicy RoutingPolicy { get; set; } = new(true, false, .7);
        public long RoutingRevision => 1;
        public ModelDescriptor Default => Models[0];
        public IReadOnlyList<ModelDescriptor> Models { get; } = [
            new("dca30000-0000-4000-8000-000000000001", "Fast chat", "local-fixture", "fixture-fast", true, false, false, false, [], null, DeveloperRoleSupport: true),
            new("dca30000-0000-4000-8000-000000000002", "Reasoning model", "local-fixture", "fixture-reasoning", true, false, false, true, ["low", "high"], "low", DeveloperRoleSupport: true)];
        public ModelDescriptor? Get(string key) => Models.SingleOrDefault(m => m.OfferingId == key);
    }

    [LocalKevLiveFact(Timeout = 90000)]
    public async Task Pinned_checkpoint_authentication_and_all_three_decision_shapes_work_over_real_HTTP()
    {
        var options = new DecisionIntelligenceOptions { Endpoint = Environment.GetEnvironmentVariable("AGENTCORE_KEV_ENDPOINT") ?? "http://127.0.0.1:8009", Enabled = true, Backend = DecisionBackendKind.LocalKev,
            ApiKey = (await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("AGENTCORE_KEV_KEY_FILE")!)).Trim(),
            RequestTimeoutMs = 10000, MaxConcurrentRequests = 1 };
        options.Validate(false);
        using var client = new HttpClient(LocalKevDecisionAdapter.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var backend = new LocalKevDecisionAdapter(client, options);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(80));
        Assert.Equal(DecisionStatus.Ready, await backend.ReadinessAsync(deadline.Token));
        var requests = new DecisionRequest[] {
            new(DecisionPurpose.ContextRelevance, DecisionKind.Choice,
                [new("paris", "Paris is the capital of France."), new("finance", "Quarterly accounting totals.")],
                new Dictionary<string,string> { ["task"] = "What is the capital of France?" }, new(), "live-contract-v1"),
            new(DecisionPurpose.InitiativeRelevance, DecisionKind.YesNo,
                [new("false", "Do not interrupt the user."), new("true", "Speak unsolicited now.")],
                new Dictionary<string,string> { ["conversation"] = "The user is currently speaking and explicitly asked the assistant to stay silent until they finish." }, new(), "live-contract-v1"),
            new(DecisionPurpose.WorkPriority, DecisionKind.Score,
                [new("0", "Can wait a month."), new("1", "Handle this week."), new("2", "Critical: handle immediately.")],
                new Dictionary<string,string> { ["activity"] = "Production is down for every customer. Restore service immediately." }, new(), "live-contract-v1")
        };
        foreach (var request in requests)
        {
            var watch = Stopwatch.StartNew();
            var raw = await backend.EvaluateAsync(request, deadline.Token);
            var validated = DecisionValidation.Validate(request, raw, backend.Id, backend.Version);
            output.WriteLine(JsonSerializer.Serialize(new { kind = request.Kind.ToString(), elapsedMs = watch.Elapsed.TotalMilliseconds,
                raw.Status, raw.SelectedCandidateId, raw.Probabilities, raw.Score, validation = validated.Status.ToString() }));
            Assert.Equal(DecisionStatus.Ready, raw.Status);
            Assert.True(validated.Status is DecisionStatus.Ready or DecisionStatus.LowConfidence,
                $"Real {request.Kind} output failed contract validation: {validated.Status}.");
            Assert.Equal(request.Candidates.Count, raw.Probabilities.Count);
        }
        options.ApiKey = "deliberately-wrong-fixture-key";
        Assert.Equal(DecisionStatus.Unavailable, await backend.ReadinessAsync(deadline.Token));
    }
}
