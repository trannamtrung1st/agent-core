# TODO

Ordered by current dependency and product value.

Reviewed against `main` on **2026-09-28**.

---

# Current state

## Frozen phases

P0–P7 are closed/frozen unless a reproducible regression or a concrete new product requirement requires reopening a boundary.

Important freeze references:

```text
P1: dceaccb
P2: 47d6ff6
P3: 4dbb920
P4: 822028f / workflow 35806764609 green
P5: 4bbc0c1 / workflow 35954811544 green
P6: 30adaeb / workflow 36085265506 green
P7 Harness Admin: follow-up tree 2acb1a8
  last harness behavior: 1090535
  canonical W08 hosted gate: f4107d7 / workflow 36239630112 green
P7 verified follow-up tree (incl. session appendix): 2c4d46f
  last session behavior: 93cb2ab
  final gate/test stabilization: 2c4d46f
  hosted gate: workflow 36336971087 green on 2c4d46f
```

P7 evidence: `docs/reports/p7-freeze-candidate.md`.

P7 owns **Harness Admin W01–W08 only**. The `ConversationTurnExecution` observer-durability correction is **closed** session/runtime bookkeeping (not part of the P7 Admin contract).

Observer-durability closure (do not merge into Harness Admin):

```text
93cb2ab — last behavior-affecting
3e75934 — gate evidence (tests + history merge)
2c4d46f — final stabilization; workflow 36336971087 green
2b3188f → f45ea27 — scope/evidence documentation
```

P7.5 is frozen on `70a5720`. Hosted workflow `36368766449` is green on that SHA. Closure report: `docs/reports/p7.5-freeze-candidate.md`.

## Current active phase

**P8 — harness/platform extensibility.**

P7.6 is frozen on `17d89ae`. Hosted workflow `36427670239` is green on that SHA. Closure report: `docs/reports/p7.6-freeze-candidate.md`.

P7.6 does **not** redefine what P7 or P7.5 previously meant or invalidate their closure evidence.

When P8 begins, start from one concrete provider need and do not add a universal provider interface. This record does not start P8 implementation.

---

# Current roadmap

1. **P0–P7 — closed/frozen.**
2. **P7.5 — architecture consolidation and infrastructure readiness — frozen on `70a5720`.**
3. **P7.6 — Admin usability closure — frozen on `17d89ae`.**
4. **P8 — harness/platform extensibility — next.**
5. **P9 — sandbox evolution when requirements justify it.**
6. **P10 — multi-user + production infrastructure when real hosting/pilot requirements justify it.**

Do not reopen a frozen phase without either:

- a reproducible regression; or
- a concrete new product requirement that belongs there rather than in a later phase.

P7.6 is such a new bounded product requirement. Keep its changes attributable to P7.6 rather than rewriting frozen P7/P7.5 history.

---

# Frozen architectural baseline

Keep these invariants while refactoring.

## Conversation/runtime

- one logical Session owns one conversation;
- one Session Runtime owns mutable conversational state while resident;
- provider callbacks do not mutate runtime state directly;
- detach/reconnect does not create a second logical conversation;
- accepted work must not be silently lost because the browser disconnects;
- historical durable state and transient live streaming remain distinct;
- session runtime state is not stored in Agent Definition configuration.

## Agent lifecycle

Preserve:

```text
Reusable Agent Definition
        ↓ publish
Immutable Agent Definition Version
        ↓
Durable Agent Instance
        ↓
Session
```

Do not collapse these into one mutable `agent.json`.

Definition upgrade, identity/persona revision, learned-memory reset, and creating/forking an Agent Instance remain separate operations.

## Authority/context boundaries

Keep these concepts distinct:

```text
Instructions
= what the agent should do

Trusted persona/profile
= trusted identity/context

Knowledge/resources
= authoritative/reference material

Learned memory
= what the agent learned/remembers

Trigger registrations
= durable future event/runtime state

WorkItems
= durable execution state

Session workspace
= mutable session-owned files
```

Do not silently promote learned memory into instructions or trusted identity.

## Tool/security boundaries

Preserve:

- registry / policy / execution separation;
- exact action approval for sensitive tool execution;
- credentials outside model-visible context;
- trigger registration does not grant future sensitive-tool permission;
- background/headless execution is never a privileged authorization bypass;
- Admin uses the same security/policy boundaries rather than a hidden privileged runtime path.

## Workspace/resource boundaries

Preserve:

```text
Published definition resources
  immutable/versioned

Session workspace
  mutable
  isolated
  session-owned
```

Do not add a general cross-session Agent Instance filesystem without a concrete workflow that requires it.

---

# Maintainer notes

Always keep this section.

- [ ] Agent communication, multi-agent orchestration/workflows, and related concepts remain future ideas. Do not pull them into P7.6/P8 without a concrete requirement.

- [ ] Admin assistant agent remains a future idea.
- [ ] Same identity but can be used in multiple applications, e.g: Sam can be a customer support in app A, but can also be a member in chat app B.
- [ ] Workflow/orchestration remains a future idea.
- [ ] Unattended loop remains a future idea.

- [x] Add proprietary license.

- [x] Establish trusted user/session-context baseline.

- [x] Establish useful general-assistant tool baseline through P3.

- [x] Close current-turn and historical-image usability.

- [x] Keep provider reasoning separate from assistant output.

- [x] Keep Model/Reasoning controls in the active composer.

- [x] Keep reusable Agent Definition separate from durable Agent Instance.

- [x] Keep trusted identity/persona separate from learned memory.

- [x] Keep runtime-local timers separate from durable scheduling.

- [x] Keep trigger registration separate from trigger execution.

- [x] Keep trigger authorization separate from later tool authorization.

- [x] Keep background responses/work that must outlive Session Runtime under P6 durable work.

