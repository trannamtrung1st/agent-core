# TODO

Ordered by current dependency and product value.

Reviewed against `main` on **2026-09-25**.

P5 implementation freeze:

```text
4bbc0c17bc54746f87fd211174690659869e3e45
workflow 35954811544 — green
```

P5 is closed/frozen. Evidence: `docs/reports/p5-freeze-candidate.md`.

P6 final behavior freeze:

```text
2455de938ad4a156189ea7affcea1ca940cd4ec1
workflow 36081962547 — Compose green; exact-SHA offline pending (last review)

P6 core durable-runtime repair (2067a44):
2067a44a1534623dafc7803d14b8833ee1ba7890
workflow 36031813141 — green (exact SHA)

Manual A:
faithful wall-clock detached reminder — pass on 2455de9 tree (2026-09-25)

Freeze-record docs only:
c7434f3d2d8d0c91c89da4671bde4ce417bb706d
```

P6 is **closed/frozen** at **`2455de9`** (core repair **`2067a44`**). Do not reopen P6 unless a reproducible regression appears. Evidence: `docs/reports/p6-freeze-candidate.md`.

**Current active phase:** **P7 — agent harness / admin lifecycle** (not started).

P4 implementation freeze:

```text
822028f7cf17e5a978aced4996022e4085c4efa2
workflow 35806764609 — green
```

`76002a6` is the follow-up documentation/freeze-record commit. Its hosted Synthetic run `35808026110` had started but was still in progress at the time of this review. Do not imply that run is green until it has actually completed successfully.

Detailed historical verification belongs in `docs/reports`. Keep this file focused on current/future work, frozen architectural invariants, and enough baseline context to prevent accidental redesign.

---

# Current roadmap

1. **P0–P6 are closed/frozen** (behavior freeze `2455de9`; core repair `2067a44` / workflow `36031813141`; Manual A on `2455de9` tree).
2. **P7 — agent harness / admin lifecycle** is the next phase and has not started. Do not implement P7 without an explicit milestone request.
3. **P8 — harness/platform extensibility.**
4. **P9 — sandbox evolution when requirements justify it.**
5. **P10 — multi-user/product infrastructure when requirements justify it.**

Do not reopen a frozen phase without either:

- a reproducible regression; or
- a concrete new product requirement that belongs there rather than in a later phase.

---

# Frozen baseline

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

Do not redesign P1 as part of trigger/background-work implementation.

---

## P2 — Response, progress, multimodal input and personalization

Frozen on:

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

Do not overload trusted-profile state with trigger registration or background-work state.

---

## P3 — Tools and external integrations

Key-free freeze:

```text
4dbb9201de8028e7454b3be70a2e0730ac7c84f7
```

Verified hosted descendant:

```text
ac795b656e2e8ebd7d97f08d2d6ebc632d100cde
workflow 35682808408 — green
```

P3A historical multimodal attachment reread remains frozen on:

```text
c0f8a85
```

Important frozen P3 contracts include:

- registry / policy / execution separation;
- workspace read/write/list/patch/search/move;
- historical attachment reread;
- artifact creation/export/verification;
- offline `sandbox.run`;
- public `web.search` / SSRF-safe `web.fetch`;
- bounded approval-gated generic HTTP;
- live approval protocol/UI;
- email search/read/draft/send;
- exact approved-action binding;
- credentials outside model context;
- network mediation rather than unrestricted sandbox networking.

A future trigger firing does **not** bypass these tool authorization contracts.

---

## P4 — Context compaction, structured memory, and durable identity

Frozen on:

```text
822028f7cf17e5a978aced4996022e4085c4efa2
workflow 35806764609 — green
```

Evidence: `docs/reports/p4-freeze-candidate.md`.

Important frozen P4 contracts include:

- semantic compaction without destructive transcript rewriting;
- valid summary boundaries and stale/cancelled compaction fencing;
- structured session memory separate from trusted profile data;
- durable Agent Instance separate from reusable Agent Definition/version;
- pinned instance persona as runtime identity;
- IdentityUser learned memory scoped to Agent Instance + trusted user/profile;
- optional User-wide learned memory gated by policy;
- layered prompt-memory budgets and trusted-profile precedence;
- reset learned memory does not reset identity, persona, definition, knowledge, or transcripts.

P5 should build on the durable Agent Instance boundary. Do not store trigger registrations inside published Agent Definitions or learned memory.

---

## P5 — Events, durable triggers, and configurable scheduling

Frozen on:

```text
4bbc0c17bc54746f87fd211174690659869e3e45
workflow 35954811544 — green
```

