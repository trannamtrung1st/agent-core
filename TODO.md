# TODO

Ordered by current dependency and product value.

Reviewed against `main` on **2026-09-25**.

P5 implementation freeze:

```text
4bbc0c17bc54746f87fd211174690659869e3e45
workflow 35954811544 — green
```

P5 is closed/frozen. Evidence: `docs/reports/p5-freeze-candidate.md`.

P6 implementation freeze:

```text
30adaebde4321531448541ba132cb92941d504e7 — final verified P6 tree
workflow 36085265506 — green
bef77d1da7a9464ab56efb66203e1c1deb9e1b2b — last behavior-affecting SHA
aeefffcce0dda0f3dbf392d6af994fd64a309d2b — runtime/UI closure repair
2067a44a1534623dafc7803d14b8833ee1ba7890 — core durable-runtime repair
workflow 36031813141 — green
```

P6 is **closed/frozen**. Do not reopen P6 implementation unless a reproducible regression appears. Evidence: `docs/reports/p6-freeze-candidate.md`.

**Current active phase:** **P7 — agent harness / admin lifecycle** (not started).

P4 implementation freeze:

```text
822028f7cf17e5a978aced4996022e4085c4efa2
workflow 35806764609 — green
```

Detailed historical verification belongs in `docs/reports`. Keep this file focused on current/future work, frozen architectural invariants, and enough baseline context to prevent accidental redesign.

---

# Current roadmap

1. **P0–P6 are closed/frozen.** P6 verified tree **`30adaeb`**, workflow **`36085265506`** green (last behavior **`bef77d1`**; core **`2067a44`**; runtime closure **`aeefffc`**).
2. **P7 — agent harness / admin lifecycle** is the active phase (implementation not started).
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

Do not redesign P1 while productizing administration.

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

Do not overload trusted-profile state with harness configuration or learned memory.

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

Admin mode must use the same policy/authorization boundaries. It does not create a privileged bypass around P3.

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

P7 must productize these seams rather than collapsing them into one generic configuration blob or filesystem.

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
- current-turn, action-specific schedule authorization;
- typed durable application-event ingress;
- owner-scoped dedupe and stale schedule revision rejection;
- trigger payload is evidence/data, not instruction authority;
- trigger registration does not grant later sensitive-tool authority;
- runtime-local timers remain separate from durable scheduling.

P7 may expose trigger policy and trigger management, but must not redesign scheduler semantics.

---

## P6 — Durable background work and triggered execution

Frozen on:

```text
30adaebde4321531448541ba132cb92941d504e7
workflow 36085265506 — green
```

Important frozen P6 contracts include:

- one logical durable execution per accepted occurrence/dedupe key;
- headless execution does not require a live browser Session Runtime;
- TriggerOccurrence remains the event rather than being replayed as a fake user message;
- tool permissions remain identical to or stricter than interactive execution unless explicit policy says otherwise;
- detached work may persist `AwaitingApproval` rather than bypass approval;
- resuming approval continues the same idempotent work;
- final work results/progress are durable and separate from ordinary chat history;
- background work remains inspectable on paused/ended sessions.

Still deferred from P6:

- standing/delegated future-action authorization;
- generalized background research / long-running sandbox work without a concrete workflow;
- additional live-attached progress unification where it becomes useful.

P7 may expose P6 execution policy and work visibility, but must not reopen the durable-work engine without a reproducible regression.

---

# Maintainer notes

Always keep this section.

- [ ] Agent communication, multi-agent orchestration/workflows, and related concepts remain future ideas. Do not pull them into P7.

- [ ] Admin assistant agent (future idea).

- [x] Add proprietary license.

- [x] Establish trusted user/session-context baseline.

- [x] Establish useful general-assistant tool baseline through P3.

- [x] Close current-turn and historical-image usability.

- [x] Keep provider reasoning separate from assistant output.

- [x] Keep Model/Reasoning controls in the active composer.

- [x] Keep reusable Agent Definition separate from durable Agent Instance.

- [x] Keep trusted identity persona/baseline separate from learned memory.

- [x] Keep runtime-local timers separate from durable scheduling.

- [x] Keep trigger registration separate from trigger execution.

- [x] Keep trigger authorization separate from later tool authorization.