- [x] Keep the P7 product surface simple even when the internal model is richer.

  Product mental model:

  ```text
  Instructions
  Capabilities
  Resources
  Identity
  Memory
  Automation
  Test & Publish
  ```

- [x] Finish P7.6 Admin authoring usability before widening the P8 extension surface. P7.6 is frozen on `17d89ae`.

- [ ] Keep infrastructure boring until product/hosting requirements justify more complexity.

---

# P7 — Agent harness / Admin lifecycle

**Status: closed/frozen.**

P7 delivered a product-level lifecycle for reusable agents without weakening P1–P6 runtime contracts.

Observed product flow:

```text
create/edit draft
→ configure instructions/capabilities/resources
→ validate/test
→ review diff
→ publish immutable version
→ create/manage durable Agent Instance
→ manage persona
→ inspect/reset eligible learned memory
→ configure/inspect/revoke eligible automation
→ inspect effective non-secret configuration
→ review Admin history
→ upgrade/rollback/deprecate without rewriting history
```

Important P7 contracts:

- Admin is a distinct product surface from normal Chat.
- published definition versions are immutable;
- sessions resolve the definition/version they actually used;
- Agent Instance identity survives definition version reassociation;
- persona revisions are typed and durable;
- learned-memory reset is not identity reset;
- trigger registrations are runtime/user state, not definition files;
- Admin history is append-only lifecycle evidence, not enterprise audit infrastructure;
- validation/evaluation evidence is tied to the relevant draft/version;
- ordinary user sessions cannot mutate published harness resources;
- secrets are not exposed by effective-config/Admin projections.

Detailed P7 slice history belongs in `docs/reports`.

---

# P7.5 — Architecture consolidation and infrastructure readiness

## Goal

Consolidate the architecture **after** P7 has exercised Definition, Version, Instance, Session, Memory, Automation, Admin, evaluation, and background-work boundaries.

P7.5 is intentionally not a redesign milestone.

Use this rule:

> Refactor only where completed P1–P7 behavior demonstrates real architectural pressure.

P7.5 may change implementation structure, names, internal APIs, dependency direction, and persistence plumbing where safe.

P7.5 must not intentionally change user-visible product behavior.

Checklist items use an explicit status. **Completed** means the work is in the tree. **Verified, no change** means the audit found the existing behavior already satisfies the item. **Deferred** means it stays as it is on purpose and is not open P7.5 work. **Not applicable** means the literal wording does not match the accepted architecture.

Observed through `docs/reports/p7.5-freeze-candidate.md`. Hosted workflow `36368766449` is green on `70a5720`:

- Ownership map is in `docs/03-system-architecture.md`. Authoring, Runtime, and Operations are responsibilities, not new projects.
- Effective configuration is shared resolution primitives plus use-case-specific composition. `EffectiveConfigurationComposer` is the Admin projection and the shared memory-policy default, not a universal runtime object. The closure audit found no contradictory model, tool, resource, memory, or trigger default.
- Cohesion audit deferred splits of the large runtime files. History-free instance methods remain because tests still call them.
- Persistence audit kept SQLite, separate stores, explicit transactions, and the single-process scheduler. Recorded in `docs/15-persistence-and-configuration.md`.
- Observability audit kept existing counters and added bounded diagnostics for the silent lifecycle, approval, quota, and Admin paths. Recorded in `docs/17-observability-and-operations.md`.
- Admin draft status, durable publications, and instance identity spacing live in `web/src/app.css`. Chat behavior was rechecked on a Synthetic turn.
- At the P7.5 freeze, the canonical handoff named P8 next. The later P7.6 requirement below is a bounded product follow-up and does not rewrite the P7.5 closure.
- `docs/13` describes the shipped Admin surface through P7G. `.agents/context/PRODUCT.md` and `DESIGN.md` stay aligned with that surface and defer behavior to `/docs`.
- The local Synthetic gate and `./scripts/compose-sqlite-volume.sh` passed. Hosted workflow `36368766449` is green on `70a5720`.

---

## P7.5A — Architecture and ownership inventory

- [x] Update the top-level architecture model so Agent Core is no longer described primarily as only a Session Runtime.

Represent the system explicitly around:

```text
Authoring
  definitions
  drafts
  resources
  validation/evaluation
  publication

Runtime
  agent instances
  sessions
  conversation execution
  tools
  memory

Operations
  triggers
  work items
  approvals
  Admin lifecycle/history

Shared cross-cutting boundaries
  effective configuration
  policy/authorization
  persistence/storage
  observability
```

- [x] Map each durable entity to exactly one primary owner/lifecycle.

At minimum:

```text
AgentDefinitionDraft
AgentDefinitionVersion
AgentInstance
PersonaRevision
Session
ConversationTurnExecution
LearnedMemory
TriggerRegistration
TriggerOccurrence
WorkItem
Approval
AdminEvent
Attachment
Artifact
DefinitionResource
SessionWorkspace
```

- [x] Identify duplicated ownership, ambiguous lifecycle, and write paths that bypass the intended Application boundary.

- [x] Confirm Session Runtime is not becoming the owner of Admin, background-work, or definition lifecycle state.

- [x] Confirm Admin services do not duplicate runtime behavior.

### P7.5A stop condition

Architecture documents and code ownership describe the same system, and every major durable concept has one clear lifecycle owner.

---

## P7.5B — Effective configuration and policy consolidation

- [x] **Completed.** Shared resolution primitives are the composition authority. Inputs may include definition version, instance association, persona revision, model defaults, tool policy, knowledge/resources, memory policy, trigger policy, background-execution policy, and trusted caller/profile context. `EffectiveConfigurationComposer.ComposeAdmin` projects those for Admin. It is not a universal runtime configuration object.

- [x] **Verified, no change.** Chat bind, Admin projection, triggered work, and draft evaluation reuse `MemoryPolicyOf`, `SessionModelBinder.PinDefault`, `ToolCatalog.For`, `ToolPolicy.EvaluateExecution`, `OccurrenceCompatibility`, and `RoleEnvironments` where the semantics match. The closure audit found no contradictory model, tool, resource, memory, or trigger default.