Evidence: `docs/reports/p5-freeze-candidate.md`.

Important frozen P5 contracts include:

- durable trigger registrations and occurrences owned by Agent Instance + trusted profile;
- OneShot, Daily, Weekly, and FixedInterval schedules with deterministic offline scheduler semantics;
- `general-assistant` v10 for fixed-interval policy without mutating v8/v9 definition versions;
- current-turn, action-specific schedule authorization; bounded referents and one-follow-up drafts;
- scheduled-occurrence delivery without schedule tools; typed durable `ApplicationEvent` order ingress;
- owner-scoped dedupe; stale schedule revision rejection; `AcceptedLive` / begin recovery;
- `AwaitingDurableWork` as the explicit P6 handoff (P5 does not execute it);
- runtime-local timers remain separate from durable scheduling.

Do not reopen P5 without a reproducible regression or a requirement that genuinely belongs in P5 rather than P6+.

---

# Maintainer notes

Always keep this section.

- [x] Add proprietary license.

- [x] Establish trusted user/session-context baseline.

- [x] Establish useful general-assistant tool baseline through P3.

- [x] Close current-turn and historical-image usability.

- [x] Keep provider reasoning separate from assistant output.

- [x] Keep Model/Reasoning controls in the active composer.

- [x] Keep reusable Agent Definition separate from durable Agent Instance.

- [x] Keep trusted identity persona/baseline separate from learned memory.

- [x] Keep runtime-local timers separate from durable scheduling.

  Existing `TimerElapsedReceived` / `Task.Delay` behavior belongs to the live `SessionRuntime` lifecycle. It must not become the durable scheduler for reminders or recurring work.

- [x] Keep trigger registration separate from trigger execution.

  P5 owns trigger definitions, persistence, scheduling, admission, and normalized occurrences. P6 owns work that must execute or continue without a live Session Runtime.

- [x] Keep trigger authorization separate from later tool authorization.

  Permission to create or fire a trigger never means permission to perform future sensitive external actions.

- [x] Background responses / work continuing after the live Session Runtime are tracked under P6.

  Do not change session-deactivation semantics merely to keep ordinary responses alive.

---

# P5 — Events, durable triggers, and configurable scheduling

P5 is **frozen** on `4bbc0c1`. The checklist below records how the phase was implemented; do not treat it as active work.

## Goal

Allow Agent Core to represent, persist, schedule, normalize, and manage future trigger opportunities without turning the live Session Runtime into a long-lived scheduler or silently granting autonomous external-action authority.

Conceptually:

```text
user / application / environment / scheduler
                ↓
        trigger registration/source
                ↓
        normalized TriggerOccurrence
                ↓
       normal agent policy boundary
                ↓
 active SessionRuntime or P6 durable execution
```

A trigger is evidence/opportunity for the agent to act. It is not itself a privileged instruction and does not bypass normal policy or tool authorization.

## Existing foundations to preserve

The repository already has useful runtime-local event seams:

```text
EventContext
SessionInput
EnvironmentReceived
TimerElapsedReceived
AgentTrigger / TriggerKind
InitiativePolicy.Triggers
longSilence
environmentUpdate
unfinishedInteraction
```

These existing timers/triggers are lifecycle-bound to a live Session Runtime. Preserve them for interaction control and initiative.

Do **not** implement durable reminders by keeping a Session Runtime alive until a future `Task.Delay` completes.

---

## P5A — Formalize trigger contracts and ownership

### Trigger categories

Make the distinction explicit:

```text
Runtime-local trigger
- interaction timer / idle / cooldown / retry-style runtime signal
- owned by the live Session Runtime
- cancelled with that runtime
- not a durable user promise

Durable trigger registration
- one-shot schedule
- recurring schedule
- future typed domain/application event subscription
- future external/webhook subscription
- survives session detach/deactivation and process restart
```

- [x] Introduce a provider-neutral durable trigger registration model.

Suggested conceptual fields:

```text
triggerRegistrationId
agentInstanceId
trustedUser/profile owner
triggerType
schedule/event criteria
intent/payload
status
createdAt
updatedAt
expiresAt?
revision
sourceSessionId?
sourceEventId?
authorizationOrigin
```

`authorizationOrigin` should be server-owned provenance, conceptually distinguishing cases such as:

```text
UserRequested
UserApprovedProposal
AdminConfigured
SystemPolicy
```

Do not let the model choose arbitrary ownership identifiers or manufacture authorization provenance.

- [x] Introduce a normalized trigger occurrence/event.

