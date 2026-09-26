# TODO

Ordered by current dependency and product value.

Reviewed against `main` on **2026-09-26**.

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

P7 implementation freeze:

```text
53d439e880b91bf28e7b2b72a2d7a157b6d3a813 — verified P7 tree (last behavior)
workflow 36228090172 — green
5eea954 — W09 closure bookkeeping (review 0164)
```

P7 is **closed/frozen**. Evidence: `docs/reports/p7-freeze-candidate.md`.

**Current active phase:** **P8 — harness/platform extensibility.**

P4 implementation freeze:

```text
822028f7cf17e5a978aced4996022e4085c4efa2
workflow 35806764609 — green
```

Detailed historical verification belongs in `docs/reports`. Keep this file focused on current/future work, frozen architectural invariants, and enough baseline context to prevent accidental redesign.

---

# Current roadmap

1. **P0–P7 are closed/frozen.** P6 verified tree **`30adaeb`**, workflow **`36085265506`** green (last behavior **`bef77d1`**). **P7 frozen** on **`53d439e`**, workflow **`36228090172`** green (last behavior **`53d439e`**; closure bookkeeping **`5eea954`**) — see `docs/reports/p7-freeze-candidate.md`.
2. **P8 — harness/platform extensibility** is the active phase.
3. **P9 — sandbox evolution when requirements justify it.**
4. **P10 — multi-user/product infrastructure when requirements justify it.**

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

- [x] Keep the P7 product surface simple even when the internal model is richer. *(observed Admin shell; frozen on `53d439e`)*

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

## P7 phase status (frozen)

**P7 is closed/frozen** on verified tree **`53d439e`** (workflow **`36228090172`** green). Work items W01–W08 and whole-phase gate: [p7-freeze-candidate.md](docs/reports/p7-freeze-candidate.md). Slice reports: [P7A](docs/reports/p7a-admin-shell-effective-config.md) · [P7B](docs/reports/p7b-definition-lifecycle.md) · [P7C](docs/reports/p7c-harness-resources-workspace.md) · [P7D](docs/reports/p7d-managed-instance-identity.md) · [P7E](docs/reports/p7e-memory-automation-admin.md) · [P7F](docs/reports/p7f-validation-evals-publish-gate.md) · [P7G](docs/reports/p7g-history-rollback-final-gate.md).

