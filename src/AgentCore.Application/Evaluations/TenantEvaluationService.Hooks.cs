using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Evaluations;

namespace AgentCore.Application.Evaluations;

public sealed record RunVerificationSummary(string Status, int RequiredChecks, int PassedChecks, IReadOnlyList<Guid> AttemptIds, string? ObservationReason = null, DateTimeOffset? RetryAfter = null);

public sealed partial class TenantEvaluationService
{
    private int terminalScanOffset;
    private static EvaluationLifecycleOrigin Origin(AgentRun run) => new(run.AgentRunId, run.Revision, run.Owner.ExecutorPrincipalId!.Value, run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId);
    private static bool Applicable(EvaluationHook hook, AgentRun run) => hook == EvaluationHook.AfterRun && run.IsTerminal
        || hook == EvaluationHook.OnFailure && run.Status is AgentRunStatus.Failed or AgentRunStatus.Cancelled;
    private static bool TrustedObservation(EvaluationAttempt attempt, AgentRun run) => attempt.LifecycleOrigin == Origin(run)
        && attempt.ActorId == run.Owner.ExecutorPrincipalId && attempt.DedupKey == HookKey(run, attempt.SuiteId, attempt.SuiteVersion, attempt.Hook);
    public static string HookKey(AgentRun run, Guid suite, int version, EvaluationHook hook) => $"hook:{run.AgentRunId:D}:{suite:D}:{version}:{(int)hook}:{run.Revision}";
    public async ValueTask<bool> BeforeRunAsync(AgentRun run, CancellationToken ct)
    {
        var bindings = (run.Admission.Configuration?.Definition.EvaluationBindings ?? []).Where(b => b.Hook == EvaluationHook.BeforeRun).ToArray();
        var allowed = true;
        var seconds = Math.Max(1, Math.Min(EvaluationRules.TimeoutSeconds, (run.Admission.ExecutionBudget?.Profile.DurationSeconds ?? 40) / 4));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(seconds), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, budget.Token);
        foreach (var binding in bindings)
        {
            if (budget.IsCancellationRequested) { ct.ThrowIfCancellationRequested(); if (binding.Policy == EvaluationPolicy.RequirePassBeforeRun) allowed = false; continue; }
            try
            {
                var attempt = await ObserveRun(run, binding, linked.Token, ct);
                if (binding.Policy == EvaluationPolicy.RequirePassBeforeRun && (attempt.Status is not (EvaluationStatus.Completed or EvaluationStatus.Pending)
                    || !attempt.Findings.Any(f => f.Required) || attempt.Findings.Any(f => f.Required && f.Outcome != EvaluationOutcome.Passed))) allowed = false;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { if (binding.Policy == EvaluationPolicy.RequirePassBeforeRun) allowed = false; }
            catch (Exception error) when (binding.Policy != EvaluationPolicy.RequirePassBeforeRun && error is AccessDeniedException or SecurityConflictException or AgentCore.Application.Sessions.AgentCoreException) { }
        }
        return allowed;
    }
    public async ValueTask ObserveTerminalsAsync(int limit, CancellationToken ct)
    {
        _ = await store.RecoverExpiredEvaluationsAsync(time.GetUtcNow(), limit, ct);
        limit = Math.Clamp(limit, 1, 32);
        lock (deferredGate) if (scanPauseUntil > time.GetUtcNow()) return;
        var suppressed = await SuppressedRuns(ct);
        var offset = Volatile.Read(ref terminalScanOffset);
        var candidates = await store.ListPendingRunEvaluationsAsync(limit, ct, offset, suppressed);
        Interlocked.Exchange(ref terminalScanOffset, candidates.Count < limit || offset > 100_000 - limit ? 0 : offset + limit);
        foreach (var run in candidates)
        {
            var denied = false; var authorityOnly = true; var catalogSensitive = false;
            foreach (var binding in (run.Admission.Configuration?.Definition.EvaluationBindings ?? []).Where(b => Applicable(b.Hook, run)))
            {
                try { _ = await ObserveRun(run, binding, ct); }
                catch (Exception error) when (error is AccessDeniedException or SecurityConflictException or AgentCore.Application.Sessions.AgentCoreException)
                {
                    denied = true; authorityOnly &= error is AccessDeniedException;
                    catalogSensitive |= error is AccessDeniedException { ReasonCode: "evaluation_suite_unavailable" or "script_unavailable" };
                    // No evaluation occurred and no passing evidence is manufactured.
                }
            }
            if (denied) await DeferObservation(run, authorityOnly, catalogSensitive, ct);
            else lock (deferredGate) deferred.Remove(run.AgentRunId);
        }
    }
    private async ValueTask<EvaluationAttempt> ObserveRun(AgentRun run, EvaluationBinding binding, CancellationToken ct, CancellationToken? originCancellation = null)
    {
        if (run.Owner.TenantId is not { } tenant || run.Owner.ExecutorPrincipalId is not { } actor) throw new AccessDeniedException("tenant_context_required");
        var state = await State(actor, tenant, null, ct); var suite = await store.FindSuiteAsync(tenant, binding.SuiteId, binding.Version, ct) ?? throw new AccessDeniedException("evaluation_suite_unavailable");
        await SuitePermission(state, actor, suite, "read", ct); await SuitePermission(state, actor, suite, "run", ct);
        var source = await Subject(state, actor, EvaluationSubjectKind.AgentRun, run.AgentRunId, false, ct);
        if (source.Revision != run.Revision) throw new SecurityConflictException("evaluation_source_changed");
        if (run.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is { } initiator)
            _ = await Subject(state, initiator, EvaluationSubjectKind.AgentRun, run.AgentRunId, false, ct);
        await EvaluationBindingValidation.RequireAsync(store, tenant, binding, suite, ct);
        var key = HookKey(run, suite.SuiteId, suite.Version, binding.Hook);
        var duplicate = await store.FindDedupAsync(tenant, actor, key, ct); if (duplicate is not null) return duplicate;
        if (suite.Archived || (await store.FindSuiteAsync(tenant, suite.SuiteId, ct: ct))?.Archived != false) throw new AccessDeniedException("evaluation_suite_unavailable");
        await ScriptPermissions(state, actor, suite, true, ct);
        if (!await slots.WaitAsync(0, ct)) throw new SecurityConflictException("evaluation_capacity");
        var executionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        EvaluationAttempt? settled = null;
        try
        {
            var seconds = binding.Hook == EvaluationHook.BeforeRun ? Math.Max(1, Math.Min(EvaluationRules.TimeoutSeconds, (run.Admission.ExecutionBudget?.Profile.DurationSeconds ?? 40) / 4)) : EvaluationRules.TimeoutSeconds;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(seconds), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var pins = modelEvaluator is null ? null : await modelEvaluator.PinAsync(state, actor, suite, linked.Token);
            var now = time.GetUtcNow();
            var attempt = new EvaluationAttempt(ids.NewId(), tenant, EvaluationSubjectKind.AgentRun, run.AgentRunId, run.Revision, source.Fingerprint,
                suite.SuiteId, suite.Version, suite.ContentHash, binding.Hook, actor, key, EvaluationStatus.Running, null, [], now, now, ModelPins: pins, LifecycleOrigin: Origin(run));
            if (!await store.TrySaveAttemptAsync(Advance(state), state.Tenant.Revision, attempt, 0, Audit(state, actor, "evaluations.run", attempt.AttemptId), null, linked.Token)) throw new SecurityConflictException("stale_revision");
            var findings = new List<EvaluationFinding>();
            try
            {
                // Both assertion and model checks are read-only. Native effects still require independent Sandbox admission.
                foreach (var check in suite.Checks) findings.Add(await Evaluate(check, suite, source, binding.Hook, linked.Token, attempt));
                var status = findings.Any(f => f.ReasonCode == "review_required") ? EvaluationStatus.Pending : EvaluationStatus.Completed;
                return settled = await Settle(attempt, findings, status, null, null, linked.Token);
            }
            catch (OperationCanceledException)
            {
                return settled = await Settle(attempt, findings, (originCancellation ?? ct).IsCancellationRequested ? EvaluationStatus.Cancelled : EvaluationStatus.Indeterminate,
                    (originCancellation ?? ct).IsCancellationRequested ? "cancelled" : "evaluation_timeout", null, CancellationToken.None);
            }
        }
        finally { slots.Release(); AgentCore.Application.Observability.EvaluationTelemetry.Record(binding.Hook, settled?.Status ?? EvaluationStatus.Indeterminate, settled?.Outcome, System.Diagnostics.Stopwatch.GetElapsedTime(executionStarted).TotalMilliseconds); }
    }
    public async ValueTask<RunVerificationSummary> SummaryAsync(Guid actor, Guid tenant, AgentRun run, HumanSessionReference? human, CancellationToken ct)
    {
        var state = await State(actor, tenant, human, ct); _ = await Subject(state, actor, EvaluationSubjectKind.AgentRun, run.AgentRunId, false, ct);
        var bindings = (run.Admission.Configuration?.Definition.EvaluationBindings ?? []).Where(b => b.Hook == EvaluationHook.Manual || Applicable(b.Hook, run)).ToArray();
        var findings = new List<EvaluationFinding>(); var attempts = new List<Guid>(); var expected = 0; var stale = false;
        foreach (var binding in bindings)
        {
            var suite = await store.FindSuiteAsync(tenant, binding.SuiteId, binding.Version, ct) ?? throw new AccessDeniedException("evaluation_suite_unavailable");
            await SuitePermission(state, actor, suite, "read", ct); await ScriptPermissions(state, actor, suite, false, ct);
            expected += suite.Checks.Count(c => c.Required);
            EvaluationAttempt? latest = null;
            // Search every bounded page; unrelated newer Suites cannot hide this binding's evidence.
            for (var pageNumber = 0; latest is null; pageNumber++)
            {
                var page = await store.ListAttemptsAsync(tenant, EvaluationSubjectKind.AgentRun, run.AgentRunId, pageNumber, 100, ct);
                latest = page.Items.FirstOrDefault(a => a.SuiteId == suite.SuiteId && a.SuiteVersion == suite.Version && a.Hook == binding.Hook && a.CommitSequence is > 0 && (binding.Hook == EvaluationHook.Manual || TrustedObservation(a, run)));
                if ((pageNumber + 1) * page.PageSize >= page.Total) break;
            }
            if (latest is null) continue;
            attempts.Add(latest.AttemptId);
            if (latest.SubjectRevision != run.Revision || latest.SourceFingerprint != EvaluationRules.Hash(new { run.AgentRunId, run.Revision, run.Status })) { stale = true; continue; }
            findings.AddRange(latest.Findings.Where(f => f.Required).Select(f => latest.Status is EvaluationStatus.Completed or EvaluationStatus.Pending ? f : f with { Outcome = EvaluationOutcome.Inconclusive }));
        }
        await Current(state, human, ct);
        var passed = findings.Count(f => f.Outcome == EvaluationOutcome.Passed);
        var status = expected == 0 ? "NotConfigured" : stale ? "Stale" : findings.Any(f => f.Outcome == EvaluationOutcome.Failed) ? "Failed"
            : passed == expected ? "Verified" : attempts.Count == 0 ? "NotEvaluated" : "Inconclusive";
        DeferredObservation? observation; lock (deferredGate) deferred.TryGetValue(run.AgentRunId, out observation);
        return new(status, expected, passed, attempts, observation is null ? null : "automatic_not_performed", observation is { Count: < 5 } pending ? pending.RetryAt : null);
    }
}