Suggested conceptual fields:

```text
occurrenceId
triggerRegistrationId?
agentInstanceId
trustedUser/profile owner
triggerType
scheduledFor / observedAt
dedupe/idempotency key
payload/evidence
source
expiry?
sourceSessionId?
```

- [x] Keep trigger registration separate from trigger occurrence.

  A recurring registration may produce many occurrences. Updating/cancelling the registration must not rewrite already-completed occurrence history.

- [x] Make trigger ownership explicit.

  Durable user-facing triggers normally belong to:

  ```text
  Agent Instance + trusted user/profile
  ```

  not an ambiguous reusable Agent Definition id.

  `sourceSessionId` is provenance only; the originating session is not the trigger owner.

- [x] Treat trigger payload as data/evidence, not system authority.

  A scheduled intent such as:

  ```text
  "Remind me to call John"
  ```

  is remembered user intent. It must not be injected as a new system/developer instruction.

- [x] Add a distinct trigger store abstraction.

  Prefer a focused abstraction such as:

  ```text
  ITriggerStore
  ```

  rather than overloading session persistence or structured-memory stores.

### P5A verification

- [x] Persistence parity tests for InMemory and SQLite.
- [x] Ownership/isolation tests across Agent Instances and users.
- [x] Revision/concurrency tests for create/update/cancel.
- [x] Tests proving trigger payload cannot alter system-instruction authority.
- [x] Tests proving durable registrations survive process/runtime restart.
- [x] Tests proving runtime-local timer state is not accidentally persisted as a durable registration.

### P5A stop condition

Agent Core has one explicit durable trigger-registration model and one normalized occurrence model, with ownership and provenance that do not depend on a live Session Runtime.

---

## P5B — Durable scheduled triggers

Start with schedules before external webhooks. Scheduled reminders are immediately useful for the personal-assistant direction and have a smaller security surface.

Initial durable types:

```text
OneShotSchedule
RecurringSchedule
```

- [x] Add a durable scheduler service outside Session Runtime ownership.

  For the current modular monolith, prefer a simple hosted scheduler backed by the durable trigger store and `TimeProvider`.

  Do not add Redis, Kafka, Quartz, Hangfire, Kubernetes, or another scheduler platform unless measured requirements justify it.

- [x] Persist schedules before acknowledging creation.

- [x] Use structured schedule arguments rather than exposing raw cron as the primary model-facing contract.

  Example concepts:

  ```text
  one-shot:
    local/absolute time
    timezone

  recurring:
    frequency
    interval
    weekdays/month-day where applicable
    local time
    timezone
    startAt?
    endAt?
    maxOccurrences?
  ```

  Infrastructure may compile this into an internal schedule representation.

- [x] Preserve timezone semantics explicitly.

  Store enough information to distinguish:

  ```text
  "09:00 every Monday in Europe/London"
  ```

  from a fixed UTC interval.

  Compute/store the next due instant durably, while retaining the original timezone-aware recurrence semantics for later occurrences.

- [x] Define deterministic daylight-saving behavior for ambiguous or missing local times.

  Cover it with tests even if the initial demo timezone does not use DST.

- [x] Add bounded schedule policy.

  Policy should be able to constrain:

  - allowed schedule types;
  - minimum recurrence interval;
  - maximum active registrations per Agent Instance + user;
  - optional maximum horizon;
  - whether indefinite recurrence is allowed;
  - expiry / maximum occurrences where required.

- [x] Define restart and missed-occurrence behavior.

  Initial policy:

  - a due one-shot trigger fires once on recovery if it has not expired;
  - recurring schedules do not replay an unbounded backlog after downtime;
  - missed recurring occurrences are coalesced by default and the next future due time is calculated deterministically;
  - every emitted occurrence has a stable idempotency/dedupe identity.

- [x] Prefer at-least-once scheduler delivery plus idempotent occurrence admission rather than pretending the infrastructure provides exactly-once execution.

- [x] Scheduler failure must not corrupt or silently delete the registration.

### P5B verification

Use fake/deterministic time; do not build tests around real sleeps.

Cover:

- one-shot future schedule;
- recurring schedule;
- timezone conversion;
- DST edge cases;
- restart before due time;
- restart after due time;
- cancellation before due time;
- update/reschedule;
- duplicate scheduler wakeup;
- stale scheduler worker;
- missed recurring occurrences;
- expiry/max-occurrence termination;
- multiple users/instances with overlapping due times.

### P5B stop condition

