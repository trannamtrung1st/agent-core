# TODO

Ordered by current dependency and product value.

Reviewed against `main` on **2026-09-22**.

Current repository HEAD reviewed:

```text
3e56a26cd3b764c57bbc022e939c7c4a027c224b
```

Hosted Synthetic workflow:

```text
35692184054 — green
```

Detailed historical verification belongs in `docs/reports`. Keep this file focused on current/future work, frozen architectural invariants, and enough baseline context to prevent accidental redesign.

---

# Current roadmap

1. **P0–P3 are closed/frozen.**
2. **P4A — session context compaction** is next.
3. **P4B — structured session memory.**
4. **P4C — cross-session memory.**
5. **P5 — events and configurable triggers.**
6. **P6 — durable background work.**
7. **P7 — agent harness / admin lifecycle.**
8. **P8 — harness/platform extensibility.**
9. **P9 — sandbox evolution when requirements justify it.**
10. **P10 — multi-user/product infrastructure when requirements justify it.**

Do not reopen a frozen phase without either:

- a reproducible regression; or
- a concrete new product requirement that belongs there rather than in a later phase.

---

# Current freeze state

## P1 — Conversation/session ergonomics

Frozen on:

```text
dceaccbad9a4db8908af147b5353805a2b1af288
```

Important frozen contracts include:

- bounded/paginated durable history;
- bounded runtime restore;
- session purpose and lifecycle;
- deadline / maximum-duration semantics;
- completion authority;
- terminal read-only history;
- conversation language separate from speech locale;
- replaceable speech providers;
- deterministic reconnect/pause behavior;
- explicit queue vs intentional steer/interruption semantics;
- accepted-send through response-start treated as an in-flight/busy turn;
- bounded detach grace with reattach continuity;
- durable terminal interruption-reason visibility.

Do not redesign P1 as part of memory work.

---

## P2 — Response, progress, multimodal input and personalization

P2 closed/frozen on:

```text
47d6ff65142d2d454c4aa3101b0f43a38f01389a
```

Important frozen contracts include:

- first-class transient response progress;
- provider reasoning isolated from user-visible output;
- validated display/speech response semantics;
- model selection and reasoning effort;
- model capability metadata;
- current-turn image input;
- capability-aware image/model admission;
- trusted explicit profile fields;
- no conversational guesses silently becoming trusted profile data.

Specific baselines:

```text
P2B response envelope: e0e8a55
P2 final closure:       47d6ff6
```

Startup validation of contradictory catalog/provider capability configuration was not required for P2E closure. Track that under continuous quality if independently configurable provider capabilities make the problem real.

---

## P3 — Tools and external integrations

P3 key-free freeze:

```text
4dbb9201de8028e7454b3be70a2e0730ac7c84f7
```

Verified hosted descendant:

```text
ac795b656e2e8ebd7d97f08d2d6ebc632d100cde
workflow 35682808408 — green
```

Post-freeze lifecycle/CI maintenance is complete through:

```text
3e56a26cd3b764c57bbc022e939c7c4a027c224b
workflow 35692184054 — green
```

This maintenance includes:

- explicit client queue behavior instead of accidental active-turn interruption;
- distinct `userSteer` interruption semantics;
- persisted terminal interruption reasons;
- detach-grace reattach continuity;
- active response projection on reattach;
- pending tool-approval replay through `session.ready`;
- deterministic zero-grace behavior for isolated API/CI fixtures.

P3A historical multimodal attachment re-inspection remains independently frozen on:

```text
c0f8a85
```

See:

- `docs/reports/p3-freeze-candidate.md`
- `docs/reports/p3a-freeze.md`

Known Real-provider historical-image behavior that was not successfully verified remains provider/maintenance work. It does **not** block P4.

Important frozen P3 contracts include:

- registry / policy / execution separation;
- workspace read/write/list/patch/search/move;
- working-directory-relative paths;
- attachment reread, including historical image re-inspection;
- artifact creation/export/verification;
- offline `sandbox.run`;
- `web.search`;
- SSRF-safe `web.fetch`;
- bounded approval-gated `http.request`;
- live approval protocol/UI;
- pending live approval recovery across supported reattach;
- email search/read/draft/send;
- exact approved-action binding;
- credentials outside model context;
- network mediation rather than unrestricted sandbox networking.

`general-assistant-v7` is the current full P3 demo harness.

Unrestricted sandbox networking, browser automation, calendar integration, GitHub mutation tools, and a plugin marketplace are not P3 closure requirements.

---

# Maintainer notes

Always keep this section.

- [x] Add proprietary license.

- [x] Establish trusted user/session-context baseline.

  P2C already provides explicit trusted handling for fields such as:

  - timezone;
  - preferred language;
  - locale;
  - preferred name.

  Additional trusted fields should be added only for concrete product requirements.

  Do not grow ad-hoc prompt fields or infer durable personal facts from conversational guesses.

- [x] Establish useful general-assistant tool baseline through P3.

  Additional typed integrations should be demand-driven rather than reopening P3 generically.

- [x] Close current-turn image usability under P2E.

- [x] Close historical attachment/image reread under P3A.

- [x] Keep provider reasoning separate from assistant output.

- [x] Keep Model/Reasoning controls in the active composer.

- [x] Harden queue/steer, interruption, detach-grace reattach, and approval-replay lifecycle behavior after the P3 freeze.

- [ ] Background responses / work continuing after the live Session Runtime are tracked under P6.

  Do not change session-deactivation semantics merely to keep ordinary responses running.

---

# P4 — Context compaction and memory

P4 should build on existing persistence and prompt-context foundations rather than introduce a second conversation-history architecture.

Current useful foundations already exist:

```text
SessionSnapshot.Summary
SessionSnapshot.SummarizedThroughEntrySequence
PromptContextBuilder.BuildMemorySystem(...)
PromptContextBuilder.MaxSummaryCharacters
HistoryRestoreWindow
IMemoryStore.ReadHistoryAsync(...)
durable paginated ConversationEntry history
durable terminal InterruptReason
session revision/concurrency semantics
session-aware language-model resolution
```

Important naming note:

`IMemoryStore` currently means the durable session/profile persistence store. It is **not** the new structured semantic-memory abstraction described in P4B.

Do not silently overload `IMemoryStore` with a second meaning.

If P4B needs a store abstraction, prefer a distinct name such as:

```text
IStructuredMemoryStore
```

A broad `IMemoryStore` rename is not required to begin P4A.

P4 provides memory capability and policy seams. It does not turn those capabilities on for every agent.

Planned policy areas, configured later by P7:

```text
Session policy
- durable or ephemeral
- future retention and reopen policy

Session-memory policy
- enabled / disabled

Cross-session-memory policy
- disabled
- same-agent/user memory allowed
- user-wide memory allowed
```

A durable transcript does not imply cross-session memory. An examination-style agent may keep a durable transcript and in-session memory while the next session inherits none of that conversational memory. P4 does not implement the P7 admin UI, ephemeral sessions, or learned-memory reset.

Trusted P2C profile values stay authoritative over model-derived memory.

---

## P4A — Session context compaction

Goal:

```text
durable raw conversation
        ↓
bounded semantic summary of older history
        +
recent raw conversation tail
        ↓
model request
```

Compaction must improve long-session continuity without replacing raw history or creating a second transcript.

---

### P4A-0 — Formalize the existing compaction boundary

- [ ] Treat the existing:

  ```text
  Summary
  SummarizedThroughEntrySequence
  ```

  as the canonical initial compaction seam.

- [ ] Make the prompt-context boundary explicit.

  When a valid summary exists:

  ```text
  summary = semantic context through sequence N
  raw prompt tail = eligible entries after sequence N
  ```

  Do not knowingly feed the same historical turns both through the summary and again as ordinary raw history.