- [x] Keep background responses/work that must outlive Session Runtime under P6 durable work.

- [ ] Keep the P7 product surface simple even when the internal model is richer.

  Prefer the admin mental model:

  ```text
  Instructions
  Capabilities
  Resources
  Identity
  Memory
  Automation
  Test & Publish
  ```

  while preserving strict internal ownership and lifecycle boundaries.

---

# P7 — Agent harness / admin lifecycle

## Goal

Turn the existing developer-oriented harness/runtime into a product-level agent administration lifecycle without weakening the runtime contracts already frozen in P1–P6.

The P7 outcome should let an authorized operator who did **not** clone the repository or edit C#:

```text
configure agent
→ validate
→ test/evaluate
→ review changes
→ publish immutable definition version
→ create/manage durable Agent Instance
→ operate memory/identity/automation safely
```

P7 is not the general plugin/platform phase. Keep it focused on making the current Agent Core capabilities manageable through an explicit product surface.

## Core model

Preserve this architecture:

```text
Reusable Agent Definition
  ├── instructions
  ├── capabilities/tool policy
  ├── knowledge/resources
  ├── harness workspace/template
  ├── default runtime configuration
  ├── default persona/configuration
  ├── memory policy
  ├── trigger/execution policy
  └── validation/evaluation scenarios
            │
            │ publish
            ▼
Immutable Agent Definition Version
            │
            ▼
Durable Agent Instance
  ├── identity id
  ├── trusted persona revision
  ├── active/pinned definition association
  ├── lifecycle metadata
  ├── learned-memory ownership
  └── trigger ownership
            │
            ▼
Session
  ├── conversation
  ├── session memory
  ├── isolated mutable runtime workspace
  ├── attachments/artifacts
  └── related WorkItems
```

Do not collapse these into a single generic `agent.json` or shared mutable filesystem.

---

## P7A — Admin shell and effective configuration

### Admin vs User area

- [ ] Add a dedicated **Admin** area inside the existing application.

  Prefer a top-level/sidebar navigation boundary rather than turning normal Chat into a page full of hidden admin controls.

  Conceptually:

  ```text
  /chat/...
  /admin/...
  ```

  P10 later adds full multi-user/RBAC enforcement. P7 still keeps the UX and API boundaries explicit.

- [ ] Keep User mode conversation-first and low-clutter.

- [ ] Prevent ordinary user-session operations from mutating source harnesses, trusted instance identity/persona, or published definitions.

### Effective configuration

- [ ] Add an effective-configuration view for an Agent Definition / Agent Instance.

It should make behavior explainable with non-secret fields such as:

```text
definition/version
instance
persona revision
model/runtime configuration
enabled capabilities/tools
tool policy
knowledge/resources
memory policy
trigger policy
background-execution policy
```

- [ ] Never expose secrets through the effective-config surface:

  - API keys;
  - raw credentials;
  - secret environment variables;
  - webhook secrets;
  - provider tokens.

- [ ] Resolve effective configuration server-side from trusted sources. Do not trust client/model-supplied owner/security metadata.

### P7A verification

- [ ] Admin navigation and route tests.
- [ ] User-mode mutation-denial tests.
- [ ] Effective-config projection tests.
- [ ] Secret-redaction/non-exposure tests.
- [ ] Existing conversation UX regression coverage.

### P7A stop condition

Admin configuration is a distinct product surface, User mode remains conversation-first, and an authorized operator can inspect the effective non-secret configuration that will govern an agent.

---

## P7B — Agent Definition draft / version / publish lifecycle

Implement an explicit publishing lifecycle:

```text
Draft
→ Validate
→ Test / Evaluate
→ Review Diff
→ Publish immutable version
→ Deprecate / Roll back association when needed
```

- [ ] Introduce an editable draft representation separate from published immutable versions.

- [ ] Keep published Agent Definition versions immutable.

Changing any published reusable behavior/configuration should produce a new version, including changes to:

- instructions;
- tool/capability configuration;
- policies/permissions;
- knowledge/resource snapshot;
- harness workspace/template;
- trigger capabilities/defaults;
- default persona/configuration;
- relevant model/runtime defaults.

- [ ] Keep existing sessions reproducible against the definition version they actually used.

