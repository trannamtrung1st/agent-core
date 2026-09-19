# TODO

Ordered by current dependency and product value.

Reviewed against `main` at `a9b724d45913c344d45caa415d114e1f98b7c716` (`a9b724d`, 2026-09-19).

Current roadmap:

1. restore `main` to a fully green Synthetic + Compose baseline;
2. implement **P2A — first-class progress semantics**;
3. implement **P2B — validated model response envelope**;
4. evolve tools and external integrations;
5. add context compaction and memory;
6. add configurable triggers;
7. add durable background work;
8. productize the agent harness/admin lifecycle;
9. evolve sandbox and multi-user infrastructure only when requirements justify it.

P1A/P1B/P1C remain **frozen** on `dceaccbad9a4db8908af147b5353805a2b1af288` (`dceaccb`). Do not reopen P1 without a reproducible regression or a concrete new product requirement.

P2D session model selection is implemented and remains closed.

P2A/P2B have **not started**. The current implementation still uses the free-form `ResponseEnvelopeParser` / `[[speech:...]]` compatibility path, and `PromptContextBuilder.VoiceModeOutputGuidance` explicitly marks that mechanism as temporary until P2B.

The recent response/voice work is now part of the baseline:

- speech/display are runtime response capabilities, not agent-persona instructions;
- provider reasoning has its own `ModelReasoningDelta` channel;
- reasoning must never become assistant display text, speech, TTS input, progress, or conversation history;
- Real/OpenRouter reasoning configuration uses the native reasoning object and requests hidden reasoning where configured;
- Voice display publication waits for speech resolution;
- runtime-authored generic spoken lead-ins have been removed;
- compatibility display→speech derivation is intentionally conservative around code, tables, dumps, and substantial technical content;
- persisted/public `SpeechText` is available when spoken output meaningfully differs from display;
- `general-assistant` remains a neutral harness identity;
- the Real DeepSeek V4.1 Flash reasoning-channel probe is documented in `docs/reports/reasoning-channel-probe.md`.

Do not continue adding marker-specific speech heuristics merely to improve architecture. P2B is the intended replacement.

---

## Maintainer notes

Always keep this section even when there is no active work.

- [ ] Add proprietary license to the project.

- [ ] Add more general-assistant tools and trusted user/session context where useful.

  Examples:
  - timezone;
  - preferred language;
  - locale;
  - explicit user preferences.

  Do not infer durable personal facts from conversational guesses. Coordinate durable personalization with P2C/P4 rather than growing ad-hoc prompt fields.

- [ ] Background responses / continuing work after leaving a session are tracked under P6.

  Do not change deactivation semantics independently just to keep responses running in the background.

- [x] Session model selection and inference controls — completed in P2D.

- [x] Persist/publicly expose `speechText` when it meaningfully differs from display.

- [x] Keep provider reasoning separate from public assistant output.

- [x] Keep Model/Reasoning selection in the composer only; paused and terminal sessions do not show editable model controls.

---

# P0 — Restore a clean current baseline

This is a bounded stabilization tail, not another voice redesign.

Current HEAD `a9b724d` is almost green.

Latest Synthetic run on this HEAD:

- Domain: **64 passed**;
- Infrastructure: **133 passed / 12 skipped**;
- Application: **420 passed** with hang detection;
- API: **136 passed**;
- frontend unit tests: **369 passed** across 48 files;
- frontend production build: **passed**;
- Compose owner-capability / SQLite-volume smoke: **passed**;
- Playwright: **34 passed / 1 failed**.

The remaining failure is:

```text
web/e2e/text-conversation.spec.ts

Expected connection status:
Starting voice…

Observed:
Listening…
```

The test currently attempts to exercise pending Voice after sending `Please hold the line`, but the response can finish before the Voice click/assertion, allowing Voice to transition directly to active listening.

## P0A — Close the remaining deterministic-test gap

- [x] Fix the Docker/web regression-fixture build boundary.

  Completed through `62ecf1e` / `6d51117`.

  The frontend regression fixture is now loaded through the test helper without widening normal Vite filesystem access or globally enabling Node typings.

- [x] Align rich-envelope Playwright assertions with mode-dependent speech labels.

  Completed in `86a4df0`.

  Text-delivered secondary speech uses `Speech text`; `Spoken` is reserved for Voice delivery.