Durable one-shot and recurring registrations survive restart and deterministically produce normalized, deduplicable trigger occurrences without requiring a live conversation runtime.

---

## P5C — Agent/user trigger-management tools and authorization

Allow the agent to manage schedules when the user asks for them, but do not turn model initiative into silent durable autonomy.

Initial model-facing tools:

```text
trigger.schedule_once
trigger.schedule_recurring
trigger.list
trigger.update
trigger.cancel
```

- [x] Keep arguments typed and narrow.

  Do not expose arbitrary SQL, code, raw scheduler internals, or unrestricted cron strings to the model.

- [x] Server-stamp owner, creation time, source event/session, and authorization origin.

  The model supplies the requested schedule/intent, not trusted ownership/security metadata.

- [x] Treat an explicit current user request as sufficient authorization for low-risk trigger registration.

  Example:

  ```text
  User: "Remind me tomorrow at 9 to call John."
  → agent may create the one-shot trigger
  → no redundant second approval dialog is required
  ```

- [x] Do not let a non-user-triggered agent run silently create durable schedules in the initial P5 design.

  If initiative/environment/another schedule makes the agent think a future trigger would be useful, it should propose/ask the user first. After the user confirms in a user turn, the trigger can be created normally.

  This prevents self-replicating or silently expanding durable autonomy without requiring a second confirmation for normal explicit requests.

- [x] Keep ordinary definition/admin policy able to disable trigger-management tools entirely for roles such as examiner/support agents where scheduling is inappropriate.

- [x] Trigger creation never grants standing permission for future sensitive actions.

  Example:

  ```text
  "Every Friday check overdue invoices and email customers"
  ```

  may authorize creation of the Friday trigger, but future email sends still pass through the normal email/tool authorization policy.

- [x] Do not introduce standing/bulk future-action approval in P5.

  If later required, model it explicitly as delegated authorization with scope, limits, expiry, provenance, and revocation rather than inferring it from trigger existence.

- [x] Allow user-requested list/update/cancel operations without an extra approval dialog when they affect triggers owned by that same Agent Instance + user.

- [x] Prevent cross-owner trigger mutation even if the model provides another id.

### P5C verification

- [x] User-requested one-shot creation through the agent.
- [x] User-requested recurring creation through the agent.
- [x] List/update/cancel own registrations.
- [x] Cross-user and cross-instance mutation denied.
- [x] Non-user-triggered agent execution cannot silently persist a new durable trigger.
- [x] Scheduling-disabled agent definition cannot create triggers.
- [x] Trigger registration does not bypass later `RequireApproval` tool policy.
- [x] Prompt/tool tests use natural user wording rather than only direct synthetic API calls.

### P5C stop condition

A user can naturally ask an eligible agent to create/manage reminders without redundant approval friction, while agent-originated durable scheduling remains user-controlled.

---

## P5D — Trigger policy separate from initiative policy

Today `InitiativePolicy.Triggers` controls existing proactive runtime behavior. P5 introduces additional policy concerns that should not be overloaded into one string list indefinitely.

- [x] Introduce/evolve a dedicated trigger policy boundary.

Conceptually it should answer:

```text
Which trigger types may wake this agent?
Which durable trigger types may be registered?
May user turns create them through agent tools?
What limits apply?
Which external/domain sources are trusted?
```

- [x] Keep `InitiativePolicy` responsible for proactive conversational behavior such as silence thresholds, cooldown, consecutive proactive turns, and whether the agent chooses to speak.

- [x] Preserve compatibility with existing `longSilence`, `environmentUpdate`, and `unfinishedInteraction` behavior during migration.

- [x] Do not make trigger eligibility equivalent to model initiative.

  A trigger can wake/evaluate the agent; normal behavior policy still determines whether/how it responds.

### P5D stop condition

Definition policy can restrict durable trigger capabilities without changing existing initiative semantics or reopening P1/P2 behavior.

---

## P5E — Typed application/domain/external trigger sources

Do this after scheduled triggers are stable.

Potential sources:

```text
application/domain event
allowlisted environment update
external webhook/event
```

- [x] Normalize the required P5 typed source (`order_status_changed`) into the same TriggerOccurrence boundary.

  Arbitrary external sources and public webhooks stay deferred. They are not an open P5 normalization gap.

- [x] Require typed/validated allowlisted event shapes.

- [x] External payloads are untrusted data, never executable instructions.

- [x] Preserve source authentication/verification outside model context.

- [x] Add replay/dedupe protection and bounded payload limits.