- [ ] Define how a durable Agent Instance adopts/upgrades to another published definition version.

- [ ] Do not make definition upgrade equivalent to identity reset.

- [ ] Support deprecating a definition version without rewriting historical sessions.

- [ ] Support moving an instance association back to a prior valid published version where policy permits.

### Publish diff

- [ ] Show a human-readable diff before publishing.

At minimum identify changes in:

```text
instructions
capabilities/tools
permissions/policies
knowledge/resources
harness workspace
default persona/configuration
memory policy
trigger/execution policy
model/runtime configuration
```

Do not require a raw JSON diff as the only review surface.

### P7B verification

- [ ] Draft mutation/versioning tests.
- [ ] Published-version immutability tests.
- [ ] Instance upgrade/rollback association tests.
- [ ] Historical session version-resolution tests.
- [ ] Publish-diff projection tests.
- [ ] Concurrency/revision conflict tests for draft edits and publishing.

### P7B stop condition

Reusable agent configuration has a reproducible draft → validation/test → diff → immutable publish lifecycle, and already-published behavior cannot be silently mutated.

---

## P7C — Harness editing: instructions, capabilities, resources, workspace

Keep the product mental model simple:

```text
Harness
≈ Instructions
+ Capabilities
+ Resources / Workspace
```

Internally, preserve typed boundaries rather than treating everything as files.

### Instructions

- [ ] Add bounded admin editing for reusable definition instructions.

- [ ] Keep instructions authoritative behavior configuration, not a dumping ground for learned memory.

### Capabilities / tools

- [ ] Expose currently supported Agent Core capabilities/tools and their relevant policy configuration.

- [ ] Validate missing/invalid tool references before publish.

- [ ] Preserve P3 registry / policy / execution separation.

- [ ] Tool configuration never carries raw provider credentials into model-visible configuration.

### Knowledge / resources

- [ ] Allow definition-scoped knowledge/resources/assets to be managed as reusable harness material.

Examples:

```text
reference docs
policies
templates
skills/resources
assets
test fixtures
```

- [ ] Preserve provenance/version association for resources included in a published definition.

### Harness workspace

Use a **definition-scoped harness workspace** distinct from the existing session runtime workspace.

Conceptually:

```text
Definition draft harness workspace
  → editable by Admin
  → may contain reusable docs/templates/assets/skills/test fixtures

Published definition harness workspace
  → immutable snapshot/versioned resource set
  → available to runtime according to capability/policy
  → never mutated by ordinary user sessions
```

- [ ] Do not store learned memory, trigger registrations, credentials, or mutable runtime state as ordinary harness-workspace files.

- [x] Continue giving every session its own isolated mutable runtime workspace.

Conceptually:

```text
Published harness resources
        │ read/use according to policy
        ▼
Agent runtime
        │
        ▼
Session workspace
  mutable
  isolated
  session-owned
```

### Explicitly defer persistent Agent Instance workspace

Do **not** add a general mutable cross-session Agent Instance filesystem in P7.

Add it later only when a concrete workflow requires durable files shared across sessions, for example:

```text
"continue updating the same budget.xlsx across future sessions"
```

If/when added, keep it separate from learned memory and definition resources.

### P7C verification

- [ ] Draft/published harness-workspace lifecycle tests.
- [ ] Published resource immutability tests.
- [ ] Session workspace isolation/regression tests.
- [ ] Tests proving user sessions cannot mutate published harness resources.
- [ ] Tests proving memory/trigger/security state is not silently represented as workspace files.

### P7C stop condition

An admin can configure the current agent harness through instructions, capabilities, knowledge/resources, and a versioned harness workspace while session runtime files remain isolated and mutable at the session layer.

---

## P7D — Agent Instance and identity/persona administration

### Agent Instance lifecycle

- [ ] Add admin lifecycle for durable Agent Instances created from published Agent Definitions.

Support explicit lifecycle states/operations sufficient for the current product, such as:

```text
create
activate
change active definition version
deactivate/archive
```

Avoid premature organization/tenant lifecycle complexity.

- [ ] Preserve the distinction between:

```text
definition upgrade
identity/persona revision
learned-memory reset
new/forked Agent Instance
```

