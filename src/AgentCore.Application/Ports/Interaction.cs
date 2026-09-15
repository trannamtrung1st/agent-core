namespace AgentCore.Application.Ports;

public enum InteractionDecision
{
    Ignore,
    Continue,
    Queue,
    Interrupt,
    InjectEvent,
    RequestInterruptionClassification,
    RequestAgentDecision
}

public sealed record InterruptionContext(
    Guid ResponseId,
    Guid UtteranceId,
    string PartialText,
    string HeardText,
    TimeSpan SpeechDuration,
    double? ActivityScore,
    double? TranscriptConfidence,
    bool HasPartialTranscripts);

public interface IInterruptionClassifier
{
    ValueTask<InteractionDecision> ClassifyAsync(
        InterruptionContext context,
        CancellationToken cancellationToken = default);
}

public abstract record SpeechRecognitionEvent(Guid UtteranceId);

public sealed record SpeechStarted(Guid UtteranceId) : SpeechRecognitionEvent(UtteranceId);

public sealed record SpeechPartial(Guid UtteranceId, int Revision, string Text, double? Confidence)
    : SpeechRecognitionEvent(UtteranceId);

public sealed record SpeechFinal(Guid UtteranceId, string Text, double? Confidence)
    : SpeechRecognitionEvent(UtteranceId);

public sealed record SpeechEnded(Guid UtteranceId) : SpeechRecognitionEvent(UtteranceId);

public sealed record RecognitionFailed(Guid UtteranceId, ProviderFailure Failure)
    : SpeechRecognitionEvent(UtteranceId);

public enum InputActivity { Idle, Listening, UserSpeaking, Finalizing }

public enum OutputActivity { Idle, WaitingForAgent, AgentGenerating, AgentSpeaking, Interrupted }

public enum ResponseLifecycle { Live, Superseded, Completed, Failed }

public sealed record InteractionPolicy(
    string BargeInPolicy = "semantic",
    double ActivityThreshold = 0.7,
    int MinSpeechMs = 120,
    int ClassifierAfterMs = 250,
    int SustainedInterruptMs = 500,
    int BackchannelMaxMs = 700,
    int DegradedInterruptMs = 250,
    int PendingVoiceTimeoutMs = 30_000);