- [x] Stabilize the stale workspace-write cancellation regression.

  Completed in `a9b724d`.

  The test now waits on the actual gated workspace operation before deactivation instead of relying on a less-direct runtime-state observation.

- [ ] Make the pending-Voice Playwright scenario deterministic.

  Current failing scenario:

  ```text
  synthetic text conversation, pending voice, and disconnect cleanup
  ```

  The test should not require observing a transient `Starting voice…` state unless it has deterministically established the condition that keeps Voice pending.

  Preferred fix:

  - explicitly keep the current assistant response active before clicking Voice;
  - then assert the pending Voice behavior;
  - release/finish the response only after the required pending assertions.

  Alternatively, if the scenario is intended only to verify successful Voice activation rather than pending-mode semantics, assert the stable resulting state instead.

  Do **not** slow down production behavior or artificially preserve `Starting voice…` merely to make Playwright catch it.

- [ ] Re-run the complete key-free `.github/workflows/synthetic.yml` gate after the above fix.

  Required:

  - Domain;
  - Infrastructure;
  - Application;
  - API;
  - frontend unit tests;
  - frontend production build;
  - Playwright Chromium;
  - Compose persistence/capability smoke.

- [ ] Record the green HEAD here and freeze this stabilization tail.

  After that point, further Browser-STT/Voice changes require either:

  - a reproducible product regression; or
  - a concrete product requirement.

Do not start another speculative Voice-hardening pass.

---

# Frozen completed work

## P1 — Conversation and session ergonomics

**Frozen:** `dceaccbad9a4db8908af147b5353805a2b1af288` (`dceaccb`).

Authoritative contracts remain in:

- `docs/10-technology-decisions.md`;
- `docs/18-implementation-plan.md`.

Completed:

- bounded/paginated durable chat history;
- newest / `before` / `after` history reads;
- bounded runtime restore;
- frontend “Load earlier messages” with scroll preservation;
- stable history merge/dedup;
- attachment/artifact/speech/status preservation across pagination;
- generic session purpose model;
- ongoing vs goal/task sessions;
- optional deadline / maximum duration;
- explicit semantic lifecycle status;
- model `RequestComplete` distinct from deactivation;
- configurable completion authority;
- host/system authoritative lifecycle transition;
- application-specific completion predicates kept outside Agent Core;
- persisted lifecycle reason/provenance/timestamps;
- read-only terminal-session history;
- multilingual speech locale;
- independent conversation language vs speech locale;
- session speech-locale override;
- Browser and hosted speech adapters consuming the effective locale;
- deterministic fallback behavior when a selected speech provider cannot support the requested locale;
- real French Chrome Browser-STT/TTS smoke.

Do not redesign P1 as part of P2.

---

## P2D — Session model selection and inference controls

Completed.

Current contract includes:

- trusted backend `IModelCatalog`;
- safe public model descriptors;
- concrete durable `SessionModelSelection`;
- trusted catalog key → provider alias / concrete model ID mapping;
- selection precedence:
  - explicit host/user;
  - optional agent default/constraint;
  - system/operator default;
- session-aware `ILanguageModelResolver`;
- `ModelPurpose`:
  - `Conversation`;
  - `Initiative`;
  - `CompletionEvaluation`;
- request/session-scoped reasoning effort;
- catalog validation of allowed effort values;
- `GET /api/v2/models`;
- model selection during session creation;
- live session model mutation;
- persist-before-use semantics;
- `SessionBusy` rejection during active generation;
- per-assistant-turn model provenance;
- first-party model/reasoning UI;
- session isolation;
- no browser-supplied provider aliases, base URLs, credentials, or arbitrary provider JSON.

Shipped Real development/demo default:

```text
deepseek-v41-flash
→ deepseek/deepseek-v4.1-flash
→ reasoning effort: medium
```

This is a development/demo default, not a production model recommendation.

Current UI rule:

- Model/Reasoning lives in the active composer;
- paused sessions show Resume instead;
- terminal sessions are read-only;
- paused/terminal headers do not duplicate Model controls.

Keep P2D closed.

---

# P2 — Response, progress, and structured generation contract

This is the next architectural phase.

Implement **P2A before P2B** so progress semantics exist before the final generation contract is frozen.

---

## P2A — First-class progress vs final assistant output

