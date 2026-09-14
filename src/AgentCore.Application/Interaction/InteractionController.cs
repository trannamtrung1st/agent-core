using AgentCore.Application.Ports;

namespace AgentCore.Application.Interaction;

public sealed record InterruptionCandidate(
    Guid CandidateId,
    Guid UtteranceId,
    Guid ResponseId,
    int Revision,
    DateTimeOffset StartedAt,
    bool ClassifierRequested);

public sealed record ControllerSnapshot(
    Domain.Conversation.SessionStatus Lifecycle,
    Domain.Conversation.SessionMode Mode,
    InputActivity Input,
    OutputActivity Output,
    InterruptionCandidate? Candidate,
    Guid? LiveResponseId,
    ResponseLifecycle? ResponseStatus,
    int TimerGeneration,
    int TurnGeneration,
    RecognitionCapabilities Recognition,
    InteractionPolicy Policy,
    Guid? CommittedUtteranceId,
    double? LastActivityScore,
    DateTimeOffset Clock);

public sealed record ControllerEvaluation(
    InteractionDecision Decision,
    InputActivity Input,
    InterruptionCandidate? Candidate,
    bool CommitUserTurn,
    string? UserText,
    bool DiscardUtterance,
    InterruptionContext? ClassifierContext);

public static class InteractionController
{
    public static ControllerEvaluation EvaluateSpeech(
        ControllerSnapshot state,
        SpeechRecognitionEvent evidence,
        double? activityScore,
        TimeSpan duration,
        Guid newCandidateId)
    {
        return evidence switch
        {
            SpeechStarted started => OnStarted(state, started, activityScore, duration, newCandidateId),
            SpeechPartial partial => OnPartial(state, partial, activityScore, duration, newCandidateId),
            SpeechFinal final => OnFinal(state, final, activityScore, duration),
            SpeechEnded ended => OnEnded(state, ended),
            RecognitionFailed => new ControllerEvaluation(
                InteractionDecision.Ignore,
                state.Input,
                state.Candidate,
                false,
                null,
                true,
                null),
            _ => new ControllerEvaluation(InteractionDecision.Ignore, state.Input, state.Candidate, false, null, false, null)
        };
    }

    public static bool ClassifierMatches(
        InterruptionCandidate candidate,
        Guid candidateId,
        Guid utteranceId,
        Guid responseId,
        int revision) =>
        candidate.CandidateId == candidateId
        && candidate.UtteranceId == utteranceId
        && candidate.ResponseId == responseId
        && candidate.Revision == revision;

    public static bool TimerMatches(int currentGeneration, int firedGeneration) =>
        currentGeneration == firedGeneration;

    public static ControllerEvaluation EvaluateCandidateTimer(ControllerSnapshot state, TimeSpan duration)
    {
        if (!HasLiveOutput(state) || state.Candidate is null)
        {
            return new ControllerEvaluation(InteractionDecision.Ignore, state.Input, state.Candidate, false, null, false, null);
        }

        var policy = EffectivePolicy(state);
        if (policy is "speechAndFinal" or "speechActivity"
            && duration.TotalMilliseconds >= state.Policy.DegradedInterruptMs)
        {
            return new ControllerEvaluation(
                InteractionDecision.Interrupt,
                InputActivity.UserSpeaking,
                state.Candidate,
                false,
                null,
                false,
                null);
        }

        return new ControllerEvaluation(InteractionDecision.Queue, state.Input, state.Candidate, false, null, false, null);
    }

    private static ControllerEvaluation OnStarted(
        ControllerSnapshot state,
        SpeechStarted started,
        double? activityScore,
        TimeSpan duration,
        Guid newCandidateId)
    {
        var noisy = activityScore is { } score && score < state.Policy.ActivityThreshold
                    || duration.TotalMilliseconds > 0 && duration.TotalMilliseconds < state.Policy.MinSpeechMs;
        if (noisy && !HasLiveOutput(state))
        {
            return new ControllerEvaluation(InteractionDecision.Ignore, state.Input, null, false, null, true, null);
        }

        if (noisy && HasLiveOutput(state))
        {
            return new ControllerEvaluation(InteractionDecision.Ignore, state.Input, null, false, null, false, null);
        }

        var candidate = HasLiveOutput(state)
            ? new InterruptionCandidate(
                newCandidateId,
                started.UtteranceId,
                state.LiveResponseId!.Value,
                Revision: 0,
                state.Clock,
                ClassifierRequested: false)
            : null;
        return new ControllerEvaluation(
            HasLiveOutput(state) ? InteractionDecision.Queue : InteractionDecision.InjectEvent,
            InputActivity.UserSpeaking,
            candidate,
            false,
            null,
            false,
            null);
    }

