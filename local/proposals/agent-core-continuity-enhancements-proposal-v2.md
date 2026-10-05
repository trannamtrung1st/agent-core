# Continuity Enhancements Proposal

**Project:** Agent Core
**Status:** Implemented locally (Phases 1–5; AC1–AC17 verified). Optional Phase 6 is deferred. See [implementation and verification report](../../docs/reports/continuity-enhancements-v2.md).
**Purpose:** Simplify and strengthen cross-session continuity while preserving the existing distinctions between Memory, Experience, and Thought.

---

## 1. Summary

Agent Core already has three useful but separate concepts:

- **Memory** — retained facts, preferences, and durable context.
- **Experience** — derived observations about how past work went.
- **Thought** — an autonomous wake-up / execution mechanism.

The proposed enhancement introduces **Continuity** as the umbrella capability that unifies how historical context is discovered, retrieved, ranked, and supplied to the agent, without collapsing these concepts into one undifferentiated store.

The target behavior is intentionally similar to ChatGPT-style cross-session continuity:

> The agent should feel like it remembers prior work and prior conversations, even when a new session starts.

The system does not need perfect synthesis to achieve this. Automatic Memory and Experience retrieval should provide likely-relevant context, while the agent also receives explicit tools to search and inspect prior continuity when needed.

The central design is:

```text
History      = what happened
Memory       = what the agent knows
Experience   = what the agent learned from outcomes
Thought      = when the agent independently reasons or acts
Continuity   = how the agent finds and reuses retained context
```

---

## 2. Goals

### G1. ChatGPT-like cross-session continuity

A managed Agent Instance should be able to begin a new session and still make use of relevant information from prior sessions and prior work.

The agent should be able to answer questions such as:

- “Have we handled this before?”
- “What happened the last time I checked this store?”
- “What did I learn from the previous attempt?”
- “What does this user prefer?”
- “Find the conversation where we discussed this.”

### G2. Keep the conceptual model simple

The product model should remain understandable:

> **History records what happened.**
> **Memory keeps important facts.**
> **Experience keeps lessons from past work.**
> **Continuity finds the relevant parts when the agent needs them.**
> **Thought lets the agent wake up and act without a user message.**

### G3. Preserve semantics and trust boundaries

Memory and Experience may share retrieval and indexing infrastructure, but they must retain different meanings.

- Memory may represent a retained belief or preference.
- Experience represents an observation derived from historical work.
- Session history remains source evidence.
- Thought is execution, not persistence.

Experience must remain non-authoritative and must never become policy, instructions, tool authority, credentials, or approval.

### G4. Avoid requiring perfect synthesis

Automatic Memory and Experience extraction will never be perfect.

The agent must therefore have an explicit cross-session retrieval fallback that can search old continuity records and bounded historical session content.

### G5. Reuse existing durable infrastructure

Where practical, continuity maintenance should reuse:

- durable background work,
- retries and recovery,
- checkpoint-based deduplication,
- model pinning,
- scheduling infrastructure,
- existing provenance boundaries.

---

## 3. Non-Goals

This proposal does **not** require:

- merging Memory and Experience into one semantic type,
- replaying all old conversations into every prompt,
- letting the model freely decide when to create Experience,
- using Thought as the Memory/Experience synthesis engine,
- full semantic-vector infrastructure as a prerequisite,
- perfect relevance ranking in the first implementation,
- deleting or replacing the current Memory or Experience implementations.

---

## 4. Core Model

### 4.1 Continuity as the umbrella

Introduce **Continuity** as the common retrieval and maintenance layer.

Conceptually:

```text
Continuity
├── Session History
├── Memory
└── Experience
```

Continuity does not imply that all three must use one physical table.

It provides a common abstraction for:

- search,
- retrieval,
- ranking,
- context selection,
- provenance,
- bounded inspection.

### 4.2 Memory

Memory answers:

> “What should this agent retain as durable context?”

