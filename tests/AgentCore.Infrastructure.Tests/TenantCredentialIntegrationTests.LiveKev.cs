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
        var other = await models.SaveOfferingAsync(human, tenant, null, new(setup.Connection.ConnectionId, null, "alternate-model", "Alternate model",
            setup.Offering.Transport, setup.Offering.Capabilities, true, false), await Revision(), 0);
        await models.SaveDefaultAsync(human, tenant, setup.Offering.OfferingId, null, await Revision(), 0);
        await models.SaveRoutingAsync(human, tenant, new(true, true, .7), await Revision(), 1);
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var reader = new TenantModelCatalogReader((ITenantModelStore)f.Iam, f.Iam);
        var sessions = (ITenantSessionStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var service = new TenantSessionService(f.Iam, (ITenantAgentStore)f.Iam, sessions, runs, (ITenantDefinitionStore)f.Iam, f.BuiltIns,
            f.Identity, f.Authorizer, reader, new(), f.Ids, f.Clock);
        var options = new DecisionIntelligenceOptions { Endpoint = Environment.GetEnvironmentVariable("AGENTCORE_KEV_ENDPOINT") ?? "http://127.0.0.1:8009", Enabled = true, Backend = DecisionBackendKind.LocalKev, ShadowMode = false,
            ApiKey = (await File.ReadAllTextAsync(Environment.GetEnvironmentVariable("AGENTCORE_KEV_KEY_FILE")!)).Trim(),
            RequestTimeoutMs = 5000, MaxConcurrentRequests = 1 };
        using var client = new HttpClient(LocalKevDecisionAdapter.CreateHandler()) { Timeout = Timeout.InfiniteTimeSpan };
        var backend = new CountingLiveKev(new LocalKevDecisionAdapter(client, options));
        using var engine = new DecisionEngine(backend, options);
        var runService = new TenantRunService(f.Iam, sessions, runs, f.RunConfiguration(), f.Identity, f.Authorizer, reader, f.Ids, f.Clock, routing: new(engine));
        var session = await service.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision,
            new ModelPreference("auto", Reasoning: ReasoningPreference.Default));
        Assert.Equal(0, backend.Calls);
        var message = f.Ids.NewId();
        var admitted = await runService.SubmitTextAsync(tenant, session.Owner.SessionId, human, message, "A public bounded fixture task", await Revision(), 1);
        var run = (await runs.FindTenantRunAsync(tenant, admitted.RunId))!;
        Assert.Contains(run.PinnedModel.OfferingId, new[] { setup.Offering.OfferingId.ToString("D"), other.OfferingId.ToString("D") });
        Assert.Equal("localKev", run.PinnedModel.Decision!.EngineId);
        Assert.Equal(DecisionIntelligenceOptions.KevModel, run.PinnedModel.Decision.EngineVersion);
        Assert.Contains(run.PinnedModel.Decision.Basis, new[] { "ranked", "configuredDefault" });
        Assert.Equal(1, backend.Calls);
        var replay = await runService.SubmitTextAsync(tenant, run.SessionId, human, message, "A public bounded fixture task", await Revision(), 1);
        Assert.False(replay.Created); Assert.Equal(run.AgentRunId, replay.RunId); Assert.Equal(1, backend.Calls);
        var reopened = new SqliteIamStore(f.Contexts!, clock: f.Clock);
        Assert.Equal(JsonSerializer.Serialize(run.PinnedModel), JsonSerializer.Serialize((await reopened.FindTenantRunAsync(tenant, run.AgentRunId))!.PinnedModel));
    }

    private sealed class CountingLiveKev(IDecisionBackend inner) : IDecisionBackend
    {
        public int Calls;
        public string Id => inner.Id;
        public string Version => inner.Version;
        public bool PreAdmissionSupported => inner.PreAdmissionSupported;
        public ValueTask<DecisionStatus> ReadinessAsync(CancellationToken ct) => inner.ReadinessAsync(ct);
        public ValueTask<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return inner.EvaluateAsync(request, ct); }
    }
}