- [ ] Preserve the current user batch exactly once.

  Trailing queued/current user turns must never disappear merely because they cross a compaction boundary.

- [ ] Preserve explicit queue/steer semantics.

  A queued trailing user turn is future conversation input, not evidence that the current assistant response was semantically completed.

  An intentional steer/interrupt must not cause superseded assistant output to be promoted into memory as though it were fully delivered.

- [ ] Preserve assistant delivery semantics.

  Historical assistant context continues to use:

  - received prefix for Text;
  - heard prefix for Voice.

  Unreceived/unheard assistant tails must not become remembered facts simply because compaction exists.

  Persisted `InterruptReason` is provenance/lifecycle metadata. It is not additional assistant semantic text.

- [ ] Keep raw `ConversationEntry` history unchanged.

  Compaction is an additional projection, not destructive history rewriting.

- [ ] Define minimal version/provenance metadata for a generated summary.

  At minimum preserve:

  ```text
  format version
  summarized-through sequence
  generated-at
  model provenance where useful
  ```

  Do not over-design the schema.

- [ ] Add boundary tests before adding an LLM compactor.

  Cover:

  - no summary;
  - summary through an older entry;
  - recent tail after summary;
  - no summary/raw-history duplication;
  - queued trailing user turns;
  - intentional user steer with persisted interruption reason;
  - interrupted assistant output;
  - Voice heard-prefix behavior;
  - reopen from persistence;
  - detach-grace reattach;
  - long transcript with only a bounded runtime restore window.

### P4A-0 stop condition

The runtime can construct a correct prompt from:

```text
existing durable summary
+
eligible raw history after the summary boundary
```

without an LLM generating new summaries yet.

The boundary remains correct for queued turns, interrupted/steered responses, persisted delivery prefixes, reopen, and reattach.

This is the first P4 implementation item.

---

### P4A-1 — Generate semantic compaction

- [ ] Add a provider-neutral session compaction service.

  It owns compaction semantics, not provider wire format.

- [ ] Read source history from durable server-side history.

  Use the existing paginated persistence path rather than requiring the browser to load old messages.

- [ ] Compact only stable durable history.

  Do not summarize:

  - streaming entries;
  - active responses;
  - incomplete current user batches;
  - pending live tool-approval state;
  - unpersisted work;
  - stale/superseded runtime state.

  Assistant content must obey the same received/heard-prefix rules used by normal historical prompt construction.

- [ ] Preserve materially useful context:

  - unresolved topics;
  - decisions;
  - user constraints;
  - session goals;
  - relevant references;
  - relevant attachment/artifact references;
  - important open loops/tasks.

- [ ] Keep summaries bounded.

  Reuse or deliberately evolve the existing summary-character budget rather than allowing unbounded accumulation.

- [ ] Use the existing model-resolution architecture.

  Do not hard-code OpenAI, OpenRouter, or a concrete model ID.

- [ ] Initially use the normal session model-selection path unless measured requirements justify a separate compaction model policy.

  Add a new `ModelPurpose` only when it has meaningful selection semantics.

- [ ] Keep provider wire format in Infrastructure.

- [ ] Validate generated summaries before persistence.

  At minimum:

  - non-empty when replacement is expected;
  - bounded length;
  - valid source boundary;
  - no raw binary/base64 attachment payloads;
  - no provider reasoning;
  - no undelivered assistant tail promoted into semantic memory.

- [ ] Provide deterministic failure behavior.

  If compaction fails:

  - keep the previous summary;
  - keep raw durable history;
  - continue using the existing bounded raw-history path;
  - do not corrupt the session;
  - do not fabricate a summary.

- [ ] Synthetic tests must not depend on an external model.

---

### P4A-2 — Compaction lifecycle

