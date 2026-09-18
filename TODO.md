# TODO

Ordered by current dependency and product value.

Reviewed against `main` through parent `215de23ecefe65560b0b7a666783122d6c67408c` plus the P0-3 preferred-name freeze (`5ff60bd`) and this P1-0 docs-only contract pass on 2026-09-19. Exact Synthetic/Compose gates and the Chrome 153 `general-assistant` Voice checklist were re-run on the P0 freeze working tree before that freeze commit. Follow-on P1A/P1B/P1C remain planned until verified.

The current baseline already includes the MVP, post-MVP phases A–H, persistent multi-session chat, attachments, rich responses, repeated initiative/deactivation, versioned role environments, session workspaces/artifacts, bounded typed tools, Docker `sandbox.run`, Synthetic full-duplex voice, the P0 conversation-lifecycle/UI stabilization work, and most of P1 replaceable speech.

Browser STT/TTS is now a usable low-cost development path. Hosted OpenAI TTS and OpenAI-compatible batch STT are selectable. Realtime OpenAI STT remains deferred and unselectable. The `general-assistant` harness identity now exists specifically for neutral display/speech probes and has initiative disabled.

The current free-form rich-response parser plus runtime speech projection is acceptable for now. Do **not** start a structured-output migration only for architectural cleanliness. Move to a validated model response contract when the response/progress model is intentionally changed, so the migration happens once rather than being rewritten twice.

---

## P0 — Close the current stabilization tail, then freeze it again

This is a bounded verification/fix pass, not another voice redesign.

- [x] Run the complete key-free Synthetic/fake-browser gate on current HEAD, matching `.github/workflows/synthetic.yml`.

  Cover:
  - Domain tests;
  - Infrastructure tests;
  - Application tests;
  - API tests;
  - frontend unit tests;
  - frontend build;
  - Playwright Chromium suite;
  - Compose persistence/capability smoke where the workflow includes it.

  Observed on this freeze (parent `215de23`): Domain 23 passed; Infrastructure 101 passed / 9 skipped; Application 319 passed with `--blame-hang --blame-hang-timeout 5m`; API 114 passed; web 305 unit tests; production build; `CI=1` Playwright 27 passed; `scripts/compose-sqlite-volume.sh` passed. Live provider flags were 0. A broad `dotnet test AgentCore.sln` was not used.

- [x] Do one bounded live Chrome/Edge voice probe on current HEAD using `general-assistant`.

  Observed 2026-09-18 on Google Chrome 153 against a disposable Browser/Browser Synthetic host (run `run-20260918T180704-c332dd` P0-2 evidence). Distinct from fake-browser Playwright. Checklist items passed, including native STT loopback turns, Voice vs Mute, silence without `SpeechRecognitionRestartLimit`, Stop/reconnect/reopen without speech replay.

- [x] Fix the unexpected preferred name `"friend"` behavior.

  Provenance: agent JSON has no `friend`; frontend has no preferred-name fallback; Synthetic raw output does not invent the name. Originating layer is `SessionManager.EnsureLocalProfileAsync` seeding `preferredName=friend`, which `PromptContextBuilder` then emitted as trusted preferences. Create-time seed no longer includes a name; historical `friend` is stripped from trusted prompt preferences and from the durable local profile when Ensure runs. Absent name adds a generic prompt rule not to invent one. Regression: `PreferredNameProvenanceTests` plus Domain `Local_profile_treats_seeded_friend_as_absent`.

- [x] After the above is green, freeze conversation lifecycle and Browser voice again.

  Further P0/Browser-STT work should require a reproducible regression or a concrete product requirement. Do not continue speculative hardening.

---

## P1 — Conversation and session ergonomics