- [x] **Not applicable as one object.** Session bind, durable work, and draft evaluation compose their own state from those primitives. They do not call `ComposeAdmin`. Forcing one configuration object would mix Admin projection, live selection, and detached execution.

- [x] **Verified, no change.** Tool execution stays on `ToolPolicy.EvaluateExecution`. Trigger eligibility stays on `OccurrenceCompatibility`. Memory default stays on `MemoryPolicyOf`. Resource bytes stay on the definition-resource owner. Admin reads project configuration. They do not authorize execution.

- [x] **Completed.** Shared policy decisions are centralized only where the contract is shared. Use-case composition stays at the caller.

- [x] **Verified, no change.** Approval and ownership are rechecked at execution. Detached admission denies session-scoped tools. A stale Admin projection cannot grant a tool or an approval.

### P7.5B stop condition

Effective configuration and policy decisions have explicit ownership and no known parallel implementations that can drift.

---

## P7.5C — Application complexity and domain cleanup

- [x] **Completed.** The cohesion audit identified the large runtime and Admin files. Splits were deferred where a split would not remove a second owner. Recorded in `docs/03-system-architecture.md`.

- [x] **Deferred.** History-free `AgentInstanceService` managed methods remain because tests still call them. Production HTTP and `SessionManager` do not. Retiring them is not P7.5 work.

- [x] **Verified, no change.** `AgentInstanceService` and `AdminAgentInstanceService` are different use cases. `EffectiveConfigurationComposer` and `AdminEffectiveConfigurationResolver` are the assembler and the projection return. Those names stay, and the composer is documented as Admin-specific plus the memory-policy default. No product concept was renamed in this closure.

- [x] **Verified, no change.** Domain stayed on durable concepts and policies. P7.5 did not move provider or storage mechanics into Domain.

- [x] **Verified, no change.** Application stayed on use cases, orchestration, and ports. HTTP mapping, EF, and provider DTOs stayed out of it.

- [x] **Verified, no change.** Infrastructure remains the SQLite, filesystem, Docker, and provider implementation.

- [x] **Verified, no change.** P7.5 did not add a `Common` project, MediatR, a generic repository, an internal event bus, or a microservice split.

### P7.5C stop condition

The modular monolith remains easy to navigate, with fewer accidental dependencies and no speculative architectural layer added solely for P8.

---

## P7.5D — Persistence and infrastructure portability audit

Current development infrastructure should remain the default:

```text
.NET modular monolith
SQLite
local filesystem-backed stores
Docker / Docker Compose
single-process Session Runtime ownership
single-process scheduler/background workers
Synthetic/offline CI by default
optional hosted providers
```

Do **not** migrate to the production stack in P7.5.

### Persistence

- [x] Audit EF/persistence code for accidental SQLite-specific assumptions that would make a future PostgreSQL migration unnecessarily invasive.

- [x] Keep migrations deterministic and repeatable.

- [x] Preserve explicit transaction boundaries around operations that must be atomic.

- [x] Preserve idempotency/retry semantics for accepted turns, trigger occurrences, WorkItems, approvals, publication, and other side-effecting lifecycle operations.

- [x] Avoid using in-memory state as the durable source of truth.

### File/blob storage

- [x] Keep host filesystem paths out of Domain and wire contracts.

- [x] Preserve separate conceptual stores for:

  - attachments;
  - artifacts;
  - definition resources;
  - session workspaces.

- [x] Do not collapse immutable blobs/resources and mutable workspace semantics into one generic file store.

- [x] Keep the current local filesystem implementation while it satisfies development requirements.

### Sandbox

- [x] Keep Docker as the sandbox implementation.

- [x] Do not introduce a new sandbox-provider abstraction unless a real second implementation is being added.

### Scheduler/background workers

- [x] Keep the current single-process scheduler/background-worker model while one process is the real deployment shape.

- [x] Do not add a distributed queue, Redis lease system, Kafka/RabbitMQ, or distributed scheduler simply because durable WorkItems exist.

### Secrets

- [x] Continue using environment/user-secrets/gitignored local configuration for development credentials.

- [x] Keep secrets outside model-visible configuration, effective-config projection, Admin history, and logs.

### P7.5D stop condition

The current dev stack remains simple, while core/application behavior is not unnecessarily coupled to SQLite, local disk, Docker-host paths, or single-process implementation details.

---

## P7.5E — Observability and operational shape

Do not deploy a large observability platform yet, but keep instrumentation production-ready.

- [x] Maintain structured logs/metrics/traces for:

  - model/provider selection and calls;
  - session/response/execution lifecycle;
  - interruption/queue/steer/detach/reattach;
  - tool calls and approvals;
  - sandbox execution;
  - compaction/memory mutation;
  - definition/version/instance resolution;
  - draft/publish/rollback/deprecation;
  - trigger registration/occurrence/scheduler lag;
  - WorkItem lifecycle;
  - policy denials;
  - resource limits.

- [x] Avoid sensitive content in operational telemetry unless explicitly required and protected.

- [x] **Verified, no change.** `GET /health` returns `status`, `profile`, and `protocolVersion` after persistence initialization. That is the readiness response for native, Compose, and CI. No second health service was added.

- [x] **Verified, no change.** Instruments use the BCL `ActivitySource` and `Meter` named `AgentCore.Runtime`. No OpenTelemetry package is referenced. `Observability:OtlpEnabled` defaults false and, when true, requires an absolute endpoint without attaching an exporter. A later host can listen to the existing meter and activity source. Local and Synthetic runs do not need a collector.

### P7.5E stop condition

Operational events are structured enough for future hosted diagnostics without requiring a production monitoring stack during development.

---

## P7.5F — UI review and closure evidence

