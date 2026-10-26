using System.Net;
using System.Text;
using System.Text.Json;
using AgentCore.Application.Decisions;
using AgentCore.Infrastructure.Decisions;

namespace AgentCore.Infrastructure.Tests;

public sealed class DecisionEngineTests
{
    private static DecisionRequest Request(DecisionKind kind = DecisionKind.Choice) => new(DecisionPurpose.ModelRouting, kind,
        [new("a", "Eligible A"), new("b", "Eligible B")], new Dictionary<string, string> { ["requiresVision"] = "false" }, new(), "policy-v1");
    private static DecisionIntelligenceOptions Options() => new() { Enabled = true, Backend = DecisionBackendKind.Synthetic };

    [Fact]
    public async Task Disabled_and_unsupported_scope_never_invoke_backend()
    {
        var backend = new Stub { PreAdmissionSupported = false };
        using var disabled = new DecisionEngine(backend, new());
        Assert.Equal(DecisionStatus.Disabled, (await disabled.DecideAsync(Request())).Status);
        using var engine = new DecisionEngine(backend, Options());
        Assert.Equal(DecisionStatus.UnsupportedScope, (await engine.DecideAsync(Request())).Status);
        Assert.Equal(0, backend.Calls);
        var llm = new AuthorizedLlmDecisionAdapter(null!);
        Assert.Equal(DecisionStatus.UnsupportedScope, (await llm.EvaluateAsync(Request(), default)).Status);
    }

    [Theory]
    [InlineData(double.NaN, 0, DecisionStatus.InvalidResult)]
    [InlineData(double.PositiveInfinity, 0, DecisionStatus.InvalidResult)]
    [InlineData(1.1, -0.1, DecisionStatus.InvalidResult)]
    [InlineData(.6, .4, DecisionStatus.LowConfidence)]
    [InlineData(.8, .1, DecisionStatus.InvalidResult)]
    [InlineData(.9, .1, DecisionStatus.Ready)]
    public async Task Validates_distribution_and_acceptance(double a, double b, DecisionStatus expected)
    {
        var backend = new Stub { Result = new(DecisionStatus.Ready, null, new Dictionary<string, double> { ["a"] = a, ["b"] = b }, null, "stub", "v1") };
        using var engine = new DecisionEngine(backend, Options());
        Assert.Equal(expected, (await engine.DecideAsync(Request())).Status);
    }

    [Fact]
    public async Task Unknown_missing_and_conflicting_backend_choices_refuse()
    {
        var backend = new Stub(); using var engine = new DecisionEngine(backend, Options());
        backend.Result = new(DecisionStatus.Ready, "c", new Dictionary<string, double> { ["a"] = .9, ["c"] = .1 }, null, "stub", "v1");
        Assert.Equal(DecisionStatus.InvalidResult, (await engine.DecideAsync(Request())).Status);
        backend.Result = new(DecisionStatus.Ready, "b", new Dictionary<string, double> { ["a"] = .9, ["b"] = .1 }, null, "stub", "v1");
        Assert.Equal(DecisionStatus.InvalidResult, (await engine.DecideAsync(Request())).Status);
        Assert.Equal(DecisionStatus.InvalidRequest, (await engine.DecideAsync(Request() with { Candidates = [new("a", "a"), new("a", "b")] })).Status);
    }

    [Fact]
    public async Task Real_kev_schema_checks_model_and_auth_and_omits_scope()
    {
        var calls = new List<string>();
        var options = Options(); options.Backend = DecisionBackendKind.LocalKev; options.ApiKey = "private-test-key-only";
        using var client = new HttpClient(new Handler(async (message, ct) =>
        {
            Assert.Equal("Bearer", message.Headers.Authorization!.Scheme);
            Assert.Equal(options.ApiKey, message.Headers.Authorization.Parameter);
            calls.Add(message.RequestUri!.AbsolutePath);
            if (message.Method == HttpMethod.Get) return Json(new { models = new[] { new { name = "kev-latest", run = options.ModelId, truncate_states = false } } });
            var raw = await message.Content!.ReadAsStringAsync(ct);
            Assert.DoesNotContain("Scope", raw); Assert.DoesNotContain("Invocation", raw); Assert.DoesNotContain("private-test", raw);
            using var body = JsonDocument.Parse(raw);
            Assert.Equal("choice", body.RootElement.GetProperty("questions").GetProperty("decision").GetProperty("type").GetString());
            return Json(new { model = "kev-latest", answers = new { decision = new { type = "choice", choice = "b", probabilities = new { a = .1, b = .9 }, confidence = .8 } } });
        }));
        using var engine = new DecisionEngine(new LocalKevDecisionAdapter(client, options), options);
        var result = await engine.DecideAsync(Request());
        Assert.Equal(DecisionStatus.Ready, result.Status); Assert.Equal("b", result.SelectedCandidateId);
        Assert.Equal(new[] { "/v1/models", "/v1/systemone" }, calls);
    }

