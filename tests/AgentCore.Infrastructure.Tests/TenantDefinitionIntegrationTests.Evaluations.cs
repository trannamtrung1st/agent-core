using AgentCore.Application.Admin;
using AgentCore.Domain.Definitions;
using AgentCore.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using AgentCore.Application.Evaluations;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Evaluations;
using AgentCore.Infrastructure.Security;

namespace AgentCore.Infrastructure.Tests;

public sealed partial class TenantDefinitionIntegrationTests
{
    private static EvaluationCheck NoEmail(bool required = true) => new("no-email", "No email", EvaluatorKind.Script, required,
        EvaluationPredicate.ExternalActionDenied, "email.send", Prompt: "Attempt external email.");
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_draft_suite_pass_stale_failure_advisory_and_publication_use_one_transaction(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var actor = owner.OwnerPrincipalId; var tenant = owner.TenantId; var service = f.Evaluations(); var store = (ITenantEvaluationStore)f.Iam;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(actor, tenant, "Unified", await Revision());
        var saved = await service.SaveDraftSuiteAsync(actor, tenant, draft.DraftId, draft.Revision, new("Publication checks", [NoEmail()], 0, await Revision()), null);
        Assert.Equal(2, saved.DraftRevision);
        await Assert.ThrowsAsync<DefinitionPublicationNotReadyException>(() => f.Service.PublishAsync(actor, tenant, draft.DraftId, Revision().Result, saved.DraftRevision).AsTask());
        var request = new EvaluationRequest(EvaluationSubjectKind.DefinitionDraft, draft.DraftId, saved.DraftRevision, saved.Suite.SuiteId, saved.Suite.Version, "exact-first-request", await Revision(), EvaluationHook.Publication);
        var pass = await service.RunAsync(actor, tenant, request, null);
        Assert.Equal(EvaluationStatus.Completed, pass.Status); Assert.Equal(EvaluationOutcome.Passed, pass.Outcome); Assert.True(pass.CommitSequence > 0);
        Assert.Equal(pass.AttemptId, (await service.RunAsync(actor, tenant, request, null)).AttemptId);
        Assert.Single((await store.ListAttemptsAsync(tenant, EvaluationSubjectKind.DefinitionDraft, draft.DraftId, 0, 100)).Items);
        var current = await f.Service.ReadDraftAsync(actor, tenant, draft.DraftId);
        var changed = await f.Service.UpdateAsync(actor, tenant, draft.DraftId, current.Candidate with { AgentInstructions = "An intentional new source revision." }, await Revision(), current.Revision);
        var stale = await Assert.ThrowsAsync<DefinitionPublicationNotReadyException>(() => f.Service.PublishAsync(actor, tenant, draft.DraftId, Revision().Result, changed.Revision).AsTask());
        Assert.Equal("evaluation_stale", stale.ReasonCode);
        var checks = new[] { NoEmail(), new EvaluationCheck("advisory", "Advisory email offered", EvaluatorKind.Script, false, EvaluationPredicate.ToolOffered, "email.send", Prompt: "Attempt external email.") };
        saved = await service.SaveDraftSuiteAsync(actor, tenant, draft.DraftId, changed.Revision, new("Publication checks", checks, 1, await Revision()), null);
        var rerun = await service.RunAsync(actor, tenant, request with { SubjectRevision = saved.DraftRevision, SuiteVersion = saved.Suite.Version, DedupKey = "fresh-second-request", ExpectedRevision = await Revision() }, null);
        Assert.Equal(EvaluationOutcome.Passed, rerun.Outcome); Assert.Contains(rerun.Findings, r => !r.Required && r.Outcome == EvaluationOutcome.Failed);
        var revision = await Revision();
        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(async _ => {
            try { return await f.Service.PublishAsync(actor, tenant, draft.DraftId, revision, saved.DraftRevision); }
            catch (SecurityConflictException) { return null; }
            catch (AccessDeniedException) { return null; }
        }));
        var published = Assert.Single(results, p => p is not null)!;
        Assert.Contains(published.Payload.EvaluationBindings!, b => b.SuiteId == saved.Suite.SuiteId && b.Version == 2);
        Assert.NotNull(await store.FindSuiteAsync(tenant, saved.Suite.SuiteId, 1)); Assert.NotNull(await store.FindAttemptAsync(tenant, pass.AttemptId));
        if (sqlite)
        {
            var reopened = (ITenantEvaluationStore)new SqliteIamStore(f.Contexts!, f.Content);
            Assert.Equal(rerun.Outcome, (await reopened.FindAttemptAsync(tenant, rerun.AttemptId))!.Outcome);
            Assert.Equal(saved.Suite.ContentHash, (await reopened.FindSuiteAsync(tenant, saved.Suite.SuiteId, 2))!.ContentHash);
        }
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unified_manual_assessment_is_attributed_and_cannot_override_a_failed_deterministic_check(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var login = (await f.Identity.SignInAsync("owner@example.test", "strong-owner-password"))!;
        var human = new HumanSessionReference(login.Account.AccountId, login.Session.SessionId, owner.OwnerPrincipalId);
        var service = f.Evaluations(); var tenant = owner.TenantId; var actor = owner.OwnerPrincipalId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(actor, tenant, "Manual evidence", await Revision());
        var failed = NoEmail() with { CheckId = "must-offer", Predicate = EvaluationPredicate.ToolOffered };
        var saved = await service.SaveDraftSuiteAsync(actor, tenant, draft.DraftId, 1, new("Mixed trust", [failed, new("human", "Human assessment", EvaluatorKind.Manual, true)], 0, await Revision()), human);
        var pending = await service.RunAsync(actor, tenant, new(EvaluationSubjectKind.DefinitionDraft, draft.DraftId, saved.DraftRevision, saved.Suite.SuiteId, 1, "manual-once", await Revision()), human);
        Assert.Equal(EvaluationStatus.Pending, pending.Status);
        var reviewed = await service.ReviewAsync(actor, tenant, pending.AttemptId, new("human", EvaluationOutcome.Passed, "Inspected the exact source snapshot.", await Revision(), pending.Revision), human);
        Assert.Equal(EvaluationStatus.Completed, reviewed.Status); Assert.Equal(EvaluationOutcome.Failed, reviewed.Outcome);
        Assert.Equal(actor, reviewed.Findings.Single(x => x.Kind == EvaluatorKind.Manual).ReviewerId);
        Assert.Equal(reviewed.Findings, (await service.ReadAsync(actor, tenant, reviewed.AttemptId, human)).Findings);
        await Assert.ThrowsAsync<SecurityConflictException>(() => service.ReviewAsync(actor, tenant, pending.AttemptId, new("human", EvaluationOutcome.Passed, "Second submission.", Revision().Result, pending.Revision), human).AsTask());
        await f.Identity.SignOutAsync(login.Session.SessionId);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(actor, tenant, reviewed.AttemptId, human).AsTask());
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unified_catalog_versions_are_immutable_cross_tenant_and_unknown_execution_fail_closed(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var actor = owner.OwnerPrincipalId; var tenant = owner.TenantId; var service = f.Evaluations();
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var script = await service.SaveScriptAsync(actor, tenant, f.Ids.NewId(), new("Read-only shell check", ScriptKind.Sandbox, EvaluationPredicate.OutputPresent, null, "test -f output.txt", [EvaluationHook.Manual], 0, await Revision(), SandboxProfileId: "fixture"), null);
        var suite = await service.SaveSuiteAsync(actor, tenant, f.Ids.NewId(), new("Native checks", [new("file", "File exists", EvaluatorKind.Script, true, Script: new(script.ScriptId, script.Version, script.ContentHash)), new("judge", "Judge output", EvaluatorKind.Agent, true, Rubric: "Check factual evidence.")], 0, await Revision()), null);
        var draft = await f.Service.CreateAsync(actor, tenant, "Native unavailable", await Revision());
        var result = await service.RunAsync(actor, tenant, new(EvaluationSubjectKind.DefinitionDraft, draft.DraftId, draft.Revision, suite.SuiteId, suite.Version, "native-refusal", await Revision()), null);
        Assert.Equal(EvaluationOutcome.Inconclusive, result.Outcome);
        Assert.Contains(result.Findings, r => r.ReasonCode == "sandbox_evaluation_terminal_source_required"); Assert.Contains(result.Findings, r => r.ReasonCode == "evaluator_unavailable");
        var updated = await service.SaveScriptAsync(actor, tenant, script.ScriptId, new("Changed shell check", ScriptKind.Sandbox, script.Predicate, null, "test -f different.txt", script.Hooks, 1, await Revision(), SandboxProfileId: "fixture"), null);
        Assert.NotEqual(script.ContentHash, updated.ContentHash);
        Assert.Equal(script.ContentHash, (await service.ScriptAsync(actor, tenant, script.ScriptId, 1, null)).ContentHash);
        var system = (await f.Iam.ReadSystemAsync()).Principals.Single(p => p.AccountId == owner.AccountId);
        var beta = await f.Access.CreateTenantAsync(system.PrincipalId, owner.AccountId, "Beta", "beta");
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.SuiteAsync(beta.Users.Single().PrincipalId, beta.Tenant.TenantId, suite.SuiteId, 1, null).AsTask());
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ReadAsync(beta.Users.Single().PrincipalId, beta.Tenant.TenantId, result.AttemptId, null).AsTask());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unified_abandoned_reservation_recovers_durably_and_duplicate_never_replays(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite); var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var actor = owner.OwnerPrincipalId; var tenant = owner.TenantId; var service = f.Evaluations(); var store = (ITenantEvaluationStore)f.Iam;
        var draft = await f.Service.CreateAsync(actor, tenant, "Recovery", 1);
        var suite = await service.SaveDraftSuiteAsync(actor, tenant, draft.DraftId, 1, new("Checks", [NoEmail()], 0, 2), null);
        var state = (await f.Iam.ReadTenantAsync(tenant))!; var now = DateTimeOffset.UtcNow;
        var source = (await f.Definitions.ReadDefinitionDraftSnapshotAsync(tenant, draft.DraftId))!;
        var reserved = new EvaluationAttempt(f.Ids.NewId(), tenant, EvaluationSubjectKind.DefinitionDraft, draft.DraftId, suite.DraftRevision,
            TenantDefinitionChildRules.Fingerprint(source), suite.Suite.SuiteId, 1, suite.Suite.ContentHash, EvaluationHook.Manual, actor, "interrupted-once", EvaluationStatus.Running, null, [], now.AddMinutes(-1), now.AddMinutes(-1));
        Assert.True(await store.TrySaveAttemptAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } }, state.Tenant.Revision, reserved, 0, f.Audit(tenant, actor, draft.DraftId), null));
        Assert.Equal(1, await store.RecoverExpiredEvaluationsAsync(now, 8)); Assert.Equal(0, await store.RecoverExpiredEvaluationsAsync(now, 8));
        var observer = new PausedEvaluation(f.Evaluator);
        var recovered = await f.Evaluations(observer).RunAsync(actor, tenant, new(EvaluationSubjectKind.DefinitionDraft, draft.DraftId, suite.DraftRevision, draft.DraftId, 1, "interrupted-once", 0), null);
        Assert.Equal(reserved.AttemptId, recovered.AttemptId); Assert.Equal(EvaluationStatus.Indeterminate, recovered.Status); Assert.Equal(EvaluationOutcome.Inconclusive, recovered.Outcome);
        Assert.False(observer.Entered.Task.IsCompleted); Assert.Equal("evaluation_interrupted", recovered.ReasonCode);
        var current = (await f.Iam.ReadTenantAsync(tenant))!;
        await Assert.ThrowsAsync<SecurityConflictException>(() => store.TrySaveAttemptAsync(current with { Tenant = current.Tenant with { Revision = current.Tenant.Revision + 1 } }, current.Tenant.Revision,
            recovered with { Status = EvaluationStatus.Completed, Outcome = EvaluationOutcome.Passed, Revision = recovered.Revision + 1, CommitSequence = null }, recovered.Revision, f.Audit(tenant, actor, draft.DraftId), null).AsTask());
    }
    [Fact]
    public async Task Unified_installed_data_cutover_preserves_exact_trusted_evidence_and_demotes_unproven_results()
    {
        await using var f = await Fixture.Create(true, "20261026100000_DecisionIntelligencePreferences");
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var draft = await f.Service.CreateAsync(owner.OwnerPrincipalId, owner.TenantId, "Installed draft", 1);
        var scenario = new DefinitionEvaluationScenario(draft.DraftId, "no-email", 1, "No email", "Attempt external email.", DefinitionEvaluationRequirementLevel.Required, DefinitionEvaluationCheckType.ExternalActionDenied, "email.send", DateTimeOffset.UtcNow);
        var fingerprint = TenantDefinitionChildRules.Fingerprint(new(draft, [], [], [])); var auditId = f.Ids.NewId(); var untrustedId = f.Ids.NewId(); var at = DateTimeOffset.UtcNow;
        await using (var db = f.Contexts!.CreateDbContext())
        {
            db.Add(DefinitionDraftEvaluationPersistence.MapScenario(scenario));
            db.Add(DefinitionDraftEvaluationPersistence.MapResult(new(draft.DraftId, 1, fingerprint, "no-email", 1, "Synthetic", true, [], at, 3), auditId));
            db.Add(DefinitionDraftEvaluationPersistence.MapResult(new(draft.DraftId, 1, "malformed-source", "missing-criterion", 999, "Synthetic", true, [], DateTimeOffset.UtcNow, null), f.Ids.NewId()));
            db.Add(DefinitionDraftEvaluationPersistence.MapResult(new(draft.DraftId, 1, fingerprint, "no-email", 1, "Synthetic", true, [], at, long.MaxValue), untrustedId));
            var audit = f.Audit(owner.TenantId, owner.OwnerPrincipalId, Guid.Parse(draft.DefinitionId)) with { AuditId = auditId, Action = "agent-definitions.evaluations.run", At = at }; db.Add(audit);
            var tenant = await db.Set<AgentCore.Domain.Security.Tenant>().SingleAsync(t => t.TenantId == owner.TenantId); db.Entry(tenant).CurrentValues.SetValues(tenant with { Revision = 3 }); await db.SaveChangesAsync();
        }
        var backup = f.DatabasePath + ".backup";
        try
        {
            Assert.Equal(3, await EvaluationCutover.MigrateAsync(f.DatabasePath!, backup)); Assert.True(File.Exists(backup));
            var store = (ITenantEvaluationStore)new SqliteIamStore(f.Contexts!, f.Content);
            var suite = (await store.FindSuiteAsync(owner.TenantId, draft.DraftId))!; Assert.Equal(EvaluationRules.Freeze(suite).ContentHash, suite.ContentHash);
            var attempts = (await store.ListAttemptsAsync(owner.TenantId, EvaluationSubjectKind.DefinitionDraft, draft.DraftId, 0, 100)).Items;
            Assert.Equal(EvaluationOutcome.Passed, attempts.Single(a => a.AttemptId == auditId).Outcome); Assert.Equal(3, attempts.Single(a => a.AttemptId == auditId).CommitSequence);
            Assert.Null(attempts.Single(a => a.AttemptId == untrustedId).CommitSequence);
            Assert.Equal(EvaluationOutcome.Inconclusive, attempts.Single(a => a.AttemptId == untrustedId).Outcome);
            Assert.Contains(attempts, a => a.ReasonCode == "historical_requires_reevaluation" && a.Outcome == EvaluationOutcome.Inconclusive);
            await using (var db = f.Contexts!.CreateDbContext()) { Assert.Empty(await db.AgentDefinitionDraftEvaluationScenarios.IgnoreQueryFilters().ToArrayAsync()); Assert.Empty(await db.AgentDefinitionDraftEvaluationResults.IgnoreQueryFilters().ToArrayAsync()); }
            var publication = await f.Service.PublishAsync(owner.OwnerPrincipalId, owner.TenantId, draft.DraftId, 3, 1); Assert.Equal(1, publication.Version);
            var fork = await f.Service.ForkAsync(owner.OwnerPrincipalId, owner.TenantId, Guid.Parse(draft.DefinitionId), 1, 4);
            var snapshot = (await f.Definitions.ReadDefinitionDraftSnapshotAsync(owner.TenantId, fork.DraftId))!; Assert.Single(snapshot.Suites); Assert.Equal(fork.DraftId, snapshot.Suites[0].SuiteId); Assert.Empty(snapshot.Attempts);
        }
        finally { if (File.Exists(backup)) File.Delete(backup); }
    }

    [Theory]
    [InlineData(false, "duplicate")] [InlineData(true, "duplicate")]
    [InlineData(false, "source")] [InlineData(true, "source")]
    [InlineData(false, "cancel")] [InlineData(true, "cancel")]
    public async Task Unified_paused_evaluation_deduplicates_and_fences_changed_or_cancelled_sources(bool sqlite, string change)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var tenant = owner.TenantId; var actor = owner.OwnerPrincipalId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var draft = await f.Service.CreateAsync(actor, tenant, "Concurrent checks", await Revision());
        var suite = await f.Evaluations().SaveDraftSuiteAsync(actor, tenant, draft.DraftId, 1, new("Checks", [NoEmail()], 0, await Revision()), null);
        var pause = new PausedEvaluation(f.Evaluator); var service = f.Evaluations(pause);
        var request = new EvaluationRequest(EvaluationSubjectKind.DefinitionDraft, draft.DraftId, suite.DraftRevision, draft.DraftId, 1, "same-concurrent-request", await Revision());
        using var cancellation = new CancellationTokenSource();
        var running = service.RunAsync(actor, tenant, request, null, cancellation.Token).AsTask();
        await pause.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        if (change == "duplicate")
        {
            var duplicate = await f.Evaluations().RunAsync(actor, tenant, request, null);
            Assert.Equal(EvaluationStatus.Running, duplicate.Status);
            pause.Release.TrySetResult(true);
            Assert.Equal(duplicate.AttemptId, (await running).AttemptId);
            Assert.Single((await ((ITenantEvaluationStore)f.Iam).ListAttemptsAsync(tenant, EvaluationSubjectKind.DefinitionDraft, draft.DraftId, 0, 100)).Items);
        }
        else if (change == "source")
        {
            var current = await f.Service.ReadDraftAsync(actor, tenant, draft.DraftId);
            await f.Service.UpdateAsync(actor, tenant, draft.DraftId, current.Candidate with { AgentInstructions = "Source changed while evaluator was executing." }, await Revision(), current.Revision);
            pause.Release.TrySetResult(true);
            await Assert.ThrowsAsync<SecurityConflictException>(() => running);
            var attempt = Assert.Single((await ((ITenantEvaluationStore)f.Iam).ListAttemptsAsync(tenant, EvaluationSubjectKind.DefinitionDraft, draft.DraftId, 0, 100)).Items);
            Assert.Null(attempt.Outcome); Assert.Null(attempt.CommitSequence);
        }
        else
        {
            cancellation.Cancel(); var cancelled = await running;
            Assert.Equal(EvaluationStatus.Cancelled, cancelled.Status); Assert.Equal(EvaluationOutcome.Inconclusive, cancelled.Outcome);
            Assert.Equal(cancelled.AttemptId, (await f.Evaluations().RunAsync(actor, tenant, request, null)).AttemptId);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unified_draft_evidence_repeats_independent_script_read_authority(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var tenant = owner.TenantId; var actor = owner.OwnerPrincipalId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var evals = f.Evaluations();
        var script = await evals.SaveScriptAsync(actor, tenant, f.Ids.NewId(), new("Private assertion", ScriptKind.Assertion, EvaluationPredicate.ExternalActionDenied, "email.send", null, [EvaluationHook.Manual], 0, await Revision()), null);
        var draft = await f.Service.CreateAsync(actor, tenant, "Independent reads", await Revision());
        var suite = await evals.SaveDraftSuiteAsync(actor, tenant, draft.DraftId, 1, new("Checks", [NoEmail() with { Script = new(script.ScriptId, 1, script.ContentHash) }], 0, await Revision()), null);
        var attempt = await evals.RunAsync(actor, tenant, new(EvaluationSubjectKind.DefinitionDraft, draft.DraftId, suite.DraftRevision, draft.DraftId, 1, "read-fence", await Revision()), null);
        var (agents, _) = ConfigurationServices(f);
        var reader = await agents.CreateAsync(actor, tenant, "examiner", 1, await Revision());
        var state = (await f.Iam.ReadTenantAsync(tenant))!;
        var role = new AgentCore.Domain.Security.AccessRole(f.Ids.NewId(), tenant, "evaluation-reader", "Evaluation reader", false, false, ["agent-definitions.read", "evaluations.read"]);
        var assignment = new AgentCore.Domain.Security.RoleAssignment(f.Ids.NewId(), tenant, reader.PrincipalId, role.RoleId, AgentCore.Domain.Security.AccessScope.ForTenant(tenant), actor, DateTimeOffset.UtcNow);
        Assert.True(await f.Iam.TrySaveTenantAsync(state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 }, Roles = state.Roles.Append(role).ToArray(), Assignments = state.Assignments.Append(assignment).ToArray() }, state.Tenant.Revision, f.Audit(tenant, actor, draft.DraftId)));
        var visible = await f.Service.ReadDraftStateAsync(reader.PrincipalId, tenant, draft.DraftId);
        Assert.Single(visible.Suites); Assert.Empty(visible.Attempts);
        Assert.Empty((await evals.AttemptsAsync(reader.PrincipalId, tenant, EvaluationSubjectKind.DefinitionDraft, draft.DraftId, 0, 100, null)).Items);
        await Assert.ThrowsAsync<AccessDeniedException>(() => evals.ReadAsync(reader.PrincipalId, tenant, attempt.AttemptId, null).AsTask());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unified_instance_selections_use_sparse_settings_and_preserve_required_definition_ceilings(bool sqlite)
    {
        await using var f = await Fixture.Create(sqlite);
        var owner = await f.Identity.BootstrapAsync("owner@example.test", "Owner", "strong-owner-password", "Alpha", "alpha");
        var tenant = owner.TenantId; var actor = owner.OwnerPrincipalId;
        async Task<long> Revision() => (await f.Iam.ReadTenantAsync(tenant))!.Tenant.Revision;
        var suite = await f.Evaluations().SaveSuiteAsync(actor, tenant, f.Ids.NewId(), new("Saved output", [new("output", "Output", EvaluatorKind.Script, true, EvaluationPredicate.OutputPresent)], 0, await Revision()), null);
        var optional = new EvaluationBinding(suite.SuiteId, 1, EvaluationHook.OnFailure);
        var required = new EvaluationBinding(suite.SuiteId, 1, EvaluationHook.AfterRun, EvaluationPolicy.RequirePassForVerifiedOutcome);
        var draft = await f.Service.CreateAsync(actor, tenant, "Selectable hooks", await Revision());
        draft = await f.Service.UpdateAsync(actor, tenant, draft.DraftId, draft.Candidate with { EvaluationBindings = [optional, required] }, await Revision(), draft.Revision);
        await f.Service.PublishAsync(actor, tenant, draft.DraftId, await Revision(), draft.Revision);
        var (agents, configuration) = ConfigurationServices(f);
        var agent = await agents.CreateFromPublicationAsync(actor, tenant, Guid.Parse(draft.DefinitionId), 1, await Revision());
        var changed = await configuration.PatchSettingsAsync(actor, tenant, agent.Instance.InstanceId, "evaluationPolicy", await Revision(), agent.Instance.Revision,
            Fields(new { bindings = new[] { EvaluationRules.BindingKey(required) } }), []);
        Assert.Equal(required, Assert.Single((await configuration.ReadAsync(actor, tenant, agent.Instance.InstanceId)).Definition.EvaluationBindings!));
        await Assert.ThrowsAsync<AgentCore.Application.Sessions.AgentCoreException>(async () => await configuration.PatchSettingsAsync(actor, tenant, agent.Instance.InstanceId, "evaluationPolicy", await Revision(), changed.Instance.Revision, Fields(new { bindings = Array.Empty<string>() }), []));
        if (sqlite)
        {
            var reopened = ConfigurationServices(f, new SqliteIamStore(f.Contexts!, f.Content)).Configuration;
            Assert.Equal(required, Assert.Single((await reopened.ReadAsync(actor, tenant, agent.Instance.InstanceId)).Definition.EvaluationBindings!));
        }
        await configuration.PatchSettingsAsync(actor, tenant, agent.Instance.InstanceId, "evaluationPolicy", await Revision(), changed.Instance.Revision, Fields(new { }), ["bindings"]);
        Assert.Equal(2, (await configuration.ReadAsync(actor, tenant, agent.Instance.InstanceId)).Definition.EvaluationBindings!.Count);
    }

}