None of these implicitly means another.

### Persona editing

Support **two views over one typed schema**:

```text
Form | JSON
```

- [ ] Make the form the default UX.

- [ ] Provide an advanced JSON editor for technical administrators/debugging/import-export.

- [ ] Validate both views against the same server-owned typed schema.

- [ ] Do not accept arbitrary trusted identity fields merely because they appeared in JSON.

- [ ] Persist persona revisions so historical sessions can resolve the trusted persona/configuration they actually used.

### Instance lifecycle interactions

Preserve:

```text
Definition upgrade:
  Agent Instance identity remains the same.
  Existing trigger registrations remain owned by the same Agent Instance + user.
  Future execution must satisfy the effective current policy.

Reset learned memory:
  does not reset identity/persona.
  does not delete trigger registrations.

New/forked Agent Instance:
  does not automatically inherit learned memory.
  does not automatically inherit trigger registrations.

Deactivate/archive Agent Instance:
  prevents future execution according to explicit lifecycle policy.
  must not leave silently firing triggers.

Delete/end user relationship:
  must not leave orphan triggers/work continuing without an owner.
```

### P7D verification

- [ ] Instance create/activate/archive tests.
- [ ] Definition-upgrade vs identity-reset tests.
- [ ] Form ↔ JSON round-trip/schema validation tests.
- [ ] Persona revision/historical resolution tests.
- [ ] Instance deactivation + future-trigger handling tests.
- [ ] Cross-instance ownership/isolation regressions.

### P7D stop condition

Durable Agent Instances can be explicitly managed without conflating reusable definition version, trusted identity/persona, learned memory, or trigger ownership.

---

## P7E — Memory and automation administration

## Memory policy/admin

Keep this authority distinction explicit:

```text
Instructions
= what the agent should do

Trusted persona/profile
= trusted identity/context

Knowledge
= authoritative/reference material

Learned memory
= what the agent learned/remembers
```

Do **not** replace learned memory with instructions and do not silently promote learned memory into instruction authority.

- [ ] Expose P4 memory-policy configuration.

- [ ] Allow authorized inspection of built-in learned memory with safe metadata such as:

  - scope;
  - owner;
  - source/provenance where available;
  - created/updated timestamps;
  - content where policy allows.

- [ ] Add explicit scoped reset/delete operations for learned memory.

At minimum preserve relevant scopes:

```text
Session
IdentityUser
User-wide (when policy enables it)
```

- [ ] Keep identity/persona reset separate from learned-memory reset.

- [ ] Do not initially provide a generic arbitrary editor that rewrites learned memory as if the agent learned something organically.

If manually curated durable facts become necessary, introduce a separate future concept such as:

```text
Trusted Context
Pinned Facts
Admin Context
```

with explicit authority/provenance rather than mixing them into learned memory.

## Trigger / scheduling / background-execution admin

- [ ] Expose P5 trigger-policy configuration:

  - allowed trigger types;
  - whether user-requested scheduling is enabled;
  - active-trigger limits;
  - minimum recurrence interval;
  - expiry/horizon limits;
  - allowed external/domain event sources.

- [ ] Expose whether P6 triggered/headless execution is permitted for the agent/instance where the existing policy model supports it.

- [ ] Allow authorized admins/users to inspect and revoke durable trigger registrations.

- [ ] Keep trigger policy/defaults in reusable/effective configuration.

- [ ] Keep individual trigger registrations/occurrences in runtime/user state, not the definition workspace or learned memory.

- [ ] Trigger configuration never grants standing permission for later sensitive external actions.

### P7E verification

- [ ] Memory policy projection/mutation tests.
- [ ] Scoped learned-memory reset/delete tests.
- [ ] Tests proving memory reset does not reset identity or triggers.
- [ ] Trigger-policy configuration tests.
- [ ] Trigger inspection/revocation ownership tests.
- [ ] Tests proving admin configuration cannot bypass P3/P6 approval policy.

### P7E stop condition

Authorized operators can understand and manage memory and automation policy/state without confusing instructions, trusted identity, learned memory, trigger registrations, or tool authorization.

---

## P7F — Validation, behavior preview, evaluations, and publish gate

Make testing a first-class part of building an agent rather than a developer-only afterthought.

