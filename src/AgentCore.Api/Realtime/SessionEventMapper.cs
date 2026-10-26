using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Channels;
using AgentCore.Api.Mapping;
using AgentCore.Application.Events;
using AgentCore.Application.Models;
using AgentCore.Application.Observability;
using AgentCore.Application.Ports;
using AgentCore.Application.Sessions;
using AgentCore.Application.Triggers;
using AgentCore.Domain.Conversation;
using AgentCore.Domain.Triggers;
using AgentCore.Application.Speech;
using AgentCore.Contracts.Realtime;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AgentCore.Api.Realtime;

public static class SessionEventMapper
{
    public static ServerEvent Map(
        SessionOutput output,
        Guid attachmentId,
        long sequence,
        IModelCatalog? catalog = null)
    {
        var (type, payload) = output.Payload switch
        {
            ReadyOutput ready => ("session.ready", Ready(ready.Ready, catalog)),
            ResponseStartedOutput started => ("agent.response.started", new Dictionary<string, object?>
            {
                ["entryId"] = started.EntryId.ToString(),
                ["entrySequence"] = started.EntrySequence,
                ["trigger"] = ToTrigger(started.Trigger),
                ["agentRunId"] = started.AgentRunId?.ToString()
            }),
            TextDeltaOutput delta => ("agent.text.delta", new Dictionary<string, object?>
            {
                ["text"] = delta.Text,
                ["textStart"] = delta.TextStart
            }),
            SpeechProjectionOutput projection => ("agent.speech.projection", new Dictionary<string, object?>
            {
                ["mode"] = ToWireSpeechMode(projection.Mode),
                ["text"] = projection.Text
            }),
            TextCompletedOutput completed => ("agent.text.completed", new Dictionary<string, object?>
            {
                ["textLength"] = completed.TextLength
            }),
            DurableApprovalPendingOutput pending => ("agent.approval.pending", new Dictionary<string, object?>
            {
                ["requestId"] = pending.RequestId.ToString("D"), ["runId"] = pending.RunId.ToString("D")
            }),
            BlockUpsertOutput block => ("agent.block.upsert", new Dictionary<string, object?>
            {
                ["blockId"] = block.BlockId,
                ["kind"] = block.Kind,
                ["text"] = block.Text,
                ["fallbackText"] = block.FallbackText,
                ["attachmentId"] = block.AttachmentId,
                ["artifactId"] = block.ArtifactId
            }),
            ResponseCompletedOutput terminal when terminal.InterruptReason is { } reason =>
                ("agent.response.interrupted", new Dictionary<string, object?>
                {
                    ["reason"] = reason,
                    ["heardTextEndExclusive"] = terminal.HeardTextEndExclusive,
                    ["speechText"] = terminal.SpeechText,
                    ["completedAt"] = terminal.CompletedAt is { } completedAt ? HttpMapping.Format(completedAt) : null
                }),
            ResponseCompletedOutput terminal => ("agent.response.completed", new Dictionary<string, object?>
            {
                ["status"] = terminal.Failed ? "failed" : "completed",
                ["heardTextEndExclusive"] = terminal.HeardTextEndExclusive,
                ["finishReason"] = terminal.FinishReason,
                ["speechText"] = terminal.SpeechText,
                ["memoryReceipts"] = MemoryReceiptPayload(terminal.MemoryReceipts),
                ["effectReceipts"] = EffectReceiptPayload(terminal.EffectReceipts),
                ["completedAt"] = terminal.CompletedAt is { } completedAt ? HttpMapping.Format(completedAt) : null
            }),
            PlaybackStopOutput stop => ("playback.stop", new Dictionary<string, object?>
            {
                ["reason"] = stop.Reason
            }),
            PlaybackGainOutput gain => ("playback.gain", new Dictionary<string, object?>
            {
                ["gain"] = gain.Gain,
                ["rampMs"] = gain.RampMs,
                ["candidateId"] = gain.CandidateId?.ToString()
            }),
            StateChangedOutput state => ("session.state.changed", new Dictionary<string, object?>
            {
                ["status"] = HttpMapping.ToStatus(state.Status),
                ["lifecycleStatus"] = LifecycleTransition.ToWire(state.LifecycleStatus),
                ["lifecycleReason"] = state.LifecycleReason,
                ["lifecycleSource"] = state.LifecycleSource?.ToString().ToLowerInvariant(),
                ["retentionDueAtUtc"] = state.RetentionDueAtUtc?.ToString("O"),
                ["mode"] = HttpMapping.ToMode(state.Mode),
                ["pendingMode"] = state.PendingMode is { } pending ? HttpMapping.ToMode(pending) : null,
                ["inputState"] = ToInput(state.InputState),
                ["outputState"] = ToOutput(state.OutputState),
                ["muted"] = state.Muted,
                ["streamId"] = state.StreamId?.ToString(),
                ["pauseReason"] = state.PauseReason
            }),
            TranscriptPartialOutput partial => ("transcript.partial", new Dictionary<string, object?>
            {
                ["utteranceId"] = partial.UtteranceId.ToString(),
                ["revision"] = partial.Revision,
                ["text"] = partial.Text
            }),
            TranscriptFinalOutput final => ("transcript.final", new Dictionary<string, object?>
            {
                ["utteranceId"] = final.UtteranceId.ToString(),
                ["text"] = final.Text,
                ["entryId"] = final.EntryId.ToString(),
                ["entrySequence"] = final.EntrySequence
            }),
            TranscriptDiscardedOutput discarded => ("transcript.discarded", new Dictionary<string, object?>
            {
                ["utteranceId"] = discarded.UtteranceId.ToString()
            }),
            CompletionIntentOutput intent => ("session.completion.intent", new Dictionary<string, object?>
            {
                ["reason"] = intent.Reason,
                ["advisory"] = intent.Advisory
            }),
            HistoryEntryRemovedOutput removed => ("session.entry.removed", new Dictionary<string, object?> { ["entryId"] = removed.EntryId.ToString() }),
            HistoryEntryUpsertOutput entry => ("session.entry.upsert", HistoryItem(entry.Entry)),
            ResponseProgressOutput progress => ("agent.progress", new Dictionary<string, object?>
            {
                ["operationId"] = progress.OperationId?.ToString(),
                ["kind"] = ToProgressKind(progress.Kind),
                ["state"] = ToProgressState(progress.State),
                ["message"] = progress.Message
            }),
            ErrorOutput error => ("error", new Dictionary<string, object?>
            {
                ["category"] = error.Category,
                ["code"] = error.Code,
                ["message"] = error.SafeMessage,
                ["fatal"] = error.Fatal,
                ["retryAfterMs"] = error.RetryAfter is { } retry ? (int)retry.TotalMilliseconds : null,
                ["diagnosticId"] = error.DiagnosticId?.ToString("D"),
                ["failureReason"] = error.FailureReason,
                ["providerResponseChannel"] = error.ProviderResponseChannel
            }),
            SpeechOutputSegmentOutput segment => ("speech.output.segment", new Dictionary<string, object?>
            {
                ["segmentIndex"] = segment.SegmentIndex,
                ["textStart"] = segment.TextStart,
                ["text"] = segment.Text,
                ["voiceHint"] = segment.VoiceHint,
                ["language"] = segment.Language,
                ["speakingRate"] = segment.SpeakingRate
            }),
            SpeechOutputCompletedOutput speechCompleted => ("speech.output.completed", new Dictionary<string, object?>
            {
                ["textEndExclusive"] = speechCompleted.TextEndExclusive
            }),
            _ => ("error", new Dictionary<string, object?>
            {
                ["category"] = "Session",
                ["code"] = "Unknown",
                ["message"] = "Unsupported event.",
                ["fatal"] = false,
                ["retryAfterMs"] = null
            })
        };

        return new ServerEvent
        {
            ProtocolVersion = 1,
            SessionId = output.Context.SessionId.ToString(),
            AttachmentId = attachmentId.ToString(),
            EventId = output.Context.EventId.ToString(),
            Sequence = sequence,
            Timestamp = output.Context.Timestamp.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            CorrelationId = output.Context.CorrelationId.ToString(),
            CausationId = output.Context.CausationId?.ToString(),
            ResponseId = output.ResponseId?.ToString(),
            Type = type,
            Payload = payload
        };
    }