The runtime already exposes coarse activity such as attachment processing and tool execution, but the product still lacks a clean semantic distinction between:

- transient progress;
- controller/runtime activity;
- provider reasoning;
- durable assistant conversation output.

Define that distinction before tools/background work become substantially richer.

### Progress contract

- [ ] Introduce a small provider/runtime-neutral progress model.

Conceptually:

```text
responseId
operationId?
kind
state
message?
```

Possible `kind` values:

- preparing;
- readingAttachments;
- runningTool;
- waitingExternal;
- finalizing.

Possible `state` values:

- started;
- updated;
- completed;
- failed.

Do not over-design the enum before real consumers need more states.

- [ ] Add a first-class realtime progress event.

Conceptually:

```text
agent.progress
```

Exact protocol naming may change during implementation.

- [ ] Tie response progress to `responseId`.

Nested tool/external operations may additionally have an `operationId`.

### Reasoning boundary

- [ ] Keep `ModelReasoningDelta` completely separate from progress.

Provider reasoning is not user-visible progress.

Never transform provider reasoning into:

- progress messages;
- display text;
- speech;
- TTS;
- history;
- artifacts.

Reasoning-presence telemetry may be recorded without logging its content.

### Persistence

- [ ] Make ordinary response progress transient by default.

Do not create durable assistant messages such as:

- “Reading attachment…”;
- “Running tool…”;
- “Preparing response…”.

- [ ] Persist the final assistant result as the conversational record.

- [ ] Clear transient progress on:

  - successful response completion;
  - interruption;
  - cancellation;
  - provider failure;
  - session switch;
  - disconnect;
  - stale response/epoch supersession.

- [ ] Do not add durable/replayable progress yet.

Current live responses do not resume across reconnect, so ordinary response progress should follow the same lifecycle.

Durable progress belongs to P6 `WorkItem`, where work can genuinely outlive the active Session Runtime.

### Existing controller state

- [ ] Keep current coarse `session.state.changed.outputState` initially.

Existing states such as:

- processing attachments;
- running tools;
- generating response;

remain useful controller/session state.

P2A progress is an additive semantic/presentation layer, not a requirement to immediately delete every existing state field.

### UI

- [ ] Render progress as transient status, not normal assistant chat bubbles.

- [ ] Keep one understandable active progress surface per response.

Do not produce a growing transcript of operational status messages.

- [ ] Replace/update progress rather than stacking repetitive messages.

- [ ] Remove active progress when final output takes over.

- [ ] Do not narrate progress through TTS by default.

Voice users should hear the assistant response, not internal/tool activity.

### Verification

- [ ] Application tests for:

  - progress start/update/complete;
  - attachment processing;
  - tool execution;
  - interruption;
  - cancellation;
  - provider failure;
  - stale response;
  - stale runtime epoch;
  - reasoning never becoming progress.

- [ ] Realtime protocol tests.

- [ ] frontend tests for progress ownership and cleanup.

- [ ] one Playwright progress → final-response workflow.

- [ ] reconnect/session-switch regression coverage.

- [ ] update protocol/observability docs.

### P2A stop condition

P2A is done when:

- progress is a first-class semantic event;
- progress is not fake conversation history;
- progress clears reliably on every terminal path;
- Voice does not narrate operational progress;
- provider reasoning stays private/internal;
- final assistant output remains the durable conversational result.

Do not add durable background-work execution in this phase.

---

## P2B — Validated model response envelope

After P2A, replace the current marker-based response composition with a validated provider-neutral generation contract.

Current compatibility debt includes:

- `ResponseEnvelopeParser`;
- `[[speech:...]]`;
- inline rich block markers;
- marker-aware streaming/display gating;
- compatibility speech derivation heuristics.

Speech/display are **Agent Core response capabilities**, not Agent Definition persona behavior.

Agent definitions must not contain:

- TTS instructions;
- marker syntax;
- transport instructions;
- UI-rendering syntax;
- provider-specific structured-output JSON.

### Canonical semantic response

- [ ] Define one validated provider-neutral semantic response.

Preferred conceptual shape:

```text
displayText: string

speech:
  mode: same | custom | none
  text?: string

blocks?: [...]
```

Exact schema may evolve, but preserve the semantic distinction.

#### `displayText`

- required;
- primary visible conversational response.