Examples:

- the user operates a nopCommerce store,
- the user prefers concise status reports,
- the user’s preferred locale,
- a durable business fact.

Memory is not a transcript.

### 4.3 Experience

Experience answers:

> “What did this agent learn from observable past work?”

Examples:

- an active order filter previously caused a wrong count,
- observing the current page before navigating avoided an error,
- a particular approach failed while another succeeded.

Experience remains:

- derived,
- provenance-backed,
- bounded,
- untrusted as instruction,
- non-authoritative.

### 4.4 Session History

Session History is the source record of what actually happened.

It is not injected wholesale into future prompts.

Instead, it is available for:

- synthesis,
- continuity search,
- bounded historical inspection,
- provenance verification.

### 4.5 Thought

Thought answers:

> “When should this agent wake up and independently reason or act?”

Thought is not a storage layer.

Thought may **consume Continuity**, but it should not be the mechanism that decides what becomes Memory or Experience.

### 4.6 Scheduled Work vs Thought

Scheduled Work and Thought intentionally share runtime infrastructure while keeping different product and domain semantics.

```text
Scheduled activation
├── Scheduled Work
│   “Do this known task later or repeatedly.”
│
└── Thought
    “Wake up periodically and decide whether anything useful needs doing.”
```

**Scheduled Work** starts with a known obligation or intent. Time determines when that task should execute.

Examples:

- “Check pending orders every morning at 09:00.”
- “Remind me tomorrow to renew the certificate.”
- “Send a weekly operations summary.”

**Thought** starts with a decision opportunity rather than a guaranteed task.

Examples:

- “Every four hours, review current responsibilities and decide whether anything useful needs attention.”
- “Periodically review recent Experience and current state; take no action when nothing meaningful is available.”

Thought therefore keeps distinct completion semantics:

- `NoAction`
- `ActionCompleted`
- `AttentionRequested`

`NoAction` is a successful and expected Thought outcome.

The systems should share:

- trigger registration persistence,
- scheduler infrastructure,
- occurrence admission,
- durable WorkItem execution,
- retries and recovery,
- model selection,
- tool execution,
- overlap/coalescing behavior,
- observability.

They should remain distinct through an activation/source kind such as:

```text
TriggerRegistration / Occurrence source
├── Schedule
├── ApplicationEvent
└── ThoughtActivation
```

Do **not** flatten Thought into ordinary recurring Scheduled Work plus flags. Its autonomous-judgment and completion semantics are meaningfully different.

---

## 5. Desired Runtime Behavior

### 5.1 New-session continuity

When a new managed session starts or a user turn is evaluated:

1. Core identifies the current Agent Instance.
2. Core gathers a bounded continuity context.
3. Relevant Memory and Experience are selected.
4. Small historical session hints may be included when useful.
5. The model receives the current task plus continuity context.
6. If the automatic context is insufficient, the model may use Continuity tools.

Conceptually:

```text
Current user turn
      │
      ▼
Continuity retrieval
      │
      ├── relevant Memory
      ├── relevant Experience
      └── optional session-history hints
      │
      ▼
Agent context
      │
      └── continuity.search / continuity.get when needed
```

### 5.2 Do not inject whole prior sessions

Historical session transcripts must not be automatically appended to normal model context.

Reasons:

- context growth,
- stale information,
- irrelevant detail,
- privacy and trust boundaries,
- prompt injection risk,
- poor scalability.

Old sessions are retrieved only when relevance or explicit agent inspection justifies it.

---

## 6. Continuity Tools

### 6.1 `continuity.search`

Add a read-only tool that searches across continuity sources owned by the current Agent Instance.

Example intent:

```text
continuity.search("pending nopCommerce orders")
```

Possible result:

```text
Memory
- User operates a nopCommerce store.

Experience
- A previous order audit was inaccurate because an order-status filter remained active.

Session
- 2026-10-02: inspected the store and found 3 pending orders.
```

