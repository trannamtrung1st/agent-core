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

## License

Agent Core is proprietary software. Source availability does not grant permission to use, copy, modify, or redistribute it. See [LICENSE](LICENSE) for details.

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

Milestone 1–12 are implemented: a .NET 10 / C# 14 layered solution and a React/Vite chat UI talk to `/hubs/session` with SignalR + MessagePack. Examiner, support, and compliance identities load from `agents/examiner.json`, `agents/customer-support.json`, and `agents/compliance.json`. `/health` and session lifecycle HTTP APIs exist. Live user text uses the hub, not a public chat HTTP endpoint. Voice click preflights getUserMedia/AudioWorklet, then streams canonical PCM into synthetic STT after Mode=voice. Streamed model text is segmented and synthesized through `SyntheticSpeechSynthesizer` into the output worklet with playback acknowledgements; capture stays active during output. Mute is input-only; disconnect tears down STT and TTS. Wait barge-in supersedes the live response, flushes the worklet, and freezes conservative spoken-until for the next turn. After silence the examiner may offer a help hint (shipped JSON `maxPerSilencePeriod=1`). Customer support may offer up to two consecutive idle nudges per silence period (pinned `maxPerSilencePeriod` / `MaxConsecutiveProactiveTurns` in JSON; runtime caps; initiative evaluation decides Speak vs StaySilent), then `RequestDeactivate` at cap. Support may also mention a simulated order update through in-process environment ingress, with StaySilent cooldown, silent-evaluation bounds, and no detached initiative. `POST /api/v2/sessions/{id}/deactivate` pauses a runtime without archive or v1 end. SQLite (or the in-memory store) persists snapshots so reconnect/resume restores received-clamped history without PCM replay; ended sessions cannot attach, though the UI can open their HTTP history read-only. `OpenAICompatibleLanguageModel` is selected by alias/profile (Synthetic stays scripted). The Interaction Controller applies orthogonal turn-taking and response supersession under `FakeTimeProvider`. `docker compose up` is a key-free Synthetic integration path with a persistent SQLite volume; overlay `docker-compose.real.yml` for hosted OpenRouter/OpenAI. Native `dotnet run` + Vite remains the fast loop. See [Implementation Plan](docs/18-implementation-plan.md) and [MVP handoff](docs/reports/m12-mvp-handoff.md).

Use a .NET 10 LTS / C# 14 modular monolith with ASP.NET Core Minimal APIs, SignalR + MessagePack (hub in Milestone 5), EF Core 10 + SQLite (Milestone 11), and a React/Vite strict TypeScript SPA with Ant Design v6 as the generic UI. One asynchronous Session Runtime mailbox owns each session's mutable state. Provider capabilities remain independently replaceable through ILanguageModel, ISpeechRecognizer and ISpeechSynthesizer. Observed selectable hosted speech is OpenAI TTS and OpenAI-compatible **batch** STT; OpenAI realtime STT is still unselectable. Live OpenAI keys are not required for default development or tests. OpenAICompatibleLanguageModel uses direct HTTP/SSE; OpenRouter is its preferred initial hosted text configuration, not an architectural dependency. Opt-in adapter smoke may use OpenRouter's Free Models Router (`openrouter/free`); the Real/demo **default** remains a **fixed** operator-selected model ID. The shipped Real catalog also offers `openai/gpt-4o-mini-2024-07-18` and `openrouter/free` as explicit session choices. Read `OPENROUTER_API_KEY` from environment or `dotnet user-secrets` only.

The canonical MVP voice pipeline is **STT → Interaction Controller → text Agent Runtime / LLM → Speech Segmenter → TTS**. Speech **providers** and **transports** are independent: Synthetic or hosted adapters use `serverAudio` PCM; Browser STT/TTS are client transports (`clientTranscript` / `clientSpeech`) with no backend speech ports. Hosted LLM is OpenAICompatibleLanguageModel via OpenRouter. Hosted TTS is selectable OpenAI speech when a backend key is present. Hosted STT that is selectable today is `OpenAICompatibleBatch` (no streaming input, no interim partials). Text and voice are modes of **one** Session. The microphone stays active during TTS playback on `serverAudio`; Browser STT is locally suspended during agent output (half-duplex) without sending PCM to backend STT—the browser vendor may still use a cloud recognizer. Cancellation and response identity jointly prevent stale output after interruption. Synthetic mode boots without AI keys or network access and is the default development/test path. Default verification is offline and deterministic; live OpenRouter or OpenAI smoke tests are explicit opt-in and skip when keys are missing. **HOSTED-04** (an actual non-Synthetic real-voice smoke) remains unverified until that opt-in succeeds. Hosted, hybrid and future fully on-prem configurations use the same runtime: replace the text endpoint with a local compatible inference server and select local STT/TTS independently. Native speech-to-speech/realtime reasoning is a future optional optimization, excluded from MVP. Authentication, WebRTC, vector memory and general autonomous-agent platform features are outside MVP. Use local `git` for daily work. Intended repository CI is GitHub Actions (`.github/workflows/synthetic.yml`). See [P1 replaceable speech handoff](docs/reports/p1-replaceable-speech-handoff.md).