#### `speech.mode = same`

The spoken answer is semantically the normal conversational display response.

The runtime may perform deterministic formatting cleanup appropriate for speech, but must not invent a semantic summary.

#### `speech.mode = custom`

`speech.text` is required.

It becomes the authoritative TTS source.

Use this when spoken wording intentionally differs from the visual response.

#### `speech.mode = none`

The response is intentionally visual-only.

No TTS should occur, and this is not a speech/provider error.

This distinction prevents the runtime from confusing:

- “same as display”;
- “custom speech”;
- “intentionally no speech”;
- malformed/missing structured output.

#### `blocks`

Optional richer visual output.

Examples:

- Markdown/detail blocks;
- attachment references;
- artifact references;
- future typed rich content.

Blocks never enter TTS automatically.

### Provider-neutral generation events

- [ ] Keep provider wire format inside Infrastructure.

Application/SessionRuntime should consume semantic events, not OpenRouter/OpenAI-specific JSON fields.

Target event concepts may include:

```text
display delta
speech projection
block update
reasoning delta
tool call
completion
failure
```

- [ ] Keep `ModelReasoningDelta` as a separate internal semantic event.

- [ ] Never concatenate reasoning into `ModelTextDelta`.

- [ ] Never persist provider reasoning in `ConversationEntry`.

### Native structured generation

- [ ] For providers/models with reliable structured-output support, request the validated schema directly.

- [ ] Use trusted model-catalog capabilities to determine structured-output availability.

- [ ] Validate completed semantic output before durable publication.

- [ ] Centralize malformed-response normalization/failure handling.

Do not scatter malformed JSON/schema recovery throughout SessionRuntime.

### Compatibility path

- [ ] Keep one bounded compatibility adapter for providers/models that cannot reliably generate the validated structure.

- [ ] Temporarily let that adapter understand the existing marker format.

- [ ] Convert compatibility output into the same canonical semantic response used by native structured providers.

- [ ] Keep marker syntax out of Domain/Application APIs where possible.

The compatibility parser should be an edge adapter, not permanent architecture.

### Streaming

- [ ] Preserve useful display streaming where safely possible.

- [ ] Never stream raw structured JSON to the user.

- [ ] Never treat incomplete marker/JSON fragments as speech.

- [ ] Never expose provider reasoning while waiting for validated display content.

- [ ] Do not require every semantic field to stream in the first P2B slice.

Correct completion-time validation is preferable to unsafe pseudo-streaming.

### Voice semantics

- [ ] `speech.mode = custom`:

  `speech.text` is the authoritative TTS input.

- [ ] `speech.mode = same`:

  use the completed conversational display projection.

- [ ] `speech.mode = none`:

  do not synthesize speech.

- [ ] Never feed these automatically into TTS:

  - code;
  - tables;
  - attachment payloads;
  - artifact payloads;
  - file dumps;
  - provider reasoning;
  - arbitrary rich blocks.

- [ ] Keep speech segmentation/playback timing in the runtime speech layer.

- [ ] Keep conversation language independent from response projection.

### Persistence

- [ ] Persist the validated semantic envelope atomically with the assistant entry.

Preserve:

- display text;
- meaningful custom speech text;
- blocks;
- display/speech delivery coordinates;
- finish reason;
- model provenance.

- [ ] Do not persist duplicate custom speech when speech is equivalent to display.

- [ ] Preserve backward compatibility with old marker-generated conversations.

Do not destructively rewrite historical conversation rows merely for schema cleanliness.

### UI

Keep the current presentation semantics:

- `displayText` always renders normally;
- absent/equivalent speech does not create duplicated content;
- meaningfully different speech appears as secondary speech content;
- Text-delivered custom speech can use the current `Speech text` presentation;
- Voice-delivered custom speech can use the current `Spoken` presentation;
- intentionally silent output does not render an empty speech section;
- no fuzzy semantic deduplication.

### Remove compatibility debt

Only after native structured + fallback generation paths are verified:

- [ ] remove `[[speech:...]]` from normal model prompting;

- [ ] remove marker parsing from normal runtime flow;

- [ ] remove marker-specific streaming/order machinery that is no longer needed;

- [ ] remove compatibility-only `SpokenOutput` heuristics;

- [ ] retain only deterministic formatting/safety projection required by the canonical response contract;