- [ ] Add a bounded compaction policy.

  Do not run an LLM summarization call after every message.

  Trigger based on enough unsummarized eligible history to justify compaction.

- [ ] Prefer opportunistic compaction after a completed durable turn.

  It should be runtime-internal maintenance, not a P6 `WorkItem`.

- [ ] Do not require a user-visible progress transcript for ordinary compaction.

  Telemetry is sufficient unless real latency proves a UX surface is needed.

- [ ] Compaction must be cancellable with the Session Runtime.

  It must not silently become durable detached/background work.

- [ ] Respect detach-grace semantics.

  A temporary disconnect followed by valid reattach must not create duplicate compaction work or corrupt the summary boundary.

  Actual Session Runtime finalization may cancel in-flight compaction normally.

- [ ] Protect summary commits against stale work.

  A delayed compaction result must not replace a newer summary.

  Bind the result to:

  - session;
  - prior summary boundary;
  - source-through sequence;
  - runtime/session revision semantics as appropriate.

- [ ] A new user turn must remain usable if compaction has not completed.

  The fallback is the previously committed summary plus the normal bounded raw tail.

- [ ] Repeated compaction should be incremental.

  Conceptually:

  ```text
  previous summary
  +
  newly eligible durable history
  →
  replacement summary
  ```

  Do not reread/re-summarize the entire lifetime transcript on every compaction.

---

### P4A verification

- [ ] Domain/Application tests for compaction boundary semantics.

- [ ] Persistence tests for summary metadata and source boundary.

- [ ] Long-transcript tests proving compaction reads older durable history without materializing the entire transcript in the active Session Runtime.

- [ ] Tests for:

  - cancellation;
  - stale compaction result;
  - reconnect/reopen;
  - detach-grace reattach;
  - interruption;
  - intentional steer;
  - persisted interrupt reasons;
  - queued user turns;
  - pending live approval;
  - provider failure;
  - malformed/oversized summary;
  - repeated incremental compaction.

- [ ] Prompt tests proving:

  ```text
  summary(old history)
  +
  raw(new history)
  ```

  contains neither omission nor intentional duplication at the boundary.

- [ ] Synthetic end-to-end long-conversation scenario.

- [ ] Keep hosted compaction probes optional/credential-gated.

### P4A stop condition

A long-running session can exceed the raw prompt-history window while preserving useful older context through a durable, replaceable, provenance-aware summary.

Raw history remains authoritative and intact.

Failures safely fall back to the previous summary plus bounded recent history.

Queue/steer, interruption, delivery-prefix, and detach/reattach semantics remain correct across the compaction boundary.

---

## P4B — Structured session memory

Compaction and structured memory solve different problems.

```text
compaction
= compressed conversational continuity

structured memory
= individually addressable durable facts/goals/decisions/open loops
```

Do not turn the session summary into an unstructured substitute for typed memory.

---

### Memory model

- [ ] Introduce structured session-scoped memory.

Suggested initial concepts:

```text
memoryId
sessionId
kind
content
source/provenance
confidence?
createdAt
updatedAt
freshness/expiry?
```

Initial kinds may include:

```text
fact
preference
goal
decision
openLoop
```

Do not over-design the taxonomy before real workflows require it.

- [ ] Keep source provenance.

A memory should be traceable to relevant conversation evidence or a trusted host/user source.

- [ ] Distinguish model-derived memory from trusted explicit profile fields.

A model-derived memory must not silently overwrite a P2C trusted profile value.

---

### Structured-memory persistence

- [ ] Introduce a dedicated structured-memory persistence abstraction when needed.

Prefer something conceptually like:

```text
IStructuredMemoryStore
```

Do not repurpose the existing session-persistence `IMemoryStore` without an explicit migration/rename decision.

- [ ] Keep memory policy separate from persistence technology.

SQLite is sufficient initially.

- [ ] Do not require embeddings or a vector database initially.

---

### Memory access