    [Theory]
    [InlineData("mutable-tag")]
    [InlineData("jaredpalmer/kev-0.8b@v1.0")]
    public async Task Incorrect_checkpoint_refuses_before_post(string run)
    {
        using var client = new HttpClient(new Handler((request, ct) =>
        { Assert.Equal(HttpMethod.Get, request.Method); return Task.FromResult(Json(new { models = new[] { new { name = "kev-latest", run, truncate_states = false } } })); }));
        var options = Options(); options.ApiKey = "private-test-key-only";
        using var engine = new DecisionEngine(new LocalKevDecisionAdapter(client, options), options);
        Assert.Equal(DecisionStatus.InvalidResult, (await engine.DecideAsync(Request())).Status);
    }

    [Fact]
    public async Task Saturation_and_cancellation_are_bounded_without_retries()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var backend = new Stub { Wait = async ct => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } };
        var options = Options(); options.MaxConcurrentRequests = 1; options.RequestTimeoutMs = 100;
        using var engine = new DecisionEngine(backend, options);
        var first = engine.DecideAsync(Request()).AsTask(); await started.Task;
        Assert.Equal(DecisionStatus.Saturated, (await engine.DecideAsync(Request())).Status);
        Assert.Equal(DecisionStatus.Timeout, (await first).Status); Assert.Equal(1, backend.Calls);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => engine.DecideAsync(Request(), cancelled.Token).AsTask());
    }

    [Theory]
    [InlineData("http://169.254.169.254:8009")]
    [InlineData("http://example.com:8009")]
    [InlineData("http://127.0.0.1:8009/x")]
    [InlineData("http://user:secret@127.0.0.1:8009")]
    [InlineData("http://127.0.0.1:8009?key=secret")]
    public void Unsafe_endpoints_refuse(string endpoint) => Assert.Throws<InvalidOperationException>(() => new DecisionIntelligenceOptions { Endpoint = endpoint }.Validate(true));

    [Theory]
    [InlineData(DecisionKind.YesNo)] [InlineData(DecisionKind.Score)]
    public async Task Typed_yes_no_and_score_use_closed_option_sets(DecisionKind kind)
    {
        var ids = kind == DecisionKind.YesNo ? new[] { "false", "true" } : new[] { "0", "1" };
        var backend = new Stub { Result = new(DecisionStatus.Ready, null, new Dictionary<string, double> { [ids[0]] = .1, [ids[1]] = .9 }, null, "stub", "v1") };
        using var engine = new DecisionEngine(backend, Options());
        var request = Request(kind) with { Candidates = ids.Select(id => new DecisionCandidate(id, id)).ToArray() };
        var result = await engine.DecideAsync(request);
        Assert.Equal(DecisionStatus.Ready, result.Status); Assert.Equal(ids[1], result.SelectedCandidateId);
        Assert.Equal(kind == DecisionKind.Score ? .9 : (double?)null, result.Score);
        Assert.Equal(DecisionStatus.InvalidRequest, (await engine.DecideAsync(Request(kind))).Status);
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct); }
    private sealed class Stub : IDecisionBackend
    {
        public string Id => "stub"; public string Version => "v1"; public bool PreAdmissionSupported { get; init; } = true;
        public int Calls { get; private set; }
        public DecisionResult Result { get; set; } = DecisionResult.Failure(DecisionStatus.Unavailable, "stub", "v1");
        public Func<CancellationToken, Task>? Wait { get; init; }
        public ValueTask<DecisionStatus> ReadinessAsync(CancellationToken ct) => ValueTask.FromResult(DecisionStatus.Ready);
        public async ValueTask<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken ct)
        { Calls++; if (Wait is not null) await Wait(ct); return Result; }
    }
}