    private static object[]? EffectReceiptPayload(IReadOnlyList<PublicEffectReceipt>? receipts) =>
        receipts is not { Count: > 0 }
            ? null
            : receipts.Select(item => (object)new Dictionary<string, object?>
            {
                ["tool"] = item.Tool,
                ["status"] = item.Status,
                ["label"] = item.Label
            }).ToArray();

    private static object[]? MemoryReceiptPayload(IReadOnlyList<PublicMemoryReceipt>? receipts) =>
        receipts is not { Count: > 0 }
            ? null
            : receipts.Select(item => (object)new Dictionary<string, object?>
            {
                ["outcome"] = item.Outcome,
                ["operation"] = item.Operation,
                ["subject"] = item.Subject,
                ["scopes"] = item.Scopes,
                ["presentation"] = item.Presentation,
                ["label"] = item.Label
            }).ToArray();

    private static Dictionary<string, object?> Ready(SessionReadyProjection ready, IModelCatalog? catalog)
    {
        var history = ready.History.Select(entry => (object)HistoryItem(entry)).ToArray();
        return new Dictionary<string, object?>
        {
            ["mode"] = HttpMapping.ToMode(ready.Mode),
            ["pendingMode"] = ready.PendingMode is { } pending ? HttpMapping.ToMode(pending) : null,
            ["status"] = HttpMapping.ToStatus(ready.Status),
            ["outputState"] = ToOutput(ready.OutputActivity),
            ["agentRunId"] = ready.AgentRunId?.ToString(),
            ["lifecycleStatus"] = LifecycleTransition.ToWire(ready.LifecycleStatus),
            ["retentionKind"] = ready.RetentionKind.ToString().ToLowerInvariant(),
            ["retentionDueAtUtc"] = ready.RetentionDueAtUtc?.ToString("O"),
            ["agentCompletion"] = ready.AgentCompletion.ToString().ToLowerInvariant(),
            ["lifecycleReason"] = ready.LifecycleReason,
            ["lifecycleSource"] = ready.LifecycleSource?.ToString().ToLowerInvariant(),
            ["agent"] = new Dictionary<string, object?>
            {
                ["id"] = ready.Agent.Id,
                ["version"] = ready.Agent.Version,
                ["name"] = ready.Agent.Name,
                ["role"] = ready.Agent.Role,
                ["description"] = ready.Agent.Description,
                ["voiceAvailable"] = ready.Agent.VoiceAvailable,
                ["language"] = ready.Agent.Language
            },
            ["streamId"] = ready.StreamId?.ToString(),
            ["audioFormat"] = ready.AudioFormat is { } format
                ? new Dictionary<string, object?>
                {
                    ["encoding"] = format.Encoding,
                    ["sampleRateHz"] = format.SampleRateHz,
                    ["channels"] = format.Channels,
                    ["frameDurationMs"] = 20
                }
                : null,
            ["capabilities"] = new Dictionary<string, object?>
            {
                ["stt"] = new Dictionary<string, object?>
                {
                    ["streamingAudio"] = ready.Recognition.StreamingAudio,
                    ["partialTranscripts"] = ready.Recognition.PartialTranscripts,
                    ["speechBoundaryEvents"] = ready.Recognition.SpeechBoundaryEvents,
                    ["cancellation"] = ready.Recognition.Cancellation,
                    ["transport"] = ready.InputTransport
                },
                ["tts"] = new Dictionary<string, object?>
                {
                    ["streamingAudio"] = ready.Synthesis.StreamingAudio,
                    ["timingMarks"] = ready.Synthesis.TimingMarks,
                    ["cancellation"] = ready.Synthesis.Cancellation,
                    ["voiceSelection"] = ready.Synthesis.VoiceSelection,
                    ["speakingRate"] = ready.Synthesis.SpeakingRate,
                    ["supportedFormats"] = Array.Empty<object>(),
                    ["transport"] = ready.OutputTransport
                },
                    ["bargeInPolicy"] = ready.BargeInPolicy,
                    ["speechLocale"] = ready.SpeechLocale is { } locale
                        ? new Dictionary<string, object?>
                        {
                            ["effective"] = locale.Effective,
                            ["source"] = SpeechLocale.ToWire(locale.Source),
                            ["override"] = locale.Override
                        }
                        : null,
                    ["modelPreference"] = ready.ModelPreference is { } preference ? new Dictionary<string, object?> {
                        ["mode"] = preference.Mode, ["offeringId"] = preference.OfferingId,
                        ["reasoning"] = preference.Reasoning is { } reasoning ? new Dictionary<string, object?> { ["mode"] = reasoning.Mode, ["effort"] = reasoning.Effort } : null } : null,
                    ["model"] = ready.ModelSelection is { } model
                        ? new Dictionary<string, object?>
                        {
                            ["offeringId"] = model.OfferingId,
                            ["displayName"] = model.Binding?.ModelLabel ?? catalog?.Get(model.OfferingId)?.DisplayName ?? model.OfferingId,
                            ["selectionSource"] = SessionModelBinder.ToWire(model.SelectionSource),
                            ["reasoningEffort"] = model.ReasoningEffort,
                            ["externalModelId"] = model.ExternalModelId,
                            ["connectionId"] = model.ConnectionId,
                            ["observedFromRun"] = model.Decision is not null,
                            ["connectionLabel"] = model.Binding?.ConnectionLabel
                        }
                        : null
                },
            ["lastEntrySequence"] = ready.LastEntrySequence,
            ["history"] = history,
            ["hasOlderHistory"] = ready.HasOlderHistory,
            ["historyBeforeSequence"] = ready.HistoryBeforeSequence,
            ["activeResponseId"] = ready.ActiveResponseId?.ToString()
        };
    }

