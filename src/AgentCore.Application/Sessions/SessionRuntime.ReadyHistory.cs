using AgentCore.Application.Audio;
using AgentCore.Application.Events;
using AgentCore.Application.Ports;
using AgentCore.Application.Speech;
using AgentCore.Domain.Conversation;

namespace AgentCore.Application.Sessions;

public sealed partial class SessionRuntime
{
    private const int ReadyHistoryPageSize = 50;

    private async Task<(PublicHistoryEntry[] History, bool HasOlderHistory, long? HistoryBeforeSequence)> BuildReadyHistoryAsync(
        CancellationToken cancellationToken)
    {
        var page = await _store
            .ReadHistoryPageAsync(SessionId, null, null, ReadyHistoryPageSize, cancellationToken)
            .ConfigureAwait(false);

        Dictionary<Guid, ConversationEntry> merged;
        var durableHasOlder = false;
        if (page is null)
        {
            var window = _snapshot.Entries
                .OrderBy(entry => entry.Sequence)
                .TakeLast(ReadyHistoryPageSize)
                .ToArray();
            merged = window.ToDictionary(entry => entry.EntryId);
            durableHasOlder = _snapshot.Entries.Count > window.Length;
        }
        else
        {
            merged = page.Items.ToDictionary(entry => entry.EntryId);
            durableHasOlder = page.HasOlder;
        }

        foreach (var entry in _snapshot.Entries)
        {
            merged[entry.EntryId] = entry;
        }

        var ordered = merged.Values.OrderBy(entry => entry.Sequence).ToArray();
        var durableMin = page is { Items.Count: > 0 } ? page.Items[0].Sequence : (long?)null;
        if (ordered.Length > ReadyHistoryPageSize)
        {
            ordered = ordered[^ReadyHistoryPageSize..];
        }

        var hasOlderHistory = durableHasOlder
            || (durableMin is { } min && ordered.Length > 0 && ordered[0].Sequence > min)
            || (ordered.Length > 0 && ordered[0].Sequence > 1);
        long? historyBeforeSequence = hasOlderHistory && ordered.Length > 0 ? ordered[0].Sequence : null;
        var history = ordered.Select(ProjectHistoryEntry).ToArray();
        return (history, hasOlderHistory, historyBeforeSequence);
    }

    private async Task<SessionReadyProjection> BuildReadyAsync(CancellationToken cancellationToken)
    {
        var voice = _snapshot.Mode == SessionMode.Voice;
        var (history, hasOlderHistory, historyBeforeSequence) =
            await BuildReadyHistoryAsync(cancellationToken).ConfigureAwait(false);
        return new SessionReadyProjection(
            _snapshot.Mode,
            _snapshot.PendingMode,
            _snapshot.Status,
            PublicHistory.FromSnapshot(_snapshot, _voice.IsAvailable(_snapshot.Definition, SpeechLocale.Resolve(_snapshot).Effective)),
            voice ? _streamId : null,
            voice ? CanonicalAudio.Format : null,
            voice
                ? _recognition
                : new RecognitionCapabilities(false, false, false, false),
            voice
                ? _synthesizer?.Capabilities
                    ?? _voice.EffectivePlan.SynthesisCapabilities
                    ?? new SynthesisCapabilities(false, false, false, false, false, [])
                : new SynthesisCapabilities(false, false, false, false, false, []),
            voice ? _policy.BargeInPolicy : "none",
            _snapshot.DurableLastEntrySequence,
            history,
            _activeResponseId ?? (_boundAgentRun is { IsTerminal: false } restoredRun ? restoredRun.ResponseId : null),
            _voice.EffectivePlan.InputTransport,
            _voice.EffectivePlan.OutputTransport,
            _snapshot.LifecycleStatus,
            SpeechLocale.Resolve(_snapshot),
            _snapshot.ModelSelection,
            BuildPublicPendingApproval(),
            _boundAgentRun?.AgentRunId,
            _outputActivity.ToString(),
            hasOlderHistory,
            historyBeforeSequence);
    }
}