- [x] Source-specific rate limits and backpressure are future hardening, not unfinished P5 work.

  The required order-status ingress uses the occurrence payload limit and owner-scoped dedupe. A general per-source rate limiter waits for a concrete external source.

- [x] Keep webhook secrets and provider credentials outside model context.

- [x] Do not make arbitrary public webhook creation a P5 scheduling prerequisite.

  Add it only when a concrete integration needs it.

### P5E stop condition

At least one typed non-schedule source can produce the same normalized occurrence semantics without weakening source validation or prompt authority boundaries.

---

## P5F — Trigger occurrence routing and P6 handoff

P5 must define what firing means without prematurely implementing all background execution.

- [x] Route every accepted occurrence through one application boundary.

  Conceptually:

  ```text
  TriggerOccurrence
      ↓
  occurrence admission / dedupe
      ↓
  execution routing
  ```

- [x] Do not target only an originating live session.

  Durable trigger ownership is Agent Instance + user. `sourceSessionId` may help result delivery/audit but cannot be the only execution identity.

- [x] If a compatible live runtime can safely consume the occurrence, allow a bounded live path.

- [x] If execution must happen without a live runtime or must outlive it, hand off to P6 durable work.

- [x] Do not replay the original user message as though the user just sent it again.

  The current event should be represented as a trigger occurrence with original user intent/provenance.

- [x] Define occurrence state/provenance sufficiently for P6 to create one idempotent execution per occurrence.

### Important phase boundary

P5 alone provides reliable trigger registration and firing semantics.

The complete unattended product flow:

```text
"Remind/check/do this later"
→ durable trigger
→ fire while user is away
→ run agent/work
→ produce result/notification
```

requires **P5 + P6**.

---

## P5G — User visibility / management surface

Once durable schedules exist, users should be able to see what the agent has committed to do.

- [x] Add a minimal API/projection for active trigger registrations.

Expose safe fields such as:

- description/intent summary;
- schedule;
- timezone;
- next occurrence;
- active/paused state;
- created source/provenance where useful.

- [x] Add at least a minimal user-facing list/cancel/manage surface when scheduled triggers become a real product feature.

  Rich calendar/task UX can wait. Do not hide durable schedules exclusively inside chat history.

- [x] Do not expose internal scheduler implementation, credentials, raw webhook secrets, or untrusted payload dumps.

---

## P5 verification gate

- [x] Domain/Application tests for registration, occurrence, policy, ownership, authorization origin, and dedupe.
- [x] InMemory/SQLite persistence parity.
- [x] Deterministic scheduler tests using `TimeProvider`.
- [x] API tests for list/update/cancel and ownership checks.
- [x] Tool-policy tests for schedule-management tools.
- [x] Synthetic end-to-end chat scenario:

  ```text
  user asks for one-shot reminder
  → agent creates trigger
  → trigger is visible/listable
  → time advances
  → one normalized occurrence is produced
  ```

- [x] Synthetic recurring scenario including restart and missed-occurrence handling.
- [x] Regression coverage proving existing initiative, interruption, detach/reattach, memory, and tool approval behavior remains unchanged.
- [x] Hosted/provider tests remain optional; P5 scheduler semantics must be fully testable offline.

### P5 stop condition

P5 is complete when:

- durable one-shot and recurring triggers are first-class, persisted resources;
- users can create/manage eligible schedules naturally through the agent;
- explicit user requests do not require redundant confirmation for low-risk scheduling;
- non-user-triggered agent initiative cannot silently create durable schedules;
- schedules survive restart and produce idempotent normalized occurrences;
- ownership is Agent Instance + user rather than reusable definition or live session;
- trigger data does not gain system-instruction authority;
- trigger firing does not grant future tool privileges;
- existing Session Runtime timers remain lifecycle-local;
- P6 has a clean occurrence handoff for unattended execution.

---

# P6 — Durable background work and triggered execution

Introduce durable work when accepted work must outlive the active Session Runtime or when a P5 occurrence must execute while no compatible runtime is alive.

Prerequisites:

- P1 purpose/lifecycle semantics;
- P2 progress/result semantics;
- P3 tool/policy/approval semantics;
- P4 durable Agent Instance + memory ownership;
- P5 normalized durable trigger occurrences.

## WorkItem / durable run

- [x] Introduce durable `WorkItem` when a real accepted workflow requires it. Observed for P5 `AwaitingDurableWork` occurrences. Phase I (Support/Compliance/`sandbox.run` after deactivation) remains not applicable.

Requirements:

- do not duplicate ordinary synchronous tool execution;
- persist execution state/checkpoints;
- cancellation;
- idempotency;
- stale runtime protection;
- initiating user/session/Agent Instance provenance;
- optional TriggerRegistration/TriggerOccurrence provenance;
- bounded retries where appropriate;
- explicit terminal states.

- [x] Create at most one logical triggered execution per accepted occurrence/dedupe key.

- [x] Allow work to continue after session deactivation only when explicitly intended.

- [x] Allow paused/reopened sessions to reconnect to existing work without replaying the original user turn.

- [x] Build scheduled-task execution on P5 occurrences rather than embedding scheduling inside WorkItem itself.

- [ ] Add background research and long-running sandbox jobs only when concrete workflows require them.

## Triggered/headless agent execution

- [x] Define a bounded execution context for an Agent Instance + user without requiring an attached browser session.

- [x] Reuse normal definition, persona, trusted profile, and authorized memory composition.

- [x] Represent the TriggerOccurrence as the current event rather than fabricating a new user message.

- [x] Keep model/tool permissions identical to or stricter than normal interactive execution unless an explicit policy says otherwise.

- [x] Decide result delivery independently from execution ownership. Observed delivery is the Background Work result route, not a chat turn.

  Possible delivery targets include:

  - originating/relevant session;
  - durable notification/inbox item;
  - assistant message;
  - artifact;
  - external integration when separately authorized.

## Approval during detached work

Current P3 approval is live-session oriented. Do not solve detached approval by bypassing it.

- [x] If triggered/background work reaches an operation that requires approval and no live approval surface exists, persist an `AwaitingApproval`-style work state and surface it to the user.

- [x] Resume the same idempotent work after approval rather than starting a duplicate run.

- [x] Do not auto-approve a sensitive action merely because the user previously created the trigger.

- [ ] Standing/delegated future-action authorization remains deferred until a concrete workflow justifies a carefully scoped design.

## Progress/results

- [ ] Reuse P2 progress semantics for live-attached work where appropriate.

- [x] Persist durable WorkItem progress/checkpoints separately from ordinary assistant chat history.

- [x] Persist final result linkage separately from transient progress.

## Session UX

- [x] Revisit pause/deactivation UX after durable work exists. Background Work stays available on paused and ended sessions.

Distinguish:

- user-paused conversation;
- runtime inactivity pause;
- disconnected session;
- scheduled trigger waiting;
- detached work running;
- awaiting approval;
- completed work;
- cancelled work;
- fully stopped session.

### P6 stop condition

Observed: a due P5 occurrence can cause one durable, policy-bounded agent run even when no Session Runtime is attached, and the resulting work can be inspected, cancelled, approved when required, and delivered without replaying the initiating user turn. Freeze waits on the exact-SHA hosted gate and independent review.

---

# P7 — Agent harness / admin mode

The repository already has:

- versioned role environments;
- isolated session workspaces;
- `develop` and `document` composition skills;
- Impeccable UI skill integration;
- durable Agent Instance / definition separation from P4.

Productize harness, identity, memory, and trigger administration only after the runtime contracts are stable.

## Effective harness context

- [ ] Allow the admin surface to inspect relevant non-secret effective configuration.

Never expose:

- API keys;
- raw credentials;
- secret environment variables;
- webhook secrets.

## Admin vs User mode

- [ ] Separate Admin mode from User mode.

### Admin mode

- [ ] Allow bounded inspection/modification of:

  - reusable agent definitions/harness workspace;
  - instructions/configuration;
  - tool/integration configuration;
  - knowledge/assets;
  - validation/tests;
  - behavior previews;
  - durable Agent Instance configuration;
  - trusted identity persona/baseline;
  - memory policy;
  - trigger policy and limits;
  - allowed external/domain trigger sources.

- [ ] Require privileged changes to be explicit operations.

```text
agent prepares proposed operation
→ UI presents operation
→ human approves where required
→ normal policy/tool authorization executes it
```

An admin agent does not bypass policy because it generated the change itself.

### User mode

- [ ] Use an immutable/pinned published Agent Definition version for each session.

- [ ] Bind sessions to the durable Agent Instance where persistent actor continuity is required.

- [x] Give every session its own isolated runtime workspace.

- [ ] Prevent user sessions from mutating source harnesses or trusted identity baseline/persona.

## Definition publishing lifecycle

- [ ] Define:

  - draft;
  - validate;
  - test/evaluate;
  - publish immutable version;
  - rollback/deprecate.

Published versions stay immutable. Changing reusable instructions, tools, policies, trigger capabilities/defaults, or default persona/config publishes a new version rather than mutating a version already pinned by sessions.

