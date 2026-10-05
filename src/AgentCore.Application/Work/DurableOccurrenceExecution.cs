using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Observability;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Work;

public abstract record DurableOccurrenceOutcome(WorkItem Running);

public sealed record DurableOccurrenceCompleted(WorkItem Running, string Text, bool AttentionRequired = false)
    : DurableOccurrenceOutcome(Running);

public sealed record DurableOccurrenceRetry(WorkItem Running, string Code, string Summary, Exception? Error = null) : DurableOccurrenceOutcome(Running);

public sealed record DurableOccurrenceFailed(WorkItem Running, string Code, string Summary) : DurableOccurrenceOutcome(Running);

public sealed record DurableOccurrenceSuspended(WorkItem Running) : DurableOccurrenceOutcome(Running);

public sealed class DurableOccurrenceExecution(
    SessionToolExecutor tools,
    TimeProvider time,
    IWorkCaptureStore? captures = null)
{
    public async ValueTask<DurableOccurrenceOutcome> RunAsync(
        WorkItem running,
        ModelRequest request,
        ILanguageModel model,
        AgentDefinition definition,
        TriggerKind triggerKind,
        Func<WorkItem, WorkCheckpoint, CancellationToken, ValueTask<WorkItem>> checkpoint,
        IWorkItemStore store,
        Guid generation,
        DateTimeOffset asOfUtc,
        IIdGenerator ids,
        CancellationToken cancellationToken,
        bool trustedConnection = false)
    {
        var occurrenceBrowser = ToolResources.IsOccurrence(triggerKind);
        var browserScope = occurrenceBrowser
            ? await tools.OpenOccurrenceBrowserAsync(
                running.WorkItemId,
                running.Owner.AgentInstanceId,
                trustedConnection,
                cancellationToken).ConfigureAwait(false)
            : null;
        if (trustedConnection && occurrenceBrowser)
        {
            tools.AdoptOccurrenceBrowser(running.Owner.AgentInstanceId);
        }

        await using var heldBrowser = browserScope ?? (IAsyncDisposable)NoopScope.Instance;
        var budget = ToolExecutionBudget.Resolve(new ToolBudgetSignal(
            InteractiveBrowser: false,
            BoundApplicationBrowser: browserScope?.BoundApplicationBrowser == true));
        var resumed = DurableToolCallCheckpoint.TryReadState(
            running.Checkpoint,
            out var savedMessages,
            out var restoredObservation,
            out var restoredBlockedHash);
        var messages = resumed ? savedMessages!.ToList() : request.Messages.ToList();
        var steps = resumed ? running.Checkpoint!.StepCount : 0;
        var outputBytes = resumed ? running.Checkpoint!.OutputBytes : 0;
        var remaining = resumed
            ? TimeSpan.FromMilliseconds(running.Checkpoint!.RemainingOverallBudgetMs)
            : budget.Overall;
        if (remaining <= TimeSpan.Zero)
        {
            return new DurableOccurrenceFailed(running, "tool-budget", "Tool budget is exhausted.");
        }

        var deadline = time.GetUtcNow() + remaining;
        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var overallTimer = time.CreateTimer(
            static state => ((CancellationTokenSource)state!).Cancel(),
            overallCts,
            remaining,
            Timeout.InfiniteTimeSpan);
        var admission = new ToolExecutionAdmission(
            Detached: true,
            triggerKind,
            AgentInstanceId: running.Owner.AgentInstanceId,
            TrustedConnection: trustedConnection,
            SupportsVision: model.Capabilities.Vision,
            CaptureScope: running.WorkItemId.ToString("D"),
            WorkItemId: running.WorkItemId,
            Harness: triggerKind == TriggerKind.ThoughtActivation ? await tools.HarnessContextAsync(running.Owner.AgentInstanceId, cancellationToken) : null,
            SupportsTools: model.Capabilities.Tools);
        var observationRequired = restoredObservation;
        string? blockedActionHash = restoredBlockedHash;
        var browserUnavailable = false;
        if (resumed)
        {
            var normalizedSteps = DurableToolCallCheckpoint.NormalizeResumedStepCount(steps, messages);
            if (normalizedSteps > budget.MaxSteps)
            {
                return new DurableOccurrenceFailed(running, "tool-step-limit", "Tool step limit reached.");
            }

            if (normalizedSteps != steps)
            {
                steps = normalizedSteps;
                running = await SaveCheckpointAsync().ConfigureAwait(false);
            }
        }

        foreach (var pendingCall in DurableToolCallCheckpoint.PendingCalls(messages))
        {
            var pendingOutcome = await ExecuteCallAsync(pendingCall, countStep: false).ConfigureAwait(false);
            if (pendingOutcome is not null)
            {
                return pendingOutcome;
            }
        }

        while (true)
        {
            var pending = new List<ModelToolCall>();
            var text = new StringBuilder();
            var finished = false;
            var failed = false;
            messages = await WorkCaptureRehydration.ApplyAsync(messages, captures, overallCts.Token).ConfigureAwait(false);
            var working = request with { Messages = messages };
            try
            {
                await foreach (var update in model.GenerateAsync(working, overallCts.Token).ConfigureAwait(false))
                {
                    switch (update)
                    {
                        case ModelToolCallEvent tool:
                            pending.Add(tool.Call);
                            break;
                        case ModelCompleted completed when completed.Reason == ModelStopReason.ToolCalls:
                            break;
                        case ModelTextDelta delta:
                            text.Append(delta.Text);
                            break;
                        case ModelDisplayDelta display:
                            text.Append(display.Text);
                            break;
                        case ModelReasoningDelta:
                            break;
                        case ModelCompleted:
                            finished = true;
                            break;
                        case ModelFailed:
                            failed = true;
                            finished = true;
                            break;
                        default:
                            return new DurableOccurrenceFailed(
                                running,
                                "unexpected-model-event",
                                "Application event received an unsupported model event.");
                    }
                }
            }
            catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
            {
                var leasedApplicationEvent = triggerKind == TriggerKind.ApplicationEvent
                    && browserScope?.BoundApplicationBrowser == true;
                if ((triggerKind == TriggerKind.ScheduledOccurrence || leasedApplicationEvent)
                    && steps == 0
                    && pending.Count == 0)
                {
                    return new DurableOccurrenceRetry(
                        running,
                        "model-timeout",
                        "The model did not finish within the reminder budget.",
                        exception);
                }

                return new DurableOccurrenceFailed(running, "tool-budget", "Tool budget is exhausted.");
            }

            if (failed)
            {
                return new DurableOccurrenceRetry(running, "model-unavailable", "The model did not complete the occurrence.");
            }

            if (pending.Count == 0)
            {
                if (observationRequired
                    && running.SideEffect.Disposition is WorkSideEffectDisposition.InFlight
                        or WorkSideEffectDisposition.Indeterminate)
                {
                    return new DurableOccurrenceFailed(
                        running,
                        "observation-required",
                        "A browser change must be observed before the work item can run again.");
                }

                var result = text.ToString().Trim();
                if (!finished || result.Length == 0)
                {
                    return new DurableOccurrenceRetry(running, "empty-result", "The model returned no result.");
                }

                return new DurableOccurrenceRetry(
                    running,
                    "completion-required",
                    "Unattended work must finish by calling work.complete.");
            }

            if (steps + pending.Count > budget.MaxSteps)
            {
                return new DurableOccurrenceFailed(running, "tool-step-limit", "Tool step limit reached.");
            }

            messages.Add(new ModelMessage(ModelRole.Assistant, string.Empty, ToolCalls: pending));
            steps += pending.Count;
            running = await SaveCheckpointAsync().ConfigureAwait(false);
            foreach (var call in pending)
            {
                var callOutcome = await ExecuteCallAsync(call, countStep: false).ConfigureAwait(false);
                if (callOutcome is not null)
                {
                    return callOutcome;
                }
            }
        }

        async ValueTask<DurableOccurrenceOutcome?> ExecuteCallAsync(ModelToolCall call, bool countStep)
        {
            if (countStep)
            {
                steps++;
            }

            if (running.SideEffect.Disposition == WorkSideEffectDisposition.Succeeded)
            {
                var fencedToolCallId = running.SideEffect.ToolCallId;
                if (fencedToolCallId is not null && !HasToolResult(messages, fencedToolCallId))
                {
                    blockedActionHash ??= running.SideEffect.ActionHash;
                    observationRequired = true;
                    var fencedCall = string.Equals(call.Id, fencedToolCallId, StringComparison.Ordinal)
                        ? call
                        : new ModelToolCall(
                            fencedToolCallId,
                            ToolNameFor(messages, fencedToolCallId) ?? call.Name,
                            call.ArgumentsJson);
                    var reconciled = await AppendResultAsync(
                        fencedCall,
                        ToolExecutionResult.FromText(
                            """{"effect":"already_completed","replayed":false}"""),
                        true).ConfigureAwait(false);
                    if (reconciled is not null || string.Equals(call.Id, fencedToolCallId, StringComparison.Ordinal))
                    {
                        return reconciled;
                    }
                }
                else
                {
                    if (fencedToolCallId is null)
                    {
                        blockedActionHash ??= running.SideEffect.ActionHash;
                        observationRequired = true;
                    }

                    var fencedName = running.SideEffect.ToolCallId is string toolCallId
                        ? ToolNameFor(messages, toolCallId) ?? call.Name
                        : call.Name;
                    running = await store.ClearSideEffectAsync(
                        running.WorkItemId,
                        running.Revision,
                        generation,
                        asOfUtc,
                        cancellationToken,
                        ToolCatalog.RecordsOwnerVisibleEffect(fencedName)).ConfigureAwait(false);
                }
            }

            if (!TryArguments(call, out var args))
            {
                return await AppendResultAsync(
                    call,
                    ToolExecutionResult.FromText("""{"error":"invalid","message":"Tool arguments must be a JSON object."}"""),
                    false)
                    .ConfigureAwait(false);
            }

            // A persisted uncertain external effect remains terminal even if current policy denies the call.
            // Policy changes cannot erase the recovery fence or turn it into a replayable retry.
            if (running.SideEffect.Disposition is WorkSideEffectDisposition.InFlight or WorkSideEffectDisposition.Indeterminate
                && !IsBrowserActHash(messages, running.SideEffect.ActionHash))
                return new DurableOccurrenceFailed(running, "side-effect-indeterminate", "External effect outcome is unknown and was not replayed.");
            if (HarnessChatTools.IsHarness(call.Name))
                admission = admission with { Harness = await tools.HarnessContextAsync(running.Owner.AgentInstanceId, cancellationToken),
                    HarnessSources = messages.Where(m => m.Role == ModelRole.Tool).SelectMany(m =>
                        messages.SelectMany(a => a.ToolCalls ?? []).Where(c => c.Id == m.ToolCallId)
                            .SelectMany(c => HarnessChatTools.Sources(c, m.Text))).ToArray() };
            var policy = tools.EvaluateExecutionPolicy(definition, call.Name, admission: admission);
            if (policy == ToolPolicyDecision.Deny)
                return await AppendResultAsync(call, ToolExecutionResult.FromText(
                    ToolResources.IsSessionTool(call.Name) && !ToolCatalog.IsBrowserTool(call.Name) && !HarnessChatTools.IsHarness(call.Name)
                    ? """{"error":"forbidden","message":"Session context is required."}"""
                    : """{"error":"forbidden","message":"Tool is not permitted in this execution origin."}"""), false);
            var hash = await ResolveActionHashAsync(call, args, cancellationToken).ConfigureAwait(false);
            if (browserUnavailable && ToolCatalog.IsBrowserTool(call.Name))
            {
                return await AppendResultAsync(
                    call,
                    ToolExecutionResult.FromText(
                        """{"error":"provider_unavailable","message":"Browser is unavailable."}"""),
                    false).ConfigureAwait(false);
            }

            if (blockedActionHash is not null
                && string.Equals(hash, blockedActionHash, StringComparison.Ordinal)
                && (call.Name == ToolCatalog.BrowserAct
                    || ToolCatalog.ReplaySafetyOf(call.Name) != ToolReplaySafety.ReplaySafe))
            {
                return await AppendResultAsync(
                    call,
                    ToolExecutionResult.FromText(
                        """{"error":"not_replayed","message":"This browser change was not replayed. Observe the page first."}"""),
                    false).ConfigureAwait(false);
            }

            var uncertainBrowserAct = running.SideEffect.Disposition is WorkSideEffectDisposition.InFlight
                    or WorkSideEffectDisposition.Indeterminate
                && IsBrowserActHash(messages, running.SideEffect.ActionHash);
            if (uncertainBrowserAct)
            {
                observationRequired = true;
                blockedActionHash ??= running.SideEffect.ActionHash;
                if (call.Name is not (ToolCatalog.BrowserNavigate or ToolCatalog.BrowserObserve))
                {
                    return await AppendResultAsync(
                        call,
                        ToolExecutionResult.FromText(
                            """{"error":"observation_required","message":"Observe the page before another browser change."}"""),
                        false).ConfigureAwait(false);
                }
            }
            else if (running.SideEffect.Disposition is WorkSideEffectDisposition.InFlight
                or WorkSideEffectDisposition.Indeterminate)
            {
                return new DurableOccurrenceFailed(
                    running,
                    "side-effect-indeterminate",
                    "External effect outcome is unknown and was not replayed.");
            }

            if (string.Equals(call.Name, ToolCatalog.WorkComplete, StringComparison.Ordinal))
            {
                if (policy != ToolPolicyDecision.Allow)
                {
                    return await AppendResultAsync(
                        call,
                        ToolExecutionResult.FromText(
                            """{"error":"forbidden","message":"Completion is owned by the occurrence."}"""),
                        false).ConfigureAwait(false);
                }

                if (triggerKind == TriggerKind.ThoughtActivation)
                {
                    if (!ThoughtCompletion.TryParse(args, messages, out var thoughtResult, out var thoughtAttention, out var thoughtRejection))
                        return new DurableOccurrenceFailed(running, "invalid-completion", thoughtRejection);
                    RuntimeTelemetry.RecordThought(thoughtAttention ? "attention" : ThoughtCompletion.Outcome(thoughtResult));
                    return new DurableOccurrenceCompleted(running, thoughtResult, thoughtAttention);
                }
                if (!WorkCompletionRequest.TryParse(args, out var summary, out var attentionRequired, out var rejection))
                {
                    return new DurableOccurrenceFailed(running, "invalid-completion", rejection);
                }

                return new DurableOccurrenceCompleted(running, summary, attentionRequired);
            }

            if (policy == ToolPolicyDecision.RequireApproval && !ApprovedFor(running, call, hash))
            {
                if (running.Approval is { } decided
                    && string.Equals(decided.ToolName, call.Name, StringComparison.Ordinal)
                    && string.Equals(decided.ActionHash, hash, StringComparison.Ordinal)
                    && decided.Decision is WorkApprovalDecision.Rejected or WorkApprovalDecision.Expired)
                {
                    return await AppendResultAsync(
                        call,
                        ToolExecutionResult.FromText("""{"error":"rejected","message":"Action was not approved."}"""),
                        false)
                        .ConfigureAwait(false);
                }

                return await SuspendForApprovalAsync(call, args, hash).ConfigureAwait(false);
            }

            var needsApproval = policy == ToolPolicyDecision.RequireApproval;
            var dispatchFenced = !uncertainBrowserAct
                && (needsApproval || ToolCatalog.ReplaySafetyOf(call.Name) != ToolReplaySafety.ReplaySafe);
            if (dispatchFenced)
            {
                if (running.SideEffect.Disposition == WorkSideEffectDisposition.None)
                {
                    running = await store.MarkSideEffectAsync(
                        running.WorkItemId,
                        running.Revision,
                        generation,
                        WorkSideEffectDisposition.Prepared,
                        call.Id,
                        hash,
                        asOfUtc,
                        cancellationToken).ConfigureAwait(false);
                }

                running = await store.MarkSideEffectAsync(
                    running.WorkItemId,
                    running.Revision,
                    generation,
                    WorkSideEffectDisposition.InFlight,
                    call.Id,
                    hash,
                    asOfUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            ToolExecutionResult execution;
            try
            {
                using var toolCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
                using var toolTimer = time.CreateTimer(
                    static state => ((CancellationTokenSource)state!).Cancel(),
                    toolCts,
                    ToolLimits.PerTool,
                    Timeout.InfiniteTimeSpan);
                execution = await tools.ExecuteAsync(
                    definition,
                    running.WorkItemId,
                    call,
                    RemainingOutput(outputBytes),
                    toolCts.Token,
                    approvalGrant: needsApproval
                        ? new ToolApprovalGrant(running.Approval!.ApprovalId, call.Name, hash, Guid.Empty, Guid.Empty, Guid.Empty)
                        : null,
                    admission: admission).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                if (!dispatchFenced)
                {
                    execution = ToolExecutionResult.FromText(
                        """{"error":"timeout","message":"Tool deadline reached."}""");
                }
                else if (call.Name == ToolCatalog.BrowserAct)
                {
                    running = await store.MarkSideEffectAsync(
                        running.WorkItemId,
                        running.Revision,
                        generation,
                        WorkSideEffectDisposition.Indeterminate,
                        call.Id,
                        hash,
                        asOfUtc,
                        CancellationToken.None).ConfigureAwait(false);
                    observationRequired = true;
                    blockedActionHash = hash;
                    return await AppendResultAsync(
                        call,
                        ToolExecutionResult.FromText(
                            """{"error":"observation_required","message":"Observe the page before another browser change."}"""),
                        false).ConfigureAwait(false);
                }
                else
                {
                    running = await store.MarkSideEffectAsync(
                        running.WorkItemId,
                        running.Revision,
                        generation,
                        WorkSideEffectDisposition.Indeterminate,
                        call.Id,
                        hash,
                        asOfUtc,
                        CancellationToken.None).ConfigureAwait(false);
                    return new DurableOccurrenceFailed(
                        running,
                        "side-effect-indeterminate",
                        "External effect outcome is unknown and was not replayed.");
                }
            }

            if (execution.Text.Contains("user_intervention_required", StringComparison.Ordinal))
            {
                if (dispatchFenced)
                {
                    running = await store.MarkSideEffectAsync(
                        running.WorkItemId,
                        running.Revision,
                        generation,
                        WorkSideEffectDisposition.DefinitelyFailed,
                        call.Id,
                        hash,
                        asOfUtc,
                        CancellationToken.None).ConfigureAwait(false);
                }

                return new DurableOccurrenceFailed(
                    running,
                    "user_intervention_required",
                    "The page needs a person before this work can continue.");
            }

            if (ToolCatalog.IsBrowserTool(call.Name)
                && execution.Text.Contains("provider_unavailable", StringComparison.Ordinal))
            {
                browserUnavailable = true;
            }

            if (uncertainBrowserAct
                && call.Name is ToolCatalog.BrowserNavigate or ToolCatalog.BrowserObserve
                && !execution.Text.Contains("\"error\"", StringComparison.Ordinal))
            {
                observationRequired = false;
                running = await store.AcceptBrowserObservationAsync(
                    running.WorkItemId,
                    running.Revision,
                    generation,
                    asOfUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            if (dispatchFenced)
            {
                running = await store.MarkSideEffectAsync(
                    running.WorkItemId,
                    running.Revision,
                    generation,
                    WorkSideEffectDisposition.Succeeded,
                    call.Id,
                    hash,
                    asOfUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            return await AppendResultAsync(call, execution, dispatchFenced).ConfigureAwait(false);
        }

        async ValueTask<DurableOccurrenceOutcome?> SuspendForApprovalAsync(ModelToolCall call, JsonElement args, string hash)
        {
            var prepared = await ToolActionPreparation.PrepareApprovalAsync(tools, call, args, cancellationToken)
                .ConfigureAwait(false);
            if (prepared.Preparation is null)
            {
                return await AppendResultAsync(
                    call,
                    ToolExecutionResult.FromText(
                        prepared.ErrorJson ?? """{"error":"invalid","message":"Unable to prepare action approval."}"""),
                    false)
                    .ConfigureAwait(false);
            }

            var preview = prepared.Preparation.Preview;
            var actionHash = prepared.Preparation.ActionHash;
            var actionJson = prepared.Preparation.ActionJson;

            running = await SaveCheckpointAsync().ConfigureAwait(false);
            running = await store.MarkSideEffectAsync(
                running.WorkItemId,
                running.Revision,
                generation,
                WorkSideEffectDisposition.Prepared,
                call.Id,
                actionHash,
                asOfUtc,
                cancellationToken).ConfigureAwait(false);
            running = await store.BeginApprovalAsync(
                running.WorkItemId,
                running.Revision,
                generation,
                ids.NewId(),
                call.Name,
                actionJson,
                actionHash,
                preview,
                asOfUtc.Add(ToolApprovalLimits.Lifetime),
                asOfUtc,
                cancellationToken).ConfigureAwait(false);
            return new DurableOccurrenceSuspended(running);
        }

        async ValueTask<DurableOccurrenceOutcome?> AppendResultAsync(
            ModelToolCall call,
            ToolExecutionResult execution,
            bool dispatchFenced)
        {
            execution = ToolResultAdmission.AdmitForModel(model, execution);
            outputBytes += ToolOutputBudget.TextByteCount(execution);
            if (outputBytes > ToolLimits.MaxOutputBytes)
            {
                return new DurableOccurrenceFailed(running, "tool-output-limit", "Tool output limit reached.");
            }

            messages.Add(new ModelMessage(
                ModelRole.Tool,
                execution.Text,
                Parts: execution.Parts,
                ToolCallId: call.Id,
                Name: call.Name));
            remaining = deadline - time.GetUtcNow();
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            running = await SaveCheckpointAsync().ConfigureAwait(false);
            if (dispatchFenced && running.SideEffect.Disposition == WorkSideEffectDisposition.Succeeded)
            {
                running = await store.ClearSideEffectAsync(
                    running.WorkItemId,
                    running.Revision,
                    generation,
                    asOfUtc,
                    cancellationToken,
                    ToolCatalog.RecordsOwnerVisibleEffect(call.Name)).ConfigureAwait(false);
            }

            if (remaining <= TimeSpan.Zero)
            {
                return new DurableOccurrenceFailed(running, "tool-budget", "Tool budget is exhausted.");
            }

            return null;
        }

        ValueTask<WorkItem> SaveCheckpointAsync() =>
            checkpoint(
                running,
                new WorkCheckpoint(
                    DurableToolCallCheckpoint.Write(messages, observationRequired, blockedActionHash),
                    steps,
                    outputBytes,
                    (int)Math.Max(remaining.TotalMilliseconds, 0)),
                cancellationToken);
    }

    private async ValueTask<string> ResolveActionHashAsync(
        ModelToolCall call,
        JsonElement args,
        CancellationToken cancellationToken)
    {
        if (string.Equals(call.Name, ToolCatalog.EmailSend, StringComparison.Ordinal))
        {
            var prepared = await tools.PrepareEmailSendApprovalAsync(args, cancellationToken).ConfigureAwait(false);
            if (prepared.Preparation is not null)
            {
                return prepared.Preparation.ActionHash;
            }
        }

        return ToolActionPreparation.ActionHash(tools, call, args);
    }

    private static bool ApprovedFor(WorkItem item, ModelToolCall call, string hash) =>
        item.Approval is { Decision: WorkApprovalDecision.Approved } approval
        && string.Equals(approval.ToolName, call.Name, StringComparison.Ordinal)
        && string.Equals(approval.ActionHash, hash, StringComparison.Ordinal);

    private static bool IsBrowserActHash(IReadOnlyList<ModelMessage> messages, string? actionHash)
    {
        if (string.IsNullOrWhiteSpace(actionHash))
        {
            return false;
        }

        foreach (var message in messages)
        {
            if (message.ToolCalls is null)
            {
                continue;
            }

            foreach (var call in message.ToolCalls)
            {
                if (!string.Equals(call.Name, ToolCatalog.BrowserAct, StringComparison.Ordinal))
                {
                    continue;
                }

                try
                {
                    var args = JsonSerializer.Deserialize<JsonElement>(
                        string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
                    if (string.Equals(ToolActionHash.Compute(call.Name, args), actionHash, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
                catch (JsonException)
                {
                }
            }
        }

        return false;
    }

    private sealed class NoopScope : IAsyncDisposable
    {
        public static NoopScope Instance { get; } = new();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string? ToolNameFor(IReadOnlyList<ModelMessage> messages, string toolCallId)
    {
        foreach (var message in messages)
        {
            if (message.ToolCalls is null)
            {
                continue;
            }

            foreach (var call in message.ToolCalls)
            {
                if (string.Equals(call.Id, toolCallId, StringComparison.Ordinal))
                {
                    return call.Name;
                }
            }
        }

        return null;
    }

    private static bool HasToolResult(IReadOnlyList<ModelMessage> messages, string toolCallId) =>
        messages.Any(message =>
            message.Role == ModelRole.Tool
            && string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal));

    private static bool TryArguments(ModelToolCall call, out JsonElement args)
    {
        try
        {
            args = JsonSerializer.Deserialize<JsonElement>(
                string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson);
            return args.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            args = default;
            return false;
        }
    }

    private static int RemainingOutput(long outputBytes)
    {
        var remaining = ToolLimits.MaxOutputBytes - outputBytes;
        if (remaining <= 0)
        {
            return 0;
        }

        return remaining > int.MaxValue ? int.MaxValue : (int)remaining;
    }
}