- [ ] Add bounded read tools:

  ```text
  memory.search
  memory.get
  ```

- [ ] Add controlled mutation semantics:

  ```text
  memory.write
  memory.update
  memory.delete
  ```

  Exact tool exposure may be staged.

- [ ] Memory mutation remains subject to normal Agent Core tool/policy controls.

- [ ] Never allow memory tools to bypass session/user ownership.

- [ ] Never store secrets or credentials merely because the model asks to remember them.

---

### Memory correctness

- [ ] Add correction semantics.

- [ ] Add deletion semantics.

- [ ] Add replacement/supersession semantics where appropriate.

- [ ] Define conflict handling for contradictory memories.

- [ ] Do not silently promote uncertain conversational guesses into durable user facts.

- [ ] Do not make confidence a fake precision score.

If confidence is retained, use it only where policy/retrieval genuinely consumes it.

---

### P4B stop condition

A session can retain individually addressable durable facts, goals, decisions, preferences, and open loops with source provenance and explicit correction/deletion behavior.

This remains session-scoped until deliberate promotion.

Session memory being stored does not enable cross-session reuse.

---

## P4C — Cross-session memory

Start only after P4B session memory is reliable.

- [ ] Add explicit memory scope.

Initial scopes:

```text
Session
AgentUser
User
```

`AgentUser` is the same logical agent id plus the same user/profile, not an agent version and not an arbitrary agent-global bucket. Customer-support memory must not appear to an examiner for the same user.

Do not add organization-wide, host-global, or agent-global scope in P4.

- [ ] Bind AgentUser ownership to the logical agent id. Do not key it to `AgentVersion`, and do not store learned memory inside the pinned published definition.

- [ ] Define ownership and authorization from trusted runtime context.

- [ ] Promote deliberately from Session to AgentUser or Session to User.

Promotion creates an independent durable memory and preserves provenance. Do not retarget the original session memory's scope.

Promotion follows the cross-session policy. Disabled means no later-session retrieval and no promotion.

- [ ] Define correction/deletion behavior across scopes.

- [ ] Preserve provenance after promotion.

- [ ] Coordinate memory-derived personalization with the P2C trusted profile boundary.

Explicit user/host profile data remains stronger than inferred/model-derived memory.

- [ ] Add bounded retrieval.

Do not inject an entire user's lifetime memory into every model request.

- [ ] Add embeddings/vector retrieval only after measured retrieval quality or scale demonstrates the need.

### P4C stop condition

Useful durable memory can safely cross sessions only where policy allows it, without confusing:

- trusted profile data;
- current conversation;
- session summary;
- structured session memory;
- same-agent memory;
- user-wide memory.

A saved transcript is not cross-session memory.

---

# P5 — Events and configurable triggers

P1 already provides generic session-purpose/lifecycle foundations.

Build trigger semantics before scheduled/durable autonomous work.

- [ ] Expand the event model beyond current idle/environment triggers.

- [ ] Define a generic trigger contract.

Suggested fields:

```text
trigger type
payload
source
timestamp
dedupe/idempotency key
expiry
target session/agent/work item
```

- [ ] Add trigger sources when required:

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

- [ ] Allow paused/reopened sessions to reconnect to existing work without replaying the original user turn.

- [ ] Add background research only when a concrete workflow requires it.

- [ ] Add scheduled tasks after P5 exists.

- [ ] Add long-running sandbox jobs only when needed.

## Progress/results

- [ ] Reuse P2 progress semantics for live work.

- [ ] Persist durable WorkItem progress/checkpoints separately from ordinary assistant chat history.

- [ ] Deliver completed results to the originating session through the appropriate combination of:

  - result event;
  - assistant response;
  - artifact.

## Session UX

- [ ] Revisit pause/deactivation UX after background work exists.

Distinguish:

- user-paused conversation;
- runtime inactivity pause;
- disconnected session;
- detached work still running;
- completed goal;
- cancelled work;
- fully stopped session.

