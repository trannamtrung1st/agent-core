# Technology Decisions

These decisions are the implementation baseline. Resolve package patches at implementation time to the latest supported compatible releases; do not scatter transient patch pins across these docs. .NET 10 is LTS and pairs with C# 14. See the [official support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) and [.NET 10 download/version information](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

| Area | Decision | Rationale / trade-off | Future migration path |
| --- | --- | --- | --- |
| Backend | .NET 10 LTS, C# 14, ASP.NET Core 10 Minimal APIs | One supported runtime, simple HTTP surface | Upgrade supported runtime as one unit |
| Structure | Modular monolith, Domain/Application/Contracts/Infrastructure/Api | Explicit boundaries without distributed operations | Services only for measured scaling needs |
| Concurrency | System.Threading.Channels, one async mailbox reader per session | Deterministic state ownership; bounded queue design required | Scale independent sessions, preserve ownership |
| Live transport | ASP.NET Core SignalR + MessagePack, WebSockets | Small bidirectional binary surface; application handles PCM buffering | WebRTC adapter if media constraints justify it |
| JSON | System.Text.Json | Built into platform; explicit versioned records | Schema-version migration |
| Persistence | EF Core 10 + SQLite | Single-app operation, transactional history; one-writer constraints | PostgreSQL EF provider with tested conversions/migrations |
| Providers | Narrow application ports, first LLM direct OpenAI-compatible HttpClient/SSE | Vendor neutrality; own parser/error handling | More adapters, no runtime vendor objects |
| HTTP lifetime/resilience | IHttpClientFactory + Microsoft.Extensions.Http.Resilience | Managed clients and explicit failure budgets | Tune measured policy; no blind stream replay |
| Time/IDs | TimeProvider, injectable IIdGenerator; UUIDv7 event/response IDs, random UUIDv4 session IDs | Deterministic tests; session IDs retain random bearer entropy | Keep wire UUID strings stable |
| Speech | Independent STT/TTS, canonical PCM16 mono 24 kHz | Canonical composed text-first pipeline; format conversion at adapters | Future-only INativeRealtimeProvider optimization |
| Definitions | Versioned JSON files via store | No YAML dependency; immutable identities | Alternative store preserving schema/version semantics |
| Frontend | React SPA, Vite, strict TypeScript, plain CSS/CSS Modules | Simple personal chat; client-only audio | Expand UI only for product needs |
| Client state/API | Zustand; fetch wrapper; @microsoft/signalr + @microsoft/signalr-protocol-msgpack | Small active state store, few HTTP endpoints | Add data caching only with evidence |
| Observability | Microsoft.Extensions.Logging, OpenTelemetry via ActivitySource/Meter | Correlate conversational latency with privacy defaults | Optional OTLP export |
| Tests | xUnit, WebApplicationFactory; Vitest, React Testing Library, Playwright | Offline deterministic behavior and boundary tests | Explicit opt-in live-provider smokes; skip when keys are missing |

