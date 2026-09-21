# TODO

Ordered by current dependency and product value.

Reviewed against `main` on 2026-09-21.

Current roadmap:

1. **P2A — first-class progress semantics** is **observed** and frozen;
2. **P2B — validated model response envelope** is **observed/frozen** on `e0e8a55` (2026-09-20 key-free Synthetic + Compose gate);
3. **P2E — multimodal/image input usability and capability handling** — **observed/frozen** (2026-09-21 key-free Synthetic + Compose gate on freeze HEAD);
4. **P3 — evolve tools and external integrations**, starting with historical multimodal attachment re-inspection;
5. **P4 — add context compaction and memory** after P3;
6. add configurable triggers;
7. add durable background work;
8. productize the agent harness/admin lifecycle;
9. evolve sandbox and multi-user infrastructure only when requirements justify it.

P1A/P1B/P1C remain **frozen** on `dceaccbad9a4db8908af147b5353805a2b1af288` (`dceaccb`). Do not reopen P1 without a reproducible regression or a concrete new product requirement.

P2D session model selection is implemented and remains closed.

P2A first-class progress is **observed** after the 2026-09-20 key-free Synthetic plus Compose gate (git HEAD `5effbb5e0942b2176c970c3a6f1b79fbaa985f8d` plus the P2A working tree). P2B validated model response envelope is **frozen** on `e0e8a55b111b190b63dfe5a2a53d0c59d0a06a59` (`e0e8a55`) with CI/Synthetic + Compose green on that HEAD (workflow run `35496178496`). Do not reopen P2B without a reproducible regression. **P2E** multimodal image-input capability closure is **observed/frozen** after the 2026-09-21 key-free Synthetic + Compose gate on the P2E freeze HEAD (implementation commits `0eeb27c`–`a502266` plus documentation on that freeze HEAD). Do not reopen P2E without a reproducible regression. Optional Real vision (`gpt-4o-mini-2024-07-18`) and structured-response probes were **skipped** (no `OPENROUTER_API_KEY` / `OPENAI_API_KEY` in the process). Do not treat those probes as verified Real-provider behavior. **P2C** personalization boundary is **observed/frozen** after the 2026-09-21 key-free Synthetic + Compose gate on the P2C freeze HEAD (implementation commits `4ae1095`–`c636b28` plus documentation on that freeze HEAD). Do not reopen P2C without a reproducible regression. **P2-Final** mandatory whole-output review **complete** (2026-09-21; TDP production evidence). Closure repair on `47d6ff6` (Real-catalog fallback, post-commit profile notification, preferredName trim). **P2 closed/frozen** on `47d6ff65142d2d454c4aa3101b0f43a38f01389a` (`47d6ff6`) with CI/Synthetic + Compose green (workflow run `35552740853`). Optional Real structured-response and vision probes **skipped/unverified**. Do not reopen P2 without a reproducible regression. Active roadmap: **P3**; **P4** follows per plan. P6 implementation has not started.

Current-turn image input already has substantial implementation and must not be redesigned from scratch:

- image attachments are accepted and processed;
- decoded image size is bounded;
- metadata is stripped before model use;
- Application already has provider-neutral `ModelContentPart`, `ModelTextContent`, and `ModelImageContent`;
- current-turn image attachments can become `ModelImageContent`;
- the OpenAI-compatible adapter maps image content to multimodal `image_url` content;
- `ModelCapabilities` and `ModelDescriptor` already expose Vision capability;
- `/api/v2/models` already exposes model vision capability;
- the Real catalog currently marks:
  - `deepseek-v41-flash` as `vision: false`;
  - `gpt-4o-mini-2024-07-18` as `vision: true`;
  - `openrouter-free` as `vision: false`;
- `PngVisionRuntimeTests` already proves that a PNG attachment reaches a vision-enabled OpenAI-compatible model request.

P2E closed that **capability/UX/correctness** gap without a new multimodal architecture (truthful sanitized MIME/`attachment-processors/2`, layered Vision admission, Synthetic `scripted-vision` UX).

The recent response/voice work is now part of the baseline:

- speech/display are runtime response capabilities, not agent-persona instructions;
- provider reasoning has its own `ModelReasoningDelta` channel;
- reasoning must never become assistant display text, speech, TTS input, progress, or conversation history;
- Real/OpenRouter reasoning configuration uses the native reasoning object and requests hidden reasoning where configured;
- Voice display publication waits for speech resolution;
- runtime-authored generic spoken lead-ins have been removed;
- compatibility display→speech derivation is intentionally conservative around code, tables, dumps, and substantial technical content;
- envelope may persist same-mode `speech.text` for playback/heard coordinates when spoken output meaningfully differs from display;
- public/history `speechText` exposes custom semantic speech only;
- `general-assistant` remains a neutral harness identity;
- the Real DeepSeek V4.1 Flash reasoning-channel probe is documented in `docs/reports/reasoning-channel-probe.md`.

Do not continue adding marker-specific speech heuristics merely to improve architecture. P2B is the intended replacement.

---

## Maintainer notes

Always keep this section even when there is no active work.

- [x] Add proprietary license to the project.

- [ ] Add more general-assistant tools and trusted user/session context where useful.

  Examples:
  - timezone;
  - preferred language;
  - locale;
  - explicit user preferences.

  Do not infer durable personal facts from conversational guesses. Coordinate durable personalization with P2C/P4 rather than growing ad-hoc prompt fields.

- [x] Close current image-input usability under P2E.

  Do not create a second multimodal abstraction. Reuse the existing:

  - attachment store/processor;
  - `ModelContentPart`;
  - `ModelImageContent`;
  - model `Vision` capability;
  - OpenAI-compatible multimodal mapping.

- [ ] Background responses / continuing work after leaving a session are tracked under P6.

  Do not change deactivation semantics independently just to keep responses running in the background.

- [x] Session model selection and inference controls — completed in P2D.

- [x] Persist/publicly expose `speechText` when it meaningfully differs from display.

- [x] Keep provider reasoning separate from public assistant output.

- [x] Keep Model/Reasoning selection in the composer only; paused and terminal sessions do not show editable model controls.

---

# P0 — Green baseline (closed)