---

# P7 — Agent harness / admin mode

The repository already has:

- versioned role environments;
- isolated session workspaces;
- `develop` and `document` composition skills;
- Impeccable UI skill integration.

Productize harness editing only after core runtime contracts are stable.

## Effective harness context

- [ ] Allow the admin surface to inspect relevant non-secret effective configuration.

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

- [ ] Require privileged changes to be explicit operations.

Flow:

```text
agent prepares proposed operation
→ UI presents operation
→ human approves where required
→ normal policy/tool authorization executes it
```

An admin agent does not bypass policy because it generated the change itself.

### User mode

- [ ] Use an immutable/pinned published agent version.

- [x] Give every session its own isolated runtime workspace.

- [ ] Prevent user sessions from mutating source harnesses.

## Publishing lifecycle

- [ ] Define:

  - draft;
  - validate;
  - test/evaluate;
  - publish immutable version;
  - rollback/deprecate.

Published versions stay immutable. Changing instructions, tools, policies, or identity text publishes a new version rather than mutating a version already pinned by sessions.

- [ ] Expose agent/admin configuration for the P4 policy seams:

  - session persistence, including future retention and reopen policy;
  - session-memory enablement;
  - cross-session scope permissions;
  - whether a new published version inherits learned memory, starts fresh, or migrates selected memory.

- [ ] Add a future **Reset learned memory** operation for an agent's learned cross-session memory.

That operation must not rewrite or delete the published agent definition, and it is not the same thing as resetting identity. Do not treat it as a P4 requirement.

- [ ] Allow an admin agent to improve its harness only through normal bounded tool/policy mechanisms.

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

- [ ] Add external tool-provider/plugin extensibility only when another real provider/integration justifies it.

MCP-like providers may be adapters.

Requirements:

- native Agent Core tools remain supported;
- external providers still pass through Agent Core policy/authorization;
- provider credentials remain outside model context.

- [ ] Add reusable harness validation:

  - schema validation;
  - missing tool/provider references;
  - invalid permissions;
  - incompatible model/provider capabilities;
  - unsafe configuration;
  - evaluation/test scenarios.

- [ ] Keep model-provider extensibility separate from tool/integration-provider extensibility.

Do not build one generic abstraction that hides different security/lifecycle boundaries.

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

- [ ] Separate host credentials/scopes where browser users must not possess host authority.

- [ ] Horizontal/distributed Session Runtime only when single-process ownership becomes a real constraint.

---

# Deferred / optional provider work

These items do not block the product roadmap.

## Real P3 provider verification

- [ ] Revisit the known Real/OpenRouter historical-image reread gap separately from P4.

Keep it:

- bounded;
- credential-gated;
- provider-specific;
- outside default CI.

Do not reopen the provider-neutral P3A architecture unless a provider-independent regression is found.

## Hosted voice verification

- [ ] Run **HOSTED-04**: one actual non-Synthetic end-to-end Voice smoke using an explicitly selected hosted configuration.

Keep it opt-in and credential-gated.

## Realtime hosted STT

- [ ] Finish or replace realtime `OpenAiSpeechRecognizer` only when a concrete need exists.

Current valid options already include:

- Browser STT;
- OpenAI-compatible batch STT;
- Synthetic STT.

Do not implement realtime hosted STT merely for adapter symmetry.

## Native speech-to-speech / realtime reasoning

- [ ] Revisit only if measured latency/quality demonstrates that:

```text
STT → text model → TTS
```

is insufficient.

Keep the composed provider-neutral pipeline canonical until then.

---

# Continuous quality work

- [ ] Keep Synthetic/offline verification as the default deterministic path.

- [ ] Keep `main` green before beginning the next architectural phase.

- [ ] Add regression coverage with every lifecycle, response, speech, multimodal, tool, memory, trigger, and background-work change.

