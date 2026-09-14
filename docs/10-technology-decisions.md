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
| Tests | xUnit, WebApplicationFactory; Vitest, React Testing Library, Playwright | Offline deterministic behavior and boundary tests | Opt-in real-provider smoke tests |

MessagePack is case-sensitive; use explicit camelCase string keys and binary DTOs tested with the JavaScript client, as described in [Microsoft's SignalR MessagePack documentation](https://learn.microsoft.com/en-us/aspnet/core/signalr/messagepackhubprotocol?view=aspnetcore-10.0). Http resilience policies must explicitly account for unsafe methods; see [Microsoft HTTP resilience guidance](https://learn.microsoft.com/en-us/dotnet/core/resilience/http-resilience). Project-specific timeout/retry choices are specified in [Backend Implementation](12-backend-implementation-spec.md), not implied library defaults.

## Decision: composed voice pipeline

**Decision:** The primary and default MVP voice architecture is STT → Interaction Controller → text Agent Runtime / LLM → Speech Segmenter → TTS. It does not use speech-to-speech reasoning. Input capture remains active while output plays.

**Rationale:** Independent capabilities simplify testing, debugging and provider replacement; preserve Agent Core's control of interruption; support different models for different identities; permit deterministic synthetic providers and per-stage latency measurement; and support a future fully on-prem deployment.

**Trade-off:** Extra recognition, segmentation and synthesis stages can add latency compared with a native end-to-end speech model.

**Mitigation:** Stream STT and use partials when available, apply fast controller heuristics, stream model text into natural Speech Segments, stream TTS into AudioWorklet playback, optionally duck locally, and measure each stage separately.

**Future migration:** INativeRealtimeProvider may later offer an optional optimized path while preserving normalized events and response/session invariants. It is excluded from all MVP implementation milestones and must not complicate the composed path.

## Decision: OpenRouter first for hosted text reasoning

**Decision:** Prefer OpenRouter as the initial hosted text gateway, configured through OpenAICompatibleLanguageModel. It is a configuration choice, never an Agent Core dependency or reasoning interface. The gateway exposes an OpenAI-style endpoint at `https://openrouter.ai/api/v1/`; see the [OpenRouter quickstart](https://openrouter.ai/docs/quickstart).

**Rationale:** A hosted gateway makes it practical to experiment with model families and compare conversation latency, quality and cost while retaining one adapter protocol.

**Trade-off:** OpenRouter is hosted, not on-prem. Its model catalog, rate limits, parameters and streaming details vary; compatibility needs contract tests rather than assumptions.

**Future migration:** Replace the configured endpoint and model mapping with a local OpenAI-compatible inference server; select local STT and TTS independently. vLLM is one example with a [compatible Chat API](https://docs.vllm.ai/en/latest/serving/online_serving/openai_compatible_server/), not a required dependency. Direct OpenAI and other compatible hosted providers are also possible configurations. Agent Runtime and Interaction Controller do not change.

[Configuration](15-persistence-and-configuration.md#hosted-and-on-prem-provider-configurations) owns examples and alias/model resolution; [Operations](17-observability-and-operations.md#hosted-hybrid-and-on-prem-deployment) owns deployment topologies.

## Decision: STT/TTS are replaceable providers

**Decision:** OpenAI STT/TTS are the initial hosted adapters. Changing STT, LLM or TTS must not require changes to Agent Runtime or Interaction Controller.

**Rationale:** Support future on-prem deployment, deterministic testing, cost/quality experimentation, no vendor lock-in and independent evolution of speech and reasoning.

**Consequence:** Application depends on capability interfaces; configuration and DI select Infrastructure adapters. Capabilities may differ, so existing degraded interaction policies remain supported. Vendor-specific code stays at the edge. [Provider ports](04-backend-interfaces.md#speech-provider-replacement-rule) and [configuration](15-persistence-and-configuration.md#provider-selection-and-di) own the concrete contracts and examples.

## Decision: Docker Compose for reproducible local integration

**Decision:** Docker is optional for development; Docker Compose is supported for reproducible integration/demo. The fast loop is native .NET + Vite. The production-like MVP artifact is one application container serving API, SignalR and built SPA, with a persistent SQLite volume.

**Rationale:** Provide a one-command demo environment, environment parity and a simple path to adding local inference services later.

**Trade-off:** Container builds/restarts are slower than the native hot-reload loop, so Compose does not block early runtime/frontend work.

**Consequence:** Add container/Compose artifacts at Milestone 12, after native development, synthetic full-stack and real-provider integration. Hosted, hybrid and on-prem topologies keep the same runtime semantics. Docker is deployment tooling, not an Agent Core dependency. [Operations](17-observability-and-operations.md#docker-compose-integration-and-demo) owns topology and workflow details.

## Explicit non-goals

No native speech-to-speech/realtime model in MVP. No microservices, Kafka, RabbitMQ, Redis requirement, Kubernetes requirement, Orleans/Akka actor framework, MediatR merely for layer forwarding, generic workflow engine, multi-agent system, vector database, RAG platform, plugin marketplace, OAuth/login system for MVP, WebRTC in the first version, mobile app, native desktop app, elaborate avatar system, SSR, Next.js or large component framework. No autonomous tools/platform, distributed event bus or generic repository framework. Reconsider only when actual requirements justify the cost.

## What may still be measured

Provider selection within independently configured speech ports, VAD thresholds, frame size within the allowed range, TTS phrase segmentation and latency optimization are tuning variables. The default behavior and degraded paths are specified; measurement must not reopen project ownership, transport, storage or response identity decisions. No guaranteed provider-dependent SLA is implied.
