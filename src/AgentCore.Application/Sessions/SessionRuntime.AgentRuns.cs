using System.Collections.Concurrent;
using System.Text.Json;
using AgentCore.Application.Events;
using AgentCore.Application.Execution;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private readonly AgentCore.Application.Composer.ComposerReferenceService? _composer;
    private ResolvedAgentRunConfiguration? _idleConfiguration;
    private AgentCore.Domain.Definitions.AgentDefinition ExecutionDefinition => _boundAgentRun is { } run
        ? run.Admission.Configuration?.Definition ?? throw AgentCoreErrors.Persistence("Run configuration is missing; historical execution must be migrated before resume.")
        : _idleConfiguration?.Configuration.Definition ?? _snapshot.Definition;
    private AgentCore.Domain.Definitions.AgentIdentity ExecutionPersona => _boundAgentRun?.PinnedPersona
        ?? _idleConfiguration?.Persona ?? _snapshot.PinnedPersona ?? _snapshot.Definition.Identity;

    private async ValueTask<(SessionSnapshot Projection, ResolvedAgentRunConfiguration Resolved)> ResolveNewRunAsync(CancellationToken ct)
    {
        var resolved = await _tools.ResolveRunConfigurationAsync(_snapshot, ct).ConfigureAwait(false);
        _idleConfiguration = resolved;
        var model = _snapshot.ModelSelection;
        if (_catalog is not null && model?.SelectionSource is not (ModelSelectionSource.User or ModelSelectionSource.Host))
            model = AgentCore.Application.Models.SessionModelBinder.RefreshDefault(_catalog, resolved.Configuration.Definition, model);
        _snapshot = _snapshot with { ModelSelection = model };
        return (_snapshot with { Definition = resolved.Configuration.Definition, PinnedPersona = resolved.Persona,
            PinnedPersonaRevision = resolved.Configuration.PersonaRevision }, resolved);
    }

    private readonly IAgentRunStore _agentRuns;
    private readonly IAgentRunAuthority? _runAuthority;
    private AgentRun? _boundAgentRun;
    private ProviderFailure? _agentRunProviderFailure;
    private Guid? _pendingTerminalAgentRunId;
    private Guid? _pendingTerminalResponseId;
    private bool _agentRunAdmissionPending;
    private readonly ConcurrentDictionary<Guid, byte> _admittedAgentInputs = new();
    private AgentRunOwner RunOwner => new(_snapshot.AgentInstanceId, _snapshot.ProfileId
        ?? throw AgentCoreErrors.Validation("AgentRun requires a trusted Session owner."));

    public Task<bool> RepairPendingAgentInputsAsync(bool headless, CancellationToken ct = default) =>
        DispatchAgentRunAsync(Guid.Empty, headless, ct);

    public async Task<bool> DispatchAgentRunAsync(Guid runId, bool headless, CancellationToken cancellationToken = default)
    {
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new AgentRunDispatchReceived(NewContext(), runId, headless, completed), urgent: true)) return false;
        return await completed.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private sealed record AgentRunDispatchReceived(EventContext Context, Guid AgentRunId,
        bool Headless, TaskCompletionSource<bool> Completed) : SessionInput(Context);

    private async Task HandleAgentRunDispatchAsync(AgentRunDispatchReceived input, CancellationToken ct)
    {
        try
        {
            if (_snapshot.Status == SessionStatus.Paused)
            {
                if (!SessionPauseSemantics.IsTransportResumable(_snapshot.PauseReason))
                { input.Completed.TrySetResult(false); return; }
                _snapshot = _snapshot with { Status = SessionStatus.Created,
                    LifecycleStatus = SessionLifecycleStatus.Active, PauseReason = null };
            }
            if (input.AgentRunId == Guid.Empty)
            {
                if (input.Headless) _headlessTransportDetached = true;
                input.Completed.TrySetResult(await TryStartPendingUserBatchAsync(input.Context, ct).ConfigureAwait(false));
                return;
            }
            var run = await _agentRuns.GetAsync(RunOwner, input.AgentRunId, ct).ConfigureAwait(false);
            if (run is null || run.SessionId != SessionId) { input.Completed.TrySetResult(false); return; }
            if (run.IsTerminal) { input.Completed.TrySetResult(true); return; }
            if (_boundAgentRun?.AgentRunId == run.AgentRunId && _boundAgentRun.Claim?.Generation == run.Claim?.Generation
                && (_activeResponseId is not null || _brainEvaluationCts is not null))
            { input.Completed.TrySetResult(true); return; }
            if (_boundAgentRun is { Status: AgentRunStatus.WaitingForSignal } waiting && waiting.AgentRunId == run.AgentRunId
                && run.Status == AgentRunStatus.Running && run.Claim is not null)
            {
                _responseCts?.Cancel();
                _responseCts?.Dispose();
                _responseCts = null;
                _activeResponseId = null;
            }
            if (_activeResponseId is not null || _agentRunAdmissionPending || run.Status != AgentRunStatus.Running
                || run.Claim is null || run.Claim.LeaseExpiresAtUtc <= _time.GetUtcNow())
            { input.Completed.TrySetResult(false); return; }
            if (_runAuthority is not null && await _runAuthority.CurrentDefinitionAsync(run, ct).ConfigureAwait(false) is null)
            {
                await _agentRuns.ApplyAsync(run.Owner, run.AgentRunId, new AgentRunCommand.Fail(run.Revision,
                    _time.GetUtcNow(), run.Claim!.Generation, "authority-unavailable", "The owner, source policy or model no longer permits execution.", false, null), ct).ConfigureAwait(false);
                input.Completed.TrySetResult(true);
                return;
            }
            if (input.Headless) _headlessTransportDetached = true;
            _boundAgentRun = run;
            var committedResponse = _snapshot.Entries.LastOrDefault(entry => entry.Role == ConversationRole.Assistant && entry.ResponseId == run.ResponseId);
            if (committedResponse is { Status: EntryStatus.Completed or EntryStatus.Interrupted or EntryStatus.Failed })
            {
                MarkAgentRunPendingTerminal();
                await FinalizeAgentRunAfterPersistAsync(_snapshot, ct).ConfigureAwait(false);
                input.Completed.TrySetResult(true);
                return;
            }
            await LaunchBoundAgentRunAsync(input.Context, run, ct).ConfigureAwait(false);
            input.Completed.TrySetResult(true);
        }
        catch (Exception exception) { input.Completed.TrySetException(exception); throw; }
    }

    public async Task<bool> HasOpenAgentRunsAsync(CancellationToken ct = default) =>
        (await _agentRuns.ListForSessionAsync(RunOwner, SessionId, ct).ConfigureAwait(false)).Any(run => !run.IsTerminal);

    public async Task RefreshDurableConversationProjectionAsync(CancellationToken ct = default)
    {
        var snapshot = await _store.LoadAsync(SessionId, ct).ConfigureAwait(false);
        if (snapshot is null) return;
        var refreshed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new DurableConversationProjectionRefreshReceived(NewContext(), snapshot, refreshed), urgent: true)) return;
        await refreshed.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleDurableConversationProjectionRefreshAsync(DurableConversationProjectionRefreshReceived input, CancellationToken ct)
    {
        try
        {
            // An attached runtime can already own model work. A refresh cannot reset that ownership.
            if (_boundAgentRun is null || _boundAgentRun.IsTerminal) ResetForTransportResume(input.Snapshot);
            await PublishAsync(new SessionOutput(input.Context, null, new ReadyOutput(await BuildReadyAsync(ct))), ct).ConfigureAwait(false);
        }
        finally { input.Refreshed.TrySetResult(); }
    }

    private async Task ReconcileAgentRunBeforeAttachAsync(CancellationToken ct)
    {
        var runs = await _agentRuns.ListForSessionAsync(RunOwner, SessionId, ct).ConfigureAwait(false);
        foreach (var id in runs.SelectMany(run => run.Admission.Activation.SourceEntryIds)) _admittedAgentInputs.TryAdd(id, 0);
        var active = runs.Where(run => !run.IsTerminal).OrderBy(run => run.CreatedAtUtc).ThenBy(run => run.AgentRunId).FirstOrDefault();
        if (_boundAgentRun is null && active is not null) _boundAgentRun = active;
        var durable = await _store.LoadAsync(SessionId, ct).ConfigureAwait(false);
        if (durable is not null && _activeResponseId is null && durable.Revision > _snapshot.Revision) ResetForTransportResume(durable);
    }

    private bool CanStartUserConversationBatch() => (_boundAgentRun is null || _boundAgentRun.IsTerminal) && !_deactivated && !_agentRunAdmissionPending && _activeResponseId is null
        && (_snapshot.Status is SessionStatus.Attached or SessionStatus.Created
            || _headlessTransportDetached && _snapshot.Status is not (SessionStatus.Ended or SessionStatus.Ending));

    private IReadOnlyList<ConversationEntry> PendingUserBatch() => _snapshot.Entries.Where(entry => _snapshot.PendingAgentInputIds.Contains(entry.EntryId)
            && !_admittedAgentInputs.ContainsKey(entry.EntryId) && entry.Role == ConversationRole.User
            && entry.Status == EntryStatus.Completed).OrderBy(entry => entry.Sequence).ToArray();

    private async Task<bool> TryStartPendingUserBatchAsync(EventContext cause, CancellationToken ct)
    {
        if (!CanStartUserConversationBatch()) return false;
        var users = PendingUserBatch();
        if (users.Count == 0 || users.Any(entry => _undurableUserEntryIds.Contains(entry.EntryId))) return false;
        var (projection, resolved) = await ResolveNewRunAsync(ct).ConfigureAwait(false);
        PinModelSelectionIfMissing();
        projection = projection with { ModelSelection = _snapshot.ModelSelection };
        ComposerRunInput? composerInput = null;
        if (users.Any(u => u.Parts is not null))
        {
            if (_composer is not null) composerInput = await _composer.PinAsync(RunOwner, users, projection.Definition, resolved.Skills, ct);
            else
            {
                var explicitKeys = users.SelectMany(u => UserMessageContent.ExplicitSkills(u.Parts)).Distinct(StringComparer.Ordinal).ToArray();
                string? error = null;
                try { AgentCore.Application.Composer.ComposerReferenceService.ActiveSkills(projection.Definition, resolved.Skills, explicitKeys); }
                catch (AgentCoreException exception) { error = exception.Message; }
                composerInput = new(explicitKeys, [], _time.GetUtcNow(), error);
            }
        }
        var run = AgentRunAdmissionFactory.ForAcceptedUserBatch(_ids.NewId(), _ids.NewId(), _ids.NewId(),
            projection, users, _time.GetUtcNow(), resolved.Skills,
            ExecutionBudgetResolver.Resolve(resolved.Configuration.Definition, resolved.InstanceBudgets, true), resolved.Configuration, composerInput);
        _agentRunAdmissionPending = true;
        var proposed = _snapshot with { PendingAgentInputIds = _snapshot.PendingAgentInputIds.Except(users.Select(entry => entry.EntryId)).ToArray() };
        RequestPersist(proposed, admittedRun: run, onAdmitted: committed => run = committed, then: async token =>
        {
            _agentRunAdmissionPending = false;
            _snapshot = _snapshot with { PendingAgentInputIds = _snapshot.PendingAgentInputIds.Except(users.Select(entry => entry.EntryId)).ToArray() };
            var admitted = await _agentRuns.GetAsync(run.Owner, run.AgentRunId, token).ConfigureAwait(false)
                ?? throw AgentCoreErrors.Persistence("Admitted AgentRun was not found.");
            if (admitted.Status != AgentRunStatus.Queued) return;
            var now = _time.GetUtcNow();
            try
            {
                _boundAgentRun = await _agentRuns.ApplyAsync(admitted.Owner, admitted.AgentRunId,
                    new AgentRunCommand.Claim(admitted.Revision, now, _ids.NewId(), now + AgentRunCoordinator.ClaimDuration), token).ConfigureAwait(false);
            }
            catch (AgentCoreException exception) when (exception.Code == "Conflict")
            {
                // The coordinator can claim after the durable admission commits. Its dispatch
                // is already queued for this mailbox; it owns launch and lease recovery.
                _boundAgentRun = await _agentRuns.GetAsync(admitted.Owner, admitted.AgentRunId, token).ConfigureAwait(false);
                return;
            }
            await LaunchBoundAgentRunAsync(cause, _boundAgentRun, token).ConfigureAwait(false);
        });
        return true;
    }

    private Task LaunchBoundAgentRunAsync(EventContext cause, AgentRun run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        cause = cause with { AgentRunGeneration = run.Claim!.Generation };
        var users = run.Admission.Activation.SourceEntryIds.Select(id => _snapshot.Entries.Single(entry => entry.EntryId == id)).ToArray();
        if (run.Admission.Activation.EvidenceJson is { } evidence && users.Length == 0)
        {
            var signal = JsonSerializer.Deserialize<AgentRunAdmissionFactory.SignalInput>(evidence)
                ?? throw AgentCoreErrors.Persistence("AgentRun signal evidence is missing.");
            _outputActivity = OutputActivity.WaitingForAgent;
            LaunchBrain(cause, new AgentTrigger(run.Admission.Activation.SourceEventId ?? run.Admission.Activation.TriggerOccurrenceId ?? run.ActivationId,
                signal.TriggerKind, signal.Text, signal.EnvironmentKind), run.ResponseId!.Value, ++_turnGeneration);
            return Task.CompletedTask;
        }
        return LaunchUserBatchAsync(cause, users, run.ResponseId ?? throw AgentCoreErrors.Persistence("AgentRun response identity is missing."));
    }

    private async Task LaunchUserBatchAsync(EventContext cause, IReadOnlyList<ConversationEntry> users, Guid responseId)
    {
        RetainProposalOnlyForConfirmingTurn(users.Select(entry => entry.Text).ToArray());
        var last = users[^1];
        var eventId = last.SourceEventId ?? last.EntryId;
        var context = new EventContext(eventId, SessionId, _epoch, _time.GetUtcNow(),
            cause.CorrelationId == Guid.Empty ? eventId : cause.CorrelationId, cause.EventId, cause.AgentRunGeneration);
        var attachments = users.SelectMany(entry => entry.Attachments ?? []).Select(item => item.AttachmentId).Distinct().ToArray();
        var kind = IsInitialBackgroundRun ? _boundAgentRun!.Admission.Activation.Kind switch
        { ActivationKind.ScheduledWork => TriggerKind.ScheduledOccurrence, ActivationKind.CoreEvent => TriggerKind.CoreEvent, ActivationKind.ApplicationEvent => TriggerKind.ApplicationEvent, _ => TriggerKind.ManualInvocation } : TriggerKind.UserTurn;
        var trigger = new AgentTrigger(eventId, kind, last.Text);
        var turn = ++_turnGeneration;
        _pendingUploadHold = false;
        NoteUserActivity();
        _environmentQueue.Clear();
        _outputActivity = OutputActivity.WaitingForAgent;
        UserTextQueueTelemetry.RecordPendingBatchStarted(users.Count);
        if (ShouldDeferUserTurnForCompaction()) AssignDeferredUserTurn(new DeferredUserTurn(context, trigger, responseId, turn, attachments));
        else await LaunchPreparedTurnAsync(context, trigger, responseId, turn, attachments).ConfigureAwait(false);
        await PublishWaitingOutputAsync(context).ConfigureAwait(false);
    }

    private async Task CancelUnstartedBoundAgentRunAsync(CancellationToken ct)
    {
        if (_activeResponseId is not null || _boundAgentRun is not { IsTerminal: false } bound
            || _snapshot.Entries.Any(entry => entry.Role == ConversationRole.Assistant && entry.ResponseId == bound.ResponseId)) return;
        var run = await _agentRuns.GetAsync(bound.Owner, bound.AgentRunId, ct).ConfigureAwait(false);
        if (run is null || run.IsTerminal) { _boundAgentRun = null; return; }
        if (run.Claim?.Generation != bound.Claim?.Generation) return;
        var now = _time.GetUtcNow();
        run = await _agentRuns.ApplyAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.RequestCancellation(run.Revision, now, null), ct).ConfigureAwait(false);
        if (run is { IsTerminal: false, Claim: { } claim })
            await _agentRuns.ApplyAsync(run.Owner, run.AgentRunId,
                new AgentRunCommand.CommitCancellation(run.Revision, now, claim.Generation, null), ct).ConfigureAwait(false);
        _boundAgentRun = null;
        ClearAgentRunTerminalPending();
    }

    private void MarkAgentRunPendingTerminal()
    {
        _pendingTerminalAgentRunId = _boundAgentRun?.AgentRunId;
        _pendingTerminalResponseId = _boundAgentRun?.ResponseId;
    }

    private async Task CloseBoundAgentRunIfAssistantTerminalAsync(CancellationToken ct)
    {
        if (_boundAgentRun is null) return;
        var assistant = _snapshot.Entries.LastOrDefault(entry => entry.Role == ConversationRole.Assistant && entry.ResponseId == _boundAgentRun.ResponseId);
        if (assistant is not { Status: EntryStatus.Interrupted or EntryStatus.Failed or EntryStatus.Completed }) return;
        MarkAgentRunPendingTerminal();
        await FinalizeAgentRunAfterPersistAsync(_snapshot, ct).ConfigureAwait(false);
    }

    private async Task FinalizeAgentRunAfterPersistAsync(SessionSnapshot saved, CancellationToken ct)
    {
        if (_pendingTerminalAgentRunId is not { } id) return;
        var run = await _agentRuns.GetAsync(RunOwner, id, ct).ConfigureAwait(false);
        if (run is null || run.IsTerminal) { _boundAgentRun = null; ClearAgentRunTerminalPending(); return; }
        var assistant = saved.Entries.LastOrDefault(entry => entry.Role == ConversationRole.Assistant && entry.ResponseId == run.ResponseId);
        if (assistant is not { Status: EntryStatus.Completed or EntryStatus.Failed or EntryStatus.Interrupted })
            throw AgentCoreErrors.Persistence("AgentRun terminal response is not durable.");
        var now = _time.GetUtcNow();
        if (run.Status == AgentRunStatus.WaitingForApproval
            || assistant.Status == EntryStatus.Interrupted && !run.CancellationRequested)
            run = await _agentRuns.ApplyAsync(run.Owner, id, new AgentRunCommand.RequestCancellation(run.Revision, now, null), ct).ConfigureAwait(false);
        if (!run.IsTerminal && run.Claim is { } claim)
        {
            AgentRunCommand command = assistant.Status switch
            {
                EntryStatus.Interrupted => new AgentRunCommand.CommitCancellation(run.Revision, now, claim.Generation, null),
                EntryStatus.Failed when assistant.Failure?.FailureReason == "runDeadline" => new AgentRunCommand.DeadlineExceeded(run.Revision, now, claim.Generation),
                EntryStatus.Failed => new AgentRunCommand.Fail(run.Revision, now, claim.Generation,
                    AgentRunFailureCode(_agentRunProviderFailure), AgentRunFailureSummary(_agentRunProviderFailure), false, null),
                _ => new AgentRunCommand.Complete(run.Revision, now, claim.Generation,
                    string.IsNullOrWhiteSpace(assistant.Text) ? "Response completed." : assistant.Text[..Math.Min(assistant.Text.Length, AgentRunLimits.MaxResultCharacters)],
                    AgentRunOutcomeKind.Response, assistant.EntryId)
            };
            await _agentRuns.ApplyAsync(run.Owner, id, command, ct).ConfigureAwait(false);
        }
        _boundAgentRun = null;
        ClearAgentRunTerminalPending();
    }

    private async Task<string> RunExperienceContextAsync(CancellationToken ct)
    {
        var selected = _boundAgentRun is { } run
            ? await _tools.SelectedExperienceContextAsync(_snapshot.AgentInstanceId, run.AgentRunId, ct).ConfigureAwait(false) : "";
        return string.IsNullOrWhiteSpace(selected)
            ? await _tools.ExperienceContextAsync(_snapshot.AgentInstanceId, ct).ConfigureAwait(false) : selected;
    }

    private static string AgentRunFailureCode(ProviderFailure? failure) => failure?.SafeMessage switch
    {
        "Run deadline reached." => "run-deadline",
        "Tool execution timed out." => "tool-timeout",
        "Tool step limit reached." => "tool-step-limit",
        "Repeated invalid tool strategy blocked." => "invalid-tool-strategy",
        "Tool output limit reached." => "tool-output-limit",
        "Tool deadline reached." => "tool-deadline",
        "Background work requires an explicit completion outcome." => "completion-required",
        _ => failure is null ? "response-failed" : "provider-" + failure.Code.ToString().ToLowerInvariant()
    };

    private static string AgentRunFailureSummary(ProviderFailure? failure) => AgentRunFailureCode(failure) switch
    {
        "run-deadline" => "The Run deadline expired before the reply completed. Recorded actions were preserved.",
        "tool-timeout" => "A tool timed out; an unconfirmed effect was not replayed.",
        "tool-step-limit" => "Tool step limit reached.",
        "invalid-tool-strategy" => "Repeated invalid tool strategy blocked.",
        "tool-output-limit" => "Tool output limit reached.",
        "tool-deadline" => "Tool deadline reached.",
        "completion-required" => "Background work requires an explicit completion outcome.",
        _ => "The model response failed."
    };

    private bool ScheduleAgentRunRetry(EventContext context, Guid responseId, ProviderFailure failure)
    {
        if (_boundAgentRun is not { Claim: { } claim } run
            || run.ResponseId != responseId || run.CancellationRequested || _publishedDisplayLength != 0
            || run.AttemptCount >= run.MaxAttempts
            || run.SideEffect.Disposition is AgentRunSideEffectDisposition.InFlight or AgentRunSideEffectDisposition.Indeterminate
            || !(failure.Code is ProviderErrorCode.Unavailable or ProviderErrorCode.RateLimited
                || failure.Code == ProviderErrorCode.Timeout && failure.FailureReason is ProviderFailureReason.SetupTimeout or ProviderFailureReason.StreamIdle
                || IsInitialBackgroundRun && AgentRunFailureCode(failure) == "completion-required")) return false;
        // Keep the same durable placeholder and checkpoint. A crash before releasing the claim
        // follows ordinary lease recovery; a later attempt reuses the response identity.
        UpdateAssistant(EntryStatus.Streaming);
        _responseTerminal = true;
        _responseLifecycle = ResponseLifecycle.Failed;
        var now = _time.GetUtcNow();
        var delay = TimeSpan.FromSeconds(Math.Clamp(failure.RetryAfter?.TotalSeconds ?? 2, 1, 300));
        RequestPersist(_snapshot, then: async ct =>
        {
            var current = await _agentRuns.GetAsync(run.Owner, run.AgentRunId, ct).ConfigureAwait(false);
            if (current is { Status: AgentRunStatus.Running, Claim: { } currentClaim }
                && currentClaim.Generation == claim.Generation)
                _boundAgentRun = await _agentRuns.ApplyAsync(current.Owner, current.AgentRunId,
                    new AgentRunCommand.Fail(current.Revision, now, claim.Generation,
                        AgentRunFailureCode(failure), AgentRunFailureSummary(failure), true, now + delay), ct).ConfigureAwait(false);
            if (_activeResponseId == responseId) ClearActive();
            await PublishOutputIdleAsync(context, ct).ConfigureAwait(false);
        });
        return true;
    }

    private sealed record AgentRunCommandReceived(EventContext Context, Guid ResponseId, Guid AgentRunId,
        Guid Generation, Func<AgentRun, DateTimeOffset, AgentRunCommand> Build,
        TaskCompletionSource<AgentRun?> Completed) : SessionInput(Context);

    private async Task<AgentRun?> RequestAgentRunCommandAsync(EventContext cause, Guid responseId,
        Func<AgentRun, DateTimeOffset, AgentRunCommand> build, CancellationToken ct)
    {
        var bound = _boundAgentRun ?? throw AgentCoreErrors.Conflict("AgentRun ownership is unavailable.");
        var generation = cause.AgentRunGeneration ?? bound.Claim?.Generation ?? bound.Approval?.ExecutionGeneration
            ?? throw AgentCoreErrors.Conflict("AgentRun generation is unavailable.");
        var completed = new TaskCompletionSource<AgentRun?>(TaskCreationOptions.RunContinuationsAsynchronously);
        BeginWork();
        if (!Enqueue(new AgentRunCommandReceived(WorkerContext(cause), responseId, bound.AgentRunId,
                generation, build, completed), urgent: true)) throw AgentCoreErrors.Conflict("Session mailbox is unavailable.");
        return await completed.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task HandleAgentRunCommandAsync(AgentRunCommandReceived input, CancellationToken ct)
    {
        try
        {
            if (_activeResponseId != input.ResponseId || input.Context.Epoch != _epoch
                || _boundAgentRun?.AgentRunId != input.AgentRunId || _responseTerminal)
                throw AgentCoreErrors.Conflict("AgentRun command is superseded.");
            var current = await _agentRuns.GetAsync(RunOwner, input.AgentRunId, ct).ConfigureAwait(false)
                ?? throw AgentCoreErrors.NotFound("AgentRun was not found.");
            if (current.IsTerminal || current.CancellationRequested ||
                (current.Claim?.Generation ?? current.Approval?.ExecutionGeneration) != input.Generation)
                throw AgentCoreErrors.Conflict("AgentRun command generation is stale.");
            _boundAgentRun = await _agentRuns.ApplyAsync(current.Owner, current.AgentRunId,
                input.Build(current, _time.GetUtcNow()), ct).ConfigureAwait(false);
            input.Completed.TrySetResult(_boundAgentRun);
        }
        catch (Exception exception) { input.Completed.TrySetException(exception); }
    }

    private async Task ResolveBoundApprovalAsync(AgentRunApprovalDecision? decision, CancellationToken ct, Action? accepted = null)
    {
        if (_boundAgentRun is not { Status: AgentRunStatus.WaitingForApproval, Approval: { } approval } run) return;
        var now = _time.GetUtcNow();
        AgentRunCommand command = decision is { } value
            ? new AgentRunCommand.DecideApproval(run.Revision, now, approval.ApprovalId, approval.Revision, approval.ActionHash, value)
            : new AgentRunCommand.ExpireApproval(run.Revision, now);
        run = await _agentRuns.ApplyAsync(run.Owner, run.AgentRunId, command, ct).ConfigureAwait(false);
        _boundAgentRun = run;
        accepted?.Invoke();
        _boundAgentRun = await _agentRuns.ApplyAsync(run.Owner, run.AgentRunId,
            new AgentRunCommand.Claim(run.Revision, now, _ids.NewId(), now + AgentRunCoordinator.ClaimDuration), ct).ConfigureAwait(false);
    }

    private void ClearAgentRunTerminalPending() { _pendingTerminalAgentRunId = null; _pendingTerminalResponseId = null; }
}