- [ ] keep `general-assistant` transport-agnostic.

### Verification

- [ ] Domain tests for semantic response validation.

- [ ] Infrastructure tests for:

  - native structured generation;
  - fallback compatibility parsing;
  - malformed response;
  - reasoning fields;
  - tool calls;
  - length limit;
  - content filtering;
  - provider failure.

- [ ] Application tests proving reasoning cannot enter:

  - display;
  - speech;
  - TTS;
  - progress;
  - persistence/history.

- [ ] Voice tests for:

  - `same`;
  - `custom`;
  - `none`.

- [ ] persistence/history/reopen coverage.

- [ ] frontend rich-response tests.

- [ ] Playwright structured-response workflow.

- [ ] bounded opt-in Real OpenRouter probe using `general-assistant`.

### P2B stop condition

P2B is done when:

- Application consumes a provider-neutral validated semantic response;
- structured-capable providers do not depend on inline speech markers;
- compatibility providers map into the exact same semantic contract;
- reasoning is fully isolated;
- rich visual content cannot leak into TTS;
- old persisted conversations still render correctly;
- marker-specific runtime architecture can be removed.

---

## P2C — Personalization boundary

Do not let personalization grow through accidental prompt inference.

- [x] Treat absent preferred name as absent.

- [ ] Distinguish trusted profile data from conversational guesses.

- [ ] Give durable personalization explicit provenance.

Possible sources:

- user-set;
- host-set;
- application profile;
- memory-derived;
- inferred/temporary.

Do not treat inferred/temporary values as trusted durable user facts.

- [ ] Treat timezone, language, preferred name, locale, and similar context as explicit trusted fields when available.

- [ ] Coordinate durable personalization with P4 structured memory rather than creating a second unrelated memory system.

---

# P3 — Evolve assistant tools from the current bounded baseline

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
- session-scoped workspace/artifact/sandbox isolation;
- runtime-epoch protection against stale tool/workspace work.

Build on this instead of replacing it.

## Tool architecture

- [ ] Refactor the static tool path only where real growth requires clearer layers:

  - **Registry** — available typed tools and schemas;
  - **Policy/Authorization** — whether this agent/session/user may call them;
  - **Executor** — performs the action;
  - **Result/Artifact layer** — bounded structured result.

Do not introduce a large plugin framework before a second real external tool provider/integration requires it.

## Workspace ergonomics

- [ ] Add missing workspace operations when workflows need them:

  - `workspace.list`;
  - `workspace.patch`.

Keep:

- logical-path boundaries;
- session isolation;
- `/workspace` as the writable model area;
- stale-runtime cancellation guarantees.

## Artifact tools

- [ ] Improve artifact workflows when required:

  - inspect metadata;
  - expose/share with user;
  - preserve provenance/hash;
  - materialize from workspace;
  - never accept arbitrary host paths.

## Public web tools

- [ ] Add bounded public web tooling:

  - `web.search`;
  - `web.fetch`.

Requirements:

- SSRF-safe URL handling;
- bounded response size;
- bounded timeout;
- explicit network policy;
- no credential leakage;
- separate policy from `sandbox.run`.

## Sandbox

- [ ] Keep `sandbox.run` as the generic execution escape hatch.

Maintain:

- no unrestricted host shell/process tool;
- read-only root;
- dropped capabilities;
- bounded CPU;
- bounded memory;
- bounded PIDs;
- bounded execution time;
- bounded output;
- network disabled by default.

- [ ] Add sandbox network policy only when a concrete workflow requires it.

Possible modes:

- `none`;
- restricted public web;
- explicit host allowlist.

Never unrestricted by default.

## Typed external actions

- [ ] Prefer typed domain actions over generic HTTP mutations.

Examples:

```text
github.create_issue
calendar.create_event
support.update_ticket
```

Tool implementation owns credentials.

Credentials never enter model context.

## Approval policy

- [ ] Add action approval policy when write-capable integrations arrive.

Conceptually:

- automatic safe/read-only actions;
- configurable ordinary writes;
- explicit approval for sensitive/destructive actions.

## Generic HTTP

- [ ] Add generic `http.request` only if typed tools cannot reasonably cover real workflows.

If added, require:

- allowed hosts;
- allowed methods;
- request/response limits;
- timeout;
- named credential aliases;
- authorization/approval checks.

