using System.Diagnostics;
using System.Text.Json;
using AgentCore.Application.Decisions;
using AgentCore.Application.Models;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Models;
using AgentCore.Infrastructure.Decisions;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [LocalKevLiveFact(Timeout = 90000)]
    public async Task RealKev_tenant_Auto_admission_replay_and_SQLite_reopen_preserve_one_real_decision_pin()
    {
        await using var f = await Fixture.Create(true);
        var setup = await ProviderSetup(f, ModelProviderKind.Anthropic);
        var tenant = setup.Route.TenantId;
        var human = new TenantSessionInitiator(setup.Human.AccountId, setup.Human.AuthenticationSessionId, setup.Human.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var models = Models(f);
        var direct = await models.SaveOfferingAsync(human, tenant, setup.Offering.OfferingId, new(setup.Connection.ConnectionId, setup.Offering.PresetId,
            setup.Offering.ExternalModelId, "Direct response", setup.Offering.Transport,
            setup.Offering.Capabilities with { ReasoningEfforts = [], DefaultReasoningEffort = null }, true, false), await Revision(), setup.Offering.Revision);
        var other = await models.SaveOfferingAsync(human, tenant, null, new(setup.Connection.ConnectionId, null, "alternate-model", "Deliberate reasoning",
            setup.Offering.Transport, setup.Offering.Capabilities with { ReasoningEfforts = ["low", "high"], DefaultReasoningEffort = "low" }, true, false), await Revision(), 0);
        await models.SaveDefaultAsync(human, tenant, setup.Offering.OfferingId, null, await Revision(), 0);
        await models.SaveRoutingAsync(human, tenant, new(true, false, .7), await Revision(), 1);
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var reader = new TenantModelCatalogReader((ITenantModelStore)f.Iam, f.Iam);
        var sessions = (ITenantSessionStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var service = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, sessions, runs, (ITenantDefinitionStore)f.Iam, f.BuiltIns,
            f.Identity, f.Authorizer, reader, new(), f.Ids, f.Clock);
        var options = new DecisionIntelligenceOptions { Endpoint = Environment.GetEnvironmentVariable("AGENTCORE_KEV_ENDPOINT") ?? "http://127.0.0.1:8009", Enabled = true, Backend = DecisionBackendKind.LocalKev, ShadowMode = false,
            ApiKey = (await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("AGENTCORE_KEV_KEY_FILE")!)).Trim(),
            RequestTimeoutMs = 2000, MaxConcurrentRequests = 1 };
        using var client = new HttpClient(LocalKevDecisionAdapter.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var adapter = new LocalKevDecisionAdapter(client, options);
        // Cold model load/first inference is not part of Run admission. Never renew its bounded budget.
        using var warmup = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        Assert.Equal(DecisionStatus.Ready, (await adapter.EvaluateAsync(new(DecisionPurpose.ContextRelevance, DecisionKind.Choice,
            [new("paris", "Paris is the capital of France."), new("other", "Accounting totals.")],
            new Dictionary<string,string> { ["task"] = "What is the capital of France?" }, new(), "warmup"), warmup.Token)).Status);
        var backend = new CountingLiveKev(adapter);
        using var engine = new DecisionEngine(backend, options);
        var runService = new TenantRunService(f.Iam, sessions, runs, f.RunConfiguration(), f.Identity, f.Authorizer, reader, f.Ids, f.Clock, routing: new(engine));
        var session = await service.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision,
            ModelPreference.Auto);
        Assert.Equal(0, backend.Calls);
        var message = f.Ids.NewId();
        var securityRevision = await Revision();
        var watch = Stopwatch.StartNew();
        var admitted = await runService.SubmitTextAsync(tenant, session.Owner.SessionId, human, message, "Diagnose the root cause of a deadlock and prove the proposed fix preserves the invariant.", securityRevision, 1);
        var complexAdmissionMs = watch.Elapsed.TotalMilliseconds;
        var run = (await runs.FindTenantRunAsync(tenant, admitted.RunId))!;
        Assert.Equal(other.OfferingId.ToString("D"), run.PinnedModel.OfferingId);
        Assert.Equal("high", run.PinnedModel.ReasoningEffort);
        Assert.Equal("localKev", run.PinnedModel.Decision!.EngineId);
        Assert.Equal(DecisionIntelligenceOptions.KevModel, run.PinnedModel.Decision.EngineVersion);
        Assert.Equal("ranked", run.PinnedModel.Decision.Basis);
        Assert.Equal(2, backend.Calls);
        var replay = await runService.SubmitTextAsync(tenant, run.SessionId, human, message, "Diagnose the root cause of a deadlock and prove the proposed fix preserves the invariant.", await Revision(), 1);
        Assert.False(replay.Created); Assert.Equal(run.AgentRunId, replay.RunId); Assert.Equal(2, backend.Calls);
        var greetingSession = await service.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision, ModelPreference.Auto);
        watch.Restart();
        var greeting = await runService.SubmitTextAsync(tenant, greetingSession.Owner.SessionId, human, f.Ids.NewId(), "Greet the team.", await Revision(), 1);
        var greetingAdmissionMs = watch.Elapsed.TotalMilliseconds;
        var greetingRun = (await runs.FindTenantRunAsync(tenant, greeting.RunId))!;
        Assert.Equal(direct.OfferingId.ToString("D"), greetingRun.PinnedModel.OfferingId);
        Assert.Equal("ranked", greetingRun.PinnedModel.Decision!.Basis); Assert.Null(greetingRun.PinnedModel.ReasoningEffort);
        Assert.Equal(3, backend.Calls);
        var reopened = new SqliteIamStore(f.Contexts!, clock: f.Clock);
        Assert.Equal(JsonSerializer.Serialize(run.PinnedModel), JsonSerializer.Serialize((await reopened.FindTenantRunAsync(tenant, run.AgentRunId))!.PinnedModel));
        if (Environment.GetEnvironmentVariable("AGENTCORE_ROUTING_ADMISSION_RESULTS") is { Length: > 0 } destination)
            await File.WriteAllTextAsync(destination, JsonSerializer.Serialize(new { policyRevision = AutoModelRouter.PolicyRevision,
                admissionBudgetMs = options.RequestTimeoutMs, complexAdmissionMs, greetingAdmissionMs, backend.Calls, backend.StageElapsedMs,
                complexPin = run.PinnedModel, greetingPin = greetingRun.PinnedModel }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private sealed class CountingLiveKev(IDecisionBackend inner) : IDecisionBackend
    {
        public int Calls;
        public List<double> StageElapsedMs { get; } = [];
        public string Id => inner.Id;
        public string Version => inner.Version;
        public bool PreAdmissionSupported => inner.PreAdmissionSupported;
        public ValueTask<DecisionStatus> ReadinessAsync(CancellationToken ct) => inner.ReadinessAsync(ct);
        public async ValueTask<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls); var watch = Stopwatch.StartNew();
            try { return await inner.EvaluateAsync(request, ct); }
            finally { StageElapsedMs.Add(watch.Elapsed.TotalMilliseconds); }
        }
    }
}