### Required behavior

- Search only records visible to the current Agent Instance / owner scope.
- Return bounded results.
- Every result must identify its kind.
- Every result must retain provenance.
- Session matches return summaries/snippets, not unlimited transcripts.
- Search results are untrusted historical context.
- Search does not grant authority.

### Candidate result model

```text
ContinuitySearchResult
- id
- kind: Memory | Experience | Session
- title / summary
- sourceId
- occurredAt
- scope
- provenance
- relevance metadata
```

Exact persistence shape is implementation-specific.

### 6.2 `continuity.get`

Add a read-only tool for bounded inspection of a selected result.

It should support:

- Memory item detail,
- Experience item detail,
- Session summary / bounded historical range.

Example:

```text
continuity.get({
  kind: "session",
  id: "..."
})
```

The runtime must enforce:

- maximum returned content,
- instance ownership,
- source visibility,
- secret/sensitive-content rules,
- historical content as untrusted input.

### 6.3 Avoid a large family of separate tools

Prefer:

```text
continuity.search
continuity.get
```

over:

```text
memory.search
memory.get
experience.search
experience.get
session.search
session.read
...
```

The agent should reason about one historical retrieval capability.

Internal services may remain separate.

---

## 7. Automatic Continuity Retrieval

### 7.1 Near-term retrieval policy

The initial implementation does not require advanced vector search.

A practical first version may rank candidates using:

1. current Agent Instance scope,
2. explicit type/scope eligibility,
3. lexical/query relevance,
4. recency,
5. source confidence / provenance,
6. current task/topic match.

The result must remain bounded.

### 7.2 Evolution path

Later implementations may add:

- semantic embeddings,
- hybrid lexical + semantic retrieval,
- recency decay,
- confidence weighting,
- source-quality weighting,
- last-used tracking,
- supersession awareness.

These are improvements, not prerequisites.

### 7.3 Automatic context budget

Continuity must have a strict prompt budget.

Suggested policy:

- retrieve a larger candidate pool,
- rank candidates,
- inject only the best few,
- preserve explicit headings for Memory and Experience,
- never silently exceed the configured continuity budget.

The existing bounded Experience context behavior should be preserved until superseded by a common Continuity budget.

---

## 8. Experience Retrospection Policy

### 8.1 Keep current stable boundaries

The existing stable-boundary behavior remains the baseline.

Eligible boundaries include:

- session paused,
- session ended,
- substantive durable work completed,
- substantive durable work failed,
- substantive durable work cancelled.

Retrospection remains secondary background work.

The original source result must remain final regardless of retrospection success or failure.

### 8.2 Add periodic continuity maintenance

Long-running sessions may remain active for a long time and never pause/end.

Add a low-frequency **Continuity Maintenance** pass.

Its purpose is:

> Find meaningful completed work that has not yet been synthesized and create any missing derived continuity.

Example policy:

```text
periodically:
  inspect active managed sessions
  find stable completed checkpoint > last processed checkpoint
  if meaningful new observable work exists:
      request retrospection
```

### Important rules

- Use checkpoint/cursor-based deduplication.
- Do not retrospect an unchanged checkpoint.
- Do not create work for inactive/no-op sessions.
- Do not require the model to decide whether maintenance should run.
- Failures must not affect the source session.
- Maintenance is host policy, not agent authority.

### 8.3 Do not expose this primarily as an “Experience interval”

Internally it may use a scheduler.

Product semantics should remain:

> Continuity is maintained in the background.

The user should not have to understand periodic reflection mechanics for normal use.

---

## 9. Memory Synthesis

### 9.1 Keep Memory distinct from Experience

Do not convert Experience directly into trusted Memory.

Example:

```text
Experience:
"Resetting the order filter previously fixed an incorrect pending-order count."
```

must not silently become:

```text
Memory:
"Always reset the order filter."
```

That would transform a historical observation into an instruction.