---

# P4 — Context compaction and memory

## P4A — Session context compaction

Current prompt history is intentionally bounded. Add semantic compaction only when long-running sessions need more context than the current window can safely retain.

- [ ] Add LLM-based session compaction.

Preserve:

- unresolved topics;
- decisions;
- user constraints;
- goals;
- relevant references;
- important open loops.

Requirements:

- deterministic fallback;
- compaction never replaces raw durable history;
- versioned/replaceable compaction format;
- clear source range/provenance.

- [ ] Make compaction operate over durable paginated history without requiring the browser to load old messages.

- [ ] Use the model-resolution architecture rather than hard-coding a provider/model.

Add another `ModelPurpose` only if compaction actually requires different model-selection policy.

---

## P4B — Structured session memory

- [ ] Introduce structured session memory.

Suggested concepts:

- facts;
- preferences;
- goals;
- decisions;
- open loops/tasks;
- provenance/source;
- confidence;
- freshness.

- [ ] Add memory tools:

  - `memory.search`;
  - `memory.get`;
  - controlled `memory.write/update`.

- [ ] Keep memory policy separate from persistence technology.

- [ ] Add correction/deletion semantics before cross-session memory.

- [ ] Do not silently promote uncertain conversational guesses into durable user facts.

---

## P4C — Cross-session memory

- [ ] Add cross-session memory only after session-scoped memory works reliably.

Requirements:

- explicit/configurable scope;
- user/agent ownership rules;
- provenance;
- correction/deletion;
- promotion rules.

- [ ] Do not require a vector database initially.

Use embeddings/vector retrieval only when measured quality/scale demonstrates a need.

---

# P5 — Events and configurable triggers

P1 already provides the generic session-purpose/lifecycle foundation.

Build trigger semantics before scheduled/durable autonomous work.

- [ ] Expand the event model beyond the current idle/environment triggers.

- [ ] Define a generic trigger contract.

Suggested fields:

- trigger type;
- payload;
- source;
- timestamp;
- dedupe/idempotency key;
- expiry;
- target session/agent/work item.

- [ ] Add useful trigger sources when required:

  - scheduled time;
  - recurring schedule;
  - webhook/external event;
  - application/domain event;
  - environment update;
  - inactivity.

- [ ] Make allowed triggers configurable by agent definition/admin.

- [ ] Later expose safe trigger configuration to end users.

- [ ] Add UI for scheduled/triggered work where useful.

- [ ] Keep trigger eligibility separate from model initiative.

A trigger supplies evidence/opportunity.

Normal policy still determines what the agent is allowed to do.

---

# P6 — Durable background work

Introduce this only when accepted work must outlive the active Session Runtime.

Prerequisites:

- P1 purpose/lifecycle semantics;
- P2 progress/result semantics;
- P5 trigger semantics for scheduled/external work.

## WorkItem

- [ ] Introduce durable `WorkItem` when a real accepted workflow requires it.

Requirements:

- do not duplicate ordinary synchronous tool execution;
- persist execution state/checkpoints;
- cancellation;
- idempotency;
- stale session/runtime protection;
- initiating user/session/agent provenance;
- bounded retries where appropriate.

- [ ] Allow work to continue after session deactivation only when explicitly configured/intended.

- [ ] Allow a paused/reopened session to reconnect to existing work without replaying the original user turn.

- [ ] Add background research only when a concrete workflow requires it.

- [ ] Add scheduled tasks after P5 exists.

- [ ] Add long-running sandbox jobs only when needed.

## Progress/results

- [ ] Reuse P2 progress semantics for live work.

- [ ] Persist durable WorkItem progress/checkpoints separately from ordinary assistant chat history.

- [ ] Deliver completed results to the originating session as the appropriate combination of:

  - result event;
  - assistant response;
  - artifact.

## Session UX

- [ ] Revisit pause/deactivation UX after background work exists.

Distinguish:

- user-paused conversation;
- runtime inactivity pause;
- session disconnected;
- detached work still running;
- completed goal;
- cancelled work;
- fully stopped session.

---

# P7 — Agent harness / admin mode

The repository already has:

- versioned role environments;
- isolated session workspaces;
- `develop` and `document` agent-work composition skills;
- Impeccable UI skill integration.

Productize harness editing only after the core runtime contracts above are stable.

