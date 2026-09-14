# Backend Interfaces

## Goal

Agent Core should be extensible by design without turning the MVP into an abstraction-heavy framework.

Use small generic interfaces around external capabilities. Keep domain concepts stable while allowing concrete providers to vary.

The first real provider can use an OpenAI-compatible API. Test and synthetic providers should implement the same contracts.

The examples below use TypeScript-like pseudocode. They describe contracts, not a required implementation language.

## Design rules

1. Core code must not depend on vendor SDK types.
2. Interfaces should represent Agent Core needs, not every feature offered by a provider.
3. Provider-specific capabilities may be exposed through optional capability flags or extension interfaces.
4. Streaming and cancellation are first-class.
5. Time, IDs, memory, and external I/O should be injectable for deterministic testing.
6. Synthetic implementations should be easy to write.
7. Prefer composition over one giant `AIProvider` interface.

## Language model port

```ts
export type Role = "system" | "user" | "assistant" | "tool";

export interface ModelMessage {
  role: Role;
  content: string;
  name?: string;
  metadata?: Record<string, unknown>;
}

export interface GenerationOptions {
  model?: string;
  temperature?: number;
  maxOutputTokens?: number;
  stop?: string[];
  metadata?: Record<string, unknown>;
}

export interface TextDelta {
  type: "text_delta";
  text: string;
}

export interface GenerationCompleted {
  type: "completed";
  finishReason?: string;
  usage?: {
    inputTokens?: number;
    outputTokens?: number;
  };
}

export interface GenerationError {
  type: "error";
  error: Error;
}

export type GenerationEvent =
  | TextDelta
  | GenerationCompleted
  | GenerationError;

export interface LanguageModel {
  readonly name: string;

  generate(
    messages: ModelMessage[],
    options: GenerationOptions,
    signal?: AbortSignal,
  ): AsyncIterable<GenerationEvent>;
}
```

This interface deliberately does not mention OpenAI request objects.

## Optional structured-output capability

Do not force all language-model providers to support identical features.

```ts
export interface StructuredOutputModel {
  generateObject<T>(request: {
    messages: ModelMessage[];
    schema: unknown;
    options?: GenerationOptions;
    signal?: AbortSignal;
  }): Promise<T>;
}
```

The runtime can use this capability when available and fall back to another strategy when it is not.

## Speech recognizer port

```ts
export interface AudioChunk {
  data: Uint8Array;
  format: "pcm16" | "opus" | "wav";
  sampleRateHz?: number;
  channels?: number;
  timestampMs?: number;
}

export type TranscriptEvent =
  | {
      type: "speech_started";
      timestampMs: number;
    }
  | {
      type: "partial_transcript";
      text: string;
      confidence?: number;
    }
  | {
      type: "final_transcript";
      text: string;
      confidence?: number;
    }
  | {
      type: "speech_ended";
      timestampMs: number;
    };

export interface SpeechRecognitionSession {
  push(chunk: AudioChunk): Promise<void>;
  events(): AsyncIterable<TranscriptEvent>;
  close(): Promise<void>;
  cancel(reason?: string): Promise<void>;
}

export interface SpeechRecognizer {
  createSession(options?: {
    language?: string;
    metadata?: Record<string, unknown>;
  }): Promise<SpeechRecognitionSession>;
}
```

## Speech synthesizer port

```ts
export type SpeechSynthesisEvent =
  | { type: "audio"; chunk: AudioChunk }
  | { type: "mark"; charIndex: number; timestampMs: number }
  | { type: "completed" }
  | { type: "error"; error: Error };

export interface SpeechRequest {
  text: string;
  voice?: string;
  speakingRate?: number;
  metadata?: Record<string, unknown>;
}

export interface SpeechSynthesizer {
  synthesize(
    request: SpeechRequest,
    signal?: AbortSignal,
  ): AsyncIterable<SpeechSynthesisEvent>;
}
```

`mark` events are useful for tracking approximately what portion of a generated response has actually been spoken before an interruption.