### 9.2 Shared synthesis infrastructure is allowed

Memory and Experience synthesis may reuse:

- background work,
- source checkpointing,
- model selection,
- source projections,
- secret filtering,
- dedupe infrastructure,
- observability.

They should produce different typed outcomes.

### 9.3 Possible future synthesis classifier

A future continuity synthesis job may classify retained output as:

```text
Memory candidate
Experience candidate
No retention
```

But each destination must still enforce its own validation and trust semantics.

This is optional and should not block the first Continuity enhancement.

---

## 10. Thought Integration

### 10.1 Keep Thought separate

Thought remains an autonomous execution mechanism.

It may be scheduled or triggered independently.

It must not become the persistence engine for continuity.

### 10.2 Thought consumes Continuity

A Thought activation should be able to receive the same bounded continuity retrieval available to normal work.

Example:

```text
Thought:
"Review store operations and determine whether useful action is required."

Continuity:
- Memory: user runs a nopCommerce store.
- Experience: previous order checks were wrong when a filter was left active.
```

The Thought can use this context to act more effectively.

### 10.3 Thought may use Continuity tools

If the initial injected context is insufficient, Thought may use:

```text
continuity.search
continuity.get
```

subject to normal capability and execution-origin policy.

### 10.4 Thought must not self-reinforce continuity automatically

Avoid:

```text
thought wakes
→ reflects on its own reflections
→ writes more memory
→ future thought treats that as stronger evidence
```

Continuity synthesis must continue to require observable source evidence and host-controlled admission rules.

---

## 11. Trust and Safety Rules

### R1. Historical content is not policy

Memory, Experience, and session history must not override:

- current Definition,
- system policy,
- approval policy,
- tool permissions,
- current user instruction,
- current trusted application context.

### R2. Experience is observational

Experience must always be presented to the model as historical derived observation.

It is never:

- authority,
- permission,
- configuration,
- instruction.

### R3. Session history is untrusted input

Historical user and assistant messages may contain:

- stale instructions,
- prompt injection,
- old assumptions,
- incorrect model output.

`continuity.get` and automatic session summaries must label historical content accordingly.

### R4. Scope isolation

Continuity retrieval must not leak:

- another Agent Instance’s private Experience,
- another user/profile’s Memory,
- unrelated session history,
- deleted/suppressed content.

### R5. Sensitive information

Existing secret/sensitive-content filtering rules remain mandatory.

Continuity synthesis must not make sensitive content more persistent than the source policy allows.

---

## 12. Provenance

Every retained or searchable continuity item must preserve enough provenance to answer:

- where did this come from?
- when was it observed?
- which Agent Instance owned it?
- which session/work item produced it?
- which Definition version was active?
- is it Memory, Experience, or historical session evidence?
- has it been suppressed, deleted, or superseded?

The UI does not need to expose every field by default, but the data must remain auditable.

---

## 13. Suggested Shared Abstraction

A common internal projection may be introduced without immediately changing persistence:

```text
ContinuityItem
- ContinuityId
- Kind
    - Memory
    - Experience
    - Session
- AgentInstanceId
- ProfileId
- Summary
- SourceId
- SourceCreatedAt
- UpdatedAt
- Scope
- Visibility
- Provenance
```

This is a retrieval abstraction, not necessarily the canonical persistence model.

Existing Memory / Experience stores may adapt into it.

---

## 14. Schedule Authoring and Background Work

### 14.1 Two authoring surfaces, one schedule model

Scheduled Work should be authorable from both:

1. **Agent Chat**
2. **Admin**

Both surfaces must create or update the same underlying Trigger Registration model.

```text
Chat / Admin
     │
     ▼
Trigger Registration
     │
     ▼
Occurrence
     │
     ▼
Durable Background Work
```

This preserves one execution model while supporting two different workflows.

### 14.2 Chat authoring

Chat remains the conversational scheduling path.

Examples:

- “Remind me tomorrow at 09:00.”
- “Check pending orders every morning.”
- “Summarize unresolved issues every Friday.”

Characteristics:

- initiated from a user turn,
- natural-language schedule interpretation,
- authorization/provenance tied to the user turn,
- optimized for convenience.

### 14.3 Admin authoring

Admin becomes the structured owner/operator scheduling path.

Admin must be able to:

- create a schedule,
- edit its intent,
- configure one-shot or recurring timing supported by the existing schedule model,
- select execution model/reasoning where supported,
- enable/disable,
- cancel/delete according to existing lifecycle rules,
- inspect next run and status,
- invoke **Run now**.

Admin authoring should not require an LLM merely to create the schedule.

This supports preconfigured persistent workers that can operate before any user conversation occurs.

Example:

```text
Morgan — Store Operations Assistant

Scheduled Work
- 08:00 daily — Review pending orders
- 17:00 daily — Summarize unresolved operational issues

Thought
- Every 4 hours — Review responsibilities and decide whether attention is needed
```

### 14.4 `Run now`

Admin should expose **Run now** for eligible schedules.

`Run now` must not call the model directly.

It must use the normal execution path:

```text
Run now
   ↓
admit occurrence
   ↓
create/route durable WorkItem
   ↓
normal execution
   ↓
normal retries / approval / completion / observability
```

Manual execution must therefore preserve the same:

- authorization boundaries,
- model pinning,
- tool policy,
- approval behavior,
- provenance,
- retry/recovery behavior,
- result history.

Thought's existing `Run now` behavior is the reference pattern.

### 14.5 Background Work remains execution-oriented

Do **not** add a generic “Create background job” action to the Background Work UI.

A WorkItem is an execution record, not an intent/configuration object.

Keep the distinction:

```text
Schedule     = future obligation / intent
Occurrence   = one firing of that obligation
WorkItem     = durable execution of that firing
```

Admin authors the **Schedule**.

Admin observes and manages execution state through **Background Work**.

This separation prevents execution records from becoming an alternate configuration model.

### 14.6 Schedule provenance

Schedule provenance must distinguish where a registration came from.

The exact enum naming is implementation-specific, but semantics should support at least:

```text
AuthorizationOrigin
- UserTurn
- AdminOwner
- AdminThought
- ApplicationEvent
```

The system must be able to answer:

> “Why does this scheduled work exist?”

with provenance such as:

- created from a user request in session X,
- created by the owner in Admin,
- created as an owner-configured Thought,
- created from an application-event subscription.

Editing provenance must not grant new execution authority.

---

## 15. Admin / UX

### 14.1 Keep specialized management

Memory and Experience may continue to have separate Admin management because their semantics differ.

### 14.2 Add continuity inspection later

A future Admin surface may expose:

```text
Continuity
├── Memory
├── Experience
└── Historical sessions
```

with shared search.

This is optional for the first implementation.

### 14.3 Manual retrospection remains useful

`Retrospect now` remains as:

- operator recovery,
- testing,
- demo tooling,
- manual checkpointing.

It is not the normal user workflow.

---

## 16. Acceptance Criteria

### AC1 — Cross-session search

Given an active managed Agent Instance with eligible historical Memory, Experience, and prior sessions:

When the agent calls `continuity.search` with a relevant query,

Then:

- matching results may include all supported continuity kinds,
- results belong only to the current ownership scope,
- results are bounded,
- each result identifies its kind and provenance.

### AC2 — Historical inspection

Given a result returned by `continuity.search`:

When the agent calls `continuity.get`,

Then:

- Core returns bounded detail,
- ownership is enforced,
- deleted/suppressed content is unavailable,
- historical session content is treated as untrusted.

### AC3 — New-session continuity

Given Experience or Memory from a prior session,

When a new session starts for the same Agent Instance,

Then relevant eligible continuity may be supplied automatically without reopening the old session.

### AC4 — No whole-history injection