## Effective harness context

- [ ] Allow the agent/admin surface to inspect relevant non-secret effective configuration.

Never expose:

- API keys;
- raw credentials;
- secret environment variables.

## Admin vs User mode

- [ ] Separate Admin mode from User mode.

### Admin mode

- [ ] Allow bounded inspection/modification of:

  - harness workspace;
  - instructions/configuration;
  - tool/integration configuration;
  - knowledge/assets;
  - validation/tests;
  - behavior previews.

- [ ] Require privileged changes to be represented as an explicit intent/payload.

Flow:

1. agent prepares proposed operation;
2. UI presents it;
3. human approves where required;
4. operation executes through normal policy/tool authorization.

An admin agent does not bypass policy because it generated the change itself.

### User mode

- [ ] Use an immutable/pinned published agent version.

- [x] Give every session its own isolated runtime workspace.

- [ ] Prevent user sessions from mutating the source harness.

## Publishing lifecycle

- [ ] Define:

  - draft;
  - validate;
  - test/evaluate;
  - publish immutable version;
  - rollback/deprecate.

- [ ] Allow an admin agent to improve its own harness only through the same bounded tool/policy system.

---

# P8 — Full harness/platform capabilities and integration extensibility

- [ ] Consolidate the eventual full harness model.

Possible components:

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

- [ ] Add external tool-provider/plugin extensibility only when a second real provider/integration justifies it.

MCP-like providers may be adapters.

Requirements:

- native Agent Core tools remain supported;
- every external provider still passes through Agent Core policy/authorization;
- provider credentials remain outside model context.

- [ ] Add reusable harness validation:

  - schema validation;
  - missing tool/provider references;
  - invalid permissions;
  - incompatible model/provider capabilities;
  - unsafe configuration;
  - evaluation/test scenarios.

- [ ] Keep model-provider extensibility separate from tool/integration-provider extensibility.

Do not create one generic abstraction that hides different security and lifecycle boundaries.

---

# P9 — Sandbox evolution

- [x] Docker is the current sandbox implementation.

- [ ] Keep Docker while it satisfies current requirements.

- [ ] Introduce `ISandboxProvider` only when a second implementation is genuinely required.

- [ ] Evaluate OpenSandbox when requirements include:

  - remote execution;
  - stronger multi-tenant isolation;
  - sandbox pools;
  - faster provisioning;
  - distributed workers;
  - multiple runtime images.

- [ ] Keep the model-facing `sandbox.run` contract stable while changing implementation providers.

- [ ] Consider Kubernetes only when deployment/scaling requirements justify it.

Do not adopt Kubernetes merely to replace a working Docker sandbox.

---

# P10 — Multi-user/product infrastructure

Do this when Agent Core moves beyond trusted single-owner/local development.

- [ ] Authentication.

- [ ] User/admin authorization and tenancy.

- [ ] Per-user resource ownership and quotas.

- [ ] Secure external-integration credential management.

- [ ] Audit history for privileged actions/tools.

- [ ] Public-hosting hardening.

- [ ] Separate host credentials/scopes for trusted host integrations where browser users must not possess host authority.

- [ ] Horizontal/distributed Session Runtime only when single-process ownership is an actual constraint.

---

# Deferred / optional provider work

These items should not block the product roadmap.

## Hosted voice verification

- [ ] Run **HOSTED-04**: one actual non-Synthetic end-to-end Voice smoke using an explicitly selected hosted configuration.

Keep it:

- opt-in;
- credential-gated;
- separate from normal CI.

---

## Realtime hosted STT

- [ ] Finish or replace the deferred realtime `OpenAiSpeechRecognizer` only when there is a concrete need for hosted streaming STT.

Current valid options already include:

- Browser STT for inexpensive interactive development/demo;
- OpenAI-compatible batch STT;
- Synthetic STT for deterministic testing.

Do not implement realtime OpenAI STT merely for conceptual adapter symmetry.

---

## Native speech-to-speech / realtime reasoning

- [ ] Revisit only if measured latency/quality demonstrates that:

```text
STT → text model → TTS
```

is insufficient.

Keep the composed provider-neutral pipeline as the canonical architecture until then.

---

# Continuous quality work

- [ ] Keep Synthetic/offline verification as the default deterministic path.