- [ ] Maintain Playwright coverage for meaningful user-visible workflows.

- [ ] Make asynchronous/race-sensitive tests deterministic.

Prefer explicit gates/events over timing assumptions.

- [ ] Keep hosted-provider tests explicitly opt-in.

- [ ] Periodically run bounded Real OpenRouter/OpenAI/browser-speech probes when credentials/support are available.

- [ ] Add startup catalog/provider capability-consistency validation if provider capability configuration becomes independently variable enough that contradictory declarations can occur.

This is not a reason to reopen frozen P2E.

- [ ] Maintain observability for:

  - model calls;
  - model/provider selection;
  - capability selection/rejection;
  - image-input presence without image-content logging;
  - reasoning presence without reasoning-content logging;
  - response lifecycle;
  - interruption reason;
  - queue/steer behavior;
  - detach/reattach lifecycle;
  - progress lifecycle;
  - speech provider/capability selection;
  - tool calls;
  - pending tool approvals;
  - sandbox execution;
  - compaction lifecycle;
  - memory retrieval/mutation;
  - trigger decisions;
  - background work;
  - policy denials;
  - resource limits.

- [ ] Keep docs synchronized with observed implementation.

In particular:

- P1 remains frozen on `dceaccb`;
- P2 remains frozen on `47d6ff6`;
- P3 key-free freeze remains `4dbb920`;
- post-freeze lifecycle/CI maintenance is green through `3e56a26`;
- P4 is the active roadmap;
- provider-specific Real gaps do not silently become architecture phases.

- [ ] Keep TODO focused on current/future work.

Detailed historical gate counts, correction narratives, and workflow evidence belong in `docs/reports`.

---

# Implemented baseline

Keep this compact. It is orientation, not another roadmap.

- [x] .NET 10 / C# 14 modular monolith.
- [x] React / Vite / TypeScript frontend.
- [x] Ant Design v6 conversation-first UI.
- [x] SignalR + MessagePack realtime transport.
- [x] Synthetic deterministic test/development profile.
- [x] OpenAI-compatible streaming model adapter.
- [x] OpenRouter configuration.
- [x] Trusted model catalog.
- [x] Per-session model/reasoning selection.
- [x] Model capability metadata.
- [x] Provider reasoning separated from public assistant output.
- [x] Validated display/speech response semantics.
- [x] First-class transient progress semantics.
- [x] Durable multi-session catalog/history.
- [x] Paginated durable conversation history.
- [x] Bounded runtime restore.
- [x] Session purpose/lifecycle/completion policy.
- [x] Terminal read-only history.
- [x] Owner trusted-profile API and persistence.
- [x] Session attachments and artifacts.
- [x] Current-turn multimodal image input.
- [x] Historical attachment/image reread.
- [x] Capability-aware model/image admission.
- [x] Repeated proactive initiative.
- [x] Client pending-send FIFO.
- [x] Explicit queue vs intentional steer behavior.
- [x] ACK-to-response-start busy-gap protection.
- [x] Steer/interrupt/Stop semantics.
- [x] Durable assistant interruption reasons.
- [x] Detach-grace reconnect/reattach continuity.
- [x] Active response recovery on reattach.
- [x] Pending tool-approval replay on reattach.
- [x] Session-owned isolated workspaces.
- [x] Typed bounded tool execution.
- [x] Workspace search/move/patch/list/read/write.
- [x] Knowledge and attachment tools.
- [x] Artifact tools.
- [x] Docker-backed offline sandbox.
- [x] Public web search/fetch.
- [x] Approval-gated generic HTTP.
- [x] Live approval UI/protocol.
- [x] Email search/read/draft/send.
- [x] Synthetic / Browser / hosted-compatible speech abstractions.
- [x] Voice interruption and heard/received tracking.
- [x] Impeccable UI skill integration.
- [x] Shared `develop` and `document` composition skills.
- [x] Existing durable session-summary fields ready for P4A.