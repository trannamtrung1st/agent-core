using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using AgentCore.Application.Decisions;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Decisions;
using Xunit.Abstractions;

namespace AgentCore.Infrastructure.Tests;

public sealed class LocalKevRoutingQualityTests(ITestOutputHelper output)
{
    private const string Direct = "dca30000-0000-4000-8000-000000000001", Reasoning = "dca30000-0000-4000-8000-000000000002";
    [LocalKevLiveFact(Timeout = 180000)]
    public async Task Real_pinned_routing_and_effort_are_task_dependent_on_frozen_heldout_tasks_without_fallback()
    {
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "kev-routing-v2.json"));
        using var fixture = JsonDocument.Parse(bytes);
        var settings = fixture.RootElement;
        var options = new DecisionIntelligenceOptions { Enabled = true, Backend = DecisionBackendKind.LocalKev, ShadowMode = false,
            Endpoint = Environment.GetEnvironmentVariable("AGENTCORE_KEV_ENDPOINT") ?? "http://127.0.0.1:8009",
            ApiKey = (await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("AGENTCORE_KEV_KEY_FILE")!)).Trim(),
            RequestTimeoutMs = settings.GetProperty("admissionBudgetMs").GetInt32(), MaxConcurrentRequests = 1 };
        using var client = new HttpClient(LocalKevDecisionAdapter.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var backend = new RecordingBackend(new LocalKevDecisionAdapter(client, options));
        using var warmup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Assert.Equal(DecisionStatus.Ready, (await backend.EvaluateAsync(new(DecisionPurpose.ContextRelevance, DecisionKind.Choice,
            [new("paris", "Paris is the capital of France."), new("other", "Accounting totals.")],
            new Dictionary<string,string> { ["task"] = "What is the capital of France?" }, new(), "warmup"), warmup.Token)).Status);
        backend.Clear();
        using var engine = new DecisionEngine(backend, options);
        var router = new AutoModelRouter(engine); var catalog = new Catalog();
        var rows = new List<object>(); var accepted = 0; var correct = 0; var total = 0; var heldout = 0;
        var filter = Environment.GetEnvironmentVariable("AGENTCORE_ROUTING_SPLIT");
        foreach (var item in settings.GetProperty("cases").EnumerateArray())
        {
            var split = item.GetProperty("split").GetString()!;
            if (filter is not null && split != filter) continue;
            var modelRouting = item.GetProperty("purpose").GetString() == "ModelRouting";
            var signals = RoutingTaskSignals.Project(item.GetProperty("task").GetString(), item.GetProperty("inputMode").GetString()!);
            var intent = modelRouting ? new ModelPreference("auto", Reasoning: ReasoningPreference.Default)
                : new ModelPreference("fixed", Reasoning, new("auto"));
            backend.Clear(); var watch = Stopwatch.StartNew(); string? selected = null; string? basis = null; string? error = null;
            try
            {
                var result = await router.ResolveAsync(catalog, intent, null, false, true, "qualityFixture", signals);
                basis = result.Decision!.Basis;
                Assert.Equal(modelRouting ? "ranked" : "fixedAutoReasoning", basis);
                selected = modelRouting ? result.OfferingId == Direct ? "direct" : "reasoning" : result.ReasoningEffort;
                accepted++;
            }
            catch (AgentCoreException e) when (e.Code == "ModelRoutingUnavailable") { error = e.Message; }
            var expected = item.GetProperty("expected").GetString();
            var right = selected == expected; if (right) correct++; total++; if (split == "heldout") heldout++;
            rows.Add(new { id = item.GetProperty("id").GetString(), split, purpose = item.GetProperty("purpose").GetString(),
                signals, expected, selected, basis, error, elapsedMs = watch.Elapsed.TotalMilliseconds,
                probabilities = backend.Last?.Probabilities, engineVersion = backend.Version });
        }
        var report = new { fixtureSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), policyRevision = AutoModelRouter.PolicyRevision,
            threshold = .7, admissionBudgetMs = options.RequestTimeoutMs, total, heldout, accepted, correct, rows };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }); output.WriteLine(json);
        if (Environment.GetEnvironmentVariable("AGENTCORE_ROUTING_RESULTS") is { Length: > 0 } destination) await File.WriteAllTextAsync(destination, json);
        Assert.True(total > 0); Assert.True(accepted >= total * .8, $"Routing coverage {accepted}/{total}; inspect real outputs.");
        Assert.Equal(accepted, correct); // Refusal is allowed; fallback and accepted wrong choices are not.
        Assert.Equal(0, backend.StatusCalls);
    }

    private sealed class Catalog : IModelCatalog
    {
        public string DefaultOfferingId => Direct;
        public AutoRoutingPolicy RoutingPolicy => new(true, false, .7);
        public ModelDescriptor Default => Models[0];
        public IReadOnlyList<ModelDescriptor> Models { get; } = [
            new(Direct, "Direct response", "fixture", "direct", true, false, true, false, [], null, DeveloperRoleSupport: true),
            new(Reasoning, "Deliberate reasoning", "fixture", "reasoning", true, false, true, true, ["low", "high"], "low", DeveloperRoleSupport: true)];
        public ModelDescriptor? Get(string id) => Models.SingleOrDefault(m => m.OfferingId == id);
    }
    private sealed class RecordingBackend(IDecisionBackend inner) : IDecisionBackend
    {
        public DecisionResult? Last; public int StatusCalls;
        public string Id => inner.Id; public string Version => inner.Version; public bool PreAdmissionSupported => true;
        public void Clear() => Last = null;
        public ValueTask<DecisionStatus> ReadinessAsync(CancellationToken ct) { StatusCalls++; return inner.ReadinessAsync(ct); }
        public async ValueTask<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken ct) => Last = await inner.EvaluateAsync(request, ct);
    }
}