**Observed post-MVP pack (A–H):** catalog, attachments, rich responses, repeated initiative/deactivation, versioned role environments, lazy session workspaces, session-owned artifacts, bounded typed tools (Support/Compliance workflows, OpenAI-compatible tool_calls mapping, 12/30s/120s/8 MiB caps), and a Docker `sandbox.run` capability (`busybox:1.36`, network none, read-only root, dropped caps, CPU/memory/PID/time bounds, session-scoped export). Broad `process`/`shell` stay disabled and are not on shipped Support/Compliance allowlists. Phase I WorkItems are **not-applicable** until a later accepted Support/Compliance/`sandbox.run` workflow must continue after SessionRuntime deactivation. **P6** durable background work is a separate observed capability: a due occurrence with no live session becomes one WorkItem, Background Work can inspect, cancel, and approve it, and the result stays out of the transcript. **P6 frozen** on `30adaeb` (workflow `36085265506` green; last behavior `bef77d1`). **P7** is next. Historical MVP Milestones 1–12 remain the shipped baseline. Product docs 01–18 and this README describe that behavior without the original proposal pack. See [Technology Decisions](docs/10-technology-decisions.md#post-mvp-planned-until-verified), [Implementation Plan](docs/18-implementation-plan.md#post-mvp-phases-planned-until-verified), [post-MVP handoff](docs/reports/post-mvp-handoff.md), and [proposal retirement](docs/reports/proposal-retirement.md).

**Follow-on P1 history, lifecycle, and speech locale:** lazy history, additive semantic lifecycle, and provider-neutral speech locale UI/adapters are observed in [Technology Decisions](docs/10-technology-decisions.md#decision-bounded-history-and-durable-lastentrysequence) and [Implementation Plan](docs/18-implementation-plan.md#follow-on-p1-history-lifecycle-and-multilingual-speech). They do not replace observed P1 replaceable speech. **P1 freeze:** `dceaccbad9a4db8908af147b5353805a2b1af288` (`dceaccb`, 2026-09-19) — Document V4.1 Flash as the intentional Real development/demo default; CI/Synthetic + Compose verification is green on that HEAD. Do not reopen P1. Observed contracts include: live `Active` never terminalizes; first-party lifecycle is always User; trusted-host purpose/policy create is `/api/v2/host/sessions`; Playwright history/terminal seeds share a dedicated SQLite file. Earlier gate counts on repair `15930985f54e2e6bf4019dd0d8040796883021c7`: Domain 40; Infra 112/9 skip; Application 356 blame-hang; API 123; web 328; Playwright 34. Compose-equivalent SQLite volume smoke passed on :5088 (exact script blocked by Real :5080). Unedited Voice re-run `p1-repair-chrome-probe-run.log` (queued-text pass; combined `non-english-browser-smoke` remains fail). Unedited `p1-repair-fr-smoke-r2` `fr-FR` Browser STT/TTS pass. Earlier probe records stay as written (`p1-final-chrome-probe-run.log` on `df0a12c`; `p1-final-fr-smoke-r2` on `5764010`; original failed probes remain fail). Optional hosted multilingual checks are unverified. P2A and P2B are observed (P2B frozen on `e0e8a55`); **P2E** and **P2C** are observed/frozen (see [Implementation Plan](docs/18-implementation-plan.md#p2e--multimodal-image-input-capability-closure-observed) and [P2C](docs/18-implementation-plan.md#p2c--personalization-boundary-observed)). **P2 closed/frozen** on `47d6ff65142d2d454c4aa3101b0f43a38f01389a` (`47d6ff6`, 2026-09-21) after mandatory whole-output review; closure repair on that HEAD (Real-catalog fallback, post-commit profile notification, preferredName trim). CI/Synthetic + Compose green (workflow run `35552740853`). Optional Real structured-response and vision probes **skipped/unverified**. Do not reopen P2 without a reproducible regression. P2D session model selection is observed (trusted catalog, persisted per-session resolved choice, Codex-like Default/effort UI, session isolation). **P3 capability closure** is `general-assistant` v7: working-directory paths, `workspace.search`/`workspace.move`, approval-gated `http.request`, and the existing web, artifact, sandbox, and email tools. `demo.sensitive_action` stays on `approval-demo`. Synthetic remains key-free; Real may supply `BRAVE_SEARCH_API_KEY` and Gmail OAuth refresh variables per `.env.example`. Without a Brave key, `web.search` stays hidden. Optional Real probes remain skipped unless explicitly opted in. See [P3 closure report](docs/reports/p3-freeze-candidate.md). **P4 frozen** on `822028f` (hosted workflow `35806764609` green). See [P4 closure report](docs/reports/p4-freeze-candidate.md). Optional model memory tools and embeddings were not added. **P5** durable schedules, one allowlisted application event, and the Schedules drawer are implemented. Hosted Synthetic workflow [`35840226344`](https://github.com/trannamtrung1st/agent-core/actions/runs/35840226344) is historical **green** evidence on `267fcbd`. Whole-phase review did not freeze P5; stale-schedule, lost-begin, action-specific authorization, and owner-scoped dedupe repairs stay inside P5. Hosted Synthetic workflow [`35889243368`](https://github.com/trannamtrung1st/agent-core/actions/runs/35889243368) is green on `7243323`. See [P5 closure report](docs/reports/p5-freeze-candidate.md). P6 frozen on `30adaeb` (workflow `36085265506`). P7 is next. **P3 key-free freeze** on `4dbb920` (verified hosted descendant `ac795b6`, workflow `35682808408`). Synthetic/fake historical reread verified; Real GPT-4o mini historical reread remains an open provider gap (not P4). See [Implementation Plan](docs/18-implementation-plan.md#p3bp3f--tools-web-approval-email-assistant-capability-closure) and [P3 closure report](docs/reports/p3-freeze-candidate.md).

Fast development uses native .NET + Vite processes with SQLite; Docker is not required. Docker Compose is the supported reproducible integration/demo environment (minimal Synthetic Compose from Milestone 5; hardening in Milestone 12), using one application container and a persistent SQLite volume. `docker compose up` stays Synthetic and key-free; `docker compose -f docker-compose.yml -f docker-compose.real.yml up` selects hosted OpenRouter text (copy `.env.example` to gitignored `.env` for keys). See [local workflows](docs/17-observability-and-operations.md#running-after-implementation); future hybrid/on-prem Compose topologies preserve the same runtime. Primary browser demo target is current Chromium-family desktop (Chrome/Edge).

## Implementation specifications

- [Technology Decisions](docs/10-technology-decisions.md)
- [Repository Structure](docs/11-repository-structure.md)
- [Backend Implementation Specification](docs/12-backend-implementation-spec.md)
- [Frontend Implementation Specification](docs/13-frontend-implementation-spec.md)
- [Ant Design migration handoff](docs/reports/antd-migration-handoff.md)
- [HTTP API and Realtime Protocol](docs/14-api-and-realtime-protocol.md)
- [Persistence and Configuration](docs/15-persistence-and-configuration.md)
- [Testing Strategy](docs/16-testing-strategy.md)
- [Observability and Operations](docs/17-observability-and-operations.md)
- [Implementation Plan](docs/18-implementation-plan.md)
- [P0 agent-lifecycle handoff](docs/reports/p0-agent-lifecycle-handoff.md)
- [P1 replaceable speech handoff](docs/reports/p1-replaceable-speech-handoff.md)

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

Development and behavior reviews include [runtime verification](.agents/skills/testing/SKILL.md#runtime-verification) when runnable. Both editors should use Playwright MCP to exercise affected frontend journeys against the running Synthetic app, with Playwright E2E or another browser tool as a fallback. Backend work should execute representative use cases through integration tests or a local Synthetic host. Reports distinguish observed outcomes from static inspection and name any blocked verification; default runs remain key-free.

The Playwright MCP configurations use isolated browser profiles so editor sessions do not contend for one persistent profile. Output uses Playwright's default workspace/temporary-directory resolution rather than paths relative to the server's launch directory. Reconnect the MCP server after configuration changes; use the returned artifact paths when reporting evidence. See [Playwright MCP configuration](https://github.com/microsoft/playwright-mcp#configuration).