Given many historical sessions,

When a new turn begins,

Then Core does not append complete prior transcripts automatically.

### AC5 — Periodic maintenance

Given an active managed session with new completed observable work after the last retrospective checkpoint,

When Continuity Maintenance runs,

Then Core may admit one retrospection for the new checkpoint.

Running maintenance again against the same unchanged checkpoint creates no duplicate.

### AC6 — No-op maintenance

Given an active session with no meaningful new completed work,

When Continuity Maintenance runs,

Then no retrospection work is created.

### AC7 — Stable boundary compatibility

Existing pause/end and terminal durable-work retrospection continues to work.

### AC8 — Thought consumes continuity

Given a Thought activation for an Agent Instance with eligible historical continuity,

Then bounded relevant continuity is available to that activation.

### AC9 — Thought does not own synthesis

Thought activation alone must not automatically create Memory or Experience without an eligible observable source and the normal continuity admission path.

### AC10 — Trust boundary

Historical Memory/Experience/session context must not increase tool authority, bypass approval, change Definition policy, or grant credentials.

### AC11 — Restart safety

Pending continuity maintenance / retrospection survives process restart according to existing durable-work guarantees.

### AC12 — Source independence

Failure to generate Memory/Experience must never alter or invalidate the completed source session/work outcome.

### AC13 — Admin schedule authoring

Given an owner operating Admin,

When the owner creates a schedule,

Then the system creates the same Trigger Registration contract used by chat-authored schedules, with Admin provenance and without requiring an LLM to interpret the configuration.

### AC14 — Admin schedule management

Given an Admin-authored or chat-authored schedule visible to the owner,

Then Admin can inspect and perform supported lifecycle mutations without changing the execution semantics of future occurrences.

### AC15 — Run-now execution parity

Given an eligible schedule,

When the owner selects **Run now**,

Then Core admits an occurrence through the normal occurrence → durable WorkItem pipeline rather than directly invoking the model.

### AC16 — Background Work remains execution-only

The Background Work surface must not create arbitrary WorkItems directly.

Any new owner-authored future work must originate from an explicit supported intent source such as a schedule, event registration, Thought, or other defined trigger type.

### AC17 — Thought remains a distinct activation kind

Thought and Scheduled Work may share scheduler and execution infrastructure, but Thought retains distinct source/admission and completion semantics including a valid `NoAction` outcome.

---

## 17. Verification Strategy

### Unit tests

Cover:

- continuity result projection,
- result ranking,
- scope filtering,
- bounded search results,
- bounded `continuity.get`,
- deduplication by checkpoint,
- deleted/suppressed filtering,
- Thought continuity availability,
- trust labels.

### Integration tests

Cover:

1. complete session A,
2. create Experience,
3. start session B,
4. verify continuity is available,
5. search for session A using `continuity.search`,
6. inspect session A with `continuity.get`,
7. restart host,
8. verify continuity remains available.

### Long-running-session test

1. keep session active,
2. complete several meaningful turns,
3. run Continuity Maintenance,
4. verify Experience created,
5. run maintenance again,
6. verify no duplicate,
7. add new meaningful work,
8. run again,
9. verify a new checkpoint may be synthesized.

### Thought integration test

1. seed Memory + Experience,
2. run Thought activation,
3. verify continuity is present,
4. verify Thought may search continuity,
5. verify `NoAction` remains a successful quiet outcome,
6. verify no unauthorized persistence or authority escalation occurs.

### Admin scheduling test

1. create a schedule from Admin,
2. verify the registration has Admin-owner provenance,
3. verify it appears through the same registration/query model as chat-authored schedules,
4. run it with **Run now**,
5. verify an occurrence and durable WorkItem are created through the standard pipeline,
6. verify model/tool/approval policy is unchanged,
7. edit and disable the schedule,
8. restart the host,
9. verify schedule state and provenance survive.

### Scheduled Work vs Thought test

