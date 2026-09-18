# TODO

Ordered by current dependency and product value.

The current baseline already includes the MVP, post-MVP phases A–H, persistent multi-session chat, attachments, rich responses, repeated initiative/deactivation, versioned role environments, session workspaces/artifacts, bounded typed tools, Docker `sandbox.run`, Synthetic full-duplex voice, and the P0 conversation-lifecycle work (adaptive wait, inactivity pause/deactivation, queued sends/Steer/Stop semantics, and rich turn presentation).

The next product slice is **cheap, replaceable browser speech**. The P0 conversation/UI stabilization pass is a frozen baseline unless a real regression appears.

---

## Maintainer notes
- [ ] Weird preferred name of 'friend', we should fix. [TBD]


## P0 — Stabilize the current conversation/UI baseline

Do this before starting the next feature slice. Do not keep expanding P0 after these gates are clean.

- [x] Run the complete key-free Synthetic gate on current HEAD, matching `.github/workflows/synthetic.yml`.

  - Domain tests.
  - Infrastructure tests.
  - Application tests.
  - API tests.
  - Frontend unit tests.
  - Frontend build.
  - Playwright Chromium suite.
  - Fix regressions before starting P1.

- [x] Do one bounded Impeccable `audit` / `harden` pass over the existing chat UI, not another redesign.

  Verify representative desktop/mobile states:
  - normal text conversation;
  - live response;
  - queued messages;
  - Steer;
  - Stop during text generation;
  - Stop during trailing voice playback;
  - attachments;
  - Markdown / rich blocks / artifact references;
  - paused + resume;
  - ended read-only history;
  - reconnect/recovery;
  - recoverable and fatal failures.

- [x] Improve failure presentation in chat.

  The current failure status is too generic. Preserve safe user-facing messages, but expose enough structured detail to distinguish:
  - provider failure;
  - persistence failure;
  - transport/reconnect failure;
  - validation/policy denial;
  - tool/sandbox failure;
  - voice/capture/playback failure.

  Prefer a compact status plus tooltip/popover/details surface. Never expose provider bodies, stack traces, secrets, or credentials.

- [x] Add/confirm regression coverage for rich response-envelope presentation together with normal reply and voice behavior.

  Cover:
  - display Markdown;
  - `SpeechText` separate from display text;
  - extra Markdown block;
  - attachment reference;
  - artifact reference;
  - unsupported/unavailable fallback;
  - reconnect/history behavior;
  - voice spoken/heard semantics remain conservative.

- [x] When the above is green, treat conversation lifecycle P0 as frozen baseline unless a real regression appears.

---

## P1 — Cheap, interchangeable real voice

### P1A — Finish speech-provider selection and capability plumbing

The core backend ports already exist:

- `ISpeechRecognizer`
- `ISpeechSynthesizer`

Synthetic implementations exist. OpenAI TTS exists. OpenAI-compatible batch STT exists. The realtime `OpenAiSpeechRecognizer` contract exists but its live session is still deferred/no-op, and Infrastructure currently resolves Synthetic speech for every profile.

Do **not** force browser APIs into backend `ISpeechRecognizer` / `ISpeechSynthesizer`. Browser speech is client-owned; keep the server adapters and browser adapters as two implementations behind one effective session capability/selection model.

- [x] Add explicit independent speech configuration.

  Example conceptual selection:
  - STT: `Synthetic | Browser | OpenAI | OpenAICompatibleBatch | future local`
  - TTS: `Synthetic | Browser | OpenAI | future local`

  STT and TTS must be independently selectable.

- [x] Replace profile-only speech resolution with explicit provider factories/selection.

  - Synthetic remains the default deterministic path.
  - Real text mode must not require speech credentials.
  - Browser mode requires no backend speech key.
  - Hosted adapters read credentials only from backend configuration/user-secrets/environment.
  - Missing hosted credentials must not break Synthetic or Browser startup.

- [x] Make `voiceAvailable` and `session.ready.capabilities` represent the **effective selected** STT/TTS path rather than only the currently registered backend implementation.

- [ ] Keep provider/vendor details outside Session Runtime and Interaction Controller.

- [x] Add configuration validation and tests for mixed combinations.

  Important combinations:
  - Synthetic STT + Synthetic TTS;
  - Browser STT + Browser TTS;
  - Browser STT + OpenAI TTS;
  - OpenAI/batch STT + Browser TTS;
  - hosted STT + hosted TTS.

### P1B — Browser STT first

Goal: inexpensive Chrome/Edge development/demo speech input that can stream recognition results to the backend while the user speaks for a long time.