The phase outcome is the A–E consolidation above, the shared Admin spacing slice, then documentation and context sync. It is not a separate refactor-only gate.

- [x] Shared Admin spacing for draft status, publications, and instance identity uses the 8/12/16 tokens in `web/src/app.css` (`b0c2e09`).

- [x] Run the complete deterministic test suite after architectural changes.

- [x] Run meaningful Synthetic Playwright journeys covering:

  - chat;
  - reconnect/durable streaming;
  - tools/approval;
  - memory;
  - automation;
  - background WorkItems;
  - Admin draft/publish/instance lifecycle.

- [x] Keep behavior changes out of refactor commits where practical.

- [x] **Completed** for the owners that changed: `docs/03-system-architecture.md`, `docs/04-backend-interfaces.md`, `docs/13-frontend-implementation-spec.md`, `docs/15-persistence-and-configuration.md`, `docs/17-observability-and-operations.md`, and the implementation-plan/report references. **Verified, no change** for `docs/10-technology-decisions.md` and `docs/11-repository-structure.md`: no technology or repository-boundary decision changed.

- [x] **Completed.** The 2026-09-28 Admin visual audit reviewed home, the definition editor, and an instance error at desktop and 390px, including loading, error, validation, dirty draft, delete and discard confirmations, and keyboard focus. One shared inventory row placed the status tag beside a short description and below a longer one. `.admin-inventory-row-description` now takes the full row so the tag stays on the next line. Deprecate and archive confirmations use the same confirmed-dialog pattern; this disposable catalog had no durable publication or managed instance, so those two dialogs did not render.

- [x] Record a P7.5 closure report with:

  - what architectural pressure was found;
  - what was refactored;
  - what was deliberately left unchanged;
  - which potential P8 abstractions are now justified;
  - which abstractions remain speculative and therefore deferred.

### P7.5 stop condition

P7.5 is complete when:

```text
P1–P7 behavior remains green
architecture docs match implementation
effective configuration has a clear authority path
policy ownership is explicit
major lifecycle boundaries remain distinct
persistence/storage dependencies remain replaceable enough
no premature production infrastructure was added
P8 extension seams are evidence-based rather than speculative
```

---

# P7.6 — Admin usability closure

**Status: ACTIVE.**

## Goal

Finish the Admin authoring and lifecycle UX for the model that already exists before widening Agent Core with P8 extensions.

P7.6 is intentionally bounded:

```text
complete Definition authoring
explicit Definition / Instance creation
clear Definition/version semantics
clear Knowledge Source ↔ Resource binding
multi-file + folder resource ingestion
final Admin UX/test/documentation closure
```

P7.6 may make small model/API corrections where the current product surface exposes a real mismatch. It must not become an architecture redesign or a platform/plugin phase.

## Current-state facts to preserve

The current implementation already establishes:

- `AgentDefinitionCandidate` as the mutable draft payload and immutable `AgentDefinition` as the published payload;
- durable new-draft creation and forked-draft creation;
- immutable published Definition versions;
- managed Agent Instance creation against a published version;
- typed Persona revision and Active/Archived lifecycle;
- Definition resources with immutable content and draft/publication bindings;
- validation, evaluation, diff, and publish gates tied to draft revision/fingerprint;
- read-only Admin effective configuration;
- resource limits of 8 MiB/item, 64 items/draft, and 64 MiB aggregate;
- existing single-file resource upload;
- Knowledge Sources as semantic identity/title/citation references;
- current legacy knowledge resolution by implicit `knowledge/{identity}` logical path.

Do not duplicate these concepts to implement the new UX.

---

## P7.6A — Complete Definition Form + Advanced JSON authoring

- [ ] Expose structured editing for the complete supported `AgentDefinitionCandidate`.

At minimum:

```text
Identity
Goals
System Instructions

Behavior Policy
Conversation Policy
Initiative Policy

Model Defaults
Provider Preferences
Voice

Memory Policy
Trigger / Automation Policy

Environment / Capabilities
Metadata
```

- [ ] Keep the existing product mental model understandable. Do not create one top-level tab for every low-level policy object.

A suitable shape is:

```text
Definition
  Form | Advanced JSON

  Identity & goals
  Instructions
  Behavior & conversation
  Initiative
  Model & providers
  Voice
  Memory policy
  Automation policy
  Advanced / metadata

Capabilities
  Harness
  Tools
  Knowledge Sources
  Workspace / attachments

Resources

Test & Publish
```

- [ ] Form and Advanced JSON must edit **one candidate draft**, not two parallel representations.

Required behavior:

```text
Form edit
  ↓
same local candidate
  ↓
JSON view

JSON edit
  ↓ parse
same local candidate
  ↓
Form view
```

- [ ] Switching Form/JSON views must not silently discard valid unsaved edits.

- [ ] Invalid JSON must remain a local editor error. It must not mutate the saved draft or advance its revision.

- [ ] Saving from either view must use the same draft `expectedRevision` conflict protection.

- [ ] Both views must use the same server-side persistence validation, publication validation, evaluation, diff, and publish path.

- [ ] The structured Form must not silently erase supported candidate fields merely because a particular section was not opened or edited.

- [ ] Definition ID remains immutable after draft creation.

- [ ] Keep secret detection and configured provider/tool validation authoritative on the server.

- [ ] Do not turn the Admin candidate editor into an alternate effective-runtime-configuration editor.

### P7.6A acceptance

A maintainer can create/open one draft, change every supported candidate area through the Form or JSON, switch repeatedly between both views without loss, save once, validate/evaluate, review the resulting diff, and publish through the existing immutable publication flow.

---

## P7.6B — Explicit New Definition / New Instance flows

### New Definition

- [ ] Add **`+ New definition`** on Admin home.

- [ ] Creating a new Definition must not require forking an existing version.

- [ ] Preserve the current domain requirement that a persisted `AgentDefinitionCandidate` has a structurally valid required graph.