Authoritative contracts for this follow-on work are in [Technology Decisions](docs/10-technology-decisions.md#decision-bounded-history-and-durable-lastentrysequence) and [Implementation Plan](docs/18-implementation-plan.md#follow-on-p1-history-lifecycle-and-multilingual-speech-planned-until-verified). They are **planned until verified**. Observed P1 replaceable speech (Browser/hosted STT/TTS independence) stays closed. P1-0 records contracts only; later batches implement P1A–P1C.

### P1A — Lazy-load old chat history

The current durable history works, but loading the whole conversation does not scale. Evolve `IMemoryStore` (no repository framework). `LastEntrySequence` is durable and independent of the in-memory `Entries` window. Bounded saves retain older rows.

- [ ] Add paginated/cursor-based history reads.

  Backend restore/metadata (P1A-1) is implemented: `LoadMetadataAsync`, bounded `LoadAsync`, durable `LastEntrySequence`, older rows retained. Remaining:
  - newest page is enough to open/reopen a session;
  - older pages can be requested explicitly (`before`); existing `after` remains;
  - `before`+`after` is rejected;
  - newest/backward pages expose `hasOlder` plus the next older cursor;
  - stable ordering and no duplicate entries;
  - attachment, artifact, rich-block, `SpeechText`, interrupted/failed status, and entry identity survive pagination;
  - ended read-only sessions use the same history contract.

- [ ] Add frontend "Load earlier messages" behavior.

  Requirements:
  - preserve visual scroll position when older entries are prepended;
  - do not jump to the newest message merely because history was hydrated;
  - live entries arriving while older history loads remain correctly ordered;
  - session switch/reconnect cancels stale page requests;
  - one controller owns history merge/dedup.

- [ ] Add deterministic API/frontend/Playwright coverage for long histories.

### P1B — Session purpose, completion, and termination policy

Some sessions are open-ended conversations; others represent a finite task, examination, interview, onboarding flow, or another condition-bound interaction. Agent Core should own the generic lifecycle capability, while integrating applications keep ownership of domain-specific completion rules.

Core rule:

> **The model may propose lifecycle intent; Agent Core policy and/or the host application owns lifecycle authority.**

- [ ] Define a small generic session-purpose model.

  Support at least:
  - ongoing conversation;
  - goal/task-oriented session;
  - optional deadline / maximum duration;
  - optional completion/termination policy.

  Keep the goal description and any domain metadata generic/opaque to Agent Core. Do not encode application-specific predicates such as exam scoring or submission rules in the runtime.

- [ ] Distinguish lifecycle states that have different meaning.

  Introduce additive durable `lifecycleStatus` conceptually:
  - Active;
  - Paused/Deactivated;
  - Completed;
  - Expired;
  - Cancelled;
  - Ended.

  Keep protocol-v1 `status` (`created|attached|paused|ending|ended`) compatible during migration. Archive (`ArchivedAt`) stays orthogonal. Do not collapse every terminal outcome into one generic `Stopped` state. One Application `TransitionLifecycle` owns the graph.

- [ ] Add a model lifecycle intent such as `RequestComplete`.

  Keep it semantically distinct from `RequestDeactivate`:
  - `RequestDeactivate` = no useful action right now / pause runtime activity;
  - `RequestComplete` = the agent believes the configured session purpose has been fulfilled.

  A model request must not automatically terminate the session unless the configured policy explicitly grants that authority.

- [ ] Define configurable completion authority.

  Conceptually support:
  - host-authoritative completion;
  - agent may request completion but host/policy must approve;
  - agent-requested completion allowed for low-risk autonomous workflows;
  - user-requested completion/cancellation where permitted.

  Avoid hard-coding these exact policy names until the domain model is implemented.

- [ ] Support authoritative deterministic termination from Agent Core / host integration.

  Examples:
  - maximum duration/deadline reached;
  - host application reports task/submission completion;
  - administrator cancels the session.

  Agent Core owns the generic transition invariants:
  - cancel active generation;
  - stop STT/TTS/playback;
  - persist the terminal state/reason;
  - reject or constrain future input appropriately;
  - publish the corresponding lifecycle event.

- [ ] Keep domain-specific stop conditions outside Agent Core.

  Example examination integration:
  - examination platform owns the 60-minute exam rule and assignment/submission state;
  - deadline expiry can authoritatively transition the Agent Core session to `Expired`;
  - early submission can authoritatively transition it to `Completed`;
  - the examiner agent may emit `RequestComplete`, but in a high-stakes exam that request remains advisory unless the host validates it.

- [ ] Persist session purpose, lifecycle policy, completion state, reason, and relevant timestamps.

- [ ] Define reconnect/history behavior for terminal sessions.

  A completed/expired/cancelled session should remain inspectable without accidentally reopening normal conversation unless an explicit product flow allows it.

- [ ] Add minimal UI only after the domain contract is stable.

  Avoid building a generic workflow/rules editor here.

### P1C — Multilingual STT/TTS

Add this as provider-neutral language configuration, not provider-specific branching in Session Runtime.

- [ ] Define effective speech language/locale selection.

  Precedence: session override > agent conversation-language default > provider/default fallback. Validate BCP-47-like tags at the Application boundary. Persist the override without rewriting agent text-language settings. Expose the resolved locale on session readiness/capability data.

  Consider:
  - Browser STT `lang` uses the effective tag;
  - Browser TTS exact locale, then base language, then compatible configured/default voice, or fail Voice clearly;
  - hosted STT language hints where supported;
  - hosted TTS voice/language compatibility in adapters;
  - no Browser/OpenAI locale branches in SessionRuntime.

- [ ] Keep text mode usable even when a selected speech provider does not support the requested language.

- [ ] Do not require automatic language detection initially.

- [ ] Add deterministic tests for configuration/fallback; keep real multilingual voice checks opt-in/manual.

---

## P2 — Response, progress, and structured generation contract

This is the right place to revisit structured model output. Do not migrate the current response format before this slice unless a real bug forces it.

### P2A — Intermediate progress vs final assistant response

The system needs a first-class distinction between transient progress and durable conversational output before tools/background work become much richer.

- [ ] Define progress/event semantics.

  Examples:
  - thinking/preparing;
  - reading attachments;
  - running a tool;
  - waiting on an external operation;
  - producing a final answer.

- [ ] Do not model every progress update as a normal assistant chat message.

  Prefer structured transient events/state for operational progress. Persist only progress that is genuinely useful after reconnect/reopen.

- [ ] Define reconnect/cancellation/supersession behavior for progress.

- [ ] Define how progress appears in text mode and voice mode.

  Voice should not narrate every internal/tool progress update by default.

- [ ] Keep the final assistant response as the durable conversational result.

### P2B — Validated model response envelope

Replace free-form inline control markers when the response contract is next intentionally changed.

Target conceptual contract:

```text
displayText: string
speechText?: string
blocks?: [...]
```

The exact schema may evolve with P2A; do not freeze it prematurely.

- [ ] Introduce a provider-neutral validated generation contract.

  Runtime owns:
  - schema validation;
  - normalization;
  - safe fallback;
  - block validation;
  - speech segmentation/delivery;
  - persistence coordinates.

  The model owns:
  - what should be displayed;
  - optional intentionally different speech text;
  - requested rich blocks within allowed types.

- [ ] Preserve a fallback path for models/providers that do not reliably support structured output.

- [ ] Keep ordinary display prose as the default speech source when explicit `speechText` is absent.

- [ ] Remove `[[speech:]]` / related inline markers only after compatibility and persistence migration are covered.

- [ ] Do not add more `SpokenOutput` heuristics to solve model-intent problems that belong in the structured contract.

- [ ] Use `general-assistant` for neutral hosted display/speech probes.

### P2C — Preferred-name / personalization boundary

If user personalization grows beyond the current `"friend"` bug, define it deliberately instead of letting prompts infer personal facts.

- [ ] Distinguish known user profile data from conversational guesses.

- [x] Treat absent preferred name as absent, not as an invitation to invent one.

- [ ] Keep personalization provenance explicit if/when memory later supplies it.

---

## P3 — Evolve assistant tools from the current bounded baseline

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

## P4 — Context compaction and memory

### P4A — Session context compaction

- [ ] Add LLM-based session compaction.

  - keep deterministic fallback;
  - preserve unresolved topics, decisions, user constraints, goals, and references;
  - never replace durable raw history;
  - keep compaction versioned/replaceable.

- [ ] Make compaction aware of paginated history without requiring the client to load all old entries.

### P4B — Structured session memory

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

- [ ] Add correction/deletion semantics before cross-session memory.

### P4C — Cross-session memory

- [ ] Add cross-session memory only after session memory works reliably.

  - opt-in/configurable scope;
  - agent/user ownership rules;
  - provenance;
  - correction/deletion;
  - no silent promotion of uncertain conversational guesses into durable user facts.

- [ ] Do not require a vector database initially.

  Add embeddings/vector retrieval only when measured retrieval quality or scale justifies it.

---

## P5 — Events and configurable triggers

Build this after session purpose/goal semantics are defined.

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

- [ ] Keep trigger evaluation separate from arbitrary model initiative.

  A trigger creates eligible work/evidence; normal policy still decides what the agent may do.

---

## P6 — Durable background work

Introduce this only when accepted work must outlive the active Session Runtime.

Prerequisites:
- session goal/task semantics from P1;
- progress/result semantics from P2;
- trigger semantics from P5 for scheduled/external work.

- [ ] Introduce durable `WorkItem` when a real accepted workflow requires it.

  - do not duplicate normal synchronous tool execution;
  - persist execution state/checkpoints;
  - support cancellation and idempotency;
  - guard against stale session epochs;
  - preserve initiating user/session/agent provenance.

- [ ] Allow work to continue after session deactivation only when explicitly intended.

- [ ] Allow a paused/reopened session to reconnect to existing work without repeating the original user turn.

- [ ] Add background research when a concrete workflow needs it.

- [ ] Add scheduled tasks after P5 trigger semantics exist.

- [ ] Add long-running sandbox jobs only when useful.

- [ ] Stream/store progress as structured events, not fake assistant messages.

- [ ] Deliver completed work back into the appropriate session as result/event/artifact.

- [ ] Revisit auto-pause vs manual-pause UX when background work exists.

  Distinguish:
  - user-paused conversation;
  - runtime inactivity pause;
  - detached work still running;
  - completed goal;
  - fully stopped/cancelled work.

---

## P7 — Agent harness / admin mode

The repository already has versioned role environments and a shared development/documentation harness. Productize agent-harness editing only after the core runtime contracts above are stable.

- [ ] Treat relevant effective agent settings as part of the agent harness/context.

  - agent may inspect non-secret effective configuration;
  - never expose secrets.

- [ ] Separate Admin mode from User mode.

### Admin mode

- [ ] Allow bounded inspection/modification of:

  - harness workspace;
  - instructions/configuration;
  - tools/integration configuration;
  - knowledge/assets;
  - validation/tests;
  - behavior previews.

- [ ] For privileged admin changes, have the agent prepare an intent/payload first.

  The UI shows the proposed operation. The human confirms/approves it. Only then execute it through the normal policy/tool path.

  Do not let an "admin agent" bypass authorization merely because it generated the change itself.

### User mode

- [ ] Use an immutable/pinned published agent version.

- [ ] Give every session its own isolated runtime workspace.

- [ ] Do not allow user sessions to mutate the source harness.

### Publishing lifecycle

- [ ] Define:

  - draft;
  - validate;
  - test/evaluate;
  - publish immutable version;
  - rollback/deprecate.

- [ ] Allow the admin agent to help improve its own harness only through the same bounded tool/policy system.

---

## P8 — Full harness/platform capabilities and integration extensibility

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
  - integrations/extensions.

- [ ] Add plugin/tool-provider extensibility only when a second real provider/integration justifies it.

  - MCP-like external providers may be adapters;
  - native Agent Core tools remain supported;
  - every provider still passes through Agent Core policy/authorization;
  - provider credentials remain outside model context.

- [ ] Add reusable harness validation.

  - schema validation;
  - missing tools/providers;
  - invalid permissions;
  - incompatible model/provider capabilities;
  - unsafe configuration;
  - test scenarios/evals.

- [ ] Keep tool-provider extensibility separate from model-provider extensibility.

  Avoid one generic abstraction that hides materially different policy/security boundaries.

---

## P9 — Sandbox evolution

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

## P10 — Multi-user/product infrastructure

Do this when the product moves beyond single-owner/local development.

- [ ] Authentication.

- [ ] User/admin authorization and tenancy.

- [ ] Per-user resource quotas and ownership.

- [ ] Secrets/credential management for external integrations.

- [ ] Audit history for privileged tools/actions.

- [ ] Public-hosting hardening.

- [ ] Horizontal/distributed Session Runtime only when single-process ownership becomes an actual constraint.

---

## Deferred / optional provider work

These are useful but should not block the product roadmap above.

### Hosted voice verification

- [ ] Run **HOSTED-04**: one actual non-Synthetic end-to-end voice smoke with an explicitly selected hosted configuration.

  Keep it opt-in and credential-gated.

### Realtime hosted STT

- [ ] Finish or replace the deferred realtime `OpenAiSpeechRecognizer` only when there is a concrete need for hosted streaming STT.

  Current alternatives are already valid:
  - Browser STT for inexpensive interactive development/demo;
  - OpenAI-compatible batch STT for supported hosted recognition;
  - Synthetic STT for deterministic tests.

  Do not implement realtime OpenAI STT merely to make every conceptual adapter selectable.

### Native speech-to-speech / realtime reasoning

- [ ] Revisit only if latency/quality measurements show the composed STT → text model → TTS pipeline is insufficient.

  Keep the composed provider-neutral pipeline as the canonical architecture.

---

## Continuous quality work

- [ ] Keep Synthetic/offline tests as the default deterministic verification path.

- [ ] Add regression tests alongside every lifecycle, speech, tool, memory, trigger, and background-work change.

- [ ] Maintain browser/Playwright coverage for user-visible workflows.

- [ ] Keep hosted-provider tests explicit opt-in.

- [ ] Periodically run real OpenRouter/OpenAI/browser-speech smoke tests when credentials/browser support are available.

- [ ] Maintain observability for:

  - model calls;
  - response/progress lifecycle;
  - speech input/output provider and capability selection;
  - tool calls;
  - sandbox execution;
  - trigger decisions;
  - background work;
  - policy denials;
  - resource limits.

- [ ] Keep docs synchronized with observed implementation.

  In particular:
  - do not describe deferred provider adapters as fully wired runtime behavior;
  - do not document a structured-output contract until it is actually implemented;
  - update handoff reports when a frozen baseline materially changes.

- [ ] Keep TODO focused on active/future work.

  Move historical implementation detail into docs/handoff reports instead of continuously growing completed checklist sections here.

---

## Implemented baseline

Keep this as a compact orientation section, not a second roadmap.

- [x] .NET 10 / C# 14 modular monolith with React/Vite TypeScript client.
- [x] SignalR + MessagePack realtime session transport.
- [x] Synthetic deterministic development/test profile.
- [x] OpenAI-compatible streaming text adapter / OpenRouter configuration.
- [x] Persistent multi-session catalog/history.
- [x] Session reopen and ended read-only history.
- [x] Session-owned attachments and later-turn attachment recall.
- [x] Markdown/rich response envelopes with separate display and optional/persisted speech projection.
- [x] Artifact references and session-owned artifacts.
- [x] Repeated proactive initiative and deactivation/pause lifecycle.
- [x] Adaptive agent wait timing and inactivity behavior.
- [x] Client pending-send FIFO while the agent is responding.
- [x] Explicit Steer/interrupt behavior separate from queueing.
- [x] Stop semantics that do not accidentally dequeue queued user messages.
- [x] Versioned role environments.
- [x] Session-owned workspaces.
- [x] Bounded typed tool execution.
- [x] Knowledge, attachment, workspace, artifact, and sandbox tools.
- [x] Docker-backed `sandbox.run` with isolation/resource limits.
- [x] Synthetic STT/TTS and full-duplex server-audio voice pipeline.
- [x] Independent STT/TTS selection and capability reporting.
- [x] Browser STT and Browser TTS client transports with fake deterministic adapters.
- [x] Selectable OpenAI TTS and OpenAI-compatible batch STT.
- [x] Browser-STT half-duplex hold during agent output, restart stitching, endpointing, durable finals, and reconnect recovery.
- [x] Full conversational speech projection with persisted derived `SpeechText` when spoken coordinates differ from display.
- [x] Separate Voice mode and Mute controls.
- [x] `general-assistant` harness identity for neutral display/speech probes with initiative disabled.
- [x] Silent Browser STT listening no longer falsely exhausts the recognition restart limit (`7fe5b166`).
- [x] Interruption/barge-in and conservative spoken/heard handling.
- [x] Ant Design v6 conversation-first UI.
- [x] Impeccable skill integrated for bounded UI audit/polish/hardening.
- [x] Shared `develop` and `document` composition skills for agent-assisted repository work.