- [x] Add a frontend ClientSpeechRecognizer port with Browser and fake adapters, plus one speech-transport orchestration service (native recognition objects stay out of Zustand).

- [ ] Wire Browser STT into voice mode: capability detection, continuous recognition, interim/final mapping, start/stop/cancel, and bounded unexpected restart.

- [ ] Keep the existing local AudioWorklet/VAD boundary path where useful for turn-taking/barge-in, but do not send PCM to backend STT when Browser STT is selected.

- [x] Add typed realtime commands for browser-recognized transcript evidence.

  Normalize browser results into the same application-level speech evidence consumed by the Interaction Controller:
  - `utteranceId`;
  - monotonically increasing partial `revision`;
  - partial text;
  - final text;
  - boundary/start/end evidence where available;
  - attachment/session identity and normal command sequencing.

  Do not persist speculative partial transcripts.

- [x] Admit client-transcript evidence through the existing Interaction Controller without opening an `ISpeechRecognizer` session.

- [x] Support long speech without waiting for a turn-by-turn submit.

  Frontend accumulator concatenates stable browser-final chunks plus current interim text, coalesces partials, emits exactly one application final per utterance, preserves final-before-ended, bounded restart, and drops stale partials on reconnect. Voice-mode wiring remains.

- [ ] Preserve interruption semantics.

  Browser STT evidence must be able to trigger:
  - Continue;
  - Ignore;
  - Interrupt/barge-in;
  - final user turn.

- [ ] Add deterministic frontend/backend tests using a fake Browser STT adapter; keep real browser speech manual/opt-in.

- [ ] Gracefully fall back to text or another configured STT provider when the browser API is unavailable.

### P1C — Browser TTS

Goal: inexpensive speech output without backend TTS cost while preserving the existing display/speech separation.

- [x] Add a frontend ClientSpeechSynthesizer port with Browser and fake adapters, voiceURI/name/language/default fallback, and the shared speech-transport service owning output.

- [ ] Queue, ACK, and immediately cancel Browser TTS (speechSynthesis playback, conservative offsets, preflight).

- [x] Add a server-to-client speech-text segment contract for Browser TTS.

  Do not synthesize directly from raw display Markdown.

  Source speech from:
  - explicit `ResponseEnvelope.SpeechText` when present;
  - otherwise the normal speakable text projection.

  Preserve:
  - `responseId`;
  - segment identity/order;
  - text coordinates;
  - cancellation identity;
  - stale-response rejection.

- [x] Keep conservative heard/spoken semantics on the server for `clientSpeech` playback (`consumedSamples=0`, completion gated on ACK).

  Browser `speechSynthesis` callbacks still vary by platform; only the later Browser TTS adapter may credit from trustworthy client events.

- [ ] Stop browser speech immediately on:
  - user barge-in;
  - explicit Stop;
  - mode change;
  - disconnect;
  - response supersession;
  - session end.

- [ ] Add fake Browser TTS tests for segment ordering, Stop, supersession, completion, and conservative heard offsets.

### P1D — Hosted speech completion and real-mode verification

Do this after Browser voice works so normal development remains cheap.

- [ ] Finish or replace the deferred realtime `OpenAiSpeechRecognizer` session with a real streaming implementation.

  If realtime transcription is not worth the complexity yet, keep `OpenAICompatibleBatchSpeechRecognizer` as the supported hosted STT fallback and document the capability difference.

- [ ] Wire `OpenAiSpeechSynthesizer` through the new explicit provider selection.

- [ ] Verify Real-mode voice end to end with at least one actual non-Synthetic configuration.

- [ ] Keep hosted-provider tests explicit opt-in.

- [ ] Keep Browser and Synthetic paths fully usable without OpenAI/OpenRouter speech credentials.

---

## P2 — Evolve assistant tools from the current bounded baseline

Already present:
- static `ToolCatalog`;
- role permission checks;
- `SessionToolExecutor`;
- `knowledge.retrieve`;
- `attachments.read`;
- `workspace.read`;
- `workspace.write`;
- `artifacts.create`;
- `artifacts.verify`;
- `sandbox.run`;
- bounded steps/time/output;
- session-scoped workspace/artifact/sandbox isolation.

Build on that instead of replacing it.

- [ ] Refactor the static tool path only as needed into clearer layers:

  - Registry: available typed tools and schemas.
  - Policy/Authorization: whether this agent/session/user may call them.
  - Executor: performs the operation.
  - Result/Artifact handling: bounded structured result.

  Do not introduce a large plugin framework before a second provider actually needs it.

- [ ] Add missing workspace ergonomics when useful.

  - `workspace.list`
  - `workspace.patch`
  - keep logical-path/session boundaries
  - keep `/workspace` as the writable model area