    private static ControllerEvaluation OnPartial(
        ControllerSnapshot state,
        SpeechPartial partial,
        double? activityScore,
        TimeSpan duration,
        Guid newCandidateId)
    {
        var candidate = AlignCandidate(state, partial.UtteranceId, partial.Revision, newCandidateId);
        if (HeuristicPhrases.IsExplicitInterrupt(partial.Text) && HasLiveOutput(state))
        {
            return new ControllerEvaluation(
                InteractionDecision.Interrupt,
                InputActivity.UserSpeaking,
                candidate,
                false,
                null,
                false,
                null);
        }

        if (HeuristicPhrases.IsBackchannel(partial.Text) && HasLiveOutput(state)
            && duration.TotalMilliseconds <= state.Policy.BackchannelMaxMs)
        {
            return new ControllerEvaluation(
                InteractionDecision.Continue,
                InputActivity.UserSpeaking,
                candidate,
                false,
                null,
                false,
                null);
        }

        var policy = EffectivePolicy(state);
        if (policy is "speechAndFinal" or "speechActivity"
            && HasLiveOutput(state)
            && duration.TotalMilliseconds >= state.Policy.DegradedInterruptMs)
        {
            return new ControllerEvaluation(
                InteractionDecision.Interrupt,
                InputActivity.UserSpeaking,
                candidate,
                false,
                null,
                false,
                null);
        }

        if (HasLiveOutput(state)
            && duration.TotalMilliseconds >= state.Policy.SustainedInterruptMs
            && !HeuristicPhrases.IsBackchannel(partial.Text))
        {
            return new ControllerEvaluation(
                InteractionDecision.Interrupt,
                InputActivity.UserSpeaking,
                candidate,
                false,
                null,
                false,
                null);
        }

        if (HasLiveOutput(state)
            && policy == "semantic"
            && state.Recognition.PartialTranscripts
            && duration.TotalMilliseconds >= state.Policy.ClassifierAfterMs
            && candidate is { ClassifierRequested: false }
            && !HeuristicPhrases.IsBackchannel(partial.Text)
            && !HeuristicPhrases.IsExplicitInterrupt(partial.Text))
        {
            var context = new InterruptionContext(
                candidate.ResponseId,
                candidate.UtteranceId,
                partial.Text,
                HeardText: string.Empty,
                duration,
                activityScore,
                partial.Confidence,
                state.Recognition.PartialTranscripts);
            var requested = candidate with { ClassifierRequested = true, Revision = partial.Revision };
            return new ControllerEvaluation(
                InteractionDecision.RequestInterruptionClassification,
                InputActivity.UserSpeaking,
                requested,
                false,
                null,
                false,
                context);
        }

        return new ControllerEvaluation(
            InteractionDecision.Queue,
            InputActivity.UserSpeaking,
            candidate,
            false,
            null,
            false,
            null);
    }

    private static ControllerEvaluation OnFinal(
        ControllerSnapshot state,
        SpeechFinal final,
        double? activityScore,
        TimeSpan duration)
    {
        if (state.CommittedUtteranceId == final.UtteranceId)
        {
            return new ControllerEvaluation(InteractionDecision.Ignore, state.Input, state.Candidate, false, null, false, null);
        }

        if (string.IsNullOrWhiteSpace(final.Text))
        {
            return new ControllerEvaluation(
                InteractionDecision.Continue,
                state.Mode == Domain.Conversation.SessionMode.Voice ? InputActivity.Listening : InputActivity.Idle,
                null,
                false,
                null,
                true,
                null);
        }

        if (HasLiveOutput(state)
            && HeuristicPhrases.IsBackchannel(final.Text)
            && duration.TotalMilliseconds <= state.Policy.BackchannelMaxMs)
        {
            return new ControllerEvaluation(
                InteractionDecision.Continue,
                InputActivity.Listening,
                null,
                false,
                null,
                true,
                null);
        }

        if (HasLiveOutput(state))
        {
            return new ControllerEvaluation(
                InteractionDecision.Interrupt,
                InputActivity.Finalizing,
                state.Candidate,
                true,
                final.Text,
                false,
                null);
        }

        return new ControllerEvaluation(
            InteractionDecision.InjectEvent,
            InputActivity.Listening,
            null,
            true,
            final.Text,
            false,
            null);
    }

    private static ControllerEvaluation OnEnded(ControllerSnapshot state, SpeechEnded ended)
    {
        if (state.CommittedUtteranceId == ended.UtteranceId)
        {
            return new ControllerEvaluation(
                InteractionDecision.Ignore,
                state.Mode == Domain.Conversation.SessionMode.Voice ? InputActivity.Listening : InputActivity.Idle,
                null,
                false,
                null,
                false,
                null);
        }

        return new ControllerEvaluation(
            InteractionDecision.Queue,
            InputActivity.Finalizing,
            state.Candidate,
            false,
            null,
            false,
            null);
    }

    private static string EffectivePolicy(ControllerSnapshot state)
    {
        if (state.Policy.BargeInPolicy == "speechActivity")
        {
            return "speechActivity";
        }

        if (!state.Recognition.PartialTranscripts)
        {
            return "speechAndFinal";
        }

        return state.Policy.BargeInPolicy;
    }

    private static bool HasLiveOutput(ControllerSnapshot state) =>
        state.LiveResponseId is not null && state.ResponseStatus is ResponseLifecycle.Live;

    private static InterruptionCandidate? AlignCandidate(
        ControllerSnapshot state,
        Guid utteranceId,
        int revision,
        Guid newCandidateId)
    {
        if (state.Candidate is { } candidate && candidate.UtteranceId == utteranceId)
        {
            return candidate with { Revision = revision };
        }

        if (!HasLiveOutput(state))
        {
            return state.Candidate;
        }

        return new InterruptionCandidate(
            newCandidateId,
            utteranceId,
            state.LiveResponseId!.Value,
            revision,
            state.Clock,
            ClassifierRequested: false);
    }
}

public sealed class HeuristicInterruptionClassifier : IInterruptionClassifier
{
    public ValueTask<InteractionDecision> ClassifyAsync(
        InterruptionContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (HeuristicPhrases.IsExplicitInterrupt(context.PartialText))
        {
            return ValueTask.FromResult(InteractionDecision.Interrupt);
        }

        if (HeuristicPhrases.IsBackchannel(context.PartialText)
            && context.SpeechDuration.TotalMilliseconds <= 700)
        {
            return ValueTask.FromResult(InteractionDecision.Continue);
        }

        return ValueTask.FromResult(InteractionDecision.Continue);
    }
}
