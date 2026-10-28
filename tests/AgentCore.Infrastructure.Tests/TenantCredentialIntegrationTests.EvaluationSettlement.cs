using System.Reflection;
using System.Runtime.ExceptionServices;
using AgentCore.Application.Admin;
using AgentCore.Application.Evaluations;
using AgentCore.Application.Execution;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantCredentialIntegrationTests
{
    [Theory]
    [InlineData(false, "manual-and-native")]
    [InlineData(true, "manual-and-native")]
    [InlineData(false, "tenant-revision")]
    [InlineData(true, "tenant-revision")]
    [InlineData(false, "exhausted")]
    [InlineData(true, "exhausted")]
    [InlineData(false, "revoked")]
    [InlineData(true, "revoked")]
    public async Task Sandbox_finding_settlement_merges_contention_without_replaying(bool sqlite, string race)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var f = await Fixture.Create(sqlite, clock, builtInAuthority: true);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Settlement", "settlement");
        var login = await SandboxHuman(f, owner.OwnerPrincipalId);
        var human = new TenantSessionInitiator(login.AccountId, login.AuthenticationSessionId, login.PrincipalId); var tenant = owner.TenantId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        using var http = new ProviderHttpFixture(); await using var services = RuntimeServices(f, http, new(), "Synthetic");
        var store = (ITenantEvaluationStore)f.Iam;
        var intercepted = DispatchProxy.Create<ITenantEvaluationStore, EvaluationSettlementRaceStore>();
        var contention = (EvaluationSettlementRaceStore)intercepted; contention.Inner = store;
        var native = new SettlementOnlySandbox();
        TenantEvaluationService Service(ITenantEvaluationStore backing) => new(f.Iam, backing, (ITenantDefinitionStore)f.Iam, (ITenantRunStore)f.Iam, (ITenantSessionStore)f.Iam,
            f.Authorizer, f.Identity, f.Ids, clock, services.GetRequiredService<AgentDefinitionDraftSyntheticEvaluationRunner>(),
            services.GetRequiredService<IDefinitionResourceContentStore>(), nativeSandbox: native, nativeAuthority: services.GetRequiredService<EvaluationSandboxAuthority>());
        var service = Service(intercepted); var competing = Service(store);
        var profile = (await SandboxService(f).SaveAsync(tenant, null, human, SandboxInput(), await Revision(), 0)).Profile;
        var agent = await f.AgentsAtRelease(35).CreateAsync(human.PrincipalId, tenant, "general-assistant", 35, await Revision());
        await GrantModelFixtureSpaceRead(f, tenant, human.PrincipalId, agent.PrincipalId);
        var role = await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Settlement executor",
            ["evaluations.read", "evaluations.run", "scripts.read", "scripts.execute", "agents.read", "sessions.read", "agent-runs.read", "sandbox.read", "sandbox.execute"], await Revision());
        await f.Access.AssignRoleAsync(human.PrincipalId, tenant, agent.PrincipalId, role.RoleId, AccessScope.ForTenant(tenant), await Revision());
        var session = await services.GetRequiredService<TenantSessionService>().CreateAsync(tenant, agent.Instance.InstanceId, human, SessionMode.Text, await Revision(), agent.Instance.Revision);
        var receipt = await services.GetRequiredService<TenantRunService>().SubmitTextAsync(tenant, session.Owner.SessionId, human, f.Ids.NewId(), "Settlement source", await Revision(), 1);
        var runs = (ITenantRunStore)f.Iam; var runtime = (ITenantRuntimeStore)f.Iam;
        var run = (await runs.FindTenantRunAsync(tenant, receipt.RunId))!;
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Claim(run.Revision, clock.GetUtcNow(), f.Ids.NewId(), clock.GetUtcNow().AddMinutes(5)), f.Ids.NewId());
        run = await runtime.ApplyTenantRuntimeCommandAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Fail(run.Revision, clock.GetUtcNow(), run.Claim!.Generation, "fixture", "Source remains failed", false, null), f.Ids.NewId());
        var script = await service.SaveScriptAsync(human.PrincipalId, tenant, f.Ids.NewId(), new("Settlement Script", ScriptKind.Sandbox, EvaluationPredicate.OutputPresent, null,
            "exit 0", [EvaluationHook.Manual], 0, await Revision(), SandboxProfileId: profile.Id), human);
        var suite = await service.SaveSuiteAsync(human.PrincipalId, tenant, f.Ids.NewId(), new("Mixed evidence", [
            new("native-a", "Native A", EvaluatorKind.Script, true, Script: new(script.ScriptId, 1, script.ContentHash)),
            new("native-b", "Native B", EvaluatorKind.Script, true, Script: new(script.ScriptId, 1, script.ContentHash)),
            new("human", "Human", EvaluatorKind.Manual, true)], 0, await Revision()), human);
        var attempt = await service.RunAsync(human.PrincipalId, tenant, new(EvaluationSubjectKind.AgentRun, run.AgentRunId, run.Revision, suite.SuiteId, 1, "settlement-once-" + race, await Revision()), human);
        Assert.Equal(EvaluationStatus.Pending, attempt.Status);
        var approvalService = services.GetRequiredService<TenantApprovalService>();
        var authority = services.GetRequiredService<EvaluationSandboxAuthority>();
        var executions = new List<EvaluationSandboxExecution>();
        // Seed completed command receipts through the real admission/Review/CAS stores.
        // This fixture tests publication only; native SDK settlement is covered by NativeEvaluation.
        foreach (var id in attempt.Findings.Select(x => x.SandboxExecutionId).OfType<Guid>())
        {
            var execution = (await store.FindSandboxEvaluationAsync(tenant, id))!;
            var approval = await approvalService.ReadAsync(tenant, execution.Approval.RequestId, human);
            await approvalService.DecideAsync(tenant, approval.RequestId, human, true, approval.Operation.ActionHash, await Revision(), approval.Revision);
            var state = await authority.RequireAsync(execution, CancellationToken.None, approved: true);
            execution = execution with { Revision = execution.Revision + 1, Phase = EvaluationSandboxPhase.Reserved, DispatchClaim = f.Ids.NewId(), DispatchExpiresAt = clock.GetUtcNow().AddMinutes(2) };
            Assert.True(await store.TrySaveSandboxEvaluationAsync(execution, execution.Revision - 1, expectedSecurityRevision: state.Tenant.Revision));
            execution = execution with { Revision = execution.Revision + 1, Phase = EvaluationSandboxPhase.Dispatching, CleanupPending = true };
            Assert.True(await store.TrySaveSandboxEvaluationAsync(execution, execution.Revision - 1, expectedSecurityRevision: await Revision()));
            execution = execution with { Revision = execution.Revision + 1, Phase = EvaluationSandboxPhase.Settled, InputJson = null, Outcome = EvaluationOutcome.Passed,
                ReasonCode = "sandbox_assertion_passed", ExitCode = 0, DurationMs = 12, CleanupPending = false, TerminationConfirmed = true };
            Assert.True(await store.TrySaveSandboxEvaluationAsync(execution, execution.Revision - 1, expectedSecurityRevision: await Revision()));
            executions.Add(execution);
        }
        Assert.False(await store.TryRecordSandboxSettlementUnavailableAsync(tenant, executions[0].ExecutionId, attempt.Revision - 1));
        Assert.Equal(EvaluationStatus.Pending, (await store.FindAttemptAsync(tenant, attempt.AttemptId))!.Status);
        async Task ReviewHuman()
        {
            var latest = (await store.FindAttemptAsync(tenant, attempt.AttemptId))!;
            await competing.ReviewAsync(human.PrincipalId, tenant, attempt.AttemptId, new("human", EvaluationOutcome.Passed, "Reviewed exact source", await Revision(), latest.Revision), human);
        }
        if (race != "manual-and-native") await ReviewHuman();
        var injected = 0;
        contention.BeforeSave = async _ => {
            injected++;
            if (race == "exhausted") return true;
            if (race == "tenant-revision" || race == "revoked")
            {
                if (injected <= 2)
                {
                    await f.Access.SaveRoleAsync(human.PrincipalId, tenant, null, "Unrelated revision " + injected, ["agents.read"], await Revision());
                    if (race == "revoked") await f.Identity.SignOutAsync(human.AuthenticationSessionId);
                }
                return false;
            }
            if (injected != 1) return false;
            await ReviewHuman();
            var latest = (await store.FindAttemptAsync(tenant, attempt.AttemptId))!;
            var state = (await f.Iam.ReadTenantAsync(tenant))!;
            var other = executions.Single(x => x.CheckId == "native-b");
            var findings = latest.Findings.Select(x => x.CheckId == other.CheckId ? EvaluationSandboxRules.Finding(x, other) : x).ToArray();
            Assert.True(await store.TrySaveAttemptAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision,
                latest with { Revision = latest.Revision + 1, CommitSequence = null, Findings = findings, UpdatedAt = clock.GetUtcNow() }, latest.Revision,
                f.Audit(tenant, human.PrincipalId, attempt.AttemptId), human));
            return false;
        };
        await service.ObserveTerminalsAsync(8, CancellationToken.None);
        if (race == "revoked")
        {
            var denied = (await store.FindAttemptAsync(tenant, attempt.AttemptId))!;
            Assert.Equal(EvaluationStatus.Indeterminate, denied.Status); Assert.Equal(EvaluationOutcome.Inconclusive, denied.Outcome);
            Assert.True(contention.UnavailableWrites > 0); Assert.Equal(0, native.Dispatches); return;
        }
        if (race == "exhausted")
        {
            var pending = (await store.FindAttemptAsync(tenant, attempt.AttemptId))!;
            Assert.Equal(6, injected); Assert.Equal(EvaluationStatus.Pending, pending.Status); Assert.Equal(EvaluationOutcome.Inconclusive, pending.Outcome);
            Assert.Equal(0, contention.UnavailableWrites);
            Assert.All(executions, item => Assert.Equal(EvaluationOutcome.Passed, store.FindSandboxEvaluationAsync(tenant, item.ExecutionId).Result!.Outcome));
            contention.BeforeSave = null; clock.Advance(TimeSpan.FromSeconds(31));
            await service.ObserveTerminalsAsync(8, CancellationToken.None);
        }
        var result = (await store.FindAttemptAsync(tenant, attempt.AttemptId))!;
        Assert.True(injected > 0); Assert.Equal(EvaluationStatus.Completed, result.Status); Assert.Equal(EvaluationOutcome.Passed, result.Outcome);
        Assert.Equal("human_attestation", result.Findings.Single(x => x.CheckId == "human").ReasonCode);
        Assert.All(result.Findings.Where(x => x.SandboxExecutionId is not null), x => Assert.Equal("sandbox_assertion_passed", x.ReasonCode));
        Assert.Equal(0, contention.UnavailableWrites); Assert.Equal(0, native.Dispatches);
        Assert.Equal(run.Revision, (await runs.FindTenantRunAsync(tenant, run.AgentRunId))!.Revision); Assert.Empty(http.Requests);
        if (sqlite)
        {
            var reopened = (ITenantEvaluationStore)new AgentCore.Infrastructure.Security.SqliteIamStore(f.Contexts!, clock: clock);
            Assert.Equal(EvaluationOutcome.Passed, (await reopened.FindAttemptAsync(tenant, attempt.AttemptId))!.Outcome);
        }
    }

    private sealed class SettlementOnlySandbox : IEvaluationSandboxExecutor
    {
        public string EvaluationHostScope => "settlement-fixture";
        public bool EvaluationEnabled => true;
        public int Dispatches { get; private set; }
        public ValueTask ExecuteEvaluationAsync(Guid tenant, Guid executionId, Guid claim, CancellationToken ct = default)
        { Dispatches++; throw new InvalidOperationException("Publishing retained evidence must not execute a command."); }
        public ValueTask<bool> CleanupEvaluationAsync(EvaluationSandboxExecution execution, CancellationToken ct = default) => ValueTask.FromResult(true);
    }
    public class EvaluationSettlementRaceStore : DispatchProxy
    {
        public ITenantEvaluationStore Inner = null!;
        public Func<EvaluationAttempt, ValueTask<bool>>? BeforeSave;
        public int UnavailableWrites;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            ArgumentNullException.ThrowIfNull(method); ArgumentNullException.ThrowIfNull(args);
            if (method.Name == nameof(ITenantEvaluationStore.TryRecordSandboxSettlementUnavailableAsync)) UnavailableWrites++;
            return method.Name == nameof(ITenantEvaluationStore.TrySaveAttemptAsync) && BeforeSave is not null
                ? Save(method, args) : Forward(method, args);
        }
        private async ValueTask<bool> Save(MethodInfo method, object?[] args)
        {
            if (await BeforeSave!((EvaluationAttempt)args[2]!)) return false;
            return await (ValueTask<bool>)Forward(method, args)!;
        }
        private object? Forward(MethodInfo method, object?[] args)
        {
            try { return method.Invoke(Inner, args); }
            catch (TargetInvocationException e) when (e.InnerException is not null)
            { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
    }
}