- [ ] Improve artifact tools when real workflows need them.

  - inspect metadata;
  - expose/share with user;
  - preserve provenance/hash;
  - materialize from workspace without copying arbitrary host paths.

- [ ] Add public web tools.

  - `web.search`
  - `web.fetch`
  - bounded response sizes/timeouts;
  - SSRF-safe URL/network policy;
  - explicit network policy separate from `sandbox.run`.

- [ ] Keep `sandbox.run` as the generic execution escape hatch.

  - no unrestricted host shell/process tool;
  - read-only root;
  - dropped capabilities;
  - bounded CPU/memory/PIDs/time/output;
  - network disabled by default.

- [ ] Add sandbox network policy only when a real workflow requires it.

  - `none`
  - restricted web
  - explicit host allowlist
  - never unrestricted by default

- [ ] Prefer typed domain actions over generic HTTP mutations.

  Examples:
  - `github.create_issue`
  - `calendar.create_event`
  - `support.update_ticket`

  Tool implementation owns credentials. Credentials never enter model context.

- [ ] Add approval policy for higher-risk actions.

  - automatic safe/read-only actions;
  - configurable ordinary writes;
  - explicit approval for sensitive/destructive operations.

- [ ] If generic `http.request` is eventually added, constrain it.

  - allowed hosts;
  - allowed methods;
  - request/response limits;
  - timeout;
  - named credential aliases;
  - policy/approval checks.

---

## P3 — Context compaction and memory

- [ ] Add LLM-based session compaction.

  - keep deterministic fallback;
  - preserve unresolved topics, decisions and references;
  - never replace durable raw history.

- [ ] Introduce structured session memory.

  - facts;
  - preferences;
  - goals;
  - decisions;
  - open loops/tasks;
  - provenance/source;
  - confidence/freshness.

- [ ] Add memory tools.

  - `memory.search`
  - `memory.get`
  - controlled `memory.write/update`
  - keep policy separate from storage.

- [ ] Add cross-session memory only after session memory works reliably.

  - opt-in/configurable scope;
  - agent/user ownership rules;
  - provenance and correction/deletion semantics.

- [ ] Do not require a vector database initially.

  Add embeddings/vector retrieval only when measured retrieval needs justify it.

---

## P4 — Events and configurable triggers

- [ ] Expand the event/trigger model beyond current idle/environment triggers.

- [ ] Define a generic trigger contract.

  - trigger type;
  - payload;
  - source;
  - timestamp;
  - dedupe/idempotency key;
  - expiry;
  - target agent/session/work item.

- [ ] Add useful trigger sources.

  - scheduled time;
  - recurring schedule;
  - webhook/external event;
  - application/domain event;
  - environment update;
  - session inactivity.

- [ ] Make allowed triggers configurable by agent definition/admin.

- [ ] Later expose safe trigger configuration to users.

- [ ] Add UI for trigger/scheduled-work visibility where appropriate.

---

## P5 — Durable background work

Introduce this only when work must outlive the active Session Runtime.

- [ ] Introduce durable `WorkItem` when a real accepted workflow requires it.

  - do not duplicate normal synchronous tool execution;
  - persist execution state/checkpoints;
  - support cancellation and idempotency;
  - guard against stale session epochs.

- [ ] Allow work to continue after session deactivation only when explicitly intended.

- [ ] Allow a paused/reopened session to reconnect to existing work without repeating the original user turn.

- [ ] Add background research when a concrete workflow needs it.

- [ ] Add scheduled tasks after P4 trigger semantics exist.

- [ ] Add long-running sandbox jobs only when useful.

- [ ] Stream/store progress as structured events rather than fake assistant messages.

- [ ] Deliver completed work back into the appropriate session as result/event/artifact.

- [ ] Revisit auto-pause vs manual-pause UX when background work exists.

  Current pause/deactivation semantics are sufficient for foreground conversation. Background work may require a clearer distinction between:
  - user-paused conversation;
  - runtime inactivity pause;
  - detached work still running;
  - fully stopped/cancelled work.

---

## P6 — Agent harness / admin mode

- [ ] Treat relevant effective agent settings as part of the agent harness/context.

  - agent may inspect non-secret effective configuration;
  - never expose secrets.

- [ ] Separate Admin mode from User mode.

- [ ] Admin mode:

  - inspect/modify harness workspace;
  - edit instructions/configuration;
  - edit tools/plugins configuration;
  - edit knowledge/assets;
  - run validation/tests;
  - preview behavior.

