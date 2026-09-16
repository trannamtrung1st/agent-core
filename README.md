# Agent Core MVP

Agent Core is a lightweight runtime for creating persistent AI identities capable of natural, real-time conversation.

An agent can represent a customer service representative, examiner, tutor, friend, interviewer, receptionist, or another role. The goal of the MVP is not to build a general-purpose autonomous-agent platform. The goal is to prove that one reusable Agent Core can inhabit different identities and interact naturally through text and voice.

The distinctive idea is the combination of:

- persistent agent identity and behavioral policy;
- continuously running conversational state;
- a separate Interaction Controller that observes the live environment;
- interruption and turn-taking support;
- proactive interaction initiated by the agent when appropriate;
- provider-agnostic backend interfaces;
- a simple personal-chat-style web UI with a voice-call mode.

## MVP thesis

> Make an AI agent feel present in a live conversation.

The most important qualities are:

1. low latency;
2. natural turn-taking;
3. interruption / barge-in;
4. identity consistency;
5. restrained proactive behavior;
6. conversation continuity.

Complex tool orchestration, multi-agent systems, workflow builders, marketplaces, and advanced long-term memory are intentionally outside the first MVP.

## Project documents

- [Product Vision](docs/01-product-vision.md)
- [MVP Scope](docs/02-mvp-scope.md)
- [System Architecture](docs/03-system-architecture.md)
- [Backend Interfaces](docs/04-backend-interfaces.md)
- [Interaction Controller](docs/05-interaction-controller.md)
- [Realtime Voice and Conversation](docs/06-realtime-voice.md)
- [Event Model](docs/07-event-model.md)
- [Development Roadmap](docs/08-development-roadmap.md)
- [Demo Scenarios](docs/09-demo-scenarios.md)

## Implementation baseline

Milestone 1–12 are implemented: a .NET 10 / C# 14 layered solution and a React/Vite chat UI talk to `/hubs/session` with SignalR + MessagePack. Examiner and support identities load from `agents/examiner.json` and `agents/customer-support.json`. `/health` and session lifecycle HTTP APIs exist. Live user text uses the hub, not a public chat HTTP endpoint. Voice click preflights getUserMedia/AudioWorklet, then streams canonical PCM into synthetic STT after Mode=voice. Streamed model text is segmented and synthesized through `SyntheticSpeechSynthesizer` into the output worklet with playback acknowledgements; capture stays active during output. Mute is input-only; disconnect tears down STT and TTS. Wait barge-in supersedes the live response, flushes the worklet, and freezes conservative spoken-until for the next turn. After silence the examiner may offer one help hint; support may mention a simulated order update through in-process environment ingress, with StaySilent cooldown and no repeated nudges or detached initiative. SQLite (or the in-memory store) persists snapshots so reconnect/resume restores received-clamped history without PCM replay; ended sessions cannot attach. `OpenAICompatibleLanguageModel` is selected by alias/profile (Synthetic stays scripted). The Interaction Controller applies orthogonal turn-taking and response supersession under `FakeTimeProvider`. `docker compose up` is a key-free Synthetic integration path with a persistent SQLite volume; overlay `docker-compose.real.yml` for hosted OpenRouter/OpenAI. Native `dotnet run` + Vite remains the fast loop. See [Implementation Plan](docs/18-implementation-plan.md) and [MVP handoff](docs/reports/m12-mvp-handoff.md).

Use a .NET 10 LTS / C# 14 modular monolith with ASP.NET Core Minimal APIs, SignalR + MessagePack (hub in Milestone 5), EF Core 10 + SQLite (Milestone 11), and a React/Vite strict TypeScript SPA. One asynchronous Session Runtime mailbox owns each session's mutable state. Provider capabilities remain independently replaceable through ILanguageModel, ISpeechRecognizer and ISpeechSynthesizer. OpenAI realtime transcription and OpenAI TTS are the initial speech adapters and remain replaceable without Agent Runtime or Interaction Controller changes; live OpenAI keys are not required for default development or tests. OpenAICompatibleLanguageModel uses direct HTTP/SSE; OpenRouter is its preferred initial hosted text configuration, not an architectural dependency. Opt-in adapter smoke may use OpenRouter's Free Models Router (`openrouter/free`); the Real/demo profile requires a **fixed** operator-selected model ID. Read `OPENROUTER_API_KEY` from environment or `dotnet user-secrets` only.