    internal static Dictionary<string, object?> HistoryItem(PublicHistoryEntry entry) =>
        new()
        {
            ["entryId"] = entry.EntryId.ToString(),
            ["sequence"] = entry.Sequence,
            ["sourceEventId"] = entry.SourceEventId?.ToString(),
            ["role"] = HttpMapping.ToHistoryRole(entry.Role),
            ["text"] = entry.Text,
            ["parts"] = UserMessageMapping.ToWire(entry.Parts),
            ["responseId"] = entry.ResponseId?.ToString(),
            ["status"] = HttpMapping.ToEntryStatus(entry.Status),
            ["deliveryMode"] = HttpMapping.ToMode(entry.DeliveryMode),
            ["heardTextEndExclusive"] = entry.HeardTextEndExclusive,
            ["receivedTextEndExclusive"] = entry.ReceivedTextEndExclusive,
            ["createdAt"] = entry.CreatedAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture),
            ["completedAt"] = entry.CompletedAt is { } completedAt ? HttpMapping.Format(completedAt) : null,
            ["attachments"] = entry.Attachments?.Select(item => (object)new Dictionary<string, object?>
            {
                ["attachmentId"] = item.AttachmentId.ToString(),
                ["displayName"] = item.DisplayName,
                ["contentType"] = item.ContentType
            }).ToArray(),
            ["blocks"] = entry.Blocks.Select(block => (object)new Dictionary<string, object?>
            {
                ["blockId"] = block.BlockId,
                ["kind"] = block.Kind,
                ["text"] = block.Text,
                ["fallbackText"] = block.FallbackText,
                ["attachmentId"] = block.AttachmentId,
                ["artifactId"] = block.ArtifactId
            }).ToArray(),
            ["finishReason"] = entry.FinishReason,
            ["interruptReason"] = entry.InterruptReason,
            ["speechText"] = entry.SpeechText,
            ["memoryReceipts"] = MemoryReceiptPayload(entry.MemoryReceipts),
            ["effectReceipts"] = EffectReceiptPayload(entry.EffectReceipts),
            ["failure"] = entry.Failure is { } failure
                ? new Dictionary<string, object?>
                {
                    ["diagnosticId"] = failure.DiagnosticId.ToString("D"),
                    ["correlationId"] = failure.CorrelationId?.ToString("D"),
                    ["category"] = failure.Category,
                    ["code"] = failure.Code,
                    ["failureReason"] = failure.FailureReason,
                    ["providerResponseChannel"] = failure.ProviderResponseChannel,
                    ["protocolRepair"] = failure.ProtocolRepair,
                    ["protocolRepairOutcome"] = failure.ProtocolRepairOutcome
                }
                : null
        };

    private static string ToTrigger(string trigger) => trigger switch
    {
        "UserTurn" => "userTurn",
        "LongSilence" => "longSilence",
        "EnvironmentUpdate" => "environmentUpdate",
        "UnfinishedInteraction" => "unfinishedInteraction",
        "ScheduledOccurrence" => "scheduledOccurrence",
        "ApplicationEvent" => "applicationEvent",
        "CoreEvent" => "coreEvent",
        _ => "userTurn"
    };

    private static string ToWireSpeechMode(ResponseSpeechMode mode) => mode switch
    {
        ResponseSpeechMode.Custom => "custom",
        ResponseSpeechMode.None => "none",
        _ => "same"
    };

    private static string ToInput(string value) => value switch
    {
        nameof(InputActivity.Listening) => "listening",
        nameof(InputActivity.UserSpeaking) => "userSpeaking",
        nameof(InputActivity.Finalizing) => "finalizing",
        _ => "idle"
    };

    private static string ToOutput(string value) => value switch
    {
        nameof(OutputActivity.ProcessingAttachments) => "processingAttachments",
        nameof(OutputActivity.RunningTools) => "runningTools",
        nameof(OutputActivity.WaitingForAgent) => "waitingForAgent",
        nameof(OutputActivity.AgentGenerating) => "agentGenerating",
        nameof(OutputActivity.AgentSpeaking) => "agentSpeaking",
        nameof(OutputActivity.Interrupted) => "interrupted",
        _ => "idle"
    };

    private static string ToProgressKind(ResponseProgressKind kind) => kind switch
    {
        ResponseProgressKind.Preparing => "preparing",
        ResponseProgressKind.ReadingAttachments => "readingAttachments",
        ResponseProgressKind.RunningTool => "runningTool",
        ResponseProgressKind.WaitingExternal => "waitingExternal",
        ResponseProgressKind.Finalizing => "finalizing",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported progress kind.")
    };

    private static string ToProgressState(ResponseProgressState state) => state switch
    {
        ResponseProgressState.Started => "started",
        ResponseProgressState.Updated => "updated",
        ResponseProgressState.Completed => "completed",
        ResponseProgressState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported progress state.")
    };
}
