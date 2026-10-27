using AgentCore.Application.Admin;
using AgentCore.Application.Ports;
using AgentCore.Application.Security;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Evaluations;
using AgentCore.Domain.Security;

namespace AgentCore.Application.Evaluations;

public sealed record SuiteWrite(string Name, IReadOnlyList<EvaluationCheck> Checks, int ExpectedVersion, long ExpectedRevision, bool Archived = false);
public sealed record ScriptWrite(string Name, ScriptKind Kind, EvaluationPredicate Predicate, string? Expected, string? Body,
    IReadOnlyList<EvaluationHook> Hooks, int ExpectedVersion, long ExpectedRevision, bool Archived = false, string? SandboxProfileId = null, int SandboxTimeoutSeconds = 10, SandboxEvaluationInput SandboxInput = SandboxEvaluationInput.RunMetadata);
public sealed record EvaluationRequest(EvaluationSubjectKind SubjectKind, Guid SubjectId, long SubjectRevision, Guid SuiteId, int SuiteVersion,
    string DedupKey, long ExpectedRevision, EvaluationHook Hook = EvaluationHook.Manual);
public sealed record ManualAssessment(string CheckId, EvaluationOutcome Outcome, string Reason, long ExpectedRevision, long ExpectedAttemptRevision);

// Catalog and attached observations only. Native effect authority remains with the incumbent Run/Sandbox owners.
public sealed partial class TenantEvaluationService(IIamStore security, ITenantEvaluationStore store, ITenantDefinitionStore definitions,
    ITenantRunStore runs, ITenantSessionStore sessions, IamAuthorizer authorizer,
    IdentityService identity, IIdGenerator ids, TimeProvider time, AgentDefinitionDraftSyntheticEvaluationRunner synthetic,
    IDefinitionResourceContentStore content, EvaluationModelEvaluator? modelEvaluator = null,
    IEvaluationSandboxExecutor? nativeSandbox = null, EvaluationSandboxAuthority? nativeAuthority = null)
{
    private readonly SemaphoreSlim slots = new(EvaluationRules.MaxConcurrent);
    public async ValueTask<EvaluationPage<EvaluationSuiteVersion>> SuitesAsync(Guid actor, Guid tenant, int page, int size, HumanSessionReference? human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); var allowed = await Readable(state, actor, false, ct);
        var value = await store.ListSuitesAsync(tenant, allowed, page, size, ct); await Current(state, human, ct); return value;
    }
    public async ValueTask<EvaluationPage<ScriptVersion>> ScriptsAsync(Guid actor, Guid tenant, int page, int size, HumanSessionReference? human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); var allowed = await Readable(state, actor, true, ct);
        var value = await store.ListScriptsAsync(tenant, allowed, page, size, ct); await Current(state, human, ct); return value;
    }
    public async ValueTask<EvaluationSuiteVersion> SuiteAsync(Guid actor, Guid tenant, Guid id, int? version, HumanSessionReference? human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); var suite = await store.FindSuiteAsync(tenant, id, version, ct) ?? throw new AccessDeniedException("resource_unavailable");
        await SuitePermission(state, actor, suite, "read", ct); await Current(state, human, ct); return suite;
    }
    public async ValueTask<ScriptVersion> ScriptAsync(Guid actor, Guid tenant, Guid id, int? version, HumanSessionReference? human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); var script = await store.FindScriptAsync(tenant, id, version, ct) ?? throw new AccessDeniedException("resource_unavailable");
        await Require(state, actor, "scripts.read", EvaluationStoreRules.Scope(tenant, id, true), ct); await Current(state, human, ct); return script;
    }
    public async ValueTask<EvaluationSuiteVersion> SaveSuiteAsync(Guid actor, Guid tenant, Guid id, SuiteWrite request, HumanSessionReference? human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct);
        var before = await store.FindSuiteAsync(tenant, id, ct: ct);
        if (before?.DraftId is not null) throw new ArgumentException("Use the Definition draft Suite editor.");
        await Require(state, actor, "evaluations.manage", before is null ? AccessScope.ForTenant(tenant) : EvaluationStoreRules.Scope(tenant, id), ct);
        if (before is not null) await SuitePermission(state, actor, before, "read", ct);
        Revision(state, request.ExpectedRevision);
        var value = EvaluationRules.Freeze(new EvaluationSuiteVersion(tenant, id, request.ExpectedVersion + 1, request.Name, request.Checks, "", actor, time.GetUtcNow(), Archived: request.Archived));
        await ScriptPermissions(state, actor, value, false, ct);
        if (!await store.TrySaveSuiteAsync(Advance(state), state.Tenant.Revision, value, request.ExpectedVersion, Audit(state, actor, "evaluations.manage", id), human, ct)) throw new SecurityConflictException("stale_revision");
        return value;
    }
    public async ValueTask<ScriptVersion> SaveScriptAsync(Guid actor, Guid tenant, Guid id, ScriptWrite request, HumanSessionReference? human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); var before = await store.FindScriptAsync(tenant, id, ct: ct);
        await Require(state, actor, "scripts.manage", before is null ? AccessScope.ForTenant(tenant) : EvaluationStoreRules.Scope(tenant, id, true), ct);
        if (before is not null) await Require(state, actor, "scripts.read", EvaluationStoreRules.Scope(tenant, id, true), ct);
        Revision(state, request.ExpectedRevision);
        if (request.Body is { } body) DefinitionResourcePolicies.RejectSecretsInTextualContent("text/plain", System.Text.Encoding.UTF8.GetBytes(body));
        var value = EvaluationRules.Freeze(new ScriptVersion(tenant, id, request.ExpectedVersion + 1, request.Name, request.Kind, request.Predicate, request.Expected,
            request.Body, request.Hooks, "", actor, time.GetUtcNow(), request.Archived, request.SandboxProfileId, request.SandboxTimeoutSeconds, request.SandboxInput));
        if (!await store.TrySaveScriptAsync(Advance(state), state.Tenant.Revision, value, request.ExpectedVersion, Audit(state, actor, "scripts.manage", id), human, ct)) throw new SecurityConflictException("stale_revision");
        return value;
    }
    public async ValueTask<DraftSuiteSaved> SaveDraftSuiteAsync(Guid actor, Guid tenant, Guid draftId, long draftRevision, SuiteWrite request, HumanSessionReference? human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); var snapshot = await Subject(state, actor, EvaluationSubjectKind.DefinitionDraft, draftId, true, ct);
        await Require(state, actor, "evaluations.manage", AccessScope.ForTenant(tenant), ct); Revision(state, request.ExpectedRevision);
        if (snapshot.Revision != draftRevision) throw new SecurityConflictException("stale_revision");
        var value = EvaluationRules.Freeze(new EvaluationSuiteVersion(tenant, draftId, request.ExpectedVersion + 1, request.Name, request.Checks, "", actor, time.GetUtcNow(), draftId, DefinitionId: Guid.Parse(snapshot.Draft!.Draft.DefinitionId)));
        await ScriptPermissions(state, actor, value, false, ct);
        await EvaluationBindingValidation.RequireAsync(store, tenant, new(draftId, value.Version, EvaluationHook.Publication), value, ct);
        return await store.TrySaveDraftSuiteAsync(Advance(state), state.Tenant.Revision, draftId, draftRevision, value,
            Audit(state, actor, "evaluations.manage", draftId), human, ct) ?? throw new SecurityConflictException("stale_revision");
    }
    public async ValueTask<EvaluationAttempt> RunAsync(Guid actor, Guid tenant, EvaluationRequest request, HumanSessionReference? human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); var suite = await store.FindSuiteAsync(tenant, request.SuiteId, request.SuiteVersion, ct) ?? throw new AccessDeniedException("resource_unavailable");
        await SuitePermission(state, actor, suite, "run", ct); await SuitePermission(state, actor, suite, "read", ct);
        var subject = await Subject(state, actor, request.SubjectKind, request.SubjectId, true, ct);
        if (!Enum.IsDefined(request.Hook) || request.SubjectKind == EvaluationSubjectKind.DefinitionDraft && request.Hook is not (EvaluationHook.Manual or EvaluationHook.Publication)
            || request.SubjectKind == EvaluationSubjectKind.AgentRun && (request.Hook != EvaluationHook.Manual || !subject.Run!.IsTerminal)) throw new ArgumentException("Public Run evaluation requires a terminal Run and the Manual hook. Automatic lifecycle provenance is assigned only by the runtime.");
        if (suite.DraftId is { } draft && (request.SubjectKind != EvaluationSubjectKind.DefinitionDraft || draft != request.SubjectId)) throw new AccessDeniedException("resource_unavailable");
        var duplicate = await store.FindDedupAsync(tenant, actor, request.DedupKey, ct);
        if (duplicate is not null)
        {
            if (duplicate.SubjectKind != request.SubjectKind || duplicate.SubjectId != request.SubjectId || duplicate.SubjectRevision != request.SubjectRevision
                || duplicate.SuiteId != suite.SuiteId || duplicate.SuiteVersion != suite.Version || duplicate.Hook != request.Hook) throw new SecurityConflictException("dedup_key_reused");
            return await ReadAsync(actor, tenant, duplicate.AttemptId, human, ct);
        }
        Revision(state, request.ExpectedRevision);
        if (subject.Revision != request.SubjectRevision || suite.Archived || (await store.FindSuiteAsync(tenant, suite.SuiteId, ct: ct))?.Archived != false) throw new SecurityConflictException("evaluation_source_changed");
        await ScriptPermissions(state, actor, suite, true, ct);
        if (!await slots.WaitAsync(0, ct)) throw new SecurityConflictException("evaluation_capacity");
        var executionStarted = System.Diagnostics.Stopwatch.GetTimestamp();
        EvaluationAttempt? settled = null;
        try
        {
            var pins = modelEvaluator is null ? null : await modelEvaluator.PinAsync(state, actor, suite, ct);
            var now = time.GetUtcNow(); var attempt = new EvaluationAttempt(ids.NewId(), tenant, request.SubjectKind, request.SubjectId, request.SubjectRevision,
                subject.Fingerprint, suite.SuiteId, suite.Version, suite.ContentHash, request.Hook, actor, request.DedupKey, EvaluationStatus.Running, null, [], now, now, ModelPins: pins, SourceDefinitionId: subject.Draft is { } definitionDraft ? Guid.Parse(definitionDraft.Draft.DefinitionId) : null);
            if (!await store.TrySaveAttemptAsync(Advance(state), state.Tenant.Revision, attempt, 0, Audit(state, actor, "evaluations.run", attempt.AttemptId), human, ct))
            {
                var raced = await store.FindDedupAsync(tenant, actor, request.DedupKey, ct);
                if (raced is not null) return await RunAsync(actor, tenant, request, human, ct);
                throw new SecurityConflictException("stale_revision");
            }
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(EvaluationRules.TimeoutSeconds), time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            var findings = new List<EvaluationFinding>();
            try
            {
                foreach (var check in suite.Checks) findings.Add(await Evaluate(check, suite, subject, request.Hook, linked.Token, attempt, human));
                return settled = await Settle(attempt, findings, findings.Any(f => f.ReasonCode is "review_required" or "sandbox_approval_required") ? EvaluationStatus.Pending : EvaluationStatus.Completed, null, human, linked.Token);
            }
            catch (OperationCanceledException)
            {
                return settled = await Settle(attempt, findings, ct.IsCancellationRequested ? EvaluationStatus.Cancelled : EvaluationStatus.Indeterminate,
                    ct.IsCancellationRequested ? "cancelled" : "evaluation_timeout", human, CancellationToken.None);
            }
        }
        finally { slots.Release(); AgentCore.Application.Observability.EvaluationTelemetry.Record(request.Hook, settled?.Status ?? EvaluationStatus.Indeterminate, settled?.Outcome, System.Diagnostics.Stopwatch.GetElapsedTime(executionStarted).TotalMilliseconds); }
    }
    public async ValueTask<EvaluationAttempt?> RecoverAsync(Guid actor, Guid tenant, string key, HumanSessionReference? human, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 128) throw new ArgumentException("Use the original evaluation request key.");
        var state = await State(actor, tenant, human, ct);
        var attempt = await store.FindDedupAsync(tenant, actor, key, ct);
        await Current(state, human, ct);
        return attempt is null ? null : await ReadAsync(actor, tenant, attempt.AttemptId, human, ct);
    }
    public async ValueTask<EvaluationAttempt> ReadAsync(Guid actor, Guid tenant, Guid id, HumanSessionReference? human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); var attempt = await store.FindAttemptAsync(tenant, id, ct) ?? throw new AccessDeniedException("resource_unavailable");
        var suite = await store.FindSuiteAsync(tenant, attempt.SuiteId, attempt.SuiteVersion, ct) ?? throw new AccessDeniedException("resource_unavailable");
        await SuitePermission(state, actor, suite, "read", ct); _ = await Subject(state, actor, attempt.SubjectKind, attempt.SubjectId, false, ct);
        await ScriptPermissions(state, actor, suite, false, ct); await Current(state, human, ct);
        // A crashed process owns no replay right. The saved reservation is projected as unknown after its bounded deadline.
        return attempt.Status == EvaluationStatus.Running && time.GetUtcNow() > attempt.CreatedAt.AddSeconds(EvaluationRules.TimeoutSeconds + 5)
            ? attempt with { Status = EvaluationStatus.Indeterminate, Outcome = EvaluationOutcome.Inconclusive, ReasonCode = "evaluation_interrupted" } : attempt;
    }
    public async ValueTask<EvaluationPage<EvaluationAttempt>> AttemptsAsync(Guid actor, Guid tenant, EvaluationSubjectKind kind, Guid subject, int page, int size, HumanSessionReference? human, CancellationToken ct = default)
    {
        var state = await State(actor, tenant, human, ct); _ = await Subject(state, actor, kind, subject, false, ct);
        var sources = await store.ListAttemptSourcesAsync(tenant, kind, subject, ct); var allowed = new List<Guid>();
        foreach (var group in sources.GroupBy(s => (s.SuiteId, s.SuiteVersion)))
        {
            var suite = await store.FindSuiteAsync(tenant, group.Key.SuiteId, group.Key.SuiteVersion, ct);
            if (suite is null) continue;
            try { await SuitePermission(state, actor, suite, "read", ct); await ScriptPermissions(state, actor, suite, false, ct); allowed.AddRange(group.Select(s => s.AttemptId)); }
            catch (AccessDeniedException) { }
        }
        var saved = await store.ListAttemptsAsync(tenant, kind, subject, page, size, ct, allowed);
        await Current(state, human, ct); return saved;
    }
    public async ValueTask<EvaluationAttempt> ReviewAsync(Guid actor, Guid tenant, Guid id, ManualAssessment request, HumanSessionReference? human, CancellationToken ct = default)
    {
        if (human is null) throw new AccessDeniedException("human_session_required");
        var state = await State(actor, tenant, human, ct); var attempt = await ReadAsync(actor, tenant, id, human, ct);
        var suite = (await store.FindSuiteAsync(tenant, attempt.SuiteId, attempt.SuiteVersion, ct))!;
        await SuitePermission(state, actor, suite, "review", ct); Revision(state, request.ExpectedRevision);
        if (!Enum.IsDefined(request.Outcome) || string.IsNullOrWhiteSpace(request.Reason) || request.Reason.Length > EvaluationRules.MaxFindingsCharacters) throw new ArgumentException("Choose an outcome and enter a reason of at most 2000 characters.");
        if (attempt.Status != EvaluationStatus.Pending || attempt.Revision != request.ExpectedAttemptRevision
            || !attempt.Findings.Any(f => f.CheckId == request.CheckId && f.Kind == EvaluatorKind.Manual && f.ReasonCode is "review_required" or "sandbox_approval_required")) throw new SecurityConflictException("evaluation_immutable");
        var subject = await Subject(state, actor, attempt.SubjectKind, attempt.SubjectId, false, ct);
        if (subject.Revision != attempt.SubjectRevision || subject.Fingerprint != attempt.SourceFingerprint) throw new SecurityConflictException("evaluation_source_changed");
        var findings = attempt.Findings.Select(f => f.CheckId == request.CheckId ? f with { Outcome = request.Outcome, ReasonCode = "human_attestation", Summary = request.Reason.Trim(), ReviewerId = actor, ReviewedAt = time.GetUtcNow() } : f).ToArray();
        return await Settle(attempt, findings, findings.Any(f => f.ReasonCode is "review_required" or "sandbox_approval_required") ? EvaluationStatus.Pending : EvaluationStatus.Completed, null, human, ct);
    }
    private async ValueTask<EvaluationAttempt> Settle(EvaluationAttempt before, IReadOnlyList<EvaluationFinding> findings, EvaluationStatus status, string? reason, HumanSessionReference? human, CancellationToken ct, bool native = false)
    {
        var state = await State(human?.PrincipalId ?? before.ActorId, before.TenantId, human, ct); var actor = human?.PrincipalId ?? before.ActorId;
        var suite = (await store.FindSuiteAsync(before.TenantId, before.SuiteId, before.SuiteVersion, ct))!;
        await SuitePermission(state, actor, suite, before.Status == EvaluationStatus.Pending && !native ? "review" : "run", ct);
        var source = await Subject(state, actor, before.SubjectKind, before.SubjectId, before.Status != EvaluationStatus.Pending || native, ct);
        if (human is null && source.Run?.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is { } original)
            _ = await Subject(state, original, before.SubjectKind, before.SubjectId, false, ct);
        await ScriptPermissions(state, actor, suite, before.Status != EvaluationStatus.Pending || native, ct);
        if (status is EvaluationStatus.Cancelled or EvaluationStatus.Indeterminate)
            findings = suite.Checks.Select(c => findings.FirstOrDefault(f => f.CheckId == c.CheckId)
                ?? Finding(c, EvaluationOutcome.Inconclusive, reason ?? "evaluation_interrupted", "This check did not complete; older evidence does not establish the current attempt.")).ToArray();
        var value = before with { Status = status, Findings = findings, Outcome = status == EvaluationStatus.Completed ? EvaluationRules.Aggregate(findings) : EvaluationOutcome.Inconclusive,
            ReasonCode = reason, UpdatedAt = time.GetUtcNow(), Revision = before.Revision + 1, CommitSequence = null };
        if (!await store.TrySaveAttemptAsync(Advance(state), state.Tenant.Revision, value, before.Revision, Audit(state, actor, before.Status == EvaluationStatus.Pending && !native ? "evaluations.review" : "evaluations.run", before.AttemptId), human, ct)) throw new SecurityConflictException("evaluation_source_changed");
        return (await store.FindAttemptAsync(before.TenantId, before.AttemptId, ct))!;
    }
    private async ValueTask<EvaluationFinding> Evaluate(EvaluationCheck check, EvaluationSuiteVersion suite, SubjectSnapshot subject, EvaluationHook hook, CancellationToken ct, EvaluationAttempt? attempt = null, HumanSessionReference? human = null)
    {
        if (check.Kind == EvaluatorKind.Manual) return Finding(check, EvaluationOutcome.Inconclusive, "review_required", "Awaiting an attributable human assessment. This is not IAM approval.");
        if (check.Kind == EvaluatorKind.Agent)
        {
            if (attempt is null || modelEvaluator is null) return Finding(check, EvaluationOutcome.Inconclusive, "evaluator_unavailable", "Select an eligible evaluator model explicitly.");
            var evidence = System.Text.Json.JsonSerializer.Serialize(subject.Run is { } observed
                ? (object)new { observed.Status, observed.Revision, Output = observed.Result?.Text[..Math.Min(observed.Result.Text.Length, 4000)], observed.Result?.OutcomeKind }
                : new { DraftRevision = subject.Revision, ConfigurationFingerprint = subject.Fingerprint });
            return await modelEvaluator.EvaluateAsync(check, attempt, evidence, async token => {
                var state = await State(attempt.ActorId, attempt.TenantId, human, token);
                await SuitePermission(state, attempt.ActorId, suite, "run", token);
                var current = await Subject(state, attempt.ActorId, attempt.SubjectKind, attempt.SubjectId, true, token);
                if (human is null && subject.Run?.Admission.ExecutionContext?.OriginalInitiatorPrincipalId is { } original)
                    _ = await Subject(state, original, attempt.SubjectKind, attempt.SubjectId, false, token);
                var reservation = await store.FindAttemptAsync(attempt.TenantId, attempt.AttemptId, token);
                if (reservation is not { Status: EvaluationStatus.Running } || reservation.Revision != attempt.Revision
                    || current.Revision != attempt.SubjectRevision || current.Fingerprint != attempt.SourceFingerprint
                    || (await store.FindSuiteAsync(attempt.TenantId, suite.SuiteId, ct: token))?.Archived != false) throw new AccessDeniedException("evaluation_source_changed");
                var pins = await modelEvaluator.PinAsync(state, attempt.ActorId, suite, token);
                var pin = pins.SingleOrDefault(p => p.CheckId == check.CheckId);
                if (pin is null || attempt.ModelPins?.SingleOrDefault(p => p.CheckId == check.CheckId)?.Binding.Equals(pin.Binding) != true)
                    throw new AccessDeniedException("evaluation_model_changed");
            }, ct);
        }
        if (check.Script is { } reference)
        {
            var script = (await store.FindScriptAsync(suite.TenantId, reference.ScriptId, reference.Version, ct))!;
            if (!script.Hooks.Contains(hook == EvaluationHook.Publication ? EvaluationHook.Manual : hook)) return Finding(check, EvaluationOutcome.Inconclusive, "script_hook_denied", "This Script version does not allow this hook.");
            if (script.Kind == ScriptKind.Sandbox) return await PrepareSandboxCheck(check, script, subject, attempt, human, ct);
            check = check with { Predicate = script.Predicate, Expected = script.Expected };
        }
        if (subject.Draft is { } draft && (int)check.Predicate <= (int)EvaluationPredicate.ExternalActionDenied)
        {
            var resources = new List<DefinitionDraftSyntheticResourceSnapshot>();
            foreach (var r in draft.Resources)
            {
                var bytes = await DefinitionResourceStoredContent.RequireForBindingAsync(content, r.ContentSha256, r.ByteLength, r.MediaType, ct);
                resources.Add(new(r.LogicalPath, r.Kind, r.ContentSha256, bytes));
            }
            var scenario = new DefinitionEvaluationScenario(draft.Draft.DraftId, check.CheckId, 1, check.Title, check.Prompt ?? "Check the saved configuration.",
                check.Required ? DefinitionEvaluationRequirementLevel.Required : DefinitionEvaluationRequirementLevel.Advisory, (DefinitionEvaluationCheckType)check.Predicate, check.Expected, time.GetUtcNow());
            var result = await synthetic.RunAsync(draft.Draft.Candidate, resources, scenario, ct);
            return Finding(check, result.Passed ? EvaluationOutcome.Passed : EvaluationOutcome.Failed, result.Passed ? "assertion_passed" : "assertion_failed",
                result.Passed ? "The specified offline Synthetic check passed." : string.Join(" ", result.Findings)[..Math.Min(EvaluationRules.MaxFindingsCharacters, string.Join(" ", result.Findings).Length)]);
        }
        bool? passed = check.Predicate switch
        {
            EvaluationPredicate.RunCompleted when subject.Run is { } run => run.Status == AgentRunStatus.Completed,
            EvaluationPredicate.OutputPresent when subject.Run is { } run => !string.IsNullOrWhiteSpace(run.Result?.Text),
            EvaluationPredicate.OutcomeSucceeded when subject.Run is { } run => run.Status == AgentRunStatus.Completed && run.Result?.OutcomeKind is AgentRunOutcomeKind.Response or AgentRunOutcomeKind.NoAction,
            EvaluationPredicate.InputPresent when subject.Run is { } run => run.Admission.Activation.SourceEntryIds.Count > 0 || !string.IsNullOrWhiteSpace(run.Admission.TaskIntent?.Text),
            _ => null
        };
        return Finding(check, passed is null ? EvaluationOutcome.Inconclusive : passed.Value ? EvaluationOutcome.Passed : EvaluationOutcome.Failed,
            passed is null ? "unsupported_subject_check" : passed.Value ? "assertion_passed" : "assertion_failed", passed is null ? "This predicate does not apply to this subject." : "Observed only the specified saved metadata condition; this does not prove broader task success.");
    }
    private static EvaluationFinding Finding(EvaluationCheck c, EvaluationOutcome outcome, string reason, string summary) => new(c.CheckId, c.Kind, c.Required, outcome, reason, summary);
    private sealed record SubjectSnapshot(long Revision, string Fingerprint, TenantDefinitionDraftSnapshot? Draft = null, AgentRun? Run = null);
    private async ValueTask<SubjectSnapshot> Subject(TenantSecurityState state, Guid actor, EvaluationSubjectKind kind, Guid id, bool execute, CancellationToken ct)
    {
        var tenant = state.Tenant.TenantId;
        if (kind == EvaluationSubjectKind.DefinitionDraft)
        {
            var draft = await definitions.ReadDefinitionDraftSnapshotAsync(tenant, id, ct);
            if (draft is null && !execute)
            {
                var suite = await store.FindSuiteAsync(tenant, id, ct: ct);
                var history = await store.ListAttemptsAsync(tenant, EvaluationSubjectKind.DefinitionDraft, id, 0, 1, ct);
                if ((suite?.DefinitionId ?? history.Items.FirstOrDefault()?.SourceDefinitionId) is not { } definition) throw new AccessDeniedException("resource_unavailable");
                await Require(state, actor, "agent-definitions.read", AccessScope.ForResource(tenant, "definition", definition.ToString("D")), ct);
                foreach (var version in await definitions.ListDefinitionPublicationsAsync(tenant, definition, ct))
                {
                    var publication = (await definitions.FindDefinitionPublicationAsync(tenant, definition, version.Version, ct))!;
                    // Associated Suites link directly; reusable-only drafts retain a trusted source owner in their attempt.
                    if (suite is not null && !(publication.Payload.EvaluationBindings ?? []).Any(b => b.SuiteId == id)) continue;
                    var resources = await definitions.ListOwnedPublicationResourcesAsync(tenant, definition, version.Version, ct);
                    foreach (var resource in resources) await Require(state, actor, "resources.read", TenantDefinitionResourceScopes.Publication(tenant, definition, version.Version, resource.ResourceId), ct);
                    return new(publication.SourceDraftRevision, EvaluationRules.Hash(publication.Payload));
                }
                throw new AccessDeniedException("resource_unavailable");
            }
            if (draft is null) throw new AccessDeniedException("resource_unavailable");
            var scope = AccessScope.ForResource(tenant, "definition", draft.Draft.DefinitionId);
            await Require(state, actor, "agent-definitions.read", scope, ct);
            if (execute) await Require(state, actor, "agent-definitions.update", scope, ct);
            foreach (var resource in draft.Resources) await Require(state, actor, "resources.read", TenantDefinitionResourceScopes.Draft(tenant, Guid.Parse(draft.Draft.DefinitionId), id, resource.ResourceId), ct);
            return new(draft.Draft.Revision, TenantDefinitionChildRules.Fingerprint(draft), draft);
        }
        if (kind != EvaluationSubjectKind.AgentRun) throw new ArgumentException("Unknown evaluation subject.");
        var run = await runs.FindTenantRunAsync(tenant, id, ct) ?? throw new AccessDeniedException("resource_unavailable");
        _ = await sessions.FindTenantSessionAsync(tenant, run.SessionId, ct) ?? throw new AccessDeniedException("resource_unavailable");
        await Require(state, actor, "agent-runs.read", TenantRunRules.Scope(tenant, id), ct);
        await Require(state, actor, "sessions.read", TenantSessionRules.Scope(tenant, run.SessionId), ct);
        await Require(state, actor, "agents.read", AccessScope.ForResource(tenant, "agent", run.AgentInstanceId.ToString("D")), ct);
        return new(run.Revision, EvaluationRules.Hash(new { run.AgentRunId, run.Revision, run.Status }), Run: run);
    }
    private async ValueTask ScriptPermissions(TenantSecurityState state, Guid actor, EvaluationSuiteVersion suite, bool execute, CancellationToken ct)
    {
        foreach (var reference in suite.Checks.Select(c => c.Script).OfType<ScriptReference>())
        {
            var script = await store.FindScriptAsync(suite.TenantId, reference.ScriptId, reference.Version, ct) ?? throw new AccessDeniedException("script_unavailable");
            await Require(state, actor, "scripts.read", EvaluationStoreRules.Scope(suite.TenantId, reference.ScriptId, true), ct);
            if (script.ContentHash != reference.ContentHash) throw new AccessDeniedException("script_unavailable");
            if (execute)
            {
                await Require(state, actor, "scripts.execute", EvaluationStoreRules.Scope(suite.TenantId, reference.ScriptId, true), ct);
                if (script.Archived || (await store.FindScriptAsync(suite.TenantId, reference.ScriptId, ct: ct))?.Archived != false) throw new AccessDeniedException("script_unavailable");
            }
        }
    }
    private async ValueTask SuitePermission(TenantSecurityState state, Guid actor, EvaluationSuiteVersion suite, string action, CancellationToken ct)
    {
        await Require(state, actor, "evaluations." + action, suite.DraftId is null ? EvaluationStoreRules.Scope(suite.TenantId, suite.SuiteId) : AccessScope.ForTenant(suite.TenantId), ct);
        if (suite.DraftId is { } draft) _ = await Subject(state, actor, EvaluationSubjectKind.DefinitionDraft, draft, action is "manage" or "run", ct);
    }
    private async ValueTask<TenantSecurityState> State(Guid actor, Guid tenant, HumanSessionReference? human, CancellationToken ct)
    {
        if (human is not null)
        {
            if (human.PrincipalId != actor) throw new AccessDeniedException("initiator_unavailable");
            return await TenantHumanSessionAuthority.RequireAsync(security, identity, tenant, human, ct);
        }
        return await security.ReadTenantAsync(tenant, ct) ?? throw new AccessDeniedException("tenant_unavailable");
    }
    private async ValueTask Current(TenantSecurityState state, HumanSessionReference? human, CancellationToken ct)
    { if ((await State(human?.PrincipalId ?? Guid.Empty, state.Tenant.TenantId, human, ct)).Tenant.Revision != state.Tenant.Revision) throw new SecurityConflictException("stale_revision"); }
    private async ValueTask Require(TenantSecurityState state, Guid actor, string permission, AccessScope scope, CancellationToken ct)
    { var decision = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct); if (!decision.Allowed) throw new AccessDeniedException(decision.ReasonCode); }
    private async ValueTask<IReadOnlyList<Guid>?> Readable(TenantSecurityState state, Guid actor, bool script, CancellationToken ct)
    {
        var permission = script ? "scripts.read" : "evaluations.read";
        var all = await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), AccessScope.ForTenant(state.Tenant.TenantId), ct);
        if (all.Allowed) return null; if (all.ReasonCode != "permission_denied") throw new AccessDeniedException(all.ReasonCode);
        var allowed = new List<Guid>();
        foreach (var scope in state.Assignments.Where(a => a.PrincipalId == actor && a.Scope.ResourceType == (script ? "script" : "evaluation-suite")).Select(a => a.Scope).Distinct())
            if (EvaluationStoreRules.TryScope(scope, out var id) && (await authorizer.CheckTenantAsync(state, actor, PermissionCatalog.Get(permission), scope, ct)).Allowed) allowed.Add(id);
        return allowed;
    }
    private AuthorizationAudit Audit(TenantSecurityState state, Guid actor, string action, Guid target) => new(ids.NewId(), state.Tenant.TenantId, actor, action, target.ToString("D"), "allowed", "authorized", time.GetUtcNow());
    private static TenantSecurityState Advance(TenantSecurityState state) => state with { Tenant = state.Tenant with { Revision = state.Tenant.Revision + 1 } };
    private static void Revision(TenantSecurityState state, long expected) { if (state.Tenant.Revision != expected) throw new SecurityConflictException("stale_revision"); }
}
