using System.Text;
using System.Text.Json;
using AgentCore.Application.Ports;
using AgentCore.Application.Tools;
using AgentCore.Domain.Definitions;
using AgentCore.Domain.Work;

namespace AgentCore.Application.Work;

public abstract record DurableOccurrenceOutcome(WorkItem Running);

public sealed record DurableOccurrenceCompleted(WorkItem Running, string Text) : DurableOccurrenceOutcome(Running);

public sealed record DurableOccurrenceRetry(WorkItem Running, string Code, string Summary) : DurableOccurrenceOutcome(Running);

public sealed record DurableOccurrenceFailed(WorkItem Running, string Code, string Summary) : DurableOccurrenceOutcome(Running);

public sealed record DurableOccurrenceSuspended(WorkItem Running) : DurableOccurrenceOutcome(Running);

public sealed class DurableOccurrenceExecution(SessionToolExecutor tools, TimeProvider time)
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
        CancellationToken cancellationToken)
    {
        var resumed = DurableTurnCheckpoint.TryRead(running.Checkpoint, out var savedMessages);
        var messages = resumed ? savedMessages!.ToList() : request.Messages.ToList();
        var steps = resumed ? running.Checkpoint!.StepCount : 0;
        var outputBytes = resumed ? running.Checkpoint!.OutputBytes : 0;
        var remaining = resumed
            ? TimeSpan.FromMilliseconds(running.Checkpoint!.RemainingOverallBudgetMs)
            : ToolLimits.Overall;
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
        var admission = new ToolExecutionAdmission(Detached: true, triggerKind);
        if (UnansweredCall(messages) is ModelToolCall pendingCall)
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
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return new DurableOccurrenceFailed(running, "tool-budget", "Tool budget is exhausted.");
            }

            if (failed)
            {
                return new DurableOccurrenceRetry(running, "model-unavailable", "The model did not complete the occurrence.");
            }

            if (pending.Count == 0)
            {
                var result = text.ToString().Trim();
                if (!finished || result.Length == 0)
                {
                    return new DurableOccurrenceRetry(running, "empty-result", "Application event produced no result.");
                }

                return new DurableOccurrenceCompleted(running, result);
            }

            if (steps + pending.Count > ToolLimits.MaxSteps)
            {
                return new DurableOccurrenceFailed(running, "tool-step-limit", "Tool step limit reached.");
            }

            messages.Add(new ModelMessage(ModelRole.Assistant, string.Empty, ToolCalls: pending));
            foreach (var call in pending)
            {
                var callOutcome = await ExecuteCallAsync(call, countStep: true).ConfigureAwait(false);
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
                if (fencedToolCallId is null || !HasToolResult(messages, fencedToolCallId))
                {
                    return new DurableOccurrenceFailed(
                        running,
                        "tool-result-lost",
                        "External effect completed but the tool result was not durably recorded.");
                }

                running = await store.ClearSideEffectAsync(
                    running.WorkItemId,
                    running.Revision,
                    generation,
                    asOfUtc,
                    cancellationToken).ConfigureAwait(false);
            }

            if (!TryArguments(call, out var args))
            {
                return await AppendResultAsync(
                    call,
                    ToolExecutionResult.FromText("""{"error":"invalid","message":"Tool arguments must be a JSON object."}"""),
                    false)
                    .ConfigureAwait(false);
            }

            var policy = tools.EvaluateExecutionPolicy(definition, call.Name, admission: admission);
            var hash = await ResolveActionHashAsync(call, args, cancellationToken).ConfigureAwait(false);
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
            var dispatchFenced = needsApproval || ToolCatalog.ReplaySafetyOf(call.Name) != ToolReplaySafety.ReplaySafe;
            if (dispatchFenced)
            {
                if (running.SideEffect.Disposition is WorkSideEffectDisposition.InFlight or WorkSideEffectDisposition.Indeterminate)
                {
                    return new DurableOccurrenceFailed(
                        running,
                        "side-effect-indeterminate",
                        "External effect outcome is unknown and was not replayed.");
                }

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
                    Guid.Empty,
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

            messages.Add(new ModelMessage(ModelRole.Tool, execution.Text, ToolCallId: call.Id, Name: call.Name));
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
                    cancellationToken).ConfigureAwait(false);
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
                    DurableTurnCheckpoint.Write(messages),
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

    private static bool HasToolResult(IReadOnlyList<ModelMessage> messages, string toolCallId) =>
        messages.Any(message =>
            message.Role == ModelRole.Tool
            && string.Equals(message.ToolCallId, toolCallId, StringComparison.Ordinal));

    private static ModelToolCall? UnansweredCall(IReadOnlyList<ModelMessage> messages)
    {
        if (messages.Count == 0 || messages[^1].Role != ModelRole.Assistant)
        {
            return null;
        }

        return messages[^1].ToolCalls?.FirstOrDefault();
    }

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

internal static class DurableTurnCheckpoint
{
    private const string Phase = "model-turn";

    public static string Write(IReadOnlyList<ModelMessage> messages) =>
        JsonSerializer.Serialize(new Document(Phase, messages.Select(MessageDto.From).ToArray()));

    public static bool TryRead(WorkCheckpoint? checkpoint, out IReadOnlyList<ModelMessage>? messages)
    {
        messages = null;
        if (checkpoint is null || !checkpoint.PayloadJson.Contains(Phase, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            var document = JsonSerializer.Deserialize<Document>(checkpoint.PayloadJson);
            if (document is null || !string.Equals(document.Phase, Phase, StringComparison.Ordinal) || document.Messages is null)
            {
                return false;
            }

            messages = document.Messages.Select(message => message.ToMessage()).ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record Document(string Phase, MessageDto[] Messages);

    private sealed record MessageDto(
        string Role,
        string Text,
        string? ToolCallId,
        string? Name,
        ToolCallDto[]? ToolCalls)
    {
        public static MessageDto From(ModelMessage message) =>
            new(
                message.Role.ToString(),
                message.Text,
                message.ToolCallId,
                message.Name,
                message.ToolCalls?.Select(call => new ToolCallDto(call.Id, call.Name, call.ArgumentsJson)).ToArray());

        public ModelMessage ToMessage() =>
            new(
                Enum.Parse<ModelRole>(Role),
                Text,
                ToolCallId: ToolCallId,
                Name: Name,
                ToolCalls: ToolCalls?.Select(call => new ModelToolCall(call.Id, call.Name, call.ArgumentsJson)).ToArray());
    }

    private sealed record ToolCallDto(string Id, string Name, string ArgumentsJson);
}