Do **not** weaken published/domain invariants merely to store null-filled UI drafts.

- [ ] Create a canonical server-owned starter candidate for a new Definition.

Requirements:

- the user supplies a valid new Definition ID;
- the server owns starter defaults rather than the browser hardcoding provider/policy internals;
- the starter is structurally persistence-valid;
- it opens immediately as a normal `SourceKind.New` draft;
- the user still passes the ordinary save/validate/evaluate/diff/publish flow;
- future starter-default changes do not affect already-created drafts.

### New Instance

- [ ] Add **`+ New instance`** on Admin home.

Flow:

```text
choose logical Definition
→ choose published version
→ choose Definition-default persona or Custom persona
→ create durable managed Agent Instance
→ open Instance Admin
```

- [ ] Default version selection should prefer the newest non-deprecated usable publication. Do not invent a stored `defaultVersion` pointer solely for this UI.

- [ ] Deprecated versions may remain explicitly reachable where current lifecycle semantics require it, but must not be the normal/default target for new managed work. Show a warning when explicitly selected.

- [ ] If Custom persona is supplied during creation, persist it **atomically with instance creation** rather than creating a default-persona instance and immediately patching it.

- [ ] Record one coherent Admin lifecycle event/history outcome for custom-persona creation rather than an accidental create-then-edit sequence.

- [ ] Keep `Start managed chat` as a convenience action:

```text
create managed instance
→ create Session
→ open Chat
```

It must not remain the only obvious managed-instance creation path.

- [ ] Keep Instance hard-delete out of P7.6. Lifecycle remains Active/Archived.

### P7.6B acceptance

Admin home independently supports creating a reusable Definition draft and a durable managed Instance. Starting a managed chat remains a shortcut rather than a hidden CRUD mechanism.

---

## P7.6C — Definition/version UX semantics

- [ ] Keep one inventory row per logical Definition.

- [ ] Continue showing drafts/publications/versions hierarchically under that Definition rather than as separate logical Definitions.

- [ ] Clearly distinguish factual version concepts:

```text
latest version
latest active / usable version
deprecated historical version
instance-pinned active version
```

- [ ] Do **not** add a persistent “default version” concept unless an independent runtime/product requirement requires one.

- [ ] When the numerically latest version is deprecated, the UI must not imply that it is the preferred version for new work.

Example:

```text
Customer Support

Latest         v6 · Deprecated
Latest active  v5 · Active
```

- [ ] Deprecated versions remain visible and explicitly forkable.

- [ ] Deprecated versions must not be the default fork source while a non-deprecated version exists.

- [ ] Preserve exact historical version lookup and existing Session/Instance associations.

- [ ] Keep built-in and durable source/status provenance visible where it helps explain lifecycle behavior without making each source a separate Definition.

### P7.6C acceptance

A maintainer can tell, without knowing persistence internals, which logical Definition they are viewing, which version is numerically latest, which version is suitable for new work, what an existing Instance is pinned to, and which historical versions are deprecated.

---

## P7.6D — Make Knowledge Source ↔ Resource binding explicit

The current implicit relationship is:

```text
Knowledge Source identity
  ↓ convention
knowledge/{identity}
```

This becomes too opaque once Admin supports arbitrary file/folder resource ingestion.

- [ ] Extend the Knowledge Source binding with an explicit optional logical resource path.

Conceptually:

```text
Knowledge Source
  identity: refund-policy
  title: Refund Policy
  citation: refund-policy@v3
  resourcePath: knowledge/refund-policy.md
```

- [ ] Use normalized logical path as the Definition-level binding. Do not use draft `ResourceId` as the durable semantic reference.

Rationale:

```text
ResourceId
= storage/lifecycle identity

Logical path
= portable Definition/package reference
```

- [ ] Preserve backward compatibility for existing Definitions that omit `resourcePath`.

Legacy fallback:

```text
resourcePath missing
→ resolve knowledge/{identity}
```

- [ ] Put explicit path resolution/fallback behind one shared authoritative helper so runtime resolution and draft validation cannot drift.

- [ ] Validation must require the resolved path to bind a resource of `Kind = Knowledge`.

- [ ] Keep the existing textual Knowledge media-type rules.

- [ ] Admin Knowledge Source UX should select from currently bound Knowledge resources instead of looking like an unrelated parallel system.

Suggested interaction:

```text
Knowledge resource
[ knowledge/refund-policy.md ▼ ]

Identity
refund-policy

Title
Refund Policy

Citation
refund-policy@v3
```

- [ ] Identity remains the stable semantic/tool-facing identity. Title remains human-readable metadata. Citation remains the citation label. `resourcePath` identifies packaged content.

- [ ] Removing or changing a resource used by a Knowledge Source should produce an obvious draft validation blocker. Do not silently rewrite or delete the semantic Knowledge Source.

- [ ] Published versions freeze the candidate binding and the matching immutable publication resource set together.

### P7.6D acceptance

A maintainer can inspect a Knowledge Source and immediately identify the packaged resource that backs it. Old Definitions using the implicit convention still behave the same.

---

## P7.6E — Multi-file and folder resource ingestion

Keep the existing single-file upload.

Add:

- [ ] multi-file upload;
- [ ] folder selection where the browser supports it;
- [ ] folder drag-and-drop where supported;
- [ ] preservation of safe relative logical paths.

### Kind inference

Infer `Kind` from conventional top-level folders:

```text
knowledge/*   → Knowledge
templates/*   → Template
references/*  → Reference
assets/*      → StaticAsset
eval/*        → EvalFixture
```

This is a convenience inference, not a new runtime storage architecture.

- [ ] Allow per-file Kind correction before binding.

- [ ] Allow per-file logical-path correction before binding.

- [ ] Do not infer a dangerous or ambiguous path silently. Path normalization/traversal checks remain server-authoritative.

### Bulk preview