MessagePack is case-sensitive; use explicit camelCase string keys and binary DTOs tested with the JavaScript client, as described in [Microsoft's SignalR MessagePack documentation](https://learn.microsoft.com/en-us/aspnet/core/signalr/messagepackhubprotocol?view=aspnetcore-10.0). Http resilience policies must explicitly account for unsafe methods; see [Microsoft HTTP resilience guidance](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience). Project-specific timeout/retry choices are specified in [Backend Implementation](12-backend-implementation-spec.md), not implied library defaults.

## Decision: composed voice pipeline

**Decision:** The primary and default MVP voice architecture is STT → Interaction Controller → text Agent Runtime / LLM → Speech Segmenter → TTS. It does not use speech-to-speech reasoning. Input capture remains active while output plays.

**Rationale:** Independent capabilities simplify testing, debugging and provider replacement; preserve Agent Core's control of interruption; support different models for different identities; permit deterministic synthetic providers and per-stage latency measurement; and support a future fully on-prem deployment.

**Trade-off:** Extra recognition, segmentation and synthesis stages can add latency compared with a native end-to-end speech model.

**Mitigation:** Stream STT and use partials when available, apply fast controller heuristics, stream model text into natural Speech Segments, stream TTS into AudioWorklet playback, optionally duck locally, and measure each stage separately.

**Future migration:** INativeRealtimeProvider may later offer an optional optimized path while preserving normalized events and response/session invariants. It is excluded from all MVP implementation milestones and must not complicate the composed path.

## Decision: OpenRouter first for hosted text reasoning

**Decision:** Prefer OpenRouter as the initial hosted text gateway, configured through OpenAICompatibleLanguageModel. It is a configuration choice, never an Agent Core dependency or reasoning interface. The gateway exposes an OpenAI-style endpoint at `https://openrouter.ai/api/v1/`; see the [OpenRouter quickstart](https://openrouter.ai/docs/quickstart). Implement and configure this adapter during the existing milestone plan (Milestone 3). The default hosted **test** `DefaultModel` is OpenRouter's Free Models Router, `openrouter/free`; see the [Free Models Router](https://openrouter.ai/docs/guides/routing/routers/free-router). Exact routed-model quality and structured-output correctness are not milestone gates. Read the key from environment or backend user-secrets only (`OPENROUTER_API_KEY`); do not commit secrets. One-time operator bootstrap from a gitignored drop-file is specified in [local personal workspace](15-persistence-and-configuration.md#local-personal-workspace); it is not a runtime config source. Default tests remain Synthetic/offline and must pass without this key. Real OpenRouter smoke tests are explicit opt-in and skip cleanly when the key is missing.

**Rationale:** A hosted gateway makes it practical to experiment with model families and compare conversation latency, quality and cost while retaining one adapter protocol. The free router unblocks live adapter smoke without treating catalog quality as an acceptance criterion.

**Trade-off:** OpenRouter is hosted, not on-prem. Its model catalog, rate limits, parameters and streaming details vary; compatibility needs contract tests rather than assumptions. `openrouter/free` may route to different free models per request.

**Future migration:** Replace the configured endpoint and model mapping with a local OpenAI-compatible inference server; select local STT and TTS independently. vLLM is one example with a [compatible Chat API](https://docs.vllm.ai/en/latest/serving/online_serving/openai_compatible_server/), not a required dependency. Direct OpenAI and other compatible hosted providers are also possible configurations. Operators may later override `DefaultModel` for quality. Agent Runtime and Interaction Controller do not change.

[Configuration](15-persistence-and-configuration.md#hosted-and-on-prem-provider-configurations) owns examples and alias/model resolution; [Operations](17-observability-and-operations.md#hosted-hybrid-and-on-prem-deployment) owns deployment topologies.

## Decision: STT/TTS are replaceable providers

**Decision:** OpenAI realtime transcription (`OpenAiSpeechRecognizer`, recommended model `gpt-live-transcribe`) and OpenAI TTS are the initial hosted speech adapters. Changing STT, LLM or TTS must not require changes to Agent Runtime or Interaction Controller. Batch `/audio/transcriptions` is a separate degraded adapter. Implement the planned speech ports and adapters, but do not block development, default tests or CI on a real OpenAI key. Prioritize completing and verifying the text-conversation path. Automated tests use Synthetic STT/TTS. Real OpenAI STT/TTS integration tests and manual voice verification may wait until the operator supplies `OPENAI_API_KEY`. Missing OpenAI credentials must not fail normal build/test.

**Rationale:** Support future on-prem deployment, deterministic testing, cost/quality experimentation, no vendor lock-in and independent evolution of speech and reasoning. Speech credentials arrive later than the text adapter without changing architecture.

**Consequence:** Application depends on capability interfaces; configuration and DI select Infrastructure adapters. Capabilities may differ, so existing degraded interaction policies remain supported. Vendor-specific code stays at the edge. [Provider ports](04-backend-interfaces.md#speech-provider-replacement-rule) and [configuration](15-persistence-and-configuration.md#provider-selection-and-di) own the concrete contracts and examples.

## Decision: default verification is offline; live providers are explicit opt-in

**Decision:** Default verification is fully offline and deterministic (Synthetic adapters plus HTTP/SSE fixtures). External-provider smoke tests run only when the operator explicitly opts in. Presence of `OPENROUTER_API_KEY` or `OPENAI_API_KEY` in the environment must not cause `dotnet test` or other default suites to call hosted APIs. Opt-in smokes skip cleanly when the required key is missing. Normal development/test loops must not silently spend API credits. Deferred live-provider testing does not weaken or skip synthetic contract tests. Do not introduce additional external services solely for testing.

**Rationale:** Keep milestone gates reproducible without secrets or network, while still allowing a bounded live check of the real adapters when keys exist.

**Trade-off:** Hosted speech quality and free-router phrasing remain unverified until an explicit smoke or manual pass.

**Consequence:** [Testing Strategy](16-testing-strategy.md) owns fixtures, skip/opt-in mechanics and suite gates. [Implementation Plan](18-implementation-plan.md) applies this policy to existing milestones rather than adding test-only architecture.

## Decision: local git CLI now; GitHub Actions is intended CI

**Decision:** Day-to-day implementation uses the local `git` CLI. Intended repository CI is **GitHub Actions**. Do not create full application workflow files until the corresponding .NET/`web` projects and test scripts exist. When introduced, core CI must remain offline: `dotnet` build/test, frontend install/build/unit tests, then synthetic Playwright after those milestones, never requiring OpenAI/OpenRouter keys, internet inference, microphone, speaker or GPU. Real-provider smokes stay opt-in.

**Rationale:** Keep early work unblocked, then make the promised offline gates reproducible on the repository host.

**Consequence:** [Implementation Plan](18-implementation-plan.md) and [Testing Strategy](16-testing-strategy.md) own when workflows appear. This documentation pass does not add `.github/workflows`.

## Decision: Docker Compose for reproducible local integration

**Decision:** Docker is optional for development; Docker Compose is supported for reproducible integration/demo. The fast loop is native .NET + Vite. The production-like MVP artifact is one application container serving API, SignalR and built SPA, with a persistent SQLite volume.

**Rationale:** Provide a one-command demo environment, environment parity and a simple path to adding local inference services later.

**Trade-off:** Container builds/restarts are slower than the native hot-reload loop, so Compose does not block early runtime/frontend work.

**Consequence:** Add the first usable key-free Compose environment at Milestone 5 (after the synthetic browser text path). Milestone 12 hardens containers, SQLite volume/restart, real-provider configuration, hybrid topology and operations. Hosted, hybrid and on-prem topologies keep the same runtime semantics. Docker is deployment tooling, not an Agent Core dependency. [Operations](17-observability-and-operations.md#docker-compose-integration-and-demo) owns topology and workflow details.

## Explicit non-goals

No native speech-to-speech/realtime model in MVP. No microservices, Kafka, RabbitMQ, Redis requirement, Kubernetes requirement, Orleans/Akka actor framework, MediatR merely for layer forwarding, generic workflow engine, multi-agent system, vector database, RAG platform, plugin marketplace, OAuth/login system for MVP, WebRTC in the first version, mobile app, native desktop app, elaborate avatar system, SSR, Next.js or large component framework. No autonomous tools/platform, distributed event bus or generic repository framework. No extra hosted mock or third-party test-only inference service. Reconsider only when actual requirements justify the cost.

## What may still be measured

Provider selection within independently configured speech ports, VAD thresholds, frame size within the allowed range, TTS phrase segmentation and latency optimization are tuning variables. The default behavior and degraded paths are specified; measurement must not reopen project ownership, transport, storage or response identity decisions. No guaranteed provider-dependent SLA is implied.