1. configure equivalent fixed-interval timing for a Scheduled Work registration and a Thought,
2. verify both reuse common scheduling/admission infrastructure,
3. verify their source kinds remain distinct,
4. verify Scheduled Work executes its known intent,
5. verify Thought may validly complete as `NoAction`,
6. verify neither activation kind gains authority from the scheduling mechanism itself.

---

## 18. Rollout Plan

### Phase 1 — Unified retrieval

Add:

- Continuity retrieval abstraction,
- `continuity.search`,
- `continuity.get`,
- shared bounded result model,
- scope/provenance enforcement.

Keep existing Memory and Experience persistence unchanged.

### Phase 2 — Automatic relevance

Replace purely recent Experience selection with shared relevance-based Continuity selection.

Initial ranking may remain simple and deterministic.

### Phase 3 — Continuity Maintenance

Add periodic maintenance for long-running active sessions.

Reuse existing checkpoint-based Experience admission and durable background work.

### Phase 4 — Admin schedule authoring

Add structured schedule management under the Agent Instance Admin experience:

- create/edit/enable/disable/cancel,
- schedule and model configuration,
- provenance display,
- **Run now** through normal occurrence admission,
- links to resulting Background Work.

Do not add direct arbitrary WorkItem creation.

### Phase 5 — Thought integration

Ensure Thought activations receive the same continuity retrieval path and may use Continuity tools where policy permits.

Preserve Thought as a distinct activation kind over shared scheduling/durable-work infrastructure.

### Phase 6 — Optional synthesis improvements (deferred)

Consider:

- shared Memory/Experience candidate classifier,
- semantic retrieval,
- conflict/supersession ranking,
- continuity Admin search,
- richer summarization.

---

## 19. Key Decisions

### Decision 1

**Continuity is an umbrella, not a replacement for Memory or Experience.**

### Decision 2

**Memory and Experience remain semantically distinct even if they later share storage/indexing infrastructure.**

### Decision 3

**Historical sessions become searchable on demand, not automatically replayed into every prompt.**

### Decision 4

**The agent gets a small Continuity tool surface: `continuity.search` and `continuity.get`.**

### Decision 5

**Core decides when continuity synthesis may occur. The model only performs bounded synthesis inside an admitted job.**

### Decision 6

**Periodic maintenance supplements stable lifecycle boundaries rather than replacing them.**

### Decision 7

**Thought consumes Continuity but is not the Continuity synthesis engine.**

### Decision 8

**Continuity must preserve provenance and remain subordinate to current policy, Definition, permissions, and user intent.**

### Decision 9

**Thought remains a distinct activation type. It shares scheduler, occurrence, and durable-work infrastructure with Scheduled Work but retains autonomous-judgment and `NoAction` semantics.**

### Decision 10

**Chat and Admin are both authoring surfaces for the same Scheduled Work / Trigger Registration model.**

### Decision 11

**Admin `Run now` admits a normal occurrence; it never bypasses the durable execution pipeline.**

### Decision 12

**Background Work is an execution/observability surface, not a generic WorkItem authoring surface.**

### Decision 13

**Registration provenance must distinguish user-turn scheduling, Admin-owner scheduling, Thought configuration, and event-driven activation.**

---

## 20. Product Statement

Agent Core provides durable continuity for persistent AI identities.

> **History records what happened. Memory retains what the agent should know. Experience retains what the agent learned from doing work. Continuity finds the relevant past when the agent needs it. Thought lets the agent wake up and act independently.**

Together, these allow an Agent Instance to behave more like a persistent worker across sessions without treating historical model output as permanent authority or requiring the entire conversation history to remain in context.

For autonomous execution:

> **Schedules execute known future obligations. Thoughts schedule opportunities for autonomous judgment. Both reuse the same durable execution foundation.**

For authoring:

> **Chat and Admin configure intent; Trigger Registrations represent that intent; Occurrences fire it; Background Work executes it.**