## Realtime transport port

The runtime should not care whether the client connection is WebSocket, WebRTC data channel, or another transport.

```ts
export interface ClientEvent {
  type: string;
  payload: unknown;
  timestampMs: number;
}

export interface ServerEvent {
  type: string;
  payload: unknown;
  timestampMs: number;
}

export interface RealtimeConnection {
  incoming(): AsyncIterable<ClientEvent>;
  send(event: ServerEvent): Promise<void>;
  close(reason?: string): Promise<void>;
}

export interface RealtimeTransport {
  accept(sessionId: string): Promise<RealtimeConnection>;
}
```

## Memory store port

```ts
export interface SessionSnapshot {
  sessionId: string;
  agentId: string;
  conversation: unknown;
  workingState: Record<string, unknown>;
  summary?: string;
  updatedAt: string;
}

export interface UserProfile {
  userId: string;
  data: Record<string, unknown>;
  updatedAt: string;
}

export interface MemoryStore {
  loadSession(sessionId: string): Promise<SessionSnapshot | null>;
  saveSession(snapshot: SessionSnapshot): Promise<void>;

  loadUserProfile(userId: string): Promise<UserProfile | null>;
  saveUserProfile(profile: UserProfile): Promise<void>;
}
```

## Clock and ID ports

These seem minor, but they make controller and timer behavior deterministic in tests.

```ts
export interface Clock {
  nowMs(): number;
  sleep(ms: number, signal?: AbortSignal): Promise<void>;
}

export interface IdGenerator {
  next(): string;
}
```

Synthetic tests can use a fake clock and deterministic IDs.

## Interaction classification port

Some interruption decisions should use heuristics; ambiguous cases may use a small model. Keep this replaceable.

```ts
export type InterruptionDecision =
  | "interrupt"
  | "continue"
  | "queue"
  | "ignore";

export interface InterruptionContext {
  transcript: string;
  isAgentSpeaking: boolean;
  agentTextSpoken?: string;
  speechDurationMs?: number;
  recentEvents: unknown[];
}

export interface InterruptionClassifier {
  classify(
    context: InterruptionContext,
    signal?: AbortSignal,
  ): Promise<InterruptionDecision>;
}
```

A default implementation might combine:

- deterministic rules;
- VAD / audio thresholds;
- text heuristics;
- a low-latency LLM fallback for ambiguous cases.

## Agent behavior model port

The main conversational model can also be hidden behind a more domain-specific interface if desired.

```ts
export interface AgentTurnInput {
  agent: AgentDefinition;
  state: AgentRuntimeState;
  event: AgentEvent;
}

export type AgentIntent =
  | { type: "say"; text: string }
  | { type: "stay_silent"; reason?: string }
  | { type: "update_state"; patch: Record<string, unknown> };

export interface AgentBrain {
  respond(
    input: AgentTurnInput,
    signal?: AbortSignal,
  ): AsyncIterable<AgentIntent>;
}
```

This allows the rest of Agent Core to be tested without invoking a real LLM at all.

## Provider registry

Keep configuration outside domain logic.

```ts
export interface ProviderRegistry {
  languageModels: Map<string, LanguageModel>;
  speechRecognizers: Map<string, SpeechRecognizer>;
  speechSynthesizers: Map<string, SpeechSynthesizer>;
  interruptionClassifiers: Map<string, InterruptionClassifier>;
}
```

An agent or environment can select adapters by logical name:

```yaml
providers:
  language_model: primary-llm
  speech_to_text: primary-stt
  text_to_speech: primary-tts
  interruption_classifier: fast-controller-model
```

The logical names map to concrete adapters through application configuration.

## OpenAI-compatible adapter

The first implementation can be an adapter around an OpenAI-compatible HTTP API.

Conceptually:

```ts
class OpenAICompatibleLanguageModel implements LanguageModel {
  constructor(private config: {
    baseUrl: string;
    apiKey?: string;
    defaultModel: string;
    headers?: Record<string, string>;
  }) {}

  async *generate(
    messages: ModelMessage[],
    options: GenerationOptions,
    signal?: AbortSignal,
  ): AsyncIterable<GenerationEvent> {
    // Translate generic request -> provider request.
    // Stream provider response.
    // Translate provider chunks -> generic GenerationEvent.
  }
}
```

Configuration example:

```yaml
providers:
  primary-llm:
    adapter: openai-compatible
    base_url: ${LLM_BASE_URL}
    api_key: ${LLM_API_KEY}
    default_model: ${LLM_MODEL}
```

Do not let `base_url`, `choices`, `delta`, or other provider-specific concepts leak into Agent Runtime code.

## Synthetic provider

A synthetic provider is valuable from day one.

Example:

```ts
class ScriptedLanguageModel implements LanguageModel {
  constructor(private replies: string[]) {}

  async *generate(): AsyncIterable<GenerationEvent> {
    const reply = this.replies.shift() ?? "";

    for (const token of reply.split(" ")) {
      yield { type: "text_delta", text: token + " " };
    }

    yield { type: "completed", finishReason: "scripted" };
  }
}
```

This enables:

- deterministic unit tests;
- latency simulations;
- cancellation tests;
- interruption tests;
- frontend development without model costs;
- demo fallback behavior.

## Synthetic speech adapters

Useful test implementations include:

```text
SyntheticSpeechRecognizer
- accepts fake transcript events directly
- can simulate partial/final transcripts
- can simulate VAD boundaries

SilentSpeechSynthesizer
- emits timing markers without real audio

SyntheticSpeechSynthesizer
- emits generated PCM silence/noise for transport tests
```

## Capability discovery

Avoid one huge interface full of optional methods.

A provider can instead expose capabilities:

```ts
export interface ProviderCapabilities {
  streaming: boolean;
  cancellation: boolean;
  structuredOutput?: boolean;
  audioInput?: boolean;
  audioOutput?: boolean;
  realtimeNative?: boolean;
}
```

This keeps Agent Core generic while allowing optimized paths later.

## Native realtime providers later

A future provider might support audio-in/audio-out and model reasoning through one realtime session.

Do not force this through separate STT + LLM + TTS internally if that damages latency.

Instead add an optional higher-level port:

```ts
export interface NativeRealtimeAgentSession {
  sendAudio(chunk: AudioChunk): Promise<void>;
  sendEvent(event: AgentEvent): Promise<void>;
  events(): AsyncIterable<AgentEvent>;
  interrupt(): Promise<void>;
  close(): Promise<void>;
}

export interface NativeRealtimeProvider {
  createSession(config: unknown): Promise<NativeRealtimeAgentSession>;
}
```

The orchestration layer can select either:

```text
Pipeline mode:
STT -> LLM -> TTS

or

Native realtime mode:
provider realtime session
```

Both should emit normalized Agent Core events to the rest of the application.

## Error model

Normalize provider failures.

```ts
export type ProviderErrorCode =
  | "auth_error"
  | "rate_limited"
  | "timeout"
  | "cancelled"
  | "invalid_request"
  | "unavailable"
  | "unknown";

export class ProviderError extends Error {
  constructor(
    public readonly code: ProviderErrorCode,
    message: string,
    public readonly retryable: boolean,
    public readonly cause?: unknown,
  ) {
    super(message);
  }
}
```

This prevents provider-specific error classes from spreading through the runtime.

## Testing philosophy

Every important runtime behavior should be testable without network access.

Examples:

- user interrupts after 800 ms -> current speech is cancelled;
- user says "mhm" -> agent continues;
- background noise -> ignored;
- 8 seconds of silence -> idle event emitted;
- fake external order event -> agent may proactively respond;
- model stream cancelled -> no stale audio continues playing;
- old response arrives after supersession -> discarded.

If a behavior cannot be tested with fake/synthetic providers, the boundary is probably too tightly coupled.