**P0A closed and frozen** on `0d118034bf12b2500912f653a79c8fdff6893b0c` (`0d11803`, 2026-09-19). [Synthetic run 35447234418](https://github.com/trannamtrung1st/agent-core/actions/runs/35447234418) completed successfully on that exact `main` commit. Both the Synthetic offline gates and the Synthetic Compose smoke succeeded.

The preceding run on `a9b724d` had one pending-Voice Playwright failure (34 passed / 1 failed). The test clicked Voice before it had established that the held text response was active, so direct activation could correctly show `Listening…` instead of the pending `Starting voice…` state. The production voice lifecycle was not changed to satisfy the test.

## P0A — Closed deterministic-test gap

- [x] Fix the Docker/web regression-fixture build boundary.

  Completed through `62ecf1e` / `6d51117`. The frontend regression fixture loads through the test helper without widening normal Vite filesystem access or globally enabling Node typings.

- [x] Align rich-envelope Playwright assertions with mode-dependent speech labels.

  Completed in `86a4df0`. Text-delivered secondary speech uses `Speech text`; `Spoken` is reserved for Voice delivery.

- [x] Stabilize the stale workspace-write cancellation regression.

  Completed in `a9b724d`. The test waits on the actual gated workspace operation before deactivation instead of relying on a runtime-state observation.

- [x] Make the pending-Voice Playwright scenario deterministic.

  Completed in `0d11803`. The test waits for the prior attachment response to finish, then observes the held response's first assistant chunk and Stop control before clicking Voice. It verifies `Starting voice…`, Cancel, zero audio frames before Voice applies, and disconnect cleanup. The focused scenario passed 10 consecutive local runs; the complete 35-test local Playwright suite also passed before the CI run.

- [x] Re-run the complete key-free `.github/workflows/synthetic.yml` gate.

  [Run 35447234418](https://github.com/trannamtrung1st/agent-core/actions/runs/35447234418) reports success for Domain, Infrastructure, Application, API, frontend unit tests and production build, Playwright Chromium, and the Compose owner-capability / SQLite-volume smoke.

- [x] Record the green HEAD and freeze this stabilization tail.

  The verified commit is `0d118034bf12b2500912f653a79c8fdff6893b0c`. Further Browser-STT/Voice changes require a reproducible product regression or a concrete product requirement. Do not start another speculative Voice-hardening pass.

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
- model capabilities including:
  - tools;
  - vision;
  - structured output;
  - reasoning;
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
→ vision: false
```

The Real catalog also contains:

```text
gpt-4o-mini-2024-07-18
→ openai/gpt-4o-mini-2024-07-18
→ vision: true
```

This is a development/demo catalog, not a production model recommendation.

The fact that the default model is non-vision does **not** mean Agent Core lacks an image-input abstraction. Current-turn vision plumbing already exists. Product/capability closure belongs to P2E.

Current UI rule:

- Model/Reasoning lives in the active composer;
- paused sessions show Resume instead;
- terminal sessions are read-only;
- paused/terminal headers do not duplicate Model controls.

Keep P2D closed. Do not reopen model-selection architecture merely to finish image UX.

---

# P2 — Response, progress, structured generation, and multimodal input contract

This is the next architectural phase.

Preferred execution order:

```text
P2A → P2B → P2E
```

P2C personalization is largely independent and can follow when durable personalization becomes useful.

P2D is already completed and frozen. P2A, P2B, **P2E**, and **P2C** are observed/frozen. **P2 closed/frozen** on `47d6ff6` after mandatory whole-output review and closure repair on that HEAD.

P2E reused the existing multimodal foundations rather than creating a second request format.

---

## P2A — First-class progress vs final assistant output

Observed. Transient `agent.progress` is distinct from controller `outputState`, provider reasoning, and durable assistant output.

### Progress contract

- [x] Introduce a small provider/runtime-neutral progress model.

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

- [x] Add a first-class realtime progress event.

Conceptually:

```text
agent.progress
```

Wire name is `agent.progress` (protocol v1).

- [x] Tie response progress to `responseId`.

Nested tool/external operations may additionally have an `operationId`.

### Reasoning boundary

- [x] Keep `ModelReasoningDelta` completely separate from progress.

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

- [x] Make ordinary response progress transient by default.

Do not create durable assistant messages such as:

- “Reading attachment…”;
- “Running tool…”;
- “Preparing response…”.

- [x] Persist the final assistant result as the conversational record.

- [x] Clear transient progress on:

  - successful response completion;
  - interruption;
  - cancellation;
  - provider failure;
  - session switch;
  - disconnect;
  - stale response/epoch supersession.

- [x] Do not add durable/replayable progress yet.

Current live responses do not resume across reconnect, so ordinary response progress should follow the same lifecycle.

Durable progress belongs to P6 `WorkItem`, where work can genuinely outlive the active Session Runtime.

### Existing controller state

- [x] Keep current coarse `session.state.changed.outputState` initially.

Existing states such as:

- processing attachments;
- running tools;
- generating response;

remain useful controller/session state.

P2A progress is an additive semantic/presentation layer, not a requirement to immediately delete every existing state field.

### UI

- [x] Render progress as transient status, not normal assistant chat bubbles.

- [x] Keep one understandable active progress surface per response.

Do not produce a growing transcript of operational status messages.

- [x] Replace/update progress rather than stacking repetitive messages.

- [x] Remove active progress when final output takes over.

- [x] Do not narrate progress through TTS by default.

Voice users should hear the assistant response, not internal/tool activity.

### Verification

- [x] Application tests for:

  - progress start/update/complete;
  - attachment processing;
  - tool execution;
  - interruption;
  - cancellation;
  - provider failure;
  - stale response;
  - stale runtime epoch;
  - reasoning never becoming progress.

- [x] Realtime protocol tests.

- [x] frontend tests for progress ownership and cleanup.

- [x] one Playwright progress → final-response workflow.

- [x] reconnect/session-switch regression coverage.

- [x] update protocol/observability docs.

### P2A stop condition

P2A is **observed**. The stop condition holds: first-class semantic event; not fake conversation history; clears on terminal paths; Voice does not narrate operational progress; provider reasoning stays internal; the final assistant result remains the durable record.

Do not add durable background-work execution in this phase.

---

## P2B — Validated model response envelope

**Observed/frozen** on `e0e8a55` (2026-09-20). Do not reopen without a reproducible regression. Application consumes a provider-neutral validated semantic response (`same` / `custom` / `none`). Structured-capable catalog models (Synthetic Scripted Beta) do not depend on inline speech markers. Compatibility models (Scripted Alpha) map into the same contract inside Infrastructure. Marker syntax stays out of Domain/Application prompting and SessionRuntime parsing. Agent definitions remain free of TTS, marker, transport, and UI-rendering instructions.

After P2A, the shipped generation contract retired this compatibility debt from Domain/Application:

- free-form `ResponseEnvelopeParser` in SessionRuntime;
- `[[speech:...]]` in normal prompting;
- inline rich block markers in Application parsing;
- marker-aware streaming/display gating in the runtime;
- compatibility speech derivation as the normal TTS path.

Infrastructure still maps unstructured catalog models through a bounded marker compatibility parser into the same semantic events.

Speech/display are **Agent Core response capabilities**, not Agent Definition persona behavior.

Agent definitions must not contain:

- TTS instructions;
- marker syntax;
- transport instructions;
- UI-rendering syntax;
- provider-specific structured-output JSON.

### Canonical semantic response

- [x] Define one validated provider-neutral semantic response.

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

- [x] Keep provider wire format inside Infrastructure.

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

- [x] Keep `ModelReasoningDelta` as a separate internal semantic event.

- [x] Never concatenate reasoning into `ModelTextDelta`.

- [x] Never persist provider reasoning in `ConversationEntry`.

### Native structured generation

- [x] For providers/models with reliable structured-output support, request the validated schema directly.

- [x] Use trusted model-catalog capabilities to determine structured-output availability.

- [x] Validate completed semantic output before durable publication.

- [x] Centralize malformed-response normalization/failure handling.

Do not scatter malformed JSON/schema recovery throughout SessionRuntime.

### Compatibility path

- [x] Keep one bounded compatibility adapter for providers/models that cannot reliably generate the validated structure.

- [x] Temporarily let that adapter understand the existing marker format.

- [x] Convert compatibility output into the same canonical semantic response used by native structured providers.

- [x] Keep marker syntax out of Domain/Application APIs where possible.

The compatibility parser should be an edge adapter, not permanent architecture.

### Streaming

- [x] Preserve useful display streaming where safely possible.

- [x] Never stream raw structured JSON to the user.

- [x] Never treat incomplete marker/JSON fragments as speech.

- [x] Never expose provider reasoning while waiting for validated display content.

- [x] Do not require every semantic field to stream in the first P2B slice.

Correct completion-time validation is preferable to unsafe pseudo-streaming.

### Voice semantics

- [x] `speech.mode = custom`:

  `speech.text` is the authoritative TTS input.

- [x] `speech.mode = same`:

  use the completed conversational display projection.

- [x] `speech.mode = none`:

  do not synthesize speech.

- [x] Never feed these automatically into TTS:

  - code;
  - tables;
  - attachment payloads;
  - artifact payloads;
  - file dumps;
  - provider reasoning;
  - arbitrary rich blocks.

- [x] Keep speech segmentation/playback timing in the runtime speech layer.

- [x] Keep conversation language independent from response projection.

### Persistence

- [x] Persist the validated semantic envelope atomically with the assistant entry.

Preserve:

- display text;
- meaningful custom speech text;
- blocks;
- display/speech delivery coordinates;
- finish reason;
- model provenance.

- [x] Do not persist duplicate custom speech when speech is equivalent to display.

- [x] Preserve backward compatibility with old marker-generated conversations.

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

- [x] remove `[[speech:...]]` from normal model prompting;

- [x] remove marker parsing from normal runtime flow;

- [x] remove marker-specific streaming/order machinery that is no longer needed;

- [x] remove compatibility-only `SpokenOutput` heuristics;

- [x] retain only deterministic formatting/safety projection required by the canonical response contract;

- [x] keep `general-assistant` transport-agnostic.

### Verification

- [x] Domain tests for semantic response validation.

- [x] Infrastructure tests for:

  - native structured generation;
  - fallback compatibility parsing;
  - malformed response;
  - reasoning fields;
  - tool calls;
  - length limit;
  - content filtering;
  - provider failure.

- [x] Application tests proving reasoning cannot enter:

  - display;
  - speech;
  - TTS;
  - progress;
  - persistence/history.

- [x] Voice tests for:

  - `same`;
  - `custom`;
  - `none`.

- [x] persistence/history/reopen coverage.

- [x] frontend rich-response tests.

- [x] Playwright structured-response workflow.

- [x] bounded opt-in Real OpenRouter probe using `general-assistant` (skipped this run: no process keys; not claimed verified).

### P2B stop condition

P2B stop condition is **met** — frozen on `e0e8a55` with key-free Synthetic + Compose green (workflow run `35496178496`).

### P2E stop condition

P2E stop condition is **met** — observed/frozen after the 2026-09-21 key-free Synthetic + Compose gate on the P2E freeze HEAD. Optional Real vision probe **skipped** (credentials unavailable). Historical image re-inspection was explicitly deferred to **P3A** and does not reopen P2E. **P2C** stop condition is **met** — observed/frozen after the 2026-09-21 key-free gate on the P2C freeze HEAD. **P2 closed/frozen** on `47d6ff6` after mandatory whole-output review, closure repair, and proposal §23 gate on that HEAD (workflow run `35552740853`; counts in TDP production evidence). Optional Real probes **skipped/unverified** (credentials unavailable). Do not reopen P2 without a reproducible regression. P3 has not started; its first recommended slice is historical multimodal attachment re-inspection. **P4** follows P3. P6 has not started.

---

## P2E — Multimodal image-input capability closure

The architecture already contains the core image-input path.

Do **not** introduce another image-specific language-model interface.

Existing foundations:

- [x] session-owned image attachments;
- [x] image signature/content-type classification;
- [x] bounded decoded-pixel validation;
- [x] image metadata stripping;
- [x] provider-neutral `ModelContentPart`;
- [x] `ModelTextContent`;
- [x] `ModelImageContent`;
- [x] current-turn image → model content-part projection;
- [x] OpenAI-compatible `image_url` mapping;
- [x] model-level Vision capability;
- [x] catalog/API Vision capability;
- [x] a Real catalog entry that supports vision;
- [x] deterministic application coverage proving PNG image bytes reach a vision-enabled request.

The current product gap is mostly that capability is not presented/enforced early enough.

### Capability-aware admission

- [x] Detect whether the pending/current user turn contains one or more image attachments before starting model generation.

- [x] Resolve the selected session model and validate its Vision capability before generation.

For an image turn with a non-vision model:

- do not send the model request;
- do not silently drop the image;
- do not pretend the model saw it;
- do not automatically switch the user's selected model;
- return a clear non-fatal capability error.

Example product meaning:

```text
This model cannot read images. Choose a vision-capable model to send this attachment.
```

Keep provider/model wire details out of the error.

- [x] Keep attachment upload/storage independent from model capability.

It is valid to upload/stage an image before choosing a model.

Capability validation belongs to the point where the attachment is about to become model input.

### Model picker / composer UX

- [x] Surface Vision capability in the model picker.

At minimum, users must be able to distinguish:

- vision-capable;
- non-vision.

Do not turn the picker into a dense provider-debug surface.

- [x] When the composer contains image attachments and the current model lacks Vision:

  - show an understandable incompatibility state;
  - prevent sending until resolved;
  - keep the draft and attachments intact;
  - allow the user to select a vision-capable model.

- [x] Re-evaluate compatibility immediately when:

  - model selection changes;
  - an image is added;
  - an image is removed;
  - a pending new-chat model choice changes.

- [x] Do not automatically choose GPT-4o mini or any other model solely because an image was attached.

Model choice remains explicit unless a future agent/session policy defines capability-based routing.

### Image encoding correctness

- [x] Make sanitized image bytes and declared model-input content type agree.

Current processing may transcode formats such as GIF/WebP through a PNG encoder while retaining the original attachment content type.

The model input must never claim:

```text
image/webp
```

while carrying PNG bytes, or equivalent mismatches.

Choose one clear rule:

1. preserve the original encoding when it remains safe and supported; or
2. normalize sanitized model-input images to a canonical format such as PNG/JPEG and update the `ModelImageContent.ContentType` accordingly.

Prefer deterministic normalization over format-specific complexity.

- [x] Add explicit tests for:

  - PNG;
  - JPEG;
  - WebP;
  - GIF, if accepted as an input type.

If GIF animation is not intentionally supported, document that only the sanitized/static projection is used.

### Provider boundary

- [x] Keep `ModelImageContent` provider-neutral.

Provider adapters may map it to:

- OpenAI/OpenRouter `image_url`;
- another provider's native image block;
- a future upload/file reference mechanism.

Application must not depend on those formats.

- [x] Keep image bytes out of prompts/logging/telemetry.

Telemetry may record bounded metadata such as:

- image present;
- content type;
- byte-size bucket;
- capability accepted/rejected.

Do not log base64 payloads or raw image bytes.

### Model capability integrity

- [x] Treat catalog capabilities as trusted backend configuration.

The browser may display capability metadata, but it does not declare whether a model supports Vision.

- [x] Keep runtime/provider capability validation as a second defensive boundary.

Even after application preflight, the adapter should continue rejecting image parts if its resolved model does not support Vision.

- [ ] Add startup/configuration validation where useful so contradictory catalog/provider capability declarations fail clearly.

Avoid configuration where:

```text
catalog.vision = true
provider.vision = false
```

silently behaves unpredictably.

### Current-turn versus historical image access

P2E closed current-turn image inspection only. Historical image re-inspection was explicitly deferred to **P3A — Historical multimodal attachment re-inspection** and does not reopen or block the frozen P2E closure.

The deferred work must continue to avoid raw/base64 image data in ordinary textual tool results and must not automatically replay every historical image on every turn.

### Supported scope

First closure target:

```text
user uploads image
→ attachment is validated/sanitized
→ compatible vision model is selected
→ current user turn contains ModelImageContent
→ provider adapter sends multimodal request
→ assistant can answer about the image
```

Do not add OCR as the primary image architecture.

A multimodal model should receive the image natively when Vision is available.

OCR/document-image extraction may be added later as a separate degraded/tool path if a concrete workflow requires it.

### Verification

- [x] Keep/expand the existing PNG vision runtime test.

- [x] Infrastructure tests for correct multimodal provider mapping.

- [x] Tests that sanitized bytes and MIME type match for every accepted image format.

- [x] Application tests for:

  - vision model + image → accepted;
  - non-vision model + image → rejected before provider request;
  - text-only turn + non-vision model → unaffected;
  - mixed text + image;
  - multiple images;
  - image attachment with no textual user message;
  - cancellation/interruption during image processing;
  - stale runtime protection.

- [x] API/model-catalog tests preserving Vision capability.

- [x] frontend tests for:

  - Vision indicator;
  - incompatible image/model state;
  - model switch resolving the state;
  - removing the image resolving the state;
  - draft/attachment preservation after capability rejection.

- [x] one deterministic Playwright flow for:

```text
attach image
→ non-vision model is visibly incompatible
→ choose vision-capable model
→ send succeeds
```

Synthetic CI may use a deterministic vision-capable fixture/catalog entry rather than external inference.

- [x] bounded opt-in Real OpenRouter image probe using a known vision-capable catalog model (skipped: no process keys; not claimed verified).

Do not make real vision inference part of default CI.

### P2E stop condition

P2E is **done** — observed/frozen after the 2026-09-21 key-free Synthetic + Compose gate on the P2E freeze HEAD (see [Implementation Plan](docs/18-implementation-plan.md#p2e--multimodal-image-input-capability-closure-observed)).

---

## P2C — Personalization boundary

P2C is **done** — observed/frozen after the 2026-09-21 key-free Synthetic + Compose gate on the P2C freeze HEAD (see [Implementation Plan](docs/18-implementation-plan.md#p2c--personalization-boundary-observed)).

Do not let personalization grow through accidental prompt inference.

- [x] Treat absent preferred name as absent.

- [x] Distinguish trusted profile data from conversational guesses (explicit provenance; no model memory-write path).

- [x] Give durable personalization explicit provenance (`userSet`, `hostSet`, `applicationProfile`; `memoryDerived` deferred to P4).

- [x] Treat timezone, language, preferred name, locale, and similar context as explicit trusted allowlisted fields when available.

- [x] Owner `GET|PATCH /api/v2/profile` with optimistic revision and server-side source stamping.

- [x] Live runtime mailbox refresh on subsequent turns without mutating in-flight responses or session revision.

- [x] Coordinate durable personalization with P4 structured memory rather than creating a second unrelated memory system (no inferred durable writes in P2C).

---

## P2-Final — Reconcile and close P2

- [x] README, TODO, and canonical docs agree P2A/P2B/P2D frozen and P2E/P2C observed/frozen; owner `GET|PATCH /api/v2/profile` and typed profile persistence are documented.

- [x] Proposal §23 key-free gate (backend, web unit/build, Synthetic Playwright, Compose SQLite volume) re-verified during whole-output review (2026-09-21; exact HEAD in TDP). Optional Real structured-response and vision probes **skipped** (credentials unavailable).

- [x] Mandatory whole-output review complete; **P2 closed/frozen** on `47d6ff6` (closure repair on same HEAD; CI/Synthetic + Compose workflow run `35552740853`). Do not reopen P2 without a reproducible regression. Roadmap focus: **P3** next; **P4** follows per proposal §26.

---

# P3 — Evolve assistant tools from the current bounded baseline

**P3 has not started.** The first recommended implementation slice is **P3A — Historical multimodal attachment re-inspection**. P4 follows after P3.

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

## P3A — Historical multimodal attachment re-inspection

- [ ] Let a model re-inspect a historical session image on demand through the existing durable `attachmentId`, session attachment manifest, `attachments.read`, attachment processor/store, and provider-neutral `ModelContentPart` / `ModelImageContent` path.
- [ ] Retrieve only explicitly selected session-owned images; do not automatically resend all historical images or duplicate image blobs into conversation history.
- [ ] Add the smallest provider-neutral typed non-text tool-result or continuation projection needed to rehydrate model-consumable image content. Keep raw/base64 image bytes out of ordinary JSON/text tool results.
- [ ] Require trusted model **Tools** capability for model-initiated `attachments.read` and **Vision** capability before historical image content reaches the provider. Never silently drop the image, pretend it was seen, or switch models automatically.
- [ ] Preserve existing text/PDF `attachments.read` behavior, session ownership and cross-session isolation, sanitized/canonical image bytes with truthful MIME, persisted/reopened session support, and runtime-epoch/cancellation/supersession fencing.

## Tool architecture evolution

- [ ] Refactor the static tool path only where real growth requires clearer layers:

  - **Registry** — available typed tools and schemas;
  - **Policy/Authorization** — whether this agent/session/user may call them;
  - **Executor** — performs the action;
  - **Result/Artifact layer** — bounded structured result.

Do not introduce a large plugin/tool framework before real integrations require it. P3A is the first concrete trigger for typed non-text tool results; further evolution remains driven by demonstrated workflows.

## Workspace ergonomics

- [ ] Add missing workspace operations when workflows need them:

  - `workspace.list`;
  - `workspace.patch`.

Keep:

- logical-path boundaries;
- session isolation;
- `/workspace` as the writable model area;
- stale-runtime cancellation guarantees.

## Artifact workflow improvements

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

## Sandbox and network-policy evolution

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

## Generic `http.request`

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

- [ ] Add regressions alongside every lifecycle, response, speech, multimodal-input, tool, memory, trigger, and background-work change.

- [ ] Maintain Playwright coverage for user-visible workflows.

- [ ] Make asynchronous/race-sensitive tests deterministic.

  Prefer explicit gates/events over timing assumptions.

  In particular, do not assert that a transient UI state must be observed unless the test has deterministically held the system in that state.

- [ ] Keep hosted-provider tests explicitly opt-in.

- [ ] Periodically run bounded Real OpenRouter/OpenAI/browser-speech checks when credentials/browser support are available.

- [ ] Maintain observability for:

  - model calls;
  - model/provider selection;
  - model capability selection/rejection;
  - image-input presence without image-content logging;
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
- do not document P2E or P2C as active; P2A, P2B, P2E, and P2C are observed/frozen (`e0e8a55` for P2B; P2E/P2C freeze HEADs in implementation plan); **P2 closed/frozen** on `47d6ff6` — do not reopen without a reproducible regression; active roadmap work is **P3** implementation/planning with **P4** next (no new P2 implementation);
- document the existing current-turn vision foundation accurately;
- distinguish vision-capable from non-vision catalog models;
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
- [x] Model capability metadata for tools/vision/structured-output/reasoning.
- [x] Per-turn assistant model provenance.
- [x] Separate provider reasoning channel.
- [x] Reasoning excluded from display/speech/history.
- [x] Persistent multi-session catalog/history.
- [x] Paginated durable history.
- [x] Bounded runtime history restore.
- [x] Session reopen and terminal read-only history.
- [x] Generic session purpose and lifecycle policy.
- [x] Host/user/agent completion-authority model.
- [x] Session-owned attachments and later-turn attachment references.
- [x] Image attachment validation and sanitization.
- [x] Provider-neutral text/image model content parts.
- [x] Current-turn image projection into multimodal model requests.
- [x] OpenAI-compatible image-content mapping.
- [x] Deterministic PNG→vision-request coverage.
- [x] User-facing capability-aware image/model admission (P2E).
- [ ] P3A historical multimodal attachment re-inspection beyond the original turn — first recommended P3 slice; P3 has not started.
- [x] Existing rich-response envelope with display/speech/blocks.
- [x] Internal same-mode `speech.text` when playback differs from display.
- [x] Public/history `speechText` custom-only (P2B).
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