The canonical MVP voice pipeline is **STT → Interaction Controller → text Agent Runtime / LLM → Speech Segmenter → TTS**. Hosted STT is OpenAI realtime transcription (recommended default `gpt-live-transcribe`); hosted LLM is OpenAICompatibleLanguageModel via OpenRouter; hosted TTS is the OpenAI speech adapter. Text and voice are modes of **one** Session. The microphone stays active during TTS playback, using full-duplex AudioWorklets and canonical PCM16 mono 24 kHz. Cancellation and response identity jointly prevent stale output after interruption. Synthetic mode boots without AI keys or network access and is the default development/test path. Default verification is offline and deterministic; live OpenRouter or OpenAI smoke tests are explicit opt-in and skip when keys are missing. Hosted, hybrid and future fully on-prem configurations use the same runtime: replace the text endpoint with a local compatible inference server and select local STT/TTS independently. Native speech-to-speech/realtime reasoning is a future optional optimization, excluded from MVP. Authentication, WebRTC, vector memory and general autonomous-agent platform features are outside MVP. Use local `git` for daily work. Intended repository CI is GitHub Actions (`.github/workflows/synthetic.yml`).

Fast development uses native .NET + Vite processes with SQLite; Docker is not required. Docker Compose is the supported reproducible integration/demo environment (minimal Synthetic Compose from Milestone 5; hardening in Milestone 12), using one application container and a persistent SQLite volume. `docker compose up` stays Synthetic and key-free; `docker compose -f docker-compose.yml -f docker-compose.real.yml up` selects hosted OpenRouter text (copy `.env.example` to gitignored `.env` for keys). See [local workflows](docs/17-observability-and-operations.md#running-after-implementation); future hybrid/on-prem Compose topologies preserve the same runtime. Primary browser demo target is current Chromium-family desktop (Chrome/Edge).

## Implementation specifications

- [Technology Decisions](docs/10-technology-decisions.md)
- [Repository Structure](docs/11-repository-structure.md)
- [Backend Implementation Specification](docs/12-backend-implementation-spec.md)
- [Frontend Implementation Specification](docs/13-frontend-implementation-spec.md)
- [HTTP API and Realtime Protocol](docs/14-api-and-realtime-protocol.md)
- [Persistence and Configuration](docs/15-persistence-and-configuration.md)
- [Testing Strategy](docs/16-testing-strategy.md)
- [Observability and Operations](docs/17-observability-and-operations.md)
- [Implementation Plan](docs/18-implementation-plan.md)

The future run commands are documented in [Operations](docs/17-observability-and-operations.md#running-after-implementation). Milestone 12 records per-stage `AgentCore.Runtime` measurements and Compose SQLite volume survival.

## Working with Codex and Cursor

Both editors use the shared [AGENTS.md](AGENTS.md) instructions and the skills in [.agents/skills](.agents/skills). This uses their native shared skill discovery ([Codex documentation](https://learn.chatgpt.com/docs/build-skills), [Cursor documentation](https://cursor.com/docs/skills)); no editor-specific skill copies are needed. Existing Playwright MCP configuration remains in `.codex/config.toml` and `.cursor/mcp.json`. Impeccable’s installer may create a local `.cursor/skills/impeccable` copy; it is gitignored. Use [`.agents/skills/impeccable`](.agents/skills/impeccable) as the committed skill.

Use [develop](.agents/skills/develop/SKILL.md) for code implementation, fixes, planning and reviews, and [document](.agents/skills/document/SKILL.md) for specifications, decisions and documentation reviews. For visual UI quality, use [impeccable](.agents/skills/impeccable/SKILL.md); product and visual adapters live in [.agents/context](.agents/context) and defer to `/docs`. In Codex, invoke `$develop` or `$document`; in Cursor, select `/develop` or `/document` from the skill menu. Both workflows also support selection from a matching natural-language request.

| Task | Codex example | Cursor example |
| --- | --- | --- |
| Implement a milestone | `$develop implement milestone 1` | `/develop implement milestone 1` |
| Review code | `$develop review the frontend realtime implementation` | `/develop review the frontend realtime implementation` |
| Review specs | `$document review the repo for implementation readiness` | `/document review the repo for implementation readiness` |
| Update a decision | `$document update specs for local STT support` | `/document update specs for local STT support` |
| Visual UI review | `$impeccable audit` / `$impeccable polish` | `/impeccable audit` / `/impeccable polish` |

These two composition skills load only relevant specialists: architecture, backend, frontend, realtime, providers, testing, persistence, operations and docs-consistency. Impeccable is a separate UI-design skill, not a third composition entry. Product/design documents remain the source of truth. Documentation work stays docs-only unless implementation is explicitly requested; these agent instructions and skills do not start Milestone 1.