The slice subsections below record **frozen invariants and orientation** only. Original planning checklists are retired here; open work lives under **P8+** or [Explicit P7 deferrals](#explicit-p7-deferrals).

---

## P7A — Admin shell and effective configuration

**Observed (W01, approved `9169bfa`):** dedicated Admin area (`/admin/...`), owner/trusted-local `/api/v2/admin/...` APIs, server-resolved effective configuration with secret redaction, User-mode boundaries (no harness/persona/publication mutation from ordinary chat). Verification: Admin navigation, mutation-denial, projection, and redaction suites in W01/W08 gates.

**Invariants:** Admin is a distinct product surface; User mode stays conversation-first; effective config never exposes secrets; resolution is server-side only.

---

## P7B — Agent Definition draft / version / publish lifecycle

**W02 slice (observed, approved `4a2bf99`):** durable drafts and publications, composite runtime catalog, owner Admin fork/save/publish/deprecate, InMemory/SQLite store parity, session snapshot pinning, and W02 browser gate. See [P7B report](docs/reports/p7b-definition-lifecycle.md). **Later slices own the rest of this section:** harness resources in publications (P7C), managed instance version association (P7D), eval-gated publish and human-readable diff (P7F), and rollback/history UX (P7G).

Implement an explicit publishing lifecycle:

```text
Draft
→ Validate
→ Test / Evaluate
→ Review Diff
→ Publish immutable version
→ Deprecate / Roll back association when needed
```

- [x] Introduce an editable draft representation separate from published immutable versions. *(W02)*

- [x] Keep published Agent Definition versions immutable. *(W02)*

Changing any published reusable behavior/configuration should produce a new version, including changes to:

- instructions;
- tool/capability configuration;
- policies/permissions;
- knowledge/resource snapshot;
- harness workspace/template;
- trigger capabilities/defaults;
- default persona/configuration;
- relevant model/runtime defaults.

- [x] Keep existing sessions reproducible against the definition version they actually used. *(W02 session-snapshot regression)*

- [x] Define how a durable Agent Instance adopts/upgrades to another published definition version. *(observed explicit active-version PATCH; P7G may extend rollback UX)*

- [x] Do not make definition upgrade equivalent to identity reset. *(observed version reassociation preserves persona and instance id)*

- [x] Support deprecating a definition version without rewriting historical sessions. *(W02 metadata-only deprecate)*

- [x] Support moving an instance association back to a prior valid published version where policy permits. *(observed managed rollback/reassociate; P7G `admin-lifecycle` + history tests)*

### Publish diff

- [x] Show a human-readable diff before publishing. *(observed P7F / W06)*

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

- [x] Draft mutation/versioning tests. *(W02 store/API contracts)*
- [x] Published-version immutability tests. *(W02)*
- [x] Instance upgrade/rollback association tests. *(observed P7D + P7G admin-lifecycle and instance version history tests)*
- [x] Historical session version-resolution tests. *(W02 `DefinitionLifecycleSessionSnapshotTests`)*
- [x] Publish-diff projection tests. *(observed P7F)*
- [x] Concurrency/revision conflict tests for draft edits and publishing. *(W02)*

### P7B stop condition

Reusable agent configuration has a reproducible draft → validation/test → diff → immutable publish lifecycle, and already-published behavior cannot be silently mutated. **W02 met the transitional draft → validate → immutable publish slice** (eval/diff gate and full phase stop remain with P7F/P7G).

---

## P7C — Harness editing: instructions, capabilities, resources, workspace

**Observed (W03, approved `e25cd46`):** Admin editing for instructions, capabilities, and definition-scoped resources; publish-time resource binding with hashes; read-only runtime `/agent` harness view vs mutable session `/workspace`; validation before publish; P3 tool/registry separation preserved. See [P7C report](docs/reports/p7c-harness-resources-workspace.md).

**Mental model:**

```text
Harness ≈ Instructions + Capabilities + Resources / Workspace
```

**Invariants:** published harness resources are immutable; session workspaces stay isolated and mutable; learned memory, triggers, and credentials are not harness-workspace files.

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

**Verification (observed):** draft/publish resource lifecycle, immutability, session isolation, and user-session mutation-denial tests in W03/W08 gates (see P7C report).

---

## P7D — Agent Instance and identity/persona administration

**Observed (W04 slice approved `0aa3ad3`):** managed instance create, persona Form|JSON, version reassociation, archive, managed new-chat inventory, pinned persona on sessions and `session.ready`, archive admission for triggers/schedules, IdentityUser isolation/retention regressions. Evidence: [P7D report](docs/reports/p7d-managed-instance-identity.md).

### Agent Instance lifecycle

- [x] Add admin lifecycle for durable Agent Instances created from published Agent Definitions. *(observed W04; approved `0aa3ad3`)*

Support explicit lifecycle states/operations sufficient for the current product, such as:

```text
create
activate
change active definition version
deactivate/archive
```

Avoid premature organization/tenant lifecycle complexity.

- [x] Preserve the distinction between definition upgrade, identity/persona revision, learned-memory reset, and new/forked Agent Instance (none implicitly means another). *(observed W04 regressions)*

### Persona editing

Support **two views over one typed schema**:

```text
Form | JSON
```

- [x] Make the form the default UX. *(observed Admin managed controls)*

- [x] Provide an advanced JSON editor for technical administrators/debugging/import-export.

- [x] Validate both views against the same server-owned typed schema.

- [x] Do not accept arbitrary trusted identity fields merely because they appeared in JSON.

- [x] Persist persona revisions so historical sessions can resolve the trusted persona/configuration they actually used.

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

- [x] Instance create/activate/archive tests. *(AgentInstanceTests, AdminApiTests, journey)*
- [x] Definition-upgrade vs identity-reset tests. *(AgentInstanceTests, occurrence routing)*
- [x] Form ↔ JSON round-trip/schema validation tests. *(Vitest + journey)*
- [x] Persona revision/historical resolution tests. *(pinned revision + session.ready)*
- [x] Instance deactivation + future-trigger handling tests. *(TriggerDurablePolicyTests archive admission)*
- [x] Cross-instance ownership/isolation regressions. *(ManagedInstanceP7DRegressionTests + UserMemoryTests)*

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

- [x] Expose P4 memory-policy configuration (effective policy via definition + instance Admin; observed W05).

- [x] Allow authorized inspection of built-in learned memory with safe metadata such as:

  - scope;
  - owner;
  - source/provenance where available;
  - created/updated timestamps;
  - content where policy allows.

- [x] Add explicit scoped reset/delete operations for learned memory.

At minimum preserve relevant scopes:

```text
Session
IdentityUser
User-wide (when policy enables it)
```

- [x] Keep identity/persona reset separate from learned-memory reset.

- [x] Do not initially provide a generic arbitrary editor that rewrites learned memory as if the agent learned something organically (no Admin learned-memory editor; W05).

If manually curated durable facts become necessary, introduce a separate future concept such as:

```text
Trusted Context
Pinned Facts
Admin Context
```

with explicit authority/provenance rather than mixing them into learned memory.

## Trigger / scheduling / background-execution admin

- [x] Expose P5 trigger-policy configuration (effective via published definition; observed W05):

  - allowed trigger types;
  - whether user-requested scheduling is enabled;
  - active-trigger limits;
  - minimum recurrence interval;
  - expiry/horizon limits;
  - allowed external/domain event sources.

- [x] Expose whether P6 triggered/headless execution is permitted for the agent/instance where the existing policy model supports it (effective-config eligibility; observed W05).

- [x] Allow authorized admins/users to inspect and revoke durable trigger registrations.

- [x] Keep trigger policy/defaults in reusable/effective configuration.

- [x] Keep individual trigger registrations/occurrences in runtime/user state, not the definition workspace or learned memory.

- [x] Trigger configuration never grants standing permission for later sensitive external actions.

### P7E verification

- [x] Memory policy projection/mutation tests (`AdminMemoryServiceTests`, `AdminApiTests`).
- [x] Scoped learned-memory reset/delete tests (`AdminMemoryServiceTests`).
- [x] Tests proving memory reset does not reset identity or triggers (`Reset_session_scope_preserves_definition_persona_profile_transcript_and_triggers`).
- [x] Trigger-policy configuration tests (`TriggerInstancePolicyReconciliationTests`).
- [x] Trigger inspection/revocation ownership tests (`AdminAutomationServiceTests`, `TriggerStoreContractTests`).
- [x] Tests proving admin configuration cannot bypass P3/P6 approval policy (`P7E_detached_sensitive_work_waits_for_approval_without_http_side_effect`).

### P7E stop condition

Authorized operators can understand and manage memory and automation policy/state without confusing instructions, trusted identity, learned memory, trigger registrations, or tool authorization.

---

## P7F — Validation, behavior preview, evaluations, and publish gate

**Observed (W06, approved `03e350a`; execution-final eval isolation on `53d439e`):** layered pure vs resolved validation, required deterministic Synthetic draft evaluation (`IDefinitionDraftSyntheticBehaviorEvaluator` / offline Scripted model), Admin check-type matrix, safe publish diff, exact-revision publish with configuration fingerprint; invalid configuration blocks publish. Hosted provider paths remain opt-in; Synthetic is the key-free gate. See [P7F report](docs/reports/p7f-validation-evals-publish-gate.md).

**Invariants:** validation and eval provenance tie to draft revision; behavioral eval uses offline Synthetic by default; structural/security failures block publish.

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

- [x] Record actor/source, timestamp, target resource, operation, and safe change metadata. *(observed `AdminEvents` + `GET /api/v2/admin/events`; see [P7G report](docs/reports/p7g-history-rollback-final-gate.md))*

- [x] Do not log secrets or raw sensitive payloads merely for audit convenience. *(observed `AdminEventSummaryPolicy` + sentinel tests)*

- [x] Keep this intentionally smaller than P10 enterprise audit/compliance infrastructure.

### Rollback/deprecation UX

- [x] Allow an authorized admin to inspect prior immutable versions. *(observed publication inventory + exact-version read APIs)*

- [x] Allow explicit instance reassociation/rollback to a compatible prior published version. *(observed managed version PATCH + P7G journey)*

- [x] Deprecation must not rewrite history. *(observed metadata-only deprecate + session/history retention)*

### End-to-end Admin UX

- [x] Provide a coherent Admin flow such as:

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

- [x] Keep the primary UI understandable without requiring users to understand internal terms such as `IdentityUser`, occurrence dedupe, or SessionRuntime ownership. *(observed Admin shell copy and scoped memory/automation labels; whole-phase journey green)*

### P7G verification

- [x] Admin-event-history tests. *(see [P7G report](docs/reports/p7g-history-rollback-final-gate.md))*
- [x] Secret-redaction tests. *(summary policy + storage sentinel coverage)*
- [x] Publish → instantiate → chat end-to-end scenario. *(`e2e/admin-lifecycle.spec.ts`)*
- [x] Draft change → test → diff → publish new version → upgrade instance scenario. *(admin-lifecycle journey)*
- [x] Rollback/deprecate scenario. *(admin-lifecycle journey)*
- [x] Memory reset and trigger revoke scenarios. *(admin-lifecycle journey + P7E history mutator tests)*
- [x] Regression coverage across P1–P6 runtime behavior. *(W08 focused filters recorded at `f1017c6`/`662ab35`; see [P7G report](docs/reports/p7g-history-rollback-final-gate.md#w08-local-gate-observed-at-2b967cf); hosted exact-SHA green on `53d439e`; P7 frozen review 0164 / execution final 0169)*

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
active phase: P8 — harness/platform extensibility (P7 frozen on 53d439e)
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

**P8 — harness/platform extensibility** is the next phase. P7 Admin lifecycle is **frozen** — see `docs/reports/p7-freeze-candidate.md`.

Do not reopen P7 without a reproducible regression. Historical P7 slice entry point was:

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