## Identity / instance lifecycle

- [ ] Add admin lifecycle for durable Agent Instances built from published definitions.

- [ ] Preserve the distinction between definition upgrade and identity reset.

- [ ] Decide how trusted persona revisions remain reproducible for historical sessions.

### Trigger lifecycle interaction

Preserve these rules:

```text
Definition upgrade:
  existing trigger registrations stay owned by the same Agent Instance + user,
  but future execution must satisfy the effective current policy.

Reset learned memory:
  does not delete trigger registrations.

New/forked Agent Instance:
  does not automatically inherit another instance's trigger registrations.

Deactivate/archive Agent Instance:
  disables or cancels its future trigger execution according to explicit lifecycle policy.

Delete/end user relationship:
  must not leave orphan triggers continuing to fire.
```

## Memory policy/admin

- [ ] Expose P4 memory policy seams.
- [ ] Add **Reset learned memory** operations with explicit scope.
- [ ] Keep identity reset separate from learned-memory reset.

## Trigger policy/admin

- [ ] Expose P5 trigger-policy configuration:

  - allowed trigger types;
  - whether user-requested scheduling is enabled;
  - active-trigger limits;
  - minimum recurrence interval;
  - expiry/horizon limits;
  - allowed external/domain event sources;
  - whether triggered execution is permitted once P6 exists.

- [ ] Allow admins/users with the proper authority to inspect and revoke durable trigger registrations.

- [ ] Keep trigger policy/defaults in configuration; keep individual user trigger registrations in runtime/user state.

---

# P8 — Full harness/platform capabilities and integration extensibility

- [ ] Consolidate the eventual full harness model.

Possible components:

```text
Reusable Agent Definition
- instructions/goals
- runtime configuration
- tools
- policies/permissions
- knowledge
- trigger capabilities/defaults
- workspace template
- validation/evals
- integrations/extensions

Durable Agent Instance
- identity id
- trusted persona/baseline
- active definition/version association
- lifecycle metadata
- memory-policy assignment/overrides where allowed

Runtime/User state
- trusted user profile
- sessions
- learned memory
- trigger registrations
- trigger occurrences
- work items
```

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
  - invalid definition/identity associations;
  - invalid trigger policy/source configuration;
  - unsafe configuration;
  - evaluation/test scenarios.

- [ ] Keep model-provider extensibility, tool-provider extensibility, and trigger-source extensibility as distinct boundaries unless real implementations prove a common abstraction is useful.

---

# P9 — Sandbox evolution

- [x] Docker is the current sandbox implementation.

- [ ] Keep Docker while it satisfies current requirements.

- [ ] Introduce `ISandboxProvider` only when a second implementation is genuinely required.

- [ ] Evaluate OpenSandbox when requirements include remote execution, stronger multi-tenant isolation, pools, faster provisioning, distributed workers, or multiple runtime images.

- [ ] Keep model-facing `sandbox.run` stable while changing implementation providers.

- [ ] Consider Kubernetes only when deployment/scaling requirements justify it.

Do not adopt Kubernetes merely to replace a working Docker sandbox.

---

# P10 — Multi-user/product infrastructure

Do this when Agent Core moves beyond trusted single-owner/local development.

- [ ] Authentication.
- [ ] User/admin authorization and tenancy.
- [ ] Per-user resource ownership and quotas.
- [ ] Enforce tenant ownership across Agent Instance, session, memory, trigger, WorkItem, artifact, and integration resources.
- [ ] Secure external-integration and webhook credential management.
- [ ] Audit history for privileged actions/tools and trigger-policy changes.
- [ ] Public-hosting hardening.
- [ ] Separate host credentials/scopes where browser users must not possess host authority.
- [ ] Add distributed trigger-claim/lease semantics only when multiple scheduler workers are required.
- [ ] Add horizontal/distributed Session Runtime only when single-process ownership becomes a real constraint.

---

# Deferred / optional provider work

These items do not block P5.

## Real P3 provider verification

- [ ] Revisit the known Real/OpenRouter historical-image reread gap separately from P5.

Keep it bounded, credential-gated, provider-specific, and outside default CI.

## Hosted voice verification

- [ ] Run **HOSTED-04**: one actual non-Synthetic end-to-end Voice smoke using an explicitly selected hosted configuration.

## Realtime hosted STT

- [ ] Finish or replace realtime `OpenAiSpeechRecognizer` only when a concrete need exists.

## Native speech-to-speech / realtime reasoning