- [ ] Show a preview before draft mutation.

Example:

```text
Path                              Kind
knowledge/refund-policy.md        Knowledge
templates/reply.md                Template
assets/logo.png                   StaticAsset
```

The preview should surface at least:

- duplicate/colliding logical paths;
- unsupported/invalid paths;
- inferred Kind;
- file size;
- media type when useful;
- known item/aggregate-limit violations.

### Revision and atomicity

Do not implement folder import as many competing resource metadata writes using the same draft revision.

Use a coherent batch-bind operation:

```text
select files/folder
       ↓
build + edit manifest
       ↓
upload/hash content
       ↓
single batch resource bind against expected draft revision
       ↓
one resulting draft revision
```

- [ ] Content payload upload may remain individually hashed/deduplicated.

- [ ] The final draft resource-binding mutation must use one `expectedRevision`.

- [ ] The final batch bind should be atomic for draft resource metadata: either the accepted manifest binds coherently or no metadata subset is committed.

- [ ] One successful batch bind advances the draft revision coherently rather than once per file.

- [ ] Server-side item, byte, aggregate, media-type, path, duplicate, and secret-content checks remain authoritative.

### Failures

- [ ] Report per-file content-upload failures clearly.

- [ ] Allow safe retry of failed content uploads without duplicating already-addressed immutable content.

- [ ] Distinguish content-upload failure from final draft-bind/revision failure.

- [ ] A stale revision on final bind must not leave a half-bound draft manifest.

- [ ] After a successful bind, refresh the draft/revision and resource list from the server.

### P7.6E acceptance

A maintainer can drop/select a small folder tree, see the exact paths and inferred kinds that will enter the Definition, correct mistakes, bind the manifest under one coherent revision, and understand any individual upload or final-bind failure.

---

## P7.6F — Instance Admin polish

Preserve the existing conceptual split:

```text
Instance management
  Persona Form | JSON
  Active version
  Lifecycle

Memory & automation

Effective configuration
```

- [ ] Keep Instance Persona Form and Persona JSON as two views over mutable **instance-owned persona data only**.

- [ ] Do not expose the full persistence record as editable Instance JSON.

- [ ] Keep revisions, timestamps, compatibility flags, memory, trigger registrations, WorkItems, and other lifecycle state outside Persona JSON.

- [ ] Keep Effective Configuration read-only.

- [ ] Effective Configuration remains a resolved, secret-safe projection. It must not become a universal editable runtime object.

- [ ] Keep Active/Archived lifecycle and active-version reassociation explicit and revision-protected.

- [ ] Continue distinguishing Definition identity/default persona from durable Instance persona revisions.

### P7.6F acceptance

Editing instance-owned state cannot accidentally mutate Definition configuration or runtime-derived Effective Configuration, and viewing Effective Configuration cannot grant or change runtime authority.

---

## P7.6G — UX, testing, documentation, and freeze

### UX review

- [ ] Perform a complete Admin UX review after behavior is implemented, using the established Impeccable workflow/skill.

Review at least:

```text
Admin home
new Definition flow
Definition Form
Advanced JSON
Capabilities
Knowledge Source binding
single/multi/folder resource upload
bulk preview
Test & Publish
new Instance flow
Instance Persona Form/JSON
version/lifecycle controls
Memory & automation
Effective Configuration
```

- [ ] Review desktop and narrow/mobile layout, including approximately 390 px width.

- [ ] Review keyboard navigation and visible focus.

- [ ] Review loading, empty, error, retry, stale-revision, invalid-JSON, validation-blocked, dirty-draft, destructive-confirmation, upload-failure, and partial-content-upload states.

- [ ] Do not hide required lifecycle semantics behind hover-only UI.

- [ ] Keep user-visible terminology aligned with the Definition → Version → Instance → Session model.

### Automated verification

- [ ] Add/extend Domain/Application/API tests for:

  - complete candidate round-trip;
  - canonical new-Definition starter;
  - new managed Instance with default persona;
  - new managed Instance with atomic custom persona;
  - deprecated-version selection rules where enforced;
  - explicit Knowledge Source `resourcePath`;
  - legacy implicit Knowledge Source fallback;
  - Knowledge Kind/media validation;
  - batch resource bind atomicity;
  - stale batch revision;
  - duplicate/path/size/count/aggregate-limit rejection.

- [ ] Add/extend frontend tests for:

  - Form ↔ JSON round-trip without data loss;
  - invalid JSON isolation;
  - dirty-state behavior;
  - new Definition flow;
  - new Instance flow;
  - Definition/version labels;
  - Knowledge resource selection;
  - Kind/path inference and correction;
  - bulk preview;
  - upload and bind failure reporting;
  - Persona JSON scope;
  - Effective Configuration read-only behavior.

- [ ] Add meaningful Synthetic Playwright coverage for the whole P7.6 happy path:

```text
New Definition
→ edit Form
→ inspect/edit JSON
→ bind resources from multi/folder flow
→ bind Knowledge Source
→ validate/evaluate/diff
→ publish
→ New Instance with custom persona
→ inspect Effective Configuration
→ create/open managed chat
```

- [ ] Add focused Synthetic E2E for stale/conflict/error states that are practical at the browser boundary.

- [ ] Re-run the complete deterministic solution/frontend test suite.

- [ ] Re-run Compose/SQLite smoke coverage because P7.6 touches durable Admin/resource lifecycle.

- [ ] Keep hosted providers opt-in; P7.6 closure must not depend on a hosted model merely to prove Admin CRUD/resource semantics.

### Documentation and design sync

- [ ] Update `docs/13-frontend-implementation-spec.md` for the final Admin information architecture and interactions.

- [ ] Update architecture/backend/persistence docs where the explicit Knowledge Source resource-path binding or batch resource mutation changes a contract.

Likely owners include:

```text
docs/03-system-architecture.md
docs/04-backend-interfaces.md
docs/13-frontend-implementation-spec.md
docs/15-persistence-and-configuration.md
```

Update `docs/10-technology-decisions.md` only if a real technology/architecture decision changed.

- [ ] Sync `.agents/context/PRODUCT.md` and `.agents/context/DESIGN.md` after the observed UI is final. `/docs` remains authoritative for detailed behavior.

- [ ] Sync the design system/tokens/components after the final Impeccable review rather than before observed interaction patterns settle.

- [ ] Record a bounded `docs/reports/p7.6-freeze-candidate.md`.

The report should identify:

- behavior added;
- small model/API corrections made;
- compatibility behavior retained;
- explicit non-goals/deferred work;
- deterministic test evidence;
- Synthetic E2E evidence;
- Compose evidence;
- hosted CI candidate SHA/workflow.

### P7.6 stop condition

P7.6 is complete when:

```text
the entire supported Definition candidate can be authored through Form or JSON
Form and JSON operate on one revisioned draft without loss
Admin exposes explicit New Definition and New Instance flows
published versions remain immutable
Definition/version semantics are understandable without a new default-version concept
Knowledge Sources visibly and durably bind to Knowledge resources
legacy implicit knowledge bindings still work
single-file, multi-file, and folder resource ingestion share one safe resource model
bulk resource binding is revision-safe and not half-applied
Instance JSON remains persona-scoped
Effective Configuration remains read-only
Admin UX has been reviewed/polished and design context is synchronized
deterministic tests + Synthetic E2E + Compose are green
hosted CI is green on the recorded closure candidate
```