- [ ] Keep `main` green before beginning the next architectural phase.

- [ ] Add regressions alongside every lifecycle, response, speech, tool, memory, trigger, and background-work change.

- [ ] Maintain Playwright coverage for user-visible workflows.

- [ ] Make asynchronous/race-sensitive tests deterministic.

  Prefer explicit gates/events over timing assumptions.

  In particular, do not assert that a transient UI state must be observed unless the test has deterministically held the system in that state.

- [ ] Keep hosted-provider tests explicitly opt-in.

- [ ] Periodically run bounded Real OpenRouter/OpenAI/browser-speech checks when credentials/browser support are available.

- [ ] Maintain observability for:

  - model calls;
  - model/provider selection;
  - reasoning-field presence without reasoning-content logging;
  - response lifecycle;
  - progress lifecycle;
  - speech provider/capability selection;
  - speech projection/fallback reason;
  - tool calls;
  - sandbox execution;
  - trigger decisions;
  - background work;
  - policy denials;
  - resource limits.

- [ ] Keep docs synchronized with observed implementation.

In particular:

- do not describe deferred adapters as active runtime behavior;
- do not document P2A/P2B as implemented before they ship;
- keep provider wire details in Infrastructure/provider docs;
- keep transport/speech-marker syntax out of Agent Definitions;
- update freeze/handoff reports when a baseline materially changes.

- [ ] Keep TODO focused on current/future work.

Historical verification detail belongs in `docs/reports` rather than continuously expanding completed checklist sections here.

---

# Implemented baseline

Keep this compact. It is orientation, not another roadmap.

- [x] .NET 10 / C# 14 modular monolith.
- [x] React / Vite / TypeScript frontend.
- [x] Ant Design v6 conversation-first UI.
- [x] SignalR + MessagePack realtime transport.
- [x] Synthetic deterministic development/test profile.
- [x] OpenAI-compatible streaming language-model adapter.
- [x] OpenRouter configuration.
- [x] Fixed Real development/demo model default.
- [x] Trusted model catalog.
- [x] Durable per-session model/reasoning selection.
- [x] Per-turn assistant model provenance.
- [x] Separate provider reasoning channel.
- [x] Reasoning excluded from display/speech/history.
- [x] Persistent multi-session catalog/history.
- [x] Paginated durable history.
- [x] Bounded runtime history restore.
- [x] Session reopen and terminal read-only history.
- [x] Generic session purpose and lifecycle policy.
- [x] Host/user/agent completion-authority model.
- [x] Session-owned attachments and later-turn recall.
- [x] Existing rich-response envelope with display/speech/blocks.
- [x] Persisted/public meaningful `SpeechText`.
- [x] Artifact references and session-owned artifacts.
- [x] Repeated proactive initiative.
- [x] Deactivation/pause lifecycle.
- [x] Adaptive wait/inactivity behavior.
- [x] Client pending-send FIFO.
- [x] Explicit Steer/interrupt behavior.
- [x] Stop semantics that preserve queued user messages.
- [x] Versioned role environments.
- [x] Session-owned isolated workspaces.
- [x] Bounded typed tool execution.
- [x] Knowledge tools.
- [x] Attachment tools.
- [x] Workspace tools.
- [x] Artifact tools.
- [x] Docker-backed `sandbox.run`.
- [x] Sandbox isolation/resource limits.
- [x] Runtime-epoch protection against stale workspace/tool writes.
- [x] Synthetic STT/TTS.
- [x] Full-duplex server-audio Voice pipeline.
- [x] Browser STT.
- [x] Browser TTS.
- [x] Selectable OpenAI TTS.
- [x] OpenAI-compatible batch STT.
- [x] Independent STT/TTS provider selection.
- [x] Provider-neutral speech-locale resolution.
- [x] Browser-STT hold/restart/endpointing/reconnect handling.
- [x] Voice display gating around speech resolution.
- [x] Conservative compatibility display→speech projection.
- [x] Separate Voice and Mute controls.
- [x] Mic mute/unmute styling remains stable while STT is temporarily held during agent output.
- [x] `general-assistant` neutral harness identity with initiative disabled.
- [x] Interruption/barge-in and heard/received tracking.
- [x] Model/Reasoning controls only on active composer.
- [x] Impeccable UI skill integration.
- [x] Shared `develop` and `document` composition skills.