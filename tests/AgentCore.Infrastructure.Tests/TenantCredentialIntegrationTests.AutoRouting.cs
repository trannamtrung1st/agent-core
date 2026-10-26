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
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DecisionIntelligence_actual_Auto_admission_replay_cancel_and_reopen_pin_once(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var setup = await ProviderSetup(f, ModelProviderKind.Anthropic);
        var tenant = setup.Route.TenantId; var human = new TenantSessionInitiator(setup.Human.AccountId, setup.Human.AuthenticationSessionId, setup.Human.PrincipalId);
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var modelService = Models(f);
        var other = await modelService.SaveOfferingAsync(human, tenant, null, new(setup.Connection.ConnectionId, null, "alternate-model", "Alternate model",
            setup.Offering.Transport, setup.Offering.Capabilities, true, false), await Revision(), 0);
        await modelService.SaveDefaultAsync(human, tenant, setup.Offering.OfferingId, null, await Revision(), 0);
        await modelService.SaveRoutingAsync(human, tenant, new(true, false, .7), await Revision(), 1);
        var reader = new TenantModelCatalogReader((ITenantModelStore)f.Iam, f.Iam);
        var agents = (ITenantAgentStore)f.Iam; var sessions = (ITenantSessionStore)f.Iam; var runs = (ITenantRunStore)f.Iam;
        var agent = await f.Agents.CreateAsync(human.PrincipalId, tenant, "examiner", 1, await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var service = new TenantSessionService(f.Iam, agents, sessions, runs, (ITenantDefinitionStore)f.Iam, f.BuiltIns,
            f.Identity, f.Authorizer, reader, new(), f.Ids, f.Clock);
        var backend = new AutoAdmissionBackend(other.OfferingId.ToString("D"));
        using var engine = new DecisionEngine(backend, new() { Enabled = true, Backend = DecisionBackendKind.Synthetic, ShadowMode = false });
        var runService = new TenantRunService(f.Iam, sessions, runs, f.RunConfiguration(), f.Identity, f.Authorizer, reader, f.Ids, f.Clock, routing: new(engine));
        var session = await service.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision,
            new ModelPreference("auto", Reasoning: ReasoningPreference.Default));
        Assert.Null((await sessions.FindTenantSessionAsync(tenant, session.Owner.SessionId))!.Snapshot.ModelSelection); Assert.Equal(0, backend.Calls);
        var message = f.Ids.NewId();
        var receipt = await runService.SubmitTextAsync(tenant, session.Owner.SessionId, human, message, "A bounded task", await Revision(), 1);
        var run = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        Assert.Equal(other.OfferingId.ToString("D"), run.PinnedModel.OfferingId); Assert.Equal("ranked", run.PinnedModel.Decision!.Basis);
        Assert.Equal(1, backend.Calls); Assert.Equal("auto", (await sessions.FindTenantSessionAsync(tenant, run.SessionId))!.Snapshot.ModelPreference!.Mode);
        var replay = await runService.SubmitTextAsync(tenant, run.SessionId, human, message, "A bounded task", await Revision(), 1);
        Assert.False(replay.Created); Assert.Equal(run.AgentRunId, replay.RunId); Assert.Equal(1, backend.Calls);
        if (sqlite)
        {
            var reopened = new SqliteIamStore(f.Contexts!, clock: f.Clock);
            var retained = (await reopened.FindTenantRunAsync(tenant, run.AgentRunId))!;
            Assert.Equal(run.PinnedModel.Binding, retained.PinnedModel.Binding); Assert.Equal(run.PinnedModel.Decision, retained.PinnedModel.Decision);
            Assert.Equal(other.OfferingId.ToString("D"), (await reopened.FindTenantSessionAsync(tenant, run.SessionId))!.Snapshot.ModelSelection!.OfferingId);
        }
        await runService.CancelAsync(tenant, run.AgentRunId, human, await Revision(), run.Revision);
        var edited = await service.SelectModelAsync(tenant, run.SessionId, human, await Revision(),
            (await sessions.FindTenantSessionAsync(tenant, run.SessionId))!.Snapshot.Revision, ModelPreference.Fixed(setup.Offering.OfferingId.ToString("D")));
        Assert.Equal(other.OfferingId.ToString("D"), edited.Snapshot.ModelSelection!.OfferingId);
        edited = await service.SelectModelAsync(tenant, run.SessionId, human, await Revision(), edited.Snapshot.Revision, new("auto", Reasoning: ReasoningPreference.Default));
        Assert.Equal(other.OfferingId.ToString("D"), edited.Snapshot.ModelSelection!.OfferingId);
        Assert.Equal(1, backend.Calls); // Intent edits never rerank or overwrite the observed prior Run.
        backend.Choice = setup.Offering.OfferingId.ToString("D");
        var saved = (await sessions.FindTenantSessionAsync(tenant, run.SessionId))!;
        var next = await runService.SubmitTextAsync(tenant, run.SessionId, human, f.Ids.NewId(), "Next bounded task", await Revision(), saved.Snapshot.Revision);
        Assert.Equal(setup.Offering.OfferingId.ToString("D"), (await runs.FindTenantRunAsync(tenant, next.RunId))!.PinnedModel.OfferingId);
        Assert.Equal(2, backend.Calls); Assert.Equal(other.OfferingId.ToString("D"), (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.PinnedModel.OfferingId);
        var second = (await runs.FindTenantRunAsync(tenant, next.RunId))!;
        await runService.CancelAsync(tenant, second.AgentRunId, human, await Revision(), second.Revision);
        var voiceSession = await service.CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Voice, await Revision(), agent.Instance.Revision,
            new ModelPreference("auto", Reasoning: ReasoningPreference.Default));
        await using var lease = new TenantVoiceLease(voiceSession.Owner, human, f.Ids.NewId(), f.Ids.NewId());
        await using (var operation = await lease.EnterAsync(false)) lease.Activate(operation);
        var spoken = await runService.SubmitSpeechAsync(lease, lease.StreamId, f.Ids.NewId(), "Spoken Auto task");
        Assert.Equal("voice", backend.LastRequest!.Signals["inputMode"]);
        Assert.Equal(3, backend.Calls);
        var voiceRun = (await runs.FindTenantRunAsync(tenant, spoken.Run.RunId))!;
        Assert.Equal(setup.Offering.OfferingId.ToString("D"), voiceRun.PinnedModel.OfferingId);
        Assert.Equal("ranked", voiceRun.PinnedModel.Decision!.Basis);

        saved = (await sessions.FindTenantSessionAsync(tenant, run.SessionId))!;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.Before = async ct => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); };
        using var cancellation = new CancellationTokenSource();
        var cancelled = runService.SubmitTextAsync(tenant, run.SessionId, human, f.Ids.NewId(), "Cancelled before admission", await Revision(), saved.Snapshot.Revision, ct: cancellation.Token).AsTask();
        await started.Task; cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(saved.Snapshot.Revision, (await sessions.FindTenantSessionAsync(tenant, run.SessionId))!.Snapshot.Revision);
        backend.Before = async ct => { await f.Service.UpdateAsync(human.PrincipalId, tenant, setup.Connection.CredentialId!.Value,
            new("Revoked during ranking", AgentCore.Domain.Credentials.CredentialStatus.Disabled, null, []), await Revision(), 1, initiator: setup.Human, ct: ct); };
        var beforeRevocation = await Revision();
        await Assert.ThrowsAsync<SecurityConflictException>(() => runService.SubmitTextAsync(tenant, run.SessionId, human, f.Ids.NewId(),
            "Refuse stale ranking", beforeRevocation, saved.Snapshot.Revision).AsTask());
        Assert.Equal(saved.Snapshot.Revision, (await sessions.FindTenantSessionAsync(tenant, run.SessionId))!.Snapshot.Revision);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(run.PinnedModel), System.Text.Json.JsonSerializer.Serialize((await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.PinnedModel));

    }

    private sealed class AutoAdmissionBackend(string choice) : IDecisionBackend
    {
        public string Choice { get; set; } = choice;
        public int Calls { get; private set; }
        public Func<CancellationToken, Task>? Before { get; set; }
        public DecisionRequest? LastRequest { get; private set; }
        public string Id => "synthetic-admission"; public string Version => "v1"; public bool PreAdmissionSupported => true;
        public ValueTask<DecisionStatus> ReadinessAsync(CancellationToken ct) => ValueTask.FromResult(DecisionStatus.Ready);
        public async ValueTask<DecisionResult> EvaluateAsync(DecisionRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++; LastRequest = request; if (Before is not null) await Before(ct);
            Assert.Null(request.Scope.Invocation); Assert.DoesNotContain(request.Signals.Keys, k => k.Contains("tenant", StringComparison.OrdinalIgnoreCase));
            return new DecisionResult(DecisionStatus.Ready, Choice, request.Candidates.ToDictionary(c => c.Id, c => c.Id == Choice ? 1d : 0d), null, Id, Version);
        }
    }
}