- [ ] For privileged admin changes, agent prepares an intent/payload first.

  The UI shows the proposed operation. The human confirms/approves it. Only then is the action executed through the normal policy/tool path.

  Do not let “admin agent” bypass authorization merely because it generated the change itself.

- [ ] User mode:

  - uses an immutable/pinned published agent version;
  - gets its own isolated session workspace;
  - cannot mutate the source harness.

- [ ] Define harness publishing lifecycle.

  - draft;
  - validate;
  - test/evaluate;
  - publish immutable version;
  - rollback/deprecate.

- [ ] Allow the admin agent to help build/refactor/improve its own harness only through the same bounded tool/policy system.

---

## P7 — Full harness/platform capabilities

- [ ] Consolidate the full harness model.

  - identity/instructions;
  - runtime configuration;
  - tools;
  - policies/permissions;
  - memory;
  - knowledge;
  - triggers;
  - workspace template;
  - validation/evals;
  - plugins/extensions.

- [ ] Add plugin/tool-provider extensibility only when a second real provider/integration justifies it.

  - MCP-like external tool providers may be adapters;
  - native Agent Core tools remain supported;
  - every plugin still passes through Agent Core policy/authorization.

- [ ] Add reusable harness validation.

  - schema validation;
  - missing tools/providers;
  - invalid permissions;
  - incompatible model capabilities;
  - unsafe configuration;
  - test scenarios/evals.

---

## P8 — Sandbox evolution

- [ ] Keep Docker sandbox as the current implementation while it meets requirements.

- [ ] Introduce `ISandboxProvider` only when a second implementation is actually needed.

- [ ] Evaluate OpenSandbox when requirements include:

  - remote execution;
  - stronger multi-tenant isolation;
  - sandbox pools;
  - faster provisioning;
  - distributed workers;
  - multiple runtime images.

- [ ] Keep the model-facing `sandbox.run` contract stable when changing providers.

- [ ] Consider Kubernetes only when deployment/scaling requirements justify it.

  Do not use Kubernetes merely to replace the current working sandbox.

---

## P9 — Multi-user/product infrastructure

- [ ] Authentication.

- [ ] User/admin authorization and tenancy.

- [ ] Per-user resource quotas and ownership.

- [ ] Secrets/credential management for external integrations.

- [ ] Audit history for privileged tools/actions.

- [ ] Public-hosting hardening.

- [ ] Horizontal/distributed Session Runtime only when single-process ownership becomes an actual constraint.

---

## Continuous quality work

- [ ] Keep Synthetic/offline tests as the default deterministic verification path.

- [ ] Add regression tests alongside every lifecycle, speech, tool, memory, trigger, and background-work change.

- [ ] Maintain browser/Playwright coverage for user-visible workflows.

- [ ] Keep hosted-provider tests explicit opt-in.

- [ ] Periodically run real OpenRouter/OpenAI/browser-speech smoke tests when credentials/browser support are available.

- [ ] Maintain observability for:

  - model calls;
  - speech input/output provider and capability selection;
  - tool calls;
  - sandbox execution;
  - trigger decisions;
  - background work;
  - policy denials;
  - resource limits.

- [ ] Keep docs synchronized with observed implementation.

  In particular, do not describe deferred provider adapters as fully wired runtime behavior.

---

## Implemented baseline

- [x] .NET 10 / C# 14 modular monolith with React/Vite TypeScript client.
- [x] SignalR + MessagePack realtime session transport.
- [x] Synthetic deterministic development/test profile.
- [x] OpenAI-compatible streaming text adapter / OpenRouter configuration.
- [x] Persistent multi-session catalog/history.
- [x] Session reopen and ended read-only history.
- [x] Session-owned attachments and later-turn attachment recall.
- [x] Markdown/rich response envelopes with separate display and optional speech text.
- [x] Artifact references and session-owned artifacts.
- [x] Repeated proactive initiative and deactivation/pause lifecycle.
- [x] Adaptive agent wait timing and inactivity behavior.
- [x] Client pending-send FIFO while the agent is responding.
- [x] Explicit Steer/interrupt behavior separate from queueing.
- [x] Stop semantics that do not accidentally dequeue queued user messages.
- [x] Versioned role environments.
- [x] Session-owned workspaces.
- [x] Bounded typed tool execution.
- [x] Knowledge, attachment, workspace, artifact and sandbox tools.
- [x] Docker-backed `sandbox.run` with isolation/resource limits.
- [x] Synthetic STT/TTS and full-duplex voice pipeline.
- [x] Interruption/barge-in and conservative spoken/heard handling.
- [x] Ant Design v6 conversation-first UI.
- [x] Impeccable skill integrated for bounded UI audit/polish/hardening.