### Validation

- [ ] Validate draft configuration before publish.

Cover at least:

- schema/config validity;
- missing tool/capability references;
- invalid permissions/policies;
- incompatible model/provider capabilities where deterministically knowable;
- invalid definition/instance associations;
- invalid memory/trigger configuration;
- missing/invalid harness resources;
- unsafe or secret-bearing configuration where detectable.

### Behavior preview / test scenarios

- [ ] Add admin-visible behavior-preview/test scenarios.

Conceptually:

```text
Scenario:
  "Customer asks for refund after 45 days"

Expected checks:
  cites/uses relevant policy
  does not perform unauthorized refund
  proposes escalation
```

- [ ] Reuse Synthetic/offline infrastructure for deterministic default evaluation whenever possible.

- [ ] Keep hosted/provider evaluation explicitly opt-in where deterministic offline behavior is sufficient.

- [ ] Store evaluation definitions/results with enough provenance to know which draft/version/configuration was tested.

- [ ] Make failed required validation block publish.

- [ ] Decide a narrow initial policy for evaluation failures:

  - structural/security validation: blocking;
  - optional behavioral evals: report clearly and allow policy to determine whether publish is blocked.

Avoid pretending subjective model behavior can always be reduced to deterministic pass/fail.

### P7F verification

- [ ] Validation unit/application tests.
- [ ] Synthetic behavior-preview end-to-end flow.
- [ ] Publish blocked by invalid configuration.
- [ ] Evaluation provenance/version tests.
- [ ] Hosted tests remain optional unless a provider-specific requirement demands them.

### P7F stop condition

An admin can validate and test a draft agent before publication, and invalid configuration cannot be silently published.

---

## P7G — Basic admin history, rollback/deprecation, and final UX

### Basic admin history

Record a lightweight immutable administrative event trail for important lifecycle changes, for example:

```text
definition draft created
definition published
definition deprecated
instance created
instance definition upgraded/rolled back
persona revised
memory reset
trigger policy changed
trigger revoked
instance archived
```

- [ ] Record actor/source, timestamp, target resource, operation, and safe change metadata.

- [ ] Do not log secrets or raw sensitive payloads merely for audit convenience.

- [ ] Keep this intentionally smaller than P10 enterprise audit/compliance infrastructure.

### Rollback/deprecation UX

- [ ] Allow an authorized admin to inspect prior immutable versions.

- [ ] Allow explicit instance reassociation/rollback to a compatible prior published version.

- [ ] Deprecation must not rewrite history.

### End-to-end Admin UX

- [ ] Provide a coherent Admin flow such as:

```text
Agent Definitions
  → edit draft
  → configure instructions/capabilities/resources
  → validate
  → test
  → review diff
  → publish

Agent Instances
  → create/select instance
  → choose published version
  → configure identity/persona
  → inspect memory/automation
  → activate/archive
```

- [ ] Keep the primary UI understandable without requiring users to understand internal terms such as `IdentityUser`, occurrence dedupe, or SessionRuntime ownership.

### P7G verification

- [ ] Admin-event-history tests.
- [ ] Secret-redaction tests.
- [ ] Publish → instantiate → chat end-to-end scenario.
- [ ] Draft change → test → diff → publish new version → upgrade instance scenario.
- [ ] Rollback/deprecate scenario.
- [ ] Memory reset and trigger revoke scenarios.
- [ ] Regression coverage across P1–P6 runtime behavior.

### P7 stop condition

P7 is complete when an authorized non-developer operator can:

```text
create/edit a reusable agent draft
→ configure instructions, capabilities, knowledge/resources, and harness workspace
→ validate and test behavior
→ review the publish diff
→ publish an immutable definition version
→ create/manage a durable Agent Instance
→ manage trusted persona through form or typed JSON
→ inspect/reset learned memory through explicit scope
→ configure/inspect/revoke eligible automation
→ inspect effective non-secret configuration
→ review basic admin history
→ safely upgrade/rollback/deprecate without rewriting history
```

while:

- User mode cannot mutate published harnesses or trusted identity;
- learned memory remains separate from instructions/trusted context;
- published harness resources remain separate from mutable session workspaces;
- P3 tool authorization and P5/P6 trigger/background-work authorization remain intact;
- secrets never enter model-visible/admin projections unintentionally;
- no persistent Agent Instance filesystem is introduced without a concrete workflow;
- no general plugin ecosystem, workflow builder, multi-agent orchestration, or enterprise tenancy/RBAC is pulled into P7.

---

# Explicit P7 deferrals

Do not expand P7 to include these without a concrete new requirement:

- persistent cross-session Agent Instance workspace/filesystem;
- general plugin marketplace;
- general MCP/provider ecosystem;
- arbitrary custom tool-provider framework;
- visual node/graph workflow builder;
- multi-agent communication/orchestration;
- standing/bulk future-action authorization;
- organization/team management;
- full RBAC/tenancy;
- enterprise-grade audit/compliance;
- generalized distributed scheduler/runtime infrastructure.

These are potential P8/P10 or later concerns.

---

# P8 — Full harness/platform capabilities and integration extensibility

P8 begins only after P7 establishes a usable administration and publishing lifecycle.

## Platform/extensibility

- [ ] Consolidate the eventual extensible harness/provider model when real second implementations justify abstraction.

Potential extension boundaries:

```text
model providers
tool providers
integration providers
trigger/event sources
sandbox providers
```

Do not force them into one common abstraction unless implementations demonstrate a useful shared contract.

- [ ] Add external tool-provider/plugin extensibility only when another real provider/integration justifies it.

MCP-like providers may be adapters.

Requirements:

- native Agent Core tools remain supported;
- external providers still pass through Agent Core policy/authorization;
- provider credentials remain outside model context.

- [ ] Extend P7 validation for provider/plugin-specific concerns:

  - provider availability/capability mismatches;
  - missing external-provider references;
  - provider-specific permission/configuration errors;
  - extension compatibility/versioning;
  - extension-specific evaluation scenarios.

- [ ] Revisit a durable mutable Agent Instance workspace only when a concrete cross-session file workflow requires it.

- [ ] Revisit richer reusable evaluation suites when multiple harness/provider implementations make them valuable.

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
- [ ] Organization/team management when required.
- [ ] Per-user/tenant resource ownership and quotas.
- [ ] Enforce tenant ownership across Agent Definition, Agent Instance, session, memory, trigger, WorkItem, artifact, and integration resources.
- [ ] Secure external-integration/webhook credential management.
- [ ] Expand P7 basic admin history into enterprise-grade audit/compliance where required.
- [ ] Public-hosting hardening.
- [ ] Separate host credentials/scopes where browser users must not possess host authority.
- [ ] Add distributed trigger-claim/lease semantics only when multiple scheduler workers are required.
- [ ] Add horizontal/distributed Session Runtime only when single-process ownership becomes a real constraint.

---

# Deferred / optional provider work

These items do not block P7.

## Real P3 provider verification

- [ ] Revisit the known Real/OpenRouter historical-image reread gap separately from P7.

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

- [ ] Add regression coverage with every lifecycle, response, speech, multimodal, tool, memory, identity, trigger, background-work, definition, publishing, and admin change.

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
  - identity/definition/version resolution;
  - draft/publish/instance lifecycle;
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
P6 freeze:  30adaeb / workflow 36085265506 green (bef77d1 last behavior)
prior P6 freeze: 6900bc1 / workflow 35990145456 attempt 2 (superseded)
active phase: P7 — agent harness / admin lifecycle (not started)
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
- [x] Durable triggered/background execution — P6 frozen on `30adaeb` (workflow `36085265506` green).

---

# Next implementation item

**P7A — Admin shell and effective configuration** is the next implementation slice.

Start with:

```text
dedicated Admin navigation/route
→ Agent Definition / Agent Instance read models
→ safe effective-config projection
→ secret redaction/non-exposure
→ User-mode mutation boundary
```

Then continue in dependency order:

```text
P7A Admin shell/effective config
→ P7B Definition lifecycle
→ P7C Harness editing/resources/workspace
→ P7D Instance + identity/persona
→ P7E Memory + automation admin
→ P7F Validation/evals/publish gate
→ P7G History/rollback/final UX
```

P6 remains frozen on `30adaeb` / workflow `36085265506`. Do not reopen P6 without a reproducible regression.