Observed: P7.6 is frozen on `17d89ae`. Hosted workflow [`36427670239`](https://github.com/trannamtrung1st/agent-core/actions/runs/36427670239) is green on that SHA. Synthetic offline gates and Compose smoke both succeeded. Closure report: `docs/reports/p7.6-freeze-candidate.md`. The commit that records this URL is documentation only.

---

## P7.6 non-goals / deferred platform work

Do **not** pull these into P7.6:

- plugins / external provider framework;
- plugin marketplace;
- persistent Agent Instance filesystem;
- ZIP/package import/export format;
- Git repository resource sync;
- remote filesystem browsing;
- remote object-storage browsing;
- live folder sync/watch;
- FTP/SFTP;
- multi-agent communication/orchestration;
- visual workflow builder;
- distributed job/queue infrastructure;
- production PostgreSQL/Redis/Kubernetes migration.

These remain P8 or later and must be justified by a concrete requirement.

---

# Infrastructure stance before production

Until a real external pilot/hosting requirement appears, prefer:

```text
SQLite
local filesystem
Docker / Compose
single application process
single scheduler/background-worker process
Synthetic deterministic CI
optional hosted model/speech/integration providers
```

Do not add by default:

```text
PostgreSQL
Redis
Kafka / RabbitMQ / SQS
Kubernetes
service mesh
distributed cache
distributed scheduler
remote object storage
cloud secret manager
multi-node SignalR
microservices
```

This is not a prohibition on those technologies.

It is a requirement that each infrastructure dependency solve a real product/operational problem before becoming part of the base stack.

Production-shaped properties that **should** exist now:

- durable source-of-truth state where required;
- explicit ownership IDs;
- idempotent/retry-safe side effects;
- deterministic migrations;
- cancellation/timeouts;
- bounded work;
- secrets outside model context;
- storage/provider ports where an actual boundary already exists;
- health checks;
- structured observability;
- clear transactional boundaries;
- no Domain/Application dependence on host paths.

---

# P8 — Harness/platform extensibility

**Status: next.**

P7.6 is frozen on `17d89ae`. Hosted workflow `36427670239` is green on that SHA.

When P8 begins, start from one concrete provider need.

## Goal

Make the established Agent Core harness extensible without turning every implementation detail into a plugin API.

## Platform/extensibility

- [ ] Use P7.5 findings to identify extension seams backed by real second implementations.

Potential boundaries:

```text
model providers
tool providers
integration providers
trigger/event sources
sandbox providers
```

Do not force these into one generic provider abstraction.

- [ ] Add external tool-provider/plugin extensibility only when another real provider/integration justifies it.

MCP-like providers may be adapters.

Requirements:

- native Agent Core tools remain first-class;
- external tools still pass through Agent Core policy/authorization;
- provider credentials remain outside model context;
- provider-specific DTOs do not leak into Domain/Application contracts;
- extension failure does not corrupt durable agent/session state.

- [ ] Define extension identity/versioning/compatibility only to the degree required by actual extensions.

- [ ] Extend P7/P7.6 validation for provider/plugin-specific concerns when a real extension exists:

  - provider availability/capability mismatches;
  - missing provider references;
  - provider-specific permissions/configuration;
  - compatibility/versioning;
  - extension-specific validation/evaluation.

- [ ] When an extension is added, reuse the shared resolution primitives for any decision whose semantics should match. Do not introduce a second policy for that decision, and do not fold the extension into one universal configuration object.

- [ ] Revisit a durable mutable Agent Instance workspace only when a concrete cross-session file workflow requires it.

- [ ] Revisit richer reusable evaluation suites when multiple harness/provider implementations make them useful.

## P8 non-goals

Do not automatically turn P8 into:

- a plugin marketplace;
- visual workflow builder;
- multi-agent orchestration engine;
- generic distributed job platform;
- enterprise tenant/RBAC implementation;
- Kubernetes migration;
- microservice decomposition.

---

# P9 — Sandbox evolution

- [x] Docker is the current sandbox implementation.

- [ ] Keep Docker while it satisfies current requirements.

- [ ] Introduce `ISandboxProvider` only when a second implementation is genuinely required.

- [ ] Evaluate OpenSandbox when requirements include:

  - remote execution;
  - stronger multi-tenant isolation;
  - pools/faster provisioning;
  - distributed workers;
  - multiple runtime images;
  - remote resource controls.

- [ ] Keep model-facing `sandbox.run` stable while changing implementation providers.

- [ ] Consider Kubernetes only when deployment/scaling requirements justify it.

Do not adopt Kubernetes merely to replace a working Docker sandbox.

---

# P10 — Multi-user and production infrastructure

Start this phase when Agent Core moves beyond trusted single-owner/local development or begins a real external hosted pilot.

Do not start all P10 infrastructure at once. Add it in dependency order.

---

## P10A — Authentication, authorization, and tenant ownership

- [ ] Authentication.

- [ ] User/Admin authorization.

- [ ] Tenant/organization model only when required.

- [ ] Per-user/tenant resource ownership and quotas.

- [ ] Enforce ownership across:

  - Agent Definition;
  - Agent Definition Version;
  - Agent Instance;
  - Session;
  - memory;
  - triggers;
  - WorkItems;
  - approvals;
  - attachments/artifacts;
  - resources;
  - integrations.

- [ ] Remove trusted-local assumptions from externally reachable paths.

- [ ] Separate browser/user credentials from host/service authority.

---

## P10B — Production persistence

Expected first major production infrastructure migration:

```text
SQLite
  ↓
PostgreSQL
```

Trigger this when real requirements include concurrent external users, stronger production operations, multi-process services, or hosted reliability needs.

- [ ] Add PostgreSQL provider/configuration.

- [ ] Verify migrations and transaction semantics on PostgreSQL.

- [ ] Verify concurrency/revision protections under real database concurrency.

- [ ] Keep SQLite available where useful for local/Synthetic development unless maintaining both becomes counterproductive.

- [ ] Define backup/restore policy and verify restore, not only backup creation.

Do not introduce a new generic persistence architecture solely for the provider swap if EF Core boundaries are already sufficient.

---

## P10C — Production object storage and secret management

Add only when local-disk assumptions no longer fit deployment.

Potential migration:

```text
attachments/artifacts/immutable resources
local disk
    ↓
S3 / Azure Blob / compatible object storage
```

- [ ] Preserve distinct workspace semantics rather than treating mutable session workspace as an ordinary immutable blob.

- [ ] Add lifecycle/retention policy where required.

- [ ] Add production secret management when deployment requires it.

Potential implementations:

```text
cloud secret manager
Vault-like service
platform-managed secrets
```

Credentials must remain outside model context and normal Admin projections.

---

## P10D — Hosted operations

When externally hosted:

- [ ] TLS/reverse-proxy/ingress hardening.

- [ ] Production logging/metrics/tracing backend.

- [ ] OpenTelemetry export where useful.

- [ ] Alerting for meaningful failure conditions.

- [ ] Backup + restore drills.

- [ ] Deployment rollback strategy.

- [ ] Rate limits/resource quotas where required.

- [ ] Public-hosting security review.

- [ ] Expand P7 Admin history into enterprise-grade audit/compliance only where required.

---

## P10E — Distributed scaling only when load requires it

Do not assume this is required for the first production deployment.

Potential future needs:

```text
multiple API nodes
multi-node SignalR
distributed scheduler/work claims
worker pools
shared ephemeral coordination
horizontal Session Runtime
```

Only then evaluate:

- [ ] Redis or another coordination/cache technology where a specific use case exists.

- [ ] SignalR backplane when multiple realtime nodes require it.

- [ ] Distributed trigger/work claiming/leases.

- [ ] Queue/broker infrastructure when durable DB-backed work is no longer sufficient.

- [ ] Horizontal/distributed Session Runtime only when single-process ownership is a measured bottleneck.

- [ ] Kubernetes only when deployment/scale/isolation requirements justify it.

Avoid:

```text
Redis because sessions exist
Kafka because events exist
Kubernetes because Docker exists
microservices because modules exist
```

---

# Deferred / optional provider work

These items do not block P7.6.

## Real P3 provider verification

- [ ] Revisit the known Real/OpenRouter historical-image reread gap separately from Admin/platform work.

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

- [ ] Add regression coverage with every lifecycle, response, speech, multimodal, tool, memory, identity, trigger, background-work, definition, publishing, Admin, and infrastructure change.

- [ ] Maintain Playwright coverage for meaningful user-visible workflows.

- [ ] Make asynchronous/race-sensitive tests deterministic.

Prefer explicit gates/events and `TimeProvider` over wall-clock sleeps.

- [ ] Keep hosted-provider tests explicitly opt-in unless a provider-specific requirement is under acceptance.

- [ ] Keep hosted infrastructure optional for local development.

- [ ] Keep docs synchronized with observed implementation.

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
- [x] Durable accepted conversation-turn execution / observer reattachment behavior.
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
- [x] Durable trigger registration/scheduler — P5 frozen on `4bbc0c1`.
- [x] Durable triggered/background execution — P6 frozen on `30adaeb`.
- [x] P7 Harness Admin: draft/version/publish, resources, instances/persona, memory/automation administration, validation/evaluation, history/rollback.
- [x] P7.5 architecture/infrastructure-readiness consolidation frozen on `70a5720`.

---

# Next implementation item

**P7.6 — Admin usability closure** is next.

Bound it to:

```text
1. complete Definition Form + Advanced JSON editing
2. explicit New Definition / New Instance flows
3. clearer Definition/version semantics
4. explicit Knowledge Source ↔ Resource binding
5. multi-file + folder resource ingestion with coherent batch revision semantics
6. final Impeccable UX review, tests, docs/design sync, and freeze evidence
```

P7 and P7.5 remain frozen. Do not rewrite their closure evidence to make P7.6 look historical.

P7.6 is frozen on `17d89ae`. Begin P8 from one concrete provider/extensibility need.

Do **not** begin P7.6 or P8 by replacing SQLite, Docker, local storage, the single-process scheduler, or the modular monolith.
