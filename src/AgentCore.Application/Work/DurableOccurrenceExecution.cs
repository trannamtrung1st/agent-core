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

public sealed class DurableOccurrenceExecution(SessionToolExecutor tools, TimeProvider time)
{
    public async ValueTask<DurableOccurrenceOutcome> RunAsync(
        WorkItem running,
        ModelRequest request,
        ILanguageModel model,
        AgentDefinition definition,
        TriggerKind triggerKind,
        Func<WorkItem, WorkCheckpoint, CancellationToken, ValueTask<WorkItem>> checkpoint,
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
                steps++;
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
                        admission: admission).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    execution = ToolExecutionResult.FromText(
                        """{"error":"timeout","message":"Tool deadline reached."}""");
                }

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

                running = await checkpoint(
                    running,
                    new WorkCheckpoint(
                        DurableTurnCheckpoint.Write(messages),
                        steps,
                        outputBytes,
                        (int)remaining.TotalMilliseconds),
                    cancellationToken).ConfigureAwait(false);
                if (remaining <= TimeSpan.Zero)
                {
                    return new DurableOccurrenceFailed(running, "tool-budget", "Tool budget is exhausted.");
                }
            }
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