- [ ] Revisit only if measured latency/quality demonstrates that the composed `STT → text model → TTS` pipeline is insufficient.

---

# Continuous quality work

- [ ] Keep Synthetic/offline verification as the default deterministic path.

- [ ] Keep `main` green before beginning the next architectural slice.

- [ ] Add regression coverage with every lifecycle, response, speech, multimodal, tool, memory, identity, trigger, and background-work change.

- [ ] Maintain Playwright coverage for meaningful user-visible workflows.

- [ ] Make asynchronous/race-sensitive tests deterministic.

  Prefer explicit gates/events and `TimeProvider` over wall-clock sleeps.

- [ ] Keep hosted-provider tests explicitly opt-in.

- [ ] Maintain observability for:

  - model calls and provider/model selection;
  - response/interruption/queue/steer lifecycle;
  - detach/reattach lifecycle;
  - progress lifecycle;
  - speech provider/capability selection;
  - tool calls and pending approvals;
  - sandbox execution;
  - compaction lifecycle;
  - memory retrieval/mutation and scope/owner without sensitive content logging;
  - identity/definition resolution;
  - trigger registration create/update/cancel;
  - trigger type and owner scope without logging sensitive payload content;
  - scheduler due/claimed/fired state;
  - scheduler lag;
  - trigger occurrence dedupe/drop/expiry;
  - missed-occurrence handling;
  - trigger authorization origin;
  - background work lifecycle;
  - policy denials;
  - resource limits.

- [ ] Add startup catalog/provider capability-consistency validation if provider capability configuration becomes independently variable enough that contradictory declarations can occur.

- [ ] Keep docs synchronized with observed implementation.

Current important baselines:

```text
P1 freeze:  dceaccb
P2 freeze:  47d6ff6
P3 freeze:  4dbb920
P4 freeze:  822028f / workflow 35806764609 green
P5 freeze:  4bbc0c1 / workflow 35954811544 green
P6 freeze: 2455de9 (core repair 2067a44 / workflow 36031813141 green) / Manual A on 2455de9 tree
prior P6 freeze: 6900bc1 / workflow 35990145456 attempt 2 (superseded)
next phase: P7 — not started
```

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
- [x] OpenAI-compatible streaming model adapter and OpenRouter configuration.
- [x] Trusted model catalog with per-session model/reasoning selection.
- [x] Provider reasoning separated from public assistant output.
- [x] Validated display/speech response semantics.
- [x] First-class transient progress semantics.
- [x] Durable multi-session catalog/history and paginated conversation history.
- [x] Bounded runtime restore.
- [x] Session purpose/lifecycle/completion policy and terminal read-only history.
- [x] Trusted owner profile API/persistence.
- [x] Session attachments/artifacts and current/historical image access.
- [x] Capability-aware model/image admission.
- [x] Repeated proactive initiative.
- [x] Explicit queue vs intentional steer, Stop, interruption reasons, detach grace, reattach recovery, and approval replay.
- [x] Session-owned isolated workspaces.
- [x] Typed bounded tool execution.
- [x] Workspace, knowledge/attachment, artifact, sandbox, web, generic HTTP, and email tools.
- [x] Live approval UI/protocol.
- [x] Synthetic / Browser / hosted-compatible speech abstractions.
- [x] Voice interruption and heard/received tracking.
- [x] Impeccable UI skill integration.
- [x] Shared `develop` and `document` composition skills.
- [x] P4 semantic compaction and durable summary boundary.
- [x] P4 structured session memory.
- [x] Durable Agent Definition vs Agent Instance separation.
- [x] IdentityUser/User learned-memory scopes and layered prompt composition.
- [x] Durable trigger registration/scheduler — P5 frozen on `4bbc0c1` (workflow `35954811544` green).
- [x] Durable triggered/background execution — P6 closed/frozen on `2455de9` (core repair `2067a44`, workflow `36031813141` green; Manual A on `2455de9` tree).

---

# Next implementation item

**P6 — durable background work and triggered execution** is closed/frozen on `2455de938ad4a156189ea7affcea1ca940cd4ec1` (core repair `2067a44`, workflow `36031813141` green). Faithful Manual A passed 2026-09-25 on the `2455de9` tree. Evidence: `docs/reports/p6-freeze-candidate.md`. P5 remains frozen on `4bbc0c1`.

P6 owns `AwaitingDurableWork` execution, headless/background runs, and unattended delivery. P5 does not include a durable `WorkItem` engine, public webhooks, a calendar UI, or standing approval for later sensitive tools.

**P7** is the next phase and has not started. Do not implement P7.
